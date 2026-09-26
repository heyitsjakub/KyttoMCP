import Foundation

public struct MCPLibraryCandidate: Identifiable, Sendable {
    public let id: UUID
    public let name: String
    public let location: String
    public let sourcePath: String
    public let commandSummary: String
    public let transport: Transport
    public let envKeys: [String]
    public let detectedClientIDs: [ClientID]
    public let missingClientIDs: [ClientID]
    public let warnings: [String]
    /// Kept native so environment values never need to cross IPC for an import.
    public let draft: ServerDraft

    public init(
        id: UUID = UUID(),
        name: String,
        location: String,
        sourcePath: String,
        draft: ServerDraft,
        detectedClientIDs: [ClientID],
        warnings: [String]
    ) {
        self.id = id
        self.name = name
        self.location = location
        self.sourcePath = sourcePath
        self.commandSummary = draft.transport == .stdio
            ? ([draft.command] + draft.args).joined(separator: " ")
            : draft.url
        self.transport = draft.transport
        self.envKeys = draft.env.map(\.key)
        self.detectedClientIDs = detectedClientIDs
        self.missingClientIDs = ClientID.allCases.filter { !detectedClientIDs.contains($0) }
        self.warnings = warnings
        self.draft = draft
    }
}

public struct MCPLibraryScanResult: Sendable {
    public let rootPath: String
    public let candidates: [MCPLibraryCandidate]
    public let warnings: [String]

    public init(rootPath: String, candidates: [MCPLibraryCandidate], warnings: [String]) {
        self.rootPath = rootPath
        self.candidates = candidates
        self.warnings = warnings
    }
}

/// Inspects one user-selected directory without executing anything it finds.
/// The scanner is intentionally conservative: known MCP maps produce candidates;
/// ordinary entry points are only suggestions with an explicit warning.
public struct MCPLibraryScanner: Sendable {
    private let root: URL

    public init(root: URL) {
        self.root = root.standardizedFileURL
    }

