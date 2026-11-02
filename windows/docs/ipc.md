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
authoring surfaces. `portable-setup` is advertised because the native file choosers
and masked-input window exist, and enables the Export and Import actions on the
Profiles screen (§7.13).

`package-safety` is advertised where `doctor.checkPackageSafety` can reach osv.dev
and the npm registry; the UI shows the "Check package safety" button only with it.

`health.http` and `health.oauth` are advertised only where the native path exists:
the first means `health.check` and `health.checkAll` inspect Streamable HTTP
servers, the second that `health.signInRemote` and `health.forgetRemoteAuthorization`
work. The UI shows a remote sign-in button only when `health.oauth` is present,
never from the platform name.

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
| `toolBudget` | `{ toolCount, maskedToolCount, unmeasuredServerIDs, limit, state, pastLimitSummary }` — see below |
| `toolSwitchSummary` | One sentence on what switching a single tool off writes in this client, or null when only Gateway mode can hide its tools (§7.11) |

`toolBudget` counts the tools this client hands its model: every server
`enabled` in it with a passing health check contributes its measured
`toolCount`, less whatever its Gateway route's allow list hides and whatever
the client's own tool switches keep off (`maskedToolCount`, already
subtracted). Servers switched on with nothing to
count — never checked, failed, remote, waiting for sign-in — are named in
`unmeasuredServerIDs`, and while that list is non-empty `toolCount` is a floor
the UI prefixes with `≥`. `limit` is the client's documented tool cap from the
registry, or null when it has none; `state` is `ok` \| `near` (≥ 80 % of the
cap) \| `over`, or null with no cap. `pastLimitSummary` is the registry's
sentence for what the client does past the cap. Computed natively
(`ToolBudget.Evaluate`); the UI does not decide what counts. Custom sources and
Claude Code project scopes carry a count with no cap. The same object is on
every client in `state.get`.

Claude Code project entries from the same `.claude.json` are represented as
opaque `custom.<uuid>` read-only workspace clients. Their `scopeLabel` is the
full native display form of the project path, `shortName` is its smallest
distinguishing leaf (for example `…\shop`), and `displayName` is
`Claude Code · …\shop`. They are never accepted as mutation targets and are
never used as write paths.

