using System.Text;

namespace Kytto.Core.Json;

public enum JsonEditErrorKind
{
    PathNotFound,
    NotAnObject,
    NotAnArray,

    /// <summary>
    /// The value is not what the caller last read there, so the edit it was
    /// planned against no longer applies.
    /// </summary>
    ValueMismatch,
}

public sealed class JsonEditException(JsonEditErrorKind kind, IReadOnlyList<string> path)
    : Exception(Describe(kind, path))
{
    public JsonEditErrorKind Kind { get; } = kind;
    public IReadOnlyList<string> Path { get; } = path;

    private static string Describe(JsonEditErrorKind kind, IReadOnlyList<string> path)
    {
        var where = path.Count == 0 ? "the top level" : string.Join(" -> ", path);
        return kind switch
        {
            JsonEditErrorKind.PathNotFound => $"No value at {where}.",
            JsonEditErrorKind.NotAnObject => $"{where} is not an object.",
            JsonEditErrorKind.ValueMismatch =>
                $"{where} no longer says what Kytto read there, so nothing was changed.",
            _ => $"{where} is not an array.",
        };
    }
}

/// <summary>Structural edits, expressed as splices.</summary>
/// <remarks>
/// <para>
/// Every operation returns the original text with one range replaced. Nothing is
/// regenerated, so keys Kytto has never heard of, comments, blank lines and the
/// user's indentation all survive untouched (§6.3).
/// </para>
/// <para>
/// The fiddly part is commas. Removing a member has to leave the file exactly as
/// if that member had never been typed — no stranded comma, no orphaned blank
/// line, and no change to the line that follows.
/// </para>
/// </remarks>
public static class JsonEditor
{
    // MARK: - Objects

    /// <summary>
    /// Sets <paramref name="key"/> in the object at <paramref name="path"/> to
    /// already-serialized JSON. Creates the object, and the key, if either is
    /// missing.
    /// </summary>
    public static string SettingMember(
        this JsonDocument self,
        string key,
        IReadOnlyList<string> path,
        string rawValue)
    {
        var prepared = self.EnsuringContainer(path, "{}");
        var document = JsonDocument.Parse(prepared);

        var container = document.Root.ValueAt([.. path])
            ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
        var members = container.Members
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnObject, path);

        // Existing key: replace just the value, leaving the key and its formatting
        // alone.
        if (members.FirstOrDefault(member => member.Key == key) is { } existing)
        {
            return document.Replacing(existing.Value.Span, rawValue);
        }

        var entry = $"{JsonText.String(key)}: {rawValue}";

        if (members.Count == 0)
        {
            return document.ReplacingEmptyContainer(container, entry);
        }

