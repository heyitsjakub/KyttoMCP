import Foundation

public struct GatewayMigrationPreview: Equatable, Sendable {
    public let routeID: String
    public let serverID: String
    public let serverName: String
    public let clientID: ClientID
    public let pathDisplay: String
    public let formatDisplay: String
    /// A structurally faithful view with all environment values redacted.
    public let directDefinitionPreview: String
    /// The exact replacement definition that will be written.
    public let gatewayDefinitionPreview: String
    public let environmentKeys: [String]
}

public struct GatewayMigrationResult: Sendable {
    public let route: GatewayRoute
    public let backupID: String?
    public let pathDisplay: String
    /// Environment variables a rebuilt Direct definition could not be given,
    /// because their stored values were gone. Empty for an ordinary restore.
    public let missingEnvironmentKeys: [String]

    public init(route: GatewayRoute, backupID: String?, pathDisplay: String, missingEnvironmentKeys: [String] = []) {
        self.route = route
        self.backupID = backupID
        self.pathDisplay = pathDisplay
        self.missingEnvironmentKeys = missingEnvironmentKeys
    }
}

/// Something that stops a Gateway definition in a client's config from
/// starting, found by reading the config and the route store only. Nothing
/// here opens the credential store, so it is safe to compute on every reload.
public struct GatewayRouteDiagnosis: Equatable, Sendable {
    public enum Problem: Equatable, Sendable {
        /// The config names a helper that is not there — Kytto was moved,
        /// renamed or deleted. `repairable` when this copy's helper can take over.
        case helperMissing(path: String, repairable: Bool)
        /// The config points at a route Kytto has no record of.
        case unknownRoute
    }

    public let serverID: String
    public let serverName: String
    public let clientID: ClientID
    public let routeID: String
    public let problem: Problem
}

public enum GatewayMigrationError: Error, LocalizedError, Equatable {
    case helperUnavailable(String)
    case unsupportedTransport
    case bundledServer
    case serverNotEnabled
    case routeAlreadyExists
    case definitionMissing
    case invalidDefinition(String)
    case routeMismatch
    case orphanedRouteInBackup(String)
    /// The app is running from a place that will not exist later — a disk
    /// image, or the random folder macOS translocates a quarantined app to.
    case helperLocationUnstable(String)

    public var errorDescription: String? {
        switch self {
        case .helperUnavailable(let path):
            "The bundled gateway helper is unavailable at \(path). Reinstall or rebuild Kytto."
        case .unsupportedTransport:
            "Gateway mode currently supports stdio servers only."
        case .bundledServer:
            "Installed extension and plugin servers cannot be migrated."
        case .serverNotEnabled:
            "Enable this server in the selected client before turning on Gateway mode."
        case .routeAlreadyExists:
            "This server already uses Gateway mode in that client."
        case .definitionMissing:
            "The selected client no longer contains this server definition."
        case .invalidDefinition(let reason):
            "This server cannot use Gateway mode: \(reason)"
        case .routeMismatch:
            "The client configuration no longer points at this gateway route. Kytto did not overwrite it."
        case .orphanedRouteInBackup(let id):
            "That backup points at gateway route \(id), but its credentials no longer exist. Restore a Direct backup instead."
        case .helperLocationUnstable(let path):
            "Kytto is running from \(path), which will not exist after a restart or once the disk image is ejected. Move Kytto to the Applications folder, open it from there, then turn on Gateway mode."
        }
    }
}

/// Opt-in Direct ↔ Gateway migration through the same guarded write path as all
/// other config changes (§3.3, §6).
public struct GatewayMigrationService: Sendable {
    private struct DirectDefinition {
        let serverName: String
        let sourceText: String
        let command: String
        let arguments: [String]
        let environment: [EnvEntry]
        let replacement: String
    }

    private let home: URL
    private let helperURL: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let ledger: DigestLedger
    private let routes: GatewayRouteStore
    private let secrets: any SecretStoring
    private let descriptors: [ClientDescriptor]

    public init(
        home: URL,
        helperURL: URL,
        writer: ConfigWriter,
        ledger: DigestLedger,
        routes: GatewayRouteStore,
        secrets: any SecretStoring,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        self.home = home
        self.helperURL = helperURL
        self.writer = writer
        self.ledger = ledger
        self.routes = routes
        self.secrets = secrets
        self.descriptors = descriptors
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
    }

