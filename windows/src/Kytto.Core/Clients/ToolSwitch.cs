using System.Text;
using Kytto.Core.Json;
using Kytto.Core.Toml;

namespace Kytto.Core.Clients;

/// <summary>
/// Where a client lets a single tool of a server be switched off (§7.11).
/// </summary>
/// <remarks>
/// A server used to be all or nothing. Several clients can say more than that in
/// their own files, each differently — which is why this is data on the descriptor
/// beside the off switch for the whole server, and why a client without one falls
/// back to Gateway's allow list instead.
/// </remarks>
public abstract record ToolSwitch
{
    /// <summary>
    /// Arrays of tool names on the server's own entry. Codex
    /// (<c>disabled_tools</c>, <c>enabled_tools</c>) and Antigravity
    /// (<c>disabledTools</c>, <c>enabledTools</c>). The deny list applies after the
    /// allow list.
    /// </summary>
    public sealed record EntryLists(string Deny, string Allow) : ToolSwitch;

    /// <summary>
    /// A top-level map from <c>&lt;server&gt;_&lt;tool&gt;</c> globs to booleans.
    /// OpenCode's <c>tools</c>; <c>false</c> keeps a tool from every agent.
    /// </summary>
    public sealed record PrefixedToolMap(string Key) : ToolSwitch;

    /// <summary>
    /// Permission deny rules <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> in another file. A rule
    /// naming a whole tool removes it from Claude Code's context rather than only
    /// refusing the call (https://code.claude.com/docs/en/permissions).
    /// </summary>
    public sealed record PermissionRules(PlatformPath File, IReadOnlyList<string> Path) : ToolSwitch;

    /// <summary>What switching a tool off writes, in one sentence for the client screen.</summary>
    public string Summary => this switch
    {
        EntryLists lists => $"Switching a tool off adds it to {lists.Deny} on the server's own entry.",
        PrefixedToolMap map =>
            $"Switching a tool off writes <server>_<tool>: false into the top-level {map.Key} map.",
        PermissionRules =>
            "Switching a tool off adds an mcp__<server>__<tool> deny rule to permissions in " +
            "%USERPROFILE%\\.claude\\settings.json, which removes the tool from Claude Code's context.",
        _ => "",
    };
}

/// <summary>
/// One client's rules about which of a server's tools stay on, as its files state
/// them. Pure: it answers questions about tool names and never reads.
/// </summary>
public sealed class ToolFilter : IEquatable<ToolFilter>
{
    public sealed record Rule(string Pattern, bool KeepsOn);

    /// <summary>
    /// Prepended to a sanitized tool name to put it in the rules' namespace. Empty
    /// for entry lists, which name tools as the server does.
    /// </summary>
    public string Prefix { get; }

    /// <summary>In file order. Later rules win over earlier ones of the same kind.</summary>
    public IReadOnlyList<Rule> Rules { get; }

    /// <summary>
    /// Patterns that address the whole server rather than tools inside it — Claude
    /// Code's bare <c>mcp__&lt;server&gt;</c>.
    /// </summary>
    public IReadOnlyList<string> WholeServerPatterns { get; }

    /// <summary>When the client keeps an allow list, only these tools stay on.</summary>
    public IReadOnlyList<string>? AllowList { get; }

    /// <summary>
    /// Whether a tool's own rule can say "on" over a broader rule. OpenCode's map can
    /// (<c>"github_x": true</c> beside <c>"github_*": false</c>); Claude Code's deny
    /// rules cannot be answered by anything but removing them.
    /// </summary>
    public bool OwnRuleOverrides { get; }

    public ToolFilter(
        string prefix = "",
        IReadOnlyList<Rule>? rules = null,
        IReadOnlyList<string>? wholeServerPatterns = null,
        IReadOnlyList<string>? allowList = null,
        bool ownRuleOverrides = false)
    {
        Prefix = prefix;
        Rules = rules ?? [];
        WholeServerPatterns = wholeServerPatterns ?? [];
        AllowList = allowList;
        OwnRuleOverrides = ownRuleOverrides;
    }

