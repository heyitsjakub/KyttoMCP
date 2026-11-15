using System.Net.Http;
using System.Text.Json;
using Kytto.Core.Tokenizer;
using JsonDocument = Kytto.Core.Json.JsonDocument;
using JsonNode = Kytto.Core.Json.JsonNode;

namespace Kytto.Core.Health;

/// <summary>
/// Streamable HTTP inspection (§7.3): read-only listings, and never <c>tools/call</c>.
/// </summary>
/// <remarks>
/// <para>
/// Speaks both eras. The first request is a modern <c>server/discover</c> carrying
/// <c>MCP-Protocol-Version: 2026-07-28</c> and <c>Mcp-Method</c>; a discovery result or
/// a recognised modern error settles it. Anything else — the 400 a handshake-era server
/// gives a request without a session, a JSON-RPC error it invents — falls back to the
/// <c>initialize</c> handshake, and a handshake that is refused outright is checked for
/// the deprecated HTTP+SSE transport before being called broken (spec:
/// basic/transports/streamable-http, "Backward Compatibility").
/// </para>
/// <para>
/// Responses may be plain JSON or an event stream; each is matched to its request by
/// JSON-RPC id, and anything else a server says on the way (its own notifications and
/// requests) is ignored and never answered.
/// </para>
/// <para>
/// Everything here is bounded, because the other end is not trusted: 4 MiB per
/// response, 8 MiB in all, 50 pages, 10,000 tools, JSON nested at most 128 deep, a
/// cursor that repeats is refused, and one absolute deadline covers the network and
/// the parsing and the token counting that follow it.
/// </para>
/// </remarks>
public sealed class McpHttpClient
{
    public const string PreferredProtocolVersion = McpStdioClient.PreferredProtocolVersion;
    public const string ModernProtocolVersion = McpStdioClient.ModernProtocolVersion;

    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxPages = 50;
    private const int MaxTools = 10_000;
    private const int MaxCursorBytes = 8_192;

    /// <summary>How long the best-effort session close may take, inside what is left.</summary>
    internal static readonly TimeSpan CleanupBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the legacy HTTP+SSE probe may take.</summary>
    internal static readonly TimeSpan LegacySseBudget = TimeSpan.FromSeconds(2);

    /// <summary>How much of a legacy event stream the probe reads.</summary>
    private const int LegacySseBytes = 64 * 1024;

    private readonly IRemoteHttpTransport? _transport;

    public McpHttpClient() { }

    /// <summary>A JSON-RPC error's code, for the HTTP and stdio clients alike.</summary>
    /// <remarks>
    /// A missing code reads as 0, as before; one that is present but not an integer
    /// Kytto can hold is a protocol failure rather than a number to convert (AUD-01).
    /// </remarks>
    internal static int ErrorCode(JsonNode error, Func<string, Exception> failure)
    {
        if (error["code"] is not { } raw) return 0;
        return raw.IntegerValue ?? throw failure("the JSON-RPC error code is not an integer");
    }

    /// <param name="transport">A transport to use instead of the network; owned by the caller.</param>
    public McpHttpClient(IRemoteHttpTransport transport) => _transport = transport;

