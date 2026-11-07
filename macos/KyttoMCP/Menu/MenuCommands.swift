import SwiftUI

/// The native menu bar. §3 calls this non-negotiable: a full menu bar with the
/// standard items is a large part of why an app reads as native, and the
/// shortcuts below are the ones a developer will try within the first minute.
struct MenuCommands: Commands {
    let router: CommandRouter
    /// Both commands act on the main window, which may be closed (the menu bar
    /// item keeps Kytto running) or behind Settings. Bringing it forward first
    /// is what makes them do something visible instead of nothing.
    let mainWindow: MainWindowLink

    var body: some Commands {
        // Replaces the New Window / New Item group, which this app has no use for.
        CommandGroup(replacing: .newItem) {}

        CommandGroup(after: .newItem) {
            Button("Refresh Configurations") {
                mainWindow.open()
                Task { await router.emit("menu.refresh", payload: EmptyEvent()) }
            }
            .keyboardShortcut("r", modifiers: .command)
        }

        CommandGroup(after: .textEditing) {
            Button("Find…") {
                mainWindow.open()
                Task { await router.emit("menu.find", payload: EmptyEvent()) }
            }
            .keyboardShortcut("f", modifiers: .command)
        }

        CommandGroup(replacing: .help) {
            // The project README is the user documentation. Only a domain the
            // project controls belongs here; the updater's host is the other one.
            Link("Kytto Help", destination: URL(string: "https://github.com/heyitsjakub/KyttoMCP#readme")!)
            Divider()
            Link("Report a bug…", destination: SupportLinks.bugReport)
            Link("Report on GitHub…", destination: SupportLinks.githubIssue)
        }
    }
}

/// Menu events carry no data; the web layer only needs to know they happened.
struct EmptyEvent: Encodable {}
