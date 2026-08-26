# KyttoMCP

**A local control panel for MCP servers across the AI clients you already use.**

[![Version](https://img.shields.io/badge/beta-1.0.5-blue)](https://github.com/heyitsjakub/KyttoMCP/releases)
[![Platforms](https://img.shields.io/badge/platform-macOS%20%7C%20Windows-lightgrey)](https://kytto.jakubhecht.sk/)
[![Price](https://img.shields.io/badge/beta-free-brightgreen)](https://kytto.jakubhecht.sk/)

[**Download Beta 1.0.5**](https://kytto.jakubhecht.sk/) · [Website](https://kytto.jakubhecht.sk/) · [Changelog](CHANGELOG.md) · [Report a bug](https://kytto.jakubhecht.sk/#report-bug)

---

![KyttoMCP showing MCP servers and their status across Claude Desktop, Claude Code, Cursor, VS Code and Codex in one matrix](docs/screenshot.png)

### See it work — 72 seconds, no narration

https://github.com/user-attachments/assets/e8e91309-8f4a-4ba5-9a1d-ac306b51d664

Eleven MCP servers across four clients, one of them refusing to start. Kytto switches a server on for Claude Code, shows the stderr behind the failing one, and measures what each server costs in context. Recorded unedited on Windows with Beta 1.0.2 — [also on the site](https://kytto.jakubhecht.sk/#demo).

## The problem

Once you run more than two or three MCP servers, the configuration is spread across files that share nothing but the idea:

```
~/.claude.json                 Claude Desktop / Claude Code
~/.cursor/mcp.json             Cursor
settings.json                  VS Code
config.toml                    Codex
```

Different formats, different locations, different rules about what a valid entry looks like. And the question that keeps coming back has no single place to answer it: **what is actually enabled, where?**

## What Kytto does

Kytto reads those files locally and turns them into one workspace.

**See and change what is enabled**
Every server against every client in one matrix. Toggle a server on or off, edit a definition across every client that uses it, or remove it from one client without touching the others — Kytto writes each client in that client's own format and respects its rules. Add servers by hand or from a built-in catalog. In 1.0.5, Kytto explains the first matrix write before it happens and offers Undo after successful changes, including exact backup restoration for a server that was previously absent.

**Attach custom sources without writing them**
Attach multiple named JSON, JSONC or TOML configuration files as read-only custom sources with Global, Profile or Workspace display scope. Kytto discovers and watches their servers without rewriting, creating or deleting the selected files, and keeps them distinct from the five built-in writable clients.

**Find out why a server fails**
Run an on-demand MCP handshake and read what the server actually reports: tools, prompts, resources, protocol details and stderr. MCP Doctor turns that evidence into concrete findings — missing environment values, relative paths, failed handshakes, executables that only resolve inside one client's PATH — and previews any fix before writing it.

**Know what a server costs you**
Tool definitions consume the context window before you type anything. Kytto estimates the context cost of each server and each profile, and flags heavy servers, over-budget stacks, unmeasured entries and duplicate tools that can confuse selection.

**Switch between setups**
Save local profiles such as Coding, Research or Minimal. Preview exactly what a switch will change, then apply it to one client with the normal backup and restart safeguards.

**Handle secrets without surprises**
Find sensitive environment values while keeping them masked by default. Reveal on request, rotate one value everywhere it appears, keep an optional Keychain copy, or tighten config file permissions.

**Undo anything**
Every write checks whether the file changed outside Kytto, makes a timestamped backup, and writes atomically. Browse those backups in the app and restore an earlier configuration — the version being replaced is backed up too.

**Optional Gateway mode**
For supported stdio servers, preview a local Gateway route, move the direct definition and credentials into native storage, and restore Direct mode with one click. Live Activity shows sessions, tool calls, latency and failures as metadata only — arguments, results and payloads stay out of the log.

## Supported clients

| Client | Status |
|---|---|
| Claude Desktop | Supported |
| Claude Code | Supported |
| Cursor | Supported |
| VS Code | Supported |
| Codex | Supported |

Custom sources are read-only and are not an additional writable client. Want another built-in client supported? [Open an issue](https://github.com/heyitsjakub/KyttoMCP/issues) — the roadmap is driven by what people actually ask for.

## Install

Download Beta 1.0.5 from [kytto.jakubhecht.sk](https://kytto.jakubhecht.sk/) or from the [v1.0.5 Release](https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.5).

### macOS

Apple Silicon only, M1 and later. Intel Macs are not supported from 1.0.5 onwards — 1.0.4.1 and earlier carry a universal build. Open the DMG and drag KyttoMCP into Applications.

The beta is **not yet signed with a paid Apple Developer certificate**, so Gatekeeper will show an "unidentified developer" warning. Control-click the app and choose **Open**, then **Open** again. If macOS still blocks it, go to System Settings → Privacy & Security → **Open Anyway**.

> Signing and notarization are planned. Until then, only open a build you downloaded from the official site or this repository's Releases page, and verify the checksum below.

### Windows

Run the setup file. Microsoft Defender SmartScreen will appear for the same reason — choose **More info** → **Run anyway**.

### Verify your download

```sh
# macOS
shasum -a 256 KyttoMCP-1.0.5.dmg
# ad9d5dcbc0646697c965ef9d0bd493e6249d59650a48b2ff424e6c3c7f288369

# Windows (PowerShell)
Get-FileHash Kytto-Setup-win-x64-1.0.5.exe -Algorithm SHA256
# 851be7f910e6d40d541d9a17e35df099f75059cfad130100602fe6163c36eae9
```

## Local by design

Kytto works with the files already on your computer.

- No account, no sign-up, no cloud sync
- MCP configurations and credentials never leave your machine
- Backups and configuration changes are handled locally
- Anonymous diagnostics are **off by default**, and the payload is deliberately incapable of carrying configuration, file paths or error text even when enabled
- Gateway activity is metadata-only; payload capture is off

See the [privacy policy](https://kytto.jakubhecht.sk/privacy.html) for exactly what is collected and how long it is kept.

## About the source

The Kytto app is currently **closed source**. It seems fair to say that plainly rather than let you find out by clicking around.

This is a solo project and I haven't settled the model yet. What I can commit to now:

- The beta is free, and I will not retroactively paywall a build someone already installed.
- The parts people reasonably worry about — what Kytto reads, what it writes, and whether anything leaves the machine — are documented in the privacy policy, and diagnostics are opt-in rather than opt-out.
- If there is interest in open-sourcing the configuration read/write layer specifically, I am open to it. That is where the trust question actually lives. [Say so in an issue](https://github.com/heyitsjakub/KyttoMCP/issues) if you care about it.

This repository is the home for releases, the changelog, the roadmap and bug reports.

## What Kytto is not

- **Not an MCP server** and not a server registry. It manages the client-side configuration of servers you already run.
- **Not a cloud service.** There is nothing to sign into.
- **Not a replacement for your client.** It configures Claude Desktop, Cursor and the rest — it does not replace them.

## Roadmap

Tracked in [Issues](https://github.com/heyitsjakub/KyttoMCP/issues). Priorities come from what people ask for, so an issue with a real use case in it carries more weight than a vote.

## Feedback

The beta needs honest criticism far more than it needs praise. If something is confusing, broken, or simply not worth keeping, that is the useful report.

- [Open an issue](https://github.com/heyitsjakub/KyttoMCP/issues)
- [Report a bug on the site](https://kytto.jakubhecht.sk/#report-bug)
- Email: studio@jakubhecht.sk

## License

Proprietary. Free to use during the 1.0.5 beta.

---

Made by [Jakub Hecht](https://kytto.jakubhecht.sk/) · Jakub Studio

*Not affiliated with Anthropic, Cursor, Microsoft or OpenAI. Claude, Cursor, VS Code and Codex are trademarks of their respective owners.*
