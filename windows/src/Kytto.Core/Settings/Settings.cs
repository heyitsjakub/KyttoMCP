using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Clients;

namespace Kytto.Core.Settings;

/// <summary>Where Kytto keeps its own data.</summary>
/// <remarks>
/// Roaming rather than Local: backups and the record of what was parked are the
/// user's data, not a cache, and losing them on a machine move would lose the only
/// copy of a definition that was removed to switch a server off.
/// </remarks>
public sealed class KyttoPaths(string? root = null)
{
    public string Root { get; } = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kytto");

    public string Backups => Path.Combine(Root, "Backups");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string MetadataFile => Path.Combine(Root, "metadata.json");
    public string ParkFile => Path.Combine(Root, "parked.json");
    public string LedgerFile => Path.Combine(Root, "digests.json");
    public string ProfilesFile => Path.Combine(Root, "profiles.json");
    public string GatewayRoutesFile => Path.Combine(Root, "gateway-routes.json");
    public string GatewayEventsFile => Path.Combine(Root, "gateway-events.jsonl");

    /// <summary>Private staging area for a verified update package.</summary>
    public string UpdateStagingDirectory => Path.Combine(Root, "Updates");

    /// <summary>The user's profile directory, which every client path hangs off.</summary>
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

public enum Theme
{
    System,
    Light,
    Dark,
}

/// <summary>How an explicitly attached configuration source is scoped.</summary>
public enum ConfigurationScope
{
    Global,
    Profile,
    Workspace,
}

public static class ConfigurationScopes
{
    public static string Raw(this ConfigurationScope scope) => scope switch
    {
        ConfigurationScope.Profile => "profile",
        ConfigurationScope.Workspace => "workspace",
        _ => "global",
    };
}

/// <summary>An explicitly attached file which discovery may read and never write.</summary>
public sealed record CustomConfigSource(
    Guid Id,
    string DisplayName,
    string Path,
    ConfigurationScope Scope = ConfigurationScope.Global,
    string ScopeLabel = "")
{
    [JsonIgnore]
    public ClientKey ClientID => ClientKey.Custom(Id);
}

/// <summary>An actionable settings error which must be shown before saving.</summary>
public sealed class SettingsValidationException(string message) : Exception(message);

/// <summary>
/// The public release data Kytto has accepted from the first-party update manifest.
/// </summary>
/// <remarks>
/// This is deliberately only public release metadata. It contains no installer
/// bytes, configuration, identity or machine information (§6).
/// </remarks>
public sealed record VerifiedUpdate(
    string Version,
    string ReleaseNotesURL,
    string Sha256,
    DateTimeOffset CheckedAt);

/// <summary>Everything §7.6 lets the user change.</summary>
/// <remarks>
/// Defaults are chosen so that a user who never opens Settings has a sensible app:
/// nothing runs on its own, nothing is hidden, and the backup retention matches §6.1.
/// </remarks>
public sealed record KyttoSettings
{
    /// <summary>How many backups to keep per client (§6.1).</summary>
    public int BackupRetention { get; init; } = 20;

    /// <summary>Above this, a server's context cost is called out in the matrix (§7.1).</summary>
    public int TokenWarningThreshold { get; init; } = 20_000;

    // §10: the graphite instrument-panel appearance is the first-run default.
    // A saved choice, including System or Light, still wins afterwards.
    public Theme Theme { get; init; } = Theme.Dark;

    public bool LaunchAtLogin { get; init; }

    public bool TrayEnabled { get; init; } = true;

    /// <summary>
    /// The client the tray item toggles servers in (§7.7). Null means "the one with
    /// the most servers", recomputed as things change, which is right until the
    /// user says otherwise.
    /// </summary>
    public string? TrayClient { get; init; }

    /// <summary>
    /// Per-client replacements for the registry's paths (§7.6), keyed by the
    /// client's raw id.
    /// </summary>
    /// <remarks>
    /// An escape hatch, and one that will be needed: these paths move between
    /// client releases, and a user should not have to wait for a Kytto update to
    /// point it at the right file.
    /// </remarks>
    public Dictionary<string, string> ClientPathOverrides { get; init; } = [];

    /// <summary>
    /// Files the user explicitly attached for discovery. They are deliberately
    /// separate from the five-entry writable client registry.
    /// </summary>
    public IReadOnlyList<CustomConfigSource> CustomConfigSources { get; init; } = [];

