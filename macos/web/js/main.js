// Bootstrap: load state from native, render, and route every interaction
// through one delegated listener (§3.1).

import { invoke, on, isStubbed } from './ipc.js';
import {
  setState,
  setRenderer,
  getState,
  setCellBusy,
  emptyDraft,
  draftFromServer,
} from './state.js';
import { render as renderMatrix } from './screens/matrix.js';
import { diagnosticReport } from './screens/serverDetail.js';
import { activityReport } from './screens/activity.js';
import { renderLibrary } from './screens/library.js';
import { renderSkills } from './screens/skills.js';

const root = document.getElementById('app');

setRenderer(() => renderMatrix(root));

async function load() {
  setState({ phase: 'loading', error: null });
  try {
    const [app, settings, state, backups, catalog, profiles] = await Promise.all([
      invoke('app.info'),
      invoke('settings.get'),
      invoke('state.get'),
      invoke('backups.list', { clientID: null }),
      invoke('catalog.list'),
      invoke('profiles.list'),
    ]);
    applyTheme(settings.theme);
    applySidebarWidth(settings.sidebarWidth);
    setState({ phase: 'ready', app, settings, backups, catalog, profiles, ...state });
    if (app.capabilities.includes('updates')) void checkForUpdates(false);
  } catch (error) {
    setState({
      phase: 'error',
      error: { code: error.code ?? 'unknown', message: error.message },
    });
  }
}

async function checkForUpdates(force) {
  if (force) setState({ checkingForUpdates: true });
  try {
    const update = await invoke('updates.check', { force });
    const staged = getState().updateStage;
    const keepStage = staged && update.status === 'updateAvailable' && staged.version === update.latestVersion;
    setState({
      update,
      updateDismissed: false,
      checkingForUpdates: false,
      updateStage: keepStage ? staged : null,
      updateProgress: null,
      updateError: null,
      notice: force && update.status === 'upToDate'
        ? { kind: 'info', message: `Kytto ${update.currentVersion} is up to date.` }
        : getState().notice,
    });
  } catch (error) {
    setState({
      checkingForUpdates: false,
      notice: force ? { kind: 'error', message: error.message } : getState().notice,
    });
  }
}

async function downloadUpdate() {
  setState({
    updateProgress: {
      phase: 'downloading',
      completedBytes: 0,
      totalBytes: null,
      fraction: null,
      message: 'Starting the verified download…',
    },
    updateStage: null,
    updateError: null,
    notice: null,
  });
  try {
    const stage = await invoke('updates.download');
    setState({
      updateProgress: null,
      updateStage: stage,
      updateError: null,
      notice: { kind: 'info', message: `Kytto ${stage.version} is downloaded and its checksum is verified. Review the install step before relaunching.` },
    });
  } catch (error) {
    setState({
      updateProgress: null,
      updateError: error.message,
      notice: { kind: 'error', message: error.message },
    });
  }
}

async function cancelUpdateDownload() {
  try {
    await invoke('updates.cancel');
    setState({ notice: { kind: 'info', message: 'Cancelling the update download…' } });
  } catch (error) {
    reportFailure(error);
  }
}

async function installUpdate() {
  const token = getState().updateStage?.token;
  if (!token) return;
  setState({
    updateProgress: {
      phase: 'installing',
      completedBytes: getState().updateStage.byteCount,
      totalBytes: getState().updateStage.byteCount,
      fraction: 1,
      message: 'Replacing Kytto safely…',
    },
    updateError: null,
    notice: null,
  });
  try {
    const result = await invoke('updates.install', { token });
    setState({
      updateProgress: null,
      updateStage: null,
      updateError: null,
      notice: { kind: 'info', message: `Kytto ${result.version} was verified and is relaunching.` },
    });
  } catch (error) {
    setState({
      updateProgress: null,
      updateError: error.message,
      notice: { kind: 'error', message: error.message },
    });
  }
}

async function refresh() {
  try {
    const [state, backups] = await Promise.all([
      invoke('discovery.refresh'),
      invoke('backups.list', { clientID: null }),
    ]);
    setState({ backups, ...state });
  } catch (error) {
    setState({ notice: { kind: 'error', message: error.message } });
  }
}

/** Applies whatever the native side says the world now looks like. */
function applyState(state) {
  if (state) setState(state);
}

async function reloadBackups() {
  setState({ backups: await invoke('backups.list', { clientID: null }) });
}

async function reloadProfiles() {
  setState({ profiles: await invoke('profiles.list') });
}

async function reloadActivity() {
  setState({ activity: await invoke('activity.list') });
}

/**
 * A write has already committed before these secondary lists are refreshed.
 * Their failure must not turn a successful mutation into an apparent failure:
 * retrying the original action could duplicate work or overwrite newer state.
 */
async function refreshAfterMutation({ backups = false, profiles = false, secrets = false } = {}) {
  const refreshes = [];
  if (backups) refreshes.push(['Backups', reloadBackups()]);
  if (profiles) refreshes.push(['Profiles', reloadProfiles()]);
  if (secrets) refreshes.push(['Secrets', reloadSecrets()]);
  if (refreshes.length === 0) return null;

  const results = await Promise.allSettled(refreshes.map(([, operation]) => operation));
  const failed = results
    .map((result, index) => (result.status === 'rejected' ? refreshes[index][0] : null))
    .filter(Boolean);
  if (failed.length === 0) return null;

  return `The change succeeded, but Kytto could not refresh ${failed.join(' and ')}. Reload Kytto before making another change.`;
}

function withRefreshWarning(notice, warning) {
  if (!warning) return notice;
  if (!notice) return { kind: 'warning', message: warning };
  return {
    ...notice,
    kind: notice.kind === 'error' ? 'error' : 'warning',
    message: `${notice.message} ${warning}`,
  };
}

// MARK: - Local MCP library and agent import

function writableLibraryClients(state = getState()) {
  return state.clients.filter((client) => !client.isReadOnly && client.state !== 'notInstalled');
}

function defaultImportTargets(state = getState()) {
  const clients = writableLibraryClients(state);
  const preferred = clients.find((client) => client.state === 'ready') ?? clients[0];
  return preferred ? [preferred.id] : [];
}

async function openLibraryPanel() {
  if (getState().importPrompt) return;
  try {
    const result = await invoke('imports.prompt');
    setState({ importPrompt: result.prompt });
  } catch (error) {
    reportFailure(error);
  }
}

