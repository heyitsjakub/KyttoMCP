# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) and other coding
agents working with code in this repository. Human contributors will find it
just as useful.

## What this is

Kytto MCP for Windows — the Windows port of the Kytto macOS app. The product is
the *cross-client matrix* (which server is enabled in which client) and *token
weight* per server, not a catalog.

**`docs/design.md` is the source of truth for scope and design.** It began as the
plan shared by the macOS and Windows builds. Source comments cite it by section
(`§6.3`, `§3.2`, `§7.1`) rather than restating the reasoning, so a `§` reference
in code means "go read that section of `docs/design.md`". Keep that convention,
never renumber its sections, and update the document when a decision in it stops
being true.

`docs/ipc.md` is the web↔native contract. It was written for the macOS app first
and this port reimplements it, so it is meant to state intent that holds on both
platforms. **If Windows ever needs the web layer changed, something in that doc
described an implementation instead of an intent — that is worth finding rather
than patching (§3.2).**

## Commands

```sh
dotnet build                                  # solution is KyttoMCP.slnx
dotnet test                                   # the safety net
dotnet test tests/Kytto.Core.Tests --filter 'FullyQualifiedName~TomlRoundTrip'
cd web && npm test                            # web-layer tests (Node's built-in runner)

src/Kytto.App/bin/Debug/net10.0-windows/Kytto.exe
```

The app holds `Kytto.exe` open, so `Stop-Process -Name Kytto` before rebuilding.

In a Debug build `WebHost.WebRoot()` walks up from the binary and prefers the
repository's own `web/`, so editing CSS or JS and reloading needs no rebuild.

## Architecture

Native shell, web content. The frame is native; only what is inside the window is
HTML.

```
src/Kytto.App/     WPF + WebView2 shell. Window, chrome, tray item, settings,
                   the IPC router. Knows about WPF; holds no config logic.
src/Kytto.Core/    Everything that is not UI: parsing, the client registry,
                   discovery, the write pipeline, health checks, secrets.
                   A separate project so the risky tests run without a window,
                   and so "no WPF in here" stays mechanical rather than remembered.
src/Kytto.Gateway/ kytto-mcp-proxy.exe, the optional stdio gateway helper
                   (docs/gateway.md).
web/               Vanilla HTML/CSS/JS, ES modules, no build step. Ported from
                   the macOS app's web layer; the two have since diverged, so
                   treat this copy as its own code.
docs/design.md     Scope and design; what the `§` references in code point at.
docs/ipc.md        The web↔native contract. Every command has a row here.
```

### The IPC boundary

`src/Kytto.App/Ipc/CommandRouter.cs` is the only channel; `web/js/ipc.js` is the
only door on the other side. Rules that are load-bearing rather than stylistic:

- Commands describe **intent**, not implementation (`secrets.store`, never
  `CredWriteW`).
- The web layer never receives a path it has to interpret — display strings and
  opaque ids only. Path building, separators and `%APPDATA%` stay native.
- No OS names cross. If behaviour must differ, native reports a capability in
  `app.info` and the UI branches on the capability.
- Secrets do not cross. `env` travels as `{ key, hasValue }`; a `null` value on
  the way back means "keep what is already in the file".
- Replies are JSON **strings**, so JavaScript receives exactly what C# encoded.
- **Adding a command means adding a row to `docs/ipc.md` in the same change.**

**The wire format is spelled by a naming policy, not by hand.** DTO properties are
PascalCase and reach the wire camelCased, which reproduces what the macOS app's
Swift `JSONEncoder` emits and therefore what the web layer was written against. A
wrong key does not throw — the UI reads `undefined` and renders a blank cell — so
`tests/Kytto.App.Tests/WireFormatTests.cs` pins it. Do not weaken those.

### Two places Windows genuinely differs

Everything else is a translation. These two are decisions:

- **`web/css/app.css` declares `app-region: drag` on `.drag-strip`**, which the
  macOS build does not need. WPF's WebView2 is an `HwndHost`, so nothing WPF draws
  can sit above it — the page has to declare where the caption area is, or the
  window stops being draggable. WebKit ignores the declaration. The caption
  buttons live in a `Popup` for the same reason.
