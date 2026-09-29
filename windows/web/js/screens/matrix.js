// The main screen: row = server, column = client, cell = toggle.
//
// Everything here is built with createElement and textContent. Server names,
// commands and error strings come from config files Kytto did not write, so they
// are never interpolated into markup (§3.1).

import {
  visibleServers,
  matrixClients,
  getState,
  restartsPending,
  cellKey,
  enabledTokenTotal,
  contextWindow,
  toolBudgetFigure,
  toolBudgetStrained,
} from '../state.js';
import {
  el,
  button,
  formatTokens,
  sheet,
  glyph,
  GLYPH,
  clientIcon,
  definitionList,
} from '../dom.js';
import { addedIn, appeared, began, changed, changedIn, stagger } from '../anim.js';
import { toggleCell, cellSnapshot, serverOpener, dotSnapshot } from './serverRow.js';
import { renderBackups } from './backups.js';
import { renderClient } from './clientDetail.js';
import { renderOnboarding } from './onboarding.js';
import {
  renderProfiles,
  renderProfileEditor,
  renderProfileApply,
  renderProfileDelete,
} from './profiles.js';
import { renderSecrets, renderRotateSheet } from './secrets.js';
import {
  renderServerDetail,
  renderDeleteConfirm,
  renderRemoveConfirm,
  renderGatewayPreview,
  renderGatewayRestore,
  renderUnifyPreview,
} from './serverDetail.js';
import { renderServerForm } from './serverForm.js';
import { renderToolMask } from './toolMask.js';
import { renderSidebar, sidebarResizeHandle } from './sidebar.js';
import { renderDoctor, renderDoctorFix } from './doctor.js';
import { renderActivity } from './activity.js';
import { renderAgentImport, renderLibrary, renderSkills } from './tools.js';
let renderedPanelIdentity = null;

const PANEL_ORDER = new Map([
  ['matrix', 0],
  ['doctor', 1],
  ['activity', 2],
  ['profiles', 3],
  ['secrets', 4],
  ['backups', 5],
  ['library', 6],
  ['skills', 7],
  ['client', 8],
]);

function panelRank(identity) {
  if (!identity) return 0;
  if (identity.startsWith('client:')) return PANEL_ORDER.get('client');
  return PANEL_ORDER.get(identity) ?? 0;
}

