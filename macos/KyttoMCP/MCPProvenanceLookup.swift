import Foundation
import KyttoCore

/// An explicit, read-only lookup for package metadata.
///
/// The ordinary health/state path never makes a registry request. This service
/// is called only after the user presses "Check latest", and sends only the
/// package name inferred from a server command. It never sends configuration,
/// environment values or process output.
@MainActor
final class MCPProvenanceLookup {
    struct Result: Sendable {
        let version: String
        let checkedAt: Date
    }

    enum LookupError: LocalizedError {
        case unsupportedSource
        case invalidPackage
        case requestFailed
        case responseTooLarge
        case invalidMetadata

        var errorDescription: String? {
            switch self {
            case .unsupportedSource:
                "Kytto can check a latest release only for an identified npm or Python package."
            case .invalidPackage:
                "The inferred package name is not safe to use for a release lookup."
            case .requestFailed:
                "Kytto could not reach the package registry. No maintenance conclusion was made."
            case .responseTooLarge:
                "The package registry response was unexpectedly large. No maintenance conclusion was made."
            case .invalidMetadata:
                "The package registry did not return a comparable release version."
            }
        }
    }

    private static let maximumResponseSize = 128 * 1024
    private let session: URLSession
    private let now: () -> Date

    init(session: URLSession = .shared, now: @escaping () -> Date = Date.init) {
        self.session = session
        self.now = now
    }

    func latest(for provenance: MCPProvenance) async throws -> Result {
        guard let packageName = provenance.packageName else {
            throw LookupError.unsupportedSource
        }
        guard let endpoint = Self.endpoint(kind: provenance.kind, packageName: packageName) else {
            throw LookupError.invalidPackage
        }

        var request = URLRequest(url: endpoint)
        request.timeoutInterval = 10
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse,
                  http.statusCode == 200,
                  Self.isFirstPartyResponse(response.url, matching: endpoint) else {
                throw LookupError.requestFailed
            }
            guard data.count <= Self.maximumResponseSize else {
                throw LookupError.responseTooLarge
            }
            guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let version = Self.version(in: object, kind: provenance.kind),
                  MCPProvenanceResolver.isComparableVersion(version) else {
                throw LookupError.invalidMetadata
            }
            return Result(version: version, checkedAt: now())
        } catch let error as LookupError {
            throw error
        } catch {
            throw LookupError.requestFailed
        }
    }

    private static func endpoint(kind: MCPProvenanceKind, packageName: String) -> URL? {
        var components = URLComponents()
        components.scheme = "https"

        switch kind {
        case .npm:
            guard isSafePackage(packageName, allowingScopedName: true) else { return nil }
            components.host = "registry.npmjs.org"
            components.path = "/\(packageName)"
        case .python:
            guard isSafePackage(packageName, allowingScopedName: false) else { return nil }
            components.host = "pypi.org"
            components.path = "/pypi/\(packageName)/json"
        default:
            return nil
        }
        return components.url
    }

    private static func isSafePackage(_ packageName: String, allowingScopedName: Bool) -> Bool {
        guard !packageName.isEmpty,
              packageName.count <= 200,
              !packageName.hasPrefix("/"),
              !packageName.hasSuffix("/"),
              !packageName.contains(".."),
              !packageName.contains("\\") else {
            return false
        }
        if allowingScopedName {
            let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "@._-/"))
            return packageName.unicodeScalars.allSatisfy(allowed.contains)
                && packageName.filter { $0 == "/" }.count <= 1
        }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "._-"))
        return packageName.unicodeScalars.allSatisfy(allowed.contains)
    }

    private static func isFirstPartyResponse(_ url: URL?, matching endpoint: URL) -> Bool {
        guard let url else { return false }
        return url.scheme == "https"
            && url.host == endpoint.host
            && url.port == nil
            && url.user == nil
            && url.password == nil
            && url.path == endpoint.path
            && url.query == nil
            && url.fragment == nil
    }

    private static func version(in object: [String: Any], kind: MCPProvenanceKind) -> String? {
        switch kind {
        case .npm:
            return (object["dist-tags"] as? [String: Any])?["latest"] as? String
        case .python:
            return (object["info"] as? [String: Any])?["version"] as? String
        default:
            return nil
        }
    }
}
