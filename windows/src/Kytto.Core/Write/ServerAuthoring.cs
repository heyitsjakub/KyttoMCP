using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Toml;

namespace Kytto.Core;

public sealed record AuthoringResult(
    string ServerName,
    /// <summary>One entry per client whose config actually changed.</summary>
    IReadOnlyList<ClientId> Changed,
    IReadOnlyDictionary<ClientId, string> BackupIDs,
    IReadOnlyDictionary<ClientId, string> PathDisplays)
{
    /// <summary>Clients whose <em>switched-off</em> copy was brought into line as well.</summary>
    /// <remarks>
    /// A server switched off in a presence-only client is not in that client's file
    /// at all — Kytto is holding its bytes so switching it back on restores them.
    /// Those bytes are a copy like any other and can drift like one, so unifying
    /// updates them too. Nothing in a config file changed, so this does not ask
    /// anyone to restart.
    /// </remarks>
    public IReadOnlyList<ClientId> ParkedUpdated { get; init; } = [];

    /// <summary>Off copies that could not be brought into line.</summary>
    /// <remarks>
    /// Rare, and reported rather than thrown: the config writes have already
    /// succeeded by this point, and turning that into a failure would be a lie in
    /// the other direction.
    /// </remarks>
    public IReadOnlyList<ClientId> ParkedFailures { get; init; } = [];

    public bool RequiresRestart => Changed.Count > 0;
}

/// <summary>One argument to change in one client's copy of a server.</summary>
/// <param name="Index">Position in that copy's <c>args</c>, counting strings only.</param>
/// <param name="Expected">What the argument says now; the edit is refused if it says otherwise.</param>
public sealed record ArgumentEdit(int Index, string Expected, string Replacement);

public sealed class AuthoringException(string message) : Exception(message)
{
    public static AuthoringException Invalid(IReadOnlyList<ServerDraft.Problem> problems) =>
        new(string.Join(" ", problems.Select(problem => problem.Message)));

    public static AuthoringException NotEditable(string name) =>
        new($"\"{name}\" is a Claude Desktop extension. Extensions are installed " +
            "bundles — change them in Claude Desktop, not here.");

    public static AuthoringException ReadOnlySource(string name) =>
        new($"\"{name}\" exists only in a read-only custom source. Kytto never changes that file.");

    public static AuthoringException NoSourceForClient(ClientId client) =>
        new($"No editable configuration is known for {client.Raw()}.");

    public static AuthoringException NotFound(string name) =>
        new($"\"{name}\" is not in any client configuration.");

    public static AuthoringException NoClients() =>
        new("Choose at least one client to add this server to.");
}

