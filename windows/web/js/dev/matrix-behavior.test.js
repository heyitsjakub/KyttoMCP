import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { restoreScroll, scrollSnapshot } from '../screens/matrix.js';
import { toggle } from '../screens/serverRow.js';
import {
  getState,
  matrixClients,
  matrixUndoPlan,
  needsMatrixWriteConfirmation,
  setState,
} from '../state.js';

const here = path.dirname(fileURLToPath(import.meta.url));

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

const clients = [
  { id: 'claudeDesktop', state: 'ready' },
  { id: 'claudeCode', state: 'notInstalled' },
  { id: 'cursor', state: 'orphanedConfig' },
  { id: 'vsCode', state: 'noConfig' },
  { id: 'codex', state: 'ready' },
];

test('matrix keeps all five supported clients, including notInstalled', () => {
  const previous = { clients: getState().clients, settings: getState().settings };
  setState({ clients, settings: { showsCustomSources: true } });
  try {
    assert.deepEqual(matrixClients().map((client) => client.id), clients.map((client) => client.id));
  } finally {
    setState(previous);
  }
});

test('matrix filters only read-only clients when custom sources are explicitly folded', () => {
  const previous = { clients: getState().clients, settings: getState().settings };
  const custom = { id: 'custom.fixture', state: 'ready', isReadOnly: true };
  setState({ clients: [...clients, custom], settings: null });
  try {
    assert.equal(matrixClients().includes(custom), true);
    setState({ settings: {} });
    assert.equal(matrixClients().includes(custom), true);
    setState({ settings: { showsCustomSources: false } });
    assert.deepEqual(matrixClients().map((client) => client.id), clients.map((client) => client.id));
  } finally {
    setState(previous);
  }
});

test('notInstalled toggles are disabled while noConfig toggles remain editable', () => {
  const state = { busyCells: new Set() };
  const server = {
    id: 'fixture-server',
    name: 'Fixture Server',
    isBundled: false,
    hasRelativePath: false,
  };
  const notInstalled = toggle(
    state,
    server,
    { id: 'claudeCode', displayName: 'Claude Code', state: 'notInstalled' },
    'absent',
    false,
  );
  const noConfig = toggle(
    state,
    server,
    { id: 'vsCode', displayName: 'VS Code', state: 'noConfig' },
    'absent',
    false,
  );

  assert.equal(notInstalled.disabled, true);
  assert.match(notInstalled.title, /was not detected.*Settings/);
  assert.equal(noConfig.disabled, false);
});

test('custom-source toggles are disabled with the read-only explanation', () => {
  const control = toggle(
    { busyCells: new Set() },
    { id: 'demo', name: 'Demo', isBundled: false, hasRelativePath: false },
    {
      id: 'custom.00000000-0000-0000-0000-000000000000',
      displayName: 'Demo workspace',
      state: 'ready',
      isReadOnly: true,
    },
    'enabled',
    false,
  );

  assert.equal(control.disabled, true);
  assert.equal(control.title, 'Kytto never changes this file.');
});

test('the matrix write explanation is required once and never for read-only sources', () => {
  const writable = { isReadOnly: false };
  assert.equal(needsMatrixWriteConfirmation({ hasConfirmedMatrixWrites: false }, writable), true);
  assert.equal(needsMatrixWriteConfirmation({ hasConfirmedMatrixWrites: true }, writable), false);
  assert.equal(
    needsMatrixWriteConfirmation(
      { hasConfirmedMatrixWrites: false },
      { isReadOnly: true },
    ),
    false,
  );
});

test('Undo restores absent through backup/remove and on/off through the safe toggle path', () => {
  assert.deepEqual(
    matrixUndoPlan({
      previousState: 'absent',
      backupId: 'backup-1',
      clientId: 'cursor',
      serverId: 'github',
    }),
    {
      command: 'backups.restore',
      payload: { backupID: 'backup-1', clientID: 'cursor' },
    },
  );
  assert.equal(
    matrixUndoPlan({
      previousState: 'absent',
      backupId: null,
      clientId: 'vsCode',
      serverId: 'github',
    }).command,
    'servers.removeFromClient',
  );
  assert.deepEqual(
    matrixUndoPlan({
      previousState: 'disabled',
      backupId: 'ignored',
      clientId: 'codex',
      serverId: 'github',
    }),
    {
      command: 'servers.setEnabled',
      payload: { serverID: 'github', clientID: 'codex', enabled: false },
    },
  );
});

test('render snapshots and restores both scroll axes for every workspace scroller', () => {
  const before = [
    { dataset: { scrollKey: 'sidebar' }, scrollTop: 37, scrollLeft: 0 },
    { dataset: { scrollKey: 'matrix' }, scrollTop: 129, scrollLeft: 45.5 },
  ];
  const after = [
    { dataset: { scrollKey: 'sidebar' }, scrollTop: 0, scrollLeft: 0 },
    { dataset: { scrollKey: 'matrix' }, scrollTop: 0, scrollLeft: 0 },
  ];
  const snapshot = scrollSnapshot({ querySelectorAll: () => before });

  restoreScroll(snapshot, { querySelectorAll: () => after });

  assert.deepEqual(after, before);
});

test('toggle routing rejects disabled and notInstalled targets before IPC', async () => {
  const source = await readFile(path.resolve(here, '..', 'main.js'), 'utf8');
  assert.match(
    source,
    /case 'toggle':\s*\{[\s\S]*?if \(target\.disabled \|\| client\?\.isReadOnly \|\| client\?\.state === 'notInstalled'\) break;[\s\S]*?await toggleCell/,
  );
});

test('focus restoration cannot scroll the replacement control into view', async () => {
  const source = await readFile(path.resolve(here, '..', 'main.js'), 'utf8');
  assert.match(source, /candidate\.focus\(\{ preventScroll: true \}\)/);
});
