using System.Reflection;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;
using Kytto.Core.Imports;
using Kytto.Core.Library;
using Kytto.Core.Skills;
using Kytto.Core.Secrets;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Updates;

namespace Kytto.App.Ipc;

/// <summary>
/// Registers every command the web layer can call.
/// </summary>
/// <remarks>
/// Keep this list and <c>docs/ipc.md</c> in step — the doc is the contract this
/// port is written against, and a command that answers differently here than it
/// does on macOS is a bug in one of the two.
/// </remarks>
internal static class CommandRegistry
{
    /// <param name="openSettings">
    /// How this shell shows its Settings window. Supplied by the window rather
    /// than reached for from here, so the registry keeps knowing nothing about
    /// WPF — and so a test host that has no window still registers the command
    /// and answers it (§3.2).
    /// </param>
    internal static void RegisterAll(CommandRouter router, AppModel model, Action? openSettings = null)
    {
        router.Register("app.info", AppInfo);

        router.Register<UpdateCheckPayload, UpdateCheckDto>("updates.check", async payload =>
            (await model.CheckUpdatesAsync(payload.Force)).ToDto());

        router.Register("updates.openDownload", () =>
        {
            model.OpenUpdateDownload();
            return new Dictionary<string, string>();
        });

        router.Register<UpdatePackageDto>("updates.download", async () =>
        {
            var package = await model.DownloadUpdateAsync(progress =>
                _ = router.EmitAsync("updates.progress", new UpdateProgressDto(
                    progress.Phase,
                    progress.Progress,
                    progress.BytesReceived,
                    progress.TotalBytes)));
            return new UpdatePackageDto(package.Token, package.Version, package.Sha256, package.ByteCount);
        });

        router.Register("updates.cancel", () =>
        {
            model.CancelUpdateDownload();
            return new Dictionary<string, string>();
        });

        router.Register<UpdateInstallPayload, Dictionary<string, string>>("updates.install", async payload =>
        {
            await model.InstallUpdateAsync(payload.Token, progress =>
                _ = router.EmitAsync("updates.progress", new UpdateProgressDto(
                    progress.Phase,
                    progress.Progress,
                    progress.BytesReceived,
                    progress.TotalBytes)));
            return new Dictionary<string, string>();
        });

        router.Register<ProvenanceCheckPayload, ProvenanceCheckResultDto>(
            "provenance.checkLatest",
            async payload =>
            {
                var provenance = await model.CheckLatestProvenanceAsync(payload.ServerID);
                return new ProvenanceCheckResultDto(provenance.ToDto(), model.ToStateDto());
            });

        router.Register("imports.prompt", () => new ImportPromptDto(model.AgentImportPrompt()));

        router.Register<AgentImportPayload, AgentImportPreviewDto>("imports.preview", payload =>
        {
            var clients = WritableClientIDs(payload.ClientIDs);
            var preview = model.PreviewAgentImport(payload.Json, clients);
            return new AgentImportPreviewDto(
                preview.Servers.Select(server => new AgentImportServerDto(
                    server.Name,
                    server.Transport.Raw(),
                    string.Join(' ', (server.Command is null ? [] : new[] { server.Command })
                        .Concat(server.Args)),
                    server.EnvironmentKeys,
                    server.Url)).ToArray(),
                clients.Select(client => client.Raw()).ToArray(),
                preview.Conflicts,
                preview.Warnings);
        });

        router.Register<AgentImportPayload, AgentImportResultDto>("imports.import", payload =>
        {
            var result = model.ImportAgent(payload.Json, WritableClientIDs(payload.ClientIDs));
            return new AgentImportResultDto(
                result.ImportedServerNames,
                result.ChangedClientIDs.Select(client => client.Raw()).ToArray(),
                result.Warnings,
                model.ToStateDto());
        });

        router.Register("library.chooseDirectory", () =>
        {
            var selection = model.ChooseLibraryDirectory();
            return selection is null
                ? null
                : new LibraryDirectoryDto(selection.SessionID, selection.DisplayName);
        });

        router.Register<LibrarySessionPayload, LibraryScanDto>("library.scan", payload =>
        {
            var result = model.ScanLibrary(LibraryToken(payload.SessionID, payload.Path, "library"));
            return new LibraryScanDto(
                result.Candidates.Select(ToDto).ToArray(),
                result.Warnings,
                result.IsComplete);
        });

        router.Register<LibraryImportPayload, LibraryPreviewDto>("library.preview", payload =>
        {
            var clients = WritableClientIDs(payload.ClientIDs);
            var preview = model.PreviewLibrary(
                LibraryToken(payload.SessionID, payload.Path, "library"),
                payload.CandidateID,
                clients);
            return new LibraryPreviewDto(
                ToDto(preview.Candidate),
                clients.Select(client => client.Raw()).ToArray(),
                preview.Conflicts,
                preview.Warnings);
        });

        router.Register<LibraryImportPayload, LibraryImportResultDto>("library.import", payload =>
        {
            var clients = WritableClientIDs(payload.ClientIDs);
            var sessionID = LibraryToken(payload.SessionID, payload.Path, "library");
            var preview = model.PreviewLibrary(sessionID, payload.CandidateID, clients);
            var result = model.ImportLibrary(sessionID, payload.CandidateID, clients);
            return new LibraryImportResultDto(
                result.ServerName,
                result.Changed.Select(client => client.Raw()).ToArray(),
                preview.Warnings,
                model.ToStateDto());
        });

        router.Register("skills.chooseDirectory", () =>
        {
            var selection = model.ChooseSkillsDirectory();
            return selection is null
                ? null
                : new SkillsDirectoryDto(selection.SessionID, selection.DisplayName);
        });

        router.Register<SkillsInventoryPayload, SkillsInventoryDto>("skills.inventory", payload =>
        {
            var result = model.InventorySkills(payload.SessionID ?? payload.WorkspacePath);
            return new SkillsInventoryDto(
                result.Skills.Select(skill => new SkillInventoryItemDto(
                    skill.ID,
                    skill.Name,
                    skill.Description,
                    skill.LocationDisplay,
                    skill.Status,
                    skill.Warnings,
                    skill.Agent,
                    skill.Scope,
                    skill.ScopeLabel,
                    skill.MetadataStatus,
                    skill.IsDuplicate,
                    skill.IsConflict)).ToArray(),
                result.Warnings);
        });

        router.Register("clients.list", () =>
            model.Current().Clients.Select(DtoMapping.ToDto).ToArray());

        router.Register("servers.list", () =>
            model.Current().Servers.Select(DtoMapping.ToDto).ToArray());

        router.Register("diagnostics.list", () =>
            model.Current().Diagnostics.Select(DtoMapping.ToDto).ToArray());

        router.Register("state.get", model.ToStateDto);

        router.Register("discovery.refresh", () =>
        {
            model.Reload();
            return model.ToStateDto();
        });

        // MARK: - Gateway (§3.3)

        router.Register<GatewayTargetPayload, GatewayMigrationPreviewDto>("gateway.preview", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            return model.GatewayPreview(payload.ServerID, clientId).ToDto();
        });