    public async Task<ToolListing> ListToolsAsync(
        string url,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = RemoteHttpEndpoint.Validate(url);
        RemoteHttpHeaders.Validate(headers);

        var budget = timeout ?? TimeSpan.FromSeconds(10);
        if (budget < TimeSpan.FromMilliseconds(50)) budget = TimeSpan.FromMilliseconds(50);
        if (budget > TimeSpan.FromSeconds(60)) budget = TimeSpan.FromSeconds(60);
        var deadline = DateTimeOffset.UtcNow + budget;

        var owned = _transport is null ? new HttpClientRemoteTransport() : null;
        try
        {
            var session = new Session(_transport ?? owned!, endpoint, headers, deadline, cancellationToken);
            return await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>What one POST came back with, before the caller decides what it means.</summary>
    private abstract record Answer
    {
        public sealed record Result(JsonDocument Document) : Answer;

        public sealed record RpcError(int Code, IReadOnlyList<string> Supported) : Answer;

        /// <summary>A non-2xx reply, with the JSON-RPC error code its body carried, if any.</summary>
        public sealed record Status(int Code, int? RpcCode, IReadOnlyList<string> Supported) : Answer;
    }

    private sealed class Session(
        IRemoteHttpTransport transport,
        Uri endpoint,
        IReadOnlyDictionary<string, string>? configuredHeaders,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        private string? _sessionId;
        private string? _protocolVersion;
        private bool _modern;
        private int _receivedBytes;

        internal async Task<ToolListing> RunAsync()
        {
            try
            {
                return await InspectAsync().ConfigureAwait(false);
            }
            finally
            {
                await EndSessionAsync().ConfigureAwait(false);
            }
        }

        private async Task<ToolListing> InspectAsync()
        {
            // Modern first: one POST that a 2026-07-28 server must answer.
            _modern = true;
            _protocolVersion = ModernProtocolVersion;
            var discovery = await PostAsync("server/discover", 1, new Dictionary<string, object?>
            {
                ["_meta"] = McpStdioClient.Meta,
            }).ConfigureAwait(false);

            string[] capabilities;
            string? serverName;
            string? serverVersion;
            string negotiated;
            switch (discovery)
            {
                case Answer.Result { Document: var document }
                    when document.ValueAt("result", "supportedVersions")?.Elements is { } elements:
                {
                    var versions = elements.Select(element => element.StringValue).OfType<string>().ToArray();
                    if (!versions.Contains(ModernProtocolVersion, StringComparer.Ordinal)) throw Unsupported(versions);
                    var info = document.ValueAt("result", "_meta", "io.modelcontextprotocol/serverInfo");
                    capabilities = CapabilityNames(document);
                    serverName = info?["name"]?.StringValue;
                    serverVersion = info?["version"]?.StringValue;
                    negotiated = ModernProtocolVersion;
                    break;
                }
                case Answer.RpcError { Code: var code, Supported: var supported }
                    when McpStdioClient.ModernErrorCodes.Contains(code):
                    throw ModernRefusal(code, supported);
                case Answer.Status { RpcCode: { } code, Supported: var supported }
                    when McpStdioClient.ModernErrorCodes.Contains(code):
                    throw ModernRefusal(code, supported);
                case Answer.Status { Code: var status } when status is 403 or 429 or (>= 300 and < 400):
                    // Forbidden, rate-limited or redirected says nothing about the era; a
                    // handshake would meet the same answer.
                    throw new RemoteHttpException(RemoteHttpFailure.HttpStatus, status: status);
                default:
                {
                    // Handshake era.
                    var document = await HandshakeAsync().ConfigureAwait(false);
                    capabilities = CapabilityNames(document);
                    serverName = document.ValueAt("result", "serverInfo", "name")?.StringValue;
                    serverVersion = document.ValueAt("result", "serverInfo", "version")?.StringValue;
                    negotiated = _protocolVersion!;
                    break;
                }
            }

            var entries = new List<(JsonDocument Document, JsonNode Tool)>();
            var arrays = new List<string>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;

            if (capabilities.Contains("tools", StringComparer.Ordinal))
            {
                for (var page = 0; page < MaxPages; page++)
                {
                    ThrowIfExpired();
                    var document = await RequestAsync(
                        "tools/list",
                        2 + page,
                        cursor is null ? null : new Dictionary<string, object?> { ["cursor"] = cursor }).ConfigureAwait(false);

                    var tools = document.ValueAt("result", "tools");
                    if (tools?.Elements is not { } elements ||
                        !elements.All(tool => tool.LastValue("name")?.StringValue is not null &&
                                              tool.LastValue("inputSchema")?.Members is not null))
                    {
                        throw RemoteHttpException.Protocol("tools/list did not return valid tool definitions");
                    }
                    if (entries.Count + elements.Count > MaxTools)
                    {
                        throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
                    }
                    entries.AddRange(elements.Select(tool => (document, tool)));
                    arrays.Add(document.Slice(tools.Span));

                    var next = document.ValueAt("result", "nextCursor");
                    if (next is null) break;
                    if (next.StringValue is not { Length: > 0 } text ||
                        System.Text.Encoding.UTF8.GetByteCount(text) > MaxCursorBytes ||
                        !cursors.Add(text) ||
                        page == MaxPages - 1)
                    {
                        throw RemoteHttpException.Protocol("tools pagination did not finish within the inspection limit");
                    }
                    cursor = text;
                }
            }

            var notes = new List<string>();
            var prompts = await OptionalCountAsync("prompts", "prompts/list", 100, capabilities, notes).ConfigureAwait(false);
            var resources = await OptionalCountAsync("resources", "resources/list", 101, capabilities, notes).ConfigureAwait(false);

            var tokenizer = BpeTokenizer.Cl100kBase;
            var summaries = new List<ToolSummary>(entries.Count);
            foreach (var (document, tool) in entries)
            {
                ThrowIfExpired();
                int? tokens;
                try
                {
                    tokens = tokenizer?.CountTokens(document.Slice(tool.Span), deadline);
                }
                catch (TimeoutException)
                {
                    throw RemoteHttpException.TimedOut();
                }
                summaries.Add(new ToolSummary(
                    tool.LastValue("name")!.StringValue!,
                    tool.LastValue("description")?.StringValue,
                    tool.LastValue("inputSchema") is { } schema ? document.Slice(schema.Span) : null,
                    // As the stdio client reads them, so one server compares the same
                    // whichever way it was checked.
                    ToolAnnotations.From(tool.LastValue("annotations")),
                    tokens));
            }
            ThrowIfExpired();

            var rawTools = arrays.Count switch
            {
                // A server with no tools hands the model nothing.
                0 => "[]",
                1 => arrays[0],
                _ => "[" + string.Join(",", entries.Select(entry => entry.Document.Slice(entry.Tool.Span))) + "]",
            };
            ThrowIfExpired();

            return new ToolListing(
                Tools: summaries,
                RawToolsJson: rawTools,
                ServerName: serverName,
                ServerVersion: serverVersion,
                NegotiatedProtocolVersion: negotiated,
                CapabilityNames: capabilities,
                PromptCount: prompts,
                ResourceCount: resources,
                InspectionNotes: notes,
                ResolvedCommand: "")
            {
                DeprecatedFeatures = DeprecatedFeature.Detected(capabilities, Array.Empty<string>()),
            };
        }

        /// <summary>The handshake-era opening: <c>initialize</c>, then the initialized notification.</summary>
        private async Task<JsonDocument> HandshakeAsync()
        {
            _modern = false;
            _protocolVersion = null;
            var initialize = await PostAsync("initialize", 1, new Dictionary<string, object?>
            {
                ["protocolVersion"] = PreferredProtocolVersion,
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new { name = "Kytto", version = "1.0" },
            }).ConfigureAwait(false);

            JsonDocument document;
            switch (initialize)
            {
                case Answer.Result result:
                    document = result.Document;
                    break;
                case Answer.Status { Code: var status } when status is 400 or 404 or 405:
                    if (await IsLegacySseAsync().ConfigureAwait(false))
                    {
                        throw new RemoteHttpException(RemoteHttpFailure.LegacySse);
                    }
                    throw new RemoteHttpException(RemoteHttpFailure.HttpStatus, status: status);
                case Answer.Status { Code: var status }:
                    throw new RemoteHttpException(RemoteHttpFailure.HttpStatus, status: status);
                default:
                    throw RemoteHttpException.Protocol("the server returned a JSON-RPC error");
            }

            var version = document.ValueAt("result", "protocolVersion")?.StringValue;
            if (version is null ||
                !McpStdioClient.HandshakeProtocolVersions.Contains(version, StringComparer.Ordinal) ||
                document.ValueAt("result", "capabilities")?.Members is null)
            {
                throw RemoteHttpException.Protocol("unsupported protocol version or missing capabilities");
            }
            _protocolVersion = version;

            // A notification is accepted with any 2xx; 202 is what the spec asks for,
            // but working clients do not insist on it.
            if (await PostAsync("notifications/initialized", null, null).ConfigureAwait(false) is Answer.Status)
            {
                throw RemoteHttpException.Protocol("the initialized notification was not accepted");
            }
            return document;
        }

        private static string[] CapabilityNames(JsonDocument document) =>
            document.ValueAt("result", "capabilities")?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [];

        private static RemoteHttpException ModernRefusal(int code, IReadOnlyList<string> supported) =>
            code == McpStdioClient.UnsupportedProtocolVersionCode
                ? Unsupported(supported)
                : RemoteHttpException.Protocol($"the server rejected Kytto's request metadata ({code})");

        private static RemoteHttpException Unsupported(IReadOnlyList<string> versions) =>
            RemoteHttpException.Protocol(
                $"the server supports MCP {(versions.Count == 0 ? "versions it did not name" : string.Join(", ", versions))}, " +
                $"not {ModernProtocolVersion} or a handshake revision Kytto speaks");

        private async Task<int?> OptionalCountAsync(
            string capability,
            string method,
            int id,
            IReadOnlyList<string> capabilities,
            List<string> notes)
        {
            if (!capabilities.Contains(capability, StringComparer.Ordinal)) return null;
            try
            {
                var document = await RequestAsync(method, id, null).ConfigureAwait(false);
                if (document.ValueAt("result", capability)?.Elements is not { } values)
                {
                    notes.Add($"{method} did not return its expected array.");
                    return null;
                }
                if (document.ValueAt("result", "nextCursor") is not null)
                {
                    notes.Add($"{char.ToUpperInvariant(capability[0])}{capability[1..]} count shows the first page; the server has more.");
                }
                return values.Count;
            }
            catch (RemoteHttpException failure) when (
                failure.Failure is RemoteHttpFailure.Protocol or RemoteHttpFailure.HttpStatus)
            {
                // Optional inspector data must not turn a successful tools handshake
                // into a failed check. Authorization, timeouts and size limits still
                // propagate: those are about the endpoint, not about this listing.
                notes.Add($"{method} could not be inspected.");
                return null;
            }
        }

        // MARK: - Requests

        /// <summary>A request whose answer must be a result; anything else fails the check.</summary>
        private async Task<JsonDocument> RequestAsync(string method, int id, IReadOnlyDictionary<string, object?>? parameters)
        {
            var body = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in parameters ?? new Dictionary<string, object?>()) body[key] = value;
            if (_modern) body["_meta"] = McpStdioClient.Meta;
            return await PostAsync(method, id, body).ConfigureAwait(false) switch
            {
                Answer.Result result => result.Document,
                // A server's error text can echo an Authorization header or a signed URL
                // query. The fact is recorded; the words are not.
                Answer.RpcError => throw RemoteHttpException.Protocol("the server returned a JSON-RPC error"),
                Answer.Status { Code: var status } => throw new RemoteHttpException(RemoteHttpFailure.HttpStatus, status: status),
                _ => throw RemoteHttpException.Protocol("no matching JSON-RPC response"),
            };
        }

        /// <summary>One POST, read up to the answer to <paramref name="id"/>; null for an accepted notification.</summary>
        private async Task<Answer?> PostAsync(string method, int? id, IReadOnlyDictionary<string, object?>? parameters)
        {
            var body = id is { } requestId
                ? JsonSerializer.SerializeToUtf8Bytes(new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters ?? new Dictionary<string, object?>(),
                })
                : JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method });
            var reply = await SendAsync(HttpMethod.Post, body, id, _modern ? method : null).ConfigureAwait(false);

