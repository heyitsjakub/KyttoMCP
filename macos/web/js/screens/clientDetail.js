// One client, from its own point of view (§7.8).
//
// The matrix answers "which server is where" and pays for it in width: a client
// is fifteen pixels of icon and a column of switches. The questions it cannot
// answer are the ones about a single client — what is in this file, what does it
// cost me in context, what happens when I switch something off here, and what
// could I add. Those are what this screen is for.
//
// It writes through exactly the same control as the matrix (`serverRow.js`), so
// nothing here is a second way to change a file.

import { el, button, clientIcon, definitionList, formatTokens } from '../dom.js';
import { changedIn } from '../anim.js';
import { cellKey, clientTokenTotal, contextWindow, serversIn, serversNotIn } from '../state.js';
import { toggle, cellSnapshot, serverOpener, dotSnapshot } from './serverRow.js';

/// The four states a client can be in, said in the client's own screen where
/// there is room for the consequence as well as the fact.
const clientState = (platform) => ({
  ready: { badge: null, note: null },
  noConfig: {
    badge: 'never configured',
    note: 'This client is installed but has never configured an MCP server. Adding one here creates the file.',
  },
  orphanedConfig: {
    badge: 'not installed',
    note: 'This configuration is still on disk, but the client itself is not installed. Kytto reads and writes it anyway — uninstalling an app does not make its config wrong.',
  },
  notInstalled: {
    badge: 'not installed',
    note: `Kytto did not find this client on ${platform}.`,
  },
});

export function renderClient(state) {
  const client = state.clients.find((entry) => entry.id === state.selectedClient);
  if (!client) {
    const missing = el('div', 'placeholder');
    missing.append(el('p', null, 'That client is no longer in the list.'));
    return missing;
  }

  const panel = el('div', 'table-scroll');
  const states = clientState(state.app?.platformDisplayName ?? 'this platform');
  const status = states[client.state] ?? states.ready;

  panel.append(header(client, status));
  panel.append(stats(state, client));
  if (status.note) panel.append(el('p', 'panel-note', status.note));

  const present = serversIn(client.id);
  // An extension is installed software belonging to the client that installed
  // it, so it is never a candidate for this one. The matrix shows those cells
  // disabled because it shows every cell; a list headed "you could add these"
  // has no business listing them at all.
  const absent = serversNotIn(client.id).filter((server) => !server.isBundled);

  // Cells on this screen are diffed under their own name: it is the same set of
  // servers as the matrix but one column of it, and a group snapshot that
  // changed shape between two renders would report the whole column as moved.
  const flipped = changedIn('client-cells', cellSnapshot([client], state.servers));
  const settled = changedIn('client-dots', dotSnapshot(state.servers), 420);

  if (present.length > 0) {
    panel.append(
      serverTable(state, client, present, {
        title: 'Servers in this client',
        flipped,
        settled,
      }),
    );
  } else if (client.state !== 'notInstalled') {
    const empty = el('p', 'muted', 'No MCP servers are configured in this client.');
    panel.append(section('Servers in this client', [empty]));
  }

  // Adding a server to a client that is not on this machine would write a file
  // for software that cannot read it, so that list is only offered where it
  // means something.
  if (absent.length > 0 && client.state !== 'notInstalled' && !client.isReadOnly) {
    panel.append(
      serverTable(state, client, absent, {
        title: 'Configured elsewhere',
        hint: `Servers Kytto found in your other clients. Switching one on writes it into ${client.displayName}.`,
        flipped,
        settled,
      }),
    );
  }

  panel.append(behaviour(client));

  // Diagnostics are deliberately not repeated here: the band at the foot of the
  // content pane carries them on every screen, and a client's own problem would
  // otherwise be on screen twice at once.
  const backups = state.backups.filter((entry) => entry.clientID === client.id);
  if (backups.length > 0) panel.append(recentBackups(backups));

  return panel;
}

function header(client, status) {
  const head = el('header', 'client-head');

  const icon = el('div', 'client-hero-icon');
  icon.append(clientIcon(client));
  head.append(icon);

  const identity = el('div', 'client-identity');
  const line = el('div', 'client-name-line');
  line.append(el('h2', 'client-name', client.displayName));
  const rawScope = client.configurationScope ?? 'global';
  const scopeName = rawScope[0].toUpperCase() + rawScope.slice(1);
  line.append(el('span', 'badge', client.scopeLabel ? `${scopeName} · ${client.scopeLabel}` : scopeName));
  if (client.isReadOnly) line.append(el('span', 'badge', 'read-only'));
  if (status.badge) line.append(el('span', 'badge', status.badge));
  identity.append(line);
  // The file is the thing this screen is about, so it is under the name rather
  // than in a tooltip. Selectable: pasting it into a terminal is a real use.
  identity.append(el('div', 'client-path mono selectable', client.configPathDisplay));
  head.append(identity);

  return head;
}

