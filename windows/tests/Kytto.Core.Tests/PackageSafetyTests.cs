using System.Net;
using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

public class PackageLookAlikeTests
{
    private static Server StdioServer(string name, string command, string[] args) => new()
    {
        Id = Server.Identity(name),
        Name = name,
        Transport = Transport.Stdio,
        Command = command,
        Args = args,
        Env = [],
        Url = null,
        EnabledIn = new Dictionary<ClientKey, Enablement> { [ClientId.Cursor] = Enablement.Enabled },
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = Server.MakeFingerprint(command, args, null),
        IsBundled = false,
        DefinitionSource = "{}",
    };

    [Theory]
    [InlineData("mcp-server-github", "@modelcontextprotocol/server-github")]
    [InlineData("@modelcontextprotocoI/server-filesystem", "@modelcontextprotocol/server-filesystem")]
    [InlineData("@model-context-protocol/server-memory", "@modelcontextprotocol/server-memory")]
    [InlineData("@playwrigth/mcp", "@playwright/mcp")]
    [InlineData("@modelcontextprotocol/server-filesytem", "@modelcontextprotocol/server-filesystem")]
    [InlineData("github-mcp-server", "@modelcontextprotocol/server-github")]
    public void OfficialLookingNpmNamesAreCaught(string name, string official) =>
        Assert.Equal(official, PackageSafety.LookAlike(name, PackageEcosystem.Npm));

    [Theory]
    [InlineData("@modelcontextprotocol/server-github")]
    [InlineData("@playwright/mcp")]
    [InlineData("firecrawl-mcp")]
    [InlineData("@upstash/context7-mcp")]
    [InlineData("some-unrelated-tool")]
    [InlineData("@acme/internal-mcp")]
    [InlineData("@sentry/mcp-server")]
    public void TheOfficialPackagesAndUnrelatedOnesAreLeftAlone(string name) =>
        Assert.Null(PackageSafety.LookAlike(name, PackageEcosystem.Npm));

    [Fact]
    public void PyPiNamesOneLetterOffAnOfficialOneAreCaughtAndTheOfficialOnesAreNot()
    {
        Assert.Equal("mcp-server-fetch", PackageSafety.LookAlike("mcp-server-fetsh", PackageEcosystem.Python));
        Assert.Null(PackageSafety.LookAlike("mcp_server.fetch", PackageEcosystem.Python));
        Assert.Null(PackageSafety.LookAlike("mcp-server-time", PackageEcosystem.Python));
        Assert.Null(PackageSafety.LookAlike("fastapi", PackageEcosystem.Python));
    }

    [Fact]
    public void ALookAlikeBecomesADoctorFindingOfferingTheCheck()
    {
        var server = StdioServer("github", "npx", ["-y", "mcp-server-github"]);
        var report = McpDoctor.Reports([server], [ClientId.Cursor]).First();
        var finding = report.Findings.First(candidate => candidate.Code == "look-alike-package");
        Assert.Equal(DoctorSeverity.Warning, finding.Severity);
        Assert.Equal(DoctorAction.CheckPackageSafety, finding.Action);
    }

    [Fact]
    public void FindingsFollowTheReportAndAReportForAnotherPackageIsIgnored()
    {
        var server = StdioServer("github", "npx", ["-y", "mcp-server-github"]);
        var report = new PackageSafetyReport(
            PackageEcosystem.Npm, "mcp-server-github", null,
            [new PackageSafetyReport.Advisory("MAL-2026-5479", null)],
            new Dictionary<string, string> { ["postinstall"] = "node collect.js" },
            DateTimeOffset.UtcNow);
        var findings = PackageSafety.Findings(server, report);
        Assert.Equal(["look-alike-package", "known-malware", "install-script"], findings.Select(item => item.Code));
        Assert.Null(findings[0].Action);
        Assert.Equal(DoctorSeverity.Error, findings.First(item => item.Code == "known-malware").Severity);

        var edited = StdioServer("filesystem", "npx", ["-y", "@modelcontextprotocol/server-filesystem"]);
        Assert.Empty(PackageSafety.Findings(edited, report));
    }

