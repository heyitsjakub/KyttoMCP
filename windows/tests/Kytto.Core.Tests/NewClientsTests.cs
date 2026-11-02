using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Json;
using Kytto.Core.Model;
using Kytto.Core.Portable;
using Kytto.Core.Secrets;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

/// <summary>
/// Antigravity, Devin Desktop and OpenCode through the real pipeline.
/// </summary>
/// <remarks>
/// The three share Codex's kind of off switch — a flag on the entry — but in JSON, and
/// two of them spell a definition differently from the first five clients. What these
/// protect is that a copy between clients means the same thing on both sides: an endpoint
/// under the key the target reads, no <c>type</c> where it is rejected, and no on/off
/// state carried from the source client.
/// </remarks>
internal static class NewClientFixtures
{
    public const string AntigravityPath = @".gemini\config\mcp_config.json";
    public const string DevinPath = @"AppData\Roaming\devin\mcp_config.json";
    public const string OpenCodePath = @".config\opencode\opencode.json";
    public const string CursorPath = @".cursor\mcp.json";

    public const string AntigravityConfig = """
        {
          "mcpServers": {
            "sqlite": {
              "command": "node",
              "args": ["/opt/sqlite-mcp.js"],
              "env": { "DB_PATH": "/var/data/app.db" },
              "disabledTools": ["drop_table"]
            },
            "docs": {
              "serverUrl": "https://docs.example.com/mcp",
              "headers": { "Authorization": "Bearer abc" },
              "timeoutSeconds": 600
            },
            "paused": {
              "command": "uvx",
              "args": ["mcp-server-time"],
              "disabled": true
            }
          }
        }
        """;

    public const string OpenCodeConfig = """
        {
          "$schema": "https://opencode.ai/config.json",
          // Personal settings.
          "theme": "opencode",
          "mcp": {
            "github": {
              "type": "local",
              "command": ["npx", "-y", "@modelcontextprotocol/server-github"],
              "environment": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_examplevalue1234567890" },
              "timeout": 20000,
            },
            "context": {
              "type": "remote",
              "url": "https://mcp.example.com/mcp",
              "enabled": false
            }
          },
          "tools": { "github_delete_*": false }
        }
        """;

    public const string CursorConfig = """
        {
          "mcpServers": {
            "fetch": {
              "command": "uvx",
              "args": ["mcp-server-fetch"],
              "env": { "FETCH_LIMIT": "10" }
            },
            "remote": {
              "type": "http",
              "url": "https://remote.example.com/mcp",
              "headers": { "X-Team": "kytto" }
            },
            "legacy": {
              "url": "https://old.example.com/sse"
            }
          }
        }
        """;

    public static JsonNode? Entry(ToggleHarness harness, string path, string serversKey, string name) =>
        JsonDocument.Parse(harness.Text(path)).ValueAt(serversKey, name);

    public static string[] Strings(JsonNode? array) =>
        array?.Elements?.Select(element => element.StringValue).OfType<string>().ToArray() ?? [];
}

