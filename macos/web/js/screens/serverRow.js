// The two halves of a matrix row: the name block that opens a server, and the
// switch that writes it into a client.
//
// They live here because two screens draw them — the matrix, where the switches
// are a grid, and the client screen (§7.8), where one column of that grid is a
// list. The switch in particular is the one control in the app that changes a
// file on disk: what a click does, what a cell may not do, and how it confirms
// that the disk agreed are stated once, because a second copy is how they drift.

import { el, glyph, GLYPH } from '../dom.js';
import { cellKey, matchingTools } from '../state.js';

const CELL_LABEL = {
  enabled: 'Enabled — click to turn off and update this client configuration',
  disabled: 'Disabled — click to turn on and update this client configuration',
  absent: 'Not configured — click to add it to this client configuration',
};

export const RELATIVE_PATH_HINT =
  'Not configured. This server is launched through a relative path, which is ' +
  'resolved against the directory the client runs from — so adding it here ' +
  'will write correctly and may still not start. Click to add it anyway.';

/**
 * @param {boolean} flipped this cell is the one that just changed, and says so once
 * @returns {HTMLTableCellElement}
 */
export function toggleCell(state, server, client, flipped) {
  const value = server.enabledIn[client.id] ?? 'absent';
  const cell = el('td', `col-client cell-${value}`);
  cell.append(toggle(state, server, client, value, flipped));
  return cell;
}

/// The switch itself, for anywhere that is not a table cell.
export function toggle(state, server, client, value, flipped) {
  const button = el('button', `cell-toggle ${value}`);
  button.dataset.action = 'toggle';
  button.dataset.serverId = server.id;
  button.dataset.clientId = client.id;
  button.dataset.enabled = String(value === 'enabled');
  button.setAttribute('role', 'switch');
  button.setAttribute('aria-checked', String(value === 'enabled'));
  button.setAttribute('aria-label', `${server.name} in ${client.displayName}`);
  button.title = CELL_LABEL[value];

  // An extension is an installed bundle, not a definition that can be copied.
  const portable = !(server.isBundled && value === 'absent');
  if (client.isReadOnly) {
    button.disabled = true;
    button.title = `${client.displayName} is a read-only custom source. Kytto never changes this file.`;
  } else if (client.state === 'notInstalled') {
    button.disabled = true;
    button.title = `${client.displayName} was not detected. Set its configuration path in Settings if it is installed somewhere unusual.`;
  } else if (!portable) {
    button.disabled = true;
    button.title = 'Claude Desktop extensions cannot be copied into another client.';
  } else if (value === 'absent' && server.hasRelativePath) {
    // Copyable, and probably not worth copying. Nothing stops the click — the
    // file is written correctly either way and the user may know something Kytto
    // does not — but the cell stops looking like the other empty ones first.
    button.classList.add('unportable');
    button.title = RELATIVE_PATH_HINT;
  }

  if (state.busyCells.has(cellKey(server.id, client.id))) {
    button.classList.add('busy');
    button.disabled = true;
  }

  // The write landed and the file on disk now says something else. The cell is
  // the only place that can confirm it, and it does so once.
  const mark = el('span', `cell-mark ${value}${flipped ? ' flipped' : ''}`);
  // On and off are the two positions of one switch, so both carry the mark that
  // tells them apart — off keeps it hidden and ghosts it in under the pointer,
  // which is the whole preview of what a click does. Absent is not a switch in
  // the off position and does not get one: hovering it outlines the control it
  // would create instead.
  if (value !== 'absent') {
    const check = glyph(GLYPH.check);
    check.classList.add('cell-check');
    mark.append(check);
  }
  button.append(mark);
  return button;
}

/// What each cell currently says, for `changedIn` to diff against the render
/// before it. Both screens snapshot the same way, under their own group name.
export const cellSnapshot = (clients, servers) =>
  new Map(
    servers.flatMap((server) =>
      clients.map((client) => [
        cellKey(server.id, client.id),
        server.enabledIn[client.id] ?? 'absent',
      ]),
    ),
  );

