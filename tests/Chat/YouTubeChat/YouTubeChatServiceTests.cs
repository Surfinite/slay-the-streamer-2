using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

[Collection("TiLog.Sink")]
public class YouTubeChatServiceTests {
    [Fact]
    public void Initial_State_Is_Disconnected() {
        var svc = MakeService();
        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
    }

    [Fact]
    public void CanSend_Is_Always_False() {
        var svc = MakeService();
        Assert.False(svc.CanSend);
    }

    [Fact]
    public async Task SendMessageAsync_Throws_NotSupported() {
        var svc = MakeService();
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.SendMessageAsync("hello"));
    }

    [Fact]
    public void LastStatusReason_Initial_Is_None() {
        var svc = MakeService();
        Assert.Equal(YouTubeChatStatusReason.None, svc.LastStatusReason);
    }

    [Fact]
    public async Task ConnectAsync_Successful_Transitions_To_ConnectedReadOnly() {
        var svc = MakeService();
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ChatConnectionState.ConnectedReadOnly, svc.State);
        Assert.Equal(YouTubeChatStatusReason.None, svc.LastStatusReason);
    }

    [Fact]
    public async Task Discovery_Returns_Null_Transitions_To_Reconnecting() {
        var discovery = new StubDiscovery { NextResult = null };
        var svc = MakeService(discovery: discovery);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(YouTubeChatStatusReason.NoLiveBroadcastFound, svc.LastStatusReason);
    }

    [Fact]
    public async Task Escalation_Fires_At_30th_Consecutive_Reconnect() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var discovery = new StubDiscovery { NextResult = null };
        var svc = MakeService(discovery: discovery, clock: clock, scheduler: scheduler);
        var events = new List<YouTubeEscalationRequestedEventArgs>();
        svc.EscalationRequested += (_, e) => events.Add(e);
        await svc.ConnectAsync("UCfake");
        // Drive 30 consecutive reconnect cycles by advancing the scheduler past
        // each timer's delay. Each cycle re-runs the connect flow which hits
        // discovery == null and transitions Reconnecting again.
        for (int i = 0; i < 30; i++) {
            // Wait for the reconnect timer to be armed (state task ran on background).
            for (int j = 0; j < 200 && scheduler.PendingCount == 0; j++) await Task.Delay(5);
            Assert.True(scheduler.PendingCount > 0, $"timer missing at cycle {i}");
            scheduler.Advance(scheduler.NextDueDelay!.Value);
        }
        // Wait briefly for the 30th transition to settle.
        for (int j = 0; j < 50 && events.Count == 0; j++) await Task.Delay(10);
        Assert.Single(events);
        Assert.Equal(30, events[0].ConsecutiveReconnectCount);
        Assert.Equal(YouTubeChatStatusReason.NoLiveBroadcastFound, events[0].LastStatusReason);
        svc.Dispose();
    }

    [Fact]
    public async Task Escalation_Does_Not_Fire_If_Connection_Succeeds_Before_Threshold() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        // 29 fails, then success.
        var discovery = new ToggleDiscovery();
        for (int i = 0; i < 29; i++) discovery.Results.Enqueue(null);
        discovery.Results.Enqueue("UCgood");
        var svc = MakeService(discovery: discovery, clock: clock, scheduler: scheduler);
        var events = new List<YouTubeEscalationRequestedEventArgs>();
        svc.EscalationRequested += (_, e) => events.Add(e);
        await svc.ConnectAsync("UCfake");
        for (int i = 0; i < 29; i++) {
            for (int j = 0; j < 200 && scheduler.PendingCount == 0; j++) await Task.Delay(5);
            Assert.True(scheduler.PendingCount > 0, $"timer missing at cycle {i}");
            scheduler.Advance(scheduler.NextDueDelay!.Value);
        }
        // Wait briefly for the 30th attempt (success) to flow.
        for (int j = 0; j < 100 && svc.State != ChatConnectionState.ConnectedReadOnly; j++) await Task.Delay(10);
        Assert.Equal(ChatConnectionState.ConnectedReadOnly, svc.State);
        Assert.Empty(events);
        svc.Dispose();
    }

    [Fact]
    public async Task Escalation_OneShot_Until_Counter_Resets() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var discovery = new ToggleDiscovery();
        // Pattern: 30 fails -> success -> 30 fails. Should fire twice.
        for (int i = 0; i < 30; i++) discovery.Results.Enqueue(null);
        discovery.Results.Enqueue("UCgood");
        // After success, the steady-state poll loop runs and the connection-ended
        // case must transition back to Reconnecting. To keep this test simple,
        // we then arrange the next 30 attempts to also fail via repeat nulls.
        for (int i = 0; i < 30; i++) discovery.Results.Enqueue(null);

        // Use a scraper that returns null continuation immediately so the success
        // flow exits ConnectedReadOnly back to Reconnecting (LiveBroadcastEnded),
        // re-running connect flow which hits the next batch of nulls.
        var scraper = new StubScraperWithSequence();
        // For the one success: cursor-establishing poll returns valid continuation,
        // then steady-state poll returns null continuation -> back to Reconnecting.
        scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 50));
        scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), null, 0));

        var svc = MakeService(discovery: discovery, scraper: scraper, clock: clock, scheduler: scheduler);
        var events = new List<YouTubeEscalationRequestedEventArgs>();
        svc.EscalationRequested += (_, e) => events.Add(e);
        await svc.ConnectAsync("UCfake");
        // Burst 1: 30 fails -> escalation #1.
        for (int i = 0; i < 30; i++) {
            for (int j = 0; j < 200 && scheduler.PendingCount == 0; j++) await Task.Delay(5);
            Assert.True(scheduler.PendingCount > 0, $"timer missing at burst1 cycle {i}");
            scheduler.Advance(scheduler.NextDueDelay!.Value);
        }
        for (int j = 0; j < 100 && events.Count == 0; j++) await Task.Delay(10);
        Assert.Single(events);

        // Now the next discovery call returns "UCgood" -> ConnectedReadOnly
        // (steady-state poll then transitions back to Reconnecting via null continuation).
        // Wait for that round-trip to complete.
        for (int j = 0; j < 200 && scheduler.PendingCount == 0; j++) await Task.Delay(10);

        // Burst 2: 30 more fails -> escalation #2.
        for (int i = 0; i < 30; i++) {
            for (int j = 0; j < 200 && scheduler.PendingCount == 0; j++) await Task.Delay(5);
            Assert.True(scheduler.PendingCount > 0, $"timer missing at burst2 cycle {i}");
            scheduler.Advance(scheduler.NextDueDelay!.Value);
        }
        for (int j = 0; j < 100 && events.Count < 2; j++) await Task.Delay(10);
        Assert.Equal(2, events.Count);
        svc.Dispose();
    }

    [Fact]
    public async Task Reconnect_ArmsTimer_After_Reconnecting_Transition() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var discovery = new StubDiscovery { NextResult = null };   // forces NoLiveBroadcastFound
        var svc = MakeService(discovery: discovery, clock: clock, scheduler: scheduler);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Wait a brief moment for the ArmReconnect call to land (it happens on the
        // connect-task continuation after the state-change post).
        for (int i = 0; i < 30 && scheduler.PendingCount == 0; i++) await Task.Delay(10);
        Assert.True(scheduler.PendingCount > 0, "expected a pending reconnect timer");
        // First reconnect after a non-429 failure uses the 60s base ± 10s jitter.
        var delay = scheduler.NextDueDelay!.Value;
        Assert.InRange(delay.TotalSeconds, 50, 70);
        svc.Dispose();
    }

    [Fact]
    public async Task RateLimit_429_With_RetryAfter_Uses_Header() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var scraper = new StubScraperWithSequence();
        // Throw 429 with Retry-After=180s on the cursor-establishing poll.
        scraper.PollExceptions.Enqueue(new YouTubeHttpStatusException(
            System.Net.HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(180), "429"));
        var svc = MakeService(scraper: scraper, clock: clock, scheduler: scheduler);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 30 && scheduler.PendingCount == 0; i++) await Task.Delay(10);
        Assert.Equal(YouTubeChatStatusReason.RateLimited, svc.LastStatusReason);
        Assert.True(scheduler.PendingCount > 0);
        var delay = scheduler.NextDueDelay!.Value;
        // 180s + [0, +10s) jitter band.
        Assert.InRange(delay.TotalSeconds, 175, 195);
        svc.Dispose();
    }

    [Fact]
    public async Task Consecutive_429_Backs_Off_Exponentially() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var scraper = new StubScraperWithSequence();
        // Three 429s without Retry-After header → backoff 60→120→240.
        for (int i = 0; i < 3; i++) {
            scraper.PollExceptions.Enqueue(new YouTubeHttpStatusException(
                System.Net.HttpStatusCode.TooManyRequests, retryAfter: null, "429"));
        }
        var svc = MakeService(scraper: scraper, clock: clock, scheduler: scheduler);
        var observedDelays = new List<double>();
        async Task WaitForArmedTimer() {
            for (int i = 0; i < 200 && scheduler.PendingCount == 0; i++) await Task.Delay(10);
        }
        await svc.ConnectAsync("UCfake");

        // First 429: ~60s.
        await WaitForArmedTimer();
        observedDelays.Add(scheduler.NextDueDelay!.Value.TotalSeconds);
        // Fire the reconnect timer → re-runs connect flow → next 429.
        scheduler.Advance(scheduler.NextDueDelay!.Value);
        // Second 429: ~120s.
        for (int i = 0; i < 200 && (scheduler.PendingCount == 0 || scheduler.NextDueDelay!.Value.TotalSeconds < 100); i++)
            await Task.Delay(10);
        observedDelays.Add(scheduler.NextDueDelay!.Value.TotalSeconds);
        scheduler.Advance(scheduler.NextDueDelay!.Value);
        // Third 429: ~240s.
        for (int i = 0; i < 200 && (scheduler.PendingCount == 0 || scheduler.NextDueDelay!.Value.TotalSeconds < 200); i++)
            await Task.Delay(10);
        observedDelays.Add(scheduler.NextDueDelay!.Value.TotalSeconds);

        Assert.InRange(observedDelays[0], 50, 70);
        Assert.InRange(observedDelays[1], 110, 130);
        Assert.InRange(observedDelays[2], 230, 250);
        svc.Dispose();
    }

    [Fact]
    public async Task Dispose_Cancels_Pending_Reconnect_Timer() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var discovery = new StubDiscovery { NextResult = null };
        var svc = MakeService(discovery: discovery, clock: clock, scheduler: scheduler);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 30 && scheduler.PendingCount == 0; i++) await Task.Delay(10);
        Assert.True(scheduler.PendingCount > 0);
        svc.Dispose();
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task SteadyState_Emits_Subsequent_Poll_Messages() {
        var scraper = new StubScraperWithSequence();
        // Cursor-establishing poll: empty, short timeout so loop fires soon.
        scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 50));
        // First steady-state poll: should emit the message.
        scraper.PollResults.Enqueue(new PollResult(
            new[] { new ParsedChatMessage("UC1", "U1", "#0", false, false) },
            "CONT2", 60_000));
        var svc = MakeService(scraper: scraper);
        var received = new List<ChatMessage>();
        svc.MessageReceived += (_, m) => received.Add(m);
        await svc.ConnectAsync("UCfake");
        // Wait briefly for steady-state poll to run.
        // Poll loop clamps timeout to [1s,10s], so first steady-state poll happens after ~1s.
        for (int i = 0; i < 30 && received.Count == 0; i++) await Task.Delay(50);
        Assert.Single(received);
        Assert.Equal("#0", received[0].Text);
        Assert.StartsWith("yt:", received[0].UserId);
    }

    [Fact]
    public async Task SteadyState_NullContinuation_Transitions_To_Reconnecting_With_LiveBroadcastEnded() {
        var scraper = new StubScraperWithSequence();
        scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 50));   // cursor
        scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), null, 0));        // ended
        var svc = MakeService(scraper: scraper);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(YouTubeChatStatusReason.LiveBroadcastEnded, svc.LastStatusReason);
    }

    [Fact]
    public async Task Disconnect_Transitions_To_Disconnected() {
        var svc = MakeService();
        await svc.ConnectAsync("UCfake");
        // Allow connect flow to settle to a non-Disconnected state before Disconnect.
        for (int i = 0; i < 20 && svc.State == ChatConnectionState.Disconnected; i++) await Task.Delay(10);
        svc.Disconnect();
        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
    }

    [Fact]
    public async Task Initial_Cursor_Establishing_Poll_Does_Not_Emit_Messages() {
        // Cursor-establishing poll has 1 backlog message. Subsequent steady-state
        // polls also return messages, but with a long timeoutMs to avoid racing.
        var scraper = new StubScraperWithSequence();
        scraper.PollResults.Enqueue(new PollResult(
            new[] { new ParsedChatMessage("UC1", "U1", "#0", false, false) },
            "CONT1", 60_000));
        // Subsequent polls stall on the long-timeout fallback in the stub.
        var svc = MakeService(scraper: scraper);
        int received = 0;
        svc.MessageReceived += (_, _) => received++;
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, received);   // initial-poll backlog suppressed
    }

    [Fact]
    public async Task SetFastPolling_Wakes_InFlight_Delay_And_Polls_At_Fast_Interval() {
        // Steady-state timeout 60s clamps to PollMax (10s), so without fast
        // polling no steady poll would land inside this test's window.
        var scraper = new StubScraper {
            NextPollResult = new PollResult(Array.Empty<ParsedChatMessage>(), "CONT", 60_000),
        };
        var svc = MakeService(scraper: scraper);
        svc.FastPollInterval = TimeSpan.FromMilliseconds(40);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // ConnectedReadOnly fires BEFORE the poll-loop task starts; give the loop
        // time to park inside its 10s steady delay so the toggle exercises the
        // wake path rather than racing loop startup.
        await Task.Delay(150);
        int baseline = scraper.PollCallCount;   // cursor-establishing poll only

        svc.SetFastPolling(true);
        // Wake should interrupt the 10s steady delay; then ~40ms cadence.
        for (int i = 0; i < 100 && scraper.PollCallCount < baseline + 3; i++) await Task.Delay(20);
        Assert.True(scraper.PollCallCount >= baseline + 3,
            $"expected >=3 fast polls within 2s; got {scraper.PollCallCount - baseline}");
        svc.Dispose();
    }

    [Fact]
    public async Task SetFastPolling_Off_Returns_To_SteadyState_Cadence() {
        var scraper = new StubScraper {
            NextPollResult = new PollResult(Array.Empty<ParsedChatMessage>(), "CONT", 60_000),
        };
        var svc = MakeService(scraper: scraper);
        svc.FastPollInterval = TimeSpan.FromMilliseconds(40);
        var tcs = new TaskCompletionSource();
        svc.ConnectionStateChanged += (_, e) => {
            if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
        };
        await svc.ConnectAsync("UCfake");
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));

        svc.SetFastPolling(true);
        int baseline = scraper.PollCallCount;
        for (int i = 0; i < 100 && scraper.PollCallCount < baseline + 2; i++) await Task.Delay(20);
        Assert.True(scraper.PollCallCount >= baseline + 2, "fast polling never engaged");

        svc.SetFastPolling(false);
        await Task.Delay(150);                   // let any in-flight fast poll land
        int afterDisable = scraper.PollCallCount;
        await Task.Delay(400);                   // steady cadence is 10s — expect no growth
        Assert.True(scraper.PollCallCount <= afterDisable + 1,
            $"polling kept running fast after disable: {scraper.PollCallCount - afterDisable} extra polls");
        svc.Dispose();
    }

    // Helper — injects fake discovery/scraper/dispatcher/clock/scheduler.
    private static YouTubeChatService MakeService(
        IYouTubeLiveBroadcastDiscovery? discovery = null,
        IYouTubeLiveChatScraper? scraper = null,
        FakeClock? clock = null,
        FakeTimerScheduler? scheduler = null) {
        clock ??= new FakeClock(DateTimeOffset.UtcNow);
        scheduler ??= new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        return new YouTubeChatService(
            dispatcher, clock, scheduler,
            discovery ?? new StubDiscovery(),
            scraper ?? new StubScraper());
    }
}

