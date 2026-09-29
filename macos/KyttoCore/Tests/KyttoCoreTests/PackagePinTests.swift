import Foundation
import Testing
@testable import KyttoCore

private func launch(_ command: String, _ args: String...) -> PackageLaunch? {
    PackageLaunch.parse(command: command, args: args)
}

@Suite("PackageLaunch — reading the package a runner starts")
struct PackageLaunchTests {

    @Test("npx with no version, a tag or a range is unpinned")
    func npxUnpinned() throws {
        let bare = try #require(launch("npx", "-y", "@modelcontextprotocol/server-github"))
        #expect(bare.ecosystem == .npm)
        #expect(bare.name == "@modelcontextprotocol/server-github")
        #expect(bare.requestedVersion == nil)
        #expect(!bare.isPinned)
        #expect(bare.argumentIndex == 1)
        #expect(bare.runner == "npx")

        for spec in ["pkg@latest", "pkg@next", "pkg@^1.2.0", "pkg@~1", "pkg@1.x", "pkg@1.2", "pkg@>=1.0.0", "pkg@*", "pkg@"] {
            let parsed = try #require(launch("npx", "--yes", spec), "\(spec)")
            #expect(parsed.name == "pkg")
            #expect(!parsed.isPinned, "\(spec) should be unpinned")
        }
    }

    @Test("an exact semver release is pinned, scoped or not")
    func npxPinned() throws {
        for spec in ["pkg@1.2.3", "@scope/pkg@1.2.3", "pkg@1.0.0-beta.2", "pkg@1.2.3+build.5", "pkg@v1.2.3"] {
            let parsed = try #require(launch("npx", "-y", spec), "\(spec)")
            #expect(parsed.isPinned, "\(spec) should be pinned")
        }
        let scoped = try #require(launch("npx", "@scope/pkg@2.0.0"))
        #expect(scoped.name == "@scope/pkg")
        #expect(scoped.requestedVersion == "2.0.0")
    }

    @Test("flags before the package are skipped, including ones that take a value")
    func npxFlags() throws {
        let parsed = try #require(launch("npx", "--registry", "https://registry.example", "-q", "--prefer-online", "pkg", "--port", "3000"))
        #expect(parsed.name == "pkg")
        #expect(parsed.argumentIndex == 4)

        let afterSeparator = try #require(launch("npx", "-y", "--", "pkg@latest", "serve"))
        #expect(afterSeparator.argumentIndex == 2)
        #expect(afterSeparator.requestedVersion == "latest")
    }

    @Test("--package names the package and the positional is only the binary")
    func npxPackageFlag() throws {
        let separate = try #require(launch("npx", "-y", "-p", "@scope/tools@latest", "tools-mcp"))
        #expect(separate.name == "@scope/tools")
        #expect(separate.argumentIndex == 2)
        #expect(separate.argumentPrefix == "")

        let joined = try #require(launch("npx", "--package=@scope/tools", "tools-mcp"))
        #expect(joined.argumentIndex == 0)
        #expect(joined.argumentPrefix == "--package=")
        #expect(joined.pinnedArgument(version: "3.1.4") == "--package=@scope/tools@3.1.4")

        // Two packages: which one is "the server" would be a guess.
        #expect(launch("npx", "-p", "a", "-p", "b", "a-bin") == nil)
    }

    @Test("the other npm-style runners are read the same way")
    func otherNodeRunners() throws {
        #expect(launch("bunx", "pkg")?.runner == "bunx")
        #expect(launch("bun", "x", "pkg")?.argumentIndex == 1)
        #expect(launch("pnpm", "dlx", "pkg@latest")?.argumentIndex == 1)
        #expect(launch("yarn", "dlx", "-q", "pkg")?.argumentIndex == 2)
        #expect(launch("npm", "exec", "--", "pkg")?.argumentIndex == 2)
        #expect(launch("/opt/homebrew/bin/npx", "-y", "pkg")?.name == "pkg")
        #expect(launch("npx.cmd", "-y", "pkg")?.name == "pkg")

        // Subcommands that do not fetch a package by name.
        #expect(launch("pnpm", "exec", "pkg") == nil)
        #expect(launch("npm", "run", "start") == nil)
        #expect(launch("bun", "server.ts") == nil)
        #expect(launch("node", "server.js") == nil)
    }

    @Test("paths, URLs, git specs, aliases and GitHub shorthand are not packages")
    func npxOutOfScope() {
        for spec in [
            "./local-server", "/abs/server", "~/server", "file:../server",
            "github:user/repo", "user/repo", "git+https://example.com/r.git",
            "https://example.com/pkg.tgz", "pkg.tgz", "alias@npm:real@1.0.0",
        ] {
            #expect(launch("npx", "-y", spec) == nil, "\(spec) should be out of scope")
        }
        #expect(launch("npx", "-y") == nil)
        #expect(launch("npx", "-c", "echo hi") == nil)
    }

