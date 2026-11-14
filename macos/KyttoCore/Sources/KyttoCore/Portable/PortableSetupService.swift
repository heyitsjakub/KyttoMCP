import Foundation

/// File-based import/export. Resolved inputs and config bytes remain native;
/// callers can pass only the opaque preview id across IPC (§3.1, §6).
public final class PortableSetupService: @unchecked Sendable {
    public static let maximumFileBytes = 2_000_000
    private let home: URL
    private let paths: KyttoPaths
    private let writer: ConfigWriter
    private let parkStore: ParkStore
    private let ledger: DigestLedger
    private let descriptors: [ClientDescriptor]
    private let resolver: ClientPathResolver
    private let profileStore: ProfileStore
    private let metadataStore: MetadataStore
    private var plans: [String: Plan] = [:]
    // Protected by MutationCoordinator. Legacy pending arrays have no local
    // timestamp; require a check after first observation in this app session.
    nonisolated(unsafe) private static var legacyPendingDates: [String: (digest: String, since: Date)] = [:]

    private struct PendingImport: Codable {
        let reference: PortableContractReference
        let importedAt: Date
    }

    private struct Snapshot {
        let url: URL
        let data: Data?
        let digest: String?
        let display: String
    }

    private struct FileEdit {
        let snapshot: Snapshot
        let clientID: ClientID
        let format: ConfigFormat
        var updated: String
    }

    private struct ParkEdit {
        let clientID: ClientID
        let name: String
        let text: String
    }

    private struct Plan {
        let preview: PortableImportPreview
        let source: Snapshot
        let edits: [FileEdit]
        let parks: [ParkEdit]
        let profiles: [PortableProfile]
        let contracts: [PortableContractReference]
        let owned: [Snapshot]
        let createdAt: Date
    }

    private var pendingContractsURL: URL { paths.root.appending(path: "pending-contract-imports.json") }

    public init(
        home: URL,
        writer: ConfigWriter,
        parkStore: ParkStore,
        ledger: DigestLedger,
        descriptors: [ClientDescriptor] = ClientRegistry.all,
        pathOverrides: [String: String] = [:]
    ) {
        self.home = home
        paths = KyttoPaths(home: home)
        self.writer = writer
        self.parkStore = parkStore
        self.ledger = ledger
        self.descriptors = descriptors
        resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        profileStore = ProfileStore(paths: paths)
        metadataStore = MetadataStore(paths: paths)
    }

    /// The native save panel chooses `file`. No Keychain lookup is performed.
    @discardableResult
    public func export(
        to file: URL,
        servers: [Server],
        includeReviewedContracts: Bool = false
    ) throws -> PortableSetupSummary {
        try MutationCoordinator.sync {
            try refuseProtectedExportLocation(file)
            var requirements: [PortableRequirement] = []
            func required(_ label: String, _ kind: PortableRequirement.Kind) -> PortableValue {
                let id = UUID().uuidString.lowercased()
                requirements.append(PortableRequirement(id: id, label: label, kind: kind))
                return .required(id)
            }
            var exported: [PortableServer] = []
            var omitted = 0
            for server in servers.sorted(by: { $0.id < $1.id }) {
                var copies = server.definitionsByClient
                if copies.isEmpty, case .configFile(let clientID) = server.origin {
                    copies[clientID] = ClientDefinition(from: server)
                }
                for (clientID, definition) in copies.sorted(by: { $0.key.rawValue < $1.key.rawValue }) {
                    guard clientID.isBuiltIn, !definition.isBundled,
                          descriptors.first(where: { $0.id == clientID })?.editableServerMap != nil,
                          server.enabledIn[clientID] != .absent
                    else { omitted += 1; continue }
                    let label = "\(server.name) (\(clientID.rawValue))"
                    let requirementsBefore = requirements.count
                    // Only exact, known launcher names and harmless flags are
                    // public. A generic argument may itself be a bearer token,
                    // even when no flag says "token" (§6.5).
                    let command: PortableValue? = definition.command.map {
                        Self.safeCommands.contains($0) ? .literal($0) : required("\(label): command", .path)
                    }
                    // The package is public only beside a public launcher. With the
                    // launcher itself an input (`/opt/homebrew/bin/npx`), import
                    // cannot tell which argument names a package, so it is an
                    // input too — otherwise the file fails its own validation.
                    let packageIndex = definition.command.map(Self.safeCommands.contains) == true
                        ? Self.safePackageIndex(command: definition.command, args: definition.args)
                        : nil
                    let args = definition.args.enumerated().map { index, argument in
                        Self.safeArguments.contains(argument) || index == packageIndex
                            ? PortableValue.literal(argument)
                            : required("\(label): argument \(index + 1)", Self.isPath(argument) ? .path : .value)
                    }
                    var env: [String: PortableValue] = [:]
                    for entry in definition.env { env[entry.key] = required("\(label): environment \(entry.key)", .secret) }
                    var headers: [String: PortableValue] = [:]
                    for key in Self.headerNames(in: definition.definitionSource, clientID: clientID) {
                        headers[key] = required("\(label): header \(key)", .secret)
                    }
                    let cwd = definition.workingDirectory ?? Self.workingDirectory(in: definition.definitionSource, clientID: clientID)
                    let copy = PortableServer(
                        name: server.name, sourceClient: clientID,
                        wasEnabled: server.enabledIn[clientID] == .enabled,
                        transport: definition.transport, command: command,
                        args: args, environment: env, headers: headers,
                        // Every URL is a required input: credentials can hide
                        // in userinfo, query, fragments *or path segments*.
                        url: definition.url.map { _ in required("\(label): remote URL", .secret) },
                        workingDirectory: cwd.map { _ in required("\(label): working directory", .path) }
                    )
                    // One copy the format cannot express (a name with a space, a
                    // stdio entry that also has a url) is left out and counted,
                    // rather than failing the export of everything else.
                    let ownRequirements = Array(requirements[requirementsBefore...])
                    do {
                        try Self.validate(PortableSetup(
                            format: "kytto-portable-setup", version: PortableSetup.currentVersion,
                            servers: [copy], profiles: [], requirements: ownRequirements, reviewedContracts: []
                        ))
                    } catch {
                        requirements.removeLast(requirements.count - requirementsBefore)
                        omitted += 1
                        continue
                    }
                    exported.append(copy)
                }
            }
            let names = Set(exported.map { Server.identity(for: $0.name) })
            let profiles = try localProfiles().map {
                PortableProfile(name: $0.name, serverIDs: $0.serverIDs.filter(names.contains), tokenBudget: $0.tokenBudget)
            }
            var contracts: [PortableContractReference] = []
            if includeReviewedContracts {
                for (id, metadata) in metadataStore.all().sorted(by: { $0.key < $1.key }) where names.contains(id) {
                    guard let tools = metadata.contractTools else { continue }
                    var hashes: [String: String] = [:]
                    for tool in tools { hashes[tool.name] = try Self.contractDigest(tool) }
                    contracts.append(PortableContractReference(serverID: id, toolDigests: hashes))
                }
            }
            let setup = PortableSetup(
                format: "kytto-portable-setup", version: PortableSetup.currentVersion,
                servers: exported, profiles: profiles, requirements: requirements, reviewedContracts: contracts
            )
            try Self.validate(setup)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            let data = try encoder.encode(setup)
            guard data.count <= Self.maximumFileBytes else { throw PortableSetupError.invalid("file is too large") }
            try AtomicWriter.write(data, to: file, permissions: KyttoPaths.privateFilePermissions)
            return Self.summary(setup, omitted: omitted)
        }
    }

