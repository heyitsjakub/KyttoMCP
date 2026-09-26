using Kytto.Core.Json;
using Kytto.Core.Toml;

namespace Kytto.Core.Model;

/// <summary>A server the user is describing, before it becomes a definition in a file.</summary>
/// <remarks>
/// Kept separate from <see cref="Server"/> because a draft is allowed to be
/// incomplete and wrong — that is the point of validating it. A
/// <see cref="Server"/> is always something that really exists in a config.
/// </remarks>
public sealed record ServerDraft
{
    public string Name { get; init; } = "";
    public Transport Transport { get; init; } = Transport.Stdio;
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyList<EnvEntry> Env { get; init; } = [];
    public string Url { get; init; } = "";

    /// <summary>Pre-fills a draft from a server that already exists, for editing.</summary>
    public static ServerDraft Editing(Server server) => new()
    {
        Name = server.Name,
        Transport = server.Transport,
        Command = server.Command ?? "",
        Args = server.Args,
        Env = server.Env,
        Url = server.Url ?? "",
    };

    public string TrimmedName => Name.Trim();

    /// <summary>Fills in environment values the editor was never given.</summary>
    /// <remarks>
    /// The UI only ever sees <c>{ key, hasValue }</c> — an API token does not cross
    /// the IPC boundary just because someone opened the edit form (§6). So a value
    /// of null coming back means "leave this one as it was", and the real value is
    /// looked up here, natively, at the last moment.
    /// </remarks>
    public ServerDraft MergingSecrets(Server server)
    {
        var existing = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in server.Env)
        {
            existing.TryAdd(entry.Key, entry.Value);
        }

        return this with
        {
            Env = Env
                .Select(entry => entry.Value is not null
                    ? entry
                    : new EnvEntry(entry.Key, existing.GetValueOrDefault(entry.Key)))
                .ToArray(),
        };
    }

    // MARK: - Validation

    public enum ValidationError
    {
        NameEmpty,
        NameHasWhitespace,
        NameTaken,
        CommandEmpty,
        UrlEmpty,
        UrlInvalid,
        EnvKeyEmpty,
        EnvKeyInvalid,
    }

    public sealed record Problem(ValidationError Error, string Message);

    /// <param name="takenNames">
    /// Names already present in the clients being written to, normalized.
    /// </param>
    /// <param name="allowing">
    /// The original name when editing, so a server does not collide with itself.
    /// </param>
    public IReadOnlyList<Problem> Validate(
        IReadOnlySet<string>? takenNames = null,
        string? allowing = null)
    {
        var problems = new List<Problem>();
        var name = TrimmedName;

        if (name.Length == 0)
        {
            problems.Add(new Problem(ValidationError.NameEmpty, "Give the server a name."));
        }
        else if (name.Any(char.IsWhiteSpace))
        {
            problems.Add(new Problem(
                ValidationError.NameHasWhitespace,
                "Server names cannot contain spaces — they are used as keys in the config file."));
        }
        else
        {
            var identity = Server.Identity(name);
            var allowed = allowing is null ? null : Server.Identity(allowing);
            if (takenNames?.Contains(identity) == true && identity != allowed)
            {
                problems.Add(new Problem(
                    ValidationError.NameTaken,
                    $"\"{name}\" already exists in one of the selected clients."));
            }
        }

        if (Transport == Transport.Stdio)
        {
            if (Command.Trim().Length == 0)
            {
                problems.Add(new Problem(
                    ValidationError.CommandEmpty, "A stdio server needs a command to run."));
            }
        }
        else
        {
            var url = Url.Trim();
            if (url.Length == 0)
            {
                problems.Add(new Problem(
                    ValidationError.UrlEmpty, "An HTTP or SSE server needs a URL."));
            }
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                     || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                     || string.IsNullOrWhiteSpace(parsed.Host))
            {
                problems.Add(new Problem(
                    ValidationError.UrlInvalid, $"\"{url}\" is not a valid URL."));
            }
        }

        if (Env.Any(entry => entry.Key.Length == 0))
        {
            problems.Add(new Problem(
                ValidationError.EnvKeyEmpty, "An environment variable has no name."));
        }
        else if (Env.FirstOrDefault(entry => !IsPortableEnvName(entry.Key)) is { } invalid)
        {
            problems.Add(new Problem(
                ValidationError.EnvKeyInvalid,
                $"\"{invalid.Key}\" is not a valid environment variable name. Use letters, numbers, and underscores, and do not start with a number."));
        }

        return problems;
    }

    private static bool IsPortableEnvName(string value)
    {
        if (value.Length == 0 || !(IsAsciiLetter(value[0]) || value[0] == '_')) return false;
        return value.Skip(1).All(character =>
            IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_');
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    // MARK: - Rendering

    /// <summary>The definition as it will appear in a config file.</summary>
    /// <remarks>
    /// <c>type</c> is written only for HTTP and SSE. Every client understands a bare
    /// <c>command</c> as stdio, but not all of them have always understood
    /// <c>"type": "stdio"</c>, so the quieter form is the portable one.
    /// </remarks>
    public string Definition(string baseIndent, string unit, string newline = "\n")
    {
        var fields = new List<JsonBuildValue.Field>();

        if (Transport == Transport.Stdio)
        {
            fields.Add(new JsonBuildValue.Field("command", JsonBuildValue.Text(Command.Trim())));
            if (Args.Count > 0)
            {
                fields.Add(new JsonBuildValue.Field("args", JsonBuildValue.Strings(Args)));
            }
        }
        else
        {
            fields.Add(new JsonBuildValue.Field("type", JsonBuildValue.Text(Transport.Raw())));
            fields.Add(new JsonBuildValue.Field("url", JsonBuildValue.Text(Url.Trim())));
        }

        var realEnv = Env.Where(entry => entry.Key.Trim().Length > 0).ToArray();
        if (realEnv.Length > 0)
        {
            fields.Add(new JsonBuildValue.Field("env", JsonBuildValue.Object(
                realEnv.Select(entry =>
                    new JsonBuildValue.Field(entry.Key, JsonBuildValue.Text(entry.Value ?? "")))
                    .ToArray())));
        }

        return JsonBuilder.Render(JsonBuildValue.Object(fields), baseIndent, unit, newline);
    }

    /// <summary>The same definition, spelled as a TOML table block.</summary>
    /// <remarks>
    /// Transport is carried by which key is present rather than by a <c>type</c>
    /// field: Codex reads <c>url</c> as remote and <c>command</c> as stdio, and
    /// writing both would be describing two servers.
    /// </remarks>
    /// <param name="enabled">
    /// Pass <c>false</c> only to keep a server that is already switched off switched
    /// off. Editing a definition must not quietly turn it back on, and absent means on.
    /// </param>
    public string TomlBlock(string key, bool? enabled)
    {
        var isRemote = Transport != Transport.Stdio;
        return TomlBuilder.ServerBlock(
            name: TrimmedName,
            key: key,
            command: isRemote ? null : Command.Trim(),
            args: Args,
            url: isRemote ? Url.Trim() : null,
            env: Env,
            enabled: enabled);
    }
}
