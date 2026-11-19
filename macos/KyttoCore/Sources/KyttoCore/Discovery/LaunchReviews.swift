import Foundation

/// Pending reviews contain only masked summaries. Baselines are the actual
/// definitions observed during this session and are never serialized.
public final class LaunchReviews {
    public struct Field: Codable, Equatable, Sendable {
        public let name: String
        public let before: String
        public let after: String
    }

    public struct Alert: Codable, Equatable, Sendable {
        public let token: String
        public let serverID: String
        public let clientID: String
        public let file: String
        public let scope: String
        public let observedAt: String
        public let fields: [Field]
    }

    private struct Key: Hashable {
        let server: String
        let client: ClientID
        let path: String
    }
    private var known: [Key: ClientDefinition] = [:]
    private let paths: KyttoPaths
    private var file: URL { paths.root.appending(path: "launch-reviews.json") }
    public private(set) var alerts: [Alert] = []
    public private(set) var storageError: String?
    private var unreadable = false

    public init(paths: KyttoPaths) {
        self.paths = paths
        guard FileManager.default.fileExists(atPath: file.path) else { return }
        do { alerts = try JSONDecoder().decode([Alert].self, from: Data(contentsOf: file)) }
        catch {
            unreadable = true
            storageError = "The saved launch reviews could not be read. New reviews are available for this session."
        }
    }

    public func observe(_ result: DiscoveryResult, prior: DigestLedger, incoming: DigestLedger) {
        let failed = Set(result.diagnostics.filter { $0.severity == .error }.compactMap(\.clientID))
        var next = known.filter { failed.contains($0.key.client) }
        let clients = Dictionary(uniqueKeysWithValues: result.clients.map { ($0.id, $0) })
        var changed = false
        for server in result.servers {
            for (id, definition) in server.definitionsByClient {
                guard let location = definition.location, let client = clients[id] else { continue }
                let key = Key(server: server.id, client: id, path: location.file.path)
                next[key] = definition
                guard let old = known[key], let digest = incoming.digest(for: location.file),
                      prior.digest(for: location.file) != digest else { continue }
                let diff = Self.changes(from: old, to: definition)
                guard !diff.isEmpty else { continue }
                let index = alerts.firstIndex { $0.file == key.path && $0.clientID == id.rawValue && $0.serverID == server.id }
                var fields = index.map { alerts[$0].fields } ?? []
                for field in diff {
                    if let i = fields.firstIndex(where: { $0.name == field.name }) {
                        fields[i] = Field(name: field.name, before: fields[i].before, after: field.after)
                    } else { fields.append(field) }
                }
                let alert = Alert(token: UUID().uuidString, serverID: server.id, clientID: id.rawValue, file: key.path,
                                  scope: client.scopeDescription(for: location),
                                  observedAt: ISO8601DateFormatter().string(from: Date()), fields: fields)
                if let index { alerts[index] = alert } else { alerts.append(alert) }
                changed = true
            }
        }
        known = next
        if changed && !unreadable {
            do { try save(alerts); storageError = nil }
            catch { storageError = "Pending launch reviews could not be saved. They are available for this session." }
        }
    }

    public func acknowledge(_ token: String) throws {
        guard alerts.contains(where: { $0.token == token }) else { throw ReviewError.stale }
        let remaining = alerts.filter { $0.token != token }
        if !unreadable { try save(remaining); storageError = nil }
        alerts = remaining
    }

    public enum ReviewError: Error, LocalizedError {
        case stale
        public var errorDescription: String? { "The launch review changed. Review the latest observed fields." }
    }

    private func save(_ entries: [Alert]) throws {
        try paths.createPrivateDirectory(paths.root)
        try AtomicWriter.write(JSONEncoder().encode(entries), to: file, permissions: KyttoPaths.privateFilePermissions)
    }

    private static func changes(from old: ClientDefinition, to new: ClientDefinition) -> [Field] {
        var fields: [Field] = []
        func presence(_ text: String?) -> String { text == nil ? "Absent" : "Set (masked)" }
        if old.command != new.command {
            fields.append(Field(name: "Command", before: presence(old.command), after: presence(new.command)))
        }
        if old.args != new.args {
            fields.append(Field(name: "Arguments", before: "\(old.args.count) masked", after: "\(new.args.count) masked"))
        }
        func sorted(_ entries: [EnvEntry]) -> [EnvEntry] { entries.sorted { $0.key < $1.key } }
        if sorted(old.env) != sorted(new.env) {
            fields.append(Field(name: "Environment", before: "\(old.env.count) masked", after: "\(new.env.count) masked"))
        }
        if old.url != new.url {
            fields.append(Field(name: "Endpoint", before: presence(old.url), after: presence(new.url)))
        }
        if old.transport != new.transport {
            fields.append(Field(name: "Transport", before: old.transport.rawValue, after: new.transport.rawValue))
        }
        return fields
    }
}
