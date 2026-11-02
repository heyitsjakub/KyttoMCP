using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Toml;

namespace Kytto.Core.Model;

/// <summary>Applying a draft to a definition that already exists (§6.3).</summary>
/// <remarks>
/// <para>
/// A draft knows five things: transport, command, args, url and env. A real
/// definition can hold more — <c>headers</c> carrying an Authorization token,
/// <c>cwd</c>, <c>envFile</c>, Codex's <c>startup_timeout_sec</c>, a key some client
/// added last month — and rebuilding the definition from the draft deleted all of
/// it on a Save that changed nothing.
/// </para>
/// <para>
/// So each of the five is compared with what the file says and spliced only where
/// it differs. A Save with no changes writes no bytes.
/// </para>
/// </remarks>
public static class ServerDraftSplicing
{
    /// <summary>
    /// The keys a draft speaks for. Everything else in a definition is the user's,
    /// and an edit leaves it alone.
    /// </summary>
    private static readonly string[] DescribedFields = ["command", "args", "url", "type", "env"];

    /// <summary>The draft spliced into the JSON definition object at <paramref name="path"/>.</summary>
    /// <param name="path">
    /// Where the definition object sits — <c>[serversKey, name]</c> in a config file,
    /// <c>[]</c> for a parked copy, which is the object alone.
    /// </param>
    /// <param name="schema">How the target client spells an entry; standard when omitted.</param>
    public static string SplicingJson(
        this ServerDraft draft,
        string source,
        IReadOnlyList<string> path,
        ServerSchema? schema = null)
    {
        schema ??= ServerSchema.Standard;
        if (schema.Shape == ServerSchema.EntryShape.OpenCode)
        {
            return SplicingOpenCode(draft, source, path);
        }
        var text = source;
        var unit = IndentStyle.Detect(source).Text;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] at = [.. path];

        (JsonDocument Document, JsonNode Node, IReadOnlyList<JsonMember> Members) Definition()
        {
            var document = JsonDocument.Parse(text);
            var node = document.Root.ValueAt(at)
                ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
            var members = node.Members
                ?? throw new JsonEditException(JsonEditErrorKind.NotAnObject, path);
            return (document, node, members);
        }

        var initial = Definition();
        // A field the draft describes, spelled twice, would make every comparison
        // below a guess about which copy the client reads.
        var described = DescribedFields
            .Concat(schema.RemoteUrlKeys)
            .Concat(schema.TransportKey is { } own ? [own] : [])
            .Distinct();
        foreach (var key in described) JsonEditor.SoleMember(key, initial.Members, path);
        var existing = schema.FieldsOf(initial.Node);
        var existingTransport = existing.ResolveTransport();
        // The endpoint stays under the key this entry already uses: Antigravity
        // reads `url` as well as its own `serverUrl`.
        var urlKey = schema.RemoteUrlKeys.FirstOrDefault(key => initial.Node[key] is not null)
            ?? schema.RemoteUrlKey;
        string[] typeKeys = schema.TransportKey is { } transportKey && transportKey != "type"
            ? ["type", transportKey]
            : ["type"];

        // Rendered to sit among the definition's own members: on one line if the
        // definition is written on one line, indented like its members otherwise.
        string Render(JsonBuildValue value)
        {
            var current = Definition();
            var oneLine = current.Members.Count > 0 &&
                !current.Document.ContainsNewline(current.Node.Span.Start, current.Node.Span.End);
            if (oneLine && value is JsonBuildValue.ObjectValue inline)
            {
                return JsonText.InlineObject(inline.Fields
                    .Select(field => (field.Key, JsonBuilder.Render(field.Value)))
                    .ToArray());
            }
            var indent = current.Members.Count > 0
                ? current.Document.LineIndent(current.Members[0].Span.Start)
                : current.Document.LineIndent(current.Node.Span.Start) + unit;
            return JsonBuilder.Render(value, oneLine ? "" : indent, unit, newline);
        }
        void Set(string key, string raw, params string[] under) =>
            text = JsonDocument.Parse(text).SettingMember(key, [.. at, .. under], raw);
        void Remove(string key, params string[] under) =>
            text = JsonDocument.Parse(text).RemovingMember(key, [.. at, .. under]);

        // Fields that describe the other transport go only when the transport
        // actually changes; a definition that already carries both keeps both.
        if (draft.Transport == Transport.Stdio && existingTransport != Transport.Stdio)
        {
            foreach (var key in schema.RemoteUrlKeys.Concat(typeKeys)) Remove(key);
        }
        else if (draft.Transport != Transport.Stdio && existingTransport == Transport.Stdio)
        {
            Remove("command");
            Remove("args");
        }

