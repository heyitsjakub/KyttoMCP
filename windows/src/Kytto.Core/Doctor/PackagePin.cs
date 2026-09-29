using Kytto.Core.Clients;
using Kytto.Core.Model;

namespace Kytto.Core.Doctor;

/// <summary>Pinning a runner-launched package to one exact release (§7.10).</summary>
/// <remarks>
/// <c>npx -y name</c> and <c>uvx name</c> fetch whatever release is newest each
/// time a client starts the server, so a new release — including a compromised one
/// — runs with the user's environment without anyone choosing it. The repair
/// rewrites the one argument that names the package; it never guesses which
/// version: the caller supplies one the user looked up, and a version that is not
/// a single exact release for that ecosystem produces no edit at all.
/// </remarks>
public static class PackagePin
{
    /// <summary>
    /// How long a registry lookup the user asked for stays good enough to pin to.
    /// </summary>
    /// <remarks>
    /// The same window the app's lookup cache keeps a result for. Lookups are
    /// remembered across launches here, so "the version you just checked" needs a
    /// bound in time rather than in session; past it the Doctor asks again.
    /// </remarks>
    public static readonly TimeSpan LookupFreshness = TimeSpan.FromHours(24);

    /// <summary>
    /// The launch MCP Doctor reports for this server: the merged definition's, or
    /// else the first client copy that has one. Null when nothing launches a
    /// registry package or every copy is already pinned.
    /// </summary>
    public static PackageLaunch? UnpinnedLaunch(Server server)
    {
        if (server.Transport != Transport.Stdio) return null;
        return new[] { PackageLaunch.Parse(server.Command, server.Args) }
            .Concat(Copies(server).Select(copy => PackageLaunch.Parse(copy.Definition.Command, copy.Definition.Args)))
            .OfType<PackageLaunch>()
            .FirstOrDefault(launch => !launch.IsPinned);
    }

    /// <summary>
    /// Whether the repair can be offered: some editable copy is unpinned and its
    /// runner accepts a version in the argument that is already there.
    /// </summary>
    public static bool CanPin(Server server)
    {
        if (UnpinnedLaunch(server) is not { } launch) return false;
        return Copies(server).Any(copy =>
            IsEditable(copy.Definition) &&
            PackageLaunch.Parse(copy.Definition.Command, copy.Definition.Args) is { } own &&
            SamePackage(own, launch) &&
            !own.IsPinned &&
            own.CanPinInPlace);
    }

    /// <summary>
    /// One argument edit per client copy that launches the same package unpinned.
    /// </summary>
    /// <remarks>
    /// Copies already pinned, launching something else, bundled, read-only or
    /// unable to take a version in place are left out; an empty result means there
    /// is nothing safe to write. Each copy is edited at its own position, because
    /// two clients can spell the same launch differently.
    /// </remarks>
    public static IReadOnlyDictionary<ClientId, ArgumentEdit> Edits(Server server, string version)
    {
        var result = new Dictionary<ClientId, ArgumentEdit>();
        if (UnpinnedLaunch(server) is not { } launch) return result;

        foreach (var (clientId, definition) in Copies(server))
        {
            if (!IsEditable(definition) ||
                PackageLaunch.Parse(definition.Command, definition.Args) is not { } own ||
                !SamePackage(own, launch) ||
                own.IsPinned ||
                own.PinnedArgument(version) is not { } replacement) continue;
            result[clientId] = new ArgumentEdit(own.ArgumentIndex, own.Argument, replacement);
        }
        return result;
    }

    private static bool SamePackage(PackageLaunch left, PackageLaunch right) =>
        left.Ecosystem == right.Ecosystem &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private static bool IsEditable(ClientDefinition definition) =>
        !definition.IsBundled && !definition.IsReadOnly;

    /// <summary>
    /// Every built-in client holding this server, with that client's own definition.
    /// </summary>
    /// <remarks>
    /// Custom sources are never write targets (§6 rule 7), so they are not copies
    /// here. Falls back to the merged view for a server built without per-client
    /// evidence.
    /// </remarks>
    private static IReadOnlyList<(ClientId ClientID, ClientDefinition Definition)> Copies(Server server) =>
        server.EnabledIn
            .Where(pair => pair.Value != Enablement.Absent && pair.Key.BuiltIn is not null)
            .Select(pair => pair.Key.BuiltIn!.Value)
            .OrderBy(client => client.Raw(), StringComparer.Ordinal)
            .Select(client => (client, server.DefinitionsByClient.GetValueOrDefault(client) ?? ClientDefinition.From(server)))
            .ToArray();
}
