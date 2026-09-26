using Kytto.Core.Clients;

namespace Kytto.Core.Profiles;

public sealed record ProfileApplyFailure(string ServerName, string Message);

public sealed record ProfileApplyResult(
    string ProfileName,
    ClientId ClientID,
    int EnabledCount,
    int DisabledCount,
    bool RequiresRestart,
    IReadOnlyList<ProfileApplyFailure> Failures);
