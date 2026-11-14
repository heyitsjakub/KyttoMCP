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
    /// Wording that tells the model to act without asking the user, or to
    /// treat an action as already approved.
    case consentBypass
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
        let surfaces = [tool.name, tool.description, tool.inputSchemaJSON.map(schemaText)].compactMap { $0 }
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

        if let phrase = unnegatedMatch(in: text, against: consentBypassPhrases) {
            risks.append(ToolRisk(
                kind: .consentBypass,
                severity: .warning,
                toolName: tool.name,
                evidence: phrase,
                explanation: """
                The description tells the model to go ahead without asking you, \
                or to treat the action as already approved. Whether you are \
                asked is decided by your client's permission settings, not by \
                this text, and Kytto cannot see those. This is a note about the \
                wording: check that the client still asks where you expect it to.
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

    /// Wording that asks the model to skip the user's approval. Describing
    /// approval is not this: "asks for confirmation before deleting" and "set
    /// `confirm` once the user agrees" are how a careful tool reads. So each
    /// phrase is about *not* asking, and a negation earlier in the same
    /// sentence ("never deletes without asking the user") cancels the match.
    static let consentBypassPhrases = [
        "without asking the user",
        "without asking for permission",
        "without asking for confirmation",
        "without asking for approval",
        "without asking first",
        "without user approval",
        "without user confirmation",
        "without the user's approval",
        "without the user's permission",
        "without the user's consent",
        "without confirmation from the user",
        "without waiting for the user",
        "do not ask the user",
        "don't ask the user",
        "do not ask for confirmation",
        "don't ask for confirmation",
        "do not ask for permission",
        "don't ask for permission",
        "do not ask for approval",
        "don't ask for approval",
        "do not wait for confirmation",
        "do not wait for the user",
        "never ask the user",
        "never ask for confirmation",
        "never ask for permission",
        "no need to ask the user",
        "no need to ask for",
        "no need to confirm",
        "no confirmation is needed",
        "no confirmation needed",
        "skip confirmation",
        "skip the confirmation",
        "skip approval",
        "skip user approval",
        "skip the approval",
        "bypass confirmation",
        "bypass approval",
        "bypass the approval",
        "bypass the confirmation",
        "assume the user has approved",
        "assume the user has consented",
        "assume the user approves",
        "assume the user agrees",
        "assume approval",
        "assume consent",
        "treat this as approved",
        "treat it as approved",
        "treat as pre-approved",
        "is pre-approved",
        "has already been approved by the user",
    ]

    private static let negations = [" never ", " not ", "n't ", " no ", " cannot ", " only after ", " unless "]

    /// The first phrase that occurs with no negation before it in its sentence.
    static func unnegatedMatch(in text: String, against needles: [String]) -> String? {
        let haystack = normalizedForMatching(text)
        for needle in needles {
            var searchStart = haystack.startIndex
            while let range = haystack.range(of: needle, range: searchStart..<haystack.endIndex) {
                searchStart = range.upperBound
                let sentenceStart = haystack[..<range.lowerBound]
                    .lastIndex { ".!?;:\n".contains($0) }
                    .map { haystack.index(after: $0) } ?? haystack.startIndex
                let before = " " + haystack[sentenceStart..<range.lowerBound]
                // The phrase's own "do not" or "never" is the instruction; only
                // a negation in front of it reverses the meaning.
                if negations.contains(where: { before.contains($0) }) { continue }
                return needle
            }
        }
        return nil
    }

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
        let haystack = normalizedForMatching(text)
        return needles.first { haystack.contains($0) }
    }

    /// What the model reads, not how the server spelled it.
    ///
    /// Characters that do not render — a soft hyphen, a zero-width joiner — and
    /// a line break or double space in the middle of a phrase change nothing for
    /// the model, but would each walk a phrase past a plain substring match.
    /// Joiners are not *flagged* (see `hiddenCharacter`), only ignored here.
    static func normalizedForMatching(_ text: String) -> String {
        var result = String.UnicodeScalarView()
        var lastWasSpace = false
        for scalar in text.lowercased().unicodeScalars {
            switch scalar.value {
            case 0x00AD, 0x034F, 0x115F, 0x1160, 0x180E, 0x200B...0x200D, 0x2060...0x2064, 0x3164, 0xFEFF, 0xFFA0,
                 0xFE00...0xFE0F, 0xE0000...0xE01EF:
                continue
            default:
                break
            }
            if scalar.properties.isWhitespace {
                if !lastWasSpace { result.append(" ") }
                lastWasSpace = true
            } else {
                result.append(scalar)
                lastWasSpace = false
            }
        }
        return String(result)
    }

    /// An input schema as the model reads it: property names and string values,
    /// with JSON escapes decoded.
    ///
    /// The stored schema is the raw slice the server sent, where a zero-width
    /// space can be written as `\u200b` and an instruction can hide behind
    /// escapes — invisible to a scan of the raw text, and decoded before the model
    /// sees it. If the slice does not parse, the raw text is scanned instead.
    static func schemaText(_ json: String) -> String {
        guard let document = try? JSONDocument.parse(json) else { return json }
        var parts: [String] = []
        func walk(_ node: JSONNode) {
            if let members = node.members {
                for member in members {
                    parts.append(member.key)
                    walk(member.value)
                }
            } else if let elements = node.elements {
                elements.forEach(walk)
            } else if let string = node.stringValue {
                parts.append(string)
            }
        }
        walk(document.root)
        return parts.joined(separator: "\n")
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
            case 0x202A...0x202E, 0x2066...0x2069: // bidi embeddings, overrides, isolates
                return label("bidirectional control", scalar)
            case 0x2060...0x2064, 0xFEFF: // word joiner, invisible operators, BOM
                return label("invisible formatting character", scalar)
            case 0xE0000...0xE007F: // Unicode tag characters
                return label("Unicode tag character", scalar)
            case 0xE0100...0xE01EF: // supplementary variation selectors, used to smuggle payloads
                return label("variation selector", scalar)
            case 0x034F, 0x180E, 0x115F, 0x1160, 0x3164, 0xFFA0: // grapheme joiner, Mongolian separator, Hangul fillers
                return label("invisible formatting character", scalar)
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
