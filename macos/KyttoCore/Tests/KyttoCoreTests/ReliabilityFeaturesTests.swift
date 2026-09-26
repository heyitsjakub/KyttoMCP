import Foundation
import Testing
@testable import KyttoCore

@Suite("MCP Doctor")
struct MCPDoctorTests {
    @Test("a successful PATH resolution offers only the verified pin repair")
    func verifiedExecutable() throws {
        var server = makeServer(command: "npx")
        server.health = health(
            tools: [],
            resolvedCommand: "/opt/homebrew/bin/npx"
        )

        let report = MCPDoctor.analyze(server)
        let finding = try #require(report.findings.first { $0.code == "unpinned-command" })
        #expect(finding.action == .pinResolvedCommand)
        #expect(finding.detail.contains("/opt/homebrew/bin/npx"))
    }

    @Test("missing environment values are errors and never guessed")
    func missingEnvironment() throws {
        let server = makeServer(
            command: "npx",
            env: [EnvEntry(key: "API_TOKEN", value: nil)]
        )
        let finding = try #require(MCPDoctor.analyze(server).findings.first {
            $0.code == "missing-environment"
        })
        #expect(finding.severity == .error)
        #expect(finding.action == nil)
    }
}

@Suite("Contract Guard")
struct ContractGuardTests {
    @Test("model-facing changes are classified without tool payloads")
    func changes() {
        let old = [
            ToolSummary(
                name: "read_file",
                description: "Read a file",
                inputSchemaJSON: #"{"type":"object"}"#,
                annotations: ToolAnnotations(readOnlyHint: true)
            ),
            ToolSummary(name: "removed", description: nil),
        ]
        let new = [
            ToolSummary(
                name: "read_file",
                description: "Read any file",
                inputSchemaJSON: #"{"type":"object","required":["path"]}"#,
                annotations: ToolAnnotations(readOnlyHint: false)
            ),
            ToolSummary(name: "added", description: nil),
        ]

        let result = ContractGuard.changes(from: old, to: new)
        #expect(result.contains { $0.kind == .toolRemoved && $0.severity == .breaking })
        #expect(result.contains { $0.kind == .toolAdded })
        #expect(result.contains { $0.kind == .descriptionChanged })
        #expect(result.contains { $0.kind == .inputSchemaChanged && $0.severity == .breaking })
        #expect(result.contains { $0.kind == .annotationsChanged })
    }

    @Test("a failed check does not erase the last successful contract baseline")
    func baselineSurvivesFailure() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = MetadataStore(paths: KyttoPaths(home: home.root))
        let original = health(
            tools: [ToolSummary(name: "read", description: "Read")],
            resolvedCommand: "/server"
        )
        try store.record(health: original, tokenWeight: nil, for: "server")
        try store.record(
            health: HealthResult(
                status: .failed,
                checkedAt: .timestamp(),
                toolCount: nil,
                tools: [],
                message: "crashed",
                stderr: nil,
                durationSeconds: 0.1,
                serverName: nil,
                serverVersion: nil
            ),
            tokenWeight: nil,
            for: "server"
        )
        let changed = health(
            tools: [ToolSummary(name: "read", description: "Read safely")],
            resolvedCommand: "/server"
        )
        try store.record(health: changed, tokenWeight: nil, for: "server")

        let entry = try #require(store.metadata(for: "server"))
        #expect(entry.contractChanges?.contains { $0.kind == .descriptionChanged } == true)
    }
}

@Suite("Gateway Activity")
struct GatewayActivityStoreTests {
    @Test("the reader summarizes complete records and ignores a partial tail")
    func summary() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let sink = try JSONLinesGatewayEventSink(url: paths.gatewayEvents)
        sink.record(GatewayEvent(
            sessionID: "session-1",
            kind: .toolCallCompleted,
            serverID: "github",
            clientID: "cursor",
            toolName: "create_issue",
            durationMilliseconds: 120,
            succeeded: false,
            errorCode: -32001
        ))
        let descriptor = try FileHandle(forWritingTo: paths.gatewayEvents)
        try descriptor.seekToEnd()
        try descriptor.write(contentsOf: Data("{partial".utf8))
        try descriptor.close()

