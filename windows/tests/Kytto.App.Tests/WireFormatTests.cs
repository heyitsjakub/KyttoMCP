using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core.Health;

namespace Kytto.App.Tests;

/// <summary>
/// What crosses the boundary, spelled exactly as the web layer already reads it.
/// </summary>
/// <remarks>
/// <para>
/// The macOS build encodes these with Swift's <c>JSONEncoder</c>, which writes
/// property names verbatim: <c>serverID</c>, <c>pathDisplay</c>, <c>isInSecretStore</c>.
/// This port reproduces that by naming its properties in PascalCase and letting a
/// camelCase policy lower the first letter — which works, and would stop working
/// silently the first time somebody renames a property or the policy changes.
/// </para>
/// <para>
/// A wrong key here does not throw. The UI simply reads <c>undefined</c> and
/// renders a blank cell, which is the failure mode worth a test.
/// </para>
/// </remarks>
public class WireFormatTests
{
    private static JsonElement Encode<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Envelope.Json);

    private static IEnumerable<string> KeysOf(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    [Fact]
    public void AnIdKeepsItsAcronymCase()
    {
        var encoded = Encode(new ToggleResultDto(
            ServerName: "github",
            ClientID: "cursor",
            Enabled: false,
            RequiresRestart: true,
            WasParked: true,
            BackupID: "mcp.json.bak",
            PathDisplay: @"%USERPROFILE%\.cursor\mcp.json",
            State: EmptyState));

        Assert.Equal(
            ["serverName", "clientID", "enabled", "requiresRestart", "wasParked",
             "backupID", "pathDisplay", "state"],
            KeysOf(encoded));
    }

    [Fact]
    public void ServerCarriesEveryFieldTheMatrixReads()
    {
        var encoded = Encode(new ServerDto(
            Id: "github",
            Name: "github",
            Transport: "stdio",
            CommandSummary: "npx -y server-github",
            Command: "npx",
            Args: ["-y"],
            Env: [new EnvEntryDto("TOKEN", HasValue: true)],
            Url: null,
            EnabledIn: new Dictionary<string, string> { ["cursor"] = "enabled" },
            OriginKind: "configFile",
            IsBundled: false,
            HasRelativePath: false,
            Health: null,
            TokenWeight: null));

        Assert.Equal(
            ["id", "name", "transport", "commandSummary", "command", "args", "env",
             "url", "enabledIn", "originKind", "isBundled", "hasRelativePath",
             "health", "tokenWeight", "provenance"],
            KeysOf(encoded));
    }

    /// <summary>
    /// The one rule that matters more than spelling: a value never crosses, only
    /// the fact that there is one (§6).
    /// </summary>
    [Fact]
    public void AnEnvEntryCarriesAFlagAndNeverAValue()
    {
        var encoded = Encode(new EnvEntryDto("GITHUB_TOKEN", HasValue: true));
        Assert.Equal(["key", "hasValue"], KeysOf(encoded));
        Assert.DoesNotContain("value", KeysOf(encoded), StringComparer.Ordinal);
    }

    /// <summary>
    /// Client ids are the opaque handles the web layer sends back, so they must
    /// survive being dictionary keys untouched.
    /// </summary>
    [Fact]
    public void DictionaryKeysAreNotRewrittenByThePolicy()
    {
        var encoded = Encode(new StateDto(
            Clients: [],
            Servers: [],
            Diagnostics: [],
            PendingRestarts: new Dictionary<string, int> { ["claudeDesktop"] = 2 },
            GatewayRoutes: [],
            DoctorReports: [],
            ContractAlerts: [],
            Drift: []));

        Assert.Equal(2, encoded.GetProperty("pendingRestarts").GetProperty("claudeDesktop").GetInt32());
    }

    /// <summary>
    /// Drift reports which environment keys differ and never what they are set to —
    /// the one field that describes something it deliberately does not carry (§6).
    /// </summary>
    [Fact]
    public void DriftNamesEnvironmentKeysAndNeverTheirValues()
    {
        var encoded = Encode(new ServerDriftDto(
            ServerID: "github",
            ServerName: "github",
            Fields: ["arguments", "environmentValues"],
            Variants:
            [
                new DriftVariantDto(
                    ClientIDs: ["claudeCode", "cursor"],
                    CommandSummary: "npx -y @modelcontextprotocol/server-github",
                    Transport: "stdio",
                    Url: null,
                    EnvironmentKeys: ["GITHUB_PERSONAL_ACCESS_TOKEN"],
                    IsBundled: false,
                    CanBeSource: true),
            ],
            UnwritableClientIDs: []));

        Assert.Equal(
            ["serverID", "serverName", "fields", "variants", "unwritableClientIDs"],
            KeysOf(encoded));
        Assert.Equal(
            ["clientIDs", "commandSummary", "transport", "url", "environmentKeys",
             "isBundled", "canBeSource"],
            KeysOf(encoded.GetProperty("variants")[0]));
    }

    [Fact]
    public void BackupTimestampsAreSecondsSinceTheEpoch()
    {
        var encoded = Encode(new BackupDto(
            Id: "mcp.json.bak",
            ClientID: "cursor",
            PathDisplay: @"%USERPROFILE%\.cursor\mcp.json",
            TakenAt: 1785534295.1,
            ByteCount: 1830));

        Assert.Equal(["id", "clientID", "pathDisplay", "takenAt", "byteCount"], KeysOf(encoded));
        // A number, not an ISO string: docs/ipc.md says the UI formats it itself.
        Assert.Equal(JsonValueKind.Number, encoded.GetProperty("takenAt").ValueKind);
    }

    [Fact]
    public void HealthKeepsStderrVerbatimAndUnabbreviated()
    {
        const string Stderr = "dyld[36368]: Library not loaded: @rpath/libnode.dylib\n  Reason: no such file";
        var encoded = Encode(new HealthDto(
            Status: "failed",
            CheckedAt: 1785534295.1,
            ToolCount: null,
            Tools: [],
            Message: "The server exited before answering.",
            Stderr: Stderr,
            DurationSeconds: 1.2,
            ServerName: null,
            ServerVersion: null,
            ProtocolVersion: null,
            CapabilityNames: [],
            PromptCount: null,
            ResourceCount: null,
            InspectionNotes: [],
            ResolvedCommand: null,
            EnvironmentSource: null,
            AuthorizationURL: "https://auth.example.com/oauth/authorize"));

        Assert.Equal(
            ["status", "checkedAt", "toolCount", "tools", "message", "stderr",
             "durationSeconds", "serverName", "serverVersion", "protocolVersion",
             "capabilityNames", "promptCount", "resourceCount", "inspectionNotes",
             "resolvedCommand", "environmentSource", "authorizationURL"],
            KeysOf(encoded));
        Assert.Equal(Stderr, encoded.GetProperty("stderr").GetString());
    }

    [Fact]
    public void ProfilesKeepBothIdAcronymsOnTheWire()
    {
        var encoded = Encode(new ProfileDto(
            "p1",
            "Coding",
            ["github"],
            15_000,
            new ContextProfileAnalysisDto(12_000, 1, 1, [])));
        Assert.Equal(["id", "name", "serverIDs", "tokenBudget", "analysis"], KeysOf(encoded));

        var payload = JsonSerializer.Deserialize<ApplyProfilePayload>(
            """{ "profileID": "p1", "clientID": "cursor" }""", Envelope.Json);
        Assert.NotNull(payload);
        Assert.Equal("p1", payload.ProfileID);
        Assert.Equal("cursor", payload.ClientID);
    }

    [Fact]
    public void GatewayPreviewKeepsOpaqueIdsAndRedactedDefinitions()
    {
        var encoded = Encode(new GatewayMigrationPreviewDto(
            "route", "github", "GitHub", "cursor", @"%USERPROFILE%\.cursor\mcp.json",
            "JSON", "{ redacted }", "{ gateway }", ["GITHUB_TOKEN"]));

        Assert.Equal(
            ["routeID", "serverID", "serverName", "clientID", "pathDisplay", "formatDisplay",
             "directDefinitionPreview", "gatewayDefinitionPreview", "environmentKeys"],
            KeysOf(encoded));
    }

    [Fact]
    public void ActivityCarriesMetadataAndNeverRequestPayloadIdentity()
    {
        var encoded = Encode(new GatewayEventDto(
            "event", "session", 1785534295.1, "tool.call.completed", "github", "cursor",
            "create_issue", 120, false, -32001, null));

        Assert.Equal(
            ["eventID", "sessionID", "timestamp", "kind", "serverID", "clientID",
             "toolName", "durationMilliseconds", "succeeded", "errorCode", "exitCode"],
            KeysOf(encoded));
        Assert.DoesNotContain("requestID", KeysOf(encoded), StringComparer.Ordinal);
    }

    [Fact]
    public void NullsAreWrittenRatherThanOmitted()
    {
        // The UI reads `server.url ?? ''`, which works either way — but a key that
        // sometimes exists is a shape the web layer would have to defend against.
        var encoded = Encode(new SettingsDto("system", 20_000, true, 20, false, true, 196));
        Assert.Equal(
            ["theme", "tokenWarningThreshold", "hasCompletedOnboarding", "backupRetention",
             "hasConfirmedMatrixWrites", "showsCustomSources", "sidebarWidth"],
            KeysOf(encoded));
    }

    [Fact]
    public void HealthDtoRendersStructuredFailureInsteadOfItsStaleSnapshot()
    {
        var health = new HealthResult(
            HealthStatus.Failed,
            DateTimeOffset.UnixEpoch,
            null,
            [],
            "stale words",
            null,
            0.1,
            null,
            null)
        {
            Failure = HealthFailureReason.TimedOut(1.2),
        };

        var encoded = Encode(health.ToDto());
        Assert.Equal(
            "The server did not answer within 2 seconds.",
            encoded.GetProperty("message").GetString());
    }

    [Fact]
    public void ProjectClientShortNameIsNullableAndExplicitOnTheWire()
    {
        var encoded = Encode(new ClientDto(
            "custom.00000000-0000-0000-0000-000000000001",
            "Claude Code · …\\shop",
            "…\\shop",
            "client-claude-code",
            "ready",
            @"C:\Users\you\.claude.json",
            "JSON",
            "Read only.",
            1,
            "Workspace source.",
            true,
            "workspace",
            @"C:\Users\you\Projects\shop"));

        Assert.Equal("…\\shop", encoded.GetProperty("shortName").GetString());
        Assert.Equal("Claude Code · …\\shop", encoded.GetProperty("displayName").GetString());
    }

    [Fact]
    public void TheEnvelopeWrapsDataUnderItsDeclaredType()
    {
        var json = Envelope.Success(
            new AppInfoDto("1.0", "1", ["config.read"], "Windows", "Credential Manager"));
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(
            ["version", "build", "capabilities", "platformDisplayName", "secretStoreDisplayName"],
            KeysOf(data));
    }

    [Fact]
    public void UpdateCheckKeepsTheSharedWireShape()
    {
        var encoded = Encode(new UpdateCheckDto(
            Status: "updateAvailable",
            CurrentVersion: "1.0.4",
            LatestVersion: "1.0.5",
            CheckedAt: 1800000000,
            ReleaseNotesURL: "https://kytto.jakubhecht.sk/changelog.php",
            Sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));

        Assert.Equal(
            ["status", "currentVersion", "latestVersion", "checkedAt", "releaseNotesURL", "sha256"],
            KeysOf(encoded));
    }

    /// <summary>
    /// The web layer names the credential store from what native reports, so the
    /// shared secrets screen never has a macOS product name written into it (§3.2).
    /// </summary>
    [Fact]
    public void TheCredentialStoreIsNamedByNativeAndNeverSpelledKeychain()
    {
        var json = Envelope.Success(
            new AppInfoDto("1.0", "1", [], "Windows", "Credential Manager"));

        Assert.Contains("\"secretStoreDisplayName\":\"Credential Manager\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("eychain", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The field says what Kytto holds, not which OS product holds it — the same
    /// key is read by a page that also runs on a Mac.
    /// </summary>
    [Fact]
    public void ASecretSaysWhetherItIsStoredWithoutNamingThePlatformsStore()
    {
        var encoded = Encode(new SecretRecordDto(
            Id: "github_token-9f3c",
            Key: "GITHUB_TOKEN",
            MaskedValue: "ghp_••••••1234",
            Usages: [],
            IsInSecretStore: true,
            IsShared: false,
            Exposure: []));

        Assert.Equal(
            ["id", "key", "maskedValue", "usages", "isInSecretStore", "isShared", "exposure"],
            KeysOf(encoded));
    }

    [Fact]
    public void AFailureIsDataWithACodeTheUiCanBranchOn()
    {
        var json = Envelope.Failure(new BadArgumentException("Unknown client wat."));
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        var error = document.RootElement.GetProperty("error");
        // Nothing in the UI branches on this, but anonymous diagnostics report the
        // code and nothing else, so a bad payload has to be distinguishable from a
        // genuine internal fault.
        Assert.Equal("badArgument", error.GetProperty("code").GetString());
        Assert.Equal("Unknown client wat.", error.GetProperty("message").GetString());
    }

    /// <summary>
    /// The payloads travel the other way, so the same spelling has to decode.
    /// </summary>
    [Fact]
    public void PayloadsDecodeFromWhatTheWebLayerSends()
    {
        const string Sent = """
            { "draft": { "name": "memory", "transport": "stdio", "command": "npx",
                         "args": ["-y", "server-memory"],
                         "env": [{ "key": "TOKEN", "value": null }], "url": "" },
              "clientIDs": ["cursor", "claudeCode"] }
            """;

        var payload = JsonSerializer.Deserialize<CreateServerPayload>(Sent, Envelope.Json);
        Assert.NotNull(payload);
        Assert.Equal(["cursor", "claudeCode"], payload.ClientIDs);

        var draft = payload.Draft.MakeDraft();
        Assert.Equal("memory", draft.Name);
        Assert.Equal("npx", draft.Command);
        Assert.Equal(["-y", "server-memory"], draft.Args);
        // Null means "keep whatever is already in the config" — an empty string
        // would overwrite a real token (§6).
        Assert.Null(draft.Env[0].Value);
    }

    private static StateDto EmptyState => new(
        [], [], [], new Dictionary<string, int>(), [], [], [], []);
}
