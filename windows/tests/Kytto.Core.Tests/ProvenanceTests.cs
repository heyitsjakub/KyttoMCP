using System.Net;
using System.Text;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

public sealed class ProvenanceTests
{
    [Fact]
    public void InfersNpmPythonLocalAndRemoteSourcesWithoutLaunchingAnything()
    {
        var npm = ServerProvenance.Infer(
            Transport.Stdio,
            "npx",
            ["-y", "@modelcontextprotocol/server-github@1.2.3"],
            null);
        var python = ServerProvenance.Infer(
            Transport.Stdio,
            "uvx",
            ["--from", "mcp-server-filesystem@2.0.0", "mcp-server-filesystem"],
            null);
        var local = ServerProvenance.Infer(
            Transport.Stdio,
            "python",
            ["./server.py"],
            null);
        var executable = ServerProvenance.Infer(
            Transport.Stdio,
            @"C:\Tools\server.exe",
            [],
            null);
        var remote = ServerProvenance.Infer(
            Transport.Http,
            null,
            [],
            "https://example.com/mcp");
        var npmScript = ServerProvenance.Infer(
            Transport.Stdio,
            "npm",
            ["run", "serve"],
            null);

        Assert.Equal(ProvenanceSourceKinds.Npm, npm.SourceKind);
        Assert.Equal("@modelcontextprotocol/server-github", npm.PackageName);
        Assert.Equal("1.2.3", npm.InstalledVersion);
        Assert.Equal(ProvenanceSourceKinds.Python, python.SourceKind);
        Assert.Equal("mcp-server-filesystem", python.PackageName);
        Assert.Equal("2.0.0", python.InstalledVersion);
        Assert.Equal(ProvenanceSourceKinds.LocalPackage, local.SourceKind);
        Assert.Equal(ProvenanceSourceKinds.LocalExecutable, executable.SourceKind);
        Assert.Equal(ProvenanceSourceKinds.Remote, remote.SourceKind);
        Assert.Equal("https://example.com/mcp", remote.SourceURL);
        Assert.Equal("unchecked", npm.MaintenanceState);
        Assert.Equal(ProvenanceSourceKinds.Npm, npmScript.SourceKind);
        Assert.Null(npmScript.PackageName);
        Assert.Equal("unknownSource", npmScript.MaintenanceState);
    }

    [Fact]
    public async Task LatestCheckSendsOnlyThePackageNameToTheFirstPartyRegistry()
    {
        Uri? requested = null;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"dist-tags\":{\"latest\":\"2.0.0\"}}", Encoding.UTF8, "application/json"),
                RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
            };
        }));
        using var checker = new ProvenanceChecker(client);
        var provenance = new ServerProvenance(
            ProvenanceSourceKinds.Npm,
            "@scope/package",
            null,
            "1.0.0",
            null,
            null,
            ProvenanceConfidences.Inferred,
            "unchecked");

        var result = await checker.CheckLatestAsync(
            provenance,
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("https://registry.npmjs.org/%40scope%2Fpackage", requested?.OriginalString);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.Equal("unchecked", result.MaintenanceState);
    }

    [Fact]
    public async Task LatestCheckRejectsMalformedRegistryShape()
    {
        using var client = new HttpClient(new DelegateHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"dist-tags\":\"not-an-object\"}", Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
        }));
        using var checker = new ProvenanceChecker(client);
        var provenance = ServerProvenance.Infer(
            Transport.Stdio,
            "npx",
            ["demo"],
            null);

        await Assert.ThrowsAsync<ProvenanceCheckException>(() => checker.CheckLatestAsync(
            provenance,
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void HealthAndLatestVersionProduceTheDocumentedMaintenanceStates()
    {
        var at = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var provenance = new ServerProvenance(
            ProvenanceSourceKinds.Npm,
            "demo",
            null,
            "1.0.0",
            null,
            null,
            ProvenanceConfidences.Inferred,
            "unchecked").WithLatest("2.0.0", at);
        var healthy = new HealthResult(
            HealthStatus.Passed, at, 0, [], null, null, 0, null, null);

        Assert.Equal("staleButResponsive", provenance.WithHealth(healthy).MaintenanceState);
        Assert.Equal("unhealthy", provenance.WithHealth(healthy with { Status = HealthStatus.Failed }).MaintenanceState);
        Assert.Equal("healthy", (provenance with { LatestVersion = null }).WithHealth(healthy).MaintenanceState);
        Assert.Equal("unchecked", provenance.WithHealth(
            healthy with { Status = HealthStatus.NeedsAuthorization }).MaintenanceState);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