- **`npx` is not a program.** It is `npx.cmd`, and a batch file cannot be spawned
  directly, so `ManagedProcess` resolves the command through PATH + PATHEXT and
  hands scripts to `cmd /d /c`. Almost every MCP server is `npx`, so this is what
  decides whether health checks work at all.

### Writing to a user's config is the whole risk

`docs/design.md` §6 is the section that decides whether this product survives.

- **`ConfigWriter.Edit` is the only way a client config is ever modified.** It
  checks the file's digest against what Kytto last read (refusing rather than
  merging if someone else touched it), backs up, applies the transform, re-parses
  the result, and writes atomically.
- **Edits are splices, never regeneration.** `JsonDocument` and `TomlDocument`
  keep the original text plus byte spans for everything in it. An edit replaces
  one range. Comments, key order, blank lines and keys Kytto has never heard of
  survive because they are never touched. *If a change cannot be expressed as
  span replacements, it does not get written.*
- **Atomic means same-directory.** `AtomicWriter` writes a temp file beside the
  target, flushes to the device, then `File.Replace`s — not `Move`, because
  Replace keeps the destination's ACLs, and restricting a config's permissions is
  something Kytto offers to do.
- No client config is written on launch. The only thing launch may change is the
  permissions on Kytto's own storage (§6 rule 6). Health checks spawn processes
  only on request.

The tests asserting "splicing a value over itself changes nothing" are the ones
protecting this. Do not weaken them.

### Clients are data, not code

`src/Kytto.Core/Clients/ClientRegistry.cs` holds one descriptor per client and is
the only place a config path, servers key, schema quirk or format is written down.
Adding or fixing a client should be a one-file change. Each descriptor carries the
macOS path as well as the Windows one: the registry was ported from the macOS
app's, and keeping both keeps the descriptors platform-neutral (§3.2).

Each client answers "how do I disable a server?" differently, and that difference
is the substance of the matrix (`EnablementStrategy`):

| Client | Format | Off switch |
|---|---|---|
| Claude Desktop | JSON | presence (+ a boolean per extension bundle) |
| Claude Code | JSON | a deny list in a *different* file |
| Cursor, VS Code | JSON | presence only |
| Codex | **TOML** | `enabled = false` on the server's own table |

"Presence only" means switching off has to remove the definition — which is only
acceptable because `ParkStore` keeps the exact bytes first and restores them byte
for byte. The UI must say so when it happens.

**Definitions do not travel as text between formats.** Copying a server into a
client whose format differs renders it from the model instead. The bug this guards
against is a TOML table spliced into a JSON file.

### The web layer

Read `web/README.md` — it carries the house rules and they are enforced by review,
not by tooling. The short version: one state object, one `setState`, one render;
never mutate the DOM from a handler; never read state back out of the DOM; never
`innerHTML` with data from a config file; one delegated listener per event type.

`web/dev.html` runs the entire UI against `js/dev/fixtures.js` with no native code.
**Anything that works in `index.html` but not in `dev.html` has reached around
`ipc.js`, and that is a bug.**

## Verifying UI work

Synthetic mouse clicks do not reach the WebView2 renderer — moves arrive and
`:hover` responds, but clicks are dropped, so a click harness silently does
nothing. Drive the UI from the shell instead (Debug builds only):

```powershell
$env:KYTTO_PROBE = '[data-action="check-all"]'   # CSS selectors to click, `;` between
Start-Process src/Kytto.App/bin/Debug/net10.0-windows/Kytto.exe
```

`MainWindow.RunProbeAsync` clicks each via `ExecuteScriptAsync` once the page has
rendered, pausing between them so a re-render lands first — most things worth
checking need two clicks, because the screen holding the control has to be reached
before the control exists. `%APPDATA%\Kytto\kytto.log` records every command
dispatched, every handler exception, and the page's own `error` /
`unhandledrejection` events — silence there means the JS handler never ran.

Screenshots must enumerate top-level windows for the pid and take the largest
visible one: the caption buttons are a `Popup`, which is its own HWND and which
Windows will happily report as `MainWindowHandle`.

## Platform

Windows 10/11, .NET 10, WPF + WebView2. WPF comes with the .NET SDK and WebView2
with a NuGet package — no workload, no WindowsAppSDK.

Not shipping via the Microsoft Store for the same reason as the Mac App Store: the
sandbox forbids reading and writing other applications' config files, which is the
entire product.
