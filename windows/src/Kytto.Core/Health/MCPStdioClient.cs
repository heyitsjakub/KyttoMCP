using System.Text.Json;
using Kytto.Core.Json;
using Kytto.Core.Tokenizer;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Health;

/// <param name="RawToolsJson">
/// The <c>tools</c> array exactly as the server sent it. Kept verbatim because
/// token weight is measured on what a model would actually be shown, not on a
/// re-serialization of Kytto's own model.
/// </param>
public sealed record ToolListing(
    IReadOnlyList<ToolSummary> Tools,
    string RawToolsJson,
    string? ServerName,
    string? ServerVersion,
    string? NegotiatedProtocolVersion,
    IReadOnlyList<string> CapabilityNames,
    int? PromptCount,
    int? ResourceCount,
    IReadOnlyList<string> InspectionNotes,
    string ResolvedCommand)
{
    /// <summary>What the check saw the server use that 2026-07-28 deprecates (<see cref="DeprecatedFeature"/>).</summary>
    public IReadOnlyList<string> DeprecatedFeatures { get; init; } = [];
}

public sealed class McpException(HealthFailureReason failure) : Exception(failure.CurrentMessage())
{
    public HealthFailureReason Failure { get; } = failure;

    public static McpException Protocol(string detail) =>
        new(HealthFailureReason.ProtocolError(detail));

    public static McpException ServerError(int code, string message) =>
        new(HealthFailureReason.ServerRefused(code, message));

    public static McpException ClosedBeforeAnswering() =>
        new(HealthFailureReason.ClosedBeforeAnswering());

    public static McpException Exited(int status) =>
        new(HealthFailureReason.Exited(status));
}

/// <summary>An error with the server's own output attached.</summary>
public sealed class HealthFailure(Exception underlying, string stderr, string? resolvedCommand = null)
    : Exception(underlying.Message)
{
    public Exception Underlying { get; } = underlying;
    public string Stderr { get; } = stderr;
    public string? ResolvedCommand { get; } = resolvedCommand;
}

/// <summary>
/// The smallest MCP client that can answer "does this server work, and how much
/// context do its tools cost?" (§7.3, §7.4).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately hand-written rather than pulled in as a dependency: the whole
/// exchange is a handful of messages, and doing it directly keeps full control over
/// the timeout and over capturing stderr, which is the part users actually need when
/// something is broken.
/// </para>
/// <para>
/// Speaks both eras of MCP. Revision 2026-07-28 removed the <c>initialize</c>
/// handshake: every request carries its version in <c>_meta</c>, and a client that
/// supports both eras probes with <c>server/discover</c> first. Any answer other than
/// a discovery result or a recognised modern error — including silence or the server
/// exiting — means a handshake-era server, which gets <c>initialize</c> (spec:
/// basic/transports/stdio, "Backward Compatibility").
/// </para>
/// </remarks>
public sealed class McpStdioClient
{
    /// <summary>The per-request-metadata revision Kytto probes for.</summary>
    public const string ModernProtocolVersion = "2026-07-28";

    /// <summary>What Kytto offers in an <c>initialize</c> handshake.</summary>
    /// <remarks>
    /// Handshake-era revisions only: 2026-07-28 has no handshake, so offering it there
    /// is meaningless and invites a dual-era server to answer in the wrong era. A
    /// server that speaks a different one answers with its own, and for listing tools
    /// that is fine — so the negotiated value is recorded, not enforced.
    /// </remarks>
    public const string PreferredProtocolVersion = "2025-11-25";

    internal static readonly string[] HandshakeProtocolVersions =
        ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The modern error that names the revisions a server does speak.</summary>
    internal const int UnsupportedProtocolVersionCode = -32022;

    /// <summary>Modern errors that prove the server speaks per-request metadata.</summary>
    internal static readonly int[] ModernErrorCodes = [-32020, -32021, -32022];

    private const int DiscoverId = 9;
    private const int MethodNotFound = -32601;

    /// <summary>How long a silent server gets before it is treated as handshake-era.</summary>
    private static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(2);

