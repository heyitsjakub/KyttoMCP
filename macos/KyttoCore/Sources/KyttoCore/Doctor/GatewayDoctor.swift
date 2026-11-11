import Foundation

/// Doctor findings for servers in Gateway mode.
///
/// A client reports every one of these the same way — the server "failed" —
/// because all it sees is a helper that exited. Kytto is the only place that
/// knows why, so it has to say: the helper is gone, the route is gone, the
/// server keeps stopping, or it never started.
public enum GatewayDoctor {
    /// How far back session failures are still worth a finding. A crash last
    /// week that has not come back is history, not something to act on.
    public static let failureWindow: TimeInterval = 24 * 60 * 60

    /// Findings by server id.
    public static func findings(
        diagnoses: [GatewayRouteDiagnosis],
        events: [GatewayEvent],
        routes: [GatewayRoute] = [],
        now: Date = Date()
    ) -> [String: [DoctorFinding]] {
        var result: [String: [DoctorFinding]] = [:]

        for diagnosis in diagnoses {
            let client = clientName(diagnosis.clientID.rawValue)
            switch diagnosis.problem {
            case .helperMissing(let path, let repairable):
                result[diagnosis.serverID, default: []].append(DoctorFinding(
                    code: "gateway-helper-missing",
                    severity: .error,
                    title: "Gateway helper is missing in \(client)",
                    detail: """
                    \(client) starts this server through Kytto's helper at \(path), and nothing is there. \
                    Kytto was probably moved, renamed or replaced after Gateway mode was turned on, so \
                    \(client) cannot start the server.
                    """,
                    remediation: repairable
                        ? "Repair points the definition at this copy of Kytto. Nothing else in the config changes."
                        : "Move Kytto to the Applications folder and open it from there, then repair the route here.",
                    action: repairable ? .repairGatewayRoute : nil,
                    routeID: diagnosis.routeID
                ))
            case .unknownRoute:
                result[diagnosis.serverID, default: []].append(DoctorFinding(
                    code: "gateway-route-unknown",
                    severity: .error,
                    title: "Gateway route no longer exists in \(client)",
                    detail: """
                    The definition in \(client) points at a Gateway route Kytto has no record of, so the \
                    helper has nothing to start. This happens when Kytto's data folder was reset or the \
                    config was copied from another Mac.
                    """,
                    remediation: "Restore a backup from before Gateway mode was turned on, or edit the server and enter its command again."
                ))
            }
        }

        let recent = events.filter { now.timeIntervalSince($0.timestamp) <= failureWindow }
        let failures = recent.filter {
            $0.kind == .sessionFailed || ($0.kind == .sessionEnded && $0.unexpected == true)
        }
        for (_, group) in Dictionary(grouping: failures, by: { "\($0.serverID)\u{0}\($0.clientID)" }) {
            guard let latest = group.max(by: { $0.timestamp < $1.timestamp }) else { continue }
            // A session that started cleanly after the last failure and is not
            // itself among the failures means the server recovered.
            let failedSessions = Set(group.map(\.sessionID))
            let recovered = recent.contains {
                $0.kind == .sessionStarted && $0.serverID == latest.serverID && $0.clientID == latest.clientID
                    && $0.timestamp > latest.timestamp && !failedSessions.contains($0.sessionID)
            }
            guard !recovered else { continue }
            let route = routes.first { $0.serverID == latest.serverID && $0.clientID.rawValue == latest.clientID }

            let client = clientName(latest.clientID)
            let time = latest.timestamp.formatted(date: .omitted, time: .shortened)
            let finding: DoctorFinding
            if latest.kind == .sessionFailed {
                let secret = latest.reason == GatewayFailureReason.secretUnavailable.rawValue
                finding = DoctorFinding(
                    code: secret ? "gateway-secret-unavailable" : "gateway-launch-failed",
                    severity: .error,
                    title: "Server did not start in \(client)",
                    detail: secret
                        ? "At \(time) \(client) asked for this server and the helper could not read one of its stored secrets from the keychain."
                        : "At \(time) \(client) asked for this server and the helper could not start its command.",
                    remediation: secret
                        ? "Restore Direct mode: Kytto puts the definition back and names any value it could not recover, so you can enter it again."
                        : "Run a health check to see the error. If the command was removed, reinstall it or restore Direct mode and edit the server.",
                    action: secret ? (route == nil ? nil : .restoreDirectMode) : .runHealthCheck,
                    routeID: route?.id
                )
            } else {
                let count = group.filter { $0.kind == .sessionEnded }.count
                finding = DoctorFinding(
                    code: "gateway-server-stopped",
                    severity: .warning,
                    title: "Server stopped on its own in \(client)",
                    detail: """
                    The server exited \(count == 1 ? "once" : "\(count) times") in the last 24 hours while \
                    \(client) was still connected — most recently at \(time)\
                    \(latest.exitCode.map { ", with exit code \($0)" } ?? ""). \(client) shows this only as a \
                    failed server.
                    """,
                    remediation: "Run a health check to see whether it starts now and what it prints. Restart \(client) to reconnect.",
                    action: .runHealthCheck
                )
            }
            result[latest.serverID, default: []].append(finding)
        }
        return result
    }

    private static func clientName(_ id: String) -> String {
        ClientRegistry.all.first { $0.id.rawValue == id }?.displayName ?? id
    }
}
