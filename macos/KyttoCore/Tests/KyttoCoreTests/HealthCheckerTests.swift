import Foundation
import Testing
@testable import KyttoCore

/// Builds a `Server` pointing at the fake MCP server in the fixtures.
private func fakeServer(mode: String, name: String = "fake", env: [EnvEntry] = []) -> Server {
    guard let base = Bundle.module.resourceURL else { fatalError("no test resources") }
    let script = base.appending(path: "Fixtures").appending(path: "fake_mcp_server.py").path

    return Server(
        id: Server.identity(for: name),
        name: name,
        transport: .stdio,
        // Invoked through python3 rather than a shebang, so the test does not
        // depend on the executable bit surviving resource copying.
        command: "python3",
        args: [script, mode],
        env: env,
        url: nil,
        enabledIn: [:],
        origin: .configFile(.cursor),
        fingerprint: "test",
        isBundled: false,
        definitionSource: "{}"
    )
}

private func remoteServer() -> Server {
    Server(
        id: "remote",
        name: "remote",
        transport: .http,
        command: nil,
        args: [],
        env: [],
        url: "https://example.com/mcp",
        enabledIn: [:],
        origin: .configFile(.cursor),
        fingerprint: "test",
        isBundled: false,
        definitionSource: "{}"
    )
}

/// The fake server needs python3 on PATH; the health checker needs a PATH that
/// has it. The real `ShellEnvironment.current` would do, but pinning it keeps
/// the tests independent of whatever the machine's login shell says.
private let testShell = ShellEnvironment.resolve(shellPath: "/bin/zsh")

@Suite("HealthChecker")
struct HealthCheckerTests {

