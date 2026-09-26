using System.Runtime.InteropServices;

namespace Kytto.App;

/// <summary>
/// The two window attributes that decide what the frame looks like.
/// </summary>
/// <remarks>
/// Both are best-effort: they were added in later Windows 11 builds and simply do
/// nothing on an older one. The caller records whether the backdrop took, because
/// the page's chrome should only become translucent when there is genuinely
/// something behind it (§10 — colour and material only where they mean something).
/// </remarks>
internal static partial class Dwm
{
    private const int UseImmersiveDarkMode = 20;
    private const int SystemBackdropType = 38;

    internal enum SystemBackdrop
    {
        Auto = 0,
        None = 1,
        /// Mica: tinted by the desktop wallpaper, for a window that stays open.
        MainWindow = 2,
        Acrylic = 3,
        /// Mica Alt: the darker variant, for tabbed shells.
        TabbedWindow = 4,
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal static bool SetDarkMode(IntPtr window, bool enabled)
    {
        var value = enabled ? 1 : 0;
        return DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref value, sizeof(int)) == 0;
    }

    internal static bool SetBackdrop(IntPtr window, SystemBackdrop backdrop)
    {
        var value = (int)backdrop;
        return DwmSetWindowAttribute(window, SystemBackdropType, ref value, sizeof(int)) == 0;
    }
}
