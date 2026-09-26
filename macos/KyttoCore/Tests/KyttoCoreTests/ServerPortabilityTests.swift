import Foundation
import Testing
@testable import KyttoCore

/// The rule behind the matrix's "this will probably not start here" warning.
///
/// The failure it guards against is silent: a relative command copied into
/// another client writes cleanly and then never runs. The tests worth having are
/// the ones that keep the rule *narrow* — a warning that fires on `npx -y
/// @modelcontextprotocol/server-github` would be ignored within a day, and then
/// it would not be there for the case it was built for.
@Suite("Server — relative paths")
struct ServerPortabilityTests {

    private func server(
        command: String?,
        args: [String] = [],
        transport: Transport = .stdio,
        url: String? = nil
    ) -> Server {
        Server(
            id: "s",
            name: "s",
            transport: transport,
            command: command,
            args: args,
            env: [],
            url: url,
            enabledIn: [:],
            origin: .configFile(.codex),
            fingerprint: "",
            isBundled: false,
            definitionSource: ""
        )
    }

    @Test("a command resolved through PATH travels")
    func bareCommand() {
        #expect(server(command: "npx", args: ["-y", "xcodebuildmcp@latest", "mcp"]).hasRelativePath == false)
        #expect(server(command: "node").hasRelativePath == false)
        #expect(server(command: "uvx", args: ["mcp-server-git"]).hasRelativePath == false)
    }

    @Test("an absolute command travels")
    func absoluteCommand() {
        #expect(server(command: "/Applications/ChatGPT.app/Contents/Resources/node").hasRelativePath == false)
        #expect(server(command: "~/.local/bin/mcp-server").hasRelativePath == false)
        #expect(server(command: #"C:\Program Files\nodejs\node.exe"#).hasRelativePath == false)
        #expect(server(command: "C:/Program Files/nodejs/node.exe").hasRelativePath == false)
        #expect(server(command: #"\\share\tools\mcp.exe"#).hasRelativePath == false)
    }

    /// The case this exists for, verbatim from a real `~/.codex/config.toml`.
    @Test("a relative command does not")
    func relativeCommand() {
        let codexComputerUse = server(
            command: "./Codex Computer Use.app/Contents/SharedSupport/SkyComputerUseClient.app/Contents/MacOS/SkyComputerUseClient",
            args: ["mcp"]
        )
        #expect(codexComputerUse.hasRelativePath)
        #expect(server(command: "bin/server").hasRelativePath)
        #expect(server(command: #"..\tools\server.exe"#).hasRelativePath)
    }

    @Test("a relative argument counts, a scoped package name does not")
    func arguments() {
        #expect(server(command: "node", args: ["./server/index.js"]).hasRelativePath)
        #expect(server(command: "node", args: ["../dist/index.js"]).hasRelativePath)
        #expect(server(command: "npx", args: ["-y", "@modelcontextprotocol/server-github"]).hasRelativePath == false)
        #expect(server(command: "npx", args: ["-y", "mcp-server", "--root", "src/lib"]).hasRelativePath == false)
    }

    /// A remote server has no working directory to be wrong about, and a URL
    /// with a relative-looking path in it is still fetched from its own host.
    @Test("remote servers are never flagged")
    func remoteTransport() {
        #expect(
            server(command: nil, transport: .http, url: "https://mcp.context7.com/mcp")
                .hasRelativePath == false
        )
    }
}
