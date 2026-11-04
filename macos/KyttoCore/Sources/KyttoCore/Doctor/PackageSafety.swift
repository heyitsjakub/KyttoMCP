import Foundation

/// What Kytto learned about one package when the user asked (§7.10).
///
/// Never fetched on its own: a check sends the package name — and its version,
/// when the launch pins one — to osv.dev and, for npm, to the npm registry,
/// and nothing else from the configuration.
public struct PackageSafetyReport: Equatable, Sendable {
    public struct Advisory: Equatable, Sendable {
        public let id: String
        public let summary: String?
        /// OSV's `MAL-` records: the package itself is malicious, not a bug in it.
        public var isMalware: Bool { id.hasPrefix("MAL-") }

        public init(id: String, summary: String?) {
            self.id = id
            self.summary = summary
        }
    }

    public let ecosystem: PackageLaunch.Ecosystem
    public let packageName: String
    /// The exact version that was asked about, or nil for the package as a whole.
    public let version: String?
    public let advisories: [Advisory]
    /// npm lifecycle scripts that run on install: name → command, as published.
    public let installScripts: [String: String]
    /// The npm registry's deprecation message for the checked version, or nil.
    public let deprecation: String?
    public let checkedAt: Date

    public init(
        ecosystem: PackageLaunch.Ecosystem,
        packageName: String,
        version: String?,
        advisories: [Advisory],
        installScripts: [String: String],
        deprecation: String? = nil,
        checkedAt: Date
    ) {
        self.ecosystem = ecosystem
        self.packageName = packageName
        self.version = version
        self.advisories = advisories
        self.installScripts = installScripts
        self.deprecation = deprecation
        self.checkedAt = checkedAt
    }

    /// Whether this report still describes what `launch` runs. An edit to the
    /// command must not leave an old verdict attached to a new package.
    public func describes(_ launch: PackageLaunch) -> Bool {
        launch.ecosystem == ecosystem && launch.name == packageName
            && (launch.isPinned ? launch.requestedVersion : nil) == version
    }
}

public enum PackageSafety {

    /// The package a server fetches by name, if it does.
    public static func launch(of server: Server) -> PackageLaunch? {
        guard server.transport == .stdio, !server.isBundled else { return nil }
        return PackageLaunch.parse(command: server.command, args: server.args)
    }

    /// The version to ask about: only one the launch pins. An unpinned launch
    /// is asked about as a whole package, which still finds malware records.
    public static func queriedVersion(of launch: PackageLaunch) -> String? {
        launch.isPinned ? launch.requestedVersion : nil
    }

    // MARK: - Findings

