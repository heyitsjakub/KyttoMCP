# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kytto MCP — a macOS control panel for MCP server configuration that is otherwise
scattered across several clients' config files. The product is the *cross-client
matrix* (which server is enabled in which client) and *token weight* per server,
not a catalog.

**`README.md` is the source of truth for scope and design.** Source comments cite
it by section (`§6.3`, `§3.2`, `§7.1`) rather than restating the reasoning, so a
`§` reference in code means "go read that section". Keep that convention when
adding code, and update `README.md` when a decision in it stops being true.

## Commands

```sh
# Core logic: builds and tests without launching the app. Use this loop.
cd KyttoCore && swift build
cd KyttoCore && swift test

# One suite. NOTE: --filter matches the *type* name, not the @Suite display
# string, so `--filter "TOMLDocument — round trips"` silently runs nothing.
cd KyttoCore && swift test --filter TOMLRoundTripTests

# The app.
xcodebuild -project KyttoMCP.xcodeproj -scheme KyttoMCP -configuration Debug \
  -destination 'platform=macOS' build

# The web UI against fixtures, with no native code at all.
cd web && python3 -m http.server 8765   # then open http://localhost:8765/dev.html
```

A static server is required for `dev.html`: browsers treat every `file://`
document as an opaque origin and refuse to load ES modules across one.

`web/` is a **folder reference** in the Xcode project, so new files under it are
bundled automatically — no project edit needed.

## Architecture

Native shell, web content. The frame is native; only what is inside the window is
HTML.

```
KyttoMCP/          AppKit + SwiftUI shell. Window, menus, menu bar item,
                   WKWebView, the IPC router. Knows about AppKit; holds no
                   config logic.
KyttoCore/         Everything that is not UI: parsing, the client registry,
                   discovery, the write pipeline, health checks, secrets.
                   A separate SwiftPM package so the risky tests run fast.
web/               Vanilla HTML/CSS/JS, ES modules, no build step.
docs/ipc.md        The web↔native contract. Every command has a row here.
```

### The IPC boundary

`KyttoMCP/IPC/CommandRouter.swift` is the only channel. `web/js/ipc.js` is the
only door on the other side. Rules that are load-bearing rather than stylistic:

- Commands describe **intent**, not implementation (`secrets.store`, never
  `keychain.addGenericPassword`).
- The web layer never receives a path it has to interpret — display strings and
  opaque ids only. Path building, separators and `%APPDATA%` stay native.
- No OS names cross. If behaviour must differ, native reports a capability in
  `app.info` and the UI branches on the capability.
- Secrets do not cross. `env` travels as `{ key, hasValue }`; a `null` value on
  the way back means "keep what is already in the file".
- Replies are JSON **strings**, so JavaScript receives exactly what Swift encoded.
- **Adding a command means adding a row to `docs/ipc.md` in the same change.**

Two things cross that are not commands, and are documented there too: client app
icons served at `/icon/<clientID>.png`, and window geometry (`--titlebar-height`,
`--traffic-light-inset`, `--chrome-alpha`) written onto the document root as CSS
custom properties by `ChromeView`. The page positions against those variables and
never learns what produced them — that is what keeps the layout portable.

### Writing to a user's config is the whole risk

`README.md` §6 is the section that decides whether this product survives. The
design that enforces it:

- **`ConfigWriter.edit` is the only way a client config is ever modified.** It
  checks the file's digest against what Kytto last read (refusing rather than
  merging if someone else touched it), backs up, applies the transform, re-parses
  the result, and writes atomically.
- **Edits are splices, never regeneration.** `JSONDocument` and `TOMLDocument`
  keep the original text plus byte spans for everything in it. An edit replaces
  one range. Comments, key order, blank lines and keys Kytto has never heard of
  survive because they are never touched. *If a change cannot be expressed as
  span replacements, it does not get written.*
- Nothing is written on launch. Health checks spawn processes only on request.

