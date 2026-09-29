// Canned IPC responses for the browser harness.
//
// Shaped after a real machine — Claude Desktop carrying its servers as
// extensions, one server denied in Claude Code, a leftover Cursor config with no
// Cursor installed, VS Code carrying more tools than a Copilot chat request
// accepts, and a server switched off in Codex without leaving its config — so
// working on the UI in a browser means working against states that actually
// occur.
//
// Stateful on purpose: toggling here really flips a cell, records a pending
// restart and adds a backup, so write-path UI can be built and seen without the
// shell. Nothing is persisted; reloading resets it.
//
// Loaded only by dev.html. Nothing in the shipped app imports this file.

// `?undetected=vsCode` gives the matrix a real notInstalled column without
// changing the default fixture, where VS Code is past its tool limit. Both
// variants are isolated in this in-memory harness.
const UNDETECTED_CLIENT =
  typeof location === 'undefined' ? null : new URLSearchParams(location.search).get('undetected');

const CUSTOM_SOURCE_ID = 'custom.1f0d32d9-88e7-40b8-9b8e-2f38edc4a7d1';
const CLAUDE_PROJECTS = [
  ['custom.4359a513-6f21-4e56-8a9f-0b269c376b65', 'api', 'C:\\Users\\you\\source\\repos\\api'],
  ['custom.a5a0c75b-717d-4ef3-b1fb-9bb75ea23a4c', 'docs', 'C:\\Users\\you\\source\\repos\\docs'],
  ['custom.de2a71f1-d645-464b-b8ee-00bc0dc37d80', 'web', 'C:\\Users\\you\\source\\repos\\web'],
  ['custom.f22796d3-5d34-467c-9c60-1372f4bb1128', 'worker', 'C:\\Users\\you\\source\\repos\\worker'],
].map(([id, label, path]) => ({
  id,
  displayName: `Claude Code · …\\${label}`,
  shortName: `…\\${label}`,
  iconAsset: 'client-claude-code',
  state: 'ready',
  configPathDisplay: 'C:\\Users\\you\\.claude.json',
  configFormatDisplay: 'JSON',
  offSwitchSummary: 'This Claude Code project scope is read-only. Kytto never changes nested projects entries.',
  serverCount: 1,
  schemaQuirks: 'Read-only workspace scope discovered from the existing Claude Code projects map.',
  isReadOnly: true,
  configurationScope: 'workspace',
  scopeLabel: path,
}));
const UPDATE_SHA256 = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

/// The four off switches, worded as the client registry words them. Two clients
/// share one — presence, the destructive one — which is the point: the client
/// screen (§7.8) exists so that difference is readable somewhere.
const OFF_SWITCH = {
  presence:
    'This client has no off switch, so switching a server off here removes its ' +
    'definition. Kytto stores the original text first and puts it back byte for ' +
    'byte when you switch it on again.',
  denyList:
    'Switching a server off adds it to a deny list in a different file. The ' +
    'definition itself stays exactly where it is.',
  inlineFlag:
    "Switching a server off writes enabled = false on the server's own entry, so " +
    'its definition never leaves the file.',
  extensions:
    'Servers that arrive as extension bundles work differently: each carries its ' +
    'own on/off flag beside the bundle, so switching one off removes nothing.',
  plugins:
    'Servers that arrive inside installed plugins are read-only — that directory ' +
    'is a cache the client rewrites, so Kytto shows them and never edits them.',
};

const CLIENTS = [
  {
    id: 'claudeDesktop',
    displayName: 'Claude Desktop',
    iconAsset: 'client-claude-desktop',
    state: 'ready',
    configPathDisplay: '~/Library/Application Support/Claude/claude_desktop_config.json',
    configFormatDisplay: 'JSON',
    offSwitchSummary: `${OFF_SWITCH.presence} ${OFF_SWITCH.extensions}`,
    serverCount: 3,
    schemaQuirks:
      'Two sources. On current builds claude_desktop_config.json often has no mcpServers key at all — servers arrive as extension bundles instead, and a config holding only preferences is normal rather than an error.',
  },
  {
    id: 'claudeCode',
    displayName: 'Claude Code',
    iconAsset: 'client-claude-code',
    state: 'ready',
    configPathDisplay: '~/.claude.json',
    configFormatDisplay: 'JSON',
    offSwitchSummary: OFF_SWITCH.denyList,
    serverCount: 3,
    schemaQuirks:
      '~/.claude.json also holds per-project history and routinely runs to hundreds of kilobytes. Only the top-level mcpServers is in scope, and enablement lives in ~/.claude/settings.json under deniedMcpServers.',
  },
  {
    id: 'cursor',
    displayName: 'Cursor',
    iconAsset: 'client-cursor',
    state: 'orphanedConfig',
    configPathDisplay: '~/.cursor/mcp.json',
    configFormatDisplay: 'JSON',
    offSwitchSummary: OFF_SWITCH.presence,
    serverCount: 1,
    schemaQuirks:
      "Cursor's own enable/disable toggle stores its state internally rather than in mcp.json, so presence is the only lever Kytto has. ~/.cursor survives uninstalling Cursor, so it is not evidence the client is installed.",
  },
  {
    id: 'vsCode',
    displayName: 'VS Code',
    iconAsset: 'client-vscode',
    state: UNDETECTED_CLIENT === 'vsCode' ? 'notInstalled' : 'ready',
    configPathDisplay: '~/Library/Application Support/Code/User/mcp.json',
    configFormatDisplay: 'JSON',
    offSwitchSummary: OFF_SWITCH.presence,
    serverCount: 3,
    schemaQuirks:
      'The only client that keys servers under servers rather than mcpServers. The file is JSONC — comments and trailing commas are expected and must survive a write.',
  },
  {
    id: 'codex',
    displayName: 'Codex',
    iconAsset: 'client-codex',
    state: 'ready',
    configPathDisplay: '~/.codex/config.toml',
    configFormatDisplay: 'TOML',
    offSwitchSummary: `${OFF_SWITCH.inlineFlag} ${OFF_SWITCH.plugins}`,
    serverCount: 3,
    schemaQuirks:
      'The only TOML client. Environment variables live in a [mcp_servers.<name>.env] sub-table, so removing a server means removing two tables, and the file also carries dozens of [projects."…"] tables that must not move.',
  },
  {
    id: CUSTOM_SOURCE_ID,
    displayName: 'Demo workspace',
    iconAsset: 'client-custom',
    state: 'ready',
    configPathDisplay: 'C:\\Kytto fixtures\\demo-workspace.jsonc',
    configFormatDisplay: 'Auto-detected · read-only',
    offSwitchSummary: 'This custom source is read-only. Kytto never changes its file.',
    serverCount: 1,
    schemaQuirks: 'Attached explicitly and auto-detected from its top-level servers map.',
    isReadOnly: true,
    configurationScope: 'workspace',
    scopeLabel: 'Demo project',
  },
  ...CLAUDE_PROJECTS,
].map((client) => ({
  shortName: null,
  isReadOnly: false,
  configurationScope: 'global',
  scopeLabel: null,
  ...client,
}));

