namespace Kytto.Core.Health;

/// <summary>
/// The environment a server is spawned into, and specifically the PATH it is
/// found on.
/// </summary>
/// <remarks>
/// <para>
/// The problem this exists to solve is the same on both platforms and has a
/// different shape on each. On macOS an app launched from Finder does not inherit
/// the login shell's PATH, so Homebrew and nvm are invisible to it. On Windows the
/// process environment is generally right — but the one thing almost every MCP
/// server needs, npm's global prefix, is added to PATH by the installer and is
/// missing from a process started before that took effect, and from any process
/// whose parent had a stale environment block.
/// </para>
/// <para>
/// So the answer here is not to interrogate a shell but to make sure the places
/// Node tooling actually installs to are on the list.
/// </para>
/// </remarks>
public sealed class ShellEnvironment
{
    private readonly IReadOnlyDictionary<string, string> _base;

    private ShellEnvironment(
        IReadOnlyDictionary<string, string> baseEnvironment,
        bool augmented,
        bool preferredPathAvailable)
    {
        _base = baseEnvironment;
        AugmentedPath = augmented;
        PreferredPathAvailable = preferredPathAvailable;
    }

    /// <summary>Whether Kytto had to add anything to the PATH it inherited.</summary>
    /// <remarks>Surfaced in the failure message, so "command not found" can say where it looked.</remarks>
    public bool AugmentedPath { get; }

    /// <summary>Whether the user + system PATH could be read from Windows.</summary>
    public bool PreferredPathAvailable { get; }

    public string SourceDisplay => (PreferredPathAvailable ? "User + system PATH" : "Process PATH") +
        (AugmentedPath ? " + common tool locations" : "");

    public static ShellEnvironment Current { get; } = Build();

    private static ShellEnvironment Build()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) environment[key] = value;
        }

        var machinePath = ReadPath(EnvironmentVariableTarget.Machine);
        var userPath = ReadPath(EnvironmentVariableTarget.User);
        var preferredPathAvailable = machinePath is not null || userPath is not null;
        var path = preferredPathAvailable
            ? string.Join(Path.PathSeparator,
                new[] { machinePath, userPath }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : environment.GetValueOrDefault("PATH") ?? "";
        var existing = path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim('"').TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var added = false;
        foreach (var candidate in LikelyToolDirectories(home))
        {
            if (!Directory.Exists(candidate)) continue;
            if (!existing.Add(candidate.TrimEnd(Path.DirectorySeparatorChar))) continue;
            path = path.Length == 0 ? candidate : path + Path.PathSeparator + candidate;
            added = true;
        }

        environment["PATH"] = path;
        return new ShellEnvironment(environment, added, preferredPathAvailable);
    }

    private static string? ReadPath(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("PATH", target);
        }
        catch (Exception error) when (
            error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where the tooling MCP servers are launched with actually installs.</summary>
    private static IEnumerable<string> LikelyToolDirectories(string home)
    {
        // npm's global prefix, which is where `npx`, `claude` and `codex` land.
        yield return Path.Combine(home, "AppData", "Roaming", "npm");

        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, "nodejs");
        }

        // uv and pipx, which is how the Python MCP servers in the catalog arrive.
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(home, ".cargo", "bin");
    }

    /// <summary>The environment for one spawn: the base, plus the server's own variables.</summary>
    /// <remarks>
    /// The server's values win. That is the whole point of putting a token in a
    /// config — and it is also why an empty value is passed through rather than
    /// dropped, since unsetting a variable is a thing a definition can legitimately
    /// mean to do.
    /// </remarks>
    public IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> extra)
    {
        var result = new Dictionary<string, string>(_base, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in extra) result[key] = value;
        return result;
    }
}
