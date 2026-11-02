using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Toml;

namespace Kytto.Core;

public sealed record ToolSwitchResult(
    ClientId ClientID,
    string ServerName,
    /// <summary>The tools now off in this client, among those the caller named.</summary>
    IReadOnlyList<string> DisabledTools,
    bool RequiresRestart,
    string? BackupID,
    string PathDisplay);

public sealed class ToolSwitchException(string message) : Exception(message)
{
    public static ToolSwitchException NotSupported(string client) =>
        new($"{client} has no setting for single tools. Use Gateway mode to choose which tools it sees.");

    public static ToolSwitchException NotInClient(string server, string client) =>
        new($"“{server}” is not in {client}'s configuration, so there is nothing to switch tools on.");

    public static ToolSwitchException Bundled(string name) =>
        new($"“{name}” comes from an installed plugin or extension. Choose its tools in the client that installed it.");

    /// <summary>
    /// A rule the user wrote for more than this tool keeps it off; changing that rule
    /// would change what it says about other tools too.
    /// </summary>
    public static ToolSwitchException BlockedByRule(string tool, string rule) =>
        new($"“{tool}” is kept off by the rule {rule}, which covers other tools too. " +
            "Change that rule in the file to switch this tool back on.");

    public static ToolSwitchException InlineDefinition(string name) =>
        new($"“{name}” is written as an inline table in config.toml. " +
            $"Move it into its own [mcp_servers.{name}] table to switch its tools.");

    public static ToolSwitchException NoKnownTools(string name) =>
        new($"Kytto does not know “{name}”'s tools yet. Run a health check first.");

    public string? Tool { get; init; }
    public string? Rule { get; init; }
}

