using System.Text;

namespace Kytto.Core.Toml;

/// <summary>
/// A TOML document that keeps its original text and knows where every table, key
/// and value lives in it.
/// </summary>
/// <remarks>
/// <para>
/// The same contract as <c>JsonDocument</c>, for the same reason: Kytto never
/// regenerates a client's config from its own model (§6.3). It parses, finds the
/// byte range of the one thing it needs to change, and splices. A Codex
/// <c>config.toml</c> is not a small file — the one this was written against
/// carries forty-odd <c>[projects."…"]</c> tables alongside the four servers — and
/// every byte of it that Kytto did not deliberately change has to come out the
/// other side identical.
/// </para>
/// <para>
/// This is deliberately not a complete TOML implementation. It reads structure
/// exactly, and values only as far as Kytto needs them; anything it cannot
/// interpret it records as raw text and leaves alone, which is the behaviour §6.3
/// wants anyway.
/// </para>
/// </remarks>
public sealed class TomlDocument : IConfigDocument<TomlDocument>
{
    /// <summary>
    /// Nothing. An empty TOML file is a valid TOML file holding no tables, and
    /// unlike JSON there is no punctuation to seed.
    /// </summary>
    public static string EmptySource => "";

    public static string FormatName => "TOML";

    private TomlDocument(string sourceText, byte[] sourceBytes, IReadOnlyList<TomlTable> tables)
    {
        SourceText = sourceText;
        SourceBytes = sourceBytes;
        Tables = tables;
    }

    /// <summary>The source exactly as it was read.</summary>
    public string SourceText { get; }

    /// <summary>UTF-8 view of <see cref="SourceText"/>. Spans index into this.</summary>
    public byte[] SourceBytes { get; }

    /// <summary>
    /// Every table in the file, in the order they appear. The first is the implicit
    /// root table holding whatever sits above the first header.
    /// </summary>
    public IReadOnlyList<TomlTable> Tables { get; }

    public static TomlDocument Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var parser = new TomlParser(bytes);
        return new TomlDocument(text, bytes, parser.ParseDocument());
    }

    public static TomlDocument ParseFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        string text;
        try
        {
            text = strict.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new TomlParseException(TomlParseErrorKind.InvalidUtf8, 0, 1, 1);
        }
        return Parse(text);
    }

    // MARK: - Lookup

    /// <summary>The raw source text covered by a span.</summary>
    public string Slice(TomlSpan span)
    {
        if (span.Start < 0 || span.End > SourceBytes.Length || span.Start > span.End) return "";
        return Encoding.UTF8.GetString(SourceBytes, span.Start, span.Length);
    }

    public TomlTable? Table(params string[] path) =>
        Tables.FirstOrDefault(table => table.Path.SequenceEqual(path));

    /// <summary>
    /// Direct children of <paramref name="path"/> — <c>[a.b]</c> is a child of
    /// <c>[a]</c>, <c>[a.b.c]</c> is not.
    /// </summary>
    /// <remarks>
    /// Used to list the servers under <c>mcp_servers</c> without also picking up
    /// each server's own <c>[mcp_servers.&lt;name&gt;.env]</c>.
    /// </remarks>
    public IReadOnlyList<TomlTable> ChildTables(params string[] path) =>
        ChildTablesOf(path);

    /// <summary>Direct children of a path given as a list.</summary>
    public IReadOnlyList<TomlTable> ChildTablesOf(IReadOnlyList<string> path) =>
        Tables.Where(table =>
            table.Path.Count == path.Count + 1 &&
            table.Path.Take(path.Count).SequenceEqual(path))
            .ToArray();

    /// <summary>
    /// Every table below <paramref name="path"/>, which is what has to be removed
    /// together when a server goes: its own table and its <c>env</c> sub-table are
    /// one thing to the user and two tables to TOML.
    /// </summary>
    public IReadOnlyList<TomlTable> TablesUnder(IReadOnlyList<string> path) =>
        Tables.Where(table =>
            table.Path.Count > path.Count &&
            table.Path.Take(path.Count).SequenceEqual(path))
            .ToArray();

    // MARK: - Splicing

    /// <summary>
    /// Returns the source with <paramref name="span"/> replaced and every other
    /// byte left alone.
    /// </summary>
    /// <remarks>
    /// As in <c>JsonDocument</c>, this is the only way new config text is produced.
    /// If a change cannot be expressed as span replacements, it does not get written.
    /// </remarks>
    public string Replacing(TomlSpan span, string replacement) =>
        Replacing([(span, replacement)]);

    /// <summary>
    /// Applies several replacements at once. Spans must not overlap; they are
    /// applied back to front so earlier offsets stay valid.
    /// </summary>
    public string Replacing(IEnumerable<(TomlSpan Span, string Replacement)> edits)
    {
        var ordered = edits.OrderByDescending(edit => edit.Span.Start).ToArray();
        var result = new List<byte>(SourceBytes);
        foreach (var (span, replacement) in ordered)
        {
            result.RemoveRange(span.Start, span.Length);
            result.InsertRange(span.Start, Encoding.UTF8.GetBytes(replacement));
        }
        return Encoding.UTF8.GetString(result.ToArray());
    }
}

