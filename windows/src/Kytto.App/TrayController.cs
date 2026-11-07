using System.Drawing;
using System.IO;
using System.Windows;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Settings;
using Forms = System.Windows.Forms;

namespace Kytto.App;

/// <summary>
/// The tray item (§7.7): servers with a quick switch, and aggregate health at a
/// glance.
/// </summary>
/// <remarks>
/// <para>
/// Native, never a hidden web view — §3 is explicit about that, and it is the
/// right call: this has to appear instantly from a click on a 16px target, and it
/// is the thing that keeps the app present without the window open.
/// </para>
/// <para>
/// It toggles in one client rather than all of them, because a tray menu is not
/// the place to make a five-way decision. The client is the one with the most
/// servers unless Settings says otherwise, which is right until the user says so.
/// </para>
/// </remarks>
internal sealed class TrayController : IDisposable
{
    private Forms.NotifyIcon? _icon;
    private Icon? _ownedIcon;
    private readonly AppModel _model;
    private readonly Window _window;
    private readonly Action _requestQuit;
    private readonly ISupportLinkLauncher _supportLinks;
    private readonly Action? _stateChanged;
    private bool _disposed;

    /// <param name="stateChanged">
    /// Told when a tray action changed the configuration, so the open window can show it.
    /// A tray toggle runs a reload of its own, which leaves the watcher nothing to report,
    /// so without this the window kept showing the old switch (audit W-03).
    /// </param>
    internal TrayController(
        AppModel model,
        Window window,
        Action requestQuit,
        ISupportLinkLauncher? supportLinks = null,
        Action? stateChanged = null)
    {
        _model = model;
        _window = window;
        _requestQuit = requestQuit;
        _supportLinks = supportLinks ?? SystemBrowserSupportLauncher.Shared;
        _stateChanged = stateChanged;
    }

    /// <summary>
    /// The tray's Help submenu (§7.14): the same two fixed report links Settings and
    /// the error screens offer. Windows has no menu bar, so this is where the macOS
    /// Help menu's entries live.
    /// </summary>
    internal const string HelpMenuTitle = "Help";

    /// <summary>Adds the Help submenu; <paramref name="open"/> runs when a link is chosen.</summary>
    internal static Forms.ToolStripMenuItem BuildHelpMenu(Action<SupportLink> open)
    {
        var help = new Forms.ToolStripMenuItem(HelpMenuTitle);
        foreach (var link in SupportLinks.All)
        {
            var item = new Forms.ToolStripMenuItem(link.Label)
            {
                ToolTipText = link.Audience,
                Tag = link,
            };
            var chosen = link;
            item.Click += (_, _) => open(chosen);
            help.DropDownItems.Add(item);
        }
        return help;
    }

    private void OpenSupportLink(SupportLink link)
    {
        if (_supportLinks.Open(link.Url)) return;
        _icon?.ShowBalloonTip(5000, "Kytto", SupportLinks.FallbackMessage(link), Forms.ToolTipIcon.Warning);
    }

    /// <summary>Idempotently makes the notification area match persisted settings.</summary>
    internal void Apply(KyttoSettings settings)
    {
        if (_disposed) return;
        if (!settings.TrayEnabled)
        {
            DisposeIcon();
            return;
        }

        if (_icon is null)
        {
            _ownedIcon = TrayIcon();
            _icon = new Forms.NotifyIcon
            {
                Icon = _ownedIcon,
                Visible = true,
                Text = "Kytto",
                ContextMenuStrip = new Forms.ContextMenuStrip(),
            };

            _icon.DoubleClick += (_, _) => Show();
            // The tooltip is built from the state at the time it is set. Asking again each time
            // the pointer reaches the icon keeps it in step with changes made in the window,
            // which the watcher does not always report (audit W-10).
            _icon.MouseMove += (_, _) => Refresh();
            // Rebuilt on open rather than kept in step, because the state behind it
            // changes from three directions — the window, the file watcher, and the
            // client itself — and a menu that is only ever right when it is visible
            // costs nothing to rebuild.
            _icon.ContextMenuStrip.Opening += (_, _) => Rebuild();
        }
        Refresh();
    }

    /// <summary>The app's own icon, in the size this machine's tray asks for.</summary>
    /// <remarks>
    /// Asked for by size rather than scaled down from one drawing: the notification
    /// area wants 16, 20 or 24 pixels depending on how the display is scaled, and
    /// <c>Kytto.ico</c> carries a frame drawn for each. The system icon remains the
    /// fallback, so an executable that somehow has no resources still leaves a tray
    /// item that can be clicked rather than none at all.
    /// </remarks>
    private static Icon TrayIcon()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        try
        {
            return Icon.ExtractIcon(executable, 0, Forms.SystemInformation.SmallIconSize.Width)
                ?? (Icon)SystemIcons.Application.Clone();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or ArgumentException or
                     System.ComponentModel.Win32Exception)
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    internal void Refresh()
    {
        if (_icon is { } icon) icon.Text = Summary();
    }

