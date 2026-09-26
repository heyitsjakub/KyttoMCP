using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Secrets;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

public static class SecretsHarnessExtensions
{
    public static SecretsService Secrets(this ToggleHarness harness, ISecretStore store) =>
        new(harness.Home,
            new ConfigWriter(harness.Backups),
            harness.Ledger,
            store);
}

public class SecretHeuristicTests
{
    [Theory]
    [InlineData("GITHUB_PERSONAL_ACCESS_TOKEN", "ghp_abc")]
    [InlineData("API_KEY", "x")]
    [InlineData("DB_PASSWORD", "hunter2")]
    [InlineData("SLACK_BOT_TOKEN", "xoxb-1")]
    [InlineData("AUTH_HEADER", "Bearer x")]
    public void NamesThatAnnounceThemselvesAreSecrets(string key, string value) =>
        Assert.True(SecretsService.LooksSensitive(key, value));

    /// <summary>These point at secrets rather than being them.</summary>
    [Theory]
    [InlineData("SSH_KEY_PATH", "~/.ssh/id_rsa")]
    [InlineData("TOKEN_FILE", "/etc/token")]
    [InlineData("SECRET_DIR", "/run/secrets")]
    [InlineData("AUTH_URL", "https://example.com/auth")]
    [InlineData("KEY_ENABLED", "true")]
    public void NamesThatPointAtSecretsAreNot(string key, string value) =>
        Assert.False(SecretsService.LooksSensitive(key, value));

    [Fact]
    public void AnOpaqueBlobIsFlaggedWhateverItIsCalled() =>
        Assert.True(SecretsService.LooksSensitive("SOMETHING", "a1b2c3d4e5f6g7h8i9j0k1l2m3"));

    [Fact]
    public void OrdinaryConfigurationIsNot()
    {
        Assert.False(SecretsService.LooksSensitive("NODE_ENV", "production"));
        Assert.False(SecretsService.LooksSensitive("ALLOWED", "/Users/me/Projects and more"));
    }

    /// <summary>
    /// Derived from the value, so changing the value produces a different secret
    /// rather than silently reusing the old id.
    /// </summary>
    [Fact]
    public void TheIdentifierFollowsTheValue()
    {
        var before = SecretsService.Identifier("TOKEN", "one");
        var after = SecretsService.Identifier("TOKEN", "two");
        Assert.NotEqual(before, after);
        Assert.Equal(before, SecretsService.Identifier("TOKEN", "one"));
        Assert.StartsWith("token-", before, StringComparison.Ordinal);
    }

    /// <summary>The separator is what stops two different pairs colliding.</summary>
    [Fact]
    public void KeyAndValueCannotRunTogether() =>
        Assert.NotEqual(
            SecretsService.Identifier("AB", "C"),
            SecretsService.Identifier("A", "BC"));

    [Fact]
    public void MaskingShowsEnoughToRecogniseAndNotEnoughToUse()
    {
        Assert.Equal("••••••••", SecretsService.Mask("ghp_1234567890"));
        Assert.Equal("••••••••", SecretsService.Mask("abc"));
        Assert.DoesNotContain("ghp_", SecretsService.Mask("ghp_1234567890"), StringComparison.Ordinal);
        Assert.DoesNotContain("7890", SecretsService.Mask("ghp_1234567890"), StringComparison.Ordinal);
        Assert.DoesNotContain("abc", SecretsService.Mask("abc"), StringComparison.Ordinal);
    }
}

