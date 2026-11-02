using System.IO;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

/// <summary>
/// Re-reading off the UI thread must not change what a re-read means: the page hears
/// about real changes only, and the next write is judged against what the pass saw.
/// </summary>
public sealed class BackgroundReloadTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-app-tests", Guid.NewGuid().ToString("N"));

    private string CursorConfig => Path.Combine(_home, ".cursor", "mcp.json");

    private AppModel NewModel(string cursorJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CursorConfig)!);
        File.WriteAllText(CursorConfig, cursorJson);
        return new AppModel(
            new KyttoPaths(Path.Combine(_home, "KyttoData")),
            _home,
            new NoInstalledApps(),
            new InMemorySecretStore(),
            watchConfigs: false);
    }

    [Fact]
    public async Task AnUnchangedPassReportsNoChange()
    {
        using var model = NewModel("""{"mcpServers":{"memory":{"command":"npx","args":["-y","m"]}}}""");

        Assert.False(await model.ReloadInBackgroundAsync());
        Assert.False(await model.ReloadInBackgroundAsync());
        Assert.Contains(model.Current().Servers, server => server.Name == "memory");
    }

    [Fact]
    public async Task ARealChangeIsReportedAndAppliedAndTheNextWriteIsNotRefused()
    {
        using var model = NewModel("""{"mcpServers":{"memory":{"command":"npx","args":["-y","m"]}}}""");

        File.WriteAllText(
            CursorConfig,
            """{"mcpServers":{"memory":{"command":"npx","args":["-y","m"]},"time":{"command":"uvx","args":["t"]}}}""");

        Assert.True(await model.ReloadInBackgroundAsync());
        Assert.Contains(model.Current().Servers, server => server.Name == "time");

        // The pass adopted the digest of the file it read, so a guarded write goes through
        // instead of being refused as "changed on disk".
        var result = model.SetEnabled(false, Server.Identity("time"), ClientId.Cursor);
        Assert.True(result.RequiresRestart);
    }

    [Fact]
    public async Task AWriteBetweenTwoPassesIsNotUndoneByTheLaterPass()
    {
        using var model = NewModel("""{"mcpServers":{"memory":{"command":"npx","args":["-y","m"]}}}""");

        model.SetEnabled(false, Server.Identity("memory"), ClientId.Cursor);
        Assert.False(await model.ReloadInBackgroundAsync());
        Assert.Equal(
            Enablement.Disabled,
            model.Current().Servers.Single(server => server.Name == "memory").EnabledIn[ClientId.Cursor]);

        // And a second write still lines up with what the file now holds.
        model.SetEnabled(true, Server.Identity("memory"), ClientId.Cursor);
        Assert.Equal(
            Enablement.Enabled,
            model.Current().Servers.Single(server => server.Name == "memory").EnabledIn[ClientId.Cursor]);
    }

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a run over.
        }
    }
}
