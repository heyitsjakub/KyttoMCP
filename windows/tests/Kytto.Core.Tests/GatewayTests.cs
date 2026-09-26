using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;
using Kytto.Core.Json;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Tests;

public sealed class GatewayTests
{
    [Fact]
    public void InvocationKeepsMetadataSeparateFromTheRealCommand()
    {
        var eventLog = Path.Combine(Path.GetTempPath(), "kytto-events.jsonl");
        var parsed = GatewayInvocation.Parse([
            "--server-id", "github",
            "--client-id", "cursor",
            "--event-log", eventLog,
            "--", "npx", "-y", "@example/server",
        ]);

        Assert.Equal("github", parsed.ServerID);
        Assert.Equal("cursor", parsed.ClientID);
        Assert.Equal(Path.GetFullPath(eventLog), parsed.EventLogPath);
        Assert.Equal("npx", parsed.Command);
        Assert.Equal(["-y", "@example/server"], parsed.Arguments);
    }

    [Fact]
    public void OpaqueRouteDoesNotExposeAnUpstreamCommand()
    {
        var id = "A6ED52D3-BE69-4A4F-A215-AC17C685F520";
        var parsed = GatewayInvocation.Parse(["--route", id, "--routes", @"C:\temp\routes.json"]);

        Assert.Equal(id.ToLowerInvariant(), parsed.RouteID);
        Assert.Null(parsed.Command);
        Assert.Empty(parsed.Arguments);
    }