    @Test("uvx: bare names and @latest float, name@version and == pin")
    func uvx() throws {
        let bare = try #require(launch("uvx", "mcp-server-time"))
        #expect(bare.ecosystem == .python)
        #expect(!bare.isPinned)
        #expect(bare.spelling == .uvAt)
        #expect(bare.pinnedArgument(version: "0.6.2") == "mcp-server-time@0.6.2")

        #expect(launch("uvx", "mcp-server-time@latest")?.isPinned == false)
        #expect(launch("uvx", "mcp-server-time@0.6.2")?.isPinned == true)
        #expect(launch("uvx", "mcp-server-time==0.6.2")?.isPinned == true)
        #expect(launch("uvx", "mcp-server-time>=0.6")?.isPinned == false)
        #expect(launch("uvx", "mcp-server-time==0.*")?.isPinned == false)
        #expect(launch("uvx", "mcp-server-time>=0.6,<1")?.isPinned == false)

        // A range written as a requirement is pinned as one.
        let ranged = try #require(launch("uvx", "mcp-server-time~=0.6"))
        #expect(ranged.spelling == .requirement)
        #expect(ranged.pinnedArgument(version: "0.6.2") == "mcp-server-time==0.6.2")
    }

    @Test("uvx options are skipped and --from carries the requirement")
    func uvxFrom() throws {
        let skipped = try #require(launch("uvx", "--python", "3.12", "--with", "httpx", "mcp-server-fetch", "--port", "1"))
        #expect(skipped.name == "mcp-server-fetch")
        #expect(skipped.argumentIndex == 4)

        let from = try #require(launch("uvx", "--from", "awslabs.aws-mcp[cli]", "aws-mcp"))
        #expect(from.name == "awslabs.aws-mcp")
        #expect(from.extras == "[cli]")
        #expect(from.argumentIndex == 1)
        #expect(from.spelling == .requirement)
        #expect(from.pinnedArgument(version: "1.4.0") == "awslabs.aws-mcp[cli]==1.4.0")

        let joined = try #require(launch("uv", "tool", "run", "--from=pkg==2.0.0", "pkg"))
        #expect(joined.isPinned)
        #expect(joined.argumentIndex == 2)

        #expect(launch("uvx", "--from", "git+https://github.com/example/pkg", "pkg") == nil)
        #expect(launch("uvx", "--from", "./checkout", "pkg") == nil)
        #expect(launch("uvx", "pkg; python_version<'3.12'") == nil)
        #expect(launch("uv", "run", "server.py") == nil)
    }

    @Test("pipx run can be read, but a bare name cannot be pinned in place")
    func pipx() throws {
        let bare = try #require(launch("pipx", "run", "mcp-server-git"))
        #expect(!bare.isPinned)
        #expect(!bare.canPinInPlace)
        #expect(bare.pinnedArgument(version: "1.0.0") == nil)

        let spec = try #require(launch("pipx", "run", "--spec", "mcp-server-git>=1", "mcp-server-git"))
        #expect(spec.argumentIndex == 2)
        #expect(spec.canPinInPlace)
        #expect(spec.pinnedArgument(version: "1.2.0") == "mcp-server-git==1.2.0")

        #expect(launch("pipx", "run", "--spec", "mcp-server-git==1.2.0", "mcp-server-git")?.isPinned == true)
        #expect(launch("pipx", "install", "mcp-server-git") == nil)
        #expect(launch("pipx", "run", "mcp-server-git@1.0") == nil)
    }

    @Test("a pin is only written for a version that names exactly one release")
    func exactVersions() throws {
        let npm = try #require(launch("npx", "pkg"))
        #expect(npm.pinnedArgument(version: "1.2.3") == "pkg@1.2.3")
        #expect(npm.pinnedArgument(version: "1.2") == nil)
        #expect(npm.pinnedArgument(version: "latest") == nil)
        #expect(npm.pinnedArgument(version: "1.2.3 || 2") == nil)

        let python = try #require(launch("uvx", "pkg"))
        #expect(python.pinnedArgument(version: "2024.1") == "pkg@2024.1")
        #expect(python.pinnedArgument(version: "1.0.0rc1") == "pkg@1.0.0rc1")
        #expect(python.pinnedArgument(version: "1.*") == nil)
    }

