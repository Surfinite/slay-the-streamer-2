using System;

namespace SlayTheStreamer2.Ti.Internal;

/// <summary>
/// Schedules one-shot and periodic callbacks. Inject so tests can drive
/// timers deterministically via FakeTimerScheduler instead of relying on
/// real wall-clock System.Threading.Timer.
/// </summary>
public interface ITimerScheduler {
    /// <summary>Fires <paramref name="callback"/> once after <paramref name="delay"/>.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);

    /// <summary>Fires <paramref name="callback"/> every <paramref name="interval"/>, starting at <c>now + interval</c>.</summary>
    IDisposable SchedulePeriodic(TimeSpan interval, Action callback);
}
