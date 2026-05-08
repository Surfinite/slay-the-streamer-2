using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Clock under explicit test control.</summary>
public sealed class FakeClock : IClock {
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) { _now = start; }
    public DateTimeOffset UtcNow => _now;

    /// <summary>Advance the clock by <paramref name="delta"/>. Throws on negative.</summary>
    public void Advance(TimeSpan delta) {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "FakeClock.Advance must be non-negative.");
        _now += delta;
    }
}
