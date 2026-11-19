import { el, button, busyButton, definitionList, field, sheet } from '../dom.js';

const clientName = (state, id) => state.clients.find((client) => client.id === id)?.displayName ?? id;

export function projectSources(state, serverId) {
  if (!state.app?.capabilities?.includes('project-copy')) return [];
  return (state.definitions?.[serverId]?.copies ?? []).filter((copy) => copy.repository);
}

export function copyTargets(state) {
  return state.clients.filter((client) => !client.isReadOnly && client.state !== 'notInstalled');
}

export function definitionSources(state, server) {
  const inventory = state.definitions?.[server.id];
  if (!inventory?.copies?.length) return null;
  const part = el('section', 'detail-section definition-sources');
  part.append(el('h3', null, 'Definition sources'));
  for (const copy of inventory.copies) {
    const item = el('div', 'definition-copy');
    item.append(el('strong', null, clientName(state, copy.clientID)),
      el('span', 'muted', [copy.scope, copy.transport, copy.name].join(' · ')),
      el('code', 'mono selectable', copy.file));
    part.append(item);
  }
  const covered = new Set();
  for (const priority of inventory.priorities) {
    const winner = inventory.copies.find((copy) => copy.clientID === priority.winner);
    if (!winner) continue;
    priority.candidates.forEach((id) => covered.add(id));
    part.append(el('p', 'field-hint',
      `In ${priority.workspace}, Claude Code gives ${winner.scope} priority among these enabled definitions. Managed settings and session options can override this order.`));
  }
  if (inventory.overlapsScopes && inventory.copies.some((copy) => !covered.has(copy.clientID))) {
    part.append(el('p', 'field-hint', covered.size ? 'Priority is unknown for the remaining definitions.' : 'Priority is unknown for these definitions.'));
  }
  if (projectSources(state, server.id).length) {
    const action = button('Import project definition…', 'open-project-copy', { dataset: { serverId: server.id } });
    action.disabled = !copyTargets(state).length;
    part.append(action);
    if (action.disabled) part.append(el('p', 'field-hint', 'A writable destination client is required.'));
  }
  return part;
}

export function launchReview(state, alert) {
  const part = el('section', 'detail-section definition-sources');
  part.append(el('h3', null, state.servers.some((server) => server.id === alert.serverID) ? 'External launch change' : `External launch change · ${alert.serverID}`),
    el('p', null, `${clientName(state, alert.clientID)} · ${alert.scope}`),
    el('code', 'mono selectable', alert.file),
    el('p', 'field-hint', `Observed ${new Date(alert.observedAt).toLocaleString()}`));
  const table = el('table', 'launch-review-fields');
  const head = el('tr');
  for (const text of ['Changed field', 'Before', 'After']) head.append(el('th', null, text));
  const thead = el('thead');
  thead.append(head);
  const tbody = el('tbody');
  for (const field of alert.fields) {
    const row = el('tr');
    row.append(el('th', null, field.name), el('td', null, field.before), el('td', null, field.after));
    tbody.append(row);
  }
  table.append(thead, tbody);
  part.append(table, button('Mark as reviewed', 'acknowledge-launch', { dataset: { token: alert.token } }));
  return part;
}

function picker(label, action, entries, selected, busy) {
  const select = el('select', 'text-input');
  select.dataset.action = action;
  select.disabled = busy;
  for (const [id, text] of entries) {
    const option = el('option', null, text);
    option.value = id;
    option.selected = id === selected;
    select.append(option);
  }
  return field(label, select);
}

export function renderProjectCopy(state) {
  const { serverId, sourceId, targetId, preview, busy, error } = state.sheet;
  const server = state.servers.find((entry) => entry.id === serverId);
  if (!server) return null;
  const sources = projectSources(state, serverId);
  const targets = copyTargets(state);
  const body = [
    picker('Project definition', 'project-copy-source', sources.map((copy) =>
      [copy.clientID, `${clientName(state, copy.clientID)} · ${copy.file}`]), sourceId, busy),
    picker('Destination client', 'project-copy-target', targets.map((client) =>
      [client.id, `${client.displayName} · ${client.configPathDisplay}`]), targetId, busy),
  ];
  if (preview) {
    body.push(definitionList([
      ['Read from', preview.sourceFile, 'mono selectable'],
      ['Write to', preview.targetFile, 'mono selectable'],
      ['Transport', preview.transport],
      ['Environment keys', preview.environmentKeys.join(', ') || 'None'],
      ['Unresolved variables', preview.unresolvedVariables.join(', ') || 'None'],
    ]));
    for (const warning of preview.warnings) body.push(el('p', 'field-hint', warning));
    for (const blocker of preview.blockers) body.push(el('p', 'failure-message', blocker));
    if (preview.token) body.push(el('p', 'field-hint', 'Import adds one definition. Existing environment values are copied locally. The destination client may start the server when it reloads.'));
  }
  if (error) body.push(el('p', 'failure-message', error));
  const action = preview?.token
    ? busyButton(busy, 'Import definition', 'Importing…', 'apply-project-copy', { className: 'button primary' })
    : busyButton(busy, 'Preview copy', 'Reading…', 'preview-project-copy', { className: 'button primary' });
  if (!sources.some((copy) => copy.clientID === sourceId) || !targets.some((client) => client.id === targetId)) action.disabled = true;
  return sheet(`Import ${server.name}`, body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }), el('span', 'spacer'), action,
  ]);
}
