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
    public static void Write(string text, string path) =>
        Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), path);

    public static void Write(byte[] data, string path)
    {
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

            if (File.Exists(path))
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
}
