using System.Threading;
using System.Windows;

namespace Kytto.App;

/// <summary>
/// Application entry point.
/// </summary>
/// <remarks>
/// <para>
/// Fully qualified because the project also references Windows Forms — for the
/// tray item (§7.7), which has no WPF equivalent — and both frameworks spell
/// <c>Application</c> the same way.
/// </para>
/// <para>
/// Only one Kytto runs per session (audit W-05). With the tray item on, closing the
/// window leaves Kytto in the tray; opening it again from the Start menu would otherwise
/// start a second process with its own tray icon, watchers and restart counts writing
/// the same client configs. The second process asks the first to show its window and
/// exits.
/// </para>
/// </remarks>
public partial class App : System.Windows.Application
{
    private const string InstanceName = @"Local\Kytto.SingleInstance";
    private const string RevealName = @"Local\Kytto.Reveal";

    private Mutex? _instance;
    private EventWaitHandle? _reveal;
    private volatile bool _listening;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(initiallyOwned: true, InstanceName, out var first);
        if (!first)
        {
            SignalRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        _reveal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, RevealName);
        StartRevealListener(_reveal);

        base.OnStartup(e);
        // Created here rather than through StartupUri, so the single-instance check above
        // runs before any window exists.
        MainWindow = new global::Kytto.App.MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _listening = false;
        try
        {
            _reveal?.Dispose();
            _instance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner, which means there is nothing to release.
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Asks the running instance to show its window. Best effort: if it cannot be reached
    /// the second process still exits, which is the outcome that matters.
    /// </summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var reveal = EventWaitHandle.OpenExisting(RevealName);
            reveal.Set();
        }
        catch (Exception error) when (error is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            Log.Failure("second instance signal", error);
        }
    }

    private void StartRevealListener(EventWaitHandle reveal)
    {
        _listening = true;
        var thread = new Thread(() =>
        {
            while (_listening)
            {
                try
                {
                    if (!reveal.WaitOne()) continue;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is global::Kytto.App.MainWindow window) TrayController.Reveal(window);
                });
            }
        })
        {
            IsBackground = true,
            Name = "Kytto reveal listener",
        };
        thread.Start();
    }

    /// <summary>
    /// The last net under the UI thread (audit W-06). Without it an exception from a
    /// handler that is not wrapped closes the app with nothing shown and nothing logged.
    /// </summary>
    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Failure("unhandled exception", e.Exception);
        if (e.Exception is OutOfMemoryException) return;

        MessageBox.Show(
            "Kytto hit an unexpected error and kept running. The details were written to its log.",
            "Kytto",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
