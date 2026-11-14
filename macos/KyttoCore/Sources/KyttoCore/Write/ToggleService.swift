import Foundation

public struct ToggleResult: Sendable {
    public let clientID: ClientID
    public let serverName: String
    public let enabled: Bool
    /// Clients hold their configuration in memory and only pick changes up on
    /// restart. Kytto states that plainly rather than pretending otherwise (§7.1).
    public let requiresRestart: Bool
    public let backupID: String?
    public let pathDisplay: String
    /// True when switching off had to remove the definition because the client
    /// has no disabled state. The definition is parked and fully restorable.
    public let wasParked: Bool
}

public enum ToggleError: Error, LocalizedError, Equatable {
    case noSourceForClient(ClientID)
    case bundledServerNotPortable(String)
    case bundledServerNotToggleable(String)
    case noParkedDefinition(String)
    case rollbackFailed(original: String, rollback: String)
    /// The definition has settings the target client's format cannot be given
    /// from Kytto's model — headers, a working directory — and copying it
    /// without them would write a server that cannot start or authenticate.
    case definitionNotPortable(String, fields: [String])
    /// The definition is Kytto's Gateway helper bound to one client's route.
    case gatewayDefinitionNotPortable(String)

    public var errorDescription: String? {
        switch self {
        case .noSourceForClient(let id):
            "No configuration source is known for \(id.rawValue)."
        case .bundledServerNotPortable(let name):
            "\"\(name)\" is a Claude Desktop extension. Extensions are installed bundles and cannot be copied into another client."
        case .bundledServerNotToggleable(let name):
            """
            "\(name)" comes from an installed plugin, not from the configuration file. \
            Its definition lives in a cache the client rewrites, so Kytto will not edit it — \
            turn the plugin off in the client that installed it.
            """
        case .noParkedDefinition(let name):
            "Kytto has no stored definition for \"\(name)\" to put back."
        case .rollbackFailed(let original, let rollback):
            "The toggle failed (\(original)), and Kytto could not completely roll it back: \(rollback)"
        case .definitionNotPortable(let name, let fields):
            """
            "\(name)" uses \(fields.joined(separator: ", ")), which Kytto cannot carry into this \
            client's configuration format. Add it to this client by hand so nothing it needs to \
            start or sign in is lost.
            """
        case .gatewayDefinitionNotPortable(let name):
            """
            "\(name)" runs through Kytto Gateway for one client. Restore its direct definition \
            there first, then add it to this client.
            """
        }
    }
}

/// Turns a matrix cell on or off.
///
/// Each client answers "how do I disable this?" differently, and the difference
/// is the substance of the feature:
///
/// - **Claude Code** keeps a deny list in a *separate* settings file. The server
///   stays configured; its name goes in or out of that list.
/// - **Claude Desktop extensions** have a boolean in their own settings file.
/// - **Everything else** has no disabled state at all, so switching off means
///   removing the definition — which is only acceptable because it is parked
///   first and restored byte for byte.
///
/// Switching on a server a client does not have copies the definition across.
/// That is the cross-client matrix doing the thing it exists to do.
public struct ToggleService: Sendable {
    private struct FileSnapshot {
        let url: URL
        let pathDisplay: String
        let data: Data?
        let digest: String?
    }

    private let home: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let parkStore: ParkStore
    private let ledger: DigestLedger
    private let descriptors: [ClientDescriptor]

    public init(
        home: URL,
        writer: ConfigWriter,
        parkStore: ParkStore,
        ledger: DigestLedger,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        self.home = home
        self.writer = writer
        self.parkStore = parkStore
        self.ledger = ledger
        self.descriptors = descriptors
    }