        router.Register<EnableGatewayPayload, GatewayMigrationResultDto>("gateway.enable", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            return GatewayResult(
                model.EnableGateway(payload.ServerID, clientId, payload.RouteID),
                model);
        });

        router.Register<GatewayRoutePayload, GatewayMigrationResultDto>("gateway.restore", payload =>
            GatewayResult(model.RestoreDirect(payload.RouteID), model));

        router.Register<ExposedToolsPayload, StateDto>("gateway.setExposedTools", payload =>
        {
            model.SetExposedTools(payload.RouteID, payload.ToolNames);
            return model.ToStateDto();
        });

        router.Register("activity.list", () => model.Activity().ToDto());

        // MARK: - MCP Doctor and Contract Guard (§7.10)

        router.Register<DoctorFixPayload, DoctorFixPreviewDto>("doctor.previewFix", payload =>
            model.DoctorFixPreview(payload.ServerID).ToDto());

        router.Register<DoctorFixPayload, StateDto>("contract.acknowledge", payload =>
        {
            model.AcknowledgeContract(payload.ServerID);
            return model.ToStateDto();
        });

        router.Register<DoctorFixPayload, AuthoringResultDto>("doctor.applyFix", payload =>
            Authored(model.ApplyDoctorFix(payload.ServerID), model));

        // MARK: - Drift (§7.12)

        router.Register<UnifyPayload, UnifyPreviewDto>("servers.unifyPreview", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.SourceClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.SourceClientID}.");
            return model.UnifyPreviewFor(payload.ServerID, clientId).ToDto();
        });

        router.Register<UnifyPayload, UnifyResultDto>("servers.unify", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.SourceClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.SourceClientID}.");
            var result = model.UnifyServer(payload.ServerID, clientId);
            return new UnifyResultDto(
                ServerName: result.ServerName,
                Changed: result.Changed.Select(id => id.Raw()).ToArray(),
                ParkedUpdated: result.ParkedUpdated.Select(id => id.Raw()).ToArray(),
                ParkedFailures: result.ParkedFailures.Select(id => id.Raw()).ToArray(),
                RequiresRestart: result.RequiresRestart,
                State: model.ToStateDto());
        });

        // MARK: - Profiles / MCP stacks (§7.9)

        router.Register("profiles.list", () => Profiles(model.Profiles(), model.Current().Servers));

        router.Register<SaveProfilePayload, IReadOnlyList<ProfileDto>>("profiles.create", payload =>
            Profiles(
                model.CreateProfile(payload.Name, payload.ServerIDs, payload.TokenBudget),
                model.Current().Servers));

        router.Register<UpdateProfilePayload, IReadOnlyList<ProfileDto>>("profiles.update", payload =>
            Profiles(
                model.UpdateProfile(
                    payload.ProfileID,
                    payload.Name,
                    payload.ServerIDs,
                    payload.TokenBudget),
                model.Current().Servers));

        router.Register<ProfileIdPayload, IReadOnlyList<ProfileDto>>("profiles.delete", payload =>
            Profiles(model.DeleteProfile(payload.ProfileID), model.Current().Servers));

        router.Register<ApplyProfilePayload, ProfileApplyResultDto>("profiles.apply", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            var result = model.ApplyProfile(payload.ProfileID, clientId);
            return new ProfileApplyResultDto(
                result.ProfileName,
                result.ClientID.Raw(),
                result.EnabledCount,
                result.DisabledCount,
                result.RequiresRestart,
                result.Failures.Select(failure =>
                    new ProfileFailureDto(failure.ServerName, failure.Message)).ToArray(),
                model.ToStateDto());
        });

        // MARK: - Writes

        // The one command that writes to a client's configuration. Every call goes
        // through the §6 pipeline: refuse if the file changed on disk, back it up,
        // splice, write atomically.
        router.Register<SetEnabledPayload, ToggleResultDto>("servers.setEnabled", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");

            var result = model.SetEnabled(payload.Enabled, payload.ServerID, clientId);
            return new ToggleResultDto(
                ServerName: result.ServerName,
                ClientID: result.ClientID.Raw(),
                Enabled: result.Enabled,
                RequiresRestart: result.RequiresRestart,
                WasParked: result.WasParked,
                BackupID: result.BackupID,
                PathDisplay: result.PathDisplay,
                State: model.ToStateDto());
        });

        router.Register<AcknowledgePayload, StateDto>("restarts.acknowledge", payload =>
        {
            ClientId? clientId = null;
            if (payload.ClientID is not null)
            {
                clientId = ClientIds.FromRaw(payload.ClientID)
                    ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            }
            model.AcknowledgeRestart(clientId);
            return model.ToStateDto();
        });

        router.Register<AcknowledgePayload, StateDto>("restarts.dismiss", payload =>
        {
            ClientId? clientId = null;
            if (payload.ClientID is not null)
            {
                clientId = ClientIds.FromRaw(payload.ClientID)
                    ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            }
            model.DismissRestart(clientId);
            return model.ToStateDto();
        });

        // MARK: - Authoring (§7.2, §7.5)

        router.Register("catalog.list", () => Catalog.Entries
            .Select(entry => new CatalogEntryDto(
                Id: entry.Id,
                Name: entry.Name,
                DisplayName: entry.DisplayName,
                Description: entry.Description,
                Transport: entry.Transport,
                Command: entry.Command,
                Args: entry.Args,
                Url: entry.Url,
                Placeholders: entry.Placeholders
                    .Select(item => new PlaceholderDto(item.Token, item.Label, item.Example))
                    .ToArray(),
                Env: entry.Env
                    .Select(item => new EnvRequirementDto(item.Key, item.Required, item.Hint))
                    .ToArray(),
                Requires: entry.Requires,
                Homepage: entry.Homepage))
            .ToArray());

        router.Register<CreateServerPayload, AuthoringResultDto>("servers.create", payload =>
        {
            var clients = payload.ClientIDs
                .Select(raw => ClientIds.FromRaw(raw)
                    ?? throw new BadArgumentException($"Unknown client {raw}."))
                .ToArray();
            if (clients.Length == 0)
            {
                throw new BadArgumentException("Choose at least one client to add this server to.");
            }
            return Authored(model.CreateServer(payload.Draft.MakeDraft(), clients), model);
        });

        router.Register<UpdateServerPayload, AuthoringResultDto>("servers.update", payload =>
            Authored(model.UpdateServer(payload.Draft.MakeDraft(), payload.ServerID), model));

        router.Register<DeleteServerPayload, AuthoringResultDto>("servers.delete", payload =>
            Authored(model.DeleteServer(payload.ServerID), model));

        // Distinct from `servers.setEnabled` on purpose: off is a state a client
        // supports, this is the server ceasing to be that client's business.
        router.Register<RemoveFromClientPayload, AuthoringResultDto>("servers.removeFromClient", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            return Authored(model.RemoveServerFrom(payload.ServerID, clientId), model);
        });

        // MARK: - Backups, the visible half of the trust story (§6.5)

        router.Register<BackupsListPayload, IReadOnlyList<BackupDto>>("backups.list", payload =>
        {
            IReadOnlyList<ClientId> clients;
            if (payload.ClientID is null)
            {
                clients = ClientIds.All;
            }
            else
            {
                clients =
                [
                    ClientIds.FromRaw(payload.ClientID)
                        ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.")
                ];
            }

            return clients
                .SelectMany(model.Backups.List)
                .OrderByDescending(backup => backup.TakenAt)
                .Select(backup => new BackupDto(
                    Id: backup.Id,
                    ClientID: backup.ClientID.Raw(),
                    PathDisplay: backup.OriginalPathDisplay,
                    TakenAt: backup.TakenAt.ToUnixTimeMilliseconds() / 1000.0,
                    ByteCount: backup.ByteCount))
                .ToArray();
        });

        router.Register<RestoreBackupPayload, StateDto>("backups.restore", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            model.Restore(payload.BackupID, clientId);
            return model.ToStateDto();
        });

        // MARK: - Settings and onboarding (§7.6)

        router.Register("settings.get", () => model.Settings.ToDto());

        // The sidebar has a Settings row; what a settings window is belongs to
        // the shell, so the page only ever states the intent (§3.2). The opener
        // queues the window and returns, so the reply does not wait for the
        // dialog the person is still standing in.
        router.Register("settings.open", () =>
        {
            openSettings?.Invoke();
            return new Dictionary<string, string>();
        });

        router.Register("settings.confirmMatrixWrites", () =>
            model.UpdateSettings(settings => settings with
            {
                HasConfirmedMatrixWrites = true,
            }).ToDto());

        router.Register<ShowsCustomSourcesPayload, SettingsDto>(
            "settings.setShowsCustomSources",
            payload => model.UpdateSettings(settings => settings with
            {
                ShowsCustomSources = payload.Shown,
            }).ToDto());

        router.Register<SidebarWidthPayload, SettingsDto>(
            "settings.setSidebarWidth",
            payload => model.UpdateSettings(settings => settings with
            {
                SidebarWidth = payload.Width,
            }).ToDto());

        router.Register("onboarding.complete", () =>
            model.UpdateSettings(settings => settings with { HasCompletedOnboarding = true }).ToDto());

        // MARK: - Health (§7.3, §7.4)

        router.Register<HealthCheckPayload, HealthCheckResultDto>("health.check", async payload =>
        {
            var health = await model.CheckHealthAsync(payload.ServerID);
            return new HealthCheckResultDto(health.ToDto(), model.ToStateDto());
        });

        router.Register<HealthCheckPayload, IReadOnlyDictionary<string, string>>(
            "health.openAuthorization",
            payload =>
            {
                model.OpenAuthorization(payload.ServerID);
                return new Dictionary<string, string>();
            });

        router.Register("health.checkAll", async () =>
        {
            await model.CheckAllHealthAsync((name, index, total) =>
            {
                // Progress goes out on the event channel so the UI can name the
                // server it is waiting on — a ten-second stall with no explanation
                // reads as a hang.
                _ = router.EmitAsync("health.progress", new HealthProgressDto(name, index, total));
            });
            return model.ToStateDto();
        });

        // The bridge is deliberately platform-neutral. WPF's dispatcher is STA,
        // which is exactly the apartment the native clipboard requires.
        router.Register<ClipboardPayload, IReadOnlyDictionary<string, string>>(
            "clipboard.writeText",
            payload =>
            {
                System.Windows.Clipboard.SetText(payload.Text);
                return new Dictionary<string, string>();
            });

        // MARK: - Secrets (§6)

        router.Register("secrets.list", () => model.Secrets().Select(ToDto).ToArray());

        // The one command that hands a value to the web layer, and only because the
        // user asked for it in front of a confirmation (§6).
        router.Register<SecretIdPayload, Dictionary<string, string>>("secrets.reveal", payload =>
        {
            var value = model.RevealSecret(payload.SecretID)
                ?? throw new BadArgumentException("That secret is no longer in any configuration.");
            return new Dictionary<string, string> { ["value"] = value };
        });

        router.Register<RotateSecretPayload, RotationResultDto>("secrets.rotate", payload =>
        {
            var result = model.RotateSecret(
                payload.SecretID, payload.NewValue, payload.StoreInSecretStore ?? true);
            return new RotationResultDto(
                Key: result.Key,
                UpdatedCount: result.Updated.Count,
                Clients: result.Updated
                    .Select(usage => usage.ClientID.Raw())
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                State: model.ToStateDto());
        });

        router.Register<SecretIdPayload, IReadOnlyList<SecretRecordDto>>("secrets.adopt", payload =>
        {
            model.AdoptSecret(payload.SecretID);
            return model.Secrets().Select(ToDto).ToArray();
        });

        router.Register<SecretIdPayload, IReadOnlyList<SecretRecordDto>>("secrets.forget", payload =>
        {
            model.ForgetSecret(payload.SecretID);
            return model.Secrets().Select(ToDto).ToArray();
        });

        router.Register<RestrictPayload, IReadOnlyList<SecretRecordDto>>("secrets.restrictPermissions", payload =>
        {
            var clientId = ClientIds.FromRaw(payload.ClientID)
                ?? throw new BadArgumentException($"Unknown client {payload.ClientID}.");
            model.RestrictPermissions(clientId);
            return model.Secrets().Select(ToDto).ToArray();
        });
    }

    private static SecretRecordDto ToDto(SecretRecord record) => new(
        Id: record.Id,
        Key: record.Key,
        MaskedValue: record.MaskedValue,
        Usages: record.Usages
            .Select(usage => new SecretUsageDto(
                usage.ServerID, usage.ServerName, usage.ClientID.Raw(), usage.PathDisplay))
            .ToArray(),
        IsInSecretStore: record.IsInSecretStore,
        IsShared: record.IsShared,
        Exposure: record.Exposure
            .Select(item => new SecretExposureDto(item.Kind, item.PathDisplayValue, item.Detail))
            .ToArray());

    private static AuthoringResultDto Authored(AuthoringResult result, AppModel model) => new(
        ServerName: result.ServerName,
        Changed: result.Changed.Select(client => client.Raw()).ToArray(),
        RequiresRestart: result.RequiresRestart,
        State: model.ToStateDto());

    private static AppInfoDto AppInfo()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = AppModel.CurrentAppVersion();

        return new AppInfoDto(
            Version: version,
            Build: assembly.GetName().Version?.Revision.ToString() ?? "0",
            // The UI enables features by asking for a capability, never by guessing
            // from the platform (§3.2). Nothing is claimed here that the shell
            // cannot actually do yet.
            Capabilities:
            [
                "config.read", "config.write", "backups",
                "authoring", "catalog", "secrets", "health",
                "profiles", "inspector", "clipboard", "gateway.stdio", "gateway.masking",
                "doctor", "activity", "contract-guard", "context-optimizer", "drift",
                "updates", "provenance", "agent-import", "library", "skills",
            ],
            // For display only — branching on it is a §3.2 violation.
            PlatformDisplayName: "Windows",
            // The name this platform's users know the store by. macOS answers
            // "Keychain" here; the secrets screen is shared and reads whichever
            // it is told.
            SecretStoreDisplayName: "Credential Manager");
    }

    private static IReadOnlyList<ProfileDto> Profiles(
        IReadOnlyList<Profile> profiles,
        IReadOnlyList<Server> servers) =>
        profiles.Select(profile => profile.ToDto(servers)).ToArray();

    private static GatewayMigrationResultDto GatewayResult(
        GatewayMigrationResult result,
        AppModel model) => new(
            result.Route.ToDto(),
            result.BackupID,
            result.PathDisplay,
            model.ToStateDto());

    private static IReadOnlyList<ClientId> WritableClientIDs(IReadOnlyList<string> raw)
    {
        var clients = raw.Select(id => ClientIds.FromRaw(id)
                ?? throw new BadArgumentException($"Unknown built-in client {id}."))
            .ToArray();
        if (clients.Length == 0 || clients.Distinct().Count() != clients.Length)
        {
            throw new BadArgumentException("Choose one or more distinct built-in clients.");
        }
        return clients;
    }

    private static string LibraryToken(string? legacyToken, string? compatibleToken, string kind) =>
        legacyToken ?? compatibleToken
        ?? throw new BadArgumentException($"Choose a {kind} directory before scanning it.");

    private static LibraryCandidateDto ToDto(McpLibraryCandidate candidate) => new(
        ID: candidate.ID,
        Name: candidate.Name,
        RelativeLocation: candidate.RelativeLocation,
        Transport: candidate.Transport.Raw(),
        CommandSummary: candidate.CommandSummary,
        EnvironmentKeys: candidate.EnvironmentKeys,
        DetectedClientIDs: candidate.DetectedClients.Select(client => client.Raw()).ToArray(),
        MissingClientIDs: candidate.MissingClients.Select(client => client.Raw()).ToArray(),
        Warnings: candidate.Warnings);
}

