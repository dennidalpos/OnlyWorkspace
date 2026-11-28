using OnlyWorkspace.Outlook.UI;

namespace OnlyWorkspace.Tests;

/// <summary>#45: the overall time cap of a generation tells its own expiry from a stop by the user.</summary>
public sealed class GenerationLimitTests
{
    [Fact]
    public async Task Cap_WhenTheTimeRunsOut_IsExpiredAndCancels()
    {
        using var limit = GenerationLimit.Start(CancellationToken.None, TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, limit.Token));
        Assert.True(limit.Expired);
        Assert.Contains("600", GenerationLimit.Start(CancellationToken.None).Timeout().Message);
    }

    [Fact]
    public void Cap_WhenTheUserStops_IsNotExpired()
    {
        using var user = new CancellationTokenSource();
        using var limit = GenerationLimit.Start(user.Token);
        user.Cancel();
        Assert.True(limit.Token.IsCancellationRequested);
        Assert.False(limit.Expired);
    }

    [Fact]
    public void Cap_WhenNothingHappens_IsNotExpired()
    {
        using var limit = GenerationLimit.Start(CancellationToken.None);
        Assert.False(limit.Expired);
    }
}
