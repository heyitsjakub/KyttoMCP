# Kytto Gateway

The gateway is the first control-plane building block. G1 connects the tested
stdio relay to an explicit, reversible app workflow; Direct mode remains the
default and recovery path.

## Preview command

```sh
cd KyttoCore
swift run kytto-mcp-proxy \
  --server-id github \
  --client-id cursor \
  --event-log /tmp/kytto-gateway-events.jsonl \
  -- npx -y @example/github-mcp-server
```

Everything after `--` is the real stdio server command. No shell interprets it.
The proxy resolves the executable against the same login-shell `PATH` used by
health checks, launches one upstream process and forwards stdin, stdout and
stderr. Protocol stdout contains only bytes received from the real server.

The event log is optional, append-only JSON Lines and restricted to the current
user. The preview records:

- session start and end,
- route, client and session identifiers,
- `tools/call` name and JSON-RPC request id,
- completion latency, success and JSON-RPC error code.

It never records tool arguments, results, environment values or stderr. Losing
the event log must not interrupt an observability-only session.

## Managed route command

The app writes only the bundled helper plus an opaque UUID into a client config:

```sh
KyttoMCP.app/Contents/MacOS/kytto-mcp-proxy --route ROUTE-UUID
```

The helper reads secret-free route metadata from Kytto's app-support directory,
resolves environment values from the system credential store, creates a process
group for the upstream tree, and logs metadata to the private gateway event log.
The route file never contains an environment value or the original definition.

Before enabling, the app shows the redacted Direct definition and exact Gateway
replacement. Enabling and restoring both use the ordinary digest guard, backup,
syntax validation and atomic splice pipeline. Restore puts back the original
definition byte for byte and deletes the route credentials.

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
- validate credential-store access after Developer ID signing and notarization;
- implement the equivalent credential store and helper bundle on Windows.