    public func setEnabled(
        _ enabled: Bool,
        server: Server,
        in clientID: ClientID
    ) throws -> ToggleResult {
        if case .plugin(let owner, _, _, _) = server.origin {
            if clientID == owner { throw ToggleError.bundledServerNotToggleable(server.name) }
            throw ToggleError.bundledServerNotPortable(server.name)
        }
        guard let descriptor = descriptors.first(where: { $0.id == clientID }) else {
            throw ToggleError.noSourceForClient(clientID)
        }

        if case .claudeDesktopExtension(let bundleID) = server.origin, clientID == .claudeDesktop {
            return try setExtensionEnabled(enabled, server: server, bundleID: bundleID, descriptor: descriptor)
        }

        guard let source = descriptor.editableServerMap else {
            throw ToggleError.noSourceForClient(clientID)
        }

        switch source.enablement {
        case .denyList(let file, let key, let nameField):
            return try setViaDenyList(
                enabled,
                server: server,
                clientID: clientID,
                configFile: source.file,
                serversKey: source.serversKey,
                denyFile: file,
                key: key,
                nameField: nameField
            )
        case .presence:
            return try setViaPresence(
                enabled,
                server: server,
                clientID: clientID,
                configFile: source.file,
                serversKey: source.serversKey
            )
        case .inlineFlag(let flagKey) where source.format == .toml:
            return try setViaInlineFlag(
                enabled,
                server: server,
                clientID: clientID,
                configFile: source.file,
                serversKey: source.serversKey,
                flagKey: flagKey
            )
        case .inlineFlag, .inlineDisabledFlag:
            guard source.format == .json, let flag = source.enablement.inlineFlag else {
                throw ToggleError.noSourceForClient(clientID)
            }
            return try setViaJSONFlag(
                enabled,
                server: server,
                clientID: clientID,
                configFile: source.file,
                serversKey: source.serversKey,
                flag: flag
            )
        case .extensionFlag:
            throw ToggleError.noSourceForClient(clientID)
        }
    }

    // MARK: - Definitions that have to travel

    /// Where the bytes come from when a definition has to appear in a client
    /// that does not have it.
    private enum ArrivingDefinition {
        /// Text Kytto already holds, going in exactly as it stands.
        case verbatim(String)
        /// Rebuilt from the model, because the text came out of a file spelled
        /// differently and would not mean the same thing here (§4).
        case fromModel
    }

    /// What a client should be given for a server it has never had.
    ///
    /// Shared by both paths that can be a definition's first arrival: presence
    /// clients, and Claude Code — whose deny list can only ever speak about a
    /// server its config file already lists.
    private func arrivingDefinition(
        for server: Server,
        in clientID: ClientID
    ) throws -> ArrivingDefinition {
        if let parked = parkStore.parked(clientID: clientID, serverName: server.name) {
            // Kytto's own bytes, going back exactly where they came from.
            return .verbatim(parked.sourceText)
        }
        if server.isBundled {
            // An extension or plugin is installed software, not a definition that
            // can be copied into another client's config.
            throw ToggleError.bundledServerNotPortable(server.name)
        }
        // A Gateway definition points at one client's route: copied, a second
        // client would run (and be logged and masked as) the first one.
        if server.args.contains("--route"),
           let command = server.command, URL(filePath: command).lastPathComponent == "kytto-mcp-proxy" {
            throw ToggleError.gatewayDefinitionNotPortable(server.name)
        }
        let target = descriptors.first { $0.id == clientID }
        let targetFormat = target?.editableServerMap?.format
        let targetSchema = target?.serverSchema ?? .standard
        let sourceSchema = schema(of: server.origin)
        if targetFormat == .json, server.transport == .sse, !targetSchema.acceptsSSE {
            throw ToggleError.definitionNotPortable(server.name, fields: ["the legacy HTTP+SSE transport"])
        }
        if !server.definitionSource.isEmpty, format(of: server.origin) == .json, targetFormat == .json,
           let copied = Self.portableJSON(server.definitionSource, from: sourceSchema, to: targetSchema) {
            // A copy from another JSON client. Where the two agree on the
            // per-server shape the text carries over as it stands, keeping
            // `headers`, `cwd` and keys Kytto does not model; where they spell
            // the endpoint differently only those keys are respelt.
            return .verbatim(copied)
        }
        if let sourceFormat = format(of: server.origin),
           sourceFormat != targetFormat || sourceSchema.shape != targetSchema.shape {
            let lost = Self.unportableFields(in: server.definitionSource, format: sourceFormat, schema: sourceSchema)
            if !lost.isEmpty { throw ToggleError.definitionNotPortable(server.name, fields: lost) }
        }
        if server.command != nil || server.url != nil {
            return .fromModel
        }
        throw ToggleError.noParkedDefinition(server.name)
    }