    public (ToolListing Listing, string Stderr) ListTools(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        string? workingDirectory = null)
    {
        ManagedProcess Spawn()
        {
            var spawned = new ManagedProcess(command, arguments, environment, workingDirectory);
            spawned.SetTimeoutBudget(timeout);
            return spawned;
        }

        var session = new Session(Spawn());
        var deadline = DateTimeOffset.UtcNow + timeout;
        var memoryKey = HandshakeMemory.Key(command, arguments, workingDirectory);

        try
        {
            ToolListing listing;
            var probed = false;
            var era = Era.Handshake;
            JsonDocument? discover = null;
            if (!HandshakeMemory.Contains(memoryKey))
            {
                probed = true;
                (era, discover) = Probe(session, deadline, timeout);
                if (era == Era.Exited)
                {
                    // Some handshake-era servers exit on any request before
                    // `initialize`. The spec's answer is a fresh process.
                    session.Process.Dispose();
                    session = new Session(Spawn());
                    era = Era.Handshake;
                }
            }

            if (era == Era.Modern)
            {
                HandshakeMemory.Remove(memoryKey);
                listing = ModernListing(discover!, session, deadline, timeout);
            }
            else
            {
                try
                {
                    listing = HandshakeListing(session, deadline);
                    HandshakeMemory.Add(memoryKey);
                }
                catch (McpException refused) when (refused.Failure.Reason == "serverRefused")
                {
                    // It refused the handshake. A modern server that was slow to start
                    // answers the probe late, after the handshake went out; one
                    // remembered as handshake-era may have moved on since.
                    HandshakeMemory.Remove(memoryKey);
                    if (session.LateDiscovery is { } late && IsModernDiscovery(late))
                    {
                        discover = late;
                    }
                    else if (!probed && Probe(session, deadline, timeout) is (Era.Modern, { } probedDiscovery))
                    {
                        discover = probedDiscovery;
                    }
                    else
                    {
                        throw;
                    }
                    listing = ModernListing(discover, session, deadline, timeout);
                }
            }

            session.Process.Terminate();
            return (listing, session.Process.CapturedStderr);
        }
        catch (Exception error)
        {
            // Whatever went wrong, the server's own words explain it better than
            // ours, so they travel with the error. Ending the process first lets the
            // last of them arrive.
            session.Process.Terminate();
            throw new HealthFailure(error, session.Process.CapturedStderr, session.Process.ResolvedExecutable);
        }
        finally
        {
            session.Process.Dispose();
        }
    }

    // MARK: - Eras

    private enum Era { Modern, Handshake, Exited }

    /// <summary>Sends <c>server/discover</c> and classifies the answer.</summary>
    private static (Era Era, JsonDocument? Discover) Probe(Session session, DateTimeOffset deadline, TimeSpan timeout)
    {
        try
        {
            session.Process.WriteLine(Request(DiscoverId, "server/discover", null, modern: true), deadline);
        }
        catch (Exception error) when (IsClosedPipe(error))
        {
            return (Era.Exited, null);
        }

        var limit = timeout / 4 < ProbeLimit ? timeout / 4 : ProbeLimit;
        var probeDeadline = DateTimeOffset.UtcNow + limit;
        if (probeDeadline > deadline) probeDeadline = deadline;
        try
        {
            var reply = Read(DiscoverId, session, probeDeadline);
            if (reply.Result is { } document)
            {
                if (document.ValueAt("result", "supportedVersions")?.Elements is not { } elements)
                {
                    // An answer that is not a discovery result: a lenient
                    // handshake-era server that replies to anything.
                    return (Era.Handshake, null);
                }
                var versions = elements.Select(element => element.StringValue).OfType<string>().ToArray();
                if (!versions.Contains(ModernProtocolVersion, StringComparer.Ordinal))
                {
                    throw McpException.Protocol(UnsupportedVersions(versions));
                }
                return (Era.Modern, document);
            }
            if (reply.Code == UnsupportedProtocolVersionCode)
            {
                throw McpException.Protocol(UnsupportedVersions(reply.Supported));
            }
            if (ModernErrorCodes.Contains(reply.Code))
            {
                throw McpException.ServerError(reply.Code, reply.Message);
            }
            return (Era.Handshake, null);
        }
        catch (McpException ended) when (ended.Failure.Reason is "closedBeforeAnswering" or "exited")
        {
            return (Era.Exited, null);
        }
        catch (Exception error) when (IsClosedPipe(error))
        {
            return (Era.Exited, null);
        }
        catch (ProcessException silent) when (silent.Failure.Reason == "timedOut" && DateTimeOffset.UtcNow < deadline)
        {
            return (Era.Handshake, null);
        }
    }

