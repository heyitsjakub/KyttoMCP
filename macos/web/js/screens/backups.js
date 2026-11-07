// Backups, visible (§6.5).
//
// A hidden folder full of .bak files is a safety net nobody knows about. Revert
// being one click away in the app is what makes a tool trustworthy enough to
// point at someone's configuration in the first place.

import { el } from '../dom.js';

const formatBytes = (count) =>
  count < 1024 ? `${count} B` : `${(count / 1024).toFixed(1)} kB`;

export function renderBackups(state) {
  const panel = el('div', 'table-scroll');
  panel.dataset.scrollKey = 'backups';

  if (state.backups.length === 0) {
    const empty = el('div', 'placeholder');
    empty.append(el('p', null, 'No backups yet.'));
    empty.append(
      el('p', 'muted', 'Kytto copies a config aside before every change it makes.'),
    );
    panel.append(empty);
    return panel;
  }

  const table = el('table', 'matrix backups-table');

  const head = el('thead');
  const headRow = el('tr');
  for (const [label, className] of [
    ['Taken', 'col-when'],
    ['File', 'col-server'],
    ['Size', 'col-weight'],
    ['', 'col-action'],
  ]) {
    headRow.append(el('th', className, label));
  }
  head.append(headRow);

  const body = el('tbody');
  for (const backup of state.backups) {
    const tr = el('tr');

    tr.append(el('td', 'col-when', new Date(backup.takenAt * 1000).toLocaleString()));

    const fileCell = el('td', 'col-server');
    const path = el('div', 'server-command', backup.pathDisplay);
    path.title = backup.pathDisplay;
    fileCell.append(path);
    fileCell.append(
      el(
        'div',
        'muted',
        state.clients.find((client) => client.id === backup.clientID)?.displayName ??
          backup.clientID,
      ),
    );
    tr.append(fileCell);

    tr.append(el('td', 'col-weight', formatBytes(backup.byteCount)));

    const actionCell = el('td', 'col-action');
    // While any revert is in flight every Revert waits: a second click would
    // write the same file again, or race a different backup onto it.
    const reverting = state.restoringBackupId === backup.id;
    const restore = el('button', reverting ? 'button busy' : 'button', reverting ? 'Reverting…' : 'Revert');
    restore.dataset.action = state.restoringBackupId ? 'noop' : 'restore-backup';
    restore.dataset.backupId = backup.id;
    restore.dataset.clientId = backup.clientID;
    restore.disabled = Boolean(state.restoringBackupId);
    // Reverting is itself backed up first, so this is not a one-way door.
    restore.title = 'Put this version back. The current file is backed up first.';
    actionCell.append(restore);
    tr.append(actionCell);

    body.append(tr);
  }

  table.append(head, body);
  panel.append(table);
  return panel;
}