The tests in `JSONDocumentTests` / `TOMLDocumentTests` that assert
"splicing a value over itself changes nothing" are the ones protecting this. Do
not weaken them.

### Clients are data, not code

`KyttoCore/Clients/ClientRegistry.swift` holds one descriptor per client and is
the only place a config path, servers key, schema quirk or format is written
down. Adding or fixing a client should be a one-file change.

Each client answers "how do I disable a server?" differently, and that difference
is the substance of the matrix (`EnablementStrategy`):

| Client | Format | Off switch |
|---|---|---|
| Claude Desktop | JSON | presence (+ a boolean per extension bundle) |
| Claude Code | JSON | a deny list in a *different* file |
| Cursor, VS Code | JSON | presence only |
| Codex | **TOML** | `enabled = false` on the server's own table |
| Antigravity, Devin Desktop | JSON | `"disabled": true` on the entry |
| OpenCode | JSON, own entry shape (`ServerSchema.openCode`) | `"enabled": false` on the entry |

"Presence only" means switching off has to remove the definition — which is only
acceptable because `ParkStore` keeps the exact bytes first and restores them
byte for byte. The UI must say so when it happens.

A client can have more than one source. Claude Desktop and Codex both have a
second, **read-only** one — extension bundles and plugins respectively — because
those are installed software in directories the client rewrites.

**Definitions do not travel as text between formats** — nor between JSON
clients whose `ServerSchema` differs, unless `ServerSchema.translating` can
respell the keys they disagree on. Copying a server into a
client whose format differs renders it from the model instead. The bug this
guards against is a TOML table spliced into a JSON file.

### The web layer

Read `web/README.md` — it carries the house rules and they are enforced by
review, not by tooling. The short version: one state object, one `setState`, one
render; never mutate the DOM from a handler; never read state back out of the
DOM; never `innerHTML` with data from a config file; one delegated listener per
event type.

`web/dev.html` runs the entire UI against `js/dev/fixtures.js` with no native
code. It stands in for the throwaway Windows shell §3.2 asks for. **Anything that
works in `index.html` but not in `dev.html` has reached around `ipc.js`, and that
is a bug.** Keep the fixtures honest — they model states that actually occur
(a leftover config with no client, a server denied in one client, a client
installed but never configured).

Styling lives in one stylesheet. Colour tokens are written once as `light-dark()`
pairs; forcing a theme sets `color-scheme`, it does not restate the palette.
Colour only ever carries meaning — green running, red failed, grey unchecked,
amber consuming context, blue waiting for the user's sign-in — and animation
only ever confirms that something happened.

## Verifying UI work

Look at the running UI through the **cua computer-use tools**
(`mcp__cua-computer-use__*`), not through terminal screenshots (headless
Chrome/Brave `--screenshot`, `screencapture`). cua shows the real window, reads
its accessibility tree and can click through a flow in the background:

- **The app:** build the Debug app, launch it with `launch_app`, then
  `get_window_state` for the screenshot and the tree, and `run_steps` to click
  through a flow. Point it at an isolated home (`KYTTO_TEST_HOME=<scratch dir>`)
  whenever a step could write, so no real client config is touched.
- **The web harness:** `cd web && python3 -m http.server 8765`, open
  `http://localhost:8765/dev.html` in a browser with `launch_app` (`urls`), and
  read and press its buttons through the accessibility tree
  (`get_window_state` with `query`, then `run_steps` with the element token).
  To show a state the fixtures do not start in, serve a scratch copy of `web/`
  with an edited `js/dev/fixtures.js`, never the repo copy.

The harness cannot show vibrancy, the native title bar or real application
icons. Check those in the app.

## Platform

macOS 15+, Swift 6. The Windows app lives in `../windows/`; §3.2 documents the
platform boundaries that keep both native shells aligned: intent-based IPC,
per-platform registry paths and no OS path handling in web code.

Not shipping via the Mac App Store: the sandbox forbids reading and writing other
applications' config files, which is the entire product.
