using Kytto.Core.Clients;
using Kytto.Core;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Toml;

namespace Kytto.Core;

public sealed record ToggleResult(
    ClientId ClientID,
    string ServerName,
    bool Enabled,
    /// <summary>
    /// Clients hold their configuration in memory and only pick changes up on
    /// restart. Kytto states that plainly rather than pretending otherwise (§7.1).
    /// </summary>
    bool RequiresRestart,
    string? BackupID,
    string PathDisplay,
    /// <summary>
    /// True when switching off had to remove the definition because the client has
    /// no disabled state. The definition is parked and fully restorable.
    /// </summary>
    bool WasParked);

public sealed class ToggleException(string message) : Exception(message)
{
    public static ToggleException NoSourceForClient(ClientId client) =>
        new($"No configuration source is known for {client.Raw()}.");

    public static ToggleException BundledServerNotPortable(string name) =>
        new($"\"{name}\" is a Claude Desktop extension. Extensions are installed " +
            "bundles and cannot be copied into another client.");

    public static ToggleException BundledServerNotToggleable(string name) =>
        new($"\"{name}\" comes from an installed plugin, not from the configuration " +
            "file. Its definition lives in a cache the client rewrites, so Kytto will " +
            "not edit it — turn the plugin off in the client that installed it.");

    /// <summary>
    /// A plugin's server is not something to copy into another client, which would
    /// make a definition up out of installed software (§4).
    /// </summary>
    public static ToggleException PluginNotPortable(string name) =>
        new($"\"{name}\" comes from an installed plugin. Plugins are installed software, " +
            "so Kytto will not copy their servers into another client — install the " +
            "plugin there instead.");

    public static ToggleException NoParkedDefinition(string name) =>
        new($"Kytto has no stored definition for \"{name}\" to put back.");

    /// <summary>
    /// The definition has settings the target client's format cannot be given from
    /// Kytto's model — headers, a working directory — and copying it without them would
    /// write a server that cannot start or authenticate.
    /// </summary>
    public static ToggleException DefinitionNotPortable(string name, IReadOnlyList<string> fields) =>
        new($"\"{name}\" uses {string.Join(", ", fields)}, which Kytto cannot carry into this " +
            "client's configuration format. Add it to this client by hand so nothing it needs to " +
            "start or sign in is lost.");

    /// <summary>The definition is Kytto's Gateway helper bound to one client's route.</summary>
    public static ToggleException GatewayDefinitionNotPortable(string name) =>
        new($"\"{name}\" runs through Kytto Gateway for one client. Restore its direct definition " +
            "there first, then add it to this client.");
}

