// The safe, native-backed import and inventory tools: agent import, the MCP
// library and the skills inventory.
//
// These are intentionally sheets rather than a second navigation system: each
// flow is a read/preview/write sequence and the state object keeps the unfinished
// step alive while the native side does the bounded work.

import { el, button, field, input, sheet, definitionList } from '../dom.js';

export function renderAgentImport(state) {
  const current = state.sheet;
  const body = [];

  body.push(
    el(
      'p',
      'muted',
      'Paste the response from an agent below. Kytto accepts strict JSON only and imports server definitions into built-in clients after you review the preview.',
    ),
  );

  if (current.prompt) {
    const prompt = el('details', 'tool-prompt');
    prompt.open = true;
    prompt.append(el('summary', null, 'Copyable prompt for an agent'));
    prompt.append(el('pre', 'mono selectable', current.prompt));
    body.push(prompt);
  }

  const textarea = el('textarea', 'text-input import-json');
  textarea.rows = 13;
  textarea.spellcheck = false;
  textarea.placeholder = '{ "schemaVersion": 1, "servers": [...] }';
  textarea.value = current.json ?? '';
  textarea.dataset.action = 'agent-import-json';
  body.push(field('Agent JSON', textarea, 'No comments, Markdown fences, trailing commas, unknown fields or environment values.'));

  body.push(targetPicker(state, current.clientIDs ?? [], 'agent-import-client'));

  if (current.preview) body.push(agentImportPreview(state, current.preview));
  if (current.error) body.push(el('p', 'failure-message', current.error));

  const previewReady = Boolean(current.preview);
  return sheet('Import from agent', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    current.prompt
      ? button('Refresh prompt', current.busy ? 'noop' : 'agent-import-prompt', {
          className: 'button subtle',
        })
      : button('Get prompt', current.busy ? 'noop' : 'agent-import-prompt', {
          className: 'button subtle',
        }),
    button(current.busy ? 'Previewing…' : 'Preview import', current.busy ? 'noop' : 'agent-import-preview', {
      className: current.busy ? 'button busy' : 'button',
    }),
    button(
      current.busy ? 'Importing…' : 'Import',
      current.busy || !previewReady ? 'noop' : 'agent-import-submit',
      { className: current.busy || !previewReady ? 'button primary busy' : 'button primary' },
    ),
  ]);
}

function agentImportPreview(state, preview) {
  const section = el('section', 'tool-preview');
  section.append(el('h3', null, 'Preview'));
  section.append(el('p', 'field-hint', `${preview.servers.length} server${preview.servers.length === 1 ? '' : 's'} · targets: ${targetNames(state, preview.targetClientIDs)}`));

  const list = el('div', 'tool-card-list');
  for (const server of preview.servers) {
    const card = el('article', 'tool-card');
    card.append(el('div', 'tool-card-head', server.name));
    card.append(el('span', 'badge', server.transport));
    card.append(el('code', 'mono selectable tool-command', server.commandSummary || server.url || '')); 
    if (server.environmentKeys?.length) {
      card.append(el('p', 'field-hint', `Environment keys only: ${server.environmentKeys.join(', ')}`));
    }
    list.append(card);
  }
  section.append(list);
  appendMessages(section, preview.conflicts, 'warning', 'Conflicts');
  appendMessages(section, preview.warnings, 'muted', 'Warnings');
  return section;
}

export function renderLibrary(state) {
  const current = state.sheet;
  const body = [];
  body.push(
    el(
      'p',
      'muted',
      'Choose a folder to inspect for MCP configuration files and package or Python entry points. Scanning is bounded, read-only and never executes a candidate.',
    ),
  );

  if (!current.sessionID) {
    body.push(el('p', 'field-hint', 'The selected folder stays native; only its display name and opaque candidate ids reach this screen.'));
    if (current.error) body.push(el('p', 'failure-message', current.error));
    return sheet('MCP library', body, [
      button('Cancel', 'close-sheet', { className: 'button subtle' }),
      el('span', 'spacer'),
      button(current.busy ? 'Choosing…' : 'Choose folder…', current.busy ? 'noop' : 'library-choose-directory', {
        className: current.busy ? 'button primary busy' : 'button primary',
      }),
    ]);
  }

  body.push(definitionList([['Selected folder', current.displayName]]));
  if (current.candidates === null) {
    body.push(el('p', 'field-hint', 'Scan the selected folder to find supported definitions.'));
  } else if (current.candidates.length === 0) {
    body.push(el('p', 'muted', 'No supported MCP definitions or entry suggestions were found.'));
  } else {
    body.push(candidatePicker(current));
    if (current.selectedCandidateID) {
      body.push(targetPicker(state, current.clientIDs ?? [], 'library-client'));
    }
    if (current.preview) body.push(libraryPreview(state, current.preview));
  }
  if (current.warnings?.length) appendMessages(body, current.warnings, 'muted', 'Scan warnings');
  if (current.error) body.push(el('p', 'failure-message', current.error));

  const hasCandidate = Boolean(current.selectedCandidateID);
  const hasTargets = (current.clientIDs ?? []).length > 0;
  return sheet('MCP library', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button('Choose another…', current.busy ? 'noop' : 'library-choose-directory', { className: 'button subtle' }),
    button(current.busy ? 'Scanning…' : 'Scan again', current.busy ? 'noop' : 'library-scan', {
      className: current.busy ? 'button busy' : 'button',
    }),
    button('Preview import', current.busy || !hasCandidate || !hasTargets ? 'noop' : 'library-preview', {
      className: 'button',
    }),
    button('Import', current.busy || !current.preview ? 'noop' : 'library-submit', {
      className: current.busy || !current.preview ? 'button primary busy' : 'button primary',
    }),
  ]);
}