A project that only a Claude Code plugin registration names (project or local
scope in `installed_plugins.json`) is the same kind of workspace client, and a
project the shared config and a registration both name — however each spells its
path (`c:/Users/you/app` and `C:\Users\you\app\` are one project) — is one client
with the id its shared-config spelling gave it. A workspace client exists only
when it has at least one server: configured there, or a plugin in effect there. A
user-scope plugin is in effect in every such workspace unless that project's own
settings switch it off, so its `enabledIn` is `enabled` for `claudeCode` and for
each workspace client that did not switch it off. No client or server field says
which of those two things made a workspace appear; `source` on the server does.

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
| `originKind` | `configFile` \| `extension` \| `plugin` |
| `isBundled` | Extensions and plugins ship their own command and are not editable. A plugin is also not toggleable and not copyable; an extension is still switched by its owning client — see `source.readOnly` |
| `source` | `{ kind, displayName, version, readOnly }`, for display: `kind` is `config` \| `custom` \| `extension` \| `plugin`; `displayName` is `Configuration`, `Custom source`, `Claude Desktop extension` or the plugin's own name; `version` is the plugin's version or null; `readOnly` is true for a plugin and for a custom source or workspace, false for configuration and extensions. No path, substitution, working directory or launch variable is part of it |
| `hasRelativePath` | The command or an argument is resolved against a working directory, so copying this definition into another client may write cleanly and still not start. The fact only — no path crosses |
| `tokenWeight` | `{ estimate, method, isMeasured, measuredAt, percentOfContext, referenceContextWindow }`, or null before a health check |
| `provenance` | `{ sourceKind, packageName, sourceURL, installedVersion, latestVersion, latestCheckedAt, confidence, maintenanceState }`, or null |
| `toolsOff` | Client id → names of the server's known tools that client's own file keeps off (§7.11). Clients with no rules are absent |
| `lockedTools` | Client id → `{ tool: rule }` for tools kept off by a broader rule (a glob, a whole-server deny) that switching one tool cannot undo |
| `packageSafety` | The last `doctor.checkPackageSafety` result while it still describes what the server launches, or null |

`provenance` is inferred from the existing definition without contacting a
registry. `latestVersion` and `latestCheckedAt` are filled only by the explicit
`provenance.checkLatest` command; environment values never cross this boundary.

A plugin's server is named by its client: `plugin:<plugin name>:<server>` for
Claude Code, the server's own key for Codex. `command`, `args` and `env` are what
the plugin wrote, placeholders such as `${CLAUDE_PLUGIN_ROOT}` unexpanded.
Expanding them, the plugin's working directory and the client-provided launch
variables are native launch context (`docs/design.md`, registered plugins) and
are not on the wire. A mutation command naming a plugin's copy —
`servers.setEnabled`, `servers.update`, `servers.delete`,
`servers.removeFromClient`, `servers.unify` and the `doctor.applyFix` argument
pin — is refused by the native side with an error naming the installed plugin;
the UI disables those controls from `source.readOnly`, but the refusal does not
depend on it. Toggling a name that is a plugin's in one client
and an ordinary entry in another is refused only for the client whose copy is the
plugin's.

`isMeasured` is what decides whether the UI writes "~" in front of a token
figure. It is true when a real vocabulary produced the number and false when the
old `chars/4` heuristic did, which is the case for anything last checked by a
build from before §7.4 changed its mind. A total that mixes the two keeps the
tilde: a sum is only as measured as its least measured term.

`disabled` and `absent` are different states and the matrix must not conflate
them. `disabled` means configured and switched off — Claude Code's deny list, or
an extension with `isEnabled: false`. `absent` means not configured in that
client at all.

#### What the matrix does with these fields (§7.1.1, §7.1.2)

Scope, source type, density and the inspector's selection are **page state**:
they add no command and put nothing on the wire. The page filters the lists it
already has. It relies on three facts, each stated here as an intent native has to
keep true:

- **Which files a scope covers.** A read-only client with a
  `configurationScope` of `workspace` or `profile` (a Claude Code project, an
  attached file) is chosen by its `id`. The writable client that owns it — the
  context it inherits — is the writable client with the **same `iconAsset`**. A
  project scope therefore has to report its owner's `iconAsset`; a custom source
  with no owner reports `client-custom`, which no writable client shares, and
  shows only itself.
- **What kind of server a row is.** `source.kind` is `config`, `custom`,
  `extension` or `plugin`. A payload without `source` is read from `originKind`.
  A server is also treated as read-only when every client that holds it is
  `isReadOnly`, whatever its `source.readOnly` says, because that is what Kytto
  can and cannot do to it.
- **What a plugin can do.** A server with `originKind: "plugin"` (or
  `source.kind: "plugin"`, or `isBundled` with `source.readOnly`) gets disabled
  switches in every client column. A Claude Desktop extension does not: it keeps
  its switch in the client that owns it.

"Check visible" sends one `health.check` per server id the filters leave, one
after another; "Check all servers" in MCP Doctor is `health.checkAll`. The
sidebar's *MCP Library* and *Skills* entries are gated on the `library` and
`skills` capabilities and open the same sheets the toolbar buttons used to.

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
action names (`runHealthCheck`, `pinResolvedCommand`, `pinPackageVersion`).
`contractAlerts` contains tool names and model-facing contract
change summaries. Neither contains environment values or tool-call payloads.

Each `contractAlerts[].changes[]` entry is `{ kind, severity, toolName, summary,
details }`. `details` is `null` for an added or removed tool and for a change
recorded before 1.0.8 (such records still load); otherwise it is an array of
`{ path, before, after, explanation }` for the model-visible fields that changed:

- `path` is `description`, `annotations.<hint>` or `inputSchema/…`, the latter an
  escaped JSON Pointer (`~` is `~0`, `/` is `~1`). Object keys are compared
  canonically, so a server that only reorders keys reports nothing; arrays keep
  their order and are shown whole.
- `before` and `after` are bounded display excerpts, at most 800 Unicode scalars
  and then a truncation marker; `null` means the field was absent. They are
  never used to decide whether anything changed — that is decided natively on the
  full values, and an excerpt that reads the same on both sides says so in
  `explanation`.
- `explanation` is `null` or prose: a `dry_run`, `dryrun`, `confirm` or `approval`
  field, or a confirmation entry in `required`, asks the reader to verify the
  server's actual enforcement; an annotation is only a hint to the client.
- At most 40 fields are returned per change, followed by one entry with an
  omission notice; at most 500 differing nodes are walked, to depth 12, and a
  schema above 1,000,000 UTF-8 bytes, malformed, or nested past 128 levels is
  compared as raw text and shown as one `inputSchema` excerpt.
An authorization wait adds the info finding `needs-authorization` with action
`runHealthCheck`; it is not classified as a health failure.

`pendingContractImports` is `{ "entries": [{ "serverID", "toolCount", "matching" }],
"error": null | "…" }` — the contract fingerprints a portable setup brought, which
are evidence and never a baseline. `matching` is `null` until a check **made on this
machine, after the import** has passed, and then `true` or `false` for whether it
agrees with the imported fingerprints; a result cached from before the import never
counts, and neither does a failed check. `error` is a fixed safe sentence when the
store cannot be read; the store is not deleted or repaired on the way.

An `unpinned-package` warning — a runner such as `npx`, `uvx` or `pipx run`
fetching a package with no exact version, however the command is spelled
(`npx.cmd`, a full path, `cmd /c npx …`) — carries no version. Its action is
`pinPackageVersion` only when some editable copy can take a version in the
argument it already has; otherwise the finding is advice. The UI offers "Check
latest release" (`provenance.checkLatest`) until the server's
`provenance.latestVersion` is set by a lookup at most 24 hours old
(`provenance.latestCheckedAt`), then "Pin to …" through `doctor.previewFix`.

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
{ "serverID": "github", "action": "pinPackageVersion" }
```

`action` names the finding's repair: `pinResolvedCommand` (the default when it
is absent) or `pinPackageVersion`; anything else is `badArgument`. It does not
write, and it makes no request.

