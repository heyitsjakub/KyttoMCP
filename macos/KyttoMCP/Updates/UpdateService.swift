import AppKit
import CryptoKit
import Foundation

@MainActor
final class UpdateService {
    static let manifestURL = URL(string: "https://kytto.jakubhecht.sk/update.php")!
    static let downloadURL = URL(
        string: "https://kytto.jakubhecht.sk/download.php?file=macos&utm_source=kytto-app&utm_medium=updater"
    )!
    static let automaticCheckInterval: TimeInterval = 24 * 60 * 60

    private static let lastAutomaticAttemptKey = "updates.lastAutomaticAttempt"
    private static let latestVersionKey = "updates.latestVersion"
    private static let latestCheckedAtKey = "updates.latestCheckedAt"
    private static let latestReleaseNotesURLKey = "updates.latestReleaseNotesURL"
    private static let latestSHA256Key = "updates.latestSHA256"
    private static let maximumManifestSize = 64 * 1024
    private static let maximumArtifactSize: Int64 = 2 * 1024 * 1024 * 1024

    private let session: URLSession
    private let defaults: UserDefaults
    private let now: () -> Date
    private let stagingDirectory: URL
    private let installer: UpdateInstaller

    private(set) var stagedUpdate: StagedUpdate?
    private var downloadInProgress = false
    private var downloadCancelled = false

    init(
        session: URLSession = .shared,
        defaults: UserDefaults = .standard,
        now: @escaping () -> Date = Date.init,
        stagingDirectory: URL? = nil,
        installer: UpdateInstaller? = nil
    ) {
        self.session = session
        self.defaults = defaults
        self.now = now
        self.stagingDirectory = stagingDirectory ?? Self.defaultStagingDirectory()
        self.installer = installer ?? UpdateInstaller()
    }

    func check(currentVersion: String, force: Bool) async throws -> UpdateCheckDTO {
        let checkedAt = now()
        if !force {
            let previous = defaults.object(forKey: Self.lastAutomaticAttemptKey) as? Date
            guard Self.isAutomaticCheckDue(lastAttempt: previous, now: checkedAt) else {
                let result = cachedResult(currentVersion: currentVersion) ?? UpdateCheckDTO(
                    status: .skipped,
                    currentVersion: currentVersion,
                    latestVersion: nil,
                    checkedAt: previous?.timeIntervalSince1970,
                    releaseNotesURL: nil,
                    sha256: nil
                )
                return result
            }

            // Record the attempt before starting it. Repeated offline launches
            // must not hammer the first-party endpoint every few seconds.
            defaults.set(checkedAt, forKey: Self.lastAutomaticAttemptKey)
        }

        var request = URLRequest(url: Self.manifestURL)
        request.timeoutInterval = 10
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse,
              http.statusCode == 200,
              http.url == Self.manifestURL else {
            throw UpdateError.invalidResponse
        }
        guard data.count <= Self.maximumManifestSize else {
            throw UpdateError.manifestTooLarge
        }

        let result = try Self.decodeManifest(
            data,
            currentVersion: currentVersion,
            checkedAt: checkedAt
        )
        cache(result)
        return result
    }

