using System.Text.RegularExpressions;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;
using Kytto.Core.Toml;

namespace Kytto.Core;

public sealed class ProjectCopyException(string message) : InvalidOperationException(message)
{
    public static ProjectCopyException Unavailable() =>
        new("Choose a project definition and an available writable client.");

    public static ProjectCopyException Stale() =>
        new("The source, destination or selection changed. Open a fresh preview.");
}

/// <summary>What the person reviews before a project definition is imported.</summary>
/// <remarks>
/// File paths are display strings. Environment <em>names</em> only: a value in the
/// repository file is copied natively and never shown (§6).
/// </remarks>
/// <param name="Token">Single use, and only when nothing blocks the import.</param>
public sealed record ProjectCopyPreview(
    string? Token,
    string SourceFile,
    string TargetFile,
    string Transport,
    IReadOnlyList<string> EnvironmentKeys,
    IReadOnlyList<string> UnresolvedVariables,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers);

/// <summary>
/// One definition from a repository file, reviewed for one writable client (roadmap §12).
/// </summary>
/// <remarks>
/// <para>
/// Only the review crosses IPC. The draft, the digests of both files and the resolved
/// destination stay here until a single confirmation consumes the plan, and applying
/// it builds the plan again from a fresh read: a source edited, a destination written,
/// a folder detached or a path override changed since the preview fails the import
/// with nothing written.
/// </para>
/// <para>
/// The import is an ordinary authoring write of one new entry — backup, splice,
/// atomic write — so Undo is removing it from that client again. It never replaces an
/// entry the destination already holds under the same name.
/// </para>
/// </remarks>
public sealed class ProjectDefinitionCopy
{
    private static readonly string[] ContextVariables =
        ["workspaceFolder", "workspaceFolderBasename", "workspaceRoot", "file", "fileDirname"];

    private static readonly string[] ContextPrefixes =
        ["${input:", "${command:", "${config:", "${workspaceFolder:"];