        // Match the siblings: same line indentation, same one-line-or-not shape.
        var last = members[^1];
        var indent = document.LineIndent(last.Span.Start);
        var isMultiline = document.ContainsNewline(container.Span.Start, last.Span.Start);
        var insertion = isMultiline ? $",{document.Newline()}{indent}{entry}" : $", {entry}";
        return document.Replacing(new JsonSpan(last.Span.End, last.Span.End), insertion);
    }

    /// <summary>
    /// Removes <paramref name="key"/> from the object at <paramref name="path"/>.
    /// A missing key is not an error — the desired end state is already true.
    /// </summary>
    public static string RemovingMember(
        this JsonDocument self,
        string key,
        IReadOnlyList<string> path)
    {
        var container = self.Root.ValueAt([.. path])
            ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
        var members = container.Members
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnObject, path);

        var index = -1;
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i].Key == key) { index = i; break; }
        }
        if (index < 0) return self.SourceText;

        var spans = members.Select(member => member.Span).ToArray();
        return self.Replacing(self.RemovalSpan(index, spans), "");
    }

    // MARK: - Arrays

    /// <summary>
    /// Appends already-serialized JSON to the array at <paramref name="path"/>,
    /// creating the array if missing.
    /// </summary>
    public static string AppendingElement(
        this JsonDocument self,
        string rawValue,
        IReadOnlyList<string> path)
    {
        var prepared = self.EnsuringContainer(path, "[]");
        var document = JsonDocument.Parse(prepared);

        var container = document.Root.ValueAt([.. path])
            ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
        var elements = container.Elements
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnArray, path);

        if (elements.Count == 0)
        {
            return document.ReplacingEmptyContainer(container, rawValue);
        }

        var last = elements[^1];
        var indent = document.LineIndent(last.Span.Start);
        var isMultiline = document.ContainsNewline(container.Span.Start, last.Span.Start);
        var insertion = isMultiline ? $",{document.Newline()}{indent}{rawValue}" : $", {rawValue}";
        return document.Replacing(new JsonSpan(last.Span.End, last.Span.End), insertion);
    }

    /// <summary>
    /// Builds an object literal shaped like the entries already in the array at
    /// <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Anything Kytto adds should be indistinguishable from what was there before.
    /// A file whose deny-list entries are spread over three lines each should not
    /// suddenly gain a one-liner because that was easier to generate.
    /// </remarks>
    public static string ObjectLiteral(
        this JsonDocument self,
        IReadOnlyList<(string Key, string Raw)> pairs,
        IReadOnlyList<string> path)
    {
        var first = self.Root.ValueAt([.. path])?.Elements?.FirstOrDefault();
        if (first is null || !self.ContainsNewline(first.Span.Start, first.Span.End))
        {
            return JsonText.InlineObject(pairs);
        }
        return JsonText.BlockObject(
            pairs,
            self.LineIndent(first.Span.Start),
            IndentStyle.Detect(self.SourceText).Text,
            self.Newline());
    }

    /// <summary>Removes every element of the array at <paramref name="path"/> matching a predicate.</summary>
    public static string RemovingElements(
        this JsonDocument self,
        IReadOnlyList<string> path,
        Func<JsonNode, bool> predicate)
    {
        var container = self.Root.ValueAt([.. path]);
        if (container is null) return self.SourceText;
        var elements = container.Elements
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnArray, path);

        var doomed = Enumerable.Range(0, elements.Count)
            .Where(index => predicate(elements[index]))
            .ToArray();
        if (doomed.Length == 0) return self.SourceText;

        // Back to front, so each removal's offsets are still valid.
        var text = self.SourceText;
        foreach (var index in doomed.Reverse())
        {
            var document = JsonDocument.Parse(text);
            var current = document.Root.ValueAt([.. path])?.Elements;
            if (current is null || index >= current.Count) continue;
            var span = document.RemovalSpan(index, current.Select(node => node.Span).ToArray());
            text = document.Replacing(span, "");
        }
        return text;
    }

    /// <summary>
    /// Replaces one string in the array at <paramref name="path"/> and nothing
    /// else — not the array, not its separators, not the elements either side (§6.3).
    /// </summary>
    /// <param name="position">
    /// Counts string elements only, the way <c>ServerFields</c> reads <c>args</c>,
    /// so an index taken from the model lands on the same element.
    /// </param>
    /// <param name="expecting">
    /// What that element must still say. A file that moved since the edit was
    /// planned is refused rather than patched somewhere else.
    /// </param>
    public static string ReplacingString(
        this JsonDocument self,
        IReadOnlyList<string> path,
        int position,
        string expecting,
        string value)
    {
        var container = self.Root.ValueAt([.. path])
            ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, path);
        var elements = container.Elements
            ?? throw new JsonEditException(JsonEditErrorKind.NotAnArray, path);

        var strings = elements.Where(element => element.StringValue is not null).ToArray();
        if (position < 0 || position >= strings.Length ||
            !string.Equals(strings[position].StringValue, expecting, StringComparison.Ordinal))
        {
            throw new JsonEditException(JsonEditErrorKind.ValueMismatch, path);
        }
        return self.Replacing(strings[position].Span, JsonText.String(value));
    }

    // MARK: - Shared removal geometry

    /// <summary>The range to delete so that an item vanishes without a trace.</summary>
    /// <remarks>
    /// Three cases, and they differ in which comma is theirs to remove:
    /// <list type="bullet">
    /// <item>Followed by a comma: take the item, that comma, and the rest of the
    /// line if nothing else is on it.</item>
    /// <item>Last of several: take the <em>preceding</em> comma and the item, but
    /// leave the line ending alone — it belongs to the closing brace.</item>
    /// <item>The only item: take the item and its line.</item>
    /// </list>
    /// </remarks>
    internal static JsonSpan RemovalSpan(this JsonDocument self, int index, IReadOnlyList<JsonSpan> spans)
    {
        var span = spans[index];
        var bytes = self.SourceBytes;

        if (self.OffsetOfCommaAfter(span.End) is { } comma)
        {
            var end = comma + 1;
            if (self.EndOfLineIfBlank(end) is { } lineEnd)
            {
                // Multi-line: take the line ending too, leaving the next member to
                // start its own line at its own indentation.
                end = lineEnd;
            }
            else
            {
                // Same line: take the space the comma was separating with, so the
                // next member closes up rather than sitting on a gap.
                while (end < bytes.Length && IsHorizontalSpace(bytes[end])) end++;
            }
            return new JsonSpan(self.BlankLinePrefixStart(span.Start) ?? span.Start, end);
        }

        if (index > 0 && self.OffsetOfCommaBefore(span.Start) is { } previous)
        {
            return new JsonSpan(previous, span.End);
        }

        var only = span.End;
        if (self.EndOfLineIfBlank(only) is { } blank) only = blank;
        return new JsonSpan(self.BlankLinePrefixStart(span.Start) ?? span.Start, only);
    }

    // MARK: - Container creation

    /// <summary>
    /// Ensures every step of <paramref name="path"/> exists, creating what is
    /// missing.
    /// </summary>
    private static string EnsuringContainer(
        this JsonDocument self,
        IReadOnlyList<string> path,
        string empty)
    {
        if (path.Count == 0) return self.SourceText;

        var text = self.SourceText;
        for (var depth = 0; depth < path.Count; depth++)
        {
            var prefix = path.Take(depth).ToArray();
            var key = path[depth];
            var document = JsonDocument.Parse(text);

            var parent = document.Root.ValueAt(prefix)
                ?? throw new JsonEditException(JsonEditErrorKind.PathNotFound, prefix);
            if (parent.Members is null)
            {
                throw new JsonEditException(JsonEditErrorKind.NotAnObject, prefix);
            }
            if (parent[key] is not null) continue;

            // The last step gets the requested container type; anything above it
            // has to be an object to hold the next key.
            var placeholder = depth == path.Count - 1 ? empty : "{}";
            text = document.SettingMember(key, prefix, placeholder);
        }
        return text;
    }

    /// <summary>Replaces <c>{}</c> or <c>[]</c> with a populated, indented version of itself.</summary>
    private static string ReplacingEmptyContainer(this JsonDocument self, JsonNode container, string entry)
    {
        var open = (char)self.SourceBytes[container.Span.Start];
        var close = open == '{' ? '}' : ']';
        var baseIndent = self.LineIndent(container.Span.Start);
        var inner = baseIndent + IndentStyle.Detect(self.SourceText).Text;
        var newline = self.Newline();
        return self.Replacing(
            container.Span,
            $"{open}{newline}{inner}{entry}{newline}{baseIndent}{close}");
    }

    // MARK: - Byte scanning

    private static bool IsHorizontalSpace(byte b) => b is 0x20 or 0x09;

    private static bool IsSpace(byte b) => IsHorizontalSpace(b) || b is 0x0A or 0x0D;

    /// <summary>Offset of the comma directly after a point, ignoring spaces.</summary>
    private static int? OffsetOfCommaAfter(this JsonDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset;
        while (index < bytes.Length && IsSpace(bytes[index])) index++;
        return index < bytes.Length && bytes[index] == (byte)',' ? index : null;
    }

    /// <summary>Offset of the comma directly before a point, ignoring whitespace.</summary>
    private static int? OffsetOfCommaBefore(this JsonDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset - 1;
        while (index >= 0 && IsSpace(bytes[index])) index--;
        return index >= 0 && bytes[index] == (byte)',' ? index : null;
    }

    /// <summary>Offset just past the newline, if everything up to it is blank.</summary>
    private static int? EndOfLineIfBlank(this JsonDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset;
        while (index < bytes.Length && IsHorizontalSpace(bytes[index])) index++;
        if (index >= bytes.Length) return null;
        if (bytes[index] == 0x0D && index + 1 < bytes.Length && bytes[index + 1] == 0x0A)
        {
            return index + 2;
        }
        return bytes[index] == 0x0A ? index + 1 : null;
    }

    /// <summary>Start of the line containing a point, if only whitespace precedes it there.</summary>
    private static int? BlankLinePrefixStart(this JsonDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var index = offset - 1;
        while (index >= 0 && IsHorizontalSpace(bytes[index])) index--;
        return index < 0 || bytes[index] == 0x0A ? index + 1 : null;
    }

    /// <summary>The whitespace that starts the line containing a point.</summary>
    internal static string LineIndent(this JsonDocument self, int offset)
    {
        var bytes = self.SourceBytes;
        var start = offset;
        while (start > 0 && bytes[start - 1] != 0x0A) start--;
        var end = start;
        while (end < bytes.Length && IsHorizontalSpace(bytes[end])) end++;
        var stop = Math.Min(end, offset);
        return stop <= start ? "" : Encoding.UTF8.GetString(bytes, start, stop - start);
    }

    internal static bool ContainsNewline(this JsonDocument self, int start, int end)
    {
        if (start >= end || end > self.SourceBytes.Length) return false;
        for (var index = start; index < end; index++)
        {
            if (self.SourceBytes[index] == 0x0A) return true;
        }
        return false;
    }

    private static string Newline(this JsonDocument self) =>
        self.SourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
