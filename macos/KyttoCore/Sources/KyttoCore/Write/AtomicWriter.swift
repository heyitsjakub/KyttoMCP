import Foundation

/// Writes a file in a way that cannot leave it half-written (§6.2).
///
/// Truncate-and-write loses a user's entire configuration if the process dies,
/// the disk fills, or the machine loses power mid-write. Write to a temporary
/// file beside the original, flush it to the platter, then rename over the top —
/// rename is atomic, so a reader sees either the old file or the new one and
/// never a torn one.
public enum AtomicWriter {

    /// What the destination must still hold at the moment it is replaced.
    ///
    /// Checked after the new bytes are staged and immediately before the swap,
    /// which is as late as a check can happen without a lock other programs
    /// would have to honour. It narrows the window in which another program's
    /// save can be overwritten; it cannot close it completely.
    public enum Precondition: Sendable, Equatable {
        /// There must be no file. Enforced by the rename itself (`RENAME_EXCL`),
        /// so a file created in the meantime is never replaced.
        case absent
        /// The file must still have exactly these bytes (SHA-256, hex).
        case digest(String)
    }

    public enum WriteError: Error, LocalizedError, Equatable {
        case couldNotCreateTemporaryFile(String)
        case renameFailed(String)
        /// The destination no longer matched the ``Precondition``; nothing was
        /// replaced.
        case targetChanged

        public var errorDescription: String? {
            switch self {
            case .couldNotCreateTemporaryFile(let reason):
                "Could not stage the new configuration: \(reason)"
            case .renameFailed(let reason):
                "Could not put the new configuration in place: \(reason)"
            case .targetChanged:
                "The file changed while Kytto was writing it, so it was left as it is."
            }
        }
    }

    public static func write(_ text: String, to url: URL, permissions: Int? = nil, expecting precondition: Precondition? = nil) throws {
        try write(Data(text.utf8), to: url, permissions: permissions, expecting: precondition)
    }

    /// Byte-preserving variant used by transaction rollback.
    ///
    /// - Parameter permissions: the mode the file ends up with. Nil carries over
    ///   the mode of the file being replaced, so a config the user had locked
    ///   down does not come back world-readable; a file that did not exist yet
    ///   gets the process default.
    /// - Parameter expecting: when set, the write is refused with
    ///   ``WriteError/targetChanged`` unless the destination still matches.
    public static func write(
        _ data: Data,
        to requested: URL,
        permissions: Int? = nil,
        expecting precondition: Precondition? = nil
    ) throws {
        // A config kept in a dotfiles folder and linked into place is a symbolic
        // link. Renaming over the link would replace it with a regular file —
        // cutting the dotfiles copy loose — and the mode read off the link is
        // the link's own, not the file's. So the write goes to the file the link
        // names, staged beside *it*. Callers keep the path the client reads:
        // backups and the digest ledger key on that, and reading through the
        // link gives them the same bytes.
        let url = try resolvingSymbolicLinks(requested)
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

        // The temporary file must share a filesystem with the target, or rename
        // stops being atomic and turns into copy-then-delete.
        let temporary = directory.appending(path: ".\(url.lastPathComponent).kytto-\(UUID().uuidString)")

        do {
            try stage(data, at: temporary, permissions: permissions ?? existingPermissions(of: url))
        } catch {
            try? FileManager.default.removeItem(at: temporary)
            throw WriteError.couldNotCreateTemporaryFile(error.localizedDescription)
        }

        switch precondition {
        case .digest(let expected):
            guard let current = try? Data(contentsOf: url), ConfigWriter.digest(of: current) == expected else {
                try? FileManager.default.removeItem(at: temporary)
                throw WriteError.targetChanged
            }
            try swap(temporary, into: url, exclusive: false)
        case .absent:
            try swap(temporary, into: url, exclusive: true)
        case nil:
            try swap(temporary, into: url, exclusive: false)
        }

        // Without this the rename itself can still be lost on power failure,
        // which would leave neither the old nor the new file.
        flushDirectory(directory)
    }

    private static func swap(_ temporary: URL, into url: URL, exclusive: Bool) throws {
        var result = exclusive
            ? renamex_np(temporary.path, url.path, UInt32(RENAME_EXCL))
            : rename(temporary.path, url.path)
        // A filesystem without exclusive rename (some network volumes) gets the
        // closest check there is instead.
        if result != 0, exclusive, errno == ENOTSUP || errno == EINVAL {
            if FileManager.default.fileExists(atPath: url.path) {
                errno = EEXIST
            } else {
                result = rename(temporary.path, url.path)
            }
        }
        guard result == 0 else {
            let code = errno
            try? FileManager.default.removeItem(at: temporary)
            if exclusive, code == EEXIST { throw WriteError.targetChanged }
            throw WriteError.renameFailed(String(cString: strerror(code)))
        }
    }

    /// Writes the temporary file and forces its contents out of the page cache
    /// before it is renamed into place. Otherwise the rename can land while the
    /// contents have not.
    ///
    /// A file with a mode to honour is created owner-only and given that mode
    /// once it is written. Writing at the default mode and tightening afterwards
    /// leaves the bytes of a `0600` config, or of a backup of one, readable under
    /// the temporary name in between — in a directory other accounts may be able
    /// to list, as they can a default macOS home folder (issue #11).
    private static func stage(_ data: Data, at url: URL, permissions: Int?) throws {
        // O_EXCL: the name is fresh, so anything already there — a symbolic link
        // included — is not something to write through.
        let descriptor = open(
            url.path,
            O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC,
            permissions == nil ? 0o666 : 0o600
        )
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let handle = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)

        try handle.write(contentsOf: data)
        if let permissions, fchmod(descriptor, mode_t(permissions & 0o7777)) != 0 {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        try handle.synchronize()
        try handle.close()
    }

    /// Follows a chain of symbolic links at the final path component to the
    /// file at the end of it, which need not exist yet. Links in the directory
    /// part are left to the kernel: a rename through them lands in the right
    /// place already.
    static func resolvingSymbolicLinks(_ url: URL) throws -> URL {
        var current = url.standardizedFileURL
        for _ in 0..<32 {
            guard let destination = try? FileManager.default.destinationOfSymbolicLink(atPath: current.path) else {
                return current
            }
            // A relative target is relative to the directory the link really
            // sits in. Removing `..` against the link's own path instead goes
            // wrong when a directory on the way is itself a link.
            current = destination.hasPrefix("/")
                ? URL(filePath: destination).standardizedFileURL
                : current.deletingLastPathComponent().resolvingSymlinksInPath()
                    .appending(path: destination).standardizedFileURL
        }
        throw WriteError.couldNotCreateTemporaryFile(POSIXError(.ELOOP).localizedDescription)
    }

    private static func existingPermissions(of url: URL) -> Int? {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes?[.posixPermissions] as? NSNumber)?.intValue
    }

    private static func flushDirectory(_ url: URL) {
        let descriptor = open(url.path, O_RDONLY)
        guard descriptor >= 0 else { return }
        defer { close(descriptor) }
        _ = fsync(descriptor)
    }
}