/// <summary>Turns a matrix cell on or off.</summary>
/// <remarks>
/// <para>
/// Each client answers "how do I disable this?" differently, and the difference is
/// the substance of the feature:
/// </para>
/// <list type="bullet">
/// <item><strong>Claude Code</strong> keeps a deny list in a <em>separate</em>
/// settings file. The server stays configured; its name goes in or out of that
/// list.</item>
/// <item><strong>Claude Desktop extensions</strong> have a boolean in their own
/// settings file.</item>
/// <item><strong>Everything else</strong> has no disabled state at all, so
/// switching off means removing the definition — which is only acceptable because
/// it is parked first and restored byte for byte.</item>
/// </list>
/// <para>
/// Switching on a server a client does not have copies the definition across. That
/// is the cross-client matrix doing the thing it exists to do.
/// </para>
/// </remarks>
public sealed class ToggleService(
    string home,
    ConfigWriter writer,
    ParkStore parkStore,
    DigestLedger ledger,
    IReadOnlyList<ClientDescriptor>? descriptors = null,
    IReadOnlyDictionary<string, string>? pathOverrides = null)
{
    private readonly ClientPathResolver _resolver = new(home, pathOverrides);
    private readonly IReadOnlyList<ClientDescriptor> _descriptors = descriptors ?? ClientRegistry.All;

    public ToggleResult SetEnabled(bool enabled, Server server, ClientId clientId)
    {
        // Plugins are refused here, not only greyed out in the UI, and per client
        // rather than per row: a name can be a plugin's in one client and an ordinary
        // entry in another, and the ordinary one stays controllable. A Claude Desktop
        // extension is not a plugin and goes on being switched by its own client below.
        if (server.HoldsPluginCopy(clientId)) throw ToggleException.BundledServerNotToggleable(server.Name);
        if (server.IsPlugin && server.DefinitionsByClient.GetValueOrDefault(clientId) is not { IsBundled: false })
        {
            throw ToggleException.PluginNotPortable(server.Name);
        }

        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId)
            ?? throw ToggleException.NoSourceForClient(clientId);

        if (server.Origin is Origin.ClaudeDesktopExtension extension && clientId == ClientId.ClaudeDesktop)
        {
            return SetExtensionEnabled(enabled, server, extension.BundleId, descriptor);
        }

        var source = descriptor.EditableServerMap
            ?? throw ToggleException.NoSourceForClient(clientId);

        return source.Enablement switch
        {
            EnablementStrategy.DenyList deny =>
                SetViaDenyList(enabled, server, clientId, source, deny),
            EnablementStrategy.Presence =>
                SetViaPresence(enabled, server, clientId, source),
            EnablementStrategy.InlineFlag flag when source.Format == ConfigFormat.Toml =>
                SetViaInlineFlag(enabled, server, clientId, source, flag.Key),
            EnablementStrategy.InlineFlag or EnablementStrategy.InlineDisabledFlag
                when source.Format == ConfigFormat.Json && source.Enablement.Flag is { } jsonFlag =>
                SetViaJsonFlag(enabled, server, clientId, source, jsonFlag),
            _ => throw ToggleException.NoSourceForClient(clientId),
        };
    }

    // MARK: - Definitions that have to travel

    /// <summary>
    /// Where the bytes come from when a definition has to appear in a client that
    /// does not have it.
    /// </summary>
    private abstract record ArrivingDefinition
    {
        /// <summary>Text Kytto already holds, going in exactly as it stands.</summary>
        public sealed record Verbatim(string Text) : ArrivingDefinition;

        /// <summary>
        /// Rebuilt from the model, because the text came out of a file spelled
        /// differently and would not mean the same thing here (§4).
        /// </summary>
        public sealed record FromModel : ArrivingDefinition;
    }

    /// <summary>What a client should be given for a server it has never had.</summary>
    /// <remarks>
    /// Shared by both paths that can be a definition's first arrival: presence
    /// clients, and Claude Code — whose deny list can only ever speak about a server
    /// its config file already lists.
    /// </remarks>
    private ArrivingDefinition ArrivingFor(Server server, ClientId clientId)
    {
        if (parkStore.Parked(clientId, server.Name) is { } parked)
        {
            // Kytto's own bytes, going back exactly where they came from.
            return new ArrivingDefinition.Verbatim(parked.SourceText);
        }
        if (server.IsPlugin) throw ToggleException.PluginNotPortable(server.Name);
        if (server.IsBundled)
        {
            // An extension or plugin is installed software, not a definition that
            // can be copied into another client's config.
            throw ToggleException.BundledServerNotPortable(server.Name);
        }
        EnsurePortable(server, clientId);
        if (server.DefinitionSource.Length > 0 && FormatOf(server.Origin) == ConfigFormat.Json &&
            TargetFormat(clientId) == ConfigFormat.Json &&
            PortableJson(server.DefinitionSource, SchemaOf(server.Origin), SchemaOfClient(clientId)) is { } copied)
        {
            // A copy from another JSON client. Where the two agree on the per-server
            // shape the text carries over as it stands, keeping `headers`, `cwd` and
            // keys Kytto does not model; where they spell the endpoint differently
            // only those keys are respelt.
            return new ArrivingDefinition.Verbatim(copied);
        }
        if (server.Command is not null || server.Url is not null)
        {
            return new ArrivingDefinition.FromModel();
        }
        throw ToggleException.NoParkedDefinition(server.Name);
    }

    /// <summary>Refuses a copy that would arrive without something it needs.</summary>
    /// <remarks>
    /// A Gateway definition points at one client's route: copied, a second client would
    /// run — and be logged and masked as — the first one. And a definition whose format
    /// differs from the target's is rendered from the model, which carries command, args,
    /// env and url and nothing else.
    /// </remarks>
    private void EnsurePortable(Server server, ClientId clientId)
    {
        if (server.Command is { } command && server.Args.Contains("--route", StringComparer.Ordinal) &&
            Path.GetFileNameWithoutExtension(command).Equals("kytto-mcp-proxy", StringComparison.OrdinalIgnoreCase))
        {
            throw ToggleException.GatewayDefinitionNotPortable(server.Name);
        }
        var targetSchema = SchemaOfClient(clientId);
        var sourceSchema = SchemaOf(server.Origin);
        if (TargetFormat(clientId) == ConfigFormat.Json && server.Transport == Transport.Sse && !targetSchema.AcceptsSse)
        {
            throw ToggleException.DefinitionNotPortable(server.Name, ["the legacy HTTP+SSE transport"]);
        }
        if (FormatOf(server.Origin) is { } sourceFormat &&
            (sourceFormat != TargetFormat(clientId) || sourceSchema.Shape != targetSchema.Shape))
        {
            // Text that translates between two mcpServers-shaped clients never gets
            // here; what does is rendered from the model.
            var lost = UnportableFields(server.DefinitionSource, sourceFormat, sourceSchema);
            if (lost.Count > 0) throw ToggleException.DefinitionNotPortable(server.Name, lost);
        }
    }

    private ServerSchema SchemaOfClient(ClientId clientId) =>
        _descriptors.FirstOrDefault(descriptor => descriptor.Id == clientId)?.ServerSchema ?? ServerSchema.Standard;

    private ServerSchema SchemaOf(Origin origin) =>
        origin is Origin.ConfigFile file && file.Client.BuiltIn is { } clientId
            ? SchemaOfClient(clientId)
            : ServerSchema.Standard;

    /// <summary>
    /// A JSON definition from a client with schema <paramref name="from"/>, ready for
    /// one with schema <paramref name="to"/>, without the source client's own on/off
    /// and tool state. Null when the shapes differ and the definition has to be rendered.
    /// </summary>
    internal static string? PortableJson(string source, ServerSchema from, ServerSchema to)
    {
        if (from != to) return to.Translating(source, from);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source);
        }
        catch (JsonParseException)
        {
            return source;
        }
        if (!document.Root.Keys.Any(ServerSchema.ClientStateKeys.Contains)) return source;
        var text = source;
        try
        {
            foreach (var key in ServerSchema.ClientStateKeys)
            {
                text = JsonDocument.Parse(text).RemovingMember(key, []);
            }
        }
        catch (Exception error) when (error is JsonEditException or JsonParseException)
        {
            return null;
        }
        return text;
    }

    private ConfigFormat? TargetFormat(ClientId clientId) =>
        _descriptors.FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap?.Format;

    /// <summary>
    /// Launch and authentication settings a definition has that rendering it from the
    /// model (command, args, env, url) would drop.
    /// </summary>
    internal static IReadOnlyList<string> UnportableFields(
        string source,
        ConfigFormat format,
        ServerSchema? schema = null)
    {
        if (source.Length == 0) return [];
        if (format == ConfigFormat.Json)
        {
            try
            {
                return (schema ?? ServerSchema.Standard).UnportableFields(JsonDocument.Parse(source).Root);
            }
            catch (JsonParseException)
            {
                return [];
            }
        }

        TomlDocument document;
        try
        {
            document = TomlDocument.Parse(source);
        }
        catch (TomlParseException)
        {
            return [];
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in document.Tables)
        {
            // Sub-tables (`[mcp_servers.x.http_headers]`) count by name; the server's
            // own header names the server, not a setting.
            if (table.Path.Count > 2) names.Add(table.Path[^1]);
            foreach (var pair in table.Pairs)
            {
                names.Add(pair.Key[0]);
                // An inline definition, `x = { url = "…", http_headers = { … } }`,
                // keeps its settings inside the one value.
                if (table.Path.Count <= 1 && pair.Value.InlinePairs is { } inline)
                {
                    foreach (var field in inline) names.Add(field.Key[0]);
                }
            }
        }
        return new[] { "http_headers", "env_http_headers", "bearer_token_env_var", "cwd", "env_vars" }
            .Where(names.Contains)
            .ToArray();
    }

    private string Literal(
        ArrivingDefinition arriving,
        Server server,
        string serversKey,
        JsonDocument document,
        ClientId clientId) => arriving switch
        {
            ArrivingDefinition.Verbatim verbatim => verbatim.Text,
            _ => JsonLiteral(server, serversKey, document, SchemaOfClient(clientId)),
        };

    /// <summary>The format a server's definition source is written in.</summary>
    /// <remarks>
    /// Copying a server into a client that does not have it used to be a matter of
    /// moving the source text across, because the four clients that existed all
    /// spelled a definition the same way. Codex does not, so the text is only
    /// portable between clients that agree — and everywhere else the definition gets
    /// rendered from the model instead.
    /// </remarks>
    private ConfigFormat? FormatOf(Origin origin)
    {
        if (origin is not Origin.ConfigFile file) return null;
        return _descriptors
            .FirstOrDefault(descriptor => descriptor.Id == file.Client)?
            .EditableServerMap?.Format;
    }

    /// <summary>
    /// A JSON object literal for a server that came from somewhere else, indented
    /// to sit among whatever is already in the document.
    /// </summary>
    private static string JsonLiteral(Server server, string serversKey, JsonDocument document, ServerSchema schema)
    {
        var unit = IndentStyle.Detect(document.SourceText).Text;
        var map = document.ValueAt(serversKey);
        string baseIndent;
        if (map?.Members?.FirstOrDefault() is { } first)
        {
            baseIndent = document.LineIndent(first.Span.Start);
        }
        else if (map is not null)
        {
            baseIndent = document.LineIndent(map.Span.Start) + unit;
        }
        else
        {
            baseIndent = unit;
        }
        var newline = document.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return ServerDraft.Editing(server).Definition(baseIndent, unit, newline, schema);
    }

    private static string TomlBlockFor(Server server, string name, string serversKey, bool enabled) =>
        TomlBuilder.ServerBlock(
            name: name,
            key: serversKey,
            command: server.Command,
            args: server.Args,
            url: server.Url,
            env: server.Env,
            // Absent means on, so the key is only worth writing to say "off".
            enabled: enabled ? null : false);

    // MARK: - Codex: a boolean on the server's own table

    /// <summary>
    /// The kindest of the four mechanisms: the definition never leaves the file, so
    /// switching off parks nothing and switching back on cannot have lost anything.
    /// Only the first arrival of a server has to write a definition.
    /// </summary>
    private ToggleResult SetViaInlineFlag(
        bool enabled,
        Server server,
        ClientId clientId,
        ConfigSource.ServerMap source,
        string flagKey)
    {
        // No up-front refusal for a bundled row: discovery merges rows by identity and
        // takes `IsBundled` from whichever source it read first, so a Codex table can
        // sit in a row that came from a Claude Desktop extension. Codex's own table is
        // toggled like any other; only an arrival needs a portable definition.
        var path = _resolver.ResolveServerMap(source.File, clientId);
        var display = _resolver.DisplayServerMap(source.File, clientId);
        var serversKey = source.ServersKey;

        var receipt = writer.Edit<TomlDocument>(
            path, clientId, display, ledger.DigestFor(path),
            document =>
            {
                // The name in this file, which need not match the row's name
                // character for character — the matrix merges rows by identity. Two
                // tables differing only in case are both valid TOML, and Codex runs
                // both; toggling one of them would report a success it did not have,
                // so an ambiguous name is refused.
                var name = document.ServerName(server.Id, serversKey);

                if (name is null)
                {
                    if (server.IsBundled && FormatOf(server.Origin) != ConfigFormat.Toml)
                    {
                        throw ToggleException.BundledServerNotPortable(server.Name);
                    }
                    // Nothing in the config file answers to this name. Either it is
                    // genuinely not here, or it is here by another route — a plugin —
                    // and that route is not one Kytto writes to. Saying so beats a
                    // click that appears to work and changes nothing.
                    if (server.IsBundled ||
                        server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) != Enablement.Absent)
                    {
                        throw ToggleException.BundledServerNotToggleable(server.Name);
                    }
                    // Switching off something that is not there is already true.
                    if (!enabled) return document.SourceText;
                    EnsurePortable(server, clientId);
                    return document.SettingServer(
                        server.Name,
                        serversKey,
                        TomlBlockFor(server, server.Name, serversKey, enabled: true));
                }

                // Absent means on, so a server with no flag is already in the state
                // an "enable" is asking for. Writing `enabled = true` anyway would
                // touch someone's config to change nothing, and cost them a backup
                // slot for the privilege.
                var current = document.Table(serversKey, name)?.Value(flagKey)?.BoolValue ?? true;
                if (current == enabled) return document.SourceText;

                return document.SettingServerFlag(enabled, flagKey, name, serversKey);
            });
        ledger.Record(path, receipt.Digest);

        return new ToggleResult(
            ClientID: clientId,
            ServerName: server.Name,
            Enabled: enabled,
            RequiresRestart: receipt.DidWrite,
            BackupID: receipt.BackupID,
            PathDisplay: receipt.PathDisplay,
            // Nothing is ever removed here, so nothing is ever parked.
            WasParked: false);
    }

    // MARK: - Antigravity, Devin Desktop, OpenCode: a boolean on the JSON entry

    /// <summary>
    /// The JSON sibling of <see cref="SetViaInlineFlag"/>. The definition never leaves
    /// the file, so nothing is parked; only a first arrival writes a definition.
    /// </summary>
    private ToggleResult SetViaJsonFlag(
        bool enabled,
        Server server,
        ClientId clientId,
        ConfigSource.ServerMap source,
        (string Key, bool OnValue) flag)
    {
        var path = _resolver.ResolveServerMap(source.File, clientId);
        var display = _resolver.DisplayServerMap(source.File, clientId);
        var serversKey = source.ServersKey;

        var receipt = writer.Edit<JsonDocument>(
            path, clientId, display, ledger.DigestFor(path),
            document =>
            {
                if (document.ServerKey(server.Id, serversKey) is not { } key)
                {
                    if (server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) != Enablement.Absent)
                    {
                        throw ToggleException.BundledServerNotToggleable(server.Name);
                    }
                    // Switching off something that is not there is already true.
                    if (!enabled) return document.SourceText;
                    var arriving = ArrivingFor(server, clientId);
                    return document.SettingMember(
                        server.Name,
                        [serversKey],
                        Literal(arriving, server, serversKey, document, clientId));
                }

                // Absent means on. A server already in the state asked for is not
                // written at all, so a no-op click costs no backup slot.
                string[] entryPath = [serversKey, key];
                var entry = document.ValueAt(entryPath);
                if (entry?.Members is not { } members)
                {
                    throw new JsonEditException(JsonEditErrorKind.NotAnObject, entryPath);
                }
                var current = JsonEditor.SoleMember(flag.Key, members, entryPath)?.Value.BoolValue;
                var isOn = current is { } value ? value == flag.OnValue : true;
                if (isOn == enabled) return document.SourceText;

                // `"disabled": true` goes away when the server comes back on, which is
                // how the entry looked before Kytto touched it; an `enabled` key states
                // its value either way, as Codex's does.
                if (enabled && !flag.OnValue)
                {
                    return document.RemovingMember(flag.Key, entryPath);
                }
                return document.SettingMember(flag.Key, entryPath, JsonText.Bool(enabled == flag.OnValue));
            });
        ledger.Record(path, receipt.Digest);

        return new ToggleResult(
            ClientID: clientId,
            ServerName: server.Name,
            Enabled: enabled,
            RequiresRestart: receipt.DidWrite,
            BackupID: receipt.BackupID,
            PathDisplay: receipt.PathDisplay,
            // Nothing is ever removed here, so nothing is ever parked.
            WasParked: false);
    }

    // MARK: - Claude Code: a deny list in another file

    /// <summary>Switching a server <em>on</em> here is two edits, not one.</summary>
    /// <remarks>
    /// <para>
    /// The deny list can only ever say something about a server the config file
    /// already lists — taking a name out of it permits a definition that has to
    /// exist for the permission to mean anything. So a cell that was empty needs the
    /// definition to arrive in the config file first. Without that step the click
    /// edits a list that never mentioned the server, writes nothing, and reports a
    /// success it did not have: the cell the user pressed comes back empty and the
    /// restart bar asks them to restart for no change at all.
    /// </para>
    /// <para>
    /// The order is the safe one. If the deny edit fails after the definition lands,
    /// the server is configured and denied — which the matrix draws as "disabled",
    /// one click from where the user was going. The other order leaves a permission
    /// for a server that does not exist.
    /// </para>
    /// </remarks>
    private ToggleResult SetViaDenyList(
        bool enabled,
        Server server,
        ClientId clientId,
        ConfigSource.ServerMap source,
        EnablementStrategy.DenyList deny)
    {
        // The model's own view decides whether this is an arrival, so a server the
        // client already lists takes exactly the path it took before — including
        // bundled ones, which must not start failing here on the strength of a flag
        // that describes where the row was first found.
        var isArriving = enabled
            && server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) == Enablement.Absent;
        var arrival = isArriving ? PrepareDefinition(server, clientId, source) : null;

        var path = _resolver.Resolve(deny.File);
        var denyWrite = writer.Prepare<JsonDocument>(
            path, clientId, deny.File.DisplayString(), ledger.DigestFor(path),
            document =>
            {
                if (enabled)
                {
                    return document.RemovingElements([deny.Key], element => Denies(element, server));
                }

                // Already denied: leave the file alone rather than adding a
                // duplicate entry the client would have to tolerate.
                var alreadyDenied = document.Root[deny.Key]?.Elements?
                    .Any(element => Denies(element, server)) ?? false;
                if (alreadyDenied) return document.SourceText;

                return document.AppendingElement(
                    document.ObjectLiteral(
                        [(deny.NameField, JsonText.String(SpellingInConfig(server, clientId, source)))],
                        [deny.Key]),
                    [deny.Key]);
            });

        // An arrival changes two files. Committing them together makes the
        // definition write reversible if the deny-list write fails, including
        // the first-use case where the definition file did not exist (§6.4).
        var writes = arrival is null
            ? new[] { denyWrite }
            : new[] { arrival, denyWrite };
        var receipts = writer.Commit(writes);
        for (var index = 0; index < writes.Length; index++)
        {
            ledger.Record(writes[index].Path, receipts[index].Digest);
        }

        var denyReceipt = receipts[^1];
        var arrivalReceipt = arrival is null ? null : receipts[0];

        // The definition is in the file now, so Kytto's copy of it is spent.
        if (isArriving) parkStore.Unpark(clientId, server.Name);

        return new ToggleResult(
            ClientID: clientId,
            ServerName: server.Name,
            Enabled: enabled,
            RequiresRestart: receipts.Any(candidate => candidate.DidWrite),
            // The file worth naming is the one the definition landed in, when this
            // click is what put it there. Otherwise only the list moved.
            BackupID: arrivalReceipt?.BackupID ?? denyReceipt.BackupID,
            PathDisplay: arrivalReceipt?.PathDisplay ?? denyReceipt.PathDisplay,
            WasParked: false);

        bool Denies(JsonNode element, Server subject)
        {
            var name = element[deny.NameField]?.StringValue ?? element.StringValue;
            return name is not null && Server.Identity(name) == subject.Id;
        }
    }

    /// <summary>The name the client's own config file uses for this server.</summary>
    /// <remarks>
    /// Not the row's: the row takes its name from whichever client listed it first, and
    /// the client matches its deny list against its own key.
    /// </remarks>
    private string SpellingInConfig(Server server, ClientId clientId, ConfigSource.ServerMap source)
    {
        var path = _resolver.ResolveServerMap(source.File, clientId);
        try
        {
            if (File.Exists(path) &&
                JsonDocument.ParseFile(path).ServerKey(server.Id, source.ServersKey) is { } key)
            {
                return key;
            }
        }
        catch (Exception error) when (error is JsonParseException or JsonEditException or IOException or
                                              UnauthorizedAccessException)
        {
            // The row's name is the best remaining guess, and the deny list matches
            // by identity when it is read back.
        }
        return server.Name;
    }

    /// <summary>
    /// Puts a definition into a client's server map, leaving one that is already
    /// there exactly as the user has it.
    /// </summary>
    private PreparedConfigWrite PrepareDefinition(
        Server server,
        ClientId clientId,
        ConfigSource.ServerMap source)
    {
        var arriving = ArrivingFor(server, clientId);
        var path = _resolver.ResolveServerMap(source.File, clientId);
        var display = _resolver.DisplayServerMap(source.File, clientId);
        var serversKey = source.ServersKey;

        return writer.Prepare<JsonDocument>(
            path, clientId, display, ledger.DigestFor(path),
            document =>
            {
                // Discovery said this client does not have it. If the file
                // disagrees, the file wins: overwriting a definition someone may
                // have edited by hand is not what a click on an empty cell asked for.
                if (document.ServerKey(server.Id, serversKey) is not null)
                {
                    return document.SourceText;
                }
                return document.SettingMember(
                    server.Name,
                    [serversKey],
                    Literal(arriving, server, serversKey, document, clientId));
            });
    }

    // MARK: - Claude Desktop extensions: a boolean of their own

    private ToggleResult SetExtensionEnabled(
        bool enabled,
        Server server,
        string bundleId,
        ClientDescriptor descriptor)
    {
        var bundles = descriptor.Sources.OfType<ConfigSource.ExtensionBundles>().FirstOrDefault()
            ?? throw ToggleException.NoSourceForClient(ClientId.ClaudeDesktop);

        var path = Path.Combine(_resolver.Resolve(bundles.SettingsDirectory), $"{bundleId}.json");
        var display = $"{bundles.SettingsDirectory.DisplayString()}\\{bundleId}.json";

        var receipt = writer.Edit<JsonDocument>(
            path, ClientId.ClaudeDesktop, display, ledger.DigestFor(path),
            // The file carries other keys — `userConfig` among them — which must
            // survive being toggled.
            document => document.SettingMember(bundles.FlagKey, [], JsonText.Bool(enabled)));
        ledger.Record(path, receipt.Digest);

        return new ToggleResult(
            ClientID: ClientId.ClaudeDesktop,
            ServerName: server.Name,
            Enabled: enabled,
            RequiresRestart: receipt.DidWrite,
            BackupID: receipt.BackupID,
            PathDisplay: receipt.PathDisplay,
            WasParked: false);
    }

    // MARK: - Everyone else: presence is the only lever

    private ToggleResult SetViaPresence(
        bool enabled,
        Server server,
        ClientId clientId,
        ConfigSource.ServerMap source)
    {
        var path = _resolver.ResolveServerMap(source.File, clientId);
        var display = _resolver.DisplayServerMap(source.File, clientId);
        var serversKey = source.ServersKey;

        // Decided before the file is touched, so a definition Kytto cannot build
        // fails without anything having been opened.
        var arriving = enabled ? ArrivingFor(server, clientId) : null;

        var parked = false;
        var restored = false;
        var previousParked = parkStore.Parked(clientId, server.Name);
        var expectedDigest = ledger.DigestFor(path);
        WriteReceipt receipt;
        try
        {
            receipt = writer.Edit<JsonDocument>(
                path, clientId, display, expectedDigest,
                document =>
                {
                    // The spelling this file uses, which need not be the row's: the
                    // matrix merges `GitHub` and `github` into one row.
                    var key = document.ServerKey(server.Id, serversKey);

                    if (arriving is not null)
                    {
                        // Already defined here — put back by hand, or edited in
                        // place. The file wins; restoring held bytes over it would
                        // undo whatever is there now.
                        if (key is not null) return document.SourceText;
                        restored = true;
                        // Back under the spelling it was held under, which is this
                        // file's own — not the merged row's, which may come from
                        // another client.
                        return document.SettingMember(
                            previousParked?.ServerName ?? server.Name,
                            [serversKey],
                            Literal(arriving, server, serversKey, document, clientId));
                    }

                    if (key is null || document.ValueAt(serversKey)?.Member(key) is not { } member)
                    {
                        return document.SourceText;
                    }
                    // Park the exact bytes before they go, so a crash cannot leave
                    // the definition removed without a restorable copy.
                    parkStore.Park(clientId, key, document.Slice(member.Value.Span));
                    parked = true;
                    return document.RemovingMember(key, [serversKey]);
                });
        }
        catch
        {
            // Preparing the edit parks before committing for crash safety. If the
            // config write is then refused or fails, put the sidecar back too;
            // otherwise discovery would draw a still-present server as disabled.
            if (parked && Digest.OfFile(path) == expectedDigest)
            {
                if (previousParked is null) parkStore.Unpark(clientId, server.Name);
                else parkStore.Park(
                    previousParked.ClientID,
                    previousParked.ServerName,
                    previousParked.SourceText);
            }
            throw;
        }
        ledger.Record(path, receipt.Digest);

        // Kytto's copy is spent once its bytes are back in the file. If the file
        // already had a definition, nothing was restored and the copy stays.
        if (restored) parkStore.Unpark(clientId, server.Name);

        return new ToggleResult(
            ClientID: clientId,
            ServerName: server.Name,
            Enabled: enabled,
            RequiresRestart: receipt.DidWrite,
            BackupID: receipt.BackupID,
            PathDisplay: receipt.PathDisplay,
            WasParked: parked);
    }
}
