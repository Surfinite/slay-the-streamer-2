using System;

namespace SlayTheStreamer2.Ti.Internal;

public sealed class SystemClock : IClock {
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
