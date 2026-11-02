using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

internal static class ToolSwitchHarnessExtensions
{
    public static ToolSwitchService ToolSwitches(this ToggleHarness harness) =>
        new(harness.Home, new ConfigWriter(harness.Backups), harness.Ledger);
}

public class ToolFilterTests
{
    [Fact]
    public void GlobsExactRulesAndAllowListsDecideInThatOrder()
    {
        var filter = new ToolFilter(prefix: "github_", rules:
        [
            new ToolFilter.Rule("github_delete_*", false),
            new ToolFilter.Rule("github_delete_tag", true),
        ]);
        Assert.True(filter.IsOff("delete_branch"));
        Assert.False(filter.IsOff("delete_tag"));
        Assert.False(filter.IsOff("get_issue"));
        Assert.Equal("github_delete_*", filter.BlockingPattern("delete_branch"));

        var allow = new ToolFilter(rules: [new ToolFilter.Rule("b", false)], allowList: ["a", "b"]);
        Assert.Equal(["b", "c"], allow.OffTools(["a", "b", "c"]));
    }

    [Fact]
    public void NamesBecomeIdentifiersTheWayTheClientsSpellThem()
    {
        Assert.Equal("my_server_name", ToolFilter.Sanitized("my.server name"));
        Assert.True(ToolFilter.Glob("mcp__git*__get_?", "mcp__github__get_x"));
        Assert.False(ToolFilter.Glob("mcp__git*__get_?", "mcp__github__get_xy"));
    }

    [Fact]
    public void AToolNameListedTwiceDoesNotBreakTheLockMap()
    {
        var filter = new ToolFilter(prefix: "mcp__s__", wholeServerPatterns: ["mcp__s"]);
        var locked = filter.LockedTools(["a", "a", "b"]);
        Assert.Equal(2, locked.Count);
        Assert.Equal("mcp__s", locked["a"]);
        Assert.Equal("mcp__s", locked["b"]);
    }
}

public class ToolSwitchTests
{
    private static readonly string[] GithubTools = ["get_issue", "create_issue", "delete_branch", "list_repos"];

    private const string OpenCodePath = @".config\opencode\opencode.json";
    private const string AntigravityPath = @".gemini\config\mcp_config.json";

    [Fact]
    public void CodexDisabledToolsOnTheServersTableGoneAgainWhenEmpty()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var before = harness.Text(@".codex\config.toml");

