import Foundation
import Testing
@testable import KyttoCore

enum Fixture: String, CaseIterable {
    case claudeDesktopNoServers = "claude_desktop_no_servers.json"
    case claudeDesktopWithServers = "claude_desktop_with_servers.json"
    case claudeCode = "claude_code.json"
    case claudeCodeSettings = "claude_code_settings.json"
    case cursorMCP = "cursor_mcp.json"
    case vscodeMCP = "vscode_mcp.jsonc"
    case extensionManifest = "extension_manifest.json"
    case extensionSettingsDisabled = "extension_settings_disabled.json"
    case codexConfig = "codex_config.toml"

    /// What this fixture is spelled in. The span tests are per-format, and a
    /// fixture added to the wrong list should fail to parse rather than quietly
    /// skip the checks that matter.
    var format: ConfigFormat {
        rawValue.hasSuffix(".toml") ? .toml : .json
    }

    static var json: [Fixture] { allCases.filter { $0.format == .json } }
    static var toml: [Fixture] { allCases.filter { $0.format == .toml } }

    var url: URL {
        guard let base = Bundle.module.resourceURL else {
            fatalError("test bundle has no resource URL")
        }
        return base.appending(path: "Fixtures").appending(path: rawValue)
    }

    var text: String {
        // Read as bytes and decode, so the exact source (line endings, trailing
        // newline, BOM) reaches the parser untouched.
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8) else {
            fatalError("missing or non-UTF8 fixture: \(rawValue)")
        }
        return text
    }

    func document() throws -> JSONDocument {
        try JSONDocument.parse(text)
    }
}

/// A view of a parsed tree with all spans removed, so two trees can be compared
/// on content alone. Used to check that the text a span points at re-parses to
/// the same value the span was taken from.
indirect enum Shape: Equatable {
    case object([Entry])
    case array([Shape])
    case string(String)
    case number(Double)
    case bool(Bool)
    case null

    struct Entry: Equatable {
        let key: String
        let value: Shape
    }

    init(_ node: JSONNode) {
        switch node.kind {
        case .object(let members):
            self = .object(members.map { Entry(key: $0.key, value: Shape($0.value)) })
        case .array(let elements):
            self = .array(elements.map(Shape.init))
        case .string(let value):
            self = .string(value)
        case .number(let value):
            self = .number(value)
        case .bool(let value):
            self = .bool(value)
        case .null:
            self = .null
        }
    }
}

/// Every node in the tree, parents before children.
func allNodes(_ node: JSONNode) -> [JSONNode] {
    var result = [node]
    switch node.kind {
    case .object(let members):
        for member in members { result.append(contentsOf: allNodes(member.value)) }
    case .array(let elements):
        for element in elements { result.append(contentsOf: allNodes(element)) }
    default:
        break
    }
    return result
}
