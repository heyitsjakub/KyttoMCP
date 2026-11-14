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

/// Whether calls written for the old contract still fit the new one.
///
/// This is one question, and security is another (roadmap §18): a new optional
/// argument is compatible, yet it can ask the model for local data. Keeping the
/// two apart stops "Breaking" from standing in for "look at this".
public enum ContractCompatibility: String, Codable, Sendable {
    /// Existing calls keep working: an added tool, a new optional argument,
    /// changed wording.
    case compatible
    /// Existing calls can fail: a removed tool or argument, a new required
    /// argument, a narrowed type or value list.
    case breaking
    /// Kytto cannot tell from the schema alone, for example a changed pattern,
    /// `$ref` or `oneOf`.
    case unknown
}

/// Whether the change touches what the model is told or allowed to do, so
/// that a person should read it. This is a reason to look, never a verdict
/// that the server is malicious.
public enum ContractSecurityRelevance: String, Codable, Sendable {
    case none
    case review
}

/// Bounded excerpts of the model-visible fields that changed. These explain a
/// review; they do not certify that a server implements its stated safeguards.
public struct ContractFieldChange: Codable, Equatable, Sendable {
    public let path: String
    public let before: String?
    public let after: String?
    public let explanation: String?

    public init(path: String, before: String?, after: String?, explanation: String? = nil) {
        self.path = path
        self.before = before
        self.after = after
        self.explanation = explanation
    }
}

public struct ContractChange: Codable, Equatable, Sendable {
    public let kind: ContractChangeKind
    public let severity: ContractChangeSeverity
    public let toolName: String
    public let summary: String
    public let details: [ContractFieldChange]?
    /// Nil on a change recorded before the two labels existed; the UI then
    /// falls back to `severity`.
    public let compatibility: ContractCompatibility?
    public let compatibilityReason: String?
    public let security: ContractSecurityRelevance?
    public let securityReason: String?

    public init(
        kind: ContractChangeKind,
        severity: ContractChangeSeverity,
        toolName: String,
        summary: String,
        details: [ContractFieldChange]? = nil,
        compatibility: ContractCompatibility? = nil,
        compatibilityReason: String? = nil,
        security: ContractSecurityRelevance? = nil,
        securityReason: String? = nil
    ) {
        self.kind = kind
        self.severity = severity
        self.toolName = toolName
        self.summary = summary
        self.details = details
        self.compatibility = compatibility
        self.compatibilityReason = compatibilityReason
        self.security = security
        self.securityReason = securityReason
    }

    /// The one-word severity, derived from the two labels: only a change that
    /// can break existing calls is `breaking`; one worth reading for security
    /// is a `warning`.
    static func severity(_ compatibility: ContractCompatibility, _ security: ContractSecurityRelevance) -> ContractChangeSeverity {
        if compatibility == .breaking { return .breaking }
        return security == .review || compatibility == .unknown ? .warning : .info
    }
}

