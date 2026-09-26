using System.Diagnostics;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Model;
using Kytto.Core.Tokenizer;

namespace Kytto.Core.Tests;

/// <summary>
/// Health checks against a stand-in MCP server, so these do not depend on npx
/// being installed or on any real server's behaviour staying still.
/// </summary>
public class HealthCheckerTests
{
    private static string FakeServer =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_mcp_server.py");

    private static string? Python =>
        Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory =>
            {
                try { return Path.Combine(directory.Trim('"'), "python.exe"); }
                catch (ArgumentException) { return null; }
            })
            .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

    private static Server Fake(string mode, IReadOnlyList<EnvEntry>? env = null) => new()
    {
        Id = "fake",
        Name = "fake",
        Transport = Transport.Stdio,
        Command = Python!,
        Args = [FakeServer, mode],
        Env = env ?? [],
        Url = null,
        EnabledIn = [],
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = "0",
        IsBundled = false,
        DefinitionSource = "",
    };

    private static HealthChecker Checker(double seconds = 10) =>
        new(timeout: TimeSpan.FromSeconds(seconds));

    [Fact]
    public void AWorkingServerPassesAndItsToolsAreCounted()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, weight) = Checker().Check(Fake("ok"));

        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(3, health.ToolCount);
        Assert.Equal(
            ["read_file", "write_file", "list_directory"],
            health.Tools.Select(tool => tool.Name));
        Assert.Equal("fake-mcp-server", health.ServerName);
        Assert.Equal("0.1.0", health.ServerVersion);
        Assert.Equal(McpStdioClient.PreferredProtocolVersion, health.ProtocolVersion);
        Assert.Equal(["prompts", "resources", "tools"], health.CapabilityNames);
        Assert.Equal(2, health.PromptCount);
        Assert.Equal(1, health.ResourceCount);
        Assert.NotNull(health.ResolvedCommand);
        Assert.StartsWith("User + system PATH", health.EnvironmentSource, StringComparison.Ordinal);
        Assert.Contains("\"required\"", health.Tools[0].InputSchemaJSON);
        Assert.True(health.Tools[0].Annotations?.ReadOnlyHint);
        Assert.True(health.Tools[0].Annotations?.IdempotentHint);

        // §7.4: measured on what the model would actually be shown.
        Assert.NotNull(weight);
        Assert.Equal(BpeTokenizer.Cl100kMethod, weight.Method);
        Assert.True(weight.IsMeasured);
        // Three tools with schemas land in the high hundreds of characters, which is
        // a few hundred tokens at most.
        Assert.True(weight.Estimate is > 50 and < 2000);
        Assert.True(weight.PercentOfContext > 0);
    }

    [Fact]
    public void EveryToolCarriesItsOwnShareOfTheWeight()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, weight) = Checker().Check(Fake("ok"));

        var counts = health.Tools.Select(tool => tool.TokenCount).ToArray();
        Assert.All(counts, count => Assert.True(count > 0, "every tool should have been measured"));

        // The parts sum to slightly less than the whole: the array's brackets and
        // the commas between entries belong to no single tool.
        Assert.NotNull(weight);
        var sum = counts.Sum(count => count ?? 0);
        Assert.True(sum < weight.Estimate);
        Assert.True(sum > weight.Estimate * 0.9);

        // The tool with the larger schema costs more. This is the ordering the
        // breakdown in server detail is read for, so it is asserted rather than
        // assumed.
        var read = health.Tools.First(tool => tool.Name == "read_file").TokenCount;
        var list = health.Tools.First(tool => tool.Name == "list_directory").TokenCount;
        Assert.NotEqual(read, list);
    }

    [Fact]
    public void AServerWithNoToolsStillPasses()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, weight) = Checker().Check(Fake("empty"));
        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(0, health.ToolCount);
        Assert.NotNull(weight);
        Assert.True(weight.Estimate < 5);
    }

    [Fact]
    public void OptionalInspectorFailureDoesNotFailTheToolsHandshake()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("optional_error"));

        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(3, health.ToolCount);
        Assert.Null(health.PromptCount);
        Assert.Contains(health.InspectionNotes, note => note.Contains("prompts/list", StringComparison.Ordinal));
        Assert.Equal(1, health.ResourceCount);
    }

    [Fact]
    public void PaginationIsCalledOutAndFirstPageIsCounted()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("pagination"));

        Assert.Equal(2, health.PromptCount);
        Assert.Contains(health.InspectionNotes, note => note.Contains("more pages", StringComparison.Ordinal));
    }

    /// <summary>
    /// Servers are not supposed to print to stdout, but enough of them log a banner
    /// there that refusing to tolerate it would fail working servers.
    /// </summary>
    [Fact]
    public void ABannerOnStdoutIsSkippedRatherThanFatal()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("noisy"));
        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(3, health.ToolCount);
    }

    /// <summary>
    /// §6 calls stderr the most useful thing you can show someone whose server is
    /// broken, so it arrives verbatim.
    /// </summary>
    [Fact]
    public void ACrashingServerFailsAndKeepsItsOwnWordsVerbatim()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, weight) = Checker().Check(Fake("crash"));

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Null(weight);
        // Verbatim, both lines, in order — not summarised and not truncated at the
        // front, which is where the useful part of a stack trace lives.
        Assert.NotNull(health.Stderr);
        Assert.Contains("cannot find module 'some-dependency'", health.Stderr, StringComparison.Ordinal);
        Assert.Contains("at Object.<anonymous> (/tmp/server.js:1:1)", health.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerThatRefusesTheRequestReportsItsOwnError()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("error"));
        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("refused", health.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFractionalResponseIdCannotMasqueradeAsTheRequestedIntegerId()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("fractional_id"));

        Assert.Equal(HealthStatus.Passed, health.Status);
        Assert.Equal(3, health.ToolCount);
    }

    [Fact]
    public void AResponseFromTheWrongJsonRpcRevisionIsRejected()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, _) = Checker().Check(Fake("wrong_jsonrpc"));

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("JSON-RPC 2.0", health.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerThatNeverAnswersTimesOutRatherThanHanging()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var stopwatch = Stopwatch.StartNew();
        var (health, _) = Checker(seconds: 2).Check(Fake("slow"));
        stopwatch.Stop();

        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "the timeout did not fire");
        Assert.Contains("did not answer", health.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OAuthConsentIsWaitingForAuthorizationInsteadOfFailed()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var (health, weight) = Checker(seconds: 1).Check(Fake("oauth"));

        Assert.Equal(HealthStatus.NeedsAuthorization, health.Status);
        Assert.Null(weight);
        Assert.Equal("awaitingAuthorization", health.Failure?.Reason);
        Assert.Equal(
            "https://auth.example.com/oauth/authorize?client_id=kytto",
            health.AuthorizationURL);
        Assert.Equal(
            "The server is waiting for you to authorize it in the browser, not broken. " +
            "Complete the sign-in it asked for, then check it again.",
            health.CurrentMessage);
        Assert.Contains("Authentication required. Waiting for authorization...", health.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("Please authorize this client by visiting:", health.Stderr,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Error (401 Unauthorized) https://docs.example.com/oauth-setup")]
    [InlineData("Error: MISSING_API_KEY")]
    public void OAuthLookingFailuresWithoutMarkersStayFailed(string stderr)
    {
        Assert.False(AuthorizationSignal.TryDetect(stderr, out var url));
        Assert.Null(url);
    }

    [Fact]
    public void AuthorizationDetectorAcceptsMarkersWithoutDependingOnAUrl()
    {
        Assert.True(AuthorizationSignal.TryDetect(
            "Authentication required. Waiting for authorization...", out var url));
        Assert.Null(url);
    }

    [Fact]
    public void AuthorizationUrlExtractionUsesMarkerLinesAndRejectsUnsafeCandidates()
    {
        Assert.True(AuthorizationSignal.TryDetect(
            "Please authorize at https://login.example.com/session/abc).", out var markerURL));
        Assert.Equal("https://login.example.com/session/abc", markerURL);

        Assert.True(AuthorizationSignal.TryDetect(
            "Waiting for authorization\nOpen http://login.example.com/oauth", out var httpURL));
        Assert.Null(httpURL);

        Assert.False(AuthorizationSignal.TryDetect(
            "See https://login.example.com/oauth/authorize for documentation", out var noMarkerURL));
        Assert.Null(noMarkerURL);
    }

    /// <summary>
    /// The whole reason spawns go into a job object: <c>npx</c> spawning
    /// <c>node</c> is the real-world shape, and killing only the direct child
    /// leaves the grandchild running forever (§6).
    /// </summary>
    [Fact]
    public void KillingAServerTakesItsChildrenWithIt()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");

        var marker = "kytto-test-" + Guid.NewGuid().ToString("N")[..8];
        var (health, _) = Checker(seconds: 2)
            .Check(Fake("children", [new EnvEntry("KYTTO_TEST_MARKER", marker)]));

        Assert.Equal(HealthStatus.Failed, health.Status);

        // Give the job object a moment to tear the tree down.
        Thread.Sleep(1500);
        Assert.False(AnyProcessMentions(marker), "a grandchild outlived the check");
    }

    [Fact]
    public void ARemoteServerIsUnsupportedRatherThanFailed()
    {
        var server = new Server
        {
            Id = "docs",
            Name = "docs",
            Transport = Transport.Http,
            Command = null,
            Args = [],
            Env = [],
            Url = "https://example.com/mcp",
            EnabledIn = [],
            Origin = new Origin.ConfigFile(ClientId.Cursor),
            Fingerprint = "0",
            IsBundled = false,
            DefinitionSource = "",
        };

        var (health, weight) = Checker().Check(server);
        Assert.Equal(HealthStatus.Unsupported, health.Status);
        Assert.Null(weight);
        Assert.Contains("remote", health.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACommandThatDoesNotExistSaysWhereKyttoLooked()
    {
        var missing = new Server
        {
            Id = "missing",
            Name = "missing",
            Transport = Transport.Stdio,
            Command = "kytto-no-such-command",
            Args = [],
            Env = [],
            Url = null,
            EnabledIn = [],
            Origin = new Origin.ConfigFile(ClientId.Cursor),
            Fingerprint = "0",
            IsBundled = false,
            DefinitionSource = "",
        };

        var (health, _) = Checker().Check(missing);
        Assert.Equal(HealthStatus.Failed, health.Status);
        Assert.Contains("was not found", health.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("searched", health.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows-specific, and the one that decides whether health checks work at
    /// all in practice: <c>npx</c> is a batch shim, not a program, so spawning it
    /// directly fails. Resolution has to find it and hand it to an interpreter.
    /// </summary>
    [Fact]
    public void ABatchShimOnPathIsRunnable()
    {
        var shim = Path.Combine(Path.GetTempPath(), $"kytto-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shim);
        try
        {
            File.WriteAllText(Path.Combine(shim, "kyttoecho.cmd"), "@echo off\r\necho hello-from-shim\r\n");

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = shim,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe",
            };

            using var process = new ManagedProcess("kyttoecho", [], environment);
            var line = process.ReadLine(DateTimeOffset.UtcNow.AddSeconds(10));
            Assert.Equal("hello-from-shim", line?.Trim());
        }
        finally
        {
            try { Directory.Delete(shim, recursive: true); } catch (IOException) { }
        }
    }

    private static bool AnyProcessMentions(string marker)
    {
        // The grandchild is `python -c "...  # marker"`, so the marker is in its
        // command line rather than its name.
        using var search = new System.Management.ManagementObjectSearcher(
            $"SELECT CommandLine FROM Win32_Process WHERE Name = 'python.exe'");
        foreach (var item in search.Get())
        {
            if (item["CommandLine"] is string line &&
                line.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