    /// Downloads the artifact into Kytto's private update staging directory.
    ///
    /// The caller receives a token, not a path. The artifact is not considered
    /// installed until `installStaged` has verified the bundle version after
    /// replacement. Cancellation is deliberately checked between streamed
    /// chunks so an interrupted download never becomes a usable stage.
    func downloadAndStage(
        currentVersion: String,
        progress: @escaping @MainActor (UpdateProgress) async -> Void = { _ in }
    ) async throws -> StagedUpdate {
        guard !downloadInProgress else { throw UpdateError.downloadInProgress }
        downloadInProgress = true
        downloadCancelled = false
        defer {
            downloadInProgress = false
            downloadCancelled = false
        }

        do {
            // The checksum is useful only when it belongs to a fresh, verified
            // manifest. A download click therefore performs a user-requested
            // manifest check even if the automatic check was yesterday.
            let check = try await check(currentVersion: currentVersion, force: true)
            guard check.status == .updateAvailable,
                  let version = check.latestVersion,
                  let expectedSHA256 = check.sha256,
                  let current = ReleaseVersion(currentVersion),
                  let latest = ReleaseVersion(version),
                  latest > current else {
                throw UpdateError.noUpdateAvailable
            }

            var request = URLRequest(url: Self.downloadURL)
            request.timeoutInterval = 60
            request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
            request.setValue("application/octet-stream", forHTTPHeaderField: "Accept")

            // Every hop is validated as it happens, by `redirectGuard`. Checking
            // only `http.url` cannot work here: it is the *end* of the chain, and
            // `download.php` exists precisely to redirect off this site.
            let redirectGuard = UpdateRedirectGuard()
            let (bytes, response) = try await session.bytes(for: request, delegate: redirectGuard)
            guard let http = response as? HTTPURLResponse,
                  http.statusCode == 200,
                  !redirectGuard.rejectedARedirect,
                  let servedURL = http.url,
                  Self.isAllowedArtifactURL(servedURL) else {
                throw UpdateError.invalidResponse
            }

            let expectedLength = response.expectedContentLength > 0
                ? response.expectedContentLength
                : nil
            if let expectedLength, expectedLength > Self.maximumArtifactSize {
                throw UpdateError.artifactTooLarge
            }

            let fileManager = FileManager.default
            try fileManager.createDirectory(
                at: stagingDirectory,
                withIntermediateDirectories: true,
                attributes: nil
            )
            let id = UUID().uuidString
            let temporaryURL = stagingDirectory.appendingPathComponent("Kytto-\(id).dmg.part")
            let finalURL = stagingDirectory.appendingPathComponent("Kytto-\(id).dmg")
            defer { try? fileManager.removeItem(at: temporaryURL) }

            guard fileManager.createFile(atPath: temporaryURL.path, contents: nil) else {
                throw UpdateError.cannotPrepareStaging
            }

            let handle = try FileHandle(forWritingTo: temporaryURL)
            defer { try? handle.close() }

            var hasher = SHA256()
            var buffer = Data()
            buffer.reserveCapacity(64 * 1024)
            var completed: Int64 = 0

            await progress(UpdateProgress(
                phase: .downloading,
                completedBytes: 0,
                totalBytes: expectedLength,
                fraction: expectedLength.map { _ in 0 },
                message: "Downloading the verified Kytto installer…"
            ))

            for try await byte in bytes {
                buffer.append(byte)
                if buffer.count >= 64 * 1024 {
                    try ensureDownloadActive()
                    try handle.write(contentsOf: buffer)
                    hasher.update(data: buffer)
                    completed += Int64(buffer.count)
                    buffer.removeAll(keepingCapacity: true)
                    guard completed <= Self.maximumArtifactSize else {
                        throw UpdateError.artifactTooLarge
                    }
                    await progress(UpdateProgress(
                        phase: .downloading,
                        completedBytes: completed,
                        totalBytes: expectedLength,
                        fraction: fraction(completed: completed, total: expectedLength),
                        message: "Downloading the verified Kytto installer…"
                    ))
                }
            }

            try ensureDownloadActive()
            if !buffer.isEmpty {
                try handle.write(contentsOf: buffer)
                hasher.update(data: buffer)
                completed += Int64(buffer.count)
            }
            guard completed <= Self.maximumArtifactSize else {
                throw UpdateError.artifactTooLarge
            }

            let actualSHA256 = Self.hex(hasher.finalize())
            await progress(UpdateProgress(
                phase: .verifying,
                completedBytes: completed,
                totalBytes: expectedLength,
                fraction: 1,
                message: "Verifying the installer checksum…"
            ))
            guard actualSHA256 == expectedSHA256.lowercased() else {
                throw UpdateError.checksumMismatch
            }

            try fileManager.moveItem(at: temporaryURL, to: finalURL)
            let stage = StagedUpdate(
                token: id,
                version: version,
                byteCount: completed,
                sha256: actualSHA256,
                fileURL: finalURL
            )
            let previous = stagedUpdate?.fileURL
            stagedUpdate = stage
            if let previous, previous != finalURL { try? fileManager.removeItem(at: previous) }
            await progress(UpdateProgress(
                phase: .staged,
                completedBytes: completed,
                totalBytes: expectedLength,
                fraction: 1,
                message: "Checksum verified. The installer is ready to install."
            ))
            return stage
        } catch let error as UpdateError {
            throw error
        } catch {
            if downloadCancelled || Task.isCancelled { throw UpdateError.cancelled }
            throw UpdateError.downloadFailed
        }
    }