// MARK: - The name block

const DOT_STATE = {
  passed: 'ok',
  failed: 'fail',
  unsupported: 'unchecked',
  needsAuthorization: 'auth',
};

const DOT_LABEL = {
  passed: 'Health check passed',
  failed: 'Health check failed',
  unsupported: 'Not checked — remote server',
  needsAuthorization: 'Waiting for you to authorize it — open the server for the sign-in link',
};

/// Health, name, what it is, and what it runs — as one button that opens the
/// detail sheet (§7.5).
///
/// @param {boolean} settled a check just finished, and the dot says so once.
export function serverOpener(state, server, settled) {
  const opener = el('button', 'server-opener');
  opener.dataset.action = 'open-server';
  opener.dataset.serverId = server.id;

  const busy = state.checking.has(server.id);
  const status = server.health?.status;
  const settledNow = settled && !busy ? ' settled' : '';
  const dot = el('span', `status-dot ${busy ? 'checking' : DOT_STATE[status] ?? 'unchecked'}${settledNow}`);
  dot.title = busy ? 'Checking…' : (DOT_LABEL[status] ?? 'Not checked yet');

  const title = el('div', 'server-title');
  title.append(dot);
  title.append(el('span', 'server-name', server.name));
  // One row, several files, and they do not agree. The row has to print one
  // command and cannot print all of them, so it says that the one on screen is
  // not the whole story — the detail sheet has room for the rest.
  if ((state.drift ?? []).some((entry) => entry.serverID === server.id)) {
    const badge = el('span', 'badge drift', 'differs');
    badge.title = 'This server is configured differently in different clients. Open it to compare.';
    title.append(badge);
  }
  // A server that changed what it tells the model. The alert was only ever
  // inside the detail sheet, which meant finding it required already suspecting
  // it — the one thing a rug pull relies on you not doing.
  if ((state.contractAlerts ?? []).some((entry) => entry.serverID === server.id)) {
    const badge = el('span', 'badge contract-changed', 'contract changed');
    badge.title =
      'The tools this server offers, or what it says about them, changed since the last check. Open it to review.';
    title.append(badge);
  }
  if (server.isBundled) title.append(el('span', 'badge', 'extension'));
  if (server.transport !== 'stdio') title.append(el('span', 'badge', server.transport));
  if (server.health?.toolCount != null) {
    const count = server.health.toolCount;
    title.append(el('span', 'tool-count', `${count} ${count === 1 ? 'tool' : 'tools'}`));
  }
  opener.append(title);

  // A failed check replaces the command with the reason it failed — that is what
  // the user needs at a glance, and the command is one click away in the detail.
  if (status === 'failed' && server.health.message) {
    opener.append(el('div', 'server-command failure', firstLine(server.health.message)));
  } else if (status === 'needsAuthorization') {
    // Its own colour and its own words, because red here was the bug: the
    // server is healthy and waiting on the user, not broken (§7.3).
    opener.append(el('div', 'server-command authwait', 'Waiting for you to authorize it in the browser'));
  } else {
    // A row that is only on screen because one of its tools matched has to say
    // which one, or it reads as a filter that is simply broken.
    const hits = matchingTools(server);
    opener.append(
      hits.length > 0
        ? el('div', 'server-command matched-tools', hits.join(', '))
        : el('div', 'server-command', server.commandSummary),
    );
  }
  return opener;
}

const firstLine = (text) => text.split('\n')[0];

/// Keyed by the time of the check as well as its verdict, so re-checking a
/// server that passes again still confirms that something happened.
export const dotSnapshot = (servers) =>
  new Map(
    servers.map((server) => [
      server.id,
      `${server.health?.status ?? 'none'}:${server.health?.checkedAt ?? ''}`,
    ]),
  );