    public func inspect(file: URL) throws -> PortableSetupSummary {
        Self.summary(try Self.read(file).setup)
    }

    /// All `values` come from native controls. Target paths come exclusively
    /// from the registry/current local overrides, never from the imported file.
    /// - Parameter existing: the servers discovered now. A name a target client
    ///   already has by another route — an extension, a plugin — is a conflict
    ///   too, not just one in its config file.
    public func preview(
        file: URL,
        targetClients: [ClientID],
        values: [String: String] = [:],
        enableImportedServers: Bool = false,
        existing: [Server] = []
    ) throws -> PortableImportPreview {
        try MutationCoordinator.sync {
            let read = try Self.read(file)
            let setup = read.setup
            guard !targetClients.isEmpty || setup.servers.isEmpty,
                  Set(targetClients).count == targetClients.count,
                  targetClients.allSatisfy(\.isBuiltIn)
            else { throw PortableSetupError.invalid("choose distinct writable target clients") }
            let knownInputs = Set(setup.requirements.map(\.id))
            guard Set(values.keys).isSubset(of: knownInputs) else { throw PortableSetupError.invalid("unknown required input") }
            // Only the copies that will actually be written need their inputs:
            // importing into Cursor alone must not demand the Codex copy's secrets.
            let neededInputs = Set(Self.selectedCopies(setup, for: targetClients).flatMap(Self.requirementIDs))
            let described = Dictionary(uniqueKeysWithValues: Self.describedRequirements(setup).map { ($0.id, $0.label) })
            for requirement in setup.requirements where neededInputs.contains(requirement.id) {
                guard let value = values[requirement.id], !value.isEmpty,
                      value.utf8.count <= 16_384, !value.contains("\0")
                else { throw PortableSetupError.missingInput(described[requirement.id] ?? requirement.label) }
                if requirement.kind == .path {
                    // Relative paths would silently change meaning on another
                    // machine. Native path mapping must resolve them first.
                    guard value.hasPrefix("/") else { throw PortableSetupError.invalid("mapped paths must be absolute") }
                }
            }
            func resolve(_ value: PortableValue) -> String {
                value.literal ?? values[value.requirementID ?? ""] ?? ""
            }
            let existingProfiles = try localProfiles()
            for profile in setup.profiles where existingProfiles.contains(where: {
                $0.name.localizedCaseInsensitiveCompare(profile.name) == .orderedSame
            }) { throw PortableSetupError.conflict("Profile \(profile.name)") }

            let grouped = Dictionary(grouping: setup.servers, by: { Server.identity(for: $0.name) })
            var edits: [FileEdit] = []
            var parks: [ParkEdit] = []
            var observed: [Snapshot] = []
            var destinationPaths = Set<String>()
            for clientID in targetClients.sorted(by: { $0.rawValue < $1.rawValue }) {
                guard let source = descriptors.first(where: { $0.id == clientID })?.editableServerMap else {
                    throw AuthoringError.noSourceForClient(clientID)
                }
                let url = resolver.resolveServerMap(source.file, for: clientID)
                let display = resolver.displayServerMap(source.file, for: clientID)
                let snapshot = try Self.snapshot(url, display: display)
                observed.append(snapshot)
                guard destinationPaths.insert(try AtomicWriter.resolvingSymbolicLinks(url).resolvingSymlinksInPath().path).inserted else {
                    throw PortableSetupError.invalid("two selected clients resolve to the same destination")
                }
                var text = try Self.text(snapshot, format: source.format)
                var denied: [String] = []
                for id in grouped.keys.sorted() {
                    guard let copies = grouped[id], let copy = copies.first(where: { $0.sourceClient == clientID }) ?? copies.first else { continue }
                    guard parkStore.parked(clientID: clientID, serverName: copy.name) == nil else {
                        throw PortableSetupError.conflict("\(copy.name) in \(clientID.rawValue) (parked)")
                    }
                    let exists: Bool
                    switch source.format {
                    case .json: exists = try JSONDocument.parse(text).serverKey(for: id, under: source.serversKey) != nil
                    case .toml: exists = try TOMLDocument.parse(text).serverName(for: id, under: source.serversKey) != nil
                    }
                    let elsewhere = existing.contains { $0.id == id && ($0.enabledIn[clientID] ?? .absent) != .absent }
                    guard !exists, !elsewhere else { throw PortableSetupError.conflict("\(copy.name) in \(clientID.rawValue)") }
                    let draft = ServerDraft(
                        name: copy.name, transport: copy.transport,
                        command: copy.command.map(resolve) ?? "", args: copy.args.map(resolve),
                        env: copy.environment.keys.sorted().map { EnvEntry(key: $0, value: resolve(copy.environment[$0]!)) },
                        url: copy.url.map(resolve) ?? ""
                    )
                    guard draft.validate().isEmpty else { throw PortableSetupError.invalid("invalid definition for \(copy.name)") }
                    let enabled = enableImportedServers && copy.wasEnabled
                    let schema = descriptors.first { $0.id == clientID }?.serverSchema ?? .standard
                    if source.format == .json, copy.transport == .sse, !schema.acceptsSSE {
                        let clientName = descriptors.first { $0.id == clientID }?.displayName ?? clientID.rawValue
                        throw PortableSetupError.invalid("\(copy.name) uses the legacy HTTP+SSE transport, which \(clientName) does not accept")
                    }
                    let definition = try Self.render(
                        copy, draft: draft, resolve: resolve, format: source.format, key: source.serversKey,
                        enabled: enabled, schema: schema, flag: source.enablement.inlineFlag
                    )
                    switch source.enablement {
                    case .presence where !enabled:
                        parks.append(ParkEdit(clientID: clientID, name: copy.name, text: definition))
                    case .presence, .inlineFlag, .inlineDisabledFlag, .denyList:
                        switch source.format {
                        case .json: text = try JSONDocument.parse(text).settingMember(copy.name, at: [source.serversKey], to: definition)
                        case .toml: text = try TOMLDocument.parse(text).settingServer(copy.name, under: source.serversKey, to: definition)
                        }
                        if case .denyList = source.enablement, !enabled { denied.append(copy.name) }
                    case .extensionFlag:
                        throw AuthoringError.noSourceForClient(clientID)
                    }
                }
                if text != (try Self.text(snapshot, format: source.format)) {
                    // Checked now, as the write will check it, so a preview never
                    // promises an import that apply then refuses.
                    switch source.format {
                    case .json: _ = try JSONDocument.parse(text)
                    case .toml: _ = try TOMLDocument.parse(text)
                    }
                    edits.append(FileEdit(snapshot: snapshot, clientID: clientID, format: source.format, updated: text))
                }
                if case .denyList(let denyFile, let key, let nameField) = source.enablement, !denied.isEmpty {
                    let denyURL = denyFile.resolve(home: home)
                    guard destinationPaths.insert(try AtomicWriter.resolvingSymbolicLinks(denyURL).resolvingSymlinksInPath().path).inserted else {
                        throw PortableSetupError.invalid("overlapping configuration destinations")
                    }
                    let denySnapshot = try Self.snapshot(denyURL, display: denyFile.displayString())
                    var denyText = try Self.text(denySnapshot, format: .json)
                    for name in denied {
                        let document = try JSONDocument.parse(denyText)
                        let already = document.root[key]?.elements?.contains {
                            ($0[nameField]?.stringValue ?? $0.stringValue).map { Server.identity(for: $0) == Server.identity(for: name) } ?? false
                        } ?? false
                        if !already {
                            denyText = try document.appendingElement(
                                document.objectLiteral([(nameField, JSONText.string(name))], matchingStyleOfArrayAt: [key]),
                                toArrayAt: [key]
                            )
                        }
                    }
                    edits.append(FileEdit(snapshot: denySnapshot, clientID: clientID, format: .json, updated: denyText))
                }
            }
            // Capture owned stores too, so another window's save between
            // preview and commit cannot be overwritten (§6.4).
            var owned = try [paths.parked, paths.profiles, pendingContractsURL].map { try Self.snapshot($0, display: $0.lastPathComponent) }
            if let data = owned[0].data {
                let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
                _ = try decoder.decode([String: ParkStore.Entry].self, from: data)
            }
            if owned[2].data != nil { _ = try pendingContractReferences() }
            owned.append(contentsOf: observed.filter { snapshot in !edits.contains { $0.snapshot.url == snapshot.url } })
            let id = UUID().uuidString.lowercased()
            var warnings = Self.summary(setup).warnings
            warnings.append(enableImportedServers
                ? "Only copies enabled in the exported setup will be enabled; disabled copies remain disabled."
                : "All imported servers start switched off. Review their commands before enabling them.")
            let preview = PortableImportPreview(
                id: id, serverNames: grouped.values.compactMap(\.first?.name).sorted(), profileNames: setup.profiles.map(\.name),
                targetClients: targetClients, pathDisplays: edits.map { $0.snapshot.display },
                pendingContractCount: setup.reviewedContracts.count, warnings: warnings, enabled: enableImportedServers
            )
            // Bound the lifetime and number of native plans containing inputs.
            plans = plans.filter { Date().timeIntervalSince($0.value.createdAt) < 600 }
            if plans.count >= 4 { plans.removeAll() }
            plans[id] = Plan(preview: preview, source: read.snapshot, edits: edits, parks: parks,
                             profiles: setup.profiles, contracts: setup.reviewedContracts, owned: owned, createdAt: Date())
            return preview
        }
    }

