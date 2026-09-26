import Foundation

public enum DoctorSeverity: String, Codable, Sendable {
    case info
    case warning
    case error
}

public enum DoctorAction: String, Codable, Sendable {
    case runHealthCheck
    case pinResolvedCommand
}

public struct DoctorFinding: Codable, Equatable, Sendable {
    public let code: String
    public let severity: DoctorSeverity
    public let title: String
    public let detail: String
    public let remediation: String
    public let action: DoctorAction?

    public init(
        code: String,
        severity: DoctorSeverity,
        title: String,
        detail: String,
        remediation: String,
        action: DoctorAction? = nil
    ) {
        self.code = code
        self.severity = severity
        self.title = title
        self.detail = detail
        self.remediation = remediation
        self.action = action
    }
}

public struct DoctorServerReport: Codable, Equatable, Sendable {
    public let serverID: String
    public let serverName: String
    public let findings: [DoctorFinding]

    public init(serverID: String, serverName: String, findings: [DoctorFinding]) {
        self.serverID = serverID
        self.serverName = serverName
        self.findings = findings
    }
}

/// Turns health data into concrete, user-facing next steps.
///
/// It deliberately does not guess edits from stderr. A guessed package name or
/// environment value is exactly how a "repair" tool damages a working config.
/// The one automatic action offered here pins an executable path that Kytto has
/// already resolved on this machine; the app still previews and writes it via
/// the ordinary backup/atomic-write transaction.
public enum MCPDoctor {

    /// Every server's findings, including the ones no single server can see.
    ///
    /// Tool-name collisions are a property of a *client*, not of a server: two
    /// servers are only in each other's way because one client has both of them
    /// switched on. So they are worked out here, across the whole matrix, and
    /// then attached to each server involved.
    public static func reports(for servers: [Server], clients: [ClientID]) -> [DoctorServerReport] {
        let collisions = collisionFindings(servers: servers, clients: clients)
        return servers
            .map { server in
                let report = analyze(server)
                let extra = collisions[server.id] ?? []
                guard !extra.isEmpty else { return report }
                return DoctorServerReport(
                    serverID: report.serverID,
                    serverName: report.serverName,
                    findings: report.findings + extra
                )
            }
            .filter { !$0.findings.isEmpty }
    }

    /// One finding per server per client where a tool name is claimed twice.
    private static func collisionFindings(
        servers: [Server],
        clients: [ClientID]
    ) -> [String: [DoctorFinding]] {
        var result: [String: [DoctorFinding]] = [:]

        for clientID in clients {
            let active = servers.filter {
                $0.enabledIn[clientID] == .enabled && !($0.health?.tools.isEmpty ?? true)
            }
            guard active.count > 1 else { continue }

            let shadowed = ToolSafetyScanner.shadowedNames(
                across: active.map { (name: $0.name, tools: $0.health?.tools ?? []) }
            )
            guard !shadowed.isEmpty else { continue }

            for server in active {
                let mine = shadowed
                    .filter { $0.value.contains(server.name) }
                    .keys
                    .sorted()
                guard !mine.isEmpty else { continue }

                let others = Set(
                    mine.flatMap { shadowed[$0] ?? [] }.filter { $0 != server.name }
                ).sorted()

                result[server.id, default: []].append(DoctorFinding(
                    code: "tool-name-collision",
                    severity: .warning,
                    title: "Another server offers the same tool name",
                    detail: """
                    Switched on together in this client with \
                    \(others.joined(separator: ", ")), which also \
                    \(others.count == 1 ? "offers" : "offer") \
                    \(mine.joined(separator: ", ")).
                    """,
                    remediation: """
                    The client shows the model one flat list of tools, so a name \
                    claimed twice is resolved by the client rather than by you. \
                    Switch one of them off in this client, or keep them in \
                    separate profiles.
                    """
                ))
            }
        }
        return result
    }

