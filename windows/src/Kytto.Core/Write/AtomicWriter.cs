using System.Text;

namespace Kytto.Core;

/// <summary>
/// Writes a file so that a reader sees either the old contents or the new ones,
/// never half of either (§6.2).
/// </summary>
/// <remarks>
/// <para>
/// Temp file in the same directory, flushed to the device, then renamed over the
/// target. Never truncate-and-write in place: a client reading its config while
/// Kytto is halfway through would see a truncated file, and a crash at the wrong
/// moment would leave one on disk.
/// </para>
/// <para>
/// Same directory specifically, because a rename is only atomic within a volume —
/// a temp file under <c>%TEMP%</c> can land on a different one.
/// </para>
/// <para>
/// The same directory is also what makes the staging window a non-issue on Windows.
/// A file created there inherits that folder's DACL at creation, so a backup staged
/// inside Kytto's private backups folder is private before it holds a byte, and a
/// config staged beside its own config is exactly as private as its folder. There is
/// no mode to tighten afterwards and therefore no moment when the bytes are readable
/// and the permissions are not (§6).
/// </para>
/// </remarks>
public static class AtomicWriter
{
    /// <summary>What the destination must still hold at the moment it is replaced.</summary>
    /// <remarks>
    /// Checked after the new bytes are staged and flushed, immediately before the
    /// swap — as late as a check can happen without a lock other programs would have
    /// to honour. It narrows the window in which another program's save can be
    /// overwritten; it cannot close it completely (AUD-02).
    /// </remarks>
    /// <param name="Digest">
    /// The file must still have exactly these bytes (SHA-256, hex). Null means there
    /// must be no file, which the final move enforces by itself: it never overwrites.
    /// </param>
    public readonly record struct Precondition(string? Digest)
    {
        public static Precondition Absent => new(null);
    }

    public static void Write(string text, string path) =>
        Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), path);

    /// <param name="throughLinks">
    /// Write to the file a symbolic link names rather than replacing the link. True
    /// for a client's config, which people keep in a dotfiles folder and link into
    /// place: replacing the link with a plain file would quietly fork their config.
    /// False for Kytto's own storage, where a link nobody made has no business
    /// steering a write somewhere else.
    /// </param>
    /// <param name="expecting">
    /// When set, the write is refused with <see cref="TargetChangedException"/> unless
    /// the destination still matches; nothing is replaced and no staged file is left.
    /// </param>
    public static void Write(byte[] data, string path, bool throughLinks = false, Precondition? expecting = null)
    {
        if (throughLinks) path = ResolvingLinks(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException($"'{path}' has no directory to write into.", nameof(path));
        }
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.Write(data);
                // Ordered onto the device before the rename, so a power loss cannot
                // leave the name pointing at a file whose contents never arrived.
                stream.Flush(flushToDisk: true);
            }

            if (expecting is { Digest: { } expected })
            {
                if (Digest.OfFile(path) != expected) throw new TargetChangedException(path);
                File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else if (expecting is not null)
            {
                try
                {
                    File.Move(temporary, path, overwrite: false);
                }
                catch (IOException) when (File.Exists(path))
                {
                    throw new TargetChangedException(path);
                }
            }
            else if (File.Exists(path))
            {
                // Replace rather than Move: it keeps the destination's ACLs and
                // creation time, which matters because restricting a config's
                // permissions is a thing Kytto offers to do (§6). `File.Move(…, true)`
                // is `MoveFileExW`, which keeps the *temporary* file's inherited ACL
                // instead and would silently undo that restriction on the next write.
                File.Replace(temporary, path, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            // A failed Replace leaves the temp file behind; it is ours, so it goes.
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Losing a temp file is not worth failing a write that succeeded.
            }
        }
    }

    /// <summary>
    /// The file <paramref name="path"/> finally names, when its last component is a
    /// symbolic link; otherwise the path itself.
    /// </summary>
    /// <remarks>
    /// A link whose target does not exist yet still resolves, so the first write
    /// creates the target and leaves the link in place. The temporary file is then
    /// staged beside the target, which is what keeps the replace on one volume.
    /// </remarks>
    public static string ResolvingLinks(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null) return path;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>
/// The destination no longer matched the <see cref="AtomicWriter.Precondition"/>, so
/// nothing was replaced.
/// </summary>
/// <remarks>
/// An <see cref="IOException"/>, so a rollback that runs into one counts the file as
/// not restored, the way it counts any other file it could not write.
/// </remarks>
public sealed class TargetChangedException(string path)
    : IOException($"{Path.GetFileName(path)} changed while Kytto was writing it, so it was left as it is.");