    private func literal(
        for arriving: ArrivingDefinition,
        server: Server,
        serversKey: String,
        in document: JSONDocument,
        clientID: ClientID
    ) -> String {
        switch arriving {
        case .verbatim(let text): text
        case .fromModel:
            jsonLiteral(
                for: server, serversKey: serversKey, in: document,
                schema: descriptors.first { $0.id == clientID }?.serverSchema ?? .standard
            )
        }
    }

    /// A JSON definition from a client with schema `from`, ready for one with
    /// schema `to`, without the source client's own on/off and tool state.
    /// Nil when the shapes differ and the definition has to be rendered.
    static func portableJSON(_ source: String, from: ServerSchema, to: ServerSchema) -> String? {
        guard from == to else { return to.translating(source, from: from) }
        guard let document = try? JSONDocument.parse(source),
              document.root.keys.contains(where: ServerSchema.clientStateKeys.contains)
        else { return source }
        var text = source
        for key in ServerSchema.clientStateKeys {
            guard let next = try? JSONDocument.parse(text).removingMember(key, at: []) else { return nil }
            text = next
        }
        return text
    }

    /// Launch and authentication settings a definition has that rendering it
    /// from the model (command, args, env, url) would drop.
    static func unportableFields(in source: String, format: ConfigFormat, schema: ServerSchema = .standard) -> [String] {
        switch format {
        case .json:
            guard let root = (try? JSONDocument.parse(source))?.root else { return [] }
            return schema.unportableFields(in: root)
        case .toml:
            guard let document = try? TOMLDocument.parse(source) else { return [] }
            let names = Set(document.tables.flatMap { table in
                // Sub-tables (`[mcp_servers.x.http_headers]`) count by name; the
                // server's own header names the server, not a setting.
                table.pairs.compactMap(\.key.first) + (table.path.count > 2 ? Array(table.path.suffix(1)) : [])
            })
            return ["http_headers", "env_http_headers", "bearer_token_env_var", "cwd", "env_vars"].filter(names.contains)
        }
    }

    /// The format a server's `definitionSource` is written in.
    ///
    /// Copying a server into a client that does not have it used to be a matter
    /// of moving the source text across, because the four clients that existed
    /// all spelled a definition the same way. Codex does not, so the text is only
    /// portable between clients that agree — and everywhere else the definition
    /// gets rendered from the model instead.
    private func format(of origin: Origin) -> ConfigFormat? {
        guard case .configFile(let clientID) = origin else { return nil }
        return descriptors.first { $0.id == clientID }?.editableServerMap?.format
    }

    private func schema(of origin: Origin) -> ServerSchema {
        guard case .configFile(let clientID) = origin else { return .standard }
        return descriptors.first { $0.id == clientID }?.serverSchema ?? .standard
    }

    /// A JSON object literal for a server that came from somewhere else,
    /// indented to sit among whatever is already in `document`.
    private func jsonLiteral(
        for server: Server,
        serversKey: String,
        in document: JSONDocument,
        schema: ServerSchema = .standard
    ) -> String {
        let unit = IndentStyle.detect(in: document.sourceText).text
        let map = document.value(at: [serversKey])
        let baseIndent: String = if let first = map?.members?.first {
            document.lineIndent(at: first.span.start)
        } else if let map {
            document.lineIndent(at: map.span.start) + unit
        } else {
            unit
        }
        return ServerDraft(editing: server).definition(baseIndent: baseIndent, unit: unit, schema: schema)
    }

    private func tomlBlock(for server: Server, name: String, serversKey: String, enabled: Bool) -> String {
        TOMLBuilder.serverBlock(
            name: name,
            under: serversKey,
            command: server.command,
            args: server.args,
            url: server.url,
            env: server.env,
            // Absent means on, so the key is only worth writing to say "off".
            enabled: enabled ? nil : false
        )
    }

    // MARK: - Codex: a boolean on the server's own table

