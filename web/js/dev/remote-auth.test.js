import assert from 'node:assert/strict';
import test from 'node:test';
import { install } from './fixtures.js';
import { contractFieldDetails } from '../screens/contractDetails.js';
import { doctorAuthAction, remoteAuthControls, remoteNotCheckedText, supportsRemoteSignIn } from '../screens/remoteAuth.js';

// The same minimal DOM the other screen tests use: enough to build and inspect nodes,
// and nothing that could parse markup.
class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.dataset = {};
    this.className = '';
    this.classList = { add: () => {} };
    this.children = [];
    this.attributes = new Map();
    this.disabled = false;
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

install();

const walk = (node) => [node, ...(node.children ?? []).flatMap(walk)];
const texts = (node) => walk(node).map((entry) => entry.textContent).filter(Boolean);
const buttons = (node) => walk(node).filter((entry) => entry.tagName === 'BUTTON');

const withOAuth = (overrides = {}) => ({
  app: { capabilities: ['health.http', 'health.oauth'] },
  checking: new Set(),
  healthProgress: null,
  servers: [],
  ...overrides,
});

const remote = (overrides = {}) => ({
  id: 'remote',
  name: 'remote',
  transport: 'http',
  url: 'https://mcp.example/mcp',
  health: null,
  ...overrides,
});

// MARK: - Contract details

test('a changed field shows its path, both sides in words, and the explanation', () => {
  const block = contractFieldDetails({
    toolName: 'write_file',
    details: [
      { path: 'inputSchema/properties/dry_run/default', before: 'true', after: 'false', explanation: 'Verify enforcement.' },
      { path: 'inputSchema/properties/confirm', before: null, after: '{"type":"boolean"}', explanation: null },
    ],
  });

  const all = texts(block);
  assert.ok(all.includes('inputSchema/properties/dry_run/default'));
  assert.ok(all.includes('Before') && all.includes('After'));
  assert.ok(all.includes('true') && all.includes('false'));
  assert.ok(all.includes('Verify enforcement.'));
  // Absent is said, not left blank, so it is never mistaken for an empty value.
  assert.ok(all.includes('Not present'));
  assert.equal(block.attributes.get('role'), 'group');
});

test('no details means nothing is drawn, for old records and for added or removed tools', () => {
  assert.equal(contractFieldDetails({ toolName: 'a' }), null);
  assert.equal(contractFieldDetails({ toolName: 'a', details: null }), null);
  assert.equal(contractFieldDetails({ toolName: 'a', details: [] }), null);
  assert.equal(contractFieldDetails(null), null);
});

test('hostile text is only ever text and is clipped again on this side', () => {
  const markup = '<img src=x onerror=alert(1)>';
  const block = contractFieldDetails({
    toolName: markup,
    details: [{ path: markup + 'p'.repeat(5_000), before: markup + 'b'.repeat(50_000), after: '\u{1F600}'.repeat(5_000), explanation: markup }],
  });

  for (const node of walk(block)) {
    assert.ok(node.textContent.length <= 2_500, `${node.tagName} holds ${node.textContent.length} characters`);
    // Only textContent was written: no node carries markup as children of text.
    assert.equal(typeof node.textContent, 'string');
  }
  const after = texts(block).find((text) => text.startsWith('\u{1F600}'));
  assert.equal(Array.from(after.replace('…', '')).length, 1_200, 'clipped by scalars, not UTF-16 units');
  assert.ok(!Array.from(after).some((character) => character.length === 1 && /[\ud800-\udfff]/.test(character)), 'no split surrogate pair');
});

test('more fields than native should ever send are not all drawn', () => {
  const details = Array.from({ length: 500 }, (_, index) => ({ path: `p${index}`, before: '1', after: '2' }));
  const block = contractFieldDetails({ toolName: 't', details });
  assert.equal(block.children.length, 41);
});

// MARK: - Remote sign-in controls

test('the sign-in button exists only for HTTP servers and only when native reports health.oauth', () => {
  const server = remote();
  assert.ok(remoteAuthControls(withOAuth(), server));
  assert.equal(remoteAuthControls(withOAuth({ app: { capabilities: ['health.http'] } }), server), null);
  assert.equal(remoteAuthControls(withOAuth({ app: undefined }), server), null);
  assert.equal(remoteAuthControls(withOAuth(), remote({ transport: 'stdio' })), null);
  assert.equal(remoteAuthControls(withOAuth(), remote({ transport: 'sse' })), null);
  assert.equal(supportsRemoteSignIn(withOAuth(), remote({ transport: 'sse' })), false);
});

