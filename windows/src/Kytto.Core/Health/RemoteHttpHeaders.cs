using System.Text;
using Kytto.Core.Toml;
using JsonDocument = Kytto.Core.Json.JsonDocument;

namespace Kytto.Core.Health;

/// <summary>
/// The HTTP headers a client's own definition would send, resolved natively (§6).
/// </summary>
/// <remarks>
/// <para>
/// Credentials that the client's config spells as an environment reference — a
/// <c>${VAR}</c> inside a header, a Codex <c>env_http_headers</c> table, a
/// <c>bearer_token_env_var</c> — are read from the environment Kytto was launched in,
/// here, and never cross IPC.
/// </para>
/// <para>
/// <b>One expansion pass.</b> Placeholders are expanded in the values the definition
/// <em>wrote</em>. A value that arrives from the environment is credential data, taken
/// literally: if it happens to contain the text <c>${OTHER}</c> that is part of the
/// secret, not a request for a second variable.
/// </para>
/// </remarks>
public static class RemoteHttpHeaders
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "content-type", "content-encoding", "accept", "accept-encoding",
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te",
        "trailer", "expect", "mcp-session-id", "mcp-protocol-version", "cookie", "proxy-authorization",
    };

    private const int MaxHeaders = 64;
    private const int MaxNameBytes = 128;
    private const int MaxValueBytes = 16_384;

    /// <summary>The headers a definition configures, with credentials resolved.</summary>
    /// <param name="omitAuthorization">
    /// Kytto holds its own sign-in for this server, which replaces the configured
    /// <c>Authorization</c> and nothing else. The configured one is dropped here, before
    /// it is resolved, so a variable that is unset does not fail a check that was never
    /// going to send it.
    /// </param>
    public static IReadOnlyDictionary<string, string> Resolve(
        string definitionSource,
        IReadOnlyDictionary<string, string> environment,
        bool omitAuthorization = false)
    {
        var literal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fromEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? bearerVariable = null;

        if (TryParseJson(definitionSource, out var json))
        {
            foreach (var key in new[] { "headers", "http_headers" })
            {
                if (json.Root[key] is not { } section) continue;
                if (section.Members is not { } members) throw Invalid();
                foreach (var member in members) AddConfigured(literal, member.Key, member.Value.StringValue);
            }
            if (json.Root["env_http_headers"] is { } envSection)
            {
                if (envSection.Members is not { } members) throw Invalid();
                foreach (var member in members)
                {
                    Put(fromEnvironment, member.Key, member.Value.StringValue ?? throw Invalid());
                }
            }
            bearerVariable = json.Root["bearer_token_env_var"]?.StringValue;
        }
        else if (TryParseToml(definitionSource, out var toml))
        {
            ReadToml(toml, literal, fromEnvironment, ref bearerVariable);
        }

        if (omitAuthorization)
        {
            literal.Remove("Authorization");
            fromEnvironment.Remove("Authorization");
            bearerVariable = null;
        }

        // The definition's own words first, expanded once...
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in literal) result[name] = Expand(value, environment);

        // ...then values that come from the environment, taken as they are.
        foreach (var (name, variable) in fromEnvironment)
        {
            result[name] = EnvironmentValue(environment, variable);
        }
        if (bearerVariable is not null)
        {
            result["Authorization"] = "Bearer " + EnvironmentValue(environment, bearerVariable);
        }

        Validate(result);
        return result;
    }

    /// <summary>Replaces headers by name, case-insensitively, and keeps the rest.</summary>
    /// <remarks>
    /// How Kytto's own OAuth token joins a definition's headers: it takes the place of
    /// <c>Authorization</c> and of nothing else.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> configured,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }
        Validate(merged);
        return merged;
    }

    /// <summary>Rejects anything that is not a plain, bounded, non-transport header.</summary>
    /// <remarks>
    /// CR, LF and every other control character are refused so a value cannot start a
    /// second header or a second request; transport headers are refused so a
    /// definition cannot change how Kytto speaks the protocol it is checking.
    /// </remarks>
    public static void Validate(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        if (headers.Count > MaxHeaders) throw Invalid();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0 ||
                Encoding.UTF8.GetByteCount(name) > MaxNameBytes ||
                Encoding.UTF8.GetByteCount(value) > MaxValueBytes ||
                !name.All(IsTokenCharacter) ||
                !value.All(IsFieldCharacter) ||
                Forbidden.Contains(name) ||
                !names.Add(name))
            {
                throw Invalid();
            }
        }
    }

    private static bool IsTokenCharacter(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' ||
        "!#$%&'*+-.^_`|~".Contains(character, StringComparison.Ordinal);

    // Visible ASCII plus space and everything above it; no control characters, which
    // takes CR, LF, NUL and DEL with it.
    private static bool IsFieldCharacter(char character) => character >= ' ' && character != '\u007f';

    private static void AddConfigured(Dictionary<string, string> into, string name, string? value) =>
        Put(into, name, value ?? throw Invalid());

    private static void Put(Dictionary<string, string> into, string name, string value)
    {
        // Two spellings of one header ("Authorization" and "authorization") are one
        // header that a server would read one way and Kytto another.
        if (!into.TryAdd(name, value)) throw Invalid();
    }

    private static string EnvironmentValue(IReadOnlyDictionary<string, string> environment, string variable) =>
        environment.TryGetValue(variable, out var value) && value.Length > 0
            ? value
            : throw new RemoteHttpException(RemoteHttpFailure.UnresolvedCredential);

    // MARK: - Reading the definition

    private static bool TryParseJson(string source, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(source);
            return document.Root.Members is not null;
        }
        catch (Json.JsonParseException)
        {
            document = null!;
            return false;
        }
    }

    private static bool TryParseToml(string source, out TomlDocument document)
    {
        try
        {
            document = TomlDocument.Parse(source);
            return true;
        }
        catch (TomlParseException)
        {
            document = null!;
            return false;
        }
    }

    private static void ReadToml(
        TomlDocument document,
        Dictionary<string, string> literal,
        Dictionary<string, string> fromEnvironment,
        ref string? bearerVariable)
    {
        var table = document.Tables.FirstOrDefault(candidate => candidate.Value("url") is not null);
        var inline = document.Tables
            .SelectMany(candidate => candidate.Pairs)
            .Select(pair => pair.Value.InlinePairs)
            .FirstOrDefault(pairs => pairs is not null && pairs.Any(pair => pair.Name == "url"))
            ?? [];

        TomlValue? Value(string key) =>
            table?.Value(key) ?? inline.FirstOrDefault(pair => pair.Name == key)?.Value;

        bearerVariable = Value("bearer_token_env_var")?.StringValue;
        foreach (var pair in Value("http_headers")?.InlinePairs ?? [])
        {
            if (pair.Name is { } name && pair.Value.StringValue is { } value) Put(literal, name, value);
            else throw Invalid();
        }
        foreach (var pair in Value("env_http_headers")?.InlinePairs ?? [])
        {
            if (pair.Name is { } name && pair.Value.StringValue is { } value) Put(fromEnvironment, name, value);
            else throw Invalid();
        }

        if (table is null) return;
        foreach (var child in document.Tables)
        {
            var target = child.Path.SequenceEqual([.. table.Path, "http_headers"])
                ? literal
                : child.Path.SequenceEqual([.. table.Path, "env_http_headers"])
                    ? fromEnvironment
                    : null;
            if (target is null) continue;
            foreach (var pair in child.Pairs)
            {
                if (pair.Name is { } name && pair.Value.StringValue is { } value) Put(target, name, value);
                else throw Invalid();
            }
        }
    }

    // MARK: - Expansion

    /// <summary>
    /// <c>${NAME}</c>, <c>${env:NAME}</c> and <c>${NAME:-default}</c>, once, left to right.
    /// </summary>
    /// <remarks>
    /// What a variable expands to is appended and never scanned again, so a secret
    /// that contains placeholder-looking text stays exactly what it is. An unset or
    /// empty variable with no default is an error and not an empty header: a request
    /// that quietly goes out without its credential fails somewhere far from the cause.
    /// </remarks>
    internal static string Expand(string value, IReadOnlyDictionary<string, string> environment)
    {
        var result = new StringBuilder();
        var index = 0;
        while (index < value.Length)
        {
            // `${VAR}` and `${env:VAR}` in most clients; OpenCode writes `{env:VAR}`.
            var dollar = value.IndexOf("${", index, StringComparison.Ordinal);
            var braced = value.IndexOf("{env:", index, StringComparison.Ordinal);
            var open = dollar < 0 ? braced : braced < 0 ? dollar : Math.Min(dollar, braced);
            if (open < 0) break;
            var opener = open == dollar ? 2 : 5;
            result.Append(value, index, open - index);
            var close = value.IndexOf('}', open + opener);
            if (close < 0) throw new RemoteHttpException(RemoteHttpFailure.UnresolvedCredential);

            var key = value[(open + opener)..close];
            if (key.StartsWith("env:", StringComparison.Ordinal)) key = key[4..];
            string? fallback = null;
            var split = key.IndexOf(":-", StringComparison.Ordinal);
            if (split >= 0)
            {
                fallback = key[(split + 2)..];
                key = key[..split];
            }

            if (environment.TryGetValue(key, out var replacement) && replacement.Length > 0)
            {
                result.Append(replacement);
            }
            else if (fallback is not null)
            {
                result.Append(fallback);
            }
            else
            {
                throw new RemoteHttpException(RemoteHttpFailure.UnresolvedCredential);
            }
            index = close + 1;
        }
        result.Append(value, index, value.Length - index);
        return result.ToString();
    }

    private static RemoteHttpException Invalid() => new(RemoteHttpFailure.InvalidHeaders);
}