public class SecretsScanTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }

    [Fact]
    public void ScanningFindsTheTokenAndMasksIt()
    {
        using var harness = MakeHarness();
        var records = harness.Secrets(new InMemorySecretStore()).Scan(harness.Discover().Servers);

        var record = Assert.Single(records);
        Assert.Equal("GITHUB_PERSONAL_ACCESS_TOKEN", record.Key);
        Assert.Equal("••••••••", record.MaskedValue);
        // The value itself is not on the record at all.
        Assert.DoesNotContain("exampletoken", record.MaskedValue, StringComparison.Ordinal);
        Assert.Equal(ClientId.Cursor, Assert.Single(record.Usages).ClientID);
        Assert.False(record.IsShared);
    }

    /// <summary>
    /// The same token in two clients is one row — that is the case rotation exists
    /// for.
    /// </summary>
    [Fact]
    public void TheSameValueInTwoClientsIsOneSharedRecord()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);

        var records = harness.Secrets(new InMemorySecretStore()).Scan(harness.Discover().Servers);
        var record = Assert.Single(records);

        Assert.True(record.IsShared);
        Assert.Equal(
            [ClientId.Cursor, ClientId.VsCode],
            record.Usages.Select(usage => usage.ClientID).Order().ToArray());
    }

    /// <summary>Two different tokens sharing a name are two rows — the drift worth seeing.</summary>
    [Fact]
    public void TheSameNameHoldingTwoValuesIsTwoRecords()
    {
        using var harness = MakeHarness();
        harness.Write("""
            {
              "servers": {
                "github": {
                  "command": "npx",
                  "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_adifferentvalueentirely" }
                }
              }
            }
            """, $@"{ToggleHarness.CodeUser}\mcp.json");

        var records = harness.Secrets(new InMemorySecretStore()).Scan(harness.Discover().Servers);
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal("GITHUB_PERSONAL_ACCESS_TOKEN", record.Key));
    }

    [Fact]
    public void RevealingReturnsTheRealValueOnlyWhenAsked()
    {
        using var harness = MakeHarness();
        var service = harness.Secrets(new InMemorySecretStore());
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        Assert.Equal("ghp_exampletokenvalue", service.Reveal(record.Id, servers));
        Assert.Null(service.Reveal("nothing-like-this", servers));
    }

    [Fact]
    public void AdoptAndForgetTrackWhetherKyttoHoldsACopy()
    {
        using var harness = MakeHarness();
        var store = new InMemorySecretStore();
        var service = harness.Secrets(store);
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        Assert.False(record.IsInSecretStore);

        service.Adopt(record.Id, servers);
        Assert.True(Assert.Single(service.Scan(servers)).IsInSecretStore);
        Assert.Equal("ghp_exampletokenvalue", store.Value(record.Id));

        service.Forget(record.Id);
        Assert.False(Assert.Single(service.Scan(servers)).IsInSecretStore);
    }
}

