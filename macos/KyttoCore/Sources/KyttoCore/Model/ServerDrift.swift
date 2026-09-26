import Foundation

/// What two clients disagree about.
public enum DriftField: String, Codable, Sendable, CaseIterable {
    case transport
    case command
    case arguments
    case url
    case environmentKeys
    case environmentValues
}

/// One distinct definition, and every client holding exactly that one.
public struct DriftVariant: Equatable, Sendable {
    /// Sorted, so a variant reads the same on every run.
    public let clientIDs: [ClientID]
    public let definition: ClientDefinition
    /// Whether this copy may be written into the other clients.
    ///
    /// An extension bundle or a plugin is installed software: its command belongs
    /// to whatever installed it, and §4 refuses to let such a definition travel.
    /// It can still be shown and compared — knowing that Claude Desktop's copy of
    /// a name is a bundle and Cursor's is an `npx` line is exactly the confusion
    /// worth surfacing — but it is not a thing to copy out of.
    public var canBeSource: Bool { !definition.isBundled }

    public init(clientIDs: [ClientID], definition: ClientDefinition) {
        self.clientIDs = clientIDs
        self.definition = definition
    }
}

/// One server's copies, compared.
///
/// `Discovery` used to notice a mismatch, raise a diagnostic reading "configured
/// differently … the command shown is the first one found", and discard the
/// evidence. That sentence names a problem and then withholds everything needed
/// to act on it: which client, differing how, and what to do about it. This is
/// the answer to all three.
public struct ServerDrift: Equatable, Sendable {
    public let serverID: String
    public let serverName: String
    /// What differs, across all copies. Empty is impossible: a report only exists
    /// when something does.
    public let fields: [DriftField]
    /// Distinct definitions, largest group first, so the copy most clients agree
    /// on is the one offered first.
    public let variants: [DriftVariant]

    public init(serverID: String, serverName: String, fields: [DriftField], variants: [DriftVariant]) {
        self.serverID = serverID
        self.serverName = serverName
        self.fields = fields
        self.variants = variants
    }

    /// Clients whose copy cannot be rewritten, whichever variant is chosen.
    public var unwritableClientIDs: [ClientID] {
        variants
            .filter { $0.definition.isBundled }
            .flatMap(\.clientIDs)
            .sorted { $0.rawValue < $1.rawValue }
    }

    /// Compares every copy of `server`. Nil when the clients agree, or when only
    /// one of them has it — a single copy cannot disagree with anything.
    public static func detect(in server: Server) -> ServerDrift? {
        let definitions = server.definitionsByClient
        guard definitions.count > 1 else { return nil }

        let fields = differingFields(across: Array(definitions.values))
        guard !fields.isEmpty else { return nil }

        // Group by the whole comparable shape rather than by `fingerprint`, which
        // covers only command, args and url. Two clients running the same command
        // with different environments have not got the same definition.
        var groups: [String: [ClientID]] = [:]
        var representative: [String: ClientDefinition] = [:]
        for (clientID, definition) in definitions {
            let key = shapeKey(definition)
            groups[key, default: []].append(clientID)
            representative[key] = representative[key] ?? definition
        }

        let variants = groups
            .compactMap { key, clientIDs -> DriftVariant? in
                guard let definition = representative[key] else { return nil }
                return DriftVariant(
                    clientIDs: clientIDs.sorted { $0.rawValue < $1.rawValue },
                    definition: definition
                )
            }
            // Largest group first; ties broken by client id so the order is stable
            // across runs rather than following dictionary iteration.
            .sorted {
                if $0.clientIDs.count != $1.clientIDs.count {
                    return $0.clientIDs.count > $1.clientIDs.count
                }
                return ($0.clientIDs.first?.rawValue ?? "") < ($1.clientIDs.first?.rawValue ?? "")
            }

        return ServerDrift(
            serverID: server.id,
            serverName: server.name,
            fields: fields,
            variants: variants
        )
    }

    /// Every server that has more than one copy and whose copies disagree.
    public static func detect(in servers: [Server]) -> [ServerDrift] {
        servers.compactMap(detect(in:))
    }

    // MARK: - Comparison

    private static func differingFields(across definitions: [ClientDefinition]) -> [DriftField] {
        var fields: [DriftField] = []

        if definitions.map(\.transport).distinctCount > 1 { fields.append(.transport) }
        if definitions.map({ $0.command ?? "" }).distinctCount > 1 { fields.append(.command) }
        if definitions.map({ $0.args.joined(separator: "\u{1}") }).distinctCount > 1 {
            fields.append(.arguments)
        }
        if definitions.map({ $0.url ?? "" }).distinctCount > 1 { fields.append(.url) }
        if definitions.map({ $0.environmentKeys.joined(separator: "\u{1}") }).distinctCount > 1 {
            fields.append(.environmentKeys)
        }
        if hasEnvironmentValueDrift(definitions) { fields.append(.environmentValues) }

        return fields.sorted { $0.rawValue < $1.rawValue }
    }

    /// A key two clients both set, to two different things.
    ///
    /// Only keys present in more than one copy are compared: a key one client
    /// simply does not have is a difference in keys, already reported as one.
    /// The values are read here and never leave — the finding is that they
    /// differ, never what they are (§6).
    private static func hasEnvironmentValueDrift(_ definitions: [ClientDefinition]) -> Bool {
        var seen: [String: String] = [:]
        for definition in definitions {
            for entry in definition.env {
                let value = entry.value ?? ""
                if let previous = seen[entry.key], previous != value { return true }
                seen[entry.key] = value
            }
        }
        return false
    }

    /// Everything that makes one copy distinct from another, values included.
    ///
    /// Stays inside this type. It is a grouping key, not a fact about the server,
    /// and it is built partly from environment values — so it must never be put
    /// in a DTO, a log line or a support report.
    private static func shapeKey(_ definition: ClientDefinition) -> String {
        var parts: [String] = [
            definition.transport.rawValue,
            definition.command ?? "",
            definition.args.joined(separator: "\u{1}"),
            definition.url ?? "",
            definition.isBundled ? "bundled" : "config",
        ]
        for entry in definition.env.sorted(by: { $0.key < $1.key }) {
            parts.append("\(entry.key)=\(entry.value ?? "")")
        }
        return parts.joined(separator: "\u{2}")
    }
}

private extension Array where Element: Hashable {
    var distinctCount: Int { Set(self).count }
}
