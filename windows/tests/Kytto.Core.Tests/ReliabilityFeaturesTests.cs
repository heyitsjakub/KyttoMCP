using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Gateway;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Settings;

namespace Kytto.Core.Tests;

public sealed class ReliabilityFeaturesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-reliability-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DoctorOffersOnlyTheVerifiedExecutableRepair()
    {
        var server = Server(command: "npx");
        server.Health = Passing([], Path.Combine(_root, "npx.cmd"));

        var finding = Assert.Single(McpDoctor.Analyze(server).Findings, item =>
            item.Code == "unpinned-command");

        Assert.Equal(DoctorAction.PinResolvedCommand, finding.Action);
        Assert.Contains("npx.cmd", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorNeverGuessesMissingEnvironmentValues()
    {
        var server = Server(command: "npx", env: [new EnvEntry("API_TOKEN", null)]);

        var finding = Assert.Single(McpDoctor.Analyze(server).Findings, item =>
            item.Code == "missing-environment");

        Assert.Equal(DoctorSeverity.Error, finding.Severity);
        Assert.Null(finding.Action);
        Assert.DoesNotContain("value", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorRejectsAStaleResolutionForAnotherExecutableExtension()
    {
        var server = Server(command: "node.exe");
        server.Health = Passing([], Path.Combine(_root, "node.cmd"));

        Assert.DoesNotContain(McpDoctor.Analyze(server).Findings, item =>
            item.Action == DoctorAction.PinResolvedCommand);
    }

    [Fact]
    public void DoctorReportsInteractiveAuthorizationAsInformation()
    {
        var server = Server(command: "npx");
        server.Health = new HealthResult(
            HealthStatus.NeedsAuthorization,
            Timestamp.Now,
            null,
            [],
            HealthFailureReason.AwaitingAuthorization().CurrentMessage(),
            "Waiting for authorization",
            1,
            null,
            null)
        {
            Failure = HealthFailureReason.AwaitingAuthorization(),
        };

        var finding = Assert.Single(McpDoctor.Analyze(server).Findings,
            item => item.Code == "needs-authorization");
        Assert.Equal(DoctorSeverity.Info, finding.Severity);
        Assert.Equal(DoctorAction.RunHealthCheck, finding.Action);
    }

    [Fact]
    public void ContractGuardClassifiesEveryModelFacingChange()
    {
        ToolSummary[] before =
        [
            new("read_file", "Read a file", "{\"type\":\"object\"}", new ToolAnnotations(ReadOnlyHint: true)),
            new("removed", null),
        ];
        ToolSummary[] after =
        [
            new("read_file", "Read any file", "{\"required\":[\"path\"]}", new ToolAnnotations(ReadOnlyHint: false)),
            new("added", null),
        ];

        var changes = ContractGuard.Changes(before, after);

        Assert.Contains(changes, item => item.Kind == ContractChangeKind.ToolRemoved &&
                                         item.Severity == ContractChangeSeverity.Breaking);
        Assert.Contains(changes, item => item.Kind == ContractChangeKind.ToolAdded);
        Assert.Contains(changes, item => item.Kind == ContractChangeKind.DescriptionChanged);
        Assert.Contains(changes, item => item.Kind == ContractChangeKind.InputSchemaChanged &&
                                         item.Severity == ContractChangeSeverity.Breaking);
        Assert.Contains(changes, item => item.Kind == ContractChangeKind.AnnotationsChanged);
    }

    [Fact]
    public void FailedCheckDoesNotEraseTheSuccessfulContractBaseline()
    {
        var store = new MetadataStore(new KyttoPaths(_root));
        store.Record("server", Passing([new ToolSummary("read", "Read")], @"C:\server.exe"), null);
        store.Record("server", new HealthResult(
            HealthStatus.Failed,
            Timestamp.Now,
            null,
            [],
            "crashed",
            null,
            0.1,
            null,
            null), null);
        store.Record("server", Passing([new ToolSummary("read", "Read safely")], @"C:\server.exe"), null);

        Assert.Contains(store.For("server")!.ContractChanges!, item =>
            item.Kind == ContractChangeKind.DescriptionChanged);
    }

    [Fact]
    public void ActivityReaderSummarizesCompleteRecordsAndIgnoresAPartialTail()
    {
        var paths = new KyttoPaths(_root);
        using (var sink = new JsonLinesGatewayEventSink(paths.GatewayEventsFile))
        {
            sink.Record(new GatewayEvent(
                "event",
                "session-1",
                Timestamp.Now,
                GatewayEventKind.ToolCallCompleted,
                "github",
                "cursor",
                "create_issue",
                DurationMilliseconds: 120,
                Succeeded: false,
                ErrorCode: -32001));
        }
        File.AppendAllText(paths.GatewayEventsFile, "{partial");

        var summary = new GatewayActivityStore(paths).Summary();

        Assert.Single(summary.Events);
        Assert.Equal(1, summary.TotalSessions);
        Assert.Equal(1, summary.CompletedCalls);
        Assert.Equal(1, summary.FailedCalls);
        Assert.Equal(120, summary.AverageDurationMilliseconds);
    }

    [Fact]
    public void ContextOptimizerFindsBudgetsHeavyServersAndDuplicateTools()
    {
        var profile = new Profile("profile", "Coding", ["one", "two"], 10_000);
        var first = Server("one", "One", "one");
        first.TokenWeight = new TokenWeight(22_000, "test", Timestamp.Now);
        first.Health = Passing([new ToolSummary("search", null)], @"C:\one.exe");
        var second = Server("two", "Two", "two");
        second.TokenWeight = new TokenWeight(2_000, "test", Timestamp.Now);
        second.Health = Passing([new ToolSummary("search", null)], @"C:\two.exe");

        var analysis = ContextOptimizer.Analyze(profile, [first, second]);

        Assert.Equal(24_000, analysis.EstimatedTokens);
        Assert.Contains(analysis.Recommendations, item => item.Kind == ContextRecommendationKind.OverBudget);
        Assert.Contains(analysis.Recommendations, item => item.Kind == ContextRecommendationKind.HeavyServer);
        Assert.Contains(analysis.Recommendations, item => item.Kind == ContextRecommendationKind.DuplicateTool);
    }

    private static Server Server(
        string id = "server",
        string name = "Server",
        string command = "npx",
        IReadOnlyList<EnvEntry>? env = null) => new()
    {
        Id = id,
        Name = name,
        Transport = Transport.Stdio,
        Command = command,
        Args = [],
        Env = env ?? [],
        Url = null,
        EnabledIn = new Dictionary<ClientKey, Enablement> { [ClientId.Cursor] = Enablement.Enabled },
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = Kytto.Core.Model.Server.MakeFingerprint(command, [], null),
        IsBundled = false,
        DefinitionSource = "{}",
    };

    private static HealthResult Passing(IReadOnlyList<ToolSummary> tools, string resolved) => new(
        HealthStatus.Passed,
        Timestamp.Now,
        tools.Count,
        tools,
        null,
        null,
        0.1,
        "fixture",
        "1")
    {
        ResolvedCommand = resolved,
        EnvironmentSource = "test",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }
}
