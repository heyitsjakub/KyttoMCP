import Darwin
import Foundation

public enum ProcessError: Error, LocalizedError, Equatable {
    case commandNotFound(String, searchedPath: String)
    /// A path-shaped command that is not absolute, so nothing Kytto can check
    /// corresponds to what the owning client actually launches.
    case relativeCommand(String)
    /// Nothing exists at the resolved path — as distinct from something existing
    /// there without the executable bit.
    case executableMissing(String, resolvedPath: String)
    case notExecutable(String)
    case spawnFailed(String, code: Int32)
    case writeFailed
    case timedOut(after: TimeInterval)
    case ended(status: Int32)

    public var errorDescription: String? {
        switch self {
        case .commandNotFound(let command, let path):
            return """
            \(command) was not found. Kytto looked in: \(path)
            """
        case .relativeCommand(let command):
            return """
            \(command) is a relative path, and the entry does not say which directory \
            to resolve it against. The client that owns this entry starts it from a \
            directory of its own choosing, which Kytto cannot know — so this server \
            may well run fine there. Give the entry an absolute command to have Kytto \
            check it.
            """
        case .executableMissing(let command, let resolvedPath):
            return "\(command) was not found at \(resolvedPath)."
        case .notExecutable(let path):
            return "\(path) exists but is not executable. Add the executable bit with chmod +x."
        case .spawnFailed(let command, let code):
            return "Could not start \(command) (error \(code))."
        case .writeFailed:
            return "The server closed its input before Kytto could finish talking to it."
        case .timedOut(let seconds):
            let rounded = max(1, Int(seconds.rounded(.up)))
            let unit = rounded == 1 ? "second" : "seconds"
            return "The server did not answer within \(rounded) \(unit)."
        case .ended(let status):
            return "The server exited with status \(status) before answering."
        }
    }
}

/// A child process Kytto can talk to and, crucially, reliably kill.
///
/// `Foundation.Process` starts the child in *our* process group, so terminating
/// a timed-out server would either miss its children — `npx` spawns `node`, and
/// killing `npx` leaves `node` running forever — or, with a negative pid, take
/// Kytto down with it. Spawning into a fresh process group is the only way to
/// honour §6's "kill the process group on timeout".
final class ManagedProcess {
    let resolvedExecutable: String
    private let pid: pid_t
    private let stdinFD: Int32
    private let stdoutFD: Int32
    private let stderrFD: Int32

    private var stdoutBuffer = Data()
    private let stderrLock = NSLock()
    private var stderrData = Data()
    private var stderrThreadDone = DispatchSemaphore(value: 0)
    private var reaped = false

    /// Everything the server wrote to stderr, verbatim.
    ///
    /// §6 calls this the most useful thing you can show a user whose server is
    /// broken, and it is right: the real failure is nearly always in here —
    /// a missing dylib, a bad token, a package that no longer exists.
    var capturedStderr: String {
        stderrLock.lock()
        defer { stderrLock.unlock() }
        return String(decoding: stderrData, as: UTF8.self)
    }

