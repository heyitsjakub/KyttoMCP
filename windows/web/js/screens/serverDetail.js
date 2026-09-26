// Server detail (§7.5): the full command, resolved environment with secrets
// masked, transport, per-client enablement, and the way to edit or delete.

import { el, button, sheet, definitionList, formatTokens, clientIcon } from '../dom.js';

const CLIENT_STATE_LABEL = {
  enabled: 'Enabled',
  disabled: 'Disabled',
  absent: 'Not configured',
};

export function renderServerDetail(state) {
  const server = state.servers.find((entry) => entry.id === state.sheet.serverId);
  if (!server) return null;

  const body = [];

  body.push(
    definitionList([
      ['Transport', server.transport],
      server.transport === 'stdio'
        ? ['Command', server.commandSummary, 'mono selectable']
        : ['URL', server.url ?? '', 'mono selectable'],
      ['Source', server.originKind === 'extension' ? 'Claude Desktop extension' : 'Config file'],
    ]),
  );

  if (server.args.length > 0) {
    const args = el('section', 'detail-section');
    args.append(el('h3', null, 'Arguments'));
    const list = el('ol', 'arg-list');
    for (const argument of server.args) {
      list.append(el('li', 'mono selectable', argument));
    }
    args.append(list);
    body.push(args);
  }

  const provenance = provenanceSection(state, server);
  if (provenance) body.push(provenance);

  const drift = driftSection(state, server);
  if (drift) body.push(drift);

  body.push(healthSection(state, server));
  const contract = contractSection(state, server);
  if (contract) body.push(contract);
  body.push(environmentSection(server));
  body.push(clientsSection(state, server));

  const readOnlyCopies = state.clients.filter(
    (client) =>
      client.isReadOnly && (server.enabledIn[client.id] ?? 'absent') !== 'absent',
  );
  const writableCopies = state.clients.filter(
    (client) =>
      !client.isReadOnly && (server.enabledIn[client.id] ?? 'absent') !== 'absent',
  );
  if (readOnlyCopies.length > 0) {
    body.push(
      el(
        'p',
        'read-only-note',
        `A copy comes from ${listPhrase(readOnlyCopies.map((client) => client.displayName))}. Kytto will not edit or delete that read-only copy.`,
      ),
    );
  }

  const managedRoutes = state.gatewayRoutes.filter((route) => route.serverID === server.id);
  if (managedRoutes.length > 0) {
    body.push(
      el(
        'p',
        'gateway-lock-note',
        'This server has an active Gateway route. Restore Direct mode for every client before editing or deleting the server.',
      ),
    );
  }

  if (server.isBundled) {
    body.push(
      el(
        'p',
        'muted',
        'This server came from an installed Claude Desktop extension. Its command is part of the bundle, so it can be switched on and off here but not edited or deleted.',
      ),
    );
  }

  // Where the matrix has room for a tooltip, this has room for the reason.
  if (server.hasRelativePath) {
    body.push(
      el(
        'p',
        'muted',
        'This server is launched through a relative path, which is resolved against the directory the client runs from. It works where it is configured now; switching it on in another client will write correctly and may still not start. An absolute path travels.',
      ),
    );
  }

  const footer = [];
  if (!server.isBundled && managedRoutes.length === 0 && writableCopies.length > 0) {
    footer.push(button(
      readOnlyCopies.length > 0 ? 'Delete writable copies…' : 'Delete…',
      'delete-server',
      { className: 'button danger', dataset: { serverId: server.id } },
    ));
    footer.push(el('span', 'spacer'));
    footer.push(button(
      readOnlyCopies.length > 0 ? 'Edit writable copies' : 'Edit',
      'edit-server',
      { dataset: { serverId: server.id } },
    ));
  }

  return sheet(server.name, body, footer.length ? footer : null);
}