const server = (name, overrides = {}) => ({
  id: name.toLowerCase(),
  name,
  transport: 'stdio',
  command: 'node',
  args: [],
  env: [],
  url: null,
  commandSummary: 'node ${__dirname}/server/index.js',
  enabledIn: {
    claudeDesktop: 'absent',
    claudeCode: 'absent',
    cursor: 'absent',
    vsCode: 'absent',
    codex: 'absent',
    [CUSTOM_SOURCE_ID]: 'absent',
    ...Object.fromEntries(CLAUDE_PROJECTS.map((project) => [project.id, 'absent'])),
  },
  originKind: 'extension',
  isBundled: true,
  hasRelativePath: false,
  health: null,
  tokenWeight: null,
  ...overrides,
});

const passingHealth = (toolCount, tools = []) => ({
  status: 'passed',
  checkedAt: Date.now() / 1000,
  toolCount,
  tools,
  message: null,
  stderr: null,
  durationSeconds: 1.2,
  serverName: 'fixture-server',
  serverVersion: '1.0.0',
  protocolVersion: '2026-07-28',
  capabilityNames: ['tools', 'prompts', 'resources'],
  promptCount: 2,
  resourceCount: 1,
  inspectionNotes: [],
  resolvedCommand: '/opt/homebrew/bin/node',
  environmentSource: 'Login shell PATH',
  authorizationURL: null,
});

const AUTHORIZATION_URL = 'https://auth.example.com/oauth/authorize?client_id=kytto-fixture';
const waitingHealth = () => ({
  status: 'needsAuthorization',
  checkedAt: Date.now() / 1000,
  toolCount: null,
  tools: [],
  message:
    'The server is waiting for you to authorize it in the browser, not broken. ' +
    'Complete the sign-in it asked for, then check it again.',
  stderr: `Please authorize this client by visiting: ${AUTHORIZATION_URL}\n`,
  durationSeconds: 10,
  serverName: null,
  serverVersion: null,
  protocolVersion: null,
  capabilityNames: [],
  promptCount: null,
  resourceCount: null,
  inspectionNotes: [],
  resolvedCommand: '/opt/homebrew/bin/npx',
  environmentSource: 'Login shell PATH',
  authorizationURL: AUTHORIZATION_URL,
});

/// A count from the bundled vocabulary — what a server checked by this build
/// carries (§7.4).
const weight = (estimate) => ({
  estimate,
  method: 'cl100k_base',
  isMeasured: true,
  measuredAt: Date.now() / 1000,
  percentOfContext: (estimate / 200_000) * 100,
  referenceContextWindow: 200_000,
});

/// The pre-vocabulary shape, which is not hypothetical: every server measured
/// by an earlier Kytto still has one of these until it is checked again. The UI
/// has to keep saying "approximately" for it and keep the tilde in the matrix.
const estimatedWeight = (estimate) => ({
  ...weight(estimate),
  method: 'chars/4',
  isMeasured: false,
});

const PATHS = {
  claudeDesktop: '~/Library/Application Support/Claude/claude_desktop_config.json',
  claudeCode: '~/.claude/settings.json',
  cursor: '~/.cursor/mcp.json',
  vsCode: '~/Library/Application Support/Code/User/mcp.json',
  codex: '~/.codex/config.toml',
};

/// Clients with no "disabled" state: switching off there removes the definition,
/// and Kytto parks it so the change is reversible.
const PARKING_CLIENTS = new Set(['cursor', 'vsCode', 'claudeDesktop']);

