// "Matrix with detail" (§7.1.1, §7.1.2, §10.1): what the screen renders, what the
// stylesheet promises, and how main.js wires it. Rendering is checked against a
// small fake DOM (fake-dom.js); layout, motion and appearance are checked against
// the stylesheet's rules, because a Node test cannot lay anything out — those are
// verified in a browser through web/dev.html and described in the stream report.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { installFakeDom, outline } from './fake-dom.js';
import { install } from './fixtures.js';
import { getState, setState, setToolOpen, visibleServers } from '../state.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, '..', '..');
const source = (relative) => readFile(path.join(webRoot, relative), 'utf8');

const root = installFakeDom();
install();
// After the fixtures are installed, so `ipc.js` sees the stub and icons fall back
// to initials instead of asking a shell that is not there.
const { render } = await import('../screens/matrix.js');
const { renderServerDetail, renderServerInspector } = await import('../screens/serverDetail.js');
const stub = globalThis.__kyttoStub;

async function load(extra = {}) {
  const [app, settings, snapshot] = await Promise.all([
    stub('app.info'),
    stub('settings.get'),
    stub('state.get'),
  ]);
  setState({
    phase: 'ready',
    app,
    settings,
    backups: [],
    profiles: [],
    filter: '',
    scopeFilter: 'all',
    sourceFilter: 'all',
    density: 'comfortable',
    selectedServerId: null,
    sheet: null,
    panel: null,
    ...snapshot,
    ...extra,
  });
}

const draw = () => {
  render(root);
  return root;
};
const first = (selector) => draw().querySelector(selector);
const all = (selector) => draw().querySelectorAll(selector);
const rowIds = () => all('tr[data-server-id]').map((row) => row.dataset.serverId);

// MARK: - Rendering

test('the matrix is a heading, three native filters and a list beside an inspector', async () => {
  await load();
  const workspace = first('.matrix-workspace');
  assert.ok(workspace.classList.contains('density-comfortable'));
  assert.equal(first('.matrix-heading h1').textContent, 'Server matrix');

  const filters = first('.matrix-filters');
  const selects = filters.querySelectorAll('select');
  assert.deepEqual(selects.map((select) => select.dataset.action), ['scope-filter', 'source-filter', 'row-density']);

  // Each select sits inside its label, so the label is its accessible name and
  // clicking the label focuses it.
  const labels = filters.querySelectorAll('label.matrix-filter');
  assert.deepEqual(
    labels.map((label) => label.querySelector('.matrix-filter-label').textContent),
    ['Scope', 'Source', 'Rows'],
  );
  for (const label of labels) assert.equal(label.querySelectorAll('select').length, 1);

  // Decoration only: both SVGs are hidden from assistive technology.
  for (const svg of filters.querySelectorAll('svg')) assert.equal(svg.getAttribute('aria-hidden'), 'true');
  assert.equal(filters.querySelectorAll('svg').length, 6);

  assert.deepEqual(
    selects[1].querySelectorAll('option').map((option) => option.textContent),
    ['All source types', 'Configurations', 'Plugins & extensions', 'Custom & workspace sources'],
  );
  assert.deepEqual(
    selects[2].querySelectorAll('option').map((option) => option.textContent),
    ['Comfortable', 'Compact'],
  );
  const scopes = selects[0].querySelectorAll('option').map((option) => option.textContent);
  assert.deepEqual(scopes.slice(0, 2), ['All sources', 'Global configurations']);
  assert.ok(scopes.some((label) => label.startsWith('Workspace · Claude Code')));

  assert.ok(first('.matrix-split .matrix-list'));
  assert.ok(first('.matrix-split .server-inspector'));
});

test('a filter that narrows something is marked active and offers a way back', async () => {
  await load();
  assert.equal(all('.matrix-filter.is-active').length, 0);
  assert.equal(first('[data-action="reset-matrix-filters"]'), null);

  setState({ sourceFilter: 'plugin' });
  assert.equal(all('.matrix-filter.is-active').length, 1);
  assert.ok(first('[data-action="reset-matrix-filters"]'));
  assert.equal(first('.matrix-heading p').textContent.startsWith(`${rowIds().length} of ${getState().servers.length} servers`), true);

  const source = first('[data-action="source-filter"]');
  assert.deepEqual(
    source.querySelectorAll('option').filter((option) => option.selected).map((option) => option.value),
    ['plugin'],
  );
});

