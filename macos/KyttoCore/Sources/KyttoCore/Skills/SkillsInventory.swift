import Foundation

public enum SkillScope: String, Codable, Sendable {
    case global
    case workspace
}

/// One bounded, known skills directory. The scanner never receives a home
/// directory and never recursively crawls beyond the direct skill folders.
public struct SkillInventoryRoot: Sendable {
    public let url: URL
    public let agent: String
    public let scope: SkillScope
    public let label: String

    public init(url: URL, agent: String, scope: SkillScope, label: String) {
        self.url = url.standardizedFileURL
        self.agent = agent
        self.scope = scope
        self.label = label
    }
}

public enum SkillMetadataStatus: String, Codable, Sendable {
    case complete
    case missingName
    case missingDescription
    case missingNameAndDescription
    case unreadable
}

public struct SkillInventoryEntry: Identifiable, Sendable {
    public let id: String
    public let name: String
    public let description: String?
    public let agent: String
    public let scope: SkillScope
    public let scopeLabel: String
    public let pathDisplay: String
    public let metadataStatus: SkillMetadataStatus
    public let isDuplicate: Bool
    public let hasConflict: Bool
    public let duplicateGroup: String
}

public struct SkillsInventoryResult: Sendable {
    public let entries: [SkillInventoryEntry]
    public let warnings: [String]
    public let roots: [String]

    public init(entries: [SkillInventoryEntry], warnings: [String], roots: [String]) {
        self.entries = entries
        self.warnings = warnings
        self.roots = roots
    }
}

/// Read-only inventory of explicitly supplied skill roots.
public struct SkillsInventoryScanner: Sendable {
    private struct Draft: Sendable {
        let id: String
        let name: String
        let description: String?
        let agent: String
        let scope: SkillScope
        let scopeLabel: String
        let pathDisplay: String
        let metadataStatus: SkillMetadataStatus
        let contentDigest: String
    }

    private let roots: [SkillInventoryRoot]
    private static let maximumSkillFileSize = 128 * 1024

    public init(roots: [SkillInventoryRoot]) {
        self.roots = roots
    }

    public func scan() -> SkillsInventoryResult {
        var drafts: [Draft] = []
        var warnings: [String] = []

        for root in roots {
            guard isDirectory(root.url) else { continue }
            let children: [URL]
            do {
                children = try FileManager.default.contentsOfDirectory(
                    at: root.url,
                    includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey],
                    options: [.skipsHiddenFiles]
                )
            } catch {
                warnings.append("Could not read the \(root.label) skills directory.")
                continue
            }

            for directory in children.sorted(by: { $0.path < $1.path }) {
                guard isDirectory(directory), !isSymbolicLink(directory) else { continue }
                guard let skillFile = skillFile(in: directory) else { continue }
                drafts.append(readSkill(
                    file: skillFile,
                    directory: directory,
                    root: root,
                    warnings: &warnings
                ))
            }
        }

        let grouped = Dictionary(grouping: drafts, by: { normalize($0.name) })
        let entries = drafts.map { draft in
            let peers = grouped[normalize(draft.name)] ?? []
            let digestSet = Set(peers.map(\.contentDigest))
            return SkillInventoryEntry(
                id: draft.id,
                name: draft.name,
                description: draft.description,
                agent: draft.agent,
                scope: draft.scope,
                scopeLabel: draft.scopeLabel,
                pathDisplay: draft.pathDisplay,
                metadataStatus: draft.metadataStatus,
                isDuplicate: peers.count > 1,
                hasConflict: peers.count > 1 && digestSet.count > 1,
                duplicateGroup: ConfigWriter.digest(of: normalize(draft.name)).prefix(12).description
            )
        }

