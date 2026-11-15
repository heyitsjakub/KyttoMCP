using System.Globalization;
using System.Text;
using Kytto.Core.Doctor;
using Kytto.Core.Json;

namespace Kytto.Core.Health;

public enum ToolRiskKind
{
    /// <summary>
    /// Characters that are in the text a model reads and not in the text a human
    /// reads. There is no benign version of this.
    /// </summary>
    HiddenCharacters,

    /// <summary>Wording that addresses the model rather than describing the tool.</summary>
    InstructionOverride,

    /// <summary>Markup that imitates a system or developer message.</summary>
    ImpersonatedAuthority,

    /// <summary>A read-only tool asking for credentials or key material by name.</summary>
    CredentialInterest,

    /// <summary>Two enabled servers claiming the same tool name in one client.</summary>
    ShadowedName,

    /// <summary>
    /// Wording that tells the model to act without asking the user, or to treat an
    /// action as already approved.
    /// </summary>
    ConsentBypass,
}

public static class ToolRiskKinds
{
    /// <summary>The spelling a Doctor finding's <c>code</c> carries.</summary>
    public static string Raw(this ToolRiskKind kind) => kind switch
    {
        ToolRiskKind.HiddenCharacters => "hiddenCharacters",
        ToolRiskKind.InstructionOverride => "instructionOverride",
        ToolRiskKind.ImpersonatedAuthority => "impersonatedAuthority",
        ToolRiskKind.CredentialInterest => "credentialInterest",
        ToolRiskKind.ConsentBypass => "consentBypass",
        _ => "shadowedName",
    };
}

/// <param name="Evidence">What was found, quoted narrowly enough to be checked by hand.</param>
public sealed record ToolRisk(
    ToolRiskKind Kind,
    DoctorSeverity Severity,
    string ToolName,
    string Evidence,
    string Explanation);

/// <summary>
/// Reads tool descriptions the way the model does, and reports what a person
/// reading the same screen would not have seen.
/// </summary>
/// <remarks>
/// <para>
/// A tool's name, description and input schema are not documentation — clients put
/// them straight into the model's context, which makes them the one part of an MCP
/// server that can give instructions without ever being called. A server that
/// passes its health check, exposes plausible tools and hides a sentence in one of
/// their descriptions is indistinguishable, on every other screen in this app, from
/// a good one.
/// </para>
/// <para>
/// <strong>Deliberately narrow, for the same reason <c>HasRelativePath</c> is.</strong>
/// These findings sit next to a green dot and have to be worth reading. Anything
/// that would fire on an ordinary filesystem or shell server is not in here: the
/// patterns are ones with no honest reading, and the scan never disables anything or
/// edits a config — it is advice, and §7.10 keeps it that way.
/// </para>
/// </remarks>
public static class ToolSafetyScanner
{
    public static IReadOnlyList<ToolRisk> Scan(IEnumerable<ToolSummary> tools) =>
        tools.SelectMany(Risks).ToArray();

