import Foundation

/// One client's server entry, reduced to the handful of things Kytto models.
///
/// §4: never assume two clients accept the same shape. The fields overlap
/// heavily in practice, but the differences that exist — VS Code's `servers` key,
/// Cursor's `envFile`/`headers`, extensions' `${__dirname}` args, Codex spelling
/// the whole thing in TOML — are why this normalizes on read instead of passing
/// raw dictionaries around.
///
/// Reading is the only thing that varies per format. Everything after it — how a
/// transport is inferred, what a fingerprint is made of — happens once, here, so
/// a second spelling of a config file cannot quietly grow a second set of rules.
struct ServerFields {
    var command: String?
    var url: String?
    /// `type` as the file states it, if it does.
    var declaredType: String?
    var args: [String] = []
    var env: [EnvEntry] = []

    /// - Parameter sourceText: the definition exactly as it appears in the file
    ///   it came from, which is what gets parked and what gets copied verbatim
    ///   into another client of the same format.
    func makeServer(
        name: String,
        origin: Origin,
        isBundled: Bool,
        sourceText: String
    ) -> Server {
        Server(
            id: Server.identity(for: name),
            name: name,
            transport: resolveTransport(),
            command: command,
            args: args,
            env: env,
            url: url,
            enabledIn: [:],
            origin: origin,
            fingerprint: Server.fingerprint(command: command, args: args, url: url),
            isBundled: isBundled,
            definitionSource: sourceText
        )
    }

    /// `type` when the client states it, otherwise inferred. Clients disagree on
    /// whether `type` is required, and older entries predate it entirely.
    private func resolveTransport() -> Transport {
        if let declaredType, let transport = Transport(rawValue: declaredType.lowercased()) {
            return transport
        }
        guard let url else { return .stdio }
        if command != nil { return .stdio }
        // A bare URL is HTTP unless the endpoint says otherwise; `/sse` is the
        // convention the older transport used.
        return url.hasSuffix("/sse") ? .sse : .http
    }
}

// MARK: - JSON

extension ServerFields {
    init(node: JSONNode) {
        command = node["command"]?.stringValue
        url = node["url"]?.stringValue
        declaredType = node["type"]?.stringValue
        args = node["args"]?.elements?.compactMap(\.stringValue) ?? []
        env = (node["env"]?.members ?? []).map {
            EnvEntry(key: $0.key, value: $0.value.stringValue)
        }
    }
}

// MARK: - TOML

extension ServerFields {
    /// - Parameter envTable: the server's `[…​.env]` sub-table, when it has one.
    ///   TOML lets the same data be written either as a sub-table or as an inline
    ///   `env = { … }`, and a config that has been edited by hand may hold either.
    init(table: TOMLTable, envTable: TOMLTable?) {
        command = table.value("command")?.stringValue
        url = table.value("url")?.stringValue
        declaredType = table.value("type")?.stringValue
        args = table.value("args")?.elements?.compactMap(\.stringValue) ?? []

        if let envTable {
            env = envTable.pairs.compactMap { pair in
                guard let key = pair.name else { return nil }
                return EnvEntry(key: key, value: pair.value.stringValue)
            }
        } else if let inline = table.value("env")?.inlinePairs {
            env = inline.compactMap { pair in
                guard let key = pair.name else { return nil }
                return EnvEntry(key: key, value: pair.value.stringValue)
            }
        }
    }

    init(inlineValue: TOMLValue) {
        let pairs = inlineValue.inlinePairs ?? []
        func value(_ name: String) -> TOMLValue? {
            pairs.first { $0.name == name }?.value
        }
        command = value("command")?.stringValue
        url = value("url")?.stringValue
        declaredType = value("type")?.stringValue
        args = value("args")?.elements?.compactMap(\.stringValue) ?? []
        env = (value("env")?.inlinePairs ?? []).compactMap { pair in
            guard let key = pair.name else { return nil }
            return EnvEntry(key: key, value: pair.value.stringValue)
        }
    }
}
