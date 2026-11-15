using System.Text;
using System.Runtime.ExceptionServices;
using Kytto.Core.Clients;

namespace Kytto.Core;

/// <summary>
/// What the write pipeline needs to know about a config file, whatever it is
/// spelled in.
/// </summary>
/// <remarks>
/// Deliberately tiny. The interesting operations — set this key, remove that
/// table — differ enough between JSON and TOML that pretending they are the same
/// call would produce a worst-of-both abstraction. What genuinely is the same is
/// the §6 pipeline around them: read, check nobody else moved it, transform, check
/// the result still parses, write atomically. That is all this covers.
/// </remarks>
public interface IConfigDocument<TSelf> where TSelf : IConfigDocument<TSelf>
{
    /// <summary>What a client that has never configured MCP starts from.</summary>
    static abstract string EmptySource { get; }

    /// <summary>For the message when an edit produces something unparseable.</summary>
    static abstract string FormatName { get; }

    static abstract TSelf Parse(string text);

    string SourceText { get; }
}

/// <param name="Digest">
/// Digest of the file after the write, so the caller can keep tracking it.
/// </param>
public sealed record WriteReceipt(
    string Digest,
    string? BackupID,
    string PathDisplay,
    /// <summary>
    /// True when this operation replaced or created the destination. A missing
    /// backup only means there were no previous bytes; it does not mean no write
    /// happened (§6).
    /// </summary>
    bool DidWrite);

/// <summary>The replace/delete primitive used by the transactional writer.</summary>
/// <remarks>
/// Kept injectable so rollback is tested with a deterministic failed write rather
/// than with machine-specific ACL behaviour (§6.2).
/// </remarks>
public interface IConfigFileWriter
{
    void Write(byte[] data, string path);
    void Delete(string path);

    /// <summary>
    /// Writes only while the destination still holds <paramref name="expectedDigest"/>
    /// — or, for null, still does not exist (AUD-02).
    /// </summary>
    /// <remarks>
    /// The default checks and then writes, which is what a test double needs; the real
    /// writer checks after staging, immediately before the swap.
    /// </remarks>
    /// <exception cref="TargetChangedException">The destination changed.</exception>
    void Write(byte[] data, string path, string? expectedDigest)
    {
        if (Digest.OfFile(path) != expectedDigest) throw new TargetChangedException(path);
        Write(data, path);
    }
}

public sealed class AtomicConfigFileWriter : IConfigFileWriter
{
    // Through a symbolic link, to the file it names: the link is the user's.
    public void Write(byte[] data, string path) => AtomicWriter.Write(data, path, throughLinks: true);

    public void Write(byte[] data, string path, string? expectedDigest) =>
        AtomicWriter.Write(data, path, throughLinks: true, new AtomicWriter.Precondition(expectedDigest));

    /// <summary>Rolls back a file a failed transaction created.</summary>
    /// <remarks>
    /// When the path is a link whose target was missing, the write created the
    /// target: that goes, and the user's link stays as it was.
    /// </remarks>
    public void Delete(string path) => File.Delete(AtomicWriter.ResolvingLinks(path));
}

public sealed class ConfigWriteException(string message) : Exception(message)
{
    /// <summary>
    /// The write was refused because the file's digest no longer matches what Kytto
    /// last read. Recoverable by reading it again, so it travels with its own code.
    /// </summary>
    public bool IsChangedOnDisk { get; private init; }

    /// <summary>Someone else changed the file since Kytto last read it.</summary>
    public static ConfigWriteException ChangedOnDisk(string pathDisplay) =>
        new($"{pathDisplay} changed on disk since Kytto last read it.") { IsChangedOnDisk = true };

    public static ConfigWriteException Unreadable(string pathDisplay, string reason) =>
        new($"Could not read {pathDisplay}: {reason}");
}

public sealed class ConfigTransactionException(
    string message,
    IReadOnlyList<string> paths,
    Exception innerException,
    IReadOnlyList<string>? backupIds = null) : Exception(message, innerException)
{
    public IReadOnlyList<string> Paths { get; } = paths;

    /// <summary>The backups taken before the failed write: the way back by hand (AUD-03).</summary>
    public IReadOnlyList<string> BackupIds { get; } = backupIds ?? [];
}

/// <summary>A fully parsed and validated config edit which has not written yet.</summary>
public sealed class PreparedConfigWrite
{
    internal PreparedConfigWrite(
        string path,
        ClientId clientId,
        string pathDisplay,
        bool existed,
        byte[]? originalBytes,
        string? originalDigest,
        byte[] updatedBytes,
        string updatedDigest,
        bool hasChanges)
    {
        Path = path;
        ClientID = clientId;
        PathDisplay = pathDisplay;
        Existed = existed;
        OriginalBytes = originalBytes;
        OriginalDigest = originalDigest;
        UpdatedBytes = updatedBytes;
        UpdatedDigest = updatedDigest;
        HasChanges = hasChanges;
    }

