import Darwin
import Foundation

public enum GatewayDirection: String, Codable, Sendable {
    case clientToServer
    case serverToClient
}

public enum GatewayEventKind: String, Codable, Sendable {
    case sessionStarted = "session.started"
    case toolCallStarted = "tool.call.started"
    case toolCallCompleted = "tool.call.completed"
    case sessionEnded = "session.ended"
    /// Kytto answered a `tools/call` itself because the route hides that tool.
    case toolCallRefused = "tool.call.refused"
    /// The helper was started by a client but could not start the server.
    case sessionFailed = "session.failed"
}

/// Why the helper could not start a session. A fixed vocabulary rather than
/// the error text, which can name a path or a variable.
public enum GatewayFailureReason: String, Codable, Sendable {
    case secretUnavailable = "secret-unavailable"
    case launchFailed = "launch-failed"
}

/// Privacy-safe metadata from one proxied MCP exchange.
///
/// Arguments and responses are deliberately absent. Recording content becomes
/// a separate, explicit user choice later; the first gateway milestone proves
/// observability without quietly building a database of credentials and files.
public struct GatewayEvent: Codable, Equatable, Sendable {
    public let eventID: String
    public let sessionID: String
    public let timestamp: Date
    public let kind: GatewayEventKind
    public let serverID: String
    public let clientID: String
    public let toolName: String?
    public let requestID: String?
    public let durationMilliseconds: Int?
    public let succeeded: Bool?
    public let errorCode: Int?
    public let exitCode: Int32?
    /// On `session.ended`: true when the server stopped while its client was
    /// still connected — a crash or an exit nobody asked for. Nil in journals
    /// written before this was recorded.
    public let unexpected: Bool?
    /// On `session.failed`: a `GatewayFailureReason` raw value.
    public let reason: String?

    public init(
        eventID: String = UUID().uuidString.lowercased(),
        sessionID: String,
        timestamp: Date = .timestamp(),
        kind: GatewayEventKind,
        serverID: String,
        clientID: String,
        toolName: String? = nil,
        requestID: String? = nil,
        durationMilliseconds: Int? = nil,
        succeeded: Bool? = nil,
        errorCode: Int? = nil,
        exitCode: Int32? = nil,
        unexpected: Bool? = nil,
        reason: String? = nil
    ) {
        self.unexpected = unexpected
        self.reason = reason
        self.eventID = eventID
        self.sessionID = sessionID
        self.timestamp = timestamp
        self.kind = kind
        self.serverID = serverID
        self.clientID = clientID
        self.toolName = toolName
        self.requestID = requestID
        self.durationMilliseconds = durationMilliseconds
        self.succeeded = succeeded
        self.errorCode = errorCode
        self.exitCode = exitCode
    }
}

public protocol GatewayEventSinking: Sendable {
    func record(_ event: GatewayEvent)
}

public struct NullGatewayEventSink: GatewayEventSinking {
    public init() {}
    public func record(_ event: GatewayEvent) {}
}

/// Append-only JSON Lines storage. `flock` protects one shared file when several
/// clients start the same server at once, and mode 0600 keeps the local activity
/// inventory private even though payload capture is not enabled.
public final class JSONLinesGatewayEventSink: GatewayEventSinking, @unchecked Sendable {
    /// Past this size the journal is moved to `<name>.1` (replacing the one
    /// before) and a new one is started, so it never grows without bound.
    static let rotationBytes: off_t = 5 * 1024 * 1024

    private let url: URL
    private let lock = NSLock()
    private var descriptor: Int32

    public init(url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        self.url = url
        descriptor = try Self.open(url)
    }

    private static func open(_ url: URL) throws -> Int32 {
        let descriptor = Darwin.open(url.path, O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        _ = fchmod(descriptor, S_IRUSR | S_IWUSR)
        return descriptor
    }

    deinit {
        close(descriptor)
    }

    /// Follows the journal across a rotation, and performs one when it is due.
    /// Several helpers share the file; whichever notices first renames it, and
    /// the others reopen the new one when their descriptor no longer matches.
    private func currentDescriptor() -> Int32 {
        var open = stat(), onDisk = stat()
        let hasOpen = fstat(descriptor, &open) == 0
        if hasOpen, stat(url.path, &onDisk) == 0, open.st_ino == onDisk.st_ino, open.st_dev == onDisk.st_dev {
            guard open.st_size > Self.rotationBytes else { return descriptor }
            let previous = url.appendingPathExtension("1")
            _ = rename(url.path, previous.path)
        }
        if let reopened = try? Self.open(url) {
            close(descriptor)
            descriptor = reopened
        }
        return descriptor
    }

    public func record(_ event: GatewayEvent) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let encoded = try? encoder.encode(event) else { return }
        var bytes = Array(encoded)
        bytes.append(0x0A)

        lock.lock()
        defer { lock.unlock() }
        let descriptor = currentDescriptor()
        _ = flock(descriptor, LOCK_EX)
        defer { _ = flock(descriptor, LOCK_UN) }

        var offset = 0
        while offset < bytes.count {
            let count = bytes.withUnsafeBufferPointer { buffer in
                Darwin.write(descriptor, buffer.baseAddress! + offset, bytes.count - offset)
            }
            guard count > 0 else { return }
            offset += count
        }
    }
}

