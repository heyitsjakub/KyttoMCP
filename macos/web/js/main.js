// Bootstrap: load state from native, render, and route every interaction
// through one delegated listener (§3.1).

import { invoke, on, isStubbed } from './ipc.js';
import {
  setState,
  setRenderer,
  getState,
  setCellBusy,
  cellKey,
  emptyDraft,
  draftFromServer,
  visibleServers,
} from './state.js';
import { render as renderMatrix, restartableClientIds } from './screens/matrix.js';
import { diagnosticReport } from './screens/serverDetail.js';
import { activityReport } from './screens/activity.js';
import { renderLibrary } from './screens/library.js';
import { renderSkills } from './screens/skills.js';
import { formatTokens } from './dom.js';

const root = document.getElementById('app');

setRenderer(() => renderMatrix(root));

// Keep unexpected script failures actionable without placing exception text,
// config values or process output into a report URL (§7.14).
let interfaceFailed = false;
function handleInterfaceFailure() {
  if (interfaceFailed) return;
  interfaceFailed = true;
  setState({
    phase: 'error',
    error: {
      code: 'interfaceFailure',
      message: 'Try again. If the problem continues, quit and reopen Kytto or report a bug.',
    },
    sheet: null,
  });
}
window.addEventListener('error', (event) => {
  if (event instanceof ErrorEvent) handleInterfaceFailure();
});
window.addEventListener('unhandledrejection', handleInterfaceFailure);

async function load() {
  interfaceFailed = false;
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
      // The installed version is no longer "available": left as it was, the
      // banner offered to download it again beside this notice.
      updateDismissed: true,
      // `installed` without a relaunch is a real outcome (DTO.swift), not only
      // the harness's: the new version runs once Kytto is reopened.
      notice: {
        kind: 'info',
        message: result.relaunched
          ? `Kytto ${result.version} was verified and is relaunching.`
          : `Kytto ${result.version} was verified and installed. Quit and reopen Kytto to use it.`,
      },
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
    await reloadActivePanel();
  } catch (error) {
    setState({ notice: { kind: 'error', message: error.message } });
  }
}

/**
 * Panels whose data is not part of the state reply. Without this, Refresh (or a
 * config changed on disk) left Secrets, Profiles and Activity showing what was
 * true before — and acting on a row that no longer exists fails natively.
 */
async function reloadActivePanel() {
  switch (getState().panel) {
    case 'secrets': return reloadSecrets();
    case 'profiles': return reloadProfiles();
    case 'activity': return reloadActivity();
    default: return undefined;
  }
}

/** Applies whatever the native side says the world now looks like. */
function applyState(state) {
  if (!state) return;
  setState(state);
  // A sheet about a server that just disappeared (removed in its client, or
  // renamed elsewhere) renders nothing; left in state it would swallow Escape
  // and keep focus "in a sheet" that is not on screen.
  const { sheet, servers } = getState();
  if (sheet && !sheet.busy && sheet.serverId && !servers.some((server) => server.id === sheet.serverId)) {
    setState({ sheet: null });
  }
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

// Portable setup prompts and secret inputs stay native. The page only sees a
// display-only preview and its opaque, short-lived id.
async function exportSetup() {
  if (getState().setupBusy) return;
  setState({ setupBusy: true });
  try {
    const result = await invoke('setup.export');
    if (!result?.cancelled) setState({ notice: {
      kind: result.warnings?.length ? 'warning' : 'info',
      message: `Exported ${result.serverCount} server${result.serverCount === 1 ? '' : 's'} and ${result.profileCount} profile${result.profileCount === 1 ? '' : 's'}. ${(result.warnings ?? []).join(' ')}`.trim(),
    } });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ setupBusy: false });
  }
}

async function previewSetupImport() {
  if (getState().setupBusy) return;
  setState({ setupBusy: true });
  try {
    const preview = await invoke('setup.previewImport');
    if (preview?.id) setState({ sheet: { kind: 'portableImport', preview, busy: false, error: null } });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ setupBusy: false });
  }
}

async function applySetupImport() {
  const current = getState().sheet;
  if (current?.kind !== 'portableImport' || current.busy) return;
  const request = beginSheetRequest();
  try {
    applyState(await invoke('setup.applyImport', { previewID: current.preview.id }));
    setState({ sheet: null });
    const warning = await refreshAfterMutation({ backups: true, profiles: true });
    setState({ notice: withRefreshWarning({ kind: 'info', message: 'Setup imported. New server definitions are disabled; review each server before enabling it.' }, warning) });
  } catch (error) {
    // The native plan is single-use and is spent even when applying fails, so
    // a retry button here could only ever answer "stale preview".
    setState({
      sheet: null,
      notice: { kind: 'error', message: `${error.message} Nothing was imported. Choose the setup file again to retry.` },
    });
  }
}

function closeCurrentSheet() {
  const current = getState().sheet;
  if (current?.busy) return;
  setState({ sheet: null });
  if (current?.kind === 'portableImport') {
    void invoke('setup.discardImport', { previewID: current.preview.id }).catch(reportFailure);
  }
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
  const targets = state.importTargetIDs ?? defaultImportTargets(state);
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
  const targets = state.importTargetIDs ?? defaultImportTargets(state);
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
  if (current?.busy) return;
  const request = beginSheetRequest();
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
    failSheetRequest(request, error);
  }
}

function defaultProfileClient(state) {
  // Only clients the apply sheet offers: a read-only source here left the
  // select showing one client while state held another, and Apply did nothing.
  const writable = state.clients.filter((client) => !client.isReadOnly && client.state !== 'notInstalled');
  return (
    writable.find((client) => client.state === 'ready') ??
    writable.find((client) => client.state === 'noConfig') ??
    writable.find((client) => client.state === 'orphanedConfig') ??
    null
  );
}

