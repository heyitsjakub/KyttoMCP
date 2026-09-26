import Foundation
import KyttoCore

/// What crosses the IPC boundary.
///
/// Deliberately not the core model. Two rules are enforced here rather than
/// hoped for at every call site:
///
/// 1. Secret values never leave the native side. `env` becomes keys plus a
///    `hasValue` flag — the value itself stays in Swift until M5 injects it at
///    spawn time (§6).
/// 2. The web layer receives display strings and opaque ids, never a path it has
///    to interpret. Separators, `~` and `%APPDATA%` are native concerns (§3.2).

struct AppInfoDTO: Encodable {
    let version: String
    let build: String
    /// The web branches on capabilities, never on the name of the OS (§3.2).
    let capabilities: [String]
    /// Present so the UI can say "Kytto for macOS" — not for branching.
    let platformDisplayName: String
    /// What this platform calls the credential store Kytto keeps its copy in.
    ///
    /// The secrets screen has to name it — "Keep a copy in …" is meaningless
    /// otherwise — and the web layer is shared, so the name is reported rather
    /// than written into the page (§3.2). Display only, like
    /// `platformDisplayName`; nothing branches on it.
    let secretStoreDisplayName: String
}

struct UpdateCheckDTO: Encodable {
    enum Status: String, Encodable {
        case updateAvailable
        case upToDate
        case skipped
    }

    let status: Status
    let currentVersion: String
    let latestVersion: String?
    let checkedAt: Double?
    let releaseNotesURL: String?
    let sha256: String?
}

struct UpdateProgressDTO: Encodable {
    let phase: String
    let completedBytes: Int64
    let totalBytes: Int64?
    let fraction: Double?
    let message: String

    init(_ progress: UpdateProgress) {
        phase = progress.phase.rawValue
        completedBytes = progress.completedBytes
        totalBytes = progress.totalBytes
        fraction = progress.fraction
        message = progress.message
    }
}

struct UpdateStageDTO: Encodable {
    let token: String
    let version: String
    let byteCount: Int64
    let sha256: String
    let status: String

    init(_ stage: StagedUpdate) {
        token = stage.token
        version = stage.version
        byteCount = stage.byteCount
        sha256 = stage.sha256
        status = "verified"
    }
}

struct UpdateInstallDTO: Encodable {
    let version: String
    let relaunched: Bool
    let status: String

    init(_ result: InstalledUpdate) {
        version = result.version
        relaunched = result.relaunched
        status = result.relaunched ? "installedAndRelaunching" : "installed"
    }
}

struct ClientDTO: Encodable {
    enum State: String, Encodable {
        /// Installed, and its config exists.
        case ready
        /// Installed, but it has never configured MCP.
        case noConfig
        /// Config left behind by an uninstalled client.
        case orphanedConfig
        case notInstalled
    }

    let id: String
    let displayName: String
    /// A shorter name for surfaces that carry the client's identity some other
    /// way — the sidebar row under its icon and section heading. Null when the
    /// display name is already the short one.
    let shortName: String?
    let iconAsset: String
    let state: State
    let configPathDisplay: String
    let configFormatDisplay: String
    let offSwitchSummary: String
    let serverCount: Int
    let schemaQuirks: String
    let isReadOnly: Bool
    let configurationScope: String
    let scopeLabel: String

    init(_ client: DiscoveredClient) {
        id = client.id.rawValue
        displayName = client.displayName
        shortName = client.shortName
        iconAsset = client.iconAsset
        state = switch (client.isInstalled, client.configExists) {
        case (true, true): .ready
        case (true, false): .noConfig
        case (false, true): .orphanedConfig
        case (false, false): .notInstalled
        }
        configPathDisplay = client.configPathDisplay
        configFormatDisplay = client.configFormatDisplay
        offSwitchSummary = client.offSwitchSummary
        serverCount = client.serverCount
        schemaQuirks = client.schemaQuirks
        isReadOnly = client.isReadOnly
        configurationScope = client.configurationScope.rawValue
        scopeLabel = client.scopeLabel
    }
}

