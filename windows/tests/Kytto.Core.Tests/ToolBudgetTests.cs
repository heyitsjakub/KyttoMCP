using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Gateway;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

/// <summary>ToolBudget — the tools a client hands its model.</summary>
public sealed class ToolBudgetTests
{
    private static readonly ClientToolLimit Limit = new(10, "Past ten, the rest are dropped.");

    [Fact]
    public void OnlyServersSwitchedOnInTheClientCount()
    {
        Server[] servers =
        [
            BudgetServer("on", ClientId.Cursor, Enablement.Enabled, tools: 4),
            BudgetServer("off", ClientId.Cursor, Enablement.Disabled, tools: 50),
            BudgetServer("elsewhere", ClientId.Codex, Enablement.Enabled, tools: 50),
        ];

        var budget = ToolBudget.Evaluate(ClientId.Cursor, Limit, servers, []);

        Assert.Equal(4, budget.ToolCount);
        Assert.Empty(budget.UnmeasuredServerIDs);
        Assert.False(budget.IsLowerBound);
        Assert.Equal(ToolLimitState.Ok, budget.State);
        Assert.Null(budget.Severity);
    }

    [Fact]
    public void NearStartsAtEightyPercentAndOverIsStrictlyPastTheCap()
    {
        static ToolLimitState? State(int tools) => ToolBudget.Evaluate(
            ClientId.VsCode,
            Limit,
            [BudgetServer("s", ClientId.VsCode, Enablement.Enabled, tools)],
            []).State;

        Assert.Equal(ToolLimitState.Ok, State(7));
        Assert.Equal(ToolLimitState.Near, State(8));
        // At the cap every tool still arrives.
        Assert.Equal(ToolLimitState.Near, State(10));
        Assert.Equal(ToolLimitState.Over, State(11));
    }

    [Fact]
    public void OverIsAWarningAndNearIsInformation()
    {
        var over = ToolBudget.Evaluate(
            ClientId.VsCode, Limit,
            [BudgetServer("s", ClientId.VsCode, Enablement.Enabled, tools: 12)], []);
        Assert.Equal(DoctorSeverity.Warning, over.Severity);

        var near = ToolBudget.Evaluate(
            ClientId.VsCode, Limit,
            [BudgetServer("s", ClientId.VsCode, Enablement.Enabled, tools: 9)], []);
        Assert.Equal(DoctorSeverity.Info, near.Severity);
    }

    [Fact]
    public void UnmeasuredServersMakeTheCountALowerBound()
    {
        var failed = BudgetServer("failed", ClientId.Cursor, Enablement.Enabled, tools: 0);
        failed.Health = new HealthResult(
            HealthStatus.Failed, Timestamp.Now, null, [], null, null, 0, null, null);
        var never = BudgetServer("never", ClientId.Cursor, Enablement.Enabled, tools: 0);
        never.Health = null;
        Server[] servers =
        [
            BudgetServer("measured", ClientId.Cursor, Enablement.Enabled, tools: 9),
            never,
            failed,
        ];

        var budget = ToolBudget.Evaluate(ClientId.Cursor, Limit, servers, []);

        Assert.Equal(9, budget.ToolCount);
        Assert.Equal(["failed", "never"], budget.UnmeasuredServerIDs);
        Assert.True(budget.IsLowerBound);
        // A lower bound already near the cap is still near; nothing is guessed for
        // the servers nobody has checked.
        Assert.Equal(ToolLimitState.Near, budget.State);
    }

    [Fact]
    public void AMeasuredServerWithNoToolsCountsAsZeroNotUnmeasured()
    {
        var budget = ToolBudget.Evaluate(
            ClientId.Cursor, Limit,
            [BudgetServer("empty", ClientId.Cursor, Enablement.Enabled, tools: 0)], []);

        Assert.Equal(0, budget.ToolCount);
        Assert.False(budget.IsLowerBound);
    }

    [Fact]
    public void GatewayMaskingRemovesHiddenToolsForThatClientOnly()
    {
        Server[] servers =
        [
            BudgetServer("github", ClientId.Cursor, Enablement.Enabled, tools: 6, alsoIn: ClientId.Codex),
        ];
        GatewayRoute[] routes = [Route("github", ClientId.Cursor, exposing: ["tool0", "tool1", "gone"])];

        var masked = ToolBudget.Evaluate(ClientId.Cursor, Limit, servers, routes);
        // "gone" is not offered by the server, so the filter has nothing to pass.
        Assert.Equal(2, masked.ToolCount);
        Assert.Equal(4, masked.MaskedToolCount);

        var other = ToolBudget.Evaluate(ClientId.Codex, Limit, servers, routes);
        Assert.Equal(6, other.ToolCount);
        Assert.Equal(0, other.MaskedToolCount);
    }

