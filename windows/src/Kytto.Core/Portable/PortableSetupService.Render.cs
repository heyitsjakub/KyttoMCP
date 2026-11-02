using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Toml;

namespace Kytto.Core.Portable;

public sealed partial class PortableSetupService
{
    // MARK: - Rendering an imported definition

    /// <summary>
    /// The definition as <em>this</em> destination spells it. Definitions never travel
    /// as text between formats: a TOML table spliced into a JSON file is the bug this
    /// guards against, so every copy is rendered from the model (§4).
    /// </summary>
    private static string RenderJson(
        PortableServer copy,
        ServerDraft draft,
        Func<PortableValue, string> resolve,
        JsonDocument document,
        string serversKey,
        ServerSchema? schema = null,
        bool enabled = true,
        (string Key, bool OnValue)? flag = null)
    {
        // Indented to sit beside the entries the map already has, or as the first.
        var unit = IndentStyle.Detect(document.SourceText).Text;
        var map = document.ValueAt(serversKey);
        string baseIndent;
        if (map?.Members?.FirstOrDefault() is { } first)
        {
            baseIndent = document.LineIndent(first.Span.Start);
        }
        else
        {
            baseIndent = map is not null ? document.LineIndent(map.Span.Start) + unit : unit;
        }
        var newline = document.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var text = draft.Definition(baseIndent, unit, newline, schema);
        if (copy.WorkingDirectory is { } cwd)
        {
            text = JsonDocument.Parse(text).SettingMember("cwd", [], JsonText.String(resolve(cwd)));
        }
        if (copy.Headers.Count > 0)
        {
            var fields = copy.Headers.Keys
                .Order(StringComparer.Ordinal)
                .Select(key => new JsonBuildValue.Field(key, JsonBuildValue.Text(resolve(copy.Headers[key]))))
                .ToArray();
            text = JsonDocument.Parse(text).SettingMember(
                "headers",
                [],
                JsonBuilder.Render(JsonBuildValue.Object(fields), baseIndent + unit, unit, newline));
        }
        // A client whose off switch sits on the entry gets it there, so a server
        // imported switched off arrives switched off.
        if (!enabled && flag is { } off)
        {
            text = JsonDocument.Parse(text).SettingMember(off.Key, [], JsonText.Bool(!off.OnValue));
        }
        return text;
    }

    private static string InsertJson(
        string text,
        PortableServer copy,
        ServerDraft draft,
        Func<PortableValue, string> resolve,
        string serversKey,
        ServerSchema? schema = null,
        bool enabled = true,
        (string Key, bool OnValue)? flag = null)
    {
        var document = JsonDocument.Parse(text);
        return document.SettingMember(
            copy.Name,
            [serversKey],
            RenderJson(copy, draft, resolve, document, serversKey, schema, enabled, flag));
    }

    private static string InsertToml(
        string text,
        PortableServer copy,
        ServerDraft draft,
        Func<PortableValue, string> resolve,
        string serversKey,
        bool enabled)
    {
        // Codex tells transports apart by key, not by a `type`: a remote server is a
        // `url`, as ServerAuthoring writes it too. A Codex URL that merely ends in /sse
        // was inferred as SSE on export.

        // The definition is switched off on its own table: nothing is parked, and
        // switching it on later is one line.
        var block = draft.TomlBlock(serversKey, enabled ? null : false);
        if (copy.WorkingDirectory is { } cwd)
        {
            // A scalar has to come before the optional `env` sub-table's header.
            var lines = block.Split('\n').ToList();
            lines.Insert(1, $"cwd = {TomlText.String(resolve(cwd))}");
            block = string.Join('\n', lines);
        }
        if (copy.Headers.Count > 0)
        {
            var table = $"[{TomlText.Path([serversKey, draft.TrimmedName, "http_headers"])}]";
            var rows = copy.Headers.Keys
                .Order(StringComparer.Ordinal)
                .Select(key => $"{TomlText.Key(key)} = {TomlText.String(resolve(copy.Headers[key]))}");
            block += "\n\n" + table + "\n" + string.Join('\n', rows);
        }
        return TomlDocument.Parse(text).SettingServer(draft.TrimmedName, serversKey, block);
    }

    /// <summary>
    /// Adds the imported names to a deny list, leaving every entry already there
    /// and every other key in the file exactly as they were.
    /// </summary>
    private static string DenyText(
        string text,
        EnablementStrategy.DenyList deny,
        IReadOnlyList<string> names,
        string display)
    {
        try
        {
            foreach (var name in names)
            {
                var document = JsonDocument.Parse(text);
                var already = document.Root[deny.Key]?.Elements?.Any(element =>
                    (element[deny.NameField]?.StringValue ?? element.StringValue) is { } entry &&
                    Server.Identity(entry) == Server.Identity(name)) ?? false;
                if (already) continue;

                text = document.AppendingElement(
                    document.ObjectLiteral([(deny.NameField, JsonText.String(name))], [deny.Key]),
                    [deny.Key]);
            }
            return text;
        }
        catch (JsonEditException error)
        {
            throw ConfigWriteException.Unreadable(display, error.Message);
        }
    }
}