    private static bool IsModernDiscovery(JsonDocument document) =>
        document.ValueAt("result", "supportedVersions")?.Elements?
            .Any(element => element.StringValue == ModernProtocolVersion) == true;

    private static string UnsupportedVersions(IReadOnlyList<string> versions)
    {
        var list = versions.Count == 0 ? "none it would name" : string.Join(", ", versions);
        return $"the server supports MCP {list}; Kytto speaks {ModernProtocolVersion} and the handshake " +
               $"revisions up to {PreferredProtocolVersion}";
    }

    private static ToolListing HandshakeListing(Session session, DateTimeOffset deadline)
    {
        var version = PreferredProtocolVersion;
        JsonDocument initialize;
        for (var attempt = 0; ; attempt++)
        {
            session.Process.WriteLine(Request(1, "initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = ClientInfo,
            }, modern: false), deadline);
            var reply = Read(1, session, deadline);
            if (reply.Result is { } document)
            {
                initialize = document;
                break;
            }
            // A strict server named the revisions it accepts instead of answering
            // with its own; one retry with the best shared one.
            if (attempt == 0 &&
                HandshakeProtocolVersions.FirstOrDefault(candidate =>
                    reply.Supported.Contains(candidate, StringComparer.Ordinal)) is { } shared &&
                shared != version)
            {
                version = shared;
                continue;
            }
            throw McpException.ServerError(reply.Code, reply.Message);
        }