test('Sign in and Forget carry only an opaque server id', () => {
  const controls = remoteAuthControls(withOAuth(), remote({ id: 'opaque-id' }));
  const [signIn, forget] = buttons(controls);

  assert.equal(signIn.dataset.action, 'sign-in-remote');
  assert.equal(forget.dataset.action, 'forget-remote-authorization');
  assert.deepEqual({ ...signIn.dataset }, { action: 'sign-in-remote', serverId: 'opaque-id' });
  assert.deepEqual({ ...forget.dataset }, { action: 'forget-remote-authorization', serverId: 'opaque-id' });
});

test('a server that needs authorization says so in words, apart from the button', () => {
  const waiting = remoteAuthControls(withOAuth(), remote({ health: { status: 'needsAuthorization' } }));
  assert.ok(texts(waiting).some((text) => text.startsWith('Sign-in required')));

  const fine = remoteAuthControls(withOAuth(), remote({ health: { status: 'passed' } }));
  assert.ok(!texts(fine).some((text) => text.startsWith('Sign-in required')));
});

test('a sign-in in progress, a check and a check-all all leave the buttons inert', () => {
  for (const state of [
    withOAuth({ checking: new Set(['remote']) }),
    withOAuth({ healthProgress: { serverName: 'x', index: 1, total: 2 } }),
  ]) {
    const [signIn, forget] = buttons(remoteAuthControls(state, remote()));
    assert.equal(signIn.dataset.action, 'noop');
    assert.equal(signIn.disabled, true);
    assert.equal(forget.dataset.action, 'noop');
  }
  const [busy] = buttons(remoteAuthControls(withOAuth({ checking: new Set(['remote']) }), remote()));
  assert.equal(busy.textContent, 'Waiting…');
});

test('the Doctor offers sign-in on the authorization finding and nowhere else', () => {
  const state = withOAuth({ servers: [remote()] });

  const action = doctorAuthAction(state, 'remote', { code: 'needs-authorization' });
  assert.equal(action.dataset.action, 'sign-in-remote');
  assert.equal(action.dataset.serverId, 'remote');
  assert.equal(doctorAuthAction(state, 'remote', { code: 'remote-check-failed' }), null);
  assert.equal(doctorAuthAction(withOAuth({ app: { capabilities: [] }, servers: [remote()] }), 'remote', { code: 'needs-authorization' }), null);
  assert.equal(doctorAuthAction(state, 'unknown', { code: 'needs-authorization' }), null);
});

test('an unchecked remote server explains what Check does, and that it calls no tools', () => {
  assert.match(remoteNotCheckedText(remote()), /does not call tools/);
  assert.match(remoteNotCheckedText(remote({ transport: 'sse' })), /Legacy SSE is not supported/);
});

// MARK: - Fixtures (what dev.html runs the UI against)

test('the fixture reports the capabilities, signs in, forgets, and checks HTTP servers', async () => {
  const info = await globalThis.__kyttoStub('app.info');
  assert.ok(info.capabilities.includes('health.http') && info.capabilities.includes('health.oauth'));

  const before = (await globalThis.__kyttoStub('state.get')).servers.find((entry) => entry.id === 'github-remote');
  assert.equal(before.health.status, 'needsAuthorization');

  const signedIn = await globalThis.__kyttoStub('health.signInRemote', { serverID: 'github-remote' });
  assert.equal(signedIn.servers.find((entry) => entry.id === 'github-remote').health.status, 'passed');

  assert.deepEqual(await globalThis.__kyttoStub('health.forgetRemoteAuthorization', { serverID: 'github-remote' }), {});
  const again = await globalThis.__kyttoStub('health.check', { serverID: 'github-remote' });
  assert.equal(again.health.status, 'needsAuthorization');

  await assert.rejects(globalThis.__kyttoStub('health.signInRemote', { serverID: 'filesystem' }), /Streamable HTTP/);
  await assert.rejects(globalThis.__kyttoStub('health.forgetRemoteAuthorization', { serverID: 'nope' }), /No server/);
});

test('the fixture contract alert carries field details in the shape native sends', async () => {
  const { contractAlerts } = await globalThis.__kyttoStub('state.get');
  const schema = contractAlerts[0].changes.find((change) => change.kind === 'inputSchemaChanged');

  assert.deepEqual(Object.keys(schema.details[0]), ['path', 'before', 'after', 'explanation']);
  assert.ok(contractFieldDetails(schema));
});
