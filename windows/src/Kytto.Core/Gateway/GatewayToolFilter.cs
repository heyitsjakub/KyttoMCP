using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kytto.Core.Gateway;

/// <summary>
/// Splits a byte stream into the newline-delimited messages MCP stdio uses.
/// </summary>
/// <remarks>
/// Kept separate from the filter so the framing can be tested against chunk
/// boundaries — a <c>tools/list</c> response arriving in three reads is the normal
/// case, not the edge case.
/// </remarks>
public sealed class GatewayLineFramer
{
    private readonly List<byte> _buffer = [];

    /// <summary>Calls <paramref name="body"/> once per complete line, without the newline.</summary>
    /// <remarks>
    /// The line leaves the buffer before <paramref name="body"/> sees it. If the body
    /// throws, what is still buffered is only what came after — never the line that
    /// failed, which a later <see cref="Flush"/> would otherwise hand over a second
    /// time glued to everything behind it (AUD-07).
    /// </remarks>
    public void Consume(ReadOnlySpan<byte> data, Action<byte[]> body)
    {
        _buffer.AddRange(data);
        while (true)
        {
            var newline = _buffer.IndexOf((byte)'\n');
            if (newline < 0) break;
            byte[] line = [.. _buffer.Take(newline)];
            _buffer.RemoveRange(0, newline + 1);
            body(line);
        }
    }

    /// <summary>Whatever arrived without a closing newline.</summary>
    /// <remarks>
    /// A server that exits mid-message still gets its bytes forwarded rather than
    /// swallowed.
    /// </remarks>
    public void Flush(Action<byte[]> body)
    {
        if (_buffer.Count == 0) return;
        byte[] tail = [.. _buffer];
        _buffer.Clear();
        body(tail);
    }
}

/// <summary>
/// A strict reading of one message: the only reading the filter acts on, and the
/// proof that it is the only reading there is (AUD-07).
/// </summary>
/// <remarks>
/// <para>
/// The filter decides and then forwards the original bytes, which the server reads
/// with its own parser. That is only safe while both parsers must agree, and a
/// repeated key is where they do not: <c>JsonDocument</c> keeps both,
/// <c>JsonNode.Parse</c> throws, Python and JavaScript keep the last one and
/// Foundation keeps the first. <c>{"name":"write_file","name":"read_file"}</c> read
/// last-wins here is an allowed call, and a first-wins server runs it as the hidden
/// tool. Keys are compared after escapes are resolved, so <c>"name"</c> repeats
/// <c>"name"</c>; refusing a message no conforming client sends is the cheap side of
/// that trade.
/// </para>
/// <para>
/// Text that cannot be decoded — an unpaired surrogate escape, invalid UTF-8 — is
/// read with U+FFFD in its place instead of throwing. <c>JsonElement.GetString</c>
/// throws <c>InvalidOperationException</c> on <c>"\ud800"</c>, and that throw used to
/// escape the relay's line callback: the helper then flushed everything still
/// buffered as one unreadable "line" and forwarded it, so a hidden tool reached the
/// server behind an id of <c>"\ud800"</c>.
/// </para>
/// </remarks>
internal static class GatewayStrictJson
{
    /// <param name="Root">The message, read last-wins, every string decoded.</param>
    /// <param name="HasDuplicateKeys">Whether any object, at any depth, repeats a key.</param>
    /// <param name="RawId">
    /// The id exactly as the sender wrote it, so a refusal can still be matched to its
    /// request — but only when there is one unambiguous string or number to echo.
    /// </param>
    internal sealed record Reading(JsonNode? Root, bool HasDuplicateKeys, string? RawId);

