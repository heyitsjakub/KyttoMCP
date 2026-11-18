import assert from 'node:assert/strict';
import test from 'node:test';
import { settlePostMutationRefresh } from '../postMutation.js';

test('successful secondary refreshes preserve the ordinary success notice', async () => {
  const notice = { kind: 'info', message: 'Added the server.' };
  const refreshed = [];

  const result = await settlePostMutationRefresh(notice, [
    { label: 'Backups', run: async () => refreshed.push('Backups') },
    { label: 'Profiles', run: async () => refreshed.push('Profiles') },
  ]);

  assert.equal(result, notice);
  assert.deepEqual(refreshed.sort(), ['Backups', 'Profiles']);
});

test('a rejected Backups refresh cannot turn a committed create or toggle into failure', async () => {
  const notice = { kind: 'info', message: 'Added “memory” to Cursor.', undo: { serverId: 'memory' } };

  const result = await settlePostMutationRefresh(notice, [
    { label: 'Backups', run: async () => { throw new Error('fixture failure'); } },
  ]);

  assert.equal(result.kind, 'warning');
  assert.equal(result.undo, notice.undo);
  assert.match(result.message, /^Added “memory” to Cursor\./);
  assert.match(result.message, /change succeeded/);
  assert.match(result.message, /could not refresh Backups/);
});

test('secret rotation keeps its existing warning and names every failed refresh', async () => {
  const notice = { kind: 'warning', message: 'The value changed in two files; one client was skipped.' };

  const result = await settlePostMutationRefresh(notice, [
    { label: 'Secrets', run: () => { throw new Error('secrets fixture failure'); } },
    { label: 'Backups', run: () => Promise.reject(new Error('backups fixture failure')) },
  ]);

  assert.equal(result.kind, 'warning');
  assert.match(result.message, /^The value changed in two files; one client was skipped\./);
  assert.match(result.message, /Secrets and Backups/);
});