        session.Process.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", deadline);
        return Listing(
            CapabilityNames(initialize),
            initialize.ValueAt("result", "serverInfo", "name")?.StringValue,
            initialize.ValueAt("result", "serverInfo", "version")?.StringValue,
            initialize.ValueAt("result", "protocolVersion")?.StringValue,
            modern: false,
            session,
            deadline);
    }

    private static ToolListing ModernListing(JsonDocument discover, Session session, DateTimeOffset deadline, TimeSpan timeout)
    {
        var serverInfo = discover.ValueAt("result", "_meta", "io.modelcontextprotocol/serverInfo");
        return Listing(
            CapabilityNames(discover),
            serverInfo?["name"]?.StringValue,
            serverInfo?["version"]?.StringValue,
            ModernProtocolVersion,
            modern: true,
            session,
            deadline);
    }

    private static string[] CapabilityNames(JsonDocument document) =>
        document.ValueAt("result", "capabilities")?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [];

    // MARK: - Messages

    private static object ClientInfo => new { name = "Kytto", version = "1.0" };

    /// <summary>A JSON-RPC request.</summary>
    /// <remarks>
    /// Modern requests carry the version, client identity and capabilities in
    /// <c>_meta</c>, since there is no handshake to hold them.
    /// </remarks>
    internal static string Request(int id, string method, IReadOnlyDictionary<string, object?>? parameters, bool modern)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters ?? new Dictionary<string, object?>()) body[key] = value;
        if (modern) body["_meta"] = Meta;
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = body });
    }

    internal static Dictionary<string, object?> Meta => new(StringComparer.Ordinal)
    {
        ["io.modelcontextprotocol/protocolVersion"] = ModernProtocolVersion,
        ["io.modelcontextprotocol/clientInfo"] = ClientInfo,
        ["io.modelcontextprotocol/clientCapabilities"] = new Dictionary<string, object?>(),
    };

    private readonly record struct Reply(JsonDocument? Result, int Code, string Message, IReadOnlyList<string> Supported);

    /// <summary>Reads until the response with this id arrives.</summary>
    /// <remarks>
    /// Skips anything else on the way: notifications, plain text, and requests the
    /// server makes of Kytto. Servers are not supposed to print to stdout, but enough
    /// of them log a banner there that refusing to tolerate it would fail perfectly
    /// working servers. A server <em>request</em> (<c>ping</c>, <c>roots/list</c>)
    /// gets a method-not-found answer, so a server that waits for one is not left
    /// hanging until the timeout, and its method is noted for Doctor.
    /// </remarks>
    private static Reply Read(int id, Session session, DateTimeOffset deadline)
    {
        while (true)
        {
            if (session.Process.ReadLine(deadline) is not { } line)
            {
                throw session.Process.HasExited
                    ? McpException.Exited(session.Process.ExitCode)
                    : McpException.ClosedBeforeAnswering();
            }

            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{')) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonParseException)
            {
                continue;
            }

            var root = document.Root;
            if (root["method"]?.StringValue is { } method)
            {
                if (root["jsonrpc"]?.StringValue != "2.0") continue;
                session.Observe(method);
                if (root.Member("id") is { Value.IsNull: false } requestId)
                {
                    var echoed = document.Slice(requestId.Value.Span);
                    // A server that stopped reading cannot hold this write past the
                    // check's deadline (AUD-04); one that closed its input is still
                    // read to the end.
                    try
                    {
                        session.Process.WriteLine(
                            "{\"jsonrpc\":\"2.0\",\"id\":" + echoed +
                            ",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}",
                            deadline);
                    }
                    catch (Exception closed) when (IsClosedPipe(closed))
                    {
                        // The read that follows reports a server that went away.
                    }
                }
                continue;
            }

            if (root["id"]?.NumberValue is not { } responseId) continue;
            if (responseId == DiscoverId && id != DiscoverId &&
                root["jsonrpc"]?.StringValue == "2.0" && root["result"] is not null)
            {
                session.LateDiscovery = document;
            }
            if (responseId != id) continue;
            if (root["jsonrpc"]?.StringValue != "2.0")
            {
                throw McpException.Protocol($"response {id} did not declare JSON-RPC 2.0");
            }

            if (root["error"] is { } error)
            {
                return new Reply(
                    null,
                    McpHttpClient.ErrorCode(error, McpException.Protocol),
                    error["message"]?.StringValue ?? "no message",
                    error.ValueAt("data", "supported")?.Elements?
                        .Select(element => element.StringValue).OfType<string>().ToArray() ?? []);
            }
            if (root["result"] is null)
            {
                throw McpException.Protocol($"response {id} had neither result nor error");
            }
            return new Reply(document, 0, "", []);
        }
    }

    private static JsonDocument ReadResponse(int id, Session session, DateTimeOffset deadline)
    {
        var reply = Read(id, session, deadline);
        return reply.Result ?? throw McpException.ServerError(reply.Code, reply.Message);
    }

    private static bool IsClosedPipe(Exception error) =>
        error is IOException or ObjectDisposedException or InvalidOperationException;

    // MARK: - Listing

    private static ToolListing Listing(
        IReadOnlyList<string> capabilityNames,
        string? serverName,
        string? serverVersion,
        string? negotiated,
        bool modern,
        Session session,
        DateTimeOffset deadline)
    {
        var notes = new List<string>();
        IReadOnlyList<JsonDocument> pages = [];
        try
        {
            session.Process.WriteLine(Request(2, "tools/list", null, modern), deadline);
            var first = ReadResponse(2, session, deadline);
            pages = MorePages(first, modern, session, deadline, notes);
        }
        catch (McpException refused) when (
            refused.Failure is { Reason: "serverRefused", Code: MethodNotFound } &&
            !capabilityNames.Contains("tools", StringComparer.Ordinal))
        {
            // A prompts- or resources-only server: it never claimed to have tools, so
            // declining to list them is not a failure.
            notes.Add("The server does not offer tools.");
        }

        if (pages.Count > 0 && pages[0].ValueAt("result", "tools")?.Elements is null)
        {
            throw McpException.Protocol("tools/list did not return a tools array");
        }

        var promptCount = capabilityNames.Contains("prompts", StringComparer.Ordinal)
            ? ReadOptionalCount("prompts", 3, modern, session, deadline, notes)
            : null;
        var resourceCount = capabilityNames.Contains("resources", StringComparer.Ordinal)
            ? ReadOptionalCount("resources", 4, modern, session, deadline, notes)
            : null;

        return MakeListing(pages, capabilityNames, serverName, serverVersion, negotiated, promptCount, resourceCount, notes, session);
    }

    /// <summary>Pages <c>tools/list</c> until the server stops returning a cursor.</summary>
    /// <remarks>
    /// Unlike prompts and resources, the tool list is what everything else is
    /// measured on — token weight, the safety scan, Contract Guard — so stopping at
    /// the first page would make all of them blind to the rest. A page that cannot be
    /// fetched ends the walk with a note rather than failing a server that did answer.
    /// </remarks>
    private static IReadOnlyList<JsonDocument> MorePages(
        JsonDocument first,
        bool modern,
        Session session,
        DateTimeOffset deadline,
        ICollection<string> notes)
    {
        var pages = new List<JsonDocument> { first };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pages[^1].ValueAt("result", "nextCursor")?.StringValue is { } cursor)
        {
            if (!seen.Add(cursor) || pages.Count >= MaxToolPages)
            {
                notes.Add($"Tool count shows the first {pages.Count} pages; the server kept offering more.");
                break;
            }
            var id = 100 + pages.Count;
            try
            {
                session.Process.WriteLine(Request(id, "tools/list", new Dictionary<string, object?> { ["cursor"] = cursor }, modern), deadline);
                var page = ReadResponse(id, session, deadline);
                if (page.ValueAt("result", "tools")?.Elements is null)
                {
                    notes.Add(
                        $"tools/list page {pages.Count + 1} did not return a tools array; " +
                        $"counts cover the first {pages.Count}.");
                    break;
                }
                pages.Add(page);
            }
            catch (Exception error) when (error is McpException or ProcessException || IsClosedPipe(error))
            {
                notes.Add($"Tool count shows the first {pages.Count} pages; the next could not be read.");
                break;
            }
        }
        return pages;
    }

    private const int MaxToolPages = 50;

    private static int? ReadOptionalCount(
        string kind,
        int id,
        bool modern,
        Session session,
        DateTimeOffset deadline,
        ICollection<string> notes)
    {
        try
        {
            session.Process.WriteLine(Request(id, $"{kind}/list", null, modern), deadline);
            var response = ReadResponse(id, session, deadline);
            var result = response.ValueAt("result");
            var count = result?[kind]?.Elements?.Count;
            if (count is null)
            {
                notes.Add($"{kind}/list did not return a {kind} array.");
                return null;
            }
            if (result?["nextCursor"] is { IsNull: false })
            {
                notes.Add($"{kind}/list has more pages; this count is for the first page.");
            }
            return count;
        }
        catch (Exception error) when (error is McpException or ProcessException || IsClosedPipe(error))
        {
            // Optional inspector data must not turn a successful tools handshake
            // into a failed health check.
            notes.Add($"{kind}/list: {error.Message}");
            return null;
        }
    }

    private static ToolListing MakeListing(
        IReadOnlyList<JsonDocument> pages,
        IReadOnlyList<string> capabilityNames,
        string? serverName,
        string? serverVersion,
        string? negotiated,
        int? promptCount,
        int? resourceCount,
        IReadOnlyList<string> notes,
        Session session)
    {
        var entries = pages
            .SelectMany(page => (page.ValueAt("result", "tools")?.Elements ?? [])
                .Select(tool => (Page: page, Tool: tool)))
            .ToArray();

        // Measured on each tool's verbatim entry rather than on a re-serialization
        // of Kytto's model, for the same reason `RawToolsJson` is kept: the cost is
        // what the client hands the model, whitespace and key order included. The
        // per-tool numbers therefore sum to slightly less than the whole-array
        // weight — the brackets and separators between them belong to no single
        // tool.
        var tokenizer = BpeTokenizer.Cl100kBase;
        var summaries = entries
            .Select(entry =>
            {
                // Last-wins, as the client that hands these to the model reads them.
                var tool = entry.Tool;
                return new ToolSummary(
                    tool.LastValue("name")?.StringValue ?? "(unnamed)",
                    tool.LastValue("description")?.StringValue,
                    tool.LastValue("inputSchema") is { } schema ? entry.Page.Slice(schema.Span) : "{}",
                    ToolAnnotations.From(tool.LastValue("annotations")),
                    tokenizer?.CountTokens(entry.Page.Slice(tool.Span)));
            })
            .ToArray();

        var rawToolsJson = pages.Count switch
        {
            // A server with no tools to list hands the model nothing.
            0 => "[]",
            // One page: the exact bytes.
            1 => pages[0].Slice(pages[0].ValueAt("result", "tools")!.Span),
            // Several: the entries as sent, in one array, which is what a client
            // that pages hands the model.
            _ => "[" + string.Join(",", entries.Select(entry => entry.Page.Slice(entry.Tool.Span))) + "]",
        };

        return new ToolListing(
            Tools: summaries,
            RawToolsJson: rawToolsJson,
            ServerName: serverName,
            ServerVersion: serverVersion,
            NegotiatedProtocolVersion: negotiated,
            CapabilityNames: capabilityNames,
            PromptCount: promptCount,
            ResourceCount: resourceCount,
            InspectionNotes: notes,
            ResolvedCommand: session.Process.ResolvedExecutable)
        {
            DeprecatedFeatures = DeprecatedFeature.Detected(capabilityNames, session.ObservedMethods),
        };
    }

    /// <summary>One spawned server and what it asked of Kytto along the way.</summary>
    private sealed class Session(ManagedProcess process)
    {
        private readonly HashSet<string> _observed = new(StringComparer.Ordinal);

        public ManagedProcess Process { get; } = process;

        public IReadOnlyCollection<string> ObservedMethods => _observed;

        /// <summary>A discovery result that arrived after the probe had given up on it.</summary>
        public JsonDocument? LateDiscovery { get; set; }

        public void Observe(string method)
        {
            if (_observed.Count < 32) _observed.Add(method);
        }
    }
}

