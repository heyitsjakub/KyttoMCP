using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.App;

/// <summary>
/// Definition sources, project-definition imports and launch reviews (roadmap §12, §22).
/// </summary>
/// <remarks>
/// The plan for an import stays here between the preview and the confirmation: only
/// its review and a single-use token reach the page, and the confirmation rebuilds the
/// plan from what the files say now before anything is written.
/// </remarks>
internal sealed partial class AppModel
{
    private ProjectDefinitionCopy? _pendingProjectCopy;

    internal LaunchReviews LaunchReviews => _launchReviews;

    /// <summary>Every file-backed copy of each server, by server id.</summary>
    internal IReadOnlyDictionary<string, DefinitionInventory> DefinitionInventories() =>
        DefinitionInventory.Inventories(_current, Settings.ProjectFolders);

    /// <summary>Reviews one repository definition for one writable client. Writes nothing.</summary>
    /// <remarks>Opening a preview discards any earlier one: only the latest token can be applied.</remarks>
    internal ProjectCopyPreview PreviewProjectCopy(string serverId, ClientKey source, ClientId target)
    {
        _pendingProjectCopy = null;
        var plan = ProjectCopy(serverId, source, target);
        if (plan.Preview.Token is not null) _pendingProjectCopy = plan;
        return plan.Preview;
    }

    /// <summary>Creates the reviewed definition through normal authoring.</summary>
    /// <exception cref="ProjectCopyException">
    /// The token is not the latest preview's, or the source, destination or selection
    /// changed since; nothing is written.
    /// </exception>
    internal AuthoringResult ApplyProjectCopy(string token)
    {
        if (_pendingProjectCopy is not { } plan || plan.Preview.Token != token)
        {
            throw ProjectCopyException.Stale();
        }
        _pendingProjectCopy = null;
        var current = ProjectCopy(plan.ServerID, plan.Source, plan.Target);
        if (!plan.Matches(current)) throw ProjectCopyException.Stale();
        return CreateServer(current.Draft, [plan.Target]);
    }

    /// <summary>Acknowledges exactly the review shown. Changes Kytto's record only.</summary>
    internal void AcknowledgeLaunchReview(string token) => _launchReviews.Acknowledge(token);

    private ProjectDefinitionCopy ProjectCopy(string serverId, ClientKey source, ClientId target)
    {
        var server = _current.Servers.FirstOrDefault(candidate => candidate.Id == serverId)
            ?? throw ProjectCopyException.Unavailable();
        return new ProjectDefinitionCopy(server, source, target, _current, _home, Settings, _ledger);
    }
}
