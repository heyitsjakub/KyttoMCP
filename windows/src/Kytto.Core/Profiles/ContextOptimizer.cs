using Kytto.Core.Doctor;
using Kytto.Core.Model;

namespace Kytto.Core.Profiles;

public enum ContextRecommendationKind { Measure, OverBudget, HeavyServer, DuplicateTool }

public sealed record ContextRecommendation(
    ContextRecommendationKind Kind,
    DoctorSeverity Severity,
    string Summary,
    IReadOnlyList<string> ServerIDs);

public sealed record ContextProfileAnalysis(
    int EstimatedTokens,
    int MeasuredServerCount,
    int TotalServerCount,
    IReadOnlyList<ContextRecommendation> Recommendations);

/// <summary>Advises on profile context cost without editing membership (§7.9).</summary>
public static class ContextOptimizer
{
    public static ContextProfileAnalysis Analyze(Profile profile, IReadOnlyList<Server> servers)
    {
        var members = servers.Where(server => profile.ServerIDs.Contains(server.Id, StringComparer.Ordinal)).ToArray();
        var measured = members.Where(server => server.TokenWeight is not null).ToArray();
        var total = measured.Sum(server => server.TokenWeight?.Estimate ?? 0);
        var recommendations = new List<ContextRecommendation>();

        var unmeasured = members.Where(server => server.TokenWeight is null)
            .Select(server => server.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unmeasured.Length > 0)
        {
            recommendations.Add(new ContextRecommendation(
                ContextRecommendationKind.Measure,
                DoctorSeverity.Info,
                $"Run health checks for {unmeasured.Length} unmeasured server{(unmeasured.Length == 1 ? "" : "s")} before trusting the total.",
                unmeasured));
        }

        if (profile.TokenBudget is { } budget && total > budget)
        {
            var remaining = total;
            var candidates = new List<string>();
            foreach (var server in measured.OrderByDescending(item => item.TokenWeight?.Estimate ?? 0))
            {
                if (remaining <= budget) break;
                candidates.Add(server.Id);
                remaining -= server.TokenWeight?.Estimate ?? 0;
            }
            recommendations.Add(new ContextRecommendation(
                ContextRecommendationKind.OverBudget,
                DoctorSeverity.Warning,
                $"Profile is ~{total - budget} tokens over its {budget}-token budget. Review the suggested heavy servers.",
                candidates));
        }

        var heaviest = measured.MaxBy(server => server.TokenWeight?.Estimate ?? 0);
        if (heaviest?.TokenWeight is { Estimate: >= 20_000 } weight)
        {
            recommendations.Add(new ContextRecommendation(
                ContextRecommendationKind.HeavyServer,
                DoctorSeverity.Warning,
                $"“{heaviest.Name}” alone contributes ~{weight.Estimate} tokens.",
                [heaviest.Id]));
        }

        var owners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var server in members)
        {
            foreach (var tool in server.Health?.Tools ?? [])
            {
                if (!owners.TryGetValue(tool.Name, out var serverIDs))
                {
                    serverIDs = new HashSet<string>(StringComparer.Ordinal);
                    owners[tool.Name] = serverIDs;
                }
                serverIDs.Add(server.Id);
            }
        }
        foreach (var (tool, serverIDs) in owners.Where(pair => pair.Value.Count > 1).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            recommendations.Add(new ContextRecommendation(
                ContextRecommendationKind.DuplicateTool,
                DoctorSeverity.Warning,
                $"Tool “{tool}” is exposed by more than one server, which can confuse tool selection.",
                serverIDs.Order(StringComparer.Ordinal).ToArray()));
        }

        return new ContextProfileAnalysis(total, measured.Length, members.Length, recommendations);
    }
}