    internal static IReadOnlyList<ToolRisk> Risks(ToolSummary tool)
    {
        var risks = new List<ToolRisk>();
        // The schema counts. A description is what gets read out loud, but a
        // parameter's `description` field reaches the model just as directly and is
        // where nobody looks.
        var text = string.Join(
            '\n',
            new[]
            {
                tool.Name,
                tool.Description,
                tool.InputSchemaJSON is { } schema ? SchemaText(schema) : null,
            }.OfType<string>());

        if (HiddenCharacter(text) is { } found)
        {
            risks.Add(new ToolRisk(
                ToolRiskKind.HiddenCharacters,
                DoctorSeverity.Error,
                tool.Name,
                found,
                "This tool's description contains characters that do not render. " +
                "The model reads them and you cannot, which is the mechanism, not a " +
                "side effect — there is no legitimate reason for them here."));
        }

        if (Match(text, OverridePhrases) is { } phrase)
        {
            risks.Add(new ToolRisk(
                ToolRiskKind.InstructionOverride,
                DoctorSeverity.Error,
                tool.Name,
                phrase,
                "The description gives the model an instruction instead of describing " +
                "what the tool does. A tool contract is loaded into context before " +
                "anything is called, so this runs whether or not you ever use the tool."));
        }

        if (UnnegatedMatch(text, ConsentBypassPhrases) is { } bypass)
        {
            risks.Add(new ToolRisk(
                ToolRiskKind.ConsentBypass,
                DoctorSeverity.Warning,
                tool.Name,
                bypass,
                "The description tells the model to go ahead without asking you, or to " +
                "treat the action as already approved. Whether you are asked is decided " +
                "by your client's permission settings, not by this text, and Kytto cannot " +
                "see those. This is a note about the wording: check that the client still " +
                "asks where you expect it to."));
        }

        if (Match(text, AuthorityMarkers) is { } marker)
        {
            risks.Add(new ToolRisk(
                ToolRiskKind.ImpersonatedAuthority,
                DoctorSeverity.Warning,
                tool.Name,
                marker,
                "The description contains markup that imitates a system or developer " +
                "message. Some clients pass it through verbatim, where it can read to " +
                "the model as though it came from you."));
        }

        // Only where the tool has told the client it is safe. A shell tool mentioning
        // `id_rsa` is doing its job; a tool advertising `readOnlyHint` and naming
        // private key material is describing something else.
        if (tool.Annotations?.ReadOnlyHint == true && Match(text, CredentialReferences) is { } reference)
        {
            risks.Add(new ToolRisk(
                ToolRiskKind.CredentialInterest,
                DoctorSeverity.Warning,
                tool.Name,
                reference,
                "This tool is advertised as read-only and its contract names credential " +
                "material. Annotations are hints from the server itself and are not " +
                "enforced by anything — treat the claim and the interest as two " +
                "separate facts."));
        }

        return risks;
    }

