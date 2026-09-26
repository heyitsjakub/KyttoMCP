# IPC contract

The single channel between the web UI and the native shell. Every native
capability is one named command; the web layer has no other way to reach the
machine.

It was written for the macOS app first, and the Windows port (milestone M7)
reimplements it against Windows APIs. If the Windows port needs the web layer
changed, something here described an implementation instead of an intent — see
[`design.md`](design.md) §3.2. Every `§` reference below points at that document.

## Rules

1. **Commands describe intent, not implementation.** `secrets.store(ref, value)`,
   never `keychain.addGenericPassword(...)`.
2. **No path the web layer has to interpret.** Native sends display strings and
   opaque ids. Path construction, separators and variable expansion stay native.
3. **No platform names cross the boundary.** The UI branches on capabilities
   reported by `app.info`, never on the name of an OS.
4. **Secrets do not cross.** Environment variables travel as key plus a
   `hasValue` flag. Values stay native until they are injected at spawn time.
5. **Every command added here gets a row in this file in the same commit.**

## Transport

Web to native, from `web/js/ipc.js`:

```js
const raw = await window.webkit.messageHandlers.kytto.postMessage({ command, payload });
```

The reply is a JSON **string**, parsed by `ipc.js`. A string rather than a
bridged object so what JavaScript receives is exactly what the native side
encoded, with no type coercion in between.

That is WebKit's shape (`WKScriptMessageHandlerWithReply`). WebView2's
`chrome.webview.postMessage` is one-way, so on Windows
`src/Kytto.App/Web/bridge.js` is injected before the page's modules and supplies
the same `window.webkit.messageHandlers.kytto` object, pairing each reply with
its request by id. `web/` itself carries no Windows-specific transport code.

Envelope:

```json
{ "ok": true, "data": ... }
{ "ok": false, "error": { "code": "…", "message": "…" } }
```

Failures are data. `invoke()` turns them into an `IPCError`; the UI renders an
error state rather than guessing at a rejected promise.

Native to web, from `CommandRouter.emit`:

```js
window.__kytto.emit(eventName, payload);
```

Subscribe with `on(event, handler)` from `ipc.js`.

## Commands

### `app.info` → `AppInfoDTO`

No payload.

```json
{
  "version": "1.0.6",
  "build": "1",
  "capabilities": ["config.read"],
  "platformDisplayName": "macOS",
  "secretStoreDisplayName": "Keychain"
}
```

`capabilities` is what the UI branches on. Each milestone adds its own flag:
`config.write` (M2), `secrets` (M4), `health` (M5), plus `profiles`,
`inspector`, `clipboard` and `gateway.stdio` for the corresponding optional
surfaces. The reliability additions advertise `doctor`, `activity`,
`contract-guard`, `context-optimizer` and `drift`. Version 1.0.5 also advertises
`updates`, `provenance`, `agent-import`, `library` and `skills` when those native
capabilities are available. `updates` is advertised only when the signed update
pipeline is available; the other flags describe read-only inspection or safe
authoring surfaces.

`platformDisplayName` is for display only — branching on it is a §3.2 violation.

`secretStoreDisplayName` is what this platform calls the credential store Kytto
keeps its own copy in: "Keychain" on macOS, "Credential Manager" on Windows. The
secrets screen has to name it — "Keep a copy in …" is meaningless otherwise — and
the alternative was one platform's product name written into a page shared by
both builds. Display only, like `platformDisplayName`.

### `updates.check` → `UpdateCheckDTO`

```json
{ "force": false }
```

The native side checks only the first-party manifest at
`https://kytto.jakubhecht.sk/update.php`. Automatic checks are attempted at most
once every 24 hours; the attempt timestamp is recorded before the request, and a
previously verified public release result may be returned as `skipped` during
that interval. `force: true` always performs a fresh user-requested check.

```json
{
  "status": "updateAvailable",
  "currentVersion": "1.0.4",
  "latestVersion": "1.0.5",
  "checkedAt": 1800000000,
  "releaseNotesURL": "https://kytto.jakubhecht.sk/changelog.php",
  "sha256": "64 lowercase hexadecimal characters"
}
```

Valid statuses are `updateAvailable`, `upToDate` and `skipped`. A skipped result
without a previously verified cache has null release fields. The Windows
manifest is accepted only after native validation of its schema, beta channel,
version, first-party URLs, response size and SHA-256 format. No configuration,
secret, installation id or telemetry value is sent with the request. The hash
is metadata for display and is never treated as a code signature or a reason to
bypass SmartScreen.

### `updates.download` → `UpdatePackageDTO`

No payload. Requires a current verified `updates.check` result. Native downloads
only the fixed first-party Windows route, streams into private Kytto app data,
limits the package to 512 MiB, and verifies the manifest SHA-256 before exposing
the staged package to the UI. A partial download is deleted on cancellation or
failed verification.

```json
{ "token": "opaque stage token", "version": "1.0.5", "sha256": "64 lowercase hexadecimal characters",
  "byteCount": 12345678 }
```

Progress is delivered as the `updates.progress` event. Its `phase` is
`downloading`, `verifying`, `staged`, `cancelled`, `installing` or `relaunching`;
the native side does not claim `installed` before a newly
launched process verifies its own version.
`progress` is a number from 0 to 1 when known, otherwise null.

