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
