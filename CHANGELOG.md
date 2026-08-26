# Changelog

All notable changes to KyttoMCP are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
KyttoMCP is in beta; until 1.0 final, minor versions may include breaking changes to
saved profiles or app-local data. Your MCP client configuration is never migrated
without a backup.

## [Unreleased]

<!-- Working area for the next release. Add entries here as you build, under
     whichever of these headings apply, then move the whole block down into a new
     version section when you ship:

       ### Added      new features
       ### Changed    changes to existing behaviour
       ### Deprecated features that will be removed later
       ### Removed    features taken out
       ### Fixed      bug fixes
       ### Security   security fixes

     Write from the user's point of view, not the commit's:
       no:  refactored the config parser
       yes: Fixed Cursor servers vanishing from the matrix when mcp.json had a
            trailing comma
-->

Nothing yet. Planned work is tracked in [Issues](https://github.com/heyitsjakub/KyttoMCP/issues).

---

## [1.0.5] — 2026-08-26

Feature and reliability update for safer configuration changes, MCP discovery, imports, provenance, updates, and skills inventory.

### Added

- Pending restarts now reflect the actual difference between the applied baseline and the current configuration. Undo and restore clear stale restart state.
- Restart banners identify the affected client and number of pending changes. Dismissing a banner hides it without discarding the pending state.
- Claude Code project/workspace entries already present in `.claude.json` are discovered as read-only sources.
- A local MCP library scans a selected directory for supported JSON, JSONC, and TOML maps, then previews conflicts, warnings, and imports into built-in writable clients.
- A copy-paste agent prompt and strict JSON import flow validate the schema, fields, values, duplicates, and environment-variable names without accepting secret values.
- Server provenance, version, and maintenance state distinguish npm, Python, local package, local executable, remote, and unknown sources.
- A read-only skills inventory shows known global and workspace skill roots, scope, metadata status, duplicates, conflicts, and warnings.
- The in-app updater checks a fresh first-party manifest, downloads to private staging, verifies SHA-256 and version, and supports cancel, install, relaunch, and rollback.

### Changed and fixed

- Health-check timeout messages preserve the configured duration and no longer show misleading zero-second errors.
- Read-only sources and their scope stay distinct from writable client configurations throughout the UI.
- Existing backup, external-change detection, digest guards, and atomic-write safeguards remain in the write path.

### Security and privacy

- Configurations and secrets remain in native/local storage; secret values are not sent to the web UI, DTOs, logs, or updater requests.
- Discovery, scanning, inventory, and provenance checks do not execute discovered files or commands.
- The macOS build is ad-hoc signed rather than notarized. The Windows installer is not Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.5.dmg` | 5.9 MB | `5a458a9b16d10c344715a238b03cc16a6e8ec8002d83772ee9cab7f8d09c34d1` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.5.exe` | 78.5 MB | `f8546a423cf93fe54d932204beeea3b73d6fed3fdd3024000c86f838b80c1bb7` |

---

## [1.0.4.1] — 2026-08-26

Small cross-platform beta update.

### Added

- Added an automatic updater that checks for a newer KyttoMCP release.
- Added a manual **Check for updates** action so you can start the check yourself.

No other new features are included in this release.

### Distribution notes

The macOS build is ad-hoc signed rather than notarized. The Windows installer is not Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.4.1.dmg` | 5.3 MB | `578beaa8fcccfc9fbdb5828cc2713faa9b885b36bae49e7c89f6934838c883a4` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.4.1.exe` | 78.4 MB | `e551166f130d712a96bf194b609cd5ceffbfb266792a8ab316e09fca477c2fd7` |

---

## [1.0.4] — 2026-08-25

Feature and usability update for custom configuration sources and high-density displays.

### Added

- Attach multiple named JSON, JSONC or TOML configuration files as read-only custom sources with Global, Profile or Workspace display scope.
- Discover and watch custom-source servers without rewriting, creating or deleting the selected files.
- Distinguish the five built-in writable clients from read-only custom sources throughout onboarding, the sidebar, matrix and detail views.
- Explain the first matrix write before it happens, then remember that choice without bypassing Kytto's backup, stale-file and atomic-write safeguards.
- Offer Undo after successful matrix changes, including exact backup restoration for a server that was previously absent.

### Fixed

- Native mutation services reject custom-source IDs even when called outside the UI.
- Normal sidebar and matrix client icons now render at a consistent 22px × 22px.
- The restart banner no longer covers the matrix in short windows.

This release does not add hosted or organization-managed MCP inventory, writable custom sources, automatic workspace crawling or scope precedence.

### Distribution notes

The macOS build is ad-hoc signed rather than notarized. The Windows installer is not Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---|---|
| macOS (universal) | `KyttoMCP-1.0.4.dmg` | 5.3 MB | `0b5b208e9c14174792a2cb14c3a0cbd6f44a1d2ae5b3596444b2bfce361f3bb4` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.4.exe` | 78.4 MB | `be3b71b03c84f3b91abc6a41c35935567152ee4e63175b492efad719fd867651` |

---

## [1.0.3.1] — 2026-08-25

Focused cross-platform usability update for the client matrix.

### Fixed

- The matrix now keeps all five supported clients visible: Claude Desktop, Claude
  Code, Cursor, VS Code, and Codex.
- Clients that are not detected remain visible but read-only, with guidance for
  installations that use a custom configuration path.
- Fixed the matrix jumping vertically after changing a server below the fold.
- Fixed horizontal scroll position being lost when changing a server in a wide
  matrix.
- Keyboard focus is restored without moving the matrix away from the user's current
  position.

This update does not add support for additional client types.

### Distribution notes

The macOS build is ad-hoc signed rather than notarized. The Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---|---|
| macOS (universal) | `KyttoMCP-1.0.3.1.dmg` | 5.1 MB | `2e7a2b0944a0159bbd3a3f452a9c45028e048e262d4b15d828b683f700d0eb0a` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.3.1.exe` | 78.4 MB | `55811ea0be9a6a554b964e50abcf9de9c6292ff3a23e8ab1e40eadbfd989f262` |

---

## [1.0.3] — 2026-08-23

Stability and security release.

### Fixed

#### Configuration and discovery

- JSON and TOML writes preserve original formatting, comments, unrelated bytes, and
  CRLF line endings more reliably.
- Discovery now finds inline Codex MCP servers and diagnoses invalid UTF-8 files.
- Ambiguous duplicate maps or server names are rejected instead of silently editing
  the wrong entry.
- File watchers respect custom configuration paths and recover when a watched
  directory is created after Kytto starts.
- The first Claude configuration file is created correctly when either the classic or
  packaged location exists.

#### Secrets, backups, and safe writes

- Secret rotation is transactional across clients and restores the original bytes on
  failure.
- Masked secrets no longer expose the real prefix or suffix.
- Fixed secret rotation in inline TOML and when the new ID matches the original ID.
- Concurrent backups, parked configurations, configuration commits, and settings
  changes no longer lose data.
- A damaged park store is no longer overwritten with empty content.
- Failed toggles no longer create a false parked state; an incomplete rollback keeps
  a safety copy.

#### MCP health and Gateway

- MCP responses must contain the exact request ID and `jsonrpc: "2.0"`.
- Gateway exits cleanly after Ctrl+C without `OperationCanceledException`, a
  misleading usage message, or exit code 64.
- Health tests use an isolated fake MCP server instead of a real user server.

#### Windows app and IPC

- Fixed Maximize/Restore in the custom window title bar.
- Closing the window and Ctrl+W correctly hide Kytto in the tray when enabled; Quit
  always exits explicitly.
- Invalid WebView messages no longer crash the UI dispatcher.
- An unknown client can no longer acknowledge every restart or list every client's
  backups.
- Missing IPC payload fields return `badPayload`; unknown transports are no longer
  silently treated as `stdio`.
- Fixed IPC registration before the first page load and a crash caused by an invalid
  saved `trayClient`.
- Login item creation now creates a missing registry key, and icon resources are
  released correctly.
- The Debug diagnostic probe is excluded from Release builds.

#### WebView security

- The privileged native bridge is available only to the internal application origin.
- External navigation, nested frames, popups, and unsolicited permission requests are
  blocked.
- Static files load only from the allowed web boundary and the UI now has a Content
  Security Policy.
- WebView startup errors are shown to the user instead of failing silently.

#### Shared Windows/macOS UI

- Onboarding and missing-client messages are platform-neutral and use the host's
  `platformDisplayName`.
- Exact token counts are shown without `~`, estimates retain `~`, and partial matrix
  totals clearly identify the known minimum.
- Fixed Activity, Profiles, and Doctor footers plus horizontal matrix scrolling in
  narrow windows.
- The Add Server catalog is keyboard-operable.
- Dialogs now have correct ARIA attributes, initial focus, focus trapping, and focus
  restoration.
- Forms render from state without treating the DOM as the source of truth, while
  preserving the caret.
- Retrying after an initial error performs a complete data reload.
- Improved ARIA states, table headers, busy/no-op controls, and text contrast in light
  and dark themes.

#### Release pipeline

- Release scripts and Inno Setup distinguish `win-x64` and `win-arm64` publish
  directories, architectures, and artifact names.
- The setup version is read from `Kytto.App.csproj`, preventing installer and binary
  version drift.
- Debug-only integration tests do not run against the real user profile in Release
  configuration.

### Distribution notes

The macOS build is ad-hoc signed rather than notarized. The Windows installer was
built locally without `KYTTO_SIGNING_CERT_THUMBPRINT` and is not Authenticode-signed.
Verify the SHA-256 checksum before bypassing Gatekeeper or SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---|---|
| macOS (universal) | `KyttoMCP-1.0.3.dmg` | 5.1 MB | `c037817dea99c5f884b2c319fc0916b8ec884f5478d7f975d365494a101494fd` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.3.exe` | 78.4 MB | `5b4114a0ec69b4dc7002171e5b52915a4c6dbbb719321b499b60d1cc1fba396b` |

---

## [1.0.2-beta] — 2026-08-09

Maintenance release: bug fixes plus stability and performance improvements. No new features.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---|---|
| macOS (universal) | `KyttoMCP-1.0.2-beta.dmg` | 4.0 MB | `86336a51163a4ff9b579cc59519e0bae7d085a3739b7aa04e7715bfeb72c0b02` |
| Windows | `Kytto-Setup-1.0.2-beta.exe` | 74.8 MB | `6b09a48702725c57f283d984b1b932d7da1f81e11152da7dd0d6bc12a1e07271` |

---

## [1.0.1-beta] — 2026-08-04

A maintenance build released one day after the first public beta.

The individual changes were not recorded at the time, and I would rather leave this
entry honest than reconstruct it from memory. Itemised entries start with the next
release.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---|---|
| macOS (universal) | `KyttoMCP-1.0.1.dmg` | 3.6 MB | `f742df0ef5d9f97abd07c61f09d97bf8106895d70ac787a0617ac9887c60fcfc` |
| Windows | `Kytto-Setup-1.0.1-beta.exe` | 74.2 MB | `622da113114b9906ca4426da25a4fb72d1369c6e9d1aa9427b53b78a205fb202` |

---

## 1.0-beta — 2026-08-03

First public beta, for macOS and Windows.

- Cross-client matrix for Claude Desktop, Claude Code, Cursor, VS Code and Codex
- Manual server authoring plus a built-in catalog
- On-demand MCP handshake exposing tools, prompts, resources, protocol details and stderr
- Local profiles with a change preview before applying
- Secret discovery, masking and rotation, with optional Keychain storage
- Timestamped backups, external-change detection and atomic writes
- Context cost estimation per server and per profile
- MCP Doctor findings with previewed fixes
- Optional Gateway mode with metadata-only Live Activity

[Unreleased]: https://github.com/heyitsjakub/KyttoMCP/compare/v1.0.5...HEAD
[1.0.5]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5
[1.0.4.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.4.1
[1.0.4]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.4
[1.0.3.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.3.1
[1.0.3]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.3
[1.0.2-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.2-beta
[1.0.1-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.1-beta
