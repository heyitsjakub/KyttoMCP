# Kytto Gateway

The gateway is the first control-plane building block (§3.3 and §8 of
[`design.md`](design.md)). G1 connects the tested stdio relay to an explicit,
reversible app workflow; Direct mode remains the default and recovery path.

The helper is `kytto-mcp-proxy.exe`, built from `src/Kytto.Gateway/` and copied
next to `Kytto.exe` by the app's build. The macOS app ships an equivalent
`kytto-mcp-proxy` inside its bundle.

## Preview command

After `dotnet build`, run the helper by hand against any stdio server:

```powershell
src\Kytto.App\bin\Debug\net10.0-windows\kytto-mcp-proxy.exe `
  --server-id github `
  --client-id cursor `
  --event-log "$env:TEMP\kytto-gateway-events.jsonl" `
  -- npx -y @example/github-mcp-server
```

Everything after `--` is the real stdio server command. No shell interprets it.
The proxy resolves the executable the same way health checks do — PATH plus
PATHEXT, with batch shims such as `npx.cmd` handed to `cmd /d /c` — launches one
upstream process and forwards stdin, stdout and stderr. Protocol stdout contains
only bytes received from the real server.

The event log is optional and append-only JSON Lines. For a managed route it
lives in Kytto's data folder and inherits that folder's per-user access list
(§6); a file named with `--event-log` gets whatever its own folder grants. The
preview records:

- session start and end,
- route, client and session identifiers,
- `tools/call` name and JSON-RPC request id,
- completion latency, success and JSON-RPC error code,
- a `tools/call` the helper refused because the route hides that tool
  (`tool.call.refused`, by name and request id),
- whether a session ended because the server stopped while its client was still
  attached (`session.ended` with `unexpected: true`),
- a session the helper could not start at all (`session.failed` with a reason
  from a fixed vocabulary: `secret-unavailable` or `launch-failed`).

The last three exist because a client reports each of them the same way — the
server "failed" — and Kytto is the only place that knows which it was. The helper
marks the client as gone before it closes the server's input, so a server that
exits the moment it sees end of input is not recorded as having stopped on its own.

It never records tool arguments, results, environment values or stderr. Losing
the event log must not interrupt an observability-only session.

## Managed route command

The app writes only the bundled helper plus an opaque UUID into a client config:

```text
<Kytto install folder>\kytto-mcp-proxy.exe --route ROUTE-UUID
```

The helper reads secret-free route metadata from Kytto's data folder
(`%APPDATA%\Kytto\gateway-routes.json`), resolves environment values from the
system credential store (Windows Credential Manager; Keychain on macOS), ties the
upstream process tree to itself so it cannot outlive the connection (a
kill-on-close job object on Windows, a process group on macOS), and logs metadata
to the gateway event log (`%APPDATA%\Kytto\gateway-events.jsonl`). The
route file never contains an environment value or the original definition.

Before enabling, the app shows the redacted Direct definition and exact Gateway
replacement. Enabling and restoring both use the ordinary digest guard, backup,
syntax validation and atomic splice pipeline. Restore puts back the original
definition byte for byte and deletes the route credentials.

A route can also be narrowed to an exact list of tools (§7.11). Only then does
the helper stop being a transparent relay: it filters `tools/list` results and
refuses calls to hidden tools itself.

## When something the route depends on goes away

A Gateway definition depends on three things outside the client's config: the
helper executable, the route record and the credential store. Each can disappear
without the config changing, so each has a way back:

| What went away | How Kytto notices | Way back |
|---|---|---|
| The helper (Kytto uninstalled, moved, or a portable copy run from another folder) | On every reload, the path each Gateway definition names is checked. Doctor: `gateway-helper-missing` | **Repair route** rewrites only the helper path, through the guarded write. Before uninstalling: **Restore Direct mode for all** |
| The route record | A definition whose route id is not in the store. Doctor: `gateway-route-unknown` | Restore a backup, or edit the server |
| A stored environment value | The helper journals `session.failed` / `secret-unavailable`. Doctor offers Restore Direct | Restore rebuilds what it can and names the missing variables |
| The stored original definition | Found at restore time | Direct mode is rebuilt from the route's command and arguments instead of refusing |
| The server process, mid-session | `session.ended` with `unexpected: true`. Doctor: `gateway-server-stopped` | Health check, then restart the client |

Diagnosis reads configs, the route file and the journal only. It never opens
Credential Manager, so it can run on every reload.

Rotating a secret in the Secrets screen also updates every route holding the old
value, and the original definition stored for Restore Direct, and counts as a
restart to do for that client.

## Safety invariants

1. Direct mode remains available and is the uninstall/recovery path.
2. No client config is migrated silently or on launch.
3. Config migration uses the existing backup, digest guard and atomic write.
4. A stdio connection owns its own upstream process; sessions are never shared.
5. Payload capture stays off unless the user explicitly enables it.
6. Future `ask` and `block` policies fail closed if Kytto cannot decide.
7. Tool annotations inform the UI but never grant permission by themselves.

## Remaining compatibility work before broad enablement

- run compatibility fixtures for initialize, notifications, tool calls,
  cancellation, server-to-client requests and malformed upstream output;
- measure relay latency and verify byte-for-byte output on real servers;
- validate credential-store access from the signed helper (Authenticode on
  Windows; Developer ID signing and notarization on macOS).
