using Kytto.Core.Doctor;
using Kytto.Core.Gateway;
using Kytto.Core.Health;
using Kytto.Core.Model;

namespace Kytto.Core.Clients;

/// <summary>A cap a client puts on how many tools it hands the model at once.</summary>
/// <remarks>
/// Data on the descriptor, next to the config path and the schema quirks, for the
/// same reason those are: the number moves between client releases, and a fix must
/// stay a one-file change in <c>ClientRegistry</c> (§4). A client with no
/// documented cap has no value here — Kytto does not guess one, because a warning
/// that fires on a limit the client does not enforce teaches people to ignore the
/// warning.
/// </remarks>
/// <param name="MaxTools">The most tools the client will pass on.</param>
/// <param name="PastLimitSummary">
/// What the client does with a request past the cap, in one sentence. It differs
/// per client — one drops the overflow silently, another refuses the request — so
/// it is written down with the client and not in the UI.
/// </param>
public sealed record ClientToolLimit(int MaxTools, string PastLimitSummary);

/// <summary>How close a client is to its tool cap.</summary>
public enum ToolLimitState
{
    Ok,

    /// <summary>At or above <see cref="ToolBudget.NearFraction"/> of the cap.</summary>
    Near,

    /// <summary>Past the cap: the client is already dropping or refusing tools.</summary>
    Over,
}

public static class ToolLimitStates
{
    /// <summary>The wire spelling, fixed so a C# rename cannot change the protocol.</summary>
    public static string Raw(this ToolLimitState state) => state switch
    {
        ToolLimitState.Near => "near",
        ToolLimitState.Over => "over",
        _ => "ok",
    };
}

/// <summary>One client's tool count, set against its cap.</summary>
/// <param name="ToolCount">
/// Tools the client will be offered by servers switched on in it, as last
/// measured, after Gateway masking. A lower bound when anything switched on has
/// not been measured.
/// </param>
/// <param name="MaskedToolCount">
/// Tools the measured servers have that Gateway masking keeps from this client.
/// Already subtracted from <paramref name="ToolCount"/>.
/// </param>
/// <param name="UnmeasuredServerIDs">
/// Servers switched on in this client with no tool list to count — never checked,
/// failed, remote, or waiting for sign-in.
/// </param>
/// <param name="State">Null when the client has no known cap.</param>
public sealed record ClientToolBudget(
    ClientKey ClientID,
    int ToolCount,
    int MaskedToolCount,
    IReadOnlyList<string> UnmeasuredServerIDs,
    ClientToolLimit? Limit,
    ToolLimitState? State)
{
    public bool IsLowerBound => UnmeasuredServerIDs.Count > 0;

    /// <summary>
    /// The same scale the Doctor and the context optimizer speak, so a surface that
    /// sorts findings can sort this one with them. Near is worth knowing; over is
    /// already costing the user tools.
    /// </summary>
    public DoctorSeverity? Severity => State switch
    {
        ToolLimitState.Over => DoctorSeverity.Warning,
        ToolLimitState.Near => DoctorSeverity.Info,
        _ => null,
    };
}

public static class ToolBudget
{
    /// <summary>
    /// "Near" starts at 80 % of the cap — close enough that the next server switched
    /// on is likely to cross it.
    /// </summary>
    public const double NearFraction = 0.8;

    /// <summary>Counts what a client hands its model.</summary>
    /// <remarks>
    /// Pure: servers carry their last health check, routes carry the Gateway allow
    /// lists, and nothing is spawned or read. Only servers <c>enabled</c> in the
    /// client count — <c>disabled</c> and <c>absent</c> offer the model nothing.
    /// </remarks>
    public static ClientToolBudget Evaluate(
        ClientKey clientID,
        ClientToolLimit? limit,
        IReadOnlyList<Server> servers,
        IReadOnlyList<GatewayRoute> routes)
    {
        var count = 0;
        var masked = 0;
        var unmeasured = new List<string>();

        foreach (var server in servers)
        {
            if (!server.EnabledIn.TryGetValue(clientID, out var enablement)
                || enablement != Enablement.Enabled)
            {
                continue;
            }
            if (MeasuredToolCount(server) is not { } offered)
            {
                unmeasured.Add(server.Id);
                continue;
            }
            // Routes exist only for built-in clients; a custom source never has one.
            var route = routes.FirstOrDefault(candidate =>
                candidate.ServerID == server.Id && clientID.BuiltIn == candidate.ClientID);
            var visible = route?.ExposedTools is { } exposed
                ? ExposedCount(exposed, server, offered)
                : offered;
            count += visible;
            masked += offered - visible;
        }

        unmeasured.Sort(StringComparer.Ordinal);
        return new ClientToolBudget(
            ClientID: clientID,
            ToolCount: count,
            MaskedToolCount: masked,
            UnmeasuredServerIDs: unmeasured,
            Limit: limit,
            State: limit is null ? null : (ToolLimitState?)StateFor(count, limit.MaxTools));
    }

    internal static ToolLimitState StateFor(int count, int limit)
    {
        if (count > limit) return ToolLimitState.Over;
        if (count >= limit * NearFraction) return ToolLimitState.Near;
        return ToolLimitState.Ok;
    }

    /// <summary>
    /// The tool count from a passing check, or null when there is nothing to count.
    /// </summary>
    /// <remarks>
    /// <c>ToolCount</c> rather than <c>Tools.Count</c>: records written before the
    /// per-tool breakdown carry a count with a partial list.
    /// </remarks>
    private static int? MeasuredToolCount(Server server)
    {
        if (server.Health is not { Status: HealthStatus.Passed } health) return null;
        return health.ToolCount ?? health.Tools.Count;
    }

    /// <summary>What survives an allow list (§7.11).</summary>
    /// <remarks>
    /// The filter passes a tool only if the server offers it and the list names it,
    /// so with a complete tool list the answer is the intersection. With a partial
    /// one — an older record — the names cannot be checked, and the list's own
    /// length, capped at what the server offers, is the best figure there is.
    /// </remarks>
    private static int ExposedCount(IReadOnlyList<string> exposed, Server server, int offered)
    {
        var names = new HashSet<string>(
            (server.Health?.Tools ?? []).Select(tool => tool.Name),
            StringComparer.Ordinal);
        var allowed = new HashSet<string>(exposed, StringComparer.Ordinal);
        if (names.Count >= offered)
        {
            allowed.IntersectWith(names);
            return allowed.Count;
        }
        return Math.Min(allowed.Count, offered);
    }
}
