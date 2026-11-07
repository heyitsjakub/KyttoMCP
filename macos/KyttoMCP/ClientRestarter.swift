import AppKit
import KyttoCore

/// Quits a client's desktop app and opens it again, so it reads the
/// configuration Kytto just wrote (Restart now, §7.1).
///
/// Only clients whose descriptor says `restartsWithApp` are offered this. The
/// quit is the app's own: `terminate()` asks it the way the Quit menu item
/// would, so an editor with unsaved files gets to ask about them and may say
/// no. Kytto never force-quits — a refusal is reported and the notice stays,
/// with "I restarted" as the way out once the person has dealt with it.
@MainActor
enum ClientRestarter {
    enum Outcome: Equatable {
        /// It was running, quit, and is open again.
        case restarted
        /// It was not running, so the next launch reads the new configuration.
        /// Kytto does not open an app nobody had open.
        case notRunning
    }

    enum RestartError: LocalizedError {
        case notRestartable(String)
        case notInstalled(String)
        case didNotQuit(String)
        case couldNotReopen(String, String)

        var errorDescription: String? {
            switch self {
            case .notRestartable(let name):
                "Kytto cannot restart \(name) for you. Restart it yourself, then choose I restarted."
            case .notInstalled(let name):
                "Kytto could not find the \(name) app. Restart it yourself, then choose I restarted."
            case .didNotQuit(let name):
                "\(name) did not quit. It may be asking about unsaved work — answer it or quit it yourself, then choose I restarted."
            case .couldNotReopen(let name, let reason):
                "\(name) quit but did not open again (\(reason)). Open it yourself; the notice has been cleared because the next launch reads the new configuration."
            }
        }
    }

    /// How long an app gets to finish quitting. Long enough for an Electron app
    /// to flush its state, short enough that a quit it is refusing — a dialog
    /// about unsaved work — reads as an answer rather than a hang.
    static let quitTimeout: Duration = .seconds(20)

    static func canRestart(_ descriptor: ClientDescriptor) -> Bool {
        descriptor.restartsWithApp && applicationURL(for: descriptor) != nil
    }

    static func restart(_ descriptor: ClientDescriptor) async throws -> Outcome {
        guard descriptor.restartsWithApp else {
            throw RestartError.notRestartable(descriptor.displayName)
        }
        guard let url = applicationURL(for: descriptor) else {
            throw RestartError.notInstalled(descriptor.displayName)
        }

        let running = descriptor.bundleIdentifiers.flatMap {
            NSRunningApplication.runningApplications(withBundleIdentifier: $0)
        }
        .filter { !$0.isTerminated }
        guard !running.isEmpty else { return .notRunning }

        for app in running { app.terminate() }
        let deadline = ContinuousClock.now + quitTimeout
        while running.contains(where: { !$0.isTerminated }) {
            guard ContinuousClock.now < deadline else {
                throw RestartError.didNotQuit(descriptor.displayName)
            }
            try await Task.sleep(for: .milliseconds(200))
        }

        let configuration = NSWorkspace.OpenConfiguration()
        // Kytto stays in front: the person is looking at the notice that is
        // about to clear, not at the client.
        configuration.activates = false
        do {
            _ = try await NSWorkspace.shared.openApplication(at: url, configuration: configuration)
        } catch {
            throw RestartError.couldNotReopen(descriptor.displayName, error.localizedDescription)
        }
        return .restarted
    }

    private static func applicationURL(for descriptor: ClientDescriptor) -> URL? {
        descriptor.bundleIdentifiers.lazy
            .compactMap { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) }
            .first
    }
}