    [Fact]
    public void ARouteWithoutAnAllowListHidesNothing()
    {
        Server[] servers = [BudgetServer("github", ClientId.Cursor, Enablement.Enabled, tools: 6)];
        GatewayRoute[] routes = [Route("github", ClientId.Cursor, exposing: null)];

        var budget = ToolBudget.Evaluate(ClientId.Cursor, Limit, servers, routes);

        Assert.Equal(6, budget.ToolCount);
        Assert.Equal(0, budget.MaskedToolCount);
    }

    [Fact]
    public void APartialToolListFromAnOlderCheckFallsBackToTheAllowListsLength()
    {
        var server = BudgetServer("legacy", ClientId.Cursor, Enablement.Enabled, tools: 0);
        server.Health = Passing(toolCount: 26, names: ["a", "b"]);
        GatewayRoute[] routes = [Route("legacy", ClientId.Cursor, exposing: ["a", "x", "y"])];

        var budget = ToolBudget.Evaluate(ClientId.Cursor, Limit, [server], routes);

        Assert.Equal(3, budget.ToolCount);
        Assert.Equal(23, budget.MaskedToolCount);
    }

    [Fact]
    public void NoCapMeansACountAndNoState()
    {
        var budget = ToolBudget.Evaluate(
            ClientId.ClaudeCode, null,
            [BudgetServer("big", ClientId.ClaudeCode, Enablement.Enabled, tools: 500)], []);

        Assert.Equal(500, budget.ToolCount);
        Assert.Null(budget.Limit);
        Assert.Null(budget.State);
        Assert.Null(budget.Severity);
    }

    /// <summary>
    /// A custom source is a column like any other for counting, and can never be
    /// matched to a Gateway route, which only built-in clients have.
    /// </summary>
    [Fact]
    public void ACustomSourceCountsWhatIsSwitchedOnInIt()
    {
        var custom = ClientKey.Custom(Guid.Parse("1f0d32d9-88e7-40b8-9b8e-2f38edc4a7d1"));
        var server = BudgetServer("shared", ClientId.Cursor, Enablement.Enabled, tools: 5);
        server.EnabledIn[custom] = Enablement.Enabled;
        GatewayRoute[] routes = [Route("shared", ClientId.Cursor, exposing: ["tool0"])];

        var budget = ToolBudget.Evaluate(custom, null, [server], routes);

        Assert.Equal(5, budget.ToolCount);
        Assert.Equal(0, budget.MaskedToolCount);
    }

    [Fact]
    public void TheRegistryEncodesOnlyDocumentedCaps()
    {
        Assert.Equal(128, ClientRegistry.Descriptor(ClientId.VsCode).ToolLimit?.MaxTools);
        Assert.False(string.IsNullOrWhiteSpace(
            ClientRegistry.Descriptor(ClientId.VsCode).ToolLimit?.PastLimitSummary));
        Assert.Null(ClientRegistry.Descriptor(ClientId.Cursor).ToolLimit);
        Assert.Null(ClientRegistry.Descriptor(ClientId.ClaudeDesktop).ToolLimit);
        Assert.Null(ClientRegistry.Descriptor(ClientId.ClaudeCode).ToolLimit);
        Assert.Null(ClientRegistry.Descriptor(ClientId.Codex).ToolLimit);
    }

    // MARK: - Helpers

    private static HealthResult Passing(int toolCount, IReadOnlyList<string> names) => new(
        HealthStatus.Passed,
        Timestamp.Now,
        toolCount,
        names.Select(name => new ToolSummary(name, null)).ToArray(),
        null,
        null,
        0.1,
        null,
        null);

    private static Server BudgetServer(
        string id,
        ClientId client,
        Enablement enablement,
        int tools,
        ClientId? alsoIn = null)
    {
        var enabledIn = new Dictionary<ClientKey, Enablement> { [client] = enablement };
        if (alsoIn is { } other) enabledIn[other] = Enablement.Enabled;
        return new Server
        {
            Id = id,
            Name = id,
            Transport = Transport.Stdio,
            Command = id,
            Args = [],
            Env = [],
            Url = null,
            EnabledIn = enabledIn,
            Origin = new Origin.ConfigFile(client),
            Fingerprint = Server.MakeFingerprint(id, [], null),
            IsBundled = false,
            DefinitionSource = "{}",
            Health = Passing(tools, Enumerable.Range(0, tools).Select(index => $"tool{index}").ToArray()),
        };
    }

    private static GatewayRoute Route(string server, ClientId client, IReadOnlyList<string>? exposing) =>
        new(
            Id: Guid.NewGuid().ToString(),
            ServerID: server,
            ServerName: server,
            ClientID: client,
            GatewayCommand: @"C:\Program Files\Kytto\kytto-mcp-proxy.exe",
            Command: server,
            Arguments: [],
            Environment: [],
            DirectDefinitionSecretID: "unused",
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            ExposedTools = exposing,
        };
}
