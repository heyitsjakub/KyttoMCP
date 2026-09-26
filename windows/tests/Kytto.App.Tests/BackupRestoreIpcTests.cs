using System.IO;
using System.Text;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

/// <summary>
/// What a refused restore looks like on the wire.
/// </summary>
/// <remarks>
/// The shared web layer needs no branch for this — it renders the message through
/// its ordinary failure path — but the code has to be the documented one, because
/// anonymous diagnostics classify on the code and never see the message.
/// </remarks>
public sealed class BackupRestoreIpcTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-app-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ARewrittenSidecarFailsWithTheBackupCodeAndChangesNothing()
    {
        using var model = Model(out var paths);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        var config = Path.Combine(_home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"mcpServers\":{}}");

        var backup = model.Backups.BackUp(config, ClientId.Cursor, @"%USERPROFILE%\.cursor\mcp.json");
        Assert.NotNull(backup);

        var bystander = Path.Combine(_home, "bystander.txt");
        File.WriteAllText(bystander, "untouched");
        File.WriteAllText(backup.Path + ".origin", bystander, new UTF8Encoding(false));

        var reply = await router.InvokeForTests(
            "backups.restore",
            $$"""{ "backupID": {{JsonSerializer.Serialize(backup.Id)}}, "clientID": "cursor" }""");

        using var envelope = JsonDocument.Parse(reply);
        var error = envelope.RootElement.GetProperty("error");
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("backup", error.GetProperty("code").GetString());
        Assert.Contains(
            "is still in the backups folder",
            error.GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        Assert.Equal("untouched", File.ReadAllText(bystander));
        Assert.Single(model.Backups.List(ClientId.Cursor));
        Assert.Empty(Directory.GetFiles(paths.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ARestoreCannotBeAimedAtACustomSource()
    {
        using var model = Model(out _);
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        // A read-only custom source has no `ClientId`, so a hand-made
        // `backups\custom.<uuid>\…` entry has no way in at all — the closed enum is
        // the boundary, and it refuses before any handler sees a path.
        var id = ClientKey.Custom(Guid.NewGuid()).Raw();
        var reply = await router.InvokeForTests(
            "backups.restore",
            $$"""{ "backupID": "anything.bak", "clientID": {{JsonSerializer.Serialize(id)}} }""");

        using var envelope = JsonDocument.Parse(reply);
        Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("badArgument", envelope.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private AppModel Model(out KyttoPaths paths)
    {
        Directory.CreateDirectory(_home);
        paths = new KyttoPaths(Path.Combine(_home, "KyttoData"));
        return new AppModel(
            paths,
            _home,
            new NoInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary profile is not worth hiding the assertion result.
        }
    }

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }
}
