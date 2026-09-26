using Kytto.Core.Clients;

namespace Kytto.Core;

/// <summary>
/// Turns a registry path into a real one, honouring the user's overrides (§7.6).
/// </summary>
/// <remarks>
/// The override exists because these paths move between client releases, and a
/// user should not have to wait for a Kytto update to point it at the right file.
/// It applies only to a client's editable server map — the one file the user has a
/// reason to relocate. Extension and plugin directories belong to the client that
/// installs into them.
/// </remarks>
public sealed class ClientPathResolver(string home, IReadOnlyDictionary<string, string>? overrides = null)
{
    private readonly IReadOnlyDictionary<string, string> _overrides =
        overrides ?? new Dictionary<string, string>();

    public string Home { get; } = home;

    public string ResolveServerMap(PlatformPath file, ClientId client) =>
        Override(client) is { } path ? Path.GetFullPath(path) : file.Resolve(Home);

    /// <summary>
    /// What the UI shows. An override is shown as the user typed it, because that
    /// is the string they would recognise.
    /// </summary>
    public string DisplayServerMap(PlatformPath file, ClientId client) =>
        Override(client) ?? file.DisplayString(Home);

    public string Resolve(PlatformPath path) => path.Resolve(Home);

    /// <summary>
    /// The file Kytto would write for <paramref name="client"/> if it wrote this
    /// path, or null when the path is not one of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restoring a backup is the one write whose destination comes from a file rather
    /// than from the registry: the sidecar beside a backup records where it was taken
    /// from. Something with write access to Kytto's folder could edit that sidecar,
    /// so the destination is answered here, from the same data the write services
    /// use, rather than believed.
    /// </para>
    /// <para>
    /// <strong>Callers write to the returned path, never to the one they passed in.</strong>
    /// That is what makes the boundary real: normalisation is lexical — on Windows as
    /// on macOS — so <c>~\link\..\.cursor\mcp.json</c> compares equal to the real
    /// config while the kernel follows <c>link</c> somewhere else entirely. Matching
    /// only has to avoid refusing legitimate backups; it is not the thing standing
    /// between a rewritten sidecar and an arbitrary file.
    /// </para>
    /// <para>
    /// Deliberately not restorable: installed extension bundles and Codex plugin
    /// manifests, anything nested deeper in the extension settings folder, read-only
    /// custom sources, and Claude Code's per-project scopes — which are read-only and
    /// whose file is already covered as Claude Code's server map.
    /// </para>
    /// </remarks>
    public string? WriteTarget(string path, ClientId client)
    {
        if (!Path.IsPathFullyQualified(path)) return null;

        string wanted;
        try
        {
            wanted = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        var descriptor = ClientRegistry.Descriptor(client);
        if (descriptor.EditableServerMap is { } map)
        {
            // Both the override in force now and the registry's own spellings: a
            // backup taken before the user pointed the client somewhere else still
            // has a home to go to, and one taken after it still does.
            foreach (var target in map.File.ResolveAll(Home).Prepend(ResolveServerMap(map.File, client)))
            {
                if (Same(target, wanted)) return target;
            }

            // Claude Code keeps enablement in a different file, and Kytto writes it,
            // so a backup of it is a backup of a config Kytto manages.
            if (map.Enablement is EnablementStrategy.DenyList denyList)
            {
                foreach (var target in denyList.File.ResolveAll(Home))
                {
                    if (Same(target, wanted)) return target;
                }
            }
        }

        foreach (var bundles in descriptor.Sources.OfType<ConfigSource.ExtensionBundles>())
        {
            foreach (var directory in bundles.SettingsDirectory.ResolveAll(Home))
            {
                if (ExtensionSettingsFile(directory, wanted) is { } target) return target;
            }
        }
        return null;
    }

    /// <summary>One settings file per extension bundle, directly inside the folder.</summary>
    /// <remarks>
    /// The name is rebuilt onto the resolved directory, so it has to be a plain file
    /// name. <c>x.json:evil</c> ends in <c>.json</c> and its parent folder is the
    /// right one, but it names an alternate data stream, not a settings file.
    /// </remarks>
    private static string? ExtensionSettingsFile(string directory, string wanted)
    {
        if (Path.GetDirectoryName(wanted) is not { } parent || !Same(parent, directory)) return null;

        var name = Path.GetFileName(wanted);
        if (name.Length <= ".json".Length) return null;
        if (name.IndexOfAny([':', '\\', '/']) >= 0) return null;
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return null;

        return Path.Combine(Path.GetFullPath(directory), name);
    }

    /// <summary>
    /// Ordinal and case-insensitive, over paths both sides already normalised.
    /// </summary>
    /// <remarks>
    /// Reparse points, short names and <c>\\?\</c> prefixes are deliberately not
    /// resolved away to force a match. A legitimate sidecar was written from this
    /// resolver's own output, so it is already spelled the same way; anything spelled
    /// differently is refused, and refusing is the safe answer.
    /// </remarks>
    private static bool Same(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private string? Override(ClientId client) =>
        _overrides.TryGetValue(client.Raw(), out var path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : null;
}
