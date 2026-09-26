import Foundation

/// Where Kytto keeps its own data.
///
/// Nothing here is created on launch (§6.6) — directories come into existence
/// the first time the user actually changes something.
public struct KyttoPaths: Sendable {
    public let root: URL

    public init(home: URL) {
        root = home.appending(path: "Library/Application Support/Kytto")
    }

    /// Timestamped copies of client configs, taken before every write.
    public var backups: URL { root.appending(path: "Backups") }

    /// Server definitions removed from a client purely to switch them off, kept
    /// so the toggle is reversible.
    public var parked: URL { root.appending(path: "parked-servers.json") }

    /// Named sets of server ids. Profiles describe intent; client config files
    /// remain authoritative for the state that is actually active (§5).
    public var profiles: URL { root.appending(path: "profiles.json") }

    /// Secret-free route metadata used by the local stdio gateway (§3.3).
    public var gatewayRoutes: URL { root.appending(path: "Gateway/routes.json") }

    /// Metadata-only session and tool-call events. Payload capture is separate.
    public var gatewayEvents: URL { root.appending(path: "Gateway/events.jsonl") }
}

// MARK: - Owner-only storage

extension KyttoPaths {
    /// For what holds a client config's own bytes: backups, and definitions
    /// parked to switch a server off. Both carry `env` blocks verbatim, API keys
    /// included, so neither may be more readable than the config it came from —
    /// and `~/.claude.json` is `0600`.
    public static let privateFilePermissions = 0o600
    public static let privateDirectoryPermissions = 0o700

    /// Creates `directory` owner-only, and takes group and other access away
    /// from it and from every folder above it up to `root`.
    ///
    /// Tightening the folders that already exist is the point: `root` is shared
    /// with stores that create it at the default mode, and whichever of them got
    /// there first would otherwise decide who can list what is inside.
    func createPrivateDirectory(_ directory: URL) throws {
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: Self.privateDirectoryPermissions]
        )
        let rootPath = root.standardizedFileURL.path
        var current = directory.standardizedFileURL
        while current.path == rootPath || current.path.hasPrefix(rootPath + "/") {
            try Self.removeGroupAndOtherAccess(from: current)
            if current.path == rootPath { break }
            current = current.deletingLastPathComponent()
        }
    }

    /// Takes group and other access away from the copies of client configs
    /// already on disk.
    ///
    /// Backups and parked definitions written before issue #11 was addressed
    /// came out `0644` in `0755` folders. This runs on every launch rather than
    /// once behind a marker: it is a few dozen `stat` calls, it changes nothing
    /// once the folder is private, and it creates nothing (§6.6) — a folder that
    /// does not exist yet is left not existing.
    ///
    /// - Returns: how many items it changed.
    @discardableResult
    public func tightenPermissions() -> Int {
        MutationCoordinator.sync {
            var changed = 0
            func tighten(_ url: URL) {
                if (try? Self.removeGroupAndOtherAccess(from: url)) == true { changed += 1 }
            }
            tighten(root)
            tighten(parked)
            tighten(backups)
            if let entries = FileManager.default.enumerator(at: backups, includingPropertiesForKeys: nil) {
                for case let url as URL in entries { tighten(url) }
            }
            return changed
        }
    }

    /// Clears the group and other bits and leaves the owner's alone, so it can
    /// only ever narrow access. A symbolic link is skipped rather than followed:
    /// changing its mode would change whatever it points at, which need not be
    /// Kytto's.
    ///
    /// - Returns: whether anything changed.
    @discardableResult
    static func removeGroupAndOtherAccess(from url: URL) throws -> Bool {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType != .typeSymbolicLink,
              let mode = (attributes[.posixPermissions] as? NSNumber)?.intValue,
              mode & 0o077 != 0
        else { return false }
        try FileManager.default.setAttributes([.posixPermissions: mode & ~0o077], ofItemAtPath: url.path)
        return true
    }
}
