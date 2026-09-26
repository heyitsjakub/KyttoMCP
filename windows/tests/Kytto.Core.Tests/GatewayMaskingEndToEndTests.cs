using System.Diagnostics;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;

namespace Kytto.Core.Tests;

/// <summary>Runs the real <c>kytto-mcp-proxy</c> binary against the fixture server.</summary>
/// <remarks>
/// The filter is unit-tested on its own; this exists because everything that can go
/// wrong in wiring it up is invisible at that level — a refusal written without its
/// newline, two tasks interleaving on stdout, or a relay that buffers a message and
/// never flushes it. Only a real process pair shows those.
/// </remarks>
[Collection("gateway-e2e")]
public sealed class GatewayMaskingEndToEndTests
{
    private static string? Python =>
        Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, "python.exe"))
            .FirstOrDefault(File.Exists);

    /// <summary>The helper is built beside the test binary by the solution build.</summary>
    private static string HelperPath
    {
        get
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, "kytto-mcp-proxy.exe");
            if (File.Exists(candidate)) return candidate;

            // Test projects do not copy a sibling executable's output, so fall back
            // to the Gateway project's own bin directory.
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(
                repo, "src", "Kytto.Gateway", "bin", "Debug", "net10.0-windows", "kytto-mcp-proxy.exe");
        }
    }

    private static string FixtureServer =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_mcp_server.py");

    private static readonly string[] Handshake =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
    ];

    /// <summary>A session with the helper: writes each line, then reads until it closes.</summary>
    private static IReadOnlyList<JsonElement> Converse(
        IReadOnlyList<string>? exposedTools,
        params string[] lines)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"kytto-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var routesPath = Path.Combine(directory, "routes.json");
            var routeId = Guid.NewGuid().ToString();
            new GatewayRouteStore(routesPath).Insert(new GatewayRoute(
                Id: routeId,
                ServerID: "fixture",
                ServerName: "fixture",
                ClientID: ClientId.Cursor,
                GatewayCommand: HelperPath,
                Command: Python!,
                Arguments: [FixtureServer, "ok"],
                // No environment references, so nothing reaches for Credential Manager.
                Environment: [],
                DirectDefinitionSecretID: $"gateway-definition::{routeId}",
                CreatedAt: DateTimeOffset.UnixEpoch)
            {
                ExposedTools = exposedTools,
            });

            var start = new ProcessStartInfo(HelperPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--route");
            start.ArgumentList.Add(routeId);
            start.ArgumentList.Add("--routes");
            start.ArgumentList.Add(routesPath);
            start.ArgumentList.Add("--event-log");
            start.ArgumentList.Add(Path.Combine(directory, "events.jsonl"));

            using var process = Process.Start(start)!;
            foreach (var line in lines) process.StandardInput.Write(line + "\n");
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            var replies = new List<JsonElement>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try { replies.Add(JsonDocument.Parse(line).RootElement.Clone()); }
                catch (JsonException) { }
            }
            return replies;
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static JsonElement Reply(IReadOnlyList<JsonElement> replies, int id) =>
        replies.First(reply =>
            reply.TryGetProperty("id", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.GetInt32() == id);

    private static string[] Tools(IReadOnlyList<JsonElement> replies, int id)
    {
        var reply = Reply(replies, id);
        Assert.True(reply.TryGetProperty("result", out var result), $"reply {id} carried no result");
        Assert.True(result.TryGetProperty("tools", out var tools), $"reply {id} carried no tools array");
        return tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).ToArray();
    }

    private static void RequireHelper()
    {
        Assert.True(Python is not null, "These tests need python.exe on PATH.");
        Assert.True(File.Exists(HelperPath), $"kytto-mcp-proxy.exe was not built at {HelperPath}");
    }

    [Fact]
    public void AnUnmaskedRouteStillRelaysTheFullToolList()
    {
        RequireHelper();
        var replies = Converse(
            null,
            [.. Handshake, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""]);

        Assert.Equal(["read_file", "write_file", "list_directory"], Tools(replies, 2));
    }

    [Fact]
    public void AMaskedRouteHidesTheToolsItWasNotGiven()
    {
        RequireHelper();
        var replies = Converse(
            ["read_file", "list_directory"],
            [.. Handshake, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""]);

        Assert.Equal(["read_file", "list_directory"], Tools(replies, 2));
    }

    [Fact]
    public void ACallToAHiddenToolIsRefusedWithoutReachingTheServer()
    {
        RequireHelper();
        var replies = Converse(
            ["read_file"],
            [
                .. Handshake,
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"write_file","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_file","arguments":{}}}""",
            ]);

        var refused = Reply(replies, 2);
        Assert.Equal(-32_601, refused.GetProperty("error").GetProperty("code").GetInt32());
        // The fixture answers every call with "fixture result", so a result here
        // would mean the refusal never happened.
        Assert.False(refused.TryGetProperty("result", out _));

        var allowed = Reply(replies, 3);
        Assert.True(allowed.TryGetProperty("result", out _));
        Assert.False(allowed.TryGetProperty("error", out _));
    }

    [Fact]
    public void TheHandshakeIsUntouchedByMasking()
    {
        RequireHelper();
        var replies = Converse(
            [],
            [.. Handshake, """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""]);

        Assert.Equal(
            "fake-mcp-server",
            Reply(replies, 1).GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Empty(Tools(replies, 2));
    }

    /// <summary>Masking must not become a filter on everything else the server offers.</summary>
    [Fact]
    public void PromptsAndResourcesAreNotTouched()
    {
        RequireHelper();
        var replies = Converse(
            ["read_file"],
            [
                .. Handshake,
                """{"jsonrpc":"2.0","id":2,"method":"prompts/list","params":{}}""",
                """{"jsonrpc":"2.0","id":3,"method":"resources/list","params":{}}""",
            ]);

        Assert.Equal(2, Reply(replies, 2).GetProperty("result").GetProperty("prompts").GetArrayLength());
        Assert.Equal(1, Reply(replies, 3).GetProperty("result").GetProperty("resources").GetArrayLength());
    }
}