    public static func analyze(_ server: Server) -> DoctorServerReport {
        var findings: [DoctorFinding] = []

        if server.transport != .stdio {
            findings.append(DoctorFinding(
                code: "remote-check-unavailable",
                severity: .info,
                title: "Remote health checks are not available yet",
                detail: "Kytto can preserve this remote definition, but the current checker only starts local stdio servers.",
                remediation: "Verify the endpoint in its client. Remote HTTP and OAuth diagnostics remain on the roadmap."
            ))
            return DoctorServerReport(serverID: server.id, serverName: server.name, findings: findings)
        }

        let missingEnvironment = server.env.filter { !$0.hasValue }.map(\.key).sorted()
        if !missingEnvironment.isEmpty {
            findings.append(DoctorFinding(
                code: "missing-environment",
                severity: .error,
                title: "Required environment values are empty",
                detail: "Empty keys: \(missingEnvironment.joined(separator: ", ")).",
                remediation: "Edit the server and provide these values. Kytto will keep them out of web UI state and logs."
            ))
        }

        if server.hasRelativePath {
            findings.append(DoctorFinding(
                code: "relative-path",
                severity: .warning,
                title: "A relative path may work in only one client",
                detail: "Relative commands and arguments are resolved from the directory that launched the client.",
                remediation: "Use an absolute path, or run a health check from the same definition before copying it to more clients.",
                action: .runHealthCheck
            ))
        }

        guard let health = server.health else {
            findings.append(DoctorFinding(
                code: "not-checked",
                severity: .info,
                title: "This server has not been checked",
                detail: "Its executable, MCP handshake and tool contract have not been verified by Kytto.",
                remediation: "Run a health check to turn this into a measured result.",
                action: .runHealthCheck
            ))
            return DoctorServerReport(serverID: server.id, serverName: server.name, findings: findings)
        }

        if health.status == .failed {
            findings.append(classifyFailure(health))
        }

        if health.status == .needsAuthorization {
            findings.append(DoctorFinding(
                code: "needs-authorization",
                severity: .info,
                title: "Waiting for you to authorize it",
                detail: "The last check reached the server, which asked for an interactive sign-in instead of answering the handshake.",
                remediation: "Open the authorization page from the server detail, complete the sign-in, then check again.",
                action: .runHealthCheck
            ))
        }

        // What the model was handed, as opposed to what the process did. A
        // server can start cleanly, answer `tools/list` correctly and still have
        // shipped an instruction in a description — every other check in this
        // file would call that healthy (§7.10).
        findings.append(contentsOf: ToolSafetyScanner.scan(health.tools).map(finding(for:)))

        if health.status == .passed,
           let configured = server.command?.trimmingCharacters(in: .whitespacesAndNewlines),
           let resolved = health.resolvedCommand,
           !configured.isEmpty,
           configured != resolved,
           !configured.hasPrefix("/") {
            findings.append(DoctorFinding(
                code: "unpinned-command",
                severity: .info,
                title: "The executable depends on the client PATH",
                detail: "“\(configured)” resolved to “\(resolved)” during the successful check.",
                remediation: "Preview a change that pins the verified absolute path in every editable client definition.",
                action: .pinResolvedCommand
            ))
        }

        return DoctorServerReport(serverID: server.id, serverName: server.name, findings: findings)
    }

    /// A tool-contract risk, as a Doctor finding.
    ///
    /// There is no automatic action. Kytto does not edit what a server says
    /// about itself and cannot — the text is the server's, not the config's —
    /// and switching a server off over a pattern match is a decision that
    /// belongs to the person reading the evidence (§7.10).
    private static func finding(for risk: ToolRisk) -> DoctorFinding {
        DoctorFinding(
            code: risk.kind.rawValue,
            severity: risk.severity,
            title: "“\(risk.toolName)”: \(title(for: risk.kind))",
            detail: "\(risk.explanation) Found: \(risk.evidence).",
            remediation: """
            Read this tool's full description and input schema in the server \
            detail before using the server, and check where the server came \
            from. Kytto does not change what a server says about itself.
            """
        )
    }

    private static func title(for kind: ToolRiskKind) -> String {
        switch kind {
        case .hiddenCharacters: "the description contains invisible characters"
        case .instructionOverride: "the description instructs the model"
        case .impersonatedAuthority: "the description imitates a system message"
        case .credentialInterest: "a read-only tool names credential material"
        case .shadowedName: "the tool name is claimed by another server"
        }
    }

    private static func classifyFailure(_ health: HealthResult) -> DoctorFinding {
        let evidence = [health.currentMessage, health.stderr].compactMap { $0 }.joined(separator: "\n")
        let lower = evidence.lowercased()

        if lower.contains("enoent") || lower.contains("not found") || lower.contains("no such file") {
            return failure(
                code: "executable-not-found",
                title: "The executable or one of its files was not found",
                remediation: "Check the command, use an absolute path, and verify that the client can see the same Node, Python or uv installation as your shell."
            )
        }
        if lower.contains("permission denied") || lower.contains("operation not permitted") {
            return failure(
                code: "permission-denied",
                title: "The operating system denied access",
                remediation: "Check executable and file permissions, then review the client’s macOS privacy permissions before trying again."
            )
        }
        if lower.contains("timed out") || lower.contains("timeout") {
            return failure(
                code: "timeout",
                title: "The server did not complete the MCP handshake in time",
                remediation: "Run the command directly to check for an install prompt, slow package download, blocked network request or server output written to stdout."
            )
        }
        if lower.contains("json") || lower.contains("parse") || lower.contains("unexpected token") {
            return failure(
                code: "protocol-output",
                title: "The server returned invalid MCP/JSON output",
                remediation: "Run it directly and ensure diagnostic messages go to stderr while stdout contains only MCP JSON-RPC messages."
            )
        }
        if lower.contains("environment") || lower.contains("api key") || lower.contains("token") {
            return failure(
                code: "environment-failure",
                title: "The server appears to be missing configuration or credentials",
                remediation: "Review its environment keys and credential scopes. Do not paste secret values into logs or support reports."
            )
        }
        return failure(
            code: "health-failed",
            title: "The server did not pass its health check",
            remediation: "Open the server detail for its stderr output, then run the displayed command directly to isolate the startup failure."
        )
    }

    private static func failure(code: String, title: String, remediation: String) -> DoctorFinding {
        DoctorFinding(
            code: code,
            severity: .error,
            title: title,
            detail: "Kytto could not complete the MCP handshake and tools/list check.",
            remediation: remediation,
            action: .runHealthCheck
        )
    }
}
