import Darwin
import Foundation

/// A byte-stream child process created in its own process group.
///
/// Gateway mode must be able to terminate an `npx`/`node` tree when its client
/// disappears. Creating the group as part of `posix_spawn` closes the race where
/// the child can `exec` before a later `setpgid` call reaches it.
public final class GatewayManagedProcess: @unchecked Sendable {
    public let processIdentifier: pid_t
    public let standardInput: FileHandle
    public let standardOutput: FileHandle
    public let standardError: FileHandle

    private let stateLock = NSLock()
    private var didReap = false
    private var reapedExitCode: Int32?

    public init(executable: String, arguments: [String], environment: [String: String]) throws {
        let resolved = try ManagedExecutable.resolve(executable, path: environment["PATH"] ?? "")

        var inputPipe: [Int32] = [0, 0]
        var outputPipe: [Int32] = [0, 0]
        var errorPipe: [Int32] = [0, 0]
        guard pipe(&inputPipe) == 0 else {
            throw ProcessError.spawnFailed(executable, code: errno)
        }
        guard pipe(&outputPipe) == 0 else {
            Self.close(inputPipe)
            throw ProcessError.spawnFailed(executable, code: errno)
        }
        guard pipe(&errorPipe) == 0 else {
            Self.close(inputPipe)
            Self.close(outputPipe)
            throw ProcessError.spawnFailed(executable, code: errno)
        }

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, inputPipe[0], STDIN_FILENO)
        posix_spawn_file_actions_adddup2(&actions, outputPipe[1], STDOUT_FILENO)
        posix_spawn_file_actions_adddup2(&actions, errorPipe[1], STDERR_FILENO)
        for descriptor in [inputPipe[0], inputPipe[1], outputPipe[0], outputPipe[1], errorPipe[0], errorPipe[1]] {
            posix_spawn_file_actions_addclose(&actions, descriptor)
        }

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setpgroup(&attributes, 0)
        var defaultSignals = sigset_t()
        sigemptyset(&defaultSignals)
        for signal in [SIGINT, SIGTERM, SIGHUP, SIGPIPE] {
            sigaddset(&defaultSignals, signal)
        }
        posix_spawnattr_setsigdefault(&attributes, &defaultSignals)
        var signalMask = sigset_t()
        sigemptyset(&signalMask)
        posix_spawnattr_setsigmask(&attributes, &signalMask)
        posix_spawnattr_setflags(
            &attributes,
            Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK)
        )

        let argv = [resolved] + arguments
        let envp = environment.map { "\($0.key)=\($0.value)" }
        var cArguments: [UnsafeMutablePointer<CChar>?] = argv.map { strdup($0) }
        var cEnvironment: [UnsafeMutablePointer<CChar>?] = envp.map { strdup($0) }
        cArguments.append(nil)
        cEnvironment.append(nil)
        defer {
            for pointer in cArguments where pointer != nil { free(pointer) }
            for pointer in cEnvironment where pointer != nil { free(pointer) }
        }

        var spawned: pid_t = 0
        let result = posix_spawn(&spawned, resolved, &actions, &attributes, cArguments, cEnvironment)

        Darwin.close(inputPipe[0])
        Darwin.close(outputPipe[1])
        Darwin.close(errorPipe[1])

        guard result == 0 else {
            Darwin.close(inputPipe[1])
            Darwin.close(outputPipe[0])
            Darwin.close(errorPipe[0])
            throw ProcessError.spawnFailed(executable, code: result)
        }

        processIdentifier = spawned
        standardInput = FileHandle(fileDescriptor: inputPipe[1], closeOnDealloc: true)
        standardOutput = FileHandle(fileDescriptor: outputPipe[0], closeOnDealloc: true)
        standardError = FileHandle(fileDescriptor: errorPipe[0], closeOnDealloc: true)
    }

    deinit {
        stateLock.lock()
        let needsCleanup = !didReap
        stateLock.unlock()
        if needsCleanup {
            terminateGroup()
            _ = waitUntilExit()
        }
    }

    /// Sends a signal to the upstream and every descendant it started.
    public func terminateGroup(signal: Int32 = SIGTERM) {
        _ = kill(-processIdentifier, signal)
    }

    /// Reaps the direct child and converts the wait status into a shell-style
    /// exit code. Calling this more than once is harmless.
    @discardableResult
    public func waitUntilExit() -> Int32 {
        stateLock.lock()
        if let reapedExitCode {
            stateLock.unlock()
            return reapedExitCode
        }
        stateLock.unlock()

        var status: Int32 = 0
        while waitpid(processIdentifier, &status, 0) < 0 {
            if errno == EINTR { continue }
            return 1
        }

        let signal = status & 0x7f
        let exitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal
        stateLock.lock()
        didReap = true
        reapedExitCode = exitCode
        stateLock.unlock()
        return exitCode
    }

    private static func close(_ pipe: [Int32]) {
        for descriptor in pipe { Darwin.close(descriptor) }
    }
}
