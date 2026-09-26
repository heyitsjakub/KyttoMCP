using System.IO;
using System.Reflection;
using System.Text;
using Kytto.App.Ipc;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Kytto.App.Web;

/// <summary>
/// Hosts the web UI. §3: the frame is native, only the content is web.
/// </summary>
/// <remarks>
/// <para>
/// The page is served from a virtual origin rather than <c>file://</c> for the
/// same reason the macOS shell registers a custom scheme: browsers treat every
/// file URL as an opaque origin and refuse to load ES modules across one, and §3.1
/// wants modules loaded directly with no build step.
/// </para>
/// <para>
/// Everything on that origin is answered here rather than by WebView2's
/// folder mapping, because <c>/icon/&lt;clientID&gt;.png</c> is generated rather
/// than read from disk, and a folder mapping answers first — a request it cannot
/// satisfy 404s before any handler sees it. One handler for both is also what the
/// macOS scheme handler does, so the two shells stay the same shape.
/// </para>
/// <para>
/// Everything a user would notice as foreign is turned off: the browser context
/// menu, swipe navigation, pinch zoom, the status bar popup.
/// </para>
/// </remarks>
internal sealed class WebHost(CommandRouter router)
{
    /// <summary>The origin the page is served on. Not a real host; nothing resolves it.</summary>
    private const string VirtualHost = "app";

    internal static readonly Uri TrustedOrigin = new($"https://{VirtualHost}/");

    private const string IconPrefix = "/icon/";

    private CoreWebView2Environment? _environment;
    private readonly string _root = WebRoot();

    internal async Task InitializeAsync(WebView2 webView)
    {
        // Beside the app's own data rather than next to the executable, which may
        // be somewhere the user cannot write.
        var userData = TestDataRoot() is { } testData
            ? Path.Combine(testData, "WebView2")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kytto",
                "WebView2");
        Directory.CreateDirectory(userData);

        _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await webView.EnsureCoreWebView2Async(_environment);

        var core = webView.CoreWebView2;
        core.AddWebResourceRequestedFilter($"https://{VirtualHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, args) =>
        {
            if (!CommandRouter.IsSameOrigin(args.Uri, TrustedOrigin)) args.Cancel = true;
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            if (!CommandRouter.IsSameOrigin(args.Uri, TrustedOrigin)) args.Cancel = true;
        };
        // The app has no browser surface. A page that asks for a popup or a camera,
        // location or notification permission is either compromised or broken, and
        // neither case should silently widen this native shell.
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) =>
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            args.Handled = true;
        };

        // Before the page's own modules, so `ipc.js` finds the bridge already in
        // place rather than racing it.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript());

        var settings = core.Settings;
        // Lets the page declare its own drag region with `app-region: drag`, which
        // is how the title bar strip keeps dragging the window while the web view
        // covers the whole frame. Without it the strip is inert and the window can
        // only be moved by its edges.
        settings.IsNonClientRegionSupportEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = IsDebug;
        settings.AreDevToolsEnabled = IsDebug;

        router.Attach(webView, TrustedOrigin);

        core.Navigate(new Uri(TrustedOrigin, "index.html").AbsoluteUri);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_environment is not { } environment) return;

        var path = new Uri(args.Request.Uri).AbsolutePath;

        if (path.StartsWith(IconPrefix, StringComparison.Ordinal))
        {
            args.Response = IconResponse(environment, path[IconPrefix.Length..]);
            return;
        }

        args.Response = FileResponse(environment, path);
    }

    /// <summary>Serves <c>GET /icon/&lt;clientID&gt;.png</c>.</summary>
    /// <remarks>
    /// Always an image where the id names a client, so the UI never branches. Where
    /// there is no icon to be had it 404s, and the page draws initials in the same
    /// square — the fallback <c>docs/ipc.md</c> names for a failed request.
    /// </remarks>
    private static CoreWebView2WebResourceResponse IconResponse(
        CoreWebView2Environment environment,
        string name)
    {
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        var png = ClientIcons.Png(Uri.UnescapeDataString(name));
        if (png is null)
        {
            return environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        return environment.CreateWebResourceResponse(
            new MemoryStream(png),
            200,
            "OK",
            // Cached because the matrix rebuilds its DOM on every keystroke in the
            // filter field, so this is asked for far more often than it changes —
            // and it never changes within a launch.
            "Content-Type: image/png\nCache-Control: max-age=86400");
    }

    private CoreWebView2WebResourceResponse FileResponse(
        CoreWebView2Environment environment,
        string path)
    {
        if (path.Length == 0 || path == "/") path = "/index.html";

        string candidate;
        try
        {
            var decoded = Uri.UnescapeDataString(path);
            candidate = Path.GetFullPath(Path.Combine(
                _root,
                decoded.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return environment.CreateWebResourceResponse(null, 400, "Bad Request", "");
        }

        // Reject anything that climbs out of the web root.
        if (!IsWithinRoot(_root, candidate))
        {
            return environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(candidate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }

        var headers = $"Content-Type: {MimeType(candidate)}\nX-Content-Type-Options: nosniff";
        if (Path.GetExtension(candidate).Equals(".html", StringComparison.OrdinalIgnoreCase))
        {
            headers += "\nContent-Security-Policy: default-src 'self'; connect-src 'none'; " +
                       "img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
                       "object-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'";
        }

        return environment.CreateWebResourceResponse(
            new MemoryStream(data),
            200,
            "OK",
            headers);
    }

    /// <summary>True only when a resolved path is the root or one of its children.</summary>
    internal static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    /// <remarks>
    /// The web types are spelled out rather than looked up: a registry lookup can
    /// come back with something a Windows install has been told to associate with
    /// <c>.js</c>, and a script served as anything but JavaScript does not run.
    /// </remarks>
    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    private static bool IsDebug =>
#if DEBUG
        true;
#else
        false;
#endif

    private static string? TestDataRoot()
    {
#if DEBUG
        var value = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
        return string.IsNullOrWhiteSpace(value) ? null : value;
#else
        return null;
#endif
    }

    /// <summary>Where <c>index.html</c> lives.</summary>
    /// <remarks>
    /// Shipped beside the executable. In a debug build the repository's own
    /// <c>web/</c> wins when one is above us, so editing a stylesheet and reloading
    /// does not mean rebuilding the app.
    /// </remarks>
    private static string WebRoot()
    {
#if DEBUG
        // From the parent up, not from here: the build copies `web/` beside the
        // executable, so a search that starts at the base directory always finds
        // that copy first and the repository's own is never reached — the edit
        // you just made is served only after the rebuild it was meant to save.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory).Parent;
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "web");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
        }
#endif
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "web"));
    }

    private static string BridgeScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Array.Find(assembly.GetManifestResourceNames(), n => n.EndsWith("bridge.js", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("bridge.js was not embedded in the assembly.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
