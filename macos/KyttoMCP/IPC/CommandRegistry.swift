import AppKit
import Foundation
import KyttoCore

/// Registers every command the web layer can call.
///
/// Keep this list and `docs/ipc.md` in step — the doc is the contract M7 has to
/// reimplement against Windows APIs.
@MainActor
enum CommandRegistry {

    static func registerAll(on router: CommandRouter, model: AppModel) {
        registerAll(on: router, model: model, updateService: UpdateService())
    }

    static func registerAll(
        on router: CommandRouter,
        model: AppModel,
        updateService: UpdateService
    ) {
        router.register("app.info") { appInfo() }

        // MARK: - Updates

        router.register("updates.check") { (payload: UpdateCheckPayload) -> UpdateCheckDTO in
            try await updateService.check(
                currentVersion: appInfo().version,
                force: payload.force
            )
        }

        router.register("updates.download") { () -> UpdateStageDTO in
            let stage = try await updateService.downloadAndStage(currentVersion: appInfo().version) { progress in
                await router.emit("updates.progress", payload: UpdateProgressDTO(progress))
            }
            return UpdateStageDTO(stage)
        }

        router.register("updates.cancel") { () -> EmptyResponse in
            updateService.cancelDownload()
            return EmptyResponse()
        }

        router.register("updates.install") { (payload: UpdateInstallPayload) -> UpdateInstallDTO in
            let result = try await updateService.installStaged(
                token: payload.token,
                currentVersion: appInfo().version
            ) { progress in
                Task { @MainActor in
                    await router.emit("updates.progress", payload: UpdateProgressDTO(progress))
                }
            }
            return UpdateInstallDTO(result)
        }

        // The web layer never opens a URL from the network response itself.
        // Each native shell owns one fixed, platform-correct download route.
        router.register("updates.openDownload") { () -> EmptyResponse in
            try updateService.openDownload()
            return EmptyResponse()
        }

        router.register("clients.list") {
            let result = model.current()
            let routes = model.routes()
            return result.clients.map { client in
                ClientDTO(client, toolBudget: model.toolBudget(for: client, in: result, routes: routes))
            }
        }

        router.register("servers.list") {
            model.current().servers.map { server in
                ServerDTO(server, provenance: model.provenance(for: server))
            }
        }

        router.register("diagnostics.list") {
            model.current().diagnostics.map(DiagnosticDTO.init)
        }

        router.register("discovery.refresh") { () -> StateDTO in
            model.reload()
            return StateDTO(model)
        }

        router.register("state.get") { StateDTO(model) }

        // MARK: - Gateway (§3.3)

        router.register("gateway.preview") { (payload: GatewayTargetPayload) -> GatewayMigrationPreviewDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            return GatewayMigrationPreviewDTO(
                try model.gatewayPreview(serverID: payload.serverID, clientID: clientID)
            )
        }

        router.register("gateway.enable") { (payload: EnableGatewayPayload) -> GatewayMigrationResultDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            let result = try model.enableGateway(
                serverID: payload.serverID,
                clientID: clientID,
                routeID: payload.routeID
            )
            return GatewayMigrationResultDTO(result, model: model)
        }

        router.register("gateway.restore") { (payload: GatewayRoutePayload) -> GatewayMigrationResultDTO in
            GatewayMigrationResultDTO(try model.restoreDirect(routeID: payload.routeID), model: model)
        }

        router.register("gateway.setExposedTools") { (payload: ExposedToolsPayload) -> StateDTO in
            try model.setExposedTools(routeID: payload.routeID, toolNames: payload.toolNames)
            return StateDTO(model)
        }

        router.register("activity.list") { GatewayActivityDTO(model.activity()) }

        // MARK: - MCP Doctor and Contract Guard

        router.register("doctor.previewFix") { (payload: DoctorFixPayload) -> DoctorFixPreviewDTO in
            DoctorFixPreviewDTO(try model.doctorFixPreview(
                serverID: payload.serverID,
                action: try payload.doctorAction()
            ))
        }

        router.register("doctor.applyFix") { (payload: DoctorFixPayload) -> AuthoringResultDTO in
            AuthoringResultDTO(try model.applyDoctorFix(
                serverID: payload.serverID,
                action: try payload.doctorAction(),
                confirmedVersion: payload.version
            ), model: model)
        }

        router.register("contract.acknowledge") { (payload: DoctorFixPayload) -> StateDTO in
            try model.acknowledgeContract(serverID: payload.serverID)
            return StateDTO(model)
        }

        // MARK: - Drift

        router.register("servers.unifyPreview") { (payload: UnifyPayload) -> UnifyPreviewDTO in
            guard let clientID = ClientID(rawValue: payload.sourceClientID) else {
                throw CommandError.badArgument("Unknown client \(payload.sourceClientID).")
            }
            return UnifyPreviewDTO(
                try model.unifyPreview(serverID: payload.serverID, sourceClientID: clientID)
            )
        }

        router.register("servers.unify") { (payload: UnifyPayload) -> UnifyResultDTO in
            guard let clientID = ClientID(rawValue: payload.sourceClientID) else {
                throw CommandError.badArgument("Unknown client \(payload.sourceClientID).")
            }
            let result = try model.unifyServer(serverID: payload.serverID, sourceClientID: clientID)
            return UnifyResultDTO(result, model: model)
        }

        // MARK: - Profiles

        router.register("profiles.list") {
            let servers = model.current().servers
            return model.profiles().map { ProfileDTO($0, servers: servers) }
        }

        router.register("profiles.create") { (payload: SaveProfilePayload) -> [ProfileDTO] in
            try model.createProfile(name: payload.name, serverIDs: payload.serverIDs, tokenBudget: payload.tokenBudget)
            let servers = model.current().servers
            return model.profiles().map { ProfileDTO($0, servers: servers) }
        }

        router.register("profiles.update") { (payload: UpdateProfilePayload) -> [ProfileDTO] in
            try model.updateProfile(id: payload.profileID, name: payload.name, serverIDs: payload.serverIDs, tokenBudget: payload.tokenBudget)
            let servers = model.current().servers
            return model.profiles().map { ProfileDTO($0, servers: servers) }
        }

        router.register("profiles.delete") { (payload: ProfileIDPayload) -> [ProfileDTO] in
            try model.deleteProfile(id: payload.profileID)
            let servers = model.current().servers
            return model.profiles().map { ProfileDTO($0, servers: servers) }
        }

        router.register("profiles.apply") { (payload: ApplyProfilePayload) -> ProfileApplyResultDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            return ProfileApplyResultDTO(try model.applyProfile(id: payload.profileID, to: clientID), model: model)
        }

        // A platform-neutral intent. The macOS shell owns the pasteboard; a
        // future Windows shell implements the same command with its own API.
        router.register("clipboard.writeText") { (payload: ClipboardTextPayload) -> EmptyResponse in
            NSPasteboard.general.clearContents()
            guard NSPasteboard.general.setString(payload.text, forType: .string) else {
                throw CommandError.badArgument("Could not copy the diagnostic report.")
            }
            return EmptyResponse()
        }

        // MARK: - Writes

        router.register("servers.setEnabled") { (payload: SetEnabledPayload) -> ToggleResultDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            let result = try model.setEnabled(
                payload.enabled,
                serverID: payload.serverID,
                clientID: clientID
            )
            return ToggleResultDTO(result, model: model)
        }

        router.register("restarts.acknowledge") { (payload: AcknowledgePayload) -> StateDTO in
            let clientID: ClientID?
            if let raw = payload.clientID {
                guard let resolved = ClientID(rawValue: raw) else {
                    throw CommandError.badArgument("Unknown client \(raw).")
                }
                clientID = resolved
            } else {
                clientID = nil
            }
            model.acknowledgeRestart(clientID: clientID)
            return StateDTO(model)
        }

        router.register("restarts.dismiss") { (payload: AcknowledgePayload) -> StateDTO in
            let clientID: ClientID?
            if let raw = payload.clientID {
                guard let resolved = ClientID(rawValue: raw) else {
                    throw CommandError.badArgument("Unknown client \(raw).")
                }
                clientID = resolved
            } else {
                clientID = nil
            }
            model.dismissRestart(clientID: clientID)
            return StateDTO(model)
        }

        // MARK: - Authoring (§7.2, §7.5)

        router.register("catalog.list") {
            Catalog.entries.map(CatalogEntryDTO.init)
        }

        router.register("servers.create") { (payload: CreateServerPayload) -> AuthoringResultDTO in
            let clients = try payload.clientIDs.map { raw -> ClientID in
                guard let id = ClientID(rawValue: raw) else {
                    throw CommandError.badArgument("Unknown client \(raw).")
                }
                return id
            }
            guard !clients.isEmpty else {
                throw CommandError.badArgument("Choose at least one client to add this server to.")
            }
            let result = try model.createServer(payload.draft.makeDraft(), in: clients)
            return AuthoringResultDTO(result, model: model)
        }

        router.register("servers.update") { (payload: UpdateServerPayload) -> AuthoringResultDTO in
            let result = try model.updateServer(payload.draft.makeDraft(), serverID: payload.serverID)
            return AuthoringResultDTO(result, model: model)
        }

        router.register("servers.delete") { (payload: DeleteServerPayload) -> AuthoringResultDTO in
            let result = try model.deleteServer(serverID: payload.serverID)
            return AuthoringResultDTO(result, model: model)
        }

        // Distinct from `servers.setEnabled` on purpose: off is a state a client
        // supports, this is the server ceasing to be that client's business.
        router.register("servers.removeFromClient") { (payload: RemoveFromClientPayload) -> AuthoringResultDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            let result = try model.removeServer(serverID: payload.serverID, clientID: clientID)
            return AuthoringResultDTO(result, model: model)
        }

        // MARK: - Local MCP library and strict agent import

        router.register("library.chooseDirectory") { () -> DirectoryChoiceDTO in
            let panel = NSOpenPanel()
            panel.canChooseFiles = false
            panel.canChooseDirectories = true
            panel.allowsMultipleSelection = false
            panel.prompt = "Scan directory"
            panel.message = "Kytto only reads MCP definitions and likely entry points. It never executes files during a scan."
            guard panel.runModal() == .OK, let url = panel.url else {
                return DirectoryChoiceDTO(path: nil)
            }
            return DirectoryChoiceDTO(path: url.standardizedFileURL.path)
        }

        router.register("library.scan") { (payload: LibraryScanPayload) -> LibraryScanDTO in
            LibraryScanDTO(try model.libraryScan(path: payload.path))
        }

        router.register("library.preview") { (payload: LibraryImportPayload) -> ImportPreviewDTO in
            ImportPreviewDTO(try model.previewLibraryCandidate(
                id: payload.candidateID,
                clientIDs: try clientIDs(from: payload.clientIDs)
            ))
        }

        router.register("library.import") { (payload: LibraryImportPayload) -> BatchImportResultDTO in
            let results = try model.importLibraryCandidate(
                id: payload.candidateID,
                clientIDs: try clientIDs(from: payload.clientIDs)
            )
            return BatchImportResultDTO(results, model: model)
        }

        router.register("imports.prompt") { () -> ImportPromptDTO in
            ImportPromptDTO(prompt: model.agentImportPrompt())
        }

        router.register("imports.preview") { (payload: AgentImportPayload) -> ImportPreviewDTO in
            ImportPreviewDTO(try model.previewAgentImport(
                json: payload.json,
                clientIDs: try clientIDs(from: payload.clientIDs)
            ))
        }

        router.register("imports.import") { (payload: AgentImportPayload) -> BatchImportResultDTO in
            let results = try model.importAgentJSON(
                json: payload.json,
                clientIDs: try clientIDs(from: payload.clientIDs)
            )
            return BatchImportResultDTO(results, model: model)
        }

        router.register("skills.chooseDirectory") { () -> DirectoryChoiceDTO in
            let panel = NSOpenPanel()
            panel.canChooseFiles = false
            panel.canChooseDirectories = true
            panel.allowsMultipleSelection = false
            panel.prompt = "Scan workspace"
            panel.message = "Kytto reads only known agent skill directories under this workspace. It never modifies skill files."
            guard panel.runModal() == .OK, let url = panel.url else {
                return DirectoryChoiceDTO(path: nil)
            }
            return DirectoryChoiceDTO(path: url.standardizedFileURL.path)
        }

        router.register("skills.inventory") { (payload: SkillsInventoryPayload) -> SkillsInventoryDTO in
            SkillsInventoryDTO(try model.skillsInventory(workspacePath: payload.workspacePath))
        }

        // MARK: - Settings and onboarding (§7.6)

        router.register("settings.get") { SettingsDTO(model.settings) }

        router.register("onboarding.complete") { () -> SettingsDTO in
            let updated = try model.updateSettings { $0.hasCompletedOnboarding = true }
            return SettingsDTO(updated)
        }

        // The sidebar has a Settings row; what a settings window is belongs to
        // the shell, so the page only ever states the intent (§3.2).
        router.register("settings.open") { () -> EmptyResponse in
            openSettingsWindow()
            return EmptyResponse()
        }

        router.register("settings.confirmMatrixWrites") { () -> SettingsDTO in
            let updated = try model.updateSettings { $0.hasConfirmedMatrixWrites = true }
            return SettingsDTO(updated)
        }

        router.register("settings.setShowsCustomSources") { (payload: ShowsCustomSourcesPayload) -> SettingsDTO in
            let updated = try model.updateSettings { $0.showsCustomSources = payload.shown }
            return SettingsDTO(updated)
        }

        // The drag happens in the page; the width is remembered here, like
        // every other choice that has to survive a relaunch. `sanitized()`
        // clamps it, so a wild value cannot fold the sidebar away entirely.
        router.register("settings.setSidebarWidth") { (payload: SidebarWidthPayload) -> SettingsDTO in
            let updated = try model.updateSettings { $0.sidebarWidth = payload.width }
            return SettingsDTO(updated)
        }

        // MARK: - Secrets (§6)

        router.register("secrets.list") {
            model.secrets().map(SecretRecordDTO.init)
        }

        // The one command that hands a value to the web layer, and only because
        // the user asked for it in front of a confirmation (§6).
        router.register("secrets.reveal") { (payload: SecretIDPayload) -> [String: String] in
            guard let value = model.revealSecret(id: payload.secretID) else {
                throw CommandError.badArgument("That secret is no longer in any configuration.")
            }
            return ["value": value]
        }

        router.register("secrets.rotate") { (payload: RotateSecretPayload) -> RotationResultDTO in
            let result = try model.rotateSecret(
                id: payload.secretID,
                to: payload.newValue,
                storeInSecretStore: payload.storeInSecretStore ?? true
            )
            return RotationResultDTO(result, model: model)
        }

        router.register("secrets.adopt") { (payload: SecretIDPayload) -> [SecretRecordDTO] in
            try model.adoptSecret(id: payload.secretID)
            return model.secrets().map(SecretRecordDTO.init)
        }

        router.register("secrets.forget") { (payload: SecretIDPayload) -> [SecretRecordDTO] in
            try model.forgetSecret(id: payload.secretID)
            return model.secrets().map(SecretRecordDTO.init)
        }

        router.register("secrets.restrictPermissions") { (payload: RestrictPayload) -> [SecretRecordDTO] in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            try model.restrictPermissions(clientID: clientID)
            return model.secrets().map(SecretRecordDTO.init)
        }

        // MARK: - Health (§7.3, §7.4)

        router.register("health.check") { (payload: HealthCheckPayload) -> HealthCheckResultDTO in
            let health = try await model.checkHealth(serverID: payload.serverID)
            return HealthCheckResultDTO(health: HealthDTO(health), state: StateDTO(model))
        }

        router.register("health.checkAll") { () -> StateDTO in
            try await model.checkAllHealth { name, index, total in
                // Progress goes out on the event channel so the UI can name the
                // server it is waiting on — a ten-second stall with no
                // explanation reads as a hang.
                await router.emit("health.progress", payload: HealthProgressDTO(
                    serverName: name, index: index, total: total
                ))
            }
            return StateDTO(model)
        }

        router.register("provenance.checkLatest") { (payload: HealthCheckPayload) -> StateDTO in
            try await model.checkProvenanceLatest(serverID: payload.serverID)
            return StateDTO(model)
        }

        // The web layer names a server; the URL that opens is the one the last
        // health check recorded from that server's own output, validated here.
        // The shell never opens a URL supplied by JavaScript — the same stance
        // as `updates.openDownload`.
        router.register("health.openAuthorization") { (payload: HealthCheckPayload) -> EmptyResponse in
            guard let server = model.current().servers.first(where: { $0.id == payload.serverID }),
                  server.health?.status == .needsAuthorization,
                  let raw = server.health?.authorizationURL,
                  let url = URL(string: raw), url.scheme == "https", url.host() != nil
            else {
                throw CommandError.badArgument(
                    "No authorization page is recorded for this server. Check it again to capture one."
                )
            }
            NSWorkspace.shared.open(url)
            return EmptyResponse()
        }

        // MARK: - Backups, the visible half of the trust story (§6.5)

        router.register("backups.list") { (payload: BackupsListPayload) -> [BackupDTO] in
            let clients: [ClientID]
            if let raw = payload.clientID {
                guard let clientID = ClientID(rawValue: raw) else {
                    throw CommandError.badArgument("Unknown client \(raw).")
                }
                clients = [clientID]
            } else {
                clients = ClientID.allCases
            }
            return clients
                .flatMap { model.backups.list(clientID: $0) }
                .sorted { $0.takenAt > $1.takenAt }
                .map(BackupDTO.init)
        }

        router.register("backups.restore") { (payload: RestoreBackupPayload) -> StateDTO in
            guard let clientID = ClientID(rawValue: payload.clientID) else {
                throw CommandError.badArgument("Unknown client \(payload.clientID).")
            }
            try model.restore(backupID: payload.backupID, clientID: clientID)
            return StateDTO(model)
        }
    }

