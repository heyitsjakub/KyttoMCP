using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core;

/// <summary>Definitions removed from a client only in order to switch them off.</summary>
/// <remarks>
/// <para>
/// Cursor, VS Code and Claude Desktop's manual <c>mcpServers</c> have no "disabled"
/// flag — Cursor and VS Code keep that state in their own internal storage, which
/// Kytto will not write to. So switching a server off there means deleting it, and
/// deleting it is only acceptable if switching back restores exactly what was there.
/// </para>
/// <para>
/// What is stored is the definition's <strong>original source text</strong>, not a
/// re-serialization of the model. Turning a server off and on again gives back the
/// same bytes, comments and formatting included.
/// </para>
/// </remarks>
public sealed class ParkStore(KyttoPaths paths)
{
    private readonly Lock _lock = new();

    public sealed record Entry(
        ClientId ClientID,
        string ServerName,
        /// <summary>Verbatim source of the server's definition.</summary>
        string SourceText,
        DateTimeOffset ParkedAt);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string Key(ClientId client, string serverName) =>
        $"{client.Raw()}::{Server.Identity(serverName)}";

    public void Park(ClientId client, string serverName, string sourceText)
    {
        lock (MutationCoordinator.Gate)
        lock (_lock)
        {
            var entries = Load(forWrite: true);
            entries[Key(client, serverName)] = new Entry(
                client, serverName, sourceText, Timestamp.Now);
            Save(entries);
        }
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> when the store cannot be read as it
    /// stands, without changing anything.
    /// </summary>
    /// <remarks>
    /// <see cref="Parked"/> shows a damaged store as empty. A caller about to rely on
    /// "nothing is parked under this name" — portable setup checking for a collision
    /// — has to know the difference, or a definition Kytto is the only keeper of is
    /// overwritten by an import that could not see it (§6.4).
    /// </remarks>
    public void VerifyReadable()
    {
        lock (_lock)
        {
            _ = Load(forWrite: true);
        }
    }

    public Entry? Parked(ClientId client, string serverName)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(Key(client, serverName));
        }
    }

    public void Unpark(ClientId client, string serverName)
    {
        lock (MutationCoordinator.Gate)
        lock (_lock)
        {
            var entries = Load(forWrite: true);
            if (!entries.Remove(Key(client, serverName))) return;
            Save(entries);
        }
    }

    public IReadOnlyList<Entry> All()
    {
        lock (_lock)
        {
            return Load().Values.OrderByDescending(entry => entry.ParkedAt).ToArray();
        }
    }

    // MARK: - Storage

    private Dictionary<string, Entry> Load(bool forWrite = false)
    {
        if (!File.Exists(paths.ParkFile)) return [];
        try
        {
            var text = File.ReadAllText(paths.ParkFile);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(text, Options) ?? [];
        }
        catch (JsonException error) when (forWrite)
        {
            // A parked definition may be the only copy left. Never interpret a
            // damaged store as empty and overwrite it with the next toggle.
            throw new InvalidDataException(
                "The parked-definition store is damaged; it was left untouched.",
                error);
        }
        catch (Exception error) when (
            forWrite && error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The parked-definition store could not be read; it was left untouched.",
                error);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        // A parked definition is the server's source text verbatim, `env` and all, so
        // this store is as sensitive as a backup and is kept the same way — including
        // when an earlier version wrote it into a looser folder (§6).
        KyttoStorage.EnsurePrivateRoot(paths);
        // Before the write, not after: the writer keeps the destination's descriptor,
        // so a store an earlier version left readable would hand that same descriptor
        // to every rewrite. Tightening first also means the new bytes never exist
        // under the old one.
        KyttoStorage.TightenOwnFile(paths.ParkFile);
        AtomicWriter.Write(JsonSerializer.Serialize(entries, Options), paths.ParkFile);
    }
}