/// Compares the model-visible contract, not the server package version.
/// Descriptions and annotations matter because clients give them to the model.
public enum ContractGuard {
    public static func changes(from old: [ToolSummary], to new: [ToolSummary]) -> [ContractChange] {
        // A server may list one name twice (or several nameless tools, which all
        // become "(unnamed)"). That is broken input, not a reason to crash; clients
        // resolve it last-wins, so this does too.
        let before = Dictionary(old.map { ($0.name, $0) }, uniquingKeysWith: { _, last in last })
        let after = Dictionary(new.map { ($0.name, $0) }, uniquingKeysWith: { _, last in last })
        var result: [ContractChange] = []

        let beforeNames = Set(before.keys)
        let afterNames = Set(after.keys)

        func change(
            _ kind: ContractChangeKind, _ name: String, _ summary: String, details: [ContractFieldChange]? = nil,
            _ compatibility: ContractCompatibility, _ compatibilityReason: String,
            _ security: ContractSecurityRelevance, _ securityReason: String?
        ) -> ContractChange {
            ContractChange(
                kind: kind, severity: ContractChange.severity(compatibility, security), toolName: name,
                summary: summary, details: details,
                compatibility: compatibility, compatibilityReason: compatibilityReason,
                security: security, securityReason: securityReason
            )
        }

        for name in beforeNames.subtracting(afterNames).sorted() {
            result.append(change(
                .toolRemoved, name, "Tool removed",
                .breaking, "Anything that calls this tool will now fail.",
                .none, nil
            ))
        }
        for name in afterNames.subtracting(beforeNames).sorted() {
            result.append(change(
                .toolAdded, name, "New tool added",
                .compatible, "Existing calls are unaffected.",
                .review, "The model can now call a tool you have not reviewed. Read what it does before relying on this server."
            ))
        }
        for name in beforeNames.intersection(afterNames).sorted() {
            guard let lhs = before[name], let rhs = after[name] else { continue }
            if lhs.description != rhs.description {
                result.append(change(
                    .descriptionChanged, name, "Model-facing description changed",
                    details: [field("description", lhs.description, rhs.description)],
                    .compatible, "Wording does not change which arguments a call accepts.",
                    .review, "The model reads this text as instructions. A changed description is not malicious by itself; check that it still only describes the tool."
                ))
            }
            if !sameSchema(lhs.inputSchemaJSON, rhs.inputSchemaJSON) {
                let assessment = assessSchema(lhs.inputSchemaJSON, rhs.inputSchemaJSON)
                result.append(change(
                    .inputSchemaChanged, name, assessment.summary,
                    details: schemaDetails(lhs.inputSchemaJSON, rhs.inputSchemaJSON),
                    assessment.compatibility, assessment.compatibilityReason,
                    assessment.security, assessment.securityReason
                ))
            }
            if lhs.annotations != rhs.annotations {
                result.append(change(
                    .annotationsChanged, name, "Safety annotations changed",
                    details: annotationDetails(lhs.annotations, rhs.annotations),
                    .compatible, "Annotations do not change which arguments a call accepts.",
                    .review, annotationSecurityReason(lhs.annotations, rhs.annotations)
                ))
            }
        }
        return result
    }

    /// Clients use these hints to decide what to ask the user about, so a hint
    /// that moves either way is worth a look: a tool newly claiming to be
    /// read-only may be approved without a prompt.
    static func annotationSecurityReason(_ before: ToolAnnotations?, _ after: ToolAnnotations?) -> String {
        if after?.readOnlyHint == true, before?.readOnlyHint != true {
            return "The tool now claims to be read-only, which some clients use to skip asking you. Hints are not enforced; check that the tool really only reads."
        }
        if before?.readOnlyHint == true, after?.readOnlyHint != true {
            return "The tool no longer claims to be read-only. It may now change data."
        }
        if after?.destructiveHint == true, before?.destructiveHint != true {
            return "The tool now says it can make destructive changes."
        }
        if after?.openWorldHint == true, before?.openWorldHint != true {
            return "The tool now says it reaches outside systems."
        }
        return "Clients use these hints when deciding what to ask you about. They are hints, not enforced permissions."
    }

    /// Whether a saved baseline predates schema and annotation capture.
    ///
    /// Early versions stored each tool's name and description only. Such a
    /// baseline has no tool with either field, and comparing its missing
    /// values against a current contract reports every tool twice for nothing
    /// (roadmap §18). A baseline that holds either field for at least one tool
    /// was taken by a version that captures them, so a missing value there is
    /// what the server really sent.
    public static func isLegacyBaseline(_ baseline: [ToolSummary]) -> Bool {
        !baseline.isEmpty && baseline.allSatisfy { $0.inputSchemaJSON == nil && $0.annotations == nil }
    }

    /// A legacy baseline with the fields it never recorded taken from the
    /// current contract, so comparison starts from here.
    ///
    /// Name and description stay the baseline's own: those were reviewed, and a
    /// change to them is still reported. Tools the current contract no longer
    /// has are kept as they are, so their removal is reported too.
    public static func upgradingLegacyBaseline(_ baseline: [ToolSummary], with current: [ToolSummary]) -> [ToolSummary] {
        guard isLegacyBaseline(baseline) else { return baseline }
        let now = Dictionary(current.map { ($0.name, $0) }, uniquingKeysWith: { _, last in last })
        return baseline.map { tool in
            guard let match = now[tool.name] else { return tool }
            return ToolSummary(
                name: tool.name,
                description: tool.description,
                inputSchemaJSON: match.inputSchemaJSON,
                annotations: match.annotations,
                tokenCount: tool.tokenCount
            )
        }
    }

    static let excerptLimit = 800

