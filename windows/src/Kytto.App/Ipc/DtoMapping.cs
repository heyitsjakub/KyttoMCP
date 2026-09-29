using Kytto.Core.Clients;
using Kytto.Core;
using Kytto.Core.Doctor;
using Kytto.Core.Health;
using Kytto.Core.Gateway;
using Kytto.Core.Model;
using Kytto.Core.Profiles;
using Kytto.Core.Settings;
using Kytto.Core.Updates;

namespace Kytto.App.Ipc;

/// <summary>
/// Turns the core model into what crosses the boundary.
/// </summary>
/// <remarks>
/// The two rules <c>Dto.cs</c> states are enforced here rather than hoped for at
/// every call site: no secret value is copied across, and no path is sent that the
/// web layer would have to interpret.
/// </remarks>
internal static class DtoMapping
{
    internal static ClientDto ToDto(this DiscoveredClient client, ClientToolBudget toolBudget) => new(
        Id: client.Id.Raw(),
        DisplayName: client.DisplayName,
        ShortName: client.ShortName,
        IconAsset: client.IconAsset,
        State: (client.IsReadOnly, client.IsInstalled, client.ConfigExists) switch
        {
            (_, true, true) => "ready",
            (true, _, false) => "noConfig",
            (_, true, false) => "noConfig",
            // A config that outlived its client. Shown rather than hidden.
            (_, false, true) => "orphanedConfig",
            _ => "notInstalled",
        },
        ConfigPathDisplay: client.ConfigPathDisplay,
        ConfigFormatDisplay: client.ConfigFormatDisplay,
        OffSwitchSummary: client.OffSwitchSummary,
        ServerCount: client.ServerCount,
        SchemaQuirks: client.SchemaQuirks,
        IsReadOnly: client.IsReadOnly,
        ConfigurationScope: client.ConfigurationScope.Raw(),
        ScopeLabel: string.IsNullOrWhiteSpace(client.ScopeLabel) ? null : client.ScopeLabel,
        ToolBudget: toolBudget.ToDto());

    internal static ClientToolBudgetDto ToDto(this ClientToolBudget budget) => new(
        ToolCount: budget.ToolCount,
        MaskedToolCount: budget.MaskedToolCount,
        UnmeasuredServerIDs: budget.UnmeasuredServerIDs,
        Limit: budget.Limit?.MaxTools,
        State: budget.State?.Raw(),
        PastLimitSummary: budget.Limit?.PastLimitSummary);

    /// <summary>
    /// Every client with its tool count against its cap. The route store is read
    /// once here rather than once per client.
    /// </summary>
    internal static ClientDto[] ClientDtos(this AppModel model, DiscoveryResult current)
    {
        var routes = model.GatewayRoutes();
        return current.Clients
            .Select(client => client.ToDto(AppModel.ToolBudgetFor(client, current, routes)))
            .ToArray();
    }

    internal static ServerDto ToDto(this Server server) => new(
        Id: server.Id,
        Name: server.Name,
        Transport: server.Transport.Raw(),
        CommandSummary: server.CommandSummary,
        Command: server.Command,
        Args: server.Args,
        // Keys and a flag. The value stays in C# until it is injected at spawn
        // time (§6) — this is the line it must never cross.
        Env: server.Env.Select(entry => new EnvEntryDto(entry.Key, entry.HasValue)).ToArray(),
        Url: server.Url,
        EnabledIn: server.EnabledIn.ToDictionary(
            pair => pair.Key.Raw(),
            pair => pair.Value.Raw()),
        OriginKind: server.Origin.Kind,
        IsBundled: server.IsBundled,
        HasRelativePath: server.HasRelativePath,
        Health: server.Health?.ToDto(),
        TokenWeight: server.TokenWeight?.ToDto(),
        Provenance: server.Provenance.ToDto());

    internal static ProvenanceDto ToDto(this ServerProvenance provenance) => new(
        SourceKind: provenance.SourceKind,
        PackageName: provenance.PackageName,
        SourceURL: provenance.SourceURL,
        InstalledVersion: provenance.InstalledVersion,
        LatestVersion: provenance.LatestVersion,
        LatestCheckedAt: provenance.LatestCheckedAt?.ToUnixTimeMilliseconds() / 1000.0,
        Confidence: provenance.Confidence,
        MaintenanceState: provenance.MaintenanceState);