test('density is a class on the workspace and the select reports it', async () => {
  await load({ density: 'compact' });
  assert.ok(first('.matrix-workspace').classList.contains('density-compact'));
  assert.equal(first('.matrix-workspace').classList.contains('density-comfortable'), false);
  const density = first('[data-action="row-density"]');
  assert.deepEqual(
    density.querySelectorAll('option').filter((option) => option.selected).map((option) => option.value),
    ['compact'],
  );
  // The rows are the same rows; only how they are drawn changed.
  assert.equal(rowIds().length, visibleServers().length);
});

test('selecting a row selects it for the inspector and says so without colour', async () => {
  await load({ selectedServerId: 'github' });
  const selected = all('tr.selected-server');
  assert.deepEqual(selected.map((row) => row.dataset.serverId), ['github']);

  const openers = all('button.server-opener');
  assert.equal(openers.length, rowIds().length);
  for (const opener of openers) {
    assert.equal(opener.getAttribute('aria-pressed'), String(opener.dataset.serverId === 'github'));
    assert.equal(opener.dataset.action, 'open-server');
  }

  const inspector = first('.server-inspector');
  assert.equal(inspector.querySelector('.inspector-header h2').textContent, 'github');
  assert.deepEqual(
    inspector.querySelectorAll('.inspector-header button').map((button) => [button.textContent, button.dataset.action]),
    [['Expand', 'open-server-modal'], ['Close', 'close-inspector']],
  );
  assert.equal(inspector.querySelector('[data-action="open-server-modal"]').dataset.serverId, 'github');
});

test('with nothing selected the inspector says what to do', async () => {
  await load();
  const inspector = first('.server-inspector');
  assert.equal(inspector.querySelector('.inspector-empty h2').textContent, 'Select a server');
  assert.equal(all('tr.selected-server').length, 0);
});

test('a selection the filters hide is cleared, and the inspector falls back to empty', async () => {
  await load({ selectedServerId: 'github' });
  setState({ filter: 'filesystem' });
  assert.equal(getState().selectedServerId, null);
  assert.equal(first('.server-inspector .inspector-empty h2').textContent, 'Select a server');
  assert.deepEqual(rowIds(), ['filesystem']);
});

test('every row says how its last check went in words, not only in a dot', async () => {
  await load();
  const labels = Object.fromEntries(
    all('tr[data-server-id]').map((row) => [
      row.dataset.serverId,
      row.querySelector('.server-health-label').textContent,
    ]),
  );
  assert.equal(labels.filesystem, 'Check passed');
  assert.equal(labels.xcodebuildmcp, 'Check failed');
  assert.equal(labels.intercom, 'Sign-in required');
  assert.equal(labels.affinity, 'Not checked');
  for (const row of all('tr[data-server-id]')) assert.ok(row.querySelector('.status-dot'), row.dataset.serverId);
});

test('plugin rows show source, version and read-only, and every one of their switches is off limits', async () => {
  await load();
  const row = all('tr[data-server-id]').find((entry) => entry.dataset.serverId === 'plugin:demo-tools:inspector');
  assert.ok(row);
  assert.equal(row.querySelector('.server-source-label').textContent, 'Read-only · Plugin · Demo tools 2.0');
  const switches = row.querySelectorAll('.cell-toggle');
  assert.ok(switches.length >= 5);
  assert.deepEqual([...new Set(switches.map((control) => control.disabled))], [true]);

  // The Desktop extension beside it keeps its switch in the client that owns it.
  const extension = all('tr[data-server-id]').find((entry) => entry.dataset.serverId === 'affinity');
  assert.equal(extension.querySelector('.server-source-label').textContent, 'Extension');
  const owner = extension.querySelectorAll('.cell-toggle').find((control) => control.dataset.clientId === 'claudeDesktop');
  assert.equal(owner.disabled, false);
});

test('a project-scope server is labelled read-only because every file that holds it is', async () => {
  await load();
  const row = all('tr[data-server-id]').find((entry) => entry.dataset.serverId === 'project-api');
  assert.match(row.querySelector('.server-source-label').textContent, /^Read-only · /);
});

// MARK: - The modal and the inspector are one renderer

test('the detail sheet and the inspector show the same sections for every kind of server', async () => {
  await load();
  const state = getState();
  for (const id of ['github', 'filesystem', 'xcodebuildmcp', 'intercom', 'context7', 'plugin:demo-tools:inspector', 'affinity', 'project-api']) {
    const modal = renderServerDetail({ ...state, sheet: { kind: 'detail', serverId: id } });
    const inspector = renderServerInspector(state, id);
    assert.deepEqual(
      outline(modal.querySelector('.sheet-body')),
      outline(inspector.querySelector('.inspector-body')),
      id,
    );
    assert.equal(
      modal.querySelector('#sheet-title')?.textContent ?? modal.querySelector('h2').textContent,
      inspector.querySelector('.inspector-header h2').textContent,
      id,
    );
    // The same footer actions, drawn by the same function.
    assert.deepEqual(
      (modal.querySelector('.sheet-footer')?.querySelectorAll('button') ?? []).map((button) => button.dataset.action),
      (inspector.querySelector('.inspector-footer')?.querySelectorAll('button') ?? []).map((button) => button.dataset.action),
      id,
    );
  }
});