    func cancelDownload() {
        guard downloadInProgress else { return }
        downloadCancelled = true
    }

    func installStaged(
        token: String,
        currentVersion: String,
        progress: @escaping @MainActor (UpdateProgress) -> Void = { _ in }
    ) async throws -> InstalledUpdate {
        guard let stagedUpdate, stagedUpdate.token == token else {
            throw UpdateError.noStagedUpdate
        }
        guard let current = ReleaseVersion(currentVersion),
              let stagedVersion = ReleaseVersion(stagedUpdate.version),
              stagedVersion > current else {
            throw UpdateError.noUpdateAvailable
        }
        guard Self.sha256(of: stagedUpdate.fileURL) == stagedUpdate.sha256.lowercased() else {
            throw UpdateError.checksumMismatch
        }

        progress(UpdateProgress(
            phase: .installing,
            completedBytes: stagedUpdate.byteCount,
            totalBytes: stagedUpdate.byteCount,
            fraction: 1,
            message: "Replacing Kytto safely…"
        ))
        let result = try await installer.install(
            artifactURL: stagedUpdate.fileURL,
            expectedVersion: stagedUpdate.version,
            destinationAppURL: Bundle.main.bundleURL,
            progress: progress
        )
        try? FileManager.default.removeItem(at: stagedUpdate.fileURL)
        self.stagedUpdate = nil
        return result
    }

    func openDownload() throws {
        guard NSWorkspace.shared.open(Self.downloadURL) else {
            throw UpdateError.couldNotOpenDownload
        }
    }

    private func cache(_ result: UpdateCheckDTO) {
        defaults.set(result.latestVersion, forKey: Self.latestVersionKey)
        defaults.set(result.checkedAt, forKey: Self.latestCheckedAtKey)
        defaults.set(result.releaseNotesURL, forKey: Self.latestReleaseNotesURLKey)
        defaults.set(result.sha256, forKey: Self.latestSHA256Key)
    }

    private func cachedResult(currentVersion: String) -> UpdateCheckDTO? {
        guard let latestRaw = defaults.string(forKey: Self.latestVersionKey),
              let current = ReleaseVersion(currentVersion),
              let latest = ReleaseVersion(latestRaw),
              let checkedAt = defaults.object(forKey: Self.latestCheckedAtKey) as? Double,
              let notes = defaults.string(forKey: Self.latestReleaseNotesURLKey),
              let sha256 = defaults.string(forKey: Self.latestSHA256Key) else {
            return nil
        }
        return UpdateCheckDTO(
            status: latest > current ? .updateAvailable : .upToDate,
            currentVersion: currentVersion,
            latestVersion: latestRaw,
            checkedAt: checkedAt,
            releaseNotesURL: notes,
            sha256: sha256
        )
    }

    static func isAutomaticCheckDue(lastAttempt: Date?, now: Date) -> Bool {
        guard let lastAttempt else { return true }
        let elapsed = now.timeIntervalSince(lastAttempt)
        return elapsed < 0 || elapsed >= automaticCheckInterval
    }

