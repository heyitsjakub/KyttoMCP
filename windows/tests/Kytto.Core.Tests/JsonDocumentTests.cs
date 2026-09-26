using System.Text;
using Kytto.Core.Json;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

// The tests in this file are the ones that decide whether writing is safe. If a
// span is off by a byte, a config write corrupts someone's file. Everything here
// runs against the fixtures, including a redacted copy of a real Claude Desktop
// config.

public class JsonDocumentSpanTests
{
    [Theory, MemberData(nameof(JsonFixtures))]
    public void EveryFixtureParses(Fixture fixture)
    {
        _ = fixture.Document();
    }

    /// <summary>
    /// The text a span points at must re-parse to the value the span came from.
    /// This is what proves the offsets are right.
    /// </summary>
    [Theory, MemberData(nameof(JsonFixtures))]
    public void SpansPointAtTheirOwnValue(Fixture fixture)
    {
        var doc = fixture.Document();
        foreach (var node in Nodes.All(doc.Root))
        {
            var slice = doc.Slice(node.Span);
            var reparsed = JsonDocument.Parse(slice);
            Assert.True(
                Shape.Of(reparsed.Root) == Shape.Of(node),
                $"span {node.Span.Start}..{node.Span.End} does not round-trip in {fixture.FileName()}");
        }
    }

    /// <summary>
    /// Splicing a node's own text back over itself must be a no-op on the whole
    /// file. If this fails, a write would move or drop bytes elsewhere.
    /// </summary>
    [Theory, MemberData(nameof(JsonFixtures))]
    public void SplicingAValueOverItselfChangesNothing(Fixture fixture)
    {
        var doc = fixture.Document();
        foreach (var node in Nodes.All(doc.Root))
        {
            var unchanged = doc.Replacing(node.Span, doc.Slice(node.Span));
            Assert.True(unchanged == doc.SourceText, $"identity splice altered {fixture.FileName()}");
        }
    }

