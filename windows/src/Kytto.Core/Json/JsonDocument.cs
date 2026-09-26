using System.Text;

namespace Kytto.Core.Json;

/// <summary>
/// A JSON(C) document that keeps its original text and knows where every value
/// lives in it.
/// </summary>
/// <remarks>
/// Kytto never regenerates a client's config from its own model — §6.3. It
/// parses, finds the byte range of the one thing it needs to change, and splices.
/// Everything else in the file survives byte for byte: key order, indentation,
/// blank lines, comments, and keys Kytto has never heard of.
/// </remarks>
public sealed class JsonDocument : IConfigDocument<JsonDocument>
{
    /// <summary>
    /// An object, because every JSON client's config is one and a bare <c>{}</c> is
    /// a valid starting point for splicing a servers key into.
    /// </summary>
    public static string EmptySource => "{}\n";

    public static string FormatName => "JSON";

    private JsonDocument(string sourceText, byte[] sourceBytes, JsonNode root)
    {
        SourceText = sourceText;
        SourceBytes = sourceBytes;
        Root = root;
    }

    /// <summary>The source exactly as it was read.</summary>
    public string SourceText { get; }

    /// <summary>UTF-8 view of <see cref="SourceText"/>. Spans index into this.</summary>
    public byte[] SourceBytes { get; }

    public JsonNode Root { get; }

    public static JsonDocument Parse(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var parser = new JsonParser(bytes);
        return new JsonDocument(text, bytes, parser.ParseDocument());
    }

    public static JsonDocument ParseFile(string path)
    {
        var data = File.ReadAllBytes(path);
        // Strict, so a file that is not UTF-8 is reported rather than silently
        // mangled into replacement characters and then written back that way.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        string text;
        try
        {
            text = strict.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new JsonParseException(JsonParseErrorKind.InvalidUtf8, 0, 1, 1);
        }
        return Parse(text);
    }

    // MARK: - Slicing

    /// <summary>The raw source text covered by a span.</summary>
    public string Slice(JsonSpan span)
    {
        if (span.Start < 0 || span.End > SourceBytes.Length || span.Start > span.End) return "";
        return Encoding.UTF8.GetString(SourceBytes, span.Start, span.Length);
    }

    public JsonNode? ValueAt(params string[] path) => Root.ValueAt(path);

    // MARK: - Splicing (the write primitive)

    /// <summary>
    /// Returns the source with <paramref name="span"/> replaced and every other
    /// byte left alone.
    /// </summary>
    /// <remarks>
    /// Deliberately the <em>only</em> way new config text is produced. If a change
    /// cannot be expressed as one or more span replacements, it does not get
    /// written (§6.3).
    /// </remarks>
    public string Replacing(JsonSpan span, string replacement) =>
        Replacing([(span, replacement)]);

