using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Settings;

namespace Kytto.Core.Gateway;

public sealed record GatewayActivitySummary(
    IReadOnlyList<GatewayEvent> Events,
    int TotalSessions,
    int CompletedCalls,
    int FailedCalls,
    int? AverageDurationMilliseconds,
    /// <summary>Calls Kytto refused because the route hides the tool.</summary>
    int RefusedCalls = 0,
    /// <summary>
    /// Sessions whose server stopped while the client was still connected, and sessions
    /// the helper could not start.
    /// </summary>
    int ServerFailures = 0)
{
    public static GatewayActivitySummary From(IReadOnlyList<GatewayEvent> events)
    {
        var completed = events.Where(item => item.Kind == GatewayEventKind.ToolCallCompleted).ToArray();
        var durations = completed.Select(item => item.DurationMilliseconds).OfType<int>().ToArray();
        return new GatewayActivitySummary(
            events,
            events.Select(item => item.SessionID).Distinct(StringComparer.Ordinal).Count(),
            completed.Length,
            completed.Count(item => item.Succeeded == false),
            durations.Length == 0 ? null : (int?)(durations.Sum(value => (long)value) / durations.Length),
            events.Count(item => item.Kind == GatewayEventKind.ToolCallRefused),
            events.Count(IsServerFailure));
    }

    /// <summary>A session that ended badly: the server stopped by itself, or never started.</summary>
    public static bool IsServerFailure(GatewayEvent item) =>
        item.Kind == GatewayEventKind.SessionFailed ||
        (item.Kind == GatewayEventKind.SessionEnded && item.Unexpected == true);
}

/// <summary>Read-only view of the append-only Gateway journal (§7.10).</summary>
/// <remarks>A malformed partial tail is ignored because the proxy may be writing.</remarks>
public sealed class GatewayActivityStore(KyttoPaths paths)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How much of the journal's end is read.</summary>
    /// <remarks>
    /// The panel shows the last few hundred events; reading a journal of any size
    /// whole, on every refresh, is how a long-lived install grows a stutter.
    /// </remarks>
    internal const int TailBytes = 2 * 1024 * 1024;

    public GatewayActivitySummary Summary(int limit = 500)
    {
        limit = Math.Max(1, limit);
        var lines = TailLines(paths.GatewayEventsFile);
        // Right after a rotation the new journal is nearly empty. The one it replaced
        // fills the view, so the panel does not go blank at 5 MB.
        if (lines.Count < limit)
        {
            lines = [.. TailLines(paths.GatewayEventsFile + ".1"), .. lines];
        }

        var events = lines
            .TakeLast(limit)
            .Select((line, index) => (Event: Parse(line), Index: index))
            .Where(item => item.Event is not null)
            // Timestamps have whole seconds; within one, the journal's own order keeps
            // "started" before "completed".
            .OrderByDescending(item => item.Event!.Timestamp)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Event!)
            .ToArray();
        return GatewayActivitySummary.From(events);
    }

    /// <summary>The complete lines in the last <see cref="TailBytes"/> of a journal file.</summary>
    private static List<string> TailLines(string path)
    {
        byte[] data;
        long start;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var tail = new MemoryStream();
            stream.CopyTo(tail);
            data = tail.ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // Decoded line by line and lossily: one line cut by a short write must cost
        // that line, not every event in the file.
        var lines = new List<string>();
        var lineStart = 0;
        for (var index = 0; index <= data.Length; index++)
        {
            if (index < data.Length && data[index] != (byte)'\n') continue;
            if (index > lineStart) lines.Add(Encoding.UTF8.GetString(data, lineStart, index - lineStart));
            lineStart = index + 1;
        }
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // began mid-line
        return lines;
    }

    private static GatewayEvent? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<GatewayEvent>(line, Options); }
        catch (JsonException) { return null; }
    }
}
