# Kytto MCP — design document

> **Kytto MCP** — MCP control for Mac & Windows.
> Manage every client today; see and control MCP traffic through an optional local gateway next.

This is the design document, and the source of truth for scope and design. It
began as the build plan shared by the macOS app and this Windows port, and is now
the record of what was decided and why. Source comments cite it by section
(`§6.3`, `§7.1`) instead of restating the reasoning, so those references have to
keep resolving: sections are never renumbered, merged or removed. When a decision
here stops being true, change it here first.

For what the app is and how to build it, start with the [repository
README](../README.md).

---

## 1. Positioning

**What this is now:** a control panel over MCP configuration. One place where
you see *which server runs in which client*, and can change it with one click.

**What it grows into:** a local MCP control plane. Gateway mode adds live
sessions, tool-call observability and deterministic policy enforcement without
turning the basic manager into a cloud service.

**What this is NOT:** a catalog of MCP servers. Catalogs are everywhere and free. The value is the cross-client matrix, not the list.

**Target user:** developers running AI coding agents with MCP servers across multiple clients. Power users who have 5+ servers and 3+ clients and have lost track of what is enabled where.

**License and cost:** free and open source under the MIT License. Distributed directly, not through an app store (see §9).

**The first three differentiators:**
1. Cross-client matrix (enable/disable per server per client from one screen)
2. Token weight per server and per tool (show how much context each server's
   tool definitions consume — and, through the gateway, spend less of it)
3. One optional local gateway for visibility and permissions across clients

---

## 2. Non-goals for v1

Do not build these. Each one costs months and none of them is why someone uses the app.

- Authoring / scaffolding new MCP servers
- Mandatory gateway migration in the first stable release
- Capturing tool arguments or results without explicit opt-in
- Team sync, cloud accounts, shared config
- A server marketplace
- Auto-updating installed servers
- Project-scoped configs (see §4 — global scope only in v1)

---

## 3. Architecture

Native shell + web UI.

**Web stack: vanilla HTML/CSS/JS, no framework.** See §3.1 for structure discipline — vanilla is the right call here but needs rules to stay maintainable.

**Platform sequencing: macOS first, Windows after M6.** See §3.2 — this creates a specific risk that must be managed from M0.

```
┌─────────────────────────────────────────┐
│  Native shell                            │
│  macOS: Swift + WKWebView    (v1)        │
│  Windows: C# (WPF) + WebView2   (M7)     │
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

**IPC:** define a single typed message channel between web and native. Every native capability is one named command with a JSON payload. Keep the surface small and document each command in [`docs/ipc.md`](ipc.md) as it is added.

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

Windows landed at M7; its native shell is in `../`. The same boundaries still matter: configuration logic and OS paths stay native, IPC describes intent, and web code does not interpret filesystem paths.

- **The IPC contract is platform-agnostic.** Commands describe intent, not implementation. `secrets.store(ref, value)` — not `keychain.addGenericPassword(...)`. `config.listClients()` — not anything that names a macOS path.
- **The web layer never sees a filesystem path it has to interpret.** It receives display strings and opaque ids. Path construction, separators and env var expansion all live native-side.
- **No platform names in the web layer.** No `if (mac)` branches in UI code. If behaviour must differ, the native side reports a capability flag and the UI branches on the capability, not the OS.
- **Client registry is data, not code.** §4's descriptors carry per-platform path arrays, now consumed by both native apps.
- **Keep the web/native boundary stable.** Each platform has its own web tree; presentation differences must not move configuration logic or filesystem knowledge into JavaScript.

The early WebView2 shell was a portability probe; the maintained Windows application is in `../`.

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

Kytto has a fixed set of built-in clients. They are the only writable client
destinations and remain a fixed native collection: Claude Desktop, Claude Code,
Cursor, VS Code and Codex, and since 1.0.9.1 Antigravity, Devin Desktop and
OpenCode (below). User-attached configuration files never join this
collection or any mutation loop.

| Client | Config location (VERIFY — these move) |
|---|---|
| Claude Desktop | macOS `~/Library/Application Support/Claude/claude_desktop_config.json`<br>Windows `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Code | `~/.claude.json` (global scope)<br>Windows `%USERPROFILE%\.claude.json` |
| Cursor | `~/.cursor/mcp.json`<br>Windows `%USERPROFILE%\.cursor\mcp.json` |
| VS Code | user-level MCP config: macOS `~/Library/Application Support/Code/User/mcp.json`<br>Windows `%APPDATA%\Code\User\mcp.json` |
| Codex | `~/.codex/config.toml` (`[mcp_servers.<name>]`)<br>Windows `%USERPROFILE%\.codex\config.toml` |

### Added in 1.0.9.1

| Client | Config location | Off switch |
|---|---|---|
| Antigravity | `~/.gemini/config/mcp_config.json` (`mcpServers`)<br>Windows `%USERPROFILE%\.gemini\config\mcp_config.json` | `"disabled": true` on the entry |
| Devin Desktop (Windsurf until June 2026) | `~/.config/devin/mcp_config.json` (`mcpServers`)<br>Windows `%APPDATA%\devin\mcp_config.json` | `"disabled": true` on the entry |
| OpenCode | `~/.config/opencode/opencode.json` (`mcp`)<br>Windows `%USERPROFILE%\.config\opencode\opencode.json` | `"enabled": false` on the entry |

