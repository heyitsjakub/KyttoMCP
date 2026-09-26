using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core.Secrets;

/// <summary>One place a secret value appears.</summary>
public sealed record SecretUsage(
    string ServerID,
    string ServerName,
    ClientId ClientID,
    string PathDisplay);

/// <summary>Something about where a secret is sitting that the user would want to know.</summary>
public abstract record SecretExposure
{
    /// <summary>The config file can be read by accounts other than its owner.</summary>
    public sealed record ReadableByOthers(string PathDisplay, string Who) : SecretExposure;

    /// <summary>The config file is inside a Git working tree and is not ignored.</summary>
    public sealed record InsideGitRepository(string PathDisplay, string RepositoryPathDisplay) : SecretExposure;

    public string Kind => this is ReadableByOthers ? "readableByOthers" : "insideGitRepository";

    public string PathDisplayValue => this switch
    {
        ReadableByOthers readable => readable.PathDisplay,
        InsideGitRepository repository => repository.PathDisplay,
        _ => "",
    };

    public string Detail => this switch
    {
        ReadableByOthers readable =>
            $"{readable.Who} can read it. Only your account needs to.",
        InsideGitRepository repository =>
            $"Inside the Git repository at {repository.RepositoryPathDisplay}, and not ignored.",
        _ => "",
    };
}

/// <summary>A secret value, and everywhere it lives.</summary>
public sealed record SecretRecord(
    string Id,
    string Key,
    /// <summary>Only ever this. The real value stays native-side (§6).</summary>
    string MaskedValue,
    IReadOnlyList<SecretUsage> Usages,
    bool IsInSecretStore,
    IReadOnlyList<SecretExposure> Exposure)
{
    /// <summary>The same value in more than one client — the case rotation exists for.</summary>
    public bool IsShared => Usages.Select(usage => usage.ClientID).Distinct().Count() > 1;
}

