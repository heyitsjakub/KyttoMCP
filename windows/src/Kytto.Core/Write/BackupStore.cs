using System.Globalization;
using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Settings;

namespace Kytto.Core;

public sealed record Backup(
    string Id,
    ClientId ClientID,
    /// <summary>The file this was taken from.</summary>
    string OriginalPathDisplay,
    DateTimeOffset TakenAt,
    long ByteCount,
    string Path,
    string OriginalPath);

/// <summary>A restore that was refused, reported to the web layer as <c>backup</c>.</summary>
/// <remarks>
/// Its own type rather than a <see cref="ConfigWriteException"/> because it is not a
/// write that failed — it is a write that never started, and the UI's wording for
/// the two is not the same.
/// </remarks>
public sealed class BackupException(string message) : Exception(message)
{
    /// <summary>The backup's recorded origin is not a file Kytto writes for its client.</summary>
    public static BackupException NotAManagedFile(string pathDisplay) => new(
        $"{pathDisplay} is not a configuration file Kytto manages for this client, so the " +
        "backup was not restored. If the client's config location was changed in Settings, " +
        "the backup is still in the backups folder.");
}

/// <summary>
/// A copy of every config Kytto is about to change, kept where the user can get at
/// it (§6.1, §6.5).
/// </summary>
/// <remarks>
/// This is a trust feature before it is a safety feature. "Revert" being visible in
/// the UI is the difference between a tool people let near their config and one
/// they uninstall.
/// </remarks>
public sealed class BackupStore(KyttoPaths paths, int retentionPerClient = BackupStore.DefaultRetentionPerClient)
{
    public const int DefaultRetentionPerClient = 20;
    private readonly Lock _lock = new();

    /// <summary>How many to keep per client. Configurable in Settings (§7.6).</summary>
    public int RetentionPerClient { get; } = retentionPerClient;

    private string Directory(ClientId client) => Path.Combine(paths.Backups, client.Raw());

    /// <summary>
    /// Copies a file aside and prunes anything past the retention limit. Null when
    /// there was nothing to back up yet.
    /// </summary>
    public Backup? BackUp(string path, ClientId client, string pathDisplay)
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return null;

            // Private before anything is written into it, and the whole chain rather
            // than the leaf: these copies hold every `env` block the config did, API
            // keys included (§6).
            var directory = Directory(client);
            KyttoStorage.EnsurePrivate(paths.Root, directory);

            var takenAt = DateTimeOffset.Now;
            var stem = $"{Path.GetFileName(path)}.{Stamp(takenAt)}";
            var name = stem + ".bak";
            var destination = Path.Combine(directory, name);
            for (var suffix = 1; File.Exists(destination); suffix++)
            {
                name = $"{stem}-{suffix:D3}.bak";
                destination = Path.Combine(directory, name);
            }

            var data = File.ReadAllBytes(path);
            AtomicWriter.Write(data, destination);

            // The path the backup came from is not recoverable from the file name once
            // a client moves its config, so it is recorded alongside.
            AtomicWriter.Write(Encoding.UTF8.GetBytes(Path.GetFullPath(path)), destination + ".origin");

            Prune(client);

            return new Backup(
                Id: name,
                ClientID: client,
                OriginalPathDisplay: pathDisplay,
                TakenAt: takenAt,
                ByteCount: data.Length,
                Path: destination,
                OriginalPath: path);
        }
    }

    public IReadOnlyList<Backup> List(ClientId client)
    {
        var directory = Directory(client);
        string[] entries;
        try
        {
            entries = System.IO.Directory.GetFiles(directory, "*.bak");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var result = new List<Backup>();
        foreach (var path in entries)
        {
            var info = new FileInfo(path);
            string? originPath = null;
            try
            {
                originPath = File.ReadAllText(path + ".origin", Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A backup whose sidecar went missing is still restorable by name.
            }

            result.Add(new Backup(
                Id: Path.GetFileName(path),
                ClientID: client,
                OriginalPathDisplay: originPath is not null ? Abbreviate(originPath) : Path.GetFileName(path),
                TakenAt: info.LastWriteTimeUtc,
                ByteCount: info.Length,
                Path: path,
                OriginalPath: originPath ?? ""));
        }

        result.Sort((left, right) =>
        {
            var byTime = right.TakenAt.CompareTo(left.TakenAt);
            return byTime != 0
                ? byTime
                : string.Compare(right.Id, left.Id, StringComparison.Ordinal);
        });
        return result;
    }

    public Backup? Get(string id, ClientId client) =>
        List(client).FirstOrDefault(backup => backup.Id == id);

    /// <summary>
    /// Read-only preflight hook for sidecar state coupled to a config definition,
    /// such as Gateway routes.
    /// </summary>
    public string Contents(Backup backup) =>
        File.ReadAllText(backup.Path, new UTF8Encoding(false, true));

    /// <summary>
    /// Where this backup is allowed to go, resolved from the registry.
    /// </summary>
    /// <remarks>
    /// The sidecar says where the backup came from; the resolver says whether that is
    /// still a file Kytto writes for this client, and answers with <em>its own</em>
    /// spelling of the path. Everything downstream uses the answer rather than the
    /// question, because comparing paths is lexical and following them is not.
    /// </remarks>
    public static string RestoreTarget(Backup backup, ClientPathResolver resolver)
    {
        if (string.IsNullOrEmpty(backup.OriginalPath))
        {
            throw new InvalidOperationException(
                $"{backup.Id} has no record of which file it came from, so it cannot be put back.");
        }

        return resolver.WriteTarget(backup.OriginalPath, backup.ClientID)
            ?? throw BackupException.NotAManagedFile(backup.OriginalPathDisplay);
    }

    /// <summary>
    /// Puts a backup back, atomically, after backing up what is there now — so
    /// reverting is itself reversible.
    /// </summary>
    public void Restore(Backup backup, ClientPathResolver resolver)
    {
        RestoreCore(backup, expectedCurrentDigest: null, enforceExpected: false, resolver);
    }

    /// <summary>Restores only if the caller's last-read file state is still current.</summary>
    public void RestoreChecked(Backup backup, string? expectedCurrentDigest, ClientPathResolver resolver)
    {
        RestoreCore(backup, expectedCurrentDigest, enforceExpected: true, resolver);
    }

    private void RestoreCore(
        Backup backup,
        string? expectedCurrentDigest,
        bool enforceExpected,
        ClientPathResolver resolver)
    {
        // First, and before any of it: a refused restore writes nothing at all — no
        // pre-restore backup, no temporary file, no stream.
        var target = RestoreTarget(backup, resolver);

        if (enforceExpected && Digest.OfFile(target) != expectedCurrentDigest)
        {
            throw ConfigWriteException.ChangedOnDisk(backup.OriginalPathDisplay);
        }

        var data = File.ReadAllBytes(backup.Path);
        BackUp(target, backup.ClientID, backup.OriginalPathDisplay);
        AtomicWriter.Write(data, target);
    }

    private void Prune(ClientId client)
    {
        var all = List(client);
        if (all.Count <= RetentionPerClient) return;

        foreach (var backup in all.Skip(RetentionPerClient))
        {
            Delete(backup.Path);
            Delete(backup.Path + ".origin");
        }

        static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A backup that will not delete is not worth failing a write over.
            }
        }
    }

    /// <summary>Sortable, filename-safe, and readable at a glance in Explorer.</summary>
    private static string Stamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd-HHmmss-fff", CultureInfo.InvariantCulture);

    private static string Abbreviate(string path)
    {
        var home = KyttoPaths.Home;
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[home.Length..]
            : path;
    }
}
