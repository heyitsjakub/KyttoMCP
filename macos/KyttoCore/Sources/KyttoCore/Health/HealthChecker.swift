import Foundation

/// Runs a server and asks it what it can do (§7.3).
///
/// Never on launch and never in the background: §6 is firm that spawning
/// arbitrary commands is something the user asks for, one server at a time.
public struct HealthChecker: Sendable {
    public static let defaultTimeout: TimeInterval = 10

    private let client: MCPStdioClient
    private let shell: ShellEnvironment
    private let timeout: TimeInterval

    public init(
        shell: ShellEnvironment = .current,
        timeout: TimeInterval = HealthChecker.defaultTimeout,
        client: MCPStdioClient = MCPStdioClient()
    ) {
        self.shell = shell
        self.timeout = timeout
        self.client = client
    }

    public func check(_ server: Server) -> (health: HealthResult, tokenWeight: TokenWeight?) {
        let started = Date.timestamp()

        guard server.transport == .stdio else {
            // Remote servers are not spawned, so there is nothing to run. Saying
            // "not checked yet" would be a lie; saying why is not.
            return (
                result(
                    status: .unsupported,
                    reason: .remoteTransport(transport: server.transport.rawValue),
                    stderr: nil,
                    started: started
                ),
                nil
            )
        }

        // Placeholders the owning client would expand — `${__dirname}` and
        // friends in a Claude Desktop extension — are resolved here. Running the
        // literal text would fail every extension for a reason that has nothing
        // to do with whether the server works.
        let (resolvedCommand, resolvedArgs) = server.runnable
        guard let command = resolvedCommand, !command.isEmpty else {
            return (result(status: .failed, reason: .noCommand, stderr: nil, started: started), nil)
        }

        let environment = shell.environment(
            adding: Dictionary(
                server.env.compactMap { entry in entry.value.map { (entry.key, $0) } },
                uniquingKeysWith: { first, _ in first }
            )
        )

        do {
            let (listing, stderr) = try client.listTools(
                command: command,
                arguments: resolvedArgs,
                environment: environment,
                timeout: timeout
            )
            let weight = TokenWeight.measuring(listing.rawToolsJSON)
            return (
                HealthResult(
                    status: .passed,
                    checkedAt: started,
                    toolCount: listing.tools.count,
                    tools: listing.tools,
                    message: nil,
                    // Servers commonly log to stderr while working perfectly.
                    // Keeping it on success too means the detail view can show
                    // warnings that are not failures.
                    stderr: stderr.isEmpty ? nil : stderr,
                    durationSeconds: Date().timeIntervalSince(started),
                    serverName: listing.serverName,
                    serverVersion: listing.serverVersion,
                    protocolVersion: listing.negotiatedProtocolVersion,
                    capabilityNames: listing.capabilityNames,
                    promptCount: listing.promptCount,
                    resourceCount: listing.resourceCount,
                    inspectionNotes: listing.inspectionNotes,
                    resolvedCommand: listing.resolvedExecutable,
                    environmentSource: environmentSource
                ),
                weight
            )
        } catch let failure as HealthFailure {
            return (
                outcome(
                    for: failure.underlying,
                    stderr: failure.stderr.isEmpty ? nil : failure.stderr,
                    started: started
                ),
                nil
            )
        } catch {
            return (outcome(for: error, stderr: nil, started: started), nil)
        }
    }

    /// Separates "this server is broken" from "Kytto cannot speak for this server".
    ///
    /// A command Kytto cannot resolve is the second, not the first: a relative path
    /// is resolved by whichever client launches it, so the binary it names may well
    /// be there and the server may run perfectly. Marking that red is the worst kind
    /// of wrong answer, because it sends someone to fix a thing that is not broken —
    /// the same reason the remote-server branch above does not report a failure.
    ///
    /// A server blocked on interactive consent is a third thing again. It never
    /// answers the handshake — it is waiting for a browser sign-in — so it
    /// arrives here as a timeout, and calling that a failure would show every
    /// OAuth-backed remote as red. Its own output says what it is waiting for,
    /// which is where the state is read from rather than from the exit code.
    private func outcome(for error: any Error, stderr: String?, started: Date) -> HealthResult {
        if case ProcessError.relativeCommand(let command) = error {
            return result(
                status: .unsupported,
                reason: .relativeCommand(command: command),
                stderr: stderr,
                started: started
            )
        }
        if let stderr, AuthorizationSignal.isAwaiting(stderr) {
            return result(
                status: .needsAuthorization,
                reason: .awaitingAuthorization,
                stderr: stderr,
                started: started,
                authorizationURL: AuthorizationSignal.url(in: stderr)
            )
        }
        return result(status: .failed, reason: reason(for: error), stderr: stderr, started: started)
    }