    public func discard(previewID: String) {
        MutationCoordinator.sync { _ = plans.removeValue(forKey: previewID) }
    }

    /// A preview can be committed exactly once, within ten minutes. This method
    /// neither starts servers nor stores any value in Keychain.
    public func apply(previewID: String) throws -> PortableImportResult {
        try MutationCoordinator.sync {
            guard let plan = plans.removeValue(forKey: previewID), Date().timeIntervalSince(plan.createdAt) < 600 else {
                throw PortableSetupError.stalePreview
            }
            for snapshot in [plan.source] + plan.edits.map(\.snapshot) + plan.owned {
                guard ConfigWriter.digest(of: snapshot.url) == snapshot.digest else { throw PortableSetupError.stalePreview }
            }
            var committed: [(Snapshot, String?)] = []
            var backupIDs: [String] = []
            var changedClients = Set<ClientID>()
            do {
                for edit in plan.edits {
                    let receipt: WriteReceipt
                    switch edit.format {
                    case .json:
                        receipt = try writer.edit(url: edit.snapshot.url, clientID: edit.clientID,
                                                  pathDisplay: edit.snapshot.display, expecting: edit.snapshot.digest) { _ in edit.updated }
                    case .toml:
                        receipt = try writer.edit(url: edit.snapshot.url, clientID: edit.clientID,
                                                  pathDisplay: edit.snapshot.display, expecting: edit.snapshot.digest, as: TOMLDocument.self) { _ in edit.updated }
                    }
                    if receipt.didWrite {
                        committed.append((edit.snapshot, receipt.digest)); changedClients.insert(edit.clientID)
                    }
                    if let backupID = receipt.backupID { backupIDs.append(backupID) }
                    ledger.record(receipt.digest, for: edit.snapshot.url)
                }
                for park in plan.parks {
                    try parkStore.park(clientID: park.clientID, serverName: park.name, sourceText: park.text)
                    Self.recordOwned(paths.parked, snapshots: plan.owned, in: &committed)
                }
                let profiles = try profileStore.importProfiles(plan.profiles)
                if !profiles.isEmpty { Self.recordOwned(paths.profiles, snapshots: plan.owned, in: &committed) }
                if !plan.contracts.isEmpty {
                    var pending = try pendingImports()
                    let incomingIDs = Set(plan.contracts.map(\.serverID))
                    pending.removeAll { incomingIDs.contains($0.reference.serverID) }
                    let importedAt = Date.timestamp()
                    pending.append(contentsOf: plan.contracts.map { PendingImport(reference: $0, importedAt: importedAt) })
                    let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
                    encoder.dateEncodingStrategy = .millisecondsSince1970
                    let pendingData = try encoder.encode(pending)
                    guard pending.count <= 1000, pendingData.count <= Self.maximumFileBytes else {
                        throw PortableSetupError.invalid("too many pending contract references")
                    }
                    try paths.createPrivateDirectory(paths.root)
                    try AtomicWriter.write(pendingData, to: pendingContractsURL, permissions: KyttoPaths.privateFilePermissions)
                    Self.recordOwned(pendingContractsURL, snapshots: plan.owned, in: &committed)
                }
                return PortableImportResult(
                    serverNames: plan.preview.serverNames, importedProfiles: profiles.map(\.name),
                    changedClients: changedClients.sorted { $0.rawValue < $1.rawValue }, backupIDs: backupIDs,
                    pendingContractCount: plan.contracts.count
                )
            } catch {
                var failures: [String] = []
                for (snapshot, writtenDigest) in committed.reversed() {
                    do {
                        guard ConfigWriter.digest(of: snapshot.url) == writtenDigest else { throw PortableSetupError.stalePreview }
                        if let data = snapshot.data { try AtomicWriter.write(data, to: snapshot.url, expecting: writtenDigest.map(AtomicWriter.Precondition.digest) ?? .absent) }
                        else {
                            let created = try AtomicWriter.resolvingSymbolicLinks(snapshot.url)
                            if FileManager.default.fileExists(atPath: created.path) { try FileManager.default.removeItem(at: created) }
                        }
                        ledger.record(snapshot.digest, for: snapshot.url)
                    } catch { failures.append(snapshot.display) }
                }
                if !failures.isEmpty { throw PortableSetupError.rollbackFailed(failures.joined(separator: ", ")) }
                throw error
            }
        }
    }