    private static func appInfo() -> AppInfoDTO {
        let info = Bundle.main.infoDictionary
        return AppInfoDTO(
            version: info?["CFBundleShortVersionString"] as? String ?? "0",
            build: info?["CFBundleVersion"] as? String ?? "0",
            // The UI enables features by asking for a capability, never by
            // guessing from the platform (§3.2). M4 adds `secrets`, M5 `health`.
            capabilities: ["config.read", "config.write", "backups", "authoring", "catalog", "health", "provenance", "secrets", "profiles", "inspector", "clipboard", "gateway.stdio", "gateway.masking", "doctor", "activity", "contract-guard", "context-optimizer", "drift", "updates", "library", "agent-import", "skills"],
            platformDisplayName: "macOS",
            secretStoreDisplayName: "Keychain"
        )
    }

    private static func clientIDs(from rawValues: [String]) throws -> [ClientID] {
        try rawValues.map { raw in
            guard let clientID = ClientID(rawValue: raw) else {
                throw CommandError.badArgument("Unknown client \(raw).")
            }
            return clientID
        }
    }
}

// MARK: - Payloads

struct SetEnabledPayload: Decodable {
    let serverID: String
    let clientID: String
    let enabled: Bool
}

struct UpdateCheckPayload: Decodable {
    let force: Bool
}