    private void Rebuild()
    {
        if (_icon?.ContextMenuStrip is not { } menu) return;
        menu.Items.Clear();

        var client = PreferredClient();
        var state = _model.Current();

        menu.Items.Add(new Forms.ToolStripMenuItem(Summary()) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (client is null)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("No client configured") { Enabled = false });
        }
        else
        {
            var descriptor = ClientRegistry.Descriptor(client.Value);
            menu.Items.Add(new Forms.ToolStripMenuItem($"In {descriptor.DisplayName}") { Enabled = false });

            var servers = state.Servers
                .Where(server => server.EnabledIn.GetValueOrDefault(client.Value, Enablement.Absent)
                                 != Enablement.Absent)
                .ToArray();

            if (servers.Length == 0)
            {
                menu.Items.Add(new Forms.ToolStripMenuItem("No servers here") { Enabled = false });
            }

            foreach (var server in servers)
            {
                var enabled = server.EnabledIn[client.Value] == Enablement.Enabled;
                // A plugin's server is shown for what is on, but belongs to the client
                // that installed it (§4); the toggle is refused natively as well.
                var item = new Forms.ToolStripMenuItem(server.Name)
                {
                    Checked = enabled,
                    Enabled = !server.HoldsPluginCopy(client.Value),
                };
                var id = server.Id;
                item.Click += (_, _) => Toggle(id, client.Value, !enabled);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        var open = new Forms.ToolStripMenuItem("Open Kytto");
        open.Click += (_, _) => Show();
        menu.Items.Add(open);
        menu.Items.Add(BuildHelpMenu(OpenSupportLink));

        var quit = new Forms.ToolStripMenuItem("Quit Kytto");
        quit.Click += (_, _) => _requestQuit();
        menu.Items.Add(quit);
    }

    private void Toggle(string serverId, ClientId client, bool enabled)
    {
        try
        {
            _model.SetEnabled(enabled, serverId, client);
            Refresh();
            _stateChanged?.Invoke();
        }
        catch (Exception error)
        {
            Log.Failure("tray toggle", error);
            // A refused write is worth saying out loud here: there is no error
            // state to render in a menu that has already closed.
            _icon?.ShowBalloonTip(5000, "Kytto", error.Message, Forms.ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// The client the tray acts on: the user's choice, or the one carrying the most
    /// servers, recomputed as things change.
    /// </summary>
    /// <remarks>
    /// Only an installed, writable client is a candidate: a read-only source would
    /// offer toggles that can only ever fail. A saved choice that is no longer a
    /// candidate — uninstalled since, or read-only — falls back to the automatic
    /// guess rather than leaving the tray with no client at all.
    /// </remarks>
    internal static ClientId? PreferredClient(string? savedChoice, IReadOnlyList<DiscoveredClient> clients)
    {
        var candidates = clients
            .Where(client => client.IsInstalled && !client.IsReadOnly && client.Id.BuiltIn is not null)
            .ToArray();
        if (ClientIds.FromRaw(savedChoice) is { } chosen &&
            candidates.Any(client => client.Id.BuiltIn == chosen))
        {
            return chosen;
        }

        return candidates
            .Where(client => client.ServerCount > 0)
            .OrderByDescending(client => client.ServerCount)
            .Select(client => client.Id.BuiltIn)
            .FirstOrDefault();
    }

    private ClientId? PreferredClient() =>
        PreferredClient(_model.Settings.TrayClient, _model.Current().Clients);

    private string Summary()
    {
        var servers = _model.Current().Servers;
        var active = servers.Count(server =>
            server.EnabledIn.Values.Contains(Enablement.Enabled));
        var failing = servers.Count(server => server.Health?.Status == Core.Health.HealthStatus.Failed);

        var text = $"Kytto — {active} of {servers.Count} active";
        return failing > 0 ? $"{text}, {failing} failing" : text;
    }

    private void Show() => Reveal(_window);

    /// <summary>
    /// Shows the window again after it was hidden to the tray, or brings it forward when a
    /// second launch asks for it (audit W-05).
    /// </summary>
    /// <remarks>
    /// The system restores to the state the window had before it was minimised, so a
    /// maximised window comes back maximised (audit W-11). Setting Normal lost that.
    /// </remarks>
    internal static void Reveal(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(window);
        window.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeIcon();
    }

    private void DisposeIcon()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}
