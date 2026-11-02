using Kytto.Core.Json;
using Kytto.Core.Model;

namespace Kytto.Core.Clients;

/// <summary>
/// How one server entry is spelled inside a JSON server map (§4).
/// </summary>
/// <remarks>
/// The first five clients agreed on the per-server shape — <c>command</c>,
/// <c>args</c>, <c>env</c>, and <c>url</c> with an optional <c>type</c> — which is
/// why a definition could travel between them as text. The clients that came after
/// them do not: Antigravity writes a remote endpoint as <c>serverUrl</c> and
/// rejects a <c>type</c>, Devin Desktop states a transport as <c>transport</c>, and
/// OpenCode spells the whole entry differently. This is data on the descriptor, next
/// to the path and the off switch, so the difference is written down once.
/// </remarks>
public sealed record ServerSchema
{
    public enum EntryShape
    {
        /// <summary><c>command</c> + <c>args</c> + <c>env</c>, a remote endpoint under a URL key.</summary>
        McpServers,

        /// <summary>
        /// OpenCode: <c>type: "local" | "remote"</c>, the command and its arguments
        /// as one argv array in <c>command</c>, and <c>environment</c> instead of <c>env</c>.
        /// </summary>
        OpenCode,
    }

    public required EntryShape Shape { get; init; }

    /// <summary>Where a remote endpoint is written. Any of <see cref="RemoteUrlKeys"/> is read.</summary>
    public required string RemoteUrlKey { get; init; }

    /// <summary>
    /// Where a remote transport is written, or null when the endpoint key is all the
    /// client looks at.
    /// </summary>
    public string? TransportKey { get; init; }

    /// <summary>
    /// False when the client assumes Streamable HTTP and only a different transport
    /// is worth stating.
    /// </summary>
    public required bool StatesDefaultTransport { get; init; }

    /// <summary>Whether the client still accepts the legacy HTTP+SSE transport.</summary>
    public required bool AcceptsSse { get; init; }

    /// <summary>Claude Desktop, Claude Code, Cursor and VS Code.</summary>
    public static ServerSchema Standard { get; } = new()
    {
        Shape = EntryShape.McpServers,
        RemoteUrlKey = "url",
        TransportKey = "type",
        StatesDefaultTransport = true,
        AcceptsSse = true,
    };

    /// <summary>
    /// Remote endpoints under <c>serverUrl</c>, no <c>type</c>, Streamable HTTP only
    /// (https://antigravity.google/docs/mcp).
    /// </summary>
    public static ServerSchema Antigravity { get; } = new()
    {
        Shape = EntryShape.McpServers,
        RemoteUrlKey = "serverUrl",
        TransportKey = null,
        StatesDefaultTransport = false,
        AcceptsSse = false,
    };

    /// <summary>
    /// <c>url</c>, with <c>transport: "sse"</c> only for the legacy transport
    /// (https://docs.devin.ai/cli/extensibility/mcp/configuration).
    /// </summary>
    public static ServerSchema Devin { get; } = new()
    {
        Shape = EntryShape.McpServers,
        RemoteUrlKey = "url",
        TransportKey = "transport",
        StatesDefaultTransport = false,
        AcceptsSse = true,
    };

    /// <summary>https://opencode.ai/docs/mcp-servers/</summary>
    public static ServerSchema OpenCode { get; } = new()
    {
        Shape = EntryShape.OpenCode,
        RemoteUrlKey = "url",
        TransportKey = "type",
        StatesDefaultTransport = true,
        AcceptsSse = true,
    };

    /// <summary>
    /// Every key a remote endpoint is read under, the written one first. Antigravity
    /// documents <c>url</c> as an alternative to its <c>serverUrl</c>; the clients that
    /// write <c>url</c> do not read <c>serverUrl</c>, so neither does Kytto.
    /// </summary>
    internal IReadOnlyList<string> RemoteUrlKeys =>
        RemoteUrlKey == "url" ? ["url"] : [RemoteUrlKey, "url"];

    /// <summary>The key holding environment variables.</summary>
    public string EnvironmentKey => Shape == EntryShape.OpenCode ? "environment" : "env";

    /// <summary>
    /// Keys that record one client's own state about a server rather than how to
    /// start it. A copy into another client starts from that client's defaults: a
    /// server switched off in Antigravity must not arrive switched off in Devin
    /// Desktop while the matrix says on.
    /// </summary>
    internal static readonly string[] ClientStateKeys = ["disabled", "enabled", "disabledTools", "enabledTools"];

