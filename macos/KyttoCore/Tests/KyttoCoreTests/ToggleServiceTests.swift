import Foundation
import Testing
@testable import KyttoCore

/// A whole Kytto, pointed at a throwaway home directory. Everything it writes —
/// configs, backups, the park store — lands inside `home` and is thrown away
/// with it, so these tests exercise the real write path without going near the
/// tester's own configuration.
struct ToggleHarness {
    let home: FakeHome
    let ledger = DigestLedger()
    let paths: KyttoPaths
    let backups: BackupStore
    let parkStore: ParkStore
    let toggles: ToggleService

    init() throws {
        home = try FakeHome()
        paths = KyttoPaths(home: home.root)
        backups = BackupStore(paths: paths)
        parkStore = ParkStore(paths: paths)
        toggles = ToggleService(
            home: home.root,
            writer: ConfigWriter(backups: backups),
            parkStore: parkStore,
            ledger: ledger
        )
    }

    /// Runs discovery, which is also what primes the digest ledger.
    func discover() -> DiscoveryResult {
        Discovery(
            home: home.root,
            locator: StubAppLocator(
                bundleIdentifiers: [
                    "com.anthropic.claudefordesktop",
                    "com.microsoft.VSCode",
                    "com.todesktop.230313mzl4w4u92",
                    "com.openai.codex",
                ],
                executables: ["claude"]
            ),
            ledger: ledger,
            parked: parkStore.all()
        ).run()
    }

    func server(_ name: String) throws -> Server {
        try #require(discover().servers.first { $0.name == name })
    }

    func text(_ relativePath: String) throws -> String {
        try String(contentsOf: home.root.appending(path: relativePath), encoding: .utf8)
    }

    func cleanUp() {
        home.cleanUp()
    }
}

private let claudeSupport = "Library/Application Support/Claude"

