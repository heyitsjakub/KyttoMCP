using System.Text.RegularExpressions;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

public static class AuthoringHarnessExtensions
{
    public static ServerAuthoring Authoring(this ToggleHarness harness) =>
        new(harness.Home,
            new ConfigWriter(harness.Backups),
            harness.ParkStore,
            harness.Ledger);
}

public class CreateServerTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        return harness;
    }

    private static readonly ServerDraft Memory = new()
    {
        Name = "memory",
        Transport = Transport.Stdio,
        Command = "npx",
        Args = ["-y", "@modelcontextprotocol/server-memory"],
    };

    [Fact]
    public void CreatingWritesTheDefinitionIntoEveryChosenClient()
    {
        using var harness = MakeHarness();
        var existing = harness.Discover().Servers;

        var result = harness.Authoring().Create(
            Memory, [ClientId.Cursor, ClientId.Codex], existing);

        Assert.Equal("memory", result.ServerName);
        Assert.True(result.RequiresRestart);
        Assert.Equal([ClientId.Cursor, ClientId.Codex], result.Changed);

        var cursor = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal("npx", cursor.ValueAt("mcpServers", "memory", "command")?.StringValue);

        var codex = TomlDocument.Parse(harness.Text(@".codex\config.toml"));
        Assert.Equal("npx", codex.Table("mcp_servers", "memory")?.Value("command")?.StringValue);
    }

    /// <summary>
    /// The bug §4 names outright, from the other direction: each client gets its
    /// own spelling, never the other's text.
    /// </summary>
    [Fact]
    public void EachClientGetsItsOwnFormat()
    {
        using var harness = MakeHarness();
        harness.Authoring().Create(Memory, [ClientId.Cursor, ClientId.Codex], harness.Discover().Servers);

        Assert.DoesNotContain("[mcp_servers", harness.Text(@".cursor\mcp.json"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"mcpServers\"", harness.Text(@".codex\config.toml"), StringComparison.Ordinal);
    }

    [Fact]
    public void CreatingInAClientWithNoConfigFileYet()
    {
        using var harness = MakeHarness();

        // VS Code is installed here but has never configured MCP.
        var result = harness.Authoring().Create(
            Memory, [ClientId.VsCode], harness.Discover().Servers);

        var vscode = JsonDocument.Parse(harness.Text($@"{ToggleHarness.CodeUser}\mcp.json"));
        // VS Code keys servers under `servers`, not `mcpServers`.
        Assert.Equal("npx", vscode.ValueAt("servers", "memory", "command")?.StringValue);
        Assert.Equal([ClientId.VsCode], result.Changed);
        Assert.Empty(result.BackupIDs);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public void CreatingTheFirstTomlConfigIsAChangeWithoutABackup()
    {
        using var harness = new ToggleHarness();

        var result = harness.Authoring().Create(
            Memory, [ClientId.Codex], harness.Discover().Servers);

        var codex = TomlDocument.Parse(harness.Text(@".codex\config.toml"));
        Assert.Equal("npx", codex.Table("mcp_servers", "memory")?.Value("command")?.StringValue);
        Assert.Equal([ClientId.Codex], result.Changed);
        Assert.Empty(result.BackupIDs);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public void ANameAlreadyTakenInAChosenClientIsRefused()
    {
        using var harness = MakeHarness();
        var existing = harness.Discover().Servers;
        var clash = Memory with { Name = "github" };

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Create(clash, [ClientId.Cursor], existing));
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidDraftIsRefusedBeforeAnythingIsOpened()
    {
        using var harness = MakeHarness();
        var before = harness.Text(@".cursor\mcp.json");

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Create(
                Memory with { Command = "" }, [ClientId.Cursor], harness.Discover().Servers));
        Assert.Contains("needs a command", error.Message, StringComparison.Ordinal);

        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
        Assert.Empty(harness.Backups.List(ClientId.Cursor));
    }

    [Fact]
    public void CreatingWithoutAClientIsRefusedBeforeAnythingIsWritten()
    {
        using var harness = MakeHarness();
        var before = harness.Text(@".cursor\mcp.json");

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Create(Memory, [], harness.Discover().Servers));

        Assert.Equal("Choose at least one client to add this server to.", error.Message);
        Assert.Equal(before, harness.Text(@".cursor\mcp.json"));
        Assert.Empty(harness.Backups.List(ClientId.Cursor));
    }

    [Fact]
    public void ARemoteServerIsWrittenWithItsTypeAndUrl()
    {
        using var harness = MakeHarness();
        var remote = new ServerDraft
        {
            Name = "docs",
            Transport = Transport.Http,
            Url = "https://example.com/mcp",
        };

        harness.Authoring().Create(remote, [ClientId.Cursor], harness.Discover().Servers);

        var cursor = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal("http", cursor.ValueAt("mcpServers", "docs", "type")?.StringValue);
        Assert.Equal("https://example.com/mcp", cursor.ValueAt("mcpServers", "docs", "url")?.StringValue);
        // A remote server has no command to run.
        Assert.Null(cursor.ValueAt("mcpServers", "docs", "command"));
    }

    [Fact]
    public void CreatingInACrlfConfigDoesNotIntroduceMixedLineEndings()
    {
        using var harness = new ToggleHarness();
        harness.Write("{\r\n  \"mcpServers\": {},\r\n  \"other\": true\r\n}", @".cursor\mcp.json");

        harness.Authoring().Create(Memory, [ClientId.Cursor], harness.Discover().Servers);

        var updated = harness.Text(@".cursor\mcp.json");
        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal("npx", JsonDocument.Parse(updated)
            .ValueAt("mcpServers", "memory", "command")?.StringValue);
    }
}

