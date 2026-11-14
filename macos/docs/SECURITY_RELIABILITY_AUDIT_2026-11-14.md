# Security and reliability audit — 2026-11-14

Seven defects were reproduced: four P1 findings and three P2 findings. The first
release priority is the Gateway tool-filter bypass, followed by the MCP crash
and configuration data-loss findings.

**Update 2026-11-14:** all seven are fixed on macOS (roadmap §21 lists the
regression suites). The probes below assert the old behavior, so against the
fixed code each of them now fails; that is the expected result. The Windows
implementation has not been checked yet.

## Scope and evidence

Reviewed the active macOS source at commit
`3ad6434f7b05132f13a1a6f0f34d6f0e61ddc4e0`: the WebKit/IPC boundary, config writer,
atomic writes, backups, secret rotation, Gateway migration/relay/filtering,
stdio and HTTP health checks, OAuth, portable import/export and the updater.
Review depth was concentrated on trust boundaries and data-changing operations;
this was not an exhaustive review of every application flow.

All reproductions ran against a temporary copy of `macos/KyttoCore`, synthetic
configurations and an in-memory credential store. The Gateway probe used the
actual built `kytto-mcp-proxy`, with an upstream that only echoes the dispatched
tool name. No real tool operation, user-config edit, credential-store mutation,
production network request or updater installation was performed.

Historical sibling copies were excluded. Windows is excluded from the findings
and runtime validation. No P0 exploit, arbitrary remote code execution or
credential theft was demonstrated. P1 denotes a high-priority security boundary
failure, process crash or configuration data loss; P2 denotes a medium-priority
defect with narrower impact or additional conditions.

The retained [audit probes](audits/2026-11-14/Audit20261114Tests.swift) assert the
current erroneous behavior. Green results mean reproduction succeeded, not that
the application is fixed. Crash probes are separately enabled and intentionally
terminate the test process; they are not part of the regular test target.

## AUD-07 — P1: duplicate JSON keys bypass Gateway tool restrictions

