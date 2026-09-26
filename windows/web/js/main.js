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
  needsMatrixWriteConfirmation,
  matrixUndoPlan,
} from './state.js';
import { render as renderMatrix } from './screens/matrix.js';
import { diagnosticReport } from './screens/serverDetail.js';
import { activityReport } from './screens/activity.js';
import { settlePostMutationRefresh } from './postMutation.js';

const root = document.getElementById('app');
const SIDEBAR_DEFAULT = 196;
const SIDEBAR_MIN = 170;
const SIDEBAR_MAX = 480;

let renderedSheetSubject = null;
let returnFocus = null;

function applySidebarWidth(width) {
  const numeric = Number(width);
  const kept = Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN,
    Number.isFinite(numeric) ? Math.round(numeric) : SIDEBAR_DEFAULT));
  document.documentElement.style.setProperty('--sidebar-width', `${kept}px`);
  return kept;
}

function currentSidebarWidth() {
  const inline = Number.parseFloat(
    document.documentElement.style.getPropertyValue('--sidebar-width'),
  );
  return Number.isFinite(inline)
    ? inline
    : applySidebarWidth(getState().settings?.sidebarWidth ?? SIDEBAR_DEFAULT);
}

async function commitSidebarWidth(width) {
  const kept = applySidebarWidth(width);
  try {
    const settings = await invoke('settings.setSidebarWidth', { width: kept });
    applySidebarWidth(settings.sidebarWidth);
    setState({ settings });
  } catch (error) {
    reportFailure(error);
    applySidebarWidth(getState().settings?.sidebarWidth ?? SIDEBAR_DEFAULT);
  }
}

// A render replaces the entire tree. Remembering the focused control is what
// makes keyboard editing survive state-driven renders, while a newly opened
// sheet deliberately takes focus and returns it to its invoker when it closes.
setRenderer(() => {
  const focused = focusSnapshot();
  const subject = focusSubject(getState().sheet);
  const opening = subject !== null && renderedSheetSubject === null;
  const replacing = subject !== null && renderedSheetSubject !== null && subject !== renderedSheetSubject;
  const closing = subject === null && renderedSheetSubject !== null;

  if (opening) returnFocus = focused;
  renderMatrix(root);

  if (opening || replacing) {
    focusSheet();
  } else if (closing) {
    restoreFocus(returnFocus);
    returnFocus = null;
  } else if (!restoreFocus(focused) && subject !== null && focused?.insideSheet) {
    // A form step legitimately removes its invoker (catalog → form). Put the
    // user at the first field instead of dropping focus back on the document.
    focusSheet(true);
  }
  renderedSheetSubject = subject;
});

function focusSubject(current) {
  if (!current) return null;
  return [current.kind, current.serverId, current.clientId, current.routeId, current.secretId, current.profileId, current.mode]
    .filter(Boolean)
    .join(':');
}

function focusSnapshot() {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement) || !root.contains(active)) return null;
  return {
    tagName: active.tagName,
    dataset: { ...active.dataset },
    text: active.tagName === 'BUTTON' ? active.textContent : null,
    selectionStart: active.selectionStart,
    selectionEnd: active.selectionEnd,
    insideSheet: Boolean(active.closest('.sheet')),
  };
}

function restoreFocus(snapshot) {
  if (!snapshot?.dataset.action) return false;
  const entries = Object.entries(snapshot.dataset);
  const candidate = [...root.querySelectorAll('[data-action]')].find(
    (node) =>
      node.tagName === snapshot.tagName &&
      (snapshot.text === null || node.textContent === snapshot.text) &&
      entries.every(([key, value]) => node.dataset[key] === value),
  );
  if (!(candidate instanceof HTMLElement)) return false;

  candidate.focus({ preventScroll: true });
  if (
    typeof snapshot.selectionStart === 'number' &&
    typeof snapshot.selectionEnd === 'number' &&
    typeof candidate.setSelectionRange === 'function'
  ) {
    candidate.setSelectionRange(snapshot.selectionStart, snapshot.selectionEnd);
  }
  return true;
}

