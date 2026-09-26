using System.Globalization;
using System.Text;
using Kytto.Core.Model;

namespace Kytto.Core.Toml;

public enum TomlEditErrorKind
{
    TableNotFound,
    NotATable,
    /// <summary>
    /// The entry exists, but as an inline table on one line rather than as a
    /// <c>[header]</c> of its own. Kytto reads those; it will not rewrite one,
    /// because doing it safely means re-serialising a line it did not write.
    /// </summary>
    InlineDefinition,
}

public sealed class TomlEditException(TomlEditErrorKind kind, string subject)
    : Exception(Describe(kind, subject))
{
    public TomlEditErrorKind Kind { get; } = kind;
    public string Subject { get; } = subject;

    private static string Describe(TomlEditErrorKind kind, string subject) => kind switch
    {
        TomlEditErrorKind.TableNotFound => $"No table at {subject}.",
        TomlEditErrorKind.NotATable => $"{subject} is not a table.",
        _ => $"\"{subject}\" is written as an inline table. Kytto will not rewrite that line.",
    };
}

/// <summary>Structural edits, expressed as splices.</summary>
/// <remarks>
/// <para>
/// Same contract as the JSON editor: every operation returns the original text
/// with one range replaced, so comments, blank lines, key order and the forty-odd
/// <c>[projects."…"]</c> tables in a real Codex config all survive untouched (§6.3).
/// </para>
/// <para>
/// The fiddly part here is not commas but <em>lines</em>. A table is a header plus
/// everything under it until the next header, and a server's <c>env</c> lives in a
/// second table of its own — so removing one server means removing two tables and
/// exactly the right number of blank lines around them.
/// </para>
/// </remarks>
public static class TomlEditor
{
    /// <summary>Adds or replaces a server's definition.</summary>
    /// <param name="block">
    /// A rendered table block, headers and all, as produced by <see cref="TomlBuilder"/>.
    /// Replacing an existing server swaps the whole block, which is what makes an
    /// edit that drops an env var actually drop it.
    /// </param>
    public static string SettingServer(
        this TomlDocument self,
        string name,
        string key,
        string block)
    {
        block = self.WithPreferredLineEndings(block);
        if (self.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }

        var existing = self.ServerTables(name, key);
        if (existing.Count > 0)
        {
            return self.Replacing(
                new TomlSpan(self.LineStart(existing[0].Span.Start), existing[^1].Span.End),
                block);
        }

        // New server. Keep it with the others if there are others, so the file
        // stays grouped the way its owner left it.
        var siblings = self.ChildTables(key);
        if (siblings.Count > 0)
        {
            var last = siblings[^1];
            var under = self.TablesUnder(last.Path);
            var anchor = (under.Count > 0 ? under[^1] : last).Span.End;
            var newline = self.Newline();
            return self.Replacing(new TomlSpan(anchor, anchor), newline + newline + block);
        }

        var end = self.SourceBytes.Length;
        var lineEnding = self.Newline();
        var separator = self.SourceText.Length == 0
            ? ""
            : self.EndsWithBlankLine() ? "" : self.EndsWithNewline() ? lineEnding : lineEnding + lineEnding;
        return self.Replacing(new TomlSpan(end, end), separator + block + lineEnding);
    }

    /// <summary>
    /// Removes a server's tables entirely, leaving the file as if it had never been
    /// typed — no stranded header, no doubled blank line.
    /// </summary>
    public static string RemovingServer(this TomlDocument self, string name, string key)
    {
        if (self.Table(key)?.Pair(name)?.Value.InlinePairs is not null)
        {
            throw new TomlEditException(TomlEditErrorKind.InlineDefinition, name);
        }

        var tables = self.ServerTables(name, key);
        if (tables.Count == 0) return self.SourceText;

        var start = self.LineStart(tables[0].Span.Start);
        var end = self.EndOfLine(tables[^1].Span.End);

        // Take the blank lines that followed it. If there were none — because it
        // was last in the file — take the blank line that preceded it instead, so
        // the gap it leaves behind is the one it was occupying.
        var afterBlanks = self.SkippingBlankLines(end);
        if (afterBlanks > end)
        {
            end = afterBlanks;
        }
        else
        {
            start = self.StartSkippingBlankLines(start);
        }

        return self.Replacing(new TomlSpan(start, end), "");
    }

    /// <summary>
    /// Sets a boolean on the server's own table, creating the key if it is not
    /// there yet.
    /// </summary>
    /// <remarks>
    /// This is Codex's off switch, and it is a better one than most: the definition
    /// stays in the file, so nothing has to be parked and switching back on cannot
    /// lose anything.
    /// </remarks>
    public static string SettingServerFlag(
        this TomlDocument self,
        bool value,
        string flagKey,
        string name,
        string key)
    {
        var table = self.Table(key, name)
            ?? throw new TomlEditException(TomlEditErrorKind.TableNotFound, $"{key}.{name}");

        if (table.Pair(flagKey) is { } pair)
        {
            return self.Replacing(pair.Value.Span, TomlText.Bool(value));
        }

        // After the last key of this table, which is before any `[…env]` header —
        // a bare key after a sub-table header would belong to the sub-table.
        var anchor = table.Span.End;
        return self.Replacing(
            new TomlSpan(anchor, anchor),
            $"{self.Newline()}{TomlText.Key(flagKey)} = {TomlText.Bool(value)}");
    }