@Suite("ToggleService — Claude Code deny list")
struct DenyListToggleTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")
        return harness
    }

    @Test("disabling adds a deny entry and leaves the server configured")
    func disable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let context7 = try harness.server("context7")
        let configBefore = try harness.text(".claude.json")

        let result = try harness.toggles.setEnabled(false, server: context7, in: .claudeCode)
        #expect(result.requiresRestart)
        #expect(!result.wasParked)

        // The server itself is untouched — this is the whole point of a deny list.
        #expect(try harness.text(".claude.json") == configBefore)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        let denied = try #require(settings.root["deniedMcpServers"]?.elements)
        let deniedNames = denied.compactMap { $0["serverName"]?.stringValue }
        #expect(deniedNames.contains("context7"))

        #expect(try harness.server("context7").enabledIn[.claudeCode] == .disabled)
    }

    @Test("enabling removes the deny entry and leaves the others")
    func enable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let xcode = try harness.server("XcodeBuildMCP")
        #expect(xcode.enabledIn[.claudeCode] == .disabled)

        _ = try harness.toggles.setEnabled(true, server: xcode, in: .claudeCode)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        let names = settings.root["deniedMcpServers"]?.elements?.compactMap { $0["serverName"]?.stringValue }
        #expect(names == ["claude-in-chrome"], "only the requested entry should go")
        #expect(try harness.server("XcodeBuildMCP").enabledIn[.claudeCode] == .enabled)
    }

    @Test("unrelated settings keys survive")
    func unrelatedKeysSurvive() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try JSONDocument.parse(harness.text(".claude/settings.json"))
        _ = try harness.toggles.setEnabled(false, server: harness.server("context7"), in: .claudeCode)
        let after = try JSONDocument.parse(harness.text(".claude/settings.json"))

        #expect(after.root.keys == before.root.keys)
        #expect(after.value(at: ["model"])?.stringValue == "opus")
        #expect(after.value(at: ["enabledPlugins", "swift-lsp@claude-plugins-official"])?.boolValue == true)
    }

    @Test("disabling twice does not add a duplicate")
    func idempotentDisable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let context7 = try harness.server("context7")
        _ = try harness.toggles.setEnabled(false, server: context7, in: .claudeCode)
        _ = try harness.toggles.setEnabled(false, server: try harness.server("context7"), in: .claudeCode)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        let names = settings.root["deniedMcpServers"]?.elements?.compactMap { $0["serverName"]?.stringValue }
        #expect(names?.filter { $0 == "context7" }.count == 1)
    }

    @Test("the deny list is created when the settings file has none")
    func createsDenyList() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write(#"{"model": "opus"}"#, to: ".claude/settings.json")

        _ = try harness.toggles.setEnabled(false, server: harness.server("context7"), in: .claudeCode)

        let settings = try JSONDocument.parse(harness.text(".claude/settings.json"))
        #expect(settings.root["deniedMcpServers"]?.elements?.count == 1)
        #expect(settings.root["model"]?.stringValue == "opus")
    }

    // MARK: - Arriving from another client
    //
    // Taking a name out of a deny list permits a definition that has to exist
    // for the permission to mean anything. These are the tests that keep the
    // config-file edit in front of the deny-list edit: without it the click
    // rewrites nothing, the cell comes back empty, and the result still says it
    // worked.

    private func makeCrossClientHarness() throws -> ToggleHarness {
        let harness = try makeHarness()
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        return harness
    }

    @Test("switching on a server this client does not have writes the definition")
    func arrivesFromAnotherJSONClient() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        #expect(github.enabledIn[.claudeCode] ?? .absent == .absent)

        let result = try harness.toggles.setEnabled(true, server: github, in: .claudeCode)
        // The file worth naming is the one the definition landed in.
        #expect(result.pathDisplay.hasSuffix(".claude.json"))
        #expect(result.requiresRestart)

        let config = try JSONDocument.parse(harness.text(".claude.json"))
        let arrived = try #require(config.value(at: ["mcpServers", "github"]))
        #expect(arrived["command"]?.stringValue == "npx")
        // Copied between two JSON clients, so it moves as the bytes it already
        // was — including the key Kytto's model has no field for.
        #expect(arrived["env"]?["GITHUB_PERSONAL_ACCESS_TOKEN"]?.stringValue == "ghp_exampletokenvalue")

        #expect(try harness.server("github").enabledIn[.claudeCode] == .enabled)
    }

    @Test("an arrival is rolled back when the deny-list edit fails")
    func arrivalRollsBackAfterDenyFailure() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        let configBefore = try harness.text(".claude.json")
        // This happens after discovery so the second file fails its digest
        // guard, exactly as it would if another process edited it mid-click.
        try harness.home.write(#"{"changedElsewhere":true}"#, to: ".claude/settings.json")

        #expect(throws: ConfigWriteError.self) {
            _ = try harness.toggles.setEnabled(true, server: github, in: .claudeCode)
        }
        #expect(try harness.text(".claude.json") == configBefore)
    }

    @Test("the servers already there are untouched by an arrival")
    func arrivalLeavesTheRestAlone() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(true, server: harness.server("github"), in: .claudeCode)

        let config = try JSONDocument.parse(harness.text(".claude.json"))
        #expect(config.value(at: ["mcpServers", "XcodeBuildMCP", "command"])?.stringValue == "npx")
        #expect(config.value(at: ["mcpServers", "context7", "url"])?.stringValue == "https://mcp.context7.com/mcp")
        // Everything this file holds that has nothing to do with MCP.
        #expect(config.root["numStartups"]?.numberValue == 560)
        #expect(config.root["migrationVersion"]?.numberValue == 3)
        #expect(
            config.value(at: ["projects", "/Users/example/Projects/Xcode/KyttoMCP", "mcpServers", "project-only-server"]) != nil,
            "project scope is out of scope, not fair game"
        )
    }

    /// The bug §4 names outright: a TOML table spliced into a JSON file.
    @Test("a definition from Codex is rendered, never spliced across")
    func arrivesFromCodex() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        let filesystem = try harness.server("filesystem")
        #expect(filesystem.enabledIn[.codex] == .enabled)
        #expect(filesystem.enabledIn[.claudeCode] ?? .absent == .absent)

        _ = try harness.toggles.setEnabled(true, server: filesystem, in: .claudeCode)

        let text = try harness.text(".claude.json")
        #expect(!text.contains("[mcp_servers"), "TOML must not reach a JSON file")
        #expect(!text.contains("startup_timeout_sec = "))

        let config = try JSONDocument.parse(text)
        let arrived = try #require(config.value(at: ["mcpServers", "filesystem"]))
        #expect(arrived["command"]?.stringValue == "npx")
        #expect(arrived["args"]?.elements?.compactMap(\.stringValue).first == "-y")
        #expect(try harness.server("filesystem").enabledIn[.claudeCode] == .enabled)
    }

    @Test("a server the client already lists is permitted, not rewritten")
    func alreadyListedIsNotRewritten() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".claude.json")
        let xcode = try harness.server("XcodeBuildMCP")
        #expect(xcode.enabledIn[.claudeCode] == .disabled)

        _ = try harness.toggles.setEnabled(true, server: xcode, in: .claudeCode)

        #expect(try harness.text(".claude.json") == before, "only the deny list had anything to say")
        #expect(try harness.server("XcodeBuildMCP").enabledIn[.claudeCode] == .enabled)
    }

    /// Off and on again is the sequence a user runs by accident, and it must not
    /// leave a duplicate, a reformat, or a definition rebuilt from the model.
    @Test("off then on returns the file byte for byte")
    func roundTrip() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".claude.json")
        _ = try harness.toggles.setEnabled(false, server: harness.server("context7"), in: .claudeCode)
        #expect(try harness.text(".claude.json") == before)

        _ = try harness.toggles.setEnabled(true, server: harness.server("context7"), in: .claudeCode)
        #expect(try harness.text(".claude.json") == before)
        #expect(try harness.server("context7").enabledIn[.claudeCode] == .enabled)
    }

    /// An extension is installed software. It was refused for presence clients
    /// already; the deny-list path must refuse it for the same reason instead of
    /// quietly permitting a name that has no definition to permit.
    @Test("an extension is still refused")
    func extensionsAreRefused() throws {
        let harness = try makeCrossClientHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            Fixture.extensionManifest.text,
            to: "\(claudeSupport)/Claude Extensions/ant.dir.test.osascript/manifest.json"
        )

        let servers = harness.discover().servers
        let bundled = try #require(servers.first { $0.isBundled })
        #expect(throws: ToggleError.bundledServerNotPortable(bundled.name)) {
            _ = try harness.toggles.setEnabled(true, server: bundled, in: .claudeCode)
        }
    }
}