function focusSheet(preferField = false) {
  const panel = root.querySelector('.sheet');
  if (!(panel instanceof HTMLElement)) return;
  const field = preferField
    ? panel.querySelector('.sheet-body input:not(:disabled), .sheet-body select:not(:disabled), .sheet-body textarea:not(:disabled)')
    : null;
  (field instanceof HTMLElement ? field : panel).focus({ preventScroll: true });
}

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
    // The automatic check starts only after the normal state projection is ready;
    // a network failure is intentionally silent in the web layer.
    void checkForUpdates(false);
  } catch (error) {
    setState({
      phase: 'error',
      error: { code: error.code ?? 'unknown', message: error.message },
    });
  }
}

function supportsUpdates(current = getState()) {
  return current.app?.capabilities?.includes('updates') === true;
}

async function checkForUpdates(force) {
  const current = getState();
  if (!supportsUpdates(current) || current.updateChecking) return;

  setState({ updateChecking: true });
  try {
    const result = await invoke('updates.check', { force });
    const changes = { update: result, updatePackage: null, updateProgress: null };
    if (force) {
      changes.updateDismissed = result.status !== 'updateAvailable';
      if (result.status === 'upToDate') {
        changes.notice = {
          kind: 'info',
          message: `Kytto is up to date (${result.currentVersion}).`,
        };
      }
    }
    setState(changes);
  } catch (error) {
    // Automatic checks are best-effort. Forced checks are user intent and use the
    // same ordinary notice surface as every other recoverable command failure.
    if (force) setState({ notice: { kind: 'error', message: error.message } });
  } finally {
    setState({ updateChecking: false });
  }
}

async function downloadUpdate() {
  setState({ updateProgress: { phase: 'starting', progress: 0, bytesReceived: 0, totalBytes: null } });
  try {
    const packageInfo = await invoke('updates.download');
    setState({ updatePackage: packageInfo, updateProgress: null, updateDismissed: false });
  } catch (error) {
    setState({ updateProgress: null, notice: { kind: 'error', message: error.message } });
  }
}

async function installUpdate() {
  setState({ updateProgress: { phase: 'verifying', progress: null, bytesReceived: 0, totalBytes: null } });
  try {
    const token = getState().updatePackage?.token;
    if (!token) throw new Error('Download an update before installing it.');
    await invoke('updates.install', { token });
    setState({ updateProgress: null, notice: { kind: 'info', message: 'The update installer was started.' } });
  } catch (error) {
    setState({ updateProgress: null, notice: { kind: 'error', message: error.message } });
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

const postMutationRefreshers = {
  Backups: reloadBackups,
  Profiles: reloadProfiles,
  Secrets: reloadSecrets,
};

async function refreshAfterMutation(labels, notice) {
  const refreshes = labels.map((label) => ({
    label,
    run: postMutationRefreshers[label],
  }));
  const settledNotice = await settlePostMutationRefresh(notice, refreshes);
  setState({ notice: settledNotice });
}

// MARK: - Read, preview and import tools

function writableTargetIDs() {
  return getState().clients
    .filter((client) => !client.isReadOnly && client.state !== 'notInstalled')
    .map((client) => client.id);
}

function openAgentImport() {
  setState({
    sheet: {
      kind: 'agentImport',
      prompt: null,
      json: '',
      clientIDs: writableTargetIDs(),
      preview: null,
      busy: false,
      error: null,
    },
  });
  void loadAgentImportPrompt();
}

async function loadAgentImportPrompt() {
  const current = getState().sheet;
  if (current?.kind !== 'agentImport') return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('imports.prompt');
    const sheet = getState().sheet;
    if (sheet?.kind === 'agentImport') setState({ sheet: { ...sheet, prompt: result.prompt, busy: false } });
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'agentImport') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

async function previewAgentImport() {
  const current = getState().sheet;
  if (current?.kind !== 'agentImport') return;
  if (!current.json?.trim()) {
    setState({ sheet: { ...current, error: 'Paste strict JSON before previewing the import.' } });
    return;
  }
  if ((current.clientIDs ?? []).length === 0) {
    setState({ sheet: { ...current, error: 'Choose at least one built-in client as an import target.' } });
    return;
  }
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const preview = await invoke('imports.preview', {
      json: current.json,
      clientIDs: current.clientIDs,
    });
    const sheet = getState().sheet;
    if (sheet?.kind === 'agentImport') setState({ sheet: { ...sheet, preview, busy: false } });
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'agentImport') setState({ sheet: { ...sheet, preview: null, busy: false, error: error.message } });
  }
}