public class ServerDraftValidationTests
{
    private static ServerDraft Remote(string url) => new()
    {
        Name = "remote",
        Transport = Transport.Http,
        Url = url,
    };

    [Theory]
    [InlineData("http://example.com/mcp")]
    [InlineData("HTTPS://EXAMPLE.COM/mcp")]
    public void HttpAndHttpsUrlsWithAHostAreAccepted(string url) =>
        Assert.DoesNotContain(Remote(url).Validate(), problem =>
            problem.Error is ServerDraft.ValidationError.UrlEmpty or ServerDraft.ValidationError.UrlInvalid);

    [Theory]
    [InlineData("httpx://example.com")]
    [InlineData("https:")]
    [InlineData("http:///mcp")]
    [InlineData("/relative")]
    [InlineData("ftp://example.com")]
    public void UnsafeOrIncompleteRemoteUrlsAreRejected(string url) =>
        Assert.Contains(Remote(url).Validate(), problem =>
            problem.Error == ServerDraft.ValidationError.UrlInvalid);

    [Theory]
    [InlineData("API_KEY")]
    [InlineData("_TOKEN")]
    [InlineData("A1")]
    public void PortableEnvironmentNamesAreAccepted(string key)
    {
        var draft = new ServerDraft { Name = "local", Command = "npx", Env = [new EnvEntry(key, "x")] };
        Assert.DoesNotContain(draft.Validate(), problem =>
            problem.Error is ServerDraft.ValidationError.EnvKeyEmpty or ServerDraft.ValidationError.EnvKeyInvalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("API KEY")]
    [InlineData("API=KEY")]
    [InlineData("API-KEY")]
    [InlineData("1TOKEN")]
    public void UnsafeEnvironmentNamesAreRejected(string key)
    {
        var draft = new ServerDraft { Name = "local", Command = "npx", Env = [new EnvEntry(key, "x")] };
        Assert.Contains(draft.Validate(), problem =>
            problem.Error is ServerDraft.ValidationError.EnvKeyEmpty or ServerDraft.ValidationError.EnvKeyInvalid);
    }
}

public class AuthoringTransactionTests
{
    private sealed class FailSecondWrite : IConfigFileWriter
    {
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 2) throw new IOException("Injected second-target failure.");
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class ChangeFirstExternallyThenFailSecond : IConfigFileWriter
    {
        private string? _firstPath;
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 1)
            {
                _firstPath = path;
                AtomicWriter.Write(data, path);
                return;
            }
            if (_writes == 2)
            {
                AtomicWriter.Write("external-newer-change", _firstPath!);
                throw new IOException("Injected failure after an external edit.");
            }
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private static readonly ServerDraft Memory = new()
    {
        Name = "memory",
        Command = "npx",
        Args = ["-y", "@modelcontextprotocol/server-memory"],
    };

