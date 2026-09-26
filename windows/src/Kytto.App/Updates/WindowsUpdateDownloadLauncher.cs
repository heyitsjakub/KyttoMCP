using System.Diagnostics;
using Kytto.Core.Updates;

namespace Kytto.App;

internal interface IUpdateDownloadLauncher
{
    void Open();
}

/// <summary>Hands the fixed Windows download route to the user's default browser.</summary>
internal sealed class WindowsUpdateDownloadLauncher : IUpdateDownloadLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    internal WindowsUpdateDownloadLauncher(Action<ProcessStartInfo>? start = null) =>
        _start = start ?? (info => { Process.Start(info); });

    public void Open() => _start(new ProcessStartInfo
    {
        FileName = UpdateChecker.WindowsDownloadUrl,
        UseShellExecute = true,
    });
}
