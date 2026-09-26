// Local MCP library and strict agent import.
//
// The native side owns scanning, parsing and writing. This screen only renders
// summaries, key names and explicit previews; it never receives environment
// values and never executes a discovered entry point.

import { el, button } from '../dom.js';

export function renderLibrary(state) {
  const panel = el('div', 'library-panel');
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'MCP Library & agent import'));
  copy.append(el('p', 'muted', 'Find local MCP definitions, or paste a strict JSON description from an agent. Nothing is executed during discovery.'));
  head.append(copy, el('span', 'spacer'));
  head.append(button(state.libraryBusy ? 'Scanning…' : 'Choose directory', state.libraryBusy ? 'noop' : 'choose-library-directory'));
  panel.append(head);

  const location = el('div', 'library-location');
  location.append(
    el('strong', null, 'Local folder'),
    el('span', 'mono', state.libraryPath || 'No folder selected yet.'),
  );
  panel.append(location);

  for (const warning of state.libraryWarnings ?? []) panel.append(message('warning', warning));

  if (state.libraryPath && state.libraryCandidates.length === 0 && !state.libraryBusy) {
    panel.append(el('div', 'profile-empty', 'No tested MCP definition or likely entry point was found.'));
  }
  if (state.libraryCandidates.length > 0) {
    const heading = el('div', 'library-section-heading');
    heading.append(el('h3', null, `${state.libraryCandidates.length} local candidate${state.libraryCandidates.length === 1 ? '' : 's'}`));
    heading.append(el('span', 'muted', 'Review each preview before adding it.'));
    panel.append(heading);
    const list = el('div', 'library-list');
    for (const candidate of state.libraryCandidates) list.append(candidateCard(state, candidate));
    panel.append(list);
  }

  panel.append(agentImport(state));
  return panel;
}

function candidateCard(state, candidate) {
  const card = el('article', 'library-card');
  const heading = el('div', 'library-card-head');
  const identity = el('div', 'library-identity');
  identity.append(el('h3', null, candidate.name));
  identity.append(el('p', 'muted mono', candidate.location));
  heading.append(identity, el('span', 'library-transport', candidate.transport));
  card.append(heading);

  card.append(el('div', 'library-command mono', candidate.commandSummary || 'No command summary'));
  card.append(definitionMeta(candidate));
  for (const warning of candidate.warnings ?? []) card.append(message('warning', warning));

  const targets = targetPicker(
    state,
    state.libraryTargets?.[candidate.id] ?? defaultTargets(state),
    'library-target',
    candidate.id,
  );
  card.append(targets);

  const actions = el('div', 'library-actions');
  const preview = button(
    state.libraryBusy && state.libraryPreviewCandidateID === candidate.id ? 'Previewing…' : 'Preview add',
    state.libraryBusy ? 'noop' : 'preview-library',
  );
  preview.dataset.candidateId = candidate.id;
  preview.disabled = state.libraryBusy;
  actions.append(preview);
  card.append(actions);

  if (state.libraryPreviewCandidateID === candidate.id && state.libraryPreview) {
    card.append(previewBlock(state, state.libraryPreview, 'Import this candidate', 'import-library', candidate.id));
  }
  return card;
}

function definitionMeta(candidate) {
  const meta = el('div', 'library-meta');
  meta.append(metaItem('Environment keys', candidate.environmentKeys?.length ? candidate.environmentKeys.join(', ') : 'None'));
  meta.append(metaItem('Found in', candidate.detectedClientIDs?.length ? candidate.detectedClientIDs.join(', ') : 'No known client map'));
  if (candidate.missingClientIDs?.length) meta.append(metaItem('Not found in', candidate.missingClientIDs.join(', ')));
  return meta;
}

function metaItem(label, value) {
  const item = el('span', 'library-meta-item');
  item.append(el('strong', null, `${label}:`), el('span', null, value));
  return item;
}

