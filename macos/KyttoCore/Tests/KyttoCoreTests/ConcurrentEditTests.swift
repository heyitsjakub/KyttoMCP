import Foundation
import Testing
@testable import KyttoCore

private enum InjectedFailure: Error { case injected }

/// A credential store that lets a test run something at a chosen moment of a
/// rotation, then fail.
private final class ScriptedStore: SecretStoring, @unchecked Sendable {
    let backing = InMemorySecretStore()
    var onStore: (() throws -> Void)?
    var onDelete: (() throws -> Void)?
    func store(_ value: String, for id: String) throws {
        if let onStore { try onStore() }
        try backing.store(value, for: id)
    }
    func value(for id: String) throws -> String? { try backing.value(for: id) }
    func delete(for id: String) throws {
        if let onDelete { try onDelete() }
        try backing.delete(for: id)
    }
    func storedIdentifiers() -> Set<String> { backing.storedIdentifiers() }
}

/// AUD-02: a save made by another program while Kytto is writing must be
/// refused, not overwritten.
@Suite("ConfigWriter — edits made during a write")
struct ConfigWriterConcurrentEditTests {
    @Test("a save during the transform is refused and survives")
    func saveDuringTransform() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let original = #"{"mcpServers":{},"other":"before"}"#
        let external = #"{"mcpServers":{},"other":"external-save"}"#
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        try h.home.write(original, to: ".cursor/mcp.json")

        #expect(throws: ConfigWriteError.changedOnDisk(pathDisplay: ".cursor/mcp.json")) {
            try ConfigWriter(backups: h.backups).edit(
                url: url, clientID: .cursor, pathDisplay: ".cursor/mcp.json",
                expecting: ConfigWriter.digest(of: original)
            ) { document in
                // External programs do not take MutationCoordinator's lock.
                try Data(external.utf8).write(to: url)
                return try document.settingMember("kytto", at: [], to: "true")
            }
        }
        #expect(try String(contentsOf: url, encoding: .utf8) == external)
    }

    @Test("a file created during the write of a new one is not replaced")
    func createdDuringTransform() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let created = #"{"mcpServers":{"theirs":{"command":"x"}}}"#

        #expect(throws: ConfigWriteError.changedOnDisk(pathDisplay: ".cursor/mcp.json")) {
            try ConfigWriter(backups: h.backups).edit(
                url: url, clientID: .cursor, pathDisplay: ".cursor/mcp.json", expecting: nil
            ) { document in
                try Data(created.utf8).write(to: url)
                return try document.settingMember("kytto", at: [], to: "true")
            }
        }
        #expect(try String(contentsOf: url, encoding: .utf8) == created)
    }

    @Test("the backup holds the bytes the edit started from")
    func backupMatchesSnapshot() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let original = #"{"mcpServers":{},"other":"before"}"#
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        try h.home.write(original, to: ".cursor/mcp.json")
        let receipt = try ConfigWriter(backups: h.backups).edit(
            url: url, clientID: .cursor, pathDisplay: ".cursor/mcp.json",
            expecting: ConfigWriter.digest(of: original)
        ) { try $0.settingMember("kytto", at: [], to: "true") }
        let backupID = try #require(receipt.backupID)
        let backup = try #require(h.backups.backup(id: backupID, clientID: .cursor))
        #expect(try h.backups.contents(of: backup) == original)
        #expect(receipt.digest == ConfigWriter.digest(of: url))
    }

    @Test("an exclusive write never replaces a file that appeared")
    func atomicWriterAbsent() throws {
        let directory = URL(filePath: NSTemporaryDirectory()).appending(path: "kytto-absent-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appending(path: "config.json")
        try AtomicWriter.write(Data("first".utf8), to: url, expecting: .absent)
        #expect(throws: AtomicWriter.WriteError.targetChanged) {
            try AtomicWriter.write(Data("second".utf8), to: url, expecting: .absent)
        }
        #expect(throws: AtomicWriter.WriteError.targetChanged) {
            try AtomicWriter.write(Data("second".utf8), to: url, expecting: .digest(ConfigWriter.digest(of: "other")))
        }
        #expect(try String(contentsOf: url, encoding: .utf8) == "first")
        // No staged file is left behind by a refusal.
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path) == ["config.json"])
    }
}

