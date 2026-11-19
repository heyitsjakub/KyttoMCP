import Foundation

public enum ProjectCopyError: Error, LocalizedError {
    case unavailable, stale
    public var errorDescription: String? {
        switch self {
        case .unavailable: "Choose a project definition and an available writable client."
        case .stale: "The source, destination or selection changed. Open a fresh preview."
        }
    }
}

/// Only the review crosses IPC. The draft, digests and binding to the two
/// native files stay here until a single confirmation consumes the plan.
public struct ProjectDefinitionCopy: Sendable {
    public struct Preview: Encodable, Sendable {
        public let token: String?
        public let sourceFile: String
        public let targetFile: String
        public let transport: String
        public let environmentKeys: [String]
        public let unresolvedVariables: [String]
        public let warnings: [String]
        public let blockers: [String]
    }
    public let preview: Preview
    public let draft: ServerDraft
    public let target: ClientID
    public let source: ClientID
    public let serverID: String
    private let sourceDigest: String
    private let targetDigest: String?
    private let destination: URL

    public init(server: Server, source: ClientID, target: ClientID, result: DiscoveryResult,
                home: URL, settings: KyttoSettings, ledger: DigestLedger) throws {
        guard let copy = server.definitionsByClient[source], let location = copy.location,
              result.clients.contains(where: { $0.id == source && $0.isRepositoryFile }),
              result.clients.contains(where: { $0.id == target && !$0.isReadOnly && ($0.isInstalled || $0.configExists) }),
              let descriptor = ClientRegistry.descriptorIfKnown(for: target), let map = descriptor.editableServerMap
        else { throw ProjectCopyError.unavailable }
        let resolver = ClientPathResolver(home: home, overrides: settings.clientPathOverrides)
        let targetURL = resolver.resolveServerMap(map.file, for: target)
        guard let digest = ledger.digest(for: location.file), ConfigWriter.digest(of: location.file) == digest,
              !ledger.hasDrifted(targetURL) else { throw ProjectCopyError.stale }
        sourceDigest = digest
        targetDigest = ledger.digest(for: targetURL)
        let resolvedDestination = targetURL.resolvingSymlinksInPath().standardizedFileURL
        destination = resolvedDestination
        self.target = target
        self.source = source
        serverID = server.id
        draft = ServerDraft(name: location.name, transport: copy.transport, command: copy.command ?? "",
                            args: copy.args, env: copy.env, url: copy.url ?? "")

        var blockers: [String] = []
        var warnings: [String] = []
        let protectedFile = settings.projectFolders.contains { folder in
            ProjectConfigFile.allCases.contains { file in
                URL(filePath: folder.path).appending(path: file.rawValue).resolvingSymlinksInPath().standardizedFileURL.path == resolvedDestination.path
            }
        }
        if protectedFile { blockers.append("The destination points to a read-only project file.") }
        var nameTaken = server.enabledIn[target].map { $0 != .absent } ?? false
        if FileManager.default.fileExists(atPath: targetURL.path) {
            do {
                let text = try String(contentsOf: targetURL, encoding: .utf8)
                switch map.format {
                case .json:
                    let doc = try JSONDocument.parse(text)
                    if doc.root.members == nil || (doc.root[map.serversKey] != nil && doc.root[map.serversKey]?.members == nil)
                        || (doc.root.members ?? []).filter({ $0.key == map.serversKey }).count > 1 {
                        blockers.append("The destination server map is invalid or ambiguous.")
                    }
                    if try doc.serverKey(for: server.id, under: map.serversKey) != nil { nameTaken = true }
                case .toml:
                    if try TOMLDocument.parse(text).serverName(for: server.id, under: map.serversKey) != nil { nameTaken = true }
                }
            } catch { blockers.append("The destination configuration could not be read unambiguously.") }
        }
        if nameTaken { blockers.append("The destination already contains this server name.") }
        // Discovery merges identities for the matrix. Import must still be able
        // to identify exactly one entry in the selected file, including JSONC.
        do {
            let file = try JSONDocument.parse(contentsOf: location.file)
            let maps = (file.root.members ?? []).filter { ["mcpServers", "servers"].contains($0.key) }
            guard maps.count == 1, let entries = maps.first?.value.members,
                  entries.filter({ Server.identity(for: $0.key) == server.id }).count == 1 else {
                throw ProjectCopyError.unavailable
            }
        } catch { blockers.append("The source must contain one unambiguous JSON or JSONC entry for this name.") }
        if let command = copy.command, URL(filePath: command).lastPathComponent == "kytto-mcp-proxy",
           copy.args.contains("--route") {
            blockers.append("A Gateway route belongs to its original client. Choose a direct server definition.")
        }
        if draft.name != draft.trimmedName || draft.command != draft.command.trimmingCharacters(in: .whitespaces)
            || draft.url != draft.url.trimmingCharacters(in: .whitespaces)
            || draft.env.contains(where: { $0.key != $0.key.trimmingCharacters(in: .whitespacesAndNewlines) }) {
            blockers.append("The source contains surrounding whitespace that the destination writer would remove.")
        }
        var references: Set<String> = []
        if let root = try? JSONDocument.parse(copy.definitionSource).root, let fields = root.members {
            let allowed: Set<String> = copy.transport == .stdio ? ["command", "args", "env", "type"] : ["url", "env", "type"]
            let unsupported = Set(fields.map(\.key)).subtracting(allowed).sorted()
            if !unsupported.isEmpty { blockers.append("Unsupported settings: " + unsupported.joined(separator: ", ") + ".") }
            if Set(fields.map(\.key)).count != fields.count || !Self.validFieldTypes(root) {
                blockers.append("The source has duplicate fields or unsupported field values.")
            }
            let substitutions = Self.substitutions(in: root)
            references = substitutions.environment
            if substitutions.requiresProject {
                blockers.append("Project inputs or workspace substitutions require their original project context.")
            }
        } else {
            blockers.append("The project entry must be a JSON or JSONC server definition.")
        }
        if copy.transport == .sse && (!descriptor.serverSchema.acceptsSSE || map.format == .toml || descriptor.serverSchema.shape == .openCode) {
            blockers.append("This destination cannot preserve the SSE transport.")
        }
        if copy.transport != .stdio && !copy.env.isEmpty && (descriptor.serverSchema.shape == .openCode || map.format == .toml) {
            blockers.append("This destination cannot store environment variables on a remote server.")
        }
        if !draft.validate().isEmpty { blockers.append("The server name, launch fields or environment keys cannot be written in this format.") }
        if copy.transport == .stdio && Server.hasRelativeLaunchPath(command: copy.command, args: copy.args) {
            warnings.append("Relative paths will resolve from the destination client’s working directory.")
        }
        references.formUnion(copy.env.filter { !$0.hasValue }.map(\.key))
        if !references.isEmpty {
            warnings.append("Variable references and empty values are copied as written. Check that the destination client can resolve them.")
        }
        preview = Preview(token: blockers.isEmpty ? UUID().uuidString : nil, sourceFile: location.file.path,
                          targetFile: targetURL.path, transport: copy.transport.rawValue, environmentKeys: copy.environmentKeys,
                          unresolvedVariables: references.sorted(), warnings: warnings, blockers: blockers)
    }

