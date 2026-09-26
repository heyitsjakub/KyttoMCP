import Foundation

public struct DiscoveredClient: Sendable {
    public let id: ClientID
    public let displayName: String
    /// The application (or CLI) is present on this machine.
    public let isInstalled: Bool
    /// At least one of the client's config sources exists on disk. A config with
    /// no client is a leftover — `~/.cursor` outlives uninstalling Cursor — and
    /// is shown rather than silently hidden.
    public let configExists: Bool
    public let configPathDisplay: String
    /// "JSON" or "TOML" — how this client's config is spelled, for display (§7.8).
    public let configFormatDisplay: String
    /// What switching a server off does to this client's file, in one sentence.
    /// The client screen exists to say this; it differs per client and the UI is
    /// not where it is decided.
    public let offSwitchSummary: String
    public let serverCount: Int
    public let schemaQuirks: String
    public let iconAsset: String
    public let isReadOnly: Bool
    public let configurationScope: ConfigurationScope
    public let scopeLabel: String
    /// A shorter name for surfaces that carry the client's identity some other
    /// way — the sidebar's project scopes, whose rows all share an icon and a
    /// heading. Nil when the display name is already short.
    public let shortName: String?
}

public struct DiscoveryResult: Sendable {
    public let clients: [DiscoveredClient]
    public let servers: [Server]
    public let diagnostics: [Diagnostic]
}

/// Reads every client's configuration and normalizes it into one list of servers.
///
/// Strictly read-only, and it stays that way through M1 (§6.6): nothing here
/// creates, touches or writes a file, including Kytto's own.
public struct Discovery: Sendable {
    private let home: URL
    private let locator: any AppLocating
    private let descriptors: [ClientDescriptor]
    /// Every file read here is recorded, so M2 can tell whether it still looks
    /// the way Kytto last saw it before writing to it.
    private let ledger: DigestLedger?
    /// Servers removed from a client only in order to switch them off.
    ///
    /// These are not in any config file any more, so without them the row would
    /// vanish from the matrix and the user would have no way to switch it back
    /// on. A parked server is a disabled server, and it is shown as one.
    private let parked: [ParkStore.Entry]
    /// What Kytto has measured about these servers, keyed by server id.
    private let metadata: [String: ServerMetadata]
    private let resolver: ClientPathResolver

    public init(
        home: URL,
        locator: any AppLocating,
        descriptors: [ClientDescriptor]? = nil,
        ledger: DigestLedger? = nil,
        parked: [ParkStore.Entry] = [],
        metadata: [String: ServerMetadata] = [:],
        pathOverrides: [String: String] = [:]
    ) {
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        self.home = home
        self.locator = locator
        self.descriptors = descriptors ?? ClientRegistry.allIncludingClaudeCodeProjects(home: home, pathOverride: pathOverrides[ClientID.claudeCode.rawValue])
        self.ledger = ledger
        self.parked = parked
        self.metadata = metadata
    }

    /// Every directory holding something Kytto reads.
    ///
    /// Directories rather than files, because a config is replaced by a rename
    /// and a watch on the old file would be left pointing at nothing.
    public func watchedDirectories() -> [URL] {
        var result: Set<URL> = []
        for descriptor in descriptors {
            for source in descriptor.sources {
                switch source {
                case .serverMap(let file, _, _, let enablement):
                    result.insert(resolver.resolveServerMap(file, for: descriptor.id).deletingLastPathComponent())
                    if case .denyList(let denyFile, _, _) = enablement {
                        result.insert(denyFile.resolve(home: home).deletingLastPathComponent())
                    }
                case .extensionBundles(let directory, let settings, _):
                    result.insert(directory.resolve(home: home))
                    result.insert(settings.resolve(home: home))
                case .bundledPackages(let root, _, _):
                    // The client owns this tree and rewrites it on its own
                    // schedule; watching the root is enough to notice a plugin
                    // arriving or leaving.
                    result.insert(root.resolve(home: home))
                case .readOnlyAutoServerMap(let file):
                    result.insert(resolver.resolve(file).deletingLastPathComponent())
                case .scopedJSONServerMap(let file, _, _):
                    result.insert(resolver.resolve(file).deletingLastPathComponent())
                }
            }
        }
        return result.sorted { $0.path < $1.path }
    }

