using System.IO;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

namespace Kytto.App.Tests;

/// <summary>
/// The ways back when something Gateway mode depends on goes away, through the model the
/// IPC commands call: Doctor names the cause, a moved Kytto is repaired in place, and
/// Restore Direct mode for all clears every route before Kytto is removed.
/// </summary>
public sealed class GatewayRecoveryIntegrationTests : IDisposable
{
    private const string RouteID = "a6ed52d3-be69-4a4f-a215-ac17c685f520";

    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-gateway-recovery-tests", Guid.NewGuid().ToString("N"));

    private readonly InMemorySecretStore _gatewaySecrets = new();

    private string Config => Path.Combine(_home, ".cursor", "mcp.json");

    private static string Helper(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "kytto-mcp-proxy.exe");
        File.WriteAllBytes(path, []);
        return path;
    }

    private AppModel Model(string helper) => new(
        new KyttoPaths(Path.Combine(_home, "KyttoData")),
        _home,
        new AllInstalledApps(),
        new InMemorySecretStore(),
        _gatewaySecrets,
        helper,
        watchConfigs: false);

    private string WriteCursor()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        const string Text = """
            {
              "mcpServers": {
                "github": { "command": "npx", "args": ["-y", "server-github"], "env": { "TOKEN": "t" } },
                "other": { "command": "other" }
              }
            }
            """;
        File.WriteAllText(Config, Text);
        return Text;
    }

    [Fact]
    public void AMovedKyttoIsReportedByDoctorAndRepairedInPlace()
    {
        WriteCursor();
        var oldHelper = Helper(Path.Combine(_home, "Downloads", "Kytto"));
        using (var first = Model(oldHelper))
        {
            first.EnableGateway("github", ClientId.Cursor, RouteID);
            // A route Kytto just wrote is what the restart is about.
            Assert.Equal(["github"], first.ServersAwaitingRestart(ClientId.Cursor));
            Assert.DoesNotContain(first.DoctorReports(), report =>
                report.Findings.Any(finding => finding.Code.StartsWith("gateway-", StringComparison.Ordinal)));
        }

        Directory.Delete(Path.GetDirectoryName(oldHelper)!, recursive: true);
        var newHelper = Helper(Path.Combine(_home, "Programs", "Kytto"));
        using var moved = Model(newHelper);

        var finding = Assert.Single(
            Assert.Single(moved.DoctorReports(), report => report.ServerID == "github").Findings,
            item => item.Code == "gateway-helper-missing");
        Assert.Equal(DoctorAction.RepairGatewayRoute, finding.Action);
        Assert.Equal(RouteID, finding.RouteID);

        var result = moved.RepairGatewayRoute(RouteID);

        Assert.Equal(newHelper, result.Route.GatewayCommand);
        Assert.Contains(newHelper.Replace(@"\", @"\\", StringComparison.Ordinal), File.ReadAllText(Config), StringComparison.Ordinal);
        Assert.DoesNotContain(moved.DoctorReports(), report =>
            report.Findings.Any(item => item.Code == "gateway-helper-missing"));
        Assert.True(moved.PendingRestarts.GetValueOrDefault(ClientId.Cursor) > 0);
    }

    [Fact]
    public void RestoreAllPutsEveryDefinitionBackAndNamesWhatCouldNotBeRecovered()
    {
        var original = WriteCursor();
        using var model = Model(Helper(Path.Combine(_home, "Kytto")));
        model.EnableGateway("github", ClientId.Cursor, RouteID);
        // Credential Manager lost the original and the value.
        _gatewaySecrets.Delete(Kytto.Core.Gateway.GatewayRoute.DirectDefinitionIdentifier(RouteID));
        _gatewaySecrets.Delete(Kytto.Core.Gateway.GatewayRoute.EnvironmentIdentifier(RouteID, "TOKEN"));

        var report = model.RestoreAllDirect();

        var restored = Assert.Single(report.Restored);
        Assert.Equal(RouteID, restored.Id);
        Assert.Empty(report.Failures);
        var missing = Assert.Single(report.MissingEnvironment);
        Assert.Equal(["TOKEN"], missing.Keys);
        Assert.Empty(model.GatewayRoutes());
        Assert.NotEqual(original, File.ReadAllText(Config));
        Assert.Contains("\"command\": \"npx\"", File.ReadAllText(Config), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException) { }
    }

    private sealed class AllInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => true;
        public bool PackageExists(string familyName) => true;
        public bool ExecutableExists(string name, string home) => true;
    }
}
