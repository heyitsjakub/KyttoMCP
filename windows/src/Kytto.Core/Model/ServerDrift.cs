using Kytto.Core.Clients;

namespace Kytto.Core.Model;

/// <summary>What two clients disagree about.</summary>
public enum DriftField
{
    Transport,
    Command,
    Arguments,
    Url,
    EnvironmentKeys,
    EnvironmentValues,
}

public static class DriftFields
{
    /// <summary>The spelling <c>docs/ipc.md</c> gives this on the wire.</summary>
    public static string Raw(this DriftField field) => field switch
    {
        DriftField.Transport => "transport",
        DriftField.Command => "command",
        DriftField.Arguments => "arguments",
        DriftField.Url => "url",
        DriftField.EnvironmentKeys => "environmentKeys",
        _ => "environmentValues",
    };
}

/// <summary>One distinct definition, and every client holding exactly that one.</summary>
/// <param name="ClientIDs">Sorted, so a variant reads the same on every run.</param>
public sealed record DriftVariant(IReadOnlyList<ClientKey> ClientIDs, ClientDefinition Definition)
{
    /// <summary>Whether this copy may be written into the other clients.</summary>
    /// <remarks>
    /// An extension bundle or a plugin is installed software: its command belongs to
    /// whatever installed it, and §4 refuses to let such a definition travel. It can
    /// still be shown and compared — knowing that Claude Desktop's copy of a name is
    /// a bundle and Cursor's is an <c>npx</c> line is exactly the confusion worth
    /// surfacing — but it is not a thing to copy out of.
    /// </remarks>
    public bool CanBeSource => !Definition.IsBundled;
}

