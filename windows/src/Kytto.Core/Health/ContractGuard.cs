namespace Kytto.Core.Health;

public enum ContractChangeKind
{
    ToolAdded,
    ToolRemoved,
    DescriptionChanged,
    InputSchemaChanged,
    AnnotationsChanged,
}

public enum ContractChangeSeverity { Info, Warning, Breaking }

public sealed record ContractChange(
    ContractChangeKind Kind,
    ContractChangeSeverity Severity,
    string ToolName,
    string Summary);

/// <summary>Compares model-visible MCP behavior between successful checks (§7.10).</summary>
public static class ContractGuard
{
    public static IReadOnlyList<ContractChange> Changes(
        IReadOnlyList<ToolSummary> oldTools,
        IReadOnlyList<ToolSummary> newTools)
    {
        var before = ByName(oldTools);
        var after = ByName(newTools);
        var beforeNames = before.Keys.ToHashSet(StringComparer.Ordinal);
        var afterNames = after.Keys.ToHashSet(StringComparer.Ordinal);
        var changes = new List<ContractChange>();

        foreach (var name in beforeNames.Except(afterNames).Order(StringComparer.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.ToolRemoved,
                ContractChangeSeverity.Breaking,
                name,
                "Tool removed"));
        }
        foreach (var name in afterNames.Except(beforeNames).Order(StringComparer.Ordinal))
        {
            changes.Add(new ContractChange(
                ContractChangeKind.ToolAdded,
                ContractChangeSeverity.Info,
                name,
                "New tool added"));
        }
        foreach (var name in beforeNames.Intersect(afterNames).Order(StringComparer.Ordinal))
        {
            var left = before[name];
            var right = after[name];
            if (left.Description != right.Description)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.DescriptionChanged,
                    ContractChangeSeverity.Warning,
                    name,
                    "Model-facing description changed"));
            }
            if (left.InputSchemaJSON != right.InputSchemaJSON)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.InputSchemaChanged,
                    ContractChangeSeverity.Breaking,
                    name,
                    "Input schema changed"));
            }
            if (left.Annotations != right.Annotations)
            {
                changes.Add(new ContractChange(
                    ContractChangeKind.AnnotationsChanged,
                    ContractChangeSeverity.Warning,
                    name,
                    "Safety annotations changed"));
            }
        }
        return changes;
    }

    private static Dictionary<string, ToolSummary> ByName(IEnumerable<ToolSummary> tools)
    {
        var result = new Dictionary<string, ToolSummary>(StringComparer.Ordinal);
        foreach (var tool in tools) result[tool.Name] = tool;
        return result;
    }
}
