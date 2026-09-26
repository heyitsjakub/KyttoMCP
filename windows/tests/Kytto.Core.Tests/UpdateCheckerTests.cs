using System.Net;
using System.Text;
using System.Text.Json;
using Kytto.Core.Settings;
using Kytto.Core.Updates;

namespace Kytto.Core.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private const string CurrentVersion = "1.0.4";
    private const string Sha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ANewerVersionIsOfferedToTheInstalledApp()
    {
        using var harness = NewHarness(Manifest(version: "1.0.5"));

        var result = await harness.Checker.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        var release = Assert.IsType<VerifiedUpdate>(result.Release);
        Assert.Equal("1.0.5", release.Version);
        Assert.Equal(Sha256, release.Sha256);
        Assert.Equal(1, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        using var harness = NewHarness(Manifest(version: "1.0.4.0"));

        var result = await harness.Checker.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal("1.0.4.0", Assert.IsType<VerifiedUpdate>(result.Release).Version);
    }

    [Theory]
    [InlineData("https://evil.example/download.php?file=windows")]
    [InlineData("https://kytto.jakubhecht.sk/download.php?file=macos")]
    [InlineData("https://kytto.jakubhecht.sk/download.php?file=windows&extra=1")]
    [InlineData("https://kytto.jakubhecht.sk:443/download.php?file=windows")]
    [InlineData("http://kytto.jakubhecht.sk/download.php?file=windows")]
    public async Task AForeignOrMalformedDownloadUrlIsRejected(string downloadUrl)
    {
        using var harness = NewHarness(Manifest(downloadUrl: downloadUrl));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Fact]
    public async Task AForeignFinalRedirectIsRejected()
    {
        using var harness = NewHarness(
            Manifest(),
            finalUri: new Uri("https://evil.example/update.php"));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Fact]
    public async Task AManifestWithoutWindowsIsRejected()
    {
        using var harness = NewHarness(Manifest(includeWindows: false));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Theory]
    [InlineData("https://evil.example/changelog.php")]
    [InlineData("https://kytto.jakubhecht.sk:443/changelog.php")]
    [InlineData("https://kytto.jakubhecht.sk/changelog.php?next=1")]
    public async Task AForeignOrMalformedReleaseNotesUrlIsRejected(string releaseNotesUrl)
    {
        using var harness = NewHarness(Manifest(releaseNotesUrl: releaseNotesUrl));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Fact]
    public async Task AMalformedSha256IsRejected()
    {
        using var harness = NewHarness(Manifest(sha256: "not-a-sha256"));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Theory]
    [InlineData("alpha", 1, "1.0.5")]
    [InlineData("beta", 2, "1.0.5")]
    [InlineData("beta", 1, "1.0.5.0.1")]
    [InlineData("beta", 1, "1..5")]
    public async Task SchemaChannelAndVersionMustMatchTheContract(
        string channel,
        int schema,
        string version)
    {
        using var harness = NewHarness(Manifest(version: version, schema: schema, channel: channel));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Fact]
    public async Task AnAutomaticAttemptIsPersistedBeforeTheRequest()
    {
        var observedBeforeRequest = false;
        Harness? harness = null;
        harness = NewHarness(
            Manifest(),
            beforeRequest: () => observedBeforeRequest =
                harness!.Settings.Current.LastAutomaticUpdateAttempt == Now);

        using (harness)
        {
            await harness.Checker.CheckAsync(force: false);
        }

        Assert.True(observedBeforeRequest);
    }

    [Fact]
    public async Task AnAutomaticAttemptLessThan24HoursOldDoesNotRequestAgain()
    {
        var attemptedAt = Now - TimeSpan.FromHours(23);
        using var harness = NewHarness(Manifest(), lastAttempt: attemptedAt);

        var result = await harness.Checker.CheckAsync(force: false);

        Assert.Equal(UpdateStatus.Skipped, result.Status);
        Assert.Null(result.Release);
        Assert.Equal(0, harness.Handler.RequestCount);
        Assert.Equal(attemptedAt, harness.Settings.Current.LastAutomaticUpdateAttempt);
    }

    [Fact]
    public async Task AForcedCheckIgnoresTheAutomaticThrottle()
    {
        using var harness = NewHarness(
            Manifest(),
            lastAttempt: Now - TimeSpan.FromHours(1));

        var result = await harness.Checker.CheckAsync(force: true);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(1, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task AVerifiedAvailableUpdateIsReturnedDuringTheSkippedInterval()
    {
        var cached = new VerifiedUpdate(
            Version: "1.0.5",
            ReleaseNotesURL: "https://kytto.jakubhecht.sk/changelog.php",
            Sha256: Sha256,
            CheckedAt: Now - TimeSpan.FromHours(2));
        using var harness = NewHarness(
            Manifest(version: "1.0.6"),
            lastAttempt: Now - TimeSpan.FromHours(1),
            cached: cached);

        var result = await harness.Checker.CheckAsync(force: false);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(cached, result.Release);
        Assert.Equal(0, harness.Handler.RequestCount);
    }

    [Fact]
    public async Task AnOversizedManifestIsRejected()
    {
        using var harness = NewHarness(Manifest(padding: new string('x', 65 * 1024)));

        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            harness.Checker.CheckAsync(force: true));
    }

    [Fact]
    public async Task VersionComparisonPadsMissingTrailingComponents()
    {
        Assert.True(UpdateChecker.CompareVersions("1.0.5", "1.0.4") > 0);
        Assert.Equal(0, UpdateChecker.CompareVersions("1.0.4", "1.0.4.0"));
        Assert.True(UpdateChecker.CompareVersions("1.0.4-beta", "1.0.4.0") == 0);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private Harness NewHarness(
        string manifest,
        DateTimeOffset? lastAttempt = null,
        VerifiedUpdate? cached = null,
        Uri? finalUri = null,
        Action? beforeRequest = null) =>
        new(
            Path.Combine(_root, Guid.NewGuid().ToString("N")),
            manifest,
            lastAttempt,
            cached,
            finalUri,
            beforeRequest);

    private static string Manifest(
        string version = "1.0.5",
        string downloadUrl = "https://kytto.jakubhecht.sk/download.php?file=windows&utm_source=kytto-app&utm_medium=updater",
        string releaseNotesUrl = "https://kytto.jakubhecht.sk/changelog.php",
        string sha256 = Sha256,
        bool includeWindows = true,
        string? padding = null,
        int schema = 1,
        string channel = "beta")
    {
        var platforms = new Dictionary<string, object>();
        if (includeWindows)
        {
            platforms["windows"] = new
            {
                displayName = "Windows",
                downloadURL = downloadUrl,
                sha256,
            };
        }

        return JsonSerializer.Serialize(new
        {
            schema,
            version,
            channel,
            releaseNotesURL = releaseNotesUrl,
            platforms,
            padding,
        });
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly HttpClient _client;

        internal Harness(
            string root,
            string manifest,
            DateTimeOffset? lastAttempt,
            VerifiedUpdate? cached,
            Uri? finalUri,
            Action? beforeRequest)
        {
            _root = root;
            var paths = new KyttoPaths(root);
            Settings = new SettingsStore(paths);
            if (lastAttempt is not null || cached is not null)
            {
                Settings.Update(settings => settings with
                {
                    LastAutomaticUpdateAttempt = lastAttempt,
                    LastVerifiedUpdate = cached,
                });
            }

            Handler = new RecordingHandler(
                manifest,
                finalUri ?? new Uri(UpdateChecker.ManifestUrl),
                beforeRequest);
            _client = new HttpClient(Handler);
            Checker = new UpdateChecker(Settings, CurrentVersion, _client, () => Now);
        }

        internal SettingsStore Settings { get; }
        internal RecordingHandler Handler { get; }
        internal UpdateChecker Checker { get; }

        public void Dispose()
        {
            Checker.Dispose();
            _client.Dispose();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // The test's assertion has already run; cleanup is best effort.
            }
        }
    }

    private sealed class RecordingHandler(
        string manifest,
        Uri finalUri,
        Action? beforeRequest) : HttpMessageHandler
    {
        private readonly byte[] _body = Encoding.UTF8.GetBytes(manifest);

        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            beforeRequest?.Invoke();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_body),
                RequestMessage = new HttpRequestMessage(request.Method, finalUri),
            };
            return Task.FromResult(response);
        }
    }
}