    /// Pending fingerprints are never consumed by MetadataStore.record or
    /// acknowledgeContract. A fresh real check/review establishes local trust.
    public func pendingContractReferences() throws -> [PortableContractReference] {
        try pendingImports().map(\.reference)
    }

    private func pendingImports() throws -> [PendingImport] {
        try MutationCoordinator.sync {
            guard FileManager.default.fileExists(atPath: pendingContractsURL.path) else { return [] }
            do {
                let handle = try FileHandle(forReadingFrom: pendingContractsURL)
                defer { try? handle.close() }
                let data = try handle.read(upToCount: Self.maximumFileBytes + 1) ?? Data()
                guard data.count <= Self.maximumFileBytes else { throw PortableSetupError.pendingContractsUnreadable }
                _ = try JSONSerialization.jsonObject(with: data)
                guard let text = String(data: data, encoding: .utf8),
                      let nodes = try JSONDocument.parse(text).root.elements, nodes.count <= 1000 else {
                    throw PortableSetupError.pendingContractsUnreadable
                }
                let isLegacy = nodes.first?.member("serverID") != nil
                for node in nodes {
                    let reference: JSONNode
                    if isLegacy { reference = node }
                    else {
                        guard node.keys.count == 2, Set(node.keys) == ["reference", "importedAt"],
                              let entry = node["reference"], node["importedAt"]?.numberValue != nil else {
                            throw PortableSetupError.pendingContractsUnreadable
                        }
                        reference = entry
                    }
                    let keys = reference.keys
                    guard Set(keys) == ["serverID", "toolDigests"], keys.count == 2,
                          let hashes = reference["toolDigests"]?.members,
                          Set(hashes.map(\.key)).count == hashes.count else {
                        throw PortableSetupError.pendingContractsUnreadable
                    }
                }
                let imports: [PendingImport]
                if isLegacy {
                    let references = try JSONDecoder().decode([PortableContractReference].self, from: data)
                    let key = pendingContractsURL.standardizedFileURL.path
                    let digest = ConfigWriter.digest(of: data)
                    let since: Date
                    if let previous = Self.legacyPendingDates[key], previous.digest == digest { since = previous.since }
                    else {
                        since = .timestamp()
                        if Self.legacyPendingDates.count >= 64 { Self.legacyPendingDates.removeAll() }
                        Self.legacyPendingDates[key] = (digest, since)
                    }
                    imports = references.map { PendingImport(reference: $0, importedAt: since) }
                } else {
                    let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .millisecondsSince1970
                    imports = try decoder.decode([PendingImport].self, from: data)
                }
                var ids = Set<String>()
                for entry in imports {
                    let reference = entry.reference
                    guard !reference.serverID.isEmpty, reference.serverID.utf8.count <= 256,
                          reference.serverID == Server.identity(for: reference.serverID),
                          reference.serverID.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }),
                          ids.insert(reference.serverID).inserted, reference.toolDigests.count <= 2048,
                          reference.toolDigests.allSatisfy({ !$0.key.isEmpty && $0.key.utf8.count <= 256 && $0.value.wholeMatch(of: /[0-9a-f]{64}/) != nil }),
                          entry.importedAt.timeIntervalSince1970.isFinite, entry.importedAt.timeIntervalSince1970 >= 0,
                          entry.importedAt <= Date().addingTimeInterval(60) else {
                        throw PortableSetupError.pendingContractsUnreadable
                    }
                }
                return imports
            } catch { throw PortableSetupError.pendingContractsUnreadable }
        }
    }

    public func pendingContractSummary() throws -> [PortablePendingContractSummary] {
        try MutationCoordinator.sync {
            let imports = try pendingImports()
            guard !imports.isEmpty else { return [] }
            let metadata = try localContractMetadata()
            return try imports.sorted { $0.reference.serverID < $1.reference.serverID }.map { entry in
                let reference = entry.reference
                let matching: Bool?
                if let health = metadata[reference.serverID]?.health, health.status == .passed, health.checkedAt > entry.importedAt {
                    var hashes: [String: String] = [:]
                    // Match ContractGuard's last-wins treatment of duplicate
                    // tool names; array presentation order is not a contract.
                    for tool in health.tools { hashes[tool.name] = try Self.contractDigest(tool) }
                    matching = hashes == reference.toolDigests
                } else { matching = nil }
                return PortablePendingContractSummary(serverID: reference.serverID, toolCount: reference.toolDigests.count, matching: matching)
            }
        }
    }

    /// Removes review evidence after explicit review of the observed local
    /// contract. It never copies imported hashes or tool definitions into
    /// MetadataStore. Call normal acknowledgeContract first if it has a diff.
    public func reviewPendingContract(serverID: String) throws {
        try MutationCoordinator.sync {
            var imports = try pendingImports()
            let id = Server.identity(for: serverID)
            guard let pending = imports.first(where: { $0.reference.serverID == id }) else { return }
            guard let health = try localContractMetadata()[id]?.health, health.status == .passed, health.checkedAt > pending.importedAt else {
                throw PortableSetupError.localContractNotChecked
            }
            imports.removeAll { $0.reference.serverID == id }
            let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .millisecondsSince1970
            try paths.createPrivateDirectory(paths.root)
            try AtomicWriter.write(try encoder.encode(imports), to: pendingContractsURL, permissions: KyttoPaths.privateFilePermissions)
        }
    }

    /// Exactly the fields compared by ContractGuard. Token counts depend on
    /// tokenizer/presentation, so they must not alter an imported reference.
    static func contractDigest(_ tool: ToolSummary) throws -> String {
        struct Contract: Encodable {
            let version: Int
            let name: String
            let description: String?
            let schema: String?
            let annotations: ToolAnnotations?
        }
        let schema = tool.inputSchemaJSON.map { raw in
            guard let canonical = ContractGuard.canonical(raw),
                  let text = String(data: canonical, encoding: .utf8) else { return raw }
            return text
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return ConfigWriter.digest(of: try encoder.encode(Contract(version: 1, name: tool.name, description: tool.description,
                                                                    schema: schema, annotations: tool.annotations)))
    }

    private func localContractMetadata() throws -> [String: ServerMetadata] {
        let url = paths.root.appending(path: "server-metadata.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .custom { decoder in
                let value = try decoder.singleValueContainer().decode(String.self)
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                guard let date = formatter.date(from: value) else { throw PortableSetupError.localContractNotChecked }
                return date
            }
            return try decoder.decode([String: ServerMetadata].self, from: Data(contentsOf: url))
        } catch { throw PortableSetupError.localContractNotChecked }
    }

    private func localProfiles() throws -> [ServerProfile] {
        guard FileManager.default.fileExists(atPath: paths.profiles.path) else { return [] }
        return try JSONDecoder().decode([ServerProfile].self, from: Data(contentsOf: paths.profiles))
    }

    /// A save-panel destination is user selected, but an export must still not
    /// turn into an unbacked replacement of a live config or recovery store.
    private func refuseProtectedExportLocation(_ file: URL) throws {
        let destination = try AtomicWriter.resolvingSymbolicLinks(file).resolvingSymlinksInPath().standardizedFileURL.path
        var protected = [paths.root.resolvingSymlinksInPath().standardizedFileURL.path]
        for descriptor in descriptors {
            protected.append(contentsOf: descriptor.sources.map { $0.file.resolve(home: home).resolvingSymlinksInPath().standardizedFileURL.path })
            if let source = descriptor.editableServerMap {
                protected.append(resolver.resolveServerMap(source.file, for: descriptor.id).resolvingSymlinksInPath().standardizedFileURL.path)
                if case .denyList(let file, _, _) = source.enablement {
                    protected.append(file.resolve(home: home).resolvingSymlinksInPath().standardizedFileURL.path)
                }
            }
        }
        // macOS volumes are usually case-insensitive: `~/.cursor/MCP.json` is
        // the live config, whatever its spelling.
        let lowered = destination.lowercased()
        guard !protected.map({ $0.lowercased() }).contains(where: { lowered == $0 || lowered.hasPrefix($0 + "/") }) else {
            throw PortableSetupError.invalid("choose an export file outside client configuration and Kytto's data folders")
        }
    }

    private static func recordOwned(_ url: URL, snapshots: [Snapshot], in committed: inout [(Snapshot, String?)]) {
        guard let snapshot = snapshots.first(where: { $0.url == url }) else { return }
        // Several parked entries share one store; rollback must restore its
        // original bytes once, guarded by the final digest.
        committed.removeAll { $0.0.url == url }
        committed.append((snapshot, ConfigWriter.digest(of: url)))
    }

    private static func snapshot(_ url: URL, display: String) throws -> Snapshot {
        if !FileManager.default.fileExists(atPath: url.path) {
            return Snapshot(url: url, data: nil, digest: nil, display: display)
        }
        let data = try Data(contentsOf: url)
        return Snapshot(url: url, data: data, digest: ConfigWriter.digest(of: data), display: display)
    }

    private static func text(_ snapshot: Snapshot, format: ConfigFormat) throws -> String {
        guard let data = snapshot.data else { return format == .json ? JSONDocument.emptySource : TOMLDocument.emptySource }
        guard let text = String(data: data, encoding: .utf8) else {
            throw ConfigWriteError.unreadable(pathDisplay: snapshot.display, reason: "not valid UTF-8")
        }
        switch format {
        case .json: _ = try JSONDocument.parse(text)
        case .toml: _ = try TOMLDocument.parse(text)
        }
        return text
    }

    private static func render(
        _ copy: PortableServer, draft: ServerDraft, resolve: (PortableValue) -> String,
        format: ConfigFormat, key: String, enabled: Bool,
        schema: ServerSchema = .standard, flag: (key: String, onValue: Bool)? = nil
    ) throws -> String {
        switch format {
        case .json:
            var text = draft.definition(baseIndent: "", unit: "  ", schema: schema)
            if let cwd = copy.workingDirectory { text = try JSONDocument.parse(text).settingMember("cwd", at: [], to: JSONText.string(resolve(cwd))) }
            if !copy.headers.isEmpty {
                let fields = copy.headers.keys.sorted().map { JSONBuildValue.Field($0, .string(resolve(copy.headers[$0]!))) }
                text = try JSONDocument.parse(text).settingMember("headers", at: [], to: JSONBuilder.render(.object(fields)))
            }
            // A client whose off switch sits on the entry gets it there, so a
            // server imported switched off arrives switched off.
            if !enabled, let flag {
                text = try JSONDocument.parse(text).settingMember(flag.key, at: [], to: JSONText.bool(!flag.onValue))
            }
            return text
        case .toml:
            // Codex tells transports apart by key, not by a `type`: a remote
            // server is a `url`, as `ServerAuthoring` writes it too. A Codex URL
            // that merely ends in /sse was inferred as SSE on export.
            var text = draft.tomlBlock(under: key, enabled: enabled ? nil : false)
            // Insert scalar cwd before the optional env subtable.
            if let cwd = copy.workingDirectory {
                let line = "cwd = \(JSONText.string(resolve(cwd)))\n"
                let lines = text.split(separator: "\n", omittingEmptySubsequences: false)
                text = ([String(lines.first ?? "")] + [line.trimmingCharacters(in: .newlines)] + lines.dropFirst().map(String.init)).joined(separator: "\n")
            }
            if !copy.headers.isEmpty {
                let table = "[\(key).\(TOMLText.key(copy.name)).http_headers]"
                text += "\n" + table + "\n"
                for header in copy.headers.keys.sorted() { text += "\(TOMLText.key(header)) = \(JSONText.string(resolve(copy.headers[header]!)))\n" }
            }
            return text
        }
    }

    private static let safeCommands: Set<String> = ["npx", "npm", "pnpm", "yarn", "bunx", "bun", "uvx", "uv", "pipx", "node", "python", "python3", "docker"]
    private static let safeArguments: Set<String> = ["-y", "--yes", "--no-install", "--ignore-scripts", "--offline", "--no-cache", "--package", "-p", "--from", "--spec", "--", "exec", "dlx", "x", "tool", "run", "-m", "--stdio", "-i", "--rm"]

    private static func isPath(_ value: String) -> Bool {
        value.hasPrefix("/") || value.hasPrefix("~/") || value.hasPrefix("./") || value.hasPrefix("../")
    }

    /// Registry package specs are structural launch information, not server
    /// arguments. Reuse Doctor's parser so a flag value or credential-bearing
    /// URL can never be mistaken for a package. Unusual tags/prereleases remain
    /// required inputs rather than risking a secret hidden in a version tag.
    private static func safePackageIndex(command: String?, args: [String]) -> Int? {
        guard let launch = PackageLaunch.parse(command: command, args: args), launch.argument.utf8.count <= 256,
              args.prefix(launch.argumentIndex).allSatisfy(safeArguments.contains) else { return nil }
        if let version = launch.requestedVersion,
           !["latest", "next", "beta", "alpha", "canary"].contains(version),
           version.wholeMatch(of: /[~^<>=!0-9.,* |]+/) == nil { return nil }
        return launch.argumentIndex
    }

    private static func headerNames(in text: String, clientID: ClientID) -> [String] {
        var names = Set<String>()
        if let document = try? JSONDocument.parse(text) {
            for key in ["headers", "http_headers", "env_http_headers"] { names.formUnion(document.root[key]?.keys ?? []) }
            if document.root["bearer_token_env_var"]?.stringValue != nil {
                names = names.filter { $0.lowercased() != "authorization" }
                names.insert("Authorization")
            }
            return names.sorted()
        }
        if clientID == .codex, let fields = tomlFields(text) {
            for key in ["http_headers", "env_http_headers"] {
                names.formUnion(fields.pairs.first { $0.name == key }?.value.inlinePairs?.compactMap(\.name) ?? [])
                if let table = fields.table {
                    for child in fields.document.tables where child.path == table.path + [key] {
                        names.formUnion(child.pairs.compactMap(\.name))
                    }
                }
            }
            if fields.pairs.first(where: { $0.name == "bearer_token_env_var" })?.value.stringValue != nil {
                names = names.filter { $0.lowercased() != "authorization" }
                names.insert("Authorization")
            }
        }
        return names.sorted()
    }

    private static func workingDirectory(in text: String, clientID: ClientID) -> String? {
        if let document = try? JSONDocument.parse(text) { return document.root["cwd"]?.stringValue }
        if clientID == .codex, let fields = tomlFields(text) {
            return fields.pairs.first { $0.name == "cwd" }?.value.stringValue
        }
        return nil
    }

    /// Discovery retains a complete table block, or the single `name = {…}`
    /// pair for an inline Codex server. Both need the same portable whitelist.
    private static func tomlFields(_ text: String) -> (document: TOMLDocument, table: TOMLTable?, pairs: [TOMLPair])? {
        guard let document = try? TOMLDocument.parse(text) else { return nil }
        if let table = document.tables.first(where: { $0.value("url") != nil || $0.value("command") != nil }) {
            return (document, table, table.pairs)
        }
        for table in document.tables {
            for pair in table.pairs {
                if let fields = pair.value.inlinePairs,
                   fields.contains(where: { $0.name == "url" || $0.name == "command" }) {
                    return (document, nil, fields)
                }
            }
        }
        return nil
    }

    /// The copy each target client receives: its own, else the first exported.
    private static func selectedCopies(_ setup: PortableSetup, for targets: [ClientID]) -> [PortableServer] {
        let grouped = Dictionary(grouping: setup.servers, by: { Server.identity(for: $0.name) })
        return targets.flatMap { client in
            grouped.values.compactMap { copies in copies.first { $0.sourceClient == client } ?? copies.first }
        }
    }

    private static func requirementIDs(_ server: PortableServer) -> [String] {
        ([server.command, server.url, server.workingDirectory] + server.args.map(Optional.some)
            + server.environment.values.map(Optional.some) + server.headers.values.map(Optional.some))
            .compactMap { $0?.requirementID }
    }

    private static func summary(_ setup: PortableSetup, omitted: Int = 0) -> PortableSetupSummary {
        var warnings = ["Environment/header values, arbitrary arguments, URLs and local paths require native input. Unknown fields and bundled plugins are excluded."]
        if omitted > 0 { warnings.append("\(omitted) \(omitted == 1 ? "definition was" : "definitions were") excluded: bundled, read-only, or not expressible in a setup file.") }
        if !setup.reviewedContracts.isEmpty { warnings.append("Reviewed contracts travel as fingerprints only and remain pending review after import.") }
        let groups = Dictionary(grouping: setup.servers, by: { Server.identity(for: $0.name) })
        if groups.values.contains(where: { $0.count > 1 }) {
            warnings.append("Each destination uses its original client's copy when available; otherwise the first exported copy is used.")
        }
        return PortableSetupSummary(serverNames: groups.values.compactMap(\.first?.name).sorted(), profileNames: setup.profiles.map(\.name),
                                    requirements: describedRequirements(setup), pendingContractCount: setup.reviewedContracts.count, warnings: warnings)
    }

    /// Requirement labels written from the file's structure, never its text.
    ///
    /// A shared setup file is untrusted: its own label could ask for "your
    /// GitHub token" on an input that feeds someone else's package. What the
    /// user is shown is where the value goes — which server, which field, and
    /// the public launcher it is handed to.
    static func describedRequirements(_ setup: PortableSetup) -> [PortableRequirement] {
        var labels: [String: String] = [:]
        for server in setup.servers {
            let launcher = ([server.command?.literal] + server.args.map(\.literal)).compactMap { $0 }.joined(separator: " ")
            let feeds = launcher.isEmpty ? "" : " — for \(launcher)"
            let base = "\(server.name) (\(server.sourceClient.rawValue))"
            func note(_ value: PortableValue?, _ field: String) {
                if let id = value?.requirementID { labels[id] = "\(base): \(field)\(feeds)" }
            }
            note(server.command, "command")
            for (index, argument) in server.args.enumerated() { note(argument, "argument \(index + 1)") }
            for (key, value) in server.environment { note(value, "environment \(key)") }
            for (key, value) in server.headers { note(value, "header \(key)") }
            note(server.url, "remote URL")
            note(server.workingDirectory, "working directory")
        }
        return setup.requirements.map { PortableRequirement(id: $0.id, label: labels[$0.id] ?? "Unused input", kind: $0.kind) }
    }

    // MARK: - Untrusted file validation

    private static func read(_ file: URL) throws -> (setup: PortableSetup, snapshot: Snapshot) {
        do {
            let handle = try FileHandle(forReadingFrom: file)
            defer { try? handle.close() }
            let data = try handle.read(upToCount: maximumFileBytes + 1) ?? Data()
            guard data.count <= maximumFileBytes else { throw PortableSetupError.invalid("file is too large") }
            // Foundation first rejects malformed/deep JSON before the
            // span-preserving parser is used to detect duplicate keys.
            _ = try JSONSerialization.jsonObject(with: data)
            guard let text = String(data: data, encoding: .utf8) else { throw PortableSetupError.invalid("UTF-8 is required") }
            let root = try JSONDocument.parse(text).root
            try validateStructure(root)
            let setup = try JSONDecoder().decode(PortableSetup.self, from: data)
            try validate(setup)
            return (setup, Snapshot(url: file, data: data, digest: ConfigWriter.digest(of: data), display: "Portable setup file"))
        } catch let error as PortableSetupError { throw error }
        catch { throw PortableSetupError.invalid("invalid JSON or unsupported fields") }
    }

    private static func validateStructure(_ root: JSONNode) throws {
        func object(_ node: JSONNode, keys: Set<String>?) throws {
            guard let members = node.members else { throw PortableSetupError.invalid("expected an object") }
            guard Set(members.map(\.key)).count == members.count else { throw PortableSetupError.invalid("duplicate JSON key") }
            if let keys, !Set(members.map(\.key)).isSubset(of: keys) { throw PortableSetupError.invalid("unknown field") }
        }
        try object(root, keys: ["format", "version", "servers", "profiles", "requirements", "reviewedContracts"])
        for server in root["servers"]?.elements ?? [] {
            try object(server, keys: ["name", "sourceClient", "wasEnabled", "transport", "command", "args", "environment", "headers", "url", "workingDirectory"])
            for key in ["command", "url", "workingDirectory"] {
                if let node = server[key], !node.isNull { try object(node, keys: ["literal", "requirementID"]) }
            }
            for node in server["args"]?.elements ?? [] { try object(node, keys: ["literal", "requirementID"]) }
            for key in ["environment", "headers"] {
                if let node = server[key] {
                    try object(node, keys: nil)
                    for member in node.members ?? [] { try object(member.value, keys: ["literal", "requirementID"]) }
                }
            }
        }
        for node in root["profiles"]?.elements ?? [] { try object(node, keys: ["name", "serverIDs", "tokenBudget"]) }
        for node in root["requirements"]?.elements ?? [] { try object(node, keys: ["id", "label", "kind"]) }
        for node in root["reviewedContracts"]?.elements ?? [] {
            try object(node, keys: ["serverID", "toolDigests"])
            if let hashes = node["toolDigests"] { try object(hashes, keys: nil) }
        }
    }

    private static func validate(_ setup: PortableSetup) throws {
        func validText(_ text: String, limit: Int = 256) -> Bool {
            !text.isEmpty && text.utf8.count <= limit
                && text.unicodeScalars.allSatisfy { !CharacterSet.controlCharacters.contains($0) }
        }
        guard setup.format == "kytto-portable-setup", setup.version == PortableSetup.currentVersion else {
            throw PortableSetupError.invalid("unsupported format or version")
        }
        guard setup.servers.count <= 200, setup.profiles.count <= 100,
              setup.requirements.count <= 4096, setup.reviewedContracts.count <= 200 else {
            throw PortableSetupError.invalid("too many entries")
        }
        var requirementMap: [String: PortableRequirement] = [:]
        for requirement in setup.requirements {
            guard UUID(uuidString: requirement.id) != nil, requirementMap[requirement.id] == nil,
                  validText(requirement.label, limit: 512) else { throw PortableSetupError.invalid("invalid or duplicate required input") }
            requirementMap[requirement.id] = requirement
        }
        var used = Set<String>()
        func value(_ entry: PortableValue, literals: Set<String>?, kind: PortableRequirement.Kind?) throws {
            if let literal = entry.literal {
                guard entry.requirementID == nil, let literals, literals.contains(literal) else {
                    throw PortableSetupError.invalid("only whitelisted launcher literals are allowed")
                }
            } else if let id = entry.requirementID, let requirement = requirementMap[id] {
                guard used.insert(id).inserted, kind == nil || requirement.kind == kind else {
                    throw PortableSetupError.invalid("reused required input or incorrect input kind")
                }
            } else { throw PortableSetupError.invalid("missing required input reference") }
        }
        var identities = Set<String>()
        var names = Set<String>()
        for server in setup.servers {
            let identity = Server.identity(for: server.name)
            guard validText(server.name), server.name == server.name.trimmingCharacters(in: .whitespacesAndNewlines),
                  server.name.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
                  server.sourceClient.isBuiltIn,
                  identities.insert(server.sourceClient.rawValue + ":" + identity).inserted,
                  server.args.count <= 128, server.environment.count <= 128, server.headers.count <= 64 else {
                throw PortableSetupError.invalid("invalid or duplicate server")
            }
            names.insert(identity)
            switch server.transport {
            case .stdio:
                guard let command = server.command, server.url == nil, server.headers.isEmpty else {
                    throw PortableSetupError.invalid("invalid stdio definition")
                }
                try value(command, literals: safeCommands, kind: .path)
            case .http, .sse:
                guard let url = server.url, server.command == nil, server.args.isEmpty, server.workingDirectory == nil else {
                    throw PortableSetupError.invalid("invalid remote definition")
                }
                try value(url, literals: nil, kind: .secret)
            }
            let packageIndex = safePackageIndex(command: server.command?.literal, args: server.args.map { $0.literal ?? "__kytto_required_input__" })
            for (index, argument) in server.args.enumerated() {
                let literals = index == packageIndex ? safeArguments.union([argument.literal ?? ""]) : safeArguments
                try value(argument, literals: literals, kind: nil)
            }
            if let cwd = server.workingDirectory { try value(cwd, literals: nil, kind: .path) }
            for (key, entry) in server.environment {
                guard key.wholeMatch(of: /[A-Za-z_][A-Za-z0-9_]*/) != nil, key.utf8.count <= 128 else {
                    throw PortableSetupError.invalid("invalid environment key")
                }
                try value(entry, literals: nil, kind: .secret)
            }
            var headerNames = Set<String>()
            for (key, entry) in server.headers {
                guard key.wholeMatch(of: /[!#$%&'*+.^_`|~0-9A-Za-z-]+/) != nil, key.utf8.count <= 128,
                      headerNames.insert(key.lowercased()).inserted else {
                    throw PortableSetupError.invalid("invalid or duplicate header name")
                }
                try value(entry, literals: nil, kind: .secret)
            }
        }
        guard used == Set(requirementMap.keys) else { throw PortableSetupError.invalid("unused required input") }
        var profileNames = Set<String>()
        for profile in setup.profiles {
            guard validText(profile.name, limit: 320), profile.name.count <= 80,
                  profile.name == profile.name.trimmingCharacters(in: .whitespacesAndNewlines),
                  profileNames.insert(profile.name.folding(options: [.caseInsensitive], locale: Locale(identifier: "en_US_POSIX"))).inserted,
                  profile.serverIDs.count <= 200,
                  Set(profile.serverIDs).count == profile.serverIDs.count,
                  profile.serverIDs.allSatisfy({ $0 == Server.identity(for: $0) && names.contains($0) }),
                  profile.tokenBudget == nil || (1...TokenWeight.referenceContextWindow).contains(profile.tokenBudget!) else {
                throw PortableSetupError.invalid("invalid or duplicate profile")
            }
        }
        var contractIDs = Set<String>()
        for contract in setup.reviewedContracts {
            guard names.contains(contract.serverID), contractIDs.insert(contract.serverID).inserted,
                  contract.toolDigests.count <= 2048,
                  contract.toolDigests.allSatisfy({ validText($0.key) && $0.value.wholeMatch(of: /[0-9a-f]{64}/) != nil }) else {
                throw PortableSetupError.invalid("invalid or duplicate contract reference")
            }
        }
    }
}
