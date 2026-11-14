// Audit-only reproductions of defects observed on 2026-11-14.
// Copy into a DISPOSABLE KyttoCore package using the linked audit report.
// These assert current erroneous behavior. Two explicitly enabled probes crash.
// Do not add this file to the production test target.

import Foundation
import Testing
@testable import KyttoCore

private enum AuditFailure: Error { case injected }

private final class AuditFailingStore: SecretStoring, @unchecked Sendable {
    let config: URL
    let external: String
    init(config: URL, external: String) { self.config = config; self.external = external }
    func store(_ value: String, for id: String) throws {
        // Simulate a client/editor saving after the rotation wrote its config,
        // immediately before a failing credential-store operation triggers rollback.
        try Data(external.utf8).write(to: config)
        throw AuditFailure.injected
    }
    func value(for id: String) throws -> String? { nil }
    func delete(for id: String) throws {}
    func storedIdentifiers() -> Set<String> { [] }
}

@Suite("Audit 2026-11-14 — confirmed behavior")
struct Audit20261114Tests {
    @Test func duplicateToolNameBypassesTheRealGateway() throws {
        let payload = #"{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"read_file","name":"write_file","arguments":{}}}"#
        let filter = GatewayToolFilter(exposedTools: ["read_file"])
        #expect(filter.inspectClientLine(Data(payload.utf8)) == .forward)

        let h = try ToggleHarness(); defer { h.cleanUp() }
        var base = Bundle.module.bundleURL
        var helper = base.appending(path: "kytto-mcp-proxy")
        for _ in 0..<5 where !FileManager.default.isExecutableFile(atPath: helper.path) {
            base = base.deletingLastPathComponent()
            helper = base.appending(path: "kytto-mcp-proxy")
        }
        #expect(FileManager.default.isExecutableFile(atPath: helper.path))
        let routeID = UUID().uuidString.lowercased()
        // Dispatch only reports which name it received. It never writes a file
        // or performs the named tool's real operation.
        let script = #"import sys,json; [(print(json.dumps({'jsonrpc':'2.0','id':(r:=json.loads(line))['id'],'result':{'called':r['params']['name']}}),flush=True)) for line in sys.stdin]"#
        let route = GatewayRoute(id: routeID, serverID: "audit", serverName: "audit", clientID: .cursor,
            gatewayCommand: helper.path, command: "/usr/bin/python3", arguments: ["-c", script],
            environment: [], directDefinitionSecretID: "unused-audit-definition", exposedTools: ["read_file"])
        let routes = h.home.root.appending(path: "routes.json")
        try GatewayRouteStore(url: routes).insert(route)
        let process = Process()
        process.executableURL = helper
        process.arguments = ["--route", routeID, "--routes", routes.path, "--event-log", h.home.root.appending(path: "events.jsonl").path]
        // Avoid loading any personal shell startup file in this probe.
        process.environment = ["SHELL":"/usr/bin/false", "PATH":"/usr/bin:/bin"]
        let input = Pipe(), output = Pipe()
        process.standardInput = input; process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        try process.run()
        try input.fileHandleForWriting.write(contentsOf: Data((payload + "\n").utf8))
        try input.fileHandleForWriting.close()
        let response = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let object = try #require(try JSONSerialization.jsonObject(with: response) as? [String: Any])
        #expect((object["result"] as? [String: Any])?["called"] as? String == "write_file")
        #expect(object["error"] == nil)
        print("AUD-07 confirmed end to end: route exposes only read_file, but the upstream dispatched write_file")
    }

