import Foundation
import Testing
@testable import KyttoCore

extension ToggleHarness {
    func secrets(store: any SecretStoring = InMemorySecretStore()) -> SecretsService {
        SecretsService(
            home: home.root,
            writer: ConfigWriter(backups: backups),
            ledger: ledger,
            store: store
        )
    }
}

@Suite("SecretsService — heuristics")
struct SecretHeuristicTests {

    @Test("names that mean a secret", arguments: [
        "GITHUB_PERSONAL_ACCESS_TOKEN", "API_KEY", "CLIENT_SECRET",
        "DB_PASSWORD", "AUTH_HEADER", "SESSION_ID", "PRIVATE_KEY",
    ])
    func sensitiveNames(_ key: String) {
        #expect(SecretsService.looksSensitive(key: key, value: "short"))
    }

    @Test("names that point at a secret rather than being one", arguments: [
        "SSH_KEY_PATH", "CREDENTIAL_FILE", "TOKEN_DIR", "AUTH_URL", "API_KEY_ENABLED",
    ])
    func pointerNames(_ key: String) {
        #expect(!SecretsService.looksSensitive(key: key, value: "short"))
    }

    @Test("an opaque blob is flagged whatever it is called")
    func opaqueValue() {
        #expect(SecretsService.looksSensitive(key: "SOMETHING", value: "a1b2c3d4e5f6g7h8i9j0k1l2m3"))
        // Ordinary settings are not.
        #expect(!SecretsService.looksSensitive(key: "WORKFLOWS", value: "simulator,ui-automation"))
        #expect(!SecretsService.looksSensitive(key: "SENTRY_DISABLED", value: "true"))
    }

    @Test("masking is opaque and reveals no real prefix or suffix")
    func masking() {
        #expect(SecretsService.mask("ghp_abcdefghijklmnop") == "••••••••••••")
        #expect(SecretsService.mask("short") == "••••••••••••")
        #expect(!SecretsService.mask("ghp_abcdefghijklmnop").contains("ghp_"))
        #expect(!SecretsService.mask("ghp_abcdefghijklmnop").contains("mnop"))
    }

    @Test("the identifier changes when the value does")
    func identifierTracksValue() {
        let a = SecretsService.identifier(key: "TOKEN", value: "one")
        let b = SecretsService.identifier(key: "TOKEN", value: "two")
        #expect(a != b)
        #expect(a == SecretsService.identifier(key: "TOKEN", value: "one"), "must be stable across launches")
    }
}

@Suite("SecretsService — inventory")
struct SecretInventoryTests {