    /// <summary>
    /// Applies several replacements at once. Spans must not overlap; they are
    /// applied back to front so earlier offsets stay valid.
    /// </summary>
    public string Replacing(IEnumerable<(JsonSpan Span, string Replacement)> edits)
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

// MARK: - Errors

public enum JsonParseErrorKind
{
    InvalidUtf8,
    UnexpectedEndOfInput,
    UnexpectedCharacter,
    TrailingContent,
    InvalidNumber,
    InvalidEscape,
    UnterminatedString,
    UnterminatedComment,
    ExpectedColon,
    ExpectedKey,
}

public sealed class JsonParseException(
    JsonParseErrorKind kind,
    int offset,
    int line,
    int column,
    byte character = 0)
    : Exception(Describe(kind, line, column, character))
{
    public JsonParseErrorKind Kind { get; } = kind;
    public int Offset { get; } = offset;
    public int Line { get; } = line;
    public int Column { get; } = column;

    private static string Describe(JsonParseErrorKind kind, int line, int column, byte character)
    {
        var what = kind switch
        {
            JsonParseErrorKind.InvalidUtf8 => "file is not valid UTF-8",
            JsonParseErrorKind.UnexpectedEndOfInput => "unexpected end of input",
            JsonParseErrorKind.UnexpectedCharacter => $"unexpected character '{(char)character}'",
            JsonParseErrorKind.TrailingContent => "unexpected content after the top-level value",
            JsonParseErrorKind.InvalidNumber => "invalid number",
            JsonParseErrorKind.InvalidEscape => "invalid escape sequence",
            JsonParseErrorKind.UnterminatedString => "unterminated string",
            JsonParseErrorKind.UnterminatedComment => "unterminated block comment",
            JsonParseErrorKind.ExpectedColon => "expected ':'",
            JsonParseErrorKind.ExpectedKey => "expected a quoted key",
            _ => "malformed JSON",
        };
        return $"{what} at line {line}, column {column}";
    }
}

// MARK: - Parser

/// <summary>
/// Recursive-descent parser over UTF-8 bytes.
/// </summary>
/// <remarks>
/// Accepts JSONC, because it has to: VS Code's <c>mcp.json</c> is JSONC and users
/// put comments in it. Line and block comments and trailing commas are skipped as
/// trivia, which means they are never part of a value's span and therefore
/// survive any splice.
/// </remarks>
internal ref struct JsonParser(byte[] bytes)
{
    private readonly byte[] _bytes = bytes;
    private int _index = 0;

    internal JsonNode ParseDocument()
    {
        SkipByteOrderMark();
        SkipTrivia();
        var node = ParseValue();
        SkipTrivia();
        if (_index != _bytes.Length) throw Error(JsonParseErrorKind.TrailingContent);
        return node;
    }

    // MARK: Trivia

    private void SkipByteOrderMark()
    {
        if (_bytes.Length >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF)
        {
            _index = 3;
        }
    }

    private void SkipTrivia()
    {
        while (_index < _bytes.Length)
        {
            switch (_bytes[_index])
            {
                case 0x20 or 0x09 or 0x0A or 0x0D:
                    _index++;
                    break;

                case (byte)'/':
                    if (_index + 1 >= _bytes.Length) return;
                    if (_bytes[_index + 1] == (byte)'/')
                    {
                        _index += 2;
                        while (_index < _bytes.Length && _bytes[_index] != 0x0A) _index++;
                    }
                    else if (_bytes[_index + 1] == (byte)'*')
                    {
                        var start = _index;
                        _index += 2;
                        var closed = false;
                        while (_index + 1 < _bytes.Length)
                        {
                            if (_bytes[_index] == (byte)'*' && _bytes[_index + 1] == (byte)'/')
                            {
                                _index += 2;
                                closed = true;
                                break;
                            }
                            _index++;
                        }
                        if (!closed)
                        {
                            _index = start;
                            throw Error(JsonParseErrorKind.UnterminatedComment);
                        }
                    }
                    else
                    {
                        return;
                    }
                    break;

                default:
                    return;
            }
        }
    }

    // MARK: Values

    private JsonNode ParseValue()
    {
        if (_index >= _bytes.Length) throw Error(JsonParseErrorKind.UnexpectedEndOfInput);
        switch (_bytes[_index])
        {
            case (byte)'{': return ParseObject();
            case (byte)'[': return ParseArray();
            case (byte)'"':
            {
                var start = _index;
                var value = ParseStringLiteral();
                return JsonNode.String(value, new JsonSpan(start, _index));
            }
            case (byte)'t': return ParseKeyword("true", static span => JsonNode.Bool(true, span));
            case (byte)'f': return ParseKeyword("false", static span => JsonNode.Bool(false, span));
            case (byte)'n': return ParseKeyword("null", JsonNode.Null);
            default: return ParseNumber();
        }
    }

    private JsonNode ParseKeyword(string word, Func<JsonSpan, JsonNode> make)
    {
        var start = _index;
        if (_index + word.Length > _bytes.Length)
        {
            throw Error(JsonParseErrorKind.UnexpectedEndOfInput);
        }
        for (var offset = 0; offset < word.Length; offset++)
        {
            if (_bytes[_index + offset] != (byte)word[offset])
            {
                throw Error(JsonParseErrorKind.UnexpectedCharacter, _bytes[_index]);
            }
        }
        _index += word.Length;
        return make(new JsonSpan(start, _index));
    }

    private JsonNode ParseObject()
    {
        var start = _index;
        _index++; // {
        var members = new List<JsonMember>();

        SkipTrivia();
        if (_index < _bytes.Length && _bytes[_index] == (byte)'}')
        {
            _index++;
            return JsonNode.Object(members, new JsonSpan(start, _index));
        }

        while (true)
        {
            SkipTrivia();

            // Trailing comma before the closing brace, e.g. `{"a": 1,}`.
            if (_index < _bytes.Length && _bytes[_index] == (byte)'}')
            {
                _index++;
                break;
            }

            if (_index >= _bytes.Length || _bytes[_index] != (byte)'"')
            {
                throw Error(JsonParseErrorKind.ExpectedKey);
            }

            var memberStart = _index;
            var keyStart = _index;
            var key = ParseStringLiteral();
            var keySpan = new JsonSpan(keyStart, _index);

            SkipTrivia();
            if (_index >= _bytes.Length || _bytes[_index] != (byte)':')
            {
                throw Error(JsonParseErrorKind.ExpectedColon);
            }
            _index++;

            SkipTrivia();
            var value = ParseValue();
            members.Add(new JsonMember(key, keySpan, value, new JsonSpan(memberStart, value.Span.End)));

            SkipTrivia();
            if (_index >= _bytes.Length) throw Error(JsonParseErrorKind.UnexpectedEndOfInput);
            switch (_bytes[_index])
            {
                case (byte)',':
                    _index++;
                    break;
                case (byte)'}':
                    _index++;
                    return JsonNode.Object(members, new JsonSpan(start, _index));
                default:
                    throw Error(JsonParseErrorKind.UnexpectedCharacter, _bytes[_index]);
            }
        }

        return JsonNode.Object(members, new JsonSpan(start, _index));
    }

    private JsonNode ParseArray()
    {
        var start = _index;
        _index++; // [
        var elements = new List<JsonNode>();

        SkipTrivia();
        if (_index < _bytes.Length && _bytes[_index] == (byte)']')
        {
            _index++;
            return JsonNode.Array(elements, new JsonSpan(start, _index));
        }

        while (true)
        {
            SkipTrivia();

            if (_index < _bytes.Length && _bytes[_index] == (byte)']') // trailing comma
            {
                _index++;
                break;
            }

            elements.Add(ParseValue());

            SkipTrivia();
            if (_index >= _bytes.Length) throw Error(JsonParseErrorKind.UnexpectedEndOfInput);
            switch (_bytes[_index])
            {
                case (byte)',':
                    _index++;
                    break;
                case (byte)']':
                    _index++;
                    return JsonNode.Array(elements, new JsonSpan(start, _index));
                default:
                    throw Error(JsonParseErrorKind.UnexpectedCharacter, _bytes[_index]);
            }
        }

        return JsonNode.Array(elements, new JsonSpan(start, _index));
    }

    private JsonNode ParseNumber()
    {
        var start = _index;
        if (_index < _bytes.Length && _bytes[_index] == (byte)'-') _index++;

        if (_index >= _bytes.Length)
        {
            _index = start;
            throw Error(JsonParseErrorKind.UnexpectedEndOfInput);
        }

        // JSON permits exactly `0` or a non-zero digit followed by digits. Treating
        // `01` as one number would let an invalid client config through the safety
        // parse and later write it back as though it were valid JSON.
        if (_bytes[_index] == (byte)'0')
        {
            _index++;
            if (_index < _bytes.Length && _bytes[_index] is >= (byte)'0' and <= (byte)'9')
            {
                throw Error(JsonParseErrorKind.InvalidNumber);
            }
        }
        else if (_bytes[_index] is >= (byte)'1' and <= (byte)'9')
        {
            _index++;
            ConsumeDigits();
        }
        else
        {
            _index = start;
            throw Error(JsonParseErrorKind.UnexpectedCharacter, _bytes[_index]);
        }

        if (_index < _bytes.Length && _bytes[_index] == (byte)'.')
        {
            _index++;
            if (!ConsumeDigits()) throw Error(JsonParseErrorKind.InvalidNumber);
        }

        if (_index < _bytes.Length && (_bytes[_index] == (byte)'e' || _bytes[_index] == (byte)'E'))
        {
            _index++;
            if (_index < _bytes.Length && (_bytes[_index] == (byte)'+' || _bytes[_index] == (byte)'-'))
            {
                _index++;
            }
            if (!ConsumeDigits()) throw Error(JsonParseErrorKind.InvalidNumber);
        }

        var text = Encoding.UTF8.GetString(_bytes, start, _index - start);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw Error(JsonParseErrorKind.InvalidNumber);
        }
        return JsonNode.Number(value, new JsonSpan(start, _index));
    }

