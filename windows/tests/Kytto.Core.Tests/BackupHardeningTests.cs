using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Settings;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// Kytto's copies of a config are as private as the config, and a restore only ever
/// writes a file Kytto manages for that client (§6).
/// </summary>
/// <remarks>
/// Two low-severity findings from the macOS audit, ported. Backups hold whole
/// <c>env</c> blocks, so a readable backups folder is a readable API key; and the
/// path a restore writes to used to come out of a sidecar file that only had to be
/// absolute.
/// </remarks>
public sealed class BackupPermissionTests
{
    [Fact]
    public void ABackupAndEverythingAroundItIsPrivate()
    {
        using var harness = MakeHarness();

        var backup = harness.Backups.BackUp(
            Path.Combine(harness.Home, ".cursor", "mcp.json"),
            ClientId.Cursor,
            @"%USERPROFILE%\.cursor\mcp.json");
        Assert.NotNull(backup);

        foreach (var path in new[]
        {
            harness.Paths.Root,
            harness.Paths.Backups,
            Path.Combine(harness.Paths.Backups, ClientId.Cursor.Raw()),
            backup.Path,
            backup.Path + ".origin",
        })
        {
            Acl.AssertPrivate(path);
        }

        // The folders Kytto created, it created protected: a file written inside one
        // inherits the private list at creation rather than after the fact.
        Assert.True(Acl.IsProtected(harness.Paths.Root));
        Assert.True(Acl.IsProtected(Path.Combine(harness.Paths.Backups, ClientId.Cursor.Raw())));
    }

    [Fact]
    public void AFolderAnEarlierVersionLeftReadableIsTightenedOnTheWay()
    {
        using var harness = MakeHarness();

        var clientFolder = Path.Combine(harness.Paths.Backups, ClientId.Cursor.Raw());
        foreach (var folder in new[] { harness.Paths.Root, harness.Paths.Backups, clientFolder })
        {
            Directory.CreateDirectory(folder);
            Acl.GrantUsersRead(folder);
            Assert.True(Acl.GrantsUsers(folder), $"{folder} should start readable");
        }

        harness.Backups.BackUp(
            Path.Combine(harness.Home, ".cursor", "mcp.json"),
            ClientId.Cursor,
            @"%USERPROFILE%\.cursor\mcp.json");

        Acl.AssertPrivate(harness.Paths.Root);
        Acl.AssertPrivate(harness.Paths.Backups);
        Acl.AssertPrivate(clientFolder);
    }

    [Fact]
    public void TheParkedStoreIsPrivateAndStaysPrivateWhenRewritten()
    {
        using var harness = MakeHarness();

        harness.ParkStore.Park(ClientId.Cursor, "figma", "{\"command\":\"npx\"}");
        harness.ParkStore.Park(ClientId.Cursor, "github", "{\"command\":\"npx\"}");
        Acl.AssertPrivate(harness.Paths.ParkFile);

        // What an earlier version could have left behind, on the file itself rather
        // than inherited — so tightening the folder alone would not have removed it.
        Acl.GrantUsersRead(harness.Paths.ParkFile);
        Assert.True(Acl.GrantsUsers(harness.Paths.ParkFile));

        harness.ParkStore.Unpark(ClientId.Cursor, "github");

        Acl.AssertPrivate(harness.Paths.ParkFile);
        Assert.NotNull(harness.ParkStore.Parked(ClientId.Cursor, "figma"));
    }

