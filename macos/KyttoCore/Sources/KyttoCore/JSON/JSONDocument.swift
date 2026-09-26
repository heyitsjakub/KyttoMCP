import Foundation

/// A JSON(C) document that keeps its original text and knows where every value lives in it.
///
/// Kytto never regenerates a client's config from its own model — §6.3. It parses,
/// finds the byte range of the one thing it needs to change, and splices. Everything
/// else in the file survives byte for byte: key order, indentation, blank lines,
/// comments, and keys Kytto has never heard of.
public struct JSONDocument: Sendable {
    /// The source exactly as it was read.
    public let sourceText: String
    /// UTF-8 view of `sourceText`. Spans index into this.
    public let sourceBytes: [UInt8]
    public let root: JSONNode

    init(sourceText: String, sourceBytes: [UInt8], root: JSONNode) {
        self.sourceText = sourceText
        self.sourceBytes = sourceBytes
        self.root = root
    }

    public static func parse(_ text: String) throws -> JSONDocument {
        let bytes = Array(text.utf8)
        var parser = JSONParser(bytes: bytes)
        let root = try parser.parseDocument()
        return JSONDocument(sourceText: text, sourceBytes: bytes, root: root)
    }

    public static func parse(contentsOf url: URL) throws -> JSONDocument {
        let data = try Data(contentsOf: url)
        guard let text = String(data: data, encoding: .utf8) else {
            throw JSONParseError(kind: .invalidUTF8, offset: 0, line: 1, column: 1)
        }
        return try parse(text)
    }

    // MARK: - Slicing

    /// The raw source text covered by a span.
    public func slice(_ span: JSONSpan) -> String {
        guard span.start >= 0, span.end <= sourceBytes.count, span.start <= span.end else { return "" }
        return String(decoding: sourceBytes[span.range], as: UTF8.self)
    }

    public func value(at path: [String]) -> JSONNode? {
        root.value(at: path)
    }

    // MARK: - Splicing (the M2 primitive)

    /// Returns the source with `span` replaced by `replacement` and every other
    /// byte left alone.
    ///
    /// This is deliberately the *only* way M2 will produce new config text. If a
    /// change cannot be expressed as one or more span replacements, it does not
    /// get written.
    public func replacing(_ span: JSONSpan, with replacement: String) -> String {
        var bytes = sourceBytes
        bytes.replaceSubrange(span.range, with: Array(replacement.utf8))
        return String(decoding: bytes, as: UTF8.self)
    }

    /// Applies several replacements at once. Spans must not overlap; they are
    /// applied back to front so earlier offsets stay valid.
    public func replacing(_ edits: [(span: JSONSpan, replacement: String)]) -> String {
        var bytes = sourceBytes
        for edit in edits.sorted(by: { $0.span.start > $1.span.start }) {
            bytes.replaceSubrange(edit.span.range, with: Array(edit.replacement.utf8))
        }
        return String(decoding: bytes, as: UTF8.self)
    }
}

// MARK: - Errors

public struct JSONParseError: Error, Equatable, Sendable {
    public enum Kind: Equatable, Sendable {
        case invalidUTF8
        case unexpectedEndOfInput
        case unexpectedCharacter(UInt8)
        case trailingContent
        case invalidNumber
        case invalidEscape
        case unterminatedString
        case unterminatedComment
        case expectedColon
        case expectedKey
    }

    public let kind: Kind
    public let offset: Int
    public let line: Int
    public let column: Int
}

extension JSONParseError: CustomStringConvertible {
    public var description: String {
        let what: String
        switch kind {
        case .invalidUTF8: what = "file is not valid UTF-8"
        case .unexpectedEndOfInput: what = "unexpected end of input"
        case .unexpectedCharacter(let byte):
            let scalar = Unicode.Scalar(byte)
            what = "unexpected character '\(Character(scalar))'"
        case .trailingContent: what = "unexpected content after the top-level value"
        case .invalidNumber: what = "invalid number"
        case .invalidEscape: what = "invalid escape sequence"
        case .unterminatedString: what = "unterminated string"
        case .unterminatedComment: what = "unterminated block comment"
        case .expectedColon: what = "expected ':'"
        case .expectedKey: what = "expected a quoted key"
        }
        return "\(what) at line \(line), column \(column)"
    }
}

