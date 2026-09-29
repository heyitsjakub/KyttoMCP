using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;

namespace Kytto.Core.Tests;

/// <summary>Reading the package a runner starts (§7.10).</summary>
public sealed class PackageLaunchTests
{
    private static PackageLaunch? Launch(string command, params string[] args) =>
        PackageLaunch.Parse(command, args);

    [Fact]
    public void NpxWithNoVersionATagOrARangeIsUnpinned()
    {
        var bare = Launch("npx", "-y", "@modelcontextprotocol/server-github");
        Assert.NotNull(bare);
        Assert.Equal(PackageEcosystem.Npm, bare.Ecosystem);
        Assert.Equal("@modelcontextprotocol/server-github", bare.Name);
        Assert.Null(bare.RequestedVersion);
        Assert.False(bare.IsPinned);
        Assert.Equal(1, bare.ArgumentIndex);
        Assert.Equal("npx", bare.Runner);

        foreach (var spec in new[] { "pkg@latest", "pkg@next", "pkg@^1.2.0", "pkg@~1", "pkg@1.x", "pkg@1.2", "pkg@>=1.0.0", "pkg@*", "pkg@" })
        {
            var parsed = Launch("npx", "--yes", spec);
            Assert.True(parsed is not null, spec);
            Assert.Equal("pkg", parsed.Name);
            Assert.False(parsed.IsPinned, $"{spec} should be unpinned");
        }
    }

    [Fact]
    public void AnExactSemverReleaseIsPinnedScopedOrNot()
    {
        foreach (var spec in new[] { "pkg@1.2.3", "@scope/pkg@1.2.3", "pkg@1.0.0-beta.2", "pkg@1.2.3+build.5", "pkg@v1.2.3" })
        {
            Assert.True(Launch("npx", "-y", spec)?.IsPinned == true, $"{spec} should be pinned");
        }
        var scoped = Launch("npx", "@scope/pkg@2.0.0");
        Assert.NotNull(scoped);
        Assert.Equal("@scope/pkg", scoped.Name);
        Assert.Equal("2.0.0", scoped.RequestedVersion);
    }

    [Fact]
    public void FlagsBeforeThePackageAreSkippedIncludingOnesThatTakeAValue()
    {
        var parsed = Launch("npx", "--registry", "https://registry.example", "-q", "--prefer-online", "pkg", "--port", "3000");
        Assert.NotNull(parsed);
        Assert.Equal("pkg", parsed.Name);
        Assert.Equal(4, parsed.ArgumentIndex);

        var afterSeparator = Launch("npx", "-y", "--", "pkg@latest", "serve");
        Assert.NotNull(afterSeparator);
        Assert.Equal(2, afterSeparator.ArgumentIndex);
        Assert.Equal("latest", afterSeparator.RequestedVersion);
    }

    [Fact]
    public void PackageFlagNamesThePackageAndThePositionalIsOnlyTheBinary()
    {
        var separate = Launch("npx", "-y", "-p", "@scope/tools@latest", "tools-mcp");
        Assert.NotNull(separate);
        Assert.Equal("@scope/tools", separate.Name);
        Assert.Equal(2, separate.ArgumentIndex);
        Assert.Equal("", separate.ArgumentPrefix);

        var joined = Launch("npx", "--package=@scope/tools", "tools-mcp");
        Assert.NotNull(joined);
        Assert.Equal(0, joined.ArgumentIndex);
        Assert.Equal("--package=", joined.ArgumentPrefix);
        Assert.Equal("--package=@scope/tools@3.1.4", joined.PinnedArgument("3.1.4"));

        // Two packages: which one is "the server" would be a guess.
        Assert.Null(Launch("npx", "-p", "a", "-p", "b", "a-bin"));
    }

