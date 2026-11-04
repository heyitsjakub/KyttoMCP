import Foundation
import Testing
@testable import KyttoCore

private func stdioServer(_ name: String, command: String, args: [String]) -> Server {
    Server(
        id: Server.identity(for: name), name: name, transport: .stdio, command: command, args: args,
        env: [], url: nil, enabledIn: [.cursor: .enabled], origin: .configFile(.cursor),
        fingerprint: Server.fingerprint(command: command, args: args, url: nil),
        isBundled: false, definitionSource: "{}"
    )
}

@Suite("Package safety — look-alike names")
struct PackageLookAlikeTests {

    @Test("official-looking names are caught", arguments: [
        ("mcp-server-github", "@modelcontextprotocol/server-github"),
        ("@modelcontextprotocoI/server-filesystem", "@modelcontextprotocol/server-filesystem"),
        ("@model-context-protocol/server-memory", "@modelcontextprotocol/server-memory"),
        ("@playwrigth/mcp", "@playwright/mcp"),
        ("@modelcontextprotocol/server-filesytem", "@modelcontextprotocol/server-filesystem"),
        ("github-mcp-server", "@modelcontextprotocol/server-github"),
    ])
    func npmLookAlikes(_ name: String, _ official: String) {
        #expect(PackageSafety.lookAlike(of: name, ecosystem: .npm) == official)
    }

    @Test("the official packages and unrelated ones are left alone", arguments: [
        "@modelcontextprotocol/server-github", "@playwright/mcp", "firecrawl-mcp",
        "@upstash/context7-mcp", "some-unrelated-tool", "@acme/internal-mcp", "@sentry/mcp-server",
    ])
    func npmClean(_ name: String) {
        #expect(PackageSafety.lookAlike(of: name, ecosystem: .npm) == nil)
    }

    @Test("PyPI names one letter off an official one are caught; the official ones are not")
    func pypi() {
        #expect(PackageSafety.lookAlike(of: "mcp-server-fetsh", ecosystem: .python) == "mcp-server-fetch")
        #expect(PackageSafety.lookAlike(of: "mcp_server.fetch", ecosystem: .python) == nil)
        #expect(PackageSafety.lookAlike(of: "mcp-server-time", ecosystem: .python) == nil)
        #expect(PackageSafety.lookAlike(of: "fastapi", ecosystem: .python) == nil)
    }

    @Test("a look-alike becomes a Doctor finding offering the check")
    func finding() {
        let server = stdioServer("github", command: "npx", args: ["-y", "mcp-server-github"])
        let report = MCPDoctor.reports(for: [server], clients: [.cursor]).first
        let finding = report?.findings.first { $0.code == "look-alike-package" }
        #expect(finding?.severity == .warning)
        #expect(finding?.action == .checkPackageSafety)
    }
}

@Suite("Package safety — checked results")
struct PackageSafetyResultTests {

