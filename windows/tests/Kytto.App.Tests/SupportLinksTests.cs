using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Kytto.App.Ipc;
using Kytto.App.Web;

namespace Kytto.App.Tests;

/// <summary>
/// The two fixed report links and the one policy that lets a page open them (§7.14).
/// </summary>
public sealed class SupportLinksTests
{
    [Fact]
    public void TheReportLinksAreTheTwoFixedAddressesAndNothingElse()
    {
        Assert.Equal("https://kytto.jakubhecht.sk/#report-bug", SupportLinks.BugReport.Url.AbsoluteUri);
        Assert.Equal(
            "https://github.com/heyitsjakub/KyttoMCP/issues/new/choose",
            SupportLinks.GitHubIssue.Url.AbsoluteUri);
        Assert.Equal(new[] { SupportLinks.BugReport, SupportLinks.GitHubIssue }, SupportLinks.All);
        Assert.Equal("Report a bug…", SupportLinks.BugReport.Label);
        Assert.Equal("Report on GitHub…", SupportLinks.GitHubIssue.Label);
        Assert.Equal("Private · No account", SupportLinks.BugReport.Audience);
        Assert.Equal("Public · GitHub account", SupportLinks.GitHubIssue.Audience);
    }

    [Fact]
    public void TheReportAddressesCarryNoDataOnlyTheFormsOwnAnchor()
    {
        foreach (var link in SupportLinks.All)
        {
            Assert.Equal("https", link.Url.Scheme);
            Assert.Equal("", link.Url.Query);
            Assert.Equal("", link.Url.UserInfo);
            Assert.True(link.Url.IsDefaultPort);
        }

        // The anchor is the only thing that is not the path, and it is fixed.
        Assert.Equal("#report-bug", SupportLinks.BugReport.Url.Fragment);
        Assert.Equal("", SupportLinks.GitHubIssue.Url.Fragment);
    }