async function submitAgentImport() {
  const current = getState().sheet;
  if (current?.kind !== 'agentImport' || !current.preview) return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('imports.import', {
      json: current.json,
      clientIDs: current.clientIDs,
    });
    applyState(result.state);
    const notice = {
      kind: result.warnings?.length ? 'warning' : 'info',
      message: `Imported ${result.importedServerNames.length} server${result.importedServerNames.length === 1 ? '' : 's'} into ${result.changedClientIDs.length} client${result.changedClientIDs.length === 1 ? '' : 's'}.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups'], notice);
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'agentImport') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

function openLibrary() {
  setState({
    sheet: {
      kind: 'library',
      sessionID: null,
      displayName: null,
      candidates: null,
      selectedCandidateID: null,
      clientIDs: writableTargetIDs(),
      preview: null,
      warnings: [],
      busy: false,
      error: null,
    },
  });
}

async function chooseLibraryDirectory() {
  const current = getState().sheet;
  if (current?.kind !== 'library') return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const selection = await invoke('library.chooseDirectory');
    const sheet = getState().sheet;
    if (sheet?.kind !== 'library') return;
    if (!selection) {
      setState({ sheet: { ...sheet, busy: false } });
      return;
    }
    setState({
      sheet: {
        ...sheet,
        sessionID: selection.sessionID,
        displayName: selection.displayName,
        candidates: null,
        selectedCandidateID: null,
        preview: null,
        warnings: [],
        busy: false,
      },
    });
    await scanLibrary();
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

async function scanLibrary() {
  const current = getState().sheet;
  if (current?.kind !== 'library' || !current.sessionID) return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('library.scan', { path: current.sessionID });
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, candidates: result.candidates, warnings: result.warnings, preview: null, busy: false } });
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

function selectLibraryCandidate(candidateID) {
  const current = getState().sheet;
  if (current?.kind !== 'library') return;
  setState({ sheet: { ...current, selectedCandidateID: candidateID, preview: null, error: null } });
}

async function previewLibrary() {
  const current = getState().sheet;
  if (current?.kind !== 'library' || !current.sessionID || !current.selectedCandidateID) return;
  if ((current.clientIDs ?? []).length === 0) {
    setState({ sheet: { ...current, error: 'Choose at least one built-in client as an import target.' } });
    return;
  }
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const preview = await invoke('library.preview', {
      path: current.sessionID,
      candidateID: current.selectedCandidateID,
      clientIDs: current.clientIDs,
    });
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, preview, busy: false } });
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, preview: null, busy: false, error: error.message } });
  }
}

async function submitLibrary() {
  const current = getState().sheet;
  if (current?.kind !== 'library' || !current.preview) return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('library.import', {
      path: current.sessionID,
      candidateID: current.selectedCandidateID,
      clientIDs: current.clientIDs,
    });
    applyState(result.state);
    const notice = {
      kind: result.warnings?.length ? 'warning' : 'info',
      message: `Imported “${result.serverName}” into ${result.changedClientIDs.length} client${result.changedClientIDs.length === 1 ? '' : 's'}.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups'], notice);
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'library') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

function openSkills() {
  setState({
    sheet: {
      kind: 'skills',
      sessionID: null,
      displayName: null,
      skills: null,
      warnings: [],
      busy: true,
      error: null,
    },
  });
  void inventorySkills(null);
}

async function chooseSkillsDirectory() {
  const current = getState().sheet;
  if (current?.kind !== 'skills') return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const selection = await invoke('skills.chooseDirectory');
    const sheet = getState().sheet;
    if (sheet?.kind !== 'skills') return;
    if (!selection) {
      setState({ sheet: { ...sheet, busy: false } });
      return;
    }
    setState({ sheet: { ...sheet, sessionID: selection.sessionID, displayName: selection.displayName, skills: null, warnings: [], busy: false } });
    await inventorySkills(selection.sessionID);
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'skills') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

async function inventorySkills(sessionID) {
  const current = getState().sheet;
  if (current?.kind !== 'skills') return;
  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const result = await invoke('skills.inventory', { workspacePath: sessionID });
    const sheet = getState().sheet;
    if (sheet?.kind === 'skills') setState({ sheet: { ...sheet, skills: result.skills, warnings: result.warnings, busy: false } });
  } catch (error) {
    const sheet = getState().sheet;
    if (sheet?.kind === 'skills') setState({ sheet: { ...sheet, busy: false, error: error.message } });
  }
}