    /// The kindest of the four mechanisms: the definition never leaves the file,
    /// so switching off parks nothing and switching back on cannot have lost
    /// anything. Only the first arrival of a server has to write a definition.
    private func setViaInlineFlag(
        _ enabled: Bool,
        server: Server,
        clientID: ClientID,
        configFile: PlatformPath,
        serversKey: String,
        flagKey: String
    ) throws -> ToggleResult {
        // No up-front refusal for a bundled row: discovery merges rows by
        // identity and takes `isBundled` from whichever source it read first, so
        // a Codex table can sit in a row that came from a Claude Desktop
        // extension. Codex's own table is toggled like any other; only an
        // arrival needs a portable definition, and that is checked below.
        let url = resolver.resolveServerMap(configFile, for: clientID)
        let display = resolver.displayServerMap(configFile, for: clientID)

        let receipt = try writer.edit(
            url: url,
            clientID: clientID,
            pathDisplay: display,
            expecting: ledger.digest(for: url),
            as: TOMLDocument.self
        ) { document in
            // The name in this file, which need not match the row's name
            // character for character — the matrix merges rows by identity.
            // Two tables differing only in case are both valid TOML, and Codex
            // runs both; toggling one of them would report a success it did not
            // have, so an ambiguous name is refused.
            let name = try document.serverName(for: server.id, under: serversKey)

            guard let name else {
                if server.isBundled, format(of: server.origin) != .toml {
                    throw ToggleError.bundledServerNotPortable(server.name)
                }
                // Nothing in the config file answers to this name. Either it is
                // genuinely not here, or it is here by another route — a plugin —
                // and that route is not one Kytto writes to. Saying so beats a
                // click that appears to work and changes nothing.
                if server.isBundled || server.enabledIn[clientID] ?? .absent != .absent {
                    throw ToggleError.bundledServerNotToggleable(server.name)
                }
                // Switching off something that is not there is already true.
                guard enabled else { return document.sourceText }
                return try document.settingServer(
                    server.name,
                    under: serversKey,
                    to: tomlBlock(for: server, name: server.name, serversKey: serversKey, enabled: true)
                )
            }

            // Absent means on, so a server with no flag is already in the state
            // an "enable" is asking for. Writing `enabled = true` anyway would
            // touch someone's config to change nothing, and cost them a backup
            // slot for the privilege.
            let current = document.table(at: [serversKey, name])?.value(flagKey)?.boolValue ?? true
            guard current != enabled else { return document.sourceText }

            return try document.settingServerFlag(
                enabled, key: flagKey, forServer: name, under: serversKey
            )
        }
        ledger.record(receipt.digest, for: url)

        return ToggleResult(
            clientID: clientID,
            serverName: server.name,
            enabled: enabled,
            requiresRestart: receipt.didWrite,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay,
            // Nothing is ever removed here, so nothing is ever parked.
            wasParked: false
        )
    }

    // MARK: - Antigravity, Devin Desktop, OpenCode: a boolean on the JSON entry

    /// The JSON sibling of `setViaInlineFlag`. The definition never leaves the
    /// file, so nothing is parked; only a first arrival writes a definition.
    private func setViaJSONFlag(
        _ enabled: Bool,
        server: Server,
        clientID: ClientID,
        configFile: PlatformPath,
        serversKey: String,
        flag: (key: String, onValue: Bool)
    ) throws -> ToggleResult {
        let url = resolver.resolveServerMap(configFile, for: clientID)
        let display = resolver.displayServerMap(configFile, for: clientID)
        let receipt = try writer.edit(
            url: url,
            clientID: clientID,
            pathDisplay: display,
            expecting: ledger.digest(for: url)
        ) { document in
            guard let key = try document.serverKey(for: server.id, under: serversKey) else {
                if server.enabledIn[clientID] ?? .absent != .absent {
                    throw ToggleError.bundledServerNotToggleable(server.name)
                }
                guard enabled else { return document.sourceText }
                let arriving = try arrivingDefinition(for: server, in: clientID)
                return try document.settingMember(
                    server.name,
                    at: [serversKey],
                    to: literal(for: arriving, server: server, serversKey: serversKey, in: document, clientID: clientID)
                )
            }

            // Absent means on. A server already in the state asked for is not
            // written at all, so a no-op click costs no backup slot.
            let path = [serversKey, key]
            guard let entry = document.value(at: path), let members = entry.members else {
                throw JSONEditError.notAnObject(path)
            }
            let current = try JSONDocument.soleMember(flag.key, in: members, at: path)?.value.boolValue
            let isOn = current.map { $0 == flag.onValue } ?? true
            guard isOn != enabled else { return document.sourceText }

            // `"disabled": true` goes away when the server comes back on, which
            // is how the entry looked before Kytto touched it; an `enabled` key
            // states its value either way, as Codex's does.
            if enabled, !flag.onValue {
                return try document.removingMember(flag.key, at: path)
            }
            return try document.settingMember(flag.key, at: path, to: JSONText.bool(enabled == flag.onValue))
        }
        ledger.record(receipt.digest, for: url)

        return ToggleResult(
            clientID: clientID,
            serverName: server.name,
            enabled: enabled,
            requiresRestart: receipt.didWrite,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay,
            wasParked: false
        )
    }

