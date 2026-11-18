// Where you are, and what this machine has.
//
// Moving between the matrix, secrets and backups used to be three of the five
// identical buttons in the toolbar, which made changing screen look like taking
// an action. Here it is a place you are in, and the toolbar goes back to being
// about the matrix.
//
// The client list below it is the other half: the matrix can only afford an icon
// per column, so this is where a client gets its name, its server count and the
// reason it is not participating. Each one is also the way into that client's own
// screen (§7.8), which is where a column becomes a place with a file behind it.

import { el, glyph, GLYPH, clientIcon } from '../dom.js';

const PANELS = [
  { id: null, label: 'Matrix', paths: GLYPH.matrix },
  { id: 'doctor', label: 'MCP Doctor', paths: GLYPH.warning },
  { id: 'activity', label: 'Activity', paths: GLYPH.activity },
  { id: 'profiles', label: 'Profiles', paths: GLYPH.profiles },
  { id: 'secrets', label: 'Secrets', paths: GLYPH.secrets },
  { id: 'backups', label: 'Backups', paths: GLYPH.backups },
];

/// Three groups instead of one flat list (§7.1.1). Every id here is a place you
/// stand in — the Discover tools are not, see `DISCOVER` below.
const PANEL_GROUPS = [
  { label: 'Overview', ids: [null, 'doctor', 'activity'] },
  { label: 'Manage', ids: ['profiles', 'secrets', 'backups'] },
];

/// MCP Library and Skills are windows over the matrix on Windows rather than
/// panels, as they have always been: they open a sheet and give the focus back to
/// the item that asked. So they live in the sidebar — under Discover, where the
/// macOS build also puts them — but they are actions, never "selected", and each
/// still appears only when native reports the capability.
const DISCOVER = [
  { capability: 'library', action: 'open-library', label: 'MCP Library', paths: GLYPH.library },
  { capability: 'skills', action: 'open-skills', label: 'Skills', paths: GLYPH.skills },
];

/// A client that is installed and configured says how many servers it holds.
/// The other three states are more useful as words than as a zero.
const CLIENT_NOTE = {
  ready: null,
  noConfig: 'Installed, but MCP has never been configured in it.',
  orphanedConfig: 'Not installed, but a configuration for it is still here.',
  notInstalled: 'Not installed.',
};

export function renderSidebar(state, selectionChanged = false) {
  const sidebar = el('nav', 'sidebar');
  sidebar.dataset.scrollKey = 'sidebar';
  sidebar.setAttribute('aria-label', 'Sections');

  sidebar.append(brand());
  for (const group of PANEL_GROUPS) {
    sidebar.append(el('div', 'side-heading', group.label));
    for (const panel of PANELS.filter((entry) => group.ids.includes(entry.id))) {
      sidebar.append(panelItem(state, panel, selectionChanged));
    }
  }

  const discover = DISCOVER.filter((entry) => hasCapability(state, entry.capability));
  if (discover.length > 0) {
    sidebar.append(el('div', 'side-heading', 'Discover'));
    for (const entry of discover) sidebar.append(discoverItem(entry));
  }

  const builtInClients = state.clients.filter((client) => !client.isReadOnly);
  const customSources = state.clients.filter((client) => client.isReadOnly);
  if (state.clients.length > 0) {
    sidebar.append(el('div', 'side-heading', 'Clients & sources'));
    const shown = state.settings?.showsCustomSources !== false;
    // A read-only source that shares its icon identity with a built-in client is
    // that client's workspace (a Claude Code project scope, say), so it nests
    // under it; native decides the identity, the page never parses a path.
    const nested = new Set();
    for (const client of builtInClients) {
      sidebar.append(clientItem(state, client, selectionChanged));
      const children = customSources.filter((source) => source.iconAsset === client.iconAsset);
      for (const child of children) nested.add(child.id);
      if (!shown) continue;
      for (const child of children) {
        const item = clientItem(state, child, selectionChanged);
        item.classList.add('side-child');
        sidebar.append(item);
      }
    }
    // Everything else — explicitly attached files with no owning client — keeps
    // the disclosure that folds them away, as before.
    if (customSources.length > 0) {
      // Grouped sources sit under their client, so with none left over the heading
      // only offers the show/hide switch, and says so.
      sidebar.append(customHeading(customSources.length, shown, customSources.some((source) => !nested.has(source.id))));
      if (shown) {
        for (const client of customSources.filter((source) => !nested.has(source.id))) {
          sidebar.append(clientItem(state, client, selectionChanged));
        }
      }
    }
  }

  // The bottom of the sidebar is where the app talks about itself rather than
  // about a client: diagnostics are visible from every screen, not just the one
  // that lists them out, and settings live under them because they are the one
  // destination that is a window rather than a panel.
  const foot = el('div', 'side-foot');
  if (state.diagnostics.length > 0) {
    const line = el('div', 'side-warning');
    line.append(glyph(GLYPH.warning));
    line.append(
      el(
        'span',
        null,
        `${state.diagnostics.length} ${state.diagnostics.length === 1 ? 'diagnostic' : 'diagnostics'}`,
      ),
    );
    foot.append(line);
  }
  foot.append(settingsItem());
  sidebar.append(foot);

  return sidebar;
}

/// Settings are a window, not a panel: the shell already owns one and its own
/// keyboard shortcut opens it. This is the same destination for anyone who
/// looks for it in the app rather than in the chrome, so it asks the shell to
/// open it and the selected rail never moves (§3.2 — the page does not learn
/// what a window is, nor which keystroke this platform spells it with).
function settingsItem() {
  const item = el('button', 'side-item side-settings');
  item.dataset.action = 'open-settings';
  item.title = 'Open Settings';
  item.append(glyph(GLYPH.settings));
  item.append(el('span', 'side-label', 'Settings'));
  return item;
}

