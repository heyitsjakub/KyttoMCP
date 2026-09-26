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
    int? ExitCode = null);

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

    private readonly FileStream _stream;
    private readonly Mutex _mutex;

    public JsonLinesGatewayEventSink(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))).ToLowerInvariant();
        _mutex = new Mutex(false, $"Local\\KyttoGatewayEvents-{hash}");
    }

    public void Record(GatewayEvent item)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, Options) + "\n");
        var held = false;
        try
        {
            try { held = _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            _stream.Write(bytes);
            _stream.Flush();
        }
        catch (IOException)
        {
            // Observability-only logging must never interrupt an MCP session.
        }
        finally
        {
            if (held) _mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _mutex.Dispose();
    }
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

    public void Finish(int exitCode)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            _pending.Clear();
        }
        _sink.Record(Event(GatewayEventKind.SessionEnded, exitCode: exitCode));
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
            if (direction == GatewayDirection.ClientToServer &&
                root.TryGetProperty("method", out var method) && method.GetString() == "tools/call")
            {
                var requestId = RequestID(root);
                string? toolName = null;
                if (root.TryGetProperty("params", out var parameters) &&
                    parameters.TryGetProperty("name", out var name)) toolName = name.GetString();
                if (requestId is not null)
                {
                    _pending[requestId] = new PendingCall(toolName, Stopwatch.GetTimestamp());
                }
                _sink.Record(Event(GatewayEventKind.ToolCallStarted, toolName, requestId));
                return;
            }

            if (direction != GatewayDirection.ServerToClient || root.TryGetProperty("method", out _) ||
                RequestID(root) is not { } responseId || !_pending.Remove(responseId, out var pending)) return;

            var succeeded = !root.TryGetProperty("error", out var error);
            int? errorCode = null;
            if (!succeeded && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number))
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
        int? exitCode = null) => new(
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
            exitCode);

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