        var result = harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "delete_branch" }, GithubTools, harness.Server("filesystem"), ClientId.Codex);
        Assert.NotNull(result.BackupID);
        var table = TomlDocument.Parse(harness.Text(@".codex\config.toml")).Table("mcp_servers", "filesystem")!;
        Assert.Equal(["delete_branch"], table.Value("disabled_tools")!.Elements!.Select(item => item.StringValue));
        Assert.True(harness.Server("filesystem").ToolFilters[ClientId.Codex].IsOff("delete_branch"));

        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string>(), GithubTools, harness.Server("filesystem"), ClientId.Codex);
        Assert.Equal(before, harness.Text(@".codex\config.toml"));
    }

    [Fact]
    public void CodexAnAllowListIsEditedInsteadOfGrowingADenyList()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            Fixture.CodexConfig.Text().Replace(
                "startup_timeout_sec = 20",
                "startup_timeout_sec = 20\nenabled_tools = [\"get_issue\", \"create_issue\"]",
                StringComparison.Ordinal),
            @".codex\config.toml");

        Assert.Equal(
            ["delete_branch", "list_repos"],
            harness.Server("filesystem").ToolFilters[ClientId.Codex].OffTools(GithubTools));
        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "create_issue", "delete_branch" },
            GithubTools, harness.Server("filesystem"), ClientId.Codex);
        var table = TomlDocument.Parse(harness.Text(@".codex\config.toml")).Table("mcp_servers", "filesystem")!;
        Assert.Equal(
            ["get_issue", "list_repos"],
            table.Value("enabled_tools")!.Elements!.Select(item => item.StringValue));
        Assert.Null(table.Value("disabled_tools"));
    }

    [Fact]
    public void AntigravityDisabledToolsOnTheEntryEverythingElseKept()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "disabledTools": ["delete_branch"]
                }
              }
            }
            """, AntigravityPath);

        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "delete_branch", "create_issue" },
            GithubTools, harness.Server("github"), ClientId.Antigravity);
        var entry = JsonDocument.Parse(harness.Text(AntigravityPath)).ValueAt("mcpServers", "github")!;
        Assert.Equal(["delete_branch", "create_issue"], entry["disabledTools"]!.Elements!.Select(item => item.StringValue));

        // A no-op save writes nothing.
        var unchanged = harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "delete_branch", "create_issue" },
            GithubTools, harness.Server("github"), ClientId.Antigravity);
        Assert.Null(unchanged.BackupID);
        Assert.False(unchanged.RequiresRestart);

        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string>(), GithubTools, harness.Server("github"), ClientId.Antigravity);
        Assert.Null(JsonDocument.Parse(harness.Text(AntigravityPath)).ValueAt("mcpServers", "github", "disabledTools"));
    }

    [Fact]
    public void OpenCodeServerToolFalseInTheToolsMapAndAGlobIsNotOverridden()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            {
              // keep
              "mcp": {
                "github": { "type": "local", "command": ["npx", "-y", "@modelcontextprotocol/server-github"] }
              },
              "tools": { "github_delete_*": false, "bash": true }
            }
            """, OpenCodePath);

        var server = harness.Server("github");
        Assert.Equal(["delete_branch"], server.ToolFilters[ClientId.OpenCode].OffTools(GithubTools));

        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "delete_branch", "create_issue" }, GithubTools, server, ClientId.OpenCode);
        var text = harness.Text(OpenCodePath);
        Assert.Contains("// keep", text, StringComparison.Ordinal);
        var tools = JsonDocument.Parse(text).Root["tools"]!;
        Assert.False(tools["github_create_issue"]?.BoolValue);
        Assert.True(tools["bash"]?.BoolValue);
        Assert.Null(tools["github_delete_branch"]);

        Assert.Empty(server.ToolFilters[ClientId.OpenCode].LockedTools(GithubTools));

        // Back on under a glob that keeps it off: the tool's own `true` answers the
        // glob, and the glob itself is left as written.
        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string>(), GithubTools, harness.Server("github"), ClientId.OpenCode);
        var after = JsonDocument.Parse(harness.Text(OpenCodePath)).Root["tools"]!;
        Assert.True(after["github_delete_branch"]?.BoolValue);
        Assert.False(after["github_delete_*"]?.BoolValue);
        Assert.Null(after["github_create_issue"]);
        Assert.Empty(harness.Server("github").ToolFilters[ClientId.OpenCode].OffTools(GithubTools));
    }

    [Fact]
    public void SwitchingOnAToolWhoseOwnRuleSitsUnderABroaderOneDoesNotReportASuccessItDidNotHave()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(
            """{"permissions":{"deny":["mcp__XcodeBuildMCP__build*","mcp__XcodeBuildMCP__build_sim"]}}""",
            @".claude\settings.json");
        var before = harness.Text(@".claude\settings.json");
        var server = harness.Server("XcodeBuildMCP");
        var locked = server.ToolFilters[ClientId.ClaudeCode].LockedTools(["build_sim", "list_sims"]);
        Assert.Equal("mcp__XcodeBuildMCP__build*", Assert.Single(locked).Value);

        var refused = Assert.Throws<ToolSwitchException>(() => harness.ToolSwitches().SetDisabledTools(
            new HashSet<string>(), ["build_sim", "list_sims"], server, ClientId.ClaudeCode));
        Assert.Contains("mcp__XcodeBuildMCP__build*", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, harness.Text(@".claude\settings.json"));
    }

    [Fact]
    public void RenamingAServerMovesTheToolSwitchesThatNameIt()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            {
              "mcp": {
                "github": { "type": "local", "command": ["npx", "-y", "@modelcontextprotocol/server-github"] },
                "github_enterprise": { "type": "local", "command": ["npx", "ghe-mcp"] }
              },
              "tools": { "github_delete_*": false, "github_create_issue": false, "github_enterprise_push": false, "bash": true }
            }
            """, OpenCodePath);
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(
            """{"deniedMcpServers":[],"permissions":{"deny":["mcp__XcodeBuildMCP__build_sim","mcp__Other__x","Bash(rm:*)"]}}""",
            @".claude\settings.json");

        var github = harness.Server("github");
        var draft = ServerDraft.Editing(github) with { Name = "gh" };
        harness.Authoring().Update(draft, "github", github, harness.Discover().Servers);
        var tools = JsonDocument.Parse(harness.Text(OpenCodePath)).Root["tools"]!;
        Assert.Equal(["gh_delete_*", "gh_create_issue", "github_enterprise_push", "bash"], tools.Keys);
        Assert.Equal(
            ["create_issue", "delete_branch"],
            harness.Server("gh").ToolFilters[ClientId.OpenCode].OffTools(GithubTools).Order(StringComparer.Ordinal));

        var xcode = harness.Server("XcodeBuildMCP");
        var xcodeDraft = ServerDraft.Editing(xcode) with { Name = "xcode" };
        harness.Authoring().Update(xcodeDraft, "XcodeBuildMCP", xcode, harness.Discover().Servers);
        var deny = JsonDocument.Parse(harness.Text(@".claude\settings.json"))
            .ValueAt("permissions", "deny")!.Elements!.Select(item => item.StringValue);
        Assert.Equal(["mcp__xcode__build_sim", "mcp__Other__x", "Bash(rm:*)"], deny);
        Assert.True(harness.Server("xcode").ToolFilters[ClientId.ClaudeCode].IsOff("build_sim"));
    }

    [Fact]
    public void ClaudeCodeOneMcpServerToolDenyRulePerToolInSettings()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(Fixture.ClaudeCodeSettings.Text(), @".claude\settings.json");
        var configBefore = harness.Text(".claude.json");

        var server = harness.Server("XcodeBuildMCP");
        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "build_sim" }, ["build_sim", "list_sims"], server, ClientId.ClaudeCode);
        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        Assert.Equal(
            ["mcp__XcodeBuildMCP__build_sim"],
            settings.ValueAt("permissions", "deny")!.Elements!.Select(item => item.StringValue));
        Assert.Equal(2, settings.Root["deniedMcpServers"]!.Elements!.Count);
        Assert.Equal(configBefore, harness.Text(".claude.json"));
        Assert.True(harness.Server("XcodeBuildMCP").ToolFilters[ClientId.ClaudeCode].IsOff("build_sim"));

        harness.ToolSwitches().SetDisabledTools(
            new HashSet<string>(), ["build_sim", "list_sims"], harness.Server("XcodeBuildMCP"), ClientId.ClaudeCode);
        Assert.Empty(JsonDocument.Parse(harness.Text(@".claude\settings.json"))
            .ValueAt("permissions", "deny")!.Elements!);
    }

    [Fact]
    public void AClientWithoutAToolSettingSaysToUseGateway()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        var server = harness.Discover().Servers.First(candidate =>
            candidate.EnabledIn.GetValueOrDefault(ClientId.Cursor) == Enablement.Enabled);
        var refused = Assert.Throws<ToolSwitchException>(() => harness.ToolSwitches().SetDisabledTools(
            new HashSet<string> { "x" }, ["x"], server, ClientId.Cursor));
        Assert.Contains("Cursor has no setting for single tools", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchedOffToolsLeaveTheClientsToolCount()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            Fixture.CodexConfig.Text().Replace(
                "startup_timeout_sec = 20",
                "startup_timeout_sec = 20\ndisabled_tools = [\"delete_branch\"]",
                StringComparison.Ordinal),
            @".codex\config.toml");
        var server = harness.Server("filesystem");
        server.Health = new HealthResult(
            HealthStatus.Passed, DateTimeOffset.UtcNow, 4,
            GithubTools.Select(name => new ToolSummary(name, null)).ToArray(),
            null, null, 0.1, null, null);
        var budget = ToolBudget.Evaluate(ClientId.Codex, null, [server], []);
        Assert.Equal(3, budget.ToolCount);
        Assert.Equal(1, budget.MaskedToolCount);
    }
}
