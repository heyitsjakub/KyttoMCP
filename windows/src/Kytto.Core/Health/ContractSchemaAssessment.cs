using System.Text;
using System.Text.Json;

namespace Kytto.Core.Health;

/// <summary>Whether calls written for the old contract still fit the new one.</summary>
/// <remarks>
/// This is one question, and security is another (roadmap §18): a new optional
/// argument is compatible, yet it can ask the model for local data. Keeping the two
/// apart stops "Breaking" from standing in for "look at this".
/// </remarks>
public enum ContractCompatibility
{
    /// <summary>Existing calls keep working: an added tool, a new optional argument, changed wording.</summary>
    Compatible,

    /// <summary>
    /// Existing calls can fail: a removed tool or argument, a new required argument, a
    /// narrowed type or value list.
    /// </summary>
    Breaking,

    /// <summary>Kytto cannot tell from the schema alone, for example a changed pattern, <c>$ref</c> or <c>oneOf</c>.</summary>
    Unknown,
}

/// <summary>
/// Whether the change touches what the model is told or allowed to do, so that a
/// person should read it. A reason to look, never a verdict that the server is
/// malicious.
/// </summary>
public enum ContractSecurityRelevance { None, Review }

/// <summary>
/// What an input schema change means for existing calls, and whether it gives the
/// model something new to be asked or to ask for (roadmap §18).
/// </summary>
internal sealed record SchemaAssessment(
    ContractCompatibility Compatibility,
    string CompatibilityReason,
    ContractSecurityRelevance Security,
    string? SecurityReason)
{
    public string Summary => Compatibility switch
    {
        ContractCompatibility.Breaking => "Input schema changed — existing calls may fail",
        ContractCompatibility.Compatible => "Input schema changed — existing calls still fit",
        _ => "Input schema changed — compatibility unknown",
    };
}

/// <remarks>
/// The walk understands the JSON Schema keywords that decide whether an argument is
/// accepted — <c>type</c>, <c>properties</c>, <c>required</c>, <c>enum</c>,
/// <c>items</c>, <c>additionalProperties</c> — and the model-facing text around them.
/// Anything else that changed (<c>pattern</c>, <c>$ref</c>, <c>oneOf</c>, bounds)
/// makes the result <c>unknown</c> rather than guessed: a label that is wrong in the
/// reassuring direction is worse than no label. Reasons are plain text the UI shows
/// as it is, so names are quoted with “” rather than backticks.
/// </remarks>
internal static class ContractSchemaAssessment
{
    /// <summary>
    /// Argument names that usually carry local data, credentials or a place to send
    /// something. A new one is the case the roadmap names: compatible, and still a way
    /// to ask an agent for local data.
    /// </summary>
    internal static readonly string[] SensitiveArgumentWords =
    [
        "path", "file", "dir", "folder", "token", "secret", "key", "password", "passwd",
        "credential", "cookie", "auth", "env", "command", "cmd", "shell", "script",
        "url", "uri", "endpoint", "host", "webhook", "email", "ssh",
    ];

    internal static readonly string[] GuardWords = ["dry_run", "dryrun", "confirm", "approval", "approve", "consent"];

    public static SchemaAssessment Assess(string? before, string? after)
    {
        using var left = before is null ? null : Parse(before);
        using var right = after is null ? null : Parse(after);
        if (left is null || right is null) return Unreadable;
        try
        {
            var walk = new SchemaWalk();
            walk.Compare(left.RootElement, right.RootElement, "");
            return walk.Assessment;
        }
        catch (InvalidOperationException)
        {
            // A string with an unpaired surrogate parses but cannot be decoded. That
            // is a schema Kytto cannot judge, not a reason to fail the check.
            return Unreadable;
        }
    }

    private static readonly SchemaAssessment Unreadable = new(
        ContractCompatibility.Unknown,
        "Kytto could not read one of the schemas, so it cannot tell whether existing calls still fit.",
        ContractSecurityRelevance.Review,
        "Compare the full schemas before relying on this tool.");