        if (draft.Transport == Transport.Stdio)
        {
            var command = draft.Command.Trim();
            if (existing.Command != command) Set("command", JsonText.String(command));

            var argsNode = Definition().Node["args"];
            if (draft.Args.Count == 0)
            {
                if (argsNode is not null && argsNode.Elements is not { Count: 0 }) Remove("args");
            }
            else if (argsNode?.Elements is null || !existing.Args.SequenceEqual(draft.Args, StringComparer.Ordinal))
            {
                Set("args", Render(JsonBuildValue.Strings(draft.Args)));
            }
        }
        else
        {
            if (existingTransport != draft.Transport)
            {
                // A `type` already in the file is kept truthful; a schema that leaves
                // Streamable HTTP unstated only ever writes the exception.
                var present = typeKeys.Where(key => initial.Node[key] is not null).ToArray();
                foreach (var key in present) Set(key, JsonText.String(draft.Transport.Raw()));
                if (present.Length == 0 && schema.TransportKey is { } stated &&
                    (schema.StatesDefaultTransport || draft.Transport != Transport.Http))
                {
                    Set(stated, JsonText.String(draft.Transport.Raw()));
                }
            }
            var url = draft.Url.Trim();
            if (existing.Url != url) Set(urlKey, JsonText.String(url));
        }

