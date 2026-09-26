import Foundation
import Testing
@testable import KyttoCore

@Suite("GatewayLineFramer — MCP stdio framing")
struct GatewayLineFramerTests {

    private func lines(feeding chunks: [String]) -> [String] {
        var framer = GatewayLineFramer()
        var result: [String] = []
        for chunk in chunks {
            framer.consume(Data(chunk.utf8)) { result.append(String(decoding: $0, as: UTF8.self)) }
        }
        framer.flush { result.append(String(decoding: $0, as: UTF8.self)) }
        return result
    }

    @Test("One message per line, whatever the read boundaries were")
    func splitsAcrossChunks() {
        // The normal case, not the edge case: a large tools/list response
        // arrives in several reads and none of them ends on a message boundary.
        #expect(lines(feeding: ["{\"a\":1}\n{\"b\":2}\n"]) == ["{\"a\":1}", "{\"b\":2}"])
        #expect(lines(feeding: ["{\"a\":", "1}\n{\"b", "\":2}\n"]) == ["{\"a\":1}", "{\"b\":2}"])
        #expect(lines(feeding: ["{\"a\":1}\n", "{\"b\":2}\n"]) == ["{\"a\":1}", "{\"b\":2}"])
    }

    @Test("A trailing partial message is still handed over")
    func flushesPartial() {
        // A server killed mid-write should not have its bytes silently dropped.
        #expect(lines(feeding: ["{\"a\":1}\n{\"partial\""]) == ["{\"a\":1}", "{\"partial\""])
    }

    @Test("Empty lines survive as empty messages")
    func emptyLines() {
        #expect(lines(feeding: ["\n\n"]) == ["", ""])
    }
}

@Suite("GatewayToolFilter — masking")
struct GatewayToolFilterTests {

    private let exposed = ["read_file", "list_directory"]

    private func filter() -> GatewayToolFilter {
        GatewayToolFilter(exposedTools: exposed)
    }

    private func json(_ text: String) -> Data { Data(text.utf8) }