const world = {
  settings: {
    theme: 'dark',
    tokenWarningThreshold: 20_000,
    // Flip to false to work on the first-run screen.
    hasCompletedOnboarding: true,
    backupRetention: 20,
    hasConfirmedMatrixWrites: false,
    showsCustomSources: true,
    sidebarWidth: 196,
  },
  updateOpened: false,
  // A server already running through the gateway in one client, already
  // narrowed. Both halves are states that occur: the route is how anyone
  // reaches the tool picker, and an existing allow list is what the badge and
  // the pre-ticked boxes have to be able to show (§7.11).
  gatewayRoutes: [
    {
      id: '9f1c2ad4-3e5b-4c77-9f6e-2b8a1d0c4e33',
      serverID: 'filesystem',
      serverName: 'Filesystem',
      clientID: 'codex',
      createdAt: Date.now() / 1000 - 86_400,
      exposedTools: ['list_directory', 'read_file'],
    },
  ],
  // A server that quietly rewrote what it tells the model. The description
  // change is the one that matters and the one that looks like nothing: the
  // tool count is unchanged, so every other surface still reads healthy.
  contractAlerts: [
    {
      serverID: 'filesystem',
      serverName: 'Filesystem',
      changedAt: Date.now() / 1000 - 3600,
      changes: [
        { kind: 'descriptionChanged', severity: 'warning', toolName: 'read_file', summary: 'Model-facing description changed' },
        { kind: 'inputSchemaChanged', severity: 'breaking', toolName: 'write_file', summary: 'Input schema changed' },
      ],
    },
  ],
  // A server whose clients disagree — the state the matrix used to merge away.
  // Two clients on one spelling, a third pinned to an old version with its own
  // token, and Claude Desktop holding an extension that shares the name and can
  // never be rewritten. All four cases in one row on purpose.
  drift: [
    {
      serverID: 'github',
      serverName: 'github',
      fields: ['arguments', 'environmentValues'],
      unwritableClientIDs: [],
      variants: [
        {
          clientIDs: ['claudeCode', 'cursor'],
          commandSummary: 'npx -y @modelcontextprotocol/server-github',
          transport: 'stdio',
          url: null,
          environmentKeys: ['GITHUB_PERSONAL_ACCESS_TOKEN'],
          isBundled: false,
          canBeSource: true,
        },
        {
          clientIDs: ['codex'],
          commandSummary: 'npx -y @modelcontextprotocol/server-github@0.6.2',
          transport: 'stdio',
          url: null,
          environmentKeys: ['GITHUB_PERSONAL_ACCESS_TOKEN'],
          isBundled: false,
          canBeSource: true,
        },
      ],
    },
  ],
  profiles: [
    { id: 'fixture-coding', name: 'Coding', serverIDs: ['filesystem', 'github', 'context7'], tokenBudget: 25_000 },
    { id: 'fixture-minimal', name: 'Minimal', serverIDs: [], tokenBudget: null },
  ],
  servers: [
    server('Affinity', {
      enabledIn: { claudeDesktop: 'enabled', claudeCode: 'absent', cursor: 'absent', vsCode: 'absent', codex: 'absent' },
    }),
    server('Control your Mac', {
      enabledIn: { claudeDesktop: 'enabled', claudeCode: 'absent', cursor: 'absent', vsCode: 'absent', codex: 'absent' },
    }),
    // An ordinary config-file server in two clients at once, which is the case
    // the matrix exists for — and, unlike an extension, one that can travel.
    server('Filesystem', {
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'disabled', claudeCode: 'absent', cursor: 'absent', vsCode: 'absent', codex: 'enabled' },
      health: passingHealth(4, [
        {
          name: 'read_file',
          description: 'Read the contents of a file.',
          inputSchemaJSON: '{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}',
          annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
          tokenCount: 74,
        },
        {
          // The one that pays for the breakdown existing: a single tool eating
          // a third of the server, which no total can tell you.
          name: 'edit_file',
          description:
            'Apply a patch to a file. Accepts a list of edits, each with an exact string to find and its replacement, and applies them in order. Fails without writing anything if any edit does not match exactly once.',
          inputSchemaJSON:
            '{"type":"object","properties":{"path":{"type":"string"},"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["oldText","newText"]}},"dryRun":{"type":"boolean"}},"required":["path","edits"]}',
          annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
          tokenCount: 312,
        },
        {
          name: 'write_file',
          description: 'Write text to a file.',
          inputSchemaJSON: '{"type":"object","properties":{"path":{"type":"string"},"contents":{"type":"string"}},"required":["path","contents"]}',
          annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
          tokenCount: 96,
        },
        { name: 'list_directory', description: 'List the entries of a directory.', inputSchemaJSON: null, annotations: null, tokenCount: 41 },
      ]),
      // Deliberately over the 20k threshold, so the warning styling is visible
      // while developing it.
      tokenWeight: weight(23_400),
    }),
    server('github', {
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-github'],
      env: [{ key: 'GITHUB_PERSONAL_ACCESS_TOKEN', hasValue: true }],
      commandSummary: 'npx -y @modelcontextprotocol/server-github',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'enabled', cursor: 'enabled', vsCode: 'absent', codex: 'disabled' },
      // Never looked up, so MCP Doctor's unpinned-package finding offers
      // "Check latest release" rather than a pin — the first half of that UX.
      provenance: {
        sourceKind: 'npm',
        packageName: '@modelcontextprotocol/server-github',
        sourceURL: null,
        installedVersion: null,
        latestVersion: null,
        latestCheckedAt: null,
        confidence: 'inferred',
        maintenanceState: 'healthy',
      },
      // Last checked by a Kytto that measured whole servers and not tools, which
      // is what every existing install looks like until it is checked again.
      // The breakdown has to degrade to the server's own order here, and the
      // matrix has to keep its tilde.
      health: passingHealth(26, [
        { name: 'create_pull_request', description: 'Open a pull request.', inputSchemaJSON: null, annotations: null },
        { name: 'list_issues', description: 'List issues in a repository.', inputSchemaJSON: null, annotations: { readOnlyHint: true } },
      ]),
      tokenWeight: estimatedWeight(8_200),
    }),
    server('XcodeBuildMCP', {
      command: 'npx',
      args: ['-y', 'xcodebuildmcp@latest', 'mcp'],
      commandSummary: 'npx -y xcodebuildmcp@latest mcp',
      // Checked a moment ago, so the Doctor offers the pin itself rather than
      // the lookup — the other half of the unpinned-package UX.
      provenance: {
        sourceKind: 'npm',
        packageName: 'xcodebuildmcp',
        sourceURL: null,
        installedVersion: null,
        latestVersion: '1.14.1',
        latestCheckedAt: Date.now() / 1000 - 120,
        confidence: 'inferred',
        maintenanceState: 'unhealthy',
      },
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'disabled', cursor: 'absent', vsCode: 'absent', codex: 'absent' },
      // A real failure shape, taken from a real machine: a Homebrew node with a
      // missing dylib. Exactly the case where verbatim stderr is the only useful
      // thing an app can show.
      health: {
        status: 'failed',
        checkedAt: Date.now() / 1000,
        toolCount: null,
        tools: [],
        message: 'The server exited with status 1 before answering.',
        stderr:
          "dyld[31716]: Library not loaded: /opt/homebrew/opt/simdjson/lib/libsimdjson.29.dylib\n  Referenced from: /opt/homebrew/Cellar/node/25.4.0/bin/node\n  Reason: tried: '/opt/homebrew/opt/simdjson/lib/libsimdjson.29.dylib' (no such file)\n",
        durationSeconds: 0.4,
        serverName: null,
        serverVersion: null,
      },
    }),
    server('context7', {
      transport: 'http',
      command: null,
      url: 'https://mcp.context7.com/mcp',
      commandSummary: 'https://mcp.context7.com/mcp',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'enabled', cursor: 'absent', vsCode: 'absent', codex: 'absent' },
    }),
    server('Intercom', {
      command: 'npx',
      args: ['-y', 'mcp-remote', 'https://mcp.intercom.com/mcp'],
      commandSummary: 'npx -y mcp-remote https://mcp.intercom.com/mcp',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'enabled', cursor: 'absent', vsCode: 'absent', codex: 'absent' },
      health: waitingHealth(),
    }),
    // A shipped-alongside-the-client server, taken from a real ~/.codex/config.toml.
    // Nothing marks it as Codex's — it is an ordinary entry in an ordinary
    // config, and the only thing that ties it to Codex is the relative command,
    // which resolves from where Codex runs and nowhere else. The whole of the
    // warning's behaviour hangs off this row: four amber empty cells, and one
    // real off switch in the client it belongs to.
    server('computer-use', {
      command: './Codex Computer Use.app/Contents/SharedSupport/SkyComputerUseClient.app/Contents/MacOS/SkyComputerUseClient',
      args: ['mcp'],
      commandSummary:
        './Codex Computer Use.app/Contents/SharedSupport/SkyComputerUseClient.app/Contents/MacOS/SkyComputerUseClient mcp',
      originKind: 'configFile',
      isBundled: false,
      hasRelativePath: true,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'absent', cursor: 'absent', vsCode: 'absent', codex: 'disabled' },
    }),
    server('demo-tool', {
      command: 'cmd',
      args: ['/c', 'echo', 'demo'],
      commandSummary: 'cmd /c echo demo',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: {
        claudeDesktop: 'absent',
        claudeCode: 'absent',
        cursor: 'absent',
        vsCode: 'absent',
        codex: 'absent',
        [CUSTOM_SOURCE_ID]: 'enabled',
        ...Object.fromEntries(CLAUDE_PROJECTS.map((project) => [project.id, 'enabled'])),
      },
    }),
    // VS Code past its 128-tool cap, which is how people meet it: one server
    // that ships a hundred tools, one ordinary one on top, and a remote nobody
    // has checked — so the count is a floor, and already over.
    server('azure', {
      command: 'npx',
      args: ['-y', '@azure/mcp@latest', 'server', 'start'],
      commandSummary: 'npx -y @azure/mcp@latest server start',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'absent', cursor: 'absent', vsCode: 'enabled', codex: 'absent' },
      health: passingHealth(112, [
        { name: 'storage_account_list', description: 'List storage accounts.', inputSchemaJSON: null, annotations: { readOnlyHint: true } },
        { name: 'group_list', description: 'List resource groups.', inputSchemaJSON: null, annotations: { readOnlyHint: true } },
      ]),
      tokenWeight: weight(31_800),
    }),
    server('playwright', {
      command: 'npx',
      args: ['-y', '@playwright/mcp@latest'],
      commandSummary: 'npx -y @playwright/mcp@latest',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'absent', cursor: 'absent', vsCode: 'enabled', codex: 'absent' },
      health: passingHealth(25, [
        { name: 'browser_navigate', description: 'Navigate to a URL.', inputSchemaJSON: null, annotations: null },
        { name: 'browser_snapshot', description: 'Capture an accessibility snapshot of the page.', inputSchemaJSON: null, annotations: { readOnlyHint: true } },
      ]),
      tokenWeight: weight(6_900),
    }),
    server('github-remote', {
      transport: 'http',
      command: null,
      url: 'https://api.githubcopilot.com/mcp/',
      commandSummary: 'https://api.githubcopilot.com/mcp/',
      originKind: 'configFile',
      isBundled: false,
      enabledIn: { claudeDesktop: 'absent', claudeCode: 'absent', cursor: 'absent', vsCode: 'enabled', codex: 'absent' },
    }),
  ],
  diagnostics: [
    {
      severity: 'warning',
      clientID: 'cursor',
      pathDisplay: '~/.cursor/mcp.json',
      message: 'Cursor is not installed, but a configuration for it still exists.',
    },
  ],
  pendingRestarts: {},
  backups: [],
  secrets: [
    {
      // The case the feature exists for: one token, three files.
      id: 'github_personal_access_token-abc123',
      key: 'GITHUB_PERSONAL_ACCESS_TOKEN',
      maskedValue: 'ghp_••••••1234',
      isInSecretStore: false,
      isShared: true,
      usages: [
        { serverID: 'github', serverName: 'github', clientID: 'claudeCode', pathDisplay: '~/.claude.json' },
        { serverID: 'github', serverName: 'github', clientID: 'cursor', pathDisplay: '~/.cursor/mcp.json' },
      ],
      exposure: [
        {
          kind: 'readableByOthers',
          pathDisplay: '~/.cursor/mcp.json',
          detail: 'Mode 644 — other users on this Mac can read it.',
        },
      ],
    },
    {
      id: 'figma_api_key-def456',
      key: 'FIGMA_API_KEY',
      maskedValue: 'figd_••••••5678',
      isInSecretStore: true,
      isShared: false,
      usages: [
        { serverID: 'figma', serverName: 'figma', clientID: 'cursor', pathDisplay: '~/.cursor/mcp.json' },
      ],
      exposure: [],
    },
  ],
};