    /// Bounded, comparable excerpts of a before/after pair.
    ///
    /// A plain prefix would make two long texts that differ only near the end
    /// look identical, which is exactly how an appended instruction would hide.
    /// When either side is too long, both are cut to the same window around
    /// the first difference. Invisible and format characters are spelled out,
    /// so a change made only of them never renders as "no change".
    static func excerpts(_ before: String?, _ after: String?) -> (String?, String?) {
        let lhs = before.map { Array($0.unicodeScalars) }
        let rhs = after.map { Array($0.unicodeScalars) }
        let limit = excerptLimit
        guard max(lhs?.count ?? 0, rhs?.count ?? 0) > limit else {
            return (before.map(visible), after.map(visible))
        }
        var start = 0
        if let lhs, let rhs {
            var common = 0
            while common < lhs.count, common < rhs.count, lhs[common] == rhs[common] { common += 1 }
            start = max(0, common - limit / 4)
        }
        func window(_ scalars: [Unicode.Scalar]?) -> String? {
            guard let scalars else { return nil }
            let lower = min(start, scalars.count)
            let upper = min(scalars.count, lower + limit)
            var text = visible(String(String.UnicodeScalarView(scalars[lower..<upper])))
            if lower > 0 { text = "[from character \(lower + 1)] …" + text }
            if upper < scalars.count { text += "… [truncated]" }
            return text
        }
        return (window(lhs), window(rhs))
    }