    /// <summary>The reading, or null when the bytes are not exactly one JSON value.</summary>
    /// <remarks>
    /// The default options are the strict ones: no comments, no trailing commas, no
    /// <c>NaN</c>, a bounded depth. A message one of those lets through is one a
    /// stricter server could read differently or not at all.
    /// </remarks>
    public static Reading? Read(ReadOnlySpan<byte> line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var duplicates = false;
            var root = Build(document.RootElement, ref duplicates);
            return new Reading(root, duplicates, RawId(document.RootElement));
        }
    }

    public static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var value in line)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return false;
        }
        return true;
    }

    private static JsonNode? Build(JsonElement element, ref bool duplicates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var json = new JsonObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    // Never property.Name: it throws on an unpaired surrogate.
                    var name = Decode(JsonMarshal.GetRawUtf8PropertyName(property));
                    if (!seen.Add(name)) duplicates = true;
                    json[name] = Build(property.Value, ref duplicates);
                }
                return json;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray()) array.Add(Build(item, ref duplicates));
                return array;
            case JsonValueKind.String:
                var raw = JsonMarshal.GetRawUtf8Value(element);
                return JsonValue.Create(Decode(raw[1..^1]));
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                // Numbers keep their exact text; cloned because the document this
                // element belongs to is disposed on return.
                return JsonValue.Create(element.Clone());
        }
    }

    private static string? RawId(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        JsonElement? id = null;
        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (Decode(JsonMarshal.GetRawUtf8PropertyName(property)) != "id") continue;
            count++;
            id = property.Value;
        }
        if (count != 1 || id is not { } value) return null;
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8Value(value))
            : null;
    }

    /// <summary>
    /// A JSON string's raw text, between its quotes, decoded — with U+FFFD for an
    /// unpaired surrogate or a byte that is not UTF-8.
    /// </summary>
    /// <remarks>The reader has already validated every escape.</remarks>
    internal static string Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.IndexOf((byte)'\\') < 0) return Encoding.UTF8.GetString(raw);

        var text = new StringBuilder(raw.Length);
        var start = 0;
        var index = 0;
        while (index < raw.Length)
        {
            if (raw[index] != (byte)'\\')
            {
                index++;
                continue;
            }
            text.Append(Encoding.UTF8.GetString(raw[start..index]));
            var escape = raw[index + 1];
            index += 2;
            switch (escape)
            {
                case (byte)'b': text.Append('\b'); break;
                case (byte)'f': text.Append('\f'); break;
                case (byte)'n': text.Append('\n'); break;
                case (byte)'r': text.Append('\r'); break;
                case (byte)'t': text.Append('\t'); break;
                case (byte)'u':
                    text.Append((char)int.Parse(
                        Encoding.ASCII.GetString(raw.Slice(index, 4)),
                        NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture));
                    index += 4;
                    break;
                default:
                    // \" \\ \/
                    text.Append((char)escape);
                    break;
            }
            start = index;
        }
        text.Append(Encoding.UTF8.GetString(raw[start..]));

        for (var position = 0; position < text.Length; position++)
        {
            if (char.IsHighSurrogate(text[position]) &&
                position + 1 < text.Length &&
                char.IsLowSurrogate(text[position + 1]))
            {
                position++;
                continue;
            }
            if (char.IsSurrogate(text[position])) text[position] = '�';
        }
        return text.ToString();
    }
}

/// <summary>Hides tools a route is not allowed to expose, in both directions (§7.11).</summary>
/// <remarks>
/// <para>
/// This is the one place Kytto stops being a transparent relay. §3.3 promises
/// byte-for-byte forwarding, and that promise still holds for every route without
/// an allow list — an unmasked route never constructs one of these. When a list is
/// present, exactly these kinds of message are altered:
/// </para>
/// <list type="bullet">
/// <item>a <c>tools/list</c> <strong>result</strong> has hidden tools removed from
/// its array, which is what makes the context saving real rather than cosmetic. The
/// response is re-serialized, so key order within it is not preserved; that is a
/// protocol message and not a user's config file (§6 governs the latter).</item>
/// <item>a <c>tools/call</c> <strong>request</strong> for a hidden tool is refused
/// here and never reaches the upstream server.</item>
/// <item>a client message the filter cannot read in exactly one way — not JSON,
/// a repeated key, a <c>tools/call</c> without a single string <c>name</c> — is
/// refused rather than forwarded. Forwarding what the filter could not read would
/// let the server read it differently (AUD-07).</item>
/// <item>a server message with a repeated key is re-serialized, so the client reads
/// the same message the filter acted on.</item>
/// </list>
/// <para>
/// Everything else — notifications, prompts, resources, banners on the server's
/// stdout — is forwarded untouched.
/// </para>
/// <para>
/// The allow list is exact and closed: a tool the server starts offering after the
/// list was chosen stays hidden until the user says otherwise. A context budget that
/// quietly grows when a server updates would defeat the point of setting one, and
/// Contract Guard already reports the new tool (§7.10).
/// </para>
/// </remarks>
public sealed class GatewayToolFilter(IReadOnlyList<string> exposedTools)
{
    /// <summary>What to do with one line travelling client → server.</summary>
    public abstract record ClientDecision
    {
        /// <summary>Send the line upstream unchanged.</summary>
        public sealed record Forward : ClientDecision;

