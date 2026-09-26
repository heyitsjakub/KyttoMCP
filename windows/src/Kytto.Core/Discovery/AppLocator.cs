using Kytto.Core.Clients;

namespace Kytto.Core;

/// <summary>
/// Answers whether a client is actually on this machine.
/// </summary>
/// <remarks>
/// An interface so tests can point discovery at a fixture directory and still
/// exercise the real code. The distinction it draws is the one §4 cares about: a
/// leftover config directory is not evidence a client is installed, because
/// <c>~/.cursor</c> outlives uninstalling Cursor.
/// </remarks>
public interface IAppLocator
{
    /// <summary>An installed application, named by where it puts its executable.</summary>
    bool ApplicationExists(string installKey, string home);

    /// <summary>An installed Store application, named by its package family.</summary>
    bool PackageExists(string familyName);

    /// <summary>A command line tool, found the way a shell would find it.</summary>
    bool ExecutableExists(string name, string home);
}

/// <summary>The real machine.</summary>
public sealed class WindowsAppLocator : IAppLocator
{
    public bool ApplicationExists(string installKey, string home)
    {
        var expanded = new PlatformPath(Darwin: "", Windows: installKey);
        try
        {
            var path = expanded.Resolve(home);
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (ArgumentException)
        {
            // A malformed override rather than a missing application.
            return false;
        }
    }

    public bool PackageExists(string familyName) => MsixPackage.Find(familyName) is not null;

    public bool ExecutableExists(string name, string home)
    {
        // PATH, the way the client's own launcher would resolve it. Not PATHEXT
        // expansion: the registry names the exact spellings it expects, because
        // `claude` on Windows is an npm shim called `claude.cmd` and guessing at
        // extensions here would make the registry less honest, not more.
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim('"'), name))) return true;
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters. Someone else's problem.
            }
        }

        // npm's global prefix is on PATH for interactive shells but not always for
        // a process launched from Explorer, and Claude Code and Codex both arrive
        // that way.
        var npm = Path.Combine(home, "AppData", "Roaming", "npm", name);
        return File.Exists(npm);
    }
}
