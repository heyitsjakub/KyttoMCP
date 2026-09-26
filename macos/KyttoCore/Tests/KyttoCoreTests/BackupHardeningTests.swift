import Foundation
import Testing
@testable import KyttoCore

// Issue #11 follow-up — docs/SECURITY_HARDENING_BACKUPS_2026-09-14.md.

private func mode(of url: URL) throws -> Int {
    let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
    return try #require(attributes[.posixPermissions] as? NSNumber).intValue & 0o7777
}

private func setMode(_ mode: Int, of url: URL) throws {
    try FileManager.default.setAttributes([.posixPermissions: mode], ofItemAtPath: url.path)
}

@Suite("Backups — owner-only storage")
struct BackupPermissionTests {

    @Test("a backup of a 0600 config is 0600, sidecar included, in 0700 folders")
    func backupIsOwnerOnly() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        let config = harness.home.root.appending(path: ".claude.json")
        try setMode(0o600, of: config)

        let created = try harness.backups.backUp(config, clientID: .claudeCode, pathDisplay: "~/.claude.json")
        let backup = try #require(created)

        #expect(try mode(of: backup.url) == 0o600)
        #expect(try mode(of: backup.url.appendingPathExtension("origin")) == 0o600)
        #expect(try mode(of: backup.url.deletingLastPathComponent()) == 0o700)
        #expect(try mode(of: harness.paths.backups) == 0o700)
        #expect(try mode(of: harness.paths.root) == 0o700)
    }

    @Test("folders another store created at the default mode are tightened by the next backup")
    func existingFoldersAreTightened() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        let clientDirectory = harness.paths.backups.appending(path: ClientID.cursor.rawValue)
        try FileManager.default.createDirectory(at: clientDirectory, withIntermediateDirectories: true)
        let folders = [harness.paths.root, harness.paths.backups, clientDirectory]
        for folder in folders { try setMode(0o755, of: folder) }

        let url = harness.home.root.appending(path: ".cursor/mcp.json")
        try harness.backups.backUp(url, clientID: .cursor, pathDisplay: "~/.cursor/mcp.json")

        for folder in folders {
            #expect(try mode(of: folder) == 0o700, "\(folder.lastPathComponent)")
        }
    }

    @Test("parked definitions are owner-only, including a store an earlier version wrote")
    func parkedDefinitionsAreOwnerOnly() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }

        try harness.parkStore.park(
            clientID: .cursor,
            serverName: "figma",
            sourceText: #"{"command": "npx", "env": {"FIGMA_TOKEN": "value-aaaaaaaaaaaa"}}"#
        )
        #expect(try mode(of: harness.paths.parked) == 0o600)
        #expect(try mode(of: harness.paths.root) == 0o700)

        try setMode(0o644, of: harness.paths.parked)
        try harness.parkStore.unpark(clientID: .cursor, serverName: "figma")
        #expect(try mode(of: harness.paths.parked) == 0o600, "rewriting an old store tightens it")
    }

    @Test("a write keeps the replaced file's mode unless it is given one")
    func atomicWriterModes() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let url = home.root.appending(path: "config.json")

        try home.write("{}", to: "config.json")
        try setMode(0o640, of: url)
        try AtomicWriter.write(#"{"a": 1}"#, to: url)
        #expect(try mode(of: url) == 0o640, "carried over exactly, not narrowed by the umask")

        try AtomicWriter.write(#"{"a": 2}"#, to: url, permissions: 0o600)
        #expect(try mode(of: url) == 0o600, "an explicit mode wins")
        #expect(try String(contentsOf: url, encoding: .utf8) == #"{"a": 2}"#)

        // A file that did not exist gets the same default as any other new file.
        let fresh = home.root.appending(path: "fresh.json")
        let reference = home.root.appending(path: "reference.json")
        try AtomicWriter.write("{}", to: fresh)
        try Data("{}".utf8).write(to: reference)
        #expect(try mode(of: fresh) == mode(of: reference))

        let leftovers = try FileManager.default.contentsOfDirectory(atPath: home.root.path)
            .filter { $0.contains(".kytto-") }
        #expect(leftovers.isEmpty, "no staged file outlives a write")
    }
}

@Suite("Backups — permissions left by earlier versions")
struct BackupPermissionMigrationTests {

