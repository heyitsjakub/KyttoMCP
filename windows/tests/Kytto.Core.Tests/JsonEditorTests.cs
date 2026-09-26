using Kytto.Core.Json;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

// docs/design.md §8 on safe writes: "Write tests that assert unrelated keys survive a
// round trip." That is this file. Breaking someone's config is the one mistake
// that gets the app deleted, and every case below is a way to break one.

public class RemoveMemberTests
{
    private static string Remove(string key, string source, params string[] path) =>
        JsonDocument.Parse(source).RemovingMember(key, path);

    [Fact]
    public void FirstOfSeveralTakesItsOwnCommaAndLine() =>
        Assert.Equal(
            "{\n  \"b\": 2\n}",
            Remove("a", "{\n  \"a\": 1,\n  \"b\": 2\n}"));

    [Fact]
    public void LastOfSeveralTakesThePrecedingCommaAndLeavesTheClosingLineAlone() =>
        Assert.Equal(
            "{\n  \"a\": 1\n}",
            Remove("b", "{\n  \"a\": 1,\n  \"b\": 2\n}"));

    [Fact]
    public void MiddleOfSeveral() =>
        Assert.Equal(
            "{\n  \"a\": 1,\n  \"c\": 3\n}",
            Remove("b", "{\n  \"a\": 1,\n  \"b\": 2,\n  \"c\": 3\n}"));

    [Fact]
    public void TheOnlyMemberLeavesAValidEmptyObject()
    {
        var result = Remove("a", "{\n  \"a\": 1\n}");
        Assert.Empty(JsonDocument.Parse(result).Root.Members!);
    }

    [Fact]
    public void SingleLineObjectsStayOnOneLine()
    {
        Assert.Equal("""{"a": 1}""", Remove("b", """{"a": 1, "b": 2}"""));
        Assert.Equal("""{"b": 2}""", Remove("a", """{"a": 1, "b": 2}"""));
    }

    [Fact]
    public void TabIndentedFilesStayTabIndented() =>
        Assert.Equal("{\n\t\"a\": 1\n}", Remove("b", "{\n\t\"a\": 1,\n\t\"b\": 2\n}"));

    [Fact]
    public void CrlfLineEndingsSurvive() =>
        Assert.Equal(
            "{\r\n  \"b\": 2\r\n}",
            Remove("a", "{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}"));

    [Fact]
    public void RemovingANestedMemberLeavesTheParentAlone()
    {
        const string Source = """
            {
              "mcpServers": {
                "github": { "command": "npx" },
                "postgres": { "command": "psql" }
              },
              "theme": "dark"
            }
            """;
        const string Expected = """
            {
              "mcpServers": {
                "postgres": { "command": "psql" }
              },
              "theme": "dark"
            }
            """;
        Assert.Equal(Expected, Remove("github", Source, "mcpServers"));
    }

    [Fact]
    public void AMissingKeyIsNotAnError()
    {
        const string Source = """{"a": 1}""";
        Assert.Equal(Source, Remove("zzz", Source));
    }

    [Fact]
    public void CommentsOnSurroundingMembersAreNotCollateralDamage()
    {
        var result = Remove("b", """
            {
              // keep this
              "a": 1,
              "b": 2,
              // and this
              "c": 3
            }
            """);
        Assert.Contains("// keep this", result, StringComparison.Ordinal);
        Assert.Contains("// and this", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\"b\"", result, StringComparison.Ordinal);
    }
}

