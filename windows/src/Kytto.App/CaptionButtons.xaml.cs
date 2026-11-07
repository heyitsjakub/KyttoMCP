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
/// anything WPF draws behind it. So the buttons are hosted in <see cref="CaptionOverlay"/>,
/// a window of their own that the main window owns and that never takes activation,
/// rather than in the frame, which would take the strip away from the page (§7.1 puts
/// the header counts up there).
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
    /// The overlay is a separate native window, so <see cref="Window.GetWindow"/>
    /// cannot recover the main window from it. The shell supplies the owner
    /// explicitly; otherwise all three caption buttons look live and do nothing.
    /// </remarks>
    internal Window? HostWindow { get; set; }

    /// <summary>Keeps the middle glyph telling the truth about what it will do.</summary>
    internal void SyncMaximiseGlyph(WindowState state)
    {
        var maximised = state == WindowState.Maximized;
        MaximiseButton.Content = maximised ? RestoreGlyph : MaximiseGlyph;
        MaximiseButton.ToolTip = maximised ? "Restore Down" : "Maximize";
        // The glyph is a private-use character a screen reader cannot name, so the
        // automation name is set explicitly and follows the same state (audit W-13).
        System.Windows.Automation.AutomationProperties.SetName(MaximiseButton, maximised ? "Restore" : "Maximize");
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