/// Mirrors what a real write does: marks the client as needing a restart and
/// leaves a backup behind.
const recordWrite = (clientID) => {
  world.pendingRestarts[clientID] = (world.pendingRestarts[clientID] ?? 0) + 1;
  world.backups.unshift({
    id: `fixture-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`,
    clientID,
    pathDisplay: PATHS[clientID],
    takenAt: Date.now() / 1000,
    byteCount: 1830,
  });
};

/// Mirrors `ClientToolLimit` in the native registry: only documented caps.
const TOOL_LIMITS = {
  vsCode: {
    maxTools: 128,
    pastLimitSummary:
      'VS Code allows at most 128 tools in one chat request. Past that, agent mode ' +
      'refuses the request ("Cannot have more than 128 tools per request") unless ' +
      'the experimental virtual tools setting groups them.',
  },
};

/// Mirrors `ToolBudget.Evaluate`, recomputed per snapshot so a toggle moves it.
const toolBudget = (clientID) => {
  let toolCount = 0;
  let maskedToolCount = 0;
  const unmeasuredServerIDs = [];
  for (const entry of world.servers) {
    if (entry.enabledIn[clientID] !== 'enabled') continue;
    if (entry.health?.status !== 'passed') {
      unmeasuredServerIDs.push(entry.id);
      continue;
    }
    const offered = entry.health.toolCount ?? entry.health.tools.length;
    const route = world.gatewayRoutes.find((candidate) => candidate.serverID === entry.id && candidate.clientID === clientID);
    let visible = offered;
    if (route?.exposedTools) {
      const names = new Set(entry.health.tools.map((tool) => tool.name));
      const exposed = new Set(route.exposedTools);
      visible = names.size >= offered
        ? [...exposed].filter((name) => names.has(name)).length
        : Math.min(exposed.size, offered);
    }
    toolCount += visible;
    maskedToolCount += offered - visible;
  }
  const limit = TOOL_LIMITS[clientID] ?? null;
  const state = !limit
    ? null
    : toolCount > limit.maxTools ? 'over' : toolCount >= limit.maxTools * 0.8 ? 'near' : 'ok';
  return {
    toolCount,
    maskedToolCount,
    unmeasuredServerIDs: unmeasuredServerIDs.sort(),
    limit: limit?.maxTools ?? null,
    state,
    pastLimitSummary: limit?.pastLimitSummary ?? null,
  };
};

/// Mirrors MCP Doctor's `unpinned-package`: present while the fixture server
/// still launches its package without an exact version, gone once it is pinned.
const packageArgument = (target) => {
  const packageName = target?.provenance?.packageName;
  if (!packageName) return null;
  return target.args.find((arg) => arg === packageName || arg.startsWith(`${packageName}@`)) ?? null;
};

const unpinnedPackageFinding = (serverID) => {
  const target = world.servers.find((entry) => entry.id === serverID);
  const argument = packageArgument(target);
  if (!argument || /@\d+\.\d+\.\d+$/.test(argument)) return [];
  const { packageName } = target.provenance;
  const requested = argument === packageName
    ? 'names no version'
    : `asks for the “${argument.slice(packageName.length + 1)}” tag`;
  return [{
    code: 'unpinned-package',
    severity: 'warning',
    title: 'The package version is not pinned',
    detail: `“${argument}” ${requested}, so ${target.command} can start a newer npm release of ${packageName} the next time a client launches it — without anyone reviewing it.`,
    remediation: 'Check the latest release (Kytto asks npm about this package name and nothing else), then preview pinning that exact version in every editable client definition. Pinning fixes this package’s own version; its dependencies still resolve within their declared ranges.',
    action: 'pinPackageVersion',
  }];
};

/// The argument each editable fixture copy would have after a package pin.
const packagePinChanges = (target) => {
  const current = packageArgument(target);
  const { packageName, latestVersion } = target.provenance ?? {};
  if (!current || !latestVersion) return [];
  return Object.entries(target.enabledIn)
    .filter(([clientID, value]) => value !== 'absent' && CLIENTS.some((client) => client.id === clientID && !client.isReadOnly))
    .map(([clientID]) => ({ clientID, current, replacement: `${packageName}@${latestVersion}` }));
};

/// What a registry lookup answers in the harness. No request is made.
const FIXTURE_LATEST = { '@modelcontextprotocol/server-github': '2025.4.8', xcodebuildmcp: '1.14.1' };