        return SkillsInventoryResult(
            entries: entries.sorted {
                ($0.name.lowercased(), $0.scope.rawValue, $0.pathDisplay) <
                    ($1.name.lowercased(), $1.scope.rawValue, $1.pathDisplay)
            },
            warnings: warnings,
            roots: roots.map(\.label)
        )
    }

    private func readSkill(
        file: URL,
        directory: URL,
        root: SkillInventoryRoot,
        warnings: inout [String]
    ) -> Draft {
        let path = directory.path
        let id = ConfigWriter.digest(of: path)
        guard let data = try? Data(contentsOf: file),
              data.count <= Self.maximumSkillFileSize,
              let text = String(data: data, encoding: .utf8)
        else {
            warnings.append("Could not read skill metadata at \(path).")
            return Draft(
                id: id,
                name: directory.lastPathComponent,
                description: nil,
                agent: root.agent,
                scope: root.scope,
                scopeLabel: root.label,
                pathDisplay: path,
                metadataStatus: .unreadable,
                contentDigest: "unreadable"
            )
        }

        let frontMatter = parseFrontMatter(text)
        let cleanedName = frontMatter.name?.trimmingCharacters(in: .whitespacesAndNewlines)
        let name = cleanedName?.isEmpty == true ? nil : cleanedName
        let cleanedDescription = frontMatter.description?.trimmingCharacters(in: .whitespacesAndNewlines)
        let description = cleanedDescription?.isEmpty == true ? nil : cleanedDescription
        let status: SkillMetadataStatus = switch (name == nil, description == nil) {
        case (false, false): .complete
        case (true, false): .missingName
        case (false, true): .missingDescription
        case (true, true): .missingNameAndDescription
        }
        return Draft(
            id: id,
            name: name ?? directory.lastPathComponent,
            description: description,
            agent: root.agent,
            scope: root.scope,
            scopeLabel: root.label,
            pathDisplay: path,
            metadataStatus: status,
            contentDigest: ConfigWriter.digest(of: text)
        )
    }

    private func skillFile(in directory: URL) -> URL? {
        guard let children = try? FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: []
        ) else { return nil }
        return children.first {
            $0.lastPathComponent.caseInsensitiveCompare("SKILL.md") == .orderedSame &&
                (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true
        }
    }

    /// Reads `name` and `description` out of YAML front matter.
    ///
    /// Not a YAML parser — it reads the subset skills actually use. What it has
    /// to get right, because real skills do all of these:
    ///
    /// - Block scalars. `description: >-` followed by indented lines is the
    ///   standard way to write a long description, and taking the line's
    ///   remainder verbatim turns every such skill's description into the
    ///   literal string `">-"` — non-empty, so no "missing" badge appears and
    ///   the card shows a stray marker instead of anything useful.
    /// - Continuation lines. Indented lines belong to the value (or a nested
    ///   mapping) above them, never to the two keys read here — splitting each
    ///   one on its first colon invents keys out of prose.
    /// - A BOM or blank line before the opening `---` is still front matter.
    private func parseFrontMatter(_ text: String) -> (name: String?, description: String?) {
        var lines = text.components(separatedBy: .newlines)[...]
        if let first = lines.first, first.hasPrefix("\u{FEFF}") {
            lines = [String(first.dropFirst())] + lines.dropFirst()
        }
        while let first = lines.first, first.trimmingCharacters(in: .whitespaces).isEmpty {
            lines = lines.dropFirst()
        }
        guard lines.first?.trimmingCharacters(in: .whitespaces) == "---" else {
            return (nil, nil)
        }

        var name: String?
        var description: String?
        var index = lines.index(after: lines.startIndex)
        while index < lines.endIndex {
            let line = lines[index]
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed == "---" || trimmed == "..." { break }
            index = lines.index(after: index)

            // Only zero-indent lines can carry the top-level keys. Anything
            // indented is a continuation or a nested mapping and is consumed by
            // the value that opened it, or skipped.
            guard !line.hasPrefix(" "), !line.hasPrefix("\t"),
                  let separator = trimmed.firstIndex(of: ":")
            else { continue }

            let key = trimmed[..<separator].trimmingCharacters(in: .whitespaces).lowercased()
            let remainder = String(trimmed[trimmed.index(after: separator)...])
                .trimmingCharacters(in: .whitespaces)

            let value: String
            if let folds = blockScalarFolds(remainder) {
                (value, index) = consumeIndentedBlock(lines, from: index, foldingLines: folds)
            } else if !remainder.isEmpty {
                // A non-empty plain value owns any indented lines that follow —
                // YAML folds them into the scalar, and so does this.
                let (continuation, next) = consumeIndentedBlock(lines, from: index, foldingLines: true)
                value = continuation.isEmpty ? unquote(remainder) : unquote(remainder) + " " + continuation
                index = next
            } else {
                // An empty value introduces a nested block, which neither of the
                // keys read here is. Its indented lines are skipped wholesale so
                // `metadata:` cannot leak a nested `name:` into the top level.
                (_, index) = consumeIndentedBlock(lines, from: index, foldingLines: true)
                value = ""
            }

            if key == "name" { name = value }
            if key == "description" { description = value }
        }
        return (name, description)
    }

    /// Whether `remainder` is a block scalar header (`|`, `>`, with optional
    /// chomping and indentation indicators), and if so whether its lines fold.
    private func blockScalarFolds(_ remainder: String) -> Bool? {
        guard let first = remainder.first, first == "|" || first == ">" else { return nil }
        let indicators = remainder.dropFirst()
        guard indicators.count <= 2,
              indicators.allSatisfy({ "+-0123456789".contains($0) })
        else { return nil }
        return first == ">"
    }

    /// Collects the indented (or blank) lines following a value, returning them
    /// joined — with spaces when folding, newlines when literal — and the index
    /// of the first line that is not part of the block.
    private func consumeIndentedBlock(
        _ lines: ArraySlice<String>,
        from start: ArraySlice<String>.Index,
        foldingLines folds: Bool
    ) -> (String, ArraySlice<String>.Index) {
        var collected: [String] = []
        var index = start
        while index < lines.endIndex {
            let line = lines[index]
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            let indented = line.hasPrefix(" ") || line.hasPrefix("\t")
            if trimmed == "---" || trimmed == "..." { break }
            guard trimmed.isEmpty || indented else { break }
            collected.append(trimmed)
            index = lines.index(after: index)
        }
        // Trailing blanks are chomping's business, and everything here is
        // trimmed for display anyway.
        while collected.last?.isEmpty == true { collected.removeLast() }
        while collected.first?.isEmpty == true { collected.removeFirst() }

        if folds {
            // A blank line inside a folded scalar is a paragraph break.
            let paragraphs = collected
                .split(separator: "", omittingEmptySubsequences: true)
                .map { $0.joined(separator: " ") }
            return (paragraphs.joined(separator: "\n"), index)
        }
        return (collected.joined(separator: "\n"), index)
    }

    /// Strips one layer of matched quotes; an unpaired quote is content.
    private func unquote(_ value: String) -> String {
        guard value.count >= 2, let first = value.first,
              first == "\"" || first == "'", value.last == first
        else { return value }
        return String(value.dropFirst().dropLast())
    }

    private func normalize(_ name: String) -> String {
        name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    private func isDirectory(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
    }

    private func isSymbolicLink(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true
    }
}