/// What each client thinks this server is, when they do not agree.
///
/// The matrix shows one row per server, which is the product — but the row has
/// to pick a command to print, and picking one hides the fact that the others
/// exist. This is where the row admits it: every distinct copy, who holds it,
/// and one button to end the argument.
const DRIFT_FIELD_LABEL = {
  command: 'the command',
  arguments: 'the arguments',
  url: 'the URL',
  transport: 'the transport',
  environmentKeys: 'which environment variables are set',
  environmentValues: 'an environment value',
};

function driftSection(state, server) {
  const drift = (state.drift ?? []).find((entry) => entry.serverID === server.id);
  if (!drift) return null;

  const section = el('section', 'detail-section drift');
  section.append(el('h3', null, 'Configured differently in each client'));

  const fields = drift.fields.map((name) => DRIFT_FIELD_LABEL[name] ?? name);
  section.append(
    el(
      'p',
      'muted',
      `These clients disagree about ${listPhrase(fields)}. The matrix shows one row, so only one of these commands is on screen anywhere else.`,
    ),
  );

  for (const variant of drift.variants) {
    section.append(driftVariant(state, server, variant));
  }

  if (drift.unwritableClientIDs.length > 0) {
    section.append(
      el(
        'p',
        'field-hint',
        `${listPhrase(drift.unwritableClientIDs.map((id) => clientName(state, id)))} cannot be changed from here — an extension or plugin is installed software, not configuration.`,
      ),
    );
  }

  return section;
}

function driftVariant(state, server, variant) {
  const box = el('div', 'drift-variant');

  const head = el('div', 'drift-variant-head');
  const who = el('div', 'drift-clients');
  for (const clientID of variant.clientIDs) {
    const client = state.clients.find((entry) => entry.id === clientID);
    const tag = el('span', 'drift-client');
    if (client) tag.append(clientIcon(client));
    tag.append(el('span', null, clientName(state, clientID)));
    who.append(tag);
  }
  head.append(who);

  // An installed bundle takes part in the comparison and can never be the
  // answer to it, so it says why instead of offering a button that would fail.
  const includesReadOnlySource = variant.clientIDs.some(
    (clientID) => state.clients.find((client) => client.id === clientID)?.isReadOnly,
  );
  if (variant.canBeSource && !includesReadOnlySource) {
    head.append(el('span', 'spacer'));
    head.append(
      button('Use this everywhere…', 'unify-server', {
        className: 'button subtle',
        dataset: { serverId: server.id, clientId: variant.clientIDs[0] },
      }),
    );
  } else {
    head.append(el('span', 'spacer'));
    head.append(
      el('span', 'badge', includesReadOnlySource ? 'read-only source' : 'installed bundle'),
    );
  }
  box.append(head);

  box.append(el('code', 'mono selectable drift-command', variant.commandSummary));

  const notes = [];
  if (variant.transport !== server.transport) notes.push(`transport: ${variant.transport}`);
  if (variant.environmentKeys.length > 0) {
    notes.push(`env: ${variant.environmentKeys.join(', ')}`);
  }
  if (notes.length > 0) box.append(el('p', 'field-hint', notes.join(' · ')));

  return box;
}

const clientName = (state, clientID) =>
  state.clients.find((entry) => entry.id === clientID)?.displayName ?? clientID;