// MARK: - Model

public readonly record struct TomlSpan(int Start, int End)
{
    public int Length => End - Start;
    public bool IsEmpty => Start >= End;
}

public enum TomlKind
{
    String,
    Bool,
    Integer,
    Array,
    InlineTable,
    /// <summary>
    /// Floats, dates, times — read as raw text. Kytto has no use for them and
    /// interpreting a value it will never write is a way to get it wrong.
    /// </summary>
    Other,
}

public sealed class TomlValue
{
    private readonly object? _payload;

    internal TomlValue(TomlKind kind, TomlSpan span, object? payload = null)
    {
        Kind = kind;
        Span = span;
        _payload = payload;
    }

    public TomlKind Kind { get; }
    public TomlSpan Span { get; }

    public string? StringValue => Kind == TomlKind.String ? (string)_payload! : null;
    public bool? BoolValue => Kind == TomlKind.Bool ? (bool)_payload! : null;
    public long? IntegerValue => Kind == TomlKind.Integer ? (long)_payload! : null;

    public IReadOnlyList<TomlValue>? Elements =>
        Kind == TomlKind.Array ? (IReadOnlyList<TomlValue>)_payload! : null;

    public IReadOnlyList<TomlPair>? InlinePairs =>
        Kind == TomlKind.InlineTable ? (IReadOnlyList<TomlPair>)_payload! : null;
}

/// <param name="Key">Dotted keys arrive split: <c>a.b = 1</c> is <c>["a", "b"]</c>.</param>
/// <param name="Span">The whole <c>key = value</c>, without its line ending.</param>
public sealed record TomlPair(
    IReadOnlyList<string> Key,
    TomlSpan KeySpan,
    TomlValue Value,
    TomlSpan Span)
{
    public string? Name => Key.Count == 1 ? Key[0] : null;
}

/// <param name="Path"><c>[]</c> for the implicit root table above the first header.</param>
/// <param name="HeaderSpan">Null for the root table, which has no header line.</param>
/// <param name="Span">
/// Header start through the end of the last line belonging to this table.
/// Deliberately stops at the last piece of content rather than running up to the
/// next header: blank lines and comments sitting before a header describe the
/// table that follows, and eating them would move somebody's note.
/// </param>
/// <param name="IsArrayElement">
/// <c>[[a.b]]</c> rather than <c>[a.b]</c>. Kytto does not author these, but it
/// has to recognise one so it never mistakes it for a table it may rewrite.
/// </param>
public sealed record TomlTable(
    IReadOnlyList<string> Path,
    TomlSpan? HeaderSpan,
    IReadOnlyList<TomlPair> Pairs,
    TomlSpan Span,
    bool IsArrayElement)
{
    public TomlPair? Pair(string name) =>
        Pairs.FirstOrDefault(pair => pair.Key.Count == 1 && pair.Key[0] == name);

    public TomlValue? Value(string name) => Pair(name)?.Value;
}

