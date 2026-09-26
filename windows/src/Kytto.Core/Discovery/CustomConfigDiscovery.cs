using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;
using Kytto.Core.Toml;

namespace Kytto.Core;

internal sealed record CustomConfigDiscoveryResult(
    bool SourceExists,
    IReadOnlyList<Server> Servers,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Auto-detects one explicitly selected, read-only configuration file.</summary>
internal static class CustomConfigDiscovery
{
    private static readonly string[] JsonMapKeys = ["mcpServers", "servers"];
    private static readonly string[] TomlMapKeys = ["mcp_servers", "mcpServers", "servers"];

    public static CustomConfigDiscoveryResult Read(
        CustomConfigSource source,
        Func<string, string?> readTracked)
    {
        if (readTracked(source.Path) is not { } text)
        {
            return File.Exists(source.Path)
                ? new CustomConfigDiscoveryResult(
                    true,
                    [],
                    [Problem(
                        source,
                        DiagnosticSeverity.Error,
                        "Could not read this custom source: it is not valid UTF-8 or access was denied.")])
                : new CustomConfigDiscoveryResult(false, [], []);
        }

        try
        {
            return ReadJson(source, JsonDocument.Parse(text));
        }
        catch (JsonParseException jsonError)
        {
            try
            {
                return ReadToml(source, TomlDocument.Parse(text));
            }
            catch (TomlParseException tomlError)
            {
                return new CustomConfigDiscoveryResult(
                    true,
                    [],
                    [Problem(
                        source,
                        DiagnosticSeverity.Error,
                        "Could not read this custom source as JSON/JSONC or TOML: " +
                        $"JSON/JSONC {jsonError.Message}; TOML {tomlError.Message}.")]);
            }
        }
    }

    private static CustomConfigDiscoveryResult ReadJson(
        CustomConfigSource source,
        JsonDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        var mapMembers = document.Root.Members?
            .Where(member => JsonMapKeys.Contains(member.Key, StringComparer.Ordinal))
            .ToArray() ?? [];
        var maps = mapMembers
            .Where(member => member.Value.Kind == JsonKind.Object)
            .ToArray();

        if (mapMembers.Length == 0 || maps.Length == 0)
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                "No supported top-level server map was found. Expected exactly one object named " +
                "\"mcpServers\" or \"servers\"."));
            return new CustomConfigDiscoveryResult(true, [], diagnostics);
        }
        if (mapMembers.Length > 1 || maps.Length > 1)
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                "More than one supported top-level server map was found, so the source is ambiguous. " +
                "Expected exactly one of \"mcpServers\" or \"servers\"."));
            return new CustomConfigDiscoveryResult(true, [], diagnostics);
        }

        var map = maps[0];
        var members = map.Value.Members ?? [];
        var ambiguous = AmbiguousNames(members.Select(member => member.Key));
        AddAmbiguousDiagnostics(source, members.Select(member => member.Key), ambiguous, diagnostics);

        var servers = new List<Server>();
        foreach (var member in members)
        {
            if (ambiguous.Contains(Server.Identity(member.Key))) continue;
            if (JsonShapeProblem(member.Value) is { } shapeProblem)
            {
                diagnostics.Add(Problem(
                    source,
                    DiagnosticSeverity.Warning,
                    $"Skipped \"{member.Key}\": {shapeProblem}."));
                continue;
            }

            servers.Add(ServerFields.FromJson(member.Value).MakeServer(
                member.Key,
                new Origin.ConfigFile(source.ClientID),
                isBundled: false,
                sourceText: document.Slice(member.Value.Span),
                isReadOnly: true));
        }

        return new CustomConfigDiscoveryResult(true, servers, diagnostics);
    }

    private static CustomConfigDiscoveryResult ReadToml(
        CustomConfigSource source,
        TomlDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        var root = document.Table();
        var present = TomlMapKeys.Where(key => HasTopLevelTomlKey(document, root, key))
            .ToArray();
        var maps = present.Where(key => HasTomlMap(document, root, key)).ToArray();

        if (present.Length == 0)
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                "No supported top-level server map was found. Expected exactly one table/map named " +
                "\"mcp_servers\", \"mcpServers\" or \"servers\"."));
            return new CustomConfigDiscoveryResult(true, [], diagnostics);
        }
        if (maps.Length == 0)
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                "No supported top-level server map was found. Expected exactly one table/map named " +
                "\"mcp_servers\", \"mcpServers\" or \"servers\"."));
            return new CustomConfigDiscoveryResult(true, [], diagnostics);
        }
        if (present.Length > 1 || maps.Length > 1 || HasDuplicateTomlMap(document, root, maps[0]))
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                "More than one supported top-level server map was found, so the source is ambiguous. " +
                "Expected exactly one of \"mcp_servers\", \"mcpServers\" or \"servers\"."));
            return new CustomConfigDiscoveryResult(true, [], diagnostics);
        }

        var key = maps[0];
        var (entries, invalidEntries) = TomlEntries(document, root, key);
        foreach (var invalidEntry in invalidEntries)
        {
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                $"Skipped \"{invalidEntry}\": expected a table/object."));
        }
        var ambiguous = AmbiguousNames(entries.Select(entry => entry.Name));
        AddAmbiguousDiagnostics(source, entries.Select(entry => entry.Name), ambiguous, diagnostics);

        var servers = new List<Server>();
        foreach (var entry in entries)
        {
            if (ambiguous.Contains(Server.Identity(entry.Name))) continue;
            if (TomlShapeProblem(entry.Pairs, entry.EnvironmentPairs) is { } shapeProblem)
            {
                diagnostics.Add(Problem(
                    source,
                    DiagnosticSeverity.Warning,
                    $"Skipped \"{entry.Name}\": {shapeProblem}."));
                continue;
            }

            var fields = entry.Table is { } table
                ? ServerFields.FromToml(table, entry.EnvironmentTable)
                : ServerFields.FromTomlInline(entry.Pairs);
            servers.Add(fields.MakeServer(
                entry.Name,
                new Origin.ConfigFile(source.ClientID),
                isBundled: false,
                sourceText: entry.SourceText,
                isReadOnly: true));
        }

        return new CustomConfigDiscoveryResult(true, servers, diagnostics);
    }

    private sealed record TomlEntry(
        string Name,
        IReadOnlyList<TomlPair> Pairs,
        TomlTable? Table,
        TomlTable? EnvironmentTable,
        IReadOnlyList<TomlPair> EnvironmentPairs,
        string SourceText);

    private static (IReadOnlyList<TomlEntry> Entries, IReadOnlyList<string> InvalidEntries) TomlEntries(
        TomlDocument document,
        TomlTable? root,
        string key)
    {
        var entries = new List<TomlEntry>();
        var invalidEntries = new List<string>();

        // Keep every spelling, including duplicates. Collapsing names here would
        // make two definitions with the same normalized identity look safe even
        // though Kytto cannot know which one the client will use.
        foreach (var table in document.ChildTables(key).Where(table => !table.IsArrayElement))
        {
            var name = table.Path[^1];
            var environment = document.Table(key, name, "env");
            entries.Add(new TomlEntry(
                name,
                table.Pairs,
                table,
                environment,
                environment?.Pairs ?? [],
                document.ServerDefinitionText(name, key) ?? document.Slice(table.Span)));
        }

        foreach (var pair in document.Table(key)?.Pairs ?? [])
        {
            if (pair.Name is not { } name) continue;
            if (pair.Value.InlinePairs is not { } inline)
            {
                invalidEntries.Add(name);
                continue;
            }
            var inlineEnvironment = inline
                .FirstOrDefault(candidate => candidate.Name == "env")?
                .Value.InlinePairs ?? [];
            entries.Add(new TomlEntry(
                name,
                inline,
                null,
                null,
                inlineEnvironment,
                document.Slice(pair.Span)));
        }

        // Also accept a compact root assignment:
        // mcp_servers = { demo = { command = "tool" } }
        foreach (var pair in root?.Pairs.Where(pair => pair.Name == key) ?? [])
        {
            foreach (var serverPair in pair.Value.InlinePairs ?? [])
            {
                if (serverPair.Name is not { } name) continue;
                if (serverPair.Value.InlinePairs is not { } inline)
                {
                    invalidEntries.Add(name);
                    continue;
                }
                var environment = inline
                    .FirstOrDefault(candidate => candidate.Name == "env")?
                    .Value.InlinePairs ?? [];
                entries.Add(new TomlEntry(
                    name,
                    inline,
                    null,
                    null,
                    environment,
                    document.Slice(serverPair.Span)));
            }
        }
        return (entries, invalidEntries);
    }

    private static bool HasTopLevelTomlKey(
        TomlDocument document,
        TomlTable? root,
        string key) =>
        document.Tables.Any(table => table.Path.Count > 0 && table.Path[0] == key) ||
        root?.Pairs.Any(pair => pair.Name == key) == true;

    private static bool HasTomlMap(
        TomlDocument document,
        TomlTable? root,
        string key) =>
        document.Tables.Any(table => table.Path.Count > 0 && table.Path[0] == key) ||
        root?.Pairs.Any(pair => pair.Name == key && pair.Value.Kind == TomlKind.InlineTable) == true;

    private static bool HasDuplicateTomlMap(
        TomlDocument document,
        TomlTable? root,
        string key)
    {
        var explicitMapTables = document.Tables.Count(table =>
            table.Path.Count == 1 && table.Path[0] == key);
        var rootAssignments = root?.Pairs.Count(pair => pair.Name == key) ?? 0;
        return explicitMapTables > 1
            || rootAssignments > 1
            || rootAssignments > 0 && document.Tables.Any(table =>
                table.Path.Count > 0 && table.Path[0] == key);
    }

    private static string? JsonShapeProblem(JsonNode node)
    {
        if (node.Kind != JsonKind.Object) return "expected an object";
        if (node.Member("command") is { Value.Kind: not JsonKind.String })
        {
            return "\"command\" must be a string";
        }
        if (node.Member("url") is { Value.Kind: not JsonKind.String })
        {
            return "\"url\" must be a string";
        }
        if (node.Member("type") is { Value.Kind: not JsonKind.String })
        {
            return "\"type\" must be a string";
        }
        if (node.Member("args") is { } args
            && (args.Value.Kind != JsonKind.Array
                || args.Value.Elements!.Any(value => value.Kind != JsonKind.String)))
        {
            return "\"args\" must be an array of strings";
        }
        if (node.Member("env") is { } env
            && (env.Value.Kind != JsonKind.Object
                || env.Value.Members!.Any(member => member.Value.Kind != JsonKind.String)))
        {
            return "\"env\" must be an object containing string values";
        }
        if (node["command"]?.StringValue is null && node["url"]?.StringValue is null)
        {
            return "expected a string \"command\" or \"url\"";
        }
        return null;
    }

    private static string? TomlShapeProblem(
        IReadOnlyList<TomlPair> pairs,
        IReadOnlyList<TomlPair> environmentPairs)
    {
        var command = pairs.FirstOrDefault(pair => pair.Name == "command");
        var url = pairs.FirstOrDefault(pair => pair.Name == "url");
        var type = pairs.FirstOrDefault(pair => pair.Name == "type");
        var args = pairs.FirstOrDefault(pair => pair.Name == "args");
        var env = pairs.FirstOrDefault(pair => pair.Name == "env");

        if (command is { Value.Kind: not TomlKind.String }) return "\"command\" must be a string";
        if (url is { Value.Kind: not TomlKind.String }) return "\"url\" must be a string";
        if (type is { Value.Kind: not TomlKind.String }) return "\"type\" must be a string";
        if (args is not null && (args.Value.Kind != TomlKind.Array
            || args.Value.Elements!.Any(value => value.Kind != TomlKind.String)))
        {
            return "\"args\" must be an array of strings";
        }
        if (env is not null && env.Value.Kind != TomlKind.InlineTable)
        {
            return "\"env\" must be an inline table or a child table";
        }
        if (environmentPairs.Any(pair => pair.Value.Kind != TomlKind.String))
        {
            return "\"env\" must contain string values";
        }
        if (command?.Value.StringValue is null && url?.Value.StringValue is null)
        {
            return "expected a string \"command\" or \"url\"";
        }
        return null;
    }

    private static HashSet<string> AmbiguousNames(IEnumerable<string> names) =>
        names.GroupBy(Server.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static void AddAmbiguousDiagnostics(
        CustomConfigSource source,
        IEnumerable<string> names,
        IReadOnlySet<string> ambiguous,
        ICollection<Diagnostic> diagnostics)
    {
        var allNames = names.ToArray();
        foreach (var identity in ambiguous)
        {
            var spellings = allNames
                .Where(name => Server.Identity(name) == identity)
                .Select(name => $"\"{name}\"");
            diagnostics.Add(Problem(
                source,
                DiagnosticSeverity.Warning,
                $"Skipped ambiguous duplicate server names: {string.Join(", ", spellings)}."));
        }
    }

    private static Diagnostic Problem(
        CustomConfigSource source,
        DiagnosticSeverity severity,
        string message) => new(
            severity,
            source.ClientID,
            source.Path,
            $"{message} Kytto did not change the file.");
}
