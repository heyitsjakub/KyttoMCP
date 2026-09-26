namespace Kytto.App.Ipc;

/// <summary>
/// What crosses the IPC boundary.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the core model. Two rules are enforced here rather than hoped
/// for at every call site:
/// </para>
/// <list type="number">
/// <item>Secret values never leave the native side. <c>env</c> becomes keys plus a
/// <c>hasValue</c> flag; the value itself stays in C# until it is injected at
/// spawn time (§6).</item>
/// <item>The web layer receives display strings and opaque ids, never a path it
/// has to interpret. Separators, <c>~</c> and <c>%APPDATA%</c> are native
/// concerns (§3.2).</item>
/// </list>
/// <para>
/// Property names are PascalCase and reach the wire as camelCase, which is what
/// the macOS build's <c>JSONEncoder</c> produced and therefore what the web layer
/// already reads. Timestamps are seconds since the epoch, as
/// <c>docs/ipc.md</c> says, not .NET ticks and not ISO strings.
/// </para>
/// </remarks>
internal sealed record AppInfoDto(
    string Version,
    string Build,
    IReadOnlyList<string> Capabilities,
    string PlatformDisplayName,
    /// What this platform calls the credential store Kytto keeps its copy in.
    /// The secrets screen has to name it — "Keep a copy in …" is meaningless
    /// otherwise — and the web layer is shared with the macOS build, so the name
    /// is reported rather than written into the page (§3.2). Display only.
    string SecretStoreDisplayName);

internal sealed record UpdateCheckDto(
    string Status,
    string CurrentVersion,
    string? LatestVersion,
    double? CheckedAt,
    string? ReleaseNotesURL,
    string? Sha256);

internal sealed record UpdateProgressDto(
    string Phase,
    double? Progress,
    long BytesReceived,
    long? TotalBytes);

internal sealed record UpdatePackageDto(string Token, string Version, string Sha256, long ByteCount);

internal sealed record ClientDto(
    string Id,
    string DisplayName,
    string? ShortName,
    string IconAsset,
    /// <c>ready</c> | <c>noConfig</c> | <c>orphanedConfig</c> | <c>notInstalled</c>.
    string State,
    string ConfigPathDisplay,
    string ConfigFormatDisplay,
    /// What switching a server off does to this client's file, in prose (§7.8).
    string OffSwitchSummary,
    int ServerCount,
    string SchemaQuirks,
    bool IsReadOnly,
    string ConfigurationScope,
    string? ScopeLabel);

internal sealed record EnvEntryDto(
    string Key,
    /// Whether a value is set. The value itself is never sent.
    bool HasValue);

internal sealed record ToolAnnotationsDto(
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    bool? IdempotentHint,
    bool? OpenWorldHint);

internal sealed record ToolSummaryDto(
    string Name,
    string? Description,
    string InputSchemaJSON,
    ToolAnnotationsDto Annotations,
    /// What this tool alone costs in context (§7.4). Null when the server was
    /// checked by a build that did not measure per tool.
    int? TokenCount);

internal sealed record HealthDto(
    string Status,
    double CheckedAt,
    int? ToolCount,
    IReadOnlyList<ToolSummaryDto> Tools,
    string? Message,
    /// The server's own output, exactly as it wrote it (§6).
    string? Stderr,
    double DurationSeconds,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    IReadOnlyList<string> CapabilityNames,
    int? PromptCount,
    int? ResourceCount,
    IReadOnlyList<string> InspectionNotes,
    string? ResolvedCommand,
    string? EnvironmentSource,
    string? AuthorizationURL);

internal sealed record TokenWeightDto(
    int Estimate,
    /// The vocabulary or heuristic behind the number, so the UI labels it honestly
    /// rather than always calling it an estimate (§7.4).
    string Method,
    /// False only when the rank table was unavailable and the old character
    /// heuristic answered instead.
    bool IsMeasured,
    double MeasuredAt,
    double PercentOfContext,
    int ReferenceContextWindow);

internal sealed record ServerDto(
    string Id,
    string Name,
    string Transport,
    string CommandSummary,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<EnvEntryDto> Env,
    string? Url,
    /// Client id → <c>enabled</c> | <c>disabled</c> | <c>absent</c>.
    IReadOnlyDictionary<string, string> EnabledIn,
    string OriginKind,
    bool IsBundled,
    /// The fact, not the path: the UI needs to know that copying this definition
    /// somewhere else may not start, and does not need to see what it is made of.
    bool HasRelativePath,
    HealthDto? Health,
    TokenWeightDto? TokenWeight,
    ProvenanceDto? Provenance = null);

internal sealed record ProvenanceDto(
    string SourceKind,
    string? PackageName,
    string? SourceURL,
    string? InstalledVersion,
    string? LatestVersion,
    double? LatestCheckedAt,
    string Confidence,
    string MaintenanceState);

internal sealed record ProvenanceCheckResultDto(ProvenanceDto Provenance, StateDto State);

internal sealed record ImportPromptDto(string Prompt);

internal sealed record AgentImportServerDto(
    string Name,
    string Transport,
    string CommandSummary,
    IReadOnlyList<string> EnvironmentKeys,
    string? Url);

