// Named MCP stacks. A profile is intent stored by Kytto; applying one still
// writes through the same per-client toggles as the matrix.

import { el, button, field, input, sheet, formatTokens, clientIcon } from '../dom.js';
import { contextWindow } from '../state.js';

export function renderProfiles(state) {
  const panel = el('div', 'profiles-panel');

  const head = el('header', 'panel-head');
  const copy = el('div');
  copy.append(el('h2', null, 'Profiles'));
  copy.append(
    el(
      'p',
      'muted',
      'Switch one client to a focused set of MCP servers without editing each configuration by hand.',
    ),
  );
  head.append(copy, el('span', 'spacer'), button('New profile', 'new-profile', { className: 'button primary' }));
  panel.append(head);

  if (state.profiles.length === 0) {
    const empty = el('div', 'profile-empty');
    empty.append(el('p', null, 'No profiles yet.'));
    empty.append(
      el(
        'p',
        'muted',
        'Create Coding, Research or Minimal, choose its servers, then apply it to any configured client.',
      ),
    );
    empty.append(button('Create a profile', 'new-profile'));
    panel.append(empty);
    return panel;
  }

  const list = el('div', 'profile-list');
  for (const profile of state.profiles) list.append(profileRow(state, profile));
  panel.append(list);
  return panel;
}

function profileRow(state, profile) {
  const row = el('article', 'profile-row');
  const identity = el('div', 'profile-identity');
  identity.append(el('h3', null, profile.name));

  const known = profile.serverIDs
    .map((id) => state.servers.find((server) => server.id === id))
    .filter(Boolean);
  const missing = profile.serverIDs.length - known.length;
  const measured = known.filter((server) => server.tokenWeight);
  const tokens = measured.reduce((sum, server) => sum + server.tokenWeight.estimate, 0);

  const summary = [
    `${profile.serverIDs.length} ${profile.serverIDs.length === 1 ? 'server' : 'servers'}`,
  ];
  if (measured.length > 0) {
    const incomplete = measured.length < known.length;
    const approximate = measured.some((server) => !server.tokenWeight.isMeasured);
    summary.push(`${incomplete ? '≥' : approximate ? '~' : ''}${formatTokens(tokens)} ${incomplete ? 'known tokens' : 'tokens'}`);
    summary.push(`${((tokens / contextWindow()) * 100).toFixed(tokens < 20_000 ? 1 : 0)}% of context`);
  } else if (known.length > 0) {
    summary.push('context not measured');
  }
  if (missing > 0) summary.push(`${missing} missing`);
  if (profile.tokenBudget) summary.push(`${formatTokens(profile.tokenBudget)} budget`);
  identity.append(el('p', missing > 0 ? 'profile-summary warning' : 'profile-summary', summary.join(' · ')));

  if (known.length > 0) {
    const names = known.map((server) => server.name).join(', ');
    identity.append(el('p', 'profile-servers mono', names));
  } else if (profile.serverIDs.length === 0) {
    identity.append(el('p', 'profile-servers muted', 'Empty by design — applying it switches every active server off.'));
  }


  const recommendations = profile.analysis?.recommendations ?? [];
  if (recommendations.length > 0) {
    const insights = el('div', 'profile-insights');
    insights.append(el('strong', null, 'Context optimizer'));
    for (const recommendation of recommendations) {
      const line = el('p', `profile-insight ${recommendation.severity}`, recommendation.summary);
      if (recommendation.serverIDs?.length) {
        const names = recommendation.serverIDs
          .map((id) => state.servers.find((server) => server.id === id)?.name ?? id)
          .join(', ');
        line.append(el('span', 'mono muted', ` ${names}`));
      }
      insights.append(line);
    }
    identity.append(insights);
  }

  const actions = el('div', 'profile-actions');
  actions.append(
    button('Apply…', 'apply-profile', { dataset: { profileId: profile.id } }),
    button('Edit', 'edit-profile', { className: 'button subtle', dataset: { profileId: profile.id } }),
    button('Delete…', 'delete-profile', { className: 'button subtle', dataset: { profileId: profile.id } }),
  );
  row.append(identity, actions);
  return row;
}

