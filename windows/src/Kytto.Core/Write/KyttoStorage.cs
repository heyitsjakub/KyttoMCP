using System.Security.AccessControl;
using System.Security.Principal;
using Kytto.Core.Settings;

namespace Kytto.Core;

/// <summary>
/// Kytto's own data directory, kept readable by this account and nobody else (§6).
/// </summary>
/// <remarks>
/// <para>
/// The backups tree holds whole copies of client configs, <c>env</c> blocks and API
/// keys included, and the parked store holds a server's definition verbatim for
/// exactly as long as it is switched off. Both are Kytto's copies of something the
/// client itself keeps private, so they have to be at least as private as the
/// original — otherwise "Kytto took a backup first" is the sentence that leaked the
/// token.
/// </para>
/// <para>
/// Windows has no mode bits, so the macOS <c>0600</c>/<c>0700</c> is spelled as a
/// <em>protected</em> DACL: inheritance from the parent switched off, and
/// FullControl for this account, <c>SYSTEM</c> and <c>BUILTIN\Administrators</c> and
/// no one else. The last two are not a concession — they can reach any file on the
/// machine regardless — and listing them keeps the app repairable and backup
/// software working.
/// </para>
/// <para>
/// Folders are created <em>with</em> that descriptor rather than tightened after the
/// fact, so a file written inside one inherits it at creation. That is what closes
/// the window macOS also had: a temporary file must never hold config bytes while it
/// still grants broader access, not even under a name nothing has read yet.
/// </para>
/// </remarks>
public static class KyttoStorage
{
    /// <summary>Creates or tightens Kytto's data root before something writes into it.</summary>
    public static void EnsurePrivateRoot(KyttoPaths paths) => EnsurePrivate(paths.Root, paths.Root);

    /// <summary>
    /// Creates or tightens every Kytto-owned folder from <paramref name="root"/> down
    /// to <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// The whole chain rather than the leaf, because the root is shared with stores
    /// that may have created it in an earlier version with whatever
    /// <c>%APPDATA%</c> happened to hand down — a private per-client folder inside a
    /// world-readable root is not private.
    /// </remarks>
    public static void EnsurePrivate(string root, string directory)
    {
        if (CurrentUser() is not { } self) return;

        foreach (var step in Chain(root, directory))
        {
            try
            {
                if (Directory.Exists(step))
                {
                    if (!IsReparsePoint(step)) TightenDirectory(step, self);
                }
                else
                {
                    PrivateDirectorySecurity(self).CreateDirectory(step);
                }
            }
            catch (Exception error) when (IsExpected(error))
            {
                // A folder whose ACL will not move is still a folder Kytto has to be
                // able to write into. The caller's own write reports the failure that
                // matters; making one unreadable descriptor fail every backup would
                // trade a low-severity exposure for a broken product.
                Directory.CreateDirectory(step);
            }
        }
    }

    /// <summary>
    /// Tightens what earlier versions left behind, and returns how many items changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs at launch, before the page can send its first command, and creates
    /// nothing: a profile that has never run Kytto stays a profile that has never run
    /// Kytto (§6.6). There is no marker file either — it reads the tree on every
    /// launch and, once the tree is private, writes nothing, which is both cheaper
    /// than the bookkeeping and correct when a folder loosens again later.
    /// </para>
    /// <para>
    /// Reparse points are skipped rather than followed. A junction or symlink inside
    /// the backups folder points at something that is not Kytto's to re-permission,
    /// and following one would turn a tidy-up into a way to rewrite the ACL of any
    /// path the user can name.
    /// </para>
    /// </remarks>
    public static int TightenExisting(KyttoPaths paths)
    {
        if (!Directory.Exists(paths.Root)) return 0;
        if (CurrentUser() is not { } self) return 0;

        var changed = 0;
        if (!IsReparsePoint(paths.Root))
        {
            changed += TightenDirectory(paths.Root, self) ? 1 : 0;
            changed += TightenTree(paths.Backups, self);
            if (File.Exists(paths.ParkFile) && !IsReparsePoint(paths.ParkFile))
            {
                changed += TightenFile(paths.ParkFile, self) ? 1 : 0;
            }
        }
        return changed;
    }

