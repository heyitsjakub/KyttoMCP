// Bug reporting and interface recovery (§7.14).
//
// Every error state in the page offers the same two fixed destinations, and this
// is the only module that spells them. They are plain links with a fixed
// `href`: the shell's external-navigation policy opens exactly these two URLs in
// the system browser and refuses anything else, so no IPC command is needed and
// the page never gets a way to open an address of its own choosing.
//
// What never goes into a link, a rendered message or a command: an error text,
// a path, a log line, a configuration value, a version or any other detail of
// this machine. The reporter writes and submits the report themselves.

import { el } from './dom.js';

/** The private form on the project's site: no account needed. */
export const BUG_REPORT_URL = 'https://kytto.jakubhecht.sk/#report-bug';
/** The public issue chooser: visible to everyone, needs a GitHub account. */
export const GITHUB_ISSUE_URL = 'https://github.com/heyitsjakub/KyttoMCP/issues/new/choose';

export const SUPPORT_NOTE =
  'Reports open in your browser. No logs or configuration are attached automatically.';

/** `error.code` of the state an unhandled script failure produces. */
export const INTERFACE_FAILURE_CODE = 'interfaceFailure';
/** Deliberately generic: the exception behind it is never shown or sent. */
export const INTERFACE_FAILURE_MESSAGE =
  'Try again. If the problem continues, quit and reopen Kytto or report a bug.';

/**
 * A link to one of the two report destinations.
 *
 * @param {{ github?: boolean, className?: string }} [options]
 */
export function bugReportLink({ github = false, className = 'button subtle' } = {}) {
  const link = el('a', `${className} support-link`, github ? 'Report on GitHub…' : 'Report a bug…');
  link.href = github ? GITHUB_ISSUE_URL : BUG_REPORT_URL;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  link.title = github
    ? 'Open a public GitHub issue in your browser'
    : 'Open the private bug report form in your browser';
  return link;
}

/**
 * The private report link an error notice carries, or null for any other kind.
 * The notice's own message stays where it is; nothing from it is copied here.
 */
export function noticeReportLink(notice) {
  return notice?.kind === 'error' ? bugReportLink() : null;
}

/**
 * The screen shown when the first load fails or the page hits an unexpected
 * script error: what happened, a retry, and both ways to report it.
 *
 * `error.message` is rendered as text. It comes from a native command failure or
 * from the generic message above, never from a caught exception's own text.
 */
export function startupFailureView(error) {
  const box = el('div', 'placeholder error');
  box.append(el('p', null, error?.code === INTERFACE_FAILURE_CODE
    ? 'Kytto’s interface encountered a problem.'
    : 'Could not read your configurations.'));
  box.append(el('p', 'muted', error?.message ?? 'Unknown error.'));

  // `refresh` is what the click handler already maps to a full reload of the
  // state while the page is in its error phase.
  const retry = el('button', 'button', 'Try again');
  retry.dataset.action = 'refresh';
  const actions = el('div', 'support-actions');
  actions.append(retry, bugReportLink(), bugReportLink({ github: true }));
  box.append(actions);
  box.append(el('p', 'muted support-note', SUPPORT_NOTE));
  return box;
}

/** The state change an unhandled script failure applies: generic text only. */
export function interfaceFailureState() {
  return {
    phase: 'error',
    error: { code: INTERFACE_FAILURE_CODE, message: INTERFACE_FAILURE_MESSAGE },
    sheet: null,
  };
}

/**
 * The `error` a failed first load shows.
 *
 * A native command failure carries a string `code` and a message the shell wrote
 * for the user, so that message is shown. Anything else — a TypeError thrown
 * while applying the reply, say — has only the engine's own text, which can quote
 * a property, a value or a path, so it is replaced by the generic message.
 */
export function loadFailureError(error) {
  if (typeof error?.code === 'string' && typeof error?.message === 'string') {
    return { code: error.code, message: error.message };
  }
  return { code: INTERFACE_FAILURE_CODE, message: INTERFACE_FAILURE_MESSAGE };
}

/**
 * Turns unhandled script errors and promise rejections into one recovery screen.
 *
 * The event is only inspected for its type. Its message, stack and `reason` are
 * never read into state, rendered or sent anywhere — they may carry a path, a
 * config value or process output. The shell already logs the page's own failures
 * locally (see `Web/bridge.js`).
 *
 * @param {(changes: ReturnType<typeof interfaceFailureState>) => void} onFailure
 */
export function createInterfaceFailureGuard(onFailure) {
  let failed = false;

  function trip() {
    // The first failure shows the screen; the cascade that usually follows it
    // must not keep re-rendering over the user's retry.
    if (failed) return;
    failed = true;
    onFailure(interfaceFailureState());
  }

  return {
    /** `window` `error` listener. */
    error(event) {
      // Resource errors are not ErrorEvents; only script failures count.
      if (typeof ErrorEvent !== 'undefined' && !(event instanceof ErrorEvent)) return;
      // Chromium reports a harmless layout notification as an ErrorEvent.
      if (typeof event?.message === 'string' && event.message.startsWith('ResizeObserver loop')) return;
      trip();
    },
    /** `window` `unhandledrejection` listener. */
    rejection() {
      trip();
    },
    /** A retry starts a fresh attempt, so the next failure may show again. */
    reset() {
      failed = false;
    },
  };
}
