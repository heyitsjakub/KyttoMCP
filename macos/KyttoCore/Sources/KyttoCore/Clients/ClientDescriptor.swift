import Foundation

public struct ClientID: RawRepresentable, Hashable, CaseIterable, Codable, Sendable {
    public let rawValue: String

    public static let claudeDesktop = ClientID(unchecked: "claudeDesktop")
    public static let claudeCode = ClientID(unchecked: "claudeCode")
    public static let cursor = ClientID(unchecked: "cursor")
    public static let vsCode = ClientID(unchecked: "vsCode")
    public static let codex = ClientID(unchecked: "codex")

    /// `allCases` remains the five clients Kytto owns and may write. Custom
    /// sources are deliberately dynamic and read-only, so they never leak into
    /// backup, authoring or menu-bar write loops that use this collection.
    public static let allCases: [ClientID] = [
        .claudeDesktop, .claudeCode, .cursor, .vsCode, .codex,
    ]

    public init?(rawValue: String) {
        let isBuiltIn = Self.allCases.contains { $0.rawValue == rawValue }
        let customID = rawValue.hasPrefix("custom.")
            ? String(rawValue.dropFirst("custom.".count))
            : nil
        guard isBuiltIn || customID.flatMap(UUID.init(uuidString:)) != nil else { return nil }
        self.rawValue = rawValue
    }

    private init(unchecked rawValue: String) {
        self.rawValue = rawValue
    }

    public static func custom(_ id: UUID) -> ClientID {
        ClientID(unchecked: "custom.\(id.uuidString.lowercased())")
    }

    public var isBuiltIn: Bool { Self.allCases.contains(self) }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let value = ClientID(rawValue: raw) else {
            throw DecodingError.dataCorruptedError(
                in: try decoder.singleValueContainer(),
                debugDescription: "Unknown client id \(raw)."
            )
        }
        self = value
    }

    public func encode(to encoder: any Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

public enum ConfigurationScope: String, CaseIterable, Codable, Sendable {
    case global
    case profile
    case workspace

    public var displayName: String { rawValue.capitalized }
}

/// A user-selected configuration file Kytto may inspect but never mutate.
/// Format and server key are detected from the document so adding a source does
/// not require the user to understand the difference between client schemas.
public struct CustomConfigSource: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    public var displayName: String
    public var path: String
    public var scope: ConfigurationScope
    public var scopeLabel: String

    public init(
        id: UUID = UUID(),
        displayName: String,
        path: String,
        scope: ConfigurationScope = .global,
        scopeLabel: String = ""
    ) {
        self.id = id
        self.displayName = displayName
        self.path = path
        self.scope = scope
        self.scopeLabel = scopeLabel
    }

    public var clientID: ClientID { .custom(id) }
}

/// How a config file is spelled.
///
/// JSON was the only answer until Codex, and the assumption had leaked into the
/// write pipeline rather than being written down. It is data on the source now,
/// because the next client to arrive is as likely to bring a third spelling as
/// to reuse one of these two.
public enum ConfigFormat: String, Sendable {
    /// JSON, tolerating JSONC where the client emits it.
    case json
    case toml

    /// How the format is spelled on screen (§7.8).
    public var displayName: String {
        switch self {
        case .json: return "JSON"
        case .toml: return "TOML"
        }
    }
}

/// How a client turns a server off without deleting it.
///
/// This differs per client and it is the whole substance of the matrix, so it is
/// described as data next to the source it applies to rather than hardcoded in
/// the toggle path.
public enum EnablementStrategy: Sendable {
    /// No mechanism: present means on. Toggling off has to remove the entry, so
    /// M2 will park the definition in Kytto's own store to make it reversible.
    case presence

    /// A list of denied server names in a separate settings file.
    /// Claude Code: `~/.claude/settings.json` → `deniedMcpServers: [{serverName}]`.
    case denyList(file: PlatformPath, key: String, nameField: String)

    /// A per-extension settings file holding a boolean.
    /// Claude Desktop: `Claude Extensions Settings/<id>.json` → `isEnabled`.
    case extensionFlag(key: String)

    /// A boolean on the server's own entry, in the same file.
    /// Codex: `[mcp_servers.<name>]` → `enabled = false`.
    ///
    /// The kindest of the four: the definition never leaves the file, so nothing
    /// has to be parked and switching back on cannot lose anything. Absent means
    /// on, which is why writing the key is only ever needed to turn something off
    /// — though Kytto writes it either way, so the state is visible in the file.
    case inlineFlag(key: String)

    /// What switching a server off in this client actually does to the file, in
    /// one sentence (§7.8).
    ///
    /// Prose about a client belongs next to the client, for the same reason
    /// `schemaQuirks` does: the difference between these four is the substance of
    /// the matrix, and the UI must not be the place it is written down.
    public var offSwitchSummary: String {
        switch self {
        case .presence:
            return """
            This client has no off switch, so switching a server off here removes \
            its definition. Kytto stores the original text first and puts it back \
            byte for byte when you switch it on again.
            """
        case .denyList:
            return """
            Switching a server off adds it to a deny list in a different file. The \
            definition itself stays exactly where it is.
            """
        case .extensionFlag:
            return """
            Each extension carries its own on/off flag in a settings file beside \
            the bundle, so nothing is removed to switch one off.
            """
        case .inlineFlag(let key):
            return """
            Switching a server off writes \(key) = false on the server's own entry, \
            so its definition never leaves the file.
            """
        }
    }
}

