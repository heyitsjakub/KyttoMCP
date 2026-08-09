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

In development. Planned work is tracked in [Issues](https://github.com/heyitsjakub/KyttoMCP/issues).

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

[Unreleased]: https://github.com/heyitsjakub/KyttoMCP/compare/v1.0.1-beta...HEAD
[1.0.1-beta]: https://github.com/heyitsjakub/KyttoMCP/releases/tag/v1.0.1-beta
