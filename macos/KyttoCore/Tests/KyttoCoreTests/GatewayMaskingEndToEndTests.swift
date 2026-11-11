import Foundation
import Testing
@testable import KyttoCore

/// Runs the real `kytto-mcp-proxy` binary against the fixture server.
///
/// The filter is unit-tested on its own; this exists because everything that
/// can go wrong in wiring it up is invisible at that level — a refusal written
/// without its newline, two threads interleaving on stdout, or a relay that
/// buffers a message and never flushes it. Only a real process pair shows those.
@Suite("Gateway masking — end to end", .serialized)
struct GatewayMaskingEndToEndTests {

    private var helperURL: URL {
        get throws {
            // The helper is built beside the test products. How deep the
            // resource bundle sits below that directory depends on the build
            // system (flat bundles, or copied into `*.xctest/Contents/Resources`),
            // so look upward from it instead of assuming one layout.
            var base = Bundle.module.bundleURL
            var url = base.appending(path: "kytto-mcp-proxy")
            for _ in 0..<5 where !FileManager.default.isExecutableFile(atPath: url.path) {
                base = base.deletingLastPathComponent()
                url = base.appending(path: "kytto-mcp-proxy")
            }
            try #require(
                FileManager.default.isExecutableFile(atPath: url.path),
                "kytto-mcp-proxy was not built at \(url.path)"
            )
            return url
        }
    }

    private var fixtureServer: String {
        get throws {
            let base = try #require(Bundle.module.resourceURL)
            return base.appending(path: "Fixtures").appending(path: "fake_mcp_server.py").path
        }
    }

    /// A session with the helper: writes each line, then reads until the
    /// upstream closes.
    private func converse(exposedTools: [String]?, sending lines: [String]) throws -> [[String: Any]] {
        let directory = URL(filePath: NSTemporaryDirectory())
            .appending(path: "kytto-e2e-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let routesURL = directory.appending(path: "routes.json")
        let routeID = UUID().uuidString.lowercased()
        let route = GatewayRoute(
            id: routeID,
            serverID: "fixture",
            serverName: "fixture",
            clientID: .cursor,
            gatewayCommand: try helperURL.path,
            command: "python3",
            arguments: [try fixtureServer, "ok"],
            // No environment references, so nothing reaches for the Keychain.
            environment: [],
            directDefinitionSecretID: "gateway-definition::\(routeID)",
            exposedTools: exposedTools
        )
        try GatewayRouteStore(url: routesURL).insert(route)

        let process = Process()
        process.executableURL = try helperURL
        process.arguments = [
            "--route", routeID,
            "--routes", routesURL.path,
            "--event-log", directory.appending(path: "events.jsonl").path,
        ]
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = Pipe()
        try process.run()

        for line in lines {
            input.fileHandleForWriting.write(Data((line + "\n").utf8))
        }
        try? input.fileHandleForWriting.close()

        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()

        return String(decoding: data, as: UTF8.self)
            .split(separator: "\n")
            .compactMap { try? JSONSerialization.jsonObject(with: Data($0.utf8)) as? [String: Any] }
    }

    private let handshake = [
        #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}"#,
        #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
    ]

    private func tools(in replies: [[String: Any]], id: Int) -> [String]? {
        guard let reply = replies.first(where: { ($0["id"] as? NSNumber)?.intValue == id }),
              let result = reply["result"] as? [String: Any],
              let tools = result["tools"] as? [[String: Any]]
        else { return nil }
        return tools.compactMap { $0["name"] as? String }
    }

    @Test("an unmasked route still relays the full tool list")
    func transparentRoute() throws {
        let replies = try converse(
            exposedTools: nil,
            sending: handshake + [#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#]
        )
        #expect(tools(in: replies, id: 2) == ["read_file", "write_file", "list_directory"])
    }

    @Test("a masked route hides the tools it was not given")
    func maskedList() throws {
        let replies = try converse(
            exposedTools: ["read_file", "list_directory"],
            sending: handshake + [#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#]
        )
        #expect(tools(in: replies, id: 2) == ["read_file", "list_directory"])
    }

    @Test("a call to a hidden tool is refused without reaching the server")
    func refusesHiddenCall() throws {
        let replies = try converse(
            exposedTools: ["read_file"],
            sending: handshake + [
                #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{}}}"#,
                #"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_file","arguments":{}}}"#,
            ]
        )

        let refused = try #require(replies.first { ($0["id"] as? NSNumber)?.intValue == 2 })
        let error = try #require(refused["error"] as? [String: Any])
        #expect(error["code"] as? Int == GatewayToolFilter.unknownTool)
        // The fixture answers every call with "fixture result", so a result
        // here would mean the refusal never happened.
        #expect(refused["result"] == nil)

        let allowed = try #require(replies.first { ($0["id"] as? NSNumber)?.intValue == 3 })
        #expect(allowed["result"] != nil)
        #expect(allowed["error"] == nil)
    }

    @Test("the handshake is untouched by masking")
    func handshakeSurvives() throws {
        let replies = try converse(
            exposedTools: [],
            sending: handshake + [#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#]
        )
        let initialize = try #require(replies.first { ($0["id"] as? NSNumber)?.intValue == 1 })
        let result = try #require(initialize["result"] as? [String: Any])
        let info = try #require(result["serverInfo"] as? [String: Any])
        #expect(info["name"] as? String == "fake-mcp-server")
        #expect(tools(in: replies, id: 2) == [])
    }

    /// Masking must not become a filter on everything else the server offers.
    @Test("prompts and resources are not touched")
    func otherListsPassThrough() throws {
        let replies = try converse(
            exposedTools: ["read_file"],
            sending: handshake + [
                #"{"jsonrpc":"2.0","id":2,"method":"prompts/list","params":{}}"#,
                #"{"jsonrpc":"2.0","id":3,"method":"resources/list","params":{}}"#,
            ]
        )
        let prompts = try #require(replies.first { ($0["id"] as? NSNumber)?.intValue == 2 })
        #expect(((prompts["result"] as? [String: Any])?["prompts"] as? [[String: Any]])?.count == 2)

        let resources = try #require(replies.first { ($0["id"] as? NSNumber)?.intValue == 3 })
        #expect(((resources["result"] as? [String: Any])?["resources"] as? [[String: Any]])?.count == 1)
    }

    // MARK: - What the journal says about a session

    /// Runs the helper against an arbitrary upstream and returns its journal.
    /// With `closingInput` false the client stays attached until the helper
    /// exits by itself, which is what a crashed server looks like.
    private func journal(
        command: String,
        arguments: [String],
        exposedTools: [String]? = nil,
        sending lines: [String] = [],
        closingInput: Bool = true
    ) throws -> [GatewayEvent] {
        let directory = URL(filePath: NSTemporaryDirectory())
            .appending(path: "kytto-e2e-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let routesURL = directory.appending(path: "routes.json")
        let eventsURL = directory.appending(path: "events.jsonl")
        let routeID = UUID().uuidString.lowercased()
        try GatewayRouteStore(url: routesURL).insert(GatewayRoute(
            id: routeID,
            serverID: "fixture",
            serverName: "fixture",
            clientID: .cursor,
            gatewayCommand: try helperURL.path,
            command: command,
            arguments: arguments,
            environment: [],
            directDefinitionSecretID: "gateway-definition::\(routeID)",
            exposedTools: exposedTools
        ))

        let process = Process()
        process.executableURL = try helperURL
        process.arguments = ["--route", routeID, "--routes", routesURL.path, "--event-log", eventsURL.path]
        let input = Pipe()
        let output = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = Pipe()
        try process.run()
        for line in lines {
            input.fileHandleForWriting.write(Data((line + "\n").utf8))
        }
        if closingInput { try? input.fileHandleForWriting.close() }
        _ = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        try? input.fileHandleForWriting.close()

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return ((try? String(contentsOf: eventsURL, encoding: .utf8)) ?? "")
            .split(separator: "\n")
            .compactMap { try? decoder.decode(GatewayEvent.self, from: Data($0.utf8)) }
    }

    @Test("a refused call is journalled by tool name, without its arguments")
    func refusalIsJournalled() throws {
        let events = try journal(
            command: "python3",
            arguments: [try fixtureServer, "ok"],
            exposedTools: ["read_file"],
            sending: handshake + [
                #"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{"path":"private-argument"}}}"#,
            ]
        )
        let refused = try #require(events.first { $0.kind == .toolCallRefused })
        #expect(refused.toolName == "write_file")
        #expect(refused.requestID == "2")
        // Refused here, so the server never started or completed it.
        #expect(!events.contains { $0.kind == .toolCallStarted || $0.kind == .toolCallCompleted })
        let encoded = String(decoding: try JSONEncoder().encode(events), as: UTF8.self)
        #expect(!encoded.contains("private-argument"))
    }

    @Test("a server that stops while its client is attached is marked unexpected")
    func crashIsJournalled() throws {
        let events = try journal(command: "/bin/sh", arguments: ["-c", "exit 3"], closingInput: false)
        let ended = try #require(events.first { $0.kind == .sessionEnded })
        #expect(ended.exitCode == 3)
        #expect(ended.unexpected == true)
    }

    @Test("a session the client closed is not a crash")
    func clientCloseIsExpected() throws {
        let events = try journal(command: "python3", arguments: [try fixtureServer, "ok"], sending: handshake)
        let ended = try #require(events.first { $0.kind == .sessionEnded })
        #expect(ended.unexpected == false)
    }

    @Test("a server that cannot be started leaves a failed session, not silence")
    func launchFailureIsJournalled() throws {
        let events = try journal(command: "kytto-no-such-command-\(UUID().uuidString)", arguments: [])
        #expect(events.map(\.kind) == [.sessionFailed])
        #expect(events.first?.reason == GatewayFailureReason.launchFailed.rawValue)
    }
}
