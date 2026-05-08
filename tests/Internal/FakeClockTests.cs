using System;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class FakeClockTests {
    [Fact]
    public void StartsAtConstructorTime() {
        var t0 = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(t0);
        Assert.Equal(t0, clock.UtcNow);
    }

    [Fact]
    public void AdvanceMovesNowForward() {
        var t0 = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(t0);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(t0 + TimeSpan.FromSeconds(30), clock.UtcNow);
    }

    [Fact]
    public void AdvanceWithNegativeThrows() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }
}