public class SecretsRotationTests
{
    private sealed class FailSecondWrite : IConfigFileWriter
    {
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 2) throw new IOException("Injected second-target failure.");
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }

    [Fact]
    public void RotatingWritesTheNewValueEverywhereTheOldOneWas()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);

        var store = new InMemorySecretStore();
        var service = harness.Secrets(store);
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        var result = service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: true);

        Assert.Equal("GITHUB_PERSONAL_ACCESS_TOKEN", result.Key);
        Assert.Equal(2, result.Updated.Count);

        Assert.Equal("ghp_brandnewtokenvalue", JsonDocument.Parse(harness.Text(@".cursor\mcp.json"))
            .ValueAt("mcpServers", "github", "env", "GITHUB_PERSONAL_ACCESS_TOKEN")?.StringValue);
        Assert.Equal("ghp_brandnewtokenvalue",
            JsonDocument.Parse(harness.Text($@"{ToggleHarness.CodeUser}\mcp.json"))
                .ValueAt("servers", "github", "env", "GITHUB_PERSONAL_ACCESS_TOKEN")?.StringValue);

        // The stored copy follows the value, and the old entry goes with it.
        Assert.Null(store.Value(record.Id));
        Assert.Single(store.StoredIdentifiers());
    }

    [Fact]
    public void RotatingLeavesEverythingElseInTheFileAlone()
    {
        using var harness = MakeHarness();
        var service = harness.Secrets(new InMemorySecretStore());
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false);

        var after = JsonDocument.Parse(harness.Text(@".cursor\mcp.json"));
        Assert.Equal(["github", "figma"], after.ValueAt("mcpServers")?.Keys);
        Assert.Equal("https://mcp.figma.com/sse", after.ValueAt("mcpServers", "figma", "url")?.StringValue);
        Assert.Equal("npx", after.ValueAt("mcpServers", "github", "command")?.StringValue);
    }

    [Fact]
    public void EveryRotatedFileIsBackedUpFirst()
    {
        using var harness = MakeHarness();
        var before = harness.Text(@".cursor\mcp.json");
        var service = harness.Secrets(new InMemorySecretStore());
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        var result = service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false);

        var backupId = Assert.Single(result.BackupIDs);
        var backup = harness.Backups.Get(backupId, ClientId.Cursor);
        Assert.NotNull(backup);
        Assert.Equal(before, File.ReadAllText(backup.Path));
    }

    [Fact]
    public void AnEmptyValueIsRefused()
    {
        using var harness = MakeHarness();
        var service = harness.Secrets(new InMemorySecretStore());
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        Assert.Throws<SecretsException>(() => service.Rotate(record.Id, "", servers, alsoStore: false));
    }

    [Fact]
    public void RotatingSomethingThatIsGoneIsRefused()
    {
        using var harness = MakeHarness();
        var service = harness.Secrets(new InMemorySecretStore());
        Assert.Throws<SecretsException>(() =>
            service.Rotate("nothing-like-this", "x", harness.Discover().Servers, alsoStore: false));
    }

    [Fact]
    public void FailedSecondTargetRestoresEveryConfigByteForByte()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);
        var cursorPath = Path.Combine(harness.Home, @".cursor\mcp.json");
        var vsCodePath = Path.Combine(harness.Home, ToggleHarness.CodeUser, "mcp.json");
        var cursorBefore = File.ReadAllBytes(cursorPath);
        var vsCodeBefore = File.ReadAllBytes(vsCodePath);
        var servers = harness.Discover().Servers;
        var service = new SecretsService(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailSecondWrite()),
            harness.Ledger,
            new InMemorySecretStore());
        var record = Assert.Single(service.Scan(servers));

        Assert.Throws<IOException>(() =>
            service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false));

        Assert.Equal(cursorBefore, File.ReadAllBytes(cursorPath));
        Assert.Equal(vsCodeBefore, File.ReadAllBytes(vsCodePath));
    }

    [Fact]
    public void StoringAnUnchangedValueDoesNotDeleteTheStoredCopy()
    {
        using var harness = MakeHarness();
        var store = new InMemorySecretStore();
        var service = harness.Secrets(store);
        var servers = harness.Discover().Servers;
        var record = Assert.Single(service.Scan(servers));

        service.Rotate(record.Id, "ghp_exampletokenvalue", servers, alsoStore: true);

        Assert.Equal("ghp_exampletokenvalue", store.Value(record.Id));
    }

    [Fact]
    public void RotatingRewritesANestedInlineTomlEnvironmentWithoutRegeneratingTheServer()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            [mcp_servers]
            inline = { command = "run", args = ["x"], env = { API_TOKEN = "old-secret-value" } }
            """, @".codex\config.toml");
        var before = harness.Discover();
        var service = harness.Secrets(new InMemorySecretStore());
        var record = Assert.Single(service.Scan(before.Servers));

        var result = service.Rotate(record.Id, "new-secret-value", before.Servers, alsoStore: false);

        Assert.Single(result.Updated);
        var document = Kytto.Core.Toml.TomlDocument.Parse(harness.Text(@".codex\config.toml"));
        var server = document.Table("mcp_servers")?.Pair("inline")?.Value.InlinePairs;
        var environment = server?.First(pair => pair.Name == "env").Value.InlinePairs;
        Assert.Equal(
            "new-secret-value",
            environment?.First(pair => pair.Name == "API_TOKEN").Value.StringValue);
        Assert.Equal("run", server?.First(pair => pair.Name == "command").Value.StringValue);
    }
}

public class SecretsExposureTests
{
    /// <summary>
    /// Restricting a config is what the UI offers when a file is readable by
    /// others; afterwards nothing else should be listed on it.
    /// </summary>
    [Fact]
    public void RestrictingPermissionsLeavesTheFileReadableByItsOwner()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");

        var service = harness.Secrets(new InMemorySecretStore());
        Assert.True(service.RestrictPermissions(ClientId.Cursor));

        // The point of the exercise: the owner can still read it.
        Assert.Contains("ghp_", harness.Text(@".cursor\mcp.json"), StringComparison.Ordinal);

        var records = service.Scan(harness.Discover().Servers);
        var record = Assert.Single(records);
        Assert.DoesNotContain(
            record.Exposure,
            exposure => exposure is SecretExposure.ReadableByOthers);
    }

    [Fact]
    public void RestrictingAClientWithNoFileIsAQuietNo()
    {
        using var harness = new ToggleHarness();
        Assert.False(harness.Secrets(new InMemorySecretStore()).RestrictPermissions(ClientId.Cursor));
    }

    /// <summary>
    /// A token inside a Git working tree is the accident §6 opens with, so it is
    /// called out — but only when the file is not ignored.
    /// </summary>
    [Fact]
    public void AConfigInsideAGitRepositoryIsCalledOut()
    {
        using var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        // A bare `.git` directory is enough: nothing shells out unless git is there.
        Directory.CreateDirectory(Path.Combine(harness.Home, ".git"));

        var records = harness.Secrets(new InMemorySecretStore()).Scan(harness.Discover().Servers);
        var record = Assert.Single(records);

        Assert.Contains(record.Exposure, exposure => exposure is SecretExposure.InsideGitRepository);
    }
}
