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
import { el, button, formatTokens, sheet, glyph, GLYPH, clientIcon, definitionList } from '../dom.js';
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
import { renderLibrary } from './library.js';
import { renderSkills } from './skills.js';

let renderedSheetSubject = '';
let returnFocus = null;
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
  const focusBeforeRender = focusSnapshot(document.activeElement, root);
  const scrollBeforeRender = scrollSnapshot(root);
  const nextSheetSubject = state.sheet ? sheetSubject(state.sheet) : '';
  if (!renderedSheetSubject && nextSheetSubject) returnFocus = focusBeforeRender;
  const closingSheet = Boolean(renderedSheetSubject && !nextSheetSubject);
  root.replaceChildren();

  if (state.phase === 'loading') {
    root.append(loadingState());
    renderedSheetSubject = nextSheetSubject;
    return;
  }

  if (state.phase === 'error') {
    root.append(errorState(state.error));
    renderedSheetSubject = nextSheetSubject;
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
    renderedSheetSubject = nextSheetSubject;
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

  if (state.update?.status === 'updateAvailable' && !state.updateDismissed) {
    content.append(updateBanner(state.update, state));
  }

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
  } else if (state.panel === 'library') {
    screen = renderLibrary(state);
  } else if (state.panel === 'skills') {
    screen = renderSkills(state);
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

  const restartSource = Object.values(state.pendingRestarts).some((count) => count > 0)
    ? state.pendingRestarts
    : state.leavingRestarts;
  const restarts = restartSource ? restartsPending(restartSource, state.dismissedRestarts) : [];
  if (restarts.length > 0) content.append(restartBar(restarts, Boolean(state.leavingRestarts && !Object.values(state.pendingRestarts).some((count) => count > 0))));

  content.append(footer(state));
  body.append(content);
  root.append(body);

  const modal = renderSheet(state);
  if (modal) root.append(modal);

  // State changes rebuild the complete tree. Put every scrollable pane back
  // before restoring focus, otherwise clicking a switch below the fold can
  // jump the matrix (and a horizontally scrolled client column) to an edge.
  restoreScroll(scrollBeforeRender, root);

  if (closingSheet) {
    restoreFocus(returnFocus, root);
    returnFocus = null;
  } else if (nextSheetSubject && nextSheetSubject !== renderedSheetSubject) {
    const initial = modal?.querySelector(
      '.sheet-body input:not([disabled]), .sheet-body select:not([disabled]), .sheet-body textarea:not([disabled]), .sheet-body button:not([disabled]), .sheet-footer button:not([disabled]), .sheet-header button:not([disabled])',
    );
    initial?.focus();
  } else {
    restoreFocus(focusBeforeRender, root);
  }
  renderedSheetSubject = nextSheetSubject;
}

function focusSnapshot(element, root) {
  if (!(element instanceof HTMLElement) || !root.contains(element)) return null;
  const dataset = { ...element.dataset };
  return {
    dataset,
    selectionStart: 'selectionStart' in element ? element.selectionStart : null,
    selectionEnd: 'selectionEnd' in element ? element.selectionEnd : null,
  };
}

function restoreFocus(snapshot, root) {
  if (!snapshot?.dataset?.action) return;
  const candidates = root.querySelectorAll(`[data-action="${snapshot.dataset.action}"]`);
  const element = [...candidates].find((candidate) =>
    Object.entries(snapshot.dataset).every(([key, value]) => candidate.dataset[key] === value),
  );
  if (!(element instanceof HTMLElement)) return;
  element.focus({ preventScroll: true });
  if (snapshot.selectionStart != null && typeof element.setSelectionRange === 'function') {
    element.setSelectionRange(snapshot.selectionStart, snapshot.selectionEnd);
  }
}

function scrollSnapshot(root) {
  return [...root.querySelectorAll('.sidebar, .table-scroll')].map((element, index) => ({
    index,
    scrollTop: element.scrollTop,
    scrollLeft: element.scrollLeft,
  }));
}

