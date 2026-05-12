using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

[Collection("TiLog.Sink")]
public class MultiChatServiceTests {
    [Fact]
    public void Constructor_With_Empty_Children_Throws() {
        Assert.Throws<ArgumentException>(() => new MultiChatService());
    }

    [Fact]
    public void Constructor_With_Null_Service_Throws() {
        Assert.Throws<ArgumentException>(() =>
            new MultiChatService((ChatPlatformNames.Twitch, (IChatConsumer)null!)));
    }

    [Fact]
    public void Constructor_With_Empty_Name_Throws() {
        var fake = new FakeChatService();
        Assert.Throws<ArgumentException>(() => new MultiChatService(("", fake)));
    }

    [Fact]
    public void Constructor_With_Duplicate_Names_Throws() {
        var f1 = new FakeChatService();
        var f2 = new FakeChatService();
        Assert.Throws<ArgumentException>(() =>
            new MultiChatService((ChatPlatformNames.Twitch, f1), (ChatPlatformNames.Twitch, f2)));
    }

    [Fact]
    public void ConfiguredPlatforms_Reflects_Registration_Order() {
        var twitch = new FakeChatService();
        var youtube = new FakeChatService();
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(
            new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube },
            multi.ConfiguredPlatforms);
    }

    [Fact]
    public void GetChildState_Returns_Child_State_When_Found() {
        var twitch = new FakeChatService();
        twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, multi.GetChildState(ChatPlatformNames.Twitch));
    }

    [Fact]
    public void GetChildState_Returns_Disposed_When_Name_Unknown() {
        var twitch = new FakeChatService();
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        Assert.Equal(ChatConnectionState.Disposed, multi.GetChildState("nope"));
    }

    [Fact]
    public void Aggregate_BestOfChildren_Picks_ConnectedReadWrite() {
        var twitch = new FakeChatService(); twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
        var youtube = new FakeChatService(); youtube.SimulateState(ChatConnectionState.Reconnecting);
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, multi.State);
    }

    [Fact]
    public void Aggregate_MixedTerminal_AuthFailedRanksAbove_Disposed() {
        var twitch = new FakeChatService(); twitch.SimulateState(ChatConnectionState.AuthenticationFailed);
        var youtube = new FakeChatService(); youtube.SimulateState(ChatConnectionState.Disposed);
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(ChatConnectionState.AuthenticationFailed, multi.State);
    }

    [Fact]
    public void MessageReceived_From_Child_Forwards_Through_Multi() {
        var twitch = new FakeChatService();
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        ChatMessage? received = null;
        multi.MessageReceived += (_, m) => received = m;
        twitch.SimulateIncoming(new ChatMessage(
            UserId: "1", Login: "a", DisplayName: "A", Text: "#0",
            ReceivedAt: DateTimeOffset.UtcNow,
            IsSubscriber: false, IsModerator: false, IsVip: false));
        Assert.NotNull(received);
        Assert.Equal("1", received!.UserId);
    }

    [Fact]
    public void ChildConnectionStateChanged_Fires_On_Child_Transition() {
        var twitch = new FakeChatService();
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        ChildConnectionStateChangedEventArgs? captured = null;
        multi.ChildConnectionStateChanged += (_, e) => captured = e;
        twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
        Assert.NotNull(captured);
        Assert.Equal(ChatPlatformNames.Twitch, captured!.ChildName);
    }

    [Fact]
    public void Aggregate_ConnectionStateChanged_Fires_Only_When_Aggregate_Changes() {
        var twitch = new FakeChatService();
        var youtube = new FakeChatService();
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        int aggregateFireCount = 0;
        multi.ConnectionStateChanged += (_, _) => aggregateFireCount++;

        twitch.SimulateState(ChatConnectionState.Connecting);   // aggregate: Disconnected -> Connecting
        Assert.Equal(1, aggregateFireCount);

        youtube.SimulateState(ChatConnectionState.Connecting);  // aggregate stays Connecting
        Assert.Equal(1, aggregateFireCount);

        twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);  // aggregate: Connecting -> CRW
        Assert.Equal(2, aggregateFireCount);
    }
}