    public func preview(
        server: Server,
        clientID: ClientID,
        routeID: String = UUID().uuidString.lowercased()
    ) throws -> GatewayMigrationPreview {
        let target = try target(for: server, clientID: clientID, requireNoRoute: true)
        guard UUID(uuidString: routeID) != nil else { throw GatewayRouteError.invalidRouteID(routeID) }
        let direct = try directDefinition(target: target, serverID: server.id, routeID: routeID)

        return GatewayMigrationPreview(
            routeID: routeID.lowercased(),
            serverID: server.id,
            serverName: direct.serverName,
            clientID: clientID,
            pathDisplay: target.pathDisplay,
            formatDisplay: target.source.format.displayName,
            directDefinitionPreview: redactedPreview(of: direct, format: target.source.format),
            gatewayDefinitionPreview: direct.replacement,
            environmentKeys: direct.environment.map(\.key).sorted()
        )
    }

    public func enable(
        server: Server,
        clientID: ClientID,
        routeID: String
    ) throws -> GatewayMigrationResult {
        let target = try target(for: server, clientID: clientID, requireNoRoute: true)
        guard UUID(uuidString: routeID) != nil else { throw GatewayRouteError.invalidRouteID(routeID) }
        let normalizedRouteID = routeID.lowercased()
        guard try !routes.all().contains(where: { $0.id == normalizedRouteID }) else {
            throw GatewayMigrationError.routeAlreadyExists
        }
        let direct = try directDefinition(target: target, serverID: server.id, routeID: normalizedRouteID)
        let definitionSecretID = GatewayRoute.directDefinitionSecretID(routeID: normalizedRouteID)
        let references = direct.environment.map {
            GatewayEnvironmentReference(
                key: $0.key,
                secretID: GatewayRoute.environmentSecretID(routeID: normalizedRouteID, key: $0.key)
            )
        }
        let route = GatewayRoute(
            id: normalizedRouteID,
            serverID: server.id,
            serverName: direct.serverName,
            clientID: clientID,
            gatewayCommand: helperURL.path,
            command: direct.command,
            arguments: direct.arguments,
            environment: references,
            directDefinitionSecretID: definitionSecretID
        )

        var storedSecretIDs: [String] = []
        do {
            try secrets.store(direct.sourceText, for: definitionSecretID)
            storedSecretIDs.append(definitionSecretID)
            for (entry, reference) in zip(direct.environment, references) {
                try secrets.store(entry.value ?? "", for: reference.secretID)
                storedSecretIDs.append(reference.secretID)
            }
            try routes.insert(route)

            let receipt = try writeReplacement(
                direct.replacement,
                target: target,
                routeID: nil,
                serverName: direct.serverName
            )
            ledger.record(receipt.digest, for: target.url)
            return GatewayMigrationResult(route: route, backupID: receipt.backupID, pathDisplay: receipt.pathDisplay)
        } catch {
            try? routes.remove(id: normalizedRouteID)
            for secretID in storedSecretIDs { try? secrets.delete(for: secretID) }
            throw error
        }
    }

    public func restore(routeID: String) throws -> GatewayMigrationResult {
        let route = try routes.route(id: routeID)
        let target = try target(clientID: route.clientID)
        // The stored original is the faithful way back. When it is gone — a
        // cleared or replaced keychain — the route still knows the command and
        // arguments, so Direct mode is rebuilt from those instead of leaving a
        // server that can be neither started nor edited. Values that are gone
        // too are named, so the user knows what to enter again.
        var missingKeys: [String] = []
        let source: String
        if let original = try? secrets.value(for: route.directDefinitionSecretID) {
            source = original
        } else {
            var environment: [EnvEntry] = []
            for reference in route.environment {
                if let value = try? secrets.value(for: reference.secretID) {
                    environment.append(EnvEntry(key: reference.key, value: value))
                } else {
                    missingKeys.append(reference.key)
                }
            }
            source = try definition(
                target: target,
                serverName: route.serverName,
                command: route.command,
                arguments: route.arguments,
                environment: environment
            )
        }
        let receipt = try writeReplacement(source, target: target, routeID: route.id, serverName: route.serverName)
        ledger.record(receipt.digest, for: target.url)

        // The direct config is already safely restored. Cleanup is deliberately
        // best-effort so a Keychain deletion problem cannot turn that success
        // into a false failure or tempt the caller to repeat the config write.
        try? routes.remove(id: route.id)
        try? secrets.delete(for: route.directDefinitionSecretID)
        for reference in route.environment { try? secrets.delete(for: reference.secretID) }

        return GatewayMigrationResult(
            route: route,
            backupID: receipt.backupID,
            pathDisplay: receipt.pathDisplay,
            missingEnvironmentKeys: missingKeys.sorted()
        )
    }