// MARK: - Parser

/// Recursive-descent parser over UTF-8 bytes.
///
/// Accepts JSONC, because we have to: VS Code's `mcp.json` is JSONC and users
/// put comments in it. Line and block comments and trailing commas are skipped
/// as trivia, which means they are never part of a value's span and therefore
/// survive any splice.
struct JSONParser {
    private let bytes: [UInt8]
    private var index: Int = 0

    init(bytes: [UInt8]) {
        self.bytes = bytes
    }

    mutating func parseDocument() throws -> JSONNode {
        skipByteOrderMark()
        try skipTrivia()
        let node = try parseValue()
        try skipTrivia()
        guard index == bytes.count else {
            throw error(.trailingContent)
        }
        return node
    }

    // MARK: Trivia

    private mutating func skipByteOrderMark() {
        if bytes.count >= 3, bytes[0] == 0xEF, bytes[1] == 0xBB, bytes[2] == 0xBF {
            index = 3
        }
    }

    private mutating func skipTrivia() throws {
        while index < bytes.count {
            switch bytes[index] {
            case 0x20, 0x09, 0x0A, 0x0D:
                index += 1
            case UInt8(ascii: "/"):
                guard index + 1 < bytes.count else { return }
                switch bytes[index + 1] {
                case UInt8(ascii: "/"):
                    index += 2
                    while index < bytes.count, bytes[index] != 0x0A { index += 1 }
                case UInt8(ascii: "*"):
                    let start = index
                    index += 2
                    var closed = false
                    while index + 1 < bytes.count {
                        if bytes[index] == UInt8(ascii: "*"), bytes[index + 1] == UInt8(ascii: "/") {
                            index += 2
                            closed = true
                            break
                        }
                        index += 1
                    }
                    if !closed {
                        index = start
                        throw error(.unterminatedComment)
                    }
                default:
                    return
                }
            default:
                return
            }
        }
    }

    // MARK: Values

    private mutating func parseValue() throws -> JSONNode {
        guard index < bytes.count else { throw error(.unexpectedEndOfInput) }
        switch bytes[index] {
        case UInt8(ascii: "{"): return try parseObject()
        case UInt8(ascii: "["): return try parseArray()
        case UInt8(ascii: "\""):
            let start = index
            let value = try parseStringLiteral()
            return JSONNode(kind: .string(value), span: JSONSpan(start: start, end: index))
        case UInt8(ascii: "t"): return try parseKeyword("true", kind: .bool(true))
        case UInt8(ascii: "f"): return try parseKeyword("false", kind: .bool(false))
        case UInt8(ascii: "n"): return try parseKeyword("null", kind: .null)
        default: return try parseNumber()
        }
    }

    private mutating func parseKeyword(_ word: String, kind: JSONNode.Kind) throws -> JSONNode {
        let literal = Array(word.utf8)
        let start = index
        guard index + literal.count <= bytes.count,
              Array(bytes[index..<(index + literal.count)]) == literal
        else {
            throw error(.unexpectedCharacter(bytes[index]))
        }
        index += literal.count
        return JSONNode(kind: kind, span: JSONSpan(start: start, end: index))
    }

