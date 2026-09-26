import Foundation

/// Writes a file in a way that cannot leave it half-written (§6.2).
///
/// Truncate-and-write loses a user's entire configuration if the process dies,
/// the disk fills, or the machine loses power mid-write. Write to a temporary
/// file beside the original, flush it to the platter, then rename over the top —
/// rename is atomic, so a reader sees either the old file or the new one and
/// never a torn one.
public enum AtomicWriter {

    public enum WriteError: Error, LocalizedError {
        case couldNotCreateTemporaryFile(String)
        case renameFailed(String)

        public var errorDescription: String? {
            switch self {
            case .couldNotCreateTemporaryFile(let reason):
                "Could not stage the new configuration: \(reason)"
            case .renameFailed(let reason):
                "Could not put the new configuration in place: \(reason)"
            }
        }
    }

    public static func write(_ text: String, to url: URL, permissions: Int? = nil) throws {
        try write(Data(text.utf8), to: url, permissions: permissions)
    }

    /// Byte-preserving variant used by transaction rollback.
    ///
    /// - Parameter permissions: the mode the file ends up with. Nil carries over
    ///   the mode of the file being replaced, so a config the user had locked
    ///   down does not come back world-readable; a file that did not exist yet
    ///   gets the process default.
    public static func write(_ data: Data, to url: URL, permissions: Int? = nil) throws {
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

        guard rename(temporary.path, url.path) == 0 else {
            let reason = String(cString: strerror(errno))
            try? FileManager.default.removeItem(at: temporary)
            throw WriteError.renameFailed(reason)
        }

        // Without this the rename itself can still be lost on power failure,
        // which would leave neither the old nor the new file.
        flushDirectory(directory)
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
