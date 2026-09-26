import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, '..', '..');
const screensRoot = path.join(webRoot, 'js', 'screens');

async function productionSources() {
  const screenNames = (await readdir(screensRoot)).filter((name) => name.endsWith('.js'));
  const names = ['anim.js', 'dom.js', 'ipc.js', 'main.js', 'postMutation.js', 'state.js'];
  const files = [
    ...names.map((name) => path.join(webRoot, 'js', name)),
    ...screenNames.map((name) => path.join(screensRoot, name)),
  ];
  return Promise.all(files.map(async (file) => ({ file, text: await readFile(file, 'utf8') })));
}

test('production UI keeps untrusted text out of HTML parsing', async () => {
  for (const { file, text } of await productionSources()) {
    assert.doesNotMatch(text, /\b(?:innerHTML|outerHTML|insertAdjacentHTML|document\.write)\b/, file);
  }
});

test('ipc.js remains the only native web boundary', async () => {
  for (const { file, text } of await productionSources()) {
    if (path.basename(file) === 'ipc.js') continue;
    assert.doesNotMatch(text, /\b(?:webkit|chrome\.webview)\b/, file);
  }
});

test('every invoked IPC command is documented', async () => {
  const docs = await readFile(path.resolve(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  const commands = new Set();
  for (const { text } of await productionSources()) {
    for (const match of text.matchAll(/\binvoke\('([^']+)'/g)) commands.add(match[1]);
  }
  for (const command of commands) assert.ok(docs.includes(command), `${command} is missing from docs/ipc.md`);
});

test('production copy does not assume the current machine is a Mac', async () => {
  for (const { file, text } of await productionSources()) {
    assert.doesNotMatch(text, /\b(?:on|this) this Mac\b|\bon this Mac\b/i, file);
  }
});

test('a tilde is only chosen from token measurement metadata', async () => {
  for (const { file, text } of await productionSources()) {
    assert.doesNotMatch(text, /`~\$\{formatTokens/, file);
  }
});

test('the updater is capability-gated, native-only, and renders the shared banner', async () => {
  const main = await readFile(path.join(webRoot, 'js', 'main.js'), 'utf8');
  const matrix = await readFile(path.join(webRoot, 'js', 'screens', 'matrix.js'), 'utf8');
  const state = await readFile(path.join(webRoot, 'js', 'state.js'), 'utf8');

  assert.match(main, /capabilities\?\.includes\('updates'\)/);
  assert.match(main, /invoke\('updates\.check', \{ force \}\)/);
  assert.match(main, /invoke\('updates\.openDownload'\)/);
  assert.match(main, /void checkForUpdates\(false\)/);
  assert.doesNotMatch(main, /\bfetch\s*\(/);
  assert.match(matrix, /updateAvailable/);
  assert.match(matrix, /Download update/);
  assert.match(matrix, /Later/);
  assert.match(state, /updateDismissed/);
  assert.match(state, /updateChecking/);
});

test('1.0.5 read-only tools use preview-first native flows and expose provenance safely', async () => {
  const main = await readFile(path.join(webRoot, 'js', 'main.js'), 'utf8');
  const matrix = await readFile(path.join(webRoot, 'js', 'screens', 'matrix.js'), 'utf8');
  const tools = await readFile(path.join(webRoot, 'js', 'screens', 'tools.js'), 'utf8');
  const detail = await readFile(path.join(webRoot, 'js', 'screens', 'serverDetail.js'), 'utf8');

  for (const command of [
    'imports.prompt', 'imports.preview', 'imports.import',
    'library.chooseDirectory', 'library.scan', 'library.preview', 'library.import',
    'skills.chooseDirectory', 'skills.inventory', 'provenance.checkLatest',
  ]) {
    const escaped = command.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    assert.match(main, new RegExp(`invoke\\('${escaped}'`));
  }
  assert.match(matrix, /capabilityButton\(state, 'agent-import'/);
  assert.match(matrix, /capabilityButton\(state, 'library'/);
  assert.match(matrix, /capabilityButton\(state, 'skills'/);
  assert.match(tools, /Preview import/);
  assert.match(tools, /button\('Import'/);
  assert.match(tools, /Read-only/);
  assert.match(detail, /provenanceSection/);
  assert.match(detail, /Check latest/);
  assert.doesNotMatch(tools, /innerHTML/);
});

test('matrix minimum width protects a sticky server-name column', async () => {
  const css = await readFile(path.join(webRoot, 'css', 'app.css'), 'utf8');
  const matrix = await readFile(path.join(webRoot, 'js', 'screens', 'matrix.js'), 'utf8');

  assert.match(css, /--col-server-min:\s*260px/);
  assert.match(css, /min-width:\s*var\(--matrix-min-width, 620px\)/);
  assert.match(css, /\.col-server\s*\{[\s\S]*?position:\s*sticky[\s\S]*?left:\s*0/);
  assert.match(matrix, /--matrix-min-width[\s\S]*?clients\.length[\s\S]*?--col-client-width/);
});

test('custom-source disclosure is wired through sidebar, state, native IPC and fixtures', async () => {
  const docs = await readFile(path.resolve(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  const [sidebar, state, main, fixtures] = await Promise.all([
    readFile(path.join(webRoot, 'js', 'screens', 'sidebar.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'state.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'dev', 'fixtures.js'), 'utf8'),
  ]);

  assert.match(sidebar, /toggle-custom-sources/);
  assert.match(sidebar, /aria-expanded/);
  assert.match(state, /showsCustomSources === false/);
  assert.match(main, /invoke\('settings\.setShowsCustomSources', \{ shown \}\)/);
  assert.match(fixtures, /world\.settings\.showsCustomSources = shown/);
  assert.match(docs, /settings\.setShowsCustomSources/);
});

test('the sidebar Settings row asks the shell for a window and names no keystroke', async () => {
  const docs = await readFile(path.resolve(webRoot, '..', 'docs', 'ipc.md'), 'utf8');
  const [css, sidebar, main, fixtures] = await Promise.all([
    readFile(path.join(webRoot, 'css', 'app.css'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'sidebar.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'dev', 'fixtures.js'), 'utf8'),
  ]);

  assert.match(sidebar, /side-item side-settings/);
  assert.match(sidebar, /dataset\.action = 'open-settings'/);
  // It goes to a window, so it is never a panel and never takes the rail.
  const builder = sidebar.slice(sidebar.indexOf('function settingsItem'));
  assert.notEqual(builder, '');
  assert.doesNotMatch(builder.slice(0, builder.indexOf('\n}')), /aria-current|dataset\.panel/);
  assert.match(main, /invoke\('settings\.open'\)/);
  assert.match(fixtures, /'settings\.open': \(\) => \(\{\}\)/);
  assert.match(css, /\.side-settings/);
  assert.match(docs, /settings\.open/);
  // Which key opens Settings is the shell's, and the two shells spell it
  // differently — so the shared page says neither (§3.2).
  for (const { file, text } of await productionSources()) {
    assert.doesNotMatch(text, /⌘|\bCtrl\s*\+/u, file);
  }
});

test('authorization is a blue state and the web sends native only a server id', async () => {
  const [css, row, detail, main, fixtures] = await Promise.all([
    readFile(path.join(webRoot, 'css', 'app.css'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'serverRow.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'serverDetail.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'dev', 'fixtures.js'), 'utf8'),
  ]);

  assert.match(css, /--auth:\s*light-dark\(#0969da,\s*#58a6ff\)/);
  assert.match(css, /\.status-dot\.auth/);
  assert.match(css, /\.status-needsAuthorization/);
  assert.match(row, /needsAuthorization:\s*'auth'/);
  assert.match(row, /Waiting for you to authorize it in the browser/);
  assert.match(detail, /open-authorization/);
  assert.match(detail, /Authorization page:/);
  assert.match(main, /invoke\('health\.openAuthorization', \{ serverID: serverId \}\)/);
  assert.doesNotMatch(main, /health\.openAuthorization[\s\S]{0,100}authorizationURL/);
  assert.match(fixtures, /status:\s*'needsAuthorization'/);
});

test('sidebar resize supports pointer, keyboard, reset and native persistence', async () => {
  const [css, sidebar, matrix, main, fixtures] = await Promise.all([
    readFile(path.join(webRoot, 'css', 'app.css'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'sidebar.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'matrix.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'dev', 'fixtures.js'), 'utf8'),
  ]);

  assert.match(css, /\.side-resize[\s\S]*?left:\s*calc\(var\(--sidebar-width\) - 3px\)/);
  assert.match(sidebar, /role', 'separator'/);
  assert.match(sidebar, /resize-sidebar/);
  assert.match(matrix, /body\.append\(sidebarResizeHandle/);
  assert.match(main, /setPointerCapture/);
  assert.match(main, /ArrowLeft/);
  assert.match(main, /ArrowRight/);
  assert.match(main, /commitSidebarWidth\(SIDEBAR_DEFAULT\)/);
  assert.match(main, /invoke\('settings\.setSidebarWidth', \{ width: kept \}\)/);
  assert.match(fixtures, /sidebarWidth:\s*196/);
  assert.match(fixtures, /Math\.min\(480, Math\.max\(170/);
});

test('skills scope appears once and long client paths keep a readable floor', async () => {
  const [css, tools, sidebar] = await Promise.all([
    readFile(path.join(webRoot, 'css', 'app.css'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'tools.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'screens', 'sidebar.js'), 'utf8'),
  ]);

  assert.match(tools, /\['Scope', skill\.scopeLabel \|\| 'unknown'\]/);
  assert.doesNotMatch(tools, /skill\.agent[^\n]*skill\.scope[^\n]*skill\.scopeLabel/);
  assert.match(css, /\.clients-table td:nth-child\(3\)[\s\S]*?min-width:\s*140px/);
  assert.match(css, /\.client-action \.button[\s\S]*?white-space:\s*nowrap/);
  assert.match(sidebar, /client\.shortName \?\? client\.displayName/);
});

test('committed mutations isolate list refresh failures and delegated actions surface rejections', async () => {
  const [main, helper] = await Promise.all([
    readFile(path.join(webRoot, 'js', 'main.js'), 'utf8'),
    readFile(path.join(webRoot, 'js', 'postMutation.js'), 'utf8'),
  ]);

  assert.match(helper, /Promise\.allSettled/);
  assert.match(helper, /The change succeeded, but Kytto could not refresh/);
  for (const marker of [
    "imports.import", "library.import", "profiles.apply", "gateway.enable",
    "gateway.restore", "servers.unify", "doctor.applyFix", "servers.setEnabled",
    "servers.create", "servers.update", "servers.delete", "servers.removeFromClient",
    "secrets.rotate", "health.check", "health.checkAll", "backups.restore",
  ]) {
    const start = main.indexOf(`invoke('${marker}'`);
    assert.notEqual(start, -1, `${marker} is missing`);
    assert.match(main.slice(start, start + 4000), /refreshAfterMutation\(/, marker);
  }
  assert.match(
    main,
    /root\.addEventListener\('click',[\s\S]*?try \{[\s\S]*?switch \(target\.dataset\.action\)[\s\S]*?catch \(error\)[\s\S]*?reportFailure\(error\)/,
  );
});
