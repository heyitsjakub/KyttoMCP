using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Settings;

namespace Kytto.Core.Gateway;

public sealed record GatewayActivitySummary(
    IReadOnlyList<GatewayEvent> Events,
    int TotalSessions,
    int CompletedCalls,
    int FailedCalls,
    int? AverageDurationMilliseconds)
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
            durations.Length == 0 ? null : (int?)(durations.Sum(value => (long)value) / durations.Length));
    }
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

    public GatewayActivitySummary Summary(int limit = 500)
    {
        string text;
        try { text = File.ReadAllText(paths.GatewayEventsFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return GatewayActivitySummary.From([]);
        }

        var events = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(Math.Max(1, limit))
            .Select(Parse)
            .OfType<GatewayEvent>()
            .OrderByDescending(item => item.Timestamp)
            .ToArray();
        return GatewayActivitySummary.From(events);
    }

    private static GatewayEvent? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<GatewayEvent>(line, Options); }
        catch (JsonException) { return null; }
    }
}