    private func object(_ data: Data) throws -> [String: Any] {
        try #require(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    // MARK: - Client to server

    @Test("A call to an exposed tool goes upstream untouched")
    func allowsExposedCall() {
        let line = json(#"{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"read_file"}}"#)
        #expect(filter().inspectClientLine(line) == .forward)
    }

    @Test("A call to a hidden tool is refused here and never reaches the server")
    func refusesHiddenCall() throws {
        let line = json(#"{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_file"}}"#)
        guard case .refuse(let response) = filter().inspectClientLine(line) else {
            Issue.record("expected a refusal")
            return
        }

        let reply = try object(response)
        let error = try #require(reply["error"] as? [String: Any])
        #expect(error["code"] as? Int == GatewayToolFilter.methodNotFound)
        #expect((error["message"] as? String)?.contains("write_file") == true)
        // The id has to come back in the type it arrived in, or the client
        // cannot match the reply to its own request.
        #expect(reply["id"] as? Int == 7)
        #expect(reply["jsonrpc"] as? String == "2.0")
    }

    @Test("A string request id comes back as a string")
    func preservesStringID() throws {
        let line = json(#"{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"write_file"}}"#)
        guard case .refuse(let response) = filter().inspectClientLine(line) else {
            Issue.record("expected a refusal")
            return
        }
        #expect(try object(response)["id"] as? String == "abc")
    }

    @Test("Anything that is not a tools call passes through")
    func passesOtherMethods() {
        let subject = filter()
        for line in [
            #"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"#,
            #"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
            #"{"jsonrpc":"2.0","id":2,"method":"prompts/list","params":{}}"#,
            "a banner the server printed to stdout",
            "",
        ] {
            #expect(subject.inspectClientLine(json(line)) == .forward, "for \(line)")
        }
    }

    // MARK: - Server to client

    @Test("Hidden tools are removed from the tools/list result")
    func filtersToolList() throws {
        let subject = filter()
        _ = subject.inspectClientLine(json(#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#))

        let response = json("""
        {"jsonrpc":"2.0","id":2,"result":{"tools":[\
        {"name":"read_file","description":"Read."},\
        {"name":"write_file","description":"Write."},\
        {"name":"list_directory","description":"List."}\
        ]}}
        """)
        let rewritten = try object(subject.rewriteServerLine(response))
        let result = try #require(rewritten["result"] as? [String: Any])
        let tools = try #require(result["tools"] as? [[String: Any]])

        #expect(tools.compactMap { $0["name"] as? String } == ["read_file", "list_directory"])
        // Everything else about the envelope survives.
        #expect(rewritten["id"] as? Int == 2)
        #expect(rewritten["jsonrpc"] as? String == "2.0")
        #expect(tools.first?["description"] as? String == "Read.")
    }

    @Test("A result with nothing to remove is returned byte for byte")
    func untouchedWhenNothingHidden() {
        let subject = filter()
        _ = subject.inspectClientLine(json(#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#))

        let response = json(#"{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file"}]}}"#)
        #expect(subject.rewriteServerLine(response) == response)
    }

    /// The reason responses are matched to their request rather than sniffed
    /// for a `tools` key: another method is allowed to return one.
    @Test("Only the answer to a tools/list request is rewritten")
    func onlyRewritesMatchedResponses() {
        let subject = filter()
        let unsolicited = json(#"{"jsonrpc":"2.0","id":99,"result":{"tools":[{"name":"write_file"}]}}"#)
        #expect(subject.rewriteServerLine(unsolicited) == unsolicited)
    }

    @Test("A request is only answered once")
    func doesNotRewriteTwice() {
        let subject = filter()
        _ = subject.inspectClientLine(json(#"{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"#))

        let response = json(#"{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"write_file"}]}}"#)
        let first = subject.rewriteServerLine(response)
        #expect(first != response, "the first answer should have been filtered")
        // A duplicate id later in the session belongs to a different request.
        #expect(subject.rewriteServerLine(response) == response)
    }

    @Test("Non-JSON output from the server is forwarded verbatim")
    func passesBanners() {
        let banner = json("Server listening on stdio…")
        #expect(filter().rewriteServerLine(banner) == banner)
    }

    @Test("An empty allow list hides everything")
    func emptyListHidesAll() throws {
        let subject = GatewayToolFilter(exposedTools: [])
        _ = subject.inspectClientLine(json(#"{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"#))

        let response = json(#"{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"read_file"}]}}"#)
        let tools = try #require(
            (try object(subject.rewriteServerLine(response))["result"] as? [String: Any])?["tools"] as? [[String: Any]]
        )
        #expect(tools.isEmpty)
    }

    /// The closed-list decision (§7.11): a tool the server begins offering after
    /// the list was chosen stays hidden rather than quietly re-growing the
    /// context budget the user set.
    @Test("A tool added by a server update stays hidden until it is chosen")
    func newToolsAreHidden() throws {
        let subject = filter()
        _ = subject.inspectClientLine(json(#"{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"#))

        let response = json(#"{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"read_file"},{"name":"brand_new_tool"}]}}"#)
        let tools = try #require(
            (try object(subject.rewriteServerLine(response))["result"] as? [String: Any])?["tools"] as? [[String: Any]]
        )
        #expect(tools.compactMap { $0["name"] as? String } == ["read_file"])

        let call = json(#"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"brand_new_tool"}}"#)
        #expect(subject.inspectClientLine(call) != .forward)
    }
}

@Suite("GatewayRoute — exposed tools")
struct GatewayRouteMaskingTests {

    private func store() throws -> (GatewayRouteStore, URL) {
        let directory = URL(filePath: NSTemporaryDirectory())
            .appending(path: "kytto-mask-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return (GatewayRouteStore(url: directory.appending(path: "routes.json")), directory)
    }

    private func route(id: String = UUID().uuidString.lowercased()) -> GatewayRoute {
        GatewayRoute(
            id: id,
            serverID: "filesystem",
            serverName: "filesystem",
            clientID: .cursor,
            gatewayCommand: "/usr/local/bin/kytto-mcp-proxy",
            command: "npx",
            arguments: ["-y", "server-filesystem"],
            environment: [],
            directDefinitionSecretID: "gateway-definition::x"
        )
    }

    @Test("A route exposes everything until it is narrowed")
    func defaultsToTransparent() {
        #expect(route().exposedTools == nil)
    }

    @Test("Narrowing and clearing round trip through the store")
    func updateRoundTrips() throws {
        let (store, directory) = try store()
        defer { try? FileManager.default.removeItem(at: directory) }

        let original = route()
        try store.insert(original)
        // Compared against what is on disk rather than against the value handed
        // to `insert`: the store encodes dates as whole-second ISO-8601, so a
        // route's `createdAt` is already truncated by the time anything reads
        // it back. That is the store's existing behaviour, not masking's.
        let stored = try store.route(id: original.id)

        let narrowed = try store.update(id: original.id, exposedTools: ["read_file", "list_directory"])
        #expect(narrowed.exposedTools == ["list_directory", "read_file"], "stored sorted, so files do not churn")
        #expect(try store.route(id: original.id).exposedTools == ["list_directory", "read_file"])

        // Nothing about launching the upstream server may have moved.
        #expect(narrowed.command == stored.command)
        #expect(narrowed.arguments == stored.arguments)
        #expect(narrowed.createdAt == stored.createdAt)

        let cleared = try store.update(id: original.id, exposedTools: nil)
        #expect(cleared.exposedTools == nil)
    }

    @Test("Exposing nothing is a real choice, not the same as exposing everything")
    func emptyIsNotNil() throws {
        let (store, directory) = try store()
        defer { try? FileManager.default.removeItem(at: directory) }

        let original = route()
        try store.insert(original)
        #expect(try store.update(id: original.id, exposedTools: []).exposedTools == [])
        #expect(try store.route(id: original.id).exposedTools == [])
    }

    @Test("Duplicates are collapsed")
    func deduplicates() {
        #expect(route().exposing(["a", "a", "b"]).exposedTools == ["a", "b"])
    }

    @Test("A route file written before masking existed still reads")
    func decodesOlderRecords() throws {
        let older = """
        [{"id":"11111111-1111-1111-1111-111111111111","serverID":"filesystem","serverName":"filesystem",\
        "clientID":"cursor","gatewayCommand":"/bin/proxy","command":"npx","arguments":[],"environment":[],\
        "directDefinitionSecretID":"gateway-definition::x","createdAt":"2026-01-01T00:00:00Z"}]
        """
        let directory = URL(filePath: NSTemporaryDirectory())
            .appending(path: "kytto-mask-old-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let url = directory.appending(path: "routes.json")
        try older.write(to: url, atomically: true, encoding: .utf8)

        let routes = try GatewayRouteStore(url: url).all()
        #expect(routes.count == 1)
        #expect(routes[0].exposedTools == nil, "an upgraded install must not start masking on its own")
    }

    @Test("Updating a route that is gone is refused")
    func unknownRoute() throws {
        let (store, directory) = try store()
        defer { try? FileManager.default.removeItem(at: directory) }
        #expect(throws: GatewayRouteError.unknownRoute("missing")) {
            try store.update(id: "missing", exposedTools: [])
        }
    }
}
