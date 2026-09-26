using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Json;
using Kytto.Core.Settings;

namespace Kytto.Core.Updates;

public enum UpdateStatus
{
    UpdateAvailable,
    UpToDate,
    Skipped,
}

/// <summary>The validated result of one update-check operation.</summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    VerifiedUpdate? Release);

/// <summary>A network or manifest-validation failure safe to expose through IPC.</summary>
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Fetches and validates Kytto's first-party Windows update manifest.
/// </summary>
/// <remarks>
/// The checker knows only the public release contract. It never downloads an
/// installer, starts a process or sends anything derived from the user's machine.
/// </remarks>
public sealed class UpdateChecker : IDisposable
{
    public const string ManifestUrl = "https://kytto.jakubhecht.sk/update.php";
    public const string WindowsDownloadUrl =
        "https://kytto.jakubhecht.sk/download.php?file=windows&utm_source=kytto-app&utm_medium=updater";

    private const int MaxManifestBytes = 64 * 1024;
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Uri ManifestUri = new(ManifestUrl, UriKind.Absolute);
    private static readonly Uri WindowsManifestDownloadUri =
        new(WindowsDownloadUrl, UriKind.Absolute);
    private static readonly Uri ReleaseNotesUri =
        new("https://kytto.jakubhecht.sk/changelog.php", UriKind.Absolute);

