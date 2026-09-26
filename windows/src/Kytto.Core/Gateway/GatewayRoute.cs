using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.Core.Gateway;

/// <summary>An environment value held by Credential Manager rather than the route file.</summary>
public sealed record GatewayEnvironmentReference(string Key, string SecretID);

/// <summary>
/// Durable, secret-free identity for one server-client gateway connection (§3.3).
/// </summary>
public sealed record GatewayRoute(
    string Id,
    string ServerID,
    string ServerName,
    ClientId ClientID,
    string GatewayCommand,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<GatewayEnvironmentReference> Environment,
    string DirectDefinitionSecretID,
    DateTimeOffset CreatedAt)
{
    private readonly IReadOnlyList<string>? _exposedTools;

    /// <summary>The tools this route lets through, or null to let everything through (§7.11).</summary>
    /// <remarks>
    /// <para>
    /// Null and empty mean different things — "no restriction" and "expose
    /// nothing" — which is why this is not just a list with emptiness standing in
    /// for "everything".
    /// </para>
    /// <para>
    /// Tool names are model-facing identifiers, not secrets, so unlike the
    /// definition and environment they belong in the route record rather than in
    /// the credential store.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? ExposedTools
    {
        get => _exposedTools;
        init => _exposedTools = value?.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The same route, exposing a different set of tools. Null clears masking.</summary>
    public GatewayRoute Exposing(IReadOnlyList<string>? tools) => this with { ExposedTools = tools };

    public static string DirectDefinitionIdentifier(string routeId) =>
        $"gateway-definition::{routeId.ToLowerInvariant()}";

    public static string EnvironmentIdentifier(string routeId, string key)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"gateway-env::{routeId.ToLowerInvariant()}::{encoded}";
    }
}

public sealed class GatewayRouteException(string message) : Exception(message)
{
    public static GatewayRouteException InvalidRouteID(string id) =>
        new($"{id} is not a valid gateway route id.");

    public static GatewayRouteException DuplicateRoute(string id) =>
        new($"Gateway route {id} already exists.");

    public static GatewayRouteException UnknownRoute(string id) =>
        new($"Gateway route {id} no longer exists. Restore Direct mode in Kytto.");

    public static GatewayRouteException Unreadable(string reason) =>
        new($"Kytto could not read its gateway routes: {reason}");

    public static GatewayRouteException MissingSecret(string key) =>
        new($"Gateway secret {key} is unavailable. Restore Direct mode in Kytto.");
}

/// <summary>Secret-free route registry, atomically replaced on each edit.</summary>
public sealed class GatewayRouteStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();

    public GatewayRouteStore(KyttoPaths paths) : this(paths.GatewayRoutesFile) { }

    public GatewayRouteStore(string path) => Path = System.IO.Path.GetFullPath(path);

    public string Path { get; }

    public IReadOnlyList<GatewayRoute> All()
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return [];
            try
            {
                return (JsonSerializer.Deserialize<GatewayRoute[]>(File.ReadAllText(Path), Options) ?? [])
                    .Select(route => route with { ServerID = Server.Identity(route.ServerID) })
                    .OrderBy(route => route.ClientID.Raw(), StringComparer.Ordinal)
                    .ThenBy(route => route.ServerID, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                throw GatewayRouteException.Unreadable(error.Message);
            }
        }
    }

    public GatewayRoute Route(string id) =>
        All().FirstOrDefault(route => route.Id == id)
        ?? throw GatewayRouteException.UnknownRoute(id);

    public GatewayRoute? Route(string serverId, ClientId clientId)
    {
        var normalized = Server.Identity(serverId);
        return All().FirstOrDefault(route => route.ServerID == normalized && route.ClientID == clientId);
    }

    public void Insert(GatewayRoute route)
    {
        if (!Guid.TryParse(route.Id, out _)) throw GatewayRouteException.InvalidRouteID(route.Id);
        lock (_lock)
        {
            var routes = All().ToList();
            if (routes.Any(candidate => candidate.Id == route.Id ||
                candidate.ServerID == route.ServerID && candidate.ClientID == route.ClientID))
            {
                throw GatewayRouteException.DuplicateRoute(route.Id);
            }
            routes.Add(route with { ServerID = Server.Identity(route.ServerID) });
            Save(routes);
        }
    }

    /// <summary>Narrows or widens what a route exposes (§7.11).</summary>
    /// <remarks>
    /// Only the allow list moves. Nothing about how the upstream server is launched
    /// is touched, and no client config is rewritten — the route id in the config
    /// already points here, so masking takes effect the next time the client starts
    /// the helper.
    /// </remarks>
    public GatewayRoute Update(string id, IReadOnlyList<string>? exposedTools)
    {
        lock (_lock)
        {
            var routes = All().ToList();
            var index = routes.FindIndex(route => route.Id == id);
            if (index < 0) throw GatewayRouteException.UnknownRoute(id);

            var updated = routes[index].Exposing(exposedTools);
            routes[index] = updated;
            Save(routes);
            return updated;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var routes = All().ToList();
            if (routes.RemoveAll(route => route.Id == id) != 1)
            {
                throw GatewayRouteException.UnknownRoute(id);
            }
            Save(routes);
        }
    }

    public GatewayLaunch Resolve(string id, ISecretStore secrets)
    {
        var route = Route(id);
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in route.Environment)
        {
            environment[reference.Key] = secrets.Value(reference.SecretID)
                ?? throw GatewayRouteException.MissingSecret(reference.Key);
        }
        return new GatewayLaunch(route, environment);
    }

    private void Save(IEnumerable<GatewayRoute> routes)
    {
        var ordered = routes
            .OrderBy(route => route.ClientID.Raw(), StringComparer.Ordinal)
            .ThenBy(route => route.ServerID, StringComparer.Ordinal)
            .ToArray();
        // Routes hold no secrets — the values live in the credential store — but the
        // file is private from the moment it exists rather than tightened afterwards,
        // because "afterwards" is a window and this costs nothing (§6).
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
        {
            KyttoStorage.EnsurePrivate(directory, directory);
        }
        KyttoStorage.TightenOwnFile(Path);
        AtomicWriter.Write(JsonSerializer.Serialize(ordered, Options), Path);
    }
}

public sealed record GatewayLaunch(
    GatewayRoute Route,
    IReadOnlyDictionary<string, string> Environment);
