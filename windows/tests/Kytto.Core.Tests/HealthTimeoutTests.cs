using Kytto.Core.Health;

namespace Kytto.Core.Tests;

public sealed class HealthTimeoutTests
{
    [Theory]
    [InlineData(0.01, "within 1 second")]
    [InlineData(1.01, "within 2 seconds")]
    public void TimeoutMessagesUseAtLeastOneRoundedSecond(double duration, string expected)
    {
        var error = ProcessException.TimedOut(duration);

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
