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

/// A second, independent reading of a message, used to prove that the first
/// one is the only reading there is.
///
/// The filter decides with Foundation's parser and then forwards the original
/// bytes, which the server reads with its own parser. That is only safe while
/// both parsers must agree. Duplicate keys are where they do not: Foundation
/// keeps the first `"name"`, Python and JavaScript keep the last, so
/// `{"name":"read_file","name":"write_file"}` was approved as one tool and run
/// as the other (AUD-07). Keys are compared after escapes are resolved and with
/// Swift's canonical equivalence, so `"name"` and a decomposed spelling
/// count as repeats too — refusing a message no conforming client sends is the
/// cheap side of that trade.
enum GatewayStrictJSON {
    struct Reading {
        let document: JSONDocument
        let hasDuplicateKeys: Bool
    }

    static func read(_ data: Data) -> Reading? {
        guard let text = String(data: data, encoding: .utf8),
              let document = try? JSONDocument.parse(text)
        else { return nil }
        return Reading(document: document, hasDuplicateKeys: hasDuplicateKeys(document.root))
    }

    /// Iterative, so a deep message costs memory rather than stack; the parser
    /// has already bounded the depth.
    static func hasDuplicateKeys(_ root: JSONNode) -> Bool {
        var stack = [root]
        while let node = stack.popLast() {
            switch node.kind {
            case .object(let members):
                var seen = Set<String>()
                for member in members {
                    guard seen.insert(member.key).inserted else { return true }
                    stack.append(member.value)
                }
            case .array(let elements):
                stack.append(contentsOf: elements)
            default:
                continue
            }
        }
        return false
    }

    /// The id exactly as the client wrote it, so a refusal can still be matched
    /// to its request — but only when there is one unambiguous id to echo.
    static func rawRequestID(in reading: Reading?) -> String? {
        guard let reading, let members = reading.document.root.members else { return nil }
        let ids = members.filter { $0.key == "id" }
        guard ids.count == 1 else { return nil }
        switch ids[0].value.kind {
        case .string, .number: return reading.document.slice(ids[0].value.span)
        default: return nil
        }
    }

    static func isBlank(_ data: Data) -> Bool {
        data.allSatisfy { $0 == 0x20 || $0 == 0x09 || $0 == 0x0D || $0 == 0x0A }
    }
}

