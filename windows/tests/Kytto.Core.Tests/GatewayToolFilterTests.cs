using System.Text;
using System.Text.Json;
using Kytto.Core.Clients;
using Kytto.Core.Gateway;

namespace Kytto.Core.Tests;

public sealed class GatewayLineFramerTests
{
    private static IReadOnlyList<string> Lines(params string[] chunks)
    {
        var framer = new GatewayLineFramer();
        var result = new List<string>();
        foreach (var chunk in chunks)
        {
            framer.Consume(Encoding.UTF8.GetBytes(chunk), line => result.Add(Encoding.UTF8.GetString(line)));
        }
        framer.Flush(line => result.Add(Encoding.UTF8.GetString(line)));
        return result;
    }

    [Fact]
    public void OneMessagePerLineWhateverTheReadBoundariesWere()
    {
        // The normal case, not the edge case: a large tools/list response arrives in
        // several reads and none of them ends on a message boundary.
        Assert.Equal(["""{"a":1}""", """{"b":2}"""], Lines("{\"a\":1}\n{\"b\":2}\n"));
        Assert.Equal(["""{"a":1}""", """{"b":2}"""], Lines("{\"a\":", "1}\n{\"b", "\":2}\n"));
        Assert.Equal(["""{"a":1}""", """{"b":2}"""], Lines("{\"a\":1}\n", "{\"b\":2}\n"));
    }

    [Fact]
    public void ATrailingPartialMessageIsStillHandedOver() =>
        // A server killed mid-write should not have its bytes silently dropped.
        Assert.Equal(["{\"a\":1}", "{\"partial\""], Lines("{\"a\":1}\n{\"partial\""));

    [Fact]
    public void EmptyLinesSurviveAsEmptyMessages() => Assert.Equal(["", ""], Lines("\n\n"));
}

public sealed class GatewayToolFilterTests
{
    private static GatewayToolFilter Filter() => new(["read_file", "list_directory"]);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static JsonElement Object(byte[] data) => JsonDocument.Parse(data).RootElement;

    private static string[] ToolNames(JsonElement message) =>
        message.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();

    // MARK: - Client to server

    [Fact]
    public void ACallToAnExposedToolGoesUpstreamUntouched()
    {
        var line = Json("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"read_file"}}""");
        Assert.IsType<GatewayToolFilter.ClientDecision.Forward>(Filter().InspectClientLine(line));
    }

    [Fact]
    public void ACallToAHiddenToolIsRefusedHereAndNeverReachesTheServer()
    {
        var line = Json("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_file"}}""");
        var refusal = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(
            Filter().InspectClientLine(line));

        var reply = Object(refusal.Response);
        var error = reply.GetProperty("error");
        Assert.Equal(-32_601, error.GetProperty("code").GetInt32());
        Assert.Contains("write_file", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        // The id has to come back in the type it arrived in, or the client cannot
        // match the reply to its own request.
        Assert.Equal(JsonValueKind.Number, reply.GetProperty("id").ValueKind);
        Assert.Equal(7, reply.GetProperty("id").GetInt32());
        Assert.Equal("2.0", reply.GetProperty("jsonrpc").GetString());
    }

    [Fact]
    public void AStringRequestIdComesBackAsAString()
    {
        var line = Json("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"write_file"}}""");
        var refusal = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(
            Filter().InspectClientLine(line));

        var id = Object(refusal.Response).GetProperty("id");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.Equal("abc", id.GetString());
    }

    [Fact]
    public void AnythingThatIsNotAToolsCallPassesThrough()
    {
        var subject = Filter();
        string[] lines =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"prompts/list","params":{}}""",
            "a banner the server printed to stdout",
            "",
        ];

        foreach (var line in lines)
        {
            Assert.IsType<GatewayToolFilter.ClientDecision.Forward>(
                subject.InspectClientLine(Json(line)));
        }
    }

    // MARK: - Server to client

    [Fact]
    public void HiddenToolsAreRemovedFromTheToolsListResult()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""));

        var response = Json(
            """
            {"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file","description":"Read."},{"name":"write_file","description":"Write."},{"name":"list_directory","description":"List."}]}}
            """);
        var rewritten = Object(subject.RewriteServerLine(response));

        Assert.Equal(["read_file", "list_directory"], ToolNames(rewritten));
        // Everything else about the envelope survives.
        Assert.Equal(2, rewritten.GetProperty("id").GetInt32());
        Assert.Equal("2.0", rewritten.GetProperty("jsonrpc").GetString());
        Assert.Equal(
            "Read.",
            rewritten.GetProperty("result").GetProperty("tools")[0].GetProperty("description").GetString());
    }

    [Fact]
    public void AResultWithNothingToRemoveIsReturnedByteForByte()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""));

        var response = Json("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file"}]}}""");
        Assert.Equal(response, subject.RewriteServerLine(response));
    }

    /// <summary>
    /// The reason responses are matched to their request rather than sniffed for a
    /// <c>tools</c> key: another method is allowed to return one.
    /// </summary>
    [Fact]
    public void OnlyTheAnswerToAToolsListRequestIsRewritten()
    {
        var unsolicited = Json("""{"jsonrpc":"2.0","id":99,"result":{"tools":[{"name":"write_file"}]}}""");
        Assert.Equal(unsolicited, Filter().RewriteServerLine(unsolicited));
    }

    [Fact]
    public void ARequestIsOnlyAnsweredOnce()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""));

        var response = Json("""{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"write_file"}]}}""");
        Assert.NotEqual(response, subject.RewriteServerLine(response));
        // A duplicate id later in the session belongs to a different request.
        Assert.Equal(response, subject.RewriteServerLine(response));
    }

    [Fact]
    public void NonJsonOutputFromTheServerIsForwardedVerbatim()
    {
        var banner = Json("Server listening on stdio…");
        Assert.Equal(banner, Filter().RewriteServerLine(banner));
    }

    [Fact]
    public void AnEmptyAllowListHidesEverything()
    {
        var subject = new GatewayToolFilter([]);
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""));

        var response = Json("""{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"read_file"}]}}""");
        Assert.Empty(ToolNames(Object(subject.RewriteServerLine(response))));
    }

    /// <summary>
    /// The closed-list decision (§7.11): a tool the server begins offering after the
    /// list was chosen stays hidden rather than quietly re-growing the context budget
    /// the user set.
    /// </summary>
    [Fact]
    public void AToolAddedByAServerUpdateStaysHiddenUntilItIsChosen()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""));

        var response = Json(
            """{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"read_file"},{"name":"brand_new_tool"}]}}""");
        Assert.Equal(["read_file"], ToolNames(Object(subject.RewriteServerLine(response))));

        var call = Json("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"brand_new_tool"}}""");
        Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(subject.InspectClientLine(call));
    }
}

