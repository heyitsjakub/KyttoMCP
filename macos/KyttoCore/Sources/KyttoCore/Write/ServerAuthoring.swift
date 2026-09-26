import Foundation

public struct AuthoringResult: Sendable {
    public let serverName: String
    /// One entry per client whose config actually changed.
    public let changed: [ClientID]
    public let backupIDs: [ClientID: String]
    public let pathDisplays: [ClientID: String]
    /// Clients whose *switched-off* copy was brought into line as well.
    ///
    /// A server switched off in a presence-only client is not in that client's
    /// file at all — Kytto is holding its bytes so switching it back on restores
    /// them. Those bytes are a copy like any other and can drift like one, so
    /// unifying updates them too. Nothing in a config file changed, so this does
    /// not ask anyone to restart.
    public var parkedUpdated: [ClientID] = []
    /// Off copies that could not be brought into line. Rare, and reported rather
    /// than thrown: the config writes have already succeeded by this point, and
    /// turning that into a failure would be a lie in the other direction.
    public var parkedFailures: [ClientID] = []
    public var requiresRestart: Bool { !changed.isEmpty }
}

public enum AuthoringError: Error, LocalizedError, Equatable {
    case invalid([ServerDraft.ValidationError])
    case notEditable(String)
    case noSourceForClient(ClientID)
    case notFound(String)
    case rollbackFailed(original: String, rollback: String)

    public var errorDescription: String? {
        switch self {
        case .invalid(let errors):
            errors.compactMap(\.errorDescription).joined(separator: " ")
        case .notEditable(let name):
            "\"\(name)\" is a Claude Desktop extension. Extensions are installed bundles — change them in Claude Desktop, not here."
        case .noSourceForClient(let id):
            "No editable configuration is known for \(id.rawValue)."
        case .notFound(let name):
            "\"\(name)\" is not in any client configuration."
        case .rollbackFailed(let original, let rollback):
            "The operation failed (\(original)), and Kytto could not completely roll it back: \(rollback)"
        }
    }
}

/// Adding, editing and removing servers (§7.2, §7.5).
///
/// Writes go through the same `ConfigWriter` pipeline as toggles, so the safety
/// rules hold here too: refuse if the file moved under us, back up, splice,
/// write atomically. The only new thing is that a definition now has to be
/// *rendered*, and rendered to match the file it lands in.
public struct ServerAuthoring: Sendable {
    private let home: URL
    private let resolver: ClientPathResolver
    private let writer: ConfigWriter
    private let parkStore: ParkStore
    private let ledger: DigestLedger
    private let descriptors: [ClientDescriptor]

    private struct FileSnapshot {
        let url: URL
        let clientID: ClientID
        let pathDisplay: String
        let text: String?
        let digest: String?
    }

    private struct EditableTarget {
        let clientID: ClientID
        let source: (
            file: PlatformPath,
            format: ConfigFormat,
            serversKey: String,
            enablement: EnablementStrategy
        )
        let snapshot: FileSnapshot
    }

    private struct CommittedWrite {
        let snapshot: FileSnapshot
        let writtenDigest: String
    }

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

    // MARK: - Create

    public func create(_ draft: ServerDraft, in clients: [ClientID], existing: [Server]) throws -> AuthoringResult {
        let taken = takenNames(in: clients, from: existing)
        let errors = draft.validate(takenNames: taken)
        guard errors.isEmpty else { throw AuthoringError.invalid(errors) }

        return try write(draft, to: clients, replacing: nil)
    }

    // MARK: - Update

    /// Applies `draft` everywhere the server currently lives.
    ///
    /// Editing is not per-client. A server configured in three clients is one row
    /// in the matrix, and changing its command in only one of them is how the
    /// three quietly drift apart — which is the problem this app exists to solve.
    public func update(
        _ draft: ServerDraft,
        originalName: String,
        server: Server,
        existing: [Server]
    ) throws -> AuthoringResult {
        guard !server.isBundled else { throw AuthoringError.notEditable(server.name) }

        let clients = server.enabledIn
            .filter { $0.value != .absent }
            .map(\.key)
            .sorted { $0.rawValue < $1.rawValue }

        let taken = takenNames(in: clients, from: existing)
        let errors = draft.validate(takenNames: taken, allowing: originalName)
        guard errors.isEmpty else { throw AuthoringError.invalid(errors) }

        // A rename is a delete plus an add, because the name is the key.
        let renamedFrom = Server.identity(for: originalName) == Server.identity(for: draft.trimmedName)
            ? nil
            : originalName
        return try write(draft, to: clients, replacing: renamedFrom)
    }