```json
{
  "serverID": "github",
  "serverName": "github",
  "currentCommand": "npx",
  "replacementCommand": "npx",
  "clientIDs": ["codex", "cursor"],
  "action": "pinPackageVersion",
  "packageName": "@modelcontextprotocol/server-github",
  "version": "2025.4.8",
  "argumentChanges": [
    { "clientID": "codex", "current": "@modelcontextprotocol/server-github@latest",
      "replacement": "@modelcontextprotocol/server-github@2025.4.8" },
    { "clientID": "cursor", "current": "@modelcontextprotocol/server-github",
      "replacement": "@modelcontextprotocol/server-github@2025.4.8" }
  ]
}
```

For `pinResolvedCommand`: the configured command, the absolute path verified by
the last passing health check and affected client ids; `packageName` and
`version` are null and `argumentChanges` is empty.

For `pinPackageVersion`: `packageName`, the `version` it would pin, and one
`argumentChanges` entry per client copy whose argument changes — copies can
differ, and the preview is exactly what will be written. `currentCommand` and
`replacementCommand` are both the unchanged executable. The version is the one
`provenance.checkLatest` returned for this package within the last 24 hours;
without that lookup the preview is refused (`appState`), so the network is only
ever reached by the user's own "Check latest release" (§7.10). A server's
self-reported `serverInfo.version` is never used.

### `doctor.applyFix` → `UnifyResultDTO`

```json
{ "serverID": "github", "action": "pinPackageVersion", "version": "2025.4.8" }
```

