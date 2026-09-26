using System.Globalization;
using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Health;

namespace Kytto.Core.Model;

public enum Transport
{
    Stdio,
    Http,
    Sse,
}

public static class Transports
{
    public static string Raw(this Transport transport) => transport switch
    {
        Transport.Stdio => "stdio",
        Transport.Http => "http",
        Transport.Sse => "sse",
        _ => "stdio",
    };

    public static Transport? FromRaw(string? raw) => raw?.ToLowerInvariant() switch
    {
        "stdio" => Transport.Stdio,
        "http" => Transport.Http,
        "sse" => Transport.Sse,
        _ => null,
    };
}

/// <summary>Whether a server is on in a given client.</summary>
public enum Enablement
{
    /// <summary>Configured and active.</summary>
    Enabled,

    /// <summary>
    /// Configured but switched off — Claude Code's deny list, or an extension with
    /// <c>isEnabled: false</c>. Still listed, shown as off.
    /// </summary>
    Disabled,

    /// <summary>Not configured in this client at all.</summary>
    Absent,
}

public static class Enablements
{
    public static string Raw(this Enablement value) => value switch
    {
        Enablement.Enabled => "enabled",
        Enablement.Disabled => "disabled",
        _ => "absent",
    };
}

/// <summary>One environment variable as it appears in a config.</summary>
/// <remarks>
/// The value is kept because spawning the process needs it, but it never crosses
/// the IPC boundary — see <c>ServerDto</c> (§6).
/// </remarks>
public sealed record EnvEntry(string Key, string? Value)
{
    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>Where a server definition was found.</summary>
public abstract record Origin
{
    public sealed record ConfigFile(ClientKey Client) : Origin;

    public sealed record ClaudeDesktopExtension(string BundleId) : Origin;

