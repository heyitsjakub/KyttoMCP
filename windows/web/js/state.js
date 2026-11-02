// One state object, one render (§3.1).
//
// The DOM is a projection of this object and never a second source of truth.
// Nothing reads a value back out of the DOM; if something needs to be known, it
// lives here.

/** @type {{
 *   phase: 'loading' | 'ready' | 'error',
 *   app: object | null,
 *   update: object | null,
 *   updateDismissed: boolean,
 *   updateChecking: boolean,
 *   updateProgress: object | null,
 *   updatePackage: object | null,
 *   provenanceChecking: Set<string>,
 *   packageSafetyBusyServerID: string | null,
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
 *   scopeFilter: string,
 *   sourceFilter: 'all' | 'config' | 'plugin' | 'custom',
 *   density: 'comfortable' | 'compact',
 *   selectedServerId: string | null,
 *   openTools: Set<string>,
 *   busyCells: Set<string>,
 *   notice: {
 *     kind: 'info' | 'warning' | 'error',
 *     message: string,
 *     undo?: { previousState: string, clientId: string, serverId: string, backupId: string | null },
 *     undoBusy?: boolean,
 *   } | null,
 *   acknowledgingContract: string | null,
 *   restoringBackupId: string | null,
 *   panel: null | 'profiles' | 'backups' | 'secrets' | 'client',
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
  // The last native updater reply. The dismissal flag is deliberately separate:
  // Later lasts for this UI session only and never changes the verified cache.
  update: null,
  updateDismissed: false,
  updateChecking: false,
  updateProgress: null,
  updatePackage: null,
  provenanceChecking: new Set(),
  packageSafetyBusyServerID: null,
  clients: [],
  servers: [],
  diagnostics: [],
  pendingRestarts: {},
  dismissedRestarts: [],
  gatewayRoutes: [],
  doctorReports: [],
  contractAlerts: [],
  // Imported contract fingerprints awaiting a check made here, and a review (§7.13).
  pendingContractImports: { entries: [], error: null },
  // Servers whose clients disagree about what they run. One entry per server,
  // and only when there is a disagreement — an empty list is the normal case.
  drift: [],
  activity: null,
  // Export or import choosing a file in a native dialog; the buttons wait meanwhile.
  setupBusy: false,
  filter: '',
  // Matrix with detail (§7.1.1). Three filters compose: the text above, a scope
  // ('all' | 'global' | a read-only client id naming one workspace/profile/custom
  // source) and a source type. They narrow rows and columns; they never change
  // what is on disk.
  scopeFilter: 'all',
  sourceFilter: 'all',
  // 'comfortable' | 'compact'. Per session: it is a reading preference, not a
  // setting that native has any use for.
  density: 'comfortable',
  // The server the persistent inspector shows (§7.1.2). Its own piece of state,
  // deliberately not a sheet: a refresh keeps it, a removed or filtered-out
  // subject clears it (see `reconcile` in `setState`).
  selectedServerId: null,
  // Tool rows expanded in a server's detail, keyed `serverId::toolName`, so a
  // re-render does not fold them shut — in the inspector or in the modal.
  openTools: new Set(),
  // Cells with a write in flight, keyed `serverId::clientId`. A toggle writes to
  // disk, so the cell must not accept a second click until the first lands.
  busyCells: new Set(),
  notice: null,
  // The server whose contract alert is being marked as reviewed.
  acknowledgingContract: null,
  // The backup being reverted. Every Revert waits for it: two reverts racing on
  // one file leave whichever lands last.
  restoringBackupId: null,
  panel: null,
  // Which client the `client` panel is showing. Held separately from `panel` so
  // moving between two clients is a change of subject rather than of screen.
  selectedClient: null,
  backups: [],
  catalog: [],
  profiles: [],
  secrets: [],
  // Null until settings.get has answered; the first-run screen waits for it rather
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
    // `originalKey` lets a renamed key carry its kept value along.
    env: server.env.map((entry) => ({ key: entry.key, value: null, hasValue: entry.hasValue, originalKey: entry.key })),
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
  reconcile(changes);

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

// Filters and the inspector refer to things native owns. When a refresh takes the
// referent away they fall back to something that exists instead of leaving the
// screen pointing at nothing (§7.1.1, §7.1.2).
const FILTER_INPUTS = ['clients', 'servers', 'filter', 'scopeFilter', 'sourceFilter', 'settings'];

function reconcile(changes) {
  if (
    Object.hasOwn(changes, 'clients') &&
    !['all', 'global'].includes(state.scopeFilter) &&
    !state.clients.some((client) => client.id === state.scopeFilter)
  ) {
    state.scopeFilter = 'all';
  }

  // Removed (the server is gone) or hidden (a filter no longer matches it): either
  // way the inspector must not keep describing a row nobody can see.
  if (
    state.selectedServerId &&
    (Object.hasOwn(changes, 'selectedServerId') || FILTER_INPUTS.some((key) => Object.hasOwn(changes, key))) &&
    !visibleServers().some((server) => server.id === state.selectedServerId)
  ) {
    state.selectedServerId = null;
  }
}

/// Records that a tool row was opened or folded. Quiet by design, like the sheet's
/// scroll offset: the `<details>` element already shows the new state, and
/// rendering in response to its own toggle event would replace the element being
/// toggled.
export function setToolOpen(serverId, toolName, open) {
  const key = toolKey(serverId, toolName);
  if (open) state.openTools.add(key);
  else state.openTools.delete(key);
}

export const toolKey = (serverId, toolName) => `${serverId}::${toolName}`;

export const cellKey = (serverId, clientId) => `${serverId}::${clientId}`;

export function setCellBusy(serverId, clientId, busy) {
  const next = new Set(state.busyCells);
  if (busy) next.add(cellKey(serverId, clientId));
  else next.delete(cellKey(serverId, clientId));
  setState({ busyCells: next });
}

/**
 * Where a server came from, as the source filter and the row label need it.
 * Native sends `source.kind`; a payload from before it existed is read from
 * `originKind`, so an older fixture or reply still sorts into the right bucket.
 * @returns {'config' | 'custom' | 'extension' | 'plugin'}
 */
