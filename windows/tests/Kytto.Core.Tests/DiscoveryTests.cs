using Kytto.Core.Clients;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

public class DiscoverySafetyTests
{
    private sealed class MissingApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    [Fact]
    public void InvalidUtf8ConfigIsReportedRatherThanPretendedAbsent()
    {
        using var harness = new ToggleHarness();
        var path = Path.Combine(harness.Home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x7B, 0x22, 0xFF, 0x22, 0x7D]);

        var result = harness.Discover();
        var cursor = result.Clients.Single(client => client.Id == ClientId.Cursor);

        Assert.True(cursor.ConfigExists);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.ClientID == ClientId.Cursor &&
            diagnostic.Message.Contains("not valid UTF-8", StringComparison.Ordinal));
        Assert.Empty(result.Servers);
    }

    [Fact]
    public void WatchTargetsIncludeTheConfiguredPathOverride()
    {
        using var harness = new ToggleHarness();
        var overridden = Path.Combine(harness.Home, "custom", "cursor-mcp.json");
        var discovery = new Discovery(
            harness.Home,
            new MissingApps(),
            pathOverrides: new Dictionary<string, string>
            {
                [ClientId.Cursor.Raw()] = overridden,
            });

        var targets = discovery.WatchTargets();

        Assert.Contains(targets, target =>
            !target.IncludeSubdirectories &&
            string.Equals(target.Path, overridden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AmbiguousDuplicateServerNamesAreReportedAndNotMadeEditable()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            { "mcpServers": {
              "GitHub": { "command": "first" },
              " github ": { "command": "second" },
              "safe": { "command": "run" }
            } }
            """, @".cursor\mcp.json");

        var result = harness.Discover();

        Assert.Equal(["safe"], result.Servers.Select(server => server.Name));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.ClientID == ClientId.Cursor &&
            diagnostic.Message.Contains("ambiguous duplicate", StringComparison.Ordinal));
    }
}