    [Fact]
    public void ToolCallsAreCorrelatedWithoutRetainingPayloads()
    {
        var sink = new MemoryGatewaySink();
        var observer = new GatewayMessageObserver("github", "cursor", sink, "session-1");

        observer.Start();
        observer.Accept(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"create_issue","arguments":{"token":"secret-value"}}}""" + "\n"),
            GatewayDirection.ClientToServer);
        observer.Accept(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"private-result"}]}}""" + "\n"),
            GatewayDirection.ServerToClient);
        observer.Finish(0);

        Assert.Equal(
            [GatewayEventKind.SessionStarted, GatewayEventKind.ToolCallStarted,
             GatewayEventKind.ToolCallCompleted, GatewayEventKind.SessionEnded],
            sink.Events.Select(item => item.Kind));
        Assert.Equal("create_issue", sink.Events[1].ToolName);
        Assert.Equal("7", sink.Events[1].RequestID);
        Assert.True(sink.Events[2].Succeeded);
        var encoded = JsonSerializer.Serialize(sink.Events);
        Assert.DoesNotContain("secret-value", encoded, StringComparison.Ordinal);
        Assert.DoesNotContain("private-result", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonLinesSinkAppendsCompletePrivacySafeRecords()
    {
        using var harness = new GatewayHarness();
        var path = Path.Combine(harness.Home, "Gateway", "events.jsonl");
        using (var sink = new JsonLinesGatewayEventSink(path))
        {
            sink.Record(Event(GatewayEventKind.SessionStarted));
            sink.Record(Event(GatewayEventKind.SessionEnded) with { ExitCode = 0 });
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"session.started\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"session.ended\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void JsonMigrationRemovesSecretsAndRestoresBytes()
    {
        using var harness = new GatewayHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        var original = harness.Text(@".cursor\mcp.json");
        var github = Assert.Single(harness.Discover().Servers, server => server.Id == "github");
        const string RouteID = "a6ed52d3-be69-4a4f-a215-ac17c685f520";

        var preview = harness.Service().Preview(github, ClientId.Cursor, RouteID);
        Assert.DoesNotContain("ghp_exampletokenvalue", preview.DirectDefinitionPreview, StringComparison.Ordinal);
        Assert.Contains(RouteID, preview.GatewayDefinitionPreview, StringComparison.Ordinal);
        Assert.Equal(["GITHUB_PERSONAL_ACCESS_TOKEN"], preview.EnvironmentKeys);

        var enabled = harness.Service().Enable(github, ClientId.Cursor, RouteID);
        Assert.NotNull(enabled.BackupID);
        var migrated = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        var definition = migrated.ValueAt("mcpServers", "github");
        Assert.NotNull(definition);
        Assert.Equal(harness.HelperPath, definition["command"]?.StringValue);
        Assert.Equal(["--route", RouteID], definition["args"]?.Elements?.Select(item => item.StringValue));
        Assert.Null(definition["env"]);
        Assert.DoesNotContain("ghp_exampletokenvalue", File.ReadAllText(harness.Paths.GatewayRoutesFile));

        var launch = harness.Routes.Resolve(RouteID, harness.Secrets);
        Assert.Equal("npx", launch.Route.Command);
        Assert.Equal("ghp_exampletokenvalue", launch.Environment["GITHUB_PERSONAL_ACCESS_TOKEN"]);

        harness.Discover();
        harness.Service().Restore(RouteID);
        Assert.Equal(original, harness.Text(@".cursor\mcp.json"));
        Assert.Empty(harness.Routes.All());
        Assert.Null(harness.Secrets.Value(GatewayRoute.DirectDefinitionIdentifier(RouteID)));
    }

    [Fact]
    public void TomlMigrationPreservesUnrelatedTablesAndRestoresTheBlock()
    {
        using var harness = new GatewayHarness();
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var original = harness.Text(@".codex\config.toml");
        var filesystem = Assert.Single(harness.Discover().Servers, server => server.Id == "filesystem");
        const string RouteID = "645c1896-23de-47ea-bdbd-f97f1fc63896";

        harness.Service().Enable(filesystem, ClientId.Codex, RouteID);
        var migrated = TomlDocument.Parse(harness.Text(@".codex\config.toml"));
        var table = migrated.Table("mcp_servers", "filesystem");
        Assert.NotNull(table);
        Assert.Equal(harness.HelperPath, table.Value("command")?.StringValue);
        Assert.Null(migrated.Table("mcp_servers", "filesystem", "env"));
        Assert.Equal("save-all", migrated.Table("history")?.Value("persistence")?.StringValue);

        harness.Discover();
        harness.Service().Restore(RouteID);
        Assert.Equal(original, harness.Text(@".codex\config.toml"));
    }

    [Fact]
    public void DriftIsRefusedBeforeRouteOrCredentialWrites()
    {
        using var harness = new GatewayHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        var github = Assert.Single(harness.Discover().Servers, server => server.Id == "github");
        harness.Write(Fixture.CursorMcp.Text() + "\n", @".cursor\mcp.json");

        Assert.Throws<ConfigWriteException>(() => harness.Service().Enable(
            github,
            ClientId.Cursor,
            "87d64ab8-e43b-4d18-afdc-0cd04052922c"));
        Assert.Empty(harness.Routes.All());
        Assert.Empty(harness.Secrets.StoredIdentifiers());
    }

    [Fact]
    public void BackupCannotReviveARouteAfterCredentialsWereRemoved()
    {
        using var harness = new GatewayHarness();
        const string RouteID = "56a3af61-f47c-4ec4-bd43-63b5ace657a8";
        var backup = "{\"mcpServers\":{\"github\":{\"command\":\"C:\\\\Kytto\\\\kytto-mcp-proxy.exe\",\"args\":[\"--route\",\"" +
            RouteID + "\"]}}}";

        var error = Assert.Throws<GatewayMigrationException>(() =>
            harness.Service().ValidateBackupRestore(backup, ClientId.Cursor));
        Assert.Contains(RouteID, error.Message, StringComparison.Ordinal);
    }

    private static GatewayEvent Event(GatewayEventKind kind) => new(
        Guid.NewGuid().ToString("D"),
        "one",
        DateTimeOffset.UtcNow,
        kind,
        "github",
        "cursor");

    private sealed class MemoryGatewaySink : IGatewayEventSink
    {
        private readonly Lock _lock = new();
        private readonly List<GatewayEvent> _events = [];

        public IReadOnlyList<GatewayEvent> Events
        {
            get { lock (_lock) return _events.ToArray(); }
        }

        public void Record(GatewayEvent item)
        {
            lock (_lock) _events.Add(item);
        }
    }

    private sealed class GatewayHarness : IDisposable
    {
        public GatewayHarness()
        {
            Home = Path.Combine(Path.GetTempPath(), "kytto-gateway-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Home);
            Paths = new KyttoPaths(Path.Combine(Home, "KyttoData"));
            Backups = new BackupStore(Paths);
            Routes = new GatewayRouteStore(Paths);
            Secrets = new InMemorySecretStore();
            HelperPath = Path.Combine(Home, "Kytto", "kytto-mcp-proxy.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(HelperPath)!);
            File.WriteAllBytes(HelperPath, []);
        }

        public string Home { get; }
        public KyttoPaths Paths { get; }
        public BackupStore Backups { get; }
        public GatewayRouteStore Routes { get; }
        public InMemorySecretStore Secrets { get; }
        public string HelperPath { get; }
        public DigestLedger Ledger { get; } = new();

        public GatewayMigrationService Service() => new(
            Home,
            HelperPath,
            new ConfigWriter(Backups),
            Ledger,
            Routes,
            Secrets);

        public DiscoveryResult Discover() => new Discovery(
            Home,
            new AllInstalledLocator(),
            recordDigest: (path, digest) => Ledger.Record(path, digest)).Run();

        public void Write(string text, string relativePath)
        {
            var path = Path.Combine(Home, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        }

        public string Text(string relativePath) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Home, relativePath)));

        public void Dispose()
        {
            try { Directory.Delete(Home, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
