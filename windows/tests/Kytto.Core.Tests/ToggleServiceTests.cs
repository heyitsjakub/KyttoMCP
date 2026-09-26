using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

public class DenyListToggleTests
{
    private sealed class FailSecondWrite : IConfigFileWriter
    {
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 2) throw new IOException("Injected deny-list failure.");
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class ChangeDefinitionExternallyThenFailDenyList : IConfigFileWriter
    {
        private string? _definitionPath;
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 1)
            {
                _definitionPath = path;
                AtomicWriter.Write(data, path);
                return;
            }
            if (_writes == 2)
            {
                AtomicWriter.Write("external-newer-change", _definitionPath!);
                throw new IOException("Injected deny-list failure after an external edit.");
            }
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(Fixture.ClaudeCodeSettings.Text(), @".claude\settings.json");
        return harness;
    }

    [Fact]
    public void DisablingAddsADenyEntryAndLeavesTheServerConfigured()
    {
        using var harness = MakeHarness();

        var context7 = harness.Server("context7");
        var configBefore = harness.Text(".claude.json");

        var result = harness.Toggles.SetEnabled(false, context7, ClientId.ClaudeCode);
        Assert.True(result.RequiresRestart);
        Assert.False(result.WasParked);

        // The server itself is untouched — this is the whole point of a deny list.
        Assert.Equal(configBefore, harness.Text(".claude.json"));

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        var denied = settings.Root["deniedMcpServers"]?.Elements;
        Assert.NotNull(denied);
        Assert.Contains(
            "context7",
            denied.Select(entry => entry["serverName"]?.StringValue),
            StringComparer.Ordinal);

        Assert.Equal(
            Enablement.Disabled,
            harness.Server("context7").EnabledIn[ClientId.ClaudeCode]);
    }

    [Fact]
    public void EnablingRemovesTheDenyEntryAndLeavesTheOthers()
    {
        using var harness = MakeHarness();

        var xcode = harness.Server("XcodeBuildMCP");
        Assert.Equal(Enablement.Disabled, xcode.EnabledIn[ClientId.ClaudeCode]);

        harness.Toggles.SetEnabled(true, xcode, ClientId.ClaudeCode);

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        var names = settings.Root["deniedMcpServers"]?.Elements?
            .Select(entry => entry["serverName"]?.StringValue).ToArray();
        Assert.Equal<IEnumerable<string?>>(["claude-in-chrome"], names!);
        Assert.Equal(
            Enablement.Enabled,
            harness.Server("XcodeBuildMCP").EnabledIn[ClientId.ClaudeCode]);
    }

    [Fact]
    public void UnrelatedSettingsKeysSurvive()
    {
        using var harness = MakeHarness();

        var before = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);
        var after = JsonDocument.Parse(harness.Text(@".claude\settings.json"));

        Assert.Equal(before.Root.Keys, after.Root.Keys);
        Assert.Equal("opus", after.ValueAt("model")?.StringValue);
        Assert.True(after.ValueAt("enabledPlugins", "swift-lsp@claude-plugins-official")?.BoolValue);
    }

    [Fact]
    public void DisablingTwiceDoesNotAddADuplicate()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);
        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        var names = settings.Root["deniedMcpServers"]?.Elements?
            .Select(entry => entry["serverName"]?.StringValue).ToArray();
        Assert.Single(names!, name => name == "context7");
    }

    [Fact]
    public void TheDenyListIsCreatedWhenTheSettingsFileHasNone()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write("""{"model": "opus"}""", @".claude\settings.json");

        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        Assert.Equal(1, settings.Root["deniedMcpServers"]?.Elements?.Count);
        Assert.Equal("opus", settings.Root["model"]?.StringValue);
    }

    // MARK: - Arriving from another client
    //
    // Taking a name out of a deny list permits a definition that has to exist for
    // the permission to mean anything. These are the tests that keep the
    // config-file edit in front of the deny-list edit: without it the click
    // rewrites nothing, the cell comes back empty, and the result still says it
    // worked.

    private static ToggleHarness MakeCrossClientHarness()
    {
        var harness = MakeHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        return harness;
    }

    [Fact]
    public void SwitchingOnAServerThisClientDoesNotHaveWritesTheDefinition()
    {
        using var harness = MakeCrossClientHarness();

        var github = harness.Server("github");
        Assert.Equal(
            Enablement.Absent,
            github.EnabledIn.GetValueOrDefault(ClientId.ClaudeCode, Enablement.Absent));

        var result = harness.Toggles.SetEnabled(true, github, ClientId.ClaudeCode);
        // The file worth naming is the one the definition landed in.
        Assert.EndsWith(".claude.json", result.PathDisplay, StringComparison.Ordinal);

        var config = JsonDocument.Parse(harness.Text(".claude.json"));
        var arrived = config.ValueAt("mcpServers", "github");
        Assert.NotNull(arrived);
        Assert.Equal("npx", arrived["command"]?.StringValue);
        // Copied between two JSON clients, so it moves as the bytes it already was
        // — including the key Kytto's model has no field for.
        Assert.Equal(
            "ghp_exampletokenvalue",
            arrived["env"]?["GITHUB_PERSONAL_ACCESS_TOKEN"]?.StringValue);

        Assert.Equal(Enablement.Enabled, harness.Server("github").EnabledIn[ClientId.ClaudeCode]);
    }

    [Fact]
    public void FirstClaudeCodeConfigArrivalIsAWriteWithoutABackup()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write("""{"deniedMcpServers": []}""", @".claude\settings.json");

        var result = harness.Toggles.SetEnabled(
            true, harness.Server("github"), ClientId.ClaudeCode);

        Assert.True(result.RequiresRestart);
        Assert.Null(result.BackupID);
        Assert.NotNull(JsonDocument.Parse(harness.Text(".claude.json"))
            .ValueAt("mcpServers", "github"));
    }

    [Fact]
    public void ExternalDenyListDriftCannotLeaveAnArrivingDefinitionBehind()
    {
        using var harness = MakeCrossClientHarness();
        var github = harness.Server("github");
        var path = Path.Combine(harness.Home, ".claude.json");
        var before = File.ReadAllBytes(path);
        harness.Write("""{"deniedMcpServers": [{"serverName": "github"}]}""",
            @".claude\settings.json");

        Assert.Throws<ConfigWriteException>(() =>
            harness.Toggles.SetEnabled(true, github, ClientId.ClaudeCode));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void FailedDenyListWriteRestoresAnExistingDefinitionFileByteForByte()
    {
        using var harness = MakeCrossClientHarness();
        harness.Write("""{"deniedMcpServers": [{"serverName": "github"}]}""",
            @".claude\settings.json");
        var github = harness.Server("github");
        var path = Path.Combine(harness.Home, ".claude.json");
        var before = File.ReadAllBytes(path);
        var service = new ToggleService(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailSecondWrite()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<IOException>(() =>
            service.SetEnabled(true, github, ClientId.ClaudeCode));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(Digest.Of(before), harness.Ledger.DigestFor(path));
    }

    [Fact]
    public void FailedDenyListWriteRemovesANewDefinitionFile()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write("""{"deniedMcpServers": [{"serverName": "github"}]}""",
            @".claude\settings.json");
        var github = harness.Server("github");
        var path = Path.Combine(harness.Home, ".claude.json");
        var service = new ToggleService(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailSecondWrite()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<IOException>(() =>
            service.SetEnabled(true, github, ClientId.ClaudeCode));

        Assert.False(File.Exists(path));
        Assert.Null(harness.Ledger.DigestFor(path));
    }

    [Fact]
    public void RollbackRefusesToOverwriteANewerDefinitionAndSurfacesBothFailures()
    {
        using var harness = MakeCrossClientHarness();
        harness.Write("""{"deniedMcpServers": [{"serverName": "github"}]}""",
            @".claude\settings.json");
        var github = harness.Server("github");
        var path = Path.Combine(harness.Home, ".claude.json");
        var service = new ToggleService(
            harness.Home,
            new ConfigWriter(harness.Backups, new ChangeDefinitionExternallyThenFailDenyList()),
            harness.ParkStore,
            harness.Ledger);

        var error = Assert.Throws<ConfigTransactionException>(() =>
            service.SetEnabled(true, github, ClientId.ClaudeCode));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("rollback", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deny-list failure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-newer-change", File.ReadAllText(path));
    }

    [Fact]
    public void TheServersAlreadyThereAreUntouchedByAnArrival()
    {
        using var harness = MakeCrossClientHarness();

        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.ClaudeCode);

        var config = JsonDocument.Parse(harness.Text(".claude.json"));
        Assert.Equal("npx", config.ValueAt("mcpServers", "XcodeBuildMCP", "command")?.StringValue);
        Assert.Equal(
            "https://mcp.context7.com/mcp",
            config.ValueAt("mcpServers", "context7", "url")?.StringValue);
        // Everything this file holds that has nothing to do with MCP.
        Assert.Equal(560, config.Root["numStartups"]?.NumberValue);
        Assert.Equal(3, config.Root["migrationVersion"]?.NumberValue);
        Assert.NotNull(config.ValueAt(
            "projects",
            "/Users/example/code/Xcode/KyttoMCP",
            "mcpServers",
            "project-only-server"));
    }

    /// <summary>The bug §4 names outright: a TOML table spliced into a JSON file.</summary>
    [Fact]
    public void ADefinitionFromCodexIsRenderedNeverSplicedAcross()
    {
        using var harness = MakeCrossClientHarness();

        var filesystem = harness.Server("filesystem");
        Assert.Equal(Enablement.Enabled, filesystem.EnabledIn[ClientId.Codex]);
        Assert.Equal(
            Enablement.Absent,
            filesystem.EnabledIn.GetValueOrDefault(ClientId.ClaudeCode, Enablement.Absent));

        harness.Toggles.SetEnabled(true, filesystem, ClientId.ClaudeCode);

        var text = harness.Text(".claude.json");
        Assert.DoesNotContain("[mcp_servers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("startup_timeout_sec = ", text, StringComparison.Ordinal);

        var config = JsonDocument.Parse(text);
        var arrived = config.ValueAt("mcpServers", "filesystem");
        Assert.NotNull(arrived);
        Assert.Equal("npx", arrived["command"]?.StringValue);
        Assert.Equal("-y", arrived["args"]?.Elements?.FirstOrDefault()?.StringValue);
        Assert.Equal(Enablement.Enabled, harness.Server("filesystem").EnabledIn[ClientId.ClaudeCode]);
    }

    [Fact]
    public void AServerTheClientAlreadyListsIsPermittedNotRewritten()
    {
        using var harness = MakeCrossClientHarness();

        var before = harness.Text(".claude.json");
        var xcode = harness.Server("XcodeBuildMCP");
        Assert.Equal(Enablement.Disabled, xcode.EnabledIn[ClientId.ClaudeCode]);

        harness.Toggles.SetEnabled(true, xcode, ClientId.ClaudeCode);

        Assert.Equal(before, harness.Text(".claude.json"));
        Assert.Equal(
            Enablement.Enabled,
            harness.Server("XcodeBuildMCP").EnabledIn[ClientId.ClaudeCode]);
    }

    /// <summary>
    /// Off and on again is the sequence a user runs by accident, and it must not
    /// leave a duplicate, a reformat, or a definition rebuilt from the model.
    /// </summary>
    [Fact]
    public void OffThenOnReturnsTheFileByteForByte()
    {
        using var harness = MakeCrossClientHarness();

        var before = harness.Text(".claude.json");
        harness.Toggles.SetEnabled(false, harness.Server("context7"), ClientId.ClaudeCode);
        Assert.Equal(before, harness.Text(".claude.json"));

        harness.Toggles.SetEnabled(true, harness.Server("context7"), ClientId.ClaudeCode);
        Assert.Equal(before, harness.Text(".claude.json"));
        Assert.Equal(Enablement.Enabled, harness.Server("context7").EnabledIn[ClientId.ClaudeCode]);
    }

    /// <summary>
    /// An extension is installed software. It was refused for presence clients
    /// already; the deny-list path must refuse it for the same reason instead of
    /// quietly permitting a name that has no definition to permit.
    /// </summary>
    [Fact]
    public void AnExtensionIsStillRefused()
    {
        using var harness = MakeCrossClientHarness();
        harness.Write(
            Fixture.ExtensionManifest.Text(),
            $@"{ToggleHarness.ClaudeSupport}\Claude Extensions\ant.dir.test.osascript\manifest.json");

        var bundled = harness.Discover().Servers.FirstOrDefault(server => server.IsBundled);
        Assert.NotNull(bundled);

        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, bundled, ClientId.ClaudeCode));
        Assert.Contains("cannot be copied", error.Message, StringComparison.Ordinal);
    }
}