    public string Path { get; }
    public ClientId ClientID { get; }
    public string PathDisplay { get; }
    public bool HasChanges { get; }
    internal bool Existed { get; }
    internal byte[]? OriginalBytes { get; }
    internal string? OriginalDigest { get; }
    internal byte[] UpdatedBytes { get; }
    internal string UpdatedDigest { get; }
}

/// <summary>The one path through which a client's config is ever modified.</summary>
/// <remarks>
/// Every write goes: check nobody else changed the file, back it up, splice, and
/// swap it in atomically. There is no shortcut around this — §6 exists because the
/// alternative is corrupting a file someone depends on.
/// </remarks>
public sealed class ConfigWriter
{
    private readonly BackupStore _backups;
    private readonly IConfigFileWriter _fileWriter;
    // Every ConfigWriter instance participates in the same last-check/write
    // critical section. App services are intentionally short-lived, but config
    // mutations still need one process-wide coordinator (§6.4).
    private static readonly Lock CommitLock = new();

    public ConfigWriter(BackupStore backups, IConfigFileWriter? fileWriter = null)
    {
        _backups = backups;
        _fileWriter = fileWriter ?? new AtomicConfigFileWriter();
    }

    /// <summary>Applies a transform to the file and writes the result.</summary>
    /// <param name="expectedDigest">
    /// The digest Kytto saw when it last read the file. Pass null only when the
    /// file is not expected to exist. If the file's current digest disagrees, the
    /// write is refused rather than merged — §6.4: do not merge blindly, ask.
    /// </param>
    /// <typeparam name="TDocument">
    /// The document type to parse the file as. Comes from the client's registry
    /// entry, so which spelling a client uses stays data in one file rather than an
    /// assumption spread through the pipeline.
    /// </typeparam>
    public WriteReceipt Edit<TDocument>(
        string path,
        ClientId clientId,
        string pathDisplay,
        string? expectedDigest,
        Func<TDocument, string> transform)
        where TDocument : IConfigDocument<TDocument>
    {
        var prepared = Prepare(path, clientId, pathDisplay, expectedDigest, transform);
        return Commit([prepared])[0];
    }

    /// <summary>
    /// Resolves, reads, digest-checks, parses and validates an edit without writing.
    /// A caller can prepare every target first and commit them as one transaction.
    /// </summary>
    public PreparedConfigWrite Prepare<TDocument>(
        string path,
        ClientId clientId,
        string pathDisplay,
        string? expectedDigest,
        Func<TDocument, string> transform)
        where TDocument : IConfigDocument<TDocument>
    {
        // Read once. The digest check, the transform, the backup and the final conflict
        // check all refer to these bytes; reading the file again for any of them is how
        // an edit made in between could be checked as one version and written over as
        // another (AUD-02).
        byte[]? originalBytes;
        try
        {
            originalBytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            originalBytes = null;
        }
        var exists = originalBytes is not null;
        var currentDigest = originalBytes is null ? null : Digest.Of(originalBytes);

        if (currentDigest != expectedDigest)
        {
            throw ConfigWriteException.ChangedOnDisk(pathDisplay);
        }

        string source;
        if (exists)
        {
            try
            {
                source = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(originalBytes!);
            }
            catch (DecoderFallbackException)
            {
                throw ConfigWriteException.Unreadable(pathDisplay, "not valid UTF-8");
            }
        }
        else
        {
            // A client that has never configured MCP has no file. Starting from the
            // format's empty document keeps the rest of the pipeline identical.
            source = TDocument.EmptySource;
        }

        TDocument document;
        try
        {
            document = TDocument.Parse(source);
        }
        catch (Exception error) when (error is not ConfigWriteException)
        {
            throw ConfigWriteException.Unreadable(pathDisplay, error.Message);
        }

        var updated = transform(document);

        // Parse what we are about to write. If the edit produced something
        // unparseable, the bug stops here rather than in the user's config.
        try
        {
            _ = TDocument.Parse(updated);
        }
        catch (Exception)
        {
            throw ConfigWriteException.Unreadable(
                pathDisplay,
                $"the edit would have produced invalid {TDocument.FormatName}, so nothing was written");
        }

        var updatedBytes = Encoding.UTF8.GetBytes(updated);
        return new PreparedConfigWrite(
            path,
            clientId,
            pathDisplay,
            exists,
            originalBytes,
            currentDigest,
            updatedBytes,
            Digest.Of(updatedBytes),
            updated != source);
    }

    /// <summary>Commits prepared edits together, rolling back earlier writes on failure.</summary>
    public IReadOnlyList<WriteReceipt> Commit(IReadOnlyList<PreparedConfigWrite> writes)
    {
        // Preparation may run concurrently, but the last drift check and writes
        // must not. Otherwise two edits of the same source can both validate the
        // old digest before either writes, and the last one silently erases the
        // first — exactly the blind merge §6.4 forbids.
        lock (CommitLock)
        {
            return CommitLocked(writes);
        }
    }

