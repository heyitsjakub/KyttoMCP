using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>
/// Every test here is really the same test — does everything Kytto did not
/// deliberately change come out the other side identical.
/// </summary>
public class TomlRoundTripTests
{
    private static TomlDocument Document() => TomlDocument.Parse(Fixture.CodexConfig.Text());

    /// <summary>The assertion that matters most: everything else is where it was.</summary>
    private static void ExpectUntouchedSurroundings(string updated)
    {
        var reparsed = TomlDocument.Parse(updated);
        Assert.NotNull(reparsed.Table("projects", "/Users/someone/Desktop/a project"));
        Assert.NotNull(reparsed.Table("projects", "/Users/someone/code/Xcode/Thing"));
        Assert.Equal(
            "en_US.UTF-8",
            reparsed.Table("shell_environment_policy", "set")?.Value("LANG")?.StringValue);
        Assert.Equal("save-all", reparsed.Table("history")?.Value("persistence")?.StringValue);
        Assert.Equal("gpt-5-codex", reparsed.Tables.FirstOrDefault()?.Value("model")?.StringValue);
        Assert.Contains("# Codex configuration.", updated, StringComparison.Ordinal);
        Assert.Contains("[not.a.table]", updated, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly one line added, and removing it again gives back the original byte
    /// for byte. A set difference would not catch this: `enabled = false` is
    /// already in the file on another server.
    /// </summary>
    [Fact]
    public void SwitchingOffWritesOneLineAndMovesNothing()
    {
        var document = Document();
        var updated = document.SettingServerFlag(false, "enabled", "filesystem", "mcp_servers");

        var before = document.SourceText.Split('\n');
        var after = updated.Split('\n');
        Assert.Equal(before.Length + 1, after.Length);

        var firstChange = -1;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i]) { firstChange = i; break; }
        }
        Assert.True(firstChange >= 0, "nothing changed");
        // The editor writes the file's own line ending; a Windows checkout of
        // the fixture (core.autocrlf) is CRLF, so the split leaves a trailing \r.
        Assert.Equal("enabled = false", after[firstChange].TrimEnd('\r'));

        var withoutInsertion = after.ToList();
        withoutInsertion.RemoveAt(firstChange);
        Assert.Equal(before, withoutInsertion);

