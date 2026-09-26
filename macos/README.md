# Kytto MCP

> **Kytto MCP** — a free, open-source MCP control panel for macOS.
> Manage every client's MCP servers from one screen; see and control MCP traffic through an optional local gateway.

Kytto MCP is a native macOS app that puts the [Model Context Protocol](https://modelcontextprotocol.io)
servers configured across your AI clients on one screen. Claude Desktop, Claude
Code, Cursor, VS Code and Codex each keep MCP servers in their own config file,
in their own format; Kytto reads all of them and shows a **matrix** of which
server is enabled in which client, with a switch in every cell.

- **Cross-client matrix** — enable or disable a server per client from one place.
- **Token weight** — how much context each server's tool definitions consume.
- **Safe writes** — every change is a minimal splice into the existing file,
  with a backup, an external-change guard and an atomic write. Comments, key
  order and unknown keys survive (§6).
- **Health checks** — start a server on request, run the MCP handshake and show
  its tools, or its stderr verbatim when it fails.
- **Secrets** — keep API keys in the macOS Keychain rather than scattered across
  config files.
- **Optional local gateway** — an opt-in stdio relay for activity, tool masking
  and future policy (§3.3, [docs/gateway.md](docs/gateway.md)).

Kytto is not a server catalog, and it never writes anything until you make a change.

## Requirements

- macOS 15 or later
- Xcode 16 or later (Swift 6 toolchain)
- Python 3 (optional — only to serve the web UI harness during development)
- Node.js (optional — only for the web UI contract tests)

## Build and run

```sh
git clone https://github.com/heyitsjakub/KyttoMCP.git
cd KyttoMCP/macos
open KyttoMCP.xcodeproj        # then Product → Run on the KyttoMCP scheme
```

Or from the command line:

```sh
xcodebuild -project KyttoMCP.xcodeproj -scheme KyttoMCP -configuration Debug \
  -destination 'platform=macOS' build
```

The project carries the maintainer's signing team. To build under your own
Apple ID, pick your team under *Signing & Capabilities* for the app target, or
pass `DEVELOPMENT_TEAM=<your team id>` to `xcodebuild`.

The web UI can also run in a browser against fixtures, with no native code at all:

```sh
cd web && python3 -m http.server 8765
# open http://localhost:8765/dev.html
```

## Running tests

```sh
# Core logic (parsing, registry, write pipeline, health checks) — the fast loop.
cd KyttoCore && swift test

# One suite. --filter matches the type name, not the @Suite display string.
cd KyttoCore && swift test --filter TOMLRoundTripTests

# App and UI tests.
xcodebuild -project KyttoMCP.xcodeproj -scheme KyttoMCP \
  -destination 'platform=macOS' test

# Web UI contract tests.
node --test web/js/dev/ui-contract.test.js
```

## Project layout

```
KyttoMCP/          AppKit + SwiftUI shell: window, menus, menu bar item,
                   WKWebView and the IPC router. Holds no config logic.
KyttoCore/         SwiftPM package with everything that is not UI: parsing,
                   the client registry, discovery, the write pipeline, health
                   checks, secrets, and the kytto-mcp-proxy gateway helper.
web/               The UI — vanilla HTML/CSS/JS, ES modules, no build step.
                   House rules in web/README.md.
KyttoMCPTests/     App tests (Swift Testing).
KyttoMCPUITests/   UI automation (XCTest).
docs/ipc.md        The web ↔ native contract. Every command has an entry.
docs/gateway.md    The optional stdio gateway.
scripts/           Release packaging.
```

## Contributing

Issues and pull requests are welcome. Before you start:

- Read §6 below. Writing to someone else's config file is the whole risk of this
  app: every write goes through `ConfigWriter`, and edits are span replacements,
  never a regenerated file. Parser or writer changes need round-trip tests.
- Code comments cite this document by section (`§6.3`, `§3.2`). Keep that
  convention, and do not renumber the sections below.
- Adding an IPC command means adding its entry to [docs/ipc.md](docs/ipc.md) in
  the same change. The web layer stays platform-neutral — no OS checks in `web/`.
- Follow [web/README.md](web/README.md) for UI work, and describe the affected
  layer, the testing you did, and any config-write or IPC impact in your pull
  request. Include screenshots for UI changes.

[AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) summarise the same rules for
coding agents.

## License

MIT — see [LICENSE](LICENSE). Bundled third-party files are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

## About this document

This document is the source of truth for scope and design. It began as the build
plan and is now the record of what was decided and why — code cites it by section
(`§6.3`, `§7.1`) instead of restating the reasoning, so those references have to
keep resolving. When a decision here stops being true, change it here first.

---

## 1. Positioning

**What this is now:** a control panel over MCP configuration. One place where
you see *which server runs in which client*, and can change it with one click.

**What it grows into:** a local MCP control plane. Gateway mode adds live
sessions, tool-call observability and deterministic policy enforcement without
turning the basic manager into a cloud service.

**What this is NOT:** a catalog of MCP servers. Catalogs are everywhere and free. The value is the cross-client matrix, not the list.

**Target user:** developers running AI coding agents with MCP servers across multiple clients. Power users who have 5+ servers and 3+ clients and have lost track of what is enabled where.

**License:** free and open source under the MIT license. Distributed directly, not through the App Store — see §9.

**The first three differentiators:**
1. Cross-client matrix (enable/disable per server per client from one screen)
2. Token weight per server and per tool (show how much context each server's
   tool definitions consume — and, through the gateway, spend less of it)
3. One optional local gateway for visibility and permissions across clients

---

## 2. Non-goals for v1

Out of scope for now. Each one costs months and none of them is what the app is for.

- Authoring / scaffolding new MCP servers
- Mandatory gateway migration in the first stable release
- Capturing tool arguments or results without explicit opt-in
- Team sync, cloud accounts, shared config
- Marketplace with payments
- Auto-updating installed servers
- Project-scoped configs (see §4 — global scope only in v1)

---

## 3. Architecture

Native shell + web UI.

**Web stack: vanilla HTML/CSS/JS, no framework.**. See §3.1 for structure discipline — vanilla is the right call here but needs rules to stay maintainable.

**Platform sequencing: macOS first, Windows after M6.** See §3.2 — this creates a specific risk that must be managed from M0.

```
┌─────────────────────────────────────────┐
│  Native shell                            │
│  macOS: Swift + WKWebView    (v1)        │
│  Windows: C# or Rust + WebView2 (M7)     │
│                                          │
│  Owns:                                   │
│   - window, menu bar, menu bar item      │
│   - file read/write (atomic + backup)    │
│   - FSEvents / ReadDirectoryChangesW     │
│   - Keychain / Credential Manager        │
│   - process spawn + stdio                │
│   - native dialogs, notifications        │
│                                          │
│  ┌───────────────────────────────────┐  │
│  │  Web UI (HTML/CSS/JS)              │  │
│  │  Renders: matrix, sheets, settings │  │
│  │  No business logic. No file access.│  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

**Rule: the frame is native, only the content is web.** Users do not detect a wrapper because of HTML inside. They detect it because the app behaves foreign. Non-negotiable native pieces:

- System window chrome + traffic lights
- Full native menu bar with all standard items
- `Cmd+,` settings, `Cmd+W`, `Cmd+F`
- Native right-click context menus
- Native file dialogs
- System notifications
- Menu bar item for quick toggle — **must be native, never a hidden webview window**

In the web layer: system font stack (`-apple-system` / `Segoe UI`), respect system accent + dark mode, disable text selection where not meaningful, disable image drag, disable rubber-band scroll where inappropriate.

**IPC:** define a single typed message channel between web and native. Every native capability is one named command with a JSON payload. Keep the surface small and document each command in `docs/ipc.md` as it is added.

### 3.1 Web layer structure (vanilla, no framework)

Vanilla is correct for this app — the UI surface is small and the app should stay a few megabytes. But the matrix is N servers × M clients of live toggle state, and vanilla degrades into spaghetti exactly at that kind of screen. Rules:

- **One state object, one render function.** All UI state lives in a single `state` object. Any mutation goes through a `setState()` that triggers a re-render. Never mutate DOM directly from an event handler.
- **Render from state, always.** The DOM is a projection of `state`, never a second source of truth. Do not read values back out of the DOM.
- **Event delegation.** One listener on the table container, dispatch by `data-action` / `data-server-id` attributes. Do not attach a listener per toggle.
- **No innerHTML with interpolated user data.** Server names, commands and error strings come from config files and process stderr. Use `textContent` or escape explicitly.
- **Split by screen, not by type.** `matrix.js`, `serverDetail.js`, `addServer.js`, `settings.js` — each exports a `render(state)`. Avoid a generic `components/` folder at this size.
- **No build step if avoidable.** ES modules loaded directly. Keeps the toolchain at zero and the bundle honest.

If the matrix render becomes a performance problem (unlikely under ~100 servers), diff at the row level before reaching for a framework.

### 3.2 Mac-first without painting into a corner

Windows lands at M7, but the risk is that macOS assumptions leak into the shared layers between now and then — and Windows stops being a port and becomes a rewrite. Discipline from M0:

- **The IPC contract is platform-agnostic.** Commands describe intent, not implementation. `secrets.store(ref, value)` — not `keychain.addGenericPassword(...)`. `config.listClients()` — not anything that names a macOS path.
- **The web layer never sees a filesystem path it has to interpret.** It receives display strings and opaque ids. Path construction, separators and env var expansion all live native-side.
- **No platform names in the web layer.** No `if (mac)` branches in UI code. If behaviour must differ, the native side reports a capability flag and the UI branches on the capability, not the OS.
- **Client registry is data, not code.** §4's descriptors already carry per-platform path arrays. Fill in the Windows paths as you go even though nothing consumes them yet — it costs minutes now and hours later.
- **Write one throwaway Windows shell at M0.** Just a window loading the same web UI, nothing else. It proves the web layer is portable and it will never be cheaper to find out than at the start. Then park it until M7.

That last one is the highest-value item in this section. Do not skip it.

### 3.3 Direct mode and Gateway mode

The existing manager remains **Direct mode**: clients launch their configured
servers themselves. It is the recovery path and must keep working if the helper
is removed. The app never silently migrates a working configuration.

**Gateway mode** is opt-in per server and client. For stdio, the client launches
the bundled `kytto-mcp-proxy` helper with opaque route ids; the helper launches
one real upstream process for that one client connection and relays exact MCP
bytes in both directions. A centralized Kytto service may collect events and
policies later, but upstream stdio sessions are not shared between clients.

The first gateway milestone records only session and tool-call metadata: client,
server, tool name, request id, latency, success and error code. Arguments,
results, environment values and stderr are not persisted. Content capture is a
separate future opt-in with redaction, retention and an explicit privacy warning.

Every migration must use §6's backup and atomic-write path and show the exact
config preview. Every gateway definition must have a one-click **Restore direct
configuration** action. A firewall-enabled route fails closed when policy cannot
be evaluated; an observability-only route may offer a clearly labelled direct
fallback.

---

## 4. Client support

### v1 clients (CONFIRMED)

| Client | Config location (VERIFY — these move) |
|---|---|
| Claude Desktop | macOS `~/Library/Application Support/Claude/claude_desktop_config.json`<br>Windows `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Code | `~/.claude.json` (global scope) |
| Cursor | `~/.cursor/mcp.json` |
| VS Code | user-level MCP config |
| Codex | `~/.codex/config.toml` (`[mcp_servers.<name>]`)<br>Windows `%USERPROFILE%\.codex\config.toml` |

Deferred to v1.1: Windsurf.

**Codex is the client that proves §4's point.** It is the only one in TOML, so
`format` on the source descriptor is real rather than aspirational, and the
per-server shape does not travel: copying a definition into or out of Codex has
to be *rendered* from the model rather than moved as text. It also has the best
off switch of the five — `enabled = false` on the server's own table, so a
disabled server keeps its definition and nothing has to be parked.

Two front ends share one config: the ChatGPT app (bundle id `com.openai.codex` —
the display name is not the identifier) and the `codex` CLI. Like Claude Desktop
it has a second, read-only source: plugins under `~/.codex/plugins` can bundle
their own `.mcp.json`. That tree is a cache the client repopulates, so those
servers are shown and never written.

**IMPORTANT:** these paths change between releases. Do not hardcode them inline. Put every path in a single `clients/` registry module with one descriptor per client, so adding or fixing a client is a one-file change. Verify every path against current docs before writing code — training data on this is unreliable.

Each client descriptor needs:
```
{
  id, displayName, iconAsset,
  configPaths: { darwin: [...], win32: [...] },
  format: "json" | "toml",
  serversKey: e.g. "mcpServers" | "servers",
  schemaQuirks: notes on how this client differs,
  detectInstalled: () => bool
}
```

Config schemas differ between clients. Normalize into an internal model on read, denormalize on write. Never assume two clients accept the same JSON shape.

### Scope decision (v1)

The five built-in clients remain global/user-level and writable. A user may also
explicitly attach a named JSON, JSONC or TOML configuration as a **read-only
custom source** and label it `Global`, `Profile` or `Workspace`. Kytto recognizes
only tested top-level server maps (`mcpServers`, `servers`, `mcp_servers`), shows
unknown or ambiguous documents as diagnostics, and never scans projects or
writes a custom source. Native workspace discovery and scope precedence remain
deferred until client-specific behavior is verified.

---

## 5. Data model

Internal normalized server record:

```
Server {
  id                 stable local id
  name               display name / config key
  transport          "stdio" | "http" | "sse"
  command            e.g. "npx"
  args               ["-y", "@modelcontextprotocol/server-github"]
  env                { KEY: { source: "keychain" | "literal", ref } }
  url                for http/sse transports
  enabledIn          { claudeDesktop: bool, cursor: bool, ... }
  health             { status, lastChecked, error, toolCount }
  tokenWeight        { estimate, method, measuredAt }
  origin             which client config it was first discovered in
}
```

State is derived from client config files, not stored separately as the source of truth. Kytto keeps its own metadata store (health results, token measurements, catalog origin, secret references) in app support dir, keyed by a stable server id. **Config files remain authoritative for what is enabled.**

Gateway routes require a durable UUID independent of the display name. Policy
identity is `route id + tool name + tool schema hash`, with client and profile or
project as optional scope. A rename must not lose audit history, while a changed
tool schema must not silently inherit a security decision made for the old one.

---

## 6. Critical safety rules

This is the section that decides whether the product survives. Breaking someone's config is the one mistake that gets the app deleted and posted about.

### Config writes

1. **Back up before every write.** Timestamped copy in app support dir. Keep last 20 per client. A backup carries every secret the config does, so it is owner-only (`0600` files in `0700` folders), and a restore only ever writes a file the client registry says Kytto manages.
2. **Write atomically.** Temp file in the same directory, `fsync`, then `rename`. Never truncate-and-write in place.
3. **Preserve everything you did not touch.** Client config files contain unrelated keys. Parse, modify the servers key only, re-serialize preserving key order and formatting where possible. Never rewrite the file from your own model wholesale.
4. **Watch for external changes.** FSEvents on macOS, ReadDirectoryChangesW on Windows. If the file changed on disk since last read, do NOT merge blindly — prompt the user.
5. **Expose backups in the UI.** A visible "Revert" action, not a hidden folder. This is a trust feature, make it visible.
6. **Never write on app launch.** Read-only until the user makes an explicit change.

### Secrets

API keys usually sit in plaintext in config files and get committed to git by accident. Kytto should make that easy to avoid.

- Store recoverable copies in Keychain (macOS) / Credential Manager (Windows)
- In Direct mode, say honestly when a client still requires the literal value
- In Gateway mode, write only an opaque route and inject secrets at upstream spawn
- Never log secret values, never render them in the UI after entry (show `••••`, offer reveal behind a confirm)
- If a client cannot consume a reference and requires a literal value, warn the user explicitly before writing it

### Process spawning

Health checks spawn arbitrary commands. That is inherent to MCP, but:
- Never spawn automatically on launch. Health check is user-initiated, or explicitly opted into.
- Timeout every spawn (10s default), kill the process group on timeout
- Capture stderr and surface it verbatim in the error state — this is the most useful thing you can show a user whose server is broken

---

## 7. Feature spec

### 7.1 Matrix (main screen)

Row = server. Column = client. Cell = toggle.

Columns: server name + command, token weight, then one column per detected client.

Row states:
- green dot — health check passed
- red dot — health check failed, show error snippet in the subtitle
- gray dot — not checked yet
- warning-tinted row — token weight above threshold (default 20k)

Header shows total configured / active count and summed token weight of enabled servers.

Footer bar shows pending-restart warning: *"Restart Claude Desktop to apply 2 changes"* — clients hold config in memory and only pick up changes on restart. Do not try to work around this, just state it clearly. Also show last backup timestamp.

### 7.2 Add server

Two paths:
- **Manual:** command, args, env vars, transport. The power path, build this first.
- **Catalog:** a small bundled list of popular servers with prefilled commands. Ship a static JSON list in v1 — no remote fetching, no registry API.

On add, offer to run a health check immediately. On success, show discovered tool count and token weight before the user commits to enabling it anywhere.

### 7.3 Health check

Spawn the server, perform MCP handshake, call `tools/list`, record tool count,
protocol version, advertised capabilities, full tool input schemas and tool
annotations, then capture errors. When advertised, inspect the first page of
`prompts/list` and `resources/list` without making an optional-list failure
invalidate a successful tools handshake. Store the result with timestamp.

A server whose output asks for interactive consent — `mcp-remote` against an
OAuth endpoint prints an authorization URL and blocks — is recorded as
*waiting for authorization*, its own state with its own colour, never as
failed. The check saw a timeout, but the server is healthy and waiting on the
user; painting it red would put a false alarm on every OAuth-backed remote.
The state is read from the server's own output, not the exit code, and the
authorization page it named is offered to the user.

Failures are stored structured (which error, with its parameters) and the
sentence shown for one is composed at display time, so improved wording
reaches results already on disk instead of being frozen into them.

### 7.4 Token weight

Serialize the tool definitions returned by `tools/list` and count the tokens.

**Method: the bundled cl100k_base vocabulary**, measured per server and per
tool. Show as: absolute number, and percentage of a 200k context window.

This reverses the original v1 decision, which was a character approximation
(chars / 4) on the grounds that ranking and order of magnitude are what a user
acts on and no vocabulary is worth the megabytes. Two things changed:

1. **The number stopped being only a ranking.** §7.11 lets a user hide
   individual tools, and "hiding these six saves ~12k" is a promise about a
   specific number rather than a comparison between servers.
2. **The heuristic was wrong in a consistent direction.** Measured against the
   reference implementation, chars / 4 overstates a realistic tool definition by
   roughly a fifth — tool JSON is dense with ordinary English and repeated
   structural punctuation, both of which the vocabulary compresses well. Every
   server looked more expensive than it was.

The cost is ~1.7 MB of rank table in the bundle, paid once. A build without the
table falls back to chars / 4 and says so: the UI writes "~" in front of a
figure only when the number did not come from a vocabulary, and a total that
mixes the two keeps the tilde.

Per-tool counts are measured on each tool's verbatim entry, so they sum to
slightly less than the server's total — the brackets and separators between
entries belong to no single tool.

### 7.5 Server detail

Full command, resolved env (secrets masked), transport, resolved executable and
PATH source, discovered tools with expandable schemas and annotations, prompt
and resource counts, last health result with raw stderr, per-client enablement,
edit and delete. Offer a copyable support report, but omit environment values
and raw stderr from the copied text because both may contain credentials.

### 7.6 Settings

Client paths (with override), backup retention, token warning threshold, theme, launch at login and menu bar item on/off. Kytto sends no telemetry.

### 7.7 Menu bar item

Native. Lists servers with quick enable/disable for the most-used client. Shows aggregate health at a glance. This is a retention feature — it keeps the app present without the window open.

### 7.8 Client screen

Reached by clicking a client in the sidebar. The matrix answers *which server is where* and pays for it in width — a client is a 15px icon and a column of switches. This screen answers the questions about one client that the matrix structurally cannot:

- **What is in this file**, with the same switch the matrix uses, and what could be added to it (servers configured in other clients, minus the ones that cannot travel — an extension bundle belongs to the client that installed it).
- **What this client costs in context.** The matrix header sums every enabled server across all clients; that total is not a share of anything real. A context window belongs to one client, so the percentage only means something here (§7.4).
- **What switching off does here.** Presence, deny list, extension flag, inline flag — §4's table is the substance of the matrix and this is the only screen with room to say it in words. Both this and `schemaQuirks` are prose composed native-side from the client registry: the web layer displays it and never decides it.
- **The client's own backups**, revertable in place, next to the switches that caused them.

Not on this screen: diagnostics, which the content pane already carries on every screen and must not appear twice; and anything that would let a config be edited in a way the matrix cannot express.

### 7.9 Profiles / MCP stacks

A profile is a named local set of normalized server ids. It is not a second
configuration source and it is never applied automatically. Applying one to a
chosen client is an exact operation: enable every member and switch off every
other active server in that client. Show the complete enable/keep/switch-off
preview and estimated context cost before confirmation.

Every resulting change goes through the ordinary §6 toggle pipeline, including
backup, external-change protection and atomic write. Empty profiles are valid
for a minimal setup. If one independent server cannot be changed safely, report
it explicitly while retaining successful changes; never claim the client fully
matches the profile when it does not.

Profiles live only in Kytto's metadata store, are not created on launch, and
follow server renames. Deleting a profile never edits any client config.

Profiles may carry an optional token budget. Context Optimizer compares measured
tool-contract weight with that budget, calls out unmeasured and unusually heavy
servers, and detects duplicate tool names inside the stack. Recommendations are
advisory; Kytto never removes a member automatically.

### 7.10 MCP Doctor and Contract Guard

MCP Doctor turns config and health evidence into actionable findings. Automatic
repairs are deliberately narrow: v1 may pin only an absolute executable path
already verified by a successful health check, after an exact preview and through
the ordinary backup/atomic-write transaction. Stderr-based guesses remain advice.

Contract Guard compares consecutive successful `tools/list` results. Surface
added and removed tools plus changes to descriptions, input schemas and safety
annotations. The compared contract is model-facing behavior and contains no
tool-call arguments or results.

### 7.11 Tool masking

A gateway route may carry an exact list of the tools it exposes. Hidden tools
are removed from `tools/list` results on their way to the client, and calls to
them are refused by the helper with a JSON-RPC "method not found" before they
reach the upstream server.

This is the only feature that *reduces* the number §7.4 measures rather than
reporting it, and it is the reason Gateway mode is worth turning on for someone
who does not care about observability. Masking is per route — one server in one
client — because the same server can reasonably be a full toolbox in an editor
and three read-only tools in a chat client.

It is also the only place Kytto stops being a transparent relay, so the scope is
deliberately narrow:

- A route with no list keeps forwarding bytes untouched. §3.3's guarantee is
  given up only by a route that has actually been narrowed.
- Exactly two message kinds are altered: a `tools/list` result, and a
  `tools/call` request for a hidden tool. Prompts, resources, notifications,
  the handshake and anything that is not valid JSON pass through.
- The list is closed. A tool the server begins offering after the list was
  chosen stays hidden until the user chooses it, because a context budget that
  quietly grows when a server updates is not a budget. §7.10 already reports the
  new tool.

Setting a list writes no config file — the client already points at the route
id — but the client must still be restarted, so it counts as a pending change
like any other.

### 7.12 Drift and unifying

One row in the matrix can be several definitions on disk. Merging them by name
is what makes the matrix readable — it is the product — but the merge is also
where the evidence used to go: discovery noticed a mismatch, raised a diagnostic
reading "configured differently … the command shown is the first one found", and
threw the rest away. That sentence names a problem and then withholds everything
needed to act on it.

So every client's copy is kept, and a server whose copies disagree carries a
**drift report**: which fields differ, and which clients hold which definition.
Clients holding byte-identical definitions are one variant, largest group first,
so the copy most clients already agree on is the one offered first. Drift is not
an edge case in a tool whose subject is several copies of the same
configuration.

`environmentValues` is the one field that reports something it does not carry.
Two clients setting `GITHUB_TOKEN` to two different things is worth seeing, and
the values themselves never cross the boundary — the finding is stated as a fact
about the key (§6).

**Unifying** rewrites the other copies to match one client's. It is the sibling
of ordinary editing, for the case editing cannot express: editing applies one
new definition everywhere because a server is one row, while unifying applies
one client's *existing* definition to the others. Neither is per-client editing;
the point of both is that the copies end up the same.

Three things constrain it:

- The command names a **client**, not a definition. The definition is assembled
  natively from that client's own entry, environment values included. Sending it
  round trip would put a token on the boundary twice, and it does not cross once.
- Every write goes through the §6 pipeline — external-change guard, backup,
  atomic write — exactly as a toggle does.
- An extension bundle or a plugin takes part in the comparison and never in a
  write. A name shared by a bundle and a config entry is exactly the confusion
  worth surfacing, and installed software is not configuration (§4).

A copy that is switched off in a presence-only client is not in that file at
all; Kytto is holding its bytes so switching it back on restores them. Those
bytes are a copy like any other and can drift like one, so unifying updates them
where they actually live. Writing them into the file instead would be
indistinguishable from switching the server *on*, which is why the two outcomes
are counted apart and only a real config write asks anyone to restart.

---

## 8. Milestones

The order the app was built in, kept as a record. Each milestone ended with
something runnable. M0–M6 are done; M7 is open.

**M0 — Shell**
macOS: Swift + WKWebView loading local web UI, typed IPC channel, native menu bar. No MCP logic.
Also: one throwaway WebView2 window on Windows loading the same UI, then park it (§3.2). Do not build Windows features — just prove the web layer renders and the IPC channel design is portable.

**M1 — Read-only discovery**
Client registry, detect installed clients, parse all configs, normalize into internal model, render the matrix read-only. No writes at all. Checked against real machines before anything could write.

**M2 — Safe writes**
Backup, atomic write, format preservation, file watching, external-change prompt, revert UI. Toggles in the matrix become live. **Spend real time here.** Write tests that assert unrelated keys survive a round trip.

**M3 — Add / edit / delete server**
Manual path first, then the static catalog. Restart-required footer bar.

**M4 — Secrets**
Keychain / Credential Manager integration, env injection at spawn, masked UI.

**M5 — Health check + token weight**
Process spawn with timeout, MCP handshake, `tools/list`, error capture, token estimation, matrix status states.

**M6 — Polish + ship (macOS v1.0)**
Menu bar item, settings, empty states, error states, onboarding on first run (detect clients, show what was found), notarization, in-app updates.

**M7 — Windows**
Native shell in C# or Rust + WebView2. Implement the same IPC command set against Windows APIs: Credential Manager for secrets, `ReadDirectoryChangesW` for watching, `%APPDATA%` paths in the client registry, native tray item. The web layer should need zero changes — if it needs more than trivial ones, §3.2 was violated somewhere and that is worth finding rather than patching.

M2 was the milestone that took longest, and it is still where most of the risk
lives. M7 is the open one: the Windows shell is not in this repository yet, and
contributions are welcome — see `docs/FUTURE_MCP_ROADMAP.md` §15 for what it has
to cover.

### Advanced control-plane track

This track builds on the shipped manager; it does not replace or destabilize it.

**G0 — Transparent stdio gateway preview**
Build and test `kytto-mcp-proxy`, byte-for-byte relay, private metadata-only
JSONL events and session identity. Developer opt-in only; no config migration UI.

Status: implemented and covered by the core test suite.

**G1 — Safe opt-in migration**
Bundle and sign the helper, create durable route records, show exact config diffs,
write through the existing backup pipeline, and restore Direct mode in one click.
Measure compatibility and overhead before enabling the feature broadly.

Status: implemented locally as an explicit per-client opt-in. Route records are
secret-free, original definitions and environment values stay in the credential
store, and backup restore is guarded against orphaned routes. Production signing,
notarization, compatibility measurement and broad enablement remain release gates.

**G2 — Live activity**
Session list, tool-call history, latency and error rates, retention controls and
schema-change detection. Keep payload capture off by default.

Status: metadata-only activity, latency/error summaries, safe support export and
tool-contract change detection are implemented. Automatic refresh and retention
controls remain follow-ups; the reader currently shows the newest bounded view
without mutating the append-only journal.

**G3 — Local firewall**
Allow / ask / block by route, tool, client and profile. Approval timeout defaults
to deny. Treat MCP annotations as untrusted hints; deterministic rules and the
actual arguments make decisions. Replay defaults to unavailable for mutating or
unknown tools.

**G4 — Remote transports and teams**
Streamable HTTP, OAuth, shared policies, audit export, device sync and emergency
revocation only after the local stdio design has real usage evidence.

Deferred candidates G3 and beyond are described in
`docs/FUTURE_MCP_ROADMAP.md` with scope and dependencies.

---

## 9. Distribution

**Not the Mac App Store.** The sandbox forbids reading and writing other applications' config files, which is the entire app. Instead:

- Build from source (see *Build and run* above), or download a release build
- Release builds are ad-hoc signed and not yet notarized (there is no Developer ID certificate), so Gatekeeper warns on first launch; verify the SHA-256 published on the website and in the release notes, or build from source
- The in-app updater checks a first-party manifest and verifies the download's SHA-256 before installing it
- No account, no license key, no network call required to launch

---

## 10. Design direction

This is an instrument panel, not a consumer app. Reference: Proxyman, TablePlus. Not Things.

- Dense table, thin hairlines, generous information per screen
- Monospace for paths, commands, numbers
- Status dots, not text labels
- Dark mode is the default
- No illustrations, no onboarding carousels, no shadowed cards
- Color only where it carries meaning: green running, red failed, gray unchecked, amber consuming context, blue waiting for your sign-in. Nowhere else.

A developer wants maximum state on one screen and wants to read it in two seconds.

---

## 11. Open questions

Resolved: v1 client list (§4), platform sequencing (§3.2), web stack (§3.1),
VS Code user-level MCP config (supported), Windows paths (filled into the
client registry), and the tokenizer (bundled — see §7.4 for what changed the
answer).

Permanently open: client config paths and schemas move between client
releases. Verify them against each client's current documentation before
changing the registry, and prefer a per-client path override (§7.6) over
waiting for a release.

---

## 12. Notes for contributors and coding agents

- Verify all external config paths and schemas against current documentation before implementing. This area changes frequently.
- Keep the web layer free of business logic. If the web UI needs a capability, add an IPC command, do not reach around it.
- Every native capability gets a documented IPC command in `docs/ipc.md`.
- Write tests for config round-tripping first, before feature work. That is where the product risk is concentrated.
- Prefer boring, readable implementations over clever ones. This is a tool people trust with their configuration.