    static func decodeManifest(
        _ data: Data,
        currentVersion: String,
        checkedAt: Date
    ) throws -> UpdateCheckDTO {
        let manifest: UpdateManifest
        do {
            manifest = try JSONDecoder().decode(UpdateManifest.self, from: data)
        } catch {
            throw UpdateError.invalidManifest
        }

        guard manifest.schema == 1,
              manifest.channel == "beta",
              let current = ReleaseVersion(currentVersion),
              let latest = ReleaseVersion(manifest.version),
              let platform = manifest.platforms["macos"],
              isAllowedWebURL(manifest.releaseNotesURL, path: "/changelog.php"),
              isAllowedDownloadURL(platform.downloadURL, platform: "macos"),
              isSHA256(platform.sha256) else {
            throw UpdateError.invalidManifest
        }

        return UpdateCheckDTO(
            status: latest > current ? .updateAvailable : .upToDate,
            currentVersion: currentVersion,
            latestVersion: manifest.version,
            checkedAt: checkedAt.timeIntervalSince1970,
            releaseNotesURL: manifest.releaseNotesURL.absoluteString,
            sha256: platform.sha256.lowercased()
        )
    }

    private static func defaultStagingDirectory() -> URL {
        let applicationSupport = FileManager.default.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first ?? FileManager.default.temporaryDirectory
        return applicationSupport.appending(path: "Kytto/Updates")
    }

    private static func isAllowedWebURL(_ url: URL, path: String) -> Bool {
        url.scheme == "https"
            && url.host == "kytto.jakubhecht.sk"
            && url.port == nil
            && url.user == nil
            && url.password == nil
            && url.path == path
            && url.query == nil
            && url.fragment == nil
    }

    private static func isAllowedDownloadURL(_ url: URL, platform: String) -> Bool {
        guard isAllowedWebURLWithoutQuery(url, path: "/download.php"),
              let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return false
        }
        return components.queryItems == [URLQueryItem(name: "file", value: platform)]
    }

    /// The hosts the installer download is allowed to travel through: this site,
    /// which issues the redirect, GitHub, and GitHub's release-asset CDN.
    ///
    /// The CDN is matched by suffix deliberately. GitHub has already moved
    /// release assets from `objects.githubusercontent.com` to
    /// `release-assets.githubusercontent.com`; pinning whichever name is current
    /// would break every download again the next time they move it, long after
    /// this shipped and with nothing in the error to suggest why. The guarantee
    /// that actually matters is the SHA-256 from the verified manifest, checked
    /// against the bytes as they arrive — no redirect, hostile or otherwise, can
    /// produce a file with the expected digest.
    nonisolated static func isAllowedArtifactURL(_ url: URL) -> Bool {
        guard url.scheme == "https",
              url.port == nil,
              url.user == nil,
              url.password == nil,
              let host = url.host?.lowercased() else {
            return false
        }
        return host == "kytto.jakubhecht.sk"
            || host == "github.com"
            || host.hasSuffix(".githubusercontent.com")
    }

    private static func isAllowedWebURLWithoutQuery(_ url: URL, path: String) -> Bool {
        url.scheme == "https"
            && url.host == "kytto.jakubhecht.sk"
            && url.port == nil
            && url.user == nil
            && url.password == nil
            && url.path == path
            && url.fragment == nil
    }

    private static func isSHA256(_ value: String) -> Bool {
        value.count == 64 && value.allSatisfy { $0.isHexDigit }
    }

    private static func hex(_ digest: SHA256.Digest) -> String {
        digest.map { String(format: "%02x", $0) }.joined()
    }

    private static func sha256(of url: URL) -> String? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        var hasher = SHA256()
        while true {
            guard let chunk = try? handle.read(upToCount: 64 * 1024),
                  !chunk.isEmpty else {
                break
            }
            hasher.update(data: chunk)
        }
        return hex(hasher.finalize())
    }

    private func fraction(completed: Int64, total: Int64?) -> Double? {
        guard let total, total > 0 else { return nil }
        return min(1, max(0, Double(completed) / Double(total)))
    }

    private func ensureDownloadActive() throws {
        guard !downloadCancelled, !Task.isCancelled else {
            throw UpdateError.cancelled
        }
    }
}

struct UpdateProgress: Sendable {
    enum Phase: String, Sendable {
        case downloading
        case verifying
        case staged
        case installing
        case relaunching
    }

    let phase: Phase
    let completedBytes: Int64
    let totalBytes: Int64?
    let fraction: Double?
    let message: String
}

