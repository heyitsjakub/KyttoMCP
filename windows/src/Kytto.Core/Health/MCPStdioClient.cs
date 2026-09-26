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
    string ResolvedCommand);

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
/// Deliberately hand-written rather than pulled in as a dependency: the whole
/// exchange is four messages, and doing it directly keeps full control over the
/// timeout and over capturing stderr, which is the part users actually need when
/// something is broken.
/// </remarks>
public sealed class McpStdioClient
{
    /// <summary>The protocol revision Kytto asks for.</summary>
    /// <remarks>
    /// A server that speaks a different one answers with its own, and for listing
    /// tools that is fine — so the negotiated value is recorded, not enforced.
    /// </remarks>
    public const string PreferredProtocolVersion = "2026-07-28";

    public (ToolListing Listing, string Stderr) ListTools(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout)
    {
        using var process = new ManagedProcess(command, arguments, environment);
        process.SetTimeoutBudget(timeout);
        var deadline = DateTimeOffset.UtcNow + timeout;

        try
        {
            process.WriteLine(InitializeRequest());
            var initialize = ReadResponse(1, process, deadline);

            process.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            process.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            var tools = ReadResponse(2, process, deadline);

            var capabilityNames = initialize.ValueAt("result", "capabilities")?.Keys
                .Order(StringComparer.Ordinal)
                .ToArray() ?? [];
            var notes = new List<string>();
            var promptCount = capabilityNames.Contains("prompts", StringComparer.Ordinal)
                ? ReadOptionalCount("prompts", 3, process, deadline, notes)
                : null;
            var resourceCount = capabilityNames.Contains("resources", StringComparer.Ordinal)
                ? ReadOptionalCount("resources", 4, process, deadline, notes)
                : null;

            return (MakeListing(
                initialize,
                tools,
                capabilityNames,
                promptCount,
                resourceCount,
                notes,
                process.ResolvedExecutable), process.CapturedStderr);
        }
        catch (Exception error)
        {
            // Whatever went wrong, the server's own words explain it better than
            // ours, so they travel with the error.
            throw new HealthFailure(error, process.CapturedStderr, process.ResolvedExecutable);
        }
    }

    private static int? ReadOptionalCount(
        string kind,
        int id,
        ManagedProcess process,
        DateTimeOffset deadline,
        ICollection<string> notes)
    {
        try
        {
            process.WriteLine(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                method = $"{kind}/list",
                @params = new { },
            }));
            var response = ReadResponse(id, process, deadline);
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
        catch (Exception error) when (error is McpException or ProcessException)
        {
            // Optional inspector data must not turn a successful tools handshake
            // into a failed health check.
            notes.Add($"{kind}/list: {error.Message}");
            return null;
        }
    }

    private static string InitializeRequest() => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = PreferredProtocolVersion,
            capabilities = new { },
            clientInfo = new { name = "Kytto", version = "1.0" },
        },
    });

    /// <summary>Reads until the response with this id arrives.</summary>
    /// <remarks>
    /// Skips anything else on the way: notifications, requests the server makes of
    /// us, and plain text. Servers are not supposed to print to stdout, but enough
    /// of them log a banner there that refusing to tolerate it would fail perfectly
    /// working servers.
    /// </remarks>
    private static JsonDocument ReadResponse(int id, ManagedProcess process, DateTimeOffset deadline)
    {
        while (true)
        {
            if (process.ReadLine(deadline) is not { } line)
            {
                throw process.HasExited
                    ? McpException.Exited(process.ExitCode)
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

            if (document.Root["id"]?.NumberValue is not { } responseId) continue;
            if (responseId != id) continue;
            if (document.Root["jsonrpc"]?.StringValue != "2.0")
            {
                throw McpException.Protocol($"response {id} did not declare JSON-RPC 2.0");
            }

            if (document.Root["error"] is { } error)
            {
                throw McpException.ServerError(
                    (int)(error["code"]?.NumberValue ?? 0),
                    error["message"]?.StringValue ?? "no message");
            }
            if (document.Root["result"] is null)
            {
                throw McpException.Protocol($"response {id} had neither result nor error");
            }
            return document;
        }
    }

    private static ToolListing MakeListing(
        JsonDocument initialize,
        JsonDocument tools,
        IReadOnlyList<string> capabilityNames,
        int? promptCount,
        int? resourceCount,
        IReadOnlyList<string> notes,
        string resolvedCommand)
    {
        var node = tools.ValueAt("result", "tools");
        if (node?.Elements is not { } elements)
        {
            throw McpException.Protocol("tools/list did not return a tools array");
        }

        // Measured on each tool's verbatim entry rather than on a re-serialization
        // of Kytto's model, for the same reason `RawToolsJson` is kept: the cost is
        // what the client hands the model, whitespace and key order included. The
        // per-tool numbers therefore sum to slightly less than the whole-array
        // weight — the brackets and separators between them belong to no single
        // tool.
        var tokenizer = BpeTokenizer.Cl100kBase;
        var summaries = elements
            .Select(tool => new ToolSummary(
                tool["name"]?.StringValue ?? "(unnamed)",
                tool["description"]?.StringValue,
                tool["inputSchema"] is { } schema ? tools.Slice(schema.Span) : "{}",
                new ToolAnnotations(
                    tool.ValueAt("annotations", "readOnlyHint")?.BoolValue,
                    tool.ValueAt("annotations", "destructiveHint")?.BoolValue,
                    tool.ValueAt("annotations", "idempotentHint")?.BoolValue,
                    tool.ValueAt("annotations", "openWorldHint")?.BoolValue),
                tokenizer?.CountTokens(tools.Slice(tool.Span))))
            .ToArray();

        return new ToolListing(
            Tools: summaries,
            RawToolsJson: tools.Slice(node.Span),
            ServerName: initialize.ValueAt("result", "serverInfo", "name")?.StringValue,
            ServerVersion: initialize.ValueAt("result", "serverInfo", "version")?.StringValue,
            NegotiatedProtocolVersion: initialize.ValueAt("result", "protocolVersion")?.StringValue,
            CapabilityNames: capabilityNames,
            PromptCount: promptCount,
            ResourceCount: resourceCount,
            InspectionNotes: notes,
            ResolvedCommand: resolvedCommand);
    }
}