    /// <summary>
    /// Whether read-only configuration sources are drawn in the sidebar and matrix.
    /// </summary>
    /// <remarks>
    /// They remain discovered and counted while folded. True is deliberate: a
    /// settings file written before this choice existed preserves the old visible
    /// behaviour.
    /// </remarks>
    public bool ShowsCustomSources { get; init; } = true;

    /// <summary>The remembered width of the shared web sidebar, in CSS pixels.</summary>
    public int SidebarWidth { get; init; } = 196;

    /// <summary>Whether the one-time matrix-write explanation was accepted.</summary>
    /// <remarks>
    /// This is presentation state only. It never bypasses backup, digest or parse
    /// validation in the write pipeline.
    /// </remarks>
    public bool HasConfirmedMatrixWrites { get; init; }

    /// <summary>Set once the user has seen the first-run screen.</summary>
    public bool HasCompletedOnboarding { get; init; }

    /// <summary>The last automatic update-check attempt, whether it succeeded or not.</summary>
    /// <remarks>
    /// Recording this before the request prevents repeated offline launches from
    /// hammering the first-party endpoint. It is separate from the verified result
    /// because a failed attempt is still an attempt.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastAutomaticUpdateAttempt { get; init; }

    /// <summary>The last update manifest accepted by native validation.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VerifiedUpdate? LastVerifiedUpdate { get; init; }

    /// <summary>
    /// How the theme is spelled on the wire. Derived, so it must not be written to
    /// the settings file — a stored copy is a second source of truth that a
    /// hand-edited <c>theme</c> would silently contradict.
    /// </summary>
    [JsonIgnore]
    public string ThemeRaw => Theme switch
    {
        Theme.Light => "light",
        Theme.Dark => "dark",
        _ => "system",
    };