// MARK: - Payloads

internal sealed record SetEnabledPayload(string ServerID, string ClientID, bool Enabled);

internal sealed record ShowsCustomSourcesPayload(bool Shown);

internal sealed record SidebarWidthPayload(int Width);

internal sealed record UpdateInstallPayload(string Token);

internal sealed record ProvenanceCheckPayload(string ServerID);

internal sealed record AgentImportPayload(string Json, IReadOnlyList<string> ClientIDs);

internal sealed record LibrarySessionPayload(string? SessionID = null, string? Path = null);

internal sealed record LibraryImportPayload(
    string CandidateID,
    IReadOnlyList<string> ClientIDs,
    string? SessionID = null,
    string? Path = null);

internal sealed record SkillsInventoryPayload(
    string? SessionID = null,
    string? WorkspacePath = null);

internal sealed record GatewayTargetPayload(string ServerID, string ClientID);

internal sealed record EnableGatewayPayload(string ServerID, string ClientID, string RouteID);

internal sealed record GatewayRoutePayload(string RouteID);

/// <param name="ToolNames">
/// Null stops masking; an empty array is a different instruction — expose nothing —
/// and is honoured as written (§7.11).
/// </param>
internal sealed record ExposedToolsPayload(string RouteID, IReadOnlyList<string>? ToolNames);

