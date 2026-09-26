import Foundation

public enum JSONEditError: Error, Equatable {
    case pathNotFound([String])
    case notAnObject([String])
    case notAnArray([String])
}

/// Structural edits, expressed as splices.
///
/// Every operation returns the original text with one range replaced. Nothing is
/// regenerated, so keys Kytto has never heard of, comments, blank lines and the
/// user's indentation all survive untouched (§6.3).
///
/// The fiddly part is commas. Removing a member has to leave the file exactly as
/// if that member had never been typed — no stranded comma, no orphaned blank
/// line, and no change to the line that follows.
public extension JSONDocument {

    // MARK: - Objects

    /// Sets `key` in the object at `path` to `rawValue` (already-serialized JSON).
    /// Creates the object, and the key, if either is missing.
    func settingMember(_ key: String, at path: [String], to rawValue: String) throws -> String {
        let prepared = try ensuringObject(at: path)
        let document = try JSONDocument.parse(prepared)

        guard let container = document.root.value(at: path) else {
            throw JSONEditError.pathNotFound(path)
        }
        guard let members = container.members else {
            throw JSONEditError.notAnObject(path)
        }

        // Existing key: replace just the value, leaving the key and its
        // formatting alone.
        if let existing = members.first(where: { $0.key == key }) {
            return document.replacing(existing.value.span, with: rawValue)
        }

        let entry = "\(JSONText.string(key)): \(rawValue)"

        guard let last = members.last else {
            return document.replacingEmptyContainer(container, with: entry)
        }

        // Match the siblings: same line indentation, same one-line-or-not shape.
        let indent = document.lineIndent(at: last.span.start)
        let isMultiline = document.containsNewline(from: container.span.start, to: last.span.start)
        let insertion = isMultiline ? ",\n\(indent)\(entry)" : ", \(entry)"
        return document.replacing(JSONSpan(start: last.span.end, end: last.span.end), with: insertion)
    }

    /// Removes `key` from the object at `path`. A missing key is not an error —
    /// the desired end state is already true.
    func removingMember(_ key: String, at path: [String]) throws -> String {
        guard let container = root.value(at: path) else { throw JSONEditError.pathNotFound(path) }
        guard let members = container.members else { throw JSONEditError.notAnObject(path) }
        guard let index = members.firstIndex(where: { $0.key == key }) else { return sourceText }

        let spans = members.map(\.span)
        return replacing(removalSpan(of: index, in: spans, container: container), with: "")
    }

    // MARK: - Arrays

    /// Appends `rawValue` to the array at `path`, creating the array if missing.
    func appendingElement(_ rawValue: String, toArrayAt path: [String]) throws -> String {
        let prepared = try ensuringContainer(at: path, empty: "[]")
        let document = try JSONDocument.parse(prepared)

        guard let container = document.root.value(at: path) else {
            throw JSONEditError.pathNotFound(path)
        }
        guard let elements = container.elements else {
            throw JSONEditError.notAnArray(path)
        }

        guard let last = elements.last else {
            return document.replacingEmptyContainer(container, with: rawValue)
        }

        let indent = document.lineIndent(at: last.span.start)
        let isMultiline = document.containsNewline(from: container.span.start, to: last.span.start)
        let insertion = isMultiline ? ",\n\(indent)\(rawValue)" : ", \(rawValue)"
        return document.replacing(JSONSpan(start: last.span.end, end: last.span.end), with: insertion)
    }

    /// Builds an object literal shaped like the entries already in the array at
    /// `path`.
    ///
    /// Anything Kytto adds should be indistinguishable from what was there
    /// before. A file whose deny-list entries are spread over three lines each
    /// should not suddenly gain a one-liner because that was easier to generate.
    func objectLiteral(_ pairs: [(String, String)], matchingStyleOfArrayAt path: [String]) -> String {
        guard let first = root.value(at: path)?.elements?.first,
              containsNewline(from: first.span.start, to: first.span.end)
        else {
            return JSONText.inlineObject(pairs)
        }
        return JSONText.blockObject(
            pairs,
            baseIndent: lineIndent(at: first.span.start),
            unit: IndentStyle.detect(in: sourceText).text
        )
    }

    /// Removes every element of the array at `path` matching `predicate`.
    func removingElements(fromArrayAt path: [String], where predicate: (JSONNode) -> Bool) throws -> String {
        guard let container = root.value(at: path) else { return sourceText }
        guard let elements = container.elements else { throw JSONEditError.notAnArray(path) }

        let doomed = elements.indices.filter { predicate(elements[$0]) }
        guard !doomed.isEmpty else { return sourceText }

        // Back to front, so each removal's offsets are still valid.
        var text = sourceText
        for index in doomed.reversed() {
            let document = try JSONDocument.parse(text)
            guard let container = document.root.value(at: path),
                  let current = container.elements, index < current.count else { continue }
            let span = document.removalSpan(of: index, in: current.map(\.span), container: container)
            text = document.replacing(span, with: "")
        }
        return text
    }

    // MARK: - Shared removal geometry

