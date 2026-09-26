using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kytto.Core.Settings;
using Kytto.Core.Updates;

namespace Kytto.Core.Tests;

public sealed class UpdatePackageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-update-package-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadStreamsToAClosedVerifiedStageAndReturnsAnOpaqueToken()
    {
        var bytes = Encoding.UTF8.GetBytes("signed installer fixture");
        var handler = new PackageHandler(bytes);
        using var client = new HttpClient(handler);
        using var manager = new UpdatePackageManager(new KyttoPaths(_root), client);
        var phases = new List<string>();
        var release = Release(bytes);

        var package = await manager.DownloadAsync(
            release,
            new DirectProgress(phases));

        Assert.StartsWith("stage.", package.Token, StringComparison.Ordinal);
        Assert.EndsWith(".exe", package.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(package.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(package.Path));
        Assert.Equal(package, manager.ReadyPackage);
        Assert.Contains("downloading", phases);
        Assert.Contains("verifying", phases);
        Assert.Contains("staged", phases);
        Assert.Equal(UpdateChecker.WindowsDownloadUrl, handler.RequestedUri?.OriginalString);
    }

    [Fact]
    public async Task AForeignFinalRedirectIsRejectedAndLeavesNoPartialStage()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        var handler = new PackageHandler(bytes, new Uri("https://evil.example/download.exe"));
        using var client = new HttpClient(handler);
        using var manager = new UpdatePackageManager(new KyttoPaths(_root), client);

        await Assert.ThrowsAsync<UpdatePackageException>(() => manager.DownloadAsync(Release(bytes)));

        Assert.Empty(Directory.Exists(Path.Combine(_root, "Updates"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(_root, "Updates"))
            : []);
        Assert.Null(manager.ReadyPackage);
    }

    [Fact]
    public async Task InstallRehashesTheStageAndInvalidatesItWhenItChanged()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        using var client = new HttpClient(new PackageHandler(bytes));
        using var manager = new UpdatePackageManager(new KyttoPaths(_root), client);
        var package = await manager.DownloadAsync(Release(bytes));
        File.WriteAllBytes(package.Path, Enumerable.Repeat((byte)'x', bytes.Length).ToArray());
        var installer = new RecordingInstaller();

        await Assert.ThrowsAsync<UpdatePackageException>(() => manager.InstallAsync(
            package.Token, "1.0.4", installer));

        Assert.False(File.Exists(package.Path));
        Assert.Null(manager.ReadyPackage);
        Assert.False(installer.WasCalled);
    }

    [Fact]
    public async Task AVerifiedStageCanOnlyBeInstalledAfterAnExplicitNewerVersionCheck()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        using var client = new HttpClient(new PackageHandler(bytes));
        using var manager = new UpdatePackageManager(new KyttoPaths(_root), client);
        var package = await manager.DownloadAsync(Release(bytes));
        var installer = new RecordingInstaller();
        var phases = new List<string>();

        await manager.InstallAsync(
            package.Token,
            "1.0.4",
            installer,
            new DirectProgress(phases));

        Assert.True(installer.WasCalled);
        Assert.Equal(package, installer.Package);
        Assert.Contains("installing", phases);
        Assert.Contains("relaunching", phases);
        Assert.DoesNotContain("installed", phases);

        await Assert.ThrowsAsync<UpdatePackageException>(() => manager.InstallAsync(
            package.Token, "1.0.5", new RecordingInstaller()));
    }

    [Fact]
    public async Task InstallRemainsPendingUntilTheInstallerHandoffCompletes()
    {
        var bytes = Encoding.UTF8.GetBytes("installer");
        using var client = new HttpClient(new PackageHandler(bytes));
        using var manager = new UpdatePackageManager(new KyttoPaths(_root), client);
        var package = await manager.DownloadAsync(Release(bytes));
        var installer = new BlockingInstaller();

        var install = manager.InstallAsync(package.Token, "1.0.4", installer);
        await installer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(install.IsCompleted);
        installer.Complete.TrySetResult();
        await install;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup is best effort; no user data is in this temp root.
        }
    }

    private static VerifiedUpdate Release(byte[] bytes) => new(
        "1.0.5",
        "https://kytto.jakubhecht.sk/changelog.php",
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

    private sealed class PackageHandler(byte[] bytes, Uri? finalUri = null) : HttpMessageHandler
    {
        internal Uri? RequestedUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = new HttpRequestMessage(
                    request.Method,
                    finalUri ?? request.RequestUri),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        internal bool WasCalled { get; private set; }
        internal UpdatePackage? Package { get; private set; }

        public Task InstallAsync(UpdatePackage package, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasCalled = true;
            Package = package;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingInstaller : IUpdateInstaller
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Complete { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InstallAsync(UpdatePackage package, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class DirectProgress(List<string> phases) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => phases.Add(value.Phase);
    }
}
