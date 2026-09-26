#!/usr/bin/env python3
"""A stand-in MCP server, so the health-check tests do not depend on npx.

Modes are chosen by the first argument:

    ok        a working server with three tools
    empty     a working server with no tools
    noisy     prints a banner to stdout before speaking JSON-RPC
    slow      accepts the handshake and then never answers tools/list
    crash     writes to stderr and exits before answering
    error     answers tools/list with a JSON-RPC error
    garbage   answers with something that is not JSON-RPC
    fractional_id sends a response whose numeric id only truncates to the request id
    wrong_jsonrpc answers using a different JSON-RPC revision
    children  spawns a long-lived grandchild, then behaves like `slow`
    oauth     prints an mcp-remote browser consent prompt, then waits
    optional_error answers prompts/list with a JSON-RPC error
    pagination     answers prompts/list with a nextCursor

`children` exists to prove Kytto kills the whole process tree (a Job Object on
Windows): `npx` spawning `node` is the real-world shape, and killing only the
direct child leaves the grandchild running forever.
"""
import json
import os
import subprocess
import sys
import time

MODE = sys.argv[1] if len(sys.argv) > 1 else "ok"

TOOLS = [
    {
        "name": "read_file",
        "description": "Read the contents of a file from the allowed directories.",
        "annotations": {"readOnlyHint": True, "idempotentHint": True},
        "inputSchema": {
            "type": "object",
            "properties": {"path": {"type": "string", "description": "Path to read"}},
            "required": ["path"],
        },
    },
    {
        "name": "write_file",
        "description": "Write text to a file, creating it if needed.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "path": {"type": "string", "description": "Path to write"},
                "contents": {"type": "string", "description": "What to write"},
            },
            "required": ["path", "contents"],
        },
    },
    {
        "name": "list_directory",
        "description": "List the entries of a directory.",
        "inputSchema": {
            "type": "object",
            "properties": {"path": {"type": "string"}},
            "required": ["path"],
        },
    },
]


def send(payload):
    sys.stdout.write(json.dumps(payload) + "\n")
    sys.stdout.flush()


def main():
    if MODE == "noisy":
        # Plenty of real servers log to stdout despite the spec.
        sys.stdout.write("starting up, listening on stdio\n")
        sys.stdout.flush()

    if MODE == "crash":
        sys.stderr.write("Error: cannot find module 'some-dependency'\n")
        sys.stderr.write("  at Object.<anonymous> (/tmp/server.js:1:1)\n")
        sys.stderr.flush()
        sys.exit(1)

    if MODE == "children":
        # A grandchild that outlives its parent unless the group is killed.
        marker = os.environ.get("KYTTO_TEST_MARKER", "kytto-test")
        subprocess.Popen(
            [sys.executable, "-c", f"import time; time.sleep(300)  # {marker}"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    if MODE == "oauth":
        sys.stderr.write(
            "Authentication required. Waiting for authorization...\n"
            "Please authorize this client by visiting: "
            "https://auth.example.com/oauth/authorize?client_id=kytto\n"
        )
        sys.stderr.flush()
        time.sleep(120)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            message = json.loads(line)
        except json.JSONDecodeError:
            continue

        method = message.get("method")
        if method == "initialize":
            send({
                "jsonrpc": "2.0",
                "id": message["id"],
                "result": {
                    "protocolVersion": message["params"]["protocolVersion"],
                    "capabilities": {"tools": {}, "prompts": {}, "resources": {}},
                    "serverInfo": {"name": "fake-mcp-server", "version": "0.1.0"},
                },
            })
        elif method == "notifications/initialized":
            continue
        elif method == "tools/list":
            if MODE in ("slow", "children"):
                time.sleep(120)
            elif MODE == "error":
                send({
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "error": {"code": -32603, "message": "tools are unavailable right now"},
                })
            elif MODE == "garbage":
                send({"jsonrpc": "2.0", "id": message["id"], "result": {"nothing": True}})
            elif MODE == "wrong_jsonrpc":
                send({"jsonrpc": "1.0", "id": message["id"], "result": {"tools": TOOLS}})
            else:
                if MODE == "fractional_id":
                    send({
                        "jsonrpc": "2.0",
                        "id": message["id"] + 0.5,
                        "result": {"tools": []},
                    })
                send({
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "result": {"tools": [] if MODE == "empty" else TOOLS},
                })
        elif method == "prompts/list":
            if MODE == "optional_error":
                send({
                    "jsonrpc": "2.0",
                    "id": message["id"],
                    "error": {"code": -32603, "message": "prompts unavailable"},
                })
            else:
                result = {"prompts": [{"name": "review"}, {"name": "summarize"}]}
                if MODE == "pagination":
                    result["nextCursor"] = "more-prompts"
                send({"jsonrpc": "2.0", "id": message["id"], "result": result})
        elif method == "resources/list":
            send({
                "jsonrpc": "2.0",
                "id": message["id"],
                "result": {"resources": [{"uri": "file:///readme.md"}]},
            })
        elif method == "tools/call":
            # Answers every call the same way. The gateway masking tests read
            # this as "the call reached the server", so what it returns matters
            # less than that it returns at all.
            send({
                "jsonrpc": "2.0",
                "id": message["id"],
                "result": {"content": [{"type": "text", "text": "fixture result"}]},
            })


if __name__ == "__main__":
    main()