    /// <summary>Keeps a hand-edited settings file from producing nonsense.</summary>
    public KyttoSettings Sanitized() => this with
    {
        BackupRetention = Math.Clamp(BackupRetention, 1, 200),
        TokenWarningThreshold = Math.Clamp(TokenWarningThreshold, 1_000, 200_000),
        SidebarWidth = Math.Clamp(SidebarWidth, 170, 480),
        // An override that is not a full path would silently resolve against
        // whatever directory the app happened to start in.
        ClientPathOverrides = ClientPathOverrides
            .Where(pair => IsUsablePath(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value),
        // A hand-edited settings file must not be able to smuggle an invalid or
        // duplicate dynamic id into discovery. Interactive updates are rejected
        // with an actionable exception before this fallback is reached.
        CustomConfigSources = SanitizedCustomSources(CustomConfigSources),
    };

    public void ValidateCustomConfigSources()
    {
        var sources = CustomConfigSources ?? [];
        var seen = new HashSet<Guid>();
        foreach (var source in sources)
        {
            if (source is null)
            {
                throw new SettingsValidationException("A custom source entry is missing.");
            }
            ValidateCustomConfigSource(source);
            if (!seen.Add(source.Id))
            {
                throw new SettingsValidationException(
                    $"The custom source id {source.Id:D} is used more than once.");
            }
        }
    }

    public static void ValidateCustomConfigSource(CustomConfigSource source)
    {
        if (source.Id == Guid.Empty)
        {
            throw new SettingsValidationException("Choose a valid custom source id.");
        }
        if (string.IsNullOrWhiteSpace(source.DisplayName))
        {
            throw new SettingsValidationException("Enter a name for the custom source.");
        }
        if (!Enum.IsDefined(source.Scope))
        {
            throw new SettingsValidationException(
                "Choose Global, Profile or Workspace for the custom source scope.");
        }
        if (!IsUsablePath(source.Path))
        {
            throw new SettingsValidationException(
                "Choose a fully qualified Windows path for the custom source.");
        }

        string extension;
        try
        {
            extension = System.IO.Path.GetExtension(source.Path);
        }
        catch (ArgumentException)
        {
            throw new SettingsValidationException("Choose a valid custom source path.");
        }
        if (extension is not ".json" and not ".jsonc" and not ".toml"
            && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsValidationException(
                "Choose a .json, .jsonc or .toml configuration file.");
        }
    }

    private static IReadOnlyList<CustomConfigSource> SanitizedCustomSources(
        IReadOnlyList<CustomConfigSource>? sources)
    {
        var result = new List<CustomConfigSource>();
        var seen = new HashSet<Guid>();
        foreach (var source in sources ?? [])
        {
            if (source is null || !seen.Add(source.Id)) continue;
            try
            {
                ValidateCustomConfigSource(source);
            }
            catch (SettingsValidationException)
            {
                continue;
            }
            result.Add(source with
            {
                DisplayName = source.DisplayName.Trim(),
                ScopeLabel = (source.ScopeLabel ?? "").Trim(),
            });
        }
        return result;
    }

    public static bool IsUsablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public string? PathOverride(ClientId client) =>
        ClientPathOverrides.TryGetValue(client.Raw(), out var path) ? path : null;
}

/// <summary>Settings on disk.</summary>
/// <remarks>
/// Written only when something is changed — a first launch that touches nothing
/// leaves no trace (§6.6).
/// </remarks>
public interface ISettingsFileWriter
{
    void Write(string text, string path);
}

public sealed class AtomicSettingsFileWriter : ISettingsFileWriter
{
    public void Write(string text, string path) => AtomicWriter.Write(text, path);
}

public sealed class SettingsTransactionException(
    Exception persistenceFailure,
    Exception rollbackFailure) : Exception(
        $"Saving settings failed: {persistenceFailure.Message} " +
        $"Restoring the previous operating-system state also failed: {rollbackFailure.Message}",
        persistenceFailure)
{
    public Exception RollbackFailure { get; } = rollbackFailure;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();
    private readonly KyttoPaths _paths;
    private readonly ISettingsFileWriter _writer;
    private KyttoSettings? _cached;

    public SettingsStore(KyttoPaths paths, ISettingsFileWriter? writer = null)
    {
        _paths = paths;
        _writer = writer ?? new AtomicSettingsFileWriter();
    }

    public KyttoSettings Current
    {
        get
        {
            lock (_lock)
            {
                if (_cached is not null) return _cached;
                _cached = Load();
                return _cached;
            }
        }
    }

    private KyttoSettings Load()
    {
        try
        {
            var text = File.ReadAllText(_paths.SettingsFile);
            return (JsonSerializer.Deserialize<KyttoSettings>(text, Options) ?? new KyttoSettings())
                .Sanitized();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new KyttoSettings();
        }
    }

    public KyttoSettings Update(
        Func<KyttoSettings, KyttoSettings> change,
        Action<KyttoSettings, KyttoSettings>? beforePersist = null,
        Action<KyttoSettings, KyttoSettings>? rollBackBeforePersist = null)
    {
        lock (_lock)
        {
            // Keep read, transform, write and cache replacement together. Two IPC
            // changes can arrive at once; letting both transform the same previous
            // value loses whichever independent setting the earlier write changed.
            var previous = _cached ??= Load();
            var candidate = change(previous);
            candidate.ValidateCustomConfigSources();
            var updated = candidate.Sanitized();

            // A change that changes nothing must not touch the file: §6.6 says a launch
            // that alters nothing leaves no trace, and rewriting identical bytes would
            // still move the modification time and wake the config watcher. Compared as
            // encoded text rather than by record equality, because `Sanitized` rebuilds
            // `ClientPathOverrides` every time and a fresh dictionary is never `==` to
            // the old one — and the encoded form is exactly the thing being decided about.
            var serialized = JsonSerializer.Serialize(updated, Options);
            if (serialized == JsonSerializer.Serialize(previous, Options)) return previous;

            // Native integration changes first so the persisted setting can never
            // claim an OS action succeeded when it did not. The callback runs under
            // the same lock as the subsequent atomic write, keeping concurrent
            // settings changes from interleaving around it.
            beforePersist?.Invoke(previous, updated);
            try
            {
                // Whichever store creates the data root first decides what every file
                // written into it later inherits, so they all create it the same way.
                KyttoStorage.EnsurePrivateRoot(_paths);
                _writer.Write(serialized, _paths.SettingsFile);
            }
            catch (Exception persistenceFailure)
            {
                if (beforePersist is not null && rollBackBeforePersist is not null)
                {
                    try
                    {
                        rollBackBeforePersist(previous, updated);
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new SettingsTransactionException(
                            persistenceFailure,
                            rollbackFailure);
                    }
                }

                ExceptionDispatchInfo.Capture(persistenceFailure).Throw();
                throw;
            }
            _cached = updated;
            return updated;
        }
    }
}
