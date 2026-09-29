// MCP Doctor's package pin (§7.10): the button is the lookup until a recent one
// exists, the preview is refused without it, and apply writes only the release
// the preview showed.

import assert from 'node:assert/strict';
import test from 'node:test';
import { install } from './fixtures.js';
import { renderDoctor, renderDoctorFix } from '../screens/doctor.js';

class FakeElement {
  constructor(tagName) {
    this.tagName = tagName.toUpperCase();
    this.dataset = {};
    this.className = '';
    this.textContent = '';
    this.children = [];
    this.attributes = new Map();
    this.disabled = false;
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
const call = (command, payload) => globalThis.__kyttoStub(command, payload);

function* walk(node) {
  yield node;
  for (const child of node.children ?? []) yield* walk(child);
}

const buttons = (tree) => [...walk(tree)].filter((node) => node.tagName === 'BUTTON');

const finding = (state, serverID) =>
  state.doctorReports
    .find((report) => report.serverID === serverID)
    ?.findings.find((item) => item.code === 'unpinned-package');

test('an unchecked package is flagged, and its pin is refused until someone looks the version up', async () => {
  const state = await call('state.get');
  const flagged = finding(state, 'github');
  assert.equal(flagged?.severity, 'warning');
  assert.equal(flagged?.action, 'pinPackageVersion');
  assert.match(flagged.detail, /names no version/);

  await assert.rejects(
    call('doctor.previewFix', { serverID: 'github', action: 'pinPackageVersion' }),
    { code: 'appState', message: /Check the latest release/ },
  );

  const checked = await call('provenance.checkLatest', { serverID: 'github' });
  assert.equal(checked.provenance.latestVersion, '2025.4.8');

  const preview = await call('doctor.previewFix', { serverID: 'github', action: 'pinPackageVersion' });
  assert.equal(preview.action, 'pinPackageVersion');
  assert.equal(preview.version, '2025.4.8');
  assert.deepEqual(preview.clientIDs, ['claudeCode', 'cursor', 'codex']);
  for (const change of preview.argumentChanges) {
    assert.equal(change.current, '@modelcontextprotocol/server-github');
    assert.equal(change.replacement, '@modelcontextprotocol/server-github@2025.4.8');
  }

  await assert.rejects(
    call('doctor.applyFix', { serverID: 'github', action: 'pinPackageVersion', version: '2025.4.7' }),
    { code: 'appState' },
  );
  const unchanged = (await call('state.get')).servers.find((server) => server.id === 'github');
  assert.deepEqual(unchanged.args, ['-y', '@modelcontextprotocol/server-github']);

  const result = await call('doctor.applyFix', { serverID: 'github', action: 'pinPackageVersion', version: '2025.4.8' });
  assert.deepEqual(result.parkedFailures, []);
  const pinned = result.state.servers.find((server) => server.id === 'github');
  assert.deepEqual(pinned.args, ['-y', '@modelcontextprotocol/server-github@2025.4.8']);
  assert.equal(finding(result.state, 'github'), undefined);
});

test('the executable pin still answers when the page sends no action', async () => {
  const preview = await call('doctor.previewFix', { serverID: 'github' });
  assert.equal(preview.action, 'pinResolvedCommand');
  assert.deepEqual(preview.argumentChanges, []);
});

test('the Doctor button is the lookup until a recent one exists, then the pin', () => {
  const base = {
    app: { capabilities: ['provenance'] },
    clients: [{ id: 'cursor', displayName: 'Cursor' }],
    provenanceChecking: new Set(),
    doctorReports: [{
      serverID: 'pkg',
      serverName: 'pkg',
      findings: [{ code: 'unpinned-package', severity: 'warning', title: 't', detail: 'd', remediation: 'r', action: 'pinPackageVersion' }],
    }],
  };
  const withProvenance = (provenance) => ({
    ...base,
    servers: [{ id: 'pkg', provenance: { packageName: 'pkg', ...provenance } }],
  });

  const unchecked = buttons(renderDoctor(withProvenance({ latestVersion: null, latestCheckedAt: null })));
  assert.ok(unchecked.some((node) => node.dataset.action === 'check-provenance' && node.textContent === 'Check latest release'));
  assert.ok(!unchecked.some((node) => node.dataset.fixAction === 'pinPackageVersion'));

  const fresh = buttons(renderDoctor(withProvenance({ latestVersion: '1.2.3', latestCheckedAt: Date.now() / 1000 - 60 })));
  const pin = fresh.find((node) => node.dataset.fixAction === 'pinPackageVersion');
  assert.equal(pin?.dataset.action, 'doctor-preview-fix');
  assert.equal(pin?.textContent, 'Pin to 1.2.3…');

  // Older than the window native accepts: ask again rather than offer a pin
  // the preview would refuse.
  const stale = buttons(renderDoctor(withProvenance({ latestVersion: '1.2.3', latestCheckedAt: Date.now() / 1000 - 2 * 86_400 })));
  assert.ok(stale.some((node) => node.dataset.action === 'check-provenance'));
  assert.ok(!stale.some((node) => node.dataset.fixAction === 'pinPackageVersion'));

  const checking = buttons(renderDoctor({ ...withProvenance({ latestVersion: null }), provenanceChecking: new Set(['pkg']) }));
  assert.ok(checking.some((node) => node.dataset.action === 'noop' && node.textContent === 'Checking…'));
});

test('the pin sheet shows each distinct argument change and applies the previewed version', () => {
  const tree = renderDoctorFix({
    clients: [{ id: 'cursor', displayName: 'Cursor' }, { id: 'codex', displayName: 'Codex' }],
    sheet: {
      kind: 'doctorFix',
      busy: false,
      error: null,
      preview: {
        serverID: 'pkg',
        serverName: 'pkg',
        currentCommand: 'npx',
        replacementCommand: 'npx',
        clientIDs: ['codex', 'cursor'],
        action: 'pinPackageVersion',
        packageName: 'pkg',
        version: '1.2.3',
        argumentChanges: [
          { clientID: 'codex', current: 'pkg@latest', replacement: 'pkg@1.2.3' },
          { clientID: 'cursor', current: 'pkg', replacement: 'pkg@1.2.3' },
        ],
      },
    },
  });

  const text = [...walk(tree)].map((node) => node.textContent);
  assert.ok(text.includes('Pin package version'));
  assert.ok(text.includes('pkg@latest'));
  assert.ok(text.includes('pkg'));
  assert.ok(text.includes('In Codex.'));
  assert.ok(text.includes('In Cursor.'));
  const apply = buttons(tree).find((node) => node.dataset.action === 'doctor-apply-fix');
  assert.equal(apply?.textContent, 'Pin to 1.2.3');
});
