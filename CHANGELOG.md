# Changelog

All notable changes to KyttoMCP are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
KyttoMCP is in beta; until 1.0 final, minor versions may include breaking changes to
saved profiles or app-local data. Your MCP client configuration is never migrated
without a backup.

## [Unreleased]

### Added (Windows)

- **Restart now** (#14): the pending-restart notice offers Restart now per client and Restart all when several apply. Kytto quits the client's app and opens it again, so it reads the new configuration; an app that is not running is not opened, and an app that asks about unsaved work is left running for you to answer. Claude Desktop, Cursor, VS Code, Antigravity and Devin Desktop restart this way; the terminal clients stay on "I restarted".

### Fixed (Windows)

- A maximised window no longer covers the taskbar (#15).
- The caption buttons stay visible when another app is active, and no longer float off the window at a screen edge or a monitor seam.
- The window follows tray changes: switching a server from the tray updates the open window, and the tray tooltip stays current.
- "Backups to keep" applies immediately instead of after a restart.
- Changing sidebar width no longer re-reads every config.
- Saving a path override no longer shows a false restart notice.
- Only one Kytto runs at a time; opening it from the Start menu while it sits in the tray shows the running window.
- An unreadable settings file shows a message instead of closing the app silently.
- Windows keeps Kytto sharp on a monitor with a different scale.
- Several Skills and MCP Library screens no longer fail to render when native reports a warning.
- The filter field keeps focus while you type; keyboard focus stays on a matrix switch after it changes.
- The sticky restart bar, secret and diagnostic layouts, onboarding, and the matrix at 1280 px lay out correctly.
- Settings labels are announced by screen readers.

## [1.0.9.3] — 2026-11-07

Restart now: Kytto can quit and reopen the apps that only read their MCP
configuration at launch, so a change takes effect without a trip to the Dock or
the tray ([#14](https://github.com/heyitsjakub/KyttoMCP/issues/14)). Plus a pass
over the whole interface for layout, focus and wording bugs. Released for macOS
on 2026-11-07; Windows 1.0.9.3, which also fixes a maximised window covering the
taskbar ([#15](https://github.com/heyitsjakub/KyttoMCP/issues/15)), follows —
its changes are under [Unreleased] until then.

**Download**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.9.3.dmg` | 6.0 MB | `c92bd99bc4a8ca01a0682e476662b9e93b6759b4c7babe4a48218fcae65d5aff` |

The macOS build is ad-hoc signed rather than notarized. Verify the SHA-256
checksum before bypassing Gatekeeper.

### Added (macOS)

- **Restart now** in the pending-changes notice ([#14](https://github.com/heyitsjakub/KyttoMCP/issues/14)). For Claude Desktop, Cursor, VS Code, Antigravity and Devin Desktop, Kytto can quit the app and open it again so it loads the new configuration, then clears the notice. With several apps waiting, **Restart all** does them one after another. Kytto asks first and names the apps, because unsaved work in them may be lost. It quits each app the way its own Quit command does and never force-quits: an app that asks about unsaved work, or refuses to quit, keeps its notice and "I restarted". An app that is not running is left closed, since it reads the new configuration when you next open it. Terminal clients (Claude Code, Codex, OpenCode) keep "I restarted", because Kytto cannot restart a shell session.

### Fixed (macOS)

- The restart notice lists several clients as aligned rows, with their buttons in one column, instead of buttons trailing each sentence.
- In full screen, the top row with the server and token counts is no longer cut in half or pushed right as if the window buttons were still there.
- Double-clicking the top strip of the window zooms or minimises it, as System Settings says a title bar should.
- The Appearance setting now applies to the whole window, the Settings window, alerts and file panels, not only to the page. A forced Light theme on a Dark Mac no longer leaves the sidebar and toolbar a muddy grey.
- The menu bar item no longer stays on "Checking All Servers…" after a check, and no longer offers a check that is already running.
- Typing a configuration path in Settings no longer re-reads every config at each pause, and pointing a client at another file no longer raises a false "restart" notice.
- Refresh Configurations (⌘R) and Find… (⌘F) bring the main window forward instead of doing nothing when it is closed or behind Settings.
- The menu bar icon appears when Kytto starts without its main window.
- Settings names all eight built-in clients, not five.
- In windows up to 1280 pt wide, clicking a server shows its details right below the matrix instead of under every row, and the client icons stay above their columns while the matrix scrolls.
- On a client's screen in a narrow window, the token and on/off columns are no longer clipped off the right edge.
- The update banner has its accent tint again, and "Check for updates" in the status bar is a link-sized control instead of the whole empty width of the bar.
- After you close a dialog, keyboard focus returns to the control that opened it. A new dialog focuses its first field instead of Close, so Enter no longer closes it.
- Selecting text in a dialog and releasing the mouse outside it no longer closes the dialog and discards what you typed.
- A form's validation error is scrolled into view when you submit, instead of appearing above the visible part of the form.
- Changing the client in "Apply profile" no longer replays the dialog's opening animation, and plugin servers in a profile are no longer reported as missing.
- Disabled buttons look disabled, and save errors in the tool pickers are shown as errors.
- After installing an update that needs a manual relaunch, the banner no longer offers to download the same version again.
- A long configuration path in Diagnostics no longer squeezes the error message into one word per line. The full path is in its tooltip.
- A wide sidebar in a small window no longer leaves the content pane a sliver. The sidebar takes at most 40% of the window, and the update banner wraps.
- Truncated failure reasons, commands, matched tools and backup paths show their full text on hover.
- Choosing another screen while a slow one is loading (Skills) no longer jumps back to the slow one when it finishes.
- The column highlight under the pointer works for every client column, not only the first five.
- In a server's detail, a client's state and its "Read-only" or "Gateway" badge no longer run together.
- A failed "Continue to matrix" on the first-run screen now says why.
- The Library button says "Working…" during preview and import, not "Scanning…".
- "Check visible" is disabled with an explanation while a single server is being checked, instead of ignoring the click.
- Typing accented characters (č, ô) in the matrix filter no longer drops or doubles them.
- After switching a matrix cell from the keyboard, focus stays on that cell instead of jumping to the top of the page.
- On first launch with many sources, the top of the "What Kytto found" card is no longer cut off above the scroll area.
- With several clients waiting for a restart in a short window, the restart notice scrolls inside itself instead of covering the matrix.
- A revealed long token no longer pushes the Hide button off the right edge of the window.
- "1 require sign-in" now reads "1 requires sign-in".

## [1.0.9.2] — 2026-11-04

MCP Doctor stops recommending archived reference servers and flags them where
they are already in use. Released for macOS on 2026-11-04 and for Windows on
2026-11-05 with the same change, listed under the Windows headings below.

**Download**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.9.2.dmg` | 6.0 MB | `5cbb4f0810ad9fb6985ef7163be2ed69ba80ca18428d18097c7e2d7e9995440e` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.9.2.exe` | 78.6 MB | `fa24c7ff8d80a312a41e86959047f3d9c5bf3486712523485216a3f2d1243618` |

The macOS build is ad-hoc signed rather than notarized. Verify the SHA-256
checksum before bypassing Gatekeeper. The Windows installer is unsigned;
verify its checksum before running it.

### Added (macOS)

- Archived reference servers are flagged. Eleven of the npm reference servers the MCP project moved to [`modelcontextprotocol/servers-archived`](https://github.com/modelcontextprotocol/servers-archived) — among them `@modelcontextprotocol/server-github`, `-puppeteer`, `-slack` and `-postgres` — and the PyPI `mcp-server-sentry`, `mcp-server-sqlite` and `mcp-server-aws-kb-retrieval` get no more fixes, security fixes included, yet coding agents still install them by name. MCP Doctor now shows a warning for a server that runs one of them. The check is offline and changes nothing.
- "Check package safety" also reads whether the npm registry marks the package version deprecated, and shows the registry's message.

### Fixed (macOS)

- The look-alike package warning no longer tells you to switch to the official package when that package is itself archived — `mcp-server-github` used to be pointed at `@modelcontextprotocol/server-github`, which npm marks deprecated.

### Added (Windows) — 2026-11-05

- Archived reference servers are flagged. Eleven of the npm reference servers the MCP project moved to [`modelcontextprotocol/servers-archived`](https://github.com/modelcontextprotocol/servers-archived) — among them `@modelcontextprotocol/server-github`, `-puppeteer`, `-slack` and `-postgres` — and the PyPI `mcp-server-sentry`, `mcp-server-sqlite` and `mcp-server-aws-kb-retrieval` get no more fixes, security fixes included, yet coding agents still install them by name. MCP Doctor now shows a warning for a server that runs one of them. The check is offline and changes nothing.
- "Check package safety" also reads whether the npm registry marks the package version deprecated, and shows the registry's message.

### Fixed (Windows)

- The look-alike package warning no longer tells you to switch to the official package when that package is itself archived — `mcp-server-github` used to be pointed at `@modelcontextprotocol/server-github`, which npm marks deprecated.

## [1.0.9.1] — 2026-10-30

Three new clients, switches for single tools, a package safety check in MCP
Doctor, and a faster, steadier window. Released for macOS on 2026-10-30. The
Windows build follows with the same features, listed under the Windows
headings below.

**Download**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.9.1.dmg` | 6.0 MB | `cb69196b4c16a76265c9a4bbabfa1e8b3ab57e3ae7fba2805c02b8d0dd1c98fa` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.9.1.exe` | 78.6 MB | `965c6babf1a5241031db527e80927c55faad98bd85b4ab2bf3d39a56e23703f2` |

The macOS build is ad-hoc signed rather than notarized. Verify the SHA-256
checksum before bypassing Gatekeeper. The Windows installer is unsigned;
verify its checksum before running it.

### Added (macOS)

- Three new clients: Google Antigravity, Devin Desktop (Windsurf until June 2026) and OpenCode. Their servers appear in the matrix, and you can switch them on and off, add, edit, copy and remove them, use them in profiles and import them from a portable setup, with the usual preview, backup and Undo. Switching a server off keeps its definition in the file (`"disabled": true` in Antigravity and Devin Desktop, `"enabled": false` in OpenCode), so nothing has to be stored aside.
- Copying a server between clients writes it the way the target client expects: a remote endpoint becomes `serverUrl` in Antigravity, OpenCode gets its own `type`/`command`/`environment` entry, and the source client's on/off and tool settings stay behind. A copy that would lose headers, a working directory or sign-in settings is refused with the settings named, and a legacy SSE server is not written into Antigravity, which accepts only Streamable HTTP.
- Remote checks resolve OpenCode's `{env:VAR}` placeholders in headers.
- Switch single tools of a server off. The tool list in a server's details now has a button per client that can do it in its own configuration — Claude Code, Codex, Antigravity and OpenCode — which opens a picker with each tool's context cost and what your choice saves. Saving is one guarded write with a backup and Undo: an `mcp__<server>__<tool>` deny rule in Claude Code (which takes the tool out of its context), `disabled_tools` in Codex, `disabledTools` in Antigravity, `<server>_<tool>: false` in OpenCode. Tools switched off are marked in the list, and the tool counter and VS Code's 128-tool limit count only the tools that stay on. For Cursor, VS Code, Claude Desktop and Devin Desktop the details point to Gateway mode, which can already hide tools. Renaming a server keeps its tools switched off: OpenCode `tools` keys and Claude Code `mcp__<server>__…` rules are renamed with it.
- Package safety in MCP Doctor. A server that fetches a package whose name imitates an official one — `mcp-server-github` instead of `@modelcontextprotocol/server-github`, or a scope one letter off — is flagged, offline. "Check package safety" (in Doctor and in a server's details) asks osv.dev whether the package is known malware or the pinned version has known vulnerabilities, and for npm packages whether that version runs a script on install. It sends only the package name, its registry and a pinned version, and only when you click. Nothing is switched off automatically.

### Changed (macOS)

- Kytto re-reads your client configs in the background, so the window no longer stalls when a config changes on disk, when you press Refresh or when you switch back to Kytto.
- Claude Code's `~/.claude.json` is read once per refresh instead of once for every Claude Code project, which makes refreshing much faster when Claude Code's history has made that file large.

### Fixed (macOS)

- While you used Claude Code, Kytto redrew its window after almost every message even though no server had changed. It now updates only when something it shows has changed, and still notices every real edit before it writes.
- Opening Secrets could start a Git process for every secret when your home folder is a Git repository. Each config file is now checked once.

### Added (Windows) — 2026-11-02

- Three new clients: Google Antigravity, Devin Desktop (Windsurf until June 2026) and OpenCode. Their servers appear in the matrix, and you can switch them on and off, add, edit, copy and remove them, use them in profiles and import them from a portable setup, with the usual preview, backup and Undo. Switching a server off keeps its definition in the file (`"disabled": true` in Antigravity and Devin Desktop, `"enabled": false` in OpenCode), so nothing has to be stored aside. The config paths are `%USERPROFILE%\.gemini\config\mcp_config.json`, `%APPDATA%\devin\mcp_config.json` and `%USERPROFILE%\.config\opencode\opencode.json`; none of the three apps was installed on the test machine, so the paths and the "installed" detection follow the clients' documentation and are not verified against a real install.
- Copying a server between clients writes it the way the target client expects: a remote endpoint becomes `serverUrl` in Antigravity, OpenCode gets its own `type`/`command`/`environment` entry, and the source client's on/off and tool settings stay behind. A copy that would lose headers, a working directory or sign-in settings is refused with the settings named, and a legacy SSE server is not written into Antigravity, which accepts only Streamable HTTP.
- Remote checks resolve OpenCode's `{env:VAR}` placeholders in headers.
- Switch single tools of a server off. The tool list in a server's details now has a button per client that can do it in its own configuration — Claude Code, Codex, Antigravity and OpenCode — which opens a picker with each tool's context cost and what your choice saves. Saving is one guarded write with a backup and Undo: an `mcp__<server>__<tool>` deny rule in Claude Code (which takes the tool out of its context), `disabled_tools` in Codex, `disabledTools` in Antigravity, `<server>_<tool>: false` in OpenCode. Tools switched off are marked in the list, and the tool counter and VS Code's 128-tool limit count only the tools that stay on. For Cursor, VS Code, Claude Desktop and Devin Desktop the details point to Gateway mode, which can already hide tools. Renaming a server keeps its tools switched off: OpenCode `tools` keys and Claude Code `mcp__<server>__…` rules are renamed with it.
- Package safety in MCP Doctor. A server that fetches a package whose name imitates an official one — `mcp-server-github` instead of `@modelcontextprotocol/server-github`, or a scope one letter off — is flagged, offline. "Check package safety" (in Doctor and in a server's details) asks osv.dev whether the package is known malware or the pinned version has known vulnerabilities, and for npm packages whether that version runs a script on install. It sends only the package name, its registry and a pinned version, and only when you click. Nothing is switched off automatically.

### Changed (Windows)

- Kytto re-reads your client configs on a background thread when a config changes on disk or when you press Refresh, and redraws the window only when something it shows is different. Claude Code rewrites `~/.claude.json` after almost every message without touching a server, which no longer redraws anything.
- Gateway mode does not support OpenCode yet and says so instead of writing a helper entry OpenCode would not read.

### Fixed (Windows)

- Opening Secrets could start a Git process for every secret when your home folder is a Git repository. Each config file is now checked once.
- The Gateway route store was read once for every client on each refresh and twice for each state reply; it is read once.

## [1.0.9] — 2026-10-24

Support for the MCP 2026-07-28 revision and the fixes from a full review of the
app. Released for macOS on 2026-10-24 and for Windows on 2026-10-25, where it
also includes the fixes the review found in the Windows code.

### Added (macOS)

- Health checks speak MCP 2026-07-28, which drops the `initialize` handshake. Kytto asks `server/discover` first and falls back to the handshake for older servers, on stdio and Streamable HTTP alike, so a server that only speaks the new revision is no longer reported as broken.
- MCP Doctor notes when a server uses something the 2026-07-28 revision deprecates: the legacy HTTP+SSE transport, Roots, Sampling or MCP logging.

### Changed (macOS)

- An endpoint that turns out to use the legacy HTTP+SSE transport is shown as not checkable instead of failed, and so is a plain-HTTP address on your local network.
- Signing in to a remote server again asks for any additional scope the server requested, keeping the scopes you already granted.
- Up to a window width of 1280 px the server inspector sits below the matrix and the window scrolls, so more client columns fit beside the server names.
- Copying a server between a JSON client and Codex is refused, with the settings named, when it uses HTTP headers, a bearer token or a working directory that the other format could not carry.

### Fixed (macOS) — configuration writes

- Renaming a server that was switched off in Claude Code switched it back on. Renaming only the letter case of a name was ignored.
- Saving an edit overwrote other clients' environment values with one client's value, and removed keys only some clients had. Each client now keeps its own value for an untouched field, and renaming a key in the edit form keeps its value.
- Undo after adding a server to a client restored the whole file from before the change, discarding anything the client or you changed in it since. Undo now removes just that server.
- Applying a profile tried, and failed, to switch off plugin servers. They are now left to their plugin.
- Toggling a Codex server that shared its name with a Claude Desktop extension was refused, and a Claude Code deny entry used another client's spelling of the name. Two Codex tables that differ only in case are now refused instead of toggling one of them.
- Switching on the first server in a Claude Code that had never run failed with a misleading "changed on disk" message.
- Backup retention counted all of a client's files together, so Claude Code's two-file switch could push out every backup of its main config, or delete the backup its own Undo needed.
- Kytto could write a Codex `config.toml` that Codex refuses (a table defined twice), and reported some valid files as unreadable. Removing an entry from a VS Code `mcp.json` with comments could leave a trailing comma.
- Importing an entry point from the MCP Library wrote a relative path that clients could not start.

### Fixed (macOS) — health checks, Doctor and Contract Guard

- A server reply nested deeply enough crashed Kytto, and so could a server that exited between two of Kytto's messages.
- Health checks reported prompts-only servers as broken, timed out on servers that asked Kytto a question, showed truncated error output, and could leave a server running if Kytto quit mid-check.
- Doctor offered to pin an executable from an outdated check, called "method not found" a missing executable, and marked servers stale by comparing against the version a server reports about itself.
- Starting Kytto ran your login shell on the main thread, and a daemon started from your shell profile could make Kytto fall back to a minimal PATH.
- Contract Guard missed a schema change hidden behind a repeated key, showed identical before/after text for changes past the first 800 characters, and printed invisible characters as nothing. "Mark as reviewed" now accepts only the changes that were on screen.
- The Tool Safety Scanner missed variation selectors, the combining grapheme joiner and Hangul fillers used to hide text or split a phrase.

### Fixed (macOS) — remote servers, Gateway and portable setup

- Remote OAuth sign-in failed when the authorization server granted a different scope than requested, when protected-resource metadata described the whole origin, when a discovery URL answered with an error page, or when `expires_in` arrived as text. A token without an expiry or revoked early is now refreshed once, and two checks no longer spend the same refresh token.
- Gateway mode dropped a Codex inline `env` and showed its values in the preview, lost working directories and client placeholders, and could leave server processes running after the client quit. A hidden tool could still be called inside a JSON-RPC batch.
- Health checks of a Gateway server now check the server itself rather than the masked helper, so hidden tools stay in the tool picker. The activity log is rotated and read without stalling the window.
- Portable export failed for any server launched through an absolute `npx` path and stopped at the first unusual server. Import into Codex refused remote URLs ending in `/sse`, missed name conflicts with installed extensions and plugins, and asked for inputs of copies going nowhere. Input labels now say where each value goes, whatever the file claims.

### Fixed (macOS) — app and interface

- Menu bar rows for servers a client does not have could be clicked, which copied the server in. Check All could run twice at once and reported interrupted checks as a disk problem.
- A setting written by a newer version no longer resets all settings, turning off "Open at login" now also cancels a registration waiting for approval, and the Settings error alert no longer comes back after "Report a bug…".
- Cancelling an update download now stops it at once, abandoned installers are cleaned up, and a failed rollback names where the previous version is.
- In narrow windows the matrix and inspector spilled over the status bar. Contract Guard diffs were squeezed into a narrow column, sheets had no shadow, row status lines were misaligned and wrapped, and Compact density looked like an active filter.
- Refresh now also reloads Secrets, Profiles and Activity. Dragging the sidebar no longer freezes when the window updates mid-drag, keyboard focus stays inside an open dialog, and Apply profile no longer does nothing when the default client is a read-only source.

### Added (Windows) — 2026-10-25

- Health checks speak MCP 2026-07-28. Kytto asks `server/discover` first and falls back to the `initialize` handshake for older servers, on stdio and Streamable HTTP alike, so a server that only speaks the new revision is no longer reported as broken.
- MCP Doctor notes when a server uses something the 2026-07-28 revision deprecates: the legacy HTTP+SSE transport, Roots, Sampling or MCP logging.

### Changed (Windows)

- An endpoint that turns out to use the legacy HTTP+SSE transport is shown as not checkable instead of failed, and so is a plain-HTTP address off this computer.
- Signing in to a remote server again asks for any additional scope the server requested, keeping the scopes you already granted.
- Up to a window width of 1280 px the server inspector sits below the matrix, and it is hidden there while no server is selected.
- Copying a server between a JSON client and Codex is refused, with the settings named, when it uses HTTP headers, a bearer token, a working directory or other settings the other format could not carry. A Gateway definition is never copied to another client.

### Fixed (Windows) — configuration writes

- Renaming a server that was switched off in Claude Code switched it back on. Renaming only the letter case of a name was ignored.
- Saving an edit overwrote other clients' environment values with one client's value. Each client now keeps its own value for an untouched field, and renaming a key in the edit form keeps its value.
- Undo after adding a server to a client restored the whole file from before the change. Undo now removes just that server.
- Applying a profile tried, and failed, to switch off plugin servers. They are now left to their plugin, in the preview too.
- A Claude Code deny entry used another client's spelling of the name, and a Codex server sharing its name with a Claude Desktop extension could not be toggled.
- Backup retention counted all of a client's files together, so Claude Code's two-file switch could push out every backup of its main config.
- Kytto could write a Codex `config.toml` that Codex refuses (a table defined twice), and reported some valid files as unreadable. Removing an entry from a JSON file with comments could leave a trailing comma.
- Importing an entry point from the MCP Library wrote a relative path that clients could not start.

### Fixed (Windows) — health checks, Doctor and Contract Guard

- A server reply nested deeply enough could crash Kytto.
- Health checks reported prompts-only servers as broken, timed out on servers that asked Kytto a question, and could cut off the end of a server's error output.
- Doctor called "method not found" a missing executable.
- Contract Guard showed identical before/after text for changes past the first 800 characters and printed invisible characters as nothing. "Mark as reviewed" now accepts only the changes that were on screen.
- The Tool Safety Scanner missed variation selectors, the combining grapheme joiner, the Mongolian vowel separator and Hangul fillers used to hide text or split a phrase.
- Check all could run twice at once, and a sign-in during the run stopped it instead of skipping that server.

### Fixed (Windows) — remote servers, Gateway and portable setup

- Remote sign-in failed when the authorization server granted a different scope than requested, when protected-resource metadata described the whole origin, when a discovery URL answered with an error page, or when `expires_in` arrived as text. A token without an expiry or revoked early is now refreshed once, and two checks no longer spend the same refresh token.
- Gateway mode dropped a Codex inline `env` and showed its values in the preview, could lose working directories and client placeholders, and could leave server processes running after the client quit. A hidden tool could still be called inside a JSON-RPC batch. Gateway mode is refused when Kytto runs from inside a ZIP file.
- Health checks of a Gateway server now check the server itself rather than the masked helper. The activity log is rotated and read without stalling the window.
- Portable import into Codex refused remote URLs ending in `/sse` and asked for inputs of copies going nowhere. Input labels now say where each value goes, whatever the file claims.

### Fixed (Windows) — app and interface

- A setting written by a newer version no longer resets all settings.
- Abandoned update downloads are cleaned up. Skills kept behind a junction or symbolic link are found. An older MCP Library scan finishing last no longer replaces a newer one.
- Sheets had no shadow, row status lines wrapped, and Compact density looked like an active filter.
- Refresh now also reloads Secrets, Profiles and Activity. Dragging the sidebar no longer freezes when the window updates mid-drag, keyboard focus stays inside an open dialog, and a failed setup import closes with an explanation.

### Downloads

| Platform | Artifact | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.9.dmg` | 5.7 MB | `2cb955c50c2df94af27fe4445fc6929f17cb4664089f383b9c0d49cdd8837846` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.9.exe` | 78.6 MB | `085d0f843e334ead78d37bda6d6074f11e14dfd9568bdca84a5cfef8d0f336ac` |

The macOS build is on the [v1.0.9](https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.9) release and the Windows build on [v1.0.9-windows](https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.9-windows). The Windows installer is unsigned; see the Windows release notes for what was and was not verified.

## [1.0.8] — 2026-10-15

### Added (macOS)

- Workspace and source filters, grouped navigation, and a matrix with a persistent server inspector. Selection, scrolling and keyboard focus survive updates; motion respects Reduce Motion.
- Read-only discovery of registered, enabled Claude Code and Codex MCP plugins, with their source and version. Supported plugin-root placeholders and working directories are resolved only for explicit checks; stale caches and nested test fixtures are not treated as active servers.
- Readable Contract Guard changes with bounded before/after descriptions, schema fields, defaults, required arguments and annotations. Changes to confirmation and dry-run fields include a safeguard review explanation.
- Remote Doctor for Streamable HTTP: MCP initialization, JSON/SSE responses, paginated tool discovery, token measurements and structured authorization/HTTP/protocol errors. Checks are bounded and never call tools. Legacy SSE transport remains unsupported.
- Explicit native OAuth sign-in for Remote Doctor, using discovered issuer metadata, S256 PKCE, callback validation, resource-bound Keychain credentials and refresh. Supports public client registration where offered, or a pre-registered public client ID.
- Portable JSON setup export/import for writable server definitions and profiles, with native inputs, explicit preview, disabled imports, guarded writes/backups and optional pending contract fingerprints requiring a fresh local check.

### Fixed (macOS)

- A health check finishing after its server definition changes no longer records its result against the replacement definition.
- An in-flight authorization refresh cannot restore credentials the user has forgotten or superseded; unreadable pending-contract state cannot partially acknowledge an alert.
- Scope, Source and Rows controls now use styled, keyboard-accessible controls; Profiles keeps export, import and New profile actions aligned across window sizes.
- Added private-site/GitHub bug reporting and a native recovery screen after WebKit interface failures, without attaching logs or configuration.

### Added (Windows)

- Report a bug from Settings, the tray menu's Help submenu, error notices and interface failure screens, using the private website form or public GitHub Issues. The links open in your browser and attach nothing. If the interface fails to load, or its WebView2 browser process ends, a native recovery panel keeps Reload and the report links available.
- Workspace and source filters, grouped sidebar navigation, and a matrix with a persistent server inspector. Selection, scrolling and keyboard focus survive updates; Rows can be Comfortable or Compact; motion respects the system animation setting.
- Read-only discovery of registered, enabled Claude Code and Codex MCP plugins, with their source and version. A plugin is never switched, edited or copied by Kytto; stale caches, disabled plugins and nested test fixtures are not treated as active servers.
- Readable Contract Guard changes: bounded before/after text for descriptions, annotations, schema fields, defaults and required arguments, with an explanation when a confirmation or dry-run field changes.
- Remote Doctor for Streamable HTTP servers: MCP initialization, JSON and SSE replies, paginated tool discovery and token measurements, all bounded and never calling a tool. Legacy SSE servers stay unsupported.
- Explicit sign-in for remote servers that ask for it, started only by you: discovered issuer metadata, S256 PKCE, a loopback callback that exists only while you sign in, and resource-bound credentials kept in Windows Credential Manager. Kytto's sign-in does not sign in your other MCP clients.
- Portable setup: export writable server definitions and profiles to a JSON file and import them on another machine, or from the macOS app. Credentials, private paths and every remote URL become inputs you type on import. Imported servers always start disabled, existing names are never overwritten, writes use backups and stale-file guards, and any failure restores what was there. Reviewed contract fingerprints are optional and stay pending until a fresh local check.
- Export setup, Import setup and New profile share one aligned action group in the Profiles header.

### Changed (Windows)

- MCP Library and Skills moved from the toolbar to the sidebar's Discover group.
- A `.cmd` launcher now receives its arguments exactly as written, including JSON and URL-encoded values. An argument that `cmd.exe` would expand, or that contains a line break, is refused with an explanation instead of being started changed.
- Links in the app window open only Kytto's two report pages, and only when you click them.

### Fixed (Windows)

- A health check that finishes after its server definition changes no longer records its result against the replacement definition.
- Scope, Source and Rows pickers use styled controls with icons, a single chevron, consistent height and keyboard focus.

### Downloads

| Platform | Artifact | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.8.dmg` | 5.5 MB | `177f92d8772981bf96ca108f9813ae47d9e31e2f6fe67c3abef417b41358d235` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.8.exe` | 78.6 MB | `0b51ecd7099809da767b904d4f349b6075b362a69410619df63d4bb422f382cd` |

The Windows installer is unsigned; see the Windows release notes for the remaining artifact verification limits.

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

---

## [1.0.7.1] — 2026-10-09

A fix release. Contract Guard now keeps reporting a changed tool until you
review it, the Tool Safety Scanner reads tools the way your client does, and a
review of the config write path fixed several ways an edit, a restore or a
switch-off could change more than you asked for. No new features.

### Fixed — Contract Guard and the Tool Safety Scanner

- **Contract Guard alerts disappeared after the next health check.** Every
  successful check quietly made the tools it had just seen the new baseline, so
  a server that changed a tool description was reported only until the following
  check, even if you never looked. Now only the first successful check and your
  acknowledgement move the baseline. An unreviewed change stays on screen,
  further changes are added to it, and it clears on its own only if the server
  goes back to the contract you approved.
- **Reordered keys in an input schema were reported as a breaking change**
  ([#12](https://github.com/heyitsjakub/KyttoMCP/issues/12)). Schemas are now
  compared by content; reordered array items, such as `enum` values, still count.
- **The scanner missed bidirectional isolate characters** U+2066–U+2069
  ([#13](https://github.com/heyitsjakub/KyttoMCP/issues/13)), half of the
  "Trojan Source" technique.
- **A tool could hide its real description behind a repeated key.** When a
  tool's JSON said `"description"` twice, Kytto read the first and the client
  gave the model the second. Kytto now reads tools the way clients do.
- **The scanner missed text written with JSON escapes in an input schema**
  (such as `\u200b`) and phrases split by a soft hyphen, a zero-width joiner or
  a line break.
- **A failed health check made safety findings disappear.** Findings and
  tool-name collisions now use the tools from the last passing check, and
  acknowledging a change after a failed check accepts the contract you were shown.
- **A server that listed two tools with the same name crashed Kytto.**
- **Renaming a server dropped its Contract Guard baseline and any unreviewed
  alert.** They now move with the server.
- **A health check that found a Contract Guard change said nothing** beyond a
  small badge. It now shows a notice naming the server.

### Fixed — health checks and the Gateway

- **Only the first page of a paginated tool list was read.** Tool count, token
  weight, the scanner and Contract Guard now cover every page.
- **Ordinary sign-in errors were shown as "waiting for authorization".** A
  server that says "Authentication required — set GITHUB_TOKEN" is now shown as
  failed.
- **A huge tool description could hold a health check far past its timeout.**
- **Two overlapping checks of one server could save the older result**, and a
  check that finished after its server was deleted brought the server's
  measurements back.
- **An unreadable measurements file was silently replaced with an empty one**,
  losing every Contract Guard baseline. Kytto now leaves it alone and says so.
- **Gateway tool masking could let hidden tools through** when the server sent a
  request of its own with the same id as the client's pending `tools/list`.
  A tool call that returned `isError` is now counted as failed in Live Activity.
- **A new server reusing a deleted server's name inherited its health result and
  Contract Guard baseline** (Windows). Deleting a server now forgets them.
- **One unusual message could end a Gateway session** (Windows). A message whose
  `method` or tool name was not a string, or that repeated a key, stopped the
  relay. It is now passed through, and a repeated key is read the way the client
  and the server read it.
- **Health-checked servers inherited Kytto's open files** (macOS). A spawned
  server now gets only its standard input, output and error.

### Fixed — writing your configs

- **Editing a switched-off server switched it back on.** In Cursor, VS Code and
  Claude Desktop, saving an edit wrote the server back into the file while the
  matrix still showed it off, and switching it on later restored the old
  definition over your edit. Edits to a switched-off server now go to Kytto's
  held copy, and it stays off.
- **Restoring a backup could delete switched-off servers** (macOS). Kytto now
  reads the backup to see what it restores and leaves other held copies alone.
- **Saving a server dropped settings Kytto does not edit**, such as `headers`
  (including `Authorization`), `cwd`, `envFile`, and in Codex
  `startup_timeout_sec`, `enabled_tools`, `http_headers` or
  `bearer_token_env_var` — even when you saved without changes. Kytto now
  changes only the fields you edited, and saving without changes writes nothing.
- **Codex: editing or deleting one server could delete another** when the
  server's tables in `config.toml` were not next to each other.
- **A config file that was a symbolic link was replaced by a plain file**
  (macOS), breaking the link to a dotfiles repository. Kytto now writes through
  the link and keeps the file's permissions.
- **A server spelled differently in two clients** (`GitHub` and `github`) could
  not be switched off or deleted in one of them although Kytto reported
  success, and editing it added a duplicate. Kytto now uses each file's own
  spelling, and refuses to edit a file that names the same server twice.
- **Rotating a secret of a switched-off server** wrote an incomplete server into
  the config and switched it on. The held copy is now updated instead.
- **A switch-off that failed to write still showed the server as off** (macOS).
- **Edits made in place by another program were not noticed** (macOS), so the
  matrix went stale and the next change was refused as "changed on disk".
- **Restoring a backup overwrote changes made since Kytto last read the file**
  (macOS) without asking. It now refuses, like every other write. Edits keep a
  UTF-8 byte-order mark and match a file's Windows line endings.
- **"Restrict permissions" reported success when there was nothing to change**,
  and checking whether a config is ignored by git could freeze Kytto in a slow
  repository (macOS).

### Fixed — the window

- **"Reveal" on a stored secret did nothing** (macOS).
- **MCP Doctor, Live Activity, Profiles, MCP Library and Skills pushed their
  last items, the restart bar and the status bar out of the window** (macOS).
- **Scroll position jumped to the top** of MCP Doctor and the server detail
  after pressing a button, and whenever a client rewrote its config.
- **Pressing Esc while a sheet was saving hid its error**, and double-clicking
  Delete, Remove, Revert or a health-check button ran the action twice.
- **The diagnostic report included the server's full URL**, which for remote
  servers can carry an API key. It now shows only the scheme and host.
- **Changes made from the menu bar did not reach the open window** (macOS), the
  menu bar item could be set to a read-only source, one unfinished field
  stopped every Settings change from saving, "Open Kytto" could do nothing after
  the window was closed, and Help opened an unrelated domain.
- **Scanning a large directory and installing an update could freeze the
  window** (macOS).
- **Scanning a large directory in the MCP Library froze the window** (Windows).
  The scan now runs in the background.
- **The tray icon lost its client** (Windows) when the client chosen for it in
  Settings was later uninstalled or was a read-only source. It now falls back to
  the automatic choice.
- Smaller fixes: client names instead of ids in MCP Doctor and the MCP Library,
  the real context window instead of "of 200k", no more "3 of 2 tools" in the
  tool mask, onboarding no longer counts project scopes as clients, the matrix
  stays usable at the smallest window size, and Agent JSON import enables
  **Preview** after pasting.

### Compatibility

Kytto's measurements file (`server-metadata.json` on macOS, `metadata.json` on
Windows) gains a `lastPassingTools` field per server; older
versions ignore it. Your existing Contract Guard baselines are kept as they are,
so nothing needs to be re-acknowledged. No IPC command was added or removed.
The backup, permission and restore protections from 1.0.6.1 are retained.

### Distribution notes

The macOS build is universal and runs on Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.7.1.dmg` | 4.8 MB | `f5409e7c8209c344fd5b0981fad0e550051a97547128a94445df0c9b04cc8f7c` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.7.1.exe` | 78.4 MB | `5d69ed7216853a9fb14c4db0eaf9f7e1210e28b47cbc738b741d5ef17ae8521f` |

**Install**

- **macOS:** Open the DMG and drag KyttoMCP into Applications. Control-click
  the app and choose **Open** on first launch if Gatekeeper warns you.
- **Windows:** Run the setup file. At the SmartScreen prompt choose
  **More info** and then **Run anyway**.

---

## [1.0.7] — 2026-09-29

The first release built from the public source. MCP Doctor can now pin a
package that a server starts without an exact version, clients with a known
tool limit show how close you are to it, and Kytto no longer sends any
telemetry.

### Added

- **The source code is public.** Both apps — `macos/` (Swift) and `windows/`
  (.NET) — are now open source under the MIT License, in this repository. You
  can read exactly what Kytto does with your config files, check every network
  request it makes, and build it yourself instead of trusting an unsigned binary.
- **MCP Doctor flags unpinned package versions.** A server launched as
  `npx some-package`, `some-package@latest`, `uvx some-package` or similar starts
  whatever release is newest each time a client launches it, with your API keys
  in its environment. Doctor now warns about these, and after you check the
  latest release it offers to pin that exact version. The preview shows the
  argument before and after in every client, and the change goes through the
  usual backup and atomic write. Kytto contacts the package registry only when
  you press **Check latest release**.
- **Tool-count limit per client.** VS Code refuses a chat request with more than
  128 tools. The client screen and the matrix header now show how many tools
  the servers switched on there offer against that limit, warn when you are
  near or over it, and suggest switching servers off, using a smaller profile
  or hiding tools through the Gateway. Servers not yet health-checked are
  listed, and the count is shown as a minimum.

### Fixed

- **"Check latest" failed for packages with a long release history.** Kytto
  downloaded an npm package's full metadata, which for many packages exceeds
  the size limit Kytto enforces, and reported "The package registry response
  was unexpectedly large." It now asks npm for the latest release only.

### Removed

- **Anonymous diagnostics.** The optional *Share anonymous diagnostics* setting,
  off by default since it was introduced, is gone from both apps. Kytto now sends
  no telemetry of any kind; the only request it makes on its own is the update
  check. If you run 1.0.6.3 or earlier with the setting on, the website has
  stopped storing what it sends, so nothing is kept either way.

### Compatibility

`doctor.applyFix` accepts a new action, `pinPackageVersion`, and now reports
held copies of switched-off servers it updated (`parkedUpdated`,
`parkedFailures`). Clients gain a `toolBudget` field. Nothing was removed. The
backup, permission and restore protections from 1.0.6.1 are retained.

### Distribution notes

The macOS build is universal and runs on Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.7.dmg` | 4.6 MB | `a6df1c891665c1371499fc5c52b1de1431ae2e1c87c86e3362411492f69d20cb` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.7.exe` | 78.4 MB | `542ba475c207be303206df47d1087f93bcdee6a6f7964849c607ed77ba73cba3` |

**Install**

- **macOS:** Open the DMG and drag KyttoMCP into Applications. Control-click
  the app and choose **Open** on first launch if Gatekeeper warns you.
- **Windows:** Run the setup file. At the SmartScreen prompt choose
  **More info** and then **Run anyway**.

---

## [1.0.6.3] — 2026-09-20

A fix release. Three panels could not be scrolled, so anything below the bottom
edge of the window was unreachable; Settings also gained a way in that does not
go through the menu bar.

### Fixed

- **Scrolling in MCP Doctor, Live Activity and Profiles.** These three panels
  clipped their content instead of scrolling it, and no gesture or key reached
  what was cut off. They now scroll like the MCP Library and Skills panels
  always did.

### Added

- **Settings in the sidebar footer.** Settings was reachable only from the app
  menu and ⌘,. It is now a row in the sidebar footer, below the diagnostics
  count. The row opens the Settings window and never takes selection in the
  sidebar.

### Compatibility

One IPC command was added, `settings.open`. Nothing was removed or changed. The
backup, permission and restore protections from 1.0.6.1 are retained.

### Distribution notes

The macOS build is universal and runs on Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.6.3.dmg` | 4.3 MB | `825c194ae505160d841eaa50cce993ebacf41dbee4b334ba554a790c9a16744e` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.6.3.exe` | 78.5 MB | `d1efbf351fa00cf010588a790f9650542137177e44fda2ad9750b6d479f71bb9` |

**Install**

- **macOS:** Open the DMG and drag KyttoMCP into Applications. Control-click
  the app and choose **Open** on first launch if Gatekeeper warns you.
- **Windows:** Run the setup file. At the SmartScreen prompt choose
  **More info** and then **Run anyway**.

---

## [1.0.6.2] — 2026-09-19

A visual and onboarding release across the macOS and Windows builds. It makes
the first-run hand-off easier to understand before any configuration is changed,
and gives the matrix shell a clearer sense of movement as you navigate.

### Changed

- **Modernized first-run onboarding.** Kytto now presents the detected local
  workspace with client icons, server/client metrics and read-only custom-source
  counts before you enter the matrix.
- **Clearer safety boundary.** Onboarding explicitly says that the screen only
  reads configuration and that edits create a backup first; the discovery list
  shows each client state and path without implying that anything was written.
- **Direction-aware panel navigation.** Moving between the matrix, client details
  and utility panels now animates in the direction of the selected panel.
- **More spacious responsive UI.** The updated shell, discovery list and matrix
  styling give the interface more room while preserving the existing responsive
  layout.

### Compatibility

No IPC command, event, request field or response field was added or removed. The
release retains the backup, permission and restore protections from 1.0.6.1.

### Distribution notes

The macOS build is universal and runs on Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.6.2.dmg` | 4.5 MB | `e0f398c590beaba2a9de9caf4a94304c258eee119c9708ce9ebc4554fa3ebdfd` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.6.2.exe` | 78.5 MB | `b64b972d92db2bb59e6ae502383159bc4874b50973853a89fc1aae2e2806e1e2` |

**Install**

- **macOS:** Open the DMG and drag KyttoMCP into Applications. Control-click
  the app and choose **Open** on first launch if Gatekeeper warns you.
- **Windows:** Run the setup file. At the SmartScreen prompt choose
  **More info** and then **Run anyway**.

---

## [1.0.6.1] — 2026-09-14

Security hardening for configuration backups, following up
[#11](https://github.com/heyitsjakub/KyttoMCP/issues/11). Backups and parked
server definitions hold the same API keys as the configuration files they were
copied from, but until now they were created readable by other local accounts on
the same computer. This release locks them to your own user and limits restoring
a backup to the files Kytto actually manages for that client.

Both findings are low severity. Reading the backups took a second account on the
same computer, and redirecting a restore took write access to your own backups
folder already.

### Security

- **Backups are readable only by you.** Backup folders are now created
  owner-only, and every backup copy and the sidecar that records where it came
  from are readable and writable by your account alone. Kytto's own data folder
  is tightened the same way.
- **Parked server definitions are owner-only.** When a client that has no
  "disabled" flag switches a server off, Kytto keeps its definition — `env`
  block included — in `parked-servers.json`. That file is now owner-only too.
- **Existing backups are fixed on first launch.** Backups made by earlier
  versions are tightened once when the app starts. Nothing is created or
  deleted, symbolic links are not followed, and later launches change nothing.
- **No readable moment during a write.** The atomic writer now creates its
  temporary file owner-only from the start and applies the final permissions
  afterwards. Previously the configuration bytes were briefly readable under the
  temporary name.
- **Restore writes only to files Kytto manages.** Revert and Undo now write a
  backup back only to that client's own files: its server configuration (at the
  current path override or the default location), the Claude Code deny list, and
  Claude Desktop's extension settings. The destination always comes from Kytto's
  client registry, never from the path recorded next to the backup, so an edited
  sidecar can no longer aim a restore at another file. Any other target is
  refused with a clear message and nothing is written.

### Changed

- A backup taken from a configuration path you have since changed in Settings can
  no longer be restored. The message explains why, and the backup stays in the
  backups folder.
- A configuration file marked read-only (`0444`) can now be written by Kytto and
  keeps its permissions. Previously the write failed as a side effect of the old
  implementation.

### Compatibility

No IPC command, event, request field or response field was added or removed.
`backups.restore` can now fail with the existing error code `backup`, and when it
does it writes nothing.

### Distribution notes

The Windows build carries the same hardening, ported natively. Windows has no
Unix permission bits, so Kytto's data folder, backups and parked definitions get
a private access list instead: your account, SYSTEM and Administrators, and no
one else.

If you are on 1.0.5.3 or later for macOS, the in-app updater can install this
release. The macOS build is universal and runs on both Apple Silicon and Intel
Macs. It is ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.6.1.dmg` | 4.5 MB | `774c6af6b8eab9fd068ea647ff5e8ab33a5b44f06da92782351f517ba3286e99` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.6.1.exe` | 78.5 MB | `077f74e7a2a668af1d8af6945177bb9759026fd6dd4fb8b38ae84a21aff86568` |

---

## [1.0.6] — 2026-08-30

Reliability release. There is no new feature to look at here — every fix is about
the app telling you the truth about what it just did, and about undoing a change
it cannot finish instead of leaving half of it behind.

**If you are on 1.0.5.3 for macOS, this is the first release the in-app updater
can install for you.** Earlier versions still have to be replaced by hand.

### Fixed

- **Creating a client's first MCP file was reported as "no change".** When a
  client had no configuration file yet, the first write was judged by whether
  there had been anything to back up — and since a file that does not exist
  cannot be backed up, Kytto concluded it had written nothing. Add, Edit and
  Remove now list that client as changed, and the restart notice appears only
  after a write that really happened, so clicking the same action twice no longer
  invents a second restart. Undo on that first write still removes the server
  from the client, which is the only correct way back when there is no earlier
  file to restore.
- **Enabling a server in Claude Code could change one file and not the other.**
  Claude Code stores the server definition in `.claude.json` and its deny list in
  `.claude/settings.json`, and the toggle wrote them one after another. If the
  second write failed, the first stayed. Both writes are now one operation: on
  failure the first file is restored byte for byte, or removed if Kytto had just
  created it. The rollback re-checks the file's digest immediately beforehand, so
  it can never overwrite a newer change made outside Kytto.
- **The macOS updater announced success before it knew.** Installing an update
  asks macOS to launch the new bundle, and the installer treated the request
  being *sent* as the install being *done* — deleting the old app and the
  rollback copy while the new one might still fail to start. It now waits for
  macOS to confirm the new bundle is actually running, and a failed relaunch goes
  down the existing rollback path and brings the previous version back.
- **A damaged `profiles.json` was treated as an empty profile list, and then
  overwritten.** A missing file still means first use and is created normally,
  but a file that exists and cannot be read or parsed is no longer read as
  "no profiles". Create, Update, Delete and rename maintenance now refuse with a
  visible error and leave the original bytes untouched, and all of them go
  through one transactional read-modify-write path so two profile operations
  cannot race.
- **Launch at login could show a setting the system disagreed with.** The macOS
  login item is now registered or unregistered *before* the preference is saved.
  If the system refuses, the settings file is left exactly as it was; if saving
  the settings then fails, Kytto puts the login item back and reports both
  problems if it cannot.
- **A successful change could be reported as a failure.** After a write, the app
  reloads the Backups, Profiles and Secrets lists. One of those reloads failing
  used to surface as though the change itself had failed, inviting you to repeat
  a write that had already succeeded. The reloads are now best-effort: you get a
  warning that the list could not be refreshed, and an explicit confirmation that
  the change went through. The Add server form no longer reappears empty and
  typeless after such a failure.
- **Errors that vanished instead of being shown.** The Secrets and Activity
  panels, and the Adopt, Forget, Restrict permissions, restart
  acknowledge/dismiss and finish-onboarding actions, now display the error they
  got back rather than dropping it as an unhandled rejection.

### Compatibility

No IPC command, event, payload field or response field was added, removed or
renamed. `updates.install` is unchanged on the wire — only the native
implementation behind it now waits for the real relaunch result.

### Distribution notes

The Windows build carries the same fixes, ported natively. The relaunch fix is
macOS-only: the Windows updater hands the download to your browser rather than
installing it in-process, so it has no relaunch to get wrong.

The macOS build is universal and runs on both Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.6.dmg` | 4.5 MB | `d0332dd74e76932a55f9408a263748993275a0580081daa5cddafb9ee79e1f5e` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.6.exe` | 78.5 MB | `1d6a056c863910a061bc5514f66e3b8813cf21454205fe7099740ec7d2069ecc` |

---

## [1.0.5.3] — 2026-08-27

Repair release for the in-app updater on macOS. **There is nothing new to see in
this version** — the thing it fixes is the update mechanism itself, so its effect
only shows up the *next* time an update is offered. Install it now and the
version after this one will arrive without a manual download.

### Fixed

- **The in-app updater on macOS could never download anything.** Clicking
  *Download and verify* always ended in "The update server did not return a valid
  response", on every version that has shipped the updater. The download is
  routed through `kytto.jakubhecht.sk/download.php`, which deliberately redirects
  to the GitHub release asset, but the code checked the URL at the *end* of that
  redirect chain against Kytto's own domain — a condition the GitHub asset URL
  can never satisfy. Each redirect hop is now checked as it happens, against an
  allow-list of the site, GitHub, and GitHub's release-asset host, and the
  download stops the moment a hop leaves them. The SHA-256 from the verified
  manifest is still checked against the bytes as they arrive, as it always was.

  Nobody hit this earlier because 1.0.5, which introduced the full download and
  install flow, did not launch at all.

### Distribution notes

Because the broken updater is inside the app, **this release has to be installed
by hand**, like every one before it. That is the last time: from 1.0.5.3 onward
the updater can fetch its own updates.

The macOS build is universal and runs on both Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

The Windows app is unaffected by this defect — its updater opens the download in
the browser rather than fetching it in code, so there is no redirect chain for it
to get wrong. It is rebuilt here only to keep both platforms on one version
number.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.5.3.dmg` | 4.4 MB | `106cb906edfdc7ff1fd3c5b57eebc35b45c1291a0e87b0a1c920ae6e3ca87a99` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.5.3.exe` | 78.5 MB | `249d40273329111291c91193394aaa7011fb2e644451447df8a55ce4d5b90229` |

---

## [1.0.5.2] — 2026-08-27

Cross-platform beta update. Everything here came from one tester's notes on
1.0.5.1; the headline is that a remote server waiting for you to sign in is no
longer reported as broken.

### Added

- **A server waiting for authorization is now its own state.** A remote server
  that opens a browser and waits for OAuth consent used to time out and be
  reported as failed, which meant every OAuth-backed server showed red in the
  matrix. It is now shown in blue as waiting for authorization, with the sign-in
  URL the server itself printed and an **Open sign-in page** action. The state is
  read from the server's own output, never guessed from a URL that merely looks
  like OAuth, and provenance treats it as unchecked rather than unhealthy.
- **The sidebar can be resized.** Drag its edge, or focus the handle and use the
  arrow keys. The width is remembered.

### Changed

- Claude Code project rows now identify themselves by the part of the path that
  actually differs — `Claude Code · …/kytto` rather than a row of identical
  truncated paths. The leaf grows leftwards only when two projects would
  otherwise read the same, and the full path is still in the tooltip.
- Health failures now store the reason rather than the finished sentence, so
  improvements to error wording reach results that were recorded earlier.
  Records written before this release keep the text they were saved with.

### Fixed

- Skills whose front matter uses a YAML block scalar (`description: >-` and the
  `>`, `|`, `|-` forms) showed the marker itself instead of the description. The
  parser now reads block scalars, plain continuation lines and nested mappings,
  and no longer gives up when the file starts with a byte-order mark or a blank
  line.
- Skill cards printed their scope twice, as `Shared agent · global · Shared
  agents · global`.
- A long configuration path in a server's **Clients** list could render one
  character per line down the side of the panel.

### Distribution notes

The macOS build is universal and runs on both Apple Silicon and Intel Macs. It is
ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.5.2.dmg` | 4.4 MB | `d544479a4523bb988c5377fe5cd6ea8ef7f5e2c5c573e9321dbcfde6d9d07bd5` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.5.2.exe` | 78.5 MB | `dbcdf3270e3acdc12fc1465f325007f70919cbb0bd12411e6296da3b2fa6b479` |

---

## [1.0.5.1] — 2026-08-26

Emergency fix. **1.0.5 does not launch on macOS and has been withdrawn.** There
are no functional changes in this release; it is 1.0.5 with a corrected
signature.

### Fixed

- The macOS app crashed immediately on launch with `Library not loaded:
  @rpath/KyttoCore.framework/Versions/A/KyttoCore` and
  `(non-platform) have different Team IDs`. The release build enabled the
  hardened runtime, which enforces library validation, which requires the app
  and its embedded framework to share a Team ID. Ad-hoc signed builds have no
  Team ID, so the check could never pass and macOS refused to load the
  framework. The hardened runtime is now off on the ad-hoc build path, and the
  release script launches the built app and verifies it is still running before
  it will produce a disk image.

### Distribution notes

The macOS build is universal and runs on both Apple Silicon and Intel Macs. It
is ad-hoc signed rather than notarized, and the Windows installer is not
Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or
SmartScreen.

The Windows installer is unchanged from 1.0.5 and is unaffected by this defect.
It is republished here under the new tag with an identical checksum.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.5.1.dmg` | 4.3 MB | `7bc266509754a9974b5efdaa1566495aa643cae80d4f396ef34aea175dcf2603` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.5.exe` | 78.4 MB | `851be7f910e6d40d541d9a17e35df099f75059cfad130100602fe6163c36eae9` |

---

## [1.0.5] — 2026-08-26

**Withdrawn — this release does not launch on macOS.** The macOS app crashes at startup because the build enabled the hardened runtime, which cannot be satisfied by an ad-hoc signature. Use [1.0.5.1](#1051--2026-08-26) instead; it contains the same features. The Windows installer is unaffected.

Cross-platform beta update for macOS and Windows.

### Added

- Claude Code project and workspace entries already present in `.claude.json` are discovered as read-only sources.
- A local MCP library scans a selected directory for supported JSON, JSONC and TOML maps, then previews conflicts and warnings before importing into built-in writable clients.
- A copy-paste agent prompt and a strict JSON import flow that validates schema, fields, values, duplicates and environment-variable names without accepting secret values.
- Server provenance, version and maintenance state, distinguishing npm, Python, local package, local executable, remote and unknown sources.
- A read-only skills inventory showing known global and workspace skill roots, scope, metadata status, duplicates, conflicts and warnings.
- The in-app updater checks a fresh first-party manifest, downloads to private staging, verifies SHA-256 and version, and supports cancel, install, relaunch and rollback.

### Changed

- Pending restarts now reflect the actual difference between the applied baseline and the current configuration. Undo and restore clear stale restart state.
- Restart banners identify the affected client and the number of pending changes. Dismissing a banner hides it without discarding the pending state.
- Read-only sources and their scope stay distinct from writable client configurations throughout the UI.
- Existing backup, external-change detection, digest guards and atomic-write safeguards remain in the write path.

### Fixed

- Health-check timeout messages preserve the configured duration and no longer show misleading zero-second errors.

### Distribution notes

The macOS build is universal and runs on both Apple Silicon and Intel Macs. It is ad-hoc signed rather than notarized, and the Windows installer is not Authenticode-signed. Verify the SHA-256 checksum before bypassing Gatekeeper or SmartScreen.

**The macOS disk image was rebuilt and replaced on 26 August 2026.** The image first published under this version was produced by an unguarded build step: it was signed with a development certificate, carried the `com.apple.security.get-task-allow` entitlement, and was Apple Silicon only. None of that was intended. If you downloaded `KyttoMCP-1.0.5.dmg` before the replacement, its checksum will not match the one below — please download it again.

**Downloads**

| Platform | File | Size | SHA-256 |
|---|---|---:|---|
| macOS (universal) | `KyttoMCP-1.0.5.dmg` | 4.3 MB | `21d038b01ba1c1438b552e515fcca45a551c3e3798b632a73d3b4670d6efba1c` |
| Windows x64 | `Kytto-Setup-win-x64-1.0.5.exe` | 78.4 MB | `851be7f910e6d40d541d9a17e35df099f75059cfad130100602fe6163c36eae9` |

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

[Unreleased]: https://github.com/heyitsjakub/KyttoMCP/compare/v1.0.9...HEAD
[1.0.9]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.9
[1.0.8]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.8
[1.0.7.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.7.1
[1.0.7]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.7
[1.0.6.3]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.6.3
[1.0.6.2]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.6.2
[1.0.6.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.6.1
[1.0.6]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.6
[1.0.5.3]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5.3
[1.0.5.2]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5.2
[1.0.5.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5.1
[1.0.5]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5
[1.0.4.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.4.1
[1.0.4]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.4
[1.0.3.1]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.3.1
[1.0.3]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.3
[1.0.2-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.2-beta
[1.0.1-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.1-beta