public class AntigravityTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(NewClientFixtures.AntigravityConfig, NewClientFixtures.AntigravityPath);
        harness.Write(NewClientFixtures.CursorConfig, NewClientFixtures.CursorPath);
        return harness;
    }

    [Fact]
    public void ServersAreFoundServerUrlIsTheEndpointAndDisabledDecidesWhatIsOn()
    {
        using var harness = MakeHarness();

        var client = harness.Discover().Clients.First(candidate => candidate.Id.BuiltIn == ClientId.Antigravity);
        Assert.True(client.ConfigExists);
        Assert.Equal(3, client.ServerCount);

        var docs = harness.Server("docs");
        Assert.Equal(Transport.Http, docs.Transport);
        Assert.Equal("https://docs.example.com/mcp", docs.Url);
        Assert.Equal(Enablement.Enabled, docs.EnabledIn[ClientId.Antigravity]);
        Assert.Equal(Enablement.Disabled, harness.Server("paused").EnabledIn[ClientId.Antigravity]);
        Assert.Equal("DB_PATH", harness.Server("sqlite").Env[0].Key);
    }

    [Fact]
    public void SwitchingOffWritesDisabledTrueAndKeepsEverythingElse()
    {
        using var harness = MakeHarness();

        var result = harness.Toggles.SetEnabled(false, harness.Server("sqlite"), ClientId.Antigravity);
        Assert.False(result.WasParked);
        Assert.Empty(harness.ParkStore.All());
        var off = JsonDocument.Parse(harness.Text(NewClientFixtures.AntigravityPath));
        Assert.True(off.ValueAt("mcpServers", "sqlite", "disabled")?.BoolValue);
        Assert.Single(off.ValueAt("mcpServers", "sqlite", "disabledTools")!.Elements!);
        Assert.Equal(Enablement.Disabled, harness.Server("sqlite").EnabledIn[ClientId.Antigravity]);

        // Back on, the flag goes away, which is how the entry looked before.
        harness.Toggles.SetEnabled(true, harness.Server("sqlite"), ClientId.Antigravity);
        Assert.Equal(NewClientFixtures.AntigravityConfig, harness.Text(NewClientFixtures.AntigravityPath));

        // And the server that was already off comes on the same way.
        harness.Toggles.SetEnabled(true, harness.Server("paused"), ClientId.Antigravity);
        Assert.Null(JsonDocument.Parse(harness.Text(NewClientFixtures.AntigravityPath))
            .ValueAt("mcpServers", "paused", "disabled"));
    }

    [Fact]
    public void ASwitchToTheStateAServerIsAlreadyInWritesNothing()
    {
        using var harness = MakeHarness();

        var result = harness.Toggles.SetEnabled(false, harness.Server("paused"), ClientId.Antigravity);
        Assert.Null(result.BackupID);
        Assert.Equal(NewClientFixtures.AntigravityConfig, harness.Text(NewClientFixtures.AntigravityPath));
    }

    [Fact]
    public void ACursorServerArrivesWithServerUrlNoTypeAndItsHeaders()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(true, harness.Server("remote"), ClientId.Antigravity);
        var entry = NewClientFixtures.Entry(harness, NewClientFixtures.AntigravityPath, "mcpServers", "remote")!;
        Assert.Equal("https://remote.example.com/mcp", entry["serverUrl"]?.StringValue);
        Assert.Null(entry["url"]);
        Assert.Null(entry["type"]);
        Assert.Equal("kytto", entry["headers"]?["X-Team"]?.StringValue);
        Assert.Equal(Enablement.Enabled, harness.Server("remote").EnabledIn[ClientId.Antigravity]);
    }

    [Fact]
    public void AnAntigravityServerArrivesInCursorWithoutAntigravitysOwnState()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(true, harness.Server("docs"), ClientId.Cursor);
        harness.Toggles.SetEnabled(true, harness.Server("paused"), ClientId.Cursor);
        var document = JsonDocument.Parse(harness.Text(NewClientFixtures.CursorPath));
        var docs = document.ValueAt("mcpServers", "docs")!;
        Assert.Equal("https://docs.example.com/mcp", docs["url"]?.StringValue);
        Assert.Null(docs["serverUrl"]);
        Assert.Equal("http", docs["type"]?.StringValue);
        Assert.NotNull(docs["headers"]);
        Assert.Null(document.ValueAt("mcpServers", "paused", "disabled"));
        Assert.Equal(Enablement.Enabled, harness.Server("paused").EnabledIn[ClientId.Cursor]);
    }

    [Fact]
    public void TheLegacySseTransportIsRefusedRatherThanWritten()
    {
        using var harness = MakeHarness();

        var legacy = harness.Server("legacy");
        Assert.Equal(Transport.Sse, legacy.Transport);
        var refused = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, legacy, ClientId.Antigravity));
        Assert.Contains("the legacy HTTP+SSE transport", refused.Message, StringComparison.Ordinal);

        var draft = new ServerDraft { Name = "old", Transport = Transport.Sse, Url = "https://old.example.com/sse" };
        var error = Assert.Throws<AuthoringException>(() =>
            harness.Authoring().Create(draft, [ClientId.Antigravity], harness.Discover().Servers));
        Assert.Contains("Antigravity only connects", error.Message, StringComparison.Ordinal);
        Assert.Equal(NewClientFixtures.AntigravityConfig, harness.Text(NewClientFixtures.AntigravityPath));
    }

    [Fact]
    public void AddingAndEditingARemoteServerKeepsTheEndpointKeyTheEntryUses()
    {
        using var harness = MakeHarness();

        var draft = new ServerDraft
        {
            Name = "search",
            Transport = Transport.Http,
            Url = "https://search.example.com/mcp",
        };
        harness.Authoring().Create(draft, [ClientId.Antigravity], harness.Discover().Servers);
        var added = NewClientFixtures.Entry(harness, NewClientFixtures.AntigravityPath, "mcpServers", "search")!;
        Assert.Equal("https://search.example.com/mcp", added["serverUrl"]?.StringValue);
        Assert.Null(added["type"]);

        // An entry written by hand with `url` stays on `url`.
        var text = harness.Text(NewClientFixtures.AntigravityPath)
            .Replace("\"serverUrl\": \"https://docs", "\"url\": \"https://docs", StringComparison.Ordinal);
        harness.Write(text, NewClientFixtures.AntigravityPath);
        var docs = harness.Server("docs");
        var edit = ServerDraft.Editing(docs) with { Url = "https://docs.example.com/v2/mcp" };
        harness.Authoring().Update(edit, "docs", docs, harness.Discover().Servers);
        var edited = NewClientFixtures.Entry(harness, NewClientFixtures.AntigravityPath, "mcpServers", "docs")!;
        Assert.Equal("https://docs.example.com/v2/mcp", edited["url"]?.StringValue);
        Assert.Null(edited["serverUrl"]);
        Assert.NotNull(edited["timeoutSeconds"]);
    }

    [Fact]
    public void ADefinitionRenderedForAntigravityCarriesNoTypeAndNoStateKeys()
    {
        var rendered = JsonDocument.Parse(
            new ServerDraft { Name = "x", Command = "uvx", Args = ["a"] }
                .Definition("", "  ", "\n", ServerSchema.Antigravity));
        Assert.Equal("uvx", rendered.Root["command"]?.StringValue);
        Assert.Null(rendered.Root["disabled"]);
        Assert.Null(rendered.Root["type"]);
    }

    [Fact]
    public void AServerImportedSwitchedOffArrivesOffInAntigravityAndOpenCodeInTheirOwnShapes()
    {
        using var source = new ToggleHarness();
        using var destination = new ToggleHarness();
        source.Write(
            """{"mcpServers":{"memory":{"command":"npx","args":["-y","@modelcontextprotocol/server-memory"],"env":{"KEY":"original-secret"}}}}""",
            NewClientFixtures.CursorPath);
        var summary = source.ExportTo();
        var service = destination.Portable();
        var preview = service.Preview(
            source.PortableFile(),
            [ClientId.Antigravity, ClientId.OpenCode],
            PortableTestSupport.Inputs(summary));
        var result = service.Apply(preview.Id);
        Assert.Equal(
            new HashSet<ClientId> { ClientId.Antigravity, ClientId.OpenCode },
            result.ChangedClients.ToHashSet());
        Assert.Empty(destination.ParkStore.All());

        var antigravity = NewClientFixtures.Entry(destination, NewClientFixtures.AntigravityPath, "mcpServers", "memory")!;
        Assert.True(antigravity["disabled"]?.BoolValue);
        Assert.Equal("replacement-value", antigravity["env"]?["KEY"]?.StringValue);

        var openCode = NewClientFixtures.Entry(destination, NewClientFixtures.OpenCodePath, "mcp", "memory")!;
        Assert.False(openCode["enabled"]?.BoolValue);
        Assert.Equal("local", openCode["type"]?.StringValue);
        Assert.Equal("npx", NewClientFixtures.Strings(openCode["command"])[0]);
        Assert.Equal("replacement-value", openCode["environment"]?["KEY"]?.StringValue);

        var memory = destination.Server("memory");
        Assert.Equal(Enablement.Disabled, memory.EnabledIn[ClientId.Antigravity]);
        Assert.Equal(Enablement.Disabled, memory.EnabledIn[ClientId.OpenCode]);
    }
}

