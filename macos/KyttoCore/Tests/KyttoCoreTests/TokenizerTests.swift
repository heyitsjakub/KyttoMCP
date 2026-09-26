import Foundation
import Testing
@testable import KyttoCore

/// Differential tests against the reference encoding, not self-comparisons.
///
/// A BPE implementation that is subtly wrong still produces plausible numbers,
/// so the vectors in `Fixtures/cl100k_vectors.json` were produced by the
/// reference `tiktoken` implementation over the same rank table this package
/// bundles. Regenerating them is a deliberate act; they are not written from
/// this code's own output.
@Suite("BPETokenizer — cl100k_base")
struct TokenizerTests {

    private struct Vector: Decodable {
        let text: String
        let tokens: [Int]
    }

    private var tokenizer: BPETokenizer {
        get throws {
            try #require(BPETokenizer.cl100kBase, "the bundled rank table did not load")
        }
    }

    private func vectors() throws -> [Vector] {
        let base = try #require(Bundle.module.resourceURL)
        let url = base.appending(path: "Fixtures").appending(path: "cl100k_vectors.json")
        return try JSONDecoder().decode([Vector].self, from: Data(contentsOf: url))
    }

    @Test("Every byte value is representable")
    func tableIsComplete() throws {
        let tokenizer = try tokenizer
        // A missing single-byte entry would make some input encode to nothing,
        // which reads as "this tool is free" rather than as a bug.
        for byte in UInt8.min...UInt8.max {
            #expect(tokenizer.mergeBoundaries([byte]) == [0, 1], "byte \(byte)")
        }
    }

    @Test("Encodes exactly what the reference implementation does")
    func matchesReference() throws {
        let tokenizer = try tokenizer
        let vectors = try vectors()
        #expect(vectors.count >= 18, "the fixture lost cases")

        for vector in vectors {
            #expect(
                tokenizer.encode(vector.text) == vector.tokens,
                "encoding \(vector.text.prefix(40).debugDescription)"
            )
        }
    }

    @Test("Counting agrees with encoding")
    func countMatchesEncode() throws {
        let tokenizer = try tokenizer
        for vector in try vectors() {
            #expect(
                tokenizer.countTokens(vector.text) == vector.tokens.count,
                "counting \(vector.text.prefix(40).debugDescription)"
            )
        }
    }

    /// Text that looks like a control token is text, because that is what a
    /// model is shown when a server puts it in a description.
    @Test("Special-token text is counted as ordinary text")
    func specialTokensAreNotSpecial() throws {
        let tokenizer = try tokenizer
        #expect(tokenizer.countTokens("<|endoftext|>") > 1)
    }

    /// The measured reason for bundling a table at all (§7.4).
    ///
    /// The old heuristic is not merely imprecise on a realistic tool
    /// definition — it is wrong in a consistent direction, and it is the
    /// direction that makes servers look more expensive than they are.
    @Test("chars/4 overstates a realistic tool definition")
    func heuristicOverstatesToolDefinitions() throws {
        let tokenizer = try tokenizer
        let toolJSON = try #require(
            try vectors().max(by: { $0.text.count < $1.text.count })?.text,
            "the fixture lost its realistic tool definition"
        )

        let measured = tokenizer.countTokens(toolJSON)
        let heuristic = Int((Double(toolJSON.count) / 4).rounded())
        #expect(measured < heuristic)
        #expect(Double(heuristic - measured) / Double(measured) > 0.15)
    }

    @Test("TokenWeight reports the vocabulary it used")
    func weightLabelsItsMethod() {
        let weight = TokenWeight.measuring(#"{"tools":[]}"#)
        #expect(weight.method == BPETokenizer.cl100kMethod)
        #expect(weight.isMeasured)

        let fallback = TokenWeight.estimating("abcd")
        #expect(fallback.estimate == 1)
        #expect(!fallback.isMeasured)
    }

    @Test("A malformed rank table is skipped line by line, not fatally")
    func malformedLinesAreSkipped() {
        let table = Data("""
        aGVsbG8= 0
        this line has no valid base64 or rank
        d29ybGQ= 1

        """.utf8)
        let ranks = BPETokenizer.parseRanks(table)
        #expect(ranks[Array("hello".utf8)] == 0)
        #expect(ranks[Array("world".utf8)] == 1)
        #expect(ranks.count == 2)
    }
}
