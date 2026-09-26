# IPC contract

The single channel between the web UI and the native shell. Every native
capability is one named command; the web layer has no other way to reach the
machine.

This document is the contract M7 reimplements against Windows APIs. If the
Windows port needs the web layer changed, something here described an
implementation instead of an intent — see README.md §3.2.

## Rules

1. **Commands describe intent, not implementation.** `secrets.store(ref, value)`,
   never `keychain.addGenericPassword(...)`.
2. **No path the web layer has to interpret.** Native sends display strings and
   opaque ids. Path construction, separators and variable expansion stay native.
3. **No platform names cross the boundary.** The UI branches on capabilities
   reported by `app.info`, never on the name of an OS.
4. **Secrets do not cross.** Environment variables travel as key plus a
   `hasValue` flag. Values stay in Swift until they are injected at spawn time.
5. **Every command added here gets a row in this file in the same commit.**

## Transport

The handler accepts messages only from the main frame of the internal
`kytto://app` origin. A subframe or any `http`/`https` page receives the stable
`untrustedOrigin` failure and cannot invoke native commands. Top-level web links
open in the system browser; they never replace the privileged application page.

Web to native, from `web/js/ipc.js`:

```js
const raw = await window.webkit.messageHandlers.kytto.postMessage({ command, payload });
```

The reply is a JSON **string**, parsed by `ipc.js`. A string rather than a
bridged object so what JavaScript receives is exactly what Swift encoded, with no
WebKit type coercion in between.

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
  "version": "1.0",
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
`contract-guard`, `context-optimizer` and `drift`. `updates` means the native
shell implements the first-party update check and verified download/install
actions below. `library` and `agent-import` expose the read-only local inventory
and strict JSON preview flow. `skills` exposes a read-only inventory of agent
skill folders. `provenance` exposes an explicit, read-only package metadata check.

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

The native shell checks Kytto's first-party HTTPS manifest. With `force: false`,
it may return a previously verified result when an automatic attempt was made in
the last 24 hours. With `force: true`, it performs a fresh user-requested check.
The web layer never fetches the manifest itself.

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

`status` is `updateAvailable`, `upToDate`, or `skipped`. Optional fields are
`null` only when no verified result has been cached and an automatic check is
skipped. The shell validates the schema, numeric release version, platform
entry, first-party HTTPS URLs and SHA-256 shape before returning data.

### `updates.openDownload` → `{}`

No payload. Opens the platform's fixed Kytto download route in the system
browser. The native shell must not open a URL supplied by JavaScript or copied
back from a network response. macOS opens `file=macos`; Windows opens
`file=windows`.

### `updates.download` → `UpdateStageDTO`

No payload. The native shell first performs a fresh first-party manifest check,
then streams the fixed platform installer into a private staging directory. It
accepts only the first-party download route, enforces a size limit, and compares
the complete artifact with the manifest SHA-256 before returning a stage token.
The token is opaque; the local staging path never crosses IPC.

```json
{
  "token": "opaque stage token",
  "version": "1.0.5",
  "byteCount": 12345678,
  "sha256": "64 lowercase hexadecimal characters",
  "status": "verified"
}
```

Progress is pushed as `updates.progress` with `phase`, `completedBytes`,
`totalBytes`, `fraction`, and a safe display `message`. It contains no local
paths or user data.

### `updates.cancel` → `{}`

No payload. Cancels the active streamed update download. A partial artifact is
discarded and cannot be passed to `updates.install`.

### `updates.install` → `UpdateInstallDTO`

```json
{ "token": "opaque stage token" }
```

The shell verifies the staged application bundle and its exact version again,
checks that the current application directory is writable, moves the old app
aside, moves the new app into place, verifies the replacement, requests a new
application instance, and only then removes the old bundle. Any failure during
replacement attempts a rollback. User configuration and secrets are outside
the replacement transaction.

```json
{
  "version": "1.0.5",
  "relaunched": true,
  "status": "installedAndRelaunching"
}
```

### `clients.list` → `ClientDTO[]`

No payload. The five built-in registry clients are always present; explicitly
attached custom sources follow them with opaque `custom.<uuid>` ids.

| Field | Notes |
|---|---|
| `id` | Built-in id or opaque `custom.<uuid>` |
| `displayName` | |
| `shortName` | Optional shorter name for surfaces that carry the client's identity some other way — the sidebar row under its icon and heading. A Claude Code project scope sends the path's distinguishing leaf (`…/kytto`); its `displayName` keeps the client prefix and `scopeLabel` the full path. Null when `displayName` is already the short one |
| `iconAsset` | Asset name, resolved by the UI |
| `state` | `ready` \| `noConfig` \| `orphanedConfig` \| `notInstalled` |
| `configPathDisplay` | For display and tooltips only — never parsed |
| `configFormatDisplay` | `JSON`, `TOML`, or `Auto-detected · read-only` |
| `offSwitchSummary` | What switching a server off does to this client's file, in prose. Composed native-side from the client's enablement strategy and its other sources, so a client with two of them answers the question twice (§7.8) |
| `serverCount` | |
| `schemaQuirks` | How this client differs; surfaced in the client screen (§7.8) |
| `isReadOnly` | True for custom sources; all write controls must remain unavailable |
| `configurationScope` | `global` \| `profile` \| `workspace` |
| `scopeLabel` | Optional user-visible profile or workspace name |

