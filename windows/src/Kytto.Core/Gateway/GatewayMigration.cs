using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Toml;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Gateway;

public sealed record GatewayMigrationPreview(
    string RouteID,
    string ServerID,
    string ServerName,
    ClientId ClientID,
    string PathDisplay,
    string FormatDisplay,
    string DirectDefinitionPreview,
    string GatewayDefinitionPreview,
    IReadOnlyList<string> EnvironmentKeys);

public sealed record GatewayMigrationResult(
    GatewayRoute Route,
    string? BackupID,
    string PathDisplay)
{
    /// <summary>
    /// Environment variables a rebuilt Direct definition could not be given, because
    /// their stored values were gone. Empty for an ordinary restore.
    /// </summary>
    public IReadOnlyList<string> MissingEnvironmentKeys { get; init; } = [];
}

/// <summary>
/// Something that stops a Gateway definition in a client's config from starting,
/// found by reading the config and the route store only.
/// </summary>
/// <remarks>
/// Nothing here opens the credential store, so it is safe to compute on every reload.
/// </remarks>
public sealed record GatewayRouteDiagnosis(
    string ServerID,
    string ServerName,
    ClientId ClientID,
    string RouteID,
    GatewayRouteProblem Problem,
    /// <summary>For <see cref="GatewayRouteProblem.HelperMissing"/>: the path the config names.</summary>
    string? HelperPath = null,
    /// <summary>
    /// For <see cref="GatewayRouteProblem.HelperMissing"/>: whether this copy's helper
    /// can take over.
    /// </summary>
    bool Repairable = false);

public enum GatewayRouteProblem
{
    /// <summary>The config names a helper that is not there — Kytto was moved, reinstalled elsewhere or deleted.</summary>
    HelperMissing,
    /// <summary>The config points at a route Kytto has no record of.</summary>
    UnknownRoute,
}

public sealed class GatewayMigrationException(string message) : Exception(message)
{
    public static GatewayMigrationException HelperUnavailable(string path) =>
        new($"The bundled gateway helper is unavailable at {path}. Reinstall or rebuild Kytto.");

    public static GatewayMigrationException UnsupportedTransport() =>
        new("Gateway mode currently supports stdio servers only.");

    public static GatewayMigrationException BundledServer() =>
        new("Installed extension and plugin servers cannot be migrated.");

    public static GatewayMigrationException ServerNotEnabled() =>
        new("Enable this server in the selected client before turning on Gateway mode.");

    public static GatewayMigrationException RouteAlreadyExists() =>
        new("This server already uses Gateway mode in that client.");

    public static GatewayMigrationException DefinitionMissing() =>
        new("The selected client no longer contains this server definition.");

    public static GatewayMigrationException InvalidDefinition(string reason) =>
        new($"This server cannot use Gateway mode: {reason}");

    public static GatewayMigrationException RouteMismatch() =>
        new("The client configuration no longer points at this gateway route. Kytto did not overwrite it.");

    public static GatewayMigrationException OrphanedRouteInBackup(string id) =>
        new($"That backup points at gateway route {id}, but its credentials no longer exist. Restore a Direct backup instead.");

    /// <summary>
    /// Kytto is running from a place that will not exist later — the temporary folder
    /// Windows extracts to when an app is started from inside a ZIP.
    /// </summary>
    public static GatewayMigrationException HelperLocationUnstable(string path) =>
        new($"Kytto is running from {path}, a temporary copy Windows made while opening a ZIP file. " +
            "Extract Kytto to a permanent folder, open it from there, then turn on Gateway mode.");

    internal static GatewayMigrationException Unmodelled(IReadOnlyList<string> keys) =>
        InvalidDefinition(
            $"it uses {string.Join(", ", keys)}, which the gateway does not carry over. " +
            $"Remove {(keys.Count == 1 ? "it" : "them")} or keep this server Direct");
}

/// <summary>Opt-in Direct ↔ Gateway migration through the §6 write path.</summary>
public sealed class GatewayMigrationService
{
    private sealed record DirectDefinition(
        string ServerName,
        string SourceText,
        string Command,
        IReadOnlyList<string> Arguments,
        IReadOnlyList<EnvEntry> Environment,
        string Replacement);

    private sealed record Target(
        ClientId ClientID,
        ConfigSource.ServerMap Source,
        string Path,
        string PathDisplay,
        string SourceText);

    private readonly string _helperPath;
    private readonly ClientPathResolver _resolver;
    private readonly ConfigWriter _writer;
    private readonly DigestLedger _ledger;
    private readonly GatewayRouteStore _routes;
    private readonly ISecretStore _secrets;
    private readonly IReadOnlyList<ClientDescriptor> _descriptors;

