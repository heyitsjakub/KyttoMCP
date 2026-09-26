import Foundation

/// Enough of a JSON writer to produce the small fragments M2 splices in.
///
/// Deliberately not a general serializer. Whole values are never regenerated
/// from the model — when a parked server comes back, its original source text is
/// spliced in verbatim. This only builds the few literals Kytto itself authors.
public enum JSONText {

    public static func string(_ value: String) -> String {
        var out = "\""
        for scalar in value.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case let s where s.value < 0x20:
                out += String(format: "\\u%04x", s.value)
            default:
                out.unicodeScalars.append(scalar)
            }
        }
        return out + "\""
    }

    public static func bool(_ value: Bool) -> String {
        value ? "true" : "false"
    }

    /// A flat object on one line, e.g. `{ "serverName": "github" }`.
    public static func inlineObject(_ pairs: [(String, String)]) -> String {
        guard !pairs.isEmpty else { return "{}" }
        let body = pairs.map { "\(string($0.0)): \($0.1)" }.joined(separator: ", ")
        return "{ \(body) }"
    }

    /// An object spread over several lines, closing at `baseIndent`.
    public static func blockObject(_ pairs: [(String, String)], baseIndent: String, unit: String) -> String {
        guard !pairs.isEmpty else { return "{}" }
        let inner = baseIndent + unit
        let body = pairs.map { "\(inner)\(string($0.0)): \($0.1)" }.joined(separator: ",\n")
        return "{\n\(body)\n\(baseIndent)}"
    }
}

/// The indentation a file already uses, so anything Kytto inserts looks like the
/// user (or the client app) wrote it.
public struct IndentStyle: Equatable, Sendable {
    public enum Unit: Equatable, Sendable {
        case spaces(Int)
        case tab
    }

    public let unit: Unit

    public static let twoSpaces = IndentStyle(unit: .spaces(2))

    public init(unit: Unit) {
        self.unit = unit
    }

    public var text: String {
        switch unit {
        case .spaces(let count): String(repeating: " ", count: count)
        case .tab: "\t"
        }
    }

    /// Infers the unit from the first indented line in the source.
    ///
    /// Claude Code writes two spaces, Claude Desktop has been seen with tabs, and
    /// a user who reformatted by hand could have anything. Guessing wrong is not
    /// dangerous, only ugly, so one sample is enough.
    public static func detect(in source: String) -> IndentStyle {
        for line in source.split(separator: "\n", omittingEmptySubsequences: false) {
            guard let first = line.first else { continue }
            if first == "\t" { return IndentStyle(unit: .tab) }
            if first == " " {
                let count = line.prefix { $0 == " " }.count
                // A continuation line inside a wrapped value could be indented
                // oddly; anything past 8 is not an indent unit.
                if (1...8).contains(count) { return IndentStyle(unit: .spaces(count)) }
            }
        }
        return .twoSpaces
    }
}
