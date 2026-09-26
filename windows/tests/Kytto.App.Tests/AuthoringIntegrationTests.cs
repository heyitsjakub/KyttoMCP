using System.IO;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

public sealed class AuthoringIntegrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-app-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SubmitWithoutAClientReturnsFormErrorAndWritesNoConfig()
    {
        Directory.CreateDirectory(_home);
        var oldData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable(
                "KYTTO_TEST_DATA_ROOT", Path.Combine(_home, "KyttoData"));
            using var model = new AppModel(
                new KyttoPaths(Path.Combine(_home, "KyttoData")),
                _home,
                new NoInstalledApps(),
                new InMemorySecretStore(),
                watchConfigs: false);
            var router = new CommandRouter();
            CommandRegistry.RegisterAll(router, model);

            var reply = await router.InvokeForTests("servers.create", """
                {
                  "draft": {
                    "name": "memory", "transport": "stdio", "command": "npx",
                    "args": [], "env": [], "url": ""
                  },
                  "clientIDs": []
                }
                """);

            using var envelope = JsonDocument.Parse(reply);
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "Choose at least one client to add this server to.",
                envelope.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.DoesNotContain(
                Directory.GetFiles(_home, "*", SearchOption.AllDirectories),
                path => path.EndsWith("mcp.json", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase)
                    || path.Contains("Backups", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", oldData);
        }
    }

    [Fact]
    public void PersistedSettingsRaiseTheDedicatedChangeImmediately()
    {
        Directory.CreateDirectory(_home);
        var paths = new KyttoPaths(Path.Combine(_home, "KyttoData"));
        using var model = new AppModel(
            paths,
            _home,
            new NoInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
        KyttoSettings? received = null;
        model.SettingsChanged += settings => received = settings;

        model.UpdateSettings(settings => settings with
        {
            TrayEnabled = false,
            TrayClient = "cursor",
        });

        Assert.NotNull(received);
        Assert.False(received.TrayEnabled);
        Assert.Equal("cursor", received.TrayClient);
        Assert.True(File.Exists(paths.SettingsFile));
    }

#if DEBUG
    [Fact]
    public void DebugLaunchEnvironmentRedirectsTheWholeAppProfile()
    {
        var dataRoot = Path.Combine(_home, "LaunchData");
        var oldHome = Environment.GetEnvironmentVariable("KYTTO_TEST_HOME");
        var oldData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", _home);
            Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", dataRoot);
            using var model = AppModel.ForCurrentProcess();

            model.UpdateSettings(settings => settings with { HasCompletedOnboarding = true });

            Assert.True(File.Exists(Path.Combine(dataRoot, "settings.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", oldHome);
            Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", oldData);
        }
    }
#endif

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary profile is not worth hiding the assertion result.
        }
    }

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }
}