internal sealed record DoctorFixPayload(string ServerID);

internal sealed record UnifyPayload(string ServerID, string SourceClientID);

/// <summary>A server as described by the edit form.</summary>
internal sealed record ServerDraftPayload(
    string Name,
    string Transport,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyList<ServerDraftPayload.EnvPayload>? Env,
    string? Url)
{
    /// <param name="Value">
    /// Null means "keep whatever value is already in the config". The form never
    /// receives secret values, so it cannot send them back; the native side merges
    /// them in before writing (§6).
    /// </param>
    internal sealed record EnvPayload(string Key, string? Value);

    internal ServerDraft MakeDraft() => new()
    {
        Name = Name,
        Transport = Transports.FromRaw(Transport)
            ?? throw new BadArgumentException($"Unknown transport {Transport}."),
        Command = Command ?? "",
        Args = Args ?? [],
        Env = (Env ?? []).Select(entry => new EnvEntry(entry.Key, entry.Value)).ToArray(),
        Url = Url ?? "",
    };
}

internal sealed record CreateServerPayload(ServerDraftPayload Draft, IReadOnlyList<string> ClientIDs);

internal sealed record UpdateServerPayload(string ServerID, ServerDraftPayload Draft);

internal sealed record DeleteServerPayload(string ServerID);

internal sealed record RemoveFromClientPayload(string ServerID, string ClientID);

internal sealed record HealthCheckPayload(string ServerID);

internal sealed record SaveProfilePayload(
    string Name,
    IReadOnlyList<string> ServerIDs,
    int? TokenBudget = null);

internal sealed record UpdateProfilePayload(
    string ProfileID,
    string Name,
    IReadOnlyList<string> ServerIDs,
    int? TokenBudget = null);

internal sealed record ProfileIdPayload(string ProfileID);

internal sealed record ApplyProfilePayload(string ProfileID, string ClientID);

internal sealed record ClipboardPayload(string Text);

internal sealed record SecretIdPayload(string SecretID);

internal sealed record RotateSecretPayload(string SecretID, string NewValue, bool? StoreInSecretStore);

internal sealed record RestrictPayload(string ClientID);

internal sealed record BackupsListPayload(string? ClientID);

internal sealed record RestoreBackupPayload(string BackupID, string ClientID);

/// <param name="ClientID">Null acknowledges every client at once.</param>
internal sealed record AcknowledgePayload(string? ClientID);

internal sealed record UpdateCheckPayload(bool Force = false);