/// <summary>One server's copies, compared (§7.12).</summary>
/// <remarks>
/// <c>Discovery</c> used to notice a mismatch, raise a diagnostic reading
/// "configured differently … the command shown is the first one found", and discard
/// the evidence. That sentence names a problem and then withholds everything needed
/// to act on it: which client, differing how, and what to do about it. This is the
/// answer to all three.
/// </remarks>
/// <param name="Fields">
/// What differs, across all copies. Empty is impossible: a report only exists when
/// something does.
/// </param>
/// <param name="Variants">
/// Distinct definitions, largest group first, so the copy most clients agree on is
/// the one offered first.
/// </param>
public sealed record ServerDrift(
    string ServerID,
    string ServerName,
    IReadOnlyList<DriftField> Fields,
    IReadOnlyList<DriftVariant> Variants)
{
    // Control characters as separators, so a value containing the separator cannot
    // forge another definition's key — the same reasoning as `Server.MakeFingerprint`.
    private const char Unit = '\u0001';
    private const char Record = '\u0002';

    /// <summary>Clients whose copy cannot be rewritten, whichever variant is chosen.</summary>
    public IReadOnlyList<ClientKey> UnwritableClientIDs =>
        Variants
            .Where(variant => variant.Definition.IsBundled || variant.Definition.IsReadOnly)
            .SelectMany(variant => variant.ClientIDs)
            .Order(ClientKeyComparer.Instance)
            .ToArray();

    /// <summary>Compares every copy of <paramref name="server"/>.</summary>
    /// <returns>
    /// Null when the clients agree, or when only one of them has it — a single copy
    /// cannot disagree with anything.
    /// </returns>
    public static ServerDrift? Detect(Server server)
    {
        var definitions = server.DefinitionsByClient;
        if (definitions.Count <= 1) return null;

        var fields = DifferingFields(definitions.Values);
        if (fields.Count == 0) return null;

        // Group by the whole comparable shape rather than by `Fingerprint`, which
        // covers only command, args and url. Two clients running the same command
        // with different environments have not got the same definition.
        var groups = new Dictionary<string, List<ClientKey>>(StringComparer.Ordinal);
        var representative = new Dictionary<string, ClientDefinition>(StringComparer.Ordinal);
        foreach (var (clientId, definition) in definitions)
        {
            var key = ShapeKey(definition);
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
            members.Add(clientId);
            representative.TryAdd(key, definition);
        }

        var variants = groups
            .Select(group => new DriftVariant(
                group.Value.Order(ClientKeyComparer.Instance).ToArray(),
                representative[group.Key]))
            // Largest group first; ties broken by client id so the order is stable
            // across runs rather than following dictionary iteration.
            .OrderByDescending(variant => variant.ClientIDs.Count)
            .ThenBy(
                variant => variant.ClientIDs.Count > 0 ? variant.ClientIDs[0].Raw() : "",
                StringComparer.Ordinal)
            .ToArray();

        return new ServerDrift(server.Id, server.Name, fields, variants);
    }

    /// <summary>Every server that has more than one copy and whose copies disagree.</summary>
    public static IReadOnlyList<ServerDrift> Detect(IEnumerable<Server> servers) =>
        servers.Select(Detect).OfType<ServerDrift>().ToArray();

    // MARK: - Comparison

    private static IReadOnlyList<DriftField> DifferingFields(
        IReadOnlyCollection<ClientDefinition> definitions)
    {
        var fields = new List<DriftField>();

        if (DistinctCount(definitions, definition => definition.Transport.Raw()) > 1)
        {
            fields.Add(DriftField.Transport);
        }
        if (DistinctCount(definitions, definition => definition.Command ?? "") > 1)
        {
            fields.Add(DriftField.Command);
        }
        if (DistinctCount(definitions, definition => string.Join(Unit, definition.Args)) > 1)
        {
            fields.Add(DriftField.Arguments);
        }
        if (DistinctCount(definitions, definition => definition.Url ?? "") > 1)
        {
            fields.Add(DriftField.Url);
        }
        if (DistinctCount(
                definitions,
                definition => string.Join(Unit, definition.EnvironmentKeys)) > 1)
        {
            fields.Add(DriftField.EnvironmentKeys);
        }
        if (HasEnvironmentValueDrift(definitions)) fields.Add(DriftField.EnvironmentValues);

        fields.Sort((left, right) => string.CompareOrdinal(left.Raw(), right.Raw()));
        return fields;
    }

    private static int DistinctCount(
        IEnumerable<ClientDefinition> definitions,
        Func<ClientDefinition, string> project) =>
        definitions.Select(project).Distinct(StringComparer.Ordinal).Count();

    /// <summary>A key two clients both set, to two different things.</summary>
    /// <remarks>
    /// Only keys present in more than one copy are compared: a key one client simply
    /// does not have is a difference in keys, already reported as one. The values are
    /// read here and never leave — the finding is that they differ, never what they
    /// are (§6).
    /// </remarks>
    private static bool HasEnvironmentValueDrift(IEnumerable<ClientDefinition> definitions)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            foreach (var entry in definition.Env)
            {
                var value = entry.Value ?? "";
                if (seen.TryGetValue(entry.Key, out var previous) && previous != value) return true;
                seen[entry.Key] = value;
            }
        }
        return false;
    }

    /// <summary>Everything that makes one copy distinct from another, values included.</summary>
    /// <remarks>
    /// Stays inside this type. It is a grouping key, not a fact about the server, and
    /// it is built partly from environment values — so it must never be put in a DTO,
    /// a log line or a support report.
    /// </remarks>
    private static string ShapeKey(ClientDefinition definition)
    {
        var parts = new List<string>
        {
            definition.Transport.Raw(),
            definition.Command ?? "",
            string.Join(Unit, definition.Args),
            definition.Url ?? "",
            definition.IsBundled ? "bundled" : "config",
            definition.IsReadOnly ? "readOnly" : "writable",
        };
        foreach (var entry in definition.Env.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            parts.Add($"{entry.Key}={entry.Value ?? ""}");
        }
        return string.Join(Record, parts);
    }
}

/// <summary>Orders client ids by their wire spelling, so listings are reproducible.</summary>
public sealed class ClientIdComparer : IComparer<ClientId>
{
    public static readonly ClientIdComparer Instance = new();

    public int Compare(ClientId left, ClientId right) =>
        string.CompareOrdinal(left.Raw(), right.Raw());
}

/// <summary>Orders discovery ids, including custom sources, by wire spelling.</summary>
public sealed class ClientKeyComparer : IComparer<ClientKey>
{
    public static readonly ClientKeyComparer Instance = new();

    public int Compare(ClientKey left, ClientKey right) =>
        string.CompareOrdinal(left.Raw(), right.Raw());
}