### `updates.cancel` → `{}`

No payload. Cancels the active download and removes its partial file. It does
not cancel an install already handed to Windows.

### `updates.install` → `{}`

```json
{ "token": "opaque stage token" }
```

Rehashes the named staged package, verifies its version/package type and starts
the trusted Windows installer only after the user explicitly chooses Install. The
installer must report a successful replacement and verified installed version
before native returns; a failed replacement is surfaced as an error. Native never
accepts a URL, command line or bypass flag from the web layer.

### `updates.openDownload` → `{}`

No payload. After the user chooses **Download update**, native opens this fixed
Windows route in the default browser:

`https://kytto.jakubhecht.sk/download.php?file=windows&utm_source=kytto-app&utm_medium=updater`

The URL is owned by native code; JavaScript cannot supply or replace it. Kytto
does not download or install anything automatically.

### `clients.list` → `ClientDTO[]`

No payload. The five fixed built-in clients come first, installed or not,
followed by the user's dynamic read-only custom sources. A custom source id is
`custom.<lowercase-uuid>` and never enters the built-in mutation registry.

| Field | Notes |
|---|---|
| `id` | `claudeDesktop` \| `claudeCode` \| `cursor` \| `vsCode` \| `codex` \| `custom.<lowercase-uuid>` |
| `displayName` | |
| `shortName` | Compact distinguishing project leaf for Claude Code project scopes; null for every other client |
| `iconAsset` | Asset name, resolved by the UI |
| `state` | `ready` \| `noConfig` \| `orphanedConfig` \| `notInstalled` |
| `configPathDisplay` | For display and tooltips only — never parsed |
| `configFormatDisplay` | `JSON` or `TOML` for built-ins; `Auto-detected · read-only` for custom sources |
| `offSwitchSummary` | What switching a server off does to this client's file, in prose. Composed native-side from the client's enablement strategy and its other sources, so a client with two of them answers the question twice (§7.8) |
| `serverCount` | |
| `schemaQuirks` | How this client differs; surfaced in the client screen (§7.8) |
| `isReadOnly` | `true` for custom sources; mutation destinations filter on this capability rather than platform |
| `configurationScope` | `global` \| `profile` \| `workspace` |
| `scopeLabel` | Optional display label for a profile or workspace; otherwise null |

Claude Code project entries from the same `.claude.json` are represented as
opaque `custom.<uuid>` read-only workspace clients. Their `scopeLabel` is the
full native display form of the project path, `shortName` is its smallest
distinguishing leaf (for example `…\shop`), and `displayName` is
`Claude Code · …\shop`. They are never accepted as mutation targets and are
never used as write paths.

`orphanedConfig` means a config exists but the client does not — a leftover
`~/.cursor` after an uninstall. Shown rather than hidden.

For a custom source, `iconAsset` is `client-custom`,
`offSwitchSummary` explains that Kytto never changes its file, and a missing
selected file is a visible `noConfig` state. Paths are display strings only.

### `servers.list` → `ServerDTO[]`

No payload. Servers merged across clients: one entry per normalized name.

| Field | Notes |
|---|---|
| `id` | Normalized name; stable across launches |
| `name` | As written in the config |
| `transport` | `stdio` \| `http` \| `sse` |
| `command`, `args`, `url` | As configured; `${__dirname}` is left unexpanded |
| `commandSummary` | Single line for the matrix subtitle |
| `env` | `[{ key, hasValue }]` — **never a value** |
| `enabledIn` | Built-in or custom client id → `enabled` \| `disabled` \| `absent`; custom entries use presence semantics for display only |
| `originKind` | `configFile` \| `extension` |
| `isBundled` | Extensions ship their own command and are not editable |
| `hasRelativePath` | The command or an argument is resolved against a working directory, so copying this definition into another client may write cleanly and still not start. The fact only — no path crosses |
| `tokenWeight` | `{ estimate, method, isMeasured, measuredAt, percentOfContext, referenceContextWindow }`, or null before a health check |
| `provenance` | `{ sourceKind, packageName, sourceURL, installedVersion, latestVersion, latestCheckedAt, confidence, maintenanceState }`, or null |

`provenance` is inferred from the existing definition without contacting a
registry. `latestVersion` and `latestCheckedAt` are filled only by the explicit
`provenance.checkLatest` command; environment values never cross this boundary.

`isMeasured` is what decides whether the UI writes "~" in front of a token
figure. It is true when a real vocabulary produced the number and false when the
old `chars/4` heuristic did, which is the case for anything last checked by a
build from before §7.4 changed its mind. A total that mixes the two keeps the
tilde: a sum is only as measured as its least measured term.

`disabled` and `absent` are different states and the matrix must not conflate
them. `disabled` means configured and switched off — Claude Code's deny list, or
an extension with `isEnabled: false`. `absent` means not configured in that
client at all.

### `diagnostics.list` → `DiagnosticDTO[]`

No payload. Problems found while reading, e.g. a config that would not parse.

```json
{ "severity": "warning" | "error", "clientID": "cursor" | null,
  "pathDisplay": "~/.cursor/mcp.json", "message": "…" }
```

A file that produced an `error` is one Kytto will not write to.

### `state.get` → `StateDTO`