    public bool IsEmpty => Rules.Count == 0 && WholeServerPatterns.Count == 0 && AllowList is null;

    /// <summary>The name a rule has to match to speak about <paramref name="tool"/>.</summary>
    public string RuleName(string tool) => Prefix.Length == 0 ? tool : Prefix + Sanitized(tool);

    /// <summary>Whether the client keeps <paramref name="tool"/> from its model.</summary>
    public bool IsOff(string tool)
    {
        if (AllowList is not null && !AllowList.Contains(tool, StringComparer.Ordinal)) return true;
        if (WholeServerPatterns.Count > 0) return true;
        var name = RuleName(tool);
        if (Rules.LastOrDefault(rule => rule.Pattern == name) is { } exact) return !exact.KeepsOn;
        if (Rules.LastOrDefault(rule => rule.Pattern != name && Glob(rule.Pattern, name)) is { } glob)
        {
            return !glob.KeepsOn;
        }
        return false;
    }

    /// <summary>
    /// A rule broader than this tool's own that would keep it off once its own rule is
    /// gone. Null when nothing but the tool's own rule (or none) applies.
    /// </summary>
    public string? BlockingPattern(string tool)
    {
        if (WholeServerPatterns.Count > 0) return WholeServerPatterns[0];
        var name = RuleName(tool);
        var glob = Rules.LastOrDefault(rule => rule.Pattern != name && Glob(rule.Pattern, name));
        return glob is { KeepsOn: false } ? glob.Pattern : null;
    }

    /// <summary>
    /// The broader rule keeping an off tool off that a switch for this one tool cannot
    /// answer, or null when switching it on is possible.
    /// </summary>
    public string? LockingRule(string tool)
    {
        if (!IsOff(tool)) return null;
        if (WholeServerPatterns.Count > 0) return WholeServerPatterns[0];
        return OwnRuleOverrides ? null : BlockingPattern(tool);
    }

    /// <summary>
    /// Tool → locking rule, for every tool in <paramref name="tools"/> that has one.
    /// Servers can list a name twice; the first wins rather than throwing.
    /// </summary>
    public IReadOnlyDictionary<string, string> LockedTools(IEnumerable<string> tools)
    {
        var locked = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (LockingRule(tool) is { } rule) locked.TryAdd(tool, rule);
        }
        return locked;
    }

    /// <summary>The names of <paramref name="tools"/> this client keeps from its model.</summary>
    public IReadOnlyList<string> OffTools(IEnumerable<string> tools) => tools.Where(IsOff).ToArray();

    // MARK: - Names

    /// <summary>
    /// How OpenCode and Claude Code turn a server or tool name into part of a tool
    /// identifier: anything outside ASCII letters, digits, <c>_</c> and <c>-</c>
    /// becomes <c>_</c>, one per character.
    /// </summary>
    public static string Sanitized(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            var allowed = rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value)
                                           || rune.Value == '_' || rune.Value == '-');
            result.Append(allowed ? rune.ToString() : "_");
        }
        return result.ToString();
    }

    /// <summary><c>*</c> matches any run of characters and <c>?</c> exactly one; everything else matches itself.</summary>
    internal static bool Glob(string pattern, string name)
    {
        var p = pattern.EnumerateRunes().Select(rune => rune.Value).ToArray();
        var n = name.EnumerateRunes().Select(rune => rune.Value).ToArray();
        int pi = 0, ni = 0, star = -1, mark = 0;
        while (ni < n.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == n[ni]))
            {
                pi++;
                ni++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                star = pi;
                mark = ni;
                pi++;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                mark++;
                ni = mark;
            }
            else
            {
                return false;
            }
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    public bool Equals(ToolFilter? other) =>
        other is not null
        && Prefix == other.Prefix
        && OwnRuleOverrides == other.OwnRuleOverrides
        && Rules.SequenceEqual(other.Rules)
        && WholeServerPatterns.SequenceEqual(other.WholeServerPatterns)
        && (AllowList is null ? other.AllowList is null : other.AllowList is not null && AllowList.SequenceEqual(other.AllowList));

    public override bool Equals(object? obj) => Equals(obj as ToolFilter);

    public override int GetHashCode() => HashCode.Combine(Prefix, Rules.Count, WholeServerPatterns.Count, AllowList?.Count);
}

