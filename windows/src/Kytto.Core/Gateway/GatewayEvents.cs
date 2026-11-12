using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kytto.Core.Gateway;

public enum GatewayDirection { ClientToServer, ServerToClient }

public enum GatewayEventKind
{
    [JsonStringEnumMemberName("session.started")] SessionStarted,
    [JsonStringEnumMemberName("tool.call.started")] ToolCallStarted,
    [JsonStringEnumMemberName("tool.call.completed")] ToolCallCompleted,
    [JsonStringEnumMemberName("session.ended")] SessionEnded,
    /// <summary>Kytto answered a <c>tools/call</c> itself because the route hides that tool.</summary>
    [JsonStringEnumMemberName("tool.call.refused")] ToolCallRefused,
    /// <summary>The helper was started by a client but could not start the server.</summary>
    [JsonStringEnumMemberName("session.failed")] SessionFailed,
}

/// <summary>Why the helper could not start a session.</summary>
/// <remarks>
/// A fixed vocabulary rather than the error text, which can name a path or a variable.
/// </remarks>
public static class GatewayFailureReason
{
    public const string SecretUnavailable = "secret-unavailable";
    public const string LaunchFailed = "launch-failed";
}

public sealed record GatewayEvent(
    string EventID,
    string SessionID,
    DateTimeOffset Timestamp,
    GatewayEventKind Kind,
    string ServerID,
    string ClientID,
    string? ToolName = null,
    string? RequestID = null,
    int? DurationMilliseconds = null,
    bool? Succeeded = null,
    int? ErrorCode = null,
    int? ExitCode = null,
    /// <summary>
    /// On <c>session.ended</c>: true when the server stopped while its client was still
    /// connected — a crash or an exit nobody asked for. Null in journals written before
    /// this was recorded.
    /// </summary>
    bool? Unexpected = null,
    /// <summary>On <c>session.failed</c>: a <see cref="GatewayFailureReason"/> value.</summary>
    string? Reason = null);

public interface IGatewayEventSink { void Record(GatewayEvent item); }

public sealed class NullGatewayEventSink : IGatewayEventSink
{
    public void Record(GatewayEvent item) { }
}

/// <summary>Cross-process, append-only JSON Lines event storage.</summary>
public sealed class JsonLinesGatewayEventSink : IGatewayEventSink, IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Past this size the journal is moved to <c>&lt;name&gt;.1</c> (replacing the one
    /// before) and a new one is started, so it never grows without bound.
    /// </summary>
    internal const long RotationBytes = 5 * 1024 * 1024;

    private readonly string _path;
    private readonly Mutex _mutex;

    /// <remarks>
    /// The file is opened for each event and closed after it, under a mutex every
    /// helper sharing the journal takes: no helper holds it open, so whichever notices
    /// the size first can rename it while other sessions keep running. Opened once
    /// here, so a journal that cannot be written fails now — the helper then runs
    /// unlogged — rather than on every event.
    /// </remarks>
    public JsonLinesGatewayEventSink(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using (Open()) { }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path))).ToLowerInvariant();
        _mutex = new Mutex(false, $"Local\\KyttoGatewayEvents-{hash}");
    }

    private FileStream Open() =>
        new(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    public void Record(GatewayEvent item)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, Options) + "\n");
        var held = false;
        try
        {
            try { held = _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            RotateIfDue();
            using var stream = Open();
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Observability-only logging must never interrupt an MCP session.
        }
        finally
        {
            if (held) _mutex.ReleaseMutex();
        }
    }

    private void RotateIfDue()
    {
        try
        {
            if (new FileInfo(_path) is { Exists: true, Length: > RotationBytes })
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A reader holding the file without delete sharing; the next event tries again.
        }
    }

    public void Dispose() => _mutex.Dispose();
}

/// <summary>
/// Observes newline-delimited JSON-RPC while the helper relays the original bytes.
/// Payload fields are inspected in memory and never persisted (§3.3).
/// </summary>
public sealed class GatewayMessageObserver
{
    private sealed record PendingCall(string? ToolName, long StartedAt);

    private readonly string _sessionId;
    private readonly string _serverId;
    private readonly string _clientId;
    private readonly IGatewayEventSink _sink;
    private readonly Lock _lock = new();
    private readonly List<byte> _clientBuffer = [];
    private readonly List<byte> _serverBuffer = [];
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private bool _finished;

