using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.Internal;

/// <summary>
/// Outgoing-message buffer with token-bucket rate limiting + priority
/// ordering + stale-Low coalescing. Per-window token budget refills at
/// the start of each window. High > Normal > Low when picking the next
/// send. New Low enqueues evict any older queued Low (stale tallies).
/// </summary>
public sealed class OutgoingMessageQueue : IDisposable {
    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly Func<string, Task> _send;

    private readonly Queue<string> _high = new();
    private readonly Queue<string> _normal = new();
    private readonly Queue<string> _low = new();   // single-element-effective due to coalescing

    private DateTimeOffset _windowStart;
    private int _tokens;
    private TaskCompletionSource? _drainTcs;
    private bool _drainPending;
    private bool _disposed;
    private readonly IDisposable _periodicTimer;

    public OutgoingMessageQueue(
        int capacity, TimeSpan window,
        IClock clock, ITimerScheduler scheduler,
        Func<string, Task> send) {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _capacity = capacity; _window = window;
        _clock = clock; _scheduler = scheduler; _send = send;
        _windowStart = clock.UtcNow;
        _tokens = capacity;
        // Tick at every window boundary (refill) and on enqueue (Pulse).
        _periodicTimer = scheduler.SchedulePeriodic(window, RefillAndDrain);
    }

    public void Enqueue(string message, OutgoingMessagePriority priority) {
        if (_disposed) return;
        switch (priority) {
            case OutgoingMessagePriority.High:   _high.Enqueue(message); break;
            case OutgoingMessagePriority.Normal: _normal.Enqueue(message); break;
            case OutgoingMessagePriority.Low:    _low.Clear(); _low.Enqueue(message); break;
        }
        // Defer drain to the next scheduler tick rather than draining synchronously.
        // This lets a burst of enqueues coalesce and order by priority before any
        // send happens — close-receipt-of-vote-N firing back-to-back with open-
        // receipt-of-vote-N+1 from a Closed handler must produce close-then-open
        // regardless of code-order, which sync-drain breaks.
        if (!_drainPending) {
            _drainPending = true;
            _scheduler.Schedule(TimeSpan.Zero, RunDeferredDrain);
        }
    }

    private void RunDeferredDrain() {
        _drainPending = false;
        Drain();
    }

    public Task DrainAsync() {
        if (_high.Count + _normal.Count + _low.Count == 0) return Task.CompletedTask;
        _drainTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _drainTcs.Task;
    }

    private void RefillAndDrain() {
        // Reset window every `_window` regardless of how many tokens were used.
        _windowStart = _clock.UtcNow;
        _tokens = _capacity;
        Drain();
    }

    private void Drain() {
        while (_tokens > 0) {
            var msg = TryDequeueHighestPriority();
            if (msg is null) break;
            _tokens--;
            var sendTask = _send(msg);
            _ = sendTask.ContinueWith(
                t => TiLog.Warn($"OutgoingMessageQueue: send failed: {t.Exception?.GetBaseException().Message}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }
        if (_high.Count + _normal.Count + _low.Count == 0) {
            _drainTcs?.TrySetResult();
            _drainTcs = null;
        }
    }

    private string? TryDequeueHighestPriority() {
        if (_high.TryDequeue(out var h)) return h;
        if (_normal.TryDequeue(out var n)) return n;
        if (_low.TryDequeue(out var l)) return l;
        return null;
    }

    public void Dispose() {
        _disposed = true;
        _periodicTimer.Dispose();
        _drainTcs?.TrySetCanceled();
    }
}