    /// <summary>A schema the walk can trust to mean one thing, or null.</summary>
    /// <remarks>
    /// A repeated key is read one way here and possibly another by the server, so it
    /// is not walked at all; neither is a schema too large or too deep to compare
    /// within bounds, nor one whose text cannot be decoded.
    /// </remarks>
    private static JsonDocument? Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > ContractGuard.MaxSchemaBytes) return null;
        JsonDocument? document = null;
        try
        {
            document = ContractFieldDiff.Parse(text);
            if (!RepeatsKeys(document.RootElement)) return document;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            // Not a schema that can be compared.
        }
        document?.Dispose();
        return null;
    }

    private static bool RepeatsKeys(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (element.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) return true;
                    stack.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) stack.Push(item);
            }
        }
        return false;
    }

    private sealed class SchemaWalk
    {
        private readonly List<string> _breaking = [];
        private readonly List<string> _unknown = [];
        private readonly List<string> _addedArguments = [];
        private readonly List<string> _sensitiveArguments = [];
        private readonly List<string> _guardChanges = [];

        /// <summary>
        /// A changed default keeps every call valid and still changes what a call that
        /// leaves the argument out does.
        /// </summary>
        private readonly List<string> _defaultChanges = [];

        private bool _textChanged;
        private int _visited;

        /// <summary>Keywords that only describe; changing them never rejects a call.</summary>
        private static readonly HashSet<string> TextKeys =
            new(["description", "title", "examples", "$comment", "deprecated"], StringComparer.Ordinal);

        /// <summary>Keywords handled structurally below.</summary>
        private static readonly HashSet<string> StructuralKeys =
            new(["type", "properties", "required", "enum", "items", "additionalProperties"], StringComparer.Ordinal);

        public SchemaAssessment Assessment
        {
            get
            {
                ContractCompatibility compatibility;
                string compatibilityReason;
                if (_breaking.Count > 0)
                {
                    compatibility = ContractCompatibility.Breaking;
                    compatibilityReason = Sentence(_breaking);
                }
                else if (_unknown.Count > 0 || _defaultChanges.Count > 0)
                {
                    compatibility = ContractCompatibility.Unknown;
                    var parts = new List<string>();
                    if (_defaultChanges.Count > 0)
                    {
                        parts.Add($"The default of {List(_defaultChanges)} changed: calls that leave it out now behave differently.");
                    }
                    if (_unknown.Count > 0)
                    {
                        parts.Add($"Changed in a way Kytto does not compare: {List(_unknown)}. Check whether existing calls still fit.");
                    }
                    compatibilityReason = string.Join(" ", parts);
                }
                else
                {
                    compatibility = ContractCompatibility.Compatible;
                    compatibilityReason = _addedArguments.Count == 0
                        ? "Only wording changed; the same calls are accepted."
                        : "Only optional arguments were added; existing calls still fit.";
                }

                var reasons = new List<string>();
                if (_sensitiveArguments.Count > 0)
                {
                    reasons.Add($"New argument {List(_sensitiveArguments)} may ask the model for files, credentials or a place to send data.");
                }
                else if (_addedArguments.Count > 0)
                {
                    reasons.Add($"New argument {List(_addedArguments)} gives the model something new to fill in.");
                }
                if (_guardChanges.Count > 0)
                {
                    reasons.Add($"A confirmation or dry-run field changed ({List(_guardChanges)}). That can change how the model asks for a guarded action; Kytto cannot see whether the server still enforces it.");
                }
                if (_textChanged) reasons.Add("Schema text the model reads changed.");

                return new SchemaAssessment(
                    compatibility,
                    compatibilityReason,
                    reasons.Count == 0 ? ContractSecurityRelevance.None : ContractSecurityRelevance.Review,
                    reasons.Count == 0 ? null : string.Join(" ", reasons));
            }
        }

        private static List<string> Unique(IEnumerable<string> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return items.Where(seen.Add).ToList();
        }

        private static string List(IEnumerable<string> items)
        {
            var all = Unique(items);
            var shown = string.Join(", ", all.Take(5).Select(item => $"“{item}”"));
            return all.Count > 5 ? $"{shown} and {all.Count - 5} more" : shown;
        }

        private static string Sentence(IEnumerable<string> reasons)
        {
            var all = Unique(reasons);
            return string.Join(" ", all.Take(3)) +
                   (all.Count > 3 ? $" {all.Count - 3} more changes can also break calls." : "");
        }

        private static string Display(string path) => path.Length == 0 ? "the arguments" : path;

        private static string Child(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

        private static HashSet<string>? Types(JsonElement? value) => value switch
        {
            { ValueKind: JsonValueKind.String } one => new HashSet<string>([one.GetString()!], StringComparer.Ordinal),
            { ValueKind: JsonValueKind.Array } many => many.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal),
            _ => null,
        };

        private static string Encoded(JsonElement value) => ContractGuard.CanonicalText(value);

        private static bool Same(JsonElement? left, JsonElement? right) => (left, right) switch
        {
            (null, null) => true,
            ({ } l, { } r) => Encoded(l) == Encoded(r),
            _ => false,
        };

        private static bool IsTrue(JsonElement? value) => value is { ValueKind: JsonValueKind.True };

        private static bool IsGuard(string name)
        {
            var lower = name.ToLowerInvariant();
            return GuardWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
        }

        /// <summary>
        /// Splits camelCase and snake_case so <c>filePath</c> and <c>api_key</c> match
        /// while <c>keyboard</c> and <c>monkey</c> do not.
        /// </summary>
        internal static bool IsSensitive(string name)
        {
            var words = new List<string>();
            var current = new StringBuilder();
            foreach (var character in name)
            {
                if (character is '_' or '-' or '.')
                {
                    if (current.Length > 0)
                    {
                        words.Add(current.ToString().ToLowerInvariant());
                        current.Clear();
                    }
                }
                else if (char.IsUpper(character) && current.Length > 0 && !char.IsUpper(current[^1]))
                {
                    words.Add(current.ToString().ToLowerInvariant());
                    current.Clear().Append(character);
                }
                else
                {
                    current.Append(character);
                }
            }
            if (current.Length > 0) words.Add(current.ToString().ToLowerInvariant());
            return words.Any(word => SensitiveArgumentWords.Any(sensitive => word == sensitive || word == sensitive + "s"));
        }

        private static Dictionary<string, JsonElement> Members(JsonElement element)
        {
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject()) members[property.Name] = property.Value;
            }
            return members;
        }

        private static JsonElement? Get(Dictionary<string, JsonElement> members, string key) =>
            members.TryGetValue(key, out var value) ? value : null;

        private static HashSet<string> Strings(JsonElement? value) =>
            value is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

        public void Compare(JsonElement lhs, JsonElement rhs, string path)
        {
            if (++_visited > 2_000)
            {
                _unknown.Add("more of the schema");
                return;
            }
            if (lhs.ValueKind != JsonValueKind.Object || rhs.ValueKind != JsonValueKind.Object)
            {
                // `true`/`false` schemas, or something that is not a schema.
                if (!Same(lhs, rhs)) _unknown.Add(Display(path));
                return;
            }
            var here = Display(path);
            var left = Members(lhs);
            var right = Members(rhs);

            // type: a wider set accepts everything the old one did.
            if (!Same(Get(left, "type"), Get(right, "type")))
            {
                var oldTypes = Types(Get(left, "type"));
                var newTypes = Types(Get(right, "type"));
                if (Get(right, "type") is null)
                {
                    // No type constraint any more: accepts more than before.
                }
                else if (newTypes is not null)
                {
                    if (oldTypes is not null)
                    {
                        // Every integer is a number, so integer → number widens.
                        var accepted = newTypes.Contains("number")
                            ? newTypes.Union(["integer"]).ToHashSet(StringComparer.Ordinal)
                            : newTypes;
                        if (!oldTypes.IsSubsetOf(accepted)) _breaking.Add($"The type of {here} changed.");
                    }
                    else if (Get(left, "type") is null)
                    {
                        _breaking.Add($"{here} now requires a specific type.");
                    }
                    else
                    {
                        _unknown.Add($"the type of {here}");
                    }
                }
                else
                {
                    _unknown.Add($"the type of {here}");
                }
            }

            // enum: removing an allowed value rejects calls that used it.
            if (!Same(Get(left, "enum"), Get(right, "enum")))
            {
                var oldValues = Get(left, "enum") is { ValueKind: JsonValueKind.Array } oldArray
                    ? oldArray.EnumerateArray().Select(Encoded).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var newValues = Get(right, "enum") is { ValueKind: JsonValueKind.Array } newArray
                    ? newArray.EnumerateArray().Select(Encoded).ToHashSet(StringComparer.Ordinal)
                    : null;
                if (newValues is not null && (!oldValues.IsSubsetOf(newValues) || Get(left, "enum") is null))
                {
                    _breaking.Add($"{here} accepts fewer values than before.");
                }
                // An enum removed altogether accepts more; compatible.
            }

            // required
            var oldRequired = Strings(Get(left, "required"));
            var newRequired = Strings(Get(right, "required"));
            foreach (var name in newRequired.Except(oldRequired).Order(StringComparer.Ordinal))
            {
                _breaking.Add($"“{name}” is now required.");
                if (IsGuard(name)) _guardChanges.Add(name);
            }
            foreach (var name in oldRequired.Except(newRequired).Order(StringComparer.Ordinal).Where(IsGuard))
            {
                _guardChanges.Add(name);
            }

            // properties
            var oldProperties = Get(left, "properties") is { ValueKind: JsonValueKind.Object } oldObject
                ? Members(oldObject)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var newProperties = Get(right, "properties") is { ValueKind: JsonValueKind.Object } newObject
                ? Members(newObject)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var name in oldProperties.Keys.Except(newProperties.Keys).Order(StringComparer.Ordinal))
            {
                _breaking.Add($"Argument “{name}” was removed.");
                if (IsGuard(name)) _guardChanges.Add(name);
            }
            foreach (var name in newProperties.Keys.Except(oldProperties.Keys).Order(StringComparer.Ordinal))
            {
                var qualified = Child(path, name);
                _addedArguments.Add(qualified);
                if (IsSensitive(name)) _sensitiveArguments.Add(qualified);
                if (IsGuard(name)) _guardChanges.Add(qualified);
            }
            foreach (var name in oldProperties.Keys.Intersect(newProperties.Keys).Order(StringComparer.Ordinal))
            {
                var oldValue = oldProperties[name];
                var newValue = newProperties[name];
                if (Same(oldValue, newValue)) continue;
                if (IsGuard(name)) _guardChanges.Add(Child(path, name));
                Compare(oldValue, newValue, Child(path, name));
            }

            // items
            if (!Same(Get(left, "items"), Get(right, "items")))
            {
                if (Get(left, "items") is { } oldItems && Get(right, "items") is { } newItems)
                {
                    Compare(oldItems, newItems, path.Length == 0 ? "[]" : $"{path}[]");
                }
                else if (Get(left, "items") is null)
                {
                    _breaking.Add($"The items of {here} are now constrained.");
                }
            }

            // additionalProperties: closing an open object rejects extra arguments.
            if (!Same(Get(left, "additionalProperties"), Get(right, "additionalProperties")))
            {
                var wasOpen = Get(left, "additionalProperties") is null || IsTrue(Get(left, "additionalProperties"));
                var nowClosed = Get(right, "additionalProperties") is { ValueKind: JsonValueKind.False };
                if (wasOpen && nowClosed)
                {
                    _breaking.Add($"{here} no longer accepts extra arguments.");
                }
                else if (!(Get(right, "additionalProperties") is null || IsTrue(Get(right, "additionalProperties"))))
                {
                    _unknown.Add($"additionalProperties of {here}");
                }
            }

            foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
            {
                if (StructuralKeys.Contains(key) || Same(Get(left, key), Get(right, key))) continue;
                if (TextKeys.Contains(key))
                {
                    _textChanged = true;
                }
                else if (key == "default")
                {
                    _defaultChanges.Add(Display(path));
                }
                else
                {
                    _unknown.Add(path.Length == 0 ? key : $"{path}.{key}");
                }
            }
        }
    }
}
