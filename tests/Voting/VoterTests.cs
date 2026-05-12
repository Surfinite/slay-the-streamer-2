using System;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoterTests : IDisposable {
    private readonly VoteCoordinator? _prior = Voter.Default;
    public void Dispose() => Voter.Default = _prior;

    [Fact]
    public void StartWithNullDefault_Throws() {
        Voter.Default = null;
        Assert.Throws<InvalidOperationException>(() =>
            Voter.Start("x", new[] { "a", "b" }, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void StartWithDefault_DelegatesToCoordinator() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var chat = new FakeChatService();
        chat.ConnectAsync("c", new ChatCredentials("u", "abc")).GetAwaiter().GetResult();
        var coord = new VoteCoordinator(chat, new[] { ChatPlatformNames.Twitch }, clock, new FakeTimerScheduler(clock), new ImmediateDispatcher(), new Random(0));
        Voter.Default = coord;

        var s = Voter.Start("test", new[] { "a", "b" }, TimeSpan.FromSeconds(10));
        Assert.Same(s, coord.CurrentSession);
    }
}
