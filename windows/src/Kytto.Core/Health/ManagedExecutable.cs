using System.Diagnostics;

namespace Kytto.Core.Health;

public sealed record ResolvedExecutable(
    string Path,
    string FileName,
    IReadOnlyList<string> PrefixArguments);

/// <summary>
/// Resolves a command against the environment without asking a shell to
/// reinterpret the real server arguments. Batch shims use cmd.exe explicitly.
/// </summary>
public static class ManagedExecutable
{
    public static ResolvedExecutable Resolve(
        string command,
        IReadOnlyDictionary<string, string> environment)
    {
        var path = Find(command, environment);
        if (!IsScript(path)) return new ResolvedExecutable(path, path, []);

        return new ResolvedExecutable(
            path,
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            ["/d", "/c", path]);
    }

    public static ProcessStartInfo StartInfo(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var resolved = Resolve(command, environment);
        var start = new ProcessStartInfo
        {
            FileName = resolved.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in resolved.PrefixArguments) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        return start;
    }

    private static string Find(string command, IReadOnlyDictionary<string, string> environment)
    {
        if (command.Contains(System.IO.Path.DirectorySeparatorChar) ||
            command.Contains(System.IO.Path.AltDirectorySeparatorChar))
        {
            var full = System.IO.Path.GetFullPath(command);
            if (File.Exists(full)) return full;
            throw ProcessException.ExecutableMissing(command, full);
        }

        var extensions = (environment.GetValueOrDefault("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in PathOf(environment)
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim('"');
            if (trimmed.Length == 0) continue;

            if (System.IO.Path.HasExtension(command))
            {
                var exact = SafeCombine(trimmed, command);
                if (exact is not null && File.Exists(exact)) return exact;
                continue;
            }

            foreach (var extension in extensions)
            {
                var candidate = SafeCombine(trimmed, command + extension);
                if (candidate is not null && File.Exists(candidate)) return candidate;
            }
        }
        throw ProcessException.CommandNotFound(command, PathOf(environment));
    }

    private static string? SafeCombine(string directory, string name)
    {
        try { return System.IO.Path.Combine(directory, name); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsScript(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(IReadOnlyDictionary<string, string> environment) =>
        environment.GetValueOrDefault("PATH")
        ?? environment.GetValueOrDefault("Path")
        ?? "";
}
