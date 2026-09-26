import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const source = (path) => readFileSync(join(webRoot, path), 'utf8');

function javascriptFiles(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? javascriptFiles(path) : entry.name.endsWith('.js') ? [path] : [];
  });
}

test('all JavaScript modules pass the Node syntax parser', () => {
  for (const path of javascriptFiles(join(webRoot, 'js'))) {
    execFileSync(process.execPath, ['--check', path], { stdio: 'pipe' });
  }
});

test('the native document has a restrictive content security policy', () => {
  const html = source('index.html');
  assert.match(html, /Content-Security-Policy/);
  assert.match(html, /default-src 'none'/);
  assert.match(html, /script-src 'self'/);
  assert.match(html, /frame-src 'none'/);
});

test('platform-facing empty states use platformDisplayName', () => {
  const onboarding = source('js/screens/onboarding.js');
  const client = source('js/screens/clientDetail.js');
  assert.match(onboarding, /platformDisplayName/);
  assert.match(client, /platformDisplayName/);
  assert.doesNotMatch(`${onboarding}\n${client}`, /this Mac/);
});

test('token labels distinguish exact, estimated and incomplete totals', () => {
  const matrix = source('js/screens/matrix.js');
  const detail = source('js/screens/clientDetail.js');
  assert.match(matrix, /incomplete \? '≥' : approximate \? '~'/);
  assert.match(matrix, /known tokens minimum/);
  assert.match(detail, /server\.tokenWeight\.isMeasured \? '' : '~'/);
});

test('the matrix preserves readable client columns and scrolls horizontally', () => {
  const css = source('css/app.css');
  const matrix = source('js/screens/matrix.js');
  assert.match(css, /\.table-scroll\s*{[^}]*overflow-x:\s*auto/s);
  assert.match(css, /\.col-client\s*{[^}]*width:\s*var\(--col-client-width\)/s);
  assert.match(css, /--col-client-width:\s*76px/);
  // The server column is the one the matrix is read by, so it is the one column
  // that may not be given away: the table's minimum counts every client instead.
  assert.match(css, /\.matrix\s*{[^}]*min-width:\s*var\(--matrix-min-width, 620px\)/s);
  assert.match(css, /\.col-server\s*{[^}]*min-width:\s*var\(--col-server-min\)/s);
  assert.match(matrix, /--matrix-min-width/);
  assert.match(matrix, /\$\{clients\.length\} \* var\(--col-client-width\)/);
  // And it stays on screen once that scroll actually happens.
  assert.match(css, /\.col-server\s*{[^}]*position:\s*sticky;[^}]*left:\s*0/s);
});