function candidatePicker(current) {
  const section = el('section', 'tool-preview');
  section.append(el('h3', null, 'Candidates'));
  const list = el('div', 'library-candidates');
  for (const candidate of current.candidates) {
    const item = el('button', `library-candidate${candidate.id === current.selectedCandidateID ? ' selected' : ''}`);
    item.dataset.action = 'library-select-candidate';
    item.dataset.candidateId = candidate.id;
    const head = el('div', 'tool-card-head');
    head.append(el('strong', null, candidate.name), el('span', 'badge', candidate.transport));
    item.append(head);
    item.append(el('span', 'mono muted', candidate.relativeLocation));
    item.append(el('span', 'mono selectable tool-command', candidate.commandSummary));
    const clients = candidate.detectedClientIDs?.length
      ? `Detected in ${candidate.detectedClientIDs.join(', ')}`
      : 'Not detected in a built-in client';
    item.append(el('span', 'field-hint', clients));
    if (candidate.warnings?.length) item.append(el('span', 'field-hint', candidate.warnings.join(' ')));
    list.append(item);
  }
  section.append(list);
  return section;
}

function libraryPreview(state, preview) {
  const section = el('section', 'tool-preview library-preview');
  section.append(el('h3', null, 'Import preview'));
  section.append(el('p', 'field-hint', `${preview.candidate.name} → ${targetNames(state, preview.targetClientIDs)}`));
  section.append(definitionList([
    ['Location', preview.candidate.relativeLocation, 'mono selectable'],
    ['Transport', preview.candidate.transport],
    ['Command', preview.candidate.commandSummary, 'mono selectable'],
  ]));
  if (preview.candidate.environmentKeys?.length) {
    section.append(el('p', 'field-hint', `Environment keys only: ${preview.candidate.environmentKeys.join(', ')}`));
  }
  appendMessages(section, preview.conflicts, 'warning', 'Conflicts');
  appendMessages(section, preview.warnings, 'muted', 'Warnings');
  return section;
}

export function renderSkills(state) {
  const current = state.sheet;
  const body = [
    el('p', 'muted', 'Read-only inventory of skills from known global roots and an optional workspace. Kytto never executes, edits or sends skill content.'),
  ];
  if (current.displayName) {
    body.push(el('p', 'field-hint', `Workspace: ${current.displayName}`));
  } else {
    body.push(el('p', 'field-hint', 'Global roots are scanned by default. Choose a workspace to include workspace-scoped skills.'));
  }
  if (current.skills === null) {
    body.push(el('p', 'muted', 'Inventory has not been scanned yet.'));
  } else if (current.skills.length === 0) {
    body.push(el('p', 'muted', 'No SKILL.md files were found in the known roots.'));
  } else {
    body.push(skillList(current.skills));
  }
  if (current.warnings?.length) appendMessages(body, current.warnings, 'muted', 'Inventory warnings');
  if (current.error) body.push(el('p', 'failure-message', current.error));

  return sheet('Skills inventory', body, [
    button('Close', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(current.busy ? 'Choosing…' : 'Choose workspace…', current.busy ? 'noop' : 'skills-choose-directory', { className: 'button subtle' }),
    button(current.busy ? 'Scanning…' : 'Scan again', current.busy ? 'noop' : 'skills-scan', {
      className: current.busy ? 'button primary busy' : 'button primary',
    }),
  ]);
}

function skillList(skills) {
  const list = el('div', 'skill-list');
  for (const skill of skills) {
    const card = el('article', `skill-card${skill.isConflict ? ' conflict' : skill.isDuplicate ? ' duplicate' : ''}`);
    const head = el('div', 'tool-card-head');
    head.append(el('strong', null, skill.name));
    head.append(el('span', `badge skill-status ${skill.status}`, skill.status));
    card.append(head);
    card.append(el('p', null, skill.description));
    card.append(definitionList([
      ['Scope', skill.scopeLabel || 'unknown'],
      ['Location', skill.locationDisplay, 'mono selectable'],
      ['Metadata', skill.metadataStatus || 'unknown'],
    ]));
    if (skill.warnings?.length) appendMessages(card, skill.warnings, 'muted', 'Notes');
    list.append(card);
  }
  return list;
}

function targetPicker(state, selected, action) {
  const section = el('section', 'tool-targets');
  section.append(el('h3', null, 'Built-in targets'));
  const clients = state.clients.filter((client) => !client.isReadOnly && client.state !== 'notInstalled');
  if (clients.length === 0) {
    section.append(el('p', 'muted', 'No built-in client is available as an import target.'));
    return section;
  }
  for (const client of clients) {
    const label = el('label', 'checkbox');
    const checkbox = el('input');
    checkbox.type = 'checkbox';
    checkbox.checked = selected.includes(client.id);
    checkbox.dataset.action = action;
    checkbox.dataset.clientId = client.id;
    label.append(checkbox, el('span', null, client.displayName));
    if (client.state === 'noConfig') label.append(el('span', 'field-hint', 'config will be created'));
    section.append(label);
  }
  return section;
}

function targetNames(state, ids) {
  return ids?.map((id) => state.clients.find((client) => client.id === id)?.displayName ?? id).join(', ') || 'none';
}

function appendMessages(parent, messages, className, title) {
  if (!messages?.length) return;
  const section = el('section', `tool-messages ${className}`);
  section.append(el('h4', null, title));
  const list = el('ul', 'plain-list');
  for (const message of messages) list.append(el('li', null, message));
  section.append(list);
  parent.append(section);
}
