import Foundation
import Testing
@testable import KyttoCore

/// The product risk for Codex is concentrated here (§12): a config.toml is not a
/// small file, and the servers are a few tables among dozens the user cares
/// about more. Every test below is really the same test — does everything Kytto
/// did not deliberately change come out the other side identical.
@Suite("TOMLDocument — spans")
struct TOMLSpanTests {

    @Test("every TOML fixture parses", arguments: Fixture.toml)
    func fixtureParses(_ fixture: Fixture) throws {
        _ = try TOMLDocument.parse(fixture.text)
    }

    /// Splicing a value's own text back over itself must be a no-op on the whole
    /// file. If this fails, a write would move or drop bytes elsewhere — which is
    /// the failure mode §6 exists to prevent.
    @Test("splicing a value over itself changes nothing", arguments: Fixture.toml)
    func spliceIdentity(_ fixture: Fixture) throws {
        let document = try TOMLDocument.parse(fixture.text)
        for table in document.tables {
            for span in [table.headerSpan, table.span].compactMap({ $0 }) {
                #expect(document.replacing(span, with: document.slice(span)) == document.sourceText)
            }
            for pair in table.pairs {
                for span in [pair.keySpan, pair.value.span, pair.span] {
                    #expect(
                        document.replacing(span, with: document.slice(span)) == document.sourceText,
                        "identity splice altered \(fixture.rawValue) at \(span.start)..<\(span.end)"
                    )
                }
            }
        }
    }

    /// A value's span must cover exactly that value: re-parsing the text it
    /// points at has to give the same thing back.
    @Test("spans point at their own value", arguments: Fixture.toml)
    func spansAreAccurate(_ fixture: Fixture) throws {
        let document = try TOMLDocument.parse(fixture.text)
        for table in document.tables {
            for pair in table.pairs {
                let slice = document.slice(pair.span)
                let reparsed = try TOMLDocument.parse(slice)
                let recovered = try #require(reparsed.tables.first?.pairs.first)
                #expect(recovered.key == pair.key)
                #expect(recovered.value.stringValue == pair.value.stringValue)
                #expect(recovered.value.boolValue == pair.value.boolValue)
                #expect(
                    recovered.value.elements?.compactMap(\.stringValue)
                        == pair.value.elements?.compactMap(\.stringValue)
                )
            }
        }
    }
}

@Suite("TOMLDocument — reading")
struct TOMLReadingTests {

    private func document() throws -> TOMLDocument {
        try TOMLDocument.parse(Fixture.codexConfig.text)
    }

    @Test("a bracket inside a multi-line string is not a table header")
    func multilineStringIsNotStructure() throws {
        let document = try document()
        #expect(document.table(at: ["not", "a", "table"]) == nil)

        let notify = try #require(document.tables.first?.pair("notify"))
        #expect(notify.value.stringValue?.contains("[not.a.table]") == true)
    }

    @Test("a multi-line array is one value, not three lines")
    func multilineArray() throws {
        let document = try document()
        let permissions = try #require(document.tables.first?.value("sandbox_permissions"))
        #expect(permissions.elements?.compactMap(\.stringValue) == ["disk-full-read-access", "disk-write-cwd"])
    }

    @Test("servers are found, and only servers")
    func serverNames() throws {
        let document = try document()
        #expect(document.serverNames(under: "mcp_servers") == ["filesystem", "legacy-tool", "docs"])
        // The env sub-table is part of a server, not a server of its own.
        #expect(!document.serverNames(under: "mcp_servers").contains("env"))
    }

    @Test("a server's fields, including an env sub-table")
    func fields() throws {
        let document = try document()
        let table = try #require(document.table(at: ["mcp_servers", "filesystem"]))
        let env = document.table(at: ["mcp_servers", "filesystem", "env"])
        let fields = ServerFields(table: table, envTable: env)

        #expect(fields.command == "npx")
        #expect(fields.args == ["-y", "@modelcontextprotocol/server-filesystem", "/Users/someone/Projects"])
        #expect(fields.env.map(\.key) == ["FS_ALLOW_WRITE"])
        #expect(fields.env.first?.value == "1")
    }