public sealed class GatewayRouteMaskingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"kytto-mask-{Guid.NewGuid():N}");

    public GatewayRouteMaskingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private GatewayRouteStore Store() => new(Path.Combine(_directory, "routes.json"));

    private static GatewayRoute Route() => new(
        Id: Guid.NewGuid().ToString(),
        ServerID: "filesystem",
        ServerName: "filesystem",
        ClientID: ClientId.Cursor,
        GatewayCommand: @"C:\Program Files\Kytto\kytto-mcp-proxy.exe",
        Command: "npx",
        Arguments: ["-y", "server-filesystem"],
        Environment: [],
        DirectDefinitionSecretID: "gateway-definition::x",
        CreatedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void ARouteExposesEverythingUntilItIsNarrowed() => Assert.Null(Route().ExposedTools);

    [Fact]
    public void NarrowingAndClearingRoundTripThroughTheStore()
    {
        var store = Store();
        var original = Route();
        store.Insert(original);
        var stored = store.Route(original.Id);

        var narrowed = store.Update(original.Id, ["read_file", "list_directory"]);
        // Stored sorted, so files do not churn.
        Assert.Equal(["list_directory", "read_file"], narrowed.ExposedTools);
        Assert.Equal(["list_directory", "read_file"], store.Route(original.Id).ExposedTools);

        // Nothing about launching the upstream server may have moved.
        Assert.Equal(stored.Command, narrowed.Command);
        Assert.Equal(stored.Arguments, narrowed.Arguments);
        Assert.Equal(stored.CreatedAt, narrowed.CreatedAt);

        Assert.Null(store.Update(original.Id, null).ExposedTools);
    }

    [Fact]
    public void ExposingNothingIsARealChoiceNotTheSameAsExposingEverything()
    {
        var store = Store();
        var original = Route();
        store.Insert(original);

        Assert.Empty(store.Update(original.Id, [])!.ExposedTools!);
        Assert.Empty(store.Route(original.Id).ExposedTools!);
    }

    [Fact]
    public void DuplicatesAreCollapsed() =>
        Assert.Equal(["a", "b"], Route().Exposing(["a", "a", "b"]).ExposedTools);

    [Fact]
    public void ARouteFileWrittenBeforeMaskingExistedStillReads()
    {
        var path = Path.Combine(_directory, "routes.json");
        File.WriteAllText(
            path,
            """
            [{"id":"11111111-1111-1111-1111-111111111111","serverID":"filesystem","serverName":"filesystem","clientID":"cursor","gatewayCommand":"C:\\proxy.exe","command":"npx","arguments":[],"environment":[],"directDefinitionSecretID":"gateway-definition::x","createdAt":"2026-01-01T00:00:00Z"}]
            """);

        var routes = new GatewayRouteStore(path).All();
        Assert.Single(routes);
        // An upgraded install must not start masking on its own.
        Assert.Null(routes[0].ExposedTools);
    }

    [Fact]
    public void UpdatingARouteThatIsGoneIsRefused() =>
        Assert.Throws<GatewayRouteException>(() => Store().Update("missing", []));
}
