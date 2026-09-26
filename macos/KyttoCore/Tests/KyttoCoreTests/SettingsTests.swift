import Darwin
import Foundation
import Testing
@testable import KyttoCore

@Suite("Settings")
struct SettingsTests {

    @Test("defaults survive a round trip")
    func roundTrip() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = SettingsStore(paths: KyttoPaths(home: home.root))

        #expect(store.current == .default)
        #expect(store.current.theme == .dark)

        try store.update {
            $0.theme = .dark
            $0.tokenWarningThreshold = 15_000
            $0.menuBarClient = .cursor
            $0.hasConfirmedMatrixWrites = true
            $0.customConfigSources = [
                CustomConfigSource(
                    displayName: "Work profile",
                    path: "/tmp/work-mcp.json",
                    scope: .profile,
                    scopeLabel: "Work"
                )
            ]
        }

        let reloaded = SettingsStore(paths: KyttoPaths(home: home.root))
        #expect(reloaded.current.theme == .dark)
        #expect(reloaded.current.tokenWarningThreshold == 15_000)
        #expect(reloaded.current.menuBarClient == .cursor)
        #expect(reloaded.current.hasConfirmedMatrixWrites)
        #expect(reloaded.current.customConfigSources.first?.scope == .profile)
        #expect(reloaded.current.customConfigSources.first?.scopeLabel == "Work")
    }

    @Test("nothing is written until something changes")
    func lazyWrite() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        _ = SettingsStore(paths: paths).current

        // §6.6: a launch that changes nothing leaves no trace.
        #expect(!FileManager.default.fileExists(atPath: paths.root.appending(path: "settings.json").path))
    }

    @Test("saving an unchanged value does not replace the settings file")
    func unchangedWrite() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let file = paths.root.appending(path: "settings.json")
        let store = SettingsStore(paths: paths)
        try store.update { $0.theme = .light }

        var before = stat()
        #expect(stat(file.path, &before) == 0)
        try store.update { $0.theme = .light }
        var after = stat()
        #expect(stat(file.path, &after) == 0)

        #expect(before.st_ino == after.st_ino)
    }

    @Test("a hand-edited file cannot produce nonsense")
    func sanitizing() {
        var settings = KyttoSettings.default
        settings.backupRetention = -5
        settings.tokenWarningThreshold = 1
        #expect(settings.sanitized().backupRetention == 1)
        #expect(settings.sanitized().tokenWarningThreshold == 1_000)
    }

    @Test("a relative path override is dropped rather than resolved somewhere surprising")
    func rejectsRelativeOverride() {
        var settings = KyttoSettings.default
        settings.clientPathOverrides = ["cursor": "mcp.json", "vsCode": "/tmp/mcp.json"]
        #expect(settings.sanitized().clientPathOverrides == ["vsCode": "/tmp/mcp.json"])
    }

    @Test("invalid and duplicate custom sources are discarded safely")
    func sanitizesCustomSources() {
        let id = UUID()
        var settings = KyttoSettings.default
        settings.customConfigSources = [
            CustomConfigSource(id: id, displayName: "Valid", path: "/tmp/mcp.json"),
            CustomConfigSource(id: id, displayName: "Duplicate", path: "/tmp/other.json"),
            CustomConfigSource(displayName: "Relative", path: "project/mcp.json"),
            CustomConfigSource(displayName: "   ", path: "/tmp/blank.json"),
        ]

        let sources = settings.sanitized().customConfigSources
        #expect(sources.count == 1)
        #expect(sources.first?.displayName == "Valid")
    }

    @Test("older settings files retain their values and opt out of diagnostics")
    func olderSettingsDecode() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let paths = KyttoPaths(home: home.root)
        let file = paths.root.appending(path: "settings.json")
        try AtomicWriter.write(#"""
        {
          "backupRetention" : 35,
          "clientPathOverrides" : {},
          "hasCompletedOnboarding" : true,
          "launchAtLogin" : false,
          "menuBarEnabled" : true,
          "theme" : "light",
          "tokenWarningThreshold" : 18000
        }
        """#, to: file)

        let settings = SettingsStore(paths: paths).current
        #expect(settings.theme == .light)
        #expect(settings.backupRetention == 35)
        #expect(settings.hasCompletedOnboarding)
        #expect(!settings.hasConfirmedMatrixWrites)
    }
}

@Suite("Client path overrides")
struct PathOverrideTests {

    @Test("discovery reads the overridden file")
    func discoveryHonoursOverride() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        // Cursor's config, somewhere Cursor would never put it.
        try home.write(Fixture.cursorMCP.text, to: "elsewhere/moved-mcp.json")

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(bundleIdentifiers: ["com.todesktop.230313mzl4w4u92"]),
            pathOverrides: ["cursor": home.root.appending(path: "elsewhere/moved-mcp.json").path]
        ).run()

        #expect(result.servers.map(\.name).sorted() == ["figma", "github"])
        let cursor = try #require(result.clients.first { $0.id == .cursor })
        #expect(cursor.configPathDisplay.hasSuffix("moved-mcp.json"), "the UI should show where it really looked")
    }

    @Test("writes go to the overridden file, not the default one")
    func writesHonourOverride() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.cursorMCP.text, to: "elsewhere/moved-mcp.json")

        let overrides = ["cursor": home.root.appending(path: "elsewhere/moved-mcp.json").path]
        let paths = KyttoPaths(home: home.root)
        let ledger = DigestLedger()
        let backups = BackupStore(paths: paths)
        let toggles = ToggleService(
            home: home.root,
            writer: ConfigWriter(backups: backups),
            parkStore: ParkStore(paths: paths),
            ledger: ledger,
            pathOverrides: overrides
        )

        func discover() -> DiscoveryResult {
            Discovery(
                home: home.root,
                locator: StubAppLocator(bundleIdentifiers: ["com.todesktop.230313mzl4w4u92"]),
                ledger: ledger,
                pathOverrides: overrides
            ).run()
        }

        let figma = try #require(discover().servers.first { $0.name == "figma" })
        _ = try toggles.setEnabled(false, server: figma, in: .cursor)

        let moved = try JSONDocument.parse(
            String(contentsOf: home.root.appending(path: "elsewhere/moved-mcp.json"), encoding: .utf8)
        )
        #expect(moved.value(at: ["mcpServers"])?.keys == ["github"])
        // And the default location was never created.
        #expect(!FileManager.default.fileExists(atPath: home.root.appending(path: ".cursor/mcp.json").path))
    }

    @Test("an override only moves the server map, not the files read beside it")
    func denyListKeepsItsPlace() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.claudeCode.text, to: "elsewhere/claude.json")
        try home.write(Fixture.claudeCodeSettings.text, to: ".claude/settings.json")

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(executables: ["claude"]),
            pathOverrides: ["claudeCode": home.root.appending(path: "elsewhere/claude.json").path]
        ).run()

        // The deny list is still found where it actually lives.
        let xcode = try #require(result.servers.first { $0.name == "XcodeBuildMCP" })
        #expect(xcode.enabledIn[.claudeCode] == .disabled)
    }
}