    // MARK: - Unify

    /// Makes the listed clients' copies of `server` match `draft`.
    ///
    /// The sibling of `update`, for the case `update` cannot express. `update`
    /// applies one edit everywhere because a server is one row; this applies one
    /// client's *existing* definition to the others, which is what resolving
    /// drift means. Neither is per-client editing — the point of both is that
    /// the copies end up the same.
    ///
    /// - Parameter clients: who to bring into line. The client the definition
    ///   came from does not need to be in the list and does no harm if it is.
    public func unify(
        _ draft: ServerDraft,
        server: Server,
        into clients: [ClientID]
    ) throws -> AuthoringResult {
        // No rename, so no name can be taken by anything but this server itself.
        let errors = draft.validate()
        guard errors.isEmpty else { throw AuthoringError.invalid(errors) }

        for clientID in clients where server.definitionsByClient[clientID]?.isBundled == true {
            throw AuthoringError.notEditable(server.name)
        }

        // A parked copy is not in the file. Writing it there would put the
        // definition back, and for a presence-only client putting the definition
        // back is precisely what switching a server *on* means — so unifying
        // would silently re-enable it. Those copies are updated where they
        // actually live instead.
        var parked: [ClientID] = []
        var inFile: [ClientID] = []
        for clientID in clients {
            if parkStore.parked(clientID: clientID, serverName: server.name) != nil {
                parked.append(clientID)
            } else {
                inFile.append(clientID)
            }
        }

        var result = inFile.isEmpty
            ? AuthoringResult(
                serverName: draft.trimmedName, changed: [], backupIDs: [:], pathDisplays: [:]
            )
            : try write(draft, to: inFile, replacing: nil)

        for clientID in parked {
            // Parked text is spelled in the format of the client it came from.
            // Only JSON clients park — a Codex server keeps its definition and
            // its `enabled = false` — but the store is per-client and nothing
            // enforces that, so an unexpected format is left alone rather than
            // overwritten with the wrong syntax.
            guard descriptors.first(where: { $0.id == clientID })?
                .editableServerMap?.format == .json
            else {
                result.parkedFailures.append(clientID)
                continue
            }

            do {
                try parkStore.park(
                    clientID: clientID,
                    serverName: server.name,
                    sourceText: draft.definition(baseIndent: "", unit: "  ")
                )
                result.parkedUpdated.append(clientID)
            } catch {
                result.parkedFailures.append(clientID)
            }
        }

        return result
    }

    // MARK: - Delete

    public func delete(_ server: Server) throws -> AuthoringResult {
        try remove(
            server,
            from: server.enabledIn
                .filter { $0.value != .absent }
                .map(\.key)
                .sorted { $0.rawValue < $1.rawValue }
        )
    }

    /// Takes a server out of one client and leaves the others holding it.
    ///
    /// The matrix cannot express this: a cell is a two-position switch over a
    /// three-state column, and switching off is `disabled` in every client by
    /// design — the definition, or Kytto's parked copy of it, is kept precisely
    /// so switching back on restores what was there rather than rebuilding it.
    /// Getting a cell back to `absent` is therefore a different act from
    /// switching it off, and this is it (§7.5).
    ///
    /// Everything that could bring the row back has to go, or the next refresh
    /// resurrects it as "disabled": the definition, the parked copy, and the
    /// deny-list entry that would otherwise name a server no file mentions.
    public func remove(_ server: Server, from clientID: ClientID) throws -> AuthoringResult {
        guard (server.enabledIn[clientID] ?? .absent) != .absent else {
            throw AuthoringError.notFound(server.name)
        }
        return try remove(server, from: [clientID])
    }

