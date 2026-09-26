import Foundation
import Testing
@testable import KyttoCore

extension ToggleHarness {
    var authoring: ServerAuthoring {
        ServerAuthoring(
            home: home.root,
            writer: ConfigWriter(backups: backups),
            parkStore: parkStore,
            ledger: ledger
        )
    }
}

private let claudeSupport = "Library/Application Support/Claude"

@Suite("JSONBuilder")
struct JSONBuilderTests {

    @Test("short scalar arrays stay on one line")
    func inlineArray() {
        let value = JSONBuildValue.array([.string("-y"), .string("pkg")])
        #expect(JSONBuilder.render(value) == #"["-y", "pkg"]"#)
    }

    @Test("long arrays break one element per line")
    func blockArray() {
        let value = JSONBuildValue.array([
            .string("--a-rather-long-argument-value"),
            .string("--and-another-long-one-here"),
        ])
        #expect(JSONBuilder.render(value).contains("\n"))
    }

    @Test("nested objects indent from the base")
    func nested() {
        let value = JSONBuildValue.object([
            .init("command", .string("npx")),
            .init("env", .object([.init("KEY", .string("value"))])),
        ])
        #expect(JSONBuilder.render(value, baseIndent: "  ", unit: "  ") == """
        {
            "command": "npx",
            "env": {
              "KEY": "value"
            }
          }
        """)
    }

    @Test("whole numbers do not gain a decimal point")
    func numbers() {
        #expect(JSONBuilder.render(.number(3)) == "3")
        #expect(JSONBuilder.render(.number(3.5)) == "3.5")
    }

    @Test("rendered output parses back to what went in")
    func roundTrip() throws {
        let value = JSONBuildValue.object([
            .init("command", .string("npx")),
            .init("args", .array([.string("-y"), .string("pkg")])),
            .init("env", .object([.init("TOKEN", .string("a \"quoted\" value\nwith newline"))])),
        ])
        let document = try JSONDocument.parse(JSONBuilder.render(value))
        #expect(document.root["command"]?.stringValue == "npx")
        #expect(document.root["args"]?.elements?.count == 2)
        #expect(document.value(at: ["env", "TOKEN"])?.stringValue == "a \"quoted\" value\nwith newline")
    }
}

@Suite("ServerDraft validation")
struct ServerDraftTests {

    @Test("a stdio server needs a name and a command")
    func stdioRequirements() {
        #expect(ServerDraft().validate().contains(.nameEmpty))
        #expect(ServerDraft(name: "x").validate().contains(.commandEmpty))
        #expect(ServerDraft(name: "x", command: "npx").validate().isEmpty)
    }

    @Test("names with spaces are rejected — they become config keys")
    func nameWhitespace() {
        #expect(ServerDraft(name: "my server", command: "npx").validate().contains(.nameHasWhitespace))
    }

    @Test("an http server needs a usable URL")
    func httpRequirements() {
        var draft = ServerDraft(name: "remote", transport: .http)
        #expect(draft.validate().contains(.urlEmpty))
        draft.url = "not a url"
        #expect(draft.validate().contains(.urlInvalid("not a url")))
        for invalid in ["httpx://example.com/mcp", "https:", "http:///mcp", "ftp://example.com/mcp"] {
            draft.url = invalid
            #expect(draft.validate().contains(.urlInvalid(invalid)))
        }
        draft.url = "https://example.com/mcp"
        #expect(draft.validate().isEmpty)
    }

    @Test("environment keys use the portable process-environment spelling")
    func environmentKeys() {
        for valid in ["API_KEY", "_PRIVATE", "KEY2"] {
            let draft = ServerDraft(name: "x", command: "npx", env: [.init(key: valid, value: "1")])
            #expect(draft.validate().isEmpty)
        }
        for invalid in ["2KEY", "API KEY", "API=KEY", "KEY-NAME"] {
            let draft = ServerDraft(name: "x", command: "npx", env: [.init(key: invalid, value: "1")])
            #expect(draft.validate().contains(.envKeyInvalid(invalid)))
        }
    }

    @Test("duplicate names are caught, but a server does not collide with itself")
    func duplicates() {
        let draft = ServerDraft(name: "github", command: "npx")
        #expect(draft.validate(takenNames: ["github"]).contains(.nameTaken("github")))
        #expect(draft.validate(takenNames: ["github"], allowing: "github").isEmpty)
    }

    @Test("stdio definitions omit type; http declares it")
    func rendering() throws {
        let stdio = ServerDraft(name: "x", command: "npx", args: ["-y", "pkg"])
        let stdioText = stdio.definition(baseIndent: "", unit: "  ")
        #expect(!stdioText.contains("\"type\""), "every client reads a bare command as stdio")
        #expect(try JSONDocument.parse(stdioText).root["args"]?.elements?.count == 2)

        let http = ServerDraft(name: "x", transport: .http, url: "https://example.com/mcp")
        let httpText = http.definition(baseIndent: "", unit: "  ")
        #expect(try JSONDocument.parse(httpText).root["type"]?.stringValue == "http")
    }

    @Test("empty env entries are dropped rather than written as blanks")
    func emptyEnv() throws {
        let draft = ServerDraft(
            name: "x",
            command: "npx",
            env: [EnvEntry(key: "  ", value: "ignored"), EnvEntry(key: "REAL", value: "1")]
        )
        let document = try JSONDocument.parse(draft.definition(baseIndent: "", unit: "  "))
        #expect(document.root["env"]?.keys == ["REAL"])
    }
}

@Suite("ServerAuthoring — create")
struct AuthoringCreateTests {

    @Test("adds a server to a config that already has some")
    func addToExisting() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        _ = harness.discover()

        let draft = ServerDraft(name: "memory", command: "npx", args: ["-y", "@modelcontextprotocol/server-memory"])
        let result = try harness.authoring.create(draft, in: [.cursor], existing: harness.discover().servers)
        #expect(result.changed == [.cursor])

        let config = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(config.value(at: ["mcpServers"])?.keys == ["github", "figma", "memory"])
        #expect(config.value(at: ["mcpServers", "memory", "command"])?.stringValue == "npx")
        // The existing entries are untouched.
        #expect(config.value(at: ["mcpServers", "figma", "url"])?.stringValue == "https://mcp.figma.com/sse")
    }

    @Test("adds to a Claude Desktop config that has no mcpServers key at all")
    func addToConfigWithoutServersKey() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeDesktopNoServers.text, to: "\(claudeSupport)/claude_desktop_config.json")
        _ = harness.discover()

        let draft = ServerDraft(name: "memory", command: "npx", args: ["-y", "@modelcontextprotocol/server-memory"])
        _ = try harness.authoring.create(draft, in: [.claudeDesktop], existing: [])

        let config = try JSONDocument.parse(harness.text("\(claudeSupport)/claude_desktop_config.json"))
        #expect(config.value(at: ["mcpServers", "memory", "command"])?.stringValue == "npx")
        #expect(config.value(at: ["preferences", "chromeExtensionEnabled"])?.boolValue == true)
    }

    @Test("creates the file for a client that has never configured MCP")
    func createsFile() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        _ = harness.discover()

        let draft = ServerDraft(name: "memory", command: "npx")
        let result = try harness.authoring.create(draft, in: [.vsCode], existing: [])

        let config = try JSONDocument.parse(harness.text("Library/Application Support/Code/User/mcp.json"))
        // VS Code keys servers under `servers`.
        #expect(config.value(at: ["servers", "memory", "command"])?.stringValue == "npx")
        #expect(result.changed == [.vsCode])
        #expect(result.backupIDs.isEmpty)
    }

    @Test("writes to several clients in one go")
    func multipleClients() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {}}"#, to: ".cursor/mcp.json")
        try harness.home.write(#"{"mcpServers": {}}"#, to: ".claude.json")
        _ = harness.discover()

        let draft = ServerDraft(name: "memory", command: "npx")
        let result = try harness.authoring.create(draft, in: [.cursor, .claudeCode], existing: [])
        #expect(Set(result.changed) == [.cursor, .claudeCode])

        let server = try #require(harness.discover().servers.first { $0.name == "memory" })
        #expect(server.enabledIn[.cursor] == .enabled)
        #expect(server.enabledIn[.claudeCode] == .enabled)
    }

    @Test("a failure in a later client rolls earlier clients back")
    func multipleClientsRollBack() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {}}"#, to: ".cursor/mcp.json")
        _ = harness.discover()

        let cursorBefore = try harness.text(".cursor/mcp.json")
        let vsCodeDirectory = harness.home.root.appending(
            path: "Library/Application Support/Code/User",
            directoryHint: .isDirectory
        )
        try FileManager.default.createDirectory(at: vsCodeDirectory, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: vsCodeDirectory.path)
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: vsCodeDirectory.path)
        }

        let draft = ServerDraft(name: "memory", command: "npx")
        #expect(throws: AtomicWriter.WriteError.self) {
            _ = try harness.authoring.create(draft, in: [.cursor, .vsCode], existing: [])
        }

        #expect(try harness.text(".cursor/mcp.json") == cursorBefore)
        #expect(!FileManager.default.fileExists(atPath: vsCodeDirectory.appending(path: "mcp.json").path))
        #expect(!harness.discover().servers.contains { $0.name == "memory" })
    }

    @Test("a duplicate name is refused before anything is written")
    func rejectsDuplicate() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let before = try harness.text(".cursor/mcp.json")
        let existing = harness.discover().servers

        let draft = ServerDraft(name: "github", command: "npx")
        #expect(throws: AuthoringError.self) {
            _ = try harness.authoring.create(draft, in: [.cursor], existing: existing)
        }
        #expect(try harness.text(".cursor/mcp.json") == before)
    }

    @Test("the inserted definition matches the file's indentation")
    func matchesIndentation() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        // This fixture uses tabs.
        try harness.home.write(Fixture.claudeDesktopWithServers.text, to: "\(claudeSupport)/claude_desktop_config.json")
        _ = harness.discover()

        let draft = ServerDraft(name: "memory", command: "npx", args: ["-y", "pkg"])
        _ = try harness.authoring.create(draft, in: [.claudeDesktop], existing: harness.discover().servers)

        let text = try harness.text("\(claudeSupport)/claude_desktop_config.json")
        #expect(text.contains("\n\t\t\"memory\": {"), "should be tab-indented like its siblings")
        #expect(!text.contains("\n    \"memory\""))
    }
}

@Suite("ServerAuthoring — update and delete")
struct AuthoringEditTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")
        return harness
    }

    @Test("editing changes the server in every client that has it")
    func editsEverywhere() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let shared = #"{"mcpServers": {"github": {"command": "npx", "args": ["-y", "old"]}}}"#
        try harness.home.write(shared, to: ".cursor/mcp.json")
        try harness.home.write(shared, to: ".claude.json")

        let server = try harness.server("github")
        var draft = ServerDraft(editing: server)
        draft.args = ["-y", "new"]

        let result = try harness.authoring.update(
            draft,
            originalName: "github",
            server: server,
            existing: harness.discover().servers
        )
        #expect(Set(result.changed) == [.cursor, .claudeCode])

        for path in [".cursor/mcp.json", ".claude.json"] {
            let config = try JSONDocument.parse(harness.text(path))
            let args = config.value(at: ["mcpServers", "github", "args"])?.elements?.compactMap(\.stringValue)
            #expect(args == ["-y", "new"], "\(path) still has the old command")
        }
    }

    @Test("renaming removes the old key and adds the new one")
    func rename() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let server = try harness.server("figma")
        var draft = ServerDraft(editing: server)
        draft.name = "figma-design"

        _ = try harness.authoring.update(
            draft,
            originalName: "figma",
            server: server,
            existing: harness.discover().servers
        )

        let config = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(config.value(at: ["mcpServers"])?.keys == ["github", "figma-design"])
        #expect(config.value(at: ["mcpServers", "figma-design", "url"])?.stringValue == "https://mcp.figma.com/sse")
    }

    @Test("deleting removes the server from every client")
    func deleteEverywhere() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        #expect(github.enabledIn[.cursor] == .enabled)

        _ = try harness.authoring.delete(github)

        let cursor = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(cursor.value(at: ["mcpServers"])?.keys == ["figma"])
        #expect(!harness.discover().servers.contains { $0.name == "github" })
    }

    @Test("deleting a disabled server also clears its deny-list entry")
    func deleteClearsDenyList() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let xcode = try harness.server("XcodeBuildMCP")
        #expect(xcode.enabledIn[.claudeCode] == .disabled)

        _ = try harness.authoring.delete(xcode)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        let denied = settings.root["deniedMcpServers"]?.elements?.compactMap { $0["serverName"]?.stringValue }
        #expect(
            denied == ["claude-in-chrome"],
            "a dangling deny entry would switch the server off again if it were ever recreated"
        )
    }

    @Test("deleting a parked server does not leave it haunting the matrix")
    func deleteClearsPark() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)
        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "figma") != nil)

        _ = try harness.authoring.delete(harness.server("figma"))

        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "figma") == nil)
        #expect(!harness.discover().servers.contains { $0.name == "figma" })
    }

    @Test("a Claude Desktop extension cannot be edited or deleted")
    func extensionsAreNotEditable() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            Fixture.extensionManifest.text,
            to: "\(claudeSupport)/Claude Extensions/ant.dir.test/manifest.json"
        )

        let server = try harness.server("Control your Mac")
        #expect(throws: AuthoringError.notEditable("Control your Mac")) {
            _ = try harness.authoring.delete(server)
        }
        #expect(throws: AuthoringError.notEditable("Control your Mac")) {
            _ = try harness.authoring.update(
                ServerDraft(editing: server),
                originalName: server.name,
                server: server,
                existing: []
            )
        }
    }

    // MARK: - Removing from one client
    //
    // The act the matrix cannot express. Switching off is `disabled` in every
    // client on purpose, so these are the tests that a cell can reach `absent`
    // again — and that nothing left behind resurrects it on the next refresh.

    @Test("removing from one client leaves the others holding it")
    func removeFromOneClient() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        // In both files, so removing from one has somewhere to survive.
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "args": ["-y", "server-github"]}}}"#,
            to: ".claude.json"
        )

        let github = try harness.server("github")
        #expect(github.enabledIn[.cursor] == .enabled)
        #expect(github.enabledIn[.claudeCode] == .enabled)

        let result = try harness.authoring.remove(github, from: .claudeCode)
        #expect(result.changed == [.claudeCode])

        let after = try harness.server("github")
        #expect(after.enabledIn[.claudeCode] ?? .absent == .absent, "the cell must reach absent")
        #expect(after.enabledIn[.cursor] == .enabled, "the other client keeps it")
        #expect(try !harness.text(".claude.json").contains("github"))
        #expect(try harness.text(".cursor/mcp.json").contains("github"))
    }

    /// Off then remove is the sequence the feature exists for: the definition is
    /// out of the file but parked, and a park is drawn as "disabled".
    @Test("removing a switched-off server clears the park that would revive it")
    func removeClearsPark() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)
        #expect(try harness.server("figma").enabledIn[.cursor] == .disabled)

        _ = try harness.authoring.remove(harness.server("figma"), from: .cursor)

        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "figma") == nil)
        #expect(!harness.discover().servers.contains { $0.name == "figma" })
    }

    /// Claude Code is the client that needs two files cleared, and the deny entry
    /// is the one that is easy to forget: it names a server no file mentions.
    @Test("removing from Claude Code clears the deny entry too")
    func removeClearsDenyEntry() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(false, server: harness.server("context7"), in: .claudeCode)
        _ = try harness.authoring.remove(harness.server("context7"), from: .claudeCode)

        let config = try JSONDocument.parse(harness.text(".claude.json"))
        #expect(config.value(at: ["mcpServers", "context7"]) == nil)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        let denied = settings.root["deniedMcpServers"]?.elements?.compactMap { $0["serverName"]?.stringValue }
        #expect(denied?.contains("context7") == false, "a deny entry for nothing is litter")
        // The entry that was already there and has nothing to do with this.
        #expect(denied?.contains("XcodeBuildMCP") == true)

        #expect(!harness.discover().servers.contains { $0.name == "context7" })
    }

    @Test("removing from a client that does not have it is refused")
    func removeFromInnocentClient() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let figma = try harness.server("figma")
        #expect(figma.enabledIn[.claudeCode] ?? .absent == .absent)
        #expect(throws: AuthoringError.notFound("figma")) {
            _ = try harness.authoring.remove(figma, from: .claudeCode)
        }
    }

    @Test("an extension cannot be removed from a client either")
    func removeRefusesExtensions() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            Fixture.extensionManifest.text,
            to: "\(claudeSupport)/Claude Extensions/ant.dir.test/manifest.json"
        )

        let server = try harness.server("Control your Mac")
        #expect(throws: AuthoringError.notEditable("Control your Mac")) {
            _ = try harness.authoring.remove(server, from: .claudeDesktop)
        }
    }

    @Test("unrelated keys survive an edit")
    func preservesRest() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let server = try harness.server("XcodeBuildMCP")
        var draft = ServerDraft(editing: server)
        draft.command = "bunx"
        _ = try harness.authoring.update(
            draft,
            originalName: "XcodeBuildMCP",
            server: server,
            existing: harness.discover().servers
        )

        let config = try JSONDocument.parse(harness.text(".claude.json"))
        #expect(config.value(at: ["mcpServers", "XcodeBuildMCP", "command"])?.stringValue == "bunx")
        #expect(config.value(at: ["numStartups"])?.numberValue == 560)
        #expect(config.value(at: ["projects"])?.keys.count == 2)
    }
}

@Suite("Catalog")
struct CatalogTests {

    @Test("the bundled catalog loads")
    func loads() {
        #expect(!Catalog.entries.isEmpty)
    }

    @Test("every entry is complete enough to produce a valid draft")
    func entriesAreUsable() throws {
        for entry in Catalog.entries {
            let draft = entry.makeDraft()
            let errors = draft.validate()
            #expect(errors.isEmpty, "\(entry.id): \(errors)")

            // And the definition it renders is parseable JSON.
            _ = try JSONDocument.parse(draft.definition(baseIndent: "", unit: "  "))
        }
    }

    @Test("placeholders name tokens that actually appear in the arguments")
    func placeholdersMatch() {
        for entry in Catalog.entries {
            for placeholder in entry.placeholders {
                #expect(
                    entry.args.contains(placeholder.token),
                    "\(entry.id) declares \(placeholder.token) but no argument uses it"
                )
            }
            // And nothing is left un-declared, which would ship a literal
            // "{{directory}}" into someone's config.
            for argument in entry.args where argument.contains("{{") {
                #expect(
                    entry.placeholders.contains { $0.token == argument },
                    "\(entry.id) has an undeclared placeholder in \(argument)"
                )
            }
        }
    }

    @Test("archived MCP servers are not shipped")
    func noArchivedServers() {
        // These were archived by the MCP project. Prefilling a command that no
        // longer works is worse than having no catalog entry at all.
        let archived = [
            "server-github", "server-postgres", "server-sqlite", "server-puppeteer",
            "server-brave-search", "server-slack", "server-gdrive", "server-google-maps",
        ]
        for entry in Catalog.entries {
            for package in archived {
                #expect(
                    !entry.args.contains { $0.contains(package) },
                    "\(entry.id) points at the archived \(package)"
                )
            }
        }
    }
}
