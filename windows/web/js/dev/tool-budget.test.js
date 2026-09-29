import assert from 'node:assert/strict';
import test from 'node:test';
import { install } from './fixtures.js';
import { toolBudgetFigure, toolBudgetStrained } from '../state.js';

install();

const budgetOf = async (clientID) =>
  (await globalThis.__kyttoStub('state.get')).clients.find((client) => client.id === clientID).toolBudget;

test('a floor keeps its ≥ and a client without a cap shows a bare count', () => {
  assert.equal(
    toolBudgetFigure({ toolCount: 137, unmeasuredServerIDs: ['remote'], limit: 128, state: 'over' }),
    '≥137 / 128',
  );
  assert.equal(toolBudgetFigure({ toolCount: 26, unmeasuredServerIDs: [], limit: null, state: null }), '26');
  assert.equal(toolBudgetFigure(undefined), null);
  assert.equal(toolBudgetStrained({ state: 'near' }), true);
  assert.equal(toolBudgetStrained({ state: 'ok' }), false);
  assert.equal(toolBudgetStrained(null), false);
});

test('the fixture VS Code is past its cap until a server is switched off', async () => {
  const before = await budgetOf('vsCode');
  assert.equal(before.limit, 128);
  assert.equal(before.state, 'over');
  assert.deepEqual(before.unmeasuredServerIDs, ['github-remote']);

  await globalThis.__kyttoStub('servers.setEnabled', { serverID: 'playwright', clientID: 'vsCode', enabled: false });
  const after = await budgetOf('vsCode');
  assert.equal(after.toolCount, before.toolCount - 25);
  assert.equal(after.state, 'near');
});

test('only documented caps reach the UI', async () => {
  for (const id of ['claudeDesktop', 'claudeCode', 'cursor', 'codex']) {
    const budget = await budgetOf(id);
    assert.equal(budget.limit, null, id);
    assert.equal(budget.state, null, id);
  }
});