/// The three numbers that are only true of one client. The share of the context
/// window in particular has no meaning in the matrix — it is one client's window
/// that fills up, not the sum of all of them (§7.1).
function stats(state, client) {
  const row = el('div', 'client-stats');

  const present = serversIn(client.id);
  const on = present.filter((server) => server.enabledIn[client.id] === 'enabled');
  row.append(stat(`${on.length}/${present.length}`, 'servers switched on'));

  const total = clientTokenTotal(client.id);
  // Only the servers that are on can contribute, so only they decide whether
  // there is a figure to show. A measured server sitting switched off would
  // otherwise turn "nothing has been checked here" into a confident "~0".
  const known = on.filter((server) => server.tokenWeight);
  if (known.length > 0) {
    const share = (total / contextWindow()) * 100;
    const heavy = total >= (state.settings?.tokenWarningThreshold ?? 20_000);
    const incomplete = known.length < on.length;
    const approximate = known.some((server) => !server.tokenWeight.isMeasured);
    row.append(
      stat(
        `${incomplete ? '≥' : approximate ? '~' : ''}${formatTokens(total)}`,
        `${incomplete ? 'known tokens minimum' : 'tokens'} · ${share.toFixed(share < 10 ? 1 : 0)}% of context`,
        heavy ? 'heavy' : null,
      ),
    );
  } else {
    row.append(stat('—', 'context cost not measured'));
  }

  // Always a number, including zero: a tile that disappears when it has nothing
  // to say is a tile you cannot trust when it says nothing.
  const pending = state.pendingRestarts[client.id] ?? 0;
  row.append(stat(String(pending), pending === 1 ? 'change awaiting restart' : 'changes awaiting restart'));

  return row;
}

function stat(value, label, modifier) {
  const box = el('div', `client-stat${modifier ? ` ${modifier}` : ''}`);
  box.append(el('span', 'client-stat-value', value));
  box.append(el('span', 'client-stat-label', label));
  return box;
}

function section(title, children, hint) {
  const box = el('section', 'client-section');
  box.append(el('h3', null, title));
  if (hint) box.append(el('p', 'field-hint', hint));
  box.append(...children);
  return box;
}

/// One column of the matrix, given the room the matrix could not spare: the
/// command, the token weight and the switch, per server.
function serverTable(state, client, servers, { title, hint, flipped, settled }) {
  // No header row: the section title says what the list is, and three columns
  // labelled twice on one screen is furniture rather than information.
  const table = el('table', 'matrix client-servers');
  const body = el('tbody');
  for (const server of servers) {
    const tr = el('tr');
    tr.dataset.serverId = server.id;

    const nameCell = el('td', 'col-server');
    nameCell.append(serverOpener(state, server, settled.has(server.id)));
    tr.append(nameCell);

    tr.append(weightCell(server));

    const value = server.enabledIn[client.id] ?? 'absent';
    const switchCell = el('td', `col-client cell-${value}`);
    switchCell.append(toggle(state, server, client, value, flipped.has(cellKey(server.id, client.id))));
    tr.append(switchCell);

    body.append(tr);
  }

  table.append(body);
  return section(title, [table], hint);
}

/// The matrix draws a bar here because it is comparing rows against each other.
/// This table is one client's list, so the figure is enough.
function weightCell(server) {
  const cell = el('td', 'col-weight');
  cell.append(
    server.tokenWeight
      ? el('span', 'weight-value', `${server.tokenWeight.isMeasured ? '' : '~'}${formatTokens(server.tokenWeight.estimate)}`)
      : el('span', 'weight-none', '—'),
  );
  return cell;
}

/// Why this client behaves unlike the one above it in the sidebar. Every string
/// here is written down in the client registry, native side — the UI is not
/// where a client's quirks are decided (§4).
function behaviour(client) {
  const pairs = [['Config file', client.configPathDisplay, 'mono selectable']];
  const rawScope = client.configurationScope ?? 'global';
  const scopeName = rawScope[0].toUpperCase() + rawScope.slice(1);
  pairs.push(['Scope', client.scopeLabel ? `${scopeName} · ${client.scopeLabel}` : scopeName]);
  if (client.configFormatDisplay) pairs.push(['Format', client.configFormatDisplay]);
  if (client.offSwitchSummary) pairs.push(['Switching off', client.offSwitchSummary]);

  const children = [definitionList(pairs)];
  if (client.schemaQuirks) children.push(el('p', 'muted', client.schemaQuirks));
  return section('How Kytto handles this client', children);
}

/// The last few versions of this client's file, revertable from here. The
/// Backups screen lists every client's; this is the one that is about to matter
/// after a toggle went wrong.
function recentBackups(backups) {
  const list = el('ul', 'plain-list');
  for (const backup of backups.slice(0, 5)) {
    const item = el('li');
    item.append(el('span', null, new Date(backup.takenAt * 1000).toLocaleString()));
    item.append(el('span', 'spacer'));
    const revert = button('Revert', 'restore-backup', {
      className: 'button subtle',
      dataset: { backupId: backup.id, clientId: backup.clientID },
    });
    revert.title = 'Put this version back. The current file is backed up first.';
    item.append(revert);
    list.append(item);
  }
  return section('Recent backups', [list]);
}
