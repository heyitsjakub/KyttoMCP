using Kytto.Core;

namespace Kytto.Core.Tests;

public sealed class ConfigWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-watcher-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactConfigIgnoresActivityElsewhereInItsParent()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, ".claude.json");
        using var changed = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(
            [ConfigWatchTarget.File(config)],
            changed.Set,
            TimeSpan.FromMilliseconds(40));

        var unrelated = Path.Combine(_root, "projects", "session.log");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "ordinary activity");

        Assert.False(changed.Wait(TimeSpan.FromMilliseconds(250)));

        File.WriteAllText(config, "{}");
        Assert.True(changed.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void InstalledPackageTreesRemainRecursive()
    {
        Directory.CreateDirectory(_root);
        using var changed = new ManualResetEventSlim();
        using var watcher = new ConfigWatcher(
            [ConfigWatchTarget.Tree(_root)],
            changed.Set,
            TimeSpan.FromMilliseconds(40));

        var manifest = Path.Combine(_root, "plugin", "nested", ".mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "{}");

        Assert.True(changed.Wait(TimeSpan.FromSeconds(2)));
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
