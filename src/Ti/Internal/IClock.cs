using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>Source of "now". Inject so tests can use FakeClock.</summary>
public interface IClock {
    DateTimeOffset UtcNow { get; }
}
