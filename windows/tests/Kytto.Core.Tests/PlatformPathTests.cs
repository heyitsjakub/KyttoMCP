using Kytto.Core.Clients;

namespace Kytto.Core.Tests;

/// <summary>
/// Where a client keeps its config, when a client can be installed two ways.
/// </summary>
/// <remarks>
/// Claude Desktop moved to an MSIX package, and a packaged app's writes to
/// <c>%APPDATA%\Claude</c> are redirected by Windows into its own container. Kytto
/// is not packaged, so writing the unredirected path produces a second config the
/// client never opens — it would back up, write valid JSON, report success and
/// change nothing. That is the failure this file exists to stop.
/// </remarks>
public sealed class PlatformPathTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-tests", Guid.NewGuid().ToString("N"));

    private const string Packaged =
        @"%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude";

    private static PlatformPath ClaudeConfig => new(
        Darwin: "~/Library/Application Support/Claude/claude_desktop_config.json",
        Windows: @"%APPDATA%\Claude\claude_desktop_config.json")
    {
        WindowsAlternates = [Packaged + @"\claude_desktop_config.json"],
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private string Create(string relative)
    {
        var full = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "{}");
        return full;
    }

    [Fact]
    public void TheClassicPathIsUsedWhenNothingElseExists()
    {
        var resolved = ClaudeConfig.Resolve(_home);
        Assert.Equal(
            Path.Combine(_home, "AppData", "Roaming", "Claude", "claude_desktop_config.json"),
            resolved);
    }

    [Fact]
    public void ThePackagedCopyWinsWhenItExists()
    {
        var packaged = Create(
            @"AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json");

        Assert.Equal(packaged, ClaudeConfig.Resolve(_home));
    }

    [Fact]
    public void ThePackagedLocationWinsBeforeItsFirstConfigFileExists()
    {
        var packagedDirectory = Path.Combine(
            _home,
            @"AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude");
        Directory.CreateDirectory(packagedDirectory);

        Assert.Equal(
            Path.Combine(packagedDirectory, "claude_desktop_config.json"),
            ClaudeConfig.Resolve(_home));
        Assert.StartsWith("%LOCALAPPDATA%", ClaudeConfig.DisplayString(_home), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingClassicConfigBeatsAStaleEmptyPackageDirectory()
    {
        var classic = Create(@"AppData\Roaming\Claude\claude_desktop_config.json");
        Directory.CreateDirectory(Path.Combine(
            _home,
            @"AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude"));

        Assert.Equal(classic, ClaudeConfig.Resolve(_home));
        Assert.StartsWith("%APPDATA%", ClaudeConfig.DisplayString(_home), StringComparison.Ordinal);
    }

    /// <summary>
    /// An MSIX install leaves the unredirected file behind too — it is what the
    /// app's own "Edit Config" button writes. The packaged one is still the one
    /// the client reads.
    /// </summary>
    [Fact]
    public void ThePackagedCopyWinsEvenWhenBothExist()
    {
        Create(@"AppData\Roaming\Claude\claude_desktop_config.json");
        var packaged = Create(
            @"AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json");

        Assert.Equal(packaged, ClaudeConfig.Resolve(_home));
    }

    /// <summary>
    /// The watcher needs every candidate: a client reinstalled the other way starts
    /// writing somewhere Kytto was not looking.
    /// </summary>
    [Fact]
    public void EveryCandidateIsAvailableForWatching()
    {
        var all = ClaudeConfig.ResolveAll(_home);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, path => path.Contains(@"Packages\Claude_", StringComparison.Ordinal));
        Assert.Contains(all, path => path.Contains(@"Roaming\Claude", StringComparison.Ordinal));
    }

    /// <summary>The UI names the copy actually in use, not the first one listed.</summary>
    [Fact]
    public void TheDisplayedPathFollowsTheOneInUse()
    {
        Assert.Equal(@"%APPDATA%\Claude\claude_desktop_config.json", ClaudeConfig.DisplayString(_home));

        Create(@"AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude_desktop_config.json");
        Assert.StartsWith("%LOCALAPPDATA%", ClaudeConfig.DisplayString(_home), StringComparison.Ordinal);
    }

    [Fact]
    public void APathWithNoAlternatesIsUnaffected()
    {
        var cursor = new PlatformPath("~/.cursor/mcp.json", @"%USERPROFILE%\.cursor\mcp.json");
        Assert.Equal(Path.Combine(_home, ".cursor", "mcp.json"), cursor.Resolve(_home));
        Assert.Equal(@"%USERPROFILE%\.cursor\mcp.json", cursor.DisplayString(_home));
    }

    /// <summary>
    /// The registry keeps the shape of the macOS one it was ported from, so
    /// every client still has to spell both platforms.
    /// </summary>
    [Fact]
    public void EveryRegistryPathNamesBothPlatforms()
    {
        foreach (var descriptor in ClientRegistry.All)
        {
            foreach (var source in descriptor.Sources)
            {
                Assert.False(string.IsNullOrWhiteSpace(source.Path.Darwin),
                    $"{descriptor.Id} has a source with no macOS path");
                Assert.False(string.IsNullOrWhiteSpace(source.Path.Windows),
                    $"{descriptor.Id} has a source with no Windows path");
            }
        }
    }
}