struct StagedUpdate: Sendable {
    let token: String
    let version: String
    let byteCount: Int64
    let sha256: String
    fileprivate let fileURL: URL
}

struct InstalledUpdate: Sendable {
    let version: String
    let relaunched: Bool
}

/// Replaces only the application bundle, never Kytto's user-data directory.
///
/// The old bundle is moved aside before the new one is moved into place. Every
/// failure after that point attempts to put the old bundle back, so a failed
/// installer cannot leave a half-copied application at the launch path.
@MainActor
final class UpdateInstaller {
    typealias RelaunchHandler = @MainActor (URL) async -> Bool

    private let fileManager: FileManager
    private let relaunchHandler: RelaunchHandler

    init(
        fileManager: FileManager = .default,
        relaunchHandler: @escaping RelaunchHandler = UpdateInstaller.defaultRelaunch
    ) {
        self.fileManager = fileManager
        self.relaunchHandler = relaunchHandler
    }

    func install(
        artifactURL: URL,
        expectedVersion: String,
        destinationAppURL: URL,
        progress: @escaping @MainActor (UpdateProgress) -> Void = { _ in }
    ) async throws -> InstalledUpdate {
        let (sourceAppURL, mountURL) = try applicationURL(for: artifactURL)
        defer {
            if let mountURL { detach(mountURL) }
        }

        guard sourceAppURL.standardizedFileURL != destinationAppURL.standardizedFileURL else {
            throw UpdateError.invalidArtifact
        }
        guard bundleVersion(at: sourceAppURL) == expectedVersion else {
            throw UpdateError.artifactVersionMismatch
        }

        let destination = destinationAppURL.standardizedFileURL
        let parent = destination.deletingLastPathComponent()
        try ensureWritable(parent)

        let transaction = parent.appendingPathComponent(".kytto-update-\(UUID().uuidString)")
        let replacement = transaction.appendingPathComponent(destination.lastPathComponent)
        let backup = parent.appendingPathComponent(".\(destination.lastPathComponent).backup-\(UUID().uuidString)")
        try fileManager.createDirectory(at: transaction, withIntermediateDirectories: true, attributes: nil)
        defer { try? fileManager.removeItem(at: transaction) }

        do {
            try fileManager.copyItem(at: sourceAppURL, to: replacement)
            guard bundleVersion(at: replacement) == expectedVersion else {
                throw UpdateError.artifactVersionMismatch
            }

            var movedOriginal = false
            var movedReplacement = false
            do {
                if fileManager.fileExists(atPath: destination.path) {
                    guard isApplicationBundle(destination) else {
                        throw UpdateError.invalidInstalledApplication
                    }
                    try fileManager.moveItem(at: destination, to: backup)
                    movedOriginal = true
                }
                try fileManager.moveItem(at: replacement, to: destination)
                movedReplacement = true
                guard bundleVersion(at: destination) == expectedVersion else {
                    throw UpdateError.artifactVersionMismatch
                }

                progress(UpdateProgress(
                    phase: .relaunching,
                    completedBytes: 0,
                    totalBytes: nil,
                    fraction: 1,
                    message: "The new version is verified. Relaunching Kytto…"
                ))
                guard await relaunchHandler(destination) else {
                    throw UpdateError.relaunchFailed
                }
                if movedOriginal { try? fileManager.removeItem(at: backup) }
                return InstalledUpdate(version: expectedVersion, relaunched: true)
            } catch {
                var rollbackFailed = false
                if movedReplacement && fileManager.fileExists(atPath: destination.path) {
                    do { try fileManager.removeItem(at: destination) } catch { rollbackFailed = true }
                }
                if movedOriginal && !fileManager.fileExists(atPath: destination.path) {
                    do { try fileManager.moveItem(at: backup, to: destination) } catch { rollbackFailed = true }
                }
                if rollbackFailed { throw UpdateError.rollbackFailed }
                throw error
            }
        } catch let error as UpdateError {
            throw error
        } catch {
            throw UpdateError.replacementFailed
        }
    }