    /// The same token in two clients, plus one that only Cursor has.
    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(
            #"""
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_sharedtokenvalue123" }
                },
                "figma": {
                  "command": "npx",
                  "env": { "FIGMA_API_KEY": "figd_cursoronlyvalue456" }
                }
              }
            }
            """#,
            to: ".cursor/mcp.json"
        )
        try harness.home.write(
            #"""
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "env": {
                    "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_sharedtokenvalue123",
                    "GITHUB_TOOLSETS": "repos,issues"
                  }
                }
              }
            }
            """#,
            to: ".claude.json"
        )
        return harness
    }

    @Test("the same value in two clients is one secret with two usages")
    func groupsAcrossClients() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let records = harness.secrets().scan(servers: harness.discover().servers)
        let token = try #require(records.first { $0.key == "GITHUB_PERSONAL_ACCESS_TOKEN" })

        #expect(token.isShared)
        #expect(Set(token.usages.map(\.clientID)) == [.cursor, .claudeCode])
        #expect(token.maskedValue == "••••••••••••")
    }

    @Test("values are never carried in the record")
    func neverCarriesValues() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        for record in harness.secrets().scan(servers: harness.discover().servers) {
            #expect(!record.maskedValue.contains("sharedtokenvalue"))
            #expect(!record.maskedValue.contains("cursoronlyvalue"))
        }
    }

    @Test("ordinary settings are left out of the inventory")
    func skipsNonSecrets() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let records = harness.secrets().scan(servers: harness.discover().servers)
        #expect(!records.contains { $0.key == "GITHUB_TOOLSETS" })
        #expect(records.count == 2)
    }

    @Test("the same name with different values is two secrets, not one")
    func detectsDrift() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "value-one-aaaa"}}}}"#,
            to: ".cursor/mcp.json"
        )
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "value-two-bbbb"}}}}"#,
            to: ".claude.json"
        )

        let records = harness.secrets().scan(servers: harness.discover().servers)
        // Two rows for one name is exactly the drift worth seeing.
        #expect(records.filter { $0.key == "API_KEY" }.count == 2)
        #expect(records.allSatisfy { !$0.isShared })
    }

    @Test("revealing requires asking, and returns the real value")
    func reveal() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let servers = harness.discover().servers
        let service = harness.secrets()
        let token = try #require(service.scan(servers: servers).first { $0.key == "GITHUB_PERSONAL_ACCESS_TOKEN" })

        #expect(service.reveal(secretID: token.id, servers: servers) == "ghp_sharedtokenvalue123")
    }
}

@Suite("SecretsService — rotation")
struct SecretRotationTests {

    private func makeHarness() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        try harness.home.write(
            #"""
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "env": { "API_KEY": "old-value-aaaaaaaaaa" }
                }
              },
              "theme": "dark"
            }
            """#,
            to: ".cursor/mcp.json"
        )
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "old-value-aaaaaaaaaa"}}}, "numStartups": 12}"#,
            to: ".claude.json"
        )
        return harness
    }

    @Test("rotating writes the new value into every file that had the old one")
    func rotatesEverywhere() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)

        let result = try service.rotate(
            secretID: secret.id,
            to: "new-value-bbbbbbbbbb",
            servers: servers,
            alsoStoreInSecretStore: false
        )
        #expect(result.updated.count == 2)

        for path in [".cursor/mcp.json", ".claude.json"] {
            let document = try JSONDocument.parse(harness.text(path))
            #expect(
                document.value(at: ["mcpServers", "github", "env", "API_KEY"])?.stringValue == "new-value-bbbbbbbbbb",
                "\(path) still has the old value"
            )
        }
    }

    @Test("rotating to the existing value is a write-free no-op")
    func sameValueIsNoOp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)
        let cursorBefore = try Data(contentsOf: harness.home.root.appending(path: ".cursor/mcp.json"))

        let result = try service.rotate(
            secretID: secret.id,
            to: "old-value-aaaaaaaaaa",
            servers: servers,
            alsoStoreInSecretStore: false
        )

        #expect(result.updated.isEmpty)
        #expect(result.backupIDs.isEmpty)
        #expect(try Data(contentsOf: harness.home.root.appending(path: ".cursor/mcp.json")) == cursorBefore)
    }

    @Test("a failure in a later client rolls earlier secret writes back byte for byte")
    func failureRollsBackEveryFile() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)
        let claudeURL = harness.home.root.appending(path: ".claude.json")
        let cursorURL = harness.home.root.appending(path: ".cursor/mcp.json")
        let claudeBefore = try Data(contentsOf: claudeURL)

        // claudeCode sorts before cursor, so this drift is discovered after one
        // successful write and exercises the rollback path.
        try Data(#"{"changed":"outside Kytto"}"#.utf8).write(to: cursorURL)

        #expect(throws: ConfigWriteError.self) {
            _ = try service.rotate(
                secretID: secret.id,
                to: "new-value-bbbbbbbbbb",
                servers: servers,
                alsoStoreInSecretStore: false
            )
        }
        #expect(try Data(contentsOf: claudeURL) == claudeBefore)
    }

    @Test("rotation updates a secret inside an inline Codex table")
    func rotatesInlineTOML() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"""
            [mcp_servers]
            inline = { command = "tool", env = { API_KEY = "old-value-aaaaaaaaaa", KEEP = "yes" } }
            """#,
            to: ".codex/config.toml"
        )
        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)

        _ = try service.rotate(
            secretID: secret.id,
            to: "new-value-bbbbbbbbbb",
            servers: servers,
            alsoStoreInSecretStore: false
        )

        let written = try harness.text(".codex/config.toml")
        #expect(written.contains(#"API_KEY = "new-value-bbbbbbbbbb""#))
        #expect(written.contains(#"KEEP = "yes""#))
    }

    @Test("rotation leaves the rest of each file alone")
    func preservesRest() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)
        _ = try service.rotate(secretID: secret.id, to: "new-value-bbbbbbbbbb", servers: servers, alsoStoreInSecretStore: false)

        let cursor = try JSONDocument.parse(harness.text(".cursor/mcp.json"))
        #expect(cursor.value(at: ["theme"])?.stringValue == "dark")
        #expect(cursor.value(at: ["mcpServers", "github", "command"])?.stringValue == "npx")

        let claude = try JSONDocument.parse(harness.text(".claude.json"))
        #expect(claude.value(at: ["numStartups"])?.numberValue == 12)
    }

    @Test("every touched file is backed up first")
    func backsUp() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)
        let result = try service.rotate(secretID: secret.id, to: "new-value-bbbbbbbbbb", servers: servers, alsoStoreInSecretStore: false)

        #expect(result.backupIDs.count == 2)
        #expect(harness.backups.list(clientID: .cursor).count == 1)
        #expect(harness.backups.list(clientID: .claudeCode).count == 1)
    }

    @Test("after rotating, the inventory shows one secret again")
    func stillGroupedAfterRotation() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let service = harness.secrets()
        let secret = try #require(service.scan(servers: harness.discover().servers).first)
        _ = try service.rotate(
            secretID: secret.id,
            to: "new-value-bbbbbbbbbb",
            servers: harness.discover().servers,
            alsoStoreInSecretStore: false
        )

        let after = service.scan(servers: harness.discover().servers)
        #expect(after.count == 1)
        #expect(try #require(after.first).isShared, "the two files must still agree")
    }

    @Test("an empty value is refused")
    func refusesEmpty() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }
        let servers = harness.discover().servers
        let service = harness.secrets()
        let secret = try #require(service.scan(servers: servers).first)

        #expect(throws: SecretsError.emptyValue) {
            _ = try service.rotate(secretID: secret.id, to: "", servers: servers, alsoStoreInSecretStore: false)
        }
    }

    @Test("the keychain copy follows the value")
    func keychainFollowsValue() throws {
        let harness = try makeHarness()
        defer { harness.cleanUp() }

        let store = InMemorySecretStore()
        let service = harness.secrets(store: store)
        let servers = harness.discover().servers
        let secret = try #require(service.scan(servers: servers).first)

        try service.adopt(secretID: secret.id, servers: servers)
        #expect(try store.value(for: secret.id) == "old-value-aaaaaaaaaa")

        _ = try service.rotate(secretID: secret.id, to: "new-value-bbbbbbbbbb", servers: servers, alsoStoreInSecretStore: true)

        // The old id described a value that is nowhere any more.
        #expect(try store.value(for: secret.id) == nil)
        let newID = SecretsService.identifier(key: secret.key, value: "new-value-bbbbbbbbbb")
        #expect(try store.value(for: newID) == "new-value-bbbbbbbbbb")
    }
}

@Suite("SecretsService — exposure")
struct SecretExposureTests {

    @Test("a world-readable config holding a secret is called out, and can be tightened")
    func permissions() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "value-aaaaaaaaaaaa"}}}}"#,
            to: ".cursor/mcp.json"
        )
        let url = harness.home.root.appending(path: ".cursor/mcp.json")
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)

        let service = harness.secrets()
        let before = try #require(service.scan(servers: harness.discover().servers).first)
        #expect(before.exposure.contains { if case .readableByOthers = $0 { true } else { false } })

        try service.restrictPermissions(clientID: .cursor)

        let after = try #require(service.scan(servers: harness.discover().servers).first)
        #expect(!after.exposure.contains { if case .readableByOthers = $0 { true } else { false } })

        let mode = try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber
        #expect(mode?.uint16Value == 0o600)
    }

    @Test("a config inside a git working tree is called out")
    func gitRepository() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "value-aaaaaaaaaaaa"}}}}"#,
            to: ".cursor/mcp.json"
        )
        // A home directory kept in a dotfiles repository — unusual, but it is
        // exactly how a token gets committed by accident.
        try FileManager.default.createDirectory(
            at: harness.home.root.appending(path: ".git"),
            withIntermediateDirectories: true
        )

        let service = harness.secrets()
        let record = try #require(service.scan(servers: harness.discover().servers).first)
        #expect(record.exposure.contains { if case .insideGitRepository = $0 { true } else { false } })
    }

    @Test("a config with tight permissions and no repository is clean")
    func noExposure() throws {
        let harness = try ToggleHarness()
        defer { harness.cleanUp() }
        try harness.home.write(
            #"{"mcpServers": {"github": {"command": "npx", "env": {"API_KEY": "value-aaaaaaaaaaaa"}}}}"#,
            to: ".cursor/mcp.json"
        )
        try harness.secrets().restrictPermissions(clientID: .cursor)

        let record = try #require(harness.secrets().scan(servers: harness.discover().servers).first)
        #expect(record.exposure.isEmpty)
    }
}