    [Fact]
    public void MigrationTightensALegacyTreeAndThenChangesNothing()
    {
        using var harness = MakeHarness();

        var clientFolder = Path.Combine(harness.Paths.Backups, ClientId.Cursor.Raw());
        Directory.CreateDirectory(clientFolder);
        var backupFile = Path.Combine(clientFolder, "mcp.json.2026-01-01-000000-000.bak");
        File.WriteAllText(backupFile, "{}");
        File.WriteAllText(backupFile + ".origin", @"C:\nowhere\mcp.json");
        File.WriteAllText(harness.Paths.ParkFile, "{}");

        string[] loose =
        [
            harness.Paths.Root,
            harness.Paths.Backups,
            clientFolder,
            backupFile,
            backupFile + ".origin",
            harness.Paths.ParkFile,
        ];
        foreach (var path in loose) Acl.GrantUsersRead(path);

        var changed = KyttoStorage.TightenExisting(harness.Paths);

        foreach (var path in loose) Acl.AssertPrivate(path);
        Assert.Equal(loose.Length, changed);

        // A second launch has nothing left to do, which is what makes running this on
        // every launch cheaper than keeping a marker file honest.
        Assert.Equal(0, KyttoStorage.TightenExisting(harness.Paths));
    }

    [Fact]
    public void MigrationWithNoDataRootCreatesNothing()
    {
        using var harness = new ToggleHarness();

        Assert.False(Directory.Exists(harness.Paths.Root));
        Assert.Equal(0, KyttoStorage.TightenExisting(harness.Paths));
        Assert.False(Directory.Exists(harness.Paths.Root));
    }

    [Fact]
    public void MigrationDoesNotFollowAReparsePointOutOfTheBackupsFolder()
    {
        using var harness = MakeHarness();

        var clientFolder = Path.Combine(harness.Paths.Backups, ClientId.Cursor.Raw());
        Directory.CreateDirectory(clientFolder);
        Acl.GrantUsersRead(harness.Paths.Root);

        // Somewhere else entirely, deliberately readable, and not Kytto's to change.
        var outside = Path.Combine(harness.Home, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "secrets.txt");
        File.WriteAllText(outsideFile, "not kytto's");
        Acl.GrantUsersRead(outside);
        Acl.GrantUsersRead(outsideFile);

        var junction = Path.Combine(clientFolder, "junction");
        Assert.True(Junction.Create(junction, outside), "the test needs a junction it can create");

        string? fileLink = Path.Combine(clientFolder, "link.bak");
        try
        {
            File.CreateSymbolicLink(fileLink, outsideFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Creating a file symlink needs Developer Mode or an elevated run. The
            // junction above covers the same code path without a privilege.
            fileLink = null;
        }

        try
        {
            KyttoStorage.TightenExisting(harness.Paths);

            Acl.AssertPrivate(harness.Paths.Root);
            Assert.True(Acl.GrantsUsers(outside), "the junction's target was re-permissioned");
            Assert.True(Acl.GrantsUsers(outsideFile), "a file outside the tree was re-permissioned");
            if (fileLink is not null) Assert.True(Acl.GrantsUsers(outsideFile));
        }
        finally
        {
            Junction.Remove(junction);
        }
    }

    [Fact]
    public void AConfigLockedDownByTheUserStaysLockedDownAcrossAWrite()
    {
        using var harness = MakeHarness();

        var config = Path.Combine(harness.Home, ".cursor", "mcp.json");
        Acl.MakeOwnerOnly(config);

        harness.Toggles.SetEnabled(false, harness.Server("figma"), ClientId.Cursor);

        // `File.Replace` keeps the replaced file's descriptor. `File.Move(…, true)`
        // would have handed the temporary file's inherited ACL to the config and
        // silently undone this.
        Assert.True(Acl.IsProtected(config));
        Acl.AssertOnly(config, Acl.Self);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(config)!, "*.tmp"));
    }

    [Fact]
    public void ANewConfigInheritsItsFolderAndLeavesNoTemporaryFile()
    {
        using var harness = MakeHarness();

        var created = Path.Combine(harness.Home, ".cursor", "fresh.json");
        AtomicWriter.Write("{}", created);

        // Its folder's list and nothing of its own. Asserted against the folder rather
        // than against a fixed set of principals, because a config lives wherever the
        // client put it and Kytto does not get to decide who else the user's own
        // directories are shared with (§6).
        Assert.False(Acl.IsProtected(created));
        Acl.AssertNoGrantsBeyond(created, Path.GetDirectoryName(created)!);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(created)!, "*.tmp"));
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }
}