    private readonly SettingsStore _settingsStore;
    private readonly string _currentVersion;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public UpdateChecker(
        SettingsStore settingsStore,
        string currentVersion,
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settingsStore = settingsStore;
        _currentVersion = currentVersion;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient(new HttpClientHandler
            {
                // Follow normal HTTPS redirects, then reject any final URL that is
                // not the exact manifest route below. A redirect is not trust.
                AllowAutoRedirect = true,
            })
            {
                Timeout = RequestTimeout,
            };
            _ownsHttpClient = true;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckCoreAsync(force, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    internal static VerifiedUpdate ParseManifest(
        ReadOnlySpan<byte> utf8Json,
        DateTimeOffset checkedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaNumber) ||
                schemaNumber != 1 ||
                !HasExactString(root, "channel", "beta"))
            {
                throw InvalidManifest();
            }

            var version = RequiredString(root, "version");
            if (!NumericVersion.TryParseStrict(version, out _)) throw InvalidManifest();

            var releaseNotesURL = RequiredString(root, "releaseNotesURL");
            if (!IsExactFirstPartyUri(releaseNotesURL, ReleaseNotesUri))
            {
                throw InvalidManifest();
            }

            if (!root.TryGetProperty("platforms", out var platforms) ||
                platforms.ValueKind != JsonValueKind.Object ||
                !platforms.TryGetProperty("windows", out var windows) ||
                windows.ValueKind != JsonValueKind.Object)
            {
                throw InvalidManifest();
            }

            var downloadURL = RequiredString(windows, "downloadURL");
            if (!IsExactFirstPartyUri(downloadURL, WindowsManifestDownloadUri))
            {
                throw InvalidManifest();
            }

            var sha256 = RequiredString(windows, "sha256");
            if (!IsSha256(sha256)) throw InvalidManifest();

            return new VerifiedUpdate(
                Version: version,
                ReleaseNotesURL: releaseNotesURL,
                Sha256: sha256.ToLowerInvariant(),
                CheckedAt: checkedAt);
        }
        catch (UpdateCheckException)
        {
            throw;
        }
        catch (JsonException error)
        {
            throw InvalidManifest(error);
        }
    }

    internal static bool IsValidCachedRelease(VerifiedUpdate? release) =>
        release is not null &&
        NumericVersion.TryParseStrict(release.Version, out _) &&
        IsExactFirstPartyUri(release.ReleaseNotesURL, ReleaseNotesUri) &&
        IsSha256(release.Sha256) &&
        release.CheckedAt != default;

    internal static int CompareVersions(string left, string right)
    {
        if (!NumericVersion.TryParse(left, out var leftVersion) ||
            !NumericVersion.TryParse(right, out var rightVersion))
        {
            throw new ArgumentException("Both versions must contain numeric components.");
        }

        return leftVersion.CompareTo(rightVersion);
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        var now = _clock().ToUniversalTime();
        if (!NumericVersion.TryParse(_currentVersion, out var currentVersion))
        {
            throw new UpdateCheckException("The installed Kytto version is invalid.");
        }

        var settings = _settingsStore.Current;
        var cachedRelease = settings.LastVerifiedUpdate;
        var cached = IsValidCachedRelease(cachedRelease)
            ? cachedRelease! with
            {
                Sha256 = cachedRelease!.Sha256.ToLowerInvariant(),
            }
            : null;

        if (!force && IsAutomaticAttemptFresh(settings.LastAutomaticUpdateAttempt, now))
        {
            return Result(UpdateStatus.Skipped, currentVersion, cached);
        }

        // This write deliberately precedes the request. An offline launch must
        // consume the same 24-hour window as a successful launch.
        if (!force)
        {
            _settingsStore.Update(current => current with
            {
                LastAutomaticUpdateAttempt = now,
            });
        }

        var release = await FetchAsync(now, cancellationToken).ConfigureAwait(false);
        _settingsStore.Update(current => current with
        {
            LastVerifiedUpdate = release,
        });

        return Result(StatusFor(release, currentVersion), currentVersion, release);
    }

    private async Task<VerifiedUpdate> FetchAsync(
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri is not { } finalUri ||
                !string.Equals(finalUri.OriginalString, ManifestUri.OriginalString, StringComparison.Ordinal))
            {
                throw InvalidManifest();
            }

            if (response.Content.Headers.ContentLength is > MaxManifestBytes)
            {
                throw InvalidManifest();
            }

            var body = await ReadBodyAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return ParseManifest(body, checkedAt);
        }
        catch (UpdateCheckException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException("The Kytto update check timed out.");
        }
        catch (HttpRequestException error)
        {
            throw new UpdateCheckException("Kytto could not check for updates.", error);
        }
        catch (IOException error)
        {
            throw new UpdateCheckException("Kytto could not read the update manifest.", error);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(capacity: Math.Min(MaxManifestBytes, 8 * 1024));
        var buffer = new byte[8 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length > MaxManifestBytes - read) throw InvalidManifest();
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static UpdateCheckResult Result(
        UpdateStatus skippedStatus,
        NumericVersion currentVersion,
        VerifiedUpdate? release) =>
        new(
            Status: release is null ? skippedStatus : StatusFor(release, currentVersion),
            CurrentVersion: currentVersion.Display,
            Release: release);

    private static UpdateStatus StatusFor(VerifiedUpdate release, NumericVersion currentVersion)
    {
        var latest = NumericVersion.Parse(release.Version);
        return latest.CompareTo(currentVersion) > 0
            ? UpdateStatus.UpdateAvailable
            : UpdateStatus.UpToDate;
    }

    private static bool IsAutomaticAttemptFresh(DateTimeOffset? attempt, DateTimeOffset now)
    {
        if (attempt is not { } value) return false;
        if (value >= now) return true;
        return now - value < AutomaticInterval;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw InvalidManifest();
        }

        return value.GetString() ?? throw InvalidManifest();
    }

    private static bool HasExactString(JsonElement parent, string name, string expected) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IsExactFirstPartyUri(string raw, Uri expected)
    {
        // Compare the original spelling as well as parsing it. This keeps an
        // explicit default port (`:443`), credentials, alternate query encoding
        // and every other look-alike URL out of the accepted contract.
        return Uri.TryCreate(raw, UriKind.Absolute, out _) &&
               string.Equals(raw, expected.OriginalString, StringComparison.Ordinal);
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return true;
    }

    private static UpdateCheckException InvalidManifest(Exception? inner = null) =>
        new("The Kytto update manifest could not be verified.", inner);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UpdateChecker));
    }

    private readonly struct NumericVersion(string display, BigInteger[] components) : IComparable<NumericVersion>
    {
        public string Display { get; } = display;
        private BigInteger[] Components { get; } = components;

        public static bool TryParse(string value, out NumericVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var numeric = value;
            var suffixStart = value.IndexOf('-', StringComparison.Ordinal);
            if (suffixStart >= 0)
            {
                if (suffixStart == value.Length - 1) return false;
                numeric = value[..suffixStart];
                for (var index = suffixStart + 1; index < value.Length; index++)
                {
                    if (char.IsWhiteSpace(value[index]) || char.IsControl(value[index])) return false;
                }
            }

            var parts = numeric.Split('.', StringSplitOptions.None);
            if (parts.Length is < 1 or > 4) return false;

            var components = new BigInteger[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (parts[index].Length == 0 ||
                    parts[index].Any(character => character is < '0' or > '9') ||
                    !BigInteger.TryParse(
                        parts[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out components[index]))
                {
                    return false;
                }
            }

            version = new NumericVersion(value, components);
            return true;
        }

        public static bool TryParseStrict(string value, out NumericVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = value.Split('.', StringSplitOptions.None);
            if (parts.Length is < 1 or > 4 || parts.Any(part =>
                part.Length == 0 || part.Any(character => character is < '0' or > '9'))) return false;

            var components = new BigInteger[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                if (!BigInteger.TryParse(parts[index], NumberStyles.None,
                        CultureInfo.InvariantCulture, out components[index])) return false;
            }
            version = new NumericVersion(value, components);
            return true;
        }

        public static NumericVersion Parse(string value) =>
            TryParse(value, out var version)
                ? version
                : throw new ArgumentException("The version is not numeric.", nameof(value));

        public int CompareTo(NumericVersion other)
        {
            for (var index = 0; index < 4; index++)
            {
                var left = index < Components.Length ? Components[index] : BigInteger.Zero;
                var right = index < other.Components.Length ? other.Components[index] : BigInteger.Zero;
                var comparison = left.CompareTo(right);
                if (comparison != 0) return comparison;
            }

            return 0;
        }
    }
}
