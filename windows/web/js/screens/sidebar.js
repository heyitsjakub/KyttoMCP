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
  sidebar.append(el('div', 'side-heading', 'Workspace'));
  for (const panel of PANELS) {
    sidebar.append(panelItem(state, panel, selectionChanged));
  }

  const builtInClients = state.clients.filter((client) => !client.isReadOnly);
  const customSources = state.clients.filter((client) => client.isReadOnly);
  if (builtInClients.length > 0) {
    sidebar.append(
      el(
        'div',
        'side-heading',
        builtInClients.length === 5 ? '5 built-in clients' : `${builtInClients.length} built-in clients`,
      ),
    );
    for (const client of builtInClients) {
      sidebar.append(clientItem(state, client, selectionChanged));
    }
  }
  if (customSources.length > 0) {
    const shown = state.settings?.showsCustomSources !== false;
    sidebar.append(customHeading(customSources.length, shown));
    if (shown) {
      for (const client of customSources) {
        sidebar.append(clientItem(state, client, selectionChanged));
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
export function sidebarResizeHandle() {
  const handle = el('div', 'side-resize');
  handle.setAttribute('role', 'separator');
  handle.setAttribute('aria-orientation', 'vertical');
  handle.setAttribute('aria-label', 'Resize sidebar');
  handle.tabIndex = 0;
  handle.dataset.action = 'resize-sidebar';
  return handle;
}

function customHeading(count, shown) {
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
    el('span', 'side-label', 'Custom sources · read-only'),
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
  item.append(el('span', 'side-count', client.serverCount > 0 ? String(client.serverCount) : '—'));
  return item;
}

const scopeDescription = (client) => {
  const scope = client.configurationScope
    ? `${client.configurationScope[0].toUpperCase()}${client.configurationScope.slice(1)}`
    : 'Global';
  return client.scopeLabel ? `${scope} · ${client.scopeLabel}` : scope;
};
