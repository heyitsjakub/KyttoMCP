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
    string PathDisplay);

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
        var source = _secrets.Value(route.DirectDefinitionSecretID)
            ?? throw GatewayRouteException.MissingSecret("original definition");
        var target = TargetFor(route.ClientID);
        var receipt = WriteReplacement(source, target, route.Id, route.ServerName);
        _ledger.Record(target.Path, receipt.Digest);

        // The authoritative config is already Direct again. Cleanup is best-effort
        // so a Credential Manager problem cannot turn that success into a false failure.
        try { _routes.Remove(route.Id); } catch (GatewayRouteException) { }
        TryDelete(route.DirectDefinitionSecretID);
        foreach (var reference in route.Environment) TryDelete(reference.SecretID);

        return new GatewayMigrationResult(route, receipt.BackupID, receipt.PathDisplay);
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
        if (server.Transport != Transport.Stdio) throw GatewayMigrationException.UnsupportedTransport();
        if (server.IsBundled) throw GatewayMigrationException.BundledServer();
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
        var command = node["command"]?.StringValue;
        if (string.IsNullOrEmpty(command))
        {
            throw GatewayMigrationException.InvalidDefinition("its command is missing or is not a string");
        }
        var arguments = StringElements(node["args"], "one of its arguments is not a string");
        var environment = JsonEnvironment(node["env"]);
        var unit = IndentStyle.Detect(document.SourceText).Text;
        var replacement = new ServerDraft
        {
            Name = member.Key,
            Command = _helperPath,
            Args = ["--route", routeId],
        }.Definition(document.LineIndent(member.Value.Span.Start), unit);
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
        var command = table.Value("command")?.StringValue;
        if (string.IsNullOrEmpty(command))
        {
            throw GatewayMigrationException.InvalidDefinition("its command is missing or is not a string");
        }
        var arguments = TomlStringElements(table.Value("args"), "one of its arguments is not a string");
        var environment = TomlEnvironment(document.Table(target.Source.ServersKey, name, "env"));
        var replacement = TomlBuilder.ServerBlock(
            name,
            target.Source.ServersKey,
            _helperPath,
            ["--route", routeId],
            null,
            [],
            null);
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

    private static IReadOnlyList<EnvEntry> TomlEnvironment(TomlTable? table) =>
        (table?.Pairs ?? []).Select(pair => new EnvEntry(
            pair.Name ?? throw GatewayMigrationException.InvalidDefinition(
                "one of its environment values is not a simple key"),
            pair.Value.StringValue ?? throw GatewayMigrationException.InvalidDefinition(
                "one of its environment values is not a string"))).ToArray();

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

        var toml = TomlDocument.Parse(direct.SourceText);
        var environment = toml.Tables.FirstOrDefault(table => table.Path.LastOrDefault() == "env");
        return environment is null
            ? direct.SourceText
            : toml.Replacing(environment.Pairs.Select(pair =>
                (pair.Value.Span, TomlText.String("<stored in credentials>"))));
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