    // MARK: - Claude Code: a deny list in another file

    /// Switching a server *on* here is two edits, not one.
    ///
    /// The deny list can only ever say something about a server the config file
    /// already lists — taking a name out of it permits a definition that has to
    /// exist for the permission to mean anything. So a cell that was empty needs
    /// the definition to arrive in the config file first. Without that step the
    /// click edits a list that never mentioned the server, writes nothing, and
    /// reports a success it did not have: the cell the user pressed comes back
    /// empty and the restart bar asks them to restart for no change at all.
    ///
    /// The definition lands first, then the deny list changes. Both files are a
    /// single Kytto transaction: if the second edit fails, the first is restored
    /// byte for byte rather than leaving a click that reported failure having
    /// silently changed the client's effective configuration.
    private func setViaDenyList(
        _ enabled: Bool,
        server: Server,
        clientID: ClientID,
        configFile: PlatformPath,
        serversKey: String,
        denyFile: PlatformPath,
        key: String,
        nameField: String
    ) throws -> ToggleResult {
        try MutationCoordinator.sync {
            // The model's own view decides whether this is an arrival, so a server
            // the client already lists takes exactly the path it took before —
            // including bundled ones, which must not start failing here on the
            // strength of a flag that describes where the row was first found.
            let isArriving = enabled && (server.enabledIn[clientID] ?? .absent) == .absent
            let arrivalSnapshot = isArriving
                ? try snapshotForArrival(configFile: configFile, clientID: clientID)
                : nil
            var arrival: WriteReceipt?

            do {
                arrival = isArriving
                    ? try addDefinition(
                        for: server,
                        clientID: clientID,
                        configFile: configFile,
                        serversKey: serversKey
                    )
                    : nil

                let url = denyFile.resolve(home: home)
                let receipt = try writer.edit(
                    url: url,
                    clientID: clientID,
                    pathDisplay: denyFile.displayString(),
                    expecting: ledger.digest(for: url)
                ) { document in
                    if enabled {
                        return try document.removingElements(fromArrayAt: [key]) { element in
                            let name = element[nameField]?.stringValue ?? element.stringValue
                            return name.map { Server.identity(for: $0) == server.id } ?? false
                        }
                    } else {
                        // Already denied: leave the file alone rather than adding a
                        // duplicate entry the client would have to tolerate.
                        let alreadyDenied = document.root[key]?.elements?.contains { element in
                            let name = element[nameField]?.stringValue ?? element.stringValue
                            return name.map { Server.identity(for: $0) == server.id } ?? false
                        } ?? false
                        if alreadyDenied { return document.sourceText }
                        // The spelling the config file uses, not the row's: the row
                        // takes its name from whichever client listed it first, and
                        // the client matches its deny list against its own key.
                        let spelling = (try? JSONDocument.parse(contentsOf: resolver.resolveServerMap(configFile, for: clientID)))
                            .flatMap { try? $0.serverKey(for: server.id, under: serversKey) } ?? server.name
                        return try document.appendingElement(
                            document.objectLiteral(
                                [(nameField, JSONText.string(spelling))],
                                matchingStyleOfArrayAt: [key]
                            ),
                            toArrayAt: [key]
                        )
                    }
                }
                ledger.record(receipt.digest, for: url)

                // The definition is in the file now, so Kytto's copy of it is spent.
                if isArriving { try? parkStore.unpark(clientID: clientID, serverName: server.name) }

                return ToggleResult(
                    clientID: clientID,
                    serverName: server.name,
                    enabled: enabled,
                    requiresRestart: (arrival?.didWrite ?? false) || receipt.didWrite,
                    // The file worth naming is the one the definition landed in, when
                    // this click is what put it there. Otherwise only the list moved.
                    backupID: arrival?.backupID ?? receipt.backupID,
                    pathDisplay: arrival?.pathDisplay ?? receipt.pathDisplay,
                    wasParked: false
                )
            } catch {
                guard let arrival, arrival.didWrite, let arrivalSnapshot else { throw error }
                try rollBackArrival(arrival, to: arrivalSnapshot, after: error)
            }
        }
    }

