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
    /// The client only accepts Streamable HTTP for remote servers.
    case sseNotAccepted(client: String)

    public var errorDescription: String? {
        switch self {
        case .sseNotAccepted(let client):
            "\(client) only connects to remote servers over Streamable HTTP, not the legacy HTTP+SSE transport. Use the server's Streamable HTTP endpoint, or leave \(client) out."
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
        /// The exact bytes, for rollback. Text decoded from them would lose a
        /// byte-order mark, and a rollback has to put back what was there.
        let data: Data?
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

        return try write(draft, to: clients, replacing: nil, requireAbsent: true)
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

        // A rename changes the key, and only the key. "github" → "GitHub" is a
        // rename too: the files should say what the user typed.
        let renamedFrom = originalName == draft.trimmedName ? nil : originalName
        let identityChanged = Server.identity(for: originalName) != Server.identity(for: draft.trimmedName)

        // A copy that is switched off in a presence-only client is not in that
        // file. Writing the edit there would switch it back on while the matrix
        // still showed it off — and the next click on "on" would then overwrite
        // the edit with the old parked bytes. It is edited where it lives, as
        // `unify` does (§7.12).
        let parked = clients.filter { isParked(server, in: $0) }
        let inFile = clients.filter { !parked.contains($0) }

        var result = inFile.isEmpty
            ? AuthoringResult(serverName: draft.trimmedName, changed: [], backupIDs: [:], pathDisplays: [:])
            : try write(draft, to: inFile, replacing: renamedFrom)

        for clientID in parked {
            guard let entry = parkStore.parked(clientID: clientID, serverName: server.name),
                  descriptors.first(where: { $0.id == clientID })?.editableServerMap?.format == .json,
                  let text = try? draft.splicingJSON(into: entry.sourceText, at: [], schema: schema(of: clientID)),
                  (try? parkStore.park(
                      clientID: clientID,
                      serverName: renamedFrom == nil ? entry.serverName : draft.trimmedName,
                      sourceText: text
                  )) != nil
            else {
                result.parkedFailures.append(clientID)
                continue
            }
            // The park store is keyed by identity, so a rename is a new entry
            // and the old one has to go or the row would come back twice.
            if renamedFrom != nil, identityChanged {
                try? parkStore.unpark(clientID: clientID, serverName: entry.serverName)
            }
            result.parkedUpdated.append(clientID)
        }
        return result
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
            if isParked(server, in: clientID) {
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

            // Spliced into the held bytes rather than rebuilt, for the same
            // reason a file is (§6.3): what the draft does not describe is kept.
            do {
                guard let entry = parkStore.parked(clientID: clientID, serverName: server.name) else {
                    throw AuthoringError.notFound(server.name)
                }
                try parkStore.park(
                    clientID: clientID,
                    serverName: entry.serverName,
                    sourceText: draft.splicingJSON(into: entry.sourceText, at: [], schema: schema(of: clientID))
                )
                result.parkedUpdated.append(clientID)
            } catch {
                result.parkedFailures.append(clientID)
            }
        }

        return result
    }

    // MARK: - One argument

    /// One argument to change in one client's copy of a server.
    public struct ArgumentEdit: Equatable, Sendable {
        /// Position in that copy's `args`, counting strings only.
        public let index: Int
        /// What the argument says now; the edit is refused if it says otherwise.
        public let expected: String
        public let replacement: String

        public init(index: Int, expected: String, replacement: String) {
            self.index = index
            self.expected = expected
            self.replacement = replacement
        }
    }

    /// Replaces a single `args` element in each listed client's copy of
    /// `server`, and nothing else.
    ///
    /// The narrow sibling of `update`: that re-renders the whole definition,
    /// which is right for an edit the user typed and wrong for a repair that
    /// changes one token (§7.10). Here every file sees one string literal
    /// spliced, inside the same transaction as any other authoring write —
    /// preflight, backup, atomic write, rollback (§6). A switched-off copy Kytto
    /// is holding is patched too, or switching it back on would restore the old
    /// argument.
    public func replaceArgument(
        of server: Server,
        edits: [ClientID: ArgumentEdit]
    ) throws -> AuthoringResult {
        for clientID in edits.keys where server.definitionsByClient[clientID]?.isBundled == true {
            throw AuthoringError.notEditable(server.name)
        }

        let ordered = edits.keys.sorted { $0.rawValue < $1.rawValue }
        let parked = ordered.filter { isParked(server, in: $0) }
        let inFile = ordered.filter { !parked.contains($0) }

        let targets = try editableTargets(for: inFile)
        var changed: [ClientID] = []
        var backupIDs: [ClientID: String] = [:]
        var pathDisplays: [ClientID: String] = [:]
        var committed: [CommittedWrite] = []

        do {
            for target in targets {
                guard let edit = edits[target.clientID] else { continue }
                let source = target.source
                let url = target.snapshot.url
                let expecting = ledger.digest(for: url)

                let receipt: WriteReceipt = switch source.format {
                case .json:
                    try writer.edit(
                        url: url, clientID: target.clientID,
                        pathDisplay: target.snapshot.pathDisplay, expecting: expecting
                    ) { document in
                        let name = try document.serverKey(for: server.id, under: source.serversKey) ?? server.name
                        // OpenCode keeps the arguments after the command in one
                        // array, so the model's argument N is element N + 1.
                        let isArgv = schema(of: target.clientID).shape == .openCode
                        return try document.replacingString(
                            inArrayAt: [source.serversKey, name, isArgv ? "command" : "args"],
                            position: isArgv ? edit.index + 1 : edit.index,
                            expecting: edit.expected,
                            with: edit.replacement
                        )
                    }
                case .toml:
                    try writer.edit(
                        url: url, clientID: target.clientID,
                        pathDisplay: target.snapshot.pathDisplay, expecting: expecting,
                        as: TOMLDocument.self
                    ) { document in
                        try document.settingServerArgument(
                            at: edit.index,
                            expecting: edit.expected,
                            to: edit.replacement,
                            forServer: Self.name(of: server, in: document, under: source.serversKey),
                            under: source.serversKey
                        )
                    }
                }
                record(receipt, for: target.snapshot, in: &committed)
                if receipt.didWrite { changed.append(target.clientID) }
                if let backupID = receipt.backupID { backupIDs[target.clientID] = backupID }
                pathDisplays[target.clientID] = receipt.pathDisplay
            }
        } catch {
            try rollBack(committed, after: error)
        }

        var result = AuthoringResult(
            serverName: server.name, changed: changed, backupIDs: backupIDs, pathDisplays: pathDisplays
        )
        // Same stance as `unify`: the config writes have landed, so a held copy
        // that cannot be patched is reported rather than thrown.
        for clientID in parked {
            guard let edit = edits[clientID],
                  let entry = parkStore.parked(clientID: clientID, serverName: server.name),
                  descriptors.first(where: { $0.id == clientID })?.editableServerMap?.format == .json,
                  let text = try? JSONDocument.parse(entry.sourceText).replacingString(
                      inArrayAt: ["args"],
                      position: edit.index,
                      expecting: edit.expected,
                      with: edit.replacement
                  ),
                  (try? parkStore.park(clientID: clientID, serverName: entry.serverName, sourceText: text)) != nil
            else {
                result.parkedFailures.append(clientID)
                continue
            }
            result.parkedUpdated.append(clientID)
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
        guard !clients.contains(where: { server.definitionsByClient[$0]?.isBundled ?? server.isBundled }) else {
            throw AuthoringError.notEditable(server.name)
        }
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
                        // The spelling this file uses, which need not be the row's.
                        guard let key = try document.serverKey(for: server.id, under: source.serversKey) else {
                            return document.sourceText
                        }
                        return try document.removingMember(key, at: [source.serversKey])
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
        replacing oldName: String?,
        requireAbsent: Bool = false
    ) throws -> AuthoringResult {
        if draft.transport == .sse,
           let refusing = descriptors.first(where: { clients.contains($0.id) && !$0.serverSchema.acceptsSSE }) {
            throw AuthoringError.sseNotAccepted(client: refusing.displayName)
        }
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
                        // Already here: splice the draft into the definition the
                        // file has, under the spelling it has (§6.3).
                        let identity = Server.identity(for: oldName ?? draft.trimmedName)
                        if let key = try document.serverKey(for: identity, under: source.serversKey) {
                            if requireAbsent { throw AuthoringError.invalid([.nameTaken(draft.trimmedName)]) }
                            var text = document.sourceText
                            var name = key
                            if oldName != nil {
                                let clash = try document.serverKey(
                                    for: Server.identity(for: draft.trimmedName), under: source.serversKey
                                )
                                guard clash == nil || clash == key else {
                                    throw AuthoringError.invalid([.nameTaken(draft.trimmedName)])
                                }
                                // A rename is the key and nothing else.
                                let member = try JSONDocument.soleMember(
                                    key, in: document.value(at: [source.serversKey])?.members ?? [],
                                    at: [source.serversKey]
                                )
                                guard let member else { throw JSONEditError.pathNotFound([source.serversKey, key]) }
                                text = document.replacing(member.keySpan, with: JSONText.string(draft.trimmedName))
                                name = draft.trimmedName
                                // Tool switches that name the server move with it.
                                if case .prefixedToolMap(let mapKey)? = descriptors.first(where: { $0.id == clientID })?.toolSwitch {
                                    text = try ToolSwitch.renamingMapRules(
                                        in: text, mapKey: mapKey, serversKey: source.serversKey,
                                        from: key, to: draft.trimmedName
                                    )
                                }
                            }
                            return try draft.splicingJSON(into: text, at: [source.serversKey, name], schema: schema(of: clientID))
                        }

                        // New to this file: the definition has to be rendered.
                        let current = document

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
                            to: draft.definition(baseIndent: baseIndent, unit: unit, schema: schema(of: clientID))
                        )
                    }
                case .toml:
                    try writer.edit(
                        url: url, clientID: clientID, pathDisplay: pathDisplay, expecting: expecting,
                        as: TOMLDocument.self
                    ) { document in
                        // Spliced into the server's own tables, so `enabled = false`
                        // and every key a draft does not describe stay put (§6.3).
                        let identity = Server.identity(for: oldName ?? draft.trimmedName)
                        if let existing = try document.serverName(for: identity, under: source.serversKey) {
                            if requireAbsent { throw AuthoringError.invalid([.nameTaken(draft.trimmedName)]) }
                            var text = document.sourceText
                            var name = existing
                            if oldName != nil {
                                let clash = try document.serverName(
                                    for: Server.identity(for: draft.trimmedName), under: source.serversKey
                                )
                                guard clash == nil || clash == existing else {
                                    throw AuthoringError.invalid([.nameTaken(draft.trimmedName)])
                                }
                                text = try document.renamingServer(
                                    existing, to: draft.trimmedName, under: source.serversKey
                                )
                                name = draft.trimmedName
                            }
                            return try draft.splicingTOML(into: text, server: name, under: source.serversKey)
                        }
                        return try document.settingServer(
                            draft.trimmedName,
                            under: source.serversKey,
                            to: draft.tomlBlock(under: source.serversKey, enabled: nil)
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
                // A server switched off through a deny list stays off under its
                // new name: the entry is renamed, not dropped.
                try clearDenyListEntries(
                    for: oldName,
                    in: clients,
                    snapshots: denySnapshots,
                    committed: &committed,
                    renamingTo: draft.trimmedName
                )
            }
        } catch {
            try rollBack(committed, after: error)
        }

        if let oldName, Server.identity(for: oldName) != Server.identity(for: draft.trimmedName) {
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
    ///
    /// - Parameter newName: for a rename, the entry is respelt instead, one
    ///   string splice, so the server is still switched off afterwards.
    private func clearDenyListEntries(
        for name: String,
        in clients: [ClientID],
        snapshots: [String: FileSnapshot],
        committed: inout [CommittedWrite],
        renamingTo newName: String? = nil
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
                func names(_ element: JSONNode) -> Bool {
                    let entry = element[nameField]?.stringValue ?? element.stringValue
                    return entry.map { Server.identity(for: $0) == Server.identity(for: name) } ?? false
                }
                guard let newName else {
                    return try document.removingElements(fromArrayAt: [key], where: names)
                }
                let edits = (document.value(at: [key])?.elements ?? []).filter(names).map { element in
                    (span: (element[nameField] ?? element).span, replacement: JSONText.string(newName))
                }
                let renamed = document.replacing(edits)
                // Single-tool deny rules in the same file name the server too;
                // left behind, the tools they switched off would come back on.
                guard case .permissionRules(let rulesFile, let path)? = descriptor.toolSwitch,
                      rulesFile.resolve(home: home).standardizedFileURL == url.standardizedFileURL
                else { return renamed }
                return try ToolSwitch.renamingPermissionRules(in: renamed, path: path, from: name, to: newName)
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
            return FileSnapshot(url: url, clientID: clientID, pathDisplay: pathDisplay, data: nil, digest: nil)
        }
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8)
        else {
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
        return FileSnapshot(url: url, clientID: clientID, pathDisplay: pathDisplay, data: data, digest: digest)
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

                if let data = write.snapshot.data {
                    try AtomicWriter.write(data, to: write.snapshot.url, expecting: .digest(write.writtenDigest))
                } else {
                    // The file did not exist. If the path is a symbolic link whose
                    // target was missing, the write created the target: remove
                    // that, and leave the user's link as it was.
                    let created = try AtomicWriter.resolvingSymbolicLinks(write.snapshot.url)
                    if FileManager.default.fileExists(atPath: created.path) {
                        try FileManager.default.removeItem(at: created)
                    }
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

    /// Whether this client's copy of `server` is one Kytto is holding rather
    /// than one in the file. Both have to be true: a park the file has since
    /// overtaken — the definition put back by hand — is stale, and the file wins.
    private func schema(of clientID: ClientID) -> ServerSchema {
        descriptors.first { $0.id == clientID }?.serverSchema ?? .standard
    }

    private func isParked(_ server: Server, in clientID: ClientID) -> Bool {
        server.enabledIn[clientID] == .disabled
            && parkStore.parked(clientID: clientID, serverName: server.name) != nil
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