    [Fact]
    public void TheOtherNpmStyleRunnersAreReadTheSameWay()
    {
        Assert.Equal("bunx", Launch("bunx", "pkg")?.Runner);
        Assert.Equal(1, Launch("bun", "x", "pkg")?.ArgumentIndex);
        Assert.Equal(1, Launch("pnpm", "dlx", "pkg@latest")?.ArgumentIndex);
        Assert.Equal(2, Launch("yarn", "dlx", "-q", "pkg")?.ArgumentIndex);
        Assert.Equal(2, Launch("npm", "exec", "--", "pkg")?.ArgumentIndex);
        Assert.Equal("pkg", Launch("/opt/homebrew/bin/npx", "-y", "pkg")?.Name);

        // Subcommands that do not fetch a package by name.
        Assert.Null(Launch("pnpm", "exec", "pkg"));
        Assert.Null(Launch("npm", "run", "start"));
        Assert.Null(Launch("bun", "server.ts"));
        Assert.Null(Launch("node", "server.js"));
    }

    /// <summary>
    /// <c>npx</c> is not a program on Windows (see CLAUDE.md), so configs name the
    /// batch file, the executable, or the whole path — in any case.
    /// </summary>
    [Fact]
    public void WindowsSpellingsOfTheRunnerAreTheSameRunner()
    {
        foreach (var command in new[]
        {
            "npx.cmd", "NPX.CMD", "npx.exe", "Npx",
            @"C:\Program Files\nodejs\npx.cmd",
            "\"C:\\Program Files\\nodejs\\npx.cmd\"",
            @"C:\Users\example\AppData\Roaming\npm\npx.ps1",
            "C:/Program Files/nodejs/npx.cmd",
        })
        {
            var parsed = Launch(command, "-y", "pkg@latest");
            Assert.True(parsed is not null, command);
            Assert.Equal("pkg", parsed.Name);
            Assert.Equal(1, parsed.ArgumentIndex);
            Assert.False(parsed.IsPinned);
        }

        Assert.Equal(1, Launch("pnpm.cmd", "dlx", "pkg")?.ArgumentIndex);
        Assert.Equal("bunx", Launch(@"C:\Users\example\.bun\bin\bunx.exe", "pkg")?.Runner);

        var uvx = Launch(@"C:\Users\example\.local\bin\uvx.exe", "mcp-server-time");
        Assert.NotNull(uvx);
        Assert.Equal(PackageEcosystem.Python, uvx.Ecosystem);
        Assert.Equal("mcp-server-time@0.6.2", uvx.PinnedArgument("0.6.2"));
        Assert.Equal("uv tool run", Launch("UV.EXE", "tool", "run", "pkg")?.Runner);
    }

    /// <summary>
    /// A client that spawns without a shell cannot start <c>npx.cmd</c> directly,
    /// so a common Windows config wraps it — and the index still has to point
    /// into the real <c>args</c>, or the splice lands on the wrong element.
    /// </summary>
    [Fact]
    public void ARunnerWrappedInCmdIsReadAndIndexedFromTheRealArguments()
    {
        var wrapped = Launch("cmd", "/c", "npx", "-y", "@modelcontextprotocol/server-github");
        Assert.NotNull(wrapped);
        Assert.Equal("npx", wrapped.Runner);
        Assert.Equal("@modelcontextprotocol/server-github", wrapped.Name);
        Assert.Equal(3, wrapped.ArgumentIndex);
        Assert.Equal("@modelcontextprotocol/server-github", wrapped.Argument);

        var switches = Launch(@"C:\Windows\System32\cmd.exe", "/d", "/s", "/C", "uvx.exe", "--python", "3.12", "mcp-server-fetch");
        Assert.NotNull(switches);
        Assert.Equal("mcp-server-fetch", switches.Name);
        Assert.Equal(6, switches.ArgumentIndex);

        Assert.Equal(3, Launch("cmd", "/c", "pnpm", "dlx", "pkg")?.ArgumentIndex);

        // One quoted command line is not a splice Kytto can make safely.
        Assert.Null(Launch("cmd", "/c", "npx -y pkg"));
        // Not a runner, nothing to run, or not a command at all.
        Assert.Null(Launch("cmd", "/c", "node", "server.js"));
        Assert.Null(Launch("cmd", "/c"));
        Assert.Null(Launch("cmd", "/k", "npx", "pkg"));
        Assert.Null(Launch("cmd", "npx", "pkg"));
        Assert.Null(Launch("cmd", "/c", "cmd", "/c", "npx", "pkg"));
    }