These three are JSON, but they do not all spell an entry the way the first
five do, so the per-server shape is data on the descriptor (`ServerSchema`)
next to the path and the off switch. Antigravity writes a remote endpoint as
`serverUrl`, rejects a `type` and accepts only Streamable HTTP; Devin Desktop
states only the legacy transport, as `transport: "sse"`; OpenCode has its own
entry — `type: local|remote`, one argv array in `command`, `environment`. A copy
between two `mcpServers`-shaped clients respells just the keys they disagree on
and keeps `headers`, `cwd` and unknown keys; a copy into or out of OpenCode is
rendered from the model and refused when it would drop headers, a working
directory or OAuth settings. A copy never carries the source client's own
`disabled`/`enabled`/`enabledTools`/`disabledTools` state. Workspace and project
configs of these clients are not read, and Gateway mode does not support
OpenCode yet. The Windows install locations and executable names used to detect
the three apps follow their documentation and were not checked against a real
install.

Single tools can be switched off in a client's own file (§7.11): Claude Code
`permissions.deny` rules (`mcp__<server>__<tool>`), Codex `disabled_tools` /
`enabled_tools`, Antigravity `disabledTools` / `enabledTools` and OpenCode's
top-level `tools` map. The other clients have no such setting, and the
inspector points to Gateway mode for them.

**Codex is the client that proves §4's point.** It is the only one in TOML, so
`format` on the source descriptor is real rather than aspirational, and the
per-server shape does not travel: copying a definition into or out of Codex has
to be *rendered* from the model rather than moved as text. It also has the best
off switch of the original five — `enabled = false` on the server's own table, so a
disabled server keeps its definition and nothing has to be parked.

Two front ends share one config: the ChatGPT app (bundle id `com.openai.codex` —
the display name is not the identifier) and the `codex` CLI. Like Claude Desktop
it has a second, read-only source: the plugins its own config has registered and
enabled, whose packages sit in a cache the client repopulates. Those servers are
shown and never written; which ones count is the *Registered plugins* subsection
below.

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

### Custom configuration sources

Users may explicitly attach multiple named JSON, JSONC or TOML files as
**read-only custom sources**. Kytto reads only the selected file, auto-detects a
supported top-level server map, and appends the source after the built-in
clients in discovery and the matrix. It does not crawl home directories,
repositories or workspaces.

Each source has a durable UUID and an opaque `custom.<lowercase-uuid>` client id,
an absolute native path, and display metadata for `Global`, `Profile` or
`Workspace` scope plus an optional label. Scope is descriptive in this feature:
there is no precedence or silent conflict resolution. Missing and invalid files
remain visible with actionable diagnostics and are never created or rewritten.

The native model owns this safety boundary. Custom ids cannot resolve through
toggle, authoring, profile application, delete, backup/write, secret rotation or
Gateway mutation services even if a caller bypasses the web UI. Detaching a
source removes only Kytto's settings entry; the selected file stays untouched.

### Registered plugins

Claude Code and Codex plugins can declare MCP servers. Kytto shows the ones the
client has itself **registered and switched on**, read-only, with the plugin's name
and version as their source. It never decides that a plugin is active from the
fact that a directory exists: a plugin cache is full of old versions, uninstalled
leftovers and test fixtures, and an earlier build that walked it for `.mcp.json`
files reported all of them as live servers.