    private func applicationURL(for artifactURL: URL) throws -> (URL, URL?) {
        if isApplicationBundle(artifactURL) {
            return (artifactURL, nil)
        }
        guard artifactURL.pathExtension.lowercased() == "dmg" else {
            throw UpdateError.invalidArtifact
        }

        let mountURL = try attach(artifactURL)
        guard let application = applicationBundles(in: mountURL).single else {
            detach(mountURL)
            throw UpdateError.invalidArtifact
        }
        return (application, mountURL)
    }

    private func applicationBundles(in root: URL) -> [URL] {
        var result: [URL] = []
        func visit(_ directory: URL, depth: Int) {
            guard depth <= 3 else { return }
            guard let children = try? fileManager.contentsOfDirectory(
                at: directory,
                includingPropertiesForKeys: [.isDirectoryKey],
                options: [.skipsHiddenFiles]
            ) else { return }
            for child in children.sorted(by: { $0.path < $1.path }) {
                guard (try? child.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true else { continue }
                if isApplicationBundle(child) {
                    result.append(child)
                } else {
                    visit(child, depth: depth + 1)
                }
            }
        }
        if isApplicationBundle(root) { return [root] }
        visit(root, depth: 0)
        return result
    }

    private func attach(_ artifactURL: URL) throws -> URL {
        let output = Pipe()
        let process = Process()
        process.executableURL = URL(filePath: "/usr/bin/hdiutil")
        process.arguments = ["attach", "-plist", "-nobrowse", "-readonly", artifactURL.path]
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            throw UpdateError.invalidArtifact
        }
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw UpdateError.invalidArtifact }

        guard let propertyList = try? PropertyListSerialization.propertyList(
            from: output.fileHandleForReading.readDataToEndOfFile(),
            options: [],
            format: nil
        ) as? [String: Any],
        let entities = propertyList["system-entities"] as? [[String: Any]],
        let mountPath = entities.compactMap({ $0["mount-point"] as? String }).first else {
            throw UpdateError.invalidArtifact
        }
        return URL(filePath: mountPath)
    }

    private func detach(_ mountURL: URL) {
        let process = Process()
        process.executableURL = URL(filePath: "/usr/bin/hdiutil")
        process.arguments = ["detach", mountURL.path, "-force"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try? process.run()
        process.waitUntilExit()
    }

    private func ensureWritable(_ directory: URL) throws {
        guard (try? directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
              fileManager.isWritableFile(atPath: directory.path) else {
            throw UpdateError.insufficientPermissions
        }
        let probe = directory.appendingPathComponent(".kytto-write-test-\(UUID().uuidString)")
        do {
            try Data([0]).write(to: probe, options: .atomic)
            try fileManager.removeItem(at: probe)
        } catch {
            try? fileManager.removeItem(at: probe)
            throw UpdateError.insufficientPermissions
        }
    }

    private func isApplicationBundle(_ url: URL) -> Bool {
        url.pathExtension.lowercased() == "app"
            && (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
            && fileManager.fileExists(atPath: url.appendingPathComponent("Contents/Info.plist").path)
    }

    private func bundleVersion(at appURL: URL) -> String? {
        guard let data = try? Data(contentsOf: appURL.appendingPathComponent("Contents/Info.plist")),
              let plist = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil)
                  as? [String: Any] else {
            return nil
        }
        return plist["CFBundleShortVersionString"] as? String
    }

    private static func defaultRelaunch(_ appURL: URL) async -> Bool {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        configuration.createsNewApplicationInstance = true
        configuration.allowsRunningApplicationSubstitution = false
        let didLaunch: Bool = await withCheckedContinuation { continuation in
            NSWorkspace.shared.openApplication(at: appURL, configuration: configuration) { application, error in
                continuation.resume(returning: error == nil && application != nil)
            }
        }
        guard didLaunch else { return false }

        // Let the install response reach the web layer before this process
        // leaves; the replacement is already running at this point.
        DispatchQueue.main.async { NSApp.terminate(nil) }
        return true
    }
}

private extension Collection {
    var single: Element? {
        count == 1 ? first : nil
    }
}

private struct UpdateManifest: Decodable {
    struct Platform: Decodable {
        let displayName: String
        let downloadURL: URL
        let sha256: String
    }

    let schema: Int
    let version: String
    let channel: String
    let releaseNotesURL: URL
    let platforms: [String: Platform]
}

private struct ReleaseVersion: Comparable {
    let components: [Int]

    init?(_ raw: String) {
        let core = raw.split(separator: "-", maxSplits: 1).first.map(String.init) ?? raw
        let parts = core.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...4).contains(parts.count),
              parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy(\.isNumber) }),
              parts.allSatisfy({ Int($0) != nil }) else {
            return nil
        }
        components = parts.compactMap { Int($0) }
    }

    static func < (lhs: ReleaseVersion, rhs: ReleaseVersion) -> Bool {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return left < right }
        }
        return false
    }
}