    [Fact]
    public void PathsUrlsGitSpecsAliasesAndGitHubShorthandAreNotPackages()
    {
        foreach (var spec in new[]
        {
            "./local-server", "/abs/server", "~/server", "file:../server",
            "github:user/repo", "user/repo", "git+https://example.com/r.git",
            "https://example.com/pkg.tgz", "pkg.tgz", "alias@npm:real@1.0.0",
            @".\local-server", @"C:\Users\example\server", @"\\share\server", "server.js",
        })
        {
            Assert.True(Launch("npx", "-y", spec) is null, $"{spec} should be out of scope");
        }
        Assert.Null(Launch("npx", "-y"));
        Assert.Null(Launch("npx", "-c", "echo hi"));
    }

    [Fact]
    public void UvxBareNamesAndLatestFloatWhileAtVersionAndEqualsPin()
    {
        var bare = Launch("uvx", "mcp-server-time");
        Assert.NotNull(bare);
        Assert.Equal(PackageEcosystem.Python, bare.Ecosystem);
        Assert.False(bare.IsPinned);
        Assert.Equal(PackageSpelling.UvAt, bare.Spelling);
        Assert.Equal("mcp-server-time@0.6.2", bare.PinnedArgument("0.6.2"));

        Assert.False(Launch("uvx", "mcp-server-time@latest")?.IsPinned);
        Assert.True(Launch("uvx", "mcp-server-time@0.6.2")?.IsPinned);
        Assert.True(Launch("uvx", "mcp-server-time==0.6.2")?.IsPinned);
        Assert.False(Launch("uvx", "mcp-server-time>=0.6")?.IsPinned);
        Assert.False(Launch("uvx", "mcp-server-time==0.*")?.IsPinned);
        Assert.False(Launch("uvx", "mcp-server-time>=0.6,<1")?.IsPinned);

        // A range written as a requirement is pinned as one.
        var ranged = Launch("uvx", "mcp-server-time~=0.6");
        Assert.NotNull(ranged);
        Assert.Equal(PackageSpelling.Requirement, ranged.Spelling);
        Assert.Equal("mcp-server-time==0.6.2", ranged.PinnedArgument("0.6.2"));
    }

    [Fact]
    public void UvxOptionsAreSkippedAndFromCarriesTheRequirement()
    {
        var skipped = Launch("uvx", "--python", "3.12", "--with", "httpx", "mcp-server-fetch", "--port", "1");
        Assert.NotNull(skipped);
        Assert.Equal("mcp-server-fetch", skipped.Name);
        Assert.Equal(4, skipped.ArgumentIndex);

        var from = Launch("uvx", "--from", "awslabs.aws-mcp[cli]", "aws-mcp");
        Assert.NotNull(from);
        Assert.Equal("awslabs.aws-mcp", from.Name);
        Assert.Equal("[cli]", from.Extras);
        Assert.Equal(1, from.ArgumentIndex);
        Assert.Equal(PackageSpelling.Requirement, from.Spelling);
        Assert.Equal("awslabs.aws-mcp[cli]==1.4.0", from.PinnedArgument("1.4.0"));

        var joined = Launch("uv", "tool", "run", "--from=pkg==2.0.0", "pkg");
        Assert.NotNull(joined);
        Assert.True(joined.IsPinned);
        Assert.Equal(2, joined.ArgumentIndex);

        Assert.Null(Launch("uvx", "--from", "git+https://github.com/example/pkg", "pkg"));
        Assert.Null(Launch("uvx", "--from", "./checkout", "pkg"));
        Assert.Null(Launch("uvx", "pkg; python_version<'3.12'"));
        Assert.Null(Launch("uvx", "server.py"));
        Assert.Null(Launch("uv", "run", "server.py"));
    }