struct EnvEntryDTO: Encodable {
    let key: String
    /// Whether a value is set. The value itself is never sent.
    let hasValue: Bool

    nonisolated init(key: String, hasValue: Bool) {
        self.key = key
        self.hasValue = hasValue
    }
}

struct ToolSummaryDTO: Encodable {
    struct AnnotationsDTO: Encodable {
        let readOnlyHint: Bool?
        let destructiveHint: Bool?
        let idempotentHint: Bool?
        let openWorldHint: Bool?

        nonisolated init(_ annotations: ToolAnnotations) {
            readOnlyHint = annotations.readOnlyHint
            destructiveHint = annotations.destructiveHint
            idempotentHint = annotations.idempotentHint
            openWorldHint = annotations.openWorldHint
        }
    }

    let name: String
    let description: String?
    let inputSchemaJSON: String?
    let annotations: AnnotationsDTO?
    /// What this tool alone costs in context (§7.4). Null when the server was
    /// checked by a build that did not measure per tool.
    let tokenCount: Int?

    nonisolated init(_ tool: ToolSummary) {
        name = tool.name
        description = tool.description
        inputSchemaJSON = tool.inputSchemaJSON
        annotations = tool.annotations.map(AnnotationsDTO.init)
        tokenCount = tool.tokenCount
    }
}

struct HealthDTO: Encodable {
    let status: String
    let checkedAt: Double
    let toolCount: Int?
    let tools: [ToolSummaryDTO]
    let message: String?
    /// Where the server asked the user to sign in, for display beside the
    /// `needsAuthorization` status. Opening it goes through
    /// `health.openAuthorization`, which validates and opens the *recorded*
    /// URL — the web layer never sends one back.
    let authorizationURL: String?
    /// The server's own output, exactly as it wrote it (§6).
    let stderr: String?
    let durationSeconds: Double
    let serverName: String?
    let serverVersion: String?
    let protocolVersion: String?
    let capabilityNames: [String]
    let promptCount: Int?
    let resourceCount: Int?
    let inspectionNotes: [String]
    let resolvedCommand: String?
    let environmentSource: String?

    nonisolated init(_ health: HealthResult) {
        status = health.status.rawValue
        checkedAt = health.checkedAt.timeIntervalSince1970
        toolCount = health.toolCount
        tools = health.tools.map(ToolSummaryDTO.init)
        // Rendered from the structured failure at this boundary, so improved
        // wording reaches results that are already on disk. The stored string
        // only answers for records older than `failure`.
        message = health.currentMessage
        authorizationURL = health.authorizationURL
        stderr = health.stderr
        durationSeconds = health.durationSeconds
        serverName = health.serverName
        serverVersion = health.serverVersion
        protocolVersion = health.protocolVersion
        capabilityNames = health.capabilityNames ?? []
        promptCount = health.promptCount
        resourceCount = health.resourceCount
        inspectionNotes = health.inspectionNotes ?? []
        resolvedCommand = health.resolvedCommand
        environmentSource = health.environmentSource
    }
}

struct TokenWeightDTO: Encodable {
    let estimate: Int
    /// The vocabulary or heuristic behind the number, so the UI labels it
    /// honestly rather than always calling it an estimate (§7.4).
    let method: String
    /// False only when the rank table was unavailable and the old character
    /// heuristic answered instead.
    let isMeasured: Bool
    let measuredAt: Double
    let percentOfContext: Double
    let referenceContextWindow: Int

    nonisolated init(_ weight: TokenWeight) {
        estimate = weight.estimate
        method = weight.method
        isMeasured = weight.isMeasured
        measuredAt = weight.measuredAt.timeIntervalSince1970
        percentOfContext = weight.percentOfContext
        referenceContextWindow = TokenWeight.referenceContextWindow
    }
}

