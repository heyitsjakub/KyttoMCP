import Foundation

/// Definitions removed from a client only in order to switch them off.
///
/// Cursor, VS Code and Claude Desktop's manual `mcpServers` have no "disabled"
/// flag — Cursor and VS Code keep that state in their own internal storage,
/// which Kytto will not write to. So switching a server off there means deleting
/// it, and deleting it is only acceptable if switching back restores exactly
/// what was there.
///
/// What is stored is the definition's **original source text**, not a
/// re-serialization of the model. Turning a server off and on again gives back
/// the same bytes, comments and formatting included.
public struct ParkStore: Sendable {
    public struct Entry: Codable, Equatable, Sendable {
        public let clientID: ClientID
        public let serverName: String
        /// Verbatim JSON source of the server's definition.
        public let sourceText: String
        public let parkedAt: Date
    }

    private let paths: KyttoPaths

    public init(paths: KyttoPaths) {
        self.paths = paths
    }

    private static func key(_ clientID: ClientID, _ serverName: String) -> String {
        "\(clientID.rawValue)::\(Server.identity(for: serverName))"
    }

    public func park(clientID: ClientID, serverName: String, sourceText: String) throws {
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
        entries[Self.key(clientID, serverName)] = Entry(
            clientID: clientID,
            serverName: serverName,
            sourceText: sourceText,
            parkedAt: Date()
        )
        try save(entries)
        }
    }

    public func parked(clientID: ClientID, serverName: String) -> Entry? {
        load()[Self.key(clientID, serverName)]
    }

    public func unpark(clientID: ClientID, serverName: String) throws {
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
        guard entries.removeValue(forKey: Self.key(clientID, serverName)) != nil else { return }
        try save(entries)
        }
    }

    public func all() -> [Entry] {
        load().values.sorted { $0.parkedAt > $1.parkedAt }
    }

    // MARK: - Storage

    private func load() -> [String: Entry] {
        guard let data = try? Data(contentsOf: paths.parked) else { return [:] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return (try? decoder.decode([String: Entry].self, from: data)) ?? [:]
    }

    /// A malformed store is not an empty store. Read-only screens may show no
    /// entries, but a mutation must stop rather than erase the recovery data.
    private func loadForMutation() throws -> [String: Entry] {
        guard FileManager.default.fileExists(atPath: paths.parked.path) else { return [:] }
        let data = try Data(contentsOf: paths.parked)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode([String: Entry].self, from: data)
    }

    private func save(_ entries: [String: Entry]) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(entries)
        guard let text = String(data: data, encoding: .utf8) else { return }
        // A parked definition is the config's own text, `env` values included,
        // so the store is kept as private as a backup.
        try paths.createPrivateDirectory(paths.root)
        try AtomicWriter.write(text, to: paths.parked, permissions: KyttoPaths.privateFilePermissions)
    }
}
