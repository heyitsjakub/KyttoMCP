import Foundation

public enum ContextRecommendationKind: String, Codable, Sendable {
    case measure
    case overBudget
    case heavyServer
    case duplicateTool
}

public struct ContextRecommendation: Codable, Equatable, Sendable {
    public let kind: ContextRecommendationKind
    public let severity: DoctorSeverity
    public let summary: String
    public let serverIDs: [String]

    public init(
        kind: ContextRecommendationKind,
        severity: DoctorSeverity,
        summary: String,
        serverIDs: [String]
    ) {
        self.kind = kind
        self.severity = severity
        self.summary = summary
        self.serverIDs = serverIDs
    }
}

public struct ContextProfileAnalysis: Codable, Equatable, Sendable {
    public let estimatedTokens: Int
    public let measuredServerCount: Int
    public let totalServerCount: Int
    public let recommendations: [ContextRecommendation]

    public init(
        estimatedTokens: Int,
        measuredServerCount: Int,
        totalServerCount: Int,
        recommendations: [ContextRecommendation]
    ) {
        self.estimatedTokens = estimatedTokens
        self.measuredServerCount = measuredServerCount
        self.totalServerCount = totalServerCount
        self.recommendations = recommendations
    }
}

public enum ContextOptimizer {
    public static func analyze(profile: ServerProfile, servers: [Server]) -> ContextProfileAnalysis {
        let members = servers.filter { profile.serverIDs.contains($0.id) }
        let measured = members.filter { $0.tokenWeight != nil }
        let total = measured.reduce(0) { $0 + ($1.tokenWeight?.estimate ?? 0) }
        var recommendations: [ContextRecommendation] = []

        let unmeasured = members.filter { $0.tokenWeight == nil }.map(\.id).sorted()
        if !unmeasured.isEmpty {
            recommendations.append(ContextRecommendation(
                kind: .measure,
                severity: .info,
                summary: "Run health checks for \(unmeasured.count) unmeasured server\(unmeasured.count == 1 ? "" : "s") before trusting the total.",
                serverIDs: unmeasured
            ))
        }

        if let budget = profile.tokenBudget, total > budget {
            let heaviest = measured.sorted {
                ($0.tokenWeight?.estimate ?? 0) > ($1.tokenWeight?.estimate ?? 0)
            }
            var remaining = total
            var candidates: [String] = []
            for server in heaviest where remaining > budget {
                candidates.append(server.id)
                remaining -= server.tokenWeight?.estimate ?? 0
            }
            recommendations.append(ContextRecommendation(
                kind: .overBudget,
                severity: .warning,
                summary: "Profile is ~\(total - budget) tokens over its \(budget)-token budget. Review the suggested heavy servers.",
                serverIDs: candidates
            ))
        }

        if let heaviest = measured.max(by: {
            ($0.tokenWeight?.estimate ?? 0) < ($1.tokenWeight?.estimate ?? 0)
        }), let weight = heaviest.tokenWeight, weight.estimate >= 20_000 {
            recommendations.append(ContextRecommendation(
                kind: .heavyServer,
                severity: .warning,
                summary: "“\(heaviest.name)” alone contributes ~\(weight.estimate) tokens.",
                serverIDs: [heaviest.id]
            ))
        }

        var owners: [String: [String]] = [:]
        for server in members {
            for tool in server.health?.tools ?? [] {
                owners[tool.name, default: []].append(server.id)
            }
        }
        for (tool, serverIDs) in owners.filter({ Set($0.value).count > 1 }).sorted(by: { $0.key < $1.key }) {
            recommendations.append(ContextRecommendation(
                kind: .duplicateTool,
                severity: .warning,
                summary: "Tool “\(tool)” is exposed by more than one server, which can confuse tool selection.",
                serverIDs: Array(Set(serverIDs)).sorted()
            ))
        }

        return ContextProfileAnalysis(
            estimatedTokens: total,
            measuredServerCount: measured.count,
            totalServerCount: members.count,
            recommendations: recommendations
        )
    }
}
