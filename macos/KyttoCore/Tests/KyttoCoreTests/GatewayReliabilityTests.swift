import Foundation
import Testing
@testable import KyttoCore

/// What happens to Gateway mode when something it depends on goes away: the
/// app is moved, a keychain entry is lost, a token is rotated, the server dies.
@Suite("Gateway — when a dependency fails")
struct GatewayReliabilityTests {
    private struct Harness {
        let home: FakeHome
        let ledger = DigestLedger()
        let paths: KyttoPaths
        let routes: GatewayRouteStore
        let secrets = InMemorySecretStore()
        let helperURL: URL

        init() throws {
            home = try FakeHome()
            paths = KyttoPaths(home: home.root)
            routes = GatewayRouteStore(paths: paths)
            helperURL = try Self.makeHelper(in: home.root.appending(path: "KyttoMCP.app/Contents/MacOS"))
        }

        static func makeHelper(in directory: URL) throws -> URL {
            let url = directory.appending(path: "kytto-mcp-proxy")
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try Data("#!/bin/sh\n".utf8).write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
            return url
        }

        func service(helper: URL? = nil) -> GatewayMigrationService {
            GatewayMigrationService(
                home: home.root,
                helperURL: helper ?? helperURL,
                writer: ConfigWriter(backups: BackupStore(paths: paths)),
                ledger: ledger,
                routes: routes,
                secrets: secrets
            )
        }

        @discardableResult
        func discover() -> DiscoveryResult {
            Discovery(
                home: home.root,
                locator: StubAppLocator(bundleIdentifiers: [], executables: []),
                ledger: ledger
            ).run()
        }

        /// The app moved: the old helper is gone and a new copy exists elsewhere.
        func moveApp() throws -> URL {
            try FileManager.default.removeItem(at: home.root.appending(path: "KyttoMCP.app"))
            return try Self.makeHelper(in: home.root.appending(path: "Applications/Kytto MCP.app/Contents/MacOS"))
        }
    }

    private let cursorRoute = "a6ed52d3-be69-4a4f-a215-ac17c685f520"
    private let codexRoute = "645c1896-23de-47ea-bdbd-f97f1fc63896"
    private let tokenKey = "GITHUB_PERSONAL_ACCESS_TOKEN"

    private func enableCursor(_ harness: Harness) throws -> (config: URL, original: String) {
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let config = harness.home.root.appending(path: ".cursor/mcp.json")
        let original = try String(contentsOf: config, encoding: .utf8)
        let github = try #require(harness.discover().servers.first { $0.id == "github" })
        _ = try harness.service().enable(server: github, clientID: .cursor, routeID: cursorRoute)
        harness.discover()
        return (config, original)
    }

    // MARK: - The app was moved

