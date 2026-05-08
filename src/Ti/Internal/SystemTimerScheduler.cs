using System;
using System.Threading;

namespace SlayTheStreamer2.Ti.Internal;

public sealed class SystemTimerScheduler : ITimerScheduler {
    public IDisposable Schedule(TimeSpan delay, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return new Handle(timer);
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action callback) {
        var timer = new Timer(_ => callback());
        timer.Change(interval, interval);
        return new Handle(timer);
    }

    private sealed class Handle : IDisposable {
        private Timer? _timer;
        public Handle(Timer t) { _timer = t; }
        public void Dispose() {
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }
    }
}
