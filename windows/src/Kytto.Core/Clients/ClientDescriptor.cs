namespace Kytto.Core.Clients;

public enum ClientId
{
    ClaudeDesktop,
    ClaudeCode,
    Cursor,
    VsCode,
    Codex,
}

/// <summary>
/// The identity of a column in discovery. Built-in ids remain the closed
/// <see cref="ClientId"/> enum used by every mutation service; this value also
/// carries explicitly attached, read-only custom-source ids.
/// </summary>
/// <remarks>
/// Keeping the dynamic id out of <see cref="ClientId"/> is a native safety
/// boundary: a <c>custom.&lt;uuid&gt;</c> value cannot be passed to toggle, authoring,
/// profile, backup, secret-rotation or gateway APIs by mistake.
/// </remarks>
public readonly record struct ClientKey
{
    public ClientKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A client id cannot be blank.", nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    public bool IsCustom => TryCustomSourceId(out _);

    public ClientId? BuiltIn => ClientIds.FromRaw(Value);

    public string Raw() => Value;

    public bool TryCustomSourceId(out Guid id)
    {
        const string prefix = "custom.";
        if (Value.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(Value[prefix.Length..], "D", out id)
            && Value == Custom(id).Value)
        {
            return true;
        }
        id = default;
        return false;
    }

    public static ClientKey Custom(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A custom source id cannot be empty.", nameof(id));
        return new ClientKey($"custom.{id:D}");
    }

    public static implicit operator ClientKey(ClientId id) => new(id.Raw());

    public override string ToString() => Value;
}

public static class ClientIds
{
    /// <summary>
    /// The wire spelling, which is the id the web layer sends back. Fixed strings
    /// rather than the enum's name so a C# rename cannot silently change the
    /// protocol.
    /// </summary>
    public static string Raw(this ClientId id) => id switch
    {
        ClientId.ClaudeDesktop => "claudeDesktop",
        ClientId.ClaudeCode => "claudeCode",
        ClientId.Cursor => "cursor",
        ClientId.VsCode => "vsCode",
        ClientId.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static ClientId? FromRaw(string? raw) => raw switch
    {
        "claudeDesktop" => ClientId.ClaudeDesktop,
        "claudeCode" => ClientId.ClaudeCode,
        "cursor" => ClientId.Cursor,
        "vsCode" => ClientId.VsCode,
        "codex" => ClientId.Codex,
        _ => null,
    };

    public static IReadOnlyList<ClientId> All { get; } = Enum.GetValues<ClientId>();
}

/// <summary>How a config file is spelled.</summary>
/// <remarks>
/// JSON was the only answer until Codex, and the assumption had leaked into the
/// write pipeline rather than being written down. It is data on the source now,
/// because the next client to arrive is as likely to bring a third spelling as to
/// reuse one of these two.
/// </remarks>
public enum ConfigFormat
{
    /// <summary>JSON, tolerating JSONC where the client emits it.</summary>
    Json,
    Toml,
}

public static class ConfigFormats
{
    /// <summary>How the format is spelled on screen (§7.8).</summary>
    public static string DisplayName(this ConfigFormat format) =>
        format == ConfigFormat.Toml ? "TOML" : "JSON";
}

/// <summary>
/// How a client turns a server off without deleting it.
/// </summary>
/// <remarks>
/// This differs per client and it is the whole substance of the matrix, so it is
/// described as data next to the source it applies to rather than hardcoded in the
/// toggle path.
/// </remarks>
public abstract record EnablementStrategy
{
    /// <summary>
    /// No mechanism: present means on. Toggling off has to remove the entry, so
    /// the definition is parked in Kytto's own store to make it reversible.
    /// </summary>
    public sealed record Presence : EnablementStrategy;

    /// <summary>
    /// A list of denied server names in a separate settings file. Claude Code:
    /// <c>~/.claude/settings.json</c> → <c>deniedMcpServers: [{serverName}]</c>.
    /// </summary>
    public sealed record DenyList(PlatformPath File, string Key, string NameField) : EnablementStrategy;

    /// <summary>
    /// A per-extension settings file holding a boolean. Claude Desktop:
    /// <c>Claude Extensions Settings/&lt;id&gt;.json</c> → <c>isEnabled</c>.
    /// </summary>
    public sealed record ExtensionFlag(string Key) : EnablementStrategy;

    /// <summary>
    /// A boolean on the server's own entry, in the same file. Codex:
    /// <c>[mcp_servers.&lt;name&gt;]</c> → <c>enabled = false</c>.
    /// </summary>
    /// <remarks>
    /// The kindest of the four: the definition never leaves the file, so nothing
    /// has to be parked and switching back on cannot lose anything. Absent means
    /// on, which is why writing the key is only ever needed to turn something off
    /// — though Kytto writes it either way, so the state is visible in the file.
    /// </remarks>
    public sealed record InlineFlag(string Key) : EnablementStrategy;

