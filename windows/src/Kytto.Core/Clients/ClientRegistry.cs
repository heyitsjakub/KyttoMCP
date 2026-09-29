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
        ],
        SchemaQuirks:
            "`~/.claude.json` is not a small config file — it also holds per-project " +
            "history and routinely runs to hundreds of kilobytes. Only the top-level " +
            "`mcpServers` is writable; `projects.<path>.mcpServers` is surfaced as a " +
            "read-only workspace column (§4), so it must never become a write target. " +
            "Enablement lives " +
            "in a different file entirely: `~/.claude/settings.json` → `deniedMcpServers`, " +
            "an array of objects with a `serverName` field. A denied server stays in the " +
            "config and must still be listed, shown as off.",
        // No tool limit: MCP tools are deferred behind tool search by default, with
        // a catalog of up to 10,000
        // (https://code.claude.com/docs/en/agent-sdk/tool-search).
        ToolLimit: null);

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
            new ConfigSource.BundledPackages(
                Root: new PlatformPath(
                    Darwin: "~/.codex/plugins",
                    Windows: @"%USERPROFILE%\.codex\plugins"),
                ManifestName: ".mcp.json",
                ServersKey: "mcpServers"),
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
            "CLI. Plugins under `~/.codex/plugins` can bundle their own `.mcp.json` in " +
            "the ordinary `mcpServers` shape; those are installed packages in a cache " +
            "directory the client repopulates, so they are read and shown but never written.",
        // No tool limit: none is documented, and MCP tools are deferred behind tool
        // search where the model supports it
        // (https://github.com/openai/codex/pull/29486).
        ToolLimit: null);
}
