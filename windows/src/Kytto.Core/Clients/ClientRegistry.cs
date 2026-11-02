namespace Kytto.Core.Clients;

/// <summary>
/// The one place any config path or schema quirk is written down (§4).
/// </summary>
/// <remarks>
/// These paths move between client releases. Adding or fixing a client must stay a
/// one-file change, so nothing here may leak into parsing or UI code.
/// </remarks>
public static class ClientRegistry
{
    public static IReadOnlyList<ClientDescriptor> All { get; } =
    [
        ClaudeDesktop,
        ClaudeCode,
        Cursor,
        VsCode,
        Codex,
        Antigravity,
        DevinDesktop,
        OpenCode,
    ];

    public static ClientDescriptor Descriptor(ClientId id) =>
        All.FirstOrDefault(descriptor => descriptor.Id == id)
        ?? throw new InvalidOperationException($"no descriptor for {id}");

    /// <summary>
    /// Where Windows redirects a packaged Claude Desktop's <c>%APPDATA%\Claude</c>.
    /// </summary>
    /// <remarks>
    /// An MSIX app's writes to Roaming land under its own package container. Kytto
    /// is not packaged, so it has to name that container outright — writing the
    /// unredirected path instead would produce a second config the client never
    /// reads, which is the failure anthropics/claude-code#26073 describes: MCP
    /// servers configured and silently never loaded.
    /// </remarks>
    private const string PackagedClaude =
        @"%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude";

    // MARK: - Claude Desktop

