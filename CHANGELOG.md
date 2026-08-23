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

## [1.0.3] — 2026-08-23

Stability and security release following a full application audit, repeated native UI
smoke tests, shared web-interface checks, code review, and Debug/Release validation.

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

### Validation

| Check | Result |
|---|---:|
| Debug build | 0 warnings, 0 errors |
| Debug .NET tests | 380/380 |
| Release build | 0 warnings, 0 errors |
| Release .NET tests | 378/378 |
| Web UI tests | 9/9 |
| JavaScript syntax checks | 21/21 |
| ARM64 App and Gateway publish | passed |
| Inno Setup x64 and ARM64 verification builds | passed |

The visual audit covered Matrix, client detail, MCP Doctor, Activity, Profiles,
Secrets, Backups, Add Server, Settings, and isolated onboarding. Write-path tests used
a temporary profile and fixtures; real user configurations and secrets were not
changed.

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

[Unreleased]: https://github.com/heyitsjakub/KyttoMCP/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.3
[1.0.2-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.2-beta
[1.0.1-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.1-beta