internal sealed record AgentImportPreviewDto(
    IReadOnlyList<AgentImportServerDto> Servers,
    IReadOnlyList<string> TargetClientIDs,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

internal sealed record AgentImportResultDto(
    IReadOnlyList<string> ImportedServerNames,
    IReadOnlyList<string> ChangedClientIDs,
    IReadOnlyList<string> Warnings,
    StateDto State);

internal sealed record LibraryDirectoryDto(string SessionID, string DisplayName);

internal sealed record LibraryCandidateDto(
    string ID,
    string Name,
    string RelativeLocation,
    string Transport,
    string CommandSummary,
    IReadOnlyList<string> EnvironmentKeys,
    IReadOnlyList<string> DetectedClientIDs,
    IReadOnlyList<string> MissingClientIDs,
    IReadOnlyList<string> Warnings);

internal sealed record LibraryScanDto(
    IReadOnlyList<LibraryCandidateDto> Candidates,
    IReadOnlyList<string> Warnings,
    bool IsComplete);

internal sealed record LibraryPreviewDto(
    LibraryCandidateDto Candidate,
    IReadOnlyList<string> TargetClientIDs,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

internal sealed record LibraryImportResultDto(
    string ServerName,
    IReadOnlyList<string> ChangedClientIDs,
    IReadOnlyList<string> Warnings,
    StateDto State);

internal sealed record SkillsDirectoryDto(string SessionID, string DisplayName);

internal sealed record SkillInventoryItemDto(
    string ID,
    string Name,
    string Description,
    string LocationDisplay,
    string Status,
    IReadOnlyList<string> Warnings,
    string Agent,
    string Scope,
    string ScopeLabel,
    string MetadataStatus,
    bool IsDuplicate,
    bool IsConflict);

internal sealed record SkillsInventoryDto(
    IReadOnlyList<SkillInventoryItemDto> Skills,
    IReadOnlyList<string> Warnings);

internal sealed record DiagnosticDto(
    string Severity,
    string? ClientID,
    string PathDisplay,
    string Message);

/// <summary>
/// Everything the matrix needs in one payload, so a toggle can update the whole
/// screen from a single reply instead of firing four follow-up commands.
/// </summary>
internal sealed record StateDto(
    IReadOnlyList<ClientDto> Clients,
    IReadOnlyList<ServerDto> Servers,
    IReadOnlyList<DiagnosticDto> Diagnostics,
    /// Client id → number of changes waiting for that client to restart.
    IReadOnlyDictionary<string, int> PendingRestarts,
    IReadOnlyList<GatewayRouteDto> GatewayRoutes,
    IReadOnlyList<DoctorServerReportDto> DoctorReports,
    IReadOnlyList<ContractAlertDto> ContractAlerts,
    /// Servers whose clients do not agree on what they run. One entry per server,
    /// only when there is a disagreement.
    IReadOnlyList<ServerDriftDto> Drift,
    /// Restart notices hidden by the user without changing the applied baseline.
    IReadOnlyList<string>? DismissedRestarts = null);

internal sealed record DriftVariantDto(
    IReadOnlyList<string> ClientIDs,
    string CommandSummary,
    string Transport,
    string? Url,
    IReadOnlyList<string> EnvironmentKeys,
    bool IsBundled,
    bool CanBeSource);

internal sealed record ServerDriftDto(
    string ServerID,
    string ServerName,
    /// <c>command</c> | <c>arguments</c> | <c>url</c> | <c>transport</c> |
    /// <c>environmentKeys</c> | <c>environmentValues</c>. The UI turns these into
    /// prose; it does not decide what counts as a difference.
    IReadOnlyList<string> Fields,
    IReadOnlyList<DriftVariantDto> Variants,
    IReadOnlyList<string> UnwritableClientIDs);

internal sealed record GatewayRouteDto(
    string Id,
    string ServerID,
    string ServerName,
    string ClientID,
    double CreatedAt,
    /// The tools this route lets through, or null when it exposes everything.
    /// Tool names are model-facing identifiers rather than secrets, so unlike a
    /// route's definition and environment they cross this boundary (§7.11).
    IReadOnlyList<string>? ExposedTools);

internal sealed record GatewayMigrationPreviewDto(
    string RouteID,
    string ServerID,
    string ServerName,
    string ClientID,
    string PathDisplay,
    string FormatDisplay,
    string DirectDefinitionPreview,
    string GatewayDefinitionPreview,
    IReadOnlyList<string> EnvironmentKeys);

internal sealed record GatewayMigrationResultDto(
    GatewayRouteDto Route,
    string? BackupID,
    string PathDisplay,
    StateDto State);

internal sealed record ToggleResultDto(
    string ServerName,
    string ClientID,
    bool Enabled,
    bool RequiresRestart,
    /// True when switching off had to remove the definition because the client has
    /// no disabled state. The UI says so, because silently deleting a user's
    /// configuration is exactly the surprise §6 exists to prevent.
    bool WasParked,
    string? BackupID,
    string PathDisplay,
    StateDto State);

internal sealed record PlaceholderDto(string Token, string Label, string Example);

internal sealed record EnvRequirementDto(string Key, bool Required, string Hint);

internal sealed record CatalogEntryDto(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    string Transport,
    string Command,
    IReadOnlyList<string> Args,
    string? Url,
    IReadOnlyList<PlaceholderDto> Placeholders,
    IReadOnlyList<EnvRequirementDto> Env,
    string Requires,
    string Homepage);

internal sealed record AuthoringResultDto(
    string ServerName,
    IReadOnlyList<string> Changed,
    bool RequiresRestart,
    StateDto State);

/// <summary>
/// The settings the web layer actually needs. The rest — login item, tray item,
/// path overrides — are native concerns and stay native (§3.2).
/// </summary>
internal sealed record SettingsDto(
    string Theme,
    int TokenWarningThreshold,
    bool HasCompletedOnboarding,
    int BackupRetention,
    bool HasConfirmedMatrixWrites,
    bool ShowsCustomSources,
    int SidebarWidth);

internal sealed record SecretUsageDto(
    string ServerID,
    string ServerName,
    string ClientID,
    string PathDisplay);

internal sealed record SecretExposureDto(string Kind, string PathDisplay, string Detail);

internal sealed record SecretRecordDto(
    string Id,
    string Key,
    /// The only form of the value that crosses this boundary by default (§6).
    string MaskedValue,
    IReadOnlyList<SecretUsageDto> Usages,
    bool IsInSecretStore,
    bool IsShared,
    IReadOnlyList<SecretExposureDto> Exposure);

internal sealed record RotationResultDto(
    string Key,
    int UpdatedCount,
    IReadOnlyList<string> Clients,
    StateDto State);

internal sealed record BackupDto(
    string Id,
    string ClientID,
    string PathDisplay,
    /// Seconds since the epoch; the UI formats it in the user's locale.
    double TakenAt,
    long ByteCount);

internal sealed record HealthCheckResultDto(HealthDto Health, StateDto State);

internal sealed record HealthProgressDto(string ServerName, int Index, int Total);

internal sealed record ContextRecommendationDto(
    string Kind,
    string Severity,
    string Summary,
    IReadOnlyList<string> ServerIDs);

internal sealed record ContextProfileAnalysisDto(
    int EstimatedTokens,
    int MeasuredServerCount,
    int TotalServerCount,
    IReadOnlyList<ContextRecommendationDto> Recommendations);

internal sealed record ProfileDto(
    string Id,
    string Name,
    IReadOnlyList<string> ServerIDs,
    int? TokenBudget,
    ContextProfileAnalysisDto Analysis);

internal sealed record DoctorFindingDto(
    string Code,
    string Severity,
    string Title,
    string Detail,
    string Remediation,
    string? Action);

internal sealed record DoctorServerReportDto(
    string ServerID,
    string ServerName,
    IReadOnlyList<DoctorFindingDto> Findings);

internal sealed record ContractChangeDto(
    string Kind,
    string Severity,
    string ToolName,
    string Summary);

internal sealed record ContractAlertDto(
    string ServerID,
    string ServerName,
    double ChangedAt,
    IReadOnlyList<ContractChangeDto> Changes);

internal sealed record DoctorFixPreviewDto(
    string ServerID,
    string ServerName,
    string CurrentCommand,
    string ReplacementCommand,
    IReadOnlyList<string> ClientIDs);

internal sealed record UnifyPreviewDto(
    string ServerID,
    string ServerName,
    string SourceClientID,
    IReadOnlyList<string> TargetClientIDs,
    IReadOnlyList<string> SkippedClientIDs,
    string CommandSummary,
    string? Url,
    string Transport,
    /// Key names only — the whole point of composing the definition natively is that
    /// no value has to make this trip (§6).
    IReadOnlyList<string> EnvironmentKeys);

/// <summary>
/// Unifying can also update a copy that is not in any file — a server switched off
/// in a presence-only client, whose bytes Kytto is holding. That is a real outcome
/// and a different one from a config write, so it is reported separately rather
/// than folded into <c>changed</c>.
/// </summary>
internal sealed record UnifyResultDto(
    string ServerName,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> ParkedUpdated,
    IReadOnlyList<string> ParkedFailures,
    bool RequiresRestart,
    StateDto State);

internal sealed record GatewayEventDto(
    string EventID,
    string SessionID,
    double Timestamp,
    string Kind,
    string ServerID,
    string ClientID,
    string? ToolName,
    int? DurationMilliseconds,
    bool? Succeeded,
    int? ErrorCode,
    int? ExitCode);

internal sealed record GatewayActivityDto(
    IReadOnlyList<GatewayEventDto> Events,
    int TotalSessions,
    int CompletedCalls,
    int FailedCalls,
    int? AverageDurationMilliseconds);

internal sealed record ProfileFailureDto(string ServerName, string Message);

internal sealed record ProfileApplyResultDto(
    string ProfileName,
    string ClientID,
    int EnabledCount,
    int DisabledCount,
    bool RequiresRestart,
    IReadOnlyList<ProfileFailureDto> Failures,
    StateDto State);
