using Kytto.Core.Clients;
using Xunit;

namespace Kytto.Core.Tests;

/// <summary>Which clients "Restart now" applies to, and what it cannot reach (issue #14).</summary>
public sealed class ClientRestartDescriptorTests
{
    [Theory]
    [InlineData(ClientId.ClaudeDesktop)]
    [InlineData(ClientId.Cursor)]
    [InlineData(ClientId.VsCode)]
    [InlineData(ClientId.Antigravity)]
    [InlineData(ClientId.DevinDesktop)]
    public void DesktopAppsThatReadTheirConfigAtLaunchRestartWithTheApp(ClientId client)
    {
        Assert.True(ClientRegistry.Descriptor(client).RestartsWithApp);
    }

    [Theory]
    [InlineData(ClientId.ClaudeCode)]
    [InlineData(ClientId.Codex)]
    [InlineData(ClientId.OpenCode)]
    public void TerminalFirstClientsAreNotRestartedWithAnApp(ClientId client)
    {
        // Restarting their desktop app would clear the notice while terminal sessions keep the old config.
        Assert.False(ClientRegistry.Descriptor(client).RestartsWithApp);
        Assert.Null(ClientRegistry.Descriptor(client).RestartCaveat);
    }

    [Fact]
    public void ARestartableClientWithLauncherExecutablesSaysWhatItDoesNotReach()
    {
        var caveat = ClientRegistry.Descriptor(ClientId.Antigravity).RestartCaveat;

        Assert.Equal(
            "Terminal sessions started with `agy` or `antigravity` keep the old configuration until you restart them.",
            caveat);
    }

    [Fact]
    public void ARestartableClientWithoutLauncherExecutablesHasNoCaveat()
    {
        Assert.Null(ClientRegistry.Descriptor(ClientId.Cursor).RestartCaveat);
    }
}
