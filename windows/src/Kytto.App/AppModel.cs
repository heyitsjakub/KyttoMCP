using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Doctor;
using Kytto.Core.Gateway;
using Kytto.Core.Health;
using Kytto.Core.Imports;
using Kytto.Core.Library;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using Kytto.Core.Skills;
using Kytto.Core.Updates;

// The port's own naming: `Discovery`, `Catalog` and the write pipeline all live in
// `Kytto.Core` rather than in a namespace of their own, because C# cannot tell a
// type called `Discovery` from a namespace called `Discovery`.

namespace Kytto.App;

/// <summary>
/// What the shell knows about the machine, and the only thing the command
/// registry talks to.
/// </summary>
/// <remarks>
/// Discovery is re-run rather than mutated: config files are authoritative for
/// what is configured (§5), so the way to learn what changed is to read them
/// again. Nothing here writes on launch (§6.6).
/// </remarks>
internal sealed class AppModel : IDisposable
{
    private readonly KyttoPaths _paths;
    private readonly string _home;
    private readonly IAppLocator _locator;
    private readonly bool _watchConfigs;
    private readonly SettingsStore _settingsStore;
    private readonly ILoginItem _loginItem;
    private readonly DigestLedger _ledger = new();
    private readonly ParkStore _parkStore;
    private readonly MetadataStore _metadata;
    private readonly ProfileStore _profiles;
    private readonly ISecretStore _secretStore;
    private readonly GatewayRouteStore _gatewayRoutes;
    private readonly GatewayActivityStore _gatewayActivity;
    private readonly ISecretStore _gatewaySecretStore;
    private readonly string _gatewayHelperPath;
    private readonly UpdateChecker _updates;
    private readonly UpdatePackageManager _updatePackages;
    private readonly ProvenanceChecker _provenanceChecker;
    private readonly McpLibraryScanner _libraryScanner = new();
    private readonly Dictionary<string, LibrarySession> _librarySessions = [];
    private readonly SkillsInventory _skillsInventory = new();
    private readonly Dictionary<string, string> _skillWorkspaceRoots = [];
    private readonly Dictionary<string, (ServerProvenance Provenance, DateTimeOffset CheckedAt)> _provenanceCache = [];
    private readonly IUpdateDownloadLauncher _updateDownloadLauncher;
    private readonly IAuthorizationPageLauncher _authorizationPageLauncher;
    private readonly Dictionary<ClientId, Dictionary<string, string>> _restartBaselines = [];
    private readonly Dictionary<ClientId, Dictionary<string, string>> _restartObserved = [];
    private readonly HashSet<ClientId> _dismissedRestarts = [];
    private DiscoveryResult _current;
    private ConfigWatcher? _watcher;

    internal static AppModel ForCurrentProcess()
    {
#if DEBUG
        // Native UI tests can launch the real shell without letting it see the
        // developer's profile. These names do not exist in Release builds (§6).
        var testHome = Environment.GetEnvironmentVariable("KYTTO_TEST_HOME");
        var testData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
        if (testHome is not null || testData is not null)
        {
            if (string.IsNullOrWhiteSpace(testHome) || string.IsNullOrWhiteSpace(testData)
                || !Path.IsPathFullyQualified(testHome) || !Path.IsPathFullyQualified(testData))
            {
                throw new InvalidOperationException(
                    "KYTTO_TEST_HOME and KYTTO_TEST_DATA_ROOT must both be complete paths.");
            }
            return new AppModel(
                new KyttoPaths(testData),
                testHome,
                secretStore: new InMemorySecretStore());
        }
#endif
        return new AppModel();
    }

    internal AppModel(
        KyttoPaths? paths = null,
        string? home = null,
        IAppLocator? locator = null,
        ISecretStore? secretStore = null,
        ISecretStore? gatewaySecretStore = null,
        string? gatewayHelperPath = null,
        bool watchConfigs = true,
        UpdateChecker? updateChecker = null,
        IUpdateDownloadLauncher? updateDownloadLauncher = null,
        ProvenanceChecker? provenanceChecker = null,
        UpdatePackageManager? updatePackageManager = null,
        IAuthorizationPageLauncher? authorizationPageLauncher = null,
        SettingsStore? settingsStore = null,
        ILoginItem? loginItem = null)
    {
        _paths = paths ?? new KyttoPaths();
        _home = home ?? KyttoPaths.Home;
        _locator = locator ?? new WindowsAppLocator();
        _secretStore = secretStore ?? new WindowsCredentialStore();
        _gatewaySecretStore = gatewaySecretStore ?? new WindowsCredentialStore("Kytto.Gateway");
        _gatewayHelperPath = gatewayHelperPath
#if DEBUG
            ?? Environment.GetEnvironmentVariable("KYTTO_GATEWAY_HELPER")
#endif
            ?? Path.Combine(AppContext.BaseDirectory, "kytto-mcp-proxy.exe");
        _watchConfigs = watchConfigs;
        _settingsStore = settingsStore ?? new SettingsStore(_paths);
        _loginItem = loginItem ?? new WindowsLoginItem();
        _parkStore = new ParkStore(_paths);
        _metadata = new MetadataStore(_paths);
        _profiles = new ProfileStore(_paths);
        _gatewayRoutes = new GatewayRouteStore(_paths);
        _gatewayActivity = new GatewayActivityStore(_paths);
        _updates = updateChecker ?? new UpdateChecker(_settingsStore, CurrentAppVersion());
        _updatePackages = updatePackageManager ?? new UpdatePackageManager(_paths);
        _provenanceChecker = provenanceChecker ?? new ProvenanceChecker();
        _updateDownloadLauncher = updateDownloadLauncher ?? new WindowsUpdateDownloadLauncher();
        _authorizationPageLauncher = authorizationPageLauncher ?? new WindowsAuthorizationPageLauncher();
        Backups = new BackupStore(_paths, Settings.BackupRetention);
        _current = Discover();
        foreach (var client in ClientIds.All)
        {
            var snapshot = RestartSnapshot(client);
            _restartBaselines[client] = snapshot;
            _restartObserved[client] = new Dictionary<string, string>(snapshot, StringComparer.Ordinal);
        }
    }