    @Test("a working route has nothing to report")
    func healthyRoute() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        _ = try enableCursor(harness)
        #expect(harness.service().diagnose().isEmpty)
    }

    @Test("a moved app is noticed, repaired in place, and still restores byte for byte")
    func movedAppJSON() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let (config, original) = try enableCursor(harness)
        let oldHelper = harness.helperURL.path
        let newHelper = try harness.moveApp()

        // From the old copy's point of view there is nothing to repair with.
        #expect(harness.service().diagnose().map(\.problem) == [.helperMissing(path: oldHelper, repairable: false)])
        let moved = harness.service(helper: newHelper)
        let diagnosis = try #require(moved.diagnose().first)
        #expect(diagnosis.problem == .helperMissing(path: oldHelper, repairable: true))
        #expect(diagnosis.serverID == "github")
        #expect(diagnosis.routeID == cursorRoute)

        let repaired = try moved.repair(routeID: cursorRoute)
        #expect(repaired.backupID != nil)
        #expect(repaired.route.gatewayCommand == newHelper.path)
        let document = try JSONDocument.parse(String(contentsOf: config, encoding: .utf8))
        let definition = try #require(document.value(at: ["mcpServers", "github"]))
        #expect(definition["command"]?.stringValue == newHelper.path)
        #expect(definition["args"]?.elements?.compactMap(\.stringValue) == ["--route", cursorRoute])
        // Nothing else in the file moved.
        #expect(document.value(at: ["mcpServers", "figma", "url"])?.stringValue == "https://mcp.figma.com/sse")
        #expect(try harness.routes.route(id: cursorRoute).gatewayCommand == newHelper.path)
        #expect(moved.diagnose().isEmpty)

        harness.discover()
        _ = try moved.restore(routeID: cursorRoute)
        #expect(try String(contentsOf: config, encoding: .utf8) == original)
    }

    @Test("repairing a Codex route keeps the settings Codex reads from the block")
    func movedAppTOML() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        let config = harness.home.root.appending(path: ".codex/config.toml")
        let original = try String(contentsOf: config, encoding: .utf8)
        let filesystem = try #require(harness.discover().servers.first { $0.id == "filesystem" })
        _ = try harness.service().enable(server: filesystem, clientID: .codex, routeID: codexRoute)
        harness.discover()
        let before = try String(contentsOf: config, encoding: .utf8)

        let newHelper = try harness.moveApp()
        let moved = harness.service(helper: newHelper)
        #expect(moved.diagnose().map(\.routeID) == [codexRoute])
        _ = try moved.repair(routeID: codexRoute)

        let after = try String(contentsOf: config, encoding: .utf8)
        #expect(after == before.replacingOccurrences(of: harness.helperURL.path, with: newHelper.path))
        #expect(moved.diagnose().isEmpty)

        harness.discover()
        _ = try moved.restore(routeID: codexRoute)
        #expect(try String(contentsOf: config, encoding: .utf8) == original)
    }

    @Test("a repair refuses a config that changed on disk")
    func repairRespectsDigestGuard() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let (config, _) = try enableCursor(harness)
        let moved = harness.service(helper: try harness.moveApp())
        let edited = try String(contentsOf: config, encoding: .utf8) + "\n"
        try Data(edited.utf8).write(to: config, options: .atomic)

        #expect(throws: ConfigWriteError.self) { try moved.repair(routeID: cursorRoute) }
        #expect(try String(contentsOf: config, encoding: .utf8) == edited)
    }

    @Test("a definition pointing at a route Kytto has lost is reported")
    func unknownRoute() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        _ = try enableCursor(harness)
        try harness.routes.remove(id: cursorRoute)

        let diagnosis = try #require(harness.service().diagnose().first)
        #expect(diagnosis.problem == .unknownRoute)
        #expect(diagnosis.serverName == "github")
        #expect(diagnosis.clientID == .cursor)
    }

    // MARK: - The credential store lost something

    @Test("Direct mode is rebuilt from the route when the stored original is gone")
    func restoreWithoutOriginal() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let (config, _) = try enableCursor(harness)
        try harness.secrets.delete(for: GatewayRoute.directDefinitionSecretID(routeID: cursorRoute))

        let result = try harness.service().restore(routeID: cursorRoute)
        #expect(result.missingEnvironmentKeys.isEmpty)
        let definition = try #require(
            JSONDocument.parse(String(contentsOf: config, encoding: .utf8)).value(at: ["mcpServers", "github"])
        )
        #expect(definition["command"]?.stringValue == "npx")
        #expect(definition["args"]?.elements?.compactMap(\.stringValue) == ["-y", "@modelcontextprotocol/server-github"])
        #expect(definition["env"]?[tokenKey]?.stringValue == "ghp_exampletokenvalue")
        #expect(try harness.routes.all().isEmpty)
    }

    @Test("a value that is gone too is named instead of written empty")
    func restoreNamesLostValues() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let (config, _) = try enableCursor(harness)
        try harness.secrets.delete(for: GatewayRoute.directDefinitionSecretID(routeID: cursorRoute))
        try harness.secrets.delete(for: GatewayRoute.environmentSecretID(routeID: cursorRoute, key: tokenKey))

        let result = try harness.service().restore(routeID: cursorRoute)
        #expect(result.missingEnvironmentKeys == [tokenKey])
        let definition = try #require(
            JSONDocument.parse(String(contentsOf: config, encoding: .utf8)).value(at: ["mcpServers", "github"])
        )
        #expect(definition["command"]?.stringValue == "npx")
        #expect(definition["env"]?[tokenKey] == nil)
    }

    // MARK: - A secret was rotated

    @Test("a rotated value reaches the route and the definition it would restore")
    func rotationReachesRoute() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let (config, original) = try enableCursor(harness)

        let changed = harness.service().rotateEnvironmentValue(
            key: tokenKey, from: "ghp_exampletokenvalue", to: "ghp_rotated"
        )
        #expect(changed.map(\.id) == [cursorRoute])
        #expect(try harness.routes.resolve(id: cursorRoute, secrets: harness.secrets).environment[tokenKey] == "ghp_rotated")

        _ = try harness.service().restore(routeID: cursorRoute)
        // Everything as it was, except the one value.
        #expect(
            try String(contentsOf: config, encoding: .utf8)
                == original.replacingOccurrences(of: "ghp_exampletokenvalue", with: "ghp_rotated")
        )
    }

    @Test("a route holding a different value under the same name is left alone")
    func rotationIgnoresOtherValues() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        _ = try enableCursor(harness)

        #expect(harness.service().rotateEnvironmentValue(key: tokenKey, from: "ghp_someone_else", to: "ghp_new").isEmpty)
        #expect(
            try harness.routes.resolve(id: cursorRoute, secrets: harness.secrets).environment[tokenKey]
                == "ghp_exampletokenvalue"
        )
    }

    @Test("a TOML definition has only the matching value replaced")
    func tomlDefinitionRotation() throws {
        let block = """
        [mcp_servers.x]
        command = "npx"
        env = { TOKEN = "old", OTHER = "old" }

        [mcp_servers.x.env]
        TOKEN = "old"
        KEEP = "old"
        """
        let rewritten = try #require(GatewayMigrationService.replacingEnvironmentValue(
            "TOKEN", from: "old", to: "new", in: block, format: .toml
        ))
        #expect(rewritten == block
            .replacingOccurrences(of: #"TOKEN = "old""#, with: #"TOKEN = "new""#))
    }

    // MARK: - Doctor

    private func event(
        _ kind: GatewayEventKind,
        session: String,
        secondsAgo: TimeInterval,
        now: Date,
        unexpected: Bool? = nil,
        reason: GatewayFailureReason? = nil
    ) -> GatewayEvent {
        GatewayEvent(
            sessionID: session,
            timestamp: now.addingTimeInterval(-secondsAgo),
            kind: kind,
            serverID: "github",
            clientID: "cursor",
            exitCode: kind == .sessionEnded ? 1 : nil,
            unexpected: unexpected,
            reason: reason?.rawValue
        )
    }

    @Test("a missing helper becomes a finding that offers the repair")
    func helperFinding() {
        let diagnosis = GatewayRouteDiagnosis(
            serverID: "github", serverName: "github", clientID: .cursor, routeID: "r1",
            problem: .helperMissing(path: "/old/kytto-mcp-proxy", repairable: true)
        )
        let finding = GatewayDoctor.findings(diagnoses: [diagnosis], events: [])["github"]?.first
        #expect(finding?.code == "gateway-helper-missing")
        #expect(finding?.severity == .error)
        #expect(finding?.action == .repairGatewayRoute)
        #expect(finding?.routeID == "r1")
        #expect(finding?.title.contains("Cursor") == true)

        let stuck = GatewayRouteDiagnosis(
            serverID: "github", serverName: "github", clientID: .cursor, routeID: "r1",
            problem: .helperMissing(path: "/old/kytto-mcp-proxy", repairable: false)
        )
        #expect(GatewayDoctor.findings(diagnoses: [stuck], events: [])["github"]?.first?.action == nil)
    }

    @Test("a server that keeps stopping is reported until it runs cleanly again")
    func crashFinding() {
        let now = Date()
        let crashes = [
            event(.sessionStarted, session: "a", secondsAgo: 600, now: now),
            event(.sessionEnded, session: "a", secondsAgo: 500, now: now, unexpected: true),
            event(.sessionStarted, session: "b", secondsAgo: 400, now: now),
            event(.sessionEnded, session: "b", secondsAgo: 300, now: now, unexpected: true),
        ]
        let finding = GatewayDoctor.findings(diagnoses: [], events: crashes, now: now)["github"]?.first
        #expect(finding?.code == "gateway-server-stopped")
        #expect(finding?.action == .runHealthCheck)
        #expect(finding?.detail.contains("2 times") == true)

        let recovered = crashes + [event(.sessionStarted, session: "c", secondsAgo: 100, now: now)]
        #expect(GatewayDoctor.findings(diagnoses: [], events: recovered, now: now).isEmpty)
    }

    @Test("an ordinary exit and an old crash are not findings")
    func quietSessions() {
        let now = Date()
        let events = [
            event(.sessionEnded, session: "a", secondsAgo: 60, now: now, unexpected: false),
            event(.sessionEnded, session: "b", secondsAgo: 60, now: now),
            event(.sessionEnded, session: "c", secondsAgo: GatewayDoctor.failureWindow + 60, now: now, unexpected: true),
        ]
        #expect(GatewayDoctor.findings(diagnoses: [], events: events, now: now).isEmpty)
    }

    @Test("an unreadable secret offers Restore Direct for the route it belongs to")
    func secretFinding() {
        let now = Date()
        let route = GatewayRoute(
            id: "r9", serverID: "github", serverName: "github", clientID: .cursor,
            gatewayCommand: "/x/kytto-mcp-proxy", command: "npx", arguments: [],
            environment: [], directDefinitionSecretID: "d"
        )
        let events = [event(.sessionFailed, session: "a", secondsAgo: 30, now: now, reason: .secretUnavailable)]
        let finding = GatewayDoctor.findings(diagnoses: [], events: events, routes: [route], now: now)["github"]?.first
        #expect(finding?.code == "gateway-secret-unavailable")
        #expect(finding?.action == .restoreDirectMode)
        #expect(finding?.routeID == "r9")
    }

    // MARK: - Journal

    @Test("the summary counts refusals and server failures, and old journals still decode")
    func summaryCounts() throws {
        let now = Date()
        let summary = GatewayActivitySummary(events: [
            GatewayEvent(sessionID: "a", kind: .toolCallRefused, serverID: "s", clientID: "c", toolName: "write", succeeded: false),
            event(.sessionEnded, session: "a", secondsAgo: 1, now: now, unexpected: true),
            event(.sessionFailed, session: "b", secondsAgo: 1, now: now, reason: .launchFailed),
            event(.sessionEnded, session: "c", secondsAgo: 1, now: now, unexpected: false),
        ])
        #expect(summary.refusedCalls == 1)
        #expect(summary.serverFailures == 2)
        // A refusal never reached the server, so it is not a failed call.
        #expect(summary.failedCalls == 0)

        let old = #"{"eventID":"e","sessionID":"s","timestamp":"2026-10-01T10:00:00Z","kind":"session.ended","serverID":"a","clientID":"b","exitCode":0}"#
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let decoded = try decoder.decode(GatewayEvent.self, from: Data(old.utf8))
        #expect(decoded.unexpected == nil)
        #expect(decoded.reason == nil)
    }

    @Test("the view is filled from the rotated journal instead of going blank")
    func readsAcrossRotation() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let previous = try JSONLinesGatewayEventSink(url: paths.gatewayEvents.appendingPathExtension("1"))
        previous.record(GatewayEvent(sessionID: "old", kind: .sessionStarted, serverID: "s", clientID: "c"))
        let current = try JSONLinesGatewayEventSink(url: paths.gatewayEvents)
        current.record(GatewayEvent(sessionID: "new", kind: .sessionStarted, serverID: "s", clientID: "c"))

        let sessions = Set(GatewayActivityStore(paths: paths).summary().events.map(\.sessionID))
        #expect(sessions == ["old", "new"])
    }
}