    @Test("an unpinned query keeps only malware; a pinned one keeps every advisory")
    func osvParsing() throws {
        let answer = Data(#"{"vulns":[{"id":"MAL-2026-5479","summary":"Malicious code in mcp-server-github"},{"id":"GHSA-xxxx-yyyy-zzzz","summary":"Path traversal"}]}"#.utf8)
        #expect(try PackageSafety.advisories(fromOSV: answer, versionQueried: false).map(\.id) == ["MAL-2026-5479"])
        #expect(try PackageSafety.advisories(fromOSV: answer, versionQueried: true).count == 2)
        #expect(try PackageSafety.advisories(fromOSV: Data("{}".utf8), versionQueried: true).isEmpty)
    }

    @Test("the query names the package, its registry and a pinned version only")
    func query() throws {
        let pinned = try #require(PackageLaunch.parse(command: "npx", args: ["-y", "@scope/pkg@1.2.3"]))
        let object = try JSONSerialization.jsonObject(with: PackageSafety.osvQuery(for: pinned)) as? [String: Any]
        #expect(object?["version"] as? String == "1.2.3")
        #expect((object?["package"] as? [String: String]) == ["name": "@scope/pkg", "ecosystem": "npm"])

        let loose = try #require(PackageLaunch.parse(command: "uvx", args: ["mcp-server-fetch"]))
        let looseObject = try JSONSerialization.jsonObject(with: PackageSafety.osvQuery(for: loose)) as? [String: Any]
        #expect(looseObject?["version"] == nil)
        #expect((looseObject?["package"] as? [String: String])?["ecosystem"] == "PyPI")
    }

    @Test("install-time scripts are read, other scripts are not")
    func scripts() throws {
        let document = Data(#"{"name":"x","version":"1.0.0","scripts":{"postinstall":"node collect.js","test":"jest","build":"tsc"}}"#.utf8)
        #expect(try PackageSafety.installScripts(fromNPMVersion: document) == ["postinstall": "node collect.js"])
    }

    @Test("findings follow the report, and a report for another package is ignored")
    func findings() throws {
        let server = stdioServer("github", command: "npx", args: ["-y", "mcp-server-github"])
        let report = PackageSafetyReport(
            ecosystem: .npm, packageName: "mcp-server-github", version: nil,
            advisories: [.init(id: "MAL-2026-5479", summary: nil)],
            installScripts: ["postinstall": "node collect.js"], checkedAt: Date()
        )
        let codes = PackageSafety.findings(for: server, report: report).map(\.code)
        #expect(codes == ["look-alike-package", "known-malware", "install-script"])
        #expect(PackageSafety.findings(for: server, report: report).first?.action == nil)
        #expect(PackageSafety.findings(for: server, report: report).first { $0.code == "known-malware" }?.severity == .error)

        let edited = stdioServer("filesystem", command: "npx", args: ["-y", "@modelcontextprotocol/server-filesystem"])
        #expect(PackageSafety.findings(for: edited, report: report).isEmpty)
    }

    @Test("the npm deprecation message is read; false or a missing field is not deprecation")
    func deprecationParsing() throws {
        let deprecated = Data(#"{"name":"x","version":"1.0.0","deprecated":"Package no longer supported."}"#.utf8)
        #expect(try PackageSafety.deprecation(fromNPMVersion: deprecated) == "Package no longer supported.")
        #expect(try PackageSafety.deprecation(fromNPMVersion: Data(#"{"deprecated":false}"#.utf8)) == nil)
        #expect(try PackageSafety.deprecation(fromNPMVersion: Data(#"{"name":"x"}"#.utf8)) == nil)
    }

    @Test("a checked deprecated package gets a finding; an archived one is not reported twice")
    func deprecatedFinding() throws {
        let server = stdioServer("old", command: "npx", args: ["-y", "some-old-mcp"])
        let report = PackageSafetyReport(
            ecosystem: .npm, packageName: "some-old-mcp", version: nil, advisories: [],
            installScripts: [:], deprecation: "Use new-mcp instead", checkedAt: Date()
        )
        #expect(PackageSafety.findings(for: server, report: report).map(\.code) == ["deprecated-package"])

        let archived = stdioServer("github", command: "npx", args: ["-y", "@modelcontextprotocol/server-github"])
        let archivedReport = PackageSafetyReport(
            ecosystem: .npm, packageName: "@modelcontextprotocol/server-github", version: nil, advisories: [],
            installScripts: [:], deprecation: "Package no longer supported.", checkedAt: Date()
        )
        #expect(PackageSafety.findings(for: archived, report: archivedReport).map(\.code) == ["archived-package"])
    }
}

@Suite("Package safety — archived reference servers")
struct PackageArchivedTests {

    @Test("archived reference servers are flagged offline", arguments: [
        ("npx", ["-y", "@modelcontextprotocol/server-github"]),
        ("npx", ["-y", "@modelcontextprotocol/server-puppeteer@2025.5.12"]),
        ("npx", ["-y", "@modelcontextprotocol/server-slack"]),
        ("uvx", ["mcp-server-sqlite", "--db-path", "x.db"]),
    ])
    func flagged(_ command: String, _ args: [String]) {
        let server = stdioServer("s", command: command, args: args)
        let finding = PackageSafety.findings(for: server, report: nil).first
        #expect(finding?.code == "archived-package")
        #expect(finding?.severity == .warning)
        #expect(finding?.action == .checkPackageSafety)
    }

    @Test("maintained reference servers are left alone", arguments: [
        "@modelcontextprotocol/server-filesystem", "@modelcontextprotocol/server-memory",
        "@modelcontextprotocol/server-everything", "@modelcontextprotocol/server-sequential-thinking",
    ])
    func maintained(_ name: String) {
        let server = stdioServer("s", command: "npx", args: ["-y", name])
        #expect(PackageSafety.findings(for: server, report: nil).isEmpty)
    }

    @Test("a look-alike of an archived server is not told to switch to it")
    func lookAlikeOfArchived() throws {
        let server = stdioServer("github", command: "npx", args: ["-y", "mcp-server-github"])
        let finding = try #require(PackageSafety.findings(for: server, report: nil).first { $0.code == "look-alike-package" })
        #expect(!finding.remediation.contains("change the command"))
        #expect(finding.remediation.contains("no longer maintained"))

        let live = stdioServer("fs", command: "npx", args: ["-y", "@modelcontextprotocol/server-filesytem"])
        let liveFinding = try #require(PackageSafety.findings(for: live, report: nil).first { $0.code == "look-alike-package" })
        #expect(liveFinding.remediation.contains("change the command"))
    }
}
