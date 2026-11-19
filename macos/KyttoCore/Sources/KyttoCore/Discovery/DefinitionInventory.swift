import Foundation

/// File-backed copies from the accepted discovery pass. A parked entry has
/// no file location until it is restored.
public struct DefinitionInventory: Encodable, Sendable {
    public struct Copy: Encodable, Sendable {
        public let clientID: String
        public let name: String
        public let file: String
        public let scope: String
        public let repository: Bool
        public let transport: String
    }

    public struct Priority: Encodable, Sendable {
        public let workspace: String
        public let winner: String
        public let candidates: [String]
    }

    public let copies: [Copy]
    public let priorities: [Priority]
    public let overlapsScopes: Bool

    public static func inventories(in result: DiscoveryResult, folders: [ProjectFolder]) -> [String: Self] {
        let portableFolders = Dictionary(folders.map {
            (ClientRegistry.projectClientID(folder: $0.id, file: .portable), $0.path)
        }, uniquingKeysWith: { first, _ in first })
        return Dictionary(uniqueKeysWithValues: result.servers.map {
            ($0.id, Self(server: $0, clients: result.clients, portableFolders: portableFolders))
        })
    }

    private init(server: Server, clients: [DiscoveredClient], portableFolders: [ClientID: String]) {
        var entries: [Copy] = []
        var user: Copy?
        var local: [String: Copy] = [:]
        var project: [String: Copy] = [:]
        var hasWorkspace = false
        for client in clients {
            guard let definition = server.definitionsByClient[client.id], let location = definition.location else { continue }
            let workspace = location.claudeLocalWorkspace
            let portable = portableFolders[client.id]
            let scope = portable.map { "Project · " + $0 } ?? client.scopeDescription(for: location)
            let entry = Copy(clientID: client.id.rawValue, name: location.name, file: location.file.path,
                             scope: scope, repository: client.isRepositoryFile, transport: definition.transport.rawValue)
            entries.append(entry)
            if client.configurationScope == .workspace { hasWorkspace = true }
            guard server.enabledIn[client.id] == .enabled else { continue }
            if client.id == .claudeCode { user = entry }
            if let workspace { local[workspace] = entry }
            if let portable { project[portable] = entry }
        }
        copies = entries
        overlapsScopes = entries.count > 1 && hasWorkspace
        // https://code.claude.com/docs/en/mcp#scope-hierarchy-and-precedence
        // A workspace and an exact server key are both required for a comparison.
        priorities = Set(local.keys).union(project.keys).sorted().compactMap { workspace in
            let ordered = [local[workspace], project[workspace], user].compactMap { $0 }
            guard let winner = ordered.first(where: { candidate in
                ordered.contains { $0.clientID != candidate.clientID && $0.name == candidate.name }
            }) else { return nil }
            return Priority(workspace: workspace, winner: winner.clientID,
                            candidates: ordered.filter { $0.name == winner.name }.map(\.clientID))
        }
    }
}

extension DiscoveredClient {
    func scopeDescription(for location: DefinitionLocation) -> String {
        if let workspace = location.claudeLocalWorkspace { return "Local · " + workspace }
        if id == .claudeCode { return "User" }
        let scope = configurationScope.displayName
        return scopeLabel.isEmpty || scopeLabel == scope ? scope : scope + " · " + scopeLabel
    }
}
