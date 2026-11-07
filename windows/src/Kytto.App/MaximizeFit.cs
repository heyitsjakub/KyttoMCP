using System.Runtime.InteropServices;

namespace Kytto.App;

/// <summary>
/// A rectangle in device pixels, as Win32 reports it: right and bottom are exclusive.
/// </summary>
internal readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// The edges of a monitor that carry an auto-hide taskbar.
/// </summary>
internal enum ScreenEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>
/// Where a maximised Kytto window goes (issue #15).
/// </summary>
/// <remarks>
/// <para>
/// The window is frameless (<c>WindowStyle="None"</c> with a <c>WindowChrome</c>), and
/// nothing told Windows where its maximised size ends. Windows then maximises it to the
/// whole monitor, so the bottom of the window sits under the taskbar.
/// </para>
/// <para>
/// The fix answers <c>WM_GETMINMAXINFO</c> with the monitor's work area. An auto-hide
/// taskbar does not reserve space in the work area, so the window would cover the
/// strip the taskbar slides out of; one pixel is left free on that edge for it.
/// </para>
/// </remarks>
internal static class MaximizedBounds
{
    /// <summary>
    /// The maximised rectangle, in the same coordinates as <paramref name="work"/>.
    /// </summary>
    internal static ScreenRect Compute(ScreenRect work, IEnumerable<ScreenEdge> autoHideEdges)
    {
        var edges = autoHideEdges.ToHashSet();
        return new ScreenRect(
            work.Left + (edges.Contains(ScreenEdge.Left) ? 1 : 0),
            work.Top + (edges.Contains(ScreenEdge.Top) ? 1 : 0),
            work.Right - (edges.Contains(ScreenEdge.Right) ? 1 : 0),
            work.Bottom - (edges.Contains(ScreenEdge.Bottom) ? 1 : 0));
    }
}

/// <summary>
/// The <c>WM_GETMINMAXINFO</c> answer for a frameless window, read from the monitor it is on.
/// </summary>
internal static partial class MaximizeFit
{
    internal const int WmGetMinMaxInfo = 0x0024;

    private const uint MonitorDefaultToNearest = 2;
    private const uint AbmGetAutoHideBarEx = 0x0000000b;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly ScreenRect ToScreen() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public IntPtr LParam;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    /// <summary>
    /// Fills a <c>MINMAXINFO</c> so a maximised window fills the work area of its monitor
    /// and never shrinks below <paramref name="minTrackWidth"/> × <paramref name="minTrackHeight"/>.
    /// </summary>
    /// <param name="window">The window the message is for.</param>
    /// <param name="infoPointer">The <c>lParam</c> of <c>WM_GETMINMAXINFO</c>.</param>
    /// <param name="minTrackWidth">The minimum width in device pixels.</param>
    /// <param name="minTrackHeight">The minimum height in device pixels.</param>
    internal static void Fill(IntPtr window, IntPtr infoPointer, int minTrackWidth, int minTrackHeight)
    {
        var info = Marshal.PtrToStructure<MinMaxInfo>(infoPointer);
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var native = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref native))
        {
            var monitorRect = native.Monitor.ToScreen();
            var fit = MaximizedBounds.Compute(native.Work.ToScreen(), AutoHideEdges(window, monitor, monitorRect));

            info.MaxPosition = new NativePoint { X = fit.Left - monitorRect.Left, Y = fit.Top - monitorRect.Top };
            info.MaxSize = new NativePoint { X = fit.Width, Y = fit.Height };
        }

        info.MinTrackSize = new NativePoint { X = minTrackWidth, Y = minTrackHeight };
        Marshal.StructureToPtr(info, infoPointer, fDeleteOld: false);
    }

    /// <summary>
    /// Which edges of this monitor have an auto-hide taskbar. Asked per edge, the way the
    /// shell answers it: a non-zero window handle means one sits there.
    /// </summary>
    private static IEnumerable<ScreenEdge> AutoHideEdges(IntPtr window, IntPtr monitor, ScreenRect monitorRect)
    {
        foreach (var edge in Enum.GetValues<ScreenEdge>())
        {
            var data = new AppBarData
            {
                Size = Marshal.SizeOf<AppBarData>(),
                Window = window,
                Edge = (uint)edge,
                Rect = new NativeRect
                {
                    Left = monitorRect.Left,
                    Top = monitorRect.Top,
                    Right = monitorRect.Right,
                    Bottom = monitorRect.Bottom,
                },
                LParam = monitor,
            };
            if (SHAppBarMessage(AbmGetAutoHideBarEx, ref data) != 0) yield return edge;
        }
    }
}
