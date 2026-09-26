# CLAUDE.md

Kytto MCP is two native apps in one repository, each a native shell around a
web UI:

| Folder | App | Guide |
|---|---|---|
| `macos/` | Swift 6, SwiftUI/AppKit + WKWebView, `KyttoCore` SwiftPM package | [macos/CLAUDE.md](macos/CLAUDE.md) |
| `windows/` | .NET 10, WPF + WebView2, `Kytto.Core` | [windows/CLAUDE.md](windows/CLAUDE.md) |

Read the guide for the folder you are working in before changing anything, and
run its commands from inside that folder. Each platform has its own `web/`,
`docs/ipc.md` and design document; the two web layers share an origin but have
diverged, so a UI change on one platform does not carry over automatically.

The root holds only the public face of the project: `README.md` (for users),
`CHANGELOG.md` (user-facing release notes, Keep a Changelog format), `LICENSE`
and `.github/`. Record user-visible changes under `## [Unreleased]` in
`CHANGELOG.md`.

This is a public repository. Never commit secrets, signing material, real user
configs or personal paths; fixtures use placeholders such as `/Users/example`.
Commit messages are in English.
