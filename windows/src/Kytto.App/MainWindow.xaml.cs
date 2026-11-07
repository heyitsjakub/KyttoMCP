using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Kytto.App.Ipc;
using Kytto.App.Web;
using Kytto.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
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
    private readonly CaptionOverlay _captionOverlay = new();
    private (double Titlebar, double Buttons)? _published;
    private bool _micaApplied;
    private bool _allowClose;
    private bool _initializationStarted;
    private bool _sessionEndingSubscribed;
    private bool _systemEventsSubscribed;
    private AppModel? _model;
    private TrayController? _tray;
    private WebHost? _host;
    private InterfaceRecovery? _recovery;
    private bool _webDetached;
    private bool _settingsOpen;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// Opens the two fixed report links (§7.14). Settable so a test never starts a
    /// browser; production always uses the system browser.
    /// </summary>
    internal ISupportLinkLauncher SupportLinkLauncher { get; set; } = SystemBrowserSupportLauncher.Shared;

    /// <summary>The interface-recovery state machine (§7.14), for tests.</summary>
    internal InterfaceRecovery? RecoveryState => _recovery;

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
        Add(Key.R, ModifierKeys.Control, RefreshOrReload);
        Add(Key.F, ModifierKeys.Control, () => _ = _router.EmitAsync("menu.find", new { }));
        // F5 as well, because on Windows that is what "reload this" means to most
        // people and the cost of accepting both is one line.
        Add(Key.F5, ModifierKeys.None, RefreshOrReload);
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
        // The caption buttons live in their own window, which cannot find the main window
        // by walking a visual tree, so they are told which window they control.
        // Owned rather than a topmost popup: it stays above this window, hides with it,
        // and does not vanish when another app is clicked (audit W-01).
        _captionOverlay.Caption.HostWindow = this;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) PlaceCaptionButtons(); };
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

    /// <summary>The caption buttons, which live in the overlay window.</summary>
    internal CaptionButtons Caption => _captionOverlay.Caption;

    /// <summary>
    /// Renders this window's own WPF content in software (§7.14).
    /// </summary>
    /// <remarks>
    /// Until the recovery panel nothing WPF drew in this window was ever on screen:
    /// the page covers it, and the caption buttons are a Popup, which is a separate
    /// window of its own. The panel is drawn in the window itself, over a sheet of
    /// glass with a system backdrop behind it. On the machine this was verified on,
    /// hardware-accelerated WPF painted nothing there — a solid-coloured grid, the
    /// window background and all text were invisible, leaving only the backdrop —
    /// and a bare WPF window with one TextBlock was blank too, so it is a property
    /// of that display path rather than of this tree. Rendered in software the same
    /// tree shows, deterministically, and it costs nothing here: this window holds
    /// no WPF content but the panel. Only this window's target is switched; the
    /// page is a separate composition and draws as before.
    /// </remarks>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Owner can only be set once this window has a handle: WPF refuses an owner that has not
        // been shown. The overlay is owned, not a topmost popup, so it stays above this window (W-01).
        _captionOverlay.Owner = this;
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            // Maximised to the work area, not the monitor (issue #15).
            source.AddHook(MaximizeWindowProc);
            if (source.CompositionTarget is { } target)
            {
                target.RenderMode = RenderMode.SoftwareOnly;
            }
        }
    }

    /// <summary>
    /// Keeps a maximised window out from under the taskbar (issue #15).
    /// </summary>
    /// <remarks>
    /// Answered here because the window has no frame of its own for Windows to size it
    /// against. The minimum size is converted to device pixels, since
    /// <c>WM_GETMINMAXINFO</c> speaks in them and <c>MinWidth</c> does not.
    /// </remarks>
    private IntPtr MaximizeWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != MaximizeFit.WmGetMinMaxInfo) return IntPtr.Zero;

        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        MaximizeFit.Fill(
            hwnd,
            lParam,
            (int)Math.Round(MinWidth * scale.M11),
            (int)Math.Round(MinHeight * scale.M22));
        handled = true;
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializationStarted) return;
        _initializationStarted = true;

        // Before the frame, because the appearance the frame takes is the one the
        // settings ask for. A settings or routes file that cannot be read used to end
        // the process here with nothing shown (audit W-06); it now gets a message.
        try
        {
            _model = AppModel.ForCurrentProcess();
            // Before the page exists, so nothing Kytto stored under an earlier version is
            // still readable by another local account by the time a command can touch it.
            _model.TightenStoragePermissions();
            ApplyAppearance();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Failure("startup", error);
            MessageBox.Show(
                this,
                "Kytto could not read its settings. Its log has the details; fix or remove the file it names, then open Kytto again.",
                "Kytto could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RequestQuit();
            return;
        }

        // Register before navigation begins. A cached page can run its first module
        // immediately after Navigate; it must never beat the command registry and
        // receive a transient unknownCommand during startup.
        // The opener is queued rather than run inline: `OpenSettings` shows a
        // modal dialog, and a command handler that blocks until it closes would
        // leave the page's `settings.open` awaiting for as long as the window is
        // open.
        CommandRegistry.RegisterAll(_router, _model, () => Dispatcher.InvokeAsync(OpenSettings));

        // Before the page exists, so the very first load can already fail into the
        // native panel (§7.14) rather than into a blank window.
        _recovery = new InterfaceRecovery(new RecoveryHost(this));
        Recovery.ReloadRequested += StartRecovery;
        Recovery.ReportRequested += OpenSupportLink;

        try
        {
            await StartInterfaceAsync();
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
        // The re-read runs off the UI thread, and the page is told only when something
        // it shows is different: Claude Code rewrites ~/.claude.json after nearly every
        // message without touching a server.
        _model.ConfigsChanged += () => Dispatcher.InvokeAsync(async () =>
        {
            if (!await _model.ReloadInBackgroundAsync()) return;
            await _router.EmitAsync("configs.changed", _model.ToStateDto());
            _tray?.Refresh();
        });

        _tray = new TrayController(_model, this, RequestQuit, SupportLinkLauncher, () =>
            Dispatcher.InvokeAsync(async () =>
            {
                await _router.EmitAsync("configs.changed", _model.ToStateDto());
                _tray?.Refresh();
            }));
        _tray.Apply(_model.Settings);
        _model.SettingsChanged += settings =>
        {
            _tray.Apply(settings);
            _ = Dispatcher.InvokeAsync(async () =>
                await _router.EmitAsync("settings.changed", settings.ToDto()));
        };

        InstallShortcuts();

        // "System" is a live value: the page re-resolves `color-scheme` on its own
        // when Windows flips, so the material behind it has to move at the same
        // moment or the two halves of the window disagree about which theme this is.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _systemEventsSubscribed = true;

        SizeChanged += (_, _) => { PublishWindowMetrics(); PlaceCaptionButtons(); };
        LocationChanged += (_, _) => PlaceCaptionButtons();
        StateChanged += (_, _) =>
        {
            PublishWindowMetrics(force: true);
            Caption.SyncMaximiseGlyph(WindowState);
            PlaceCaptionButtons();
        };
        // The overlay is owned, so it stays above this window and is placed against it;
        // activation does not change it, and it is kept in step with the window's moves.
        Activated += (_, _) => PlaceCaptionButtons();

        Caption.SyncMaximiseGlyph(WindowState);
        PlaceCaptionButtons();

        await RunProbeAsync();
    }

    // MARK: - The web interface and its recovery (§7.14)

    /// <summary>
    /// Brings up the web interface in the current <c>Web</c> control: the
    /// environment, the navigation policy and bridge, the router, and the callbacks
    /// recovery listens to. Everything that has to be redone when the control is
    /// rebuilt lives here, and only here.
    /// </summary>
    private async Task StartInterfaceAsync()
    {
        var host = new WebHost(_router, SupportLinkLauncher);
        host.ProcessFailed += kind =>
        {
            // Once the browser process is gone the control's CoreWebView2 throws on
            // every access, so nothing native may keep reaching for it: the router
            // lets go at once, and a rebuild attaches the new control.
            if (kind is InterfaceFailureKind.BrowserProcessExited or InterfaceFailureKind.OtherFatal)
            {
                _router.Detach();
            }
            _recovery?.ReportProcessFailure(kind);
        };
        host.NavigationFinished += outcome => _recovery?.ReportNavigation(outcome);
        _host = host;
        _webDetached = false;

        await host.InitializeAsync(Web);

        // A load replaces `documentElement`, and with it the custom properties the
        // window measured into it. They go back on as soon as there is a document.
        Web.CoreWebView2.DOMContentLoaded += (_, _) => PublishWindowMetrics(force: true);
    }

    /// <summary>
    /// The page's <c>CoreWebView2</c>, or null when there is none to use — not yet
    /// created, or no longer valid because its browser process is gone, which the
    /// control reports by throwing from the getter itself (§7.14).
    /// </summary>
    private CoreWebView2? LiveCore()
    {
        if (_webDetached) return null;
        try
        {
            return Web.CoreWebView2;
        }
        catch (Exception error) when (
            error is InvalidOperationException or ObjectDisposedException or COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Throws the web control away and builds a new one (§7.14). For when the
    /// browser process is gone, which takes every object tied to it with it.
    /// </summary>
    /// <remarks>
    /// Navigates to the bundled app origin and nothing else. No command is re-sent:
    /// whatever the old page had in flight finishes natively, and its reply is
    /// dropped by the bridge because it carries another page's tag.
    /// </remarks>
    private async Task RecreateInterfaceAsync()
    {
        try
        {
            _host?.Retire();
            _router.Detach();

            var stale = Web;
            // -1 when the stale control already left the layout (see HideWebView).
            var index = Layout.Children.IndexOf(stale);
            Layout.Children.Remove(stale);
            try
            {
                stale.Dispose();
            }
            catch (Exception error)
            {
                // The point of replacing it is that it may already be unusable.
                Log.Failure("web view dispose", error);
            }

            // Hidden, not collapsed: still part of the layout, and the panel stays
            // up until the page it loads has finished.
            Web = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.Transparent,
                Visibility = Visibility.Hidden,
            };
            Layout.Children.Insert(Math.Max(0, index), Web);

            await StartInterfaceAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Failure("interface rebuild", error);
            throw;
        }
    }

    /// <summary>Navigates the existing control to the bundled origin — only there.</summary>
    private void ReloadInterface()
    {
        try
        {
            if (LiveCore() is not { } core)
            {
                throw new InvalidOperationException("The web view is not usable.");
            }
            core.Navigate(WebHost.AppEntryUri.AbsoluteUri);
        }
        catch (Exception error) when (
            error is InvalidOperationException or COMException or ObjectDisposedException)
        {
            Log.Failure("interface reload", error);
            throw;
        }
    }

    private void ShowRecoveryPanel(RecoveryPanelState state)
    {
        var firstShow = Recovery.Visibility != Visibility.Visible;
        Recovery.Present(state);

        // The web view is a child window and paints over anything WPF draws, so the
        // panel only shows once the web view is out of the way.
        HideWebView();
        Recovery.Visibility = Visibility.Visible;
        if (firstShow) Recovery.FocusReload();
    }

    private void HideWebView()
    {
        try
        {
            Web.Visibility = Visibility.Hidden;
        }
        catch (Exception error) when (
            error is InvalidOperationException or ObjectDisposedException or COMException)
        {
            // A control whose browser process is gone can refuse even this. It still
            // must not be left painting over the panel, so it leaves the layout; the
            // next recovery has to rebuild it, which ReloadInterface insists on.
            Log.Failure("web view hide", error);
            Layout.Children.Remove(Web);
            _webDetached = true;
        }
    }

    private void HideRecoveryPanel()
    {
        Recovery.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        PublishWindowMetrics(force: true);
    }

    private async void StartRecovery()
    {
        if (_recovery is null) return;
        try
        {
            await _recovery.RecoverAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // RecoverAsync reports its own failures through the panel; this is the
            // last guard on an async-void handler.
            Log.Failure("interface recovery", error);
        }
    }

    /// <summary>
    /// F5 and Ctrl+R: refresh the page's data — or, when the interface is down,
    /// reload it, which is what pressing them there has to mean.
    /// </summary>
    private void RefreshOrReload()
    {
        if (_recovery is { PanelVisible: true })
        {
            StartRecovery();
            return;
        }
        _ = _router.EmitAsync("menu.refresh", new { });
    }

    /// <summary>
    /// Opens one of the two fixed report links in the system browser. Nothing is
    /// attached to it and nothing is submitted (§7.14).
    /// </summary>
    private void OpenSupportLink(SupportLink link)
    {
        if (!SupportLinkLauncher.Open(link.Url)) Recovery.ShowNotice(SupportLinks.FallbackMessage(link));
    }

    /// <summary>What the recovery state machine drives.</summary>
    private sealed class RecoveryHost(MainWindow window) : IInterfaceRecoveryHost
    {
        public void ShowPanel(RecoveryPanelState state) => window.ShowRecoveryPanel(state);

        public void HidePanel() => window.HideRecoveryPanel();

        public void ReloadInterface() => window.ReloadInterface();

        public Task RecreateInterfaceAsync() => window.RecreateInterfaceAsync();
    }

    /// <summary>
    /// Closing the window leaves the native tray controller running when the user
    /// asked for it (§7.7). Quit is a separate, explicit tray action.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!ShouldKeepRunning(_model?.Settings.TrayEnabled == true, _allowClose)) return;

        args.Cancel = true;
        _captionOverlay.Hide();
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
        _captionOverlay.Close();

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
        // Two settings.open requests can queue before the first dialog disables this
        // window, and the second would then open a dialog on top of the first (audit W-16).
        if (_settingsOpen)
        {
            if (_settingsWindow is { } open) TrayController.Reveal(open);
            return;
        }

        _settingsOpen = true;
        try
        {
            var settings = new SettingsWindow(_model, SupportLinkLauncher) { Owner = this };
            _settingsWindow = settings;
            if (settings.ShowDialog() != true) return;

            // The page renders the theme it is told, so it hears about a change here
            // the same way it hears about anything else: as state.
            await _router.EmitAsync("configs.changed", _model.ToStateDto());
            await _router.EmitAsync("settings.changed", _model.Settings.ToDto());
            // And the material the chrome is a tint over changes with it.
            ApplyAppearance();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // An exception here used to escape an async void handler and close the app
            // (audit W-06). It is logged, and the app keeps running.
            Log.Failure("settings", error);
        }
        finally
        {
            _settingsOpen = false;
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// Pins the caption buttons to the window's top-right, in device-independent
    /// units relative to the window itself.
    /// </summary>
    private void PlaceCaptionButtons()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            _captionOverlay.Hide();
            return;
        }

        // The window's top-right corner in device pixels, converted to the DIPs WPF
        // positions a window in. Computed from the screen rather than from Left/Top,
        // which do not describe a maximised window's position.
        var corner = PointToScreen(new Point(ActualWidth, 0));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            corner = target.TransformFromDevice.Transform(corner);
        }

        _captionOverlay.Left = corner.X - CaptionOverlay.StripWidth;
        _captionOverlay.Top = corner.Y;
        if (!_captionOverlay.IsVisible) _captionOverlay.Show();
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
        // reach, because they are the one piece that is WPF rather than HTML. The
        // recovery panel is native for the same reason and follows the same decision.
        Caption.GlyphBrush = dark ? Brushes.White : Brushes.Black;
        Recovery.ApplyAppearance(dark);
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
        if (LiveCore() is not { } core) return;

        // Maximised, the window now fills exactly its monitor's work area (see
        // MaximizeWindowProc), so the strip keeps its normal height. The 40 px this
        // used to publish compensated for a window that sat above the work area (#15).
        var titlebar = 32d;
        var metrics = (Titlebar: titlebar, Buttons: 0d);

        if (!force && _published == metrics) return;
        _published = metrics;

        // Only lowered when there is really a material behind the page. Where Mica
        // is unavailable the chrome stays a flat fill, which is the same thing the
        // browser harness shows.
        var alpha = _micaApplied ? "58%" : "100%";
        _ = core.ExecuteScriptAsync(
            $"""
             document.documentElement.style.setProperty('--titlebar-height', '{metrics.Titlebar}px');
             document.documentElement.style.setProperty('--traffic-light-inset', '{metrics.Buttons}px');
             document.documentElement.style.setProperty('--chrome-alpha', '{alpha}');
             """);
    }
}