    // MARK: - Keeping routes working

    /// What stops each Gateway definition from starting, config by config.
    ///
    /// A client only ever reports "server failed". The causes Kytto can see
    /// without starting anything are the two here: the helper the config names
    /// is gone (the app was moved or deleted), or the route record is.
    public func diagnose() -> [GatewayRouteDiagnosis] {
        guard let known = try? routes.all() else { return [] }
        let byID = Dictionary(known.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let currentHelperUsable = FileManager.default.isExecutableFile(atPath: helperURL.path)
            && !Self.isUnstable(helperURL)
        var result: [GatewayRouteDiagnosis] = []

        for descriptor in descriptors {
            guard let source = descriptor.editableServerMap, descriptor.serverSchema.shape == .mcpServers else { continue }
            let url = resolver.resolveServerMap(source.file, for: descriptor.id)
            guard let text = try? String(contentsOf: url, encoding: .utf8),
                  let entries = try? gatewayEntries(in: text, clientID: descriptor.id)
            else { continue }

            for entry in entries {
                guard let route = byID[entry.routeID], route.clientID == descriptor.id else {
                    result.append(GatewayRouteDiagnosis(
                        serverID: Server.identity(for: entry.serverName),
                        serverName: entry.serverName,
                        clientID: descriptor.id,
                        routeID: entry.routeID,
                        problem: .unknownRoute
                    ))
                    continue
                }
                guard !FileManager.default.isExecutableFile(atPath: entry.command) else { continue }
                result.append(GatewayRouteDiagnosis(
                    serverID: route.serverID,
                    serverName: entry.serverName,
                    clientID: descriptor.id,
                    routeID: route.id,
                    problem: .helperMissing(path: entry.command, repairable: currentHelperUsable)
                ))
            }
        }
        return result
    }

    /// Points a route's definition at this copy of the helper.
    ///
    /// For an app that was moved after Gateway mode was turned on: the config
    /// still names the old location. Only the command path changes; the route,
    /// its credentials and its tool mask stay as they are.
    public func repair(routeID: String) throws -> GatewayMigrationResult {
        let route = try routes.route(id: routeID)
        guard FileManager.default.isExecutableFile(atPath: helperURL.path) else {
            throw GatewayMigrationError.helperUnavailable(helperURL.path)
        }
        if Self.isUnstable(helperURL) {
            throw GatewayMigrationError.helperLocationUnstable(helperURL.deletingLastPathComponent().path)
        }
        let target = try target(clientID: route.clientID)
        let replacement = try gatewayDefinition(target: target, serverName: route.serverName, routeID: route.id)
        let receipt = try writeReplacement(replacement, target: target, routeID: route.id, serverName: route.serverName)
        ledger.record(receipt.digest, for: target.url)
        let updated = (try? routes.update(id: route.id, gatewayCommand: helperURL.path)) ?? route
        return GatewayMigrationResult(route: updated, backupID: receipt.backupID, pathDisplay: receipt.pathDisplay)
    }

    /// Carries a rotated secret into every route that was holding the old one.
    ///
    /// A route keeps its own copy of each value, and the original definition it
    /// would restore. Rotating in the configs alone would leave a Gateway server
    /// running on the revoked token, and bring it back on Restore Direct.
    /// - Returns: the routes that changed, whose clients need a restart.
    @discardableResult
    public func rotateEnvironmentValue(key: String, from oldValue: String, to newValue: String) -> [GatewayRoute] {
        guard !oldValue.isEmpty, oldValue != newValue, let all = try? routes.all() else { return [] }
        var changed: [GatewayRoute] = []
        for route in all {
            var touched = false
            for reference in route.environment where reference.key == key {
                guard (try? secrets.value(for: reference.secretID)) == oldValue,
                      (try? secrets.store(newValue, for: reference.secretID)) != nil
                else { continue }
                touched = true
            }
            guard touched else { continue }
            changed.append(route)
            // Best effort: a definition that cannot be rewritten is left alone,
            // and Restore Direct then brings back the old value as it always did.
            guard let format = descriptors.first(where: { $0.id == route.clientID })?.editableServerMap?.format,
                  let original = try? secrets.value(for: route.directDefinitionSecretID),
                  let rewritten = Self.replacingEnvironmentValue(key, from: oldValue, to: newValue, in: original, format: format),
                  rewritten != original
            else { continue }
            try? secrets.store(rewritten, for: route.directDefinitionSecretID)
        }
        return changed
    }

    /// The same definition text with one environment value replaced, or nil if
    /// it does not parse. Only a value that equals `oldValue` is touched.
    static func replacingEnvironmentValue(
        _ key: String,
        from oldValue: String,
        to newValue: String,
        in definition: String,
        format: ConfigFormat
    ) -> String? {
        switch format {
        case .json:
            guard let document = try? JSONDocument.parse(definition) else { return nil }
            let edits = (document.root["env"]?.members ?? [])
                .filter { $0.key == key && $0.value.stringValue == oldValue }
                .map { (span: $0.value.span, replacement: JSONText.string(newValue)) }
            return edits.isEmpty ? definition : document.replacing(edits)
        case .toml:
            guard let document = try? TOMLDocument.parse(definition) else { return nil }
            var spans: [TOMLSpan] = []
            for table in document.tables {
                if table.path.last == "env" {
                    spans += table.pairs.filter { $0.key == [key] && $0.value.stringValue == oldValue }.map(\.value.span)
                }
                for pair in table.pairs where pair.key.first == "env" {
                    if pair.key == ["env", key], pair.value.stringValue == oldValue { spans.append(pair.value.span) }
                    spans += (pair.value.inlinePairs ?? [])
                        .filter { $0.key == [key] && $0.value.stringValue == oldValue }
                        .map(\.value.span)
                }
            }
            return spans.isEmpty ? definition : document.replacing(spans.map { ($0, TOMLText.string(newValue)) })
        }
    }

    private static func isUnstable(_ helper: URL) -> Bool {
        helper.path.contains("/AppTranslocation/") || helper.path.hasPrefix("/Volumes/")
    }

    /// The helper definition for a server already in Gateway mode, rendered the
    /// way `enable` renders it, at this copy's helper path.
    private func gatewayDefinition(target: Target, serverName: String, routeID: String) throws -> String {
        switch target.source.format {
        case .json:
            return try definition(
                target: target, serverName: serverName,
                command: helperURL.path, arguments: ["--route", routeID], environment: []
            )
        case .toml:
            let document = try TOMLDocument.parse(target.sourceText)
            guard let name = document.serverNames(under: target.source.serversKey)
                .first(where: { Server.identity(for: $0) == Server.identity(for: serverName) }),
                  let table = document.table(at: [target.source.serversKey, name])
            else { throw GatewayMigrationError.definitionMissing }
            // Settings that tell Codex how to treat the entry stay, as in `enable`.
            let clientSide = table.pairs.filter { Self.codexClientKeys.contains($0.key.first ?? "") }
            return ([TOMLBuilder.serverBlock(
                name: name, under: target.source.serversKey,
                command: helperURL.path, args: ["--route", routeID], url: nil, env: [], enabled: nil
            )] + clientSide.map { document.slice($0.span) }).joined(separator: "\n")
        }
    }

    /// A stdio definition rendered from the model, in the target file's format.
    private func definition(
        target: Target,
        serverName: String,
        command: String,
        arguments: [String],
        environment: [EnvEntry]
    ) throws -> String {
        switch target.source.format {
        case .json:
            let document = try JSONDocument.parse(target.sourceText)
            guard let map = document.value(at: [target.source.serversKey]),
                  let member = map.members?.first(where: {
                      Server.identity(for: $0.key) == Server.identity(for: serverName)
                  })
            else { throw GatewayMigrationError.definitionMissing }
            return ServerDraft(name: member.key, command: command, args: arguments, env: environment)
                .definition(
                    baseIndent: document.lineIndent(at: member.value.span.start),
                    unit: IndentStyle.detect(in: document.sourceText).text
                )
        case .toml:
            let document = try TOMLDocument.parse(target.sourceText)
            let name = document.serverNames(under: target.source.serversKey)
                .first(where: { Server.identity(for: $0) == Server.identity(for: serverName) }) ?? serverName
            let clientSide = document.table(at: [target.source.serversKey, name])?
                .pairs.filter { Self.codexClientKeys.contains($0.key.first ?? "") } ?? []
            return ([TOMLBuilder.serverBlock(
                name: name, under: target.source.serversKey,
                command: command, args: arguments, url: nil, env: environment, enabled: nil
            )] + clientSide.map { document.slice($0.span) }).joined(separator: "\n")
        }
    }

    /// Every Gateway definition in a config: its server, route and the helper
    /// path it names.
    private func gatewayEntries(
        in text: String,
        clientID: ClientID
    ) throws -> [(serverName: String, routeID: String, command: String)] {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw AuthoringError.noSourceForClient(clientID) }

        switch source.format {
        case .json:
            let document = try JSONDocument.parse(text)
            return document.value(at: [source.serversKey])?.members?.compactMap { member in
                guard let command = member.value["command"]?.stringValue,
                      let id = routeID(command: command, arguments: member.value["args"]?.elements?.compactMap(\.stringValue))
                else { return nil }
                return (member.key, id, command)
            } ?? []
        case .toml:
            let document = try TOMLDocument.parse(text)
            return document.serverNames(under: source.serversKey).compactMap { name in
                let table = document.table(at: [source.serversKey, name])
                guard let command = table?.value("command")?.stringValue,
                      let id = routeID(command: command, arguments: table?.value("args")?.elements?.compactMap(\.stringValue))
                else { return nil }
                return (name, id, command)
            }
        }
    }

