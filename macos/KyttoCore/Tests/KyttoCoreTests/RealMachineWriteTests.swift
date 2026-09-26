import Foundation
import Testing
@testable import KyttoCore

/// Exercises the write path against this machine's actual configurations —
/// **on a copy**. The real files are read and never written.
///
///     KYTTO_REAL_HOME=1 swift test --filter RealMachineWrite
///
/// Fixtures are a guess at what files look like; this is the thing itself, with
/// whatever formatting, unusual keys and size the machine really has. Toggling
/// every server off and back on and demanding the bytes match is the strongest
/// statement available that M2 will not damage a config.
@Suite(
    "RealMachineWrite",
    .enabled(if: ProcessInfo.processInfo.environment["KYTTO_REAL_HOME"] == "1")
)
struct RealMachineWriteTests {

    /// Copies the real configs into a throwaway home.
    private func mirrorRealHome() throws -> ToggleHarness {
        let harness = try ToggleHarness()
        let real = FileManager.default.homeDirectoryForCurrentUser

        let relativePaths = [
            "Library/Application Support/Claude/claude_desktop_config.json",
            ".claude.json",
            ".claude/settings.json",
            ".cursor/mcp.json",
            "Library/Application Support/Code/User/mcp.json",
        ]
        for path in relativePaths {
            let source = real.appending(path: path)
            guard let text = try? String(contentsOf: source, encoding: .utf8) else { continue }
            try harness.home.write(text, to: path)
        }

        // Extensions: manifests and their settings files.
        let extensionsRoot = real.appending(path: "Library/Application Support/Claude/Claude Extensions")
        let settingsRoot = real.appending(path: "Library/Application Support/Claude/Claude Extensions Settings")
        if let entries = try? FileManager.default.contentsOfDirectory(
            at: extensionsRoot, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]
        ) {
            for entry in entries {
                let bundleID = entry.lastPathComponent
                if let manifest = try? String(contentsOf: entry.appending(path: "manifest.json"), encoding: .utf8) {
                    try harness.home.write(
                        manifest,
                        to: "Library/Application Support/Claude/Claude Extensions/\(bundleID)/manifest.json"
                    )
                }
                if let settings = try? String(
                    contentsOf: settingsRoot.appending(path: "\(bundleID).json"), encoding: .utf8
                ) {
                    try harness.home.write(
                        settings,
                        to: "Library/Application Support/Claude/Claude Extensions Settings/\(bundleID).json"
                    )
                }
            }
        }
        return harness
    }

    private func fileTree(of root: URL) throws -> [String: String] {
        var result: [String: String] = [:]
        let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)
        while let url = enumerator?.nextObject() as? URL {
            guard let text = try? String(contentsOf: url, encoding: .utf8) else { continue }
            result[url.path.replacingOccurrences(of: root.path, with: "")] = text
        }
        return result
    }

    @Test("toggling every real server off and back on restores every file exactly")
    func fullRoundTrip() throws {
        let harness = try mirrorRealHome()
        defer { harness.cleanUp() }

        let discovered = harness.discover()
        try #require(!discovered.servers.isEmpty, "nothing to test against on this machine")

        // Snapshot the mirrored configs before anything is written. Kytto's own
        // directory is excluded — backups and the park store are supposed to grow.
        let configRoots = [
            harness.home.root.appending(path: "Library/Application Support/Claude"),
            harness.home.root.appending(path: "Library/Application Support/Code"),
            harness.home.root.appending(path: ".claude"),
            harness.home.root.appending(path: ".cursor"),
        ]
        var before: [String: String] = [:]
        for root in configRoots {
            before.merge(try fileTree(of: root)) { current, _ in current }
        }
        if let claudeJSON = try? String(contentsOf: harness.home.root.appending(path: ".claude.json"), encoding: .utf8) {
            before[".claude.json"] = claudeJSON
        }

        var toggled = 0
        for server in discovered.servers {
            for (clientID, enablement) in server.enabledIn where enablement != .absent {
                // Off, then back on.
                let fresh = try #require(harness.discover().servers.first { $0.id == server.id })
                let wasEnabled = fresh.enabledIn[clientID] == .enabled

                _ = try harness.toggles.setEnabled(!wasEnabled, server: fresh, in: clientID)
                let flipped = try #require(harness.discover().servers.first { $0.id == server.id })
                #expect(
                    flipped.enabledIn[clientID] == (wasEnabled ? .disabled : .enabled),
                    "\(server.name) in \(clientID.rawValue) did not change state"
                )

                _ = try harness.toggles.setEnabled(wasEnabled, server: flipped, in: clientID)
                toggled += 1
            }
        }

        var after: [String: String] = [:]
        for root in configRoots {
            after.merge(try fileTree(of: root)) { current, _ in current }
        }
        if let claudeJSON = try? String(contentsOf: harness.home.root.appending(path: ".claude.json"), encoding: .utf8) {
            after[".claude.json"] = claudeJSON
        }

        var reordered: [String] = []
        for (path, original) in before {
            let result = try #require(after[path], "\(path) disappeared")
            if result == original { continue }

            // One difference is expected and harmless: switching a server on and
            // off again in Claude Code takes its name out of `deniedMcpServers`
            // and appends it back, so the entry moves to the end. The list is a
            // set as far as the client is concerned. Anything *else* differing is
            // a bug, so the comparison only forgives that specific reordering.
            #expect(
                try normalized(original) == normalized(result),
                "\(path) did not survive a round trip"
            )
            reordered.append(path)
        }

        print("""

          round-tripped \(toggled) server/client pairs across \(before.count) real config files
          byte-identical: \(before.count - reordered.count)/\(before.count)
          deny-list entries reordered in: \(reordered.isEmpty ? "none" : reordered.joined(separator: ", "))

        """)
    }

    /// A comparable view of a config with deny-list order removed.
    private func normalized(_ text: String) throws -> Shape {
        let document = try JSONDocument.parse(text)
        guard let denied = document.root["deniedMcpServers"]?.elements else {
            return Shape(document.root)
        }
        let names = denied
            .compactMap { $0["serverName"]?.stringValue ?? $0.stringValue }
            .sorted()
        // Rebuild the shape with the deny list replaced by its sorted names.
        var members = (document.root.members ?? []).map { member -> Shape.Entry in
            guard member.key == "deniedMcpServers" else {
                return Shape.Entry(key: member.key, value: Shape(member.value))
            }
            return Shape.Entry(key: member.key, value: .array(names.map { Shape.string($0) }))
        }
        members.sort { $0.key < $1.key }
        return .object(members)
    }

    @Test("the real files themselves are never touched")
    func realFilesUntouched() throws {
        let real = FileManager.default.homeDirectoryForCurrentUser
        let watched = [
            "Library/Application Support/Claude/claude_desktop_config.json",
            ".claude.json",
            ".claude/settings.json",
        ].map { real.appending(path: $0) }

        let before = watched.map { ConfigWriter.digest(of: $0) }

        let harness = try mirrorRealHome()
        defer { harness.cleanUp() }
        for server in harness.discover().servers.prefix(3) {
            for (clientID, enablement) in server.enabledIn where enablement != .absent {
                _ = try? harness.toggles.setEnabled(false, server: server, in: clientID)
            }
        }

        #expect(watched.map { ConfigWriter.digest(of: $0) } == before)
    }
}
