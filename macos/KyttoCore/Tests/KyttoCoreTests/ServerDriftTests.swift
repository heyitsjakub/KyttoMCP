import Testing
@testable import KyttoCore

/// Drift is the evidence under a matrix row: one name, several files, and no
/// guarantee they agree. These assert that a disagreement is found, attributed
/// to the right client, and described without ever naming a secret value.
@Suite("ServerDrift")
struct ServerDriftTests {

    private func definition(
        command: String? = "npx",
        args: [String] = [],
        env: [EnvEntry] = [],
        url: String? = nil,
        transport: Transport = .stdio,
        isBundled: Bool = false
    ) -> ClientDefinition {
        ClientDefinition(
            transport: transport,
            command: command,
            args: args,
            env: env,
            url: url,
            origin: .configFile(.cursor),
            isBundled: isBundled,
            definitionSource: "{}"
        )
    }

    private func server(_ definitions: [ClientID: ClientDefinition]) -> Server {
        var server = Server(
            id: "github",
            name: "github",
            transport: .stdio,
            command: "npx",
            args: [],
            env: [],
            url: nil,
            enabledIn: [:],
            origin: .configFile(.cursor),
            fingerprint: "x",
            isBundled: false,
            definitionSource: "{}"
        )
        server.definitionsByClient = definitions
        return server
    }

    @Test("clients that agree produce no report")
    func noDriftWhenIdentical() {
        let shared = definition(args: ["-y", "server-github"])
        let drift = ServerDrift.detect(in: server([.cursor: shared, .claudeCode: shared]))
        #expect(drift == nil)
    }

    @Test("a single copy cannot disagree with anything")
    func noDriftWithOneClient() {
        #expect(ServerDrift.detect(in: server([.cursor: definition()])) == nil)
    }

    @Test("a different command is attributed to the client holding it")
    func commandDrift() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .cursor: definition(command: "npx"),
                    .claudeCode: definition(command: "bunx"),
                ])
            )
        )

        #expect(drift.fields == [.command])
        #expect(drift.variants.count == 2)
        let cursor = try #require(drift.variants.first { $0.clientIDs == [.cursor] })
        #expect(cursor.definition.command == "npx")
        let claude = try #require(drift.variants.first { $0.clientIDs == [.claudeCode] })
        #expect(claude.definition.command == "bunx")
    }

    @Test("clients sharing a definition are one variant, and the majority leads")
    func majorityVariantFirst() throws {
        let common = definition(args: ["-y", "server-github"])
        let odd = definition(args: ["-y", "server-github@0.1.0"])
        let drift = try #require(
            ServerDrift.detect(
                in: server([.cursor: common, .claudeCode: common, .vsCode: odd])
            )
        )

        #expect(drift.fields == [.arguments])
        #expect(drift.variants.count == 2)
        #expect(drift.variants[0].clientIDs == [.claudeCode, .cursor])
        #expect(drift.variants[1].clientIDs == [.vsCode])
    }

    @Test("a key one client is missing is reported as a key difference")
    func environmentKeyDrift() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .cursor: definition(env: [EnvEntry(key: "TOKEN", value: "abc")]),
                    .claudeCode: definition(env: []),
                ])
            )
        )
        #expect(drift.fields == [.environmentKeys])
    }

    @Test("the same key holding two values is drift, and the values stay out of the report")
    func environmentValueDrift() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .cursor: definition(env: [EnvEntry(key: "TOKEN", value: "ghp_live")]),
                    .claudeCode: definition(env: [EnvEntry(key: "TOKEN", value: "ghp_stale")]),
                ])
            )
        )

        #expect(drift.fields == [.environmentValues])
        // The finding is that they differ. Nothing in the report's own surface
        // spells either value out (§6).
        #expect(!"\(drift.fields)".contains("ghp_"))
        #expect(drift.variants.count == 2)
    }

    @Test("an identical command with different transports still disagrees")
    func transportDrift() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .cursor: definition(command: nil, url: "https://example.com/mcp", transport: .http),
                    .claudeCode: definition(command: nil, url: "https://example.com/mcp", transport: .sse),
                ])
            )
        )
        #expect(drift.fields == [.transport])
    }

    @Test("an installed bundle can be compared but never copied out of")
    func bundledVariantIsNotASource() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .claudeDesktop: definition(command: "node", args: ["${__dirname}/index.js"], isBundled: true),
                    .cursor: definition(command: "npx"),
                ])
            )
        )

        let bundled = try #require(drift.variants.first { $0.clientIDs == [.claudeDesktop] })
        #expect(bundled.canBeSource == false)
        let editable = try #require(drift.variants.first { $0.clientIDs == [.cursor] })
        #expect(editable.canBeSource)
        #expect(drift.unwritableClientIDs == [.claudeDesktop])
    }

    @Test("several fields differing are all reported")
    func multipleFields() throws {
        let drift = try #require(
            ServerDrift.detect(
                in: server([
                    .cursor: definition(command: "npx", args: ["a"], env: [EnvEntry(key: "TOKEN", value: "1")]),
                    .claudeCode: definition(command: "bunx", args: ["b"], env: [EnvEntry(key: "TOKEN", value: "2")]),
                ])
            )
        )
        #expect(drift.fields == [.arguments, .command, .environmentValues])
    }
}
