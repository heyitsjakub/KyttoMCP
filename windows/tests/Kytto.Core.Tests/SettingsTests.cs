using Kytto.Core;
using Kytto.Core.Settings;

namespace Kytto.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    /// <summary>
    /// §6.6: a first launch that touches nothing leaves no trace. Reading settings
    /// must not be what creates the directory.
    /// </summary>
    [Fact]
    public void ReadingDefaultsWritesNothing()
    {
        var store = new SettingsStore(new KyttoPaths(_root));
        var settings = store.Current;

        Assert.Equal(20, settings.BackupRetention);
        Assert.Equal(20_000, settings.TokenWarningThreshold);
        Assert.Equal(Theme.Dark, settings.Theme);
        Assert.False(settings.HasCompletedOnboarding);
        Assert.True(settings.ShowsCustomSources);
        Assert.Equal(196, settings.SidebarWidth);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void UpdatingCreatesTheDirectoryAndPersists()
    {
        var paths = new KyttoPaths(_root);
        var store = new SettingsStore(paths);

        var updated = store.Update(settings => settings with { HasCompletedOnboarding = true });
        Assert.True(updated.HasCompletedOnboarding);
        Assert.True(File.Exists(paths.SettingsFile));

        // A second store reads what the first wrote.
        Assert.True(new SettingsStore(paths).Current.HasCompletedOnboarding);
    }

    /// <summary>
    /// Derived values must not reach the file: a stored copy is a second source of
    /// truth that a hand-edited <c>theme</c> would silently contradict.
    /// </summary>
    [Fact]
    public void ComputedPropertiesAreNotPersisted()
    {
        var paths = new KyttoPaths(_root);
        // Light rather than Dark: Dark is the default, and a change that changes
        // nothing is deliberately not written at all.
        new SettingsStore(paths).Update(settings => settings with { Theme = Theme.Light });

        var written = File.ReadAllText(paths.SettingsFile);
        Assert.Contains("\"theme\": \"light\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("themeRaw", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.6: a launch that alters nothing leaves no trace. Rewriting identical bytes
    /// would still move the file's modification time and wake the config watcher.
    /// </summary>
    [Fact]
    public void SavingAnUnchangedSettingWritesNothing()
    {
        var paths = new KyttoPaths(_root);
        var store = new SettingsStore(paths);

        store.Update(settings => settings with { Theme = settings.Theme });
        Assert.False(File.Exists(paths.SettingsFile));

        store.Update(settings => settings with { BackupRetention = 30 });
        var stamp = File.GetLastWriteTimeUtc(paths.SettingsFile);

        var unchanged = store.Update(settings => settings with { BackupRetention = 30 });
        Assert.Equal(30, unchanged.BackupRetention);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(paths.SettingsFile));
    }

    [Fact]
    public void ConcurrentIndependentUpdatesDoNotLoseOneAnother()
    {
        var store = new SettingsStore(new KyttoPaths(_root));

        Parallel.For(0, 20, index =>
        {
            store.Update(settings =>
            {
                var overrides = new Dictionary<string, string>(settings.ClientPathOverrides)
                {
                    [$"client-{index}"] = Path.Combine(_root, $"client-{index}.json"),
                };
                return settings with { ClientPathOverrides = overrides };
            });
        });

        Assert.Equal(20, store.Current.ClientPathOverrides.Count);
        Assert.Equal(20, new SettingsStore(new KyttoPaths(_root))
            .Current.ClientPathOverrides.Count);
    }

    /// <summary>
    /// Diagnostics are an explicit choice, so a settings file written before they
    /// existed has to read as opted out rather than failing to parse.
    /// </summary>
    [Fact]
    public void ASettingsFileFromAnOlderBuildKeepsEverySetting()
    {
        var paths = new KyttoPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """
            { "backupRetention": 12, "tokenWarningThreshold": 15000, "theme": "light" }
            """);

        var settings = new SettingsStore(paths).Current;

        // Every older setting survives being read by the newer build.
        Assert.Equal(12, settings.BackupRetention);
        Assert.Equal(15_000, settings.TokenWarningThreshold);
        Assert.Equal(Theme.Light, settings.Theme);
        Assert.True(settings.ShowsCustomSources);
        Assert.Equal(196, settings.SidebarWidth);
    }

    [Fact]
    public void CustomSourceVisibilityPersistsWhenFolded()
    {
        var paths = new KyttoPaths(_root);
        var store = new SettingsStore(paths);

        store.Update(settings => settings with { ShowsCustomSources = false });

        Assert.False(new SettingsStore(paths).Current.ShowsCustomSources);
    }

    [Fact]
    public void SidebarWidthIsRememberedAndHandEditedValuesAreClamped()
    {
        var paths = new KyttoPaths(_root);
        var store = new SettingsStore(paths);
        Assert.Equal(480, store.Update(settings => settings with { SidebarWidth = 900 }).SidebarWidth);
        Assert.Equal(480, new SettingsStore(paths).Current.SidebarWidth);

        File.WriteAllText(paths.SettingsFile, """{ "sidebarWidth": 12 }""");
        Assert.Equal(170, new SettingsStore(paths).Current.SidebarWidth);
    }

    [Fact]
    public void IndependentNativeAndWebSettingsUpdatesPreserveFreshPresentationState()
    {
        var store = new SettingsStore(new KyttoPaths(_root));
        store.Update(settings => settings with
        {
            HasCompletedOnboarding = true,
            HasConfirmedMatrixWrites = true,
            ShowsCustomSources = false,
            SidebarWidth = 260,
        });

        var savedByNativeWindow = store.Update(settings => settings with
        {
            Theme = Theme.Light,
            BackupRetention = 30,
        });

        Assert.True(savedByNativeWindow.HasCompletedOnboarding);
        Assert.True(savedByNativeWindow.HasConfirmedMatrixWrites);
        Assert.False(savedByNativeWindow.ShowsCustomSources);
        Assert.Equal(260, savedByNativeWindow.SidebarWidth);
    }

    /// <summary>
    /// The same rule as settings, for the store that holds measurements: a derived
    /// value written to disk is a second source of truth waiting to disagree.
    /// </summary>
    [Fact]
    public void MetadataDoesNotPersistDerivedValues()
    {
        var paths = new KyttoPaths(_root);
        new MetadataStore(paths).Update("github", existing => existing with
        {
            Health = new Core.Health.HealthResult(
                Core.Health.HealthStatus.Passed, DateTimeOffset.UnixEpoch, 3, [],
                null, null, 1.0, "srv", "1.0"),
            TokenWeight = Core.Health.TokenWeight.Estimating("abcdefgh"),
        });

        var written = File.ReadAllText(paths.MetadataFile);
        Assert.Contains("\"status\": \"passed\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("statusRaw", written, StringComparison.Ordinal);
        Assert.DoesNotContain("isPassing", written, StringComparison.Ordinal);
        Assert.DoesNotContain("percentOfContext", written, StringComparison.Ordinal);

        // And it still reads back.
        Assert.Equal(3, new MetadataStore(paths).For("github")?.Health?.ToolCount);
    }

    [Fact]
    public void OlderHealthMetadataLoadsWithInspectorDefaults()
    {
        var paths = new KyttoPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.MetadataFile, """
            { "github": { "health": {
              "status": "passed", "checkedAt": "1970-01-01T00:00:00+00:00",
              "toolCount": 1, "tools": [{ "name": "read", "description": null }],
              "message": null, "stderr": null, "durationSeconds": 0.2,
              "serverName": "old", "serverVersion": "1.0"
            }, "tokenWeight": null } }
            """);

        var health = new MetadataStore(paths).For("github")?.Health;
        Assert.NotNull(health);
        Assert.Empty(health.CapabilityNames);
        Assert.Empty(health.InspectionNotes);
        Assert.Null(health.ProtocolVersion);
        Assert.Null(health.Tools[0].InputSchemaJSON);
    }

    [Fact]
    public void StructuredHealthFailureRendersCurrentCopyAfterRoundTripWhileLegacyKeepsItsWords()
    {
        var paths = new KyttoPaths(_root);
        var store = new MetadataStore(paths);
        store.Record("current", new Core.Health.HealthResult(
            Core.Health.HealthStatus.Failed,
            DateTimeOffset.UnixEpoch,
            null,
            [],
            "stale timeout wording",
            null,
            0.1,
            null,
            null)
        {
            Failure = Core.Health.HealthFailureReason.TimedOut(0.1),
        }, null);
        store.Record("legacy", new Core.Health.HealthResult(
            Core.Health.HealthStatus.Failed,
            DateTimeOffset.UnixEpoch,
            null,
            [],
            "words from an older build",
            null,
            0.1,
            null,
            null), null);

        var reloaded = new MetadataStore(paths);
        Assert.Equal(
            "The server did not answer within 1 second.",
            reloaded.For("current")?.Health?.CurrentMessage);
        Assert.Equal("words from an older build", reloaded.For("legacy")?.Health?.CurrentMessage);
    }

    [Fact]
    public void NonsenseInAHandEditedFileIsClamped()
    {
        var paths = new KyttoPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile, """
            { "backupRetention": 9999, "tokenWarningThreshold": 1,
              "clientPathOverrides": { "cursor": "not-a-full-path" } }
            """);

        var settings = new SettingsStore(paths).Current;
        Assert.Equal(200, settings.BackupRetention);
        Assert.Equal(1_000, settings.TokenWarningThreshold);
        Assert.Equal(196, settings.SidebarWidth);
        // An override that is not fully qualified would resolve against whatever
        // directory the app started in.
        Assert.Empty(settings.ClientPathOverrides);
    }

    [Fact]
    public void AFullyQualifiedOverrideSurvives()
    {
        var paths = new KyttoPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(paths.SettingsFile,
            """{ "clientPathOverrides": { "cursor": "D:\\configs\\mcp.json" } }""");

        var settings = new SettingsStore(paths).Current;
        Assert.Equal(@"D:\configs\mcp.json", settings.PathOverride(Core.Clients.ClientId.Cursor));
    }

    [Theory]
    [InlineData(@"C:\Users\you\AppData\Roaming\Code\User\mcp.json")]
    [InlineData(@"\\server\share\configs\mcp.json")]
    public void CompleteWindowsOverridePathsAreAccepted(string path) =>
        Assert.True(KyttoSettings.IsUsablePath(path));

    [Theory]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData(@"Users\you\mcp.json")]
    [InlineData(@"%USERPROFILE%\mcp.json")]
    public void IncompleteOrUnsupportedOverridePathsAreRejected(string path) =>
        Assert.False(KyttoSettings.IsUsablePath(path));

    [Fact]
    public void AtomicWriteReplacesContentWithoutLeavingTemporaries()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "config.json");

        AtomicWriter.Write("first", target);
        AtomicWriter.Write("second", target);

        Assert.Equal("second", File.ReadAllText(target));
        Assert.Equal([target], Directory.GetFiles(_root));
    }
}