    @Test("launch takes group and other access away, and a second launch changes nothing")
    func tightensLegacyStore() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)

        // What 1.0.5 left behind: 0755 folders and 0644 files.
        let clientDirectory = paths.backups.appending(path: "claudeCode")
        try FileManager.default.createDirectory(at: clientDirectory, withIntermediateDirectories: true)
        let backup = clientDirectory.appending(path: "settings.json.2026-07-31-113227-363.bak")
        let files = [backup, backup.appendingPathExtension("origin"), paths.parked]
        let folders = [paths.root, paths.backups, clientDirectory]
        for file in files {
            try Data("{}".utf8).write(to: file)
            try setMode(0o644, of: file)
        }
        for folder in folders { try setMode(0o755, of: folder) }

        #expect(paths.tightenPermissions() == files.count + folders.count)
        for file in files {
            #expect(try mode(of: file) == 0o600, "\(file.lastPathComponent)")
        }
        for folder in folders {
            #expect(try mode(of: folder) == 0o700, "\(folder.lastPathComponent)")
        }
        #expect(paths.tightenPermissions() == 0)
    }

    @Test("launch creates nothing when nothing has been stored (§6.6)")
    func createsNothing() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)

        #expect(paths.tightenPermissions() == 0)
        #expect(!FileManager.default.fileExists(atPath: paths.root.path))
    }

    @Test("a symbolic link in the backups folder is not followed out of it")
    func symbolicLinksAreNotFollowed() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        try home.write("not Kytto's", to: "outside.txt")
        let outside = home.root.appending(path: "outside.txt")
        try setMode(0o644, of: outside)
        try setMode(0o755, of: home.root)

        let clientDirectory = paths.backups.appending(path: "cursor")
        try FileManager.default.createDirectory(at: clientDirectory, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(
            at: clientDirectory.appending(path: "mcp.json.2026-07-31-113227-363.bak"),
            withDestinationURL: outside
        )
        try FileManager.default.createSymbolicLink(
            at: paths.backups.appending(path: "vsCode"),
            withDestinationURL: home.root
        )

        paths.tightenPermissions()

        #expect(try mode(of: outside) == 0o644)
        #expect(try mode(of: home.root) == 0o755)
    }
}

@Suite("Backups — restore targets")
struct BackupRestoreTargetTests {

    private func makeHarness() throws -> (ToggleHarness, Backup) {
        let harness = try ToggleHarness()
        try harness.home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")
        try harness.home.write(Fixture.claudeCode.text, to: ".claude.json")
        try harness.home.write("export PATH=/usr/bin\n", to: ".zshrc")
        let created = try harness.backups.backUp(
            harness.home.root.appending(path: ".cursor/mcp.json"),
            clientID: .cursor,
            pathDisplay: "~/.cursor/mcp.json"
        )
        return (harness, try #require(created))
    }

    /// Rewrites a sidecar the way anything with access to Kytto's folder could.
    private func pointing(_ backup: Backup, at path: String, in harness: ToggleHarness) throws -> Backup {
        try Data(path.utf8).write(to: backup.url.appendingPathExtension("origin"))
        return try #require(harness.backups.backup(id: backup.id, clientID: backup.clientID))
    }

    @Test("a sidecar edited to point elsewhere is refused, and nothing is written", arguments: [
        ".zshrc",
        ".cursor/../.zshrc",
        ".claude.json",
    ])
    func editedSidecarIsRefused(relativePath: String) throws {
        let (harness, original) = try makeHarness()
        defer { harness.cleanUp() }
        let untouched = [".zshrc", ".claude.json"]
        let before = try untouched.map(harness.text)
        let backup = try pointing(original, at: harness.home.root.path + "/" + relativePath, in: harness)

        #expect(throws: BackupError.unknownRestoreTarget(pathDisplay: backup.originalPathDisplay)) {
            try harness.backups.restore(backup, home: harness.home.root)
        }
        #expect(try untouched.map(harness.text) == before)
        #expect(harness.backups.list(clientID: .cursor).count == 1, "a refused restore backs nothing up")
    }

    @Test("a restore writes the registry's file, however the sidecar spells the path")
    func spellingCannotRedirect() throws {
        let (harness, original) = try makeHarness()
        defer { harness.cleanUp() }
        // `link/..` is the home folder on paper and `elsewhere` on disk.
        try harness.home.makeDirectory("elsewhere/deeper")
        try harness.home.makeDirectory("elsewhere/.cursor")
        try FileManager.default.createSymbolicLink(
            at: harness.home.root.appending(path: "link"),
            withDestinationURL: harness.home.root.appending(path: "elsewhere/deeper")
        )
        let backup = try pointing(original, at: harness.home.root.path + "/link/../.cursor/mcp.json", in: harness)

        // Refusing and restoring to ~/.cursor/mcp.json are both safe outcomes;
        // following the link is not.
        _ = try? harness.backups.restore(backup, home: harness.home.root)

        let redirected = harness.home.root.appending(path: "elsewhere/.cursor/mcp.json")
        #expect(!FileManager.default.fileExists(atPath: redirected.path))
        #expect(try harness.text(".cursor/mcp.json") == Fixture.cursorMCP.text)
    }