struct MCPProvenanceDTO: Encodable {
    let sourceKind: String
    let packageName: String?
    let sourceURL: String?
    let installedVersion: String?
    let latestVersion: String?
    let latestCheckedAt: Double?
    let confidence: String
    let maintenanceState: String

    nonisolated init(_ server: Server, provenance: MCPProvenance? = nil) {
        let resolvedProvenance = provenance ?? MCPProvenanceResolver.identify(server: server)
        let reportedVersion = server.health?.serverVersion ?? resolvedProvenance.installedVersion
        sourceKind = resolvedProvenance.kind.rawValue
        packageName = resolvedProvenance.packageName
        sourceURL = resolvedProvenance.sourceURL
        installedVersion = reportedVersion
        latestVersion = resolvedProvenance.latestVersion
        latestCheckedAt = resolvedProvenance.latestCheckedAt?.timeIntervalSince1970
        confidence = resolvedProvenance.confidence
        maintenanceState = MCPProvenanceResolver.maintenanceState(
            health: server.health,
            provenance: resolvedProvenance,
            installedVersion: reportedVersion
        ).rawValue
    }
}

struct ServerDTO: Encodable {
    let id: String
    let name: String
    let transport: String
    let commandSummary: String
    let command: String?
    let args: [String]
    let env: [EnvEntryDTO]
    let url: String?
    /// Client id → "enabled" | "disabled" | "absent".
    let enabledIn: [String: String]
    let originKind: String
    let isBundled: Bool
    /// The fact, not the path: the UI needs to know that copying this definition
    /// somewhere else may not start, and does not need to see what it is made of.
    let hasRelativePath: Bool
    let health: HealthDTO?
    let tokenWeight: TokenWeightDTO?
    let provenance: MCPProvenanceDTO

    nonisolated init(_ server: Server, provenance: MCPProvenance? = nil) {
        health = server.health.map(HealthDTO.init)
        tokenWeight = server.tokenWeight.map(TokenWeightDTO.init)
        self.provenance = MCPProvenanceDTO(server, provenance: provenance)
        id = server.id
        name = server.name
        transport = server.transport.rawValue
        commandSummary = server.commandSummary
        command = server.command
        args = server.args
        env = server.env.map { EnvEntryDTO(key: $0.key, hasValue: $0.hasValue) }
        url = server.url
        enabledIn = Dictionary(
            uniqueKeysWithValues: server.enabledIn.map { ($0.key.rawValue, $0.value.rawValue) }
        )
        originKind = switch server.origin {
        case .configFile: "configFile"
        case .claudeDesktopExtension: "extension"
        }
        isBundled = server.isBundled
        hasRelativePath = server.hasRelativePath
    }
}

/// Everything the matrix needs in one payload, so a toggle can update the whole
/// screen from a single reply instead of firing four follow-up commands.
struct StateDTO: Encodable {
    let clients: [ClientDTO]
    let servers: [ServerDTO]
    let diagnostics: [DiagnosticDTO]
    /// Client id → number of changes waiting for that client to restart.
    let pendingRestarts: [String: Int]
    /// Clients for which the pending-restart notice was hidden. The underlying
    /// pending count remains intact until the client is marked restarted.
    let dismissedRestarts: [String]
    let gatewayRoutes: [GatewayRouteDTO]
    let doctorReports: [DoctorServerReportDTO]
    let contractAlerts: [ContractAlertDTO]
    /// Servers whose clients do not agree on what they run. One entry per server,
    /// only when there is a disagreement.
    let drift: [ServerDriftDTO]

    @MainActor
    init(_ model: AppModel) {
        let result = model.current()
        clients = result.clients.map(ClientDTO.init)
        servers = result.servers.map { server in
            ServerDTO(server, provenance: model.provenance(for: server))
        }
        diagnostics = result.diagnostics.map(DiagnosticDTO.init)
        pendingRestarts = Dictionary(
            uniqueKeysWithValues: model.pendingRestarts.map { ($0.key.rawValue, $0.value) }
        )
        dismissedRestarts = model.dismissedRestartClients.map(\.rawValue).sorted()
        gatewayRoutes = model.routes().map(GatewayRouteDTO.init)
        doctorReports = model.doctorReports().map(DoctorServerReportDTO.init)
        contractAlerts = model.contractAlerts().map(ContractAlertDTO.init)
        drift = ServerDrift.detect(in: result.servers).map(ServerDriftDTO.init)
    }
}