No payload. Everything the matrix needs in one reply.

```json
{ "clients": [...], "servers": [...], "diagnostics": [...],
  "pendingRestarts": { "claudeDesktop": 2 },
  "dismissedRestarts": ["cursor"],
  "gatewayRoutes": [{ "id": "opaque UUID", "serverID": "github",
    "serverName": "github", "clientID": "cursor", "createdAt": 1785744000 }],
  "drift": [{ "serverID": "github", "serverName": "github",
    "fields": ["arguments", "environmentValues"],
    "unwritableClientIDs": [],
    "variants": [{ "clientIDs": ["claudeCode", "cursor"],
                   "commandSummary": "npx -y @modelcontextprotocol/server-github",
                   "transport": "stdio", "url": null,
                   "environmentKeys": ["GITHUB_PERSONAL_ACCESS_TOKEN"],
                   "isBundled": false, "canBeSource": true }] }] }
```

`pendingRestarts` counts changes a client has not picked up yet. Clients read
their configuration at launch and only at launch; §7.1 says to state that plainly
rather than work around it.

`dismissedRestarts` is presentation state only. Dismissing hides a still-pending
notice; it does not advance the applied baseline. A new net change makes the
notice visible again, and an exact inverse removes the pending count.

`gatewayRoutes` contains safe route identity only. Upstream commands, original
definitions and environment values do not cross IPC.

`doctorReports` contains actionable configuration/health findings and opaque
action names. `contractAlerts` contains tool names and model-facing contract
change summaries. Neither contains environment values or tool-call payloads.
An authorization wait adds the info finding `needs-authorization` with action
`runHealthCheck`; it is not classified as a health failure.

`drift` is present for a server only when its clients disagree about what it
runs. One row in the matrix can be several definitions on disk, and merging them
is what makes the matrix readable — this is where the merge shows its working.
`fields` names what differs (`command`, `arguments`, `url`, `transport`,
`environmentKeys`, `environmentValues`) and the UI turns those into prose; it
does not decide what counts as a difference. `variants` groups clients that hold
byte-identical definitions.

**`environmentValues` is the one field that reports something it does not
carry.** Two clients setting `GITHUB_TOKEN` to two different things is drift
worth seeing and the values themselves never cross — `environmentKeys` names the
key, and the difference is stated as a fact about it.

`canBeSource` is false for an extension bundle or a plugin. Those take part in
the comparison, because a name shared by a bundle and a config entry is exactly
the confusion worth surfacing, and they are never something to copy out of (§4).

### `doctor.previewFix` → `DoctorFixPreviewDTO`

```json
{ "serverID": "github" }
```

Returns the configured command, the absolute path verified by the last passing
health check and affected client ids. It does not write.

### `doctor.applyFix` → `AuthoringResultDTO`

```json
{ "serverID": "github" }
```

Applies only the validatable executable-path repair through the ordinary
multi-client authoring transaction. Every affected configuration is backed up
and written atomically; a stale or missing passing check rejects it.

### `contract.acknowledge` → `StateDTO`

```json
{ "serverID": "filesystem" }
```

Records that a Contract Guard alert has been read, and makes the current tool
contract the baseline the next change is measured against.

Without it an alert cannot end. `contractChanges` is rewritten only by a check
that finds something *different*, so a change already reviewed and accepted goes
on being reported until the server happens to change again — and an alert that
never clears is one nobody reads the second time. Acknowledging clears the
changes and keeps the tools, so accepting a contract is a statement about that
contract rather than a dismissal.

### `activity.list` → `GatewayActivityDTO`

No payload. Returns the newest bounded view of the local Gateway JSONL journal,
including session ids, client/server ids, tool names, duration, success/error
codes and process exit codes. Arguments, results, raw protocol messages and
environment values are intentionally absent.

### `discovery.refresh` → `StateDTO`

No payload. Re-reads every config from disk.

### `servers.setEnabled` → `ToggleResultDTO`

```json
{ "serverID": "github", "clientID": "cursor", "enabled": false }
```

The one command that writes to a client's configuration. Every call goes through
the §6 pipeline: refuse if the file changed on disk, back it up, splice, write
atomically.

A `custom.<uuid>` client id is rejected natively. The first-write explanation is
a web flow and does not weaken this command: after the user confirms it, the UI
calls this same safe path.

```json
{ "serverName": "github", "clientID": "cursor", "enabled": false,
  "requiresRestart": true, "wasParked": true,
  "backupID": "mcp.json.2026-07-30-214455-102.bak",
  "pathDisplay": "~/.cursor/mcp.json", "state": { ... } }
```

`wasParked` is true when the client has no disabled state, so switching off had
to remove the definition. Kytto stores the original source text and restores it
byte for byte when the server is switched back on. **The UI must say so** —
quietly deleting configuration is the surprise §6 exists to prevent.

The reply carries a full `state`, so one toggle updates the whole screen without
four follow-up calls.

Errors worth handling by code:

| Message contains | Meaning |
|---|---|
| `changed on disk` | Someone else edited the file. Nothing was written. Refresh, then retry. |
| `cannot be copied` | A Claude Desktop extension is an installed bundle, not a portable definition. |
| `installed plugin` | A Codex plugin provides this server. Its definition is in a cache the client rewrites, so there is nothing here to toggle. |

