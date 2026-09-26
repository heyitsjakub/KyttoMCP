import Foundation

public enum Transport: String, Codable, Sendable {
    case stdio
    case http
    case sse
}

/// Whether a server is on in a given client.
public enum Enablement: String, Codable, Sendable {
    /// Configured and active.
    case enabled
    /// Configured but switched off — Claude Code's deny list, or an extension
    /// with `isEnabled: false`. Still listed, shown as off.
    case disabled
    /// Not configured in this client at all.
    case absent
}

/// One environment variable as it appears in a config.
///
/// The value is kept because M5 needs it to spawn the process, but it never
/// crosses the IPC boundary — see `ServerDTO` (§6, and groundwork for M4).
public struct EnvEntry: Equatable, Sendable {
    public let key: String
    public let value: String?

    public init(key: String, value: String?) {
        self.key = key
        self.value = value
    }

    public var hasValue: Bool { !(value ?? "").isEmpty }
}

/// Where a server definition was found.
public enum Origin: Equatable, Sendable {
    case configFile(ClientID)
    case claudeDesktopExtension(bundleID: String)
}

/// One client's copy of a definition, as that client actually has it.
///
/// The matrix merges a name into one row, which is the whole point of it — but
/// the merge is also where the evidence goes. Two clients can disagree about
/// what "github" runs, and until this existed the disagreement was noticed
/// (`Discovery` raises a diagnostic) and then thrown away: the row kept whichever
/// definition was read first and nothing could say which client held the other
/// one. Drift is not an edge case in a tool whose subject is several copies of
/// the same configuration, so each copy is kept.
public struct ClientDefinition: Equatable, Sendable {
    public let transport: Transport
    public let command: String?
    public let args: [String]
    public let env: [EnvEntry]
    public let url: String?
    public let origin: Origin
    /// Extensions and plugins are installed software, not configuration. They can
    /// take part in a comparison and never in a write.
    public let isBundled: Bool
    /// This client's definition verbatim, so unifying moves exact bytes rather
    /// than a re-serialization (§6).
    public let definitionSource: String

    public init(
        transport: Transport,
        command: String?,
        args: [String],
        env: [EnvEntry],
        url: String?,
        origin: Origin,
        isBundled: Bool,
        definitionSource: String
    ) {
        self.transport = transport
        self.command = command
        self.args = args
        self.env = env
        self.url = url
        self.origin = origin
        self.isBundled = isBundled
        self.definitionSource = definitionSource
    }

    /// The copy a single client holds, taken from the server as that client's
    /// source produced it — before any merging.
    public init(from server: Server) {
        self.init(
            transport: server.transport,
            command: server.command,
            args: server.args,
            env: server.env,
            url: server.url,
            origin: server.origin,
            isBundled: server.isBundled,
            definitionSource: server.definitionSource
        )
    }

    public var fingerprint: String {
        Server.fingerprint(command: command, args: args, url: url)
    }

    /// Environment keys, normalized for comparison. Keys only: a value that
    /// differs is reported as the fact that it differs, never as itself (§6).
    public var environmentKeys: [String] {
        env.map(\.key).sorted()
    }
}

/// The normalized record every client's config is mapped onto (§5).
///
/// Config files stay authoritative for what is enabled; this is a read-through
/// view of them, not a store.
public struct Server: Identifiable, Sendable {
    public let id: String
    public let name: String
    public let transport: Transport
    public let command: String?
    public let args: [String]
    public let env: [EnvEntry]
    public let url: String?
    public var enabledIn: [ClientID: Enablement]
    public let origin: Origin
    /// Identifies the same server configured under different names. Not used to
    /// merge rows in M1 — only to flag when one name means two different commands.
    public let fingerprint: String
    /// Bundled servers (Claude Desktop extensions) ship their own command and
    /// are not editable in M3.
    public let isBundled: Bool
    /// The definition as each client has it, separately.
    ///
    /// The top-level `command`, `args` and `env` are the merged view — enough to
    /// run the server, and the reason a row is a row. This is the evidence
    /// underneath it: the same server written into three files, where it can
    /// quietly drift out of step in one of them.
    public var definitionsByClient: [ClientID: ClientDefinition] = [:]

    /// The environment as each client has it. Derived, so there is one place a
    /// client's copy of anything is recorded.
    public var envByClient: [ClientID: [EnvEntry]] {
        definitionsByClient.mapValues(\.env)
    }
    /// Substitutions the owning client makes before it runs this server.
    ///
    /// Claude Desktop extensions ship commands full of `${__dirname}` and
    /// `${user_config.something}`, which only Claude Desktop expands. Kytto knows
    /// what they mean — the bundle directory and the extension's own settings —
    /// so it can expand them too, and without that a health check on an
    /// extension can only ever fail.
    ///
    /// Applied when running, never when displaying: the matrix shows the command
    /// as it is written in the manifest, because that is what is really there.
    public var placeholders: [String: String] = [:]
    /// The last health check, if one has ever been run (§7.3).
    ///
    /// Never populated automatically: §6 forbids spawning on launch, so a server
    /// nobody has checked stays honestly grey.
    public var health: HealthResult?
    /// The last token measurement (§7.4).
    public var tokenWeight: TokenWeight?
    /// The definition's verbatim JSON source, as found.
    ///
    /// Kept so that parking a server and bringing it back, or copying it into
    /// another client, moves the exact bytes rather than a re-serialization of
    /// the model. Anything Kytto's model does not understand travels with it.
    public let definitionSource: String

