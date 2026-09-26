import Foundation

/// A TOML document that keeps its original text and knows where every table,
/// key and value lives in it.
///
/// The same contract as `JSONDocument`, for the same reason: Kytto never
/// regenerates a client's config from its own model (§6.3). It parses, finds the
/// byte range of the one thing it needs to change, and splices. A Codex
/// `config.toml` is not a small file — the one this was written against carries
/// forty-odd `[projects."…"]` tables alongside the four servers — and every byte
/// of it that Kytto did not deliberately change has to come out the other side
/// identical.
///
/// This is deliberately not a complete TOML implementation. It reads structure
/// exactly, and values only as far as Kytto needs them; anything it cannot
/// interpret it records as raw text and leaves alone, which is the behaviour
/// §6.3 wants anyway.
public struct TOMLDocument: Sendable {
    /// The source exactly as it was read.
    public let sourceText: String
    /// UTF-8 view of `sourceText`. Spans index into this.
    public let sourceBytes: [UInt8]
    /// Every table in the file, in the order they appear. The first is the
    /// implicit root table holding whatever sits above the first header.
    public let tables: [TOMLTable]

    init(sourceText: String, sourceBytes: [UInt8], tables: [TOMLTable]) {
        self.sourceText = sourceText
        self.sourceBytes = sourceBytes
        self.tables = tables
    }

    public static func parse(_ text: String) throws -> TOMLDocument {
        let bytes = Array(text.utf8)
        var parser = TOMLParser(bytes: bytes)
        let tables = try parser.parseDocument()
        return TOMLDocument(sourceText: text, sourceBytes: bytes, tables: tables)
    }

    public static func parse(contentsOf url: URL) throws -> TOMLDocument {
        let data = try Data(contentsOf: url)
        guard let text = String(data: data, encoding: .utf8) else {
            throw TOMLParseError(kind: .invalidUTF8, offset: 0, line: 1, column: 1)
        }
        return try parse(text)
    }

    // MARK: - Lookup

    /// The raw source text covered by a span.
    public func slice(_ span: TOMLSpan) -> String {
        guard span.start >= 0, span.end <= sourceBytes.count, span.start <= span.end else { return "" }
        return String(decoding: sourceBytes[span.range], as: UTF8.self)
    }

    public func table(at path: [String]) -> TOMLTable? {
        tables.first { $0.path == path }
    }

    /// Direct children of `path` — `[a.b]` is a child of `[a]`, `[a.b.c]` is not.
    ///
    /// Used to list the servers under `mcp_servers` without also picking up each
    /// server's own `[mcp_servers.<name>.env]`.
    public func childTables(of path: [String]) -> [TOMLTable] {
        tables.filter { $0.path.count == path.count + 1 && Array($0.path.prefix(path.count)) == path }
    }

    /// Every table at or below `path`, which is what has to be removed together
    /// when a server goes: its own table and its `env` sub-table are one thing to
    /// the user and two tables to TOML.
    public func tables(under path: [String]) -> [TOMLTable] {
        tables.filter { $0.path.count > path.count && Array($0.path.prefix(path.count)) == path }
    }

    // MARK: - Splicing

    /// Returns the source with `span` replaced by `replacement` and every other
    /// byte left alone.
    ///
    /// As in `JSONDocument`, this is the only way new config text is produced. If
    /// a change cannot be expressed as span replacements, it does not get written.
    public func replacing(_ span: TOMLSpan, with replacement: String) -> String {
        var bytes = sourceBytes
        bytes.replaceSubrange(span.range, with: Array(replacement.utf8))
        return String(decoding: bytes, as: UTF8.self)
    }

    /// Applies several replacements at once. Spans must not overlap; they are
    /// applied back to front so earlier offsets stay valid.
    public func replacing(_ edits: [(span: TOMLSpan, replacement: String)]) -> String {
        var bytes = sourceBytes
        for edit in edits.sorted(by: { $0.span.start > $1.span.start }) {
            bytes.replaceSubrange(edit.span.range, with: Array(edit.replacement.utf8))
        }
        return String(decoding: bytes, as: UTF8.self)
    }
}

// MARK: - Model

public struct TOMLSpan: Equatable, Sendable {
    public let start: Int
    public let end: Int

    public init(start: Int, end: Int) {
        self.start = start
        self.end = end
    }

    public var range: Range<Int> { start..<end }
    public var isEmpty: Bool { start >= end }
}