/// <summary>Server definitions that answered the handshake rather than <c>server/discover</c>.</summary>
/// <remarks>
/// The spec lets a client remember a server's era per configuration. Remembering it
/// saves a handshake-era server that ignores unknown requests the probe's wait on
/// every later check. In memory only: a relaunch probes again.
/// </remarks>
internal static class HandshakeMemory
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal);

    public static string Key(string command, IReadOnlyList<string> arguments, string? workingDirectory) =>
        string.Join('\0', new[] { command, workingDirectory ?? "" }.Concat(arguments));

    public static bool Contains(string key)
    {
        lock (Gate) return Keys.Contains(key);
    }

    public static void Add(string key)
    {
        lock (Gate) Keys.Add(key);
    }

    public static void Remove(string key)
    {
        lock (Gate) Keys.Remove(key);
    }
}

/// <summary>
/// Features 2026-07-28 deprecates that a check saw a server use, so Doctor can mention
/// them before a client drops them.
/// </summary>
public static class DeprecatedFeature
{
    public const string Logging = "logging";
    public const string Roots = "roots";
    public const string Sampling = "sampling";

    public static IReadOnlyList<string> Detected(
        IReadOnlyCollection<string> capabilityNames,
        IReadOnlyCollection<string> serverMethods)
    {
        var found = new List<string>();
        if (capabilityNames.Contains(Logging, StringComparer.Ordinal) ||
            serverMethods.Contains("notifications/message", StringComparer.Ordinal))
        {
            found.Add(Logging);
        }
        if (serverMethods.Contains("roots/list", StringComparer.Ordinal)) found.Add(Roots);
        if (serverMethods.Contains("sampling/createMessage", StringComparer.Ordinal)) found.Add(Sampling);
        return found;
    }
}
