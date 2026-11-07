using System.Windows;
using System.Windows.Interop;

namespace Kytto.App;

/// <summary>
/// The caption buttons' window: owned by the main window, above it, and never active.
/// </summary>
/// <remarks>
/// Clicking one must not move activation to it. Activation would take it away from the
/// main window, so the buttons would be drawn for an inactive window — the state this
/// window exists to fix. <c>WM_MOUSEACTIVATE</c> answers <c>MA_NOACTIVATE</c>, so the
/// click reaches the button and the main window stays where it was.
/// </remarks>
public partial class CaptionOverlay : Window
{
    /// <summary>Three 46-pixel buttons, the Windows 11 caption strip (§7.1).</summary>
    internal const double StripWidth = 46 * 3;
    internal const double StripHeight = 32;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    public CaptionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(NoActivateHook);
            }
        };
    }

    /// <summary>The buttons this window hosts.</summary>
    internal CaptionButtons Caption => Buttons;

    private static IntPtr NoActivateHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseActivate) return IntPtr.Zero;
        handled = true;
        return (IntPtr)MaNoActivate;
    }
}