### `servers.unifyPreview` → `UnifyPreviewDTO`

```json
{ "serverID": "github", "sourceClientID": "cursor" }
```

What unifying would do, without writing. Returns the clients that would be
rewritten, the ones that cannot be, and the winning definition's command and
environment **key names**.

```json
{ "serverID": "github", "serverName": "github", "sourceClientID": "cursor",
  "targetClientIDs": ["codex"], "skippedClientIDs": [],
  "commandSummary": "npx -y @modelcontextprotocol/server-github",
  "url": null, "transport": "stdio",
  "environmentKeys": ["GITHUB_PERSONAL_ACCESS_TOKEN"] }
```

### `servers.unify` → `UnifyResultDTO`

```json
{ "serverID": "github", "sourceClientID": "cursor" }
```

Rewrites every drifted copy to match one client's. Each file goes through the §6
pipeline, exactly as `servers.setEnabled` and `servers.update` do.

**The payload names a client, not a definition, and that is the point.** The
definition is assembled natively from the source client's own entry, including
its environment values. Sending it round trip would put a token on this boundary
twice; it does not cross once (§6).

```json
{ "serverName": "github", "changed": ["codex"],
  "parkedUpdated": ["cursor"], "parkedFailures": [],
  "requiresRestart": true, "state": { … } }
```

`parkedUpdated` is separate from `changed` because nothing in a config file
moved. A server switched off in a presence-only client is not in that file at
all — Kytto is holding its bytes so switching it back on restores them, and
those bytes are a copy that can drift like any other. Writing the definition
into the file instead would be indistinguishable from switching the server *on*,
which is why the two outcomes are counted apart and only `changed` asks anyone
to restart.

Refused for a server with an active Gateway route, and for an extension or
plugin: restore Direct mode first, and installed software is not configuration.

### `restarts.acknowledge` → `StateDTO`

```json
{ "clientID": "claudeDesktop" }
```

Nil `clientID` clears every client.

### `restarts.dismiss` → `StateDTO`

```json
{ "clientID": "claudeDesktop" }
```

Hides the current restart notice for one client without changing its pending
baseline. Nil `clientID` hides all current notices. The native state continues
to carry the pending count so a later net change can surface it again.

### `provenance.checkLatest` → `ProvenanceCheckResultDTO`

```json
{ "serverID": "github" }
```

An explicit user action that sends only the inferred package name to the
package's first-party npm or PyPI registry endpoint. Discovery, launch, normal
health checks and the update checker do not call it. Results are cached for 24
hours per session.
The response includes `state` and provenance fields such as `sourceKind`,
`packageName`, `installedVersion`, `latestVersion`, `latestCheckedAt`,
`confidence` and `maintenanceState` (`healthy`, `unhealthy`, `unknownSource`,
`staleButResponsive`, or `unchecked`).
`needsAuthorization` maps to `unchecked`: the handshake did not finish, so the
result says nothing about maintenance health.

### `imports.prompt` → `ImportPromptDTO`

No payload. Returns the copyable strict-JSON agent prompt. It requests only
`schemaVersion`, `servers`, server names, transport, command/URL, arguments and
environment key names; it never requests environment values.

### `imports.preview` → `AgentImportPreviewDTO`

```json
{ "json": "{\"schemaVersion\":1,\"servers\":[...]}",
  "clientIDs": ["cursor", "vsCode"] }
```

The native parser accepts strict JSON only: no comments, fences, trailing
commas, unknown fields, duplicate fields, control characters, unsafe names or
secrets. Preview validates the complete document and returns a diff-like list
of servers, built-in targets, conflicts and warnings without writing.

### `imports.import` → `AgentImportResultDTO`

Uses the same strict parser and then the ordinary built-in authoring pipeline,
including digest guards, backups and atomic writes. Dynamic read-only clients,
paths, secret values and invented blank environment values are never accepted.

### `library.chooseDirectory` → `LibraryDirectoryDTO | null`

Opens the native directory picker and returns an opaque session id. The selected
root remains native state; the web layer receives only its display name.

### `library.scan` → `LibraryScanDTO`

```json
{ "path": "library-session.…" }
```

On Windows, `path` is an opaque native picker token rather than a filesystem
path for the web layer to parse; the selected root remains native state.

Performs a bounded, read-only scan below the selected root. It recognizes the
supported JSON/JSONC/TOML server maps and package/Python entry suggestions,
skips common dependency/build directories, does not follow symlink directories,
does not execute or contact anything, and returns relative locations,
transport, command summaries, environment key names, detected/missing built-in
clients and warnings.

### `library.preview` → `LibraryPreviewDTO`

```json
{ "path": "library-session.…", "candidateID": "library.…",
  "clientIDs": ["cursor"] }
```

Returns the selected candidate, conflicts, warnings and built-in targets. It
does not write.

### `library.import` → `LibraryImportResultDTO`

```json
{ "path": "library-session.…", "candidateID": "library.…",
  "clientIDs": ["cursor"] }
```

Imports one scanned candidate through the normal safe authoring transaction.
Only built-in client ids are accepted; candidate ids are opaque session data,
and the scanner's root is never treated as a write destination.

### `skills.chooseDirectory` → `SkillsDirectoryDTO | null`

