using System.Text;
using System.Text.Json;
using Kytto.Core.Model;

namespace Kytto.Core.Doctor;

/// <summary>
/// What Kytto learned about one package when the user asked (§7.10).
/// </summary>
/// <remarks>
/// Never fetched on its own: a check sends the package name — and its version, when the
/// launch pins one — to osv.dev and, for npm, to the npm registry, and nothing else from
/// the configuration.
/// </remarks>
public sealed record PackageSafetyReport(
    PackageEcosystem Ecosystem,
    string PackageName,
    /// <summary>The exact version that was asked about, or null for the package as a whole.</summary>
    string? Version,
    IReadOnlyList<PackageSafetyReport.Advisory> Advisories,
    /// <summary>npm lifecycle scripts that run on install: name → command, as published.</summary>
    IReadOnlyDictionary<string, string> InstallScripts,
    DateTimeOffset CheckedAt)
{
    public sealed record Advisory(string Id, string? Summary)
    {
        /// <summary>OSV's <c>MAL-</c> records: the package itself is malicious, not a bug in it.</summary>
        public bool IsMalware => Id.StartsWith("MAL-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this report still describes what <paramref name="launch"/> runs. An edit to
    /// the command must not leave an old verdict attached to a new package.
    /// </summary>
    public bool Describes(PackageLaunch launch) =>
        launch.Ecosystem == Ecosystem && launch.Name == PackageName
        && (launch.IsPinned ? launch.RequestedVersion : null) == Version;
}

public static class PackageSafety
{
    /// <summary>The package a server fetches by name, if it does.</summary>
    public static PackageLaunch? Launch(Server server) =>
        server.Transport == Transport.Stdio && !server.IsBundled
            ? PackageLaunch.Parse(server.Command, server.Args)
            : null;

    /// <summary>
    /// The version to ask about: only one the launch pins. An unpinned launch is asked
    /// about as a whole package, which still finds malware records.
    /// </summary>
    public static string? QueriedVersion(PackageLaunch launch) =>
        launch.IsPinned ? launch.RequestedVersion : null;

    // MARK: - Findings

    /// <summary>
    /// Every package-safety finding for <paramref name="server"/>: the look-alike check,
    /// which is offline and always runs, and the results of a check the user asked for,
    /// when there is one for the package this server runs now.
    /// </summary>
    public static IReadOnlyList<DoctorFinding> Findings(Server server, PackageSafetyReport? report)
    {
        if (Launch(server) is not { } launch) return [];
        var findings = new List<DoctorFinding>();
        if (LookAlike(launch.Name, launch.Ecosystem) is { } official)
        {
            findings.Add(new DoctorFinding(
                "look-alike-package",
                DoctorSeverity.Warning,
                "The package name imitates an official server",
                $"“{launch.Name}” is not “{official}”, but its name is close enough to be " +
                "mistaken for it. Fake MCP packages with official-looking names have sent " +
                "machine details and secrets to attackers on install.",
                $"If you meant the official server, change the command to “{official}”. If " +
                "this package is the one you chose on purpose, check who publishes it before " +
                "the next launch — Check package safety asks osv.dev about known malware.",
                report?.Describes(launch) == true ? null : DoctorAction.CheckPackageSafety));
        }
        if (report is null || !report.Describes(launch)) return findings;

        var malware = report.Advisories.Where(advisory => advisory.IsMalware).ToArray();
        if (malware.Length > 0)
        {
            findings.Add(new DoctorFinding(
                "known-malware",
                DoctorSeverity.Error,
                "This package is listed as malware",
                $"osv.dev lists “{launch.Name}” as malicious ({Describe(malware)}). A client " +
                "that launches this server runs that code with the server's environment, " +
                "tokens included.",
                "Switch the server off in every client now, remove it, and rotate any secret " +
                "its environment carried. Kytto did not change anything on its own."));
        }
        var vulnerabilities = report.Advisories.Where(advisory => !advisory.IsMalware).ToArray();
        if (vulnerabilities.Length > 0)
        {
            findings.Add(new DoctorFinding(
                "known-vulnerabilities",
                DoctorSeverity.Warning,
                "This version has known vulnerabilities",
                $"osv.dev lists {Describe(vulnerabilities)} for {launch.Name} {report.Version ?? ""}.",
                "Read the advisories and pin a release that fixes them."));
        }
        if (report.InstallScripts.Count > 0)
        {
            var scripts = report.InstallScripts.Keys
                .Order(StringComparer.Ordinal)
                .Select(name => $"{name}: {Truncated(report.InstallScripts[name])}");
            findings.Add(new DoctorFinding(
                "install-script",
                DoctorSeverity.Warning,
                "The package runs a script when it is installed",
                $"{launch.Name}{(report.Version is { } version ? " " + version : "")} declares " +
                $"{string.Join("; ", scripts)}. {launch.Runner} runs it before the " +
                "server starts, with your user's permissions — the route install-time " +
                "malware takes.",
                "Read what the script does on the package page. Pinning an exact version " +
                "keeps a later release from adding or changing it."));
        }
        return findings;
    }

    private static string Describe(IReadOnlyList<PackageSafetyReport.Advisory> advisories)
    {
        var ids = advisories.Take(5).Select(advisory => advisory.Id);
        var more = advisories.Count > 5 ? $" and {advisories.Count - 5} more" : "";
        return string.Join(", ", ids) + more;
    }

    private static string Truncated(string text)
    {
        var line = text.Replace("\n", " ", StringComparison.Ordinal);
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    // MARK: - Look-alike names (offline)

    /// <summary>
    /// Official servers whose names get imitated, per registry. A small list on purpose:
    /// it only has to name what an imitation would be mistaken for.
    /// </summary>
    internal static readonly string[] OfficialNpm =
    [
        "@modelcontextprotocol/server-everything",
        "@modelcontextprotocol/server-filesystem",
        "@modelcontextprotocol/server-memory",
        "@modelcontextprotocol/server-sequential-thinking",
        "@modelcontextprotocol/server-github",
        "@modelcontextprotocol/server-gitlab",
        "@modelcontextprotocol/server-slack",
        "@modelcontextprotocol/server-postgres",
        "@modelcontextprotocol/server-puppeteer",
        "@modelcontextprotocol/server-brave-search",
        "@modelcontextprotocol/server-google-maps",
        "@modelcontextprotocol/server-gdrive",
        "@modelcontextprotocol/server-redis",
        "@modelcontextprotocol/server-aws-kb-retrieval",
        "@modelcontextprotocol/server-everart",
        "@modelcontextprotocol/inspector",
        "@playwright/mcp",
        "@upstash/context7-mcp",
        "@notionhq/notion-mcp-server",
        "@stripe/mcp",
        "@supabase/mcp-server-supabase",
        "@sentry/mcp-server",
        "@cloudflare/mcp-server-cloudflare",
        "@browserbasehq/mcp",
        "@azure/mcp",
        "@heroku/mcp-server",
        "@shopify/dev-mcp",
        "@hubspot/mcp-server",
        "@paypal/mcp",
        "@vercel/mcp-adapter",
        "@figma/mcp",
        "@atlassian/mcp",
        "@linear/mcp",
        "firecrawl-mcp",
        "chrome-devtools-mcp",
        "xcodebuildmcp",
    ];

    internal static readonly string[] OfficialPyPi =
    [
        "mcp-server-fetch",
        "mcp-server-git",
        "mcp-server-time",
        "mcp-server-sqlite",
        "mcp-server-sentry",
        "mcp-server-aws-kb-retrieval",
        "awslabs.core-mcp-server",
        "mcp",
    ];

    /// <summary>The official package <paramref name="name"/> imitates, or null.</summary>
    /// <remarks>
    /// An unscoped npm name that reduces to the same words as an official one
    /// (<c>mcp-server-github</c> for <c>@modelcontextprotocol/server-github</c>), a scope
    /// one or two letters away from an official scope, or a name one or two letters away
    /// from an official one. Exact official names never match.
    /// </remarks>
    public static string? LookAlike(string name, PackageEcosystem ecosystem)
    {
        var lowered = name.ToLowerInvariant();
        if (ecosystem == PackageEcosystem.Npm)
        {
            if (OfficialNpm.Contains(lowered, StringComparer.Ordinal)) return null;
            var (scope, bare) = Split(lowered);
            foreach (var official in OfficialNpm)
            {
                var (officialScope, officialBare) = Split(official);
                if (scope is not null && officialScope is not null && scope != officialScope
                    && Distance(scope, officialScope) <= 2 && Distance(bare, officialBare) <= 1)
                {
                    return official;
                }
                if (scope is null && officialScope is not null)
                {
                    var core = CoreWords(bare);
                    if (core.Length >= 3 && core == CoreWords(officialBare))
                    {
                        return official;
                    }
                }
                if (scope == officialScope && bare != officialBare && bare.Length >= 6
                    && Distance(bare, officialBare) <= (bare.Length >= 12 ? 2 : 1))
                {
                    return official;
                }
            }
            return null;
        }

        var normalized = NormalizePyPi(lowered);
        if (OfficialPyPi.Select(NormalizePyPi).Contains(normalized, StringComparer.Ordinal)) return null;
        foreach (var official in OfficialPyPi.Where(candidate => candidate.Length >= 6))
        {
            if (Distance(normalized, NormalizePyPi(official)) <= (official.Length >= 12 ? 2 : 1))
            {
                return official;
            }
        }
        return null;
    }

    private static (string? Scope, string Bare) Split(string name)
    {
        var slash = name.IndexOf('/');
        return name.StartsWith('@') && slash >= 0 ? (name[1..slash], name[(slash + 1)..]) : (null, name);
    }

    /// <summary>The words of a name without the ones every MCP package carries.</summary>
    private static string CoreWords(string name) =>
        string.Join("-", name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("mcp" or "server" or "mcpserver" or "model" or "context" or "protocol")));

    /// <summary>PEP 503: runs of <c>-</c>, <c>_</c> and <c>.</c> are one separator.</summary>
    private static string NormalizePyPi(string name) =>
        string.Join("-", name.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Levenshtein distance, capped early: only "0, 1, 2 or more" matters here.</summary>
    internal static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return 3;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[b.Length];
    }

    // MARK: - Registry answers

    /// <summary>The OSV query for one package, as JSON, with its keys in sorted order.</summary>
    public static byte[] OsvQuery(PackageLaunch launch)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("package");
            writer.WriteString("ecosystem", launch.Ecosystem == PackageEcosystem.Npm ? "npm" : "PyPI");
            writer.WriteString("name", launch.Name);
            writer.WriteEndObject();
            if (QueriedVersion(launch) is { } version) writer.WriteString("version", version);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Advisories from an OSV <c>/v1/query</c> answer. Without a version OSV answers for
    /// every release ever published, so only malware — which concerns the package, not a
    /// release — is kept then.
    /// </summary>
    public static IReadOnlyList<PackageSafetyReport.Advisory> AdvisoriesFromOsv(byte[] data, bool versionQueried)
    {
        using var document = ParseObject(data);
        var advisories = new List<PackageSafetyReport.Advisory>();
        if (document.RootElement.TryGetProperty("vulns", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in records.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object
                    || !record.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String
                    || idElement.GetString() is not { Length: > 0 and <= 64 } id)
                {
                    continue;
                }
                string? summary = null;
                if (record.TryGetProperty("summary", out var summaryElement)
                    && summaryElement.ValueKind == JsonValueKind.String
                    && summaryElement.GetString() is { } text)
                {
                    summary = text.Length > 200 ? text[..200] : text;
                }
                advisories.Add(new PackageSafetyReport.Advisory(id, summary));
            }
        }
        return versionQueried ? advisories : advisories.Where(advisory => advisory.IsMalware).ToArray();
    }

    /// <summary>The install-time lifecycle scripts in an npm version document.</summary>
    public static IReadOnlyDictionary<string, string> InstallScriptsFromNpmVersion(byte[] data)
    {
        using var document = ParseObject(data);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var name in new[] { "preinstall", "install", "postinstall" })
        {
            if (scripts.TryGetProperty(name, out var command)
                && command.ValueKind == JsonValueKind.String
                && command.GetString() is { Length: > 0 } text)
            {
                result[name] = text;
            }
        }
        return result;
    }

    private static System.Text.Json.JsonDocument ParseObject(byte[] data)
    {
        try
        {
            var document = System.Text.Json.JsonDocument.Parse(data);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException)
        {
            // Falls through to the same refusal as a document of the wrong shape.
        }
        throw new InvalidDataException("The answer was not a JSON object.");
    }
}
