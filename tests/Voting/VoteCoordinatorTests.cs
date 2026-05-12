using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteCoordinatorTests {
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeChatService _chat = new();
    private readonly FakeTimerScheduler _scheduler;
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly Random _rng = new(7);

    public VoteCoordinatorTests() {
        _scheduler = new FakeTimerScheduler(_clock);
        _chat.ConnectAsync("test", new ChatCredentials("bot", "abc")).GetAwaiter().GetResult();
    }

    private VoteCoordinator NewCoord() =>
        new(_chat, new[] { ChatPlatformNames.Twitch }, _clock, _scheduler, _dispatcher, _rng);

    [Fact]
    public void Start_BuildsOptionListWithCorrectIndices() {
        var c = NewCoord();
        var s = c.Start("card reward", new[] { "Bash", "Defend", "Strike" }, TimeSpan.FromSeconds(30));
        Assert.Equal(0, s.Options[0].Index);
        Assert.Equal("Bash", s.Options[0].Label);
        Assert.Equal(2, s.Options[2].Index);
    }

    [Fact]
    public void Start_AssignsCurrentSession() {
        var c = NewCoord();
        var s = c.Start("test", new[] { "a", "b" }, TimeSpan.FromSeconds(10));
        Assert.Same(s, c.CurrentSession);
    }

    [Fact]
    public void Start_WhileOpen_Throws() {
        var c = NewCoord();
        c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        Assert.Throws<InvalidOperationException>(() =>
            c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Start_AfterPriorClosed_Succeeds() {
        var c = NewCoord();
        var s1 = c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s1.CloseNow();
        var s2 = c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30));
        Assert.Same(s2, c.CurrentSession);
    }

    [Fact]
    public void Start_AfterPriorCancelled_Succeeds() {
        var c = NewCoord();
        var s1 = c.Start("v1", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s1.Cancel();
        var s2 = c.Start("v2", new[] { "x", "y" }, TimeSpan.FromSeconds(30));
        Assert.Same(s2, c.CurrentSession);
    }

    [Fact]
    public void CurrentSession_ClearedAfterClose() {
        var c = NewCoord();
        var s = c.Start("v", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        s.CloseNow();
        Assert.Null(c.CurrentSession);
    }

    [Fact]
    public void Start_WithNullOptions_ThrowsArgumentNullException() {
        var c = NewCoord();
        Assert.Throws<ArgumentNullException>(() =>
            c.Start("v", null!, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Dispose_CancelsActiveSession() {
        var c = NewCoord();
        var s = c.Start("v", new[] { "a", "b" }, TimeSpan.FromSeconds(30));
        c.Dispose();
        Assert.Equal(VoteSessionState.Cancelled, s.State);
    }

    [Fact]
    public void Dispatcher_ReturnsConstructorInjected() {
        var chat = new FakeChatService();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        var coord = new VoteCoordinator(chat, new[] { ChatPlatformNames.Twitch }, clock, scheduler, dispatcher);
        Assert.Same(dispatcher, coord.Dispatcher);
    }
}
