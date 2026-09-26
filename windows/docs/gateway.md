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
- completion latency, success and JSON-RPC error code.

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
