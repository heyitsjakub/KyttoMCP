using System.IO;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Portable;
using Kytto.Core.Settings;

namespace Kytto.App;

/// <summary>
/// Portable setup: native file selection, native secret inputs, and the gate that
/// keeps an imported contract fingerprint from ever becoming trust (§7.13).
/// </summary>
internal sealed partial class AppModel
{
    /// <summary>
    /// Kept for as long as settings stay as they are. It holds the previews' plans —
    /// and with them the values typed into the native dialog — so changing the client
    /// paths or sources drops it, and every plan prepared against the old ones with it.
    /// </summary>
    private PortableSetupService? _portable;

    internal PortableSetupService PortableSetup() => _portable ??= new PortableSetupService(
        home: _home,
        writer: new ConfigWriter(Backups),
        parkStore: _parkStore,
        ledger: _ledger,
        paths: _paths,
        profileStore: _profiles,
        pathOverrides: Settings.ClientPathOverrides,
        // A file the user attached for reading is theirs, and an export is not allowed
        // to land on it any more than on a live client config.
        additionalProtectedPaths: Settings.CustomConfigSources.Select(source => source.Path).ToArray(),
        // What the matrix shows a client under each name, plugins and extensions included:
        // a config entry beside one of those would shadow it (§4).
        existingServers: () => _current.Servers);

    /// <summary>Writes the setup to a file the native dialog chose.</summary>
    internal PortableSetupSummary ExportPortableSetup(string file, bool includeReviewedContracts) =>
        PortableSetup().Export(file, _current.Servers, includeReviewedContracts);

    internal PortableSetupSummary InspectPortableSetup(string file) => PortableSetup().Inspect(file);

    /// <summary>
    /// The clients an import may write to: built-in, writable and installed — a client
    /// that is installed but has never been configured included, because the import is
    /// what creates its file.
    /// </summary>
    internal IReadOnlyList<(ClientId Id, string DisplayName)> PortableTargets() => _current.Clients
        .Where(client => !client.IsReadOnly && client.IsInstalled)
        .Select(client => (Id: client.Id.BuiltIn, client.DisplayName))
        .Where(client => client.Id is not null)
        .Select(client => (client.Id!.Value, client.DisplayName))
        .ToArray();

    /// <summary>
    /// Always an import that leaves every imported server switched off: this is the
    /// only way the application makes a plan, and it has no parameter for anything else.
    /// </summary>
    internal PortableImportPreview PreviewPortableSetup(
        string file,
        IReadOnlyList<ClientId> targetClients,
        IReadOnlyDictionary<string, string> values)
    {
        var allowed = PortableTargets().Select(target => target.Id).ToHashSet();
        if (targetClients.Any(client => !allowed.Contains(client)))
        {
            throw PortableSetupException.Invalid("choose distinct writable target clients");
        }
        return PortableSetup().Preview(file, targetClients, values);
    }

    internal PortableImportResult ApplyPortableSetup(string previewId)
    {
        var result = PortableSetup().Apply(previewId);
        Reload();
        return result;
    }

    internal void DiscardPortableSetup(string previewId) => _portable?.Discard(previewId);

    /// <summary>
    /// A plan names destinations resolved from the client paths and sources of the
    /// moment it was made. A change to either makes every plan one for a world that is
    /// gone; a change of theme or sidebar width does not.
    /// </summary>
    /// <summary>
    /// Whether a settings change moves where Kytto reads or watches clients from: a path
    /// override or a custom source. Only those need discovery run again (audit W-07).
    /// </summary>
    private static bool ReadTargetsChanged(KyttoSettings previous, KyttoSettings updated)
    {
        var sameOverrides = previous.ClientPathOverrides.Count == updated.ClientPathOverrides.Count &&
            previous.ClientPathOverrides.All(pair =>
                updated.ClientPathOverrides.TryGetValue(pair.Key, out var path) && path == pair.Value);
        return !sameOverrides || !previous.CustomConfigSources.SequenceEqual(updated.CustomConfigSources);
    }

    private void ResetPortableSetupIfReconfigured(KyttoSettings previous, KyttoSettings updated)
    {
        if (!ReadTargetsChanged(previous, updated)) return;

        _portable?.DiscardAll();
        _portable = null;
    }

    private void DisposePortableSetup() => _portable?.DiscardAll();

    // MARK: - Imported contract references

    /// <summary>
    /// The pending fingerprints and what the latest local check says about each. A
    /// store that cannot be read is a safe message and nothing else: it is neither
    /// deleted nor repaired on the way (§7.13).
    /// </summary>
    internal (IReadOnlyList<PortablePendingContractSummary> Entries, string? Error) PendingContractImports()
    {
        try
        {
            return (PortableSetup().PendingContractSummary(), null);
        }
        catch (Exception error) when (
            error is PortableSetupException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (
                [],
                "Imported contract fingerprints could not be read. The local trusted baseline was left unchanged.");
        }
    }

    /// <summary>
    /// Acknowledges a local Contract Guard alert, and retires an imported reference
    /// for the same server — in that order of care, as one step.
    /// </summary>
    /// <remarks>
    /// Both stores are read, and a fresh local check is required, before either is
    /// written. A store that cannot be read, or a reference nothing has been checked
    /// against yet, stops the acknowledgement with the alert and the baseline exactly
    /// as they were; it never half-acknowledges (§7.10).
    /// </remarks>
    private void AcknowledgeWithPendingReview(string serverId, Action acknowledgeBaseline) =>
        PortableSetup().ReviewPending(serverId, acknowledgeBaseline);
}