public struct TOMLValue: Sendable {
    public indirect enum Kind: Sendable {
        case string(String)
        case bool(Bool)
        case integer(Int)
        case array([TOMLValue])
        case inlineTable([TOMLPair])
        /// Floats, dates, times — read as raw text. Kytto has no use for them and
        /// interpreting a value it will never write is a way to get it wrong.
        case other
    }

    public let kind: Kind
    public let span: TOMLSpan

    public var stringValue: String? {
        if case .string(let value) = kind { return value }
        return nil
    }

    public var boolValue: Bool? {
        if case .bool(let value) = kind { return value }
        return nil
    }

    public var elements: [TOMLValue]? {
        if case .array(let values) = kind { return values }
        return nil
    }

    public var inlinePairs: [TOMLPair]? {
        if case .inlineTable(let pairs) = kind { return pairs }
        return nil
    }
}

public struct TOMLPair: Sendable {
    /// Dotted keys arrive split: `a.b = 1` is `["a", "b"]`.
    public let key: [String]
    public let keySpan: TOMLSpan
    public let value: TOMLValue
    /// The whole `key = value`, without its line ending.
    public let span: TOMLSpan

    public var name: String? { key.count == 1 ? key[0] : nil }
}

public struct TOMLTable: Sendable {
    /// `[]` for the implicit root table above the first header.
    public let path: [String]
    /// Nil for the root table, which has no header line.
    public let headerSpan: TOMLSpan?
    public let pairs: [TOMLPair]
    /// Header start through the end of the last line belonging to this table.
    ///
    /// Deliberately stops at the last piece of content rather than running up to
    /// the next header: blank lines and comments sitting before a header describe
    /// the table that follows, and eating them would move somebody's note.
    public let span: TOMLSpan
    /// `[[a.b]]` rather than `[a.b]`. Kytto does not author these, but it has to
    /// recognise one so it never mistakes it for a table it may rewrite.
    public let isArrayElement: Bool

    public func pair(_ name: String) -> TOMLPair? {
        pairs.first { $0.key == [name] }
    }

    public func value(_ name: String) -> TOMLValue? {
        pair(name)?.value
    }
}

// MARK: - Errors

public struct TOMLParseError: Error, Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case invalidUTF8
        case unexpectedEndOfInput
        case unexpectedCharacter(UInt8)
        case expectedEquals
        case expectedKey
        case unterminatedString
        case unterminatedArray
        case unterminatedInlineTable
        case unterminatedTableHeader
    }

    public let kind: Kind
    public let offset: Int
    public let line: Int
    public let column: Int
}

extension TOMLParseError: CustomStringConvertible {
    public var description: String {
        let what: String
        switch kind {
        case .invalidUTF8: what = "file is not valid UTF-8"
        case .unexpectedEndOfInput: what = "unexpected end of input"
        case .unexpectedCharacter(let byte):
            what = "unexpected character '\(Character(Unicode.Scalar(byte)))'"
        case .expectedEquals: what = "expected '='"
        case .expectedKey: what = "expected a key"
        case .unterminatedString: what = "unterminated string"
        case .unterminatedArray: what = "unterminated array"
        case .unterminatedInlineTable: what = "unterminated inline table"
        case .unterminatedTableHeader: what = "unterminated table header"
        }
        return "\(what) at line \(line), column \(column)"
    }
}

// MARK: - Parser

/// Scanner over UTF-8 bytes.
///
/// TOML looks line-oriented and mostly is, but the exceptions are exactly the
/// ones that would corrupt a file if guessed at: a multi-line string can contain
/// a line that starts with `[`, and an array can run over a dozen lines. So this
/// consumes real values rather than splitting on newlines, and a `[` is only a
/// table header when it is found where a header can be.
struct TOMLParser {
    private let bytes: [UInt8]
    private var index: Int = 0

    init(bytes: [UInt8]) {
        self.bytes = bytes
    }