/// Hides tools a route is not allowed to expose, in both directions (§7.11).
///
/// This is the one place Kytto stops being a transparent relay. §3.3 promises
/// byte-for-byte forwarding, and that promise still holds for every route
/// without an allow list — an unmasked route never constructs one of these.
/// When a list is present, exactly these kinds of message are altered:
///
/// - a `tools/list` **result** has hidden tools removed from its array, which
///   is what makes the context saving real rather than cosmetic. The response
///   is re-serialized, so key order within it is not preserved; that is a
///   protocol message and not a user's config file (§6 governs the latter).
/// - a `tools/call` **request** for a hidden tool is refused here and never
///   reaches the upstream server.
/// - a client message the filter cannot read in exactly one way — not JSON,
///   repeated keys, a `tools/call` without a single string `name` — is refused
///   rather than forwarded. Forwarding what the filter could not read would let
///   the server read it differently (AUD-07).
/// - a server message with repeated keys is re-serialized, so the client sees
///   the same reading the filter acted on.
///
/// Everything else — notifications, prompts, resources, banners on the server's
/// stdout — is forwarded untouched.
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
    static let parseError = -32_700
    static let invalidRequest = -32_600

    /// What the activity journal records for a refused message that had no
    /// readable tool name.
    public static let unreadableRequest = "(unreadable request)"

    /// Compared byte for byte. Swift's `String ==` treats canonically
    /// equivalent spellings as equal; the server does not, so a decomposed
    /// spelling of an exposed name is a different tool.
    private let exposed: Set<Data>
    private let lock = NSLock()
    /// Ids of `tools/list` requests still waiting for an answer, so only the
    /// matching responses are rewritten rather than anything holding a `tools`
    /// key.
    private var pendingListRequests: Set<String> = []

    public init(exposedTools: [String]) {
        exposed = Set(exposedTools.map { Data($0.utf8) })
    }

    /// Whether a tool survives the filter.
    public func allows(_ toolName: String) -> Bool { exposed.contains(Data(toolName.utf8)) }

    // MARK: - Client → server

    public func inspectClientLine(_ line: Data) -> ClientDecision {
        evaluate(line).decision
    }

    /// The refused calls in a client line, for the activity journal. Names and
    /// request ids only; arguments are never looked at. A message refused
    /// because it could not be read is recorded as ``unreadableRequest``.
    public func hiddenCalls(in line: Data) -> [(toolName: String, requestID: String?)] {
        evaluate(line, registeringLists: false).refused
    }

    private enum CallCheck {
        case notACall
        case allowed
        case hidden(String)
        case unreadable
    }

    private func check(_ object: [String: Any]) -> CallCheck {
        guard object["method"] as? String == "tools/call" else { return .notACall }
        guard let params = object["params"] as? [String: Any],
              let name = params["name"] as? String,
              // U+FFFD is what an unpaired surrogate escape decodes to. The
              // server may keep the original code unit, so the name Kytto
              // would approve is not necessarily the one it would run.
              !name.unicodeScalars.contains("\u{FFFD}")
        else { return .unreadable }
        return allows(name) ? .allowed : .hidden(name)
    }

    private func evaluate(
        _ line: Data,
        registeringLists: Bool = true
    ) -> (decision: ClientDecision, refused: [(toolName: String, requestID: String?)]) {
        // Blank lines carry nothing a server could act on.
        if GatewayStrictJSON.isBlank(line) { return (.forward, []) }

        let reading = GatewayStrictJSON.read(line)
        guard let parsed = try? JSONSerialization.jsonObject(with: line), let reading else {
            return (.refuse(error(rawID: GatewayStrictJSON.rawRequestID(in: reading), code: Self.parseError,
                                  message: "Kytto could not read this message, so it was not forwarded.")),
                    [(Self.unreadableRequest, nil)])
        }
        guard !reading.hasDuplicateKeys else {
            return (.refuse(error(rawID: GatewayStrictJSON.rawRequestID(in: reading), code: Self.invalidRequest,
                                  message: "Kytto refused a message that repeats a key: it can be read more than one way.")),
                    [(Self.unreadableRequest, nil)])
        }

        if let batch = parsed as? [Any] {
            // A batch is inspected element by element; letting it through
            // unread would let a hidden tool be called by wrapping it in `[…]`.
            // One hidden or unreadable call refuses the whole batch, answered
            // as a batch.
            let objects = batch.compactMap { $0 as? [String: Any] }
            var refused: [(toolName: String, requestID: String?)] = []
            if objects.count != batch.count { refused.append((Self.unreadableRequest, nil)) }
            for object in objects {
                switch check(object) {
                case .hidden(let name): refused.append((name, GatewayJSONRPC.requestID(object["id"])))
                case .unreadable: refused.append((Self.unreadableRequest, GatewayJSONRPC.requestID(object["id"])))
                case .allowed, .notACall: break
                }
            }
            guard !refused.isEmpty else {
                if registeringLists {
                    for object in objects where object["method"] as? String == "tools/list" {
                        register(object["id"])
                    }
                }
                return (.forward, [])
            }
            let replies: [[String: Any]] = objects.compactMap { object in
                guard let id = object["id"], GatewayJSONRPC.requestID(id) != nil else { return nil }
                return ["jsonrpc": "2.0", "id": id, "error": [
                    "code": Self.unknownTool,
                    "message": "Kytto refused this batch: it calls a tool this route does not expose, or a tool it cannot read.",
                ]]
            }
            return (.refuse(replies.isEmpty ? Data() : (try? JSONSerialization.data(withJSONObject: replies)) ?? Data()),
                    refused)
        }
        guard let object = parsed as? [String: Any] else { return (.forward, []) }

        if object["method"] as? String == "tools/list" {
            if registeringLists { register(object["id"]) }
            return (.forward, [])
        }

        let requestID = GatewayJSONRPC.requestID(object["id"])
        switch check(object) {
        case .notACall, .allowed:
            return (.forward, [])
        case .hidden(let name):
            // A notification has no id and therefore no reply to send. Dropping
            // it silently is still correct: the tool is not available.
            guard let requestID else { return (.refuse(Data()), [(name, nil)]) }
            return (.refuse(refusal(id: object["id"], rawID: requestID, toolName: name)), [(name, requestID)])
        case .unreadable:
            guard requestID != nil else { return (.refuse(Data()), [(Self.unreadableRequest, nil)]) }
            return (.refuse(error(rawID: GatewayStrictJSON.rawRequestID(in: reading), code: Self.unknownTool,
                                  message: "Kytto refused a tools/call whose tool name it cannot read: params.name must be one string.")),
                    [(Self.unreadableRequest, requestID)])
        }
    }

    private func register(_ id: Any?) {
        guard let key = GatewayJSONRPC.requestKey(id) else { return }
        lock.lock()
        pendingListRequests.insert(key)
        lock.unlock()
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

    /// An error built around the id's original text, so the id round-trips
    /// exactly even where it could not be decoded safely.
    private func error(rawID: String?, code: Int, message: String) -> Data {
        let messageJSON = (try? JSONSerialization.data(withJSONObject: [message], options: [.fragmentsAllowed]))
            .map { String(decoding: $0.dropFirst().dropLast(), as: UTF8.self) } ?? "\"\""
        return Data(#"{"jsonrpc":"2.0","id":\#(rawID ?? "null"),"error":{"code":\#(code),"message":\#(messageJSON)}}"#.utf8)
    }

    // MARK: - Server → client

    /// The line to forward, which is the original unless it is a `tools/list`
    /// result with something to remove, or a message with repeated keys.
    public func rewriteServerLine(_ line: Data) -> Data {
        guard let parsed = try? JSONSerialization.jsonObject(with: line) else { return line }
        // A repeated key would let the client read a different tool list from
        // the one filtered here, so such a message is passed on in the one
        // reading the filter used.
        let ambiguous = GatewayStrictJSON.read(line)?.hasDuplicateKeys ?? true

        if let batch = parsed as? [Any] {
            var changed = ambiguous
            let rewritten = batch.map { element -> Any in
                guard let object = element as? [String: Any], let filtered = filterListResponse(object) else {
                    return element
                }
                changed = true
                return filtered
            }
            guard changed else { return line }
            return (try? JSONSerialization.data(withJSONObject: rewritten)) ?? line
        }
        guard let object = parsed as? [String: Any] else { return line }
        if let filtered = filterListResponse(object) {
            return (try? JSONSerialization.data(withJSONObject: filtered)) ?? line
        }
        guard ambiguous else { return line }
        return (try? JSONSerialization.data(withJSONObject: object)) ?? line
    }

    /// The response with hidden tools removed, or `nil` when it is not a
    /// pending `tools/list` answer or has nothing to remove.
    private func filterListResponse(_ object: [String: Any]) -> [String: Any]? {
        guard let id = GatewayJSONRPC.requestKey(object["id"]),
              // Only a response answers the client's request. The server's own
              // requests (`roots/list`, `sampling/createMessage`) number from
              // their own id space and can reuse the id of a pending `tools/list`;
              // consuming it here would let the real reply through unfiltered.
              object["method"] == nil,
              object["result"] != nil || object["error"] != nil
        else { return nil }

        lock.lock()
        let isListResponse = pendingListRequests.remove(id) != nil
        lock.unlock()

        guard isListResponse,
              var result = object["result"] as? [String: Any],
              let tools = result["tools"] as? [Any]
        else { return nil }

        // An entry without a readable name cannot be shown to be allowed.
        let kept = tools.filter { tool in
            guard let name = (tool as? [String: Any])?["name"] as? String else { return false }
            return allows(name)
        }
        guard kept.count != tools.count else { return nil }

        result["tools"] = kept
        var rewritten = object
        rewritten["result"] = result
        return rewritten
    }
}
