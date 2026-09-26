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