    /// Refuses a backup that would revive a helper route after its credentials
    /// were deliberately removed by Restore Direct.
    public func validateBackupRestore(_ text: String, clientID: ClientID) throws {
        let known = Set(try routes.all().map(\.id))
        for id in try routeIDs(in: text, clientID: clientID) where !known.contains(id) {
            throw GatewayMigrationError.orphanedRouteInBackup(id)
        }
    }

    /// A Direct backup can intentionally replace an active gateway definition.
    /// Once that write succeeds, remove route material the config no longer uses.
    public func reconcileAfterBackupRestore(clientID: ClientID) throws {
        let target = try target(clientID: clientID)
        let configured = Set(try routeIDs(in: target.sourceText, clientID: clientID))
        for route in try routes.all() where route.clientID == clientID && !configured.contains(route.id) {
            try? routes.remove(id: route.id)
            try? secrets.delete(for: route.directDefinitionSecretID)
            for reference in route.environment { try? secrets.delete(for: reference.secretID) }
        }
    }

    // MARK: - Targets and parsing

    private typealias Source = (
        file: PlatformPath,
        format: ConfigFormat,
        serversKey: String,
        enablement: EnablementStrategy
    )

    private struct Target {
        let clientID: ClientID
        let source: Source
        let url: URL
        let pathDisplay: String
        let sourceText: String
    }