struct UpdateInstallPayload: Decodable {
    let token: String
}

struct LibraryScanPayload: Decodable {
    let path: String
}

struct LibraryImportPayload: Decodable {
    let candidateID: String
    let clientIDs: [String]
}

struct AgentImportPayload: Decodable {
    let json: String
    let clientIDs: [String]
}

struct SkillsInventoryPayload: Decodable {
    let workspacePath: String?
}

struct ShowsCustomSourcesPayload: Decodable {
    let shown: Bool
}

struct SidebarWidthPayload: Decodable {
    let width: Int
}

struct DirectoryChoiceDTO: Encodable {
    let path: String?
}

struct GatewayTargetPayload: Decodable {
    let serverID: String
    let clientID: String
}

struct EnableGatewayPayload: Decodable {
    let serverID: String
    let clientID: String
    let routeID: String
}

struct GatewayRoutePayload: Decodable {
    let routeID: String
}

struct ExposedToolsPayload: Decodable {
    let routeID: String
    /// The exact tools to expose, or null to stop masking entirely. An empty
    /// array means "expose nothing" and is a different instruction from null.
    let toolNames: [String]?
}

struct DoctorFixPayload: Decodable {
    let serverID: String
    /// Which repair; absent means the executable pin, the only one there was.
    var action: String?
    /// `pinPackageVersion` only: the release the preview showed. The native
    /// side writes it only if it is still the version it looked up.
    var version: String?