@Suite("ToggleService — Claude Desktop extensions")
struct ExtensionToggleTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(
            Fixture.extensionManifest.text,
            to: "\(claudeSupport)/Claude Extensions/ant.dir.test.osascript/manifest.json"
        )
        try harness.home.write(
            Fixture.extensionSettingsDisabled.text,
            to: "\(claudeSupport)/Claude Extensions Settings/ant.dir.test.osascript.json"
        )
        return harness
    }

    @Test("flipping isEnabled keeps userConfig intact")
    func flipsFlag() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let server = try harness.server("Control your Mac")
        #expect(server.enabledIn[.claudeDesktop] == .disabled)

        _ = try harness.toggles.setEnabled(true, server: server, in: .claudeDesktop)

        let settings = try JSONDocument.parse(
            harness.text("\(claudeSupport)/Claude Extensions Settings/ant.dir.test.osascript.json")
        )
        #expect(settings.root["isEnabled"]?.boolValue == true)
        #expect(settings.value(at: ["userConfig", "allowed_directories"])?.elements?.count == 1)

        #expect(try harness.server("Control your Mac").enabledIn[.claudeDesktop] == .enabled)
    }

    @Test("an extension that has never been toggled gets a settings file")
    func createsSettingsFile() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            Fixture.extensionManifest.text,
            to: "\(claudeSupport)/Claude Extensions/ant.dir.test.osascript/manifest.json"
        )

        let server = try harness.server("Control your Mac")
        #expect(server.enabledIn[.claudeDesktop] == .enabled, "no file means never toggled, i.e. on")

        _ = try harness.toggles.setEnabled(false, server: server, in: .claudeDesktop)
        #expect(try harness.server("Control your Mac").enabledIn[.claudeDesktop] == .disabled)
    }

    @Test("an extension cannot be copied into another client")
    func notPortable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        try harness.home.write(#"{"mcpServers": {}}"#, to: ".cursor/mcp.json")

        let server = try harness.server("Control your Mac")
        #expect(throws: ToggleError.bundledServerNotPortable("Control your Mac")) {
            _ = try harness.toggles.setEnabled(true, server: server, in: .cursor)
        }
    }
}

