namespace Kytto.Core.Json;

/// <summary>
/// A byte range in the source text, as UTF-8 offsets.
/// </summary>
/// <remarks>
/// Offsets rather than string indices because the write pipeline splices edits
/// back into the original bytes and needs ranges that survive being stored and
/// passed around.
/// </remarks>
public readonly record struct JsonSpan(int Start, int End)
{
    public int Length => End - Start;
    public bool IsEmpty => Start >= End;
}

/// <summary>One <c>"key": value</c> pair inside an object.</summary>
/// <param name="Key">Decoded key, with escapes resolved.</param>
/// <param name="KeySpan">Range of the quoted key literal, quotes included.</param>
/// <param name="Span">Key through value. This is what removing a member deletes.</param>
public sealed record JsonMember(string Key, JsonSpan KeySpan, JsonNode Value, JsonSpan Span);

public enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    Bool,
    Null,
}

/// <summary>
/// A parsed JSON value that remembers where it came from.
/// </summary>
/// <remarks>
/// Every node carries the range it occupies in the source, which is what lets the
/// writer rewrite one server entry and leave every other byte of the file —
/// including comments and whatever formatting the user or the client app chose —
/// untouched (§6.3).
/// </remarks>
public sealed class JsonNode
{
    private readonly object? _payload;

    private JsonNode(JsonKind kind, JsonSpan span, object? payload)
    {
        Kind = kind;
        Span = span;
        _payload = payload;
    }

    public JsonKind Kind { get; }
    public JsonSpan Span { get; }

    internal static JsonNode Object(IReadOnlyList<JsonMember> members, JsonSpan span) =>
        new(JsonKind.Object, span, members);

    internal static JsonNode Array(IReadOnlyList<JsonNode> elements, JsonSpan span) =>
        new(JsonKind.Array, span, elements);

    internal static JsonNode String(string value, JsonSpan span) =>
        new(JsonKind.String, span, value);

    internal static JsonNode Number(double value, JsonSpan span) =>
        new(JsonKind.Number, span, value);

    internal static JsonNode Bool(bool value, JsonSpan span) =>
        new(JsonKind.Bool, span, value);

    internal static JsonNode Null(JsonSpan span) => new(JsonKind.Null, span, null);

    // MARK: - Reading

    public IReadOnlyList<JsonMember>? Members =>
        Kind == JsonKind.Object ? (IReadOnlyList<JsonMember>)_payload! : null;

    public IReadOnlyList<JsonNode>? Elements =>
        Kind == JsonKind.Array ? (IReadOnlyList<JsonNode>)_payload! : null;

    public string? StringValue => Kind == JsonKind.String ? (string)_payload! : null;

    public double? NumberValue => Kind == JsonKind.Number ? (double)_payload! : null;

    /// <summary>
    /// The number as an <c>int</c>, or null when it is not finite, has a fraction or
    /// does not fit.
    /// </summary>
    /// <remarks>
    /// A plain <c>(int)</c> cast never throws here, it saturates: <c>1e100</c> read as
    /// <c>int.MaxValue</c> and <c>-32601.5</c> as <c>-32601</c>, "method not found".
    /// A server's error code that is not an integer is a protocol failure, not a
    /// number to round (AUD-01).
    /// </remarks>
    public int? IntegerValue =>
        NumberValue is { } value && double.IsFinite(value) && Math.Floor(value) == value &&
        value >= int.MinValue && value <= int.MaxValue
            ? (int)value
            : null;

    public bool? BoolValue => Kind == JsonKind.Bool ? (bool)_payload! : null;

    public bool IsNull => Kind == JsonKind.Null;

    /// <summary>First member with this key.</summary>
    /// <remarks>
    /// Duplicate keys are legal JSON and clients resolve them last-wins, so
    /// <c>Last</c> would be more faithful — but a config with duplicate server
    /// names is broken input worth surfacing, not silently resolving. Discovery
    /// reports it as a diagnostic.
    /// </remarks>
    public JsonMember? Member(string key)
    {
        if (Members is not { } members) return null;
        foreach (var member in members)
        {
            if (string.Equals(member.Key, key, StringComparison.Ordinal)) return member;
        }
        return null;
    }

    public JsonNode? this[string key] => Member(key)?.Value;

    /// <summary>
    /// Last member with this key — how the clients' own JSON parsers resolve a
    /// duplicate.
    /// </summary>
    /// <remarks>
    /// Used where Kytto must read what the <em>model</em> will be shown (a tool's
    /// name, description, schema), so a second <c>"description"</c> cannot hide
    /// behind a harmless first one. Config parsing keeps <see cref="Member"/>, which
    /// surfaces duplicates instead of resolving them.
    /// </remarks>
    public JsonNode? LastValue(string key)
    {
        if (Members is not { } members) return null;
        for (var index = members.Count - 1; index >= 0; index--)
        {
            if (string.Equals(members[index].Key, key, StringComparison.Ordinal)) return members[index].Value;
        }
        return null;
    }

    /// <summary>Walks a chain of object keys, e.g. <c>["mcpServers", "github"]</c>.</summary>
    public JsonNode? ValueAt(params string[] path)
    {
        var node = this;
        foreach (var key in path)
        {
            if (node[key] is not { } next) return null;
            node = next;
        }
        return node;
    }

    /// <summary>
    /// Object keys in source order, so what the user is shown matches the file.
    /// </summary>
    public IReadOnlyList<string> Keys =>
        Members?.Select(member => member.Key).ToArray() ?? [];
}
