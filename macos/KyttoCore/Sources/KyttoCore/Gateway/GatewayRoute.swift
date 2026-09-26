import Foundation

/// One environment value needed by an upstream process.
///
/// The value is deliberately absent. It lives in the platform credential store
/// and the helper resolves this opaque account immediately before spawning.
public struct GatewayEnvironmentReference: Codable, Equatable, Sendable {
    public let key: String
    public let secretID: String

    public init(key: String, secretID: String) {
        self.key = key
        self.secretID = secretID
    }
}

/// Durable identity for one server-client gateway connection (§3.3).
///
/// A route is independent of the server's display name, so future policies can
/// stay attached across renames. No secret value or original config text is
/// allowed in this record; both are held by `SecretStoring` instead.
public struct GatewayRoute: Codable, Equatable, Identifiable, Sendable {
    public let id: String
    public let serverID: String
    public let serverName: String
    public let clientID: ClientID
    public let gatewayCommand: String
    public let command: String
    public let arguments: [String]
    public let environment: [GatewayEnvironmentReference]
    public let directDefinitionSecretID: String
    public let createdAt: Date
    /// The only tools this client may see and call through this route (§7.11).
    ///
    /// Nil means no masking, and specifically means the helper keeps forwarding
    /// bytes untouched — §3.3's transparency guarantee is only given up by a
    /// route that has actually been narrowed. An empty array is a real choice
    /// (a route that exposes nothing), which is why this is not just `[String]`
    /// with emptiness standing in for "everything".
    ///
    /// Tool names are model-facing identifiers, not secrets, so unlike the
    /// definition and environment they belong in the route record rather than
    /// in the credential store.
    public let exposedTools: [String]?

    public init(
        id: String,
        serverID: String,
        serverName: String,
        clientID: ClientID,
        gatewayCommand: String,
        command: String,
        arguments: [String],
        environment: [GatewayEnvironmentReference],
        directDefinitionSecretID: String,
        createdAt: Date = Date(),
        exposedTools: [String]? = nil
    ) {
        self.id = id
        self.serverID = Server.identity(for: serverID)
        self.serverName = serverName
        self.clientID = clientID
        self.gatewayCommand = gatewayCommand
        self.command = command
        self.arguments = arguments
        self.environment = environment
        self.directDefinitionSecretID = directDefinitionSecretID
        self.createdAt = createdAt
        self.exposedTools = exposedTools.map { Array(Set($0)).sorted() }
    }

    /// The same route, exposing a different set of tools. Nil clears masking.
    public func exposing(_ tools: [String]?) -> GatewayRoute {
        GatewayRoute(
            id: id,
            serverID: serverID,
            serverName: serverName,
            clientID: clientID,
            gatewayCommand: gatewayCommand,
            command: command,
            arguments: arguments,
            environment: environment,
            directDefinitionSecretID: directDefinitionSecretID,
            createdAt: createdAt,
            exposedTools: tools
        )
    }

    public static func directDefinitionSecretID(routeID: String) -> String {
        "gateway-definition::\(routeID.lowercased())"
    }

    public static func environmentSecretID(routeID: String, key: String) -> String {
        let encoded = Data(key.utf8).base64EncodedString()
        return "gateway-env::\(routeID.lowercased())::\(encoded)"
    }
}

public enum GatewayRouteError: Error, LocalizedError, Equatable {
    case invalidRouteID(String)
    case duplicateRoute(String)
    case unknownRoute(String)
    case unreadableStore(String)
    case missingSecret(String)

    public var errorDescription: String? {
        switch self {
        case .invalidRouteID(let id):
            "\(id) is not a valid gateway route id."
        case .duplicateRoute(let id):
            "Gateway route \(id) already exists."
        case .unknownRoute(let id):
            "Gateway route \(id) no longer exists. Restore Direct mode in Kytto."
        case .unreadableStore(let reason):
            "Kytto could not read its gateway routes: \(reason)"
        case .missingSecret(let key):
            "Gateway secret \(key) is unavailable. Restore Direct mode in Kytto."
        }
    }
}

/// Secret-free, atomically written route registry.
public struct GatewayRouteStore: Sendable {
    public let url: URL

    public init(paths: KyttoPaths) {
        url = paths.gatewayRoutes
    }

    public init(url: URL) {
        self.url = url
    }

    public func all() throws -> [GatewayRoute] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        do {
            let data = try Data(contentsOf: url)
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            return try decoder.decode([GatewayRoute].self, from: data)
                .sorted { ($0.clientID.rawValue, $0.serverID) < ($1.clientID.rawValue, $1.serverID) }
        } catch {
            throw GatewayRouteError.unreadableStore(error.localizedDescription)
        }
    }

    public func route(id: String) throws -> GatewayRoute {
        guard let route = try all().first(where: { $0.id == id }) else {
            throw GatewayRouteError.unknownRoute(id)
        }
        return route
    }

    public func route(serverID: String, clientID: ClientID) throws -> GatewayRoute? {
        let normalized = Server.identity(for: serverID)
        return try all().first { $0.serverID == normalized && $0.clientID == clientID }
    }

    public func insert(_ route: GatewayRoute) throws {
        guard UUID(uuidString: route.id) != nil else {
            throw GatewayRouteError.invalidRouteID(route.id)
        }
        var routes = try all()
        guard !routes.contains(where: {
            $0.id == route.id || ($0.serverID == route.serverID && $0.clientID == route.clientID)
        }) else {
            throw GatewayRouteError.duplicateRoute(route.id)
        }
        routes.append(route)
        try save(routes)
    }

    /// Narrows or widens what a route exposes (§7.11).
    ///
    /// Only the allow list moves. Nothing about how the upstream server is
    /// launched is touched, and no client config is rewritten — the route id in
    /// the config already points here, so masking takes effect the next time
    /// the client starts the helper.
    @discardableResult
    public func update(id: String, exposedTools: [String]?) throws -> GatewayRoute {
        var routes = try all()
        guard let index = routes.firstIndex(where: { $0.id == id }) else {
            throw GatewayRouteError.unknownRoute(id)
        }
        let updated = routes[index].exposing(exposedTools)
        routes[index] = updated
        try save(routes)
        return updated
    }

    public func remove(id: String) throws {
        var routes = try all()
        guard routes.contains(where: { $0.id == id }) else {
            throw GatewayRouteError.unknownRoute(id)
        }
        routes.removeAll(where: { $0.id == id })
        try save(routes)
    }

    private func save(_ routes: [GatewayRoute]) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(routes.sorted {
            ($0.clientID.rawValue, $0.serverID) < ($1.clientID.rawValue, $1.serverID)
        })
        guard let text = String(data: data, encoding: .utf8) else { return }
        // Given the mode while staged rather than after the rename, so a new file
        // is never briefly readable by others under its real name.
        try AtomicWriter.write(text, to: url, permissions: 0o600)
    }
}

public struct GatewayLaunch: Equatable, Sendable {
    public let route: GatewayRoute
    public let environment: [String: String]
}

public extension GatewayRouteStore {
    func resolve(id: String, secrets: any SecretStoring) throws -> GatewayLaunch {
        let route = try route(id: id)
        var environment: [String: String] = [:]
        for reference in route.environment {
            guard let value = try secrets.value(for: reference.secretID) else {
                throw GatewayRouteError.missingSecret(reference.key)
            }
            environment[reference.key] = value
        }
        return GatewayLaunch(route: route, environment: environment)
    }
}
