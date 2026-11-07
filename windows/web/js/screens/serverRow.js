// The two halves of a matrix row: the name block that opens a server, and the
// switch that writes it into a client.
//
// They live here because two screens draw them — the matrix, where the switches
// are a grid, and the client screen (§7.8), where one column of that grid is a
// list. The switch in particular is the one control in the app that changes a
// file on disk: what a click does, what a cell may not do, and how it confirms
// that the disk agreed are stated once, because a second copy is how they drift.

import { el, glyph, GLYPH } from '../dom.js';
import { cellKey, matchingTools, serverIsReadOnly, sourceKind } from '../state.js';

const CELL_LABEL = {
  enabled: 'Enabled — click to turn off. Clicking updates the client configuration.',
  disabled: 'Disabled — click to turn on. Clicking updates the client configuration.',
  absent: 'Not configured — click to add it. Clicking updates the client configuration.',
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
  button.dataset.state = value;
  button.setAttribute('role', 'switch');
  button.setAttribute('aria-checked', String(value === 'enabled'));
  button.setAttribute('aria-label', `${server.name} in ${client.displayName}`);
  button.title = CELL_LABEL[value];

  // A stable column must not turn an undetected client into a write target. A
  // custom config path in Settings is the explicit way to make it editable.
  const portable = !(server.isBundled && value === 'absent');
  if (isPlugin(server)) {
    // A plugin is installed software that the owning client registers and
    // enables; the file Kytto can read is its cache, which the client rewrites.
    // Unlike a Claude Desktop extension, which carries its own on/off flag in the
    // client that installed it, there is no switch of ours to flip (§4).
    button.disabled = true;
    button.classList.add('read-only');
    button.title =
      value === 'absent'
        ? 'Plugin servers belong to their client installation and cannot be copied.'
        : 'Read-only plugin. Manage its enablement in the owning client.';
  } else if (client.isReadOnly) {
    button.disabled = true;
    button.classList.add('read-only');
    button.title = 'Kytto never changes this file.';
  } else if (client.state === 'notInstalled') {
    button.disabled = true;
    button.title =
      `${client.displayName} was not detected. Set its configuration path in ` +
      'Settings if it is installed somewhere unusual.';
  } else if (!portable) {
    // An extension is an installed bundle, not a definition that can be copied.
    button.disabled = true;
    button.title = 'Installed extensions and plugins cannot be copied into another client.';
  } else if (value === 'absent' && server.hasRelativePath) {
    // Copyable, and probably not worth copying. Nothing stops the click — the
    // file is written correctly either way and the user may know something Kytto
    // does not — but the cell stops looking like the other empty ones first.
    button.classList.add('unportable');
    button.title = RELATIVE_PATH_HINT;
  }

  if (state.busyCells.has(cellKey(server.id, client.id))) {
    // Not `disabled`: a disabled control drops focus, and the write this cell is
    // waiting on is the one that moves focus back to it (audit W6). The click
    // handler ignores busy cells.
    button.classList.add('busy');
    button.setAttribute('aria-disabled', 'true');
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

/// Plugin servers are read-only everywhere, in every client column. The three
/// spellings are the same fact from three generations of the payload: the
/// source kind, the origin kind, and a bundle whose source says read-only.
export const isPlugin = (server) =>
  server.originKind === 'plugin' ||
  server.source?.kind === 'plugin' ||
  (server.isBundled === true && server.source?.readOnly === true);

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
  unsupported: 'Check unsupported — open details for the reason',
  needsAuthorization: 'Waiting for you to authorize it — open the server for the sign-in link',
};

/// The same verdict in words. The dot is a redundant cue, never the only one
/// (§7.1.2): a row has to read correctly to someone who cannot tell green from
/// red, and in compact density it is the only line that says how the last check
/// went.
const STATUS_TEXT = {
  passed: 'Check passed',
  failed: 'Check failed',
  needsAuthorization: 'Sign-in required',
  unsupported: 'Check unavailable',
};

/// "Read-only · Plugin · Demo tools 2.0" — what kind of thing this row is, who
/// supplied it, and whether Kytto may touch it.
export function sourceLabel(server) {
  const source = server.source ?? { kind: sourceKind(server), displayName: null, version: null };
  const name = source.displayName ?? '';
  const version = source.version ? ` ${source.version}` : '';
  const kind =
    source.kind === 'plugin'
      ? `Plugin${name ? ` · ${name}${version}` : ''}`
      : source.kind === 'extension'
        ? `Extension${name && name !== 'Claude Desktop extension' ? ` · ${name}${version}` : ''}`
        : source.kind === 'custom'
          ? 'Custom source'
          : 'Configuration';
  return `${serverIsReadOnly(server) ? 'Read-only · ' : ''}${kind}`;
}

/// Health, name, what it is, and what it runs — as one button that opens the
/// server (§7.5). On the matrix that is the persistent inspector (§7.1.2); on the
/// client screen, which has no inspector, it is the detail sheet. The label says
/// "selected" through `aria-pressed` only where there is something to press.
///
/// @param {boolean} settled a check just finished, and the dot says so once.
export function serverOpener(state, server, settled) {
  const opener = el('button', 'server-opener');
  opener.dataset.action = 'open-server';
  opener.dataset.serverId = server.id;
  if (state.panel === null) opener.setAttribute('aria-pressed', String(state.selectedServerId === server.id));

  const busy = state.checking.has(server.id);
  const status = server.health?.status;
  const settledNow = settled && !busy ? ' settled' : '';
  const dot = el('span', `status-dot ${busy ? 'checking' : DOT_STATE[status] ?? 'unchecked'}${settledNow}`);
  dot.title = busy ? 'Checking…' : (DOT_LABEL[status] ?? 'Not checked yet');

  const title = el('div', 'server-title');
  title.append(dot);
  const name = el('span', 'server-name', server.name);
  // Badges share the line, and a narrow window ellipsizes the name first.
  name.title = server.name;
  title.append(name);
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
    const badge = el('span', 'badge contract-changed', 'changed');
    badge.title =
      'The tools this server offers, or what it says about them, changed since the last check. Open it to review.';
    title.append(badge);
  }
  // Titled because a narrow row can cut a badge down to a stub. What an
  // extension or plugin is, and which one, is the source line under the title.
  if (server.transport !== 'stdio' && !server.isBundled) {
    const badge = el('span', 'badge', server.transport);
    badge.title = `Remote server over ${server.transport}`;
    title.append(badge);
  }
  if (server.health?.toolCount != null) {
    const count = server.health.toolCount;
    title.append(el('span', 'tool-count', `${count} ${count === 1 ? 'tool' : 'tools'}`));
  }
  opener.append(title);

  // Status in words beside where the row came from. Wrapping rather than
  // truncating the pair: a long plugin name must not push the verdict off the row.
  const meta = el('div', 'server-meta');
  const label = el(
    'span',
    `server-health-label ${busy ? 'checking' : DOT_STATE[status] ?? 'unchecked'}`,
    busy ? 'Checking…' : (STATUS_TEXT[status] ?? 'Not checked'),
  );
  const origin = el('span', 'server-source-label', sourceLabel(server));
  origin.title = origin.textContent;
  meta.append(label, origin);
  opener.append(meta);

  // A failed check replaces the command with the reason it failed — that is what
  // the user needs at a glance, and the command is one click away in the detail.
  if (status === 'needsAuthorization') {
    opener.append(el('div', 'server-command authwait', 'Waiting for you to authorize it in the browser'));
  } else if (status === 'failed' && server.health.message) {
    opener.append(el('div', 'server-command failure', firstLine(server.health.message)));
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
