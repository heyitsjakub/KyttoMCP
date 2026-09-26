import Foundation
import Testing
@testable import KyttoCore

// The tests in this file are the ones that decide whether M2 is safe. If a span
// is off by a byte, a config write corrupts someone's file. Everything here runs
// against the fixtures, including a redacted copy of a real Claude Desktop config.

@Suite("JSONDocument — spans")
struct JSONDocumentSpanTests {

    @Test("every fixture parses", arguments: Fixture.json)
    func fixtureParses(_ fixture: Fixture) throws {
        _ = try fixture.document()
    }

    /// The text a span points at must re-parse to the value the span came from.
    /// This is what proves the offsets are right.
    @Test("spans point at their own value", arguments: Fixture.json)
    func spansAreAccurate(_ fixture: Fixture) throws {
        let doc = try fixture.document()
        for node in allNodes(doc.root) {
            let slice = doc.slice(node.span)
            let reparsed = try JSONDocument.parse(slice)
            #expect(
                Shape(reparsed.root) == Shape(node),
                "span \(node.span.start)..<\(node.span.end) does not round-trip in \(fixture.rawValue)"
            )
        }
    }

    /// Splicing a node's own text back over itself must be a no-op on the whole
    /// file. If this fails, a write would move or drop bytes elsewhere.
    @Test("splicing a value over itself changes nothing", arguments: Fixture.json)
    func spliceIdentity(_ fixture: Fixture) throws {
        let doc = try fixture.document()
        for node in allNodes(doc.root) {
            let unchanged = doc.replacing(node.span, with: doc.slice(node.span))
            #expect(unchanged == doc.sourceText, "identity splice altered \(fixture.rawValue)")
        }
    }

    @Test("member spans cover key through value")
    func memberSpans() throws {
        let doc = try Fixture.cursorMCP.document()
        let servers = try #require(doc.value(at: ["mcpServers"]))
        let member = try #require(servers.member("figma"))
        let text = doc.slice(member.span)
        #expect(text.hasPrefix("\"figma\""))
        #expect(text.hasSuffix("}"))
    }

    @Test("replacing one server leaves the rest of the file byte-identical")
    func targetedReplacement() throws {
        let doc = try Fixture.claudeDesktopWithServers.document()
        let postgres = try #require(doc.value(at: ["mcpServers", "postgres"]))
        let updated = doc.replacing(postgres.span, with: #"{"command": "replaced"}"#)

        // Everything before and after the replaced span survives unchanged.
        let originalBytes = Array(doc.sourceText.utf8)
        let updatedBytes = Array(updated.utf8)
        #expect(Array(originalBytes[..<postgres.span.start]) == Array(updatedBytes[..<postgres.span.start]))

        let originalTail = Array(originalBytes[postgres.span.end...])
        let updatedTail = Array(updatedBytes.suffix(originalTail.count))
        #expect(originalTail == updatedTail)

        // And the result is still valid JSON with the sibling keys intact.
        let reparsed = try JSONDocument.parse(updated)
        #expect(reparsed.value(at: ["mcpServers", "github"]) != nil)
        #expect(reparsed.value(at: ["preferences", "chromeExtensionEnabled"])?.boolValue == true)
        #expect(reparsed.value(at: ["mcpServers", "postgres", "command"])?.stringValue == "replaced")
    }

    @Test("multiple edits apply without shifting each other")
    func batchedEdits() throws {
        let doc = try Fixture.cursorMCP.document()
        let github = try #require(doc.value(at: ["mcpServers", "github", "command"]))
        let figmaURL = try #require(doc.value(at: ["mcpServers", "figma", "url"]))

        let updated = doc.replacing([
            (github, #""bunx""#),
            (figmaURL, #""https://example.com/sse""#),
        ].map { (span: $0.0.span, replacement: $0.1) })

        let reparsed = try JSONDocument.parse(updated)
        #expect(reparsed.value(at: ["mcpServers", "github", "command"])?.stringValue == "bunx")
        #expect(reparsed.value(at: ["mcpServers", "figma", "url"])?.stringValue == "https://example.com/sse")
    }
}

@Suite("JSONDocument — JSONC")
struct JSONCTests {

    @Test("comments and trailing commas parse")
    func vscodeConfig() throws {
        let doc = try Fixture.vscodeMCP.document()
        let servers = try #require(doc.value(at: ["servers"]))
        #expect(servers.keys == ["playwright", "github"])
        #expect(doc.value(at: ["servers", "github", "url"])?.stringValue == "https://api.githubcopilot.com/mcp/")
    }

    @Test("comments survive a splice")
    func commentsSurvive() throws {
        let doc = try Fixture.vscodeMCP.document()
        let playwright = try #require(doc.value(at: ["servers", "playwright"]))
        let updated = doc.replacing(playwright.span, with: #"{"type": "stdio", "command": "echo"}"#)

        #expect(updated.contains("// User-level MCP configuration for VS Code."))
        #expect(updated.contains("// Inputs are prompted for on first use."))
        #expect(updated.contains("/* Servers are keyed by name"))
        // The comment that lived inside the replaced value is gone, which is correct.
        #expect(!updated.contains("// pinned deliberately"))
    }

    @Test("a comment between key and value does not corrupt the member span")
    func commentBetweenKeyAndValue() throws {
        let source = """
        {
          "a" /* why */ : 1,
          "b": 2
        }
        """
        let doc = try JSONDocument.parse(source)
        #expect(doc.root.keys == ["a", "b"])
        let a = try #require(doc.root.member("a"))
        #expect(doc.slice(a.value.span) == "1")
        #expect(doc.replacing(a.value.span, with: "9").contains("/* why */"))
    }

    @Test("trailing comma in an array")
    func trailingCommaArray() throws {
        let doc = try JSONDocument.parse("[1, 2, 3,]")
        #expect(doc.root.elements?.count == 3)
    }

    @Test("empty containers")
    func emptyContainers() throws {
        let doc = try JSONDocument.parse(#"{"a": {}, "b": [], "c": null}"#)
        #expect(doc.root.member("a")?.value.members?.isEmpty == true)
        #expect(doc.root.member("b")?.value.elements?.isEmpty == true)
        #expect(doc.root.member("c")?.value.isNull == true)
    }
}

@Suite("JSONDocument — literals")
struct JSONLiteralTests {

    @Test("string escapes decode")
    func escapes() throws {
        let doc = try JSONDocument.parse(#"{"s": "tab\there\nnew \"quoted\" \\ é 😀"}"#)
        #expect(doc.root["s"]?.stringValue == "tab\there\nnew \"quoted\" \\ é 😀")
    }

    @Test("numbers")
    func numbers() throws {
        let doc = try JSONDocument.parse(#"[0, -1, 3.5, 1e3, -2.5E-2]"#)
        let values = doc.root.elements?.compactMap(\.numberValue)
        #expect(values == [0, -1, 3.5, 1000, -0.025])
    }

    @Test("leading-zero numbers are rejected")
    func leadingZero() {
        #expect(throws: JSONParseError.self) { try JSONDocument.parse(#"{"value": 01}"#) }
        #expect(throws: JSONParseError.self) { try JSONDocument.parse(#"{"value": -01}"#) }
    }

    @Test("raw control characters in strings are rejected")
    func rawControlCharacter() {
        #expect(throws: JSONParseError.self) { try JSONDocument.parse("{\"value\": \"line\nfeed\"}") }
    }

    @Test("UTF-8 multibyte content keeps spans aligned")
    func multibyte() throws {
        // Spans are byte offsets; a naive character-index parser breaks here.
        let doc = try JSONDocument.parse(#"{"mesto": "Košice — Ťahanovce", "n": 1}"#)
        #expect(doc.root["mesto"]?.stringValue == "Košice — Ťahanovce")
        let n = try #require(doc.root.member("n"))
        #expect(doc.slice(n.value.span) == "1")
        #expect(doc.replacing(n.value.span, with: "2").contains("Košice — Ťahanovce"))
    }

    @Test("BOM is skipped")
    func byteOrderMark() throws {
        let doc = try JSONDocument.parse("\u{FEFF}{\"a\": 1}")
        #expect(doc.root["a"]?.numberValue == 1)
    }

    @Test("duplicate keys are preserved, not merged")
    func duplicateKeys() throws {
        let doc = try JSONDocument.parse(#"{"a": 1, "a": 2}"#)
        #expect(doc.root.keys == ["a", "a"])
        #expect(doc.root["a"]?.numberValue == 1)
    }
}

@Suite("JSONDocument — errors")
struct JSONErrorTests {

    @Test("unterminated string reports a position")
    func unterminatedString() {
        #expect(throws: JSONParseError.self) {
            try JSONDocument.parse(#"{"a": "oops}"#)
        }
    }

    @Test("trailing content after the root value is rejected")
    func trailingContent() {
        #expect(throws: JSONParseError.self) {
            try JSONDocument.parse(#"{"a": 1} {"b": 2}"#)
        }
    }

    @Test("unterminated block comment is rejected")
    func unterminatedComment() {
        #expect(throws: JSONParseError.self) {
            try JSONDocument.parse("{\"a\": 1} /* never closed")
        }
    }

    @Test("error carries line and column")
    func errorPosition() throws {
        do {
            _ = try JSONDocument.parse("{\n  \"a\": ,\n}")
            Issue.record("expected a parse error")
        } catch let error as JSONParseError {
            #expect(error.line == 2)
        }
    }
}
