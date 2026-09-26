import SwiftUI

/// The native menu bar. §3 calls this non-negotiable: a full menu bar with the
/// standard items is a large part of why an app reads as native, and the
/// shortcuts below are the ones a developer will try within the first minute.
struct MenuCommands: Commands {
    let router: CommandRouter

    var body: some Commands {
        // Replaces the New Window / New Item group, which this app has no use for.
        CommandGroup(replacing: .newItem) {}

        CommandGroup(after: .newItem) {
            Button("Refresh Configurations") {
                Task { await router.emit("menu.refresh", payload: EmptyEvent()) }
            }
            .keyboardShortcut("r", modifiers: .command)
        }

        CommandGroup(after: .textEditing) {
            Button("Find…") {
                Task { await router.emit("menu.find", payload: EmptyEvent()) }
            }
            .keyboardShortcut("f", modifiers: .command)
        }

        CommandGroup(replacing: .help) {
            Link("Kytto Help", destination: URL(string: "https://kytto.app/help")!)
        }
    }
}

/// Menu events carry no data; the web layer only needs to know they happened.
struct EmptyEvent: Encodable {}
