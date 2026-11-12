// Actionable diagnostics derived natively from configuration and measured
// health data. The web layer displays findings and requests explicit actions;
// it never guesses paths, commands or edits.

import { el, button, sheet } from '../dom.js';
import { clientNames } from '../state.js';
import { doctorAuthAction } from './remoteAuth.js';

export function renderDoctor(state) {
  const panel = el('div', 'doctor-panel');
  panel.dataset.scrollKey = 'doctor';
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'MCP Doctor'));
  copy.append(el('p', 'muted', 'Find startup, environment and portability problems before opening every client log.'));
  // The same run the matrix toolbar starts; both wait on it.
  const checkingAll = Boolean(state.healthProgress);
  const checkAll = button(checkingAll ? 'Checking…' : 'Check all servers', checkingAll ? 'noop' : 'check-all', {
    className: checkingAll ? 'button busy' : 'button',
  });
  head.append(copy, el('span', 'spacer'), checkAll);
  panel.append(head);

  const reports = state.doctorReports ?? [];
  if (reports.length === 0) {
    panel.append(el('div', 'placeholder', 'No actionable server findings.'));
    const gateway = gatewayCard(state);
    if (gateway) panel.append(gateway);
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
      const signIn = doctorAuthAction(state, report.serverID, finding);
      if (signIn) item.append(signIn);
      if (finding.action === 'runHealthCheck') {
        const checking = state.checking.has(report.serverID) || checkingAll;
        item.append(button(checking ? 'Checking…' : 'Run health check', checking ? 'noop' : 'check-server', {
          className: checking ? 'button busy' : 'button',
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
      } else if (finding.action === 'checkPackageSafety') {
        const action = packageSafetyAction(state, report.serverID);
        if (action) item.append(action);
      } else if (finding.action === 'repairGatewayRoute' && finding.routeID) {
        const busy = state.gatewayRepairBusyRouteID === finding.routeID;
        const repair = button(busy ? 'Repairing…' : 'Repair route', busy ? 'noop' : 'repair-gateway', {
          className: busy ? 'button primary busy' : 'button primary',
          dataset: { routeId: finding.routeID },
        });
        repair.disabled = busy;
        item.append(repair);
      } else if (finding.action === 'restoreDirectMode' && finding.routeID) {
        item.append(button('Restore Direct mode…', 'restore-gateway', {
          className: 'button',
          dataset: { routeId: finding.routeID },
        }));
      }
      card.append(item);
    }
    list.append(card);
  }
  panel.append(list);
  const gateway = gatewayCard(state);
  if (gateway) panel.append(gateway);
  return panel;
}

/**
 * The way out of Gateway mode for everything at once. A config in Gateway mode points
 * at a helper inside Kytto's folder, so removing Kytto without this leaves every such
 * server unable to start — and the client would only say "failed".
 */
function gatewayCard(state) {
  const routes = state.gatewayRoutes ?? [];
  if (routes.length === 0) return null;
  const card = el('section', 'doctor-card doctor-gateway');
  const title = el('div', 'doctor-card-head');
  title.append(el('h3', null, 'Gateway mode'));
  title.append(button('Restore Direct mode for all…', 'restore-all-gateway', { className: 'button subtle' }));
  card.append(title);
  const count = routes.length === 1 ? '1 server definition starts' : `${routes.length} server definitions start`;
  card.append(el('p', 'muted', `${count} through Kytto’s helper. Before you uninstall, move or replace Kytto, restore Direct mode so those clients can still start their servers.`));
  return card;
}

export function renderGatewayRestoreAll(state) {
  const routes = state.gatewayRoutes ?? [];
  const body = [
    el('p', null, `Restore Direct mode for ${routes.length === 1 ? 'the 1 server' : `all ${routes.length} servers`} in Gateway mode?`),
  ];
  const list = el('ul', 'plain-list');
  for (const route of routes) {
    list.append(el('li', null, `${route.serverName} — ${clientNames(state, [route.clientID])}`));
  }
  body.push(list);
  body.push(el('p', 'muted', 'Kytto puts each original definition back, backs up every config first and removes the routes. Tool masks set on these routes stop applying. Restart the clients afterwards.'));
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));
  return sheet('Restore Direct mode', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Restoring…' : 'Restore all', state.sheet.busy ? 'noop' : 'confirm-restore-all-gateway', {
      className: state.sheet.busy ? 'button busy' : 'button',
    }),
  ]);
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

/// The safety check reaches osv.dev, so it only ever runs from this click.
export function packageSafetyAction(state, serverId, className = 'button') {
  if (!state.app?.capabilities?.includes('package-safety')) return null;
  const busy = state.packageSafetyBusyServerID === serverId;
  const check = button(busy ? 'Checking…' : 'Check package safety', busy ? 'noop' : 'check-package-safety', {
    className: busy ? `${className} busy` : className,
    dataset: { serverId },
  });
  check.disabled = busy;
  return check;
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
  body.push(el('p', 'muted', `Affected client definitions: ${clientNames(state, preview.clientIDs)}. Kytto will preview this here, back up every affected config, preserve unrelated content and write atomically.`));
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

function commandBlock(label, command) {
  const block = el('div', 'doctor-command');
  block.append(el('span', 'field-label', label));
  block.append(el('code', 'mono selectable', command));
  return block;
}