    internal static HealthDto ToDto(this HealthResult health) => new(
        Status: health.StatusRaw,
        CheckedAt: health.CheckedAt.ToUnixTimeMilliseconds() / 1000.0,
        ToolCount: health.ToolCount,
        Tools: health.Tools.Select(tool => new ToolSummaryDto(
            tool.Name,
            tool.Description,
            tool.InputSchemaJSON ?? "{}",
            new ToolAnnotationsDto(
                tool.Annotations?.ReadOnlyHint,
                tool.Annotations?.DestructiveHint,
                tool.Annotations?.IdempotentHint,
                tool.Annotations?.OpenWorldHint),
            tool.TokenCount)).ToArray(),
        Message: health.CurrentMessage,
        Stderr: health.Stderr,
        DurationSeconds: health.DurationSeconds,
        ServerName: health.ServerName,
        ServerVersion: health.ServerVersion,
        ProtocolVersion: health.ProtocolVersion,
        CapabilityNames: health.CapabilityNames ?? [],
        PromptCount: health.PromptCount,
        ResourceCount: health.ResourceCount,
        InspectionNotes: health.InspectionNotes ?? [],
        ResolvedCommand: health.ResolvedCommand,
        EnvironmentSource: health.EnvironmentSource,
        AuthorizationURL: health.AuthorizationURL);

    internal static TokenWeightDto ToDto(this TokenWeight weight) => new(
        Estimate: weight.Estimate,
        Method: weight.Method,
        IsMeasured: weight.IsMeasured,
        MeasuredAt: weight.MeasuredAt.ToUnixTimeMilliseconds() / 1000.0,
        PercentOfContext: weight.PercentOfContext,
        ReferenceContextWindow: TokenWeight.ReferenceContextWindow);

    internal static DiagnosticDto ToDto(this Diagnostic diagnostic) => new(
        Severity: diagnostic.SeverityRaw,
        ClientID: diagnostic.ClientID?.Raw(),
        PathDisplay: diagnostic.PathDisplay,
        Message: diagnostic.Message);

    internal static GatewayRouteDto ToDto(this GatewayRoute route) => new(
        Id: route.Id,
        ServerID: route.ServerID,
        ServerName: route.ServerName,
        ClientID: route.ClientID.Raw(),
        CreatedAt: route.CreatedAt.ToUnixTimeMilliseconds() / 1000.0,
        ExposedTools: route.ExposedTools);

    internal static GatewayMigrationPreviewDto ToDto(this GatewayMigrationPreview preview) => new(
        RouteID: preview.RouteID,
        ServerID: preview.ServerID,
        ServerName: preview.ServerName,
        ClientID: preview.ClientID.Raw(),
        PathDisplay: preview.PathDisplay,
        FormatDisplay: preview.FormatDisplay,
        DirectDefinitionPreview: preview.DirectDefinitionPreview,
        GatewayDefinitionPreview: preview.GatewayDefinitionPreview,
        EnvironmentKeys: preview.EnvironmentKeys);

    internal static DoctorServerReportDto ToDto(this DoctorServerReport report) => new(
        report.ServerID,
        report.ServerName,
        report.Findings.Select(finding => new DoctorFindingDto(
            finding.Code,
            Raw(finding.Severity),
            finding.Title,
            finding.Detail,
            finding.Remediation,
            finding.Action is { } action ? Raw(action) : null)).ToArray());

    internal static ContractAlertDto ToDto(this ContractAlert alert) => new(
        alert.ServerID,
        alert.ServerName,
        alert.ChangedAt.ToUnixTimeMilliseconds() / 1000.0,
        alert.Changes.Select(change => new ContractChangeDto(
            Raw(change.Kind),
            Raw(change.Severity),
            change.ToolName,
            change.Summary)).ToArray());

    internal static DoctorFixPreviewDto ToDto(this DoctorFixPreview preview) => new(
        preview.ServerID,
        preview.ServerName,
        preview.CurrentCommand,
        preview.ReplacementCommand,
        preview.ClientIDs.Select(client => client.Raw()).ToArray(),
        Raw(preview.Action),
        preview.PackageName,
        preview.Version,
        preview.ArgumentChanges
            .Select(change => new DoctorArgumentChangeDto(
                change.ClientID.Raw(),
                change.Current,
                change.Replacement))
            .ToArray());

    internal static UnifyPreviewDto ToDto(this UnifyPreview preview) => new(
        ServerID: preview.ServerID,
        ServerName: preview.ServerName,
        SourceClientID: preview.SourceClientID.Raw(),
        TargetClientIDs: preview.TargetClientIDs.Select(client => client.Raw()).ToArray(),
        SkippedClientIDs: preview.SkippedClientIDs.Select(client => client.Raw()).ToArray(),
        CommandSummary: preview.CommandSummary,
        Url: preview.Url,
        Transport: preview.Transport.Raw(),
        EnvironmentKeys: preview.EnvironmentKeys);