/// Validates each hop of the installer download instead of its destination.
///
/// This is the bug that made the in-app updater fail for every user from the day
/// it shipped: the old code compared the *final* URL against this site's own
/// host, but `download.php` exists to redirect to the GitHub release asset, so
/// that comparison could never succeed and every download ended in
/// `invalidResponse`. Nobody hit it earlier only because 1.0.5, the release that
/// introduced the full flow, did not launch at all.
///
/// Returning `nil` from the completion handler stops the chain rather than
/// following it; the redirect response is then delivered as the result, whose
/// non-200 status the caller rejects.
private final class UpdateRedirectGuard: NSObject, URLSessionTaskDelegate {
    private let lock = NSLock()
    private var rejected = false

    var rejectedARedirect: Bool {
        lock.lock()
        defer { lock.unlock() }
        return rejected
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        guard let url = request.url, UpdateService.isAllowedArtifactURL(url) else {
            lock.lock()
            rejected = true
            lock.unlock()
            completionHandler(nil)
            return
        }
        completionHandler(request)
    }
}

enum UpdateError: LocalizedError {
    case invalidResponse
    case manifestTooLarge
    case invalidManifest
    case noUpdateAvailable
    case downloadInProgress
    case downloadFailed
    case cancelled
    case artifactTooLarge
    case checksumMismatch
    case cannotPrepareStaging
    case noStagedUpdate
    case invalidArtifact
    case artifactVersionMismatch
    case invalidInstalledApplication
    case insufficientPermissions
    case replacementFailed
    case rollbackFailed
    case relaunchFailed
    case couldNotOpenDownload

    var errorDescription: String? {
        switch self {
        case .invalidResponse:
            "The update server did not return a valid response."
        case .manifestTooLarge:
            "The update response was unexpectedly large."
        case .invalidManifest:
            "The update information could not be verified."
        case .noUpdateAvailable:
            "There is no verified update ready to install."
        case .downloadInProgress:
            "An update download is already in progress."
        case .downloadFailed:
            "Kytto could not download the update installer. Check your connection and try again."
        case .cancelled:
            "The update download was cancelled."
        case .artifactTooLarge:
            "The update installer is larger than the safety limit."
        case .checksumMismatch:
            "The downloaded installer failed its checksum verification and was discarded."
        case .cannotPrepareStaging:
            "Kytto could not prepare its private update staging area."
        case .noStagedUpdate:
            "No verified installer is waiting to be installed. Download it again."
        case .invalidArtifact:
            "The downloaded installer is not a valid Kytto application image."
        case .artifactVersionMismatch:
            "The installer did not contain the version promised by the verified manifest."
        case .invalidInstalledApplication:
            "The current Kytto application is not a valid application bundle."
        case .insufficientPermissions:
            "Kytto cannot write beside the current application. Move it to a writable Applications folder and try again."
        case .replacementFailed:
            "Kytto could not replace the current application. The previous version was kept when possible."
        case .rollbackFailed:
            "The update failed and Kytto could not restore the previous application automatically. Do not quit Kytto; move the backup back into place or reinstall the verified installer."
        case .relaunchFailed:
            "The new application was not launched, so Kytto restored the previous version."
        case .couldNotOpenDownload:
            "Kytto could not open the update download in your browser."
        }
    }
}
