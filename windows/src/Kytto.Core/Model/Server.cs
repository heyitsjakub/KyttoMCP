using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
/// <param name="PreviousKey">
/// In an edit: the name this entry had before the user renamed it, so a value left
/// "unchanged" moves with it instead of being lost. Names only; never a value.
/// </param>
public sealed record EnvEntry(string Key, string? Value, string? PreviousKey = null)
{
    public bool HasValue => !string.IsNullOrEmpty(Value);
}

/// <summary>Where a server definition was found.</summary>
public abstract record Origin
{
    public sealed record ConfigFile(ClientKey Client) : Origin;

    public sealed record ClaudeDesktopExtension(string BundleId) : Origin;

    /// <summary>
    /// A server declared by an installed, registered, enabled client plugin (§4).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ClaudeDesktopExtension"/>: an extension stays
    /// controllable in its owning client, a plugin is read-only everywhere. The owner
    /// is always the built-in client that registered it — a project scope is still
    /// Claude Code's plugin — so the write services, which only ever receive a
    /// <see cref="ClientId"/>, can compare against it directly. Paths and launch
    /// substitutions deliberately stay out of this value: it is what the web layer is
    /// told about the source, and it is not allowed to interpret a path.
    /// </remarks>
    public sealed record Plugin(
        ClientId Client,
        string PluginId,
        string DisplayName,
        string? Version) : Origin;

    /// <summary>The spelling <c>docs/ipc.md</c> gives this on the wire.</summary>
    public string Kind => this switch
    {
        ClaudeDesktopExtension => "extension",
        Plugin => "plugin",
        _ => "configFile",
    };

    /// <summary>Display metadata only: no path, no substitution, no launch context.</summary>
    public ServerSource Source => this switch
    {
        ClaudeDesktopExtension => new ServerSource(
            ServerSourceKinds.Extension, "Claude Desktop extension", null, ReadOnly: false),
        Plugin plugin => new ServerSource(
            ServerSourceKinds.Plugin, plugin.DisplayName, plugin.Version, ReadOnly: true),
        ConfigFile file when file.Client.BuiltIn is not null => new ServerSource(
            ServerSourceKinds.Config, "Configuration", null, ReadOnly: false),
        _ => new ServerSource(
            ServerSourceKinds.Custom, "Custom source", null, ReadOnly: true),
    };
}

public static class ServerSourceKinds
{
    public const string Config = "config";
    public const string Custom = "custom";
    public const string Extension = "extension";
    public const string Plugin = "plugin";
}

