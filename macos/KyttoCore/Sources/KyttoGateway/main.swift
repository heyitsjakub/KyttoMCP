import Darwin
import Foundation
import KyttoCore

private let usage = """
Usage:
  kytto-mcp-proxy --route UUID
  kytto-mcp-proxy [--server-id ID] [--client-id ID] [--event-log PATH] -- COMMAND [ARG ...]

Forwards MCP stdio byte-for-byte unless the route restricts which tools it
exposes, in which case tools/list results are filtered and calls to hidden tools
are refused here. The event log records session and tool-call metadata only;
arguments, responses, environment values and stderr are never stored.
"""

/// One writer per file handle, so the two threads that can both answer the
/// client — the upstream relay and a refusal from the filter — cannot interleave
/// halfway through a message.
private final class SynchronizedWriter: @unchecked Sendable {
    private let handle: FileHandle
    private let lock = NSLock()

    init(_ handle: FileHandle) { self.handle = handle }

    func write(_ data: Data) {
        guard !data.isEmpty else { return }
        lock.lock()
        defer { lock.unlock() }
        try? handle.write(contentsOf: data)
    }

    /// A complete MCP message: exactly one line.
    func writeLine(_ data: Data) {
        guard !data.isEmpty else { return }
        var line = data
        line.append(0x0A)
        write(line)
    }

    func close() {
        lock.lock()
        defer { lock.unlock() }
        try? handle.close()
    }
}

/// Byte-for-byte forwarding — the §3.3 path, used whenever nothing is masked.
private func relay(
    from source: FileHandle,
    to destination: SynchronizedWriter,
    observer: GatewayMessageObserver? = nil,
    direction: GatewayDirection? = nil,
    closeDestinationAtEOF: Bool = false,
    finished: DispatchSemaphore? = nil,
    afterEOF: (@Sendable () -> Void)? = nil
) {
    Thread.detachNewThread {
        defer {
            // First, before the server's input is closed: a server that exits
            // the moment it sees EOF must already count as let go.
            afterEOF?()
            if closeDestinationAtEOF { destination.close() }
            finished?.signal()
        }
        do {
            while let data = try source.read(upToCount: 64 * 1024), !data.isEmpty {
                if let observer, let direction { observer.accept(data, direction: direction) }
                destination.write(data)
            }
        } catch {
            // Closure in either direction is a normal part of process teardown.
        }
    }
}

/// Message-by-message forwarding, used only by a route with an allow list.
///
/// `transform` returns the bytes to forward, or nil to forward nothing — in
/// which case it has already answered the sender itself.
private func relayMessages(
    from source: FileHandle,
    to destination: SynchronizedWriter,
    observer: GatewayMessageObserver,
    direction: GatewayDirection,
    transform: @escaping @Sendable (Data) -> Data?,
    closeDestinationAtEOF: Bool = false,
    finished: DispatchSemaphore? = nil,
    afterEOF: (@Sendable () -> Void)? = nil
) {
    Thread.detachNewThread {
        var framer = GatewayLineFramer()
        defer {
            // First, before the server's input is closed (see `relay`).
            afterEOF?()
            // A trailing partial message still belongs to the other side, and
            // still goes through the filter: servers that read line by line
            // act on a final line without its newline too.
            framer.flush { tail in
                if let forwarded = transform(tail) { destination.write(forwarded) }
            }
            if closeDestinationAtEOF { destination.close() }
            finished?.signal()
        }
        do {
            while let data = try source.read(upToCount: 64 * 1024), !data.isEmpty {
                framer.consume(data) { line in
                    // The observer sees what actually crossed, so a refused call
                    // is never recorded as having reached the server.
                    guard let forwarded = transform(line) else { return }
                    observer.accept(forwarded + [0x0A], direction: direction)
                    destination.writeLine(forwarded)
                }
            }
        } catch {
            // Closure in either direction is a normal part of process teardown.
        }
    }
}

/// Whether the client has let go — closed its end or signalled the helper.
/// A server that stops before that did so on its own.
private final class ClientPresence: @unchecked Sendable {
    private let lock = NSLock()
    private var gone = false

    func markGone() {
        lock.lock()
        gone = true
        lock.unlock()
    }

    var isGone: Bool {
        lock.lock()
        defer { lock.unlock() }
        return gone
    }
}

/// Set once the route is known, so a failure after that point can still be
/// written to the journal: the client only shows "server failed".
private var startingObserver: GatewayMessageObserver?

