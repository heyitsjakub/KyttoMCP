import Foundation
import Testing
@testable import KyttoCore

/// Roadmap §12: repository MCP files in folders the user chose, read-only,
/// with nothing outside those exact files opened.
@Suite("Project folders — repository MCP files")
struct ProjectFolderTests {

    private func discover(_ home: FakeHome, _ folder: ProjectFolder) -> DiscoveryResult {
        Discovery(
            home: home.root,
            locator: StubAppLocator(),
            descriptors: ClientRegistry.projectDescriptors(for: folder)
        ).run()
    }

    @Test("each known file present becomes a read-only workspace source")
    func readsKnownFiles() throws {
        let home = try FakeHome(); defer { home.cleanUp() }
        let cursor = #"{"mcpServers":{"repo-tool":{"command":"npx","args":["-y","repo-tool"]}}}"#
        let vscode = #"{"servers":{"vs-tool":{"type":"stdio","command":"node","args":["s.js"]}},"inputs":[]}"#
        try home.write(cursor, to: "work/shop/.cursor/mcp.json")
        try home.write(vscode, to: "work/shop/.vscode/mcp.json")
        let folder = ProjectFolder(path: home.root.appending(path: "work/shop").path)

        let result = discover(home, folder)
        #expect(result.clients.count == 2)
        for client in result.clients {
            #expect(client.isReadOnly)
            #expect(client.isRepositoryFile)
            #expect(client.configurationScope == .workspace)
            #expect(client.scopeLabel == "shop")
        }
        #expect(Set(result.servers.map(\.name)) == ["repo-tool", "vs-tool"])
        // Read, never written.
        #expect(try String(contentsOf: home.root.appending(path: "work/shop/.cursor/mcp.json"), encoding: .utf8) == cursor)
    }

    @Test("nothing but the three exact files is read")
    func noCrawling() throws {
        let home = try FakeHome(); defer { home.cleanUp() }
        // Look-alikes in places Kytto must not look.
        try home.write(#"{"mcpServers":{"nested":{"command":"x"}}}"#, to: "work/shop/packages/app/.mcp.json")
        try home.write(#"{"mcpServers":{"other":{"command":"x"}}}"#, to: "work/shop/mcp.json")
        try home.write(#"{"mcpServers":{"claude":{"command":"x"}}}"#, to: "work/shop/.claude/settings.json")
        let folder = ProjectFolder(path: home.root.appending(path: "work/shop").path)
        let result = discover(home, folder)
        #expect(result.clients.isEmpty)
        #expect(result.servers.isEmpty)
    }

    @Test("ids are stable per folder and file, and differ between them")
    func stableIDs() {
        let folder = UUID()
        let cursor = ClientRegistry.projectClientID(folder: folder, file: .cursor)
        #expect(cursor == ClientRegistry.projectClientID(folder: folder, file: .cursor))
        #expect(cursor != ClientRegistry.projectClientID(folder: folder, file: .vscode))
        #expect(cursor != ClientRegistry.projectClientID(folder: UUID(), file: .cursor))
    }

    @Test("a relative folder path produces nothing")
    func relativeFolder() {
        #expect(ClientRegistry.projectDescriptors(for: ProjectFolder(path: "work/shop"), fileExists: { _ in true }).isEmpty)
    }

    @Test("a server only in repository files is repository-only; one also elsewhere is not")
    func repositoryOnly() throws {
        let home = try FakeHome(); defer { home.cleanUp() }
        try home.write(#"{"mcpServers":{"repo-tool":{"command":"npx","args":["repo-tool"]},"shared":{"command":"node"}}}"#,
                       to: "work/shop/.mcp.json")
        try home.write(#"{"mcpServers":{"shared":{"command":"node"}}}"#, to: ".cursor/mcp.json")
        let folder = ProjectFolder(path: home.root.appending(path: "work/shop").path)
        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(),
            descriptors: ClientRegistry.all + ClientRegistry.projectDescriptors(for: folder)
        ).run()
        let repositoryClients = Set(result.clients.filter(\.isRepositoryFile).map(\.id))
        #expect(repositoryClients.count == 1)
        let repo = try #require(result.servers.first { $0.name == "repo-tool" })
        let shared = try #require(result.servers.first { $0.name == "shared" })
        #expect(ClientRegistry.isRepositoryOnly(repo, repositoryClients: repositoryClients))
        #expect(!ClientRegistry.isRepositoryOnly(shared, repositoryClients: repositoryClients))
    }

    @Test("settings keep absolute, distinct folders and survive an older file")
    func settings() throws {
        let id = UUID()
        var settings = KyttoSettings.default
        settings.projectFolders = [
            ProjectFolder(id: id, path: "/Users/example/work/shop/", label: "  "),
            ProjectFolder(path: "/Users/example/work/shop"),
            ProjectFolder(path: "relative/path"),
        ]
        let cleaned = settings.sanitized()
        #expect(cleaned.projectFolders.count == 1)
        #expect(cleaned.projectFolders.first?.id == id)
        #expect(cleaned.projectFolders.first?.label == "shop")

        let old = try JSONDecoder().decode(KyttoSettings.self, from: Data(#"{"backupRetention":20}"#.utf8))
        #expect(old.projectFolders.isEmpty)
        let roundTrip = try JSONDecoder().decode(KyttoSettings.self, from: JSONEncoder().encode(cleaned))
        #expect(roundTrip.projectFolders == cleaned.projectFolders)
    }
}
