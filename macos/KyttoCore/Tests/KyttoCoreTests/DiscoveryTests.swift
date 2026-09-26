import Foundation
import Testing
@testable import KyttoCore

/// Builds a throwaway home directory so discovery runs against the real code
/// paths — real files, real parser — without touching the tester's own configs.
struct FakeHome {
    let root: URL

    init() throws {
        root = URL(filePath: NSTemporaryDirectory())
            .appending(path: "kytto-tests")
            .appending(path: UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    func write(_ text: String, to relativePath: String) throws {
        let url = root.appending(path: relativePath)
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try text.write(to: url, atomically: true, encoding: .utf8)
    }

    func makeDirectory(_ relativePath: String) throws {
        try FileManager.default.createDirectory(
            at: root.appending(path: relativePath),
            withIntermediateDirectories: true
        )
    }

    func cleanUp() {
        try? FileManager.default.removeItem(at: root)
    }
}

private let claudeSupport = "Library/Application Support/Claude"

/// Reproduces the shape of the machine this was developed on: Claude Desktop
/// with extensions and no `mcpServers`, Claude Code with a denied server, a
/// leftover `~/.cursor` with no Cursor installed, VS Code with no `mcp.json`.
private func makeRealisticHome() throws -> FakeHome {
    let home = try FakeHome()

    try home.write(Fixture.claudeDesktopNoServers.text, to: "\(claudeSupport)/claude_desktop_config.json")

    try home.write(
        Fixture.extensionManifest.text,
        to: "\(claudeSupport)/Claude Extensions/ant.dir.gh.k6l3.osascript/manifest.json"
    )
    try home.write(
        #"{"name": "Filesystem", "server": {"mcp_config": {"command": "node", "args": ["${__dirname}/server/index.js"]}}}"#,
        to: "\(claudeSupport)/Claude Extensions/ant.dir.ant.anthropic.filesystem/manifest.json"
    )
    try home.write(
        Fixture.extensionSettingsDisabled.text,
        to: "\(claudeSupport)/Claude Extensions Settings/ant.dir.ant.anthropic.filesystem.json"
    )
    // osascript has no settings file at all — never toggled, so it counts as on.

    try home.write(Fixture.claudeCode.text, to: ".claude.json")
    try home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")

    // Cursor's directory survives an uninstall; its config does not exist.
    try home.makeDirectory(".cursor")

    return home
}

private func makeDiscovery(_ home: FakeHome) -> Discovery {
    Discovery(
        home: home.root,
        locator: StubAppLocator(
            bundleIdentifiers: ["com.anthropic.claudefordesktop", "com.microsoft.VSCode"],
            executables: ["claude"]
        )
    )
}

@Suite("Discovery")
struct DiscoveryTests {

    @Test("a custom JSON source is auto-detected, scoped and left byte-identical")
    func customJSONSource() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let file = home.root.appending(path: "projects/demo/.vscode/mcp.json")
        let original = #"{"servers":{"workspace-tool":{"command":"node","args":["server.js"]}},"unrelated":true}"#
        try home.write(original, to: "projects/demo/.vscode/mcp.json")
        let source = CustomConfigSource(
            displayName: "Demo workspace",
            path: file.path,
            scope: .workspace,
            scopeLabel: "Demo"
        )

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(),
            descriptors: [ClientRegistry.customDescriptor(source)]
        ).run()

        let client = try #require(result.clients.first)
        #expect(client.id == source.clientID)
        #expect(client.isReadOnly)
        #expect(client.configurationScope == .workspace)
        #expect(client.scopeLabel == "Demo")
        #expect(result.servers.map(\.name) == ["workspace-tool"])
        #expect(try String(contentsOf: file, encoding: .utf8) == original)
    }

