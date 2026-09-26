import Foundation

public struct Backup: Identifiable, Equatable, Sendable {
    public let id: String
    public let clientID: ClientID
    /// The file this was taken from.
    public let originalPathDisplay: String
    public let takenAt: Date
    public let byteCount: Int
    let url: URL
    let originalURL: URL
}

public enum BackupError: Error, LocalizedError, Equatable {
    /// The backup's sidecar names a file Kytto does not write for its client:
    /// edited outside Kytto, or taken from a location Settings no longer uses.
    case unknownRestoreTarget(pathDisplay: String)

    public var errorDescription: String? {
        switch self {
        case .unknownRestoreTarget(let path):
            """
            \(path) is not a configuration file Kytto manages for this client, so the \
            backup was not restored. If the client's config location was changed in \
            Settings, the backup is still in the backups folder.
            """
        }
    }
}

/// A copy of every config Kytto is about to change, kept where the user can get
/// at it (§6.1, §6.5).
///
/// This is a trust feature before it is a safety feature. "Revert" being visible
/// in the UI is the difference between a tool people let near their config and
/// one they uninstall.
public struct BackupStore: Sendable {
    public static let defaultRetentionPerClient = 20

    private let paths: KyttoPaths
    /// How many to keep per client. Configurable in Settings (§7.6).
    public let retentionPerClient: Int

    public init(paths: KyttoPaths, retentionPerClient: Int = BackupStore.defaultRetentionPerClient) {
        self.paths = paths
        self.retentionPerClient = retentionPerClient
    }

    private func directory(for clientID: ClientID) -> URL {
        paths.backups.appending(path: clientID.rawValue)
    }

    /// Copies `url` aside and prunes anything past the retention limit.
    /// Returns nil when there was nothing to back up yet.
    @discardableResult
    public func backUp(_ url: URL, clientID: ClientID, pathDisplay: String) throws -> Backup? {
        try MutationCoordinator.sync {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }

        // Owner-only, folders and files alike: a backup is the whole config,
        // secrets included, and must not be more readable than what it copies.
        let directory = directory(for: clientID)
        try paths.createPrivateDirectory(directory)

        let takenAt = Date()
        let baseName = "\(url.lastPathComponent).\(Self.stamp(takenAt))"
        var name = "\(baseName).bak"
        var destination = directory.appending(path: name)
        var collision = 1
        while FileManager.default.fileExists(atPath: destination.path) {
            name = "\(baseName)-\(collision).bak"
            destination = directory.appending(path: name)
            collision += 1
        }

        let data = try Data(contentsOf: url)
        try AtomicWriter.write(data, to: destination, permissions: KyttoPaths.privateFilePermissions)

        // The path the backup came from is not recoverable from the file name
        // once a client moves its config, so it is recorded alongside.
        let sidecar = destination.appendingPathExtension("origin")
        do {
            try AtomicWriter.write(
                Data(url.path.utf8), to: sidecar, permissions: KyttoPaths.privateFilePermissions
            )
        } catch {
            // A backup without its restore target is not a usable backup. Do not
            // leave an entry that later resolves an empty path against whatever
            // working directory the app happened to inherit.
            try? FileManager.default.removeItem(at: destination)
            try? FileManager.default.removeItem(at: sidecar)
            throw error
        }

        try prune(clientID: clientID)

        return Backup(
            id: name,
            clientID: clientID,
            originalPathDisplay: pathDisplay,
            takenAt: takenAt,
            byteCount: data.count,
            url: destination,
            originalURL: url
        )
        }
    }

    public func list(clientID: ClientID) -> [Backup] {
        let directory = directory(for: clientID)
        guard let entries = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.contentModificationDateKey, .fileSizeKey]
        ) else { return [] }

        return entries
            .filter { $0.pathExtension == "bak" }
            .compactMap { url -> Backup? in
                let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
                guard let originPath = Self.originPath(for: url) else { return nil }
                return Backup(
                    id: url.lastPathComponent,
                    clientID: clientID,
                    originalPathDisplay: Self.abbreviate(originPath),
                    takenAt: values?.contentModificationDate ?? .distantPast,
                    byteCount: values?.fileSize ?? 0,
                    url: url,
                    originalURL: URL(filePath: originPath)
                )
            }
            .sorted { $0.takenAt > $1.takenAt }
    }

    public func backup(id: String, clientID: ClientID) -> Backup? {
        list(clientID: clientID).first { $0.id == id }
    }

    /// Read-only preflight hook for features that keep sidecar state coupled to
    /// a config definition, such as Gateway routes.
    public func contents(of backup: Backup) throws -> String {
        try String(contentsOf: backup.url, encoding: .utf8)
    }

    /// Puts a backup back, atomically, after backing up what is there now — so
    /// reverting is itself reversible.
    ///
    /// Where it goes is read from the `.origin` sidecar, which is only a file in
    /// Kytto's folder. It is honoured only when it names a file Kytto writes for
    /// that client, so an edited sidecar cannot aim a restore at any other file
    /// the user can write. `home` and `pathOverrides` must be the ones the write
    /// services were built with.
    public func restore(_ backup: Backup, home: URL, pathOverrides: [String: String] = [:]) throws {
        try MutationCoordinator.sync {
        let resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        guard let descriptor = ClientRegistry.descriptorIfKnown(for: backup.clientID),
              let target = resolver.writeTarget(matching: backup.originalURL, for: descriptor)
        else {
            throw BackupError.unknownRestoreTarget(pathDisplay: backup.originalPathDisplay)
        }

        let text = try contents(of: backup)
        try backUp(target, clientID: backup.clientID, pathDisplay: backup.originalPathDisplay)
        try AtomicWriter.write(text, to: target)
        }
    }

    private func prune(clientID: ClientID) throws {
        let all = list(clientID: clientID)
        guard all.count > retentionPerClient else { return }
        for backup in all.dropFirst(retentionPerClient) {
            try? FileManager.default.removeItem(at: backup.url)
            try? FileManager.default.removeItem(at: backup.url.appendingPathExtension("origin"))
        }
    }

    /// Sortable, filename-safe, and readable at a glance in Finder.
    private static func stamp(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd-HHmmss-SSS"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        return formatter.string(from: date)
    }

    private static func abbreviate(_ path: String) -> String {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        return path.hasPrefix(home) ? "~" + path.dropFirst(home.count) : path
    }

    private static func originPath(for backupURL: URL) -> String? {
        guard let data = try? Data(contentsOf: backupURL.appendingPathExtension("origin")),
              let path = String(data: data, encoding: .utf8),
              path.hasPrefix("/"),
              URL(filePath: path).lastPathComponent.isEmpty == false
        else { return nil }
        return path
    }
}
