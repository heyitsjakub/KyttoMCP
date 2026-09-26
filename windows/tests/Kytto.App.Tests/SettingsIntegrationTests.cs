using System.IO;
using Kytto.Core;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

public sealed class SettingsIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-app-settings-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoginRegistrationFailureLeavesSavedSettingsAndEventsUntouched()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        var login = new RecordingLoginItem { FailEnabling = true };
        using var model = Model(paths, new SettingsStore(paths), login);
        var events = 0;
        model.SettingsChanged += _ => events++;

        var error = Assert.Throws<InvalidOperationException>(() =>
            model.UpdateSettings(settings => settings with { LaunchAtLogin = true }));

        Assert.Contains("registration failure", error.Message, StringComparison.Ordinal);
        Assert.False(model.Settings.LaunchAtLogin);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(login.Enabled);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SettingsWriteFailureRestoresThePreviousLoginState()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        var login = new RecordingLoginItem();
        var store = new SettingsStore(paths, new FailingSettingsWriter());
        using var model = Model(paths, store, login);
        var events = 0;
        model.SettingsChanged += _ => events++;

        var error = Assert.Throws<IOException>(() =>
            model.UpdateSettings(settings => settings with { LaunchAtLogin = true }));

        Assert.Contains("settings write failure", error.Message, StringComparison.Ordinal);
        Assert.Equal([true, false], login.Attempts);
        Assert.False(login.Enabled);
        Assert.False(model.Settings.LaunchAtLogin);
        Assert.Equal(0, events);
    }

    [Fact]
    public void SettingsAndLoginRollbackFailuresAreReturnedTogether()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        var login = new RecordingLoginItem { FailDisabling = true };
        var store = new SettingsStore(paths, new FailingSettingsWriter());
        using var model = Model(paths, store, login);
        var events = 0;
        model.SettingsChanged += _ => events++;

        var error = Assert.Throws<SettingsTransactionException>(() =>
            model.UpdateSettings(settings => settings with { LaunchAtLogin = true }));

        Assert.Contains("settings write failure", error.Message, StringComparison.Ordinal);
        Assert.Contains("rollback failure", error.Message, StringComparison.Ordinal);
        Assert.Equal([true, false], login.Attempts);
        Assert.True(login.Enabled);
        Assert.False(model.Settings.LaunchAtLogin);
        Assert.Equal(0, events);
    }

    [Fact]
    public void CorruptProfilesCannotTurnACommittedServerRenameIntoFailure()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        var cursorPath = Path.Combine(_root, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(cursorPath)!);
        File.WriteAllText(cursorPath, """{"mcpServers":{"old":{"command":"npx"}}}""");
        Directory.CreateDirectory(paths.Root);
        var corruptProfiles = new byte[] { 0xff, 0x00, (byte)'x' };
        File.WriteAllBytes(paths.ProfilesFile, corruptProfiles);
        using var model = Model(paths, new SettingsStore(paths), new RecordingLoginItem());

        var result = model.UpdateServer(new Kytto.Core.Model.ServerDraft
        {
            Name = "new",
            Command = "npx",
        }, "old");

        Assert.Equal("new", result.ServerName);
        Assert.Contains("\"new\"", File.ReadAllText(cursorPath), StringComparison.Ordinal);
        Assert.DoesNotContain("\"old\"", File.ReadAllText(cursorPath), StringComparison.Ordinal);
        Assert.Equal(corruptProfiles, File.ReadAllBytes(paths.ProfilesFile));
    }

    /// <summary>
    /// The sidebar's Settings row states an intent; the shell owns the window.
    /// The reply must not wait for it, or a modal dialog would leave the page's
    /// <c>settings.open</c> awaiting for as long as it is open (§3.2).
    /// </summary>
    [Fact]
    public async Task SettingsOpenAsksTheShellForItsWindowAndAnswersWithoutWaiting()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        using var model = Model(paths, new SettingsStore(paths), new RecordingLoginItem());
        var router = new Kytto.App.Ipc.CommandRouter();
        var asked = 0;
        Kytto.App.Ipc.CommandRegistry.RegisterAll(router, model, () => asked++);

        var reply = await router.InvokeForTests("settings.open");

        Assert.Equal(1, asked);
        Assert.Contains("\"ok\":true", reply, StringComparison.Ordinal);
        // Nothing is written by looking at settings.
        Assert.False(File.Exists(paths.SettingsFile));
    }

    /// <summary>
    /// A host with no window — the test host, and any future one — still
    /// registers the command, so the page never sees a transient
    /// <c>unknownCommand</c> for a row that is always on screen.
    /// </summary>
    [Fact]
    public async Task SettingsOpenIsRegisteredEvenWhereThereIsNoWindowToOpen()
    {
        var paths = new KyttoPaths(Path.Combine(_root, "KyttoData"));
        using var model = Model(paths, new SettingsStore(paths), new RecordingLoginItem());
        var router = new Kytto.App.Ipc.CommandRouter();
        Kytto.App.Ipc.CommandRegistry.RegisterAll(router, model);

        var reply = await router.InvokeForTests("settings.open");

        Assert.Contains("\"ok\":true", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("unknownCommand", reply, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    private AppModel Model(
        KyttoPaths paths,
        SettingsStore store,
        ILoginItem login) => new(
            paths,
            _root,
            new AllInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false,
            settingsStore: store,
            loginItem: login);

    private sealed class FailingSettingsWriter : ISettingsFileWriter
    {
        public void Write(string text, string path) =>
            throw new IOException("Injected settings write failure.");
    }

    private sealed class RecordingLoginItem : ILoginItem
    {
        internal bool Enabled { get; private set; }
        internal bool FailEnabling { get; init; }
        internal bool FailDisabling { get; init; }
        internal List<bool> Attempts { get; } = [];

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            Attempts.Add(enabled);
            if (enabled && FailEnabling)
            {
                throw new InvalidOperationException("Injected login registration failure.");
            }
            if (!enabled && FailDisabling)
            {
                throw new InvalidOperationException("Injected login rollback failure.");
            }
            Enabled = enabled;
        }
    }

    private sealed class AllInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => true;
        public bool PackageExists(string familyName) => true;
        public bool ExecutableExists(string name, string home) => true;
    }
}
