import Foundation

/// A byte range in the source text, as UTF-8 offsets.
///
/// Offsets rather than `String.Index` because M2 splices edits back into the
/// original bytes and needs ranges that survive being stored and passed around.
public struct JSONSpan: Equatable, Hashable, Sendable {
    public let start: Int
    public let end: Int

    public init(start: Int, end: Int) {
        self.start = start
        self.end = end
    }

    public var range: Range<Int> { start..<end }
    public var isEmpty: Bool { start >= end }
}

/// One `"key": value` pair inside an object.
public struct JSONMember: Equatable, Sendable {
    /// Decoded key, with escapes resolved.
    public let key: String
    /// Range of the quoted key literal, quotes included.
    public let keySpan: JSONSpan
    public let value: JSONNode
    /// Key through value. This is what M2 removes when deleting a member.
    public let span: JSONSpan
}

/// A parsed JSON value that remembers where it came from.
///
/// Every node carries the range it occupies in the source, which is what lets
/// M2 rewrite one server entry and leave every other byte of the file — including
/// comments and whatever formatting the user or the client app chose — untouched.
public struct JSONNode: Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case object([JSONMember])
        case array([JSONNode])
        case string(String)
        case number(Double)
        case bool(Bool)
        case null
    }

    public let kind: Kind
    public let span: JSONSpan

    public init(kind: Kind, span: JSONSpan) {
        self.kind = kind
        self.span = span
    }
}

// MARK: - Reading

public extension JSONNode {
    var members: [JSONMember]? {
        if case .object(let members) = kind { return members }
        return nil
    }

    var elements: [JSONNode]? {
        if case .array(let elements) = kind { return elements }
        return nil
    }

    var stringValue: String? {
        if case .string(let value) = kind { return value }
        return nil
    }

    var numberValue: Double? {
        if case .number(let value) = kind { return value }
        return nil
    }

    var boolValue: Bool? {
        if case .bool(let value) = kind { return value }
        return nil
    }

    var isNull: Bool { kind == .null }

    /// First member with this key. Duplicate keys are legal JSON and clients
    /// resolve them last-wins, so `last` would be more faithful — but a config
    /// with duplicate server names is broken input we want to surface, not
    /// silently resolve. `Normalizer` reports it as a diagnostic.
    func member(_ key: String) -> JSONMember? {
        members?.first { $0.key == key }
    }

    subscript(key: String) -> JSONNode? {
        member(key)?.value
    }

    /// Walks a chain of object keys, e.g. `["mcpServers", "github"]`.
    func value(at path: [String]) -> JSONNode? {
        var node = self
        for key in path {
            guard let next = node[key] else { return nil }
            node = next
        }
        return node
    }

    /// Object keys in source order. Used when a client's config is rendered
    /// back to the user, so the order they see matches the order on disk.
    var keys: [String] {
        members?.map(\.key) ?? []
    }
}
