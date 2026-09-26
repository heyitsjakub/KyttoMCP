import Foundation

/// A server the user is describing, before it becomes a definition in a file.
///
/// Kept separate from `Server` because a draft is allowed to be incomplete and
/// wrong — that is the point of validating it. `Server` is always something that
/// really exists in a config.
public struct ServerDraft: Equatable, Sendable {
    public var name: String
    public var transport: Transport
    public var command: String
    public var args: [String]
    public var env: [EnvEntry]
    public var url: String

    public init(
        name: String = "",
        transport: Transport = .stdio,
        command: String = "",
        args: [String] = [],
        env: [EnvEntry] = [],
        url: String = ""
    ) {
        self.name = name
        self.transport = transport
        self.command = command
        self.args = args
        self.env = env
        self.url = url
    }

    /// Pre-fills a draft from a server that already exists, for editing.
    public init(editing server: Server) {
        name = server.name
        transport = server.transport
        command = server.command ?? ""
        args = server.args
        env = server.env
        url = server.url ?? ""
    }

    public var trimmedName: String {
        name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Fills in environment values the editor was never given.
    ///
    /// The UI only ever sees `{ key, hasValue }` — an API token does not cross
    /// the IPC boundary just because someone opened the edit form (§6). So a
    /// value of `nil` coming back means "leave this one as it was", and the real
    /// value is looked up here, natively, at the last moment.
    public func mergingSecrets(from server: Server) -> ServerDraft {
        let existing = Dictionary(
            server.env.map { ($0.key, $0.value) },
            uniquingKeysWith: { first, _ in first }
        )
        var merged = self
        merged.env = env.map { entry in
            guard entry.value == nil else { return entry }
            return EnvEntry(key: entry.key, value: existing[entry.key] ?? nil)
        }
        return merged
    }

    // MARK: - Validation

    public enum ValidationError: Error, Equatable, LocalizedError {
        case nameEmpty
        case nameHasWhitespace
        case nameTaken(String)
        case commandEmpty
        case urlEmpty
        case urlInvalid(String)
        case envKeyEmpty
        case envKeyInvalid(String)

        public var errorDescription: String? {
            switch self {
            case .nameEmpty:
                "Give the server a name."
            case .nameHasWhitespace:
                "Server names cannot contain spaces — they are used as keys in the config file."
            case .nameTaken(let name):
                "\"\(name)\" already exists in one of the selected clients."
            case .commandEmpty:
                "A stdio server needs a command to run."
            case .urlEmpty:
                "An HTTP or SSE server needs a URL."
            case .urlInvalid(let url):
                "\"\(url)\" is not a valid URL."
            case .envKeyEmpty:
                "An environment variable has no name."
            case .envKeyInvalid(let key):
                "\"\(key)\" is not a valid environment variable name. Use letters, numbers and underscores, and do not start with a number."
            }
        }
    }

    /// - Parameter takenNames: names already present in the clients being written
    ///   to, normalized. Pass the original name in `allowing` when editing, so a
    ///   server does not collide with itself.
    public func validate(takenNames: Set<String> = [], allowing existingName: String? = nil) -> [ValidationError] {
        var errors: [ValidationError] = []

        let name = trimmedName
        if name.isEmpty {
            errors.append(.nameEmpty)
        } else if name.rangeOfCharacter(from: .whitespacesAndNewlines) != nil {
            errors.append(.nameHasWhitespace)
        } else {
            let identity = Server.identity(for: name)
            let allowed = existingName.map(Server.identity(for:))
            if takenNames.contains(identity), identity != allowed {
                errors.append(.nameTaken(name))
            }
        }

        switch transport {
        case .stdio:
            if command.trimmingCharacters(in: .whitespaces).isEmpty {
                errors.append(.commandEmpty)
            }
        case .http, .sse:
            let url = self.url.trimmingCharacters(in: .whitespaces)
            if url.isEmpty {
                errors.append(.urlEmpty)
            } else if !Self.isValidRemoteURL(url) {
                errors.append(.urlInvalid(url))
            }
        }

        for entry in env {
            let key = entry.key.trimmingCharacters(in: .whitespacesAndNewlines)
            if key.isEmpty {
                errors.append(.envKeyEmpty)
            } else if !Self.isValidEnvironmentKey(key) {
                errors.append(.envKeyInvalid(key))
            }
        }

        return errors
    }

    private static func isValidRemoteURL(_ text: String) -> Bool {
        guard let components = URLComponents(string: text),
              let scheme = components.scheme?.lowercased(),
              scheme == "http" || scheme == "https",
              let host = components.host,
              !host.isEmpty
        else { return false }
        return true
    }

    private static func isValidEnvironmentKey(_ key: String) -> Bool {
        guard let first = key.unicodeScalars.first,
              CharacterSet.letters.union(CharacterSet(charactersIn: "_")).contains(first)
        else { return false }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_"))
        return key.unicodeScalars.dropFirst().allSatisfy(allowed.contains)
    }

    // MARK: - Rendering

    /// The definition as it will appear in a config file.
    ///
    /// `type` is written only for HTTP and SSE. Every client understands a bare
    /// `command` as stdio, but not all of them have always understood
    /// `"type": "stdio"`, so the quieter form is the portable one.
    public func definition(baseIndent: String, unit: String) -> String {
        var fields: [JSONBuildValue.Field] = []

        switch transport {
        case .stdio:
            fields.append(.init("command", .string(command.trimmingCharacters(in: .whitespaces))))
            if !args.isEmpty {
                fields.append(.init("args", .array(args.map { .string($0) })))
            }
        case .http, .sse:
            fields.append(.init("type", .string(transport.rawValue)))
            fields.append(.init("url", .string(url.trimmingCharacters(in: .whitespaces))))
        }

        let realEnv = env.filter { !$0.key.trimmingCharacters(in: .whitespaces).isEmpty }
        if !realEnv.isEmpty {
            fields.append(
                .init("env", .object(realEnv.map { .init($0.key, .string($0.value ?? "")) }))
            )
        }

        return JSONBuilder.render(.object(fields), baseIndent: baseIndent, unit: unit)
    }

    /// The same definition, spelled as a TOML table block.
    ///
    /// Transport is carried by which key is present rather than by a `type`
    /// field: Codex reads `url` as remote and `command` as stdio, and writing
    /// both would be describing two servers.
    ///
    /// - Parameter enabled: pass `false` only to keep a server that is already
    ///   switched off switched off. Editing a definition must not quietly turn
    ///   it back on, and absent means on.
    public func tomlBlock(under key: String, enabled: Bool?) -> String {
        let isRemote = transport != .stdio
        return TOMLBuilder.serverBlock(
            name: trimmedName,
            under: key,
            command: isRemote ? nil : command.trimmingCharacters(in: .whitespaces),
            args: args,
            url: isRemote ? url.trimmingCharacters(in: .whitespaces) : nil,
            env: env,
            enabled: enabled
        )
    }
}
