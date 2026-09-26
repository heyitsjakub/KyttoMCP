import Foundation
import Testing
@testable import KyttoCore

// README.md §8 on M2: "Write tests that assert unrelated keys survive a round
// trip." That is this file. Breaking someone's config is the one mistake that
// gets the app deleted, and every case below is a way to break one.

@Suite("JSONEditor — removing members")
struct RemoveMemberTests {

    private func remove(_ key: String, from source: String, at path: [String] = []) throws -> String {
        try JSONDocument.parse(source).removingMember(key, at: path)
    }

    @Test("first of several takes its own comma and line")
    func firstOfSeveral() throws {
        let result = try remove("a", from: """
        {
          "a": 1,
          "b": 2
        }
        """)
        #expect(result == """
        {
          "b": 2
        }
        """)
    }

    @Test("last of several takes the preceding comma and leaves the closing line alone")
    func lastOfSeveral() throws {
        let result = try remove("b", from: """
        {
          "a": 1,
          "b": 2
        }
        """)
        #expect(result == """
        {
          "a": 1
        }
        """)
    }

    @Test("middle of several")
    func middleOfSeveral() throws {
        let result = try remove("b", from: """
        {
          "a": 1,
          "b": 2,
          "c": 3
        }
        """)
        #expect(result == """
        {
          "a": 1,
          "c": 3
        }
        """)
    }

    @Test("the only member leaves a valid, empty object")
    func onlyMember() throws {
        let result = try remove("a", from: """
        {
          "a": 1
        }
        """)
        let reparsed = try JSONDocument.parse(result)
        #expect(reparsed.root.members?.isEmpty == true)
    }

    @Test("single-line objects stay on one line")
    func singleLine() throws {
        #expect(try remove("b", from: #"{"a": 1, "b": 2}"#) == #"{"a": 1}"#)
        #expect(try remove("a", from: #"{"a": 1, "b": 2}"#) == #"{"b": 2}"#)
    }

    @Test("tab-indented files stay tab-indented")
    func tabs() throws {
        let result = try remove("b", from: "{\n\t\"a\": 1,\n\t\"b\": 2\n}")
        #expect(result == "{\n\t\"a\": 1\n}")
    }

    @Test("CRLF line endings survive")
    func crlf() throws {
        let result = try remove("a", from: "{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}")
        #expect(result == "{\r\n  \"b\": 2\r\n}")
    }

    @Test("removing a nested member leaves the parent alone")
    func nested() throws {
        let result = try remove("github", from: """
        {
          "mcpServers": {
            "github": { "command": "npx" },
            "postgres": { "command": "psql" }
          },
          "theme": "dark"
        }
        """, at: ["mcpServers"])
        #expect(result == """
        {
          "mcpServers": {
            "postgres": { "command": "psql" }
          },
          "theme": "dark"
        }
        """)
    }

    @Test("a missing key is not an error — the end state is already true")
    func missingKey() throws {
        let source = #"{"a": 1}"#
        #expect(try remove("zzz", from: source) == source)
    }

    @Test("comments on surrounding members are not collateral damage")
    func commentsSurvive() throws {
        let result = try remove("b", from: """
        {
          // keep this
          "a": 1,
          "b": 2,
          // and this
          "c": 3
        }
        """)
        #expect(result.contains("// keep this"))
        #expect(result.contains("// and this"))
        #expect(!result.contains("\"b\""))
    }
}

@Suite("JSONEditor — setting members")
struct SetMemberTests {

    @Test("appending matches the indentation of its siblings")
    func appendMultiline() throws {
        let document = try JSONDocument.parse("""
        {
          "a": 1
        }
        """)
        #expect(try document.settingMember("b", at: [], to: "2") == """
        {
          "a": 1,
          "b": 2
        }
        """)
    }

    @Test("appending to a single-line object stays on the line")
    func appendSingleLine() throws {
        let document = try JSONDocument.parse(#"{"a": 1}"#)
        #expect(try document.settingMember("b", at: [], to: "2") == #"{"a": 1, "b": 2}"#)
    }

    @Test("an existing key has only its value replaced")
    func replaceExisting() throws {
        let document = try JSONDocument.parse("""
        {
          "isEnabled": true,
          "userConfig": { "a": 1 }
        }
        """)
        #expect(try document.settingMember("isEnabled", at: [], to: "false") == """
        {
          "isEnabled": false,
          "userConfig": { "a": 1 }
        }
        """)
    }

    @Test("an empty object gets opened up with the file's own indent unit")
    func fillEmptyObject() throws {
        let document = try JSONDocument.parse("""
        {
          "mcpServers": {},
          "theme": "dark"
        }
        """)
        #expect(try document.settingMember("github", at: ["mcpServers"], to: #"{ "command": "npx" }"#) == """
        {
          "mcpServers": {
            "github": { "command": "npx" }
          },
          "theme": "dark"
        }
        """)
    }

    @Test("a missing key is created on the way down")
    func createsMissingPath() throws {
        // Claude Desktop configs routinely have no mcpServers key at all.
        let document = try JSONDocument.parse("""
        {
          "preferences": { "theme": "dark" }
        }
        """)
        let result = try document.settingMember("github", at: ["mcpServers"], to: #"{ "command": "npx" }"#)
        let reparsed = try JSONDocument.parse(result)
        #expect(reparsed.value(at: ["mcpServers", "github", "command"])?.stringValue == "npx")
        #expect(reparsed.value(at: ["preferences", "theme"])?.stringValue == "dark")
    }

    @Test("tab-indented files get tab-indented insertions")
    func tabInsertion() throws {
        let document = try JSONDocument.parse("{\n\t\"a\": 1\n}")
        #expect(try document.settingMember("b", at: [], to: "2") == "{\n\t\"a\": 1,\n\t\"b\": 2\n}")
    }
}

@Suite("JSONEditor — arrays")
struct ArrayEditTests {

    @Test("appending to a populated array")
    func append() throws {
        let document = try JSONDocument.parse("""
        {
          "deniedMcpServers": [
            { "serverName": "a" }
          ]
        }
        """)
        let result = try document.appendingElement(
            #"{ "serverName": "b" }"#,
            toArrayAt: ["deniedMcpServers"]
        )
        #expect(result == """
        {
          "deniedMcpServers": [
            { "serverName": "a" },
            { "serverName": "b" }
          ]
        }
        """)
    }

    @Test("appending to an empty array")
    func appendEmpty() throws {
        let document = try JSONDocument.parse("""
        {
          "deniedMcpServers": []
        }
        """)
        let result = try document.appendingElement(
            #"{ "serverName": "a" }"#,
            toArrayAt: ["deniedMcpServers"]
        )
        #expect(result == """
        {
          "deniedMcpServers": [
            { "serverName": "a" }
          ]
        }
        """)
    }

    @Test("appending creates the array when the key is absent")
    func appendCreatesKey() throws {
        let document = try JSONDocument.parse("""
        {
          "model": "opus"
        }
        """)
        let result = try document.appendingElement(
            #"{ "serverName": "a" }"#,
            toArrayAt: ["deniedMcpServers"]
        )
        let reparsed = try JSONDocument.parse(result)
        #expect(reparsed.value(at: ["deniedMcpServers"])?.elements?.count == 1)
        #expect(reparsed.value(at: ["model"])?.stringValue == "opus")
    }

    @Test("removing a matching element")
    func removeMatching() throws {
        let document = try JSONDocument.parse("""
        {
          "deniedMcpServers": [
            { "serverName": "a" },
            { "serverName": "b" }
          ]
        }
        """)
        let result = try document.removingElements(fromArrayAt: ["deniedMcpServers"]) {
            $0["serverName"]?.stringValue == "a"
        }
        #expect(result == """
        {
          "deniedMcpServers": [
            { "serverName": "b" }
          ]
        }
        """)
    }

    @Test("removing every element leaves a valid empty array")
    func removeAll() throws {
        let document = try JSONDocument.parse("""
        {
          "deniedMcpServers": [
            { "serverName": "a" },
            { "serverName": "b" }
          ]
        }
        """)
        let result = try document.removingElements(fromArrayAt: ["deniedMcpServers"]) { _ in true }
        let reparsed = try JSONDocument.parse(result)
        #expect(reparsed.value(at: ["deniedMcpServers"])?.elements?.isEmpty == true)
    }
}

@Suite("JSONEditor — real configs")
struct RealConfigEditTests {

    /// The assertion README.md §8 asks for, against the shapes that actually exist
    /// on disk: change one server, and nothing else in the file moves.
    @Test("editing one server preserves everything else", arguments: [
        Fixture.claudeDesktopWithServers,
        Fixture.cursorMCP,
    ])
    func unrelatedKeysSurvive(_ fixture: Fixture) throws {
        let original = try fixture.document()
        let serversKey = "mcpServers"
        let names = try #require(original.value(at: [serversKey])?.keys)

        for name in names {
            let edited = try original.removingMember(name, at: [serversKey])
            let reparsed = try JSONDocument.parse(edited)

            // Every other server is still there, in order.
            #expect(reparsed.value(at: [serversKey])?.keys == names.filter { $0 != name })

            // Every top-level key the file had, it still has, in the same order.
            #expect(reparsed.root.keys == original.root.keys)

            // And the bytes outside the servers map are untouched.
            let mapSpan = try #require(original.value(at: [serversKey])?.span)
            #expect(edited.hasPrefix(String(original.sourceText.prefix(mapSpan.start))))
        }
    }

    @Test("a Claude Desktop config with no mcpServers gains one cleanly")
    func addToConfigWithoutServersKey() throws {
        let original = try Fixture.claudeDesktopNoServers.document()
        #expect(original.value(at: ["mcpServers"]) == nil)

        let edited = try original.settingMember(
            "github",
            at: ["mcpServers"],
            to: #"{ "command": "npx", "args": ["-y", "server-github"] }"#
        )
        let reparsed = try JSONDocument.parse(edited)

        #expect(reparsed.value(at: ["mcpServers", "github", "command"])?.stringValue == "npx")
        // Everything the file already had survives, in order.
        #expect(reparsed.root.keys == original.root.keys + ["mcpServers"])
        #expect(reparsed.value(at: ["preferences", "chromeExtensionEnabled"])?.boolValue == true)
    }

    @Test("VS Code's JSONC keeps its comments through an edit")
    func vscodeCommentsSurvive() throws {
        let original = try Fixture.vscodeMCP.document()
        let edited = try original.removingMember("playwright", at: ["servers"])

        #expect(edited.contains("// User-level MCP configuration for VS Code."))
        #expect(edited.contains("/* Servers are keyed by name"))
        #expect(edited.contains("// Inputs are prompted for on first use."))

        let reparsed = try JSONDocument.parse(edited)
        #expect(reparsed.value(at: ["servers"])?.keys == ["github"])
        #expect(reparsed.value(at: ["inputs"]) != nil)
    }

    @Test("a 6000-line ~/.claude.json edit touches only the servers map")
    func largeFile() throws {
        let original = try Fixture.claudeCode.document()
        let edited = try original.removingMember("context7", at: ["mcpServers"])
        let reparsed = try JSONDocument.parse(edited)

        #expect(reparsed.value(at: ["mcpServers"])?.keys == ["XcodeBuildMCP"])
        // Project scope is out of v1, and an edit must not disturb it.
        #expect(reparsed.value(at: ["projects"])?.keys.count == 2)
        #expect(
            reparsed.value(at: [
                "projects", "/Users/example/Projects/Xcode/KyttoMCP",
                "mcpServers", "project-only-server", "command",
            ])?.stringValue == "node"
        )
        #expect(reparsed.value(at: ["numStartups"])?.numberValue == 560)
    }

    @Test("round trip: remove then re-add restores the file exactly")
    func removeThenReAdd() throws {
        let original = try Fixture.cursorMCP.document()
        let member = try #require(original.value(at: ["mcpServers"])?.member("figma"))
        // Parking stores the definition's original source text, so putting it
        // back is byte-for-byte, not a re-serialization.
        let parked = original.slice(member.value.span)

        let removed = try original.removingMember("figma", at: ["mcpServers"])
        let restored = try JSONDocument.parse(removed).settingMember("figma", at: ["mcpServers"], to: parked)

        #expect(restored == original.sourceText)
    }
}
