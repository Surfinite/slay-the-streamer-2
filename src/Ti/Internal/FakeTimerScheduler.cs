using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Test-controlled scheduler. Fires due callbacks when Advance() is called.</summary>
public sealed class FakeTimerScheduler : ITimerScheduler {
    private readonly FakeClock _clock;
    private readonly List<Entry> _entries = new();

    public FakeTimerScheduler(FakeClock clock) { _clock = clock; }

    public IDisposable Schedule(TimeSpan delay, Action callback) {
        var entry = new Entry { NextFire = _clock.UtcNow + delay, Interval = null, Callback = callback };
        _entries.Add(entry);
        return new Handle(() => _entries.Remove(entry));
    }

    public IDisposable SchedulePeriodic(TimeSpan interval, Action callback) {
        var entry = new Entry { NextFire = _clock.UtcNow + interval, Interval = interval, Callback = callback };
        _entries.Add(entry);
        return new Handle(() => _entries.Remove(entry));
    }

    /// <summary>Advance the clock and fire any callbacks whose due time falls within the advance.</summary>
    public void Advance(TimeSpan delta) {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "FakeTimerScheduler.Advance must be non-negative.");
        var target = _clock.UtcNow + delta;
        // Walk forward firing in chronological order until we hit `target` with no due entries left.
        while (true) {
            Entry? next = null;
            foreach (var e in _entries)
                if (e.NextFire <= target && (next is null || e.NextFire < next.NextFire))
                    next = e;
            if (next is null) break;
            // advance the clock to that entry's fire time, then fire it
            _clock.Advance(next.NextFire - _clock.UtcNow);
            next.Callback();
            if (next.Interval is { } iv) next.NextFire += iv;
            else _entries.Remove(next);
        }
        // advance any remaining time the wall-clock should reflect even if no more callbacks
        if (_clock.UtcNow < target) _clock.Advance(target - _clock.UtcNow);
    }

    private sealed class Entry {
        public DateTimeOffset NextFire;
        public TimeSpan? Interval;
        public Action Callback = () => {};
    }

    private sealed class Handle : IDisposable {
        private Action? _onDispose;
        public Handle(Action onDispose) { _onDispose = onDispose; }
        public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
    }
}