export function render(root) {
  const state = getState();
  const scrollBeforeRender = scrollSnapshot(root);
  root.replaceChildren();

  if (state.phase === 'loading') {
    root.append(loadingState());
    restoreScroll(scrollBeforeRender, root);
    return;
  }

  if (state.phase === 'error') {
    root.append(errorState(state.error));
    restoreScroll(scrollBeforeRender, root);
    return;
  }

  const shellEntered = changed(
    'shell',
    state.settings?.hasCompletedOnboarding ? 'workspace' : 'onboarding',
    460,
  );

  // First run: say what was found before showing anything to act on (§8).
  if (state.settings && !state.settings.hasCompletedOnboarding) {
    root.append(renderOnboarding(state));
    restoreScroll(scrollBeforeRender, root);
    return;
  }

  const clients = matrixClients();
  const servers = visibleServers();

  // The band spans the window; below it the sidebar says where you are and the
  // content pane is the only opaque surface in the shell.
  const band = chromeBand(state);
  if (shellEntered) band.classList.add('shell-entering');
  root.append(band);

  const body = el('div');
  body.id = 'body';
  // A sheet is the only active surface while it is open. Keeping the workspace
  // in the DOM preserves its scroll position, while inert keeps keyboard and
  // assistive-technology navigation inside the dialog.
  body.inert = Boolean(state.sheet);
  if (shellEntered) body.classList.add('shell-entering');

  const panelIdentity = state.panel === 'client'
    ? `client:${state.selectedClient}`
    : state.panel ?? 'matrix';
  const previousPanelIdentity = renderedPanelIdentity;
  const panelChanged = previousPanelIdentity !== null && previousPanelIdentity !== panelIdentity;
  const panelDirection = panelRank(panelIdentity) >= panelRank(previousPanelIdentity)
    ? 'forward'
    : 'back';
  renderedPanelIdentity = panelIdentity;
  body.append(renderSidebar(state, panelChanged));
  body.append(sidebarResizeHandle(state));

  const content = el('main');
  content.id = 'content';

  const availableUpdate = updateBanner(state);
  if (availableUpdate) content.append(availableUpdate);

  const currentNotice = state.notice ?? state.leavingNotice;
  if (currentNotice) content.append(notice(currentNotice, !state.notice));

  let screen;
  if (state.panel === 'backups') {
    screen = renderBackups(state);
  } else if (state.panel === 'profiles') {
    screen = renderProfiles(state);
  } else if (state.panel === 'doctor') {
    screen = renderDoctor(state);
  } else if (state.panel === 'activity') {
    screen = renderActivity(state);
  } else if (state.panel === 'secrets') {
    screen = renderSecrets(state);
  } else if (state.panel === 'client') {
    screen = renderClient(state);
  } else if (state.servers.length === 0) {
    screen = emptyState(clients);
  } else if (servers.length === 0) {
    screen = el('div', 'placeholder', `Nothing matches “${state.filter}”.`);
  } else {
    screen = table(state, clients, servers);
  }
  if (panelChanged) screen.classList.add('panel-entering', `panel-${panelDirection}`);
  content.append(screen);

  if (state.diagnostics.length > 0) content.append(diagnostics(state.diagnostics));

  const visiblePending = Object.fromEntries(
    Object.entries(state.pendingRestarts)
      .filter(([clientId, count]) => count > 0 && !(state.dismissedRestarts ?? []).includes(clientId)),
  );
  const restartSource = Object.values(visiblePending).some((count) => count > 0)
    ? visiblePending
    : state.leavingRestarts;
  const restarts = restartSource ? restartsPending(restartSource) : [];
  if (restarts.length > 0) content.append(restartBar(
    restarts,
    Boolean(state.leavingRestarts && !Object.values(state.pendingRestarts).some((count) => count > 0)),
  ));

  content.append(footer(state));
  body.append(content);
  root.append(body);

  const modal = renderSheet(state);
  if (modal) {
    root.append(modal);
    // The state tree can legitimately refresh while a sheet is open. Restore
    // its view position after the replacement DOM has acquired layout, or a
    // config event turns scrolling a long server detail into a jump to the top.
    const body = modal.querySelector('.sheet-body');
    if (body) body.scrollTop = (state.sheet ?? state.leavingSheet)?.scrollTop ?? 0;
  }

  // State changes rebuild the whole tree. Put each workspace scroller back
  // before main.js restores focus, or the browser scrolls the replacement
  // toggle into view and the matrix appears to jump.
  restoreScroll(scrollBeforeRender, root);
}

export function scrollSnapshot(root) {
  return [...root.querySelectorAll('.sidebar, .table-scroll')].map((element, index) => ({
    key: element.dataset.scrollKey ?? String(index),
    scrollTop: element.scrollTop,
    scrollLeft: element.scrollLeft,
  }));
}

export function restoreScroll(snapshot, root) {
  const scrollers = new Map(
    [...root.querySelectorAll('.sidebar, .table-scroll')].map((element, index) => [
      element.dataset.scrollKey ?? String(index),
      element,
    ]),
  );
  for (const saved of snapshot) {
    const element = scrollers.get(saved.key);
    if (!element) continue;
    element.scrollTop = saved.scrollTop;
    element.scrollLeft = saved.scrollLeft;
  }
}

function renderSheet(state) {
  const current = state.sheet ?? state.leavingSheet;
  if (!current) return null;

  const renderState = state.sheet ? state : { ...state, sheet: current };
  const modal = sheetBody(renderState);
  // Only a sheet that was not on screen a moment ago animates in. Typing in a
  // field re-renders the whole tree, and a panel that re-pops per keystroke is
  // unusable — so the step and the subject, not the sheet's mere existence,
  // decide what counts as new.
  let entering = false;
  if (state.sheet) entering = began('sheet', sheetSubject(current));
  else began('sheet', null);
  const contentChanged = changed('sheet-content', sheetIdentity(current), 220);
  if (modal && entering) {
    modal.classList.add('is-entering');
  }
  if (modal && state.leavingSheet && !state.sheet) {
    modal.classList.add('is-leaving');
    modal.inert = true;
    modal.setAttribute('aria-hidden', 'true');
  }

  if (modal && state.sheet && contentChanged) {
    modal.querySelector('.sheet-body')?.classList.add('content-entering');
  }
  return modal;
}

const sheetSubject = (current) =>
  [current.kind, current.serverId, current.clientId, current.routeId, current.secretId, current.profileId, current.mode]
    .filter(Boolean)
    .join(':');

