using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Tests.Support;

namespace Kytto.Core.Tests;

public sealed class ClaudeCodeProjectTests
{
    [Fact]
    public void ClaudeCodeProjectsBecomeDeterministicReadOnlyWorkspaceClients()
    {
        using var harness = new ToggleHarness();
        var path = @"C:\Projects\Demo";
        var contents = $$"""
            {
              "mcpServers": { "global": { "command": "node" } },
              "projects": {
                "{{path.Replace("\\", "\\\\", StringComparison.Ordinal)}}": {
                  "mcpServers": { "workspace": { "command": "python" } }
                }
              }
            }
            """;
        harness.Write(contents, @".claude.json");
        var before = harness.Text(@".claude.json");

        var result = harness.Discover();
        var project = Assert.Single(result.Clients,
            client => client.ConfigurationScope == Kytto.Core.Settings.ConfigurationScope.Workspace);
        var projectID = Discovery.ProjectClientID(path);
        var workspace = Assert.Single(result.Servers, server => server.Name == "workspace");

        Assert.Equal(projectID, project.Id);
        Assert.Equal("Claude Code · …\\Demo", project.DisplayName);
        Assert.Equal("…\\Demo", project.ShortName);
        Assert.Equal(path, project.ScopeLabel);
        Assert.True(project.IsReadOnly);
        Assert.Equal("workspace", workspace.Name);
        Assert.True(workspace.IsReadOnly);
        Assert.Equal(Enablement.Enabled, workspace.EnabledIn[projectID]);
        Assert.Equal(before, harness.Text(@".claude.json"));
    }

    [Fact]
    public void AProjectIdKeepsTheExactPathIdentityAndIsOpaqueOnTheWire()
    {
        var first = Discovery.ProjectClientID(@"C:\Projects\Demo");
        var same = Discovery.ProjectClientID(@"C:\Projects\Demo");
        var differentSpelling = Discovery.ProjectClientID(@"C:\Projects\Demo\");

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentSpelling);
        Assert.StartsWith("custom.", first.Raw(), StringComparison.Ordinal);
        Assert.DoesNotContain("Projects", first.Raw(), StringComparison.Ordinal);
        Assert.Null(first.BuiltIn);
    }

    [Fact]
    public void OnlyProjectScopesThatActuallyConfigureAServerBecomeClients()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            {
              "projects": {
                "C:\\Users\\you\\source\\repos\\empty-one": { "mcpServers": {} },
                "C:\\Users\\you\\source\\repos\\real": {
                  "mcpServers": { "workspace": { "command": "node" } }
                },
                "C:\\Users\\you\\source\\repos\\empty-two": { "mcpServers": {} }
              }
            }
            """, @".claude.json");

        var result = harness.Discover();

        var project = Assert.Single(result.Clients,
            client => client.ConfigurationScope == Kytto.Core.Settings.ConfigurationScope.Workspace);
        Assert.Equal(@"C:\Users\you\source\repos\real", project.ScopeLabel);
        Assert.True(project.IsReadOnly);
        Assert.Equal(1, project.ServerCount);
    }

    [Fact]
    public void ProjectDiscoveryDoesNotDependOnAGlobalServerMap()
    {
        using var harness = new ToggleHarness();
        harness.Write("""
            {
              "projects": {
                "/work/demo": {
                  "mcpServers": { "workspace": { "command": "node" } }
                }
              }
            }
            """, @".claude.json");

        var result = harness.Discover();

        Assert.Contains(result.Clients, client =>
            client.ConfigurationScope == Kytto.Core.Settings.ConfigurationScope.Workspace);
        Assert.Contains(result.Servers, server => server.Name == "workspace");
    }

    [Fact]
    public void CompactProjectLeavesUseTheSmallestDistinguishingWindowsSuffix()
    {
        var compact = Discovery.CompactProjectLeaves([
            @"C:\Users\you\Projects\shop",
            @"C:\Users\you\Projects\docs",
            @"C:\a\work\repo",
            @"C:\b\work\repo",
        ]);

        Assert.Equal(@"…\shop", compact[@"C:\Users\you\Projects\shop"]);
        Assert.Equal(@"…\docs", compact[@"C:\Users\you\Projects\docs"]);
        Assert.Equal(@"…\a\work\repo", compact[@"C:\a\work\repo"]);
        Assert.Equal(@"…\b\work\repo", compact[@"C:\b\work\repo"]);
    }

    [Fact]
    public void CompactProjectLeavesKeepWholeRootsUnchanged()
    {
        var compact = Discovery.CompactProjectLeaves([@"C:\", "/"]);

        Assert.Equal(@"C:\", compact[@"C:\"]);
        Assert.Equal("/", compact["/"]);
    }
}
