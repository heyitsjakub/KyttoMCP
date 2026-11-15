using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kytto.Core.Health;

public sealed class ProcessException(HealthFailureReason failure) : Exception(failure.CurrentMessage())
{
    public HealthFailureReason Failure { get; } = failure;

    public static ProcessException CommandNotFound(string command, string searchedPath) =>
        new(HealthFailureReason.CommandNotFound(command, searchedPath));

    public static ProcessException ExecutableMissing(string command, string resolvedPath) =>
        new(HealthFailureReason.ExecutableMissing(command, resolvedPath));

    public static ProcessException SpawnFailed(string command, int code) =>
        new(HealthFailureReason.SpawnFailed(command, code));

    /// <summary>The definition's own working directory is not somewhere Kytto can start it.</summary>
    public static ProcessException InvalidWorkingDirectory() =>
        new(HealthFailureReason.InvalidWorkingDirectory());

    /// <summary>An argument cannot reach a batch file exactly as written.</summary>
    public static ProcessException UnsafeArgument() =>
        new(HealthFailureReason.UnsafeArgument());

    public static ProcessException TimedOut(double seconds) =>
        new(HealthFailureReason.TimedOut(seconds));
}

/// <summary>
/// A spawned MCP server, and the guarantee that it dies with us.
/// </summary>
/// <remarks>
/// <para>
/// §6: health checks run arbitrary commands, so every spawn is timed out and
/// killed — and killed <em>with its children</em>. macOS says that with a process
/// group; Windows has no such thing, so the process goes into a Job Object with
/// <c>KILL_ON_JOB_CLOSE</c>. Closing the handle then takes the whole tree down
/// even if Kytto itself is killed, which a manual walk of the tree cannot promise.
/// It matters here more than usual: almost every MCP server is <c>npx</c>, which
/// exists only to spawn <c>node</c> and would otherwise outlive it.
/// </para>
/// <para>
/// The other Windows fact this hides: <c>npx</c> is not a program. It is
/// <c>npx.cmd</c>, a batch shim, and a batch file cannot be started directly —
/// it needs a command interpreter. So the resolved command decides whether the
/// process is the program or <c>cmd.exe</c> running it.
/// </para>
/// </remarks>
public sealed class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly IntPtr _job;
    private readonly StringBuilder _stderr = new();
    private readonly Lock _stderrLock = new();
    private readonly StringBuilder _stdout = new();
    private readonly char[] _chunk = new char[64 * 1024];
    private Task<int>? _pendingRead;
    private int _scannedTo;
    private bool _stdoutClosed;
    private bool _disposed;

    /// <summary>The longest stdout line accepted, in characters.</summary>
    /// <remarks>
    /// stderr is capped; stdout cannot be truncated without breaking the message,
    /// so a line this long ends the check instead of growing without bound.
    /// </remarks>
    internal const int MaxLineChars = 32 * 1_048_576;

    public string ResolvedExecutable { get; }

    /// <summary>
    /// What the timeout message says. Reported from the budget the caller set
    /// rather than from how much of it happens to be left, which would say
    /// "did not answer within 0 seconds".
    /// </summary>
    private double _timeoutSeconds = HealthChecker.DefaultTimeout.TotalSeconds;

    /// <summary>Told to the process so a timeout can name the budget it blew.</summary>
    public void SetTimeoutBudget(TimeSpan timeout) => _timeoutSeconds = timeout.TotalSeconds;

    /// <param name="workingDirectory">
    /// The directory the owning client or plugin registration established for this
    /// server, or null. Anything given must be an existing absolute directory: a server
    /// whose directory is missing is not started somewhere else instead, which would
    /// answer a different question than the client's (§4).
    /// </param>
    public ManagedProcess(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory = null)
    {
        if (workingDirectory is not null
            && !(Path.IsPathFullyQualified(workingDirectory) && Directory.Exists(workingDirectory)))
        {
            throw ProcessException.InvalidWorkingDirectory();
        }

        var resolved = ManagedExecutable.Resolve(executable, environment, workingDirectory);
        ResolvedExecutable = resolved.Path;

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        start.FileName = resolved.FileName;
        ManagedExecutable.Apply(start, resolved, arguments, environment);
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;

        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;

        _job = CreateKillOnCloseJob();

        try
        {
            _process = Process.Start(start)
                ?? throw ProcessException.CommandNotFound(
                    executable,
                    environment.GetValueOrDefault("PATH") ?? environment.GetValueOrDefault("Path") ?? "");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            CloseJob();
            throw ProcessException.SpawnFailed(executable, error.NativeErrorCode);
        }

        if (_job != IntPtr.Zero) AssignProcessToJobObject(_job, _process.Handle);

        // Read on a thread rather than polling: a server that fills the stderr
        // pipe and is never drained blocks forever, which would turn a noisy
        // server into a timeout and report the wrong problem.
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                _stderrEnded.TrySetResult();
                return;
            }
            lock (_stderrLock) _stderr.AppendLine(args.Data);
        };
        _process.BeginErrorReadLine();
    }

    private readonly TaskCompletionSource _stderrEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _terminated;
    private bool _jobClosed;

    /// <summary>How long a server whose input was closed gets to exit on its own.</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>Ends the server and everything it started; afterwards <see cref="CapturedStderr"/> is complete.</summary>
    /// <remarks>
    /// The MCP stdio shutdown: close the server's input first — the portable "please
    /// exit" — and give it a moment, then kill the tree. The job is closed whatever
    /// happened, so descendants of a server that exited on its own are not left
    /// behind. Only then is stderr read to its end: the last lines a crashing server
    /// writes are the ones that explain it.
    /// </remarks>
    public void Terminate()
    {
        if (_terminated || _disposed) return;
        _terminated = true;

        // A server holding a write it never drains is not going to read the closing of
        // its input either, and closing the stream under a blocked write is not safe.
        // It is ended straight away; the kill breaks the pipe and releases the write.
        var stuck = WriteIsPending;
        if (!stuck)
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed by a server that went away, which is the point.
            }
        }

        try
        {
            if (stuck || !_process.WaitForExit(ExitGrace)) _process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or
                                              System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }

        CloseJob();
        // Every holder of the stderr pipe is gone with the job, so its end arrives
        // promptly; the bound only covers a handle something outside the job kept.
        _stderrEnded.Task.Wait(TimeSpan.FromSeconds(1));
        if (stuck)
        {
            // The broken pipe fails the blocked write; waited for so the process's
            // own disposal does not close the stream underneath it, and observed so
            // it is not reported as an unobserved task exception.
            ((IAsyncResult)_pendingWrite).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            _ = _pendingWrite.Exception;
        }
    }

    /// <summary>The server's own output so far, exactly as it wrote it (§6).</summary>
    public string CapturedStderr
    {
        get
        {
            lock (_stderrLock) return _stderr.ToString();
        }
    }

    /// <summary>The last write handed to the pipe, which may still be blocked in it.</summary>
    private Task _pendingWrite = Task.CompletedTask;

    /// <summary>Writes one message, giving up at <paramref name="deadline"/> (AUD-04).</summary>
    /// <remarks>
    /// <para>
    /// A pipe write blocks once the server stops reading and the pipe is full, and
    /// an anonymous pipe has no timeout of its own. Answering a server's request with
    /// a 256 KB id it never drains held a one-second check for as long as the server
    /// cared to sleep. The write therefore runs off this thread and is waited for only
    /// until the deadline; the job kill in <see cref="Terminate"/> is what finally
    /// releases it.
    /// </para>
    /// <para>
    /// Writes are chained, so one that timed out and is still pending cannot be
    /// overtaken by the next: interleaved bytes would corrupt both messages. UTF-8,
    /// as MCP requires, rather than the console code page the default writer uses.
    /// </para>
    /// </remarks>
    public void WriteLine(string line, DateTimeOffset deadline)
    {
        var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
        var stream = _process.StandardInput.BaseStream;
        var write = _pendingWrite.ContinueWith(
            _ =>
            {
                stream.Write(bytes);
                stream.Flush();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        _pendingWrite = write;

        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) ((IAsyncResult)write).AsyncWaitHandle.WaitOne(remaining);
        if (!write.IsCompleted) throw ProcessException.TimedOut(_timeoutSeconds);
        // The pipe's own exception, not an AggregateException, so callers can tell a
        // server that closed its input from anything else.
        write.GetAwaiter().GetResult();
    }

    /// <summary>Whether a write is still blocked in the pipe.</summary>
    private bool WriteIsPending => !_pendingWrite.IsCompleted;

    /// <summary>
    /// The next line of stdout, or null when the server closed it. Throws once the
    /// deadline passes.
    /// </summary>
    public string? ReadLine(DateTimeOffset deadline)
    {
        while (true)
        {
            if (TakeBufferedLine() is { } line) return line;
            if (_stdoutClosed)
            {
                if (_stdout.Length == 0) return null;
                var rest = _stdout.ToString();
                _stdout.Clear();
                _scannedTo = 0;
                return rest;
            }
            if (_stdout.Length > MaxLineChars)
            {
                throw new ProcessException(HealthFailureReason.ProtocolError(
                    $"the server wrote more than {MaxLineChars / 1_048_576} MB without ending a message"));
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw ProcessException.TimedOut(_timeoutSeconds);

            // The read is left running rather than cancelled: a pipe read that is
            // abandoned mid-chunk would desynchronise the stream, and the process is
            // about to be killed by the job object anyway. The next call picks it up.
            _pendingRead ??= _process.StandardOutput.ReadAsync(_chunk, 0, _chunk.Length);
            if (!_pendingRead.Wait(remaining))
            {
                throw ProcessException.TimedOut(_timeoutSeconds);
            }
            var count = _pendingRead.Result;
            _pendingRead = null;
            if (count == 0) _stdoutClosed = true;
            else _stdout.Append(_chunk, 0, count);
        }
    }

    /// <summary>One complete line from what has been read, without its terminator.</summary>
    private string? TakeBufferedLine()
    {
        for (var index = _scannedTo; index < _stdout.Length; index++)
        {
            if (_stdout[index] != '\n') continue;
            var end = index > 0 && _stdout[index - 1] == '\r' ? index - 1 : index;
            var line = _stdout.ToString(0, end);
            _stdout.Remove(0, index + 1);
            _scannedTo = 0;
            return line;
        }
        _scannedTo = _stdout.Length;
        return null;
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public int ExitCode => _process.ExitCode;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or
                                              System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }

        // Closing the job is what guarantees it, whatever the kill above did.
        CloseJob();
        _process.Dispose();
    }

    // MARK: - Job object

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var information = new JobObjectExtendedLimit
        {
            BasicLimitInformation = new JobObjectBasicLimit { LimitFlags = LimitKillOnJobClose },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimit>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;
    }

    private void CloseJob()
    {
        if (_jobClosed) return;
        _jobClosed = true;
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
