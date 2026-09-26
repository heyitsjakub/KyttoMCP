using Kytto.Core.Imports;
using Kytto.Core.Model;

namespace Kytto.Core.Tests;

public sealed class AgentImportTests
{
    private const string Valid = """
        {
          "schemaVersion": 1,
          "servers": [
            {
              "name": "demo",
              "transport": "stdio",
              "command": "node",
              "args": ["server.js"],
              "env": ["API_KEY", "OPTIONAL_FLAG"]
            },
            {
              "name": "remote",
              "transport": "http",
              "url": "https://example.com/mcp"
            }
          ]
        }
        """;

    [Fact]
    public void ParsesTheExactVersionOneShapeWithoutMovingSecrets()
    {
        var document = AgentImportParser.Parse(Valid);

        Assert.Equal(["demo", "remote"], document.Servers.Select(server => server.Name));
        Assert.Equal(Transport.Stdio, document.Servers[0].Transport);
        Assert.Equal(["API_KEY", "OPTIONAL_FLAG"], document.Servers[0].EnvironmentKeys);
        Assert.Empty(document.Servers[0].ToDraft().Env);
        Assert.Equal(Transport.Http, document.Servers[1].Transport);
        Assert.Equal("https://example.com/mcp", document.Servers[1].Url);
    }

    [Fact]
    public void RejectsCommentsFencesTrailingCommasAndNonNumericVersionOne()
    {
        var invalid = new[]
        {
            "// comment\n" + Valid,
            "```json\n" + Valid + "\n```",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"stdio\",\"command\":\"node\",}],}",
            Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": \"1\"", StringComparison.Ordinal),
            Valid.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1.0", StringComparison.Ordinal),
        };

        foreach (var json in invalid)
        {
            Assert.Throws<AgentImportException>(() => AgentImportParser.Parse(json));
        }
    }

    [Fact]
    public void RejectsUnknownDuplicateAndUnsafeFields()
    {
        var invalid = new[]
        {
            "{\"schemaVersion\":1,\"servers\":[],\"extra\":true}",
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"servers\":[]}",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"stdio\",\"command\":\"node\",\"unknown\":1}]}",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"stdio\",\"command\":\"node\",\"env\":[\"BAD-NAME\"]}]}",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"stdio\",\"command\":\"node\"},{\"name\":\"X\",\"transport\":\"stdio\",\"command\":\"other\"}]}",
        };

        foreach (var json in invalid)
        {
            Assert.Throws<AgentImportException>(() => AgentImportParser.Parse(json));
        }
    }

    [Fact]
    public void EnforcesTransportSpecificFields()
    {
        var invalid = new[]
        {
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"stdio\",\"url\":\"https://example.com\"}]}",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"http\",\"command\":\"node\",\"url\":\"https://example.com\"}]}",
            "{\"schemaVersion\":1,\"servers\":[{\"name\":\"x\",\"transport\":\"sse\",\"url\":\"file:///tmp/server\"}]}",
        };

        foreach (var json in invalid)
        {
            Assert.Throws<AgentImportException>(() => AgentImportParser.Parse(json));
        }
    }
}