async function chooseLibraryDirectory() {
  try {
    const choice = await invoke('library.chooseDirectory');
    if (!choice.path) return;
    setState({ libraryBusy: true, libraryPath: choice.path, libraryWarnings: [], libraryCandidates: [], libraryPreview: null, libraryPreviewCandidateID: null });
    const result = await invoke('library.scan', { path: choice.path });
    const targets = { ...getState().libraryTargets };
    for (const candidate of result.candidates) {
      if (!targets[candidate.id]) targets[candidate.id] = defaultImportTargets();
    }
    setState({
      libraryBusy: false,
      libraryPath: result.rootPathDisplay,
      libraryCandidates: result.candidates,
      libraryWarnings: result.warnings ?? [],
      libraryTargets: targets,
      libraryPreview: null,
      libraryPreviewCandidateID: null,
    });
  } catch (error) {
    setState({ libraryBusy: false });
    reportFailure(error);
  }
}

async function previewLibrary(candidateID) {
  const state = getState();
  const targets = state.libraryTargets?.[candidateID] ?? defaultImportTargets(state);
  setState({ libraryBusy: true, libraryPreviewCandidateID: candidateID, notice: null });
  try {
    const preview = await invoke('library.preview', { candidateID, clientIDs: targets });
    setState({ libraryBusy: false, libraryPreview: preview, libraryPreviewCandidateID: candidateID });
  } catch (error) {
    setState({ libraryBusy: false, libraryPreview: null, libraryPreviewCandidateID: null });
    reportFailure(error);
  }
}

async function importLibrary(candidateID) {
  const state = getState();
  const targets = state.libraryTargets?.[candidateID] ?? defaultImportTargets(state);
  setState({ libraryBusy: true });
  try {
    const result = await invoke('library.import', { candidateID, clientIDs: targets });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      libraryBusy: false,
      libraryPreview: null,
      libraryPreviewCandidateID: null,
      notice: withRefreshWarning(
        { kind: 'info', message: `Imported ${result.serverNames.join(', ')}. The affected clients should be restarted before using the new configuration.` },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ libraryBusy: false });
    reportFailure(error);
  }
}

async function copyImportPrompt() {
  try {
    const result = getState().importPrompt ? { prompt: getState().importPrompt } : await invoke('imports.prompt');
    setState({ importPrompt: result.prompt });
    await invoke('clipboard.writeText', { text: result.prompt });
    setState({ notice: { kind: 'info', message: 'Copied the strict agent-import prompt. It asks for JSON without secret values.' } });
  } catch (error) {
    reportFailure(error);
  }
}

async function previewAgentImport() {
  const state = getState();
  const targets = state.importTargetIDs?.length ? state.importTargetIDs : defaultImportTargets(state);
  setState({ importBusy: true, importError: null, notice: null });
  try {
    const preview = await invoke('imports.preview', { json: state.importText, clientIDs: targets });
    setState({ importBusy: false, importPreview: preview, importTargetIDs: targets, importError: null });
  } catch (error) {
    setState({ importBusy: false, importPreview: null, importError: error.message });
  }
}

async function applyAgentImport() {
  const state = getState();
  const targets = state.importTargetIDs?.length ? state.importTargetIDs : defaultImportTargets(state);
  setState({ importBusy: true, importError: null });
  try {
    const result = await invoke('imports.import', { json: state.importText, clientIDs: targets });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      importBusy: false,
      importPreview: null,
      importError: null,
      notice: withRefreshWarning(
        { kind: 'info', message: `Imported ${result.serverNames.join(', ')}. The affected clients should be restarted before using the new configuration.` },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ importBusy: false, importError: error.message });
  }
}

// MARK: - Skills inventory

async function refreshSkills(workspacePath = getState().skillsWorkspacePath || null) {
  setState({ skillsBusy: true });
  try {
    const result = await invoke('skills.inventory', { workspacePath });
    setState({
      skillsBusy: false,
      skillsWorkspacePath: workspacePath ?? '',
      skillsEntries: result.entries ?? [],
      skillsWarnings: result.warnings ?? [],
      skillsRoots: result.roots ?? [],
    });
  } catch (error) {
    setState({ skillsBusy: false });
    reportFailure(error);
  }
}

async function chooseSkillsDirectory() {
  try {
    const choice = await invoke('skills.chooseDirectory');
    if (choice.path) await refreshSkills(choice.path);
  } catch (error) {
    reportFailure(error);
  }
}

// MARK: - Profiles

function openProfileEditor(profile = null) {
  setState({
    sheet: {
      kind: 'profileEditor',
      mode: profile ? 'edit' : 'create',
      profileId: profile?.id ?? null,
      name: profile?.name ?? '',
      serverIDs: [...(profile?.serverIDs ?? [])],
      tokenBudget: profile?.tokenBudget ?? null,
      busy: false,
      error: null,
    },
  });
}