/// One server's copies, as the UI is allowed to see them.
///
/// Commands and argument lists cross because they are already on screen in the
/// matrix. Environment **values** do not, in either direction: a key that two
/// clients set differently is reported by naming the key and saying they differ
/// (§6).
struct ServerDriftDTO: Encodable {
    struct Variant: Encodable {
        let clientIDs: [String]
        let commandSummary: String
        let transport: String
        let url: String?
        let environmentKeys: [String]
        let isBundled: Bool
        let canBeSource: Bool
    }

    let serverID: String
    let serverName: String
    /// `command` | `arguments` | `url` | `transport` | `environmentKeys` |
    /// `environmentValues`. The UI turns these into prose; it does not decide
    /// what counts as a difference.
    let fields: [String]
    let variants: [Variant]
    let unwritableClientIDs: [String]

    init(_ drift: ServerDrift) {
        serverID = drift.serverID
        serverName = drift.serverName
        fields = drift.fields.map(\.rawValue)
        unwritableClientIDs = drift.unwritableClientIDs.map(\.rawValue)
        variants = drift.variants.map { variant in
            let definition = variant.definition
            let command = ([definition.command].compactMap { $0 } + definition.args)
                .joined(separator: " ")
            return Variant(
                clientIDs: variant.clientIDs.map(\.rawValue),
                commandSummary: definition.url ?? command,
                transport: definition.transport.rawValue,
                url: definition.url,
                environmentKeys: definition.environmentKeys,
                isBundled: definition.isBundled,
                canBeSource: variant.canBeSource
            )
        }
    }
}

struct GatewayRouteDTO: Encodable {
    let id: String
    let serverID: String
    let serverName: String
    let clientID: String
    let createdAt: Double
    /// The tools this route lets through, or null when it lets everything
    /// through and keeps forwarding bytes untouched (§7.11).
    let exposedTools: [String]?

    init(_ route: GatewayRoute) {
        id = route.id
        serverID = route.serverID
        serverName = route.serverName
        clientID = route.clientID.rawValue
        createdAt = route.createdAt.timeIntervalSince1970
        exposedTools = route.exposedTools
    }
}

struct GatewayMigrationPreviewDTO: Encodable {
    let routeID: String
    let serverID: String
    let serverName: String
    let clientID: String
    let pathDisplay: String
    let formatDisplay: String
    let directDefinitionPreview: String
    let gatewayDefinitionPreview: String
    let environmentKeys: [String]

    init(_ preview: GatewayMigrationPreview) {
        routeID = preview.routeID
        serverID = preview.serverID
        serverName = preview.serverName
        clientID = preview.clientID.rawValue
        pathDisplay = preview.pathDisplay
        formatDisplay = preview.formatDisplay
        directDefinitionPreview = preview.directDefinitionPreview
        gatewayDefinitionPreview = preview.gatewayDefinitionPreview
        environmentKeys = preview.environmentKeys
    }
}

struct GatewayMigrationResultDTO: Encodable {
    let route: GatewayRouteDTO
    let backupID: String?
    let pathDisplay: String
    let state: StateDTO

    @MainActor
    init(_ result: GatewayMigrationResult, model: AppModel) {
        route = GatewayRouteDTO(result.route)
        backupID = result.backupID
        pathDisplay = result.pathDisplay
        state = StateDTO(model)
    }
}