    init(executable: String, arguments: [String], environment: [String: String]) throws {
        let resolved = try ManagedExecutable.resolve(executable, path: environment["PATH"] ?? "")
        resolvedExecutable = resolved

        var inPipe: [Int32] = [0, 0]
        var outPipe: [Int32] = [0, 0]
        var errPipe: [Int32] = [0, 0]
        guard pipe(&inPipe) == 0, pipe(&outPipe) == 0, pipe(&errPipe) == 0 else {
            throw ProcessError.spawnFailed(executable, code: errno)
        }

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, inPipe[0], STDIN_FILENO)
        posix_spawn_file_actions_adddup2(&actions, outPipe[1], STDOUT_FILENO)
        posix_spawn_file_actions_adddup2(&actions, errPipe[1], STDERR_FILENO)
        posix_spawn_file_actions_addclose(&actions, inPipe[1])
        posix_spawn_file_actions_addclose(&actions, outPipe[0])
        posix_spawn_file_actions_addclose(&actions, errPipe[0])

        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        // pgroup 0 makes the child the leader of a brand new group, so a single
        // kill(-pid) later reaches it and everything it started.
        posix_spawnattr_setpgroup(&attributes, 0)
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP))

        let argv: [String] = [resolved] + arguments
        let envp = environment.map { "\($0.key)=\($0.value)" }

        var cArgs: [UnsafeMutablePointer<CChar>?] = argv.map { strdup($0) }
        cArgs.append(nil)
        var cEnv: [UnsafeMutablePointer<CChar>?] = envp.map { strdup($0) }
        cEnv.append(nil)
        defer {
            for pointer in cArgs where pointer != nil { free(pointer) }
            for pointer in cEnv where pointer != nil { free(pointer) }
        }

        var spawned: pid_t = 0
        let result = posix_spawn(&spawned, resolved, &actions, &attributes, cArgs, cEnv)

        close(inPipe[0])
        close(outPipe[1])
        close(errPipe[1])

        guard result == 0 else {
            close(inPipe[1])
            close(outPipe[0])
            close(errPipe[0])
            throw ProcessError.spawnFailed(executable, code: result)
        }

        pid = spawned
        stdinFD = inPipe[1]
        stdoutFD = outPipe[0]
        stderrFD = errPipe[0]

        startDrainingStderr()
    }

    deinit {
        terminateGroup()
    }

    // MARK: - Talking

    func write(line: String) throws {
        let data = Array((line + "\n").utf8)
        var offset = 0
        while offset < data.count {
            let written = data.withUnsafeBufferPointer { buffer in
                Darwin.write(stdinFD, buffer.baseAddress! + offset, data.count - offset)
            }
            if written <= 0 {
                // EPIPE: the server is already gone. Its stderr says why.
                throw ProcessError.writeFailed
            }
            offset += written
        }
    }

    /// Reads one newline-terminated line, waiting no longer than `deadline`.
    ///
    /// Returns nil when the server closes stdout without sending anything more.
    func readLine(deadline: Date, timeout: TimeInterval) throws -> String? {
        while true {
            if let line = takeBufferedLine() { return line }

            let remaining = deadline.timeIntervalSinceNow
            guard remaining > 0 else { throw ProcessError.timedOut(after: timeout) }

            var descriptor = pollfd(fd: stdoutFD, events: Int16(POLLIN), revents: 0)
            let ready = poll(&descriptor, 1, Int32(min(remaining, 1) * 1000))
            if ready == 0 { continue }
            if ready < 0 {
                if errno == EINTR { continue }
                throw ProcessError.spawnFailed("read", code: errno)
            }

            var chunk = [UInt8](repeating: 0, count: 16 * 1024)
            let count = read(stdoutFD, &chunk, chunk.count)
            if count > 0 {
                stdoutBuffer.append(contentsOf: chunk[0..<count])
                continue
            }
            // EOF: hand back anything left, then report closure.
            if let line = takeBufferedLine() { return line }
            return nil
        }
    }

    private func takeBufferedLine() -> String? {
        guard let index = stdoutBuffer.firstIndex(of: 0x0A) else { return nil }
        let line = stdoutBuffer[stdoutBuffer.startIndex..<index]
        stdoutBuffer.removeSubrange(stdoutBuffer.startIndex...index)
        return String(decoding: line, as: UTF8.self)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\r"))
    }

    // MARK: - Lifecycle

    /// Kills the whole group and reaps the child, so no `node` outlives its `npx`.
    func terminateGroup() {
        guard !reaped else { return }
        reaped = true

        kill(-pid, SIGTERM)
        // A short grace period, then no more Mr Nice Guy.
        for _ in 0..<20 {
            var status: Int32 = 0
            if waitpid(pid, &status, WNOHANG) == pid { break }
            usleep(25_000)
        }
        kill(-pid, SIGKILL)
        var status: Int32 = 0
        _ = waitpid(pid, &status, 0)

        close(stdinFD)
        close(stdoutFD)
        _ = stderrThreadDone.wait(timeout: .now() + 1)
        close(stderrFD)
    }

    private func startDrainingStderr() {
        let fd = stderrFD
        Thread.detachNewThread { [stderrLock, stderrThreadDone] in
            var chunk = [UInt8](repeating: 0, count: 8 * 1024)
            while true {
                let count = read(fd, &chunk, chunk.count)
                guard count > 0 else { break }
                stderrLock.lock()
                // Cap it: a server in a crash loop can produce megabytes, and
                // only the first part is diagnostic.
                if self.stderrData.count < 64 * 1024 {
                    self.stderrData.append(contentsOf: chunk[0..<count])
                }
                stderrLock.unlock()
            }
            stderrThreadDone.signal()
        }
    }

}
