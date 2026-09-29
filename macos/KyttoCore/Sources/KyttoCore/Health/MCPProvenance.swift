import Foundation

/// The source evidence Kytto can identify without running a server or querying
/// a registry. It is deliberately descriptive: a package name inferred from an
/// `npx`/`uvx` command is useful context, not proof of a release state.
public enum MCPProvenanceKind: String, Codable, Sendable {
    case npm
    case python
    case localPackage
    case localExecutable
    case remote
    case unknown
}

public enum MCPMaintenanceState: String, Codable, Sendable {
    case unchecked
    case healthy
    case unhealthy
    case unknownSource
    case staleButResponsive
}

public struct MCPProvenance: Codable, Equatable, Sendable {
    public let kind: MCPProvenanceKind
    public let packageName: String?
    public let sourceURL: String?
    public let installedVersion: String?
    public let latestVersion: String?
    public let latestCheckedAt: Date?
    /// `identified`, `inferred`, or `unknown`; the UI should not overstate it.
    public let confidence: String

    public init(
        kind: MCPProvenanceKind,
        packageName: String? = nil,
        sourceURL: String? = nil,
        installedVersion: String? = nil,
        latestVersion: String? = nil,
        latestCheckedAt: Date? = nil,
        confidence: String = "unknown"
    ) {
        self.kind = kind
        self.packageName = packageName
        self.sourceURL = sourceURL
        self.installedVersion = installedVersion
        self.latestVersion = latestVersion
        self.latestCheckedAt = latestCheckedAt
        self.confidence = confidence
    }
}

/// Read-only provenance adapters for the command shapes Kytto understands.
public enum MCPProvenanceResolver {

    /// Whether a version string is structured enough for a numeric comparison.
    /// Registry responses that do not pass this check remain informational rather
    /// than being labelled newer or older by accident.
    public static func isComparableVersion(_ raw: String) -> Bool {
        SemanticVersion(raw) != nil
    }

    public static func identify(server: Server) -> MCPProvenance {
        if server.transport != .stdio {
            return MCPProvenance(
                kind: .remote,
                sourceURL: server.url,
                confidence: server.url == nil ? "unknown" : "identified"
            )
        }

        let command = server.command ?? ""
        let basename = URL(filePath: command).lastPathComponent.lowercased()
        if case let (kind, package)? = packageToken(command: command, basename: basename, args: server.args) {
            return MCPProvenance(
                kind: kind,
                packageName: package,
                sourceURL: packageURL(kind: kind, package: package),
                confidence: "inferred"
            )
        }

        if command.hasPrefix("/") {
            if let package = localPackage(at: URL(filePath: command)) {
                return package
            }
            return MCPProvenance(kind: .localExecutable, confidence: "identified")
        }

        return MCPProvenance(kind: .unknown)
    }

    public static func maintenanceState(
        health: HealthResult?,
        provenance: MCPProvenance,
        installedVersion: String? = nil
    ) -> MCPMaintenanceState {
        guard let health else { return .unchecked }
        guard health.status == .passed else {
            if health.status == .failed { return .unhealthy }
            // Blocked on interactive consent says nothing about upkeep: the
            // check never completed, so nothing was measured. "Unhealthy" here
            // is how every OAuth-backed remote ends up looking abandoned.
            return health.status == .needsAuthorization ? .unchecked : .unknownSource
        }
        guard provenance.kind != .unknown,
              let latest = provenance.latestVersion,
              let installed = installedVersion ?? provenance.installedVersion,
              let latestVersion = SemanticVersion(latest),
              let installedVersion = SemanticVersion(installed)
        else {
            return provenance.kind == .unknown ? .unknownSource : .healthy
        }
        return latestVersion > installedVersion ? .staleButResponsive : .healthy
    }

    /// The registry package a command launches. Runners go through
    /// `PackageLaunch`, the same parser MCP Doctor pins with, so the name shown
    /// here and the name a version lookup asks about cannot disagree. `python -m`
    /// names a module rather than a package, and stays an inference.
    private static func packageToken(
        command: String,
        basename: String,
        args: [String]
    ) -> (MCPProvenanceKind, String)? {
        if let launch = PackageLaunch.parse(command: command, args: args) {
            return (launch.ecosystem == .python ? .python : .npm, launch.name)
        }

        let pythonCommands = ["python", "python3", "python3.11", "python3.12"]
        guard pythonCommands.contains(basename),
              let moduleIndex = args.firstIndex(of: "-m"),
              args.index(after: moduleIndex) < args.endIndex,
              let module = normalizePackage(args[args.index(after: moduleIndex)])
        else { return nil }
        return (.python, module)
    }

    private static func normalizePackage(_ raw: String) -> String? {
        let value: String
        if raw.hasPrefix("@"), let versionSeparator = raw.dropFirst().firstIndex(of: "@") {
            value = String(raw[..<versionSeparator])
        } else if let versionSeparator = raw.firstIndex(of: "@") {
            value = String(raw[..<versionSeparator])
        } else {
            value = raw
        }
        guard !value.isEmpty,
              value.count <= 200,
              value.allSatisfy({ $0.isLetter || $0.isNumber || ".-_/@".contains($0) })
        else { return nil }
        return value
    }

    private static func packageURL(kind: MCPProvenanceKind, package: String) -> String? {
        let host = kind == .python ? "https://pypi.org/project/" : "https://www.npmjs.com/package/"
        return host + (package.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? package)
    }

    private static func localPackage(at commandURL: URL) -> MCPProvenance? {
        var directory = commandURL.deletingLastPathComponent()
        for _ in 0..<8 {
            let manifest = directory.appending(path: "package.json")
            if let data = try? Data(contentsOf: manifest),
               let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let name = object["name"] as? String {
                let homepage = object["homepage"] as? String
                let version = object["version"] as? String
                return MCPProvenance(
                    kind: .localPackage,
                    packageName: name,
                    sourceURL: homepage?.hasPrefix("https://") == true ? homepage : nil,
                    installedVersion: version,
                    confidence: "identified"
                )
            }
            let parent = directory.deletingLastPathComponent()
            if parent == directory { break }
            directory = parent
        }
        return nil
    }
}

private struct SemanticVersion: Comparable {
    let components: [Int]

    init?(_ raw: String) {
        let core = raw.split(separator: "-", maxSplits: 1).first.map(String.init) ?? raw
        let parts = core.split(separator: ".", omittingEmptySubsequences: false)
        guard !parts.isEmpty, parts.allSatisfy({ !$0.isEmpty && Int($0) != nil }) else { return nil }
        components = parts.map { Int($0)! }
    }

    static func < (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return left < right }
        }
        return false
    }
}