Applies only a validatable repair through the ordinary multi-client authoring
transaction. Every affected configuration is backed up and written atomically,
and every target is prepared before any is written.
`pinResolvedCommand` (the default) is rejected by a stale or missing passing
check. `pinPackageVersion` requires `version`, and writes only if it still equals
the version the preview showed (`appState` otherwise); it splices that one
`args` string in each JSON or TOML copy — an inline Codex definition included —
and nothing else in the file. A switched-off copy Kytto is holding is patched
where it is held, so it stays off (`parkedUpdated`); one that could not be is
reported in `parkedFailures` rather than failing a write that already landed.

The response is the `servers.unify` shape: `AuthoringResultDTO` plus
`parkedUpdated` and `parkedFailures`, both empty for `pinResolvedCommand`.

### `contract.acknowledge` → `StateDTO`

```json
{ "serverID": "filesystem", "reviewToken": "3f0c9a1b2d4e5f60a7b8c9d0" }
```

`reviewToken` is the opaque value the alert in `StateDTO.contractAlerts` carried
when it was shown. If a check has recorded different changes since, the command
fails (error code `appState`) and nothing is acknowledged: a review accepts what
was read, not whatever arrived meanwhile. It is absent for an imported contract
with no local alert.

Records that a Contract Guard alert has been read, and makes the contract it
showed the baseline the next change is measured against.

An imported fingerprint for the same server is retired in the same step, and
only after a check made here after the import has passed. Both stores are read, and
that requirement is met, before either is written: a pending store that cannot be
read, or one nothing has been checked against, refuses the acknowledgement with the
local alert and the baseline exactly as they were (error code `portableSetup`).
Imported hashes are never copied into the baseline.

This and the first passing check are the only things that move the baseline.
Every passing check compares against it, so an unreviewed change keeps being
reported — and later changes add to it — until it is acknowledged here or the
server reverts it. The new baseline is the last *passing* check's tools, so
acknowledging after a check that failed accepts what the user was shown rather
than leaving the old baseline in place. Acknowledging a server with no alert
changes nothing.

### `activity.list` → `GatewayActivityDTO`

No payload. Returns the newest bounded view of the local Gateway JSONL journal,
including session ids, client/server ids, tool names, duration, success/error
codes and process exit codes. Arguments, results, raw protocol messages and
environment values are intentionally absent.

### `discovery.refresh` → `StateDTO`

No payload. Re-reads every config from disk, off the main thread, and replies
once a pass that started after the request has finished.

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
| `installed plugin` | A Claude Code or Codex plugin provides this server. Its definition is in a cache the client rewrites, so there is nothing here to toggle — turn the plugin off in the client that installed it. |
| `install the plugin there` | The same server asked for in a client that did not install the plugin (the message also says `installed plugin`; test for this one first). Plugins are never copied across clients. |

### `servers.setDisabledTools` → `ToolSwitchResultDTO`

```json
{ "serverID": "github", "clientID": "codex", "disabledTools": ["delete_branch", "create_issue"] }
```

States which of the server's known tools (from its last passing check) are off
in one client's own configuration (§7.11); every other known tool ends up on,
and tools Kytto does not know are left alone. One guarded write with one
backup: Codex `disabled_tools` (or `enabled_tools` when the table has an allow
list), Antigravity `disabledTools`/`enabledTools`, OpenCode's top-level `tools`
map (`<server>_<tool>: false`), Claude Code `permissions.deny` rules
(`mcp__<server>__<tool>`) in its `settings.json`. A save that changes
nothing writes nothing (`requiresRestart: false`, `backupID: null`). Undo is the
same command with the previous `toolsOff` list.

```json
{ "serverName": "github", "clientID": "codex", "disabledTools": ["create_issue", "delete_branch"],
  "requiresRestart": true, "backupID": "config.toml.2026-10-30-101500-120.bak",
  "pathDisplay": "~/.codex/config.toml", "state": { ... } }
```

| Message contains | Meaning |
|---|---|
| `no setting for single tools` | The client cannot say this in its files; offer Gateway mode instead. |
| `is kept off by the rule` | A glob or whole-server rule covers this tool; nothing was written. |
| `changed on disk` | As for every write. |

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

