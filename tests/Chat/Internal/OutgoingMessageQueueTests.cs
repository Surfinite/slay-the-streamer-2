using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.Internal;

public class OutgoingMessageQueueTests {
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeTimerScheduler _scheduler;
    private readonly List<string> _sent = new();

    public OutgoingMessageQueueTests() {
        _scheduler = new FakeTimerScheduler(_clock);
    }

    private OutgoingMessageQueue New(int capacity = 90, TimeSpan? window = null) {
        return new OutgoingMessageQueue(
            capacity: capacity,
            window: window ?? TimeSpan.FromSeconds(30),
            clock: _clock,
            scheduler: _scheduler,
            send: s => { _sent.Add(s); return Task.CompletedTask; });
    }

    [Fact]
    public void SingleEnqueue_SendsAfterScheduledDrain_WhenTokensAvailable() {
        var q = New();
        q.Enqueue("hi", OutgoingMessagePriority.Normal);
        _scheduler.Advance(TimeSpan.Zero);   // fires the deferred drain scheduled by Enqueue
        Assert.Single(_sent);
        Assert.Equal("hi", _sent[0]);
    }

    [Fact]
    public void PriorityOrder_HighBeforeNormalBeforeLow() {
        var q = New(capacity: 1, window: TimeSpan.FromSeconds(30));
        // Burst: 3 messages enqueued before any drain runs (deferred drain is the
        // key — Enqueue doesn't drain synchronously, so all three queue first and
        // the priority pick happens against the full set).
        q.Enqueue("low",  OutgoingMessagePriority.Low);
        q.Enqueue("norm", OutgoingMessagePriority.Normal);
        q.Enqueue("high", OutgoingMessagePriority.High);
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Single(_sent);
        Assert.Equal("high", _sent[0]);     // first token spent on highest-priority

        _scheduler.Advance(TimeSpan.FromSeconds(30));   // window refills, 1 more token
        Assert.Equal(2, _sent.Count);
        Assert.Equal("norm", _sent[1]);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(3, _sent.Count);
        Assert.Equal("low", _sent[2]);
    }

    [Fact]
    public void LowCoalescesStale_WhenAnotherLowEnqueued() {
        var q = New(capacity: 1, window: TimeSpan.FromSeconds(30));
        // Three Low enqueues with no drain in between → first two coalesce away.
        q.Enqueue("tally1", OutgoingMessagePriority.Low);
        q.Enqueue("tally2", OutgoingMessagePriority.Low);
        q.Enqueue("tally3", OutgoingMessagePriority.Low);
        _scheduler.Advance(TimeSpan.Zero);   // deferred drain fires
        Assert.Single(_sent);
        Assert.Equal("tally3", _sent[0]);   // only the latest-Low survives
    }

    [Fact]
    public void RateLimit_NeverExceedsCapacityPerWindow() {
        var q = New(capacity: 3, window: TimeSpan.FromSeconds(30));
        for (int i = 0; i < 10; i++)
            q.Enqueue($"m{i}", OutgoingMessagePriority.Normal);

        _scheduler.Advance(TimeSpan.FromSeconds(0));
        Assert.Equal(3, _sent.Count);   // capacity exhausted on first drain

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(6, _sent.Count);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(9, _sent.Count);

        _scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(10, _sent.Count);   // last one drains
    }

    [Fact]
    public async Task DrainAsync_FlushesPendingMessages_RespectingRateLimit() {
        var q = New(capacity: 2, window: TimeSpan.FromSeconds(30));
        q.Enqueue("a", OutgoingMessagePriority.Normal);
        q.Enqueue("b", OutgoingMessagePriority.Normal);
        q.Enqueue("c", OutgoingMessagePriority.Normal);
        _scheduler.Advance(TimeSpan.Zero);
        Assert.Equal(2, _sent.Count);

        var drainTask = q.DrainAsync();
        _scheduler.Advance(TimeSpan.FromSeconds(30));
        await drainTask;
        Assert.Equal(3, _sent.Count);
    }
}