    /// Every package-safety finding for `server`: the look-alike check, which
    /// is offline and always runs, and the results of a check the user asked
    /// for, when there is one for the package this server runs now.
    public static func findings(for server: Server, report: PackageSafetyReport?) -> [DoctorFinding] {
        guard let launch = launch(of: server) else { return [] }
        var findings: [DoctorFinding] = []
        let checkAction: DoctorAction? = report?.describes(launch) == true ? nil : .checkPackageSafety
        let archived = isArchivedReference(launch.name, ecosystem: launch.ecosystem)
        if archived {
            findings.append(DoctorFinding(
                code: "archived-package",
                severity: .warning,
                title: "This reference server is archived and no longer maintained",
                detail: """
                The MCP project moved “\(launch.name)” to its archive of reference servers \
                that are no longer maintained\(launch.ecosystem == .npm ? ", and npm marks it deprecated" : ""). \
                It gets no fixes, security fixes included. Coding agents still install it \
                by name because they remember it.
                """,
                remediation: """
                Replace it with a server that the service itself, or another active \
                maintainer, publishes — and check who publishes that one before switching. \
                Kytto did not change anything on its own.
                """,
                action: checkAction
            ))
        }
        if let official = lookAlike(of: launch.name, ecosystem: launch.ecosystem) {
            let advice = isArchivedReference(official, ecosystem: launch.ecosystem)
                ? """
                  “\(official)” is itself archived and no longer maintained, so switching to \
                  it is not a fix either: use a maintained server for the same service.
                  """
                : "If you meant the official server, change the command to “\(official)”."
            findings.append(DoctorFinding(
                code: "look-alike-package",
                severity: .warning,
                title: "The package name imitates an official server",
                detail: """
                “\(launch.name)” is not “\(official)”, but its name is close enough to be \
                mistaken for it. Fake MCP packages with official-looking names have sent \
                machine details and secrets to attackers on install.
                """,
                remediation: """
                \(advice) If this package is the one you chose on purpose, check who \
                publishes it before the next launch — Check package safety asks osv.dev \
                about known malware.
                """,
                action: checkAction
            ))
        }
        guard let report, report.describes(launch) else { return findings }

        if let deprecation = report.deprecation, !archived {
            findings.append(DoctorFinding(
                code: "deprecated-package",
                severity: .warning,
                title: "The registry marks this package deprecated",
                detail: """
                npm marks \(launch.name)\(report.version.map { " \($0)" } ?? "") deprecated: \
                “\(truncated(deprecation))”. A deprecated package usually gets no more fixes, \
                security fixes included.
                """,
                remediation: """
                Move to the replacement the message names, or to another maintained server, \
                and check who publishes it before switching.
                """
            ))
        }

        let malware = report.advisories.filter(\.isMalware)
        if !malware.isEmpty {
            findings.append(DoctorFinding(
                code: "known-malware",
                severity: .error,
                title: "This package is listed as malware",
                detail: """
                osv.dev lists “\(launch.name)” as malicious (\(describe(malware))). A client \
                that launches this server runs that code with the server's environment, \
                tokens included.
                """,
                remediation: """
                Switch the server off in every client now, remove it, and rotate any secret \
                its environment carried. Kytto did not change anything on its own.
                """
            ))
        }
        let vulnerabilities = report.advisories.filter { !$0.isMalware }
        if !vulnerabilities.isEmpty {
            findings.append(DoctorFinding(
                code: "known-vulnerabilities",
                severity: .warning,
                title: "This version has known vulnerabilities",
                detail: "osv.dev lists \(describe(vulnerabilities)) for \(launch.name) \(report.version ?? "").",
                remediation: "Read the advisories and pin a release that fixes them."
            ))
        }
        if !report.installScripts.isEmpty {
            let scripts = report.installScripts.keys.sorted().map { name in
                "\(name): \(truncated(report.installScripts[name] ?? ""))"
            }
            findings.append(DoctorFinding(
                code: "install-script",
                severity: .warning,
                title: "The package runs a script when it is installed",
                detail: """
                \(launch.name)\(report.version.map { " \($0)" } ?? "") declares \
                \(scripts.joined(separator: "; ")). \(launch.runner) runs it before the \
                server starts, with your user's permissions — the route install-time \
                malware takes.
                """,
                remediation: """
                Read what the script does on the package page. Pinning an exact version \
                keeps a later release from adding or changing it.
                """
            ))
        }
        return findings
    }

    private static func describe(_ advisories: [PackageSafetyReport.Advisory]) -> String {
        let ids = advisories.prefix(5).map(\.id)
        let more = advisories.count > 5 ? " and \(advisories.count - 5) more" : ""
        return ids.joined(separator: ", ") + more
    }

    private static func truncated(_ text: String) -> String {
        let line = text.replacingOccurrences(of: "\n", with: " ")
        return line.count > 160 ? String(line.prefix(160)) + "…" : line
    }

    // MARK: - Look-alike names (offline)

