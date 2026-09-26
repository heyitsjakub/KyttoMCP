import Foundation

public struct GatewayActivitySummary: Equatable, Sendable {
    public let events: [GatewayEvent]
    public let totalSessions: Int
    public let completedCalls: Int
    public let failedCalls: Int
    public let averageDurationMilliseconds: Int?

    public init(events: [GatewayEvent]) {
        self.events = events
        totalSessions = Set(events.map(\.sessionID)).count
        let completed = events.filter { $0.kind == .toolCallCompleted }
        completedCalls = completed.count
        failedCalls = completed.filter { $0.succeeded == false }.count
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

    public func summary(limit: Int = 500) -> GatewayActivitySummary {
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8)
        else { return GatewayActivitySummary(events: []) }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let events = text.split(separator: "\n", omittingEmptySubsequences: true)
            .suffix(max(1, limit))
            .compactMap { try? decoder.decode(GatewayEvent.self, from: Data($0.utf8)) }
            .sorted { $0.timestamp > $1.timestamp }
        return GatewayActivitySummary(events: events)
    }
}