Opens the native picker for a workspace root. The selection is an opaque,
read-only inventory session.

### `skills.inventory` → `SkillsInventoryDTO`

```json
{ "workspacePath": "skills-session.…" }
```

On Windows, `workspacePath` is an opaque native picker token; the selected
filesystem path remains native state.

Inventories direct child skill folders under known global and workspace roots,
recognizes `SKILL.md` case-insensitively, reads at most 128 KiB and extracts
only front-matter `name` and `description`. It never executes or modifies a
skill. Unreadable files, duplicate names and duplicate content remain visible
with warnings and opaque ids.

### `settings.get` → `SettingsDTO`

No payload. Only the settings the web layer needs.

```json
{ "theme": "system" | "light" | "dark", "tokenWarningThreshold": 20000,
  "hasCompletedOnboarding": true, "backupRetention": 20,
  "hasConfirmedMatrixWrites": false, "showsCustomSources": true,
  "sidebarWidth": 196 }
```

The rest of Settings — login item, menu bar and client path overrides —
never crosses this boundary. They are native
concerns, changed in a native window (§3.2), and the web layer has no use for
them.

`theme` is applied as `data-theme` on `<html>`; `system` means the OS decides.

`hasConfirmedMatrixWrites` records only that the one-time matrix-write
explanation was accepted. It never bypasses backup, digest or write validation.

`showsCustomSources` controls whether read-only sources are drawn in both the
sidebar and matrix. Missing means `true`; folding does not stop discovery or
remove any built-in client.

`sidebarWidth` is the remembered web-sidebar width in CSS pixels. Missing means
196; native clamps every decoded and interactive value to 170–480.

### `settings.open` → `{}`

No payload. Opens the native Settings window and returns at once — the
sidebar's Settings row is the same destination as the shell's own settings
shortcut. The page states the intent only; what a settings window is, how one is
shown, and which keystroke this platform spells it with stay in the shell
(§3.2). The reply does not wait for the window to close, and any settings the
web layer cares about arrive afterwards on the `settings.changed` event.

### `settings.confirmMatrixWrites` → `SettingsDTO`

No payload. Sets `hasConfirmedMatrixWrites` to true and returns the updated
settings. It performs no configuration write; the explicit primary action then
uses the ordinary `servers.setEnabled` command.

### `settings.setShowsCustomSources` → `SettingsDTO`

```json
{ "shown": false }
```

Persists whether read-only sources are drawn and returns the full updated
settings. The sources remain discovered and parsed while folded.

### `settings.setSidebarWidth` → `SettingsDTO`

```json
{ "width": 260 }
```

Persists the shared sidebar width and returns the full settings object. The
reply carries the native-clamped value actually kept (170–480).

### `onboarding.complete` → `SettingsDTO`

No payload. Marks the first-run screen as seen.

### `secrets.list` → `SecretRecordDTO[]`

No payload. Every sensitive-looking environment value, grouped by what it
actually is: name *and* value. The same token in three clients is one row; the
same name holding two different values is two rows, which is the drift worth
seeing.

```json
{ "id": "github_personal_access_token-9f3c…", "key": "GITHUB_PERSONAL_ACCESS_TOKEN",
  "maskedValue": "ghp_••••••1234", "isInSecretStore": false, "isShared": true,
  "usages": [{ "serverID": "github", "serverName": "github",
               "clientID": "cursor", "pathDisplay": "~/.cursor/mcp.json" }],
  "exposure": [{ "kind": "readableByOthers", "pathDisplay": "~/.cursor/mcp.json",
                 "detail": "Mode 644 — other users on this Mac can read it." }] }
```

**A note on what this feature is.** §6 asks for a reference in the config with
the value injected at spawn time. That is not possible: the client spawns the
server, not Kytto, and no MCP client understands a reference. Delivering it
literally would mean rewriting every `command` to point at a Kytto launcher,
making a working setup break the moment Kytto is uninstalled. So the value stays
in the file, and Kytto is where it is *managed* — which is why `maskedValue` is
the only form that crosses this boundary by default.

`exposure` kinds: `readableByOthers`, `insideGitRepository`.

### `secrets.reveal` → `{ "value": "…" }`

```json
{ "secretID": "github_personal_access_token-9f3c…" }
```

The one command that hands a real value to the web layer. §6 permits a reveal
behind a confirm — the UI must ask first, and does.

### `secrets.rotate` → `RotationResultDTO`

```json
{ "secretID": "…", "newValue": "ghp_new…", "storeInSecretStore": true }
```

Writes the new value into every file the old one was in, one file at a time,
each backed up first. This is the point of the feature: rotating a token by hand
means finding every config that has it, which is how one gets missed.

```json
{ "key": "GITHUB_PERSONAL_ACCESS_TOKEN", "updatedCount": 2,
  "clients": ["claudeCode", "cursor"], "state": { … } }
```

### `secrets.adopt` / `secrets.forget` → `SecretRecordDTO[]`

```json
{ "secretID": "…" }
```

Keeps or drops Kytto's own copy of the value in the platform credential store.
The copy is what makes a value recoverable; it is not what any client reads.
`app.info` supplies the name to show it under — rule 3 is why neither the field
nor the command is spelled `keychain`.

### `secrets.restrictPermissions` → `SecretRecordDTO[]`