            if (reply.Status == 401 || (reply.Status == 403 && reply.Header("www-authenticate") is not null))
            {
                throw new RemoteHttpException(RemoteHttpFailure.AuthorizationRequired);
            }
            if (reply.Status is < 200 or >= 300)
            {
                // Only the error code and the advertised versions are read from an error
                // body; its message can echo headers or signed URLs.
                var error = RpcErrorIn(reply);
                return new Answer.Status(reply.Status, error?.Code, error?.Supported ?? []);
            }
            if (id is not { } expected) return null;

            if (!_modern && reply.Header("mcp-session-id") is { } assigned && _sessionId is null)
            {
                if (assigned.Length == 0 || assigned.Length > 1_024 || assigned.Any(c => c is < '!' or > '~'))
                {
                    throw RemoteHttpException.Protocol("invalid session header");
                }
                _sessionId = assigned;
            }

            foreach (var message in Messages(reply))
            {
                if (RemoteJson.TryParse(message) is not { } document || !RemoteJson.IsResponseTo(document, expected)) continue;
                if (document.Root["error"] is { } rpcError)
                {
                    return new Answer.RpcError(ErrorCode(rpcError, RemoteHttpException.Protocol), SupportedIn(rpcError));
                }
                if (document.Root["result"]?.Members is null)
                {
                    throw RemoteHttpException.Protocol("the response has no result object");
                }
                return new Answer.Result(document);
            }
            throw RemoteHttpException.Protocol("no matching JSON-RPC response");
        }

        private static IReadOnlyList<string> Messages(RemoteHttpReply reply)
        {
            var media = (reply.Header("content-type") ?? "").ToLowerInvariant().Split(';')[0].Trim();
            if (media == "application/json")
            {
                return RemoteJson.Decode(reply.Body) is { } text
                    ? [text]
                    : throw RemoteHttpException.Protocol("the response was not valid UTF-8");
            }
            if (media == "text/event-stream") return ServerSentEventReader.Messages(reply.Body);
            throw RemoteHttpException.Protocol("expected application/json or text/event-stream");
        }

        /// <summary>
        /// The code and advertised versions of a JSON-RPC error in an HTTP error body, and
        /// nothing else from it.
        /// </summary>
        private static (int Code, IReadOnlyList<string> Supported)? RpcErrorIn(RemoteHttpReply reply)
        {
            if (reply.Body.Length == 0) return null;
            IReadOnlyList<string> messages;
            try
            {
                messages = Messages(reply);
            }
            catch (RemoteHttpException)
            {
                return null;
            }
            foreach (var message in messages)
            {
                if (RemoteJson.TryParse(message) is { } document &&
                    document.Root["error"] is { } error &&
                    error["code"]?.IntegerValue is { } code)
                {
                    return (code, SupportedIn(error));
                }
            }
            return null;
        }

        private static string[] SupportedIn(JsonNode error) =>
            error.ValueAt("data", "supported")?.Elements?
                .Select(element => element.StringValue).OfType<string>().ToArray() ?? [];

        /// <summary>Whether the endpoint is a deprecated HTTP+SSE server.</summary>
        /// <remarks>
        /// A GET that opens an event stream whose first event is <c>endpoint</c>, read for
        /// two seconds and 64 KiB at most. Any failure means "no", and the refusal that led
        /// here is reported instead.
        /// </remarks>
        private async Task<bool> IsLegacySseAsync()
        {
            var headers = new List<KeyValuePair<string, string>> { new("Accept", "text/event-stream") };
            foreach (var header in configuredHeaders ?? new Dictionary<string, string>()) headers.Add(header);
            var probeDeadline = DateTimeOffset.UtcNow + LegacySseBudget;
            if (probeDeadline > deadline) probeDeadline = deadline;

            RemoteHttpReply reply;
            try
            {
                reply = await transport.SendAsync(
                    new RemoteHttpRequest(HttpMethod.Get, endpoint, headers),
                    probeDeadline,
                    LegacySseBytes,
                    HttpClientRemoteTransport.FirstEventOnly,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteHttpException)
            {
                return false;
            }
            if (reply.Status != 200 ||
                reply.Header("content-type")?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) != true ||
                RemoteJson.Decode(reply.Body) is not { } text)
            {
                return false;
            }
            var firstEvent = text.Replace("\r\n", "\n").Split("\n\n")[0];
            return firstEvent.Split('\n').Any(line => line.Trim() == "event: endpoint" || line == "event:endpoint");
        }

        private async Task<RemoteHttpReply> SendAsync(HttpMethod method, byte[]? body, int? responseId, string? mcpMethod)
        {
            var headers = new List<KeyValuePair<string, string>>();
            if (method == HttpMethod.Post)
            {
                headers.Add(new("Accept", "application/json, text/event-stream"));
            }
            foreach (var header in configuredHeaders ?? new Dictionary<string, string>()) headers.Add(header);
            if (_sessionId is not null) headers.Add(new("MCP-Session-Id", _sessionId));
            if (_protocolVersion is not null) headers.Add(new("MCP-Protocol-Version", _protocolVersion));
            if (mcpMethod is not null) headers.Add(new("Mcp-Method", mcpMethod));

            var limit = Math.Min(MaxResponseBytes, MaxTotalBytes - _receivedBytes);
            if (limit <= 0) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);

            var reply = await transport.SendAsync(
                new RemoteHttpRequest(method, endpoint, headers, body, body is null ? null : "application/json"),
                deadline,
                limit,
                responseId,
                cancellationToken).ConfigureAwait(false);

            if (DateTimeOffset.UtcNow >= deadline) throw RemoteHttpException.TimedOut();
            if (reply.Body.Length > limit) throw new RemoteHttpException(RemoteHttpFailure.ResponseTooLarge);
            _receivedBytes += reply.Body.Length;
            return reply;
        }

        private void ThrowIfExpired()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline) throw RemoteHttpException.TimedOut();
        }

        /// <summary>Best-effort <c>DELETE</c> of the session, within what is left of the deadline.</summary>
        /// <remarks>
        /// A server that issued a session id is told it can drop it. Failing to say so
        /// changes nothing about the check, so every failure is swallowed — and it never
        /// takes more than 0.3 s, nor anything past the deadline the check was given.
        /// </remarks>
        private async Task EndSessionAsync()
        {
            if (_sessionId is null) return;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return;

            var budget = remaining < CleanupBudget ? remaining : CleanupBudget;
            try
            {
                var headers = (configuredHeaders ?? new Dictionary<string, string>()).ToList();
                headers.Add(new("MCP-Session-Id", _sessionId));
                if (_protocolVersion is not null) headers.Add(new("MCP-Protocol-Version", _protocolVersion));

                using var cleanup = new CancellationTokenSource(budget);
                await transport.SendAsync(
                    new RemoteHttpRequest(HttpMethod.Delete, endpoint, headers),
                    DateTimeOffset.UtcNow + budget,
                    1_024,
                    responseId: null,
                    cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort, by design.
            }
        }
    }
}