/// "a", "a and b", "a, b and c" — the report reads as a sentence rather than as
/// a comma-joined array.
function listPhrase(items) {
  if (items.length === 0) return '';
  if (items.length === 1) return items[0];
  return `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

function contractSection(state, server) {
  const alert = (state.contractAlerts ?? []).find((entry) => entry.serverID === server.id);
  if (!alert) return null;
  const section = el('section', 'detail-section contract-alert');
  section.append(el('h3', null, 'Contract Guard'));
  section.append(el('p', 'muted', `Changed during the health check on ${new Date(alert.changedAt * 1000).toLocaleString()}.`));
  const list = el('ul', 'contract-change-list');
  for (const change of alert.changes) {
    const item = el('li', `contract-change ${change.severity}`);
    item.append(el('strong', 'mono', change.toolName));
    item.append(el('span', null, change.summary));
    list.append(item);
  }
  section.append(list);
  section.append(el('p', 'field-hint', 'Descriptions, schemas and safety annotations are model-facing behavior. Review unexpected changes before relying on this server.'));
  // Without this the alert has no way to end: it is rewritten only by the next
  // check that finds something *different*, so a change you have read and
  // accepted keeps being reported. Accepting makes this contract the baseline.
  section.append(
    button('Mark as reviewed', 'acknowledge-contract', {
      className: 'button subtle',
      dataset: { serverId: server.id },
    }),
  );
  return section;
}

/// Health, token weight, the tool list, and the server's own error output (§7.3).
function healthSection(state, server) {
  const section = el('section', 'detail-section');

  const heading = el('div', 'section-heading');
  heading.append(el('h3', null, 'Health'));
  const busy = state.checking.has(server.id);
  if (server.health) {
    heading.append(
      button('Copy report', 'copy-diagnostic', {
        className: 'button subtle',
        dataset: { serverId: server.id },
      }),
    );
  }
  heading.append(
    button(busy ? 'Checking…' : 'Check now', busy ? 'noop' : 'check-server', {
      className: busy ? 'button subtle busy' : 'button subtle',
      dataset: { serverId: server.id },
    }),
  );
  section.append(heading);

  const health = server.health;
  if (!health) {
    section.append(
      el(
        'p',
        'muted',
        server.transport === 'stdio'
          ? 'Not checked yet. Checking runs the command below, so Kytto only does it when you ask.'
          : 'Kytto cannot check remote servers yet.',
      ),
    );
    return section;
  }

  const pairs = [
    ['Status', statusLabel(health), `status-${health.status}`],
    ['Checked', new Date(health.checkedAt * 1000).toLocaleString()],
  ];
  if (health.toolCount != null) pairs.push(['Tools', String(health.toolCount)]);
  if (health.promptCount != null) pairs.push(['Prompts', String(health.promptCount)]);
  if (health.resourceCount != null) pairs.push(['Resources', String(health.resourceCount)]);
  if (server.tokenWeight) {
    pairs.push([
      'Context cost',
      `${server.tokenWeight.isMeasured ? '' : '~'}${formatTokens(server.tokenWeight.estimate)} tokens · ${server.tokenWeight.percentOfContext.toFixed(1)}% of 200k`,
    ]);
  }
  if (health.serverName) {
    pairs.push(['Reports as', [health.serverName, health.serverVersion].filter(Boolean).join(' ')]);
  }
  if (health.protocolVersion) pairs.push(['Protocol', health.protocolVersion, 'mono selectable']);
  if (health.capabilityNames?.length) pairs.push(['Capabilities', health.capabilityNames.join(', ')]);
  if (health.resolvedCommand) pairs.push(['Executable', health.resolvedCommand, 'mono selectable']);
  if (health.environmentSource) pairs.push(['Environment', health.environmentSource]);
  pairs.push(['Took', `${health.durationSeconds.toFixed(1)}s`]);
  section.append(definitionList(pairs));

  if (server.tokenWeight) {
    section.append(el('p', 'field-hint', weightMethodNote(server.tokenWeight)));
  }

  if (health.message) {
    const messageClass = health.status === 'failed'
      ? 'failure-message'
      : health.status === 'needsAuthorization'
        ? 'auth-message'
        : 'muted';
    section.append(el('p', messageClass, health.message));
  }

  if (health.status === 'needsAuthorization' && health.authorizationURL) {
    const actions = el('div', 'auth-actions');
    actions.append(
      button('Open sign-in page', 'open-authorization', {
        className: 'button subtle',
        dataset: { serverId: server.id },
      }),
      el('span', 'mono selectable', health.authorizationURL),
    );
    section.append(actions);
  }

  if (health.inspectionNotes?.length) {
    const notes = el('ul', 'inspection-notes');
    for (const note of health.inspectionNotes) notes.append(el('li', null, note));
    section.append(notes);
  }

  if (health.tools.length > 0) {
    const tools = toolsByWeight(health.tools);
    const heaviest = tools[0]?.tokenCount ?? 0;

    const heading = el('h3', 'inspector-heading', `Tools (${health.tools.length})`);
    // Saying so matters: the list is no longer in the order the server sent,
    // and a reader looking for one tool needs to know why it moved.
    if (heaviest > 0) heading.append(el('span', 'muted', 'heaviest first'));
    section.append(heading);

    const list = el('div', 'inspector-tools');
    for (const tool of tools) {
      const item = el('details', 'inspector-tool');
      item.dataset.toolName = tool.name;
      item.open = state.sheet.openTools?.includes(tool.name) ?? false;
      const summary = el('summary');
      summary.append(el('span', 'mono inspector-tool-name', tool.name));
      for (const annotation of annotationLabels(tool.annotations)) {
        summary.append(el('span', `badge tool-annotation ${annotation.kind}`, annotation.label));
      }
      if (tool.description) summary.append(el('span', 'muted inspector-tool-description', firstLine(tool.description)));
      if (tool.tokenCount != null) summary.append(toolWeight(tool.tokenCount, heaviest));
      item.append(summary);
      if (tool.description) item.append(el('p', 'selectable', tool.description));
      if (tool.inputSchemaJSON) {
        item.append(el('h4', null, 'Input schema'));
        item.append(el('pre', 'schema-json selectable', prettyJSON(tool.inputSchemaJSON)));
      } else {
        item.append(el('p', 'muted', 'No input schema was returned.'));
      }
      list.append(item);
    }
    section.append(list);
  }

  if (health.stderr) {
    section.append(el('h3', 'stderr-heading', 'Server output'));
    // Verbatim. §6: this is the most useful thing you can show someone whose
    // server is broken, and paraphrasing it would throw that away.
    const output = el('pre', 'stderr selectable', health.stderr);
    section.append(output);
  }

  return section;
}

/// Heaviest first, so the question the breakdown exists to answer — what is
/// this server actually spending my context on — is answered by the first row
/// rather than by reading all forty.
///
/// Unmeasured tools keep the server's own order behind the measured ones: a
/// health check from an older build has no counts, and inventing an order for
/// them would be worse than leaving them alone.
function toolsByWeight(tools) {
  const measured = tools.filter((tool) => tool.tokenCount != null);
  if (measured.length === 0) return tools;
  return [
    ...measured.sort((left, right) => right.tokenCount - left.tokenCount),
    ...tools.filter((tool) => tool.tokenCount == null),
  ];
}

/// A number and a bar. The bar is relative to the heaviest tool on this server,
/// not to the context window — at this scale every tool would otherwise be an
/// invisible sliver, and the comparison worth making here is between siblings.
function toolWeight(count, heaviest) {
  const wrapper = el('span', 'tool-weight');
  const track = el('span', 'tool-weight-track');
  const fill = el('span', 'tool-weight-fill');
  fill.style.width = `${heaviest > 0 ? Math.max(2, Math.round((count / heaviest) * 100)) : 0}%`;
  track.append(fill);
  wrapper.append(track, el('span', 'tool-weight-count mono', formatTokens(count)));
  wrapper.title = `${count.toLocaleString()} tokens`;
  return wrapper;
}

/// §7.4 used to make the UI say "estimate" unconditionally. It now depends on
/// whether a real vocabulary answered, because claiming a measurement is an
/// estimate is as dishonest as the reverse.
function weightMethodNote(weight) {
  return weight.isMeasured
    ? `Counted with the ${weight.method} vocabulary over the tool definitions the server returned.`
    : `Approximated as ${weight.method} over the tool definitions — good for ranking servers, not an exact count.`;
}

function annotationLabels(annotations) {
  if (!annotations) return [];
  const labels = [];
  if (annotations.readOnlyHint === true) labels.push({ label: 'read only', kind: 'safe' });
  if (annotations.destructiveHint === true) labels.push({ label: 'destructive', kind: 'danger' });
  if (annotations.idempotentHint === true) labels.push({ label: 'idempotent', kind: 'neutral' });
  if (annotations.openWorldHint === true) labels.push({ label: 'external', kind: 'warning' });
  return labels;
}

function prettyJSON(text) {
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

/// A support-ready summary with environment values and raw stderr deliberately
/// omitted. Both can contain credentials; the full output remains visible in
/// the app when the user needs it.
export function diagnosticReport(server, state) {
  const health = server.health;
  if (!health) return '';
  const clients = state.clients
    .filter((client) => (server.enabledIn[client.id] ?? 'absent') !== 'absent')
    .map((client) => `${client.displayName}: ${server.enabledIn[client.id]}`)
    .join(', ');
  const lines = [
    'Kytto MCP diagnostic report',
    `Generated: ${new Date().toISOString()}`,
    `Server: ${server.name}`,
    `Transport: ${server.transport}`,
    server.command
      ? `Command: ${server.command} (${server.args.length} argument${server.args.length === 1 ? '' : 's'} omitted)`
      : `Endpoint: ${server.url ?? '(not configured)'}`,
    `Clients: ${clients || 'none'}`,
    `Status: ${health.status}`,
    `Checked: ${new Date(health.checkedAt * 1000).toISOString()}`,
    `Duration: ${health.durationSeconds.toFixed(2)}s`,
    `Protocol: ${health.protocolVersion ?? 'unknown'}`,
    `Server identity: ${[health.serverName, health.serverVersion].filter(Boolean).join(' ') || 'unknown'}`,
    `Capabilities: ${health.capabilityNames?.join(', ') || 'none reported'}`,
    `Tools: ${health.toolCount ?? 'unknown'}`,
    `Prompts: ${health.promptCount ?? 'not advertised or not available'}`,
    `Resources: ${health.resourceCount ?? 'not advertised or not available'}`,
    `Executable: ${health.resolvedCommand ?? 'not resolved'}`,
    `Environment source: ${health.environmentSource ?? 'unknown'}`,
  ];
  if (health.authorizationURL) lines.push(`Authorization page: ${health.authorizationURL}`);
  if (health.message) lines.push(`Message: ${health.message}`);
  for (const note of health.inspectionNotes ?? []) lines.push(`Inspector note: ${note}`);
  lines.push('', 'Tools:');
  for (const tool of health.tools) {
    const annotations = annotationLabels(tool.annotations).map((item) => item.label).join(', ');
    lines.push(`- ${tool.name}${annotations ? ` [${annotations}]` : ''}`);
    if (tool.description) lines.push(`  ${firstLine(tool.description)}`);
    if (tool.inputSchemaJSON) lines.push(`  Input schema: ${tool.inputSchemaJSON}`);
  }
  lines.push('', 'Environment values and raw server output are omitted because they may contain secrets.');
  return lines.join('\n');
}

const firstLine = (text) => text.split('\n')[0];

function statusLabel(health) {
  switch (health.status) {
    case 'passed':
      return 'Passed';
    case 'failed':
      return 'Failed';
    case 'needsAuthorization':
      return 'Waiting for authorization';
    default:
      return 'Not checked';
  }
}

function environmentSection(server) {
  const section = el('section', 'detail-section');
  section.append(el('h3', null, 'Environment'));

  if (server.env.length === 0) {
    section.append(el('p', 'muted', 'No environment variables.'));
    return section;
  }

  const table = el('table', 'env-table');
  for (const entry of server.env) {
    const row = el('tr');
    row.append(el('td', 'mono', entry.key));
    // §6: a value never crosses the IPC boundary, so there is nothing here to
    // reveal even by accident. The Secrets screen is where a copy can be kept in
    // the platform credential store.
    row.append(el('td', 'muted', entry.hasValue ? '••••••••' : 'not set'));
    table.append(row);
  }
  section.append(table);
  section.append(
    el('p', 'field-hint', 'Values are stored in the client config file. Kytto never sends them to this screen.'),
  );
  return section;
}

function provenanceSection(state, server) {
  const provenance = server.provenance;
  if (!provenance) return null;

  const section = el('section', 'detail-section provenance-section');
  const heading = el('div', 'section-heading');
  heading.append(el('h3', null, 'Provenance'));
  const canCheck = Boolean(
    provenance.packageName && state.app?.capabilities?.includes('provenance'),
  );
  if (canCheck) {
    const busy = state.provenanceChecking?.has(server.id) === true;
    heading.append(button(busy ? 'Checking…' : 'Check latest', busy ? 'noop' : 'check-provenance', {
      className: busy ? 'button subtle busy' : 'button subtle',
      dataset: { serverId: server.id },
    }));
  }
  section.append(heading);

  const pairs = [
    ['Source', provenance.sourceKind],
    ['Confidence', provenance.confidence],
    ['Maintenance', provenance.maintenanceState],
  ];
  if (provenance.packageName) pairs.push(['Package', provenance.packageName, 'mono selectable']);
  if (provenance.sourceURL) pairs.push(['Source URL', provenance.sourceURL, 'mono selectable']);
  if (provenance.installedVersion) pairs.push(['Installed', provenance.installedVersion]);
  if (provenance.latestVersion) pairs.push(['Latest known', provenance.latestVersion]);
  if (provenance.latestCheckedAt) pairs.push(['Checked', new Date(provenance.latestCheckedAt * 1000).toLocaleString()]);
  section.append(definitionList(pairs));

  if (!provenance.packageName) {
    section.append(el('p', 'field-hint', provenance.sourceKind === 'remote'
      ? 'Remote endpoint provenance is recorded locally; no package registry lookup is available.'
      : 'Kytto could not identify a package name for an explicit latest-version check.'));
  } else {
    section.append(el('p', 'field-hint', 'Latest-version checks are explicit, first-party and cached for this session. Kytto never updates packages automatically.'));
  }
  return section;
}

/// The per-client states, and the one act the matrix cannot express.
///
/// A cell is a two-position switch: off is `disabled` in every client, because
/// each one keeps enough to switch back on — the definition itself, or Kytto's
/// parked copy of it. Getting back to `absent` is a different act, it is
/// destructive, and it needs room to name the file it will edit. That is this
/// list rather than a third click on a 15px square.
function clientsSection(state, server) {
  const section = el('section', 'detail-section');
  section.append(el('h3', null, 'Clients'));

  const table = el('table', 'env-table clients-table');
  for (const client of state.clients) {
    if (client.state === 'notInstalled') continue;
    const value = server.enabledIn[client.id] ?? 'absent';
    const route = state.gatewayRoutes.find(
      (entry) => entry.serverID === server.id && entry.clientID === client.id,
    );
    const row = el('tr');
    row.append(el('td', null, client.displayName));
    const stateCell = el('td', `client-state ${value}`);
    stateCell.append(el('span', null, CLIENT_STATE_LABEL[value]));
    if (client.isReadOnly) stateCell.append(el('span', 'badge', 'read only'));
    if (route) stateCell.append(el('span', 'badge gateway-badge', 'Gateway'));
    // A narrowed route is worth saying out loud: the model in this client is
    // seeing a different set of tools from the one the server offers (§7.11).
    if (route?.exposedTools) {
      const total = server.health?.tools?.length;
      stateCell.append(
        el(
          'span',
          'badge mask-badge',
          total ? `${route.exposedTools.length} of ${total} tools` : `${route.exposedTools.length} tools`,
        ),
      );
    }
    row.append(stateCell);
    row.append(el('td', 'mono muted', client.configPathDisplay));

    const action = el('td', 'client-action');
    // An extension is installed software; nothing here edits it.
    if (client.isReadOnly) {
      action.append(el('span', 'field-hint', 'Kytto never changes this file.'));
    } else if (route) {
      action.append(
        button('Choose tools…', 'choose-tools', {
          className: 'button subtle',
          dataset: { routeId: route.id },
        }),
      );
      action.append(
        button('Restore Direct…', 'restore-gateway', {
          className: 'button subtle',
          dataset: { routeId: route.id },
        }),
      );
    } else if (
      value === 'enabled' &&
      server.transport === 'stdio' &&
      !server.isBundled &&
      state.app?.capabilities?.includes('gateway.stdio')
    ) {
      action.append(
        button('Use Gateway…', 'enable-gateway', {
          className: 'button subtle',
          dataset: { serverId: server.id, clientId: client.id },
        }),
      );
      action.append(
        button('Remove', 'remove-from-client', {
          className: 'button subtle',
          dataset: { serverId: server.id, clientId: client.id },
        }),
      );
    } else if (value !== 'absent' && !server.isBundled) {
      action.append(
        button('Remove', 'remove-from-client', {
          className: 'button subtle',
          dataset: { serverId: server.id, clientId: client.id },
        }),
      );
    }
    row.append(action);
    table.append(row);
  }
  section.append(table);
  return section;
}

/// Confirming a unify. Every client that is about to be rewritten is named,
/// because this is the one action in the app that changes several files on the
/// strength of a single click.
export function renderUnifyPreview(state) {
  const preview = state.sheet.preview;
  if (!preview) return null;

  const source = clientName(state, preview.sourceClientID);
  const targets = preview.targetClientIDs.map((id) => clientName(state, id));

  const body = [
    el(
      'p',
      null,
      `Rewrite ${listPhrase(targets)} so “${preview.serverName}” matches the copy in ${source}?`,
    ),
    el('code', 'mono selectable drift-command', preview.commandSummary),
  ];

  if (preview.environmentKeys.length > 0) {
    body.push(
      el(
        'p',
        'field-hint',
        `${source}'s values for ${listPhrase(preview.environmentKeys)} are written too. They are read natively at the moment of writing and are not on this screen.`,
      ),
    );
  }

  if (preview.skippedClientIDs.length > 0) {
    body.push(
      el(
        'p',
        'muted',
        `${listPhrase(preview.skippedClientIDs.map((id) => clientName(state, id)))} keeps its own copy — an installed bundle is not configuration Kytto may rewrite.`,
      ),
    );
  }

  body.push(
    el(
      'p',
      'muted',
      'Every file is backed up before it is written, and refused outright if it changed on disk since Kytto last read it.',
    ),
  );
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Make the copies match', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Writing…' : 'Unify', state.sheet.busy ? 'noop' : 'confirm-unify', {
      className: state.sheet.busy ? 'button primary busy' : 'button primary',
      dataset: { serverId: preview.serverID, clientId: preview.sourceClientID },
    }),
  ]);
}

