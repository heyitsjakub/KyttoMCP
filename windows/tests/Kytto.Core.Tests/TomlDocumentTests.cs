using Kytto.Core.Tests.Support;
using Kytto.Core.Toml;
using Kytto.Core.Clients;

namespace Kytto.Core.Tests;

/// <summary>
/// The product risk for Codex is concentrated here (§12): a config.toml is not a
/// small file, and the servers are a few tables among dozens the user cares about
/// more. Every test below is really the same test — does everything Kytto did not
/// deliberately change come out the other side identical.
/// </summary>
public class TomlSpanTests
{
    [Theory, MemberData(nameof(TomlFixtures))]
    public void EveryTomlFixtureParses(Fixture fixture)
    {
        _ = TomlDocument.Parse(fixture.Text());
    }

    /// <summary>
    /// Splicing a value's own text back over itself must be a no-op on the whole
    /// file. If this fails, a write would move or drop bytes elsewhere — which is
    /// the failure mode §6 exists to prevent.
    /// </summary>
    [Theory, MemberData(nameof(TomlFixtures))]
    public void SplicingAValueOverItselfChangesNothing(Fixture fixture)
    {
        var document = TomlDocument.Parse(fixture.Text());
        foreach (var table in document.Tables)
        {
            var tableSpans = table.HeaderSpan is { } header
                ? new[] { header, table.Span }
                : [table.Span];

            foreach (var span in tableSpans)
            {
                Assert.Equal(document.SourceText, document.Replacing(span, document.Slice(span)));
            }

            foreach (var pair in table.Pairs)
            {
                foreach (var span in new[] { pair.KeySpan, pair.Value.Span, pair.Span })
                {
                    Assert.True(
                        document.Replacing(span, document.Slice(span)) == document.SourceText,
                        $"identity splice altered {fixture.FileName()} at {span.Start}..{span.End}");
                }
            }
        }
    }

    /// <summary>
    /// A value's span must cover exactly that value: re-parsing the text it points
    /// at has to give the same thing back.
    /// </summary>
    [Theory, MemberData(nameof(TomlFixtures))]
    public void SpansPointAtTheirOwnValue(Fixture fixture)
    {
        var document = TomlDocument.Parse(fixture.Text());
        foreach (var table in document.Tables)
        {
            foreach (var pair in table.Pairs)
            {
                var slice = document.Slice(pair.Span);
                var reparsed = TomlDocument.Parse(slice);
                var recovered = reparsed.Tables.FirstOrDefault()?.Pairs.FirstOrDefault();
                Assert.NotNull(recovered);

                Assert.Equal(pair.Key, recovered.Key);
                Assert.Equal(pair.Value.StringValue, recovered.Value.StringValue);
                Assert.Equal(pair.Value.BoolValue, recovered.Value.BoolValue);
                Assert.Equal(
                    pair.Value.Elements?.Select(element => element.StringValue).ToArray(),
                    recovered.Value.Elements?.Select(element => element.StringValue).ToArray());
            }
        }
    }

    public static TheoryData<Fixture> TomlFixtures => Fixtures.Toml;
}

public class TomlReadingTests
{
    private static TomlDocument Document() => TomlDocument.Parse(Fixture.CodexConfig.Text());

    [Fact]
    public void ABracketInsideAMultilineStringIsNotATableHeader()
    {
        var document = Document();
        Assert.Null(document.Table("not", "a", "table"));

        var notify = document.Tables.FirstOrDefault()?.Pair("notify");
        Assert.NotNull(notify);
        Assert.Contains("[not.a.table]", notify.Value.StringValue!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMultilineArrayIsOneValueNotThreeLines()
    {
        var document = Document();
        var permissions = document.Tables.FirstOrDefault()?.Value("sandbox_permissions");
        Assert.NotNull(permissions);
        Assert.Equal<IEnumerable<string?>>(
            ["disk-full-read-access", "disk-write-cwd"],
            permissions.Elements!.Select(element => element.StringValue).ToArray());
    }

    /// <summary>
    /// The env sub-table is part of a server, not a server of its own.
    /// </summary>
    [Fact]
    public void ChildTablesFindServersAndOnlyServers()
    {
        var document = Document();
        var names = document.ChildTables("mcp_servers").Select(table => table.Path[^1]).ToArray();
        Assert.Equal(["filesystem", "legacy-tool", "docs"], names);
        Assert.DoesNotContain("env", names);
    }

    [Fact]
    public void AbsentMeansOnAndOnlyAWrittenFalseMeansOff()
    {
        var document = Document();
        Assert.Null(document.Table("mcp_servers", "filesystem")?.Value("enabled")?.BoolValue);
        Assert.False(document.Table("mcp_servers", "legacy-tool")?.Value("enabled")?.BoolValue);
    }

    [Fact]
    public void RemovingAServerHasToTakeItsEnvSubTableWithIt()
    {
        var document = Document();
        var under = document.TablesUnder(["mcp_servers", "filesystem"]);
        Assert.Equal([["mcp_servers", "filesystem", "env"]], under.Select(table => table.Path));
    }

    [Fact]
    public void AnInlineCodexServerIsShownAndNormalized()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            [mcp_servers]
            inline = { command = "run", args = ["x"], enabled = false, env = { API_TOKEN = "secret" } }
            """, @".codex\config.toml");

        var result = harness.Discover();
        var server = Assert.Single(result.Servers);

        Assert.Equal("inline", server.Name);
        Assert.Equal("run", server.Command);
        Assert.Equal(["x"], server.Args);
        Assert.Equal("secret", Assert.Single(server.Env).Value);
        Assert.Equal(Kytto.Core.Model.Enablement.Disabled, server.EnabledIn[ClientId.Codex]);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("inline table", StringComparison.Ordinal));
    }
}
