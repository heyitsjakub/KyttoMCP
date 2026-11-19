import Foundation
import KyttoCore
import Testing
import WebKit
@testable import KyttoMCP

@MainActor
struct ProjectDefinitionTests {
    @MainActor private struct Fixture {
        let home = FileManager.default.temporaryDirectory.appending(path: "KyttoSources-\(UUID().uuidString)")
        let first: ProjectFolder
        let second: ProjectFolder
        let model: AppModel
        var cursor: URL { home.appending(path: ".cursor/mcp.json") }
        func file(_ folder: ProjectFolder) -> URL { URL(filePath: folder.path).appending(path: ".mcp.json") }
        func source(_ folder: ProjectFolder) -> ClientID { ClientRegistry.projectClientID(folder: folder.id, file: .portable) }

        init() throws {
            first = ProjectFolder(path: home.appending(path: "one").path)
            second = ProjectFolder(path: home.appending(path: "two").path)
            model = AppModel(home: home)
            try write(#"{"theme":"dark","mcpServers":{}}"#, at: cursor)
            try write(#"{"mcpServers":{"Demo":{"command":"first"}}}"#, at: file(first))
            try write(#"{"mcpServers":{"Demo":{"command":"second","args":["--mode"],"env":{"REF":"${env:SOURCE_TOKEN}","TOKEN":"private-fixture"}}}}"#, at: file(second))
            let user: [String: Any] = [
                "mcpServers": ["Demo": ["command": "user"]],
                "projects": [first.path: ["mcpServers": ["Demo": ["command": "local"]]]],
            ]
            try JSONSerialization.data(withJSONObject: user).write(to: home.appending(path: ".claude.json"))
            _ = try model.updateSettings {
                $0.hasCompletedOnboarding = true
                $0.projectFolders = [first, second]
            }
        }

        func write(_ text: String, at file: URL) throws {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try text.write(to: file, atomically: true, encoding: .utf8)
        }
        func preview() async throws -> ProjectDefinitionCopy.Preview {
            try await model.previewProjectCopy(serverID: "demo", source: source(second), target: .cursor)
        }
    }

    @Test func workspacePriorityAndSelectedCopy() async throws {
        let f = try Fixture()
        defer { try? FileManager.default.removeItem(at: f.home) }
        let inventory = try #require(StateDTO(f.model).definitions["demo"])
        #expect(inventory.copies.count == 4)
        #expect(inventory.overlapsScopes)
        let one = try #require(inventory.priorities.first { $0.workspace == f.first.path })
        #expect(one.candidates.count == 3)
        #expect(inventory.copies.first { $0.clientID == one.winner }?.scope == "Local · " + f.first.path)
        #expect(inventory.priorities.first { $0.workspace == f.second.path }?.winner == f.source(f.second).rawValue)

        let original = try Data(contentsOf: f.file(f.second))
        let preview = try await f.preview()
        #expect(preview.sourceFile == f.file(f.second).path && preview.targetFile == f.cursor.path)
        #expect(preview.unresolvedVariables == ["SOURCE_TOKEN"])
        #expect(!String(decoding: try JSONEncoder().encode(preview), as: UTF8.self).contains("private-fixture"))
        let result = try await f.model.applyProjectCopy(token: #require(preview.token))
        #expect(result.changed == [.cursor] && result.backupIDs[.cursor] != nil)
        let copy = try #require(f.model.current().servers.first { $0.id == "demo" }?.definitionsByClient[.cursor])
        #expect(copy.command == "second")
        #expect(copy.env.first { $0.key == "TOKEN" }?.value == "private-fixture")
        #expect(try await f.preview().token == nil)
        _ = try f.model.removeServer(serverID: "demo", clientID: .cursor)
        #expect(try Data(contentsOf: f.file(f.second)) == original)
        #expect(try JSONDocument.parse(contentsOf: f.cursor).root["theme"]?.stringValue == "dark")
        #expect(try JSONDocument.parse(contentsOf: f.cursor).root["mcpServers"]?["Demo"] == nil)
    }

    @Test func staleAndUnsupportedCopiesNeverWrite() async throws {
        let f = try Fixture()
        defer { try? FileManager.default.removeItem(at: f.home) }
        let token = try #require(await f.preview().token)
        try f.write(#"{"mcpServers":{"Demo":{"command":"changed"}}}"#, at: f.file(f.second))
        await #expect(throws: ProjectCopyError.self) { try await f.model.applyProjectCopy(token: token) }
        let fresh = try #require(await f.preview().token)
        let raced = #"{"mcpServers":{"dEmO":{"command":"keep"}}}"#
        try f.write(raced, at: f.cursor)
        await #expect(throws: ProjectCopyError.self) { try await f.model.applyProjectCopy(token: fresh) }
        #expect(try String(contentsOf: f.cursor, encoding: .utf8) == raced)
        try f.write(#"{"mcpServers":{"Demo":{"url":"https://example.invalid","headers":{"Authorization":"secret-header"}}}}"#, at: f.file(f.second))
        let preview = try await f.preview()
        #expect(preview.token == nil)
        #expect(preview.blockers.contains { $0.contains("headers") })
        #expect(preview.blockers.contains { $0.contains("already contains") })
        #expect(!String(decoding: try JSONEncoder().encode(preview), as: UTF8.self).contains("secret-header"))
        #expect(f.model.backups.list(clientID: .cursor).isEmpty)
    }

    @Test func reviewTracksExternalSemanticsAndSurvivesRestart() async throws {
        let f = try Fixture()
        defer { try? FileManager.default.removeItem(at: f.home) }
        _ = try await f.model.applyProjectCopy(token: #require(await f.preview().token))
        let text = try String(contentsOf: f.cursor, encoding: .utf8)
        try f.write("  \n" + text, at: f.cursor)
        await f.model.reloadInBackground()
        #expect(f.model.launchReviews.alerts.isEmpty)
        let draft = ServerDraft(name: "Solo", transport: .stdio, command: "original")
        _ = try f.model.createServer(draft, in: [.cursor])
        _ = try f.model.updateServer(ServerDraft(name: "Solo", command: "own-write"), serverID: "solo")
        #expect(f.model.launchReviews.alerts.isEmpty)
        try f.write(#"{"mcpServers":{"Solo":{"command":"outside-secret","args":["secret-arg"],"env":{"SECRET":"secret-val"}}}}"#, at: f.cursor)
        await f.model.reloadInBackground()
        let first = try #require(f.model.launchReviews.alerts.first)
        #expect(first.file == f.cursor.path && first.scope == "Global")
        #expect(first.fields.map(\.name) == ["Command", "Arguments", "Environment"])
        try f.write(#"{"mcpServers":{"Solo":{"type":"http","url":"https://host.invalid/?token=private-url"}}}"#, at: f.cursor)
        await #expect(throws: LaunchReviews.ReviewError.self) { try await f.model.acknowledgeLaunchReview(token: first.token) }
        let latest = try #require(f.model.launchReviews.alerts.first)
        #expect(latest.fields.contains { $0.name == "Endpoint" })
        let reopened = AppModel(home: f.home)
        #expect(reopened.launchReviews.alerts == [latest])
        let saved = try String(contentsOf: KyttoPaths(home: f.home).root.appending(path: "launch-reviews.json"), encoding: .utf8)
        for value in ["outside-secret", "secret-arg", "secret-val", "private-url"] {
            #expect(!saved.contains(value))
        }
        try await reopened.acknowledgeLaunchReview(token: latest.token)
        #expect(AppModel(home: f.home).launchReviews.alerts.isEmpty)
    }

    @Test func projectCopyAndUndoThroughWebView() async throws {
        let f = try Fixture()
        defer { try? FileManager.default.removeItem(at: f.home) }
        let router = CommandRouter()
        CommandRegistry.registerAll(on: router, model: f.model)
        router.register("app.info") {
            AppInfoDTO(version: "test", build: "1", capabilities: ["project-copy"], platformDisplayName: "Test", secretStoreDisplayName: "Test")
        }
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.setURLSchemeHandler(KyttoSchemeHandler(), forURLScheme: KyttoSchemeHandler.scheme)
        configuration.userContentController.addScriptMessageHandler(router, contentWorld: .page, name: CommandRouter.messageHandlerName)
        let view = WKWebView(frame: NSRect(x: 0, y: 0, width: 1100, height: 800), configuration: configuration)
        router.attach(to: view)
        view.load(URLRequest(url: try #require(URL(string: "kytto://app/index.html"))))
        func wait(_ selector: String) async throws {
            for _ in 0..<150 {
                if (try? await view.evaluateJavaScript("Boolean(document.querySelector('\(selector)'))")) as? Bool == true { return }
                try await Task.sleep(for: .milliseconds(40))
            }
            try #require(Bool(false), "Control did not appear: \(selector)")
        }
        func click(_ selector: String) async throws {
            try await wait(selector)
            _ = try await view.evaluateJavaScript("document.querySelector('\(selector)').click()")
        }
        try await click("[data-action=open-server][data-server-id=demo]")
        try await click("[data-action=open-project-copy]")
        _ = try await view.evaluateJavaScript("""
            for (const [action, value] of [['project-copy-source', '\(f.source(f.second).rawValue)'], ['project-copy-target', 'cursor']]) {
                const select = document.querySelector('[data-action=' + action + ']');
                select.value = value;
                select.dispatchEvent(new Event('change', { bubbles: true }));
            }
            """)
        try await click("[data-action=preview-project-copy]")
        try await wait("[data-action=apply-project-copy]")
        let body = try #require(try await view.evaluateJavaScript("document.querySelector('.sheet').textContent") as? String)
        #expect(body.contains(f.file(f.second).path) && body.contains("SOURCE_TOKEN") && !body.contains("private-fixture"))
        try await click("[data-action=apply-project-copy]")
        try await wait("[data-action=undo-matrix-write]")
        #expect(try JSONDocument.parse(contentsOf: f.cursor).root["mcpServers"]?["Demo"]?["command"]?.stringValue == "second")
        try await click("[data-action=undo-matrix-write]")
        try await wait("[data-action=toggle][data-server-id=demo][data-client-id=cursor][data-state=absent]")
        #expect(try JSONDocument.parse(contentsOf: f.cursor).root["mcpServers"]?["Demo"] == nil)
    }
}