export function renderGatewayPreview(state) {
  const preview = state.sheet.preview;
  if (!preview) return null;
  const server = state.servers.find((entry) => entry.id === preview.serverID);
  const client = state.clients.find((entry) => entry.id === preview.clientID);

  const body = [
    el(
      'p',
      null,
      `Route “${server?.name ?? preview.serverName}” through Kytto for ${client?.displayName ?? preview.clientID}?`,
    ),
    el('p', 'mono muted', preview.pathDisplay),
  ];

  const comparison = el('div', 'gateway-preview-grid');
  comparison.append(gatewayPreviewBlock('Direct now', preview.directDefinitionPreview));
  comparison.append(gatewayPreviewBlock('Gateway after', preview.gatewayDefinitionPreview));
  body.push(comparison);

  body.push(
    el(
      'p',
      'gateway-security-note',
      preview.environmentKeys.length > 0
        ? `${preview.environmentKeys.length} environment value${preview.environmentKeys.length === 1 ? '' : 's'} will move into the system credential store. The preview deliberately hides their values.`
        : 'No environment values need to move. Tool arguments and responses will not be recorded.',
    ),
  );
  body.push(
    el(
      'p',
      'muted',
      'Kytto backs up the client configuration first. You can restore the original Direct definition in one click.',
    ),
  );
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Enable Gateway mode', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Enabling…' : 'Enable Gateway', state.sheet.busy ? 'noop' : 'confirm-enable-gateway', {
      className: state.sheet.busy ? 'button primary busy' : 'button primary',
      dataset: {
        serverId: preview.serverID,
        clientId: preview.clientID,
        routeId: preview.routeID,
      },
    }),
  ]);
}

