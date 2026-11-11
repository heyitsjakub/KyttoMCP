# Kytto MCP — roadmap

The one place for what comes next: ideas for the next version, deferred
candidates and known gaps. Everything here is a candidate, not a commitment —
good places to contribute. Revalidate protocol, client and related-tool behavior
before implementing any of it. What already shipped is in
[`CHANGELOG.md`](../../CHANGELOG.md), not here.

Section numbers start at 5 and stay stable because other documents cite them;
new sections are appended at the end.

## Next version — candidates

In rough order of value. Each links to its section below.

1. **Context cost: full catalog vs. what a session loads** (§16).
2. **Project configs inside repositories**, read-only, in folders the user
   chooses, scanned by the Tool Safety Scanner (§12).
3. **Alert when a server's launch definition changes outside Kytto** (§7).
4. **`kytto` command-line tool** to switch a profile or a server from a terminal
   or hook ([claude-code#10447](https://github.com/anthropics/claude-code/issues/10447)).
5. **Import from MCP Router**, which reportedly ended support on 2026-09-18
   (claim from a competitor's site; verify first).
6. **Contract Guard: separate compatibility and security labels** (§18).
7. Later: Linux ([#2](https://github.com/heyitsjakub/KyttoMCP/issues/2)),
   Firewall rules (§5), cost limits (§6), Focused Inspector (§10),
   organization-managed inventory (§13).

## 5. Local MCP Firewall

**Problem:** MCP clients commonly expose every configured tool without a useful
per-tool policy layer. Tool annotations are optional and untrusted, while prompt
injection and tool poisoning can turn a broad permission into an unintended
write or data transfer.

**Already shipped:** per-tool switches (1.0.9.1), written to the client's own
config where it supports them and enforced by Gateway mode where it does not.
That is the first step toward this section.

**Candidate scope:** Gateway rules with `allow`, `ask` and `block`, keyed by
route, tool, client and profile. Approval timeouts default to deny. Rules inspect
actual arguments but never persist them by default. Destructive or unknown tools
cannot be replayed automatically.

**Dependencies:** Live Activity must first prove gateway compatibility and
reliable request correlation. Requires a native approval surface that cannot be
bypassed by the web layer.

## 6. Cost controls and rate limits

**Problem:** A model cannot see the user's bill and may repeatedly call a paid
tool inside a loop.

**Candidate scope:** per-server and per-tool daily limits, repeated-call loop
detection, cooldowns, optional user-supplied unit prices and hard failure after a
limit. Enforcement belongs in Gateway below the model.

**Dependencies:** Firewall policy engine and durable metadata-only counters.

## 7. Configuration history and conflict attribution

**Problem:** Client updates and other applications can move, overwrite or
rewrite MCP configuration. A backup alone does not explain what changed.

**Candidate scope:** semantic server diff, timestamp, best-effort process/source
attribution, conflict timeline and restore-one-server. Alert when a server's
launch definition (command, arguments, environment) changes outside Kytto.
Preserve the existing digest guard and never lock a client-owned file.

**Dependencies:** extend the existing watcher and backup store; determine what
attribution macOS and Windows can provide without elevated permissions.

## 8. Remote MCP and OAuth assistant

**Problem:** Streamable HTTP, legacy SSE, OAuth discovery and client support vary.
Users currently add wrapper processes to make remote servers work in stdio-only
clients.

**Already shipped:** Remote Doctor Streamable HTTP checks with native OAuth
sign-in and refresh (1.0.8).

**Candidate scope:** clearly-labelled SSE compatibility, bearer/custom headers,
discovery diagnostics, TLS/server identity checks and an optional local bridge
for clients that only support stdio.

**Dependencies:** revalidate the current MCP authorization specification and
each supported client's transport capabilities. Keep tokens in Keychain or the
Windows Credential Manager.

## 9. Safe Registry Installer

**Problem:** A generic catalog is crowded and does not establish package safety.
Users need to know what will run and what will change across their clients.

**Already shipped:** the package safety check in MCP Doctor (look-alike names,
OSV malware lookup, install scripts, archived and deprecated packages; 1.0.9.1
and 1.0.9.2) — the first piece of this section.

**Candidate scope:** official MCP Registry source, namespace ownership and
version display, optional pinning, command preview, required-secret list,
pre-install health check, contract snapshot and a multi-client config diff before
commit. Never present registry presence as a security guarantee.

**Dependencies:** Contract Guard and MCP Doctor. Recheck registry validation and
package-manager behavior before executing anything.

## 10. Focused Inspector

**Problem:** Users need a quick way to reproduce one broken tool call without
installing a separate developer suite.

**Candidate scope:** schema-generated argument form, manual tool invocation,
result/timing/raw JSON-RPC display, resource/prompt inspection and saved smoke
tests. Keep this diagnostic and small; do not compete with full MCP development
and evaluation products.

**Dependencies:** Gateway request path, explicit warnings for mutating tools and
the future Firewall approval model.

## 11. Custom configuration sources and client families

**Initial beta slice implemented:** Users can attach multiple named JSON, JSONC
or TOML files. Kytto auto-detects a tested top-level server-map adapter, displays
the servers as a separate source and enforces read-only behavior in both native
services and UI. Supporting writes or automatic client-family discovery remains
deferred.

**Observed need:** Some users run several fast-moving MCP clients and forks
beyond Kytto's eight built-in clients. Adding every product name to the fixed
client registry would be brittle, and one compatible configuration format may
cover several related clients.

**Candidate scope:** Let the user register a named configuration source with an
explicit file path, configuration format and scope. Start read-only: discover
and display servers only after the document matches a known adapter. A later
write mode must use the normal preview, digest guard, backup and atomic-write
pipeline. Unknown or ambiguous structures remain read-only. Keep this distinct
from the existing path override, which only relocates a built-in client's known
configuration format.

**Validation before implementation:** Collect redacted path and schema examples,
confirm that each candidate actually supports MCP, and group compatible clients
by tested format rather than by assumed fork ancestry. Do not recursively scan
the user's home directory or repositories for unknown configuration files.

## 12. User, profile and workspace configuration scopes

**Initial beta slice implemented:** Every source now carries a visible `Global`,
`Profile` or `Workspace` identity and optional label. Custom paths must be chosen
explicitly and remain read-only. Claude Code project scopes registered in its
own config are discovered (1.0.4.1). Other built-in project discovery,
precedence and conflict resolution remain deferred.

**Observed need:** A server may be available globally, in one client profile or
only inside a particular workspace. Flattening these sources into one client
column can make Kytto overstate where a server is available. Repository configs
are also an attack path: the strutil-core worm (November 2026, [Socket
analysis](https://socket.dev/blog/shai-hulud-descendant-malicious-ruby-gem))
commits `.cursor/mcp.json`, `.claude/settings.json`, `AGENTS.md`, `CLAUDE.md` and
`.vscode/tasks.json` into repositories, and today Kytto only sees user-level
configs.

**Candidate scope:** Project configs inside repositories (`.mcp.json`,
`.cursor/mcp.json`, `.vscode/mcp.json`) in folders the user selects, read-only,
scanned by the Tool Safety Scanner. Clear `Global`, `Profile` and `Workspace`
badges and filters. Kytto must not crawl arbitrary projects. If the same server
appears at several scopes, show precedence and conflicts instead of silently
merging definitions. A server found only in a repository is never health-checked
automatically, because a check starts it.

**Dependencies:** A source-aware normalized model, deterministic precedence and
deduplication rules, per-source watchers, and backups that always identify the
exact file being changed. Revalidate current VS Code, Cursor and portable
`.mcp.json` behavior before choosing the first supported workspace source.

## 13. Organization-managed and hosted MCP inventory

**Observed need:** Claude Team organization connectors and hosted agents do not
necessarily exist in the local files Kytto reads. Treating them as missing local
clients would give users the wrong explanation of their setup.

**Candidate scope:** Research a separately labelled, read-only inventory for
remote or organization-managed MCP connections, but only where the provider
offers a documented and permissioned API or local source. Show ownership,
availability and authentication state without importing credentials or
claiming that Kytto can edit organization policy.

**Dependencies:** Provider-supported discovery, explicit account consent, secure
token storage and a clear boundary from the Remote MCP transport/OAuth work in
section 8. Never scrape a signed-in web session or infer organization state from
local configuration alone.

## 14. Beta-tester feedback backlog (done)

Nine items from the first beta round were implemented in 1.0.4.1:
net undo/restart state, clearer restart-banner wording, useful health-check
timeout errors, Claude Code project-scope discovery, a local MCP library scan,
a copy-paste agent prompt with strict JSON import, package provenance and
maintenance state, a verified in-app updater, and a read-only skills inventory.
They are listed here only so the section numbers above stay stable.

## 15. Windows port

The Windows implementation now lives in [`../../windows/`](../../windows/) as a
.NET 10, WPF and WebView2 app. Its platform-specific setup is described in
[`../../windows/README.md`](../../windows/README.md). It reimplements the IPC
command set in [`ipc.md`](ipc.md) against Windows APIs; the two web layers have
since diverged where native behavior requires it, while preserving the
platform-neutral contract in README §3.2.

Parity work that any Windows implementation has to cover, beyond the IPC
contract itself:

- **Paths.** Per-platform config paths live in the client registry
  (`%APPDATA%`, `%USERPROFILE%`); the web layer never parses a Windows path.
  Claude Code project scopes are keyed by Windows paths in `.claude.json`.
- **Backups and parked definitions.** Owner-only ACLs as the equivalent of the
  macOS `0600`/`0700` modes, and a restore that writes only to a file the client
  registry says Kytto manages (§6).
- **Secrets.** Windows Credential Manager in place of the Keychain.
- **Watching.** `ReadDirectoryChangesW` in place of FSEvents.
- **Updater.** The same first-party manifest and SHA-256 verification; opening
  the Windows download route is sufficient where self-replacement is not
  implemented.
- **Gateway.** The `kytto-mcp-proxy` helper and its credential lookup need a
  Windows build before Gateway mode can be offered there.

## 16. Context cost: full catalog vs. what a session loads

**Problem:** Kytto shows the estimated token cost of every tool's full
definition, summed per server and per client for the servers that are switched
on. That is the cost if every tool loads. Clients increasingly defer MCP tools
behind tool search instead — Claude Code does by default (see the notes in
`ClientRegistry.swift`) — so the full number can overstate what a session
actually pays, and users cannot tell whether to switch tools off or let search
handle it. Requested by a user on X on 2026-11-09: "show two
numbers: the cost if every tool loads and the cost for what this session
actually loaded". Overlaps with issue
[#3](https://github.com/heyitsjakub/KyttoMCP/issues/3) (label deferred loading
in the context estimate), which is still open.

**Candidate scope:**

- Per client, two numbers side by side: **if every tool loads** (today's
  estimate) and **loaded up front** — for a client that defers tools, only what
  it sends before a search (tool names or short stubs). Label which clients
  defer and which load everything.
- **Actually loaded in a session**, where Kytto can know it without guessing:
  - Gateway mode already sees `tools/list` and every `tools/call` on its routes,
    so Live Activity can count which tools a session used — metadata only, never
    arguments or results.
  - Client session logs (for example Claude Code transcripts) might record which
    tools were pulled in through search. Research only: opt-in, read-only,
    nothing stored, and dropped if the logs do not say it reliably.
- Never present an estimate as a measurement; keep the `~` marker and say where
  each number comes from.

**Dependencies:** verify per client what is loaded before a search and whether
that changes by model; Gateway request correlation from Live Activity; the same
`cl100k_base` tokenizer for both numbers so they are comparable.

## 17. Review follow-ups

Smaller follow-ups from the 2026-10-24 review are tracked outside this
document. The section number is kept so references stay stable.

## 18. Contract Guard: baselines and labels

**Done:** a baseline saved before schemas and annotations were recorded no
longer reports every tool as changed; it adopts the missing fields from the
next check (see `CHANGELOG.md`). Check that the Windows port has the same fix.

**Still open:** every input schema change is reported as Breaking. That label
speaks about compatibility, while the reason to look is often security (a new
optional field is compatible but can still ask an agent for local data).
Consider separate compatibility and security labels.

## 19. Failure handling — what is still thin

The reliability pass (see `CHANGELOG.md`) covered a moved app, lost route
material, a rotated secret, a server that stops mid-session, a restart into a
broken definition and a partly applied profile. Known remainders:

- **Gateway findings need the journal.** A server that fails only inside a
  client that does not go through Gateway mode is still invisible to Kytto.
- **Restart now checks from outside.** Kytto starts the changed servers itself;
  it cannot see whether the client connected to them. Client log parsing would
  be the only way, and is deliberately not done.
- **Undo for a profile is per switch**, not a file restore, so a config edited
  by something else in between is refused by the digest guard like any write.
- **Retry after "changed on disk"** is offered for a switch in the matrix. Other
  writes offer Refresh only.
