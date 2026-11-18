// Scope, source type and text filters (§7.1.1), and the inspector's selection
// (§7.1.2). Ported from the macOS build's workspace-state test, with the cases
// Windows adds: a Claude Code project scope sharing its icon identity with the
// global client, a custom source with no owner, and a read-only project whose
// servers arrive labelled as ordinary configuration.

import assert from 'node:assert/strict';
import test from 'node:test';
import { installFakeDom } from './fake-dom.js';
import {
  setState,
  getState,
  matrixClients,
  visibleServers,
  scopeOptions,
  serverIsReadOnly,
  sourceKind,
} from '../state.js';

installFakeDom();
const { toggle, isPlugin } = await import('../screens/serverRow.js');

const clients = [
  { id: 'claudeCode', displayName: 'Claude Code', iconAsset: 'client-claude-code', state: 'ready' },
  { id: 'vsCode', displayName: 'VS Code', iconAsset: 'client-vscode', state: 'notInstalled' },
  {
    id: 'custom.project-a',
    displayName: 'Claude Code · …\\a',
    iconAsset: 'client-claude-code',
    configurationScope: 'workspace',
    isReadOnly: true,
    state: 'ready',
  },
  {
    id: 'custom.project-b',
    displayName: 'Claude Code · …\\b',
    iconAsset: 'client-claude-code',
    configurationScope: 'workspace',
    isReadOnly: true,
    state: 'ready',
  },
  // Attached by hand: no owning client, a profile rather than a workspace.
  {
    id: 'custom.notes',
    displayName: 'Team notes',
    iconAsset: 'client-custom',
    configurationScope: 'profile',
    isReadOnly: true,
    state: 'ready',
  },
].map((client) => ({ isReadOnly: false, configurationScope: 'global', ...client }));

const server = (id, enabledIn, kind = 'config', extra = {}) => ({
  id,
  name: id,
  commandSummary: `node ${id}`,
  enabledIn,
  source: { kind, readOnly: kind === 'plugin' || kind === 'custom' },
  originKind: kind === 'plugin' ? 'plugin' : 'configFile',
  isBundled: kind === 'plugin',
  health: null,
  ...extra,
});

const servers = [
  server('global', { claudeCode: 'enabled' }),
  server('project-a', { 'custom.project-a': 'enabled' }, 'custom'),
  server('project-b', { 'custom.project-b': 'enabled' }, 'custom'),
  server('plugin', { claudeCode: 'enabled' }, 'plugin'),
  // A global server the first project also sees: what a workspace inherits.
  server('inherited', { claudeCode: 'enabled', 'custom.project-a': 'enabled' }),
  server('notes-only', { 'custom.notes': 'enabled' }, 'custom'),
  // Held only by a read-only project, but labelled as plain configuration —
  // which is how a Claude Code project scope's servers can arrive.
  server('labelled-config', { 'custom.project-b': 'enabled' }, 'config'),
];

const ids = (list) => list.map((entry) => entry.id);

function reset(extra = {}) {
  setState({
    clients,
    servers,
    filter: '',
    sourceFilter: 'all',
    scopeFilter: 'all',
    settings: { showsCustomSources: true },
    selectedServerId: null,
    ...extra,
  });
}

test('the default matrix keeps every column and folding sources removes source-only rows', () => {
  reset();
  assert.deepEqual(ids(matrixClients()), ids(clients));
  assert.deepEqual(ids(visibleServers()), ids(servers));

  setState({ settings: { showsCustomSources: false } });
  assert.deepEqual(ids(matrixClients()), ['claudeCode', 'vsCode']);
  // The rows that exist only because of a folded source go with it.
  assert.deepEqual(ids(visibleServers()), ['global', 'plugin', 'inherited']);
});

test('the global scope shows global configurations and the servers they hold', () => {
  reset({ scopeFilter: 'global' });
  assert.deepEqual(ids(matrixClients()), ['claudeCode', 'vsCode']);
  assert.deepEqual(ids(visibleServers()), ['global', 'plugin', 'inherited']);
  setState({ settings: { showsCustomSources: false } });
  assert.deepEqual(ids(matrixClients()), ['claudeCode', 'vsCode']);
});

test('an explicit workspace shows its own source, its owning client and what it inherits', () => {
  reset({ scopeFilter: 'custom.project-a' });
  // The owner arrives through the shared icon identity — not through a path or a name.
  assert.deepEqual(ids(matrixClients()), ['claudeCode', 'custom.project-a']);
  assert.deepEqual(ids(visibleServers()), ['global', 'project-a', 'plugin', 'inherited']);

  // Folding the sidebar is the weaker statement: the chosen workspace stays.
  setState({ settings: { showsCustomSources: false } });
  assert.deepEqual(ids(matrixClients()), ['claudeCode', 'custom.project-a']);
});

test('a custom source with no owning client shows only itself', () => {
  reset({ scopeFilter: 'custom.notes' });
  assert.deepEqual(ids(matrixClients()), ['custom.notes']);
  assert.deepEqual(ids(visibleServers()), ['notes-only']);
});

test('source and text filters compose without including another workspace', () => {
  reset({ scopeFilter: 'custom.project-a', sourceFilter: 'custom' });
  // `inherited` is held by the workspace as well as by the global client, so it
  // is a workspace source here; `global` is held by the global file alone.
  assert.deepEqual(ids(visibleServers()), ['project-a', 'inherited']);

  setState({ sourceFilter: 'plugin' });
  assert.deepEqual(ids(visibleServers()), ['plugin']);

  setState({ sourceFilter: 'config' });
  assert.deepEqual(ids(visibleServers()), ['global', 'inherited']);

  setState({ sourceFilter: 'all', filter: 'inherit' });
  assert.deepEqual(ids(visibleServers()), ['inherited']);

  setState({ filter: 'not-present' });
  assert.deepEqual(visibleServers(), []);
});