`orphanedConfig` means a config exists but the client does not — a leftover
`~/.cursor` after an uninstall. Shown rather than hidden.

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
| `enabledIn` | Client id → `enabled` \| `disabled` \| `absent` |
| `originKind` | `configFile` \| `extension` |
| `isBundled` | Extensions ship their own command and are not editable |
| `hasRelativePath` | The command or an argument is resolved against a working directory, so copying this definition into another client may write cleanly and still not start. The fact only — no path crosses |
| `tokenWeight` | `{ estimate, method, isMeasured, measuredAt, percentOfContext, referenceContextWindow }`, or null before a health check |
| `provenance` | `{ sourceKind, packageName, sourceURL, installedVersion, latestVersion, latestCheckedAt, confidence, maintenanceState }` |

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

`gatewayRoutes` contains safe route identity only. Upstream commands, original
definitions and environment values do not cross IPC.

`doctorReports` contains actionable configuration/health findings and opaque
action names. `contractAlerts` contains tool names and model-facing contract
change summaries. Neither contains environment values or tool-call payloads.

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

Marks the named client as restarted and treats its current semantic
configuration as applied. Nil `clientID` marks every client. This command must
only be used after the client was actually restarted.

### `restarts.dismiss` → `StateDTO`

```json
{ "clientID": "cursor" }
```

Hides the restart notice without clearing the pending state. Nil `clientID`
hides all visible restart notices. A later net configuration change makes the
notice visible again.

### `library.chooseDirectory` → `{ "path": string | null }`

No payload. Opens the native directory picker. Cancellation returns `path: null`.

### `library.scan` → `LibraryScanDTO`

```json
{ "path": "/Users/you/Projects" }
```

Scans only the selected local directory. It recognizes known JSON/JSONC/TOML MCP
maps and reports likely Node/Python entry points as suggestions. It never runs a
command, follows symlinked directories or writes a file. Candidate ids are
opaque and valid only until the next scan.

```json
{
  "rootPathDisplay": "/Users/you/Projects",
  "candidates": [{
    "id": "opaque UUID", "name": "filesystem", "location": ".cursor/mcp.json",
    "commandSummary": "npx -y …", "transport": "stdio",
    "environmentKeys": ["API_KEY"],
    "detectedClientIDs": ["cursor"], "missingClientIDs": ["codex"],
    "warnings": []
  }],
  "warnings": []
}
```

Environment keys are safe metadata; values remain native until an explicit local
import. Read-only source maps are never used as write targets.

### `library.preview` / `library.import` → `ImportPreviewDTO` / `BatchImportResultDTO`

```json
{ "candidateID": "opaque UUID", "clientIDs": ["cursor", "codex"] }
```

Preview names the transport, command summary, environment key names and existing
name conflicts. Only built-in writable clients can be targets. `library.import`
requires the same conflict-free preview and then uses the ordinary authoring
pipeline: external-change guard, timestamped backup and atomic write.

### `imports.prompt` → `{ "prompt": string }`

No payload. Returns the exact prompt for an agent that needs to describe a server
Kytto does not know. The prompt requires strict JSON with `schemaVersion: 1` and
allows environment key names only.

### `imports.preview` / `imports.import` → `ImportPreviewDTO` / `BatchImportResultDTO`

```json
{ "json": "{\\"schemaVersion\\":1,\\"servers\\":[…]}",
  "clientIDs": ["cursor"] }
```

The native parser rejects comments, trailing commas, duplicate fields, unknown
fields, duplicate server names and literal environment objects/values. The
preview must be conflict-free before `imports.import` writes. Environment keys
are shown for follow-up setup, but no values are accepted or invented by this
import.

### `settings.get` → `SettingsDTO`

No payload. Only the settings the web layer needs.

```json
{ "theme": "system" | "light" | "dark", "tokenWarningThreshold": 20000,
  "hasCompletedOnboarding": true, "hasConfirmedMatrixWrites": true,
  "showsCustomSources": true, "backupRetention": 20, "sidebarWidth": 196 }
```

The rest of Settings — login item, menu bar and client path overrides —
never crosses this boundary. They are native
concerns, changed in a native window (§3.2), and the web layer has no use for
them.

`theme` is applied as `data-theme` on `<html>`; `system` means the OS decides.

### `onboarding.complete` → `SettingsDTO`

No payload. Marks the first-run screen as seen.

### `settings.open` → `{}`

No payload. Opens the native Settings window and brings the app forward — the
sidebar's Settings row is the same destination as the menu bar's ⌘,. The page
states the intent only; what a settings window is, and how one is shown, stays
in the shell (§3.2).

### `settings.confirmMatrixWrites` → `SettingsDTO`

