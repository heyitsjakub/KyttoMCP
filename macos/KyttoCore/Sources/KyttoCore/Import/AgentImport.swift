import Foundation

/// The deliberately small, local-only JSON contract used when an agent
/// describes an unsupported MCP server. Environment values are not part of the
/// contract: an agent may name required keys, but it cannot smuggle credentials
/// into a write.
public struct AgentImportDocument: Sendable {
    public let servers: [AgentImportServer]

    public init(json text: String) throws {
        guard !Self.containsTrailingComma(text) else { throw AgentImportError.invalidJSON }
        guard let data = text.data(using: .utf8),
              (try? JSONSerialization.jsonObject(with: data)) is [String: Any]
        else { throw AgentImportError.invalidJSON }

        let document: JSONDocument
        do {
            document = try JSONDocument.parse(text)
        } catch {
            throw AgentImportError.invalidJSON
        }

        guard let rootMembers = document.root.members,
              Self.unique(rootMembers.map(\.key))
        else { throw AgentImportError.unknownFields(["<duplicate top-level key>"]) }

        let allowedRoot = Set(["schemaVersion", "servers"])
        let unknownRoot = rootMembers.map(\.key).filter { !allowedRoot.contains($0) }
        guard unknownRoot.isEmpty else { throw AgentImportError.unknownFields(unknownRoot) }
        guard document.root["schemaVersion"]?.numberValue == 1 else {
            throw AgentImportError.unsupportedSchema
        }
        guard let serverNodes = document.root["servers"]?.elements else {
            throw AgentImportError.missingField("servers")
        }

        var seenNames: Set<String> = []
        var parsed: [AgentImportServer] = []
        for (index, node) in serverNodes.enumerated() {
            let path = "servers[\(index)]"
            guard let members = node.members else {
                throw AgentImportError.invalidField(path, "expected an object")
            }
            guard Self.unique(members.map(\.key)) else {
                throw AgentImportError.invalidField(path, "contains duplicate fields")
            }

            let allowed = Set(["name", "transport", "command", "args", "url", "env"])
            let unknown = members.map(\.key).filter { !allowed.contains($0) }
            guard unknown.isEmpty else {
                throw AgentImportError.unknownFields(unknown.map { "\(path).\($0)" })
            }

            guard let name = node["name"]?.stringValue,
                  !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            else { throw AgentImportError.missingField("\(path).name") }
            guard Self.isSafe(name) else {
                throw AgentImportError.invalidField("\(path).name", "contains control characters")
            }
            let identity = Server.identity(for: name)
            guard seenNames.insert(identity).inserted else {
                throw AgentImportError.duplicateServer(name)
            }

            guard let transportRaw = node["transport"]?.stringValue,
                  let transport = Transport(rawValue: transportRaw.lowercased())
            else { throw AgentImportError.invalidField("\(path).transport", "expected stdio, http or sse") }

            let command = node["command"]?.stringValue ?? ""
            let url = node["url"]?.stringValue ?? ""
            let args = try Self.strings(node["args"], field: "\(path).args")
            let envKeys = try Self.strings(node["env"], field: "\(path).env")

            guard Self.unique(envKeys) else {
                throw AgentImportError.invalidField("\(path).env", "contains duplicate keys")
            }
            guard envKeys.allSatisfy(Self.isSafeEnvironmentKey) else {
                throw AgentImportError.invalidField("\(path).env", "contains an unsafe environment key")
            }
            guard [command, url].allSatisfy(Self.isSafe) && args.allSatisfy(Self.isSafe) else {
                throw AgentImportError.invalidField(path, "contains control characters")
            }

            let draft = ServerDraft(
                name: name,
                transport: transport,
                command: command,
                args: args,
                env: envKeys.map { EnvEntry(key: $0, value: nil) },
                url: url
            )
            let validation = draft.validate()
            guard validation.isEmpty else {
                throw AgentImportError.invalidField(
                    path,
                    validation.map { $0.errorDescription ?? "invalid value" }.joined(separator: " ")
                )
            }

            parsed.append(
                AgentImportServer(name: name, transport: transport, command: command, args: args, url: url, envKeys: envKeys)
            )
        }

        guard !parsed.isEmpty else { throw AgentImportError.invalidField("servers", "must contain at least one server") }
        servers = parsed
    }

    private static func unique(_ values: [String]) -> Bool {
        Set(values).count == values.count
    }

    /// `JSONDocument` also reads the JSONC-shaped files used by clients. The
    /// import contract is deliberately stricter, so reject trailing commas
    /// before handing the text to that shared parser.
    private static func containsTrailingComma(_ text: String) -> Bool {
        var inString = false
        var escaped = false
        let scalars = Array(text.unicodeScalars)
        for index in scalars.indices {
            let scalar = scalars[index]
            if inString {
                if escaped {
                    escaped = false
                } else if scalar == "\\" {
                    escaped = true
                } else if scalar == "\"" {
                    inString = false
                }
                continue
            }
            if scalar == "\"" {
                inString = true
                continue
            }
            guard scalar == "," else { continue }
            var next = index + 1
            while next < scalars.count && CharacterSet.whitespacesAndNewlines.contains(scalars[next]) {
                next += 1
            }
            if next < scalars.count && (scalars[next] == "}" || scalars[next] == "]") {
                return true
            }
        }
        return false
    }

    private static func strings(_ node: JSONNode?, field: String) throws -> [String] {
        guard let node else { return [] }
        guard let elements = node.elements else {
            throw AgentImportError.invalidField(field, "expected an array of strings")
        }
        let values = elements.compactMap(\.stringValue)
        guard values.count == elements.count else {
            throw AgentImportError.invalidField(field, "expected an array of strings")
        }
        return values
    }

    private static func isSafe(_ value: String) -> Bool {
        value.unicodeScalars.allSatisfy { $0.value >= 0x20 && $0.value != 0x7F }
    }

    private static func isSafeEnvironmentKey(_ value: String) -> Bool {
        guard let first = value.unicodeScalars.first,
              CharacterSet.letters.union(CharacterSet(charactersIn: "_")).contains(first)
        else { return false }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_"))
        return value.unicodeScalars.dropFirst().allSatisfy(allowed.contains)
    }
}

public struct AgentImportServer: Sendable {
    public let name: String
    public let transport: Transport
    public let command: String
    public let args: [String]
    public let url: String
    public let envKeys: [String]

    public var draft: ServerDraft {
        ServerDraft(
            name: name,
            transport: transport,
            command: command,
            args: args,
            // Keys are shown in the preview, but values are intentionally not
            // invented or written as empty strings during import.
            env: [],
            url: url
        )
    }
}

public enum AgentImportError: Error, LocalizedError, Equatable, Sendable {
    case invalidJSON
    case unsupportedSchema
    case missingField(String)
    case unknownFields([String])
    case duplicateServer(String)
    case invalidField(String, String)

    public var errorDescription: String? {
        switch self {
        case .invalidJSON:
            return "The import is not strict JSON. Comments, trailing commas and secret values are not accepted."
        case .unsupportedSchema:
            return "The import must declare schemaVersion 1."
        case .missingField(let field):
            return "The import is missing required field \(field)."
        case .unknownFields(let fields):
            return "The import contains unknown fields: \(fields.joined(separator: ", "))."
        case .duplicateServer(let name):
            return "The import contains the server \"\(name)\" more than once."
        case .invalidField(let field, let reason):
            return "\(field) is invalid: \(reason)."
        }
    }
}