    /// Official servers whose names get imitated, per registry. A small list on
    /// purpose: it only has to name what an imitation would be mistaken for.
    static let officialNPM = [
        "@modelcontextprotocol/server-everything",
        "@modelcontextprotocol/server-filesystem",
        "@modelcontextprotocol/server-memory",
        "@modelcontextprotocol/server-sequential-thinking",
        "@modelcontextprotocol/server-github",
        "@modelcontextprotocol/server-gitlab",
        "@modelcontextprotocol/server-slack",
        "@modelcontextprotocol/server-postgres",
        "@modelcontextprotocol/server-puppeteer",
        "@modelcontextprotocol/server-brave-search",
        "@modelcontextprotocol/server-google-maps",
        "@modelcontextprotocol/server-gdrive",
        "@modelcontextprotocol/server-redis",
        "@modelcontextprotocol/server-aws-kb-retrieval",
        "@modelcontextprotocol/server-everart",
        "@modelcontextprotocol/inspector",
        "@playwright/mcp",
        "@upstash/context7-mcp",
        "@notionhq/notion-mcp-server",
        "@stripe/mcp",
        "@supabase/mcp-server-supabase",
        "@sentry/mcp-server",
        "@cloudflare/mcp-server-cloudflare",
        "@browserbasehq/mcp",
        "@azure/mcp",
        "@heroku/mcp-server",
        "@shopify/dev-mcp",
        "@hubspot/mcp-server",
        "@paypal/mcp",
        "@vercel/mcp-adapter",
        "@figma/mcp",
        "@atlassian/mcp",
        "@linear/mcp",
        "firecrawl-mcp",
        "chrome-devtools-mcp",
        "xcodebuildmcp",
    ]

    static let officialPyPI = [
        "mcp-server-fetch",
        "mcp-server-git",
        "mcp-server-time",
        "mcp-server-sqlite",
        "mcp-server-sentry",
        "mcp-server-aws-kb-retrieval",
        "awslabs.core-mcp-server",
        "mcp",
    ]

    /// Reference servers the MCP project moved to `modelcontextprotocol/servers-archived`
    /// ("no longer maintained"). Every npm one is marked deprecated on the registry.
    /// They stay in the official lists above, because imitations of them still circulate.
    static let archivedNPM: Set<String> = [
        "@modelcontextprotocol/server-aws-kb-retrieval",
        "@modelcontextprotocol/server-brave-search",
        "@modelcontextprotocol/server-everart",
        "@modelcontextprotocol/server-gdrive",
        "@modelcontextprotocol/server-github",
        "@modelcontextprotocol/server-gitlab",
        "@modelcontextprotocol/server-google-maps",
        "@modelcontextprotocol/server-postgres",
        "@modelcontextprotocol/server-puppeteer",
        "@modelcontextprotocol/server-redis",
        "@modelcontextprotocol/server-slack",
    ]

    static let archivedPyPI: Set<String> = [
        "mcp-server-aws-kb-retrieval",
        "mcp-server-sentry",
        "mcp-server-sqlite",
    ]

    /// Whether `name` is one of the archived reference servers.
    public static func isArchivedReference(_ name: String, ecosystem: PackageLaunch.Ecosystem) -> Bool {
        let lowered = name.lowercased()
        switch ecosystem {
        case .npm: return archivedNPM.contains(lowered)
        case .python: return archivedPyPI.map(normalizePyPI).contains(normalizePyPI(lowered))
        }
    }

    /// The official package `name` imitates, or nil.
    ///
    /// An unscoped npm name that reduces to the same words as an official one
    /// (`mcp-server-github` for `@modelcontextprotocol/server-github`), a scope
    /// one or two letters away from an official scope, or a name one or two
    /// letters away from an official one. Exact official names never match.
    public static func lookAlike(of name: String, ecosystem: PackageLaunch.Ecosystem) -> String? {
        let lowered = name.lowercased()
        switch ecosystem {
        case .npm:
            if officialNPM.contains(lowered) { return nil }
            let (scope, bare) = split(lowered)
            for official in officialNPM {
                let (officialScope, officialBare) = split(official)
                if let scope, let officialScope, scope != officialScope,
                   distance(scope, officialScope) <= 2, distance(bare, officialBare) <= 1 {
                    return official
                }
                if scope == nil, officialScope != nil {
                    let core = coreWords(bare)
                    if core.count >= 3, core == coreWords(officialBare) { return official }
                }
                if scope == officialScope, bare != officialBare, bare.count >= 6,
                   distance(bare, officialBare) <= (bare.count >= 12 ? 2 : 1) {
                    return official
                }
            }
            return nil
        case .python:
            let normalized = normalizePyPI(lowered)
            if officialPyPI.map(normalizePyPI).contains(normalized) { return nil }
            for official in officialPyPI where official.count >= 6 {
                if distance(normalized, normalizePyPI(official)) <= (official.count >= 12 ? 2 : 1) {
                    return official
                }
            }
            return nil
        }
    }