**Claude Code.** `~/.claude/plugins/installed_plugins.json` (format version 2) lists
each installation with a scope — `user`, `project` or `local` — and its install
directory. `enabledPlugins` in `~/.claude/settings.json`, then the project's
`.claude/settings.json`, then `.claude/settings.local.json` says whether it is on;
a later file that mentions a plugin overrides an earlier one, and an unmentioned
plugin stays on unless its manifest says `defaultEnabled: false`. The most specific
installation wins; two at the same scope are ambiguous and the plugin is skipped
with a diagnostic, because nothing on disk proves which copy the client loads. A
user-scope plugin belongs to the global Claude Code scope and is also in effect in
every project scope, where that project's settings may switch it off; a project- or
local-scope registration belongs only to its project. Project paths are explicit
registrations — the shared `.claude.json` or a plugin registration names them, and
the disk is never searched for more. One project spelled two ways (`c:/work/app`,
`C:\work\app\`) is one scope, compared case-insensitively and with either
separator; its id is still the one its first spelling in the shared config derives.
A workspace scope exists only if something is configured or enabled in it.

**Codex.** `[plugins."name@marketplace"]` with `enabled = true` in the same
`config.toml` as `[mcp_servers]`, and a package under
`~/.codex/plugins/cache/<marketplace>/<name>/<version>`. The registered version is
used when the table names one that is cached; otherwise the single cached version
is; several with nothing to choose between them is a diagnostic and no servers. A
plugin registered twice in one config is uncertain and skipped. A plugin id is
never allowed to escape the cache (`..`, separators, device names).

**What is read.** Only what the package declares: its root `.mcp.json` and the MCP
components its manifest (`.claude-plugin/plugin.json`, `.codex-plugin/plugin.json`)
names — a `./relative.json` path, an inline map, or a list of either, merged in
order with a later definition replacing an earlier one of the same name. A
declared path must resolve inside the installation as a plain file under 2 MB with
no `..`, drive or stream syntax, reserved device name or reparse point (junction or
symlink) anywhere on the way down; one that does not is reported and skipped. The
installation directory itself must not be a junction. Nested fixtures, other
versions and undeclared files are never opened. Claude Code names these servers
`plugin:<plugin>:<server>`; Codex uses the server's own key. When a plugin and an
ordinary config entry in the same client share a name, the config entry is kept
and a diagnostic says so.

**Launch context is native.** A plugin's command is full of `${CLAUDE_PLUGIN_ROOT}`,
`${CLAUDE_PLUGIN_DATA}`, `${CLAUDE_PROJECT_DIR}` or `${CODEX_PLUGIN_ROOT}`, and
relies on running in the plugin's directory. Discovery attaches that context to the
server — placeholders, launch variables, working directory — and none of it is on
the wire. Placeholders are expanded **once**, over the original text, when a server
is run: a value that came out of a placeholder is a path, never another template,
and an unknown `${…}` is left as written. A relative `cwd` (default `.`) is
resolved only against the plugin root and must stay inside it without crossing a
junction; an absolute drive-letter or UNC `cwd` is the plugin's own choice and is
kept; anything else — an unresolved placeholder, a rooted-but-driveless `\work`, a
drive-relative `C:work` — skips that server with a diagnostic, because resolving it
against Kytto's own drive would be a guess. The data directory is named, never
created. Nothing is spawned to discover any of this: a plugin's server runs only on
an explicit health check, in that directory, with that environment.

**Windows launch.** The context rides on the existing `ManagedProcess` resolution
(PATH + PATHEXT, `.cmd` through `cmd /d /c`) rather than replacing it. A command
written as a path without an extension (`${CLAUDE_PLUGIN_ROOT}/bin/tool`) is
resolved through PATHEXT like a bare command is, and a relative one resolves against
the established directory. A batch file receives one raw command line,
`cmd /d /s /c ""shim.cmd" arg …"`, so cmd strips only the outer quotes and a shim
under `C:\Program Files\nodejs` keeps working when an argument needs quotes; each
argument is quoted when it holds a space or a cmd metacharacter, which is literal
inside quotes. A double quote inside an argument is written `""`, which cmd reads as
a quote that stays inside the pair and a program parsing its command line the usual
way reads as one literal quote, so a JSON argument arrives intact. A line break, a
backslash directly before a quote, or a `%NAME%` naming a variable cmd would
substitute (one in the environment the server is started with, or one of cmd's own
such as `%CD%`) cannot be made literal for every batch file, so an argument
containing one fails the check without starting the server instead of starting it
with a different one. A percent sign that cannot expand (`100%`, `?q=%7B`) is left
alone by cmd and passed as written.

**Read-only, natively.** A plugin's server is not editable, deletable, unifiable,
copyable into another client or toggleable, and the services refuse it themselves
(`ToggleService`, `ServerAuthoring`) rather than leaving it to a disabled button.
The refusal is per client, not per row: a name that is a plugin's in Codex and an
ordinary entry in Cursor stays controllable in Cursor, and an edit or delete there
never writes a definition into Codex that would shadow the plugin. A Claude
Desktop extension is a different thing — still installed software that is not
copied, but still switched by its own client through its own flag — and is not
caught by the plugin rule.

**What Kytto does not infer** (each of these would be a guess about what the client
will load; they are documented limits, not bugs to patch around):

- plugins enabled for one session only, synced from an account, or switched on by a
  managed (enterprise) settings file;
- a Claude Code configuration directory relocated with `CLAUDE_CONFIG_DIR`;
- Codex plugins in an account-managed cache with no explicit `[plugins."…"]`
  registration, and registrations spelled as dotted keys or inline tables rather
  than as a table of their own;
- plugin components other than MCP servers (skills, agents, hooks, commands), and
  MCP components declared as anything but a contained `.json` file or an inline map;
- a project registered with a path that is not a plain drive-letter or UNC path
  (`/work/app`, `C:work`, `\\?\…`).

### Scope decision (v1)

**Built-in writable clients remain global/user-level only.** Automatically
discovering or managing project-scoped configs (`.cursor/mcp.json`,
`.vscode/mcp.json`, project `.mcp.json`) is deferred. An explicitly attached
read-only source may be labelled Profile or Workspace, but Kytto neither scans
for it nor writes it.

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

1. **Back up before every write.** Timestamped copy in app support dir. Keep last 20 per client. **A backup is a copy of the whole config, `env` blocks and API keys included, so it is readable by this user and nobody else** — folders created private, files private from the moment they exist rather than tightened afterwards, and anything an earlier version left looser brought up to date at launch. The same goes for every store holding a definition verbatim, such as parked servers.
2. **Write atomically.** Temp file in the same directory, `fsync`, then `rename`. Never truncate-and-write in place. The temp file carries the replaced file's permissions before it carries its bytes, so a config the user locked down does not come back readable by others.
3. **Preserve everything you did not touch.** Client config files contain unrelated keys. Parse, modify the servers key only, re-serialize preserving key order and formatting where possible. Never rewrite the file from your own model wholesale.
4. **Watch for external changes.** FSEvents on macOS, ReadDirectoryChangesW on Windows. If the file changed on disk since last read, do NOT merge blindly — prompt the user.
5. **Expose backups in the UI.** A visible "Revert" action, not a hidden folder. This is a trust feature, make it visible.
6. **Never write on app launch.** Read-only until the user makes an explicit change. Bringing existing storage up to today's permissions is the one thing launch may do, and it creates nothing.
7. **Custom sources are never write targets.** Their native ids are excluded from
   every authoring and mutation service; a disabled web control is only the
   visible expression of that boundary.
8. **A restore writes only a file Kytto manages for that client.** The set comes
   from the client registry and the path resolver — the same data the write
   pipeline uses — and the write goes to *that* path, never to the location
   recorded beside the backup. Path comparison is lexical on both platforms, so
   that record is not a trustworthy destination. A backup that no longer matches
   is refused, with nothing written.

**Where Kytto keeps its own data on Windows.** Everything lives under
`%APPDATA%\Kytto`: `Backups\<client>\`, `parked.json`, `settings.json`,
`metadata.json`, `profiles.json`, `pending-contract-imports.json` (§7.13),
`gateway-routes.json`, the gateway event log
and the update staging folder. Windows has no mode bits, so rule 1's "readable by
this user and nobody else" is a **protected DACL**: inheritance switched off, and
full control for the current user, `SYSTEM` and `Administrators` only. Folders are
created with that descriptor, so a backup inherits it at creation rather than
being tightened afterwards; the launch-time repair in rule 6 changes only items
that grant access to someone else and does not follow junctions or symbolic links.
It is a local guarantee: on a roaming profile or a redirected `%APPDATA%` on a
network share, the share's own permissions decide, and Kytto does not detect
either case.

### Secrets

API keys currently sit in plaintext in config files and get committed to git by accident. Fix this — it is one of the clearest reasons a tool like this should exist.

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

Columns: server name + command, token weight, then the built-in clients and
any attached custom sources. Custom-source columns show their scope and have
disabled switches explaining that Kytto never changes the file. Normal client
icons and fallback initial tiles occupy the same 22px × 22px footprint in the
sidebar and matrix header; the larger detail icon is unchanged.

Row states:
- green dot — health check passed
- red dot — health check failed, show error snippet in the subtitle
- gray dot — not checked yet
- warning-tinted row — token weight above threshold (default 20k)

Header shows total configured / active count and summed token weight of enabled servers.

Footer bar shows pending-restart warning: *"Restart Claude Desktop to apply 2 changes"* — clients hold config in memory and only pick up changes on restart. Do not try to work around this, just state it clearly. Also show last backup timestamp.

The first writable cell action opens an accessible explanation naming the exact
server, client, action, display path, backup behavior and real switch-off
semantics. Only its explicit primary action may persist acceptance and continue
through the ordinary safe toggle path. Every successful cell change exposes
Undo: enabled/disabled states reverse through that same path, while a previously
absent entry restores the exact backup or uses safe remove-from-client when the
write created a new file. The restart banner remains a non-overlay flex row and
must never cover the matrix scroller, including at short window heights.

#### 7.1.1 Scope, source and density

Three controls sit between the heading and the matrix, and they compose with the
text filter (name, command, and the tool names of a checked server):

- **Scope** — *All sources*, *Global configurations*, or one workspace, profile
  or custom source by name. There is no project crawler: the choices are exactly
  the read-only scopes discovery already reports (Claude Code's existing
  `projects` map, files attached in Settings).
- **Source** — *All source types*, *Configurations*, *Plugins & extensions*,
  *Custom & workspace sources*.
- **Rows** — *Comfortable* or *Compact* (§10.1).

Scope decides the columns first, and a server is in scope when any of them holds
it. An explicit workspace shows its own file **and the writable client that owns
it**, because the project's file is only half of what that client reads: the rest
is the applicable global context, and a server inherited from it is a row here
too. A custom source with no owning client shows only itself. Choosing a
workspace is a stronger statement than folding "Custom sources" away in the
sidebar, so a folded source still shows when it is the chosen scope; folding with
*All sources* selected drops the rows that exist only because of those sources.

Source type asks what a row is. *Configurations* are definitions Kytto manages;
*Plugins & extensions* are installed software; *Custom & workspace sources* are
rows held by at least one read-only file in scope. A server held **only** by
read-only files is never called a configuration, whatever label its first copy
carried. These filters narrow what is drawn and never what is written.

When a refresh stops reporting the chosen scope, the scope returns to *All
sources*; when a filter hides the server the inspector is showing, the selection
is cleared (§7.1.2).

Every row says, in words, how its last check went and where it came from —
*Check passed · Configuration*, *Sign-in required*, *Read-only · Plugin · Demo
tools 2.0* — so neither is carried by a dot or a colour alone. **Check visible**
runs the health checks of the rows the filters leave, one after another, through
the same `health.check` a single *Check now* uses; the list is fixed when the run
starts. *MCP Doctor → Check all servers* stays the all-servers run.

The sidebar groups its destinations — *Overview* (Matrix, MCP Doctor, Activity),
*Manage* (Profiles, Secrets, Backups), *Discover* (MCP Library, Skills) — and
lists *Clients & sources*: the built-in clients, with read-only workspaces nested
under the client that owns them and a padlock on every read-only entry, then the
foldable *Custom sources · read-only*. On Windows MCP Library and Skills remain
sheets over the matrix rather than panels; they are actions, never "selected".

#### 7.1.2 Matrix with detail

The matrix is a card beside a persistent **inspector** (310–360 px, a 14 px gap).
`selectedServerId` is its own piece of state, not a sheet:

- a row click on the matrix selects the server; nothing modal opens. On the client
  screen, which has no inspector, the same click opens the detail sheet;
- **Expand** opens the full §7.5 sheet over the matrix; **Close** ends the
  selection;
- a refresh keeps the selection, the focus and every scroll position — the
  inspector's body is keyed by server, so scrolling one server's tools does not
  leave the next half-way down;
- a server that disappears, or that the filters now hide, clears the selection
  rather than leaving the inspector describing a row nobody can see.

The sheet and the inspector are the same renderer (`serverDetailParts`); a section
added to one is in the other, and the disclosure state of a tool row is shared
between them. Focus is restored within the half of the page it left, because the
two surfaces draw the same controls and the one behind an open sheet is inert.

Below 1280 px the inspector moves under the matrix (at most 420 px tall, its body
scrolling), and with nothing selected it is not shown at all: beside the table a
fixed inspector column left the matrix only two or three client columns wide. In both layouts nothing widens the page: the matrix scrolls itself in
both directions with its server column pinned, the inspector scrolls its own
body, and what a config file or a server wrote — a name, a path, a schema —
wraps or is ellipsised inside its column. The workspace takes the height the
banners and bars leave, never less than its heading, filters and a few rows; a
window shorter than that scrolls the content pane instead of painting over the
bars below it.

An installed **plugin** is read-only everywhere: its switches are disabled in
every client column with a reason, its row and inspector say *Read-only · Plugin*
with its registered version, and it offers no edit, delete or copy. A Claude
Desktop extension keeps its switch in the client that owns it (§4).

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

#### Remote servers: Streamable HTTP and Kytto's own sign-in

A server that is reached over Streamable HTTP is checked the same way over POSTs
(`initialize`, `notifications/initialized`, paginated `tools/list`) and **never**
with `tools/call`. Legacy SSE is not inspected and says so. Remote servers are
untrusted input, so the check is bounded rather than trusted:

- **Where it may go.** HTTPS, or HTTP to the loopback interface for a server the user
  runs. No credentials or fragment in the URL; no redirect is followed, no cookie is
  kept, no Windows credential is sent on its own, and TLS validation is the
  platform's, never loosened. The definition's own headers are kept; environment and
  bearer forms are resolved natively and placeholders expanded once (a secret that
  contains `${…}` stays literal); CR/LF and transport headers are refused; Kytto's
  own OAuth token replaces `Authorization` and nothing else.
- **How much.** 4 MiB per response, 8 MiB in all, 50 pages, 10,000 tools, JSON nested
  at most 128 deep with no duplicate keys, and one deadline (10 seconds) over the
  network, the parsing and the token counting — a hostile description cannot outlast
  it. A repeated cursor, an unmatched JSON-RPC id or an oversized body fails the check
  instead of reporting a partial list. An event-stream answer ends the wait at the
  matching response, not at EOF; a bounded best-effort `DELETE` (0.3 s, inside the
  remaining deadline) ends the session.
- **What is kept.** A `401`, or a `403` with an authentication challenge, is
  `needsAuthorization`; a plain `403` is a failure. No body, challenge, JSON-RPC
  error text or URL query is stored or shown: they can echo a credential.
- **Staleness.** A result is recorded only if the server's definition (not just its
  name) and its authorization generation are what they were when the check started.

**Sign-in is explicit and native.** `health.signInRemote` validates the resource's
OAuth metadata (same-origin hint, exact resource, first advertised issuer, `S256`
PKCE), shows the provider and scopes for confirmation, registers a public client or
asks for a public client ID, opens the system browser and receives the callback on a
listener bound to `127.0.0.1` for the length of one sign-in — no installer, no
protocol registration, no URL reservation. The callback is accepted once, for the
exact route, host and `state`, and the transaction is spent before the token request.
The result lives in the credential store under its own prefix, one record per
canonical resource (a SHA-256 names it), split into small checksummed pieces so
Credential Manager's item limit never truncates it; there is no plaintext fallback.
Refresh happens only when a check is asked for, rotation is kept, and Forget raises
the authorization generation *before* it deletes, so an in-flight refresh cannot put
tokens back and an older refresh cannot undo a newer sign-in. Kytto's sign-in never
changes a client configuration and never authenticates any other client.

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

Client paths (with override), backup retention, token warning threshold, theme, launch at login and menu bar item on/off. Kytto sends no telemetry. A **Support** section holds the two report links (§7.14).

Under Clients, Settings also explains the built-in writable clients and
manages read-only custom sources. A native picker accepts `.json`, `.jsonc` and
`.toml`; the editable name, scope and optional Profile/Workspace label are
validated before the normal atomic settings save. Opening the window refreshes
its editable model so stale onboarding state cannot be written back.

### 7.7 Menu bar item

Native. Lists servers with quick enable/disable for the most-used client. Shows aggregate health at a glance. This is a retention feature — it keeps the app present without the window open.

### 7.8 Client screen

Reached by clicking a client in the sidebar. The matrix answers *which server is where* and pays for it in width — a client is a 22px icon and a column of switches. This screen answers the questions about one client that the matrix structurally cannot:

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

#### Profiles header actions

The Profiles screen's header holds the heading copy and **one** group of actions —
Export setup, Import setup and New profile (§7.13) — and that group is a direct
child of the header, never a separate row below it. A row of its own is what left
the buttons against the panel's left edge, detached from everything above it.

The rules, which `portable-setup.test.js` pins:

- The header and the empty state use the same content gutter, so the heading, the
  empty-state text and "Create a profile" share a left edge whether or not the
  portable-setup capability is present. Without it the group holds New profile
  alone; there is no empty container left to take room.
- The three buttons share one height (32 px) and one centre line, and the group is
  centred against the heading copy when they sit on one row.
- The group wraps **as a whole** when the window narrows — moving below the text
  inside the same gutter — rather than letting a button be cut off or the page
  scroll sideways. Light and dark, wide and narrow, and the empty panel are all
  checked.

### 7.10 MCP Doctor and Contract Guard

MCP Doctor turns config and health evidence into actionable findings. Automatic
repairs are deliberately narrow: v1 may pin only an absolute executable path
already verified by a successful health check, after an exact preview and through
the ordinary backup/atomic-write transaction. Stderr-based guesses remain advice.

A package a runner fetches by name without an exact version — `npx -y pkg`,
`pkg@latest`, `^1.2`, `uvx pkg`, `pipx run --spec "pkg>=1"` — is an
`unpinned-package` **warning**: each client start can run a release nobody
chose, with the server's environment. It is read from the configuration alone
(`PackageLaunch`, the same parser provenance takes its package name from) and
covers `npx`, `npm exec`, `pnpm dlx`, `yarn dlx`, `bunx`/`bun x`, `uvx`/`uv tool
run` and `pipx run`, however Windows spells them: `npx.cmd`, `uvx.exe`, a full
path, or wrapped as `cmd /c npx …` as separate arguments. Local paths and scripts,
tarballs, git and URL specs, npm aliases, GitHub shorthand, several `--package`
flags, a command line quoted into one `cmd /c` argument, Docker images and plain
executables are not flagged. The repair splices the one argument to an exact
release in every editable copy, a switched-off copy Kytto is holding included,
and only to the version the user's own "Check latest release" lookup returned —
never to `serverInfo.version`, which is self-reported and often not the
package's release number. Lookups are remembered across launches on Windows, so
"the version you just checked" is bounded in time instead: one older than 24
hours is looked up again before it can be pinned. Where a version cannot be added
without a new argument (`pipx run pkg`), the finding is advice only. Pinning
fixes the package's own version; its dependencies still resolve within their
ranges.

Contract Guard compares each successful `tools/list` result against a baseline:
the contract the user last acknowledged, or the first successful result when
nothing has been acknowledged yet. Only those two events move the baseline; a
check never does, so an unreviewed change keeps being reported — and further
changes add to it — until it is acknowledged or the server reverts it. A failed
check does not touch the contract. Surface added and removed tools plus changes
to descriptions, input schemas and safety annotations. Input schemas are compared
in a canonical form (object keys sorted, insignificant whitespace dropped, array
order kept), so a server that only re-serializes a schema is not reported. The
compared contract is model-facing behavior and contains no tool-call arguments or
results.

A change also carries optional field-level `details` — path, before, after,
explanation — so a review can read *what* changed. Paths are escaped JSON Pointers;
annotations are reported hint by hint and said to be hints, not enforcement; a
`dry_run`, `dryrun`, `confirm` or `approval` field, or a confirmation entry in
`required`, is explained as a possible change to a safeguard whose actual enforcement
the reader has to verify. Everything is bounded because a server wrote it: 40 fields
plus an omission notice, 500 differing nodes, depth 12, excerpts of 800 Unicode
scalars, and no parse of a schema over 1,000,000 UTF-8 bytes (it is compared as raw
text and shown as one excerpt). Whether a contract changed is decided on the full
values and never on a truncated excerpt. Records written before details existed still
load, with none.

The scanner and tool-name collisions read the tools from the last passing check,
not the last check: a failed check lists none, and failing must not be a way to
make a finding go away. Tools are read the way clients read them — last-wins on
a repeated key — and a schema is scanned decoded, so an escape cannot hide a
phrase or an invisible character.

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

### 7.13 Portable setup

A setup moves between machines — and between the macOS and Windows apps, which read
and write the same file — as versioned JSON: `kytto-portable-setup`, version 1,
carrying `servers`, `profiles`, `requirements` and, optionally, `reviewedContracts`.
It is a **whitelist**, not a copy of anyone's config. Only writable, built-in client
definitions and profiles are exported; custom and workspace sources, installed
plugins and extension bundles never are, and the credential store is never read.
Exact launcher names, known flags and a verified registry package identifier may stay
literal; every other argument, every environment and header value, every remote URL
and every private path becomes a **requirement** of kind `secret`, `path` or `value`
that the destination supplies natively.

*Reading* a file is as strict as writing one is careful: a size cap, strict JSON
(no comments or trailing commas), valid UTF-8, no key twice, no key outside the
known set, every value of exactly the declared type, and every limit the macOS app
applies. A literal outside the whitelist is refused, so an unknown command or
argument cannot ride in as "public" text. A byte-order mark is skipped.
An export refuses to land on a live client config or one of Kytto's own stores —
by what each path finally names, with symbolic links, junctions, mount points, short
names and letter case followed through. A share of this very computer
(`\\localhost\C$\…`, its own name or address) is refused outright: the file-sharing
stack names the file by the share, never by its drive path, so the comparison above
cannot see through it.

**Import is a preview and an apply.** The preview reads everything, collects the
inputs in a native window (masked for secrets, URLs and plain values; file and
folder browsing for paths), and keeps a plan natively under an opaque id: single
use, ten minutes, at most four at a time, discarded by Cancel, Escape and the
backdrop, and invalidated when the client paths or sources change. Neither the file's
path nor anything typed crosses to the page. The apply checks the digest of the setup
file, of every config it would write or read, and of Kytto's own stores again; refuses
a name or profile that now exists — a name an installed plugin or extension already
provides to that client included, since a config entry beside it would shadow it; and
writes Kytto's stores first and the configs
last, as one §6 transaction with a backup of each. If the config transaction
refuses, the stores are put back byte for byte, and anything that could not be put
back is named in the error rather than hidden. **An imported server is always
switched off** — `enabled = false` for Codex, a deny-list entry for Claude Code, a
held definition for the presence-only clients — and no command has a way to ask
otherwise. Existing parked copies, comments, unknown keys and everything else in the
user's files survive because nothing is regenerated. No server is launched.

**Reviewed contracts** are optional and exported only on request. Each tool becomes a
SHA-256 digest of a canonical envelope — sorted keys, the schema as its canonical
text rather than a nested object, token measurements excluded, nothing escaped that
does not need to be — written by hand so that no serializer default can change it;
the digest vectors the macOS app produced are test fixtures. On import a reference is
**pending evidence, never a baseline**: it gets this machine's own import time, shows
`matching: null` until a check that passed *after* that time exists, and is then only
compared. Acknowledging a Contract Guard alert reads both stores and requires that
fresh check *before* either is written; an unreadable pending store is kept as it is,
blocks the review, and never leaves a local alert half acknowledged. Imported hashes
are never copied into trust.

### 7.14 Bug reporting and interface recovery

(Numbered to match the macOS plan's §7.14; §7.13 is the portable setup.)

**Two fixed links.** *Report a bug…* opens the private form at
`https://kytto.jakubhecht.sk/#report-bug` — no account — and *Report on GitHub…*
opens `https://github.com/heyitsjakub/KyttoMCP/issues/new/choose`, which is
public and needs a GitHub account. Both open the **system browser**, never the
app's own web view with its privileged bridge, and the app attaches and submits
nothing: no error text, path, log, config, version or machine detail goes into
either address, and the reporter writes the report themselves. The UI says which
link is private and which is public, and that nothing is attached automatically.
They live in `SupportLinks.cs` and `web/js/support.js`, and a test pins that the
two spell the same addresses.

**Where they appear.** Settings → **Support**; the tray's **Help** submenu
(Windows has no menu bar, so this is where the macOS Help menu's entries go); the
private link on every error notice; the startup failure screen, beside *Try
again*; and next to a settings save that failed (never next to input the user
merely got wrong). An unhandled script error or promise rejection shows the same
screen with a generic message — the exception is never rendered or sent.

**One navigation policy.** `ExternalNavigationPolicy` is the single decision about
where the web view may go: the bundled origin, and exactly those two addresses.
It is a literal allow-list, not "any http(s) URL" — the same host with a query or
another fragment is refused — and a browser is only started for a user's click,
never for a script's `window.open`. Everything else is cancelled. No IPC command
is involved; the web layer only renders ordinary links.

**Native recovery, independent of the page.** If the bundled page fails to load
(a navigation error or an HTTP error status) or WebView2 reports a failed
process, the window hides the web view — an `HwndHost` paints over anything WPF
draws — and shows a native panel with *Reload interface*, *Report a bug…* and
*Report on GitHub…*. It needs neither the page nor IPC. `InterfaceRecovery` is the
state machine behind it, with the window as a testable seam:

- **One panel.** Repeated callbacks refresh the one panel and never add another.
  A worse failure escalates the action instead.
- **No false alarms.** A cancelled navigation — the shell refusing an external
  link, a reload replacing a pending load — never covers a working app, nor does
  a helper process (GPU, utility, sandbox helper, plug-in, a frame's renderer)
  that WebView2 restarts by itself.
- **By kind.** A lost renderer, or one that stopped responding, is recovered by
  navigating the existing control to the bundled app origin. A lost browser
  process — after which the control's own `CoreWebView2` throws — means a new
  control, with its message handlers, navigation policy, bridge script and
  router subscription rebuilt. A reload that does not bring the page back
  escalates to a rebuild.
- **No replay.** Recovery only ever navigates to the bundled origin and holds no
  way to send a command. Every request carries its document's random tag and the
  bridge drops a reply that echoes another one, so a slow command finishing after
  a reload cannot answer the new page's request with the same number.

The caption buttons, the page-declared drag region, the tray, DPI and theme keep
working while the panel shows; the panel carries its own drag strip and follows
the window's light/dark decision. The window renders its own WPF content in
software: on the machine this was checked on, hardware-accelerated WPF painted
nothing in a window with extended glass and a system backdrop (nothing in that
window had ever been WPF-drawn before — the page covers it and the caption
buttons are a Popup), while the same tree in software showed at once. The page
is a separate composition and is unaffected. There is no telemetry SDK, no automatic crash
report and no detection of a previous crash of the whole app: when the process
dies the operating system handles it.

---

## 8. Milestones

Build in this order. Each milestone should end with something runnable.

**M0 — Shell**
macOS: Swift + WKWebView loading local web UI, typed IPC channel, native menu bar. No MCP logic.
Also: one throwaway WebView2 window on Windows loading the same UI, then park it (§3.2). Do not build Windows features — just prove the web layer renders and the IPC channel design is portable.

**M1 — Read-only discovery**
Client registry, detect installed clients, parse all configs, normalize into internal model, render the matrix read-only. No writes at all. Ship this to yourself and check it against your real machine.

**M2 — Safe writes**
Backup, atomic write, format preservation, file watching, external-change prompt, revert UI. Toggles in the matrix become live. **Spend real time here.** Write tests that assert unrelated keys survive a round trip.

**M3 — Add / edit / delete server**
Manual path first, then the static catalog. Restart-required footer bar.

**M4 — Secrets**
Keychain / Credential Manager integration, env injection at spawn, masked UI.

**M5 — Health check + token weight**
Process spawn with timeout, MCP handshake, `tools/list`, error capture, token estimation, matrix status states.

**M6 — Polish + ship (macOS v1.0)**
Menu bar item, settings, empty states, error states, onboarding on first run (detect clients, show what was found), notarization, Sparkle updates, landing page.

**M7 — Windows**
Native shell in C# or Rust + WebView2. Implement the same IPC command set against Windows APIs: Credential Manager for secrets, `ReadDirectoryChangesW` for watching, `%APPDATA%` paths in the client registry, native tray item. The web layer should need zero changes — if it needs more than trivial ones, §3.2 was violated somewhere and that is worth finding rather than patching.

Realistic solo target: 6–8 weeks to M6 at a steady pace. M2 is the milestone that will take longer than you expect. Do not let it slip to the end. M7 should be 2–3 weeks if §3.2 was respected, and considerably worse if it was not.

**Ship M6 before starting M7.** Real macOS users will change your priorities for Windows, and shipping to nobody for three extra weeks is the classic solo-dev failure mode.

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

Beyond G4, the candidates considered so far are cost controls and rate limits
enforced in the gateway, configuration history with conflict attribution, a
remote MCP and OAuth assistant, a registry-backed installer with a pre-install
contract snapshot, and a small focused inspector for reproducing one tool call.
They are ideas, not commitments: each depends on G2/G3 proving out first, and
protocol, client and registry behaviour must be re-verified before any of them
is built.

---

## 9. Distribution

**Not the Mac App Store, and not the Microsoft Store.** The store sandbox forbids reading and writing other applications' config files, which is the entire product. Direct distribution:

- Free, open source (MIT), signed direct downloads — GitHub releases and the product page
- Developer ID signed + notarized (macOS), Authenticode-signed (Windows); an unsigned build is labelled as such
- Sparkle for macOS updates; on Windows, a built-in checker against a first-party manifest that verifies the package hash and signature and installs only on an explicit user action
- No payments, license keys or activation — nothing requires a network call to launch

Building and publishing a Windows release is described in [`release.md`](release.md).

---

## 10. Design direction

This is an instrument panel, not a consumer app. Reference: Proxyman, TablePlus. Not Things.

- Dense table, thin hairlines, generous information per screen
- Monospace for paths, commands, numbers
- Status dots, not text labels
- Dark mode is the default
- No illustrations, no onboarding carousels, no shadowed cards
- Color only where it carries meaning: green running, red failed, gray unchecked, amber consuming context. Nowhere else.

A developer wants maximum state on one screen and wants to read it in two seconds.

### 10.1 Controls, density and motion

**Filters are native selects with our own closed appearance.** The `<select>`
stays — its menu, keyboard handling and typeahead are the platform's, and the page
reacts to the committed `change`, never to keys inside the menu. Only the closed
control is drawn: `appearance: none` (and `-webkit-appearance: none`), 36 px high,
an 11 px radius, a leading SVG icon, a single subtle chevron on the right, padding
`0 34px 0 35px`, a soft surface gradient and shadow, a 2 px accent focus ring, and
an accent border and tint while the filter is narrowing something. Both SVGs are
decoration — `aria-hidden` and `pointer-events: none`, so the whole control is the
select — and the label wraps the select so the two are associated. No custom
combobox, no UI dependency.

**Density.** *Comfortable* rows are three lines (name, verdict and source,
command) at 68 px; *Compact* rows are 52 px and drop the command line but keep the
name, the verdict and the source, which are what a row is for. A failed check, a
wait for sign-in and the tool names that matched the filter still replace the
command, because there they are the reason. Density is per session.

**Never colour alone.** Status is a dot *and* words. A selected row is a tint, a
bar and an underline, and `aria-pressed` for assistive technology. Read-only is a
padlock and the word. An active filter is an accent and a value that says what it
is. Colour keeps the meaning §10 gives it and is never the only carrier.

**Motion confirms; it does not decorate.** Two tokens — fast 110 ms, medium
200 ms — cover transitions. The inspector arrives in 180 ms, rows entering the
visible set in 180 ms, a cell that landed on disk in 150 ms, panels and sheets in
200 ms with a few pixels of travel and no overshoot. Typing in a filter animates
only the rows that appear. `prefers-reduced-motion: reduce` collapses every
animation and transition to a single frame, and the screen then says the same
thing immediately; WebView2 reports Windows' *Show animations* setting through
that media query, so the page needs no platform branch (§3.2).

---

## 11. Open questions

Resolved: v1 client list (§4), platform sequencing (§3.2), web stack (§3.1).

1. Exact current config path and schema for each v1 client — **ongoing.** The
   paths shipped in the client registry, but clients move them between releases:
   re-verify against live docs whenever a client updates, and do not trust
   training data
2. ~~Whether VS Code user-level MCP config is stable enough to support in v1 or
   should move to v1.1~~ — resolved: VS Code shipped as a v1 client (§4)
3. ~~Windows paths for all four clients~~ — resolved: the registry carries
   Windows paths for all built-in clients, and the Windows port reads them
4. ~~Whether to bundle a real tokenizer in v1.1 or keep the character
   estimate~~ — resolved: bundled, see §7.4 for what changed the answer

---

## 12. Notes for contributors and coding agents

- Verify all external config paths and schemas against current documentation before implementing. This area changes frequently.
- Keep the web layer free of business logic. If the web UI needs a capability, add an IPC command, do not reach around it.
- Every native capability gets a documented IPC command in [`docs/ipc.md`](ipc.md).
- Write tests for config round-tripping first, before feature work. That is where the product risk is concentrated.
- Prefer boring, readable implementations over clever ones. This is a tool people trust with their configuration.
