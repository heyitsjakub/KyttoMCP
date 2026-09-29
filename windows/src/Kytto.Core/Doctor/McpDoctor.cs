using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Doctor;

public enum DoctorSeverity { Info, Warning, Error }

public enum DoctorAction
{
    RunHealthCheck,
    PinResolvedCommand,

    /// <summary>Pin a runner-launched package to an exact release the user looked up.</summary>
    PinPackageVersion,
}

public sealed record DoctorFinding(
    string Code,
    DoctorSeverity Severity,
    string Title,
    string Detail,
    string Remediation,
    DoctorAction? Action = null);

public sealed record DoctorServerReport(
    string ServerID,
    string ServerName,
    IReadOnlyList<DoctorFinding> Findings);

/// <summary>Turns config and health evidence into concrete next steps (§7.10).</summary>
/// <remarks>
/// Stderr is classified only into advice. The automatic repairs pin an executable
/// path already established by a successful health check, or a package version
/// the user looked up; either is previewed and written through the ordinary
/// backup/atomic-write transaction.
/// </remarks>
public static class McpDoctor
{
    /// <summary>Every server's findings, including the ones no single server can see.</summary>
    /// <remarks>
    /// Tool-name collisions are a property of a <em>client</em>, not of a server: two
    /// servers are only in each other's way because one client has both of them
    /// switched on. So they are worked out here, across the whole matrix, and then
    /// attached to each server involved.
    /// </remarks>
    public static IReadOnlyList<DoctorServerReport> Reports(
        IReadOnlyList<Server> servers,
        IReadOnlyList<ClientId> clients)
    {
        var collisions = CollisionFindings(servers, clients);
        return servers
            .Select(server =>
            {
                var report = Analyze(server);
                if (collisions.GetValueOrDefault(server.Id) is not { Count: > 0 } extra) return report;
                return report with { Findings = [.. report.Findings, .. extra] };
            })
            .Where(report => report.Findings.Count > 0)
            .ToArray();
    }

