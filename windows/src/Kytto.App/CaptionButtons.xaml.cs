using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kytto.App;

/// <summary>
/// The window's own minimise, maximise and close buttons.
/// </summary>
/// <remarks>
/// <para>
/// They exist as WPF rather than as the frame's because the web view is a child
/// window that covers the whole frame, and a child window is painted over
/// anything WPF draws behind it. So the buttons are hosted in a <c>Popup</c>,
/// which gets a window of its own and therefore lands on top — the standard way
/// out of that overlap, and cheaper than the alternative of giving the shell a
/// strip of the window and taking it away from the page (§7.1 puts the header
/// counts up there).
/// </para>
/// <para>
/// The glyphs are the system's own, so they match whatever the user's Windows
/// build draws in every other window.
/// </para>
/// </remarks>
public partial class CaptionButtons : UserControl
{
    // Segoe Fluent Icons, by code point rather than as literals: these live in
    // the Unicode private use area, where the character itself survives a trip
    // through a text pipeline far less reliably than the digits naming it.
    private static readonly string MaximiseGlyph = char.ConvertFromUtf32(0xE922);
    private static readonly string RestoreGlyph = char.ConvertFromUtf32(0xE923);

    public static readonly DependencyProperty GlyphBrushProperty =
        DependencyProperty.Register(
            nameof(GlyphBrush),
            typeof(Brush),
            typeof(CaptionButtons),
            new PropertyMetadata(Brushes.Black));

    /// <summary>Follows the page's theme, so the glyphs stay legible on both.</summary>
    public Brush GlyphBrush
    {
        get => (Brush)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    public CaptionButtons()
    {
        InitializeComponent();
    }

    /// <summary>The window these buttons control.</summary>
    /// <remarks>
    /// A popup owns a separate native window, so <see cref="Window.GetWindow"/>
    /// cannot recover the main window from its child. The shell supplies the owner
    /// explicitly; otherwise all three caption buttons look live and do nothing.
    /// </remarks>
    internal Window? HostWindow { get; set; }

    /// <summary>Keeps the middle glyph telling the truth about what it will do.</summary>
    internal void SyncMaximiseGlyph(WindowState state)
    {
        var maximised = state == WindowState.Maximized;
        MaximiseButton.Content = maximised ? RestoreGlyph : MaximiseGlyph;
        MaximiseButton.ToolTip = maximised ? "Restore Down" : "Maximize";
    }

    private void OnMinimise(object sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window) window.WindowState = WindowState.Minimized;
    }

    private void OnMaximise(object sender, RoutedEventArgs e)
    {
        if (HostWindow is not { } window) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => HostWindow?.Close();
}