    private static func split(_ name: String) -> (scope: String?, bare: String) {
        guard name.hasPrefix("@"), let slash = name.firstIndex(of: "/") else { return (nil, name) }
        return (String(name[name.index(after: name.startIndex)..<slash]), String(name[name.index(after: slash)...]))
    }

    /// The words of a name without the ones every MCP package carries.
    private static func coreWords(_ name: String) -> String {
        name.split(whereSeparator: { "-_.".contains($0) })
            .filter { !["mcp", "server", "mcpserver", "model", "context", "protocol"].contains($0) }
            .joined(separator: "-")
    }

    /// PEP 503: runs of `-`, `_` and `.` are one separator.
    private static func normalizePyPI(_ name: String) -> String {
        name.split(whereSeparator: { "-_.".contains($0) }).joined(separator: "-")
    }

    /// Levenshtein distance, capped early: only "0, 1, 2 or more" matters here.
    static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        if abs(a.count - b.count) > 2 { return 3 }
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var previous = Array(0...b.count)
        for i in 1...a.count {
            var current = [i] + Array(repeating: 0, count: b.count)
            for j in 1...b.count {
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            previous = current
        }
        return previous[b.count]
    }

    // MARK: - Registry answers

    /// The OSV query for one package, as JSON.
    public static func osvQuery(for launch: PackageLaunch) -> Data {
        var body: [String: Any] = [
            "package": ["name": launch.name, "ecosystem": launch.ecosystem == .npm ? "npm" : "PyPI"],
        ]
        if let version = queriedVersion(of: launch) { body["version"] = version }
        return (try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])) ?? Data()
    }

    /// Advisories from an OSV `/v1/query` answer. Without a version OSV
    /// answers for every release ever published, so only malware — which
    /// concerns the package, not a release — is kept then.
    public static func advisories(fromOSV data: Data, versionQueried: Bool) throws -> [PackageSafetyReport.Advisory] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CocoaError(.coderReadCorrupt)
        }
        let records = object["vulns"] as? [[String: Any]] ?? []
        let advisories = records.compactMap { record -> PackageSafetyReport.Advisory? in
            guard let id = record["id"] as? String, !id.isEmpty, id.count <= 64 else { return nil }
            let summary = (record["summary"] as? String).map { String($0.prefix(200)) }
            return .init(id: id, summary: summary)
        }
        return versionQueried ? advisories : advisories.filter(\.isMalware)
    }

    /// The install-time lifecycle scripts in an npm version document.
    public static func installScripts(fromNPMVersion data: Data) throws -> [String: String] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CocoaError(.coderReadCorrupt)
        }
        let scripts = object["scripts"] as? [String: Any] ?? [:]
        var result: [String: String] = [:]
        for name in ["preinstall", "install", "postinstall"] {
            if let command = scripts[name] as? String, !command.isEmpty { result[name] = command }
        }
        return result
    }

    /// The `deprecated` message in an npm version document, or nil when the
    /// version is not deprecated (npm leaves the field out, or sends `false`).
    public static func deprecation(fromNPMVersion data: Data) throws -> String? {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CocoaError(.coderReadCorrupt)
        }
        guard let message = object["deprecated"] as? String else { return nil }
        let trimmed = message.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? "deprecated" : String(trimmed.prefix(300))
    }
}