public class DevinDesktopTests
{
    [Fact]
    public void OnlyTheLegacyTransportIsStatedAsTransport()
    {
        using var harness = new ToggleHarness();
        harness.Write(NewClientFixtures.CursorConfig, NewClientFixtures.CursorPath);
        harness.Write(
            """{ "mcpServers": { "events": { "url": "https://events.example.com/mcp", "transport": "sse" } } }""",
            NewClientFixtures.DevinPath);

        Assert.Equal(Transport.Sse, harness.Server("events").Transport);

        harness.Toggles.SetEnabled(true, harness.Server("remote"), ClientId.DevinDesktop);
        harness.Toggles.SetEnabled(true, harness.Server("legacy"), ClientId.DevinDesktop);
        var document = JsonDocument.Parse(harness.Text(NewClientFixtures.DevinPath));
        var remote = document.ValueAt("mcpServers", "remote")!;
        Assert.Equal("https://remote.example.com/mcp", remote["url"]?.StringValue);
        Assert.Null(remote["type"]);
        Assert.Null(remote["transport"]);
        Assert.Equal("sse", document.ValueAt("mcpServers", "legacy", "transport")?.StringValue);

        harness.Toggles.SetEnabled(false, harness.Server("events"), ClientId.DevinDesktop);
        Assert.Equal(Enablement.Disabled, harness.Server("events").EnabledIn[ClientId.DevinDesktop]);
        Assert.True(JsonDocument.Parse(harness.Text(NewClientFixtures.DevinPath))
            .ValueAt("mcpServers", "events", "disabled")?.BoolValue);
    }

