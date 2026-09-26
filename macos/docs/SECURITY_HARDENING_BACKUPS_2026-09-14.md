# Security hardening: config backups — 14 Sep 2026

Follow-up to GitHub issue
[heyitsjakub/KyttoMCP#11](https://github.com/heyitsjakub/KyttoMCP/issues/11)
("Consider hardening local credential storage & subprocess argument handling",
closed as not planned the same day with a reply).

The issue named no concrete bug. It asked two generic questions, and both were
checked in the source before replying:

1. **Are credentials stored encrypted?** Kytto's own copy of a secret lives in the
   macOS Keychain, `kSecAttrAccessibleWhenUnlockedThisDeviceOnly`, never synced
   (`KyttoCore/Sources/KyttoCore/Secrets/SecretStore.swift`). The plaintext
   values in client config files are the clients' format, not Kytto's choice.
2. **Can profile or backup data inject into process launch?** No shell is
   involved. Servers start via `posix_spawn` with an explicit argv array
   (`Health/ManagedProcess.swift:110-123`, `Gateway/GatewayManagedProcess.swift:78`).

Checking point 1 turned up one real, low-severity gap that the reply did not
mention: **config backups loosen file permissions and carry every secret in
plaintext.**

## Finding 1 — backups are written world-readable (verified)

`BackupStore.backUp` (`KyttoCore/Sources/KyttoCore/Write/BackupStore.swift`)
copies the whole config before every mutation, keeping up to
`defaultRetentionPerClient = 20` copies per client under
`~/Library/Application Support/Kytto/Backups/<client>/`.

- `:44` creates the directory with default attributes, so it ends up `0755`.
- `:58` writes the `.bak` with `Data.write(options: .atomic)`, so it ends up `0644`.
- `:64` writes the `.origin` sidecar the same way, also `0644`.

Observed on disk, 14 Sep 2026:

```
drwxr-xr-x  …/Kytto
drwxr-xr-x  …/Kytto/backups
-rw-r--r--  …/backups/claudeCode/settings.json.2026-07-31-113227-363.bak
-rw-------  ~/.claude.json           ← the original is 0600
```

So a backup of a `0600` config such as `~/.claude.json`, which holds the `env`
blocks with API keys, comes out `0644`. `AtomicWriter.write` already preserves
permissions when *writing a config* (`Write/AtomicWriter.swift:42-47`, "so a
config the user had locked down does not come back world-readable"). The backup
path skips that, which contradicts the intent stated there.

**Practical impact is low.** `~/Library` and `~/Library/Application Support` are
`0700` on a default install, so another local user cannot traverse to the files.
The exposure is limited to setups where those parent permissions were changed,
and to tools that copy the Kytto folder elsewhere (sync, archives, support
bundles) while keeping the mode bits.

### Fix

- Create `paths.backups` and each per-client directory with
  `[.posixPermissions: 0o700]`. Also tighten the existing
  `~/Library/Application Support/Kytto` root, which is `0755`.
- Write `.bak` and `.origin` files as `0o600`. Either set attributes on the
  destination after writing, or write through a temp file that already has the
  mode, as `AtomicWriter` does, so the file is never briefly `0644`.
- One-off migration on launch: `chmod` existing directories to `0700` and files
  to `0600`. Backups taken since July are all `0644` today.
- Test: back up a `0600` fixture, then assert the `.bak`, the `.origin` and the
  directory modes.

## Finding 2 — restore trusts the `.origin` sidecar path (verified, hardening only)

`BackupStore.restore` (`:125-129`) writes the backup's contents to
`backup.originalURL`, which is read from the `.origin` sidecar. `originPath(for:)`
(`:156`) only checks that the path is absolute and has a last component. A
tampered sidecar could point a restore at any file the user can write.

This is **not an escalation**. Tampering requires write access to the user's own
Application Support folder, and an attacker who has that already owns the
account. It is still cheap defence in depth.

### Fix

- In `restore`, require that `originalURL` matches a config path the
  `ClientRegistry` currently knows for `backup.clientID`, including custom
  sources and Claude Code project scopes. Otherwise refuse the restore with a
  clear error.
- Test: a sidecar pointing outside the client's known paths is rejected, and a
  legitimate restore still works.

## Not changing

- **Plaintext secrets in client configs:** the clients read literal values, and
  `SecretStore.swift` already documents why pretending otherwise would be theatre.
- **Backups containing secrets:** a restore has to reproduce the exact file, so
  redacting backups would break the feature. Tight permissions are the right
  lever here, not redaction.

## Priority

Low. Neither finding blocks a release. Finding 1 is a small change and fits into
the next patch release. Finding 2 can ride along, or wait.

## Status — implemented 14 Sep 2026 (macOS)

Both findings are fixed. Where the implementation differs from the plan above, the
reason is below.

### Finding 1

- `KyttoPaths.createPrivateDirectory` creates `Backups/<client>` as `0700` and
  removes group/other access from every existing folder up to the Kytto root.
  `BackupStore.backUp` writes the `.bak` and `.origin` through `AtomicWriter` with
  `permissions: 0o600`.
- **`AtomicWriter` needed a fix too.** The plan assumed it already staged through a
  temp file that has the mode from the start. It did not: the temp file was written
  at the default `0644` and chmodded afterwards. It is now created with
  `open(O_CREAT | O_EXCL, 0600)`, and only then given its final mode (explicit, or
  carried over from the replaced file). This also closes the same short window for
  config writes such as `~/.claude.json`, which is staged in `~`, a folder that
  other `staff` accounts can list.
- **Also covered: `parked-servers.json`.** It stores verbatim server definitions,
  `env` values included, so it had the same gap. It is now `0600`, and the Kytto
  root is `0700`. `Gateway/routes.json` now gets its `0600` while staged instead of
  after the rename.
- Launch migration: `KyttoPaths.tightenPermissions()`, called from
  `KyttoMCPApp.init`. It covers the root, the whole `Backups` tree and the parked
  store, and only ever removes group/other bits. It skips symbolic links and
  creates nothing. It runs on every launch, with no marker file; once the folder is
  private it changes nothing.

### Finding 2

- `ClientPathResolver.writeTarget(matching:for:)` derives the restorable files from
  the registry:
  - the server map, at its current override and at its registry default;
  - the Claude Code deny list;
  - `*.json` directly inside Claude Desktop's extension settings folder.
- `BackupStore.restore(_:home:pathOverrides:)` refuses anything else with
  `BackupError.unknownRestoreTarget`. Over IPC this is error code `backup`.
- **The restore writes the registry path, not the path spelled in the sidecar.**
  `standardizedFileURL` resolves `..` lexically, so a sidecar containing
  `~/link/../.cursor/mcp.json` passes the comparison but follows `link` on disk. A
  mutation test confirmed that writing to the sidecar path would be a real bypass.
- Custom sources and Claude Code project scopes are read-only and never written,
  so they are not restore targets. Project scopes live inside `~/.claude.json`,
  which is covered as Claude Code's server map. A backup filed under a custom
  source can only have been made by hand, and it is refused.
- Behaviour change: a backup taken from a path override that has since been
  changed is refused. The message says so, and the backup stays in the folder.

### Tests

`KyttoCore/Tests/KyttoCoreTests/BackupHardeningTests.swift` adds 14 tests in three
suites: modes, the migration, and restore targets (the edited-sidecar cases, the
`link/..` case, every legitimate target, overrides, read-only sources). Each fix
was reverted in a scratch copy to check that its tests fail. `swift test`: 345
passing. `KyttoMCPTests` passes.

### Not done

The Windows port has its own backup store. It needs the equivalent: owner-only
ACLs and the same restore-target check (see `docs/FUTURE_MCP_ROADMAP.md` §15).
Shipped on macOS in 1.0.6.1.
