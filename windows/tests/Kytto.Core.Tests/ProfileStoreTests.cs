using Kytto.Core.Profiles;
using Kytto.Core.Health;
using Kytto.Core.Settings;

namespace Kytto.Core.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kytto-profile-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void ReadingProfilesWritesNothing()
    {
        Assert.Empty(new ProfileStore(new KyttoPaths(_root)).All());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void CreateReadUpdateDeleteRoundTripsIncludingAnEmptyProfile()
    {
        var paths = new KyttoPaths(_root);
        var store = new ProfileStore(paths);
        var created = Assert.Single(store.Create("  Minimal  ", [], 250_000));

        Assert.Equal("Minimal", created.Name);
        Assert.Empty(created.ServerIDs);
        Assert.Equal(TokenWeight.ReferenceContextWindow, created.TokenBudget);
        Assert.True(File.Exists(paths.ProfilesFile));
        Assert.Equal(created, Assert.Single(new ProfileStore(paths).All()));

        var updated = Assert.Single(store.Update(
            created.Id,
            "Coding",
            [" GitHub ", "github", "FILEsystem"],
            15_000));
        Assert.Equal(["github", "filesystem"], updated.ServerIDs);
        Assert.Equal(15_000, updated.TokenBudget);
        Assert.Empty(store.Delete(created.Id));
    }

    [Fact]
    public void NamesAreUniqueIgnoringCaseAndLimited()
    {
        var store = new ProfileStore(new KyttoPaths(_root));
        store.Create("Coding", []);

        Assert.Throws<ProfileException>(() => store.Create(" coding ", []));
        Assert.Throws<ProfileException>(() => store.Create("", []));
        Assert.Throws<ProfileException>(() => store.Create(new string('x', 81), []));
    }

    [Fact]
    public void RenameUpdatesAndDeduplicatesReferences()
    {
        var store = new ProfileStore(new KyttoPaths(_root));
        var profile = Assert.Single(store.Create("Coding", ["old", "new"]));

        store.RenameServer("old", "New");

        Assert.Equal(["new"], store.Get(profile.Id).ServerIDs);
    }

    [Fact]
    public void ACorruptStoreIsVisibleAsEmptyButNeverOverwrittenByAMutation()
    {
        var paths = new KyttoPaths(_root);
        Directory.CreateDirectory(_root);
        var original = new byte[] { 0xff, 0xfe, (byte)'{', (byte)'x' };
        File.WriteAllBytes(paths.ProfilesFile, original);
        var store = new ProfileStore(paths);

        Assert.Empty(store.All());
        var error = Assert.Throws<ProfileException>(() => store.Create("Coding", ["github"]));

        Assert.Contains("left untouched", error.Message, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(paths.ProfilesFile));
    }
}
