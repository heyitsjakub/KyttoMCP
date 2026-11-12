using System.Globalization;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;

namespace Kytto.Core.Doctor;

/// <summary>Doctor findings for servers in Gateway mode.</summary>
/// <remarks>
/// A client reports every one of these the same way — the server "failed" — because
/// all it sees is a helper that exited. Kytto is the only place that knows why, so it
/// has to say: the helper is gone, the route is gone, the server keeps stopping, or it
/// never started.
/// </remarks>
public static class GatewayDoctor
{
    /// <summary>How far back session failures are still worth a finding.</summary>
    /// <remarks>A crash last week that has not come back is history, not something to act on.</remarks>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(24);

    /// <summary>Findings by server id.</summary>
    /// <param name="diagnoses">What reading the configs found (<see cref="GatewayMigrationService.Diagnose"/>).</param>
    /// <param name="events">The journal, already limited to servers that still have a route.</param>
    /// <param name="routes">The known routes, so a finding can name the one to act on.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<DoctorFinding>> Findings(
        IReadOnlyList<GatewayRouteDiagnosis> diagnoses,
        IReadOnlyList<GatewayEvent> events,
        IReadOnlyList<GatewayRoute>? routes = null,
        DateTimeOffset? now = null)
    {
        var result = new Dictionary<string, List<DoctorFinding>>(StringComparer.Ordinal);
        void Add(string serverId, DoctorFinding finding)
        {
            if (!result.TryGetValue(serverId, out var list)) result[serverId] = list = [];
            list.Add(finding);
        }

        foreach (var diagnosis in diagnoses)
        {
            var client = ClientName(diagnosis.ClientID.Raw());
            switch (diagnosis.Problem)
            {
                case GatewayRouteProblem.HelperMissing:
                    Add(diagnosis.ServerID, new DoctorFinding(
                        "gateway-helper-missing",
                        DoctorSeverity.Error,
                        $"Gateway helper is missing in {client}",
                        $"{client} starts this server through Kytto's helper at {diagnosis.HelperPath}, and nothing is there. " +
                        $"Kytto was probably moved, reinstalled in another folder or removed after Gateway mode was turned on, " +
                        $"so {client} cannot start the server.",
                        diagnosis.Repairable
                            ? "Repair points the definition at this copy of Kytto. Nothing else in the config changes."
                            : "Install or extract Kytto to a permanent folder and open it from there, then repair the route here.",
                        diagnosis.Repairable ? DoctorAction.RepairGatewayRoute : null,
                        diagnosis.RouteID));
                    break;
                case GatewayRouteProblem.UnknownRoute:
                    Add(diagnosis.ServerID, new DoctorFinding(
                        "gateway-route-unknown",
                        DoctorSeverity.Error,
                        $"Gateway route no longer exists in {client}",
                        $"The definition in {client} points at a Gateway route Kytto has no record of, so the helper has " +
                        "nothing to start. This happens when Kytto's data folder was reset or the config was copied from another PC.",
                        "Restore a backup from before Gateway mode was turned on, or edit the server and enter its command again."));
                    break;
            }
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow) - FailureWindow;
        var recent = events.Where(item => item.Timestamp >= cutoff).ToArray();
        var failures = recent.Where(GatewayActivitySummary.IsServerFailure);
        foreach (var group in failures.GroupBy(item => (item.ServerID, item.ClientID)))
        {
            var latest = group.MaxBy(item => item.Timestamp)!;
            // A session that started cleanly after the last failure and is not itself
            // among the failures means the server recovered.
            var failedSessions = group.Select(item => item.SessionID).ToHashSet(StringComparer.Ordinal);
            var recovered = recent.Any(item =>
                item.Kind == GatewayEventKind.SessionStarted &&
                item.ServerID == latest.ServerID && item.ClientID == latest.ClientID &&
                item.Timestamp > latest.Timestamp && !failedSessions.Contains(item.SessionID));
            if (recovered) continue;
            var route = routes?.FirstOrDefault(candidate =>
                candidate.ServerID == latest.ServerID && candidate.ClientID.Raw() == latest.ClientID);

            var client = ClientName(latest.ClientID);
            var time = latest.Timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            DoctorFinding finding;
            if (latest.Kind == GatewayEventKind.SessionFailed)
            {
                var secret = latest.Reason == GatewayFailureReason.SecretUnavailable;
                finding = new DoctorFinding(
                    secret ? "gateway-secret-unavailable" : "gateway-launch-failed",
                    DoctorSeverity.Error,
                    $"Server did not start in {client}",
                    secret
                        ? $"At {time} {client} asked for this server and the helper could not read one of its stored secrets from Credential Manager."
                        : $"At {time} {client} asked for this server and the helper could not start its command.",
                    secret
                        ? "Restore Direct mode: Kytto puts the definition back and names any value it could not recover, so you can enter it again."
                        : "Run a health check to see the error. If the command was removed, reinstall it or restore Direct mode and edit the server.",
                    secret ? (route is null ? null : DoctorAction.RestoreDirectMode) : DoctorAction.RunHealthCheck,
                    route?.Id);
            }
            else
            {
                var count = group.Count(item => item.Kind == GatewayEventKind.SessionEnded);
                var exit = latest.ExitCode is { } code ? $", with exit code {code}" : "";
                finding = new DoctorFinding(
                    "gateway-server-stopped",
                    DoctorSeverity.Warning,
                    $"Server stopped on its own in {client}",
                    $"The server exited {(count == 1 ? "once" : $"{count} times")} in the last 24 hours while {client} " +
                    $"was still connected — most recently at {time}{exit}. {client} shows this only as a failed server.",
                    $"Run a health check to see whether it starts now and what it prints. Restart {client} to reconnect.",
                    DoctorAction.RunHealthCheck);
            }
            Add(latest.ServerID, finding);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<DoctorFinding>)pair.Value,
            StringComparer.Ordinal);
    }

    private static string ClientName(string id) =>
        ClientRegistry.All.FirstOrDefault(descriptor => descriptor.Id.Raw() == id)?.DisplayName ?? id;
}
