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
            // The test bundle sits beside the built products.
            let base = try #require(Bundle.module.resourceURL).deletingLastPathComponent()
            let url = base.appending(path: "kytto-mcp-proxy")
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
        #expect(error["code"] as? Int == GatewayToolFilter.methodNotFound)
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
}