function restoreScroll(snapshot, root) {
  const scrollers = [...root.querySelectorAll('.sidebar, .table-scroll')];
  for (const saved of snapshot) {
    const element = scrollers[saved.index];
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
  if (modal && state.leavingSheet && !state.sheet) modal.classList.add('is-leaving');
  if (modal && state.leavingSheet && !state.sheet) {
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
    case 'confirmToggle':
      return renderToggleConfirm(state);
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
    default:
      return null;
  }
}

function renderToggleConfirm(state) {
  const current = state.sheet;
  const server = state.servers.find((entry) => entry.id === current.serverId);
  const client = state.clients.find((entry) => entry.id === current.clientId);
  if (!server || !client) return null;

  const value = server.enabledIn[client.id] ?? 'absent';
  const action = value === 'enabled' ? 'Turn off' : value === 'absent' ? 'Add to client' : 'Turn on';
  const body = [
    el('p', null, `${action} “${server.name}” in ${client.displayName}?`),
    el(
      'p',
      'muted',
      'This changes the client configuration on disk immediately. Kytto creates a timestamped backup first and offers Undo after the change.',
    ),
    definitionList([
      ['Config file', client.configPathDisplay],
      ['What switching off does', client.offSwitchSummary],
    ]),
  ];
  if (current.error) body.push(el('p', 'failure-message', current.error));

  return sheet('Change client configuration?', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    button(
      current.busy ? 'Changing…' : action,
      current.busy ? 'noop' : 'confirm-toggle',
      {
        className: current.busy ? 'button primary busy' : 'button primary',
        dataset: {
          serverId: server.id,
          clientId: client.id,
          enabled: String(current.currentlyEnabled),
        },
      },
    ),
  ]);
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

    actions.append(
      button('Add server', 'add-server', { className: 'button primary' }),
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

function counts(state) {
  const line = el('div', 'counts');
  const enabledCount = state.servers.filter((server) =>
    Object.values(server.enabledIn).includes('enabled'),
  ).length;
  line.append(countMetric(state.servers.length, 'servers', 'primary'));
  line.append(countMetric(enabledCount, 'active', 'active'));

  // The headline number: what the enabled servers cost in context (§7.1). The
  // share of the window is the part anyone actually reasons about, so it is
  // written out rather than left in a tooltip — this strip cannot be hovered.
  const total = enabledTokenTotal();
  if (total > 0) {
    const enabled = state.servers.filter((server) =>
      Object.values(server.enabledIn).includes('enabled'),
    );
    const known = enabled.filter((server) => server.tokenWeight);
    const share = (total / contextWindow()) * 100;
    const heavy = total >= (state.settings?.tokenWarningThreshold ?? 20_000);
    // A sum is only as measured as its least measured term. One server still
    // carrying a chars/4 figure keeps the tilde on the whole total, rather than
    // letting a real count launder an approximation.
    const approximate = enabled.some(
      (server) =>
        server.tokenWeight &&
        !server.tokenWeight.isMeasured,
    );
    const incomplete = known.length < enabled.length;
    line.append(countMetric(
      `${incomplete ? '≥' : approximate ? '~' : ''}${formatTokens(total)}`,
      `${incomplete ? 'known tokens minimum' : 'tokens'} · ${share.toFixed(share < 10 ? 1 : 0)}% context`,
      heavy ? 'context heavy' : 'context',
    ));
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
  const table = el('table', filtering ? 'matrix is-filtering' : 'matrix');
  // A fixed table layout hands the leftover width to the one auto column, and
  // with a long tail of read-only project scopes there is no leftover — the
  // server column collapses to nothing and the names land on top of the token
  // figures. The table is told how wide its own columns are so it scrolls
  // sideways instead of eating the column the matrix is read by.
  table.style.setProperty(
    '--matrix-min-width',
    `calc(var(--col-server-min) + var(--col-weight-width) + ${clients.length} * var(--col-client-width))`,
  );

  const head = el('thead');
  const headRow = el('tr');
  headRow.append(el('th', 'col-server', 'Server'));
  headRow.append(el('th', 'col-weight', 'Tokens'));
  // An icon, not two wrapped words. The name, the count and the reason a client
  // is not participating are all in the sidebar, which has the width for them.
  for (const client of clients) {
    const idle = client.state !== 'ready';
    const cell = el('th', `col-client${idle ? ' idle' : ''}`);
    cell.append(clientIcon(client));
    const scopeKind = client.configurationScope ?? 'global';
    const scope = client.scopeLabel ? `${scopeKind}: ${client.scopeLabel}` : scopeKind;
    // A client past (or nearly at) its tool cap says so under its icon, because
    // the column is where switching servers off happens. Otherwise the figure
    // waits in the tooltip: a count under every icon is furniture.
    const budget = client.toolBudget;
    const strained = toolBudgetStrained(budget);
    if (strained) {
      cell.append(el('span', `client-tool-count ${budget.state}`, toolBudgetFigure(budget)));
    }
    cell.title = [
      client.displayName,
      scope,
      client.isReadOnly ? 'read-only custom source' : null,
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
  switch (client.state) {
    case 'noConfig':
      return 'no config yet';
    case 'orphanedConfig':
      return 'not installed';
    default:
      return '';
  }
}

// MARK: - Bars and banners

function notice(current, leaving = false) {
  // A banner appearing pushes the matrix down; sliding in makes that a movement
  // rather than a jump. It slides only when the message is a new one — the same
  // notice sitting there through the next ten renders stays put.
  const fresh = appeared('notice', `${current.kind}:${current.message}`);
  const bar = el('div', `notice ${current.kind}${fresh && !leaving ? ' is-entering' : ''}${leaving ? ' is-leaving' : ''}`);
  bar.append(el('span', 'notice-message', current.message));
  if (current.action) {
    const action = el('button', 'button subtle', current.action.label);
    action.dataset.action = current.action.action;
    action.dataset.serverId = current.action.serverId;
    action.dataset.clientId = current.action.clientId;
    action.dataset.previousState = current.action.previousState;
    if (current.action.backupId) action.dataset.backupId = current.action.backupId;
    bar.append(action);
  }
  const dismiss = el('button', 'button subtle', 'Dismiss');
  dismiss.dataset.action = 'dismiss-notice';
  bar.append(dismiss);
  return bar;
}

function updateBanner(update, state) {
  const fresh = appeared('update', update.latestVersion);
  const bar = el('div', `update-banner${fresh ? ' is-entering' : ''}`);
  const copy = el('div', 'update-banner-copy');
  const progress = state.updateProgress;
  const stage = state.updateStage;
  if (progress) {
    copy.append(
      el('strong', null,
        progress.phase === 'verifying'
          ? 'Verifying update'
          : progress.phase === 'installing' || progress.phase === 'relaunching'
            ? 'Installing update'
            : 'Downloading update'),
      el('span', null, progress.message),
    );
    const track = el('div', 'update-progress');
    const fill = el('span', 'update-progress-fill');
    if (progress.fraction != null) fill.style.width = `${Math.round(progress.fraction * 100)}%`;
    track.append(fill);
    copy.append(track);
    bar.append(copy);
    if (progress.phase === 'downloading' || progress.phase === 'verifying') {
      const cancel = el('button', 'button subtle', 'Cancel');
      cancel.dataset.action = 'cancel-update-download';
      bar.append(cancel);
    }
    return bar;
  }

  if (stage) {
    copy.append(
      el('strong', null, `Kytto ${stage.version} is ready to install`),
      el('span', null, 'The installer checksum is verified. Installing replaces only the app and then relaunches it.'),
    );
    const install = el('button', 'button primary', 'Install and relaunch');
    install.dataset.action = 'install-update';
    const later = el('button', 'button subtle', 'Later');
    later.dataset.action = 'dismiss-update';
    bar.append(copy, install, later);
    return bar;
  }

  copy.append(
    el('strong', null, `Kytto ${update.latestVersion} is available`),
    el('span', null, `You are using ${update.currentVersion}. Download and verify the official installer here.`),
  );
  bar.append(copy);

  const download = el('button', 'button primary', 'Download and verify');
  download.dataset.action = 'download-update';
  const later = el('button', 'button subtle', 'Later');
  later.dataset.action = 'dismiss-update';
  bar.append(download, later);
  return bar;
}

/// Clients hold config in memory and only pick changes up on restart. There is
/// no way around that, so Kytto states it plainly (§7.1).
function restartBar(restarts, leaving = false) {
  const text = restarts
    .map((entry) => `${entry.displayName}:${entry.count}`)
    .join('|');
  const fresh = appeared('restarts', text);
  const bar = el('div', `restart-bar${fresh && !leaving ? ' is-entering' : ''}${leaving ? ' is-leaving' : ''}`);
  const list = el('div', 'restart-list');
  for (const entry of restarts) {
    const row = el('div', 'restart-item');
    const count = `${entry.count} pending configuration change${entry.count === 1 ? '' : 's'}`;
    row.append(el('span', null, `Restart ${entry.displayName} to apply ${count}.`));
    const restarted = el('button', 'button subtle', 'I restarted');
    restarted.dataset.action = 'acknowledge-restart';
    restarted.dataset.clientId = entry.id;
    row.append(restarted);
    list.append(row);
  }
  const dismiss = el('button', 'button subtle', 'Dismiss');
  dismiss.dataset.action = 'dismiss-restarts';
  bar.append(list, dismiss);
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

/// Reading four config files off disk takes a moment. Showing the shape of the
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
    for (let column = 0; column < 4; column += 1) {
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
  retry.dataset.action = 'retry-load';
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
    if (state.app.capabilities.includes('updates')) {
      const check = el(
        'button',
        'status-link spacer',
        state.checkingForUpdates ? 'Checking for updates…' : 'Check for updates',
      );
      check.dataset.action = state.checkingForUpdates ? 'noop' : 'check-for-updates';
      check.disabled = state.checkingForUpdates;
      bar.append(check);
    }
    bar.append(el('span', 'muted', `Kytto ${state.app.version} (${state.app.build})`));
  }
  return bar;
}
