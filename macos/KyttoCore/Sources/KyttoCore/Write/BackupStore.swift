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
        try backUp(url, clientID: clientID, pathDisplay: pathDisplay, snapshot: nil)
    }

    /// - Parameter snapshot: the bytes to keep, when the caller has already
    ///   read the file and is about to act on exactly those bytes. A backup
    ///   then describes what the edit started from, not whatever the file held
    ///   a moment later (AUD-02). Nil reads the file now.
    public func backUp(_ url: URL, clientID: ClientID, pathDisplay: String, snapshot: Data?) throws -> Backup? {
        try MutationCoordinator.sync {
        guard snapshot != nil || FileManager.default.fileExists(atPath: url.path) else { return nil }

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

        let data = try snapshot ?? Data(contentsOf: url)
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
    ///
    /// - Parameter ledger: what Kytto last saw on disk. When given, a target
    ///   that changed since is refused rather than overwritten — a restore is a
    ///   write like any other, and §6.4 does not make an exception for it. The
    ///   ledger is updated to the restored bytes on success.
    public func restore(
        _ backup: Backup,
        home: URL,
        pathOverrides: [String: String] = [:],
        ledger: DigestLedger? = nil
    ) throws {
        try MutationCoordinator.sync {
        let resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        guard let descriptor = ClientRegistry.descriptorIfKnown(for: backup.clientID),
              let target = resolver.writeTarget(matching: backup.originalURL, for: descriptor)
        else {
            throw BackupError.unknownRestoreTarget(pathDisplay: backup.originalPathDisplay)
        }

        // Read once: the check, the pre-restore backup and the final swap all
        // describe these bytes (AUD-02).
        let current = try? Data(contentsOf: target)
        let currentDigest = current.map { ConfigWriter.digest(of: $0) }

        // Before anything is written — no pre-restore backup either.
        if let ledger, currentDigest != ledger.digest(for: target) {
            throw ConfigWriteError.changedOnDisk(pathDisplay: backup.originalPathDisplay)
        }

        // Bytes, not text: decoding would drop a byte-order mark, and a restore
        // puts back exactly what was there.
        let data = try Data(contentsOf: backup.url)
        if let current {
            try backUp(target, clientID: backup.clientID, pathDisplay: backup.originalPathDisplay, snapshot: current)
        }
        do {
            try AtomicWriter.write(
                data, to: target,
                expecting: currentDigest.map(AtomicWriter.Precondition.digest) ?? .absent
            )
        } catch AtomicWriter.WriteError.targetChanged {
            throw ConfigWriteError.changedOnDisk(pathDisplay: backup.originalPathDisplay)
        }
        ledger?.record(ConfigWriter.digest(of: data), for: target)
        }
    }

    /// The servers a backup defines, by name, when the file it was taken from
    /// is its client's server map. Nil for any other file — a deny list, an
    /// extension's settings — and for one that does not parse.
    ///
    /// Restoring decides from this which of Kytto's parked copies the restored
    /// file has made redundant. It cannot decide from discovery: discovery draws
    /// a parked copy into the model as that client's definition, so every
    /// parked server would look restored and lose its only copy.
    public func serverNames(in backup: Backup, home: URL, pathOverrides: [String: String] = [:]) -> [String]? {
        let resolver = ClientPathResolver(home: home, overrides: pathOverrides)
        guard let descriptor = ClientRegistry.descriptorIfKnown(for: backup.clientID),
              let source = descriptor.editableServerMap,
              resolver.resolveServerMap(source.file, for: backup.clientID).standardizedFileURL.path
                == backup.originalURL.standardizedFileURL.path,
              let data = try? Data(contentsOf: backup.url),
              let text = String(data: data, encoding: .utf8)
        else { return nil }

        switch source.format {
        case .json:
            guard let document = try? JSONDocument.parse(text) else { return nil }
            return document.value(at: [source.serversKey])?.keys ?? []
        case .toml:
            guard let document = try? TOMLDocument.parse(text) else { return nil }
            return document.serverNames(under: source.serversKey)
        }
    }

    /// Keeps the newest `retentionPerClient` backups of each file.
    ///
    /// Per file, not per client: Claude Code's off switch writes two files, so
    /// counting them together let twenty deny-list toggles push out every
    /// backup of its main config — and at a low setting, one toggle could
    /// delete the backup its own Undo was about to use.
    private func prune(clientID: ClientID) throws {
        let byFile = Dictionary(grouping: list(clientID: clientID)) { $0.originalURL.standardizedFileURL.path }
        for backups in byFile.values where backups.count > retentionPerClient {
            for backup in backups.sorted(by: { ($0.takenAt, $0.id) > ($1.takenAt, $1.id) }).dropFirst(retentionPerClient) {
                try? FileManager.default.removeItem(at: backup.url)
                try? FileManager.default.removeItem(at: backup.url.appendingPathExtension("origin"))
            }
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