    /// Reads a file and records what it looked like. Returns nil if absent.
    private func readTracked(_ url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else {
            ledger?.record(nil, for: url)
            return nil
        }
        ledger?.record(ConfigWriter.digest(of: data), for: url)
        return String(data: data, encoding: .utf8)
    }

    public func run() -> DiscoveryResult {
        var clients: [DiscoveredClient] = []
        var diagnostics: [Diagnostic] = []
        // Keyed by `Server.identity`, so the same name in three clients is one row.
        var merged: [String: Server] = [:]
        var order: [String] = []

        for descriptor in descriptors {
            var configExists = false
            var countForClient = 0

            for source in descriptor.sources {
                let outcome = read(source: source, descriptor: descriptor)
                diagnostics.append(contentsOf: outcome.diagnostics)
                configExists = configExists || outcome.sourceExists

                for found in outcome.servers {
                    countForClient += 1
                    let key = Server.identity(for: found.name)

                    if var existing = merged[key] {
                        // The disagreement itself is not reported here any more.
                        // Every client's copy is kept, so `ServerDrift` can say
                        // which clients differ and in what — a diagnostic in the
                        // footer could only say that something, somewhere, did.
                        existing.enabledIn[descriptor.id] = found.enablement
                        existing.definitionsByClient[descriptor.id] =
                            ClientDefinition(from: found.server)
                        merged[key] = existing
                    } else {
                        var server = found.server
                        server.enabledIn[descriptor.id] = found.enablement
                        server.definitionsByClient[descriptor.id] =
                            ClientDefinition(from: found.server)
                        merged[key] = server
                        order.append(key)
                    }
                }
            }

            // The map the user can actually author into is what the client screen
            // describes: its format and its off switch are the two things that
            // make one client behave unlike the next.
            let editable = descriptor.editableServerMap
            clients.append(
                DiscoveredClient(
                    id: descriptor.id,
                    displayName: descriptor.displayName,
                    isInstalled: isInstalled(descriptor),
                    configExists: configExists,
                    configPathDisplay: editable.map {
                        resolver.displayServerMap($0.file, for: descriptor.id)
                    } ?? descriptor.sources.first.map { source in
                        let path = source.file.displayString()
                        return descriptor.configurationScope == .global
                            ? path
                            : "\(path) · \(descriptor.scopeLabel)"
                    } ?? "",
                    configFormatDisplay: descriptor.isReadOnly
                        ? "Auto-detected · read-only"
                        : editable?.format.displayName ?? "",
                    offSwitchSummary: descriptor.offSwitchSummary,
                    serverCount: countForClient,
                    schemaQuirks: descriptor.schemaQuirks,
                    iconAsset: descriptor.iconAsset,
                    isReadOnly: descriptor.isReadOnly,
                    configurationScope: descriptor.configurationScope,
                    scopeLabel: descriptor.scopeLabel,
                    shortName: descriptor.shortName
                )
            )
        }

        // Bring back anything Kytto parked. It is not in a config file any more,
        // but from the user's point of view it is a server they switched off.
        for entry in parked {
            guard descriptors.contains(where: { $0.id == entry.clientID }) else { continue }
            let key = Server.identity(for: entry.serverName)

            // Parked text is in whatever the client it came from is spelled in.
            let parkedServer = parkedFields(entry).map {
                $0.makeServer(
                    name: entry.serverName,
                    origin: .configFile(entry.clientID),
                    isBundled: false,
                    sourceText: entry.sourceText
                )
            }

            if var existing = merged[key] {
                existing.enabledIn[entry.clientID] = .disabled
                // A parked definition is still this client's definition — it is
                // what switching the server back on will put back, byte for byte.
                // Leaving it out of the comparison would call a client identical
                // to the others on the strength of holding nothing.
                if let parkedServer, existing.definitionsByClient[entry.clientID] == nil {
                    existing.definitionsByClient[entry.clientID] =
                        ClientDefinition(from: parkedServer)
                }
                merged[key] = existing
                continue
            }

            guard var server = parkedServer else { continue }
            server.enabledIn[entry.clientID] = .disabled
            server.definitionsByClient[entry.clientID] = ClientDefinition(from: server)
            merged[key] = server
            order.append(key)
        }

        // Fill in `absent` so the web layer can render a full grid without
        // knowing which clients exist.
        let allClientIDs = descriptors.map(\.id)
        let servers = order.compactMap { key -> Server? in
            guard var server = merged[key] else { return nil }
            for id in allClientIDs where server.enabledIn[id] == nil {
                server.enabledIn[id] = .absent
            }
            // Measurements are looked up rather than taken: nothing here spawns
            // anything (§6).
            server.health = metadata[server.id]?.health
            server.tokenWeight = metadata[server.id]?.tokenWeight
            return server
        }

        return DiscoveryResult(
            clients: clients,
            servers: servers.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending },
            diagnostics: diagnostics
        )
    }

    /// Reads a parked definition back, in the format of the client it was taken
    /// from. Codex never parks — its servers stay in the file when switched off —
    /// but the store is per-client and nothing else guarantees that stays true.
    private func parkedFields(_ entry: ParkStore.Entry) -> ServerFields? {
        let format = descriptors
            .first { $0.id == entry.clientID }?
            .editableServerMap?.format ?? .json

        switch format {
        case .json:
            guard let document = try? JSONDocument.parse(entry.sourceText) else { return nil }
            return ServerFields(node: document.root)
        case .toml:
            guard let document = try? TOMLDocument.parse(entry.sourceText),
                  let table = document.tables.first(where: { $0.headerSpan != nil })
            else { return nil }
            let envPath = table.path + ["env"]
            return ServerFields(table: table, envTable: document.table(at: envPath))
        }
    }

    // MARK: - Detection

    private func isInstalled(_ descriptor: ClientDescriptor) -> Bool {
        if descriptor.isReadOnly { return true }
        if descriptor.bundleIdentifiers.contains(where: locator.applicationExists(bundleIdentifier:)) {
            return true
        }
        return descriptor.executableNames.contains { locator.executableExists(named: $0, home: home) }
    }

    // MARK: - Reading sources

    private struct FoundServer {
        let name: String
        let server: Server
        let enablement: Enablement
        var fingerprint: String { server.fingerprint }
    }

    private struct SourceOutcome {
        var servers: [FoundServer] = []
        var diagnostics: [Diagnostic] = []
        var sourceExists = false
    }

    private func read(source: ConfigSource, descriptor: ClientDescriptor) -> SourceOutcome {
        switch source {
        case .serverMap(let file, let format, let serversKey, let enablement):
            switch format {
            case .json:
                return readJSONServerMap(
                    file: file, serversKey: serversKey, enablement: enablement, descriptor: descriptor
                )
            case .toml:
                return readTOMLServerMap(
                    file: file, serversKey: serversKey, enablement: enablement, descriptor: descriptor
                )
            }
        case .extensionBundles(let directory, let settingsDirectory, let flagKey):
            return readExtensionBundles(
                directory: directory,
                settingsDirectory: settingsDirectory,
                flagKey: flagKey,
                descriptor: descriptor
            )
        case .bundledPackages(let root, let manifestName, let serversKey):
            return readBundledPackages(
                root: root, manifestName: manifestName, serversKey: serversKey, descriptor: descriptor
            )
        case .readOnlyAutoServerMap(let file):
            return readAutoServerMap(file: file, descriptor: descriptor)
        case .scopedJSONServerMap(let file, let scopePath, let serversKey):
            return readScopedJSONServerMap(
                file: file,
                scopePath: scopePath,
                serversKey: serversKey,
                descriptor: descriptor
            )
        }
    }

    /// Recognizes the small set of configuration adapters Kytto has actually
    /// tested. An unknown or ambiguous document is reported and remains
    /// untouched instead of being guessed from its filename.
    private func readAutoServerMap(file: PlatformPath, descriptor: ClientDescriptor) -> SourceOutcome {
        var outcome = SourceOutcome()
        let url = resolver.resolve(file)
        guard let source = readTracked(url) else { return outcome }
        outcome.sourceExists = true

        if let document = try? JSONDocument.parse(source) {
            let keys = ["mcpServers", "servers"].filter { document.root[$0]?.members != nil }
            guard keys.count == 1, let key = keys.first else {
                outcome.diagnostics.append(unknownAdapterDiagnostic(file: file, descriptor: descriptor, ambiguous: keys.count > 1))
                return outcome
            }
            return readJSONServerMap(
                file: file,
                serversKey: key,
                enablement: .presence,
                descriptor: descriptor
            )
        }

        if let document = try? TOMLDocument.parse(source) {
            let keys = ["mcp_servers", "mcpServers", "servers"].filter { key in
                document.table(at: [key]) != nil || !document.serverNames(under: key).isEmpty
            }
            guard keys.count == 1, let key = keys.first else {
                outcome.diagnostics.append(unknownAdapterDiagnostic(file: file, descriptor: descriptor, ambiguous: keys.count > 1))
                return outcome
            }
            return readTOMLServerMap(
                file: file,
                serversKey: key,
                enablement: .presence,
                descriptor: descriptor
            )
        }

        outcome.diagnostics.append(
            Diagnostic(
                severity: .error,
                clientID: descriptor.id,
                pathDisplay: file.displayString(),
                message: "Could not recognize this custom source as JSON, JSONC or TOML. Kytto did not change it."
            )
        )
        return outcome
    }

    /// Reads one project map from Claude Code's shared `projects` object. Scope
    /// descriptors are read-only and have their own dynamic client id, so a
    /// global entry and a project entry never silently overwrite one another in
    /// the normalized matrix.
    private func readScopedJSONServerMap(
        file: PlatformPath,
        scopePath: String,
        serversKey: String,
        descriptor: ClientDescriptor
    ) -> SourceOutcome {
        var outcome = SourceOutcome()
        let url = resolver.resolve(file)
        guard let source = readTracked(url) else { return outcome }
        outcome.sourceExists = true

        let document: JSONDocument
        do {
            document = try JSONDocument.parse(source)
        } catch {
            outcome.diagnostics.append(unreadable(error, file: file, descriptor: descriptor))
            return outcome
        }

        guard let project = document.root["projects"]?.members?.first(where: { $0.key == scopePath })?.value,
              let map = project[serversKey],
              let members = map.members
        else { return outcome }

        for member in members {
            guard member.value.members != nil else {
                outcome.diagnostics.append(
                    Diagnostic(
                        severity: .warning,
                        clientID: descriptor.id,
                        pathDisplay: file.displayString(),
                        message: "Skipped \"\(member.key)\" in \(scopePath): expected an object."
                    )
                )
                continue
            }
            let server = ServerFields(node: member.value).makeServer(
                name: member.key,
                origin: .configFile(descriptor.id),
                isBundled: false,
                sourceText: document.slice(member.value.span)
            )
            outcome.servers.append(
                FoundServer(name: member.key, server: server, enablement: .enabled)
            )
        }
        return outcome
    }

    private func unknownAdapterDiagnostic(
        file: PlatformPath,
        descriptor: ClientDescriptor,
        ambiguous: Bool
    ) -> Diagnostic {
        Diagnostic(
            severity: .warning,
            clientID: descriptor.id,
            pathDisplay: file.displayString(),
            message: ambiguous
                ? "This custom source contains more than one known MCP server map. Choose a less ambiguous document; Kytto did not change it."
                : "No supported top-level MCP server map was found. Expected mcpServers, servers or mcp_servers; Kytto did not change it."
        )
    }

    /// A file Kytto could not parse is one it will not write to, and saying so is
    /// more use than an empty column.
    private func unreadable(
        _ error: any Error,
        file: PlatformPath,
        descriptor: ClientDescriptor
    ) -> Diagnostic {
        Diagnostic(
            severity: .error,
            clientID: descriptor.id,
            pathDisplay: resolver.displayServerMap(file, for: descriptor.id),
            message: descriptor.isReadOnly
                ? "Could not read this custom source: \(error). Kytto did not change it."
                : "Could not read this config: \(error). Kytto will not write to it."
        )
    }

    private func readJSONServerMap(
        file: PlatformPath,
        serversKey: String,
        enablement: EnablementStrategy,
        descriptor: ClientDescriptor
    ) -> SourceOutcome {
        var outcome = SourceOutcome()
        let url = resolver.resolveServerMap(file, for: descriptor.id)
        guard let source = readTracked(url) else { return outcome }
        outcome.sourceExists = true

        let document: JSONDocument
        do {
            document = try JSONDocument.parse(source)
        } catch {
            outcome.diagnostics.append(unreadable(error, file: file, descriptor: descriptor))
            return outcome
        }

        let denied = deniedNames(for: enablement)

        // Only the top-level servers key. For Claude Code that deliberately skips
        // `projects.<path>.mcpServers`, which is project scope and out of v1 (§4).
        guard let map = document.root[serversKey], let members = map.members else { return outcome }

        for member in members {
            guard member.value.members != nil else {
                outcome.diagnostics.append(
                    Diagnostic(
                        severity: .warning,
                        clientID: descriptor.id,
                        pathDisplay: file.displayString(),
                        message: "Skipped \"\(member.key)\": expected an object."
                    )
                )
                continue
            }
            let server = ServerFields(node: member.value).makeServer(
                name: member.key,
                origin: .configFile(descriptor.id),
                isBundled: false,
                sourceText: document.slice(member.value.span)
            )
            outcome.servers.append(
                FoundServer(
                    name: member.key,
                    server: server,
                    enablement: denied.contains(Server.identity(for: member.key)) ? .disabled : .enabled
                )
            )
        }
        return outcome
    }

    /// Codex. One table per server, its environment in a sub-table, and an
    /// `enabled` boolean on the server's own table rather than in a file
    /// somewhere else — the only client of the five whose off switch is local.
    private func readTOMLServerMap(
        file: PlatformPath,
        serversKey: String,
        enablement: EnablementStrategy,
        descriptor: ClientDescriptor
    ) -> SourceOutcome {
        var outcome = SourceOutcome()
        let url = resolver.resolveServerMap(file, for: descriptor.id)
        guard let source = readTracked(url) else { return outcome }
        outcome.sourceExists = true

        let document: TOMLDocument
        do {
            document = try TOMLDocument.parse(source)
        } catch {
            outcome.diagnostics.append(unreadable(error, file: file, descriptor: descriptor))
            return outcome
        }

        let flagKey: String? = if case .inlineFlag(let key) = enablement { key } else { nil }
        let inlinePairs = document.table(at: [serversKey])?.pairs ?? []

        for name in document.serverNames(under: serversKey) {
            guard let table = document.table(at: [serversKey, name]) else {
                let matches = inlinePairs.filter { $0.name == name && $0.value.inlinePairs != nil }
                guard matches.count == 1, let pair = matches.first else {
                    outcome.diagnostics.append(
                        Diagnostic(
                            severity: .error,
                            clientID: descriptor.id,
                            pathDisplay: file.displayString(),
                            message: "Skipped \"\(name)\": its inline definition is duplicated or ambiguous."
                        )
                    )
                    continue
                }
                let fields = ServerFields(inlineValue: pair.value)
                let server = fields.makeServer(
                    name: name,
                    origin: .configFile(descriptor.id),
                    isBundled: false,
                    sourceText: document.slice(pair.span)
                )
                let isEnabled = flagKey.flatMap { key in
                    pair.value.inlinePairs?.first { $0.name == key }?.value.boolValue
                } ?? true
                outcome.servers.append(
                    FoundServer(name: name, server: server, enablement: isEnabled ? .enabled : .disabled)
                )
                outcome.diagnostics.append(
                    Diagnostic(
                        severity: .warning,
                        clientID: descriptor.id,
                        pathDisplay: file.displayString(),
                        message: """
                        "\(name)" is written as an inline table. Kytto can rotate an existing \
                        environment secret in place, but other edits remain disabled.
                        """
                    )
                )
                continue
            }

            let envTable = document.table(at: [serversKey, name, "env"])
            let fields = ServerFields(table: table, envTable: envTable)
            let server = fields.makeServer(
                name: name,
                origin: .configFile(descriptor.id),
                isBundled: false,
                sourceText: document.serverDefinitionText(name, under: serversKey) ?? ""
            )

            // Absent means on. That is Codex's rule, not a guess: `codex mcp list`
            // reports a server with no `enabled` key as enabled.
            let isEnabled = flagKey.flatMap { table.value($0)?.boolValue } ?? true
            outcome.servers.append(
                FoundServer(name: name, server: server, enablement: isEnabled ? .enabled : .disabled)
            )
        }
        return outcome
    }

    /// Servers that arrive inside an installed package rather than being
    /// configured by anyone — Codex plugins.
    ///
    /// Read-only on purpose. The tree is a cache the client repopulates, so a
    /// write here would be undone without warning, and the row is marked bundled
    /// so the matrix refuses to copy it into another client for the same reason
    /// Claude Desktop extensions are refused.
    private func readBundledPackages(
        root: PlatformPath,
        manifestName: String,
        serversKey: String,
        descriptor: ClientDescriptor
    ) -> SourceOutcome {
        var outcome = SourceOutcome()
        let directory = root.resolve(home: home)
        guard FileManager.default.fileExists(atPath: directory.path) else { return outcome }
        outcome.sourceExists = true

        for manifestURL in Self.bundledManifestURLs(root: directory, manifestName: manifestName) {
            guard let text = try? String(contentsOf: manifestURL, encoding: .utf8),
                  let document = try? JSONDocument.parse(text),
                  let members = document.root[serversKey]?.members
            else { continue }

            for member in members where member.value.members != nil {
                let server = ServerFields(node: member.value).makeServer(
                    name: member.key,
                    origin: .configFile(descriptor.id),
                    isBundled: true,
                    sourceText: document.slice(member.value.span)
                )
                outcome.servers.append(
                    FoundServer(name: member.key, server: server, enablement: .enabled)
                )
            }
        }
        return outcome
    }

    /// Finds package manifests without walking through the payload of a package
    /// after its manifest has been found. Codex plugin caches can contain entire
    /// runtimes and `node_modules` trees; inspecting all of those files made every
    /// otherwise-small discovery pass proportional to the cache's byte size.
    static func bundledManifestURLs(root: URL, manifestName: String) -> [URL] {
        var pending = [root]
        var manifests: [URL] = []

        while let directory = pending.popLast() {
            let manifest = directory.appending(path: manifestName)
            if let values = try? manifest.resourceValues(forKeys: [.isRegularFileKey]),
               values.isRegularFile == true {
                manifests.append(manifest)
                // A manifest marks the root of one installed package. Anything
                // below it is that package's implementation, not another package
                // Kytto should recursively inspect.
                continue
            }

            guard let entries = try? FileManager.default.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]
            ) else { continue }

            for entry in entries {
                guard let values = try? entry.resourceValues(
                    forKeys: [.isDirectoryKey, .isSymbolicLinkKey]
                ), values.isDirectory == true, values.isSymbolicLink != true
                else { continue }
                pending.append(entry)
            }
        }

        return manifests.sorted { $0.path < $1.path }
    }

    /// Names switched off in a separate settings file, normalized for comparison.
    private func deniedNames(for enablement: EnablementStrategy) -> Set<String> {
        guard case .denyList(let file, let key, let nameField) = enablement else { return [] }
        let url = file.resolve(home: home)
        guard let source = readTracked(url),
              let document = try? JSONDocument.parse(source),
              let list = document.root[key]?.elements
        else { return [] }

        return Set(
            list.compactMap { entry -> String? in
                // Tolerate both `[{serverName: "x"}]` and a plain `["x"]`.
                let name = entry[nameField]?.stringValue ?? entry.stringValue
                return name.map(Server.identity(for:))
            }
        )
    }

    private func readExtensionBundles(
        directory: PlatformPath,
        settingsDirectory: PlatformPath,
        flagKey: String,
        descriptor: ClientDescriptor
    ) -> SourceOutcome {
        var outcome = SourceOutcome()
        let root = directory.resolve(home: home)
        let settingsRoot = settingsDirectory.resolve(home: home)

        guard let entries = try? FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return outcome }
        outcome.sourceExists = true

        for entry in entries.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let manifestURL = entry.appending(path: "manifest.json")
            guard FileManager.default.fileExists(atPath: manifestURL.path) else { continue }

            let manifest: JSONDocument
            do {
                manifest = try JSONDocument.parse(String(contentsOf: manifestURL, encoding: .utf8))
            } catch {
                outcome.diagnostics.append(
                    Diagnostic(
                        severity: .warning,
                        clientID: descriptor.id,
                        pathDisplay: directory.displayString() + "/" + entry.lastPathComponent,
                        message: "Could not read this extension's manifest: \(error)"
                    )
                )
                continue
            }

            let bundleID = entry.lastPathComponent
            let name = manifest.root["display_name"]?.stringValue
                ?? manifest.root["name"]?.stringValue
                ?? bundleID

            // Extensions describe their process under `server.mcp_config`.
            guard let config = manifest.root.value(at: ["server", "mcp_config"]) else { continue }
            var server = ServerFields(node: config).makeServer(
                name: name,
                origin: .claudeDesktopExtension(bundleID: bundleID),
                isBundled: true,
                sourceText: manifest.slice(config.span)
            )

            // Absent settings file means the extension has never been toggled,
            // which Claude Desktop treats as on.
            let settingsURL = settingsRoot.appending(path: "\(bundleID).json")
            let settings = readTracked(settingsURL).flatMap { try? JSONDocument.parse($0) }
            let isEnabled = settings?.root[flagKey]?.boolValue ?? true

            // What Claude Desktop would substitute before running this. The
            // trailing slash goes: manifests write `${__dirname}/server/index.js`,
            // and keeping it would produce a doubled separator.
            var placeholders = [
                "__dirname": entry.standardizedFileURL.path(percentEncoded: false)
                    .replacingOccurrences(of: "/+$", with: "", options: .regularExpression),
            ]
            for member in settings?.root["userConfig"]?.members ?? [] {
                let value = member.value.stringValue
                    ?? member.value.elements?.compactMap(\.stringValue).joined(separator: " ")
                if let value { placeholders["user_config.\(member.key)"] = value }
            }
            server.placeholders = placeholders

            outcome.servers.append(
                FoundServer(name: name, server: server, enablement: isEnabled ? .enabled : .disabled)
            )
        }
        return outcome
    }
}
