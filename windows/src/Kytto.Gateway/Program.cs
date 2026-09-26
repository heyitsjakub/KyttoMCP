using System.Diagnostics;
using System.Runtime.InteropServices;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;
using Kytto.Core.Health;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;

const string usage = """
Usage:
  kytto-mcp-proxy --route UUID
  kytto-mcp-proxy [--server-id ID] [--client-id ID] [--event-log PATH] -- COMMAND [ARG ...]

Forwards MCP stdio byte-for-byte unless the route restricts which tools it
exposes, in which case tools/list results are filtered and calls to hidden tools
are refused here. The event log records session and tool-call metadata only;
arguments, responses, environment values and stderr are never stored.
""";

try
{
    var invocation = GatewayInvocation.Parse(args);
    var paths = new KyttoPaths();
    string command;
    IReadOnlyList<string> arguments;
    IReadOnlyDictionary<string, string> extraEnvironment;
    string serverId;
    string clientId;
    string? eventLogPath;
    IReadOnlyList<string>? exposedTools = null;

    if (invocation.RouteID is { } routeId)
    {
        var routes = new GatewayRouteStore(invocation.RoutesPath ?? paths.GatewayRoutesFile);
        var launch = routes.Resolve(routeId, new WindowsCredentialStore("Kytto.Gateway"));
        command = launch.Route.Command;
        arguments = launch.Route.Arguments;
        extraEnvironment = launch.Environment;
        serverId = launch.Route.ServerID;
        clientId = launch.Route.ClientID.Raw();
        eventLogPath = invocation.EventLogPath ?? paths.GatewayEventsFile;
        exposedTools = launch.Route.ExposedTools;
    }
    else
    {
        command = invocation.Command ?? throw GatewayInvocationException.MissingCommand();
        arguments = invocation.Arguments;
        extraEnvironment = new Dictionary<string, string>();
        serverId = invocation.ServerID;
        clientId = invocation.ClientID;
        eventLogPath = invocation.EventLogPath;
    }

    using var eventSink = eventLogPath is null ? null : new JsonLinesGatewayEventSink(eventLogPath);
    IGatewayEventSink sink = eventSink is not null ? eventSink : new NullGatewayEventSink();
    var observer = new GatewayMessageObserver(serverId, clientId, sink);

    var environment = ShellEnvironment.Current.With(extraEnvironment);
    var start = ManagedExecutable.StartInfo(command, arguments, environment);
    start.RedirectStandardInput = true;
    start.RedirectStandardOutput = true;
    start.RedirectStandardError = true;

    using var process = Process.Start(start)
        ?? throw new InvalidOperationException($"Could not start {command}.");
    using var job = KillOnCloseJob.Create(process);
    observer.Start();

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
        TryKill(process);
    };

    var toClient = new SynchronizedWriter(Console.OpenStandardOutput());
    var toServer = new SynchronizedWriter(process.StandardInput.BaseStream);

    Task clientToServer;
    Task serverToClient;
    if (exposedTools is not null)
    {
        // A narrowed route. Both directions are read message by message so the tool
        // list can be filtered and calls to hidden tools can be answered here
        // (§7.11).
        var filter = new GatewayToolFilter(exposedTools);
        clientToServer = RelayMessagesAsync(
            Console.OpenStandardInput(),
            toServer,
            observer,
            GatewayDirection.ClientToServer,
            line => filter.InspectClientLine(line) switch
            {
                GatewayToolFilter.ClientDecision.Refuse refusal =>
                    RefuseAndDrop(toClient, refusal.Response),
                _ => line,
            },
            closeDestination: true,
            cancellation.Token);
        serverToClient = RelayMessagesAsync(
            process.StandardOutput.BaseStream,
            toClient,
            observer,
            GatewayDirection.ServerToClient,
            filter.RewriteServerLine,
            closeDestination: false,
            cancellation.Token);
    }
    else
    {
        clientToServer = RelayAsync(
            Console.OpenStandardInput(),
            toServer,
            observer,
            GatewayDirection.ClientToServer,
            closeDestination: true,
            cancellation.Token);
        serverToClient = RelayAsync(
            process.StandardOutput.BaseStream,
            toClient,
            observer,
            GatewayDirection.ServerToClient,
            closeDestination: false,
            cancellation.Token);
    }

    var stderr = RelayAsync(
        process.StandardError.BaseStream,
        new SynchronizedWriter(Console.OpenStandardError()),
        null,
        GatewayDirection.ServerToClient,
        closeDestination: false,
        cancellation.Token);

    try
    {
        await process.WaitForExitAsync(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        // Ctrl+C cancels the relays and kills the upstream. WaitForExitAsync gives
        // cancellation precedence even when that kill has already completed; if it
        // escaped to the outer catch, an ordinary shutdown printed usage and
        // returned 64 as though the command line were invalid.
        TryKill(process);
        await process.WaitForExitAsync();
    }
    await Task.WhenAll(IgnoreClosure(serverToClient), IgnoreClosure(stderr));
    observer.Finish(process.ExitCode);
    return process.ExitCode;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync($"Kytto gateway: {error.Message}\n\n{usage}");
    return 64;
}

