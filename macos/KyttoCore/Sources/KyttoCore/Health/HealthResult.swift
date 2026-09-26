import Foundation

extension Date {
    /// This instant, rounded to whole milliseconds.
    ///
    /// Timestamps here are written to disk as ISO-8601, which carries at most
    /// millisecond precision. Rounding on the way in rather than losing digits
    /// on the way out means a value read back is the value that was stored —
    /// worth having when these get compared.
    public static func timestamp() -> Date {
        Date(timeIntervalSince1970: (Date().timeIntervalSince1970 * 1000).rounded() / 1000)
    }
}

/// What a server's tool definitions cost in context (§7.4).
public struct TokenWeight: Codable, Equatable, Sendable {
    /// A context window to measure against. 200k is what the UI shows a
    /// percentage of; it is a yardstick, not a claim about any specific model.
    public static let referenceContextWindow = 200_000

    public let estimate: Int
    /// How the number was arrived at, so the UI can label it honestly.
    public let method: String
    public let measuredAt: Date

    /// Whether `estimate` came from a real vocabulary rather than a heuristic.
    /// The UI stops saying "estimate" when this is true, and only then.
    public var isMeasured: Bool { method != Self.estimateMethod }

    static let estimateMethod = "chars/4"

    public var percentOfContext: Double {
        Double(estimate) / Double(Self.referenceContextWindow) * 100
    }

    /// Counts `text` with the bundled cl100k_base vocabulary (§7.4).
    ///
    /// Falls back to the old character heuristic if the table did not load,
    /// and says so in `method` rather than passing an approximation off as a
    /// measurement.
    public static func measuring(_ text: String, at date: Date = .timestamp()) -> TokenWeight {
        guard let tokenizer = BPETokenizer.cl100kBase else { return estimating(text, at: date) }
        return TokenWeight(
            estimate: tokenizer.countTokens(text),
            method: BPETokenizer.cl100kMethod,
            measuredAt: date
        )
    }

    /// Characters divided by four — the pre-vocabulary heuristic.
    ///
    /// Kept only as the fallback for a build whose rank table is missing. It
    /// overstates a realistic tool definition by roughly a fifth, which is why
    /// it is no longer the default.
    public static func estimating(_ text: String, at date: Date = .timestamp()) -> TokenWeight {
        TokenWeight(
            estimate: Int((Double(text.count) / 4).rounded()),
            method: estimateMethod,
            measuredAt: date
        )
    }
}

/// What a health check actually ran into, as data rather than prose.
///
/// The sentence a user reads is composed from this at display time
/// (`userMessage`), so a wording fix reaches every result already on disk. A
/// stored `HealthResult.message` string cannot do that — it froze the words as
/// they were on the day of the check, which is how a fixed bug goes on being
/// reported by cached results.
public enum HealthFailureReason: Codable, Equatable, Sendable {
    case noCommand
    /// Remote servers are not spawned, so there is nothing to run.
    case remoteTransport(transport: String)
    case commandNotFound(command: String, searchedPath: String, searchedLoginShellPATH: Bool)
    case relativeCommand(command: String)
    case executableMissing(command: String, resolvedPath: String)
    case notExecutable(path: String)
    case spawnFailed(command: String, code: Int32)
    case inputClosed
    case timedOut(seconds: Double)
    case exited(status: Int32)
    case protocolError(detail: String)
    case serverRefused(code: Int, message: String)
    case closedBeforeAnswering
    /// Healthy and waiting on interactive consent, not broken (§7.3).
    case awaitingAuthorization
    /// Anything the mapping above does not know. The message is stored, which
    /// gives up display-time rendering for exactly this case and no other.
    case other(message: String)

    public var userMessage: String {
        switch self {
        case .noCommand:
            return "This server has no command to run."
        case .remoteTransport(let transport):
            return "Kytto cannot check remote servers yet — this one is reached over \(transport)."
        case .commandNotFound(let command, let searchedPath, let searchedLoginShellPATH):
            // The single most likely failure, and the one where a bare
            // "command not found" would send the user looking in the wrong place.
            return """
            \(command) was not found. Kytto searched the PATH from your login shell\
            \(searchedLoginShellPATH ? "" : " — which it could not read, so it used a default list") : \(searchedPath)
            """
        case .relativeCommand(let command):
            return """
            \(command) is a relative path, and the entry does not say which directory \
            to resolve it against. The client that owns this entry starts it from a \
            directory of its own choosing, which Kytto cannot know — so this server \
            may well run fine there. Give the entry an absolute command to have Kytto \
            check it.
            """
        case .executableMissing(let command, let resolvedPath):
            return "\(command) was not found at \(resolvedPath)."
        case .notExecutable(let path):
            return "\(path) exists but is not executable. Add the executable bit with chmod +x."
        case .spawnFailed(let command, let code):
            return "Could not start \(command) (error \(code))."
        case .inputClosed:
            return "The server closed its input before Kytto could finish talking to it."
        case .timedOut(let seconds):
            let rounded = max(1, Int(seconds.rounded(.up)))
            return "The server did not answer within \(rounded) \(rounded == 1 ? "second" : "seconds")."
        case .exited(let status):
            return "The server exited with status \(status) before answering."
        case .protocolError(let detail):
            return "The server did not speak MCP as expected: \(detail)"
        case .serverRefused(let code, let message):
            return "The server refused the request (\(code)): \(message)"
        case .closedBeforeAnswering:
            return "The server exited before answering."
        case .awaitingAuthorization:
            return """
            The server is waiting for you to authorize it in the browser, not \
            broken. Complete the sign-in it asked for, then check it again.
            """
        case .other(let message):
            return message
        }
    }
}

