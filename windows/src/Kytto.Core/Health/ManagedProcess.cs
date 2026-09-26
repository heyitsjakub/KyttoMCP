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
    private bool _disposed;

    public string ResolvedExecutable { get; }

    /// <summary>
    /// What the timeout message says. Reported from the budget the caller set
    /// rather than from how much of it happens to be left, which would say
    /// "did not answer within 0 seconds".
    /// </summary>
    private double _timeoutSeconds = HealthChecker.DefaultTimeout.TotalSeconds;

    /// <summary>Told to the process so a timeout can name the budget it blew.</summary>
    public void SetTimeoutBudget(TimeSpan timeout) => _timeoutSeconds = timeout.TotalSeconds;

    public ManagedProcess(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var resolved = ManagedExecutable.Resolve(executable, environment);
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
        foreach (var argument in resolved.PrefixArguments) start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

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
            if (args.Data is null) return;
            lock (_stderrLock) _stderr.AppendLine(args.Data);
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>The server's own output so far, exactly as it wrote it (§6).</summary>
    public string CapturedStderr
    {
        get
        {
            lock (_stderrLock) return _stderr.ToString();
        }
    }

    public void WriteLine(string line)
    {
        _process.StandardInput.Write(line);
        _process.StandardInput.Write('\n');
        _process.StandardInput.Flush();
    }

    /// <summary>
    /// The next line of stdout, or null when the server closed it. Throws once the
    /// deadline passes.
    /// </summary>
    public string? ReadLine(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw ProcessException.TimedOut(_timeoutSeconds);

        // The read is left running rather than cancelled: a pipe read that is
        // abandoned mid-line would desynchronise the stream, and the process is
        // about to be killed by the job object anyway.
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(remaining))
        {
            throw ProcessException.TimedOut(_timeoutSeconds);
        }
        return read.Result;
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