    /// <summary>The spelling <c>docs/ipc.md</c> gives this on the wire.</summary>
    public string Kind => this is ClaudeDesktopExtension ? "extension" : "configFile";
}

/// <summary>One client's copy of a definition, as that client actually has it.</summary>
/// <remarks>
/// The matrix merges a name into one row, which is the whole point of it — but the
/// merge is also where the evidence goes. Two clients can disagree about what
/// "github" runs, and until this existed the disagreement was noticed
/// (<c>Discovery</c> raised a diagnostic) and then thrown away: the row kept
/// whichever definition was read first and nothing could say which client held the
/// other one. Drift is not an edge case in a tool whose subject is several copies
/// of the same configuration, so each copy is kept.
/// </remarks>
/// <param name="IsBundled">
/// Extensions and plugins are installed software, not configuration. They can take
/// part in a comparison and never in a write.
/// </param>
/// <param name="DefinitionSource">
/// This client's definition verbatim, so unifying moves exact bytes rather than a
/// re-serialization (§6).
/// </param>
public sealed record ClientDefinition(
    Transport Transport,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<EnvEntry> Env,
    string? Url,
    Origin Origin,
    bool IsBundled,
    string DefinitionSource)
{
    /// <summary>
    /// This copy came from an explicitly attached custom source. It participates
    /// in discovery and comparison, but is never a write destination.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// The copy a single client holds, taken from the server as that client's
    /// source produced it — before any merging.
    /// </summary>
    public static ClientDefinition From(Server server) => new(
        server.Transport,
        server.Command,
        server.Args,
        server.Env,
        server.Url,
        server.Origin,
        server.IsBundled,
        server.DefinitionSource)
    {
        IsReadOnly = server.IsReadOnly,
    };

    public string Fingerprint => Server.MakeFingerprint(Command, Args, Url);

    /// <summary>Environment keys, normalized for comparison.</summary>
    /// <remarks>
    /// Keys only: a value that differs is reported as the fact that it differs,
    /// never as itself (§6).
    /// </remarks>
    public IReadOnlyList<string> EnvironmentKeys =>
        Env.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>The normalized record every client's config is mapped onto (§5).</summary>
/// <remarks>
/// Config files stay authoritative for what is enabled; this is a read-through
/// view of them, not a store.
/// </remarks>
public sealed class Server
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Transport Transport { get; init; }
    public required string? Command { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public required IReadOnlyList<EnvEntry> Env { get; init; }
    public required string? Url { get; init; }
    public required Dictionary<ClientKey, Enablement> EnabledIn { get; init; }
    public required Origin Origin { get; init; }

    /// <summary>
    /// Identifies the same server configured under different names. Not used to
    /// merge rows — only to flag when one name means two different commands.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Bundled servers (Claude Desktop extensions) ship their own command and are
    /// not editable.
    /// </summary>
    public required bool IsBundled { get; init; }

    /// <summary>The definition selected for this merged view is discovery-only.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// The definition's verbatim source, as found.
    /// </summary>
    /// <remarks>
    /// Kept so that parking a server and bringing it back, or copying it into
    /// another client, moves the exact bytes rather than a re-serialization of the
    /// model. Anything Kytto's model does not understand travels with it.
    /// </remarks>
    public required string DefinitionSource { get; init; }

    /// <summary>Read-only origin and maintenance hints for this definition.</summary>
    public ServerProvenance Provenance { get; set; } = ServerProvenance.Unknown;

    /// <summary>The definition as each client has it, separately.</summary>
    /// <remarks>
    /// The top-level <see cref="Command"/>, <see cref="Args"/> and <see cref="Env"/>
    /// are the merged view — enough to run the server, and the reason a row is a
    /// row. This is the evidence underneath it: the same server written into three
    /// files, where it can quietly drift out of step in one of them.
    /// </remarks>
    public Dictionary<ClientKey, ClientDefinition> DefinitionsByClient { get; init; } = [];

    /// <summary>The environment as each client has it.</summary>
    /// <remarks>
    /// Derived, so there is one place a client's copy of anything is recorded.
    /// </remarks>
    public IReadOnlyDictionary<ClientKey, IReadOnlyList<EnvEntry>> EnvByClient =>
        DefinitionsByClient.ToDictionary(pair => pair.Key, pair => pair.Value.Env);

    /// <summary>Substitutions the owning client makes before it runs this server.</summary>
    /// <remarks>
    /// Claude Desktop extensions ship commands full of <c>${__dirname}</c> and
    /// <c>${user_config.something}</c>, which only Claude Desktop expands. Kytto
    /// knows what they mean — the bundle directory and the extension's own
    /// settings — so it can expand them too, and without that a health check on an
    /// extension can only ever fail. Applied when running, never when displaying:
    /// the matrix shows the command as it is written in the manifest, because that
    /// is what is really there.
    /// </remarks>
    public Dictionary<string, string> Placeholders { get; init; } = [];

    /// <summary>The last health check, if one has ever been run (§7.3).</summary>
    /// <remarks>
    /// Never populated automatically: §6 forbids spawning on launch, so a server
    /// nobody has checked stays honestly grey.
    /// </remarks>
    public HealthResult? Health { get; set; }

    /// <summary>The last token measurement (§7.4).</summary>
    public TokenWeight? TokenWeight { get; set; }

    /// <summary>
    /// Identity across clients: the name, normalized. Two clients listing "github"
    /// are showing the same row of the matrix.
    /// </summary>
    public static string Identity(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// A cheap, stable hash of what actually runs, used to spot two servers sharing
    /// a name but not a command.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <c>GetHashCode</c>, which .NET seeds randomly per process
    /// — this needs to mean the same thing across launches once it is persisted.
    /// </remarks>
    public static string MakeFingerprint(string? command, IReadOnlyList<string> args, string? url)
    {
        // Control characters as separators so a value containing the separator
        // cannot forge a different server's fingerprint.
        var joinedArgs = string.Join('\u0001', args);
        var parts = string.Join('\u0002', command ?? "", joinedArgs, url ?? "");
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(parts))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>The command with every placeholder the client would substitute resolved.</summary>
    public (string? Command, IReadOnlyList<string> Args) Runnable =>
        (Expand(Command), Args.Select(arg => Expand(arg) ?? arg).ToArray());

    private string? Expand(string? text)
    {
        if (text is null || Placeholders.Count == 0) return text;
        var result = text;
        foreach (var (token, value) in Placeholders)
        {
            result = result.Replace($"${{{token}}}", value, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Single-line command for the matrix subtitle.</summary>
    public string CommandSummary
    {
        get
        {
            if (Url is { } url) return url;
            if (Command is not { } command) return "";
            return string.Join(' ', new[] { command }.Concat(Args));
        }
    }

    /// <summary>
    /// Whether anything this server is launched through is resolved against a
    /// working directory rather than named outright (§4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It matters at exactly one moment: copying the definition into another
    /// client. A relative path is resolved against whatever directory launched the
    /// client, so it finds its binary from where the client that already has it
    /// happens to run and nowhere else. Moved elsewhere the definition parses,
    /// writes, backs up and reads back perfectly, and then the server does not
    /// start — the one outcome where every check Kytto makes passes and the user
    /// still gets nothing. So the matrix says so before the click and again after it.
    /// </para>
    /// <para>
    /// Deliberately narrow. A bare program name is not a relative path — <c>npx</c>
    /// goes through PATH, which no client owns — and an argument is only counted
    /// when it opens with <c>./</c> or <c>../</c>, which is unambiguous. Anything
    /// looser flags <c>@modelcontextprotocol/server-github</c>, and a warning that
    /// cries wolf on half the matrix is worse than no warning at all.
    /// </para>
    /// </remarks>
    public bool HasRelativePath
    {
        get
        {
            if (Transport != Transport.Stdio) return false;
            if (!string.IsNullOrEmpty(Command) && ContainsSeparator(Command) && !IsAbsolute(Command))
            {
                return true;
            }
            return Args.Any(OpensWithRelativePrefix);
        }
    }

    /// <summary>Whether the executable itself, rather than only an argument, is relative.</summary>
    public bool HasRelativeCommand =>
        Transport == Transport.Stdio &&
        !string.IsNullOrEmpty(Command) &&
        ContainsSeparator(Command) &&
        !IsAbsolute(Command);

    private static bool ContainsSeparator(string text) =>
        text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal);

    private static bool IsAbsolute(string text)
    {
        // POSIX roots, the shell's home shorthand, a UNC share, and a Windows drive
        // letter in either slash. The rule knows both platforms on purpose: a
        // definition copied here from a Mac still has to be judged correctly.
        if (text.StartsWith('/') || text.StartsWith('~') || text.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }
        var afterDrive = text.Length > 1 ? text[1..] : "";
        return afterDrive.StartsWith(@":\", StringComparison.Ordinal)
            || afterDrive.StartsWith(":/", StringComparison.Ordinal);
    }

    private static bool OpensWithRelativePrefix(string text) =>
        text.StartsWith("./", StringComparison.Ordinal)
        || text.StartsWith("../", StringComparison.Ordinal)
        || text.StartsWith(@".\", StringComparison.Ordinal)
        || text.StartsWith(@"..\", StringComparison.Ordinal);
}
