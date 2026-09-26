namespace Kytto.Core.Clients;

/// <summary>
/// Where a client keeps a file, spelled once per platform.
/// </summary>
/// <remarks>
/// <para>
/// The macOS spelling is carried here even though nothing on Windows resolves it,
/// so that this file keeps the shape of the macOS registry it was ported from: a
/// client fix reads the same on both platforms, and a path that has
/// moved is visible as a difference rather than hidden as an absence (§4).
/// </para>
/// <para>
/// Tokens: <c>~</c> on macOS; <c>%APPDATA%</c> and <c>%USERPROFILE%</c> here.
/// </para>
/// </remarks>
public sealed record PlatformPath(string Darwin, string Windows)
{
    /// <summary>
    /// Other places this file is found on Windows, tried in order before falling
    /// back to <see cref="Windows"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Windows-only concept, which is why it does not disturb the macOS mirror:
    /// an app installed from the Store runs packaged, and its writes to
    /// <c>%APPDATA%\Thing</c> are redirected by the OS into
    /// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\Thing</c>. Kytto is
    /// not packaged, so writing the unredirected path would put the file somewhere
    /// the client never reads — it would take a backup, write valid JSON, report
    /// success, and change nothing the user can see.
    /// </para>
    /// <para>
    /// Claude Desktop is exactly that case, and shipped both ways within a year, so
    /// which one is real has to be answered by looking rather than by assuming.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> WindowsAlternates { get; init; } = [];

    /// <summary>
    /// Resolves the path against a home directory, preferring a candidate that
    /// exists.
    /// </summary>
    /// <param name="home">
    /// A parameter rather than the real profile directory so tests can point
    /// discovery at a temporary directory and exercise the real code.
    /// </param>
    public string Resolve(string home)
    {
        foreach (var alternate in WindowsAlternates)
        {
            var candidate = Expand(alternate, home);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }

        var primary = Expand(Windows, home);
        if (File.Exists(primary) || Directory.Exists(primary)) return primary;

        // A packaged client can be installed and have created its redirected
        // Roaming directory before this particular config exists. Waiting for the
        // file itself to appear would make Kytto create the first config at the
        // classic path — a valid file the packaged client never reads.
        foreach (var alternate in WindowsAlternates)
        {
            var candidate = Expand(alternate, home);
            if (Path.GetDirectoryName(candidate) is { } parent && Directory.Exists(parent))
            {
                return candidate;
            }
        }
        return primary;
    }

    /// <summary>
    /// Every place this file could be, in preference order.
    /// </summary>
    /// <remarks>
    /// The watcher needs all of them: a client that is reinstalled the other way
    /// starts writing somewhere Kytto was not looking.
    /// </remarks>
    public IReadOnlyList<string> ResolveAll(string home) =>
        WindowsAlternates.Append(Windows).Select(path => Expand(path, home)).ToArray();

    private static string Expand(string path, string home)
    {
        // Derived from `home` rather than read from the environment, or a test
        // pointing at a temp directory would still find the real Roaming folder.
        var appData = Path.Combine(home, "AppData", "Roaming");

        var expanded = path
            .Replace("%APPDATA%", appData, StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Path.Combine(home, "AppData", "Local"), StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", home, StringComparison.OrdinalIgnoreCase);

        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// What the UI shows. The web layer receives this string and never takes it
    /// apart — it has no business knowing about separators or <c>%APPDATA%</c> (§3.2).
    /// </summary>
    public string DisplayString() => Windows;

    /// <summary>
    /// What the UI shows for the copy actually in use, which is not always the one
    /// the registry names first.
    /// </summary>
    public string DisplayString(string home)
    {
        foreach (var alternate in WindowsAlternates)
        {
            var candidate = Expand(alternate, home);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return alternate;
        }
        var primary = Expand(Windows, home);
        if (File.Exists(primary) || Directory.Exists(primary)) return Windows;
        foreach (var alternate in WindowsAlternates)
        {
            var candidate = Expand(alternate, home);
            if (Path.GetDirectoryName(candidate) is { } parent && Directory.Exists(parent))
            {
                return alternate;
            }
        }
        return Windows;
    }
}