    public init(
        id: String,
        name: String,
        transport: Transport,
        command: String?,
        args: [String],
        env: [EnvEntry],
        url: String?,
        enabledIn: [ClientID: Enablement],
        origin: Origin,
        fingerprint: String,
        isBundled: Bool,
        definitionSource: String,
        health: HealthResult? = nil,
        tokenWeight: TokenWeight? = nil
    ) {
        self.health = health
        self.tokenWeight = tokenWeight
        self.id = id
        self.name = name
        self.transport = transport
        self.command = command
        self.args = args
        self.env = env
        self.url = url
        self.enabledIn = enabledIn
        self.origin = origin
        self.fingerprint = fingerprint
        self.isBundled = isBundled
        self.definitionSource = definitionSource
    }

    /// Identity across clients: the name, normalized. Two clients listing
    /// "github" are showing the same row of the matrix.
    public static func identity(for name: String) -> String {
        name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    /// A cheap, stable hash of what actually runs, used to spot two servers
    /// sharing a name but not a command.
    ///
    /// FNV-1a rather than `hashValue`, which Swift seeds randomly per process —
    /// this needs to mean the same thing across launches once M2 persists it.
    public static func fingerprint(command: String?, args: [String], url: String?) -> String {
        let parts = [command ?? "", args.joined(separator: "\u{1}"), url ?? ""]
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in parts.joined(separator: "\u{2}").utf8 {
            hash ^= UInt64(byte)
            hash &*= 0x100000001b3
        }
        return String(format: "%016llx", hash)
    }

    /// The command with every placeholder the client would substitute resolved.
    public var runnable: (command: String?, args: [String]) {
        (expand(command), args.map { expand($0) ?? $0 })
    }

    private func expand(_ text: String?) -> String? {
        guard var result = text, !placeholders.isEmpty else { return text }
        for (token, value) in placeholders {
            result = result.replacingOccurrences(of: "${\(token)}", with: value)
        }
        return result
    }

    /// Single-line command for the matrix subtitle.
    public var commandSummary: String {
        if let url { return url }
        guard let command else { return "" }
        return ([command] + args).joined(separator: " ")
    }

    /// Whether anything this server is launched through is resolved against a
    /// working directory rather than named outright (§4).
    ///
    /// It matters at exactly one moment: copying the definition into another
    /// client. A relative path is resolved against whatever directory launched
    /// the client, so it finds its binary from where the client that already has
    /// it happens to run and nowhere else. Moved elsewhere the definition
    /// parses, writes, backs up and reads back perfectly, and then the server
    /// does not start — the one outcome where every check Kytto makes passes and
    /// the user still gets nothing. So the matrix says so before the click and
    /// again after it.
    ///
    /// Deliberately narrow. A bare program name is not a relative path — `npx`
    /// goes through PATH, which no client owns — and an argument is only counted
    /// when it opens with `./` or `../`, which is unambiguous. Anything looser
    /// flags `@modelcontextprotocol/server-github`, and a warning that cries
    /// wolf on half the matrix is worse than no warning at all.
    public var hasRelativePath: Bool {
        guard transport == .stdio else { return false }
        if let command, !command.isEmpty, Self.containsSeparator(command),
           !Self.isAbsolute(command) {
            return true
        }
        return args.contains { Self.opensWithRelativePrefix($0) }
    }

    private static func containsSeparator(_ text: String) -> Bool {
        text.contains("/") || text.contains(#"\"#)
    }

    private static func isAbsolute(_ text: String) -> Bool {
        // POSIX roots, the shell's home shorthand, a UNC share, and a Windows
        // drive letter in either slash. Windows is M7 but the registry already
        // carries its paths, and a rule that only knows one platform is the kind
        // of thing §3.2 exists to prevent.
        if text.hasPrefix("/") || text.hasPrefix("~") || text.hasPrefix(#"\\"#) { return true }
        let afterDrive = text.dropFirst()
        return afterDrive.hasPrefix(#":\"#) || afterDrive.hasPrefix(":/")
    }

    private static func opensWithRelativePrefix(_ text: String) -> Bool {
        ["./", "../", #".\"#, #"..\"#].contains { text.hasPrefix($0) }
    }
}