/// <summary>
/// Finds secrets, changes them everywhere at once, and points out where they are
/// sitting badly.
/// </summary>
/// <remarks>
/// What this deliberately does <strong>not</strong> do: replace the value in the
/// config with a reference. No MCP client understands one — they read the file
/// literally — so a "reference" would either break every server or require Kytto
/// to wrap every command in its own launcher, making a working setup depend on
/// Kytto staying installed. The honest version is this: Kytto is where you manage
/// the value, the file still holds it, and Kytto tells you exactly where "it" is.
/// </remarks>
public sealed class SecretsService(
    string home,
    ConfigWriter writer,
    DigestLedger ledger,
    ISecretStore store,
    IReadOnlyList<ClientDescriptor>? descriptors = null,
    IReadOnlyDictionary<string, string>? pathOverrides = null)
{
    private readonly ClientPathResolver _resolver = new(home, pathOverrides);
    private readonly IReadOnlyList<ClientDescriptor> _descriptors = descriptors ?? ClientRegistry.All;

    // MARK: - Inventory

    /// <summary>Groups every sensitive-looking environment value by what it actually is.</summary>
    /// <remarks>
    /// Keyed on name <em>and</em> value, so the same token under one name in three
    /// clients is one row — and two different tokens sharing a name are two, which
    /// is exactly the drift worth seeing.
    /// </remarks>
    public IReadOnlyList<SecretRecord> Scan(IReadOnlyList<Server> servers)
    {
        var groups = new Dictionary<string, (string Key, string Value, List<SecretUsage> Usages)>(
            StringComparer.Ordinal);

        foreach (var server in servers)
        {
            foreach (var (clientId, entries) in server.EnvByClient)
            {
                // Custom sources are a discovery surface, never part of secret
                // inventory or rotation. Keeping the writable enum here makes the
                // safety boundary hold even when this service is called directly.
                if (clientId.BuiltIn is not { } builtIn) continue;
                var source = _descriptors
                    .FirstOrDefault(descriptor => descriptor.Id == builtIn)?.EditableServerMap;
                if (source is null) continue;

                foreach (var entry in entries)
                {
                    if (entry.Value is not { Length: > 0 } value) continue;
                    if (!LooksSensitive(entry.Key, value)) continue;

                    var id = Identifier(entry.Key, value);
                    if (!groups.TryGetValue(id, out var group))
                    {
                        group = (entry.Key, value, []);
                        groups[id] = group;
                    }
                    group.Usages.Add(new SecretUsage(
                        ServerID: server.Id,
                        ServerName: server.Name,
                        ClientID: builtIn,
                        PathDisplay: _resolver.DisplayServerMap(source.File, builtIn)));
                }
            }
        }

        var known = store.StoredIdentifiers();
        return groups
            .Select(pair => new SecretRecord(
                Id: pair.Key,
                Key: pair.Value.Key,
                MaskedValue: Mask(pair.Value.Value),
                Usages: pair.Value.Usages
                    .OrderBy(usage => usage.ClientID.Raw(), StringComparer.Ordinal)
                    .ToArray(),
                IsInSecretStore: known.Contains(pair.Key),
                Exposure: ExposureFor(pair.Value.Usages)))
            .OrderBy(record => record.Key, StringComparer.Ordinal)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Reveals a value, for the one case §6 allows: the user asked, explicitly.</summary>
    public string? Reveal(string secretId, IReadOnlyList<Server> servers)
    {
        foreach (var server in servers)
        {
            foreach (var (clientId, entries) in server.EnvByClient)
            {
                if (clientId.BuiltIn is null) continue;
                foreach (var entry in entries)
                {
                    if (entry.Value is not { Length: > 0 } value) continue;
                    if (Identifier(entry.Key, value) == secretId) return value;
                }
            }
        }
        return store.Value(secretId);
    }

    // MARK: - Rotation

    public sealed record RotationResult(
        string Key,
        IReadOnlyList<SecretUsage> Updated,
        IReadOnlyList<string> BackupIDs);

    /// <summary>Writes a new value everywhere the old one appeared.</summary>
    /// <remarks>
    /// The point of the whole feature: rotating a token today means finding every
    /// config that has it and editing each by hand, which is how one gets missed.
    /// </remarks>
    public RotationResult Rotate(
        string secretId,
        string newValue,
        IReadOnlyList<Server> servers,
        bool alsoStore)
    {
        var record = Scan(servers).FirstOrDefault(candidate => candidate.Id == secretId)
            ?? throw SecretsException.NotFound();
        if (newValue.Length == 0) throw SecretsException.EmptyValue();

        var updated = new List<SecretUsage>();
        var prepared = new List<(PreparedConfigWrite Write, string Path, IReadOnlyList<SecretUsage> Usages)>();

        // Grouped per file, so a config holding the same token for two servers is
        // opened, backed up and written once rather than twice.
        var byClient = record.Usages
            .GroupBy(usage => usage.ClientID)
            .OrderBy(group => group.Key.Raw(), StringComparer.Ordinal);

        foreach (var group in byClient)
        {
            var source = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == group.Key)?.EditableServerMap;
            if (source is null) continue;

            var usages = group.ToArray();
            var path = _resolver.ResolveServerMap(source.File, group.Key);

            var actuallyUpdated = new List<SecretUsage>();
            var write = source.Format == ConfigFormat.Json
                ? writer.Prepare<JsonDocument>(
                    path, group.Key, _resolver.DisplayServerMap(source.File, group.Key),
                    ledger.DigestFor(path),
                    document =>
                    {
                        var text = document.SourceText;
                        foreach (var usage in usages)
                        {
                            text = JsonDocument.Parse(text).SettingMember(
                                record.Key,
                                [source.ServersKey, usage.ServerName, "env"],
                                JsonText.String(newValue));
                            actuallyUpdated.Add(usage);
                        }
                        return text;
                    })
                : writer.Prepare<Toml.TomlDocument>(
                    path, group.Key, _resolver.DisplayServerMap(source.File, group.Key),
                    ledger.DigestFor(path),
                    document =>
                    {
                        var text = document.SourceText;
                        foreach (var usage in usages)
                        {
                            var current = Toml.TomlDocument.Parse(text);
                            var pair = EnvironmentPair(
                                current, source.ServersKey, usage.ServerName, record.Key);
                            if (pair is null) continue;
                            text = current.Replacing(pair.Value.Span, Toml.TomlText.String(newValue));
                            actuallyUpdated.Add(usage);
                        }
                        return text;
                    });
            prepared.Add((write, path, actuallyUpdated));
        }

        // Rotation means one value everywhere. Preparing every file before the
        // first write, then committing them together, keeps that promise if a later
        // target fails: ConfigWriter restores the earlier files byte for byte (§6).
        var receipts = writer.Commit(prepared.Select(target => target.Write).ToArray());
        var backupIds = new List<string>();
        for (var index = 0; index < prepared.Count; index++)
        {
            ledger.Record(prepared[index].Path, receipts[index].Digest);
            if (receipts[index].BackupID is { } backupId) backupIds.Add(backupId);
            updated.AddRange(prepared[index].Usages);
        }

        if (alsoStore)
        {
            var newId = Identifier(record.Key, newValue);
            try
            {
                store.Store(newValue, newId);
                // The old entry describes a value that is no longer anywhere.
                if (newId != secretId) store.Delete(secretId);
            }
            catch (SecretsException)
            {
                // The files were rewritten, which is the part that mattered. A
                // Credential Manager that refused is not worth undoing that over.
            }
        }

        return new RotationResult(record.Key, updated, backupIds);
    }

    /// <summary>The value span for an environment key in any TOML spelling Codex accepts.</summary>
    private static Toml.TomlPair? EnvironmentPair(
        Toml.TomlDocument document,
        string serversKey,
        string serverName,
        string environmentKey)
    {
        var separate = document.Table(serversKey, serverName, "env")?.Pair(environmentKey);
        if (separate is not null) return separate;

        var inlineEnvironment = document.Table(serversKey, serverName)?
            .Value("env")?.InlinePairs;
        var pair = inlineEnvironment?.FirstOrDefault(candidate => candidate.Name == environmentKey);
        if (pair is not null) return pair;

        // Hand-written Codex configs may put the whole server in an inline table
        // under [mcp_servers]. Its nested env table still has an exact value span,
        // so rotating that value requires no re-serialization.
        var inlineServer = document.Table(serversKey)?.Pair(serverName)?.Value.InlinePairs;
        return inlineServer?
            .FirstOrDefault(candidate => candidate.Name == "env")?
            .Value.InlinePairs?
            .FirstOrDefault(candidate => candidate.Name == environmentKey);
    }

    /// <summary>Keeps a copy of the current value, so a later mistake is recoverable.</summary>
    public void Adopt(string secretId, IReadOnlyList<Server> servers)
    {
        var value = Reveal(secretId, servers) ?? throw SecretsException.NotFound();
        store.Store(value, secretId);
    }

    public void Forget(string secretId) => store.Delete(secretId);

    // MARK: - Exposure

    /// <summary>Restricts a config file to its owner.</summary>
    /// <remarks>
    /// <para>
    /// Small, reversible, and the only hardening available while the value has to
    /// stay in the file.
    /// </para>
    /// <para>
    /// Where macOS writes mode <c>0600</c>, Windows has to say the same thing in
    /// ACL terms: stop inheriting whatever the parent directory grants, then keep a
    /// single entry for this account. SYSTEM and Administrators are deliberately not
    /// re-added — an administrator can take ownership regardless, so listing them
    /// would only make the file look more shared than it is.
    /// </para>
    /// </remarks>
    public bool RestrictPermissions(ClientId clientId)
    {
        var source = _descriptors
            .FirstOrDefault(descriptor => descriptor.Id == clientId)?.EditableServerMap;
        if (source is null) return false;

        var path = _resolver.ResolveServerMap(source.File, clientId);
        if (!File.Exists(path)) return false;

        if (WindowsIdentity.GetCurrent().User is not { } self) return false;

        // A DACL built from nothing rather than the file's own edited down to size.
        // Editing means enumerating what is there and removing each entry, and the
        // directories these files live in can carry a dozen inherited ACEs from
        // sandboxes and installers — one missed entry and the file still is not
        // private, silently. Stating the whole intended access list leaves no room
        // for that: inheritance off, one entry, this account.
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            self, FileSystemRights.FullControl, AccessControlType.Allow));

        new FileInfo(path).SetAccessControl(security);
        return true;
    }

    private IReadOnlyList<SecretExposure> ExposureFor(IReadOnlyList<SecretUsage> usages)
    {
        var found = new List<SecretExposure>();
        var seen = new HashSet<ClientId>();

        foreach (var usage in usages)
        {
            if (!seen.Add(usage.ClientID)) continue;

            var source = _descriptors
                .FirstOrDefault(descriptor => descriptor.Id == usage.ClientID)?.EditableServerMap;
            if (source is null) continue;

            var path = _resolver.ResolveServerMap(source.File, usage.ClientID);
            var display = _resolver.DisplayServerMap(source.File, usage.ClientID);

            if (OtherAccountsWithRead(path) is { } who)
            {
                found.Add(new SecretExposure.ReadableByOthers(display, who));
            }

            if (GitRepositoryContaining(path) is { } repository)
            {
                found.Add(new SecretExposure.InsideGitRepository(display, Abbreviate(repository)));
            }
        }
        return found;
    }

    /// <summary>
    /// Who other than this account, SYSTEM or the administrators can read the file.
    /// </summary>
    /// <remarks>
    /// The Windows reading of macOS's "mode 644". SYSTEM and Administrators are not
    /// counted: they can reach any file on the machine anyway, so reporting them
    /// would flag every config on every install and teach the user to ignore the
    /// warning that matters.
    /// </remarks>
    private static string? OtherAccountsWithRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var security = new FileInfo(path).GetAccessControl();
            if (WindowsIdentity.GetCurrent().User is not { } self) return null;
            var names = new List<string>();

            foreach (FileSystemAccessRule rule in
                     security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & FileSystemRights.Read) == 0) continue;

                var identity = (SecurityIdentifier)rule.IdentityReference;
                if (identity.Equals(self)) continue;
                if (identity.IsWellKnown(WellKnownSidType.LocalSystemSid)) continue;
                if (identity.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;

                names.Add(Describe(identity));
            }

            return names.Count == 0 ? null : string.Join(", ", names.Distinct());
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or
                     PlatformNotSupportedException or IdentityNotMappedException)
        {
            // A file whose ACL cannot be read is not evidence of anything.
            return null;
        }
    }

    private static string Describe(SecurityIdentifier identity)
    {
        try
        {
            return ((NTAccount)identity.Translate(typeof(NTAccount))).Value;
        }
        catch (IdentityNotMappedException)
        {
            return identity.Value;
        }
    }

    /// <summary>
    /// The Git working tree this file sits in, if any and if the file is not ignored.
    /// </summary>
    /// <remarks>
    /// Rare for the global configs v1 supports, but people do keep their profile
    /// directory in a dotfiles repository, and a token committed there is the exact
    /// accident §6 opens with.
    /// </remarks>
    private static string? GitRepositoryContaining(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(directory))
        {
            if (Directory.Exists(Path.Combine(directory, ".git")) ||
                File.Exists(Path.Combine(directory, ".git")))
            {
                return IsIgnoredByGit(path, directory) ? null : directory;
            }
            var parent = Path.GetDirectoryName(directory);
            if (parent == directory) break;
            directory = parent;
        }
        return null;
    }

    private static bool IsIgnoredByGit(string path, string repository)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                ArgumentList = { "check-ignore", "-q", path },
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return false;

            // A git that hangs must not hang the scan the matrix is waiting on.
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No git on PATH. Not knowing is not the same as being ignored.
            return false;
        }
    }

    private string Abbreviate(string path) =>
        path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[home.Length..]
            : path;

    // MARK: - Heuristics

    /// <summary>Whether a variable is worth treating as a secret.</summary>
    /// <remarks>
    /// A heuristic, and it says so: the name is the strong signal, and a long opaque
    /// value is the weak one. It errs towards including things — mentioning a
    /// variable that turns out to be harmless costs a line in a list, while missing
    /// a real token costs the point of the feature.
    /// </remarks>
    internal static bool LooksSensitive(string key, string value)
    {
        var upper = key.ToUpperInvariant();
        string[] names =
        [
            "TOKEN", "KEY", "SECRET", "PASSWORD", "PASSWD",
            "CREDENTIAL", "AUTH", "_PAT", "SESSION", "COOKIE", "PRIVATE",
        ];

        if (names.Any(name => upper.Contains(name, StringComparison.Ordinal)))
        {
            // `..._KEY_PATH` and friends point at secrets rather than being them.
            string[] pointers = ["PATH", "FILE", "DIR", "URL", "ENABLED", "DISABLED"];
            return !pointers.Any(pointer => upper.EndsWith(pointer, StringComparison.Ordinal));
        }

        // An opaque blob is worth flagging whatever it is called.
        return value.Length >= 24
            && !value.Contains(' ', StringComparison.Ordinal)
            && value.Any(char.IsDigit);
    }

    /// <summary>
    /// Stable across launches, and derived from the value so that changing the value
    /// produces a different secret rather than silently reusing the old id.
    /// </summary>
    internal static string Identifier(string key, string value)
    {
        var hash = 0xcbf29ce484222325UL;
        // A separator between the two, so a key ending in "a" with value "bc"
        // cannot hash the same as key "ab" with value "c".
        foreach (var b in Encoding.UTF8.GetBytes(key + '\u0001' + value))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return $"{key.ToLowerInvariant()}-{hash.ToString("x16", CultureInfo.InvariantCulture)}";
    }

    /// <summary>A display placeholder containing no bytes from the secret (§6).</summary>
    /// <remarks>
    /// Even a prefix or suffix would cross the native/web boundary as part of
    /// <see cref="SecretRecord"/>. Recognition comes from the environment key and
    /// usage list; the value appears only after the explicit reveal action.
    /// </remarks>
    internal static string Mask(string value) => "••••••••";
}
