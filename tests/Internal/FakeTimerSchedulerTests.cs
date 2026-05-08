using System;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Internal;

public class FakeTimerSchedulerTests {
    [Fact]
    public void OneShotFiresOnceAtScheduledTime() {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        scheduler.Schedule(TimeSpan.FromSeconds(5), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fired);

        scheduler.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);

        scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fired);                                   // one-shot: doesn't refire
    }

    [Fact]
    public void PeriodicFiresRepeatedlyAtInterval() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        scheduler.SchedulePeriodic(TimeSpan.FromSeconds(7), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(20));              // 7, 14 — fires twice
        Assert.Equal(2, fired);

        scheduler.Advance(TimeSpan.FromSeconds(7));               // 21 — fires a third time
        Assert.Equal(3, fired);
    }

    [Fact]
    public void DisposedHandleStopsFiring() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var fired = 0;
        var handle = scheduler.SchedulePeriodic(TimeSpan.FromSeconds(7), () => fired++);

        scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(1, fired);

        handle.Dispose();
        scheduler.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void AdvancingDoesNotMoveClockBackward() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void SchedulePeriodic_RejectsZeroInterval() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            sched.SchedulePeriodic(TimeSpan.Zero, () => { }));
    }
}
