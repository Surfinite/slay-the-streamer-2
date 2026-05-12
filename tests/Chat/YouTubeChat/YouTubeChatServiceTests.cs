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
