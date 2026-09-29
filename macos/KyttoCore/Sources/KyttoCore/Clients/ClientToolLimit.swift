import Foundation

/// A cap a client puts on how many tools it hands the model at once.
///
/// Data on the descriptor, next to the config path and the schema quirks, for
/// the same reason those are: the number moves between client releases, and a
/// fix must stay a one-file change in `ClientRegistry` (§4). A client with no
/// documented cap has no value here — Kytto does not guess one, because a
/// warning that fires on a limit the client does not enforce teaches people to
/// ignore the warning.
public struct ClientToolLimit: Equatable, Sendable {
    /// The most tools the client will pass on.
    public let maxTools: Int
    /// What the client does with a request past the cap, in one sentence. It
    /// differs per client — one drops the overflow silently, another refuses
    /// the request — so it is written down with the client and not in the UI.
    public let pastLimitSummary: String

    public init(maxTools: Int, pastLimitSummary: String) {
        self.maxTools = maxTools
        self.pastLimitSummary = pastLimitSummary
    }
}

/// How close a client is to its tool cap.
public enum ToolLimitState: String, Codable, Sendable {
    case ok
    /// At or above `ToolBudget.nearFraction` of the cap.
    case near
    /// Past the cap: the client is already dropping or refusing tools.
    case over
}

/// One client's tool count, set against its cap.
public struct ClientToolBudget: Equatable, Sendable {
    public let clientID: ClientID
    /// Tools the client will be offered by servers switched on in it, as last
    /// measured, after Gateway masking. A lower bound when anything switched on
    /// has not been measured.
    public let toolCount: Int
    /// Tools the measured servers have that Gateway masking keeps from this
    /// client. Already subtracted from `toolCount`.
    public let maskedToolCount: Int
    /// Servers switched on in this client with no tool list to count — never
    /// checked, failed, remote, or waiting for sign-in.
    public let unmeasuredServerIDs: [String]
    public let limit: ClientToolLimit?
    /// Nil when the client has no known cap.
    public let state: ToolLimitState?

    public var isLowerBound: Bool { !unmeasuredServerIDs.isEmpty }

    /// The same scale the Doctor and the context optimizer speak, so a surface
    /// that sorts findings can sort this one with them. Near is worth knowing;
    /// over is already costing the user tools.
    public var severity: DoctorSeverity? {
        switch state {
        case .over: .warning
        case .near: .info
        case .ok, nil: nil
        }
    }
}

public enum ToolBudget {
    /// "Near" starts at 80 % of the cap — close enough that the next server
    /// switched on is likely to cross it.
    public static let nearFraction = 0.8

    /// Counts what a client hands its model.
    ///
    /// Pure: servers carry their last health check, routes carry the Gateway
    /// allow lists, and nothing is spawned or read. Only servers `enabled` in
    /// the client count — `disabled` and `absent` offer the model nothing.
    public static func evaluate(
        clientID: ClientID,
        limit: ClientToolLimit?,
        servers: [Server],
        routes: [GatewayRoute]
    ) -> ClientToolBudget {
        var count = 0
        var masked = 0
        var unmeasured: [String] = []

        for server in servers where server.enabledIn[clientID] == .enabled {
            guard let offered = measuredToolCount(server) else {
                unmeasured.append(server.id)
                continue
            }
            let route = routes.first { $0.serverID == server.id && $0.clientID == clientID }
            let visible = route?.exposedTools.map { exposedCount($0, of: server, offered: offered) } ?? offered
            count += visible
            masked += offered - visible
        }

        return ClientToolBudget(
            clientID: clientID,
            toolCount: count,
            maskedToolCount: masked,
            unmeasuredServerIDs: unmeasured.sorted(),
            limit: limit,
            state: limit.map { state(count: count, limit: $0.maxTools) }
        )
    }

    static func state(count: Int, limit: Int) -> ToolLimitState {
        if count > limit { return .over }
        if Double(count) >= Double(limit) * nearFraction { return .near }
        return .ok
    }

    /// The tool count from a passing check, or nil when there is nothing to
    /// count. `toolCount` rather than `tools.count`: records written before the
    /// per-tool breakdown carry a count with a partial list.
    private static func measuredToolCount(_ server: Server) -> Int? {
        guard let health = server.health, health.status == .passed else { return nil }
        return health.toolCount ?? health.tools.count
    }

    /// What survives an allow list (§7.11). The filter passes a tool only if the
    /// server offers it and the list names it, so with a complete tool list the
    /// answer is the intersection. With a partial one — an older record — the
    /// names cannot be checked, and the list's own length, capped at what the
    /// server offers, is the best figure there is.
    private static func exposedCount(_ exposed: [String], of server: Server, offered: Int) -> Int {
        let names = Set(server.health?.tools.map(\.name) ?? [])
        if names.count >= offered {
            return Set(exposed).intersection(names).count
        }
        return min(Set(exposed).count, offered)
    }
}