    // MARK: - Line geometry

    private static bool EndsWithNewline(this TomlDocument self) =>
        self.SourceBytes.Length > 0 && self.SourceBytes[^1] == 0x0A;

    private static string Newline(this TomlDocument self) =>
        self.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string WithPreferredLineEndings(this TomlDocument self, string text)
    {
        var newline = self.Newline();
        if (newline == "\n") return text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);
    }

    private static bool EndsWithBlankLine(this TomlDocument self)
    {
        var bytes = self.SourceBytes;
        if (bytes.Length < 2) return false;
        var index = bytes.Length - 1;
        if (bytes[index] != 0x0A) return false;
        index--;
        while (index >= 0 && bytes[index] is 0x0D or 0x20 or 0x09) index--;
        return index >= 0 && bytes[index] == 0x0A;
    }

    private static int LineStart(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = Math.Min(offset, bytes.Length);
        while (index > 0 && bytes[index - 1] != 0x0A) index--;
        return index;
    }

    /// <summary>Just past the newline that ends the line containing an offset.</summary>
    private static int EndOfLine(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = Math.Min(offset, bytes.Length);
        while (index < bytes.Length && bytes[index] != 0x0A) index++;
        return index < bytes.Length ? index + 1 : index;
    }

    private static int? IsBlankLine(this TomlDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset;
        while (index < bytes.Length && bytes[index] is 0x20 or 0x09 or 0x0D) index++;
        return index < bytes.Length && bytes[index] == 0x0A ? index + 1 : null;
    }

    private static int SkippingBlankLines(this TomlDocument self, int offset)
    {
        var index = offset;
        while (self.IsBlankLine(index) is { } next) index = next;
        return index;
    }

    private static int StartSkippingBlankLines(this TomlDocument self, int offset)
    {
        var index = offset;
        while (index > 0)
        {
            var previousLine = self.LineStart(index - 1);
            if (self.IsBlankLine(previousLine) is null) break;
            index = previousLine;
        }
        return index;
    }
}

// MARK: - Rendering

/// <summary>Literals, spelled the way TOML spells them.</summary>
public static class TomlText
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
                    if (c < 0x20) result.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    else result.Append(c);
                    break;
            }
        }
        return result.Append('"').ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Array(IReadOnlyList<string> values) =>
        "[" + string.Join(", ", values.Select(String)) + "]";

    /// <summary>A bare key where TOML allows one, a quoted key where it does not.</summary>
    /// <remarks>
    /// Server names reach this from a config file Kytto did not write, so "allows
    /// one" has to be checked rather than assumed.
    /// </remarks>
    public static string Key(string value)
    {
        var bare = value.Length > 0 && value.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
        return bare ? value : String(value);
    }

    /// <summary>A dotted path, each component quoted only if it has to be.</summary>
    public static string Path(IReadOnlyList<string> components) =>
        string.Join(".", components.Select(Key));
}

/// <summary>Renders a server definition as a TOML table block.</summary>
/// <remarks>
/// Shaped after what <c>codex mcp add</c> itself writes — a table per server with
/// the environment in a sub-table — so a config Kytto has edited still looks like
/// one Codex wrote, and a diff shows only what changed.
/// </remarks>
public static class TomlBuilder
{
    public static string ServerBlock(
        string name,
        string key,
        string? command,
        IReadOnlyList<string> args,
        string? url,
        IReadOnlyList<EnvEntry> env,
        bool? enabled)
    {
        var lines = new List<string> { $"[{TomlText.Path([key, name])}]" };

        if (!string.IsNullOrEmpty(url))
        {
            lines.Add($"url = {TomlText.String(url)}");
        }
        else if (!string.IsNullOrEmpty(command))
        {
            lines.Add($"command = {TomlText.String(command)}");
            if (args.Count > 0) lines.Add($"args = {TomlText.Array(args)}");
        }

        if (enabled is { } flag) lines.Add($"enabled = {TomlText.Bool(flag)}");

        var realEnv = env.Where(entry => !string.IsNullOrWhiteSpace(entry.Key)).ToArray();
        if (realEnv.Length > 0)
        {
            lines.Add("");
            lines.Add($"[{TomlText.Path([key, name, "env"])}]");
            foreach (var entry in realEnv)
            {
                lines.Add($"{TomlText.Key(entry.Key)} = {TomlText.String(entry.Value ?? "")}");
            }
        }

        return string.Join("\n", lines);
    }
}
