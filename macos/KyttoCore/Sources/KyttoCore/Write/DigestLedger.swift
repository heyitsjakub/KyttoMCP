import Foundation

/// What Kytto believes is currently on disk, per file.
///
/// Discovery records a digest for every file it reads; every write updates it.
/// Before writing, `ConfigWriter` checks the file still matches — if it does not,
/// someone edited it behind Kytto's back and the change is refused rather than
/// merged (§6.4).
///
/// Deliberately in memory only. Persisting it would mean a stale record from a
/// previous launch could wave through a write over changes made while Kytto was
/// closed. Starting each session by reading the truth is both simpler and safer.
public final class DigestLedger: @unchecked Sendable {
    private var digests: [String: String] = [:]
    private let lock = NSLock()

    public init() {}

    public func record(_ digest: String?, for url: URL) {
        lock.lock()
        defer { lock.unlock() }
        if let digest {
            digests[url.standardizedFileURL.path] = digest
        } else {
            digests.removeValue(forKey: url.standardizedFileURL.path)
        }
    }

    public func digest(for url: URL) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return digests[url.standardizedFileURL.path]
    }

    /// True when the file on disk is not what Kytto last saw.
    public func hasDrifted(_ url: URL) -> Bool {
        ConfigWriter.digest(of: url) != digest(for: url)
    }

    public func forgetAll() {
        lock.lock()
        defer { lock.unlock() }
        digests.removeAll()
    }
}