    private bool ConsumeDigits()
    {
        var from = _index;
        while (_index < _bytes.Length && _bytes[_index] >= (byte)'0' && _bytes[_index] <= (byte)'9')
        {
            _index++;
        }
        return _index > from;
    }

    /// <summary>
    /// Consumes a quoted string and returns it with escapes resolved. On return,
    /// the cursor sits just past the closing quote.
    /// </summary>
    private string ParseStringLiteral()
    {
        _index++; // opening quote
        var result = new StringBuilder();
        var literalStart = _index;

        while (_index < _bytes.Length)
        {
            var b = _bytes[_index];

            if (b == (byte)'"')
            {
                FlushLiteral(result, literalStart, _index);
                _index++;
                return result.ToString();
            }

            if (b < 0x20)
            {
                // JSONC adds comments and trailing commas, not raw control bytes in
                // strings. A newline or tab has to be escaped just as in JSON.
                throw Error(JsonParseErrorKind.UnexpectedCharacter, b);
            }

            if (b != (byte)'\\')
            {
                _index++;
                continue;
            }

            FlushLiteral(result, literalStart, _index);
            _index++;
            if (_index >= _bytes.Length) throw Error(JsonParseErrorKind.UnterminatedString);
            var escape = _bytes[_index];
            _index++;
            switch (escape)
            {
                case (byte)'"': result.Append('"'); break;
                case (byte)'\\': result.Append('\\'); break;
                case (byte)'/': result.Append('/'); break;
                case (byte)'b': result.Append('\b'); break;
                case (byte)'f': result.Append('\f'); break;
                case (byte)'n': result.Append('\n'); break;
                case (byte)'r': result.Append('\r'); break;
                case (byte)'t': result.Append('\t'); break;
                case (byte)'u': AppendUnicodeEscape(result); break;
                default: throw Error(JsonParseErrorKind.InvalidEscape);
            }
            literalStart = _index;
        }

        throw Error(JsonParseErrorKind.UnterminatedString);
    }

