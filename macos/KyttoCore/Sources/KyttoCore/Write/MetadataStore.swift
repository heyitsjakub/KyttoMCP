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

    /// A malformed store is not an empty store (as in `ParkStore`). Reading
    /// screens may show nothing, but a write must stop rather than replace every
    /// server's Contract Guard baseline and pending alert with an empty file.
    private func loadForMutation() throws -> [String: ServerMetadata] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        let data = try Data(contentsOf: url)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom(Self.decodeDate)
        do {
            return try decoder.decode([String: ServerMetadata].self, from: data)
        } catch {
            throw MetadataStoreError.unreadable(fileName: url.lastPathComponent)
        }
    }

    public func record(
        health: HealthResult,
        tokenWeight: TokenWeight?,
        for serverID: String
    ) throws {
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
        var entry = entries[serverID] ?? ServerMetadata()
        // Two checks of one server can finish out of order (a single check and
        // "Check all"). The one that started later describes the server better.
        if let previous = entry.health, previous.checkedAt > health.checkedAt { return }
        if health.status == .passed {
            let baseline = entry.contractTools ?? entry.health.flatMap {
                $0.status == .passed ? $0.tools : nil
            }
            if var baseline {
                // A baseline from before schemas and annotations were recorded
                // adopts them from this check instead of reporting each tool
                // as changed (roadmap §18).
                baseline = ContractGuard.upgradingLegacyBaseline(baseline, with: health.tools)
                // Every check is measured against the contract the user last
                // reviewed, so changes add up until they are acknowledged. Only
                // `acknowledgeContract` moves the baseline (§7.10); a check that
                // moved it would let an unreviewed change vanish on the next run.
                let changes = ContractGuard.changes(from: baseline, to: health.tools)
                let wasPending = entry.contractChanges?.isEmpty == false
                entry.contractChanges = changes
                // A repeat check is not a new change: keep when it was first seen.
                entry.contractChangedAt = changes.isEmpty
                    ? nil
                    : (wasPending ? entry.contractChangedAt : nil) ?? health.checkedAt
                entry.contractTools = baseline
            } else {
                entry.contractTools = health.tools
            }
            entry.lastPassingTools = health.tools
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
    /// This and the first passing check are the only things that move the
    /// baseline. Every check compares against `contractTools`, so a change keeps
    /// being reported until it is reviewed here (or the server reverts it).
    /// Acknowledging makes the last passing contract — the one shown in the
    /// diff — the new baseline, and the next real change is measured against it.
    public func acknowledgeContract(for serverID: String) throws {
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
        guard var entry = entries[serverID], entry.contractChanges?.isEmpty == false else { return }
        entry.contractChanges = nil
        entry.contractChangedAt = nil
        // The last passing contract, not the last check: a check that failed
        // after the diff was shown must not leave the old baseline in place.
        entry.contractTools = entry.knownTools
        entries[serverID] = entry
        try save(entries)
        }
    }

    /// Carries everything remembered about a server to its new name.
    ///
    /// Metadata is keyed by name, so without this a rename would drop the
    /// Contract Guard baseline and any unreviewed alert — the next check under
    /// the new name would adopt whatever the server now says — and leave the old
    /// entry for a later server that reuses the name.
    public func rename(from oldID: String, to newID: String) throws {
        guard oldID != newID else { return }
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
        guard let entry = entries.removeValue(forKey: oldID) else { return }
        entries[newID] = entry
        try save(entries)
        }
    }

    /// Drops everything remembered about a server. Called when one is deleted,
    /// so a later server reusing the name does not inherit a stale red dot.
    public func forget(_ serverID: String) throws {
        try MutationCoordinator.sync {
        var entries = try loadForMutation()
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

public enum MetadataStoreError: LocalizedError, Equatable {
    case unreadable(fileName: String)

    public var errorDescription: String? {
        switch self {
        case .unreadable(let fileName):
            "Kytto could not read its own measurements file (\(fileName)), so it left it untouched instead of overwriting it. Move the file out of Kytto's data folder to start fresh."
        }
    }
}