### `doctor.checkPackageSafety` → `StateDTO`

```json
{ "serverID": "github" }
```

Capability `package-safety`. An explicit, read-only check of the package a stdio
server fetches by name (§7.10). Native code sends osv.dev
(`POST https://api.osv.dev/v1/query`) the package name, its ecosystem (`npm` or
`PyPI`) and the version only when the launch pins one; for npm it also reads
`https://registry.npmjs.org/<name>/<version|latest>` to see whether that version
declares `preinstall`/`install`/`postinstall` scripts. Nothing else leaves the
machine, and nothing is requested on launch, during discovery or during a health
check. The result is kept in memory for the session and applies only while the
server still launches the same package and version; it shows up as Doctor
findings (`known-malware`, `known-vulnerabilities`, `install-script`) and as
`ServerDTO.packageSafety`
`{ packageName, version, advisoryIDs, malware, installScripts, checkedAt }`.
Without a pinned version only `MAL-` (malware) records are kept, since OSV then
answers for every release ever published. The offline `look-alike-package`
finding needs no request and carries the `checkPackageSafety` action. Nothing
is switched off automatically.

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

Fails rather than reporting success when nothing changed: when the client has no
config file, or when the file is still readable by another account afterwards
(a volume that does not keep access lists). The UI tells the user the file is now
private, and that has to be true.

### `health.check` → `{ health, state }`

```json
{ "serverID": "github" }
```

Runs a stdio server, or connects to a Streamable HTTP endpoint, does the MCP
handshake, calls `tools/list`, and records what came back. `tools/call` is never
sent. A paginated tool list is followed through `nextCursor` within the same
deadline; a page that cannot be read ends the walk with an inspection note rather
than failing the check. When the server advertises prompts or resources, the same
inspection also requests their first page. Takes up to ten seconds; the native
side runs it off the main thread. A result whose server was deleted or renamed
meanwhile is returned but not recorded, and a check that started before the one
already stored does not replace it.

Only ever on request (§6). Nothing is checked on launch or in the background.

A server is started in the context its owner established, none of which the
caller sends or receives: a plugin's own directory as the working directory
(or the absolute `cwd` an ordinary definition declares), the placeholders
resolved once in command, arguments and environment, and the client-provided
path variables such as `CLAUDE_PLUGIN_ROOT`. A relative command is resolved
against that directory; with none established it is `unsupported`, as before.
A working directory that does not exist is a `failed` check and the server is
not started elsewhere. A command through `cmd.exe` (`npx`, any `.cmd` shim) gets
each argument quoted for it (a double quote inside an argument is doubled, so a JSON
argument arrives intact); an argument a batch file cannot receive literally — a line
break, a backslash directly before a double quote, or a `%NAME%` that cmd would
replace with a variable's value — fails the check without starting anything, rather
than starting it with a different argument.

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

`unsupported` means Kytto cannot check this kind of server — currently only a
legacy SSE endpoint. Streamable HTTP is checked: `initialize`,
`notifications/initialized`, then paginated `tools/list`, with JSON or event-stream
responses matched by JSON-RPC id, the session and protocol headers, and a bounded
best-effort `DELETE` to end the session. The definition's own headers are kept
(environment and bearer forms resolved natively, placeholders expanded once), with
only `Authorization` replaced by Kytto's own sign-in. HTTPS only, or HTTP to the
loopback interface; no redirect is followed, no cookie kept, no ambient Windows
credential sent. Limits: 4 MiB per response, 8 MiB in all, 50 pages, 10,000 tools,
JSON nested at most 128 deep with no duplicate keys, and one 10 second deadline
covering the network, parsing and token counting. Nothing a server says — body,
challenge, JSON-RPC error text — is kept in the result or its message, so a health
result never carries a token or a signed URL. A remote result has
`environmentSource` `Streamable HTTP` and no `resolvedCommand`.

A remote check that finishes after the server was edited, renamed, removed, or
signed in or out meanwhile is returned but not recorded: the name alone is not
enough, the definition and the authorization generation are compared as well.