    private func target(
        for server: Server,
        clientID: ClientID,
        requireNoRoute: Bool
    ) throws -> Target {
        guard FileManager.default.isExecutableFile(atPath: helperURL.path) else {
            throw GatewayMigrationError.helperUnavailable(helperURL.path)
        }
        // The helper's path is written into the client's config, so it has to
        // outlive this launch.
        if helperURL.path.contains("/AppTranslocation/") || helperURL.path.hasPrefix("/Volumes/") {
            throw GatewayMigrationError.helperLocationUnstable(helperURL.deletingLastPathComponent().path)
        }
        guard server.transport == .stdio else { throw GatewayMigrationError.unsupportedTransport }
        guard !server.isBundled else { throw GatewayMigrationError.bundledServer }
        guard server.enabledIn[clientID] == .enabled else { throw GatewayMigrationError.serverNotEnabled }
        if requireNoRoute, try routes.route(serverID: server.id, clientID: clientID) != nil {
            throw GatewayMigrationError.routeAlreadyExists
        }
        return try target(clientID: clientID)
    }

    private func target(clientID: ClientID) throws -> Target {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw AuthoringError.noSourceForClient(clientID) }
        // The helper is written as `command` + `args`, which OpenCode does not
        // read; its argv-array entry is not modelled here yet.
        guard descriptor.serverSchema.shape == .mcpServers else {
            throw GatewayMigrationError.invalidDefinition("Gateway mode does not support \(descriptor.displayName) yet")
        }

