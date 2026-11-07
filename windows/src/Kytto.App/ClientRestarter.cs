using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using Kytto.Core.Clients;

namespace Kytto.App;

/// <summary>How one client came out of "Restart now".</summary>
internal enum RestartOutcomeKind
{
    /// <summary>The app was running, has quit and was opened again.</summary>
    Restarted,

    /// <summary>The app was not running, so nothing was quit and nothing was opened.</summary>
    NotRunning,

    /// <summary>The restart did not finish; the notice stays.</summary>
    Failed,
}

internal sealed record RestartOutcome(RestartOutcomeKind Kind, string? Message = null);

/// <summary>
/// Quits a client's desktop app and opens it again, so it reads the configuration Kytto
/// wrote (issue #14).
/// </summary>
/// <remarks>
/// <para>
/// The processes are found by where they run, not by their names: <c>claude.exe</c> is also
/// Claude Code's npm binary, so a name match could quit the wrong program. Only processes
/// whose executable lies under one of the client's install directories are touched.
/// </para>
/// <para>
/// A graceful close comes first. Claude Desktop hides to its tray when its window closes, so
/// a process that survives the close with no window is ended with its tree after a short
/// grace period; the confirmation has already warned about unsaved work. Editors are never
/// forced: a process still running after the timeout usually means an unsaved-files dialog,
/// and the user answers it.
/// </para>
/// </remarks>
internal static class ClientRestarter
{
    internal static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TrayGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Restarts one client. Blocks until it has quit or failed, so run it off the UI thread.</summary>
    internal static RestartOutcome Restart(ClientDescriptor descriptor, string home)
    {
        if (!descriptor.RestartsWithApp)
        {
            return new(RestartOutcomeKind.Failed,
                $"{descriptor.DisplayName} does not restart from Kytto. Restart it yourself, then choose I restarted.");
        }

        var directories = InstallDirectories(descriptor, home);
        var running = RunningFrom(directories);
        if (running.Count == 0) return new(RestartOutcomeKind.NotRunning);

        try
        {
            var stuck = Quit(descriptor, running);
            if (stuck is not null) return new(RestartOutcomeKind.Failed, stuck);
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }

        try
        {
            if (!Reopen(descriptor, home))
            {
                return new(RestartOutcomeKind.Failed,
                    $"{descriptor.DisplayName} quit, but Kytto could not find it to open it again. Open it yourself, then choose I restarted.");
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Log.Failure($"reopen {descriptor.DisplayName}", error);
            return new(RestartOutcomeKind.Failed,
                $"{descriptor.DisplayName} quit, but it did not open again: {error.Message}");
        }

        return new(RestartOutcomeKind.Restarted);
    }

    /// <summary>The message for a client that did not quit, or null once every process has gone.</summary>
    private static string? Quit(ClientDescriptor descriptor, IReadOnlyList<Process> running)
    {
        foreach (var process in running)
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        var clock = Stopwatch.StartNew();
        var trayResident = descriptor.Id == ClientId.ClaudeDesktop;
        while (true)
        {
            var alive = running.Where(StillRunning).ToArray();
            if (alive.Length == 0) return null;

            if (trayResident && clock.Elapsed >= TrayGrace)
            {
                foreach (var process in alive)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Exited on its own in the meantime.
                    }
                    catch (Win32Exception error)
                    {
                        Log.Failure($"end {descriptor.DisplayName}", error);
                    }
                }
                WaitForExit(running, TimeSpan.FromSeconds(5));
                if (!running.Any(StillRunning)) return null;
                return $"{descriptor.DisplayName} did not quit. Quit it yourself, then choose I restarted.";
            }

            if (clock.Elapsed >= QuitTimeout)
            {
                return $"{descriptor.DisplayName} did not quit. It may be asking about unsaved work — answer it or quit it yourself, then choose I restarted.";
            }

            Thread.Sleep(PollInterval);
        }
    }

    private static bool StillRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WaitForExit(IReadOnlyList<Process> processes, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit && processes.Any(StillRunning)) Thread.Sleep(PollInterval);
    }

    /// <summary>The directories the client's executables live in: the install keys and the store package.</summary>
    internal static IReadOnlyList<string> InstallDirectories(ClientDescriptor descriptor, string home)
    {
        var directories = new List<string>();
        foreach (var key in descriptor.InstallKeys)
        {
            try
            {
                var executable = new PlatformPath(Darwin: "", Windows: key).Resolve(home);
                if (Path.GetDirectoryName(executable) is { Length: > 0 } directory) directories.Add(directory);
            }
            catch (ArgumentException)
            {
                // A malformed key cannot name an install.
            }
        }

        foreach (var family in descriptor.PackageFamilyNames)
        {
            if (MsixPackage.Find(family)?.RootFolder is { Length: > 0 } root) directories.Add(root);
        }

        return directories;
    }

    /// <summary>The processes whose executable is under one of the directories.</summary>
    private static IReadOnlyList<Process> RunningFrom(IReadOnlyList<string> directories)
    {
        var prefixes = directories
            .Select(directory => directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();
        var found = new List<Process>();
        if (prefixes.Length == 0) return found;

        foreach (var process in Process.GetProcesses())
        {
            var matched = false;
            try
            {
                var file = process.MainModule?.FileName;
                matched = file is not null &&
                    prefixes.Any(prefix => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                // Access denied to a process of another account, or it exited while we looked.
            }

            if (matched) found.Add(process);
            else process.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Opens the client again: through its package when it is an MSIX app, otherwise through
    /// its executable. Returns false when neither can be found.
    /// </summary>
    private static bool Reopen(ClientDescriptor descriptor, string home)
    {
        foreach (var family in descriptor.PackageFamilyNames)
        {
            if (MsixPackage.Find(family) is not { } package) continue;
            var appId = ReadApplicationId(package.RootFolder);
            if (appId is null) continue;

            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{family}!{appId}")
            {
                UseShellExecute = true,
            });
            return true;
        }

        foreach (var key in descriptor.InstallKeys)
        {
            string executable;
            try
            {
                executable = new PlatformPath(Darwin: "", Windows: key).Resolve(home);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!File.Exists(executable)) continue;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            return true;
        }

        return false;
    }

    /// <summary>The application id declared in a package's manifest, which the shell needs to launch it.</summary>
    internal static string? ReadApplicationId(string packageRoot)
    {
        var manifest = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(manifest)) return null;
        try
        {
            var document = XDocument.Load(manifest);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Application")
                ?.Attribute("Id")?.Value;
        }
        catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            Log.Failure("read package manifest", error);
            return null;
        }
    }
}