    private static func visible(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: invisible) else { return text }
        var result = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            if invisible(scalar) {
                result.append(contentsOf: String(format: "⟨U+%04X⟩", scalar.value).unicodeScalars)
            } else {
                result.append(scalar)
            }
        }
        return String(result)
    }

    private static func invisible(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.properties.generalCategory {
        case .format, .control:
            return scalar != "\n" && scalar != "\t"
        default:
            return scalar.properties.isVariationSelector || scalar.value == 0x034F
                || (0x115F...0x1160).contains(scalar.value) || scalar.value == 0x3164 || scalar.value == 0xFFA0
        }
    }

    private static func field(_ path: String, _ before: String?, _ after: String?, explanation: String? = nil) -> ContractFieldChange {
        let (lhs, rhs) = excerpts(before, after)
        return ContractFieldChange(path: path, before: lhs, after: rhs, explanation: explanation)
    }

    private static func serialized(_ value: Any?) -> String? {
        guard let value else { return nil }
        guard let data = try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .fragmentsAllowed, .withoutEscapingSlashes]),
              let text = String(data: data, encoding: .utf8) else { return nil }
        return text
    }

    private static func annotationDetails(_ before: ToolAnnotations?, _ after: ToolAnnotations?) -> [ContractFieldChange] {
        let fields: [(String, Bool?, Bool?)] = [
            ("readOnlyHint", before?.readOnlyHint, after?.readOnlyHint),
            ("destructiveHint", before?.destructiveHint, after?.destructiveHint),
            ("idempotentHint", before?.idempotentHint, after?.idempotentHint),
            ("openWorldHint", before?.openWorldHint, after?.openWorldHint),
        ]
        var result = fields.compactMap { name, lhs, rhs -> ContractFieldChange? in
            guard lhs != rhs else { return nil }
            return ContractFieldChange(path: "annotations." + name, before: lhs.map(String.init), after: rhs.map(String.init), explanation: "An annotation is a hint to the client, not an enforced permission.")
        }
        if result.isEmpty {
            result.append(ContractFieldChange(path: "annotations", before: before == nil ? nil : "{}", after: after == nil ? nil : "{}"))
        }
        return result
    }

    private static func schemaDetails(_ before: String?, _ after: String?) -> [ContractFieldChange] {
        // Parsing is bounded independently of rendering. Large or malformed
        // contracts remain changes, with a raw excerpt instead of a deep walk.
        guard let before, let after, before.utf8.count <= 1_000_000, after.utf8.count <= 1_000_000,
              !repeatsKeys(before), !repeatsKeys(after),
              let lhs = try? JSONSerialization.jsonObject(with: Data(before.utf8), options: [.fragmentsAllowed]),
              let rhs = try? JSONSerialization.jsonObject(with: Data(after.utf8), options: [.fragmentsAllowed]) else {
            return [field("inputSchema", before, after, explanation: "Inspect the original schema; a structured comparison is unavailable.")]
        }
        var result: [ContractFieldChange] = []
        var omitted = false
        var visited = 0
        func visit(_ lhs: Any?, _ rhs: Any?, _ path: String, _ depth: Int) {
            visited += 1
            if result.count >= 40 || visited > 500 { omitted = true; return }
            let leftBytes = lhs.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
            let rightBytes = rhs.flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
            guard leftBytes != rightBytes else { return }
            if depth < 12, let left = lhs as? [String: Any], let right = rhs as? [String: Any] {
                for key in Set(left.keys).union(right.keys).sorted() {
                    if key.utf8.count > 256 || path.utf8.count > 1_024 {
                        let oldField = left[key].flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
                        let newField = right[key].flatMap { try? JSONSerialization.data(withJSONObject: $0, options: [.sortedKeys, .fragmentsAllowed]) }
                        if oldField == newField { continue }
                        if result.count >= 40 { omitted = true; break }
                        result.append(field(
                            "inputSchema/[long field path]", serialized(left[key]), serialized(right[key]),
                            explanation: "The schema field name or path is too long to display. Inspect the complete schema."
                        ))
                        continue
                    }
                    // JSON Pointer escaping makes names containing '/' or '~'
                    // unambiguous. Arrays remain whole so their order is visible.
                    let escaped = key.replacingOccurrences(of: "~", with: "~0").replacingOccurrences(of: "/", with: "~1")
                    visit(left[key], right[key], path + "/" + escaped, depth + 1)
                }
                return
            }
            let lower = path.lowercased()
            let guardRelated = ["dry_run", "dryrun", "confirm", "approval"].contains { lower.contains($0) }
                || (lower.hasSuffix("/required") && ((serialized(lhs) ?? "") + (serialized(rhs) ?? "")).lowercased().contains("confirm"))
            let explanation = guardRelated
                ? "A confirmation or dry-run field changed. This may alter how the model requests a guarded action; verify the server's actual enforcement."
                : (path.hasSuffix("/required") ? "Required arguments changed; existing calls may need different inputs." : nil)
            result.append(field(path, serialized(lhs), serialized(rhs), explanation: explanation))
        }
        visit(lhs, rhs, "inputSchema", 0)
        if omitted {
            result.append(ContractFieldChange(path: "inputSchema", before: nil, after: nil, explanation: "Additional changes omitted. Inspect the complete schema before acknowledging."))
        }
        if result.isEmpty {
            result.append(field("inputSchema", before, after))
        }
        return result
    }

    /// Whether two input schemas say the same thing.
    ///
    /// The stored text is the raw slice the server sent, so a server that only
    /// re-serializes its schema — keys in another order, other whitespace — would
    /// otherwise read as a breaking change, and false alarms teach people to
    /// acknowledge without reading. Object keys are sorted; array order is kept,
    /// because `enum`, `oneOf` or `prefixItems` can mean something else reordered.
    /// If either side does not parse, the raw text decides, so a change is never
    /// hidden by the canonicalization.
    static func sameSchema(_ lhs: String?, _ rhs: String?) -> Bool {
        if lhs == rhs { return true }
        guard let lhs, let rhs,
              let left = canonical(lhs),
              let right = canonical(rhs)
        else { return false }
        return left == right
    }

    static func canonical(_ json: String) -> Data? {
        guard json.utf8.count <= 1_000_000, !repeatsKeys(json) else { return nil }
        guard let object = try? JSONSerialization.jsonObject(
            with: Data(json.utf8),
            options: [.fragmentsAllowed]
        ) else { return nil }
        return try? JSONSerialization.data(
            withJSONObject: object,
            options: [.sortedKeys, .fragmentsAllowed, .withoutEscapingSlashes]
        )
    }

    /// Whether any object in the schema names a key twice.
    ///
    /// `JSONSerialization` keeps the first value of a repeated key; clients'
    /// parsers keep the last. Canonicalizing would therefore drop exactly the
    /// value the model is shown, so a schema like this is compared as raw text
    /// instead, and never collapses into "unchanged".
    static func repeatsKeys(_ json: String) -> Bool {
        guard let document = try? JSONDocument.parse(json) else { return false }
        var pending = [document.root]
        while let node = pending.popLast() {
            switch node.kind {
            case .object(let members):
                var seen = Set<String>()
                for member in members {
                    if !seen.insert(member.key).inserted { return true }
                    pending.append(member.value)
                }
            case .array(let elements):
                pending.append(contentsOf: elements)
            default:
                break
            }
        }
        return false
    }
}
