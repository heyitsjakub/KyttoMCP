using Kytto.Core.Model;

namespace Kytto.Core.Health;

/// <summary>Runs a server and asks it what it can do (§7.3).</summary>
/// <remarks>
/// Never on launch and never in the background: §6 is firm that spawning arbitrary
/// commands is something the user asks for, one server at a time.
/// </remarks>
public sealed class HealthChecker(
    ShellEnvironment? shell = null,
    TimeSpan? timeout = null,
    McpStdioClient? client = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly ShellEnvironment _shell = shell ?? ShellEnvironment.Current;
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly McpStdioClient _client = client ?? new McpStdioClient();

    public (HealthResult Health, TokenWeight? TokenWeight) Check(Server server)
    {
        var started = Timestamp.Now;

        if (server.Transport != Transport.Stdio)
        {
            var reason = HealthFailureReason.RemoteTransport(server.Transport.Raw());
            // Remote servers are not spawned, so there is nothing to run. Saying
            // "not checked yet" would be a lie; saying why is not.
            return (new HealthResult(
                Status: HealthStatus.Unsupported,
                CheckedAt: started,
                ToolCount: null,
                Tools: [],
                Message: reason.CurrentMessage(),
                Stderr: null,
                DurationSeconds: 0,
                ServerName: null,
                ServerVersion: null)
                {
                    Failure = reason,
                }, null);
        }

        // Placeholders the owning client would expand — `${__dirname}` and friends
        // in a Claude Desktop extension — are resolved here. Running the literal
        // text would fail every extension for a reason that has nothing to do with
        // whether the server works.
        var (command, arguments) = server.Runnable;
        if (string.IsNullOrEmpty(command))
        {
            return (Failure(HealthFailureReason.NoCommand(), null, started), null);
        }
        if (server.HasRelativeCommand)
        {
            return (Failure(
                HealthFailureReason.RelativeCommand(command),
                null,
                started,
                HealthStatus.Unsupported), null);
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in server.Env)
        {
            if (entry.Value is { } value) extra.TryAdd(entry.Key, value);
        }

        try
        {
            var (listing, stderr) = _client.ListTools(
                command, arguments, _shell.With(extra), _timeout);

            return (new HealthResult(
                Status: HealthStatus.Passed,
                CheckedAt: started,
                ToolCount: listing.Tools.Count,
                Tools: listing.Tools,
                Message: null,
                // Servers commonly log to stderr while working perfectly. Keeping
                // it on success too means the detail view can show warnings that
                // are not failures.
                Stderr: stderr.Length == 0 ? null : stderr,
                DurationSeconds: (Timestamp.Now - started).TotalSeconds,
                ServerName: listing.ServerName,
                ServerVersion: listing.ServerVersion)
                {
                    ProtocolVersion = listing.NegotiatedProtocolVersion,
                    CapabilityNames = listing.CapabilityNames,
                    PromptCount = listing.PromptCount,
                    ResourceCount = listing.ResourceCount,
                    InspectionNotes = listing.InspectionNotes,
                    ResolvedCommand = listing.ResolvedCommand,
                    EnvironmentSource = _shell.SourceDisplay,
                },
                TokenWeight.Measuring(listing.RawToolsJson));
        }
        catch (HealthFailure failure)
        {
            var reason = ReasonFor(failure.Underlying);
            var stderr = failure.Stderr.Length == 0 ? null : failure.Stderr;
            string? authorizationURL = null;
            var status = HealthStatus.Failed;
            if (AuthorizationSignal.IsFailedHandshake(reason) &&
                AuthorizationSignal.TryDetect(stderr, out authorizationURL))
            {
                reason = HealthFailureReason.AwaitingAuthorization();
                status = HealthStatus.NeedsAuthorization;
            }
            return (Failure(reason, stderr, started, status, authorizationURL) with
                {
                    ResolvedCommand = failure.ResolvedCommand,
                    EnvironmentSource = _shell.SourceDisplay,
                }, null);
        }
        catch (Exception error)
        {
            return (Failure(ReasonFor(error), null, started), null);
        }
    }

    private static HealthResult Failure(
        HealthFailureReason reason,
        string? stderr,
        DateTimeOffset started,
        HealthStatus status = HealthStatus.Failed,
        string? authorizationURL = null) => new(
        Status: status,
        CheckedAt: started,
        ToolCount: null,
        Tools: [],
        Message: reason.CurrentMessage(),
        Stderr: stderr,
        DurationSeconds: (Timestamp.Now - started).TotalSeconds,
        ServerName: null,
        ServerVersion: null)
        {
            Failure = reason,
            AuthorizationURL = authorizationURL,
        };

    private HealthFailureReason ReasonFor(Exception error)
    {
        if (error is ProcessException process)
        {
            return process.Failure.Reason == "commandNotFound"
                ? process.Failure with { SearchedPreferredPATH = _shell.PreferredPathAvailable }
                : process.Failure;
        }
        if (error is McpException mcp) return mcp.Failure;
        if (error is IOException or ObjectDisposedException) return HealthFailureReason.InputClosed();
        return HealthFailureReason.Other(error.Message);
    }
}