    /// <summary>
    /// Tightens one of Kytto's own files, if it exists and grants anyone else.
    /// </summary>
    /// <remarks>
    /// Needed before rewriting a file rather than after, because
    /// <see cref="AtomicWriter"/> replaces rather than moves and therefore carries the
    /// destination's DACL onto the new contents. That is right for a client's config
    /// — the user's own lockdown must survive a Kytto write — and wrong for Kytto's
    /// own stores, where a loose descriptor from an earlier version would otherwise
    /// outlive every rewrite.
    /// </remarks>
    public static bool TightenOwnFile(string path)
    {
        if (!File.Exists(path) || IsReparsePoint(path)) return false;
        return CurrentUser() is { } self && TightenFile(path, self);
    }

    /// <summary>True when nothing but this account, SYSTEM or the administrators is allowed.</summary>
    /// <remarks>
    /// The question is "does this grant anyone else access", not "is it protected".
    /// A file inside a protected private folder inherits exactly the right entries
    /// and is not itself protected, so requiring protection would make the migration
    /// rewrite every backup on every launch.
    /// </remarks>
    public static bool IsPrivate(string path)
    {
        if (CurrentUser() is not { } self) return true;
        try
        {
            var security = Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            return GrantsNobodyElse(security, self);
        }
        catch (Exception error) when (IsExpected(error))
        {
            // An ACL that cannot be read is not evidence of anything.
            return true;
        }
    }

    // MARK: - The tree

    private static int TightenTree(string directory, SecurityIdentifier self)
    {
        if (!Directory.Exists(directory) || IsReparsePoint(directory)) return 0;

        var changed = TightenDirectory(directory, self) ? 1 : 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (IsReparsePoint(file)) continue;
                if (TightenFile(file, self)) changed++;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                changed += TightenTree(child, self);
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            // Best effort: one unreadable folder must not stop the launch.
        }
        return changed;
    }

    private static bool TightenDirectory(string path, SecurityIdentifier self)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (GrantsNobodyElse(info.GetAccessControl(AccessControlSections.Access), self)) return false;
            info.SetAccessControl(PrivateDirectorySecurity(self));
            return true;
        }
        catch (Exception error) when (IsExpected(error))
        {
            return false;
        }
    }

    private static bool TightenFile(string path, SecurityIdentifier self)
    {
        try
        {
            var info = new FileInfo(path);
            if (GrantsNobodyElse(info.GetAccessControl(AccessControlSections.Access), self)) return false;
            info.SetAccessControl(PrivateFileSecurity(self));
            return true;
        }
        catch (Exception error) when (IsExpected(error))
        {
            return false;
        }
    }

    // MARK: - Descriptors

    private static DirectorySecurity PrivateDirectorySecurity(SecurityIdentifier self)
    {
        // Stated from nothing rather than edited down from what is there. Editing
        // means enumerating and removing, and one missed entry leaves the folder
        // readable — silently, which is the failure mode that matters here.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in Principals(self))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                principal,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity PrivateFileSecurity(SecurityIdentifier self)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in Principals(self))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                principal, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    private static IEnumerable<SecurityIdentifier> Principals(SecurityIdentifier self)
    {
        yield return self;
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }

    private static bool GrantsNobodyElse(FileSystemSecurity security, SecurityIdentifier self)
    {
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;

            var identity = (SecurityIdentifier)rule.IdentityReference;
            if (identity.Equals(self)) continue;
            if (identity.IsWellKnown(WellKnownSidType.LocalSystemSid)) continue;
            if (identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;
            // CREATOR OWNER grants nothing by itself; it is a template the system
            // stamps with whoever creates a child, and that is this account.
            if (identity.IsWellKnown(WellKnownSidType.CreatorOwnerSid) &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;

            return false;
        }
        return true;
    }

    // MARK: - Plumbing

    private static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    /// <summary>
    /// Every folder from the root down to the leaf, outermost first.
    /// </summary>
    /// <remarks>
    /// A leaf that is not under the root yields the leaf alone. Walking past the root
    /// would mean re-permissioning <c>%APPDATA%</c>, which is not Kytto's.
    /// </remarks>
    private static IReadOnlyList<string> Chain(string root, string directory)
    {
        var full = Path.GetFullPath(directory);
        var target = Path.GetFullPath(root);

        var steps = new List<string>();
        for (var step = full; step is not null; step = Path.GetDirectoryName(step))
        {
            steps.Add(step);
            if (string.Equals(step, target, StringComparison.OrdinalIgnoreCase))
            {
                steps.Reverse();
                return steps;
            }
        }
        return [full];
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (IsExpected(error))
        {
            return false;
        }
    }

    private static bool IsExpected(Exception error) =>
        error is IOException or UnauthorizedAccessException or PrivilegeNotHeldException
            or PlatformNotSupportedException or IdentityNotMappedException
            or ArgumentException or NotSupportedException;
}