    // MARK: - Archived reference servers (offline)

    [Theory]
    [InlineData("npx", new[] { "-y", "@modelcontextprotocol/server-github" })]
    [InlineData("npx", new[] { "-y", "@modelcontextprotocol/server-puppeteer@2025.5.12" })]
    [InlineData("npx", new[] { "-y", "@modelcontextprotocol/server-slack" })]
    [InlineData("uvx", new[] { "mcp-server-sqlite", "--db-path", "x.db" })]
    [InlineData("uvx", new[] { "MCP_Server.Sentry" })]
    public void ArchivedReferenceServersAreFlaggedOffline(string command, string[] args)
    {
        var finding = PackageSafety.Findings(StdioServer("s", command, args), null).First();
        Assert.Equal("archived-package", finding.Code);
        Assert.Equal(DoctorSeverity.Warning, finding.Severity);
        Assert.Equal(DoctorAction.CheckPackageSafety, finding.Action);
    }

    [Fact]
    public void AnArchivedServerFindingNamesNpmDeprecationOnlyForNpm()
    {
        var npm = PackageSafety.Findings(StdioServer("s", "npx", ["-y", "@modelcontextprotocol/server-postgres"]), null).Single();
        Assert.Contains("npm marks it deprecated", npm.Detail, StringComparison.Ordinal);
        var pypi = PackageSafety.Findings(StdioServer("s", "uvx", ["mcp-server-sqlite"]), null).Single();
        Assert.DoesNotContain("npm", pypi.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@modelcontextprotocol/server-filesystem")]
    [InlineData("@modelcontextprotocol/server-memory")]
    [InlineData("@modelcontextprotocol/server-everything")]
    [InlineData("@modelcontextprotocol/server-sequential-thinking")]
    public void MaintainedReferenceServersAreLeftAlone(string name) =>
        Assert.Empty(PackageSafety.Findings(StdioServer("s", "npx", ["-y", name]), null));

    [Fact]
    public void ALookAlikeOfAnArchivedServerIsNotToldToSwitchToIt()
    {
        var server = StdioServer("github", "npx", ["-y", "mcp-server-github"]);
        var finding = PackageSafety.Findings(server, null).First(item => item.Code == "look-alike-package");
        Assert.DoesNotContain("change the command", finding.Remediation, StringComparison.Ordinal);
        Assert.Contains("no longer maintained", finding.Remediation, StringComparison.Ordinal);

        var live = StdioServer("fs", "npx", ["-y", "@modelcontextprotocol/server-filesytem"]);
        var liveFinding = PackageSafety.Findings(live, null).First(item => item.Code == "look-alike-package");
        Assert.Contains("change the command", liveFinding.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckedDeprecatedPackageGetsAFindingAndAnArchivedOneIsNotReportedTwice()
    {
        var server = StdioServer("old", "npx", ["-y", "some-old-mcp"]);
        var report = new PackageSafetyReport(
            PackageEcosystem.Npm, "some-old-mcp", null, [], new Dictionary<string, string>(),
            DateTimeOffset.UtcNow, "Use new-mcp instead");
        var finding = Assert.Single(PackageSafety.Findings(server, report));
        Assert.Equal("deprecated-package", finding.Code);
        Assert.Contains("Use new-mcp instead", finding.Detail, StringComparison.Ordinal);

        var archived = StdioServer("github", "npx", ["-y", "@modelcontextprotocol/server-github"]);
        var archivedReport = new PackageSafetyReport(
            PackageEcosystem.Npm, "@modelcontextprotocol/server-github", null, [], new Dictionary<string, string>(),
            DateTimeOffset.UtcNow, "Package no longer supported.");
        var only = Assert.Single(PackageSafety.Findings(archived, archivedReport));
        Assert.Equal("archived-package", only.Code);
        Assert.Null(only.Action);
    }

    [Fact]
    public void ABundledOrRemoteServerHasNoPackageToCheck()
    {
        var bundled = new Server
        {
            Id = "ext", Name = "ext", Transport = Transport.Stdio, Command = "npx",
            Args = ["-y", "mcp-server-github"], Env = [], Url = null,
            EnabledIn = [], Origin = new Origin.ConfigFile(ClientId.Cursor),
            Fingerprint = "f", IsBundled = true, DefinitionSource = "{}",
        };
        Assert.Null(PackageSafety.Launch(bundled));
        Assert.NotNull(PackageSafety.Launch(StdioServer("x", "npx", ["-y", "mcp-server-github"])));
    }
}

public class PackageSafetyResultTests
{
    [Fact]
    public void AnUnpinnedQueryKeepsOnlyMalwareAndAPinnedOneKeepsEveryAdvisory()
    {
        var answer = Encoding.UTF8.GetBytes(
            """{"vulns":[{"id":"MAL-2026-5479","summary":"Malicious code in mcp-server-github"},{"id":"GHSA-xxxx-yyyy-zzzz","summary":"Path traversal"}]}""");
        Assert.Equal(["MAL-2026-5479"], PackageSafety.AdvisoriesFromOsv(answer, versionQueried: false).Select(item => item.Id));
        Assert.Equal(2, PackageSafety.AdvisoriesFromOsv(answer, versionQueried: true).Count);
        Assert.Empty(PackageSafety.AdvisoriesFromOsv("{}"u8.ToArray(), versionQueried: true));
        Assert.Throws<InvalidDataException>(() => PackageSafety.AdvisoriesFromOsv("[]"u8.ToArray(), true));
    }

    [Fact]
    public void TheQueryNamesThePackageItsRegistryAndAPinnedVersionOnly()
    {
        var pinned = PackageLaunch.Parse("npx", ["-y", "@scope/pkg@1.2.3"])!;
        using var pinnedQuery = JsonDocument.Parse(PackageSafety.OsvQuery(pinned));
        Assert.Equal("1.2.3", pinnedQuery.RootElement.GetProperty("version").GetString());
        Assert.Equal("@scope/pkg", pinnedQuery.RootElement.GetProperty("package").GetProperty("name").GetString());
        Assert.Equal("npm", pinnedQuery.RootElement.GetProperty("package").GetProperty("ecosystem").GetString());

        var loose = PackageLaunch.Parse("uvx", ["mcp-server-fetch"])!;
        using var looseQuery = JsonDocument.Parse(PackageSafety.OsvQuery(loose));
        Assert.False(looseQuery.RootElement.TryGetProperty("version", out _));
        Assert.Equal("PyPI", looseQuery.RootElement.GetProperty("package").GetProperty("ecosystem").GetString());
    }

    [Fact]
    public void InstallTimeScriptsAreReadOtherScriptsAreNot()
    {
        var document = Encoding.UTF8.GetBytes(
            """{"name":"x","version":"1.0.0","scripts":{"postinstall":"node collect.js","test":"jest","build":"tsc"}}""");
        var scripts = PackageSafety.InstallScriptsFromNpmVersion(document);
        Assert.Equal("node collect.js", Assert.Single(scripts).Value);
        Assert.Equal("postinstall", scripts.Keys.Single());
    }

    [Fact]
    public void TheNpmDeprecationMessageIsReadAndFalseOrAMissingFieldIsNotDeprecation()
    {
        Assert.Equal("Package no longer supported.", PackageSafety.DeprecationFromNpmVersion(
            """{"name":"x","version":"1.0.0","deprecated":"  Package no longer supported.\n"}"""u8.ToArray()));
        Assert.Null(PackageSafety.DeprecationFromNpmVersion("""{"deprecated":false}"""u8.ToArray()));
        Assert.Null(PackageSafety.DeprecationFromNpmVersion("""{"name":"x"}"""u8.ToArray()));
        Assert.Equal("deprecated", PackageSafety.DeprecationFromNpmVersion("""{"deprecated":"  "}"""u8.ToArray()));
        Assert.Equal(300, PackageSafety.DeprecationFromNpmVersion(
            Encoding.UTF8.GetBytes($$"""{"deprecated":"{{new string('a', 500)}}"}"""))!.Length);
        Assert.Throws<InvalidDataException>(() => PackageSafety.DeprecationFromNpmVersion("[]"u8.ToArray()));
    }

    [Theory]
    [InlineData("@modelcontextprotocol/server-github", true, true)]
    [InlineData("left-pad", true, true)]
    [InlineData("@scope/a/b", true, false)]
    [InlineData("../etc", true, false)]
    [InlineData("a\\b", true, false)]
    [InlineData("@scope/pkg", false, false)]
    [InlineData("mcp-server-fetch", false, true)]
    public void OnlyNamesThatAddressOnePackageAreSent(string name, bool scoped, bool safe) =>
        Assert.Equal(safe, PackageSafetyLookup.IsSafePackage(name, scoped));

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken)));
            var response = respond(request);
            response.RequestMessage = request;
            return response;
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ACheckSendsOnlyThePackageItsRegistryAndAPinnedVersion()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.Host == "api.osv.dev"
            ? Json("""{"vulns":[{"id":"GHSA-aaaa-bbbb-cccc"}]}""")
            : Json("""{"scripts":{"postinstall":"node x.js"},"deprecated":"Use @scope/next"}"""));
        using var lookup = new PackageSafetyLookup(new HttpClient(handler), () => DateTimeOffset.FromUnixTimeSeconds(1_000));
        var launch = PackageLaunch.Parse("npx", ["-y", "@scope/pkg@1.2.3"])!;