/// <summary>
/// Switches single tools of a server off and on in one client (§7.11).
/// </summary>
/// <remarks>
/// One call states the whole set: the tools named in <c>among</c> end up off exactly
/// when they are in <c>disabled</c>, and nothing else in the file moves. That makes a
/// save from the tool picker one guarded write with one backup, and Undo the same call
/// with the previous set.
/// </remarks>
public sealed class ToolSwitchService(
    string home,
    ConfigWriter writer,
    DigestLedger ledger,
    IReadOnlyList<ClientDescriptor>? descriptors = null,
    IReadOnlyDictionary<string, string>? pathOverrides = null)
{
    private readonly ClientPathResolver _resolver = new(home, pathOverrides);
    private readonly IReadOnlyList<ClientDescriptor> _descriptors = descriptors ?? ClientRegistry.All;

    public ToolSwitchResult SetDisabledTools(
        IReadOnlySet<string> disabled,
        IReadOnlyList<string> among,
        Server server,
        ClientId clientId)
    {
        var descriptor = _descriptors.FirstOrDefault(candidate => candidate.Id == clientId);
        if (descriptor?.EditableServerMap is not { } source || descriptor.ToolSwitch is not { } toolSwitch)
        {
            throw ToolSwitchException.NotSupported(descriptor?.DisplayName ?? clientId.Raw());
        }
        if (server.DefinitionsByClient.GetValueOrDefault(clientId)?.IsBundled == true)
        {
            throw ToolSwitchException.Bundled(server.Name);
        }
        if (server.EnabledIn.GetValueOrDefault(clientId, Enablement.Absent) == Enablement.Absent)
        {
            throw ToolSwitchException.NotInClient(server.Name, descriptor.DisplayName);
        }
        var tools = among.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (tools.Length == 0) throw ToolSwitchException.NoKnownTools(server.Name);
        var off = disabled.Where(tools.Contains).ToHashSet(StringComparer.Ordinal);

        var configPath = _resolver.ResolveServerMap(source.File, clientId);
        var configDisplay = _resolver.DisplayServerMap(source.File, clientId);
        WriteReceipt receipt;
        string path;

        switch (toolSwitch)
        {
            case ToolSwitch.EntryLists(var deny, var allow):
                path = configPath;
                receipt = source.Format == ConfigFormat.Toml
                    ? writer.Edit<TomlDocument>(
                        path, clientId, configDisplay, ledger.DigestFor(path),
                        document =>
                        {
                            var name = document.ServerName(server.Id, source.ServersKey)
                                ?? throw ToolSwitchException.NotInClient(server.Name, descriptor.DisplayName);
                            string[] entryPath = [source.ServersKey, name];
                            var table = document.Table(entryPath)
                                ?? throw ToolSwitchException.InlineDefinition(name);
                            var current = ToolSwitches.EntryFilter(deny, allow, table);
                            var plan = ListPlan(current, off, tools);
                            var text = document.SourceText;
                            foreach (var (key, list, wasPresent) in new[]
                                     {
                                         (deny, plan.Deny, table.Value(deny) is not null),
                                         (allow, plan.Allow, table.Value(allow) is not null),
                                     })
                            {
                                if (list is null) continue;
                                if (list.Count == 0 && key == deny)
                                {
                                    if (wasPresent) text = TomlDocument.Parse(text).RemovingValue(key, entryPath);
                                }
                                else
                                {
                                    text = TomlDocument.Parse(text).SettingValue(key, entryPath, TomlText.Array(list));
                                }
                            }
                            return text;
                        })
                    : writer.Edit<JsonDocument>(
                        path, clientId, configDisplay, ledger.DigestFor(path),
                        document =>
                        {
                            var name = document.ServerKey(server.Id, source.ServersKey)
                                ?? throw ToolSwitchException.NotInClient(server.Name, descriptor.DisplayName);
                            string[] entryPath = [source.ServersKey, name];
                            var entry = document.ValueAt(entryPath);
                            if (entry?.Members is not { } members)
                            {
                                throw new JsonEditException(JsonEditErrorKind.NotAnObject, entryPath);
                            }
                            foreach (var key in new[] { deny, allow }) JsonEditor.SoleMember(key, members, entryPath);
                            var current = ToolSwitches.EntryFilter(deny, allow, entry);
                            var plan = ListPlan(current, off, tools);
                            var text = document.SourceText;
                            foreach (var (key, list) in new[] { (deny, plan.Deny), (allow, plan.Allow) })
                            {
                                if (list is null) continue;
                                text = list.Count == 0 && key == deny
                                    ? JsonDocument.Parse(text).RemovingMember(key, entryPath)
                                    : JsonDocument.Parse(text).SettingMember(key, entryPath, InlineArray(list));
                            }
                            return text;
                        });
                break;

            case ToolSwitch.PrefixedToolMap(var mapKey):
                path = configPath;
                receipt = writer.Edit<JsonDocument>(
                    path, clientId, configDisplay, ledger.DigestFor(path),
                    document =>
                    {
                        var name = document.ServerKey(server.Id, source.ServersKey)
                            ?? throw ToolSwitchException.NotInClient(server.Name, descriptor.DisplayName);
                        if (document.Root[mapKey]?.Members is { } members)
                        {
                            // Two spellings of one key would make "which one wins" a guess.
                            foreach (var key in members.Select(member => member.Key).Distinct(StringComparer.Ordinal))
                            {
                                JsonEditor.SoleMember(key, members, [mapKey]);
                            }
                        }
                        var filter = ToolSwitches.MapFilter(document.Root[mapKey], name);
                        var text = document.SourceText;
                        foreach (var tool in tools)
                        {
                            var wantOff = off.Contains(tool);
                            if (wantOff == filter.IsOff(tool)) continue;
                            var key = filter.RuleName(tool);
                            if (wantOff)
                            {
                                text = JsonDocument.Parse(text).SettingMember(key, [mapKey], JsonText.Bool(false));
                            }
                            else if (filter.BlockingPattern(tool) is not null)
                            {
                                // A broader `false` would still reach the tool once its
                                // own key is gone; the tool's own `true` answers it.
                                text = JsonDocument.Parse(text).SettingMember(key, [mapKey], JsonText.Bool(true));
                            }
                            else
                            {
                                text = JsonDocument.Parse(text).RemovingMember(key, [mapKey]);
                            }
                        }
                        return text;
                    });
                break;

            case ToolSwitch.PermissionRules(var file, var rulesPath):
                // The rule names the server as the config file spells it.
                string? ruleServer = null;
                try
                {
                    ruleServer = JsonDocument.ParseFile(configPath).ServerKey(server.Id, source.ServersKey);
                }
                catch (Exception error) when (error is JsonParseException or IOException or
                                                  UnauthorizedAccessException or JsonEditException)
                {
                    // Treated as not being there: nothing can be named in a rule.
                }
                if (ruleServer is null) throw ToolSwitchException.NotInClient(server.Name, descriptor.DisplayName);
                path = _resolver.Resolve(file);
                receipt = writer.Edit<JsonDocument>(
                    path, clientId, file.DisplayString(), ledger.DigestFor(path),
                    document =>
                    {
                        var rules = document.Root.ValueAt([.. rulesPath])?.Elements?
                            .Select(element => element.StringValue)
                            .OfType<string>()
                            .ToArray() ?? [];
                        var filter = ToolSwitches.PermissionFilter(rules, ruleServer);
                        var text = document.SourceText;
                        foreach (var tool in tools)
                        {
                            var wantOff = off.Contains(tool);
                            if (wantOff == filter.IsOff(tool)) continue;
                            var rule = filter.RuleName(tool);
                            if (wantOff)
                            {
                                text = JsonDocument.Parse(text).AppendingElement(JsonText.String(rule), rulesPath);
                            }
                            else
                            {
                                if (filter.BlockingPattern(tool) is { } blocking)
                                {
                                    throw ToolSwitchException.BlockedByRule(tool, blocking);
                                }
                                text = JsonDocument.Parse(text).RemovingElements(
                                    rulesPath, element => element.StringValue == rule);
                            }
                        }
                        return text;
                    });
                break;

            default:
                throw ToolSwitchException.NotSupported(descriptor.DisplayName);
        }
        ledger.Record(path, receipt.Digest);

        return new ToolSwitchResult(
            ClientID: clientId,
            ServerName: server.Name,
            DisabledTools: off.Order(StringComparer.Ordinal).ToArray(),
            RequiresRestart: receipt.DidWrite,
            BackupID: receipt.BackupID,
            PathDisplay: receipt.PathDisplay);
    }

    /// <summary>The new deny and allow lists, null where a list stays as it is.</summary>
    /// <remarks>
    /// With an allow list the allow list decides: a tool switched on joins it and one
    /// switched off leaves it, and a tool switched on also leaves the deny list, which
    /// applies after it. Without one only the deny list moves. Names the caller did not
    /// mention keep their place.
    /// </remarks>
    internal static (IReadOnlyList<string>? Deny, IReadOnlyList<string>? Allow) ListPlan(
        ToolFilter filter,
        IReadOnlySet<string> disabled,
        IReadOnlyList<string> tools)
    {
        var deny = filter.Rules.Select(rule => rule.Pattern).ToArray();
        var known = tools.ToHashSet(StringComparer.Ordinal);
        var newDeny = deny.Where(name => !known.Contains(name) || disabled.Contains(name)).ToList();
        List<string>? newAllow = null;
        if (filter.AllowList is { } allow)
        {
            var kept = allow.Where(name => !known.Contains(name) || !disabled.Contains(name)).ToList();
            kept.AddRange(tools.Where(name => !disabled.Contains(name) && !kept.Contains(name)));
            newAllow = kept;
        }
        else
        {
            newDeny.AddRange(tools.Where(name => disabled.Contains(name) && !newDeny.Contains(name)));
        }
        return (
            newDeny.SequenceEqual(deny, StringComparer.Ordinal) ? null : newDeny,
            newAllow is not null && filter.AllowList is { } before
                && newAllow.SequenceEqual(before, StringComparer.Ordinal) ? null : newAllow);
    }

    private static string InlineArray(IReadOnlyList<string> names) =>
        "[" + string.Join(", ", names.Select(JsonText.String)) + "]";
}
