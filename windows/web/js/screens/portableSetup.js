// Portable setup (§7.13): the Profiles header actions, the import preview sheet, and
// the notes about imported contract fingerprints.
//
// Everything typed — secrets, URLs, private paths — and every file path lives in
// native dialogs. This module only ever sees names, counts, display strings and the
// opaque id of a plan that native code is holding.

import { el, button, busyButton, sheet, definitionList } from '../dom.js';

export const hasPortableSetup = (state) => state.app?.capabilities?.includes('portable-setup') === true;

/**
 * Export and Import, as plain buttons to sit directly in the Profiles heading's one
 * action group. Without the capability there are none, so nothing is left behind
 * to take up room.
 */
export function setupActionButtons(state) {
  if (!hasPortableSetup(state)) return [];
  return [
    busyButton(state.setupBusy, 'Export setup…', 'Please wait…', 'export-setup', { className: 'button subtle' }),
    busyButton(state.setupBusy, 'Import setup…', 'Please wait…', 'preview-setup-import', { className: 'button subtle' }),
  ];
}

export function renderPortableImport(state) {
  const current = state.sheet;
  const preview = current.preview;
  if (!preview) return null;
  const names = (preview.targetClients ?? []).map(
    (id) => state.clients.find((client) => client.id === id)?.displayName ?? id,
  );
  const body = [
    el('p', null, 'Review the destination and imported items before changing your client configurations.'),
    definitionList([
      ['Servers', preview.serverNames?.join(', ') || 'None'],
      ['Profiles', preview.profileNames?.join(', ') || 'None'],
      ['Destination clients', names.join(', ') || 'None'],
      ['Initial state', 'Disabled — enable each server after reviewing it'],
    ]),
  ];
  if (preview.pathDisplays?.length) {
    const destinations = el('section', 'detail-section');
    destinations.append(el('h3', null, 'Files to update'));
    const list = el('ul', 'selectable mono');
    preview.pathDisplays.forEach((path) => list.append(el('li', null, path)));
    destinations.append(list);
    body.push(destinations);
  }
  if (preview.pendingContractCount) {
    body.push(el(
      'p',
      'field-hint',
      `${preview.pendingContractCount} imported contract${preview.pendingContractCount === 1 ? '' : 's'} will require a fresh health check and local review.`,
    ));
  }
  for (const warning of preview.warnings ?? []) body.push(el('p', 'field-hint warning', warning));
  body.push(el('p', 'muted', 'Kytto backs up existing configurations. Imported definitions are disabled and no server runs during import.'));
  if (current.error) body.push(el('p', 'failure-message', current.error));
  return sheet('Import setup', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    busyButton(current.busy, 'Import disabled', 'Importing…', 'apply-setup-import', { className: 'button primary' }),
  ]);
}

export const PENDING_REVIEW_HINT =
  'Complete a fresh passing health check before reviewing this imported contract.';

/**
 * What the Contract Guard section says about an imported fingerprint, and whether
 * the review button must stay disabled.
 *
 * An imported fingerprint is evidence to compare a local check against. It becomes
 * reviewable only once a check made here, after the import, has passed — and a store
 * that could not be read blocks the review outright.
 */
export function pendingContractEvidence(state, server) {
  const imports = state.pendingContractImports ?? {};
  const entry = (imports.entries ?? []).find((item) => item.serverID === server.id) ?? null;
  const error = imports.error ?? null;
  const notes = [];
  if (error) notes.push(el('p', 'failure-message', error));
  if (entry) {
    const status = entry.matching === true
      ? 'The fresh check matches the imported fingerprints.'
      : entry.matching === false
        ? 'The fresh check differs from the imported fingerprints.'
        : 'Run a fresh health check to compare this server with the imported fingerprints.';
    const count = entry.toolCount === 1 ? '1 imported tool fingerprint' : `${entry.toolCount} imported tool fingerprints`;
    notes.push(
      el('p', 'muted', `${count} ${entry.toolCount === 1 ? 'awaits' : 'await'} local review. ${status}`),
      el('p', 'field-hint', 'Imported contracts are reference material. Mark the current, locally checked contract as reviewed only after inspecting its tools.'),
    );
  }
  const blocked = Boolean(error) || (entry !== null && (entry.matching == null || server.health?.status !== 'passed'));
  return { entry, error, notes, blocked };
}
