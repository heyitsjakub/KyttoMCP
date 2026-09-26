import Foundation
import KyttoCore
import ServiceManagement

/// The native side's state: what is on disk, what Kytto has changed, and which
/// clients are now out of date with their own configuration.
///
/// Everything that writes goes through here, so there is exactly one place where
/// the safety rules in §6 are enforced.
@MainActor
final class AppModel {
    private struct RestartEntry: Equatable {
        let definitionDigest: String
        let enablement: Enablement
    }

    private typealias RestartSnapshot = [String: RestartEntry]

    private final class WatcherTarget: @unchecked Sendable {
        weak var model: AppModel?
    }
    private let home: URL
    private let ledger = DigestLedger()
    private let paths: KyttoPaths
    private let parkStore: ParkStore
    private let metadata: MetadataStore
    private let checker = HealthChecker()
    private let settingsStore: SettingsStore
    private let profileStore: ProfileStore
    private let gatewayRoutes: GatewayRouteStore
    private let gatewayActivity: GatewayActivityStore
    private let gatewaySecretStore: any SecretStoring
    private let gatewayHelperURL: URL

    // Rebuilt only when retention or path overrides change, because those values
    // are baked into the write services at construction.
    private(set) var backups: BackupStore
    private var toggles: ToggleService
    private var authoring: ServerAuthoring
    private var secretsService: SecretsService
    private var gatewayMigration: GatewayMigrationService
    private var watcher: ConfigWatcher!

    private var cached: DiscoveryResult?
    /// Candidates are kept native between the scan and the user's explicit
    /// import. A candidate can contain environment values from a local config;
    /// only its key names ever become DTO fields.
    private var libraryCandidates: [UUID: MCPLibraryCandidate] = [:]
    private let provenanceLookup = MCPProvenanceLookup()
    private struct CheckedProvenance: Sendable {
        let kind: MCPProvenanceKind
        let packageName: String
        let version: String
        let checkedAt: Date
    }
    private var latestProvenance: [String: CheckedProvenance] = [:]

    /// Clients whose effective configuration differs from the last state Kytto
    /// treated as applied. The count is the number of net server-level changes,
    /// not the number of write actions (§7.1).
    private(set) var pendingRestarts: [ClientID: Int] = [:]
    /// A dismissed notice remains pending; this is only presentation state.
    private(set) var dismissedRestartClients: Set<ClientID> = []
    private var restartBaselines: [ClientID: RestartSnapshot] = [:]
    private var lastRestartSnapshots: [ClientID: RestartSnapshot] = [:]
    private var restartBaselineInitialized = false

    /// Raised when a watched file changes on disk, whoever changed it.
    var onExternalChange: (@MainActor () -> Void)?
    /// Raised when anything the menu bar item shows has moved.
    var onStateChanged: (@MainActor () -> Void)?
    /// Raised after settings have been persisted. `stateChanged` tells the shell
    /// that a path override also produced a fresh discovery snapshot.
    var onSettingsChanged: (@MainActor (_ settings: KyttoSettings, _ stateChanged: Bool) -> Void)?

    var settings: KyttoSettings { settingsStore.current }

    /// Where the backups live, for the "Show in Finder" button (§6.5 — visible,
    /// not hidden).
    var backupsDirectory: URL { paths.backups }

    init(home: URL? = nil) {
        #if DEBUG
        let testHome = ProcessInfo.processInfo.environment["KYTTO_TEST_HOME"].map { URL(filePath: $0) }
        #else
        let testHome: URL? = nil
        #endif
        self.home = home ?? testHome ?? FileManager.default.homeDirectoryForCurrentUser
        paths = KyttoPaths(home: self.home)
        settingsStore = SettingsStore(paths: paths)
        profileStore = ProfileStore(paths: paths)
        gatewayRoutes = GatewayRouteStore(paths: paths)
        gatewayActivity = GatewayActivityStore(paths: paths)
        gatewaySecretStore = KeychainSecretStore(service: "app.kytto.gateway")
        #if DEBUG
        gatewayHelperURL = ProcessInfo.processInfo.environment["KYTTO_GATEWAY_HELPER"]
            .map { URL(fileURLWithPath: $0) }
            ?? Bundle.main.bundleURL.appending(path: "Contents/MacOS/kytto-mcp-proxy")
        #else
        gatewayHelperURL = Bundle.main.bundleURL.appending(path: "Contents/MacOS/kytto-mcp-proxy")
        #endif
        parkStore = ParkStore(paths: paths)
        metadata = MetadataStore(paths: paths)

        let settings = settingsStore.current
        backups = BackupStore(paths: paths, retentionPerClient: settings.backupRetention)
        let writer = ConfigWriter(backups: backups)
        toggles = ToggleService(
            home: self.home, writer: writer, parkStore: parkStore,
            ledger: ledger, pathOverrides: settings.clientPathOverrides
        )
        authoring = ServerAuthoring(
            home: self.home, writer: writer, parkStore: parkStore,
            ledger: ledger, pathOverrides: settings.clientPathOverrides
        )
        secretsService = SecretsService(
            home: self.home, writer: writer, ledger: ledger,
            store: KeychainSecretStore(), pathOverrides: settings.clientPathOverrides
        )
        gatewayMigration = GatewayMigrationService(
            home: self.home,
            helperURL: gatewayHelperURL,
            writer: writer,
            ledger: ledger,
            routes: gatewayRoutes,
            secrets: gatewaySecretStore,
            pathOverrides: settings.clientPathOverrides
        )

        // The watcher fires off the main actor; hop back before touching state.
        let watcherTarget = WatcherTarget()
        watcher = ConfigWatcher { [watcherTarget] in
            Task { @MainActor in watcherTarget.model?.handleExternalChange() }
        }
        watcherTarget.model = self
    }

