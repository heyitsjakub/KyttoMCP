using System.Text;
using System.Security.Cryptography;
using Kytto.Core.Clients;
using Kytto.Core.Health;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Settings;
using Kytto.Core.Toml;

namespace Kytto.Core;

public sealed record DiscoveredClient(
    ClientKey Id,
    string DisplayName,
    string IconAsset,
    /// <summary>The application (or CLI) is present on this machine.</summary>
    bool IsInstalled,
    /// <summary>
    /// At least one of the client's config sources exists on disk. A config with no
    /// client is a leftover — <c>~/.cursor</c> outlives uninstalling Cursor — and is
    /// shown rather than silently hidden.
    /// </summary>
    bool ConfigExists,
    string ConfigPathDisplay,
    string ConfigFormatDisplay,
    string OffSwitchSummary,
    int ServerCount,
    string SchemaQuirks,
    bool IsReadOnly,
    ConfigurationScope ConfigurationScope,
    string ScopeLabel,
    string? ShortName = null);

public sealed record DiscoveryResult(
    IReadOnlyList<DiscoveredClient> Clients,
    IReadOnlyList<Server> Servers,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// A server that was removed from a client only in order to switch it off.
/// </summary>
/// <remarks>
/// Not in any config file any more, so without it the row would vanish from the
/// matrix and the user would have no way to switch it back on. A parked server is
/// a disabled server, and it is shown as one.
/// </remarks>
public sealed record ParkedEntry(ClientId ClientID, string ServerName, string SourceText);

/// <summary>
/// Reads every client's configuration and normalizes it into one list of servers.
/// </summary>
/// <remarks>
/// Strictly read-only (§6.6): nothing here creates, touches or writes a file,
/// including Kytto's own.
/// </remarks>
public sealed class Discovery
{
    private readonly string _home;
    private readonly IAppLocator _locator;
    private readonly IReadOnlyList<ClientDescriptor> _descriptors;
    private readonly IReadOnlyList<CustomConfigSource> _customSources;
    private readonly IReadOnlyList<ParkedEntry> _parked;
    private readonly IReadOnlyDictionary<string, ServerMetadata> _metadata;
    private readonly ClientPathResolver _resolver;

    /// <summary>
    /// Records what every file looked like when it was read, so a later write can
    /// tell whether it still looks the way Kytto last saw it.
    /// </summary>
    private readonly Action<string, string?>? _recordDigest;

    public Discovery(
        string home,
        IAppLocator locator,
        IReadOnlyList<ClientDescriptor>? descriptors = null,
        Action<string, string?>? recordDigest = null,
        IReadOnlyList<ParkedEntry>? parked = null,
        IReadOnlyDictionary<string, ServerMetadata>? metadata = null,
        IReadOnlyDictionary<string, string>? pathOverrides = null,
        IReadOnlyList<CustomConfigSource>? customSources = null)
    {
        _home = home;
        _locator = locator;
        _descriptors = descriptors ?? ClientRegistry.All;
        _recordDigest = recordDigest;
        _parked = parked ?? [];
        _metadata = metadata ?? new Dictionary<string, ServerMetadata>();
        _resolver = new ClientPathResolver(home, pathOverrides);
        _customSources = customSources ?? [];
    }

    /// <summary>Every file or installed-package tree whose contents Kytto reads.</summary>
    /// <remarks>
    /// Config files stay exact targets even though their parent directories are
    /// watched underneath: Claude Code keeps one at the profile root, and watching
    /// that whole tree would turn every unrelated file write into a UI refresh.
    /// Package roots remain recursive because manifests can live below them.
    /// </remarks>
    public IReadOnlyList<ConfigWatchTarget> WatchTargets()
    {
        var result = new Dictionary<string, ConfigWatchTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in _descriptors)
        {
            foreach (var source in descriptor.Sources)
            {
                switch (source)
                {
                    case ConfigSource.ServerMap map:
                        // The editable map may be redirected in Settings. Reading
                        // and writing already honour that override; watching only
                        // the registry candidates would miss an external edit to
                        // the file Kytto is actually using (§6.4).
                        AddFile(_resolver.ResolveServerMap(map.File, descriptor.Id));
                        // Every candidate, not just the one in use: a client
                        // reinstalled the other way starts writing somewhere Kytto
                        // was not watching.
                        foreach (var path in map.File.ResolveAll(_home)) AddFile(path);
                        if (map.Enablement is EnablementStrategy.DenyList deny)
                        {
                            foreach (var path in deny.File.ResolveAll(_home)) AddFile(path);
                        }
                        break;
                    case ConfigSource.ExtensionBundles bundles:
                        foreach (var path in bundles.Directory.ResolveAll(_home)) AddTree(path);
                        foreach (var path in bundles.SettingsDirectory.ResolveAll(_home)) AddTree(path);
                        break;
                    case ConfigSource.BundledPackages packages:
                        // The client owns this tree and rewrites it on its own
                        // schedule; watching the root is enough to notice a plugin
                        // arriving or leaving.
                        AddTree(_resolver.Resolve(packages.Root));
                        break;
                }
            }
        }
        foreach (var source in _customSources)
        {
            AddFile(source.Path);
        }
        return result.Values.OrderBy(target => target.Path, StringComparer.Ordinal).ToArray();

        void AddFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            result[$"file:{fullPath}"] = ConfigWatchTarget.File(fullPath);
        }

        void AddTree(string path)
        {
            var fullPath = Path.GetFullPath(path);
            result[$"tree:{fullPath}"] = ConfigWatchTarget.Tree(fullPath);
        }
    }

    /// <summary>Reads a file and records what it looked like. Null if absent.</summary>
    private string? ReadTracked(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _recordDigest?.Invoke(path, null);
            return null;
        }

        _recordDigest?.Invoke(path, Digest.Of(data));

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    public DiscoveryResult Run()
    {
        var clients = new List<DiscoveredClient>();
        var projectClientIDs = new List<ClientKey>();
        var diagnostics = new List<Diagnostic>();
        // Keyed by `Server.Identity`, so the same name in three clients is one row.
        var merged = new Dictionary<string, Server>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var descriptor in _descriptors)
        {
            var configExists = false;
            var countForClient = 0;
            var projectClients = new List<DiscoveredClient>();

            foreach (var source in descriptor.Sources)
            {
                var outcome = Read(source, descriptor);
                diagnostics.AddRange(outcome.Diagnostics);
                configExists = configExists || outcome.SourceExists;

                foreach (var found in outcome.Servers)
                {
                    countForClient++;
                    var key = Server.Identity(found.Name);

                    if (merged.TryGetValue(key, out var existing))
                    {
                        // The disagreement itself is not reported here any more.
                        // Every client's copy is kept, so `ServerDrift` can say which
                        // clients differ and in what — a diagnostic in the footer
                        // could only say that something, somewhere, did.
                        existing.EnabledIn[descriptor.Id] = found.Enablement;
                        existing.DefinitionsByClient[descriptor.Id] =
                            ClientDefinition.From(found.Server);
                    }
                    else
                    {
                        var server = found.Server;
                        server.EnabledIn[descriptor.Id] = found.Enablement;
                        server.DefinitionsByClient[descriptor.Id] =
                            ClientDefinition.From(found.Server);
                        merged[key] = server;
                        order.Add(key);
                    }
                }

                foreach (var project in outcome.Projects)
                {
                    projectClientIDs.Add(project.ClientID);
                    foreach (var found in project.Servers)
                    {
                        var key = Server.Identity(found.Name);
                        if (merged.TryGetValue(key, out var existing))
                        {
                            existing.EnabledIn[project.ClientID] = found.Enablement;
                            existing.DefinitionsByClient[project.ClientID] =
                                ClientDefinition.From(found.Server);
                        }
                        else
                        {
                            var server = found.Server;
                            server.EnabledIn[project.ClientID] = found.Enablement;
                            server.DefinitionsByClient[project.ClientID] =
                                ClientDefinition.From(found.Server);
                            merged[key] = server;
                            order.Add(key);
                        }
                    }

                    projectClients.Add(new DiscoveredClient(
                        Id: project.ClientID,
                        DisplayName: project.DisplayName,
                        IconAsset: descriptor.IconAsset,
                        IsInstalled: true,
                        ConfigExists: true,
                        ConfigPathDisplay: _resolver.DisplayServerMap(
                            descriptor.EditableServerMap?.File ?? descriptor.Sources[0].Path,
                            descriptor.Id),
                        ConfigFormatDisplay: "JSON",
                        OffSwitchSummary:
                            "This Claude Code project scope is read-only. Kytto never changes " +
                            "nested projects entries.",
                        ServerCount: project.Servers.Count,
                        SchemaQuirks:
                            "Read-only workspace scope discovered from the existing " +
                            "Claude Code projects map. Kytto does not crawl or edit project files.",
                        IsReadOnly: true,
                        ConfigurationScope: ConfigurationScope.Workspace,
                        ScopeLabel: project.Path,
                        ShortName: project.ShortName));
                }
            }

            // The map the user can actually author into is what the client screen
            // describes: its format and its off switch are the two things that make
            // one client behave unlike the next.
            var editable = descriptor.EditableServerMap;
            clients.Add(new DiscoveredClient(
                Id: descriptor.Id,
                DisplayName: descriptor.DisplayName,
                IconAsset: descriptor.IconAsset,
                IsInstalled: IsInstalled(descriptor),
                ConfigExists: configExists,
                ConfigPathDisplay: editable is not null
                    ? _resolver.DisplayServerMap(editable.File, descriptor.Id)
                    : descriptor.Sources.FirstOrDefault()?.Path.DisplayString() ?? "",
                ConfigFormatDisplay: editable?.Format.DisplayName() ?? "",
                OffSwitchSummary: descriptor.OffSwitchSummary,
                ServerCount: countForClient,
                SchemaQuirks: descriptor.SchemaQuirks,
                IsReadOnly: false,
                ConfigurationScope: ConfigurationScope.Global,
                ScopeLabel: ""));
            clients.AddRange(projectClients);
        }

        // Custom sources are discovery-only columns and deliberately come after
        // the closed five-client registry. Nothing in this loop can resolve them
        // to a writable ClientId.
        foreach (var source in _customSources)
        {
            var outcome = CustomConfigDiscovery.Read(source, ReadTracked);
            diagnostics.AddRange(outcome.Diagnostics);
            foreach (var server in outcome.Servers)
            {
                var key = Server.Identity(server.Name);
                if (merged.TryGetValue(key, out var existing))
                {
                    existing.EnabledIn[source.ClientID] = Enablement.Enabled;
                    existing.DefinitionsByClient[source.ClientID] = ClientDefinition.From(server);
                }
                else
                {
                    server.EnabledIn[source.ClientID] = Enablement.Enabled;
                    server.DefinitionsByClient[source.ClientID] = ClientDefinition.From(server);
                    merged[key] = server;
                    order.Add(key);
                }
            }

            clients.Add(new DiscoveredClient(
                Id: source.ClientID,
                DisplayName: source.DisplayName,
                IconAsset: "client-custom",
                IsInstalled: true,
                ConfigExists: outcome.SourceExists,
                ConfigPathDisplay: source.Path,
                ConfigFormatDisplay: "Auto-detected · read-only",
                OffSwitchSummary: "This custom source is read-only. Kytto never changes its file.",
                ServerCount: outcome.Servers.Count,
                SchemaQuirks: "Custom source: JSON/JSONC or TOML is auto-detected from its contents. " +
                    "Kytto discovers supported top-level server maps and never changes this file.",
                IsReadOnly: true,
                ConfigurationScope: source.Scope,
                ScopeLabel: source.ScopeLabel ?? ""));
        }

        // Bring back anything Kytto parked. It is not in a config file any more, but
        // from the user's point of view it is a server they switched off.
        foreach (var entry in _parked)
        {
            if (!_descriptors.Any(descriptor => descriptor.Id == entry.ClientID)) continue;
            var key = Server.Identity(entry.ServerName);

            // Parked text is in whatever the client it came from is spelled in.
            var parkedServer = ParkedFields(entry)?.MakeServer(
                name: entry.ServerName,
                origin: new Origin.ConfigFile(entry.ClientID),
                isBundled: false,
                sourceText: entry.SourceText);

            if (merged.TryGetValue(key, out var existing))
            {
                existing.EnabledIn[entry.ClientID] = Enablement.Disabled;
                // A parked definition is still this client's definition — it is what
                // switching the server back on will put back, byte for byte. Leaving
                // it out of the comparison would call a client identical to the
                // others on the strength of holding nothing.
                if (parkedServer is not null
                    && !existing.DefinitionsByClient.ContainsKey(entry.ClientID))
                {
                    existing.DefinitionsByClient[entry.ClientID] =
                        ClientDefinition.From(parkedServer);
                }
                continue;
            }

            if (parkedServer is not { } server) continue;
            server.EnabledIn[entry.ClientID] = Enablement.Disabled;
            server.DefinitionsByClient[entry.ClientID] = ClientDefinition.From(server);
            merged[key] = server;
            order.Add(key);
        }

        // Fill in `absent` so the web layer can render a full grid without knowing
        // which clients exist.
        var servers = new List<Server>();
        foreach (var key in order)
        {
            if (!merged.TryGetValue(key, out var server)) continue;
            foreach (var id in _descriptors
                         .Select(descriptor => (ClientKey)descriptor.Id)
                         .Concat(projectClientIDs)
                         .Concat(_customSources.Select(source => source.ClientID))
                         .Distinct())
            {
                if (!server.EnabledIn.ContainsKey(id)) server.EnabledIn[id] = Enablement.Absent;
            }
            // Measurements are looked up rather than taken: nothing here spawns
            // anything (§6).
            if (_metadata.TryGetValue(server.Id, out var metadata))
            {
                server.Health = metadata.Health;
                server.TokenWeight = metadata.TokenWeight;
                if (metadata.Provenance is { } provenance &&
                    string.Equals(provenance.SourceKind, server.Provenance.SourceKind, StringComparison.Ordinal) &&
                    string.Equals(provenance.PackageName, server.Provenance.PackageName, StringComparison.Ordinal) &&
                    string.Equals(provenance.SourceURL, server.Provenance.SourceURL, StringComparison.Ordinal))
                {
                    server.Provenance = server.Provenance with
                    {
                        LatestVersion = provenance.LatestVersion,
                        LatestCheckedAt = provenance.LatestCheckedAt,
                        MaintenanceState = provenance.MaintenanceState,
                    };
                }
            }
            server.Provenance = server.Provenance.WithHealth(server.Health);
            servers.Add(server);
        }

        servers.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));

        return new DiscoveryResult(clients, servers, diagnostics);
    }

    /// <summary>
    /// Reads a parked definition back, in the format of the client it was taken
    /// from.
    /// </summary>
    /// <remarks>
    /// Codex never parks — its servers stay in the file when switched off — but the
    /// store is per-client and nothing else guarantees that stays true.
    /// </remarks>
    private ServerFields? ParkedFields(ParkedEntry entry)
    {
        var format = _descriptors
            .FirstOrDefault(descriptor => descriptor.Id == entry.ClientID)?
            .EditableServerMap?.Format ?? ConfigFormat.Json;

        try
        {
            if (format == ConfigFormat.Json)
            {
                return ServerFields.FromJson(JsonDocument.Parse(entry.SourceText).Root);
            }

            var document = TomlDocument.Parse(entry.SourceText);
            var table = document.Tables.FirstOrDefault(candidate => candidate.HeaderSpan is not null);
            if (table is null) return null;
            var envPath = table.Path.Append("env").ToArray();
            return ServerFields.FromToml(table, document.Table(envPath));
        }
        catch (Exception error) when (error is JsonParseException or TomlParseException)
        {
            return null;
        }
    }

    // MARK: - Detection

    private bool IsInstalled(ClientDescriptor descriptor) =>
        descriptor.InstallKeys.Any(key => _locator.ApplicationExists(key, _home))
        || descriptor.PackageFamilyNames.Any(_locator.PackageExists)
        || descriptor.ExecutableNames.Any(name => _locator.ExecutableExists(name, _home));

    // MARK: - Reading sources

    private sealed record FoundServer(string Name, Server Server, Enablement Enablement);

    private sealed class SourceOutcome
    {
        public List<FoundServer> Servers { get; } = [];
        public List<ProjectSource> Projects { get; } = [];
        public List<Diagnostic> Diagnostics { get; } = [];
        public bool SourceExists { get; set; }
    }

    private sealed record ProjectSource(
        ClientKey ClientID,
        string Path,
        string DisplayName,
        string ShortName,
        IReadOnlyList<FoundServer> Servers);

    /// <summary>
    /// Stable opaque identity for a Claude Code project entry. The path is only
    /// used natively to derive the id; it is never a write target (§4).
    /// </summary>
    public static ClientKey ProjectClientID(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("A project path cannot be blank.", nameof(projectPath));
        }

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(
            "kytto:claude-code-project\u0000" + projectPath));
        return ClientKey.Custom(new Guid(bytes));
    }

    private SourceOutcome Read(ConfigSource source, ClientDescriptor descriptor) => source switch
    {
        ConfigSource.ServerMap { Format: ConfigFormat.Json } map =>
            ReadJsonServerMap(map, descriptor),
        ConfigSource.ServerMap map =>
            ReadTomlServerMap(map, descriptor),
        ConfigSource.ExtensionBundles bundles =>
            ReadExtensionBundles(bundles, descriptor),
        ConfigSource.BundledPackages packages =>
            ReadBundledPackages(packages, descriptor),
        _ => new SourceOutcome(),
    };

    /// <summary>
    /// A file Kytto could not parse is one it will not write to, and saying so is
    /// more use than an empty column.
    /// </summary>
    private Diagnostic Unreadable(Exception error, PlatformPath file, ClientDescriptor descriptor) =>
        new(DiagnosticSeverity.Error,
            descriptor.Id,
            _resolver.DisplayServerMap(file, descriptor.Id),
            $"Could not read this config: {error.Message}. Kytto will not write to it.");

    private SourceOutcome ReadJsonServerMap(ConfigSource.ServerMap map, ClientDescriptor descriptor)
    {
        var outcome = new SourceOutcome();
        // Track a separate deny-list source even when the main server map has not
        // been created yet. A first arrival may need to edit both files, and both
        // digests must come from the same discovery snapshot (§6.4).
        var denied = DeniedNames(map.Enablement);
        var path = _resolver.ResolveServerMap(map.File, descriptor.Id);
        if (ReadTracked(path) is not { } source)
        {
            if (File.Exists(path))
            {
                outcome.SourceExists = true;
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    descriptor.Id,
                    _resolver.DisplayServerMap(map.File, descriptor.Id),
                    "Could not read this config: it is not valid UTF-8 or access was denied. " +
                    "Kytto will not write to it."));
            }
            return outcome;
        }
        outcome.SourceExists = true;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source);
        }
        catch (JsonParseException error)
        {
            outcome.Diagnostics.Add(Unreadable(error, map.File, descriptor));
            return outcome;
        }

        // The global map is writable. Project maps are inspected separately below
        // and become opaque read-only workspace columns (§4).
        var serverMaps = document.Root.Members?
            .Where(member => member.Key == map.ServersKey)
            .ToArray() ?? [];
        if (serverMaps.Length > 1)
        {
            outcome.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                descriptor.Id,
                _resolver.DisplayServerMap(map.File, descriptor.Id),
                $"Skipped servers: the top-level \"{map.ServersKey}\" key appears more than once, " +
                "so the effective configuration is ambiguous."));
            return outcome;
        }
        // Claude Code can have project-scoped servers even when its global map is
        // absent (a fresh CLI profile commonly starts that way). Project discovery
        // is independent and remains read-only; do it before the writable global
        // map's early return (§4).
        if (descriptor.Id == ClientId.ClaudeCode)
        {
            ReadClaudeCodeProjects(document, map, descriptor, outcome);
        }
        if (serverMaps.FirstOrDefault()?.Value.Members is not { } members) return outcome;

        var ambiguousNames = members
            .GroupBy(member => Server.Identity(member.Key), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var identity in ambiguousNames)
        {
            var spellings = members
                .Where(member => Server.Identity(member.Key) == identity)
                .Select(member => $"\"{member.Key}\"");
            outcome.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                descriptor.Id,
                _resolver.DisplayServerMap(map.File, descriptor.Id),
                $"Skipped ambiguous duplicate server names: {string.Join(", ", spellings)}."));
        }

        foreach (var member in members)
        {
            if (ambiguousNames.Contains(Server.Identity(member.Key))) continue;
            if (member.Value.Members is null)
            {
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    descriptor.Id,
                    map.File.DisplayString(),
                    $"Skipped \"{member.Key}\": expected an object."));
                continue;
            }

            var server = ServerFields.FromJson(member.Value).MakeServer(
                name: member.Key,
                origin: new Origin.ConfigFile(descriptor.Id),
                isBundled: false,
                sourceText: document.Slice(member.Value.Span));

            outcome.Servers.Add(new FoundServer(
                member.Key,
                server,
                denied.Contains(Server.Identity(member.Key)) ? Enablement.Disabled : Enablement.Enabled));
        }

        return outcome;
    }

    private void ReadClaudeCodeProjects(
        JsonDocument document,
        ConfigSource.ServerMap map,
        ClientDescriptor descriptor,
        SourceOutcome outcome)
    {
        var projects = document.Root.Members?
            .Where(member => member.Key == "projects")
            .ToArray() ?? [];
        if (projects.Length == 0) return;
        if (projects.Length > 1)
        {
            outcome.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                descriptor.Id,
                _resolver.DisplayServerMap(map.File, descriptor.Id),
                "Skipped Claude Code projects: the top-level \"projects\" key appears more than once."));
            return;
        }
        if (projects[0].Value.Members is not { } projectMembers)
        {
            outcome.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                descriptor.Id,
                _resolver.DisplayServerMap(map.File, descriptor.Id),
                "Skipped Claude Code projects: expected an object keyed by project path."));
            return;
        }

        var seen = new HashSet<ClientKey>();
        foreach (var project in projectMembers.OrderBy(member => member.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(project.Key) || project.Value.Members is not { } projectObject)
            {
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    descriptor.Id,
                    _resolver.DisplayServerMap(map.File, descriptor.Id),
                    $"Skipped Claude Code project \"{project.Key}\": expected an object."));
                continue;
            }

            var maps = projectObject.Where(member => member.Key == map.ServersKey).ToArray();
            if (maps.Length == 0) continue;
            var projectID = ProjectClientID(project.Key);
            if (maps.Length > 1 || maps[0].Value.Members is not { } projectServers)
            {
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    projectID,
                    _resolver.DisplayServerMap(map.File, descriptor.Id),
                    $"Skipped Claude Code project \"{project.Key}\": its \"{map.ServersKey}\" map is ambiguous."));
                continue;
            }

            // An empty mcpServers object is Claude Code bookkeeping, not a
            // configuration source. Long-lived profiles can contain dozens of
            // these entries, and none earns a sidebar row or matrix column (§4).
            if (projectServers.Count == 0) continue;

            if (!seen.Add(projectID))
            {
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    descriptor.Id,
                    _resolver.DisplayServerMap(map.File, descriptor.Id),
                    $"Skipped duplicate Claude Code project identity for \"{project.Key}\"."));
                continue;
            }

            var ambiguousNames = projectServers
                .GroupBy(member => Server.Identity(member.Key), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            var found = new List<FoundServer>();
            foreach (var member in projectServers)
            {
                if (ambiguousNames.Contains(Server.Identity(member.Key))) continue;
                if (member.Value.Members is null)
                {
                    outcome.Diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        projectID,
                        _resolver.DisplayServerMap(map.File, descriptor.Id),
                        $"Skipped \"{member.Key}\" in Claude Code project \"{project.Key}\": expected an object."));
                    continue;
                }
                var server = ServerFields.FromJson(member.Value).MakeServer(
                    member.Key,
                    new Origin.ConfigFile(projectID),
                    isBundled: false,
                    sourceText: document.Slice(member.Value.Span),
                    isReadOnly: true);
                found.Add(new FoundServer(member.Key, server, Enablement.Enabled));
            }

            outcome.Projects.Add(new ProjectSource(
                projectID,
                project.Key,
                "",
                "",
                found));
        }

        var compact = CompactProjectLeaves(outcome.Projects.Select(project => project.Path).ToArray());
        for (var index = 0; index < outcome.Projects.Count; index++)
        {
            var project = outcome.Projects[index];
            var shortName = compact[project.Path];
            outcome.Projects[index] = project with
            {
                ShortName = shortName,
                DisplayName = $"Claude Code · {shortName}",
            };
        }
    }

    /// <summary>Smallest distinguishing suffix for each discovered project path.</summary>
    internal static IReadOnlyDictionary<string, string> CompactProjectLeaves(
        IReadOnlyList<string> projectPaths)
    {
        var paths = projectPaths.Select(path => new
        {
            Original = path,
            Components = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries),
            Separator = path.LastIndexOf('\\') >= path.LastIndexOf('/') ? '\\' : '/',
        }).ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (path.Components.Length == 0)
            {
                result[path.Original] = path.Original;
                continue;
            }

            var depth = 1;
            while (depth < path.Components.Length && paths.Any(other =>
                       !ReferenceEquals(other, path) &&
                       string.Equals(
                           Suffix(path.Components, depth, path.Separator),
                           Suffix(other.Components, depth, other.Separator),
                           StringComparison.Ordinal)))
            {
                depth++;
            }

            // Consuming the whole path keeps roots, drive spelling and trailing
            // separators byte-for-byte instead of rebuilding them (§4).
            result[path.Original] = depth >= path.Components.Length
                ? path.Original
                : $"…{path.Separator}{Suffix(path.Components, depth, path.Separator)}";
        }
        return result;

        static string Suffix(string[] components, int depth, char separator)
        {
            var kept = Math.Min(depth, components.Length);
            return string.Join(separator, components[^kept..]);
        }
    }

    /// <summary>
    /// Codex. One table per server, its environment in a sub-table, and an
    /// <c>enabled</c> boolean on the server's own table rather than in a file
    /// somewhere else — the only client of the five whose off switch is local.
    /// </summary>
    private SourceOutcome ReadTomlServerMap(ConfigSource.ServerMap map, ClientDescriptor descriptor)
    {
        var outcome = new SourceOutcome();
        var path = _resolver.ResolveServerMap(map.File, descriptor.Id);
        if (ReadTracked(path) is not { } source)
        {
            if (File.Exists(path))
            {
                outcome.SourceExists = true;
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Error,
                    descriptor.Id,
                    _resolver.DisplayServerMap(map.File, descriptor.Id),
                    "Could not read this config: it is not valid UTF-8 or access was denied. " +
                    "Kytto will not write to it."));
            }
            return outcome;
        }
        outcome.SourceExists = true;

        TomlDocument document;
        try
        {
            document = TomlDocument.Parse(source);
        }
        catch (TomlParseException error)
        {
            outcome.Diagnostics.Add(Unreadable(error, map.File, descriptor));
            return outcome;
        }

        var flagKey = (map.Enablement as EnablementStrategy.InlineFlag)?.Key;

        var names = document.ServerNames(map.ServersKey);
        var ambiguousNames = names
            .GroupBy(Server.Identity, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var identity in ambiguousNames)
        {
            var spellings = names
                .Where(name => Server.Identity(name) == identity)
                .Select(name => $"\"{name}\"");
            outcome.Diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                descriptor.Id,
                _resolver.DisplayServerMap(map.File, descriptor.Id),
                $"Skipped ambiguous duplicate server names: {string.Join(", ", spellings)}."));
        }

        foreach (var name in names)
        {
            if (ambiguousNames.Contains(Server.Identity(name))) continue;
            var table = document.Table(map.ServersKey, name);
            if (table is null)
            {
                // An inline `name = { … }` under `[mcp_servers]`. Kytto reads it so
                // the row is not missing, but says plainly that it will not rewrite
                // a line it did not write.
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    descriptor.Id,
                    map.File.DisplayString(),
                    $"\"{name}\" is written as an inline table. Kytto shows it but will not " +
                    "edit it — rewriting that line safely means regenerating it."));

                var pair = document.Table(map.ServersKey)?.Pair(name);
                if (pair?.Value.InlinePairs is not { } inline) continue;
                var inlineServer = ServerFields.FromTomlInline(inline).MakeServer(
                    name: name,
                    origin: new Origin.ConfigFile(descriptor.Id),
                    isBundled: false,
                    sourceText: document.Slice(pair.Span));
                var inlineEnabled = flagKey is null ||
                    inline.FirstOrDefault(candidate => candidate.Name == flagKey)?
                        .Value.BoolValue != false;
                outcome.Servers.Add(new FoundServer(
                    name,
                    inlineServer,
                    inlineEnabled ? Enablement.Enabled : Enablement.Disabled));
                continue;
            }

            var envTable = document.Table(map.ServersKey, name, "env");
            var server = ServerFields.FromToml(table, envTable).MakeServer(
                name: name,
                origin: new Origin.ConfigFile(descriptor.Id),
                isBundled: false,
                sourceText: document.ServerDefinitionText(name, map.ServersKey) ?? "");

            // Absent means on. That is Codex's rule, not a guess: `codex mcp list`
            // reports a server with no `enabled` key as enabled.
            var isEnabled = flagKey is null || table.Value(flagKey)?.BoolValue != false;
            outcome.Servers.Add(new FoundServer(
                name, server, isEnabled ? Enablement.Enabled : Enablement.Disabled));
        }

        return outcome;
    }

    /// <summary>
    /// Servers that arrive inside an installed package rather than being configured
    /// by anyone — Codex plugins.
    /// </summary>
    /// <remarks>
    /// Read-only on purpose. The tree is a cache the client repopulates, so a write
    /// here would be undone without warning, and the row is marked bundled so the
    /// matrix refuses to copy it into another client for the same reason Claude
    /// Desktop extensions are refused.
    /// </remarks>
    private SourceOutcome ReadBundledPackages(
        ConfigSource.BundledPackages packages,
        ClientDescriptor descriptor)
    {
        var outcome = new SourceOutcome();
        var directory = _resolver.Resolve(packages.Root);
        if (!Directory.Exists(directory)) return outcome;
        outcome.SourceExists = true;

        foreach (var manifestPath in BundledManifestPaths(directory, packages.ManifestName))
        {
            if (ReadTracked(manifestPath) is not { } text) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (document.Root[packages.ServersKey]?.Members is not { } members) continue;

            foreach (var member in members)
            {
                if (member.Value.Members is null) continue;
                var server = ServerFields.FromJson(member.Value).MakeServer(
                    name: member.Key,
                    origin: new Origin.ConfigFile(descriptor.Id),
                    isBundled: true,
                    sourceText: document.Slice(member.Value.Span));
                outcome.Servers.Add(new FoundServer(member.Key, server, Enablement.Enabled));
            }
        }

        return outcome;
    }

    /// <summary>
    /// Finds package manifests without walking through the payload of a package
    /// after its manifest has been found.
    /// </summary>
    /// <remarks>
    /// Codex plugin caches can contain entire runtimes and <c>node_modules</c>
    /// trees; inspecting all of those files made every otherwise-small discovery
    /// pass proportional to the cache's byte size. Hidden entries are not skipped:
    /// the manifest this looks for is called <c>.mcp.json</c>, so skipping them
    /// would skip the whole point.
    /// </remarks>
    internal static IReadOnlyList<string> BundledManifestPaths(string root, string manifestName)
    {
        var pending = new Stack<string>([root]);
        var manifests = new List<string>();

        while (pending.TryPop(out var directory))
        {
            var manifest = Path.Combine(directory, manifestName);
            if (File.Exists(manifest))
            {
                manifests.Add(manifest);
                // A manifest marks the root of one installed package. Anything below
                // it is that package's implementation, not another package Kytto
                // should recursively inspect.
                continue;
            }

            try
            {
                foreach (var entry in Directory.EnumerateDirectories(directory))
                {
                    // A reparse point can point back up its own tree, and following
                    // one turns this walk into a loop.
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    pending.Push(entry);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory is not evidence of anything; the rest of
                // the cache is still worth reading.
            }
        }

        // Sorted, so the matrix does not reorder itself between launches on the
        // strength of directory enumeration order.
        manifests.Sort(StringComparer.Ordinal);
        return manifests;
    }

    /// <summary>Names switched off in a separate settings file, normalized for comparison.</summary>
    private HashSet<string> DeniedNames(EnablementStrategy enablement)
    {
        var empty = new HashSet<string>(StringComparer.Ordinal);
        if (enablement is not EnablementStrategy.DenyList deny) return empty;

        var path = _resolver.Resolve(deny.File);
        if (ReadTracked(path) is not { } source) return empty;

        try
        {
            var document = JsonDocument.Parse(source);
            if (document.Root[deny.Key]?.Elements is not { } list) return empty;

            foreach (var entry in list)
            {
                // Tolerate both `[{serverName: "x"}]` and a plain `["x"]`.
                var name = entry[deny.NameField]?.StringValue ?? entry.StringValue;
                if (name is not null) empty.Add(Server.Identity(name));
            }
        }
        catch (JsonParseException)
        {
            // A settings file Kytto cannot read is not evidence anything is denied.
        }

        return empty;
    }

    private SourceOutcome ReadExtensionBundles(
        ConfigSource.ExtensionBundles bundles,
        ClientDescriptor descriptor)
    {
        var outcome = new SourceOutcome();
        var root = _resolver.Resolve(bundles.Directory);
        var settingsRoot = _resolver.Resolve(bundles.SettingsDirectory);

        string[] entries;
        try
        {
            entries = Directory
                .EnumerateDirectories(root)
                .Where(path => !Path.GetFileName(path).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return outcome;
        }
        outcome.SourceExists = true;

        foreach (var entry in entries)
        {
            var manifestPath = Path.Combine(entry, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            if (ReadTracked(manifestPath) is not { } manifestText) continue;

            JsonDocument manifest;
            try
            {
                manifest = JsonDocument.Parse(manifestText);
            }
            catch (JsonParseException error)
            {
                outcome.Diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    descriptor.Id,
                    bundles.Directory.DisplayString() + Path.DirectorySeparatorChar + Path.GetFileName(entry),
                    $"Could not read this extension's manifest: {error.Message}"));
                continue;
            }

            var bundleId = Path.GetFileName(entry);
            var name = manifest.Root["display_name"]?.StringValue
                ?? manifest.Root["name"]?.StringValue
                ?? bundleId;

            // Extensions describe their process under `server.mcp_config`.
            if (manifest.Root.ValueAt("server", "mcp_config") is not { } config) continue;

            var server = ServerFields.FromJson(config).MakeServer(
                name: name,
                origin: new Origin.ClaudeDesktopExtension(bundleId),
                isBundled: true,
                sourceText: manifest.Slice(config.Span));

            // Absent settings file means the extension has never been toggled, which
            // Claude Desktop treats as on.
            var settingsPath = Path.Combine(settingsRoot, $"{bundleId}.json");
            JsonDocument? settings = null;
            if (ReadTracked(settingsPath) is { } settingsText)
            {
                try
                {
                    settings = JsonDocument.Parse(settingsText);
                }
                catch (JsonParseException)
                {
                    // Treated as never toggled, which is what an absent file means.
                }
            }
            var isEnabled = settings?.Root[bundles.FlagKey]?.BoolValue != false;

            // What Claude Desktop would substitute before running this. The trailing
            // separator goes: manifests write `${__dirname}/server/index.js`, and
            // keeping it would produce a doubled separator.
            server.Placeholders["__dirname"] =
                entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var member in settings?.Root["userConfig"]?.Members ?? [])
            {
                var value = member.Value.StringValue
                    ?? (member.Value.Elements is { } elements
                        ? string.Join(' ', elements.Select(e => e.StringValue).OfType<string>())
                        : null);
                if (value is not null) server.Placeholders[$"user_config.{member.Key}"] = value;
            }

            outcome.Servers.Add(new FoundServer(
                name, server, isEnabled ? Enablement.Enabled : Enablement.Disabled));
        }

        return outcome;
    }
}
