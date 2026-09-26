import Foundation

public enum ToolRiskKind: String, Codable, Sendable {
    /// Characters that are in the text a model reads and not in the text a human
    /// reads. There is no benign version of this.
    case hiddenCharacters
    /// Wording that addresses the model rather than describing the tool.
    case instructionOverride
    /// Markup that imitates a system or developer message.
    case impersonatedAuthority
    /// A read-only tool asking for credentials or key material by name.
    case credentialInterest
    /// Two enabled servers claiming the same tool name in one client.
    case shadowedName
}

public struct ToolRisk: Codable, Equatable, Sendable {
    public let kind: ToolRiskKind
    public let severity: DoctorSeverity
    public let toolName: String
    /// What was found, quoted narrowly enough to be checked by hand.
    public let evidence: String
    public let explanation: String

    public init(
        kind: ToolRiskKind,
        severity: DoctorSeverity,
        toolName: String,
        evidence: String,
        explanation: String
    ) {
        self.kind = kind
        self.severity = severity
        self.toolName = toolName
        self.evidence = evidence
        self.explanation = explanation
    }
}

/// Reads tool descriptions the way the model does, and reports what a person
/// reading the same screen would not have seen.
///
/// A tool's name, description and input schema are not documentation — clients
/// put them straight into the model's context, which makes them the one part of
/// an MCP server that can give instructions without ever being called. A server
/// that passes its health check, exposes plausible tools and hides a sentence in
/// one of their descriptions is indistinguishable, on every other screen in this
/// app, from a good one.
///
/// **Deliberately narrow, for the same reason `hasRelativePath` is.** These
/// findings sit next to a green dot and have to be worth reading. Anything that
/// would fire on an ordinary filesystem or shell server is not in here: the
/// patterns are ones with no honest reading, and the scan never disables
/// anything or edits a config — it is advice, and §7.10 keeps it that way.
public enum ToolSafetyScanner {

    public static func scan(_ tools: [ToolSummary]) -> [ToolRisk] {
        tools.flatMap(risks(in:))
    }

    static func risks(in tool: ToolSummary) -> [ToolRisk] {
        var risks: [ToolRisk] = []
        // The schema counts. A description is what gets read out loud, but a
        // parameter's `description` field reaches the model just as directly and
        // is where nobody looks.
        let surfaces = [tool.name, tool.description, tool.inputSchemaJSON].compactMap { $0 }
        let text = surfaces.joined(separator: "\n")

        if let found = hiddenCharacter(in: text) {
            risks.append(ToolRisk(
                kind: .hiddenCharacters,
                severity: .error,
                toolName: tool.name,
                evidence: found,
                explanation: """
                This tool's description contains characters that do not render. \
                The model reads them and you cannot, which is the mechanism, not \
                a side effect — there is no legitimate reason for them here.
                """
            ))
        }

        if let phrase = match(in: text, against: overridePhrases) {
            risks.append(ToolRisk(
                kind: .instructionOverride,
                severity: .error,
                toolName: tool.name,
                evidence: phrase,
                explanation: """
                The description gives the model an instruction instead of \
                describing what the tool does. A tool contract is loaded into \
                context before anything is called, so this runs whether or not \
                you ever use the tool.
                """
            ))
        }

        if let marker = match(in: text, against: authorityMarkers) {
            risks.append(ToolRisk(
                kind: .impersonatedAuthority,
                severity: .warning,
                toolName: tool.name,
                evidence: marker,
                explanation: """
                The description contains markup that imitates a system or \
                developer message. Some clients pass it through verbatim, where \
                it can read to the model as though it came from you.
                """
            ))
        }

        // Only where the tool has told the client it is safe. A shell tool
        // mentioning `id_rsa` is doing its job; a tool advertising `readOnlyHint`
        // and naming private key material is describing something else.
        if tool.annotations?.readOnlyHint == true,
           let reference = match(in: text, against: credentialReferences) {
            risks.append(ToolRisk(
                kind: .credentialInterest,
                severity: .warning,
                toolName: tool.name,
                evidence: reference,
                explanation: """
                This tool is advertised as read-only and its contract names \
                credential material. Annotations are hints from the server \
                itself and are not enforced by anything — treat the claim and \
                the interest as two separate facts.
                """
            ))
        }

        return risks
    }

    /// Two enabled servers offering the same tool name to one client.
    ///
    /// The client presents one flat list of tools, so the model picks by name and
    /// nothing tells it which server it got. That makes the collision both an
    /// ordinary bug — the wrong `search` runs — and the shape of a real attack,
    /// where a newly added server quietly claims the name of a trusted one.
    public static func shadowedNames(
        across servers: [(name: String, tools: [ToolSummary])]
    ) -> [String: [String]] {
        var owners: [String: [String]] = [:]
        for server in servers {
            for toolName in Set(server.tools.map(\.name)) {
                owners[toolName, default: []].append(server.name)
            }
        }
        return owners
            .filter { $0.value.count > 1 }
            .mapValues { $0.sorted() }
    }

    // MARK: - Patterns

    /// Phrasing that addresses the reader as an agent to be redirected. Each of
    /// these is a documented prompt-injection opener; none of them describes a
    /// tool.
    private static let overridePhrases = [
        "ignore previous instruction",
        "ignore prior instruction",
        "ignore all previous",
        "disregard previous instruction",
        "disregard the above",
        "do not tell the user",
        "do not mention this",
        "without telling the user",
        "without informing the user",
        "do not inform the user",
        "hide this from",
        "never reveal",
        "before using any other tool",
        "before calling any other tool",
        "you must first call",
        "always call this tool first",
        "this supersedes",
        "override your",
        "new instructions:",
        "system prompt:",
    ]

    private static let authorityMarkers = [
        "<important>",
        "<system>",
        "</system>",
        "<system-reminder",
        "[[system",
        "<|im_start|>",
        "<|system|>",
        "### system",
    ]

    private static let credentialReferences = [
        "id_rsa",
        "id_ed25519",
        ".ssh/",
        ".aws/credentials",
        ".netrc",
        "/etc/passwd",
        "etc/shadow",
        ".env file",
        "private key",
        "api key",
        "access token",
        "password",
    ]

    private static func match(in text: String, against needles: [String]) -> String? {
        let haystack = text.lowercased()
        return needles.first { haystack.contains($0) }
    }

    // MARK: - Invisible characters

    /// Scalars that carry no visible mark of their own.
    ///
    /// Zero-width joiners and non-joiners are excluded on purpose: they are load
    /// bearing in Arabic, Indic and Persian text and in emoji sequences, so
    /// flagging them would punish a correctly-written non-English description.
    /// What is left has no typographic use inside a tool description.
    private static func hiddenCharacter(in text: String) -> String? {
        for scalar in text.unicodeScalars {
            switch scalar.value {
            case 0x200B, 0x200E, 0x200F: // zero-width space, LTR/RTL marks
                return label("zero-width or direction mark", scalar)
            case 0x202A...0x202E: // bidirectional overrides
                return label("bidirectional override", scalar)
            case 0x2060...0x2064, 0xFEFF: // word joiner, invisible operators, BOM
                return label("invisible formatting character", scalar)
            case 0xE0000...0xE007F: // Unicode tag characters
                return label("Unicode tag character", scalar)
            default:
                continue
            }
        }
        return nil
    }

    private static func label(_ description: String, _ scalar: Unicode.Scalar) -> String {
        String(format: "%@ (U+%04X)", description, scalar.value)
    }
}
