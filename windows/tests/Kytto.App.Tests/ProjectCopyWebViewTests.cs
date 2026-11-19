#if DEBUG
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using KyttoJson = Kytto.Core.Json.JsonDocument;

namespace Kytto.App.Tests;

/// <summary>
/// The shared web UI driving the Windows backend in the real window: definition
/// sources in the inspector, a project-definition import and its Undo — what the macOS
/// app pins in <c>projectCopyAndUndoThroughWebView</c>.
/// </summary>
/// <remarks>
/// Debug only and fully isolated, like the other native UI tests: the profile is a
/// temporary <c>KYTTO_TEST_HOME</c>. Creates no <c>System.Windows.Application</c>, so it
/// runs before the native smoke test (<see cref="NativeUiSmokeLastOrderer"/>).
/// </remarks>
public sealed class ProjectCopyWebViewTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-project-copy-ui", Guid.NewGuid().ToString("N"));

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    private string Cursor => Path.Combine(_home, ".cursor", "mcp.json");

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ImportAProjectDefinitionAndUndoItThroughTheSharedUi()
    {
        var folder = Path.Combine(_home, "two");
        var source = Path.Combine(folder, ".mcp.json");
        Write(Cursor, """{"theme":"dark","mcpServers":{}}""");
        Write(source,
            """{"mcpServers":{"Demo":{"command":"second","args":["--mode"],"env":{"REF":"${env:SOURCE_TOKEN}","TOKEN":"private-fixture"}}}}""");
        Write(Path.Combine(_home, ".claude.json"), """{"mcpServers":{"Demo":{"command":"user"}}}""");
        var data = Path.Combine(_home, "KyttoData");
        string sourceId;
        using (var setup = new AppModel(new KyttoPaths(data), _home, new NoInstalledApps(), new InMemorySecretStore(), watchConfigs: false))
        {
            var settings = setup.UpdateSettings(current => current with
            {
                HasCompletedOnboarding = true,
                ProjectFolders = [new ProjectFolder(Guid.NewGuid(), folder)],
            });
            sourceId = ClientKey.Custom(ProjectFolders.SourceId(settings.ProjectFolders[0].Id, ProjectFolders.Files[0])).Raw();
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var oldHome = Environment.GetEnvironmentVariable("KYTTO_TEST_HOME");
            var oldData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", _home);
                Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", data);
                var window = new MainWindow();
                var dispatcher = Dispatcher.CurrentDispatcher;
                window.Show();
                _ = dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await Run(window, sourceId, source);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    finally
                    {
                        window.RequestQuit();
                        dispatcher.InvokeShutdown();
                    }
                });
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                failure ??= error;
            }
            finally
            {
                Environment.SetEnvironmentVariable("KYTTO_TEST_HOME", oldHome);
                Environment.SetEnvironmentVariable("KYTTO_TEST_DATA_ROOT", oldData);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The project copy scenario did not finish.");
        if (failure is not null) throw failure;
    }

    private async Task Run(MainWindow window, string sourceId, string source)
    {
        await Click(window, "[data-action=open-server][data-server-id=demo]");
        await Wait(window, ".definition-sources");
        var badges = await Eval(window, "document.body.textContent");
        Assert.Contains("multiple scopes", badges, StringComparison.Ordinal);

        await Click(window, "[data-action=open-project-copy]");
        await Eval(window, $$"""
            (() => {
              for (const [action, value] of [['project-copy-source', '{{sourceId}}'], ['project-copy-target', 'cursor']]) {
                const select = document.querySelector('[data-action=' + action + ']');
                select.value = value;
                select.dispatchEvent(new Event('change', { bubbles: true }));
              }
              return 'set';
            })()
            """);
        await Click(window, "[data-action=preview-project-copy]");
        await Wait(window, "[data-action=apply-project-copy]");
        var sheet = await Eval(window, "document.querySelector('.sheet').textContent") ?? "";
        Assert.Contains(source, sheet, StringComparison.Ordinal);
        Assert.Contains("SOURCE_TOKEN", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("private-fixture", sheet, StringComparison.Ordinal);

        await Click(window, "[data-action=apply-project-copy]");
        await Wait(window, "[data-action=undo-matrix-write]");
        Assert.Equal("second", KyttoJson.Parse(File.ReadAllText(Cursor)).Root["mcpServers"]?["Demo"]?["command"]?.StringValue);

        await Click(window, "[data-action=undo-matrix-write]");
        await Wait(window, "[data-action=toggle][data-server-id=demo][data-client-id=cursor][data-state=absent]");
        var cursor = KyttoJson.Parse(File.ReadAllText(Cursor)).Root;
        Assert.Null(cursor["mcpServers"]?["Demo"]);
        Assert.Equal("dark", cursor["theme"]?.StringValue);
    }

    private static async Task<string?> Eval(MainWindow window, string script) =>
        JsonSerializer.Deserialize<string?>(await window.Web.CoreWebView2.ExecuteScriptAsync(script));

    private static async Task Wait(MainWindow window, string selector, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var query = JsonSerializer.Serialize(selector);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (window.Web.CoreWebView2 is not null &&
                    await Eval(window, $"String(Boolean(document.querySelector({query})))") == "true")
                {
                    return;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The control is still starting; ask again.
            }
            await Task.Delay(100);
        }
        var text = window.Web.CoreWebView2 is null ? "no web view" : await Eval(window, "document.body.innerText.slice(0, 600)");
        throw new TimeoutException($"Control did not appear: {selector}\n{text}");
    }

    private static async Task Click(MainWindow window, string selector)
    {
        await Wait(window, selector);
        await Eval(window, $"(document.querySelector({JsonSerializer.Serialize(selector)}).click(), 'clicked')");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // WebView2 may still hold its profile for a moment; a leftover temp folder is harmless.
        }
    }
}
#endif
