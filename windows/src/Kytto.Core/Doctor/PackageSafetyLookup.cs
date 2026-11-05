using System.Net;
using System.Net.Http.Headers;
using Kytto.Core.Model;

namespace Kytto.Core.Doctor;

public sealed class PackageSafetyException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    public static PackageSafetyException NotAPackage() =>
        new("This server does not fetch a package by name, so there is no package to check.");

    public static PackageSafetyException InvalidPackage() =>
        new("The package name is not safe to send in a lookup.");

    public static PackageSafetyException RequestFailed(string service) =>
        new($"Kytto could not reach {service}. No safety conclusion was made.");

    public static PackageSafetyException InvalidResponse(string service) =>
        new($"{service} returned an answer Kytto could not read. No safety conclusion was made.");
}

/// <summary>An explicit, read-only package safety check (§7.10).</summary>
/// <remarks>
/// Called only when the user asks. It sends osv.dev the package name, its registry, and
/// the version when the launch pins one; for an npm package it also reads that version's
/// document from the npm registry to see whether it runs install scripts or is deprecated.
/// Nothing else from the configuration leaves the machine.
/// </remarks>
public sealed class PackageSafetyLookup : IDisposable
{
    public const string OsvEndpoint = "https://api.osv.dev/v1/query";

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTimeOffset> _now;

    public PackageSafetyLookup(HttpClient? httpClient = null, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        if (httpClient is not null)
        {
            _http = httpClient;
            return;
        }
        // No redirects: the answer has to come from the address that was asked, so there
        // is nothing to follow.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout };
        _ownsHttp = true;
    }

    public async Task<PackageSafetyReport> CheckAsync(
        PackageLaunch launch,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafePackage(launch.Name, allowScopedName: launch.Ecosystem == PackageEcosystem.Npm))
        {
            throw PackageSafetyException.InvalidPackage();
        }
        var version = PackageSafety.QueriedVersion(launch);

        var osvUri = new Uri(OsvEndpoint, UriKind.Absolute);
        using var query = new HttpRequestMessage(HttpMethod.Post, osvUri)
        {
            Content = new ByteArrayContent(PackageSafety.OsvQuery(launch)),
        };
        query.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var osvData = await FetchAsync(query, osvUri, "osv.dev", cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PackageSafetyReport.Advisory> advisories;
        try
        {
            advisories = PackageSafety.AdvisoriesFromOsv(osvData, versionQueried: version is not null);
        }
        catch (InvalidDataException)
        {
            throw PackageSafetyException.InvalidResponse("osv.dev");
        }

        IReadOnlyDictionary<string, string> scripts = new Dictionary<string, string>();
        string? deprecation = null;
        if (launch.Ecosystem == PackageEcosystem.Npm)
        {
            var npmUri = new Uri(
                ProvenanceChecker.NpmRegistry + Uri.EscapeDataString(launch.Name) + "/"
                + Uri.EscapeDataString(version ?? "latest"),
                UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, npmUri);
            var data = await FetchAsync(request, npmUri, "the npm registry", cancellationToken).ConfigureAwait(false);
            try
            {
                scripts = PackageSafety.InstallScriptsFromNpmVersion(data);
                deprecation = PackageSafety.DeprecationFromNpmVersion(data);
            }
            catch (InvalidDataException)
            {
                throw PackageSafetyException.InvalidResponse("The npm registry");
            }
        }

        return new PackageSafetyReport(
            launch.Ecosystem, launch.Name, version, advisories, scripts, _now(), deprecation);
    }

    private async Task<byte[]> FetchAsync(
        HttpRequestMessage request,
        Uri endpoint,
        string service,
        CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK
                || response.RequestMessage?.RequestUri is not { } final
                || !IsFirstPartyResponse(final, endpoint)
                || response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw PackageSafetyException.RequestFailed(service);
            }
            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (body.Length > MaxResponseBytes) throw PackageSafetyException.RequestFailed(service);
            return body;
        }
        catch (PackageSafetyException)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            throw PackageSafetyException.RequestFailed(service);
        }
    }

    /// <summary>The answer must come from the address that was asked, over HTTPS.</summary>
    internal static bool IsFirstPartyResponse(Uri final, Uri endpoint) =>
        final.Scheme == Uri.UriSchemeHttps
        && string.Equals(final.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase)
        && final.IsDefaultPort
        && string.IsNullOrEmpty(final.UserInfo)
        && final.Query.Length == 0
        && final.Fragment.Length == 0
        && string.Equals(final.AbsolutePath, endpoint.AbsolutePath, StringComparison.Ordinal);

    /// <summary>A name that can be put in a URL path without changing what it addresses.</summary>
    internal static bool IsSafePackage(string name, bool allowScopedName)
    {
        if (name.Length is 0 or > 200
            || name.StartsWith('/') || name.EndsWith('/')
            || name.Contains("..", StringComparison.Ordinal)
            || name.Contains('\\'))
        {
            return false;
        }
        var allowed = allowScopedName ? "@._-/" : "._-";
        if (!name.All(character => char.IsAsciiLetterOrDigit(character) || allowed.Contains(character)))
        {
            return false;
        }
        return !allowScopedName || name.Count(character => character == '/') <= 1;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