    [Fact]
    public void MemberSpansCoverKeyThroughValue()
    {
        var doc = Fixture.CursorMcp.Document();
        var servers = doc.ValueAt("mcpServers");
        Assert.NotNull(servers);
        var member = servers.Member("figma");
        Assert.NotNull(member);

        var text = doc.Slice(member.Span);
        Assert.StartsWith("\"figma\"", text, StringComparison.Ordinal);
        Assert.EndsWith("}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacingOneServerLeavesTheRestByteIdentical()
    {
        var doc = Fixture.ClaudeDesktopWithServers.Document();
        var postgres = doc.ValueAt("mcpServers", "postgres");
        Assert.NotNull(postgres);

        var updated = doc.Replacing(postgres.Span, """{"command": "replaced"}""");

        // Everything before and after the replaced span survives unchanged.
        var originalBytes = Encoding.UTF8.GetBytes(doc.SourceText);
        var updatedBytes = Encoding.UTF8.GetBytes(updated);
        Assert.Equal(
            originalBytes[..postgres.Span.Start],
            updatedBytes[..postgres.Span.Start]);

        var originalTail = originalBytes[postgres.Span.End..];
        var updatedTail = updatedBytes[^originalTail.Length..];
        Assert.Equal(originalTail, updatedTail);

        // And the result is still valid JSON with the sibling keys intact.
        var reparsed = JsonDocument.Parse(updated);
        Assert.NotNull(reparsed.ValueAt("mcpServers", "github"));
        Assert.True(reparsed.ValueAt("preferences", "chromeExtensionEnabled")?.BoolValue);
        Assert.Equal("replaced", reparsed.ValueAt("mcpServers", "postgres", "command")?.StringValue);
    }

    [Fact]
    public void MultipleEditsApplyWithoutShiftingEachOther()
    {
        var doc = Fixture.CursorMcp.Document();
        var github = doc.ValueAt("mcpServers", "github", "command");
        var figmaUrl = doc.ValueAt("mcpServers", "figma", "url");
        Assert.NotNull(github);
        Assert.NotNull(figmaUrl);

        var updated = doc.Replacing(
        [
            (github.Span, "\"bunx\""),
            (figmaUrl.Span, "\"https://example.com/sse\""),
        ]);

        var reparsed = JsonDocument.Parse(updated);
        Assert.Equal("bunx", reparsed.ValueAt("mcpServers", "github", "command")?.StringValue);
        Assert.Equal("https://example.com/sse", reparsed.ValueAt("mcpServers", "figma", "url")?.StringValue);
    }

    public static TheoryData<Fixture> JsonFixtures => Fixtures.Json;
}

public class JsonCTests
{
    [Fact]
    public void CommentsAndTrailingCommasParse()
    {
        var doc = Fixture.VsCodeMcp.Document();
        var servers = doc.ValueAt("servers");
        Assert.NotNull(servers);
        Assert.Equal(["playwright", "github"], servers.Keys);
        Assert.Equal(
            "https://api.githubcopilot.com/mcp/",
            doc.ValueAt("servers", "github", "url")?.StringValue);
    }

    [Fact]
    public void CommentsSurviveASplice()
    {
        var doc = Fixture.VsCodeMcp.Document();
        var playwright = doc.ValueAt("servers", "playwright");
        Assert.NotNull(playwright);

        var updated = doc.Replacing(playwright.Span, """{"type": "stdio", "command": "echo"}""");

        Assert.Contains("// User-level MCP configuration for VS Code.", updated, StringComparison.Ordinal);
        Assert.Contains("// Inputs are prompted for on first use.", updated, StringComparison.Ordinal);
        Assert.Contains("/* Servers are keyed by name", updated, StringComparison.Ordinal);
        // The comment that lived inside the replaced value is gone, which is correct.
        Assert.DoesNotContain("// pinned deliberately", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommentBetweenKeyAndValueDoesNotCorruptTheMemberSpan()
    {
        const string Source = """
            {
              "a" /* why */ : 1,
              "b": 2
            }
            """;
        var doc = JsonDocument.Parse(Source);
        Assert.Equal(["a", "b"], doc.Root.Keys);

        var a = doc.Root.Member("a");
        Assert.NotNull(a);
        Assert.Equal("1", doc.Slice(a.Value.Span));
        Assert.Contains("/* why */", doc.Replacing(a.Value.Span, "9"), StringComparison.Ordinal);
    }

    [Fact]
    public void TrailingCommaInAnArray()
    {
        var doc = JsonDocument.Parse("[1, 2, 3,]");
        Assert.Equal(3, doc.Root.Elements?.Count);
    }

    [Fact]
    public void EmptyContainers()
    {
        var doc = JsonDocument.Parse("""{"a": {}, "b": [], "c": null}""");
        Assert.Empty(doc.Root.Member("a")!.Value.Members!);
        Assert.Empty(doc.Root.Member("b")!.Value.Elements!);
        Assert.True(doc.Root.Member("c")!.Value.IsNull);
    }
}

public class JsonLiteralTests
{
    [Fact]
    public void StringEscapesDecode()
    {
        var doc = JsonDocument.Parse("""{"s": "tab\there\nnew \"quoted\" \\ é 😀"}""");
        Assert.Equal("tab\there\nnew \"quoted\" \\ é 😀", doc.Root["s"]?.StringValue);
    }

    [Fact]
    public void Numbers()
    {
        var doc = JsonDocument.Parse("[0, -1, 3.5, 1e3, -2.5E-2]");
        var values = doc.Root.Elements!.Select(node => node.NumberValue!.Value).ToArray();
        Assert.Equal([0, -1, 3.5, 1000, -0.025], values);
    }

    /// <summary>
    /// Spans are byte offsets; a naive character-index parser breaks here. The
    /// Slovak place name is deliberate non-ASCII input: it mixes two- and
    /// three-byte UTF-8 sequences before the span being checked.
    /// </summary>
    [Fact]
    public void Utf8MultibyteContentKeepsSpansAligned()
    {
        var doc = JsonDocument.Parse("{\"mesto\": \"Košice — Ťahanovce\", \"n\": 1}");
        Assert.Equal("Košice — Ťahanovce", doc.Root["mesto"]?.StringValue);

        var n = doc.Root.Member("n");
        Assert.NotNull(n);
        Assert.Equal("1", doc.Slice(n.Value.Span));
        Assert.Contains(
            "Košice — Ťahanovce",
            doc.Replacing(n.Value.Span, "2"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOrderMarkIsSkipped()
    {
        var doc = JsonDocument.Parse("﻿{\"a\": 1}");
        Assert.Equal(1, doc.Root["a"]?.NumberValue);
    }

    [Fact]
    public void DuplicateKeysArePreservedNotMerged()
    {
        var doc = JsonDocument.Parse("""{"a": 1, "a": 2}""");
        Assert.Equal(["a", "a"], doc.Root.Keys);
        Assert.Equal(1, doc.Root["a"]?.NumberValue);
    }
}

public class JsonErrorTests
{
    [Fact]
    public void UnterminatedStringIsRejected() =>
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("""{"a": "oops}"""));

    [Fact]
    public void TrailingContentAfterTheRootValueIsRejected() =>
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("""{"a": 1} {"b": 2}"""));

    [Fact]
    public void UnterminatedBlockCommentIsRejected() =>
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse("{\"a\": 1} /* never closed"));

    [Theory]
    [InlineData("01")]
    [InlineData("-01")]
    public void NumbersWithLeadingZerosAreRejected(string number) =>
        Assert.Throws<JsonParseException>(() => JsonDocument.Parse(number));

    [Fact]
    public void RawControlCharactersInsideStringsAreRejected() =>
        Assert.Throws<JsonParseException>(() =>
            JsonDocument.Parse("{\"a\": \"first line\nsecond line\"}"));

    [Fact]
    public void ErrorCarriesLineAndColumn()
    {
        var error = Assert.Throws<JsonParseException>(() => JsonDocument.Parse("{\n  \"a\": ,\n}"));
        Assert.Equal(2, error.Line);
    }
}