    private func remove(_ server: Server, from clients: [ClientID]) throws -> AuthoringResult {
        guard !server.isBundled else { throw AuthoringError.notEditable(server.name) }
        guard !clients.isEmpty else { throw AuthoringError.notFound(server.name) }

        let targets = try editableTargets(for: clients)
        let denySnapshots = try denyListSnapshots(for: server.name, in: clients)
        var changed: [ClientID] = []
        var backupIDs: [ClientID: String] = [:]
        var pathDisplays: [ClientID: String] = [:]
        var committed: [CommittedWrite] = []

        do {
            for target in targets {
                let clientID = target.clientID
                let source = target.source
                let url = target.snapshot.url
                let pathDisplay = target.snapshot.pathDisplay
                let expecting = ledger.digest(for: url)

                let receipt: WriteReceipt = switch source.format {
                case .json:
                    try writer.edit(
                        url: url, clientID: clientID, pathDisplay: pathDisplay, expecting: expecting
                    ) { document in
                        try document.removingMember(server.name, at: [source.serversKey])
                    }
                case .toml:
                    try writer.edit(
                        url: url, clientID: clientID, pathDisplay: pathDisplay, expecting: expecting,
                        as: TOMLDocument.self
                    ) { document in
                        try document.removingServer(
                            Self.name(of: server, in: document, under: source.serversKey),
                            under: source.serversKey
                        )
                    }
                }
                record(receipt, for: target.snapshot, in: &committed)

                if receipt.didWrite {
                    changed.append(clientID)
                }
                if let backupID = receipt.backupID {
                    backupIDs[clientID] = backupID
                }
                pathDisplays[clientID] = receipt.pathDisplay
            }

            try clearDenyListEntries(
                for: server.name,
                in: clients,
                snapshots: denySnapshots,
                committed: &committed
            )
        } catch {
            try rollBack(committed, after: error)
        }

        // Sidecar state changes only after every configuration file committed.
        // A removed server is gone on purpose, so any parked copy of it is dead
        // weight — leaving it would make the row reappear as "disabled".
        for clientID in clients {
            try? parkStore.unpark(clientID: clientID, serverName: server.name)
        }

        return AuthoringResult(
            serverName: server.name,
            changed: changed,
            backupIDs: backupIDs,
            pathDisplays: pathDisplays
        )
    }

    // MARK: - Shared

