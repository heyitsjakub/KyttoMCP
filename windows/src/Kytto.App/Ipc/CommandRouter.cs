using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;

namespace Kytto.App.Ipc;

/// <summary>
/// The single typed channel between the web UI and the native shell (§3).
/// </summary>
/// <remarks>
/// <para>
/// Every native capability is one named command. The web layer has no other way
/// to reach the machine — no file access, no process spawning, no paths it has to
/// understand. Adding a capability means adding a command here and a row in
/// <c>docs/ipc.md</c>, never widening this surface in some other way.
/// </para>
/// <para>
/// Replies are JSON <em>strings</em> rather than bridged objects, so what
/// JavaScript receives is exactly what C# encoded, with no marshalling in
/// between. <c>Web/bridge.js</c> is the other half: WebView2's message channel is
/// one-way, so the request id this class echoes back is what turns it into the
/// request/response shape <c>docs/ipc.md</c> describes.
/// </para>
/// </remarks>
internal sealed class CommandRouter
{
    private readonly Dictionary<string, Func<JsonElement, Task<string>>> _handlers = new(StringComparer.Ordinal);
    private WebView2? _webView;
    private Uri? _trustedOrigin;

    internal void Attach(WebView2 webView, Uri trustedOrigin)
    {
        _webView = webView;
        _trustedOrigin = trustedOrigin;
        webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
    }

    // MARK: - Registration

    /// <summary>Registers a command that takes a payload.</summary>
    internal void Register<TPayload, TResponse>(string name, Func<TPayload, Task<TResponse>> handler)
    {
        _handlers[name] = async payload =>
        {
            try
            {
                var decoded = payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? default
                    : payload.Deserialize<TPayload>(Envelope.Json);
                if (decoded is null)
                {
                    throw new JsonException($"{name} needs a payload.");
                }
                return Envelope.Success(await handler(decoded).ConfigureAwait(true));
            }
            catch (Exception error)
            {
                Log.Failure(name, error);
                return Envelope.Failure(error);
            }
        };
    }

    /// <summary>Registers a command that takes no payload.</summary>
    internal void Register<TResponse>(string name, Func<Task<TResponse>> handler)
    {
        _handlers[name] = async _ =>
        {
            try
            {
                return Envelope.Success(await handler().ConfigureAwait(true));
            }
            catch (Exception error)
            {
                Log.Failure(name, error);
                return Envelope.Failure(error);
            }
        };
    }

    /// <summary>Registers a command that takes no payload and does not await.</summary>
    internal void Register<TResponse>(string name, Func<TResponse> handler) =>
        Register(name, () => Task.FromResult(handler()));

    /// <summary>Exercises the exact registered IPC handler without a WebView.</summary>
    internal Task<string> InvokeForTests(string command, string? payloadJson = null)
    {
        if (!_handlers.TryGetValue(command, out var handler))
        {
            return Task.FromResult(Envelope.Failure(IpcException.UnknownCommand(command)));
        }
        if (payloadJson is null) return handler(default);

        using var document = JsonDocument.Parse(payloadJson);
        return handler(document.RootElement.Clone());
    }

    /// <summary>Registers a command that takes a payload and does not await.</summary>
    internal void Register<TPayload, TResponse>(string name, Func<TPayload, TResponse> handler) =>
        Register<TPayload, TResponse>(name, payload => Task.FromResult(handler(payload)));

    // MARK: - Native to web

    /// <summary>Pushes an event into the web layer.</summary>
    /// <remarks>
    /// By script evaluation rather than the message channel, so it lands on
    /// <c>window.__kytto.emit</c> exactly as it does on macOS — and so
    /// <c>bridge.js</c> can tell a reply from an event by looking for a request id.
    /// </remarks>
    internal async Task EmitAsync<T>(string eventName, T payload)
    {
        if (_webView?.CoreWebView2 is not { } core) return;

        var json = JsonSerializer.Serialize(payload, Envelope.Json);
        var script = $"window.__kytto && window.__kytto.emit({JsStringLiteral(eventName)}, {json});";
        try
        {
            await core.ExecuteScriptAsync(script).ConfigureAwait(true);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            // The window closed between the check and the call. Nothing to tell.
        }
    }

    private static string JsStringLiteral(string value) =>
        JsonSerializer.Serialize(value, Envelope.Json);

    // MARK: - Message handling

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
    {
        // AddScriptToExecuteOnDocumentCreated runs for every document a WebView
        // reaches, not only Kytto's. Even though WebHost also blocks navigation,
        // the privileged side of the boundary verifies the sender independently.
        if (_trustedOrigin is not { } trustedOrigin || !IsSameOrigin(args.Source, trustedOrigin)) return;

        var incoming = DecodeIncoming(args.WebMessageAsJson, out var pageLog);
        if (pageLog is not null)
        {
            Log.Write($"[page] {pageLog}");
            return;
        }
        if (incoming is null) return;

        Log.Write($"-> {incoming.Command} (#{incoming.Id})");

        var reply = _handlers.TryGetValue(incoming.Command, out var handler)
            ? await handler(incoming.Payload).ConfigureAwait(true)
            : Envelope.Failure(IpcException.UnknownCommand(incoming.Command));

        Reply(incoming.Id, reply);
    }

    /// <summary>Decodes an untrusted web message without letting its shape throw.</summary>
    /// <remarks>
    /// A JavaScript bug used to be able to terminate the WPF dispatcher by sending
    /// a string or out-of-range request id: <c>GetInt32</c> throws exceptions that
    /// an <c>async void</c> event cannot return to WebView2. Malformed input is now
    /// ignored at the boundary.
    /// </remarks>
    internal static IncomingMessage? DecodeIncoming(string json, out string? pageLog)
    {
        pageLog = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Not a command: the page reporting one of its own failures.
            if (root.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String &&
                kind.GetString() == "log")
            {
                if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    pageLog = text.GetString();
                }
                return null;
            }

            if (!root.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.Number ||
                !idElement.TryGetInt32(out var id) ||
                !root.TryGetProperty("command", out var commandElement) ||
                commandElement.ValueKind != JsonValueKind.String ||
                commandElement.GetString() is not { Length: > 0 } command)
            {
                return null;
            }

            // Cloned because the document is disposed before the handler runs.
            var payload = root.TryGetProperty("payload", out var value) ? value.Clone() : default;
            return new IncomingMessage(id, command, payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsSameOrigin(string source, Uri trustedOrigin) =>
        Uri.TryCreate(source, UriKind.Absolute, out var parsed) &&
        string.Equals(parsed.Scheme, trustedOrigin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(parsed.IdnHost, trustedOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        parsed.Port == trustedOrigin.Port;

    private void Reply(int id, string envelope)
    {
        if (_webView?.CoreWebView2 is not { } core) return;

        var message = JsonSerializer.Serialize(new ReplyMessage(id, envelope), Envelope.Json);
        try
        {
            core.PostWebMessageAsJson(message);
        }
        catch (Exception error) when (error is InvalidOperationException or COMException)
        {
            // Window closed while the command was running.
        }
    }

    private sealed record ReplyMessage(int Id, string Reply);

    internal sealed record IncomingMessage(int Id, string Command, JsonElement Payload);
}