No payload. Records that the user has seen and accepted the one-time explanation
that a matrix switch immediately changes a client configuration after taking a
backup. The write itself still uses `servers.setEnabled`.

### `settings.setShowsCustomSources` → `SettingsDTO`

```json
{ "shown": false }
```

Folds the read-only custom sources away — out of the sidebar and out of the
matrix, which is the whole point: a machine that has run Claude Code in thirty
projects gets thirty columns nobody can switch. Nothing is forgotten, and the
sources are still discovered, read and counted; they are simply not drawn. The
choice is remembered across launches, which is why it is a setting rather than
web-layer state.

### `settings.setSidebarWidth` → `SettingsDTO`

```json
{ "width": 260 }
```

Remembers the sidebar width the user dragged to. Project scopes put paths in
the sidebar and no fixed width suits a path, so the width is the user's; it has
to survive a relaunch, which is why it is a setting rather than web-layer
state. Native clamps it to a sane range and the reply carries what was actually
kept. The page applies it as the `--sidebar-width` CSS custom property, the
same way the shell applies its window metrics.

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

`chmod 600` on that client's config. Small, reversible, and the only hardening
available while the value has to stay in the file.

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
  "authorizationURL": null,
  "stderr": "dyld[36368]: Library not loaded: …",
  "durationSeconds": 1.2,
  "serverName": "filesystem", "serverVersion": "2026.7.10",
  "resolvedCommand": "/opt/homebrew/bin/node",
  "environmentSource": "Login shell PATH",
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

`needsAuthorization` means the server is healthy and blocked on interactive
consent: its output asked for a browser sign-in (`mcp-remote` against an OAuth
endpoint prints "Please authorize this client by visiting: …" and waits), so
the check saw a timeout that is not a failure. `authorizationURL` carries the
page the server named, when it named one, for display beside the status. The
state is read from the server's own output during the handshake, never from
the exit code.

`message` is rendered natively from the structured failure at reply time, so
improved wording reaches results already on disk; only records written before
the structured field existed still show the sentence stored with them.

`servers.list` and `state.get` carry `health` and `tokenWeight` on each server
from then on, read from Kytto's own store rather than measured again.

### `health.openAuthorization` → `{}`

```json
{ "serverID": "intercom" }
```

Opens the authorization page recorded by the last health check in the system
browser. The web layer names a server and nothing else: the URL that opens is
the one native captured from that server's own output, validated (https, with a
host) before opening. The shell never opens a URL supplied by JavaScript — the
same stance as `updates.openDownload`. Fails if the server's last result is not
`needsAuthorization` or recorded no page.

### `health.checkAll` → `StateDTO`

No payload. Checks every stdio server, one at a time. Sequential on purpose:
these are arbitrary commands, and starting a dozen node processes at once to save
a few seconds is not a trade worth making on someone's laptop.

Emits `health.progress` before each server.

### `provenance.checkLatest` → `StateDTO`

```json
{ "serverID": "github" }
```

An explicit, read-only request to compare one identified npm or Python package
with its first-party registry metadata. The native shell sends only the inferred
package name, never configuration, environment values or process output. No
registry request is made during discovery, launch or an ordinary health check.
The response stores the checked version in memory for the current session and
returns a fresh state. Unsupported or uncertain sources remain informational and
are not called abandoned.

`maintenanceState` is `healthy`, `unhealthy`, `unknownSource`,
`staleButResponsive` or `unchecked`. A stale result means the server responded
successfully while a trusted comparable registry version was newer than the
installed/server-reported version; it is not an automatic package update.

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

Where it goes is the file the backup was taken from, and native honours that only
when it is a file Kytto writes for `clientID` today: the server map (at its
override or its default location), a deny list, or an extension settings file.
Anything else — a backup of a location Settings no longer points at, a read-only
source, a record edited outside Kytto — fails with code `backup` and writes
nothing.

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
same 16px square, which is also what it falls back to if a request fails.

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
| `menu.refresh` | `{}` | User picked File → Refresh Configurations (`Cmd+R`) |
| `menu.find` | `{}` | User picked Edit → Find (`Cmd+F`) |
| `configs.changed` | `StateDTO` | A watched file changed on disk, whoever changed it |
| `settings.changed` | `SettingsDTO` | Native settings were saved |
| `health.progress` | `{ serverName, index, total }` | `health.checkAll` moved on to the next server |
| `updates.progress` | `{ phase, completedBytes, totalBytes, fraction, message }` | A verified updater download or install moved to the next phase |

`configs.changed` fires for Kytto's own writes too. Re-reading is cheap and the
render is idempotent, so the UI settles on the truth rather than trying to work
out which events were its own.

## Not implemented

Health checking remote (HTTP and SSE) servers. Those report `unsupported` with
the reason rather than a misleading grey dot — there is no process to start, so
checking one means speaking streamable HTTP, which is a transport this client
does not have yet.

Claude Code project-scoped discovery is limited to project entries already
present in the shared `~/.claude.json` document. Those entries are shown as
read-only workspace clients with deterministic ids and paths; Kytto does not
crawl repositories, infer precedence outside that document, or write project
scope from the shared UI.

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
