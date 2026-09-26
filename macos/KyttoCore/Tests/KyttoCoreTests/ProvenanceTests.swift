import Foundation
import Testing
@testable import KyttoCore

private func provenanceServer(
    command: String,
    args: [String] = [],
    transport: Transport = .stdio,
    url: String? = nil
) -> Server {
    Server(
        id: "provenance",
        name: "provenance",
        transport: transport,
        command: transport == .stdio ? command : nil,
        args: args,
        env: [],
        url: url,
        enabledIn: [:],
        origin: .configFile(.cursor),
        fingerprint: "test",
        isBundled: false,
        definitionSource: "{}"
    )
}

@Suite("MCP provenance")
struct ProvenanceTests {
    @Test("package-manager commands expose inferred package provenance")
    func packageManagers() {
        let npm = MCPProvenanceResolver.identify(server: provenanceServer(
            command: "npx",
            args: ["-y", "@modelcontextprotocol/server-filesystem@1.2.3"]
        ))
        #expect(npm.kind == .npm)
        #expect(npm.packageName == "@modelcontextprotocol/server-filesystem")
        #expect(npm.confidence == "inferred")

        let python = MCPProvenanceResolver.identify(server: provenanceServer(
            command: "uvx",
            args: ["mcp-server-time"]
        ))
        #expect(python.kind == .python)
        #expect(python.packageName == "mcp-server-time")
    }

    @Test("local package manifests are identified without executing code")
    func localManifest() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "KyttoProvenance-\(UUID().uuidString)", directoryHint: .isDirectory)
        let bin = root.appending(path: "bin/server.js")
        try FileManager.default.createDirectory(at: bin.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"name":"local-mcp","version":"2.4.0","homepage":"https://example.com/local-mcp"}"#.utf8)
            .write(to: root.appending(path: "package.json"))
        try Data("module.exports = {}\n".utf8).write(to: bin)
        defer { try? FileManager.default.removeItem(at: root) }

        let result = MCPProvenanceResolver.identify(server: provenanceServer(command: bin.path))
        #expect(result.kind == .localPackage)
        #expect(result.packageName == "local-mcp")
        #expect(result.installedVersion == "2.4.0")
        #expect(result.sourceURL == "https://example.com/local-mcp")
    }

    @Test("maintenance state separates health from source uncertainty and staleness")
    func maintenanceStates() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let healthy = HealthResult(
            status: .passed, checkedAt: now, toolCount: 0, tools: [], message: nil,
            stderr: nil, durationSeconds: 0.1, serverName: "mcp", serverVersion: "1.0.0"
        )
        let failed = HealthResult(
            status: .failed, checkedAt: now, toolCount: nil, tools: [], message: "failed",
            stderr: "error", durationSeconds: 0.1, serverName: nil, serverVersion: nil
        )
        let known = MCPProvenance(kind: .npm, packageName: "demo", latestVersion: "1.2.0", confidence: "inferred")
        let unknown = MCPProvenance(kind: .unknown)

        #expect(MCPProvenanceResolver.maintenanceState(health: healthy, provenance: known, installedVersion: "1.0.0") == .staleButResponsive)
        #expect(MCPProvenanceResolver.maintenanceState(health: healthy, provenance: unknown) == .unknownSource)
        #expect(MCPProvenanceResolver.maintenanceState(health: failed, provenance: known) == .unhealthy)
        #expect(MCPProvenanceResolver.maintenanceState(health: nil, provenance: known) == .unchecked)
    }
}