    private void FlushLiteral(StringBuilder into, int from, int to)
    {
        if (to <= from) return;
        into.Append(Encoding.UTF8.GetString(_bytes, from, to - from));
    }

    private void AppendUnicodeEscape(StringBuilder into)
    {
        var value = ParseHexQuad();

        // A surrogate pair is two escapes, and only the pair names a character.
        if (value is >= 0xD800 and <= 0xDBFF &&
            _index + 1 < _bytes.Length &&
            _bytes[_index] == (byte)'\\' &&
            _bytes[_index + 1] == (byte)'u')
        {
            var save = _index;
            _index += 2;
            var low = ParseHexQuad();
            if (low is >= 0xDC00 and <= 0xDFFF)
            {
                into.Append((char)value).Append((char)low);
                return;
            }
            _index = save;
            into.Append('�');
            return;
        }

        // A lone surrogate is not a character; the replacement says so rather than
        // producing a string that cannot be encoded again.
        if (value is >= 0xD800 and <= 0xDFFF)
        {
            into.Append('�');
            return;
        }

        into.Append((char)value);
    }

    private uint ParseHexQuad()
    {
        if (_index + 4 > _bytes.Length) throw Error(JsonParseErrorKind.InvalidEscape);
        uint value = 0;
        for (var digit = 0; digit < 4; digit++)
        {
            var b = _bytes[_index];
            uint parsed = b switch
            {
                >= (byte)'0' and <= (byte)'9' => (uint)(b - (byte)'0'),
                >= (byte)'a' and <= (byte)'f' => (uint)(b - (byte)'a') + 10,
                >= (byte)'A' and <= (byte)'F' => (uint)(b - (byte)'A') + 10,
                _ => throw Error(JsonParseErrorKind.InvalidEscape),
            };
            value = value * 16 + parsed;
            _index++;
        }
        return value;
    }

    // MARK: Diagnostics

    private readonly JsonParseException Error(JsonParseErrorKind kind, byte character = 0)
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
        return new JsonParseException(kind, _index, line, column, character);
    }
}