    [Fact]
    public void PipxRunCanBeReadButABareNameCannotBePinnedInPlace()
    {
        var bare = Launch("pipx", "run", "mcp-server-git");
        Assert.NotNull(bare);
        Assert.False(bare.IsPinned);
        Assert.False(bare.CanPinInPlace);
        Assert.Null(bare.PinnedArgument("1.0.0"));

        var spec = Launch("pipx", "run", "--spec", "mcp-server-git>=1", "mcp-server-git");
        Assert.NotNull(spec);
        Assert.Equal(2, spec.ArgumentIndex);
        Assert.True(spec.CanPinInPlace);
        Assert.Equal("mcp-server-git==1.2.0", spec.PinnedArgument("1.2.0"));

        Assert.True(Launch("pipx", "run", "--spec", "mcp-server-git==1.2.0", "mcp-server-git")?.IsPinned);
        Assert.Null(Launch("pipx", "install", "mcp-server-git"));
        Assert.Null(Launch("pipx", "run", "mcp-server-git@1.0"));
    }

    [Fact]
    public void APinIsOnlyWrittenForAVersionThatNamesExactlyOneRelease()
    {
        var npm = Launch("npx", "pkg");
        Assert.NotNull(npm);
        Assert.Equal("pkg@1.2.3", npm.PinnedArgument("1.2.3"));
        Assert.Null(npm.PinnedArgument("1.2"));
        Assert.Null(npm.PinnedArgument("latest"));
        Assert.Null(npm.PinnedArgument("1.2.3 || 2"));
        // .NET's `$` would accept a trailing newline; a version must not smuggle one in.
        Assert.Null(npm.PinnedArgument("1.2.3\n"));

        var python = Launch("uvx", "pkg");
        Assert.NotNull(python);
        Assert.Equal("pkg@2024.1", python.PinnedArgument("2024.1"));
        Assert.Equal("pkg@1.0.0rc1", python.PinnedArgument("1.0.0rc1"));
        Assert.Null(python.PinnedArgument("1.*"));
    }

    [Fact]
    public void ProvenanceReadsItsPackageNameFromTheSameParser()
    {
        var python = ServerProvenance.Infer(
            Transport.Stdio,
            @"C:\Users\example\.local\bin\uvx.exe",
            ["--from", "awslabs.aws-mcp[cli]==1.0.0", "aws-mcp"],
            null);
        Assert.Equal(ProvenanceSourceKinds.Python, python.SourceKind);
        Assert.Equal("awslabs.aws-mcp", python.PackageName);
        Assert.Equal("1.0.0", python.InstalledVersion);

        var wrapped = ServerProvenance.Infer(
            Transport.Stdio,
            "cmd",
            ["/c", "npx", "-y", "@scope/pkg@1.2.3"],
            null);
        Assert.Equal(ProvenanceSourceKinds.Npm, wrapped.SourceKind);
        Assert.Equal("@scope/pkg", wrapped.PackageName);
        Assert.Equal("1.2.3", wrapped.InstalledVersion);

        // A script is a local file, not a registry package, whoever runs it.
        Assert.Equal(
            ProvenanceSourceKinds.LocalPackage,
            ServerProvenance.Infer(Transport.Stdio, "uvx", ["server.py"], null).SourceKind);
    }
}

/// <summary>MCP Doctor's <c>unpinned-package</c> finding (§7.10).</summary>
public sealed class UnpinnedPackageDoctorTests
{
    internal static Server StdioServer(
        string command,
        IReadOnlyList<string> args,
        Dictionary<ClientKey, Enablement>? enabledIn = null) => new()
    {
        Id = "pkg",
        Name = "pkg",
        Transport = Transport.Stdio,
        Command = command,
        Args = args,
        Env = [],
        Url = null,
        EnabledIn = enabledIn ?? new Dictionary<ClientKey, Enablement> { [ClientId.Cursor] = Enablement.Enabled },
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = Server.MakeFingerprint(command, args, null),
        IsBundled = false,
        DefinitionSource = "{}",
    };

    private static ClientDefinition Copy(string command, IReadOnlyList<string> args, bool bundled = false) => new(
        Transport.Stdio,
        command,
        args,
        [],
        null,
        new Origin.ConfigFile(ClientId.Cursor),
        bundled,
        "{}");