internal sealed class StubDiscovery : IYouTubeLiveBroadcastDiscovery {
    public string? NextResult { get; set; } = "FIXTUREvid001";
    public int CallCount;
    public Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct) {
        Interlocked.Increment(ref CallCount);
        return Task.FromResult(NextResult);
    }
}

internal sealed class ToggleDiscovery : IYouTubeLiveBroadcastDiscovery {
    public Queue<string?> Results { get; } = new();
    public string? Fallback { get; set; } = null;
    public int CallCount;
    public Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct) {
        Interlocked.Increment(ref CallCount);
        if (Results.Count > 0) return Task.FromResult(Results.Dequeue());
        return Task.FromResult(Fallback);
    }
}

internal sealed class StubScraper : IYouTubeLiveChatScraper {
    public InitialPageParseResult? NextInitialResult { get; set; } =
        new InitialPageParseResult("APIKEY", "1.0.0", "CONT0");
    public PollResult NextPollResult { get; set; } =
        new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 50);
    public Func<PollResult>? PollFactory { get; set; }
    public int PollCallCount;

    public Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct) =>
        Task.FromResult(NextInitialResult);

    public Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) {
        Interlocked.Increment(ref PollCallCount);
        if (PollFactory is not null) return Task.FromResult(PollFactory());
        return Task.FromResult(NextPollResult);
    }
}

internal sealed class StubScraperWithSequence : IYouTubeLiveChatScraper {
    public InitialPageParseResult? NextInitialResult { get; set; } =
        new InitialPageParseResult("APIKEY", "1.0.0", "CONT0");
    public Queue<PollResult> PollResults { get; } = new();
    public Queue<Exception> PollExceptions { get; } = new();
    public int PollCallCount;

    public Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct) =>
        Task.FromResult(NextInitialResult);

    public Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) {
        Interlocked.Increment(ref PollCallCount);
        if (PollExceptions.Count > 0) {
            var ex = PollExceptions.Dequeue();
            return Task.FromException<PollResult>(ex);
        }
        if (PollResults.Count > 0)
            return Task.FromResult(PollResults.Dequeue());
        // Stall — return an empty result with a continuation but no messages, long timeout.
        return Task.FromResult(new PollResult(Array.Empty<ParsedChatMessage>(), "CONT_LATE", 10000));
    }
}