/// AUD-03 and AUD-06: undoing a failed rotation must not erase newer work, and
/// must not delete a credential it never changed.
@Suite("Secret rotation — rollback after a failure")
struct RotationRollbackTests {
    private let original = #"{"mcpServers":{"svc":{"command":"node","env":{"API_KEY":"old-value"}}}}"#

    @Test("a save made after the rotation wrote the file survives the rollback")
    func externalSaveSurvives() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        let external = #"{"mcpServers":{"svc":{"command":"node","env":{"API_KEY":"new-value"}}},"editorNote":"must-survive"}"#
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let url = h.home.root.appending(path: ".cursor/mcp.json")
        let store = ScriptedStore()
        store.onStore = {
            try Data(external.utf8).write(to: url)
            throw InjectedFailure.injected
        }
        let service = h.secrets(store: store)
        let id = try #require(service.scan(servers: servers).first?.id)

        do {
            _ = try service.rotate(secretID: id, to: "new-value", servers: servers, alsoStoreInSecretStore: true)
            Issue.record("the rotation should have failed")
        } catch SecretsError.rollbackFailed(let paths, let backupIDs) {
            #expect(paths.count == 1)
            #expect(paths.first?.contains("changed by another app") == true)
            // The backup of the original is the way back, and the error says so.
            #expect(backupIDs.count == 1)
        }
        #expect(try h.text(".cursor/mcp.json") == external)
    }

    @Test("a rollback with nothing in the way still restores the file")
    func cleanRollback() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let store = ScriptedStore()
        store.onStore = { throw InjectedFailure.injected }
        let service = h.secrets(store: store)
        let id = try #require(service.scan(servers: servers).first?.id)
        #expect(throws: InjectedFailure.self) {
            try service.rotate(secretID: id, to: "new-value", servers: servers, alsoStoreInSecretStore: true)
        }
        #expect(try h.text(".cursor/mcp.json") == original)
    }

    @Test("a failure before any credential change leaves the store untouched")
    func savedCredentialSurvivesConflict() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let store = InMemorySecretStore()
        let savedID = SecretsService.identifier(key: "API_KEY", value: "already-saved-value")
        try store.store("already-saved-value", for: savedID)
        let service = h.secrets(store: store)
        let id = try #require(service.scan(servers: servers).first?.id)
        // A digest conflict before the rotation changed anything.
        try h.home.write(original + "\n", to: ".cursor/mcp.json")
        for alsoStore in [false, true] {
            #expect(throws: ConfigWriteError.self) {
                try service.rotate(secretID: id, to: "already-saved-value", servers: servers, alsoStoreInSecretStore: alsoStore)
            }
            #expect(try store.value(for: savedID) == "already-saved-value")
        }
    }

    @Test("a failure after the new credential was saved restores what that id held before")
    func previousDestinationValueRestored() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        try h.home.write(original, to: ".cursor/mcp.json")
        let servers = h.discover().servers
        let store = ScriptedStore()
        let newID = SecretsService.identifier(key: "API_KEY", value: "new-value")
        try store.backing.store("new-value", for: newID)
        let oldID = SecretsService.identifier(key: "API_KEY", value: "old-value")
        try store.backing.store("old-value", for: oldID)
        store.onDelete = { throw InjectedFailure.injected }
        let service = h.secrets(store: store)
        let id = try #require(service.scan(servers: servers).first?.id)
        #expect(throws: InjectedFailure.self) {
            try service.rotate(secretID: id, to: "new-value", servers: servers, alsoStoreInSecretStore: true)
        }
        #expect(try store.value(for: newID) == "new-value")
        #expect(try store.value(for: oldID) == "old-value")
        #expect(try h.text(".cursor/mcp.json") == original)
    }
}