    private static ClientDescriptor ClaudeDesktop => new(
        Id: ClientId.ClaudeDesktop,
        DisplayName: "Claude Desktop",
        IconAsset: "client-claude-desktop",
        // Installs per-user under Local rather than into Program Files, so this is
        // where the executable is even though it is an ordinary desktop app.
        // That is the older NSIS installer's location. Current builds ship as MSIX and put
        // the executable inside the package instead, which is why the family name
        // below is what actually finds one.
        InstallKeys: [@"%LOCALAPPDATA%\AnthropicClaude\claude.exe"],
        PackageFamilyNames: ["Claude_pzs8sxrjxfjjc", "Anthropic.ClaudeDesktop_h6f0761"],
        ExecutableNames: [],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/Library/Application Support/Claude/claude_desktop_config.json",
                    Windows: @"%APPDATA%\Claude\claude_desktop_config.json")
                {
                    WindowsAlternates = [PackagedClaude + @"\claude_desktop_config.json"],
                },
                Format: ConfigFormat.Json,
                ServersKey: "mcpServers",
                Enablement: new EnablementStrategy.Presence()),
            new ConfigSource.ExtensionBundles(
                Directory: new PlatformPath(
                    Darwin: "~/Library/Application Support/Claude/Claude Extensions",
                    Windows: @"%APPDATA%\Claude\Claude Extensions")
                {
                    WindowsAlternates = [PackagedClaude + @"\Claude Extensions"],
                },
                SettingsDirectory: new PlatformPath(
                    Darwin: "~/Library/Application Support/Claude/Claude Extensions Settings",
                    Windows: @"%APPDATA%\Claude\Claude Extensions Settings")
                {
                    WindowsAlternates = [PackagedClaude + @"\Claude Extensions Settings"],
                },
                FlagKey: "isEnabled"),
        ],
        SchemaQuirks:
            "Installed from the Store on current builds, which moves everything: the " +
            "config the app reads is under its package container, not `%APPDATA%\\Claude`. " +
            "Both are listed, the one that exists wins, and getting this wrong writes a " +
            "second config the client never opens. " +
            "Two sources. On current builds `claude_desktop_config.json` often has no " +
            "`mcpServers` key at all — servers arrive as extension bundles instead, and " +
            "a config with only `coworkUserFilesPath` and `preferences` is normal, not " +
            "an error. Extensions carry their command in `manifest.json` at " +
            "`server.mcp_config` and their on/off state in a separate settings file. " +
            "Extension args contain `${__dirname}`, which is relative to the bundle " +
            "directory and must not be resolved for display.",
        // No tool limit: none is documented. A cut to the alphabetically first 256
        // connector tools in July 2026 was a regression and has been fixed
        // (https://www.mintmcp.com/docs/mcp-client-issues-claude), not a cap.
        ToolLimit: null);

    // MARK: - Claude Code

    private static ClientDescriptor ClaudeCode => new(
        Id: ClientId.ClaudeCode,
        DisplayName: "Claude Code",
        IconAsset: "client-claude-code",
        InstallKeys: [],
        PackageFamilyNames: [],
        // A CLI, so it is found on PATH rather than as installed software. The
        // `.cmd` shim is what an npm install puts there on Windows.
        ExecutableNames: ["claude.cmd", "claude.exe", "claude"],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.claude.json",
                    Windows: @"%USERPROFILE%\.claude.json"),
                Format: ConfigFormat.Json,
                ServersKey: "mcpServers",
                Enablement: new EnablementStrategy.DenyList(
                    File: new PlatformPath(
                        Darwin: "~/.claude/settings.json",
                        Windows: @"%USERPROFILE%\.claude\settings.json"),
                    Key: "deniedMcpServers",
                    NameField: "serverName")),
            // Installed, enabled plugins: read-only, and only what a registration
            // names (§4). Project and local registrations become workspace scopes.
            new ConfigSource.RegisteredPlugins(PluginRegistrySource.ClaudeCodeDefault),
        ],
        SchemaQuirks:
            "`~/.claude.json` is not a small config file — it also holds per-project " +
            "history and routinely runs to hundreds of kilobytes. Only the top-level " +
            "`mcpServers` is writable; `projects.<path>.mcpServers` is surfaced as a " +
            "read-only workspace column (§4), so it must never become a write target. " +
            "Installed plugins that Claude Code itself has registered (`~/.claude/plugins/" +
            "installed_plugins.json`) and enabled (`enabledPlugins` in user, project or " +
            "local settings) are a second, read-only source: only the MCP definitions the " +
            "plugin declares are read, and nothing in the plugin cache is ever written. " +
            "Enablement lives " +
            "in a different file entirely: `~/.claude/settings.json` → `deniedMcpServers`, " +
            "an array of objects with a `serverName` field. A denied server stays in the " +
            "config and must still be listed, shown as off.",
        // No tool limit: MCP tools are deferred behind tool search by default, with
        // a catalog of up to 10,000
        // (https://code.claude.com/docs/en/agent-sdk/tool-search).
        ToolLimit: null)
    {
        ToolSwitch = new ToolSwitch.PermissionRules(
            File: new PlatformPath(
                Darwin: "~/.claude/settings.json",
                Windows: @"%USERPROFILE%\.claude\settings.json"),
            Path: ["permissions", "deny"]),
    };

    // MARK: - Cursor

    private static ClientDescriptor Cursor => new(
        Id: ClientId.Cursor,
        DisplayName: "Cursor",
        IconAsset: "client-cursor",
        InstallKeys:
        [
            @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe",
            @"%PROGRAMFILES%\Cursor\Cursor.exe",
        ],
        // An ordinary installer, not a Store package.
        PackageFamilyNames: [],
        ExecutableNames: [],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.cursor/mcp.json",
                    Windows: @"%USERPROFILE%\.cursor\mcp.json"),
                Format: ConfigFormat.Json,
                ServersKey: "mcpServers",
                Enablement: new EnablementStrategy.Presence()),
        ],
        SchemaQuirks:
            "Same `mcpServers` shape as Claude Desktop, plus `envFile`, `headers` and " +
            "`auth` fields we pass through untouched. Cursor's own enable/disable toggle " +
            "stores state internally, not in `mcp.json`, so Kytto cannot read or write it " +
            "— presence is the only lever. Note that `~/.cursor` survives uninstalling " +
            "Cursor, so it is not evidence the client is installed.",
        // No tool limit any more. Cursor used to send only the first 40 tools and
        // silently drop the rest
        // (https://forum.cursor.com/t/tools-limited-to-40-total/67976). Since
        // January 2026 the agent receives tool names only and looks tools up on
        // demand (https://cursor.com/blog/dynamic-context-discovery), and the
        // current docs (https://cursor.com/docs/context/mcp) state no cap.
        ToolLimit: null);

    // MARK: - VS Code

    private static ClientDescriptor VsCode => new(
        Id: ClientId.VsCode,
        DisplayName: "VS Code",
        IconAsset: "client-vscode",
        InstallKeys:
        [
            @"%LOCALAPPDATA%\Programs\Microsoft VS Code\Code.exe",
            @"%PROGRAMFILES%\Microsoft VS Code\Code.exe",
        ],
        // An ordinary installer, not a Store package.
        PackageFamilyNames: [],
        ExecutableNames: [],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/Library/Application Support/Code/User/mcp.json",
                    Windows: @"%APPDATA%\Code\User\mcp.json"),
                Format: ConfigFormat.Json,
                ServersKey: "servers",
                Enablement: new EnablementStrategy.Presence()),
        ],
        SchemaQuirks:
            "The only client that keys servers under `servers` rather than `mcpServers`. " +
            "The file is JSONC — comments and trailing commas are expected and must " +
            "survive a write. VS Code's own enabled/disabled state is kept in " +
            "`globalStorage/state.vscdb`, a SQLite database we will not write to, so " +
            "presence is the only lever here too. The file often does not exist yet on " +
            "an install that has never configured MCP.",
        // "A chat request can have a maximum of 128 tools enabled at a time."
        // https://code.visualstudio.com/docs/agents/run/tools
        ToolLimit: new ClientToolLimit(
            MaxTools: 128,
            PastLimitSummary:
                "VS Code allows at most 128 tools in one chat request. Past that, " +
                "agent mode refuses the request (\"Cannot have more than 128 tools per " +
                "request\") unless the experimental virtual tools setting groups them."));

    // MARK: - Codex

    private static ClientDescriptor Codex => new(
        Id: ClientId.Codex,
        DisplayName: "Codex",
        IconAsset: "client-codex",
        // The application is called ChatGPT and its directory is not, the same way
        // its bundle identifier is not on macOS. Detection goes on the install
        // location, so the name it shows in the Start menu is not something Kytto
        // has to have an opinion about.
        InstallKeys: [@"%LOCALAPPDATA%\Programs\ChatGPT\ChatGPT.exe"],
        // Codex is distributed through the Store, so the family name is what
        // actually finds it — and what carries its branding.
        PackageFamilyNames: ["OpenAI.Codex_2p2nqsd0c76g0"],
        ExecutableNames: ["codex.cmd", "codex.exe", "codex"],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.codex/config.toml",
                    Windows: @"%USERPROFILE%\.codex\config.toml"),
                Format: ConfigFormat.Toml,
                ServersKey: "mcp_servers",
                Enablement: new EnablementStrategy.InlineFlag("enabled")),
            new ConfigSource.RegisteredPlugins(new PluginRegistrySource.Codex(
                Config: new PlatformPath(
                    Darwin: "~/.codex/config.toml",
                    Windows: @"%USERPROFILE%\.codex\config.toml"),
                Cache: new PlatformPath(
                    Darwin: "~/.codex/plugins/cache",
                    Windows: @"%USERPROFILE%\.codex\plugins\cache"))),
        ],
        SchemaQuirks:
            "The only TOML client, and the only one whose off switch is genuinely " +
            "pleasant: `enabled = false` on the server's own `[mcp_servers.<name>]` " +
            "table, so a disabled server keeps its definition and nothing has to be " +
            "parked. Environment variables live in a `[mcp_servers.<name>.env]` " +
            "sub-table, which means removing a server means removing two tables. The " +
            "file is not small — a real one carries dozens of `[projects.\"…\"]` tables " +
            "recording per-directory history — and none of that may move when a server " +
            "is added. Two front ends share one config: the ChatGPT app and the `codex` " +
            "CLI. A plugin counts only when `[plugins.\"name@marketplace\"]` in that same " +
            "file says `enabled = true` and its package is in `~/.codex/plugins/cache`; " +
            "Kytto reads the MCP definitions the package declares (its root `.mcp.json` " +
            "and the paths its `.codex-plugin/plugin.json` names), at the registered " +
            "version or the only installed one. A cache with several versions and no " +
            "registered one is skipped with a diagnostic, and a cache nobody registered " +
            "is never read. Packages sit in a directory the client repopulates, so they " +
            "are shown but never written.",
        // No tool limit: none is documented, and MCP tools are deferred behind tool
        // search where the model supports it
        // (https://github.com/openai/codex/pull/29486).
        ToolLimit: null)
    {
        ToolSwitch = new ToolSwitch.EntryLists(Deny: "disabled_tools", Allow: "enabled_tools"),
    };

    // MARK: - Google Antigravity

    private static ClientDescriptor Antigravity => new(
        Id: ClientId.Antigravity,
        DisplayName: "Antigravity",
        IconAsset: "client-antigravity",
        // Per-user install under Local\Programs, the way Electron-style editors
        // arrive. Not verified against a real install (none is on the test machine);
        // a missing app only changes the "installed" label.
        InstallKeys: [@"%LOCALAPPDATA%\Programs\Antigravity\Antigravity.exe"],
        PackageFamilyNames: [],
        ExecutableNames: ["agy.cmd", "agy.exe", "agy", "antigravity.cmd", "antigravity.exe", "antigravity"],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.gemini/config/mcp_config.json",
                    Windows: @"%USERPROFILE%\.gemini\config\mcp_config.json"),
                Format: ConfigFormat.Json,
                ServersKey: "mcpServers",
                Enablement: new EnablementStrategy.InlineDisabledFlag("disabled")),
        ],
        SchemaQuirks:
            "The `mcpServers` shape with three differences that matter: a remote " +
            "endpoint is `serverUrl` (`url` is read too), a `type` field is rejected, " +
            "and remote servers must speak Streamable HTTP — the legacy HTTP+SSE " +
            "transport is not accepted. `\"disabled\": true` switches a server off " +
            "without removing it, and `enabledTools`/`disabledTools` filter its " +
            "tools; Kytto keeps both. The file is shared by the Antigravity app, IDE " +
            "and CLI. Older releases kept it at `~/.gemini/antigravity/mcp_config.json`; " +
            "point Kytto there with a path override if that is the file you use. " +
            "Workspace configs (`.agents/mcp_config.json`) are not read.")
    {
        ServerSchema = ServerSchema.Antigravity,
        ToolSwitch = new ToolSwitch.EntryLists(Deny: "disabledTools", Allow: "enabledTools"),
    };

    // MARK: - Devin Desktop (formerly Windsurf)

    private static ClientDescriptor DevinDesktop => new(
        Id: ClientId.DevinDesktop,
        DisplayName: "Devin Desktop",
        IconAsset: "client-devin",
        // Windsurf became Devin Desktop on 2 June 2026 and kept its app, so the
        // install is looked for under both names. Unverified for the same reason.
        InstallKeys:
        [
            @"%LOCALAPPDATA%\Programs\Windsurf\Windsurf.exe",
            @"%LOCALAPPDATA%\Programs\Devin\Devin.exe",
        ],
        PackageFamilyNames: [],
        ExecutableNames: ["devin.cmd", "devin.exe", "devin", "windsurf.cmd", "windsurf.exe", "windsurf"],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.config/devin/mcp_config.json",
                    Windows: @"%APPDATA%\devin\mcp_config.json"),
                Format: ConfigFormat.Json,
                ServersKey: "mcpServers",
                Enablement: new EnablementStrategy.InlineDisabledFlag("disabled")),
        ],
        SchemaQuirks:
            "Windsurf, renamed Devin Desktop in June 2026. The user-level " +
            "`mcp_config.json` uses the `mcpServers` shape; a remote server is a " +
            "`url`, and only the legacy SSE transport is stated, as `transport: " +
            "\"sse\"`. `\"disabled\": true` switches a server off without removing it. " +
            "Windsurf kept the same shape at `~/.codeium/windsurf/mcp_config.json`, " +
            "which Devin Desktop migrates on first launch; a Windsurf install that " +
            "has not been migrated can be pointed at with a path override. Project " +
            "configs (`.devin/mcp_config.json`) are not read.")
    {
        ServerSchema = ServerSchema.Devin,
    };

    // MARK: - OpenCode

    private static ClientDescriptor OpenCode => new(
        Id: ClientId.OpenCode,
        DisplayName: "OpenCode",
        IconAsset: "client-opencode",
        InstallKeys: [@"%LOCALAPPDATA%\Programs\OpenCode\OpenCode.exe"],
        PackageFamilyNames: [],
        ExecutableNames: ["opencode.cmd", "opencode.exe", "opencode"],
        Sources:
        [
            new ConfigSource.ServerMap(
                File: new PlatformPath(
                    Darwin: "~/.config/opencode/opencode.json",
                    Windows: @"%USERPROFILE%\.config\opencode\opencode.json"),
                Format: ConfigFormat.Json,
                ServersKey: "mcp",
                Enablement: new EnablementStrategy.InlineFlag("enabled")),
        ],
        SchemaQuirks:
            "A schema of its own, under `mcp` rather than `mcpServers`: " +
            "`type: \"local\"` with the command and its arguments as one array in " +
            "`command` and variables in `environment`, or `type: \"remote\"` with a " +
            "`url`. `enabled: false` switches a server off and keeps it. The file is " +
            "JSONC — comments and trailing commas survive a write — and holds " +
            "OpenCode's whole configuration, including the `tools` map that " +
            "switches off single MCP tools as `<server>_<tool>`. A global " +
            "`opencode.jsonc`, `OPENCODE_CONFIG` or project `opencode.json` is not " +
            "read; use a path override for the first two.")
    {
        ServerSchema = ServerSchema.OpenCode,
        ToolSwitch = new ToolSwitch.PrefixedToolMap("tools"),
    };
}