const snapshot = () => ({
  clients: CLIENTS.map((client) => ({ ...client, toolBudget: toolBudget(client.id) })),
  servers: structuredClone(world.servers),
  diagnostics: structuredClone(world.diagnostics),
  pendingRestarts: { ...world.pendingRestarts },
  gatewayRoutes: structuredClone(world.gatewayRoutes),
  doctorReports: [
    {
      serverID: 'github',
      serverName: 'github',
      findings: [...unpinnedPackageFinding('github'), {
        code: 'unpinned-command',
        severity: 'info',
        title: 'The executable depends on the client PATH',
        detail: '“npx” resolved to “/opt/homebrew/bin/npx” during the successful check.',
        remediation: 'Preview a change that pins the verified absolute path in every editable client definition.',
        action: 'pinResolvedCommand',
      }],
    },
    {
      serverID: 'xcodebuildmcp',
      serverName: 'XcodeBuildMCP',
      findings: [...unpinnedPackageFinding('xcodebuildmcp'), {
        code: 'executable-not-found',
        severity: 'error',
        title: 'The executable or one of its files was not found',
        detail: 'Kytto could not complete the MCP handshake and tools/list check.',
        remediation: 'Check the command and the runtime installation used by the client.',
        action: 'runHealthCheck',
      }],
    },
    {
      serverID: 'intercom',
      serverName: 'Intercom',
      findings: [{
        code: 'needs-authorization',
        severity: 'info',
        title: 'Waiting for you to authorize it',
        detail: 'The last check reached the server, which asked for an interactive sign-in instead of answering the handshake.',
        remediation: 'Open the authorization page from the server detail, complete the sign-in, then check again.',
        action: 'runHealthCheck',
      }],
    },
    // A server that starts cleanly and passes every other check, which is what
    // makes this the finding worth having: nothing else on any screen would
    // separate it from a healthy one.
    {
      serverID: 'filesystem',
      serverName: 'Filesystem',
      findings: [
        {
          code: 'instructionOverride',
          severity: 'error',
          title: '“read_file”: the description instructs the model',
          detail: 'The description gives the model an instruction instead of describing what the tool does. A tool contract is loaded into context before anything is called, so this runs whether or not you ever use the tool. Found: before using any other tool.',
          remediation: 'Read this tool’s full description and input schema in the server detail before using the server, and check where the server came from. Kytto does not change what a server says about itself.',
          action: null,
        },
        {
          code: 'tool-name-collision',
          severity: 'warning',
          title: 'Another server offers the same tool name',
          detail: 'Switched on together in this client with github, which also offers search.',
          remediation: 'The client shows the model one flat list of tools, so a name claimed twice is resolved by the client rather than by you. Switch one of them off in this client, or keep them in separate profiles.',
          action: null,
        },
      ],
    },
  ],
  contractAlerts: structuredClone(world.contractAlerts),
  drift: structuredClone(world.drift),
});

const profileSnapshot = () => structuredClone(
  [...world.profiles]
    .sort((left, right) => left.name.localeCompare(right.name, undefined, { sensitivity: 'base' }))
    .map((profile) => {
      const members = world.servers.filter((entry) => profile.serverIDs.includes(entry.id));
      const estimatedTokens = members.reduce((sum, entry) => sum + (entry.tokenWeight?.estimate ?? 0), 0);
      const recommendations = [];
      if (profile.tokenBudget && estimatedTokens > profile.tokenBudget) {
        recommendations.push({
          kind: 'overBudget', severity: 'warning',
          summary: `Profile is ~${estimatedTokens - profile.tokenBudget} tokens over its ${profile.tokenBudget}-token budget.`,
          serverIDs: members.filter((entry) => entry.tokenWeight).sort((a, b) => b.tokenWeight.estimate - a.tokenWeight.estimate).map((entry) => entry.id),
        });
      }
      return {
        ...profile,
        analysis: { estimatedTokens, measuredServerCount: members.filter((entry) => entry.tokenWeight).length, totalServerCount: members.length, recommendations },
      };
    }),
);

const validatedProfileName = (name, excludingID = null) => {
  const cleaned = name.trim();
  if (!cleaned) throw Object.assign(new Error('Give the profile a name.'), { code: 'emptyName' });
  if (cleaned.length > 80) {
    throw Object.assign(new Error('Profile names can be at most 80 characters.'), { code: 'nameTooLong' });
  }
  const duplicate = world.profiles.some(
    (entry) => entry.id !== excludingID && entry.name.localeCompare(cleaned, undefined, { sensitivity: 'base' }) === 0,
  );
  if (duplicate) {
    throw Object.assign(new Error(`A profile named "${cleaned}" already exists.`), { code: 'duplicateName' });
  }
  return cleaned;
};

const normalizedProfileServerIDs = (serverIDs) => [
  ...new Set(serverIDs.map((id) => id.trim().toLowerCase()).filter(Boolean)),
].sort();

/**
 * Refuses the drafts native validation refuses, in native's own words.
 *
 * Anything `index.html` rejects and `dev.html` accepts is a hole in the harness:
 * a form fix looks finished here and then fails against the real shell. These
 * mirror `ServerDraft.validate` deliberately — when that changes, this changes.
 */
const validateDraft = (draft) => {
  const invalid = (message) => Object.assign(new Error(message), { code: 'invalid' });

  if (/\s/.test(draft.name.trim())) {
    throw invalid('Server names cannot contain spaces — they are used as keys in the config file.');
  }

  if (draft.transport === 'stdio') {
    if (!(draft.command ?? '').trim()) throw invalid('A stdio server needs a command to run.');
  } else {
    const url = (draft.url ?? '').trim();
    if (!url) throw invalid('An HTTP or SSE server needs a URL.');
    // Parsed rather than pattern-matched, and restricted to the two schemes a
    // client will actually dial.
    let parsed = null;
    try {
      parsed = new URL(url);
    } catch {
      parsed = null;
    }
    if (!parsed || !['http:', 'https:'].includes(parsed.protocol) || !parsed.hostname) {
      throw invalid(`"${url}" is not a valid URL.`);
    }
  }

  for (const entry of draft.env ?? []) {
    if (!entry.key) throw invalid('An environment variable has no name.');
    if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(entry.key)) {
      throw invalid(
        `"${entry.key}" is not a valid environment variable name. `
        + 'Use letters, numbers and underscores, and do not start with a number.',
      );
    }
  }
};