        var reparsed = TomlDocument.Parse(updated);
        Assert.False(reparsed.Table("mcp_servers", "filesystem")?.Value("enabled")?.BoolValue);
        // The new key must land on the server's table, not inside its env.
        Assert.Equal(1, reparsed.Table("mcp_servers", "filesystem", "env")?.Pairs.Count);
        ExpectUntouchedSurroundings(updated);
    }

    [Fact]
    public void SwitchingOnRewritesTheFlagThatIsAlreadyThere()
    {
        var document = Document();
        var updated = document.SettingServerFlag(true, "enabled", "legacy-tool", "mcp_servers");

        Assert.Equal(document.SourceText.Split('\n').Length, updated.Split('\n').Length);
        var reparsed = TomlDocument.Parse(updated);
        Assert.True(reparsed.Table("mcp_servers", "legacy-tool")?.Value("enabled")?.BoolValue);
        ExpectUntouchedSurroundings(updated);
    }

    [Fact]
    public void RemovingAServerTakesItsEnvSubTableWithIt()
    {
        var document = Document();
        var updated = document.RemovingServer("filesystem", "mcp_servers");
        var reparsed = TomlDocument.Parse(updated);

        Assert.Null(reparsed.Table("mcp_servers", "filesystem"));
        Assert.Null(reparsed.Table("mcp_servers", "filesystem", "env"));
        Assert.Equal(["legacy-tool", "docs"], reparsed.ServerNames("mcp_servers"));
        Assert.DoesNotContain("FS_ALLOW_WRITE", updated, StringComparison.Ordinal);
        // No hole where it used to be.
        Assert.DoesNotContain("\n\n\n", updated, StringComparison.Ordinal);
        ExpectUntouchedSurroundings(updated);
    }

    [Fact]
    public void ARemovedServerCanBePutBackExactlyAsItWas()
    {
        var document = Document();
        var parked = document.ServerDefinitionText("filesystem", "mcp_servers");
        Assert.NotNull(parked);

        var without = document.RemovingServer("filesystem", "mcp_servers");
        var restored = TomlDocument.Parse(without)
            .SettingServer("filesystem", "mcp_servers", parked);

        var reparsed = TomlDocument.Parse(restored);
        var table = reparsed.Table("mcp_servers", "filesystem");
        Assert.NotNull(table);
        Assert.Equal("npx", table.Value("command")?.StringValue);
        Assert.NotNull(table.Value("startup_timeout_sec"));
        Assert.Equal(
            "1",
            reparsed.Table("mcp_servers", "filesystem", "env")?.Value("FS_ALLOW_WRITE")?.StringValue);
        ExpectUntouchedSurroundings(restored);
    }

    [Fact]
    public void ANewServerJoinsTheOthersRatherThanLandingAtTheEnd()
    {
        var document = Document();
        var block = TomlBuilder.ServerBlock(
            name: "github",
            key: "mcp_servers",
            command: "npx",
            args: ["-y", "@modelcontextprotocol/server-github"],
            url: null,
            env: [new EnvEntry("GITHUB_TOKEN", "ghp_example")],
            enabled: null);

        var updated = document.SettingServer("github", "mcp_servers", block);
        var reparsed = TomlDocument.Parse(updated);

        Assert.Equal(
            ["filesystem", "legacy-tool", "docs", "github"],
            reparsed.ServerNames("mcp_servers"));
        Assert.Equal(
            "ghp_example",
            reparsed.Table("mcp_servers", "github", "env")?.Value("GITHUB_TOKEN")?.StringValue);
        // Grouped with the servers, so `[history]` is still the last table.
        Assert.Equal(["history"], reparsed.Tables[^1].Path);
        ExpectUntouchedSurroundings(updated);
    }

    [Fact]
    public void AddingToAFileWithNoServersAtAllStillProducesValidToml()
    {
        var document = TomlDocument.Parse("model = \"gpt-5-codex\"\n");
        var block = TomlBuilder.ServerBlock(
            name: "memory",
            key: "mcp_servers",
            command: "npx",
            args: ["-y", "@modelcontextprotocol/server-memory"],
            url: null,
            env: [],
            enabled: null);

        var updated = document.SettingServer("memory", "mcp_servers", block);
        var reparsed = TomlDocument.Parse(updated);

        Assert.Equal(["memory"], reparsed.ServerNames("mcp_servers"));
        Assert.Equal("gpt-5-codex", reparsed.Tables.FirstOrDefault()?.Value("model")?.StringValue);
    }

    [Fact]
    public void ANameThatIsNotABareKeyIsQuoted()
    {
        var block = TomlBuilder.ServerBlock(
            name: "my server.v2",
            key: "mcp_servers",
            command: "run",
            args: [],
            url: null,
            env: [],
            enabled: false);
        Assert.StartsWith("[mcp_servers.\"my server.v2\"]", block, StringComparison.Ordinal);

        var updated = TomlDocument.Parse("").SettingServer("my server.v2", "mcp_servers", block);
        var reparsed = TomlDocument.Parse(updated);
        Assert.Equal(["my server.v2"], reparsed.ServerNames("mcp_servers"));
        Assert.False(reparsed.Table("mcp_servers", "my server.v2")?.Value("enabled")?.BoolValue);
    }

    [Fact]
    public void AnInlineDefinitionIsReadButRefusedForEditing()
    {
        var document = TomlDocument.Parse("""
            [mcp_servers]
            inline = { command = "run", args = ["x"] }
            """);

        Assert.Equal(["inline"], document.ServerNames("mcp_servers"));

        var removing = Assert.Throws<TomlEditException>(() =>
            document.RemovingServer("inline", "mcp_servers"));
        Assert.Equal(TomlEditErrorKind.InlineDefinition, removing.Kind);

        var setting = Assert.Throws<TomlEditException>(() =>
            document.SettingServer("inline", "mcp_servers", "[mcp_servers.inline]"));
        Assert.Equal(TomlEditErrorKind.InlineDefinition, setting.Kind);
    }

    [Fact]
    public void CrlfSurvivesAWrite()
    {
        const string Source =
            "model = \"x\"\r\n\r\n[mcp_servers.a]\r\ncommand = \"run\"\r\n\r\n[history]\r\nkeep = 1\r\n";
        var document = TomlDocument.Parse(Source);
        var updated = document.SettingServerFlag(false, "enabled", "a", "mcp_servers");

        var reparsed = TomlDocument.Parse(updated);
        Assert.False(reparsed.Table("mcp_servers", "a")?.Value("enabled")?.BoolValue);
        Assert.NotNull(reparsed.Table("history")?.Value("keep"));
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void AddingToACrlfFileDoesNotIntroduceMixedLineEndings()
    {
        const string Source = "model = \"x\"\r\n";
        var block = TomlBuilder.ServerBlock(
            "a", "mcp_servers", "run", ["x"], null, [], null);

        var updated = TomlDocument.Parse(Source).SettingServer("a", "mcp_servers", block);

        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal("run", TomlDocument.Parse(updated)
            .Table("mcp_servers", "a")?.Value("command")?.StringValue);
    }
}