```json
{ "clientID": "cursor" }
```

Restricts that client's config to its owner: `chmod 600` on macOS; on Windows, a
protected access list with inheritance switched off and a single entry for the
current account. Small, reversible, and the only hardening available while the
value has to stay in the file.

### `health.check` → `{ health, state }`

```json
{ "serverID": "github" }
```

Runs the server, does the MCP handshake, calls `tools/list`, and records what
came back. When the server advertises prompts or resources, the same inspection
also requests their first page. Takes up to ten seconds; the native side runs it
off the main thread.

Only ever on request (§6). Nothing is checked on launch or in the background.

```json
{ "status": "passed" | "failed" | "unsupported" | "needsAuthorization",
  "checkedAt": 1785534295.1, "toolCount": 11,
  "promptCount": 2, "resourceCount": 1,
  "protocolVersion": "2025-03-26",
  "capabilityNames": ["tools", "prompts", "resources"],
  "tools": [{ "name": "read_file", "description": "…",
              "inputSchemaJSON": "{\"type\":\"object\",…}",
              "annotations": { "readOnlyHint": true },
              "tokenCount": 74 }],
  "message": null,
  "stderr": "dyld[36368]: Library not loaded: …",
  "durationSeconds": 1.2,
  "serverName": "filesystem", "serverVersion": "2026.7.10",
  "resolvedCommand": "/opt/homebrew/bin/node",
  "environmentSource": "Login shell PATH",
  "authorizationURL": null,
  "inspectionNotes": [] }
```

Tool schemas are the exact JSON slices the weight is measured on, not a lossy
summary. `tokenCount` is what that one tool's entry costs (§7.4), and is null
for a server last checked by a build that measured whole servers only — the UI
falls back to the server's own tool order when that happens rather than
inventing a ranking. The per-tool counts sum to slightly less than the server's
`tokenWeight`: the array brackets and the commas between entries belong to no
single tool. `annotations` contains the optional MCP safety hints. A prompt/resource
failure is recorded in `inspectionNotes` and does not turn an otherwise healthy
server red. Pagination is called out there too: counts represent the inspected
first page when a continuation cursor is returned.

`stderr` is the server's own output, **verbatim**. §6 calls it the most useful
thing you can show a user whose server is broken, so it is never summarised,
truncated at the front, or interpreted. The UI shows it in a `<pre>`.

`unsupported` means Kytto cannot check this kind of server yet — currently
anything remote, since there is no process to start.

`needsAuthorization` means the failed handshake's captured stderr explicitly
said that the process is waiting for browser authorization. It is neither a
passing nor a failing verdict. Its blue UI state carries the current message,
verbatim stderr, and an optional native-validated `authorizationURL`. An
OAuth-looking URL without a wait marker remains `failed`; `http://` is never
recorded as an authorization page. The stored health failure is structured and
`message` is rendered from that reason when the DTO is produced, so wording
fixes also apply to results already on disk.

`servers.list` and `state.get` carry `health` and `tokenWeight` on each server
from then on, read from Kytto's own store rather than measured again.

### `health.checkAll` → `StateDTO`

No payload. Checks every stdio server except one already waiting for interactive
authorization, one at a time. Sequential on purpose:
these are arbitrary commands, and starting a dozen node processes at once to save
a few seconds is not a trade worth making on someone's laptop.

Emits `health.progress` before each server.

### `health.openAuthorization` → `{}`

```json
{ "serverID": "intercom" }
```

The web layer sends only an opaque server id. Native looks up its own last health
record, requires `status == needsAuthorization`, re-validates the recorded URL as
HTTPS with a host, and opens that page in the default browser. JavaScript can
never supply the URL. If no valid page was captured, the command fails with:

```text
No authorization page is recorded for this server. Check it again to capture one.
```

### `profiles.list` → `ProfileDTO[]`

No payload. Profiles are local Kytto metadata, not another source of truth for
client configuration.

```json
{ "id": "5a2f…", "name": "Coding", "serverIDs": ["filesystem", "github"],
  "tokenBudget": 15000, "analysis": { "estimatedTokens": 12200,
    "measuredServerCount": 2, "totalServerCount": 2, "recommendations": [] } }
```

An empty `serverIDs` array is valid and represents a minimal profile.

### `profiles.create` / `profiles.update` → `ProfileDTO[]`

```json
{ "name": "Coding", "serverIDs": ["filesystem", "github"], "tokenBudget": 15000 }
{ "profileID": "5a2f…", "name": "Writing", "serverIDs": ["memory"], "tokenBudget": null }
```

Names are trimmed, limited to 80 characters and unique without regard to case.
Updating a server name also updates its references in profiles.
Non-positive budgets mean no limit; positive values are capped at the reference
context window. Optimizer recommendations are advisory and never edit membership.

### `profiles.delete` → `ProfileDTO[]`

```json
{ "profileID": "5a2f…" }
```

Deletes only Kytto's profile. Client configurations are not changed.

### `profiles.apply` → `ProfileApplyResultDTO`

```json
{ "profileID": "5a2f…", "clientID": "cursor" }
```

Makes one client match the profile exactly: profile members are enabled and
every other active server is switched off. Each individual change uses the same
backup, external-change guard and atomic writer as `servers.setEnabled`.