export function renderProfileEditor(state) {
  const current = state.sheet;
  const editing = current.mode === 'edit';
  const body = [];

  if (current.error) {
    const errors = el('div', 'form-errors');
    errors.append(el('p', null, current.error));
    body.push(errors);
  }

  body.push(field('Name', input(current.name, { action: 'profile-name', placeholder: 'Coding' })));
  body.push(field(
    'Token budget (optional)',
    input(current.tokenBudget, { action: 'profile-budget', placeholder: '15000', type: 'number' }),
    'Kytto warns when measured server contracts exceed this profile budget. Empty means no limit.',
  ));

  const picker = el('div', 'profile-server-picker');
  picker.append(el('h3', null, 'Servers in this profile'));
  picker.append(
    el(
      'p',
      'field-hint',
      'An empty profile is valid: applying it switches every active server off in the selected client.',
    ),
  );

  if (state.servers.length === 0) {
    picker.append(el('p', 'muted', 'No servers are available yet.'));
  } else {
    for (const server of state.servers) {
      const label = el('label', 'profile-server-option');
      const check = el('input');
      check.type = 'checkbox';
      check.checked = current.serverIDs.includes(server.id);
      check.dataset.action = 'profile-server';
      check.dataset.serverId = server.id;
      const details = el('span', 'profile-option-copy');
      details.append(el('span', 'profile-option-name', server.name));
      details.append(
        el(
          'span',
          'profile-option-meta',
          server.tokenWeight
            ? `${server.tokenWeight.isMeasured ? '' : '~'}${formatTokens(server.tokenWeight.estimate)} tokens`
            : 'not measured',
        ),
      );
      label.append(check, details);
      picker.append(label);
    }
  }
  body.push(picker);

  return sheet(editing ? 'Edit profile' : 'New profile', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(current.busy ? 'Saving…' : 'Save profile', current.busy ? 'noop' : 'save-profile', {
      className: current.busy ? 'button primary busy' : 'button primary',
    }),
  ]);
}

export function renderProfileApply(state) {
  const profile = state.profiles.find((entry) => entry.id === state.sheet.profileId);
  if (!profile) return null;

  const clients = state.clients.filter((client) => client.state !== 'notInstalled' && !client.isReadOnly);
  const client = clients.find((entry) => entry.id === state.sheet.clientId);
  const body = [];

  if (clients.length === 0) {
    body.push(el('p', 'muted', 'No configured or installed client is available.'));
  } else {
    const select = el('select', 'text-input');
    select.dataset.action = 'profile-client';
    for (const optionClient of clients) {
      const option = el('option');
      option.value = optionClient.id;
      option.selected = optionClient.id === state.sheet.clientId;
      option.textContent = optionClient.state === 'orphanedConfig'
        ? `${optionClient.displayName} — not installed`
        : optionClient.displayName;
      select.append(option);
    }
    body.push(field('Apply to', select));

    if (client) body.push(applyPreview(state, profile, client));
  }

  body.push(
    el(
      'p',
      'muted',
      'Kytto will back up every affected configuration before writing. Servers installed as read-only plugins may be reported as skipped.',
    ),
  );

  return sheet(`Apply “${profile.name}”`, body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(
      state.sheet.busy ? 'Applying…' : 'Apply profile',
      state.sheet.busy || !client ? 'noop' : 'confirm-profile-apply',
      {
        className: state.sheet.busy ? 'button primary busy' : 'button primary',
        dataset: { profileId: profile.id, clientId: client?.id ?? '' },
      },
    ),
  ]);
}

function applyPreview(state, profile, client) {
  const desired = new Set(profile.serverIDs);
  const knownDesired = state.servers.filter((server) => desired.has(server.id));
  const toEnable = knownDesired.filter((server) => server.enabledIn[client.id] !== 'enabled');
  const toDisable = state.servers.filter(
    (server) => !desired.has(server.id) && server.enabledIn[client.id] === 'enabled',
  );
  const unchanged = knownDesired.length - toEnable.length;
  const box = el('div', 'profile-preview');
  box.append(el('h3', null, 'Preview'));
  const line = el('div', 'profile-preview-client');
  line.append(clientIcon(client), el('strong', null, client.displayName));
  box.append(line);
  box.append(
    previewLine('Enable', toEnable),
    previewLine('Keep on', knownDesired.filter((server) => server.enabledIn[client.id] === 'enabled')),
    previewLine('Switch off', toDisable),
  );
  if (profile.serverIDs.length > knownDesired.length) {
    box.append(
      el(
        'p',
        'profile-summary warning',
        `${profile.serverIDs.length - knownDesired.length} profile server(s) are no longer present and will be reported as skipped.`,
      ),
    );
  }
  if (toEnable.length === 0 && toDisable.length === 0 && unchanged === knownDesired.length) {
    box.append(el('p', 'muted', 'This client already matches the profile.'));
  }
  return box;
}

function previewLine(label, servers) {
  const row = el('div', 'profile-preview-row');
  row.append(el('span', 'profile-preview-label', label));
  row.append(el('span', servers.length ? null : 'muted', servers.length ? servers.map((server) => server.name).join(', ') : 'None'));
  return row;
}

export function renderProfileDelete(state) {
  const profile = state.profiles.find((entry) => entry.id === state.sheet.profileId);
  if (!profile) return null;
  return sheet('Delete profile', [
    el('p', null, `Delete “${profile.name}”?`),
    el('p', 'muted', 'This removes only the saved profile. It does not change any client configuration.'),
  ], [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button('Delete', 'confirm-profile-delete', { className: 'button danger', dataset: { profileId: profile.id } }),
  ]);
}
