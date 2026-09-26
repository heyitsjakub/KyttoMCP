using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kytto.Core.Model;

public sealed class ProvenanceCheckException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Checks one package name against its first-party package registry.</summary>
/// <remarks>
/// This service is never called by discovery, launch or health checks. Its one
/// request contains only the inferred package name (§7). The app adds a session
/// cache around it so repeated explicit clicks do not create a registry poll.
/// </remarks>
public sealed class ProvenanceChecker : IDisposable
{
    public const string NpmRegistry = "https://registry.npmjs.org/";
    public const string PythonRegistry = "https://pypi.org/pypi/";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _disposed;

    public ProvenanceChecker(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = Timeout,
            };
            _ownsHttp = true;
        }
    }

    public async Task<ServerProvenance> CheckLatestAsync(
        ServerProvenance provenance,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provenance.PackageName))
        {
            throw new ProvenanceCheckException(
                "Kytto could not identify a package name for this server.");
        }
        ThrowIfDisposed();

        var uri = RegistryUri(provenance);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is not { } finalUri ||
                !string.Equals(finalUri.OriginalString, uri.OriginalString, StringComparison.Ordinal))
            {
                throw new ProvenanceCheckException("Kytto could not check this package right now.");
            }

            var body = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (body.Length > 64 * 1024) throw new ProvenanceCheckException("The package response was too large.");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProvenanceCheckException("The package response could not be verified.");
            }
            var root = document.RootElement;
            var latest = provenance.SourceKind == ProvenanceSourceKinds.Npm
                ? root.TryGetProperty("dist-tags", out var tags) &&
                  tags.ValueKind == JsonValueKind.Object &&
                  tags.TryGetProperty("latest", out var npmLatest)
                    ? npmLatest
                    : default
                : root.TryGetProperty("info", out var info) &&
                  info.ValueKind == JsonValueKind.Object &&
                  info.TryGetProperty("version", out var pythonLatest)
                    ? pythonLatest
                    : default;
            if (latest.ValueKind != JsonValueKind.String ||
                latest.GetString() is not { } version ||
                !IsVersion(version))
            {
                throw new ProvenanceCheckException("The package response could not be verified.");
            }
            return provenance.WithLatest(version, checkedAt);
        }
        catch (ProvenanceCheckException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProvenanceCheckException("The package check timed out.");
        }
        catch (HttpRequestException error)
        {
            throw new ProvenanceCheckException("Kytto could not check this package.", error);
        }
        catch (JsonException error)
        {
            throw new ProvenanceCheckException("The package response could not be verified.", error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
    }

    private static bool IsVersion(string value)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        return parts.Length is >= 1 and <= 4 && parts.All(part =>
            part.Length > 0 && part.All(character => character is >= '0' and <= '9'));
    }

    private static Uri RegistryUri(ServerProvenance provenance)
    {
        var package = Uri.EscapeDataString(provenance.PackageName!);
        return provenance.SourceKind switch
        {
            ProvenanceSourceKinds.Npm => new Uri(NpmRegistry + package, UriKind.Absolute),
            ProvenanceSourceKinds.Python => new Uri(
                PythonRegistry + package + "/json", UriKind.Absolute),
            _ => throw new ProvenanceCheckException(
                "Kytto can only check npm and Python package provenance."),
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProvenanceChecker));
    }
}