// MARK: - Errors

public enum TomlParseErrorKind
{
    InvalidUtf8,
    UnexpectedEndOfInput,
    UnexpectedCharacter,
    ExpectedEquals,
    ExpectedKey,
    UnterminatedString,
    UnterminatedArray,
    UnterminatedInlineTable,
    UnterminatedTableHeader,
}

public sealed class TomlParseException(
    TomlParseErrorKind kind,
    int offset,
    int line,
    int column,
    byte character = 0)
    : Exception(Describe(kind, line, column, character))
{
    public TomlParseErrorKind Kind { get; } = kind;
    public int Offset { get; } = offset;
    public int Line { get; } = line;
    public int Column { get; } = column;

    private static string Describe(TomlParseErrorKind kind, int line, int column, byte character)
    {
        var what = kind switch
        {
            TomlParseErrorKind.InvalidUtf8 => "file is not valid UTF-8",
            TomlParseErrorKind.UnexpectedEndOfInput => "unexpected end of input",
            TomlParseErrorKind.UnexpectedCharacter => $"unexpected character '{(char)character}'",
            TomlParseErrorKind.ExpectedEquals => "expected '='",
            TomlParseErrorKind.ExpectedKey => "expected a key",
            TomlParseErrorKind.UnterminatedString => "unterminated string",
            TomlParseErrorKind.UnterminatedArray => "unterminated array",
            TomlParseErrorKind.UnterminatedInlineTable => "unterminated inline table",
            TomlParseErrorKind.UnterminatedTableHeader => "unterminated table header",
            _ => "malformed TOML",
        };
        return $"{what} at line {line}, column {column}";
    }
}

// MARK: - Parser