    private mutating func parseObject() throws -> JSONNode {
        let start = index
        index += 1 // {
        var members: [JSONMember] = []

        try skipTrivia()
        if index < bytes.count, bytes[index] == UInt8(ascii: "}") {
            index += 1
            return JSONNode(kind: .object(members), span: JSONSpan(start: start, end: index))
        }

        while true {
            try skipTrivia()

            // Trailing comma before the closing brace, e.g. `{"a": 1,}`.
            if index < bytes.count, bytes[index] == UInt8(ascii: "}") {
                index += 1
                break
            }

            guard index < bytes.count, bytes[index] == UInt8(ascii: "\"") else {
                throw error(.expectedKey)
            }
            let memberStart = index
            let keyStart = index
            let key = try parseStringLiteral()
            let keySpan = JSONSpan(start: keyStart, end: index)

            try skipTrivia()
            guard index < bytes.count, bytes[index] == UInt8(ascii: ":") else {
                throw error(.expectedColon)
            }
            index += 1

            try skipTrivia()
            let value = try parseValue()
            members.append(
                JSONMember(
                    key: key,
                    keySpan: keySpan,
                    value: value,
                    span: JSONSpan(start: memberStart, end: value.span.end)
                )
            )

            try skipTrivia()
            guard index < bytes.count else { throw error(.unexpectedEndOfInput) }
            switch bytes[index] {
            case UInt8(ascii: ","):
                index += 1
            case UInt8(ascii: "}"):
                index += 1
                return JSONNode(kind: .object(members), span: JSONSpan(start: start, end: index))
            default:
                throw error(.unexpectedCharacter(bytes[index]))
            }
        }

        return JSONNode(kind: .object(members), span: JSONSpan(start: start, end: index))
    }

    private mutating func parseArray() throws -> JSONNode {
        let start = index
        index += 1 // [
        var elements: [JSONNode] = []

        try skipTrivia()
        if index < bytes.count, bytes[index] == UInt8(ascii: "]") {
            index += 1
            return JSONNode(kind: .array(elements), span: JSONSpan(start: start, end: index))
        }

        while true {
            try skipTrivia()

            if index < bytes.count, bytes[index] == UInt8(ascii: "]") { // trailing comma
                index += 1
                break
            }

            elements.append(try parseValue())

            try skipTrivia()
            guard index < bytes.count else { throw error(.unexpectedEndOfInput) }
            switch bytes[index] {
            case UInt8(ascii: ","):
                index += 1
            case UInt8(ascii: "]"):
                index += 1
                return JSONNode(kind: .array(elements), span: JSONSpan(start: start, end: index))
            default:
                throw error(.unexpectedCharacter(bytes[index]))
            }
        }

        return JSONNode(kind: .array(elements), span: JSONSpan(start: start, end: index))
    }

    private mutating func parseNumber() throws -> JSONNode {
        let start = index
        if index < bytes.count, bytes[index] == UInt8(ascii: "-") { index += 1 }

        func consumeDigits() -> Bool {
            let from = index
            while index < bytes.count, bytes[index] >= UInt8(ascii: "0"), bytes[index] <= UInt8(ascii: "9") {
                index += 1
            }
            return index > from
        }

        guard index < bytes.count else { throw error(.unexpectedEndOfInput) }
        if bytes[index] == UInt8(ascii: "0") {
            index += 1
            if index < bytes.count,
               bytes[index] >= UInt8(ascii: "0"), bytes[index] <= UInt8(ascii: "9") {
                throw error(.invalidNumber)
            }
        } else if bytes[index] >= UInt8(ascii: "1"), bytes[index] <= UInt8(ascii: "9") {
            guard consumeDigits() else { throw error(.invalidNumber) }
        } else {
            index = start
            throw error(.unexpectedCharacter(bytes[index]))
        }

        if index < bytes.count, bytes[index] == UInt8(ascii: ".") {
            index += 1
            guard consumeDigits() else { throw error(.invalidNumber) }
        }

        if index < bytes.count, bytes[index] == UInt8(ascii: "e") || bytes[index] == UInt8(ascii: "E") {
            index += 1
            if index < bytes.count, bytes[index] == UInt8(ascii: "+") || bytes[index] == UInt8(ascii: "-") {
                index += 1
            }
            guard consumeDigits() else { throw error(.invalidNumber) }
        }

        let text = String(decoding: bytes[start..<index], as: UTF8.self)
        guard let value = Double(text) else { throw error(.invalidNumber) }
        return JSONNode(kind: .number(value), span: JSONSpan(start: start, end: index))
    }