const sheetIdentity = (current) =>
  [sheetSubject(current), current.step, current.draft?.transport]
    .filter(Boolean)
    .join(':');

function sheetBody(state) {
  switch (state.sheet?.kind) {
    case 'detail':
      return renderServerDetail(state);
    case 'confirmDelete':
      return renderDeleteConfirm(state);
    case 'confirmRemoveFromClient':
      return renderRemoveConfirm(state);
    case 'gatewayPreview':
      return renderGatewayPreview(state);
    case 'gatewayRestore':
      return renderGatewayRestore(state);
    case 'toolMask':
      return renderToolMask(state);
    case 'unifyPreview':
      return renderUnifyPreview(state);
    case 'form':
      return renderServerForm(state);
    case 'rotate': {
      const parts = renderRotateSheet(state);
      return parts ? sheet(parts.title, parts.body, parts.footer) : null;
    }
    case 'profileEditor':
      return renderProfileEditor(state);
    case 'profileApply':
      return renderProfileApply(state);
    case 'profileDelete':
      return renderProfileDelete(state);
    case 'doctorFix':
      return renderDoctorFix(state);
    case 'confirmMatrixWrite':
      return renderMatrixWriteConfirmation(state);
    case 'agentImport':
      return renderAgentImport(state);
    case 'library':
      return renderLibrary(state);
    case 'skills':
      return renderSkills(state);
    default:
      return null;
  }
}

// MARK: - Toolbar band

/// Two rows sharing one surface. The upper one is the window's own title bar
/// strip — the shell owns the mouse up there, so it carries the numbers, which
/// need reading and not clicking, and dragging the window by them still works.
/// Everything interactive is in the lower row.
function chromeBand(state) {
  const band = el('header', 'chrome-band');

  const strip = el('div', 'drag-strip');
  strip.append(counts(state));

  const bar = el('div', 'toolbar');
  const checking = state.healthProgress;
  const actions = el('div', 'toolbar-actions');

  // Filtering servers, adding one and checking them all are things you do to the
  // matrix. On the other screens they would be controls pointing at something
  // that is not on screen, so only refresh — which re-reads every config
  // regardless of what you are looking at — survives the trip.
  if (state.panel === null) {
    const search = el('input', 'search');
    search.type = 'search';
    search.placeholder = 'Filter servers';
    search.value = state.filter;
    search.dataset.action = 'filter';
    search.setAttribute('aria-label', 'Filter servers');

    actions.append(button('Add server', 'add-server', { className: 'button primary' }));
    for (const control of [
      capabilityButton(state, 'agent-import', 'Import agent…', 'open-agent-import'),
      capabilityButton(state, 'library', 'MCP library…', 'open-library'),
      capabilityButton(state, 'skills', 'Skills…', 'open-skills'),
    ]) {
      if (control) actions.append(control);
    }
    actions.append(
      button(
        checking ? `Checking ${checking.index}/${checking.total}…` : 'Check all',
        checking ? 'noop' : 'check-all',
        { className: checking ? 'button busy' : 'button' },
      ),
    );
    bar.append(search);
  } else {
    bar.append(el('span', 'spacer'));
  }

  const refresh = el('button', 'button icon-button');
  refresh.dataset.action = 'refresh';
  refresh.title = 'Re-read every configuration from disk';
  refresh.setAttribute('aria-label', 'Refresh');
  refresh.append(glyph(GLYPH.refresh));
  actions.append(refresh);
  bar.append(actions);

  band.append(strip, bar);
  if (checking) band.append(progressLine(checking));
  return band;
}

function capabilityButton(state, capability, label, action) {
  const capabilities = state.app?.capabilities ?? [];
  return capabilities.includes(capability)
    ? button(label, action, { className: 'button subtle' })
    : null;
}

