import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  BUG_REPORT_URL,
  GITHUB_ISSUE_URL,
  INTERFACE_FAILURE_CODE,
  INTERFACE_FAILURE_MESSAGE,
  SUPPORT_NOTE,
  bugReportLink,
  createInterfaceFailureGuard,
  interfaceFailureState,
  loadFailureError,
  noticeReportLink,
  startupFailureView,
} from '../support.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, '..', '..');

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.dataset = {};
    this.className = '';
    this.children = [];
    this.textContent = '';
  }

  append(...children) {
    this.children.push(...children);
  }

  /** Every element in the subtree, this one first. */
  all() {
    return [this, ...this.children.flatMap((child) => (child instanceof FakeElement ? child.all() : []))];
  }

  /** All text in the subtree, which is everything a user could read. */
  text() {
    return this.all().map((node) => node.textContent).join('\n');
  }
}

globalThis.document = { createElement: (tagName) => new FakeElement(tagName) };

const links = (view) => view.all().filter((node) => node.tagName === 'A');

test('the two report links are fixed and carry no data', () => {
  assert.equal(BUG_REPORT_URL, 'https://kytto.jakubhecht.sk/#report-bug');
  assert.equal(GITHUB_ISSUE_URL, 'https://github.com/heyitsjakub/KyttoMCP/issues/new/choose');

  const bug = new URL(BUG_REPORT_URL);
  assert.equal(bug.protocol, 'https:');
  assert.equal(bug.pathname, '/');
  assert.equal(bug.search, '');
  assert.equal(bug.hash, '#report-bug');
  assert.equal(bug.username + bug.password, '');

  const github = new URL(GITHUB_ISSUE_URL);
  assert.equal(github.protocol, 'https:');
  assert.equal(github.host, 'github.com');
  assert.equal(github.search, '');
  assert.equal(github.hash, '');
  assert.equal(github.username + github.password, '');
});

test('a report link opens outside the page and only ever to its fixed address', () => {
  const bug = bugReportLink();
  const github = bugReportLink({ github: true });

  assert.equal(bug.tagName, 'A');
  assert.equal(bug.href, BUG_REPORT_URL);
  assert.equal(bug.textContent, 'Report a bug…');
  assert.equal(github.href, GITHUB_ISSUE_URL);
  assert.equal(github.textContent, 'Report on GitHub…');
  for (const link of [bug, github]) {
    assert.equal(link.target, '_blank');
    assert.equal(link.rel, 'noopener noreferrer');
    assert.match(link.className, /\bsupport-link\b/);
    // A link, not a button: nothing in the page can intercept it and build a URL.
    assert.equal(link.dataset.action, undefined);
  }
});

test('only an error notice offers the private report link', () => {
  assert.equal(noticeReportLink({ kind: 'error', message: 'Could not write.' }).href, BUG_REPORT_URL);
  assert.equal(noticeReportLink({ kind: 'info', message: 'Reverted.' }), null);
  assert.equal(noticeReportLink({ kind: 'warning', message: 'Changed on disk.' }), null);
  assert.equal(noticeReportLink(null), null);
});

test('the notice link does not depend on, and never carries, the notice text', () => {
  const message = 'EACCES C:\\Users\\someone\\secret.json token=abc123';
  const link = noticeReportLink({ kind: 'error', message });

  assert.equal(link.href, BUG_REPORT_URL);
  assert.ok(!JSON.stringify(link).includes('abc123'));
  assert.ok(!JSON.stringify(link).includes('someone'));
});

test('the startup failure screen offers retry and both report links', () => {
  const view = startupFailureView({ code: 'unknown', message: 'Could not read the settings.' });

  const retry = view.all().find((node) => node.tagName === 'BUTTON');
  assert.equal(retry.textContent, 'Try again');
  // The click handler turns this into a full reload while the page is in its
  // error phase; the screen must not invent a second retry action.
  assert.equal(retry.dataset.action, 'refresh');
  assert.deepEqual(links(view).map((link) => link.href), [BUG_REPORT_URL, GITHUB_ISSUE_URL]);
  assert.match(view.text(), /Could not read your configurations\./);
  assert.match(view.text(), /Could not read the settings\./);
  assert.ok(view.text().includes(SUPPORT_NOTE));
});

test('the startup failure screen says what is private and what is public, and that nothing is attached', () => {
  assert.match(SUPPORT_NOTE, /No logs or configuration are attached automatically/);
  assert.match(bugReportLink().title, /private/i);
  assert.match(bugReportLink({ github: true }).title, /public/i);
});

test('an interface failure is described generically on the same screen', () => {
  const view = startupFailureView(interfaceFailureState().error);

  assert.match(view.text(), /interface encountered a problem/);
  assert.ok(view.text().includes(INTERFACE_FAILURE_MESSAGE));
  assert.equal(links(view).length, 2);
});

