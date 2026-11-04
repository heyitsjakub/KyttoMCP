import Foundation
import KyttoCore

/// An explicit, read-only package safety check (§7.10).
///
/// Called only when the user asks. It sends osv.dev the package name, its
/// registry, and the version when the launch pins one; for an npm package it
/// also reads that version's document from the npm registry to see whether it
/// runs install scripts or is deprecated. Nothing else from the configuration
/// leaves the Mac.
@MainActor
final class PackageSafetyLookup {
    enum LookupError: LocalizedError {
        case notAPackage
        case invalidPackage
        case requestFailed(String)
        case invalidResponse(String)

        var errorDescription: String? {
            switch self {
            case .notAPackage:
                "This server does not fetch a package by name, so there is no package to check."
            case .invalidPackage:
                "The package name is not safe to send in a lookup."
            case .requestFailed(let service):
                "Kytto could not reach \(service). No safety conclusion was made."
            case .invalidResponse(let service):
                "\(service) returned an answer Kytto could not read. No safety conclusion was made."
            }
        }
    }

    private static let maximumResponseSize = 4 * 1024 * 1024
    private static let osvEndpoint = URL(string: "https://api.osv.dev/v1/query")!
    private let session: URLSession
    private let now: () -> Date

    init(session: URLSession = .shared, now: @escaping () -> Date = Date.init) {
        self.session = session
        self.now = now
    }

    func check(_ launch: PackageLaunch) async throws -> PackageSafetyReport {
        guard MCPProvenanceLookup.isSafePackage(launch.name, allowingScopedName: launch.ecosystem == .npm) else {
            throw LookupError.invalidPackage
        }
        let version = PackageSafety.queriedVersion(of: launch)

        var query = URLRequest(url: Self.osvEndpoint)
        query.httpMethod = "POST"
        query.httpBody = PackageSafety.osvQuery(for: launch)
        query.setValue("application/json", forHTTPHeaderField: "Content-Type")
        let osvData = try await fetch(query, service: "osv.dev")
        let advisories: [PackageSafetyReport.Advisory]
        do {
            advisories = try PackageSafety.advisories(fromOSV: osvData, versionQueried: version != nil)
        } catch {
            throw LookupError.invalidResponse("osv.dev")
        }

        var scripts: [String: String] = [:]
        var deprecation: String?
        if launch.ecosystem == .npm {
            var components = URLComponents()
            components.scheme = "https"
            components.host = "registry.npmjs.org"
            components.path = "/\(launch.name)/\(version ?? "latest")"
            guard let url = components.url else { throw LookupError.invalidPackage }
            let data = try await fetch(URLRequest(url: url), service: "the npm registry")
            do {
                scripts = try PackageSafety.installScripts(fromNPMVersion: data)
                deprecation = try PackageSafety.deprecation(fromNPMVersion: data)
            } catch {
                throw LookupError.invalidResponse("The npm registry")
            }
        }

        return PackageSafetyReport(
            ecosystem: launch.ecosystem,
            packageName: launch.name,
            version: version,
            advisories: advisories,
            installScripts: scripts,
            deprecation: deprecation,
            checkedAt: now()
        )
    }

    private func fetch(_ request: URLRequest, service: String) async throws -> Data {
        var request = request
        request.timeoutInterval = 10
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        guard let endpoint = request.url else { throw LookupError.invalidPackage }
        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200,
                  MCPProvenanceLookup.isFirstPartyResponse(response.url, matching: endpoint),
                  data.count <= Self.maximumResponseSize
            else { throw LookupError.requestFailed(service) }
            return data
        } catch let error as LookupError {
            throw error
        } catch {
            throw LookupError.requestFailed(service)
        }
    }
}
