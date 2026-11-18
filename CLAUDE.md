# CLAUDE.md

Kytto MCP is two native apps in one repository, each a native shell around the
same web UI:

| Folder | App | Guide |
|---|---|---|
| `macos/` | Swift 6, SwiftUI/AppKit + WKWebView, `KyttoCore` SwiftPM package | [macos/CLAUDE.md](macos/CLAUDE.md) |
| `windows/` | .NET 10, WPF + WebView2, `Kytto.Core` | [windows/CLAUDE.md](windows/CLAUDE.md) |
| `web/` | The UI both apps load: vanilla HTML/CSS/JS, no build step | [web/README.md](web/README.md) |

Read the guide for the folder you are working in before changing anything, and
run its commands from inside that folder. The web UI in `web/` is **one
codebase shared by both apps**: a UI change reaches macOS and Windows together,
so it has to work against both native backends (run `cd web && npm test`, and
look at it in the app you can run). Each platform keeps its own `docs/ipc.md`
and design document. Where the two backends answer a command in a different
shape, `web/js/compat.js` turns the reply into the shape the screens read; the
web code never branches on an OS name.

To look at or click through the app or its web harness, use the cua
computer-use tools (`mcp__cua-computer-use__*`), not screenshots taken from
the terminal (headless browser `--screenshot`, `screencapture`).

The root holds only the public face of the project: `README.md` (for users),
`CHANGELOG.md` (user-facing release notes, Keep a Changelog format), `LICENSE`
and `.github/`. Record user-visible changes under `## [Unreleased]` in
`CHANGELOG.md`.

This is a public repository. Never commit secrets, signing material, real user
configs or personal paths; fixtures use placeholders such as `/Users/example`.
Commit messages are in English.
