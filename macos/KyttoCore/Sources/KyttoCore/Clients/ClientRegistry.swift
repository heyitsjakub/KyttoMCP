import Foundation

/// The one place any config path or schema quirk is written down (§4).
///
/// These paths move between client releases. Adding or fixing a client must stay
/// a one-file change, so nothing here may leak into parsing or UI code.
public enum ClientRegistry {

    public static let all: [ClientDescriptor] = [
        claudeDesktop,
        claudeCode,
        cursor,
        vsCode,
        codex,
    ]

    public static func descriptor(for id: ClientID) -> ClientDescriptor {
        guard let match = descriptorIfKnown(for: id) else {
            preconditionFailure("no descriptor for \(id)")
        }
        return match
    }

    public static func descriptorIfKnown(for id: ClientID) -> ClientDescriptor? {
        all.first(where: { $0.id == id })
    }

    /// Custom sources are intentionally absent from `all`: every write service
    /// receives `all`, so this separation is the native enforcement of their
    /// read-only promise rather than a disabled button alone.
    public static func customDescriptor(_ source: CustomConfigSource) -> ClientDescriptor {
        ClientDescriptor(
            id: source.clientID,
            displayName: source.displayName,
            iconAsset: "client-custom",
            bundleIdentifiers: [],
            executableNames: [],
            sources: [
                .readOnlyAutoServerMap(
                    file: PlatformPath(darwin: source.path, windows: source.path)
                )
            ],
            schemaQuirks: "Custom source. Kytto automatically recognizes a supported JSON, JSONC or TOML server map and never writes to this file.",
            isReadOnly: true,
            configurationScope: source.scope,
            scopeLabel: source.scopeLabel
        )
    }

    /// Built-ins plus read-only Claude Code project scopes found in its shared
    /// JSON document. The document is the only place inspected; this does not
    /// crawl projects or the user's home directory.
    public static func allIncludingClaudeCodeProjects(
        home: URL,
        pathOverride: String? = nil
    ) -> [ClientDescriptor] {
        let path = pathOverride ?? "~/.claude.json"
        let url: URL = if path.hasPrefix("~/") {
            home.appending(path: String(path.dropFirst(2)))
        } else {
            URL(filePath: path)
        }
        guard let data = try? Data(contentsOf: url),
              let object = try? JSONSerialization.jsonObject(with: data),
              let root = object as? [String: Any],
              let projects = root["projects"] as? [String: Any]
        else { return all }

        // Claude Code writes an empty `mcpServers: {}` into every directory it has
        // ever been run in, so the key's presence says nothing — a long-used
        // machine carries dozens of them. A scope earns a place in the matrix by
        // actually configuring a server; the rest is the client's bookkeeping.
        let projectPaths = projects.keys.sorted().filter { key in
            guard let project = projects[key] as? [String: Any],
                  let servers = project["mcpServers"] as? [String: Any]
            else { return false }
            return !servers.isEmpty
        }

        let leaves = compactProjectLeaves(for: projectPaths)
        let projectDescriptors = projectPaths.map { projectPath in
            claudeCodeProjectDescriptor(
                projectPath: projectPath,
                displayLeaf: leaves[projectPath] ?? projectPath,
                configPath: path
            )
        }
        return all + projectDescriptors
    }

    /// The shortest right-hand slice of each project path that still tells the
    /// projects apart.
    ///
    /// Home-directory paths share their prefix, so truncating a sidebar label
    /// from the right shows exactly the part that never differs — three rows
    /// all reading `Claude Code · /U…`. The label therefore keeps the *end* of
    /// the path: the last component, extended leftwards only as far as needed
    /// to be unique among the scopes actually present. The full path stays in
    /// `scopeLabel` for tooltips and the client screen.
    static func compactProjectLeaves(for projectPaths: [String]) -> [String: String] {
        // Split on both separators so a Windows-keyed `~/.claude.json` read on
        // another machine still shortens instead of surviving whole (§3.2).
        let components = Dictionary(uniqueKeysWithValues: projectPaths.map { path in
            (path, path.split(whereSeparator: { $0 == "/" || $0 == "\\" }).map(String.init))
        })

        func leaf(of path: String, depth: Int) -> String {
            let parts = components[path] ?? []
            guard !parts.isEmpty, depth < parts.count else { return path }
            let separator = path.contains("\\") ? "\\" : "/"
            return "…" + separator + parts.suffix(depth).joined(separator: separator)
        }

        var result: [String: String] = [:]
        for path in projectPaths {
            var depth = 1
            var candidate = leaf(of: path, depth: depth)
            // Two checkouts ending in the same directory name keep growing until
            // they differ; unrelated paths stay at their last component.
            while projectPaths.contains(where: { $0 != path && leaf(of: $0, depth: depth) == candidate }),
                  depth < (components[path]?.count ?? 1) {
                depth += 1
                candidate = leaf(of: path, depth: depth)
            }
            result[path] = candidate
        }
        return result
    }