test('the inspector draws without a sheet, and its tool rows remember what was opened', async () => {
  await load({ sheet: null });
  const state = getState();
  assert.equal(state.sheet, null);
  const closed = renderServerInspector(state, 'filesystem');
  const tools = closed.querySelectorAll('details.inspector-tool');
  assert.ok(tools.length > 0);
  assert.equal(tools.every((tool) => tool.open === false), true);
  assert.equal(tools.every((tool) => tool.dataset.serverId === 'filesystem'), true);

  setToolOpen('filesystem', tools[0].dataset.toolName, true);
  const opened = renderServerInspector(getState(), 'filesystem').querySelectorAll('details.inspector-tool');
  assert.equal(opened[0].open, true);
  assert.equal(opened.slice(1).every((tool) => tool.open === false), true);

  // The same key opens the same row in the detail sheet.
  const modal = renderServerDetail({ ...getState(), sheet: { kind: 'detail', serverId: 'filesystem' } });
  assert.equal(modal.querySelectorAll('details.inspector-tool')[0].open, true);

  setToolOpen('filesystem', tools[0].dataset.toolName, false);
  assert.equal(
    renderServerInspector(getState(), 'filesystem').querySelectorAll('details.inspector-tool')[0].open,
    false,
  );
});

test('a plugin explains itself in the inspector and offers nothing to edit or delete', async () => {
  await load();
  const inspector = renderServerInspector(getState(), 'plugin:demo-tools:inspector');
  assert.match(inspector.textContent, /belongs to a registered plugin/);
  assert.match(inspector.textContent, /Demo tools · 2\.0/);
  assert.match(inspector.textContent, /Read-only/);
  assert.equal(inspector.querySelector('.inspector-footer'), null);
});

// MARK: - The stylesheet's promises

const rule = (css, selector) => {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\>]/g, '\\$&');
  return css.match(new RegExp(`(?:^|\\n)\\s*${escaped}\\s*\\{([^}]+)\\}`))?.[1] ?? '';
};

test('the closed filter control is drawn by us and the native menu is kept', async () => {
  const css = await source('css/app.css');
  const select = rule(css, '.scope-select');
  assert.match(select, /-webkit-appearance:\s*none/);
  assert.match(select, /(?<!-webkit-)appearance:\s*none/);
  assert.match(select, /height:\s*36px/);
  assert.match(select, /border-radius:\s*11px/);
  assert.match(select, /padding:\s*0 34px 0 35px/);
  assert.match(select, /linear-gradient/);
  assert.match(select, /box-shadow/);
  assert.match(css, /\.scope-select:focus-visible\s*\{[^}]*outline:\s*2px solid AccentColor/);
  assert.match(css, /\.matrix-filter\.is-active \.scope-select\s*\{[^}]*AccentColor/);

  // Decoration never takes the pointer: the whole control is the select.
  assert.match(rule(css, '.filter-control > .glyph'), /pointer-events:\s*none/);
  // A select is only ever styled, never replaced by a custom listbox.
  const matrix = await source('js/screens/matrix.js');
  assert.match(matrix, /el\('select', 'scope-select'\)/);
  assert.doesNotMatch(matrix, /role', 'combobox'|role', 'listbox'|aria-haspopup/);
});