export function sourceKind(server) {
  if (server.source?.kind) return server.source.kind;
  if (server.originKind === 'plugin') return 'plugin';
  return server.originKind === 'extension' ? 'extension' : 'config';
}

/// The source types the filter offers, in the order it offers them. Shared with
/// the tests so the labels have one home.
export const SOURCE_FILTERS = [
  { id: 'all', label: 'All source types' },
  { id: 'config', label: 'Configurations' },
  { id: 'plugin', label: 'Plugins & extensions' },
  { id: 'custom', label: 'Custom & workspace sources' },
];

/// Clients (columns) that hold a definition of this server, switched on or off.
const holdersOf = (server, clients) =>
  clients.filter((client) => (server.enabledIn[client.id] ?? 'absent') !== 'absent');

/**
 * Kytto never edits this server: native says so, or every file that holds it is a
 * read-only source. The second half matters on Windows, where a project scope is
 * read-only by construction and its servers arrive with an ordinary `config`
 * source — the row still has to say "read-only".
 */
export function serverIsReadOnly(server) {
  if (server.source?.readOnly === true) return true;
  const holders = holdersOf(server, state.clients);
  return holders.length > 0 && holders.every((client) => client.isReadOnly);
}

/**
 * Servers matching scope, source type and text, in display order (§7.1.1).
 *
 * Scope narrows by columns first — `matrixClients()` already knows which files
 * the chosen scope covers, global context included — and a server is in scope
 * when any of those columns holds it. Source type then asks what kind of thing
 * the server is; the text filter is applied last so it only ever searches what
 * the other two left.
 */