/// <summary>
/// Where a server came from, in terms a person can read (<c>ServerDto.Source</c>).
/// </summary>
/// <remarks>
/// Not <see cref="ServerProvenance"/>: that one says how a package is maintained, this
/// one says which installation or file produced the definition. <c>ReadOnly</c> is the
/// fact that Kytto neither edits, copies nor toggles it.
/// </remarks>
public sealed record ServerSource(string Kind, string DisplayName, string? Version, bool ReadOnly);

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
    /// The native launch context this client's definition needs, kept beside the
    /// definition so a copy found through one client is never run with the context
    /// of another. None of it crosses IPC (§4).
    /// </summary>
    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Placeholders { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> LaunchEnvironment { get; init; } =
        new Dictionary<string, string>();

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
        WorkingDirectory = server.WorkingDirectory,
        Placeholders = server.Placeholders,
        LaunchEnvironment = server.LaunchEnvironment,
    };

    /// <summary>This copy was declared by an installed plugin, not written by anyone.</summary>
    public bool IsPlugin => Origin is Origin.Plugin;

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
    /// Bundled servers (Claude Desktop extensions, registered plugins) ship their own
    /// command and are not editable. What each may still do differs: an extension is
    /// controlled by its owning client, a plugin is not toggled either (see
    /// <see cref="IsPlugin"/>).
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

    /// <summary>
    /// Absolute directory established by the owning configuration or by a plugin
    /// registration, or null when nothing established one.
    /// </summary>
    /// <remarks>
    /// Never guessed from Kytto's own launch directory: a definition whose directory
    /// nothing can name is run, if at all, from wherever the process happens to be, and
    /// that is exactly the case <see cref="HasRelativeCommand"/> exists to refuse.
    /// Native only — it never reaches a DTO.
    /// </remarks>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Path variables the owning client provides to the process, kept apart from the
    /// raw config's <c>env</c> so what the file says is never rewritten (§4).
    /// </summary>
    public Dictionary<string, string> LaunchEnvironment { get; init; } = [];

    /// <summary>This server is declared by an installed plugin and owned by <see cref="Origin.Plugin.Client"/>.</summary>
    public bool IsPlugin => Origin is Origin.Plugin;

    /// <summary>
    /// Whether the copy this client holds is a plugin's. A merged row can carry a
    /// plugin copy in one client and a plain config entry in another, and the write
    /// services must refuse the first without blocking the second.
    /// </summary>
    public bool HoldsPluginCopy(ClientKey client) =>
        DefinitionsByClient.TryGetValue(client, out var copy)
            ? copy.IsPlugin
            : Origin is Origin.Plugin plugin && client == plugin.Client;

    /// <summary>
    /// Each client's own rules for this server's single tools, where its files have any
    /// to say (§7.11). Absent means every tool stays on.
    /// </summary>
    public Dictionary<ClientKey, ToolFilter> ToolFilters { get; init; } = [];

    /// <summary>The last health check, if one has ever been run (§7.3).</summary>
    /// <remarks>
    /// Never populated automatically: §6 forbids spawning on launch, so a server
    /// nobody has checked stays honestly grey.
    /// </remarks>
    public HealthResult? Health { get; set; }

    /// <summary>The tools from the last passing check, kept through later failures.</summary>
    public IReadOnlyList<ToolSummary>? LastPassingTools { get; set; }

    /// <summary>What the server offers as far as Kytto knows.</summary>
    /// <remarks>
    /// <c>Health.Tools</c> is empty after a failed check, yet clients keep running
    /// the server with what it last offered. The safety scan and tool-name collisions
    /// read this so a failing check cannot hide a finding (§7.10).
    /// </remarks>
    public IReadOnlyList<ToolSummary> KnownTools =>
        Health is { Status: HealthStatus.Passed } health ? health.Tools : LastPassingTools ?? [];

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

    /// <summary>
    /// The environment the process is started with: the definition's own variables
    /// with their placeholders resolved, then the path variables the owning client
    /// would add (§4).
    /// </summary>
    /// <remarks>
    /// Windows environment names are case-insensitive, so a launch variable never
    /// shadows a configured one that differs only in case — the file's spelling wins.
    /// </remarks>
    public IReadOnlyList<EnvEntry> RunnableEnvironment
    {
        get
        {
            var configured = Env.Select(entry => new EnvEntry(entry.Key, Expand(entry.Value))).ToList();
            var keys = configured.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in LaunchEnvironment.Keys.Order(StringComparer.Ordinal))
            {
                if (keys.Contains(key)) continue;
                configured.Add(new EnvEntry(key, LaunchEnvironment[key]));
            }
            return configured;
        }
    }

    /// <summary>Resolves the known placeholders in <paramref name="text"/> and nothing else.</summary>
    internal string ExpandingKnownPlaceholders(string text) => Expand(text) ?? text;

    private static readonly Regex PlaceholderPattern = new(
        @"\$\{([^{}]+)\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private string? Expand(string? text)
    {
        if (text is null || Placeholders.Count == 0) return text;
        // One pass over the original text: a substituted value is data, never another
        // template. That is also what makes the result independent of dictionary
        // iteration order, which a replace-per-token loop is not (§4).
        return PlaceholderPattern.Replace(text, match =>
            Placeholders.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
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
    /// <para>
    /// Judged on what would run, placeholders resolved: a plugin's
    /// <c>${CLAUDE_PLUGIN_ROOT}/bin/tool</c> is an absolute path once its root is known,
    /// and calling it relative because it starts with <c>$</c> would be exactly the
    /// crying wolf the paragraph above warns about.
    /// </para>
    /// </remarks>
    public bool HasRelativePath
    {
        get
        {
            if (Transport != Transport.Stdio) return false;
            var (command, args) = Runnable;
            if (!string.IsNullOrEmpty(command) && ContainsSeparator(command) && !IsAbsolute(command))
            {
                return true;
            }
            return args.Any(OpensWithRelativePrefix);
        }
    }

    /// <summary>Whether the executable itself, rather than only an argument, is relative.</summary>
    public bool HasRelativeCommand =>
        Transport == Transport.Stdio &&
        Runnable.Command is { Length: > 0 } command &&
        ContainsSeparator(command) &&
        !IsAbsolute(command);

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
