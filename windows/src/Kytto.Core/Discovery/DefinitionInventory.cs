using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core;

/// <summary>
/// Every file-backed copy of one server from the accepted discovery pass, and which
/// one Claude Code prefers where several meet in a workspace.
/// </summary>
/// <remarks>
/// <para>
/// The matrix merges a name into one row, so a server defined in the user file, in a
/// project's <c>.mcp.json</c> and in a Claude Code local scope prints one command and
/// hides the other two. This is the list underneath it. A parked copy has no file
/// until it is restored, and installed software (extensions, plugins) has no entry
/// anyone wrote, so neither appears.
/// </para>
/// <para>
/// Priority follows Claude Code's documented scope hierarchy — local, then project,
/// then user (https://code.claude.com/docs/en/mcp#scope-hierarchy-and-precedence) —
/// and is stated only where it can be: for enabled copies that share a workspace and
/// an exact server key. Other clients' precedence is not documented, so it stays
/// unknown rather than guessed.
/// </para>
/// </remarks>
public sealed record DefinitionInventory(
    IReadOnlyList<DefinitionInventory.Copy> Copies,
    IReadOnlyList<DefinitionInventory.Priority> Priorities,
    bool OverlapsScopes)
{
    /// <param name="File">A display string. The web layer shows it and never builds on it.</param>
    public sealed record Copy(
        string ClientID,
        string Name,
        string File,
        string Scope,
        bool Repository,
        string Transport);

    /// <param name="Candidates">The clients whose copies compete, highest priority first.</param>
    public sealed record Priority(string Workspace, string Winner, IReadOnlyList<string> Candidates);

    public static IReadOnlyDictionary<string, DefinitionInventory> Inventories(
        DiscoveryResult result,
        IReadOnlyList<ProjectFolder> folders)
    {
        // Claude Code reads a chosen folder's `.mcp.json` as that folder's project scope.
        var portable = ProjectFolders.Files.First(file => file.Name == ".mcp.json");
        var portableFolders = new Dictionary<ClientKey, string>();
        foreach (var folder in folders)
        {
            portableFolders.TryAdd(ClientKey.Custom(ProjectFolders.SourceId(folder.Id, portable)), folder.Path);
        }

        var inventories = new Dictionary<string, DefinitionInventory>(StringComparer.Ordinal);
        foreach (var server in result.Servers)
        {
            inventories.TryAdd(server.Id, For(server, result.Clients, portableFolders));
        }
        return inventories;
    }

    private static DefinitionInventory For(
        Server server,
        IReadOnlyList<DiscoveredClient> clients,
        IReadOnlyDictionary<ClientKey, string> portableFolders)
    {
        var copies = new List<Copy>();
        Copy? user = null;
        // Keyed by a normalized path: Claude Code spells a project's key with forward
        // slashes on Windows, while a folder chosen here is a full path with backslashes.
        var local = new Dictionary<string, (string Workspace, Copy Copy)>(StringComparer.OrdinalIgnoreCase);
        var project = new Dictionary<string, (string Workspace, Copy Copy)>(StringComparer.OrdinalIgnoreCase);
        var hasWorkspace = false;

        foreach (var client in clients)
        {
            if (!server.DefinitionsByClient.TryGetValue(client.Id, out var definition) ||
                !server.Locations.TryGetValue(client.Id, out var location))
            {
                continue;
            }
            var folder = portableFolders.GetValueOrDefault(client.Id);
            var scope = folder is not null ? "Project · " + folder : ScopeDescription(client, location);
            var copy = new Copy(
                client.Id.Raw(),
                location.Name,
                location.File,
                scope,
                client.IsRepositoryFile,
                definition.Transport.Raw());
            copies.Add(copy);
            if (client.ConfigurationScope == ConfigurationScope.Workspace) hasWorkspace = true;

            if (server.EnabledIn.GetValueOrDefault(client.Id, Enablement.Absent) != Enablement.Enabled) continue;
            if (client.Id == ClientId.ClaudeCode) user = copy;
            if (location.ClaudeLocalWorkspace is { } workspace) local[WorkspaceKey(workspace)] = (workspace, copy);
            if (folder is not null) project[WorkspaceKey(folder)] = (folder, copy);
        }

        var priorities = new List<Priority>();
        foreach (var key in local.Keys.Union(project.Keys, StringComparer.OrdinalIgnoreCase))
        {
            // A workspace and an exact server key are both required for a comparison.
            var ordered = new[]
            {
                local.TryGetValue(key, out var localCopy) ? localCopy.Copy : null,
                project.TryGetValue(key, out var projectCopy) ? projectCopy.Copy : null,
                user,
            }.OfType<Copy>().ToArray();
            var winner = ordered.FirstOrDefault(candidate => ordered.Any(other =>
                other.ClientID != candidate.ClientID &&
                string.Equals(other.Name, candidate.Name, StringComparison.Ordinal)));
            if (winner is null) continue;
            // The folder as the user chose it reads better than Claude Code's spelling.
            var shown = project.TryGetValue(key, out var chosen) ? chosen.Workspace : local[key].Workspace;
            priorities.Add(new Priority(
                shown,
                winner.ClientID,
                ordered
                    .Where(candidate => string.Equals(candidate.Name, winner.Name, StringComparison.Ordinal))
                    .Select(candidate => candidate.ClientID)
                    .ToArray()));
        }
        priorities.Sort((left, right) => string.CompareOrdinal(left.Workspace, right.Workspace));

        return new DefinitionInventory(copies, priorities, copies.Count > 1 && hasWorkspace);
    }

    /// <summary>How a copy's scope is named on screen.</summary>
    public static string ScopeDescription(DiscoveredClient client, DefinitionLocation location)
    {
        if (location.ClaudeLocalWorkspace is { } workspace) return "Local · " + workspace;
        if (client.Id == ClientId.ClaudeCode) return "User";
        var scope = client.ConfigurationScope.DisplayName();
        return client.ScopeLabel.Length == 0 || client.ScopeLabel == scope
            ? scope
            : scope + " · " + client.ScopeLabel;
    }

    /// <summary>Two spellings of one folder compare equal; anything unparseable compares as written.</summary>
    internal static string WorkspaceKey(string path)
    {
        var text = path.Replace('/', '\\');
        try
        {
            text = Path.GetFullPath(text);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Compared as written.
        }
        return Path.TrimEndingDirectorySeparator(text);
    }
}