function counts(state) {
  const line = el('div', 'counts');
  const enabledServers = state.servers.filter((server) =>
    Object.values(server.enabledIn).includes('enabled'),
  );
  const enabledCount = enabledServers.length;
  line.append(countMetric(state.servers.length, 'servers', 'primary'));
  line.append(countMetric(enabledCount, 'active', 'active'));

  // The headline number: what the enabled servers cost in context (§7.1). The
  // share of the window is the part anyone actually reasons about, so it is
  // written out rather than left in a tooltip — this strip cannot be hovered.
  const total = enabledTokenTotal();
  if (total > 0) {
    const share = (total / contextWindow()) * 100;
    const heavy = total >= (state.settings?.tokenWarningThreshold ?? 20_000);
    const weighted = enabledServers.filter((server) => server.tokenWeight);
    const incomplete = weighted.length < enabledServers.length;
    // A sum is only as measured as its least measured term. One server still
    // carrying a chars/4 figure keeps the tilde on the whole total, rather than
    // letting a real count launder an approximation.
    const approximate = weighted.some((server) => !server.tokenWeight.isMeasured);
    line.append(countMetric(
      `${approximate ? '~' : ''}${formatTokens(total)}${incomplete ? '+' : ''}`,
      `${incomplete ? 'known tokens' : 'tokens'} · ${share.toFixed(share < 10 ? 1 : 0)}%${incomplete ? '+' : ''} context`,
      heavy ? 'context heavy' : 'context',
    ));
  } else if (enabledCount > 0) {
    line.append(countMetric('—', 'token cost not measured', 'context'));
  }
  return line;
}

function countMetric(value, label, kind) {
  const metric = el('span', `count-metric ${kind}`);
  metric.append(el('strong', null, String(value)), el('span', null, label));
  return metric;
}

/// A ten-second stall with no explanation reads as a hang, so the toolbar's own
/// edge carries how far "Check all" has got (§7.3). Before the first server
/// answers there is no ratio to draw, and the bar says "working" instead of
/// claiming a position it does not have.
function progressLine({ index, total }) {
  const track = el('div', total > 0 ? 'progress-line' : 'progress-line indeterminate');
  track.setAttribute('role', 'progressbar');
  track.setAttribute('aria-label', 'Checking servers');
  if (total > 0) {
    track.setAttribute('aria-valuemin', '0');
    track.setAttribute('aria-valuemax', String(total));
    track.setAttribute('aria-valuenow', String(index));
  }
  const fill = el('span', 'progress-fill');
  if (total > 0) fill.style.width = `${Math.round((index / total) * 100)}%`;
  track.append(fill);
  return track;
}

// MARK: - Table

function table(state, clients, servers) {
  // What moved since the last render. Collected before anything is built, so
  // each cell and each dot can be told whether it is the thing that changed —
  // a toggle animates its own cell and leaves the other three hundred alone.
  const flipped = changedIn('cells', cellSnapshot(clients, servers));
  const settled = changedIn('dots', dotSnapshot(servers), 420);
  // The list itself is a different question: it animates when the set of rows
  // changes — first load, a refresh, a filter — and never when a value inside
  // one of them does.
  const added = addedIn('matrix-rows', servers.map((server) => server.id), 320);
  const filtering = changed('matrix-filter', state.filter, 180);

  // The table lives inside a scroll container rather than being a flex child
  // itself. As a flex item it would stretch to fill the window and hand the
  // extra height to the rows, which is the opposite of a dense instrument panel.
  const scroller = el('div', 'table-scroll');
  scroller.dataset.scrollKey = 'matrix';
  const table = el('table', filtering ? 'matrix is-filtering' : 'matrix');
  table.style.setProperty(
    '--matrix-min-width',
    `calc(var(--col-server-min) + var(--col-weight-width) + ${clients.length} * var(--col-client-width))`,
  );

  const head = el('thead');
  const headRow = el('tr');
  const serverHeading = el('th', 'col-server', 'Server');
  serverHeading.scope = 'col';
  const tokenHeading = el('th', 'col-weight', 'Tokens');
  tokenHeading.scope = 'col';
  headRow.append(serverHeading, tokenHeading);
  // Built-ins stay as the compact icon columns people already recognise. A
  // custom source has no product logo and can have several siblings, so its
  // name and scope are part of the header identity rather than hidden in a
  // tooltip.
  for (const client of clients) {
    const idle = client.state !== 'ready';
    const cell = el(
      'th',
      `col-client${idle ? ' idle' : ''}${client.isReadOnly ? ' custom-source' : ''}`,
    );
    cell.scope = 'col';
    if (client.isReadOnly) {
      const identity = el('span', 'matrix-client-identity');
      identity.append(clientIcon(client));
      identity.append(el('span', 'matrix-client-name', client.displayName));
      identity.append(el('span', 'matrix-client-scope', scopeDescription(client)));
      cell.append(identity);
    } else {
      cell.append(clientIcon(client));
    }
    // A client past (or nearly at) its tool cap says so under its icon, because
    // the column is where switching servers off happens. Otherwise the figure
    // waits in the tooltip: a count under every icon is furniture.
    const budget = client.toolBudget;
    if (toolBudgetStrained(budget)) {
      cell.append(el('span', `client-tool-count ${budget.state}`, toolBudgetFigure(budget)));
    }
    cell.title = [
      client.displayName,
      client.isReadOnly ? `${scopeDescription(client)} · read-only` : null,
      clientNote(client),
      budget?.limit ? `${toolBudgetFigure(budget)} tools${budget.state === 'over' ? ' — past the limit' : ''}` : null,
      client.configPathDisplay,
    ]
      .filter(Boolean)
      .join('\n');
    headRow.append(cell);
  }
  head.append(headRow);

  const body = el('tbody');
  servers.forEach((server, index) => {
    const tr = row(state, server, clients, flipped, settled.has(server.id));
    if (added.has(server.id)) {
      tr.classList.add('row-entering');
      tr.style.animationDelay = stagger(index);
    }
    body.append(tr);
  });

  table.append(head, body);
  scroller.append(table);
  return scroller;
}

