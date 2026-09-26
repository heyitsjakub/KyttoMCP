namespace Kytto.Core.Toml;

/// <summary>
/// The read-only half of Codex's server map: which servers a file declares, and
/// the verbatim text of one.
/// </summary>
/// <remarks>
/// Separate from the editor because discovery needs exactly this much and nothing
/// that writes — §6.6 keeps a launch read-only, and a reader that cannot write is
/// a cheaper thing to guarantee than one that merely does not.
/// </remarks>
public static class TomlServerReading
{
    /// <summary>Server names under a key, in file order.</summary>
    /// <remarks>
    /// Both spellings count: a <c>[mcp_servers.name]</c> table, and an inline
    /// <c>name = { … }</c> pair on the <c>[mcp_servers]</c> table itself. A config
    /// edited by hand may hold either, and a row missing from the matrix because
    /// of how it was written down is worse than one Kytto declines to edit.
    /// </remarks>
    public static IReadOnlyList<string> ServerNames(this TomlDocument document, string key)
    {
        var names = new List<string>();

        foreach (var table in document.ChildTables(key))
        {
            // `[[a.b]]` is an array of tables, which Kytto does not author and
            // must not mistake for a server it may rewrite.
            if (table.IsArrayElement) continue;
            if (table.Path.Count > 0) names.Add(table.Path[^1]);
        }

        foreach (var pair in document.Table(key)?.Pairs ?? [])
        {
            if (pair.Name is { } name && pair.Value.InlinePairs is not null && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Every table belonging to one server: its own, plus sub-tables such as
    /// <c>env</c>, which are one thing to the user and two tables to TOML.
    /// </summary>
    public static IReadOnlyList<TomlTable> ServerTables(
        this TomlDocument document,
        string name,
        string key)
    {
        string[] path = [key, name];
        var own = document.Table(path);
        if (own is null) return [];
        return new[] { own }.Concat(document.TablesUnder(path)).ToArray();
    }

    /// <summary>
    /// The server's definition exactly as it appears in the file — what gets
    /// parked, and what gets copied verbatim into another TOML client.
    /// </summary>
    public static string? ServerDefinitionText(this TomlDocument document, string name, string key)
    {
        var tables = document.ServerTables(name, key);
        if (tables.Count == 0)
        {
            // An inline definition has no table of its own; park the pair.
            var pair = document.Table(key)?.Pair(name);
            return pair is null ? null : document.Slice(pair.Span);
        }
        return document.Slice(new TomlSpan(tables[0].Span.Start, tables[^1].Span.End));
    }
}