/// The resize handle is a sibling of the sidebar so moving it never rebuilds or
/// reads layout state from the navigation tree.
export function sidebarResizeHandle(state) {
  const handle = el('div', 'side-resize');
  handle.setAttribute('role', 'separator');
  handle.setAttribute('aria-orientation', 'vertical');
  handle.setAttribute('aria-label', 'Resize sidebar');
  handle.tabIndex = 0;
  handle.dataset.action = 'resize-sidebar';
  handle.title = 'Drag to resize the sidebar. Double-click to reset.';
  return handle;
}

const hasCapability = (state, capability) =>
  (state.app?.capabilities ?? []).includes(capability);

/// A tool that opens a sheet. Reachable by keyboard like every other row, and
/// never `aria-current`: the rail marks where you are, and these are not places.
function discoverItem({ action, label, paths }) {
  const item = el('button', 'side-item side-discover');
  item.type = 'button';
  item.dataset.action = action;
  item.setAttribute('aria-haspopup', 'dialog');
  item.append(glyph(paths));
  item.append(el('span', 'side-label', label));
  return item;
}

function customHeading(count, shown, hasUngrouped = true) {
  const heading = el(
    'button',
    `side-heading side-disclosure${shown ? '' : ' collapsed'}`,
  );
  heading.type = 'button';
  heading.dataset.action = 'toggle-custom-sources';
  heading.dataset.shown = String(shown);
  heading.setAttribute('aria-expanded', String(shown));
  heading.append(
    glyph(GLYPH.disclosure),
    el('span', 'side-label', hasUngrouped ? 'Custom sources' : shown ? 'Hide read-only sources' : 'Show read-only sources'),
    el('span', 'side-count', String(count)),
  );
  return heading;
}

/// A compact identity rather than a decorative logo. The nine cells echo the
/// app icon and the matrix itself, while staying neutral so green remains a
/// server-state colour (§10).
function brand() {
  const header = el('div', 'side-brand');
  const mark = el('span', 'kytto-mark');
  mark.setAttribute('aria-hidden', 'true');
  for (let index = 0; index < 9; index += 1) {
    mark.append(el('span', [0, 4, 8].includes(index) ? 'active' : null));
  }

  const copy = el('span', 'brand-copy');
  copy.append(el('strong', null, 'Kytto'), el('span', null, 'MCP Control'));
  header.append(mark, copy);
  return header;
}

function panelItem(state, panel, selectionChanged) {
  const selected = state.panel === panel.id;
  const item = el('button', `side-item${selected ? ' selected' : ''}${selected && selectionChanged ? ' selection-entering' : ''}`);
  item.dataset.action = 'select-panel';
  // Absent rather than empty for the matrix, so main.js reads it back as null
  // instead of as the string "null".
  if (panel.id) item.dataset.panel = panel.id;
  item.setAttribute('aria-current', String(selected));

  item.append(glyph(panel.paths));
  item.append(el('span', 'side-label', panel.label));

  const count = panelCount(state, panel.id);
  if (count !== null) item.append(el('span', 'side-count', String(count)));
  return item;
}

/// Only where the number is already in state. Secrets are read when that panel
/// is opened, and a count that says 0 until you look at it is worse than none.
function panelCount(state, panelID) {
  switch (panelID) {
    case null:
      return state.servers.length;
    case 'backups':
      return state.backups.length || null;
    case 'profiles':
      return state.profiles.length || null;
    case 'doctor':
      return (state.doctorReports ?? []).reduce((total, report) => total + report.findings.length, 0) || null;
    case 'activity':
      return state.activity?.failedCalls || null;
    default:
      return null;
  }
}

function clientItem(state, client, selectionChanged) {
  const note = client.isReadOnly && client.state === 'noConfig'
    ? 'The selected source file is missing. Kytto will not recreate it.'
    : CLIENT_NOTE[client.state];
  const selected = state.panel === 'client' && state.selectedClient === client.id;
  // A button, not a row that happens to be clickable: it goes somewhere, and it
  // has to be reachable by keyboard like the panels above it.
  const item = el(
    'button',
    `side-item${note ? ' dim' : ''}${selected ? ' selected' : ''}${selected && selectionChanged ? ' selection-entering' : ''}`,
  );
  item.dataset.action = 'select-client';
  item.dataset.clientId = client.id;
  item.setAttribute('aria-current', String(selected));
  item.title = client.isReadOnly
    ? [client.displayName, scopeDescription(client), 'Read-only. Kytto never changes this file.', note, client.configPathDisplay]
        .filter(Boolean)
        .join('\n')
    : note
      ? `${note}\n${client.configPathDisplay}`
      : client.configPathDisplay;

  item.append(clientIcon(client));
  item.append(el('span', 'side-label', client.shortName ?? client.displayName));
  if (client.isReadOnly) {
    // The state is words for assistive technology and a padlock for everyone
    // else: read-only is a fact about the row, not a colour.
    const lock = glyph(['M5 7V5a3 3 0 0 1 6 0v2', 'M3.5 7h9v7h-9z']);
    lock.classList.add('source-lock');
    item.append(lock, el('span', 'visually-hidden', 'Read-only'));
  }
  item.append(el('span', 'side-count', client.serverCount > 0 ? String(client.serverCount) : '—'));
  return item;
}

const scopeDescription = (client) => {
  const scope = client.configurationScope
    ? `${client.configurationScope[0].toUpperCase()}${client.configurationScope.slice(1)}`
    : 'Global';
  return client.scopeLabel ? `${scope} · ${client.scopeLabel}` : scope;
};