    // MARK: - Reading

    internal ServerFields FieldsOf(JsonNode node)
    {
        if (Shape == EntryShape.McpServers)
        {
            var standard = ServerFields.FromJson(node);
            return new ServerFields
            {
                Command = standard.Command,
                Args = standard.Args,
                Env = standard.Env,
                WorkingDirectory = standard.WorkingDirectory,
                Url = RemoteUrlKeys.Select(key => node[key]?.StringValue).OfType<string>().FirstOrDefault(),
                DeclaredType = node["type"]?.StringValue
                    ?? (TransportKey is { } key ? node[key]?.StringValue : null),
            };
        }

        var argv = node["command"]?.Elements?
            .Select(element => element.StringValue)
            .OfType<string>()
            .ToArray() ?? [];
        return new ServerFields
        {
            Command = argv.Length > 0 ? argv[0] : node["command"]?.StringValue,
            Args = argv.Length > 0 ? argv[1..] : [],
            Url = node["url"]?.StringValue,
            WorkingDirectory = node["cwd"]?.StringValue,
            Env = (node["environment"]?.Members ?? [])
                .Select(member => new EnvEntry(member.Key, member.Value.StringValue))
                .ToArray(),
            DeclaredType = node["type"]?.StringValue switch
            {
                "local" => Transport.Stdio.Raw(),
                "remote" => Transport.Http.Raw(),
                _ => null,
            },
        };
    }

    // MARK: - Moving a definition between clients

    /// <summary>
    /// Settings a definition carries that a definition rendered from Kytto's model
    /// (command, args, env, url) would drop.
    /// </summary>
    internal IReadOnlyList<string> UnportableFields(JsonNode node)
    {
        var keys = node.Keys;
        string[] candidates = Shape == EntryShape.McpServers
            ? ["headers", "cwd", "envFile"]
            : ["headers", "cwd", "oauth"];
        return candidates.Where(keys.Contains).ToArray();
    }

    /// <summary>
    /// <paramref name="source"/>, written for a client with schema <paramref name="from"/>,
    /// respelt for one with this schema. Only the keys the two disagree on move;
    /// <c>headers</c>, <c>cwd</c> and keys Kytto has never heard of come along as they are.
    /// </summary>
    /// <returns>
    /// Null when this schema cannot express the definition — the legacy SSE transport in
    /// a client that only speaks Streamable HTTP — or when the shapes differ, in which
    /// case the definition is rendered from the model.
    /// </returns>
    internal string? Translating(string source, ServerSchema from)
    {
        if (Shape != EntryShape.McpServers || from.Shape != EntryShape.McpServers) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source);
        }
        catch (JsonParseException)
        {
            return null;
        }
        if (document.Root.Members is null) return null;

        var transport = from.FieldsOf(document.Root).ResolveTransport();
        if (transport == Transport.Sse && !AcceptsSse) return null;

        var text = source;
        void Remove(string key) => text = JsonDocument.Parse(text).RemovingMember(key, []);
        void Set(string key, string raw) => text = JsonDocument.Parse(text).SettingMember(key, [], raw);
        try
        {
            foreach (var key in ClientStateKeys) Remove(key);
            string[] transportKeys = ["type", "transport"];
            if (transport == Transport.Stdio)
            {
                // Only this client's own key may stay; Antigravity refuses an entry
                // carrying a `type` it does not know.
                foreach (var key in transportKeys.Where(key => key != TransportKey)) Remove(key);
                return text;
            }
            var url = RemoteUrlKeys.Select(key => document.Root[key]?.StringValue).OfType<string>().FirstOrDefault()
                ?? from.RemoteUrlKeys.Select(key => document.Root[key]?.StringValue).OfType<string>().FirstOrDefault();
            if (url is null) return null;
            foreach (var key in transportKeys) Remove(key);
            foreach (var key in RemoteUrlKeys.Concat(from.RemoteUrlKeys).Distinct().Where(key => key != RemoteUrlKey))
            {
                Remove(key);
            }
            Set(RemoteUrlKey, JsonText.String(url));
            if (TransportKey is { } transportKey && (StatesDefaultTransport || transport != Transport.Http))
            {
                Set(transportKey, JsonText.String(transport.Raw()));
            }
            return text;
        }
        catch (Exception error) when (error is JsonEditException or JsonParseException)
        {
            return null;
        }
    }
}