    private IReadOnlyList<WriteReceipt> CommitLocked(IReadOnlyList<PreparedConfigWrite> writes)
    {
        var duplicate = writes
            .GroupBy(write => Path.GetFullPath(write.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"A config transaction contains the same path more than once: {duplicate.Key}",
                nameof(writes));
        }

        // The last drift check is over every target and happens before the first
        // backup or write. A long edit form must not make the first file move before
        // Kytto notices that the last one changed (§6.4).
        foreach (var write in writes)
        {
            if (Digest.OfFile(write.Path) != write.OriginalDigest)
            {
                throw ConfigWriteException.ChangedOnDisk(write.PathDisplay);
            }
        }

        // The bytes each edit started from, not whatever the file holds now: the
        // backup must describe what the transform read (AUD-02).
        var backupIds = new Dictionary<PreparedConfigWrite, string?>();
        foreach (var write in writes.Where(candidate => candidate.HasChanges))
        {
            backupIds[write] = write.OriginalBytes is { } original
                ? _backups.BackUp(write.Path, write.ClientID, write.PathDisplay, original)?.Id
                : null;
        }

        var attempted = new List<PreparedConfigWrite>();
        try
        {
            foreach (var write in writes.Where(candidate => candidate.HasChanges))
            {
                // Record before entering the injected primitive: it may fail after
                // replacing the destination, and that still needs rollback.
                attempted.Add(write);
                // Another program does not take Kytto's lock, so the file is checked
                // once more at the last moment: it must still hold what was read, or
                // still not exist. A save in between is refused, not overwritten.
                try
                {
                    _fileWriter.Write(write.UpdatedBytes, write.Path, write.OriginalDigest);
                }
                catch (TargetChangedException)
                {
                    // Nothing of this one was written; only the earlier ones roll back.
                    attempted.Remove(write);
                    throw ConfigWriteException.ChangedOnDisk(write.PathDisplay);
                }
            }
        }
        catch (Exception error)
        {
            var incomplete = RollBack(attempted);
            if (incomplete.Count > 0)
            {
                var backups = backupIds.Values.OfType<string>().ToArray();
                throw new ConfigTransactionException(
                    $"CRITICAL: the operation failed: {error.Message} " +
                    "Rollback could not safely restore: " + string.Join(", ", incomplete) +
                    (backups.Length > 0 ? ". Backups taken before the change: " + string.Join(", ", backups) : ""),
                    incomplete,
                    error,
                    backups);
            }
            ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }

        return writes.Select(write => new WriteReceipt(
            write.HasChanges ? write.UpdatedDigest : write.OriginalDigest ?? write.UpdatedDigest,
            backupIds.GetValueOrDefault(write),
            write.PathDisplay,
            write.HasChanges)).ToArray();
    }

    private IReadOnlyList<string> RollBack(IEnumerable<PreparedConfigWrite> attempted)
    {
        var incomplete = new List<string>();
        foreach (var write in attempted.Reverse())
        {
            try
            {
                var current = Digest.OfFile(write.Path);
                if (current == write.OriginalDigest) continue;

                // Never overwrite a newer external change. Only the exact bytes
                // this transaction produced are ours to undo (§6.4, AUD-03).
                if (current != write.UpdatedDigest)
                {
                    incomplete.Add($"{write.PathDisplay} (changed by another app since; left as it is)");
                    continue;
                }

                if (write.Existed)
                {
                    // And checked again at the swap, for a save that lands in between.
                    _fileWriter.Write(write.OriginalBytes!, write.Path, write.UpdatedDigest);
                }
                else
                {
                    _fileWriter.Delete(write.Path);
                }

                if (Digest.OfFile(write.Path) != write.OriginalDigest)
                {
                    incomplete.Add(write.PathDisplay);
                }
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                incomplete.Add(write.PathDisplay);
            }
        }
        return incomplete.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

/// <summary>What Kytto believes is currently on disk, per file.</summary>
/// <remarks>
/// <para>
/// Discovery records a digest for every file it reads; every write updates it.
/// Before writing, <see cref="ConfigWriter"/> checks the file still matches — if it
/// does not, someone edited it behind Kytto's back and the change is refused
/// rather than merged (§6.4).
/// </para>
/// <para>
/// Deliberately in memory only. Persisting it would mean a stale record from a
/// previous launch could wave through a write over changes made while Kytto was
/// closed. Starting each session by reading the truth is both simpler and safer.
/// </para>
/// </remarks>
public sealed class DigestLedger
{
    private readonly Dictionary<string, string> _digests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Record(string path, string? digest)
    {
        var key = Normalize(path);
        lock (_lock)
        {
            if (digest is null) _digests.Remove(key);
            else _digests[key] = digest;
        }
    }

    public string? DigestFor(string path)
    {
        lock (_lock)
        {
            return _digests.GetValueOrDefault(Normalize(path));
        }
    }

    /// <summary>True when the file on disk is not what Kytto last saw.</summary>
    public bool HasDrifted(string path) => Digest.OfFile(path) != DigestFor(path);

    public void ForgetAll()
    {
        lock (_lock)
        {
            _digests.Clear();
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
