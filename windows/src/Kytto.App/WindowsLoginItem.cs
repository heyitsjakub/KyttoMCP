using System.IO;
using Microsoft.Win32;

namespace Kytto.App;

internal interface ILoginItem
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

/// <summary>Per-user launch-at-login integration through the existing Run key.</summary>
internal sealed class WindowsLoginItem : ILoginItem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kytto";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception error) when (
            error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Failure("read login item", error);
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = enabled
                ? Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null)
            {
                if (!enabled) return;
                throw new InvalidOperationException("Windows did not open the current-user startup registry key.");
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Windows did not report the Kytto executable path.");
                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception error) when (
            error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Windows could not {(enabled ? "enable" : "disable")} launch at login.",
                error);
        }
    }
}