async function saveProfile() {
  const current = getState().sheet;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const payload = { name: current.name, serverIDs: current.serverIDs, tokenBudget: current.tokenBudget };
    const profiles = current.mode === 'edit'
      ? await invoke('profiles.update', { profileID: current.profileId, ...payload })
      : await invoke('profiles.create', payload);
    setState({
      profiles,
      sheet: null,
      notice: { kind: 'info', message: `Saved profile “${current.name.trim()}”.` },
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

function defaultProfileClient(state) {
  return (
    state.clients.find((client) => client.state === 'ready') ??
    state.clients.find((client) => client.state === 'noConfig') ??
    state.clients.find((client) => client.state === 'orphanedConfig') ??
    null
  );
}

async function applyProfile(profileId, clientId) {
  const current = getState().sheet;
  setState({ sheet: { ...current, busy: true } });
  try {
    const result = await invoke('profiles.apply', { profileID: profileId, clientID: clientId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    const changed = result.enabledCount + result.disabledCount;
    const skipped = result.failures.length;
    const notice = {
      kind: skipped > 0 ? 'warning' : 'info',
      message: skipped > 0
        ? `Applied “${result.profileName}” with ${changed} change${changed === 1 ? '' : 's'}; ${skipped} server${skipped === 1 ? ' was' : 's were'} skipped: ${result.failures.map((failure) => failure.serverName).join(', ')}.`
        : changed > 0
          ? `Applied “${result.profileName}”: ${result.enabledCount} enabled, ${result.disabledCount} switched off.`
          : `This client already matched “${result.profileName}”.`,
    };
    setState({
      sheet: null,
      notice: withRefreshWarning(notice, refreshWarning),
    });
  } catch (error) {
    reportFailure(error);
    setState({ sheet: null });
  }
}

// MARK: - Gateway

async function openGatewayPreview(serverId, clientId) {
  try {
    const preview = await invoke('gateway.preview', { serverID: serverId, clientID: clientId });
    setState({ sheet: { kind: 'gatewayPreview', preview, busy: false, error: null } });
  } catch (error) {
    reportFailure(error);
  }
}

async function enableGateway(serverId, clientId, routeId) {
  const current = getState().sheet;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('gateway.enable', {
      serverID: serverId,
      clientID: clientId,
      routeID: routeId,
    });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      sheet: null,
      notice: withRefreshWarning(
        {
          kind: 'info',
          message: `Gateway mode is active for “${result.route.serverName}”. Restart the client to use the new route.`,
        },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

async function restoreGateway(routeId) {
  const current = getState().sheet;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('gateway.restore', { routeID: routeId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      sheet: null,
      notice: withRefreshWarning(
        {
          kind: 'info',
          message: `Restored Direct mode for “${result.route.serverName}”. Restart the client to load the original definition.`,
        },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

/// Opens the picker with the route's current allow list, or with everything
/// ticked when it has none — the starting point is what the client sees today,
/// so opening the sheet and saving without touching anything changes nothing.
function openToolMask(routeId) {
  const state = getState();
  const route = state.gatewayRoutes.find((entry) => entry.id === routeId);
  if (!route) return;

  const server = state.servers.find((entry) => entry.id === route.serverID);
  const names = (server?.health?.tools ?? []).map((tool) => tool.name);
  setState({
    sheet: {
      kind: 'toolMask',
      routeId,
      selected: route.exposedTools ?? names,
      busy: false,
      error: null,
    },
  });
}

async function saveToolMask(routeId) {
  const sheet = getState().sheet;
  const route = getState().gatewayRoutes.find((entry) => entry.id === routeId);
  const server = getState().servers.find((entry) => entry.id === route?.serverID);
  const names = (server?.health?.tools ?? []).map((tool) => tool.name);

  // Keeping everything is not the same instruction as a list that happens to
  // contain everything: it puts the route back to forwarding bytes untouched
  // (§3.3), rather than leaving a filter in place that currently blocks nothing.
  const keepsEverything = names.length > 0 && names.every((name) => sheet.selected.includes(name));
  const toolNames = keepsEverything ? null : sheet.selected;

  setState({ sheet: { ...sheet, busy: true, error: null } });
  try {
    applyState(await invoke('gateway.setExposedTools', { routeID: routeId, toolNames }));
    const hidden = toolNames === null ? 0 : names.length - toolNames.length;
    setState({
      sheet: null,
      notice: {
        kind: 'info',
        message:
          hidden === 0
            ? `“${route.serverName}” now offers all of its tools again. Restart the client to apply it.`
            : `Hiding ${hidden} of ${names.length} tools from “${route.serverName}”. Restart the client to apply it.`,
      },
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

// MARK: - Drift

async function openUnifyPreview(serverId, sourceClientId) {
  try {
    const preview = await invoke('servers.unifyPreview', {
      serverID: serverId,
      sourceClientID: sourceClientId,
    });
    setState({ sheet: { kind: 'unifyPreview', preview, busy: false, error: null } });
  } catch (error) {
    reportFailure(error);
  }
}

async function confirmUnify(serverId, sourceClientId) {
  setState({ sheet: { ...getState().sheet, busy: true, error: null } });
  try {
    const result = await invoke('servers.unify', {
      serverID: serverId,
      sourceClientID: sourceClientId,
    });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });

    // Three outcomes worth telling apart: files rewritten, an off copy brought
    // into line without touching any file, and an off copy that could not be.
    const written = result.changed.length;
    const parked = result.parkedUpdated.length;
    const parts = [];
    if (written > 0) {
      parts.push(`${written} client${written === 1 ? '' : 's'} rewritten`);
    }
    if (parked > 0) {
      parts.push(`${parked} switched-off cop${parked === 1 ? 'y' : 'ies'} updated in place`);
    }
    setState({
      sheet: null,
      notice: withRefreshWarning(
        {
          kind: result.parkedFailures.length > 0 ? 'warning' : 'info',
          message: result.parkedFailures.length > 0
            ? `“${result.serverName}” was unified — ${parts.join(', ')} — but Kytto could not update the stored off copy for ${result.parkedFailures.join(', ')}. Switching it on there would bring back the old definition.`
            : `“${result.serverName}” now reads the same everywhere: ${parts.join(', ')}. Every file was backed up first.`,
        },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

// MARK: - MCP Doctor

async function previewDoctorFix(serverId) {
  try {
    const preview = await invoke('doctor.previewFix', { serverID: serverId });
    setState({ sheet: { kind: 'doctorFix', serverId, preview, busy: false, error: null } });
  } catch (error) {
    reportFailure(error);
  }
}

async function applyDoctorFix(serverId) {
  setState({ sheet: { ...getState().sheet, busy: true, error: null } });
  try {
    const result = await invoke('doctor.applyFix', { serverID: serverId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true, profiles: true });
    setState({
      sheet: null,
      notice: withRefreshWarning(
        { kind: 'info', message: `Pinned the verified executable for “${result.serverName}”. Affected configurations were backed up first.` },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

/** Turns a write failure into something the user can act on. */
function reportFailure(error) {
  const changedOnDisk = /changed on disk/i.test(error.message ?? '');
  setState({
    notice: {
      kind: changedOnDisk ? 'warning' : 'error',
      message: changedOnDisk
        ? `${error.message} Kytto did not write anything. Refresh to load the current version, then try again.`
        : error.message,
    },
  });
  return changedOnDisk;
}

// MARK: - Toggling

async function toggleCell(serverId, clientId, currentlyEnabled, { undoing = false } = {}) {
  // Read before the write: afterwards the definition is in the client and the
  // state cannot say whether this click is the one that put it there.
  const before = getState().servers.find((entry) => entry.id === serverId);
  const previousState = before?.enabledIn[clientId] ?? 'absent';
  const wasAbsent = previousState === 'absent';
  const arrivingByRelativePath = wasAbsent && !currentlyEnabled && before?.hasRelativePath;

  setCellBusy(serverId, clientId, true);
  try {
    const result = await invoke('servers.setEnabled', {
      serverID: serverId,
      clientID: clientId,
      enabled: !currentlyEnabled,
    });
    applyState(result.state);

    const client = getState().clients.find((entry) => entry.id === result.clientID);
    const clientName = client?.displayName ?? result.clientID;
    const undo = {
      label: 'Undo',
      action: 'undo-toggle',
      serverId,
      clientId,
      previousState,
      backupId: result.backupID,
    };

    // Removing a definition to switch a server off is surprising enough that it
    // gets said out loud, along with the fact that it is reversible.
    if (undoing) {
      setState({
        notice: {
          kind: 'info',
          message: `Undid the change to “${result.serverName}” and restored its previous state in ${clientName}.`,
        },
      });
    } else if (result.wasParked) {
      setState({
        notice: {
          kind: 'info',
          message: `${clientName} has no "disabled" state, so “${result.serverName}” was removed from ${result.pathDisplay} and stored by Kytto. Switching it back on restores it exactly.`,
          action: undo,
        },
      });
    } else if (arrivingByRelativePath) {
      // The write succeeded, which is the whole problem: nothing else on screen
      // will ever say that this one may not run. A health check is the only
      // thing that settles it, so the notice points at one.
      setState({
        notice: {
          kind: 'warning',
          message: `“${result.serverName}” was added to ${clientName}, but it is launched through a relative path. That path is resolved against the directory the client runs from, so it may not start here — check it before relying on it.`,
          action: undo,
        },
      });
    } else {
      setState({
        notice: {
          kind: 'info',
          message: `Updated “${result.serverName}” in ${clientName}. A timestamped backup was created first.`,
          action: undo,
        },
      });
    }
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) {
      setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
    }
  } catch (error) {
    if (reportFailure(error)) await refresh();
  } finally {
    setCellBusy(serverId, clientId, false);
  }
}

/// The page asks for settings; what a settings window is stays native (§3.2).
async function openSettings() {
  try {
    await invoke('settings.open');
  } catch (error) {
    setState({ notice: { kind: 'error', message: error.message } });
  }
}

/// Folding the read-only sources away is remembered, so it is native settings
/// rather than a flag that resets on every launch.
async function setShowsCustomSources(shown) {
  try {
    setState({ settings: await invoke('settings.setShowsCustomSources', { shown }) });
  } catch (error) {
    setState({ notice: { kind: 'error', message: error.message } });
  }
}

// MARK: - Sidebar width

const DEFAULT_SIDEBAR_WIDTH = 196;

/// Mirrors the native clamp so a drag feels the rail; native sanitizes
/// authoritatively when the width is saved.
const clampSidebarWidth = (width) => Math.min(480, Math.max(170, Math.round(width)));

/// Geometry, not state: written onto the document root exactly the way the
/// shell writes its window metrics, so the layout positions against a variable
/// and never re-renders per pointer move.
function applySidebarWidth(width) {
  document.documentElement.style.setProperty(
    '--sidebar-width',
    `${clampSidebarWidth(width ?? DEFAULT_SIDEBAR_WIDTH)}px`,
  );
}

/// The width is the user's and has to survive a relaunch, so it lives in native
/// settings like the custom-sources fold. The reply is what was actually kept.
async function commitSidebarWidth(width) {
  try {
    const settings = await invoke('settings.setSidebarWidth', { width: clampSidebarWidth(width) });
    applySidebarWidth(settings.sidebarWidth);
    setState({ settings });
  } catch (error) {
    reportFailure(error);
  }
}

async function confirmToggle(serverId, clientId, currentlyEnabled) {
  setState({ sheet: { ...getState().sheet, busy: true, error: null } });
  try {
    const settings = await invoke('settings.confirmMatrixWrites');
    setState({ settings, sheet: null });
    await toggleCell(serverId, clientId, currentlyEnabled);
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

async function undoToggle(action) {
  const server = getState().servers.find((entry) => entry.id === action.serverId);
  const current = server?.enabledIn[action.clientId] ?? 'absent';
  if (current === action.previousState) {
    setState({ notice: { kind: 'info', message: 'That configuration is already back in its previous state.' } });
    return;
  }

  // Adding an absent server has no inverse toggle: switching it off would leave
  // a parked/disabled definition. Restore the exact pre-write backup instead.
  if (action.previousState === 'absent') {
    setCellBusy(action.serverId, action.clientId, true);
    try {
      const restored = action.backupId
        ? await invoke('backups.restore', {
            backupID: action.backupId,
            clientID: action.clientId,
          })
        : await invoke('servers.removeFromClient', {
            serverID: action.serverId,
            clientID: action.clientId,
          });
      applyState(restored.state ?? restored);
      const refreshWarning = await refreshAfterMutation({ backups: true });
      setState({
        notice: withRefreshWarning(
          {
            kind: 'info',
            message: `Undid the change to “${server?.name ?? action.serverId}” and restored the previous configuration.`,
          },
          refreshWarning,
        ),
      });
    } catch (error) {
      if (reportFailure(error)) await refresh();
    } finally {
      setCellBusy(action.serverId, action.clientId, false);
    }
    return;
  }

  await toggleCell(action.serverId, action.clientId, current === 'enabled', { undoing: true });
}

// MARK: - Authoring

function openForm(changes) {
  setState({
    sheet: {
      kind: 'form',
      mode: 'create',
      step: 'pick',
      draft: emptyDraft(),
      targets: [],
      errors: [],
      envHints: {},
      note: null,
      busy: false,
      ...changes,
    },
  });
}

function updateSheet(changes) {
  setState({ sheet: { ...getState().sheet, ...changes } });
}

function updateDraft(changes) {
  const sheet = getState().sheet;
  updateSheet({ draft: { ...sheet.draft, ...changes }, errors: [] });
}

function pickFromCatalog(catalogId) {
  const entry = getState().catalog.find((item) => item.id === catalogId);
  if (!entry) return;

  const placeholders = entry.placeholders ?? [];
  updateSheet({
    step: 'form',
    draft: {
      name: entry.name,
      transport: entry.transport,
      command: entry.command ?? '',
      args: [...(entry.args ?? [])],
      env: (entry.env ?? []).map((item) => ({ key: item.key, value: '' })),
      url: entry.url ?? '',
    },
    envHints: Object.fromEntries((entry.env ?? []).map((item) => [item.key, item.hint])),
    note: placeholders.length
      ? `Replace ${placeholders.map((item) => `${item.token} (${item.label}, e.g. ${item.example})`).join(' and ')} below.`
      : entry.requires
        ? `Needs ${entry.requires} installed.`
        : null,
  });
}

async function submitForm() {
  const sheet = getState().sheet;
  updateSheet({ busy: true, errors: [] });

  // Empty values mean "unchanged" when editing, and nothing at all when
  // creating. Either way an empty string would overwrite a real secret.
  const env = sheet.draft.env
    .filter((entry) => entry.key.trim() !== '')
    .map((entry) => ({
      key: entry.key,
      value: entry.value === '' || entry.value === null ? null : entry.value,
    }));
  const draft = { ...sheet.draft, env };

  try {
    const result =
      sheet.mode === 'edit'
        ? await invoke('servers.update', { serverID: sheet.serverId, draft })
        : await invoke('servers.create', { draft, clientIDs: sheet.targets });

    applyState(result.state);
    setState({
      sheet: null,
      notice: {
        kind: 'info',
        message:
          sheet.mode === 'edit'
            ? `Saved “${result.serverName}”.`
            : `Added “${result.serverName}” to ${result.changed.length} client${result.changed.length === 1 ? '' : 's'}.`,
      },
    });
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) {
      setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
    }
  } catch (error) {
    // Validation failures belong in the form, next to the fields they describe.
    updateSheet({ busy: false, errors: [error.message] });
  }
}

async function confirmDelete(serverId) {
  try {
    const result = await invoke('servers.delete', { serverID: serverId });
    applyState(result.state);
    setState({
      sheet: null,
      notice: { kind: 'info', message: `Deleted “${result.serverName}”. The files it was in were backed up first.` },
    });
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) {
      setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
    }
  } catch (error) {
    reportFailure(error);
    setState({ sheet: null });
  }
}

async function confirmRemoveFromClient(serverId, clientId) {
  // The client's name is read before the reply lands, because a removal that
  // empties the last row takes the client's own entry out of the state with it.
  const clientName =
    getState().clients.find((entry) => entry.id === clientId)?.displayName ?? clientId;
  try {
    const result = await invoke('servers.removeFromClient', {
      serverID: serverId,
      clientID: clientId,
    });
    applyState(result.state);
    setState({
      sheet: null,
      notice: {
        kind: 'info',
        message: `Took “${result.serverName}” out of ${clientName}. The file was backed up first, so this can be undone from Backups.`,
      },
    });
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) {
      setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
    }
  } catch (error) {
    reportFailure(error);
    setState({ sheet: null });
  }
}

// MARK: - Secrets

async function reloadSecrets() {
  setState({ secrets: await invoke('secrets.list') });
}

async function revealSecret(secretId) {
  // §6: never rendered by default, only behind an explicit action.
  if (!window.confirm('Show this value in plain text on screen?')) return;
  try {
    const { value } = await invoke('secrets.reveal', { secretID: secretId });
    setState({ revealedSecrets: { ...getState().revealedSecrets, [secretId]: value } });
  } catch (error) {
    reportFailure(error);
  }
}

function hideSecret(secretId) {
  const next = { ...getState().revealedSecrets };
  delete next[secretId];
  setState({ revealedSecrets: next });
}

async function confirmRotate(secretId) {
  const sheet = getState().sheet;
  if (!sheet.value) {
    setState({ sheet: { ...sheet, error: 'Enter the new value.' } });
    return;
  }
  setState({ sheet: { ...sheet, busy: true, error: null } });
  try {
    const result = await invoke('secrets.rotate', {
      secretID: secretId,
      newValue: sheet.value,
      storeInSecretStore: sheet.storeInSecretStore !== false,
    });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true, secrets: true });
    setState({
      sheet: null,
      revealedSecrets: {},
      notice: withRefreshWarning(
        {
          kind: 'info',
          message: `${result.key} updated in ${result.updatedCount} place${result.updatedCount === 1 ? '' : 's'}. Restart the affected clients to pick it up.`,
        },
        refreshWarning,
      ),
    });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
  }
}

// MARK: - Health

function setChecking(serverId, busy) {
  const next = new Set(getState().checking);
  if (busy) next.add(serverId);
  else next.delete(serverId);
  setState({ checking: next });
}

async function checkServer(serverId) {
  setChecking(serverId, true);
  try {
    const result = await invoke('health.check', { serverID: serverId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ profiles: true });
    if (result.health.status === 'failed') {
      setState({
        notice: withRefreshWarning(
          { kind: 'warning', message: `${firstLine(result.health.message ?? 'Check failed.')} Open the server for the full output.` },
          refreshWarning,
        ),
      });
    } else if (result.health.status === 'needsAuthorization') {
      // Not a failure and must not read like one (§7.3): the server asked for
      // a browser sign-in, and the detail sheet has the link.
      setState({
        notice: withRefreshWarning(
          {
            kind: 'info',
            message: `${firstLine(result.health.message ?? 'The server is waiting for authorization.')} Open the server for the sign-in link.`,
          },
          refreshWarning,
        ),
      });
    } else {
      setState({ notice: withRefreshWarning(null, refreshWarning) });
    }
  } catch (error) {
    reportFailure(error);
  } finally {
    setChecking(serverId, false);
  }
}

async function checkAll() {
  setState({ healthProgress: { serverName: '', index: 0, total: 0 }, notice: null });
  try {
    applyState(await invoke('health.checkAll'));
    const refreshWarning = await refreshAfterMutation({ profiles: true });
    if (refreshWarning) setState({ notice: withRefreshWarning(null, refreshWarning) });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ healthProgress: null });
  }
}

async function checkProvenanceLatest(serverId) {
  setState({ provenanceBusyServerID: serverId, notice: null });
  try {
    applyState(await invoke('provenance.checkLatest', { serverID: serverId }));
    const server = getState().servers.find((entry) => entry.id === serverId);
    setState({
      notice: {
        kind: 'info',
        message: server?.provenance?.latestVersion
          ? `Checked ${server.name}: latest comparable release is ${server.provenance.latestVersion}.`
          : 'The registry did not provide a comparable release version.',
      },
    });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ provenanceBusyServerID: null });
  }
}

const firstLine = (text) => String(text).split('\n')[0];

async function restoreBackup(backupId, clientId) {
  try {
    applyState(await invoke('backups.restore', { backupID: backupId, clientID: clientId }));
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      notice: withRefreshWarning(
        { kind: 'info', message: 'Reverted. The version it replaced was backed up too.' },
        refreshWarning,
      ),
    });
  } catch (error) {
    reportFailure(error);
  }
}

// MARK: - Event routing

// The one drag in the app, and the reason this file has a pointerdown listener:
// resizing cannot be said with clicks. While the pointer is down the width is
// written straight onto the document root — presentation geometry, the same
// channel the shell's window metrics use — and state learns the final number
// once, on release, when it is committed as a native setting. The move/up
// listeners exist only for the drag's duration, like the caret exceptions in
// the input handler below.
root.addEventListener('pointerdown', (event) => {
  const target = event.target.closest('[data-action="resize-sidebar"]');
  if (!target || event.button !== 0) return;
  event.preventDefault();

  const startX = event.clientX;
  const startWidth = getState().settings?.sidebarWidth ?? DEFAULT_SIDEBAR_WIDTH;
  const widthAt = (pointer) => clampSidebarWidth(startWidth + (pointer.clientX - startX));

  target.classList.add('dragging');
  target.setPointerCapture(event.pointerId);
  const move = (pointer) => applySidebarWidth(widthAt(pointer));
  const finish = (pointer) => {
    target.removeEventListener('pointermove', move);
    target.removeEventListener('pointerup', finish);
    target.removeEventListener('pointercancel', finish);
    target.classList.remove('dragging');
    void commitSidebarWidth(widthAt(pointer));
  };
  target.addEventListener('pointermove', move);
  target.addEventListener('pointerup', finish);
  target.addEventListener('pointercancel', finish);
});

root.addEventListener('dblclick', (event) => {
  const target = event.target.closest('[data-action="resize-sidebar"]');
  if (!target) return;
  void commitSidebarWidth(DEFAULT_SIDEBAR_WIDTH);
});

// One listener per event type on the container. Not one per row, and never a
// listener attached inside a render.
root.addEventListener('click', async (event) => {
  const target = event.target.closest('[data-action]');
  if (!target) return;

  const state = getState();
  const sheet = state.sheet;

  switch (target.dataset.action) {
    case 'retry-load':
      await load();
      break;
    case 'refresh':
      await refresh();
      break;
    case 'check-for-updates':
      await checkForUpdates(true);
      break;
    case 'download-update':
      await downloadUpdate();
      break;
    case 'cancel-update-download':
      await cancelUpdateDownload();
      break;
    case 'install-update':
      await installUpdate();
      break;
    case 'dismiss-update':
      setState({ updateDismissed: true });
      break;
    case 'toggle':
      if (!state.settings?.hasConfirmedMatrixWrites) {
        setState({
          sheet: {
            kind: 'confirmToggle',
            serverId: target.dataset.serverId,
            clientId: target.dataset.clientId,
            currentlyEnabled: target.dataset.enabled === 'true',
            busy: false,
            error: null,
          },
        });
      } else {
        await toggleCell(
          target.dataset.serverId,
          target.dataset.clientId,
          target.dataset.enabled === 'true',
        );
      }
      break;
    case 'confirm-toggle':
      await confirmToggle(
        target.dataset.serverId,
        target.dataset.clientId,
        target.dataset.enabled === 'true',
      );
      break;
    case 'undo-toggle':
      await undoToggle({
        serverId: target.dataset.serverId,
        clientId: target.dataset.clientId,
        previousState: target.dataset.previousState,
        backupId: target.dataset.backupId,
      });
      break;
    // The sidebar names a place rather than toggling one, so picking the screen
    // you are already on is a no-op instead of a way back to the matrix.
    case 'select-panel': {
      const panel = target.dataset.panel ?? null;
      if (panel === state.panel) break;
      try {
        if (panel === 'secrets') await reloadSecrets();
        if (panel === 'activity') await reloadActivity();
        if (panel === 'library') await openLibraryPanel();
        if (panel === 'skills') await refreshSkills();
      } catch (error) {
        reportFailure(error);
        break;
      }
      setState({ panel, selectedClient: null, revealedSecrets: {} });
      break;
    }
    // A different subject on the same screen when one client follows another, so
    // it is its own action rather than a panel that is never a no-op (§7.8).
    case 'toggle-custom-sources':
      await setShowsCustomSources(target.dataset.shown !== 'true');
      break;
    case 'open-settings':
      await openSettings();
      break;
    case 'select-client': {
      const clientId = target.dataset.clientId;
      if (state.panel === 'client' && state.selectedClient === clientId) break;
      setState({ panel: 'client', selectedClient: clientId, revealedSecrets: {} });
      break;
    }
    case 'reveal-secret':
      await revealSecret(target.dataset.secretId);
      break;
    case 'hide-secret':
      hideSecret(target.dataset.secretId);
      break;
    case 'rotate-secret':
      setState({
        sheet: { kind: 'rotate', secretId: target.dataset.secretId, value: '', storeInSecretStore: true, busy: false, error: null },
      });
      break;
    case 'confirm-rotate':
      await confirmRotate(target.dataset.secretId);
      break;
    case 'adopt-secret':
      try {
        setState({ secrets: await invoke('secrets.adopt', { secretID: target.dataset.secretId }) });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'forget-secret':
      try {
        setState({ secrets: await invoke('secrets.forget', { secretID: target.dataset.secretId }) });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'restrict-permissions':
      try {
        setState({
          secrets: await invoke('secrets.restrictPermissions', { clientID: target.dataset.clientId }),
          notice: { kind: 'info', message: 'That file can now only be read by you.' },
        });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'restore-backup':
      await restoreBackup(target.dataset.backupId, target.dataset.clientId);
      break;
    case 'acknowledge-restart':
      try {
        applyState(await invoke('restarts.acknowledge', { clientID: target.dataset.clientId }));
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'dismiss-restarts':
      try {
        applyState(await invoke('restarts.dismiss', { clientID: null }));
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'choose-library-directory':
      await chooseLibraryDirectory();
      break;
    case 'preview-library':
      await previewLibrary(target.dataset.candidateId);
      break;
    case 'import-library':
      await importLibrary(target.dataset.candidateId);
      break;
    case 'copy-import-prompt':
      await copyImportPrompt();
      break;
    case 'preview-import':
      await previewAgentImport();
      break;
    case 'apply-import':
      await applyAgentImport();
      break;
    case 'choose-skills-directory':
      await chooseSkillsDirectory();
      break;
    case 'dismiss-notice':
      setState({ notice: null });
      break;
    case 'finish-onboarding':
      try {
        setState({ settings: await invoke('onboarding.complete') });
      } catch (error) {
        reportFailure(error);
      }
      break;

    case 'refresh-activity':
      try {
        await reloadActivity();
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'copy-activity-report':
      try {
        await invoke('clipboard.writeText', { text: activityReport(state) });
        setState({ notice: { kind: 'info', message: 'Copied a metadata-only gateway support report. Arguments, results and environment values were not captured.' } });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'acknowledge-contract':
      try {
        applyState(await invoke('contract.acknowledge', { serverID: target.dataset.serverId }));
        setState({
          notice: {
            kind: 'info',
            message: 'Marked as reviewed. Kytto will report the next change against this contract.',
          },
        });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'unify-server':
      await openUnifyPreview(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'confirm-unify':
      await confirmUnify(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'doctor-preview-fix':
      await previewDoctorFix(target.dataset.serverId);
      break;
    case 'doctor-apply-fix':
      await applyDoctorFix(target.dataset.serverId);
      break;

    case 'new-profile':
      openProfileEditor();
      break;
    case 'edit-profile': {
      const profile = state.profiles.find((entry) => entry.id === target.dataset.profileId);
      if (profile) openProfileEditor(profile);
      break;
    }
    case 'save-profile':
      await saveProfile();
      break;
    case 'apply-profile': {
      const client = defaultProfileClient(state);
      setState({
        sheet: {
          kind: 'profileApply',
          profileId: target.dataset.profileId,
          clientId: client?.id ?? null,
          busy: false,
        },
      });
      break;
    }
    case 'confirm-profile-apply':
      await applyProfile(target.dataset.profileId, target.dataset.clientId);
      break;
    case 'delete-profile':
      setState({ sheet: { kind: 'profileDelete', profileId: target.dataset.profileId } });
      break;
    case 'confirm-profile-delete': {
      try {
        const profiles = await invoke('profiles.delete', { profileID: target.dataset.profileId });
        setState({ profiles, sheet: null, notice: { kind: 'info', message: 'Profile deleted. Client configurations were not changed.' } });
      } catch (error) {
        reportFailure(error);
        setState({ sheet: null });
      }
      break;
    }

    case 'open-server':
      setState({ sheet: { kind: 'detail', serverId: target.dataset.serverId } });
      break;
    case 'close-sheet':
      // Only the backdrop itself closes; clicks inside the panel bubble up here
      // too and must be ignored.
      if (target.classList.contains('sheet-backdrop') && event.target !== target) break;
      setState({ sheet: null });
      break;

    case 'check-server':
      await checkServer(target.dataset.serverId);
      break;
    // Only the server id crosses; the native side validates and opens the URL
    // the last check recorded from the server's own output.
    case 'open-authorization':
      try {
        await invoke('health.openAuthorization', { serverID: target.dataset.serverId });
        setState({
          notice: {
            kind: 'info',
            message: 'Opened the sign-in page in your browser. Complete the authorization there, then check the server again.',
          },
        });
      } catch (error) {
        reportFailure(error);
      }
      break;
    case 'check-all':
      await checkAll();
      break;
    case 'check-provenance-latest':
      await checkProvenanceLatest(target.dataset.serverId);
      break;
    case 'copy-diagnostic': {
      const server = state.servers.find((entry) => entry.id === target.dataset.serverId);
      if (!server?.health) break;
      try {
        await invoke('clipboard.writeText', { text: diagnosticReport(server, state) });
        setState({ notice: { kind: 'info', message: `Copied the diagnostic report for “${server.name}”. Secret values and raw server output were omitted.` } });
      } catch (error) {
        reportFailure(error);
      }
      break;
    }

    case 'enable-gateway':
      await openGatewayPreview(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'confirm-enable-gateway':
      await enableGateway(target.dataset.serverId, target.dataset.clientId, target.dataset.routeId);
      break;
    case 'restore-gateway':
      setState({ sheet: { kind: 'gatewayRestore', routeId: target.dataset.routeId, busy: false, error: null } });
      break;
    case 'confirm-restore-gateway':
      await restoreGateway(target.dataset.routeId);
      break;
    case 'choose-tools':
      openToolMask(target.dataset.routeId);
      break;
    case 'confirm-tool-mask':
      await saveToolMask(target.dataset.routeId);
      break;

    case 'add-server':
      openForm({});
      break;
    case 'form-manual':
      updateSheet({ step: 'form' });
      break;
    case 'catalog-pick':
      pickFromCatalog(target.dataset.catalogId);
      break;
    case 'edit-server': {
      const server = state.servers.find((entry) => entry.id === target.dataset.serverId);
      if (!server) break;
      setState({
        sheet: {
          kind: 'form',
          mode: 'edit',
          step: 'form',
          serverId: server.id,
          original: server.name,
          draft: draftFromServer(server),
          targets: [],
          errors: [],
          envHints: {},
          note: null,
          busy: false,
        },
      });
      break;
    }
    case 'delete-server':
      setState({ sheet: { kind: 'confirmDelete', serverId: target.dataset.serverId } });
      break;
    case 'confirm-delete':
      await confirmDelete(target.dataset.serverId);
      break;
    case 'remove-from-client':
      setState({
        sheet: {
          kind: 'confirmRemoveFromClient',
          serverId: target.dataset.serverId,
          clientId: target.dataset.clientId,
        },
      });
      break;
    case 'confirm-remove-from-client':
      await confirmRemoveFromClient(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'submit-form':
      await submitForm();
      break;

    case 'draft-transport':
      updateDraft({ transport: target.dataset.value });
      break;
    case 'draft-arg-add':
      updateDraft({ args: [...sheet.draft.args, ''] });
      break;
    case 'draft-arg-remove':
      updateDraft({ args: sheet.draft.args.filter((_, i) => i !== Number(target.dataset.index)) });
      break;
    case 'draft-env-add':
      updateDraft({ env: [...sheet.draft.env, { key: '', value: '' }] });
      break;
    case 'draft-env-remove':
      updateDraft({ env: sheet.draft.env.filter((_, i) => i !== Number(target.dataset.index)) });
      break;
  }
});

root.addEventListener('input', (event) => {
  const action = event.target.dataset.action;
  if (!action) return;

  const state = getState();
  const sheet = state.sheet;
  const value = event.target.value;
  // Re-rendering on every keystroke would steal the caret, so text fields write
  // into state without triggering a render and the DOM keeps its own value until
  // the next structural change.
  const quiet = (changes) => Object.assign(sheet.draft, changes);

  switch (action) {
    case 'filter': {
      setState({ filter: value });
      const search = root.querySelector('[data-action="filter"]');
      if (search) {
        search.focus();
        search.setSelectionRange(search.value.length, search.value.length);
      }
      break;
    }
    case 'import-json':
      // As with the form fields, keep the caret in the native state without
      // rebuilding the tree on every pasted character.
      state.importText = value;
      state.importPreview = null;
      state.importError = null;
      break;
    case 'draft-name':
      quiet({ name: value });
      break;
    case 'draft-command':
      quiet({ command: value });
      break;
    case 'draft-url':
      quiet({ url: value });
      break;
    case 'draft-arg': {
      const args = [...sheet.draft.args];
      args[Number(event.target.dataset.index)] = value;
      quiet({ args });
      break;
    }
    case 'draft-env-key': {
      const env = [...sheet.draft.env];
      env[Number(event.target.dataset.index)] = { ...env[Number(event.target.dataset.index)], key: value };
      quiet({ env });
      break;
    }
    case 'draft-env-value': {
      const env = [...sheet.draft.env];
      env[Number(event.target.dataset.index)] = { ...env[Number(event.target.dataset.index)], value };
      quiet({ env });
      break;
    }
    // Held on the sheet, not the draft; same reason — re-rendering would take
    // the caret away mid-paste.
    case 'rotate-value':
      sheet.value = value;
      break;
    case 'profile-name':
      sheet.name = value;
      break;
    case 'profile-budget':
      sheet.tokenBudget = value === '' ? null : Number(value);
      break;
  }
});

root.addEventListener('change', (event) => {
  const action = event.target.dataset.action;
  const sheet = getState().sheet;
  if (action === 'library-target') {
    const state = getState();
    const candidateID = event.target.dataset.subject;
    const current = state.libraryTargets?.[candidateID] ?? defaultImportTargets(state);
    const clientID = event.target.dataset.clientId;
    const selected = event.target.checked
      ? [...new Set([...current, clientID])]
      : current.filter((id) => id !== clientID);
    setState({
      libraryTargets: { ...state.libraryTargets, [candidateID]: selected },
      libraryPreview: null,
      libraryPreviewCandidateID: null,
    });
  } else if (action === 'import-target') {
    const state = getState();
    const clientID = event.target.dataset.clientId;
    const current = state.importTargetIDs?.length ? state.importTargetIDs : defaultImportTargets(state);
    const importTargetIDs = event.target.checked
      ? [...new Set([...current, clientID])]
      : current.filter((id) => id !== clientID);
    setState({ importTargetIDs, importPreview: null, importError: null });
  } else if (action === 'draft-target') {
    const clientId = event.target.dataset.clientId;
    const targets = event.target.checked
      ? [...sheet.targets, clientId]
      : sheet.targets.filter((id) => id !== clientId);
    updateSheet({ targets, errors: [] });
  } else if (event.target.dataset.action === 'rotate-secret-store') {
    sheet.storeInSecretStore = event.target.checked;
  } else if (event.target.dataset.action === 'profile-server') {
    const serverId = event.target.dataset.serverId;
    const serverIDs = event.target.checked
      ? [...sheet.serverIDs, serverId]
      : sheet.serverIDs.filter((id) => id !== serverId);
    updateSheet({ serverIDs, error: null });
  } else if (event.target.dataset.action === 'profile-client') {
    updateSheet({ clientId: event.target.value });
  } else if (event.target.dataset.action === 'toggle-mask-tool') {
    const toolName = event.target.dataset.toolName;
    const selected = sheet.selected.includes(toolName)
      ? sheet.selected.filter((name) => name !== toolName)
      : [...sheet.selected, toolName];
    updateSheet({ selected, error: null });
  }
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && getState().sheet) {
    event.preventDefault();
    setState({ sheet: null });
    return;
  }

  // The resize handle is a separator, so the keyboard moves it too.
  if (
    document.activeElement?.dataset.action === 'resize-sidebar' &&
    (event.key === 'ArrowLeft' || event.key === 'ArrowRight')
  ) {
    event.preventDefault();
    const current = getState().settings?.sidebarWidth ?? DEFAULT_SIDEBAR_WIDTH;
    void commitSidebarWidth(current + (event.key === 'ArrowRight' ? 16 : -16));
    return;
  }

  const modal = root.querySelector('.sheet[role="dialog"]');
  if (event.key === 'Tab' && modal) {
    const focusable = [...modal.querySelectorAll(
      'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
    )].filter((element) => !element.closest('[inert]'));
    if (focusable.length === 0) {
      event.preventDefault();
      return;
    }
    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    } else if (!modal.contains(document.activeElement)) {
      event.preventDefault();
      first.focus();
    }
  }

  if (
    (event.key === 'Enter' || event.key === ' ') &&
    document.activeElement?.dataset.action === 'catalog-pick'
  ) {
    event.preventDefault();
    document.activeElement.click();
  }
});

// Menu bar events. The UI reacts to intent, not to keystrokes — the native side
// owns the shortcuts (§3).
on('menu.refresh', refresh);
on('menu.find', () => {
  root.querySelector('[data-action="filter"]')?.focus();
});

// A watched file changed, whoever changed it (§6.4).
on('configs.changed', applyState);

// Settings live in the native window, so reflect a saved choice in the open
// web view instead of waiting for its next navigation or app launch (§7.6).
on('settings.changed', (settings) => {
  applyTheme(settings.theme);
  applySidebarWidth(settings.sidebarWidth);
  setState({ settings });
});

// Which server "Check all" is currently waiting on. A ten-second stall with no
// explanation reads as a hang.
on('health.progress', (progress) => setState({ healthProgress: progress }));

// The native updater streams only phase, byte counts and a safe status message;
// it never emits the staging path or any local application data.
on('updates.progress', (progress) => setState({ updateProgress: progress, updateError: null }));

/// The web layer never decides the theme; it renders what the native side says
/// the user chose, and `system` means "leave it to the OS" (§3).
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme ?? 'system';
}

if (isStubbed) {
  document.body.dataset.stubbed = 'true';
}

load();