test('a failed first load keeps a native message but never an engine exception text', () => {
  assert.deepEqual(
    loadFailureError({ code: 'ioFailure', message: 'The settings file is unreadable.' }),
    { code: 'ioFailure', message: 'The settings file is unreadable.' },
  );

  const engine = loadFailureError(new TypeError("Cannot read properties of undefined (reading 'C:\\Users\\x')"));
  assert.deepEqual(engine, { code: INTERFACE_FAILURE_CODE, message: INTERFACE_FAILURE_MESSAGE });
  // A DOMException has a numeric `code`; it is an engine error too.
  assert.equal(loadFailureError({ code: 11, message: 'InvalidStateError: C:\\x' }).message, INTERFACE_FAILURE_MESSAGE);
  assert.equal(loadFailureError(undefined).message, INTERFACE_FAILURE_MESSAGE);
});

test('an unhandled script error shows generic recovery and exposes no exception text', () => {
  const states = [];
  const guard = createInterfaceFailureGuard((changes) => states.push(changes));
  const error = new Error('secret token=abc123 at C:\\Users\\someone\\repo');

  const details = { message: error.message, filename: 'C:\\Users\\someone\\x.js', error };
  // Newer runtimes define ErrorEvent, and the guard only counts real ones.
  guard.error(typeof ErrorEvent === 'undefined' ? details : new ErrorEvent('error', details));

  assert.equal(states.length, 1);
  assert.equal(states[0].phase, 'error');
  assert.equal(states[0].sheet, null);
  assert.deepEqual(states[0].error, { code: INTERFACE_FAILURE_CODE, message: INTERFACE_FAILURE_MESSAGE });

  const serialized = JSON.stringify(states[0]);
  assert.ok(!serialized.includes('abc123'));
  assert.ok(!serialized.includes('someone'));
  assert.ok(!startupFailureView(states[0].error).text().includes('abc123'));
});

test('an unhandled promise rejection shows the same generic recovery', () => {
  const states = [];
  const guard = createInterfaceFailureGuard((changes) => states.push(changes));

  guard.rejection({ reason: new Error('password=hunter2') });

  assert.equal(states.length, 1);
  assert.equal(states[0].error.message, INTERFACE_FAILURE_MESSAGE);
  assert.ok(!JSON.stringify(states[0]).includes('hunter2'));
});

test('a cascade of failures shows the screen once, and a retry arms the next one', () => {
  const states = [];
  const guard = createInterfaceFailureGuard((changes) => states.push(changes));

  guard.error({ message: 'first' });
  guard.rejection({});
  guard.error({ message: 'third' });
  assert.equal(states.length, 1);

  guard.reset();
  guard.rejection({});
  assert.equal(states.length, 2);
});

test('a harmless ResizeObserver notification is not a failure', () => {
  const states = [];
  const guard = createInterfaceFailureGuard((changes) => states.push(changes));

  guard.error({ message: 'ResizeObserver loop completed with undelivered notifications.' });

  assert.equal(states.length, 0);
});

test('main.js and the matrix wire the shared recovery in, and Try again reloads the state', async () => {
  const [main, matrix, support] = await Promise.all([
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'matrix.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'support.js'), 'utf8'),
  ]);

  assert.match(main, /createInterfaceFailureGuard\(\(changes\) => setState\(changes\)\)/);
  assert.match(main, /addEventListener\('error', \(event\) => interfaceGuard\.error\(event\)\)/);
  assert.match(main, /addEventListener\('unhandledrejection', \(\) => interfaceGuard\.rejection\(\)\)/);
  // A fresh load re-arms the guard and shows only a generic failure message.
  assert.match(main, /async function load\(\) \{\s*interfaceGuard\.reset\(\);/);
  assert.match(main, /phase: 'error', error: loadFailureError\(error\)/);
  // The retry the failure screen offers reloads everything while in the error phase.
  assert.match(main, /case 'refresh':\s*if \(state\.phase === 'error'\) await load\(\);/);

  assert.match(matrix, /startupFailureView\(error\)/);
  assert.match(matrix, /noticeReportLink\(current\)/);

  // No IPC: the links are plain anchors and the shell's navigation policy opens them.
  assert.doesNotMatch(support, /\binvoke\(/);
  assert.doesNotMatch(support, /\b(?:innerHTML|outerHTML|insertAdjacentHTML|document\.write)\b/);
  assert.doesNotMatch(support, /\b(?:webkit|chrome\.webview)\b/);
  assert.doesNotMatch(support, /\bfetch\s*\(|XMLHttpRequest|sendBeacon|window\.open/);
});

test('the page reads no exception property into anything it renders or sends', async () => {
  const support = await readFile(path.join(webRoot, 'js', 'support.js'), 'utf8');
  // The guard's listeners are the only code that ever sees a raw failure.
  assert.doesNotMatch(support, /event\.(?:error|reason|filename)|\.stack\b|\.reason\b/);
});