struct ToggleResultDTO: Encodable {
    let serverName: String
    let clientID: String
    let enabled: Bool
    let requiresRestart: Bool
    /// True when switching off had to remove the definition because the client
    /// has no disabled state. The UI says so, because silently deleting a user's
    /// configuration is exactly the surprise §6 exists to prevent.
    let wasParked: Bool
    let backupID: String?
    let pathDisplay: String
    let state: StateDTO

    @MainActor
    init(_ result: ToggleResult, model: AppModel) {
        serverName = result.serverName
        clientID = result.clientID.rawValue
        enabled = result.enabled
        requiresRestart = result.requiresRestart
        wasParked = result.wasParked
        backupID = result.backupID
        pathDisplay = result.pathDisplay
        state = StateDTO(model)
    }
}

struct CatalogEntryDTO: Encodable {
    struct PlaceholderDTO: Encodable {
        let token: String
        let label: String
        let example: String
    }

    struct EnvRequirementDTO: Encodable {
        let key: String
        let required: Bool
        let hint: String
    }

    let id: String
    let name: String
    let displayName: String
    let description: String
    let transport: String
    let command: String
    let args: [String]
    let url: String?
    let placeholders: [PlaceholderDTO]
    let env: [EnvRequirementDTO]
    let requires: String
    let homepage: String

    init(_ entry: CatalogEntry) {
        id = entry.id
        name = entry.name
        displayName = entry.displayName
        description = entry.description
        transport = entry.transport.rawValue
        command = entry.command
        args = entry.args
        url = entry.url
        placeholders = entry.placeholders.map {
            PlaceholderDTO(token: $0.token, label: $0.label, example: $0.example)
        }
        env = entry.env.map {
            EnvRequirementDTO(key: $0.key, required: $0.required, hint: $0.hint)
        }
        requires = entry.requires
        homepage = entry.homepage
    }
}

struct AuthoringResultDTO: Encodable {
    let serverName: String
    let changed: [String]
    let requiresRestart: Bool
    let state: StateDTO

    @MainActor
    init(_ result: AuthoringResult, model: AppModel) {
        serverName = result.serverName
        changed = result.changed.map(\.rawValue)
        requiresRestart = result.requiresRestart
        state = StateDTO(model)
    }
}

struct LibraryCandidateDTO: Encodable {
    let id: String
    let name: String
    /// Relative to the directory the user selected; native absolute paths stay
    /// on the native side unless they are already an intentional display field.
    let location: String
    let commandSummary: String
    let transport: String
    let environmentKeys: [String]
    let detectedClientIDs: [String]
    let missingClientIDs: [String]
    let warnings: [String]

    nonisolated init(_ candidate: MCPLibraryCandidate) {
        id = candidate.id.uuidString
        name = candidate.name
        location = candidate.location
        commandSummary = candidate.commandSummary
        transport = candidate.transport.rawValue
        environmentKeys = candidate.envKeys
        detectedClientIDs = candidate.detectedClientIDs.map(\.rawValue)
        missingClientIDs = candidate.missingClientIDs.map(\.rawValue)
        warnings = candidate.warnings
    }
}

struct LibraryScanDTO: Encodable {
    let rootPathDisplay: String
    let candidates: [LibraryCandidateDTO]
    let warnings: [String]

    nonisolated init(_ result: MCPLibraryScanResult) {
        rootPathDisplay = result.rootPath
        candidates = result.candidates.map(LibraryCandidateDTO.init)
        warnings = result.warnings
    }
}

struct ImportServerPreviewDTO: Encodable {
    let name: String
    let transport: String
    let commandSummary: String
    let environmentKeys: [String]
    let conflictingClientIDs: [String]
    let warnings: [String]

    nonisolated init(_ server: ImportServerPreview) {
        name = server.name
        transport = server.transport.rawValue
        commandSummary = server.commandSummary
        environmentKeys = server.environmentKeys
        conflictingClientIDs = server.conflictingClientIDs.map(\.rawValue)
        warnings = server.warnings
    }
}

struct ImportPreviewDTO: Encodable {
    let source: String
    let targetClientIDs: [String]
    let servers: [ImportServerPreviewDTO]
    let warnings: [String]
    let canImport: Bool