    private func reason(for error: any Error) -> HealthFailureReason {
        switch error {
        case ProcessError.commandNotFound(let command, let path):
            return .commandNotFound(
                command: command,
                searchedPath: path,
                searchedLoginShellPATH: shell.resolvedFromLoginShell
            )
        case ProcessError.relativeCommand(let command):
            return .relativeCommand(command: command)
        case ProcessError.executableMissing(let command, let resolvedPath):
            return .executableMissing(command: command, resolvedPath: resolvedPath)
        case ProcessError.notExecutable(let path):
            return .notExecutable(path: path)
        case ProcessError.spawnFailed(let command, let code):
            return .spawnFailed(command: command, code: code)
        case ProcessError.writeFailed:
            return .inputClosed
        case ProcessError.timedOut(let seconds):
            return .timedOut(seconds: seconds)
        case ProcessError.ended(let status):
            return .exited(status: status)
        case MCPError.protocolError(let detail):
            return .protocolError(detail: detail)
        case MCPError.serverError(let code, let message):
            return .serverRefused(code: code, message: message)
        case MCPError.closedBeforeAnswering:
            return .closedBeforeAnswering
        default:
            return .other(
                message: (error as? LocalizedError)?.errorDescription ?? String(describing: error)
            )
        }
    }

    private func result(
        status: HealthResult.Status,
        reason: HealthFailureReason,
        stderr: String?,
        started: Date,
        authorizationURL: String? = nil
    ) -> HealthResult {
        HealthResult(
            status: status,
            checkedAt: started,
            toolCount: nil,
            tools: [],
            // A snapshot for older readers of the store; displays render the
            // structured reason instead, so wording fixes reach old results.
            message: reason.userMessage,
            stderr: stderr,
            durationSeconds: Date().timeIntervalSince(started),
            serverName: nil,
            serverVersion: nil,
            environmentSource: environmentSource,
            failure: reason,
            authorizationURL: authorizationURL
        )
    }

    private var environmentSource: String {
        shell.resolvedFromLoginShell ? "Login shell PATH" : "Fallback PATH"
    }
}

/// Reads "healthy, but waiting for interactive consent" out of a server's own
/// output.
///
/// `mcp-remote` and friends print an instruction and an URL, then block until
/// the browser round trip completes — which a health check can only see as a
/// timeout. The markers are deliberately narrow: `unauthorized`, an expired
/// token or a missing API key are real failures whose fix is not a browser, so
/// none of them may match.
enum AuthorizationSignal {
    private static let markers = [
        "please authorize",
        "waiting for authorization",
        "authorization required",
        "authorization needed",
        "authentication required",
        "authorization_pending",
        "browser opened automatically",
    ]

    /// Markers only, on purpose. An oauth-looking URL alone is not evidence of
    /// waiting — a crash whose stack trace links to OAuth documentation is
    /// still a crash, and a false "waiting for sign-in" would be the same class
    /// of bug this state exists to remove.
    static func isAwaiting(_ stderr: String) -> Bool {
        let lowered = stderr.lowercased()
        return markers.contains(where: lowered.contains)
    }

    /// The page the server asked the user to visit, if its output named one:
    /// an https URL that is itself about authorization, or one printed on a
    /// line that is. A documentation link inside an unrelated stack trace
    /// matches neither.
    static func url(in stderr: String) -> String? {
        let candidates = stderr.components(separatedBy: .newlines).flatMap { line -> [String] in
            let lineCarriesMarker = markers.contains(where: line.lowercased().contains)
            return urls(in: line).filter { candidate in
                let lowered = candidate.lowercased()
                return lowered.contains("authoriz") || lowered.contains("oauth") || lineCarriesMarker
            }
        }
        return candidates.first { candidate in
            guard let url = URL(string: candidate), url.scheme == "https", url.host() != nil else {
                return false
            }
            return true
        }
    }

    private static func urls(in line: String) -> [String] {
        var found: [String] = []
        var search = line[...]
        while let start = search.range(of: "https://") {
            let tail = search[start.lowerBound...]
            let end = tail.firstIndex {
                $0.isWhitespace || "\"'<>`)]}".contains($0)
            } ?? tail.endIndex
            var candidate = String(tail[..<end])
            while let last = candidate.last, ".,;:!".contains(last) {
                candidate.removeLast()
            }
            if candidate.count > "https://".count { found.append(candidate) }
            search = search[end...]
        }
        return found
    }
}
