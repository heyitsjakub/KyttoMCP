# Contributing to Kytto MCP

Kytto MCP is open source under the [MIT License](LICENSE), and issues and pull
requests are welcome. A bug report with clear steps to reproduce it is as useful
as code.

## Where things live

| Folder | App | Read first |
|---|---|---|
| [`macos/`](macos) | Swift 6, SwiftUI/AppKit shell + WKWebView, `KyttoCore` package | [macos/README.md](macos/README.md), [macos/CLAUDE.md](macos/CLAUDE.md) |
| [`windows/`](windows) | .NET 10, WPF shell + WebView2, `Kytto.Core` | [windows/README.md](windows/README.md), [windows/CLAUDE.md](windows/CLAUDE.md) |

Each folder builds and tests on its own. Run commands from inside it:

```sh
cd macos/KyttoCore && swift test     # macOS, Xcode 16+
cd windows && dotnet test            # Windows, .NET 10 SDK
```

The two web layers share an origin but have diverged, so a UI change on one
platform does not carry over to the other automatically.

## House rules

- **Writes are the whole risk of this app.** Every change to a client's config
  goes through the one write pipeline: digest check, backup, span-level edit that
  preserves comments and key order, atomic write. Never regenerate a file. Parser
  or writer changes need round-trip tests.
- **Adding an IPC command** means documenting it in that platform's
  `docs/ipc.md` in the same change.
- **No telemetry.** Kytto makes no network request of its own beyond the update
  check and lookups the user explicitly asks for. Keep it that way.
- **This repository is public.** Never commit secrets, signing material, real user
  configs or personal paths; fixtures use placeholders such as `/Users/example`.
- **User-visible changes** go under `## [Unreleased]` in
  [CHANGELOG.md](CHANGELOG.md), written from the user's point of view.
- Commit messages are in English.

## Reporting bugs

[Open an issue](https://github.com/heyitsjakub/KyttoMCP/issues/new/choose) or use
the [form on the website](https://kytto.jakubhecht.sk/#report-bug). Remove API
keys, tokens and private paths from anything you paste.

If you find a security problem — for example a way to make Kytto write outside the
file it should, or to leak a secret — please email studio@jakubhecht.sk instead of
opening a public issue.