    private func write(
        _ draft: ServerDraft,
        to clients: [ClientID],
        replacing oldName: String?
    ) throws -> AuthoringResult {
        let targets = try editableTargets(for: clients)
        let denySnapshots: [String: FileSnapshot]
        if let oldName {
            denySnapshots = try denyListSnapshots(for: oldName, in: clients)
        } else {
            denySnapshots = [:]
        }
        var changed: [ClientID] = []
        var backupIDs: [ClientID: String] = [:]
        var pathDisplays: [ClientID: String] = [:]
        var committed: [CommittedWrite] = []

        do {
            for target in targets {
                let clientID = target.clientID
                let source = target.source
                let url = target.snapshot.url
                let pathDisplay = target.snapshot.pathDisplay
                let expecting = ledger.digest(for: url)

                let receipt: WriteReceipt = switch source.format {
                case .json:
                    try writer.edit(
                        url: url, clientID: clientID, pathDisplay: pathDisplay, expecting: expecting
                    ) { document in
                        var text = document.sourceText
                        if let oldName {
                            text = try document.removingMember(oldName, at: [source.serversKey])
                        }
                        let current = try JSONDocument.parse(text)

                        // Indent to match the siblings this will sit beside, or the
                        // map itself when it is the first entry.
                        let unit = IndentStyle.detect(in: current.sourceText).text
                        let map = current.value(at: [source.serversKey])
                        let baseIndent: String = if let first = map?.members?.first {
                            current.lineIndent(at: first.span.start)
                        } else if let map {
                            current.lineIndent(at: map.span.start) + unit
                        } else {
                            unit
                        }

                        return try current.settingMember(
                            draft.trimmedName,
                            at: [source.serversKey],
                            to: draft.definition(baseIndent: baseIndent, unit: unit)
                        )
                    }
                case .toml:
                    try writer.edit(
                        url: url, clientID: clientID, pathDisplay: pathDisplay, expecting: expecting,
                        as: TOMLDocument.self
                    ) { document in
                        // A server that is switched off must stay switched off. The
                        // whole block is rewritten, so a flag nobody carried across
                        // would silently turn it back on.
                        let flagKey: String? = if case .inlineFlag(let key) = source.enablement { key } else { nil }
                        let previousName = oldName ?? draft.trimmedName
                        let wasDisabled = flagKey.flatMap {
                            document.table(at: [source.serversKey, previousName])?.value($0)?.boolValue
                        } == false

                        var text = document.sourceText
                        if let oldName {
                            text = try document.removingServer(oldName, under: source.serversKey)
                        }
                        return try TOMLDocument.parse(text).settingServer(
                            draft.trimmedName,
                            under: source.serversKey,
                            to: draft.tomlBlock(under: source.serversKey, enabled: wasDisabled ? false : nil)
                        )
                    }
                }
                record(receipt, for: target.snapshot, in: &committed)

                if receipt.didWrite {
                    changed.append(clientID)
                }
                if let backupID = receipt.backupID {
                    backupIDs[clientID] = backupID
                }
                pathDisplays[clientID] = receipt.pathDisplay
            }

            if let oldName {
                try clearDenyListEntries(
                    for: oldName,
                    in: clients,
                    snapshots: denySnapshots,
                    committed: &committed
                )
            }
        } catch {
            try rollBack(committed, after: error)
        }

        if let oldName {
            for clientID in clients {
                try? parkStore.unpark(clientID: clientID, serverName: oldName)
            }
        }

        return AuthoringResult(
            serverName: draft.trimmedName,
            changed: changed,
            backupIDs: backupIDs,
            pathDisplays: pathDisplays
        )
    }

    /// Removes a name from any client's deny list. Without this, deleting a
    /// disabled server leaves a dangling entry, and recreating it later would
    /// come back mysteriously switched off.
    private func clearDenyListEntries(
        for name: String,
        in clients: [ClientID],
        snapshots: [String: FileSnapshot],
        committed: inout [CommittedWrite]
    ) throws {
        for clientID in clients {
            guard let descriptor = descriptors.first(where: { $0.id == clientID }),
                  let source = descriptor.editableServerMap,
                  case .denyList(let file, let key, let nameField) = source.enablement
            else { continue }

            let url = file.resolve(home: home)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            guard let snapshot = snapshots[url.standardizedFileURL.path] else {
                throw ConfigWriteError.unreadable(
                    pathDisplay: file.displayString(),
                    reason: "the file was not included in the authoring transaction"
                )
            }

            let receipt = try writer.edit(
                url: url,
                clientID: clientID,
                pathDisplay: file.displayString(),
                expecting: ledger.digest(for: url)
            ) { document in
                try document.removingElements(fromArrayAt: [key]) { element in
                    let entry = element[nameField]?.stringValue ?? element.stringValue
                    return entry.map { Server.identity(for: $0) == Server.identity(for: name) } ?? false
                }
            }
            record(receipt, for: snapshot, in: &committed)
        }
    }

    // MARK: - Transaction safety

    /// Resolves and preflights every target before the first write. This catches
    /// stale, unreadable and malformed files up front; the rollback below covers
    /// failures that can still occur while committing, such as permissions or a
    /// disk error on a later client.
    private func editableTargets(for clients: [ClientID]) throws -> [EditableTarget] {
        try clients.map { clientID in
            guard let descriptor = descriptors.first(where: { $0.id == clientID }),
                  let source = descriptor.editableServerMap
            else { throw AuthoringError.noSourceForClient(clientID) }

            let url = resolver.resolveServerMap(source.file, for: clientID)
            let pathDisplay = resolver.displayServerMap(source.file, for: clientID)
            let snapshot = try snapshot(
                of: url,
                clientID: clientID,
                pathDisplay: pathDisplay,
                format: source.format
            )
            return EditableTarget(clientID: clientID, source: source, snapshot: snapshot)
        }
    }