    // MARK: - Settings (§7.6)

    @discardableResult
    func updateSettings(_ change: (inout KyttoSettings) -> Void) throws -> KyttoSettings {
        let previous = settingsStore.current
        var requested = previous
        change(&requested)
        let target = requested.sanitized()
        guard target != previous else { return previous }

        // The login item lives outside the settings file. Apply that external
        // effect first so a failed registration is never persisted as though it
        // succeeded; if the subsequent file write fails, put the OS state back.
        let launchAtLoginChanged = target.launchAtLogin != previous.launchAtLogin
        if launchAtLoginChanged {
            try applyLaunchAtLogin(target.launchAtLogin)
        }

        let updated: KyttoSettings
        do {
            updated = try settingsStore.update { $0 = target }
        } catch {
            guard launchAtLoginChanged else { throw error }
            do {
                try applyLaunchAtLogin(previous.launchAtLogin)
            } catch let rollbackError {
                throw AppError.launchAtLoginRollbackFailed(
                    original: error.localizedDescription,
                    rollback: rollbackError.localizedDescription
                )
            }
            throw error
        }

        let pathsChanged = updated.clientPathOverrides != previous.clientPathOverrides
        let sourcesChanged = updated.customConfigSources != previous.customConfigSources
        let discoveryChanged = pathsChanged || sourcesChanged
        let storageChanged = pathsChanged || updated.backupRetention != previous.backupRetention
        if storageChanged {
            rebuildServices(with: updated, forgetLedger: pathsChanged)
        }
        if discoveryChanged {
            if sourcesChanged { ledger.forgetAll() }
            reload()
        }
        onSettingsChanged?(updated, discoveryChanged)
        return updated
    }

    // MARK: - Storage permissions

    /// Called by the application shell at launch. Takes group and other access
    /// away from backups and parked definitions that earlier versions stored
    /// world-readable. It creates nothing and touches no client config (§6.6).
    func tightenStoragePermissions() {
        paths.tightenPermissions()
    }

    private func rebuildServices(with settings: KyttoSettings, forgetLedger: Bool) {
        backups = BackupStore(paths: paths, retentionPerClient: settings.backupRetention)
        let writer = ConfigWriter(backups: backups)
        toggles = ToggleService(
            home: home, writer: writer, parkStore: parkStore,
            ledger: ledger, pathOverrides: settings.clientPathOverrides
        )
        authoring = ServerAuthoring(
            home: home, writer: writer, parkStore: parkStore,
            ledger: ledger, pathOverrides: settings.clientPathOverrides
        )
        secretsService = SecretsService(
            home: home, writer: writer, ledger: ledger,
            store: KeychainSecretStore(), pathOverrides: settings.clientPathOverrides
        )
        gatewayMigration = GatewayMigrationService(
            home: home,
            helperURL: gatewayHelperURL,
            writer: writer,
            ledger: ledger,
            routes: gatewayRoutes,
            secrets: gatewaySecretStore,
            pathOverrides: settings.clientPathOverrides
        )
        if forgetLedger {
            // A changed override points at a different file; what we believed
            // about the old one says nothing about the new one.
            ledger.forgetAll()
        }
    }

    /// The client the menu bar item acts on: the user's choice, or the one with
    /// the most servers, which is the right guess until they say otherwise (§7.7).
    var menuBarClient: ClientID? {
        if let chosen = settings.menuBarClient { return chosen }
        return current().clients
            .filter { $0.isInstalled && !$0.isReadOnly }
            .max { $0.serverCount < $1.serverCount }?
            .id
    }

    // MARK: - Reading

    func current() -> DiscoveryResult {
        if let cached { return cached }
        return reload()
    }

    @discardableResult
    func reload() -> DiscoveryResult {
        let discovery = makeDiscovery()
        let result = discovery.run()
        cached = result
        reconcileRestartState(using: result)
        onStateChanged?()
        // Re-arm each time: a client that just gained a config directory becomes
        // watchable only once it exists.
        watcher.watch(directories: discovery.watchedDirectories())
        return result
    }

    private func makeDiscovery() -> Discovery {
        let descriptors = ClientRegistry.allIncludingClaudeCodeProjects(
            home: home,
            pathOverride: settings.clientPathOverrides[ClientID.claudeCode.rawValue]
        ) + settings.customConfigSources.map(ClientRegistry.customDescriptor)
        return Discovery(
            home: home,
            locator: SystemAppLocator(),
            descriptors: descriptors,
            ledger: ledger,
            parked: parkStore.all(),
            metadata: metadata.all(),
            pathOverrides: settings.clientPathOverrides
        )
    }

    // MARK: - MCP provenance

    /// Builds informational provenance for one current server. A registry
    /// result is applied only when it still belongs to the same inferred package
    /// and source kind, so editing a command cannot leave an old latest version
    /// attached to a new server definition.
    func provenance(for server: Server) -> MCPProvenance {
        let identified = MCPProvenanceResolver.identify(server: server)
        let installedVersion = server.health?.serverVersion ?? identified.installedVersion
        let checked = latestProvenance[server.id]
        let latest = checked?.kind == identified.kind && checked?.packageName == identified.packageName
            ? checked
            : nil
        return MCPProvenance(
            kind: identified.kind,
            packageName: identified.packageName,
            sourceURL: identified.sourceURL,
            installedVersion: installedVersion,
            latestVersion: latest?.version,
            latestCheckedAt: latest?.checkedAt,
            confidence: identified.confidence
        )
    }

    func checkProvenanceLatest(serverID: String) async throws {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let identified = provenance(for: server)
        let result = try await provenanceLookup.latest(for: identified)
        latestProvenance[server.id] = CheckedProvenance(
            kind: identified.kind,
            packageName: identified.packageName ?? "",
            version: result.version,
            checkedAt: result.checkedAt
        )
    }

