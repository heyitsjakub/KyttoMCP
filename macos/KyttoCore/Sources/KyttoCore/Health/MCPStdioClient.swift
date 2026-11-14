import Foundation

public struct ToolAnnotations: Codable, Equatable, Sendable {
    public let readOnlyHint: Bool?
    public let destructiveHint: Bool?
    public let idempotentHint: Bool?
    public let openWorldHint: Bool?

    public init(
        readOnlyHint: Bool? = nil,
        destructiveHint: Bool? = nil,
        idempotentHint: Bool? = nil,
        openWorldHint: Bool? = nil
    ) {
        self.readOnlyHint = readOnlyHint
        self.destructiveHint = destructiveHint
        self.idempotentHint = idempotentHint
        self.openWorldHint = openWorldHint
    }
}

public struct ToolSummary: Codable, Equatable, Sendable {
    public let name: String
    public let description: String?
    public let inputSchemaJSON: String?
    public let annotations: ToolAnnotations?
    /// What this one tool's entry costs, measured on the exact bytes the server
    /// sent (§7.4).
    ///
    /// Optional because health records written before per-tool measurement
    /// existed have to stay readable, and because a build without the rank
    /// table has no business inventing one.
    ///
    /// Deliberately *not* compared by `ContractGuard`, which asks what the
    /// model was told — a tool whose cost moved by two tokens has not changed
    /// its contract.
    public let tokenCount: Int?

    public init(
        name: String,
        description: String?,
        inputSchemaJSON: String? = nil,
        annotations: ToolAnnotations? = nil,
        tokenCount: Int? = nil
    ) {
        self.name = name
        self.description = description
        self.inputSchemaJSON = inputSchemaJSON
        self.annotations = annotations
        self.tokenCount = tokenCount
    }
}

public struct ToolListing: Sendable {
    public let tools: [ToolSummary]
    /// The `tools` array exactly as the server sent it.
    ///
    /// Kept verbatim because token weight is measured on what a model would
    /// actually be shown, not on a re-serialization of Kytto's own model.
    public let rawToolsJSON: String
    public let serverName: String?
    public let serverVersion: String?
    public let negotiatedProtocolVersion: String?
    public let capabilityNames: [String]
    public let promptCount: Int?
    public let resourceCount: Int?
    public let inspectionNotes: [String]
    public let resolvedExecutable: String
    public var deprecatedFeatures: [DeprecatedFeature] = []
}

public enum MCPError: Error, LocalizedError {
    case protocolError(String)
    case serverError(code: Int, message: String)
    case closedBeforeAnswering

    public var errorDescription: String? {
        switch self {
        case .protocolError(let detail):
            "The server did not speak MCP as expected: \(detail)"
        case .serverError(let code, let message):
            "The server refused the request (\(code)): \(message)"
        case .closedBeforeAnswering:
            "The server exited before answering."
        }
    }
}