    internal static GatewayActivityDto ToDto(this GatewayActivitySummary summary) => new(
        summary.Events.Select(item => new GatewayEventDto(
            item.EventID,
            item.SessionID,
            item.Timestamp.ToUnixTimeMilliseconds() / 1000.0,
            Raw(item.Kind),
            item.ServerID,
            item.ClientID,
            item.ToolName,
            item.DurationMilliseconds,
            item.Succeeded,
            item.ErrorCode,
            item.ExitCode)).ToArray(),
        summary.TotalSessions,
        summary.CompletedCalls,
        summary.FailedCalls,
        summary.AverageDurationMilliseconds);

    internal static ProfileDto ToDto(this Profile profile, IReadOnlyList<Server> servers)
    {
        var analysis = ContextOptimizer.Analyze(profile, servers);
        return new ProfileDto(
            profile.Id,
            profile.Name,
            profile.ServerIDs,
            profile.TokenBudget,
            new ContextProfileAnalysisDto(
                analysis.EstimatedTokens,
                analysis.MeasuredServerCount,
                analysis.TotalServerCount,
                analysis.Recommendations.Select(item => new ContextRecommendationDto(
                    Raw(item.Kind),
                    Raw(item.Severity),
                    item.Summary,
                    item.ServerIDs)).ToArray()));
    }

    internal static SettingsDto ToDto(this KyttoSettings settings) => new(
        Theme: settings.ThemeRaw,
        TokenWarningThreshold: settings.TokenWarningThreshold,
        HasCompletedOnboarding: settings.HasCompletedOnboarding,
        BackupRetention: settings.BackupRetention,
        HasConfirmedMatrixWrites: settings.HasConfirmedMatrixWrites,
        ShowsCustomSources: settings.ShowsCustomSources,
        SidebarWidth: settings.SidebarWidth);

    internal static UpdateCheckDto ToDto(this UpdateCheckResult result) => new(
        Status: result.Status switch
        {
            UpdateStatus.UpdateAvailable => "updateAvailable",
            UpdateStatus.UpToDate => "upToDate",
            _ => "skipped",
        },
        CurrentVersion: result.CurrentVersion,
        LatestVersion: result.Release?.Version,
        CheckedAt: result.Release?.CheckedAt.ToUnixTimeMilliseconds() / 1000.0,
        ReleaseNotesURL: result.Release?.ReleaseNotesURL,
        Sha256: result.Release?.Sha256);

    /// <summary>
    /// Everything the matrix needs in one payload, so a write can update the whole
    /// screen from a single reply instead of firing four follow-up commands.
    /// </summary>
    internal static StateDto ToStateDto(this AppModel model)
    {
        var current = model.Current();
        return new StateDto(
            Clients: model.ClientDtos(current),
            Servers: current.Servers.Select(ToDto).ToArray(),
            Diagnostics: current.Diagnostics.Select(ToDto).ToArray(),
            PendingRestarts: model.PendingRestarts.ToDictionary(
                pair => pair.Key.Raw(),
                pair => pair.Value),
            GatewayRoutes: model.GatewayRoutes().Select(ToDto).ToArray(),
            DoctorReports: model.DoctorReports().Select(ToDto).ToArray(),
            ContractAlerts: model.ContractAlerts().Select(ToDto).ToArray(),
            Drift: ServerDrift.Detect(current.Servers).Select(ToDto).ToArray(),
            DismissedRestarts: model.DismissedRestarts
                .Select(client => client.Raw())
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    internal static ServerDriftDto ToDto(this ServerDrift drift) => new(
        ServerID: drift.ServerID,
        ServerName: drift.ServerName,
        Fields: drift.Fields.Select(field => field.Raw()).ToArray(),
        Variants: drift.Variants.Select(variant =>
        {
            var definition = variant.Definition;
            var command = string.Join(
                ' ',
                (definition.Command is null ? [] : new[] { definition.Command }).Concat(definition.Args));
            return new DriftVariantDto(
                ClientIDs: variant.ClientIDs.Select(id => id.Raw()).ToArray(),
                CommandSummary: definition.Url ?? command,
                Transport: definition.Transport.Raw(),
                Url: definition.Url,
                EnvironmentKeys: definition.EnvironmentKeys,
                IsBundled: definition.IsBundled,
                CanBeSource: variant.CanBeSource);
        }).ToArray(),
        UnwritableClientIDs: drift.UnwritableClientIDs.Select(id => id.Raw()).ToArray());

    private static string Raw(Enum value) => value.ToString() switch
    {
        { Length: 0 } => "",
        var text => char.ToLowerInvariant(text[0]) + text[1..],
    };

    private static string Raw(GatewayEventKind value) => value switch
    {
        GatewayEventKind.SessionStarted => "session.started",
        GatewayEventKind.ToolCallStarted => "tool.call.started",
        GatewayEventKind.ToolCallCompleted => "tool.call.completed",
        _ => "session.ended",
    };
}
