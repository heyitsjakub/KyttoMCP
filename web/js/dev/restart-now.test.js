// "Restart now" (issue #14): the web side offers it only where the native shell can do it,
// asks before quitting anything, and keeps the notice for a client that did not restart.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = (...parts) => readFile(path.resolve(here, '..', ...parts), 'utf8');

test('Restart now is offered only where the shell can restart the client, and asks first', async () => {
  const matrix = await source('screens', 'matrix.js');
  const main = await source('main.js');

  // Both the capability and the per-client flag must be there (§3.2: the UI branches on capability).
  assert.match(matrix, /includes\('client-restart'\)/);
  assert.match(matrix, /filter\(\(client\) => client\.canRestart\)/);
  assert.match(matrix, /restartable\.includes\(entry\.id\)/);

  // The button opens the confirmation; nothing is quit from the click itself.
  assert.match(main, /case 'restart-client':\s*setState\(\{ sheet: \{ kind: 'confirmRestart'/);
  assert.match(matrix, /case 'confirmRestart':\s*return renderRestartConfirm\(state\)/);
  assert.match(main, /case 'confirm-restart':\s*await confirmRestart\(\)/);
  assert.match(matrix, /Unsaved work in/);
  assert.match(matrix, /An app that is not running is not opened\./);
});

test('a client that did not restart keeps its notice as an error, and the rest is informational', async () => {
  const main = await source('main.js');

  assert.match(main, /if \(failures\.length > 0\) \{\s*return \{\s*kind: 'error'/);
  assert.match(main, /return \{ kind: 'info', message: messages\.join\(' '\) \}/);
  assert.match(main, /was not running and will read the new configuration when opened\./);
});

test('the fixtures advertise the capability and answer every restart outcome', async () => {
  const fixtures = await source('dev', 'fixtures.js');

  assert.match(fixtures, /'client-restart'\]/);
  assert.match(fixtures, /'restarts\.restart': \(\{ clientIDs = \[\] \}\)/);
  assert.match(fixtures, /restarted\.push\(id\)/);
  assert.match(fixtures, /notRunning\.push\(id\)/);
  assert.match(fixtures, /failures\.push\(/);
});
