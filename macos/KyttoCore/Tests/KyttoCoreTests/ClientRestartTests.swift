import Testing
@testable import KyttoCore

/// Which clients Restart now may offer to quit and reopen (§7.1).
///
/// The line this protects: a restart that clears the notice must actually have
/// reloaded the configuration. A terminal client cannot be restarted from a
/// window, and the Codex and OpenCode apps are side doors to CLI-first clients
/// whose terminal sessions would keep the old configuration.
@Suite("Restart now — which clients restart with their app")
struct ClientRestartTests {
    @Test func desktopAppsRestartAndTerminalClientsDoNot() {
        let restartable = Set(ClientRegistry.all.filter(\.restartsWithApp).map(\.id))
        #expect(restartable == [.claudeDesktop, .cursor, .vsCode, .antigravity, .devinDesktop])
    }

    @Test func everyRestartableClientHasAnAppToReopen() {
        for descriptor in ClientRegistry.all where descriptor.restartsWithApp {
            #expect(!descriptor.bundleIdentifiers.isEmpty, "\(descriptor.displayName)")
        }
    }

    @Test func aLauncherCommandIsNamedInTheCaveat() {
        let antigravity = ClientRegistry.descriptor(for: .antigravity)
        #expect(antigravity.restartCaveat == "Terminal sessions started with `agy` or `antigravity` keep the old configuration until you restart them.")
        #expect(ClientRegistry.descriptor(for: .claudeDesktop).restartCaveat == nil)
        // Not restartable, so nothing to qualify.
        #expect(ClientRegistry.descriptor(for: .codex).restartCaveat == nil)
    }
}