function row(state, server, clients, flipped, settled) {
  const tr = el('tr');
  tr.dataset.serverId = server.id;

  const heavy =
    (server.tokenWeight?.estimate ?? 0) >= (state.settings?.tokenWarningThreshold ?? 20000) &&
    Object.values(server.enabledIn).includes('enabled');
  if (heavy) tr.classList.add('heavy');

  const nameCell = el('td', 'col-server');
  // The whole name block opens the detail sheet (§7.5).
  nameCell.append(serverOpener(state, server, settled));
  tr.append(nameCell);

  tr.append(weightCell(state, server, heavy));

  for (const client of clients) {
    tr.append(toggleCell(state, server, client, flipped.has(cellKey(server.id, client.id))));
  }

  return tr;
}

/// The number and how big it is. A grey figure in a grey column reads like a
/// footnote; the bar is what makes the column comparable at a glance, which is
/// the entire point of measuring this (§1).
///
/// It fills against the user's own warning threshold rather than the whole
/// context window — at 200k every real server is a sliver, and the question
/// anyone is actually asking is how close this one is to too much.
function weightCell(state, server, heavy) {
  const cell = el('td', 'col-weight');
  const weight = server.tokenWeight;

  if (!weight) {
    cell.append(el('span', 'weight-none', '—'));
    return cell;
  }

  const threshold = state.settings?.tokenWarningThreshold ?? 20_000;
  const fraction = Math.min(1, weight.estimate / threshold);

  const box = el('div', heavy ? 'weight heavy' : 'weight');
  // The tilde is what §7.4 asked for while the number was chars/4. A count from
  // a real vocabulary has earned its absence; a fallback build still shows it.
  box.append(
    el('span', 'weight-value', `${weight.isMeasured ? '' : '~'}${formatTokens(weight.estimate)}`),
  );

  const track = el('span', 'weight-track');
  const fill = el('span', 'weight-fill');
  fill.style.width = `${(fraction * 100).toFixed(1)}%`;
  track.append(fill);
  box.append(track);

  box.title = `${weight.isMeasured ? '' : 'About '}${weight.estimate.toLocaleString()} tokens, ${weight.percentOfContext.toFixed(
    1,
  )}% of a ${formatTokens(weight.referenceContextWindow)} context window, and ${(
    fraction * 100
  ).toFixed(0)}% of your ${formatTokens(threshold)} warning threshold. ${
    weight.isMeasured ? 'Counted with' : 'Approximated as'
  } ${weight.method}.`;

  cell.append(box);
  return cell;
}

function clientNote(client) {
  if (client.isReadOnly) {
    return client.state === 'noConfig'
      ? 'source file missing — Kytto will not recreate it'
      : 'Kytto never changes this file';
  }
  switch (client.state) {
    case 'noConfig':
      return 'no config yet';
    case 'orphanedConfig':
      return 'not installed';
    case 'notInstalled':
      return 'not detected — set its configuration path in Settings if it is installed somewhere unusual';
    default:
      return '';
  }
}

function scopeDescription(client) {
  const scope = client.configurationScope
    ? `${client.configurationScope[0].toUpperCase()}${client.configurationScope.slice(1)}`
    : 'Global';
  return client.scopeLabel ? `${scope} · ${client.scopeLabel}` : scope;
}

// MARK: - Bars and banners