    [Fact]
    public void TheWebLayerSpellsTheSameTwoAddresses()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "..", "web", "js", "support.js"));

        Assert.Contains($"'{SupportLinks.BugReportAddress}'", script, StringComparison.Ordinal);
        Assert.Contains($"'{SupportLinks.GitHubIssueAddress}'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", true)]
    [InlineData("https://KYTTO.jakubhecht.sk/#report-bug", true)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues/new/choose", true)]
    // Anything added to, or different from, the fixed address is a different address.
    [InlineData("https://kytto.jakubhecht.sk/#report-bug&error=boom", false)]
    [InlineData("https://kytto.jakubhecht.sk/?error=boom#report-bug", false)]
    [InlineData("https://kytto.jakubhecht.sk/#other", false)]
    [InlineData("https://kytto.jakubhecht.sk/", false)]
    [InlineData("https://kytto.jakubhecht.sk/#", false)]
    [InlineData("http://kytto.jakubhecht.sk/#report-bug", false)]
    [InlineData("https://user@kytto.jakubhecht.sk/#report-bug", false)]
    [InlineData("https://kytto.jakubhecht.sk.evil.example/#report-bug", false)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues/new/choose?title=C%3A%5CUsers", false)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues/new/choose#x", false)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues/new", false)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP", false)]
    [InlineData("https://github.com/someone-else/KyttoMCP/issues/new/choose", false)]
    [InlineData("https://app/index.html", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("kytto.jakubhecht.sk/#report-bug", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheExactSupportAddressesAreSupportLinks(string? address, bool expected) =>
        Assert.Equal(expected, SupportLinks.IsSupportLink(address));

    [Theory]
    // The page's own origin, always.
    [InlineData("https://app/index.html", true, NavigationDecision.Allow)]
    [InlineData("https://app/index.html", false, NavigationDecision.Allow)]
    // The two links, but only when the user did the navigating.
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", true, NavigationDecision.OpenInBrowser)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues/new/choose", true, NavigationDecision.OpenInBrowser)]
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", false, NavigationDecision.Block)]
    // Nothing broader: not the same host with more on it, not any other web address.
    [InlineData("https://kytto.jakubhecht.sk/#report-bug&error=boom", true, NavigationDecision.Block)]
    [InlineData("https://kytto.jakubhecht.sk/", true, NavigationDecision.Block)]
    [InlineData("https://example.test/", true, NavigationDecision.Block)]
    [InlineData("http://example.test/", true, NavigationDecision.Block)]
    [InlineData("https://github.com/heyitsjakub/KyttoMCP/issues", true, NavigationDecision.Block)]
    [InlineData("mailto:someone@example.test", true, NavigationDecision.Block)]
    [InlineData("ms-settings:privacy", true, NavigationDecision.Block)]
    [InlineData("file:///C:/", true, NavigationDecision.Block)]
    [InlineData("not a uri", true, NavigationDecision.Block)]
    [InlineData(null, true, NavigationDecision.Block)]
    public void TheNavigationPolicyAllowsTheAppAndOpensExactlyTheSupportLinks(
        string? address,
        bool userInitiated,
        NavigationDecision expected) =>
        Assert.Equal(expected, ExternalNavigationPolicy.Decide(address, WebHost.TrustedOrigin, userInitiated));

    [Theory]
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", true, NavigationDecision.OpenInBrowser)]
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", false, NavigationDecision.Block)]
    // The app has no browser surface, so even its own origin gets no new window.
    [InlineData("https://app/index.html", true, NavigationDecision.Block)]
    [InlineData("https://example.test/", true, NavigationDecision.Block)]
    public void ANewWindowCanOnlyEverOpenASupportLinkInTheBrowser(
        string address,
        bool userInitiated,
        NavigationDecision expected) =>
        Assert.Equal(expected, ExternalNavigationPolicy.DecideNewWindow(address, WebHost.TrustedOrigin, userInitiated));

    [Theory]
    [InlineData("https://app/index.html", NavigationDecision.Allow)]
    [InlineData("https://kytto.jakubhecht.sk/#report-bug", NavigationDecision.Block)]
    [InlineData("https://example.test/", NavigationDecision.Block)]
    public void AFrameMayLoadTheAppOriginAndNothingElse(string address, NavigationDecision expected) =>
        Assert.Equal(expected, ExternalNavigationPolicy.DecideFrame(address, WebHost.TrustedOrigin));

    [Fact]
    public void TheSystemBrowserIsStartedWithTheFixedAddressThroughTheShell()
    {
        ProcessStartInfo? started = null;
        var launcher = new SystemBrowserSupportLauncher(info => started = info);

        Assert.True(launcher.Open(SupportLinks.BugReport.Url));

        Assert.NotNull(started);
        Assert.Equal("https://kytto.jakubhecht.sk/#report-bug", started.FileName);
        Assert.True(started.UseShellExecute);
        // The address is the whole command: nothing is passed along with it.
        Assert.Equal("", started.Arguments);
    }

    [Fact]
    public void TheLauncherNeverHandsAnythingButAFixedAddressToTheShell()
    {
        var starts = 0;
        var launcher = new SystemBrowserSupportLauncher(_ => starts++);

        Assert.False(launcher.Open(new Uri("https://example.test/")));
        Assert.False(launcher.Open(new Uri("https://kytto.jakubhecht.sk/#report-bug?error=C%3A%5Csecret")));
        Assert.False(launcher.Open(new Uri("file:///C:/Windows/System32/cmd.exe")));
        Assert.Equal(0, starts);
    }

    [Fact]
    public void ABrowserThatCannotStartIsReportedNotThrown()
    {
        var launcher = new SystemBrowserSupportLauncher(_ => throw new Win32Exception("no browser"));

        Assert.False(launcher.Open(SupportLinks.GitHubIssue.Url));
    }

    [Fact]
    public void TheFallbackMessageNamesTheFixedAddressAndNothingElse()
    {
        var message = SupportLinks.FallbackMessage(SupportLinks.BugReport);

        Assert.Contains("https://kytto.jakubhecht.sk/#report-bug", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttachmentNoteSaysNothingIsAttachedAutomatically() =>
        Assert.Contains("No logs or configuration are attached automatically", SupportLinks.AttachmentNote, StringComparison.Ordinal);

    // MARK: - The transport tag that keeps a reply from crossing a reload

    [Fact]
    public void ARequestsSessionTagIsReadAndEchoedInItsReply()
    {
        var message = CommandRouter.DecodeIncoming(
            """{ "id": 4, "session": "3f2c", "command": "state.get", "payload": null }""",
            out _);

        Assert.NotNull(message);
        Assert.Equal("3f2c", message.Session);

        using var reply = JsonDocument.Parse(CommandRouter.ReplyJson(message.Id, """{"ok":true}""", message.Session));
        Assert.Equal(4, reply.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("3f2c", reply.RootElement.GetProperty("session").GetString());
        Assert.Equal("""{"ok":true}""", reply.RootElement.GetProperty("reply").GetString());
    }

    [Theory]
    [InlineData("""{ "id": 1, "command": "state.get" }""")]
    [InlineData("""{ "id": 1, "session": 7, "command": "state.get" }""")]
    [InlineData("""{ "id": 1, "session": "", "command": "state.get" }""")]
    public void AMissingOrMalformedSessionTagIsSimplyAbsent(string json)
    {
        var message = CommandRouter.DecodeIncoming(json, out _);

        Assert.NotNull(message);
        Assert.Null(message.Session);
    }

    [Fact]
    public void AnOversizedSessionTagIsNotEchoed()
    {
        var message = CommandRouter.DecodeIncoming(
            $$"""{ "id": 1, "session": "{{new string('x', 65)}}", "command": "state.get" }""",
            out _);

        Assert.NotNull(message);
        Assert.Null(message.Session);
    }

    internal static string RepositoryRoot()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "KyttoMCP.slnx"))) return candidate.FullName;
            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate KyttoMCP.slnx above {AppContext.BaseDirectory}.");
    }
}