public class ExtensionToggleTests
{
    private const string SettingsFile =
        $@"{ToggleHarness.ClaudeSupport}\Claude Extensions Settings\ant.dir.test.osascript.json";

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(
            Fixture.ExtensionManifest.Text(),
            $@"{ToggleHarness.ClaudeSupport}\Claude Extensions\ant.dir.test.osascript\manifest.json");
        harness.Write(Fixture.ExtensionSettingsDisabled.Text(), SettingsFile);
        return harness;
    }

    [Fact]
    public void FlippingIsEnabledKeepsUserConfigIntact()
    {
        using var harness = MakeHarness();

        var server = harness.Server("Control your Mac");
        Assert.Equal(Enablement.Disabled, server.EnabledIn[ClientId.ClaudeDesktop]);

        harness.Toggles.SetEnabled(true, server, ClientId.ClaudeDesktop);

        var settings = JsonDocument.Parse(harness.Text(SettingsFile));
        Assert.True(settings.Root["isEnabled"]?.BoolValue);
        Assert.Equal(1, settings.ValueAt("userConfig", "allowed_directories")?.Elements?.Count);

        Assert.Equal(
            Enablement.Enabled,
            harness.Server("Control your Mac").EnabledIn[ClientId.ClaudeDesktop]);
    }

    /// <summary>No settings file means never toggled, which Claude Desktop treats as on.</summary>
    [Fact]
    public void AnExtensionThatHasNeverBeenToggledGetsASettingsFile()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            Fixture.ExtensionManifest.Text(),
            $@"{ToggleHarness.ClaudeSupport}\Claude Extensions\ant.dir.test.osascript\manifest.json");

        var server = harness.Server("Control your Mac");
        Assert.Equal(Enablement.Enabled, server.EnabledIn[ClientId.ClaudeDesktop]);

        harness.Toggles.SetEnabled(false, server, ClientId.ClaudeDesktop);
        Assert.Equal(
            Enablement.Disabled,
            harness.Server("Control your Mac").EnabledIn[ClientId.ClaudeDesktop]);
    }

    [Fact]
    public void AnExtensionCannotBeCopiedIntoAnotherClient()
    {
        using var harness = MakeHarness();
        harness.Write("""{"mcpServers": {}}""", @".cursor\mcp.json");

        var server = harness.Server("Control your Mac");
        var error = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, server, ClientId.Cursor));
        Assert.Contains("cannot be copied", error.Message, StringComparison.Ordinal);
    }
}

