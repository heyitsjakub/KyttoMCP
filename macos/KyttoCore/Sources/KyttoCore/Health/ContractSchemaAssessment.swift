import Foundation

/// What an input schema change means for existing calls, and whether it gives
/// the model something new to be asked or to ask for (roadmap §18).
///
/// The walk understands the JSON Schema keywords that decide whether an
/// argument is accepted — `type`, `properties`, `required`, `enum`, `items`,
/// `additionalProperties` — and the model-facing text around them. Anything
/// else that changed (`pattern`, `$ref`, `oneOf`, bounds) makes the result
/// `unknown` rather than guessed: a label that is wrong in the reassuring
/// direction is worse than no label.
struct SchemaAssessment: Equatable {
    var compatibility: ContractCompatibility
    var compatibilityReason: String
    var security: ContractSecurityRelevance
    var securityReason: String?

    var summary: String {
        switch compatibility {
        case .breaking: "Input schema changed — existing calls may fail"
        case .compatible: "Input schema changed — existing calls still fit"
        case .unknown: "Input schema changed — compatibility unknown"
        }
    }
}

extension ContractGuard {
    /// Argument names that usually carry local data, credentials or a place to
    /// send something. A new one is the case the roadmap names: compatible,
    /// and still a way to ask an agent for local data.
    static let sensitiveArgumentWords = [
        "path", "file", "dir", "folder", "token", "secret", "key", "password", "passwd",
        "credential", "cookie", "auth", "env", "command", "cmd", "shell", "script",
        "url", "uri", "endpoint", "host", "webhook", "email", "ssh",
    ]

    static let guardWords = ["dry_run", "dryrun", "confirm", "approval", "approve", "consent"]

    static func assessSchema(_ before: String?, _ after: String?) -> SchemaAssessment {
        guard let before, let after,
              let lhs = parsedSchema(before), let rhs = parsedSchema(after) else {
            return SchemaAssessment(
                compatibility: .unknown,
                compatibilityReason: "Kytto could not read one of the schemas, so it cannot tell whether existing calls still fit.",
                security: .review,
                securityReason: "Compare the full schemas before relying on this tool."
            )
        }
        var walk = SchemaWalk()
        walk.compare(lhs, rhs, path: "")
        return walk.assessment
    }

    private static func parsedSchema(_ text: String) -> Any? {
        guard text.utf8.count <= 1_000_000, !repeatsKeys(text) else { return nil }
        return try? JSONSerialization.jsonObject(with: Data(text.utf8), options: [.fragmentsAllowed])
    }
}

private struct SchemaWalk {
    var breaking: [String] = []
    var unknown: [String] = []
    var addedArguments: [String] = []
    var sensitiveArguments: [String] = []
    var guardChanges: [String] = []
    /// A changed default keeps every call valid and still changes what a
    /// call that leaves the argument out does.
    var defaultChanges: [String] = []
    var textChanged = false
    private var visited = 0

    /// Keywords that only describe; changing them never rejects a call.
    static let textKeys: Set<String> = ["description", "title", "examples", "$comment", "deprecated"]
    /// Keywords handled structurally below.
    static let structuralKeys: Set<String> = ["type", "properties", "required", "enum", "items", "additionalProperties"]