    /// Re-resolving against current discovery also catches detached folders,
    /// path overrides, parked-name collisions and retargeted symbolic links.
    public func matches(_ other: Self) -> Bool {
        preview.token != nil && other.preview.token != nil && draft == other.draft &&
        sourceDigest == other.sourceDigest && targetDigest == other.targetDigest &&
        preview.sourceFile == other.preview.sourceFile && preview.targetFile == other.preview.targetFile &&
        destination.path == other.destination.path && source == other.source && target == other.target
    }

    private static func validFieldTypes(_ root: JSONNode) -> Bool {
        for key in ["command", "url", "type"] where root[key] != nil && root[key]?.stringValue == nil { return false }
        if let type = root["type"]?.stringValue, Transport(rawValue: type.lowercased()) == nil { return false }
        if let args = root["args"], args.elements?.allSatisfy({ $0.stringValue != nil }) != true { return false }
        if let env = root["env"] {
            guard let pairs = env.members, Set(pairs.map(\.key)).count == pairs.count,
                  pairs.allSatisfy({ $0.value.stringValue != nil }) else { return false }
        }
        return true
    }

    private static let variablePattern = try! NSRegularExpression(pattern: #"\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}|\$([A-Za-z_][A-Za-z0-9_]*)"#)
    private static func substitutions(in node: JSONNode) -> (environment: Set<String>, requiresProject: Bool) {
        if let value = node.stringValue {
            let matches = variablePattern.matches(in: value, range: NSRange(value.startIndex..., in: value))
            var names = Set(matches.compactMap { match in
                let group = match.range(at: 1).location == NSNotFound ? 2 : 1
                return Range(match.range(at: group), in: value).map { String(value[$0]) }
            })
            let context = ["workspaceFolder", "workspaceFolderBasename", "workspaceRoot", "file", "fileDirname"]
            let scoped = context.filter { value.contains("${" + $0 + "}") }
            names.subtract(scoped)
            let requiresProject = !scoped.isEmpty || ["${input:", "${command:", "${config:", "${workspaceFolder:"].contains(where: value.contains)
            return (names, requiresProject)
        }
        var names: Set<String> = []
        var scoped = false
        for child in node.elements ?? node.members?.map(\.value) ?? [] {
            let nested = substitutions(in: child)
            names.formUnion(nested.environment)
            scoped = scoped || nested.requiresProject
        }
        return (names, scoped)
    }
}