    @Test("a working server reports its tools")
    func passes() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, weight) = checker.check(fakeServer(mode: "ok"))

        #expect(health.status == .passed, Comment(rawValue: health.message ?? "no message"))
        #expect(health.toolCount == 3)
        #expect(health.tools.map(\.name) == ["read_file", "write_file", "list_directory"])
        #expect(health.serverName == "fake-mcp-server")
        #expect(health.serverVersion == "0.1.0")
        #expect(health.protocolVersion == MCPStdioClient.preferredProtocolVersion)
        #expect(health.capabilityNames == ["prompts", "resources", "tools"])
        #expect(health.promptCount == 2)
        #expect(health.resourceCount == 1)
        #expect(health.resolvedCommand?.hasSuffix("python3") == true)
        #expect(weight != nil)
    }

    @Test("inspector keeps schemas and safety annotations")
    func inspectorDetails() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, _) = checker.check(fakeServer(mode: "ok"))

        let read = try #require(health.tools.first { $0.name == "read_file" })
        #expect(try #require(read.inputSchemaJSON).contains("required"))
        #expect(read.annotations?.readOnlyHint == true)
        #expect(read.annotations?.destructiveHint == false)

        let write = try #require(health.tools.first { $0.name == "write_file" })
        #expect(write.annotations?.destructiveHint == true)
    }

    @Test("token weight is measured on the definitions the server actually sent")
    func tokenWeight() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (_, weight) = checker.check(fakeServer(mode: "ok"))

        let measured = try #require(weight)
        #expect(measured.method == BPETokenizer.cl100kMethod)
        #expect(measured.isMeasured)
        // Three tools with schemas land in the high hundreds of characters.
        #expect(measured.estimate > 50)
        #expect(measured.estimate < 2000)
        #expect(measured.percentOfContext > 0)
    }

    @Test("every tool carries its own share of the weight")
    func perToolWeight() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, weight) = checker.check(fakeServer(mode: "ok"))

        let counts = health.tools.map(\.tokenCount)
        #expect(counts.allSatisfy { ($0 ?? 0) > 0 }, "every tool should have been measured")

        // The parts sum to slightly less than the whole: the array's brackets
        // and the commas between entries belong to no single tool.
        let total = try #require(weight).estimate
        let sum = counts.compactMap { $0 }.reduce(0, +)
        #expect(sum < total)
        #expect(Double(sum) > Double(total) * 0.9)

        // The tool with the larger schema costs more. This is the ordering the
        // breakdown in server detail is read for, so it is asserted rather than
        // assumed.
        let read = try #require(health.tools.first { $0.name == "read_file" }?.tokenCount)
        let list = try #require(health.tools.first { $0.name == "list_directory" }?.tokenCount)
        #expect(read != list)
    }

    @Test("a server with no tools still passes, and weighs almost nothing")
    func emptyToolList() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, weight) = checker.check(fakeServer(mode: "empty"))

        #expect(health.status == .passed)
        #expect(health.toolCount == 0)
        #expect(try #require(weight).estimate < 5)
    }

    @Test("a banner on stdout does not confuse the handshake")
    func noisyStdout() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, _) = checker.check(fakeServer(mode: "noisy"))

        // Servers are not supposed to print to stdout, but enough of them do
        // that refusing to tolerate it would fail working servers.
        #expect(health.status == .passed, Comment(rawValue: health.message ?? "no message"))
        #expect(health.toolCount == 3)
    }

    @Test("a response with the wrong JSON-RPC version is never accepted")
    func wrongJSONRPCVersion() {
        let checker = HealthChecker(shell: testShell, timeout: 2)
        let (health, weight) = checker.check(fakeServer(mode: "wrong-jsonrpc"))
        #expect(health.status == .failed)
        #expect(weight == nil)
    }

    @Test("a fractional request id cannot impersonate the initialize response")
    func fractionalRequestID() {
        let checker = HealthChecker(shell: testShell, timeout: 2)
        let (health, weight) = checker.check(fakeServer(mode: "fractional-id"))
        #expect(health.status == .failed)
        #expect(weight == nil)
    }

    @Test("a crashing server fails with its own stderr, verbatim")
    func crash() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, weight) = checker.check(fakeServer(mode: "crash"))

        #expect(health.status == .failed)
        #expect(weight == nil)
        let stderr = try #require(health.stderr)
        // §6: surfaced exactly as written, not summarised.
        #expect(stderr.contains("cannot find module 'some-dependency'"))
        #expect(stderr.contains("at Object.<anonymous> (/tmp/server.js:1:1)"))
    }

    @Test("a server that never answers is timed out, not waited on forever")
    func timeout() throws {
        let checker = HealthChecker(shell: testShell, timeout: 2)
        let started = Date()
        let (health, _) = checker.check(fakeServer(mode: "slow"))

        #expect(health.status == .failed)
        #expect(Date().timeIntervalSince(started) < 15, "the timeout did not take effect")
        #expect(try #require(health.message).contains("did not answer"))
    }

    @Test("timeout errors preserve a useful configured duration and unit")
    func timeoutWording() throws {
        let short = HealthChecker(shell: testShell, timeout: 0.25)
        let (shortHealth, _) = short.check(fakeServer(mode: "slow"))
        #expect(try #require(shortHealth.message).contains("within 1 second"))
        #expect(!shortHealth.message!.contains("0 seconds"))

        let normal = HealthChecker(shell: testShell, timeout: 2)
        let (normalHealth, _) = normal.check(fakeServer(mode: "slow"))
        #expect(try #require(normalHealth.message).contains("within 2 seconds"))
    }

    @Test("a JSON-RPC error is reported as the server phrased it")
    func serverError() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, _) = checker.check(fakeServer(mode: "error"))

        #expect(health.status == .failed)
        #expect(try #require(health.message).contains("tools are unavailable right now"))
    }

    @Test("a malformed response fails rather than reporting zero tools")
    func garbage() throws {
        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, _) = checker.check(fakeServer(mode: "garbage"))

        #expect(health.status == .failed)
        #expect(health.toolCount == nil, "a broken answer must not look like an empty one")
    }

    @Test("a missing command explains where Kytto looked")
    func commandNotFound() throws {
        var server = fakeServer(mode: "ok")
        server = Server(
            id: server.id,
            name: server.name,
            transport: .stdio,
            command: "definitely-not-a-real-command-xyz",
            args: [],
            env: [],
            url: nil,
            enabledIn: [:],
            origin: server.origin,
            fingerprint: server.fingerprint,
            isBundled: false,
            definitionSource: "{}"
        )

        let checker = HealthChecker(shell: testShell, timeout: 10)
        let (health, _) = checker.check(server)

        #expect(health.status == .failed)
        let message = try #require(health.message)
        #expect(message.contains("definitely-not-a-real-command-xyz"))
        // The PATH is named, because "command not found" from a GUI app sends
        // people looking in the wrong place.
        #expect(message.contains("/usr/bin") || message.contains("/opt/homebrew/bin"))
    }

    /// Claude Desktop extensions ship `${__dirname}` in their arguments. Kytto
    /// knows the bundle directory, so running the literal text would fail every
    /// extension for a reason that has nothing to do with the server.
    @Test("client placeholders are expanded before the server is run")
    func expandsPlaceholders() throws {
        guard let base = Bundle.module.resourceURL else { fatalError("no test resources") }
        let directory = base.appending(path: "Fixtures").path

        var server = fakeServer(mode: "ok")
        server = Server(
            id: server.id,
            name: server.name,
            transport: .stdio,
            command: "python3",
            args: ["${__dirname}/fake_mcp_server.py", "ok"],
            env: [],
            url: nil,
            enabledIn: [:],
            origin: .claudeDesktopExtension(bundleID: "test"),
            fingerprint: "test",
            isBundled: true,
            definitionSource: "{}"
        )
        server.placeholders = ["__dirname": directory]

        let checker = HealthChecker(shell: testShell, timeout: 20)
        let (health, _) = checker.check(server)

        #expect(health.status == .passed, Comment(rawValue: health.message ?? "no message"))
        #expect(health.toolCount == 3)
        // The displayed command keeps the placeholder — it is what the manifest
        // really says.
        #expect(server.commandSummary.contains("${__dirname}"))
    }

    /// The finding that motivated the state: `mcp-remote` against an OAuth
    /// endpoint prints a consent prompt and blocks, which used to arrive as a
    /// ten-second timeout painted red — on every OAuth-backed remote (§7.3).
    @Test("a server waiting for browser consent is not reported as failed")
    func awaitingAuthorization() throws {
        let checker = HealthChecker(shell: testShell, timeout: 2)
        let (health, weight) = checker.check(fakeServer(mode: "oauth"))

        #expect(health.status == .needsAuthorization)
        #expect(weight == nil)
        #expect(health.failure == .awaitingAuthorization)
        #expect(health.authorizationURL == "https://mcp.example.com/authorize?client_id=abc&state=xyz")
        let message = try #require(health.currentMessage)
        #expect(message.contains("waiting for you to authorize"))
        #expect(!message.lowercased().contains("fail"))
        // The server's own words still travel verbatim (§6).
        #expect(try #require(health.stderr).contains("Please authorize this client"))
    }

    @Test("a crash that merely mentions OAuth stays a failure")
    func oauthMentionIsNotConsent() {
        #expect(!AuthorizationSignal.isAwaiting("""
        Error: token exchange failed (401 Unauthorized)
            at OAuthProvider.refresh (/app/node_modules/oauth/lib/provider.js:88:11)
        See https://docs.example.com/oauth-setup for configuration.
        """))
        #expect(!AuthorizationSignal.isAwaiting("Error: MISSING_API_KEY — set FIRECRAWL_API_KEY"))
        #expect(AuthorizationSignal.isAwaiting("Authentication required. Waiting for authorization..."))
    }

    @Test("the sign-in link is read from the line that asks for it")
    func authorizationURLExtraction() {
        let url = AuthorizationSignal.url(in: """
        [12:01:33] starting proxy
        Please authorize this client by visiting: https://mcp.intercom.com/authorize?client=1&state=2.
        Browser opened automatically.
        """)
        #expect(url == "https://mcp.intercom.com/authorize?client=1&state=2")

        // A link that is not about authorization is not promoted into one.
        #expect(AuthorizationSignal.url(in: "crashed, see https://example.com/help") == nil)
        // http is not something Kytto will ever open for a sign-in.
        #expect(AuthorizationSignal.url(in: "Please authorize by visiting: http://mcp.example.com/authorize") == nil)
    }

    @Test("failure reasons survive a Codable round trip and render at read time")
    func structuredFailureRoundTrip() throws {
        let stored = HealthResult(
            status: .failed,
            checkedAt: Date(timeIntervalSince1970: 1_700_000_000),
            toolCount: nil,
            tools: [],
            // What an older build with worse wording might have frozen in.
            message: "The server did not answer within 0 seconds.",
            stderr: nil,
            durationSeconds: 10,
            serverName: nil,
            serverVersion: nil,
            failure: .timedOut(seconds: 10)
        )
        let decoded = try JSONDecoder().decode(
            HealthResult.self,
            from: JSONEncoder().encode(stored)
        )
        #expect(decoded == stored)
        // The stale snapshot loses to the structured reason.
        #expect(decoded.currentMessage == "The server did not answer within 10 seconds.")

        // A record from before `failure` existed keeps its stored words.
        let legacy = try JSONDecoder().decode(HealthResult.self, from: Data("""
        {"status":"failed","checkedAt":0,"toolCount":null,"tools":[],
         "message":"old wording","stderr":null,"durationSeconds":1,
         "serverName":null,"serverVersion":null}
        """.utf8))
        #expect(legacy.failure == nil)
        #expect(legacy.currentMessage == "old wording")
    }

    @Test("a remote server is reported as unchecked, with the reason")
    func remoteUnsupported() throws {
        let checker = HealthChecker(shell: testShell, timeout: 10)
        let (health, weight) = checker.check(remoteServer())

        #expect(health.status == .unsupported)
        #expect(weight == nil)
        #expect(try #require(health.message).contains("remote"))
    }

    @Test("environment variables reach the server")
    func environmentIsPassed() throws {
        // The `children` mode names its grandchild after KYTTO_TEST_MARKER, which
        // is only possible if env made it across.
        let marker = "kytto-env-probe-\(UUID().uuidString.prefix(8))"
        let server = fakeServer(mode: "children", env: [EnvEntry(key: "KYTTO_TEST_MARKER", value: marker)])

        let checker = HealthChecker(shell: testShell, timeout: 2)
        _ = checker.check(server)

        // Give the kill a moment to land before looking.
        Thread.sleep(forTimeInterval: 1.0)
        #expect(!processExists(matching: marker), "environment did not reach the server, or the child survived")
    }

    /// §6: "kill the process group on timeout". `npx` spawning `node` is the
    /// real shape of this, and killing only the direct child leaves the
    /// grandchild running indefinitely.
    @Test("timing out kills the whole process group, not just the child")
    func killsProcessGroup() throws {
        let marker = "kytto-group-probe-\(UUID().uuidString.prefix(8))"
        let server = fakeServer(mode: "children", env: [EnvEntry(key: "KYTTO_TEST_MARKER", value: marker)])

        let checker = HealthChecker(shell: testShell, timeout: 2)
        let (health, _) = checker.check(server)
        #expect(health.status == .failed)

        Thread.sleep(forTimeInterval: 1.5)
        #expect(!processExists(matching: marker), "a grandchild outlived the health check")
    }

    /// Codex ships `computer-use` with a relative `command` and `cwd = "."`, so this
    /// is what a stock Codex install looks like to Kytto — not an edge case.
    @Test("a relative command is uncheckable, not broken")
    func relativeCommandIsNotAFailure() throws {
        let checker = HealthChecker(shell: testShell, timeout: 10)
        let (health, _) = checker.check(commandServer("./Some App.app/Contents/MacOS/Some App"))

        #expect(health.status == .unsupported, "a path Kytto cannot resolve is not evidence of a broken server")
        let message = try #require(health.message)
        #expect(message.contains("relative path"))
        #expect(!message.contains("is not executable"), "the binary may be perfectly executable where its client runs it")
    }

    @Test("a missing absolute command says where Kytto looked, not that it lacks +x")
    func missingAbsoluteCommand() throws {
        let missing = "/tmp/kytto-does-not-exist-\(UUID().uuidString)/server"
        let checker = HealthChecker(shell: testShell, timeout: 10)
        let (health, _) = checker.check(commandServer(missing))

        #expect(health.status == .failed)
        let message = try #require(health.message)
        #expect(message.contains("was not found at"))
        #expect(message.contains(missing))
        #expect(!message.contains("chmod"), "nothing is there to chmod")
    }

    @Test("a file without the executable bit is named by its absolute path")
    func presentButNotExecutable() throws {
        let directory = URL.temporaryDirectory.appending(path: "kytto-perm-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let script = directory.appending(path: "server")
        try "#!/bin/sh\nexit 0\n".write(to: script, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: script.path)

        let checker = HealthChecker(shell: testShell, timeout: 10)
        let (health, _) = checker.check(commandServer(script.path))

        #expect(health.status == .failed)
        let message = try #require(health.message)
        #expect(message.contains(script.path))
        #expect(message.contains("chmod"), "this is the one case where chmod is the actual fix")
    }
}

/// A stdio server whose command is exactly `command`, for exercising resolution.
private func commandServer(_ command: String) -> Server {
    Server(
        id: Server.identity(for: "resolve-probe"),
        name: "resolve-probe",
        transport: .stdio,
        command: command,
        args: [],
        env: [],
        url: nil,
        enabledIn: [:],
        origin: .configFile(.cursor),
        fingerprint: "test",
        isBundled: false,
        definitionSource: "{}"
    )
}

/// True if any process command line contains `needle`.
private func processExists(matching needle: String) -> Bool {
    let process = Process()
    process.executableURL = URL(filePath: "/bin/ps")
    process.arguments = ["-Ao", "command"]
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = FileHandle.nullDevice
    guard (try? process.run()) != nil else { return false }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    return String(decoding: data, as: UTF8.self).contains(needle)
}

@Suite("TokenWeight")
struct TokenWeightTests {

    @Test("the estimate is characters over four")
    func estimate() {
        #expect(TokenWeight.estimating(String(repeating: "a", count: 400)).estimate == 100)
        #expect(TokenWeight.estimating("").estimate == 0)
    }

    @Test("the percentage is measured against a 200k window")
    func percentage() {
        let weight = TokenWeight.estimating(String(repeating: "a", count: 80_000))
        #expect(weight.estimate == 20_000)
        #expect(abs(weight.percentOfContext - 10) < 0.001)
    }

    @Test("the method is recorded so the UI can call it an estimate")
    func methodIsLabelled() {
        #expect(TokenWeight.estimating("abc").method == "chars/4")
    }
}

@Suite("MetadataStore")
struct MetadataStoreTests {

    @Test("results survive a round trip")
    func roundTrip() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = MetadataStore(paths: KyttoPaths(home: home.root))

        let health = HealthResult(
            status: .passed,
            checkedAt: Date(timeIntervalSince1970: 1_700_000_000),
            toolCount: 3,
            tools: [ToolSummary(name: "a", description: "b")],
            message: nil,
            stderr: "a warning",
            durationSeconds: 1.5,
            serverName: "s",
            serverVersion: "1"
        )
        let weight = TokenWeight.estimating("abcd")

        try store.record(health: health, tokenWeight: weight, for: "github")

        let loaded = try #require(store.metadata(for: "github"))
        #expect(loaded.health == health)
        #expect(loaded.tokenWeight == weight)
    }

    @Test("a failed check keeps the last good token measurement")
    func failureKeepsWeight() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = MetadataStore(paths: KyttoPaths(home: home.root))

        let weight = TokenWeight.estimating(String(repeating: "x", count: 4000))
        try store.record(health: passing(), tokenWeight: weight, for: "github")
        try store.record(health: failing(), tokenWeight: nil, for: "github")

        let loaded = try #require(store.metadata(for: "github"))
        #expect(loaded.health?.status == .failed)
        #expect(loaded.tokenWeight == weight, "a failure says nothing new about size")
    }

    @Test("forgetting a server clears its result")
    func forget() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        let store = MetadataStore(paths: KyttoPaths(home: home.root))

        try store.record(health: passing(), tokenWeight: nil, for: "github")
        try store.forget("github")
        #expect(store.metadata(for: "github") == nil)
    }

    @Test("discovery attaches stored results without running anything")
    func discoveryAttaches() throws {
        let home = try FakeHome()
        defer { home.cleanUp() }
        try home.write(Fixture.cursorMCP.text, to: ".cursor/mcp.json")

        let store = MetadataStore(paths: KyttoPaths(home: home.root))
        try store.record(health: passing(), tokenWeight: TokenWeight.estimating("abcd"), for: "github")

        let result = Discovery(
            home: home.root,
            locator: StubAppLocator(),
            metadata: store.all()
        ).run()

        let github = try #require(result.servers.first { $0.name == "github" })
        #expect(github.health?.status == .passed)
        #expect(github.tokenWeight?.estimate == 1)

        let figma = try #require(result.servers.first { $0.name == "figma" })
        #expect(figma.health == nil, "a server nobody checked stays grey")
    }

    private func passing() -> HealthResult {
        HealthResult(
            status: .passed, checkedAt: Date(), toolCount: 1, tools: [],
            message: nil, stderr: nil, durationSeconds: 0.1,
            serverName: nil, serverVersion: nil
        )
    }

    private func failing() -> HealthResult {
        HealthResult(
            status: .failed, checkedAt: Date(), toolCount: nil, tools: [],
            message: "boom", stderr: "boom", durationSeconds: 0.1,
            serverName: nil, serverVersion: nil
        )
    }
}
