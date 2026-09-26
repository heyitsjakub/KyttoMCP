// The Windows half of the IPC transport, injected before the page's own modules
// run. It exists so that `web/` carries no Windows-specific transport code and
// can stay in step with the macOS app's copy.
//
// docs/ipc.md describes the channel as `postMessage` returning the reply. WebKit
// hands that shape over directly (WKScriptMessageHandlerWithReply); WebView2 does
// not — `chrome.webview.postMessage` is one-way and replies arrive later on a
// separate event. The gap is a request id and a map, and it belongs here rather
// than in `web/js/ipc.js`, because it is a fact about this shell and the web
// layer is not allowed to know which shell it is running in (§3.2).
//
// Everything else about the contract is unchanged: one door, and the reply is a
// JSON *string*, so JavaScript receives exactly what C# encoded.

(() => {
  'use strict';

  const pending = new Map();
  let nextID = 0;

  // The page's own failures, forwarded to the shell's log. A command that throws
  // becomes a failure envelope the UI can render; a handler that throws before it
  // reaches `invoke` produces nothing at all, and silence is the one outcome
  // impossible to debug from the outside.
  const report = (what) => window.chrome.webview.postMessage({ kind: 'log', text: String(what) });

  window.addEventListener('error', (event) =>
    report(`${event.message} (${event.filename}:${event.lineno}:${event.colno})`));
  window.addEventListener('unhandledrejection', (event) =>
    report(`unhandled rejection: ${event.reason && event.reason.stack || event.reason}`));

  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data;
    // The event channel (`__kytto.emit`) arrives by script evaluation, the same
    // way it does on macOS, so anything without a request id is not ours.
    if (!data || typeof data.id !== 'number') return;

    const resolve = pending.get(data.id);
    if (!resolve) return;
    pending.delete(data.id);
    resolve(data.reply);
  });

  window.webkit = {
    messageHandlers: {
      kytto: {
        /**
         * @param {{ command: string, payload: unknown }} message
         * @returns {Promise<string>} the reply envelope, as a JSON string
         */
        postMessage(message) {
          const id = ++nextID;
          return new Promise((resolve) => {
            pending.set(id, resolve);
            window.chrome.webview.postMessage({
              id,
              command: message.command,
              payload: message.payload ?? null,
            });
          });
        },
      },
    },
  };
})();