    @Test("a URL server is remote, a command server is stdio")
    func transports() throws {
        let document = try document()

        let docs = try #require(document.table(at: ["mcp_servers", "docs"]))
        let remote = ServerFields(table: docs, envTable: nil)
            .makeServer(name: "docs", origin: .configFile(.codex), isBundled: false, sourceText: "")
        #expect(remote.transport == .http)
        #expect(remote.url == "https://developers.openai.com/mcp")

        let fs = try #require(document.table(at: ["mcp_servers", "filesystem"]))
        let local = ServerFields(table: fs, envTable: nil)
            .makeServer(name: "filesystem", origin: .configFile(.codex), isBundled: false, sourceText: "")
        #expect(local.transport == .stdio)
    }

    @Test("absent means on; only a written false means off")
    func enablement() throws {
        let document = try document()
        #expect(document.table(at: ["mcp_servers", "filesystem"])?.value("enabled")?.boolValue == nil)
        #expect(document.table(at: ["mcp_servers", "legacy-tool"])?.value("enabled")?.boolValue == false)
    }
}

@Suite("TOMLDocument — round trips")
struct TOMLRoundTripTests {

    private func document() throws -> TOMLDocument {
        try TOMLDocument.parse(Fixture.codexConfig.text)
    }

    /// The assertion that matters most: everything else is where it was.
    private func expectUntouchedSurroundings(_ updated: String) throws {
        let reparsed = try TOMLDocument.parse(updated)
        #expect(reparsed.table(at: ["projects", "/Users/someone/Desktop/a project"]) != nil)
        #expect(reparsed.table(at: ["projects", "/Users/someone/Projects/Xcode/Thing"]) != nil)
        #expect(reparsed.table(at: ["shell_environment_policy", "set"])?.value("LANG")?.stringValue == "en_US.UTF-8")
        #expect(reparsed.table(at: ["history"])?.value("persistence")?.stringValue == "save-all")
        #expect(reparsed.tables.first?.value("model")?.stringValue == "gpt-5-codex")
        #expect(updated.contains("# Codex configuration."))
        #expect(updated.contains("[not.a.table]"))
    }

    @Test("switching off writes one line and moves nothing")
    func disable() throws {
        let document = try document()
        let updated = try document.settingServerFlag(
            false, key: "enabled", forServer: "filesystem", under: "mcp_servers"
        )

        // Exactly one line added, and removing it again gives back the original
        // byte for byte. A set difference would not catch this: `enabled = false`
        // is already in the file on another server.
        let before = document.sourceText.components(separatedBy: "\n")
        let after = updated.components(separatedBy: "\n")
        #expect(after.count == before.count + 1)

        let firstChange = try #require(
            zip(before, after).enumerated().first { $0.element.0 != $0.element.1 }?.offset
        )
        #expect(after[firstChange] == "enabled = false")
        var withoutInsertion = after
        withoutInsertion.remove(at: firstChange)
        #expect(withoutInsertion == before)

