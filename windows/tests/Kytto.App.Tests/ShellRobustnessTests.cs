using System.IO;
using System.Threading;
using Kytto.App.Ipc;
using Kytto.App.Web;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using Kytto.Core.Health;

namespace Kytto.App.Tests;

public sealed class ShellRobustnessTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-shell-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void CloseOnlyKeepsTheAppAliveForANonQuitTrayClose(
        bool trayEnabled,
        bool explicitQuit,
        bool expected) =>
        Assert.Equal(expected, MainWindow.ShouldKeepRunning(trayEnabled, explicitQuit));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"id\": \"1\", \"command\": \"state.get\" }")]
    [InlineData("{ \"id\": 2147483648, \"command\": \"state.get\" }")]
    [InlineData("{ \"id\": 1, \"command\": 7 }")]
    [InlineData("{ \"kind\": \"log\" }")]
    [InlineData("{ \"kind\": \"log\", \"text\": 7 }")]
    public void MalformedWebMessagesCannotThrowOnTheDispatcher(string json)
    {
        var message = CommandRouter.DecodeIncoming(json, out var pageLog);

        Assert.Null(message);
        Assert.Null(pageLog);
    }

    [Fact]
    public void ValidWebMessagesRetainTheirPayloadAfterParsing()
    {
        var message = CommandRouter.DecodeIncoming(
            """{ "id": 7, "command": "servers.setEnabled", "payload": { "enabled": true } }""",
            out var pageLog);

        Assert.NotNull(message);
        Assert.Null(pageLog);
        Assert.Equal(7, message.Id);
        Assert.Equal("servers.setEnabled", message.Command);
        Assert.True(message.Payload.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void PageLogMessagesAreRecognisedWithoutBeingCommands()
    {
        var message = CommandRouter.DecodeIncoming(
            """{ "kind": "log", "text": "handler failed" }""",
            out var pageLog);

        Assert.Null(message);
        Assert.Equal("handler failed", pageLog);
    }

    [Theory]
    [InlineData("https://app/index.html", true)]
    [InlineData("https://APP/js/main.js", true)]
    [InlineData("https://app:443/", true)]
    [InlineData("http://app/index.html", false)]
    [InlineData("https://app.example/index.html", false)]
    [InlineData("https://example.test/", false)]
    [InlineData("not a uri", false)]
    public void NativeCommandsAcceptOnlyTheAppOrigin(string source, bool expected) =>
        Assert.Equal(expected, CommandRouter.IsSameOrigin(source, WebHost.TrustedOrigin));

    [Fact]
    public void StaticFilesCannotEscapeIntoASiblingWhoseNameSharesTheRootPrefix()
    {
        var root = Path.Combine(_home, "web");
        var child = Path.Combine(root, "js", "main.js");
        var sibling = Path.Combine(_home, "web-secret", "token.txt");

        Assert.True(WebHost.IsWithinRoot(root, child));
        Assert.False(WebHost.IsWithinRoot(root, sibling));
        Assert.False(WebHost.IsWithinRoot(root, Path.Combine(root, "..", "settings.json")));
    }

    [Fact]
    public void UnknownPersistedTrayClientFallsBackToAutomaticInSettings()
    {
        Assert.Null(SettingsWindow.ValidTrayClient("removed-client"));
        Assert.Null(SettingsWindow.ValidTrayClient(null));
        Assert.Equal("cursor", SettingsWindow.ValidTrayClient("cursor"));
    }

    [Fact]
    public async Task UnknownRestartClientCannotAccidentallyAcknowledgeEveryClient()
    {
        Directory.CreateDirectory(_home);
        using var model = NewModel();
        model.PendingRestarts[ClientId.Cursor] = 2;
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests(
            "restarts.acknowledge",
            """{ "clientID": "curser" }""");

        Assert.Contains("\"code\":\"badArgument\"", reply, StringComparison.Ordinal);
        Assert.Equal(2, model.PendingRestarts[ClientId.Cursor]);
    }

    [Fact]
    public async Task MissingRequiredPayloadMembersAreBadPayloadsRatherThanInternalErrors()
    {
        var router = new CommandRouter();
        router.Register<SetEnabledPayload, bool>("test", _ => true);

        var missing = await router.InvokeForTests(
            "test",
            """{ "serverID": "github", "enabled": true }""");
        var explicitNull = await router.InvokeForTests(
            "test",
            """{ "serverID": "github", "clientID": null, "enabled": true }""");

        Assert.Contains("\"code\":\"badPayload\"", missing, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"badPayload\"", explicitNull, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTransportIsRejectedInsteadOfSilentlyBecomingStdio()
    {
        var payload = new ServerDraftPayload(
            "remote", "htpt", null, [], [], "https://example.test/mcp");

        var error = Assert.Throws<BadArgumentException>(payload.MakeDraft);
        Assert.Equal("Unknown transport htpt.", error.Message);
    }

    [Fact]
    public void RefreshStartsWatchingAConfigDirectoryCreatedAfterLaunch()
    {
        using var model = NewModel(watchConfigs: true);
        var config = Path.Combine(_home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """{ "mcpServers": {} }""");

        // The launch-time watcher could not attach to .cursor because it did not
        // exist. A manual refresh must rebuild the targets now that it does.
        model.Reload();

        using var changed = new ManualResetEventSlim();
        model.ConfigsChanged += changed.Set;
        File.AppendAllText(config, " ");

        Assert.True(
            changed.Wait(TimeSpan.FromSeconds(5)),
            "The refresh kept the launch-time watcher that skipped the missing directory.");
    }

    [Fact]
    public async Task AddingAndDetachingCustomSourceReloadsDiscoveryWithoutChangingItsFile()
    {
        Directory.CreateDirectory(_home);
        var sourcePath = Path.Combine(_home, "demo.jsonc");
        var original = """
            {
              // deliberately retained
              "servers": { "demo-tool": { "command": "cmd", "args": ["/c", "echo"] } }
            }
            """;
        File.WriteAllText(sourcePath, original);
        var before = File.ReadAllBytes(sourcePath);
        var source = new CustomConfigSource(
            Guid.NewGuid(), "Demo workspace", sourcePath,
            ConfigurationScope.Workspace, "Demo project");

        using var model = NewModel(watchConfigs: true);
        model.UpdateSettings(settings => settings with { CustomConfigSources = [source] });

        var client = Assert.Single(model.Current().Clients, item => item.Id == source.ClientID);
        Assert.True(client.IsReadOnly);
        Assert.Equal(ConfigurationScope.Workspace, client.ConfigurationScope);
        Assert.Equal("Demo project", client.ScopeLabel);
        Assert.Contains(model.Current().Servers, server => server.Name == "demo-tool");
        Assert.Equal(before, File.ReadAllBytes(sourcePath));

        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);
        var reply = await router.InvokeForTests("clients.list", "{}");
        Assert.Contains("\"isReadOnly\":true", reply, StringComparison.Ordinal);
        Assert.Contains("\"configurationScope\":\"workspace\"", reply, StringComparison.Ordinal);
        Assert.Contains("\"scopeLabel\":\"Demo project\"", reply, StringComparison.Ordinal);

        model.UpdateSettings(settings => settings with { CustomConfigSources = [] });
        Assert.DoesNotContain(model.Current().Clients, item => item.Id == source.ClientID);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void SettingsChangeRebuildsWatcherForNewCustomSource()
    {
        Directory.CreateDirectory(_home);
        var sourcePath = Path.Combine(_home, "watched.toml");
        File.WriteAllText(sourcePath, "[mcp_servers.demo]\ncommand = 'cmd'\n");
        using var model = NewModel(watchConfigs: true);

        model.UpdateSettings(settings => settings with
        {
            CustomConfigSources =
            [
                new CustomConfigSource(Guid.NewGuid(), "Watched", sourcePath),
            ],
        });
        using var changed = new ManualResetEventSlim();
        model.ConfigsChanged += changed.Set;
        File.AppendAllText(sourcePath, "# external edit\n");

        Assert.True(changed.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConfirmationPersistsAndCustomIdsAreRejectedByMutationIpc()
    {
        Directory.CreateDirectory(_home);
        using var model = NewModel();
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var confirmed = await router.InvokeForTests("settings.confirmMatrixWrites", "{}");
        Assert.Contains("\"hasConfirmedMatrixWrites\":true", confirmed, StringComparison.Ordinal);
        Assert.True(model.Settings.HasConfirmedMatrixWrites);

        var folded = await router.InvokeForTests(
            "settings.setShowsCustomSources",
            """{ "shown": false }""");
        Assert.Contains("\"showsCustomSources\":false", folded, StringComparison.Ordinal);
        Assert.False(model.Settings.ShowsCustomSources);

        var resized = await router.InvokeForTests(
            "settings.setSidebarWidth",
            """{ "width": 900 }""");
        Assert.Contains("\"sidebarWidth\":480", resized, StringComparison.Ordinal);
        Assert.Equal(480, model.Settings.SidebarWidth);

        var customId = $"custom.{Guid.NewGuid():D}";
        var rejected = await router.InvokeForTests(
            "servers.setEnabled",
            $$"""{ "serverID": "demo", "clientID": "{{customId}}", "enabled": true }""");
        Assert.Contains("\"code\":\"badArgument\"", rejected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationCommandOpensOnlyTheNativeRecordedHttpsPage()
    {
        var paths = new KyttoPaths(Path.Combine(_home, "KyttoData"));
        const string Recorded = "https://auth.example.com/oauth/authorize?server=demo";
        new MetadataStore(paths).Record("demo", new HealthResult(
            HealthStatus.NeedsAuthorization,
            Timestamp.Now,
            null,
            [],
            HealthFailureReason.AwaitingAuthorization().CurrentMessage(),
            "Please authorize",
            1,
            null,
            null)
        {
            Failure = HealthFailureReason.AwaitingAuthorization(),
            AuthorizationURL = Recorded,
        }, null);
        var launcher = new RecordingAuthorizationPageLauncher();
        using var model = NewModel(paths: paths, authorizationPageLauncher: launcher);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests(
            "health.openAuthorization",
            """{ "serverID": "demo", "url": "https://attacker.example/" }""");

        Assert.Contains("\"ok\":true", reply, StringComparison.Ordinal);
        Assert.Equal(Recorded, launcher.Opened);
    }

    [Fact]
    public async Task AuthorizationCommandRejectsMissingOrUnsafeNativePages()
    {
        var paths = new KyttoPaths(Path.Combine(_home, "KyttoData"));
        new MetadataStore(paths).Record("demo", new HealthResult(
            HealthStatus.NeedsAuthorization,
            Timestamp.Now,
            null,
            [],
            "waiting",
            null,
            1,
            null,
            null)
        {
            AuthorizationURL = "http://auth.example.com/oauth",
        }, null);
        var launcher = new RecordingAuthorizationPageLauncher();
        using var model = NewModel(paths: paths, authorizationPageLauncher: launcher);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests(
            "health.openAuthorization",
            """{ "serverID": "demo" }""");

        Assert.Contains(
            "No authorization page is recorded for this server. Check it again to capture one.",
            reply,
            StringComparison.Ordinal);
        Assert.Null(launcher.Opened);
    }

    private AppModel NewModel(
        bool watchConfigs = false,
        KyttoPaths? paths = null,
        IAuthorizationPageLauncher? authorizationPageLauncher = null) => new(
        paths ?? new KyttoPaths(Path.Combine(_home, "KyttoData")),
        _home,
        new NoInstalledApps(),
        new InMemorySecretStore(),
        watchConfigs: watchConfigs,
        authorizationPageLauncher: authorizationPageLauncher);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    private sealed class RecordingAuthorizationPageLauncher : IAuthorizationPageLauncher
    {
        public string? Opened { get; private set; }
        public void Open(string url) => Opened = url;
    }
}