    public func scan() -> MCPLibraryScanResult {
        guard isDirectory(root) else {
            return MCPLibraryScanResult(rootPath: root.path, candidates: [], warnings: ["Choose an existing directory."])
        }

        var candidates: [MCPLibraryCandidate] = []
        var warnings: [String] = []
        var seen: Set<String> = []
        let baseDepth = root.pathComponents.count

        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey],
            options: []
        ) else {
            return MCPLibraryScanResult(rootPath: root.path, candidates: [], warnings: ["Kytto could not read this directory."])
        }

        var inspected = 0
        while let url = enumerator.nextObject() as? URL {
            inspected += 1
            if inspected > 5_000 {
                warnings.append("The directory is large; scanning stopped after 5,000 entries.")
                break
            }

            let relative = relativePath(url)
            let depth = url.pathComponents.count - baseDepth
            if depth > 6 {
                if isDirectory(url) { enumerator.skipDescendants() }
                continue
            }
            if isSkippedDirectory(url) {
                enumerator.skipDescendants()
                continue
            }
            guard isRegularFile(url) else { continue }

            let fileName = url.lastPathComponent.lowercased()
            if ["mcp.json", "mcp.jsonc", "claude_desktop_config.json"].contains(fileName) ||
                ["json", "jsonc"].contains(url.pathExtension.lowercased()) {
                let found = scanJSON(url: url, relativePath: relative)
                for candidate in found where seen.insert("\(candidate.name)|\(candidate.sourcePath)").inserted {
                    candidates.append(candidate)
                }
            } else if url.pathExtension.lowercased() == "toml" {
                let found = scanTOML(url: url, relativePath: relative)
                for candidate in found where seen.insert("\(candidate.name)|\(candidate.sourcePath)").inserted {
                    candidates.append(candidate)
                }
            }

            if fileName == "package.json" {
                if let candidate = scanNodeEntryPoint(url: url, relativePath: relative),
                   seen.insert("\(candidate.name)|\(candidate.sourcePath)").inserted {
                    candidates.append(candidate)
                }
            }

            if ["server.py", "mcp_server.py", "main.py"].contains(fileName), depth <= 2 {
                let candidate = entryPointCandidate(
                    name: url.deletingPathExtension().lastPathComponent,
                    sourcePath: url.path,
                    relativePath: relative,
                    command: "python3",
                    args: [relative],
                    detected: detectedClients(for: url),
                    warning: "This is an entry-point suggestion. Kytto did not execute it or verify that it speaks MCP."
                )
                if seen.insert("\(candidate.name)|\(candidate.sourcePath)").inserted { candidates.append(candidate) }
            }
        }

        if candidates.isEmpty {
            warnings.append("No tested MCP definition or likely entry point was found. Kytto did not execute any file.")
        }
        return MCPLibraryScanResult(rootPath: root.path, candidates: candidates.sorted {
            ($0.name.lowercased(), $0.sourcePath) < ($1.name.lowercased(), $1.sourcePath)
        }, warnings: warnings)
    }

    private func scanJSON(url: URL, relativePath: String) -> [MCPLibraryCandidate] {
        guard let text = try? String(contentsOf: url, encoding: .utf8),
              let document = try? JSONDocument.parse(text)
        else { return [] }
        let keys = ["mcpServers", "servers"].filter { document.root[$0]?.members != nil }
        guard keys.count == 1, let key = keys.first, let members = document.root[key]?.members else { return [] }
        return members.compactMap { member in
            guard member.value.members != nil else { return nil }
            let fields = ServerFields(node: member.value)
            let draft = ServerDraft(
                name: member.key,
                transport: fields.resolveTransportForLibrary(),
                command: fields.command ?? "",
                args: fields.args,
                env: fields.env,
                url: fields.url ?? ""
            )
            guard draft.validate().isEmpty else { return nil }
            return MCPLibraryCandidate(
                name: member.key,
                location: relativePath,
                sourcePath: url.path,
                draft: draft,
                detectedClientIDs: detectedClients(for: url),
                warnings: []
            )
        }
    }

    private func scanTOML(url: URL, relativePath: String) -> [MCPLibraryCandidate] {
        guard let text = try? String(contentsOf: url, encoding: .utf8),
              let document = try? TOMLDocument.parse(text)
        else { return [] }
        let keys = ["mcp_servers", "mcpServers", "servers"].filter { !document.serverNames(under: $0).isEmpty }
        guard keys.count == 1, let key = keys.first else { return [] }
        return document.serverNames(under: key).compactMap { name in
            guard let table = document.table(at: [key, name]) else { return nil }
            let fields = ServerFields(table: table, envTable: document.table(at: [key, name, "env"]))
            let draft = ServerDraft(
                name: name,
                transport: fields.resolveTransportForLibrary(),
                command: fields.command ?? "",
                args: fields.args,
                env: fields.env,
                url: fields.url ?? ""
            )
            guard draft.validate().isEmpty else { return nil }
            return MCPLibraryCandidate(
                name: name,
                location: relativePath,
                sourcePath: url.path,
                draft: draft,
                detectedClientIDs: detectedClients(for: url),
                warnings: []
            )
        }
    }

    private func scanNodeEntryPoint(url: URL, relativePath: String) -> MCPLibraryCandidate? {
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        let entry: String?
        if let main = object["main"] as? String { entry = main }
        else if let bin = object["bin"] as? String { entry = bin }
        else if let bins = object["bin"] as? [String: Any], let first = bins.values.first as? String { entry = first }
        else { entry = nil }
        guard let entry, !entry.isEmpty else { return nil }
        let name = (object["name"] as? String)?.split(separator: "/").last.map(String.init)
            ?? url.deletingLastPathComponent().lastPathComponent
        return entryPointCandidate(
            name: name,
            sourcePath: url.path,
            relativePath: relativePath,
            command: "node",
            args: [relativePath == "package.json" ? entry : "\(url.deletingLastPathComponent().path)/\(entry)"],
            detected: detectedClients(for: url),
            warning: "This is a package entry-point suggestion. Kytto did not execute it or verify that it speaks MCP."
        )
    }

    private func entryPointCandidate(
        name: String,
        sourcePath: String,
        relativePath: String,
        command: String,
        args: [String],
        detected: [ClientID],
        warning: String
    ) -> MCPLibraryCandidate {
        MCPLibraryCandidate(
            name: name,
            location: relativePath,
            sourcePath: sourcePath,
            draft: ServerDraft(name: name, command: command, args: args),
            detectedClientIDs: detected,
            warnings: [warning]
        )
    }

    private func detectedClients(for url: URL) -> [ClientID] {
        let path = url.path
        var result: [ClientID] = []
        if path.contains("/.cursor/") { result.append(.cursor) }
        if path.contains("/.claude/") || path.hasSuffix("/.claude.json") { result.append(.claudeCode) }
        if path.contains("/Code/User/") || path.contains("/.vscode/") { result.append(.vsCode) }
        if path.contains("/.codex/") { result.append(.codex) }
        if path.contains("/Claude/") { result.append(.claudeDesktop) }
        return result
    }

    private func relativePath(_ url: URL) -> String {
        let prefix = root.path.hasSuffix("/") ? root.path : root.path + "/"
        return url.path.hasPrefix(prefix) ? String(url.path.dropFirst(prefix.count)) : url.lastPathComponent
    }

    private func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
    }

    private func isRegularFile(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
    }

    private func isSkippedDirectory(_ url: URL) -> Bool {
        guard isDirectory(url) else { return false }
        return [".git", "node_modules", ".venv", "venv", "dist", "build", ".tox"].contains(url.lastPathComponent)
    }
}

private extension ServerFields {
    func resolveTransportForLibrary() -> Transport {
        if let declaredType, let transport = Transport(rawValue: declaredType.lowercased()) { return transport }
        if url != nil && command == nil { return url?.hasSuffix("/sse") == true ? .sse : .http }
        return .stdio
    }
}
