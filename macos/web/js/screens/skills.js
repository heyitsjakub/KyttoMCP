// Read-only skill inventory. Skill bodies and values never cross IPC; only
// front-matter summaries, scope and duplicate/conflict facts are rendered.

import { el, button } from '../dom.js';

export function renderSkills(state) {
  const panel = el('div', 'skills-panel');
  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'Skills inventory'));
  copy.append(el('p', 'muted', 'Read-only view of known global and workspace skill folders. Kytto does not edit or synchronize them.'));
  head.append(copy, el('span', 'spacer'));
  head.append(button(state.skillsBusy ? 'Scanning…' : 'Choose workspace', state.skillsBusy ? 'noop' : 'choose-skills-directory'));
  panel.append(head);

  const roots = el('div', 'skills-roots');
  roots.append(el('strong', null, 'Scanned roots'));
  roots.append(el('span', 'muted', (state.skillsRoots ?? []).join(' · ') || 'Known global roots; choose a workspace to include project scopes.'));
  panel.append(roots);

  for (const warning of state.skillsWarnings ?? []) panel.append(el('p', 'skills-warning', warning));

  if (state.skillsEntries.length === 0 && !state.skillsBusy) {
    panel.append(el('div', 'placeholder', 'No skills were found in the known roots.'));
    return panel;
  }

  const heading = el('div', 'library-section-heading');
  heading.append(el('h3', null, `${state.skillsEntries.length} skill${state.skillsEntries.length === 1 ? '' : 's'}`));
  heading.append(el('span', 'muted', 'Duplicate names are grouped; conflicts have different content.'));
  panel.append(heading);

  const list = el('div', 'skills-list');
  for (const entry of state.skillsEntries) list.append(skillRow(entry));
  panel.append(list);
  return panel;
}

function skillRow(entry) {
  const row = el('article', `skill-row${entry.hasConflict ? ' conflict' : ''}`);
  const head = el('div', 'skill-row-head');
  const identity = el('div', 'skill-identity');
  identity.append(el('h3', null, entry.name));
  // The label already composes the agent and the scope ("Codex · global");
  // prefixing them again printed every card's identity twice.
  identity.append(el('span', 'muted', entry.scopeLabel));
  head.append(identity);
  if (entry.isDuplicate) head.append(el('span', `badge${entry.hasConflict ? ' contract-changed' : ''}`, entry.hasConflict ? 'conflict' : 'duplicate'));
  if (entry.metadataStatus !== 'complete') head.append(el('span', 'badge', metadataLabel(entry.metadataStatus)));
  row.append(head);
  if (entry.description) row.append(el('p', null, entry.description));
  row.append(el('p', 'muted mono', entry.pathDisplay));
  return row;
}

function metadataLabel(status) {
  switch (status) {
    case 'missingName': return 'name missing';
    case 'missingDescription': return 'description missing';
    case 'missingNameAndDescription': return 'metadata missing';
    case 'unreadable': return 'unreadable';
    default: return status;
  }
}