    // MARK: - MCP library and agent import

    private struct ImportInput: Sendable {
        let draft: ServerDraft
        let environmentKeys: [String]
        let warnings: [String]
    }

    func libraryScan(path: String) throws -> MCPLibraryScanResult {
        let url = URL(filePath: path).standardizedFileURL
        guard url.isFileURL, url.path.hasPrefix("/"), isDirectory(url) else {
            throw AppError.invalidLibraryDirectory
        }
        let result = MCPLibraryScanner(root: url).scan()
        libraryCandidates = Dictionary(uniqueKeysWithValues: result.candidates.map { ($0.id, $0) })
        return result
    }

    func libraryCandidate(id: String) throws -> MCPLibraryCandidate {
        guard let uuid = UUID(uuidString: id), let candidate = libraryCandidates[uuid] else {
            throw AppError.unknownLibraryCandidate(id)
        }
        return candidate
    }

    func previewLibraryCandidate(id: String, clientIDs: [ClientID]) throws -> ImportPreview {
        let candidate = try libraryCandidate(id: id)
        let targets = try editableImportClients(clientIDs)
        return makeImportPreview(
            inputs: [ImportInput(
                draft: candidate.draft,
                environmentKeys: candidate.envKeys,
                warnings: candidate.warnings
            )],
            targets: targets,
            source: "Local MCP library · \(candidate.location)"
        )
    }

    func importLibraryCandidate(id: String, clientIDs: [ClientID]) throws -> [AuthoringResult] {
        let candidate = try libraryCandidate(id: id)
        let targets = try editableImportClients(clientIDs)
        let preview = makeImportPreview(
            inputs: [ImportInput(
                draft: candidate.draft,
                environmentKeys: candidate.envKeys,
                warnings: candidate.warnings
            )],
            targets: targets,
            source: "Local MCP library · \(candidate.location)"
        )
        try requireImportable(preview)
        return [try createServer(candidate.draft, in: targets)]
    }

    func agentImportPrompt() -> String {
        """
        Describe an MCP server using strict JSON only. Return exactly one object:
        {
          "schemaVersion": 1,
          "servers": [
            {
              "name": "short-name",
              "transport": "stdio",
              "command": "python3",
              "args": ["server.py"],
              "env": ["API_KEY"]
            }
          ]
        }

        Allowed server fields are name, transport, command, args, url and env.
        Transport must be stdio, http or sse. Put environment variable names in
        env only; never include secret values. Do not add Markdown fences,
        comments, trailing commas or unknown fields.
        """
    }

    func skillsInventory(workspacePath: String?) throws -> SkillsInventoryResult {
        let workspace: URL?
        if let workspacePath, !workspacePath.isEmpty {
            let url = URL(filePath: workspacePath).standardizedFileURL
            guard url.isFileURL, url.path.hasPrefix("/"), isDirectory(url) else {
                throw AppError.invalidSkillsDirectory
            }
            workspace = url
        } else {
            workspace = nil
        }

        var roots = [
            SkillInventoryRoot(url: home.appending(path: ".codex/skills"), agent: "Codex", scope: .global, label: "Codex · global"),
            SkillInventoryRoot(url: home.appending(path: ".claude/skills"), agent: "Claude", scope: .global, label: "Claude · global"),
            SkillInventoryRoot(url: home.appending(path: ".cursor/skills"), agent: "Cursor", scope: .global, label: "Cursor · global"),
            SkillInventoryRoot(url: home.appending(path: ".agents/skills"), agent: "Shared agent", scope: .global, label: "Shared agents · global"),
        ]
        if let workspace {
            roots += [
                SkillInventoryRoot(url: workspace.appending(path: ".codex/skills"), agent: "Codex", scope: .workspace, label: "Codex · workspace"),
                SkillInventoryRoot(url: workspace.appending(path: ".claude/skills"), agent: "Claude", scope: .workspace, label: "Claude · workspace"),
                SkillInventoryRoot(url: workspace.appending(path: ".cursor/skills"), agent: "Cursor", scope: .workspace, label: "Cursor · workspace"),
                SkillInventoryRoot(url: workspace.appending(path: ".agents/skills"), agent: "Shared agent", scope: .workspace, label: "Shared agents · workspace"),
                SkillInventoryRoot(url: workspace.appending(path: ".github/skills"), agent: "GitHub Copilot", scope: .workspace, label: "GitHub Copilot · workspace"),
            ]
        }
        return SkillsInventoryScanner(roots: roots).scan()
    }

    func previewAgentImport(json: String, clientIDs: [ClientID]) throws -> ImportPreview {
        let document = try AgentImportDocument(json: json)
        let targets = try editableImportClients(clientIDs)
        return makeImportPreview(
            inputs: document.servers.map { server in
                ImportInput(
                    draft: server.draft,
                    environmentKeys: server.envKeys,
                    warnings: server.envKeys.isEmpty
                        ? []
                        : ["Environment values are not part of this JSON import; only the key names will be shown."]
                )
            },
            targets: targets,
            source: "Strict agent JSON"
        )
    }

    func importAgentJSON(json: String, clientIDs: [ClientID]) throws -> [AuthoringResult] {
        let document = try AgentImportDocument(json: json)
        let targets = try editableImportClients(clientIDs)
        let preview = makeImportPreview(
            inputs: document.servers.map { server in
                ImportInput(
                    draft: server.draft,
                    environmentKeys: server.envKeys,
                    warnings: []
                )
            },
            targets: targets,
            source: "Strict agent JSON"
        )
        try requireImportable(preview)
        return try document.servers.map { try createServer($0.draft, in: targets) }
    }