function gatewayPreviewBlock(label, text) {
  const block = el('section', 'gateway-preview-block');
  block.append(el('h3', null, label));
  block.append(el('pre', 'mono selectable', text));
  return block;
}

export function renderGatewayRestore(state) {
  const route = state.gatewayRoutes.find((entry) => entry.id === state.sheet.routeId);
  if (!route) return null;
  const client = state.clients.find((entry) => entry.id === route.clientID);
  const body = [
    el('p', null, `Restore “${route.serverName}” to Direct mode in ${client?.displayName ?? route.clientID}?`),
    el(
      'p',
      'muted',
      'Kytto will put back the exact original server definition, including its environment values, and remove this route. The current gateway config is backed up first.',
    ),
  ];
  if (state.sheet.error) body.push(el('p', 'failure-message', state.sheet.error));

  return sheet('Restore Direct mode', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(state.sheet.busy ? 'Restoring…' : 'Restore Direct', state.sheet.busy ? 'noop' : 'confirm-restore-gateway', {
      className: state.sheet.busy ? 'button busy' : 'button',
      dataset: { routeId: route.id },
    }),
  ]);
}

/// Removing from one client is destructive and irreversible from the matrix, so
/// it asks first, names the file, and says what it will leave behind.
export function renderRemoveConfirm(state) {
  const server = state.servers.find((entry) => entry.id === state.sheet.serverId);
  const client = state.clients.find((entry) => entry.id === state.sheet.clientId);
  if (!server || !client) return null;

  const others = state.clients.filter(
    (entry) =>
      entry.id !== client.id && (server.enabledIn[entry.id] ?? 'absent') !== 'absent',
  );

  const body = [];
  body.push(el('p', null, `Take “${server.name}” out of ${client.displayName}?`));
  body.push(el('p', 'mono muted', client.configPathDisplay));
  body.push(
    el(
      'p',
      'muted',
      others.length
        ? `It stays in ${others.map((entry) => entry.displayName).join(', ')}, so you can switch it back on for ${client.displayName} later.`
        : `${client.displayName} is the last client that has it, so the row goes with it.`,
    ),
  );
  body.push(
    el(
      'p',
      'muted',
      'This is not the same as switching it off: off keeps the definition so it can come back exactly as it was. The file is backed up first either way.',
    ),
  );

  return sheet('Remove from client', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button('Remove', 'confirm-remove-from-client', {
      className: 'button danger',
      dataset: { serverId: server.id, clientId: client.id },
    }),
  ]);
}

