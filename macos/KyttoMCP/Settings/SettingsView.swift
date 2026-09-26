import KyttoCore
import SwiftUI
import UniformTypeIdentifiers

/// The Settings window (§7.6).
///
/// Native rather than another web screen: `Cmd+,` opening something that is not
/// a real macOS settings window is one of the clearest tells that an app is a
/// wrapper, and §3 lists this among the pieces that must not be (§3).
struct SettingsView: View {
    let model: AppModel
    @State private var settings: KyttoSettings
    @State private var error: String?

    init(model: AppModel) {
        self.model = model
        _settings = State(initialValue: model.settings)
    }

    var body: some View {
        TabView {
            general.tabItem { Label("General", systemImage: "gearshape") }
            clients.tabItem { Label("Clients", systemImage: "square.grid.2x2") }
            safety.tabItem { Label("Safety", systemImage: "clock.arrow.circlepath") }
        }
        .frame(width: 520)
        // SwiftUI may keep the Settings scene alive before onboarding changes
        // the model. Refresh the draft whenever the window is presented so an
        // unrelated edit cannot restore that stale first-launch flag.
        .onAppear { settings = model.settings }
        // Settings can change on every keystroke in a path field or every click
        // of a Stepper. Coalesce the edits so one deliberate choice causes one
        // atomic settings write and, for path changes, one discovery pass.
        .task(id: settings) {
            guard settings != model.settings else { return }
            do {
                try await Task.sleep(for: .milliseconds(350))
            } catch {
                return
            }
            guard settings.clientPathOverrides.values.allSatisfy(Self.isSavablePath),
                  settings.customConfigSources.allSatisfy({
                      Self.isSavablePath($0.path) && !$0.displayName.trimmingCharacters(in: .whitespaces).isEmpty
                  })
            else { return }
            save(settings)
        }
        .alert("Could not save", isPresented: .constant(error != nil)) {
            Button("OK") { error = nil }
        } message: {
            Text(error ?? "")
        }
    }

    // MARK: - General

