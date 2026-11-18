import assert from 'node:assert/strict';
import test from 'node:test';

const { adaptEvent, adaptReply, adaptRequest } = await import('../compat.js');

// The replies below are what the macOS shell sends (docs/ipc.md of that shell).
// Each must come out in the shape the screens read; replies that are already in
// that shape must pass through untouched.

test('a folder choice becomes a session whose token is the folder itself', () => {
  assert.deepEqual(adaptReply('library.chooseDirectory', null, { path: '/Users/example/Projects' }), {
    sessionID: '/Users/example/Projects',
    displayName: '/Users/example/Projects',
  });
  assert.equal(adaptReply('skills.chooseDirectory', null, { path: null }), null);
  const canonical = { sessionID: 'library-session.1', displayName: 'Projects' };
  assert.equal(adaptReply('library.chooseDirectory', null, canonical), canonical);
});

test('a scan, preview and import round trip in the path-based shape', () => {
  const scan = adaptReply('library.scan', { path: '/p' }, {
    rootPathDisplay: '/p',
    candidates: [{ id: 'c1', name: 'filesystem', location: '.cursor/mcp.json', commandSummary: 'npx -y x', transport: 'stdio', environmentKeys: ['API_KEY'], detectedClientIDs: ['cursor'], missingClientIDs: ['codex'], warnings: [] }],
    warnings: ['skipped a large file'],
  });
  assert.equal(scan.candidates[0].relativeLocation, '.cursor/mcp.json');
  assert.equal(Object.hasOwn(scan.candidates[0], 'location'), false);
  assert.deepEqual(scan.warnings, ['skipped a large file']);

  // Follow-up requests of that shape carry no session token.
  assert.deepEqual(adaptRequest('library.preview', { path: '/p', candidateID: 'c1', clientIDs: ['cursor'] }), { candidateID: 'c1', clientIDs: ['cursor'] });

  const preview = adaptReply('library.preview', { candidateID: 'c1' }, {
    source: 'filesystem',
    targetClientIDs: ['cursor'],
    servers: [{ name: 'filesystem', transport: 'stdio', commandSummary: 'npx -y x', environmentKeys: [], conflictingClientIDs: ['cursor'], warnings: ['careful'] }],
    warnings: [],
    canImport: false,
  });
  assert.equal(preview.candidate.name, 'filesystem');
  assert.equal(preview.conflicts.length, 1);
  assert.match(preview.conflicts[0], /filesystem/);
  assert.deepEqual(preview.warnings, ['filesystem: careful']);

  const imported = adaptReply('library.import', {}, { serverNames: ['a', 'b'], changed: ['cursor'], requiresRestart: true, state: { servers: [] } });
  assert.deepEqual(imported, { serverName: 'a, b', changedClientIDs: ['cursor'], warnings: [], state: { servers: [] } });
});

test('an agent import is described and applied in the canonical shape', () => {
  const preview = adaptReply('imports.preview', {}, {
    source: 'agent JSON',
    targetClientIDs: ['cursor'],
    servers: [{ name: 'memory', transport: 'stdio', commandSummary: 'npx memory', environmentKeys: ['KEY'], conflictingClientIDs: [], warnings: [] }],
    warnings: ['one note'],
    canImport: true,
  });
  assert.deepEqual(preview.servers[0], { name: 'memory', transport: 'stdio', commandSummary: 'npx memory', environmentKeys: ['KEY'], url: null });
  assert.deepEqual(preview.conflicts, []);

  const result = adaptReply('imports.import', {}, { serverNames: ['memory'], changed: ['cursor'], requiresRestart: false, state: {} });
  assert.deepEqual(result.importedServerNames, ['memory']);
  assert.deepEqual(result.changedClientIDs, ['cursor']);
});

test('the skills inventory arrives as skills with a conflict flag', () => {
  const inventory = adaptReply('skills.inventory', {}, {
    entries: [{ id: 's1', name: 'pdf', description: null, agent: 'Claude', scope: 'global', scopeLabel: 'Claude · global', pathDisplay: '~/.claude/skills/pdf', metadataStatus: 'complete', isDuplicate: true, hasConflict: true, duplicateGroup: 'g' }],
    warnings: [],
    roots: ['Claude · global'],
  });
  assert.equal(inventory.skills.length, 1);
  assert.equal(inventory.skills[0].locationDisplay, '~/.claude/skills/pdf');
  assert.equal(inventory.skills[0].isConflict, true);
  assert.equal(inventory.skills[0].description, '');
  const canonical = { skills: [], warnings: [] };
  assert.equal(adaptReply('skills.inventory', {}, canonical), canonical);
});

test('a bare state reply to the package lookup is wrapped', () => {
  const state = { clients: [], servers: [] };
  assert.deepEqual(adaptReply('provenance.checkLatest', {}, state), { provenance: null, state });
  const wrapped = { provenance: {}, state };
  assert.equal(adaptReply('provenance.checkLatest', {}, wrapped), wrapped);
});

test('updater progress is reported in one shape', () => {
  assert.deepEqual(
    adaptEvent('updates.progress', { phase: 'downloading', completedBytes: 50, totalBytes: 100, fraction: 0.5, message: 'Downloading' }),
    { phase: 'downloading', progress: 0.5, bytesReceived: 50, totalBytes: 100, message: 'Downloading' },
  );
  const canonical = { phase: 'verifying', progress: null, bytesReceived: 0, totalBytes: null };
  assert.equal(adaptEvent('updates.progress', canonical), canonical);
  assert.equal(adaptEvent('configs.changed', { a: 1 }).a, 1);
});
