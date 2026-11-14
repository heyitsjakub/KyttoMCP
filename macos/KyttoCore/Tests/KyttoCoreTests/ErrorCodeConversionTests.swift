import Foundation
import Testing
@testable import KyttoCore

/// AUD-01: a JSON-RPC error code is a number the server chose. Converting it
/// with `Int(_: Double)` trapped on values that do not fit, which took down the
/// whole app instead of failing one check. These ran in a subprocess while the
/// trap was live; they run in-process now because the conversion cannot trap.
@Suite("JSON-RPC error codes — no conversion can crash")
struct ErrorCodeConversionTests {

    static let invalidCodes = ["1e100", "-1e100", "1.5", "1e400", "9223372036854775808"]

    @Test("integerValue accepts only exact integers that fit")
    func integerValue() throws {
        let cases: [(String, Int?)] = [
            ("-32601", -32_601), ("0", 0), ("1e3", 1_000), ("-0", 0),
            ("1.5", nil), ("1e100", nil), ("-1e100", nil), ("1e400", nil), ("-1e400", nil),
            ("9223372036854775808", nil),
        ]
        for (text, expected) in cases {
            let node = try JSONDocument.parse(#"{"code":\#(text)}"#).root["code"]
            #expect(node?.integerValue == expected, "for \(text)")
        }
    }

    @Test("a remote server's out-of-range code is a protocol failure, not a crash",
          arguments: invalidCodes)
    func remote(code: String) {
        let body = Data(#"{"jsonrpc":"2.0","id":1,"error":{"code":\#(code),"message":"fixture"}}"#.utf8)
        let transport = RemoteHTTPTransport { _, _, _, _ in
            .init(status: 200, headers: ["content-type": "application/json"], body: body)
        }
        #expect(throws: (any Error).self) {
            try MCPHTTPClient(transport: transport).listTools(url: "https://mcp.example/mcp")
        }
    }

    @Test("an out-of-range code in an HTTP error body is ignored, not converted",
          arguments: invalidCodes)
    func remoteErrorStatus(code: String) {
        let body = Data(#"{"jsonrpc":"2.0","id":1,"error":{"code":\#(code),"message":"fixture"}}"#.utf8)
        let transport = RemoteHTTPTransport { _, _, _, _ in
            .init(status: 400, headers: ["content-type": "application/json"], body: body)
        }
        #expect(throws: (any Error).self) {
            try MCPHTTPClient(transport: transport).listTools(url: "https://mcp.example/mcp")
        }
    }

    @Test("a local server's out-of-range code is a protocol error, not a crash",
          arguments: invalidCodes)
    func stdio(code: String) {
        let script = """
        import sys
        for line in sys.stdin:
            import json
            request = json.loads(line)
            if "id" in request:
                print('{"jsonrpc":"2.0","id":%s,"error":{"code":\(code),"message":"fixture"}}' % json.dumps(request["id"]), flush=True)
        """
        #expect(throws: (any Error).self) {
            try MCPStdioClient().listTools(
                command: "/usr/bin/python3", arguments: ["-c", script],
                environment: ["PATH": "/usr/bin:/bin"], timeout: 5
            )
        }
    }
}

/// AUD-04 and AUD-05.
@Suite("Health checks — deadline and private results", .serialized)
struct HealthCheckBoundaryTests {
    @Test("a server that stops reading cannot hold a check past its deadline")
    func undrainedServerRequest() throws {
        // The server sends a request with a huge id, so Kytto's answer fills
        // the pipe, then never reads and never exits on its own.
        let script = #"import sys,json,time; print(json.dumps({'jsonrpc':'2.0','id':'x'*262144,'method':'ping'}),flush=True); time.sleep(60)"#
        let started = Date()
        #expect(throws: (any Error).self) {
            try MCPStdioClient().listTools(command: "/usr/bin/python3", arguments: ["-c", script],
                                           environment: ["PATH": "/usr/bin:/bin"], timeout: 1)
        }
        // One second of check, plus the documented shutdown grace (stdin
        // close, SIGTERM, SIGKILL: about 0.7 s), plus scheduling slack.
        #expect(Date().timeIntervalSince(started) < 2.5)
    }

    @Test("the first measurement is written owner-only, folder included")
    func metadataIsPrivate() throws {
        let h = try ToggleHarness(); defer { h.cleanUp() }
        // Nothing has created Kytto's folder yet, as on a first check.
        try? FileManager.default.removeItem(at: h.paths.root)
        let previous = umask(0o022); defer { umask(previous) }
        let health = HealthResult(status: .failed, checkedAt: .timestamp(), toolCount: nil, tools: [],
                                  message: "fixture", stderr: "Bearer placeholder-token", durationSeconds: 0,
                                  serverName: nil, serverVersion: nil)
        try MetadataStore(paths: h.paths).record(health: health, tokenWeight: nil, for: "svc")
        let file = h.paths.root.appending(path: "server-metadata.json")
        func mode(_ url: URL) throws -> Int {
            try #require(FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber).intValue
        }
        #expect(try mode(file) & 0o077 == 0)
        #expect(try mode(h.paths.root) & 0o077 == 0)
        // The diagnostic itself is kept.
        #expect(try String(contentsOf: file, encoding: .utf8).contains("placeholder-token"))
    }
}
