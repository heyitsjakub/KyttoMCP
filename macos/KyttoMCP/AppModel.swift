import Foundation
import CryptoKit
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
    private var portableSetup: PortableSetupService?
    private let remoteOAuthCredentials = RemoteOAuthCredentials(store: KeychainSecretStore(service: "app.kytto.remote-oauth"))
    private var remoteAuthorizationGenerations: [String: Int] = [:]
    private var remoteRefreshes: [String: Task<RemoteOAuthCredential, Error>] = [:]

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
    private let packageSafetyLookup = PackageSafetyLookup()
    /// Package checks the user asked for this session, by server id (§7.10).
    private var packageSafety: [String: PackageSafetyReport] = [:]
    /// Doctor findings about Gateway routes, by server id. Worked out when the
    /// configs are re-read and when the activity journal is, not on every
    /// state reply: it reads client configs and the journal's tail.
    private var gatewayFindings: [String: [DoctorFinding]] = [:]
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
            store: KeychainSecretStore(), parkStore: parkStore,
            pathOverrides: settings.clientPathOverrides
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
        if pathsChanged {
            // Pointing a client at another file is Kytto looking somewhere
            // else, not a change the client has to restart for. Without a new
            // baseline every server in the old file counted as pending (§7.1).
            let keys = Set(updated.clientPathOverrides.keys).union(previous.clientPathOverrides.keys)
            for key in keys where updated.clientPathOverrides[key] != previous.clientPathOverrides[key] {
                if let clientID = ClientID(rawValue: key) { acknowledgeRestart(clientID: clientID) }
            }
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
        portableSetup = nil
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
            store: KeychainSecretStore(), parkStore: parkStore,
            pathOverrides: settings.clientPathOverrides
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

    /// Clients the menu bar item can toggle in: installed and writable. A
    /// read-only source (an extension bundle, a custom source, a Claude Code
    /// project scope) would offer toggles that can only ever fail.
    var menuBarCandidates: [DiscoveredClient] {
        current().clients.filter { $0.isInstalled && !$0.isReadOnly }
    }

    /// The client the menu bar item acts on: the user's choice, or the one with
    /// the most servers, which is the right guess until they say otherwise (§7.7).
    /// A choice that is no longer a candidate — uninstalled, removed, or a
    /// read-only source saved by an earlier version — falls back to that guess
    /// rather than leaving the menu with no client at all.
    var menuBarClient: ClientID? {
        let candidates = menuBarCandidates
        if let chosen = settings.menuBarClient, candidates.contains(where: { $0.id == chosen }) {
            return chosen
        }
        return candidates.max { $0.serverCount < $1.serverCount }?.id
    }

    // MARK: - Reading

    func current() -> DiscoveryResult {
        if let cached { return cached }
        return reload()
    }

    /// Re-reads every client's configuration now, on the main actor.
    ///
    /// For the write paths: a command that has just written answers with the
    /// state it produced, and the next write's digest check needs what this
    /// read. Anything that only wants to catch up with the disk uses
    /// `reloadInBackground()` instead.
    @discardableResult
    func reload() -> DiscoveryResult {
        // Any background pass still reading started before this one; its
        // result is older and must not replace this.
        reloadGeneration += 1
        let output = Self.discover(discoveryInput(), ledger: ledger)
        apply(output, alwaysNotify: true)
        return output.result
    }

    /// Re-reads off the main actor and applies the result only if nothing
    /// newer has been read or written meanwhile. Returns whether the state the
    /// window shows changed.
    ///
    /// Concurrent requests are coalesced by `ReloadScheduler`: one pass at a
    /// time, and one more after it for everything asked in the meantime.
    @discardableResult
    func reloadInBackground() async -> Bool {
        await reloads.request().changed
    }

    private lazy var reloads = ReloadScheduler { [weak self] in
        await self?.backgroundPass() ?? false
    }
    private var reloadGeneration = 0
    private var lastPushedExternalPass = 0

    private func backgroundPass() async -> Bool {
        reloadGeneration += 1
        let generation = reloadGeneration
        let revision = ledger.revision
        let input = discoveryInput()
        // A private ledger: what this pass read reaches the digest guard only
        // together with the state the window is then shown (§6.4).
        let passLedger = DigestLedger()
        let output = await Task.detached(priority: .userInitiated) {
            Self.discover(input, ledger: passLedger)
        }.value

        // A synchronous reload or a write happened while this pass was
        // reading. Both are newer than what it read; the write's own watcher
        // event brings another pass.
        guard generation == reloadGeneration, revision == ledger.revision else { return false }
        ledger.adopt(passLedger)
        return apply(output, alwaysNotify: false)
    }

    /// Installs a discovery result. Returns whether anything the window or the
    /// menu bar shows changed, so a re-read that found the same servers — the
    /// usual outcome when Claude Code rewrites its file after a message — costs
    /// no redraw.
    @discardableResult
    private func apply(_ output: DiscoveryOutput, alwaysNotify: Bool) -> Bool {
        let previous = cached
        let previousRestarts = pendingRestarts
        let previousDismissed = dismissedRestartClients
        cached = output.result
        reconcileRestartState(using: output.result)
        refreshGatewayFindings()
        let changed = previous != output.result
            || pendingRestarts != previousRestarts
            || dismissedRestartClients != previousDismissed
        if changed || alwaysNotify { onStateChanged?() }
        // Re-arm each time: a client that just gained a config directory becomes
        // watchable only once it exists.
        watcher.watch(directories: output.watchedDirectories)
        return changed
    }

    private nonisolated struct DiscoveryInput: Sendable {
        let home: URL
        let settings: KyttoSettings
        let parkStore: ParkStore
        let metadata: MetadataStore
    }

    private nonisolated struct DiscoveryOutput: Sendable {
        let result: DiscoveryResult
        let watchedDirectories: [URL]
    }

    private func discoveryInput() -> DiscoveryInput {
        DiscoveryInput(home: home, settings: settings, parkStore: parkStore, metadata: metadata)
    }

    /// Everything a pass does, with nothing borrowed from the main actor.
    private nonisolated static func discover(_ input: DiscoveryInput, ledger: DigestLedger) -> DiscoveryOutput {
        let discovery = Discovery(
            home: input.home,
            locator: SystemAppLocator(),
            ledger: ledger,
            parked: input.parkStore.all(),
            metadata: input.metadata.all(),
            pathOverrides: input.settings.clientPathOverrides,
            additionalDescriptors: input.settings.customConfigSources.map(ClientRegistry.customDescriptor)
        )
        return DiscoveryOutput(result: discovery.run(), watchedDirectories: discovery.watchedDirectories())
    }

    // MARK: - Portable setup (native file selection and secret inputs)

    private func makePortableSetup() -> PortableSetupService {
        PortableSetupService(
            home: home, writer: ConfigWriter(backups: backups), parkStore: parkStore, ledger: ledger,
            pathOverrides: settings.clientPathOverrides
        )
    }

    func exportPortableSetup(to file: URL, includeReviewedContracts: Bool) throws -> PortableSetupSummary {
        try makePortableSetup().export(to: file, servers: current().servers, includeReviewedContracts: includeReviewedContracts)
    }

    func inspectPortableSetup(file: URL) throws -> PortableSetupSummary {
        let service = makePortableSetup()
        let summary = try service.inspect(file: file)
        portableSetup = service
        return summary
    }

    func previewPortableSetup(file: URL, targetClients: [ClientID], values: [String: String]) throws -> PortableImportPreview {
        guard let service = portableSetup else { throw PortableSetupError.stalePreview }
        return try service.preview(file: file, targetClients: targetClients, values: values, existing: current().servers)
    }

    func applyPortableSetup(previewID: String) throws {
        guard let service = portableSetup else { throw PortableSetupError.stalePreview }
        _ = try service.apply(previewID: previewID)
        portableSetup = nil
        reload()
    }

    func discardPortableSetup(previewID: String) {
        portableSetup?.discard(previewID: previewID)
        portableSetup = nil
    }

    func pendingContractImports() -> (entries: [PortablePendingContractSummary], error: String?) {
        do {
            return (try makePortableSetup().pendingContractSummary(), nil)
        } catch {
            return ([], "Imported contract fingerprints could not be read. The local trusted baseline was left unchanged.")
        }
    }

    // MARK: - MCP provenance

    /// Builds informational provenance for one current server. A registry
    /// result is applied only when it still belongs to the same inferred package
    /// and source kind, so editing a command cannot leave an old latest version
    /// attached to a new server definition.
    func provenance(for server: Server) -> MCPProvenance {
        let identified = MCPProvenanceResolver.identify(server: server)
        // Only a version that comes from the package itself (a pinned launch
        // or a local package.json) is compared with the registry.
        let installedVersion = identified.installedVersion
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

    func checkPackageSafety(serverID: String) async throws -> PackageSafetyReport {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        guard let launch = PackageSafety.launch(of: server) else {
            throw PackageSafetyLookup.LookupError.notAPackage
        }
        let report = try await packageSafetyLookup.check(launch)
        packageSafety[server.id] = report
        return report
    }

    func packageSafetyReport(for serverID: String) -> PackageSafetyReport? {
        packageSafety[serverID]
    }

    // MARK: - MCP library and agent import

    private struct ImportInput: Sendable {
        let draft: ServerDraft
        let environmentKeys: [String]
        let warnings: [String]
    }

    private var libraryScanGeneration = 0

    func libraryScan(path: String) async throws -> MCPLibraryScanResult {
        let url = URL(filePath: path).standardizedFileURL
        guard url.isFileURL, url.path.hasPrefix("/"), isDirectory(url) else {
            throw AppError.invalidLibraryDirectory
        }
        // Up to 5,000 entries and as many file reads: off the main actor, so the
        // window keeps drawing while a large directory is walked.
        libraryScanGeneration += 1
        let generation = libraryScanGeneration
        let result = await Task.detached(priority: .userInitiated) {
            MCPLibraryScanner(root: url).scan()
        }.value
        // A slower, older scan finishing last must not replace the candidates
        // of the one the window is showing.
        if generation == libraryScanGeneration {
            libraryCandidates = Dictionary(uniqueKeysWithValues: result.candidates.map { ($0.id, $0) })
        }
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
        // Read once, not once per client: this runs on every reload.
        let routes = (try? gatewayRoutes.all()) ?? []

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
            for route in routes where route.clientID == clientID {
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
        } else if service.status != .notRegistered && service.status != .notFound {
            // Also when it is waiting for approval: approved later, it would
            // open Kytto at login although the setting says off.
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
        // Read before the rotation replaces it: a Gateway route keeps its own
        // copy of the value and is found by comparing against the old one.
        let oldValue = secretsService.reveal(secretID: id, servers: current().servers)
        let result = try secretsService.rotate(
            secretID: id,
            to: newValue,
            servers: current().servers,
            alsoStoreInSecretStore: storeInSecretStore
        )
        for clientID in Set(result.updated.map(\.clientID)) {
            pendingRestarts[clientID, default: 0] += 1
        }
        if let oldValue {
            let routes = gatewayMigration.rotateEnvironmentValue(key: result.key, from: oldValue, to: newValue)
            for clientID in Set(routes.map(\.clientID)) {
                pendingRestarts[clientID, default: 0] += 1
            }
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
        let resource = server.transport == .http ? server.runnableURL.flatMap { try? RemoteOAuthClient.canonicalResource($0) } : nil
        let authorizationGeneration = resource.map { remoteAuthorizationGenerations[$0, default: 0] }
        var credential: RemoteOAuthCredential?
        var refreshed = false
        if let resource {
            do {
                credential = try remoteOAuthCredentials.load(resourceURL: resource)
                if let saved = credential, saved.requiresRefresh {
                    credential = try await refreshedCredential(saved, resource: resource, generation: authorizationGeneration)
                    refreshed = true
                }
            } catch {
                if remoteAuthorizationGenerations[resource, default: 0] != authorizationGeneration {
                    throw AppError.authorizationChanged
                }
                let health = HealthResult(
                    status: .needsAuthorization, checkedAt: .timestamp(), toolCount: nil, tools: [],
                    message: "Kytto's saved authorization could not be refreshed or read. Sign in again.",
                    stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil
                )
                if let latest = current().servers.first(where: { $0.id == server.id }),
                   ClientDefinition(from: latest) == ClientDefinition(from: server) {
                    try metadata.record(health: health, tokenWeight: nil, for: server.id)
                }
                return health
            }
        }
        let checked = directDefinitionForCheck(server)
        func run(_ headers: [String: String]?) async -> (health: HealthResult, tokenWeight: TokenWeight?) {
            await Task.detached(priority: .userInitiated) {
                checker.check(checked, httpHeaders: headers)
            }.value
        }
        var outcome = await run(credential?.authorizationHeaders)
        // A token revoked early, or one issued without an expiry, shows up only
        // as the server refusing it. One refresh and one retry, not a loop.
        if outcome.health.status == .needsAuthorization, !refreshed, let resource,
           let saved = credential, saved.canRefresh,
           let renewed = try? await refreshedCredential(saved, resource: resource, generation: authorizationGeneration) {
            outcome = await run(renewed.authorizationHeaders)
        }

        // The check can take ten seconds and the window stays usable meanwhile.
        // A server deleted (or renamed) in that time must not get its metadata
        // back from a result nobody is waiting for any more.
        guard let latest = current().servers.first(where: { $0.id == server.id }),
              ClientDefinition(from: latest) == ClientDefinition(from: server),
              resource.map({ remoteAuthorizationGenerations[$0, default: 0] == authorizationGeneration }) ?? true else {
            return outcome.health
        }
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
        // One run at a time: the window and the menu bar can both start one,
        // and two would start every server twice, in parallel, which is what
        // "sequential on purpose" is for.
        guard !isCheckingAll else { throw AppError.checkAllAlreadyRunning }
        // The menu bar item is a static menu rebuilt on state changes; it has
        // to hear both edges of the run, or it keeps offering a run that is
        // already going, and afterwards stays on "Checking All Servers…".
        isCheckingAll = true
        onStateChanged?()
        defer {
            isCheckingAll = false
            onStateChanged?()
        }
        let servers = current().servers.filter { $0.transport != .sse }
        var persistenceFailures = 0
        var interrupted = 0
        for (index, server) in servers.enumerated() {
            await onProgress(server.name, index + 1, servers.count)
            do {
                _ = try await recordHealth(for: server)
            } catch AppError.authorizationChanged {
                // A sign-in during the run: that server is simply not recorded,
                // which is not a disk problem.
                interrupted += 1
            } catch {
                persistenceFailures += 1
            }
        }
        if !servers.isEmpty { reload() }
        if persistenceFailures > 0 {
            throw AppError.healthResultsCouldNotBeSaved(persistenceFailures)
        }
        if interrupted > 0 {
            throw AppError.checksInterrupted(interrupted)
        }
    }

    /// Whether a check of every server is in progress, for menus that offer one.
    private(set) var isCheckingAll = false

    /// The server a Gateway route stands for, so a health check measures the
    /// server rather than the helper. Checked through the helper, `tools/list`
    /// comes back already masked: hidden tools would vanish from the tool
    /// picker (and saving it unchanged would drop the mask), Contract Guard
    /// would see tools removed, and every check would log a client session in
    /// Live Activity that no client started.
    private func directDefinitionForCheck(_ server: Server) -> Server {
        guard server.transport == .stdio, let command = server.command,
              URL(filePath: command).lastPathComponent == "kytto-mcp-proxy",
              let flag = server.args.firstIndex(of: "--route"), server.args.indices.contains(flag + 1),
              let launch = try? gatewayRoutes.resolve(id: server.args[flag + 1], secrets: gatewaySecretStore)
        else { return server }
        var direct = Server(
            id: server.id, name: server.name, transport: .stdio,
            command: launch.route.command, args: launch.route.arguments,
            env: launch.environment.map { EnvEntry(key: $0.key, value: $0.value) }.sorted { $0.key < $1.key },
            url: nil, enabledIn: server.enabledIn, origin: server.origin, fingerprint: server.fingerprint,
            isBundled: server.isBundled, definitionSource: server.definitionSource
        )
        direct.definitionsByClient = server.definitionsByClient
        return direct
    }

    /// One refresh per resource at a time. Refresh tokens rotate, so two
    /// checks of the same remote refreshing in parallel would spend the same
    /// token twice — and an authorization server that detects the replay
    /// revokes the whole grant. Later callers wait for the running refresh.
    private func refreshedCredential(_ saved: RemoteOAuthCredential, resource: String, generation: Int?) async throws -> RemoteOAuthCredential {
        if let running = remoteRefreshes[resource] { return try await running.value }
        let task = Task.detached(priority: .userInitiated) { try RemoteOAuthClient().refresh(saved) }
        remoteRefreshes[resource] = task
        defer { remoteRefreshes[resource] = nil }
        let renewed = try await task.value
        guard remoteAuthorizationGenerations[resource, default: 0] == generation else {
            throw AppError.authorizationChanged
        }
        try remoteOAuthCredentials.store(renewed)
        return renewed
    }

    /// The saved credential's headers and scopes, for a step-up sign-in.
    func remoteAuthorizationContext(serverID: String) -> (headers: [String: String], scopes: [String])? {
        guard let url = try? remoteServerURL(serverID: serverID),
              let resource = try? RemoteOAuthClient.canonicalResource(url),
              let credential = try? remoteOAuthCredentials.load(resourceURL: resource) else { return nil }
        return (credential.authorizationHeaders, credential.scopes)
    }

    func remoteServerURL(serverID: String) throws -> String {
        guard let server = current().servers.first(where: { $0.id == serverID }), server.transport == .http,
              let url = server.runnableURL else { throw AppError.unknownServer(serverID) }
        return url
    }

    func saveRemoteAuthorization(_ credential: RemoteOAuthCredential, serverID: String) throws {
        let currentURL = try RemoteOAuthClient.canonicalResource(remoteServerURL(serverID: serverID))
        guard currentURL == credential.configuration.resourceURL else { throw RemoteOAuthError.resourceMismatch }
        remoteAuthorizationGenerations[currentURL, default: 0] += 1
        try remoteOAuthCredentials.store(credential)
    }

    func forgetRemoteAuthorization(serverID: String) throws {
        let resource = try RemoteOAuthClient.canonicalResource(remoteServerURL(serverID: serverID))
        remoteAuthorizationGenerations[resource, default: 0] += 1
        try remoteOAuthCredentials.delete(resourceURL: resource)
    }

    // MARK: - Doctor, contract guard and activity

    func doctorReports() -> [DoctorServerReport] {
        let result = current()
        return MCPDoctor.reports(
            for: result.servers,
            clients: result.clients.map(\.id),
            packageSafety: packageSafety,
            gateway: gatewayFindings
        )
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
    static func reviewToken(for changes: [ContractChange]) -> String {
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        let data = (try? encoder.encode(changes)) ?? Data()
        return SHA256.hash(data: data).prefix(12).map { String(format: "%02x", $0) }.joined()
    }

    func acknowledgeContract(serverID: String, reviewToken: String? = nil) throws {
        guard current().servers.contains(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        if let reviewToken, let changes = metadata.all()[serverID]?.contractChanges, !changes.isEmpty,
           Self.reviewToken(for: changes) != reviewToken {
            throw AppError.contractChangedSinceShown
        }
        let portable = makePortableSetup()
        let pending = try portable.pendingContractSummary().first { $0.serverID == serverID }
        if let pending, pending.matching == nil { throw PortableSetupError.localContractNotChecked }
        try metadata.acknowledgeContract(for: serverID)
        if pending != nil {
            try portable.reviewPendingContract(serverID: serverID)
        }
        reload()
    }

    func doctorFixPreview(
        serverID: String,
        action: DoctorAction = .pinResolvedCommand
    ) throws -> DoctorFixPreview {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        try requireDirect(serverID: serverID)
        if action == .pinPackageVersion { return try packagePinPreview(for: server) }
        guard server.transport == .stdio,
              let currentCommand = server.command,
              let resolvedCommand = server.health?.resolvedCommand,
              server.health?.status == .passed,
              resolvedCommand.hasPrefix("/"),
              MCPDoctor.resolvedCommand(resolvedCommand, matches: currentCommand.trimmingCharacters(in: .whitespacesAndNewlines))
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

    func applyDoctorFix(
        serverID: String,
        action: DoctorAction = .pinResolvedCommand,
        confirmedVersion: String? = nil
    ) throws -> AuthoringResult {
        if action == .pinPackageVersion {
            return try applyPackagePin(serverID: serverID, confirmedVersion: confirmedVersion)
        }
        let preview = try doctorFixPreview(serverID: serverID)
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        var draft = ServerDraft(editing: server)
        draft.command = preview.replacementCommand
        return try updateServer(draft, serverID: serverID)
    }

    /// The package pin, before it is written (§7.10).
    ///
    /// The version comes from one place only: the registry lookup the user
    /// asked for with "Check latest" (`checkProvenanceLatest`), for this very
    /// package. Nothing here makes a request, so a preview never reaches the
    /// network on its own. `serverInfo.version` from a health check is not a
    /// source: it is whatever the server says about itself, frequently not its
    /// package's release number, and pinning a version the registry does not
    /// have would break the server on its next start.
    private func packagePinPreview(for server: Server) throws -> DoctorFixPreview {
        guard let launch = PackagePin.unpinnedLaunch(in: server), PackagePin.canPin(server) else {
            throw AppError.noUnpinnedPackage(server.name)
        }
        let checked = provenance(for: server)
        let kind: MCPProvenanceKind = launch.ecosystem == .npm ? .npm : .python
        guard checked.kind == kind,
              checked.packageName == launch.name,
              let version = checked.latestVersion
        else { throw AppError.packageVersionNotChecked(launch.name) }

        let edits = PackagePin.edits(for: server, version: version)
        guard !edits.isEmpty else { throw AppError.packageVersionNotPinnable(launch.name, version) }
        return DoctorFixPreview(
            serverID: server.id,
            serverName: server.name,
            currentCommand: server.command ?? "",
            replacementCommand: server.command ?? "",
            clientIDs: edits.keys.sorted { $0.rawValue < $1.rawValue },
            action: .pinPackageVersion,
            packageName: launch.name,
            version: version,
            argumentChanges: edits
                .sorted { $0.key.rawValue < $1.key.rawValue }
                .map { DoctorArgumentChange(clientID: $0.key, current: $0.value.expected, replacement: $0.value.replacement) }
        )
    }

    /// Writes the pin the user saw. A lookup that has moved on since the preview
    /// is a different decision, so a mismatch writes nothing.
    private func applyPackagePin(serverID: String, confirmedVersion: String?) throws -> AuthoringResult {
        let preview = try doctorFixPreview(serverID: serverID, action: .pinPackageVersion)
        guard let version = preview.version, confirmedVersion == version else {
            throw AppError.packageVersionChanged(preview.packageName ?? preview.serverName)
        }
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let result = try authoring.replaceArgument(
            of: server,
            edits: PackagePin.edits(for: server, version: version)
        )
        noteRestarts(result)
        reload()
        return result
    }

    func activity(limit: Int = 500) -> GatewayActivitySummary {
        let summary = gatewayActivity.summary(limit: limit)
        refreshGatewayFindings(events: summary.events)
        return summary
    }

    // MARK: - Profiles

    func profiles() -> [ServerProfile] {
        profileStore.all()
    }

    // MARK: - Gateway (§3.3)

    func routes() -> [GatewayRoute] {
        (try? gatewayRoutes.all()) ?? []
    }

    /// One client's tool count against its cap. The caller reads the route
    /// store once and passes it in, rather than once per client.
    func toolBudget(
        for client: DiscoveredClient,
        in result: DiscoveryResult,
        routes: [GatewayRoute]
    ) -> ClientToolBudget {
        ToolBudget.evaluate(
            clientID: client.id,
            limit: client.toolLimit,
            servers: result.servers,
            routes: routes
        )
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

    /// Points a route at this copy of the helper, after Kytto was moved.
    func repairGatewayRoute(routeID: String) throws -> GatewayMigrationResult {
        let result = try gatewayMigration.repair(routeID: routeID)
        pendingRestarts[result.route.clientID, default: 0] += 1
        reload()
        return result
    }

    /// Restores Direct mode for every route — what to do before removing
    /// Kytto, since the helper the configs point at goes with the app.
    ///
    /// Routes are independent, so one that cannot be restored (its config
    /// changed on disk, say) does not stop the others; it is reported.
    func restoreAllDirect() -> GatewayRestoreAllReport {
        var report = GatewayRestoreAllReport()
        for route in routes() {
            do {
                let result = try gatewayMigration.restore(routeID: route.id)
                pendingRestarts[route.clientID, default: 0] += 1
                report.restored.append(route)
                if !result.missingEnvironmentKeys.isEmpty {
                    report.missingEnvironment.append((route, result.missingEnvironmentKeys))
                }
            } catch {
                report.failures.append((route, error.localizedDescription))
            }
        }
        reload()
        return report
    }

    /// Re-derives the Gateway findings Doctor shows. Reads configs and, unless
    /// the caller already has them, the journal's tail; never the keychain.
    private func refreshGatewayFindings(events: [GatewayEvent]? = nil) {
        let known = routes()
        let diagnoses = gatewayMigration.diagnose()
        // Without a route or a Gateway definition there is nothing to explain,
        // and no reason to read the journal.
        guard !known.isEmpty || !diagnoses.isEmpty else {
            gatewayFindings = [:]
            return
        }
        // The journal outlives a route. Once a server is back in Direct mode,
        // what its Gateway sessions did is history, not something to act on.
        let live = (events ?? gatewayActivity.summary().events).filter { event in
            known.contains { $0.serverID == event.serverID && $0.clientID.rawValue == event.clientID }
        }
        gatewayFindings = GatewayDoctor.findings(diagnoses: diagnoses, events: live, routes: known)
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
            // A plugin's servers are switched with the plugin, in its client;
            // listing them under "Switch off" promised a change that always fails.
            if case .plugin = server.origin { return nil }
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
        var applied: [ProfileAppliedChange] = []

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
                applied.append(ProfileAppliedChange(serverID: change.server.id, enabled: change.enabled))
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
            failures: failures,
            applied: applied
        )
    }

    /// Puts back the switches a profile apply made, newest first.
    ///
    /// A profile is applied one server at a time, so a failure part-way leaves
    /// the client between two profiles. This is the way back to where it
    /// started: each switch is reversed through the same guarded write, and a
    /// server that was removed to switch it off comes back byte for byte from
    /// the park store. A switch that has since been changed again is left.
    func revertProfileApply(_ changes: [ProfileAppliedChange], in clientID: ClientID) -> ProfileRevertResult {
        var reverted = 0
        var failures: [ProfileApplyFailure] = []
        for change in changes.reversed() {
            guard let server = current().servers.first(where: { $0.id == change.serverID }) else {
                failures.append(ProfileApplyFailure(
                    serverName: change.serverID,
                    message: "This server is no longer present in any client."
                ))
                continue
            }
            // Only undo what the apply did: if it is no longer in the state the
            // apply left it in, someone changed it since and it is theirs.
            guard (server.enabledIn[clientID] == .enabled) == change.enabled else { continue }
            do {
                try requireDirect(serverID: server.id, clientID: clientID)
                let result = try toggles.setEnabled(!change.enabled, server: server, in: clientID)
                reverted += 1
                if result.requiresRestart { pendingRestarts[clientID, default: 0] += 1 }
                reload()
            } catch {
                failures.append(ProfileApplyFailure(
                    serverName: server.name,
                    message: (error as? LocalizedError)?.errorDescription ?? String(describing: error)
                ))
            }
        }
        return ProfileRevertResult(clientID: clientID, revertedCount: reverted, failures: failures)
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

    /// States which of a server's known tools are off in one client (§7.11).
    func setDisabledTools(_ names: [String], serverID: String, clientID: ClientID) throws -> ToolSwitchResult {
        guard let server = current().servers.first(where: { $0.id == serverID }) else {
            throw AppError.unknownServer(serverID)
        }
        let service = ToolSwitchService(
            home: home, writer: ConfigWriter(backups: backups),
            ledger: ledger, pathOverrides: settings.clientPathOverrides
        )
        let result = try service.setDisabledTools(
            Set(names), among: server.knownTools.map(\.name), server: server, in: clientID
        )
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
        // The baseline and any unreviewed Contract Guard alert follow the server.
        try? metadata.rename(from: serverID, to: Server.identity(for: result.serverName))
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
        // What the restored file defines, read from the backup itself and before
        // the restore — which backs up the current file and may prune this one.
        // Not from the reloaded model: discovery draws parked copies into it as
        // that client's definitions, so every parked server would look restored
        // and lose its only copy.
        let restoredNames = backups.serverNames(
            in: backup, home: home, pathOverrides: settings.clientPathOverrides
        ) ?? []
        // Checked against what Kytto last read, like any other write (§6.4).
        try backups.restore(
            backup, home: home, pathOverrides: settings.clientPathOverrides, ledger: ledger
        )
        pendingRestarts[clientID, default: 0] += 1
        reload()
        let restoredServerIDs = Set(restoredNames.map(Server.identity(for:)))
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

    /// What Restart now did for each client it was asked about.
    struct ClientRestartReport {
        var restarted: [ClientID] = []
        var notRunning: [ClientID] = []
        var failures: [(clientID: ClientID, message: String)] = []
        /// For each client that was restarted: the servers whose definition
        /// or state had changed, and are on. The page checks these, so a
        /// restart that loaded a broken definition is not reported as done.
        var changedServers: [(clientID: ClientID, serverIDs: [String])] = []
    }

    /// The servers a client's pending restart is about, and that it will now
    /// try to start: changed since the last acknowledged state, switched on,
    /// and of a transport a health check can run.
    private func serversAwaitingRestart(in clientID: ClientID) -> [String] {
        let result = current()
        let now = restartSnapshots(from: result)[clientID, default: [:]]
        let baseline = restartBaselines[clientID, default: [:]]
        let routes = routes()
        var ids: Set<String> = []
        for key in Set(now.keys).union(baseline.keys) where now[key] != baseline[key] {
            if key.hasPrefix("__gateway.") {
                let routeID = String(key.dropFirst("__gateway.".count))
                if let route = routes.first(where: { $0.id == routeID }) { ids.insert(route.serverID) }
            } else {
                ids.insert(key)
            }
        }
        return result.servers
            .filter { ids.contains($0.id) && $0.enabledIn[clientID] == .enabled && $0.transport != .sse }
            .map(\.id)
            .sorted()
    }

    /// Quits and reopens each client's app, one after another, and clears a
    /// client's notice only once its app has actually quit (Restart now, §7.1).
    /// A client that was not running needs nothing: its next launch reads the
    /// new configuration, so its notice clears too. A refusal to quit leaves
    /// the notice exactly as it was.
    func restartClients(_ clientIDs: [ClientID]) async -> ClientRestartReport {
        var report = ClientRestartReport()
        for id in clientIDs {
            guard let descriptor = ClientRegistry.descriptorIfKnown(for: id) else {
                report.failures.append((id, "Kytto cannot restart this client for you. Restart it yourself, then choose I restarted."))
                continue
            }
            // Before the acknowledgement below resets what "changed" means.
            let changed = serversAwaitingRestart(in: id)
            do {
                switch try await ClientRestarter.restart(descriptor) {
                case .restarted:
                    report.restarted.append(id)
                    if !changed.isEmpty { report.changedServers.append((id, changed)) }
                case .notRunning: report.notRunning.append(id)
                }
                acknowledgeRestart(clientID: id)
            } catch let error as ClientRestarter.RestartError {
                // It quit, so whatever opens it next reads the new file.
                if case .couldNotReopen = error { acknowledgeRestart(clientID: id) }
                report.failures.append((id, error.localizedDescription))
            } catch {
                report.failures.append((id, error.localizedDescription))
            }
        }
        return report
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
        // Kytto's own writes trip the watcher too, and Claude Code rewrites
        // `~/.claude.json` after nearly every message, so this fires far more
        // often than a server changes. The re-read runs off the main actor and
        // always records the new digests; the window is redrawn only when what
        // it shows actually changed, and once per pass however many events
        // that pass answered.
        Task { [weak self] in
            guard let self else { return }
            let outcome = await reloads.request()
            guard outcome.changed, outcome.pass > lastPushedExternalPass else { return }
            lastPushedExternalPass = outcome.pass
            onExternalChange?()
        }
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
        case checkAllAlreadyRunning
        case contractChangedSinceShown
        case checksInterrupted(Int)
        case authorizationChanged
        case noDrift(String)
        case launchAtLoginRollbackFailed(original: String, rollback: String)
        case noUnpinnedPackage(String)
        case packageVersionNotChecked(String)
        case packageVersionNotPinnable(String, String)
        case packageVersionChanged(String)

        var errorDescription: String? {
            switch self {
            case .unknownServer(let id): "No server with id \(id)."
            case .unknownBackup(let id): "No backup with id \(id)."
            case .authorizationChanged: "Authorization changed while the check was running. Check the server again."
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
            case .contractChangedSinceShown:
                "The contract changed again while you were reviewing it. Kytto did not mark it reviewed; read the new changes first."
            case .checkAllAlreadyRunning:
                "Kytto is already checking every server. Wait for that run to finish."
            case .checksInterrupted(let count):
                "\(count) server\(count == 1 ? " was" : "s were") not recorded because \(count == 1 ? "its" : "their") sign-in or saved authorization changed during the run. Check \(count == 1 ? "it" : "them") again."
            case .noDrift(let name):
                "Every editable copy of “\(name)” already matches. There is nothing to unify."
            case .launchAtLoginRollbackFailed(let original, let rollback):
                "Could not save Settings (\(original)) and could not restore the previous login-item state (\(rollback))."
            case .noUnpinnedPackage(let name):
                "“\(name)” has no unpinned package that Kytto can pin in place."
            case .packageVersionNotChecked(let package):
                "Check the latest release of \(package) first. Kytto pins only a version you looked up."
            case .packageVersionNotPinnable(let package, let version):
                "\(version) is not a single exact release of \(package), so Kytto did not write it."
            case .packageVersionChanged(let package):
                "The checked version of \(package) changed after the preview. Kytto did not write anything; preview the pin again."
            }
        }
    }
}

struct ContractAlert: Sendable {
    let serverID: String
    let serverName: String
    let changedAt: Date
    let changes: [ContractChange]

    /// Names exactly these changes. The page sends it back when the user marks
    /// the alert reviewed, so a check that recorded something newer in the
    /// meantime cannot be accepted along with what was on screen.
    var reviewToken: String { AppModel.reviewToken(for: changes) }
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
    var action: DoctorAction = .pinResolvedCommand
    /// Package pins only: what is pinned, to which release, and the one
    /// argument each client copy has now and will have after.
    var packageName: String?
    var version: String?
    var argumentChanges: [DoctorArgumentChange] = []
}

struct DoctorArgumentChange: Sendable {
    let clientID: ClientID
    let current: String
    let replacement: String
}

struct GatewayRestoreAllReport {
    var restored: [GatewayRoute] = []
    var failures: [(route: GatewayRoute, message: String)] = []
    /// Routes whose Direct definition was rebuilt without some values.
    var missingEnvironment: [(route: GatewayRoute, keys: [String])] = []
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
    /// The switches that were actually made, in order, so they can be undone.
    let applied: [ProfileAppliedChange]
}

/// One switch a profile apply made: the server and the state it was put in.
struct ProfileAppliedChange: Sendable {
    let serverID: String
    let enabled: Bool
}

struct ProfileRevertResult: Sendable {
    let clientID: ClientID
    let revertedCount: Int
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