    nonisolated init(_ preview: ImportPreview) {
        source = preview.source
        targetClientIDs = preview.targetClientIDs.map(\.rawValue)
        servers = preview.servers.map(ImportServerPreviewDTO.init)
        warnings = preview.warnings
        canImport = preview.canImport
    }
}

struct ImportPromptDTO: Encodable {
    let prompt: String
}

struct BatchImportResultDTO: Encodable {
    let serverNames: [String]
    let changed: [String]
    let requiresRestart: Bool
    let state: StateDTO

    @MainActor
    init(_ results: [AuthoringResult], model: AppModel) {
        serverNames = results.map(\.serverName)
        changed = Array(Set(results.flatMap(\.changed).map(\.rawValue))).sorted()
        requiresRestart = results.contains { $0.requiresRestart }
        state = StateDTO(model)
    }
}

struct SkillInventoryEntryDTO: Encodable {
    let id: String
    let name: String
    let description: String?
    let agent: String
    let scope: String
    let scopeLabel: String
    let pathDisplay: String
    let metadataStatus: String
    let isDuplicate: Bool
    let hasConflict: Bool
    let duplicateGroup: String

    nonisolated init(_ entry: SkillInventoryEntry) {
        id = entry.id
        name = entry.name
        description = entry.description
        agent = entry.agent
        scope = entry.scope.rawValue
        scopeLabel = entry.scopeLabel
        pathDisplay = entry.pathDisplay
        metadataStatus = entry.metadataStatus.rawValue
        isDuplicate = entry.isDuplicate
        hasConflict = entry.hasConflict
        duplicateGroup = entry.duplicateGroup
    }
}

struct SkillsInventoryDTO: Encodable {
    let entries: [SkillInventoryEntryDTO]
    let warnings: [String]
    let roots: [String]

    nonisolated init(_ result: SkillsInventoryResult) {
        entries = result.entries.map(SkillInventoryEntryDTO.init)
        warnings = result.warnings
        roots = result.roots
    }
}

/// The settings the web layer actually needs. The rest — login item, menu bar,
/// path overrides — are native concerns and stay native (§3.2).
struct SettingsDTO: Encodable {
    let theme: String
    let tokenWarningThreshold: Int
    let hasCompletedOnboarding: Bool
    let hasConfirmedMatrixWrites: Bool
    let showsCustomSources: Bool
    let backupRetention: Int
    /// CSS pixels; the page applies it as `--sidebar-width` the same way the
    /// shell applies its window metrics.
    let sidebarWidth: Int

    init(_ settings: KyttoSettings) {
        theme = settings.theme.rawValue
        tokenWarningThreshold = settings.tokenWarningThreshold
        hasCompletedOnboarding = settings.hasCompletedOnboarding
        hasConfirmedMatrixWrites = settings.hasConfirmedMatrixWrites
        showsCustomSources = settings.showsCustomSources
        backupRetention = settings.backupRetention
        sidebarWidth = settings.sidebarWidth
    }
}

struct SecretRecordDTO: Encodable {
    struct UsageDTO: Encodable {
        let serverID: String
        let serverName: String
        let clientID: String
        let pathDisplay: String
    }

    struct ExposureDTO: Encodable {
        let kind: String
        let pathDisplay: String
        let detail: String
    }

    let id: String
    let key: String
    /// The only form of the value that crosses this boundary by default (§6).
    let maskedValue: String
    let usages: [UsageDTO]
    let isInSecretStore: Bool
    let isShared: Bool
    let exposure: [ExposureDTO]

