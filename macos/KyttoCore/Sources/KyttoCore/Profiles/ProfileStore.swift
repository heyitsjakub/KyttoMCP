import Foundation

/// A named set of servers that can be applied to one client at a time.
///
/// Server ids are Kytto's normalized names, so a profile survives the same
/// server being represented by JSON in one client and TOML in another. The
/// profile is not a second source of truth: applying it writes through the
/// ordinary toggle service, and discovery still reads the resulting configs.
public struct ServerProfile: Codable, Equatable, Identifiable, Sendable {
    public let id: String
    public let name: String
    public let serverIDs: [String]
    public let tokenBudget: Int?

    public init(id: String, name: String, serverIDs: [String], tokenBudget: Int? = nil) {
        self.id = id
        self.name = name
        self.serverIDs = serverIDs
        self.tokenBudget = tokenBudget
    }
}

public enum ProfileError: Error, LocalizedError, Equatable {
    case emptyName
    case nameTooLong
    case duplicateName(String)
    case unknownProfile(String)
    case unreadableStore(String)

    public var errorDescription: String? {
        switch self {
        case .emptyName:
            "Give the profile a name."
        case .nameTooLong:
            "Profile names can be at most 80 characters."
        case .duplicateName(let name):
            "A profile named \"\(name)\" already exists."
        case .unknownProfile(let id):
            "No profile with id \(id)."
        case .unreadableStore(let reason):
            "Kytto could not read the saved profiles, so it left them untouched: \(reason)"
        }
    }
}

/// Small local store for profiles.
///
/// Reading does not create anything. The app-support directory and file appear
/// only after the user saves the first profile, preserving §6's no-write launch.
public struct ProfileStore: Sendable {
    private let url: URL

    public init(paths: KyttoPaths) {
        url = paths.profiles
    }

    public func all() -> [ServerProfile] {
        guard let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode([ServerProfile].self, from: data)
        else { return [] }
        return decoded.sorted(by: Self.sortProfiles)
    }

    @discardableResult
    public func create(name: String, serverIDs: [String], tokenBudget: Int? = nil) throws -> ServerProfile {
        try MutationCoordinator.sync {
            var profiles = try loadForMutation()
            let cleaned = try validatedName(name, excluding: nil, in: profiles)
            let profile = ServerProfile(
                id: UUID().uuidString.lowercased(),
                name: cleaned,
                serverIDs: normalized(serverIDs),
                tokenBudget: normalizedBudget(tokenBudget)
            )
            profiles.append(profile)
            try save(profiles)
            return profile
        }
    }

    @discardableResult
    public func update(id: String, name: String, serverIDs: [String], tokenBudget: Int? = nil) throws -> ServerProfile {
        try MutationCoordinator.sync {
            var profiles = try loadForMutation()
            guard let index = profiles.firstIndex(where: { $0.id == id }) else {
                throw ProfileError.unknownProfile(id)
            }
            let cleaned = try validatedName(name, excluding: id, in: profiles)
            let profile = ServerProfile(
                id: id,
                name: cleaned,
                serverIDs: normalized(serverIDs),
                tokenBudget: normalizedBudget(tokenBudget)
            )
            profiles[index] = profile
            try save(profiles)
            return profile
        }
    }

    public func delete(id: String) throws {
        try MutationCoordinator.sync {
            var profiles = try loadForMutation()
            guard profiles.contains(where: { $0.id == id }) else {
                throw ProfileError.unknownProfile(id)
            }
            profiles.removeAll(where: { $0.id == id })
            try save(profiles)
        }
    }

    /// Keeps profile membership attached to a server when the user renames it.
    public func replaceServerID(_ oldID: String, with newID: String) throws {
        let old = Server.identity(for: oldID)
        let new = Server.identity(for: newID)
        guard old != new else { return }

        try MutationCoordinator.sync {
            var profiles = try loadForMutation()
            var changed = false
            profiles = profiles.map { profile in
                guard profile.serverIDs.contains(old) else { return profile }
                changed = true
                return ServerProfile(
                    id: profile.id,
                    name: profile.name,
                    serverIDs: normalized(profile.serverIDs.map { $0 == old ? new : $0 }),
                    tokenBudget: profile.tokenBudget
                )
            }
            if changed { try save(profiles) }
        }
    }

    /// Mutations must distinguish "no profile file yet" from "a profile file
    /// exists but cannot be decoded". Treating both as an empty list would turn
    /// the next create/update into silent data loss.
    private func loadForMutation() throws -> [ServerProfile] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        do {
            let data = try Data(contentsOf: url)
            return try JSONDecoder().decode([ServerProfile].self, from: data)
                .sorted(by: Self.sortProfiles)
        } catch {
            throw ProfileError.unreadableStore(error.localizedDescription)
        }
    }

    private func validatedName(
        _ name: String,
        excluding id: String?,
        in profiles: [ServerProfile]
    ) throws -> String {
        let cleaned = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else { throw ProfileError.emptyName }
        guard cleaned.count <= 80 else { throw ProfileError.nameTooLong }
        if profiles.contains(where: {
            $0.id != id && $0.name.localizedCaseInsensitiveCompare(cleaned) == .orderedSame
        }) {
            throw ProfileError.duplicateName(cleaned)
        }
        return cleaned
    }

    private func normalized(_ serverIDs: [String]) -> [String] {
        Array(Set(serverIDs.map(Server.identity(for:)).filter { !$0.isEmpty })).sorted()
    }

    private func normalizedBudget(_ budget: Int?) -> Int? {
        guard let budget, budget > 0 else { return nil }
        return min(budget, TokenWeight.referenceContextWindow)
    }

    private func save(_ profiles: [ServerProfile]) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(profiles.sorted(by: Self.sortProfiles))
        guard let text = String(data: data, encoding: .utf8) else { return }
        try AtomicWriter.write(text, to: url)
    }

    private static func sortProfiles(_ lhs: ServerProfile, _ rhs: ServerProfile) -> Bool {
        lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
    }
}
