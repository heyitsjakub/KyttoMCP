using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kytto.Core.Model;

namespace Kytto.Core;

/// <summary>A prefilled server the user can start from (§7.2).</summary>
public sealed record CatalogEntry(
    string Id,
    string Name,
    string DisplayName,
    string Description,
    string Transport,
    string Command,
    IReadOnlyList<string> Args,
    string? Url,
    IReadOnlyList<CatalogEntry.Placeholder> Placeholders,
    IReadOnlyList<CatalogEntry.EnvRequirement> Env,
    /// <summary>What has to be installed for the command to work, in plain words.</summary>
    string Requires,
    string Homepage)
{
    /// <param name="Token">The token to substitute, e.g. <c>{{directory}}</c>.</param>
    public sealed record Placeholder(string Token, string Label, string Example);

    public sealed record EnvRequirement(string Key, bool Required, string Hint);

    /// <summary>
    /// A draft ready to edit, with placeholders left in so the user can see what
    /// still needs filling in.
    /// </summary>
    public ServerDraft MakeDraft() => new()
    {
        Name = Name,
        Transport = Transports.FromRaw(Transport) ?? Model.Transport.Stdio,
        Command = Command,
        Args = Args,
        Env = Env.Select(requirement => new EnvEntry(requirement.Key, null)).ToArray(),
        Url = Url ?? "",
    };
}

/// <summary>The bundled catalog.</summary>
/// <remarks>
/// Static by design (§7.2): no remote fetching, no registry API. Catalogs are
/// everywhere and free — the value of this product is the matrix, not the list.
/// This exists only so that adding a first server is not a blank form.
/// </remarks>
public static class Catalog
{
    private sealed record CatalogFile(IReadOnlyList<CatalogEntry> Servers);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IReadOnlyList<CatalogEntry> Entries { get; } = Load();

    public static CatalogEntry? Entry(string id) =>
        Entries.FirstOrDefault(entry => entry.Id == id);

    private static IReadOnlyList<CatalogEntry> Load()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = Array.Find(
                assembly.GetManifestResourceNames(),
                candidate => candidate.EndsWith("catalog.json", StringComparison.Ordinal));
            if (name is null) return [];

            using var stream = assembly.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<CatalogFile>(stream, Options)?.Servers ?? [];
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // A missing or malformed catalog is a packaging mistake, not a user
            // problem. The manual path (§7.2) works without it, so degrade quietly.
            return [];
        }
    }
}
