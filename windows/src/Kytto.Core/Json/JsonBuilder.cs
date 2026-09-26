using System.Globalization;

namespace Kytto.Core.Json;

/// <summary>A JSON value Kytto is about to write.</summary>
/// <remarks>
/// The serializer that toggling deliberately does without: switching a server on or off
/// only ever moves bytes that were already in the file. Authoring creates
/// definitions that never existed, so they have to be rendered — and rendered to
/// look like the rest of the file they land in.
/// </remarks>
public abstract record JsonBuildValue
{
    public sealed record Field(string Key, JsonBuildValue Value);

    public sealed record ObjectValue(IReadOnlyList<Field> Fields) : JsonBuildValue;
    public sealed record ArrayValue(IReadOnlyList<JsonBuildValue> Elements) : JsonBuildValue;
    public sealed record StringValue(string Value) : JsonBuildValue;
    public sealed record BoolValue(bool Value) : JsonBuildValue;
    public sealed record NumberValue(double Value) : JsonBuildValue;

    public static JsonBuildValue Object(params Field[] fields) => new ObjectValue(fields);
    public static JsonBuildValue Object(IReadOnlyList<Field> fields) => new ObjectValue(fields);
    public static JsonBuildValue Array(IReadOnlyList<JsonBuildValue> elements) => new ArrayValue(elements);
    public static JsonBuildValue Strings(IReadOnlyList<string> values) =>
        new ArrayValue(values.Select(value => (JsonBuildValue)new StringValue(value)).ToArray());
    public static JsonBuildValue Text(string value) => new StringValue(value);
    public static JsonBuildValue Flag(bool value) => new BoolValue(value);
}

public static class JsonBuilder
{
    /// <summary>Width past which a scalar array is broken onto separate lines.</summary>
    /// <remarks>
    /// Real configs write short argument lists inline — <c>["-y", "some-package"]</c>
    /// — and break long ones up. Matching that is the difference between a
    /// definition that looks typed and one that looks generated.
    /// </remarks>
    private const int InlineArrayBudget = 60;

    public static string Render(
        JsonBuildValue value,
        string baseIndent = "",
        string unit = "  ",
        string newline = "\n") =>
        value switch
        {
            JsonBuildValue.StringValue text => JsonText.String(text.Value),
            JsonBuildValue.BoolValue flag => JsonText.Bool(flag.Value),
            JsonBuildValue.NumberValue number => RenderNumber(number.Value),
            JsonBuildValue.ArrayValue array => RenderArray(array.Elements, baseIndent, unit, newline),
            JsonBuildValue.ObjectValue obj => RenderObject(obj.Fields, baseIndent, unit, newline),
            _ => "null",
        };

    /// <summary>Whole numbers should not come out as "3.0".</summary>
    private static string RenderNumber(double number) =>
        number == Math.Round(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);

    private static string RenderArray(
        IReadOnlyList<JsonBuildValue> elements,
        string baseIndent,
        string unit,
        string newline)
    {
        if (elements.Count == 0) return "[]";

        if (elements.All(IsScalar))
        {
            var inline = "[" + string.Join(", ", elements.Select(element => Render(element))) + "]";
            if (baseIndent.Length + inline.Length <= InlineArrayBudget) return inline;
        }

        var inner = baseIndent + unit;
        var body = string.Join("," + newline,
            elements.Select(element => inner + Render(element, inner, unit, newline)));
        return $"[{newline}{body}{newline}{baseIndent}]";
    }

    private static string RenderObject(
        IReadOnlyList<JsonBuildValue.Field> fields,
        string baseIndent,
        string unit,
        string newline)
    {
        if (fields.Count == 0) return "{}";
        var inner = baseIndent + unit;
        var body = string.Join("," + newline, fields.Select(field =>
            $"{inner}{JsonText.String(field.Key)}: {Render(field.Value, inner, unit, newline)}"));
        return $"{{{newline}{body}{newline}{baseIndent}}}";
    }

    private static bool IsScalar(JsonBuildValue value) =>
        value is JsonBuildValue.StringValue or JsonBuildValue.BoolValue or JsonBuildValue.NumberValue;
}