    private func denyListSnapshots(for name: String, in clients: [ClientID]) throws -> [String: FileSnapshot] {
        var result: [String: FileSnapshot] = [:]
        for clientID in clients {
            guard let descriptor = descriptors.first(where: { $0.id == clientID }),
                  let source = descriptor.editableServerMap,
                  case .denyList(let file, _, _) = source.enablement
            else { continue }

            let url = file.resolve(home: home)
            guard FileManager.default.fileExists(atPath: url.path) else { continue }
            let key = url.standardizedFileURL.path
            if result[key] == nil {
                result[key] = try snapshot(
                    of: url,
                    clientID: clientID,
                    pathDisplay: file.displayString(),
                    format: .json
                )
            }
        }
        return result
    }

    private func snapshot(
        of url: URL,
        clientID: ClientID,
        pathDisplay: String,
        format: ConfigFormat
    ) throws -> FileSnapshot {
        let exists = FileManager.default.fileExists(atPath: url.path)
        let digest = ConfigWriter.digest(of: url)
        guard digest == ledger.digest(for: url) else {
            throw ConfigWriteError.changedOnDisk(pathDisplay: pathDisplay)
        }

        guard exists else {
            return FileSnapshot(url: url, clientID: clientID, pathDisplay: pathDisplay, text: nil, digest: nil)
        }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else {
            throw ConfigWriteError.unreadable(pathDisplay: pathDisplay, reason: "not valid UTF-8")
        }
        do {
            switch format {
            case .json: _ = try JSONDocument.parse(text)
            case .toml: _ = try TOMLDocument.parse(text)
            }
        } catch {
            throw ConfigWriteError.unreadable(pathDisplay: pathDisplay, reason: "\(error)")
        }
        return FileSnapshot(url: url, clientID: clientID, pathDisplay: pathDisplay, text: text, digest: digest)
    }

    private func record(
        _ receipt: WriteReceipt,
        for snapshot: FileSnapshot,
        in committed: inout [CommittedWrite]
    ) {
        ledger.record(receipt.digest, for: snapshot.url)
        if receipt.didWrite {
            committed.append(CommittedWrite(snapshot: snapshot, writtenDigest: receipt.digest))
        }
    }

    /// Restores only files that still contain the bytes Kytto just wrote. If
    /// another process changed one after our write, refusing to overwrite that
    /// newer work is safer than pretending the rollback was complete.
    private func rollBack(_ committed: [CommittedWrite], after originalError: Error) throws -> Never {
        var failures: [String] = []
        for write in committed.reversed() {
            do {
                guard ConfigWriter.digest(of: write.snapshot.url) == write.writtenDigest else {
                    throw ConfigWriteError.changedOnDisk(pathDisplay: write.snapshot.pathDisplay)
                }

                if let text = write.snapshot.text {
                    try AtomicWriter.write(text, to: write.snapshot.url)
                } else if FileManager.default.fileExists(atPath: write.snapshot.url.path) {
                    try FileManager.default.removeItem(at: write.snapshot.url)
                }
                ledger.record(write.snapshot.digest, for: write.snapshot.url)
            } catch {
                failures.append("\(write.snapshot.pathDisplay): \(error.localizedDescription)")
            }
        }

        guard failures.isEmpty else {
            throw AuthoringError.rollbackFailed(
                original: originalError.localizedDescription,
                rollback: failures.joined(separator: " ")
            )
        }
        throw originalError
    }

    /// The name this server goes by in this particular file.
    ///
    /// The matrix merges rows by identity, so a server called `GitHub` in one
    /// client and `github` in another is one row — and removing it from the
    /// second has to use the spelling that file actually contains.
    private static func name(of server: Server, in document: TOMLDocument, under key: String) -> String {
        document.serverNames(under: key).first { Server.identity(for: $0) == server.id } ?? server.name
    }

    private func takenNames(in clients: [ClientID], from existing: [Server]) -> Set<String> {
        let wanted = Set(clients)
        return Set(
            existing
                .filter { server in
                    server.enabledIn.contains { wanted.contains($0.key) && $0.value != .absent }
                }
                .map(\.id)
        )
    }
}
