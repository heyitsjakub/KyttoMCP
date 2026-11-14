import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Streamable HTTP inspection (§7.3): read-only listings, never tools/call.
///
/// Speaks both eras. The first request is a modern `server/discover` carrying
/// `MCP-Protocol-Version: 2026-07-28` and `Mcp-Method`; a discovery result or a
/// recognised modern error settles it. Anything else — the 400 a handshake-era
/// server gives a request without a session, a JSON-RPC error it invents — falls
/// back to the `initialize` handshake, and a handshake that is refused outright
/// is checked for the deprecated HTTP+SSE transport before being called broken
/// (spec: basic/transports/streamable-http, "Backward Compatibility").
public struct MCPHTTPClient: Sendable {
    public static let preferredProtocolVersion = MCPStdioClient.preferredProtocolVersion
    public static let modernProtocolVersion = MCPStdioClient.modernProtocolVersion
    private let transport: RemoteHTTPTransport
    private let maxResponseBytes = 4 * 1024 * 1024
    private let maxTotalBytes = 8 * 1024 * 1024

    public init(transport: RemoteHTTPTransport = .init()) { self.transport = transport }

    /// What one POST came back with, before the caller decides what it means.
    private enum Answer {
        case result(JSONDocument)
        case rpcError(code: Int, supported: [String])
        case status(Int, rpcCode: Int?, supported: [String])
    }

