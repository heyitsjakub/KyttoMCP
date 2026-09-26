import AppKit
import KyttoCore

/// The menu bar item (§7.7).
///
/// Native, and specifically **not** a hidden webview window — §3 calls that out
/// by name. It is an `NSStatusItem` with a real `NSMenu`, so it opens instantly,
/// behaves under keyboard control, and costs nothing while it sits there.
///
/// This is the retention feature: it keeps Kytto present without the window
/// open, which is the difference between a tool someone uses and a tool someone
/// installs and forgets.
@MainActor
final class MenuBarController {
    private let model: AppModel
    private var statusItem: NSStatusItem?
    private let openWindow: @MainActor () -> Void

    init(model: AppModel, openWindow: @escaping @MainActor () -> Void) {
        self.model = model
        self.openWindow = openWindow
    }

    func apply(settings: KyttoSettings) {
        if settings.menuBarEnabled {
            install()
            refresh()
        } else {
            remove()
        }
    }

    private func install() {
        guard statusItem == nil else { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = Self.icon(failing: 0)
        item.menu = NSMenu()
        statusItem = item
    }

    /// The app's own mark, as a template image: alpha only, no colour of its
    /// own. That is what lets macOS invert it for a light menu bar, dim it when
    /// the app is inactive and highlight it when the menu is open — a
    /// full-colour icon up there would do none of those and would read as a
    /// sticker rather than as part of the system.
    ///
    /// Failure keeps the system symbol on purpose: it is an alert, and the one
    /// moment the menu bar should stop looking like Kytto sitting quietly is
    /// the moment a server is down (§7.7).
    private static func icon(failing: Int) -> NSImage? {
        guard failing == 0 else {
            let symbol = NSImage(
                systemSymbolName: "exclamationmark.square",
                accessibilityDescription: "Kytto — \(failing) servers failing"
            )
            symbol?.isTemplate = true
            return symbol
        }

        let image = NSImage(named: "MenuBarIcon")
        image?.isTemplate = true
        image?.accessibilityDescription = "Kytto"
        return image
    }

    private func remove() {
        guard let item = statusItem else { return }
        NSStatusBar.system.removeStatusItem(item)
        statusItem = nil
    }

    /// Rebuilds the menu from current state.
    func refresh() {
        guard let statusItem, let menu = statusItem.menu else { return }
        menu.removeAllItems()

        let result = model.current()
        let clientID = model.menuBarClient
        let client = result.clients.first { $0.id == clientID }

        // Aggregate health at a glance, without opening anything (§7.7).
        let failing = result.servers.filter { $0.health?.status == .failed }.count
        statusItem.button?.image = Self.icon(failing: failing)

        menu.addItem(header(summary(result: result, failing: failing)))

        if let client {
            menu.addItem(header("Toggling in \(client.displayName)"))
            menu.addItem(.separator())
            addServerItems(to: menu, result: result, clientID: client.id)
            addProfileItems(to: menu, result: result, clientID: client.id)
        } else {
            menu.addItem(header("No client detected"))
        }

        menu.addItem(.separator())

        let open = NSMenuItem(title: "Open Kytto", action: #selector(openMain), keyEquivalent: "")
        open.target = self
        menu.addItem(open)

        let check = NSMenuItem(title: "Check All Servers", action: #selector(checkAll), keyEquivalent: "")
        check.target = self
        menu.addItem(check)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit Kytto", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
    }

    private func addServerItems(to menu: NSMenu, result: DiscoveryResult, clientID: ClientID) {
        let servers = result.servers.sorted {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
        guard !servers.isEmpty else {
            menu.addItem(header("No servers configured"))
            return
        }

        for server in servers {
            let state = server.enabledIn[clientID] ?? .absent
            let item = NSMenuItem(
                title: server.name,
                action: #selector(toggleServer(_:)),
                keyEquivalent: ""
            )
            item.target = self
            item.representedObject = server.id
            item.state = state == .enabled ? .on : .off
            // A server this client does not have is shown, but dimmed: it is
            // information, and switching it on here would be a bigger decision
            // than a menu bar click should carry.
            item.isEnabled = state != .absent
            if state == .absent {
                item.title = "\(server.name) — not in this client"
            }
            if server.health?.status == .failed {
                item.image = NSImage(systemSymbolName: "circle.fill", accessibilityDescription: "failing")
                item.image?.isTemplate = false
            }
            menu.addItem(item)
        }
    }

    /// Whole stacks, not just one server at a time (§7.9).
    ///
    /// One server per click is the right granularity for a fix; changing what
    /// you are working on is a change of five servers at once, and going to the
    /// window to do it is exactly the friction that leaves the menu bar item
    /// unused. A submenu keeps it out of the way of the server list, which is
    /// still what the menu is mostly for.
    private func addProfileItems(to menu: NSMenu, result: DiscoveryResult, clientID: ClientID) {
        let profiles = model.profiles()
        guard !profiles.isEmpty else { return }

        menu.addItem(.separator())
        let item = NSMenuItem(title: "Apply Profile", action: nil, keyEquivalent: "")
        let submenu = NSMenu()

        for profile in profiles {
            let entry = NSMenuItem(
                title: profile.name,
                action: #selector(applyProfile(_:)),
                keyEquivalent: ""
            )
            entry.target = self
            entry.representedObject = profile.id
            // A tick on the profile this client already matches, so the menu
            // answers "what am I running" and not only "what could I run".
            entry.state = matches(profile: profile, result: result, clientID: clientID) ? .on : .off
            submenu.addItem(entry)
        }

        item.submenu = submenu
        menu.addItem(item)
    }

    /// Whether this client's enabled set is exactly the profile's membership —
    /// the same equality applying the profile would produce.
    private func matches(profile: ServerProfile, result: DiscoveryResult, clientID: ClientID) -> Bool {
        let enabled = Set(
            result.servers.filter { $0.enabledIn[clientID] == .enabled }.map(\.id)
        )
        return enabled == Set(profile.serverIDs)
    }

    private func summary(result: DiscoveryResult, failing: Int) -> String {
        let enabled = result.servers.filter { $0.enabledIn.values.contains(.enabled) }.count
        var text = "\(enabled) of \(result.servers.count) servers active"
        if failing > 0 { text += " · \(failing) failing" }
        return text
    }

    private func header(_ text: String) -> NSMenuItem {
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.isEnabled = false
        return item
    }

    // MARK: - Actions

    @objc private func openMain() {
        NSApp.activate(ignoringOtherApps: true)
        openWindow()
    }

    @objc private func checkAll() {
        Task {
            do {
                try await model.checkAllHealth { _, _, _ in }
            } catch {
                let alert = NSAlert()
                alert.messageText = "Could not save every health result"
                alert.informativeText = error.localizedDescription
                alert.alertStyle = .warning
                alert.runModal()
            }
        }
    }

    /// Applying a profile switches several servers off as well as on, so §7.9's
    /// preview is not optional just because the trigger is a menu item. There is
    /// no sheet up here, so it is an alert — and it says the same three things
    /// the window's preview does before anything is written.
    @objc private func applyProfile(_ sender: NSMenuItem) {
        guard let profileID = sender.representedObject as? String,
              let clientID = model.menuBarClient
        else { return }

        do {
            let plan = try model.profileApplyPlan(id: profileID, to: clientID)
            guard !plan.changesNothing else {
                let alert = NSAlert()
                alert.messageText = "“\(plan.profileName)” is already applied"
                alert.informativeText = "\(plan.clientName) already runs exactly these servers. Nothing would change."
                alert.runModal()
                return
            }

            let alert = NSAlert()
            alert.messageText = "Apply “\(plan.profileName)” to \(plan.clientName)?"
            alert.informativeText = describe(plan)
            alert.addButton(withTitle: "Apply")
            alert.addButton(withTitle: "Cancel")
            guard alert.runModal() == .alertFirstButtonReturn else { return }

            let result = try model.applyProfile(id: profileID, to: clientID)
            guard !result.failures.isEmpty else { return }

            // Never claim the client matches the profile when it does not (§7.9).
            let failed = NSAlert()
            failed.messageText = "“\(result.profileName)” was applied with problems"
            failed.informativeText = ([
                "\(result.enabledCount) switched on, \(result.disabledCount) switched off.",
            ] + result.failures.map { "\($0.serverName): \($0.message)" }).joined(separator: "\n")
            failed.alertStyle = .warning
            failed.runModal()
        } catch {
            let alert = NSAlert()
            alert.messageText = "Could not apply that profile"
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
            alert.runModal()
        }
    }

    private func describe(_ plan: AppModel.ProfileApplyPlan) -> String {
        var lines: [String] = []
        if !plan.toEnable.isEmpty {
            lines.append("Switch on: \(plan.toEnable.joined(separator: ", "))")
        }
        if !plan.toDisable.isEmpty {
            lines.append("Switch off: \(plan.toDisable.joined(separator: ", "))")
        }
        if plan.unchanged > 0 {
            lines.append("Already on: \(plan.unchanged)")
        }
        if !plan.missing.isEmpty {
            lines.append("No longer configured anywhere: \(plan.missing.joined(separator: ", "))")
        }

        // "About 0 tokens" is a worse answer than admitting there is no answer
        // yet, which is what a profile whose members have never been checked
        // actually has (§7.4).
        if plan.estimatedTokens == 0, plan.unmeasured > 0 {
            lines.append("Context cost afterwards: not known — none of these servers has been measured yet.")
        } else {
            var cost = "Context cost afterwards: \(plan.estimatedTokens.formatted()) tokens"
            if plan.unmeasured > 0 {
                cost += " — \(plan.unmeasured) member\(plan.unmeasured == 1 ? " has" : "s have") never been measured, so the real figure is higher"
            }
            lines.append(cost)
        }
        lines.append("Every affected configuration is backed up before it is written.")
        return lines.joined(separator: "\n")
    }

    @objc private func toggleServer(_ sender: NSMenuItem) {
        guard let serverID = sender.representedObject as? String,
              let clientID = model.menuBarClient,
              let server = model.current().servers.first(where: { $0.id == serverID })
        else { return }

        let enabled = server.enabledIn[clientID] != .enabled
        do {
            _ = try model.setEnabled(enabled, serverID: serverID, clientID: clientID)
        } catch {
            // A failure here has no window to report into, so it gets a real
            // alert rather than disappearing.
            let alert = NSAlert()
            alert.messageText = "Could not change \(server.name)"
            alert.informativeText = error.localizedDescription
            alert.alertStyle = .warning
            alert.runModal()
        }
    }
}
