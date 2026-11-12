using Kytto.Core.Clients;

namespace Kytto.Core.Profiles;

public sealed record ProfileApplyFailure(string ServerName, string Message);

public sealed record ProfileApplyResult(
    string ProfileName,
    ClientId ClientID,
    int EnabledCount,
    int DisabledCount,
    bool RequiresRestart,
    IReadOnlyList<ProfileApplyFailure> Failures)
{
    /// <summary>The switches that were actually made, in order, so they can be undone.</summary>
    public IReadOnlyList<ProfileAppliedChange> Applied { get; init; } = [];
}

/// <summary>One switch a profile apply made: the server and the state it was put in.</summary>
public sealed record ProfileAppliedChange(string ServerID, bool Enabled);

public sealed record ProfileRevertResult(
    ClientId ClientID,
    int RevertedCount,
    IReadOnlyList<ProfileApplyFailure> Failures);