        let summary = GatewayActivityStore(paths: paths).summary()
        #expect(summary.events.count == 1)
        #expect(summary.totalSessions == 1)
        #expect(summary.completedCalls == 1)
        #expect(summary.failedCalls == 1)
        #expect(summary.averageDurationMilliseconds == 120)
    }
}

@Suite("Context Optimizer")
struct ContextOptimizerTests {
    @Test("budgets, heavy servers and duplicate tools become recommendations")
    func recommendations() {
        let profile = ServerProfile(
            id: "profile",
            name: "Coding",
            serverIDs: ["one", "two"],
            tokenBudget: 10_000
        )
        var first = makeServer(id: "one", name: "One", command: "one")
        first.tokenWeight = TokenWeight(estimate: 22_000, method: "test", measuredAt: .timestamp())
        first.health = health(tools: [ToolSummary(name: "search", description: nil)], resolvedCommand: "/one")
        var second = makeServer(id: "two", name: "Two", command: "two")
        second.tokenWeight = TokenWeight(estimate: 2_000, method: "test", measuredAt: .timestamp())
        second.health = health(tools: [ToolSummary(name: "search", description: nil)], resolvedCommand: "/two")

        let analysis = ContextOptimizer.analyze(profile: profile, servers: [first, second])
        #expect(analysis.estimatedTokens == 24_000)
        #expect(analysis.recommendations.contains { $0.kind == .overBudget })
        #expect(analysis.recommendations.contains { $0.kind == .heavyServer })
        #expect(analysis.recommendations.contains { $0.kind == .duplicateTool })
    }
}

@Suite("Coordinated persistence")
struct CoordinatedPersistenceTests {
    @Test("hundreds of concurrent park operations retain every entry")
    func concurrentParking() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = ParkStore(paths: KyttoPaths(home: home.root))

        DispatchQueue.concurrentPerform(iterations: 200) { index in
            try? store.park(
                clientID: .cursor,
                serverName: "server-\(index)",
                sourceText: #"{"command":"tool"}"#
            )
        }

        #expect(store.all().count == 200)
    }

    @Test("hundreds of concurrent settings updates lose no increments")
    func concurrentSettings() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = SettingsStore(paths: KyttoPaths(home: home.root))

        DispatchQueue.concurrentPerform(iterations: 100) { _ in
            try? store.update { $0.backupRetention += 1 }
        }

        #expect(store.current.backupRetention == 120)
    }

    @Test("a damaged park store is never replaced by an empty one")
    func corruptParkStoreSurvivesMutation() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        try FileManager.default.createDirectory(
            at: paths.parked.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let damaged = Data("{not-json".utf8)
        try damaged.write(to: paths.parked)

        #expect(throws: DecodingError.self) {
            try ParkStore(paths: paths).park(clientID: .cursor, serverName: "new", sourceText: "{}")
        }
        #expect(try Data(contentsOf: paths.parked) == damaged)
    }
}

private func makeServer(
    id: String = "server",
    name: String = "Server",
    command: String,
    env: [EnvEntry] = []
) -> Server {
    Server(
        id: id,
        name: name,
        transport: .stdio,
        command: command,
        args: [],
        env: env,
        url: nil,
        enabledIn: [.cursor: .enabled],
        origin: .configFile(.cursor),
        fingerprint: Server.fingerprint(command: command, args: [], url: nil),
        isBundled: false,
        definitionSource: "{}"
    )
}

private func health(tools: [ToolSummary], resolvedCommand: String) -> HealthResult {
    HealthResult(
        status: .passed,
        checkedAt: .timestamp(),
        toolCount: tools.count,
        tools: tools,
        message: nil,
        stderr: nil,
        durationSeconds: 0.1,
        serverName: "fixture",
        serverVersion: "1",
        resolvedCommand: resolvedCommand,
        environmentSource: "test"
    )
}
