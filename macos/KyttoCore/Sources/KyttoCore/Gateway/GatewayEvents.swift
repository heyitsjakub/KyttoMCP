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
        exitCode: Int32? = nil
    ) {
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
    private let descriptor: Int32

    public init(url: URL) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        descriptor = open(url.path, O_WRONLY | O_CREAT | O_APPEND, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        _ = fchmod(descriptor, S_IRUSR | S_IWUSR)
    }

    deinit {
        close(descriptor)
    }

    public func record(_ event: GatewayEvent) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        guard let encoded = try? encoder.encode(event) else { return }
        var bytes = Array(encoded)
        bytes.append(0x0A)

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

    public func finish(exitCode: Int32) {
        lock.lock()
        guard !finished else {
            lock.unlock()
            return
        }
        finished = true
        pendingCalls.removeAll()
        lock.unlock()
        sink.record(event(kind: .sessionEnded, exitCode: exitCode))
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
        sink.record(event(
            kind: .toolCallCompleted,
            toolName: pending.toolName,
            requestID: requestID,
            durationMilliseconds: Int(elapsed / 1_000_000),
            succeeded: error == nil,
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
        exitCode: Int32? = nil
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
            exitCode: exitCode
        )
    }

    private static func requestID(_ value: Any?) -> String? {
        GatewayJSONRPC.requestID(value)
    }
}