public static class ToolSwitches
{
    /// <summary>
    /// The server-name part of a rule, for the server spelled <paramref name="serverKey"/>
    /// in the client's own file.
    /// </summary>
    public static string PrefixFor(this ToolSwitch toolSwitch, string serverKey) => toolSwitch switch
    {
        ToolSwitch.EntryLists => "",
        ToolSwitch.PrefixedToolMap => ToolFilter.Sanitized(serverKey) + "_",
        ToolSwitch.PermissionRules => "mcp__" + ToolFilter.Sanitized(serverKey) + "__",
        _ => "",
    };

    internal static bool IsGlob(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    /// <summary>Entry lists read off a JSON entry.</summary>
    public static ToolFilter EntryFilter(string deny, string allow, JsonNode entry)
    {
        var denied = Strings(entry[deny]);
        return new ToolFilter(
            rules: denied.Select(name => new ToolFilter.Rule(name, false)).ToArray(),
            allowList: entry[allow]?.Elements is not null ? Strings(entry[allow]) : null);
    }

    /// <summary>Entry lists read off a TOML table.</summary>
    public static ToolFilter EntryFilter(string deny, string allow, TomlTable table)
    {
        var denied = TomlStrings(table.Value(deny));
        return new ToolFilter(
            rules: denied.Select(name => new ToolFilter.Rule(name, false)).ToArray(),
            allowList: table.Value(allow) is { Elements: not null } list ? TomlStrings(list) : null);
    }

    /// <summary>OpenCode's map: the rules that can match this server's tools.</summary>
    public static ToolFilter MapFilter(JsonNode? map, string serverKey)
    {
        var prefix = new ToolSwitch.PrefixedToolMap("").PrefixFor(serverKey);
        var rules = (map?.Members ?? [])
            .Where(member => member.Value.BoolValue is not null)
            // Only rules that can name one of this server's tools: its prefix, or a
            // glob, which is matched against each full name later.
            .Where(member => member.Key.StartsWith(prefix, StringComparison.Ordinal) || IsGlob(member.Key))
            .Select(member => new ToolFilter.Rule(member.Key, member.Value.BoolValue!.Value))
            .ToArray();
        return new ToolFilter(prefix: prefix, rules: rules, ownRuleOverrides: true);
    }

    /// <summary>
    /// Claude Code's deny rules that can reach this server's tools. Allow rules only
    /// skip a prompt, so they never switch a tool back on.
    /// </summary>
    public static ToolFilter PermissionFilter(IEnumerable<string> deny, string serverKey)
    {
        var prefix = new ToolSwitch.PermissionRules(new PlatformPath(Darwin: "", Windows: ""), []).PrefixFor(serverKey);
        var whole = "mcp__" + ToolFilter.Sanitized(serverKey);
        var rules = new List<ToolFilter.Rule>();
        var wholeServer = new List<string>();
        foreach (var rule in deny.Where(rule => !rule.Contains('(')))
        {
            if (rule == whole || rule == prefix + "*" || rule == "mcp__*" || rule == "*")
            {
                wholeServer.Add(rule);
            }
            else if (rule.StartsWith(prefix, StringComparison.Ordinal)
                     || (rule.StartsWith("mcp__", StringComparison.Ordinal) && IsGlob(rule)))
            {
                rules.Add(new ToolFilter.Rule(rule, false));
            }
        }
        return new ToolFilter(prefix: prefix, rules: rules, wholeServerPatterns: wholeServer);
    }

    private static string[] Strings(JsonNode? array) =>
        array?.Elements?.Select(element => element.StringValue).OfType<string>().ToArray() ?? [];

    private static string[] TomlStrings(TomlValue? array) =>
        array?.Elements?.Select(element => element.StringValue).OfType<string>().ToArray() ?? [];

    // MARK: - Renaming a server

    /// <summary>
    /// OpenCode's <c>tools</c> keys for <paramref name="oldServer"/>, respelt for
    /// <paramref name="newServer"/>.
    /// </summary>
    /// <remarks>
    /// The rules name the server, so a rename that left them behind would switch every
    /// tool the user had switched off back on. A key already under the new name gives
    /// way to the one that moves: the moving rules are what the matrix showed for this
    /// server. Keys that belong to another server whose name merely starts the same way
    /// (<c>github_enterprise_*</c> beside <c>github</c>) stay where they are.
    /// </remarks>
    public static string RenamingMapRules(
        string text,
        string mapKey,
        string serversKey,
        string oldServer,
        string newServer)
    {
        var oldPrefix = ToolFilter.Sanitized(oldServer) + "_";
        var newPrefix = ToolFilter.Sanitized(newServer) + "_";
        if (oldPrefix == newPrefix) return text;
        var document = JsonDocument.Parse(text);
        if (document.Root[mapKey]?.Members is not { } members) return text;

        var otherPrefixes = (document.Root[serversKey]?.Keys ?? [])
            .Select(key => ToolFilter.Sanitized(key) + "_")
            .Where(prefix => prefix != oldPrefix && prefix != newPrefix
                             && prefix.StartsWith(oldPrefix, StringComparison.Ordinal))
            .ToArray();
        bool Moves(string key) =>
            key.StartsWith(oldPrefix, StringComparison.Ordinal)
            && !otherPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));
        string Renamed(string key) => newPrefix + key[oldPrefix.Length..];

