using System.Text;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core.Tests.Support;

/// <summary>Every client counts as installed.</summary>
/// <remarks>
/// Detection is not what these tests are about, and a client that reads as absent
/// would make them pass for the wrong reason: an empty column looks a lot like a
/// working toggle that wrote nothing.
/// </remarks>
public sealed class AllInstalledLocator : IAppLocator
{
    public bool ApplicationExists(string installKey, string home) => true;
    public bool PackageExists(string familyName) => true;
    public bool ExecutableExists(string name, string home) => true;
}

/// <summary>
/// A whole Kytto, pointed at a throwaway profile directory.
/// </summary>
/// <remarks>
/// Everything it writes — configs, backups, the park store — lands inside
/// <see cref="Home"/> and is thrown away with it, so these tests exercise the real
/// write path without going near the tester's own configuration.
/// </remarks>
public sealed class ToggleHarness : IDisposable
{
    /// <summary>Where the registry's <c>%APPDATA%</c> lands under a fake profile.</summary>
    public const string ClaudeSupport = @"AppData\Roaming\Claude";
    public const string CodeUser = @"AppData\Roaming\Code\User";

    public ToggleHarness()
    {
        Home = Path.Combine(Path.GetTempPath(), "kytto-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Home);

        Paths = new KyttoPaths(Path.Combine(Home, "KyttoData"));
        Backups = new BackupStore(Paths);
        ParkStore = new ParkStore(Paths);
        Ledger = new DigestLedger();
        Toggles = new ToggleService(
            home: Home,
            writer: new ConfigWriter(Backups),
            parkStore: ParkStore,
            ledger: Ledger);
    }

    public string Home { get; }
    public KyttoPaths Paths { get; }
    public BackupStore Backups { get; }

    /// <summary>
    /// The same resolver the write services use, pointed at the throwaway profile.
    /// </summary>
    /// <remarks>
    /// Restoring needs one: the set of files a backup may be written to is derived
    /// from the registry, not read out of the backup's sidecar.
    /// </remarks>
    public ClientPathResolver Resolver(IReadOnlyDictionary<string, string>? overrides = null) =>
        new(Home, overrides);

    public ParkStore ParkStore { get; }
    public DigestLedger Ledger { get; }
    public ToggleService Toggles { get; }

    /// <summary>Runs discovery, which is also what primes the digest ledger.</summary>
    public DiscoveryResult Discover() =>
        new Discovery(
            home: Home,
            locator: new AllInstalledLocator(),
            recordDigest: (path, digest) => Ledger.Record(path, digest),
            parked: ParkStore.All()
                .Select(entry => new ParkedEntry(entry.ClientID, entry.ServerName, entry.SourceText))
                .ToArray()).Run();

    public Server Server(string name)
    {
        var found = Discover().Servers.FirstOrDefault(server => server.Name == name);
        Assert.True(found is not null, $"discovery found no server named \"{name}\"");
        return found!;
    }

    public void Write(string contents, string relativePath)
    {
        var full = Path.Combine(Home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(contents));
    }

    public string Text(string relativePath) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Home, relativePath)));

    public bool Exists(string relativePath) => File.Exists(Path.Combine(Home, relativePath));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over — and a
            // test that made a reparse point is exactly where the recursive delete
            // refuses.
        }
    }
}
