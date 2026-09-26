import Foundation

public enum ContractChangeKind: String, Codable, Sendable {
    case toolAdded
    case toolRemoved
    case descriptionChanged
    case inputSchemaChanged
    case annotationsChanged
}

public enum ContractChangeSeverity: String, Codable, Sendable {
    case info
    case warning
    case breaking
}

public struct ContractChange: Codable, Equatable, Sendable {
    public let kind: ContractChangeKind
    public let severity: ContractChangeSeverity
    public let toolName: String
    public let summary: String

    public init(
        kind: ContractChangeKind,
        severity: ContractChangeSeverity,
        toolName: String,
        summary: String
    ) {
        self.kind = kind
        self.severity = severity
        self.toolName = toolName
        self.summary = summary
    }
}

/// Compares the model-visible contract, not the server package version.
/// Descriptions and annotations matter because clients give them to the model.
public enum ContractGuard {
    public static func changes(from old: [ToolSummary], to new: [ToolSummary]) -> [ContractChange] {
        let before = Dictionary(uniqueKeysWithValues: old.map { ($0.name, $0) })
        let after = Dictionary(uniqueKeysWithValues: new.map { ($0.name, $0) })
        var result: [ContractChange] = []

        let beforeNames = Set(before.keys)
        let afterNames = Set(after.keys)

        for name in beforeNames.subtracting(afterNames).sorted() {
            result.append(ContractChange(
                kind: .toolRemoved,
                severity: .breaking,
                toolName: name,
                summary: "Tool removed"
            ))
        }
        for name in afterNames.subtracting(beforeNames).sorted() {
            result.append(ContractChange(
                kind: .toolAdded,
                severity: .info,
                toolName: name,
                summary: "New tool added"
            ))
        }
        for name in beforeNames.intersection(afterNames).sorted() {
            guard let lhs = before[name], let rhs = after[name] else { continue }
            if lhs.description != rhs.description {
                result.append(ContractChange(
                    kind: .descriptionChanged,
                    severity: .warning,
                    toolName: name,
                    summary: "Model-facing description changed"
                ))
            }
            if lhs.inputSchemaJSON != rhs.inputSchemaJSON {
                result.append(ContractChange(
                    kind: .inputSchemaChanged,
                    severity: .breaking,
                    toolName: name,
                    summary: "Input schema changed"
                ))
            }
            if lhs.annotations != rhs.annotations {
                result.append(ContractChange(
                    kind: .annotationsChanged,
                    severity: .warning,
                    toolName: name,
                    summary: "Safety annotations changed"
                ))
            }
        }
        return result
    }
}