    /// The range to delete so that an item vanishes without a trace.
    ///
    /// Three cases, and they differ in which comma is theirs to remove:
    ///
    /// - Followed by a comma: take the item, that comma, and the rest of the line
    ///   if nothing else is on it.
    /// - Last of several: take the *preceding* comma and the item, but leave the
    ///   line ending alone — it belongs to the closing brace.
    /// - The only item: take the item and its line.
    internal func removalSpan(of index: Int, in spans: [JSONSpan], container: JSONNode) -> JSONSpan {
        let span = spans[index]
        let trailingComma = offsetOfComma(after: span.end)

        if let comma = trailingComma {
            var end = comma + 1
            if let lineEnd = endOfLineIfBlank(from: end) {
                // Multi-line: take the line ending too, leaving the next member
                // to start its own line at its own indentation.
                end = lineEnd
            } else {
                // Same line: take the space the comma was separating with, so
                // the next member closes up rather than sitting on a gap.
                while end < sourceBytes.count, isHorizontalSpace(sourceBytes[end]) { end += 1 }
            }
            let start = blankLinePrefixStart(before: span.start) ?? span.start
            return JSONSpan(start: start, end: end)
        }

        if index > 0, let comma = offsetOfComma(before: span.start) {
            return JSONSpan(start: comma, end: span.end)
        }

        var end = span.end
        if let lineEnd = endOfLineIfBlank(from: end) { end = lineEnd }
        let start = blankLinePrefixStart(before: span.start) ?? span.start
        return JSONSpan(start: start, end: end)
    }

    // MARK: - Container creation

    /// Ensures every step of `path` exists as an object, creating what is missing.
    private func ensuringObject(at path: [String]) throws -> String {
        try ensuringContainer(at: path, empty: "{}")
    }

    private func ensuringContainer(at path: [String], empty: String) throws -> String {
        guard !path.isEmpty else { return sourceText }

        var text = sourceText
        for depth in path.indices {
            let prefix = Array(path[..<depth])
            let key = path[depth]
            let document = try JSONDocument.parse(text)

            guard let parent = document.root.value(at: prefix) else {
                throw JSONEditError.pathNotFound(prefix)
            }
            guard parent.members != nil else {
                throw JSONEditError.notAnObject(prefix)
            }
            if parent[key] != nil { continue }

            // The last step gets the requested container type; anything above it
            // has to be an object to hold the next key.
            let placeholder = depth == path.count - 1 ? empty : "{}"
            text = try document.settingMember(key, at: prefix, to: placeholder)
        }
        return text
    }

    /// Replaces `{}` or `[]` with a populated, indented version of itself.
    private func replacingEmptyContainer(_ container: JSONNode, with entry: String) -> String {
        let open = sourceBytes[container.span.start]
        let close: String = open == UInt8(ascii: "{") ? "}" : "]"
        let base = lineIndent(at: container.span.start)
        let inner = base + IndentStyle.detect(in: sourceText).text
        return replacing(container.span, with: "\(Character(Unicode.Scalar(open)))\n\(inner)\(entry)\n\(base)\(close)")
    }

    // MARK: - Byte scanning

    private func isHorizontalSpace(_ byte: UInt8) -> Bool {
        byte == 0x20 || byte == 0x09
    }

    private func isSpace(_ byte: UInt8) -> Bool {
        isHorizontalSpace(byte) || byte == 0x0A || byte == 0x0D
    }

    /// Offset of the comma directly after `offset`, ignoring spaces.
    private func offsetOfComma(after offset: Int) -> Int? {
        var index = offset
        while index < sourceBytes.count, isSpace(sourceBytes[index]) { index += 1 }
        guard index < sourceBytes.count, sourceBytes[index] == UInt8(ascii: ",") else { return nil }
        return index
    }

    /// Offset of the comma directly before `offset`, ignoring whitespace.
    private func offsetOfComma(before offset: Int) -> Int? {
        var index = offset - 1
        while index >= 0, isSpace(sourceBytes[index]) { index -= 1 }
        guard index >= 0, sourceBytes[index] == UInt8(ascii: ",") else { return nil }
        return index
    }

    /// Offset just past the newline, if everything between `offset` and it is blank.
    private func endOfLineIfBlank(from offset: Int) -> Int? {
        var index = offset
        while index < sourceBytes.count, isHorizontalSpace(sourceBytes[index]) { index += 1 }
        guard index < sourceBytes.count else { return nil }
        if sourceBytes[index] == 0x0D, index + 1 < sourceBytes.count, sourceBytes[index + 1] == 0x0A {
            return index + 2
        }
        guard sourceBytes[index] == 0x0A else { return nil }
        return index + 1
    }

    /// Start of the line containing `offset`, if only whitespace precedes it there.
    private func blankLinePrefixStart(before offset: Int) -> Int? {
        var index = offset - 1
        while index >= 0, isHorizontalSpace(sourceBytes[index]) { index -= 1 }
        guard index < 0 || sourceBytes[index] == 0x0A else { return nil }
        return index + 1
    }

    /// The whitespace that starts the line containing `offset`.
    internal func lineIndent(at offset: Int) -> String {
        var start = offset
        while start > 0, sourceBytes[start - 1] != 0x0A { start -= 1 }
        var end = start
        while end < sourceBytes.count, isHorizontalSpace(sourceBytes[end]) { end += 1 }
        return String(decoding: sourceBytes[start..<min(end, offset)], as: UTF8.self)
    }

    internal func containsNewline(from start: Int, to end: Int) -> Bool {
        guard start < end, end <= sourceBytes.count else { return false }
        return sourceBytes[start..<end].contains(0x0A)
    }
}