async function checkLatestProvenance(serverID) {
  const checking = new Set(getState().provenanceChecking);
  checking.add(serverID);
  setState({ provenanceChecking: checking });
  try {
    const result = await invoke('provenance.checkLatest', { serverID });
    applyState(result.state);
    setState({ notice: { kind: 'info', message: `Checked the latest known version for this server.` } });
  } catch (error) {
    reportFailure(error);
  } finally {
    const next = new Set(getState().provenanceChecking);
    next.delete(serverID);
    setState({ provenanceChecking: next });
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
    state.clients.find((client) => !client.isReadOnly && client.state === 'ready') ??
    state.clients.find((client) => !client.isReadOnly && client.state === 'noConfig') ??
    state.clients.find((client) => !client.isReadOnly && client.state === 'orphanedConfig') ??
    null
  );
}

async function applyProfile(profileId, clientId) {
  const current = getState().sheet;
  setState({ sheet: { ...current, busy: true } });
  try {
    const result = await invoke('profiles.apply', { profileID: profileId, clientID: clientId });
    applyState(result.state);
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
      notice,
    });
    await refreshAfterMutation(['Backups'], notice);
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
    const notice = {
      kind: 'info',
      message: `Gateway mode is active for “${result.route.serverName}”. Restart the client to use the new route.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups'], notice);
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
    const notice = {
      kind: 'info',
      message: `Restored Direct mode for “${result.route.serverName}”. Restart the client to load the original definition.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups'], notice);
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
    const notice = {
      kind: result.parkedFailures.length > 0 ? 'warning' : 'info',
      message: result.parkedFailures.length > 0
        ? `“${result.serverName}” was unified — ${parts.join(', ')} — but Kytto could not update the stored off copy for ${result.parkedFailures.join(', ')}. Switching it on there would bring back the old definition.`
        : `“${result.serverName}” now reads the same everywhere: ${parts.join(', ')}. Every file was backed up first.`,
    };
    setState({ sheet: null, notice });
    await refreshAfterMutation(['Backups'], notice);
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
    const notice = { kind: 'info', message: `Pinned the verified executable for “${result.serverName}”. Affected configurations were backed up first.` };
    setState({ sheet: null, notice });
    await refreshAfterMutation(['Backups', 'Profiles'], notice);
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

async function toggleCell(
  serverId,
  clientId,
  previousState,
  { closeSheetOnSuccess = false, failureInSheet = false } = {},
) {
  // Read before the write: afterwards the definition is in the client and the
  // state cannot say whether this click is the one that put it there.
  const before = getState().servers.find((entry) => entry.id === serverId);
  const wasAbsent = previousState === 'absent';
  const enabled = previousState !== 'enabled';
  const arrivingByRelativePath = wasAbsent && enabled && before?.hasRelativePath;

  setCellBusy(serverId, clientId, true);
  try {
    const result = await invoke('servers.setEnabled', {
      serverID: serverId,
      clientID: clientId,
      enabled,
    });
    // Capture the reversal before replacing the discovery state. In particular,
    // an absent cell is no longer absent in the reply and only the returned
    // backup can restore the exact pre-write bytes.
    const undo = {
      previousState,
      clientId,
      serverId,
      backupId: result.backupID ?? null,
    };
    applyState(result.state);

    const client = getState().clients.find((entry) => entry.id === result.clientID);
    const clientName = client?.displayName ?? result.clientID;
    let notice;

    // Removing a definition to switch a server off is surprising enough that it
    // gets said out loud, along with the fact that it is reversible.
    if (result.wasParked) {
      notice = {
        kind: 'info',
        message: `${clientName} has no "disabled" state, so “${result.serverName}” was removed from ${result.pathDisplay} and stored by Kytto. Switching it back on restores it exactly.`,
        undo,
      };
    } else if (arrivingByRelativePath) {
      // The write succeeded, which is the whole problem: nothing else on screen
      // will ever say that this one may not run. A health check is the only
      // thing that settles it, so the notice points at one.
      notice = {
        kind: 'warning',
        message: `“${result.serverName}” was added to ${clientName}, but it is launched through a relative path. That path is resolved against the directory the client runs from, so it may not start here — check it before relying on it.`,
        undo,
      };
    } else {
      const outcome = previousState === 'absent'
        ? `Added “${result.serverName}” to ${clientName}.`
        : `${result.enabled ? 'Turned on' : 'Turned off'} “${result.serverName}” in ${clientName}.`;
      notice = { kind: 'info', message: outcome, undo };
    }
    setState({ sheet: closeSheetOnSuccess ? null : getState().sheet, notice });
    await refreshAfterMutation(['Backups'], notice);
  } catch (error) {
    if (failureInSheet) {
      const changedOnDisk = /changed on disk/i.test(error.message ?? '');
      setState({
        sheet: {
          ...getState().sheet,
          busy: false,
          error: changedOnDisk
            ? `${error.message} Kytto did not write anything. Refresh, then try again.`
            : error.message,
        },
      });
      if (changedOnDisk) await refresh();
    } else if (reportFailure(error)) {
      await refresh();
    }
  } finally {
    setCellBusy(serverId, clientId, false);
  }
}

async function confirmMatrixWrite() {
  const current = getState().sheet;
  if (current?.kind !== 'confirmMatrixWrite') return;

  setState({ sheet: { ...current, busy: true, error: null } });
  try {
    const settings = await invoke('settings.confirmMatrixWrites');
    setState({ settings });
  } catch (error) {
    setState({ sheet: { ...getState().sheet, busy: false, error: error.message } });
    return;
  }

  await toggleCell(current.serverId, current.clientId, current.previousState, {
    closeSheetOnSuccess: true,
    failureInSheet: true,
  });
}

/// The page asks for settings; what a settings window is stays native (§3.2).
async function openSettings() {
  try {
    await invoke('settings.open');
  } catch (error) {
    reportFailure(error);
  }
}

async function setShowsCustomSources() {
  const shown = getState().settings?.showsCustomSources === false;
  try {
    const settings = await invoke('settings.setShowsCustomSources', { shown });
    setState({ settings });
  } catch (error) {
    reportFailure(error);
  }
}

async function undoMatrixWrite() {
  const current = getState().notice;
  const undo = current?.undo;
  if (!undo || current.undoBusy) return;

  setState({ notice: { ...current, undoBusy: true } });
  try {
    const plan = matrixUndoPlan(undo);
    const result = await invoke(plan.command, plan.payload);
    applyState(result.state ?? result);
    const notice = { kind: 'info', message: 'Undone. The previous client state is restored.' };
    setState({ notice });
    await refreshAfterMutation(['Backups'], notice);
  } catch (error) {
    const changedOnDisk = /changed on disk/i.test(error.message ?? '');
    setState({
      notice: {
        ...current,
        kind: changedOnDisk ? 'warning' : 'error',
        message: changedOnDisk
          ? `${error.message} Refresh to load the current file, then try Undo again.`
          : `Undo failed: ${error.message}`,
        undoBusy: false,
      },
    });
  }
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
    const notice = {
      kind: 'info',
      message:
        sheet.mode === 'edit'
          ? `Saved “${result.serverName}”.`
          : `Added “${result.serverName}” to ${result.changed.length} client${result.changed.length === 1 ? '' : 's'}.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups', 'Profiles'], notice);
  } catch (error) {
    // Validation failures belong in the form, next to the fields they describe.
    updateSheet({ busy: false, errors: [error.message] });
  }
}

async function confirmDelete(serverId) {
  try {
    const result = await invoke('servers.delete', { serverID: serverId });
    applyState(result.state);
    const notice = { kind: 'info', message: `Deleted “${result.serverName}”. The files it was in were backed up first.` };
    setState({ sheet: null, notice });
    await refreshAfterMutation(['Backups', 'Profiles'], notice);
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
    const notice = {
      kind: 'info',
      message: `Took “${result.serverName}” out of ${clientName}. The file was backed up first, so this can be undone from Backups.`,
    };
    setState({
      sheet: null,
      notice,
    });
    await refreshAfterMutation(['Backups', 'Profiles'], notice);
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
    const notice = {
      kind: 'info',
      message: `${result.key} updated in ${result.updatedCount} place${result.updatedCount === 1 ? '' : 's'}. Restart the affected clients to pick it up.`,
    };
    setState({
      sheet: null,
      revealedSecrets: {},
      notice,
    });
    await refreshAfterMutation(['Secrets', 'Backups'], notice);
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
    let notice;
    if (result.health.status === 'failed') {
      notice = { kind: 'warning', message: `${firstLine(result.health.message ?? 'Check failed.')} Open the server for the full output.` };
    } else if (result.health.status === 'needsAuthorization') {
      notice = {
        kind: 'info',
        message: `${firstLine(result.health.message ?? 'Waiting for authorization.')} Open the server for the sign-in link.`,
      };
    } else {
      notice = null;
    }
    setState({ notice });
    await refreshAfterMutation(['Profiles'], notice);
  } catch (error) {
    reportFailure(error);
  } finally {
    setChecking(serverId, false);
  }
}

async function openAuthorization(serverId) {
  try {
    await invoke('health.openAuthorization', { serverID: serverId });
    setState({
      notice: {
        kind: 'info',
        message: 'Opened the sign-in page in your browser. Complete the authorization there, then check the server again.',
      },
    });
  } catch (error) {
    reportFailure(error);
  }
}

async function checkAll() {
  setState({ healthProgress: { serverName: '', index: 0, total: 0 }, notice: null });
  try {
    applyState(await invoke('health.checkAll'));
    setState({ notice: null });
    await refreshAfterMutation(['Profiles'], null);
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ healthProgress: null });
  }
}

const firstLine = (text) => String(text).split('\n')[0];

async function restoreBackup(backupId, clientId) {
  try {
    applyState(await invoke('backups.restore', { backupID: backupId, clientID: clientId }));
    const notice = { kind: 'info', message: 'Reverted. The version it replaced was backed up too.' };
    setState({ notice });
    await refreshAfterMutation(['Backups'], notice);
  } catch (error) {
    reportFailure(error);
  }
}

// MARK: - Event routing

// One listener per event type on the container. Not one per row, and never a
// listener attached inside a render.
root.addEventListener('click', async (event) => {
  const target = event.target.closest('[data-action]');
  if (!target) return;

  const state = getState();
  const sheet = state.sheet;

  try {
    switch (target.dataset.action) {
    case 'refresh':
      if (state.phase === 'error') await load();
      else await refresh();
      break;
    case 'check-for-updates':
      await checkForUpdates(true);
      break;
    case 'download-update':
      await downloadUpdate();
      break;
    case 'cancel-update':
      try {
        await invoke('updates.cancel');
        setState({ updateProgress: null });
      } catch (error) {
        setState({ notice: { kind: 'error', message: error.message } });
      }
      break;
    case 'open-update-download':
      try {
        await invoke('updates.openDownload');
      } catch (error) {
        setState({ notice: { kind: 'error', message: error.message } });
      }
      break;
    case 'install-update':
      await installUpdate();
      break;
    case 'dismiss-update':
      setState({ updateDismissed: true });
      break;
    case 'open-agent-import':
      openAgentImport();
      break;
    case 'agent-import-prompt':
      await loadAgentImportPrompt();
      break;
    case 'agent-import-preview':
      await previewAgentImport();
      break;
    case 'agent-import-submit':
      await submitAgentImport();
      break;
    case 'open-library':
      openLibrary();
      break;
    case 'library-choose-directory':
      await chooseLibraryDirectory();
      break;
    case 'library-scan':
      await scanLibrary();
      break;
    case 'library-select-candidate':
      selectLibraryCandidate(target.dataset.candidateId);
      break;
    case 'library-preview':
      await previewLibrary();
      break;
    case 'library-submit':
      await submitLibrary();
      break;
    case 'open-skills':
      openSkills();
      break;
    case 'skills-choose-directory':
      await chooseSkillsDirectory();
      break;
    case 'skills-scan':
      await inventorySkills(getState().sheet?.sessionID ?? null);
      break;
    case 'check-provenance':
      await checkLatestProvenance(target.dataset.serverId);
      break;
    case 'toggle': {
      const client = state.clients.find((entry) => entry.id === target.dataset.clientId);
      if (target.disabled || client?.isReadOnly || client?.state === 'notInstalled') break;
      const previousState = target.dataset.state ?? (target.dataset.enabled === 'true' ? 'enabled' : 'disabled');
      if (needsMatrixWriteConfirmation(state.settings, client)) {
        setState({
          sheet: {
            kind: 'confirmMatrixWrite',
            serverId: target.dataset.serverId,
            clientId: target.dataset.clientId,
            previousState,
            busy: false,
            error: null,
          },
        });
        break;
      }
      await toggleCell(
        target.dataset.serverId,
        target.dataset.clientId,
        previousState,
      );
      break;
    }
    case 'confirm-matrix-write':
      await confirmMatrixWrite();
      break;
    case 'undo-matrix-write':
      await undoMatrixWrite();
      break;
    case 'toggle-custom-sources':
      await setShowsCustomSources();
      break;
    case 'open-settings':
      await openSettings();
      break;
    // The sidebar names a place rather than toggling one, so picking the screen
    // you are already on is a no-op instead of a way back to the matrix.
    case 'select-panel': {
      const panel = target.dataset.panel ?? null;
      if (panel === state.panel) break;
      if (panel === 'secrets') await reloadSecrets();
      if (panel === 'activity') await reloadActivity();
      setState({ panel, selectedClient: null, revealedSecrets: {} });
      break;
    }
    // A different subject on the same screen when one client follows another, so
    // it is its own action rather than a panel that is never a no-op (§7.8).
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
      setState({ secrets: await invoke('secrets.adopt', { secretID: target.dataset.secretId }) });
      break;
    case 'forget-secret':
      setState({ secrets: await invoke('secrets.forget', { secretID: target.dataset.secretId }) });
      break;
    case 'restrict-permissions':
      setState({
        secrets: await invoke('secrets.restrictPermissions', { clientID: target.dataset.clientId }),
        notice: { kind: 'info', message: 'That file can now only be read by you.' },
      });
      break;
    case 'restore-backup':
      await restoreBackup(target.dataset.backupId, target.dataset.clientId);
      break;
    case 'acknowledge-restarts':
      applyState(await invoke('restarts.acknowledge', { clientID: null }));
      break;
    case 'acknowledge-restart':
      applyState(await invoke('restarts.acknowledge', { clientID: target.dataset.clientId ?? null }));
      break;
    case 'dismiss-restart':
      applyState(await invoke('restarts.dismiss', { clientID: target.dataset.clientId ?? null }));
      break;
    case 'dismiss-restarts':
      applyState(await invoke('restarts.dismiss', { clientID: null }));
      break;
    case 'dismiss-notice':
      setState({ notice: null });
      break;
    case 'finish-onboarding':
      setState({ settings: await invoke('onboarding.complete') });
      break;

    case 'refresh-activity':
      await reloadActivity();
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
      setState({
        sheet: {
          kind: 'detail',
          serverId: target.dataset.serverId,
          scrollTop: 0,
          openTools: [],
        },
      });
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
    case 'open-authorization':
      await openAuthorization(target.dataset.serverId);
      break;
    case 'check-all':
      await checkAll();
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
  } catch (error) {
    // Delegated actions that call native directly still need the same visible
    // failure surface as the larger flows; an async event callback otherwise
    // becomes an unhandled rejection.
    reportFailure(error);
  }
});

// Pointer listeners live only for the duration of a drag. The CSS custom
// property moves the edge without rebuilding the state-driven UI.
root.addEventListener('pointerdown', (event) => {
  const handle = event.target.closest?.('[data-action="resize-sidebar"]');
  if (!(handle instanceof HTMLElement) || event.button !== 0) return;

  event.preventDefault();
  const startX = event.clientX;
  const startWidth = currentSidebarWidth();
  handle.setPointerCapture(event.pointerId);

  const move = (moveEvent) => {
    if (moveEvent.pointerId !== event.pointerId) return;
    applySidebarWidth(startWidth + moveEvent.clientX - startX);
  };
  const finish = async (upEvent) => {
    if (upEvent.pointerId !== event.pointerId) return;
    handle.removeEventListener('pointermove', move);
    handle.removeEventListener('pointerup', finish);
    handle.removeEventListener('pointercancel', finish);
    if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
    await commitSidebarWidth(currentSidebarWidth());
  };

  handle.addEventListener('pointermove', move);
  handle.addEventListener('pointerup', finish);
  handle.addEventListener('pointercancel', finish);
});

root.addEventListener('dblclick', async (event) => {
  if (!event.target.closest?.('[data-action="resize-sidebar"]')) return;
  event.preventDefault();
  await commitSidebarWidth(SIDEBAR_DEFAULT);
});

root.addEventListener('input', (event) => {
  const action = event.target.dataset.action;
  if (!action) return;

  const state = getState();
  const sheet = state.sheet;
  const value = event.target.value;
  switch (action) {
    case 'filter': {
      setState({ filter: value });
      break;
    }
    case 'draft-name':
      updateDraft({ name: value });
      break;
    case 'draft-command':
      updateDraft({ command: value });
      break;
    case 'draft-url':
      updateDraft({ url: value });
      break;
    case 'draft-arg': {
      const args = [...sheet.draft.args];
      args[Number(event.target.dataset.index)] = value;
      updateDraft({ args });
      break;
    }
    case 'draft-env-key': {
      const env = [...sheet.draft.env];
      env[Number(event.target.dataset.index)] = { ...env[Number(event.target.dataset.index)], key: value };
      updateDraft({ env });
      break;
    }
    case 'draft-env-value': {
      const env = [...sheet.draft.env];
      env[Number(event.target.dataset.index)] = { ...env[Number(event.target.dataset.index)], value };
      updateDraft({ env });
      break;
    }
    case 'rotate-value':
      updateSheet({ value });
      break;
    case 'profile-name':
      updateSheet({ name: value });
      break;
    case 'profile-budget':
      updateSheet({ tokenBudget: value === '' ? null : Number(value) });
      break;
    case 'agent-import-json':
      updateSheet({ json: value, preview: null, error: null });
      break;
  }
});

root.addEventListener('change', (event) => {
  const sheet = getState().sheet;
  if (event.target.dataset.action === 'draft-target') {
    const clientId = event.target.dataset.clientId;
    const targets = event.target.checked
      ? [...sheet.targets, clientId]
      : sheet.targets.filter((id) => id !== clientId);
    updateSheet({ targets, errors: [] });
  } else if (event.target.dataset.action === 'rotate-secret-store') {
    updateSheet({ storeInSecretStore: event.target.checked });
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
  } else if (
    event.target.dataset.action === 'agent-import-client' ||
    event.target.dataset.action === 'library-client'
  ) {
    const selected = getState().sheet?.clientIDs ?? [];
    const clientIDs = event.target.checked
      ? [...selected, event.target.dataset.clientId]
      : selected.filter((id) => id !== event.target.dataset.clientId);
    updateSheet({ clientIDs: [...new Set(clientIDs)], preview: null, error: null });
  }
});

// Scroll and disclosure state belong to the sheet, just like an unfinished form
// value. Recording them must stay quiet: rendering in response to the scroll
// event would replace the element currently being scrolled and create a loop.
root.addEventListener('scroll', (event) => {
  const sheet = getState().sheet;
  if (!sheet || !event.target.classList?.contains('sheet-body')) return;
  sheet.scrollTop = event.target.scrollTop;
}, true);

root.addEventListener('toggle', (event) => {
  const sheet = getState().sheet;
  const toolName = event.target.dataset?.toolName;
  if (sheet?.kind !== 'detail' || !toolName) return;

  const openTools = new Set(sheet.openTools ?? []);
  if (event.target.open) openTools.add(toolName);
  else openTools.delete(toolName);
  sheet.openTools = [...openTools];
}, true);

document.addEventListener('keydown', (event) => {
  if (event.target instanceof HTMLElement &&
      event.target.dataset.action === 'resize-sidebar' &&
      event.key in { ArrowLeft: true, ArrowRight: true }) {
    event.preventDefault();
    const direction = event.key === 'ArrowLeft' ? -1 : 1;
    void commitSidebarWidth(currentSidebarWidth() + direction * 16);
    return;
  }
  if (event.key === 'Escape' && getState().sheet) {
    setState({ sheet: null });
    return;
  }
  if (event.key !== 'Tab' || !getState().sheet) return;

  const panel = root.querySelector('.sheet');
  if (!panel) return;
  const focusable = [...panel.querySelectorAll(
    'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])',
  )].filter((node) => node instanceof HTMLElement);
  if (focusable.length === 0) {
    event.preventDefault();
    panel.focus();
    return;
  }

  const first = focusable[0];
  const last = focusable[focusable.length - 1];
  if (!panel.contains(document.activeElement)) {
    event.preventDefault();
    first.focus();
  } else if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
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

// Native download progress contains only a safe phase, counters and ratio.
on('updates.progress', (progress) => setState({ updateProgress: progress }));

/// The web layer never decides the theme; it renders what the native side says
/// the user chose, and `system` means "leave it to the OS" (§3).
function applyTheme(theme) {
  document.documentElement.dataset.theme = theme ?? 'system';
}

if (isStubbed) {
  document.body.dataset.stubbed = 'true';
}

load();
