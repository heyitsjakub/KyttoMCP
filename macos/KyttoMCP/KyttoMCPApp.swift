//
//  KyttoMCPApp.swift
//  KyttoMCP
//

import AppKit
import SwiftUI

@main
struct KyttoMCPApp: App {
    @State private var router: CommandRouter
    @State private var model: AppModel
    @State private var menuBar: MenuBarController

    @Environment(\.openWindow) private var openWindow

    init() {
        // Registered before the web view is built, because the page starts
        // calling commands as soon as it loads. Doing this in a `.task` would be
        // a race the UI would lose intermittently.
        let router = CommandRouter()
        let model = AppModel()
        // Earlier versions stored backups world-readable. Tightened before the
        // page can send its first command, so it never runs beside a write.
        model.tightenStoragePermissions()
        CommandRegistry.registerAll(on: router, model: model)
        // A config changing on disk — whoever changed it — pushes fresh state
        // into the UI rather than letting it show something stale (§6.4).
        model.onExternalChange = { [weak router] in
            guard let router else { return }
            Task { await router.emit("configs.changed", payload: StateDTO(model)) }
        }

        let menuBar = MenuBarController(model: model) {
            NSApp.windows.first { $0.identifier?.rawValue.contains("main") == true }?
                .makeKeyAndOrderFront(nil)
        }
        model.onStateChanged = { [weak menuBar] in menuBar?.refresh() }
        model.onSettingsChanged = { [weak menuBar, weak router] settings, stateChanged in
            menuBar?.apply(settings: settings)
            guard let router else { return }
            Task {
                await router.emit("settings.changed", payload: SettingsDTO(settings))
                if stateChanged {
                    await router.emit("configs.changed", payload: StateDTO(model))
                }
            }
        }

        _router = State(initialValue: router)
        _model = State(initialValue: model)
        _menuBar = State(initialValue: menuBar)
    }

    var body: some Scene {
        Window("Kytto", id: "main") {
            WebView(router: router)
                // The matrix needs width before it needs height; below this the
                // client columns stop being readable.
                .frame(minWidth: 860, minHeight: 480)
                // Otherwise SwiftUI parks the whole page below the title bar and
                // the transparent strip shows the window's own background —
                // a second, emptier toolbar above the real one.
                .ignoresSafeArea()
                .task { menuBar.apply(settings: model.settings) }
        }
        // No native toolbar: the title bar is a transparent strip the page draws
        // its own toolbar band under, and ChromeView tells it how tall that strip
        // is. A toolbar style here would size a bar that has nothing in it.
        .defaultSize(width: 1080, height: 700)
        .commands { MenuCommands(router: router) }

        Settings {
            SettingsView(model: model)
        }
    }
}
