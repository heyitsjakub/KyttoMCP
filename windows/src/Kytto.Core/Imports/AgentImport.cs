using System.Text.Json;
using Kytto.Core.Model;

namespace Kytto.Core.Imports;

/// <summary>One server definition returned by the copy-paste agent prompt.</summary>
/// <remarks>
/// Environment values are intentionally absent. The prompt asks for key names
/// only, so an import can never invent a blank secret or move one through IPC.
/// </remarks>
public sealed record AgentImportServer(
    string Name,
    Transport Transport,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> EnvironmentKeys,
    string? Url)
{
    public ServerDraft ToDraft() => new()
    {
        Name = Name,
        Transport = Transport,
        Command = Command ?? "",
        Args = Args,
        // Key-only imports do not write an env map. A value can be supplied later
        // through the ordinary native authoring form, where secrets stay native.
        Env = [],
        Url = Url ?? "",
    };
}

public sealed record AgentImportDocument(IReadOnlyList<AgentImportServer> Servers);

public sealed class AgentImportException(string message) : Exception(message);

/// <summary>Strict parser for the local, copy-paste agent import contract (§6).</summary>
public static class AgentImportParser
{
    public const string Prompt = """
        Return only one strict JSON object — no Markdown fences, comments or trailing commas.
        Use exactly this schema and no other fields:
        {
          "schemaVersion": 1,
          "servers": [
            {
              "name": "short-name",
              "transport": "stdio",
              "command": "python3",
              "args": ["server.py"],
              "env": ["API_KEY"]
            }
          ]
        }
        The only allowed server fields are name, transport, command, args, url and env.
        transport must be stdio, http or sse. Put environment variable names in env,
        never their values or any credentials. Return at least one server.
        """;

    private static readonly JsonDocumentOptions Strict = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 64,
    };

    private static readonly HashSet<string> RootFields = ["schemaVersion", "servers"];
    private static readonly HashSet<string> ServerFields =
        ["name", "transport", "command", "args", "url", "env"];

    public static AgentImportDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw Invalid("The import is empty.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Strict);
        }
        catch (JsonException error)
        {
            throw Invalid("The import must be strict JSON without comments, Markdown fences or trailing commas.", error);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The import root must be an object.");
            }

            var rootProperties = Properties(root, RootFields, "root");
            if (!root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetRawText() != "1")
            {
                throw Invalid("schemaVersion must be exactly the number 1.");
            }
            if (!root.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Array ||
                servers.GetArrayLength() == 0)
            {
                throw Invalid("servers must be a non-empty array.");
            }

            var result = new List<AgentImportServer>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in servers.EnumerateArray())
            {
                result.Add(ParseServer(element, names));
            }
            return new AgentImportDocument(result);
        }
    }

    private static AgentImportServer ParseServer(
        JsonElement element,
        ISet<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Every servers entry must be an object.");
        }
        Properties(element, ServerFields, "server");

        var name = RequiredString(element, "name");
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || HasControl(name))
        {
            throw Invalid("Every server name must be non-empty, single-token text.");
        }
        if (!names.Add(Server.Identity(name)))
        {
            throw Invalid($"The server name \"{name}\" appears more than once.");
        }

        var transportText = RequiredString(element, "transport");
        var transport = Transports.FromRaw(transportText)
            ?? throw Invalid("transport must be stdio, http or sse.");

        string? command = null;
        if (element.TryGetProperty("command", out var commandElement))
        {
            if (commandElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid("command must be a string.");
            }
            command = commandElement.GetString();
            if (string.IsNullOrWhiteSpace(command) || HasControl(command))
            {
                throw Invalid("command must be non-empty text without control characters.");
            }
        }

        string? url = null;
        if (element.TryGetProperty("url", out var urlElement))
        {
            if (urlElement.ValueKind != JsonValueKind.String)
            {
                throw Invalid("url must be a string.");
            }
            url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url) || HasControl(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(parsed.Host))
            {
                throw Invalid("url must be an absolute HTTP or HTTPS URL.");
            }
        }

        var args = ReadStringArray(element, "args");
        var environment = ReadEnvironment(element);
        if (transport == Transport.Stdio)
        {
            if (command is null || url is not null)
            {
                throw Invalid("stdio servers require command and must not have url.");
            }
        }
        else if (url is null || command is not null || args.Count > 0)
        {
            throw Invalid("http and sse servers require url and must not have command or args.");
        }

        return new AgentImportServer(name, transport, command, args, environment, url);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{name} must be an array of strings.");
        }

        var result = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value || HasControl(value))
            {
                throw Invalid($"{name} must contain only strings without control characters.");
            }
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<string> ReadEnvironment(JsonElement parent)
    {
        var values = ReadStringArray(parent, "env");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (value.Length == 0 ||
                !(IsAsciiLetter(value[0]) || value[0] == '_') ||
                !value.Skip(1).All(character =>
                    IsAsciiLetter(character) || char.IsAsciiDigit(character) || character == '_'))
            {
                throw Invalid($"\"{value}\" is not a safe environment variable name.");
            }
            if (!seen.Add(value)) throw Invalid($"The environment key \"{value}\" appears more than once.");
            result.Add(value);
        }
        return result;
    }

    private static IReadOnlyList<string> Properties(
        JsonElement objectElement,
        IReadOnlySet<string> allowed,
        string subject)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in objectElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid($"Unknown field \"{property.Name}\" in {subject}.");
            }
            if (!seen.Add(property.Name))
            {
                throw Invalid($"The field \"{property.Name}\" appears more than once in {subject}.");
            }
        }
        return seen.ToArray();
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } value ||
            HasControl(value))
        {
            throw Invalid($"{name} must be a string.");
        }
        return value;
    }

    private static bool HasControl(string value) => value.Any(char.IsControl);

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static AgentImportException Invalid(string message, Exception? inner = null) =>
        new(message);
}