    @Test("provenance reads its package name from the same parser")
    func provenanceAgrees() {
        let server = Server(
            id: "s", name: "s", transport: .stdio, command: "uvx",
            args: ["--from", "awslabs.aws-mcp[cli]==1.0.0", "aws-mcp"], env: [], url: nil,
            enabledIn: [:], origin: .configFile(.cursor), fingerprint: "f",
            isBundled: false, definitionSource: "{}"
        )
        let provenance = MCPProvenanceResolver.identify(server: server)
        #expect(provenance.kind == .python)
        #expect(provenance.packageName == "awslabs.aws-mcp")
    }
}

// MARK: - Doctor

private func stdioServer(
    command: String,
    args: [String],
    enabledIn: [ClientID: Enablement] = [.cursor: .enabled]
) -> Server {
    Server(
        id: "pkg", name: "pkg", transport: .stdio, command: command, args: args, env: [],
        url: nil, enabledIn: enabledIn, origin: .configFile(.cursor),
        fingerprint: Server.fingerprint(command: command, args: args, url: nil),
        isBundled: false, definitionSource: "{}"
    )
}

@Suite("MCP Doctor — unpinned packages")
struct UnpinnedPackageDoctorTests {

    @Test("an unpinned npx package is a warning with the pin repair, even unchecked")
    func flagged() throws {
        let report = MCPDoctor.analyze(stdioServer(command: "npx", args: ["-y", "pkg@latest"]))
        let finding = try #require(report.findings.first { $0.code == "unpinned-package" })
        #expect(finding.severity == .warning)
        #expect(finding.action == .pinPackageVersion)
        #expect(finding.detail.contains("pkg@latest"))
        #expect(report.findings.contains { $0.code == "not-checked" })
    }

    @Test("pinned, unrecognized and remote servers are not flagged")
    func notFlagged() {
        for server in [
            stdioServer(command: "npx", args: ["-y", "pkg@1.2.3"]),
            stdioServer(command: "node", args: ["/Users/example/server.js"]),
            stdioServer(command: "docker", args: ["run", "-i", "ghcr.io/example/mcp@sha256:abc"]),
        ] {
            #expect(!MCPDoctor.analyze(server).findings.contains { $0.code == "unpinned-package" })
        }
    }

    @Test("a runner that cannot take a version in place is advice, not a repair")
    func adviceOnly() throws {
        let report = MCPDoctor.analyze(stdioServer(command: "pipx", args: ["run", "mcp-server-git"]))
        let finding = try #require(report.findings.first { $0.code == "unpinned-package" })
        #expect(finding.action == nil)
        #expect(finding.remediation.contains("--spec mcp-server-git==VERSION"))
    }

    @Test("edits cover every copy of the same package, each at its own position")
    func editsPerCopy() {
        var server = stdioServer(
            command: "npx", args: ["-y", "pkg"],
            enabledIn: [.cursor: .enabled, .claudeCode: .disabled, .codex: .enabled, .vsCode: .absent]
        )
        func copy(_ command: String, _ args: [String], bundled: Bool = false) -> ClientDefinition {
            ClientDefinition(
                transport: .stdio, command: command, args: args, env: [], url: nil,
                origin: .configFile(.cursor), isBundled: bundled, definitionSource: "{}"
            )
        }
        server.definitionsByClient = [
            .cursor: copy("npx", ["-y", "pkg"]),
            .claudeCode: copy("bunx", ["pkg@latest", "--flag"]),
            .codex: copy("npx", ["-y", "pkg@1.0.0"]),          // already pinned
            .vsCode: copy("npx", ["-y", "pkg"]),               // absent here
        ]

        let edits = PackagePin.edits(for: server, version: "2.0.0")
        #expect(edits[.cursor] == .init(index: 1, expected: "pkg", replacement: "pkg@2.0.0"))
        #expect(edits[.claudeCode] == .init(index: 0, expected: "pkg@latest", replacement: "pkg@2.0.0"))
        #expect(edits[.codex] == nil)
        #expect(edits[.vsCode] == nil)

        #expect(PackagePin.edits(for: server, version: "2.x").isEmpty)
    }
}

// MARK: - Writing

@Suite("ServerAuthoring — pinning one argument")
struct PackagePinWriteTests {

    private let cursorText = """
    {
      // Hand-written, and it stays that way.
      "mcpServers": {
        "GitHub": {
          "command": "npx",
          "args": [
            "-y",
            "@modelcontextprotocol/server-github" // the server
          ],
          "env": { "GITHUB_TOKEN": "ghp_example" }
        }
      }
    }
    """

    private let codexText = """
    # Codex config
    model = "o3"

    [mcp_servers.github]
    command = "npx"
    args = ["-y", "@modelcontextprotocol/server-github@latest"]   # floating
    enabled = false

    [mcp_servers.github.env]
    GITHUB_TOKEN = "ghp_example"
    """