/// <summary>
/// A restore writes one of the files Kytto manages for that client, or nothing.
/// </summary>
/// <remarks>
/// The sidecar beside a backup records where the backup came from, and anything with
/// write access to Kytto's folder can edit it. These cases rewrite it the way such a
/// thing would, so the refusal is exercised rather than assumed.
/// </remarks>
public sealed class BackupRestoreTargetTests
{
    [Fact]
    public void ASidecarPointingSomewhereElseIsRefusedAndWritesNothing()
    {
        using var harness = MakeHarness();

        var bystander = Path.Combine(harness.Home, "bystander.txt");
        File.WriteAllText(bystander, "untouched");

        var backup = Repoint(harness, bystander);
        var before = harness.Backups.List(ClientId.Cursor).Count;

        var error = Assert.Throws<BackupException>(() =>
            harness.Backups.Restore(backup, harness.Resolver()));
        Assert.Contains("not a configuration file Kytto manages", error.Message, StringComparison.Ordinal);

        Assert.Equal("untouched", File.ReadAllText(bystander));
        // Refused means refused: not even the pre-restore backup was taken.
        Assert.Equal(before, harness.Backups.List(ClientId.Cursor).Count);
    }

    [Fact]
    public void ASidecarThatClimbsOutWithDotDotIsRefused()
    {
        using var harness = MakeHarness();

        var escape = Path.Combine(harness.Home, ".cursor", "..", "unrelated.txt");
        File.WriteAllText(Path.Combine(harness.Home, "unrelated.txt"), "untouched");

        var backup = Repoint(harness, escape);

        Assert.Throws<BackupException>(() => harness.Backups.Restore(backup, harness.Resolver()));
        Assert.Equal("untouched", File.ReadAllText(Path.Combine(harness.Home, "unrelated.txt")));
    }

    [Fact]
    public void ABackupCannotBeAimedAtAnotherClientsConfig()
    {
        using var harness = MakeHarness();

        var claudeCode = Path.Combine(harness.Home, ".claude.json");
        File.WriteAllText(claudeCode, "{\"mcpServers\":{}}");

        var backup = Repoint(harness, claudeCode);

        Assert.Throws<BackupException>(() => harness.Backups.Restore(backup, harness.Resolver()));
        Assert.Equal("{\"mcpServers\":{}}", File.ReadAllText(claudeCode));
    }

    [Fact]
    public void AnAlternateDataStreamIsRefusedAndNotCreated()
    {
        using var harness = MakeHarness();

        var config = Path.Combine(harness.Home, ".cursor", "mcp.json");
        var backup = Repoint(harness, config + ":evil");

        Assert.Throws<BackupException>(() => harness.Backups.Restore(backup, harness.Resolver()));
        Assert.False(File.Exists(config + ":evil"));
    }