/// The smallest MCP client that can answer "does this server work, and how much
/// context do its tools cost?" (§7.3, §7.4).
///
/// Deliberately hand-written rather than pulled in as a dependency: the whole
/// exchange is a handful of messages, and doing it directly keeps full control
/// over the timeout and over capturing stderr, which is the part users actually
/// need when something is broken.
///
/// Speaks both eras of MCP. Revision 2026-07-28 removed the `initialize`
/// handshake: every request carries its version in `_meta`, and a client that
/// supports both eras probes with `server/discover` first. Any answer other
/// than a discovery result or a recognised modern error — including silence or
/// the server exiting — means a handshake-era server, which gets `initialize`
/// (spec: basic/transports/stdio, "Backward Compatibility").
public struct MCPStdioClient: Sendable {
    /// The per-request-metadata revision Kytto probes for.
    public static let modernProtocolVersion = "2026-07-28"
    /// What Kytto offers in an `initialize` handshake. Handshake-era revisions
    /// only: 2026-07-28 has no handshake, so offering it there is meaningless
    /// and invites a dual-era server to answer in the wrong era.
    public static let preferredProtocolVersion = "2025-11-25"
    static let handshakeProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]
    static var clientInfo: [String: Any] { ["name": "Kytto", "version": "1.0"] }

    /// Modern errors that prove the server speaks per-request metadata.
    static let unsupportedProtocolVersionCode = -32022
    static let modernErrorCodes: Set<Int> = [-32020, -32021, -32022]

    private static let discoverID = 9
    /// How long a silent server gets before it is treated as handshake-era.
    private static let probeLimit: TimeInterval = 2

    public init() {}

    public func listTools(
        command: String,
        arguments: [String],
        environment: [String: String],
        timeout: TimeInterval,
        workingDirectory: String? = nil
    ) throws -> (listing: ToolListing, stderr: String) {
        func spawn() throws -> Session {
            Session(process: try ManagedProcess(
                executable: command,
                arguments: arguments,
                environment: environment,
                workingDirectory: workingDirectory
            ))
        }
        var session = try spawn()
        // §6: every spawn is killed, and its group with it. No exception, no
        // early return that skips this. (`defer` reads `session` when it runs,
        // so a respawned process is the one it reaches.)
        defer { session.process.terminateGroup() }

        let deadline = Date().addingTimeInterval(timeout)
        let memoryKey = HandshakeMemory.key(command: command, arguments: arguments, workingDirectory: workingDirectory)

        do {
            let listing: ToolListing
            var probed = false
            var era: Era = .handshake
            if !HandshakeMemory.contains(memoryKey) {
                probed = true
                era = try probe(session, deadline: deadline, timeout: timeout)
                if case .exited = era {
                    // Some handshake-era servers exit on any request before
                    // `initialize`. The spec's answer is a fresh process.
                    session.process.terminateGroup()
                    session = try spawn()
                    era = .handshake
                }
            }
            switch era {
            case .modern(let discover):
                HandshakeMemory.remove(memoryKey)
                listing = try modernListing(discover: discover, session: session, deadline: deadline, timeout: timeout)
            case .handshake, .exited:
                do {
                    listing = try handshakeListing(session: session, deadline: deadline, timeout: timeout)
                    HandshakeMemory.insert(memoryKey)
                } catch MCPError.serverError(let code, let message) {
                    // It refused the handshake. A modern server that was slow to
                    // start answers the probe late, after the handshake went out;
                    // one remembered as handshake-era may have moved on since.
                    HandshakeMemory.remove(memoryKey)
                    let discover: JSONDocument
                    if let late = session.lateDiscovery, Self.isModernDiscovery(late) {
                        discover = late
                    } else if !probed, case .modern(let probedDiscovery) = try probe(session, deadline: deadline, timeout: timeout) {
                        discover = probedDiscovery
                    } else {
                        throw MCPError.serverError(code: code, message: message)
                    }
                    listing = try modernListing(discover: discover, session: session, deadline: deadline, timeout: timeout)
                }
            }
            session.process.terminateGroup()
            return (listing, session.process.capturedStderr)
        } catch {
            // Whatever went wrong, the server's own words explain it better than
            // ours, so they travel with the error. Ending the process first lets
            // the drain thread catch the last of them.
            session.process.terminateGroup()
            throw HealthFailure(underlying: error, stderr: session.process.capturedStderr)
        }
    }

    // MARK: - Eras

    private enum Era {
        case modern(JSONDocument)
        case handshake
        case exited
    }

    /// Sends `server/discover` and classifies the answer.
    private func probe(_ session: Session, deadline: Date, timeout: TimeInterval) throws -> Era {
        do {
            try session.process.write(line: Self.request(id: Self.discoverID, method: "server/discover", params: [:], modern: true), deadline: deadline, timeout: timeout)
        } catch ProcessError.timedOut(let after) {
            throw ProcessError.timedOut(after: after)
        } catch {
            return .exited
        }
        let probeDeadline = min(deadline, Date().addingTimeInterval(min(Self.probeLimit, timeout / 4)))
        do {
            switch try read(id: Self.discoverID, session: session, deadline: probeDeadline, timeout: timeout) {
            case .result(let document):
                guard let versions = document.value(at: ["result", "supportedVersions"])?.elements?.compactMap(\.stringValue) else {
                    // An answer that is not a discovery result: a lenient
                    // handshake-era server that replies to anything.
                    return .handshake
                }
                guard versions.contains(Self.modernProtocolVersion) else {
                    throw MCPError.protocolError(Self.unsupportedVersions(versions))
                }
                return .modern(document)
            case .error(let code, let message, let supported):
                if code == Self.unsupportedProtocolVersionCode {
                    throw MCPError.protocolError(Self.unsupportedVersions(supported))
                }
                if Self.modernErrorCodes.contains(code) {
                    throw MCPError.serverError(code: code, message: message)
                }
                return .handshake
            }
        } catch MCPError.closedBeforeAnswering {
            return .exited
        } catch ProcessError.writeFailed {
            return .exited
        } catch ProcessError.timedOut {
            guard Date() < deadline else { throw ProcessError.timedOut(after: timeout) }
            return .handshake
        }
    }

    private static func isModernDiscovery(_ document: JSONDocument) -> Bool {
        document.value(at: ["result", "supportedVersions"])?.elements?.contains { $0.stringValue == modernProtocolVersion } == true
    }

    private static func unsupportedVersions(_ versions: [String]) -> String {
        let list = versions.isEmpty ? "none it would name" : versions.joined(separator: ", ")
        return "the server supports MCP \(list); Kytto speaks \(modernProtocolVersion) and the handshake revisions up to \(preferredProtocolVersion)"
    }

    private func handshakeListing(session: Session, deadline: Date, timeout: TimeInterval) throws -> ToolListing {
        var initialize: JSONDocument
        do {
            initialize = try initializeHandshake(version: Self.preferredProtocolVersion, session: session, deadline: deadline, timeout: timeout)
        } catch MCPHandshakeRetry.version(let version) {
            // A strict server named the revisions it accepts instead of
            // answering with its own; one retry with the best shared one.
            initialize = try initializeHandshake(version: version, session: session, deadline: deadline, timeout: timeout, allowRetry: false)
        }
        try session.process.write(line: #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#, deadline: deadline, timeout: timeout)
        let capabilityNames = initialize.value(at: ["result", "capabilities"])?.keys.sorted() ?? []
        return try listing(
            capabilityNames: capabilityNames,
            serverName: initialize.value(at: ["result", "serverInfo", "name"])?.stringValue,
            serverVersion: initialize.value(at: ["result", "serverInfo", "version"])?.stringValue,
            negotiated: initialize.value(at: ["result", "protocolVersion"])?.stringValue,
            modern: false,
            session: session,
            deadline: deadline,
            timeout: timeout
        )
    }

    private enum MCPHandshakeRetry: Error { case version(String) }

    private func initializeHandshake(version: String, session: Session, deadline: Date, timeout: TimeInterval, allowRetry: Bool = true) throws -> JSONDocument {
        try session.process.write(line: Self.request(id: 1, method: "initialize", params: [
            "protocolVersion": version,
            "capabilities": [String: Any](),
            "clientInfo": Self.clientInfo,
        ], modern: false), deadline: deadline, timeout: timeout)
        switch try read(id: 1, session: session, deadline: deadline, timeout: timeout) {
        case .result(let document):
            return document
        case .error(let code, let message, let supported):
            if allowRetry, let shared = Self.handshakeProtocolVersions.first(where: { supported.contains($0) }), shared != version {
                throw MCPHandshakeRetry.version(shared)
            }
            throw MCPError.serverError(code: code, message: message)
        }
    }

    private func modernListing(discover: JSONDocument, session: Session, deadline: Date, timeout: TimeInterval) throws -> ToolListing {
        let serverInfo = discover.value(at: ["result", "_meta", "io.modelcontextprotocol/serverInfo"])
        return try listing(
            capabilityNames: discover.value(at: ["result", "capabilities"])?.keys.sorted() ?? [],
            serverName: serverInfo?["name"]?.stringValue,
            serverVersion: serverInfo?["version"]?.stringValue,
            negotiated: Self.modernProtocolVersion,
            modern: true,
            session: session,
            deadline: deadline,
            timeout: timeout
        )
    }

    // MARK: - Messages

    /// A JSON-RPC request. Modern requests carry the version, client identity
    /// and capabilities in `_meta`, since there is no handshake to hold them.
    static func request(id: Int, method: String, params: [String: Any], modern: Bool) -> String {
        var params = params
        if modern {
            params["_meta"] = [
                "io.modelcontextprotocol/protocolVersion": modernProtocolVersion,
                "io.modelcontextprotocol/clientInfo": clientInfo,
                "io.modelcontextprotocol/clientCapabilities": [String: Any](),
            ]
        }
        let body: [String: Any] = ["jsonrpc": "2.0", "id": id, "method": method, "params": params]
        let data = (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys, .withoutEscapingSlashes])) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    private enum Reply {
        case result(JSONDocument)
        case error(code: Int, message: String, supported: [String])
    }

    /// Reads until the response with `id` arrives.
    ///
    /// Skips anything else on the way: notifications, plain text, and requests
    /// the server makes of Kytto. Servers are not supposed to print to stdout,
    /// but enough of them log a banner there that refusing to tolerate it would
    /// fail perfectly working servers. A server *request* (`ping`, `roots/list`)
    /// gets a method-not-found answer, so a server that waits for one is not
    /// left hanging until the timeout, and its method is noted for Doctor.
    private func read(id: Int, session: Session, deadline: Date, timeout: TimeInterval) throws -> Reply {
        while true {
            guard let line = try session.process.readLine(deadline: deadline, timeout: timeout) else {
                throw MCPError.closedBeforeAnswering
            }
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("{"), let document = try? JSONDocument.parse(trimmed),
                  document.root["jsonrpc"]?.stringValue == "2.0" else {
                continue
            }
            if let method = document.root["method"]?.stringValue {
                session.observe(method)
                if let requestID = document.root.member("id"), !requestID.value.isNull {
                    let echoed = document.slice(requestID.value.span)
                    // A server that stopped reading cannot hold this write
                    // past the check's deadline (AUD-04); one that closed its
                    // input is still read to the end.
                    do {
                        try session.process.write(
                            line: #"{"jsonrpc":"2.0","id":\#(echoed),"error":{"code":-32601,"message":"Method not found"}}"#,
                            deadline: deadline, timeout: timeout
                        )
                    } catch ProcessError.writeFailed {}
                }
                continue
            }
            guard let responseID = document.root["id"]?.numberValue else { continue }
            if responseID == Double(Self.discoverID), id != Self.discoverID, document.root["result"] != nil {
                session.lateDiscovery = document
            }
            guard responseID == Double(id) else { continue }
            if let error = document.root["error"] {
                return .error(
                    code: try MCPHTTPClient.errorCode(error) { MCPError.protocolError($0) },
                    message: error["message"]?.stringValue ?? "no message",
                    supported: error.value(at: ["data", "supported"])?.elements?.compactMap(\.stringValue) ?? []
                )
            }
            guard document.root["result"] != nil else {
                throw MCPError.protocolError("response \(id) had neither result nor error")
            }
            return .result(document)
        }
    }

    private func readResponse(id: Int, session: Session, deadline: Date, timeout: TimeInterval) throws -> JSONDocument {
        switch try read(id: id, session: session, deadline: deadline, timeout: timeout) {
        case .result(let document): return document
        case .error(let code, let message, _): throw MCPError.serverError(code: code, message: message)
        }
    }

    // MARK: - Listing

    private func listing(
        capabilityNames: [String],
        serverName: String?,
        serverVersion: String?,
        negotiated: String?,
        modern: Bool,
        session: Session,
        deadline: Date,
        timeout: TimeInterval
    ) throws -> ToolListing {
        var notes: [String] = []
        var pages: [JSONDocument] = []
        do {
            try session.process.write(line: Self.request(id: 2, method: "tools/list", params: [:], modern: modern), deadline: deadline, timeout: timeout)
            let first = try readResponse(id: 2, session: session, deadline: deadline, timeout: timeout)
            pages = morePages(after: first, modern: modern, session: session, deadline: deadline, timeout: timeout, notes: &notes)
        } catch MCPError.serverError(let code, _) where code == -32601 && !capabilityNames.contains("tools") {
            // A prompts- or resources-only server: it never claimed to have
            // tools, so declining to list them is not a failure.
            notes.append("The server does not offer tools.")
        }

        let entries = pages.flatMap { page in
            (page.value(at: ["result", "tools"])?.elements ?? []).map { (page: page, tool: $0) }
        }
        if let first = pages.first, first.value(at: ["result", "tools"])?.elements == nil {
            throw MCPError.protocolError("tools/list did not return a tools array")
        }

        // Measured on each tool's verbatim entry rather than on a
        // re-serialization of Kytto's model, for the same reason `rawToolsJSON`
        // is kept: the cost is what the client hands the model, whitespace and
        // key order included. The per-tool numbers therefore sum to slightly
        // less than the whole-array weight — the brackets and separators
        // between them belong to no single tool.
        let tokenizer = BPETokenizer.cl100kBase
        let summaries = entries.map { page, tool in
            ToolSummary(
                // Last-wins, as the client that hands these to the model reads them.
                name: tool.lastValue("name")?.stringValue ?? "(unnamed)",
                description: tool.lastValue("description")?.stringValue,
                inputSchemaJSON: tool.lastValue("inputSchema").map { page.slice($0.span) },
                annotations: ToolAnnotations(node: tool.lastValue("annotations")),
                tokenCount: tokenizer?.countTokens(page.slice(tool.span))
            )
        }

        let promptCount = optionalListCount(
            capability: "prompts", method: "prompts/list", resultKey: "prompts", id: 3,
            capabilityNames: capabilityNames, modern: modern, session: session,
            deadline: deadline, timeout: timeout, notes: &notes
        )
        let resourceCount = optionalListCount(
            capability: "resources", method: "resources/list", resultKey: "resources", id: 4,
            capabilityNames: capabilityNames, modern: modern, session: session,
            deadline: deadline, timeout: timeout, notes: &notes
        )

        let rawToolsJSON: String
        if pages.count == 1, let first = pages.first, let node = first.value(at: ["result", "tools"]) {
            // One page: the exact bytes.
            rawToolsJSON = first.slice(node.span)
        } else {
            // Several: the entries as sent, in one array, which is what a
            // client that pages hands the model.
            rawToolsJSON = "[" + entries.map { $0.page.slice($0.tool.span) }.joined(separator: ",") + "]"
        }

        return ToolListing(
            tools: summaries,
            rawToolsJSON: rawToolsJSON,
            serverName: serverName,
            serverVersion: serverVersion,
            negotiatedProtocolVersion: negotiated,
            capabilityNames: capabilityNames,
            promptCount: promptCount,
            resourceCount: resourceCount,
            inspectionNotes: notes,
            resolvedExecutable: session.process.resolvedExecutable,
            deprecatedFeatures: DeprecatedFeature.detected(capabilityNames: capabilityNames, serverMethods: session.observedMethods)
        )
    }

    /// Pages `tools/list` until the server stops returning a cursor.
    ///
    /// Unlike prompts and resources, the tool list is what everything else is
    /// measured on — token weight, the safety scan, Contract Guard — so stopping
    /// at the first page would make all of them blind to the rest. A page that
    /// cannot be fetched ends the walk with a note rather than failing a server
    /// that did answer.
    private func morePages(
        after first: JSONDocument,
        modern: Bool,
        session: Session,
        deadline: Date,
        timeout: TimeInterval,
        notes: inout [String]
    ) -> [JSONDocument] {
        var pages = [first]
        var seen: Set<String> = []
        while let cursor = pages.last?.value(at: ["result", "nextCursor"])?.stringValue {
            guard seen.insert(cursor).inserted, pages.count < Self.maxToolPages else {
                notes.append("Tool count shows the first \(pages.count) pages; the server kept offering more.")
                break
            }
            let id = 100 + pages.count
            do {
                try session.process.write(line: Self.request(id: id, method: "tools/list", params: ["cursor": cursor], modern: modern), deadline: deadline, timeout: timeout)
                let page = try readResponse(id: id, session: session, deadline: deadline, timeout: timeout)
                guard page.value(at: ["result", "tools"])?.elements != nil else {
                    notes.append("tools/list page \(pages.count + 1) did not return a tools array; counts cover the first \(pages.count).")
                    break
                }
                pages.append(page)
            } catch {
                notes.append("Tool count shows the first \(pages.count) pages; the next could not be read.")
                break
            }
        }
        return pages
    }

    private static let maxToolPages = 50

    /// Optional capabilities enrich the Inspector but do not decide whether the
    /// server's core tools handshake is healthy.
    private func optionalListCount(
        capability: String,
        method: String,
        resultKey: String,
        id: Int,
        capabilityNames: [String],
        modern: Bool,
        session: Session,
        deadline: Date,
        timeout: TimeInterval,
        notes: inout [String]
    ) -> Int? {
        guard capabilityNames.contains(capability) else { return nil }
        do {
            try session.process.write(line: Self.request(id: id, method: method, params: [:], modern: modern), deadline: deadline, timeout: timeout)
            let response = try readResponse(id: id, session: session, deadline: deadline, timeout: timeout)
            guard let values = response.value(at: ["result", resultKey])?.elements else {
                notes.append("\(method) did not return a \(resultKey) array.")
                return nil
            }
            if response.value(at: ["result", "nextCursor"]) != nil {
                notes.append("\(resultKey.capitalized) count shows the first page; the server has more.")
            }
            return values.count
        } catch {
            let message = (error as? LocalizedError)?.errorDescription ?? String(describing: error)
            notes.append("\(method) could not be inspected: \(message)")
            return nil
        }
    }
}

