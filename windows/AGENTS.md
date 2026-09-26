# Repository Guidelines

## Project Structure & Architecture

Kytto MCP is a Windows control panel for MCP server configurations, ported from
the Kytto macOS app. `src/Kytto.App/` is the WPF shell: window, chrome, tray
item, settings, `WebView2`, and the IPC router. Keep configuration, discovery,
writing, health checks, client definitions and secrets in `src/Kytto.Core/`; it
must not reference WPF. `src/Kytto.Gateway/` builds `kytto-mcp-proxy.exe`, the
optional stdio gateway helper described in `docs/gateway.md`.

`web/` is a vanilla HTML/CSS/JavaScript ES-module UI with no build step,
originally ported from the macOS app's web layer. `web/js/ipc.js` is its only
native boundary; add each command to `docs/ipc.md` in the same change. Put screen
renderers in `web/js/screens/`, shared DOM helpers in `web/js/dom.js`, and styles
in `web/css/app.css`. `docs/design.md` is the product and design source of truth;
preserve its section-reference convention (`§6.3`) in code comments, and never
renumber its sections.

## Build, Test & Development

```sh
dotnet build
dotnet test
dotnet test tests/Kytto.Core.Tests --filter 'FullyQualifiedName~TomlRoundTrip'
cd web && npm test
```

`Stop-Process -Name Kytto` before rebuilding — a running app holds the exe open.
A Debug build serves `web/` from the repository, so UI edits need only a reload.

## Style & Safety Rules

Four-space C# indentation, two-space JavaScript/CSS indentation; `PascalCase`
types and members, `_camelCase` private fields, file-scoped namespaces, and files
named after their primary type. No formatter or linter is configured — match
nearby code.

Prefer expression-bodied members and `switch` expressions where they read as one
thought, and full bodies where they do not. Comments explain *why*, and cite
`docs/design.md` by section rather than restating it.

The web UI has one state object and renders from `setState()`; handlers must not
mutate or read back the DOM. Use `textContent`, never interpolated `innerHTML`.
Keep the UI platform-neutral: branch on native capabilities, never OS names or
paths.

Treat user configuration and secrets as critical data. Route edits through
`ConfigWriter`, preserve unrelated bytes and comments, back up and write
atomically, and never expose secret values to web code or logs.

**Every client icon takes up the same room.** The matrix has one column per
client and is meant to be read in two seconds; icons that disagree about how big
an icon is read as a rendering bug. Holding that is not one constant, because the
sources disagree about what "the icon" includes — a Store logo may be a glyph
meant to sit on a declared tile colour, or finished artwork on `transparent`; an
executable's icon is neither. So `ClientIcons` trims every source to its ink and
fits it to a shared footprint, and a plate fills that footprint exactly as
plateless artwork does. `ClientIconTests` measures the result rather than the
method — change how an icon is produced freely, but it still has to come out the
same size and centred.

Icons are always read from software installed on the machine, never bundled. The
macOS app does the same through `NSWorkspace`, and it keeps other companies' marks
out of the product.

Never round-trip a source file through PowerShell's `Get-Content` /
`Set-Content`: it re-encodes UTF-8 as ANSI and will silently corrupt `§`, em
dashes and the file's BOM. Use the editing tools, or `[System.IO.File]` with an
explicit `UTF8Encoding($false)`.

## Testing & Reviews

Core tests use xUnit in `tests/Kytto.Core.Tests/`, with fixtures under
`Fixtures/`. Add round-trip and preservation tests for any parser or write
change — those are what stop a bug reaching someone's config.
`tests/Kytto.App.Tests/` pins the IPC wire format, which is spelled by a naming
policy and would otherwise break silently. Web-layer tests live in
`web/js/dev/*.test.js` and run with `npm test` from `web/`.

Health-check tests need `python.exe` on PATH; they run against
`Fixtures/fake_mcp_server.py` rather than a real server.

PRs should state the affected layer, testing performed, any config-write or IPC
impact, and include screenshots for UI changes.
