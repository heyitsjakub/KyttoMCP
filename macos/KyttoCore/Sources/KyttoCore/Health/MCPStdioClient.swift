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
/// exchange is four messages, and doing it directly keeps full control over the
/// timeout and over capturing stderr, which is the part users actually need
/// when something is broken.
public struct MCPStdioClient: Sendable {
    /// The protocol revision Kytto asks for. A server that speaks a different
    /// one answers with its own, and for listing tools that is fine — so the
    /// negotiated value is recorded, not enforced.
    public static let preferredProtocolVersion = "2026-07-28"

    public init() {}

    public func listTools(
        command: String,
        arguments: [String],
        environment: [String: String],
        timeout: TimeInterval
    ) throws -> (listing: ToolListing, stderr: String) {
        let process = try ManagedProcess(
            executable: command,
            arguments: arguments,
            environment: environment
        )
        // §6: every spawn is killed, and its group with it. No exception, no
        // early return that skips this.
        defer { process.terminateGroup() }

        let deadline = Date().addingTimeInterval(timeout)

        do {
            try process.write(line: Self.initializeRequest)
            let initialize = try readResponse(
                id: 1,
                from: process,
                deadline: deadline,
                timeout: timeout
            )

            try process.write(line: #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
            try process.write(line: #"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#)
            let toolsResponse = try readResponse(
                id: 2,
                from: process,
                deadline: deadline,
                timeout: timeout
            )

            let listing = try makeListing(
                initialize: initialize,
                tools: toolsResponse,
                process: process,
                deadline: deadline,
                timeout: timeout
            )
            return (listing, process.capturedStderr)
        } catch {
            // Whatever went wrong, the server's own words explain it better than
            // ours, so they travel with the error.
            throw HealthFailure(underlying: error, stderr: process.capturedStderr)
        }
    }

    private static var initializeRequest: String {
        let params: [String: Any] = [
            "protocolVersion": preferredProtocolVersion,
            "capabilities": [:],
            "clientInfo": ["name": "Kytto", "version": "1.0"],
        ]
        let body: [String: Any] = [
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": params,
        ]
        let data = (try? JSONSerialization.data(withJSONObject: body)) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    /// Reads until the response with `id` arrives.
    ///
    /// Skips anything else on the way: notifications, requests the server makes
    /// of us, and plain text. Servers are not supposed to print to stdout, but
    /// enough of them log a banner there that refusing to tolerate it would fail
    /// perfectly working servers.
    private func readResponse(
        id: Int,
        from process: ManagedProcess,
        deadline: Date,
        timeout: TimeInterval
    ) throws -> JSONDocument {
        while true {
            guard let line = try process.readLine(deadline: deadline, timeout: timeout) else {
                throw MCPError.closedBeforeAnswering
            }
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("{"), let document = try? JSONDocument.parse(trimmed) else {
                continue
            }
            guard document.root["jsonrpc"]?.stringValue == "2.0",
                  let responseID = document.root["id"]?.numberValue,
                  responseID == Double(id) else {
                continue
            }
            if let error = document.root["error"] {
                throw MCPError.serverError(
                    code: Int(error["code"]?.numberValue ?? 0),
                    message: error["message"]?.stringValue ?? "no message"
                )
            }
            guard document.root["result"] != nil else {
                throw MCPError.protocolError("response \(id) had neither result nor error")
            }
            return document
        }
    }

    private func makeListing(
        initialize: JSONDocument,
        tools: JSONDocument,
        process: ManagedProcess,
        deadline: Date,
        timeout: TimeInterval
    ) throws -> ToolListing {
        guard let toolsNode = tools.value(at: ["result", "tools"]), let elements = toolsNode.elements else {
            throw MCPError.protocolError("tools/list did not return a tools array")
        }

        // Measured on each tool's verbatim entry rather than on a
        // re-serialization of Kytto's model, for the same reason `rawToolsJSON`
        // is kept: the cost is what the client hands the model, whitespace and
        // key order included. The per-tool numbers therefore sum to slightly
        // less than the whole-array weight — the brackets and separators
        // between them belong to no single tool.
        let tokenizer = BPETokenizer.cl100kBase
        let summaries = elements.map { tool in
            ToolSummary(
                name: tool["name"]?.stringValue ?? "(unnamed)",
                description: tool["description"]?.stringValue,
                inputSchemaJSON: tool["inputSchema"].map { tools.slice($0.span) },
                annotations: annotations(from: tool["annotations"]),
                tokenCount: tokenizer?.countTokens(tools.slice(tool.span))
            )
        }

        let capabilityNames = initialize.value(at: ["result", "capabilities"])?.keys.sorted() ?? []
        var notes: [String] = []
        let promptCount = optionalListCount(
            capability: "prompts",
            method: "prompts/list",
            resultKey: "prompts",
            id: 3,
            capabilityNames: capabilityNames,
            process: process,
            deadline: deadline,
            timeout: timeout,
            notes: &notes
        )
        let resourceCount = optionalListCount(
            capability: "resources",
            method: "resources/list",
            resultKey: "resources",
            id: 4,
            capabilityNames: capabilityNames,
            process: process,
            deadline: deadline,
            timeout: timeout,
            notes: &notes
        )

        return ToolListing(
            tools: summaries,
            rawToolsJSON: tools.slice(toolsNode.span),
            serverName: initialize.value(at: ["result", "serverInfo", "name"])?.stringValue,
            serverVersion: initialize.value(at: ["result", "serverInfo", "version"])?.stringValue,
            negotiatedProtocolVersion: initialize.value(at: ["result", "protocolVersion"])?.stringValue,
            capabilityNames: capabilityNames,
            promptCount: promptCount,
            resourceCount: resourceCount,
            inspectionNotes: notes,
            resolvedExecutable: process.resolvedExecutable
        )
    }

    private func annotations(from node: JSONNode?) -> ToolAnnotations? {
        guard let node, node.members != nil else { return nil }
        let value = ToolAnnotations(
            readOnlyHint: node["readOnlyHint"]?.boolValue,
            destructiveHint: node["destructiveHint"]?.boolValue,
            idempotentHint: node["idempotentHint"]?.boolValue,
            openWorldHint: node["openWorldHint"]?.boolValue
        )
        guard value.readOnlyHint != nil || value.destructiveHint != nil ||
                value.idempotentHint != nil || value.openWorldHint != nil
        else { return nil }
        return value
    }

    /// Optional capabilities enrich the Inspector but do not decide whether the
    /// server's core tools handshake is healthy.
    private func optionalListCount(
        capability: String,
        method: String,
        resultKey: String,
        id: Int,
        capabilityNames: [String],
        process: ManagedProcess,
        deadline: Date,
        timeout: TimeInterval,
        notes: inout [String]
    ) -> Int? {
        guard capabilityNames.contains(capability) else { return nil }
        do {
            try process.write(line: "{\"jsonrpc\":\"2.0\",\"id\":\(id),\"method\":\"\(method)\",\"params\":{}}")
            let response = try readResponse(
                id: id,
                from: process,
                deadline: deadline,
                timeout: timeout
            )
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

/// An error with the server's own output attached.
public struct HealthFailure: Error, LocalizedError {
    public let underlying: any Error
    public let stderr: String

    public var errorDescription: String? {
        (underlying as? LocalizedError)?.errorDescription ?? String(describing: underlying)
    }
}