    internal BackupStore Backups { get; }

    internal KyttoSettings Settings => _settingsStore.Current;

    internal bool LaunchAtLoginEnabled => _loginItem.IsEnabled();

    internal static string CurrentAppVersion()
    {
        var version = typeof(AppModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    private VerifiedUpdate? _verifiedUpdate;

    internal async Task<UpdateCheckResult> CheckUpdatesAsync(bool force)
    {
        var result = await _updates.CheckAsync(force).ConfigureAwait(true);
        if (result.Release is { } release) _verifiedUpdate = release;
        return result;
    }

    internal async Task<UpdatePackage> DownloadUpdateAsync(Action<UpdateProgress> onProgress)
    {
        // Download is a fresh user action, so do not trust an old manifest cached
        // in settings or a banner that survived a long-running session (§8).
        var check = await _updates.CheckAsync(force: true).ConfigureAwait(true);
        var release = check.Status == UpdateStatus.UpdateAvailable
            ? check.Release
            : null;
        if (release is null)
        {
            throw new UpdatePackageException("There is no verified newer Kytto update to download.");
        }
        _verifiedUpdate = release;
        return await _updatePackages.DownloadAsync(
            release,
            new Progress<UpdateProgress>(onProgress)).ConfigureAwait(true);
    }

    internal void CancelUpdateDownload() => _updatePackages.Cancel();

    internal Task InstallUpdateAsync(string token, Action<UpdateProgress> onProgress) =>
        _updatePackages.InstallAsync(
            token,
            CurrentAppVersion(),
            progress: new Progress<UpdateProgress>(onProgress));

    internal LibraryDirectorySelection? ChooseLibraryDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a folder containing MCP servers to inspect",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dialog.SelectedPath)) return null;

        var sessionId = $"library-session.{Guid.NewGuid():D}";
        var root = Path.GetFullPath(dialog.SelectedPath);
        _librarySessions[sessionId] = new LibrarySession(root, null);
        return new LibraryDirectorySelection(sessionId, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    internal McpLibraryScanResult ScanLibrary(string sessionId)
    {
        var session = LibrarySessionFor(sessionId);
        var result = _libraryScanner.Scan(session.Root, _current.Servers);
        _librarySessions[sessionId] = session with { Candidates = result.Candidates };
        return result;
    }

    internal McpLibraryCandidate LibraryCandidate(string sessionId, string candidateId) =>
        LibrarySessionFor(sessionId).Candidates?.FirstOrDefault(candidate => candidate.ID == candidateId)
        ?? throw new InvalidOperationException("That library candidate is no longer available. Scan the selected folder again.");

    internal LibraryImportPreview PreviewLibrary(
        string sessionId,
        string candidateId,
        IReadOnlyList<ClientId> clients)
    {
        var candidate = LibraryCandidate(sessionId, candidateId);
        ValidateWritableClients(clients);
        var conflicts = clients
            .Where(client => _current.Servers.Any(server => server.Id == candidate.NativeServer?.Id &&
                server.EnabledIn.GetValueOrDefault(client) != Enablement.Absent))
            .Select(client => client.Raw())
            .ToArray();
        var warnings = candidate.Warnings.ToList();
        if (candidate.EnvironmentKeys.Count > 0)
        {
            warnings.Add("Environment values stay on the native side and are not shown in preview.");
        }
        if (candidate.NativeServer is null)
        {
            warnings.Add("This candidate has incomplete metadata and cannot be imported.");
        }
        return new LibraryImportPreview(candidate, clients, conflicts, warnings);
    }

    internal AuthoringResult ImportLibrary(
        string sessionId,
        string candidateId,
        IReadOnlyList<ClientId> clients)
    {
        var preview = PreviewLibrary(sessionId, candidateId, clients);
        var candidate = preview.Candidate;
        if (candidate.NativeServer is null)
        {
            throw new AuthoringException("This library candidate has no complete server definition to import.");
        }
        return CreateServer(candidate.ToDraft(), clients);
    }

    internal string AgentImportPrompt() => AgentImportParser.Prompt;

    internal AgentImportPreview PreviewAgentImport(
        string json,
        IReadOnlyList<ClientId> clients)
    {
        ValidateWritableClients(clients);
        var document = AgentImportParser.Parse(json);
        var conflicts = document.Servers
            .Where(server => clients.Any(client => _current.Servers.Any(existing =>
                existing.Id == Server.Identity(server.Name) &&
                existing.EnabledIn.GetValueOrDefault(client) != Enablement.Absent)))
            .Select(server => server.Name)
            .ToArray();
        var warnings = document.Servers
            .Where(server => server.EnvironmentKeys.Count > 0)
            .Select(server => $"{server.Name}: environment key names were received, but no values will be invented or written.")
            .ToArray();
        return new AgentImportPreview(
            document.Servers,
            clients,
            conflicts,
            warnings);
    }

    internal AgentImportResult ImportAgent(
        string json,
        IReadOnlyList<ClientId> clients)
    {
        var preview = PreviewAgentImport(json, clients);
        var changed = new HashSet<ClientId>();
        foreach (var server in preview.Servers)
        {
            var result = CreateServer(server.ToDraft(), clients);
            foreach (var client in result.Changed) changed.Add(client);
        }
        return new AgentImportResult(
            preview.Servers.Select(server => server.Name).ToArray(),
            changed.OrderBy(client => client.Raw(), StringComparer.Ordinal).ToArray(),
            preview.Warnings,
            _current);
    }

    internal SkillsDirectorySelection? ChooseSkillsDirectory()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a workspace folder whose known skills roots should be inspected",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK ||
            string.IsNullOrWhiteSpace(dialog.SelectedPath)) return null;

        var sessionId = $"skills-session.{Guid.NewGuid():D}";
        var root = Path.GetFullPath(dialog.SelectedPath);
        _skillWorkspaceRoots[sessionId] = root;
        return new SkillsDirectorySelection(sessionId, Path.GetFileName(root.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    internal SkillInventoryResult InventorySkills(string? sessionId)
    {
        string? workspace = null;
        if (sessionId is not null)
        {
            workspace = _skillWorkspaceRoots.GetValueOrDefault(sessionId)
                ?? throw new InvalidOperationException("That skills folder selection has expired.");
        }
        return _skillsInventory.Scan(_home, workspace);
    }

    private void ValidateWritableClients(IReadOnlyList<ClientId> clients)
    {
        if (clients.Count == 0) throw new AuthoringException("Choose at least one built-in client.");
        if (clients.Distinct().Count() != clients.Count)
        {
            throw new AuthoringException("Choose each built-in client only once.");
        }
        foreach (var client in clients)
        {
            if (!ClientIds.All.Contains(client)) throw new AuthoringException("Only built-in clients can be import targets.");
        }
    }

    private LibrarySession LibrarySessionFor(string sessionId) =>
        _librarySessions.GetValueOrDefault(sessionId)
        ?? throw new InvalidOperationException("That library selection has expired. Choose a folder again.");

    internal async Task<ServerProvenance> CheckLatestProvenanceAsync(string serverId)
    {
        var server = ServerById(serverId);
        var package = server.Provenance.PackageName
            ?? throw new ProvenanceCheckException(
                $"Kytto could not identify a package name for \"{server.Name}\".");
        var now = Timestamp.Now;
        var cacheKey = $"{server.Provenance.SourceKind}:{package}";
        if (!_provenanceCache.TryGetValue(cacheKey, out var cached) ||
            now - cached.CheckedAt >= TimeSpan.FromHours(24))
        {
            var checkedProvenance = await _provenanceChecker.CheckLatestAsync(
                server.Provenance,
                now).ConfigureAwait(false);
            cached = (checkedProvenance, now);
            _provenanceCache[cacheKey] = cached;
        }

        _metadata.Update(server.Id, existing => existing with { Provenance = cached.Provenance });
        Reload();
        return _current.Servers.First(candidate => candidate.Id == server.Id).Provenance;
    }

    internal void OpenUpdateDownload() => _updateDownloadLauncher.Open();

    internal IReadOnlyList<Profile> Profiles() => _profiles.All();

    internal IReadOnlyList<Profile> CreateProfile(
        string name,
        IReadOnlyList<string> serverIds,
        int? tokenBudget = null) =>
        _profiles.Create(name, serverIds, tokenBudget);

    internal IReadOnlyList<Profile> UpdateProfile(
        string profileId,
        string name,
        IReadOnlyList<string> serverIds,
        int? tokenBudget = null) =>
        _profiles.Update(profileId, name, serverIds, tokenBudget);

    internal IReadOnlyList<Profile> DeleteProfile(string profileId) =>
        _profiles.Delete(profileId);

    // MARK: - Gateway (§3.3)

    internal IReadOnlyList<GatewayRoute> GatewayRoutes() => _gatewayRoutes.All();

    internal GatewayActivitySummary Activity(int limit = 500) =>
        _gatewayActivity.Summary(limit);

    // MARK: - MCP Doctor and Contract Guard (§7.10)

    internal IReadOnlyList<DoctorServerReport> DoctorReports() => McpDoctor.Reports(
        _current.Servers,
        _current.Clients
            .Select(client => client.Id.BuiltIn)
            .OfType<ClientId>()
            .ToArray());

    internal IReadOnlyList<ContractAlert> ContractAlerts()
    {
        var entries = _metadata.All();
        return _current.Servers
            .Select(server => (server, metadata: entries.GetValueOrDefault(server.Id)))
            .Where(item => item.metadata?.ContractChanges is { Count: > 0 } &&
                           item.metadata.ContractChangedAt is not null)
            .Select(item => new ContractAlert(
                item.server.Id,
                item.server.Name,
                item.metadata!.ContractChangedAt!.Value,
                item.metadata.ContractChanges!))
            .ToArray();
    }

    /// <summary>Records that a Contract Guard alert has been read (§7.10).</summary>
    internal void AcknowledgeContract(string serverId)
    {
        // Asking about a server that is not there is a mistake worth reporting,
        // rather than a no-op that looks like it worked.
        _ = ServerById(serverId);
        _metadata.AcknowledgeContract(serverId);
        Reload();
    }

    internal DoctorFixPreview DoctorFixPreview(string serverId)
    {
        RequireDirect(serverId);
        var server = ServerById(serverId);
        var health = server.Health;
        var currentCommand = server.Command;
        var replacement = health?.ResolvedCommand;
        if (server.Transport != Transport.Stdio ||
            health?.Status != HealthStatus.Passed ||
            string.IsNullOrWhiteSpace(currentCommand) ||
            string.IsNullOrWhiteSpace(replacement) ||
            string.Equals(currentCommand, replacement, StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(replacement) ||
            !McpDoctor.MatchesExecutable(currentCommand, replacement) ||
            !File.Exists(replacement))
        {
            throw new InvalidOperationException(
                $"“{server.Name}” does not have a current successful check with a verified absolute executable path.");
        }

        var clients = server.EnabledIn
            .Where(pair => pair.Value != Enablement.Absent)
            .Select(pair => pair.Key.BuiltIn)
            .OfType<ClientId>()
            .OrderBy(client => client.Raw(), StringComparer.Ordinal)
            .ToArray();
        return new DoctorFixPreview(
            server.Id,
            server.Name,
            currentCommand,
            replacement,
            clients);
    }

    internal AuthoringResult ApplyDoctorFix(string serverId)
    {
        var preview = DoctorFixPreview(serverId);
        var server = ServerById(serverId);
        var draft = ServerDraft.Editing(server) with { Command = preview.ReplacementCommand };
        return UpdateServer(draft, serverId);
    }

    internal GatewayMigrationPreview GatewayPreview(string serverId, ClientId clientId) =>
        GatewayMigration().Preview(ServerById(serverId), clientId);

    internal GatewayMigrationResult EnableGateway(
        string serverId,
        ClientId clientId,
        string routeId)
    {
        var result = GatewayMigration().Enable(ServerById(serverId), clientId, routeId);
        Reload();
        return result;
    }

    internal GatewayMigrationResult RestoreDirect(string routeId)
    {
        var result = GatewayMigration().Restore(routeId);
        Reload();
        return result;
    }

    /// <summary>Narrows or widens what one route exposes (§7.11).</summary>
    /// <remarks>
    /// No config file is touched: the client already points at this route id, and
    /// the helper reads the allow list when it starts. That still means the client
    /// has to be restarted before the change is visible to the model, so this counts
    /// as a pending restart like any other change.
    /// </remarks>
    internal GatewayRoute SetExposedTools(string routeId, IReadOnlyList<string>? toolNames)
    {
        var route = _gatewayRoutes.Update(routeId, toolNames);
        RefreshRestartState();
        return route;
    }

    private void RequireDirect(string serverId, ClientId? clientId = null)
    {
        var normalized = Server.Identity(serverId);
        if (_gatewayRoutes.All().Any(route =>
            route.ServerID == normalized && (clientId is null || route.ClientID == clientId)))
        {
            throw new GatewayManagedException();
        }
    }

    /// <summary>Changes a client has not picked up yet (§7.1).</summary>
    /// <remarks>
    /// Clients read their configuration at launch and only at launch, so this is a
    /// count of writes since the user last acknowledged them — not something that
    /// can be inferred from the file.
    /// </remarks>
    internal Dictionary<ClientId, int> PendingRestarts { get; } = [];

    /// <summary>Restart notices hidden without claiming that a client restarted.</summary>
    internal IReadOnlyCollection<ClientId> DismissedRestarts => _dismissedRestarts;

    /// <summary>
    /// Raised when a watched file changed on disk, whoever changed it — including
    /// Kytto itself. Re-reading is cheap and the render is idempotent, so the UI
    /// settles on the truth rather than working out which events were its own.
    /// </summary>
    internal event Action? ConfigsChanged;

    /// <summary>Raised only after the new settings were persisted successfully.</summary>
    internal event Action<KyttoSettings>? SettingsChanged;

    internal DiscoveryResult Current() => _current;

    internal DiscoveryResult Reload()
    {
        // Watch targets can appear after launch (a client creates its config
        // directory), disappear, or move through Settings. Rebuild them whenever
        // discovery is explicitly refreshed instead of pinning the launch-time
        // directory set for the lifetime of the app.
        if (_watchConfigs)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
        _current = Discover();
        RefreshRestartState();
        return _current;
    }

    internal KyttoSettings UpdateSettings(Func<KyttoSettings, KyttoSettings> change)
    {
        var previous = Settings;
        bool? previousLoginState = null;
        var updated = _settingsStore.Update(
            change,
            beforePersist: (stored, candidate) =>
            {
                if (candidate.LaunchAtLogin == stored.LaunchAtLogin) return;
                previousLoginState = _loginItem.IsEnabled();
                _loginItem.SetEnabled(candidate.LaunchAtLogin);
            },
            rollBackBeforePersist: (stored, candidate) =>
            {
                if (candidate.LaunchAtLogin == stored.LaunchAtLogin || previousLoginState is null) return;
                _loginItem.SetEnabled(previousLoginState.Value);
            });
        // Custom sources and path overrides both change discovery's read/watch
        // targets. Rebuild them only after the settings write succeeded.
        Reload();
        SettingsChanged?.Invoke(updated);
        return updated;
    }

    internal void AcknowledgeRestart(ClientId? client)
    {
        var clients = client is { } selected ? [selected] : ClientIds.All;
        foreach (var target in clients)
        {
            _restartBaselines[target] = RestartSnapshot(target);
            _dismissedRestarts.Remove(target);
        }
        RefreshRestartState();
    }

    internal void DismissRestart(ClientId? client)
    {
        var clients = client is { } selected ? [selected] : PendingRestarts.Keys.ToArray();
        foreach (var target in clients)
        {
            if (PendingRestarts.GetValueOrDefault(target) > 0) _dismissedRestarts.Add(target);
        }
    }

    internal Server ServerById(string serverId) =>
        _current.Servers.FirstOrDefault(server => server.Id == serverId)
        ?? throw new InvalidOperationException(
            $"No server called \"{serverId}\" is configured in any client.");

    /// <summary>The one command that writes to a client's configuration.</summary>
    internal ToggleResult SetEnabled(bool enabled, string serverId, ClientId clientId)
    {
        RequireDirect(serverId, clientId);
        var service = new ToggleService(
            home: _home,
            writer: new ConfigWriter(Backups),
            parkStore: _parkStore,
            ledger: _ledger,
            pathOverrides: Settings.ClientPathOverrides);

        var result = service.SetEnabled(enabled, ServerById(serverId), clientId);
        Reload();
        return result;
    }

    // MARK: - Authoring (§7.2, §7.5)

    internal AuthoringResult CreateServer(ServerDraft draft, IReadOnlyList<ClientId> clients) =>
        Record(Authoring().Create(draft, clients, _current.Servers));

    /// <summary>Applies a draft everywhere the server currently lives.</summary>
    /// <remarks>
    /// The environment values arrive with nulls where the form was never given a
    /// secret, and they are filled in from the existing server at the last moment
    /// (§6) — here, natively, rather than anywhere the web layer can see.
    /// </remarks>
    internal AuthoringResult UpdateServer(ServerDraft draft, string serverId)
    {
        RequireDirect(serverId);
        var server = ServerById(serverId);
        var result = Record(Authoring().Update(
            draft.MergingSecrets(server), server.Name, server, _current.Servers));
        try
        {
            _profiles.RenameServer(server.Id, result.ServerName);
        }
        catch (Exception error) when (
            error is ProfileException or IOException or UnauthorizedAccessException)
        {
            // The config rename is already committed. Optional profile maintenance
            // must not turn that successful mutation into a false failure.
            Log.Failure("profile rename maintenance", error);
        }
        return result;
    }

    internal AuthoringResult DeleteServer(string serverId)
    {
        RequireDirect(serverId);
        return Record(Authoring().Delete(ServerById(serverId)));
    }

    // MARK: - Unify (§7.12)

    /// <summary>What every client would look like if one client's copy won.</summary>
    /// <remarks>
    /// Read-only. Environment <strong>keys</strong> are named because a key one
    /// client is missing is half of what drift means; no value is read here and none
    /// is returned (§6).
    /// </remarks>
    internal UnifyPreview UnifyPreviewFor(string serverId, ClientId sourceClientId)
    {
        var server = ServerById(serverId);
        var drift = ServerDrift.Detect(server) ?? throw new NoDriftException(server.Name);
        var source = SourceDefinition(server, sourceClientId);

        var targets = UnifyTargets(server, sourceClientId);
        var command = string.Join(
            ' ',
            (source.Command is null ? [] : new[] { source.Command }).Concat(source.Args));

        return new UnifyPreview(
            ServerID: server.Id,
            ServerName: server.Name,
            SourceClientID: sourceClientId,
            TargetClientIDs: targets,
            SkippedClientIDs: drift.UnwritableClientIDs
                .Select(client => client.BuiltIn)
                .OfType<ClientId>()
                .ToArray(),
            CommandSummary: command,
            Url: source.Url,
            Transport: source.Transport,
            EnvironmentKeys: source.EnvironmentKeys);
    }

    /// <summary>Rewrites every drifted copy of a server to match one client's.</summary>
    /// <remarks>
    /// The definition is assembled here, natively, from the source client's own
    /// entry — including its environment <strong>values</strong>, which is the reason
    /// this is one command rather than the web layer reading a definition and sending
    /// it back. A token would have had to cross the boundary twice to do that, and it
    /// never crosses once (§6).
    /// </remarks>
    internal AuthoringResult UnifyServer(string serverId, ClientId sourceClientId)
    {
        RequireDirect(serverId);
        var server = ServerById(serverId);
        var source = SourceDefinition(server, sourceClientId);

        var targets = UnifyTargets(server, sourceClientId);
        if (targets.Count == 0) throw new NoDriftException(server.Name);

        var draft = new ServerDraft
        {
            Name = server.Name,
            Transport = source.Transport,
            Command = source.Command ?? "",
            Args = source.Args,
            Env = source.Env,
            Url = source.Url ?? "",
        };

        return Record(Authoring().Unify(draft, server, targets));
    }

    private static ClientDefinition SourceDefinition(Server server, ClientId sourceClientId)
    {
        var source = server.DefinitionsByClient.GetValueOrDefault(sourceClientId)
            ?? throw new InvalidOperationException(
                $"\"{server.Name}\" is not configured in {sourceClientId.Raw()}.");
        if (source.IsBundled) throw AuthoringException.NotEditable(server.Name);
        return source;
    }

    /// <summary>
    /// Every client that holds a copy, minus the ones already matching the source and
    /// the ones that are installed software rather than configuration.
    /// </summary>
    private static IReadOnlyList<ClientId> UnifyTargets(Server server, ClientId sourceClientId)
    {
        if (server.DefinitionsByClient.GetValueOrDefault(sourceClientId) is not { } source)
        {
            return [];
        }
        return server.DefinitionsByClient
            .Where(pair => pair.Key.BuiltIn is { } clientId
                && clientId != sourceClientId
                && !pair.Value.IsBundled
                && pair.Value != source)
            .Select(pair => pair.Key.BuiltIn!.Value)
            .Order(ClientIdComparer.Instance)
            .ToArray();
    }

    internal AuthoringResult RemoveServerFrom(string serverId, ClientId clientId)
    {
        RequireDirect(serverId, clientId);
        return Record(Authoring().RemoveFrom(ServerById(serverId), clientId));
    }

    // MARK: - Profiles / MCP stacks (§7.9)

    internal ProfileApplyResult ApplyProfile(string profileId, ClientId clientId)
    {
        var profile = _profiles.Get(profileId);
        // Plan from a fresh read. Each individual mutation still goes through the
        // normal toggle path, which refreshes the ledger before the next one.
        Reload();

        var desired = profile.ServerIDs.ToHashSet(StringComparer.Ordinal);
        var known = _current.Servers.ToDictionary(server => server.Id, StringComparer.Ordinal);
        var failures = desired
            .Where(id => !known.ContainsKey(id))
            .Select(id => new ProfileApplyFailure(id, "Server is missing from every client."))
            .ToList();

        var changes = _current.Servers
            .Select(server => new
            {
                server.Id,
                server.Name,
                Current = server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent),
                Wanted = desired.Contains(server.Id),
            })
            .Where(item => item.Wanted
                ? item.Current != Enablement.Enabled
                : item.Current == Enablement.Enabled)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var enabledCount = 0;
        var disabledCount = 0;
        var requiresRestart = false;
        foreach (var change in changes)
        {
            try
            {
                var result = SetEnabled(change.Wanted, change.Id, clientId);
                if (change.Wanted) enabledCount++;
                else disabledCount++;
                requiresRestart |= result.RequiresRestart;
            }
            catch (Exception error) when (
                error is ToggleException or ConfigWriteException or GatewayManagedException or
                         IOException or UnauthorizedAccessException)
            {
                failures.Add(new ProfileApplyFailure(change.Name, error.Message));
            }
        }

        Reload();
        return new ProfileApplyResult(
            profile.Name,
            clientId,
            enabledCount,
            disabledCount,
            requiresRestart,
            failures);
    }

    // MARK: - Health (§7.3, §7.4)

    /// <summary>Runs one server and records what came back.</summary>
    /// <remarks>
    /// Off the UI thread, because it takes up to ten seconds and a frozen window
    /// would be the app's own answer to "is this server slow?".
    /// </remarks>
    internal async Task<HealthResult> CheckHealthAsync(string serverId)
    {
        var server = ServerById(serverId);
        var (health, weight) = await Task.Run(() => new HealthChecker().Check(server));

        _metadata.Record(server.Id, health, weight);
        Reload();
        return health;
    }

    /// <summary>Opens only the HTTPS page captured by native during the last check.</summary>
    internal void OpenAuthorization(string serverId)
    {
        var health = _metadata.For(serverId)?.Health;
        if (health?.Status != HealthStatus.NeedsAuthorization ||
            !AuthorizationSignal.IsValidAuthorizationURL(health.AuthorizationURL))
        {
            throw new InvalidOperationException(
                "No authorization page is recorded for this server. Check it again to capture one.");
        }

        _authorizationPageLauncher.Open(health.AuthorizationURL!);
    }

    /// <summary>Checks every stdio server, one at a time.</summary>
    /// <remarks>
    /// Sequential on purpose (§7.3): these are arbitrary commands, and starting a
    /// dozen node processes at once to save a few seconds is not a trade worth
    /// making on someone's laptop.
    /// </remarks>
    internal async Task CheckAllHealthAsync(Action<string, int, int> onProgress)
    {
        var servers = _current.Servers
            .Where(server => server.Transport == Transport.Stdio &&
                             server.Health?.Status != HealthStatus.NeedsAuthorization)
            .ToArray();

        for (var index = 0; index < servers.Length; index++)
        {
            onProgress(servers[index].Name, index, servers.Length);
            await CheckHealthAsync(servers[index].Id);
        }
    }

    // MARK: - Secrets (§6)

    internal IReadOnlyList<SecretRecord> Secrets() => SecretsService().Scan(_current.Servers);

    /// <summary>
    /// The one call that hands a real value back. §6 permits it behind a confirm,
    /// and the UI asks before getting here.
    /// </summary>
    internal string? RevealSecret(string secretId) =>
        SecretsService().Reveal(secretId, _current.Servers);

    internal SecretsService.RotationResult RotateSecret(string secretId, string newValue, bool alsoStore)
    {
        var service = SecretsService();
        var result = service.Rotate(secretId, newValue, _current.Servers, alsoStore);
        Reload();
        return result;
    }

    internal void AdoptSecret(string secretId) => SecretsService().Adopt(secretId, _current.Servers);

    internal void ForgetSecret(string secretId) => SecretsService().Forget(secretId);

    internal void RestrictPermissions(ClientId clientId) => SecretsService().RestrictPermissions(clientId);

    private SecretsService SecretsService() => new(
        home: _home,
        writer: new ConfigWriter(Backups),
        ledger: _ledger,
        store: _secretStore,
        pathOverrides: Settings.ClientPathOverrides);

    private ServerAuthoring Authoring() => new(
        home: _home,
        writer: new ConfigWriter(Backups),
        parkStore: _parkStore,
        ledger: _ledger,
        pathOverrides: Settings.ClientPathOverrides);

    private GatewayMigrationService GatewayMigration() => new(
        home: _home,
        helperPath: _gatewayHelperPath,
        writer: new ConfigWriter(Backups),
        ledger: _ledger,
        routes: _gatewayRoutes,
        secrets: _gatewaySecretStore,
        pathOverrides: Settings.ClientPathOverrides);

    private AuthoringResult Record(AuthoringResult result)
    {
        Reload();
        return result;
    }

    internal void Restore(string backupId, ClientId clientId)
    {
        var backup = Backups.Get(backupId, clientId)
            ?? throw new InvalidOperationException($"No backup called \"{backupId}\".");

        // Resolved before anything else runs, so a backup whose recorded origin is no
        // longer a file Kytto writes for this client fails with nothing written — and
        // so the digest check below is about the file that will actually be replaced,
        // not about the path the sidecar happens to name.
        var resolver = PathResolver();
        var target = BackupStore.RestoreTarget(backup, resolver);

        GatewayMigration().ValidateBackupRestore(Backups.Contents(backup), clientId);
        Backups.RestoreChecked(backup, _ledger.DigestFor(target), resolver);
        Reload();
        GatewayMigration().ReconcileAfterBackupRestore(clientId);
    }

    /// <summary>
    /// Brings what earlier versions stored up to today's permissions, at launch.
    /// </summary>
    /// <remarks>
    /// Best effort and silent: it creates nothing, changes only what grants access to
    /// someone else, and a failure on one item must not stop Kytto opening. The count
    /// goes to the log and nowhere else — the paths involved are the user's.
    /// </remarks>
    internal void TightenStoragePermissions()
    {
        try
        {
            var changed = KyttoStorage.TightenExisting(_paths);
            if (changed > 0) Log.Write($"storage: tightened {changed} item(s)");
        }
        catch (Exception error)
        {
            Log.Failure("storage permissions", error);
        }
    }

    private ClientPathResolver PathResolver() => new(_home, Settings.ClientPathOverrides);

    /// <summary>
    /// Counts semantic definition changes against the last acknowledged state.
    /// Formatting, comments and write count are intentionally absent: a write
    /// followed by its inverse must settle back to zero (§7.1).
    /// </summary>
    private void RefreshRestartState()
    {
        foreach (var client in ClientIds.All)
        {
            var current = RestartSnapshot(client);
            if (_restartObserved.TryGetValue(client, out var observed) &&
                !SameSnapshot(observed, current) &&
                _restartBaselines.TryGetValue(client, out var baseline) &&
                !SameSnapshot(baseline, current))
            {
                // A new net change makes a previously dismissed notice relevant
                // again; dismissal is presentation state, not a baseline update.
                _dismissedRestarts.Remove(client);
            }

            var count = _restartBaselines.TryGetValue(client, out var expected)
                ? ChangedEntries(expected, current)
                : current.Count;
            if (count > 0) PendingRestarts[client] = count;
            else
            {
                PendingRestarts.Remove(client);
                _dismissedRestarts.Remove(client);
            }

            _restartObserved[client] = current;
        }
    }

    private Dictionary<string, string> RestartSnapshot(ClientId client)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var server in _current.Servers)
        {
            var state = server.EnabledIn.GetValueOrDefault(client, Enablement.Absent);
            var definition = server.DefinitionsByClient.GetValueOrDefault(client);
            if (state == Enablement.Absent && definition is null) continue;

            var value = new StringBuilder()
                .Append(state.Raw()).Append('\u0001')
                .Append(definition?.Transport.Raw() ?? "").Append('\u0001')
                .Append(definition?.Command ?? "").Append('\u0001')
                .Append(definition?.Url ?? "").Append('\u0001')
                .Append(definition?.IsBundled == true ? "bundled" : "config")
                .ToString();
            if (definition is not null)
            {
                value += '\u0001' + string.Join('\u0002', definition.Args) + '\u0001' +
                    string.Join('\u0002', definition.Env.Select(entry =>
                        entry.Key + '\u0003' + (entry.Value is null ? "<null>" : entry.Value)));
            }
            snapshot[server.Id] = value;
        }

        foreach (var route in _gatewayRoutes.All()
                     .Where(route => route.ClientID == client)
                     .OrderBy(route => route.Id, StringComparer.Ordinal))
        {
            snapshot[$"@gateway:{route.Id}"] = route.ServerID + "\u0001" +
                (route.ExposedTools is null
                    ? "*"
                    : string.Join('\u0002', route.ExposedTools.Order(StringComparer.Ordinal)));
        }
        return snapshot;
    }

    private static int ChangedEntries(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> current) =>
        expected.Keys.Concat(current.Keys).Distinct(StringComparer.Ordinal)
            .Count(key => expected.GetValueOrDefault(key) != current.GetValueOrDefault(key));

    private static bool SameSnapshot(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private DiscoveryResult Discover()
    {
        var discovery = new Discovery(
            home: _home,
            locator: _locator,
            recordDigest: (path, digest) => _ledger.Record(path, digest),
            parked: _parkStore.All()
                .Select(entry => new ParkedEntry(entry.ClientID, entry.ServerName, entry.SourceText))
                .ToArray(),
            metadata: _metadata.All(),
            pathOverrides: Settings.ClientPathOverrides,
            customSources: Settings.CustomConfigSources);

        var result = discovery.Run();

        // Started after the first read, so the ledger is primed before anything can
        // report a change against it.
        if (_watchConfigs)
        {
            _watcher ??= new ConfigWatcher(
                discovery.WatchTargets(),
                () => ConfigsChanged?.Invoke());
        }

        return result;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _updates.Dispose();
        _updatePackages.Dispose();
        _provenanceChecker.Dispose();
    }
}

internal sealed class GatewayManagedException() : InvalidOperationException(
    "This server is using Gateway mode. Restore Direct mode before editing, disabling or removing it.");

internal sealed record LibraryDirectorySelection(string SessionID, string DisplayName);

internal sealed record SkillsDirectorySelection(string SessionID, string DisplayName);

internal sealed record LibrarySession(
    string Root,
    IReadOnlyList<Kytto.Core.Library.McpLibraryCandidate>? Candidates);

internal sealed record LibraryImportPreview(
    Kytto.Core.Library.McpLibraryCandidate Candidate,
    IReadOnlyList<ClientId> TargetClientIDs,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

internal sealed record AgentImportPreview(
    IReadOnlyList<Kytto.Core.Imports.AgentImportServer> Servers,
    IReadOnlyList<ClientId> TargetClientIDs,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

internal sealed record AgentImportResult(
    IReadOnlyList<string> ImportedServerNames,
    IReadOnlyList<ClientId> ChangedClientIDs,
    IReadOnlyList<string> Warnings,
    DiscoveryResult State);

internal sealed class NoDriftException(string name) : InvalidOperationException(
    $"Every editable copy of “{name}” already matches. There is nothing to unify.");

/// <summary>What unifying would do, before it does it.</summary>
/// <remarks>
/// Carries the source client's command and its environment <strong>key names</strong>.
/// No value is in here, which is what lets it cross to the web layer at all.
/// </remarks>
/// <param name="TargetClientIDs">Clients that would be rewritten.</param>
/// <param name="SkippedClientIDs">
/// Clients holding a copy that cannot be rewritten whatever is chosen — extension
/// bundles and plugins, which are installed software (§4).
/// </param>
internal sealed record UnifyPreview(
    string ServerID,
    string ServerName,
    ClientId SourceClientID,
    IReadOnlyList<ClientId> TargetClientIDs,
    IReadOnlyList<ClientId> SkippedClientIDs,
    string CommandSummary,
    string? Url,
    Transport Transport,
    IReadOnlyList<string> EnvironmentKeys);

internal sealed record ContractAlert(
    string ServerID,
    string ServerName,
    DateTimeOffset ChangedAt,
    IReadOnlyList<ContractChange> Changes);

internal sealed record DoctorFixPreview(
    string ServerID,
    string ServerName,
    string CurrentCommand,
    string ReplacementCommand,
    IReadOnlyList<ClientId> ClientIDs);
