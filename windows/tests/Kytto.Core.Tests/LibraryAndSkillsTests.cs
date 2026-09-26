using System.Text;
using Kytto.Core.Library;
using Kytto.Core.Skills;

namespace Kytto.Core.Tests;

public sealed class LibraryAndSkillsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-1.0.5-scan-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LibraryScanReadsSupportedMapsAndPackageSuggestionsWithoutChangingFiles()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "servers.json");
        var package = Path.Combine(_root, "package.json");
        var skipped = Path.Combine(_root, "node_modules", "ignored.json");
        Directory.CreateDirectory(Path.GetDirectoryName(skipped)!);
        File.WriteAllText(config, "{\"mcpServers\":{\"demo\":{\"command\":\"node\",\"args\":[\"server.js\"],\"env\":{\"API_KEY\":\"secret-stays-native\"}}}}", Encoding.UTF8);
        File.WriteAllText(package, "{\"name\":\"demo-package\",\"bin\":{\"demo\":\"cli.js\"}}", Encoding.UTF8);
        File.WriteAllText(skipped, "{\"mcpServers\":{\"ignored\":{\"command\":\"node\"}}}", Encoding.UTF8);
        var before = File.ReadAllBytes(config);

        var result = new McpLibraryScanner().Scan(_root);

        Assert.Contains(result.Candidates, candidate => candidate.Name == "demo" &&
            candidate.EnvironmentKeys.SequenceEqual(["API_KEY"]));
        Assert.True(
            result.Candidates.Any(candidate => candidate.Name == "demo-package"),
            $"Candidates: {string.Join(", ", result.Candidates.Select(candidate => candidate.Name))}; warnings: {string.Join(" | ", result.Warnings)}");
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Name == "ignored");
        Assert.Contains(result.Candidates.SelectMany(candidate => candidate.Warnings), warning =>
            warning.Contains("Read-only", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(config));
    }

    [Fact]
    public void LibraryScanRejectsAmbiguousTopLevelMapsAndHonoursBounds()
    {
        Directory.CreateDirectory(_root);
        var ambiguous = Path.Combine(_root, "ambiguous.json");
        File.WriteAllText(ambiguous, "{\"mcpServers\":{},\"servers\":{}}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_root, "second.json"), "{}", Encoding.UTF8);

        var result = new McpLibraryScanner().Scan(_root, maxDepth: 0, maxEntries: 1);

        Assert.Empty(result.Candidates);
        Assert.False(result.IsComplete);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("stopped after 1 entries", StringComparison.Ordinal));
    }

    [Fact]
    public void SkillsInventoryReturnsMetadataOnlyGlobalAndWorkspaceEntriesAndFlagsConflicts()
    {
        var workspace = Path.Combine(_root, "workspace");
        var globalSkill = Path.Combine(_root, ".codex", "skills", "global-demo", "SKILL.md");
        var workspaceSkill = Path.Combine(workspace, ".github", "skills", "local-demo", "skill.md");
        Directory.CreateDirectory(Path.GetDirectoryName(globalSkill)!);
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceSkill)!);
        File.WriteAllText(globalSkill, "---\nname: Shared Demo\ndescription: global copy\n---\nbody", Encoding.UTF8);
        File.WriteAllText(workspaceSkill, "---\nname: shared demo\ndescription: workspace copy\n---\nother body", Encoding.UTF8);

        var result = new SkillsInventory().Scan(_root, workspace);

        Assert.Equal(2, result.Skills.Count);
        Assert.All(result.Skills, skill =>
        {
            Assert.Equal("conflict", skill.Status);
            Assert.True(skill.IsDuplicate);
            Assert.True(skill.IsConflict);
            Assert.Equal("Shared Demo", skill.Name, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("global copy", skill.LocationDisplay, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace copy", skill.LocationDisplay, StringComparison.Ordinal);
        });
        Assert.Contains(result.Skills, skill => skill.Scope == "global" && skill.Agent == "codex");
        Assert.Contains(result.Skills, skill => skill.Scope == "workspace" && skill.Agent == "github");
    }

    [Fact]
    public void OversizedSkillsStayVisibleAsUnreadableAndAreNotLoaded()
    {
        var path = Path.Combine(_root, ".claude", "skills", "large", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', SkillsInventory.MaxSkillBytes + 1), Encoding.UTF8);

        var result = new SkillsInventory().Scan(_root);
        var skill = Assert.Single(result.Skills);

        Assert.Equal("unreadable", skill.Status);
        Assert.Equal("unreadable", skill.MetadataStatus);
        Assert.Contains(skill.Warnings, warning => warning.Contains("read limit", StringComparison.Ordinal));
    }

    [Fact]
    public void SkillsFrontMatterHandlesBlocksContinuationsBomAndNestedMappings()
    {
        WriteSkill("folded", """
            ---
            name: Folded
            description: >-
              First part of the sentence,
              followed by the second part.
            ---
            """);
        WriteSkill("literal", """
            ---
            name: Literal
            description: |
              First line.
              Second line.
            ---
            """);
        WriteSkill("continuation", """
            ---
            name: Continuation
            description: Start here.
              Usage: run it with --all.
            ---
            """);
        WriteSkill("bom", "\uFEFF\n\n---\nname: BOM\ndescription: Still parsed.\n---\n");
        WriteSkill("nested-folder", """
            ---
            metadata:
              name: Leaked name
              description: Leaked description
            description: Top-level description.
            ---
            """);

        var result = new SkillsInventory().Scan(_root);

        var folded = Assert.Single(result.Skills, skill => skill.Name == "Folded");
        Assert.Equal("First part of the sentence, followed by the second part.", folded.Description);
        Assert.Equal("complete", folded.MetadataStatus);
        Assert.Equal("First line.\nSecond line.",
            Assert.Single(result.Skills, skill => skill.Name == "Literal").Description);
        Assert.Equal("Start here. Usage: run it with --all.",
            Assert.Single(result.Skills, skill => skill.Name == "Continuation").Description);
        Assert.Equal("Still parsed.",
            Assert.Single(result.Skills, skill => skill.Name == "BOM").Description);

        var nested = Assert.Single(result.Skills, skill => skill.Name == "nested-folder");
        Assert.Equal("Top-level description.", nested.Description);
        Assert.Equal("missingName", nested.MetadataStatus);
        Assert.DoesNotContain("Leaked", nested.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("Leaked", nested.Description, StringComparison.Ordinal);

        void WriteSkill(string directory, string contents)
        {
            var path = Path.Combine(_root, ".codex", "skills", directory, "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup is best effort; no user data is in this temp root.
        }
    }
}
