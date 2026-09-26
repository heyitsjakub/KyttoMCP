import Foundation

/// Health results and token measurements, keyed by server id.
///
/// §5: config files stay authoritative for what is enabled. This is the other
/// half — what Kytto measured — and it belongs to Kytto, not to any client.
/// Written only when a check actually runs, never on launch (§6.6).
public struct MetadataStore: Sendable {
    private let url: URL

    public init(paths: KyttoPaths) {
        url = paths.root.appending(path: "server-metadata.json")
    }

    public func all() -> [String: ServerMetadata] {
        guard let data = try? Data(contentsOf: url) else { return [:] }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom(Self.decodeDate)
        return (try? decoder.decode([String: ServerMetadata].self, from: data)) ?? [:]
    }

    public func metadata(for serverID: String) -> ServerMetadata? {
        all()[serverID]
    }

    public func record(
        health: HealthResult,
        tokenWeight: TokenWeight?,
        for serverID: String
    ) throws {
        try MutationCoordinator.sync {
        var entries = all()
        var entry = entries[serverID] ?? ServerMetadata()
        if health.status == .passed {
            let baseline = entry.contractTools ?? entry.health.flatMap {
                $0.status == .passed ? $0.tools : nil
            }
            if let baseline {
                let changes = ContractGuard.changes(from: baseline, to: health.tools)
                entry.contractChanges = changes
                entry.contractChangedAt = changes.isEmpty ? nil : health.checkedAt
            }
            entry.contractTools = health.tools
        }
        entry.health = health
        // A failed check tells us nothing new about token weight, so the last
        // good measurement survives rather than being blanked out.
        if let tokenWeight { entry.tokenWeight = tokenWeight }
        entries[serverID] = entry
        try save(entries)
        }
    }

    /// Marks the current tool contract as reviewed.
    ///
    /// Without this, an alert has no way to end: `contractChanges` is rewritten
    /// only by the next check that finds *different* tools, so a change that has
    /// been read and accepted keeps being reported until the server happens to
    /// change again. Clearing the changes while keeping `contractTools` is what
    /// makes the acceptance mean something — the reviewed contract becomes the
    /// baseline, and the next real change is measured against it.
    public func acknowledgeContract(for serverID: String) throws {
        try MutationCoordinator.sync {
        var entries = all()
        guard var entry = entries[serverID], entry.contractChanges?.isEmpty == false else { return }
        entry.contractChanges = nil
        entry.contractChangedAt = nil
        entry.contractTools = entry.health.flatMap { $0.status == .passed ? $0.tools : nil }
            ?? entry.contractTools
        entries[serverID] = entry
        try save(entries)
        }
    }

    /// Drops everything remembered about a server. Called when one is deleted,
    /// so a later server reusing the name does not inherit a stale red dot.
    public func forget(_ serverID: String) throws {
        try MutationCoordinator.sync {
        var entries = all()
        guard entries.removeValue(forKey: serverID) != nil else { return }
        try save(entries)
        }
    }

    /// ISO-8601 *with* fractional seconds.
    ///
    /// `JSONEncoder.iso8601` truncates to whole seconds, which means a timestamp
    /// does not survive a round trip. The file stays readable either way, so
    /// there is no reason to lose the milliseconds.
    private static func makeFormatter() -> ISO8601DateFormatter {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }

    private static func encodeDate(_ date: Date, into encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(makeFormatter().string(from: date))
    }

    private static func decodeDate(_ decoder: any Decoder) throws -> Date {
        let text = try decoder.singleValueContainer().decode(String.self)
        guard let date = makeFormatter().date(from: text) else {
            throw DecodingError.dataCorrupted(
                .init(codingPath: decoder.codingPath, debugDescription: "not an ISO-8601 date: \(text)")
            )
        }
        return date
    }

    private func save(_ entries: [String: ServerMetadata]) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom(Self.encodeDate)
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(entries)
        guard let text = String(data: data, encoding: .utf8) else { return }
        try AtomicWriter.write(text, to: url)
    }
}