function updateBanner(state) {
  if (state.updateProgress) {
    const bar = el('div', 'notice update-banner');
    bar.setAttribute('role', 'status');
    const progress = state.updateProgress.progress;
    const phase = state.updateProgress.phase;
    const phaseLabel = {
      starting: 'Preparing update…',
      downloading: 'Downloading update…',
      verifying: 'Verifying update…',
      staged: 'Update ready to install.',
      installing: 'Installing update…',
      relaunching: 'Relaunching Kytto…',
    }[phase] ?? 'Updating Kytto…';
    const label = progress == null || phase !== 'downloading'
      ? phaseLabel
      : `${phaseLabel} ${Math.round(progress * 100)}%`;
    const controls = [el('span', 'notice-message', label)];
    if (phase === 'downloading' || phase === 'starting') {
      controls.push(button('Cancel', 'cancel-update', { className: 'button subtle' }));
    }
    bar.append(...controls);
    return bar;
  }
  const update = state.update;
  if (!update || update.status !== 'updateAvailable' || state.updateDismissed) return null;

  const bar = el('div', 'notice update-banner');
  bar.setAttribute('role', 'status');
  bar.append(
    el(
      'span',
      'notice-message',
      `Kytto ${update.latestVersion} is available (you have ${update.currentVersion}).`,
    ),
    button(state.updatePackage ? 'Install update' : 'Download update', state.updatePackage ? 'install-update' : 'download-update', { className: 'button subtle' }),
    button('Download in browser', 'open-update-download', { className: 'button subtle' }),
    button('Later', 'dismiss-update', { className: 'button subtle' }),
  );
  return bar;
}

function notice(current, leaving = false) {
  // A banner appearing pushes the matrix down; sliding in makes that a movement
  // rather than a jump. It slides only when the message is a new one — the same
  // notice sitting there through the next ten renders stays put.
  const fresh = appeared('notice', `${current.kind}:${current.message}`);
  const bar = el('div', `notice ${current.kind}${fresh && !leaving ? ' is-entering' : ''}${leaving ? ' is-leaving' : ''}`);
  bar.setAttribute('role', current.kind === 'info' ? 'status' : 'alert');
  bar.append(el('span', 'notice-message', current.message));
  if (current.undo && !leaving) {
    bar.append(
      button(current.undoBusy ? 'Undoing…' : 'Undo', current.undoBusy ? 'noop' : 'undo-matrix-write', {
        className: current.undoBusy ? 'button subtle busy' : 'button subtle',
      }),
    );
  }
  const dismiss = el('button', 'button subtle', 'Dismiss');
  dismiss.dataset.action = 'dismiss-notice';
  bar.append(dismiss);
  return bar;
}

/// The first matrix write is an informed action rather than a switch that writes
/// before the person knows it is backed by a file operation.
function renderMatrixWriteConfirmation(state) {
  const current = state.sheet;
  const server = state.servers.find((entry) => entry.id === current.serverId);
  const client = state.clients.find((entry) => entry.id === current.clientId);
  if (!server || !client || client.isReadOnly) return null;

  const action = {
    enabled: { sentence: 'turns the server off', label: 'Turn off' },
    disabled: { sentence: 'turns the server on', label: 'Turn on' },
    absent: { sentence: 'adds the server', label: 'Add server' },
  }[current.previousState];
  if (!action) return null;

  const body = [
    el(
      'p',
      null,
      `This action ${action.sentence}: “${server.name}” in ${client.displayName}.`,
    ),
    definitionList([
      ['Server', server.name],
      ['Client', client.displayName],
      ['Configuration', client.configPathDisplay, 'mono selectable'],
    ]),
    el(
      'p',
      null,
      'The client configuration file changes immediately after you confirm. Kytto creates a timestamped backup first.',
    ),
    el('p', 'muted', client.offSwitchSummary),
    el('p', 'field-hint', 'Undo will be offered beside the result after the change succeeds.'),
  ];
  if (current.error) body.push(el('p', 'failure-message', current.error));

  return sheet('Change client configuration?', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    el('span', 'spacer'),
    button(current.busy ? 'Changing…' : action.label, current.busy ? 'noop' : 'confirm-matrix-write', {
      className: current.busy ? 'button primary busy' : 'button primary',
    }),
  ]);
}

