using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Kytto.App.Ipc;
using Kytto.App.Web;
using Kytto.Core.Settings;
using Microsoft.Win32;

namespace Kytto.App;

/// <summary>
/// The window, and the one place that knows how it is put together.
/// </summary>
/// <remarks>
/// It also measures the pieces of window geometry the layout needs and hands them
/// to the page as CSS custom properties. The web layer positions against those
/// variables and never learns what produced them, which is what keeps §3.2 true:
/// on macOS the same properties are a title bar height and a traffic light inset;
/// here they are whatever this shell measures.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly CommandRouter _router = new();
    private (double Titlebar, double Buttons)? _published;
    private bool _micaApplied;
    private bool _allowClose;
    private bool _initializationStarted;
    private bool _sessionEndingSubscribed;
    private bool _systemEventsSubscribed;
    private AppModel? _model;
    private TrayController? _tray;

    /// <summary>
    /// The shortcuts §3 asks for, translated to the keys Windows users press.
    /// </summary>
    /// <remarks>
    /// The shell owns them, and the page hears about the intent rather than the
    /// keystroke — `menu.refresh`, not "Ctrl+R" — which is what keeps the web layer
    /// free of a platform's keyboard conventions (§3.2).
    /// </remarks>
    private void InstallShortcuts()
    {
        Add(Key.R, ModifierKeys.Control, () => _ = _router.EmitAsync("menu.refresh", new { }));
        Add(Key.F, ModifierKeys.Control, () => _ = _router.EmitAsync("menu.find", new { }));
        // F5 as well, because on Windows that is what "reload this" means to most
        // people and the cost of accepting both is one line.
        Add(Key.F5, ModifierKeys.None, () => _ = _router.EmitAsync("menu.refresh", new { }));
        Add(Key.W, ModifierKeys.Control, Close);
        Add(Key.OemComma, ModifierKeys.Control, OpenSettings);

        void Add(Key key, ModifierKeys modifiers, Action action)
        {
            var command = new RoutedCommand();
            CommandBindings.Add(new CommandBinding(command, (_, _) => action()));
            InputBindings.Add(new KeyBinding(command, key, modifiers));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        // Popup content lives in another native window, so it cannot discover the
        // main window by walking its own visual tree.
        Caption.HostWindow = this;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        if (System.Windows.Application.Current is { } application)
        {
            application.SessionEnding += OnSessionEnding;
            _sessionEndingSubscribed = true;
        }
    }

    internal CommandRouter Router => _router;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializationStarted) return;
        _initializationStarted = true;

        // Before the frame, because the appearance the frame takes is the one the
        // settings ask for.
        _model = AppModel.ForCurrentProcess();
        // Before the page exists, so nothing Kytto stored under an earlier version is
        // still readable by another local account by the time a command can touch it.
        _model.TightenStoragePermissions();
        ApplyAppearance();

        // Register before navigation begins. A cached page can run its first module
        // immediately after Navigate; it must never beat the command registry and
        // receive a transient unknownCommand during startup.
        // The opener is queued rather than run inline: `OpenSettings` shows a
        // modal dialog, and a command handler that blocks until it closes would
        // leave the page's `settings.open` awaiting for as long as the window is
        // open.
        CommandRegistry.RegisterAll(_router, _model, () => Dispatcher.InvokeAsync(OpenSettings));

        var host = new WebHost(_router);
        try
        {
            await host.InitializeAsync(Web);
        }
        catch (Exception error)
        {
            Log.Failure("web view startup", error);
            MessageBox.Show(
                this,
                "Kytto could not start its interface. Repair or install the Microsoft Edge WebView2 Runtime, then open Kytto again.",
                "Kytto could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RequestQuit();
            return;
        }

        // A watched file changed, whoever changed it — including Kytto (§6.4). The
        // watcher fires off the UI thread, so the hop back is what makes it safe to
        // touch the web view.
        _model.ConfigsChanged += () => Dispatcher.InvokeAsync(async () =>
        {
            _model.Reload();
            await _router.EmitAsync("configs.changed", _model.ToStateDto());
        });

        _tray = new TrayController(_model, this, RequestQuit);
        _tray.Apply(_model.Settings);
        _model.SettingsChanged += settings =>
        {
            _tray.Apply(settings);
            _ = Dispatcher.InvokeAsync(async () =>
                await _router.EmitAsync("settings.changed", settings.ToDto()));
        };
        _model.ConfigsChanged += () => Dispatcher.InvokeAsync(() => _tray.Refresh());

        InstallShortcuts();

        // "System" is a live value: the page re-resolves `color-scheme` on its own
        // when Windows flips, so the material behind it has to move at the same
        // moment or the two halves of the window disagree about which theme this is.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _systemEventsSubscribed = true;

        // A load replaces `documentElement`, and with it the custom properties the
        // window measured into it. They go back on as soon as there is a document.
        Web.CoreWebView2.DOMContentLoaded += (_, _) => PublishWindowMetrics(force: true);
        SizeChanged += (_, _) => { PublishWindowMetrics(); PlaceCaptionButtons(); };
        LocationChanged += (_, _) => PlaceCaptionButtons();
        StateChanged += (_, _) =>
        {
            PublishWindowMetrics(force: true);
            Caption.SyncMaximiseGlyph(WindowState);
            PlaceCaptionButtons();
        };
        // A popup is its own window, so it would otherwise sit over whatever the
        // user switched to.
        Activated += (_, _) => PlaceCaptionButtons();
        Deactivated += (_, _) => CaptionPopup.IsOpen = false;
        Closing += (_, _) => CaptionPopup.IsOpen = false;

        Caption.SyncMaximiseGlyph(WindowState);
        PlaceCaptionButtons();

        await RunProbeAsync();
    }

    /// <summary>
    /// Closing the window leaves the native tray controller running when the user
    /// asked for it (§7.7). Quit is a separate, explicit tray action.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!ShouldKeepRunning(_model?.Settings.TrayEnabled == true, _allowClose)) return;

        args.Cancel = true;
        CaptionPopup.IsOpen = false;
        Hide();
    }

    internal static bool ShouldKeepRunning(bool trayEnabled, bool explicitQuit) =>
        trayEnabled && !explicitQuit;

    internal void RequestQuit()
    {
        _allowClose = true;
        if (System.Windows.Application.Current is { } application) application.Shutdown();
        else Close();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs args) =>
        _allowClose = true;

    private void OnClosed(object? sender, EventArgs args)
    {
        if (_sessionEndingSubscribed && System.Windows.Application.Current is { } application)
        {
            application.SessionEnding -= OnSessionEnding;
        }
        if (_systemEventsSubscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        _tray?.Dispose();

        if (_model is not { } model) return;
        model.Dispose();
    }

    /// <summary>
    /// Clicks an element from the shell side, so a UI check can tell a broken
    /// handler apart from input that never arrived.
    /// </summary>
    /// <remarks>
    /// Only in Debug builds and when <c>KYTTO_PROBE</c> names a selector, so no
    /// arbitrary-click surface exists in the production app.
    /// </remarks>
    private async Task RunProbeAsync()
    {
#if !DEBUG
        // The probe can click an arbitrary selector and is therefore test
        // instrumentation, never a production capability.
        await Task.CompletedTask;
#else
        if (Environment.GetEnvironmentVariable("KYTTO_PROBE") is not { Length: > 0 } probe) return;

        // Long enough for the page to have loaded its state and rendered once;
        // the probe is a debugging aid, not a race to win.
        await Task.Delay(4000);

        // `;` separates steps, because anything worth checking is more than one
        // click: reaching the Backups screen and then reverting something on it is
        // two, and a re-render has to land in between.
        foreach (var selector in probe.Split(';', StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            var script = $$"""
                (() => {
                  const node = document.querySelector({{System.Text.Json.JsonSerializer.Serialize(selector)}});
                  if (!node) return 'no element matched';
                  node.click();
                  return 'clicked ' + node.tagName + ' [' + (node.dataset.action ?? '') + ']';
                })()
                """;
            Log.Write($"[probe] {selector} -> {await Web.CoreWebView2.ExecuteScriptAsync(script)}");
            await Task.Delay(1500);
        }
#endif
    }

    /// <summary>
    /// The native half of Settings (§7.6). What the web layer needs of it comes
    /// back through <c>settings.get</c>; the rest never crosses (§3.2).
    /// </summary>
    private async void OpenSettings()
    {
        if (_model is null) return;

        var settings = new SettingsWindow(_model) { Owner = this };
        if (settings.ShowDialog() != true) return;

        // The page renders the theme it is told, so it hears about a change here
        // the same way it hears about anything else: as state.
        await _router.EmitAsync("configs.changed", _model.ToStateDto());
        await _router.EmitAsync("settings.changed", _model.Settings.ToDto());
        // And the material the chrome is a tint over changes with it.
        ApplyAppearance();
    }

    /// <summary>
    /// Pins the caption buttons to the window's top-right, in device-independent
    /// units relative to the window itself.
    /// </summary>
    private void PlaceCaptionButtons()
    {
        if (WindowState == WindowState.Minimized)
        {
            CaptionPopup.IsOpen = false;
            return;
        }

        const double ButtonStripWidth = 46 * 3;
        CaptionPopup.HorizontalOffset = ActualWidth - ButtonStripWidth;
        CaptionPopup.VerticalOffset = 0;
        CaptionPopup.IsOpen = true;

        // HorizontalOffset only moves an open popup when it changes, and a window
        // that merely moved keeps the same offset — so nudge it into recomputing
        // its screen position against the window's new origin.
        CaptionPopup.HorizontalOffset += 0.01;
        CaptionPopup.HorizontalOffset -= 0.01;
    }

    /// <summary>
    /// Mica behind the page, in the appearance the page is about to render.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page leaves its chrome — the toolbar band and the sidebar —
    /// translucent, so what shows through there is a real system material
    /// reacting to the desktop behind the window. It is the one thing HTML cannot
    /// fake, and here it costs two calls rather than macOS's extra view.
    /// </para>
    /// <para>
    /// Which is why the appearance cannot be a constant. At `--chrome-alpha` the
    /// chrome is a tint over the material rather than a fill, so the dark material
    /// under a light page does not read as a dark frame — it reads as §10's
    /// surfaces having been washed out, because that is arithmetically what
    /// happened to them. macOS gets the pairing for free from the effect view
    /// inheriting the window's appearance; here the frame has to be told the same
    /// thing the page was told.
    /// </para>
    /// </remarks>
    private void ApplyAppearance()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = IsDarkAppearance();
        Dwm.SetDarkMode(handle, dark);
        _micaApplied = Dwm.SetBackdrop(handle, Dwm.SystemBackdrop.MainWindow);

        // The caption buttons are the one piece of chrome the page's theme cannot
        // reach, because they are the one piece that is WPF rather than HTML.
        Caption.GlyphBrush = dark ? Brushes.White : Brushes.Black;
    }

    /// <summary>Which of the two appearances the page will resolve to.</summary>
    /// <remarks>
    /// "System" is the page's `color-scheme` resolving against Windows, and this
    /// registry value is what it resolves against. Reading it here rather than
    /// asking the page keeps the traffic going the one direction it goes
    /// everywhere else: the shell measures, the page renders (§3.2).
    /// </remarks>
    private bool IsDarkAppearance() => (_model?.Settings.Theme ?? Theme.System) switch
    {
        Theme.Light => false,
        Theme.Dark => true,
        _ => Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1) is 0,
    };

    /// <remarks>
    /// `General` is the category the light/dark switch arrives in — Windows sends
    /// it as WM_SETTINGCHANGE for "ImmersiveColorSet" — and it arrives off the UI
    /// thread.
    /// </remarks>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Dispatcher.InvokeAsync(ApplyAppearance);
    }

    /// <summary>
    /// Publishes the three window metrics <c>docs/ipc.md</c> describes.
    /// </summary>
    /// <remarks>
    /// <c>--traffic-light-inset</c> is 0: the caption buttons are on the trailing
    /// edge here, so there is nothing for the page to clear on the leading one.
    /// The name stays because it names what the page needs to know — how far in
    /// content may start — and not what produced it.
    /// </remarks>
    private void PublishWindowMetrics(bool force = false)
    {
        if (Web.CoreWebView2 is null) return;

        // Maximised, a Windows window's top edge sits above the work area, so the
        // strip grows by the amount that is off screen or the numbers are clipped.
        var titlebar = WindowState == WindowState.Maximized ? 40d : 32d;
        var metrics = (Titlebar: titlebar, Buttons: 0d);

        if (!force && _published == metrics) return;
        _published = metrics;

        // Only lowered when there is really a material behind the page. Where Mica
        // is unavailable the chrome stays a flat fill, which is the same thing the
        // browser harness shows.
        var alpha = _micaApplied ? "58%" : "100%";
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"""
             document.documentElement.style.setProperty('--titlebar-height', '{metrics.Titlebar}px');
             document.documentElement.style.setProperty('--traffic-light-inset', '{metrics.Buttons}px');
             document.documentElement.style.setProperty('--chrome-alpha', '{alpha}');
             """);
    }
}
