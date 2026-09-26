import Foundation

/// A prefilled server the user can start from (§7.2).
public struct CatalogEntry: Codable, Identifiable, Equatable, Sendable {
    public struct Placeholder: Codable, Equatable, Sendable {
        /// The token to substitute, e.g. `{{directory}}`.
        public let token: String
        public let label: String
        public let example: String
    }

    public struct EnvRequirement: Codable, Equatable, Sendable {
        public let key: String
        public let required: Bool
        public let hint: String
    }

    public let id: String
    public let name: String
    public let displayName: String
    public let description: String
    public let transport: Transport
    public let command: String
    public let args: [String]
    public let url: String?
    public let placeholders: [Placeholder]
    public let env: [EnvRequirement]
    /// What has to be installed for the command to work, in plain words.
    public let requires: String
    public let homepage: String

    /// A draft ready to edit, with placeholders left in so the user can see what
    /// still needs filling in.
    public func makeDraft() -> ServerDraft {
        ServerDraft(
            name: name,
            transport: transport,
            command: command,
            args: args,
            env: env.map { EnvEntry(key: $0.key, value: nil) },
            url: url ?? ""
        )
    }
}

/// The bundled catalog.
///
/// Static by design (§7.2): no remote fetching, no registry API. Catalogs are
/// everywhere and free — the value of this product is the matrix, not the list.
/// This exists only so that adding a first server is not a blank form.
public enum Catalog {
    private struct File: Decodable {
        let servers: [CatalogEntry]
    }

    public static let entries: [CatalogEntry] = load()

    private static func load() -> [CatalogEntry] {
        guard let url = Bundle.module.url(forResource: "catalog", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let file = try? JSONDecoder().decode(File.self, from: data)
        else {
            // A missing catalog is a packaging mistake, not a user problem. The
            // manual path (§7.2) works without it, so degrade quietly.
            return []
        }
        return file.servers
    }

    public static func entry(id: String) -> CatalogEntry? {
        entries.first { $0.id == id }
    }
}