For a Streamable HTTP server, `needsAuthorization` is a `401`, or a `403` that
carries an authentication challenge; a plain `403` is `failed` ("HTTP 403"). It
never records an `authorizationURL`: sign in with `health.signInRemote`.

For a stdio process, `needsAuthorization` means the failed handshake's captured
stderr explicitly said that the process is waiting for browser authorization. It is neither a
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
authorization, and every Streamable HTTP server, one at a time. Legacy SSE servers
have no check and are skipped. Sequential on purpose:
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

### `health.signInRemote` → `StateDTO`

```json
{ "serverID": "remote" }
```

Capability `health.oauth`. An explicit, native-only sign-in of Kytto's own remote
checks for one Streamable HTTP server; it never runs at launch, never changes a
client configuration and never signs another MCP client in. Only the server id
crosses: no URL, endpoint, code, client ID or token is accepted from or returned to
the web layer.

1. Native discovers the protected-resource and authorization-server metadata and
   validates it: HTTPS, the metadata's `resource` equal to this exact resource, a
   same-origin metadata hint, the first advertised issuer matching the issuer in its
   own document, and `S256` PKCE advertised. A resource URL with a query string is
   refused, because the resource is sent to the issuer.
2. A native confirmation names the server, the provider (issuer) and the requested
   scopes. Declining returns the unchanged state.
3. A short-lived listener is opened on `127.0.0.1` only, on a free port, at
   `/oauth/callback`. Nothing is installed and no protocol, URL reservation or
   firewall rule is registered; the listener exists until the sign-in ends.
4. The provider's public dynamic registration is used when advertised (the public
   client carries no secret, and a secret in the reply is refused). Otherwise a native
   prompt asks for a public client ID registered for that loopback redirect; cancelling
   returns the unchanged state.
5. The system browser is opened on the validated HTTPS authorization URL (PKCE with a
   random verifier and `state`, the exact resource, the scopes). A native window
   waits and can be cancelled; giving up is an error.
6. The callback is accepted once, only for the exact route, host and `state`; the
   `iss` parameter must match, an error or a repeated parameter is refused, and the
   transaction is spent before the token request. Stray or replayed requests are
   answered and ignored.
7. The tokens are validated and saved in the system credential store under Kytto's own
   prefix, bound to the canonical resource, as small checksummed pieces; a record too
   large to store safely is refused, never truncated. A new sign-in wins over a
   refresh that started before it.
8. A check of the server follows, so the reply shows what the new authorization does.

An explicit later check may refresh an expiring token (and keeps a rotated refresh
token); nothing refreshes in the background. A refresh that the provider refuses, or a
saved record that cannot be read, reads as `needsAuthorization` with the message to
sign in again. One sign-in runs at a time (`A sign-in is already open.`).

### `health.forgetRemoteAuthorization` → `{}`

```json
{ "serverID": "remote" }
```

Capability `health.oauth`. Deletes only Kytto's own saved sign-in for this server's
exact resource. The authorization generation is raised before the delete, so a
refresh already in flight cannot store its tokens afterwards. Forgetting something that
is not saved is not an error. No URL or token is accepted from the web layer.

### `setup.export` → `PortableExportDTO`

No payload. Capability `portable-setup`. A native window explains what an export
holds and offers an unchecked opt-in for reviewed contract fingerprints; a native
save dialog then chooses the file. The export carries writable, built-in client
definitions and profiles, with a required input in place of every credential, URL,
private path and argument Kytto cannot prove public. Installed plugins and
extension bundles, custom sources and everything else are not copied, and nothing
is read from the credential store. A destination that is, or aliases, a live client
configuration or one of Kytto's own stores is refused — a symbolic link, a junction
or a mount point on the way does not change that, and neither does reaching it through
a share of this computer, which is refused whatever it points at.

```json
{ "cancelled": false, "serverCount": 3, "profileCount": 1, "warnings": [] }
```

Cancelling either dialog is `{ "cancelled": true, … }` with zero counts, not an
error. The file is the same format the macOS app writes and reads.