    private func editableImportClients(_ requested: [ClientID]) throws -> [ClientID] {
        let unique = Array(Set(requested)).sorted { $0.rawValue < $1.rawValue }
        guard !unique.isEmpty else { throw AppError.noImportTargets }
        for clientID in unique {
            guard clientID.isBuiltIn,
                  let descriptor = ClientRegistry.descriptorIfKnown(for: clientID),
                  descriptor.editableServerMap != nil
            else { throw AppError.importTargetNotEditable(clientID.rawValue) }
        }
        return unique
    }

    private func makeImportPreview(
        inputs: [ImportInput],
        targets: [ClientID],
        source: String
    ) -> ImportPreview {
        let existing = current().servers
        let servers = inputs.map { input in
            let conflicts = targets.filter { clientID in
                existing.contains { server in
                    (server.enabledIn[clientID] ?? .absent) != .absent &&
                        Server.identity(for: server.name) == Server.identity(for: input.draft.trimmedName)
                }
            }
            return ImportServerPreview(
                name: input.draft.trimmedName,
                transport: input.draft.transport,
                commandSummary: input.draft.transport == .stdio
                    ? ([input.draft.command] + input.draft.args).joined(separator: " ")
                    : input.draft.url,
                environmentKeys: input.environmentKeys,
                conflictingClientIDs: conflicts,
                warnings: input.warnings
            )
        }
        return ImportPreview(source: source, targetClientIDs: targets, servers: servers, warnings: [])
    }

    private func requireImportable(_ preview: ImportPreview) throws {
        guard let conflict = preview.servers.first(where: { !$0.conflictingClientIDs.isEmpty }),
              let clientID = conflict.conflictingClientIDs.first
        else { return }
        throw AppError.importConflict(server: conflict.name, client: clientID.rawValue)
    }