    private static func claudeCodeProjectDescriptor(
        projectPath: String,
        displayLeaf: String,
        configPath: String
    ) -> ClientDescriptor {
        let digest = ConfigWriter.digest(of: projectPath)
        let uuidText = String(digest.prefix(32))
        let uuid = UUID(uuidString: "\(uuidText.prefix(8))-\(uuidText.dropFirst(8).prefix(4))-\(uuidText.dropFirst(12).prefix(4))-\(uuidText.dropFirst(16).prefix(4))-\(uuidText.dropFirst(20).prefix(12))")!
        return ClientDescriptor(
            id: .custom(uuid),
            displayName: "Claude Code · \(displayLeaf)",
            iconAsset: "client-claude-code",
            bundleIdentifiers: [],
            executableNames: [],
            sources: [
                .scopedJSONServerMap(
                    file: PlatformPath(
                        darwin: configPath,
                        windows: #"%USERPROFILE%\.claude.json"#
                    ),
                    scopePath: projectPath,
                    serversKey: "mcpServers"
                )
            ],
            schemaQuirks: "Claude Code project/workspace scope. Kytto reads this project entry from the shared Claude Code file and never writes it.",
            isReadOnly: true,
            configurationScope: .workspace,
            scopeLabel: projectPath,
            shortName: displayLeaf
        )
    }

    // MARK: - Claude Desktop

    static let claudeDesktop = ClientDescriptor(
        id: .claudeDesktop,
        displayName: "Claude Desktop",
        iconAsset: "client-claude-desktop",
        bundleIdentifiers: ["com.anthropic.claudefordesktop"],
        executableNames: [],
        sources: [
            .serverMap(
                file: PlatformPath(
                    darwin: "~/Library/Application Support/Claude/claude_desktop_config.json",
                    windows: #"%APPDATA%\Claude\claude_desktop_config.json"#
                ),
                format: .json,
                serversKey: "mcpServers",
                enablement: .presence
            ),
            .extensionBundles(
                directory: PlatformPath(
                    darwin: "~/Library/Application Support/Claude/Claude Extensions",
                    windows: #"%APPDATA%\Claude\Claude Extensions"#
                ),
                settingsDirectory: PlatformPath(
                    darwin: "~/Library/Application Support/Claude/Claude Extensions Settings",
                    windows: #"%APPDATA%\Claude\Claude Extensions Settings"#
                ),
                flagKey: "isEnabled"
            ),
        ],
        schemaQuirks: """
        Two sources. On current builds `claude_desktop_config.json` often has no \
        `mcpServers` key at all — servers arrive as extension bundles instead, and \
        a config with only `coworkUserFilesPath` and `preferences` is normal, not \
        an error. Extensions carry their command in `manifest.json` at \
        `server.mcp_config` and their on/off state in a separate settings file. \
        Extension args contain `${__dirname}`, which is relative to the bundle \
        directory and must not be resolved for display.
        """
        // No tool limit: none is documented. A cut to the alphabetically first
        // 256 connector tools in July 2026 was a regression and has been fixed
        // (https://www.mintmcp.com/docs/mcp-client-issues-claude), not a cap.
    )

    // MARK: - Claude Code

    static let claudeCode = ClientDescriptor(
        id: .claudeCode,
        displayName: "Claude Code",
        iconAsset: "client-claude-code",
        bundleIdentifiers: [],
        executableNames: ["claude"],
        sources: [
            .serverMap(
                file: PlatformPath(
                    darwin: "~/.claude.json",
                    windows: #"%USERPROFILE%\.claude.json"#
                ),
                format: .json,
                serversKey: "mcpServers",
                enablement: .denyList(
                    file: PlatformPath(
                        darwin: "~/.claude/settings.json",
                        windows: #"%USERPROFILE%\.claude\settings.json"#
                    ),
                    key: "deniedMcpServers",
                    nameField: "serverName"
                )
            )
        ],
        schemaQuirks: """
        `~/.claude.json` is not a small config file — it also holds per-project \
        history and routinely runs to hundreds of kilobytes. Only the top-level \
        `mcpServers` is in scope; `projects.<path>.mcpServers` is project scope and \
        is deferred (§4), so it must be skipped without erroring. Enablement lives \
        in a different file entirely: `~/.claude/settings.json` → `deniedMcpServers`, \
        an array of objects with a `serverName` field. A denied server stays in the \
        config and must still be listed, shown as off.
        """
        // No tool limit: MCP tools are deferred behind tool search by default,
        // with a catalog of up to 10,000
        // (https://code.claude.com/docs/en/agent-sdk/tool-search).
    )