public class PresenceToggleTests
{
    private sealed class FailWrite : IConfigFileWriter
    {
        public void Write(byte[] data, string path) => throw new IOException("Injected failure.");

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class ReplaceWithExternalChangeThenFail : IConfigFileWriter
    {
        public void Write(byte[] data, string path)
        {
            AtomicWriter.Write(data, path);
            AtomicWriter.Write("external-newer-change", path);
            throw new IOException("Injected failure after an external edit.");
        }

        public void Delete(string path) => File.Delete(path);
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }

    [Fact]
    public void SwitchingOffParksTheDefinitionAndRemovesIt()
    {
        using var harness = MakeHarness();

        var figma = harness.Server("figma");
        var result = harness.Toggles.SetEnabled(false, figma, ClientId.Cursor);
        Assert.True(result.WasParked);

        var config = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal(["github"], config.ValueAt("mcpServers")?.Keys);

        var parked = harness.ParkStore.Parked(ClientId.Cursor, "figma");
        Assert.NotNull(parked);
        Assert.Contains("mcp.figma.com", parked.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void OffThenOnRestoresTheFileByteForByte()
    {
        using var harness = MakeHarness();

        var before = harness.Text(@".cursor\mcp.json");
        harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);
        Assert.NotEqual(before, harness.Text(@".cursor\mcp.json"));

        harness.Toggles.SetEnabled(true, harness.Server("figma"), ClientId.Cursor);

        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
        Assert.Null(harness.ParkStore.Parked(ClientId.Cursor, "figma"));
    }

    /// <summary>
    /// Parked servers must not vanish from the matrix — the row is the only way
    /// back to switching one on again.
    /// </summary>
    [Fact]
    public void AParkedServerIsStillListedShownAsDisabled()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);

