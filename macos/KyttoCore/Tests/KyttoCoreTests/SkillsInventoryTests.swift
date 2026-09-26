import Foundation
import Testing
@testable import KyttoCore

@Suite("Skills inventory")
struct SkillsInventoryTests {
    @Test("inventory reports scope, duplicate names and conflicting content")
    func overlappingScopes() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "KyttoSkills-\(UUID().uuidString)", directoryHint: .isDirectory)
        let global = root.appending(path: "global")
        let workspace = root.appending(path: "workspace")
        try FileManager.default.createDirectory(at: global.appending(path: "review"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: workspace.appending(path: "review"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: workspace.appending(path: "missing"), withIntermediateDirectories: true)
        try Data("---\nname: Review\ndescription: Global review\n---\n".utf8)
            .write(to: global.appending(path: "review/SKILL.md"))
        try Data("---\nname: review\ndescription: Workspace review\n---\n".utf8)
            .write(to: workspace.appending(path: "review/SKILL.md"))
        try Data("# Missing metadata\n".utf8)
            .write(to: workspace.appending(path: "missing/SKILL.md"))
        defer { try? FileManager.default.removeItem(at: root) }

        let result = SkillsInventoryScanner(roots: [
            SkillInventoryRoot(url: global, agent: "Codex", scope: .global, label: "Codex global"),
            SkillInventoryRoot(url: workspace, agent: "Claude", scope: .workspace, label: "Claude workspace"),
        ]).scan()

        let reviews = result.entries.filter { $0.name.lowercased() == "review" }
        #expect(reviews.count == 2)
        #expect(reviews.allSatisfy { $0.isDuplicate })
        #expect(reviews.allSatisfy { $0.hasConflict })
        #expect(Set(reviews.map(\.duplicateGroup)).count == 1)
        #expect(reviews.map(\.scope).contains(.global))
        #expect(reviews.map(\.scope).contains(.workspace))

        let missing = try #require(result.entries.first { $0.name == "missing" })
        #expect(missing.metadataStatus == .missingNameAndDescription)
        #expect(result.warnings.isEmpty)
        let unchanged = try String(contentsOf: workspace.appending(path: "review/SKILL.md"), encoding: .utf8)
        #expect(unchanged == "---\nname: review\ndescription: Workspace review\n---\n")
    }

    /// The front matter shapes that real-world skills actually use.
    /// Many published skills write `description: >-`, which the
    /// old line-splitter turned into the literal string ">-" — non-empty, so no
    /// "description missing" badge appeared either.
    @Test("block scalars, continuations and nested mappings parse as YAML means them")
    func blockScalarFrontMatter() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "KyttoSkillsYAML-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: root) }

        func write(_ text: String, to name: String) throws {
            let directory = root.appending(path: name)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try Data(text.utf8).write(to: directory.appending(path: "SKILL.md"))
        }

        try write("""
        ---
        name: folded-cli
        description: >-
          Long description that continues
          onto the following lines.
        ---
        Body.
        """, to: "folded")

        try write("""
        ---
        name: literal
        description: |
          First line.
          Second line.
        ---
        """, to: "literal")

        // A continuation line that happens to contain a colon must stay in the
        // description instead of becoming a key named "usage".
        try write("""
        ---
        name: colonized
        description: >-
          Runs checks.
          Usage: run it with --all.
        ---
        """, to: "colonized")

        // A BOM and a blank line before the fence still open front matter.
        try write("\u{FEFF}\n---\nname: bommed\ndescription: Survives a BOM.\n---\n", to: "bommed")

        // A nested mapping must not leak `name`/`description` into the top level.
        try write("""
        ---
        description: Top level.
        metadata:
          name: nested-name
          description: nested-description
        ---
        """, to: "nested")

        let result = SkillsInventoryScanner(roots: [
            SkillInventoryRoot(url: root, agent: "Codex", scope: .global, label: "Codex global"),
        ]).scan()

        func entry(_ name: String) throws -> SkillInventoryEntry {
            try #require(result.entries.first { $0.pathDisplay.hasSuffix("/\(name)") })
        }

        let folded = try entry("folded")
        #expect(folded.name == "folded-cli")
        #expect(folded.description == "Long description that continues onto the following lines.")
        #expect(folded.metadataStatus == .complete)

        let literal = try entry("literal")
        #expect(literal.description == "First line.\nSecond line.")

        let colonized = try entry("colonized")
        #expect(colonized.description == "Runs checks. Usage: run it with --all.")

        let bommed = try entry("bommed")
        #expect(bommed.name == "bommed")
        #expect(bommed.description == "Survives a BOM.")

        let nested = try entry("nested")
        #expect(nested.description == "Top level.")
        #expect(nested.name == "nested", "the directory name, not the nested mapping's")
        #expect(nested.metadataStatus == .missingName)
    }

    @Test("oversized skill metadata is reported without exposing its contents")
    func unreadableMetadata() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "KyttoSkillsUnreadable-\(UUID().uuidString)", directoryHint: .isDirectory)
        let skill = root.appending(path: "large")
        try FileManager.default.createDirectory(at: skill, withIntermediateDirectories: true)
        try Data(repeating: 65, count: 128 * 1024 + 1).write(to: skill.appending(path: "SKILL.md"))
        defer { try? FileManager.default.removeItem(at: root) }

        let result = SkillsInventoryScanner(roots: [
            SkillInventoryRoot(url: root, agent: "Codex", scope: .global, label: "Codex global"),
        ]).scan()
        let entry = try #require(result.entries.first)
        #expect(entry.metadataStatus == .unreadable)
        #expect(result.warnings.count == 1)
    }
}