        let url = resolver.resolveServerMap(source.file, for: clientID)
        let display = resolver.displayServerMap(source.file, for: clientID)
        guard ConfigWriter.digest(of: url) == ledger.digest(for: url) else {
            throw ConfigWriteError.changedOnDisk(pathDisplay: display)
        }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            throw ConfigWriteError.unreadable(pathDisplay: display, reason: "not valid UTF-8")
        }
        return Target(clientID: clientID, source: source, url: url, pathDisplay: display, sourceText: text)
    }

    private func directDefinition(target: Target, serverID: String, routeID: String) throws -> DirectDefinition {
        switch target.source.format {
        case .json:
            let document = try JSONDocument.parse(target.sourceText)
            guard let map = document.value(at: [target.source.serversKey]),
                  let member = map.members?.first(where: { Server.identity(for: $0.key) == serverID })
            else { throw GatewayMigrationError.definitionMissing }
            let node = member.value
            let extra = node.keys.filter { !["command", "args", "env", "type"].contains($0) }
            if !extra.isEmpty { throw Self.unmodelled(extra) }
            if let type = node["type"], type.stringValue != "stdio" {
                throw GatewayMigrationError.invalidDefinition("it is not a stdio server")
            }
            guard let command = node["command"]?.stringValue, !command.isEmpty else {
                throw GatewayMigrationError.invalidDefinition("its command is missing or is not a string")
            }
            let arguments = node["args"]?.elements?.compactMap(\.stringValue) ?? []
            if let elements = node["args"]?.elements, arguments.count != elements.count {
                throw GatewayMigrationError.invalidDefinition("one of its arguments is not a string")
            }
            let environment = try jsonEnvironment(node["env"])
            try Self.refusePlaceholders(command: command, arguments: arguments, environment: environment)
            let unit = IndentStyle.detect(in: document.sourceText).text
            let replacement = ServerDraft(
                name: member.key,
                command: helperURL.path,
                args: ["--route", routeID]
            ).definition(baseIndent: document.lineIndent(at: member.value.span.start), unit: unit)
            return DirectDefinition(
                serverName: member.key,
                sourceText: document.slice(member.value.span),
                command: command,
                arguments: arguments,
                environment: environment,
                replacement: replacement
            )

        case .toml:
            let document = try TOMLDocument.parse(target.sourceText)
            guard let name = document.serverNames(under: target.source.serversKey)
                .first(where: { Server.identity(for: $0) == serverID }),
                  let table = document.table(at: [target.source.serversKey, name]),
                  let sourceText = document.serverDefinitionText(name, under: target.source.serversKey)
            else { throw GatewayMigrationError.definitionMissing }
            // Restore puts this text back as one block, so it must be this
            // server's tables and nothing else — not a neighbour sitting between
            // two of them, which would then be written twice.
            let ownPath = [target.source.serversKey, name]
            guard let slice = try? TOMLDocument.parse(sourceText),
                  slice.tables.allSatisfy({ $0.path.starts(with: ownPath) }) else {
                throw GatewayMigrationError.invalidDefinition("its tables are not next to each other in config.toml; move them together first")
            }
            let subtables = slice.tables.map(\.path).filter { $0.count > ownPath.count }
            if let other = subtables.first(where: { $0 != ownPath + ["env"] }) {
                throw Self.unmodelled([other[ownPath.count]])
            }
            // Launch settings move into the route; settings that tell Codex how
            // to treat the entry (timeouts, which tools to show) apply to the
            // helper just the same and stay in the block, spelled as they were.
            let clientSide = table.pairs.filter { Self.codexClientKeys.contains($0.key.first ?? "") }
            let extra = table.pairs.compactMap(\.key.first)
                .filter { !["command", "args", "env"].contains($0) && !Self.codexClientKeys.contains($0) }
            if !extra.isEmpty { throw Self.unmodelled(Array(Set(extra)).sorted()) }
            guard let command = table.value("command")?.stringValue, !command.isEmpty else {
                throw GatewayMigrationError.invalidDefinition("its command is missing or is not a string")
            }
            let arguments = table.value("args")?.elements?.compactMap(\.stringValue) ?? []
            if let elements = table.value("args")?.elements, arguments.count != elements.count {
                throw GatewayMigrationError.invalidDefinition("one of its arguments is not a string")
            }
            let environment = try tomlEnvironment(
                table: table,
                subtable: document.table(at: [target.source.serversKey, name, "env"])
            )
            try Self.refusePlaceholders(command: command, arguments: arguments, environment: environment)
            let replacement = ([TOMLBuilder.serverBlock(
                name: name,
                under: target.source.serversKey,
                command: helperURL.path,
                args: ["--route", routeID],
                url: nil,
                env: [],
                enabled: nil
            )] + clientSide.map { document.slice($0.span) }).joined(separator: "\n")
            return DirectDefinition(
                serverName: name,
                sourceText: sourceText,
                command: command,
                arguments: arguments,
                environment: environment,
                replacement: replacement
            )
        }
    }

    private func jsonEnvironment(_ node: JSONNode?) throws -> [EnvEntry] {
        guard let node else { return [] }
        guard let members = node.members else {
            throw GatewayMigrationError.invalidDefinition("its environment is not an object")
        }
        return try members.map { member in
            guard let value = member.value.stringValue else {
                throw GatewayMigrationError.invalidDefinition("environment value \(member.key) is not a string")
            }
            return EnvEntry(key: member.key, value: value)
        }
    }

    /// Every way Codex accepts an environment: an `[….env]` sub-table, an
    /// inline `env = { … }` (what Codex's own docs show), or dotted `env.K = …`.
    /// Missing one would start the server without it — and leave its values
    /// unredacted in the preview.
    private func tomlEnvironment(table: TOMLTable, subtable: TOMLTable?) throws -> [EnvEntry] {
        var pairs: [(String?, TOMLValue)] = (subtable?.pairs ?? []).map { ($0.name, $0.value) }
        if let env = table.value("env") {
            guard let inline = env.inlinePairs else {
                throw GatewayMigrationError.invalidDefinition("its environment is not a table")
            }
            pairs += inline.map { ($0.name, $0.value) }
        }
        pairs += table.pairs.filter { $0.key.count == 2 && $0.key[0] == "env" }.map { ($0.key[1], $0.value) }
        return try pairs.map { key, value in
            guard let key, let value = value.stringValue else {
                throw GatewayMigrationError.invalidDefinition("one of its environment values is not a string")
            }
            return EnvEntry(key: key, value: value)
        }
    }

    /// Codex keys about the entry rather than the process it launches.
    private static let codexClientKeys: Set<String> = [
        "enabled", "startup_timeout_sec", "startup_timeout_ms", "tool_timeout_sec", "enabled_tools", "disabled_tools",
    ]

    private static func unmodelled(_ keys: [String]) -> GatewayMigrationError {
        .invalidDefinition("it uses \(keys.joined(separator: ", ")), which the gateway does not carry over. Remove \(keys.count == 1 ? "it" : "them") or keep this server Direct")
    }

    /// `${…}` is expanded by the client that launches a definition. Behind the
    /// gateway the client launches the helper instead, so the server would
    /// receive the placeholder text itself.
    private static func refusePlaceholders(command: String, arguments: [String], environment: [EnvEntry]) throws {
        let values = [command] + arguments + environment.compactMap(\.value)
        if values.contains(where: { $0.contains("${") }) {
            throw GatewayMigrationError.invalidDefinition("it uses ${…} placeholders, which only its client can expand")
        }
    }

    private func routeIDs(in text: String, clientID: ClientID) throws -> [String] {
        guard let descriptor = descriptors.first(where: { $0.id == clientID }),
              let source = descriptor.editableServerMap
        else { throw AuthoringError.noSourceForClient(clientID) }

        switch source.format {
        case .json:
            let document = try JSONDocument.parse(text)
            return document.value(at: [source.serversKey])?.members?.compactMap { member in
                routeID(command: member.value["command"]?.stringValue, arguments: member.value["args"]?.elements?.compactMap(\.stringValue))
            } ?? []
        case .toml:
            let document = try TOMLDocument.parse(text)
            return document.serverNames(under: source.serversKey).compactMap { name in
                let table = document.table(at: [source.serversKey, name])
                return routeID(command: table?.value("command")?.stringValue, arguments: table?.value("args")?.elements?.compactMap(\.stringValue))
            }
        }
    }

    private func routeID(command: String?, arguments: [String]?) -> String? {
        guard let command,
              URL(fileURLWithPath: command).lastPathComponent == "kytto-mcp-proxy",
              let arguments,
              arguments.count == 2,
              arguments[0] == "--route",
              UUID(uuidString: arguments[1]) != nil
        else { return nil }
        return arguments[1].lowercased()
    }

    // MARK: - Splicing

    private func writeReplacement(
        _ replacement: String,
        target: Target,
        routeID: String?,
        serverName: String? = nil
    ) throws -> WriteReceipt {
        switch target.source.format {
        case .json:
            return try writer.edit(
                url: target.url,
                clientID: target.clientID,
                pathDisplay: target.pathDisplay,
                expecting: ledger.digest(for: target.url),
                as: JSONDocument.self
            ) { document in
                guard let serverName,
                      let map = document.value(at: [target.source.serversKey]),
                      let member = map.members?.first(where: {
                          Server.identity(for: $0.key) == Server.identity(for: serverName)
                      })
                else { throw GatewayMigrationError.definitionMissing }
                if let routeID {
                    guard member.value["args"]?.elements?.compactMap(\.stringValue) == ["--route", routeID] else {
                        throw GatewayMigrationError.routeMismatch
                    }
                }
                return try document.settingMember(member.key, at: [target.source.serversKey], to: replacement)
            }

        case .toml:
            return try writer.edit(
                url: target.url,
                clientID: target.clientID,
                pathDisplay: target.pathDisplay,
                expecting: ledger.digest(for: target.url),
                as: TOMLDocument.self
            ) { document in
                guard let serverName else { throw GatewayMigrationError.definitionMissing }
                let name = document.serverNames(under: target.source.serversKey)
                    .first(where: { Server.identity(for: $0) == Server.identity(for: serverName) }) ?? serverName
                if let routeID {
                    let arguments = document.table(at: [target.source.serversKey, name])?
                        .value("args")?.elements?.compactMap(\.stringValue)
                    guard arguments == ["--route", routeID] else { throw GatewayMigrationError.routeMismatch }
                }
                return try document.settingServer(name, under: target.source.serversKey, to: replacement)
            }
        }
    }

    private func redactedPreview(of direct: DirectDefinition, format: ConfigFormat) -> String {
        switch format {
        case .json:
            guard let document = try? JSONDocument.parse(direct.sourceText),
                  let members = document.root["env"]?.members
            else { return direct.sourceText }
            return document.replacing(members.map {
                ($0.value.span, JSONText.string("<stored in credentials>"))
            })
        case .toml:
            guard let document = try? TOMLDocument.parse(direct.sourceText) else {
                return direct.environment.isEmpty ? direct.sourceText : "<preview withheld: the definition could not be redacted>"
            }
            var spans: [TOMLSpan] = []
            for table in document.tables {
                if table.path.last == "env" { spans += table.pairs.map(\.value.span) }
                for pair in table.pairs where pair.key.first == "env" {
                    if pair.key.count == 2 { spans.append(pair.value.span) }
                    spans += (pair.value.inlinePairs ?? []).map(\.value.span)
                }
            }
            guard spans.count >= direct.environment.count else {
                return "<preview withheld: the definition could not be redacted>"
            }
            return document.replacing(spans.map { ($0, TOMLText.string("<stored in credentials>")) })
        }
    }
}
