using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-app-update-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OpenDownloadUsesOnlyTheFixedWindowsRoute()
    {
        ProcessStartInfo? started = null;
        using var model = new AppModel(
            new KyttoPaths(Path.Combine(_root, "KyttoData")),
            _root,
            new NoInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false,
            updateDownloadLauncher: new WindowsUpdateDownloadLauncher(info => started = info));
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests(
            "updates.openDownload",
            "{ \"url\": \"https://evil.example/installer.exe\" }");

        Assert.Equal("{\"ok\":true,\"data\":{}}", reply);
        Assert.NotNull(started);
        Assert.Equal(
            "https://kytto.jakubhecht.sk/download.php?file=windows&utm_source=kytto-app&utm_medium=updater",
            started!.FileName);
        Assert.True(started!.UseShellExecute);
    }

    [Fact]
    public async Task AppInfoAdvertisesUpdatesCapability()
    {
        using var model = new AppModel(
            new KyttoPaths(Path.Combine(_root, "KyttoData")),
            _root,
            new NoInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests("app.info");
        using var json = JsonDocument.Parse(reply);
        var capabilities = json.RootElement
            .GetProperty("data")
            .GetProperty("capabilities")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Contains("updates", capabilities);
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

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }
}
