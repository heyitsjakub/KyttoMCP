import Foundation

/// Splits a byte stream into the newline-delimited messages MCP stdio uses.
///
/// Kept separate from the filter so the framing can be tested against chunk
/// boundaries — a `tools/list` response arriving in three reads is the normal
/// case, not the edge case.
public struct GatewayLineFramer {
    private var buffer = Data()

    public init() {}

    /// Calls `body` once per complete line, without the newline.
    public mutating func consume(_ data: Data, _ body: (Data) -> Void) {
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 0x0A) {
            body(Data(buffer[..<newline]))
            buffer.removeSubrange(...newline)
        }
    }

    /// Whatever arrived without a closing newline. A server that exits
    /// mid-message still gets its bytes forwarded rather than swallowed.
    public mutating func flush(_ body: (Data) -> Void) {
        guard !buffer.isEmpty else { return }
        body(buffer)
        buffer.removeAll()
    }
}

enum GatewayJSONRPC {
    /// JSON-RPC ids are strings or numbers; booleans are neither.
    static func requestID(_ value: Any?) -> String? {
        if let text = value as? String { return text }
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() {
            return number.stringValue
        }
        return nil
    }

    /// The id as a correlation key, kind included: `1` and `"1"` are different
    /// requests, and matching them up would filter (or skip) the wrong reply.
    static func requestKey(_ value: Any?) -> String? {
        guard let id = requestID(value) else { return nil }
        return (value is String ? "s:" : "n:") + id
    }
}

/// Hides tools a route is not allowed to expose, in both directions (§7.11).
///
/// This is the one place Kytto stops being a transparent relay. §3.3 promises
/// byte-for-byte forwarding, and that promise still holds for every route
/// without an allow list — an unmasked route never constructs one of these.
/// When a list is present, exactly two kinds of message are altered:
///
/// - a `tools/list` **result** has hidden tools removed from its array, which
///   is what makes the context saving real rather than cosmetic. The response
///   is re-serialized, so key order within it is not preserved; that is a
///   protocol message and not a user's config file (§6 governs the latter).
/// - a `tools/call` **request** for a hidden tool is refused here and never
///   reaches the upstream server.
///
/// Everything else — notifications, prompts, resources, banners on stdout,
/// anything that is not valid JSON — is forwarded untouched.
///
/// The allow list is exact and closed: a tool the server starts offering after
/// the list was chosen stays hidden until the user says otherwise. A context
/// budget that quietly grows when a server updates would defeat the point of
/// setting one, and Contract Guard already reports the new tool (§7.10).
public final class GatewayToolFilter: @unchecked Sendable {
    public enum ClientDecision: Equatable {
        /// Send the line upstream unchanged.
        case forward
        /// Do not send it. Write this JSON-RPC error back to the client instead.
        case refuse(Data)
    }

    /// JSON-RPC "invalid params" — what MCP servers answer for a tool they do
    /// not have, which is the behaviour being imitated. "Method not found"
    /// would tell the client the server has no `tools/call` at all.
    static let unknownTool = -32_602

    private let exposed: Set<String>
    private let lock = NSLock()
    /// Ids of `tools/list` requests still waiting for an answer, so only the
    /// matching responses are rewritten rather than anything holding a `tools`
    /// key.
    private var pendingListRequests: Set<String> = []

    public init(exposedTools: [String]) {
        exposed = Set(exposedTools)
    }

    /// Whether a tool survives the filter.
    public func allows(_ toolName: String) -> Bool { exposed.contains(toolName) }

    // MARK: - Client → server