    [Fact]
    public void FailedSecondTargetRestoresTheFirstByteForByteAndLeavesDiscoveryUnchanged()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var before = harness.Discover();
        var cursorPath = Path.Combine(harness.Home, @".cursor\mcp.json");
        var codexPath = Path.Combine(harness.Home, @".codex\config.toml");
        var cursorBytes = File.ReadAllBytes(cursorPath);
        var codexBytes = File.ReadAllBytes(codexPath);
        var authoring = new ServerAuthoring(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailSecondWrite()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<IOException>(() =>
            authoring.Create(Memory, [ClientId.Cursor, ClientId.Codex], before.Servers));

        Assert.Equal(cursorBytes, File.ReadAllBytes(cursorPath));
        Assert.Equal(codexBytes, File.ReadAllBytes(codexPath));
        Assert.DoesNotContain(harness.Discover().Servers, server => server.Name == "memory");
    }

    [Fact]
    public void RollbackDeletesAConfigCreatedByTheFailedTransaction()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var before = harness.Discover();
        var vsCodePath = Path.Combine(harness.Home, ToggleHarness.CodeUser, "mcp.json");
        var authoring = new ServerAuthoring(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailSecondWrite()),
            harness.ParkStore,
            harness.Ledger);

        Assert.Throws<IOException>(() =>
            authoring.Create(Memory, [ClientId.VsCode, ClientId.Codex], before.Servers));

        Assert.False(File.Exists(vsCodePath));
        Assert.DoesNotContain(harness.Discover().Servers, server => server.Name == "memory");
    }

    [Fact]
    public void RollbackNeverOverwritesANewerExternalChangeAndNamesThePath()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.CodexConfig.Text(), @".codex\config.toml");
        var before = harness.Discover();
        var cursorPath = Path.Combine(harness.Home, @".cursor\mcp.json");
        var authoring = new ServerAuthoring(
            harness.Home,
            new ConfigWriter(harness.Backups, new ChangeFirstExternallyThenFailSecond()),
            harness.ParkStore,
            harness.Ledger);

        var error = Assert.Throws<ConfigTransactionException>(() =>
            authoring.Create(Memory, [ClientId.Cursor, ClientId.Codex], before.Servers));

        Assert.Contains(@".cursor\mcp.json", error.Paths.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("external-newer-change", File.ReadAllText(cursorPath));
    }
}

