//
//  KyttoMCPTests.swift
//  KyttoMCPTests
//
//  Created by Jakub Hecht on 30/07/2026.
//

import Foundation
import KyttoCore
import Testing
@testable import KyttoMCP

struct KyttoMCPTests {

    @Test("the native bridge accepts only the internal main frame")
    @MainActor
    func bridgeOriginIsBoundToTheAppMainFrame() {
        #expect(CommandRouter.isTrustedOrigin(scheme: "kytto", host: "app", isMainFrame: true))
        #expect(!CommandRouter.isTrustedOrigin(scheme: "https", host: "example.invalid", isMainFrame: true))
        #expect(!CommandRouter.isTrustedOrigin(scheme: "kytto", host: "app", isMainFrame: false))
        #expect(!CommandRouter.isTrustedOrigin(scheme: "kytto", host: "other", isMainFrame: true))
    }

    @Test("malformed wire messages return stable envelopes instead of trapping")
    @MainActor
    func malformedIPCMessagesAreFallible() async throws {
        let router = CommandRouter()
        router.register("wire.test") { (payload: WirePayload) in WireResponse(value: payload.count) }

        let malformed = try decodeEnvelope(await router.response(for: NSNull()))
        #expect(malformed["ok"] as? Bool == false)
        #expect(errorCode(malformed) == "malformedMessage")

        let missingPayload = try decodeEnvelope(await router.response(for: ["command": "wire.test"]))
        #expect(errorCode(missingPayload) == "badPayload")

        let nullPayload = try decodeEnvelope(await router.response(for: [
            "command": "wire.test",
            "payload": NSNull(),
        ]))
        #expect(errorCode(nullPayload) == "badPayload")

        let wrongNumber = try decodeEnvelope(await router.response(for: [
            "command": "wire.test",
            "payload": ["count": "one"],
        ]))
        #expect(errorCode(wrongNumber) == "badPayload")

        let unknown = try decodeEnvelope(await router.response(for: ["command": "WIRE.TEST"]))
        #expect(errorCode(unknown) == "unknownCommand")

        let valid = try decodeEnvelope(await router.response(for: [
            "command": "wire.test",
            "payload": ["count": 3],
        ]))
        #expect(valid["ok"] as? Bool == true)
        #expect((valid["data"] as? [String: Any])?["value"] as? Int == 3)
    }

