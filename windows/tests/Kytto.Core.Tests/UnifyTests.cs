using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// Unifying rewrites several real config files at once, so these assert the same
/// things the rest of the write pipeline is held to — unrelated content survives,
/// and nothing is switched on or off as a side effect.
/// </summary>
public sealed class UnifyTests
{
    /// <summary>The same server in two clients, spelled differently in each.</summary>
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(
            """
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "env": { "GITHUB_TOKEN": "ghp_current" }
                }
              }
            }
            """,
            @".cursor\mcp.json");
        harness.Write(
            """
            {
              "editorTheme": "dark",
              "mcpServers": {
                "github": {
                  "command": "bunx",
                  "args": ["@modelcontextprotocol/server-github@0.1.0"],
                  "env": { "GITHUB_TOKEN": "ghp_stale" }
                }
              }
            }
            """,
            ".claude.json");
        return harness;
    }

    private static ServerDraft Draft(Server server, ClientId clientId)
    {
        var definition = server.DefinitionsByClient[clientId];
        return new ServerDraft
        {
            Name = server.Name,
            Transport = definition.Transport,
            Command = definition.Command ?? "",
            Args = definition.Args,
            Env = definition.Env,
            Url = definition.Url ?? "",
        };
    }

    [Fact]
    public void TheChosenClientsDefinitionReplacesTheOtherOne()
    {
        using var harness = MakeHarness();

        var github = harness.Server("github");
        var result = harness.Authoring().Unify(
            Draft(github, ClientId.Cursor), github, [ClientId.ClaudeCode]);

        Assert.Equal([ClientId.ClaudeCode], result.Changed);
        Assert.True(result.RequiresRestart);

        var after = harness.Server("github");
        var claude = after.DefinitionsByClient[ClientId.ClaudeCode];
        Assert.Equal("npx", claude.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], claude.Args);
        // And the two copies now agree, which is the only outcome that matters.
        Assert.Null(ServerDrift.Detect(after));
    }

    [Fact]
    public void TheEnvironmentValueTravelsWithoutTheWebLayerEverSeeingIt()
    {
        using var harness = MakeHarness();

        var github = harness.Server("github");
        harness.Authoring().Unify(
            Draft(github, ClientId.Cursor), github, [ClientId.ClaudeCode]);

        var text = harness.Text(".claude.json");
        Assert.Contains("ghp_current", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_stale", text, StringComparison.Ordinal);
    }

    [Fact]
    public void KeysTheTargetFileOwnsAndKyttoDoesNotAreLeftAlone()
    {
        using var harness = MakeHarness();

        var github = harness.Server("github");
        harness.Authoring().Unify(
            Draft(github, ClientId.Cursor), github, [ClientId.ClaudeCode]);

        Assert.Contains("\"editorTheme\": \"dark\"", harness.Text(".claude.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSourceClientsOwnFileIsNotRewritten()
    {
        using var harness = MakeHarness();

        var before = harness.Text(@".cursor\mcp.json");
        var github = harness.Server("github");
        harness.Authoring().Unify(
            Draft(github, ClientId.Cursor), github, [ClientId.ClaudeCode]);

        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
    }

    /// <summary>
    /// The trap this whole method exists around. In a presence-only client, a
    /// switched-off server has been <em>removed</em> from the file and its bytes are
    /// in Kytto's park store. Writing a definition into that file is exactly what
    /// switching a server on means — so a careless unify would turn it back on.
    /// </summary>
    [Fact]
    public void ASwitchedOffServerIsBroughtIntoLineWithoutBeingSwitchedOn()
    {
        using var harness = MakeHarness();

        // Switch it off in Cursor first: that parks the definition.
        var initial = harness.Server("github");
        Assert.True(harness.Toggles.SetEnabled(false, initial, ClientId.Cursor).WasParked);

        // Now unify from Claude Code, which still has its own spelling.
        var github = harness.Server("github");
        Assert.Equal(Enablement.Disabled, github.EnabledIn[ClientId.Cursor]);
        var result = harness.Authoring().Unify(
            Draft(github, ClientId.ClaudeCode), github, [ClientId.Cursor]);

        // Nothing was written to Cursor's file, so nothing asks for a restart.
        Assert.Empty(result.Changed);
        Assert.Equal([ClientId.Cursor], result.ParkedUpdated);
        Assert.Empty(result.ParkedFailures);

        // Still off — the definition did not reappear in the file.
        var after = harness.Server("github");
        Assert.Equal(Enablement.Disabled, after.EnabledIn[ClientId.Cursor]);
        Assert.DoesNotContain("bunx", harness.Text(@".cursor\mcp.json"), StringComparison.Ordinal);

        // And switching it back on restores the unified definition, not the old one.
        harness.Toggles.SetEnabled(true, after, ClientId.Cursor);
        var revived = harness.Server("github");
        Assert.Equal(Enablement.Enabled, revived.EnabledIn[ClientId.Cursor]);
        Assert.Equal("bunx", revived.DefinitionsByClient[ClientId.Cursor].Command);
        Assert.Null(ServerDrift.Detect(revived));
    }

    [Fact]
    public void AnInstalledBundleIsNeverWrittenTo()
    {
        using var harness = MakeHarness();

        var github = harness.Server("github");
        github.DefinitionsByClient[ClientId.ClaudeDesktop] = new ClientDefinition(
            Transport.Stdio,
            "node",
            ["${__dirname}/index.js"],
            [],
            null,
            new Origin.ClaudeDesktopExtension("com.example.github"),
            IsBundled: true,
            "{}");

        var error = Assert.Throws<AuthoringException>(() => harness.Authoring().Unify(
            Draft(github, ClientId.Cursor), github, [ClientId.ClaudeDesktop]));
        Assert.Contains("extension", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACodexServerKeepsItsOffSwitchWhenItsDefinitionIsReplaced()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            """{"mcpServers": {"github": {"command": "npx", "args": ["-y", "pkg"]}}}""",
            @".cursor\mcp.json");
        harness.Write(
            """
            [mcp_servers.github]
            command = "bunx"
            enabled = false
            """,
            @".codex\config.toml");

        var github = harness.Server("github");
        Assert.Equal(Enablement.Disabled, github.EnabledIn[ClientId.Codex]);

        harness.Authoring().Unify(Draft(github, ClientId.Cursor), github, [ClientId.Codex]);

        var after = harness.Server("github");
        Assert.Equal("npx", after.DefinitionsByClient[ClientId.Codex].Command);
        // Replacing the block must not quietly turn the server back on.
        Assert.Equal(Enablement.Disabled, after.EnabledIn[ClientId.Codex]);
    }
}
