import Foundation

/// Something the user should know about their configuration.
///
/// Read errors are surfaced rather than swallowed: a config Kytto cannot parse is
/// a config Kytto must never write, and the user needs to see which file and why.
public struct Diagnostic: Equatable, Sendable {
    public enum Severity: String, Codable, Sendable {
        case warning
        case error
    }

    public let severity: Severity
    public let clientID: ClientID?
    /// Display form of the path — the web layer only ever shows this (§3.2).
    public let pathDisplay: String
    public let message: String

    public init(severity: Severity, clientID: ClientID?, pathDisplay: String, message: String) {
        self.severity = severity
        self.clientID = clientID
        self.pathDisplay = pathDisplay
        self.message = message
    }
}
