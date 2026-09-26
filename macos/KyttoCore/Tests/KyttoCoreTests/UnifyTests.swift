import Foundation
import Testing
@testable import KyttoCore

/// Unifying rewrites several real config files at once, so these assert the
/// same things the rest of the write pipeline is held to — unrelated content
/// survives, and nothing is switched on or off as a side effect.
@Suite("ServerAuthoring — unify")
struct UnifyTests {

    /// The same server in two clients, spelled differently in each.
    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(
            """
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github"],
                  "env": { "GITHUB_TOKEN": "ghp_current" }
                }
              }
            }
            """,
            to: ".cursor/mcp.json"
        )
        try harness.home.write(
            """
            {
              "editorTheme": "dark",
              "mcpServers": {
                "github": {
                  "command": "bunx",
                  "args": ["@modelcontextprotocol/server-github@0.1.0"],
                  "env": { "GITHUB_TOKEN": "ghp_stale" }
                }
              }
            }
            """,
            to: ".claude.json"
        )
        return harness
    }

    private func draft(from server: Server, in clientID: ClientID) throws -> ServerDraft {
        let definition = try #require(server.definitionsByClient[clientID])
        return ServerDraft(
            name: server.name,
            transport: definition.transport,
            command: definition.command ?? "",
            args: definition.args,
            env: definition.env,
            url: definition.url ?? ""
        )
    }

    @Test("the chosen client's definition replaces the other one")
    func unifiesCommandAndArguments() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        let result = try harness.authoring.unify(
            draft(from: github, in: .cursor),
            server: github,
            into: [.claudeCode]
        )

        #expect(result.changed == [.claudeCode])
        #expect(result.requiresRestart)

        let after = try harness.server("github")
        let claude = try #require(after.definitionsByClient[.claudeCode])
        #expect(claude.command == "npx")
        #expect(claude.args == ["-y", "@modelcontextprotocol/server-github"])
        // And the two copies now agree, which is the only outcome that matters.
        #expect(ServerDrift.detect(in: after) == nil)
    }

    @Test("the environment value travels without the web layer ever seeing it")
    func unifiesEnvironmentValue() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        _ = try harness.authoring.unify(
            draft(from: github, in: .cursor),
            server: github,
            into: [.claudeCode]
        )

        let text = try harness.text(".claude.json")
        #expect(text.contains("ghp_current"))
        #expect(!text.contains("ghp_stale"))
    }

    @Test("keys the target file owns and Kytto does not are left alone")
    func preservesUnrelatedKeys() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let github = try harness.server("github")
        _ = try harness.authoring.unify(
            draft(from: github, in: .cursor),
            server: github,
            into: [.claudeCode]
        )

        #expect(try harness.text(".claude.json").contains("\"editorTheme\": \"dark\""))
    }

    @Test("the source client's own file is not rewritten")
    func sourceUntouched() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let before = try harness.text(".cursor/mcp.json")
        let github = try harness.server("github")
        _ = try harness.authoring.unify(
            draft(from: github, in: .cursor),
            server: github,
            into: [.claudeCode]
        )

        #expect(try harness.text(".cursor/mcp.json") == before)
    }

    /// The trap this whole method exists around. In a presence-only client, a
    /// switched-off server has been *removed* from the file and its bytes are in
    /// Kytto's park store. Writing a definition into that file is exactly what
    /// switching a server on means — so a careless unify would turn it back on.
    @Test("a switched-off server is brought into line without being switched on")
    func parkedCopyIsUpdatedNotRevived() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        // Switch it off in Cursor first: that parks the definition.
        let initial = try harness.server("github")
        let toggle = try harness.toggles.setEnabled(false, server: initial, in: .cursor)
        #expect(toggle.wasParked)

        // Now unify from Claude Code, which still has its own spelling.
        let github = try harness.server("github")
        #expect(github.enabledIn[.cursor] == .disabled)
        let result = try harness.authoring.unify(
            draft(from: github, in: .claudeCode),
            server: github,
            into: [.cursor]
        )

        // Nothing was written to Cursor's file, so nothing asks for a restart.
        #expect(result.changed.isEmpty)
        #expect(result.parkedUpdated == [.cursor])
        #expect(result.parkedFailures.isEmpty)

        // Still off — the definition did not reappear in the file.
        let after = try harness.server("github")
        #expect(after.enabledIn[.cursor] == .disabled)
        #expect(!(try harness.text(".cursor/mcp.json").contains("bunx")))

        // And switching it back on restores the unified definition, not the old one.
        _ = try harness.toggles.setEnabled(true, server: after, in: .cursor)
        let revived = try harness.server("github")
        #expect(revived.enabledIn[.cursor] == .enabled)
        #expect(revived.definitionsByClient[.cursor]?.command == "bunx")
        #expect(ServerDrift.detect(in: revived) == nil)
    }

    @Test("an installed bundle is never written to")
    func refusesBundledTarget() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        var github = try harness.server("github")
        github.definitionsByClient[.claudeDesktop] = ClientDefinition(
            transport: .stdio,
            command: "node",
            args: ["${__dirname}/index.js"],
            env: [],
            url: nil,
            origin: .claudeDesktopExtension(bundleID: "com.example.github"),
            isBundled: true,
            definitionSource: "{}"
        )

        #expect(throws: AuthoringError.notEditable("github")) {
            try harness.authoring.unify(
                draft(from: github, in: .cursor),
                server: github,
                into: [.claudeDesktop]
            )
        }
    }

    @Test("a Codex server keeps its off switch when its definition is replaced")
    func codexStaysDisabled() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "args": ["-y", "pkg"]}}}"#,
            to: ".cursor/mcp.json"
        )
        try harness.home.write(
            """
            [mcp_servers.github]
            command = "bunx"
            enabled = false
            """,
            to: ".codex/config.toml"
        )

        let github = try harness.server("github")
        #expect(github.enabledIn[.codex] == .disabled)

        _ = try harness.authoring.unify(
            draft(from: github, in: .cursor),
            server: github,
            into: [.codex]
        )

        let after = try harness.server("github")
        #expect(after.definitionsByClient[.codex]?.command == "npx")
        // Replacing the block must not quietly turn the server back on.
        #expect(after.enabledIn[.codex] == .disabled)
    }
}
