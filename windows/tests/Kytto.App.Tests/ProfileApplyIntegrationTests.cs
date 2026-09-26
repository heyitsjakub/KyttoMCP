using System.IO;
using System.Text.Json;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

public sealed class ProfileApplyIntegrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-profile-apply-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OneProfileCanDisableSeveralServersInTheSameFileSafely()
    {
        var config = Path.Combine(_home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """
            {
              "unrelated": { "keep": true },
              "mcpServers": {
                "alpha": { "command": "alpha" },
                "beta": { "command": "beta" },
                "gamma": { "command": "gamma" }
              }
            }
            """);

        var paths = new KyttoPaths(Path.Combine(_home, "KyttoData"));
        using var model = new AppModel(
            paths,
            _home,
            new AllInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
        var profile = Assert.Single(model.CreateProfile("Minimal", ["alpha"]));

        var result = model.ApplyProfile(profile.Id, ClientId.Cursor);

        Assert.Equal(0, result.EnabledCount);
        Assert.Equal(2, result.DisabledCount);
        Assert.Empty(result.Failures);
        Assert.Equal(2, model.PendingRestarts[ClientId.Cursor]);
        Assert.Equal(2, model.Backups.List(ClientId.Cursor).Count);

        using var written = JsonDocument.Parse(File.ReadAllText(config));
        Assert.True(written.RootElement.GetProperty("unrelated").GetProperty("keep").GetBoolean());
        var servers = written.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("alpha", out _));
        Assert.False(servers.TryGetProperty("beta", out _));
        Assert.False(servers.TryGetProperty("gamma", out _));
        Assert.Equal(
            Kytto.Core.Model.Enablement.Enabled,
            model.ServerById("alpha").EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void EmptyProfileSwitchesEveryActiveServerOff()
    {
        var config = Path.Combine(_home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """{ "mcpServers": { "alpha": { "command": "alpha" } } }""");

        using var model = new AppModel(
            new KyttoPaths(Path.Combine(_home, "KyttoData")),
            _home,
            new AllInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
        var profile = Assert.Single(model.CreateProfile("Nothing", []));

        var result = model.ApplyProfile(profile.Id, ClientId.Cursor);

        Assert.Equal(1, result.DisabledCount);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(model.Current().Servers, server =>
            server.EnabledIn.GetValueOrDefault(ClientId.Cursor) == Kytto.Core.Model.Enablement.Enabled);
    }

    [Fact]
    public void MissingServerIsReportedAndFreshDiscoveryPreservesUnrelatedData()
    {
        var config = Path.Combine(_home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """{ "mcpServers": { "alpha": { "command": "alpha" } } }""");

        using var model = new AppModel(
            new KyttoPaths(Path.Combine(_home, "KyttoData")),
            _home,
            new AllInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
        var profile = Assert.Single(model.CreateProfile("Missing", ["gone"]));
        File.WriteAllText(config, """{ "external": true, "mcpServers": { "alpha": { "command": "alpha" } } }""");

        var result = model.ApplyProfile(profile.Id, ClientId.Cursor);

        Assert.Equal(1, result.DisabledCount);
        Assert.Contains(result.Failures, failure => failure.ServerName == "gone");
        Assert.Equal(1, model.PendingRestarts[ClientId.Cursor]);
        Assert.Single(model.Backups.List(ClientId.Cursor));
        Assert.Contains("\"external\": true", File.ReadAllText(config), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class AllInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => true;
        public bool PackageExists(string familyName) => true;
        public bool ExecutableExists(string name, string home) => true;
    }
}