    public func listTools(url: String, headers: [String: String] = [:], timeout: TimeInterval = 10) throws -> ToolListing {
        let endpoint = try RemoteHTTPTransport.endpoint(url)
        try Self.validate(headers)
        let deadline = Date().addingTimeInterval(min(max(timeout, 0.05), 60))
        var sessionID: String?
        var protocolVersion: String?
        var modern = false
        var receivedBytes = 0

        func post(_ body: [String: Any], method: String, id: Int?) throws -> Answer? {
            var request = URLRequest(url: endpoint)
            request.httpMethod = "POST"
            request.httpBody = try JSONSerialization.data(withJSONObject: body, options: [.withoutEscapingSlashes])
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("application/json, text/event-stream", forHTTPHeaderField: "Accept")
            for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
            if let sessionID { request.setValue(sessionID, forHTTPHeaderField: "MCP-Session-Id") }
            if let protocolVersion { request.setValue(protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version") }
            if modern { request.setValue(method, forHTTPHeaderField: "Mcp-Method") }
            let reply = try transport.send(request, deadline: deadline, limit: min(maxResponseBytes, maxTotalBytes - receivedBytes), responseID: id)
            receivedBytes += reply.body.count
            if reply.status == 401 || (reply.status == 403 && reply.header("www-authenticate") != nil) {
                throw RemoteHTTPError.authorizationRequired(challenge: reply.header("www-authenticate"))
            }
            guard (200..<300).contains(reply.status) else {
                // Only the error code and the advertised versions are read from
                // an error body; its message can echo headers or signed URLs.
                let error = Self.rpcError(in: reply)
                return .status(reply.status, rpcCode: error?.code, supported: error?.supported ?? [])
            }
            // A notification is accepted with any 2xx; 202 is what the spec
            // asks for, but working clients do not insist on it.
            guard let id else { return nil }
            if let assigned = reply.header("mcp-session-id"), sessionID == nil, !modern {
                guard assigned.utf8.count <= 1024,
                      !assigned.isEmpty, assigned.utf8.allSatisfy({ (0x21...0x7E).contains($0) }) else {
                    throw RemoteHTTPError.protocolFailure("invalid session header")
                }
                sessionID = assigned
            }
            for message in try Self.messages(in: reply) {
                guard let document = RemoteHTTPTransport.document(message), document.root["jsonrpc"]?.stringValue == "2.0",
                      document.root["id"]?.numberValue == Double(id) else { continue }
                if let error = document.root["error"] {
                    let code = try Self.errorCode(error) { RemoteHTTPError.protocolFailure($0) }
                    return .rpcError(code: code,
                                     supported: error.value(at: ["data", "supported"])?.elements?.compactMap(\.stringValue) ?? [])
                }
                guard document.root["result"]?.members != nil else {
                    throw RemoteHTTPError.protocolFailure("the response has no result object")
                }
                return .result(document)
            }
            throw RemoteHTTPError.protocolFailure("no matching JSON-RPC response")
        }
        func request(_ method: String, id: Int, params: [String: Any] = [:]) throws -> JSONDocument {
            var params = params
            if modern { params["_meta"] = Self.meta }
            switch try post(["jsonrpc": "2.0", "id": id, "method": method, "params": params], method: method, id: id) {
            case .result(let document)?:
                return document
            case .rpcError?:
                // Server error messages can echo Authorization headers or signed URL queries.
                throw RemoteHTTPError.protocolFailure("the server returned a JSON-RPC error")
            case .status(let status, _, _)?:
                throw RemoteHTTPError.httpStatus(status)
            case nil:
                throw RemoteHTTPError.protocolFailure("no matching JSON-RPC response")
            }
        }
        defer {
            if let sessionID, Date() < deadline {
                var request = URLRequest(url: endpoint)
                request.httpMethod = "DELETE"
                for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
                request.setValue(sessionID, forHTTPHeaderField: "MCP-Session-Id")
                request.setValue(protocolVersion, forHTTPHeaderField: "MCP-Protocol-Version")
                _ = try? transport.send(request, deadline: min(deadline, Date().addingTimeInterval(0.3)), limit: 1024)
            }
        }

        // Modern first: one POST that a 2026-07-28 server must answer.
        modern = true
        protocolVersion = Self.modernProtocolVersion
        let discovery = try post(["jsonrpc": "2.0", "id": 1, "method": "server/discover", "params": ["_meta": Self.meta]],
                                 method: "server/discover", id: 1)
        let capabilities: [String]
        let serverName: String?
        let serverVersion: String?
        let negotiated: String
        var deprecated: [DeprecatedFeature] = []
        switch discovery {
        case .result(let document)? where document.value(at: ["result", "supportedVersions"])?.elements != nil:
            let versions = document.value(at: ["result", "supportedVersions"])?.elements?.compactMap(\.stringValue) ?? []
            guard versions.contains(Self.modernProtocolVersion) else { throw Self.unsupported(versions) }
            let info = document.value(at: ["result", "_meta", "io.modelcontextprotocol/serverInfo"])
            capabilities = document.value(at: ["result", "capabilities"])?.keys.sorted() ?? []
            serverName = info?["name"]?.stringValue
            serverVersion = info?["version"]?.stringValue
            negotiated = Self.modernProtocolVersion
        case .rpcError(let code, let supported)? where MCPStdioClient.modernErrorCodes.contains(code),
             .status(_, let code?, let supported)? where MCPStdioClient.modernErrorCodes.contains(code):
            if code == MCPStdioClient.unsupportedProtocolVersionCode { throw Self.unsupported(supported) }
            throw RemoteHTTPError.protocolFailure("the server rejected Kytto's request metadata (\(code))")
        case .status(let status, _, _)? where status == 403 || status == 429 || (300..<400).contains(status):
            // Forbidden, rate-limited or redirected says nothing about the era;
            // a handshake would meet the same answer.
            throw RemoteHTTPError.httpStatus(status)
        default:
            // Handshake era.
            modern = false
            protocolVersion = nil
            let initialize: JSONDocument
            switch try post(["jsonrpc": "2.0", "id": 1, "method": "initialize", "params": [
                "protocolVersion": Self.preferredProtocolVersion, "capabilities": [String: Any](),
                "clientInfo": MCPStdioClient.clientInfo,
            ]], method: "initialize", id: 1) {
            case .result(let document)?:
                initialize = document
            case .status(let status, _, _)? where [400, 404, 405].contains(status):
                if try legacySSE(endpoint: endpoint, headers: headers, deadline: deadline) { throw RemoteHTTPError.legacySSE }
                throw RemoteHTTPError.httpStatus(status)
            case .status(let status, _, _)?:
                throw RemoteHTTPError.httpStatus(status)
            case .rpcError?, nil:
                throw RemoteHTTPError.protocolFailure("the server returned a JSON-RPC error")
            }
            guard let version = initialize.value(at: ["result", "protocolVersion"])?.stringValue,
                  MCPStdioClient.handshakeProtocolVersions.contains(version),
                  initialize.value(at: ["result", "capabilities"])?.members != nil else {
                throw RemoteHTTPError.protocolFailure("unsupported protocol version or missing capabilities")
            }
            protocolVersion = version
            negotiated = version
            switch try post(["jsonrpc": "2.0", "method": "notifications/initialized"], method: "notifications/initialized", id: nil) {
            case .status?:
                throw RemoteHTTPError.protocolFailure("the initialized notification was not accepted")
            default:
                break
            }
            capabilities = initialize.value(at: ["result", "capabilities"])?.keys.sorted() ?? []
            serverName = initialize.value(at: ["result", "serverInfo", "name"])?.stringValue
            serverVersion = initialize.value(at: ["result", "serverInfo", "version"])?.stringValue
        }
        deprecated = DeprecatedFeature.detected(capabilityNames: capabilities, serverMethods: [])

        var entries: [(document: JSONDocument, tool: JSONNode)] = []
        var arrays: [String] = []
        var cursors = Set<String>()
        var cursor: String?
        if capabilities.contains("tools") {
            for page in 0..<50 {
                guard Date() < deadline else { throw RemoteHTTPError.timedOut }
                let document = try request("tools/list", id: 2 + page, params: cursor.map { ["cursor": $0] } ?? [:])
                guard let tools = document.value(at: ["result", "tools"]), let elements = tools.elements,
                      elements.allSatisfy({ $0.lastValue("name")?.stringValue != nil && $0.lastValue("inputSchema")?.members != nil }) else {
                    throw RemoteHTTPError.protocolFailure("tools/list did not return valid tool definitions")
                }
                guard entries.count + elements.count <= 10_000 else { throw RemoteHTTPError.responseTooLarge }
                entries.append(contentsOf: elements.map { (document, $0) })
                arrays.append(document.slice(tools.span))
                if let nextNode = document.value(at: ["result", "nextCursor"]) {
                    guard let next = nextNode.stringValue, !next.isEmpty, next.utf8.count <= 8192,
                          cursors.insert(next).inserted, page < 49 else {
                        throw RemoteHTTPError.protocolFailure("tools pagination did not finish within the inspection limit")
                    }
                    cursor = next
                } else { break }
            }
        }
        var notes: [String] = []
        func optionalCount(_ capability: String, method: String, id: Int) -> Int? {
            guard capabilities.contains(capability) else { return nil }
            do {
                let document = try request(method, id: id)
                guard let values = document.value(at: ["result", capability])?.elements else {
                    notes.append("\(method) did not return its expected array."); return nil
                }
                if document.value(at: ["result", "nextCursor"]) != nil {
                    notes.append("\(capability.capitalized) count shows the first page; the server has more.")
                }
                return values.count
            } catch {
                notes.append("\(method) could not be inspected."); return nil
            }
        }
        let prompts = optionalCount("prompts", method: "prompts/list", id: 100)
        let resources = optionalCount("resources", method: "resources/list", id: 101)
        let summaries = try entries.map { document, tool in
                guard Date() < deadline else { throw RemoteHTTPError.timedOut }
                return ToolSummary(
                    name: tool.lastValue("name")!.stringValue!,
                    description: tool.lastValue("description")?.stringValue,
                    inputSchemaJSON: tool.lastValue("inputSchema").map { document.slice($0.span) },
                    annotations: ToolAnnotations(node: tool.lastValue("annotations")),
                    tokenCount: try BPETokenizer.cl100kBase?.countTokens(document.slice(tool.span), deadline: deadline)
                )
            }
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        let rawToolsJSON = arrays.count == 1 ? arrays[0] : "[" + entries.map { $0.document.slice($0.tool.span) }.joined(separator: ",") + "]"
        guard Date() < deadline else { throw RemoteHTTPError.timedOut }
        return ToolListing(
            tools: summaries,
            rawToolsJSON: arrays.isEmpty ? "[]" : rawToolsJSON,
            serverName: serverName,
            serverVersion: serverVersion,
            negotiatedProtocolVersion: negotiated,
            capabilityNames: capabilities,
            promptCount: prompts, resourceCount: resources, inspectionNotes: notes,
            resolvedExecutable: "",
            deprecatedFeatures: deprecated
        )
    }

    private static var meta: [String: Any] {
        [
            "io.modelcontextprotocol/protocolVersion": modernProtocolVersion,
            "io.modelcontextprotocol/clientInfo": MCPStdioClient.clientInfo,
            "io.modelcontextprotocol/clientCapabilities": [String: Any](),
        ]
    }

    private static func unsupported(_ versions: [String]) -> RemoteHTTPError {
        .protocolFailure("the server supports MCP \(versions.isEmpty ? "versions it did not name" : versions.joined(separator: ", ")), not \(modernProtocolVersion) or a handshake revision Kytto speaks")
    }

    private static func messages(in reply: RemoteHTTPTransport.Reply) throws -> [String] {
        let mime = (reply.header("content-type") ?? "").lowercased().split(separator: ";", maxSplits: 1).first.map(String.init)
        if mime == "application/json", let text = String(data: reply.body, encoding: .utf8) { return [text] }
        if mime == "text/event-stream" { return sseMessages(reply.body) }
        throw RemoteHTTPError.protocolFailure("expected application/json or text/event-stream")
    }

    /// A JSON-RPC error's code. A missing code reads as 0, as before; one that
    /// is present but not an integer Kytto can hold is a protocol failure, not
    /// a number to convert (AUD-01).
    static func errorCode(_ error: JSONNode, _ failure: (String) -> Error) throws -> Int {
        guard let raw = error["code"] else { return 0 }
        guard let code = raw.integerValue else {
            throw failure("the JSON-RPC error code is not an integer")
        }
        return code
    }

    /// The code and advertised versions of a JSON-RPC error in an HTTP error
    /// body, and nothing else from it.
    private static func rpcError(in reply: RemoteHTTPTransport.Reply) -> (code: Int, supported: [String])? {
        guard let messages = try? messages(in: reply) else { return nil }
        for message in messages {
            guard let document = RemoteHTTPTransport.document(message), let error = document.root["error"],
                  let code = error["code"]?.integerValue else { continue }
            return (code, error.value(at: ["data", "supported"])?.elements?.compactMap(\.stringValue) ?? [])
        }
        return nil
    }

    /// Whether the endpoint is a deprecated HTTP+SSE server: a GET that opens an
    /// event stream whose first event is `endpoint`.
    private func legacySSE(endpoint: URL, headers: [String: String], deadline: Date) throws -> Bool {
        var request = URLRequest(url: endpoint)
        request.httpMethod = "GET"
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
        guard let reply = try? transport.send(request, deadline: min(deadline, Date().addingTimeInterval(2)),
                                              limit: 64 * 1024, responseID: RemoteHTTPTransport.firstEventOnly),
              reply.status == 200,
              reply.header("content-type")?.lowercased().hasPrefix("text/event-stream") == true,
              let text = String(data: reply.body, encoding: .utf8) else { return false }
        let firstEvent = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n").first ?? ""
        return firstEvent.split(separator: "\n").contains { $0.trimmingCharacters(in: .whitespaces) == "event: endpoint" || $0 == "event:endpoint" }
    }

    static func validate(_ headers: [String: String]) throws {
        let forbidden: Set<String> = ["host", "content-length", "content-type", "accept", "connection", "transfer-encoding", "mcp-session-id", "mcp-protocol-version", "cookie", "proxy-authorization"]
        guard headers.count <= 64 else { throw RemoteHTTPError.invalidHeaders }
        var names = Set<String>()
        for (name, value) in headers {
            let lowered = name.lowercased()
            guard !name.isEmpty, name.utf8.count <= 128, value.utf8.count <= 16_384,
                  name.utf8.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || "!#$%&'*+-.^_`|~".utf8.contains($0) }),
                  value.utf8.allSatisfy({ $0 >= 0x20 && $0 != 0x7F }),
                  !forbidden.contains(lowered), names.insert(lowered).inserted else {
                throw RemoteHTTPError.invalidHeaders
            }
        }
    }

    /// Only complete SSE events count; preserves multi-line data and ignores comments/heartbeats.
    static func sseMessages(_ data: Data) -> [String] {
        guard let text = String(data: data, encoding: .utf8) else { return [] }
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        return normalized.components(separatedBy: "\n\n").dropLast().compactMap { event in
            let lines = event.split(separator: "\n", omittingEmptySubsequences: false).compactMap { line -> String? in
                guard line.hasPrefix("data:") else { return nil }
                let value = line.dropFirst(5)
                return String(value.first == " " ? value.dropFirst() : value)
            }
            return lines.isEmpty ? nil : lines.joined(separator: "\n")
        }
    }
}