do {
    let invocation = try GatewayInvocation.parse(Array(CommandLine.arguments.dropFirst()))
    let command: String
    let arguments: [String]
    let extraEnvironment: [String: String]
    let serverID: String
    let clientID: String
    let eventLogURL: URL?
    var exposedTools: [String]?

    if let routeID = invocation.routeID {
        let paths = KyttoPaths(home: FileManager.default.homeDirectoryForCurrentUser)
        let routeStore = invocation.routesURL.map(GatewayRouteStore.init(url:)) ?? GatewayRouteStore(paths: paths)
        let known = try routeStore.route(id: routeID)
        let journal = invocation.eventLogURL ?? paths.gatewayEvents
        startingObserver = GatewayMessageObserver(
            serverID: known.serverID,
            clientID: known.clientID.rawValue,
            sink: (try? JSONLinesGatewayEventSink(url: journal)) ?? NullGatewayEventSink()
        )
        let launch: GatewayLaunch
        do {
            launch = try routeStore.resolve(
                id: routeID,
                secrets: KeychainSecretStore(service: "app.kytto.gateway")
            )
        } catch {
            startingObserver?.fail(reason: .secretUnavailable)
            startingObserver = nil
            throw error
        }
        command = launch.route.command
        arguments = launch.route.arguments
        extraEnvironment = launch.environment
        serverID = launch.route.serverID
        clientID = launch.route.clientID.rawValue
        eventLogURL = invocation.eventLogURL ?? paths.gatewayEvents
        exposedTools = launch.route.exposedTools
    } else {
        guard let directCommand = invocation.command else { throw GatewayInvocationError.missingCommand }
        command = directCommand
        arguments = invocation.arguments
        extraEnvironment = [:]
        serverID = invocation.serverID
        clientID = invocation.clientID
        eventLogURL = invocation.eventLogURL
    }

    // Losing the event log must not interrupt the client's session
    // (docs/gateway.md): if it cannot be opened, the session runs unlogged.
    let sink: any GatewayEventSinking = eventLogURL
        .flatMap { try? JSONLinesGatewayEventSink(url: $0) } ?? NullGatewayEventSink()

    let observer = GatewayMessageObserver(
        serverID: serverID,
        clientID: clientID,
        sink: sink
    )
    // From here the session's own observer reports a failed launch.
    startingObserver = observer

    let shell = ShellEnvironment.current
    let executable = try ManagedExecutable.resolve(command, path: shell.path)
    signal(SIGPIPE, SIG_IGN)
    let process = try GatewayManagedProcess(
        executable: executable,
        arguments: arguments,
        environment: shell.environment(adding: extraEnvironment)
    )
    startingObserver = nil
    observer.start()
    let client = ClientPresence()

    let terminationQueue = DispatchQueue(label: "app.kytto.gateway.signals")
    /// SIGTERM to the upstream's group, then SIGKILL for anything that ignored it.
    let shutDownUpstream: @Sendable (TimeInterval) -> Void = { delay in
        terminationQueue.asyncAfter(deadline: .now() + delay) {
            process.terminateGroup()
            terminationQueue.asyncAfter(deadline: .now() + 2) {
                process.terminateGroup(signal: SIGKILL)
            }
        }
    }
    var signalSources: [DispatchSourceSignal] = []
    for watchedSignal in [SIGINT, SIGTERM, SIGHUP] {
        signal(watchedSignal, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: watchedSignal, queue: terminationQueue)
        source.setEventHandler {
            client.markGone()
            shutDownUpstream(0)
        }
        source.resume()
        signalSources.append(source)
    }
    // The client closing its end (a crash, a force quit) is the only notice the
    // helper gets. The upstream's stdin is closed with it; a server with live
    // handles that does not exit on that gets a grace period, then the signals.
    let clientGone: @Sendable () -> Void = {
        client.markGone()
        shutDownUpstream(5)
    }

    let outputFinished = DispatchSemaphore(value: 0)
    let errorFinished = DispatchSemaphore(value: 0)
    let toClient = SynchronizedWriter(FileHandle.standardOutput)
    let toServer = SynchronizedWriter(process.standardInput)

    if let exposedTools {
        // A narrowed route. Both directions are read message by message so the
        // tool list can be filtered and calls to hidden tools can be answered
        // here (§7.11).
        let filter = GatewayToolFilter(exposedTools: exposedTools)
        relayMessages(
            from: FileHandle.standardInput,
            to: toServer,
            observer: observer,
            direction: .clientToServer,
            transform: { line in
                switch filter.inspectClientLine(line) {
                case .forward:
                    return line
                case .refuse(let response):
                    // Journalled so a refusal is visible in Live Activity:
                    // without it a blocked call looks like nothing happened.
                    for call in filter.hiddenCalls(in: line) {
                        observer.recordRefusal(toolName: call.toolName, requestID: call.requestID)
                    }
                    toClient.writeLine(response)
                    return nil
                }
            },
            closeDestinationAtEOF: true,
            afterEOF: clientGone
        )
        relayMessages(
            from: process.standardOutput,
            to: toClient,
            observer: observer,
            direction: .serverToClient,
            transform: filter.rewriteServerLine,
            finished: outputFinished
        )
    } else {
        relay(
            from: FileHandle.standardInput,
            to: toServer,
            observer: observer,
            direction: .clientToServer,
            closeDestinationAtEOF: true,
            afterEOF: clientGone
        )
        relay(
            from: process.standardOutput,
            to: toClient,
            observer: observer,
            direction: .serverToClient,
            finished: outputFinished
        )
    }

    relay(
        from: process.standardError,
        to: SynchronizedWriter(FileHandle.standardError),
        finished: errorFinished
    )

    let exitCode = process.waitUntilExit()
    // Read before anything else can close the client's end: a server that is
    // gone while its client is still attached stopped on its own.
    let stoppedOnItsOwn = !client.isGone
    // Whatever the upstream started outlives it unless its group is told too.
    process.terminateGroup()
    _ = outputFinished.wait(timeout: .now() + 1)
    _ = errorFinished.wait(timeout: .now() + 1)
    observer.finish(exitCode: exitCode, unexpected: stoppedOnItsOwn)
    for source in signalSources { source.cancel() }
    exit(exitCode)
} catch {
    startingObserver?.fail(reason: .launchFailed)
    let message = (error as? LocalizedError)?.errorDescription ?? String(describing: error)
    FileHandle.standardError.write(Data("Kytto gateway: \(message)\n\n\(usage)\n".utf8))
    exit(64)
}
