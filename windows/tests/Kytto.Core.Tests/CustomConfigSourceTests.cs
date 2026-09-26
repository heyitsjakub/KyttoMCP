using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.Core.Tests;

public sealed class CustomConfigSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-custom-source-tests", Guid.NewGuid().ToString("N"));

    private sealed class MissingApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    [Fact]
    public void JsonServersMapIsDiscoveredAfterTheFiveBuiltIns()
    {
        var path = Write("custom.json", """
            {
              "servers": {
                "demo-tool": { "command": "cmd", "args": ["/c", "echo", "demo"] }
              }
            }
            """);
        var source = Source(path, ConfigurationScope.Workspace, "Demo project");

        var result = Discover(source);

        Assert.Equal(6, result.Clients.Count);
        Assert.Equal(
            ClientIds.All,
            result.Clients.Take(5).Select(client => client.Id.BuiltIn!.Value).ToArray());
        var client = result.Clients[^1];
        Assert.Equal(source.ClientID, client.Id);
        Assert.Equal("client-custom", client.IconAsset);
        Assert.True(client.IsReadOnly);
        Assert.Equal(ConfigurationScope.Workspace, client.ConfigurationScope);
        Assert.Equal("Demo project", client.ScopeLabel);
        Assert.Equal("Auto-detected · read-only", client.ConfigFormatDisplay);
        Assert.Equal(path, client.ConfigPathDisplay);
        Assert.Equal(1, client.ServerCount);

        var server = Assert.Single(result.Servers);
        Assert.Equal("demo-tool", server.Name);
        Assert.Equal(Enablement.Enabled, server.EnabledIn[source.ClientID]);
        Assert.All(ClientIds.All, id => Assert.Equal(Enablement.Absent, server.EnabledIn[id]));
        Assert.True(server.DefinitionsByClient[source.ClientID].IsReadOnly);
    }

    [Fact]
    public void JsoncCommentsAreAcceptedWithoutChangingAnyByte()
    {
        var path = Write("commented.jsonc", """
            {
              // This comment and its exact bytes belong to the user.
              "mcpServers": {
                "commented": { "command": "tool", },
              },
            }
            """);
        var before = File.ReadAllBytes(path);

        var result = Discover(Source(path));

        Assert.Equal("commented", Assert.Single(result.Servers).Name);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void TomlMcpServersTableIsDiscoveredRegardlessOfExtension()
    {
        // Detection is content-based. A selected .json file may contain TOML and
        // still has to be tried after JSON/JSONC parsing fails.
        var path = Write("actually-toml.json", """
            [mcp_servers.demo]
            command = "demo-tool"
            args = ["--safe"]

            [mcp_servers.demo.env]
            MODE = "test"
            """);
        var before = File.ReadAllBytes(path);

        var server = Assert.Single(Discover(Source(path)).Servers);

        Assert.Equal("demo", server.Name);
        Assert.Equal("demo-tool", server.Command);
        Assert.Equal(["--safe"], server.Args);
        Assert.Equal([new EnvEntry("MODE", "test")], server.Env);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AllScopesLabelsAndConfirmationSurviveSettingsRoundTrip()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "settings"));
        var global = Source(Path.Combine(_root, "global.json"));
        var profile = Source(
            Path.Combine(_root, "profile.jsonc"), ConfigurationScope.Profile, "Work");
        var workspace = Source(
            Path.Combine(_root, "workspace.toml"), ConfigurationScope.Workspace, "Demo project");

        new SettingsStore(paths).Update(settings => settings with
        {
            CustomConfigSources = [global, profile, workspace],
            HasConfirmedMatrixWrites = true,
        });
        var loaded = new SettingsStore(paths).Current;

        Assert.True(loaded.HasConfirmedMatrixWrites);
        Assert.Equal([global, profile, workspace], loaded.CustomConfigSources);
        Assert.Equal(
            [ConfigurationScope.Global, ConfigurationScope.Profile, ConfigurationScope.Workspace],
            loaded.CustomConfigSources.Select(source => source.Scope));
        Assert.Equal(["", "Work", "Demo project"],
            loaded.CustomConfigSources.Select(source => source.ScopeLabel));
    }

    [Fact]
    public void OlderSettingsDefaultToNoSourcesAndNoWriteConfirmation()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "older-settings"));
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(paths.SettingsFile, """
            { "backupRetention": 12, "theme": "light", "hasCompletedOnboarding": true }
            """);

        var settings = new SettingsStore(paths).Current;

        Assert.Empty(settings.CustomConfigSources);
        Assert.False(settings.HasConfirmedMatrixWrites);
        Assert.True(settings.HasCompletedOnboarding);
        Assert.Equal(12, settings.BackupRetention);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankDisplayNamesAreRejectedWithoutWriting(string name)
    {
        var paths = new KyttoPaths(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var source = Source(Path.Combine(_root, "valid.json")) with { DisplayName = name };

        var error = Assert.Throws<SettingsValidationException>(() =>
            new SettingsStore(paths).Update(settings => settings with
            {
                CustomConfigSources = [source],
            }));

        Assert.Contains("name", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void RelativePathsAreRejectedWithoutWriting()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "relative-settings"));
        var source = Source(Path.Combine(_root, "valid.json")) with
        {
            Path = @"project\mcp.json",
        };

        var error = Assert.Throws<SettingsValidationException>(() =>
            new SettingsStore(paths).Update(settings => settings with
            {
                CustomConfigSources = [source],
            }));

        Assert.Contains("fully qualified", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void DuplicateSourceIdsAreRejectedWithoutWriting()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "duplicate-settings"));
        var first = Source(Path.Combine(_root, "first.json"));
        var duplicate = first with
        {
            DisplayName = "Second",
            Path = Path.Combine(_root, "second.toml"),
        };

        var error = Assert.Throws<SettingsValidationException>(() =>
            new SettingsStore(paths).Update(settings => settings with
            {
                CustomConfigSources = [first, duplicate],
            }));

        Assert.Contains("more than once", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void UnknownScopesAreRejectedWithoutWriting()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "invalid-scope-settings"));
        var source = Source(Path.Combine(_root, "valid.json")) with
        {
            Scope = (ConfigurationScope)99,
        };

        var error = Assert.Throws<SettingsValidationException>(() =>
            new SettingsStore(paths).Update(settings => settings with
            {
                CustomConfigSources = [source],
            }));

        Assert.Contains("scope", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void UnknownAndAmbiguousMapsProduceDiagnosticsAndNoServers()
    {
        var unknownPath = Write("unknown.json", """{ "notMcp": { "demo": {} } }""");
        var ambiguousPath = Write("ambiguous.jsonc", """
            {
              "mcpServers": { "one": { "command": "one" } },
              "servers": { "two": { "command": "two" } }
            }
            """);

        var unknown = Discover(Source(unknownPath));
        var ambiguous = Discover(Source(ambiguousPath));

        Assert.Empty(unknown.Servers);
        Assert.Contains(unknown.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Expected exactly one", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Kytto did not change the file", StringComparison.Ordinal));
        Assert.Empty(ambiguous.Servers);
        Assert.Contains(ambiguous.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
            && diagnostic.Message.Contains("Kytto did not change the file", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidEntriesDoNotHideValidSiblings()
    {
        var path = Write("siblings.json", """
            {
              "servers": {
                "bad": { "command": 42 },
                "good": { "command": "safe-tool" }
              }
            }
            """);

        var result = Discover(Source(path));

        Assert.Equal(["good"], result.Servers.Select(server => server.Name));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Skipped \"bad\"", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Kytto did not change the file", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidTomlEntriesDoNotHideValidSiblings()
    {
        var path = Write("siblings.toml", """
            [mcp_servers]
            bad = 42
            good = { command = "safe-tool" }
            """);

        var result = Discover(Source(path));

        Assert.Equal(["good"], result.Servers.Select(server => server.Name));
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Skipped \"bad\"", StringComparison.Ordinal)
            && diagnostic.Message.Contains("expected a table/object", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Kytto did not change the file", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateTomlServerNamesAreAmbiguousAndNotDiscovered()
    {
        var path = Write("duplicate-names.toml", """
            [mcp_servers]
            Demo = { command = "first" }
            demo = { command = "second" }
            """);

        var result = Discover(Source(path));

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("ambiguous duplicate server names", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Kytto did not change the file", StringComparison.Ordinal));
    }

    [Fact]
    public void ASecondSupportedTomlKeyIsAmbiguousEvenWhenItsValueIsNotAMap()
    {
        var path = Write("ambiguous-key.toml", """
            servers = "not-a-server-map"

            [mcp_servers.demo]
            command = "safe-tool"
            """);

        var result = Discover(Source(path));

        Assert.Empty(result.Servers);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidDocumentIsReportedAndLeftByteForByteUntouched()
    {
        var path = Write("invalid.toml", "{ definitely not JSON or TOML");
        var before = File.ReadAllBytes(path);

        var result = Discover(Source(path));

        Assert.Empty(result.Servers);
        var diagnostic = Assert.Single(result.Diagnostics,
            item => item.ClientID?.IsCustom == true);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("JSON/JSONC or TOML", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Kytto did not change the file", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AttachWatchDiscoverAndDetachNeverChangeOrDeleteTheSource()
    {
        var path = Write("watched.json", """
            { "servers": { "demo": { "command": "demo" } }, "unrelated": true }
            """);
        var before = File.ReadAllBytes(path);
        var source = Source(path);
        var paths = new KyttoPaths(Path.Combine(_root, "kytto-data"));
        var settings = new SettingsStore(paths);
        settings.Update(current => current with { CustomConfigSources = [source] });

        var attached = NewDiscovery(settings.Current.CustomConfigSources);
        Assert.Contains(attached.WatchTargets(), target =>
            !target.IncludeSubdirectories
            && string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.Single(attached.Run().Servers);
        Assert.Equal(before, File.ReadAllBytes(path));

        using var changed = new ManualResetEventSlim();
        using (var watcher = new ConfigWatcher(
                   attached.WatchTargets(),
                   changed.Set,
                   TimeSpan.FromMilliseconds(40)))
        {
            File.AppendAllText(path, Environment.NewLine + "// external edit");
            Assert.True(changed.Wait(TimeSpan.FromSeconds(2)));
        }
        var externallyEdited = File.ReadAllBytes(path);

        settings.Update(current => current with { CustomConfigSources = [] });
        var detached = NewDiscovery(settings.Current.CustomConfigSources);
        Assert.DoesNotContain(detached.WatchTargets(), target =>
            string.Equals(target.Path, path, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(detached.Run().Clients, client => client.Id == source.ClientID);
        Assert.True(File.Exists(path));
        Assert.Equal(externallyEdited, File.ReadAllBytes(path));
    }

    [Fact]
    public void MissingAttachedFileIsVisibleAndNeverCreated()
    {
        var path = Path.Combine(_root, "later.json");
        var source = Source(path);

        var client = Discover(source).Clients.Single(candidate => candidate.Id == source.ClientID);

        Assert.False(client.ConfigExists);
        Assert.Equal(0, client.ServerCount);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CustomIdCannotResolveToAMutationClientAndAuthoringRejectsIt()
    {
        var path = Write("readonly.json", """
            { "servers": { "readonly": { "command": "tool", "env": { "TOKEN": "secret-12345678901234567890" } } } }
            """);
        var before = File.ReadAllBytes(path);
        var source = Source(path);
        var server = Assert.Single(Discover(source).Servers);

        Assert.Null(ClientIds.FromRaw(source.ClientID.Raw()));

        var paths = new KyttoPaths(Path.Combine(_root, "authoring-data"));
        var writer = new ConfigWriter(new BackupStore(paths));
        var ledger = new DigestLedger();
        var authoring = new ServerAuthoring(
            _root,
            writer,
            new ParkStore(paths),
            ledger);
        var error = Assert.Throws<AuthoringException>(() => authoring.Delete(server));
        Assert.Contains("read-only custom source", error.Message, StringComparison.OrdinalIgnoreCase);

        var secrets = new SecretsService(
            _root,
            writer,
            ledger,
            new InMemorySecretStore());
        Assert.Empty(secrets.Scan([server]));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private DiscoveryResult Discover(params CustomConfigSource[] sources) =>
        NewDiscovery(sources).Run();

    private Discovery NewDiscovery(IReadOnlyList<CustomConfigSource> sources) => new(
        home: _root,
        locator: new MissingApps(),
        customSources: sources);

    private CustomConfigSource Source(
        string path,
        ConfigurationScope scope = ConfigurationScope.Global,
        string label = "") => new(
            Guid.NewGuid(),
            "Demo workspace",
            path,
            scope,
            label);

    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A delayed watcher handle is not worth hiding the assertion result.
        }
    }
}
