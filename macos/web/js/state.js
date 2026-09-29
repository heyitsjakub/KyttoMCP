// One state object, one render (§3.1).
//
// The DOM is a projection of this object and never a second source of truth.
// Nothing reads a value back out of the DOM; if something needs to be known, it
// lives here.

/** @type {{
 *   phase: 'loading' | 'ready' | 'error',
 *   app: object | null,
 *   clients: object[],
 *   servers: object[],
 *   diagnostics: object[],
 *   pendingRestarts: Record<string, number>,
 *   dismissedRestarts: string[],
 *   gatewayRoutes: object[],
 *   doctorReports: object[],
 *   contractAlerts: object[],
 *   drift: object[],
 *   filter: string,
 *   busyCells: Set<string>,
 *   notice: { kind: 'info' | 'warning' | 'error', message: string } | null,
 *   update: object | null,
 *   updateDismissed: boolean,
 *   checkingForUpdates: boolean,
 *   updateProgress: object | null,
 *   updateStage: object | null,
 *   updateError: string | null,
 *   provenanceBusyServerID: string | null,
 *   panel: null | 'profiles' | 'backups' | 'secrets' | 'library' | 'skills' | 'client',
 *   selectedClient: string | null,
 *   backups: object[],
 *   catalog: object[],
 *   profiles: object[],
 *   sheet: null | object,
 *   leavingSheet: null | object,
 *   leavingNotice: null | object,
 *   leavingRestarts: Record<string, number> | null,
 *   error: { code: string, message: string } | null,
 * }} */
const state = {
  phase: 'loading',
  app: null,
  clients: [],
  servers: [],
  diagnostics: [],
  pendingRestarts: {},
  dismissedRestarts: [],
  gatewayRoutes: [],
  doctorReports: [],
  contractAlerts: [],
  // Servers whose clients disagree about what they run. One entry per server,
  // and only when there is a disagreement — an empty list is the normal case.
  drift: [],
  activity: null,
  filter: '',
  // Cells with a write in flight, keyed `serverId::clientId`. A toggle writes to
  // disk, so the cell must not accept a second click until the first lands.
  busyCells: new Set(),
  notice: null,
  update: null,
  updateDismissed: false,
  checkingForUpdates: false,
  updateProgress: null,
  updateStage: null,
  updateError: null,
  provenanceBusyServerID: null,
  panel: null,
  // Which client the `client` panel is showing. Held separately from `panel` so
  // moving between two clients is a change of subject rather than of screen.
  selectedClient: null,
  backups: [],
  catalog: [],
  profiles: [],
  secrets: [],
  // The native library scan is read-only until an explicit preview is
  // confirmed. Target selections stay in state so re-renders do not choose a
  // different client under the user's pointer.
  libraryPath: '',
  libraryCandidates: [],
  libraryWarnings: [],
  libraryTargets: {},
  libraryPreview: null,
  libraryPreviewCandidateID: null,
  libraryBusy: false,
  importPrompt: null,
  importText: '',
  importTargetIDs: [],
  importPreview: null,
  importBusy: false,
  importError: null,
  skillsWorkspacePath: '',
  skillsEntries: [],
  skillsWarnings: [],
  skillsRoots: [],
  skillsBusy: false,
  // Nil until app.info has answered; the first-run screen waits for it rather
  // than flashing the matrix and replacing it.
  settings: null,
  // Secret id → the real value, present only while the user is looking at it (§6).
  revealedSecrets: {},
  // Servers with a health check running, by id.
  checking: new Set(),
  // { serverName, index, total } while "Check all" is working through the list.
  healthProgress: null,
  // The modal on screen, if any:
  //   { kind: 'detail', serverId }
  //   { kind: 'confirmDelete', serverId }
  //   { kind: 'form', mode: 'create' | 'edit', step, draft, targets, errors, ... }
  sheet: null,
  // A removed surface remains renderable for one short exit animation. It is
  // inert and carries no application meaning; the real state has already moved.
  leavingSheet: null,
  leavingNotice: null,
  leavingRestarts: null,
  error: null,
};

/** A blank server, ready for the manual path. */
export function emptyDraft() {
  return { name: '', transport: 'stdio', command: '', args: [], env: [], url: '' };
}

/** Turns a server from `servers.list` into something the form can edit. */
export function draftFromServer(server) {
  return {
    name: server.name,
    transport: server.transport,
    command: server.command ?? '',
    args: [...server.args],
    // Values are deliberately absent — they never left the native side. An
    // untouched field sends null, which means "keep what is there" (§6).
    env: server.env.map((entry) => ({ key: entry.key, value: null, hasValue: entry.hasValue })),
    url: server.url ?? '',
  };
}

let render = () => {};
const exitSerials = new Map();
const EXIT_HOLD = 190;

export function setRenderer(fn) {
  render = fn;
}

export function getState() {
  return state;
}

/**
 * The only way to change anything. Mutating `state` directly means the screen
 * and the data drift apart, which is exactly how a vanilla UI turns to spaghetti.
 * @param {Partial<typeof state>} changes
 */
