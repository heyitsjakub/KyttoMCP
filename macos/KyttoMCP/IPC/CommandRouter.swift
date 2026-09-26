import Foundation
import KyttoCore
import WebKit

/// The single typed channel between the web UI and the native shell (§3).
///
/// Every native capability is one named command. The web layer has no other way
/// to reach the machine — no file access, no process spawning, no paths it has to
/// understand. Adding a capability means adding a command here and a row in
/// `docs/ipc.md`, never widening this surface in some other way.
///
/// Replies are JSON *strings* rather than bridged objects, so what JavaScript
/// receives is exactly what Swift encoded, with no WebKit type coercion in between.
@MainActor
final class CommandRouter: NSObject {
    static let messageHandlerName = "kytto"

    private var handlers: [String: (Data) async -> Data] = [:]
    private weak var webView: WKWebView?

    private let decoder = JSONDecoder()
    private let encoder = JSONEncoder()

    func attach(to webView: WKWebView) {
        self.webView = webView
    }

    // MARK: - Registration

    /// Registers a command that takes a payload.
    func register<Payload: Decodable, Response: Encodable>(
        _ name: String,
        _ handler: @escaping (Payload) async throws -> Response
    ) {
        handlers[name] = { [decoder, encoder] payloadData in
            do {
                let payload = try decoder.decode(Payload.self, from: payloadData)
                let response = try await handler(payload)
                return try encoder.encode(Envelope.success(response))
            } catch {
                return Envelope.failureData(command: name, error: error)
            }
        }
    }

    /// Registers a command that takes no payload.
    func register<Response: Encodable>(
        _ name: String,
        _ handler: @escaping () async throws -> Response
    ) {
        register(name) { (_: EmptyPayload) in try await handler() }
    }

    // MARK: - Native to web

    /// Pushes an event into the web layer. Unused in M0; M2's file watcher needs it.
    func emit(_ event: String, payload: some Encodable) async {
        guard let webView else { return }
        guard let data = try? encoder.encode(payload),
              let json = String(data: data, encoding: .utf8) else { return }
        let script = "window.__kytto && window.__kytto.emit(\(Self.jsStringLiteral(event)), \(json));"
        _ = try? await webView.evaluateJavaScript(script)
    }

    private static func jsStringLiteral(_ value: String) -> String {
        let escaped = value
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        return "\"\(escaped)\""
    }
}

// MARK: - Message handling

extension CommandRouter: WKScriptMessageHandlerWithReply {
    func userContentController(
        _ controller: WKUserContentController,
        didReceive message: WKScriptMessage,
        replyHandler: @escaping (Any?, String?) -> Void
    ) {
        let origin = message.frameInfo.securityOrigin
        guard Self.isTrustedOrigin(
            scheme: origin.protocol,
            host: origin.host,
            isMainFrame: message.frameInfo.isMainFrame
        ) else {
            replyHandler(Envelope.failureString(command: "?", error: IPCError.untrustedOrigin), nil)
            return
        }

        Task {
            replyHandler(await response(for: message.body), nil)
        }
    }

    /// Kept separate from WebKit's message object so malformed wire messages can
    /// be exercised without constructing private WebKit frame types in tests.
    func response(for rawBody: Any) async -> String {
        guard let body = rawBody as? [String: Any],
              let command = body["command"] as? String,
              !command.isEmpty else {
            return Envelope.failureString(command: "?", error: IPCError.malformedMessage)
        }

        guard let handler = handlers[command] else {
            return Envelope.failureString(command: command, error: IPCError.unknownCommand(command))
        }

        // `payload` is optional; commands that take none decode `EmptyPayload`
        // from `null`.
        let payloadData: Data
        if let payload = body["payload"], !(payload is NSNull) {
            payloadData = (try? JSONSerialization.data(withJSONObject: payload)) ?? Data("null".utf8)
        } else {
            payloadData = Data("null".utf8)
        }

        let responseData = await handler(payloadData)
        return String(data: responseData, encoding: .utf8)
            ?? Envelope.failureString(command: command, error: IPCError.responseEncodingFailed)
    }

    static func isTrustedOrigin(scheme: String, host: String, isMainFrame: Bool) -> Bool {
        isMainFrame && scheme == KyttoSchemeHandler.scheme && host == KyttoSchemeHandler.host
    }
}

// MARK: - Envelope

/// `{ ok: true, data }` or `{ ok: false, error: { code, message } }`.
///
/// Failures are data, not exceptions. The web layer renders an error state; it
/// never sees a rejected promise it has to guess the meaning of.
private enum Envelope {
    struct Success<Data: Encodable>: Encodable {
        let ok = true
        let data: Data
    }

    struct Failure: Encodable {
        struct Detail: Encodable {
            let code: String
            let message: String
        }
        let ok = false
        let error: Detail
    }

    static func success<Data: Encodable>(_ data: Data) -> Success<Data> {
        Success(data: data)
    }

    static func failureData(command: String, error: any Error) -> Data {
        let detail = Failure.Detail(code: code(for: error), message: message(for: error))
        return (try? JSONEncoder().encode(Failure(error: detail)))
            ?? Data(#"{"ok":false,"error":{"code":"encodingFailed","message":"Could not encode the error."}}"#.utf8)
    }

    static func failureString(command: String, error: any Error) -> String {
        String(data: failureData(command: command, error: error), encoding: .utf8) ?? ""
    }

    static func code(for error: any Error) -> String {
        switch error {
        case let ipc as IPCError: return ipc.code
        case is DecodingError: return "badPayload"
        case is CommandError: return "badArgument"
        case is AppModel.AppError: return "appState"
        case is ConfigWriteError: return "configWrite"
        case is BackupError: return "backup"
        case is ToggleError: return "toggle"
        case is AuthoringError: return "authoring"
        case is SecretsError: return "secrets"
        case is ProfileError: return "profile"
        case is GatewayMigrationError: return "gatewayMigration"
        case is UpdateError: return "update"
        case is MCPProvenanceLookup.LookupError: return "provenance"
        default: return "internal"
        }
    }

    private static func message(for error: any Error) -> String {
        if let ipc = error as? IPCError { return ipc.message }
        if error is DecodingError { return "The command payload did not match what this command expects." }
        return (error as? LocalizedError)?.errorDescription ?? String(describing: error)
    }
}

struct EmptyPayload: Decodable {
    init() {}
    init(from decoder: any Decoder) throws {}
}

enum IPCError: Error {
    case malformedMessage
    case untrustedOrigin
    case unknownCommand(String)
    case responseEncodingFailed

    var code: String {
        switch self {
        case .malformedMessage: "malformedMessage"
        case .untrustedOrigin: "untrustedOrigin"
        case .unknownCommand: "unknownCommand"
        case .responseEncodingFailed: "encodingFailed"
        }
    }

    var message: String {
        switch self {
        case .malformedMessage:
            "The message did not carry a command name."
        case .untrustedOrigin:
            "Native commands are available only to Kytto's main application page."
        case .unknownCommand(let name):
            "No such command: \(name). Commands are listed in docs/ipc.md."
        case .responseEncodingFailed:
            "Could not encode the native response."
        }
    }
}
