using System.Diagnostics;

namespace Kytto.App;

internal interface IAuthorizationPageLauncher
{
    void Open(string url);
}

/// <summary>Hands a native-recorded HTTPS authorization page to the default browser.</summary>
internal sealed class WindowsAuthorizationPageLauncher : IAuthorizationPageLauncher
{
    private readonly Action<ProcessStartInfo> _start;

    internal WindowsAuthorizationPageLauncher(Action<ProcessStartInfo>? start = null) =>
        _start = start ?? (info => { Process.Start(info); });

    public void Open(string url) => _start(new ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true,
    });
}