async function applyProfile(profileId, clientId) {
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest({});
  try {
    const result = await invoke('profiles.apply', { profileID: profileId, clientID: clientId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    const changed = result.enabledCount + result.disabledCount;
    const skipped = result.failures.length;
    // A profile is applied one server at a time, so a failure leaves the client
    // between two profiles. The notice says why, and offers both ways out:
    // finish it, or go back to where it started.
    const reason = skipped === 1 ? ` (${firstLine(result.failures[0].message).replace(/\.$/, '')})` : '';
    const skippedNames = `${skipped} server${skipped === 1 ? '' : 's'} skipped — ${result.failures.map((failure) => failure.serverName).join(', ')}${reason}.`;
    const applied = result.applied ?? [];
    const notice = {
      kind: skipped > 0 ? 'warning' : 'info',
      message: skipped > 0
        ? changed > 0
          ? `Applied “${result.profileName}” only in part: ${changed} change${changed === 1 ? '' : 's'} made, ${skippedNames}`
          : `“${result.profileName}” was not applied: ${skippedNames}`
        : changed > 0
          ? `Applied “${result.profileName}”: ${result.enabledCount} enabled, ${result.disabledCount} switched off.`
          : `This client already matched “${result.profileName}”.`,
      ...(skipped > 0
        ? { action: { label: 'Try again', action: 'retry-profile-apply', profileId, clientId } }
        : {}),
      ...(applied.length > 0
        ? {
            secondaryAction: {
              label: skipped > 0 ? 'Undo the changes' : 'Undo',
              action: 'revert-profile-apply',
              clientId,
              changes: JSON.stringify(applied),
            },
          }
        : {}),
    };
    setState({
      sheet: null,
      notice: withRefreshWarning(notice, refreshWarning),
    });
  } catch (error) {
    closeSheetAfterFailure(request, error);
  }
}

/// Applying is idempotent — only what still differs is switched — so trying
/// again after a partial apply finishes it without touching what worked.
async function retryProfileApply(profileId, clientId) {
  setState({ notice: null });
  await refresh();
  try {
    const result = await invoke('profiles.apply', { profileID: profileId, clientID: clientId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    const skipped = result.failures.length;
    setState({
      notice: withRefreshWarning(
        skipped > 0
          ? {
              kind: 'warning',
              message: `“${result.profileName}” is still not fully applied: ${result.failures.map((failure) => `${failure.serverName} (${firstLine(failure.message)})`).join('; ')}.`,
            }
          : { kind: 'info', message: `“${result.profileName}” is now fully applied.` },
        refreshWarning,
      ),
    });
  } catch (error) {
    reportFailure(error);
  }
}

async function revertProfileApply(clientId, changesJSON) {
  let changes;
  try {
    changes = JSON.parse(changesJSON);
  } catch {
    return;
  }
  setState({ notice: null });
  try {
    const result = await invoke('profiles.revertApply', { clientID: clientId, changes });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    const failed = result.failures.length;
    setState({
      notice: withRefreshWarning(
        failed > 0
          ? {
              kind: 'warning',
              message: `Put back ${result.revertedCount} of ${changes.length} changes. Not undone: ${result.failures.map((failure) => `${failure.serverName} (${firstLine(failure.message)})`).join('; ')}.`,
            }
          : {
              kind: 'info',
              message: result.revertedCount > 0
                ? `Undid the profile: ${result.revertedCount} server${result.revertedCount === 1 ? '' : 's'} put back as ${result.revertedCount === 1 ? 'it was' : 'they were'}.`
                : 'Those servers were already changed again, so nothing was undone.',
            },
        refreshWarning,
      ),
    });
  } catch (error) {
    reportFailure(error);
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
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest();
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
    failSheetRequest(request, error);
  }
}

async function restoreGateway(routeId) {
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest();
  try {
    const result = await invoke('gateway.restore', { routeID: routeId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      sheet: null,
      notice: withRefreshWarning(
        restoredNotice(result),
        refreshWarning,
      ),
    });
  } catch (error) {
    failSheetRequest(request, error);
  }
}

/// A restore that had to rebuild the definition says which values are missing:
/// the server is back in Direct mode but will not authenticate until they are
/// entered again.
function restoredNotice(result) {
  const missing = result.missingEnvironmentKeys ?? [];
  if (missing.length === 0) {
    return {
      kind: 'info',
      message: `Restored Direct mode for “${result.route.serverName}”. Restart the client to load the original definition.`,
    };
  }
  return {
    kind: 'warning',
    message: `Restored Direct mode for “${result.route.serverName}”, but the stored original could not be read, so Kytto rebuilt it without ${missing.join(', ')}. Edit the server and enter ${missing.length === 1 ? 'that value' : 'those values'} again.`,
  };
}

async function repairGatewayRoute(routeId) {
  if (getState().gatewayRepairBusyRouteID) return;
  setState({ gatewayRepairBusyRouteID: routeId });
  try {
    const result = await invoke('gateway.repair', { routeID: routeId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    setState({
      notice: withRefreshWarning(
        {
          kind: 'info',
          message: `Repaired the Gateway route for “${result.route.serverName}”. Restart the client to start it through this copy of Kytto.`,
        },
        refreshWarning,
      ),
    });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ gatewayRepairBusyRouteID: null });
  }
}

async function restoreAllGateway() {
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest();
  try {
    const result = await invoke('gateway.restoreAll');
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true });
    const parts = [`Restored Direct mode for ${result.restoredCount} ${result.restoredCount === 1 ? 'server' : 'servers'}.`];
    for (const entry of result.missingEnvironment) {
      parts.push(`“${entry.serverName}” was rebuilt without ${entry.keys.join(', ')} — edit it and enter ${entry.keys.length === 1 ? 'that value' : 'those values'} again.`);
    }
    for (const failure of result.failures) {
      parts.push(`“${failure.serverName}” was not restored: ${failure.message}`);
    }
    const clean = result.failures.length === 0 && result.missingEnvironment.length === 0;
    if (clean) parts.push('Restart the clients to load the original definitions.');
    setState({
      sheet: null,
      notice: withRefreshWarning({ kind: clean ? 'info' : 'warning', message: parts.join(' ') }, refreshWarning),
    });
  } catch (error) {
    failSheetRequest(request, error);
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
  // Counted against the tools the server offers now. The saved list can still
  // name a tool the server has since dropped, and subtracting those produced
  // "hiding -1 of 2".
  const hidden = toolNames === null ? 0 : names.filter((name) => !toolNames.includes(name)).length;

  if (sheet.busy) return;
  const request = beginSheetRequest();
  try {
    applyState(await invoke('gateway.setExposedTools', { routeID: routeId, toolNames }));
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
    failSheetRequest(request, error);
  }
}

// MARK: - Tool switches in a client's own file (§7.11)

/// Opens the picker with what the client keeps today, so saving without
/// touching anything writes nothing.
function openToolSwitch(serverId, clientId) {
  const server = getState().servers.find((entry) => entry.id === serverId);
  if (!server) return;
  const off = new Set(server.toolsOff?.[clientId] ?? []);
  const names = (server.health?.tools ?? []).map((tool) => tool.name);
  setState({
    sheet: {
      kind: 'toolSwitch',
      serverId,
      clientId,
      selected: names.filter((name) => !off.has(name)),
      busy: false,
      error: null,
    },
  });
}

async function saveToolSwitch(serverId, clientId) {
  const sheet = getState().sheet;
  if (sheet.busy) return;
  const server = getState().servers.find((entry) => entry.id === serverId);
  const tools = server?.health?.tools ?? [];
  const previous = server?.toolsOff?.[clientId] ?? [];
  const disabledTools = tools.map((tool) => tool.name).filter((name) => !sheet.selected.includes(name));
  const request = beginSheetRequest();
  try {
    const result = await invoke('servers.setDisabledTools', { serverID: serverId, clientID: clientId, disabledTools });
    applyState(result.state);
    setState({ sheet: null, notice: toolSwitchNotice(serverId, result, tools, previous) });
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
  } catch (error) {
    failSheetRequest(request, error);
  }
}

function toolSwitchNotice(serverId, result, tools, previous, { undoing = false } = {}) {
  const clientName = getState().clients.find((entry) => entry.id === result.clientID)?.displayName ?? result.clientID;
  if (!result.requiresRestart) {
    return { kind: 'info', message: `Nothing changed for “${result.serverName}” in ${clientName}.` };
  }
  const off = new Set(result.disabledTools);
  const saved = tools
    .filter((tool) => off.has(tool.name) && tool.tokenCount != null)
    .reduce((total, tool) => total + tool.tokenCount, 0);
  const what = off.size === 0
    ? `All tools of “${result.serverName}” are on in ${clientName} again.`
    : `${off.size} of ${tools.length} tools of “${result.serverName}” are off in ${clientName}${saved > 0 ? `, about ${formatTokens(saved)} tokens less context` : ''}.`;
  return {
    kind: 'info',
    message: `${undoing ? 'Undid the change. ' : ''}${what}${result.backupID ? ' A backup was created first.' : ''} Restart ${clientName} to apply it.`,
    action: undoing
      ? undefined
      : {
          label: 'Undo',
          action: 'undo-tool-switch',
          serverId,
          clientId: result.clientID,
          disabledTools: JSON.stringify(previous),
        },
  };
}

async function undoToolSwitch(serverId, clientId, disabledTools) {
  const server = getState().servers.find((entry) => entry.id === serverId);
  try {
    const result = await invoke('servers.setDisabledTools', { serverID: serverId, clientID: clientId, disabledTools });
    applyState(result.state);
    setState({ notice: toolSwitchNotice(serverId, result, server?.health?.tools ?? [], [], { undoing: true }) });
    const refreshWarning = await refreshAfterMutation({ backups: true });
    if (refreshWarning) setState({ notice: withRefreshWarning(getState().notice, refreshWarning) });
  } catch (error) {
    if (reportFailure(error)) await refresh();
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
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest();
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
    // Never an empty list after the colon.
    if (parts.length === 0) parts.push('no file needed changing');
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
    failSheetRequest(request, error);
  }
}

// MARK: - MCP Doctor

async function previewDoctorFix(serverId, fixAction = 'pinResolvedCommand') {
  try {
    const preview = await invoke('doctor.previewFix', { serverID: serverId, action: fixAction });
    setState({ sheet: { kind: 'doctorFix', serverId, preview, busy: false, error: null } });
  } catch (error) {
    reportFailure(error);
  }
}

async function applyDoctorFix(serverId) {
  // The version travels back so the native side writes only the release this
  // preview showed, not whatever a later lookup found.
  const { preview, busy } = getState().sheet;
  if (busy) return;
  const request = beginSheetRequest();
  try {
    const result = await invoke('doctor.applyFix', {
      serverID: serverId,
      action: preview.action,
      version: preview.version ?? null,
    });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ backups: true, profiles: true });
    const parkedFailures = result.parkedFailures ?? [];
    const message = preview.action !== 'pinPackageVersion'
      ? `Pinned the verified executable for “${result.serverName}”. Affected configurations were backed up first.`
      : parkedFailures.length > 0
        ? `Pinned ${preview.packageName} to ${preview.version} for “${result.serverName}”, but Kytto could not update the stored off copy for ${parkedFailures.join(', ')}. Switching it on there would bring back the unpinned version.`
        : `Pinned ${preview.packageName} to ${preview.version} for “${result.serverName}”. Affected configurations were backed up first.`;
    setState({
      sheet: null,
      notice: withRefreshWarning(
        { kind: parkedFailures.length > 0 ? 'warning' : 'info', message },
        refreshWarning,
      ),
    });
  } catch (error) {
    failSheetRequest(request, error);
  }
}

// A sheet's request can outlive the sheet: a configs.changed push can rebuild
// the world under it. Each busy request carries a token, so a failure lands on
// the sheet that started it — or, when that sheet is gone, as a notice instead
// of resurrecting a sheet with no kind that swallows the error.
let sheetRequestSerial = 0;

/** Marks the open sheet busy and returns the token naming this request. */
function beginSheetRequest(changes = { error: null }) {
  const request = ++sheetRequestSerial;
  setState({ sheet: { ...getState().sheet, ...changes, busy: true, request } });
  return request;
}

/** True while the sheet on screen is still the one that started `request`. */
const sheetStillOpen = (request) => getState().sheet?.request === request;

/** Puts a failure back on the sheet that asked, or reports it if it is gone. */
function failSheetRequest(request, error, changes = { error: error.message }) {
  if (sheetStillOpen(request)) {
    setState({ sheet: { ...getState().sheet, ...changes, busy: false } });
  } else {
    reportFailure(error);
  }
}

/** For confirmations that close on failure and say why in a notice. */
function closeSheetAfterFailure(request, error) {
  const changedOnDisk = reportFailure(error);
  if (sheetStillOpen(request)) setState({ sheet: null });
  return changedOnDisk;
}

/**
 * Whether a write was refused because the config changed underneath Kytto.
 * Decided by the error's code. The wording is only a fallback for a shell that
 * does not send the code yet, so rephrasing the message cannot turn this
 * recoverable case into a plain error.
 */
function isChangedOnDisk(error) {
  return error.code === 'changedOnDisk' || /changed on disk/i.test(error.message ?? '');
}

/**
 * Turns a write failure into something the user can act on.
 * @param {{ label: string, action: string }} [retry] what the notice offers
 *   when the file changed on disk; by default, a refresh.
 */
function reportFailure(error, retry = null) {
  const changedOnDisk = isChangedOnDisk(error);
  setState({
    notice: {
      kind: changedOnDisk ? 'warning' : 'error',
      message: changedOnDisk
        ? `${error.message} Kytto did not write anything.${retry ? ' The current version is loaded now.' : ' Refresh to load the current version, then try again.'}`
        : error.message,
      ...(changedOnDisk ? { action: retry ?? { label: 'Refresh', action: 'refresh-after-conflict' } } : {}),
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
    // The file changed underneath: load what is there now, and offer the same
    // switch again against it rather than leaving the user to find the cell.
    const retry = {
      label: 'Try again',
      action: 'retry-toggle',
      serverId,
      clientId,
      enable: String(!currentlyEnabled),
    };
    if (reportFailure(error, undoing ? null : retry)) await refresh();
  } finally {
    setCellBusy(serverId, clientId, false);
  }
}

/// Repeats a switch that was refused because the config changed on disk. The
/// re-read may have made it unnecessary — the other edit could be this one.
async function retryToggle(serverId, clientId, enable) {
  const state = getState();
  const server = state.servers.find((entry) => entry.id === serverId);
  if (!server) {
    setState({ notice: { kind: 'warning', message: 'That server is no longer in any configuration, so there is nothing to switch.' } });
    return;
  }
  const enabledNow = server.enabledIn[clientId] === 'enabled';
  if (enabledNow === enable) {
    const client = state.clients.find((entry) => entry.id === clientId)?.displayName ?? clientId;
    setState({ notice: { kind: 'info', message: `“${server.name}” is already ${enable ? 'on' : 'off'} in ${client}, so nothing was changed.` } });
    return;
  }
  await toggleCell(serverId, clientId, enabledNow);
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

// MARK: - Restart now (§7.1)

function openRestartConfirm(clientIds) {
  const ids = clientIds.filter(Boolean);
  if (ids.length === 0) return;
  setState({ sheet: { kind: 'confirmRestart', clientIds: ids, error: null } });
}

async function confirmRestart() {
  const current = getState().sheet;
  if (current?.kind !== 'confirmRestart' || current.busy) return;
  const request = beginSheetRequest();
  try {
    const result = await invoke('restarts.restart', { clientIDs: current.clientIds });
    applyState(result.state);
    setState({ sheet: null, notice: restartNotice(result) });
    await verifyAfterRestart(result);
  } catch (error) {
    failSheetRequest(request, error);
  }
}

/**
 * A restart only proves the app came back. Whether the definition it loaded
 * actually starts is a separate question, and the client answers it somewhere
 * the user is not looking — so Kytto checks the servers that changed and says
 * so in the same notice. Kytto starts them itself; it cannot see inside the
 * client.
 */
async function verifyAfterRestart(result) {
  const serverIds = [...new Set((result.verify ?? []).flatMap((entry) => entry.serverIDs))];
  if (serverIds.length === 0 || getState().healthProgress) return;
  const base = restartNotice(result);
  const failed = [];
  const waiting = [];
  let checked = 0;
  for (const serverId of serverIds) {
    if (getState().checking.has(serverId)) continue;
    setChecking(serverId, true);
    try {
      const reply = await invoke('health.check', { serverID: serverId });
      applyState(reply.state);
      checked += 1;
      const name = getState().servers.find((entry) => entry.id === serverId)?.name ?? serverId;
      if (reply.health.status === 'failed') failed.push({ serverId, name });
      else if (reply.health.status === 'needsAuthorization') waiting.push(name);
    } catch {
      // A check that could not run says nothing about the server either way.
    } finally {
      setChecking(serverId, false);
    }
  }
  if (checked === 0) return;

  const quoted = (names) => names.map((name) => `“${name}”`).join(', ');
  const parts = [base.message];
  const passed = checked - failed.length - waiting.length;
  if (failed.length === 0) {
    if (passed > 0) {
      parts.push(`Kytto checked the ${passed === 1 ? 'server that changed, and it starts' : `${passed} servers that changed, and they start`}.`);
    }
  } else {
    parts.push(`${quoted(failed.map((entry) => entry.name))} did not start when Kytto checked ${failed.length === 1 ? 'it' : 'them'} after the restart, so the client is probably showing ${failed.length === 1 ? 'it' : 'them'} as failed.`);
  }
  if (waiting.length > 0) parts.push(`${quoted(waiting)} ${waiting.length === 1 ? 'is' : 'are'} waiting for a sign-in.`);
  setState({
    notice: {
      kind: failed.length > 0 ? 'warning' : base.kind,
      message: parts.join(' '),
      ...(failed.length > 0
        ? { action: { label: failed.length === 1 ? 'Open server' : `Open “${failed[0].name}”`, action: 'open-server', serverId: failed[0].serverId } }
        : {}),
    },
  });
}

function restartNotice(result) {
  const name = (id) => getState().clients.find((client) => client.id === id)?.displayName ?? id;
  const names = (ids) => {
    const list = ids.map(name);
    return list.length <= 1 ? list.join('') : `${list.slice(0, -1).join(', ')} and ${list[list.length - 1]}`;
  };
  const parts = [];
  if (result.restarted.length > 0) parts.push(`Restarted ${names(result.restarted)}.`);
  if (result.notRunning.length > 0) {
    const single = result.notRunning.length === 1;
    parts.push(`${names(result.notRunning)} ${single ? 'was' : 'were'} not running and will read the new configuration when opened.`);
  }
  for (const failure of result.failures) parts.push(failure.message);
  return { kind: result.failures.length > 0 ? 'error' : 'info', message: parts.join(' ') };
}

async function confirmToggle(serverId, clientId, currentlyEnabled) {
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest();
  try {
    const settings = await invoke('settings.confirmMatrixWrites');
    setState({ settings, sheet: null });
    await toggleCell(serverId, clientId, currentlyEnabled);
  } catch (error) {
    failSheetRequest(request, error);
  }
}

async function undoToggle(action) {
  // A second click while the first undo is still in flight must not send it twice.
  if (getState().busyCells.has(cellKey(action.serverId, action.clientId))) return;
  const server = getState().servers.find((entry) => entry.id === action.serverId);
  const current = server?.enabledIn[action.clientId] ?? 'absent';
  if (current === action.previousState) {
    setState({ notice: { kind: 'info', message: 'That configuration is already back in its previous state.' } });
    return;
  }

  // Adding an absent server has no inverse toggle: switching it off would leave
  // a parked/disabled definition. Remove just that definition instead. Restoring
  // the whole pre-write backup would also undo everything the client or the user
  // changed in that file since — a notice can be clicked hours later.
  if (action.previousState === 'absent') {
    setCellBusy(action.serverId, action.clientId, true);
    try {
      const restored = await invoke('servers.removeFromClient', {
        serverID: action.serverId,
        clientID: action.clientId,
      });
      applyState(restored.state ?? restored);
      const refreshWarning = await refreshAfterMutation({ backups: true });
      setState({
        notice: withRefreshWarning(
          {
            kind: 'info',
            message: `Undid the change: “${server?.name ?? action.serverId}” was removed from that client again.`,
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
  if (sheet.busy) return;
  const request = beginSheetRequest({ errors: [] });

  // Empty values mean "unchanged" when editing, and nothing at all when
  // creating. Either way an empty string would overwrite a real secret.
  const env = sheet.draft.env
    .filter((entry) => entry.key.trim() !== '')
    .map((entry) => ({
      key: entry.key,
      value: entry.value === '' || entry.value === null ? null : entry.value,
      previousKey: entry.originalKey && entry.originalKey !== entry.key ? entry.originalKey : null,
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
    failSheetRequest(request, error, { errors: [error.message] });
  }
}

async function confirmDelete(serverId) {
  // Busy until the reply lands: a second click would ask native to delete a
  // server that is already gone and report "No such server".
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest({});
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
    closeSheetAfterFailure(request, error);
  }
}

async function confirmRemoveFromClient(serverId, clientId) {
  // The client's name is read before the reply lands, because a removal that
  // empties the last row takes the client's own entry out of the state with it.
  const clientName =
    getState().clients.find((entry) => entry.id === clientId)?.displayName ?? clientId;
  if (getState().sheet?.busy) return;
  const request = beginSheetRequest({});
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
    closeSheetAfterFailure(request, error);
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
  if (sheet.busy) return;
  const request = beginSheetRequest();
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
    failSheetRequest(request, error);
  }
}

// MARK: - Health

function setChecking(serverId, busy) {
  const next = new Set(getState().checking);
  if (busy) next.add(serverId);
  else next.delete(serverId);
  setState({ checking: next });
}

/// Contract alerts present now, keyed so a fresh change to a server that was
/// already flagged counts as new too.
const contractAlertKeys = () =>
  new Set((getState().contractAlerts ?? []).map((alert) => `${alert.serverID}@${alert.changedAt}`));

/// Alerts the last reply raised that were not on screen before it.
function newContractAlerts(before) {
  return (getState().contractAlerts ?? []).filter(
    (alert) => !before.has(`${alert.serverID}@${alert.changedAt}`),
  );
}

/// A passing check used to clear the notice, so a check that *raised* a
/// contract alert was announced only by a small badge on the row.
function contractNotice(alerts) {
  if (alerts.length === 0) return null;
  if (alerts.length === 1) {
    return {
      kind: 'warning',
      message: `“${alerts[0].serverName}” changed what it tells the model — review it in the server’s details.`,
    };
  }
  return {
    kind: 'warning',
    message: `${alerts.length} servers changed what they tell the model — review each in its server’s details.`,
  };
}

async function checkServer(serverId) {
  // "Check all" already covers this server, and two runs racing would each
  // write the notice — a passing single check erased the run's contract report.
  if (getState().checking.has(serverId) || getState().healthProgress) return;
  setChecking(serverId, true);
  const alertsBefore = contractAlertKeys();
  try {
    const result = await invoke('health.check', { serverID: serverId });
    applyState(result.state);
    const refreshWarning = await refreshAfterMutation({ profiles: true });
    const contract = contractNotice(
      newContractAlerts(alertsBefore).filter((alert) => alert.serverID === serverId),
    );
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
      setState({ notice: withRefreshWarning(contract, refreshWarning) });
    }
  } catch (error) {
    reportFailure(error);
  } finally {
    setChecking(serverId, false);
  }
}

async function checkAll() {
  // The Doctor and the matrix toolbar both offer this; one run at a time.
  if (getState().healthProgress) return;
  const alertsBefore = contractAlertKeys();
  setState({ healthProgress: { serverName: '', index: 0, total: 0 }, notice: null });
  try {
    applyState(await invoke('health.checkAll'));
    const refreshWarning = await refreshAfterMutation({ profiles: true });
    const contract = contractNotice(newContractAlerts(alertsBefore));
    if (refreshWarning || contract) setState({ notice: withRefreshWarning(contract, refreshWarning) });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ healthProgress: null });
  }
}

async function checkVisible() {
  if (getState().healthProgress || getState().checking.size) return;
  const servers = [...visibleServers()];
  if (!servers.length) return;
  const alertsBefore = contractAlertKeys();
  const failed = [];
  const waiting = [];
  setState({ healthProgress: { serverName: '', index: 0, total: servers.length }, notice: null });
  try {
    for (const [index, server] of servers.entries()) {
      setState({ healthProgress: { serverName: server.name, index: index + 1, total: servers.length } });
      const result = await invoke('health.check', { serverID: server.id });
      applyState(result.state);
      if (result.health.status === 'failed') failed.push(server.name);
      if (result.health.status === 'needsAuthorization') waiting.push(server.name);
    }
    const warning = await refreshAfterMutation({ profiles: true });
    const contract = contractNotice(newContractAlerts(alertsBefore));
    const messages = [`Checked ${servers.length} visible server${servers.length === 1 ? '' : 's'}.`];
    if (failed.length) messages.push(`${failed.length} failed; open their details for the reason.`);
    if (waiting.length) messages.push(`${waiting.length} ${waiting.length === 1 ? 'requires' : 'require'} sign-in.`);
    if (contract) messages.push(contract.message);
    setState({ notice: withRefreshWarning({ kind: failed.length || waiting.length || contract ? 'warning' : 'info', message: messages.join(' ') }, warning) });
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

/// Asks osv.dev (and, for npm, the registry) about one server's package —
/// only on this click (§7.10). The findings land in Doctor; the notice says
/// the verdict in one line.
async function checkPackageSafety(serverId) {
  if (getState().packageSafetyBusyServerID) return;
  setState({ packageSafetyBusyServerID: serverId, notice: null });
  try {
    applyState(await invoke('doctor.checkPackageSafety', { serverID: serverId }));
    const server = getState().servers.find((entry) => entry.id === serverId);
    const safety = server?.packageSafety;
    let message = `Checked ${safety?.packageName ?? server?.name ?? 'the package'}: no known malware`;
    if (safety?.malware) {
      message = `${safety.packageName} is listed as malware (${safety.advisoryIDs.filter((id) => id.startsWith('MAL-')).join(', ')}). Switch it off and see MCP Doctor.`;
    } else {
      if (safety?.advisoryIDs.length) message += `, ${safety.advisoryIDs.length} known vulnerabilit${safety.advisoryIDs.length === 1 ? 'y' : 'ies'}`;
      if (safety?.installScripts.length) message += `, runs ${safety.installScripts.join(' and ')} on install`;
      message += '. Details are in MCP Doctor.';
    }
    // A finding about the package, not a failure of Kytto: warning, never the
    // error banner that offers to report a bug.
    setState({ notice: { kind: safety?.malware || safety?.advisoryIDs.length || safety?.installScripts.length ? 'warning' : 'info', message } });
  } catch (error) {
    reportFailure(error);
  } finally {
    setState({ packageSafetyBusyServerID: null });
  }
}

const firstLine = (text) => String(text).split('\n')[0];

async function restoreBackup(backupId, clientId) {
  // One revert at a time: a double click wrote the same backup twice, and two
  // different reverts racing on one client leave whichever lands last.
  if (getState().restoringBackupId) return;
  setState({ restoringBackupId: backupId });
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
  } finally {
    setState({ restoringBackupId: null });
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
// Bumped on every sidebar navigation; see `select-panel`.
let panelNavigation = 0;

// Where the press that ends in a click began; see `close-sheet`.
let pointerDownOnBackdrop = false;
root.addEventListener('pointerdown', (event) => {
  pointerDownOnBackdrop = event.target instanceof Element && event.target.classList.contains('sheet-backdrop');
}, true);

root.addEventListener('pointerdown', (event) => {
  const target = event.target.closest('[data-action="resize-sidebar"]');
  if (!target || event.button !== 0) return;
  event.preventDefault();

  const startX = event.clientX;
  const startWidth = getState().settings?.sidebarWidth ?? DEFAULT_SIDEBAR_WIDTH;
  const widthAt = (pointer) => clampSidebarWidth(startWidth + (pointer.clientX - startX));

  // On the window, not the handle: any render during the drag (a notice
  // timing out, a health-progress push) replaces the handle element, which
  // takes its pointer capture and listeners with it and freezes the drag.
  target.classList.add('dragging');
  document.body.classList.add('resizing-sidebar');
  const move = (pointer) => applySidebarWidth(widthAt(pointer));
  const finish = (pointer) => {
    window.removeEventListener('pointermove', move);
    window.removeEventListener('pointerup', finish);
    window.removeEventListener('pointercancel', finish);
    document.body.classList.remove('resizing-sidebar');
    root.querySelector('[data-action="resize-sidebar"]')?.classList.remove('dragging');
    void commitSidebarWidth(widthAt(pointer));
  };
  window.addEventListener('pointermove', move);
  window.addEventListener('pointerup', finish);
  window.addEventListener('pointercancel', finish);
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
      if (state.busyCells.has(cellKey(target.dataset.serverId, target.dataset.clientId))) break;
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
    case 'refresh-after-conflict':
      setState({ notice: null });
      await refresh();
      if (!getState().notice) {
        setState({ notice: { kind: 'info', message: 'Loaded the current configuration. Make your change again.' } });
      }
      break;
    case 'retry-profile-apply':
      await retryProfileApply(target.dataset.profileId, target.dataset.clientId);
      break;
    case 'revert-profile-apply':
      await revertProfileApply(target.dataset.clientId, target.dataset.changes);
      break;
    case 'retry-toggle':
      await retryToggle(target.dataset.serverId, target.dataset.clientId, target.dataset.enable === 'true');
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
      // A slow load (a Skills scan) must not take the window back from a panel
      // the person chose while it ran: only the latest choice lands.
      const navigation = ++panelNavigation;
      try {
        if (panel === 'secrets') await reloadSecrets();
        if (panel === 'activity') await reloadActivity();
        if (panel === 'library') await openLibraryPanel();
        if (panel === 'skills') await refreshSkills();
      } catch (error) {
        if (navigation === panelNavigation) reportFailure(error);
        break;
      }
      if (navigation !== panelNavigation) break;
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
    case 'restart-client':
      openRestartConfirm([target.dataset.clientId]);
      break;
    case 'restart-clients':
      openRestartConfirm(restartableClientIds(getState()));
      break;
    case 'confirm-restart':
      await confirmRestart();
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
      if (state.acknowledgingContract) break;
      setState({ acknowledgingContract: target.dataset.serverId });
      try {
        applyState(await invoke('contract.acknowledge', {
          serverID: target.dataset.serverId,
          reviewToken: target.dataset.reviewToken || null,
        }));
        setState({
          notice: {
            kind: 'info',
            message: 'Marked as reviewed. Kytto will report the next change against this contract.',
          },
        });
      } catch (error) {
        reportFailure(error);
      } finally {
        setState({ acknowledgingContract: null });
      }
      break;
    case 'unify-server':
      await openUnifyPreview(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'confirm-unify':
      await confirmUnify(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'doctor-preview-fix':
      await previewDoctorFix(target.dataset.serverId, target.dataset.fixAction);
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
      if (sheet?.busy) break;
      const request = beginSheetRequest({});
      try {
        const profiles = await invoke('profiles.delete', { profileID: target.dataset.profileId });
        setState({ profiles, sheet: null, notice: { kind: 'info', message: 'Profile deleted. Client configurations were not changed.' } });
      } catch (error) {
        closeSheetAfterFailure(request, error);
      }
      break;
    }

    case 'open-server':
      if (state.panel === null) setState({ selectedServerId: target.dataset.serverId });
      else setState({ sheet: { kind: 'detail', serverId: target.dataset.serverId }, selectedServerId: target.dataset.serverId });
      break;
    case 'open-server-modal':
      setState({ sheet: { kind: 'detail', serverId: target.dataset.serverId } });
      break;
    case 'close-inspector':
      setState({ selectedServerId: null });
      break;
    case 'reset-matrix-filters':
      setState({ filter: '', scopeFilter: 'all', sourceFilter: 'all' });
      break;
    case 'export-setup':
      await exportSetup();
      break;
    case 'preview-setup-import':
      await previewSetupImport();
      break;
    case 'apply-setup-import':
      await applySetupImport();
      break;
    case 'close-sheet':
      // Only the backdrop itself closes; clicks inside the panel bubble up here
      // too and must be ignored. So must a drag that started inside the panel
      // (selecting text) and ended over the backdrop: the click then lands on
      // their common ancestor, the backdrop, and used to discard the draft.
      if (target.classList.contains('sheet-backdrop') && (event.target !== target || !pointerDownOnBackdrop)) break;
      // A sheet with a write in flight stays until the reply lands, so its
      // outcome has somewhere to go.
      if (sheet?.busy) break;
      closeCurrentSheet();
      break;

    case 'check-server':
      await checkServer(target.dataset.serverId);
      break;
    case 'sign-in-remote': {
      const serverID = target.dataset.serverId;
      if (state.checking.has(serverID) || state.healthProgress) break;
      setChecking(serverID, true);
      try {
        applyState(await invoke('health.signInRemote', { serverID }));
        setState({ notice: { kind: 'info', message: 'Sign-in dialog closed. The server detail shows the latest check.' } });
      } catch (error) {
        reportFailure(error);
      } finally {
        setChecking(serverID, false);
      }
      break;
    }
    case 'forget-remote-sign-in':
      try {
        await invoke('health.forgetRemoteAuthorization', { serverID: target.dataset.serverId });
        setState({ notice: { kind: 'info', message: 'Kytto’s saved authorization was removed from the credential store.' } });
      } catch (error) {
        reportFailure(error);
      }
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
    case 'check-visible':
      await checkVisible();
      break;
    case 'check-provenance-latest':
      await checkProvenanceLatest(target.dataset.serverId);
      break;
    case 'check-package-safety':
      await checkPackageSafety(target.dataset.serverId);
      break;
    case 'copy-diagnostic': {
      const server = state.servers.find((entry) => entry.id === target.dataset.serverId);
      if (!server?.health) break;
      try {
        await invoke('clipboard.writeText', { text: diagnosticReport(server, state) });
        setState({ notice: { kind: 'info', message: `Copied the diagnostic report for “${server.name}”. Secret values, raw server output and URL paths and queries were omitted.` } });
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
    case 'repair-gateway':
      await repairGatewayRoute(target.dataset.routeId);
      break;
    case 'restore-all-gateway':
      setState({ sheet: { kind: 'gatewayRestoreAll', busy: false, error: null } });
      break;
    case 'confirm-restore-all-gateway':
      await restoreAllGateway();
      break;
    case 'choose-tools':
      openToolMask(target.dataset.routeId);
      break;
    case 'confirm-tool-mask':
      await saveToolMask(target.dataset.routeId);
      break;
    case 'choose-client-tools':
      openToolSwitch(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'confirm-tool-switch':
      await saveToolSwitch(target.dataset.serverId, target.dataset.clientId);
      break;
    case 'undo-tool-switch':
      await undoToolSwitch(target.dataset.serverId, target.dataset.clientId, JSON.parse(target.dataset.disabledTools ?? '[]'));
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

// The composed text lands with compositionend; WebKit fires its last `input`
// while still composing, so the filter is applied here as well.
root.addEventListener('compositionend', (event) => {
  if (event.target.dataset?.action === 'filter') setState({ filter: event.target.value });
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
      // Mid-composition (a dead key for č or ô, an IME) the render would
      // replace the field under the composition; the final input after
      // compositionend carries the finished text.
      if (event.isComposing) break;
      setState({ filter: value });
      break;
    }
    case 'import-json': {
      // As with the form fields, keep the caret in the native state without
      // rebuilding the tree on every pasted character. Render only when what is
      // on screen depends on the edit: the Preview button enables once there is
      // text, and a preview of the old text must not stay up to be imported.
      const wasEmpty = !(state.importText ?? '').trim();
      const stale = state.importPreview || state.importError;
      if (!stale && wasEmpty === !value.trim()) {
        state.importText = value;
        break;
      }
      const caret = event.target.selectionStart;
      setState({ importText: value, importPreview: null, importError: null });
      const field = root.querySelector('[data-action="import-json"]');
      if (field) {
        field.focus();
        field.setSelectionRange(caret, caret);
      }
      break;
    }
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
      // Whole, non-negative tokens; the native payload is an integer.
      sheet.tokenBudget = value === '' || !Number.isFinite(Number(value)) ? null : Math.max(0, Math.round(Number(value)));
      break;
  }
});

root.addEventListener('change', (event) => {
  const action = event.target.dataset.action;
  const sheet = getState().sheet;
  if (action === 'scope-filter') {
    setState({ scopeFilter: event.target.value });
  } else if (action === 'source-filter') {
    setState({ sourceFilter: event.target.value });
  } else if (action === 'row-density') {
    setState({ density: event.target.value });
  } else if (action === 'library-target') {
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
    const current = state.importTargetIDs ?? defaultImportTargets(state);
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

// `toggle` does not bubble, so the one listener for it listens in the capture
// phase. Which tools are expanded lives in state so a re-render (a copied
// report, a configs.changed push) does not fold them all shut. The DOM already
// shows the user's toggle, so state is updated without a render — the same
// exception the text fields make.
root.addEventListener(
  'toggle',
  (event) => {
    const details = event.target;
    if (details?.dataset?.action !== 'toggle-tool-details') return;
    const key = `${details.dataset.serverId}::${details.dataset.toolName}`;
    const state = getState();
    if (details.open === state.openTools.has(key)) return;
    const next = new Set(state.openTools);
    if (details.open) next.add(key);
    else next.delete(key);
    state.openTools = next;
  },
  true,
);

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && getState().sheet) {
    event.preventDefault();
    // Same rule as the Close button: not while the sheet is waiting on a write.
    if (!getState().sheet.busy) closeCurrentSheet();
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

  const modal = root.querySelector('.sheet-backdrop:not([inert]) .sheet[role="dialog"]');
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
on('configs.changed', (state) => {
  applyState(state);
  reloadActivePanel().catch(() => {});
});

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