    var assessment: SchemaAssessment {
        let compatibility: ContractCompatibility
        let compatibilityReason: String
        if !breaking.isEmpty {
            compatibility = .breaking
            compatibilityReason = sentence(breaking)
        } else if !unknown.isEmpty || !defaultChanges.isEmpty {
            compatibility = .unknown
            var parts: [String] = []
            if !defaultChanges.isEmpty {
                parts.append("The default of \(list(defaultChanges)) changed: calls that leave it out now behave differently.")
            }
            if !unknown.isEmpty {
                parts.append("Changed in a way Kytto does not compare: \(list(unknown)). Check whether existing calls still fit.")
            }
            compatibilityReason = parts.joined(separator: " ")
        } else {
            compatibility = .compatible
            compatibilityReason = addedArguments.isEmpty
                ? "Only wording changed; the same calls are accepted."
                : "Only optional arguments were added; existing calls still fit."
        }

        var reasons: [String] = []
        if !sensitiveArguments.isEmpty {
            reasons.append("New argument \(list(sensitiveArguments)) may ask the model for files, credentials or a place to send data.")
        } else if !addedArguments.isEmpty {
            reasons.append("New argument \(list(addedArguments)) gives the model something new to fill in.")
        }
        if !guardChanges.isEmpty {
            reasons.append("A confirmation or dry-run field changed (\(list(guardChanges))). That can change how the model asks for a guarded action; Kytto cannot see whether the server still enforces it.")
        }
        if textChanged {
            reasons.append("Schema text the model reads changed.")
        }
        return SchemaAssessment(
            compatibility: compatibility,
            compatibilityReason: compatibilityReason,
            security: reasons.isEmpty ? .none : .review,
            securityReason: reasons.isEmpty ? nil : reasons.joined(separator: " ")
        )
    }

    private static func unique(_ items: [String]) -> [String] {
        var seen = Set<String>()
        return items.filter { seen.insert($0).inserted }
    }

    private func list(_ items: [String]) -> String {
        let all = Self.unique(items)
        let shown = all.prefix(5).map { "“\($0)”" }.joined(separator: ", ")
        return all.count > 5 ? shown + " and \(all.count - 5) more" : shown
    }

    private func sentence(_ reasons: [String]) -> String {
        let all = Self.unique(reasons)
        return all.prefix(3).joined(separator: " ") + (all.count > 3 ? " \(all.count - 3) more changes can also break calls." : "")
    }

    private static func display(_ path: String) -> String { path.isEmpty ? "the arguments" : path }

    private static func types(_ value: Any?) -> Set<String>? {
        if let one = value as? String { return [one] }
        if let many = value as? [Any] { return Set(many.compactMap { $0 as? String }) }
        return nil
    }