    public GatewayMigrationService(
        string home,
        string helperPath,
        ConfigWriter writer,
        DigestLedger ledger,
        GatewayRouteStore routes,
        ISecretStore secrets,
        IReadOnlyList<ClientDescriptor>? descriptors = null,
        IReadOnlyDictionary<string, string>? pathOverrides = null)
    {
        _helperPath = Path.GetFullPath(helperPath);
        _writer = writer;
        _ledger = ledger;
        _routes = routes;
        _secrets = secrets;
        _descriptors = descriptors ?? ClientRegistry.All;
        _resolver = new ClientPathResolver(home, pathOverrides);
    }

    public GatewayMigrationPreview Preview(
        Server server,
        ClientId clientId,
        string? routeId = null)
    {
        var target = TargetFor(server, clientId, requireNoRoute: true);
        var normalizedRouteId = NormalizeRouteID(routeId ?? Guid.NewGuid().ToString("D"));
        var direct = Direct(target, server.Id, normalizedRouteId);
        return new GatewayMigrationPreview(
            normalizedRouteId,
            server.Id,
            direct.ServerName,
            clientId,
            target.PathDisplay,
            target.Source.Format.DisplayName(),
            RedactedPreview(direct, target.Source.Format),
            direct.Replacement,
            direct.Environment.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray());
    }

    public GatewayMigrationResult Enable(Server server, ClientId clientId, string routeId)
    {
        var target = TargetFor(server, clientId, requireNoRoute: true);
        var normalizedRouteId = NormalizeRouteID(routeId);
        if (_routes.All().Any(route => route.Id == normalizedRouteId))
        {
            throw GatewayMigrationException.RouteAlreadyExists();
        }

        var direct = Direct(target, server.Id, normalizedRouteId);
        var definitionId = GatewayRoute.DirectDefinitionIdentifier(normalizedRouteId);
        var references = direct.Environment.Select(entry => new GatewayEnvironmentReference(
            entry.Key,
            GatewayRoute.EnvironmentIdentifier(normalizedRouteId, entry.Key))).ToArray();
        var route = new GatewayRoute(
            normalizedRouteId,
            server.Id,
            direct.ServerName,
            clientId,
            _helperPath,
            direct.Command,
            direct.Arguments,
            references,
            definitionId,
            DateTimeOffset.UtcNow);

        var storedIds = new List<string>();
        try
        {
            _secrets.Store(direct.SourceText, definitionId);
            storedIds.Add(definitionId);
            foreach (var pair in direct.Environment.Zip(references))
            {
                _secrets.Store(pair.First.Value ?? "", pair.Second.SecretID);
                storedIds.Add(pair.Second.SecretID);
            }
            _routes.Insert(route);

            var receipt = WriteReplacement(direct.Replacement, target, null, direct.ServerName);
            _ledger.Record(target.Path, receipt.Digest);
            return new GatewayMigrationResult(route, receipt.BackupID, receipt.PathDisplay);
        }
        catch
        {
            try { _routes.Remove(normalizedRouteId); } catch (GatewayRouteException) { }
            foreach (var id in storedIds)
            {
                try { _secrets.Delete(id); } catch (SecretsException) { }
            }
            throw;
        }
    }

    public GatewayMigrationResult Restore(string routeId)
    {
        var route = _routes.Route(routeId);
        var target = TargetFor(route.ClientID);
        // The stored original is the faithful way back. When it is gone — Credential
        // Manager cleared, or the profile moved to another machine — the route still
        // knows the command and arguments, so Direct mode is rebuilt from those instead
        // of leaving a server that can be neither started nor edited. Values that are
        // gone too are named, so the user knows what to enter again.
        var missingKeys = new List<string>();
        var source = TryValue(route.DirectDefinitionSecretID);
        if (source is null)
        {
            var environment = new List<EnvEntry>();
            foreach (var reference in route.Environment)
            {
                if (TryValue(reference.SecretID) is { } value) environment.Add(new EnvEntry(reference.Key, value));
                else missingKeys.Add(reference.Key);
            }
            source = Definition(target, route.ServerName, route.Command, route.Arguments, environment);
        }
        var receipt = WriteReplacement(source, target, route.Id, route.ServerName);
        _ledger.Record(target.Path, receipt.Digest);

        // The authoritative config is already Direct again. Cleanup is best-effort
        // so a Credential Manager problem cannot turn that success into a false failure.
        try { _routes.Remove(route.Id); } catch (GatewayRouteException) { }
        TryDelete(route.DirectDefinitionSecretID);
        foreach (var reference in route.Environment) TryDelete(reference.SecretID);

        return new GatewayMigrationResult(route, receipt.BackupID, receipt.PathDisplay)
        {
            MissingEnvironmentKeys = missingKeys.Order(StringComparer.Ordinal).ToArray(),
        };
    }

