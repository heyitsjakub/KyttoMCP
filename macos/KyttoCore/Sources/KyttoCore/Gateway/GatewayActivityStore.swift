import Foundation

public struct GatewayActivitySummary: Equatable, Sendable {
    public let events: [GatewayEvent]
    public let totalSessions: Int
    public let completedCalls: Int
    public let failedCalls: Int
    public let averageDurationMilliseconds: Int?
    /// Calls Kytto refused because the route hides the tool.
    public let refusedCalls: Int
    /// Sessions whose server stopped while the client was still connected, and
    /// sessions the helper could not start.
    public let serverFailures: Int

    public init(events: [GatewayEvent]) {
        self.events = events
        totalSessions = Set(events.map(\.sessionID)).count
        let completed = events.filter { $0.kind == .toolCallCompleted }
        completedCalls = completed.count
        failedCalls = completed.filter { $0.succeeded == false }.count
        refusedCalls = events.filter { $0.kind == .toolCallRefused }.count
        serverFailures = events.filter {
            $0.kind == .sessionFailed || ($0.kind == .sessionEnded && $0.unexpected == true)
        }.count
        let durations = completed.compactMap(\.durationMilliseconds)
        averageDurationMilliseconds = durations.isEmpty ? nil : durations.reduce(0, +) / durations.count
    }
}

/// Read-only view of the append-only gateway journal.
///
/// A malformed or partially-written final line is ignored. The proxy can be
/// writing while the app reads, and an activity screen must never interfere
/// with the relay just to obtain a perfectly consistent snapshot.
public struct GatewayActivityStore: Sendable {
    private let url: URL

    public init(paths: KyttoPaths) {
        url = paths.gatewayEvents
    }

    /// How much of the journal's end is read. The panel shows the last few
    /// hundred events; reading a journal of any size whole, on the main actor,
    /// on every refresh is how a long-lived install grows a stutter.
    static let tailBytes = 2 * 1024 * 1024

    public func summary(limit: Int = 500) -> GatewayActivitySummary {
        let limit = max(1, limit)
        var lines = Self.tailLines(of: url)
        // Right after a rotation the new journal is nearly empty. The one it
        // replaced fills the view, so the panel does not go blank at 5 MB.
        if lines.count < limit {
            lines = Self.tailLines(of: url.appendingPathExtension("1")) + lines
        }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let events = lines.suffix(limit)
            .compactMap { try? decoder.decode(GatewayEvent.self, from: Data($0)) }
            .enumerated()
            // Timestamps have whole seconds; within one, the journal's own order
            // keeps "started" before "completed".
            .sorted { ($0.element.timestamp, $0.offset) > ($1.element.timestamp, $1.offset) }
            .map(\.element)
        return GatewayActivitySummary(events: events)
    }

    /// The complete lines in the last `tailBytes` of a journal file.
    private static func tailLines(of url: URL) -> [Data.SubSequence] {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        let start = size > UInt64(tailBytes) ? size - UInt64(tailBytes) : 0
        guard (try? handle.seek(toOffset: start)) != nil, let data = try? handle.readToEnd() else { return [] }
        // Decoded line by line and lossily: one line cut by a short write must
        // cost that line, not every event in the file.
        var lines = data.split(separator: 0x0A, omittingEmptySubsequences: true)
        if start > 0, !lines.isEmpty { lines.removeFirst() } // began mid-line
        return lines
    }
}