    func doctorAction() throws -> DoctorAction {
        guard let action else { return .pinResolvedCommand }
        guard let known = DoctorAction(rawValue: action), known != .runHealthCheck else {
            throw CommandError.badArgument("Unknown repair \(action).")
        }
        return known
    }
}

/// Which copy of a drifted server wins. The definition itself is not in here —
/// the native side already has it, and sending it round trip would mean putting
/// an environment value on the boundary (§6).
struct UnifyPayload: Decodable {
    let serverID: String
    let sourceClientID: String
}

/// A server as described by the edit form.
struct ServerDraftPayload: Decodable {
    struct EnvPayload: Decodable {
        let key: String
        /// Nil means "keep whatever value is already in the config".
        ///
        /// The form never receives secret values, so it cannot send them back.
        /// `AppModel` fills these in from the existing server before writing.
        let value: String?
    }

    let name: String
    let transport: String
    let command: String?
    let args: [String]?
    let env: [EnvPayload]?
    let url: String?

    func makeDraft() throws -> ServerDraft {
        guard let resolvedTransport = Transport(rawValue: transport) else {
            throw CommandError.badArgument("Unknown transport \(transport).")
        }
        return ServerDraft(
            name: name,
            transport: resolvedTransport,
            command: command ?? "",
            args: args ?? [],
            env: (env ?? []).map { EnvEntry(key: $0.key, value: $0.value) },
            url: url ?? ""
        )
    }
}