        var report = await lookup.CheckAsync(launch);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://api.osv.dev/v1/query", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("""{"package":{"ecosystem":"npm","name":"@scope/pkg"},"version":"1.2.3"}""", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("https://registry.npmjs.org/%40scope%2Fpkg/1.2.3", handler.Requests[1].Uri.OriginalString);
        Assert.Equal("1.2.3", report.Version);
        Assert.Equal(["GHSA-aaaa-bbbb-cccc"], report.Advisories.Select(item => item.Id));
        Assert.Equal("node x.js", report.InstallScripts["postinstall"]);
        Assert.Equal("Use @scope/next", report.Deprecation);
        Assert.Equal(1_000, report.CheckedAt.ToUnixTimeSeconds());
        Assert.True(report.Describes(launch));
    }

    [Fact]
    public async Task APyPiPackageIsAskedAboutOnlyAtOsv()
    {
        var handler = new RecordingHandler(_ => Json("""{"vulns":[{"id":"MAL-2026-1"}]}"""));
        using var lookup = new PackageSafetyLookup(new HttpClient(handler));

        var report = await lookup.CheckAsync(PackageLaunch.Parse("uvx", ["mcp-server-fetsh"])!);

        Assert.Single(handler.Requests);
        Assert.Contains("MAL-2026-1", report.Advisories.Select(item => item.Id));
        Assert.Empty(report.InstallScripts);
        Assert.Null(report.Deprecation);
    }

    [Fact]
    public async Task AnAnswerThatIsNotOkOrIsTooLargeConcludesNothing()
    {
        var failing = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var lookup = new PackageSafetyLookup(new HttpClient(failing));
        var error = await Assert.ThrowsAsync<PackageSafetyException>(() =>
            lookup.CheckAsync(PackageLaunch.Parse("uvx", ["mcp-server-fetch"])!));
        Assert.Contains("osv.dev", error.Message, StringComparison.Ordinal);

        var garbled = new RecordingHandler(_ => Json("[]"));
        using var second = new PackageSafetyLookup(new HttpClient(garbled));
        await Assert.ThrowsAsync<PackageSafetyException>(() =>
            second.CheckAsync(PackageLaunch.Parse("uvx", ["mcp-server-fetch"])!));
    }
}
