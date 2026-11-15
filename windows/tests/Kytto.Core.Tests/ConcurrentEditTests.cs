using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Secrets;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// AUD-02: a save made by another program while Kytto is writing must be refused,
/// not overwritten. Other programs do not take Kytto's lock, so what is tested is
/// that every check refers to the bytes the edit read, and that the last one happens
/// at the swap.
/// </summary>
public sealed class ConfigWriterConcurrentEditTests : IDisposable
{
    private const string Original = """{"mcpServers":{},"other":"before"}""";
    private const string External = """{"mcpServers":{},"other":"external-save"}""";

    private readonly ToggleHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private string ConfigPath => Path.Combine(_harness.Home, @".cursor\mcp.json");

    private static string Edited(JsonDocument document) =>
        document.SourceText.Replace("\"before\"", "\"kytto\"", StringComparison.Ordinal);

    /// <summary>Saves <see cref="External"/> at the last moment before the real swap.</summary>
    private sealed class SaveJustBeforeTheSwap(string path) : IConfigFileWriter
    {
        private readonly AtomicConfigFileWriter _real = new();

        public void Write(byte[] data, string target) => _real.Write(data, target);

        public void Delete(string target) => _real.Delete(target);

        public void Write(byte[] data, string target, string? expectedDigest)
        {
            File.WriteAllText(path, External);
            _real.Write(data, target, expectedDigest);
        }
    }

