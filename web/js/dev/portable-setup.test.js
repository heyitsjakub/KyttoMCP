import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.dataset = {};
    this.className = '';
    this.classList = { add: () => {} };
    this.children = [];
    this.attributes = new Map();
    this.disabled = false;
    this.title = '';
    this.textContent = '';
  }

  append(...children) {
    this.children.push(...children);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }
}

globalThis.document = {
  createElement: (tagName) => new FakeElement(tagName),
  createElementNS: (_namespace, tagName) => new FakeElement(tagName),
};

const { install } = await import('./fixtures.js');
install();
const { renderProfiles } = await import('../screens/profiles.js');
const {
  renderPortableImport,
  pendingContractEvidence,
  setupActionButtons,
  PENDING_REVIEW_HINT,
} = await import('../screens/portableSetup.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, '..', '..');
const read = (...parts) => readFile(path.join(webRoot, ...parts), 'utf8');

const walk = (node, visit) => {
  visit(node);
  for (const child of node.children ?? []) walk(child, visit);
};
const textOf = (node) => {
  let text = '';
  walk(node, (current) => { text += current.textContent ?? ''; });
  return text;
};

const baseState = (overrides = {}) => ({
  app: { capabilities: ['portable-setup'] },
  profiles: [],
  servers: [],
  clients: [],
  setupBusy: false,
  ...overrides,
});

// MARK: - The Profiles header

test('Export, Import and New profile are one group, directly in the header', () => {
  const panel = renderProfiles(baseState());
  const [header, empty] = panel.children;

  assert.equal(header.tagName, 'HEADER');
  assert.equal(header.className, 'panel-head profiles-heading');
  // The header's own children: the copy and the one group — nothing else, and in
  // particular no row of actions as a sibling of the header under it.
  assert.deepEqual(
    header.children.map((child) => child.className),
    ['profiles-heading-copy', 'profiles-heading-actions'],
  );
  const group = header.children[1];
  assert.deepEqual(
    group.children.map((child) => child.dataset.action),
    ['export-setup', 'preview-setup-import', 'new-profile'],
  );
  assert.deepEqual(
    group.children.map((child) => child.tagName),
    ['BUTTON', 'BUTTON', 'BUTTON'],
  );
  assert.deepEqual(
    panel.children.map((child) => child.className),
    ['panel-head profiles-heading', 'profile-empty'],
  );

  let nested = 0;
  walk(panel, (node) => {
    if (/setup-actions/.test(node.className)) nested += 1;
  });
  assert.equal(nested, 0, 'no nested setup row, which is what left the buttons on the left edge');
  assert.match(textOf(empty), /No profiles yet\./);
});

test('without the capability the group holds New profile alone and nothing is left to take room', () => {
  const panel = renderProfiles(baseState({ app: { capabilities: ['config.read'] } }));
  const group = panel.children[0].children[1];

  assert.deepEqual(group.children.map((child) => child.dataset.action), ['new-profile']);
  assert.deepEqual(setupActionButtons({ app: null }), []);
  assert.deepEqual(setupActionButtons({ app: { capabilities: [] } }), []);
});

test('the header keeps its actions in the same place when profiles exist', () => {
  const state = baseState({
    profiles: [{ id: 'p1', name: 'Coding', serverIDs: [], tokenBudget: null, analysis: { recommendations: [] } }],
  });
  const panel = renderProfiles(state);

  assert.equal(panel.children[0].children[1].children.length, 3);
  assert.equal(panel.children[1].className, 'profile-list');
});

test('while a native dialog is open both setup actions refuse a second click', () => {
  const buttons = setupActionButtons(baseState({ setupBusy: true }));

  assert.deepEqual(buttons.map((button) => button.dataset.action), ['noop', 'noop']);
  assert.ok(buttons.every((button) => button.disabled));
});

test('the header, its actions and the empty state share one gutter and one centre line', async () => {
  const css = await read('css', 'app.css');
  const rule = (selector) => {
    const match = css.match(new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*{([^}]*)}`));
    assert.ok(match, `${selector} must have a rule`);
    return match[1];
  };

  assert.match(rule('.panel-head'), /padding:\s*16px var\(--gutter\)/);
  assert.match(rule('.profile-empty'), /padding:\s*22px var\(--gutter\)/);
  assert.match(rule('.profiles-heading'), /flex-wrap:\s*wrap/);
  assert.match(rule('.profiles-heading-copy'), /min-width:\s*0/);
  const group = rule('.profiles-heading-actions');
  assert.match(group, /align-items:\s*center/);
  assert.match(group, /flex-wrap:\s*wrap/);
  assert.match(group, /margin-inline-start:\s*auto/);
  const buttons = rule('.profiles-heading-actions .button');
  assert.match(buttons, /height:\s*32px/);
  assert.match(buttons, /white-space:\s*nowrap/);
  // No rule for a separate setup row survives anywhere.
  assert.doesNotMatch(css, /\.setup-actions/);
});

// MARK: - The import sheet

test('the import preview names destinations and files and explains that everything starts off', () => {
  const state = baseState({
    clients: [{ id: 'codex', displayName: 'Codex' }],
    sheet: {
      kind: 'portableImport',
      busy: false,
      error: null,
      preview: {
        id: 'opaque',
        serverNames: ['memory', 'remote'],
        profileNames: ['Research'],
        targetClients: ['codex', 'unknownClient'],
        pathDisplays: ['%USERPROFILE%\\.codex\\config.toml'],
        pendingContractCount: 2,
        warnings: ['All imported servers start switched off.'],
        enabled: false,
      },
    },
  });

  const sheet = renderPortableImport(state);
  const text = textOf(sheet);
  for (const expected of [
    'memory, remote', 'Research', 'Codex, unknownClient', 'Disabled — enable each server',
    '%USERPROFILE%\\.codex\\config.toml', '2 imported contracts will require a fresh health check',
    'All imported servers start switched off.',
  ]) {
    assert.ok(text.includes(expected), `the sheet should say "${expected}"`);
  }

  const actions = [];
  walk(sheet, (node) => { if (node.dataset?.action) actions.push(node.dataset.action); });
  assert.ok(actions.includes('close-sheet'), 'Cancel, Close and the backdrop all go through close-sheet');
  assert.ok(actions.includes('apply-setup-import'));
});

test('a failed apply is shown on the sheet that asked, and a busy sheet cannot be applied twice', () => {
  const base = {
    kind: 'portableImport',
    error: 'The setup or destination changed after the preview. Preview the import again.',
    preview: { id: 'opaque', serverNames: [], profileNames: [], targetClients: [], pathDisplays: [], warnings: [] },
  };
  assert.match(textOf(renderPortableImport(baseState({ sheet: { ...base, busy: false } }))), /changed after the preview/);

  const actions = [];
  walk(renderPortableImport(baseState({ sheet: { ...base, busy: true } })), (node) => {
    if (node.dataset?.action) actions.push(node.dataset.action);
  });
  assert.ok(actions.includes('noop') && !actions.includes('apply-setup-import'));
});

test('a sheet with no preview renders nothing', () => {
  assert.equal(renderPortableImport(baseState({ sheet: { kind: 'portableImport' } })), null);
});

// MARK: - The web layer never handles what native keeps

test('Cancel, Escape and the backdrop all release the native plan', async () => {
  const main = await read('js', 'main.js');

  assert.match(
    main,
    /function closeCurrentSheet\(\)[\s\S]*?if \(current\?\.busy\) return;[\s\S]*?setState\(\{ sheet: null \}\);[\s\S]*?current\?\.kind === 'portableImport'[\s\S]*?invoke\('setup\.discardImport', \{ previewID: current\.preview\.id \}\)/,
  );
  assert.match(main, /case 'close-sheet':[\s\S]*?if \(sheet\?\.busy\) break;\s*closeCurrentSheet\(\);/);
  assert.match(main, /event\.key === 'Escape'[\s\S]*?if \(!getState\(\)\.sheet\.busy\) closeCurrentSheet\(\);/);
  // The one other way out of the preview — applying it — consumes the plan natively.
  assert.match(main, /invoke\('setup\.applyImport', \{ previewID: current\.preview\.id \}\)/);
});

test('the page sends an opaque id and nothing else, and cannot choose files or enable anything', async () => {
  const main = await read('js', 'main.js');
  const screen = await read('js', 'screens', 'portableSetup.js');

  const payloads = [...main.matchAll(/invoke\('setup\.(\w+)'(?:,\s*(\{[^}]*\}))?\)/g)]
    .map((match) => [match[1], match[2] ?? null]);
  assert.deepEqual(payloads.sort(), [
    ['applyImport', '{ previewID: current.preview.id }'],
    ['discardImport', '{ previewID: current.preview.id }'],
    ['export', null],
    ['previewImport', null],
  ].sort());

  for (const text of [main, screen]) {
    assert.doesNotMatch(text, /type\s*=\s*['"]file['"]|FileReader|showOpenFilePicker|showSaveFilePicker|webkitdirectory/);
    assert.doesNotMatch(text, /setup\.[a-zA-Z]+['"],\s*\{[^}]*(?:enable|enabled|path|value|secret)/i);
  }
});

test('a preview that came back null is a cancellation, not an error sheet', async () => {
  const main = await read('js', 'main.js');

  assert.match(main, /if \(preview\?\.id\) setState\(\{ sheet: \{ kind: 'portableImport'/);
  assert.match(main, /async function exportSetup\(\)[\s\S]*?if \(!result\?\.cancelled\)/);
  // Both wait on a native dialog, and say so while they do.
  assert.match(main, /async function previewSetupImport\(\)[\s\S]*?setState\(\{ setupBusy: true \}\)[\s\S]*?finally \{\s*setState\(\{ setupBusy: false \}\)/);
});

// MARK: - Imported contract fingerprints

const server = (overrides = {}) => ({ id: 'memory', health: null, ...overrides });
const imports = (entries, error = null) => baseState({ pendingContractImports: { entries, error } });

test('imported fingerprints are described as evidence and cannot be reviewed before a fresh passing check', () => {
  const unknown = pendingContractEvidence(
    imports([{ serverID: 'memory', toolCount: 2, matching: null }]),
    server({ health: { status: 'passed' } }),
  );
  assert.ok(unknown.entry);
  assert.equal(unknown.blocked, true, 'no check made here since the import');
  assert.match(textOf({ children: unknown.notes }), /2 imported tool fingerprints await local review\. Run a fresh health check/);
  assert.match(textOf({ children: unknown.notes }), /reference material/);

  const stale = pendingContractEvidence(
    imports([{ serverID: 'memory', toolCount: 1, matching: true }]),
    server({ health: { status: 'failed' } }),
  );
  assert.equal(stale.blocked, true, 'the latest check did not pass');
  assert.match(textOf({ children: stale.notes }), /1 imported tool fingerprint await/);

  for (const [matching, wording] of [[true, 'matches'], [false, 'differs from']]) {
    const compared = pendingContractEvidence(
      imports([{ serverID: 'memory', toolCount: 2, matching }]),
      server({ health: { status: 'passed' } }),
    );
    assert.equal(compared.blocked, false);
    assert.ok(textOf({ children: compared.notes }).includes(`The fresh check ${wording}`));
  }
  assert.match(PENDING_REVIEW_HINT, /fresh passing health check/);
});

test('an unreadable pending store is shown safely and blocks every review', () => {
  const evidence = pendingContractEvidence(
    imports([], 'Imported contract fingerprints could not be read. The local trusted baseline was left unchanged.'),
    server({ health: { status: 'passed' } }),
  );

  assert.equal(evidence.entry, null);
  assert.equal(evidence.blocked, true);
  assert.match(textOf({ children: evidence.notes }), /could not be read/);
  assert.equal(evidence.notes[0].className, 'failure-message');
});

test('a server with nothing pending is untouched, and a missing field is not an error', () => {
  const quiet = pendingContractEvidence(imports([{ serverID: 'other', toolCount: 1, matching: true }]), server());
  assert.deepEqual([quiet.entry, quiet.error, quiet.notes, quiet.blocked], [null, null, [], false]);
  assert.equal(pendingContractEvidence({}, server()).blocked, false);
});

test('Contract Guard renders imported evidence and disables Mark as reviewed with its reason', async () => {
  const source = await read('js', 'screens', 'serverDetail.js');

  assert.match(source, /pendingContractEvidence\(state, server\)/);
  // An unreadable store is one file for every server: its error is shown with a
  // server that has something to review, never on every server's page.
  assert.match(source, /if \(!alert && !imported\.entry\) return null;/);
  assert.match(source, /if \(imported\.blocked\) \{\s*review\.disabled = true;\s*review\.title = PENDING_REVIEW_HINT;/);
});

// MARK: - The dev harness mirrors the native contract

const stub = (command, payload) => globalThis.__kyttoStub(command, payload);

test('the harness advertises the capability and keeps the plan single-use', async () => {
  assert.ok((await stub('app.info')).capabilities.includes('portable-setup'));

  const preview = await stub('setup.previewImport');
  assert.equal(preview.enabled, false);
  assert.deepEqual(Object.keys(preview).sort(), [
    'enabled', 'id', 'pathDisplays', 'pendingContractCount', 'profileNames', 'serverNames',
    'targetClients', 'warnings',
  ]);

  const state = await stub('setup.applyImport', { previewID: preview.id });
  const imported = state.servers.find((entry) => entry.id === 'portable-example');
  assert.equal(imported.enabledIn.codex, 'disabled');
  assert.deepEqual(state.pendingContractImports, {
    entries: [{ serverID: 'portable-example', toolCount: 2, matching: null }],
    error: null,
  });
  await assert.rejects(stub('setup.applyImport', { previewID: preview.id }), { code: 'portableSetup' });
});

test('the harness refuses a review until a check has run, then retires the reference', async () => {
  await assert.rejects(
    stub('contract.acknowledge', { serverID: 'portable-example' }),
    { code: 'portableSetup', message: /successful local check/ },
  );
  assert.equal((await stub('state.get')).pendingContractImports.entries.length, 1);

  await stub('health.check', { serverID: 'portable-example' });
  const checked = await stub('state.get');
  assert.equal(checked.pendingContractImports.entries[0].matching, true);

  const reviewed = await stub('contract.acknowledge', { serverID: 'portable-example' });
  assert.deepEqual(reviewed.pendingContractImports.entries, []);
});

test('discarding a harness preview makes applying it stale', async () => {
  const preview = await stub('setup.previewImport');
  await stub('setup.discardImport', { previewID: preview.id });
  await assert.rejects(stub('setup.applyImport', { previewID: preview.id }), { code: 'portableSetup' });
  assert.deepEqual(await stub('setup.discardImport', { previewID: 'never-existed' }), {});
});
