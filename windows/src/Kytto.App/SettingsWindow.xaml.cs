using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kytto.Core.Clients;
using Kytto.Core.Settings;

namespace Kytto.App;

/// <summary>
/// Everything §7.6 lets the user change that the web layer has no business
/// knowing about.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly List<OverrideRow> _overrides = [];
    private readonly List<CustomSourceRow> _customSources = [];

    /// <summary>One client's path override, bound to a row in the list.</summary>
    private sealed class OverrideRow
    {
        public required ClientId Id { get; init; }
        public required string DisplayName { get; init; }
        public string Path { get; set; } = "";
    }

    private sealed class CustomSourceRow
    {
        public required Guid Id { get; init; }
        public string DisplayName { get; set; } = "";
        public required string Path { get; init; }
        public ConfigurationScope Scope { get; set; }
        public string ScopeLabel { get; set; } = "";
        public IReadOnlyList<ConfigurationScope> ScopeOptions { get; } =
            Enum.GetValues<ConfigurationScope>();
    }

    internal SettingsWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();

        var settings = model.Settings;

        ThemeBox.SelectedIndex = settings.Theme switch
        {
            Theme.Light => 1,
            Theme.Dark => 2,
            _ => 0,
        };
        RetentionBox.Text = settings.BackupRetention.ToString(CultureInfo.CurrentCulture);
        ThresholdBox.Text = settings.TokenWarningThreshold.ToString(CultureInfo.CurrentCulture);
        TrayBox.IsChecked = settings.TrayEnabled;
        TrayClientBox.Items.Add(new ComboBoxItem { Content = "Automatic (most servers)", Tag = "" });
        foreach (var descriptor in ClientRegistry.All)
        {
            TrayClientBox.Items.Add(new ComboBoxItem
            {
                Content = descriptor.DisplayName,
                Tag = descriptor.Id.Raw(),
            });
        }
        var trayClient = ValidTrayClient(settings.TrayClient) ?? "";
        TrayClientBox.SelectedItem = TrayClientBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as string ?? "") == trayClient)
            // A hand-edited setting, or an id removed in a future release, must not
            // make Ctrl+, crash the application. Automatic is the safe fallback.
            ?? TrayClientBox.Items[0];
        LoginBox.IsChecked = model.LaunchAtLoginEnabled;

        foreach (var descriptor in ClientRegistry.All)
        {
            _overrides.Add(new OverrideRow
            {
                Id = descriptor.Id,
                DisplayName = descriptor.DisplayName,
                Path = settings.PathOverride(descriptor.Id) ?? "",
            });
        }
        OverrideList.ItemsSource = _overrides;

        foreach (var source in settings.CustomConfigSources)
        {
            _customSources.Add(new CustomSourceRow
            {
                Id = source.Id,
                DisplayName = source.DisplayName,
                Path = source.Path,
                Scope = source.Scope,
                ScopeLabel = source.ScopeLabel,
            });
        }
        CustomSourceList.ItemsSource = _customSources;
    }

    internal static string? ValidTrayClient(string? raw) =>
        ClientIds.FromRaw(raw)?.Raw();

    private void OnBrowse(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: OverrideRow row }) return;

        var descriptor = ClientRegistry.Descriptor(row.Id);
        var dialog = new OpenFileDialog
        {
            Title = $"Where does {descriptor.DisplayName} keep its configuration?",
            Filter = descriptor.EditableServerMap?.Format == ConfigFormat.Toml
                ? "TOML files (*.toml)|*.toml|All files (*.*)|*.*"
                : "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;
        row.Path = dialog.FileName;
        // The rows are plain objects rather than observable, so the list is told.
        OverrideList.Items.Refresh();
    }

    private void OnAddCustomSource(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach a read-only MCP configuration source",
            Filter = "MCP configuration (*.json;*.jsonc;*.toml)|*.json;*.jsonc;*.toml|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        _customSources.Add(new CustomSourceRow
        {
            Id = Guid.NewGuid(),
            DisplayName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName),
            Path = dialog.FileName,
            Scope = ConfigurationScope.Global,
        });
        CustomSourceList.Items.Refresh();
    }

    private void OnDetachCustomSource(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CustomSourceRow row }) return;
        var answer = MessageBox.Show(
            this,
            $"Detach “{row.DisplayName}” from Kytto?\n\nThe configuration file will not be deleted or changed.",
            "Detach custom source?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        _customSources.Remove(row);
        CustomSourceList.Items.Refresh();
    }

    private void OnSave(object sender, RoutedEventArgs args)
    {
        PathError.Visibility = Visibility.Collapsed;
        CustomSourceError.Visibility = Visibility.Collapsed;
        var theme = ((ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "light" => Theme.Light,
            "dark" => Theme.Dark,
            _ => Theme.System,
        };

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _overrides)
        {
            var path = row.Path.Trim();
            if (path.Length == 0) continue;
            if (!KyttoSettings.IsUsablePath(path))
            {
                PathError.Text = $"{row.DisplayName}: enter a complete drive path such as C:\\Users\\… or a UNC path such as \\\\server\\share\\….";
                PathError.Visibility = Visibility.Visible;
                return;
            }
            overrides[row.Id.Raw()] = path;
        }
        var trayClient = (TrayClientBox.SelectedItem as ComboBoxItem)?.Tag as string;

        // Out-of-range numbers are clamped rather than rejected: Settings sanitises
        // on the way in anyway, and an error dialog over a typo in a spinner is not
        // worth the interruption.
        try
        {
            var customSources = _customSources.Select(row => new CustomConfigSource(
                row.Id,
                row.DisplayName.Trim(),
                row.Path,
                row.Scope,
                row.Scope == ConfigurationScope.Global ? "" : row.ScopeLabel.Trim())).ToArray();

            // Transform the model's current value at Save time. This deliberately
            // preserves onboarding (and other settings) changed while this window
            // was open instead of writing a stale constructor snapshot.
            _model.UpdateSettings(settings => settings with
            {
                Theme = theme,
                BackupRetention = Number(RetentionBox.Text, settings.BackupRetention),
                TokenWarningThreshold = Number(ThresholdBox.Text, settings.TokenWarningThreshold),
                TrayEnabled = TrayBox.IsChecked == true,
                TrayClient = string.IsNullOrEmpty(trayClient) ? null : trayClient,
                ClientPathOverrides = overrides,
                CustomConfigSources = customSources,
                LaunchAtLogin = LoginBox.IsChecked == true,
            });
        }
        catch (SettingsValidationException error)
        {
            CustomSourceError.Text = error.Message;
            CustomSourceError.Visibility = Visibility.Visible;
            CustomSourceError.BringIntoView();
            return;
        }
        catch (Exception error) when (
            error is SettingsTransactionException or IOException or UnauthorizedAccessException or
                     InvalidOperationException or System.Security.SecurityException)
        {
            CustomSourceError.Text = error.Message;
            CustomSourceError.Visibility = Visibility.Visible;
            CustomSourceError.BringIntoView();
            return;
        }

        DialogResult = true;
    }

    private static int Number(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? value
            : fallback;
}
