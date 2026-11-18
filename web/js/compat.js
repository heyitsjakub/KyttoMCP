// Reply normalisation for the one place the two native shells spell a command
// differently (§3.2).
//
// The web layer is written against one shape. The macOS and Windows shells grew
// their MCP library, agent import, skills inventory, package lookup and updater
// progress independently, so a few replies arrive in a second shape. This module
// turns that second shape into the first, so nothing outside `ipc.js` has to know
// there are two. It decides by the *shape of the reply*, never by an OS name: a
// reply that is already in the canonical shape passes through untouched.
//
// Canonical shapes (what `screens/tools.js` and `main.js` read):
//   library.chooseDirectory  → { sessionID, displayName } | null
//   library.scan             → { candidates: [{ …, relativeLocation }], warnings }
//   library.preview          → { candidate, targetClientIDs, conflicts, warnings }
//   library.import           → { serverName, changedClientIDs, warnings, state }
//   imports.preview          → { servers, targetClientIDs, conflicts, warnings }
//   imports.import           → { importedServerNames, changedClientIDs, warnings, state }
//   skills.chooseDirectory   → { sessionID, displayName } | null
//   skills.inventory         → { skills: [{ …, locationDisplay, isConflict }], warnings }
//   provenance.checkLatest   → { provenance, state }
//   updates.progress (event) → { phase, progress, bytesReceived, totalBytes, message }

import { getState } from './state.js';

// A candidate the last scan produced, kept so a preview can name it. The second
// shape's preview reply does not repeat the candidate.
const candidates = new Map();

// True once a scan reply arrived in the second shape. That shape's follow-up
// requests take no session token: the "session" is the chosen folder itself.
let pathBasedLibrary = false;

const clientName = (id) => getState().clients.find((client) => client.id === id)?.displayName ?? id;

const list = (value) => (Array.isArray(value) ? value : []);

/** The payload to send, adjusted to what the shell that answered earlier expects. */
export function adaptRequest(command, payload) {
  if (!pathBasedLibrary || !payload) return payload;
  if (command === 'library.preview' || command === 'library.import') {
    const { path: _session, ...rest } = payload;
    return rest;
  }
  return payload;
}

/** The reply, in the canonical shape. */
export function adaptReply(command, payload, reply) {
  switch (command) {
    case 'library.chooseDirectory':
    case 'skills.chooseDirectory':
      return folderChoice(reply);
    case 'library.scan':
      return libraryScan(reply);
    case 'library.preview':
      return libraryPreview(payload, reply);
    case 'library.import':
      return libraryImport(reply);
    case 'imports.preview':
      return importPreview(reply);
    case 'imports.import':
      return importResult(reply);
    case 'skills.inventory':
      return skillsInventory(reply);
    case 'provenance.checkLatest':
      return provenanceResult(reply);
    default:
      return reply;
  }
}

/** `updates.progress` events: one shape for both shells' progress reports. */
export function adaptEvent(event, payload) {
  if (event !== 'updates.progress' || !payload) return payload;
  if (Object.hasOwn(payload, 'fraction') || Object.hasOwn(payload, 'completedBytes')) {
    return {
      phase: payload.phase,
      progress: payload.fraction ?? null,
      bytesReceived: payload.completedBytes ?? 0,
      totalBytes: payload.totalBytes ?? null,
      message: payload.message ?? null,
    };
  }
  return payload;
}

// MARK: - Folder choice

// A folder choice is `{ path }` (null when cancelled) in the second shape. The
// path doubles as the session token the next request carries, and as the name the
// sheet shows: the second shape's native side already answers with a display path.
function folderChoice(reply) {
  if (reply && Object.hasOwn(reply, 'path') && !Object.hasOwn(reply, 'sessionID')) {
    return reply.path ? { sessionID: reply.path, displayName: reply.path } : null;
  }
  return reply;
}

// MARK: - MCP library

function candidate(entry) {
  const { location, ...rest } = entry;
  return { ...rest, relativeLocation: entry.relativeLocation ?? location ?? '' };
}

