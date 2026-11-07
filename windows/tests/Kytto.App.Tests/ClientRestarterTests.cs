using System.IO;
using System.Diagnostics;
using Kytto.App;
using Kytto.Core.Clients;
using Xunit;

namespace Kytto.App.Tests;

/// <summary>
/// "Restart now" (issue #14). The end-to-end case runs a copy of a harmless system binary
/// from a temporary directory, so no real client is ever quit: only processes running from
/// that directory are touched.
/// </summary>
public sealed class ClientRestarterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kytto-restart-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var file = process.MainModule?.FileName;
                if (file is not null && file.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not ours to touch, or already gone.
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A process still holding the copy; the temp directory is cleared by Windows later.
        }
    }

    [Fact]
    public void AnApplicationIdIsReadFromThePackageManifest()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "AppxManifest.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="Claude" Executable="app\claude.exe" />
              </Applications>
            </Package>
            """);

        Assert.Equal("Claude", ClientRestarter.ReadApplicationId(_root));
    }

    [Fact]
    public void AMissingManifestHasNoApplicationId()
    {
        Assert.Null(ClientRestarter.ReadApplicationId(_root));
    }

    [Fact]
    public void AClientThatIsNotRunningIsReportedAsNotRunningAndNotOpened()
    {
        Directory.CreateDirectory(_root);
        var descriptor = ClientRegistry.Descriptor(ClientId.Cursor) with
        {
            InstallKeys = [Path.Combine(_root, "Cursor", "Cursor.exe")],
            PackageFamilyNames = [],
        };

        var outcome = ClientRestarter.Restart(descriptor, home: _root);

        Assert.Equal(RestartOutcomeKind.NotRunning, outcome.Kind);
    }

    [Fact]
    public void AClientThatDoesNotRestartWithItsAppIsRefusedBeforeAnyProcessIsTouched()
    {
        var outcome = ClientRestarter.Restart(ClientRegistry.Descriptor(ClientId.Codex), home: _root);

        Assert.Equal(RestartOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("does not restart from Kytto", outcome.Message);
    }

    [Fact]
    public void ARunningCopyFromTheInstallDirectoryIsQuitAndOpenedAgain()
    {
        // A copy of ping.exe stands in for the client. Run with no arguments it prints usage
        // and exits, so the reopened copy does not linger.
        var installDirectory = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(installDirectory);
        var executable = Path.Combine(installDirectory, "fake-client.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), executable);

        using var running = Process.Start(new ProcessStartInfo(executable, "-n 100 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        // Claude Desktop is the tray-resident case: no window, so it is ended after the grace period.
        var descriptor = ClientRegistry.Descriptor(ClientId.ClaudeDesktop) with
        {
            InstallKeys = [executable],
            PackageFamilyNames = [],
            RestartsWithApp = true,
        };

        var outcome = ClientRestarter.Restart(descriptor, home: _root);

        Assert.Equal(RestartOutcomeKind.Restarted, outcome.Kind);
        Assert.True(running.WaitForExit(TimeSpan.FromSeconds(10)), "The running copy was not quit.");
    }
}