test('the text filter still finds tools and commands inside the chosen scope', () => {
  reset({
    scopeFilter: 'custom.project-a',
    servers: servers.map((entry) =>
      entry.id === 'global' ? { ...entry, health: { tools: [{ name: 'create_pull_request' }] } } : entry,
    ),
    filter: 'pull_request',
  });
  assert.deepEqual(ids(visibleServers()), ['global']);
  setState({ scopeFilter: 'custom.notes' });
  assert.deepEqual(visibleServers(), []);
});

test('a server held only by read-only files is not a "configuration", whatever it is labelled', () => {
  reset({ sourceFilter: 'config' });
  assert.equal(ids(visibleServers()).includes('labelled-config'), false);
  setState({ sourceFilter: 'custom' });
  assert.equal(ids(visibleServers()).includes('labelled-config'), true);
  assert.equal(serverIsReadOnly(servers.find((entry) => entry.id === 'labelled-config')), true);
  assert.equal(serverIsReadOnly(servers.find((entry) => entry.id === 'global')), false);
  assert.equal(serverIsReadOnly(servers.find((entry) => entry.id === 'inherited')), false);
});

test('"Custom & workspace sources" is empty, not wrong, while the sources are folded', () => {
  reset({ sourceFilter: 'custom', settings: { showsCustomSources: false } });
  assert.deepEqual(visibleServers(), []);
});

test('the scope list names every read-only source and says what kind it is', () => {
  reset();
  assert.deepEqual(
    scopeOptions().map((entry) => [entry.id, entry.label]),
    [
      ['all', 'All sources'],
      ['global', 'Global configurations'],
      ['custom.project-a', 'Workspace · Claude Code · …\\a'],
      ['custom.project-b', 'Workspace · Claude Code · …\\b'],
      ['custom.notes', 'Profile · Team notes'],
    ],
  );
});

test('a payload from before `source` existed still sorts into the right bucket', () => {
  assert.equal(sourceKind({ originKind: 'plugin' }), 'plugin');
  assert.equal(sourceKind({ originKind: 'extension' }), 'extension');
  assert.equal(sourceKind({ originKind: 'configFile' }), 'config');
  assert.equal(sourceKind({ originKind: 'configFile', source: { kind: 'custom' } }), 'custom');
});

test('refresh preserves the inspector selection and removed subjects reset safely', () => {
  reset({ scopeFilter: 'custom.project-a', selectedServerId: 'project-a' });
  setState({ servers: [...servers] });
  assert.equal(getState().selectedServerId, 'project-a');

  // A refresh that no longer reports the workspace takes the scope back to
  // everything, and the server that lived in it takes the selection with it.
  setState({
    clients: clients.filter((client) => client.id !== 'custom.project-a'),
    servers: servers.filter((entry) => entry.id !== 'project-a'),
  });
  assert.equal(getState().scopeFilter, 'all');
  assert.equal(getState().selectedServerId, null);
  assert.equal(scopeOptions().some((entry) => entry.id === 'custom.project-a'), false);
});

test('a filter that hides the selected server clears the selection instead of keeping it', () => {
  reset({ selectedServerId: 'global' });
  setState({ filter: 'plugin' });
  assert.equal(getState().selectedServerId, null);

  reset({ selectedServerId: 'project-a' });
  setState({ scopeFilter: 'custom.notes' });
  assert.equal(getState().selectedServerId, null);

  reset({ selectedServerId: 'plugin' });
  setState({ sourceFilter: 'custom' });
  assert.equal(getState().selectedServerId, null);

  reset({ selectedServerId: 'project-a' });
  setState({ settings: { showsCustomSources: false } });
  assert.equal(getState().selectedServerId, null);

  // A filter that still shows it keeps it, and so does one that is merely
  // narrowed around it.
  reset({ selectedServerId: 'inherited' });
  setState({ scopeFilter: 'custom.project-a', filter: 'inherit' });
  assert.equal(getState().selectedServerId, 'inherited');
});

test('installed plugin controls cannot write or copy while extension owner controls remain available', () => {
  const state = { busyCells: new Set() };
  const owner = { id: 'claudeCode', displayName: 'Client', state: 'ready' };
  const plugin = { id: 'plugin', name: 'Plugin', originKind: 'plugin', isBundled: true };
  assert.equal(toggle(state, plugin, owner, 'enabled', false).disabled, true);
  assert.equal(toggle(state, plugin, owner, 'absent', false).disabled, true);
  assert.match(toggle(state, plugin, owner, 'enabled', false).title, /Read-only plugin/);
  assert.match(toggle(state, plugin, owner, 'absent', false).title, /cannot be copied/);

  const extension = {
    id: 'extension',
    name: 'Extension',
    originKind: 'extension',
    isBundled: true,
    source: { kind: 'extension', readOnly: false },
  };
  assert.notEqual(toggle(state, extension, owner, 'enabled', false).disabled, true);
  assert.equal(toggle(state, extension, owner, 'absent', false).disabled, true);

  // However native spells it, a plugin is a plugin.
  assert.equal(isPlugin({ originKind: 'plugin' }), true);
  assert.equal(isPlugin({ source: { kind: 'plugin' } }), true);
  assert.equal(isPlugin({ isBundled: true, source: { kind: 'x', readOnly: true } }), true);
  assert.equal(isPlugin(extension), false);
  assert.equal(isPlugin({ isBundled: false }), false);
});