    [Fact]
    public void AnUnpinnedNpxPackageIsAWarningWithThePinRepairEvenUnchecked()
    {
        var report = McpDoctor.Analyze(StdioServer("npx", ["-y", "pkg@latest"]));

        var finding = Assert.Single(report.Findings, item => item.Code == "unpinned-package");
        Assert.Equal(DoctorSeverity.Warning, finding.Severity);
        Assert.Equal(DoctorAction.PinPackageVersion, finding.Action);
        Assert.Contains("pkg@latest", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("“latest” tag", finding.Detail, StringComparison.Ordinal);
        Assert.Contains(report.Findings, item => item.Code == "not-checked");
    }

    [Fact]
    public void AWrappedWindowsLaunchIsFlaggedToo()
    {
        var report = McpDoctor.Analyze(StdioServer(
            @"C:\Windows\System32\cmd.exe", ["/c", "npx.cmd", "-y", "@modelcontextprotocol/server-github"]));

        var finding = Assert.Single(report.Findings, item => item.Code == "unpinned-package");
        Assert.Equal(DoctorAction.PinPackageVersion, finding.Action);
        Assert.Contains("names no version", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedUnrecognizedAndRemoteServersAreNotFlagged()
    {
        var remote = StdioServer("npx", ["-y", "pkg"]);
        remote = new Server
        {
            Id = remote.Id,
            Name = remote.Name,
            Transport = Transport.Http,
            Command = null,
            Args = [],
            Env = [],
            Url = "https://example.com/mcp",
            EnabledIn = remote.EnabledIn,
            Origin = remote.Origin,
            Fingerprint = remote.Fingerprint,
            IsBundled = false,
            DefinitionSource = "{}",
        };

        foreach (var server in new[]
        {
            StdioServer("npx", ["-y", "pkg@1.2.3"]),
            StdioServer("npx.cmd", ["-y", "pkg@1.2.3"]),
            StdioServer("node", [@"C:\Users\example\server.js"]),
            StdioServer("docker", ["run", "-i", "ghcr.io/example/mcp@sha256:abc"]),
            remote,
        })
        {
            Assert.DoesNotContain(McpDoctor.Analyze(server).Findings, item => item.Code == "unpinned-package");
        }
    }

    [Fact]
    public void ARunnerThatCannotTakeAVersionInPlaceIsAdviceNotARepair()
    {
        var report = McpDoctor.Analyze(StdioServer("pipx", ["run", "mcp-server-git"]));

        var finding = Assert.Single(report.Findings, item => item.Code == "unpinned-package");
        Assert.Null(finding.Action);
        Assert.Contains("--spec mcp-server-git==VERSION", finding.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void EditsCoverEveryCopyOfTheSamePackageEachAtItsOwnPosition()
    {
        var server = StdioServer("npx", ["-y", "pkg"], new Dictionary<ClientKey, Enablement>
        {
            [ClientId.Cursor] = Enablement.Enabled,
            [ClientId.ClaudeCode] = Enablement.Disabled,
            [ClientId.Codex] = Enablement.Enabled,
            [ClientId.VsCode] = Enablement.Absent,
            [ClientId.ClaudeDesktop] = Enablement.Enabled,
        });
        server.DefinitionsByClient[ClientId.Cursor] = Copy("npx", ["-y", "pkg"]);
        server.DefinitionsByClient[ClientId.ClaudeCode] = Copy("bunx", ["pkg@latest", "--flag"]);
        server.DefinitionsByClient[ClientId.Codex] = Copy("npx", ["-y", "pkg@1.0.0"]);          // already pinned
        server.DefinitionsByClient[ClientId.VsCode] = Copy("npx", ["-y", "pkg"]);               // absent here
        server.DefinitionsByClient[ClientId.ClaudeDesktop] = Copy("cmd", ["/c", "npx", "-y", "pkg"]);

        var edits = PackagePin.Edits(server, "2.0.0");

        Assert.Equal(new ArgumentEdit(1, "pkg", "pkg@2.0.0"), edits[ClientId.Cursor]);
        Assert.Equal(new ArgumentEdit(0, "pkg@latest", "pkg@2.0.0"), edits[ClientId.ClaudeCode]);
        Assert.Equal(new ArgumentEdit(3, "pkg", "pkg@2.0.0"), edits[ClientId.ClaudeDesktop]);
        Assert.False(edits.ContainsKey(ClientId.Codex));
        Assert.False(edits.ContainsKey(ClientId.VsCode));

        Assert.Empty(PackagePin.Edits(server, "2.x"));
    }

    [Fact]
    public void BundledAndReadOnlyCopiesAreNeverEdited()
    {
        var server = StdioServer("npx", ["-y", "pkg"], new Dictionary<ClientKey, Enablement>
        {
            [ClientId.ClaudeDesktop] = Enablement.Enabled,
        });
        server.DefinitionsByClient[ClientId.ClaudeDesktop] = Copy("npx", ["-y", "pkg"], bundled: true);

        Assert.False(PackagePin.CanPin(server));
        Assert.Empty(PackagePin.Edits(server, "1.0.0"));
        var finding = Assert.Single(McpDoctor.Analyze(server).Findings, item => item.Code == "unpinned-package");
        Assert.Null(finding.Action);
    }
}

/// <summary>Writing the pin: one string literal, and nothing else (§6.3, §7.10).</summary>
public sealed class PackagePinWriteTests
{
    private const string CursorText = """
        {
          // Hand-written, and it stays that way.
          "mcpServers": {
            "GitHub": {
              "command": "npx",
              "args": [
                "-y",
                "@modelcontextprotocol/server-github" // the server
              ],
              "env": { "GITHUB_TOKEN": "ghp_example" }
            }
          }
        }
        """;

    private const string CodexText = """
        # Codex config
        model = "o3"

        [mcp_servers.github]
        command = "npx"
        args = ["-y", "@modelcontextprotocol/server-github@latest"]   # floating
        enabled = false

        [mcp_servers.github.env]
        GITHUB_TOKEN = "ghp_example"
        """;

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(CursorText, @".cursor\mcp.json");
        harness.Write(CodexText, @".codex\config.toml");
        return harness;
    }

    /// <summary>
    /// By identity: the row is one server spelled <c>GitHub</c> in Cursor and
    /// <c>github</c> in Codex, and which spelling names it depends on read order.
    /// </summary>
    private static Server GitHub(ToggleHarness harness)
    {
        var found = harness.Discover().Servers.FirstOrDefault(server => server.Id == "github");
        Assert.NotNull(found);
        return found;
    }

    [Fact]
    public void JsonAndTomlEachChangeByExactlyOneStringLiteral()
    {
        using var harness = MakeHarness();

        var github = GitHub(harness);
        var edits = PackagePin.Edits(github, "2025.4.8");
        Assert.Equal(
            new[] { ClientId.Codex, ClientId.Cursor },
            edits.Keys.OrderBy(client => client.Raw(), StringComparer.Ordinal).ToArray());

        var result = harness.Authoring().ReplaceArgument(github, edits);
        Assert.Equal([ClientId.Codex, ClientId.Cursor], result.Changed);
        Assert.Equal(2, result.BackupIDs.Count);

        Assert.Equal(
            CursorText.Replace(
                "\"@modelcontextprotocol/server-github\" // the server",
                "\"@modelcontextprotocol/server-github@2025.4.8\" // the server",
                StringComparison.Ordinal),
            harness.Text(@".cursor\mcp.json"));
        Assert.Equal(
            CodexText.Replace(
                "\"@modelcontextprotocol/server-github@latest\"",
                "\"@modelcontextprotocol/server-github@2025.4.8\"",
                StringComparison.Ordinal),
            harness.Text(@".codex\config.toml"));

        // Still off in Codex, and nothing left to flag.
        var after = GitHub(harness);
        Assert.Equal(Enablement.Disabled, after.EnabledIn[ClientId.Codex]);
        Assert.Null(PackagePin.UnpinnedLaunch(after));
        Assert.DoesNotContain(McpDoctor.Analyze(after).Findings, item => item.Code == "unpinned-package");
    }

    [Fact]
    public void AFileThatNoLongerSaysWhatTheEditExpectsIsRefusedWithNothingWritten()
    {
        using var harness = MakeHarness();

        var github = GitHub(harness);
        var edits = new Dictionary<ClientId, ArgumentEdit>(PackagePin.Edits(github, "1.0.0"))
        {
            // Codex sorts first and would be fine; Cursor no longer matches.
            [ClientId.Cursor] = new ArgumentEdit(1, "something-else", "x@1.0.0"),
        };

        var error = Assert.Throws<JsonEditException>(() => harness.Authoring().ReplaceArgument(github, edits));
        Assert.Equal(JsonEditErrorKind.ValueMismatch, error.Kind);
        Assert.Equal(CursorText, harness.Text(@".cursor\mcp.json"));
        Assert.Equal(CodexText, harness.Text(@".codex\config.toml"));
    }

    /// <summary>
    /// Cursor has no off switch, so switching a server off there removed it and
    /// Kytto is holding the bytes. Pinning must reach them, or switching back on
    /// brings the floating version back — and must not write them into the file,
    /// which would switch it on.
    /// </summary>
    [Fact]
    public void ASwitchedOffCopyKyttoHoldsIsPinnedTooAndStaysOff()
    {
        using var harness = MakeHarness();

        Assert.True(harness.Toggles.SetEnabled(false, GitHub(harness), ClientId.Cursor).WasParked);
        var cursorOff = harness.Text(@".cursor\mcp.json");

        var github = GitHub(harness);
        Assert.Equal(Enablement.Disabled, github.EnabledIn[ClientId.Cursor]);
        var result = harness.Authoring().ReplaceArgument(github, PackagePin.Edits(github, "1.0.0"));

        Assert.Equal([ClientId.Codex], result.Changed);
        Assert.Equal([ClientId.Cursor], result.ParkedUpdated);
        Assert.Empty(result.ParkedFailures);
        Assert.Equal(cursorOff, harness.Text(@".cursor\mcp.json"));

        var parked = harness.ParkStore.Parked(ClientId.Cursor, "GitHub");
        Assert.NotNull(parked);
        Assert.Contains(
            "\"@modelcontextprotocol/server-github@1.0.0\" // the server",
            parked.SourceText,
            StringComparison.Ordinal);
        Assert.Equal(Enablement.Disabled, GitHub(harness).EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void AnInlineCodexDefinitionIsSplicedInPlace()
    {
        const string Inline = """
            [mcp_servers]
            time = { command = "uvx", args = ["mcp-server-time"], enabled = true }
            """;

        var text = TomlDocument.Parse(Inline).SettingServerArgument(
            0, "mcp-server-time", "mcp-server-time@0.6.2", "time", "mcp_servers");

        Assert.Equal(
            Inline.Replace("\"mcp-server-time\"", "\"mcp-server-time@0.6.2\"", StringComparison.Ordinal),
            text);
    }

    [Fact]
    public void TheJsonSpliceCountsStringElementsTheWayTheModelDoes()
    {
        const string Source = """{"args": [1, "-y", null, "pkg"]}""";

        var text = JsonDocument.Parse(Source).ReplacingString(["args"], 1, "pkg", "pkg@1.0.0");
        Assert.Equal("""{"args": [1, "-y", null, "pkg@1.0.0"]}""", text);

        var error = Assert.Throws<JsonEditException>(() =>
            JsonDocument.Parse(Source).ReplacingString(["args"], 0, "pkg", "x"));
        Assert.Equal(JsonEditErrorKind.ValueMismatch, error.Kind);
    }

    [Fact]
    public void SplicingAnArgumentOverItselfChangesNothing()
    {
        var document = JsonDocument.Parse(CursorText);
        Assert.Equal(
            CursorText,
            document.ReplacingString(
                ["mcpServers", "GitHub", "args"], 1,
                "@modelcontextprotocol/server-github", "@modelcontextprotocol/server-github"));

        var toml = TomlDocument.Parse(CodexText);
        Assert.Equal(
            CodexText,
            toml.SettingServerArgument(
                1, "@modelcontextprotocol/server-github@latest",
                "@modelcontextprotocol/server-github@latest", "github", "mcp_servers"));
    }
}
