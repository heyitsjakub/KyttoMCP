using Kytto.App;
using Xunit;

namespace Kytto.App.Tests;

/// <summary>
/// Where a maximised frameless window goes (issue #15). The Win32 call itself is
/// exercised by the real-window check in the release notes; these pin the geometry.
/// </summary>
public sealed class MaximizedBoundsTests
{
    // A 1920×1080 monitor with a 48 px bottom taskbar: the work area stops above it.
    private static readonly ScreenRect Work = new(0, 0, 1920, 1032);

    [Fact]
    public void WithoutAnAutoHideTaskbarTheWorkAreaIsTheAnswer()
    {
        Assert.Equal(Work, MaximizedBounds.Compute(Work, []));
    }

    [Fact]
    public void AnAutoHideTaskbarKeepsOnePixelOnItsOwnEdgeOnly()
    {
        var fit = MaximizedBounds.Compute(new ScreenRect(0, 0, 1920, 1080), [ScreenEdge.Bottom]);

        Assert.Equal(new ScreenRect(0, 0, 1920, 1079), fit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryEdgeCanCarryTheAutoHideStrip(int edgeIndex)
    {
        var edge = (ScreenEdge)edgeIndex;
        var monitor = new ScreenRect(0, 0, 1920, 1080);

        var fit = MaximizedBounds.Compute(monitor, [edge]);

        // Exactly one pixel comes off that edge and nothing else moves.
        var expected = new ScreenRect(
            monitor.Left + (edge == ScreenEdge.Left ? 1 : 0),
            monitor.Top + (edge == ScreenEdge.Top ? 1 : 0),
            monitor.Right - (edge == ScreenEdge.Right ? 1 : 0),
            monitor.Bottom - (edge == ScreenEdge.Bottom ? 1 : 0));
        Assert.Equal(expected, fit);
    }

    [Fact]
    public void AMonitorThatIsNotAtTheOriginKeepsItsOwnWorkArea()
    {
        // A second monitor to the right of the primary: its work area starts at x = 1920.
        var secondary = new ScreenRect(1920, 0, 3840, 1040);

        var fit = MaximizedBounds.Compute(secondary, []);

        Assert.Equal(secondary, fit);
        Assert.Equal(1920, fit.Left);
    }
}
