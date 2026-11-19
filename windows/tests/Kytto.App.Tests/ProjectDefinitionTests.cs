using System.IO;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.Core;
using Kytto.Core.Clients;
using Kytto.Core.Model;
using Kytto.Core.Secrets;
using Kytto.Core.Settings;
using KyttoJson = Kytto.Core.Json.JsonDocument;

namespace Kytto.App.Tests;

/// <summary>
/// Definition sources, project-definition imports and launch reviews, through the
/// model and the IPC router the page uses — the same scenarios the macOS app pins in
/// <c>ProjectDefinitionTests.swift</c>.
/// </summary>
public sealed class ProjectDefinitionTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "kytto-sources-tests", Guid.NewGuid().ToString("N"));

    private sealed class NoInstalledApps : IAppLocator
    {
        public bool ApplicationExists(string installKey, string home) => false;
        public bool PackageExists(string familyName) => false;
        public bool ExecutableExists(string name, string home) => false;
    }

    private string First => Path.Combine(_home, "one");
    private string Second => Path.Combine(_home, "two");
    private string Cursor => Path.Combine(_home, ".cursor", "mcp.json");
    private KyttoPaths Paths => new(Path.Combine(_home, "KyttoData"));

    private static string ProjectFile(string folder) => Path.Combine(folder, ".mcp.json");

    private AppModel Open() => new(Paths, _home, new NoInstalledApps(), new InMemorySecretStore(), watchConfigs: false);

    /// <param name="claudeKey">How Claude Code spells the first folder in its projects map.</param>
    private AppModel Fixture(string? claudeKey = null)
    {
        Write(Cursor, """{"theme":"dark","mcpServers":{}}""");
        Write(ProjectFile(First), """{"mcpServers":{"Demo":{"command":"first"}}}""");
        Write(ProjectFile(Second),
            """{"mcpServers":{"Demo":{"command":"second","args":["--mode"],"env":{"REF":"${env:SOURCE_TOKEN}","TOKEN":"private-fixture"}}}}""");
        var user = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object> { ["Demo"] = new { command = "user" } },
            ["projects"] = new Dictionary<string, object>
            {
                [claudeKey ?? First] = new { mcpServers = new Dictionary<string, object> { ["Demo"] = new { command = "local" } } },
            },
        };
        Write(Path.Combine(_home, ".claude.json"), JsonSerializer.Serialize(user));
        var model = Open();
        model.UpdateSettings(settings => settings with
        {
            HasCompletedOnboarding = true,
            ProjectFolders = [new ProjectFolder(Guid.NewGuid(), First), new ProjectFolder(Guid.NewGuid(), Second)],
        });
        return model;
    }

    private static ClientKey Source(AppModel model, string folder)
    {
        var chosen = model.Settings.ProjectFolders.Single(candidate =>
            string.Equals(candidate.Path, folder, StringComparison.OrdinalIgnoreCase));
        return ClientKey.Custom(ProjectFolders.SourceId(chosen.Id, ProjectFolders.Files[0]));
    }

    private async Task<ProjectCopyPreview> Preview(AppModel model)
    {
        await model.ReloadInBackgroundAsync();
        return model.PreviewProjectCopy("demo", Source(model, Second), ClientId.Cursor);
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task WorkspacePriorityAndSelectedCopy()
    {
        using var model = Fixture();
        var inventory = model.ToStateDto().Definitions!["demo"];
        Assert.Equal(4, inventory.Copies.Count);
        Assert.True(inventory.OverlapsScopes);
        var one = Assert.Single(inventory.Priorities, priority => priority.Workspace == First);
        Assert.Equal(3, one.Candidates.Count);
        Assert.Equal("Local · " + First, inventory.Copies.Single(copy => copy.ClientID == one.Winner).Scope);
        var two = Assert.Single(inventory.Priorities, priority => priority.Workspace == Second);
        Assert.Equal(Source(model, Second).Raw(), two.Winner);
        Assert.Equal("Project · " + Second, inventory.Copies.Single(copy => copy.ClientID == two.Winner).Scope);
        Assert.Equal("User", inventory.Copies.Single(copy => copy.ClientID == "claudeCode").Scope);

        var original = File.ReadAllBytes(ProjectFile(Second));
        var preview = await Preview(model);
        Assert.Equal(ProjectFile(Second), preview.SourceFile);
        Assert.Equal(Cursor, preview.TargetFile);
        Assert.Equal(new[] { "SOURCE_TOKEN" }, preview.UnresolvedVariables);
        Assert.Equal(new[] { "REF", "TOKEN" }, preview.EnvironmentKeys);
        Assert.Empty(preview.Blockers);
        Assert.DoesNotContain("private-fixture", JsonSerializer.Serialize(preview.ToDto(), Envelope.Json));

        var result = model.ApplyProjectCopy(Assert.IsType<string>(preview.Token));
        Assert.Equal(new[] { ClientId.Cursor }, result.Changed);
        Assert.True(result.BackupIDs.ContainsKey(ClientId.Cursor));
        var copy = model.ServerById("demo").DefinitionsByClient[ClientId.Cursor];
        Assert.Equal("second", copy.Command);
        Assert.Equal("private-fixture", copy.Env.Single(entry => entry.Key == "TOKEN").Value);
        Assert.Null((await Preview(model)).Token);

        model.RemoveServerFrom("demo", ClientId.Cursor);
        Assert.Equal(original, File.ReadAllBytes(ProjectFile(Second)));
        var cursor = KyttoJson.Parse(File.ReadAllText(Cursor)).Root;
        Assert.Equal("dark", cursor["theme"]?.StringValue);
        Assert.Null(cursor["mcpServers"]?["Demo"]);
    }

    /// <summary>
    /// Claude Code writes a Windows project key with forward slashes, and nothing stops
    /// it differing in case or a trailing separator from the folder chosen here. It is
    /// still the same workspace (§4).
    /// </summary>
    [Fact]
    public void AClaudeCodeProjectKeyMatchesTheFolderHoweverItIsSpelled()
    {
        var spelled = First.Replace('\\', '/').ToUpperInvariant() + "/";
        using var model = Fixture(spelled);
        var inventory = model.ToStateDto().Definitions!["demo"];
        var one = Assert.Single(inventory.Priorities, priority => priority.Workspace == First);
        Assert.Equal(3, one.Candidates.Count);
        Assert.Equal("Local · " + spelled, inventory.Copies.Single(copy => copy.ClientID == one.Winner).Scope);
        Assert.Equal(2, inventory.Priorities.Count);
    }

    [Fact]
    public async Task StaleAndUnsupportedCopiesNeverWrite()
    {
        using var model = Fixture();
        var token = Assert.IsType<string>((await Preview(model)).Token);
        Write(ProjectFile(Second), """{"mcpServers":{"Demo":{"command":"changed"}}}""");
        Assert.Throws<ProjectCopyException>(() => model.ApplyProjectCopy(token));

        var fresh = Assert.IsType<string>((await Preview(model)).Token);
        const string raced = """{"mcpServers":{"dEmO":{"command":"keep"}}}""";
        Write(Cursor, raced);
        Assert.Throws<ProjectCopyException>(() => model.ApplyProjectCopy(fresh));
        Assert.Equal(raced, File.ReadAllText(Cursor));

        Write(ProjectFile(Second),
            """{"mcpServers":{"Demo":{"url":"https://example.invalid","headers":{"Authorization":"secret-header"}}}}""");
        var preview = await Preview(model);
        Assert.Null(preview.Token);
        Assert.Contains(preview.Blockers, blocker => blocker.Contains("headers", StringComparison.Ordinal));
        Assert.Contains(preview.Blockers, blocker => blocker.Contains("already contains", StringComparison.Ordinal));
        Assert.DoesNotContain("secret-header", JsonSerializer.Serialize(preview.ToDto(), Envelope.Json));
        Assert.Empty(model.Backups.List(ClientId.Cursor));
        // A token that was never issued, or was already spent, writes nothing either.
        Assert.Throws<ProjectCopyException>(() => model.ApplyProjectCopy(fresh));
    }

    [Fact]
    public async Task ProjectInputsAndGatewayRoutesAreBlocked()
    {
        using var model = Fixture();
        Write(ProjectFile(Second), """{"mcpServers":{"Demo":{"command":"node","args":["${workspaceFolder}/server.js"]}}}""");
        var preview = await Preview(model);
        Assert.Null(preview.Token);
        Assert.Contains(preview.Blockers, blocker => blocker.Contains("original project context", StringComparison.Ordinal));

        Write(ProjectFile(Second),
            """{"mcpServers":{"Demo":{"command":"C:\\Tools\\kytto-mcp-proxy.exe","args":["--route","abc"]}}}""");
        preview = await Preview(model);
        Assert.Null(preview.Token);
        Assert.Contains(preview.Blockers, blocker => blocker.Contains("Gateway route", StringComparison.Ordinal));

        Write(ProjectFile(Second), """{"mcpServers":{"Demo":{"command":"./bin/server"}}}""");
        preview = await Preview(model);
        Assert.NotNull(preview.Token);
        Assert.Contains(preview.Warnings, warning => warning.Contains("Relative paths", StringComparison.Ordinal));
        Assert.Empty(model.Backups.List(ClientId.Cursor));
    }

    [Fact]
    public async Task ReviewTracksExternalSemanticsAndSurvivesRestart()
    {
        using (var model = Fixture())
        {
            model.ApplyProjectCopy(Assert.IsType<string>((await Preview(model)).Token));
            var text = File.ReadAllText(Cursor);
            Write(Cursor, "  \n" + text);
            await model.ReloadInBackgroundAsync();
            Assert.Empty(model.LaunchReviews.Alerts);

            model.CreateServer(new ServerDraft { Name = "Solo", Command = "original" }, [ClientId.Cursor]);
            model.UpdateServer(new ServerDraft { Name = "Solo", Command = "own-write" }, "solo");
            Assert.Empty(model.LaunchReviews.Alerts);

            Write(Cursor, """{"mcpServers":{"Solo":{"command":"outside-secret","args":["secret-arg"],"env":{"SECRET":"secret-val"}}}}""");
            await model.ReloadInBackgroundAsync();
            var first = Assert.Single(model.LaunchReviews.Alerts);
            Assert.Equal(Cursor, first.File);
            Assert.Equal("Global", first.Scope);
            Assert.Equal(new[] { "Command", "Arguments", "Environment" }, first.Fields.Select(field => field.Name));
            Assert.Contains(model.ToStateDto().LaunchReviews!, alert => alert.Token == first.Token);

            Write(Cursor, """{"mcpServers":{"Solo":{"type":"http","url":"https://host.invalid/?token=private-url"}}}""");
            await model.ReloadInBackgroundAsync();
            Assert.Throws<LaunchReviewStaleException>(() => model.AcknowledgeLaunchReview(first.Token));
            var latest = Assert.Single(model.LaunchReviews.Alerts);
            Assert.Contains(latest.Fields, field => field.Name == "Endpoint");
            // The first change's "before" is kept; only "after" moves on.
            Assert.Equal(first.Fields[1].Before, latest.Fields.Single(field => field.Name == "Arguments").Before);
        }

        var saved = File.ReadAllText(Paths.LaunchReviewsFile);
        foreach (var value in new[] { "outside-secret", "secret-arg", "secret-val", "private-url" })
        {
            Assert.DoesNotContain(value, saved);
        }

        string token;
        using (var reopened = Open())
        {
            var alert = Assert.Single(reopened.LaunchReviews.Alerts);
            Assert.Equal("solo", alert.ServerID);
            Assert.Contains(alert.Fields, field => field.Name == "Endpoint");
            token = alert.Token;
            reopened.AcknowledgeLaunchReview(token);
        }
        using var again = Open();
        Assert.Empty(again.LaunchReviews.Alerts);
        Assert.Null(again.LaunchReviews.StorageError);
    }

    [Fact]
    public async Task RestoringABackupIsNotAnExternalChange()
    {
        using var model = Fixture();
        model.CreateServer(new ServerDraft { Name = "Solo", Command = "original" }, [ClientId.Cursor]);
        // Taken just before the edit, so restoring it changes Solo's command back.
        var edit = model.UpdateServer(new ServerDraft { Name = "Solo", Command = "edited" }, "solo");
        model.Restore(edit.BackupIDs[ClientId.Cursor], ClientId.Cursor);
        Assert.Equal("original", model.ServerById("solo").DefinitionsByClient[ClientId.Cursor].Command);
        await model.ReloadInBackgroundAsync();
        Assert.Empty(model.LaunchReviews.Alerts);
    }

    [Fact]
    public async Task UnreadableReviewFileIsReportedAndNeverOverwritten()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.LaunchReviewsFile, "{ not json");
        using var model = Fixture();
        Assert.NotNull(model.ToStateDto().LaunchReviewStorageError);
        model.CreateServer(new ServerDraft { Name = "Solo", Command = "original" }, [ClientId.Cursor]);
        Write(Cursor, """{"mcpServers":{"Solo":{"command":"outside"}}}""");
        await model.ReloadInBackgroundAsync();
        Assert.Single(model.LaunchReviews.Alerts);
        Assert.Equal("{ not json", File.ReadAllText(Paths.LaunchReviewsFile));
    }

    [Fact]
    public async Task CommandsGoThroughTheRouter()
    {
        using var model = Fixture();
        var router = new CommandRouter();
        CommandRegistry.RegisterAll(router, model);

        using var info = JsonDocument.Parse(await router.InvokeForTests("app.info", "{}"));
        Assert.Contains(
            info.RootElement.GetProperty("data").GetProperty("capabilities").EnumerateArray(),
            capability => capability.GetString() == "project-copy");

        var source = Source(model, Second).Raw();
        var reply = await router.InvokeForTests("servers.projectCopyPreview",
            JsonSerializer.Serialize(new { serverID = "demo", sourceClientID = source, targetClientID = "cursor" }));
        Assert.DoesNotContain("private-fixture", reply);
        using var preview = JsonDocument.Parse(reply);
        var data = preview.RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal(ProjectFile(Second), data.GetProperty("sourceFile").GetString());

        using var applied = JsonDocument.Parse(await router.InvokeForTests("servers.applyProjectCopy",
            JsonSerializer.Serialize(new { token })));
        Assert.True(applied.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "cursor" }, applied.RootElement.GetProperty("data").GetProperty("changed")
            .EnumerateArray().Select(item => item.GetString()));

        using var undone = JsonDocument.Parse(await router.InvokeForTests("servers.removeFromClient",
            JsonSerializer.Serialize(new { serverID = "demo", clientID = "cursor" })));
        Assert.True(undone.RootElement.GetProperty("ok").GetBoolean());
        Assert.Null(KyttoJson.Parse(File.ReadAllText(Cursor)).Root["mcpServers"]?["Demo"]);

        using var spent = JsonDocument.Parse(await router.InvokeForTests("servers.applyProjectCopy",
            JsonSerializer.Serialize(new { token })));
        Assert.False(spent.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("appState", spent.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var acknowledged = JsonDocument.Parse(await router.InvokeForTests("launch.acknowledge",
            JsonSerializer.Serialize(new { token = "unknown" })));
        Assert.Equal("appState", acknowledged.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var badTarget = JsonDocument.Parse(await router.InvokeForTests("servers.projectCopyPreview",
            JsonSerializer.Serialize(new { serverID = "demo", sourceClientID = source, targetClientID = source })));
        Assert.Equal("appState", badTarget.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary profile is not worth hiding the assertion result.
        }
    }
}