    /// Consumes a quoted string and returns it with escapes resolved.
    /// On return, `index` sits just past the closing quote.
    private mutating func parseStringLiteral() throws -> String {
        index += 1 // opening quote
        var scalars = String.UnicodeScalarView()
        var literalStart = index

        func flushLiteral(upTo end: Int) {
            guard end > literalStart else { return }
            let chunk = String(decoding: bytes[literalStart..<end], as: UTF8.self)
            scalars.append(contentsOf: chunk.unicodeScalars)
        }

        while index < bytes.count {
            let byte = bytes[index]
            if byte == UInt8(ascii: "\"") {
                flushLiteral(upTo: index)
                index += 1
                return String(scalars)
            }
            if byte == UInt8(ascii: "\\") {
                flushLiteral(upTo: index)
                index += 1
                guard index < bytes.count else { throw error(.unterminatedString) }
                let escape = bytes[index]
                index += 1
                switch escape {
                case UInt8(ascii: "\""): scalars.append("\"")
                case UInt8(ascii: "\\"): scalars.append("\\")
                case UInt8(ascii: "/"): scalars.append("/")
                case UInt8(ascii: "b"): scalars.append(Unicode.Scalar(8))
                case UInt8(ascii: "f"): scalars.append(Unicode.Scalar(12))
                case UInt8(ascii: "n"): scalars.append("\n")
                case UInt8(ascii: "r"): scalars.append("\r")
                case UInt8(ascii: "t"): scalars.append("\t")
                case UInt8(ascii: "u"):
                    let value = try parseHexQuad()
                    if value >= 0xD800, value <= 0xDBFF,
                       index + 1 < bytes.count,
                       bytes[index] == UInt8(ascii: "\\"),
                       bytes[index + 1] == UInt8(ascii: "u") {
                        let save = index
                        index += 2
                        let low = try parseHexQuad()
                        if low >= 0xDC00, low <= 0xDFFF {
                            let combined = 0x10000 + (value - 0xD800) * 0x400 + (low - 0xDC00)
                            scalars.append(Unicode.Scalar(combined) ?? "\u{FFFD}")
                        } else {
                            index = save
                            scalars.append("\u{FFFD}")
                        }
                    } else if let scalar = Unicode.Scalar(value), !(0xD800...0xDFFF).contains(value) {
                        scalars.append(scalar)
                    } else {
                        scalars.append("\u{FFFD}")
                    }
                default:
                    throw error(.invalidEscape)
                }
                literalStart = index
                continue
            }
            // JSON strings may contain escaped control characters, never raw
            // U+0000...U+001F bytes.
            if byte < 0x20 { throw error(.unexpectedCharacter(byte)) }
            index += 1
        }
        throw error(.unterminatedString)
    }

    private mutating func parseHexQuad() throws -> UInt32 {
        guard index + 4 <= bytes.count else { throw error(.invalidEscape) }
        var value: UInt32 = 0
        for _ in 0..<4 {
            let byte = bytes[index]
            let digit: UInt32
            switch byte {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): digit = UInt32(byte - UInt8(ascii: "0"))
            case UInt8(ascii: "a")...UInt8(ascii: "f"): digit = UInt32(byte - UInt8(ascii: "a")) + 10
            case UInt8(ascii: "A")...UInt8(ascii: "F"): digit = UInt32(byte - UInt8(ascii: "A")) + 10
            default: throw error(.invalidEscape)
            }
            value = value * 16 + digit
            index += 1
        }
        return value
    }

    // MARK: Diagnostics

    private func error(_ kind: JSONParseError.Kind) -> JSONParseError {
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
        return JSONParseError(kind: kind, offset: index, line: line, column: column)
    }
}
