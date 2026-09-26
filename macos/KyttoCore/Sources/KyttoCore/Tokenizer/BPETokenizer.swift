import Foundation

/// A byte-level BPE tokenizer over a bundled rank table.
///
/// §7.4 originally rejected bundling a tokenizer, on the grounds that the
/// ranking and the order of magnitude are what a user acts on and a real
/// vocabulary is not worth the megabytes. That call has been reversed, and
/// §7.4 now records why: the number stopped being only a ranking the moment
/// tools became individually maskable (§7.11). "Hiding these six saves ~12k"
/// is a promise about a specific number.
///
/// The old `chars/4` heuristic overstates a realistic tool definition by
/// roughly a fifth — measured, not assumed, in `TokenizerTests`. Tool JSON is
/// dense with ordinary English and repeated structural punctuation, both of
/// which this vocabulary compresses well, so the error runs consistently in
/// the direction that made every server look more expensive than it is.
///
/// This is not an MCP-visible component and never sees a config file. It reads
/// one static table and turns text into a count.
public final class BPETokenizer: @unchecked Sendable {
    /// Ranks keyed by the exact bytes of each vocabulary entry.
    private let ranks: [[UInt8]: Int]
    /// Splits text the way the encoding's own pre-tokenizer does, before any
    /// merging happens. Getting this wrong changes counts far more than the
    /// merge loop does.
    private let pattern: NSRegularExpression

    /// The pre-tokenization pattern cl100k_base was built with.
    private static let cl100kPattern = #"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+"#

    /// The encoding used by GPT-4 and text-embedding-3, and the one this app
    /// measures with.
    ///
    /// Nil only if the bundled table is missing, which is a packaging mistake
    /// rather than a user problem — callers fall back to the old estimate and
    /// say so in the label rather than failing a health check over it.
    public static let cl100kBase: BPETokenizer? = load(resource: "cl100k_base")

    /// The name shown wherever a measurement is labelled.
    public static let cl100kMethod = "cl100k_base"

    init(ranks: [[UInt8]: Int], pattern: NSRegularExpression) {
        self.ranks = ranks
        self.pattern = pattern
    }

    private static func load(resource: String) -> BPETokenizer? {
        guard let url = Bundle.module.url(forResource: resource, withExtension: "tiktoken"),
              let data = try? Data(contentsOf: url),
              let pattern = try? NSRegularExpression(pattern: cl100kPattern)
        else { return nil }

        let ranks = parseRanks(data)
        guard !ranks.isEmpty else { return nil }
        return BPETokenizer(ranks: ranks, pattern: pattern)
    }

    /// One `base64(token) rank` pair per line, which is the format the published
    /// tables ship in. A malformed line is skipped rather than fatal: a partial
    /// table still counts better than `chars/4`.
    static func parseRanks(_ data: Data) -> [[UInt8]: Int] {
        var result: [[UInt8]: Int] = [:]
        result.reserveCapacity(110_000)

        for line in data.split(separator: 0x0A, omittingEmptySubsequences: true) {
            guard let space = line.firstIndex(of: 0x20) else { continue }
            guard let token = Data(base64Encoded: Data(line[line.startIndex..<space])),
                  let rank = Int(String(decoding: line[line.index(after: space)...], as: UTF8.self))
            else { continue }
            result[Array(token)] = rank
        }
        return result
    }

    // MARK: - Counting

    /// How many tokens `text` costs.
    ///
    /// Special tokens are not recognised, deliberately: a server description
    /// containing the literal text of one is text a model would be shown, and
    /// counting it as a single control token would understate it.
    public func countTokens(_ text: String) -> Int {
        var total = 0
        forEachPiece(in: text) { bytes in
            total += ranks[bytes] != nil ? 1 : mergeBoundaries(bytes).count - 1
        }
        return total
    }

    /// The token ids for `text`. Only the count is used in the app; this exists
    /// so the merge loop can be tested against known vectors rather than
    /// against itself.
    public func encode(_ text: String) -> [Int] {
        var tokens: [Int] = []
        forEachPiece(in: text) { bytes in
            if let rank = ranks[bytes] {
                tokens.append(rank)
                return
            }
            let boundaries = mergeBoundaries(bytes)
            for index in 0..<(boundaries.count - 1) {
                let part = Array(bytes[boundaries[index]..<boundaries[index + 1]])
                guard let rank = ranks[part] else { continue }
                tokens.append(rank)
            }
        }
        return tokens
    }

    // MARK: - Pre-tokenization

    private func forEachPiece(in text: String, _ body: ([UInt8]) -> Void) {
        guard !text.isEmpty else { return }
        let scalars = text as NSString
        let whole = NSRange(location: 0, length: scalars.length)

        pattern.enumerateMatches(in: text, range: whole) { match, _, _ in
            guard let match, match.range.length > 0 else { return }
            body(Array(scalars.substring(with: match.range).utf8))
        }
    }

    // MARK: - Merging

    /// Byte offsets where the merged tokens of `piece` begin, plus its end.
    ///
    /// The loop repeatedly merges the adjacent pair with the lowest rank, which
    /// is what makes a BPE result depend on merge *order* rather than on greedy
    /// longest-match. Doing this the cheap way — greedily taking the longest
    /// known prefix — is the classic way to get counts that are close but
    /// quietly wrong, so it is done properly here.
    func mergeBoundaries(_ piece: [UInt8]) -> [Int] {
        guard piece.count > 1 else { return piece.isEmpty ? [0] : [0, piece.count] }

        let unrankable = Int.max
        // `parts[i]` is the start of a part, paired with the rank of merging it
        // with the part after it. Two sentinels at the end keep the lookahead
        // in `rank(of:in:)` in bounds.
        var parts: [(start: Int, rank: Int)] = []
        parts.reserveCapacity(piece.count + 1)

        var lowest = (rank: unrankable, index: -1)
        for index in 0..<(piece.count - 1) {
            let rank = ranks[Array(piece[index..<(index + 2)])] ?? unrankable
            if rank < lowest.rank { lowest = (rank, index) }
            parts.append((index, rank))
        }
        parts.append((piece.count - 1, unrankable))
        parts.append((piece.count, unrankable))

        func rank(of index: Int, in parts: [(start: Int, rank: Int)]) -> Int {
            guard index + 3 < parts.count else { return unrankable }
            return ranks[Array(piece[parts[index].start..<parts[index + 3].start])] ?? unrankable
        }

        while lowest.rank != unrankable {
            let index = lowest.index
            if index > 0 { parts[index - 1].rank = rank(of: index - 1, in: parts) }
            parts[index].rank = rank(of: index, in: parts)
            parts.remove(at: index + 1)

            lowest = (unrankable, -1)
            for (position, part) in parts.dropLast().enumerated() where part.rank < lowest.rank {
                lowest = (part.rank, position)
            }
        }

        return parts.map(\.start)
    }
}
