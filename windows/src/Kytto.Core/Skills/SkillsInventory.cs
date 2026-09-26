using System.Security.Cryptography;
using System.Text;

namespace Kytto.Core.Skills;

public sealed record SkillInventoryItem(
    string ID,
    string Name,
    string Description,
    string LocationDisplay,
    string Status,
    IReadOnlyList<string> Warnings,
    string Agent = "",
    string Scope = "",
    string ScopeLabel = "",
    string MetadataStatus = "",
    bool IsDuplicate = false,
    bool IsConflict = false);

public sealed record SkillInventoryResult(
    IReadOnlyList<SkillInventoryItem> Skills,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reads SKILL.md files from direct, known roots only. It never executes or edits
/// a skill and keeps the file content on the native side (§7).
/// </summary>
public sealed class SkillsInventory
{
    public const int MaxSkillBytes = 128 * 1024;
    private static readonly string[] GlobalRoots = [
        ".codex\\skills", ".claude\\skills", ".cursor\\skills", ".agents\\skills",
    ];

    public SkillInventoryResult Scan(string home, string? workspaceRoot = null)
    {
        if (!Path.IsPathFullyQualified(home)) throw new ArgumentException("Home must be fully qualified.", nameof(home));
        var roots = GlobalRoots
            .Select(path =>
            {
                var agent = path.Split('\\')[0].TrimStart('.');
                return new RootSpec(
                    Path.Combine(home, path),
                    agent,
                    "global",
                    ScopeLabel(agent, "global"));
            })
            .ToList();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            if (!Path.IsPathFullyQualified(workspaceRoot))
            {
                throw new ArgumentException("Workspace must be fully qualified.", nameof(workspaceRoot));
            }
            var workspaceLabel = WorkspaceLabel(workspaceRoot);
            roots.AddRange([
                new RootSpec(Path.Combine(workspaceRoot, ".codex", "skills"), "codex", "workspace", ScopeLabel("codex", "workspace", workspaceLabel)),
                new RootSpec(Path.Combine(workspaceRoot, ".claude", "skills"), "claude", "workspace", ScopeLabel("claude", "workspace", workspaceLabel)),
                new RootSpec(Path.Combine(workspaceRoot, ".cursor", "skills"), "cursor", "workspace", ScopeLabel("cursor", "workspace", workspaceLabel)),
                new RootSpec(Path.Combine(workspaceRoot, ".agents", "skills"), "agents", "workspace", ScopeLabel("agents", "workspace", workspaceLabel)),
                new RootSpec(Path.Combine(workspaceRoot, ".github", "skills"), "github", "workspace", ScopeLabel("github", "workspace", workspaceLabel)),
            ]);
        }

        var items = new List<SkillInventoryItem>();
        var warnings = new List<string>();
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots
                     .GroupBy(root => Path.GetFullPath(root.Path), StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            ScanRoot(Path.GetFullPath(root.Path), root.Agent, root.Scope, root.ScopeLabel);
        }

        var byName = items.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byName.Where(group => group.Count() > 1))
        {
            var ids = group.Select(item => item.ID).ToHashSet(StringComparer.Ordinal);
            var fingerprintsForName = group
                .Select(item => fingerprints.GetValueOrDefault(item.ID))
                .Where(fingerprint => fingerprint is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var conflict = fingerprintsForName.Length > 1;
            for (var index = 0; index < items.Count; index++)
            {
                if (!ids.Contains(items[index].ID)) continue;
                items[index] = items[index] with
                {
                    Status = items[index].Status == "unreadable"
                        ? "unreadable"
                        : conflict ? "conflict" : "duplicateName",
                    IsDuplicate = true,
                    IsConflict = conflict,
                    Warnings = items[index].Warnings
                        .Append(conflict
                            ? "Another discovered skill uses the same name with different content."
                            : "Another discovered skill uses the same name (case-insensitive).")
                        .ToArray(),
                };
            }
        }

        var byContent = items.GroupBy(ContentFingerprint, StringComparer.Ordinal);
        foreach (var group in byContent.Where(group => group.Key is not null && group.Count() > 1))
        {
            var ids = group.Select(item => item.ID).ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                if (!ids.Contains(items[index].ID) || items[index].Status == "unreadable") continue;
                items[index] = items[index] with
                {
                    Status = items[index].Status is "conflict" or "unreadable"
                        ? items[index].Status
                        : "duplicateContent",
                    IsDuplicate = true,
                    Warnings = items[index].Warnings
                        .Append("Another discovered skill has identical content.")
                        .ToArray(),
                };
            }
        }

        items.Sort((left, right) =>
        {
            var bySkill = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return bySkill != 0
                ? bySkill
                : string.Compare(left.LocationDisplay, right.LocationDisplay, StringComparison.OrdinalIgnoreCase);
        });
        return new SkillInventoryResult(items, warnings);

        void ScanRoot(string root, string agent, string scope, string scopeLabel)
        {
            if (!Directory.Exists(root)) return;
            DirectoryInfo rootInfo;
            try
            {
                rootInfo = new DirectoryInfo(root);
                if (IsReparse(rootInfo)) return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not inspect skills root {Display(root)}: {error.Message}.");
                return;
            }

            IEnumerable<DirectoryInfo> skillDirectories;
            try
            {
                skillDirectories = rootInfo.EnumerateDirectories()
                    .Where(directory => !IsReparse(directory))
                    .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not inspect skills root {Display(root)}: {error.Message}.");
                return;
            }

            foreach (var directory in skillDirectories)
            {
                FileInfo? file;
                try
                {
                    file = directory.EnumerateFiles()
                        .Where(candidate => !IsReparse(candidate))
                        .FirstOrDefault(candidate => candidate.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not inspect skill directory {Display(directory.FullName)}: {error.Message}.");
                    continue;
                }
                if (file is null) continue;
                AddSkill(file, agent, scope, scopeLabel);
            }
        }

        void AddSkill(FileInfo file, string agent, string scope, string scopeLabel)
        {
            var id = $"skill.{Guid.NewGuid():D}";
            string text;
            var warningsForSkill = new List<string>();
            var location = DisplaySkill(file.FullName, scope, scopeLabel);
            try
            {
                if (file.Length > MaxSkillBytes)
                {
                    items.Add(new SkillInventoryItem(
                        id,
                        file.Directory?.Name ?? "Unreadable skill",
                        "",
                        location,
                        "unreadable",
                        [$"SKILL.md is larger than the {MaxSkillBytes / 1024} KiB read limit."],
                        agent,
                        scope,
                        scopeLabel,
                        "unreadable"));
                    return;
                }
                text = File.ReadAllText(file.FullName, new UTF8Encoding(false, true));
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                items.Add(new SkillInventoryItem(
                    id,
                    file.Directory?.Name ?? "Unreadable skill",
                    "",
                    location,
                    "unreadable",
                    [$"Could not read SKILL.md: {error.Message}"],
                    agent,
                    scope,
                    scopeLabel,
                    "unreadable"));
                return;
            }

            var metadata = ParseMetadata(text);
            var name = metadata.Name ?? file.Directory?.Name ?? "Unnamed skill";
            var description = metadata.Description ?? "No description provided.";
            if (metadata.Name is null) warningsForSkill.Add("No name metadata was found; using the containing folder name.");
            if (metadata.Description is null) warningsForSkill.Add("No description metadata was found.");
            var metadataStatus = (metadata.Name is null, metadata.Description is null) switch
            {
                (false, false) => "complete",
                (true, true) => "missingNameAndDescription",
                (true, false) => "missingName",
                _ => "missingDescription",
            };
            items.Add(new SkillInventoryItem(
                id,
                name,
                description,
                location,
                "ok",
                warningsForSkill,
                agent,
                scope,
                scopeLabel,
                metadataStatus));
            fingerprints[id] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        string? ContentFingerprint(SkillInventoryItem item) => fingerprints.GetValueOrDefault(item.ID);

        string DisplaySkill(string path, string scope, string scopeLabel)
        {
            var basePath = scope == "global" ? home : workspaceRoot!;
            var relative = Path.GetRelativePath(basePath, path);
            return $"{scopeLabel} · {relative}";
        }

        string Display(string path) => Path.GetFileName(path) is { Length: > 0 } name
            ? name
            : "skills root";
    }

    private sealed record RootSpec(string Path, string Agent, string Scope, string ScopeLabel);

    private static string WorkspaceLabel(string workspaceRoot)
    {
        var trimmed = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : "Workspace";
    }

    private static string ScopeLabel(string agent, string scope, string? workspace = null)
    {
        var displayAgent = agent switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "cursor" => "Cursor",
            "agents" => "Agents",
            "github" => "GitHub",
            _ => agent,
        };
        return string.Join(" · ", new[] { displayAgent, scope, workspace }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static (string? Name, string? Description) ParseMetadata(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var index = 0;
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        if (index >= lines.Length || lines[index] != "---") return (null, null);
        index++;

        string? name = null;
        string? description = null;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line is "---" or "...") break;
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                index++;
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                index++;
                continue;
            }

            var key = line[..separator].Trim().ToLowerInvariant();
            var opening = line[(separator + 1)..].Trim();
            index++;
            var owned = OwnedLines(lines, ref index);

            // An empty value introduces a nested mapping. Its indented name or
            // description belongs to that mapping and must never leak upward.
            if (opening.Length == 0) continue;

            string value;
            if (IsBlockScalar(opening, out var style))
            {
                var normalized = RemoveIndent(owned);
                value = style == '|'
                    ? string.Join('\n', normalized).Trim()
                    : Fold(normalized);
            }
            else
            {
                value = Unquote(opening);
                var continuation = Fold(RemoveIndent(owned));
                if (continuation.Length > 0) value = $"{value} {continuation}";
                value = value.Trim();
            }

            if (value.Length == 0 || value.Any(character => char.IsControl(character) &&
                                                        character is not '\n' and not '\t')) continue;
            if (key == "name") name ??= value;
            if (key == "description") description ??= value;
        }
        return (name, description);
    }

    private static IReadOnlyList<string> OwnedLines(string[] lines, ref int index)
    {
        var owned = new List<string>();
        while (index < lines.Length)
        {
            var line = lines[index];
            if (line.Length == 0)
            {
                owned.Add("");
                index++;
                continue;
            }
            if (!char.IsWhiteSpace(line[0]) || line is "---" or "...") break;
            owned.Add(line);
            index++;
        }
        return owned;
    }

    private static IReadOnlyList<string> RemoveIndent(IReadOnlyList<string> lines)
    {
        var indent = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(LeadingWhitespace)
            .DefaultIfEmpty(0)
            .Min();
        return lines.Select(line => line.Length >= indent ? line[indent..] : "").ToArray();
    }

    private static int LeadingWhitespace(string line)
    {
        var count = 0;
        while (count < line.Length && char.IsWhiteSpace(line[count])) count++;
        return count;
    }

    private static string Fold(IReadOnlyList<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(string.Join(' ', current));
                    current.Clear();
                }
                continue;
            }
            current.Add(line.Trim());
        }
        if (current.Count > 0) paragraphs.Add(string.Join(' ', current));
        return string.Join("\n\n", paragraphs).Trim();
    }

    private static bool IsBlockScalar(string value, out char style)
    {
        style = value.Length > 0 ? value[0] : '\0';
        return style is '|' or '>' &&
            value.Length is >= 1 and <= 3 &&
            value[1..].All(character => character is '+' or '-' or >= '0' and <= '9');
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
        {
            return value[1..^1];
        }
        return value;
    }

    private static bool IsReparse(FileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
}