        var figma = harness.Server("figma");
        Assert.Equal(Enablement.Disabled, figma.EnabledIn[ClientId.Cursor]);
        Assert.Equal("https://mcp.figma.com/sse", figma.Url);
        Assert.Equal(["figma"], harness.ParkStore.All().Select(entry => entry.ServerName));
    }

    [Fact]
    public void SwitchingOnAServerTheClientDoesNotHaveCopiesItAcross()
    {
        using var harness = MakeHarness();

        // VS Code is installed but has never configured MCP.
        var github = harness.Server("github");
        Assert.Equal(Enablement.Absent, github.EnabledIn[ClientId.VsCode]);

        harness.Toggles.SetEnabled(true, github, ClientId.VsCode);

        var vscode = JsonDocument.Parse(harness.Text($@"{ToggleHarness.CodeUser}\mcp.json"));
        // VS Code keys servers under `servers`, not `mcpServers`.
        Assert.Equal("npx", vscode.ValueAt("servers", "github", "command")?.StringValue);
        Assert.Equal(Enablement.Enabled, harness.Server("github").EnabledIn[ClientId.VsCode]);
    }

    [Fact]
    public void UnrelatedKeysAndFormattingSurviveAToggle()
    {
        using var harness = new ToggleHarness();
        harness.Write(
            Fixture.ClaudeDesktopWithServers.Text(),
            $@"{ToggleHarness.ClaudeSupport}\claude_desktop_config.json");

        harness.Toggles.SetEnabled(false, harness.Server("postgres"), ClientId.ClaudeDesktop);

        var path = $@"{ToggleHarness.ClaudeSupport}\claude_desktop_config.json";
        var after = JsonDocument.Parse(harness.Text(path));
        Assert.Equal(["coworkUserFilesPath", "mcpServers", "preferences"], after.Root.Keys);
        Assert.True(after.ValueAt("preferences", "chromeExtensionEnabled")?.BoolValue);
        Assert.Equal(["github", "remote-docs"], after.ValueAt("mcpServers")?.Keys);
        // The file used tabs; it still does.
        Assert.Contains("\n\t\"mcpServers\"", harness.Text(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedConfigWriteDoesNotLeaveAFalseParkedState()
    {
        using var harness = MakeHarness();
        var before = harness.Text(@".cursor\mcp.json");
        var service = new ToggleService(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailWrite()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<IOException>(() =>
            service.SetEnabled(false, harness.Server("figma"), ClientId.Cursor));

        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
        Assert.Null(harness.ParkStore.Parked(ClientId.Cursor, "figma"));
        Assert.Equal(Enablement.Enabled, harness.Server("figma").EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void AnUnrestorableExternalChangeKeepsTheParkedDefinitionSafe()
    {
        using var harness = MakeHarness();
        var service = new ToggleService(
            harness.Home,
            new ConfigWriter(harness.Backups, new ReplaceWithExternalChangeThenFail()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<ConfigTransactionException>(() =>
            service.SetEnabled(false, harness.Server("figma"), ClientId.Cursor));

        Assert.Equal("external-newer-change", harness.Text(@".cursor\mcp.json"));
        var parked = harness.ParkStore.Parked(ClientId.Cursor, "figma");
        Assert.NotNull(parked);
        Assert.Contains("mcp.figma.com", parked.SourceText, StringComparison.Ordinal);
    }
}

public class ToggleSafetyTests
{
    private sealed class SlowWrite : IConfigFileWriter
    {
        public void Write(byte[] data, string path)
        {
            Thread.Sleep(200);
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }

    [Fact]
    public void ABackupIsTakenBeforeEveryWrite()
    {
        using var harness = MakeHarness();

        var before = harness.Text(@".cursor\mcp.json");
        var result = harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);

        Assert.NotNull(result.BackupID);
        var backup = harness.Backups.Get(result.BackupID, ClientId.Cursor);
        Assert.NotNull(backup);
        Assert.Equal(before, File.ReadAllText(backup.Path));
    }

    [Fact]
    public void RestoringABackupPutsTheOriginalFileBack()
    {
        using var harness = MakeHarness();

        var before = harness.Text(@".cursor\mcp.json");
        var result = harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);
        Assert.NotEqual(before, harness.Text(@".cursor\mcp.json"));

        var backup = harness.Backups.Get(result.BackupID!, ClientId.Cursor);
        Assert.NotNull(backup);
        harness.Backups.Restore(backup, harness.Resolver());

        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void RevertingIsItselfReversible()
    {
        using var harness = MakeHarness();

        var result = harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);
        var backup = harness.Backups.Get(result.BackupID!, ClientId.Cursor);
        Assert.NotNull(backup);
        harness.Backups.Restore(backup, harness.Resolver());

        Assert.Equal(2, harness.Backups.List(ClientId.Cursor).Count);
    }

    [Fact]
    public void RetentionKeepsTheLastTwentyPerClient()
    {
        using var harness = MakeHarness();

        var path = Path.Combine(harness.Home, ".cursor", "mcp.json");
        for (var index = 0; index < 25; index++)
        {
            harness.Backups.BackUp(path, ClientId.Cursor, "%USERPROFILE%\\.cursor\\mcp.json");
            // Distinct timestamps; the stamp has millisecond resolution.
            if (index % 5 == 0) Thread.Sleep(2);
        }

        Assert.Equal(
            BackupStore.DefaultRetentionPerClient,
            harness.Backups.List(ClientId.Cursor).Count);
    }

    [Fact]
    public void ConcurrentParksNeverLoseAnotherServersOnlyCopy()
    {
        using var harness = MakeHarness();
        var store = new ParkStore(new Kytto.Core.Settings.KyttoPaths(
            Path.Combine(harness.Home, "concurrent-park")));

        Parallel.For(0, 30, index =>
            store.Park(ClientId.Cursor, $"server-{index}", $"{{ \"command\": \"run-{index}\" }}"));

        var parked = store.All();
        Assert.Equal(30, parked.Count);
        Assert.Equal(30, parked.Select(entry => entry.ServerName).Distinct().Count());
    }

    [Fact]
    public void ACorruptParkStoreIsNeverOverwrittenAsThoughItWereEmpty()
    {
        using var harness = MakeHarness();
        var paths = new Kytto.Core.Settings.KyttoPaths(Path.Combine(harness.Home, "corrupt-park"));
        Directory.CreateDirectory(paths.Root);
        const string Corrupt = "{ definitely not valid json";
        File.WriteAllText(paths.ParkFile, Corrupt);
        var store = new ParkStore(paths);

        Assert.Throws<InvalidDataException>(() =>
            store.Park(ClientId.Cursor, "only-copy", "{ \"command\": \"run\" }"));

        Assert.Equal(Corrupt, File.ReadAllText(paths.ParkFile));
    }

    [Fact]
    public async Task ConcurrentEditsOfOneConfigCannotSilentlyOverwriteEachOther()
    {
        using var harness = MakeHarness();
        _ = harness.Discover();
        var path = Path.Combine(harness.Home, ".cursor", "mcp.json");
        var writer = new ConfigWriter(harness.Backups, new SlowWrite());
        using var preparedTogether = new Barrier(2);

        Task<Exception?> Edit(string name) => Task.Run(() =>
        {
            try
            {
                writer.Edit<JsonDocument>(
                    path,
                    ClientId.Cursor,
                    "%USERPROFILE%\\.cursor\\mcp.json",
                    harness.Ledger.DigestFor(path),
                    document =>
                    {
                        preparedTogether.SignalAndWait();
                        return document.SettingMember(
                            name,
                            ["mcpServers"],
                            "{ \"command\": \"run\" }");
                    });
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        });

        var outcomes = await Task.WhenAll(Edit("race-a"), Edit("race-b"));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is ConfigWriteException);
        var config = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal(1, new[] { "race-a", "race-b" }
            .Count(name => config.ValueAt("mcpServers", name) is not null));
    }

    [Fact]
    public void ConcurrentBackupsNeverOverwriteOneAnother()
    {
        using var harness = MakeHarness();
        var path = Path.Combine(harness.Home, ".cursor", "mcp.json");
        var store = new BackupStore(new Kytto.Core.Settings.KyttoPaths(
            Path.Combine(harness.Home, "concurrent-backups")), retentionPerClient: 100);
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 30, _ =>
        {
            var backup = store.BackUp(path, ClientId.Cursor, "%USERPROFILE%\\.cursor\\mcp.json");
            ids.Add(Assert.IsType<Backup>(backup).Id);
        });

        Assert.Equal(30, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, store.List(ClientId.Cursor).Count);
    }

    [Fact]
    public void AFileChangedBehindKyttosBackIsNotWrittenOver()
    {
        using var harness = MakeHarness();

        var figma = harness.Server("figma"); // primes the ledger
        // Something else edits the file — the user, or the client app itself.
        harness.Write("""{"mcpServers": {"typed-by-hand": {"command": "x"}}}""", @".cursor\mcp.json");

        var error = Assert.Throws<ConfigWriteException>(() =>
            harness.Toggles.SetEnabled(false, figma, ClientId.Cursor));
        Assert.Contains("changed on disk", error.Message, StringComparison.Ordinal);

        // And the hand-written content is still there, unmerged.
        Assert.Contains("typed-by-hand", harness.Text(@".cursor\mcp.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void AToggleThatChangesNothingWritesNothing()
    {
        using var harness = MakeHarness();

        var github = harness.Server("github");
        var result = harness.Toggles.SetEnabled(true, github, ClientId.Cursor);

        Assert.Null(result.BackupID);
        Assert.False(result.RequiresRestart);
        Assert.Empty(harness.Backups.List(ClientId.Cursor));
    }

    [Fact]
    public void TheWrittenFileIsAlwaysValidJson()
    {
        using var harness = MakeHarness();

        foreach (var name in new[] { "github", "figma" })
        {
            harness.Toggles.SetEnabled(false, harness.Server(name), ClientId.Cursor);
            _ = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        }

        var final = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Empty(final.ValueAt("mcpServers")!.Keys);
    }
}