        /// <summary>Do not send it. Write this JSON-RPC error back to the client instead.</summary>
        public sealed record Refuse(byte[] Response) : ClientDecision;
    }

    /// <summary>JSON-RPC "invalid params".</summary>
    /// <remarks>
    /// What MCP servers answer for a tool they do not have, which is the behaviour
    /// being imitated. "Method not found" would tell the client the server has no
    /// <c>tools/call</c> at all.
    /// </remarks>
    internal const int UnknownTool = -32_602;

    internal const int ParseError = -32_700;

    internal const int InvalidRequest = -32_600;

    /// <summary>
    /// What the activity journal records for a refused message that had no readable
    /// tool name.
    /// </summary>
    public const string UnreadableRequest = "(unreadable request)";

    /// <summary>
    /// Ordinal, which for text that decoded cleanly is byte for byte. A culture or
    /// normalizing comparison would treat a decomposed spelling of an exposed name as
    /// that name; the server does not, so it is a different tool.
    /// </summary>
    private readonly HashSet<string> _exposed = new(exposedTools, StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Ids of <c>tools/list</c> requests still waiting for an answer, so only the
    /// matching responses are rewritten rather than anything holding a <c>tools</c>
    /// key.
    /// </summary>
    private readonly HashSet<string> _pendingListRequests = new(StringComparer.Ordinal);

    /// <summary>Whether a tool survives the filter.</summary>
    public bool Allows(string toolName) => _exposed.Contains(toolName);

    // MARK: - Client to server

    /// <remarks>
    /// Never throws. A filter that cannot decide refuses: an exception here would end
    /// the relay, and what it had buffered would reach the server unread.
    /// </remarks>
    public ClientDecision InspectClientLine(ReadOnlySpan<byte> line)
    {
        try
        {
            return Evaluate(line, registeringLists: true).Decision;
        }
        catch (Exception)
        {
            return new ClientDecision.Refuse(Error(null, ParseError, CouldNotRead));
        }
    }

    /// <summary>The refused calls in a client line, for the activity journal.</summary>
    /// <remarks>
    /// Names and request ids only; arguments are never looked at. A message refused
    /// because it could not be read is recorded as <see cref="UnreadableRequest"/>.
    /// </remarks>
    public IReadOnlyList<(string ToolName, string? RequestId)> HiddenCalls(ReadOnlySpan<byte> line)
    {
        try
        {
            return Evaluate(line, registeringLists: false).Refused;
        }
        catch (Exception)
        {
            return [(UnreadableRequest, null)];
        }
    }

    private const string CouldNotRead = "Kytto could not read this message, so it was not forwarded.";

    private enum CallKind { NotACall, Allowed, Hidden, Unreadable }

    private (CallKind Kind, string? Name) Check(JsonObject message)
    {
        if (StringAt(message, "method") != "tools/call") return (CallKind.NotACall, null);
        // U+FFFD is what an unpaired surrogate or a stray byte was read as. The server
        // may keep the original, so the name Kytto would approve is not necessarily
        // the one it would run.
        if (StringAt(message["params"], "name") is not { } name || name.Contains('�'))
        {
            return (CallKind.Unreadable, null);
        }
        return Allows(name) ? (CallKind.Allowed, name) : (CallKind.Hidden, name);
    }

    private (ClientDecision Decision, IReadOnlyList<(string ToolName, string? RequestId)> Refused) Evaluate(
        ReadOnlySpan<byte> line,
        bool registeringLists)
    {
        // Blank lines carry nothing a server could act on.
        if (GatewayStrictJson.IsBlank(line)) return (new ClientDecision.Forward(), []);

        if (GatewayStrictJson.Read(line) is not { } reading)
        {
            return (new ClientDecision.Refuse(Error(null, ParseError, CouldNotRead)), [(UnreadableRequest, null)]);
        }
        if (reading.HasDuplicateKeys)
        {
            return (
                new ClientDecision.Refuse(Error(
                    reading.RawId,
                    InvalidRequest,
                    "Kytto refused a message that repeats a key: it can be read more than one way.")),
                [(UnreadableRequest, null)]);
        }

        if (reading.Root is JsonArray batch) return EvaluateBatch(batch, registeringLists);
        if (reading.Root is not JsonObject message) return (new ClientDecision.Forward(), []);

        if (StringAt(message, "method") == "tools/list")
        {
            if (registeringLists) Register(message["id"]);
            return (new ClientDecision.Forward(), []);
        }

        var requestId = RequestId(message["id"]);
        var (kind, name) = Check(message);
        switch (kind)
        {
            case CallKind.Hidden:
                // A notification has no id and therefore no reply to send. Dropping
                // it silently is still correct: the tool is not available.
                if (requestId is null) return (new ClientDecision.Refuse([]), [(name!, null)]);
                // The id goes back exactly as it arrived — a client that sent a number
                // and gets a string back cannot match its own request.
                return (
                    new ClientDecision.Refuse(Error(
                        reading.RawId,
                        UnknownTool,
                        $"{name} is not available: Kytto is not exposing this tool on this route.")),
                    [(name!, requestId)]);
            case CallKind.Unreadable:
                if (requestId is null) return (new ClientDecision.Refuse([]), [(UnreadableRequest, null)]);
                return (
                    new ClientDecision.Refuse(Error(
                        reading.RawId,
                        UnknownTool,
                        "Kytto refused a tools/call whose tool name it cannot read: params.name must be one string.")),
                    [(UnreadableRequest, requestId)]);
            default:
                return (new ClientDecision.Forward(), []);
        }
    }

    /// <summary>A JSON-RPC batch, inspected element by element.</summary>
    /// <remarks>
    /// Letting a batch through unread would let a hidden tool be called by wrapping
    /// it in <c>[…]</c>. One hidden or unreadable call — or an element that is not a
    /// message at all — refuses the whole batch, answered as a batch.
    /// </remarks>
    private (ClientDecision Decision, IReadOnlyList<(string ToolName, string? RequestId)> Refused) EvaluateBatch(
        JsonArray batch,
        bool registeringLists)
    {
        var objects = batch.OfType<JsonObject>().ToArray();
        var refused = new List<(string ToolName, string? RequestId)>();
        if (objects.Length != batch.Count) refused.Add((UnreadableRequest, null));
        foreach (var item in objects)
        {
            var (kind, name) = Check(item);
            if (kind == CallKind.Hidden) refused.Add((name!, RequestId(item["id"])));
            else if (kind == CallKind.Unreadable) refused.Add((UnreadableRequest, RequestId(item["id"])));
        }

        if (refused.Count == 0)
        {
            if (registeringLists)
            {
                foreach (var item in objects.Where(item => StringAt(item, "method") == "tools/list"))
                {
                    Register(item["id"]);
                }
            }
            return (new ClientDecision.Forward(), []);
        }

        var replies = new JsonArray();
        foreach (var item in objects)
        {
            if (RequestId(item["id"]) is null) continue;
            replies.Add(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = item["id"]?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = UnknownTool,
                    ["message"] =
                        "Kytto refused this batch: it calls a tool this route does not expose, or a tool it cannot read.",
                },
            });
        }
        return (
            new ClientDecision.Refuse(replies.Count == 0 ? [] : Encoding.UTF8.GetBytes(replies.ToJsonString())),
            refused);
    }

    private void Register(JsonNode? id)
    {
        if (RequestKey(id) is not { } key) return;
        lock (_lock) _pendingListRequests.Add(key);
    }

    /// <summary>An error built around the id's original text, so the id round-trips exactly.</summary>
    private static byte[] Error(string? rawId, int code, string message) =>
        Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":" + (rawId ?? "null") +
            ",\"error\":{\"code\":" + code.ToString(CultureInfo.InvariantCulture) +
            ",\"message\":" + JsonSerializer.Serialize(message) + "}}");

    // MARK: - Server to client

    /// <summary>
    /// The line to forward, which is the original unless it is a <c>tools/list</c>
    /// result with something to remove, or a message with a repeated key.
    /// </summary>
    /// <remarks>
    /// A line that is not JSON — a banner on stdout — is forwarded untouched. A
    /// repeated key would let the client read a different id or tool list from the
    /// one filtered here, so such a message is passed on in the one reading the
    /// filter used.
    /// </remarks>
    public byte[] RewriteServerLine(byte[] line)
    {
        try
        {
            if (GatewayStrictJson.Read(line) is not { } reading) return line;
            var changed = reading.HasDuplicateKeys;
            switch (reading.Root)
            {
                case JsonArray batch:
                    // A batched response: each element answers on its own.
                    foreach (var element in batch)
                    {
                        if (element is JsonObject item && FilterListResponse(item)) changed = true;
                    }
                    return changed ? Encoding.UTF8.GetBytes(batch.ToJsonString()) : line;
                case JsonObject message:
                    if (FilterListResponse(message)) changed = true;
                    return changed ? Encoding.UTF8.GetBytes(message.ToJsonString()) : line;
                default:
                    return line;
            }
        }
        catch (Exception)
        {
            // Never the end of the session over one odd message; a hidden tool that
            // slips into a list still cannot be called.
            return line;
        }
    }

    /// <summary>
    /// Removes hidden tools from a pending <c>tools/list</c> answer, in place; whether
    /// anything was removed.
    /// </summary>
    private bool FilterListResponse(JsonObject message)
    {
        // Only a response can answer a pending request. The server numbers its own
        // requests (`roots/list`, `sampling/createMessage`) from its own id space,
        // and one that happens to reuse a pending id must not use it up — the real
        // tool list would then pass through unfiltered.
        if (message.ContainsKey("method") ||
            !(message.ContainsKey("result") || message.ContainsKey("error"))) return false;
        if (RequestKey(message["id"]) is not { } id) return false;

        bool isListResponse;
        lock (_lock) isListResponse = _pendingListRequests.Remove(id);
        if (!isListResponse) return false;

        if (message["result"] is not JsonObject result) return false;
        if (result["tools"] is not JsonArray tools) return false;

        // An entry without a readable name cannot be shown to be allowed.
        var kept = tools
            .Where(tool => StringAt(tool, "name") is { } name && Allows(name))
            .Select(tool => tool!.DeepClone())
            .ToArray();
        if (kept.Length == tools.Count) return false;

        result["tools"] = new JsonArray(kept);
        return true;
    }

    /// <summary>A string member of an object, or null for any other shape.</summary>
    /// <remarks>
    /// <c>GetValue&lt;string&gt;</c> throws on a number or an object, and one odd
    /// message from either side must not end the relay for the whole session. A
    /// value that is not a string is treated as absent.
    /// </remarks>
    private static string? StringAt(JsonNode? node, string key) =>
        node is JsonObject json && json[key] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    /// <summary>The id as a correlation key, kind included.</summary>
    /// <remarks>
    /// <c>1</c> and <c>"1"</c> are different requests, and matching them up would
    /// filter — or skip — the wrong reply.
    /// </remarks>
    private static string? RequestKey(JsonNode? value) =>
        RequestId(value) is { } id
            ? (value is JsonValue json && json.TryGetValue<string>(out _) ? "s:" : "n:") + id
            : null;

    /// <summary>JSON-RPC ids are strings or numbers; booleans are neither.</summary>
    private static string? RequestId(JsonNode? value)
    {
        if (value is not JsonValue json) return null;
        if (json.TryGetValue<string>(out var text)) return text;
        if (json.TryGetValue<bool>(out _)) return null;
        return json.TryGetValue<decimal>(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
