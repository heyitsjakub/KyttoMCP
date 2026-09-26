using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

/// <summary>
/// Drift is the evidence under a matrix row: one name, several files, and no
/// guarantee they agree. These assert that a disagreement is found, attributed to
/// the right client, and described without ever naming a secret value.
/// </summary>
public sealed class ServerDriftTests
{
    private static ClientDefinition Definition(
        string? command = "npx",
        IReadOnlyList<string>? args = null,
        IReadOnlyList<EnvEntry>? env = null,
        string? url = null,
        Transport transport = Transport.Stdio,
        bool isBundled = false,
        bool isReadOnly = false) =>
        new(
            transport,
            command,
            args ?? [],
            env ?? [],
            url,
            new Origin.ConfigFile(ClientId.Cursor),
            isBundled,
            "{}")
        {
            IsReadOnly = isReadOnly,
        };

    private static Server ServerWith(Dictionary<ClientKey, ClientDefinition> definitions) => new()
    {
        Id = "github",
        Name = "github",
        Transport = Transport.Stdio,
        Command = "npx",
        Args = [],
        Env = [],
        Url = null,
        EnabledIn = [],
        Origin = new Origin.ConfigFile(ClientId.Cursor),
        Fingerprint = "x",
        IsBundled = false,
        DefinitionSource = "{}",
        DefinitionsByClient = definitions,
    };

    [Fact]
    public void ClientsThatAgreeProduceNoReport()
    {
        var shared = Definition(args: ["-y", "server-github"]);
        Assert.Null(ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = shared,
            [ClientId.ClaudeCode] = shared,
        })));
    }

    [Fact]
    public void ASingleCopyCannotDisagreeWithAnything() =>
        Assert.Null(ServerDrift.Detect(ServerWith(new() { [ClientId.Cursor] = Definition() })));

    [Fact]
    public void ADifferentCommandIsAttributedToTheClientHoldingIt()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = Definition(command: "npx"),
            [ClientId.ClaudeCode] = Definition(command: "bunx"),
        }));

        Assert.NotNull(drift);
        Assert.Equal([DriftField.Command], drift.Fields);
        Assert.Equal(2, drift.Variants.Count);
        Assert.Equal(
            "npx",
            drift.Variants.Single(v => v.ClientIDs.SequenceEqual([ClientId.Cursor])).Definition.Command);
        Assert.Equal(
            "bunx",
            drift.Variants.Single(v => v.ClientIDs.SequenceEqual([ClientId.ClaudeCode])).Definition.Command);
    }

    [Fact]
    public void ClientsSharingADefinitionAreOneVariantAndTheMajorityLeads()
    {
        var common = Definition(args: ["-y", "server-github"]);
        var odd = Definition(args: ["-y", "server-github@0.1.0"]);

        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = common,
            [ClientId.ClaudeCode] = common,
            [ClientId.VsCode] = odd,
        }));

        Assert.NotNull(drift);
        Assert.Equal([DriftField.Arguments], drift.Fields);
        Assert.Equal(2, drift.Variants.Count);
        Assert.Equal([ClientId.ClaudeCode, ClientId.Cursor], drift.Variants[0].ClientIDs);
        Assert.Equal([ClientId.VsCode], drift.Variants[1].ClientIDs);
    }

    [Fact]
    public void AKeyOneClientIsMissingIsReportedAsAKeyDifference()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = Definition(env: [new EnvEntry("TOKEN", "abc")]),
            [ClientId.ClaudeCode] = Definition(env: []),
        }));

        Assert.NotNull(drift);
        Assert.Equal([DriftField.EnvironmentKeys], drift.Fields);
    }

    [Fact]
    public void TheSameKeyHoldingTwoValuesIsDriftAndTheValuesStayOutOfTheReport()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = Definition(env: [new EnvEntry("TOKEN", "ghp_live")]),
            [ClientId.ClaudeCode] = Definition(env: [new EnvEntry("TOKEN", "ghp_stale")]),
        }));

        Assert.NotNull(drift);
        Assert.Equal([DriftField.EnvironmentValues], drift.Fields);
        // The finding is that they differ. Nothing the report itself states spells
        // either value out (§6).
        Assert.DoesNotContain("ghp_", string.Join(',', drift.Fields.Select(f => f.Raw())), StringComparison.Ordinal);
        Assert.Equal(2, drift.Variants.Count);
    }

    [Fact]
    public void AnIdenticalCommandWithDifferentTransportsStillDisagrees()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] =
                Definition(command: null, url: "https://example.com/mcp", transport: Transport.Http),
            [ClientId.ClaudeCode] =
                Definition(command: null, url: "https://example.com/mcp", transport: Transport.Sse),
        }));

        Assert.NotNull(drift);
        Assert.Equal([DriftField.Transport], drift.Fields);
    }

    [Fact]
    public void AnInstalledBundleCanBeComparedButNeverCopiedOutOf()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.ClaudeDesktop] =
                Definition(command: "node", args: ["${__dirname}/index.js"], isBundled: true),
            [ClientId.Cursor] = Definition(command: "npx"),
        }));

        Assert.NotNull(drift);
        Assert.False(
            drift.Variants.Single(v => v.ClientIDs.SequenceEqual([ClientId.ClaudeDesktop])).CanBeSource);
        Assert.True(
            drift.Variants.Single(v => v.ClientIDs.SequenceEqual([ClientId.Cursor])).CanBeSource);
        Assert.Equal([ClientId.ClaudeDesktop], drift.UnwritableClientIDs);
    }

    [Fact]
    public void AReadOnlyCustomCopyIsReportedAsUnwritable()
    {
        var custom = ClientKey.Custom(Guid.Parse("7f045d9a-7d34-4bf5-bf24-85d3a2d9ed7a"));
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] = Definition(command: "npx"),
            [custom] = Definition(command: "bunx", isReadOnly: true),
        }));

        Assert.NotNull(drift);
        Assert.Equal([custom], drift.UnwritableClientIDs);
    }

    [Fact]
    public void SeveralFieldsDifferingAreAllReported()
    {
        var drift = ServerDrift.Detect(ServerWith(new()
        {
            [ClientId.Cursor] =
                Definition(command: "npx", args: ["a"], env: [new EnvEntry("TOKEN", "1")]),
            [ClientId.ClaudeCode] =
                Definition(command: "bunx", args: ["b"], env: [new EnvEntry("TOKEN", "2")]),
        }));

        Assert.NotNull(drift);
        Assert.Equal(
            [DriftField.Arguments, DriftField.Command, DriftField.EnvironmentValues],
            drift.Fields);
    }
}