```json
{ "profileName": "Coding", "clientID": "cursor",
  "enabledCount": 2, "disabledCount": 3, "requiresRestart": true,
  "failures": [{ "serverName": "bundled", "message": "…" }],
  "state": { … } }
```

Application is best-effort across independent server entries. A server Kytto
cannot safely change is returned in `failures`; successful changes are retained
and reflected in `state`.

### `clipboard.writeText` → `{}`

```json
{ "text": "Kytto MCP diagnostic report\n…" }
```

Copies user-requested text to the platform clipboard. The Inspector uses it for
a support report that omits environment values and raw server output because
both may contain credentials.

### `catalog.list` → `CatalogEntryDTO[]`

No payload. The bundled starting points (§7.2). Static — no remote fetching, no
registry API.

```json
{ "id": "filesystem", "name": "filesystem", "displayName": "Filesystem",
  "description": "…", "transport": "stdio", "command": "npx",
  "args": ["-y", "@modelcontextprotocol/server-filesystem", "{{directory}}"],
  "url": null,
  "placeholders": [{ "token": "{{directory}}", "label": "Directory to allow",
                     "example": "/Users/you/Projects" }],
  "env": [{ "key": "…", "required": true, "hint": "…" }],
  "requires": "Node.js", "homepage": "…" }
```

`placeholders` name tokens that appear literally in `args`. The UI must prompt
for them — shipping a literal `{{directory}}` into a config is a bug, and there
is a test asserting every token is declared.

### `servers.create` → `AuthoringResultDTO`

```json
{ "draft": { "name": "memory", "transport": "stdio", "command": "npx",
             "args": ["-y", "@modelcontextprotocol/server-memory"],
             "env": [{ "key": "TOKEN", "value": "secret" }], "url": "" },
  "clientIDs": ["cursor", "claudeCode"] }
```

Writes the same definition into every listed client, in that client's schema.
Creates the config file if the client has never configured MCP.

### `servers.update` → `AuthoringResultDTO`

```json
{ "serverID": "github", "draft": { ... } }
```

Applies to **every client the server is configured in**. Editing is not
per-client: a server in three clients is one row in the matrix, and letting the
three drift apart is the problem this app exists to solve.

Renaming works — the name is the config key, so it is handled as a remove plus an
add, and any deny-list entry for the old name is cleaned up with it.

**Environment values:** an entry with `"value": null` means *keep whatever is
already in the config*. The form never receives secret values, so it cannot send
them back; the native side merges them in at the last moment. An empty string
would overwrite a real token, so the UI sends `null`, not `""`.

### `servers.delete` → `AuthoringResultDTO`

```json
{ "serverID": "github" }
```

Removes the server from every client that has it, clears any parked copy, and
clears any deny-list entry. Each file is backed up first.

Claude Desktop extensions cannot be created, edited or deleted — they are
installed bundles. The attempt fails with a message saying so.

`AuthoringResultDTO`:

```json
{ "serverName": "memory", "changed": ["cursor", "claudeCode"],
  "requiresRestart": true, "state": { ... } }
```

### `servers.removeFromClient` → `AuthoringResultDTO`

```json
{ "serverID": "computer-use", "clientID": "claudeCode" }
```

Takes the server out of one client and leaves the others holding it: the
definition, any parked copy and any deny-list entry all go, so the cell returns
to `absent`.

Not the same command as `servers.setEnabled` with `false`, and the difference is
the point. Off is a state a client supports, and every client keeps enough to
switch back on — so a cell never returns to `absent` by being switched off. This
is the server ceasing to be that client's business. Fails with `notFound` if the
client did not have it, and refuses bundled servers like `servers.delete` does.

### `backups.list` → `BackupDTO[]`

```json
{ "clientID": "cursor" }
```

Nil `clientID` lists every client's. Newest first.

```json
{ "id": "mcp.json.2026-07-30-214455-102.bak", "clientID": "cursor",
  "pathDisplay": "~/.cursor/mcp.json", "takenAt": 1785534295.1, "byteCount": 1830 }
```

`takenAt` is seconds since the epoch; the UI formats it in the user's locale.

### `backups.restore` → `StateDTO`

```json
{ "backupID": "mcp.json.2026-07-30-214455-102.bak", "clientID": "cursor" }
```

Puts a backup back. The file it replaces is itself backed up first, so reverting
is never a one-way door.

The original location is honoured only when it is still a file Kytto writes for
`clientID` — that client's server map, at either the path configured in Settings
or the registry's own, its deny list if it has one, and a settings file directly
inside its extension settings folder. The set is derived from the registry, not
read out of the backup, and the write goes to the registry's spelling of the
path.

Anything else fails with code `backup`, and nothing is written — not even the
pre-restore backup:

```json
{ "ok": false, "error": { "code": "backup", "message": "…" } }
```

That includes a backup taken while the client's config location was overridden to
somewhere it no longer points. The message says so, and says the backup is still
in the backups folder.

## Resources

Not everything the shell provides is a request/response command. Two things are
pushed or served instead, and they follow the same rules: opaque ids in, display
material out, nothing the web layer has to interpret.

### `GET /icon/<clientID>.png`

Served by the shell on its own origin; `ipc.js` builds the URL from a client id
and nothing else knows the route exists.