    private static func encoded(_ value: Any) -> Data? {
        try? JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .fragmentsAllowed])
    }

    private static func same(_ lhs: Any?, _ rhs: Any?) -> Bool {
        switch (lhs, rhs) {
        case (nil, nil): true
        case (let l?, let r?): encoded(l) == encoded(r)
        default: false
        }
    }

    private func isGuard(_ name: String) -> Bool {
        let lower = name.lowercased()
        return ContractGuard.guardWords.contains { lower.contains($0) }
    }

    private func isSensitive(_ name: String) -> Bool {
        // Split camelCase and snake_case so `filePath` and `api_key` match
        // while `keyboard` and `monkey` do not.
        var words: [String] = []
        var current = ""
        for character in name {
            if character == "_" || character == "-" || character == "." {
                if !current.isEmpty { words.append(current.lowercased()); current = "" }
            } else if character.isUppercase, !current.isEmpty, !(current.last?.isUppercase ?? false) {
                words.append(current.lowercased()); current = String(character)
            } else {
                current.append(character)
            }
        }
        if !current.isEmpty { words.append(current.lowercased()) }
        return words.contains { word in
            ContractGuard.sensitiveArgumentWords.contains { word == $0 || word == $0 + "s" }
        }
    }

    mutating func compare(_ lhs: Any, _ rhs: Any, path: String) {
        visited += 1
        guard visited <= 2_000 else {
            unknown.append("more of the schema")
            return
        }
        guard let left = lhs as? [String: Any], let right = rhs as? [String: Any] else {
            // `true`/`false` schemas, or something that is not a schema.
            if !Self.same(lhs, rhs) { unknown.append(Self.display(path)) }
            return
        }
        let here = Self.display(path)

        // type: a wider set accepts everything the old one did.
        if !Self.same(left["type"], right["type"]) {
            let old = Self.types(left["type"]), new = Self.types(right["type"])
            if right["type"] == nil {
                // No type constraint any more: accepts more than before.
            } else if let new {
                if let old {
                    // Every integer is a number, so integer → number widens.
                    let accepted = new.contains("number") ? new.union(["integer"]) : new
                    if !old.isSubset(of: accepted) { breaking.append("The type of \(here) changed.") }
                } else if left["type"] == nil {
                    breaking.append("\(here) now requires a specific type.")
                } else {
                    unknown.append("the type of \(here)")
                }
            } else {
                unknown.append("the type of \(here)")
            }
        }

        // enum: removing an allowed value rejects calls that used it.
        if !Self.same(left["enum"], right["enum"]) {
            let old = (left["enum"] as? [Any])?.compactMap(Self.encoded) ?? []
            let new = (right["enum"] as? [Any])?.compactMap(Self.encoded)
            if let new {
                if !Set(old).isSubset(of: Set(new)) || left["enum"] == nil {
                    breaking.append("\(here) accepts fewer values than before.")
                }
            }
            // An enum removed altogether accepts more; compatible.
        }

        // required
        let oldRequired = Set((left["required"] as? [Any])?.compactMap { $0 as? String } ?? [])
        let newRequired = Set((right["required"] as? [Any])?.compactMap { $0 as? String } ?? [])
        for name in newRequired.subtracting(oldRequired).sorted() {
            breaking.append("“\(name)” is now required.")
            if isGuard(name) { guardChanges.append(name) }
        }
        for name in oldRequired.subtracting(newRequired).sorted() where isGuard(name) {
            guardChanges.append(name)
        }

        // properties
        let oldProperties = left["properties"] as? [String: Any] ?? [:]
        let newProperties = right["properties"] as? [String: Any] ?? [:]
        for name in Set(oldProperties.keys).subtracting(newProperties.keys).sorted() {
            breaking.append("Argument “\(name)” was removed.")
            if isGuard(name) { guardChanges.append(name) }
        }
        for name in Set(newProperties.keys).subtracting(oldProperties.keys).sorted() {
            let qualified = path.isEmpty ? name : "\(path).\(name)"
            addedArguments.append(qualified)
            if isSensitive(name) { sensitiveArguments.append(qualified) }
            if isGuard(name) { guardChanges.append(qualified) }
        }
        for name in Set(oldProperties.keys).intersection(newProperties.keys).sorted() {
            guard let old = oldProperties[name], let new = newProperties[name], !Self.same(old, new) else { continue }
            if isGuard(name) { guardChanges.append(path.isEmpty ? name : "\(path).\(name)") }
            compare(old, new, path: path.isEmpty ? name : "\(path).\(name)")
        }

        // items
        if !Self.same(left["items"], right["items"]) {
            if let old = left["items"], let new = right["items"] {
                compare(old, new, path: path.isEmpty ? "[]" : "\(path)[]")
            } else if left["items"] == nil {
                breaking.append("The items of \(here) are now constrained.")
            }
        }

        // additionalProperties: closing an open object rejects extra arguments.
        if !Self.same(left["additionalProperties"], right["additionalProperties"]) {
            let wasOpen = left["additionalProperties"] == nil || (left["additionalProperties"] as? Bool) == true
            if wasOpen, (right["additionalProperties"] as? Bool) == false {
                breaking.append("\(here) no longer accepts extra arguments.")
            } else if !(right["additionalProperties"] == nil || (right["additionalProperties"] as? Bool) == true) {
                unknown.append("additionalProperties of \(here)")
            }
        }

        for key in Set(left.keys).union(right.keys).sorted()
        where !Self.structuralKeys.contains(key) && !Self.same(left[key], right[key]) {
            if Self.textKeys.contains(key) {
                textChanged = true
            } else if key == "default" {
                defaultChanges.append(path.isEmpty ? "the arguments" : path)
            } else {
                unknown.append(path.isEmpty ? key : "\(path).\(key)")
            }
        }
    }
}
