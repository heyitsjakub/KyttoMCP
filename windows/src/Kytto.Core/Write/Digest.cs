using System.Security.Cryptography;

namespace Kytto.Core;

/// <summary>
/// What a config file looked like when Kytto last read it.
/// </summary>
/// <remarks>
/// The whole point of §6.4: before writing, compare the file on disk against this.
/// If it differs, somebody else edited it and Kytto refuses rather than merging.
/// Only ever compared against itself, so the algorithm matters less than the fact
/// that it is over the exact bytes rather than a parsed or re-encoded view.
/// </remarks>
public static class Digest
{
    public static string Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The digest of a file, or null when there is no file.</summary>
    public static string? OfFile(string path)
    {
        try
        {
            return Of(File.ReadAllBytes(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