@Suite("ToggleService — presence clients")
struct PresenceToggleTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        return harness
    }

    @Test("switching off parks the definition and removes it")
    func parkOnDisable() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let figma = try harness.server("figma")
        let result = try harness.toggles.setEnabled(false, server: figma, in: .cursor)
        #expect(result.wasParked)

        let config = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(config.value(at: ["mcpServers"])?.keys == ["github"])

        let parked = try #require(harness.parkStore.parked(clientID: .cursor, serverName: "figma"))
        #expect(parked.sourceText.contains("mcp.figma.com"))
    }

    @Test("off then on restores the file byte for byte")
    func roundTrip() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".cursor/mcp.json")
        let figma = try harness.server("figma")

        _ = try harness.toggles.setEnabled(false, server: figma, in: .cursor)
        #expect(try harness.text(".cursor/mcp.json") != before)

        _ = try harness.toggles.setEnabled(true, server: harness.server("figma"), in: .cursor)

        #expect(try harness.text(".cursor/mcp.json") == before, "a restored server must not be a reconstruction")
        #expect(harness.parkStore.parked(clientID: .cursor, serverName: "figma") == nil)
    }

    @Test("a parked server is still listed, shown as disabled")
    func parkedIsStillVisible() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        _ = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)

        // Parked servers must not vanish from the matrix — the row is the only
        // way back to switching it on again.
        let figma = try harness.server("figma")
        #expect(figma.enabledIn[.cursor] == .disabled)
        #expect(figma.url == "https://mcp.figma.com/sse")
        #expect(harness.parkStore.all().map(\.serverName) == ["figma"])
    }

    @Test("switching on a server the client does not have copies it across")
    func copyBetweenClients() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        // VS Code is installed but has never configured MCP.
        let github = try harness.server("github")
        #expect(github.enabledIn[.vsCode] == .absent)

        _ = try harness.toggles.setEnabled(true, server: github, in: .vsCode)

        let vscode = try JSONDocument.parse(harness.text("Library/Application Support/Code/User/mcp.json"))
        // VS Code keys servers under `servers`, not `mcpServers`.
        #expect(vscode.value(at: ["servers", "github", "command"])?.stringValue == "npx")
        #expect(try harness.server("github").enabledIn[.vsCode] == .enabled)
    }

    @Test("unrelated keys and formatting survive a toggle")
    func preservesRest() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeDesktopWithServers.text, to: "\(claudeSupport)/claude_desktop_config.json")

        _ = try harness.toggles.setEnabled(false, server: harness.server("postgres"), in: .claudeDesktop)

        let after = try JSONDocument.parse(harness.text("\(claudeSupport)/claude_desktop_config.json"))
        #expect(after.root.keys == ["coworkUserFilesPath", "mcpServers", "preferences"])
        #expect(after.value(at: ["preferences", "chromeExtensionEnabled"])?.boolValue == true)
        #expect(after.value(at: ["mcpServers"])?.keys == ["github", "remote-docs"])
        // The file used tabs; it still does.
        #expect(try harness.text("\(claudeSupport)/claude_desktop_config.json").contains("\n\t\"mcpServers\""))
    }
}