The matrix has one column per client and their names do not fit in one, so the
column head is the client's real application icon. The shell resolves the id to
whatever it uses to identify installed software — a bundle identifier on macOS, a
registry key on Windows — and returns a square PNG. Three outcomes, all 200:

| Case | What comes back |
|---|---|
| Installed application | Its actual icon |
| Client that is a command line tool | A drawn terminal tile |
| Neither — a config that outlived its client | A drawn placeholder |

Always an image, so the UI never branches. Where there is no shell at all —
`web/dev.html` — `clientIconURL` returns null and the UI draws initials in the
same 22px square, which is also what it falls back to if a request fails.

### Window metrics

Written onto the document root as CSS custom properties by the shell, not
requested by the page:

| Property | Meaning |
|---|---|
| `--titlebar-height` | Height of the strip at the top of the window that the shell owns the mouse in |
| `--traffic-light-inset` | How far in from the leading edge the window buttons reach |
| `--chrome-alpha` | How opaque the page's chrome should be, given what the shell draws behind it |

The page puts its numbers in the title bar strip — text, which needs no mouse, and
which leaves the strip dragging the window — and keeps everything clickable below
it. That is the whole reason these cross the boundary.

None of them names an operating system, and all three have defaults that produce
the same layout when nothing sets them (28px, 0px, 100%). A shell with no window
buttons and no material behind the page sets the first two to `0` and leaves the
third alone; nothing else in the UI changes.

## Events

| Event | Payload | Sent when |
|---|---|---|
| `menu.refresh` | `{}` | User asked to re-read configurations: File → Refresh Configurations (`Cmd+R`) on macOS, `Ctrl+R` or `F5` on Windows |
| `menu.find` | `{}` | User asked to find: Edit → Find (`Cmd+F`) on macOS, `Ctrl+F` on Windows |
| `configs.changed` | `StateDTO` | A watched file changed on disk, whoever changed it |
| `settings.changed` | `SettingsDTO` | Native settings were saved |
| `health.progress` | `{ serverName, index, total }` | `health.checkAll` moved on to the next server |
| `updates.progress` | `{ phase, progress, bytesReceived, totalBytes }` | A download or explicit install moved to its next verified phase |

`configs.changed` fires for Kytto's own writes too. Re-reading is cheap and the
render is idempotent, so the UI settles on the truth rather than trying to work
out which events were its own.

## Not implemented

Health checking remote (HTTP and SSE) servers. Those report `unsupported` with
the reason rather than a misleading grey dot — there is no process to start, so
checking one means speaking streamable HTTP, which is a transport this client
does not have yet.

General project-scoped configs (`.mcp.json`, `.cursor/mcp.json` and similar
repository-local files) remain out of scope by §4. Claude Code's explicit
`.claude.json` `projects` map is the v1.0.5 exception: it is surfaced as
read-only workspace clients because it can be inspected without crawling
projects or introducing a writable project axis.

## Gateway mode

### `gateway.preview` → `GatewayMigrationPreviewDTO`

```json
{ "serverID": "github", "clientID": "cursor" }
```

Preflights one enabled stdio definition without writing. The reply carries a new
opaque `routeID`, display-only config location and format, a redacted Direct
definition and the exact Gateway replacement. Environment keys may cross; their
values never do.

```json
{ "routeID": "opaque UUID", "serverID": "github", "serverName": "github",
  "clientID": "cursor", "pathDisplay": "~/.cursor/mcp.json",
  "formatDisplay": "JSON", "directDefinitionPreview": "…redacted…",
  "gatewayDefinitionPreview": "…", "environmentKeys": ["GITHUB_TOKEN"] }
```

### `gateway.enable` → `GatewayMigrationResultDTO`

```json
{ "serverID": "github", "clientID": "cursor", "routeID": "UUID from preview" }
```

Revalidates the file, moves the exact original definition and environment
values into the native credential store, atomically saves a secret-free route,
then replaces only that server definition through `ConfigWriter`.

### `gateway.restore` → `GatewayMigrationResultDTO`

```json
{ "routeID": "opaque UUID" }
```

Both write commands reply with `{ route, backupID, pathDisplay, state }`.
Restore verifies that the client still points at this route, puts back the exact
Direct definition through the guarded backup pipeline, then removes the route
and its credential entries. While a route is active, ordinary edit, delete,
disable and remove commands are refused; restore Direct mode first.

### `gateway.setExposedTools` → `StateDTO`

```json
{ "routeID": "opaque UUID", "toolNames": ["read_file", "list_directory"] }
```

Narrows one route to exactly these tools (§7.11). `toolNames: null` stops
masking and puts the route back to forwarding bytes untouched; `[]` is a
different instruction — expose nothing — and is honoured as written.

**No config file is written.** The client already points at this route id, so
only Kytto's own route record changes. The client still has to be restarted
before it asks for the tool list again, so the reply carries the same pending
restart as a real config write.

The list is exact and closed: a tool the server starts offering later stays
hidden until it is chosen. A context budget that quietly grew when a server
updated would defeat the point of setting one, and Contract Guard already
reports the new tool.

Every `GatewayRouteDTO` carries `exposedTools` — the array, or null when the
route exposes everything. Tool names are model-facing identifiers rather than
secrets, so unlike a route's definition and environment they cross this
boundary.