    /// <summary>
    /// What switching a server off in this client actually does to the file, in
    /// one sentence (§7.8).
    /// </summary>
    /// <remarks>
    /// Prose about a client belongs next to the client, for the same reason
    /// <c>SchemaQuirks</c> does: the difference between these four is the substance
    /// of the matrix, and the UI must not be the place it is written down.
    /// </remarks>
    public string OffSwitchSummary => this switch
    {
        Presence =>
            "This client has no off switch, so switching a server off here removes " +
            "its definition. Kytto stores the original text first and puts it back " +
            "byte for byte when you switch it on again.",
        DenyList =>
            "Switching a server off adds it to a deny list in a different file. The " +
            "definition itself stays exactly where it is.",
        ExtensionFlag =>
            "Each extension carries its own on/off flag in a settings file beside " +
            "the bundle, so nothing is removed to switch one off.",
        InlineFlag flag =>
            $"Switching a server off writes {flag.Key} = false on the server's own " +
            "entry, so its definition never leaves the file.",
        _ => "",
    };
}

/// <summary>
/// Where one client keeps its MCP servers.
/// </summary>
/// <remarks>
/// A client can have more than one source, and the sources need not share an
/// enablement mechanism. Claude Desktop has two — the <c>mcpServers</c> map, which
/// has no off switch, and extension bundles, which have a boolean each. Reading
/// only the first is why a naive implementation shows an empty column on a modern
/// install.
/// </remarks>
public abstract record ConfigSource
{
    /// <summary>
    /// A map of <c>name: definition</c> entries at <c>ServersKey</c> inside a
    /// config file.
    /// </summary>
    /// <remarks>
    /// In JSON that is an object; in TOML it is a <c>[serversKey.&lt;name&gt;]</c>
    /// table per server. Same idea, and the same one editable map per client.
    /// </remarks>
    public sealed record ServerMap(
        PlatformPath File,
        ConfigFormat Format,
        string ServersKey,
        EnablementStrategy Enablement) : ConfigSource
    {
        public override PlatformPath Path => File;
    }

    /// <summary>
    /// One directory per extension holding a <c>manifest.json</c>, with the on/off
    /// flag in a sibling settings directory.
    /// </summary>
    public sealed record ExtensionBundles(
        PlatformPath Directory,
        PlatformPath SettingsDirectory,
        string FlagKey) : ConfigSource
    {
        public override PlatformPath Path => Directory;
    }

    /// <summary>
    /// Servers that arrive inside installed packages, each carrying a config file
    /// in the ordinary <c>mcpServers</c> shape somewhere under a root directory.
    /// </summary>
    /// <remarks>
    /// Codex plugins. Like Claude Desktop extensions these are installed software,
    /// not something Kytto authors into — the directory is a cache the client
    /// repopulates — so they are read and shown, never written.
    /// </remarks>
    public sealed record BundledPackages(
        PlatformPath Root,
        string ManifestName,
        string ServersKey) : ConfigSource
    {
        public override PlatformPath Path => Root;
    }

    public abstract PlatformPath Path { get; }
}

public sealed record ClientDescriptor(
    ClientId Id,
    string DisplayName,
    string IconAsset,
    /// <summary>
    /// How the client is recognised as installed. A leftover config directory is
    /// not evidence — <c>~/.cursor</c> outlives an uninstall.
    /// </summary>
    IReadOnlyList<string> InstallKeys,
    /// <summary>
    /// Store (MSIX) package family names, the Windows analogue of the macOS bundle
    /// identifiers this registry mirrors.
    /// </summary>
    /// <remarks>
    /// A stable name that survives an update, where <see cref="InstallKeys"/> is a
    /// path that does not. It also carries the app's real branding, so a client
    /// found this way gets its own icon rather than a drawn placeholder.
    /// </remarks>
    IReadOnlyList<string> PackageFamilyNames,
    /// <summary>For clients that are a CLI rather than an installed application.</summary>
    IReadOnlyList<string> ExecutableNames,
    IReadOnlyList<ConfigSource> Sources,
    /// <summary>How this client differs. Read this before touching its parsing.</summary>
    string SchemaQuirks,
    /// <summary>
    /// How many tools this client hands the model before it drops or refuses the
    /// rest. Null when there is no documented cap — see <see cref="ClientToolLimit"/>.
    /// </summary>
    ClientToolLimit? ToolLimit = null)
{
    /// <summary>
    /// The file and key holding servers the user can add, edit and remove.
    /// </summary>
    /// <remarks>
    /// Every client has exactly one. The other source kinds — Claude Desktop's
    /// extension bundles, Codex's plugins — are installed software, not something
    /// Kytto authors into.
    /// </remarks>
    public ConfigSource.ServerMap? EditableServerMap =>
        Sources.OfType<ConfigSource.ServerMap>().FirstOrDefault();

    /// <summary>
    /// What switching a server off does in this client, for the client screen (§7.8).
    /// </summary>
    /// <remarks>
    /// Composed rather than stored, because a client with two sources answers the
    /// question twice: Claude Desktop deletes a config-file definition to switch it
    /// off and flips a flag to switch an extension off, and a screen that mentioned
    /// only the first would be telling half the truth about the client where it
    /// matters most.
    /// </remarks>
    public string OffSwitchSummary
    {
        get
        {
            var parts = new List<string>();
            if (EditableServerMap is { } editable)
            {
                parts.Add(editable.Enablement.OffSwitchSummary);
            }
            foreach (var source in Sources)
            {
                switch (source)
                {
                    case ConfigSource.ServerMap:
                        continue;
                    case ConfigSource.ExtensionBundles:
                        parts.Add(
                            "Servers that arrive as extension bundles work differently: each " +
                            "carries its own on/off flag beside the bundle, so switching one " +
                            "off removes nothing.");
                        break;
                    case ConfigSource.BundledPackages:
                        parts.Add(
                            "Servers that arrive inside installed plugins are read-only — that " +
                            "directory is a cache the client rewrites, so Kytto shows them and " +
                            "never edits them.");
                        break;
                }
            }
            return string.Join(" ", parts);
        }
    }
}
