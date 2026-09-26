using System.Text;
using System.Text.Json;
using Kytto.Core.Health;
using Kytto.Core.Tokenizer;

namespace Kytto.Core.Tests;

/// <summary>
/// Differential tests against the reference encoding, not self-comparisons.
/// </summary>
/// <remarks>
/// A BPE implementation that is subtly wrong still produces plausible numbers, so
/// the vectors in <c>Fixtures/cl100k_vectors.json</c> were produced by the
/// reference <c>tiktoken</c> implementation over the same rank table this project
/// bundles. Regenerating them is a deliberate act; they are not written from this
/// code's own output.
/// </remarks>
public sealed class TokenizerTests
{
    private sealed record Vector(string Text, int[] Tokens);

    private static BpeTokenizer Tokenizer =>
        BpeTokenizer.Cl100kBase ?? throw new InvalidOperationException("the bundled rank table did not load");

    private static IReadOnlyList<Vector> Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cl100k_vectors.json");
        return JsonSerializer.Deserialize<Vector[]>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void EveryByteValueIsRepresentable()
    {
        // A missing single-byte entry would make some input encode to nothing,
        // which reads as "this tool is free" rather than as a bug.
        for (var byteValue = 0; byteValue <= byte.MaxValue; byteValue += 1)
        {
            Assert.Equal([0, 1], Tokenizer.MergeBoundaries([(byte)byteValue]));
        }
    }

    [Fact]
    public void EncodesExactlyWhatTheReferenceImplementationDoes()
    {
        var vectors = Vectors();
        Assert.True(vectors.Count >= 18, "the fixture lost cases");

        foreach (var vector in vectors)
        {
            Assert.Equal(vector.Tokens, Tokenizer.Encode(vector.Text));
        }
    }

    [Fact]
    public void CountingAgreesWithEncoding()
    {
        foreach (var vector in Vectors())
        {
            Assert.Equal(vector.Tokens.Length, Tokenizer.CountTokens(vector.Text));
        }
    }

    /// <summary>
    /// Text that looks like a control token is text, because that is what a model
    /// is shown when a server puts it in a description.
    /// </summary>
    [Fact]
    public void SpecialTokenTextIsCountedAsOrdinaryText() =>
        Assert.True(Tokenizer.CountTokens("<|endoftext|>") > 1);

    /// <summary>The measured reason for bundling a table at all (§7.4).</summary>
    /// <remarks>
    /// The old heuristic is not merely imprecise on a realistic tool definition — it
    /// is wrong in a consistent direction, and it is the direction that makes
    /// servers look more expensive than they are.
    /// </remarks>
    [Fact]
    public void CharsOverFourOverstatesToolDefinitions()
    {
        var toolJson = Vectors().MaxBy(vector => vector.Text.Length)?.Text
            ?? throw new InvalidOperationException("the fixture lost its realistic tool definition");

        var measured = Tokenizer.CountTokens(toolJson);
        var heuristic = (int)Math.Round(toolJson.Length / 4.0, MidpointRounding.AwayFromZero);

        Assert.True(measured < heuristic);
        Assert.True((double)(heuristic - measured) / measured > 0.15);
    }

    [Fact]
    public void TokenWeightReportsTheVocabularyItUsed()
    {
        var weight = TokenWeight.Measuring("""{"tools":[]}""");
        Assert.Equal(BpeTokenizer.Cl100kMethod, weight.Method);
        Assert.True(weight.IsMeasured);

        var fallback = TokenWeight.Estimating("abcd");
        Assert.Equal(1, fallback.Estimate);
        Assert.False(fallback.IsMeasured);
    }

    [Fact]
    public void MalformedRankLinesAreSkippedNotFatal()
    {
        var table = Encoding.UTF8.GetBytes(
            """
            aGVsbG8= 0
            this line has no valid base64 or rank
            d29ybGQ= 1

            """);

        var ranks = BpeTokenizer.ParseRanks(table);

        Assert.Equal(0, ranks[Encoding.UTF8.GetBytes("hello")]);
        Assert.Equal(1, ranks[Encoding.UTF8.GetBytes("world")]);
        Assert.Equal(2, ranks.Count);
    }
}
