using System.Globalization;
using System.Text;

namespace Kytto.Core.Json;

/// <summary>
/// Enough of a JSON writer to produce the small fragments the editor splices in.
/// </summary>
/// <remarks>
/// Deliberately not a general serializer. Whole values are never regenerated from
/// the model — when a parked server comes back, its original source text is
/// spliced in verbatim. This only builds the few literals Kytto itself authors.
/// </remarks>
public static class JsonText
{
    public static string String(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        result.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        result.Append(c);
                    }
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>A flat object on one line, e.g. <c>{ "serverName": "github" }</c>.</summary>
    public static string InlineObject(IReadOnlyList<(string Key, string Raw)> pairs)
    {
        if (pairs.Count == 0) return "{}";
        var body = string.Join(", ", pairs.Select(pair => $"{String(pair.Key)}: {pair.Raw}"));
        return $"{{ {body} }}";
    }

    /// <summary>An object spread over several lines, closing at <paramref name="baseIndent"/>.</summary>
    public static string BlockObject(
        IReadOnlyList<(string Key, string Raw)> pairs,
        string baseIndent,
        string unit,
        string newline = "\n")
    {
        if (pairs.Count == 0) return "{}";
        var inner = baseIndent + unit;
        var body = string.Join("," + newline, pairs.Select(pair => $"{inner}{String(pair.Key)}: {pair.Raw}"));
        return $"{{{newline}{body}{newline}{baseIndent}}}";
    }
}

/// <summary>
/// The indentation a file already uses, so anything Kytto inserts looks like the
/// user (or the client app) wrote it.
/// </summary>
public sealed record IndentStyle(string Text)
{
    public static readonly IndentStyle TwoSpaces = new("  ");
    public static readonly IndentStyle Tab = new("\t");

    /// <summary>Infers the unit from the first indented line in the source.</summary>
    /// <remarks>
    /// Claude Code writes two spaces, Claude Desktop has been seen with tabs, and a
    /// user who reformatted by hand could have anything. Guessing wrong is not
    /// dangerous, only ugly, so one sample is enough.
    /// </remarks>
    public static IndentStyle Detect(string source)
    {
        foreach (var line in source.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t') return Tab;
            if (line[0] != ' ') continue;

            var count = 0;
            while (count < line.Length && line[count] == ' ') count++;
            // A continuation line inside a wrapped value could be indented oddly;
            // anything past 8 is not an indent unit.
            if (count is >= 1 and <= 8) return new IndentStyle(new string(' ', count));
        }
        return TwoSpaces;
    }
}
