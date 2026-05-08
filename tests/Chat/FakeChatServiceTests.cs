using System;
using System.Linq;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class FakeChatServiceTests {
    private static ChatMessage Msg(string login = "alice", string text = "hi", string? userId = "1") =>
        new(userId, login, login, text, DateTimeOffset.UtcNow, false, false, false);

    [Fact]
    public void StartsDisconnected() {
        var chat = new FakeChatService();
        Assert.Equal(ChatConnectionState.Disconnected, chat.State);
        Assert.False(chat.IsConnected);
        Assert.False(chat.CanSend);
    }

    [Fact]
    public async Task ConnectMovesToConnectedReadWriteWithCreds() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, chat.State);
        Assert.True(chat.IsConnected);
        Assert.True(chat.CanSend);
    }

    [Fact]
    public async Task ConnectMovesToConnectedReadOnlyWithoutCreds() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", creds: null);
        Assert.Equal(ChatConnectionState.ConnectedReadOnly, chat.State);
        Assert.True(chat.IsConnected);
        Assert.False(chat.CanSend);
    }

    [Fact]
    public async Task InjectRaisesMessageReceivedSynchronously() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo");
        ChatMessage? seen = null;
        chat.MessageReceived += (_, m) => seen = m;

        var injected = Msg();
        chat.Inject(injected);
        Assert.Same(injected, seen);
    }

    [Fact]
    public async Task SendMessageAsyncRecordsAtCorrectPriority() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        await chat.SendMessageAsync("open", OutgoingMessagePriority.Normal);
        await chat.SendMessageAsync("tally", OutgoingMessagePriority.Low);
        await chat.SendMessageAsync("close", OutgoingMessagePriority.High);

        Assert.Equal(3, chat.SentMessages.Count);
        Assert.Equal(("open", OutgoingMessagePriority.Normal), (chat.SentMessages[0].Text, chat.SentMessages[0].Priority));
        Assert.Equal(("tally", OutgoingMessagePriority.Low), (chat.SentMessages[1].Text, chat.SentMessages[1].Priority));
        Assert.Equal(("close", OutgoingMessagePriority.High), (chat.SentMessages[2].Text, chat.SentMessages[2].Priority));
    }

    [Fact]
    public async Task SendInAnonymousModeFailsTask() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", creds: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendMessageAsync("hi"));
    }

    [Fact]
    public async Task DisconnectMovesToDisconnectedAndFiresEvent() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo", new ChatCredentials("u", "abc"));
        ChatConnectionChangedEventArgs? lastEvt = null;
        chat.ConnectionStateChanged += (_, e) => lastEvt = e;

        chat.Disconnect();
        Assert.Equal(ChatConnectionState.Disconnected, chat.State);
        Assert.NotNull(lastEvt);
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, lastEvt!.OldState);
        Assert.Equal(ChatConnectionState.Disconnected, lastEvt.NewState);
    }

    [Fact]
    public async Task LastMessageReceivedAtUpdatesOnInject() {
        var chat = new FakeChatService();
        await chat.ConnectAsync("foo");
        Assert.Null(chat.LastMessageReceivedAt);

        var t = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        chat.Inject(Msg() with { ReceivedAt = t });
        Assert.Equal(t, chat.LastMessageReceivedAt);
    }
}
