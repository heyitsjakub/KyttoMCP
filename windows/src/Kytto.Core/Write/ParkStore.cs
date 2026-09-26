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
        lock (_lock)
        {
            var entries = Load(forWrite: true);
            entries[Key(client, serverName)] = new Entry(
                client, serverName, sourceText, Timestamp.Now);
            Save(entries);
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
        Update(serverId, existing =>
        {
            var updated = existing;
            if (health.Status == HealthStatus.Passed)
            {
                var baseline = existing.ContractTools ??
                    (existing.Health?.Status == HealthStatus.Passed ? existing.Health.Tools : null);
                var changes = baseline is null
                    ? null
                    : ContractGuard.Changes(baseline, health.Tools);
                updated = updated with
                {
                    ContractTools = health.Tools,
                    ContractChanges = changes,
                    ContractChangedAt = changes is { Count: > 0 } ? health.CheckedAt : null,
                };
            }
            return updated with
            {
                Health = health,
                // A failed check says nothing new about weight; retain the last
                // successful measurement rather than making it disappear.
                TokenWeight = tokenWeight ?? existing.TokenWeight,
            };
        });
    }

    /// <summary>Marks a Contract Guard alert read, and rebaselines on what is there now.</summary>
    /// <remarks>
    /// Without this an alert cannot end. <c>ContractChanges</c> is rewritten only by a
    /// check that finds something <em>different</em>, so a change already reviewed and
    /// accepted goes on being reported until the server happens to change again — and
    /// an alert that never clears is one nobody reads the second time. Acknowledging
    /// clears the changes and keeps the tools, so accepting a contract is a statement
    /// about that contract rather than a dismissal.
    /// </remarks>
    public void AcknowledgeContract(string serverId)
    {
        if (For(serverId)?.ContractChanges is not { Count: > 0 }) return;
        Update(serverId, existing => existing with
        {
            ContractChanges = null,
            ContractChangedAt = null,
            ContractTools = existing.Health is { Status: HealthStatus.Passed } health
                ? health.Tools
                : existing.ContractTools,
        });
    }

    public void Update(string serverId, Func<ServerMetadata, ServerMetadata> change)
    {
        lock (_lock)
        {
            var entries = new Dictionary<string, ServerMetadata>(_cached ??= Load());
            entries[serverId] = change(entries.GetValueOrDefault(serverId) ?? new ServerMetadata());
            _cached = entries;

            KyttoStorage.EnsurePrivateRoot(paths);
            AtomicWriter.Write(JsonSerializer.Serialize(entries, Options), paths.MetadataFile);
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
