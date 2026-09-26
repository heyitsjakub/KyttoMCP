using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Kytto.Core.Tokenizer;

/// <summary>A byte-level BPE tokenizer over a bundled rank table.</summary>
/// <remarks>
/// <para>
/// §7.4 originally rejected bundling a tokenizer, on the grounds that the ranking
/// and the order of magnitude are what a user acts on and a real vocabulary is not
/// worth the megabytes. That call has been reversed, and §7.4 now records why: the
/// number stopped being only a ranking the moment tools became individually
/// maskable (§7.11). "Hiding these six saves ~12k" is a promise about a specific
/// number.
/// </para>
/// <para>
/// The old <c>chars/4</c> heuristic overstates a realistic tool definition by
/// roughly a fifth — measured, not assumed, in <c>TokenizerTests</c>. Tool JSON is
/// dense with ordinary English and repeated structural punctuation, both of which
/// this vocabulary compresses well, so the error runs consistently in the
/// direction that made every server look more expensive than it is.
/// </para>
/// <para>
/// This is not an MCP-visible component and never sees a config file. It reads one
/// static table and turns text into a count.
/// </para>
/// </remarks>
public sealed partial class BpeTokenizer
{
    /// <summary>Ranks keyed by the exact bytes of each vocabulary entry.</summary>
    private readonly Dictionary<byte[], int> _ranks;

    /// <summary>
    /// The same table, looked up by span so the merge loop does not allocate an
    /// array per candidate pair. .NET's alternate lookup is what makes a faithful
    /// port of the merge loop affordable.
    /// </summary>
    private readonly Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> _bySpan;