    // MARK: - Cursor

    static let cursor = ClientDescriptor(
        id: .cursor,
        displayName: "Cursor",
        iconAsset: "client-cursor",
        bundleIdentifiers: ["com.todesktop.230313mzl4w4u92"],
        executableNames: [],
        sources: [
            .serverMap(
                file: PlatformPath(
                    darwin: "~/.cursor/mcp.json",
                    windows: #"%USERPROFILE%\.cursor\mcp.json"#
                ),
                format: .json,
                serversKey: "mcpServers",
                enablement: .presence
            )
        ],
        schemaQuirks: """
        Same `mcpServers` shape as Claude Desktop, plus `envFile`, `headers` and \
        `auth` fields we pass through untouched. Cursor's own enable/disable toggle \
        stores state internally, not in `mcp.json`, so Kytto cannot read or write it \
        — presence is the only lever. Note that `~/.cursor` survives uninstalling \
        Cursor, so it is not evidence the client is installed.
        """
        // No tool limit any more. Cursor used to send only the first 40 tools
        // and silently drop the rest
        // (https://forum.cursor.com/t/tools-limited-to-40-total/67976). Since
        // January 2026 the agent receives tool names only and looks tools up on
        // demand (https://cursor.com/blog/dynamic-context-discovery), and the
        // current docs (https://cursor.com/docs/context/mcp) state no cap.
    )

    // MARK: - VS Code

    static let vsCode = ClientDescriptor(
        id: .vsCode,
        displayName: "VS Code",
        iconAsset: "client-vscode",
        bundleIdentifiers: ["com.microsoft.VSCode"],
        executableNames: [],
        sources: [
            .serverMap(
                file: PlatformPath(
                    darwin: "~/Library/Application Support/Code/User/mcp.json",
                    windows: #"%APPDATA%\Code\User\mcp.json"#
                ),
                format: .json,
                serversKey: "servers",
                enablement: .presence
            )
        ],
        schemaQuirks: """
        The only client that keys servers under `servers` rather than `mcpServers`. \
        The file is JSONC — comments and trailing commas are expected and must \
        survive a write. VS Code's own enabled/disabled state is kept in \
        `globalStorage/state.vscdb`, a SQLite database we will not write to, so \
        presence is the only lever here too. The file often does not exist yet on \
        an install that has never configured MCP.
        """,
        // "A chat request can have a maximum of 128 tools enabled at a time."
        // https://code.visualstudio.com/docs/agents/run/tools
        toolLimit: ClientToolLimit(
            maxTools: 128,
            pastLimitSummary: """
            VS Code allows at most 128 tools in one chat request. Past that, \
            agent mode refuses the request ("Cannot have more than 128 tools per \
            request") unless the experimental virtual tools setting groups them.
            """
        )
    )

    // MARK: - Codex

    static let codex = ClientDescriptor(
        id: .codex,
        displayName: "Codex",
        iconAsset: "client-codex",
        // The application is called ChatGPT and its bundle identifier is not.
        // Detection goes on the identifier, so the name it shows in the Dock is
        // not something Kytto has to have an opinion about.
        bundleIdentifiers: ["com.openai.codex"],
        executableNames: ["codex"],
        sources: [
            .serverMap(
                file: PlatformPath(
                    darwin: "~/.codex/config.toml",
                    windows: #"%USERPROFILE%\.codex\config.toml"#
                ),
                format: .toml,
                serversKey: "mcp_servers",
                enablement: .inlineFlag(key: "enabled")
            ),
            .bundledPackages(
                root: PlatformPath(
                    darwin: "~/.codex/plugins",
                    windows: #"%USERPROFILE%\.codex\plugins"#
                ),
                manifestName: ".mcp.json",
                serversKey: "mcpServers"
            ),
        ],
        schemaQuirks: """
        The only TOML client, and the only one whose off switch is genuinely \
        pleasant: `enabled = false` on the server's own `[mcp_servers.<name>]` \
        table, so a disabled server keeps its definition and nothing has to be \
        parked. Environment variables live in a `[mcp_servers.<name>.env]` \
        sub-table, which means removing a server means removing two tables. The \
        file is not small — a real one carries dozens of `[projects."…"]` tables \
        recording per-directory history — and none of that may move when a server \
        is added. Two front ends share one config: the ChatGPT app (bundle id \
        `com.openai.codex`) and the `codex` CLI. Plugins under `~/.codex/plugins` \
        can bundle their own `.mcp.json` in the ordinary `mcpServers` shape; those \
        are installed packages in a cache directory the client repopulates, so \
        they are read and shown but never written.
        """
        // No tool limit: none is documented, and MCP tools are deferred behind
        // tool search where the model supports it
        // (https://github.com/openai/codex/pull/29486).
    )
}
