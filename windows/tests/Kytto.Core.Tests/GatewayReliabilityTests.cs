using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Gateway;
using Kytto.Core.Json;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using Kytto.Core.Tests.Support;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Tests;

/// <summary>
/// What happens to Gateway mode when something it depends on goes away: Kytto is moved,
/// a Credential Manager entry is lost, a token is rotated, the server dies.
/// </summary>
public sealed class GatewayReliabilityTests
{
    private const string CursorRoute = "a6ed52d3-be69-4a4f-a215-ac17c685f520";
    private const string CodexRoute = "645c1896-23de-47ea-bdbd-f97f1fc63896";
    private const string TokenKey = "GITHUB_PERSONAL_ACCESS_TOKEN";

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Home = Path.Combine(Path.GetTempPath(), "kytto-gateway-reliability", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Home);
            Paths = new KyttoPaths(Path.Combine(Home, "KyttoData"));
            Backups = new BackupStore(Paths);
            Routes = new GatewayRouteStore(Paths);
            HelperPath = MakeHelper(Path.Combine(Home, "Downloads", "Kytto"));
        }

        public string Home { get; }
        public KyttoPaths Paths { get; }
        public BackupStore Backups { get; }
        public GatewayRouteStore Routes { get; }
        public InMemorySecretStore Secrets { get; } = new();
        public string HelperPath { get; }
        public DigestLedger Ledger { get; } = new();

        public static string MakeHelper(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "kytto-mcp-proxy.exe");
            File.WriteAllBytes(path, []);
            return path;
        }

        public GatewayMigrationService Service(string? helper = null) => new(
            Home, helper ?? HelperPath, new ConfigWriter(Backups), Ledger, Routes, Secrets);

        public DiscoveryResult Discover() => new Discovery(
            Home,
            new AllInstalledLocator(),
            recordDigest: (path, digest) => Ledger.Record(path, digest)).Run();

        /// <summary>Kytto moved: the old helper is gone and a new copy exists elsewhere.</summary>
        public string MoveKytto()
        {
            Directory.Delete(Path.Combine(Home, "Downloads", "Kytto"), recursive: true);
            return MakeHelper(Path.Combine(Home, "Programs", "Kytto"));
        }

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

    private static string EnableCursor(Harness harness)
    {
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        var original = harness.Text(@".cursor\mcp.json");
        var github = Assert.Single(harness.Discover().Servers, server => server.Id == "github");
        harness.Service().Enable(github, ClientId.Cursor, CursorRoute);
        harness.Discover();
        return original;
    }

    // Kytto was moved

    [Fact]
    public void AWorkingRouteHasNothingToReport()
    {
        using var harness = new Harness();
        EnableCursor(harness);
        Assert.Empty(harness.Service().Diagnose());
    }

    [Fact]
    public void AMovedKyttoIsNoticedRepairedInPlaceAndStillRestoresByteForByte()
    {
        using var harness = new Harness();
        var original = EnableCursor(harness);
        var oldHelper = harness.HelperPath;
        var newHelper = harness.MoveKytto();

        // From the old copy's point of view there is nothing to repair with.
        var stuck = Assert.Single(harness.Service().Diagnose());
        Assert.Equal(GatewayRouteProblem.HelperMissing, stuck.Problem);
        Assert.Equal(oldHelper, stuck.HelperPath);
        Assert.False(stuck.Repairable);

        var moved = harness.Service(newHelper);
        var diagnosis = Assert.Single(moved.Diagnose());
        Assert.True(diagnosis.Repairable);
        Assert.Equal("github", diagnosis.ServerID);
        Assert.Equal(CursorRoute, diagnosis.RouteID);

        var repaired = moved.Repair(CursorRoute);
        Assert.NotNull(repaired.BackupID);
        Assert.Equal(newHelper, repaired.Route.GatewayCommand);
        var document = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        var definition = document.ValueAt("mcpServers", "github")!;
        Assert.Equal(newHelper, definition["command"]?.StringValue);
        Assert.Equal(["--route", CursorRoute], definition["args"]?.Elements?.Select(item => item.StringValue));
        // Nothing else in the file moved.
        Assert.Equal("https://mcp.figma.com/sse", document.ValueAt("mcpServers", "figma", "url")?.StringValue);
        Assert.Equal(newHelper, harness.Routes.Route(CursorRoute).GatewayCommand);
        Assert.Empty(moved.Diagnose());

        harness.Discover();
        moved.Restore(CursorRoute);
        Assert.Equal(original, harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void RepairingACodexRouteKeepsTheSettingsCodexReadsFromTheBlock()
    {
        using var harness = new Harness();
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var original = harness.Text(@".codex\config.toml");
        var filesystem = Assert.Single(harness.Discover().Servers, server => server.Id == "filesystem");
        harness.Service().Enable(filesystem, ClientId.Codex, CodexRoute);
        harness.Discover();
        var before = harness.Text(@".codex\config.toml");

        var newHelper = harness.MoveKytto();
        var moved = harness.Service(newHelper);
        Assert.Equal([CodexRoute], moved.Diagnose().Select(item => item.RouteID));
        moved.Repair(CodexRoute);

        Assert.Equal(
            before.Replace(TomlEscaped(harness.HelperPath), TomlEscaped(newHelper), StringComparison.Ordinal),
            harness.Text(@".codex\config.toml"));
        Assert.Empty(moved.Diagnose());

        harness.Discover();
        moved.Restore(CodexRoute);
        Assert.Equal(original, harness.Text(@".codex\config.toml"));
    }

    private static string TomlEscaped(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    [Fact]
    public void ARepairRefusesAConfigThatChangedOnDisk()
    {
        using var harness = new Harness();
        EnableCursor(harness);
        var moved = harness.Service(harness.MoveKytto());
        var edited = harness.Text(@".cursor\mcp.json") + "\n";
        harness.Write(edited, @".cursor\mcp.json");

        var error = Assert.Throws<ConfigWriteException>(() => moved.Repair(CursorRoute));
        Assert.True(error.IsChangedOnDisk);
        Assert.Equal(edited, harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void ADefinitionPointingAtARouteKyttoHasLostIsReported()
    {
        using var harness = new Harness();
        EnableCursor(harness);
        harness.Routes.Remove(CursorRoute);

        var diagnosis = Assert.Single(harness.Service().Diagnose());
        Assert.Equal(GatewayRouteProblem.UnknownRoute, diagnosis.Problem);
        Assert.Equal("github", diagnosis.ServerName);
        Assert.Equal(ClientId.Cursor, diagnosis.ClientID);
    }

    // The credential store lost something

    [Fact]
    public void DirectModeIsRebuiltFromTheRouteWhenTheStoredOriginalIsGone()
    {
        using var harness = new Harness();
        EnableCursor(harness);
        harness.Secrets.Delete(GatewayRoute.DirectDefinitionIdentifier(CursorRoute));

        var result = harness.Service().Restore(CursorRoute);

        Assert.Empty(result.MissingEnvironmentKeys);
        var definition = JsonDocument.Parse(harness.Text(@".cursor\mcp.json")).ValueAt("mcpServers", "github")!;
        Assert.Equal("npx", definition["command"]?.StringValue);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], definition["args"]?.Elements?.Select(item => item.StringValue));
        Assert.Equal("ghp_exampletokenvalue", definition["env"]?[TokenKey]?.StringValue);
        Assert.Empty(harness.Routes.All());
    }

    [Fact]
    public void AValueThatIsGoneTooIsNamedInsteadOfWrittenEmpty()
    {
        using var harness = new Harness();
        EnableCursor(harness);
        harness.Secrets.Delete(GatewayRoute.DirectDefinitionIdentifier(CursorRoute));
        harness.Secrets.Delete(GatewayRoute.EnvironmentIdentifier(CursorRoute, TokenKey));

        var result = harness.Service().Restore(CursorRoute);

        Assert.Equal([TokenKey], result.MissingEnvironmentKeys);
        var definition = JsonDocument.Parse(harness.Text(@".cursor\mcp.json")).ValueAt("mcpServers", "github")!;
        Assert.Equal("npx", definition["command"]?.StringValue);
        Assert.Null(definition["env"]?[TokenKey]);
    }

    // A secret was rotated

    [Fact]
    public void ARotatedValueReachesTheRouteAndTheDefinitionItWouldRestore()
    {
        using var harness = new Harness();
        var original = EnableCursor(harness);

        var changed = harness.Service().RotateEnvironmentValue(TokenKey, "ghp_exampletokenvalue", "ghp_rotated");

        Assert.Equal([CursorRoute], changed.Select(route => route.Id));
        Assert.Equal("ghp_rotated", harness.Routes.Resolve(CursorRoute, harness.Secrets).Environment[TokenKey]);
        harness.Service().Restore(CursorRoute);
        // Everything as it was, except the one value.
        Assert.Equal(
            original.Replace("ghp_exampletokenvalue", "ghp_rotated", StringComparison.Ordinal),
            harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void ARouteHoldingADifferentValueUnderTheSameNameIsLeftAlone()
    {
        using var harness = new Harness();
        EnableCursor(harness);

        Assert.Empty(harness.Service().RotateEnvironmentValue(TokenKey, "ghp_someone_else", "ghp_new"));
        Assert.Equal("ghp_exampletokenvalue", harness.Routes.Resolve(CursorRoute, harness.Secrets).Environment[TokenKey]);
    }

    /// <summary>Every way Codex accepts an environment: inline, dotted, or a sub-table.</summary>
    [Theory]
    [InlineData("[mcp_servers.x]\ncommand = \"npx\"\nenv = { TOKEN = \"old\", OTHER = \"old\" }\n")]
    [InlineData("[mcp_servers.x]\ncommand = \"npx\"\nenv.TOKEN = \"old\"\nenv.OTHER = \"old\"\n")]
    [InlineData("[mcp_servers.x]\ncommand = \"npx\"\n\n[mcp_servers.x.env]\nTOKEN = \"old\"\nKEEP = \"old\"\n")]
    public void ATomlDefinitionHasOnlyTheMatchingValueReplaced(string block)
    {
        var rewritten = GatewayMigrationService.ReplacingEnvironmentValue("TOKEN", "old", "new", block, ConfigFormat.Toml);

        Assert.Equal(block.Replace("TOKEN = \"old\"", "TOKEN = \"new\"", StringComparison.Ordinal), rewritten);
    }

    // Doctor

    private static GatewayEvent Event(
        GatewayEventKind kind,
        string session,
        double secondsAgo,
        DateTimeOffset now,
        bool? unexpected = null,
        string? reason = null) => new(
            Guid.NewGuid().ToString("D"),
            session,
            now.AddSeconds(-secondsAgo),
            kind,
            "github",
            "cursor",
            ExitCode: kind == GatewayEventKind.SessionEnded ? 1 : null,
            Unexpected: unexpected,
            Reason: reason);

    [Fact]
    public void AMissingHelperBecomesAFindingThatOffersTheRepair()
    {
        var diagnosis = new GatewayRouteDiagnosis(
            "github", "github", ClientId.Cursor, "r1", GatewayRouteProblem.HelperMissing,
            @"C:\Old\kytto-mcp-proxy.exe", Repairable: true);

        var finding = GatewayDoctor.Findings([diagnosis], [])["github"].Single();

        Assert.Equal("gateway-helper-missing", finding.Code);
        Assert.Equal(DoctorSeverity.Error, finding.Severity);
        Assert.Equal(DoctorAction.RepairGatewayRoute, finding.Action);
        Assert.Equal("r1", finding.RouteID);
        Assert.Contains("Cursor", finding.Title, StringComparison.Ordinal);

        var stuck = diagnosis with { Repairable = false };
        Assert.Null(GatewayDoctor.Findings([stuck], [])["github"].Single().Action);
    }

    [Fact]
    public void AServerThatKeepsStoppingIsReportedUntilItRunsCleanlyAgain()
    {
        var now = DateTimeOffset.UtcNow;
        GatewayEvent[] crashes =
        [
            Event(GatewayEventKind.SessionStarted, "a", 600, now),
            Event(GatewayEventKind.SessionEnded, "a", 500, now, unexpected: true),
            Event(GatewayEventKind.SessionStarted, "b", 400, now),
            Event(GatewayEventKind.SessionEnded, "b", 300, now, unexpected: true),
        ];

        var finding = GatewayDoctor.Findings([], crashes, now: now)["github"].Single();
        Assert.Equal("gateway-server-stopped", finding.Code);
        Assert.Equal(DoctorAction.RunHealthCheck, finding.Action);
        Assert.Contains("2 times", finding.Detail, StringComparison.Ordinal);

        GatewayEvent[] recovered = [.. crashes, Event(GatewayEventKind.SessionStarted, "c", 100, now)];
        Assert.Empty(GatewayDoctor.Findings([], recovered, now: now));
    }

    [Fact]
    public void AnOrdinaryExitAndAnOldCrashAreNotFindings()
    {
        var now = DateTimeOffset.UtcNow;
        GatewayEvent[] events =
        [
            Event(GatewayEventKind.SessionEnded, "a", 60, now, unexpected: false),
            Event(GatewayEventKind.SessionEnded, "b", 60, now),
            Event(GatewayEventKind.SessionEnded, "c", GatewayDoctor.FailureWindow.TotalSeconds + 60, now, unexpected: true),
        ];
        Assert.Empty(GatewayDoctor.Findings([], events, now: now));
    }

    [Fact]
    public void AnUnreadableSecretOffersRestoreDirectForTheRouteItBelongsTo()
    {
        var now = DateTimeOffset.UtcNow;
        var route = new GatewayRoute(
            "r9", "github", "github", ClientId.Cursor, @"C:\Kytto\kytto-mcp-proxy.exe", "npx", [], [], "d", now);
        GatewayEvent[] events = [Event(GatewayEventKind.SessionFailed, "a", 30, now, reason: GatewayFailureReason.SecretUnavailable)];

        var finding = GatewayDoctor.Findings([], events, [route], now)["github"].Single();

        Assert.Equal("gateway-secret-unavailable", finding.Code);
        Assert.Equal(DoctorAction.RestoreDirectMode, finding.Action);
        Assert.Equal("r9", finding.RouteID);
    }

    // Journal

    [Fact]
    public void TheSummaryCountsRefusalsAndServerFailuresAndOldJournalsStillDecode()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = GatewayActivitySummary.From(
        [
            new GatewayEvent("e", "a", now, GatewayEventKind.ToolCallRefused, "s", "c", "write", Succeeded: false),
            Event(GatewayEventKind.SessionEnded, "a", 1, now, unexpected: true),
            Event(GatewayEventKind.SessionFailed, "b", 1, now, reason: GatewayFailureReason.LaunchFailed),
            Event(GatewayEventKind.SessionEnded, "c", 1, now, unexpected: false),
        ]);
        Assert.Equal(1, summary.RefusedCalls);
        Assert.Equal(2, summary.ServerFailures);
        // A refusal never reached the server, so it is not a failed call.
        Assert.Equal(0, summary.FailedCalls);

        const string Old = """{"eventID":"e","sessionID":"s","timestamp":"2026-10-01T10:00:00Z","kind":"session.ended","serverID":"a","clientID":"b","exitCode":0}""";
        var decoded = JsonSerializer.Deserialize<GatewayEvent>(Old, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        })!;
        Assert.Null(decoded.Unexpected);
        Assert.Null(decoded.Reason);
    }

    [Fact]
    public void NewKindsAndFieldsRoundTripThroughTheJournal()
    {
        using var harness = new Harness();
        var path = harness.Paths.GatewayEventsFile;
        using (var sink = new JsonLinesGatewayEventSink(path))
        {
            var observer = new GatewayMessageObserver("github", "cursor", sink, "one");
            observer.RecordRefusal("write_file", "2");
            observer.Fail(GatewayFailureReason.LaunchFailed);
            // A session reports one ending, however many paths reach it.
            observer.Finish(1, unexpected: true);
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"tool.call.refused\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"session.failed\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"launch-failed\"", lines[1], StringComparison.Ordinal);
        var events = new GatewayActivityStore(harness.Paths).Summary().Events;
        Assert.Equal(1, GatewayActivitySummary.From(events).ServerFailures);
    }

    [Fact]
    public void TheViewIsFilledFromTheRotatedJournalInsteadOfGoingBlank()
    {
        using var harness = new Harness();
        var path = harness.Paths.GatewayEventsFile;
        using (var previous = new JsonLinesGatewayEventSink(path + ".1"))
        {
            previous.Record(Event(GatewayEventKind.SessionStarted, "old", 10, DateTimeOffset.UtcNow));
        }
        using (var current = new JsonLinesGatewayEventSink(path))
        {
            current.Record(Event(GatewayEventKind.SessionStarted, "new", 1, DateTimeOffset.UtcNow));
        }

        var sessions = new GatewayActivityStore(harness.Paths).Summary().Events.Select(item => item.SessionID).ToHashSet();
        Assert.Equal(["new", "old"], sessions.Order());
    }

    [Fact]
    public void TheFilterNamesOnlyTheHiddenCallsInALine()
    {
        var filter = new GatewayToolFilter(["read_file"]);
        var calls = filter.HiddenCalls(Encoding.UTF8.GetBytes(
            """[{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"private"}}},""" +
            """{"jsonrpc":"2.0","id":"x","method":"tools/call","params":{"name":"read_file"}}]"""));

        var call = Assert.Single(calls);
        Assert.Equal("write_file", call.ToolName);
        Assert.Equal("2", call.RequestId);
    }
}