/// Where one client keeps its MCP servers.
///
/// A client can have more than one source, and the sources need not share an
/// enablement mechanism. Claude Desktop has two — the `mcpServers` map, which has
/// no off switch, and extension bundles, which have a boolean each. Reading only
/// the first is why a naive implementation shows an empty column on a modern install.
public enum ConfigSource: Sendable {
    /// A map of `name: definition` entries at `serversKey` inside a config file.
    ///
    /// In JSON that is an object; in TOML it is a `[serversKey.<name>]` table per
    /// server. Same idea, and the same one editable map per client.
    case serverMap(
        file: PlatformPath,
        format: ConfigFormat,
        serversKey: String,
        enablement: EnablementStrategy
    )

    /// One directory per extension holding a `manifest.json`, with the on/off
    /// flag in a sibling settings directory.
    case extensionBundles(directory: PlatformPath, settingsDirectory: PlatformPath, flagKey: String)

    /// Servers that arrive inside installed packages, each carrying a config file
    /// in the ordinary `mcpServers` shape somewhere under a root directory.
    ///
    /// Codex plugins. Like Claude Desktop extensions these are installed
    /// software, not something Kytto authors into — the directory is a cache the
    /// client repopulates — so they are read and shown, never written.
    case bundledPackages(root: PlatformPath, manifestName: String, serversKey: String)

    /// A user-selected JSON/JSONC/TOML document. Discovery identifies a known
    /// top-level adapter and reads it; no write service receives this source.
    case readOnlyAutoServerMap(file: PlatformPath)

    /// A read-only server map nested under one explicitly named project in a
    /// client's shared JSON document. Claude Code stores these under
    /// `projects.<path>.mcpServers`.
    case scopedJSONServerMap(file: PlatformPath, scopePath: String, serversKey: String)

    var file: PlatformPath {
        switch self {
        case .serverMap(let file, _, _, _): return file
        case .extensionBundles(let directory, _, _): return directory
        case .bundledPackages(let root, _, _): return root
        case .readOnlyAutoServerMap(let file): return file
        case .scopedJSONServerMap(let file, _, _): return file
        }
    }
}

public struct ClientDescriptor: Sendable {
    public let id: ClientID
    public let displayName: String
    public let iconAsset: String
    /// Used to decide whether the client is actually installed. A leftover
    /// config directory is not evidence — `~/.cursor` outlives an uninstall.
    public let bundleIdentifiers: [String]
    /// For clients that are a CLI rather than an app bundle.
    public let executableNames: [String]
    public let sources: [ConfigSource]
    /// How this client differs from the others. Read this before touching its parsing.
    public let schemaQuirks: String
    public var isReadOnly: Bool = false
    public var configurationScope: ConfigurationScope = .global
    public var scopeLabel: String = ""
    /// A shorter name for surfaces that give the client's identity some other
    /// way. The sidebar's project scopes sit under a Claude Code icon and a
    /// "custom sources" heading, so repeating "Claude Code · " in every row
    /// spends the narrow column on the one part that never differs; this
    /// carries just the distinguishing leaf. Nil means the display name is
    /// already short.
    public var shortName: String? = nil

    /// The file and key holding servers the user can add, edit and remove.
    ///
    /// Every client has exactly one. The other source kinds — Claude Desktop's
    /// extension bundles, Codex's plugins — are installed software, not something
    /// Kytto authors into.
    public var editableServerMap: (
        file: PlatformPath,
        format: ConfigFormat,
        serversKey: String,
        enablement: EnablementStrategy
    )? {
        for source in sources {
            if case .serverMap(let file, let format, let key, let enablement) = source {
                return (file, format, key, enablement)
            }
        }
        return nil
    }

    /// What switching a server off does in this client, for the client screen
    /// (§7.8).
    ///
    /// Composed rather than stored, because a client with two sources answers the
    /// question twice: Claude Desktop deletes a config-file definition to switch
    /// it off and flips a flag to switch an extension off, and a screen that
    /// mentioned only the first would be telling half the truth about the client
    /// where it matters most.
    public var offSwitchSummary: String {
        var parts: [String] = []
        if let editable = editableServerMap {
            parts.append(editable.enablement.offSwitchSummary)
        }
        for source in sources {
            switch source {
            case .serverMap:
                continue
            case .extensionBundles:
                parts.append("""
                Servers that arrive as extension bundles work differently: each \
                carries its own on/off flag beside the bundle, so switching one \
                off removes nothing.
                """)
            case .bundledPackages:
                parts.append("""
                Servers that arrive inside installed plugins are read-only — that \
                directory is a cache the client rewrites, so Kytto shows them and \
                never edits them.
                """)
            case .readOnlyAutoServerMap:
                parts.append("Kytto reads this custom source and never changes it.")
            case .scopedJSONServerMap:
                parts.append("Kytto reads this project/workspace source and never changes it.")
            }
        }
        return parts.joined(separator: " ")
    }
}
