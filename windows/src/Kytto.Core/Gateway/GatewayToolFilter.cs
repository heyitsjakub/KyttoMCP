using System.Buffers;
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
    public void Consume(ReadOnlySpan<byte> data, Action<byte[]> body)
    {
        _buffer.AddRange(data);
        while (true)
        {
            var newline = _buffer.IndexOf((byte)'\n');
            if (newline < 0) break;
            body([.. _buffer.Take(newline)]);
            _buffer.RemoveRange(0, newline + 1);
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
        body([.. _buffer]);
        _buffer.Clear();
    }
}

/// <summary>Hides tools a route is not allowed to expose, in both directions (§7.11).</summary>
/// <remarks>
/// <para>
/// This is the one place Kytto stops being a transparent relay. §3.3 promises
/// byte-for-byte forwarding, and that promise still holds for every route without
/// an allow list — an unmasked route never constructs one of these. When a list is
/// present, exactly two kinds of message are altered:
/// </para>
/// <list type="bullet">
/// <item>a <c>tools/list</c> <strong>result</strong> has hidden tools removed from
/// its array, which is what makes the context saving real rather than cosmetic. The
/// response is re-serialized, so key order within it is not preserved; that is a
/// protocol message and not a user's config file (§6 governs the latter).</item>
/// <item>a <c>tools/call</c> <strong>request</strong> for a hidden tool is refused
/// here and never reaches the upstream server.</item>
/// </list>
/// <para>
/// Everything else — notifications, prompts, resources, banners on stdout, anything
/// that is not valid JSON — is forwarded untouched.
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

    public ClientDecision InspectClientLine(ReadOnlySpan<byte> line)
    {
        var parsed = Parse(line);
        if (parsed is JsonArray batch) return InspectBatch(batch);
        if (parsed is not JsonObject message) return new ClientDecision.Forward();

        switch (StringAt(message, "method"))
        {
            case "tools/list":
                if (RequestKey(message["id"]) is { } listId)
                {
                    lock (_lock) _pendingListRequests.Add(listId);
                }
                return new ClientDecision.Forward();

            case "tools/call":
                var name = StringAt(message["params"], "name");
                if (name is null || Allows(name)) return new ClientDecision.Forward();
                // A notification has no id and therefore no reply to send. Dropping
                // it silently is still correct: the tool is not available.
                if (RequestId(message["id"]) is null) return new ClientDecision.Refuse([]);
                return new ClientDecision.Refuse(Refusal(message["id"], name));

            default:
                return new ClientDecision.Forward();
        }
    }

    /// <summary>A JSON-RPC batch, inspected element by element.</summary>
    /// <remarks>
    /// Letting a batch through unread would let a hidden tool be called by wrapping
    /// it in <c>[…]</c>. One hidden call refuses the whole batch, answered as a batch.
    /// </remarks>
    private ClientDecision InspectBatch(JsonArray batch)
    {
        var objects = batch.OfType<JsonObject>().ToArray();
        var hidden = objects.Any(item =>
            StringAt(item, "method") == "tools/call" &&
            StringAt(item["params"], "name") is { } name && !Allows(name));
        if (!hidden)
        {
            foreach (var item in objects.Where(item => StringAt(item, "method") == "tools/list"))
            {
                if (RequestKey(item["id"]) is { } listId)
                {
                    lock (_lock) _pendingListRequests.Add(listId);
                }
            }
            return new ClientDecision.Forward();
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
                    ["message"] = "Kytto refused this batch: it calls a tool this route does not expose.",
                },
            });
        }
        return new ClientDecision.Refuse(replies.Count == 0 ? [] : Encoding.UTF8.GetBytes(replies.ToJsonString()));
    }

    /// <summary>The hidden tools a client line tries to call, for the activity journal.</summary>
    /// <remarks>Names and request ids only; arguments are never looked at.</remarks>
    public IReadOnlyList<(string ToolName, string? RequestId)> HiddenCalls(ReadOnlySpan<byte> line)
    {
        var objects = Parse(line) switch
        {
            JsonArray batch => batch.OfType<JsonObject>().ToArray(),
            JsonObject message => [message],
            _ => [],
        };
        var calls = new List<(string, string?)>();
        foreach (var item in objects)
        {
            if (StringAt(item, "method") != "tools/call" ||
                StringAt(item["params"], "name") is not { } name || Allows(name)) continue;
            calls.Add((name, RequestId(item["id"])));
        }
        return calls;
    }

    private static byte[] Refusal(JsonNode? id, string toolName)
    {
        // The id goes back exactly as it arrived — a client that sent a number and
        // gets a string back cannot match its own request.
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = UnknownTool,
                ["message"] =
                    $"{toolName} is not available: Kytto is not exposing this tool on this route.",
            },
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    // MARK: - Server to client

    /// <summary>
    /// The line to forward, which is the original unless it is a <c>tools/list</c>
    /// result with something to remove.
    /// </summary>
    public byte[] RewriteServerLine(byte[] line)
    {
        var parsed = Parse(line);
        if (parsed is JsonArray batch)
        {
            // A batched response: each element answers on its own.
            var changed = false;
            var rewritten = new JsonArray();
            foreach (var element in batch)
            {
                if (element is JsonObject item && Rewrite(item) is { } filtered)
                {
                    rewritten.Add(filtered.DeepClone());
                    changed = true;
                }
                else
                {
                    rewritten.Add(element?.DeepClone());
                }
            }
            return changed ? Encoding.UTF8.GetBytes(rewritten.ToJsonString()) : line;
        }
        if (parsed is not JsonObject message) return line;
        return Rewrite(message) is { } result ? Encoding.UTF8.GetBytes(result.ToJsonString()) : line;
    }

    /// <summary>The message with hidden tools removed, or null when it is not one to change.</summary>
    private JsonObject? Rewrite(JsonObject message)
    {
        // Only a response can answer a pending request. The server numbers its own
        // requests (`roots/list`, `sampling/createMessage`) from its own id space,
        // and one that happens to reuse a pending id must not use it up — the real
        // tool list would then pass through unfiltered.
        if (message.ContainsKey("method") ||
            !(message.ContainsKey("result") || message.ContainsKey("error"))) return null;
        if (RequestKey(message["id"]) is not { } id) return null;

        bool isListResponse;
        lock (_lock) isListResponse = _pendingListRequests.Remove(id);
        if (!isListResponse) return null;

        if (message["result"] is not JsonObject result) return null;
        if (result["tools"] is not JsonArray tools) return null;

        var kept = tools
            .Where(tool => StringAt(tool, "name") is { } name && Allows(name))
            .Select(tool => tool!.DeepClone())
            .ToArray();
        if (kept.Length == tools.Count) return null;

        result["tools"] = new JsonArray(kept);
        return message;
    }

    /// <remarks>
    /// Built member by member, last-wins, rather than with <c>JsonNode.Parse</c>:
    /// that throws on a repeated key the first time the object is read, and the
    /// client and server on either side resolve one by keeping the last. Reading it
    /// the same way is what stops <c>{"name":"read_file","name":"delete_all"}</c> from
    /// meaning one tool to the filter and another to the server.
    /// </remarks>
    private static JsonNode? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            return Build(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON at all — a banner on stdout, say. Forwarded untouched.
            return null;
        }
    }

    private static JsonNode? Build(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var json = new JsonObject();
                foreach (var property in element.EnumerateObject()) json[property.Name] = Build(property.Value);
                return json;
            case JsonValueKind.Array:
                return new JsonArray(element.EnumerateArray().Select(Build).ToArray());
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                // Cloned: the document this element belongs to is disposed on return.
                return JsonValue.Create(element.Clone());
        }
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
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
