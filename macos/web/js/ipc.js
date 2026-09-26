// The web layer's only connection to the machine.
//
// Nothing else in web/ may touch window.webkit, read a path, or assume an OS.
// If a screen needs a capability it does not have, the answer is a new command
// in docs/ipc.md — not a way around this file (§3, §12).

const stub = globalThis.__kyttoStub ?? null;

export class IPCError extends Error {
  constructor({ code, message }) {
    super(message);
    this.name = 'IPCError';
    this.code = code;
  }
}

/**
 * Calls a native command and resolves with its data.
 * @param {string} command
 * @param {unknown} [payload]
 */
export async function invoke(command, payload = null) {
  if (stub) return stub(command, payload);

  const bridge = globalThis.webkit?.messageHandlers?.kytto;
  if (!bridge) {
    throw new IPCError({
      code: 'noBridge',
      message: 'Not running inside the Kytto shell. Open dev.html to work on the UI in a browser.',
    });
  }

  const raw = await bridge.postMessage({ command, payload });
  const envelope = JSON.parse(raw);
  if (!envelope.ok) throw new IPCError(envelope.error);
  return envelope.data;
}

const listeners = new Map();

/**
 * Subscribes to an event pushed from the native side.
 * @param {string} event
 * @param {(payload: unknown) => void} handler
 */
export function on(event, handler) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event).add(handler);
  return () => listeners.get(event)?.delete(handler);
}

// The native side calls this through evaluateJavaScript.
globalThis.__kytto = {
  emit(event, payload) {
    for (const handler of listeners.get(event) ?? []) handler(payload);
  },
};

/** True when running against fixtures rather than the real shell. */
export const isStubbed = stub !== null;

/**
 * Where the shell serves a client's application icon, or null when there is no
 * shell to serve one and the caller should draw its own square.
 *
 * Origin-relative on purpose: the page is served by the shell, so this stays
 * true of any shell that serves it, and the web layer still never sees a path it
 * has to interpret — `clientId` is the same opaque id every other command takes.
 *
 * @param {string} clientId
 */
export function clientIconURL(clientId) {
  if (stub) return null;
  return `/icon/${encodeURIComponent(clientId)}.png`;
}
