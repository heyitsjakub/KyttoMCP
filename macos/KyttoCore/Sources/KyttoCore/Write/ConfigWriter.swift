import CryptoKit
import Foundation

public struct WriteReceipt: Sendable {
    /// Digest of the file after the write, so the caller can keep tracking it.
    public let digest: String
    public let backupID: String?
    public let pathDisplay: String
    /// A new file has no previous bytes to back up, so `backupID == nil` does
    /// not mean that no write happened. Callers use this flag for restart and
    /// changed-client reporting without guessing from backup availability.
    public let didWrite: Bool
}

public enum ConfigWriteError: Error, LocalizedError, Equatable {
    /// Someone else changed the file since Kytto last read it.
    case changedOnDisk(pathDisplay: String)
    case unreadable(pathDisplay: String, reason: String)

    public var errorDescription: String? {
        switch self {
        case .changedOnDisk(let path):
            "\(path) changed on disk since Kytto last read it."
        case .unreadable(let path, let reason):
            "Could not read \(path): \(reason)"
        }
    }
}

/// The one path through which a client's config is ever modified.
///
/// Every write goes: check nobody else changed the file, back it up, splice, and
/// swap it in atomically. There is no shortcut around this — §6 exists because
/// the alternative is corrupting a file someone depends on.
public struct ConfigWriter: Sendable {
    private let backups: BackupStore

    public init(backups: BackupStore) {
        self.backups = backups
    }

    /// SHA-256 of a file's bytes, or nil if it does not exist.
    ///
    /// Used instead of a modification date because editors routinely rewrite a
    /// file with identical content, and prompting the user about a change that
    /// did not happen trains them to click through the prompt that matters.
    public static func digest(of url: URL) -> String? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return digest(of: data)
    }

    public static func digest(of data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    public static func digest(of text: String) -> String {
        digest(of: Data(text.utf8))
    }

    /// Applies `transform` to the file and writes the result.
    ///
    /// - Parameter expecting: the digest Kytto saw when it last read the file.
    ///   Pass nil only when the file is not expected to exist. If the file's
    ///   current digest disagrees, the write is refused rather than merged —
    ///   §6.4: do not merge blindly, ask the user.
    /// - Parameter as: the document type to parse the file as. Comes from the
    ///   client's registry entry, so which spelling a client uses stays data
    ///   in one file rather than an assumption spread through the pipeline.
    public func edit(
        url: URL,
        clientID: ClientID,
        pathDisplay: String,
        expecting expectedDigest: String?,
        transform: (JSONDocument) throws -> String
    ) throws -> WriteReceipt {
        try edit(
            url: url,
            clientID: clientID,
            pathDisplay: pathDisplay,
            expecting: expectedDigest,
            as: JSONDocument.self,
            transform: transform
        )
    }

    public func edit<Document: ConfigDocument>(
        url: URL,
        clientID: ClientID,
        pathDisplay: String,
        expecting expectedDigest: String?,
        as documentType: Document.Type,
        transform: (Document) throws -> String
    ) throws -> WriteReceipt {
        try MutationCoordinator.sync {
        let exists = FileManager.default.fileExists(atPath: url.path)
        let currentDigest = Self.digest(of: url)

        guard currentDigest == expectedDigest else {
            throw ConfigWriteError.changedOnDisk(pathDisplay: pathDisplay)
        }

        let source: String
        if exists {
            guard let text = try? String(contentsOf: url, encoding: .utf8) else {
                throw ConfigWriteError.unreadable(pathDisplay: pathDisplay, reason: "not valid UTF-8")
            }
            source = text
        } else {
            // A client that has never configured MCP has no file. Starting from
            // the format's empty document keeps the rest of the pipeline identical.
            source = Document.emptySource
        }

        let document: Document
        do {
            document = try Document.parse(source)
        } catch {
            throw ConfigWriteError.unreadable(pathDisplay: pathDisplay, reason: "\(error)")
        }

        let updated = try transform(document)

        // Nothing to do, so nothing to back up. Saves a pointless backup slot
        // every time a toggle is set to the value it already had.
        guard updated != source else {
            return WriteReceipt(
                digest: currentDigest ?? Self.digest(of: source),
                backupID: nil,
                pathDisplay: pathDisplay,
                didWrite: false
            )
        }

        // Parse what we are about to write. If the edit produced something
        // unparseable, the bug stops here rather than in the user's config.
        guard (try? Document.parse(updated)) != nil else {
            throw ConfigWriteError.unreadable(
                pathDisplay: pathDisplay,
                reason: "the edit would have produced invalid \(Document.formatName), so nothing was written"
            )
        }

        let backup = try backups.backUp(url, clientID: clientID, pathDisplay: pathDisplay)
        try AtomicWriter.write(updated, to: url)

        return WriteReceipt(
            digest: Self.digest(of: updated),
            backupID: backup?.id,
            pathDisplay: pathDisplay,
            didWrite: true
        )
        }
    }
}
