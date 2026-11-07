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
            // A path is typed, not clicked: every save of one re-reads every
            // config, and a pause mid-path (`/Us`) is not a choice. Paths wait
            // longer than toggles and steppers; closing the window still
            // flushes at once.
            let pathsEdited = settings.clientPathOverrides != model.settings.clientPathOverrides
                || settings.customConfigSources != model.settings.customConfigSources
            do {
                try await Task.sleep(for: .milliseconds(pathsEdited ? 1500 : 350))
            } catch {
                return
            }
            flush()
        }
        // Closing the window cancels the debounce above; without this the last
        // change made just before closing would look applied and be lost.
        .onDisappear { flush() }
        // A real binding, so however the alert is dismissed the error clears;
        // a constant one let it reappear on the next render.
        .alert("Could not save", isPresented: Binding(get: { error != nil }, set: { if !$0 { error = nil } })) {
            Button("OK") { error = nil }
            Button("Report a bug…") {
                error = nil
                NSWorkspace.shared.open(SupportLinks.bugReport)
            }
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
                    ForEach(model.menuBarCandidates, id: \.id) { client in
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

            Section {
                Link(destination: SupportLinks.bugReport) {
                    HStack {
                        Label("Report a bug…", systemImage: "ladybug")
                        Spacer()
                        Text("Private · No account")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
                Link(destination: SupportLinks.githubIssue) {
                    HStack {
                        Label("Report on GitHub…", systemImage: "arrow.up.right.square")
                        Spacer()
                        Text("Public · GitHub account")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("Support")
            } footer: {
                Text("Opens in your browser. No logs or configuration are attached automatically.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
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
                    Text("Kytto has \(Self.builtInClientCount) built-in local clients: \(Self.builtInClientNames).")
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

                        if !Self.isSavable(source) {
                            Text("A source needs a name. Until it has one, Kytto keeps the last saved name.")
                                .font(.caption)
                                .foregroundStyle(.red)
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

    /// A saved choice that is no longer a candidate reads as automatic, because
    /// that is what the menu bar item is actually doing (`AppModel.menuBarClient`).
    private var menuBarClientBinding: Binding<ClientID?> {
        Binding(
            get: {
                settings.menuBarClient.flatMap { chosen in
                    model.menuBarCandidates.contains { $0.id == chosen } ? chosen : nil
                }
            },
            set: { settings.menuBarClient = $0 }
        )
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

    /// Saves whatever in the draft is valid now. A field that is still invalid —
    /// a half-typed path, a source whose name was cleared — keeps its last saved
    /// value instead of holding every other change in the window hostage, and
    /// keeps showing its error until it is fixed.
    private func flush() {
        guard settings != model.settings else { return }
        let saved = model.settings
        var savable = settings
        savable.clientPathOverrides = settings.clientPathOverrides.filter { Self.isSavablePath($0.value) }
        for (key, value) in saved.clientPathOverrides
        where settings.clientPathOverrides[key].map(Self.isSavablePath) == false {
            savable.clientPathOverrides[key] = value
        }
        savable.customConfigSources = settings.customConfigSources.compactMap { source in
            Self.isSavable(source) ? source : saved.customConfigSources.first { $0.id == source.id }
        }
        // Only a fully valid draft is reflected back; otherwise the field the
        // user is still typing in would be replaced under their cursor.
        save(savable, reflectingBack: savable == settings)
    }

    private func save(_ new: KyttoSettings, reflectingBack: Bool) {
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
            if reflectingBack, saved != new { settings = saved }
        } catch {
            self.error = error.localizedDescription
        }
    }

    private var hasInvalidPathOverride: Bool {
        settings.clientPathOverrides.values.contains { value in
            value != "~" && !Self.isSavablePath(value)
        }
    }

    /// Read from the registry so the sentence cannot fall behind it again.
    private static var builtInClientCount: String {
        let count = ClientRegistry.all.count
        let words = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"]
        return count < words.count ? words[count] : String(count)
    }

    private static var builtInClientNames: String {
        let names = ClientRegistry.all.map(\.displayName)
        guard names.count > 1 else { return names.joined() }
        return names.dropLast().joined(separator: ", ") + " and " + names[names.count - 1]
    }

    nonisolated private static func isSavablePath(_ value: String) -> Bool {
        value.hasPrefix("/") || value.hasPrefix("~/")
    }

    nonisolated private static func isSavable(_ source: CustomConfigSource) -> Bool {
        isSavablePath(source.path) && !source.displayName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
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