    private func snapshotForArrival(configFile: PlatformPath, clientID: ClientID) throws -> FileSnapshot {
        let url = resolver.resolveServerMap(configFile, for: clientID)
        let pathDisplay = resolver.displayServerMap(configFile, for: clientID)
        let exists = FileManager.default.fileExists(atPath: url.path)
        let digest = ConfigWriter.digest(of: url)
        guard digest == ledger.digest(for: url) else {
            throw ConfigWriteError.changedOnDisk(pathDisplay: pathDisplay)
        }
        guard exists else {
            return FileSnapshot(url: url, pathDisplay: pathDisplay, data: nil, digest: nil)
        }
        do {
            return FileSnapshot(
                url: url,
                pathDisplay: pathDisplay,
                data: try Data(contentsOf: url),
                digest: digest
            )
        } catch {
            throw ConfigWriteError.unreadable(pathDisplay: pathDisplay, reason: error.localizedDescription)
        }
    }

    private func rollBackArrival(
        _ receipt: WriteReceipt,
        to snapshot: FileSnapshot,
        after originalError: Error
    ) throws -> Never {
        do {
            guard ConfigWriter.digest(of: snapshot.url) == receipt.digest else {
                throw ConfigWriteError.changedOnDisk(pathDisplay: snapshot.pathDisplay)
            }
            if let data = snapshot.data {
                try AtomicWriter.write(data, to: snapshot.url, expecting: .digest(receipt.digest))
            } else if FileManager.default.fileExists(atPath: snapshot.url.path) {
                try FileManager.default.removeItem(at: snapshot.url)
            }
            ledger.record(snapshot.digest, for: snapshot.url)
        } catch {
            throw ToggleError.rollbackFailed(
                original: originalError.localizedDescription,
                rollback: error.localizedDescription
            )
        }
        throw originalError
    }

    /// Puts a definition into a client's server map, leaving one that is already
    /// there exactly as the user has it.
    private func addDefinition(
        for server: Server,
        clientID: ClientID,
        configFile: PlatformPath,
        serversKey: String
    ) throws -> WriteReceipt {
        let arriving = try arrivingDefinition(for: server, in: clientID)
        let url = resolver.resolveServerMap(configFile, for: clientID)
        let display = resolver.displayServerMap(configFile, for: clientID)

        let receipt = try writer.edit(
            url: url,
            clientID: clientID,
            pathDisplay: display,
            expecting: ledger.digest(for: url)
        ) { document in
            // Discovery said this client does not have it. If the file disagrees,
            // the file wins: overwriting a definition someone may have edited by
            // hand is not what a click on an empty cell asked for.
            guard try document.serverKey(for: server.id, under: serversKey) == nil else {
                return document.sourceText
            }
            return try document.settingMember(
                server.name,
                at: [serversKey],
                to: literal(for: arriving, server: server, serversKey: serversKey, in: document, clientID: clientID)
            )
        }
        ledger.record(receipt.digest, for: url)
        return receipt
    }

    // MARK: - Claude Desktop extensions: a boolean of their own