test('matrix and inspector share the window and the page never scrolls sideways', async () => {
  const css = await source('css/app.css');
  const split = rule(css, '.matrix-split');
  assert.match(split, /display:\s*grid/);
  assert.match(split, /grid-template-columns:\s*minmax\(0, 1fr\) minmax\(310px, 360px\)/);
  assert.match(split, /gap:\s*14px/);
  // The matrix scrolls itself, both ways; the inspector scrolls its own body.
  assert.match(rule(css, '.table-scroll'), /overflow-x:\s*auto/);
  assert.match(rule(css, '.table-scroll'), /overflow-y:\s*auto/);
  assert.match(rule(css, '.inspector-body'), /overflow-y:\s*auto/);
  assert.match(rule(css, '.inspector-body'), /overflow-x:\s*hidden/);
  assert.match(rule(css, '#content'), /overflow-x:\s*hidden/);
  // Grid items may be narrower than their content, or a long path widens the page.
  assert.match(css, /grid-template-columns:\s*minmax\(0, 1fr\)/);
  assert.match(rule(css, '.matrix-list'), /min-width:\s*0/);
  assert.match(rule(css, '.server-inspector'), /min-width:\s*0/);
  // What a config file or a server wrote wraps inside the column.
  assert.match(css, /\.inspector-body :is\(code, pre, dd, li, p\)\s*\{[^}]*overflow-wrap:\s*anywhere/);
  assert.match(rule(css, '.server-name'), /text-overflow:\s*ellipsis/);
  assert.match(rule(css, '.server-meta > .server-source-label'), /text-overflow:\s*ellipsis/);
  // One line per row, so every row is the same height.
  assert.match(rule(css, '.server-meta'), /flex-wrap:\s*nowrap/);
});

test('at 1280px the inspector moves under the matrix', async () => {
  const css = await source('css/app.css');
  const block = css.match(/@media \(max-width: 1280px\)\s*\{([\s\S]*?)\r?\n\}\r?\n/)?.[1] ?? '';
  assert.notEqual(block, '');
  assert.match(block, /\.matrix-split\s*\{[^}]*flex-direction:\s*column/);
  assert.match(block, /\.server-inspector\s*\{[^}]*max-height:\s*420px/);
  assert.match(block, /\.matrix-workspace\s*\{[^}]*flex:\s*1 0 auto/);
  // With nothing selected, stacked, the inspector takes no room.
  assert.match(block, /\.server-inspector:has\(\.inspector-empty\)\s*\{[^}]*display:\s*none/);
  // And above it the workspace takes what is left and never fewer than its parts.
  assert.match(rule(css, '.matrix-workspace'), /flex:\s*1 1 0/);
  assert.match(rule(css, '.matrix-workspace'), /min-height:\s*440px/);
});

test('compact density trims the row to name, verdict and source', async () => {
  const css = await source('css/app.css');
  assert.match(rule(css, '.matrix-workspace'), /--row-height:\s*68px/);
  assert.match(rule(css, '.density-compact'), /--row-height:\s*52px/);
  assert.match(css, /\.density-compact \.server-command:not\(\.failure\):not\(\.authwait\):not\(\.matched-tools\)\s*\{[^}]*display:\s*none/);
  // The verdict and the source stay in compact: they are the point of the row.
  assert.doesNotMatch(css, /\.density-compact[^{]*\.server-(?:source|health)-label\s*\{[^}]*display:\s*none/);
  const state = await source('js/state.js');
  assert.match(state, /density: 'comfortable'/);
});

test('motion is short, and every animation yields to reduced motion', async () => {
  const css = await source('css/app.css');
  assert.match(css, /--fast:\s*110ms/);
  assert.match(css, /--medium:\s*200ms/);
  assert.match(rule(css, '.inspector-entering'), /inspector-in 180ms/);
  assert.match(rule(css, '.panel-entering'), /panel-in-forward var\(--medium\)/);
  assert.match(rule(css, '.matrix tbody tr.row-entering'), /row-in 180ms/);
  assert.match(rule(css, '.cell-mark.flipped'), /mark-flip 150ms/);
  assert.match(css, /\.sheet-backdrop\.is-entering \.sheet\s*\{[^}]*sheet-in var\(--medium\)/);
  // Nothing in the matrix settles on every keystroke any more.
  assert.doesNotMatch(css, /filter-settle|is-filtering/);

  const reduced = css.match(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*\*,[\s\S]*?\r?\n\}\r?\n/)?.[0] ?? '';
  assert.match(reduced, /animation-duration:\s*1ms !important/);
  assert.match(reduced, /transition-duration:\s*1ms !important/);
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)\s*\{\s*\.inspector-entering\s*\{\s*animation:\s*none/);
});

// MARK: - main.js wiring

