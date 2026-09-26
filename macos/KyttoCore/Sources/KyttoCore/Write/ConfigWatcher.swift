import Foundation

/// Notices when a client's configuration changes underneath Kytto (§6.4).
///
/// Watches the *directories*, not the files. Every safe writer — Kytto included —
/// replaces a config by renaming a new file over the old one, which leaves any
/// watch attached to the original file pointing at an inode nobody can reach.
/// Directory watches survive that.
///
/// Directories are also all Kytto needs: on any event it re-reads and compares
/// digests, so it learns what actually changed rather than trusting the event.
public final class ConfigWatcher: @unchecked Sendable {
    private let queue = DispatchQueue(label: "app.kytto.config-watcher")
    private var sources: [DispatchSourceFileSystemObject] = []
    private var descriptors: [Int32] = []
    private var pending: DispatchWorkItem?
    private let onChange: @Sendable () -> Void
    private let debounce: DispatchTimeInterval

    /// - Parameter debounce: editors and client apps often touch a file several
    ///   times in quick succession. Coalescing avoids re-reading everything four
    ///   times for one save.
    public init(debounce: DispatchTimeInterval = .milliseconds(250), onChange: @escaping @Sendable () -> Void) {
        self.debounce = debounce
        self.onChange = onChange
    }

    deinit {
        for source in sources { source.cancel() }
    }

    /// Starts watching. When a requested directory does not exist yet, its
    /// nearest existing ancestor is watched; creating the missing directory then
    /// triggers discovery, which re-arms this watcher on the exact new path.
    public func watch(directories urls: [URL]) {
        stop()

        let directories = Set(urls.compactMap(Self.nearestExistingDirectory).map(\.path))
        for path in directories.sorted() {
            let descriptor = open(path, O_EVTONLY)
            guard descriptor >= 0 else { continue }

            let source = DispatchSource.makeFileSystemObjectSource(
                fileDescriptor: descriptor,
                eventMask: [.write, .rename, .delete],
                queue: queue
            )
            source.setEventHandler { [weak self] in self?.scheduleNotification() }
            source.setCancelHandler { close(descriptor) }
            source.resume()

            sources.append(source)
            descriptors.append(descriptor)
        }
    }

    private static func nearestExistingDirectory(_ requested: URL) -> URL? {
        var candidate = requested.standardizedFileURL
        var isDirectory: ObjCBool = false
        while !FileManager.default.fileExists(atPath: candidate.path, isDirectory: &isDirectory) || !isDirectory.boolValue {
            let parent = candidate.deletingLastPathComponent()
            guard parent != candidate else { return nil }
            candidate = parent
        }
        return candidate
    }

    public func stop() {
        pending?.cancel()
        pending = nil
        for source in sources { source.cancel() }
        sources.removeAll()
        descriptors.removeAll()
    }

    private func scheduleNotification() {
        pending?.cancel()
        let work = DispatchWorkItem { [onChange] in onChange() }
        pending = work
        queue.asyncAfter(deadline: .now() + debounce, execute: work)
    }
}