function agentImport(state) {
  const section = el('section', 'agent-import');
  const heading = el('div', 'library-section-heading');
  const copy = el('div');
  copy.append(el('h3', null, 'Copy-paste agent import'));
  copy.append(el('p', 'muted', 'Ask an agent for the exact contract below. Secret values are never accepted or written from this import.'));
  heading.append(copy, el('span', 'spacer'));
  heading.append(button('Copy prompt', 'copy-import-prompt', { className: 'button subtle' }));
  section.append(heading);

  const prompt = el('pre', 'import-prompt');
  prompt.textContent = state.importPrompt || 'Load the strict import prompt with Copy prompt.';
  section.append(prompt);

  const inputLabel = el('label', 'library-field');
  inputLabel.append(el('span', 'field-label', 'Agent JSON'));
  const input = el('textarea', 'text-input import-json');
  input.rows = 10;
  input.placeholder = '{"schemaVersion":1,"servers":[...]}';
  input.dataset.action = 'import-json';
  input.value = state.importText ?? '';
  inputLabel.append(input);
  section.append(inputLabel);

  section.append(targetPicker(state, state.importTargetIDs?.length ? state.importTargetIDs : defaultTargets(state), 'import-target', 'import'));
  const actions = el('div', 'library-actions');
  const preview = button(state.importBusy ? 'Previewing…' : 'Preview JSON', state.importBusy ? 'noop' : 'preview-import');
  preview.disabled = state.importBusy || !(state.importText ?? '').trim();
  actions.append(preview);
  section.append(actions);

  if (state.importError) section.append(message('error', state.importError));
  if (state.importPreview) section.append(previewBlock(state, state.importPreview, 'Import these servers', 'apply-import', 'import'));
  return section;
}

function targetPicker(state, selected, action, subject) {
  const wrapper = el('div', 'library-targets');
  wrapper.append(el('h4', null, 'Add to'));
  const clients = writableClients(state);
  if (clients.length === 0) {
    wrapper.append(el('p', 'muted', 'No writable built-in client is available.'));
    return wrapper;
  }
  for (const client of clients) {
    const label = el('label', 'library-target');
    const checkbox = el('input');
    checkbox.type = 'checkbox';
    checkbox.checked = selected.includes(client.id);
    checkbox.dataset.action = action;
    checkbox.dataset.subject = subject;
    checkbox.dataset.clientId = client.id;
    label.append(checkbox, clientLabel(client));
    wrapper.append(label);
  }
  return wrapper;
}

function clientLabel(client) {
  const copy = el('span');
  copy.append(el('strong', null, client.displayName));
  copy.append(el('span', 'muted', client.state === 'noConfig' ? ' · will create its config' : ''));
  return copy;
}

function writableClients(state) {
  return state.clients.filter((client) => !client.isReadOnly && client.state !== 'notInstalled');
}

function defaultTargets(state) {
  const clients = writableClients(state);
  const preferred = clients.find((client) => client.state === 'ready') ?? clients[0];
  return preferred ? [preferred.id] : [];
}

function previewBlock(state, preview, label, action, subject) {
  const box = el('div', 'import-preview');
  const head = el('div', 'library-section-heading');
  head.append(el('h4', null, `${preview.source} · preview`));
  head.append(el('span', 'muted', preview.canImport ? 'Ready to import' : 'Resolve conflicts first'));
  box.append(head);

  for (const warning of preview.warnings ?? []) box.append(message('warning', warning));
  for (const server of preview.servers ?? []) {
    const row = el('div', 'import-server');
    const title = el('strong', null, server.name);
    row.append(title, el('span', 'muted', `${server.transport} · ${server.commandSummary}`));
    if (server.environmentKeys?.length) row.append(el('span', 'muted', `Keys only: ${server.environmentKeys.join(', ')}`));
    if (server.conflictingClientIDs?.length) {
      const names = server.conflictingClientIDs.map((id) => state.clients.find((client) => client.id === id)?.displayName ?? id);
      row.append(message('error', `Already exists in ${names.join(', ')}.`));
    }
    for (const warning of server.warnings ?? []) row.append(message('warning', warning));
    box.append(row);
  }

  const importButton = button(preview.canImport ? label : 'Resolve conflicts to continue', action, { className: 'button primary' });
  importButton.disabled = !preview.canImport || state.libraryBusy || state.importBusy;
  if (subject === 'import') importButton.dataset.importSubject = subject;
  else if (action === 'import-library') importButton.dataset.candidateId = subject;
  box.append(importButton);
  return box;
}

function message(kind, text) {
  return el('p', `library-message ${kind}`, text);
}