        let reparsed = try TOMLDocument.parse(updated)
        #expect(reparsed.table(at: ["mcp_servers", "filesystem"])?.value("enabled")?.boolValue == false)
        // The new key must land on the server's table, not inside its env.
        #expect(reparsed.table(at: ["mcp_servers", "filesystem", "env"])?.pairs.count == 1)
        try expectUntouchedSurroundings(updated)
    }

    @Test("switching on rewrites the flag that is already there")
    func enable() throws {
        let document = try document()
        let updated = try document.settingServerFlag(
            true, key: "enabled", forServer: "legacy-tool", under: "mcp_servers"
        )

        #expect(updated.components(separatedBy: "\n").count == document.sourceText.components(separatedBy: "\n").count)
        let reparsed = try TOMLDocument.parse(updated)
        #expect(reparsed.table(at: ["mcp_servers", "legacy-tool"])?.value("enabled")?.boolValue == true)
        try expectUntouchedSurroundings(updated)
    }

    @Test("removing a server takes its env sub-table with it")
    func remove() throws {
        let document = try document()
        let updated = try document.removingServer("filesystem", under: "mcp_servers")
        let reparsed = try TOMLDocument.parse(updated)

        #expect(reparsed.table(at: ["mcp_servers", "filesystem"]) == nil)
        #expect(reparsed.table(at: ["mcp_servers", "filesystem", "env"]) == nil)
        #expect(reparsed.serverNames(under: "mcp_servers") == ["legacy-tool", "docs"])
        #expect(!updated.contains("FS_ALLOW_WRITE"))
        // No hole where it used to be.
        #expect(!updated.contains("\n\n\n"))
        try expectUntouchedSurroundings(updated)
    }

    @Test("a removed server can be put back exactly as it was")
    func parkAndRestore() throws {
        let document = try document()
        let parked = try #require(document.serverDefinitionText("filesystem", under: "mcp_servers"))

        let without = try document.removingServer("filesystem", under: "mcp_servers")
        let restored = try TOMLDocument.parse(without)
            .settingServer("filesystem", under: "mcp_servers", to: parked)

        let reparsed = try TOMLDocument.parse(restored)
        let table = try #require(reparsed.table(at: ["mcp_servers", "filesystem"]))
        #expect(table.value("command")?.stringValue == "npx")
        #expect(table.value("startup_timeout_sec") != nil)
        #expect(reparsed.table(at: ["mcp_servers", "filesystem", "env"])?.value("FS_ALLOW_WRITE")?.stringValue == "1")
        try expectUntouchedSurroundings(restored)
    }

    @Test("a new server joins the others rather than landing at the end")
    func addServer() throws {
        let document = try document()
        let block = TOMLBuilder.serverBlock(
            name: "github",
            under: "mcp_servers",
            command: "npx",
            args: ["-y", "@modelcontextprotocol/server-github"],
            url: nil,
            env: [EnvEntry(key: "GITHUB_TOKEN", value: "ghp_example")],
            enabled: nil
        )
        let updated = try document.settingServer("github", under: "mcp_servers", to: block)
        let reparsed = try TOMLDocument.parse(updated)

        #expect(reparsed.serverNames(under: "mcp_servers") == ["filesystem", "legacy-tool", "docs", "github"])
        #expect(reparsed.table(at: ["mcp_servers", "github", "env"])?.value("GITHUB_TOKEN")?.stringValue == "ghp_example")
        // Grouped with the servers, so `[history]` is still the last table.
        #expect(reparsed.tables.last?.path == ["history"])
        try expectUntouchedSurroundings(updated)
    }

    @Test("adding to a file with no servers at all still produces valid TOML")
    func addToEmptyish() throws {
        let document = try TOMLDocument.parse("model = \"gpt-5-codex\"\n")
        let block = TOMLBuilder.serverBlock(
            name: "memory", under: "mcp_servers",
            command: "npx", args: ["-y", "@modelcontextprotocol/server-memory"],
            url: nil, env: [], enabled: nil
        )
        let updated = try document.settingServer("memory", under: "mcp_servers", to: block)
        let reparsed = try TOMLDocument.parse(updated)

        #expect(reparsed.serverNames(under: "mcp_servers") == ["memory"])
        #expect(reparsed.tables.first?.value("model")?.stringValue == "gpt-5-codex")
    }

    @Test("a name that is not a bare key is quoted")
    func awkwardName() throws {
        let block = TOMLBuilder.serverBlock(
            name: "my server.v2", under: "mcp_servers",
            command: "run", args: [], url: nil, env: [], enabled: false
        )
        #expect(block.hasPrefix("[mcp_servers.\"my server.v2\"]"))

        let document = try TOMLDocument.parse("")
        let updated = try document.settingServer("my server.v2", under: "mcp_servers", to: block)
        let reparsed = try TOMLDocument.parse(updated)
        #expect(reparsed.serverNames(under: "mcp_servers") == ["my server.v2"])
        #expect(reparsed.table(at: ["mcp_servers", "my server.v2"])?.value("enabled")?.boolValue == false)
    }

    @Test("an inline definition is read but refused for editing")
    func inlineDefinition() throws {
        let document = try TOMLDocument.parse("""
        [mcp_servers]
        inline = { command = "run", args = ["x"] }
        """)

        #expect(document.serverNames(under: "mcp_servers") == ["inline"])
        #expect(throws: TOMLEditError.inlineDefinition("inline")) {
            try document.removingServer("inline", under: "mcp_servers")
        }
        #expect(throws: TOMLEditError.inlineDefinition("inline")) {
            try document.settingServer("inline", under: "mcp_servers", to: "[mcp_servers.inline]")
        }
    }

    @Test("CRLF survives a write")
    func windowsLineEndings() throws {
        let source = "model = \"x\"\r\n\r\n[mcp_servers.a]\r\ncommand = \"run\"\r\n\r\n[history]\r\nkeep = 1\r\n"
        let document = try TOMLDocument.parse(source)
        let updated = try document.settingServerFlag(false, key: "enabled", forServer: "a", under: "mcp_servers")

        let reparsed = try TOMLDocument.parse(updated)
        #expect(reparsed.table(at: ["mcp_servers", "a"])?.value("enabled")?.boolValue == false)
        #expect(reparsed.table(at: ["history"])?.value("keep") != nil)
        #expect(updated.contains("\r\n"))
    }
}
