import Foundation
import Testing
@testable import KyttoCore

@Suite("ToolBudget — tools a client hands its model")
struct ToolBudgetTests {

    private let limit = ClientToolLimit(maxTools: 10, pastLimitSummary: "Past ten, the rest are dropped.")

    @Test("Only servers switched on in the client count")
    func countsEnabledOnly() {
        let servers = [
            budgetServer("on", in: .cursor, .enabled, tools: 4),
            budgetServer("off", in: .cursor, .disabled, tools: 50),
            budgetServer("elsewhere", in: .codex, .enabled, tools: 50),
        ]
        let budget = ToolBudget.evaluate(clientID: .cursor, limit: limit, servers: servers, routes: [])
        #expect(budget.toolCount == 4)
        #expect(budget.unmeasuredServerIDs.isEmpty)
        #expect(!budget.isLowerBound)
        #expect(budget.state == .ok)
        #expect(budget.severity == nil)
    }

    @Test("Near starts at 80 % of the cap, over is strictly past it")
    func thresholds() {
        func state(_ tools: Int) -> ToolLimitState? {
            ToolBudget.evaluate(
                clientID: .vsCode,
                limit: limit,
                servers: [budgetServer("s", in: .vsCode, .enabled, tools: tools)],
                routes: []
            ).state
        }
        #expect(state(7) == .ok)
        #expect(state(8) == .near)
        // At the cap every tool still arrives.
        #expect(state(10) == .near)
        #expect(state(11) == .over)
    }

    @Test("Over is a warning, near is information")
    func severity() {
        let over = ToolBudget.evaluate(
            clientID: .vsCode, limit: limit,
            servers: [budgetServer("s", in: .vsCode, .enabled, tools: 12)], routes: []
        )
        #expect(over.severity == .warning)
    }

    @Test("Unmeasured servers make the count a lower bound")
    func unmeasured() {
        var failed = budgetServer("failed", in: .cursor, .enabled, tools: 0)
        failed.health = HealthResult(
            status: .failed, checkedAt: .timestamp(), toolCount: nil, tools: [],
            message: nil, stderr: nil, durationSeconds: 0, serverName: nil, serverVersion: nil
        )
        var never = budgetServer("never", in: .cursor, .enabled, tools: 0)
        never.health = nil
        let servers = [budgetServer("measured", in: .cursor, .enabled, tools: 9), failed, never]

        let budget = ToolBudget.evaluate(clientID: .cursor, limit: limit, servers: servers, routes: [])
        #expect(budget.toolCount == 9)
        #expect(budget.unmeasuredServerIDs == ["failed", "never"])
        #expect(budget.isLowerBound)
        // A lower bound already near the cap is still near; nothing is guessed
        // for the servers nobody has checked.
        #expect(budget.state == .near)
    }

    @Test("A measured server with no tools counts as zero, not unmeasured")
    func measuredZero() {
        let budget = ToolBudget.evaluate(
            clientID: .cursor, limit: limit,
            servers: [budgetServer("empty", in: .cursor, .enabled, tools: 0)], routes: []
        )
        #expect(budget.toolCount == 0)
        #expect(!budget.isLowerBound)
    }

    @Test("Gateway masking removes hidden tools, for that client only")
    func masking() {
        let servers = [budgetServer("github", in: .cursor, .enabled, tools: 6, alsoIn: .codex)]
        let routes = [gatewayRoute(server: "github", client: .cursor, exposing: ["tool0", "tool1", "gone"])]

        let masked = ToolBudget.evaluate(clientID: .cursor, limit: limit, servers: servers, routes: routes)
        // "gone" is not offered by the server, so the filter has nothing to pass.
        #expect(masked.toolCount == 2)
        #expect(masked.maskedToolCount == 4)

        let other = ToolBudget.evaluate(clientID: .codex, limit: limit, servers: servers, routes: routes)
        #expect(other.toolCount == 6)
        #expect(other.maskedToolCount == 0)
    }

    @Test("A route without an allow list hides nothing")
    func unmaskedRoute() {
        let servers = [budgetServer("github", in: .cursor, .enabled, tools: 6)]
        let routes = [gatewayRoute(server: "github", client: .cursor, exposing: nil)]
        let budget = ToolBudget.evaluate(clientID: .cursor, limit: limit, servers: servers, routes: routes)
        #expect(budget.toolCount == 6)
        #expect(budget.maskedToolCount == 0)
    }

    @Test("A partial tool list from an older check falls back to the allow list's length")
    func partialToolList() {
        var server = budgetServer("legacy", in: .cursor, .enabled, tools: 0)
        server.health = passing(toolCount: 26, names: ["a", "b"])
        let routes = [gatewayRoute(server: "legacy", client: .cursor, exposing: ["a", "x", "y"])]
        let budget = ToolBudget.evaluate(clientID: .cursor, limit: limit, servers: [server], routes: routes)
        #expect(budget.toolCount == 3)
        #expect(budget.maskedToolCount == 23)
    }

    @Test("No cap means a count and no state")
    func nilLimit() {
        let budget = ToolBudget.evaluate(
            clientID: .claudeCode, limit: nil,
            servers: [budgetServer("big", in: .claudeCode, .enabled, tools: 500)], routes: []
        )
        #expect(budget.toolCount == 500)
        #expect(budget.limit == nil)
        #expect(budget.state == nil)
        #expect(budget.severity == nil)
    }

    @Test("The registry encodes only documented caps")
    func registryLimits() {
        #expect(ClientRegistry.descriptor(for: .vsCode).toolLimit?.maxTools == 128)
        #expect(ClientRegistry.descriptor(for: .cursor).toolLimit == nil)
        #expect(ClientRegistry.descriptor(for: .claudeDesktop).toolLimit == nil)
        #expect(ClientRegistry.descriptor(for: .claudeCode).toolLimit == nil)
        #expect(ClientRegistry.descriptor(for: .codex).toolLimit == nil)
    }
}

// MARK: - Helpers

private func passing(toolCount: Int, names: [String]) -> HealthResult {
    HealthResult(
        status: .passed,
        checkedAt: .timestamp(),
        toolCount: toolCount,
        tools: names.map { ToolSummary(name: $0, description: nil) },
        message: nil,
        stderr: nil,
        durationSeconds: 0.1,
        serverName: nil,
        serverVersion: nil
    )
}

private func budgetServer(
    _ id: String,
    in client: ClientID,
    _ enablement: Enablement,
    tools: Int,
    alsoIn other: ClientID? = nil
) -> Server {
    var enabledIn: [ClientID: Enablement] = [client: enablement]
    if let other { enabledIn[other] = .enabled }
    return Server(
        id: id,
        name: id,
        transport: .stdio,
        command: id,
        args: [],
        env: [],
        url: nil,
        enabledIn: enabledIn,
        origin: .configFile(client),
        fingerprint: Server.fingerprint(command: id, args: [], url: nil),
        isBundled: false,
        definitionSource: "{}",
        health: passing(toolCount: tools, names: (0..<tools).map { "tool\($0)" })
    )
}

private func gatewayRoute(server: String, client: ClientID, exposing tools: [String]?) -> GatewayRoute {
    GatewayRoute(
        id: UUID().uuidString,
        serverID: server,
        serverName: server,
        clientID: client,
        gatewayCommand: "/usr/local/bin/kytto-mcp-proxy",
        command: server,
        arguments: [],
        environment: [],
        directDefinitionSecretID: "unused",
        exposedTools: tools
    )
}