    mutating func parseDocument() throws -> [TOMLTable] {
        skipByteOrderMark()

        var tables: [TOMLTable] = []
        var path: [String] = []
        var headerSpan: TOMLSpan?
        var isArrayElement = false
        var pairs: [TOMLPair] = []
        var contentEnd = 0

        func closeTable() {
            // A root table with nothing above the first header is not a table
            // anybody wrote, and emitting it would give the editor a span to
            // insert into that does not exist.
            if headerSpan == nil, pairs.isEmpty, path.isEmpty, tables.isEmpty, contentEnd == 0 {
                return
            }
            tables.append(
                TOMLTable(
                    path: path,
                    headerSpan: headerSpan,
                    pairs: pairs,
                    span: TOMLSpan(start: headerSpan?.start ?? 0, end: contentEnd),
                    isArrayElement: isArrayElement
                )
            )
        }

        while true {
            skipTrivia()
            guard index < bytes.count else { break }

            if bytes[index] == UInt8(ascii: "[") {
                closeTable()

                let start = index
                index += 1
                let double = index < bytes.count && bytes[index] == UInt8(ascii: "[")
                if double { index += 1 }

                skipInlineSpace()
                let key = try parseDottedKey()
                skipInlineSpace()

                guard index < bytes.count, bytes[index] == UInt8(ascii: "]") else {
                    throw error(.unterminatedTableHeader)
                }
                index += 1
                if double {
                    guard index < bytes.count, bytes[index] == UInt8(ascii: "]") else {
                        throw error(.unterminatedTableHeader)
                    }
                    index += 1
                }

                path = key
                headerSpan = TOMLSpan(start: start, end: index)
                isArrayElement = double
                pairs = []
                contentEnd = index
                continue
            }

            let pair = try parsePair()
            pairs.append(pair)
            contentEnd = pair.span.end
        }

        closeTable()
        return tables
    }

    // MARK: Trivia

    private mutating func skipByteOrderMark() {
        if bytes.count >= 3, bytes[0] == 0xEF, bytes[1] == 0xBB, bytes[2] == 0xBF {
            index = 3
        }
    }

    private func isInlineSpace(_ byte: UInt8) -> Bool {
        byte == 0x20 || byte == 0x09
    }

    private mutating func skipInlineSpace() {
        while index < bytes.count, isInlineSpace(bytes[index]) { index += 1 }
    }

    /// Whitespace, newlines and comments — everything that can sit between two
    /// meaningful things and is never part of either.
    private mutating func skipTrivia() {
        while index < bytes.count {
            let byte = bytes[index]
            if isInlineSpace(byte) || byte == 0x0A || byte == 0x0D {
                index += 1
            } else if byte == UInt8(ascii: "#") {
                while index < bytes.count, bytes[index] != 0x0A { index += 1 }
            } else {
                return
            }
        }
    }

    // MARK: Keys

    private mutating func parseDottedKey() throws -> [String] {
        var parts: [String] = [try parseKeyPart()]
        while true {
            skipInlineSpace()
            guard index < bytes.count, bytes[index] == UInt8(ascii: ".") else { return parts }
            index += 1
            skipInlineSpace()
            parts.append(try parseKeyPart())
        }
    }

    private mutating func parseKeyPart() throws -> String {
        guard index < bytes.count else { throw error(.expectedKey) }
        switch bytes[index] {
        case UInt8(ascii: "\""):
            return try parseBasicString()
        case UInt8(ascii: "'"):
            return try parseLiteralString()
        default:
            let start = index
            while index < bytes.count, isBareKeyByte(bytes[index]) { index += 1 }
            guard index > start else { throw error(.expectedKey) }
            return String(decoding: bytes[start..<index], as: UTF8.self)
        }
    }

    private func isBareKeyByte(_ byte: UInt8) -> Bool {
        (byte >= UInt8(ascii: "A") && byte <= UInt8(ascii: "Z"))
            || (byte >= UInt8(ascii: "a") && byte <= UInt8(ascii: "z"))
            || (byte >= UInt8(ascii: "0") && byte <= UInt8(ascii: "9"))
            || byte == UInt8(ascii: "_")
            || byte == UInt8(ascii: "-")
    }

    // MARK: Pairs

    private mutating func parsePair() throws -> TOMLPair {
        let start = index
        let key = try parseDottedKey()
        let keySpan = TOMLSpan(start: start, end: index)

        skipInlineSpace()
        guard index < bytes.count, bytes[index] == UInt8(ascii: "=") else {
            throw error(.expectedEquals)
        }
        index += 1
        skipInlineSpace()

        let value = try parseValue()
        return TOMLPair(key: key, keySpan: keySpan, value: value, span: TOMLSpan(start: start, end: value.span.end))
    }

    // MARK: Values

