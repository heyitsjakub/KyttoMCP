using System.Security.Cryptography;
using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.Core.Settings;

/// <summary>
/// A folder the user chose so Kytto can show the MCP servers its repository
/// configures (roadmap §12).
/// </summary>
/// <remarks>
/// <para>
/// Repository files are also an attack path: a compromised package can commit
/// <c>.cursor\mcp.json</c> or <c>.mcp.json</c> into a project, and the client then
/// offers that server the next time the folder is opened. Kytto used to see only
/// user-level configs, so such a server was invisible here.
/// </para>
/// <para>
/// The boundary is deliberate. Only folders the user picked, only the fixed file
/// names in <see cref="ProjectFolders.Files"/>, no recursion into subfolders and no
/// scanning of the profile. Every file is read-only, and a server found only in a
/// repository is never started by Kytto unless the user asks, because a health check
/// runs its command.
/// </para>
/// </remarks>
/// <param name="Path">Fully qualified path of the folder.</param>
/// <param name="Label">What the folder is called on screen. Defaults to its last component.</param>
public sealed record ProjectFolder(Guid Id, string Path, string Label = "");

/// <summary>One project-level file Kytto reads inside a chosen folder, and who reads it.</summary>
/// <param name="Name">The relative path as people write it, which is also what the column is called.</param>
public sealed record ProjectConfigFile(string Name, string[] Segments, string ReadBy);

public static class ProjectFolders
{
    /// <summary>Exact relative paths; nothing else in the folder is opened.</summary>
    public static readonly IReadOnlyList<ProjectConfigFile> Files =
    [
        // The portable project file. Claude Code reads it as its project scope.
        new(".mcp.json", [".mcp.json"], "Claude Code and other clients that read a project .mcp.json"),
        new(".cursor/mcp.json", [".cursor", "mcp.json"], "Cursor"),
        // VS Code's workspace file; its map is `servers`, not `mcpServers`.
        new(".vscode/mcp.json", [".vscode", "mcp.json"], "VS Code"),
    ];

    /// <summary>"a, b and c", for the sentences that name every file.</summary>
    public static string FileNames =>
        string.Join(", ", Files.Take(Files.Count - 1).Select(file => file.Name)) + " and " + Files[^1].Name;

    public static string PathOf(ProjectFolder folder, ProjectConfigFile file) =>
        System.IO.Path.Combine([folder.Path, .. file.Segments]);

    /// <summary>A stable id per folder and file.</summary>
    /// <remarks>
    /// So a server's row, its review state and the sidebar's fold survive relaunches.
    /// The same name-based UUID the macOS app derives, from
    /// <c>kytto-project:&lt;FOLDER-UUID&gt;:&lt;file&gt;</c>.
    /// </remarks>
    public static Guid SourceId(Guid folder, ProjectConfigFile file)
    {
        var seed = $"kytto-project:{folder.ToString("D").ToUpperInvariant()}:{file.Name}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // name-based UUID
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>One read-only source per project file that exists in the folder.</summary>
    /// <remarks>
    /// A file that does not exist yet gets no column: three empty "no config" entries
    /// per folder would bury the one that matters. Discovery watches the missing ones,
    /// so a newly committed file appears without a relaunch.
    /// </remarks>
    public static IReadOnlyList<CustomConfigSource> Sources(
        ProjectFolder folder,
        Func<string, bool>? fileExists = null)
    {
        if (!KyttoSettings.IsUsablePath(folder.Path)) return [];
        var exists = fileExists ?? File.Exists;
        var sources = new List<CustomConfigSource>();
        foreach (var file in Files)
        {
            var path = PathOf(folder, file);
            if (!exists(path)) continue;
            sources.Add(new CustomConfigSource(
                SourceId(folder.Id, file),
                $"{folder.Label} · {file.Name}",
                path,
                ConfigurationScope.Workspace,
                folder.Label)
            {
                IsRepositoryFile = true,
                ShortName = file.Name,
                Quirks =
                    $"A repository file in {folder.Label}, read by {file.ReadBy} when that folder is open. " +
                    "Kytto only reads it and never writes to it. A server found only here is left out of " +
                    "Check all: a check starts the server's command on this computer, so it runs only when you ask.",
            });
        }
        return sources;
    }

    /// <summary>Every file Kytto would read in the folder, whether it exists yet or not.</summary>
    public static IEnumerable<string> CandidatePaths(ProjectFolder folder) =>
        KyttoSettings.IsUsablePath(folder.Path) ? Files.Select(file => PathOf(folder, file)) : [];

    /// <summary>
    /// Whether every client that lists this server reads it from a repository file, so
    /// nothing outside a project vouches for it.
    /// </summary>
    /// <remarks>
    /// <c>absent</c> entries are the matrix's empty cells, not places the server is
    /// configured.
    /// </remarks>
    public static bool IsRepositoryOnly(Server server, IReadOnlySet<ClientKey> repositoryClients)
    {
        var configuredIn = server.EnabledIn.Where(pair => pair.Value != Enablement.Absent).Select(pair => pair.Key).ToArray();
        return configuredIn.Length > 0 && configuredIn.All(repositoryClients.Contains);
    }

    /// <summary>Fully qualified, each folder once, each label filled in.</summary>
    /// <remarks>
    /// A relative path would resolve against whatever directory the app was started
    /// from. Paths are compared the way Windows compares them: case-insensitively,
    /// with a trailing separator ignored.
    /// </remarks>
    internal static IReadOnlyList<ProjectFolder> Sanitized(IReadOnlyList<ProjectFolder>? folders)
    {
        var result = new List<ProjectFolder>();
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders ?? [])
        {
            if (folder is null || folder.Id == Guid.Empty || !KyttoSettings.IsUsablePath(folder.Path)) continue;
            string path;
            try
            {
                path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder.Path));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!ids.Add(folder.Id) || !paths.Add(path)) continue;
            var label = (folder.Label ?? "").Trim();
            if (label.Length == 0) label = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
            result.Add(folder with { Path = path, Label = label });
        }
        return result;
    }
}