    @Test("only project scopes that actually configure a server become clients")
    func claudeCodeProjectScopes() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        // What Claude Code leaves behind: an empty servers map in every directory
        // it has been run in, and a real one where a server was actually added.
        try home.write(#"""
        {
          "projects": {
            "/Users/example/scratch": { "mcpServers": {} },
            "/Users/example/notes": { "mcpServers": {}, "history": [] },
            "/Users/example/work/api": {
              "mcpServers": { "project-tool": { "command": "node", "args": ["server.js"] } }
            }
          }
        }
        """#, to: ".claude.json")

        let descriptors = ClientRegistry.allIncludingClaudeCodeProjects(home: home.root)
        let scopes = descriptors.filter { $0.configurationScope == .workspace }

        #expect(scopes.map(\.scopeLabel) == ["/Users/example/work/api"])
        // Three lengths for three surfaces: the sidebar row shows the bare
        // leaf (its icon and heading already say Claude Code), prose surfaces
        // the prefixed short form, and tooltips the full path in `scopeLabel`.
        #expect(scopes.map(\.displayName) == ["Claude Code · …/api"])
        #expect(scopes.map(\.shortName) == ["…/api"])
        #expect(scopes.allSatisfy { $0.isReadOnly })
    }

    /// Home-directory paths share their prefix, so a right-truncated label
    /// shows exactly the part that never differs. The leaf keeps the *end*,
    /// growing leftwards only when two projects would otherwise read the same.
    @Test("project labels keep the distinguishing end of the path")
    func compactProjectLeaves() {
        let leaves = ClientRegistry.compactProjectLeaves(for: [
            "/Users/example/Projects/Xcode/KyttoMCP",
            "/Users/example/work/api-gateway",
            "/Users/example/work/repo",
            "/Users/example/scratch/repo",
            #"C:\Users\example\projects\shop"#,
            "/",
        ])

        #expect(leaves["/Users/example/Projects/Xcode/KyttoMCP"] == "…/KyttoMCP")
        #expect(leaves["/Users/example/work/api-gateway"] == "…/api-gateway")
        // Two checkouts ending in "repo" grow until they differ.
        #expect(leaves["/Users/example/work/repo"] == "…/work/repo")
        #expect(leaves["/Users/example/scratch/repo"] == "…/scratch/repo")
        // A Windows-keyed path shortens with its own separator (§3.2).
        #expect(leaves[#"C:\Users\example\projects\shop"#] == #"…\shop"#)
        // A path with no components to shorten survives whole.
        #expect(leaves["/"] == "/")
    }

    @Test("a custom TOML source recognizes mcp_servers automatically")
    func customTOMLSource() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let file = home.root.appending(path: "custom/config.toml")
        try home.write("[mcp_servers.inspector]\ncommand = \"npx\"\nargs = [\"-y\", \"inspector\"]\n", to: "custom/config.toml")
        let source = CustomConfigSource(displayName: "TOML source", path: file.path)

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(),
            descriptors: [ClientRegistry.customDescriptor(source)]
        ).run()

        #expect(result.servers.map(\.name) == ["inspector"])
        #expect(result.clients.first?.configFormatDisplay == "Auto-detected · read-only")
    }

    @Test("reproduces the development machine's client states")
    func clientStates() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        func client(_ id: ClientID) throws -> DiscoveredClient {
            try #require(result.clients.first { $0.id == id })
        }

        let desktop = try client(.claudeDesktop)
        #expect(desktop.isInstalled)
        #expect(desktop.configExists)
        #expect(desktop.serverCount == 2, "both extensions should be listed")

        let code = try client(.claudeCode)
        #expect(code.isInstalled)
        #expect(code.serverCount == 2)

        // Installed check must not be fooled by the leftover directory.
        let cursor = try client(.cursor)
        #expect(!cursor.isInstalled)
        #expect(!cursor.configExists)
        #expect(cursor.serverCount == 0)

        let vscode = try client(.vsCode)
        #expect(vscode.isInstalled)
        #expect(!vscode.configExists, "VS Code is installed but has never configured MCP")
        #expect(vscode.serverCount == 0)
    }

    @Test("a config with no mcpServers key is normal, not an error")
    func missingServersKeyIsNotAnError() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        #expect(result.diagnostics.allSatisfy { $0.severity != .error })
    }

    @Test("Claude Code's deny list disables without removing")
    func denyListDisables() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        let xcode = try #require(result.servers.first { $0.name == "XcodeBuildMCP" })
        #expect(xcode.enabledIn[.claudeCode] == .disabled, "denied, but still configured")
        #expect(xcode.command == "npx")

        let context7 = try #require(result.servers.first { $0.name == "context7" })
        #expect(context7.enabledIn[.claudeCode] == .enabled)
        #expect(context7.transport == .http)
    }

    @Test("extension isEnabled drives the toggle state")
    func extensionFlag() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        let filesystem = try #require(result.servers.first { $0.name == "Filesystem" })
        #expect(filesystem.enabledIn[.claudeDesktop] == .disabled)
        #expect(filesystem.isBundled)

        let osascript = try #require(result.servers.first { $0.name == "Control your Mac" })
        #expect(osascript.enabledIn[.claudeDesktop] == .enabled, "no settings file means never toggled, i.e. on")
        #expect(osascript.origin == .claudeDesktopExtension(bundleID: "ant.dir.gh.k6l3.osascript"))
    }

    @Test("extensions carry the substitutions Claude Desktop would make")
    func extensionPlaceholders() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        let filesystem = try #require(result.servers.first { $0.name == "Filesystem" })
        // The bundle directory, so `${__dirname}` can be resolved at run time.
        let dirname = try #require(filesystem.placeholders["__dirname"])
        #expect(dirname.hasSuffix("Claude Extensions/ant.dir.ant.anthropic.filesystem"))
        // And the extension's own settings, which is where the rest come from.
        #expect(filesystem.placeholders["user_config.allowed_directories"] == "/Users/example/Documents")

        // Displayed text is untouched: the matrix shows what the manifest says.
        #expect(filesystem.commandSummary.contains("${__dirname}"))
        #expect(filesystem.runnable.args.first?.hasPrefix(dirname) == true)
    }

    @Test("Claude Code project-scoped servers are visible as read-only workspace sources")
    func projectScopeDiscovered() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        let project = try #require(result.servers.first { $0.name == "project-only-server" })
        let projectClient = try #require(result.clients.first {
            $0.configurationScope == .workspace && $0.scopeLabel == "/Users/example/Projects/Xcode/KyttoMCP"
        })
        #expect(project.enabledIn[projectClient.id] == .enabled)
        #expect(projectClient.isReadOnly)
        #expect(projectClient.configPathDisplay.contains(".claude.json"))
        #expect(projectClient.configPathDisplay.contains(projectClient.scopeLabel))
    }

    @Test("plugin discovery stops descending once a package manifest is found")
    func pluginPayloadIsPruned() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("{}", to: "plugins/cache/example/1/.mcp.json")
        try home.write("{}", to: "plugins/cache/example/1/node_modules/nested/.mcp.json")
        try home.write("{}", to: "plugins/cache/second/2/.mcp.json")

        let manifests = Discovery.bundledManifestURLs(
            root: home.root.appending(path: "plugins"),
            manifestName: ".mcp.json"
        )

        #expect(manifests.count == 2)
        #expect(manifests.allSatisfy { !$0.path.contains("node_modules") })
    }

    @Test("every server carries a cell for every client")
    func fullGrid() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }
        let result = makeDiscovery(home).run()

        for server in result.servers {
            #expect(server.enabledIn.count == result.clients.count, "\(server.name) has a gap in the grid")
        }
    }

    @Test("the same name in two clients is one row")
    func mergeAcrossClients() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let shared = #"{"mcpServers": {"github": {"command": "npx", "args": ["-y", "server-github"]}}}"#
        try home.write(shared, to: ".cursor/mcp.json")
        try home.write(shared, to: ".claude.json")

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(
                bundleIdentifiers: ["com.todesktop.230313mzl4w4u92"],
                executables: ["claude"]
            )
        ).run()

        #expect(result.servers.count == 1)
        let github = try #require(result.servers.first)
        #expect(github.enabledIn[.cursor] == .enabled)
        #expect(github.enabledIn[.claudeCode] == .enabled)
        #expect(github.enabledIn[.vsCode] == .absent)
    }

    @Test("one name, two different commands, keeps both copies")
    func fingerprintConflict() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(#"{"mcpServers": {"github": {"command": "npx"}}}"#, to: ".cursor/mcp.json")
        try home.write(#"{"mcpServers": {"github": {"command": "bunx"}}}"#, to: ".claude.json")

        let result = Discovery(home: home.root, locator: StubAppLocator()).run()

        // One row, because the name is the identity — but the row can still say
        // which client holds which command.
        #expect(result.servers.count == 1)
        let github = try #require(result.servers.first)
        #expect(github.definitionsByClient[.cursor]?.command == "npx")
        #expect(github.definitionsByClient[.claudeCode]?.command == "bunx")
    }

    @Test("a client's own copy is recorded even when every client agrees")
    func definitionsRecordedWithoutDrift() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let shared = #"{"mcpServers": {"github": {"command": "npx", "env": {"TOKEN": "abc"}}}}"#
        try home.write(shared, to: ".cursor/mcp.json")
        try home.write(shared, to: ".claude.json")

        let result = Discovery(home: home.root, locator: StubAppLocator()).run()

        let github = try #require(result.servers.first)
        #expect(github.definitionsByClient.count == 2)
        #expect(github.envByClient[.cursor]?.first?.value == "abc")
        #expect(github.envByClient[.claudeCode]?.first?.value == "abc")
    }

    @Test("VS Code's servers key maps onto the same model as mcpServers")
    func vscodeServersKey() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.vscodeMCP.text, to: "Library/Application Support/Code/User/mcp.json")

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(bundleIdentifiers: ["com.microsoft.VSCode"])
        ).run()

        #expect(result.servers.map(\.name).sorted() == ["github", "playwright"])
        let playwright = try #require(result.servers.first { $0.name == "playwright" })
        #expect(playwright.transport == .stdio)
        #expect(playwright.args == ["-y", "@playwright/mcp@latest"])
    }

    @Test("an unparseable config is reported and never treated as empty")
    func brokenConfigIsReported() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write("{ this is not json", to: ".cursor/mcp.json")

        let result = Discovery(home: home.root, locator: StubAppLocator()).run()

        let error = try #require(result.diagnostics.first { $0.severity == .error })
        #expect(error.clientID == .cursor)
        #expect(error.message.contains("will not write"))
    }

    @Test("discovery writes nothing")
    func readOnly() throws {
        let home = try makeRealisticHome()
        defer { home.cleanUp() }

        func snapshot() throws -> [String: Date] {
            var result: [String: Date] = [:]
            let enumerator = FileManager.default.enumerator(at: home.root, includingPropertiesForKeys: [.contentModificationDateKey])
            while let url = enumerator?.nextObject() as? URL {
                let values = try url.resourceValues(forKeys: [.contentModificationDateKey])
                result[url.path] = values.contentModificationDate
            }
            return result
        }

        let before = try snapshot()
        _ = makeDiscovery(home).run()
        let after = try snapshot()

        #expect(before == after, "M1 must not create or modify a single file (§6.6)")
    }
}