    @Test("every file the write services touch can be restored", arguments: [
        (ClientID.cursor, ".cursor/mcp.json"),
        (.claudeCode, ".claude.json"),
        (.claudeCode, ".claude/settings.json"),
        (.claudeDesktop, "Library/Application Support/Claude/claude_desktop_config.json"),
        (.claudeDesktop, "Library/Application Support/Claude/Claude Extensions Settings/ant.dir.test.osascript.json"),
        (.vsCode, "Library/Application Support/Code/User/mcp.json"),
        (.codex, ".codex/config.toml"),
    ])
    func legitimateTargetsRestore(clientID: ClientID, relativePath: String) throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write("original\n", to: relativePath)
        let url = harness.home.root.appending(path: relativePath)
        let created = try harness.backups.backUp(url, clientID: clientID, pathDisplay: relativePath)
        let backup = try #require(created)
        try harness.home.write("changed\n", to: relativePath)

        try harness.backups.restore(backup, home: harness.home.root)

        #expect(try harness.text(relativePath) == "original\n")
    }

    @Test("with an override, both the overridden file and the registry default can be restored")
    func overriddenAndDefaultLocations() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        let overrides = ["cursor": harness.home.root.appending(path: "elsewhere/mcp.json").path]

        for relativePath in ["elsewhere/mcp.json", ".cursor/mcp.json"] {
            try harness.home.write("original\n", to: relativePath)
            let created = try harness.backups.backUp(
                harness.home.root.appending(path: relativePath), clientID: .cursor, pathDisplay: relativePath
            )
            let backup = try #require(created)
            try harness.home.write("changed\n", to: relativePath)

            try harness.backups.restore(backup, home: harness.home.root, pathOverrides: overrides)

            #expect(try harness.text(relativePath) == "original\n", "\(relativePath)")
        }
    }

    @Test("a backup of a location Settings no longer points at is refused")
    func staleOverride() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write("original\n", to: "elsewhere/mcp.json")
        let created = try harness.backups.backUp(
            harness.home.root.appending(path: "elsewhere/mcp.json"),
            clientID: .cursor,
            pathDisplay: "elsewhere/mcp.json"
        )
        let backup = try #require(created)
        try harness.home.write("changed\n", to: "elsewhere/mcp.json")

        #expect(throws: BackupError.self) {
            try harness.backups.restore(backup, home: harness.home.root)
        }
        #expect(try harness.text("elsewhere/mcp.json") == "changed\n")
    }

    @Test("a backup filed under a read-only source is refused")
    func readOnlySource() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write("original\n", to: "mine/mcp.json")
        let url = harness.home.root.appending(path: "mine/mcp.json")
        let source = CustomConfigSource(displayName: "Mine", path: url.path)
        // Kytto never backs one up; this is what a hand-made folder would hold.
        let created = try harness.backups.backUp(url, clientID: source.clientID, pathDisplay: url.path)
        let backup = try #require(created)
        try harness.home.write("changed\n", to: "mine/mcp.json")

        #expect(throws: BackupError.self) {
            try harness.backups.restore(backup, home: harness.home.root)
        }
        #expect(try harness.text("mine/mcp.json") == "changed\n")
    }

    @Test("installed bundles, plugins and stray files beside extension settings are never targets")
    func installedSoftwareIsNotATarget() {
        let home = URL(filePath: "/Users/someone")
        let resolver = ClientPathResolver(home: home)
        func target(_ path: String, _ clientID: ClientID) -> URL? {
            resolver.writeTarget(matching: home.appending(path: path), for: ClientRegistry.descriptor(for: clientID))
        }
        let claude = "Library/Application Support/Claude"

        #expect(target("\(claude)/Claude Extensions/ant.dir.test/manifest.json", .claudeDesktop) == nil)
        #expect(target("\(claude)/Claude Extensions Settings/nested/ant.dir.test.json", .claudeDesktop) == nil)
        #expect(target("\(claude)/Claude Extensions Settings/notes.txt", .claudeDesktop) == nil)
        #expect(target("\(claude)/Claude Extensions Settings/../../../../.zshrc.json", .claudeDesktop) == nil)
        #expect(target(".codex/plugins/some-plugin/.mcp.json", .codex) == nil)
        #expect(target(".cursor/mcp.json", .vsCode) == nil)

        let custom = CustomConfigSource(displayName: "Mine", path: "/Users/someone/mine.json")
        #expect(resolver.writeTarget(
            matching: URL(filePath: custom.path),
            for: ClientRegistry.customDescriptor(custom)
        ) == nil)
    }
}