    @Test("the resource resolver rejects foreign origins, traversal and escaping symlinks")
    @MainActor
    func schemeResourcesStayInsideTheWebRoot() throws {
        let temp = FileManager.default.temporaryDirectory
            .appending(path: "KyttoSchemeTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let root = temp.appending(path: "web", directoryHint: .isDirectory)
        let outside = temp.appending(path: "outside", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        try Data("ok".utf8).write(to: root.appending(path: "index.html"))
        try Data("secret".utf8).write(to: outside.appending(path: "secret.txt"))
        try FileManager.default.createSymbolicLink(
            at: root.appending(path: "escape"),
            withDestinationURL: outside
        )
        defer { try? FileManager.default.removeItem(at: temp) }

        let valid = try #require(URL(string: "kytto://app/index.html"))
        #expect(KyttoSchemeHandler.resolvedResourceURL(root: root, requestURL: valid)?.lastPathComponent == "index.html")

        let foreign = try #require(URL(string: "https://example.invalid/index.html"))
        #expect(KyttoSchemeHandler.resolvedResourceURL(root: root, requestURL: foreign) == nil)

        let wrongHost = try #require(URL(string: "kytto://other/index.html"))
        #expect(KyttoSchemeHandler.resolvedResourceURL(root: root, requestURL: wrongHost) == nil)

        let symlinkEscape = try #require(URL(string: "kytto://app/escape/secret.txt"))
        #expect(KyttoSchemeHandler.resolvedResourceURL(root: root, requestURL: symlinkEscape) == nil)
    }

    @Test("an isolated model reads an empty home without creating app data")
    @MainActor
    func isolatedModelStartsClean() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        #expect(model.settings == .default)
        #expect(model.current().servers.isEmpty)
        #expect(!FileManager.default.fileExists(
            atPath: home.appending(path: "Library/Application Support/Kytto").path
        ))
    }

    @Test("applying a profile makes one client match it through safe toggles")
    @MainActor
    func applyingProfileUsesTogglePipeline() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"""
        {
          "theme": "dark",
          "mcpServers": {
            "alpha": { "command": "alpha" },
            "beta": { "command": "beta" },
            "gamma": { "command": "gamma" }
          }
        }
        """#.utf8).write(to: config)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        let profile = try model.createProfile(name: "Focused", serverIDs: ["alpha"])
        let result = try model.applyProfile(id: profile.id, to: .cursor)

        #expect(result.enabledCount == 0)
        #expect(result.disabledCount == 2)
        #expect(result.failures.isEmpty)
        #expect(model.pendingRestarts[.cursor] == 2)
        #expect(model.backups.list(clientID: .cursor).count == 2)

        let servers = model.current().servers
        #expect(servers.first { $0.id == "alpha" }?.enabledIn[.cursor] == .enabled)
        #expect(servers.first { $0.id == "beta" }?.enabledIn[.cursor] == .disabled)
        #expect(servers.first { $0.id == "gamma" }?.enabledIn[.cursor] == .disabled)

        let written = try String(contentsOf: config, encoding: .utf8)
        #expect(written.contains(#""theme": "dark""#))
        #expect(written.contains(#""alpha""#))
        #expect(!written.contains(#""beta""#))
        #expect(!written.contains(#""gamma""#))
    }

    @Test("pending restarts follow net configuration state through undo and restore")
    @MainActor
    func pendingRestartsAreNetState() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"{"mcpServers":{"alpha":{"command":"alpha"},"beta":{"command":"beta"}}}"#.utf8)
            .write(to: config)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        _ = model.current()

        try model.setEnabled(false, serverID: "alpha", clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == 1)
        try model.setEnabled(false, serverID: "beta", clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == 2)

        // Undoing one of two net changes leaves exactly one change pending.
        try model.setEnabled(true, serverID: "alpha", clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == 1)

        // Undoing the second returns the configuration to the applied state.
        try model.setEnabled(true, serverID: "beta", clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == nil)

        let offResult = try model.setEnabled(false, serverID: "alpha", clientID: .cursor)
        let backupID = try #require(offResult.backupID)
        #expect(model.pendingRestarts[.cursor] == 1)
        try model.restore(backupID: backupID, clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == nil)
    }

    @Test("dismissing a restart notice does not mark the client restarted")
    @MainActor
    func restartDismissalIsPresentationOnly() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"{"mcpServers":{"alpha":{"command":"alpha"}}}"#.utf8).write(to: config)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        _ = model.current()
        try model.setEnabled(false, serverID: "alpha", clientID: .cursor)
        model.dismissRestart(clientID: .cursor)

        #expect(model.pendingRestarts[.cursor] == 1)
        #expect(model.dismissedRestartClients.contains(.cursor))

        model.acknowledgeRestart(clientID: .cursor)
        #expect(model.pendingRestarts[.cursor] == nil)
        #expect(!model.dismissedRestartClients.contains(.cursor))
    }

    /// The menu bar shows this plan in an alert and then writes (§7.9). If the
    /// two ever disagree, the user approved something other than what happened,
    /// which is why both read the same computation.
    @Test("the profile preview describes exactly what applying it does")
    @MainActor
    func profilePlanMatchesTheWrite() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"""
        {
          "mcpServers": {
            "alpha": { "command": "alpha" },
            "beta": { "command": "beta" },
            "gamma": { "command": "gamma" }
          }
        }
        """#.utf8).write(to: config)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        // "delta" is in the profile but in no client, which is the case the
        // preview has to admit to rather than quietly drop.
        let profile = try model.createProfile(name: "Focused", serverIDs: ["alpha", "delta"])

        let plan = try model.profileApplyPlan(id: profile.id, to: .cursor)
        #expect(plan.profileName == "Focused")
        #expect(plan.clientName == "Cursor")
        #expect(plan.toEnable.isEmpty, "alpha is already on")
        #expect(plan.toDisable == ["beta", "gamma"])
        #expect(plan.missing == ["delta"])
        #expect(!plan.changesNothing)
        // Nothing was measured, so the stated cost is a floor and says so.
        #expect(plan.estimatedTokens == 0)
        #expect(plan.unmeasured == 1)

        let result = try model.applyProfile(id: profile.id, to: .cursor)
        #expect(result.disabledCount == plan.toDisable.count)
        #expect(result.enabledCount == plan.toEnable.count)
        #expect(result.failures.map(\.serverName) == plan.missing)

        // Applied twice over, the preview has to stop promising changes.
        let after = try model.profileApplyPlan(id: profile.id, to: .cursor)
        #expect(after.changesNothing)
        #expect(after.toDisable.isEmpty)
    }

    @Test("check all records every result and reloads discovery only once")
    @MainActor
    func checkAllBatchesDiscovery() async throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        let config = home.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"""
        {
          "mcpServers": {
            "alpha": { "command": "/usr/bin/false" },
            "beta": { "command": "/usr/bin/false" }
          }
        }
        """#.utf8).write(to: config)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        _ = model.current()
        var reloads = 0
        var progress: [String] = []
        model.onStateChanged = { reloads += 1 }

        try await model.checkAllHealth { name, index, total in
            progress.append("\(index)/\(total):\(name)")
        }

        #expect(progress == ["1/2:alpha", "2/2:beta"])
        #expect(reloads == 1)
        #expect(model.current().servers.allSatisfy { $0.health?.status == .failed })
    }

    @Test("ordinary settings avoid discovery while path changes publish fresh state")
    @MainActor
    func settingsReloadOnlyWhenDiscoveryChanges() throws {
        let home = FileManager.default.temporaryDirectory
            .appending(path: "KyttoMCPTests-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: home) }

        let model = AppModel(home: home)
        _ = model.current()
        var reloads = 0
        var publishedStateFlags: [Bool] = []
        model.onStateChanged = { reloads += 1 }
        model.onSettingsChanged = { _, stateChanged in
            publishedStateFlags.append(stateChanged)
        }

        try model.updateSettings { $0.theme = .light }
        #expect(reloads == 0)
        #expect(publishedStateFlags == [false])

        try model.updateSettings {
            $0.clientPathOverrides[ClientID.cursor.rawValue] = home.appending(path: "moved.json").path
        }
        #expect(reloads == 1)
        #expect(publishedStateFlags == [false, true])
    }
}

private struct WirePayload: Decodable {
    let count: Int
}

private struct WireResponse: Encodable {
    let value: Int
}

private func decodeEnvelope(_ value: String) throws -> [String: Any] {
    try #require(
        JSONSerialization.jsonObject(with: Data(value.utf8)) as? [String: Any]
    )
}

private func errorCode(_ envelope: [String: Any]) -> String? {
    (envelope["error"] as? [String: Any])?["code"] as? String
}