    // Keeping routes working

    /// <summary>What stops each Gateway definition from starting, config by config.</summary>
    /// <remarks>
    /// A client only ever reports "server failed". The causes Kytto can see without
    /// starting anything are the two here: the helper the config names is gone (Kytto
    /// was moved, reinstalled elsewhere or removed), or the route record is.
    /// </remarks>
    public IReadOnlyList<GatewayRouteDiagnosis> Diagnose()
    {
        IReadOnlyList<GatewayRoute> known;
        try { known = _routes.All(); }
        catch (GatewayRouteException) { return []; }
        var byId = new Dictionary<string, GatewayRoute>(StringComparer.Ordinal);
        foreach (var route in known) byId.TryAdd(route.Id, route);
        var currentHelperUsable = File.Exists(_helperPath) && !IsTemporaryZipExtraction(_helperPath);
        var result = new List<GatewayRouteDiagnosis>();

        foreach (var descriptor in _descriptors)
        {
            if (descriptor.EditableServerMap is not { } source ||
                descriptor.ServerSchema.Shape != ServerSchema.EntryShape.McpServers) continue;
            var path = _resolver.ResolveServerMap(source.File, descriptor.Id);
            IReadOnlyList<(string ServerName, string RouteID, string Command)> entries;
            try
            {
                if (!File.Exists(path)) continue;
                entries = GatewayEntries(File.ReadAllText(path), descriptor.Id);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonParseException or TomlParseException or AuthoringException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!byId.TryGetValue(entry.RouteID, out var route) || route.ClientID != descriptor.Id)
                {
                    result.Add(new GatewayRouteDiagnosis(
                        Server.Identity(entry.ServerName),
                        entry.ServerName,
                        descriptor.Id,
                        entry.RouteID,
                        GatewayRouteProblem.UnknownRoute));
                    continue;
                }
                // A bare name is found through PATH by the client; only a path Kytto
                // wrote can be checked from here.
                if (!Path.IsPathFullyQualified(entry.Command) || File.Exists(entry.Command)) continue;
                result.Add(new GatewayRouteDiagnosis(
                    route.ServerID,
                    entry.ServerName,
                    descriptor.Id,
                    route.Id,
                    GatewayRouteProblem.HelperMissing,
                    entry.Command,
                    currentHelperUsable));
            }
        }
        return result;
    }

    /// <summary>Points a route's definition at this copy of the helper.</summary>
    /// <remarks>
    /// For a Kytto that was moved or reinstalled in another folder after Gateway mode was
    /// turned on: the config still names the old location. Only the command path
    /// changes; the route, its credentials and its tool mask stay as they are.
    /// </remarks>
    public GatewayMigrationResult Repair(string routeId)
    {
        var route = _routes.Route(routeId);
        if (!File.Exists(_helperPath)) throw GatewayMigrationException.HelperUnavailable(_helperPath);
        if (IsTemporaryZipExtraction(_helperPath))
        {
            throw GatewayMigrationException.HelperLocationUnstable(Path.GetDirectoryName(_helperPath) ?? _helperPath);
        }
        var target = TargetFor(route.ClientID);
        var replacement = GatewayDefinition(target, route.ServerName, route.Id);
        var receipt = WriteReplacement(replacement, target, route.Id, route.ServerName);
        _ledger.Record(target.Path, receipt.Digest);
        GatewayRoute updated;
        try { updated = _routes.UpdateGatewayCommand(route.Id, _helperPath); }
        catch (GatewayRouteException) { updated = route; }
        return new GatewayMigrationResult(updated, receipt.BackupID, receipt.PathDisplay);
    }

    /// <summary>Carries a rotated secret into every route that was holding the old one.</summary>
    /// <remarks>
    /// A route keeps its own copy of each value, and the original definition it would
    /// restore. Rotating in the configs alone would leave a Gateway server running on
    /// the revoked token, and bring it back on Restore Direct.
    /// </remarks>
    /// <returns>The routes that changed, whose clients need a restart.</returns>
    public IReadOnlyList<GatewayRoute> RotateEnvironmentValue(string key, string oldValue, string newValue)
    {
        if (oldValue.Length == 0 || oldValue == newValue) return [];
        IReadOnlyList<GatewayRoute> all;
        try { all = _routes.All(); }
        catch (GatewayRouteException) { return []; }

        var changed = new List<GatewayRoute>();
        foreach (var route in all)
        {
            var touched = false;
            foreach (var reference in route.Environment.Where(reference => reference.Key == key))
            {
                if (TryValue(reference.SecretID) != oldValue) continue;
                try
                {
                    _secrets.Store(newValue, reference.SecretID);
                    touched = true;
                }
                catch (SecretsException) { }
            }
            if (!touched) continue;
            changed.Add(route);

            // Best effort: a definition that cannot be rewritten is left alone, and
            // Restore Direct then brings back the old value as it always did.
            var format = _descriptors.FirstOrDefault(candidate => candidate.Id == route.ClientID)?.EditableServerMap?.Format;
            if (format is null || TryValue(route.DirectDefinitionSecretID) is not { } original) continue;
            if (ReplacingEnvironmentValue(key, oldValue, newValue, original, format.Value) is not { } rewritten ||
                rewritten == original) continue;
            try { _secrets.Store(rewritten, route.DirectDefinitionSecretID); }
            catch (SecretsException) { }
        }
        return changed;
    }

    /// <summary>
    /// The same definition text with one environment value replaced, or null if it
    /// does not parse. Only a value that equals <paramref name="oldValue"/> is touched.
    /// </summary>
    internal static string? ReplacingEnvironmentValue(
        string key,
        string oldValue,
        string newValue,
        string definition,
        ConfigFormat format)
    {
        if (format == ConfigFormat.Json)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(definition); }
            catch (JsonParseException) { return null; }
            var edits = (document.Root["env"]?.Members ?? [])
                .Where(member => member.Key == key && member.Value.StringValue == oldValue)
                .Select(member => (member.Value.Span, JsonText.String(newValue)))
                .ToArray();
            return edits.Length == 0 ? definition : document.Replacing(edits);
        }

        TomlDocument toml;
        try { toml = TomlDocument.Parse(definition); }
        catch (TomlParseException) { return null; }
        var spans = new List<TomlSpan>();
        foreach (var table in toml.Tables)
        {
            if (table.Path.LastOrDefault() == "env")
            {
                spans.AddRange(table.Pairs
                    .Where(pair => pair.Name == key && pair.Value.StringValue == oldValue)
                    .Select(pair => pair.Value.Span));
            }
            foreach (var pair in table.Pairs.Where(pair => pair.Key[0] == "env"))
            {
                if (pair.Key.Count == 2 && pair.Key[1] == key && pair.Value.StringValue == oldValue)
                {
                    spans.Add(pair.Value.Span);
                }
                spans.AddRange((pair.Value.InlinePairs ?? [])
                    .Where(inner => inner.Name == key && inner.Value.StringValue == oldValue)
                    .Select(inner => inner.Value.Span));
            }
        }
        return spans.Count == 0
            ? definition
            : toml.Replacing(spans.Select(span => (span, TomlText.String(newValue))));
    }

    /// <summary>
    /// The helper definition for a server already in Gateway mode, rendered the way
    /// <see cref="Enable"/> renders it, at this copy's helper path.
    /// </summary>
    private string GatewayDefinition(Target target, string serverName, string routeId) =>
        Definition(target, serverName, _helperPath, ["--route", routeId], []);

    /// <summary>A stdio definition rendered from the model, in the target file's format.</summary>
    /// <remarks>
    /// Codex settings about the entry rather than the process (timeouts, which tools to
    /// show) stay in the block as they were, as in <see cref="Enable"/>.
    /// </remarks>
    private static string Definition(
        Target target,
        string serverName,
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyList<EnvEntry> environment)
    {
        if (target.Source.Format == ConfigFormat.Json)
        {
            var document = JsonDocument.Parse(target.SourceText);
            var member = document.ValueAt(target.Source.ServersKey)?.Members?
                .FirstOrDefault(candidate => Server.Identity(candidate.Key) == Server.Identity(serverName))
                ?? throw GatewayMigrationException.DefinitionMissing();
            return new ServerDraft
            {
                Name = member.Key,
                Command = command,
                Args = arguments,
                Env = environment,
            }.Definition(
                document.LineIndent(member.Value.Span.Start),
                IndentStyle.Detect(document.SourceText).Text,
                document.Newline());
        }

        var toml = TomlDocument.Parse(target.SourceText);
        var name = toml.ServerNames(target.Source.ServersKey)
            .FirstOrDefault(candidate => Server.Identity(candidate) == Server.Identity(serverName))
            ?? throw GatewayMigrationException.DefinitionMissing();
        var clientSide = toml.Table(target.Source.ServersKey, name)?.Pairs
            .Where(pair => CodexClientKeys.Contains(pair.Key[0])) ?? [];
        return string.Join("\n", new[]
        {
            TomlBuilder.ServerBlock(name, target.Source.ServersKey, command, arguments, null, environment, null),
        }.Concat(clientSide.Select(pair => toml.Slice(pair.Span))));
    }

    /// <summary>Every Gateway definition in a config: its server, route and the helper path it names.</summary>
    private IReadOnlyList<(string ServerName, string RouteID, string Command)> GatewayEntries(string text, ClientId clientId)
    {
        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId);
        var source = descriptor?.EditableServerMap
            ?? throw AuthoringException.NoSourceForClient(clientId);
        var result = new List<(string, string, string)>();
        if (source.Format == ConfigFormat.Json)
        {
            var document = JsonDocument.Parse(text);
            foreach (var member in document.ValueAt(source.ServersKey)?.Members ?? [])
            {
                var command = member.Value["command"]?.StringValue;
                if (command is not null && RouteID(command,
                        member.Value["args"]?.Elements?.Select(item => item.StringValue).ToArray()) is { } id)
                {
                    result.Add((member.Key, id, command));
                }
            }
            return result;
        }

        var toml = TomlDocument.Parse(text);
        foreach (var name in toml.ServerNames(source.ServersKey))
        {
            var table = toml.Table(source.ServersKey, name);
            var command = table?.Value("command")?.StringValue;
            if (command is not null && RouteID(command,
                    table?.Value("args")?.Elements?.Select(item => item.StringValue).ToArray()) is { } id)
            {
                result.Add((name, id, command));
            }
        }
        return result;
    }

    /// <summary>A stored value, or null when it is gone or cannot be read.</summary>
    private string? TryValue(string id)
    {
        try { return _secrets.Value(id); }
        catch (SecretsException) { return null; }
    }

    public void ValidateBackupRestore(string text, ClientId clientId)
    {
        var known = _routes.All().Select(route => route.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in RouteIDs(text, clientId).Where(id => !known.Contains(id)))
        {
            throw GatewayMigrationException.OrphanedRouteInBackup(id);
        }
    }

    public void ReconcileAfterBackupRestore(ClientId clientId)
    {
        var target = TargetFor(clientId);
        var configured = RouteIDs(target.SourceText, clientId).ToHashSet(StringComparer.Ordinal);
        foreach (var route in _routes.All().Where(route => route.ClientID == clientId && !configured.Contains(route.Id)))
        {
            try { _routes.Remove(route.Id); } catch (GatewayRouteException) { }
            TryDelete(route.DirectDefinitionSecretID);
            foreach (var reference in route.Environment) TryDelete(reference.SecretID);
        }
    }

    private Target TargetFor(Server server, ClientId clientId, bool requireNoRoute)
    {
        if (!File.Exists(_helperPath)) throw GatewayMigrationException.HelperUnavailable(_helperPath);
        // The helper's path is written into the client's config, so it has to outlive
        // this launch.
        if (IsTemporaryZipExtraction(_helperPath))
        {
            throw GatewayMigrationException.HelperLocationUnstable(Path.GetDirectoryName(_helperPath) ?? _helperPath);
        }
        if (server.Transport != Transport.Stdio) throw GatewayMigrationException.UnsupportedTransport();
        // Per client as well as per row: a name that is a plugin's in this client is
        // installed software here whatever it is elsewhere (§4).
        if (server.IsBundled || server.HoldsPluginCopy(clientId)) throw GatewayMigrationException.BundledServer();
        if (server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) != Enablement.Enabled)
        {
            throw GatewayMigrationException.ServerNotEnabled();
        }
        if (requireNoRoute && _routes.Route(server.Id, clientId) is not null)
        {
            throw GatewayMigrationException.RouteAlreadyExists();
        }
        return TargetFor(clientId);
    }

    private Target TargetFor(ClientId clientId)
    {
        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId);
        var source = descriptor?.EditableServerMap
            ?? throw AuthoringException.NoSourceForClient(clientId);
        // The helper is written as `command` + `args`, which OpenCode does not read;
        // its argv-array entry is not modelled here yet.
        if (descriptor!.ServerSchema.Shape != ServerSchema.EntryShape.McpServers)
        {
            throw GatewayMigrationException.InvalidDefinition($"{descriptor.DisplayName} is not supported by Gateway mode yet");
        }
        var path = _resolver.ResolveServerMap(source.File, clientId);
        var display = _resolver.DisplayServerMap(source.File, clientId);
        if (Digest.OfFile(path) != _ledger.DigestFor(path))
        {
            throw ConfigWriteException.ChangedOnDisk(display);
        }
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException)
        {
            throw ConfigWriteException.Unreadable(display, "not valid UTF-8");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw ConfigWriteException.Unreadable(display, error.Message);
        }
        return new Target(clientId, source, path, display, text);
    }

    /// <summary>Whether a path lies in the folder Explorer extracts a ZIP to when an app inside it is opened.</summary>
    /// <remarks>
    /// <c>%TEMP%\Temp1_Kytto.zip\…</c>: deleted when Explorer is done with it, after
    /// which every client pointed at the helper would fail to start the server.
    /// </remarks>
    internal static bool IsTemporaryZipExtraction(string path)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
        return full[temp.Length..].Split(Path.DirectorySeparatorChar).Any(segment =>
            System.Text.RegularExpressions.Regex.IsMatch(segment, @"^Temp\d+_.+\.zip$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    /// <summary>Codex keys about the entry rather than the process it launches.</summary>
    private static readonly string[] CodexClientKeys =
        ["enabled", "startup_timeout_sec", "startup_timeout_ms", "tool_timeout_sec", "enabled_tools", "disabled_tools"];

    /// <summary>
    /// <c>${…}</c> is expanded by the client that launches a definition. Behind the
    /// gateway the client launches the helper instead, so the server would receive the
    /// placeholder text itself.
    /// </summary>
    private static void RefusePlaceholders(string command, IReadOnlyList<string> arguments, IReadOnlyList<EnvEntry> environment)
    {
        if (new[] { command }.Concat(arguments).Concat(environment.Select(entry => entry.Value ?? ""))
            .Any(value => value.Contains("${", StringComparison.Ordinal)))
        {
            throw GatewayMigrationException.InvalidDefinition("it uses ${…} placeholders, which only its client can expand");
        }
    }

    private DirectDefinition Direct(Target target, string serverId, string routeId) =>
        target.Source.Format == ConfigFormat.Json
            ? DirectJson(target, serverId, routeId)
            : DirectToml(target, serverId, routeId);

    private DirectDefinition DirectJson(Target target, string serverId, string routeId)
    {
        var document = JsonDocument.Parse(target.SourceText);
        var map = document.ValueAt(target.Source.ServersKey);
        var member = map?.Members?.FirstOrDefault(candidate => Server.Identity(candidate.Key) == serverId)
            ?? throw GatewayMigrationException.DefinitionMissing();
        var node = member.Value;
        // The route carries command, args and env; anything else would be lost behind
        // the helper — a working directory, headers, an envFile.
        var extra = node.Keys.Where(key => key is not ("command" or "args" or "env" or "type")).ToArray();
        if (extra.Length > 0) throw GatewayMigrationException.Unmodelled(extra);
        if (node["type"] is { } type && type.StringValue != "stdio")
        {
            throw GatewayMigrationException.InvalidDefinition("it is not a stdio server");
        }
        var command = node["command"]?.StringValue;
        if (string.IsNullOrEmpty(command))
        {
            throw GatewayMigrationException.InvalidDefinition("its command is missing or is not a string");
        }
        var arguments = StringElements(node["args"], "one of its arguments is not a string");
        var environment = JsonEnvironment(node["env"]);
        RefusePlaceholders(command, arguments, environment);
        var unit = IndentStyle.Detect(document.SourceText).Text;
        var replacement = new ServerDraft
        {
            Name = member.Key,
            Command = _helperPath,
            Args = ["--route", routeId],
        }.Definition(document.LineIndent(member.Value.Span.Start), unit, document.Newline());
        return new DirectDefinition(
            member.Key,
            document.Slice(member.Value.Span),
            command,
            arguments,
            environment,
            replacement);
    }

    private DirectDefinition DirectToml(Target target, string serverId, string routeId)
    {
        var document = TomlDocument.Parse(target.SourceText);
        var name = document.ServerNames(target.Source.ServersKey)
            .FirstOrDefault(candidate => Server.Identity(candidate) == serverId)
            ?? throw GatewayMigrationException.DefinitionMissing();
        var table = document.Table(target.Source.ServersKey, name)
            ?? throw GatewayMigrationException.DefinitionMissing();
        var sourceText = document.ServerDefinitionText(name, target.Source.ServersKey)
            ?? throw GatewayMigrationException.DefinitionMissing();

        // Restore puts this text back as one block, so it must be this server's tables
        // and nothing else — not a neighbour sitting between two of them, which would
        // then be written twice.
        string[] ownPath = [target.Source.ServersKey, name];
        TomlDocument slice;
        try
        {
            slice = TomlDocument.Parse(sourceText);
        }
        catch (TomlParseException)
        {
            throw GatewayMigrationException.InvalidDefinition(
                "its tables are not next to each other in config.toml; move them together first");
        }
        if (!slice.Tables.All(candidate => candidate.Path.Count >= ownPath.Length && candidate.Path.Take(ownPath.Length).SequenceEqual(ownPath)))
        {
            throw GatewayMigrationException.InvalidDefinition(
                "its tables are not next to each other in config.toml; move them together first");
        }
        if (slice.Tables.Select(candidate => candidate.Path)
                .FirstOrDefault(path => path.Count > ownPath.Length && !path.SequenceEqual([.. ownPath, "env"])) is { } other)
        {
            throw GatewayMigrationException.Unmodelled([other[ownPath.Length]]);
        }

        // Launch settings move into the route; settings that tell Codex how to treat
        // the entry (timeouts, which tools to show) apply to the helper just the same
        // and stay in the block, spelled as they were.
        var clientSide = table.Pairs.Where(pair => CodexClientKeys.Contains(pair.Key[0])).ToArray();
        var extra = table.Pairs.Select(pair => pair.Key[0])
            .Where(key => key is not ("command" or "args" or "env") && !CodexClientKeys.Contains(key))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (extra.Length > 0) throw GatewayMigrationException.Unmodelled(extra);

        var command = table.Value("command")?.StringValue;
        if (string.IsNullOrEmpty(command))
        {
            throw GatewayMigrationException.InvalidDefinition("its command is missing or is not a string");
        }
        var arguments = TomlStringElements(table.Value("args"), "one of its arguments is not a string");
        var environment = TomlEnvironment(table, document.Table(target.Source.ServersKey, name, "env"));
        RefusePlaceholders(command, arguments, environment);
        var replacement = string.Join("\n", new[]
        {
            TomlBuilder.ServerBlock(
                name,
                target.Source.ServersKey,
                _helperPath,
                ["--route", routeId],
                null,
                [],
                null),
        }.Concat(clientSide.Select(pair => document.Slice(pair.Span))));
        return new DirectDefinition(name, sourceText, command, arguments, environment, replacement);
    }

    private static IReadOnlyList<string> StringElements(JsonNode? node, string error)
    {
        if (node is null) return [];
        var elements = node.Elements ?? throw GatewayMigrationException.InvalidDefinition(error);
        var values = elements.Select(item => item.StringValue).ToArray();
        if (values.Any(value => value is null)) throw GatewayMigrationException.InvalidDefinition(error);
        return values.Select(value => value!).ToArray();
    }

    private static IReadOnlyList<string> TomlStringElements(TomlValue? node, string error)
    {
        if (node is null) return [];
        var elements = node.Elements ?? throw GatewayMigrationException.InvalidDefinition(error);
        var values = elements.Select(item => item.StringValue).ToArray();
        if (values.Any(value => value is null)) throw GatewayMigrationException.InvalidDefinition(error);
        return values.Select(value => value!).ToArray();
    }

    private static IReadOnlyList<EnvEntry> JsonEnvironment(JsonNode? node)
    {
        if (node is null) return [];
        var members = node.Members
            ?? throw GatewayMigrationException.InvalidDefinition("its environment is not an object");
        return members.Select(member => new EnvEntry(
            member.Key,
            member.Value.StringValue ?? throw GatewayMigrationException.InvalidDefinition(
                $"environment value {member.Key} is not a string"))).ToArray();
    }

    /// <summary>Every way Codex accepts an environment.</summary>
    /// <remarks>
    /// An <c>[….env]</c> sub-table, an inline <c>env = { … }</c> (what Codex's own docs
    /// show), or dotted <c>env.K = …</c>. Missing one would start the server without it
    /// — and leave its values unredacted in the preview.
    /// </remarks>
    private static IReadOnlyList<EnvEntry> TomlEnvironment(TomlTable table, TomlTable? subtable)
    {
        var pairs = (subtable?.Pairs ?? []).Select(pair => (pair.Name, pair.Value)).ToList();
        if (table.Value("env") is { } env)
        {
            var inline = env.InlinePairs
                ?? throw GatewayMigrationException.InvalidDefinition("its environment is not a table");
            pairs.AddRange(inline.Select(pair => (pair.Name, pair.Value)));
        }
        pairs.AddRange(table.Pairs
            .Where(pair => pair.Key.Count == 2 && pair.Key[0] == "env")
            .Select(pair => ((string?)pair.Key[1], pair.Value)));
        return pairs.Select(pair => new EnvEntry(
            pair.Name ?? throw GatewayMigrationException.InvalidDefinition(
                "one of its environment values is not a simple key"),
            pair.Value.StringValue ?? throw GatewayMigrationException.InvalidDefinition(
                "one of its environment values is not a string"))).ToArray();
    }

    private IReadOnlyList<string> RouteIDs(string text, ClientId clientId)
    {
        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId);
        var source = descriptor?.EditableServerMap
            ?? throw AuthoringException.NoSourceForClient(clientId);
        if (source.Format == ConfigFormat.Json)
        {
            var document = JsonDocument.Parse(text);
            return document.ValueAt(source.ServersKey)?.Members?
                .Select(member => RouteID(member.Value["command"]?.StringValue,
                    member.Value["args"]?.Elements?.Select(item => item.StringValue).ToArray()))
                .Where(id => id is not null).Select(id => id!).ToArray() ?? [];
        }

        var toml = TomlDocument.Parse(text);
        return toml.ServerNames(source.ServersKey).Select(name =>
        {
            var table = toml.Table(source.ServersKey, name);
            return RouteID(
                table?.Value("command")?.StringValue,
                table?.Value("args")?.Elements?.Select(item => item.StringValue).ToArray());
        }).Where(id => id is not null).Select(id => id!).ToArray();
    }

    private static string? RouteID(string? command, IReadOnlyList<string?>? arguments)
    {
        if (command is null ||
            !Path.GetFileNameWithoutExtension(command).Equals("kytto-mcp-proxy", StringComparison.OrdinalIgnoreCase) ||
            arguments is not { Count: 2 } || arguments[0] != "--route" ||
            !Guid.TryParse(arguments[1], out var id)) return null;
        return id.ToString("D").ToLowerInvariant();
    }

    private WriteReceipt WriteReplacement(
        string replacement,
        Target target,
        string? routeId,
        string serverName)
    {
        if (target.Source.Format == ConfigFormat.Json)
        {
            return _writer.Edit<JsonDocument>(
                target.Path,
                target.ClientID,
                target.PathDisplay,
                _ledger.DigestFor(target.Path),
                document =>
                {
                    var map = document.ValueAt(target.Source.ServersKey);
                    var member = map?.Members?.FirstOrDefault(candidate =>
                        Server.Identity(candidate.Key) == Server.Identity(serverName))
                        ?? throw GatewayMigrationException.DefinitionMissing();
                    if (routeId is not null &&
                        RouteID(member.Value["command"]?.StringValue,
                            member.Value["args"]?.Elements?.Select(item => item.StringValue).ToArray()) != routeId)
                    {
                        throw GatewayMigrationException.RouteMismatch();
                    }
                    return document.SettingMember(member.Key, [target.Source.ServersKey], replacement);
                });
        }

        return _writer.Edit<TomlDocument>(
            target.Path,
            target.ClientID,
            target.PathDisplay,
            _ledger.DigestFor(target.Path),
            document =>
            {
                var name = document.ServerNames(target.Source.ServersKey)
                    .FirstOrDefault(candidate => Server.Identity(candidate) == Server.Identity(serverName))
                    ?? throw GatewayMigrationException.DefinitionMissing();
                if (routeId is not null)
                {
                    var table = document.Table(target.Source.ServersKey, name);
                    if (RouteID(
                            table?.Value("command")?.StringValue,
                            table?.Value("args")?.Elements?.Select(item => item.StringValue).ToArray()) != routeId)
                    {
                        throw GatewayMigrationException.RouteMismatch();
                    }
                }
                return document.SettingServer(name, target.Source.ServersKey, replacement);
            });
    }

    private static string RedactedPreview(DirectDefinition direct, ConfigFormat format)
    {
        if (format == ConfigFormat.Json)
        {
            var document = JsonDocument.Parse(direct.SourceText);
            var members = document.Root["env"]?.Members;
            return members is null
                ? direct.SourceText
                : document.Replacing(members.Select(member =>
                    (member.Value.Span, JsonText.String("<stored in credentials>"))));
        }

        const string Withheld = "<preview withheld: the definition could not be redacted>";
        TomlDocument toml;
        try
        {
            toml = TomlDocument.Parse(direct.SourceText);
        }
        catch (TomlParseException)
        {
            return direct.Environment.Count == 0 ? direct.SourceText : Withheld;
        }
        var spans = new List<TomlSpan>();
        foreach (var table in toml.Tables)
        {
            if (table.Path.LastOrDefault() == "env") spans.AddRange(table.Pairs.Select(pair => pair.Value.Span));
            foreach (var pair in table.Pairs.Where(pair => pair.Key[0] == "env"))
            {
                if (pair.Key.Count == 2) spans.Add(pair.Value.Span);
                spans.AddRange((pair.Value.InlinePairs ?? []).Select(inner => inner.Value.Span));
            }
        }
        // Every value the route stores has to be covered, or the preview shows one.
        if (spans.Count < direct.Environment.Count) return Withheld;
        return spans.Count == 0
            ? direct.SourceText
            : toml.Replacing(spans.Select(span => (span, TomlText.String("<stored in credentials>"))));
    }

    private static string NormalizeRouteID(string routeId) =>
        Guid.TryParse(routeId, out var id)
            ? id.ToString("D").ToLowerInvariant()
            : throw GatewayRouteException.InvalidRouteID(routeId);

    private void TryDelete(string id)
    {
        try { _secrets.Delete(id); } catch (SecretsException) { }
    }
}
