import Foundation

/// A JSON value Kytto is about to write.
///
/// This is the serializer M2 deliberately did without: toggling only ever moved
/// bytes that were already in the file. M3 authors definitions that never
/// existed, so they have to be rendered — and rendered to look like the rest of
/// the file they land in.
public indirect enum JSONBuildValue: Sendable {
    case object([Field])
    case array([JSONBuildValue])
    case string(String)
    case bool(Bool)
    case number(Double)

    public struct Field: Sendable {
        public let key: String
        public let value: JSONBuildValue

        public init(_ key: String, _ value: JSONBuildValue) {
            self.key = key
            self.value = value
        }
    }
}

public enum JSONBuilder {
    /// Width past which a scalar array is broken onto separate lines.
    ///
    /// Real configs write short argument lists inline — `["-y", "some-package"]`
    /// — and break long ones up. Matching that is the difference between a
    /// definition that looks typed and one that looks generated.
    private static let inlineArrayBudget = 60

    public static func render(
        _ value: JSONBuildValue,
        baseIndent: String = "",
        unit: String = "  "
    ) -> String {
        switch value {
        case .string(let text):
            return JSONText.string(text)

        case .bool(let flag):
            return JSONText.bool(flag)

        case .number(let number):
            // Whole numbers should not come out as "3.0".
            return number == number.rounded() && abs(number) < 1e15
                ? String(Int64(number))
                : String(number)

        case .array(let elements):
            return renderArray(elements, baseIndent: baseIndent, unit: unit)

        case .object(let fields):
            return renderObject(fields, baseIndent: baseIndent, unit: unit)
        }
    }

    private static func renderArray(
        _ elements: [JSONBuildValue],
        baseIndent: String,
        unit: String
    ) -> String {
        guard !elements.isEmpty else { return "[]" }

        if elements.allSatisfy(isScalar) {
            let inline = "[" + elements.map { render($0) }.joined(separator: ", ") + "]"
            if baseIndent.count + inline.count <= inlineArrayBudget { return inline }
        }

        let inner = baseIndent + unit
        let body = elements
            .map { inner + render($0, baseIndent: inner, unit: unit) }
            .joined(separator: ",\n")
        return "[\n\(body)\n\(baseIndent)]"
    }

    private static func renderObject(
        _ fields: [JSONBuildValue.Field],
        baseIndent: String,
        unit: String
    ) -> String {
        guard !fields.isEmpty else { return "{}" }
        let inner = baseIndent + unit
        let body = fields
            .map { "\(inner)\(JSONText.string($0.key)): \(render($0.value, baseIndent: inner, unit: unit))" }
            .joined(separator: ",\n")
        return "{\n\(body)\n\(baseIndent)}"
    }

    private static func isScalar(_ value: JSONBuildValue) -> Bool {
        switch value {
        case .string, .bool, .number: true
        case .object, .array: false
        }
    }
}
