using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace Kytto.App.Tests;

public sealed class NativeUiSmokeTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-ui-tests", Guid.NewGuid().ToString("N"));

#if DEBUG
    [Fact]
    public void ManualAddWithoutAClientShowsValidationAndWritesNoConfig()
    {
        Directory.CreateDirectory(_home);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var oldHome = Environment.GetEnvironmentVariable("KYTTO_TEST_HOME");
            var oldData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", _home);
                Environment.SetEnvironmentVariable(
                    "KYTTO_TEST_DATA_ROOT", Path.Combine(_home, "KyttoData"));

                var application = new System.Windows.Application
                {
                    ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
                };
                var window = new MainWindow();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var captionChecked = false;
                var submitted = false;
                var lastState = "WebView2 was not initialized";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += async (_, _) =>
                {
                    timer.Stop();
                    try
                    {
                        if (DateTime.UtcNow >= deadline)
                        {
                            var logPath = Path.Combine(_home, "KyttoData", "kytto.log");
                            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "no native log";
                            throw new TimeoutException(
                                $"The native window did not finish the manual Add flow. Last state: {lastState}\nLog:\n{log}");
                        }
                        if (window.Web.CoreWebView2 is null)
                        {
                            timer.Start();
                            return;
                        }

                        if (!captionChecked)
                        {
                            Assert.Same(window, window.Caption.HostWindow);
                            window.Caption.MaximiseButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                                System.Windows.Controls.Button.ClickEvent));
                            Assert.Equal(System.Windows.WindowState.Maximized, window.WindowState);
                            window.Caption.MaximiseButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                                System.Windows.Controls.Button.ClickEvent));
                            Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);
                            captionChecked = true;
                        }

                        if (!submitted)
                        {
                            var result = await window.Web.CoreWebView2.ExecuteScriptAsync("""
                                (() => {
                                  const add = document.querySelector('[data-action="add-server"]');
                                  if (!add) {
                                    const finish = document.querySelector('[data-action="finish-onboarding"]');
                                    if (finish) finish.click();
                                    return 'waiting: ' + document.body.innerText.slice(0, 300);
                                  }
                                  add.click();
                                  const manual = document.querySelector('[data-action="form-manual"]');
                                  if (!manual) return 'waiting: manual button missing';
                                  manual.click();
                                  const enter = (selector, value) => {
                                    const field = document.querySelector(selector);
                                    if (!field) return false;
                                    field.value = value;
                                    field.dispatchEvent(new Event('input', { bubbles: true }));
                                    return true;
                                  };
                                  // Every input renders from state and replaces the
                                  // form DOM, so query each next control afresh.
                                  if (!enter('[data-action="draft-name"]', 'ui-smoke')) return 'missing name';
                                  if (!enter('[data-action="draft-command"]', 'npx')) return 'missing command';
                                  const submit = document.querySelector('[data-action="submit-form"]');
                                  if (!submit) return 'missing submit';
                                  submit.click();
                                  return 'submitted';
                                })()
                                """);
                            var state = JsonSerializer.Deserialize<string>(result);
                            lastState = state ?? "script returned null";
                            if (state is null || lastState.StartsWith("waiting", StringComparison.Ordinal))
                            {
                                timer.Start();
                                return;
                            }
                            Assert.Equal("submitted", state);
                            submitted = true;
                            timer.Start();
                            return;
                        }

                        var snapshotJson = await window.Web.CoreWebView2.ExecuteScriptAsync(
                            "[document.querySelector('.form-errors')?.textContent ?? null, " +
                            "document.body.innerText.slice(0, 500) + '\\nsubmit action: ' + " +
                            "(document.querySelector('.sheet-footer button:last-child')?.dataset.action ?? 'missing')]");
                        var snapshot = JsonSerializer.Deserialize<string?[]>(snapshotJson) ?? [];
                        var error = snapshot.ElementAtOrDefault(0);
                        if (error is null)
                        {
                            lastState = snapshot.ElementAtOrDefault(1) ?? "the document body was empty";
                            timer.Start();
                            return;
                        }

                        Assert.Equal("Kytto", window.Title);
                        Assert.Equal("Choose at least one client to add this server to.", error);
                        window.Close();
                        Assert.False(window.IsVisible);
                        Assert.False(application.Dispatcher.HasShutdownStarted);
                        window.RequestQuit();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        window.RequestQuit();
                    }
                };

                window.Show();
                timer.Start();
                application.Run();
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", oldHome);
                Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", oldData);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The native UI smoke test did not exit.");
        if (failure is not null) throw failure;

        var forbidden = Directory.GetFiles(_home, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith("mcp.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("config.toml", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Backups", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(forbidden);
    }
#endif

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary profile is not worth hiding the assertion result.
        }
    }
}