    [Fact]
    public void AJunctionInTheMiddleNeverWritesUnderItsTarget()
    {
        using var harness = MakeHarness();

        // One level deeper than the link, so the two readings of `..` land in
        // different folders and the bypass would be visible if it happened.
        var elsewhere = Path.Combine(harness.Home, "deep", "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var link = Path.Combine(harness.Home, "link");
        Assert.True(Junction.Create(link, elsewhere), "the test needs a junction it can create");

        // Lexically this is `~\.cursor\mcp.json`, which is why matching alone is not
        // the boundary; the kernel follows `link` and lands in `~\deep\.cursor`
        // instead. Refusing and restoring the real config are both fine — writing
        // under the junction's target is not.
        var backup = Repoint(harness, Path.Combine(harness.Home, "link", "..", ".cursor", "mcp.json"));
        try
        {
            try
            {
                harness.Backups.Restore(backup, harness.Resolver());
            }
            catch (BackupException)
            {
                // Also an acceptable answer.
            }

            Assert.False(Directory.Exists(Path.Combine(harness.Home, "deep", ".cursor")));
            Assert.False(Directory.Exists(Path.Combine(elsewhere, ".cursor")));
            Assert.Equal(
                Fixture.CursorMcp.Text(),
                File.ReadAllText(Path.Combine(harness.Home, ".cursor", "mcp.json")));
        }
        finally
        {
            Junction.Remove(link);
        }
    }

    [Theory]
    [MemberData(nameof(EveryManagedFile))]
    public void EveryFileKyttoManagesRestoresByteForByte(ClientId client, string relativePath)
    {
        using var harness = new ToggleHarness();

        var path = Path.Combine(harness.Home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = $"{{\"marker\":\"{relativePath.Replace('\\', '/')}\"}}";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(original));

        var backup = harness.Backups.BackUp(path, client, relativePath);
        Assert.NotNull(backup);

        File.WriteAllText(path, "clobbered");
        harness.Backups.Restore(backup, harness.Resolver());

        Assert.Equal(original, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    public static TheoryData<ClientId, string> EveryManagedFile => new()
    {
        { ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\claude_desktop_config.json" },
        { ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\Claude Extensions Settings\ant.dir.test.json" },
        { ClientId.ClaudeCode, ".claude.json" },
        { ClientId.ClaudeCode, @".claude\settings.json" },
        { ClientId.Cursor, @".cursor\mcp.json" },
        { ClientId.VsCode, @"AppData\Roaming\Code\User\mcp.json" },
        { ClientId.Codex, @".codex\config.toml" },
    };

    [Fact]
    public void BothAnOverrideAndTheRegistryDefaultAreRestorable()
    {
        using var harness = new ToggleHarness();

        var overridden = Path.Combine(harness.Home, "moved", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overridden)!);
        File.WriteAllText(overridden, "{\"from\":\"override\"}");

        var registryDefault = Path.Combine(harness.Home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryDefault)!);
        File.WriteAllText(registryDefault, "{\"from\":\"default\"}");

        var overrides = new Dictionary<string, string> { [ClientId.Cursor.Raw()] = overridden };
        var resolver = harness.Resolver(overrides);

        var movedBackup = harness.Backups.BackUp(overridden, ClientId.Cursor, "moved");
        var defaultBackup = harness.Backups.BackUp(registryDefault, ClientId.Cursor, "default");
        Assert.NotNull(movedBackup);
        Assert.NotNull(defaultBackup);

        File.WriteAllText(overridden, "clobbered");
        File.WriteAllText(registryDefault, "clobbered");

        harness.Backups.Restore(movedBackup, resolver);
        harness.Backups.Restore(defaultBackup, resolver);

        Assert.Equal("{\"from\":\"override\"}", File.ReadAllText(overridden));
        Assert.Equal("{\"from\":\"default\"}", File.ReadAllText(registryDefault));

        // Deliberate: once the override is gone, a backup taken under it has nowhere
        // Kytto is willing to put it, and the message says where the file still is.
        Assert.Throws<BackupException>(() =>
            harness.Backups.Restore(movedBackup, harness.Resolver()));
    }

    [Theory]
    [InlineData(ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\Claude Extensions\ant.dir.test\manifest.json")]
    [InlineData(ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\Claude Extensions Settings\nested\ant.dir.test.json")]
    [InlineData(ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\Claude Extensions Settings\notes.txt")]
    [InlineData(ClientId.ClaudeDesktop, @"AppData\Roaming\Claude\Claude Extensions Settings\ant.json:evil")]
    [InlineData(ClientId.Codex, @".codex\plugins\thing\.mcp.json")]
    [InlineData(ClientId.Codex, @".cursor\mcp.json")]
    [InlineData(ClientId.Cursor, @"Documents\custom-source.json")]
    public void TheResolverRefusesWhatKyttoDoesNotWrite(ClientId client, string relativePath)
    {
        using var harness = new ToggleHarness();

        var resolver = harness.Resolver();
        Assert.Null(resolver.WriteTarget(Path.Combine(harness.Home, relativePath), client));
    }

    [Fact]
    public void TheResolverAnswersWithItsOwnSpellingNotTheCallers()
    {
        using var harness = new ToggleHarness();

        var real = Path.Combine(harness.Home, ".cursor", "mcp.json");
        var lexicallyEqual = Path.Combine(harness.Home, "link", "..", ".cursor", "mcp.json");

        // The two spell the same path lexically and different paths to the kernel, so
        // the answer has to be the resolver's, never the caller's string.
        var target = harness.Resolver().WriteTarget(lexicallyEqual, ClientId.Cursor);
        Assert.Equal(real, target);
    }

    [Fact]
    public void ARelativeOrEmptySidecarIsRefused()
    {
        using var harness = new ToggleHarness();

        var resolver = harness.Resolver();
        Assert.Null(resolver.WriteTarget(@".cursor\mcp.json", ClientId.Cursor));
        Assert.Null(resolver.WriteTarget("", ClientId.Cursor));
    }

    /// <summary>Rewrites a real backup's sidecar the way something with write access could.</summary>
    private static Backup Repoint(ToggleHarness harness, string path)
    {
        var made = harness.Backups.BackUp(
            Path.Combine(harness.Home, ".cursor", "mcp.json"),
            ClientId.Cursor,
            @"%USERPROFILE%\.cursor\mcp.json");
        Assert.NotNull(made);

        File.WriteAllText(made.Path + ".origin", path, new UTF8Encoding(false));

        var reread = harness.Backups.Get(made.Id, ClientId.Cursor);
        Assert.NotNull(reread);
        return reread;
    }

    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(Fixture.CursorMcp.Text(), @".cursor\mcp.json");
        return harness;
    }
}

/// <summary>Reading and writing DACLs, spelled once for the tests that need it.</summary>
internal static class Acl
{
    internal static SecurityIdentifier Self { get; } = CurrentUser();

    private static SecurityIdentifier Users { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Nothing but this account, SYSTEM and the administrators is allowed.</summary>
    internal static void AssertPrivate(string path) => AssertOnly(
        path,
        Self,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

    internal static void AssertOnly(string path, params SecurityIdentifier[] allowed)
    {
        var unexpected = Granted(path)
            .Where(identity => !allowed.Contains(identity))
            .Select(identity => identity.Value)
            .ToArray();
        Assert.True(unexpected.Length == 0, $"{path} also grants {string.Join(", ", unexpected)}");
    }

    internal static bool GrantsUsers(string path) => Granted(path).Contains(Users);

    /// <summary>The file gained no principal its folder did not already grant.</summary>
    internal static void AssertNoGrantsBeyond(string path, string folder) =>
        AssertOnly(path, [.. Granted(folder)]);

    internal static bool IsProtected(string path) => Security(path).AreAccessRulesProtected;

    internal static void GrantUsersRead(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                Users,
                FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.Read, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
    }

    /// <summary>A config the user locked down themselves: protected, one entry.</summary>
    internal static void MakeOwnerOnly(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            Self, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static IReadOnlyList<SecurityIdentifier> Granted(string path) =>
        Security(path)
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .Distinct()
            .ToArray();

    private static FileSystemSecurity Security(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }
}

/// <summary>
/// A directory junction, which unlike a symbolic link needs no privilege.
/// </summary>
/// <remarks>
/// The reparse-point cases have to run on an ordinary developer machine, or they are
/// not the safety net they claim to be.
/// </remarks>
internal static class Junction
{
    internal static bool Create(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null) return false;
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Unlinks without descending, so a failed assertion cannot take the target with it.
    /// </summary>
    internal static void Remove(string link)
    {
        try
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The harness's own cleanup tolerates what is left.
        }
    }
}
