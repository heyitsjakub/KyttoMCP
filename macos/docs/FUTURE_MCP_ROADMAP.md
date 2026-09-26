# Kytto MCP — roadmap

Ideas that were deliberately deferred while the app focused on MCP Doctor, Live
Activity, Contract Guard and Context Optimizer. These are candidates, not
commitments — good places to contribute. Revalidate protocol, client and
related-tool behavior before implementing any of them. Section numbers start at
5 and stay stable because other documents cite them.

## 5. Local MCP Firewall

**Problem:** MCP clients commonly expose every configured tool without a useful
per-tool policy layer. Tool annotations are optional and untrusted, while prompt
injection and tool poisoning can turn a broad permission into an unintended
write or data transfer.

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
attribution, conflict timeline and restore-one-server. Preserve the existing
digest guard and never lock a client-owned file.

**Dependencies:** extend the existing watcher and backup store; determine what
attribution macOS and Windows can provide without elevated permissions.

## 8. Remote MCP and OAuth assistant

**Problem:** Streamable HTTP, legacy SSE, OAuth discovery and client support vary.
Users currently add wrapper processes to make remote servers work in stdio-only
clients.

**Candidate scope:** Streamable HTTP, clearly-labelled SSE compatibility,
bearer/custom headers, OAuth login and refresh, discovery diagnostics, TLS/server
identity checks and an optional local bridge for clients that only support
stdio.

**Dependencies:** revalidate the current MCP authorization specification and
each supported client's transport capabilities. Keep tokens in Keychain or the
Windows Credential Manager.

## 9. Safe Registry Installer

**Problem:** A generic catalog is crowded and does not establish package safety.
Users need to know what will run and what will change across their clients.

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

**Observed need:** Some users run several fast-moving MCP
clients and forks beyond Kytto's five built-in clients. Adding every product
name to the fixed client registry would be brittle, and one compatible
configuration format may cover several related clients.

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
explicitly and remain read-only. Built-in project discovery, precedence and
conflict resolution remain deferred.

**Observed need:** A server may be available globally, in one client profile or
only inside a particular workspace. Flattening these sources into one client
column can make Kytto overstate where a server is available. The v1 product
design intentionally supports global/user-level configuration only.

**Candidate scope:** Extend configuration-source identity with client, scope
kind and a user-visible workspace/profile label. Add read-only discovery first,
with clear `Global`, `Profile` and `Workspace` badges and filters. Workspace
paths must be explicitly selected by the user; Kytto must not crawl arbitrary
projects. If the same server appears at several scopes, show precedence and
conflicts instead of silently merging definitions.

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

## Suggested order for a future beta

1. ~~Clarify built-in coverage as five supported local clients in the app UI.~~
2. ~~Prototype explicit read-only workspace-scope discovery.~~
3. ~~Prototype read-only custom sources for known configuration adapters.~~
4. Enable writes only after source-aware preservation and backup tests pass.
5. Keep organization/hosted discovery in research until a supported interface
   and real user demand are both confirmed.

## 14. Beta-tester feedback backlog (done)

Nine items from the first beta round were implemented in 1.0.4.1:
net undo/restart state, clearer restart-banner wording, useful health-check
timeout errors, Claude Code project-scope discovery, a local MCP library scan,
a copy-paste agent prompt with strict JSON import, package provenance and
maintenance state, a verified in-app updater, and a read-only skills inventory.
They are listed here only so the section numbers above stay stable.

## 15. Windows port

A Windows shell (C# or Rust + WebView2, README §8 M7) is planned but not yet in
this repository; contributions are welcome. Its discipline is README §3.2: it reimplements the command set in
[`ipc.md`](ipc.md) against Windows APIs and loads the shared `web/` directory
unchanged. If a feature needs a Windows-specific change in `web/`, that is a
§3.2 violation to find, not a branch to add.

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
