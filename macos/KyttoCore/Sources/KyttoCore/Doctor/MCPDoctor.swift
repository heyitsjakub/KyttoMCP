import Foundation

public enum DoctorSeverity: String, Codable, Sendable {
    case info
    case warning
    case error
}

public enum DoctorAction: String, Codable, Sendable {
    case runHealthCheck
    case pinResolvedCommand
    /// Pin a runner-launched package to an exact release the user confirmed.
    case pinPackageVersion
    /// Ask osv.dev and the package registry about the package (§7.10).
    case checkPackageSafety
    /// Point a Gateway definition at this copy of the helper (app was moved).
    case repairGatewayRoute
    /// Put the server's own definition back in place of the Gateway one.
    case restoreDirectMode
}

public struct DoctorFinding: Codable, Equatable, Sendable {
    public let code: String
    public let severity: DoctorSeverity
    public let title: String
    public let detail: String
    public let remediation: String
    public let action: DoctorAction?
    /// The Gateway route a route-level action applies to. A server can be in
    /// Gateway mode in several clients, so the server id alone does not say.
    public let routeID: String?

    public init(
        code: String,
        severity: DoctorSeverity,
        title: String,
        detail: String,
        remediation: String,
        action: DoctorAction? = nil,
        routeID: String? = nil
    ) {
        self.routeID = routeID
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
/// The automatic actions offered here pin an executable path that Kytto has
/// already resolved on this machine, or a package version the user looked up
/// and confirmed; the app still previews and writes either via the ordinary
/// backup/atomic-write transaction.
public enum MCPDoctor {

    /// Every server's findings, including the ones no single server can see.
    ///
    /// Tool-name collisions are a property of a *client*, not of a server: two
    /// servers are only in each other's way because one client has both of them
    /// switched on. So they are worked out here, across the whole matrix, and
    /// then attached to each server involved.
    /// - Parameter packageSafety: package checks the user asked for, by server id.
    public static func reports(
        for servers: [Server],
        clients: [ClientID],
        packageSafety: [String: PackageSafetyReport] = [:],
        gateway: [String: [DoctorFinding]] = [:]
    ) -> [DoctorServerReport] {
        let collisions = collisionFindings(servers: servers, clients: clients)
        return servers
            .map { server in
                let report = analyze(server)
                let extra = PackageSafety.findings(for: server, report: packageSafety[server.id])
                    + (collisions[server.id] ?? [])
                    + (gateway[server.id] ?? [])
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
                $0.enabledIn[clientID] == .enabled && !$0.knownTools.isEmpty
            }
            guard active.count > 1 else { continue }

            let shadowed = ToolSafetyScanner.shadowedNames(
                across: active.map { (name: $0.name, tools: $0.knownTools) }
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
            let detectedSSE = server.health?.failure == .remoteTransport(transport: "sse")
            if server.transport == .sse || detectedSSE {
                findings.append(DoctorFinding(
                    code: "deprecated-http-sse", severity: .warning,
                    title: "This server uses the deprecated HTTP+SSE transport",
                    detail: """
                    HTTP+SSE has been superseded since MCP 2025-03-26 and is formally deprecated                     in 2026-07-28, eligible for removal after at least twelve months' notice.                     Kytto does not inspect it.
                    """,
                    remediation: "Move to the server's Streamable HTTP endpoint if it has one; until then, verify it in its client."
                ))
            } else if let health = server.health {
                if health.status == .needsAuthorization {
                    findings.append(DoctorFinding(
                        code: "needs-authorization", severity: .info,
                        title: "Remote authorization is required",
                        detail: "The remote endpoint requires a sign-in or additional permissions.",
                        remediation: "Sign in from the server detail or review its configured credentials, then check again.",
                        action: .runHealthCheck
                    ))
                } else if health.status == .failed {
                    findings.append(DoctorFinding(
                        code: "remote-check-failed", severity: .error,
                        title: "Remote MCP inspection did not complete",
                        detail: health.currentMessage ?? "The endpoint did not complete the MCP handshake and tools listing.",
                        remediation: "Review the endpoint, HTTP status, TLS connection and configured credentials in server detail.",
                        action: .runHealthCheck
                    ))
                }
            } else {
                findings.append(DoctorFinding(
                    code: "not-checked", severity: .info,
                    title: "This remote server has not been checked",
                    detail: "Its MCP handshake and tool contract have not been verified by Kytto.",
                    remediation: "Run Remote Doctor to inspect the endpoint on request.",
                    action: .runHealthCheck
                ))
            }
            findings.append(contentsOf: deprecatedFeatureFindings(server.health))
            findings.append(contentsOf: ToolSafetyScanner.scan(server.knownTools).map(finding(for:)))
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

        if let launch = PackagePin.unpinnedLaunch(in: server) {
            findings.append(unpinnedPackage(launch, canPin: PackagePin.canPin(server)))
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
        // The last passing contract, not this check's: a failed check lists no
        // tools, and failing must not be a way to make a finding go away.
        findings.append(contentsOf: ToolSafetyScanner.scan(server.knownTools).map(finding(for:)))
        findings.append(contentsOf: deprecatedFeatureFindings(health))

        if health.status == .passed,
           let configured = server.command?.trimmingCharacters(in: .whitespacesAndNewlines),
           let resolved = health.resolvedCommand,
           resolvedCommand(resolved, matches: configured) {
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

    /// Whether a check's resolved path is the configured command, found on PATH.
    ///
    /// Health is stored per server, not per definition, so after the command
    /// is edited the last result describes a different program. Offering to pin
    /// that program's path would replace `docker` with an old `npx`; the names
    /// must agree before the resolution counts.
    public static func resolvedCommand(_ resolved: String, matches configured: String) -> Bool {
        guard !configured.isEmpty, configured != resolved, !configured.hasPrefix("/"),
              !configured.contains("/") else { return false }
        return URL(filePath: resolved).lastPathComponent == configured
    }

    /// Features the 2026-07-28 revision deprecates, seen during the last check.
    /// Information only: nothing is broken yet, but clients will drop them.
    private static func deprecatedFeatureFindings(_ health: HealthResult?) -> [DoctorFinding] {
        (health?.deprecatedFeatures ?? []).map { feature in
            let (name, replacement): (String, String) = switch feature {
            case .logging: ("MCP logging", "Servers are expected to log to stderr or through OpenTelemetry instead.")
            case .roots: ("Roots", "The server asked Kytto for roots during the check; future clients may not answer.")
            case .sampling: ("Sampling", "The server asked for sampling during the check; future clients may not answer.")
            }
            return DoctorFinding(
                code: "deprecated-\(feature.rawValue)",
                severity: .info,
                title: "Uses \(name), deprecated in MCP 2026-07-28",
                detail: "\(replacement) Deprecated features stay available for at least twelve months.",
                remediation: "Nothing to change now. Prefer an updated release of the server when one drops this dependency."
            )
        }
    }

    /// A package fetched by name with no exact version (§7.10).
    ///
    /// A warning rather than information: every client start can run a release
    /// nobody chose, with the server's environment — tokens included — which is
    /// the supply-chain path a compromised package takes. Not an error, because
    /// nothing is broken today and the server may be exactly what it claims.
    ///
    /// Read from the configuration alone, so it needs no health check and makes
    /// no request. The version to pin to is not in here: it comes from a lookup
    /// the user asks for, never from the server's self-reported `serverInfo`,
    /// which names whatever the author typed and is often not the package's
    /// release number at all.
    private static func unpinnedPackage(_ launch: PackageLaunch, canPin: Bool) -> DoctorFinding {
        let registry = launch.ecosystem == .npm ? "npm" : "PyPI"
        let request: String = switch launch.requestedVersion {
        case nil: "names no version"
        case let tag? where tag.first?.isLetter == true: "asks for the “\(tag)” tag"
        case let requested?: "asks for “\(requested)”, which matches more than one release"
        }
        let example = !launch.canPinInPlace
            ? "--spec \(launch.name)==VERSION"
            : launch.spelling == .requirement ? "\(launch.name)==VERSION" : "\(launch.name)@VERSION"
        return DoctorFinding(
            code: "unpinned-package",
            severity: .warning,
            title: "The package version is not pinned",
            detail: """
            “\(launch.argument)” \(request), so \(launch.runner) can start a newer \(registry) \
            release of \(launch.name) the next time a client launches it — without anyone \
            reviewing it.
            """,
            remediation: canPin
                ? """
                Check the latest release (Kytto asks \(registry) about this package name and \
                nothing else), then preview pinning that exact version in every editable \
                client definition. Pinning fixes this package's own version; its dependencies \
                still resolve within their declared ranges.
                """
                : """
                Pin it by hand to an exact release, for example “\(example)”. Kytto will not \
                add arguments this command does not already have.
                """,
            action: canPin ? .pinPackageVersion : nil
        )
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
        // What the check actually ran into decides first. Matching words over
        // the message and stderr comes after, and only for failures where the
        // server's own output is the evidence — otherwise "Method not found"
        // reads as a missing executable, and a timeout whose stderr mentions a
        // missing optional file reads as one too.
        switch health.failure {
        case .commandNotFound?, .executableMissing?, .relativeCommand?:
            return failure(
                code: "executable-not-found",
                title: "The executable or one of its files was not found",
                remediation: "Check the command, use an absolute path, and verify that the client can see the same Node, Python or uv installation as your shell."
            )
        case .notExecutable?:
            return failure(
                code: "permission-denied",
                title: "The operating system denied access",
                remediation: "Check executable and file permissions, then review the client’s macOS privacy permissions before trying again."
            )
        case .timedOut?:
            return failure(
                code: "timeout",
                title: "The server did not complete the MCP handshake in time",
                remediation: "Run the command directly to check for an install prompt, slow package download, blocked network request or server output written to stdout."
            )
        case .protocolError?:
            return failure(
                code: "protocol-output",
                title: "The server returned invalid MCP/JSON output",
                remediation: "Run it directly and ensure diagnostic messages go to stderr while stdout contains only MCP JSON-RPC messages."
            )
        case .serverRefused?:
            return failure(
                code: "server-refused",
                title: "The server refused Kytto's MCP request",
                remediation: "Open the server detail for the error it returned. A server that only speaks a newer MCP revision than Kytto, or needs configuration it lacks, refuses this way."
            )
        default:
            break
        }
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