public class SetMemberTests
{
    [Fact]
    public void AppendingMatchesTheIndentationOfItsSiblings()
    {
        var document = JsonDocument.Parse("{\n  \"a\": 1\n}");
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": 2\n}", document.SettingMember("b", [], "2"));
    }

    [Fact]
    public void AppendingToASingleLineObjectStaysOnTheLine()
    {
        var document = JsonDocument.Parse("""{"a": 1}""");
        Assert.Equal("""{"a": 1, "b": 2}""", document.SettingMember("b", [], "2"));
    }

    [Fact]
    public void AnExistingKeyHasOnlyItsValueReplaced()
    {
        var document = JsonDocument.Parse("""
            {
              "isEnabled": true,
              "userConfig": { "a": 1 }
            }
            """);
        Assert.Equal("""
            {
              "isEnabled": false,
              "userConfig": { "a": 1 }
            }
            """, document.SettingMember("isEnabled", [], "false"));
    }

    [Fact]
    public void AnEmptyObjectGetsOpenedUpWithTheFilesOwnIndentUnit()
    {
        var document = JsonDocument.Parse("""
            {
              "mcpServers": {},
              "theme": "dark"
            }
            """);
        Assert.Equal("""
            {
              "mcpServers": {
                "github": { "command": "npx" }
              },
              "theme": "dark"
            }
            """, document.SettingMember("github", ["mcpServers"], """{ "command": "npx" }"""));
    }

    /// <summary>Claude Desktop configs routinely have no mcpServers key at all.</summary>
    [Fact]
    public void AMissingKeyIsCreatedOnTheWayDown()
    {
        var document = JsonDocument.Parse("""
            {
              "preferences": { "theme": "dark" }
            }
            """);
        var result = document.SettingMember("github", ["mcpServers"], """{ "command": "npx" }""");
        var reparsed = JsonDocument.Parse(result);

        Assert.Equal("npx", reparsed.ValueAt("mcpServers", "github", "command")?.StringValue);
        Assert.Equal("dark", reparsed.ValueAt("preferences", "theme")?.StringValue);
    }

    [Fact]
    public void TabIndentedFilesGetTabIndentedInsertions()
    {
        var document = JsonDocument.Parse("{\n\t\"a\": 1\n}");
        Assert.Equal("{\n\t\"a\": 1,\n\t\"b\": 2\n}", document.SettingMember("b", [], "2"));
    }

    [Fact]
    public void CrlfFilesGetOnlyCrlfInsertions()
    {
        var source = "{\r\n  \"mcpServers\": {},\r\n  \"theme\": \"dark\"\r\n}";
        var updated = JsonDocument.Parse(source)
            .SettingMember("github", ["mcpServers"], "{ \"command\": \"npx\" }");

        Assert.DoesNotContain("\n", updated.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.Equal(
            "npx",
            JsonDocument.Parse(updated).ValueAt("mcpServers", "github", "command")?.StringValue);
    }
}

public class ArrayEditTests
{
    [Fact]
    public void AppendingToAPopulatedArray()
    {
        var document = JsonDocument.Parse("""
            {
              "deniedMcpServers": [
                { "serverName": "a" }
              ]
            }
            """);
        Assert.Equal("""
            {
              "deniedMcpServers": [
                { "serverName": "a" },
                { "serverName": "b" }
              ]
            }
            """, document.AppendingElement("""{ "serverName": "b" }""", ["deniedMcpServers"]));
    }

    [Fact]
    public void AppendingToAnEmptyArray()
    {
        var document = JsonDocument.Parse("""
            {
              "deniedMcpServers": []
            }
            """);
        Assert.Equal("""
            {
              "deniedMcpServers": [
                { "serverName": "a" }
              ]
            }
            """, document.AppendingElement("""{ "serverName": "a" }""", ["deniedMcpServers"]));
    }

    [Fact]
    public void AppendingCreatesTheArrayWhenTheKeyIsAbsent()
    {
        var document = JsonDocument.Parse("""
            {
              "model": "opus"
            }
            """);
        var result = document.AppendingElement("""{ "serverName": "a" }""", ["deniedMcpServers"]);
        var reparsed = JsonDocument.Parse(result);

        Assert.Equal(1, reparsed.ValueAt("deniedMcpServers")?.Elements?.Count);
        Assert.Equal("opus", reparsed.ValueAt("model")?.StringValue);
    }

    [Fact]
    public void RemovingAMatchingElement()
    {
        var document = JsonDocument.Parse("""
            {
              "deniedMcpServers": [
                { "serverName": "a" },
                { "serverName": "b" }
              ]
            }
            """);
        Assert.Equal("""
            {
              "deniedMcpServers": [
                { "serverName": "b" }
              ]
            }
            """, document.RemovingElements(
                ["deniedMcpServers"],
                node => node["serverName"]?.StringValue == "a"));
    }

    [Fact]
    public void RemovingEveryElementLeavesAValidEmptyArray()
    {
        var document = JsonDocument.Parse("""
            {
              "deniedMcpServers": [
                { "serverName": "a" },
                { "serverName": "b" }
              ]
            }
            """);
        var result = document.RemovingElements(["deniedMcpServers"], _ => true);
        Assert.Empty(JsonDocument.Parse(result).ValueAt("deniedMcpServers")!.Elements!);
    }
}