    [Fact]
    public void TheFirstServerCreatesTheFile()
    {
        using var harness = new ToggleHarness();
        harness.Write(NewClientFixtures.CursorConfig, NewClientFixtures.CursorPath);

        Assert.False(harness.Discover().Clients.First(candidate => candidate.Id.BuiltIn == ClientId.DevinDesktop).ConfigExists);
        harness.Toggles.SetEnabled(true, harness.Server("fetch"), ClientId.DevinDesktop);
        var entry = NewClientFixtures.Entry(harness, NewClientFixtures.DevinPath, "mcpServers", "fetch")!;
        Assert.Equal("uvx", entry["command"]?.StringValue);
        Assert.Equal("10", entry["env"]?["FETCH_LIMIT"]?.StringValue);
    }
}

public class OpenCodeTests
{
    private static ToggleHarness MakeHarness()
    {
        var harness = new ToggleHarness();
        harness.Write(NewClientFixtures.OpenCodeConfig, NewClientFixtures.OpenCodePath);
        harness.Write(NewClientFixtures.CursorConfig, NewClientFixtures.CursorPath);
        return harness;
    }

    [Fact]
    public void TheArgvArrayEnvironmentAndEnabledAreRead()
    {
        using var harness = MakeHarness();

        var client = harness.Discover().Clients.First(candidate => candidate.Id.BuiltIn == ClientId.OpenCode);
        Assert.Equal(2, client.ServerCount);

        var github = harness.Server("github");
        Assert.Equal(Transport.Stdio, github.Transport);
        Assert.Equal("npx", github.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], github.Args);
        Assert.Equal("GITHUB_PERSONAL_ACCESS_TOKEN", github.Env[0].Key);
        Assert.Equal(Enablement.Enabled, github.EnabledIn[ClientId.OpenCode]);

