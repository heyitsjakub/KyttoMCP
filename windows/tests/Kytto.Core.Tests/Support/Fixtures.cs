using System.Text;
using Kytto.Core.Clients;
using Kytto.Core.Json;

namespace Kytto.Core.Tests.Support;

public enum Fixture
{
    ClaudeDesktopNoServers,
    ClaudeDesktopWithServers,
    ClaudeCode,
    ClaudeCodeSettings,
    CursorMcp,
    VsCodeMcp,
    ExtensionManifest,
    ExtensionSettingsDisabled,
    CodexConfig,
}

public static class Fixtures
{
    private static readonly Dictionary<Fixture, string> FileNames = new()
    {
        [Fixture.ClaudeDesktopNoServers] = "claude_desktop_no_servers.json",
        [Fixture.ClaudeDesktopWithServers] = "claude_desktop_with_servers.json",
        [Fixture.ClaudeCode] = "claude_code.json",
        [Fixture.ClaudeCodeSettings] = "claude_code_settings.json",
        [Fixture.CursorMcp] = "cursor_mcp.json",
        [Fixture.VsCodeMcp] = "vscode_mcp.jsonc",
        [Fixture.ExtensionManifest] = "extension_manifest.json",
        [Fixture.ExtensionSettingsDisabled] = "extension_settings_disabled.json",
        [Fixture.CodexConfig] = "codex_config.toml",
    };

    public static string FileName(this Fixture fixture) => FileNames[fixture];

    /// <summary>
    /// What this fixture is spelled in.
    /// </summary>
    /// <remarks>
    /// The span tests are per-format, and a fixture added to the wrong list should
    /// fail to parse rather than quietly skip the checks that matter.
    /// </remarks>
    public static ConfigFormat Format(this Fixture fixture) =>
        fixture.FileName().EndsWith(".toml", StringComparison.Ordinal)
            ? ConfigFormat.Toml
            : ConfigFormat.Json;

    public static string Path(this Fixture fixture) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture.FileName());

    /// <summary>
    /// Read as bytes and decoded, so the exact source — line endings, trailing
    /// newline, BOM — reaches the parser untouched.
    /// </summary>
    public static string Text(this Fixture fixture) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(fixture.Path()));

    public static JsonDocument Document(this Fixture fixture) => JsonDocument.Parse(fixture.Text());

    /// <summary>xUnit member data: every JSON fixture, one per case.</summary>
    public static TheoryData<Fixture> Json
    {
        get
        {
            var data = new TheoryData<Fixture>();
            foreach (var fixture in Enum.GetValues<Fixture>())
            {
                if (fixture.Format() == ConfigFormat.Json) data.Add(fixture);
            }
            return data;
        }
    }

    public static TheoryData<Fixture> Toml
    {
        get
        {
            var data = new TheoryData<Fixture>();
            foreach (var fixture in Enum.GetValues<Fixture>())
            {
                if (fixture.Format() == ConfigFormat.Toml) data.Add(fixture);
            }
            return data;
        }
    }
}

/// <summary>
/// A view of a parsed tree with all spans removed, so two trees can be compared
/// on content alone.
/// </summary>
/// <remarks>
/// Used to check that the text a span points at re-parses to the same value the
/// span was taken from.
/// </remarks>
public abstract record Shape
{
    public sealed record Entry(string Key, Shape Value);

    public sealed record ObjectShape(IReadOnlyList<Entry> Entries) : Shape
    {
        public bool Equals(ObjectShape? other) =>
            other is not null && Entries.SequenceEqual(other.Entries);

        public override int GetHashCode() => Entries.Count;
    }

    public sealed record ArrayShape(IReadOnlyList<Shape> Elements) : Shape
    {
        public bool Equals(ArrayShape? other) =>
            other is not null && Elements.SequenceEqual(other.Elements);

        public override int GetHashCode() => Elements.Count;
    }

    public sealed record StringShape(string Value) : Shape;
    public sealed record NumberShape(double Value) : Shape;
    public sealed record BoolShape(bool Value) : Shape;
    public sealed record NullShape : Shape;

    public static Shape Of(JsonNode node) => node.Kind switch
    {
        JsonKind.Object => new ObjectShape(
            node.Members!.Select(member => new Entry(member.Key, Of(member.Value))).ToArray()),
        JsonKind.Array => new ArrayShape(node.Elements!.Select(Of).ToArray()),
        JsonKind.String => new StringShape(node.StringValue!),
        JsonKind.Number => new NumberShape(node.NumberValue!.Value),
        JsonKind.Bool => new BoolShape(node.BoolValue!.Value),
        _ => new NullShape(),
    };
}

public static class Nodes
{
    /// <summary>Every node in the tree, parents before children.</summary>
    public static List<JsonNode> All(JsonNode node)
    {
        var result = new List<JsonNode> { node };
        if (node.Members is { } members)
        {
            foreach (var member in members) result.AddRange(All(member.Value));
        }
        else if (node.Elements is { } elements)
        {
            foreach (var element in elements) result.AddRange(All(element));
        }
        return result;
    }
}