    private static readonly Regex VariablePattern = new(
        @"\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}|\$([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _sourceDigest;
    private readonly string? _targetDigest;
    private readonly string _destination;

    public ProjectCopyPreview Preview { get; }
    public ServerDraft Draft { get; }
    public ClientId Target { get; }
    public ClientKey Source { get; }
    public string ServerID { get; }

    public ProjectDefinitionCopy(
        Server server,
        ClientKey source,
        ClientId target,
        DiscoveryResult result,
        string home,
        KyttoSettings settings,
        DigestLedger ledger,
        IReadOnlyList<ClientDescriptor>? descriptors = null)
    {
        if (!server.DefinitionsByClient.TryGetValue(source, out var copy) ||
            !server.Locations.TryGetValue(source, out var location) ||
            !result.Clients.Any(client => client.Id == source && client.IsRepositoryFile) ||
            !result.Clients.Any(client => client.Id == (ClientKey)target && !client.IsReadOnly &&
                (client.IsInstalled || client.ConfigExists)) ||
            (descriptors ?? ClientRegistry.All).FirstOrDefault(candidate => candidate.Id == target) is not { } descriptor ||
            descriptor.EditableServerMap is not { } map)
        {
            throw ProjectCopyException.Unavailable();
        }

        var resolver = new ClientPathResolver(home, settings.ClientPathOverrides);
        var targetPath = resolver.ResolveServerMap(map.File, target);
        if (ledger.DigestFor(location.File) is not { } digest ||
            Digest.OfFile(location.File) != digest ||
            ledger.HasDrifted(targetPath))
        {
            throw ProjectCopyException.Stale();
        }
        _sourceDigest = digest;
        _targetDigest = ledger.DigestFor(targetPath);
        _destination = Resolved(targetPath);
        Target = target;
        Source = source;
        ServerID = server.Id;
        Draft = new ServerDraft
        {
            Name = location.Name,
            Transport = copy.Transport,
            Command = copy.Command ?? "",
            Args = copy.Args,
            Env = copy.Env,
            Url = copy.Url ?? "",
        };

        var blockers = new List<string>();
        var warnings = new List<string>();
        var protectedFile = settings.ProjectFolders.Any(folder => ProjectFolders.Files.Any(file =>
            string.Equals(Resolved(ProjectFolders.PathOf(folder, file)), _destination, StringComparison.OrdinalIgnoreCase)));
        if (protectedFile) blockers.Add("The destination points to a read-only project file.");

        var nameTaken = server.EnabledIn.GetValueOrDefault(target, Enablement.Absent) != Enablement.Absent;
        if (File.Exists(targetPath))
        {
            try
            {
                var text = File.ReadAllText(targetPath);
                if (map.Format == ConfigFormat.Json)
                {
                    var document = JsonDocument.Parse(text);
                    var root = document.Root;
                    if (root.Members is null ||
                        (root[map.ServersKey] is { } servers && servers.Members is null) ||
                        root.Members.Count(member => member.Key == map.ServersKey) > 1)
                    {
                        blockers.Add("The destination server map is invalid or ambiguous.");
                    }
                    if (document.ServerKey(server.Id, map.ServersKey) is not null) nameTaken = true;
                }
                else if (TomlDocument.Parse(text).ServerName(server.Id, map.ServersKey) is not null)
                {
                    nameTaken = true;
                }
            }
            catch (Exception error) when (
                error is JsonParseException or JsonEditException or TomlParseException or TomlEditException or
                         IOException or UnauthorizedAccessException)
            {
                blockers.Add("The destination configuration could not be read unambiguously.");
            }
        }
        if (nameTaken) blockers.Add("The destination already contains this server name.");

        // Discovery merges identities for the matrix. Import must still be able to
        // identify exactly one entry in the selected file, including JSONC.
        if (!HasOneEntry(location.File, server.Id))
        {
            blockers.Add("The source must contain one unambiguous JSON or JSONC entry for this name.");
        }
        if (copy.Command is { } command &&
            string.Equals(Path.GetFileNameWithoutExtension(command), "kytto-mcp-proxy", StringComparison.OrdinalIgnoreCase) &&
            copy.Args.Contains("--route", StringComparer.Ordinal))
        {
            blockers.Add("A Gateway route belongs to its original client. Choose a direct server definition.");
        }
        if (Draft.Name != Draft.TrimmedName ||
            Draft.Command != Draft.Command.Trim(' ', '\t') ||
            Draft.Url != Draft.Url.Trim(' ', '\t') ||
            Draft.Env.Any(entry => entry.Key != entry.Key.Trim()))
        {
            blockers.Add("The source contains surrounding whitespace that the destination writer would remove.");
        }

        var references = new SortedSet<string>(StringComparer.Ordinal);
        JsonNode? entry = null;
        try
        {
            entry = JsonDocument.Parse(copy.DefinitionSource).Root;
        }
        catch (JsonParseException)
        {
            // Reported below.
        }
        if (entry?.Members is { } fields)
        {
            HashSet<string> allowed = copy.Transport == Transport.Stdio
                ? ["command", "args", "env", "type"]
                : ["url", "env", "type"];
            var unsupported = fields.Select(field => field.Key).Distinct(StringComparer.Ordinal)
                .Where(key => !allowed.Contains(key))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unsupported.Length > 0) blockers.Add("Unsupported settings: " + string.Join(", ", unsupported) + ".");
            if (fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count() != fields.Count ||
                !ValidFieldTypes(entry))
            {
                blockers.Add("The source has duplicate fields or unsupported field values.");
            }
            var (names, requiresProject) = Substitutions(entry);
            references.UnionWith(names);
            if (requiresProject)
            {
                blockers.Add("Project inputs or workspace substitutions require their original project context.");
            }
        }
        else
        {
            blockers.Add("The project entry must be a JSON or JSONC server definition.");
        }

        var schema = descriptor.ServerSchema;
        if (copy.Transport == Transport.Sse &&
            (!schema.AcceptsSse || map.Format == ConfigFormat.Toml || schema.Shape == ServerSchema.EntryShape.OpenCode))
        {
            blockers.Add("This destination cannot preserve the SSE transport.");
        }
        if (copy.Transport != Transport.Stdio && copy.Env.Count > 0 &&
            (schema.Shape == ServerSchema.EntryShape.OpenCode || map.Format == ConfigFormat.Toml))
        {
            blockers.Add("This destination cannot store environment variables on a remote server.");
        }
        if (Draft.Validate().Count > 0)
        {
            blockers.Add("The server name, launch fields or environment keys cannot be written in this format.");
        }
        if (copy.Transport == Transport.Stdio && Server.HasRelativeLaunchPath(copy.Command, copy.Args))
        {
            warnings.Add("Relative paths will resolve from the destination client’s working directory.");
        }
        references.UnionWith(copy.Env.Where(item => !item.HasValue).Select(item => item.Key));
        if (references.Count > 0)
        {
            warnings.Add("Variable references and empty values are copied as written. " +
                "Check that the destination client can resolve them.");
        }

        Preview = new ProjectCopyPreview(
            Token: blockers.Count == 0 ? Guid.NewGuid().ToString("D").ToUpperInvariant() : null,
            SourceFile: location.File,
            TargetFile: targetPath,
            Transport: copy.Transport.Raw(),
            EnvironmentKeys: copy.EnvironmentKeys,
            UnresolvedVariables: references.ToArray(),
            Warnings: warnings,
            Blockers: blockers);
    }