    [Fact]
    public void ASaveDuringTheTransformIsRefusedAndSurvives()
    {
        _harness.Write(Original, @".cursor\mcp.json");

        var error = Assert.Throws<ConfigWriteException>(() => new ConfigWriter(_harness.Backups).Edit<JsonDocument>(
            ConfigPath, ClientId.Cursor, ".cursor/mcp.json", Digest.Of(Encoding.UTF8.GetBytes(Original)),
            document =>
            {
                File.WriteAllText(ConfigPath, External);
                return Edited(document);
            }));

        Assert.True(error.IsChangedOnDisk);
        Assert.Equal(External, _harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void ASaveThatLandsAfterTheLastCheckIsRefusedAtTheSwap()
    {
        _harness.Write(Original, @".cursor\mcp.json");

        var error = Assert.Throws<ConfigWriteException>(() =>
            new ConfigWriter(_harness.Backups, new SaveJustBeforeTheSwap(ConfigPath)).Edit<JsonDocument>(
                ConfigPath, ClientId.Cursor, ".cursor/mcp.json", Digest.Of(Encoding.UTF8.GetBytes(Original)),
                Edited));

        Assert.True(error.IsChangedOnDisk);
        Assert.Equal(External, _harness.Text(@".cursor\mcp.json"));
        // No staged file is left beside the config.
        Assert.Equal([ConfigPath], Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!));
    }

    [Fact]
    public void AFileCreatedDuringTheWriteOfANewOneIsNotReplaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        const string created = """{"mcpServers":{"theirs":{"command":"x"}}}""";

        var error = Assert.Throws<ConfigWriteException>(() => new ConfigWriter(_harness.Backups).Edit<JsonDocument>(
            ConfigPath, ClientId.Cursor, ".cursor/mcp.json", expectedDigest: null,
            document =>
            {
                File.WriteAllText(ConfigPath, created);
                return document.SourceText;
            }));

        Assert.True(error.IsChangedOnDisk);
        Assert.Equal(created, _harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void TheBackupHoldsTheBytesTheEditStartedFrom()
    {
        _harness.Write(Original, @".cursor\mcp.json");

        var receipt = new ConfigWriter(_harness.Backups).Edit<JsonDocument>(
            ConfigPath, ClientId.Cursor, ".cursor/mcp.json", Digest.Of(Encoding.UTF8.GetBytes(Original)), Edited);

        var backup = _harness.Backups.Get(receipt.BackupID!, ClientId.Cursor)!;
        Assert.Equal(Original, File.ReadAllText(backup.Path));
        Assert.Equal(receipt.Digest, Digest.OfFile(ConfigPath));
    }

    [Fact]
    public void AnExclusiveWriteNeverReplacesAFileThatAppeared()
    {
        var directory = Path.Combine(_harness.Home, "absent");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");

        AtomicWriter.Write(Encoding.UTF8.GetBytes("first"), path, expecting: AtomicWriter.Precondition.Absent);
        Assert.Throws<TargetChangedException>(() =>
            AtomicWriter.Write(Encoding.UTF8.GetBytes("second"), path, expecting: AtomicWriter.Precondition.Absent));
        Assert.Throws<TargetChangedException>(() =>
            AtomicWriter.Write(Encoding.UTF8.GetBytes("second"), path,
                expecting: new AtomicWriter.Precondition(Digest.Of(Encoding.UTF8.GetBytes("other")))));

        Assert.Equal("first", File.ReadAllText(path));
        // No staged file is left behind by a refusal.
        Assert.Equal([path], Directory.GetFiles(directory));

        AtomicWriter.Write(Encoding.UTF8.GetBytes("third"), path,
            expecting: new AtomicWriter.Precondition(Digest.Of(Encoding.UTF8.GetBytes("first"))));
        Assert.Equal("third", File.ReadAllText(path));
    }

    [Fact]
    public void ARestoreBacksUpAndChecksTheBytesItRead()
    {
        _harness.Write(Original, @".cursor\mcp.json");
        var receipt = new ConfigWriter(_harness.Backups).Edit<JsonDocument>(
            ConfigPath, ClientId.Cursor, ".cursor/mcp.json", Digest.Of(Encoding.UTF8.GetBytes(Original)), Edited);
        var backup = _harness.Backups.Get(receipt.BackupID!, ClientId.Cursor)!;
        var edited = _harness.Text(@".cursor\mcp.json");

        File.WriteAllText(ConfigPath, External);
        var error = Assert.Throws<ConfigWriteException>(() =>
            _harness.Backups.RestoreChecked(backup, receipt.Digest, _harness.Resolver()));
        Assert.True(error.IsChangedOnDisk);
        Assert.Equal(External, _harness.Text(@".cursor\mcp.json"));

        File.WriteAllText(ConfigPath, edited);
        _harness.Backups.RestoreChecked(backup, receipt.Digest, _harness.Resolver());
        Assert.Equal(Original, _harness.Text(@".cursor\mcp.json"));
        // What the restore replaced was kept, so the restore is itself reversible.
        Assert.Contains(
            _harness.Backups.List(ClientId.Cursor),
            candidate => File.ReadAllText(candidate.Path) == edited);
    }
}

/// <summary>
/// AUD-03 and AUD-06: undoing a failed rotation must not erase newer work, and must
/// not delete a credential it never changed.
/// </summary>
/// <remarks>
/// Windows commits every config of a rotation in one <see cref="ConfigWriter"/>
/// transaction, and touches Credential Manager only after that commit succeeded, so
/// a failure can only come before any credential change. These pin both.
/// </remarks>
public sealed class RotationRollbackTests
{
    /// <summary>Fails the second file of a commit, after another app saved the first.</summary>
    private sealed class SaveFirstThenFailSecond(string firstPath, string external) : IConfigFileWriter
    {
        private int _writes;

        public void Write(byte[] data, string path)
        {
            _writes++;
            if (_writes == 2)
            {
                File.WriteAllText(firstPath, external);
                throw new IOException("Injected second-target failure.");
            }
            AtomicWriter.Write(data, path);
        }

        public void Delete(string path) => File.Delete(path);
    }

    /// <summary>Changes the held copy behind Kytto's back, then fails the commit.</summary>
    /// <remarks>
    /// Edits the store's file directly: calling <see cref="ParkStore.Park"/> from inside
    /// the commit would take the mutation gate under the commit lock, the reverse of
    /// the order everything else takes them in, and deadlock against a parallel test.
    /// </remarks>
    private sealed class ChangeParkThenFail(string parkFile) : IConfigFileWriter
    {
        public void Write(byte[] data, string path)
        {
            File.WriteAllText(parkFile, File.ReadAllText(parkFile).Replace(
                "ghp_brandnewtokenvalue", "changed-elsewhere", StringComparison.Ordinal));
            throw new IOException("Injected write failure.");
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
    public void ASaveMadeAfterTheRotationWroteAFileSurvivesTheRollback()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);
        var cursorPath = Path.Combine(harness.Home, @".cursor\mcp.json");
        var external = harness.Text(@".cursor\mcp.json").Replace(
            "\"mcpServers\"", "\"editorNote\":\"must-survive\",\"mcpServers\"", StringComparison.Ordinal);
        var servers = harness.Discover().Servers;
        var service = new SecretsService(
            harness.Home,
            new ConfigWriter(harness.Backups, new SaveFirstThenFailSecond(cursorPath, external)),
            harness.Ledger,
            new InMemorySecretStore());
        var record = Assert.Single(service.Scan(servers));

        var error = Assert.Throws<ConfigTransactionException>(() =>
            service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false));

        var path = Assert.Single(error.Paths);
        Assert.Contains("changed by another app", path, StringComparison.Ordinal);
        // The backups of the originals are the way back, and the error says so.
        Assert.Equal(2, error.BackupIds.Count);
        Assert.Contains(error.BackupIds[0], error.Message, StringComparison.Ordinal);
        Assert.Equal(external, harness.Text(@".cursor\mcp.json"));
    }

    [Fact]
    public void AFailureBeforeAnyCredentialChangeLeavesTheStoreUntouched()
    {
        using var harness = MakeHarness();
        var servers = harness.Discover().Servers;
        var store = new InMemorySecretStore();
        var savedId = SecretsService.Identifier("GITHUB_PERSONAL_ACCESS_TOKEN", "already-saved-value");
        store.Store("already-saved-value", savedId);
        var service = harness.Secrets(store);
        var record = Assert.Single(service.Scan(servers));
        store.Store("ghp_original", record.Id);

        // A digest conflict before the rotation changed anything.
        harness.Write(harness.Text(@".cursor\mcp.json") + "\n", @".cursor\mcp.json");
        foreach (var alsoStore in new[] { false, true })
        {
            var error = Assert.Throws<ConfigWriteException>(() =>
                service.Rotate(record.Id, "already-saved-value", servers, alsoStore));
            Assert.True(error.IsChangedOnDisk);
            Assert.Equal("already-saved-value", store.Value(savedId));
            Assert.Equal("ghp_original", store.Value(record.Id));
        }
    }

    [Fact]
    public void AHeldCopyChangedSinceTheRotationWroteItIsLeftAsItIs()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);
        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);
        var servers = harness.Discover().Servers;
        var service = new SecretsService(
            harness.Home,
            new ConfigWriter(harness.Backups, new ChangeParkThenFail(harness.Paths.ParkFile)),
            harness.Ledger,
            new InMemorySecretStore(),
            parkStore: harness.ParkStore);
        var record = Assert.Single(service.Scan(servers));

        var error = Assert.Throws<SecretsException>(() =>
            service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false));

        var path = Assert.Single(error.Paths);
        Assert.Contains("switched-off copy of github, changed since", path, StringComparison.Ordinal);
        Assert.Contains("changed-elsewhere", harness.ParkStore.Parked(ClientId.Cursor, "github")!.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldCopyNobodyTouchedIsPutBackAfterAFailedCommit()
    {
        using var harness = MakeHarness();
        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.VsCode);
        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.Cursor);
        var before = harness.ParkStore.Parked(ClientId.Cursor, "github")!.SourceText;
        var servers = harness.Discover().Servers;
        var service = new SecretsService(
            harness.Home,
            new ConfigWriter(harness.Backups, new FailingWriter()),
            harness.Ledger,
            new InMemorySecretStore(),
            parkStore: harness.ParkStore);
        var record = Assert.Single(service.Scan(servers));

        Assert.Throws<IOException>(() =>
            service.Rotate(record.Id, "ghp_brandnewtokenvalue", servers, alsoStore: false));

        Assert.Equal(before, harness.ParkStore.Parked(ClientId.Cursor, "github")!.SourceText);
    }

    private sealed class FailingWriter : IConfigFileWriter
    {
        public void Write(byte[] data, string path) => throw new IOException("Injected write failure.");

        public void Delete(string path) => File.Delete(path);
    }
}