test('client icons are legible and the restart bar cannot cover matrix content', () => {
  const css = source('css/app.css');
  assert.match(css, /\.app-icon\s*{[^}]*width:\s*22px;[^}]*height:\s*22px/s);
  assert.match(css, /#content\s*{[^}]*min-height:\s*0;[^}]*overflow:\s*hidden/s);
  assert.match(css, /#content:has\(\.restart-bar\) \.table-scroll\s*{[^}]*padding-bottom:\s*12px/s);
  assert.doesNotMatch(css, /\.restart-bar\s*{[^}]*order:\s*99/s);
});

test('restart notices name each client and distinguish dismissal from confirmation', () => {
  const matrix = source('js/screens/matrix.js');
  const main = source('js/main.js');
  assert.match(matrix, /Restart \$\{entry\.displayName\} to apply/);
  assert.match(matrix, /I restarted/);
  assert.match(matrix, /Dismiss/);
  assert.match(main, /invoke\('restarts\.acknowledge', \{ clientID: target\.dataset\.clientId \}\)/);
  assert.match(main, /invoke\('restarts\.dismiss', \{ clientID: null \}\)/);
});

test('the first matrix write is explained and successful changes offer undo', () => {
  const main = source('js/main.js');
  const matrix = source('js/screens/matrix.js');
  assert.match(main, /settings\.confirmMatrixWrites/);
  assert.match(main, /kind: 'confirmToggle'/);
  assert.match(main, /case 'undo-toggle'/);
  assert.match(matrix, /Change client configuration\?/);
  assert.match(matrix, /timestamped backup first and offers Undo/);
});

test('the matrix keeps every supported client visible but absent clients read-only', () => {
  const state = source('js/state.js');
  const row = source('js/screens/serverRow.js');
  // Detection failing is never a reason to drop a column; the user folding the
  // read-only sources away is the one thing that is.
  assert.match(
    state,
    /export function matrixClients\(\)\s*{\s*if \(state\.settings\?\.showsCustomSources === false\)[\s\S]*?client\.isReadOnly[\s\S]*?return state\.clients;/s,
  );
  assert.match(row, /client\.state === 'notInstalled'[\s\S]*?button\.disabled = true/);
});

test('read-only sources fold away from the sidebar and the matrix together', () => {
  const sidebar = source('js/screens/sidebar.js');
  const main = source('js/main.js');
  const fixtures = source('js/dev/fixtures.js');
  const ipc = readFileSync(join(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  assert.match(sidebar, /data-action|toggle-custom-sources/);
  assert.match(sidebar, /aria-expanded/);
  assert.match(main, /invoke\('settings\.setShowsCustomSources', \{ shown \}\)/);
  // The harness answers the command too, or dev.html stops standing in for the app.
  assert.match(fixtures, /'settings\.setShowsCustomSources'/);
  assert.match(ipc, /### `settings\.setShowsCustomSources`/);
});

test('state-driven renders preserve pane scroll without focus-induced jumps', () => {
  const matrix = source('js/screens/matrix.js');
  assert.match(matrix, /scrollSnapshot\(root\)/);
  assert.match(matrix, /restoreScroll\(scrollBeforeRender, root\)/);
  assert.match(matrix, /focus\(\{ preventScroll: true \}\)/);
  assert.match(matrix, /scrollTop: element\.scrollTop/);
  assert.match(matrix, /scrollLeft: element\.scrollLeft/);
});

test('sheets expose the dialog accessibility contract', () => {
  const dom = source('js/dom.js');
  assert.match(dom, /setAttribute\('role', 'dialog'\)/);
  assert.match(dom, /setAttribute\('aria-modal', 'true'\)/);
  assert.match(dom, /setAttribute\('aria-labelledby'/);
});

test('sheets trap Tab, close on Escape and restore focus', () => {
  const main = source('js/main.js');
  const matrix = source('js/screens/matrix.js');
  assert.match(main, /event\.key === 'Tab'/);
  assert.match(main, /event\.key === 'Escape'/);
  assert.match(matrix, /returnFocus/);
  assert.match(matrix, /restoreFocus/);
});

test('initial-load retry executes the complete load pipeline', () => {
  const main = source('js/main.js');
  const matrix = source('js/screens/matrix.js');
  assert.match(matrix, /dataset\.action = 'retry-load'/);
  assert.match(main, /case 'retry-load':\s*await load\(\)/s);
});

test('updates stay behind native IPC and render a persistent download banner', () => {
  const main = source('js/main.js');
  const matrix = source('js/screens/matrix.js');
  assert.match(main, /invoke\('updates\.check', \{ force \}\)/);
  assert.match(main, /invoke\('updates\.download'\)/);
  assert.match(main, /invoke\('updates\.cancel'\)/);
  assert.match(main, /invoke\('updates\.install', \{ token \}\)/);
  assert.match(main, /invoke\('provenance\.checkLatest'/);
  assert.match(main, /app\.capabilities\.includes\('updates'\)/);
  assert.match(matrix, /Kytto \$\{update\.latestVersion\} is available/);
  assert.match(matrix, /dataset\.action = 'download-update'/);
  assert.match(matrix, /Install and relaunch/);
  assert.doesNotMatch(main, /fetch\(/);
});

test('waiting-for-authorization is its own state and the URL never leaves native hands', () => {
  const css = source('css/app.css');
  const row = source('js/screens/serverRow.js');
  const detail = source('js/screens/serverDetail.js');
  const main = source('js/main.js');
  const fixtures = source('js/dev/fixtures.js');
  const ipc = readFileSync(join(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  // Its own colour: neither red (it is not broken) nor amber (it costs nothing).
  assert.match(css, /--auth:\s*light-dark/);
  assert.match(css, /\.status-dot\.auth\s*{[^}]*var\(--auth\)/s);
  assert.match(row, /needsAuthorization: 'auth'/);
  assert.match(detail, /case 'needsAuthorization':/);
  // The page sends a server id; the native side opens the URL it recorded.
  assert.match(main, /invoke\('health\.openAuthorization', \{ serverID: target\.dataset\.serverId \}\)/);
  assert.doesNotMatch(`${main}\n${row}\n${detail}`, /window\.open/);
  assert.match(fixtures, /'health\.openAuthorization'/);
  assert.match(ipc, /### `health\.openAuthorization`/);
});

test('the sidebar width is draggable, keyboard-reachable and remembered natively', () => {
  const css = source('css/app.css');
  const sidebar = source('js/screens/sidebar.js');
  const main = source('js/main.js');
  const fixtures = source('js/dev/fixtures.js');
  const ipc = readFileSync(join(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  // The handle follows the CSS variable, so a drag needs no re-render.
  assert.match(css, /\.side-resize\s*{[^}]*left:\s*calc\(var\(--sidebar-width\)/s);
  assert.match(sidebar, /role', 'separator'/);
  assert.match(main, /invoke\('settings\.setSidebarWidth', \{ width: clampSidebarWidth\(width\) \}\)/);
  assert.match(main, /'--sidebar-width'/);
  assert.match(main, /ArrowLeft|ArrowRight/);
  assert.match(fixtures, /'settings\.setSidebarWidth'/);
  assert.match(ipc, /### `settings\.setSidebarWidth`/);
});

test('a skill card names its scope once and long paths cannot collapse vertically', () => {
  const skills = source('js/screens/skills.js');
  const css = source('css/app.css');
  // scopeLabel already composes agent and scope; prefixing them again printed
  // "Codex · global · Codex · global" on every card.
  assert.doesNotMatch(skills, /entry\.agent\s*}\s*·/);
  assert.match(skills, /entry\.scopeLabel/);
  // `anywhere` alone lets auto table layout squeeze the path column to one
  // glyph per line; the floor is what forbids the vertical-text degenerate case.
  assert.match(css, /\.clients-table td:nth-child\(3\)\s*{[^}]*min-width:[^}]*overflow-wrap:\s*anywhere/s);
});

test('forms keep state and caret while catalog entries support the keyboard', () => {
  const main = source('js/main.js');
  const matrix = source('js/screens/matrix.js');
  const form = source('js/screens/serverForm.js');
  assert.match(main, /Object\.assign\(sheet\.draft, changes\)/);
  assert.match(matrix, /selectionStart/);
  assert.match(form, /item\.tabIndex = 0/);
  assert.match(main, /document\.activeElement\?\.dataset\.action === 'catalog-pick'/);
});
