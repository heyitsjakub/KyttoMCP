# Repository Guidelines

## Project Structure & Architecture

Kytto MCP is a macOS control panel for MCP server configurations. `KyttoMCP/` is the SwiftUI/AppKit shell: menus, window chrome, `WKWebView`, and the IPC router. Keep configuration, discovery, writing, health checks, client definitions, and secrets in the SwiftPM library under `KyttoCore/Sources/KyttoCore/`; it must remain independent of AppKit.

`web/` is a vanilla HTML/CSS/JavaScript ES-module UI with no build step. `web/js/ipc.js` is its only native boundary; add each command to `docs/ipc.md` in the same change. Put screen renderers in `web/js/screens/`, shared DOM helpers in `web/js/dom.js`, and styles in `web/css/app.css`. `README.md` is the product and design source of truth; preserve its section-reference convention (`§6.3`) in code comments.

## Build, Test & Development

Run the fast core loop from the package directory:

```sh
cd KyttoCore && swift build
cd KyttoCore && swift test
cd KyttoCore && swift test --filter TOMLRoundTripTests
```

Build the macOS app with:

```sh
xcodebuild -project KyttoMCP.xcodeproj -scheme KyttoMCP \
  -configuration Debug -destination 'platform=macOS' build
```

For UI-only work, use `cd web && python3 -m http.server 8765`, then open `http://localhost:8765/dev.html`. Do not use `file://`: ES modules require the static server. The Xcode project treats `web/` as a folder reference, so new web files bundle automatically.

## Style & Safety Rules

Follow the existing style: four-space Swift indentation; two-space JavaScript/CSS indentation; `PascalCase` types, `camelCase` members, and focused files named after their primary type or screen. No formatter or linter is configured—match nearby code.

The web UI has one state object and renders from `setState()`; handlers must not mutate or read back the DOM. Use `textContent`, never interpolated `innerHTML`. Keep the UI platform-neutral: branch on native capabilities, never OS names or paths.

Treat user configuration and secrets as critical data. Route edits through `ConfigWriter`, preserve unrelated bytes/comments, back up and atomically write, and never expose secret values to web code or logs.

## Testing & Reviews

Core tests use Swift Testing (`@Suite`, `@Test`, `#expect`) in `KyttoCore/Tests/KyttoCoreTests/`, with fixtures under `Fixtures/`. Add round-trip and preservation tests for parser/write changes. App tests use Swift Testing in `KyttoMCPTests/`; UI automation uses XCTest in `KyttoMCPUITests/`.

Use concise, imperative, English commit subjects. PRs should state the affected layer, testing performed, any config-write or IPC impact, and include screenshots for UI changes.
