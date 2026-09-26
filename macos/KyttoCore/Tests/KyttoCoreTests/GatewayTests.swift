import Darwin
import Foundation
import Testing
@testable import KyttoCore

@Suite("Gateway preview")
struct GatewayTests {
    @Test("the invocation keeps route metadata separate from the real command")
    func invocation() throws {
        let parsed = try GatewayInvocation.parse([
            "--server-id", "github",
            "--client-id", "cursor",
            "--event-log", "/tmp/kytto-events.jsonl",
            "--", "npx", "-y", "@example/server",
        ])

        #expect(parsed.serverID == "github")
        #expect(parsed.clientID == "cursor")
        #expect(parsed.eventLogURL?.path == "/tmp/kytto-events.jsonl")
        #expect(parsed.command == "npx")
        #expect(parsed.arguments == ["-y", "@example/server"])
    }

    @Test("the real command must be separated from gateway options")
    func separatorIsRequired() {
        #expect(throws: GatewayInvocationError.missingSeparator) {
            try GatewayInvocation.parse(["npx", "server"])
        }
    }

    @Test("an opaque route is accepted without exposing its upstream command")
    func routeInvocation() throws {
        let parsed = try GatewayInvocation.parse([
            "--route", "A6ED52D3-BE69-4A4F-A215-AC17C685F520",
            "--routes", "/tmp/kytto-routes.json",
        ])

        #expect(parsed.routeID == "a6ed52d3-be69-4a4f-a215-ac17c685f520")
        #expect(parsed.routesURL?.path == "/tmp/kytto-routes.json")
        #expect(parsed.command == nil)
        #expect(parsed.arguments.isEmpty)
    }

    @Test("tool calls are correlated without retaining arguments or results")
    func toolCallMetadata() throws {
        let sink = MemoryGatewaySink()
        let observer = GatewayMessageObserver(
            sessionID: "session-1",
            serverID: "github",
            clientID: "cursor",
            sink: sink
        )

        observer.start()
        observer.accept(Data(#"{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"create_issue","arguments":{"token":"secret-value"}}}"#.utf8), direction: .clientToServer)
        observer.accept(Data("\n".utf8), direction: .clientToServer)
        observer.accept(Data(#"{"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"private-result"}]}}"#.utf8), direction: .serverToClient)
        observer.accept(Data("\n".utf8), direction: .serverToClient)
        observer.finish(exitCode: 0)

        let events = sink.events
        #expect(events.map(\.kind) == [.sessionStarted, .toolCallStarted, .toolCallCompleted, .sessionEnded])
        #expect(events[1].toolName == "create_issue")
        #expect(events[1].requestID == "7")
        #expect(events[2].succeeded == true)
        #expect(events[3].exitCode == 0)

        let encoded = String(decoding: try JSONEncoder().encode(events), as: UTF8.self)
        #expect(!encoded.contains("secret-value"))
        #expect(!encoded.contains("private-result"))
    }

    @Test("JSONL event files are private and append complete records")
    func jsonLinesSink() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let url = home.root.appending(path: "Gateway/events.jsonl")
        let sink = try JSONLinesGatewayEventSink(url: url)
        sink.record(GatewayEvent(
            sessionID: "one",
            kind: .sessionStarted,
            serverID: "github",
            clientID: "cursor"
        ))
        sink.record(GatewayEvent(
            sessionID: "one",
            kind: .sessionEnded,
            serverID: "github",
            clientID: "cursor",
            exitCode: 0
        ))

        let lines = try String(contentsOf: url, encoding: .utf8)
            .split(separator: "\n")
        #expect(lines.count == 2)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        #expect(try decoder.decode(GatewayEvent.self, from: Data(lines[0].utf8)).kind == .sessionStarted)
        let mode = try #require(
            FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        )
        #expect(mode.intValue & 0o077 == 0)
    }

    @Test("the upstream and its descendants start in a dedicated process group")
    func managedProcessGroup() throws {
        let process = try GatewayManagedProcess(
            executable: "/bin/sh",
            arguments: ["-c", "sleep 30 & wait"],
            environment: ShellEnvironment.current.environment()
        )

        #expect(getpgid(process.processIdentifier) == process.processIdentifier)
        usleep(100_000)
        #expect(kill(-process.processIdentifier, SIGTERM) == 0)
        #expect(process.waitUntilExit() == 128 + SIGTERM)

        let deadline = Date().addingTimeInterval(1)
        while kill(-process.processIdentifier, 0) == 0, Date() < deadline {
            usleep(10_000)
        }
        // The claim is that nothing in the group survived, which is exactly what
        // a failed signal says. Which failure it is depends on timing the test
        // does not control: this suite spawns processes in parallel, and once
        // the group's last member has been reaped the kernel is entitled to
        // answer EPERM rather than ESRCH. Asserting one of the two made the
        // test fail perhaps one run in three under load, always for a reason
        // that had nothing to do with process groups. `errno` is still captured
        // before the first expectation, because reading it afterwards would be
        // a second, quieter version of the same bug.
        let probe = kill(-process.processIdentifier, 0)
        let probeError = errno
        #expect(probe == -1)
        #expect(probeError == ESRCH || probeError == EPERM)
    }
}

@Suite("Gateway migration")
struct GatewayMigrationTests {
    private struct Harness {
        let home: FakeHome
        let ledger = DigestLedger()
        let paths: KyttoPaths
        let backups: BackupStore
        let routes: GatewayRouteStore
        let secrets = InMemorySecretStore()
        let helperURL: URL

        init() throws {
            home = try FakeHome()
            paths = KyttoPaths(home: home.root)
            backups = BackupStore(paths: paths)
            routes = GatewayRouteStore(paths: paths)
            helperURL = home.root.appending(path: "KyttoMCP.app/Contents/MacOS/kytto-mcp-proxy")
            try FileManager.default.createDirectory(
                at: helperURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try Data("#!/bin/sh\n".utf8).write(to: helperURL)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: helperURL.path)
        }

        func service() -> GatewayMigrationService {
            GatewayMigrationService(
                home: home.root,
                helperURL: helperURL,
                writer: ConfigWriter(backups: backups),
                ledger: ledger,
                routes: routes,
                secrets: secrets
            )
        }

        func discover() -> DiscoveryResult {
            Discovery(
                home: home.root,
                locator: StubAppLocator(bundleIdentifiers: [], executables: []),
                ledger: ledger
            ).run()
        }
    }

    @Test("JSON migration removes plaintext secrets and restores the file byte for byte")
    func jsonRoundTrip() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let original = try String(
            contentsOf: harness.home.root.appending(path: ".cursor/mcp.json"),
            encoding: .utf8
        )
        let github = try #require(harness.discover().servers.first { $0.id == "github" })
        let routeID = "a6ed52d3-be69-4a4f-a215-ac17c685f520"

        let preview = try harness.service().preview(server: github, clientID: .cursor, routeID: routeID)
        #expect(!preview.directDefinitionPreview.contains("ghp_exampletokenvalue"))
        #expect(preview.gatewayDefinitionPreview.contains(routeID))
        #expect(preview.environmentKeys == ["GITHUB_PERSONAL_ACCESS_TOKEN"])

        let enabled = try harness.service().enable(server: github, clientID: .cursor, routeID: routeID)
        #expect(enabled.backupID != nil)
        let migratedText = try String(
            contentsOf: harness.home.root.appending(path: ".cursor/mcp.json"),
            encoding: .utf8
        )
        let migrated = try JSONDocument.parse(migratedText)
        let definition = try #require(migrated.value(at: ["mcpServers", "github"]))
        #expect(definition["command"]?.stringValue == harness.helperURL.path)
        #expect(definition["args"]?.elements?.compactMap(\.stringValue) == ["--route", routeID])
        #expect(definition["env"] == nil)
        #expect(migrated.value(at: ["mcpServers", "figma", "url"])?.stringValue == "https://mcp.figma.com/sse")

        let routeFile = try String(contentsOf: harness.paths.gatewayRoutes, encoding: .utf8)
        #expect(!routeFile.contains("ghp_exampletokenvalue"))
        let launch = try harness.routes.resolve(id: routeID, secrets: harness.secrets)
        #expect(launch.route.command == "npx")
        #expect(launch.route.arguments == ["-y", "@modelcontextprotocol/server-github"])
        #expect(launch.environment["GITHUB_PERSONAL_ACCESS_TOKEN"] == "ghp_exampletokenvalue")

        _ = harness.discover()
        _ = try harness.service().restore(routeID: routeID)
        let restored = try String(
            contentsOf: harness.home.root.appending(path: ".cursor/mcp.json"),
            encoding: .utf8
        )
        #expect(restored == original)
        #expect(try harness.routes.all().isEmpty)
        #expect(try harness.secrets.value(for: GatewayRoute.directDefinitionSecretID(routeID: routeID)) == nil)
    }

    @Test("TOML migration preserves unrelated tables and restores the original block")
    func tomlRoundTrip() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        let configURL = harness.home.root.appending(path: ".codex/config.toml")
        let original = try String(contentsOf: configURL, encoding: .utf8)
        let filesystem = try #require(harness.discover().servers.first { $0.id == "filesystem" })
        let routeID = "645c1896-23de-47ea-bdbd-f97f1fc63896"

        _ = try harness.service().enable(server: filesystem, clientID: .codex, routeID: routeID)
        let migrated = try TOMLDocument.parse(String(contentsOf: configURL, encoding: .utf8))
        let table = try #require(migrated.table(at: ["mcp_servers", "filesystem"]))
        #expect(table.value("command")?.stringValue == harness.helperURL.path)
        #expect(table.value("args")?.elements?.compactMap(\.stringValue) == ["--route", routeID])
        #expect(migrated.table(at: ["mcp_servers", "filesystem", "env"]) == nil)
        #expect(migrated.table(at: ["history"])?.value("persistence")?.stringValue == "save-all")

        _ = harness.discover()
        _ = try harness.service().restore(routeID: routeID)
        #expect(try String(contentsOf: configURL, encoding: .utf8) == original)
    }

    @Test("a changed config is refused before migration writes anything")
    func staleConfig() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let github = try #require(harness.discover().servers.first { $0.id == "github" })
        try harness.home.write(Fixture.cursorMCP.text + "\n", to: ".cursor/mcp.json")

        #expect(throws: ConfigWriteError.self) {
            try harness.service().enable(
                server: github,
                clientID: .cursor,
                routeID: "87d64ab8-e43b-4d18-afdc-0cd04052922c"
            )
        }
        #expect(try harness.routes.all().isEmpty)
    }

    @Test("a reused route id is refused before it can overwrite another route's credentials")
    func routeIDCollision() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        try harness.home.write(Fixture.codexConfig.text, to: ".codex/config.toml")
        let discovery = harness.discover()
        let github = try #require(discovery.servers.first { $0.id == "github" })
        let filesystem = try #require(discovery.servers.first { $0.id == "filesystem" })
        let routeID = "5b474982-5749-4cd0-882d-3f80741b7c90"

        _ = try harness.service().enable(server: github, clientID: .cursor, routeID: routeID)
        #expect(throws: GatewayMigrationError.routeAlreadyExists) {
            try harness.service().enable(server: filesystem, clientID: .codex, routeID: routeID)
        }

        let launch = try harness.routes.resolve(id: routeID, secrets: harness.secrets)
        #expect(launch.route.serverID == "github")
        #expect(launch.environment["GITHUB_PERSONAL_ACCESS_TOKEN"] == "ghp_exampletokenvalue")
    }

    @Test("a backup cannot revive a gateway route after its credentials were removed")
    func orphanedGatewayBackup() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        let routeID = "56a3af61-f47c-4ec4-bd43-63b5ace657a8"
        let backup = #"{"mcpServers":{"github":{"command":"/Applications/KyttoMCP.app/Contents/MacOS/kytto-mcp-proxy","args":["--route","56a3af61-f47c-4ec4-bd43-63b5ace657a8"]}}}"#

        #expect(throws: GatewayMigrationError.orphanedRouteInBackup(routeID)) {
            try harness.service().validateBackupRestore(backup, clientID: .cursor)
        }
    }

    @Test("restoring a Direct backup removes route material no longer used by the client")
    func directBackupCleanup() throws {
        let harness = try Harness()
        defer { harness.home.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let configURL = harness.home.root.appending(path: ".cursor/mcp.json")
        let original = try String(contentsOf: configURL, encoding: .utf8)
        let github = try #require(harness.discover().servers.first { $0.id == "github" })
        let routeID = "fcfbcf7a-e1d7-4d33-8906-c83eb3107c53"

        _ = try harness.service().enable(server: github, clientID: .cursor, routeID: routeID)
        try Data(original.utf8).write(to: configURL, options: .atomic)
        harness.ledger.record(ConfigWriter.digest(of: configURL), for: configURL)

        try harness.service().reconcileAfterBackupRestore(clientID: .cursor)

        #expect(try harness.routes.all().isEmpty)
        #expect(try harness.secrets.value(for: GatewayRoute.directDefinitionSecretID(routeID: routeID)) == nil)
        #expect(
            try harness.secrets.value(
                for: GatewayRoute.environmentSecretID(
                    routeID: routeID,
                    key: "GITHUB_PERSONAL_ACCESS_TOKEN"
                )
            ) == nil
        )
    }
}

private final class MemoryGatewaySink: GatewayEventSinking, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [GatewayEvent] = []

    var events: [GatewayEvent] {
        lock.withLock { stored }
    }

    func record(_ event: GatewayEvent) {
        lock.withLock { stored.append(event) }
    }
}