/// <summary>
/// Everything Kytto has measured, keyed by server id.
/// </summary>
/// <remarks>
/// Config files stay authoritative for what is <em>configured</em> (§5); this is
/// the measured half — health results and token weights — and it lives here so a
/// relaunch does not forget what a check already established.
/// </remarks>
public sealed class MetadataStore(KyttoPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();
    private Dictionary<string, ServerMetadata>? _cached;

    public IReadOnlyDictionary<string, ServerMetadata> All()
    {
        lock (_lock)
        {
            return _cached ??= Load();
        }
    }

    public ServerMetadata? For(string serverId) => All().GetValueOrDefault(serverId);

    /// <summary>Records one check and advances Contract Guard only on success (§7.10).</summary>
    public void Record(string serverId, HealthResult health, TokenWeight? tokenWeight)
    {
        Mutate(entries =>
        {
            var existing = entries.GetValueOrDefault(serverId) ?? new ServerMetadata();
            // Two checks of one server can finish out of order (a single check and
            // "Check all"). The one that started later describes the server better.
            if (existing.Health is { } previous && previous.CheckedAt > health.CheckedAt) return false;

            var updated = existing;
            if (health.Status == HealthStatus.Passed)
            {
                var baseline = existing.ContractTools ??
                    (existing.Health?.Status == HealthStatus.Passed ? existing.Health.Tools : null);
                if (baseline is not null)
                {
                    // A baseline from before schemas and annotations were recorded
                    // adopts them from this check instead of reporting each tool as
                    // changed.
                    baseline = ContractGuard.UpgradingLegacyBaseline(baseline, health.Tools);
                    // Every check is measured against the contract the user last
                    // reviewed, so changes add up until they are acknowledged. Only
                    // AcknowledgeContract moves the baseline (§7.10); a check that
                    // moved it would let an unreviewed change vanish on the next run.
                    var changes = ContractGuard.Changes(baseline, health.Tools);
                    var wasPending = existing.ContractChanges is { Count: > 0 };
                    updated = updated with
                    {
                        ContractTools = baseline,
                        ContractChanges = changes,
                        // A repeat check is not a new change: keep when it was first seen.
                        ContractChangedAt = changes.Count == 0
                            ? null
                            : (wasPending ? existing.ContractChangedAt : null) ?? health.CheckedAt,
                    };
                }
                else
                {
                    updated = updated with { ContractTools = health.Tools };
                }
                updated = updated with { LastPassingTools = health.Tools };
            }
            entries[serverId] = updated with
            {
                Health = health,
                // A failed check says nothing new about weight; retain the last
                // successful measurement rather than making it disappear.
                TokenWeight = tokenWeight ?? existing.TokenWeight,
            };
            return true;
        });
    }

    /// <summary>Marks the current tool contract as reviewed (§7.10).</summary>
    /// <remarks>
    /// This and the first passing check are the only things that move the baseline.
    /// Every check compares against <c>ContractTools</c>, so a change keeps being
    /// reported until it is reviewed here (or the server reverts it). Acknowledging
    /// makes the last passing contract — the one shown in the diff — the new
    /// baseline, and the next real change is measured against it.
    /// </remarks>
    public void AcknowledgeContract(string serverId)
    {
        Mutate(entries =>
        {
            if (entries.GetValueOrDefault(serverId) is not { ContractChanges.Count: > 0 } existing)
                return false;
            entries[serverId] = existing with
            {
                ContractChanges = null,
                ContractChangedAt = null,
                // The last passing contract, not the last check: a check that failed
                // after the diff was shown must not leave the old baseline in place.
                ContractTools = existing.KnownTools,
            };
            return true;
        });
    }

    /// <summary>Carries everything remembered about a server to its new name.</summary>
    /// <remarks>
    /// Metadata is keyed by name, so without this a rename would drop the Contract
    /// Guard baseline and any unreviewed alert — the next check under the new name
    /// would adopt whatever the server now says — and leave the old entry for a later
    /// server that reuses the name.
    /// </remarks>
    public void Rename(string fromId, string toId)
    {
        if (string.Equals(fromId, toId, StringComparison.Ordinal)) return;
        Mutate(entries =>
        {
            if (!entries.Remove(fromId, out var entry)) return false;
            entries[toId] = entry;
            return true;
        });
    }

    /// <summary>Drops everything remembered about a deleted server.</summary>
    /// <remarks>So a later server reusing the name does not inherit a stale red dot.</remarks>
    public void Forget(string serverId) =>
        Mutate(entries => entries.Remove(serverId));

    public void Update(string serverId, Func<ServerMetadata, ServerMetadata> change) =>
        Mutate(entries =>
        {
            entries[serverId] = change(entries.GetValueOrDefault(serverId) ?? new ServerMetadata());
            return true;
        });

    /// <summary>Applies <paramref name="change"/> and saves if it reports a change.</summary>
    private void Mutate(Func<Dictionary<string, ServerMetadata>, bool> change)
    {
        lock (MutationCoordinator.Gate)
        lock (_lock)
        {
            var entries = new Dictionary<string, ServerMetadata>(LoadForMutation());
            if (!change(entries)) return;

            KyttoStorage.EnsurePrivateRoot(paths);
            AtomicWriter.Write(JsonSerializer.Serialize(entries, Options), paths.MetadataFile);
            _cached = entries;
        }
    }

    /// <summary>
    /// A malformed store is not an empty store (as in <see cref="ParkStore"/>).
    /// </summary>
    /// <remarks>
    /// Reading screens may show nothing, but a write must stop rather than replace
    /// every server's Contract Guard baseline and pending alert with an empty file.
    /// Read from disk rather than the cache, so a store that became readable again
    /// is not overwritten with what an earlier failed read made of it.
    /// </remarks>
    private Dictionary<string, ServerMetadata> LoadForMutation()
    {
        if (!File.Exists(paths.MetadataFile)) return [];
        try
        {
            var text = File.ReadAllText(paths.MetadataFile);
            return JsonSerializer.Deserialize<Dictionary<string, ServerMetadata>>(text, Options) ?? [];
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                $"Kytto could not read its own measurements file ({Path.GetFileName(paths.MetadataFile)}), " +
                "so it left it untouched instead of overwriting it. Move the file out of Kytto's " +
                "data folder to start fresh.",
                error);
        }
    }

    /// <summary>
    /// What is on disk right now, or <see cref="InvalidDataException"/> when it cannot
    /// be read — never a cache, and never an unreadable store passed off as empty.
    /// </summary>
    /// <remarks>
    /// Imported contract references are compared with what the last local check
    /// recorded. That comparison must see a check another store instance wrote a
    /// moment ago, and must fail closed rather than conclude "no check" from a file
    /// it could not read (§7.10).
    /// </remarks>
    internal static IReadOnlyDictionary<string, ServerMetadata> ReadStrict(KyttoPaths paths)
    {
        if (!File.Exists(paths.MetadataFile)) return new Dictionary<string, ServerMetadata>();
        try
        {
            var text = File.ReadAllText(paths.MetadataFile);
            return JsonSerializer.Deserialize<Dictionary<string, ServerMetadata>>(text, Options) ?? [];
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Kytto's measurements file could not be read.", error);
        }
    }

    private Dictionary<string, ServerMetadata> Load()
    {
        try
        {
            var text = File.ReadAllText(paths.MetadataFile);
            return JsonSerializer.Deserialize<Dictionary<string, ServerMetadata>>(text, Options) ?? [];
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
