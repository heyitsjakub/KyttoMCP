import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webRoot = path.resolve(here, '..', '..');
const source = (relative) => readFile(path.join(webRoot, relative), 'utf8');

function cssRule(css, selector) {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return css.match(new RegExp(`${escaped}\\s*\\{([^}]+)\\}`))?.[1] ?? '';
}

test('sidebar separates the five writable clients from read-only custom sources', async () => {
  const sidebar = await source('js/screens/sidebar.js');
  assert.match(sidebar, /5 built-in clients/);
  assert.match(sidebar, /Custom sources · read-only/);
  assert.match(sidebar, /state\.clients\.filter\(\(client\) => !client\.isReadOnly\)/);
  assert.match(sidebar, /state\.clients\.filter\(\(client\) => client\.isReadOnly\)/);
});

test('normal and fallback client icons occupy 22px while the detail hero stays large', async () => {
  const css = await source('css/app.css');
  const normal = cssRule(css, '.app-icon');
  assert.match(normal, /width:\s*22px/);
  assert.match(normal, /height:\s*22px/);

  const fallback = cssRule(css, '.app-icon.fallback');
  assert.match(fallback, /display:\s*inline-grid/);

  const hero = cssRule(css, '.client-hero-icon .app-icon');
  assert.match(hero, /width:\s*34px/);
  assert.match(hero, /height:\s*34px/);
});

test('the first cell click opens a sheet before either settings or config writes', async () => {
  const main = await source('js/main.js');
  const toggleCase = main.slice(
    main.indexOf("case 'toggle':"),
    main.indexOf("case 'confirm-matrix-write':"),
  );
  assert.ok(toggleCase.indexOf('needsMatrixWriteConfirmation') >= 0);
  assert.ok(toggleCase.indexOf("kind: 'confirmMatrixWrite'") >= 0);
  assert.ok(toggleCase.indexOf("kind: 'confirmMatrixWrite'") < toggleCase.indexOf('await toggleCell'));
  assert.match(toggleCase, /setState\([\s\S]+?break;[\s\S]+?await toggleCell/);

  const confirmation = main.slice(
    main.indexOf('async function confirmMatrixWrite()'),
    main.indexOf('async function undoMatrixWrite()'),
  );
  assert.ok(
    confirmation.indexOf("invoke('settings.confirmMatrixWrites')") <
      confirmation.indexOf('await toggleCell'),
  );

  const matrix = await source('js/screens/matrix.js');
  assert.match(matrix, /Change client configuration\?/);
  assert.match(matrix, /timestamped backup first/);
  assert.match(matrix, /Undo will be offered/);
});

test('every successful matrix change exposes Undo and refreshes supporting state separately', async () => {
  const main = await source('js/main.js');
  const toggling = main.slice(
    main.indexOf('async function toggleCell('),
    main.indexOf('async function confirmMatrixWrite()'),
  );
  assert.match(toggling, /backupId:\s*result\.backupID \?\? null/);
  assert.match(toggling, /notice = \{ kind: 'info', message: outcome, undo \}/);

  const undo = main.slice(
    main.indexOf('async function undoMatrixWrite()'),
    main.indexOf('// MARK: - Authoring'),
  );
  assert.match(undo, /matrixUndoPlan\(undo\)/);
  assert.match(undo, /await invoke\(plan\.command, plan\.payload\)/);
  assert.match(undo, /applyState\(result\.state \?\? result\)/);
  assert.match(undo, /refreshAfterMutation\(\['Backups'\], notice\)/);
});

test('restart banner is an ordinary flex row that cannot cover the table viewport', async () => {
  const css = await source('css/app.css');
  assert.match(cssRule(css, '#content'), /min-height:\s*0/);
  assert.match(cssRule(css, '#content'), /overflow:\s*hidden/);
  assert.match(cssRule(css, '.table-scroll'), /min-height:\s*0/);
  assert.match(cssRule(css, '.table-scroll'), /padding-bottom:\s*12px/);
  assert.doesNotMatch(cssRule(css, '.restart-bar'), /(?:position|order)\s*:/);

  const matrix = await source('js/screens/matrix.js');
  assert.ok(
    matrix.indexOf('content.append(restartBar') < matrix.indexOf('content.append(footer(state))'),
  );
});