### `setup.previewImport` → `PortableImportPreview | null`

No payload. A native open dialog chooses the file, which is read as untrusted
input: a size cap, strict JSON, valid UTF-8, no duplicate or unknown keys and every
value of the declared type. A native window then asks where to import (installed,
writable, built-in clients only) and for every input the file could not carry —
masked for secrets, URLs and plain values, with file and folder browsing for paths.
**None of it crosses this boundary:** not the file's path, not what was typed, not a
command, argument or URL from the file. A refused input keeps the window open with
everything as typed; cancelling returns `null`.

```json
{ "id": "opaque-preview", "serverNames": ["memory"], "profileNames": [],
  "targetClients": ["cursor"], "pathDisplays": ["%USERPROFILE%\\.cursor\\mcp.json"],
  "pendingContractCount": 0, "warnings": [], "enabled": false }
```

The id names a plan Kytto holds natively: the digests of the file, of every config it
would write or only read, and of Kytto's own stores, plus the typed values. A plan is
single-use, expires after ten minutes, at most four are retained (a fifth evicts the
oldest), and changing the client paths or sources invalidates them all. Nothing is
written. `enabled` is always `false` from the application.

### `setup.applyImport` → `StateDTO`

```json
{ "previewID": "opaque-preview" }
```

Commits the plan, once. It checks every digest again and refuses with a message
rather than merge if anything moved; refuses a name or profile that now exists, and a
name an installed plugin or extension already provides to that client (the preview
refuses it first);
backs up each existing config; and writes only through the §6 pipeline. Kytto's own
stores are written first and put back byte for byte if the config transaction
fails; if anything cannot be put back, the error says which, and is never hidden.
**Imported servers are always switched off**, in each client's own way — an
`enabled = false` on a Codex table, a Claude Code deny-list entry, a definition
Kytto holds for the presence-only clients — and this command has no parameter that
could say otherwise. Existing parked copies are never replaced. No server is
launched. Contract fingerprints stay pending (see `pendingContractImports`).

Failures use the code `portableSetup`.

### `setup.discardImport` → `{}`

Same `{ "previewID": … }` payload. Releases the plan and the values it holds when the
preview is cancelled, including Escape and a click on the backdrop. An id that no
longer names a plan is not an error.

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

Renaming works — the name is the config key, so only the key changes. A server
switched off through a deny list stays switched off: its entry is renamed with
it. A rename that only changes letter case is still a rename.

**Environment values:** an entry with `"value": null` means *keep whatever is
already in the config* — per file, so each client keeps its own value even when
the clients disagree. The form never receives secret values, so it cannot send
them back; for a client that does not have the key yet, the native side falls
back to the server's existing value. An empty string would overwrite a real
token, so the UI sends `null`, not `""`. When the user renames a key in the
form, the entry carries `"previousKey"` (the old name, never a value) so a kept
value moves to the new name instead of being lost.

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

Health checking legacy SSE servers. A remote Streamable HTTP server is checked
over HTTP (capability `health.http`, §7.3) and may be signed in to (`health.oauth`);
a legacy SSE server reports `unsupported` with the reason rather than a misleading
grey dot, because that is a different transport this client does not speak.

General project-scoped configs (`.mcp.json`, `.cursor/mcp.json` and similar
repository-local files) remain out of scope by §4. Claude Code's explicit
`.claude.json` `projects` map is the v1.0.5 exception: it is surfaced as
read-only workspace clients because it can be inspected without crawling
projects or introducing a writable project axis. Project and local plugin
registrations in `installed_plugins.json` extend the same exception, and no
other: nothing looks for a project on disk.

Plugin activation Kytto does not infer, because nothing on disk says what the
client will load: session-only plugins, plugins synced from an account, plugins
a managed (enterprise) settings file switches on, a Claude Code config directory
relocated with `CLAUDE_CONFIG_DIR`, and Codex plugins an account manages in a
cache with no explicit registration in the config. See the registered-plugins
subsection of `docs/design.md` §4.

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