/// The outcome of asking a server whether it works (§7.3).
public struct HealthResult: Codable, Equatable, Sendable {
    public enum Status: String, Codable, Sendable {
        case passed
        case failed
        /// Not something Kytto can check yet — a remote server, for now.
        case unsupported
        /// The server is healthy and blocked on interactive consent: it asked
        /// for a browser sign-in instead of answering the handshake. Not a
        /// failure — an OAuth-backed remote shown red would put a false alarm
        /// on every matrix that has one, and the matrix's whole value is that
        /// it reflects reality.
        case needsAuthorization
    }

    public let status: Status
    public let checkedAt: Date
    public let toolCount: Int?
    public let tools: [ToolSummary]
    /// A one-line explanation, in the user's terms.
    ///
    /// A snapshot of `failure`'s wording at the time of the check. Readers
    /// should prefer rendering `failure` so improved wording reaches results
    /// already on disk; this survives for records written by older builds.
    public let message: String?
    /// What actually went wrong, structured, so the sentence shown for it is
    /// composed at display time rather than frozen into the record. The `in 0
    /// seconds` bug outlived its fix by exactly this: the corrected wording
    /// never reached results already written.
    public let failure: HealthFailureReason?
    /// Where the server asked the user to sign in, when `status` is
    /// `needsAuthorization` and its output named a page. Server-provided text —
    /// only ever opened natively, after validation, on the user's click.
    public let authorizationURL: String?
    /// The server's own output, verbatim and untouched.
    ///
    /// §6 calls this the most useful thing you can show someone whose server is
    /// broken. It is not summarised, cleaned up or interpreted.
    public let stderr: String?
    public let durationSeconds: Double
    public let serverName: String?
    public let serverVersion: String?
    /// Inspector data learned during the handshake. Optional fields keep health
    /// records written by older Kytto builds readable.
    public let protocolVersion: String?
    public let capabilityNames: [String]?
    public let promptCount: Int?
    public let resourceCount: Int?
    public let inspectionNotes: [String]?
    /// Display-only resolution of the configured command.
    public let resolvedCommand: String?
    public let environmentSource: String?

    public init(
        status: Status,
        checkedAt: Date,
        toolCount: Int?,
        tools: [ToolSummary],
        message: String?,
        stderr: String?,
        durationSeconds: Double,
        serverName: String?,
        serverVersion: String?,
        protocolVersion: String? = nil,
        capabilityNames: [String]? = nil,
        promptCount: Int? = nil,
        resourceCount: Int? = nil,
        inspectionNotes: [String]? = nil,
        resolvedCommand: String? = nil,
        environmentSource: String? = nil,
        failure: HealthFailureReason? = nil,
        authorizationURL: String? = nil
    ) {
        self.status = status
        self.checkedAt = checkedAt
        self.toolCount = toolCount
        self.tools = tools
        self.message = message
        self.failure = failure
        self.authorizationURL = authorizationURL
        self.stderr = stderr
        self.durationSeconds = durationSeconds
        self.serverName = serverName
        self.serverVersion = serverVersion
        self.protocolVersion = protocolVersion
        self.capabilityNames = capabilityNames
        self.promptCount = promptCount
        self.resourceCount = resourceCount
        self.inspectionNotes = inspectionNotes
        self.resolvedCommand = resolvedCommand
        self.environmentSource = environmentSource
    }

    public var isPassing: Bool { status == .passed }

    /// The explanation to show, rendered from the structured failure when the
    /// record carries one so current wording reaches old results, and falling
    /// back to the stored snapshot for records written before `failure` existed.
    public var currentMessage: String? {
        failure?.userMessage ?? message
    }
}

/// Everything Kytto has learned about a server that is not in a config file.
///
/// Config files stay authoritative for what is *configured* (§5); this is the
/// measured half, and it lives in Kytto's own store.
public struct ServerMetadata: Codable, Equatable, Sendable {
    public var health: HealthResult?
    public var tokenWeight: TokenWeight?
    public var contractTools: [ToolSummary]?
    public var contractChanges: [ContractChange]?
    public var contractChangedAt: Date?

    public init(
        health: HealthResult? = nil,
        tokenWeight: TokenWeight? = nil,
        contractTools: [ToolSummary]? = nil,
        contractChanges: [ContractChange]? = nil,
        contractChangedAt: Date? = nil
    ) {
        self.health = health
        self.tokenWeight = tokenWeight
        self.contractTools = contractTools
        self.contractChanges = contractChanges
        self.contractChangedAt = contractChangedAt
    }
}