    /// <summary>
    /// The pre-tokenization pattern cl100k_base was built with. It splits text the
    /// way the encoding's own pre-tokenizer does, before any merging happens.
    /// Getting this wrong changes counts far more than the merge loop does.
    /// </summary>
    [GeneratedRegex(
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+")]
    private static partial Regex Cl100kPattern { get; }

    /// <summary>The name shown wherever a measurement is labelled.</summary>
    public const string Cl100kMethod = "cl100k_base";

    /// <summary>
    /// The encoding used by GPT-4 and text-embedding-3, and the one this app
    /// measures with.
    /// </summary>
    /// <remarks>
    /// Null only if the bundled table is missing, which is a packaging mistake
    /// rather than a user problem — callers fall back to the old estimate and say
    /// so in the label rather than failing a health check over it.
    /// </remarks>
    public static BpeTokenizer? Cl100kBase { get; } = Load("cl100k_base.tiktoken");

    internal BpeTokenizer(Dictionary<byte[], int> ranks)
    {
        _ranks = ranks;
        _bySpan = ranks.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    private static BpeTokenizer? Load(string resource)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                candidate => candidate.EndsWith(resource, StringComparison.Ordinal));
            if (name is null) return null;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var ranks = ParseRanks(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return ranks.Count == 0 ? null : new BpeTokenizer(ranks);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses one <c>base64(token) rank</c> pair per line, which is the format the
    /// published tables ship in.
    /// </summary>
    /// <remarks>
    /// A malformed line is skipped rather than fatal: a partial table still counts
    /// better than <c>chars/4</c>.
    /// </remarks>
    internal static Dictionary<byte[], int> ParseRanks(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte[], int>(110_000, ByteSequenceComparer.Instance);

        foreach (var range in data.Split((byte)'\n'))
        {
            var line = data[range];
            if (line.Length == 0) continue;

            var space = line.IndexOf((byte)' ');
            if (space < 0) continue;

            var token = new byte[Base64.GetMaxDecodedFromUtf8Length(space)];
            if (Base64.DecodeFromUtf8(line[..space], token, out _, out var written) != OperationStatus.Done)
            {
                continue;
            }
            if (!int.TryParse(line[(space + 1)..], CultureInfo.InvariantCulture, out var rank)) continue;

            result[token.AsSpan(0, written).ToArray()] = rank;
        }
        return result;
    }

    // MARK: - Counting

    /// <summary>How many tokens <paramref name="text"/> costs.</summary>
    /// <remarks>
    /// Special tokens are not recognised, deliberately: a server description
    /// containing the literal text of one is text a model would be shown, and
    /// counting it as a single control token would understate it.
    /// </remarks>
    public int CountTokens(string text)
    {
        var total = 0;
        foreach (var piece in Pieces(text))
        {
            total += _bySpan.ContainsKey(piece) ? 1 : MergeBoundaries(piece).Count - 1;
        }
        return total;
    }

    /// <summary>The token ids for <paramref name="text"/>.</summary>
    /// <remarks>
    /// Only the count is used in the app; this exists so the merge loop can be
    /// tested against known vectors rather than against itself.
    /// </remarks>
    public IReadOnlyList<int> Encode(string text)
    {
        var tokens = new List<int>();
        foreach (var piece in Pieces(text))
        {
            if (_bySpan.TryGetValue(piece, out var whole))
            {
                tokens.Add(whole);
                continue;
            }
            var boundaries = MergeBoundaries(piece);
            for (var index = 0; index < boundaries.Count - 1; index += 1)
            {
                var part = piece.AsSpan(boundaries[index], boundaries[index + 1] - boundaries[index]);
                if (_bySpan.TryGetValue(part, out var rank)) tokens.Add(rank);
            }
        }
        return tokens;
    }

    // MARK: - Pre-tokenization

    private static IEnumerable<byte[]> Pieces(string text)
    {
        if (text.Length == 0) yield break;

        foreach (Match match in Cl100kPattern.Matches(text))
        {
            if (match.Length == 0) continue;
            yield return Encoding.UTF8.GetBytes(match.Value);
        }
    }

    // MARK: - Merging

    /// <summary>
    /// Byte offsets where the merged tokens of <paramref name="piece"/> begin, plus
    /// its end.
    /// </summary>
    /// <remarks>
    /// The loop repeatedly merges the adjacent pair with the lowest rank, which is
    /// what makes a BPE result depend on merge <em>order</em> rather than on greedy
    /// longest-match. Doing this the cheap way — greedily taking the longest known
    /// prefix — is the classic way to get counts that are close but quietly wrong,
    /// so it is done properly here.
    /// </remarks>
    internal List<int> MergeBoundaries(byte[] piece)
    {
        if (piece.Length <= 1) return piece.Length == 0 ? [0] : [0, piece.Length];

        const int unrankable = int.MaxValue;
        // `parts[i]` is the start of a part, paired with the rank of merging it
        // with the part after it. Two sentinels at the end keep the lookahead in
        // `RankOf` in bounds.
        var parts = new List<(int Start, int Rank)>(piece.Length + 1);

        var lowestRank = unrankable;
        var lowestIndex = -1;
        for (var index = 0; index < piece.Length - 1; index += 1)
        {
            var rank = _bySpan.TryGetValue(piece.AsSpan(index, 2), out var found) ? found : unrankable;
            if (rank < lowestRank) (lowestRank, lowestIndex) = (rank, index);
            parts.Add((index, rank));
        }
        parts.Add((piece.Length - 1, unrankable));
        parts.Add((piece.Length, unrankable));

        // Deliberately reads the array as it stands *before* the merge is spliced
        // out: the three-part lookahead is what spans the pair being merged plus
        // the part after it.
        int RankOf(int index)
        {
            if (index + 3 >= parts.Count) return unrankable;
            var start = parts[index].Start;
            var end = parts[index + 3].Start;
            return _bySpan.TryGetValue(piece.AsSpan(start, end - start), out var rank) ? rank : unrankable;
        }

        while (lowestRank != unrankable)
        {
            var index = lowestIndex;
            if (index > 0) parts[index - 1] = (parts[index - 1].Start, RankOf(index - 1));
            parts[index] = (parts[index].Start, RankOf(index));
            parts.RemoveAt(index + 1);

            lowestRank = unrankable;
            lowestIndex = -1;
            for (var position = 0; position < parts.Count - 1; position += 1)
            {
                if (parts[position].Rank < lowestRank) (lowestRank, lowestIndex) = (parts[position].Rank, position);
            }
        }

        return parts.Select(part => part.Start).ToList();
    }

    /// <summary>
    /// Hashes and compares vocabulary keys by their bytes, and lets the same table
    /// be probed with a span so lookups on the hot path allocate nothing.
    /// </summary>
    private sealed class ByteSequenceComparer
        : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        internal static readonly ByteSequenceComparer Instance = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            left is null || right is null ? ReferenceEquals(left, right) : left.AsSpan().SequenceEqual(right);

        public int GetHashCode([DisallowNull] byte[] value) => GetHashCode(value.AsSpan());

        public bool Equals(ReadOnlySpan<byte> probe, byte[] stored) => probe.SequenceEqual(stored);

        public int GetHashCode(ReadOnlySpan<byte> probe)
        {
            var hash = new HashCode();
            hash.AddBytes(probe);
            return hash.ToHashCode();
        }

        public byte[] Create(ReadOnlySpan<byte> probe) => probe.ToArray();
    }
}
