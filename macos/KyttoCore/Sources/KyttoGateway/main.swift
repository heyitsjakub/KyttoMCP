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
    finished: DispatchSemaphore? = nil
) {
    Thread.detachNewThread {
        defer {
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
    transform: @escaping (Data) -> Data?,
    closeDestinationAtEOF: Bool = false,
    finished: DispatchSemaphore? = nil
) {
    Thread.detachNewThread {
        var framer = GatewayLineFramer()
        defer {
            // A trailing partial message still belongs to the other side.
            framer.flush { destination.write($0) }
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
        let launch = try routeStore.resolve(
            id: routeID,
            secrets: KeychainSecretStore(service: "app.kytto.gateway")
        )
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

    let sink: any GatewayEventSinking
    if let url = eventLogURL {
        sink = try JSONLinesGatewayEventSink(url: url)
    } else {
        sink = NullGatewayEventSink()
    }

    let observer = GatewayMessageObserver(
        serverID: serverID,
        clientID: clientID,
        sink: sink
    )

    let shell = ShellEnvironment.current
    let executable = try ManagedExecutable.resolve(command, path: shell.path)
    signal(SIGPIPE, SIG_IGN)
    let process = try GatewayManagedProcess(
        executable: executable,
        arguments: arguments,
        environment: shell.environment(adding: extraEnvironment)
    )
    observer.start()

    let terminationQueue = DispatchQueue(label: "app.kytto.gateway.signals")
    var signalSources: [DispatchSourceSignal] = []
    for watchedSignal in [SIGINT, SIGTERM, SIGHUP] {
        signal(watchedSignal, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: watchedSignal, queue: terminationQueue)
        source.setEventHandler {
            process.terminateGroup()
        }
        source.resume()
        signalSources.append(source)
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
                    toClient.writeLine(response)
                    return nil
                }
            },
            closeDestinationAtEOF: true
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
            closeDestinationAtEOF: true
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
    _ = outputFinished.wait(timeout: .now() + 1)
    _ = errorFinished.wait(timeout: .now() + 1)
    observer.finish(exitCode: exitCode)
    for source in signalSources { source.cancel() }
    exit(exitCode)
} catch {
    let message = (error as? LocalizedError)?.errorDescription ?? String(describing: error)
    FileHandle.standardError.write(Data("Kytto gateway: \(message)\n\n\(usage)\n".utf8))
    exit(64)
}