@Suite("ToggleService — safety")
struct ToggleSafetyTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        return harness
    }

    @Test("a backup is taken before every write")
    func backsUp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".cursor/mcp.json")
        let result = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)

        let backupID = try #require(result.backupID)
        let backup = try #require(harness.backups.backup(id: backupID, clientID: .cursor))
        #expect(try String(contentsOf: backup.url, encoding: .utf8) == before)
    }

    @Test("restoring a backup puts the original file back")
    func restore() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".cursor/mcp.json")
        let result = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)
        #expect(try harness.text(".cursor/mcp.json") != before)

        let backupID = try #require(result.backupID)
        let backup = try #require(harness.backups.backup(id: backupID, clientID: .cursor))
        try harness.backups.restore(backup, home: harness.home.root)

        #expect(try harness.text(".cursor/mcp.json") == before)
    }

    @Test("reverting is itself reversible")
    func restoreIsBackedUp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let result = try harness.toggles.setEnabled(false, server: harness.server("figma"), in: .cursor)
        let backupID = try #require(result.backupID)
        let backup = try #require(harness.backups.backup(id: backupID, clientID: .cursor))
        try harness.backups.restore(backup, home: harness.home.root)

        #expect(harness.backups.list(clientID: .cursor).count == 2, "the state before the revert is kept too")
    }

    @Test("a backup without a valid restore target is never offered")
    func missingBackupOrigin() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let url = harness.home.root.appending(path: ".cursor/mcp.json")
        let created = try harness.backups.backUp(
            url, clientID: .cursor, pathDisplay: "~/.cursor/mcp.json"
        )
        let backup = try #require(created)
        try FileManager.default.removeItem(at: backup.url.appendingPathExtension("origin"))

        #expect(harness.backups.list(clientID: .cursor).isEmpty)
        #expect(harness.backups.backup(id: backup.id, clientID: .cursor) == nil)
    }

    @Test("retention keeps the last 20 per client")
    func retention() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        for index in 0..<25 {
            let url = harness.home.root.appending(path: ".cursor/mcp.json")
            try harness.backups.backUp(url, clientID: .cursor, pathDisplay: "~/.cursor/mcp.json")
            // Distinct timestamps; the stamp has millisecond resolution.
            if index % 5 == 0 { usleep(2000) }
        }
        #expect(harness.backups.list(clientID: .cursor).count == BackupStore.defaultRetentionPerClient)
    }

    @Test("a file changed behind Kytto's back is not written over")
    func refusesToClobberExternalEdits() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let figma = try harness.server("figma") // primes the ledger
        // Something else edits the file — the user, or the client app itself.
        try harness.home.write(#"{"mcpServers": {"typed-by-hand": {"command": "x"}}}"#, to: ".cursor/mcp.json")

        #expect(throws: ConfigWriteError.changedOnDisk(pathDisplay: "~/.cursor/mcp.json")) {
            _ = try harness.toggles.setEnabled(false, server: figma, in: .cursor)
        }
        // And the hand-written content is still there, unmerged.
        #expect(try harness.text(".cursor/mcp.json").contains("typed-by-hand"))
    }

    @Test("a toggle that changes nothing writes nothing")
    func noOpWritesNothing() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        let result = try harness.toggles.setEnabled(true, server: github, in: .cursor)

        #expect(result.backupID == nil, "no change means no backup slot burned")
        #expect(harness.backups.list(clientID: .cursor).isEmpty)
    }

    @Test("the written file is always valid JSON")
    func alwaysValid() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        for name in ["github", "figma"] {
            _ = try harness.toggles.setEnabled(false, server: harness.server(name), in: .cursor)
            _ = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        }
        let final = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(final.value(at: ["mcpServers"])?.keys.isEmpty == true)
    }
}