public class UpdateServerTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(Fixture.ClaudeCodeSettings.Text(), @".claude\settings.json");
        return harness;
    }

    [Fact]
    public void EditingAppliesToEveryClientTheServerIsIn()
    {
        using var harness = MakeHarness();
        // Put github in Claude Code too, so it lives in two clients.
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.ClaudeCode);

        var github = harness.Server("github");
        var draft = ServerDraft.Editing(github) with { Command = "bunx" };
        var result = harness.Authoring().Update(draft, "github", github, harness.Discover().Servers);

        Assert.Contains(ClientId.Cursor, result.Changed);
        Assert.Contains(ClientId.ClaudeCode, result.Changed);

        Assert.Equal("bunx", JsonDocument.Parse(harness.Text(@".cursor\mcp.json"))
            .ValueAt("mcpServers", "github", "command")?.StringValue);
        Assert.Equal("bunx", JsonDocument.Parse(harness.Text(".claude.json"))
            .ValueAt("mcpServers", "github", "command")?.StringValue);
    }

    /// <summary>A rename is a remove plus an add, because the name is the key.</summary>
    [Fact]
    public void RenamingReplacesTheKey()
    {
        using var harness = MakeHarness();
        var figma = harness.Server("figma");
        var draft = ServerDraft.Editing(figma) with { Name = "figma-design" };

        harness.Authoring().Update(draft, "figma", figma, harness.Discover().Servers);

        var cursor = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal(["github", "figma-design"], cursor.ValueAt("mcpServers")?.Keys);
        Assert.Equal(
            "https://mcp.figma.com/sse",
            cursor.ValueAt("mcpServers", "figma-design", "url")?.StringValue);
    }

    /// <summary>
    /// Recreating a renamed server later must not come back mysteriously switched
    /// off because a deny-list entry outlived the name.
    /// </summary>
    [Fact]
    public void RenamingClearsTheOldNamesDenyListEntry()
    {
        using var harness = MakeHarness();
        var xcode = harness.Server("XcodeBuildMCP");
        Assert.Equal(Enablement.Disabled, xcode.EnabledIn[ClientId.ClaudeCode]);

        var draft = ServerDraft.Editing(xcode) with { Name = "xcode-build" };
        harness.Authoring().Update(draft, "XcodeBuildMCP", xcode, harness.Discover().Servers);

        var settings = JsonDocument.Parse(harness.Text(@".claude\settings.json"));
        var denied = settings.Root["deniedMcpServers"]?.Elements?
            .Select(entry => entry["serverName"]?.StringValue).ToArray();
        Assert.DoesNotContain("XcodeBuildMCP", denied!, StringComparer.Ordinal);
    }

    [Fact]
    public void AnExtensionCannotBeEdited()
    {
        using var harness = MakeHarness();
        harness.Write(
            Fixture.ExtensionManifest.Text(),
            $@"{ToggleHarness.ClaudeSupport}\Claude Extensions\ant.dir.test.osascript\manifest.json");

        var bundled = harness.Discover().Servers.First(server => server.IsBundled);
        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Update(
                ServerDraft.Editing(bundled), bundled.Name, bundled, harness.Discover().Servers));
        Assert.Contains("installed bundles", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The form never receives secret values, so an untouched field sends null,
    /// which means "keep what is there" (§6).
    /// </summary>
    [Fact]
    public void AnUntouchedEnvValueKeepsWhatIsAlreadyInTheFile()
    {
        using var harness = MakeHarness();
        var github = harness.Server("github");
        Assert.Contains(github.Env, entry => entry.Key == "GITHUB_PERSONAL_ACCESS_TOKEN");

        // What the edit form sends back: the key, and no value.
        var draft = (ServerDraft.Editing(github) with
        {
            Command = "bunx",
            Env = [new EnvEntry("GITHUB_PERSONAL_ACCESS_TOKEN", null)],
        }).MergingSecrets(github);

        harness.Authoring().Update(draft, "github", github, harness.Discover().Servers);

        var cursor = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal(
            "ghp_exampletokenvalue",
            cursor.ValueAt("mcpServers", "github", "env", "GITHUB_PERSONAL_ACCESS_TOKEN")?.StringValue);
    }
}

