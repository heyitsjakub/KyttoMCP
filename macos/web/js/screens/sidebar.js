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
  { id: 'library', label: 'MCP Library', paths: GLYPH.library },
  { id: 'skills', label: 'Skills', paths: GLYPH.skills },
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
  sidebar.setAttribute('aria-label', 'Sections');

  sidebar.append(brand());
  sidebar.append(el('div', 'side-heading', 'Workspace'));
  for (const panel of PANELS) {
    sidebar.append(panelItem(state, panel, selectionChanged));
  }

  if (state.clients.length > 0) {
    const builtIn = state.clients.filter((client) => !client.isReadOnly);
    const custom = state.clients.filter((client) => client.isReadOnly);
    sidebar.append(el('div', 'side-heading', `${builtIn.length} built-in clients`));
    for (const client of builtIn) {
      sidebar.append(clientItem(state, client, selectionChanged));
    }
    if (custom.length > 0) {
      const shown = state.settings?.showsCustomSources !== false;
      sidebar.append(customHeading(custom.length, shown));
      if (shown) {
        for (const client of custom) sidebar.append(clientItem(state, client, selectionChanged));
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

/// The one control that changes the sidebar's geometry rather than the app's
/// state. Project scopes put paths in the sidebar, and no fixed width is right
/// for a path — so the width is the user's, dragged here and remembered as a
/// native setting. Rendered as a sibling of the sidebar (it sits astride the
/// border), and it positions itself against `--sidebar-width` in CSS.
export function sidebarResizeHandle(state) {
  const handle = el('div', 'side-resize');
  handle.dataset.action = 'resize-sidebar';
  handle.setAttribute('role', 'separator');
  handle.setAttribute('aria-orientation', 'vertical');
  handle.setAttribute('aria-label', 'Resize sidebar');
  handle.setAttribute('aria-valuenow', String(state.settings?.sidebarWidth ?? 196));
  handle.tabIndex = 0;
  handle.title = 'Drag to resize the sidebar. Double-click to reset.';
  return handle;
}

/// Settings are a window, not a panel: the shell already owns one and the menu
/// bar's ⌘, opens it. This is the same destination for anyone who looks for it
/// in the app rather than in the menu, so it asks the shell to open it and the
/// selected rail never moves (§3.2 — the page does not learn what a window is).
function settingsItem() {
  const item = el('button', 'side-item side-settings');
  item.dataset.action = 'open-settings';
  item.title = 'Open Settings (⌘,)';
  item.append(glyph(GLYPH.settings));
  item.append(el('span', 'side-label', 'Settings'));
  return item;
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
    case 'library':
      return state.libraryCandidates?.length || null;
    case 'skills':
      return state.skillsEntries?.length || null;
    default:
      return null;
  }
}

/// The one heading that is a control. Read-only sources are the long tail of the
/// sidebar — a project scope per directory Claude Code has been run in — and
/// folding them takes them out of the matrix as well, because a column of
/// switches that cannot be switched is the part that was in the way (§7.8).
function customHeading(count, shown) {
  const heading = el('button', `side-heading side-disclosure${shown ? '' : ' collapsed'}`);
  heading.dataset.action = 'toggle-custom-sources';
  heading.dataset.shown = String(shown);
  heading.setAttribute('aria-expanded', String(shown));
  heading.title = shown
    ? `Hide the ${count} read-only ${count === 1 ? 'source' : 'sources'}, here and in the matrix. They are still read and counted.`
    : `Show the ${count} read-only ${count === 1 ? 'source' : 'sources'} again, here and in the matrix.`;
  heading.append(glyph(GLYPH.disclosure));
  heading.append(el('span', null, 'Custom sources · read-only'));
  heading.append(el('span', 'side-count', String(count)));
  return heading;
}

function clientItem(state, client, selectionChanged) {
  const note = CLIENT_NOTE[client.state];
  const selected = state.panel === 'client' && state.selectedClient === client.id;
  // A button, not a row that happens to be clickable: it goes somewhere, and it
  // has to be reachable by keyboard like the three panels above it.
  const item = el(
    'button',
    `side-item${note ? ' dim' : ''}${selected ? ' selected' : ''}${selected && selectionChanged ? ' selection-entering' : ''}`,
  );
  item.dataset.action = 'select-client';
  item.dataset.clientId = client.id;
  item.setAttribute('aria-current', String(selected));
  const scope = scopeDescription(client);
  // When the row shows the short name, the tooltip leads with the full one.
  item.title = [client.shortName ? client.displayName : null, note, scope, client.isReadOnly ? 'Read-only' : null, client.configPathDisplay]
    .filter(Boolean)
    .join('\n');

  item.append(clientIcon(client));
  // Project scopes all share this icon and the heading above them, so the row
  // spends its width on the part that differs — the path's leaf — rather than
  // on repeating "Claude Code · " until the ellipsis eats the difference.
  item.append(el('span', 'side-label', client.shortName ?? client.displayName));
  item.append(el('span', 'side-count', client.serverCount > 0 ? String(client.serverCount) : '—'));
  return item;
}

function scopeDescription(client) {
  const raw = client.configurationScope ?? 'global';
  const kind = raw[0].toUpperCase() + raw.slice(1);
  return client.scopeLabel ? `${kind}: ${client.scopeLabel}` : kind;
}