/// Observes newline-delimited MCP messages while the relay forwards their exact
/// bytes. It understands only enough JSON-RPC to correlate `tools/call`; it is
/// not another MCP implementation and therefore cannot alter protocol behavior.
public final class GatewayMessageObserver: @unchecked Sendable {
    private struct PendingCall {
        let toolName: String?
        let startedAt: UInt64
    }

    private let sessionID: String
    private let serverID: String
    private let clientID: String
    private let sink: any GatewayEventSinking
    private let lock = NSLock()
    private var clientBuffer = Data()
    private var serverBuffer = Data()
    private var pendingCalls: [String: PendingCall] = [:]
    private var finished = false

    public init(
        sessionID: String = UUID().uuidString.lowercased(),
        serverID: String,
        clientID: String,
        sink: any GatewayEventSinking
    ) {
        self.sessionID = sessionID
        self.serverID = serverID
        self.clientID = clientID
        self.sink = sink
    }

    public func start() {
        sink.record(event(kind: .sessionStarted))
    }

    public func accept(_ data: Data, direction: GatewayDirection) {
        lock.lock()
        defer { lock.unlock() }

        switch direction {
        case .clientToServer:
            clientBuffer.append(data)
            consumeLines(from: &clientBuffer, direction: direction)
        case .serverToClient:
            serverBuffer.append(data)
            consumeLines(from: &serverBuffer, direction: direction)
        }
    }

    /// A call Kytto refused on a narrowed route. It never reached the server,
    /// so there is no completion to wait for.
    public func recordRefusal(toolName: String, requestID: String?) {
        sink.record(event(kind: .toolCallRefused, toolName: toolName, requestID: requestID, succeeded: false))
    }

    /// The helper could not start the server at all.
    public func fail(reason: GatewayFailureReason) {
        lock.lock()
        guard !finished else {
            lock.unlock()
            return
        }
        finished = true
        lock.unlock()
        sink.record(event(kind: .sessionFailed, reason: reason.rawValue))
    }

    public func finish(exitCode: Int32, unexpected: Bool? = nil) {
        lock.lock()
        guard !finished else {
            lock.unlock()
            return
        }
        finished = true
        pendingCalls.removeAll()
        lock.unlock()
        sink.record(event(kind: .sessionEnded, exitCode: exitCode, unexpected: unexpected))
    }

    private func consumeLines(from buffer: inout Data, direction: GatewayDirection) {
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[..<newline])
            buffer.removeSubrange(...newline)
            inspect(line, direction: direction)
        }
    }

    private func inspect(_ line: Data, direction: GatewayDirection) {
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else {
            return
        }

        if direction == .clientToServer, object["method"] as? String == "tools/call" {
            let requestID = Self.requestID(object["id"])
            let toolName = (object["params"] as? [String: Any])?["name"] as? String
            if let requestID {
                pendingCalls[requestID] = PendingCall(
                    toolName: toolName,
                    startedAt: DispatchTime.now().uptimeNanoseconds
                )
            }
            sink.record(event(
                kind: .toolCallStarted,
                toolName: toolName,
                requestID: requestID
            ))
            return
        }

        guard direction == .serverToClient,
              object["method"] == nil,
              let requestID = Self.requestID(object["id"]),
              let pending = pendingCalls.removeValue(forKey: requestID)
        else { return }

        let elapsed = DispatchTime.now().uptimeNanoseconds - pending.startedAt
        let error = object["error"] as? [String: Any]
        // A tool that ran and reported failure answers with a result carrying
        // `isError: true`, not a JSON-RPC error. Both are failed calls.
        let toolFailed = (object["result"] as? [String: Any])?["isError"] as? Bool == true
        sink.record(event(
            kind: .toolCallCompleted,
            toolName: pending.toolName,
            requestID: requestID,
            durationMilliseconds: Int(elapsed / 1_000_000),
            succeeded: error == nil && !toolFailed,
            errorCode: (error?["code"] as? NSNumber)?.intValue
        ))
    }

    private func event(
        kind: GatewayEventKind,
        toolName: String? = nil,
        requestID: String? = nil,
        durationMilliseconds: Int? = nil,
        succeeded: Bool? = nil,
        errorCode: Int? = nil,
        exitCode: Int32? = nil,
        unexpected: Bool? = nil,
        reason: String? = nil
    ) -> GatewayEvent {
        GatewayEvent(
            sessionID: sessionID,
            kind: kind,
            serverID: serverID,
            clientID: clientID,
            toolName: toolName,
            requestID: requestID,
            durationMilliseconds: durationMilliseconds,
            succeeded: succeeded,
            errorCode: errorCode,
            exitCode: exitCode,
            unexpected: unexpected,
            reason: reason
        )
    }

    private static func requestID(_ value: Any?) -> String? {
        GatewayJSONRPC.requestID(value)
    }
}