public class DeleteServerTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        harness.Write(Fixture.ClaudeCode.Text(), ".claude.json");
        harness.Write(Fixture.ClaudeCodeSettings.Text(), @".claude\settings.json");
        return harness;
    }

    [Fact]
    public void DeletingRemovesTheServerFromEveryClient()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.ClaudeCode);

        var github = harness.Server("github");
        harness.Authoring().Delete(github);

        Assert.Equal(["figma"], JsonDocument.Parse(harness.Text(@".cursor\mcp.json"))
            .ValueAt("mcpServers")?.Keys);
        Assert.DoesNotContain("github", JsonDocument.Parse(harness.Text(".claude.json"))
            .ValueAt("mcpServers")!.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(harness.Discover().Servers, server => server.Name == "github");
    }

    /// <summary>
    /// Everything that could bring the row back has to go, or the next refresh
    /// resurrects it as "disabled".
    /// </summary>
    [Fact]
    public void DeletingClearsTheParkedCopyAndTheDenyListEntry()
    {
        using var harness = MakeHarness();

        // Park it by switching it off in a presence client.
        harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);
        Assert.NotNull(harness.ParkStore.Parked(ClientId.Cursor, "figma"));

        harness.Authoring().Delete(harness.Server("figma"));

        Assert.Null(harness.ParkStore.Parked(ClientId.Cursor, "figma"));
        Assert.DoesNotContain(harness.Discover().Servers, server => server.Name == "figma");
    }

    [Fact]
    public void RemovingFromOneClientLeavesTheOthersHoldingIt()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.ClaudeCode);

        var github = harness.Server("github");
        harness.Authoring().RemoveFrom(github, ClientId.ClaudeCode);

        var after = harness.Server("github");
        Assert.Equal(Enablement.Absent, after.EnabledIn[ClientId.ClaudeCode]);
        Assert.Equal(Enablement.Enabled, after.EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void RemovingFromAClientThatDoesNotHaveItIsRefused()
    {
        using var harness = MakeHarness();
        var figma = harness.Server("figma");
        Assert.Equal(Enablement.Absent, figma.EnabledIn[ClientId.VsCode]);

        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().RemoveFrom(figma, ClientId.VsCode));
        Assert.Contains("is not in any client", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDeleteIsBackedUpFirst()
    {
        using var harness = MakeHarness();
        var before = harness.Text(@".cursor\mcp.json");

        var result = harness.Authoring().Delete(harness.Server("figma"));

        var backupId = result.BackupIDs[ClientId.Cursor];
        var backup = harness.Backups.Get(backupId, ClientId.Cursor);
        Assert.NotNull(backup);
        Assert.Equal(before, File.ReadAllText(backup.Path));
    }
}

public class CatalogTests
{
    [Fact]
    public void TheCatalogLoads() => Assert.NotEmpty(Catalog.Entries);

    /// <summary>
    /// Shipping a literal <c>{{directory}}</c> into a config is a bug, so every
    /// token that appears in an entry's arguments has to be declared where the UI
    /// will find it and prompt for it (§7.2).
    /// </summary>
    [Fact]
    public void EveryPlaceholderTokenInArgsIsDeclared()
    {
        foreach (var entry in Catalog.Entries)
        {
            var declared = entry.Placeholders.Select(placeholder => placeholder.Token).ToHashSet();
            foreach (var argument in entry.Args)
            {
                foreach (Match match in Regex.Matches(argument, @"\{\{[^}]+\}\}"))
                {
                    Assert.True(
                        declared.Contains(match.Value),
                        $"{entry.Id} uses {match.Value} but does not declare it");
                }
            }
        }
    }

    /// <summary>A declared token nothing substitutes would prompt for nothing.</summary>
    [Fact]
    public void EveryDeclaredPlaceholderAppearsInArgs()
    {
        foreach (var entry in Catalog.Entries)
        {
            foreach (var placeholder in entry.Placeholders)
            {
                Assert.True(
                    entry.Args.Any(argument => argument.Contains(placeholder.Token, StringComparison.Ordinal)),
                    $"{entry.Id} declares {placeholder.Token} but never uses it");
            }
        }
    }

    [Fact]
    public void EntriesHaveTheFieldsTheUiRenders()
    {
        foreach (var entry in Catalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id));
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.False(string.IsNullOrWhiteSpace(entry.Homepage));
        }
    }

    [Fact]
    public void IdsAreUnique() =>
        Assert.Equal(Catalog.Entries.Count, Catalog.Entries.Select(entry => entry.Id).Distinct().Count());

    [Fact]
    public void ADraftFromAnEntryValidatesOnceItsPlaceholdersAreFilled()
    {
        var entry = Catalog.Entry("filesystem");
        Assert.NotNull(entry);

        var draft = entry.MakeDraft();
        var filled = draft with
        {
            Args = draft.Args
                .Select(argument => argument.Replace("{{directory}}", @"C:\work", StringComparison.Ordinal))
                .ToArray(),
        };

        Assert.Empty(filled.Validate());
    }
}
