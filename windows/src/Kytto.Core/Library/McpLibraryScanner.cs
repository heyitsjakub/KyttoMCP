using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Settings;

namespace Kytto.Core.Library;

/// <summary>A safe, temporary candidate found under one selected directory.</summary>
/// <remarks>
/// The native-only server is retained behind the record so import can use the
/// normal authoring pipeline without sending command arguments or env values to
/// the web layer. The public projection contains summaries and key names only.
/// </remarks>
public sealed record McpLibraryCandidate(
    string ID,
    string Name,
    string RelativeLocation,
    Transport Transport,
    string CommandSummary,
    IReadOnlyList<string> EnvironmentKeys,
    IReadOnlyList<ClientId> DetectedClients,
    IReadOnlyList<ClientId> MissingClients,
    IReadOnlyList<string> Warnings)
{
    internal Server? NativeServer { get; init; }

    internal ServerDraft ToDraft() => NativeServer is { } server
        ? ServerDraft.Editing(server)
        : new ServerDraft { Name = Name, Transport = Transport };
}

public sealed record McpLibraryScanResult(
    IReadOnlyList<McpLibraryCandidate> Candidates,
    IReadOnlyList<string> Warnings,
    bool IsComplete);

/// <summary>
/// Bounded read-only scanner for a directory the user explicitly selected (§7).
/// </summary>
public sealed class McpLibraryScanner
{
    public const int DefaultMaxDepth = 4;
    public const int DefaultMaxEntries = 2_000;
    public const int MaxFileBytes = 512 * 1024;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", ".venv", "venv", "dist", "build", ".tox",
    };

    private static readonly HashSet<string> JsonExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".jsonc",
    };

    public McpLibraryScanResult Scan(
        string root,
        IReadOnlyList<Server>? configuredServers = null,
        int maxDepth = DefaultMaxDepth,
        int maxEntries = DefaultMaxEntries)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The library root must be a fully qualified path.", nameof(root));
        }
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException("The selected library directory no longer exists.");
        }
        if (maxDepth < 0 || maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxDepth));

        var configured = configuredServers ?? [];
        var candidates = new List<McpLibraryCandidate>();
        var warnings = new List<string>();
        var visitedEntries = 0;
        var complete = true;
        Walk(new DirectoryInfo(fullRoot), 0);

        return new McpLibraryScanResult(
            candidates.OrderBy(candidate => candidate.RelativeLocation, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .ToArray(),
            warnings,
            complete);

        void Walk(DirectoryInfo directory, int depth)
        {
            if (depth > maxDepth) return;
            if (IsReparsePoint(directory))
            {
                warnings.Add($"Skipped symlinked directory {Relative(directory.FullName)}.");
                return;
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos()
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read {Relative(directory.FullName)}: {error.Message}.");
                return;
            }

            foreach (var entry in entries)
            {
                if (!complete) return;
                if (++visitedEntries > maxEntries)
                {
                    complete = false;
                    warnings.Add($"Library scan stopped after {maxEntries} entries.");
                    return;
                }
                if (entry is DirectoryInfo child)
                {
                    if (SkippedDirectories.Contains(child.Name)) continue;
                    if (depth < maxDepth) Walk(child, depth + 1);
                    else complete = false;
                    continue;
                }
                if (entry is not FileInfo file || IsReparsePoint(file)) continue;

                var extension = file.Extension;
                if (JsonExtensions.Contains(extension) || extension.Equals(".toml", StringComparison.OrdinalIgnoreCase))
                {
                    ScanConfiguration(file);
                }
                if (file.Name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                {
                    ScanPackage(file);
                }
                else if (file.Extension.Equals(".py", StringComparison.OrdinalIgnoreCase) && IsLikelyPythonEntry(file.Name))
                {
                    AddPythonSuggestion(file);
                }
            }
        }

        void ScanConfiguration(FileInfo file)
        {
            string text;
            try
            {
                if (file.Length > MaxFileBytes)
                {
                    warnings.Add($"Skipped {Relative(file.FullName)}: the file is larger than the bounded scan limit.");
                    return;
                }
                text = File.ReadAllText(file.FullName, new UTF8Encoding(false, true));
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                warnings.Add($"Could not read {Relative(file.FullName)}: {error.Message}.");
                return;
            }

            var source = new CustomConfigSource(Guid.NewGuid(), file.Name, file.FullName);
            var parsed = CustomConfigDiscovery.Read(source, _ => text);
            var diagnostics = parsed.Diagnostics.Select(diagnostic => diagnostic.Message).ToArray();
            if (parsed.Servers.Count == 0)
            {
                if (diagnostics.Length > 0) warnings.AddRange(diagnostics);
                return;
            }

            foreach (var server in parsed.Servers)
            {
                var serverWarnings = diagnostics.ToList();
                serverWarnings.Add("Read-only library candidate. Kytto did not execute or verify it.");
                AddCandidate(server, file, serverWarnings);
            }
        }

        void ScanPackage(FileInfo file)
        {
            JsonDocument document;
            try
            {
                if (file.Length > MaxFileBytes)
                {
                    warnings.Add($"Skipped {Relative(file.FullName)}: the file is larger than the bounded scan limit.");
                    return;
                }
                document = JsonDocument.Parse(
                    File.ReadAllText(file.FullName, new UTF8Encoding(false, true)),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException or DecoderFallbackException or JsonException)
            {
                warnings.Add($"Could not inspect {Relative(file.FullName)} as package.json: {error.Message}.");
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var packageName = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;
                var entries = new List<string>();
                if (root.TryGetProperty("bin", out var bin))
                {
                    if (bin.ValueKind == JsonValueKind.String && bin.GetString() is { } single) entries.Add(single);
                    else if (bin.ValueKind == JsonValueKind.Object)
                    {
                        entries.AddRange(bin.EnumerateObject()
                            .Where(pair => pair.Value.ValueKind == JsonValueKind.String)
                            .Select(pair => pair.Value.GetString()!)
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                    }
                }
                if (entries.Count == 0 && root.TryGetProperty("main", out var main) &&
                    main.ValueKind == JsonValueKind.String && main.GetString() is { } mainPath)
                {
                    entries.Add(mainPath);
                }
                foreach (var entry in entries.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                {
                    var relativeEntry = SafeRelative(file.DirectoryName!, entry);
                    if (relativeEntry is null) continue;
                    var serverName = packageName ?? Path.GetFileNameWithoutExtension(file.Name);
                    var server = SuggestedServer(
                        name: serverName,
                        command: "node",
                        args: [relativeEntry],
                        provenance: ServerProvenance.Infer(Transport.Stdio, "node", [relativeEntry], null));
                    AddCandidate(server, file,
                    [
                        "Suggested from package.json; the entry point was not executed or verified.",
                        "Read-only library candidate. Import only after reviewing the command.",
                    ]);
                }
            }
        }

        void AddPythonSuggestion(FileInfo file)
        {
            var relative = Relative(file.FullName);
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var server = SuggestedServer(
                name,
                "python",
                [relative],
                ServerProvenance.Infer(Transport.Stdio, "python", [relative], null));
            AddCandidate(server, file,
            [
                "Suggested from a Python entry point; the file was not executed or verified.",
                "Read-only library candidate. Import only after reviewing the command.",
            ]);
        }

        void AddCandidate(Server server, FileInfo file, IReadOnlyList<string> candidateWarnings)
        {
            var known = configured.FirstOrDefault(candidate => candidate.Id == server.Id);
            var detected = (known?.EnabledIn ?? server.EnabledIn)
                .Where(pair => pair.Key.BuiltIn is not null && pair.Value != Enablement.Absent)
                .Select(pair => pair.Key.BuiltIn!.Value)
                .Distinct()
                .OrderBy(id => id.Raw(), StringComparer.Ordinal)
                .ToArray();
            var missing = ClientIds.All.Except(detected).ToArray();
            var candidate = new McpLibraryCandidate(
                ID: $"library.{Guid.NewGuid():D}",
                Name: server.Name,
                RelativeLocation: Relative(file.FullName),
                Transport: server.Transport,
                CommandSummary: server.CommandSummary,
                EnvironmentKeys: server.Env.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray(),
                DetectedClients: detected,
                MissingClients: missing,
                Warnings: candidateWarnings)
            {
                NativeServer = server,
            };
            candidates.Add(candidate);
        }

        Server SuggestedServer(
            string name,
            string command,
            IReadOnlyList<string> args,
            ServerProvenance provenance) => new()
            {
                Id = Server.Identity(name),
                Name = name,
                Transport = Transport.Stdio,
                Command = command,
                Args = args,
                Env = [],
                Url = null,
                EnabledIn = [],
                Origin = new Origin.ConfigFile(ClientKey.Custom(Guid.NewGuid())),
                Fingerprint = Server.MakeFingerprint(command, args, null),
                IsBundled = false,
                IsReadOnly = true,
                DefinitionSource = "",
                Provenance = provenance,
            };

        string Relative(string path) => Path.GetRelativePath(fullRoot, path);

        string? SafeRelative(string directory, string path)
        {
            if (Path.IsPathFullyQualified(path)) return null;
            var combined = Path.GetFullPath(Path.Combine(directory, path));
            var relative = Path.GetRelativePath(fullRoot, combined);
            return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? null
                : relative;
        }
    }

    private static bool IsLikelyPythonEntry(string name) =>
        name.Equals("server.py", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("mcp_server.py", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("main.py", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(FileSystemInfo entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null;
}