    init(_ record: SecretRecord) {
        id = record.id
        key = record.key
        maskedValue = record.maskedValue
        usages = record.usages.map {
            UsageDTO(
                serverID: $0.serverID,
                serverName: $0.serverName,
                clientID: $0.clientID.rawValue,
                pathDisplay: $0.pathDisplay
            )
        }
        isInSecretStore = record.isInSecretStore
        isShared = record.isShared
        exposure = record.exposure.map { item in
            switch item {
            case .readableByOthers(let path, let mode):
                ExposureDTO(
                    kind: "readableByOthers",
                    pathDisplay: path,
                    detail: "Mode \(mode) — other users on this Mac can read it."
                )
            case .insideGitRepository(let path, let repository):
                ExposureDTO(
                    kind: "insideGitRepository",
                    pathDisplay: path,
                    detail: "Inside the Git repository at \(repository), and not ignored."
                )
            }
        }
    }
}

struct RotationResultDTO: Encodable {
    let key: String
    let updatedCount: Int
    let clients: [String]
    let state: StateDTO

    @MainActor
    init(_ result: SecretsService.RotationResult, model: AppModel) {
        key = result.key
        updatedCount = result.updated.count
        clients = Array(Set(result.updated.map(\.clientID.rawValue))).sorted()
        state = StateDTO(model)
    }
}

struct BackupDTO: Encodable {
    let id: String
    let clientID: String
    let pathDisplay: String
    /// Seconds since the epoch; the UI formats it in the user's locale.
    let takenAt: Double
    let byteCount: Int

    init(_ backup: Backup) {
        id = backup.id
        clientID = backup.clientID.rawValue
        pathDisplay = backup.originalPathDisplay
        takenAt = backup.takenAt.timeIntervalSince1970
        byteCount = backup.byteCount
    }
}

struct DiagnosticDTO: Encodable {
    let severity: String
    let clientID: String?
    let pathDisplay: String
    let message: String

    init(_ diagnostic: Diagnostic) {
        severity = diagnostic.severity.rawValue
        clientID = diagnostic.clientID?.rawValue
        pathDisplay = diagnostic.pathDisplay
        message = diagnostic.message
    }
}

struct ProfileDTO: Encodable {
    let id: String
    let name: String
    let serverIDs: [String]
    let tokenBudget: Int?
    let analysis: ContextProfileAnalysis

    init(_ profile: ServerProfile, servers: [Server]) {
        id = profile.id
        name = profile.name
        serverIDs = profile.serverIDs
        tokenBudget = profile.tokenBudget
        analysis = ContextOptimizer.analyze(profile: profile, servers: servers)
    }
}

struct DoctorFindingDTO: Encodable {
    let code: String
    let severity: String
    let title: String
    let detail: String
    let remediation: String
    let action: String?

    nonisolated init(_ finding: DoctorFinding) {
        code = finding.code
        severity = finding.severity.rawValue
        title = finding.title
        detail = finding.detail
        remediation = finding.remediation
        action = finding.action?.rawValue
    }
}

struct DoctorServerReportDTO: Encodable {
    let serverID: String
    let serverName: String
    let findings: [DoctorFindingDTO]

    nonisolated init(_ report: DoctorServerReport) {
        serverID = report.serverID
        serverName = report.serverName
        findings = report.findings.map(DoctorFindingDTO.init)
    }
}

struct ContractChangeDTO: Encodable {
    let kind: String
    let severity: String
    let toolName: String
    let summary: String

    nonisolated init(_ change: ContractChange) {
        kind = change.kind.rawValue
        severity = change.severity.rawValue
        toolName = change.toolName
        summary = change.summary
    }
}

struct ContractAlertDTO: Encodable {
    let serverID: String
    let serverName: String
    let changedAt: Double
    let changes: [ContractChangeDTO]

    nonisolated init(_ alert: ContractAlert) {
        serverID = alert.serverID
        serverName = alert.serverName
        changedAt = alert.changedAt.timeIntervalSince1970
        changes = alert.changes.map(ContractChangeDTO.init)
    }
}

struct UnifyPreviewDTO: Encodable {
    let serverID: String
    let serverName: String
    let sourceClientID: String
    let targetClientIDs: [String]
    let skippedClientIDs: [String]
    let commandSummary: String
    let url: String?
    let transport: String
    /// Key names only — the whole point of composing the definition natively is
    /// that no value has to make this trip (§6).
    let environmentKeys: [String]

