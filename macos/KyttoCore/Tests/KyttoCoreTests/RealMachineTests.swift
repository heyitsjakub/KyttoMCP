import Foundation
import Testing
@testable import KyttoCore

/// M1 in README.md §8 says to check discovery against a real machine. This does
/// that on demand:
///
///     KYTTO_REAL_HOME=1 swift test --filter RealMachine
///
/// It is read-only and it never asserts on the contents, because what is on any
/// given machine is not a fact the test suite can own. It prints what Kytto sees
/// so it can be compared against what the client apps actually show.
@Suite(
    "RealMachine",
    .enabled(if: ProcessInfo.processInfo.environment["KYTTO_REAL_HOME"] == "1")
)
struct RealMachineTests {

    @Test("report what discovery finds in the current user's configs")
    func report() throws {
        let home = FileManager.default.homeDirectoryForCurrentUser
        #if canImport(AppKit)
        let locator: any AppLocating = SystemAppLocator()
        #else
        let locator: any AppLocating = StubAppLocator()
        #endif

        let result = Discovery(home: home, locator: locator).run()

        var report = "\n=== Clients ===\n"
        for client in result.clients {
            let state = client.isInstalled ? "installed" : "not installed"
            let config = client.configExists ? "config present" : "no config"
            report += "  \(client.displayName.padding(toLength: 18, withPad: " ", startingAt: 0))"
            report += "\(state), \(config), \(client.serverCount) server(s)\n"
            report += "    \(client.configPathDisplay)\n"
        }

        report += "\n=== Servers ===\n"
        for server in result.servers {
            let cells = ClientID.allCases.map { id -> String in
                switch server.enabledIn[id] ?? .absent {
                case .enabled: return "on "
                case .disabled: return "off"
                case .absent: return " · "
                }
            }.joined(separator: " ")
            report += "  [\(cells)]  \(server.name)\n"
            report += "            \(server.transport.rawValue)  \(server.commandSummary)\n"
        }
        report += "\n  columns: " + ClientID.allCases.map(\.rawValue).joined(separator: " | ") + "\n"

        if result.diagnostics.isEmpty {
            report += "\n=== Diagnostics ===\n  none\n"
        } else {
            report += "\n=== Diagnostics ===\n"
            for diagnostic in result.diagnostics {
                report += "  [\(diagnostic.severity.rawValue)] \(diagnostic.pathDisplay)\n"
                report += "    \(diagnostic.message)\n"
            }
        }

        print(report)
    }
}