test('a row selects, Expand opens the sheet, and Check visible walks the filtered rows one by one', async () => {
  const main = await source('js/main.js');
  assert.match(main, /case 'open-server':\s*if \(state\.panel === null\) setState\(\{ selectedServerId/);
  assert.match(main, /case 'open-server-modal':\s*setState\(\{ sheet: \{ kind: 'detail'/);
  assert.match(main, /case 'close-inspector':\s*setState\(\{ selectedServerId: null \}\)/);
  assert.match(main, /case 'reset-matrix-filters':\s*setState\(\{ filter: '', scopeFilter: 'all', sourceFilter: 'all' \}\)/);
  assert.match(main, /case 'check-visible':\s*await checkVisible\(\)/);

  const check = main.slice(main.indexOf('async function checkVisible'), main.indexOf('const firstLine'));
  // Taken once, before anything runs, and as plain ids.
  assert.equal((check.match(/visibleServers\(\)/g) ?? []).length, 1);
  assert.match(check, /if \(getState\(\)\.healthProgress \|\| getState\(\)\.checking\.size > 0\) return;/);
  assert.match(check, /for \(const \[index, server\] of servers\.entries\(\)\)[\s\S]*?await invoke\('health\.check', \{ serverID: server\.id \}\)/);
  assert.doesNotMatch(check, /health\.checkAll|Promise\.all/);
  assert.match(check, /refreshAfterMutation\(\['Profiles'\], notice\)/);

  // MCP Doctor keeps the all-servers run; the matrix toolbar offers the visible one.
  const doctor = await source('js/screens/doctor.js');
  assert.match(doctor, /'check-all'/);
  const matrix = await source('js/screens/matrix.js');
  assert.match(matrix, /'Check visible'/);
  assert.match(matrix, /'check-visible'/);
  assert.doesNotMatch(matrix, /'check-all'/);
});

test('the three selects commit on change and never on a keystroke inside the menu', async () => {
  const main = await source('js/main.js');
  const change = main.slice(main.indexOf("root.addEventListener('change'"), main.indexOf('// Scroll and disclosure state'));
  assert.match(change, /'scope-filter'[\s\S]*?setState\(\{ scopeFilter: event\.target\.value \}\)/);
  assert.match(change, /'source-filter'[\s\S]*?setState\(\{ sourceFilter: event\.target\.value \}\)/);
  assert.match(change, /'row-density'[\s\S]*?setState\(\{ density:/);
  // One listener per event type, as everywhere else.
  assert.equal((main.match(/root\.addEventListener\('change'/g) ?? []).length, 1);
});

test('focus returns to the half of the page it left, never to the inert twin behind a sheet', async () => {
  const main = await source('js/main.js');
  assert.match(main, /snapshot\.insideSheet \? root\.querySelector\('\.sheet'\) \?\? root : root/);
  assert.match(main, /candidate\.focus\(\{ preventScroll: true \}\)/);
});

test('the sidebar groups the places and keeps what Windows already had', async () => {
  const sidebar = await source('js/screens/sidebar.js');
  for (const label of ['Overview', 'Manage', 'Discover', 'Clients & sources']) assert.match(sidebar, new RegExp(label));
  assert.match(sidebar, /ids: \[null, 'doctor', 'activity'\]/);
  assert.match(sidebar, /ids: \['profiles', 'secrets', 'backups'\]/);
  // Read-only sources keep their padlock and their words.
  assert.match(sidebar, /source-lock/);
  assert.match(sidebar, /'Read-only'/);
  assert.match(sidebar, /'Custom sources'/);
  // With every source nested under its client, the heading is only the switch.
  assert.match(sidebar, /'Hide read-only sources' : 'Show read-only sources'/);
  // The Settings row names no shortcut: the shell owns that.
  assert.match(sidebar, /item\.title = 'Open Settings';/);
  assert.match(await source('css/app.css'), /\.side-resize\s*\{[^}]*left:\s*calc\(var\(--sidebar-width\) - 3px\)/);
});

// MARK: - The harness

test('the fixtures carry every source form and refuse to write a plugin', async () => {
  const state = await stub('state.get');
  const kinds = new Set(state.servers.map((entry) => entry.source.kind));
  assert.deepEqual([...kinds].sort(), ['config', 'custom', 'extension', 'plugin']);
  for (const entry of state.servers) {
    assert.ok(['configFile', 'extension', 'plugin'].includes(entry.originKind), entry.id);
    assert.equal(typeof entry.isBundled, 'boolean', entry.id);
    assert.equal(typeof entry.source.readOnly, 'boolean', entry.id);
  }
  const plugin = state.servers.find((entry) => entry.originKind === 'plugin');
  assert.equal(plugin.source.readOnly, true);
  assert.ok(plugin.source.version);

  await assert.rejects(stub('servers.setEnabled', { serverID: plugin.id, clientID: 'claudeCode', enabled: false }), /registered plugin/);
  await assert.rejects(stub('servers.delete', { serverID: plugin.id }), /registered plugin/);
  const after = await stub('state.get');
  assert.deepEqual(after.servers.find((entry) => entry.id === plugin.id).enabledIn, plugin.enabledIn);

  const info = await stub('app.info');
  assert.ok(info.capabilities.includes('library') && info.capabilities.includes('skills'));
});
