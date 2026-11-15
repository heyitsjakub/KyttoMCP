using System.Runtime.InteropServices;
using System.Text;

namespace Kytto.Core.Secrets;

/// <summary>Where Kytto keeps its own copy of a secret value.</summary>
/// <remarks>
/// An interface because the copy is a convenience, not the source of truth: the
/// config file holds the value the client actually reads. This is what makes a
/// value recoverable after a mistake, and it is what tests substitute.
/// </remarks>
public interface ISecretStore
{
    void Store(string value, string id);
    string? Value(string id);
    void Delete(string id);
    IReadOnlySet<string> StoredIdentifiers();
}

/// <summary>Windows Credential Manager.</summary>
/// <remarks>
/// The counterpart of Keychain on macOS, and what §6 names for this platform.
/// Credentials are stored per-user and encrypted by the OS with the user's logon
/// secret, so another account on the machine cannot read them even with disk
/// access — which is precisely the guarantee the config file itself cannot give.
/// </remarks>
public sealed class WindowsCredentialStore(string prefix = "Kytto") : ISecretStore
{
    private const int CredTypeGeneric = 1;

    /// <summary>Survives a reboot and stays on this machine; never roams.</summary>
    private const int CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    private string TargetFor(string id) => $"{prefix}:{id}";

    public void Store(string value, string id)
    {
        var blob = Encoding.Unicode.GetBytes(value);
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetFor(id),
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new SecretsException(
                    "Windows Credential Manager refused to store the value " +
                    $"(error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
            // The plaintext copy in managed memory is not worth leaving around any
            // longer than the call that needed it.
            Array.Clear(blob);
        }
    }

    public string? Value(string id)
    {
        if (!CredReadW(TargetFor(id), CredTypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return "";
            }
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Encoding.Unicode.GetString(blob);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Delete(string id)
    {
        if (CredDeleteW(TargetFor(id), CredTypeGeneric, 0)) return;
        // Already gone is the state the caller asked for.
        if (Marshal.GetLastWin32Error() == ErrorNotFound) return;
        throw new SecretsException(
            $"Could not remove the stored value (error {Marshal.GetLastWin32Error()}).");
    }

    public IReadOnlySet<string> StoredIdentifiers()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!CredEnumerateW($"{prefix}:*", 0, out var count, out var handle)) return result;

        try
        {
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.ReadIntPtr(handle, index * IntPtr.Size);
                var credential = Marshal.PtrToStructure<Credential>(entry);
                if (credential.TargetName is { } target && target.StartsWith($"{prefix}:", StringComparison.Ordinal))
                {
                    result.Add(target[(prefix.Length + 1)..]);
                }
            }
        }
        finally
        {
            CredFree(handle);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>A store that forgets everything when the process ends.</summary>
/// <remarks>
/// For tests. Nothing here reaches the tester's own Credential Manager, which
/// matters because the real one is shared with every other application.
/// </remarks>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Store(string value, string id) => _values[id] = value;

    public string? Value(string id) => _values.GetValueOrDefault(id);

    public void Delete(string id) => _values.Remove(id);

    public IReadOnlySet<string> StoredIdentifiers() =>
        _values.Keys.ToHashSet(StringComparer.Ordinal);
}

public sealed class SecretsException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>What a failed rotation could not put back, for <see cref="RollbackFailed"/>.</summary>
    public IReadOnlyList<string> Paths { get; private init; } = [];

    /// <summary>The backups taken before the rotation wrote anything.</summary>
    public IReadOnlyList<string> BackupIds { get; private init; } = [];

    public static SecretsException NotFound() =>
        new("That secret is no longer in any configuration.");

    /// <summary>
    /// A rotation failed and could not be undone everywhere. What is listed was left as
    /// it is — usually because something saved it after the rotation did (AUD-03).
    /// </summary>
    public static SecretsException RollbackFailed(
        IReadOnlyList<string> paths,
        IReadOnlyList<string> backupIds,
        Exception cause) =>
        new(
            $"The rotation failed ({cause.Message}) and could not be undone everywhere: " +
            string.Join(", ", paths) +
            (backupIds.Count > 0 ? ". Backups taken before the change: " + string.Join(", ", backupIds) : "") + ".",
            cause)
        {
            Paths = paths,
            BackupIds = backupIds,
        };

    public static SecretsException EmptyValue() =>
        new("A secret cannot be set to an empty value.");

    /// <summary>There is no configuration file to restrict.</summary>
    public static SecretsException NoConfigFile(string pathDisplay) =>
        new($"There is no configuration file at {pathDisplay}, so its permissions were not changed.");

    /// <summary>The permissions were set and did not take.</summary>
    public static SecretsException PermissionsUnchanged(string pathDisplay) =>
        new($"{pathDisplay} is still readable by other users — the file system did not accept the change.");
}