    /// <summary>Whether a plan built from a fresh read is still the one that was reviewed.</summary>
    /// <remarks>
    /// Re-resolving against current discovery also catches detached folders, path
    /// overrides, parked-name collisions and retargeted symbolic links.
    /// </remarks>
    public bool Matches(ProjectDefinitionCopy other) =>
        Preview.Token is not null && other.Preview.Token is not null &&
        SameDraft(Draft, other.Draft) &&
        _sourceDigest == other._sourceDigest &&
        _targetDigest == other._targetDigest &&
        string.Equals(Preview.SourceFile, other.Preview.SourceFile, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Preview.TargetFile, other.Preview.TargetFile, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_destination, other._destination, StringComparison.OrdinalIgnoreCase) &&
        Source == other.Source &&
        Target == other.Target;

    private static bool SameDraft(ServerDraft left, ServerDraft right) =>
        left.Name == right.Name &&
        left.Transport == right.Transport &&
        left.Command == right.Command &&
        left.Url == right.Url &&
        left.Args.SequenceEqual(right.Args, StringComparer.Ordinal) &&
        left.Env.Select(entry => (entry.Key, entry.Value)).SequenceEqual(right.Env.Select(entry => (entry.Key, entry.Value)));

    private static bool HasOneEntry(string file, string identity)
    {
        try
        {
            var root = JsonDocument.ParseFile(file).Root;
            var maps = (root.Members ?? []).Where(member => member.Key is "mcpServers" or "servers").ToArray();
            return maps.Length == 1 &&
                maps[0].Value.Members is { } entries &&
                entries.Count(member => Server.Identity(member.Key) == identity) == 1;
        }
        catch (Exception error) when (error is JsonParseException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Resolved(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(FinalPath.Resolve(path));
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool ValidFieldTypes(JsonNode root)
    {
        foreach (var key in new[] { "command", "url", "type" })
        {
            if (root[key] is { } value && value.StringValue is null) return false;
        }
        if (root["type"]?.StringValue is { } type && Transports.FromRaw(type) is null) return false;
        if (root["args"] is { } args && args.Elements?.All(element => element.StringValue is not null) != true)
        {
            return false;
        }
        if (root["env"] is { } env)
        {
            if (env.Members is not { } pairs ||
                pairs.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != pairs.Count ||
                !pairs.All(pair => pair.Value.StringValue is not null))
            {
                return false;
            }
        }
        return true;
    }

    private static (IReadOnlySet<string> Environment, bool RequiresProject) Substitutions(JsonNode node)
    {
        if (node.StringValue is { } value)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in VariablePattern.Matches(value))
            {
                names.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            }
            var scoped = ContextVariables.Where(name => value.Contains("${" + name + "}", StringComparison.Ordinal)).ToArray();
            names.ExceptWith(scoped);
            var requiresProject = scoped.Length > 0 ||
                ContextPrefixes.Any(prefix => value.Contains(prefix, StringComparison.Ordinal));
            return (names, requiresProject);
        }

        var all = new HashSet<string>(StringComparer.Ordinal);
        var project = false;
        var children = node.Elements ?? node.Members?.Select(member => member.Value).ToArray() ?? [];
        foreach (var child in children)
        {
            var (names, requiresProject) = Substitutions(child);
            all.UnionWith(names);
            project = project || requiresProject;
        }
        return (all, project);
    }
}
