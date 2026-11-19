# KyttoMCP — roadmap

The one place for what comes next: ideas for the next version, deferred
candidates and known gaps. Everything here is a candidate, not a commitment —
good places to contribute. Revalidate protocol, client and related-tool behavior
before implementing any of it. What already shipped is in
[`CHANGELOG.md`](../../CHANGELOG.md), not here.

Section numbers start at 5 and stay stable because other documents cite them;
new sections are appended at the end.

## Next version — candidates

Proposed focus for macOS and Windows: **see which MCP definition a client
uses, then deliberately copy that definition across clients.** The
[19 November feedback review](#22-feature-proposals-from-community-feedback--2026-11-19)
records the source and first usable scope for each candidate.

1. **Show source, scope and conflicts in the matrix** (§12, §22). Show which
   definition takes precedence where client behavior has been verified.
2. **Import a selected project's MCP definition into a supported client**
   (§11, §12, §22). Extend the existing copy preview with source selection
   and a setup check before writing.
3. **Flag launch configuration changes made outside Kytto** (§7, §22). Start
   with a local, masked diff in the matrix or server detail.

The macOS and Windows backends and the shared web UI implement these three items for 1.0.9.6. Launch comparisons begin with the first read in each session and pending reviews survive restarts.

Other candidates:

- **Tool risk evidence** (§22). Explore a local advisory view and follow the
  mcpward draft before deciding on interoperability.
- **Context cost, the rest** (§16): verify behavior for additional clients
  and investigate session measurements with reliable client evidence.
- **`kytto` command-line tool** to switch a profile or a server from a terminal
   or hook ([claude-code#10447](https://github.com/anthropics/claude-code/issues/10447)).
- **Import from MCP Router**, which reportedly ended support on 2026-09-18
   (claim from a competitor's site; verify first).
- Later: Linux ([#2](https://github.com/heyitsjakub/KyttoMCP/issues/2)),
   Firewall rules (§5), cost limits (§6), Focused Inspector (§10),
   organization-managed inventory (§13).

The [1.0.9.5 release notes](../../CHANGELOG.md#1095--2026-11-14) record project
folder discovery, the two Contract Guard labels, the second context estimate,
and the November audit and demo fixes for both platforms. The shared web UI is
already listed under Unreleased.

### Bugs and reliability

Fixing bugs and reliability problems is also a priority for the next version.
Review reports and known gaps (§19, §20), verify which still reproduce in the
current macOS and Windows builds, and record confirmed issues here. Prioritize
crashes, configuration or data loss, and failures in everyday workflows such
as discovery, health checks, and saving or undoing changes. Verify each fix on
the affected platform before release.

The first stability pass covers repeated entrance animations, text fields
being replaced during typing, keyboard focus after closing a dialog, and
excessive nesting during TOML discovery. The fixes are under Unreleased in
the changelog. Repeat the typing, dialog and discovery checks in the native
Windows build before release.

The macOS Add Server UI test also logged main-thread and thread-priority
warnings during launch and text entry. Profile these paths in a normal app
session to establish whether they cause a visible pause.

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

**Project folders, first slice (1.0.9.5, macOS):** Settings → Project folders
reads `.mcp.json`, `.cursor/mcp.json` and `.vscode/mcp.json` at the top of each
chosen folder, read-only, labelled `Workspace`, with a stable id per folder and
file (`ClientRegistry.projectDescriptors`). A server found only in such files
is left out of Check all. Conflicts with other scopes show through the existing
drift report. Not done: precedence as each client applies it, and a file
committed later appears when Kytto next re-reads (window activation), because
the folder itself is not watched. The scanner needs tool definitions, so a
repository-only server is scanned only after the user checks it.

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

**First slice (1.0.9.5, macOS):** each built-in client carries a `toolLoading`
mode. Claude Code and Codex are `deferred` (documented, notes in
`ClientRegistry.swift`); every other client is `unknown` until verified, and
its page says so. For a deferring client the page shows a second figure, the
cl100k tokens of the switched-on tools' names, labelled as a lower bound.
Session measurement is not attempted.

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

**Done (1.0.9.5, macOS):** every change carries a compatibility label
(`compatible`, `breaking`, `unknown`) and a security label (`none`, `review`),
each with a reason (`ContractSchemaAssessment.swift`). Schema keywords Kytto
does not compare make the result `unknown`, never `compatible`; a changed
`default` is `unknown` because calls that omit the argument behave
differently. Changes recorded earlier keep only their severity. Check the
Windows port.

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

## 20. Marketing and feedback review — 2026-11-14

**Scope:** planning from the supplied marketing records, the existing roadmap,
README and release notes. This is not a code review, a fresh reproduction of a
reported defect, or a commitment to ship these items. A source-code and
stability/security audit will start only when the owner requests it.

### Evidence and its limits

The main source is the internal `marketing/KyttoMCP-stav.md`, especially the
entries dated 2026-10-25/26 and 2026-11-04/06/09/13/14. Historical context comes
from the archived plans v3, v4 and v5 and the daily records for 2026-09-17,
2026-09-25 and 2026-10-12. These are historical records, not fresh verification
of the linked conversations. Private correspondence, contact lists, screenshots
and private-repository material are not reproduced here.

The records contain a small amount of qualitative feedback, not a representative
user survey. Reactions, stars and downloads are not feature votes or confirmed
repeat use. Requests in other products' issue trackers are adjacent demand,
not requests submitted to Kytto. Prepared outreach is not received feedback.

### Proposed shortlist

| Priority | Candidate | Evidence and expected benefit | Smallest useful scope / completion criterion |
|---|---|---|---|
| First | Make Contract Guard findings easier to interpret (§18) | Marketing records identify definition review as a promising value proposition, but do not prove broad demand. The 14 November record also describes a dependency update changing descriptions without an attack. | Explain what changed and why it deserves review; separate compatibility from security relevance. A compatible schema addition must not automatically mean breaking, and a description diff must not automatically mean malicious. Preserve the before/after evidence and explicit acknowledgement. |
| First | Scanner coverage for instructions that bypass consent | Concrete public discussion recorded on 25–26 October: a description change was visible to Contract Guard, but consent-bypass wording was not covered by the scanner at that time. The change was explicitly deferred. | Revisit narrowly scoped detection of instructions such as skipping approval. Include benign descriptions of approval behavior as counterexamples. Remain advisory; do not claim that keyword coverage enforces client permissions. Current behavior still needs verification before implementation. |
| First | Project configuration visibility (§12) | Repeated marketing discussions on 26 October and 6/9 November acknowledge project-config coverage limits. This supports the promise of knowing where a server is configured; it is not a newly confirmed application defect. | Read-only discovery in explicitly selected projects, exact source and scope labels, and understandable conflicts. Distinguish Claude Code local entries in its user file from repository `.mcp.json`. Discovery must not execute newly found servers. |
| Next | Explain context estimates (§16) | The existing roadmap records a direct request on 9 November for full-catalog versus session-loaded cost. The marketing plan v5 independently warns against claiming actual savings from schema size. The original X request was not independently found in the supplied marketing records. | Clearly separate full-schema estimate, any supported upfront estimate, and unavailable session measurements. Tools called through Gateway do not establish which definitions a model loaded. Show unknown when the client supplies no reliable evidence. |
| Next | Explain discovery coverage for additional clients (§11, §13) | Historical outreach exposes confusion between built-in clients, attached read-only files and hosted connections. The 16 October record contains an adjacent multi-client need including Grok, not a confirmed Kytto test. | Make supported source types and read-only limits discoverable. Select a new adapter only after a concrete current setup and redacted format example are available. Zed, Gemini CLI and Grok remain research candidates, not promised integrations. |

Suggested next-version theme: **understand what is configured, what changed,
and what Kytto actually checked**. This ordering is a product judgment from the
records; it is not a ranking measured from user votes. Final scope should wait
for the separately requested reliability audit.

### Expectations to carry into design

These are inferences from the marketing promises, not additional user requests:

- A successful Kytto check should say when it ran and what it checked. It must
  not imply that the target client is currently connected or that the server is
  safe. Preserve the distinction between configured, checked and observed use.
- A changed launch definition should be visible with a useful explanation and
  recovery path (§7); the marketing discussions about unexpected config edits
  reinforce this existing candidate. Do not imply monitoring of hooks,
  `AGENTS.md`, `CLAUDE.md` or agent traces.
- Secret management should distinguish replacing matching local values from
  revoking or issuing a key at its provider. The 4 and 14 November records
  reinforce clarity and discoverability of an existing feature, not demand for
  a new credential service.
- Preserve the local, account-free workflow, previews, backups and Undo when
  expanding coverage. The marketing material does not justify cloud sync,
  automatic server execution or automatic blocking as next-version defaults.

### Reports parked for the later audit

The 9 November marketing entry records two observations from a Windows 1.0.9.3
demo report. They are **reported, not reproduced in this review**, and their
status in the current build is unknown:

1. After a failed health check, Review reportedly shows Tool Safety findings
   from the saved contract without identifying them as historical. Later verify
   the failure-to-review flow and whether the last successful check is visible.
2. A leading space in an Add server argument reportedly prevents launch. Later
   reproduce with the exact field and input; do not assume trimming every
   argument is correct, because whitespace can be intentional argument data.

No new critical vulnerability is asserted here. These reports are inputs for
the future audit, not findings from inspecting the application today.

**Checked on macOS (2026-11-14):** both apply to the Mac as well. (1) Doctor
scans the last passing contract after a failed check without saying so; the
finding now names that check and its date. (2) Each Add server argument is one
argv element passed verbatim, so a leading space breaks an option; the form
now points it out and offers Remove spaces, without trimming automatically.
Windows still needs the same changes.

### Avoid reopening work already documented as shipped

- The October marketing summary says Claude Code plugin cache discovery is
  missing. Release notes for 1.0.8 document discovery of registered, enabled
  Claude Code and Codex plugins on both platforms. Do not turn arbitrary cache
  crawling into the replacement requirement.
- The migration-to-a-new-machine discussion overlaps with portable setup
  export/import documented in 1.0.8. First collect a limitation of that workflow
  before proposing another migration feature.
- Old client lists predate current README support for Antigravity, Devin Desktop
  (formerly Windsurf) and OpenCode. Historical claims about missing support or
  storage formats must not become current requirements without revalidation.
- Earlier tester fixes, OAuth support, package provenance checks, schema-key
  reordering and restart assistance are already covered by release notes.
  Keep any new regression report separate from the original delivered work.
- CLI automation, Linux, MCP Router import, Firewall and cost limits remain
  existing research/backlog items. This marketing review adds no verified new
  demand sufficient to move them ahead of the shortlist.

Marketing distribution work (listings, outreach, SEO and demo promotion) stays
outside the application feature backlog. No messages were sent and no public
marketing material was changed during this review.

## 21. Security and reliability audit — 2026-11-14

**Status: fixed on macOS (2026-11-14, branch `release/1.0.9.5`); Windows not
yet checked.** Each finding has regression tests in the regular core suite:
`GatewayToolFilterAmbiguityTests` and the end-to-end duplicate-name test
(AUD-07), `ErrorCodeConversionTests` (AUD-01), `ConfigWriterConcurrentEditTests`
(AUD-02), `RotationRollbackTests` (AUD-03, AUD-06) and `HealthCheckBoundaryTests`
(AUD-04, AUD-05). All nine original audit probes now fail against the fixed
code, the two crash probes with controlled errors instead of signal 5.

The audit covered the
active macOS implementation at `3ad6434`, with isolated reproductions against
its actual core library and Gateway executable. Historical copies were excluded;
the Windows implementation was not audited or tested. The full evidence,
conditions, source locations and acceptance criteria are in
[`SECURITY_RELIABILITY_AUDIT_2026-11-14.md`](SECURITY_RELIABILITY_AUDIT_2026-11-14.md).

P1 means a high-priority security boundary failure, process crash or data-loss
bug to address before the next release. P2 means a medium-priority defect with
more limited impact or additional conditions. No P0 vulnerability was confirmed.
These findings supplement the product shortlist in §20; that earlier review
did not inspect code.

| Order | ID | Priority | Confirmed defect | Completion criterion |
|---|---|---|---|---|
| 1 | AUD-07 | P1 | Gateway's JSON parser can see an allowed tool while the upstream sees a hidden tool in the same request. The real proxy forwarded and dispatched the hidden tool. | Reject ambiguous duplicate keys before forwarding narrowed traffic; cover requests, batches and tool-list responses with real upstream tests. |
| 2 | AUD-01 | P1 | An out-of-range JSON-RPC error code traps during conversion to `Int`, crashing the checking process on both HTTP and stdio paths. | Invalid, fractional, non-finite and out-of-range codes produce controlled protocol errors; test crash inputs in subprocesses. |
| 3 | AUD-02 | P1 | `ConfigWriter` checks the digest before reading/transforming, then overwrites a change made during the edit without another check. | Transform one verified byte snapshot and refuse observed changes before commit; the backup and write must describe that snapshot. |
| 4 | AUD-03 | P1 | Secret-rotation rollback restores old config bytes over a newer external save without checking the digest. | Roll back only bytes still matching that transaction's receipt; preserve conflicting edits and report incomplete rollback with recovery information. |
| 5 | AUD-04 | P2 | A blocking stdio response write can outlive the health-check deadline when the server stops reading. | Apply the shared deadline to writes as well as reads, and terminate/reap the process group after expiry. |
| 6 | AUD-05 | P2 | The first measurements write can persist credential-bearing stderr in a `0644` file under a newly created `0755` Kytto folder. Cross-user access depends on ancestor permissions. | Create measurements storage privately before writing and keep the file `0600`; retain useful stderr without broadening access. |
| 7 | AUD-06 | P2 | A failed rotation unconditionally deletes the destination credential ID, including a previously saved value when credential storage was not selected. | Track credential mutations and restore their previous state; failures before any credential mutation must leave the store unchanged. |

**Validation:** the existing 589-test core run had two loopback failures caused
by sandbox restrictions; both passed when rerun with local socket access. Six
additional non-crashing probes reproduced the findings, including the real
Gateway bypass. Two separately enabled crash probes both terminated with signal
5. The probes document current defects; passing them does not mean the defects
are fixed.

**Follow-up scope:** verify the same boundaries independently on Windows, then
convert each reproduction into a regression test for the corrected behavior.
The two Windows demo reports parked in §20 remain unverified by this macOS audit.

## 22. Feature proposals from community feedback — 2026-11-19

The November marketing notes record one concrete Windows migration request
for another app, an older multi-client question on X, and a collaboration
proposal from the mcpward maintainer. These are individual, qualitative
signals. The ordering below is a product proposal based on those notes and
the behavior documented in the changelog and shared UI.

### 1. Show where each definition comes from

When the same name appears globally and in an attached project, show each
definition with its file and scope in the server detail. Surface the conflict
from the matrix. For the first client with verified precedence, explain which
copy takes priority and in which workspace. Show unknown precedence elsewhere.
Keep project discovery read-only and require a separate user action to run a
project server. The older X question asked for one place to manage MCP across
Grok, Claude and Codex. It supports the multi-client workflow, with Grok
support still a research candidate. [X discussion](https://x.com/bhavikbuilds/status/2110978605648060530)

### 2. Import a project definition using the copy preview

Extend Kytto's existing client-to-client copy flow with an explicit project
source. Start with one server and one writable destination from the existing
client list. The preview names both files, lets the user choose between
same-named source definitions, shows unresolved environment variable names
with values masked, and explains unsupported settings before any write.
Reuse the existing config validation, backup and Undo. Keep selection and
preview local. If the destination already contains that name, stop and show
the conflict. This covers a gap between 1.0.9.5's read-only project inventory
and the existing portable setup flow for writable definitions. The Cherry
Studio issue describes a broader migration that also includes skills. Use
it to validate the workflow, and evaluate a Cherry Studio adapter separately
once there is a redacted config and a verified write path. [Migration request](https://github.com/CherryHQ/cherry-studio/issues/24823) · [Preview discussion](https://github.com/CherryHQ/cherry-studio/issues/24823#issuecomment-7010935588)

### 3. Show external launch configuration changes

Build on §7. When a known file is re-read after an external save, show a review
badge for a semantic change to a server's command, arguments, environment,
or remote endpoint. Show the file, scope and changed fields in a masked
before/after view. Formatting-only saves should stay quiet. Make this local
and observational, with explicit acknowledgement. This is a Kytto product
proposal that complements Contract Guard's review of tool definitions.
Cardigan's separate report about stale client-side tool lists is a reason to
label clearly when Kytto observed a definition and what it was able to
check. [Cardigan discussion](https://github.com/camaragon/cardigan/issues/92)

### Explore tool risk evidence with mcpward

Prototype an advisory tool detail that shows effect and whether a tool
interacts with external systems as separate fields. Label the evidence behind
each value, distinguishing server-declared annotations from Kytto's own
findings. Default to unknown when evidence is absent. Treat the shared
manifest as research while the format is still a draft. Decide on import or
export after both native implementations can agree on contract identity
against common test vectors. [Original discussion](https://github.com/TsvetanG2/mcpward/discussions/83) · [Draft RFC](https://github.com/TsvetanG2/mcpward/discussions/98)