struct CreateServerPayload: Decodable {
    let draft: ServerDraftPayload
    let clientIDs: [String]
}

struct UpdateServerPayload: Decodable {
    let serverID: String
    let draft: ServerDraftPayload
}

struct DeleteServerPayload: Decodable {
    let serverID: String
}

struct RemoveFromClientPayload: Decodable {
    let serverID: String
    let clientID: String
}

struct HealthCheckPayload: Decodable {
    let serverID: String
}

struct SaveProfilePayload: Decodable {
    let name: String
    let serverIDs: [String]
    let tokenBudget: Int?
}

struct UpdateProfilePayload: Decodable {
    let profileID: String
    let name: String
    let serverIDs: [String]
    let tokenBudget: Int?
}

struct ProfileIDPayload: Decodable {
    let profileID: String
}

struct ApplyProfilePayload: Decodable {
    let profileID: String
    let clientID: String
}

struct ClipboardTextPayload: Decodable {
    let text: String
}

struct EmptyResponse: Encodable {}

struct SecretIDPayload: Decodable {
    let secretID: String
}

struct RotateSecretPayload: Decodable {
    let secretID: String
    let newValue: String
    let storeInSecretStore: Bool?
}

struct RestrictPayload: Decodable {
    let clientID: String
}

struct HealthCheckResultDTO: Encodable {
    let health: HealthDTO
    let state: StateDTO
}