/// <summary>Byte-for-byte forwarding — the §3.3 path, used whenever nothing is masked.</summary>
static async Task RelayAsync(
    Stream source,
    SynchronizedWriter destination,
    GatewayMessageObserver? observer,
    GatewayDirection direction,
    bool closeDestination,
    CancellationToken cancellation)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            observer?.Accept(buffer.AsSpan(0, count), direction);
            destination.Write(buffer.AsSpan(0, count));
        }
    }
    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
    {
        // Closure in either direction is normal process teardown.
    }
    finally
    {
        if (closeDestination) destination.Close();
    }
}

/// <summary>Message-by-message forwarding, used only by a route with an allow list.</summary>
/// <remarks>
/// <paramref name="transform"/> returns the bytes to forward, or null to forward
/// nothing — in which case it has already answered the sender itself.
/// </remarks>
static async Task RelayMessagesAsync(
    Stream source,
    SynchronizedWriter destination,
    GatewayMessageObserver observer,
    GatewayDirection direction,
    Func<byte[], byte[]?> transform,
    bool closeDestination,
    CancellationToken cancellation)
{
    var framer = new GatewayLineFramer();
    var buffer = new byte[64 * 1024];
    try
    {
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellation);
            if (count == 0) break;
            framer.Consume(buffer.AsSpan(0, count), line =>
            {
                // The observer sees what actually crossed, so a refused call is
                // never recorded as having reached the server.
                if (transform(line) is not { } forwarded) return;
                observer.Accept([.. forwarded, (byte)'\n'], direction);
                destination.WriteLine(forwarded);
            });
        }
    }
    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
    {
        // Closure in either direction is normal process teardown.
    }
    finally
    {
        // A trailing partial message still belongs to the other side.
        framer.Flush(tail => destination.Write(tail));
        if (closeDestination) destination.Close();
    }
}

/// <summary>Writes a refusal back to the client and forwards nothing upstream.</summary>
static byte[]? RefuseAndDrop(SynchronizedWriter toClient, byte[] response)
{
    toClient.WriteLine(response);
    return null;
}

static async Task IgnoreClosure(Task task)
{
    try { await task; }
    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
}

static void TryKill(Process process)
{
    try
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
}

/// <summary>
/// One writer per stream, so the two tasks that can both answer the client — the
/// upstream relay and a refusal from the filter — cannot interleave halfway through
/// a message.
/// </summary>
sealed class SynchronizedWriter(Stream stream)
{
    private readonly Lock _lock = new();

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            try
            {
                stream.Write(data);
                stream.Flush();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The other end went away; teardown is already under way.
            }
        }
    }

    /// <summary>A complete MCP message: exactly one line.</summary>
    public void WriteLine(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        Write([.. data, (byte)'\n']);
    }

    public void Close()
    {
        lock (_lock)
        {
            try { stream.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}

/// <summary>
/// Keeps the complete MCP process tree tied to the lifetime of the gateway.
/// If the client force-kills the helper, Windows closes this handle and terminates
/// descendants such as the <c>node</c> process started by <c>npx.cmd</c>.
/// </summary>
sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;
    private IntPtr _handle;

    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    public static KillOnCloseJob Create(Process process)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return new KillOnCloseJob(IntPtr.Zero);

        var information = new JobObjectExtendedLimit
        {
            BasicLimitInformation = new JobObjectBasicLimit { LimitFlags = LimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimit>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, pointer, (uint)size) ||
                !AssignProcessToJobObject(handle, process.Handle))
            {
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return new KillOnCloseJob(handle);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
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
