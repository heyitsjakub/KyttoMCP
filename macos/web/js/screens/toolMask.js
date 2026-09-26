// Choosing which of a server's tools one client may see (§7.11).
//
// This is the screen where the token weight stops being a diagnosis and becomes
// something you can act on: every row carries what that tool costs, and the
// footer says what the current selection saves. The numbers come from the last
// health check — native measured them, this screen only adds them up.
//
// Masking is per route, and a route is one server in one client. That is not an
// accident of the data model: the same server can reasonably be a full toolbox
// in an editor and three read-only tools in a chat client.

import { el, button, sheet, formatTokens } from '../dom.js';

export function renderToolMask(state) {
  const { routeId, selected, busy, error } = state.sheet;
  const route = state.gatewayRoutes.find((entry) => entry.id === routeId);
  if (!route) return null;

  const server = state.servers.find((entry) => entry.id === route.serverID);
  const client = state.clients.find((entry) => entry.id === route.clientID);
  const tools = server?.health?.tools ?? [];

  const body = [];
  body.push(
    el(
      'p',
      'muted',
      `Only the tools you keep are offered to ${client?.displayName ?? route.clientID}. `
        + 'Everything else is hidden from its tool list, and calls to it are refused by Kytto before they reach the server.',
    ),
  );

  // Without a health check there is no tool list and no cost to weigh, so there
  // is nothing honest to choose from.
  if (tools.length === 0) {
    body.push(
      el(
        'div',
        'profile-empty',
        'Kytto has not seen this server’s tools yet. Run a health check on it first, then come back and choose.',
      ),
    );
    return sheet(`Tools exposed to ${client?.displayName ?? route.clientID}`, body, [
      button('Close', 'close-sheet', { className: 'button subtle' }),
    ]);
  }

  const chosen = new Set(selected);
  const list = el('div', 'mask-list');
  for (const tool of byWeight(tools)) {
    list.append(toolRow(tool, chosen.has(tool.name)));
  }
  body.push(list);

  const footer = [];
  footer.push(summary(tools, chosen));
  footer.push(el('span', 'spacer'));
  if (error) footer.push(el('span', 'sheet-error', error));
  footer.push(button('Cancel', 'close-sheet', { className: 'button subtle' }));

  const save = button(busy ? 'Saving…' : 'Save', 'confirm-tool-mask', {
    className: 'button primary',
    dataset: { routeId },
  });
  save.disabled = Boolean(busy);
  footer.push(save);

  return sheet(`Tools exposed to ${client?.displayName ?? route.clientID}`, body, footer);
}

/// Heaviest first — the same order as the breakdown in server detail, and the
/// order that puts the tools worth un-checking at the top.
function byWeight(tools) {
  const measured = tools.filter((tool) => tool.tokenCount != null);
  if (measured.length === 0) return tools;
  return [
    ...measured.slice().sort((left, right) => right.tokenCount - left.tokenCount),
    ...tools.filter((tool) => tool.tokenCount == null),
  ];
}

function toolRow(tool, isChosen) {
  const row = el('label', `mask-row${isChosen ? '' : ' hidden-tool'}`);

  const box = el('input');
  box.type = 'checkbox';
  box.checked = isChosen;
  box.dataset.action = 'toggle-mask-tool';
  box.dataset.toolName = tool.name;
  row.append(box);

  const copy = el('div', 'mask-copy');
  const title = el('div', 'mask-title');
  title.append(el('span', 'mono', tool.name));
  for (const label of annotationLabels(tool.annotations)) {
    title.append(el('span', `badge tool-annotation ${label.kind}`, label.label));
  }
  copy.append(title);
  if (tool.description) copy.append(el('p', 'muted', firstLine(tool.description)));
  row.append(copy);

  row.append(el('span', 'mask-tokens mono', tool.tokenCount == null ? '—' : formatTokens(tool.tokenCount)));
  return row;
}

/// What the current selection costs and what it saves.
///
/// Both numbers are shown because either alone misleads: "saves 12k" invites
/// hiding everything, and "costs 6k" hides the reason you came here.
function summary(tools, chosen) {
  const measured = tools.filter((tool) => tool.tokenCount != null);
  const kept = measured.filter((tool) => chosen.has(tool.name));
  const keptTokens = kept.reduce((total, tool) => total + tool.tokenCount, 0);
  const savedTokens = measured.reduce((total, tool) => total + tool.tokenCount, 0) - keptTokens;

  const line = el('div', 'mask-summary');
  line.append(el('strong', null, `${chosen.size} of ${tools.length} tools`));
  if (measured.length === 0) {
    line.append(el('span', 'muted', 'no measurements yet'));
    return line;
  }
  line.append(el('span', 'muted', `${formatTokens(keptTokens)} tokens`));
  if (savedTokens > 0) line.append(el('span', 'mask-saved', `−${formatTokens(savedTokens)} saved`));
  if (measured.length < tools.length) {
    line.append(el('span', 'muted', `${tools.length - measured.length} unmeasured`));
  }
  return line;
}

function annotationLabels(annotations) {
  if (!annotations) return [];
  const labels = [];
  if (annotations.readOnlyHint === true) labels.push({ label: 'read only', kind: 'safe' });
  if (annotations.destructiveHint === true) labels.push({ label: 'destructive', kind: 'danger' });
  if (annotations.openWorldHint === true) labels.push({ label: 'external', kind: 'warning' });
  return labels;
}

const firstLine = (text) => text.split('\n')[0];
