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
    GatewayRouteStore? routes = null;

    if (invocation.RouteID is { } routeId)
    {
        // The route is looked up before its secrets are read, so a session that then
        // cannot start is still journalled against its server and client: the client
        // itself only shows "server failed".
        routes = new GatewayRouteStore(invocation.RoutesPath ?? paths.GatewayRoutesFile);
        var known = routes.Route(routeId);
        command = known.Command;
        arguments = known.Arguments;
        extraEnvironment = new Dictionary<string, string>();
        serverId = known.ServerID;
        clientId = known.ClientID.Raw();
        eventLogPath = invocation.EventLogPath ?? paths.GatewayEventsFile;
        exposedTools = known.ExposedTools;
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

    // Losing the event log must not interrupt the client's session (docs/gateway.md):
    // if it cannot be opened, the session runs unlogged.
    using var eventSink = OpenEventLog(eventLogPath);
    IGatewayEventSink sink = eventSink is not null ? eventSink : new NullGatewayEventSink();
    var observer = new GatewayMessageObserver(serverId, clientId, sink);

    if (routes is not null && invocation.RouteID is { } resolvedId)
    {
        try
        {
            var launch = routes.Resolve(resolvedId, new WindowsCredentialStore("Kytto.Gateway"));
            command = launch.Route.Command;
            arguments = launch.Route.Arguments;
            extraEnvironment = launch.Environment;
            exposedTools = launch.Route.ExposedTools;
        }
        catch
        {
            observer.Fail(GatewayFailureReason.SecretUnavailable);
            throw;
        }
    }

    Process? started;
    try
    {
        var environment = ShellEnvironment.Current.With(extraEnvironment);
        var start = ManagedExecutable.StartInfo(command, arguments, environment);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        started = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {command}.");
    }
    catch
    {
        observer.Fail(GatewayFailureReason.LaunchFailed);
        throw;
    }
    using var process = started;
    using var job = KillOnCloseJob.Create(process);
    observer.Start();
    var client = new ClientPresence();

    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        client.MarkGone();
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
            line =>
            {
                if (filter.InspectClientLine(line) is not GatewayToolFilter.ClientDecision.Refuse refusal) return line;
                // Journalled so a refusal is visible in Live Activity: without it a
                // blocked call looks like nothing happened.
                foreach (var (toolName, requestId) in filter.HiddenCalls(line))
                {
                    observer.RecordRefusal(toolName, requestId);
                }
                return RefuseAndDrop(toClient, refusal.Response);
            },
            closeDestination: true,
            cancellation.Token,
            client);
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
            cancellation.Token,
            client);
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

    // The client closing its end (a crash, a force quit) is the only notice the helper
    // gets. The upstream's input is closed with it; a server that does not exit on that
    // gets a grace period, then is ended with everything it started.
    _ = clientToServer.ContinueWith(
        async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            TryKill(process);
        },
        TaskScheduler.Default);

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
    // Read before anything else can close the client's end: a server that is gone
    // while its client is still attached stopped on its own.
    var stoppedOnItsOwn = !client.IsGone;
    // Whatever the upstream started outlives it unless the job is closed too — and a
    // descendant still holding its stdout would keep the relays below waiting forever.
    job.Dispose();
    await Task.WhenAny(
        Task.WhenAll(IgnoreClosure(serverToClient), IgnoreClosure(stderr)),
        Task.Delay(TimeSpan.FromSeconds(1)));
    observer.Finish(process.ExitCode, unexpected: stoppedOnItsOwn);
    return process.ExitCode;
}
catch (Exception error)
{
    await Console.Error.WriteLineAsync($"Kytto gateway: {error.Message}\n\n{usage}");
    return 64;
}

static JsonLinesGatewayEventSink? OpenEventLog(string? path)
{
    if (path is null) return null;
    try
    {
        return new JsonLinesGatewayEventSink(path);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
        return null;
    }
}

/// <summary>Byte-for-byte forwarding — the §3.3 path, used whenever nothing is masked.</summary>
static async Task RelayAsync(
    Stream source,
    SynchronizedWriter destination,
    GatewayMessageObserver? observer,
    GatewayDirection direction,
    bool closeDestination,
    CancellationToken cancellation,
    ClientPresence? presence = null)
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
        // First, before the server's input is closed: a server that exits the moment
        // it sees EOF must already count as let go.
        presence?.MarkGone();
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
    CancellationToken cancellation,
    ClientPresence? presence = null)
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
        // First, before the server's input is closed (see RelayAsync).
        presence?.MarkGone();
        // A trailing partial message still belongs to the other side, and still goes
        // through the filter: servers that read line by line act on a final line
        // without its newline too.
        framer.Flush(tail =>
        {
            if (transform(tail) is { } forwarded) destination.Write(forwarded);
        });
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

/// <summary>Whether the client has let go — closed its end or interrupted the helper.</summary>
/// <remarks>A server that stops before that did so on its own.</remarks>
sealed class ClientPresence
{
    private volatile bool _gone;

    public void MarkGone() => _gone = true;

    public bool IsGone => _gone;
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