struct HealthProgressDTO: Encodable {
    let serverName: String
    let index: Int
    let total: Int
}

struct AcknowledgePayload: Decodable {
    /// Nil acknowledges every client at once.
    let clientID: String?
}

struct BackupsListPayload: Decodable {
    let clientID: String?
}

struct RestoreBackupPayload: Decodable {
    let backupID: String
    let clientID: String
}

enum CommandError: Error, LocalizedError {
    case badArgument(String)

    var errorDescription: String? {
        switch self {
        case .badArgument(let message): message
        }
    }
}

/// Opens the `Settings` scene.
///
/// There is no public API for this. SwiftUI installs the opener on the app
/// menu's own Settings item, and `sendAction` reports success from a responder
/// that then does nothing when the web view is first responder — so the menu
/// item is performed directly, which is exactly what ⌘, does. The selector is
/// only the fallback, for a shell whose menu has no such item.
@MainActor
private func openSettingsWindow() {
    NSApp.activate(ignoringOtherApps: true)

    let known: Set<Selector> = [Selector(("showSettingsWindow:")), Selector(("showPreferencesWindow:"))]
    if let menu = NSApp.mainMenu?.item(at: 0)?.submenu,
       let index = menu.items.firstIndex(where: { item in
           if let action = item.action, known.contains(action) { return true }
           // A localized title is the last resort; the selector identifies it.
           return item.title.hasPrefix("Settings") || item.title.hasPrefix("Preferences")
       })
    {
        menu.performActionForItem(at: index)
        return
    }

    for selector in known where NSApp.sendAction(selector, to: nil, from: nil) { return }
}