        var context = harness.Server("context");
        Assert.Equal(Transport.Http, context.Transport);
        Assert.Equal("https://mcp.example.com/mcp", context.Url);
        Assert.Equal(Enablement.Disabled, context.EnabledIn[ClientId.OpenCode]);
    }

    [Fact]
    public void SwitchingWritesEnabledAndTheRestOfTheJsoncFileSurvives()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(false, harness.Server("github"), ClientId.OpenCode);
        var text = harness.Text(NewClientFixtures.OpenCodePath);
        Assert.Contains("// Personal settings.", text, StringComparison.Ordinal);
        Assert.Contains("\"tools\": { \"github_delete_*\": false }", text, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(text).ValueAt("mcp", "github", "enabled")?.BoolValue);
        Assert.Equal(Enablement.Disabled, harness.Server("github").EnabledIn[ClientId.OpenCode]);

        harness.Toggles.SetEnabled(true, harness.Server("context"), ClientId.OpenCode);
        Assert.True(JsonDocument.Parse(harness.Text(NewClientFixtures.OpenCodePath))
            .ValueAt("mcp", "context", "enabled")?.BoolValue);
        Assert.Equal(Enablement.Enabled, harness.Server("context").EnabledIn[ClientId.OpenCode]);
    }

    [Fact]
    public void ACursorServerIsRenderedInOpenCodesOwnShape()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(true, harness.Server("fetch"), ClientId.OpenCode);
        var entry = NewClientFixtures.Entry(harness, NewClientFixtures.OpenCodePath, "mcp", "fetch")!;
        Assert.Equal("local", entry["type"]?.StringValue);
        Assert.Equal(["uvx", "mcp-server-fetch"], NewClientFixtures.Strings(entry["command"]));
        Assert.Equal("10", entry["environment"]?["FETCH_LIMIT"]?.StringValue);
        Assert.Null(entry["args"]);
        Assert.Null(entry["env"]);
        Assert.Equal(Enablement.Enabled, harness.Server("fetch").EnabledIn[ClientId.OpenCode]);
    }

    [Fact]
    public void ADefinitionWithHeadersIsRefusedRatherThanCopiedWithoutThem()
    {
        using var harness = MakeHarness();

        var refused = Assert.Throws<ToggleException>(() =>
            harness.Toggles.SetEnabled(true, harness.Server("remote"), ClientId.OpenCode));
        Assert.Contains("headers", refused.Message, StringComparison.Ordinal);
        Assert.Equal(NewClientFixtures.OpenCodeConfig, harness.Text(NewClientFixtures.OpenCodePath));
    }

    [Fact]
    public void AnOpenCodeServerArrivesInCursorAsCommandAndArgs()
    {
        using var harness = MakeHarness();

        harness.Toggles.SetEnabled(true, harness.Server("github"), ClientId.Cursor);
        var entry = NewClientFixtures.Entry(harness, NewClientFixtures.CursorPath, "mcpServers", "github")!;
        Assert.Equal("npx", entry["command"]?.StringValue);
        Assert.Equal(2, entry["args"]?.Elements?.Count);
        Assert.NotNull(entry["env"]?["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Null(entry["type"]);
        Assert.Null(entry["timeout"]);
    }

    [Fact]
    public void AddingEditingAndRepairingAnArgumentKeepOpenCodesKeys()
    {
        using var harness = MakeHarness();

        var draft = new ServerDraft
        {
            Name = "time",
            Command = "uvx",
            Args = ["mcp-server-time"],
            Env = [new EnvEntry("TZ", "UTC")],
        };
        harness.Authoring().Create(draft, [ClientId.OpenCode], harness.Discover().Servers);
        var added = NewClientFixtures.Entry(harness, NewClientFixtures.OpenCodePath, "mcp", "time")!;
        Assert.Equal("local", added["type"]?.StringValue);
        Assert.Equal(["uvx", "mcp-server-time"], NewClientFixtures.Strings(added["command"]));
        Assert.Equal("UTC", added["environment"]?["TZ"]?.StringValue);

        // An unchanged save writes nothing.
        var before = harness.Text(NewClientFixtures.OpenCodePath);
        var github = harness.Server("github");
        var unchanged = harness.Authoring().Update(ServerDraft.Editing(github), "github", github, harness.Discover().Servers);
        Assert.Empty(unchanged.Changed);
        Assert.Equal(before, harness.Text(NewClientFixtures.OpenCodePath));

        var edit = ServerDraft.Editing(github) with { Args = ["-y", "@modelcontextprotocol/server-github@1.2.3"] };
        harness.Authoring().Update(edit, "github", github, harness.Discover().Servers);
        var edited = NewClientFixtures.Entry(harness, NewClientFixtures.OpenCodePath, "mcp", "github")!;
        Assert.Equal(
            ["npx", "-y", "@modelcontextprotocol/server-github@1.2.3"],
            NewClientFixtures.Strings(edited["command"]));
        Assert.NotNull(edited["timeout"]);
        Assert.Equal("ghp_examplevalue1234567890", edited["environment"]?["GITHUB_PERSONAL_ACCESS_TOKEN"]?.StringValue);

        var pinned = harness.Server("github");
        harness.Authoring().ReplaceArgument(pinned, new Dictionary<ClientId, ArgumentEdit>
        {
            [ClientId.OpenCode] = new(1, "@modelcontextprotocol/server-github@1.2.3", "@modelcontextprotocol/server-github@1.2.4"),
        });
        Assert.Equal(
            ["-y", "@modelcontextprotocol/server-github@1.2.4"],
            harness.Server("github").Args);
    }

    [Fact]
    public void ARemoteEntryNeverGetsEnvironmentAndATransportChangeLeavesNoKeysOfTheOtherKind()
    {
        using var harness = MakeHarness();

        var remote = new ServerDraft
        {
            Name = "hosted",
            Transport = Transport.Http,
            Env = [new EnvEntry("TOKEN", "x")],
            Url = "https://hosted.example.com/mcp",
        };
        Assert.Null(JsonDocument.Parse(remote.Definition("", "  ", "\n", ServerSchema.OpenCode)).Root["environment"]);

        var github = harness.Server("github");
        var toRemote = ServerDraft.Editing(github) with
        {
            Transport = Transport.Http,
            Url = "https://github.example.com/mcp",
        };
        harness.Authoring().Update(toRemote, "github", github, harness.Discover().Servers);
        var remoteEntry = NewClientFixtures.Entry(harness, NewClientFixtures.OpenCodePath, "mcp", "github")!;
        Assert.Equal("remote", remoteEntry["type"]?.StringValue);
        Assert.Null(remoteEntry["command"]);
        Assert.Null(remoteEntry["environment"]);
        Assert.NotNull(remoteEntry["timeout"]);

        var context = harness.Server("context");
        var toLocal = ServerDraft.Editing(context) with
        {
            Transport = Transport.Stdio,
            Command = "npx",
            Args = ["-y", "context-mcp"],
        };
        harness.Authoring().Update(toLocal, "context", context, harness.Discover().Servers);
        var localEntry = NewClientFixtures.Entry(harness, NewClientFixtures.OpenCodePath, "mcp", "context")!;
        Assert.Equal("local", localEntry["type"]?.StringValue);
        Assert.Null(localEntry["url"]);
        Assert.Equal(3, localEntry["command"]?.Elements?.Count);
        Assert.False(localEntry["enabled"]?.BoolValue);
    }

    [Fact]
    public void RotatingASecretChangesTheValueUnderEnvironment()
    {
        using var harness = MakeHarness();

        var servers = harness.Discover().Servers;
        var secrets = new SecretsService(
            harness.Home,
            new ConfigWriter(harness.Backups),
            harness.Ledger,
            new InMemorySecretStore());
        var record = secrets.Scan(servers).First(candidate => candidate.Key == "GITHUB_PERSONAL_ACCESS_TOKEN");
        secrets.Rotate(record.Id, "ghp_rotatedvalue0987654321", servers, alsoStore: false);
        Assert.Equal("ghp_rotatedvalue0987654321", harness.Server("github").Env[0].Value);
        Assert.Contains("// Personal settings.", harness.Text(NewClientFixtures.OpenCodePath), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteHeaderPlaceholdersInOpenCodesSpellingAreExpanded()
    {
        var environment = new Dictionary<string, string> { ["TEAM_TOKEN"] = "abc" };
        Assert.Equal("Bearer abc", Kytto.Core.Health.RemoteHttpHeaders.Expand("Bearer {env:TEAM_TOKEN}", environment));
        Assert.Equal("Bearer abc", Kytto.Core.Health.RemoteHttpHeaders.Expand("Bearer ${TEAM_TOKEN}", environment));
        Assert.Equal("Bearer abc", Kytto.Core.Health.RemoteHttpHeaders.Expand("Bearer ${env:TEAM_TOKEN}", environment));
    }
}

public class OpenCodeGatewayTests
{
    [Fact]
    public void GatewayModeRefusesOpenCodeInsteadOfWritingAnEntryItWouldNotRead()
    {
        using var harness = new ToggleHarness();
        harness.Write(NewClientFixtures.OpenCodeConfig, NewClientFixtures.OpenCodePath);
        var helper = Path.Combine(harness.Home, "Kytto", "kytto-mcp-proxy.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllBytes(helper, []);
        var service = new Kytto.Core.Gateway.GatewayMigrationService(
            harness.Home,
            helper,
            new ConfigWriter(harness.Backups),
            harness.Ledger,
            new Kytto.Core.Gateway.GatewayRouteStore(harness.Paths),
            new InMemorySecretStore());

        var refused = Assert.Throws<Kytto.Core.Gateway.GatewayMigrationException>(() =>
            service.Preview(harness.Server("github"), ClientId.OpenCode));
        Assert.Contains("OpenCode is not supported by Gateway mode yet", refused.Message, StringComparison.Ordinal);
        Assert.Equal(NewClientFixtures.OpenCodeConfig, harness.Text(NewClientFixtures.OpenCodePath));
    }
}

public class ClientIdentityTests
{
    [Fact]
    public void EveryBuiltInClientRoundTripsThroughItsWireSpelling()
    {
        foreach (var id in ClientIds.All)
        {
            Assert.Equal(id, ClientIds.FromRaw(id.Raw()));
            Assert.Equal(id, ClientRegistry.Descriptor(id).Id);
        }
        Assert.Equal("devinDesktop", ClientId.DevinDesktop.Raw());
        Assert.Equal("openCode", ClientId.OpenCode.Raw());
    }
}