    /// By identity: the row is one server spelled `GitHub` in Cursor and
    /// `github` in Codex, and which spelling names it depends on read order.
    private func githubServer(in harness: ToggleHarness) throws -> Server {
        try #require(harness.discover().servers.first { $0.id == "github" })
    }

    private func harness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(cursorText, to: ".cursor/mcp.json")
        try harness.home.write(codexText, to: ".codex/config.toml")
        return harness
    }

    @Test("JSON and TOML each change by exactly one string literal")
    func splicesOnlyTheArgument() throws {
        let harness = try harness()
        defer { harness.cleanUp() }

        let github = try githubServer(in: harness)
        let edits = PackagePin.edits(for: github, version: "2025.4.8")
        #expect(Set(edits.keys) == [.cursor, .codex])

        let result = try harness.authoring.replaceArgument(of: github, edits: edits)
        #expect(Set(result.changed) == [.cursor, .codex])
        #expect(result.backupIDs.count == 2)

        #expect(try harness.text(".cursor/mcp.json") == cursorText.replacingOccurrences(
            of: #""@modelcontextprotocol/server-github" // the server"#,
            with: #""@modelcontextprotocol/server-github@2025.4.8" // the server"#
        ))
        #expect(try harness.text(".codex/config.toml") == codexText.replacingOccurrences(
            of: #""@modelcontextprotocol/server-github@latest""#,
            with: #""@modelcontextprotocol/server-github@2025.4.8""#
        ))

        // Still off in Codex, and nothing left to flag.
        let after = try githubServer(in: harness)
        #expect(after.enabledIn[.codex] == .disabled)
        #expect(PackagePin.unpinnedLaunch(in: after) == nil)
        #expect(!MCPDoctor.analyze(after).findings.contains { $0.code == "unpinned-package" })
    }

    @Test("a file that no longer says what the edit expects is refused and rolled back")
    func refusesMovedArgument() throws {
        let harness = try harness()
        defer { harness.cleanUp() }

        let github = try githubServer(in: harness)
        var edits = PackagePin.edits(for: github, version: "1.0.0")
        // Codex sorts first and is written; Cursor then fails and Codex is restored.
        edits[.cursor] = .init(index: 1, expected: "something-else", replacement: "x@1.0.0")

        #expect(throws: (any Error).self) {
            try harness.authoring.replaceArgument(of: github, edits: edits)
        }
        #expect(try harness.text(".cursor/mcp.json") == cursorText)
        #expect(try harness.text(".codex/config.toml") == codexText)
    }

    @Test("a switched-off copy Kytto holds is pinned too, and stays off")
    func parkedCopy() throws {
        let harness = try harness()
        defer { harness.cleanUp() }

        let toggle = try harness.toggles.setEnabled(false, server: try githubServer(in: harness), in: .cursor)
        #expect(toggle.wasParked)
        let cursorOff = try harness.text(".cursor/mcp.json")

        let github = try githubServer(in: harness)
        let result = try harness.authoring.replaceArgument(
            of: github, edits: PackagePin.edits(for: github, version: "1.0.0")
        )
        #expect(result.changed == [.codex])
        #expect(result.parkedUpdated == [.cursor])
        #expect(try harness.text(".cursor/mcp.json") == cursorOff)

        let parked = try #require(harness.parkStore.parked(clientID: .cursor, serverName: "GitHub"))
        #expect(parked.sourceText.contains(#""@modelcontextprotocol/server-github@1.0.0" // the server"#))
        #expect(try githubServer(in: harness).enabledIn[.cursor] == .disabled)
    }

    @Test("an inline Codex definition is spliced in place")
    func inlineTOML() throws {
        let inline = """
        [mcp_servers]
        time = { command = "uvx", args = ["mcp-server-time"], enabled = true }
        """
        let document = try TOMLDocument.parse(inline)
        let text = try document.settingServerArgument(
            at: 0, expecting: "mcp-server-time", to: "mcp-server-time@0.6.2",
            forServer: "time", under: "mcp_servers"
        )
        #expect(text == inline.replacingOccurrences(of: #""mcp-server-time""#, with: #""mcp-server-time@0.6.2""#))
    }

    @Test("the JSON splice counts string elements the way the model does")
    func jsonPositionSkipsNonStrings() throws {
        let source = #"{"args": [1, "-y", null, "pkg"]}"#
        let text = try JSONDocument.parse(source).replacingString(
            inArrayAt: ["args"], position: 1, expecting: "pkg", with: "pkg@1.0.0"
        )
        #expect(text == #"{"args": [1, "-y", null, "pkg@1.0.0"]}"#)
        #expect(throws: JSONEditError.valueMismatch(["args"])) {
            try JSONDocument.parse(source).replacingString(
                inArrayAt: ["args"], position: 0, expecting: "pkg", with: "x"
            )
        }
    }
}