    private func setExtensionEnabled(
        _ enabled: Bool,
        server: Server,
        bundleID: String,
        descriptor: ClientDescriptor
    ) throws -> ToggleResult {
        guard let (settingsDirectory, flagKey) = descriptor.sources.compactMap({ source -> (PlatformPath, String)? in
            guard case .extensionBundles(_, let settings, let flagKey) = source else { return nil }
            return (settings, flagKey)
        }).first else {
            throw ToggleError.noSourceForClient(.claudeDesktop)
        }

        let url = settingsDirectory.resolve(home: home).appending(path: "\(bundleID).json")
        let display = "\(settingsDirectory.displayString())/\(bundleID).json"

        let receipt = try writer.edit(
            url: url,
            clientID: .claudeDesktop,
            pathDisplay: display,
            expecting: ledger.digest(for: url)
        ) { document in
            // The file carries other keys — `userConfig` among them — which must
            // survive being toggled.
            try document.settingMember(flagKey, at: [], to: JSONText.bool(enabled))
        }
        ledger.record(receipt.digest, for: url)

        return ToggleResult(
            clientID: .claudeDesktop,
            serverName: server.name,
            enabled: enabled,
            requiresRestart: receipt.didWrite,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay,
            wasParked: false
        )
    }

    // MARK: - Everyone else: presence is the only lever

    private func setViaPresence(
        _ enabled: Bool,
        server: Server,
        clientID: ClientID,
        configFile: PlatformPath,
        serversKey: String
    ) throws -> ToggleResult {
        let url = resolver.resolveServerMap(configFile, for: clientID)
        let display = resolver.displayServerMap(configFile, for: clientID)

        // Decided before the file is touched, so a definition Kytto cannot build
        // fails without anything having been opened.
        let arriving = enabled ? try arrivingDefinition(for: server, in: clientID) : nil

        let expecting = ledger.digest(for: url)
        let previousPark = parkStore.parked(clientID: clientID, serverName: server.name)
        var parked = false
        var restored = false
        let receipt: WriteReceipt
        do {
            receipt = try writer.edit(
                url: url,
                clientID: clientID,
                pathDisplay: display,
                expecting: expecting
            ) { document in
                // The spelling this file uses, which need not be the row's: the
                // matrix merges `GitHub` and `github` into one row.
                let key = try document.serverKey(for: server.id, under: serversKey)

                if let arriving {
                    // Already defined here — put back by hand, or edited in place.
                    // The file wins; restoring held bytes over it would undo
                    // whatever is there now.
                    guard key == nil else { return document.sourceText }
                    restored = true
                    // Back under the spelling it was held under, which is this
                    // file's own — not the merged row's, which may come from
                    // another client.
                    return try document.settingMember(
                        previousPark?.serverName ?? server.name,
                        at: [serversKey],
                        to: literal(for: arriving, server: server, serversKey: serversKey, in: document, clientID: clientID)
                    )
                }
                guard let key, let member = document.value(at: [serversKey])?.member(key) else {
                    return document.sourceText
                }
                // Park the exact bytes before they go, so switching back is a
                // restoration rather than a reconstruction, and a crash cannot
                // leave the definition removed without a restorable copy.
                try parkStore.park(
                    clientID: clientID,
                    serverName: key,
                    sourceText: document.slice(member.value.span)
                )
                parked = true
                return try document.removingMember(key, at: [serversKey])
            }
        } catch {
            // The park happened inside the transaction. If the write was then
            // refused or failed, the definition is still in the file, and a
            // park left behind would draw a server that is on as off. Put the
            // store back the way it was — but only while the file still says
            // what it said, so a park can never outlive its definition.
            if parked, ConfigWriter.digest(of: url) == expecting {
                if let previousPark {
                    try? parkStore.park(
                        clientID: previousPark.clientID,
                        serverName: previousPark.serverName,
                        sourceText: previousPark.sourceText
                    )
                } else {
                    try? parkStore.unpark(clientID: clientID, serverName: server.name)
                }
            }
            throw error
        }
        ledger.record(receipt.digest, for: url)

        // Kytto's copy is spent once its bytes are back in the file. If the file
        // already had a definition, nothing was restored and the copy stays.
        if restored {
            try? parkStore.unpark(clientID: clientID, serverName: server.name)
        }

        return ToggleResult(
            clientID: clientID,
            serverName: server.name,
            enabled: enabled,
            requiresRestart: receipt.didWrite,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay,
            wasParked: parked
        )
    }
}