/// Clients hold config in memory and only pick changes up on restart. There is
/// no way around that, so Kytto states it plainly (§7.1).
function restartBar(restarts, leaving = false) {
  const text = restarts.map((entry) => `${entry.id}:${entry.count}`).join('|');
  const fresh = appeared('restarts', text);
  const bar = el('div', `restart-bar${fresh && !leaving ? ' is-entering' : ''}${leaving ? ' is-leaving' : ''}`);
  bar.setAttribute('role', 'status');
  for (const entry of restarts) {
    const item = el('div', 'restart-item');
    item.append(el(
      'span',
      null,
      `Restart ${entry.displayName} to apply ${entry.count} pending configuration change${entry.count === 1 ? '' : 's'}.`,
    ));
    const acknowledged = button('I restarted', 'acknowledge-restart', { className: 'button subtle' });
    acknowledged.dataset.clientId = entry.id;
    const dismissed = button('Dismiss', 'dismiss-restart', { className: 'button subtle' });
    dismissed.dataset.clientId = entry.id;
    item.append(acknowledged, dismissed);
    bar.append(item);
  }
  if (restarts.length > 1) {
    const all = button('I restarted all', 'acknowledge-restarts', { className: 'button subtle' });
    const dismissAll = button('Dismiss all', 'dismiss-restarts', { className: 'button subtle' });
    bar.append(all, dismissAll);
  }
  return bar;
}

// MARK: - Supporting states

function emptyState(clients) {
  const box = el('div', 'placeholder');
  box.append(el('p', null, 'No MCP servers found in any client configuration.'));
  const detail = clients.length
    ? 'The clients below were detected, but none of them has a server configured yet.'
    : 'No supported clients were detected on this machine.';
  box.append(el('p', 'muted', detail));
  return box;
}

/// Reading five clients off disk takes a moment. Showing the shape of the
/// table for that moment is more honest than a sentence in the middle of an
/// empty window: it says what is about to be here.
function loadingState() {
  const box = el('div', 'loading');
  box.setAttribute('aria-label', 'Reading client configurations…');

  const toolbar = el('div', 'skeleton-toolbar');
  toolbar.append(el('span', 'skeleton skeleton-chip'), el('span', 'skeleton skeleton-chip wide'));
  box.append(toolbar);

  for (let index = 0; index < 7; index += 1) {
    const row = el('div', 'skeleton-row');
    row.style.animationDelay = stagger(index, 40);

    const server = el('div', 'skeleton-server');
    const title = el('div', 'skeleton-title');
    title.append(el('span', 'skeleton skeleton-dot'), el('span', 'skeleton skeleton-name'));
    server.append(title, el('span', 'skeleton skeleton-command'));

    row.append(server, el('span', 'skeleton skeleton-weight'));
    for (let column = 0; column < 5; column += 1) {
      row.append(el('span', 'skeleton skeleton-cell'));
    }
    box.append(row);
  }
  return box;
}

function errorState(error) {
  const box = el('div', 'placeholder error');
  box.append(el('p', null, 'Could not read your configurations.'));
  box.append(el('p', 'muted', error?.message ?? 'Unknown error.'));
  const retry = el('button', 'button', 'Try again');
  retry.dataset.action = 'refresh';
  box.append(retry);
  return box;
}

function diagnostics(items) {
  const box = el('section', 'diagnostics');
  box.append(el('h2', null, `Diagnostics (${items.length})`));
  const list = el('ul');
  for (const item of items) {
    const entry = el('li', `diagnostic ${item.severity}`);
    entry.append(el('span', 'diagnostic-path', item.pathDisplay));
    entry.append(el('span', 'diagnostic-message', item.message));
    list.append(entry);
  }
  box.append(list);
  return box;
}

function footer(state) {
  const bar = el('footer', 'statusbar');
  const latest = state.backups[0];
  bar.append(
    el(
      'span',
      'muted',
      latest
        ? `Last backup ${new Date(latest.takenAt * 1000).toLocaleString()}`
        : 'Every change is backed up before it is written.',
    ),
  );
  if (state.app) {
    bar.append(el('span', 'muted spacer', `Kytto ${state.app.version} (${state.app.build})`));
  }
  if (state.app?.capabilities?.includes('updates')) {
    bar.append(
      button(
        state.updateChecking ? 'Checking for updates…' : 'Check for updates',
        state.updateChecking ? 'noop' : 'check-for-updates',
        { className: state.updateChecking ? 'button subtle busy' : 'button subtle' },
      ),
    );
  }
  return bar;
}