        var moving = members.Select(member => member.Key).Where(Moves).ToArray();
        if (moving.Length == 0) return text;
        var result = text;
        foreach (var key in moving.Select(Renamed).ToHashSet(StringComparer.Ordinal))
        {
            if (moving.Contains(key, StringComparer.Ordinal)) continue;
            if (!members.Any(member => member.Key == key)) continue;
            result = JsonDocument.Parse(result).RemovingMember(key, [mapKey]);
        }
        var current = JsonDocument.Parse(result);
        var edits = (current.Root[mapKey]?.Members ?? [])
            .Where(member => Moves(member.Key))
            .Select(member => (member.KeySpan, JsonText.String(Renamed(member.Key))))
            .ToArray();
        return current.Replacing(edits);
    }

    /// <summary>
    /// Claude Code's <c>mcp__&lt;server&gt;…</c> permission rules for
    /// <paramref name="oldServer"/>, respelt for <paramref name="newServer"/>; every
    /// other rule is left as written.
    /// </summary>
    public static string RenamingPermissionRules(
        string text,
        IReadOnlyList<string> path,
        string oldServer,
        string newServer)
    {
        var old = "mcp__" + ToolFilter.Sanitized(oldServer);
        var renamedTo = "mcp__" + ToolFilter.Sanitized(newServer);
        if (old == renamedTo) return text;
        var document = JsonDocument.Parse(text);
        var edits = new List<(JsonSpan Span, string Replacement)>();
        foreach (var element in document.Root.ValueAt([.. path])?.Elements ?? [])
        {
            if (element.StringValue is not { } rule) continue;
            var lowered = rule.ToLowerInvariant();
            var prefix = old.ToLowerInvariant();
            if (lowered != prefix && !lowered.StartsWith(prefix + "__", StringComparison.Ordinal)) continue;
            edits.Add((element.Span, JsonText.String(renamedTo + rule[old.Length..])));
        }
        return edits.Count == 0 ? text : document.Replacing(edits);
    }
}