    init(_ preview: UnifyPreview) {
        serverID = preview.serverID
        serverName = preview.serverName
        sourceClientID = preview.sourceClientID.rawValue
        targetClientIDs = preview.targetClientIDs.map(\.rawValue)
        skippedClientIDs = preview.skippedClientIDs.map(\.rawValue)
        commandSummary = preview.commandSummary
        url = preview.url
        transport = preview.transport.rawValue
        environmentKeys = preview.environmentKeys
    }
}

/// Unifying can also update a copy that is not in any file — a server switched
/// off in a presence-only client, whose bytes Kytto is holding. That is a real
/// outcome and a different one from a config write, so it is reported separately
/// rather than folded into `changed`.
struct UnifyResultDTO: Encodable {
    let serverName: String
    let changed: [String]
    let parkedUpdated: [String]
    let parkedFailures: [String]
    let requiresRestart: Bool
    let state: StateDTO

    @MainActor
    init(_ result: AuthoringResult, model: AppModel) {
        serverName = result.serverName
        changed = result.changed.map(\.rawValue)
        parkedUpdated = result.parkedUpdated.map(\.rawValue)
        parkedFailures = result.parkedFailures.map(\.rawValue)
        requiresRestart = result.requiresRestart
        state = StateDTO(model)
    }
}

struct DoctorFixPreviewDTO: Encodable {
    let serverID: String
    let serverName: String
    let currentCommand: String
    let replacementCommand: String
    let clientIDs: [String]

    init(_ preview: DoctorFixPreview) {
        serverID = preview.serverID
        serverName = preview.serverName
        currentCommand = preview.currentCommand
        replacementCommand = preview.replacementCommand
        clientIDs = preview.clientIDs.map(\.rawValue)
    }
}

struct GatewayEventDTO: Encodable {
    let eventID: String
    let sessionID: String
    let timestamp: Double
    let kind: String
    let serverID: String
    let clientID: String
    let toolName: String?
    let durationMilliseconds: Int?
    let succeeded: Bool?
    let errorCode: Int?
    let exitCode: Int32?

    nonisolated init(_ event: GatewayEvent) {
        eventID = event.eventID
        sessionID = event.sessionID
        timestamp = event.timestamp.timeIntervalSince1970
        kind = event.kind.rawValue
        serverID = event.serverID
        clientID = event.clientID
        toolName = event.toolName
        durationMilliseconds = event.durationMilliseconds
        succeeded = event.succeeded
        errorCode = event.errorCode
        exitCode = event.exitCode
    }
}

struct GatewayActivityDTO: Encodable {
    let events: [GatewayEventDTO]
    let totalSessions: Int
    let completedCalls: Int
    let failedCalls: Int
    let averageDurationMilliseconds: Int?

    nonisolated init(_ summary: GatewayActivitySummary) {
        events = summary.events.map(GatewayEventDTO.init)
        totalSessions = summary.totalSessions
        completedCalls = summary.completedCalls
        failedCalls = summary.failedCalls
        averageDurationMilliseconds = summary.averageDurationMilliseconds
    }
}

struct ProfileApplyFailureDTO: Encodable {
    let serverName: String
    let message: String
}

struct ProfileApplyResultDTO: Encodable {
    let profileName: String
    let clientID: String
    let enabledCount: Int
    let disabledCount: Int
    let failures: [ProfileApplyFailureDTO]
    let state: StateDTO

    @MainActor
    init(_ result: ProfileApplyResult, model: AppModel) {
        profileName = result.profileName
        clientID = result.clientID.rawValue
        enabledCount = result.enabledCount
        disabledCount = result.disabledCount
        failures = result.failures.map {
            ProfileApplyFailureDTO(serverName: $0.serverName, message: $0.message)
        }
        state = StateDTO(model)
    }
}
