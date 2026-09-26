import Foundation
import Testing
@testable import KyttoCore

/// Codex end to end, through the real pipeline rather than the document layer.
///
/// The interesting part is not that TOML can be edited — `TOMLDocumentTests`
/// covers that — but that a client spelled differently from the other four does
/// not break the assumptions the other four were written under. The one that
/// mattered: copying a server between clients used to move its source text
/// across, which silently assumed every client spells a definition the same way.
@Suite("Codex")
struct CodexTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        return harness
    }

    // MARK: - Discovery

    @Test("servers are found, with the flag deciding which are on")
    func discovery() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let result = harness.discover()
        let codex = try #require(result.clients.first { $0.id == .codex })
        #expect(codex.displayName == "Codex")
        #expect(codex.isInstalled)
        #expect(codex.configExists)
        #expect(codex.serverCount == 3)

        let filesystem = try #require(result.servers.first { $0.name == "filesystem" })
        #expect(filesystem.enabledIn[.codex] == .enabled)
        #expect(filesystem.command == "npx")
        #expect(filesystem.env.first?.key == "FS_ALLOW_WRITE")

        let legacy = try #require(result.servers.first { $0.name == "legacy-tool" })
        #expect(legacy.enabledIn[.codex] == .disabled)

        let docs = try #require(result.servers.first { $0.name == "docs" })
        #expect(docs.transport == .http)
    }

    @Test("a plugin's server is shown, and marked as installed software")
    func pluginServers() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        try harness.home.write(
            """
            {
              "mcpServers": {
                "xcodebuildmcp": {
                  "command": "npx",
                  "args": ["-y", "xcodebuildmcp@latest", "mcp"]
                }
              }
            }
            """,
            to: ".codex/plugins/cache/openai-curated/build-ios-apps/abc123/.mcp.json"
        )

        let server = try #require(harness.discover().servers.first { $0.name == "xcodebuildmcp" })
        #expect(server.isBundled)
        #expect(server.enabledIn[.codex] == .enabled)

        // A plugin lives in a cache the client repopulates. Copying it elsewhere
        // would produce a config pointing into that cache, so it is refused for
        // the same reason a Claude Desktop extension is.
        #expect(throws: ToggleError.bundledServerNotPortable("xcodebuildmcp")) {
            try harness.toggles.setEnabled(true, server: server, in: .cursor)
        }

        // And it cannot be switched off in Codex either: there is nothing in
        // config.toml to write a flag onto, and quietly doing nothing would be a
        // toggle that lies.
        let before = try harness.text(".codex/config.toml")
        #expect(throws: ToggleError.bundledServerNotToggleable("xcodebuildmcp")) {
            try harness.toggles.setEnabled(false, server: server, in: .codex)
        }
        #expect(try harness.text(".codex/config.toml") == before)
    }

    // MARK: - Toggling

    @Test("switching off writes the flag and parks nothing")
    func disable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let filesystem = try harness.server("filesystem")
        let result = try harness.toggles.setEnabled(false, server: filesystem, in: .codex)

        #expect(result.enabled == false)
        // The definition never left the file, so there was nothing to park.
        #expect(result.wasParked == false)
        #expect(harness.parkStore.all().isEmpty)
        #expect(result.backupID != nil)

        let text = try harness.text(".codex/config.toml")
        #expect(text.contains("FS_ALLOW_WRITE"))
        #expect(try TOMLDocument.parse(text)
            .table(at: ["mcp_servers", "filesystem"])?.value("enabled")?.boolValue == false)

        // And the rest of somebody's config is exactly where it was.
        #expect(text.contains("[projects.\"/Users/someone/Desktop/a project\"]"))
        #expect(text.contains("# Codex configuration."))
        #expect(harness.discover().servers.first { $0.name == "filesystem" }?.enabledIn[.codex] == .disabled)
    }

    @Test("switching back on flips the same flag")
    func enable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let legacy = try harness.server("legacy-tool")
        _ = try harness.toggles.setEnabled(true, server: legacy, in: .codex)

        #expect(harness.discover().servers.first { $0.name == "legacy-tool" }?.enabledIn[.codex] == .enabled)
        #expect(try harness.text(".codex/config.toml").contains("/usr/local/bin/legacy-mcp"))
    }

    @Test("setting a flag to what it already is writes nothing")
    func noOp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".codex/config.toml")
        let docs = try harness.server("docs")
        let result = try harness.toggles.setEnabled(true, server: docs, in: .codex)

        #expect(result.backupID == nil)
        #expect(try harness.text(".codex/config.toml") == before)
    }

    // MARK: - Definitions crossing formats

    @Test("a Codex server copied into a JSON client is rendered as JSON")
    func copyOutOfCodex() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")

        let filesystem = try harness.server("filesystem")
        _ = try harness.toggles.setEnabled(true, server: filesystem, in: .cursor)

        let text = try harness.text(".cursor/mcp.json")
        // The give-away of the bug this guards: a TOML table header spliced into
        // a JSON file.
        #expect(!text.contains("[mcp_servers"))

        let document = try JSONDocument.parse(text)
        let entry = try #require(document.value(at: ["mcpServers", "filesystem"]))
        #expect(entry["command"]?.stringValue == "npx")
        #expect(entry["args"]?.elements?.compactMap(\.stringValue).first == "-y")
        #expect(entry.value(at: ["env", "FS_ALLOW_WRITE"])?.stringValue == "1")
    }

    @Test("a JSON client's server copied into Codex is rendered as TOML")
    func copyIntoCodex() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")

        let cursorServer = try #require(
            harness.discover().servers.first { $0.enabledIn[.cursor] == .enabled && !$0.isBundled }
        )
        _ = try harness.toggles.setEnabled(true, server: cursorServer, in: .codex)

        let text = try harness.text(".codex/config.toml")
        #expect(!text.contains("\"mcpServers\""))

        let document = try TOMLDocument.parse(text)
        #expect(document.serverNames(under: "mcp_servers").contains(cursorServer.name))
        let table = try #require(document.table(at: ["mcp_servers", cursorServer.name]))
        #expect(table.value("command")?.stringValue == cursorServer.command)
        // Added switched on, and absent is how TOML says on.
        #expect(table.value("enabled") == nil)
        #expect(text.contains("[projects.\"/Users/someone/Desktop/a project\"]"))
    }

    // MARK: - Authoring

    private func authoring(_ harness: ToggleHarness) -> ServerAuthoring {
        ServerAuthoring(
            home: harness.home.root,
            writer: ConfigWriter(backups: harness.backups),
            parkStore: harness.parkStore,
            ledger: harness.ledger
        )
    }

    @Test("a server can be created in Codex")
    func create() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let draft = ServerDraft(
            name: "memory",
            transport: .stdio,
            command: "npx",
            args: ["-y", "@modelcontextprotocol/server-memory"],
            env: [EnvEntry(key: "MEMORY_PATH", value: "/tmp/memory.json")]
        )
        let result = try authoring(harness).create(draft, in: [.codex], existing: harness.discover().servers)
        #expect(result.changed == [.codex])

        let document = try TOMLDocument.parse(try harness.text(".codex/config.toml"))
        #expect(document.serverNames(under: "mcp_servers") == ["filesystem", "legacy-tool", "docs", "memory"])
        #expect(document.table(at: ["mcp_servers", "memory", "env"])?
            .value("MEMORY_PATH")?.stringValue == "/tmp/memory.json")
        #expect(document.table(at: ["history"])?.value("persistence")?.stringValue == "save-all")
    }

    @Test("editing a switched-off server leaves it switched off")
    func editKeepsDisabled() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let legacy = try harness.server("legacy-tool")
        var draft = ServerDraft(editing: legacy)
        draft.command = "/opt/homebrew/bin/legacy-mcp"

        _ = try authoring(harness).update(
            draft, originalName: legacy.name, server: legacy, existing: harness.discover().servers
        )

        let document = try TOMLDocument.parse(try harness.text(".codex/config.toml"))
        let table = try #require(document.table(at: ["mcp_servers", "legacy-tool"]))
        #expect(table.value("command")?.stringValue == "/opt/homebrew/bin/legacy-mcp")
        // The point of the test: rewriting the block must not turn it back on.
        #expect(table.value("enabled")?.boolValue == false)
        #expect(harness.discover().servers.first { $0.name == "legacy-tool" }?.enabledIn[.codex] == .disabled)
    }

    @Test("renaming moves the table and takes its env with it")
    func rename() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let filesystem = try harness.server("filesystem")
        var draft = ServerDraft(editing: filesystem)
        draft.name = "files"

        _ = try authoring(harness).update(
            draft, originalName: "filesystem", server: filesystem, existing: harness.discover().servers
        )

        let document = try TOMLDocument.parse(try harness.text(".codex/config.toml"))
        #expect(document.table(at: ["mcp_servers", "filesystem"]) == nil)
        #expect(document.table(at: ["mcp_servers", "filesystem", "env"]) == nil)
        #expect(document.table(at: ["mcp_servers", "files", "env"])?
            .value("FS_ALLOW_WRITE")?.stringValue == "1")
    }

    @Test("deleting removes both of a server's tables")
    func delete() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let filesystem = try harness.server("filesystem")
        _ = try authoring(harness).delete(filesystem)

        let text = try harness.text(".codex/config.toml")
        #expect(!text.contains("FS_ALLOW_WRITE"))
        let document = try TOMLDocument.parse(text)
        #expect(document.serverNames(under: "mcp_servers") == ["legacy-tool", "docs"])
        #expect(document.table(at: ["shell_environment_policy", "set"]) != nil)
    }

    @Test("a client with no config yet gets one that is valid TOML")
    func firstEverServer() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }

        let draft = ServerDraft(name: "memory", transport: .stdio, command: "npx", args: ["-y", "server-memory"])
        let result = try authoring(harness).create(draft, in: [.codex], existing: [])

        let document = try TOMLDocument.parse(try harness.text(".codex/config.toml"))
        #expect(document.serverNames(under: "mcp_servers") == ["memory"])
        #expect(result.changed == [.codex])
        #expect(result.backupIDs.isEmpty)
    }
}
