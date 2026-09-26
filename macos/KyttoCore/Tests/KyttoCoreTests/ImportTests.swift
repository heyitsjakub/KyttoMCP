import Foundation
import Testing
@testable import KyttoCore

@Suite("Agent import and MCP library")
struct ImportTests {
    @Test("strict agent JSON accepts env keys but never env values")
    func strictImport() throws {
        let document = try AgentImportDocument(json: #"""
        {
          "schemaVersion": 1,
          "servers": [{
            "name": "demo",
            "transport": "stdio",
            "command": "python3",
            "args": ["server.py"],
            "env": ["API_KEY"]
          }]
        }
        """#)

        let server = try #require(document.servers.first)
        #expect(server.name == "demo")
        #expect(server.envKeys == ["API_KEY"])
        #expect(server.draft.env.isEmpty)
    }

    @Test("agent import rejects unknown fields duplicates and literal secret values")
    func strictImportRejectsUnsafeShapes() {
        #expect(throws: AgentImportError.self) {
            try AgentImportDocument(json: #"{"schemaVersion":1,"extra":true,"servers":[]}"#)
        }
        #expect(throws: AgentImportError.self) {
            try AgentImportDocument(json: #"{"schemaVersion":1,"servers":[{"name":"a","transport":"stdio","command":"x"},{"name":"A","transport":"stdio","command":"y"}]}"#)
        }
        #expect(throws: AgentImportError.self) {
            try AgentImportDocument(json: #"{"schemaVersion":1,"servers":[{"name":"a","transport":"stdio","command":"x","env":{"TOKEN":"secret"}}]}"#)
        }
        #expect(throws: AgentImportError.self) {
            try AgentImportDocument(json: #"{"schemaVersion":1,"servers":[{"name":"a","transport":"stdio","command":"x",},]}"#)
        }
    }

    @Test("library scan finds known maps and safe entry-point suggestions without executing them")
    func libraryScan() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "KyttoLibraryTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let config = root.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(at: config.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(#"{"mcpServers":{"workspace":{"command":"node","args":["server.js"],"env":{"TOKEN":"secret"}}}}"#.utf8)
            .write(to: config)
        try Data("from mcp.server.fastmcp import FastMCP\n".utf8)
            .write(to: root.appending(path: "server.py"))

        let result = MCPLibraryScanner(root: root).scan()
        let mapped = try #require(result.candidates.first { $0.name == "workspace" })
        #expect(mapped.detectedClientIDs == [.cursor])
        #expect(mapped.missingClientIDs.contains(.claudeCode))
        #expect(mapped.envKeys == ["TOKEN"])

        let entry = try #require(result.candidates.first { $0.name == "server" })
        #expect(entry.commandSummary.contains("python3"))
        #expect(entry.warnings.contains { $0.contains("did not execute") })
    }
}