    private var general: some View {
        Form {
            Picker("Appearance", selection: $settings.theme) {
                Text("System").tag(KyttoSettings.Theme.system)
                Text("Light").tag(KyttoSettings.Theme.light)
                Text("Dark").tag(KyttoSettings.Theme.dark)
            }
            .pickerStyle(.segmented)

            Section {
                Toggle("Show Kytto in the menu bar", isOn: $settings.menuBarEnabled)
                Picker("Menu bar toggles servers in", selection: menuBarClientBinding) {
                    Text("Whichever has the most").tag(nil as ClientID?)
                    ForEach(model.current().clients.filter(\.isInstalled), id: \.id) { client in
                        Text(client.displayName).tag(client.id as ClientID?)
                    }
                }
                .disabled(!settings.menuBarEnabled)
            } footer: {
                Text("The menu bar item lists your servers and switches them on and off in one client without opening the window.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Toggle("Open Kytto at login", isOn: $settings.launchAtLogin)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - Clients

    private var clients: some View {
        Form {
            Section {
                ForEach(model.current().clients.filter { !$0.isReadOnly }, id: \.id) { client in
                    LabeledContent(client.displayName) {
                        VStack(alignment: .leading, spacing: 4) {
                            TextField(
                                client.configPathDisplay,
                                text: pathBinding(for: client.id),
                                prompt: Text(client.configPathDisplay)
                            )
                            .textFieldStyle(.roundedBorder)
                            .font(.system(.body, design: .monospaced))
                            if !client.isInstalled {
                                Text("Not installed")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            } header: {
                Text("Configuration files")
            } footer: {
                // §4: these paths move between client releases, which is exactly
                // when an override stops being a nicety.
                VStack(alignment: .leading, spacing: 4) {
                    Text("Kytto has five built-in local clients: Claude Desktop, Claude Code, Cursor, VS Code and Codex.")
                        .foregroundStyle(.secondary)
                    Text("Leave blank to use the standard location. Set a path if a client has moved its configuration and Kytto is looking in the wrong place.")
                        .foregroundStyle(.secondary)
                    if hasInvalidPathOverride {
                        Text("Paths must be absolute, beginning with / or ~/.")
                            .foregroundStyle(.red)
                    }
                }
                .font(.caption)
            }

            Section {
                ForEach($settings.customConfigSources) { $source in
                    VStack(alignment: .leading, spacing: 8) {
                        HStack {
                            TextField("Source name", text: $source.displayName)
                            Button(role: .destructive) {
                                settings.customConfigSources.removeAll { $0.id == source.id }
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                            .help("Remove this source from Kytto. The configuration file is not deleted.")
                        }

                        Text(source.path)
                            .font(.system(.caption, design: .monospaced))
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)

                        Picker("Available in", selection: $source.scope) {
                            ForEach(ConfigurationScope.allCases, id: \.self) { scope in
                                Text(scope.displayName).tag(scope)
                            }
                        }

                        if source.scope != .global {
                            TextField(
                                source.scope == .workspace ? "Workspace label" : "Profile label",
                                text: $source.scopeLabel
                            )
                        }
                    }
                    .padding(.vertical, 4)
                }

                Button("Add Custom Source…") { chooseCustomSource() }
            } header: {
                Text("Custom configuration sources")
            } footer: {
                Text("Choose a JSON, JSONC or TOML file. Kytto detects known MCP server maps automatically and reads this source only—it never edits it. Workspace and profile paths are selected explicitly; Kytto does not scan your projects.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - Safety

    private var safety: some View {
        Form {
            Section {
                Stepper(
                    "Keep \(settings.backupRetention) backups per client",
                    value: $settings.backupRetention,
                    in: 1...200,
                    step: 5
                )
                Button("Show Backups in Finder") {
                    NSWorkspace.shared.activateFileViewerSelecting([model.backupsDirectory])
                }
            } header: {
                Text("Backups")
            } footer: {
                Text("Kytto copies a configuration file aside before every change it makes.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }

            Section {
                Stepper(
                    "Warn above \(settings.tokenWarningThreshold.formatted()) tokens",
                    value: $settings.tokenWarningThreshold,
                    in: 1_000...200_000,
                    step: 1_000
                )
            } header: {
                Text("Context")
            } footer: {
                Text("Servers whose tool definitions cost more than this are highlighted in the matrix. Counts are estimates.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    // MARK: - Bindings

    private var menuBarClientBinding: Binding<ClientID?> {
        Binding(get: { settings.menuBarClient }, set: { settings.menuBarClient = $0 })
    }

    private func pathBinding(for client: ClientID) -> Binding<String> {
        Binding(
            get: { settings.clientPathOverrides[client.rawValue] ?? "" },
            set: { value in
                let trimmed = value.trimmingCharacters(in: .whitespaces)
                if trimmed.isEmpty {
                    settings.clientPathOverrides.removeValue(forKey: client.rawValue)
                } else {
                    settings.clientPathOverrides[client.rawValue] = trimmed
                }
            }
        )
    }

    private func save(_ new: KyttoSettings) {
        do {
            // These belong to web-layer flows — onboarding, the write
            // confirmation, the custom-sources fold, the sidebar width — not to
            // a control in this window. Preserve their freshest model values if
            // both windows happen to be open at once.
            var merged = new
            merged.hasCompletedOnboarding = model.settings.hasCompletedOnboarding
            merged.hasConfirmedMatrixWrites = model.settings.hasConfirmedMatrixWrites
            merged.showsCustomSources = model.settings.showsCustomSources
            merged.sidebarWidth = model.settings.sidebarWidth
            let saved = try model.updateSettings { $0 = merged }
            // The store sanitizes, so reflect back what was actually kept rather
            // than leaving the field showing something that was rejected.
            if saved != new { settings = saved }
        } catch {
            self.error = error.localizedDescription
        }
    }

    private var hasInvalidPathOverride: Bool {
        settings.clientPathOverrides.values.contains { value in
            value != "~" && !Self.isSavablePath(value)
        }
    }

    nonisolated private static func isSavablePath(_ value: String) -> Bool {
        value.hasPrefix("/") || value.hasPrefix("~/")
    }

    private func chooseCustomSource() {
        let panel = NSOpenPanel()
        panel.title = "Choose an MCP configuration"
        panel.prompt = "Add Read-Only Source"
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [
            UTType.json,
            UTType(filenameExtension: "jsonc") ?? .data,
            UTType(filenameExtension: "toml") ?? .data,
        ]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let suggestedName = url.deletingPathExtension().lastPathComponent
        settings.customConfigSources.append(
            CustomConfigSource(displayName: suggestedName, path: url.path)
        )
    }
}