export function visibleServers() {
  const clients = matrixClients();
  const scoped = state.servers.filter((server) => {
    const holders = holdersOf(server, clients);
    if (state.scopeFilter !== 'all' && holders.length === 0) return false;

    const kind = sourceKind(server);
    if (state.sourceFilter === 'plugin' && !['plugin', 'extension'].includes(kind)) return false;
    if (state.sourceFilter === 'config') {
      if (server.isBundled || kind === 'custom') return false;
      // Held only by read-only workspace or custom files: not a configuration
      // Kytto manages, whatever label native gave the first copy it found.
      if (holders.length > 0 && holders.every((client) => client.isReadOnly)) return false;
    }
    if (state.sourceFilter === 'custom' && !holders.some((client) => client.isReadOnly)) return false;

    // Folding the read-only sources away also folds away rows that exist only
    // because of them; an empty row of switches is not a server you can act on.
    if (state.settings?.showsCustomSources === false && state.scopeFilter === 'all' && holders.length === 0) return false;
    return true;
  });
  const needle = state.filter.trim().toLowerCase();
  if (!needle) return scoped;
  return scoped.filter(
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
 * The columns for the current scope (§7.1.1).
 *
 * Built-ins keep stable columns; explicitly folded read-only sources do not draw —
 * except when the scope names one of them, because choosing a workspace is the
 * stronger statement than folding the sidebar.
 *
 * A workspace shows its own source plus the writable client that owns it, which
 * is the "applicable global and inherited context": the project's file is only
 * half of what that client reads. Native supplies the owner through the shared
 * icon identity — nothing here parses a path or guesses a client from a name.
 */
export function matrixClients() {
  const scope = state.clients.find((client) => client.id === state.scopeFilter);
  if (scope) {
    return state.clients.filter(
      (client) =>
        client.id === scope.id || (!client.isReadOnly && client.iconAsset === scope.iconAsset),
    );
  }
  if (state.scopeFilter === 'global') {
    return state.clients.filter(
      (client) =>
        (client.configurationScope ?? 'global') === 'global' &&
        (state.settings?.showsCustomSources !== false || !client.isReadOnly),
    );
  }
  return state.settings?.showsCustomSources === false
    ? state.clients.filter((client) => !client.isReadOnly)
    : state.clients;
}

/// What the Scope filter offers: everything, the global configurations, then one
/// entry per read-only source or non-global scope native reported.
const SCOPE_KIND = { workspace: 'Workspace', profile: 'Profile' };

export function scopeOptions(from = state) {
  return [
    { id: 'all', label: 'All sources' },
    { id: 'global', label: 'Global configurations' },
    ...from.clients
      .filter((client) => client.isReadOnly || (client.configurationScope ?? 'global') !== 'global')
      .map((client) => ({
        id: client.id,
        label: `${SCOPE_KIND[client.configurationScope] ?? 'Custom'} · ${client.displayName}`,
      })),
  ];
}

/** Clients native has declared safe write destinations. */
export function writableClients() {
  return state.clients.filter((client) => !client.isReadOnly);
}

/** The first writable cell click explains the disk operation before it can run. */
export function needsMatrixWriteConfirmation(settings, client) {
  return !client?.isReadOnly && settings?.hasConfirmedMatrixWrites !== true;
}

/** The safe native command that restores one captured pre-write matrix state. */
export function matrixUndoPlan(undo) {
  // Adding an absent server has no inverse toggle: switching it off would leave a
  // parked or disabled definition. Remove just that definition instead. Restoring
  // the whole pre-write backup would also undo everything the client or the user
  // changed in that file since — a notice can be clicked hours later.
  if (undo.previousState === 'absent') {
    return {
      command: 'servers.removeFromClient',
      payload: { serverID: undo.serverId, clientID: undo.clientId },
    };
  }
  return {
    command: 'servers.setEnabled',
    payload: {
      serverID: undo.serverId,
      clientID: undo.clientId,
      enabled: undo.previousState === 'enabled',
    },
  };
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

/** Client ids as the names the user knows them by: "Claude Code, Cursor". */
export function clientNames(from, clientIDs) {
  return clientIDs
    .map((id) => from.clients.find((client) => client.id === id)?.displayName ?? id)
    .join(', ');
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
export function restartsPending(pending = state.pendingRestarts) {
  return Object.entries(pending)
    .filter(([, count]) => count > 0)
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