    public func inspectClientLine(_ line: Data) -> ClientDecision {
        guard let parsed = try? JSONSerialization.jsonObject(with: line) else { return .forward }
        if let batch = parsed as? [Any] {
            // A batch is inspected element by element; letting it through
            // unread would let a hidden tool be called by wrapping it in `[…]`.
            // One hidden call refuses the whole batch, answered as a batch.
            let objects = batch.compactMap { $0 as? [String: Any] }
            let hidden = objects.contains { object in
                object["method"] as? String == "tools/call"
                    && ((object["params"] as? [String: Any])?["name"] as? String).map { !allows($0) } ?? false
            }
            guard hidden else {
                for object in objects where object["method"] as? String == "tools/list" {
                    if let id = GatewayJSONRPC.requestKey(object["id"]) {
                        lock.lock(); pendingListRequests.insert(id); lock.unlock()
                    }
                }
                return .forward
            }
            let replies: [[String: Any]] = objects.compactMap { object in
                guard let id = object["id"], GatewayJSONRPC.requestID(id) != nil else { return nil }
                return ["jsonrpc": "2.0", "id": id, "error": [
                    "code": Self.unknownTool,
                    "message": "Kytto refused this batch: it calls a tool this route does not expose.",
                ]]
            }
            return .refuse(replies.isEmpty ? Data() : (try? JSONSerialization.data(withJSONObject: replies)) ?? Data())
        }
        guard let object = parsed as? [String: Any] else { return .forward }

        switch object["method"] as? String {
        case "tools/list":
            if let id = GatewayJSONRPC.requestKey(object["id"]) {
                lock.lock()
                pendingListRequests.insert(id)
                lock.unlock()
            }
            return .forward

        case "tools/call":
            guard let name = (object["params"] as? [String: Any])?["name"] as? String,
                  !allows(name)
            else { return .forward }
            // A notification has no id and therefore no reply to send. Dropping
            // it silently is still correct: the tool is not available.
            guard let id = GatewayJSONRPC.requestID(object["id"]) else { return .refuse(Data()) }
            return .refuse(refusal(id: object["id"], rawID: id, toolName: name))

        default:
            return .forward
        }
    }

    /// The hidden tools a client line tries to call, for the activity journal.
    /// Names and request ids only; arguments are never looked at.
    public func hiddenCalls(in line: Data) -> [(toolName: String, requestID: String?)] {
        guard let parsed = try? JSONSerialization.jsonObject(with: line) else { return [] }
        let objects = (parsed as? [Any])?.compactMap { $0 as? [String: Any] }
            ?? (parsed as? [String: Any]).map { [$0] } ?? []
        return objects.compactMap { object in
            guard object["method"] as? String == "tools/call",
                  let name = (object["params"] as? [String: Any])?["name"] as? String,
                  !allows(name)
            else { return nil }
            return (name, GatewayJSONRPC.requestID(object["id"]))
        }
    }

    private func refusal(id: Any?, rawID: String, toolName: String) -> Data {
        // The id has to go back exactly as it arrived — a client that sent a
        // number and gets a string back cannot match its own request.
        let body: [String: Any] = [
            "jsonrpc": "2.0",
            "id": id ?? rawID,
            "error": [
                "code": Self.unknownTool,
                "message": "\(toolName) is not available: Kytto is not exposing this tool on this route.",
            ],
        ]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data()
    }

    // MARK: - Server → client

    /// The line to forward, which is the original unless it is a `tools/list`
    /// result with something to remove.
    public func rewriteServerLine(_ line: Data) -> Data {
        if let batch = try? JSONSerialization.jsonObject(with: line) as? [[String: Any]] {
            let rewritten = batch.map { element -> Any in
                guard let data = try? JSONSerialization.data(withJSONObject: element) else { return element }
                let filtered = rewriteServerLine(data)
                return filtered == data ? element : ((try? JSONSerialization.jsonObject(with: filtered)) ?? element)
            }
            return (try? JSONSerialization.data(withJSONObject: rewritten)) ?? line
        }
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let id = GatewayJSONRPC.requestKey(object["id"]),
              // Only a response answers the client's request. The server's own
              // requests (`roots/list`, `sampling/createMessage`) number from
              // their own id space and can reuse the id of a pending `tools/list`;
              // consuming it here would let the real reply through unfiltered.
              object["method"] == nil,
              object["result"] != nil || object["error"] != nil
        else { return line }

        lock.lock()
        let isListResponse = pendingListRequests.remove(id) != nil
        lock.unlock()

        guard isListResponse,
              var result = object["result"] as? [String: Any],
              let tools = result["tools"] as? [[String: Any]]
        else { return line }

        let kept = tools.filter { tool in
            guard let name = tool["name"] as? String else { return false }
            return allows(name)
        }
        guard kept.count != tools.count else { return line }

        result["tools"] = kept
        var rewritten = object
        rewritten["result"] = result
        return (try? JSONSerialization.data(withJSONObject: rewritten)) ?? line
    }
}