/// One spawned server and what it asked of Kytto along the way.
private final class Session {
    let process: ManagedProcess
    private(set) var observedMethods: Set<String> = []
    /// A discovery result that arrived after the probe had given up on it.
    var lateDiscovery: JSONDocument?

    init(process: ManagedProcess) { self.process = process }

    func observe(_ method: String) {
        if observedMethods.count < 32 { observedMethods.insert(method) }
    }
}

/// Server definitions that answered the handshake rather than `server/discover`.
///
/// The spec lets a client remember a server's era per configuration. Remembering
/// it saves a handshake-era server that ignores unknown requests the probe's
/// wait on every later check. In memory only: a relaunch probes again.
enum HandshakeMemory {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var keys: Set<String> = []

    static func key(command: String, arguments: [String], workingDirectory: String?) -> String {
        ([command, workingDirectory ?? ""] + arguments).joined(separator: "\u{0}")
    }
    static func contains(_ key: String) -> Bool { lock.withLock { keys.contains(key) } }
    static func insert(_ key: String) { lock.withLock { _ = keys.insert(key) } }
    static func remove(_ key: String) { lock.withLock { _ = keys.remove(key) } }
}

/// Features 2026-07-28 deprecates that a check saw a server use, so Doctor can
/// mention them before a client drops them.
public enum DeprecatedFeature: String, Codable, Sendable, CaseIterable {
    case logging
    case roots
    case sampling