    @Test func configWriterOverwritesAnEditDuringTransform() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let original = #"{"mcpServers":{},"other":"before"}"#
        let external = #"{"mcpServers":{},"other":"external-save"}"#
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        try h.home.write(original, to: ".cursor/mcp.json")
        let receipt = try ConfigWriter(backups: h.backups).edit(
            url: url, clientID: .cursor, pathDisplay: ".cursor/mcp.json",
            expecting: ConfigWriter.digest(of: original)
        ) { document in
            // External programs do not acquire MutationCoordinator's lock.
            try Data(external.utf8).write(to: url)
            return try document.settingMember("kytto", at: [], to: "true")
        }
        let actual = try String(contentsOf: url, encoding: .utf8)
        #expect(receipt.didWrite)
        #expect(actual.contains("before"))
        #expect(!actual.contains("external-save"))
        let backup = try #require(h.backups.backup(id: receipt.backupID!, clientID: .cursor))
        #expect(try h.backups.contents(of: backup) == external)
        print("AUD-02 confirmed: a successful write erased external-save; the backup contains different bytes from the transformed snapshot")
    }

    @Test func rotationRollbackErasesAnExternalSave() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let original = #"{"mcpServers":{"svc":{"command":"node","env":{"API_KEY":"old-value"}}}}"#
        let external = #"{"mcpServers":{"svc":{"command":"node","env":{"API_KEY":"new-value"}}},"editorNote":"must-survive"}"#
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        let service = h.secrets(store: AuditFailingStore(config: url, external: external))
        let id = try #require(service.scan(servers: servers).first?.id)
        #expect(throws: AuditFailure.self) {
            try service.rotate(secretID: id, to: "new-value", servers: servers, alsoStoreInSecretStore: true)
        }
        #expect(try h.text(".cursor/mcp.json") == original)
        #expect(try !h.text(".cursor/mcp.json").contains("must-survive"))
        print("AUD-03 confirmed: rollback erased a later external save without a conflict error or a backup of that save")
    }

    @Test func failedRotationDeletesAnUnrelatedSavedCredential() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let original = #"{"mcpServers":{"svc":{"command":"node","env":{"API_KEY":"old-value"}}}}"#
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let store = InMemorySecretStore()
        let savedID = SecretsService.identifier(key: "API_KEY", value: "already-saved-value")
        try store.store("already-saved-value", for: savedID)
        let service = h.secrets(store: store)
        let id = try #require(service.scan(servers: servers).first?.id)
        // Force a digest conflict before any config or credential was changed by rotation.
        try h.home.write(original + "\n", to: ".cursor/mcp.json")
        #expect(throws: ConfigWriteError.self) {
            try service.rotate(secretID: id, to: "already-saved-value", servers: servers, alsoStoreInSecretStore: false)
        }
        #expect(try store.value(for: savedID) == nil)
        print("AUD-06 confirmed: a failed config-only rotation deleted a previously saved credential")
    }

    @Test func serverRequestCanHoldCheckPastItsTimeout() throws {
        // The fixture writes a request but never consumes our response. Its
        // own exit after 3 s keeps this audit probe bounded.
        let script = #"import sys,json,time; print(json.dumps({'jsonrpc':'2.0','id':'x'*262144,'method':'ping'}),flush=True); time.sleep(3)"#
        let started = Date()
        do {
            _ = try MCPStdioClient().listTools(command: "/usr/bin/python3", arguments: ["-c", script], environment: ["PATH":"/usr/bin:/bin"], timeout: 1)
        } catch {}
        let elapsed = Date().timeIntervalSince(started)
        try #require(elapsed > 2.5)
        print(String(format: "AUD-04 confirmed: a 1 s stdio check took %.3f s because the blocking response write had no deadline", elapsed))
    }

    @Test func metadataFirstWriteStoresRawStderrWithDefaultPermissions() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let store = MetadataStore(paths: h.paths)
        let health = HealthResult(status: .failed, checkedAt: .timestamp(), toolCount: nil, tools: [],
            message: "fixture", stderr: "Bearer audit-secret-placeholder", durationSeconds: 0,
            serverName: nil, serverVersion: nil)
        try store.record(health: health, tokenWeight: nil, for: "svc")
        let url = h.paths.root.appending(path: "server-metadata.json")
        let data = try String(contentsOf: url, encoding: .utf8)
        let fileMode = (try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as! NSNumber).intValue
        let dirMode = (try FileManager.default.attributesOfItem(atPath: h.paths.root.path)[.posixPermissions] as! NSNumber).intValue
        #expect(data.contains("audit-secret-placeholder"))
        #expect(fileMode & 0o077 != 0)
        #expect(dirMode & 0o077 != 0)
        print(String(format: "AUD-05 confirmed: raw stderr persisted in %03o file inside %03o directory (ancestor access still determines cross-user reach)", fileMode, dirMode))
    }
}

@Suite("Audit 2026-11-14 — run crash probes separately")
struct AuditCrashProbeTests {
    @Test(.enabled(if: ProcessInfo.processInfo.environment["AUDIT_CRASH_PROBE"] == "http")) func remoteHTTPErrorCodeOverflowCrashes() throws {
        let transport = RemoteHTTPTransport { _, _, _, _ in
            .init(status: 200, headers: ["content-type": "application/json"],
                body: Data(#"{"jsonrpc":"2.0","id":1,"error":{"code":1e100,"message":"fixture"}}"#.utf8))
        }
        _ = try MCPHTTPClient(transport: transport).listTools(url: "https://mcp.example/mcp")
    }

    @Test(.enabled(if: ProcessInfo.processInfo.environment["AUDIT_CRASH_PROBE"] == "stdio")) func stdioErrorCodeOverflowCrashes() throws {
        let script = #"import sys,json; [(print(json.dumps({'jsonrpc':'2.0','id':json.loads(line)['id'],'error':{'code':1e100,'message':'fixture'}}),flush=True)) for line in sys.stdin]"#
        _ = try MCPStdioClient().listTools(command: "/usr/bin/python3", arguments: ["-c", script], environment: ["PATH":"/usr/bin:/bin"], timeout: 2)
    }
}
