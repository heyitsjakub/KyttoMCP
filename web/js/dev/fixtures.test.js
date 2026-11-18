import assert from 'node:assert/strict';
import test from 'node:test';
import { install } from './fixtures.js';
import { diagnosticReport } from '../screens/serverDetail.js';

install();

const validDraft = {
  name: 'fixture-no-targets',
  transport: 'stdio',
  command: 'npx',
  args: [],
  env: [],
  url: '',
};

test('create without a client changes no server, backup, or restart state', async () => {
  const before = await globalThis.__kyttoStub('state.get');
  const backupsBefore = await globalThis.__kyttoStub('backups.list');

  await assert.rejects(
    globalThis.__kyttoStub('servers.create', { draft: validDraft, clientIDs: [] }),
    { message: 'Choose at least one client to add this server to.', code: 'badArgument' },
  );

  const after = await globalThis.__kyttoStub('state.get');
  const backupsAfter = await globalThis.__kyttoStub('backups.list');
  assert.equal(after.servers.length, before.servers.length);
  assert.deepEqual(after.pendingRestarts, before.pendingRestarts);
  assert.deepEqual(backupsAfter, backupsBefore);
});

test('fixture rejects the same unsafe URL and environment names as native validation', async () => {
  await assert.rejects(
    globalThis.__kyttoStub('servers.create', {
      draft: { ...validDraft, name: 'bad-url', transport: 'http', command: '', url: 'httpx://example.com' },
      clientIDs: ['cursor'],
    }),
    /not a valid URL/,
  );
  await assert.rejects(
    globalThis.__kyttoStub('servers.create', {
      draft: { ...validDraft, name: 'bad-env', env: [{ key: 'API-KEY', value: 'secret' }] },
      clientIDs: ['cursor'],
    }),
    /not a valid environment variable name/,
  );
});

/// Editing is the other door into the same files, and it was never guarded here.
test('fixture rejects an unsafe draft on update too, not just on create', async () => {
  const [existing] = (await globalThis.__kyttoStub('state.get')).servers;
  await assert.rejects(
    globalThis.__kyttoStub('servers.update', {
      serverID: existing.id,
      draft: { ...validDraft, name: existing.name, env: [{ key: '1BAD', value: 'x' }] },
    }),
    /not a valid environment variable name/,
  );
});

test('diagnostic report excludes arguments, environment values, and raw stderr', () => {
  const report = diagnosticReport({
    name: 'safe-report',
    transport: 'stdio',
    command: 'npx',
    args: ['--token', 'argument-secret'],
    env: [{ key: 'API_TOKEN', hasValue: true, value: 'environment-secret' }],
    health: {
      status: 'failed',
      checkedAt: 0,
      durationSeconds: 1,
      serverName: null,
      serverVersion: null,
      capabilityNames: [],
      inspectionNotes: [],
      tools: [],
      message: 'The server exited.',
      stderr: 'raw-stderr-secret',
    },
  }, { clients: [] });

  assert.match(report, /Command: npx \(2 arguments omitted\)/);
  assert.doesNotMatch(report, /Environment keys|API_TOKEN/);
  assert.doesNotMatch(report, /argument-secret|environment-secret|raw-stderr-secret/);
});

/// `dev.html` runs the whole UI against these fixtures, so a command the page
/// sends with no fixture is a screen that only works natively (CLAUDE.md).
test('every command the page invokes has a fixture', async () => {
  const { readFile, readdir } = await import('node:fs/promises');
  const path = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const files = ['main.js', ...(await readdir(path.join(root, 'screens'))).map((name) => `screens/${name}`)];
  const commands = new Set();
  for (const file of files) {
    const source = await readFile(path.join(root, file), 'utf8');
    for (const match of source.matchAll(/invoke\('([a-zA-Z]+\.[a-zA-Z]+)'/g)) commands.add(match[1]);
  }
  const fixtures = await readFile(path.join(root, 'dev', 'fixtures.js'), 'utf8');
  const missing = [...commands].filter((command) => !fixtures.includes(`'${command}':`));
  assert.deepEqual(missing, []);
});

test('a dismissed restart notice keeps its count and comes back with the next write', async () => {
  const state = await globalThis.__kyttoStub('state.get');
  const server = state.servers.find((entry) => (entry.enabledIn.cursor ?? 'absent') === 'enabled');
  await globalThis.__kyttoStub('servers.setEnabled', { serverID: server.id, clientID: 'cursor', enabled: false });

  const dismissed = await globalThis.__kyttoStub('restarts.dismiss', { clientID: 'cursor' });
  assert.ok(dismissed.pendingRestarts.cursor > 0);
  assert.ok(dismissed.dismissedRestarts.includes('cursor'));

  const again = await globalThis.__kyttoStub('servers.setEnabled', { serverID: server.id, clientID: 'cursor', enabled: true });
  assert.ok(!again.state.dismissedRestarts.includes('cursor'));
});

test('library and agent import fixtures speak the native DTO shapes', async () => {
  const selection = await globalThis.__kyttoStub('library.chooseDirectory');
  assert.ok(selection.sessionID && selection.displayName);
  const scan = await globalThis.__kyttoStub('library.scan', { path: selection.sessionID });
  assert.equal(scan.isComplete, true);
  const notes = scan.candidates.find((entry) => entry.name === 'notes-search');
  assert.ok(notes.relativeLocation && notes.missingClientIDs.includes('codex'));

  const preview = await globalThis.__kyttoStub('library.preview', {
    path: selection.sessionID, candidateID: notes.id, clientIDs: ['claudeCode'],
  });
  assert.deepEqual(preview.targetClientIDs, ['claudeCode']);
  assert.deepEqual(preview.conflicts, []);

  const imported = await globalThis.__kyttoStub('library.import', {
    path: selection.sessionID, candidateID: notes.id, clientIDs: ['claudeCode'],
  });
  assert.equal(imported.serverName, 'notes-search');
  assert.ok(imported.state.servers.some((entry) => entry.name === 'notes-search'));

  const json = JSON.stringify({ schemaVersion: 1, servers: [{ name: 'agent-made', transport: 'stdio', command: 'node', args: ['x.js'], env: ['TOKEN'] }] });
  const agentPreview = await globalThis.__kyttoStub('imports.preview', { json, clientIDs: ['cursor'] });
  assert.deepEqual(agentPreview.servers.map((entry) => entry.environmentKeys), [['TOKEN']]);
  await assert.rejects(globalThis.__kyttoStub('imports.preview', { json: '{"schemaVersion":1,}', clientIDs: ['cursor'] }), /strict JSON/);
});

test('the diagnostic report prints URL origins only', () => {
  const report = diagnosticReport({
    name: 'remote',
    transport: 'http',
    command: null,
    args: [],
    url: 'https://user:pw@mcp.example.com:8443/v1/key-abc?token=secret#frag',
    enabledIn: {},
    health: {
      status: 'needsAuthorization',
      checkedAt: 0,
      durationSeconds: 0,
      toolCount: 0,
      tools: [],
      message: 'Open https://auth.example.com/oauth/authorize?code=SECRET to continue.',
      authorizationURL: 'https://auth.example.com/oauth/authorize?code=SECRET',
    },
  }, { clients: [] });

  assert.match(report, /Endpoint: https:\/\/mcp\.example\.com:8443\/…/);
  assert.match(report, /Authorization page: https:\/\/auth\.example\.com\/…/);
  assert.doesNotMatch(report, /SECRET|token=|key-abc|user:pw|frag/);
});
