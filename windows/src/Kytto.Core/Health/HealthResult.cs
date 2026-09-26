using System.Text.Json.Serialization;
using Kytto.Core.Tokenizer;

namespace Kytto.Core.Health;

/// <summary>What a server's tool definitions cost in context (§7.4).</summary>
public sealed record TokenWeight(int Estimate, string Method, DateTimeOffset MeasuredAt)
{
    /// <summary>
    /// A context window to measure against. 200k is what the UI shows a percentage
    /// of; it is a yardstick, not a claim about any specific model.
    /// </summary>
    public const int ReferenceContextWindow = 200_000;

    internal const string EstimateMethod = "chars/4";

    [JsonIgnore]
    public double PercentOfContext => (double)Estimate / ReferenceContextWindow * 100;

    /// <summary>
    /// Whether <see cref="Estimate"/> came from a real vocabulary rather than a
    /// heuristic. The UI stops saying "estimate" when this is true, and only then.
    /// </summary>
    [JsonIgnore]
    public bool IsMeasured => Method != EstimateMethod;

    /// <summary>Counts <paramref name="text"/> with the bundled cl100k_base vocabulary (§7.4).</summary>
    /// <remarks>
    /// Falls back to the old character heuristic if the table did not load, and
    /// says so in <see cref="Method"/> rather than passing an approximation off as
    /// a measurement.
    /// </remarks>
    public static TokenWeight Measuring(string text, DateTimeOffset? at = null) =>
        BpeTokenizer.Cl100kBase is { } tokenizer
            ? new(
                Estimate: tokenizer.CountTokens(text),
                Method: BpeTokenizer.Cl100kMethod,
                MeasuredAt: at ?? Timestamp.Now)
            : Estimating(text, at);

    /// <summary>Characters divided by four — the pre-vocabulary heuristic.</summary>
    /// <remarks>
    /// Kept only as the fallback for a build whose rank table is missing. It
    /// overstates a realistic tool definition by roughly a fifth, which is why it is
    /// no longer the default.
    /// </remarks>
    public static TokenWeight Estimating(string text, DateTimeOffset? at = null) =>
        new(
            Estimate: (int)Math.Round(text.Length / 4.0, MidpointRounding.AwayFromZero),
            Method: EstimateMethod,
            MeasuredAt: at ?? Timestamp.Now);
}

public enum HealthStatus
{
    Passed,
    Failed,
    /// <summary>Not something Kytto can check yet — a remote server, for now.</summary>
    Unsupported,
    /// <summary>The handshake reached an interactive browser sign-in and is waiting.</summary>
    NeedsAuthorization,
}

/// <summary>Stable data describing why a health check did not pass.</summary>
/// <remarks>
/// The snapshot <see cref="HealthResult.Message"/> remains on disk for old builds,
/// while current UI copy is composed from this record at the IPC boundary. That
/// keeps wording fixes from being frozen into every cached result (§7.3).
/// </remarks>
public sealed record HealthFailureReason(string Reason)
{
    public string? Transport { get; init; }
    public string? Command { get; init; }
    public string? SearchedPath { get; init; }
    public bool? SearchedPreferredPATH { get; init; }
    public string? ResolvedPath { get; init; }
    public string? Path { get; init; }
    public int? Code { get; init; }
    public double? Seconds { get; init; }
    public int? Status { get; init; }
    public string? Detail { get; init; }
    public string? CustomMessage { get; init; }

    public static HealthFailureReason NoCommand() => new("noCommand");

    public static HealthFailureReason RemoteTransport(string transport) =>
        new("remoteTransport") { Transport = transport };

    public static HealthFailureReason CommandNotFound(
        string command,
        string searchedPath,
        bool searchedPreferredPATH = false) => new("commandNotFound")
        {
            Command = command,
            SearchedPath = searchedPath,
            SearchedPreferredPATH = searchedPreferredPATH,
        };

    public static HealthFailureReason RelativeCommand(string command) =>
        new("relativeCommand") { Command = command };

    public static HealthFailureReason ExecutableMissing(string command, string resolvedPath) =>
        new("executableMissing") { Command = command, ResolvedPath = resolvedPath };

    public static HealthFailureReason NotExecutable(string path) =>
        new("notExecutable") { Path = path };

    public static HealthFailureReason SpawnFailed(string command, int code) =>
        new("spawnFailed") { Command = command, Code = code };

    public static HealthFailureReason InputClosed() => new("inputClosed");

    public static HealthFailureReason TimedOut(double seconds) =>
        new("timedOut") { Seconds = seconds };

    public static HealthFailureReason Exited(int status) =>
        new("exited") { Status = status };

    public static HealthFailureReason ProtocolError(string detail) =>
        new("protocolError") { Detail = detail };

    public static HealthFailureReason ServerRefused(int code, string message) =>
        new("serverRefused") { Code = code, CustomMessage = message };

    public static HealthFailureReason ClosedBeforeAnswering() => new("closedBeforeAnswering");

    public static HealthFailureReason AwaitingAuthorization() => new("awaitingAuthorization");

    public static HealthFailureReason Other(string message) =>
        new("other") { CustomMessage = message };