    /// <summary>Two enabled servers offering the same tool name to one client.</summary>
    /// <remarks>
    /// The client presents one flat list of tools, so the model picks by name and
    /// nothing tells it which server it got. That makes the collision both an ordinary
    /// bug — the wrong <c>search</c> runs — and the shape of a real attack, where a
    /// newly added server quietly claims the name of a trusted one.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ShadowedNames(
        IEnumerable<(string Name, IReadOnlyList<ToolSummary> Tools)> servers)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            foreach (var toolName in server.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal))
            {
                if (!owners.TryGetValue(toolName, out var holders)) owners[toolName] = holders = [];
                holders.Add(server.Name);
            }
        }

        return owners
            .Where(pair => pair.Value.Count > 1)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
    }

    // MARK: - Patterns

    /// <summary>Phrasing that addresses the reader as an agent to be redirected.</summary>
    /// <remarks>
    /// Each of these is a documented prompt-injection opener; none of them describes a
    /// tool.
    /// </remarks>
    private static readonly string[] OverridePhrases =
    [
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
    ];

    /// <summary>Wording that asks the model to skip the user's approval.</summary>
    /// <remarks>
    /// Describing approval is not this: "asks for confirmation before deleting" and
    /// "set <c>confirm</c> once the user agrees" are how a careful tool reads. So each
    /// phrase is about <em>not</em> asking, and a negation earlier in the same sentence
    /// ("never deletes without asking the user") cancels the match.
    /// </remarks>
    internal static readonly string[] ConsentBypassPhrases =
    [
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
    ];

    private static readonly string[] Negations = [" never ", " not ", "n't ", " no ", " cannot ", " only after ", " unless "];

    /// <summary>The first phrase that occurs with no negation before it in its sentence.</summary>
    /// <remarks>
    /// The phrase's own "do not" or "never" is the instruction; only a negation in
    /// front of it reverses the meaning.
    /// </remarks>
    internal static string? UnnegatedMatch(string text, string[] needles)
    {
        var haystack = NormalizedForMatching(text);
        foreach (var needle in needles)
        {
            var from = 0;
            while (from <= haystack.Length)
            {
                var index = haystack.IndexOf(needle, from, StringComparison.Ordinal);
                if (index < 0) break;
                from = index + needle.Length;
                var sentenceStart = index == 0 ? 0 : haystack.LastIndexOfAny(SentenceEnds, index - 1) + 1;
                var before = " " + haystack[sentenceStart..index];
                if (Negations.Any(negation => before.Contains(negation, StringComparison.Ordinal))) continue;
                return needle;
            }
        }
        return null;
    }

    private static readonly char[] SentenceEnds = ['.', '!', '?', ';', ':', '\n'];

    private static readonly string[] AuthorityMarkers =
    [
        "<important>",
        "<system>",
        "</system>",
        "<system-reminder",
        "[[system",
        "<|im_start|>",
        "<|system|>",
        "### system",
    ];

    private static readonly string[] CredentialReferences =
    [
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
    ];

    private static string? Match(string text, string[] needles)
    {
        var haystack = NormalizedForMatching(text);
        return Array.Find(needles, needle => haystack.Contains(needle, StringComparison.Ordinal));
    }

    /// <summary>What the model reads, not how the server spelled it.</summary>
    /// <remarks>
    /// Characters that do not render — a soft hyphen, a zero-width joiner — and a
    /// line break or double space in the middle of a phrase change nothing for the
    /// model, but would each walk a phrase past a plain substring match. Joiners are
    /// not <em>flagged</em> (see <see cref="HiddenCharacter"/>), only ignored here.
    /// </remarks>
    internal static string NormalizedForMatching(string text)
    {
        var result = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (rune.Value is 0x00AD or 0x034F or 0x115F or 0x1160 or 0x180E or (>= 0x200B and <= 0x200D) or
                (>= 0x2060 and <= 0x2064) or 0x3164 or 0xFEFF or 0xFFA0 or (>= 0xFE00 and <= 0xFE0F) or
                (>= 0xE0000 and <= 0xE01EF))
            {
                continue;
            }
            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                result.Append(rune.ToString());
                lastWasSpace = false;
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// An input schema as the model reads it: property names and string values, with
    /// JSON escapes decoded.
    /// </summary>
    /// <remarks>
    /// The stored schema is the raw slice the server sent, where a zero-width space
    /// can be written as an escape and an instruction can hide behind escapes —
    /// invisible to a scan of the raw text, and decoded before the model sees it. If
    /// the slice does not parse, the raw text is scanned instead.
    /// </remarks>
    internal static string SchemaText(string json)
    {
        JsonNode root;
        try
        {
            root = JsonDocument.Parse(json).Root;
        }
        catch (JsonParseException)
        {
            return json;
        }

        var parts = new List<string>();
        Walk(root);
        return string.Join('\n', parts);

        void Walk(JsonNode node)
        {
            if (node.Members is { } members)
            {
                foreach (var member in members)
                {
                    parts.Add(member.Key);
                    Walk(member.Value);
                }
            }
            else if (node.Elements is { } elements)
            {
                foreach (var element in elements) Walk(element);
            }
            else if (node.StringValue is { } value)
            {
                parts.Add(value);
            }
        }
    }

    // MARK: - Invisible characters

    /// <summary>Scalars that carry no visible mark of their own.</summary>
    /// <remarks>
    /// Zero-width joiners and non-joiners are excluded on purpose: they are
    /// load-bearing in Arabic, Indic and Persian text and in emoji sequences, so
    /// flagging them would punish a correctly-written non-English description. What is
    /// left has no typographic use inside a tool description.
    /// </remarks>
    private static string? HiddenCharacter(string text)
    {
        // Enumerated by rune rather than by char, so the tag block above the BMP is
        // matched as one scalar instead of as two surrogates.
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var description = value switch
            {
                // Zero-width space, LTR/RTL marks.
                0x200B or 0x200E or 0x200F => "zero-width or direction mark",
                // Bidirectional embeddings, overrides and isolates.
                (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) => "bidirectional control",
                // Word joiner, invisible operators, BOM.
                (>= 0x2060 and <= 0x2064) or 0xFEFF => "invisible formatting character",
                >= 0xE0000 and <= 0xE007F => "Unicode tag character",
                // Supplementary variation selectors, used to smuggle payloads.
                >= 0xE0100 and <= 0xE01EF => "variation selector",
                // Grapheme joiner, Mongolian vowel separator, Hangul fillers.
                0x034F or 0x180E or 0x115F or 0x1160 or 0x3164 or 0xFFA0 => "invisible formatting character",
                _ => null,
            };
            if (description is not null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{description} (U+{value:X4})");
            }
        }
        return null;
    }
}
