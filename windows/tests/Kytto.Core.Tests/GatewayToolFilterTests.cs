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

    /// <summary>A hidden tool cannot be called by wrapping the call in a JSON-RPC batch.</summary>
    [Fact]
    public void ABatchHidingACallIsRefusedWholeAndAnsweredAsABatch()
    {
        var line = Json("""[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file"}},{"jsonrpc":"2.0","id":"two","method":"tools/call","params":{"name":"write_file"}},{"jsonrpc":"2.0","method":"notifications/x"}]""");

        var refusal = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(Filter().InspectClientLine(line));

        var replies = Object(refusal.Response).EnumerateArray().ToArray();
        Assert.Equal(2, replies.Length);
        Assert.All(replies, reply => Assert.Equal(-32_602, reply.GetProperty("error").GetProperty("code").GetInt32()));
        Assert.Equal(1, replies[0].GetProperty("id").GetInt32());
        Assert.Equal("two", replies[1].GetProperty("id").GetString());
    }

    [Fact]
    public void ABatchedToolsListResponseIsFiltered()
    {
        var filter = Filter();
        Assert.IsType<GatewayToolFilter.ClientDecision.Forward>(filter.InspectClientLine(
            Json("""[{"jsonrpc":"2.0","id":4,"method":"tools/list"},{"jsonrpc":"2.0","id":5,"method":"ping"}]""")));

        var rewritten = filter.RewriteServerLine(Json(
            """[{"jsonrpc":"2.0","id":5,"result":{}},{"jsonrpc":"2.0","id":4,"result":{"tools":[{"name":"read_file"},{"name":"write_file"}]}}]"""));

        var batch = Object(rewritten).EnumerateArray().ToArray();
        Assert.Equal(["read_file"], ToolNames(batch[1]));
        Assert.Equal(5, batch[0].GetProperty("id").GetInt32());
    }

    /// <summary><c>1</c> and <c>"1"</c> are different requests; matching them would filter the wrong reply.</summary>
    [Fact]
    public void AStringIdAndANumberIdAreDifferentRequests()
    {
        var filter = Filter();
        filter.InspectClientLine(Json("""{"jsonrpc":"2.0","id":"1","method":"tools/list"}"""));

        var unrelated = Json("""{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"write_file"}]}}""");
        Assert.Same(unrelated, filter.RewriteServerLine(unrelated));

        var answer = filter.RewriteServerLine(Json("""{"jsonrpc":"2.0","id":"1","result":{"tools":[{"name":"write_file"}]}}"""));
        Assert.Empty(ToolNames(Object(answer)));
    }

    [Fact]
    public void ACallToAHiddenToolIsRefusedHereAndNeverReachesTheServer()
    {
        var line = Json("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_file"}}""");
        var refusal = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(
            Filter().InspectClientLine(line));

        var reply = Object(refusal.Response);
        var error = reply.GetProperty("error");
        Assert.Equal(-32_602, error.GetProperty("code").GetInt32());
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
            "",
            "  \r",
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
    public void AServerRequestReusingAPendingIdDoesNotLetTheRealListThroughUnfiltered()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}"""));
        // The server's own request, numbered from its own id space.
        var request = Json("""{"jsonrpc":"2.0","id":1,"method":"roots/list"}""");
        Assert.Equal(request, subject.RewriteServerLine(request));

        var response = Json(
            """{"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"read_file"},{"name":"delete_all"}]}}""");
        Assert.Equal(["read_file"], ToolNames(Object(subject.RewriteServerLine(response))));
    }

    /// <summary>
    /// <c>GetValue&lt;string&gt;</c> throws on a number, and the relay only survives
    /// I/O errors — one odd message used to end the whole session. A method that is
    /// not a string is not a call, so there is nothing to refuse.
    /// </summary>
    [Fact]
    public void AMethodThatIsNotAStringIsForwardedRatherThanThrown() =>
        Assert.IsType<GatewayToolFilter.ClientDecision.Forward>(
            Filter().InspectClientLine(Json("""{"jsonrpc":"2.0","id":3,"method":42}""")));

    /// <summary>Read last-wins, the way the server on the other side reads it.</summary>
    [Fact]
    public void ARepeatedNameIsReadTheWayTheServerReadsIt()
    {
        var call = Json(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_file","name":"write_file"}}""");
        Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(Filter().InspectClientLine(call));

        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""));
        var response = Json(
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file","name":"delete_all"},{"name":"list_directory"}]}}""");
        Assert.Equal(["list_directory"], ToolNames(Object(subject.RewriteServerLine(response))));
    }

    [Fact]
    public void OddToolEntriesInAListAreDroppedRatherThanThrown()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""));

        var response = Json(
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"read_file"},"write_file",{"name":5},null,[1]]}}""");
        Assert.Equal(["read_file"], ToolNames(Object(subject.RewriteServerLine(response))));
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

/// <summary>
/// AUD-07: the filter decides with one parser and forwards the original bytes to a
/// server that reads them with another. Anything the two could read differently is
/// refused, not forwarded.
/// </summary>
public sealed class GatewayToolFilterAmbiguityTests
{
    private static GatewayToolFilter Filter() => new(["read_file"]);

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The refusal's reply, or null when the line was forwarded.</summary>
    private static JsonElement? Refusal(byte[] line)
    {
        if (Filter().InspectClientLine(line) is not GatewayToolFilter.ClientDecision.Refuse refuse) return null;
        if (refuse.Response.Length == 0) return default(JsonElement);
        return JsonDocument.Parse(refuse.Response).RootElement.Clone();
    }

    private static JsonElement Refused(string line)
    {
        var reply = Refusal(Json(line));
        Assert.True(reply is not null, $"forwarded: {line}");
        return reply!.Value;
    }

    /// <summary>
    /// Read last-wins, the first order was refused before this; the second is what a
    /// first-wins server runs as <c>write_file</c>.
    /// </summary>
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"read_file","name":"write_file"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"write_file","name":"read_file"}}""")]
    public void ARepeatedToolNameIsRefusedWhicheverComesFirst(string line)
    {
        var reply = Refused(line);
        Assert.Equal(41, reply.GetProperty("id").GetInt32());
        Assert.Equal(GatewayToolFilter.InvalidRequest, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void AnEscapedSpellingOfTheSameKeyCountsAsARepeat() =>
        Refused("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file","na\u006de":"write_file"}}""");

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","method":"tools/call","params":{"name":"write_file"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file"},"params":{"name":"write_file"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"id":2,"method":"tools/call","params":{"name":"read_file"}}""")]
    public void RepeatedEnvelopeKeysAreRefused(string line) => Refused(line);

    [Fact]
    public void WithTwoIdsThereIsNoSingleOneToEcho() =>
        Assert.Equal(
            JsonValueKind.Null,
            Refused("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""").GetProperty("id").ValueKind);

    [Fact]
    public void ARepeatAnywhereInTheMessageIsRefusedNotOnlyInTheCall() =>
        Refused("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file","arguments":{"path":"a","path":"b"}}}""");

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":["write_file"]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":7}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":["write_file"]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":"write_file"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_file\ud800"}}""")]
    public void ACallWithoutExactlyOneStringNameIsRefused(string line)
    {
        var reply = Refused(line);
        Assert.Equal(3, reply.GetProperty("id").GetInt32());
        Assert.Equal(GatewayToolFilter.UnknownTool, reply.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"write_file","arguments":{"n":NaN}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_file" /* , "name":"write_file" */}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_file",}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_file"}} {"x":1}""")]
    [InlineData("not json at all")]
    public void AMessageTheFilterCannotParseIsRefusedInsteadOfForwarded(string line) =>
        Assert.Equal(
            GatewayToolFilter.ParseError,
            Refused(line).GetProperty("error").GetProperty("code").GetInt32());

    /// <summary>
    /// The Windows-only form of AUD-07: <c>JsonElement.GetString</c> throws on an
    /// unpaired surrogate, and the throw used to let the helper forward its whole
    /// buffer unread. A hidden tool reached the real server this way.
    /// </summary>
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":"\ud800","method":"tools/call","params":{"name":"write_file"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_file"},"\udc00":1}""")]
    [InlineData("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"write_file","arguments":{"p":"\ud83d"}}}""")]
    public void AnUnpairedSurrogateNeitherThrowsNorLetsAHiddenCallThrough(string line)
    {
        var filter = Filter();
        Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(filter.InspectClientLine(Json(line)));
        Assert.Equal(["write_file"], filter.HiddenCalls(Json(line)).Select(call => call.ToolName));
    }

    [Fact]
    public void AnUnpairedSurrogateIdIsEchoedExactlyAsItArrived()
    {
        var reply = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(Filter().InspectClientLine(
            Json("""{"jsonrpc":"2.0","id":"\ud800","method":"tools/call","params":{"name":"write_file"}}""")));
        Assert.Contains("\"id\":\"\\ud800\"", Encoding.UTF8.GetString(reply.Response), StringComparison.Ordinal);
    }

    [Fact]
    public void BytesThatAreNotUtf8DoNotThrow()
    {
        byte[] line =
        [
            .. Json("""{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"read_file"""),
            0xFF,
            .. Json("\"}}"),
        ];
        var reply = Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(Filter().InspectClientLine(line));
        Assert.Equal(8, JsonDocument.Parse(reply.Response).RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void ADecomposedSpellingOfAnExposedNameIsADifferentTool()
    {
        var filter = new GatewayToolFilter(["caf\u00E9"]);
        Assert.True(filter.Allows("caf\u00E9"));
        Assert.False(filter.Allows("cafe\u0301"));
        Assert.False(filter.Allows("CAF\u00C9"));
    }

    [Theory]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file","name":"write_file"}}]""")]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":7}}]""")]
    [InlineData("""[[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"write_file"}}]]""")]
    [InlineData("""[{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"read_file"}},null]""")]
    [InlineData("""[1,2,3]""")]
    public void ABatchWithARepeatOrAnElementThatIsNotAMessageIsRefused(string line) =>
        Assert.IsType<GatewayToolFilter.ClientDecision.Refuse>(Filter().InspectClientLine(Json(line)));

    [Fact]
    public void RefusalsOfUnreadableMessagesAreJournalledWithoutAToolName()
    {
        var calls = Filter().HiddenCalls(Json(
            """{"jsonrpc":"2.0","id":"x","method":"tools/call","params":{"name":"read_file","name":"write_file"}}"""));
        Assert.Equal([GatewayToolFilter.UnreadableRequest], calls.Select(call => call.ToolName));

        var unparsed = Filter().HiddenCalls(Json("not json"));
        Assert.Equal([GatewayToolFilter.UnreadableRequest], unparsed.Select(call => call.ToolName));

        var malformed = Assert.Single(Filter().HiddenCalls(Json(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":7}}""")));
        Assert.Equal((GatewayToolFilter.UnreadableRequest, "9"), malformed);
    }

    /// <summary>
    /// Read last-wins here; what matters is that the client is handed that one reading,
    /// not the original bytes it might resolve the other way.
    /// </summary>
    [Fact]
    public void AToolListWithARepeatedNameIsPassedOnInTheFiltersReading()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}"""));
        var response = Json("""{"jsonrpc":"2.0","id":9,"result":{"tools":[{"name":"write_file","name":"read_file"},{"name":"read_file","name":"write_file"}]}}""");

        var rewritten = subject.RewriteServerLine(response);
        var text = Encoding.UTF8.GetString(rewritten);
        Assert.DoesNotContain("write_file", text, StringComparison.Ordinal);
        Assert.Single(JsonDocument.Parse(rewritten).RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public void ARepeatedIdCannotSlipAnUnfilteredToolListPastTheFilter()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}"""));
        var response = Json("""{"jsonrpc":"2.0","id":9,"id":"other","result":{"tools":[{"name":"read_file"},{"name":"write_file"}]}}""");

        var rewritten = subject.RewriteServerLine(response);
        // The client now reads the single id the filter read, and with it the list
        // the filter left alone because it answered nothing pending.
        var text = Encoding.UTF8.GetString(rewritten);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "\"id\""));
        Assert.Equal("other", JsonDocument.Parse(rewritten).RootElement.GetProperty("id").GetString());

        // The real answer to request 9 is still filtered when it comes.
        var answer = subject.RewriteServerLine(Json("""{"jsonrpc":"2.0","id":9,"result":{"tools":[{"name":"write_file"}]}}"""));
        Assert.DoesNotContain("write_file", Encoding.UTF8.GetString(answer), StringComparison.Ordinal);
    }

    [Fact]
    public void AToolEntryWithoutAReadableNameIsNotShown()
    {
        var subject = Filter();
        subject.InspectClientLine(Json("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}"""));
        var response = Json("""{"jsonrpc":"2.0","id":9,"result":{"tools":[{"name":"read_file"},"write_file",{"title":"x"},{"name":"read_file\udc00"}]}}""");
        var tools = JsonDocument.Parse(subject.RewriteServerLine(response)).RootElement
            .GetProperty("result").GetProperty("tools");
        Assert.Single(tools.EnumerateArray());
    }

    [Fact]
    public void AnUnchangedBatchFromTheServerIsForwardedByteForByte()
    {
        var line = Json("""[{"jsonrpc":"2.0","id":1,"result":{}} ,{"jsonrpc":"2.0","method":"x"}]""");
        Assert.Same(line, Filter().RewriteServerLine(line));
    }

    [Fact]
    public void ServerTextThatCannotBeDecodedIsForwardedWithoutThrowing()
    {
        var line = Json("""{"jsonrpc":"2.0","method":"notifications/message","params":{"data":"\ud800"}}""");
        Assert.Same(line, Filter().RewriteServerLine(line));
    }
}

public sealed class GatewayLineFramerFailureTests
{
    /// <summary>
    /// A line whose handler throws must not come back in <see cref="GatewayLineFramer.Flush"/>
    /// glued to the lines behind it — the relay's shutdown flush used to forward that
    /// glued text unread (AUD-07).
    /// </summary>
    [Fact]
    public void ALineWhoseHandlerThrowsIsNotHandedOverAgain()
    {
        var framer = new GatewayLineFramer();
        Assert.Throws<InvalidOperationException>(() => framer.Consume(
            Encoding.UTF8.GetBytes("first\nsecond\n"),
            line => throw new InvalidOperationException(Encoding.UTF8.GetString(line))));

        var flushed = new List<string>();
        framer.Flush(tail => flushed.Add(Encoding.UTF8.GetString(tail)));
        Assert.Equal(["second\n"], flushed);
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