    /// <summary>The current user-facing wording for this structured reason.</summary>
    public string CurrentMessage() => Reason switch
    {
        "noCommand" => "This server has no command to run.",
        "remoteTransport" =>
            $"Kytto cannot check remote servers yet — this one is reached over {Transport}.",
        "commandNotFound" => CommandNotFoundMessage(),
        "relativeCommand" =>
            $"Kytto cannot safely check the relative command {Command}. The owning client " +
            "resolves it from its own working directory, so the server may run there even " +
            "though Kytto cannot reproduce that location.",
        "executableMissing" => $"{Command} was not found at {ResolvedPath}.",
        "notExecutable" => $"Kytto could not start {Path}; Windows did not accept it as an executable.",
        "spawnFailed" => $"Could not start {Command} (error {Code}).",
        "inputClosed" =>
            "The server closed its input before Kytto could finish talking to it.",
        "timedOut" => TimeoutMessage(Seconds ?? 0),
        "exited" => $"The server exited with status {Status} before answering.",
        "protocolError" => $"The server did not speak MCP as expected: {Detail}",
        "serverRefused" => $"The server refused the request ({Code}): {CustomMessage}",
        "closedBeforeAnswering" => "The server exited before answering.",
        "awaitingAuthorization" =>
            "The server is waiting for you to authorize it in the browser, not broken. " +
            "Complete the sign-in it asked for, then check it again.",
        "other" => CustomMessage ?? "The health check failed.",
        _ => CustomMessage ?? "The health check failed.",
    };

    private string CommandNotFoundMessage()
    {
        var source = SearchedPreferredPATH == true
            ? "the user + system PATH"
            : "its process PATH because the user + system PATH could not be read";
        return $"{Command} was not found. Kytto searched {source}: {SearchedPath}";
    }

    private static string TimeoutMessage(double seconds)
    {
        var displayed = Math.Max(1, (int)Math.Ceiling(seconds));
        var unit = displayed == 1 ? "second" : "seconds";
        return $"The server did not answer within {displayed} {unit}.";
    }
}

public sealed record ToolAnnotations(
    bool? ReadOnlyHint = null,
    bool? DestructiveHint = null,
    bool? IdempotentHint = null,
    bool? OpenWorldHint = null);

/// <param name="TokenCount">
/// What this one tool's entry costs, measured on the exact bytes the server sent
/// (§7.4).
/// <para>
/// Optional because health records written before per-tool measurement existed
/// have to stay readable, and because a build without the rank table has no
/// business inventing one.
/// </para>
/// <para>
/// Deliberately <em>not</em> compared by <c>ContractGuard</c>, which asks what the
/// model was told — a tool whose cost moved by two tokens has not changed its
/// contract.
/// </para>
/// </param>
public sealed record ToolSummary(
    string Name,
    string? Description,
    string? InputSchemaJSON = null,
    ToolAnnotations? Annotations = null,
    int? TokenCount = null);

/// <summary>The outcome of asking a server whether it works (§7.3).</summary>
public sealed record HealthResult(
    HealthStatus Status,
    DateTimeOffset CheckedAt,
    int? ToolCount,
    IReadOnlyList<ToolSummary> Tools,
    /// <summary>A one-line explanation, in the user's terms.</summary>
    string? Message,
    /// <summary>
    /// The server's own output, verbatim and untouched. §6 calls this the most
    /// useful thing you can show someone whose server is broken. It is not
    /// summarised, cleaned up or interpreted.
    /// </summary>
    string? Stderr,
    double DurationSeconds,
    string? ServerName,
    string? ServerVersion)
{
    // Derived, so they must not reach the metadata file. A stored copy is a second
    // source of truth that a hand-edited `status` would silently contradict.
    [JsonIgnore]
    public bool IsPassing => Status == HealthStatus.Passed;

    // Init properties keep old metadata valid: missing JSON fields retain these
    // defaults when an existing installation is upgraded.
    public string? ProtocolVersion { get; init; }
    public IReadOnlyList<string> CapabilityNames { get; init; } = [];
    public int? PromptCount { get; init; }
    public int? ResourceCount { get; init; }
    public IReadOnlyList<string> InspectionNotes { get; init; } = [];
    public string? ResolvedCommand { get; init; }
    public string? EnvironmentSource { get; init; }
    /// <summary>Structured reason used to render current wording; null for legacy records.</summary>
    public HealthFailureReason? Failure { get; init; }
    /// <summary>A validated browser page captured from server stderr, if any.</summary>
    public string? AuthorizationURL { get; init; }

    [JsonIgnore]
    public string? CurrentMessage => Failure?.CurrentMessage() ?? Message;

    [JsonIgnore]
    public string StatusRaw => Status switch
    {
        HealthStatus.Passed => "passed",
        HealthStatus.Failed => "failed",
        HealthStatus.NeedsAuthorization => "needsAuthorization",
        _ => "unsupported",
    };
}

/// <summary>
/// Everything Kytto has learned about a server that is not in a config file.
/// </summary>
/// <remarks>
/// Config files stay authoritative for what is <em>configured</em> (§5); this is
/// the measured half, and it lives in Kytto's own store.
/// </remarks>
public sealed record ServerMetadata(HealthResult? Health = null, TokenWeight? TokenWeight = null)
{
    // Optional init properties keep metadata written by older builds readable.
    public IReadOnlyList<ToolSummary>? ContractTools { get; init; }
    public IReadOnlyList<ContractChange>? ContractChanges { get; init; }
    public DateTimeOffset? ContractChangedAt { get; init; }
    /// <summary>Explicit latest-version checks survive a relaunch.</summary>
    public Model.ServerProvenance? Provenance { get; init; }
}

public static class Timestamp
{
    /// <summary>
    /// This instant, rounded to whole milliseconds.
    /// </summary>
    /// <remarks>
    /// Timestamps here are written to disk as ISO-8601, which carries at most
    /// millisecond precision. Rounding on the way in rather than losing digits on
    /// the way out means a value read back is the value that was stored — worth
    /// having when these get compared.
    /// </remarks>
    public static DateTimeOffset Now =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