export function setState(changes) {
  const previousSheet = state.sheet;
  const previousNotice = state.notice;
  const previousRestarts = state.pendingRestarts;
  Object.assign(state, changes);

  const exits = [];
  if (Object.hasOwn(changes, 'sheet')) {
    if (previousSheet && changes.sheet === null) {
      state.leavingSheet = previousSheet;
      exits.push('leavingSheet');
    } else if (changes.sheet) {
      state.leavingSheet = null;
    }
  }
  if (Object.hasOwn(changes, 'notice')) {
    if (previousNotice && changes.notice === null) {
      state.leavingNotice = previousNotice;
      exits.push('leavingNotice');
    } else if (changes.notice) {
      state.leavingNotice = null;
    }
  }
  if (
    Object.hasOwn(changes, 'pendingRestarts') &&
    Object.values(previousRestarts).some((count) => count > 0) &&
    !Object.values(state.pendingRestarts).some((count) => count > 0)
  ) {
    state.leavingRestarts = previousRestarts;
    exits.push('leavingRestarts');
  } else if (
    Object.hasOwn(changes, 'pendingRestarts') &&
    Object.values(state.pendingRestarts).some((count) => count > 0)
  ) {
    state.leavingRestarts = null;
  }

  render(state);

  if (exits.length > 0) {
    for (const key of exits) {
      const serial = (exitSerials.get(key) ?? 0) + 1;
      exitSerials.set(key, serial);
      window.setTimeout(() => {
        if (serial !== exitSerials.get(key)) return;
        state[key] = null;
        render(state);
      }, EXIT_HOLD);
    }
  }
}

export const cellKey = (serverId, clientId) => `${serverId}::${clientId}`;

export function setCellBusy(serverId, clientId, busy) {
  const next = new Set(state.busyCells);
  if (busy) next.add(cellKey(serverId, clientId));
  else next.delete(cellKey(serverId, clientId));
  setState({ busyCells: next });
}

/** Servers matching the current filter, in display order. */
export function visibleServers() {
  const needle = state.filter.trim().toLowerCase();
  if (!needle) return state.servers;
  return state.servers.filter(
    (server) =>
      server.name.toLowerCase().includes(needle) ||
      server.commandSummary.toLowerCase().includes(needle) ||
      // "Which server gives me create_pull_request?" is a question the health
      // check already has the answer to. Only checked servers can match, which
      // is honest: an unchecked server has no known tools to search.
      (server.health?.tools ?? []).some((tool) => tool.name.toLowerCase().includes(needle)),
  );
}

/** Tools matching the filter on one server, for the row to name what it hit. */
export function matchingTools(server) {
  const needle = state.filter.trim().toLowerCase();
  if (!needle) return [];
  // A row matching on its own name is not also announcing its tools — the
  // subtitle is the command, and replacing it needs a better reason than that
  // one of eleven tools happens to contain the same letters.
  if (server.name.toLowerCase().includes(needle)) return [];
  return (server.health?.tools ?? [])
    .map((tool) => tool.name)
    .filter((name) => name.toLowerCase().includes(needle));
}

/**
 * Every supported client gets a stable column.
 *
 * Hiding a client merely because app detection missed it makes the matrix look
 * incomplete and removes the clearest route to that client's status and path.
 * A client that is genuinely absent remains read-only in `serverRow.js`.
 *
 * The one exception is the read-only sources folded away in the sidebar. That is
 * an explicit choice rather than a guess about what the user has installed, and
 * a column of switches that can never be switched is what it removes.
 */
export function matrixClients() {
  if (state.settings?.showsCustomSources === false) {
    return state.clients.filter((client) => !client.isReadOnly);
  }
  return state.clients;
}

/// The window every token figure is a share of. Native measures against it and
/// reports it per server, so the number is read back rather than restated here.
export function contextWindow() {
  return (
    state.servers.find((server) => server.tokenWeight)?.tokenWeight.referenceContextWindow ??
    200_000
  );
}

/// Context cost of everything switched on somewhere, which is the number that
/// actually matters — a heavy server nobody has enabled costs nothing (§7.1).
export function enabledTokenTotal() {
  return state.servers
    .filter((server) => Object.values(server.enabledIn).includes('enabled'))
    .reduce((total, server) => total + (server.tokenWeight?.estimate ?? 0), 0);
}

/// The same question as `enabledTokenTotal`, asked of one client (§7.8). The
/// matrix can only give the total across all of them, and "what does Claude
/// Desktop cost me" is the version anyone acts on — it is one client's context
/// window that fills up.
export function clientTokenTotal(clientID) {
  return state.servers
    .filter((server) => server.enabledIn[clientID] === 'enabled')
    .reduce((total, server) => total + (server.tokenWeight?.estimate ?? 0), 0);
}

/** Servers this client has a definition for, on or off, in display order. */
export function serversIn(clientID) {
  return state.servers.filter((server) => (server.enabledIn[clientID] ?? 'absent') !== 'absent');
}

/** Servers configured elsewhere but not here — the ones this client could take. */
export function serversNotIn(clientID) {
  return state.servers.filter((server) => (server.enabledIn[clientID] ?? 'absent') === 'absent');
}

/** Clients holding changes they have not picked up yet, with counts. */
export function restartsPending(pending = state.pendingRestarts, dismissed = state.dismissedRestarts) {
  const hidden = new Set(dismissed ?? []);
  return Object.entries(pending)
    .filter(([id, count]) => count > 0 && !hidden.has(id))
    .map(([id, count]) => ({
      id,
      count,
      displayName: state.clients.find((client) => client.id === id)?.displayName ?? id,
    }));
}

/**
 * A client's tool count as a figure: `≥133 / 128`. The `≥` is there when a
 * server switched on in the client has no tool list to count, so the figure is
 * a floor rather than a total — the same honesty the token tiles keep. Null
 * when native sent no budget.
 */
export function toolBudgetFigure(budget) {
  if (!budget) return null;
  const floor = budget.unmeasuredServerIDs.length > 0 ? '≥' : '';
  return `${floor}${budget.toolCount}${budget.limit ? ` / ${budget.limit}` : ''}`;
}

/** Near or over a client's cap — the only budget states that earn colour. */
export const toolBudgetStrained = (budget) => budget?.state === 'near' || budget?.state === 'over';