    private func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
    }

    // MARK: - Restart state

    /// A secret-free, semantic snapshot of one client's configuration. The
    /// definition digest includes environment values internally, but only the
    /// digest is retained and it never crosses the IPC boundary.
    private func restartSnapshots(from result: DiscoveryResult) -> [ClientID: RestartSnapshot] {
        var snapshots: [ClientID: RestartSnapshot] = [:]

        for clientID in ClientID.allCases {
            var entries: RestartSnapshot = [:]
            for server in result.servers {
                let enablement = server.enabledIn[clientID] ?? .absent
                guard enablement != .absent || server.definitionsByClient[clientID] != nil else { continue }

                let definitionDigest = server.definitionsByClient[clientID]
                    .map(restartDefinitionDigest)
                    ?? ConfigWriter.digest(of: "absent")
                entries[server.id] = RestartEntry(
                    definitionDigest: definitionDigest,
                    enablement: enablement
                )
            }

            // Gateway route metadata changes also require the owning client to
            // restart, even though the server map itself may stay byte-identical.
            for route in (try? gatewayRoutes.all()) ?? [] where route.clientID == clientID {
                let routeState = [
                    route.serverID,
                    route.exposedTools?.joined(separator: "\u{1}") ?? "*",
                ].joined(separator: "\u{2}")
                entries["__gateway.\(route.id)"] = RestartEntry(
                    definitionDigest: ConfigWriter.digest(of: routeState),
                    enablement: .enabled
                )
            }
            snapshots[clientID] = entries
        }
        return snapshots
    }

    /// Restart state follows the effective definition, not its presentation.
    /// Toggling a presence-only client may restore the same server with a
    /// different indentation or line wrapping; that is not a second runtime
    /// configuration. Environment values stay inside this digest so a rotated
    /// secret still correctly requires a restart.
    private func restartDefinitionDigest(_ definition: ClientDefinition) -> String {
        var fields = [
            definition.transport.rawValue,
            definition.command ?? "",
            definition.url ?? "",
            definition.isBundled ? "bundled" : "config",
        ]
        fields.append(contentsOf: definition.args)
        fields.append(contentsOf: definition.env
            .sorted {
                if $0.key == $1.key { return ($0.value ?? "") < ($1.value ?? "") }
                return $0.key < $1.key
            }
            .flatMap { [$0.key, $0.value ?? ""] })
        return ConfigWriter.digest(of: fields.map { "\($0.utf8.count):\($0)" }.joined())
    }

    private func restartChangeCount(
        from baseline: RestartSnapshot,
        to current: RestartSnapshot
    ) -> Int {
        let keys = Set(baseline.keys).union(current.keys)
        return keys.reduce(into: 0) { count, key in
            if baseline[key] != current[key] { count += 1 }
        }
    }

    private func reconcileRestartState(using result: DiscoveryResult) {
        let snapshots = restartSnapshots(from: result)
        guard restartBaselineInitialized else {
            restartBaselines = snapshots
            lastRestartSnapshots = snapshots
            restartBaselineInitialized = true
            pendingRestarts.removeAll()
            dismissedRestartClients.removeAll()
            return
        }

        for clientID in ClientID.allCases {
            let current = snapshots[clientID, default: [:]]
            let previous = lastRestartSnapshots[clientID, default: [:]]
            let count = restartChangeCount(
                from: restartBaselines[clientID, default: [:]],
                to: current
            )

            if count == 0 {
                pendingRestarts.removeValue(forKey: clientID)
                dismissedRestartClients.remove(clientID)
            } else {
                // A new net state should make a previously dismissed notice
                // visible again. Re-reading the same state preserves dismissal.
                if previous != current { dismissedRestartClients.remove(clientID) }
                pendingRestarts[clientID] = count
            }
        }
        lastRestartSnapshots = snapshots
    }

    /// Registers or unregisters the login item (§7.6).
    ///
    /// `SMAppService` is the only supported way since macOS 13; the old login
    /// items API silently stopped working and left apps looking broken.
    private func applyLaunchAtLogin(_ enabled: Bool) throws {
        let service = SMAppService.mainApp
        if enabled {
            guard service.status != .enabled else { return }
            try service.register()
        } else if service.status == .enabled {
            try service.unregister()
        }
    }

    // MARK: - Secrets (§6)

    func secrets() -> [SecretRecord] {
        secretsService.scan(servers: current().servers)
    }

    /// Only ever on an explicit request, and only for as long as it takes to
    /// show it. §6 permits a reveal behind a confirm; it does not permit values
    /// travelling with the ordinary listing.
    func revealSecret(id: String) -> String? {
        secretsService.reveal(secretID: id, servers: current().servers)
    }

    func rotateSecret(id: String, to newValue: String, storeInSecretStore: Bool) throws -> SecretsService.RotationResult {
        let result = try secretsService.rotate(
            secretID: id,
            to: newValue,
            servers: current().servers,
            alsoStoreInSecretStore: storeInSecretStore
        )
        for clientID in Set(result.updated.map(\.clientID)) {
            pendingRestarts[clientID, default: 0] += 1
        }
        reload()
        return result
    }

    func adoptSecret(id: String) throws {
        try secretsService.adopt(secretID: id, servers: current().servers)
    }

    func forgetSecret(id: String) throws {
        try secretsService.forget(secretID: id)
    }

    @discardableResult
    func restrictPermissions(clientID: ClientID) throws -> Bool {
        try secretsService.restrictPermissions(clientID: clientID)
    }

    // MARK: - Health (§7.3, §7.4)

    /// Runs one server and records what it said.
    ///
    /// Always user-initiated, never on launch (§6). The check itself blocks for
    /// up to ten seconds, so it runs off the main actor — the window must stay
    /// responsive while a server is starting up.
    func checkHealth(serverID: String) async throws -> HealthResult {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let health = try await recordHealth(for: server)
        reload()
        return health
    }

    private func recordHealth(for server: Server) async throws -> HealthResult {
        let checker = self.checker
        let outcome = await Task.detached(priority: .userInitiated) {
            checker.check(server)
        }.value

        try metadata.record(health: outcome.health, tokenWeight: outcome.tokenWeight, for: server.id)
        return outcome.health
    }

    /// Checks every server one at a time, reporting after each.
    ///
    /// Sequential on purpose: these are arbitrary commands, and starting a dozen
    /// node processes at once to save a few seconds is not a trade worth making
    /// on someone's laptop.
    func checkAllHealth(
        onProgress: @MainActor (String, Int, Int) async -> Void
    ) async throws {
        let servers = current().servers.filter { $0.transport == .stdio }
        var persistenceFailures = 0
        for (index, server) in servers.enumerated() {
            await onProgress(server.name, index + 1, servers.count)
            do {
                _ = try await recordHealth(for: server)
            } catch {
                persistenceFailures += 1
            }
        }
        if !servers.isEmpty { reload() }
        if persistenceFailures > 0 {
            throw AppError.healthResultsCouldNotBeSaved(persistenceFailures)
        }
    }

    // MARK: - Doctor, contract guard and activity

    func doctorReports() -> [DoctorServerReport] {
        let result = current()
        return MCPDoctor.reports(for: result.servers, clients: result.clients.map(\.id))
    }

    func contractAlerts() -> [ContractAlert] {
        let entries = metadata.all()
        return current().servers.compactMap { server in
            guard let entry = entries[server.id],
                  let changes = entry.contractChanges,
                  !changes.isEmpty,
                  let changedAt = entry.contractChangedAt
            else { return nil }
            return ContractAlert(
                serverID: server.id,
                serverName: server.name,
                changedAt: changedAt,
                changes: changes
            )
        }
    }

    /// Records that a contract change has been read, so the alert can end.
    func acknowledgeContract(serverID: String) throws {
        guard current().servers.contains(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        try metadata.acknowledgeContract(for: serverID)
        reload()
    }

    func doctorFixPreview(serverID: String) throws -> DoctorFixPreview {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        try requireDirect(serverID: serverID)
        guard server.transport == .stdio,
              let currentCommand = server.command,
              let resolvedCommand = server.health?.resolvedCommand,
              server.health?.status == .passed,
              currentCommand != resolvedCommand,
              resolvedCommand.hasPrefix("/")
        else { throw AppError.noVerifiedExecutable(server.name) }

        let clients = server.enabledIn
            .filter { $0.value != .absent }
            .map(\.key)
            .sorted { $0.rawValue < $1.rawValue }
        return DoctorFixPreview(
            serverID: server.id,
            serverName: server.name,
            currentCommand: currentCommand,
            replacementCommand: resolvedCommand,
            clientIDs: clients
        )
    }

    func applyDoctorFix(serverID: String) throws -> AuthoringResult {
        let preview = try doctorFixPreview(serverID: serverID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        var draft = ServerDraft(editing: server)
        draft.command = preview.replacementCommand
        return try updateServer(draft, serverID: serverID)
    }

    func activity(limit: Int = 500) -> GatewayActivitySummary {
        gatewayActivity.summary(limit: limit)
    }

    // MARK: - Profiles

    func profiles() -> [ServerProfile] {
        profileStore.all()
    }

    // MARK: - Gateway (§3.3)

    func routes() -> [GatewayRoute] {
        (try? gatewayRoutes.all()) ?? []
    }

    func gatewayPreview(serverID: String, clientID: ClientID) throws -> GatewayMigrationPreview {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        return try gatewayMigration.preview(server: server, clientID: clientID)
    }

    func enableGateway(serverID: String, clientID: ClientID, routeID: String) throws -> GatewayMigrationResult {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let result = try gatewayMigration.enable(server: server, clientID: clientID, routeID: routeID)
        pendingRestarts[clientID, default: 0] += 1
        reload()
        return result
    }

    func restoreDirect(routeID: String) throws -> GatewayMigrationResult {
        let result = try gatewayMigration.restore(routeID: routeID)
        pendingRestarts[result.route.clientID, default: 0] += 1
        reload()
        return result
    }

    /// Narrows what one route exposes to its client (§7.11).
    ///
    /// No config file is touched: the client already points at this route id,
    /// and the helper reads the allow list when it starts. That still means the
    /// client has to be restarted before the change is visible to the model,
    /// so this counts as a pending restart like any other change.
    @discardableResult
    func setExposedTools(routeID: String, toolNames: [String]?) throws -> GatewayRoute {
        let route = try gatewayRoutes.update(id: routeID, exposedTools: toolNames)
        pendingRestarts[route.clientID, default: 0] += 1
        if let cached { reconcileRestartState(using: cached) }
        return route
    }

    private func route(serverID: String, clientID: ClientID) -> GatewayRoute? {
        try? gatewayRoutes.route(serverID: serverID, clientID: clientID)
    }

    private func requireDirect(serverID: String, clientID: ClientID? = nil) throws {
        let active = routes().contains { route in
            route.serverID == Server.identity(for: serverID) && (clientID == nil || route.clientID == clientID)
        }
        if active { throw AppError.gatewayManaged(serverID) }
    }

    @discardableResult
    func createProfile(name: String, serverIDs: [String], tokenBudget: Int? = nil) throws -> ServerProfile {
        try profileStore.create(name: name, serverIDs: serverIDs, tokenBudget: tokenBudget)
    }

    @discardableResult
    func updateProfile(id: String, name: String, serverIDs: [String], tokenBudget: Int? = nil) throws -> ServerProfile {
        try profileStore.update(id: id, name: name, serverIDs: serverIDs, tokenBudget: tokenBudget)
    }

    func deleteProfile(id: String) throws {
        try profileStore.delete(id: id)
    }

    /// Makes one client's active set match a profile exactly.
    ///
    /// Every individual change still passes through `ToggleService`, including
    /// its backup, format-preserving write, digest guard and parked definition.
    /// Different clients can require different files, so failures are collected
    /// and reported instead of pretending the whole operation was atomic.
    /// Exactly what applying a profile to a client would change (§7.9).
    ///
    /// Shared with `applyProfile` rather than recomputed alongside it, so a
    /// preview cannot promise one thing and the write do another.
    private static func profileChanges(
        desired: Set<String>,
        servers: [Server],
        clientID: ClientID
    ) -> [(server: Server, enabled: Bool)] {
        servers.compactMap { server in
            let current = server.enabledIn[clientID] ?? .absent
            if desired.contains(server.id), current != .enabled {
                return (server, true)
            }
            if !desired.contains(server.id), current == .enabled {
                return (server, false)
            }
            return nil
        }
    }

    /// A profile application, described before anything is written.
    ///
    /// §7.9 requires the complete enable/keep/switch-off picture and the
    /// estimated context cost before confirmation. The window has room for a
    /// table; the menu bar has room for a sentence, and this is what that
    /// sentence is built from.
    struct ProfileApplyPlan: Sendable {
        let profileName: String
        let clientName: String
        let toEnable: [String]
        let toDisable: [String]
        let unchanged: Int
        let missing: [String]
        /// What this client would cost in context afterwards.
        let estimatedTokens: Int
        /// Members whose weight has never been measured, so the total is a floor.
        let unmeasured: Int

        var changesNothing: Bool { toEnable.isEmpty && toDisable.isEmpty }
    }

    func profileApplyPlan(id: String, to clientID: ClientID) throws -> ProfileApplyPlan {
        guard let profile = profiles().first(where: { $0.id == id }) else {
            throw ProfileError.unknownProfile(id)
        }
        let servers = current().servers
        let desired = Set(profile.serverIDs)
        let changes = Self.profileChanges(desired: desired, servers: servers, clientID: clientID)
        let members = servers.filter { desired.contains($0.id) }

        return ProfileApplyPlan(
            profileName: profile.name,
            clientName: ClientRegistry.all.first { $0.id == clientID }?.displayName ?? clientID.rawValue,
            toEnable: changes.filter(\.enabled).map(\.server.name).sorted(),
            toDisable: changes.filter { !$0.enabled }.map(\.server.name).sorted(),
            unchanged: members.count - changes.filter(\.enabled).count,
            missing: desired.subtracting(servers.map(\.id)).sorted(),
            estimatedTokens: members.reduce(0) { $0 + ($1.tokenWeight?.estimate ?? 0) },
            unmeasured: members.filter { $0.tokenWeight == nil }.count
        )
    }

    func applyProfile(id: String, to clientID: ClientID) throws -> ProfileApplyResult {
        guard let profile = profiles().first(where: { $0.id == id }) else {
            throw ProfileError.unknownProfile(id)
        }

        let servers = current().servers
        let desired = Set(profile.serverIDs)
        var enabled = 0
        var disabled = 0
        var failures: [ProfileApplyFailure] = []

        for missingID in desired.subtracting(servers.map(\.id)).sorted() {
            failures.append(ProfileApplyFailure(
                serverName: missingID,
                message: "This server is no longer present in any client."
            ))
        }

        let changes = Self.profileChanges(desired: desired, servers: servers, clientID: clientID)

        for change in changes {
            do {
                try requireDirect(serverID: change.server.id, clientID: clientID)
                let result = try toggles.setEnabled(change.enabled, server: change.server, in: clientID)
                if change.enabled { enabled += 1 } else { disabled += 1 }
                if result.requiresRestart {
                    pendingRestarts[clientID, default: 0] += 1
                }
            } catch {
                failures.append(ProfileApplyFailure(
                    serverName: change.server.name,
                    message: (error as? LocalizedError)?.errorDescription ?? String(describing: error)
                ))
            }
        }

        if !changes.isEmpty { reload() }
        return ProfileApplyResult(
            profileName: profile.name,
            clientID: clientID,
            enabledCount: enabled,
            disabledCount: disabled,
            failures: failures
        )
    }

    // MARK: - Writing

    func setEnabled(_ enabled: Bool, serverID: String, clientID: ClientID) throws -> ToggleResult {
        try requireDirect(serverID: serverID, clientID: clientID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }

        let result = try toggles.setEnabled(enabled, server: server, in: clientID)
        if result.requiresRestart {
            pendingRestarts[clientID, default: 0] += 1
        }
        reload()
        return result
    }

    func createServer(_ draft: ServerDraft, in clients: [ClientID]) throws -> AuthoringResult {
        let result = try authoring.create(draft, in: clients, existing: current().servers)
        noteRestarts(result)
        reload()
        return result
    }

    func updateServer(_ draft: ServerDraft, serverID: String) throws -> AuthoringResult {
        try requireDirect(serverID: serverID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let result = try authoring.update(
            draft.mergingSecrets(from: server),
            originalName: server.name,
            server: server,
            existing: current().servers
        )
        // The config write has already succeeded. A secondary profile-file
        // problem must not turn that success into a reported failure.
        try? profileStore.replaceServerID(serverID, with: Server.identity(for: result.serverName))
        noteRestarts(result)
        reload()
        return result
    }

    func deleteServer(serverID: String) throws -> AuthoringResult {
        try requireDirect(serverID: serverID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let result = try authoring.delete(server)
        // Otherwise a later server reusing the name inherits a stale red dot.
        try? metadata.forget(server.id)
        noteRestarts(result)
        reload()
        return result
    }

    /// Takes a server out of one client. The row survives if anyone else has it.
    func removeServer(serverID: String, clientID: ClientID) throws -> AuthoringResult {
        try requireDirect(serverID: serverID, clientID: clientID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let result = try authoring.remove(server, from: clientID)

        // The health record belongs to the server, not to any one client, so it
        // only goes when the last client holding it does — otherwise removing a
        // copy would blank the dot on a row that is still there.
        let remaining = server.enabledIn.filter { $0.key != clientID && $0.value != .absent }
        if remaining.isEmpty { try? metadata.forget(server.id) }

        noteRestarts(result)
        reload()
        return result
    }

    /// What every client would look like if one client's copy won.
    ///
    /// Read-only. Environment **keys** are named because a key one client is
    /// missing is half of what drift means; no value is read here and none is
    /// returned (§6).
    func unifyPreview(serverID: String, sourceClientID: ClientID) throws -> UnifyPreview {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        guard let drift = ServerDrift.detect(in: server) else {
            throw AppError.noDrift(server.name)
        }
        guard let source = server.definitionsByClient[sourceClientID] else {
            throw AppError.unknownServer(serverID)
        }
        guard !source.isBundled else { throw AuthoringError.notEditable(server.name) }

        let targets = unifyTargets(server: server, sourceClientID: sourceClientID)
        return UnifyPreview(
            serverID: server.id,
            serverName: server.name,
            sourceClientID: sourceClientID,
            targetClientIDs: targets,
            skippedClientIDs: drift.unwritableClientIDs,
            commandSummary: [source.command].compactMap { $0 }.joined()
                + (source.args.isEmpty ? "" : " " + source.args.joined(separator: " ")),
            url: source.url,
            transport: source.transport,
            environmentKeys: source.environmentKeys
        )
    }

    /// Every client that holds a copy, minus the ones already matching the source
    /// and the ones that are installed software rather than configuration.
    private func unifyTargets(server: Server, sourceClientID: ClientID) -> [ClientID] {
        guard let source = server.definitionsByClient[sourceClientID] else { return [] }
        return server.definitionsByClient
            .filter { clientID, definition in
                clientID != sourceClientID && !definition.isBundled && definition != source
            }
            .map(\.key)
            .sorted { $0.rawValue < $1.rawValue }
    }

    /// Rewrites every drifted copy of a server to match one client's.
    ///
    /// The definition is assembled here, natively, from the source client's own
    /// entry — including its environment **values**, which is the reason this is
    /// one command rather than the web layer reading a definition and sending it
    /// back. A token would have had to cross the boundary twice to do that, and
    /// it never crosses once (§6).
    func unifyServer(serverID: String, sourceClientID: ClientID) throws -> AuthoringResult {
        try requireDirect(serverID: serverID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        guard let source = server.definitionsByClient[sourceClientID] else {
            throw AppError.unknownServer(serverID)
        }
        guard !source.isBundled else { throw AuthoringError.notEditable(server.name) }

        let targets = unifyTargets(server: server, sourceClientID: sourceClientID)
        guard !targets.isEmpty else { throw AppError.noDrift(server.name) }

        let draft = ServerDraft(
            name: server.name,
            transport: source.transport,
            command: source.command ?? "",
            args: source.args,
            env: source.env,
            url: source.url ?? ""
        )

        let result = try authoring.unify(draft, server: server, into: targets)
        noteRestarts(result)
        reload()
        return result
    }

    private func noteRestarts(_ result: AuthoringResult) {
        for clientID in result.changed {
            pendingRestarts[clientID, default: 0] += 1
        }
    }

    func restore(backupID: String, clientID: ClientID) throws {
        guard let backup = backups.backup(id: backupID, clientID: clientID) else {
            throw AppError.unknownBackup(backupID)
        }
        try gatewayMigration.validateBackupRestore(backups.contents(of: backup), clientID: clientID)
        try backups.restore(backup, home: home, pathOverrides: settings.clientPathOverrides)
        pendingRestarts[clientID, default: 0] += 1
        let restored = reload()
        let restoredServerIDs = Set(
            restored.servers
                .filter { $0.definitionsByClient[clientID] != nil }
                .map(\.id)
        )
        var clearedPark = false
        for entry in parkStore.all()
        where entry.clientID == clientID && restoredServerIDs.contains(Server.identity(for: entry.serverName)) {
            try? parkStore.unpark(clientID: clientID, serverName: entry.serverName)
            clearedPark = true
        }
        if clearedPark { reload() }
        try? gatewayMigration.reconcileAfterBackupRestore(clientID: clientID)
    }

    func acknowledgeRestart(clientID: ClientID?) {
        let result = current()
        let snapshots = restartSnapshots(from: result)
        let clients = clientID.map { [$0] } ?? ClientID.allCases
        for id in clients {
            restartBaselines[id] = snapshots[id, default: [:]]
            lastRestartSnapshots[id] = snapshots[id, default: [:]]
            pendingRestarts.removeValue(forKey: id)
            dismissedRestartClients.remove(id)
        }
    }

    /// Hides the notice without claiming that the client has been restarted.
    func dismissRestart(clientID: ClientID?) {
        let clients = clientID.map { [$0] } ?? Array(pendingRestarts.keys)
        for id in clients where pendingRestarts[id, default: 0] > 0 {
            dismissedRestartClients.insert(id)
        }
    }

    // MARK: - External changes

    private func handleExternalChange() {
        // Kytto's own writes trip the watcher too, so this fires more often than
        // the user makes changes. Re-reading is cheap and the render is
        // idempotent, so the honest thing is to reload and let the UI settle on
        // the truth rather than try to guess which events were ours.
        reload()
        onExternalChange?()
    }

    enum AppError: LocalizedError {
        case unknownServer(String)
        case unknownBackup(String)
        case invalidLibraryDirectory
        case unknownLibraryCandidate(String)
        case noImportTargets
        case importTargetNotEditable(String)
        case importConflict(server: String, client: String)
        case invalidSkillsDirectory
        case gatewayManaged(String)
        case noVerifiedExecutable(String)
        case healthResultsCouldNotBeSaved(Int)
        case noDrift(String)
        case launchAtLoginRollbackFailed(original: String, rollback: String)

        var errorDescription: String? {
            switch self {
            case .unknownServer(let id): "No server with id \(id)."
            case .unknownBackup(let id): "No backup with id \(id)."
            case .invalidLibraryDirectory:
                "Choose an existing directory for the local MCP library."
            case .unknownLibraryCandidate:
                "That local MCP library entry is no longer available. Scan the directory again."
            case .noImportTargets:
                "Choose at least one installed client to receive the imported server."
            case .importTargetNotEditable(let id):
                "\(id) is read-only or has no editable MCP configuration. Choose a built-in client."
            case .importConflict(let server, let client):
                "\(server) already exists in \(client). Review the preview and choose another client or rename the server."
            case .invalidSkillsDirectory:
                "Choose an existing workspace directory for the skills inventory."
            case .gatewayManaged:
                "This server is using Gateway mode. Restore Direct mode before editing, disabling or removing it."
            case .noVerifiedExecutable(let name):
                "“\(name)” does not have a successful health check with a verified absolute executable path."
            case .healthResultsCouldNotBeSaved(let count):
                "Could not save \(count) health check result\(count == 1 ? "" : "s"). The server processes were stopped safely; check that Kytto can write to its application data directory."
            case .noDrift(let name):
                "Every editable copy of “\(name)” already matches. There is nothing to unify."
            case .launchAtLoginRollbackFailed(let original, let rollback):
                "Could not save Settings (\(original)) and could not restore the previous login-item state (\(rollback))."
            }
        }
    }
}

struct ContractAlert: Sendable {
    let serverID: String
    let serverName: String
    let changedAt: Date
    let changes: [ContractChange]
}

/// What unifying would do, before it does it.
///
/// Carries the source client's command and its environment **key names**. No
/// value is in here, which is what lets it cross to the web layer at all.
struct UnifyPreview: Sendable {
    let serverID: String
    let serverName: String
    let sourceClientID: ClientID
    /// Clients that would be rewritten.
    let targetClientIDs: [ClientID]
    /// Clients holding a copy that cannot be rewritten whatever is chosen —
    /// extension bundles and plugins, which are installed software (§4).
    let skippedClientIDs: [ClientID]
    let commandSummary: String
    let url: String?
    let transport: Transport
    let environmentKeys: [String]
}

struct DoctorFixPreview: Sendable {
    let serverID: String
    let serverName: String
    let currentCommand: String
    let replacementCommand: String
    let clientIDs: [ClientID]
}

struct ProfileApplyFailure: Sendable {
    let serverName: String
    let message: String
}

struct ProfileApplyResult: Sendable {
    let profileName: String
    let clientID: ClientID
    let enabledCount: Int
    let disabledCount: Int
    let failures: [ProfileApplyFailure]
}

struct ImportServerPreview: Sendable {
    let name: String
    let transport: Transport
    let commandSummary: String
    let environmentKeys: [String]
    let conflictingClientIDs: [ClientID]
    let warnings: [String]
}

struct ImportPreview: Sendable {
    let source: String
    let targetClientIDs: [ClientID]
    let servers: [ImportServerPreview]
    let warnings: [String]

    nonisolated var canImport: Bool {
        !servers.isEmpty && servers.allSatisfy { $0.conflictingClientIDs.isEmpty }
    }
}