**Source:** [`GatewayToolFilter.swift:99–141`](../KyttoCore/Sources/KyttoCore/Gateway/GatewayToolFilter.swift#L99),
also response parsing at line 194. The relay forwards the original request bytes
after inspecting them.

**Trigger:** a route exposes only `read_file`. A client sends a request containing
two `params.name` members: first `read_file`, then `write_file`. Foundation's
`JSONSerialization` on the tested macOS build retains the first value; the
upstream Python JSON parser retains the last. The filter approves `read_file`
and forwards the original bytes, which the server dispatches as `write_file`.

**Observed:** the real proxy returned `result.called = "write_file"`, with no
refusal, despite its allow list containing only `read_file`. The upstream only
reported the name; it did not perform a write. This proves failure of the tool
restriction, not an operating-system sandbox escape. A caller able to supply
ambiguous JSON to the proxy can cross this boundary; ordinary conforming client
requests do not need duplicate keys.

**Cause:** an enforcement decision is made with one interpretation of ambiguous
JSON, then different software receives the unreconciled original representation.
The batch path uses the same parser. Response filtering likewise lacks duplicate
key rejection; its downstream consequences need regression coverage rather than
being assumed identical to this demonstrated request bypass.

**Acceptance:** reject duplicate object keys recursively on narrowed routes,
including escaped equivalent keys, before forwarding requests or filtered
responses. Reject malformed `tools/call` envelopes rather than treating an
unreadable tool name as permission to forward. Exercise duplicate `method`,
`params`, `name`, batches and tool-list entries against a real upstream. Preserve
the documented transparent behavior for routes without restrictions.

**Probe:** `duplicateToolNameBypassesTheRealGateway`.

## AUD-01 — P1: an MCP response can crash the checking process

**Source:** [`MCPHTTPClient.swift:75`](../KyttoCore/Sources/KyttoCore/Health/MCPHTTPClient.swift#L75),
[`MCPHTTPClient.swift:270`](../KyttoCore/Sources/KyttoCore/Health/MCPHTTPClient.swift#L270),
[`MCPStdioClient.swift:365`](../KyttoCore/Sources/KyttoCore/Health/MCPStdioClient.swift#L365).

**Trigger:** a server returns a matching JSON-RPC error with `code: 1e100`.
`JSONDocument` represents the number as a `Double`, and the client calls
`Int(value)` without checking its range. This is a Swift runtime trap, which
`do/catch` and `try?` cannot turn into a failed check.

**Observed:** the HTTP probe used the real response parsing path with an injected
transport reply; the stdio probe used a real Python child process. Each separate
test process terminated with signal 5 and:

> Double value cannot be converted to Int because the result would be greater than Int.max

The GUI runs these clients inside its own process, so a trap there terminates
the application rather than just marking the server unhealthy. The additional
HTTP error-status conversion has the same unchecked operation; the retained
HTTP crash probe exercises the 200 response path.

**Acceptance:** validate that the code is finite, integral and representable,
then convert with a failable conversion such as `Int(exactly:)`. Invalid values
must become controlled protocol errors at every conversion site. Include
positive/negative overflow, fractional values, a non-finite parsed number and
valid boundary cases. Keep crash-input checks in subprocesses until fixed.

**Probes:** `remoteHTTPErrorCodeOverflowCrashes`,
`stdioErrorCodeOverflowCrashes`.

## AUD-02 — P1: the config digest guard misses edits during a write

**Source:** [`ConfigWriter.swift:121–178`](../KyttoCore/Sources/KyttoCore/Write/ConfigWriter.swift#L121).

**Trigger:** another application saves the config after the initial digest check
but before Kytto commits the transformed text. The writer checks a digest, reads
the file again, transforms and validates it, backs up the current on-disk file,
then renames its output over that file without another conflict check.
`MutationCoordinator` only serializes Kytto's own in-process operations.

**Observed:** an isolated transform hook simulated an external save while the
transaction was in progress. The writer returned success, replaced the new
`external-save` field value with the old `before` value, and backed up different
bytes from those it had transformed. This is a deterministic interleaving of
the normal filesystem operations; no production transform was changed.

**Impact:** a client or editor's newer settings can be overwritten by a toggle,
edit, rotation or import using this shared writer. In the demonstrated timing,
the external save survived in a backup, but the active config lost it silently.

**Acceptance:** read the source bytes once and verify that exact snapshot against
the expected digest. Ensure backup creation and the pre-commit conflict check
refer to that snapshot, refusing an observed change with `changedOnDisk`.
Cover changes between read/transform/backup/commit and creation of a previously
absent destination. Rechecking reduces the race window; do not claim perfect
coordination with arbitrary programs that ignore Kytto's locks.

**Probe:** `configWriterOverwritesAnEditDuringTransform`.

## AUD-03 — P1: secret-rotation rollback overwrites newer external work

**Source:** [`SecretsService.swift:276–290`](../KyttoCore/Sources/KyttoCore/Secrets/SecretsService.swift#L276).

**Trigger:** rotation has updated a config, an editor or client then saves newer
content, and a later rotation step fails. The catch block restores the old bytes
through `AtomicWriter` without checking that the target still contains this
transaction's output. Other write paths, including `ServerAuthoring` and
portable import rollback, already retain and check their written digests.

**Observed:** a fake credential-store failure simulated an external config save
immediately before throwing. Rotation restored the original file over the newer
`editorNote: "must-survive"` content. It reported the injected store error, with
no conflict indication and no backup of that external save.

**Acceptance:** retain each written digest and roll back only while the file
still matches it. Preserve conflicting external edits, report incomplete
rollback with paths and recovery backup IDs, and apply equivalent guards to
parked-state restoration. Cover config files, absence, symlinks and failures
after several clients were updated.

**Probe:** `rotationRollbackErasesAnExternalSave`.

## AUD-04 — P2: blocking stdio writes bypass the check deadline

**Source:** [`ManagedProcess.swift:186–198`](../KyttoCore/Sources/KyttoCore/Health/ManagedProcess.swift#L186),
called when answering server requests at
[`MCPStdioClient.swift:350–354`](../KyttoCore/Sources/KyttoCore/Health/MCPStdioClient.swift#L350).

**Trigger:** a server sends a request with a large string ID and stops reading
stdin. Kytto echoes that ID in its method-not-found response. `Darwin.write` uses
a blocking pipe and receives no deadline; once the pipe fills, the code cannot
reach its read deadline or normal process cleanup.

**Observed:** a bounded fixture sent a 262,144-character ID and exited after
three seconds without reading the response. A one-second check took about
3.1 seconds. The fixture deliberately exits so the reproduction itself cannot
wait indefinitely. A server that keeps the pipe open without consuming it can
hold the write until an external action interrupts it.

**Acceptance:** use nonblocking writes and `poll(POLLOUT)` with the same absolute
inspection deadline, handle partial writes and `EINTR`, and terminate/reap the
whole process group on expiry. Test a server that never drains stdin and verify
completion within the deadline plus the documented shutdown grace.

**Probe:** `serverRequestCanHoldCheckPastItsTimeout`.

## AUD-05 — P2: first measurement storage is not created privately

**Source:** [`MetadataStore.swift:160–166`](../KyttoCore/Sources/KyttoCore/Write/MetadataStore.swift#L160),
[`AtomicWriter.swift`](../KyttoCore/Sources/KyttoCore/Write/AtomicWriter.swift).

**Trigger:** a first health check occurs before a backup or park operation has
created Kytto's private data folder. Health results preserve raw stderr, which
can contain credentials printed by a server. The metadata writer neither calls
`createPrivateDirectory` nor requests private file permissions.

**Observed:** with the audit process's normal `022` umask, recording a synthetic
stderr token created `server-metadata.json` as `0644` under a new `0755` Kytto
folder, with the token present in plaintext. App startup tightening cannot
protect a directory that does not exist yet. A later backup/park operation or
next launch may tighten the root, but this first write does not.

**Limits:** cross-user readability additionally requires traversable ancestor
directories and compatible ACLs. This is not proof that every macOS account
exposes its Library directory, or that another user read any real credential.
Owner-only storage also does not isolate processes running as the same user.

**Acceptance:** create the metadata directory as `0700` before staging any bytes
and explicitly write the metadata file as `0600`. Cover the first successful
and failed checks without a prior settings/write operation, existing storage
and the temporary staged file. Preserve useful verbatim stderr under private
storage rather than removing the diagnostic feature.

**Probe:** `metadataFirstWriteStoresRawStderrWithDefaultPermissions`.

## AUD-06 — P2: failure cleanup deletes an existing destination credential

**Source:** [`SecretsService.swift:289`](../KyttoCore/Sources/KyttoCore/Secrets/SecretsService.swift#L289).

**Trigger:** the destination value already has its own saved credential ID.
Rotation fails before changing that credential, for example because the config
changed on disk. The catch block unconditionally calls `store.delete(newID)`,
including when `alsoStoreInSecretStore` is false.

**Observed:** preloaded an unrelated saved destination value in an in-memory
store, forced a config digest conflict, and attempted a config-only rotation.
Rotation threw `changedOnDisk` while deleting that existing saved value. It had
never written a new credential in the failed transaction.

**Acceptance:** snapshot affected credential records and track which mutations
actually succeeded. Restore previous values after a failure rather than always
deleting the destination. A failure before any credential mutation, especially
with credential storage disabled, must leave the credential store unchanged.

**Probe:** `failedRotationDeletesAnUnrelatedSavedCredential`.

## Validation and reproduction

The existing core suite ran 589 tests. Its two real-loopback tests initially
failed because the execution sandbox denied `bind(127.0.0.1)`; running the same
two tests with local socket access passed. The opt-in real-home inventory test
was skipped. These environment failures were not counted as application bugs.

Six additional non-crashing audit probes passed, with the two crash probes
disabled. Both crash probes were then enabled separately and each terminated
with signal 5. The Gateway bypass was verified through the executable, while
HTTP overflow used an injected reply and stdio overflow used a real child.

From the repository root, prepare a disposable package copy; do not add these
audit probes to the production test target:

```sh
audit_dir="$(mktemp -d /private/tmp/kytto-audit-XXXXXX)"
cp macos/KyttoCore/Package.swift "$audit_dir/Package.swift"
cp -R macos/KyttoCore/Sources "$audit_dir/Sources"
cp -R macos/KyttoCore/Tests "$audit_dir/Tests"
cp macos/docs/audits/2026-11-14/Audit20261114Tests.swift "$audit_dir/Tests/KyttoCoreTests/"
export CLANG_MODULE_CACHE_PATH="$audit_dir/clang-cache"
export SWIFTPM_MODULECACHE_OVERRIDE="$audit_dir/swift-cache"
swift test --package-path "$audit_dir" --disable-sandbox --filter Audit20261114Tests
```

Run each intentional crash separately against that copy:

```sh
AUDIT_CRASH_PROBE=http swift test --package-path "$audit_dir" --disable-sandbox --skip-build --filter AuditCrashProbeTests
AUDIT_CRASH_PROBE=stdio swift test --package-path "$audit_dir" --disable-sandbox --skip-build --filter AuditCrashProbeTests
```

After fixes, turn the probes into ordinary regression tests that assert refusal,
controlled errors, preserved concurrent edits and unchanged credential state.
Validate the same boundaries independently in the Windows implementation.
The Windows demo reports parked in roadmap §20 were not reproduced here.