        return SplicingJsonEnvironment(draft, text, path, "env");
    }

    /// <summary>OpenCode's entry: <c>type</c>, one argv array in <c>command</c>, <c>environment</c>.</summary>
    /// <remarks>
    /// Same rules as <see cref="SplicingJson"/> — only what the draft describes is
    /// touched, and <c>enabled</c>, <c>timeout</c>, <c>headers</c>, <c>oauth</c> and
    /// comments stay put.
    /// </remarks>
    private static string SplicingOpenCode(ServerDraft draft, string source, IReadOnlyList<string> path)
    {
        var text = source;
        string[] at = [.. path];
        var document = JsonDocument.Parse(text);
        var node = document.Root.ValueAt(at)
            ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
        var members = node.Members
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnObject, path);
        foreach (var key in new[] { "type", "command", "url", "environment" })
        {
            JsonEditor.SoleMember(key, members, path);
        }
        var existing = ServerSchema.OpenCode.FieldsOf(node);
        var existingTransport = existing.ResolveTransport();
        var unit = IndentStyle.Detect(source).Text;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        void Set(string key, string raw) =>
            text = JsonDocument.Parse(text).SettingMember(key, at, raw);
        void Remove(string key) =>
            text = JsonDocument.Parse(text).RemovingMember(key, at);

        // OpenCode's local and remote entries accept different keys and refuse the
        // other's, so a change of transport takes the old one's with it. That
        // includes `headers` going local: the backup holds them.
        if ((draft.Transport == Transport.Stdio) != (existingTransport == Transport.Stdio))
        {
            foreach (var key in draft.Transport == Transport.Stdio
                         ? new[] { "url", "headers", "oauth" }
                         : new[] { "command", "cwd", "environment" })
            {
                Remove(key);
            }
        }
        var type = draft.Transport == Transport.Stdio ? "local" : "remote";
        if (node["type"]?.StringValue != type) Set("type", JsonText.String(type));

        if (draft.Transport == Transport.Stdio)
        {
            string[] argv = [draft.Command.Trim(), .. draft.Args];
            var current = node["command"]?.Elements;
            if (current is null || current.Count != argv.Length ||
                !current.Select(element => element.StringValue).SequenceEqual(argv, StringComparer.Ordinal))
            {
                var now = JsonDocument.Parse(text);
                var entry = now.Root.ValueAt(at)
                    ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
                var first = entry.Members?.FirstOrDefault()
                    ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
                var oneLine = !now.ContainsNewline(entry.Span.Start, entry.Span.End);
                var indent = now.LineIndent(first.Span.Start);
                var value = JsonBuildValue.Strings(argv);
                Set("command", oneLine
                    ? JsonBuilder.Render(value)
                    : JsonBuilder.Render(value, indent, unit, newline));
            }
        }
        else
        {
            var url = draft.Url.Trim();
            if (existing.Url != url) Set("url", JsonText.String(url));
            return text;
        }

        return SplicingJsonEnvironment(draft, text, path, "environment");
    }

    /// <summary>Environment, key by key, for the object at <paramref name="path"/>.</summary>
    private static string SplicingJsonEnvironment(
        ServerDraft draft,
        string source,
        IReadOnlyList<string> path,
        string envKey)
    {
        var text = source;
        var unit = IndentStyle.Detect(source).Text;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] at = [.. path];

        (JsonDocument Document, JsonNode Node, IReadOnlyList<JsonMember> Members) Definition()
        {
            var document = JsonDocument.Parse(text);
            var node = document.Root.ValueAt(at)
                ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
            var members = node.Members
                ?? throw new JsonEditException(JsonEditErrorKind.NotAnObject, path);
            return (document, node, members);
        }
        string Render(JsonBuildValue value)
        {
            var current = Definition();
            var oneLine = current.Members.Count > 0 &&
                !current.Document.ContainsNewline(current.Node.Span.Start, current.Node.Span.End);
            if (oneLine && value is JsonBuildValue.ObjectValue inline)
            {
                return JsonText.InlineObject(inline.Fields
                    .Select(field => (field.Key, JsonBuilder.Render(field.Value)))
                    .ToArray());
            }
            var indent = current.Members.Count > 0
                ? current.Document.LineIndent(current.Members[0].Span.Start)
                : current.Document.LineIndent(current.Node.Span.Start) + unit;
            return JsonBuilder.Render(value, oneLine ? "" : indent, unit, newline);
        }
        void Set(string key, string raw, params string[] under) =>
            text = JsonDocument.Parse(text).SettingMember(key, [.. at, .. under], raw);
        void Remove(string key, params string[] under) =>
            text = JsonDocument.Parse(text).RemovingMember(key, [.. at, .. under]);

        // Environment, key by key. A value of null is the UI saying "keep what is
        // there" (§6), which for a key already in this file means not touching it at
        // all — including values that are not strings.
        var wanted = draft.RealEnvironment;
        var fileValues = StringValues((Definition().Node[envKey]?.Members ?? [])
            .Select(member => (member.Key, member.Value.StringValue)));
        if (Definition().Node[envKey]?.Members is { } envMembers)
        {
            if (wanted.Count == 0)
            {
                if (envMembers.Count > 0) Remove(envKey);
            }
            else
            {
                var keys = wanted.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var member in envMembers.Where(member => !keys.Contains(member.Key)))
                {
                    Remove(member.Key, envKey);
                }
                foreach (var entry in wanted)
                {
                    var present = (Definition().Node[envKey]?.Members ?? [])
                        .Where(member => member.Key == entry.Key)
                        .ToArray();
                    if (present.Length > 1)
                    {
                        throw new JsonEditException(JsonEditErrorKind.DuplicateKey, [.. at, envKey, entry.Key]);
                    }
                    if (present.Length == 1)
                    {
                        if (entry.Value is null || present[0].Value.StringValue == entry.Value) continue;
                        Set(entry.Key, JsonText.String(entry.Value), envKey);
                    }
                    else
                    {
                        Set(entry.Key, JsonText.String(draft.ValueForNewKey(entry, fileValues)), envKey);
                    }
                }
            }
        }
        else if (wanted.Count > 0)
        {
            Set(envKey, Render(JsonBuildValue.Object(wanted
                .Select(entry => new JsonBuildValue.Field(entry.Key, JsonBuildValue.Text(draft.ValueForNewKey(entry))))
                .ToArray())));
        }

        return text;
    }

    /// <summary>The same, for a server's table in a TOML file (§6.3).</summary>
    /// <remarks>
    /// <c>startup_timeout_sec</c>, <c>enabled_tools</c>, <c>http_headers</c>,
    /// <c>bearer_token_env_var</c>, <c>enabled = false</c> and comments all stay
    /// exactly where they are. Only the keys a draft describes are spliced, each
    /// inside the table that holds it.
    /// </remarks>
    public static string SplicingToml(this ServerDraft draft, string source, string name, string key)
    {
        var text = source;
        string[] path = [key, name];
        string[] envPath = [key, name, "env"];

        var document = TomlDocument.Parse(text);
        if (document.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }
        var table = document.Table(path)
            ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, $"{key}.{name}");
        if (table.Pairs.FirstOrDefault(pair => pair.Key.Count > 1 && DescribedFields.Contains(pair.Key[0])) is { } dotted)
        {
            throw new TomlEditException(
                TomlEditErrorKind.UnsupportedLayout,
                string.Join(".", [.. path, .. dotted.Key]));
        }
        var envTable = document.Table(envPath);
        var existing = ServerFields.FromToml(table, envTable);
        var existingTransport = existing.ResolveTransport();

        void Set(string field, string raw, string[]? tablePath = null) =>
            text = TomlDocument.Parse(text).SettingValue(field, tablePath ?? path, raw);
        void Remove(string field, string[]? tablePath = null) =>
            text = TomlDocument.Parse(text).RemovingValue(field, tablePath ?? path);

        // Codex reads `url` as remote and `command` as stdio, so a transport change
        // is a change of key. A `type` is only rewritten if one is there.
        if (draft.Transport != existingTransport)
        {
            if (draft.Transport == Transport.Stdio)
            {
                Remove("url");
            }
            else
            {
                Remove("command");
                Remove("args");
            }
            if (table.Pair("type") is not null)
            {
                if (draft.Transport == Transport.Stdio) Remove("type");
                else Set("type", TomlText.String(draft.Transport.Raw()));
            }
        }

        if (draft.Transport == Transport.Stdio)
        {
            var command = draft.Command.Trim();
            if (existing.Command != command) Set("command", TomlText.String(command));
            var argsValue = TomlDocument.Parse(text).Table(path)?.Value("args");
            if (draft.Args.Count == 0)
            {
                if (argsValue is not null && argsValue.Elements is not { Count: 0 }) Remove("args");
            }
            else if (argsValue?.Elements is null || !existing.Args.SequenceEqual(draft.Args, StringComparer.Ordinal))
            {
                Set("args", TomlText.Array(draft.Args));
            }
        }
        else
        {
            var url = draft.Url.Trim();
            if (existing.Url != url) Set("url", TomlText.String(url));
        }

        var wanted = draft.RealEnvironment;
        var tableValues = StringValues((envTable?.Pairs ?? []).Select(pair => (pair.Name ?? "", pair.Value.StringValue)));
        if (envTable is not null)
        {
            if (wanted.Count == 0)
            {
                if (envTable.Pairs.Count > 0) text = TomlDocument.Parse(text).RemovingTable(envPath);
            }
            else
            {
                var keys = wanted.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var pair in envTable.Pairs)
                {
                    if (pair.Name is not { } envName || keys.Contains(envName)) continue;
                    Remove(envName, envPath);
                }
                foreach (var entry in wanted)
                {
                    var present = (TomlDocument.Parse(text).Table(envPath)?.Pairs ?? [])
                        .Where(pair => pair.Name == entry.Key)
                        .ToArray();
                    if (present.Length > 1)
                    {
                        throw new TomlEditException(
                            TomlEditErrorKind.AmbiguousValue, string.Join(".", [.. envPath, entry.Key]));
                    }
                    if (present.Length == 1)
                    {
                        if (entry.Value is null || present[0].Value.StringValue == entry.Value) continue;
                        Set(entry.Key, TomlText.String(entry.Value), envPath);
                    }
                    else
                    {
                        Set(entry.Key, TomlText.String(draft.ValueForNewKey(entry, tableValues)), envPath);
                    }
                }
            }
        }
        else if (table.Value("env")?.InlinePairs is not null)
        {
            // An inline `env = { … }` is one value. It is respelt only if something
            // in it changed, and a kept value keeps its own text.
            var current = TomlDocument.Parse(text);
            if (current.Table(path)?.Value("env") is not { InlinePairs: { } pairs } envValue) return text;
            if (wanted.Count == 0)
            {
                if (pairs.Count > 0) Remove("env");
            }
            else
            {
                // Changed means a different set of keys or a value that decodes
                // differently — never a different spelling of the same value (`'v'`
                // against `"v"`, spacing), which would rewrite on a no-op save.
                var sameKeys = wanted.Select(entry => entry.Key)
                    .SequenceEqual(pairs.Select(pair => pair.Name ?? ""), StringComparer.Ordinal);
                var sameValues = wanted.All(entry =>
                    entry.Value is null ||
                    pairs.LastOrDefault(pair => pair.Name == entry.Key)?.Value.StringValue == entry.Value);
                if (!(sameKeys && sameValues))
                {
                    var inlineValues = StringValues(pairs.Select(pair => (pair.Name ?? "", pair.Value.StringValue)));
                    var spelled = wanted.Select(entry =>
                    {
                        var kept = pairs.LastOrDefault(pair => pair.Name == entry.Key);
                        var raw = kept is not null && (entry.Value is null || kept.Value.StringValue == entry.Value)
                            ? current.Slice(kept.Value.Span)
                            : TomlText.String(draft.ValueForNewKey(entry, inlineValues));
                        return $"{TomlText.Key(entry.Key)} = {raw}";
                    });
                    text = current.Replacing(envValue.Span, "{ " + string.Join(", ", spelled) + " }");
                }
            }
        }
        else if (wanted.Count > 0)
        {
            // Where `codex mcp add` puts it: a sub-table right after the server's own.
            var current = TomlDocument.Parse(text);
            var anchor = current.EndOfTableContent(path)
                ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, $"{key}.{name}");
            var newline = current.Newline();
            var lines = new[] { "", $"[{TomlText.Path(envPath)}]" }
                .Concat(wanted.Select(entry => $"{TomlText.Key(entry.Key)} = {TomlText.String(draft.ValueForNewKey(entry))}"));
            text = current.Replacing(new TomlSpan(anchor, anchor), newline + string.Join(newline, lines));
        }

        return text;
    }

    /// <summary>The string values a file holds, last spelling winning, for a key a draft keeps or renames.</summary>
    private static Dictionary<string, string> StringValues(IEnumerable<(string Key, string? Value)> members)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in members)
        {
            if (value is not null) values[key] = value;
        }
        return values;
    }
}