    public GatewayMessageObserver(
        string serverId,
        string clientId,
        IGatewayEventSink sink,
        string? sessionId = null)
    {
        _sessionId = sessionId ?? Guid.NewGuid().ToString("D").ToLowerInvariant();
        _serverId = serverId;
        _clientId = clientId;
        _sink = sink;
    }

    public void Start() => _sink.Record(Event(GatewayEventKind.SessionStarted));

    public void Accept(ReadOnlySpan<byte> data, GatewayDirection direction)
    {
        lock (_lock)
        {
            var buffer = direction == GatewayDirection.ClientToServer ? _clientBuffer : _serverBuffer;
            foreach (var value in data) buffer.Add(value);
            ConsumeLines(buffer, direction);
        }
    }

    /// <summary>A call Kytto refused on a narrowed route.</summary>
    /// <remarks>It never reached the server, so there is no completion to wait for.</remarks>
    public void RecordRefusal(string toolName, string? requestId) =>
        _sink.Record(Event(GatewayEventKind.ToolCallRefused, toolName, requestId, succeeded: false));

    /// <summary>The helper could not start the server at all.</summary>
    public void Fail(string reason)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
        }
        _sink.Record(Event(GatewayEventKind.SessionFailed, reason: reason));
    }

    public void Finish(int exitCode, bool? unexpected = null)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            _pending.Clear();
        }
        _sink.Record(Event(GatewayEventKind.SessionEnded, exitCode: exitCode, unexpected: unexpected));
    }

    private void ConsumeLines(List<byte> buffer, GatewayDirection direction)
    {
        while (buffer.IndexOf((byte)'\n') is var newline && newline >= 0)
        {
            var line = buffer.GetRange(0, newline).ToArray();
            buffer.RemoveRange(0, newline + 1);
            Inspect(line, direction);
        }
    }

    private void Inspect(byte[] line, GatewayDirection direction)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (document)
        {
            var root = document.RootElement;
            // Every shape is checked before it is read: an odd message must not throw
            // out of the relay that carries the whole session.
            if (root.ValueKind != JsonValueKind.Object) return;
            if (direction == GatewayDirection.ClientToServer &&
                StringAt(root, "method") == "tools/call")
            {
                var requestId = RequestID(root);
                var toolName = root.TryGetProperty("params", out var parameters)
                    ? StringAt(parameters, "name")
                    : null;
                if (requestId is not null)
                {
                    _pending[requestId] = new PendingCall(toolName, Stopwatch.GetTimestamp());
                }
                _sink.Record(Event(GatewayEventKind.ToolCallStarted, toolName, requestId));
                return;
            }

            if (direction != GatewayDirection.ServerToClient || root.TryGetProperty("method", out _) ||
                RequestID(root) is not { } responseId || !_pending.Remove(responseId, out var pending)) return;

            // A tool that ran and reported failure answers with `result.isError`,
            // not a JSON-RPC error; it did not succeed either.
            var hasError = root.TryGetProperty("error", out var error);
            var reportedError = root.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("isError", out var isError) &&
                isError.ValueKind == JsonValueKind.True;
            var succeeded = !hasError && !reportedError;
            int? errorCode = null;
            if (hasError && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var number))
            {
                errorCode = number;
            }
            var elapsed = Stopwatch.GetElapsedTime(pending.StartedAt);
            _sink.Record(Event(
                GatewayEventKind.ToolCallCompleted,
                pending.ToolName,
                responseId,
                (int)elapsed.TotalMilliseconds,
                succeeded,
                errorCode));
        }
    }

    private GatewayEvent Event(
        GatewayEventKind kind,
        string? toolName = null,
        string? requestId = null,
        int? durationMilliseconds = null,
        bool? succeeded = null,
        int? errorCode = null,
        int? exitCode = null,
        bool? unexpected = null,
        string? reason = null) => new(
            Guid.NewGuid().ToString("D").ToLowerInvariant(),
            _sessionId,
            DateTimeOffset.UtcNow,
            kind,
            _serverId,
            _clientId,
            toolName,
            requestId,
            durationMilliseconds,
            succeeded,
            errorCode,
            exitCode,
            unexpected,
            reason);

    private static string? StringAt(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? RequestID(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }
}