const HANDLERS = {
  'app.info': () => ({
    version: '1.0',
    build: '1',
    capabilities: ['config.read', 'config.write', 'backups', 'gateway.stdio', 'gateway.masking', 'updates', 'skills', 'provenance'],
    platformDisplayName: 'fixtures',
    // Deliberately neither "Keychain" nor "Credential Manager": the harness is
    // not pretending to be either platform, and a screen that reads correctly
    // here reads correctly on both (§3.2).
    secretStoreDisplayName: 'the fixture store',
  }),

  'updates.check': () => ({
    status: 'updateAvailable',
    currentVersion: '1.0',
    latestVersion: '1.0.1',
    checkedAt: Date.now() / 1000,
    releaseNotesURL: 'https://kytto.jakubhecht.sk/changelog.php',
    sha256: UPDATE_SHA256,
  }),

  'updates.openDownload': () => {
    world.updateOpened = true;
    return {};
  },

  'settings.get': () => structuredClone(world.settings),

  // No window to open in the harness. The command still has to answer, so the
  // sidebar's Settings row behaves the same here as in the shell.
  'settings.open': () => ({}),

  'settings.confirmMatrixWrites': () => {
    world.settings.hasConfirmedMatrixWrites = true;
    return HANDLERS['settings.get']();
  },

  'settings.setShowsCustomSources': ({ shown }) => {
    world.settings.showsCustomSources = shown;
    return HANDLERS['settings.get']();
  },

  'settings.setSidebarWidth': ({ width }) => {
    world.settings.sidebarWidth = Math.min(480, Math.max(170, Math.round(Number(width) || 196)));
    return HANDLERS['settings.get']();
  },

  'skills.chooseDirectory': () => ({ sessionID: 'skills-session.fixture', displayName: 'Demo workspace' }),

  'skills.inventory': () => ({
    skills: [
      {
        id: 'skill.folded',
        name: 'Folded fixture',
        description: 'A folded block scalar is shown as its real description.',
        locationDisplay: 'Codex · global · .codex\\skills\\folded\\SKILL.md',
        status: 'ok',
        warnings: [],
        agent: 'codex',
        scope: 'global',
        scopeLabel: 'Codex · global',
        metadataStatus: 'complete',
        isDuplicate: false,
        isConflict: false,
      },
    ],
    warnings: [],
  }),

  'onboarding.complete': () => {
    world.settings.hasCompletedOnboarding = true;
    return HANDLERS['settings.get']();
  },

  'state.get': snapshot,
  'discovery.refresh': snapshot,

  'profiles.list': profileSnapshot,
  'profiles.create': ({ name, serverIDs, tokenBudget }) => {
    world.profiles.push({
      id: `fixture-${Date.now()}`,
      name: validatedProfileName(name),
      serverIDs: normalizedProfileServerIDs(serverIDs),
      tokenBudget: tokenBudget || null,
    });
    return profileSnapshot();
  },
  'profiles.update': ({ profileID, name, serverIDs, tokenBudget }) => {
    const profile = world.profiles.find((entry) => entry.id === profileID);
    if (!profile) throw Object.assign(new Error(`No profile with id ${profileID}.`), { code: 'unknownProfile' });
    Object.assign(profile, {
      name: validatedProfileName(name, profileID),
      serverIDs: normalizedProfileServerIDs(serverIDs),
      tokenBudget: tokenBudget || null,
    });
    return profileSnapshot();
  },
  'profiles.delete': ({ profileID }) => {
    if (!world.profiles.some((entry) => entry.id === profileID)) {
      throw Object.assign(new Error(`No profile with id ${profileID}.`), { code: 'unknownProfile' });
    }
    world.profiles = world.profiles.filter((entry) => entry.id !== profileID);
    return profileSnapshot();
  },
  'profiles.apply': ({ profileID, clientID }) => {
    const profile = world.profiles.find((entry) => entry.id === profileID);
    if (!profile) throw Object.assign(new Error(`No profile with id ${profileID}.`), { code: 'unknownProfile' });
    const desired = new Set(profile.serverIDs);
    let enabledCount = 0;
    let disabledCount = 0;
    for (const entry of world.servers) {
      const shouldEnable = desired.has(entry.id);
      const isEnabled = entry.enabledIn[clientID] === 'enabled';
      if (shouldEnable && !isEnabled) {
        entry.enabledIn[clientID] = 'enabled';
        enabledCount += 1;
        recordWrite(clientID);
      } else if (!shouldEnable && isEnabled) {
        entry.enabledIn[clientID] = 'disabled';
        disabledCount += 1;
        recordWrite(clientID);
      }
    }
    return { profileName: profile.name, clientID, enabledCount, disabledCount, failures: [], state: snapshot() };
  },

  'gateway.preview': ({ serverID, clientID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    if (target.id === 'intercom') {
      target.health = waitingHealth();
    } else if (target.transport !== 'stdio') {
      throw Object.assign(new Error('Gateway mode currently supports stdio servers only.'), { code: 'unsupportedTransport' });
    }
    const routeID = crypto.randomUUID();
    return {
      routeID,
      serverID,
      serverName: target.name,
      clientID,
      pathDisplay: PATHS[clientID],
      formatDisplay: clientID === 'codex' ? 'TOML' : 'JSON',
      directDefinitionPreview: JSON.stringify({
        command: target.command,
        args: target.args,
        env: Object.fromEntries(target.env.map((entry) => [entry.key, '<stored in credentials>'])),
      }, null, 2),
      gatewayDefinitionPreview: JSON.stringify({
        command: '/Applications/KyttoMCP.app/Contents/MacOS/kytto-mcp-proxy',
        args: ['--route', routeID],
      }, null, 2),
      environmentKeys: target.env.map((entry) => entry.key),
    };
  },

  'gateway.enable': ({ serverID, clientID, routeID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    const route = {
      id: routeID,
      serverID,
      serverName: target.name,
      clientID,
      createdAt: Date.now() / 1000,
      // A new route masks nothing: turning on Gateway mode and narrowing what a
      // client sees are two separate decisions (§7.11).
      exposedTools: null,
    };
    world.gatewayRoutes.push(route);
    recordWrite(clientID);
    return { route, backupID: world.backups[0].id, pathDisplay: PATHS[clientID], state: snapshot() };
  },

  'gateway.restore': ({ routeID }) => {
    const route = world.gatewayRoutes.find((entry) => entry.id === routeID);
    if (!route) throw Object.assign(new Error('No such gateway route'), { code: 'unknownRoute' });
    world.gatewayRoutes = world.gatewayRoutes.filter((entry) => entry.id !== routeID);
    recordWrite(route.clientID);
    return { route, backupID: world.backups[0].id, pathDisplay: PATHS[route.clientID], state: snapshot() };
  },

  'gateway.setExposedTools': ({ routeID, toolNames }) => {
    const route = world.gatewayRoutes.find((entry) => entry.id === routeID);
    if (!route) throw Object.assign(new Error('No such gateway route'), { code: 'unknownRoute' });
    // Sorted, like the native store, so the fixture cannot make the UI look
    // stable when the real thing reorders.
    route.exposedTools = toolNames === null ? null : [...new Set(toolNames)].sort();
    // No config file is rewritten, but the client still has to be restarted
    // before it asks for the tool list again.
    world.pendingRestarts[route.clientID] = (world.pendingRestarts[route.clientID] ?? 0) + 1;
    return snapshot();
  },

  'contract.acknowledge': ({ serverID }) => {
    world.contractAlerts = world.contractAlerts.filter((entry) => entry.serverID !== serverID);
    return snapshot();
  },

  'servers.unifyPreview': ({ serverID, sourceClientID }) => {
    const drift = world.drift.find((entry) => entry.serverID === serverID);
    const source = drift?.variants.find((entry) => entry.clientIDs.includes(sourceClientID));
    if (!source) throw Object.assign(new Error('Nothing to unify'), { code: 'noDrift' });
    return {
      serverID,
      serverName: drift.serverName,
      sourceClientID,
      targetClientIDs: drift.variants
        .filter((entry) => entry !== source && !entry.isBundled)
        .flatMap((entry) => entry.clientIDs),
      skippedClientIDs: drift.unwritableClientIDs,
      commandSummary: source.commandSummary,
      url: source.url,
      transport: source.transport,
      environmentKeys: source.environmentKeys,
    };
  },

  'servers.unify': ({ serverID, sourceClientID }) => {
    const drift = world.drift.find((entry) => entry.serverID === serverID);
    const source = drift?.variants.find((entry) => entry.clientIDs.includes(sourceClientID));
    if (!source) throw Object.assign(new Error('Nothing to unify'), { code: 'noDrift' });

    const changed = drift.variants
      .filter((entry) => entry !== source && !entry.isBundled)
      .flatMap((entry) => entry.clientIDs);
    for (const clientID of changed) recordWrite(clientID);
    // The copies now agree, so the row stops reporting a disagreement.
    world.drift = world.drift.filter((entry) => entry.serverID !== serverID);

    return {
      serverName: drift.serverName,
      changed,
      parkedUpdated: [],
      parkedFailures: [],
      requiresRestart: changed.length > 0,
      state: snapshot(),
    };
  },

  'activity.list': () => ({
    totalSessions: 2,
    completedCalls: 3,
    failedCalls: 1,
    averageDurationMilliseconds: 428,
    events: [
      { eventID: 'event-1', sessionID: 'session-2', timestamp: Date.now() / 1000, kind: 'tool.call.completed', serverID: 'github', clientID: 'claudeCode', toolName: 'create_issue', durationMilliseconds: 812, succeeded: false, errorCode: -32001, exitCode: null },
      { eventID: 'event-2', sessionID: 'session-1', timestamp: Date.now() / 1000 - 8, kind: 'tool.call.completed', serverID: 'filesystem', clientID: 'codex', toolName: 'read_file', durationMilliseconds: 44, succeeded: true, errorCode: null, exitCode: null },
      { eventID: 'event-3', sessionID: 'session-1', timestamp: Date.now() / 1000 - 9, kind: 'tool.call.started', serverID: 'filesystem', clientID: 'codex', toolName: 'read_file', durationMilliseconds: null, succeeded: null, errorCode: null, exitCode: null },
    ],
  }),

  'doctor.previewFix': ({ serverID, action }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    if (action === 'pinPackageVersion') {
      const { packageName, latestVersion } = target.provenance ?? {};
      if (!latestVersion) {
        throw Object.assign(
          new Error(`Check the latest release of ${packageName} first. Kytto pins only a version you looked up.`),
          { code: 'appState' },
        );
      }
      const argumentChanges = packagePinChanges(target);
      return {
        serverID, serverName: target.name, currentCommand: target.command, replacementCommand: target.command,
        clientIDs: argumentChanges.map((change) => change.clientID),
        action, packageName, version: latestVersion, argumentChanges,
      };
    }
    return {
      serverID, serverName: target.name, currentCommand: target.command, replacementCommand: '/opt/homebrew/bin/npx',
      clientIDs: Object.entries(target.enabledIn).filter(([, value]) => value !== 'absent').map(([id]) => id),
      action: 'pinResolvedCommand', packageName: null, version: null, argumentChanges: [],
    };
  },

  'doctor.applyFix': ({ serverID, action, version }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    if (action === 'pinPackageVersion') {
      if (!version || version !== target.provenance?.latestVersion) {
        throw Object.assign(
          new Error('The checked version changed after the preview. Kytto did not write anything; preview the pin again.'),
          { code: 'appState' },
        );
      }
      const changes = packagePinChanges(target);
      const [change] = changes;
      target.args = target.args.map((arg) => (arg === change.current ? change.replacement : arg));
      target.commandSummary = [target.command, ...target.args].join(' ');
      const changed = changes.map((entry) => entry.clientID);
      for (const id of changed) recordWrite(id);
      return { serverName: target.name, changed, parkedUpdated: [], parkedFailures: [], requiresRestart: changed.length > 0, state: snapshot() };
    }
    target.command = '/opt/homebrew/bin/npx';
    target.commandSummary = [target.command, ...target.args].join(' ');
    const changed = Object.entries(target.enabledIn).filter(([, value]) => value !== 'absent').map(([id]) => id);
    for (const id of changed) recordWrite(id);
    return { serverName: target.name, changed, parkedUpdated: [], parkedFailures: [], requiresRestart: true, state: snapshot() };
  },

  'provenance.checkLatest': ({ serverID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    const packageName = target.provenance?.packageName;
    if (!packageName) {
      throw Object.assign(new Error(`Kytto could not identify a package name for "${target.name}".`), { code: 'provenanceCheck' });
    }
    target.provenance = {
      ...target.provenance,
      latestVersion: FIXTURE_LATEST[packageName] ?? '1.0.0',
      latestCheckedAt: Date.now() / 1000,
    };
    return { provenance: structuredClone(target.provenance), state: snapshot() };
  },

  'clipboard.writeText': () => ({}),

  'backups.list': () => structuredClone(world.backups),

  'health.check': ({ serverID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });

    if (target.transport !== 'stdio') {
      target.health = {
        status: 'unsupported',
        checkedAt: Date.now() / 1000,
        toolCount: null,
        tools: [],
        message: `Kytto cannot check remote servers yet — this one is reached over ${target.transport}.`,
        stderr: null,
        durationSeconds: 0,
        serverName: null,
        serverVersion: null,
      };
    } else {
      target.health = passingHealth(7);
      target.tokenWeight = weight(3_100 + Math.floor(Math.random() * 4000));
    }
    return { health: target.health, state: snapshot() };
  },

  'health.checkAll': () => {
    for (const entry of world.servers) {
      if (entry.transport !== 'stdio' ||
          entry.health?.status === 'failed' ||
          entry.health?.status === 'needsAuthorization') continue;
      entry.health = passingHealth(7);
      entry.tokenWeight = entry.tokenWeight ?? weight(2_400);
    }
    return snapshot();
  },

  'health.openAuthorization': ({ serverID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (target?.health?.status !== 'needsAuthorization' || !target.health.authorizationURL) {
      throw Object.assign(new Error(
        'No authorization page is recorded for this server. Check it again to capture one.'),
      { code: 'notFound' });
    }
    globalThis.window?.open(target.health.authorizationURL, '_blank', 'noopener');
    return {};
  },

  'secrets.list': () => structuredClone(world.secrets),

  'secrets.reveal': ({ secretID }) => ({
    value: secretID.startsWith('github') ? 'ghp_exampletokenvalue1234' : 'figd_examplekeyvalue5678',
  }),

  'secrets.rotate': ({ secretID }) => {
    const secret = world.secrets.find((entry) => entry.id === secretID);
    for (const usage of secret.usages) recordWrite(usage.clientID);
    return {
      key: secret.key,
      updatedCount: secret.usages.length,
      clients: [...new Set(secret.usages.map((usage) => usage.clientID))],
      state: snapshot(),
    };
  },

  'secrets.adopt': ({ secretID }) => {
    const secret = world.secrets.find((entry) => entry.id === secretID);
    if (secret) secret.isInSecretStore = true;
    return structuredClone(world.secrets);
  },

  'secrets.forget': ({ secretID }) => {
    const secret = world.secrets.find((entry) => entry.id === secretID);
    if (secret) secret.isInSecretStore = false;
    return structuredClone(world.secrets);
  },

  'secrets.restrictPermissions': ({ clientID }) => {
    for (const secret of world.secrets) {
      secret.exposure = secret.exposure.filter(
        (item) =>
          !(item.kind === 'readableByOthers' && secret.usages.some((usage) => usage.clientID === clientID)),
      );
    }
    return structuredClone(world.secrets);
  },

  'catalog.list': () => [
    {
      id: 'filesystem',
      name: 'filesystem',
      displayName: 'Filesystem',
      description: 'Read, write and search files in directories you explicitly allow.',
      transport: 'stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-filesystem', '{{directory}}'],
      url: null,
      placeholders: [
        { token: '{{directory}}', label: 'Directory to allow', example: '/Users/you/Projects' },
      ],
      env: [],
      requires: 'Node.js',
      homepage: '',
    },
    {
      id: 'memory',
      name: 'memory',
      displayName: 'Memory',
      description: 'A knowledge graph the model can write to and recall across conversations.',
      transport: 'stdio',
      command: 'npx',
      args: ['-y', '@modelcontextprotocol/server-memory'],
      url: null,
      placeholders: [],
      env: [],
      requires: 'Node.js',
      homepage: '',
    },
    {
      id: 'firecrawl',
      name: 'firecrawl',
      displayName: 'Firecrawl',
      description: 'Scrape, crawl and search the web through the Firecrawl API.',
      transport: 'stdio',
      command: 'npx',
      args: ['-y', 'firecrawl-mcp'],
      url: null,
      placeholders: [],
      env: [{ key: 'FIRECRAWL_API_KEY', required: true, hint: 'From firecrawl.dev.' }],
      requires: 'Node.js',
      homepage: '',
    },
  ],

  'servers.create': ({ draft, clientIDs }) => {
    if (!draft.name.trim()) throw Object.assign(new Error('Give the server a name.'), { code: 'invalid' });
    if (!Array.isArray(clientIDs) || clientIDs.length === 0) {
      throw Object.assign(new Error('Choose at least one client to add this server to.'), { code: 'badArgument' });
    }
    if (world.servers.some((entry) => entry.id === draft.name.toLowerCase())) {
      throw Object.assign(new Error(`"${draft.name}" already exists.`), { code: 'invalid' });
    }
    validateDraft(draft);

    const enabledIn = { claudeDesktop: 'absent', claudeCode: 'absent', cursor: 'absent', vsCode: 'absent' };
    for (const id of clientIDs) enabledIn[id] = 'enabled';

    world.servers.push(
      server(draft.name, {
        transport: draft.transport,
        command: draft.command || null,
        args: draft.args ?? [],
        env: (draft.env ?? []).map((entry) => ({ key: entry.key, hasValue: Boolean(entry.value) })),
        url: draft.url || null,
        commandSummary:
          draft.transport === 'stdio' ? [draft.command, ...(draft.args ?? [])].join(' ') : draft.url,
        originKind: 'configFile',
        isBundled: false,
        enabledIn,
      }),
    );
    for (const id of clientIDs) recordWrite(id);
    return { serverName: draft.name, changed: clientIDs, requiresRestart: true, state: snapshot() };
  },

  'servers.update': ({ serverID, draft }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    validateDraft(draft);

    const changed = Object.entries(target.enabledIn)
      .filter(([, value]) => value !== 'absent')
      .map(([id]) => id);

    Object.assign(target, {
      name: draft.name,
      id: draft.name.toLowerCase(),
      transport: draft.transport,
      command: draft.command || null,
      args: draft.args ?? [],
      url: draft.url || null,
      commandSummary:
        draft.transport === 'stdio' ? [draft.command, ...(draft.args ?? [])].join(' ') : draft.url,
    });
    for (const id of changed) recordWrite(id);
    return { serverName: draft.name, changed, requiresRestart: true, state: snapshot() };
  },

  'servers.delete': ({ serverID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    const changed = Object.entries(target.enabledIn)
      .filter(([, value]) => value !== 'absent')
      .map(([id]) => id);
    world.servers = world.servers.filter((entry) => entry.id !== serverID);
    for (const id of changed) recordWrite(id);
    return { serverName: target.name, changed, requiresRestart: true, state: snapshot() };
  },

  // Unlike setEnabled(false), this is the cell going back to `absent` — the
  // state the harness could not otherwise reach, which is the whole point of it
  // being a separate command.
  'servers.removeFromClient': ({ serverID, clientID }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });
    if ((target.enabledIn[clientID] ?? 'absent') === 'absent') {
      throw Object.assign(new Error(`“${target.name}” is not in that client.`), { code: 'notFound' });
    }

    target.enabledIn[clientID] = 'absent';
    // The last client holding it takes the row with it.
    if (!Object.values(target.enabledIn).some((value) => value !== 'absent')) {
      world.servers = world.servers.filter((entry) => entry.id !== serverID);
    }
    recordWrite(clientID);

    return { serverName: target.name, changed: [clientID], requiresRestart: true, state: snapshot() };
  },

  'servers.setEnabled': ({ serverID, clientID, enabled }) => {
    const target = world.servers.find((entry) => entry.id === serverID);
    if (!target) throw Object.assign(new Error('No such server'), { code: 'unknownServer' });

    const wasAbsent = target.enabledIn[clientID] === 'absent';
    const wasParked = !enabled && PARKING_CLIENTS.has(clientID);
    target.enabledIn[clientID] = enabled ? 'enabled' : wasParked ? 'disabled' : 'disabled';

    recordWrite(clientID);

    return {
      serverName: target.name,
      clientID,
      enabled,
      requiresRestart: true,
      wasParked: wasParked && !wasAbsent,
      backupID: world.backups[0].id,
      pathDisplay: PATHS[clientID],
      state: snapshot(),
    };
  },

  'restarts.acknowledge': ({ clientID }) => {
    if (clientID) delete world.pendingRestarts[clientID];
    else world.pendingRestarts = {};
    return snapshot();
  },

  'backups.restore': ({ backupID }) => {
    world.backups = world.backups.filter((entry) => entry.id !== backupID);
    return snapshot();
  },
};

export function install() {
  globalThis.__kyttoStub = async (command, payload) => {
    const handler = HANDLERS[command];
    if (!handler) {
      throw Object.assign(new Error(`No fixture for ${command}`), { code: 'unknownCommand' });
    }
    // A small delay so loading and busy states are visible while developing them.
    await new Promise((resolve) => setTimeout(resolve, 60));
    return handler(payload ?? {});
  };
}