    /// <summary>One finding per server per client where a tool name is claimed twice.</summary>
    private static Dictionary<string, List<DoctorFinding>> CollisionFindings(
        IReadOnlyList<Server> servers,
        IReadOnlyList<ClientId> clients)
    {
        var result = new Dictionary<string, List<DoctorFinding>>(StringComparer.Ordinal);

        foreach (var clientId in clients)
        {
            var active = servers
                .Where(server =>
                    server.EnabledIn.GetValueOrDefault(clientId) == Enablement.Enabled &&
                    server.Health?.Tools is { Count: > 0 })
                .ToArray();
            if (active.Length <= 1) continue;

            var shadowed = ToolSafetyScanner.ShadowedNames(
                active.Select(server => (server.Name, server.Health!.Tools)));
            if (shadowed.Count == 0) continue;

            foreach (var server in active)
            {
                var mine = shadowed
                    .Where(pair => pair.Value.Contains(server.Name, StringComparer.Ordinal))
                    .Select(pair => pair.Key)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (mine.Length == 0) continue;

                var others = mine
                    .SelectMany(name => shadowed[name])
                    .Where(name => !string.Equals(name, server.Name, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                if (!result.TryGetValue(server.Id, out var findings)) result[server.Id] = findings = [];
                findings.Add(new DoctorFinding(
                    "tool-name-collision",
                    DoctorSeverity.Warning,
                    "Another server offers the same tool name",
                    $"Switched on together in this client with {string.Join(", ", others)}, " +
                    $"which also {(others.Length == 1 ? "offers" : "offer")} {string.Join(", ", mine)}.",
                    "The client shows the model one flat list of tools, so a name claimed " +
                    "twice is resolved by the client rather than by you. Switch one of them " +
                    "off in this client, or keep them in separate profiles."));
            }
        }
        return result;
    }

    public static DoctorServerReport Analyze(Server server)
    {
        var findings = new List<DoctorFinding>();

        if (server.Transport != Transport.Stdio)
        {
            findings.Add(new DoctorFinding(
                "remote-check-unavailable",
                DoctorSeverity.Info,
                "Remote health checks are not available yet",
                "Kytto can preserve this remote definition, but the current checker only starts local stdio servers.",
                "Verify the endpoint in its client. Remote HTTP and OAuth diagnostics remain on the roadmap."));
            return new DoctorServerReport(server.Id, server.Name, findings);
        }

        var missingEnvironment = server.Env
            .Where(entry => !entry.HasValue)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingEnvironment.Length > 0)
        {
            findings.Add(new DoctorFinding(
                "missing-environment",
                DoctorSeverity.Error,
                "Required environment values are empty",
                $"Empty keys: {string.Join(", ", missingEnvironment)}.",
                "Edit the server and provide these values. Kytto will keep them out of web UI state and logs."));
        }

        if (server.HasRelativePath)
        {
            findings.Add(new DoctorFinding(
                "relative-path",
                DoctorSeverity.Warning,
                "A relative path may work in only one client",
                "Relative commands and arguments are resolved from the directory that launched the client.",
                "Use an absolute path, or run a health check from the same definition before copying it to more clients.",
                DoctorAction.RunHealthCheck));
        }

        if (PackagePin.UnpinnedLaunch(server) is { } launch)
        {
            findings.Add(UnpinnedPackage(launch, PackagePin.CanPin(server)));
        }

        if (server.Health is not { } health)
        {
            findings.Add(new DoctorFinding(
                "not-checked",
                DoctorSeverity.Info,
                "This server has not been checked",
                "Its executable, MCP handshake and tool contract have not been verified by Kytto.",
                "Run a health check to turn this into a measured result.",
                DoctorAction.RunHealthCheck));
            return new DoctorServerReport(server.Id, server.Name, findings);
        }

        if (health.Status == HealthStatus.NeedsAuthorization)
        {
            findings.Add(new DoctorFinding(
                "needs-authorization",
                DoctorSeverity.Info,
                "Waiting for you to authorize it",
                "The last check reached the server, which asked for an interactive " +
                "sign-in instead of answering the handshake.",
                "Open the authorization page from the server detail, complete the " +
                "sign-in, then check again.",
                DoctorAction.RunHealthCheck));
        }
        else if (health.Status == HealthStatus.Failed)
        {
            findings.Add(ClassifyFailure(health));
        }

        // What the model was handed, as opposed to what the process did. A server can
        // start cleanly, answer `tools/list` correctly and still have shipped an
        // instruction in a description — every other check in this file would call
        // that healthy (§7.10).
        findings.AddRange(ToolSafetyScanner.Scan(health.Tools).Select(Finding));

        if (health.Status == HealthStatus.Passed &&
            server.Command?.Trim() is { Length: > 0 } configured &&
            health.ResolvedCommand is { Length: > 0 } resolved &&
            !string.Equals(configured, resolved, StringComparison.OrdinalIgnoreCase) &&
            !Path.IsPathFullyQualified(configured) &&
            MatchesExecutable(configured, resolved))
        {
            findings.Add(new DoctorFinding(
                "unpinned-command",
                DoctorSeverity.Info,
                "The executable depends on the client PATH",
                $"“{configured}” resolved to “{resolved}” during the successful check.",
                "Preview a change that pins the verified absolute path in every editable client definition.",
                DoctorAction.PinResolvedCommand));
        }

        return new DoctorServerReport(server.Id, server.Name, findings);
    }

    /// <summary>A package fetched by name with no exact version (§7.10).</summary>
    /// <remarks>
    /// <para>
    /// A warning rather than information: every client start can run a release
    /// nobody chose, with the server's environment — tokens included — which is the
    /// supply-chain path a compromised package takes. Not an error, because nothing
    /// is broken today and the server may be exactly what it claims.
    /// </para>
    /// <para>
    /// Read from the configuration alone, so it needs no health check and makes no
    /// request. The version to pin to is not in here: it comes from a lookup the
    /// user asks for, never from the server's self-reported <c>serverInfo</c>,
    /// which names whatever the author typed and is often not the package's
    /// release number at all.
    /// </para>
    /// </remarks>
    private static DoctorFinding UnpinnedPackage(PackageLaunch launch, bool canPin)
    {
        var registry = launch.Ecosystem == PackageEcosystem.Npm ? "npm" : "PyPI";
        var request = launch.RequestedVersion switch
        {
            null or "" => "names no version",
            { Length: > 0 } tag when char.IsLetter(tag[0]) => $"asks for the “{tag}” tag",
            var requested => $"asks for “{requested}”, which matches more than one release",
        };
        var example = !launch.CanPinInPlace
            ? $"--spec {launch.Name}==VERSION"
            : launch.Spelling == PackageSpelling.Requirement
                ? $"{launch.Name}==VERSION"
                : $"{launch.Name}@VERSION";
        return new DoctorFinding(
            "unpinned-package",
            DoctorSeverity.Warning,
            "The package version is not pinned",
            $"“{launch.Argument}” {request}, so {launch.Runner} can start a newer {registry} " +
            $"release of {launch.Name} the next time a client launches it — without anyone " +
            "reviewing it.",
            canPin
                ? $"Check the latest release (Kytto asks {registry} about this package name and " +
                  "nothing else), then preview pinning that exact version in every editable " +
                  "client definition. Pinning fixes this package's own version; its dependencies " +
                  "still resolve within their declared ranges."
                : $"Pin it by hand to an exact release, for example “{example}”. " +
                  (launch.CanPinInPlace
                      ? "Kytto has no editable copy of this definition to change."
                      : "Kytto will not add arguments this command does not already have."),
            canPin ? DoctorAction.PinPackageVersion : null);
    }

    /// <summary>A tool-contract risk, as a Doctor finding.</summary>
    /// <remarks>
    /// There is no automatic action. Kytto does not edit what a server says about
    /// itself and cannot — the text is the server's, not the config's — and switching
    /// a server off over a pattern match is a decision that belongs to the person
    /// reading the evidence (§7.10).
    /// </remarks>
    private static DoctorFinding Finding(ToolRisk risk) => new(
        risk.Kind.Raw(),
        risk.Severity,
        $"“{risk.ToolName}”: {Title(risk.Kind)}",
        $"{risk.Explanation} Found: {risk.Evidence}.",
        "Read this tool's full description and input schema in the server detail " +
        "before using the server, and check where the server came from. Kytto does " +
        "not change what a server says about itself.");

    private static string Title(ToolRiskKind kind) => kind switch
    {
        ToolRiskKind.HiddenCharacters => "the description contains invisible characters",
        ToolRiskKind.InstructionOverride => "the description instructs the model",
        ToolRiskKind.ImpersonatedAuthority => "the description imitates a system message",
        ToolRiskKind.CredentialInterest => "a read-only tool names credential material",
        _ => "the tool name is claimed by another server",
    };

    public static bool MatchesExecutable(string configured, string resolved)
    {
        var configuredName = Path.GetFileName(configured);
        var resolvedName = Path.GetFileName(resolved);
        if (Path.HasExtension(configuredName))
        {
            return string.Equals(configuredName, resolvedName, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(
            configuredName,
            Path.GetFileNameWithoutExtension(resolvedName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static DoctorFinding ClassifyFailure(HealthResult health)
    {
        var evidence = string.Join('\n', new[] { health.Message, health.Stderr }.OfType<string>());

        if (ContainsAny(evidence, "enoent", "not found", "no such file", "cannot find the file"))
        {
            return Failure(
                "executable-not-found",
                "The executable or one of its files was not found",
                "Check the command, use an absolute path, and verify that the client can see the same Node, Python or uv installation as your terminal.");
        }
        if (ContainsAny(evidence, "permission denied", "operation not permitted", "access is denied"))
        {
            return Failure(
                "permission-denied",
                "The operating system denied access",
                "Check file permissions and Windows security controls before trying again.");
        }
        if (ContainsAny(evidence, "timed out", "timeout"))
        {
            return Failure(
                "timeout",
                "The server did not complete the MCP handshake in time",
                "Run the command directly to check for an install prompt, slow package download, blocked network request or server output written to stdout.");
        }
        if (ContainsAny(evidence, "json", "parse", "unexpected token"))
        {
            return Failure(
                "protocol-output",
                "The server returned invalid MCP/JSON output",
                "Run it directly and ensure diagnostic messages go to stderr while stdout contains only MCP JSON-RPC messages.");
        }
        if (ContainsAny(evidence, "environment", "api key", "token"))
        {
            return Failure(
                "environment-failure",
                "The server appears to be missing configuration or credentials",
                "Review its environment keys and credential scopes. Do not paste secret values into logs or support reports.");
        }
        return Failure(
            "health-failed",
            "The server did not pass its health check",
            "Open the server detail for its stderr output, then run the displayed command directly to isolate the startup failure.");
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static DoctorFinding Failure(string code, string title, string remediation) => new(
        code,
        DoctorSeverity.Error,
        title,
        "Kytto could not complete the MCP handshake and tools/list check.",
        remediation,
        DoctorAction.RunHealthCheck);
}
