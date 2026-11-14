import CryptoKit
import Foundation

/// A folder the user chose so Kytto can show the MCP servers its repository
/// configures (roadmap §12).
///
/// Repository files are also an attack path: a compromised package can commit
/// `.cursor/mcp.json` or `.mcp.json` into a project, and the client then offers
/// that server the next time the folder is opened. Kytto used to see only
/// user-level configs, so such a server was invisible here.
///
/// The boundary is deliberate. Only folders the user picked, only the fixed
/// file names in ``ProjectConfigFile``, no recursion into subfolders and no
/// scanning of the home directory. Every file is read-only, and a server found
/// only in a repository is never started by Kytto unless the user asks, because
/// a health check runs its command.
public struct ProjectFolder: Identifiable, Codable, Equatable, Sendable {
    public var id: UUID
    /// Absolute path of the folder.
    public var path: String
    /// What the folder is called on screen. Defaults to its last component.
    public var label: String

    public init(id: UUID = UUID(), path: String, label: String? = nil) {
        self.id = id
        self.path = path
        self.label = label ?? URL(filePath: path).lastPathComponent
    }
}

/// The project-level files Kytto reads inside a chosen folder, and who reads
/// them. Exact relative paths; nothing else in the folder is opened.
public enum ProjectConfigFile: String, CaseIterable, Sendable {
    /// The portable project file. Claude Code reads it as its project scope.
    case portable = ".mcp.json"
    case cursor = ".cursor/mcp.json"
    /// VS Code's workspace file; its map is `servers`, not `mcpServers`.
    case vscode = ".vscode/mcp.json"

    public var readBy: String {
        switch self {
        case .portable: "Claude Code and other clients that read a project .mcp.json"
        case .cursor: "Cursor"
        case .vscode: "VS Code"
        }
    }
}

extension ClientRegistry {
    /// A stable id per folder and file, so a server's row, its review state
    /// and the sidebar's fold survive relaunches.
    public static func projectClientID(folder: UUID, file: ProjectConfigFile) -> ClientID {
        var bytes = Array(SHA256.hash(data: Data("kytto-project:\(folder.uuidString):\(file.rawValue)".utf8)).prefix(16))
        bytes[6] = (bytes[6] & 0x0F) | 0x50 // name-based UUID
        bytes[8] = (bytes[8] & 0x3F) | 0x80 // RFC 4122 variant
        let uuid = UUID(uuid: (bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5], bytes[6], bytes[7],
                               bytes[8], bytes[9], bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]))
        return .custom(uuid)
    }

    /// One read-only source per project file that exists in the folder.
    ///
    /// A file that does not exist yet gets no column: three empty "no config"
    /// entries per folder would bury the one that matters. Discovery runs again
    /// when the window becomes active, which is when a newly committed file
    /// appears.
    public static func projectDescriptors(
        for folder: ProjectFolder,
        fileExists: (String) -> Bool = { FileManager.default.fileExists(atPath: $0) }
    ) -> [ClientDescriptor] {
        guard folder.path.hasPrefix("/") else { return [] }
        let root = URL(filePath: folder.path).standardizedFileURL
        return ProjectConfigFile.allCases.compactMap { file in
            let path = root.appending(path: file.rawValue).path
            guard fileExists(path) else { return nil }
            var descriptor = ClientDescriptor(
                id: projectClientID(folder: folder.id, file: file),
                displayName: "\(folder.label) · \(file.rawValue)",
                iconAsset: "client-custom",
                bundleIdentifiers: [],
                executableNames: [],
                sources: [.readOnlyAutoServerMap(file: PlatformPath(darwin: path, windows: path))],
                schemaQuirks: """
                A repository file in \(folder.label), read by \(file.readBy) when that folder is open. \
                Kytto only reads it and never writes to it. A server found only here is left out of \
                Check all: a check starts the server's command on this Mac, so it runs only when you ask.
                """,
                isReadOnly: true,
                configurationScope: .workspace,
                scopeLabel: folder.label
            )
            descriptor.shortName = file.rawValue
            descriptor.isRepositoryFile = true
            return descriptor
        }
    }
}

extension ClientRegistry {
    /// Whether every client that lists this server reads it from a repository
    /// file, so nothing outside a project vouches for it. `absent` entries are
    /// the matrix's empty cells, not places the server is configured.
    public static func isRepositoryOnly(_ server: Server, repositoryClients: Set<ClientID>) -> Bool {
        let configuredIn = server.enabledIn.filter { $0.value != .absent }.keys
        return !configuredIn.isEmpty && configuredIn.allSatisfy(repositoryClients.contains)
    }
}