    static func detected(capabilityNames: [String], serverMethods: Set<String>) -> [DeprecatedFeature] {
        var found: [DeprecatedFeature] = []
        if capabilityNames.contains("logging") || serverMethods.contains("notifications/message") { found.append(.logging) }
        if serverMethods.contains("roots/list") { found.append(.roots) }
        if serverMethods.contains("sampling/createMessage") { found.append(.sampling) }
        return found
    }
}

/// An error with the server's own output attached.
public struct HealthFailure: Error, LocalizedError {
    public let underlying: any Error
    public let stderr: String

    public var errorDescription: String? {
        (underlying as? LocalizedError)?.errorDescription ?? String(describing: underlying)
    }
}

extension ToolAnnotations {
    /// The four hints a tool listing may carry, or nil when it carries none.
    /// Shared by the stdio and HTTP clients so the same server reached either
    /// way records the same contract (`{}` or a title alone is "no hints").
    init?(node: JSONNode?) {
        guard let node, node.members != nil else { return nil }
        self.init(
            readOnlyHint: node.lastValue("readOnlyHint")?.boolValue,
            destructiveHint: node.lastValue("destructiveHint")?.boolValue,
            idempotentHint: node.lastValue("idempotentHint")?.boolValue,
            openWorldHint: node.lastValue("openWorldHint")?.boolValue
        )
        guard readOnlyHint != nil || destructiveHint != nil || idempotentHint != nil || openWorldHint != nil else { return nil }
    }
}
