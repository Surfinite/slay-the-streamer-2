using System;
using System.Threading;

namespace SlayTheStreamer2.Ti.Internal;

public sealed class SystemTimerScheduler : ITimerScheduler {
    public IDisposable Schedule(TimeSpan delay, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return timer;
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(interval, interval);
        return timer;
    }
}
