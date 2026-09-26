using System.Globalization;
using Kytto.Core.Doctor;

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
            new[] { tool.Name, tool.Description, tool.InputSchemaJSON }.OfType<string>());

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

    private static string? Match(string text, string[] needles) =>
        Array.Find(needles, needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

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
                // Bidirectional overrides.
                >= 0x202A and <= 0x202E => "bidirectional override",
                // Word joiner, invisible operators, BOM.
                (>= 0x2060 and <= 0x2064) or 0xFEFF => "invisible formatting character",
                >= 0xE0000 and <= 0xE007F => "Unicode tag character",
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