public class RealConfigEditTests
{
    /// <summary>
    /// The assertion docs/design.md §8 asks for, against the shapes that actually exist on
    /// disk: change one server, and nothing else in the file moves.
    /// </summary>
    [Theory]
    [InlineData(Fixture.ClaudeDesktopWithServers)]
    [InlineData(Fixture.CursorMcp)]
    public void EditingOneServerPreservesEverythingElse(Fixture fixture)
    {
        var original = fixture.Document();
        const string ServersKey = "mcpServers";
        var names = original.ValueAt(ServersKey)?.Keys;
        Assert.NotNull(names);

        foreach (var name in names)
        {
            var edited = original.RemovingMember(name, [ServersKey]);
            var reparsed = JsonDocument.Parse(edited);

            // Every other server is still there, in order.
            Assert.Equal(names.Where(other => other != name), reparsed.ValueAt(ServersKey)?.Keys);

            // Every top-level key the file had, it still has, in the same order.
            Assert.Equal(original.Root.Keys, reparsed.Root.Keys);

            // And the bytes outside the servers map are untouched.
            var mapSpan = original.ValueAt(ServersKey)!.Span;
            Assert.StartsWith(original.SourceText[..mapSpan.Start], edited, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AClaudeDesktopConfigWithNoMcpServersGainsOneCleanly()
    {
        var original = Fixture.ClaudeDesktopNoServers.Document();
        Assert.Null(original.ValueAt("mcpServers"));

        var edited = original.SettingMember(
            "github",
            ["mcpServers"],
            """{ "command": "npx", "args": ["-y", "server-github"] }""");
        var reparsed = JsonDocument.Parse(edited);

        Assert.Equal("npx", reparsed.ValueAt("mcpServers", "github", "command")?.StringValue);
        // Everything the file already had survives, in order.
        Assert.Equal(original.Root.Keys.Append("mcpServers"), reparsed.Root.Keys);
        Assert.True(reparsed.ValueAt("preferences", "chromeExtensionEnabled")?.BoolValue);
    }

    [Fact]
    public void VsCodeJsonCKeepsItsCommentsThroughAnEdit()
    {
        var original = Fixture.VsCodeMcp.Document();
        var edited = original.RemovingMember("playwright", ["servers"]);

        Assert.Contains("// User-level MCP configuration for VS Code.", edited, StringComparison.Ordinal);
        Assert.Contains("/* Servers are keyed by name", edited, StringComparison.Ordinal);
        Assert.Contains("// Inputs are prompted for on first use.", edited, StringComparison.Ordinal);

        var reparsed = JsonDocument.Parse(edited);
        Assert.Equal(["github"], reparsed.ValueAt("servers")?.Keys);
        Assert.NotNull(reparsed.ValueAt("inputs"));
    }

    /// <summary>
    /// Project scopes are never a write target (§4), and an edit must not disturb them.
    /// </summary>
    [Fact]
    public void ALargeClaudeJsonEditTouchesOnlyTheServersMap()
    {
        var original = Fixture.ClaudeCode.Document();
        var edited = original.RemovingMember("context7", ["mcpServers"]);
        var reparsed = JsonDocument.Parse(edited);

        Assert.Equal(["XcodeBuildMCP"], reparsed.ValueAt("mcpServers")?.Keys);
        Assert.Equal(2, reparsed.ValueAt("projects")?.Keys.Count);
        Assert.Equal("node", reparsed.ValueAt(
            "projects",
            "/Users/example/code/Xcode/KyttoMCP",
            "mcpServers",
            "project-only-server",
            "command")?.StringValue);
        Assert.Equal(560, reparsed.ValueAt("numStartups")?.NumberValue);
    }

    /// <summary>
    /// Parking stores the definition's original source text, so putting it back is
    /// byte-for-byte, not a re-serialization.
    /// </summary>
    [Fact]
    public void RoundTripRemoveThenReAddRestoresTheFileExactly()
    {
        var original = Fixture.CursorMcp.Document();
        var member = original.ValueAt("mcpServers")?.Member("figma");
        Assert.NotNull(member);
        var parked = original.Slice(member.Value.Span);

        var removed = original.RemovingMember("figma", ["mcpServers"]);
        var restored = JsonDocument.Parse(removed).SettingMember("figma", ["mcpServers"], parked);

        Assert.Equal(original.SourceText, restored);
    }
}
