using HassHwStats.Core;
using Xunit;

namespace HassHwStats.Tests;

public class BackoffTests
{
    private static readonly DateTime T0 = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Doubles_up_to_the_cap_and_resets()
    {
        var b = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        Assert.True(b.IsDue(T0));
        var now = T0;
        var waits = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            b.Failed(now);
            waits.Add(b.Remaining(now).TotalSeconds);
            Assert.False(b.IsDue(now));
            now += b.Remaining(now);
            Assert.True(b.IsDue(now));
        }
        Assert.Equal([2, 4, 8, 16, 30, 30], waits);
        Assert.Equal(6, b.Attempt);

        b.Reset();
        Assert.True(b.IsDue(now));
        b.Failed(now);
        Assert.Equal(2, b.Remaining(now).TotalSeconds);
    }

    [Fact]
    public void Restart_after_waits_then_starts_short()
    {
        var b = new Backoff(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        for (int i = 0; i < 5; i++) b.Failed(T0);
        b.RestartAfter(T0, TimeSpan.FromSeconds(3));
        Assert.False(b.IsDue(T0.AddSeconds(2)));
        Assert.True(b.IsDue(T0.AddSeconds(3)));
        b.Failed(T0.AddSeconds(3));
        Assert.Equal(2, b.Remaining(T0.AddSeconds(3)).TotalSeconds);
    }
}