function libraryScan(reply) {
  if (!reply || !Object.hasOwn(reply, 'rootPathDisplay')) return reply;
  pathBasedLibrary = true;
  candidates.clear();
  const normalised = list(reply.candidates).map(candidate);
  for (const entry of normalised) candidates.set(entry.id, entry);
  return { candidates: normalised, warnings: list(reply.warnings), isComplete: true };
}

// "Conflict" lines for a preview whose per-server conflicts arrive as client ids.
function conflictLines(preview) {
  const lines = [];
  for (const server of list(preview.servers)) {
    const ids = list(server.conflictingClientIDs);
    if (ids.length > 0) {
      lines.push(`“${server.name}” already exists in ${ids.map(clientName).join(', ')}.`);
    }
  }
  if (preview.canImport === false && lines.length === 0) {
    lines.push('Resolve the conflicts before importing.');
  }
  return lines;
}

// Per-server notes the second shape attaches to each entry, flattened.
function serverWarnings(preview) {
  return [
    ...list(preview.warnings),
    ...list(preview.servers).flatMap((server) =>
      list(server.warnings).map((warning) => `${server.name}: ${warning}`)),
  ];
}

function libraryPreview(payload, reply) {
  if (!reply || !Object.hasOwn(reply, 'canImport')) return reply;
  const known = candidates.get(payload?.candidateID);
  return {
    candidate: known ?? {
      id: payload?.candidateID ?? '',
      name: list(reply.servers)[0]?.name ?? '',
      relativeLocation: reply.source ?? '',
      transport: list(reply.servers)[0]?.transport ?? '',
      commandSummary: list(reply.servers)[0]?.commandSummary ?? '',
      environmentKeys: list(reply.servers)[0]?.environmentKeys ?? [],
      detectedClientIDs: [],
      missingClientIDs: [],
      warnings: [],
    },
    targetClientIDs: list(reply.targetClientIDs),
    conflicts: conflictLines(reply),
    warnings: serverWarnings(reply),
  };
}

function libraryImport(reply) {
  if (!reply || !Object.hasOwn(reply, 'serverNames')) return reply;
  return {
    serverName: list(reply.serverNames).join(', '),
    changedClientIDs: list(reply.changed),
    warnings: [],
    state: reply.state,
  };
}

// MARK: - Agent import

function importPreview(reply) {
  if (!reply || !Object.hasOwn(reply, 'canImport')) return reply;
  return {
    servers: list(reply.servers).map((server) => ({
      name: server.name,
      transport: server.transport,
      commandSummary: server.commandSummary,
      environmentKeys: list(server.environmentKeys),
      url: null,
    })),
    targetClientIDs: list(reply.targetClientIDs),
    conflicts: conflictLines(reply),
    warnings: serverWarnings(reply),
  };
}

function importResult(reply) {
  if (!reply || !Object.hasOwn(reply, 'serverNames')) return reply;
  return {
    importedServerNames: list(reply.serverNames),
    changedClientIDs: list(reply.changed),
    warnings: [],
    state: reply.state,
  };
}

// MARK: - Skills

function skillsInventory(reply) {
  if (!reply || !Object.hasOwn(reply, 'entries')) return reply;
  return {
    skills: list(reply.entries).map((entry) => ({
      id: entry.id,
      name: entry.name,
      description: entry.description ?? '',
      locationDisplay: entry.pathDisplay,
      status: entry.metadataStatus,
      warnings: [],
      agent: entry.agent,
      scope: entry.scope,
      scopeLabel: entry.scopeLabel,
      metadataStatus: entry.metadataStatus,
      isDuplicate: entry.isDuplicate === true,
      isConflict: entry.hasConflict === true,
    })),
    warnings: list(reply.warnings),
  };
}

// MARK: - Package lookup

// One shell answers with the fresh state alone; the other wraps it.
function provenanceResult(reply) {
  if (reply && !Object.hasOwn(reply, 'state') && Object.hasOwn(reply, 'servers')) {
    return { provenance: null, state: reply };
  }
  return reply;
}