    private mutating func parseValue() throws -> TOMLValue {
        guard index < bytes.count else { throw error(.unexpectedEndOfInput) }
        let start = index

        switch bytes[index] {
        case UInt8(ascii: "\""), UInt8(ascii: "'"):
            let text = try parseAnyString()
            return TOMLValue(kind: .string(text), span: TOMLSpan(start: start, end: index))

        case UInt8(ascii: "["):
            let elements = try parseArray()
            return TOMLValue(kind: .array(elements), span: TOMLSpan(start: start, end: index))

        case UInt8(ascii: "{"):
            let pairs = try parseInlineTable()
            return TOMLValue(kind: .inlineTable(pairs), span: TOMLSpan(start: start, end: index))

        default:
            // A bare token: true, false, a number, a date. Runs to the end of the
            // line or to whatever closes the container it sits in.
            while index < bytes.count {
                let byte = bytes[index]
                if byte == 0x0A || byte == 0x0D || byte == UInt8(ascii: ",")
                    || byte == UInt8(ascii: "]") || byte == UInt8(ascii: "}")
                    || byte == UInt8(ascii: "#") {
                    break
                }
                index += 1
            }
            // Trailing spaces before the delimiter are not part of the value.
            var end = index
            while end > start, isInlineSpace(bytes[end - 1]) { end -= 1 }

            let raw = String(decoding: bytes[start..<end], as: UTF8.self)
            let span = TOMLSpan(start: start, end: end)
            index = end
            switch raw {
            case "true": return TOMLValue(kind: .bool(true), span: span)
            case "false": return TOMLValue(kind: .bool(false), span: span)
            default:
                if let integer = Int(raw) {
                    return TOMLValue(kind: .integer(integer), span: span)
                }
                return TOMLValue(kind: .other, span: span)
            }
        }
    }

    private mutating func parseArray() throws -> [TOMLValue] {
        index += 1 // [
        var elements: [TOMLValue] = []

        while true {
            skipTrivia()
            guard index < bytes.count else { throw error(.unterminatedArray) }

            if bytes[index] == UInt8(ascii: "]") {
                index += 1
                return elements
            }

            elements.append(try parseValue())
            skipTrivia()

            guard index < bytes.count else { throw error(.unterminatedArray) }
            if bytes[index] == UInt8(ascii: ",") {
                index += 1
            } else if bytes[index] != UInt8(ascii: "]") {
                throw error(.unexpectedCharacter(bytes[index]))
            }
        }
    }

    private mutating func parseInlineTable() throws -> [TOMLPair] {
        index += 1 // {
        var pairs: [TOMLPair] = []

        while true {
            skipTrivia()
            guard index < bytes.count else { throw error(.unterminatedInlineTable) }

            if bytes[index] == UInt8(ascii: "}") {
                index += 1
                return pairs
            }

            pairs.append(try parsePair())
            skipTrivia()

            guard index < bytes.count else { throw error(.unterminatedInlineTable) }
            if bytes[index] == UInt8(ascii: ",") {
                index += 1
            } else if bytes[index] != UInt8(ascii: "}") {
                throw error(.unexpectedCharacter(bytes[index]))
            }
        }
    }

    // MARK: Strings

    private mutating func parseAnyString() throws -> String {
        if hasPrefix("\"\"\"") { return try parseMultilineString(quote: UInt8(ascii: "\""), escaped: true) }
        if hasPrefix("'''") { return try parseMultilineString(quote: UInt8(ascii: "'"), escaped: false) }
        if bytes[index] == UInt8(ascii: "'") { return try parseLiteralString() }
        return try parseBasicString()
    }

    private func hasPrefix(_ text: String) -> Bool {
        let literal = Array(text.utf8)
        guard index + literal.count <= bytes.count else { return false }
        return Array(bytes[index..<(index + literal.count)]) == literal
    }

    private mutating func parseBasicString() throws -> String {
        index += 1 // opening quote
        var scalars = String.UnicodeScalarView()
        var literalStart = index

        while index < bytes.count {
            let byte = bytes[index]
            if byte == UInt8(ascii: "\"") {
                appendChunk(&scalars, from: literalStart, to: index)
                index += 1
                return String(scalars)
            }
            if byte == UInt8(ascii: "\\") {
                appendChunk(&scalars, from: literalStart, to: index)
                index += 1
                guard index < bytes.count else { throw error(.unterminatedString) }
                try appendEscape(&scalars)
                literalStart = index
                continue
            }
            if byte == 0x0A { throw error(.unterminatedString) }
            index += 1
        }
        throw error(.unterminatedString)
    }

    private mutating func parseLiteralString() throws -> String {
        index += 1 // opening quote
        let start = index
        while index < bytes.count {
            if bytes[index] == UInt8(ascii: "'") {
                let text = String(decoding: bytes[start..<index], as: UTF8.self)
                index += 1
                return text
            }
            if bytes[index] == 0x0A { throw error(.unterminatedString) }
            index += 1
        }
        throw error(.unterminatedString)
    }