/// <summary>Adding, editing and removing servers (§7.2, §7.5).</summary>
/// <remarks>
/// Writes go through the same <see cref="ConfigWriter"/> pipeline as toggles, so
/// the safety rules hold here too: refuse if the file moved under us, back up,
/// splice, write atomically. The only new thing is that a definition now has to be
/// <em>rendered</em>, and rendered to match the file it lands in.
/// </remarks>
public sealed class ServerAuthoring(
    string home,
    ConfigWriter writer,
    ParkStore parkStore,
    DigestLedger ledger,
    IReadOnlyList<ClientDescriptor>? descriptors = null,
    IReadOnlyDictionary<string, string>? pathOverrides = null)
{
    private readonly ClientPathResolver _resolver = new(home, pathOverrides);
    private readonly IReadOnlyList<ClientDescriptor> _descriptors = descriptors ?? ClientRegistry.All;

    // MARK: - Create

    public AuthoringResult Create(
        ServerDraft draft,
        IReadOnlyList<ClientId> clients,
        IReadOnlyList<Server> existing)
    {
        if (clients.Count == 0) throw AuthoringException.NoClients();
        var problems = draft.Validate(TakenNames(clients, existing));
        if (problems.Count > 0) throw AuthoringException.Invalid(problems);

        return Write(draft, clients, oldName: null);
    }

    // MARK: - Update

    /// <summary>Applies a draft everywhere the server currently lives.</summary>
    /// <remarks>
    /// Editing is not per-client. A server configured in three clients is one row in
    /// the matrix, and changing its command in only one of them is how the three
    /// quietly drift apart — which is the problem this app exists to solve.
    /// </remarks>
    public AuthoringResult Update(
        ServerDraft draft,
        string originalName,
        Server server,
        IReadOnlyList<Server> existing)
    {
        if (server.IsBundled) throw AuthoringException.NotEditable(server.Name);

        var clients = ClientsHolding(server);
        if (clients.Count == 0 && server.DefinitionsByClient.Values.Any(copy => copy.IsReadOnly))
        {
            throw AuthoringException.ReadOnlySource(server.Name);
        }
        var problems = draft.Validate(TakenNames(clients, existing), allowing: originalName);
        if (problems.Count > 0) throw AuthoringException.Invalid(problems);

        // A rename is a delete plus an add, because the name is the key.
        var renamedFrom = Server.Identity(originalName) == Server.Identity(draft.TrimmedName)
            ? null
            : originalName;
        return Write(draft, clients, renamedFrom);
    }

    // MARK: - Unify

    /// <summary>Makes the listed clients' copies of a server match one draft.</summary>
    /// <remarks>
    /// The sibling of <see cref="Update"/>, for the case <c>Update</c> cannot
    /// express. <c>Update</c> applies one edit everywhere because a server is one
    /// row; this applies one client's <em>existing</em> definition to the others,
    /// which is what resolving drift means. Neither is per-client editing — the
    /// point of both is that the copies end up the same.
    /// </remarks>
    /// <param name="clients">
    /// Who to bring into line. The client the definition came from does not need to
    /// be in the list and does no harm if it is.
    /// </param>
    public AuthoringResult Unify(ServerDraft draft, Server server, IReadOnlyList<ClientId> clients)
    {
        // No rename, so no name can be taken by anything but this server itself.
        var problems = draft.Validate(new HashSet<string>(StringComparer.Ordinal));
        if (problems.Count > 0) throw AuthoringException.Invalid(problems);

        foreach (var clientId in clients)
        {
            if (server.DefinitionsByClient.GetValueOrDefault(clientId)?.IsBundled == true)
            {
                throw AuthoringException.NotEditable(server.Name);
            }
        }

        // A parked copy is not in the file. Writing it there would put the definition
        // back, and for a presence-only client putting the definition back is
        // precisely what switching a server *on* means — so unifying would silently
        // re-enable it. Those copies are updated where they actually live instead.
        var parked = new List<ClientId>();
        var inFile = new List<ClientId>();
        foreach (var clientId in clients)
        {
            if (parkStore.Parked(clientId, server.Name) is not null) parked.Add(clientId);
            else inFile.Add(clientId);
        }

        var result = inFile.Count == 0
            ? new AuthoringResult(draft.TrimmedName, [], new Dictionary<ClientId, string>(),
                new Dictionary<ClientId, string>())
            : Write(draft, inFile, oldName: null);

        var parkedUpdated = new List<ClientId>();
        var parkedFailures = new List<ClientId>();
        foreach (var clientId in parked)
        {
            // Parked text is spelled in the format of the client it came from. Only
            // JSON clients park — a Codex server keeps its definition and its
            // `enabled = false` — but the store is per-client and nothing enforces
            // that, so an unexpected format is left alone rather than overwritten
            // with the wrong syntax.
            var format = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap?.Format;
            if (format != ConfigFormat.Json)
            {
                parkedFailures.Add(clientId);
                continue;
            }

            try
            {
                parkStore.Park(clientId, server.Name, draft.Definition(baseIndent: "", unit: "  "));
                parkedUpdated.Add(clientId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                parkedFailures.Add(clientId);
            }
        }

        return result with { ParkedUpdated = parkedUpdated, ParkedFailures = parkedFailures };
    }

    // MARK: - One argument

    /// <summary>Replaces a single <c>args</c> element in each listed client's copy, and nothing else.</summary>
    /// <remarks>
    /// <para>
    /// The narrow sibling of <see cref="Update"/>: that re-renders the whole
    /// definition, which is right for an edit the user typed and wrong for a repair
    /// that changes one token (§7.10). Here every file sees one string literal
    /// spliced, inside the same transaction as any other authoring write — digest
    /// check, backup, atomic write, rollback (§6). Every target is prepared before
    /// anything is written, so an argument that no longer says what was previewed
    /// fails the whole edit with nothing written.
    /// </para>
    /// <para>
    /// A switched-off copy Kytto is holding is patched too, where it lives, or
    /// switching it back on would restore the old argument — and writing it into
    /// the file instead would switch it on.
    /// </para>
    /// </remarks>
    public AuthoringResult ReplaceArgument(
        Server server,
        IReadOnlyDictionary<ClientId, ArgumentEdit> edits)
    {
        if (edits.Count == 0) throw AuthoringException.NotFound(server.Name);
        foreach (var clientId in edits.Keys)
        {
            var copy = server.DefinitionsByClient.GetValueOrDefault(clientId);
            if (copy?.IsBundled == true) throw AuthoringException.NotEditable(server.Name);
            if (copy?.IsReadOnly == true) throw AuthoringException.ReadOnlySource(server.Name);
        }

        var parked = new List<ClientId>();
        var inFile = new List<ClientId>();
        foreach (var clientId in edits.Keys.OrderBy(client => client.Raw(), StringComparer.Ordinal))
        {
            if (parkStore.Parked(clientId, server.Name) is not null) parked.Add(clientId);
            else inFile.Add(clientId);
        }

        var serverWrites = new List<ClientWrite>();
        foreach (var clientId in inFile)
        {
            var edit = edits[clientId];
            var source = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap
                ?? throw AuthoringException.NoSourceForClient(clientId);

            var path = _resolver.ResolveServerMap(source.File, clientId);
            var pathDisplay = _resolver.DisplayServerMap(source.File, clientId);
            var expecting = ledger.DigestFor(path);

            var prepared = source.Format == ConfigFormat.Json
                ? writer.Prepare<JsonDocument>(path, clientId, pathDisplay, expecting,
                    document => document.ReplacingString(
                        [source.ServersKey, NameOf(server, document, source.ServersKey), "args"],
                        edit.Index,
                        edit.Expected,
                        edit.Replacement))
                : writer.Prepare<TomlDocument>(path, clientId, pathDisplay, expecting,
                    document => document.SettingServerArgument(
                        edit.Index,
                        edit.Expected,
                        edit.Replacement,
                        NameOf(server, document, source.ServersKey),
                        source.ServersKey));
            serverWrites.Add(new ClientWrite(clientId, prepared));
        }

        IReadOnlyList<WriteReceipt> receipts = serverWrites.Count == 0
            ? []
            : writer.Commit(serverWrites.Select(item => item.Write).ToArray());
        RecordDigests(serverWrites, receipts);
        var result = Result(server.Name, serverWrites, receipts);

        // Same stance as `Unify`: the config writes have landed, so a held copy
        // that cannot be patched is reported rather than thrown.
        var parkedUpdated = new List<ClientId>();
        var parkedFailures = new List<ClientId>();
        foreach (var clientId in parked)
        {
            var edit = edits[clientId];
            var entry = parkStore.Parked(clientId, server.Name);
            var format = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap?.Format;
            if (entry is null || format != ConfigFormat.Json)
            {
                parkedFailures.Add(clientId);
                continue;
            }

            try
            {
                var text = JsonDocument.Parse(entry.SourceText).ReplacingString(
                    ["args"], edit.Index, edit.Expected, edit.Replacement);
                parkStore.Park(clientId, entry.ServerName, text);
                parkedUpdated.Add(clientId);
            }
            catch (Exception error) when (
                error is JsonEditException or JsonParseException or IOException or UnauthorizedAccessException)
            {
                parkedFailures.Add(clientId);
            }
        }

        return result with { ParkedUpdated = parkedUpdated, ParkedFailures = parkedFailures };
    }

    // MARK: - Delete

    public AuthoringResult Delete(Server server)
    {
        var clients = ClientsHolding(server);
        if (clients.Count == 0 && server.DefinitionsByClient.Values.Any(copy => copy.IsReadOnly))
        {
            throw AuthoringException.ReadOnlySource(server.Name);
        }
        return Remove(server, clients);
    }

    /// <summary>Takes a server out of one client and leaves the others holding it.</summary>
    /// <remarks>
    /// <para>
    /// The matrix cannot express this: a cell is a two-position switch over a
    /// three-state column, and switching off is <c>disabled</c> in every client by
    /// design — the definition, or Kytto's parked copy of it, is kept precisely so
    /// switching back on restores what was there rather than rebuilding it. Getting
    /// a cell back to <c>absent</c> is therefore a different act from switching it
    /// off, and this is it (§7.5).
    /// </para>
    /// <para>
    /// Everything that could bring the row back has to go, or the next refresh
    /// resurrects it as "disabled": the definition, the parked copy, and the
    /// deny-list entry that would otherwise name a server no file mentions.
    /// </para>
    /// </remarks>
    public AuthoringResult RemoveFrom(Server server, ClientId clientId)
    {
        if (server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) == Enablement.Absent)
        {
            throw AuthoringException.NotFound(server.Name);
        }
        return Remove(server, [clientId]);
    }

    private AuthoringResult Remove(Server server, IReadOnlyList<ClientId> clients)
    {
        if (server.IsBundled) throw AuthoringException.NotEditable(server.Name);
        if (clients.Count == 0) throw AuthoringException.NotFound(server.Name);

        var serverWrites = new List<ClientWrite>();

        foreach (var clientId in clients)
        {
            var source = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap;
            if (source is null) continue;

            var path = _resolver.ResolveServerMap(source.File, clientId);
            var pathDisplay = _resolver.DisplayServerMap(source.File, clientId);
            var expecting = ledger.DigestFor(path);

            var prepared = source.Format == ConfigFormat.Json
                ? writer.Prepare<JsonDocument>(path, clientId, pathDisplay, expecting,
                    document => document.RemovingMember(server.Name, [source.ServersKey]))
                : writer.Prepare<TomlDocument>(path, clientId, pathDisplay, expecting,
                    document => document.RemovingServer(
                        NameOf(server, document, source.ServersKey),
                        source.ServersKey));
            serverWrites.Add(new ClientWrite(clientId, prepared));
        }

        var allWrites = serverWrites.Concat(PrepareDenyListEntries(server.Name, clients)).ToArray();
        var receipts = writer.Commit(allWrites.Select(item => item.Write).ToArray());
        RecordDigests(allWrites, receipts);

        // Side state changes only after every config target committed. A failed
        // transaction therefore leaves discovery seeing precisely the old world.
        foreach (var item in serverWrites)
        {
            parkStore.Unpark(item.ClientID, server.Name);
        }

        return Result(server.Name, serverWrites, receipts);
    }

    // MARK: - Shared

    private AuthoringResult Write(
        ServerDraft draft,
        IReadOnlyList<ClientId> clients,
        string? oldName)
    {
        var serverWrites = new List<ClientWrite>();

        foreach (var clientId in clients)
        {
            var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId)
                ?? throw AuthoringException.NoSourceForClient(clientId);
            var source = descriptor.EditableServerMap
                ?? throw AuthoringException.NoSourceForClient(clientId);

            var path = _resolver.ResolveServerMap(source.File, clientId);
            var pathDisplay = _resolver.DisplayServerMap(source.File, clientId);
            var expecting = ledger.DigestFor(path);

            var prepared = source.Format == ConfigFormat.Json
                ? writer.Prepare<JsonDocument>(path, clientId, pathDisplay, expecting,
                    document => WriteJson(document, draft, source.ServersKey, oldName))
                : writer.Prepare<TomlDocument>(path, clientId, pathDisplay, expecting,
                    document => WriteToml(document, draft, source, oldName));
            serverWrites.Add(new ClientWrite(clientId, prepared));
        }

        var allWrites = oldName is null
            ? serverWrites.ToArray()
            : serverWrites.Concat(PrepareDenyListEntries(oldName, clients)).ToArray();
        var receipts = writer.Commit(allWrites.Select(item => item.Write).ToArray());
        RecordDigests(allWrites, receipts);

        if (oldName is not null)
        {
            foreach (var item in serverWrites)
            {
                parkStore.Unpark(item.ClientID, oldName);
            }
        }

        return Result(draft.TrimmedName, serverWrites, receipts);
    }

    private static string WriteJson(
        JsonDocument document,
        ServerDraft draft,
        string serversKey,
        string? oldName)
    {
        var text = oldName is not null
            ? document.RemovingMember(oldName, [serversKey])
            : document.SourceText;
        var current = JsonDocument.Parse(text);

        // Indent to match the siblings this will sit beside, or the map itself when
        // it is the first entry.
        var unit = IndentStyle.Detect(current.SourceText).Text;
        var map = current.ValueAt(serversKey);
        string baseIndent;
        if (map?.Members?.FirstOrDefault() is { } first)
        {
            baseIndent = current.LineIndent(first.Span.Start);
        }
        else if (map is not null)
        {
            baseIndent = current.LineIndent(map.Span.Start) + unit;
        }
        else
        {
            baseIndent = unit;
        }

        return current.SettingMember(
            draft.TrimmedName,
            [serversKey],
            draft.Definition(
                baseIndent,
                unit,
                current.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"));
    }

    private static string WriteToml(
        TomlDocument document,
        ServerDraft draft,
        ConfigSource.ServerMap source,
        string? oldName)
    {
        // A server that is switched off must stay switched off. The whole block is
        // rewritten, so a flag nobody carried across would silently turn it back on.
        var flagKey = (source.Enablement as EnablementStrategy.InlineFlag)?.Key;
        var previousName = oldName ?? draft.TrimmedName;
        var wasDisabled = flagKey is not null
            && document.Table(source.ServersKey, previousName)?.Value(flagKey)?.BoolValue == false;

        var text = oldName is not null
            ? document.RemovingServer(oldName, source.ServersKey)
            : document.SourceText;

        return TomlDocument.Parse(text).SettingServer(
            draft.TrimmedName,
            source.ServersKey,
            draft.TomlBlock(source.ServersKey, wasDisabled ? false : null));
    }

    /// <summary>Removes a name from any client's deny list.</summary>
    /// <remarks>
    /// Without this, deleting a disabled server leaves a dangling entry, and
    /// recreating it later would come back mysteriously switched off.
    /// </remarks>
    private IReadOnlyList<ClientWrite> PrepareDenyListEntries(
        string name,
        IReadOnlyList<ClientId> clients)
    {
        var writes = new List<ClientWrite>();
        foreach (var clientId in clients)
        {
            var source = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap;
            if (source?.Enablement is not EnablementStrategy.DenyList deny) continue;

            var path = _resolver.Resolve(deny.File);
            if (!File.Exists(path)) continue;

            var prepared = writer.Prepare<JsonDocument>(
                path, clientId, deny.File.DisplayString(), ledger.DigestFor(path),
                document => document.RemovingElements([deny.Key], element =>
                {
                    var entry = element[deny.NameField]?.StringValue ?? element.StringValue;
                    return entry is not null && Server.Identity(entry) == Server.Identity(name);
                }));
            writes.Add(new ClientWrite(clientId, prepared));
        }
        return writes;
    }

    private static AuthoringResult Result(
        string serverName,
        IReadOnlyList<ClientWrite> serverWrites,
        IReadOnlyList<WriteReceipt> allReceipts)
    {
        var receipts = allReceipts.Take(serverWrites.Count).ToArray();
        var changed = serverWrites
            .Select((item, index) => (item.ClientID, receipts[index].DidWrite))
            .Where(item => item.DidWrite)
            .Select(item => item.ClientID)
            .ToArray();
        var backupIds = serverWrites
            .Select((item, index) => (item.ClientID, receipts[index].BackupID))
            .Where(item => item.BackupID is not null)
            .ToDictionary(item => item.ClientID, item => item.BackupID!);
        var pathDisplays = serverWrites
            .Select((item, index) => (item.ClientID, receipts[index].PathDisplay))
            .ToDictionary(item => item.ClientID, item => item.PathDisplay);
        return new AuthoringResult(serverName, changed, backupIds, pathDisplays);
    }

    private void RecordDigests(
        IReadOnlyList<ClientWrite> writes,
        IReadOnlyList<WriteReceipt> receipts)
    {
        for (var index = 0; index < writes.Count; index++)
        {
            ledger.Record(writes[index].Write.Path, receipts[index].Digest);
        }
    }

    private sealed record ClientWrite(ClientId ClientID, PreparedConfigWrite Write);

    /// <summary>The name this server goes by in this particular file.</summary>
    /// <remarks>
    /// The matrix merges rows by identity, so a server called <c>GitHub</c> in one
    /// client and <c>github</c> in another is one row — and removing it from the
    /// second has to use the spelling that file actually contains.
    /// </remarks>
    private static string NameOf(Server server, TomlDocument document, string key) =>
        document.ServerNames(key).FirstOrDefault(name => Server.Identity(name) == server.Id)
        ?? server.Name;

    /// <summary>The same, for a JSON server map.</summary>
    private static string NameOf(Server server, JsonDocument document, string key) =>
        document.ValueAt(key)?.Keys.FirstOrDefault(name => Server.Identity(name) == server.Id)
        ?? server.Name;

    private static IReadOnlyList<ClientId> ClientsHolding(Server server) =>
        server.EnabledIn
            .Where(pair => pair.Value != Enablement.Absent && pair.Key.BuiltIn is not null)
            .Select(pair => pair.Key.BuiltIn!.Value)
            .OrderBy(client => client.Raw(), StringComparer.Ordinal)
            .ToArray();

    private static HashSet<string> TakenNames(
        IReadOnlyList<ClientId> clients,
        IReadOnlyList<Server> existing)
    {
        var wanted = clients.ToHashSet();
        return existing
            .Where(server => server.EnabledIn.Any(pair =>
                pair.Key.BuiltIn is { } builtIn
                && wanted.Contains(builtIn)
                && pair.Value != Enablement.Absent))
            .Select(server => server.Id)
            .ToHashSet(StringComparer.Ordinal);
    }
}
