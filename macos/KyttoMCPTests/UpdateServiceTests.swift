import Foundation
import CryptoKit
import Testing
@testable import KyttoMCP

@MainActor
struct UpdateServiceTests {
    @Test("a newer verified macOS release is offered")
    func newerReleaseIsOffered() throws {
        let result = try UpdateService.decodeManifest(
            manifest(version: "1.0.5"),
            currentVersion: "1.0.4",
            checkedAt: Date(timeIntervalSince1970: 1_800_000_000)
        )

        #expect(result.status == .updateAvailable)
        #expect(result.currentVersion == "1.0.4")
        #expect(result.latestVersion == "1.0.5")
        #expect(result.sha256 == String(repeating: "a", count: 64))
    }

    @Test("the installed release is reported as current")
    func currentReleaseIsUpToDate() throws {
        let result = try UpdateService.decodeManifest(
            manifest(version: "1.0.4"),
            currentVersion: "1.0.4",
            checkedAt: .now
        )

        #expect(result.status == .upToDate)
    }

    @Test("a manifest cannot redirect the updater away from Kytto")
    func foreignDownloadIsRejected() {
        #expect(throws: UpdateError.self) {
            try UpdateService.decodeManifest(
                manifest(
                    version: "1.0.5",
                    downloadURL: "https://example.invalid/update.dmg"
                ),
                currentVersion: "1.0.4",
                checkedAt: .now
            )
        }
    }

    @Test("the download may follow its redirect to the GitHub release asset")
    func downloadRedirectChainIsAllowed() {
        // The exact chain download.php produces. The old check compared the end
        // of this chain against kytto.jakubhecht.sk, so every download failed.
        #expect(UpdateService.isAllowedArtifactURL(
            URL(string: "https://kytto.jakubhecht.sk/download.php?file=macos")!
        ))
        #expect(UpdateService.isAllowedArtifactURL(
            URL(string: "https://github.com/heyitsjakub/KyttoMCP/releases/download/v1.0.5.2/KyttoMCP-1.0.5.2.dmg")!
        ))
        #expect(UpdateService.isAllowedArtifactURL(
            URL(string: "https://release-assets.githubusercontent.com/github-production-release-asset/1320214041/x?sig=y")!
        ))
        // The CDN host GitHub used before the current one must keep working too.
        #expect(UpdateService.isAllowedArtifactURL(
            URL(string: "https://objects.githubusercontent.com/github-production-release-asset/1320214041/x")!
        ))
    }

    @Test("the download refuses to be redirected off the allowed hosts")
    func downloadRedirectChainRejectsForeignHosts() {
        let rejected = [
            "https://githubusercontent.com.evil.example/asset.dmg",
            "https://evil.example/KyttoMCP.dmg",
            "http://github.com/heyitsjakub/KyttoMCP/releases/download/v1/x.dmg",
            "https://github.com:8443/heyitsjakub/KyttoMCP/releases/download/v1/x.dmg",
            "https://user:pass@github.com/heyitsjakub/KyttoMCP/releases/download/v1/x.dmg",
            "file:///tmp/KyttoMCP.dmg",
        ]
        for candidate in rejected {
            #expect(
                !UpdateService.isAllowedArtifactURL(URL(string: candidate)!),
                "\(candidate) must not be an allowed download host"
            )
        }
    }

    @Test("automatic update checks run at most once per day")
    func automaticCheckCadence() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        #expect(UpdateService.isAutomaticCheckDue(lastAttempt: nil, now: now))
        #expect(!UpdateService.isAutomaticCheckDue(
            lastAttempt: now.addingTimeInterval(-23 * 60 * 60),
            now: now
        ))
        #expect(UpdateService.isAutomaticCheckDue(
            lastAttempt: now.addingTimeInterval(-24 * 60 * 60),
            now: now
        ))
    }

    @Test("a verified application replaces the old bundle and requests a relaunch")
    func successfulReplacementVerifiesVersionAndRelaunches() async throws {
        let root = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let current = root.appendingPathComponent("Kytto.app")
        let incoming = root.appendingPathComponent("incoming.app")
        try makeApplication(at: current, version: "1.0.4", marker: "old")
        try makeApplication(at: incoming, version: "1.0.5", marker: "new")

        var relaunchedURL: URL?
        let installer = UpdateInstaller(relaunchHandler: { url in
            relaunchedURL = url
            return true
        })
        let result = try await installer.install(
            artifactURL: incoming,
            expectedVersion: "1.0.5",
            destinationAppURL: current
        )

        #expect(result.version == "1.0.5")
        #expect(result.relaunched)
        #expect(relaunchedURL == current.standardizedFileURL)
        #expect(try version(of: current) == "1.0.5")
        #expect(try String(contentsOf: current.appendingPathComponent("Contents/marker")) == "new")
    }

    @Test("a relaunch failure restores the previous application")
    func relaunchFailureRollsBack() async throws {
        let root = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let current = root.appendingPathComponent("Kytto.app")
        let incoming = root.appendingPathComponent("incoming.app")
        try makeApplication(at: current, version: "1.0.4", marker: "old")
        try makeApplication(at: incoming, version: "1.0.5", marker: "new")

        let installer = UpdateInstaller(relaunchHandler: { _ in false })
        await #expect(throws: UpdateError.self) {
            try await installer.install(
                artifactURL: incoming,
                expectedVersion: "1.0.5",
                destinationAppURL: current
            )
        }
        #expect(try version(of: current) == "1.0.4")
        #expect(try String(contentsOf: current.appendingPathComponent("Contents/marker")) == "old")
    }

    @Test("invalid artifacts and unwritable destinations are rejected before replacement")
    func invalidArtifactAndPermissionsAreRejected() async throws {
        let root = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let installer = UpdateInstaller(relaunchHandler: { _ in true })
        let invalid = root.appendingPathComponent("not-an-app.zip")
        try Data("not an installer".utf8).write(to: invalid)
        let destination = root.appendingPathComponent("Kytto.app")

        await #expect(throws: UpdateError.self) {
            try await installer.install(
                artifactURL: invalid,
                expectedVersion: "1.0.5",
                destinationAppURL: destination
            )
        }

        let incoming = root.appendingPathComponent("incoming.app")
        try makeApplication(at: incoming, version: "1.0.5", marker: "new")
        let fileParent = root.appendingPathComponent("not-a-directory")
        try Data().write(to: fileParent)
        await #expect(throws: UpdateError.self) {
            try await installer.install(
                artifactURL: incoming,
                expectedVersion: "1.0.5",
                destinationAppURL: fileParent.appendingPathComponent("Kytto.app")
            )
        }
    }

    @Test("cancelling a streamed update never creates a staged installer")
    func cancellationDiscardsPartialDownload() async throws {
        let root = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }

        let artifact = Data(repeating: 0x41, count: 192 * 1024)
        let hash = SHA256.hash(data: artifact).map { String(format: "%02x", $0) }.joined()
        UpdateURLProtocol.manifest = manifest(version: "1.0.5", sha256: hash)
        UpdateURLProtocol.artifact = artifact
        defer {
            UpdateURLProtocol.manifest = nil
            UpdateURLProtocol.artifact = nil
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [UpdateURLProtocol.self]
        let session = URLSession(configuration: configuration)
        let service = UpdateService(
            session: session,
            defaults: UserDefaults(suiteName: "KyttoMCP.UpdateTests.\(UUID().uuidString)")!,
            stagingDirectory: root
        )

        await #expect(throws: UpdateError.self) {
            try await service.downloadAndStage(currentVersion: "1.0.4") { progress in
                if progress.phase == .downloading, progress.completedBytes > 0 {
                    service.cancelDownload()
                }
            }
        }
        #expect(service.stagedUpdate == nil)
        #expect(try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil).isEmpty)
    }

    private func manifest(
        version: String,
        downloadURL: String = "https://kytto.jakubhecht.sk/download.php?file=macos",
        sha256: String = String(repeating: "a", count: 64)
    ) -> Data {
        Data(#"""
        {
          "schema": 1,
          "version": "\#(version)",
          "channel": "beta",
          "releaseNotesURL": "https://kytto.jakubhecht.sk/changelog.php",
          "platforms": {
            "macos": {
              "displayName": "macOS",
              "downloadURL": "\#(downloadURL)",
              "sha256": "\#(sha256)"
            }
          }
        }
        """#.utf8)
    }

    private func makeTemporaryDirectory() throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("KyttoMCP.UpdateTests.\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    private func makeApplication(at url: URL, version: String, marker: String) throws {
        let contents = url.appendingPathComponent("Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
        let plist: [String: Any] = ["CFBundleShortVersionString": version]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: contents.appendingPathComponent("Info.plist"))
        try Data(marker.utf8).write(to: contents.appendingPathComponent("marker"))
    }

    private func version(of app: URL) throws -> String? {
        let data = try Data(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
        let plist = try PropertyListSerialization.propertyList(from: data, options: [], format: nil) as? [String: Any]
        return plist?["CFBundleShortVersionString"] as? String
    }
}

private final class UpdateURLProtocol: URLProtocol {
    nonisolated(unsafe) static var manifest: Data?
    nonisolated(unsafe) static var artifact: Data?

    override class func canInit(with request: URLRequest) -> Bool {
        request.url?.host == "kytto.jakubhecht.sk"
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url else { return }
        let data: Data?
        if url.path == "/update.php" {
            data = Self.manifest
        } else if url.path == "/download.php" {
            data = Self.artifact
        } else {
            data = nil
        }
        guard let data,
              let response = HTTPURLResponse(
                url: url,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Length": String(data.count)]
              ) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }

        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if url.path == "/download.php" {
            let chunkSize = 64 * 1024
            var offset = 0
            while offset < data.count {
                let end = min(offset + chunkSize, data.count)
                client?.urlProtocol(self, didLoad: data.subdata(in: offset..<end))
                offset = end
            }
        } else {
            client?.urlProtocol(self, didLoad: data)
        }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