    private mutating func parseMultilineString(quote: UInt8, escaped: Bool) throws -> String {
        index += 3
        // A newline immediately after the opening delimiter is not part of the value.
        if index < bytes.count, bytes[index] == 0x0D { index += 1 }
        if index < bytes.count, bytes[index] == 0x0A { index += 1 }

        var scalars = String.UnicodeScalarView()
        var literalStart = index

        while index < bytes.count {
            if bytes[index] == quote, hasClosingDelimiter(quote) {
                appendChunk(&scalars, from: literalStart, to: index)
                index += 3
                // TOML allows more than three closing quotes; the extras belong
                // to the value, but for our purposes stopping at three is right
                // because the span, not the decoded text, is what gets spliced.
                return String(scalars)
            }
            if escaped, bytes[index] == UInt8(ascii: "\\") {
                appendChunk(&scalars, from: literalStart, to: index)
                index += 1
                guard index < bytes.count else { throw error(.unterminatedString) }
                // A backslash before a newline swallows the whitespace that follows.
                if bytes[index] == 0x0A || bytes[index] == 0x0D {
                    while index < bytes.count,
                          isInlineSpace(bytes[index]) || bytes[index] == 0x0A || bytes[index] == 0x0D {
                        index += 1
                    }
                } else {
                    try appendEscape(&scalars)
                }
                literalStart = index
                continue
            }
            index += 1
        }
        throw error(.unterminatedString)
    }

    private func hasClosingDelimiter(_ quote: UInt8) -> Bool {
        guard index + 3 <= bytes.count else { return false }
        return bytes[index] == quote && bytes[index + 1] == quote && bytes[index + 2] == quote
    }

    private func appendChunk(_ scalars: inout String.UnicodeScalarView, from start: Int, to end: Int) {
        guard end > start else { return }
        let chunk = String(decoding: bytes[start..<end], as: UTF8.self)
        scalars.append(contentsOf: chunk.unicodeScalars)
    }

    private mutating func appendEscape(_ scalars: inout String.UnicodeScalarView) throws {
        let escape = bytes[index]
        index += 1
        switch escape {
        case UInt8(ascii: "\""): scalars.append("\"")
        case UInt8(ascii: "\\"): scalars.append("\\")
        case UInt8(ascii: "b"): scalars.append(Unicode.Scalar(8))
        case UInt8(ascii: "f"): scalars.append(Unicode.Scalar(12))
        case UInt8(ascii: "n"): scalars.append("\n")
        case UInt8(ascii: "r"): scalars.append("\r")
        case UInt8(ascii: "t"): scalars.append("\t")
        case UInt8(ascii: "e"): scalars.append(Unicode.Scalar(27))
        case UInt8(ascii: "u"): scalars.append(try parseHex(digits: 4))
        case UInt8(ascii: "U"): scalars.append(try parseHex(digits: 8))
        default:
            // Not a valid escape, but this parser's job is to find spans, not to
            // referee the file. Keep the bytes and move on.
            scalars.append("\\")
            scalars.append(Unicode.Scalar(escape))
        }
    }

    private mutating func parseHex(digits: Int) throws -> Unicode.Scalar {
        guard index + digits <= bytes.count else { throw error(.unterminatedString) }
        var value: UInt32 = 0
        for _ in 0..<digits {
            let byte = bytes[index]
            let digit: UInt32
            switch byte {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): digit = UInt32(byte - UInt8(ascii: "0"))
            case UInt8(ascii: "a")...UInt8(ascii: "f"): digit = UInt32(byte - UInt8(ascii: "a")) + 10
            case UInt8(ascii: "A")...UInt8(ascii: "F"): digit = UInt32(byte - UInt8(ascii: "A")) + 10
            default: return "\u{FFFD}"
            }
            value = value * 16 + digit
            index += 1
        }
        return Unicode.Scalar(value) ?? "\u{FFFD}"
    }

    // MARK: Diagnostics

    private func error(_ kind: TOMLParseError.Kind) -> TOMLParseError {
        var line = 1
        var column = 1
        var position = 0
        while position < index, position < bytes.count {
            if bytes[position] == 0x0A {
                line += 1
                column = 1
            } else {
                column += 1
            }
            position += 1
        }
        return TOMLParseError(kind: kind, offset: index, line: line, column: column)
    }
}
