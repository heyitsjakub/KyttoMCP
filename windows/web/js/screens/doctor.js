// Actionable diagnostics derived natively from configuration and measured
// health data. The web layer displays findings and requests explicit actions;
// it never guesses paths, commands or edits.

import { el, button, sheet } from '../dom.js';

export function renderDoctor(state) {
  const panel = el('div', 'doctor-panel');
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'MCP Doctor'));
  copy.append(el('p', 'muted', 'Find startup, environment and portability problems before opening every client log.'));
  head.append(copy, el('span', 'spacer'), button('Check all servers', 'check-all'));
  panel.append(head);

  const reports = state.doctorReports ?? [];
  if (reports.length === 0) {
    panel.append(el('div', 'placeholder', 'No actionable server findings.'));
    return panel;
  }

  const list = el('div', 'doctor-list');
  for (const report of reports) {
    const card = el('section', 'doctor-card');
    const title = el('div', 'doctor-card-head');
    title.append(el('h3', null, report.serverName));
    title.append(button('Open server', 'open-server', {
      className: 'button subtle',
      dataset: { serverId: report.serverID },
    }));
    card.append(title);

    for (const finding of report.findings) {
      const item = el('div', `doctor-finding ${finding.severity}`);
      item.append(el('strong', null, finding.title));
      item.append(el('p', null, finding.detail));
      item.append(el('p', 'muted', finding.remediation));
      if (finding.action === 'runHealthCheck') {
        item.append(button('Run health check', 'check-server', {
          dataset: { serverId: report.serverID },
        }));
      } else if (finding.action === 'pinResolvedCommand') {
        item.append(button('Preview safe fix…', 'doctor-preview-fix', {
          className: 'button primary',
          dataset: { serverId: report.serverID, fixAction: 'pinResolvedCommand' },
        }));
      } else if (finding.action === 'pinPackageVersion') {
        const action = packagePinAction(state, report.serverID);
        if (action) item.append(action);
      }
      card.append(item);
    }
    list.append(card);
  }
  panel.append(list);
  return panel;
}

/** How long a lookup stays good enough to pin to; native enforces the same window. */
const LOOKUP_FRESHNESS_SECONDS = 24 * 60 * 60;

/**
 * Pinning needs a version, and the only source is a registry lookup the user
 * asks for — so until a recent one has happened the button is that lookup, and
 * after it the button names the release it would pin. Nothing here reaches the
 * network on its own (§7.10).
 */
function packagePinAction(state, serverId) {
  const provenance = state.servers.find((entry) => entry.id === serverId)?.provenance;
  const checkedAt = provenance?.latestCheckedAt;
  const fresh = typeof checkedAt === 'number' && Date.now() / 1000 - checkedAt < LOOKUP_FRESHNESS_SECONDS;
  if (provenance?.latestVersion && fresh) {
    return button(`Pin to ${provenance.latestVersion}…`, 'doctor-preview-fix', {
      className: 'button primary',
      dataset: { serverId, fixAction: 'pinPackageVersion' },
    });
  }
  if (!provenance?.packageName || !state.app?.capabilities?.includes('provenance')) return null;
  const busy = state.provenanceChecking?.has(serverId) === true;
  return button(busy ? 'Checking…' : 'Check latest release', busy ? 'noop' : 'check-provenance', {
    className: busy ? 'button busy' : 'button',
    dataset: { serverId },
  });
}

export function renderDoctorFix(state) {
  const preview = state.sheet.preview;
  if (preview.action === 'pinPackageVersion') return renderPackagePin(state, preview);
  const body = [
    el('p', null, `Pin the executable for “${preview.serverName}” to the path verified by its successful health check?`),
  ];
  const comparison = el('div', 'doctor-fix-comparison');
  comparison.append(commandBlock('Configured now', preview.currentCommand));
  comparison.append(commandBlock('After', preview.replacementCommand));
  body.push(comparison);
  body.push(el('p', 'muted', `Affected client definitions: ${preview.clientIDs.join(', ')}. Kytto will preview this here, back up every affected config, preserve unrelated content and write atomically.`));
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Safe repair preview', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Applying…' : 'Apply repair', state.sheet.busy ? 'noop' : 'doctor-apply-fix', {
      className: state.sheet.busy ? 'button primary busy' : 'button primary',
      dataset: { serverId: preview.serverID },
    }),
  ]);
}

function renderPackagePin(state, preview) {
  const body = [
    el('p', null, `Pin ${preview.packageName} for “${preview.serverName}” to ${preview.version}?`),
  ];

  // Copies usually agree. When they do not, each distinct change is shown with
  // the clients it applies to, so the preview is exactly what will be written.
  const groups = new Map();
  for (const change of preview.argumentChanges ?? []) {
    const key = `${change.current}\u0000${change.replacement}`;
    const group = groups.get(key) ?? { current: change.current, replacement: change.replacement, clientIDs: [] };
    group.clientIDs.push(change.clientID);
    groups.set(key, group);
  }
  for (const group of groups.values()) {
    const comparison = el('div', 'doctor-fix-comparison');
    comparison.append(commandBlock('Argument now', group.current));
    comparison.append(commandBlock('After', group.replacement));
    body.push(comparison);
    if (groups.size > 1) body.push(el('p', 'muted', `In ${clientNames(state, group.clientIDs)}.`));
  }

  body.push(el('p', 'muted', `${preview.version} is the latest release the registry reported when you checked. Pinning stops newer releases from starting without you choosing them; it does not vouch for this one, and its dependencies still resolve within their own ranges.`));
  body.push(el('p', 'muted', `Affected client definitions: ${clientNames(state, preview.clientIDs)}. Kytto changes only this one argument — in a switched-off copy it is holding too — backs up every affected config, preserves unrelated content and writes atomically.`));
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Pin package version', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Pinning…' : `Pin to ${preview.version}`, state.sheet.busy ? 'noop' : 'doctor-apply-fix', {
      className: state.sheet.busy ? 'button primary busy' : 'button primary',
      dataset: { serverId: preview.serverID },
    }),
  ]);
}

function clientNames(state, clientIDs) {
  return clientIDs
    .map((id) => state.clients.find((client) => client.id === id)?.displayName ?? id)
    .join(', ');
}

function commandBlock(label, command) {
  const block = el('div', 'doctor-command');
  block.append(el('span', 'field-label', label));
  block.append(el('code', 'mono selectable', command));
  return block;
}