/// <summary>Scanner over UTF-8 bytes.</summary>
/// <remarks>
/// TOML looks line-oriented and mostly is, but the exceptions are exactly the ones
/// that would corrupt a file if guessed at: a multi-line string can contain a line
/// that starts with <c>[</c>, and an array can run over a dozen lines. So this
/// consumes real values rather than splitting on newlines, and a <c>[</c> is only a
/// table header when it is found where a header can be.
/// </remarks>
internal ref struct TomlParser(byte[] bytes)
{
    private readonly byte[] _bytes = bytes;
    private int _index = 0;

    internal List<TomlTable> ParseDocument()
    {
        SkipByteOrderMark();

        var tables = new List<TomlTable>();
        IReadOnlyList<string> path = [];
        TomlSpan? headerSpan = null;
        var isArrayElement = false;
        var pairs = new List<TomlPair>();
        var contentEnd = 0;

        void CloseTable()
        {
            // A root table with nothing above the first header is not a table
            // anybody wrote, and emitting it would give the editor a span to insert
            // into that does not exist.
            if (headerSpan is null && pairs.Count == 0 && path.Count == 0 &&
                tables.Count == 0 && contentEnd == 0)
            {
                return;
            }
            tables.Add(new TomlTable(
                Path: path,
                HeaderSpan: headerSpan,
                Pairs: pairs.ToArray(),
                Span: new TomlSpan(headerSpan?.Start ?? 0, contentEnd),
                IsArrayElement: isArrayElement));
        }

        while (true)
        {
            SkipTrivia();
            if (_index >= _bytes.Length) break;

            if (_bytes[_index] == (byte)'[')
            {
                CloseTable();

                var start = _index;
                _index++;
                var doubled = _index < _bytes.Length && _bytes[_index] == (byte)'[';
                if (doubled) _index++;

                SkipInlineSpace();
                var key = ParseDottedKey();
                SkipInlineSpace();

                if (_index >= _bytes.Length || _bytes[_index] != (byte)']')
                {
                    throw Error(TomlParseErrorKind.UnterminatedTableHeader);
                }
                _index++;
                if (doubled)
                {
                    if (_index >= _bytes.Length || _bytes[_index] != (byte)']')
                    {
                        throw Error(TomlParseErrorKind.UnterminatedTableHeader);
                    }
                    _index++;
                }

                path = key;
                headerSpan = new TomlSpan(start, _index);
                isArrayElement = doubled;
                pairs = [];
                contentEnd = _index;
                continue;
            }

            var pair = ParsePair();
            pairs.Add(pair);
            contentEnd = pair.Span.End;
        }

        CloseTable();
        return tables;
    }

    // MARK: Trivia

    private void SkipByteOrderMark()
    {
        if (_bytes.Length >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF)
        {
            _index = 3;
        }
    }

    private static bool IsInlineSpace(byte b) => b is 0x20 or 0x09;

    private void SkipInlineSpace()
    {
        while (_index < _bytes.Length && IsInlineSpace(_bytes[_index])) _index++;
    }

    /// <summary>
    /// Whitespace, newlines and comments — everything that can sit between two
    /// meaningful things and is never part of either.
    /// </summary>
    private void SkipTrivia()
    {
        while (_index < _bytes.Length)
        {
            var b = _bytes[_index];
            if (IsInlineSpace(b) || b == 0x0A || b == 0x0D)
            {
                _index++;
            }
            else if (b == (byte)'#')
            {
                while (_index < _bytes.Length && _bytes[_index] != 0x0A) _index++;
            }
            else
            {
                return;
            }
        }
    }

    // MARK: Keys

    private List<string> ParseDottedKey()
    {
        var parts = new List<string> { ParseKeyPart() };
        while (true)
        {
            SkipInlineSpace();
            if (_index >= _bytes.Length || _bytes[_index] != (byte)'.') return parts;
            _index++;
            SkipInlineSpace();
            parts.Add(ParseKeyPart());
        }
    }

    private string ParseKeyPart()
    {
        if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.ExpectedKey);
        switch (_bytes[_index])
        {
            case (byte)'"': return ParseBasicString();
            case (byte)'\'': return ParseLiteralString();
            default:
            {
                var start = _index;
                while (_index < _bytes.Length && IsBareKeyByte(_bytes[_index])) _index++;
                if (_index <= start) throw Error(TomlParseErrorKind.ExpectedKey);
                return Encoding.UTF8.GetString(_bytes, start, _index - start);
            }
        }
    }

    private static bool IsBareKeyByte(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'_' or (byte)'-';

    // MARK: Pairs

    private TomlPair ParsePair()
    {
        var start = _index;
        var key = ParseDottedKey();
        var keySpan = new TomlSpan(start, _index);

        SkipInlineSpace();
        if (_index >= _bytes.Length || _bytes[_index] != (byte)'=')
        {
            throw Error(TomlParseErrorKind.ExpectedEquals);
        }
        _index++;
        SkipInlineSpace();

        var value = ParseValue();
        return new TomlPair(key, keySpan, value, new TomlSpan(start, value.Span.End));
    }

    // MARK: Values

    private TomlValue ParseValue()
    {
        if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnexpectedEndOfInput);
        var start = _index;

        switch (_bytes[_index])
        {
            case (byte)'"' or (byte)'\'':
            {
                var text = ParseAnyString();
                return new TomlValue(TomlKind.String, new TomlSpan(start, _index), text);
            }

            case (byte)'[':
            {
                var elements = ParseArray();
                return new TomlValue(TomlKind.Array, new TomlSpan(start, _index), elements);
            }

            case (byte)'{':
            {
                var pairs = ParseInlineTable();
                return new TomlValue(TomlKind.InlineTable, new TomlSpan(start, _index), pairs);
            }

            default:
            {
                // A bare token: true, false, a number, a date. Runs to the end of
                // the line or to whatever closes the container it sits in.
                while (_index < _bytes.Length)
                {
                    var b = _bytes[_index];
                    if (b is 0x0A or 0x0D or (byte)',' or (byte)']' or (byte)'}' or (byte)'#') break;
                    _index++;
                }

                // Trailing spaces before the delimiter are not part of the value.
                var end = _index;
                while (end > start && IsInlineSpace(_bytes[end - 1])) end--;

                var raw = Encoding.UTF8.GetString(_bytes, start, end - start);
                var span = new TomlSpan(start, end);
                _index = end;

                if (raw == "true") return new TomlValue(TomlKind.Bool, span, true);
                if (raw == "false") return new TomlValue(TomlKind.Bool, span, false);
                if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var integer))
                {
                    return new TomlValue(TomlKind.Integer, span, integer);
                }
                return new TomlValue(TomlKind.Other, span);
            }
        }
    }

    private List<TomlValue> ParseArray()
    {
        _index++; // [
        var elements = new List<TomlValue>();

        while (true)
        {
            SkipTrivia();
            if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedArray);

            if (_bytes[_index] == (byte)']')
            {
                _index++;
                return elements;
            }

            elements.Add(ParseValue());
            SkipTrivia();

            if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedArray);
            if (_bytes[_index] == (byte)',')
            {
                _index++;
            }
            else if (_bytes[_index] != (byte)']')
            {
                throw Error(TomlParseErrorKind.UnexpectedCharacter, _bytes[_index]);
            }
        }
    }

    private List<TomlPair> ParseInlineTable()
    {
        _index++; // {
        var pairs = new List<TomlPair>();

        while (true)
        {
            SkipTrivia();
            if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedInlineTable);

            if (_bytes[_index] == (byte)'}')
            {
                _index++;
                return pairs;
            }

            pairs.Add(ParsePair());
            SkipTrivia();

            if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedInlineTable);
            if (_bytes[_index] == (byte)',')
            {
                _index++;
            }
            else if (_bytes[_index] != (byte)'}')
            {
                throw Error(TomlParseErrorKind.UnexpectedCharacter, _bytes[_index]);
            }
        }
    }

    // MARK: Strings

    private string ParseAnyString()
    {
        if (HasPrefix("\"\"\"")) return ParseMultilineString((byte)'"', escaped: true);
        if (HasPrefix("'''")) return ParseMultilineString((byte)'\'', escaped: false);
        if (_bytes[_index] == (byte)'\'') return ParseLiteralString();
        return ParseBasicString();
    }

    private readonly bool HasPrefix(string text)
    {
        if (_index + text.Length > _bytes.Length) return false;
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (_bytes[_index + offset] != (byte)text[offset]) return false;
        }
        return true;
    }

    private string ParseBasicString()
    {
        _index++; // opening quote
        var result = new StringBuilder();
        var literalStart = _index;

        while (_index < _bytes.Length)
        {
            var b = _bytes[_index];
            if (b == (byte)'"')
            {
                AppendChunk(result, literalStart, _index);
                _index++;
                return result.ToString();
            }
            if (b == (byte)'\\')
            {
                AppendChunk(result, literalStart, _index);
                _index++;
                if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedString);
                AppendEscape(result);
                literalStart = _index;
                continue;
            }
            if (b == 0x0A) throw Error(TomlParseErrorKind.UnterminatedString);
            _index++;
        }
        throw Error(TomlParseErrorKind.UnterminatedString);
    }

    private string ParseLiteralString()
    {
        _index++; // opening quote
        var start = _index;
        while (_index < _bytes.Length)
        {
            if (_bytes[_index] == (byte)'\'')
            {
                var text = Encoding.UTF8.GetString(_bytes, start, _index - start);
                _index++;
                return text;
            }
            if (_bytes[_index] == 0x0A) throw Error(TomlParseErrorKind.UnterminatedString);
            _index++;
        }
        throw Error(TomlParseErrorKind.UnterminatedString);
    }

    private string ParseMultilineString(byte quote, bool escaped)
    {
        _index += 3;
        // A newline immediately after the opening delimiter is not part of the value.
        if (_index < _bytes.Length && _bytes[_index] == 0x0D) _index++;
        if (_index < _bytes.Length && _bytes[_index] == 0x0A) _index++;

        var result = new StringBuilder();
        var literalStart = _index;

        while (_index < _bytes.Length)
        {
            if (_bytes[_index] == quote && HasClosingDelimiter(quote))
            {
                AppendChunk(result, literalStart, _index);
                _index += 3;
                // TOML allows more than three closing quotes; the extras belong to
                // the value, but for our purposes stopping at three is right because
                // the span, not the decoded text, is what gets spliced.
                return result.ToString();
            }
            if (escaped && _bytes[_index] == (byte)'\\')
            {
                AppendChunk(result, literalStart, _index);
                _index++;
                if (_index >= _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedString);
                // A backslash before a newline swallows the whitespace that follows.
                if (_bytes[_index] is 0x0A or 0x0D)
                {
                    while (_index < _bytes.Length &&
                           (IsInlineSpace(_bytes[_index]) || _bytes[_index] is 0x0A or 0x0D))
                    {
                        _index++;
                    }
                }
                else
                {
                    AppendEscape(result);
                }
                literalStart = _index;
                continue;
            }
            _index++;
        }
        throw Error(TomlParseErrorKind.UnterminatedString);
    }

    private readonly bool HasClosingDelimiter(byte quote)
    {
        if (_index + 3 > _bytes.Length) return false;
        return _bytes[_index] == quote && _bytes[_index + 1] == quote && _bytes[_index + 2] == quote;
    }

    private readonly void AppendChunk(StringBuilder into, int start, int end)
    {
        if (end <= start) return;
        into.Append(Encoding.UTF8.GetString(_bytes, start, end - start));
    }

    private void AppendEscape(StringBuilder into)
    {
        var escape = _bytes[_index];
        _index++;
        switch (escape)
        {
            case (byte)'"': into.Append('"'); break;
            case (byte)'\\': into.Append('\\'); break;
            case (byte)'b': into.Append('\b'); break;
            case (byte)'f': into.Append('\f'); break;
            case (byte)'n': into.Append('\n'); break;
            case (byte)'r': into.Append('\r'); break;
            case (byte)'t': into.Append('\t'); break;
            // TOML's own extension: \e is ESC.
            case (byte)'e': into.Append((char)0x1B); break;
            case (byte)'u': AppendHex(into, 4); break;
            case (byte)'U': AppendHex(into, 8); break;
            default:
                // Not a valid escape, but this parser's job is to find spans, not
                // to referee the file. Keep the bytes and move on.
                into.Append('\\').Append((char)escape);
                break;
        }
    }

    private void AppendHex(StringBuilder into, int digits)
    {
        if (_index + digits > _bytes.Length) throw Error(TomlParseErrorKind.UnterminatedString);
        uint value = 0;
        for (var digit = 0; digit < digits; digit++)
        {
            var b = _bytes[_index];
            uint parsed;
            switch (b)
            {
                case >= (byte)'0' and <= (byte)'9': parsed = (uint)(b - (byte)'0'); break;
                case >= (byte)'a' and <= (byte)'f': parsed = (uint)(b - (byte)'a') + 10; break;
                case >= (byte)'A' and <= (byte)'F': parsed = (uint)(b - (byte)'A') + 10; break;
                default: into.Append('\uFFFD'); return;
            }
            value = value * 16 + parsed;
            _index++;
        }

        if (value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF)
        {
            into.Append('\uFFFD');
            return;
        }
        into.Append(char.ConvertFromUtf32((int)value));
    }

    // MARK: Diagnostics

    private readonly TomlParseException Error(TomlParseErrorKind kind, byte character = 0)
    {
        var line = 1;
        var column = 1;
        for (var position = 0; position < _index && position < _bytes.Length; position++)
        {
            if (_bytes[position] == 0x0A)
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return new TomlParseException(kind, _index, line, column, character);
    }
}