/// Deleting is destructive and touches several files at once, so it asks first
/// and says exactly what it is about to do.
export function renderDeleteConfirm(state) {
  const server = state.servers.find((entry) => entry.id === state.sheet.serverId);
  if (!server) return null;

  const affected = state.clients.filter(
    (client) =>
      !client.isReadOnly && (server.enabledIn[client.id] ?? 'absent') !== 'absent',
  );
  const retained = state.clients.filter(
    (client) =>
      client.isReadOnly && (server.enabledIn[client.id] ?? 'absent') !== 'absent',
  );

  const body = [];
  body.push(el('p', null, `Remove “${server.name}” from ${affected.length === 1 ? 'this client' : 'these clients'}?`));

  const list = el('ul', 'plain-list');
  for (const client of affected) {
    const item = el('li');
    item.append(el('span', null, client.displayName));
    item.append(el('span', 'mono muted', client.configPathDisplay));
    list.append(item);
  }
  body.push(list);
  if (retained.length > 0) {
    body.push(
      el(
        'p',
        'muted',
        `The read-only ${retained.length === 1 ? 'copy' : 'copies'} in ${listPhrase(retained.map((client) => client.displayName))} will remain visible and unchanged.`,
      ),
    );
  }
  body.push(
    el('p', 'muted', 'Each file is backed up first, so this can be undone from Backups.'),
  );

  return sheet('Delete server', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button('Delete', 'confirm-delete', { className: 'button danger', dataset: { serverId: server.id } }),
  ]);
}
