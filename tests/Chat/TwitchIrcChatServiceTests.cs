using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SlayTheStreamer2.Tests.Chat.Internal;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.Internal;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class TwitchIrcChatServiceTests {
    private static (TwitchIrcChatService svc, FakeIrcTransport transport, FakeClock clock, FakeTimerScheduler sched) Build() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        var transport = new FakeIrcTransport();
        var svc = new TwitchIrcChatService(
            dispatcher: dispatcher, clock: clock, scheduler: sched,
            transportFactory: () => transport,
            sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30),
            sendMinInterval: TimeSpan.FromSeconds(1));
        return (svc, transport, clock, sched);
    }

    [Fact]
    public void NewService_StartsDisconnected() {
        var (svc, _, _, _) = Build();
        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
        Assert.False(svc.IsConnected);
        Assert.False(svc.CanSend);
        svc.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_HappyPath_TransitionsToConnectedReadWrite() {
        var (svc, transport, clock, sched) = Build();
        var stateChanges = new List<(ChatConnectionState Old, ChatConnectionState New)>();
        svc.ConnectionStateChanged += (_, e) => stateChanges.Add((e.OldState, e.NewState));

        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);

        // Drive the read loop: deliver successful auth + JOIN confirmation.
        transport.InjectIncoming(":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands");
        transport.InjectIncoming(":tmi.twitch.tv 001 surfinitebot :Welcome, GLHF!");
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");

        // Allow read loop iterations.
        for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) {
            await Task.Delay(20);
        }

        Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
        Assert.Contains(transport.Writes, w => w == "CAP REQ :twitch.tv/tags twitch.tv/commands");
        Assert.Contains(transport.Writes, w => w == "PASS oauth:abc123def456ghi789jkl012mno345");
        Assert.Contains(transport.Writes, w => w == "NICK surfinitebot");
        Assert.Contains(transport.Writes, w => w == "JOIN #surfinite");
        Assert.Contains(stateChanges, c => c.New == ChatConnectionState.ConnectedReadWrite);
        svc.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_AuthFailureNotice_TransitionsToAuthenticationFailed() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);

        transport.InjectIncoming(":tmi.twitch.tv NOTICE * :Login authentication failed");

        for (int i = 0; i < 10 && svc.State != ChatConnectionState.AuthenticationFailed; i++) {
            await Task.Delay(20);
        }
        Assert.Equal(ChatConnectionState.AuthenticationFailed, svc.State);
        svc.Dispose();
    }

    [Fact]
    public async Task CapNak_FallsBackToNoTagsMode_AndStillReachesConnected() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);

        transport.InjectIncoming(":tmi.twitch.tv CAP * NAK :twitch.tv/tags twitch.tv/commands");
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");

        for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) {
            await Task.Delay(20);
        }
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
        Assert.False(svc.HasTags);   // expose a property indicating tag-mode
        svc.Dispose();
    }

    [Fact]
    public async Task Privmsg_RaisesMessageReceived_OnDispatcherThread() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var received = new List<ChatMessage>();
        svc.MessageReceived += (_, m) => received.Add(m);

        var connectTask = svc.ConnectAsync("surfinite", creds);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        transport.InjectIncoming(
            "@user-id=1234;display-name=Carol :carol!carol@carol.tmi.twitch.tv PRIVMSG #surfinite :#0");

        for (int i = 0; i < 10 && received.Count == 0; i++) await Task.Delay(20);

        Assert.Single(received);
        Assert.Equal("#0", received[0].Text);
        Assert.Equal("carol", received[0].Login);
        svc.Dispose();
    }

    [Fact]
    public async Task SendMessageAsync_WhenConnected_WritesPrivmsgToTransport() {
        var (svc, transport, clock, sched) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

        await svc.SendMessageAsync("hello chat", OutgoingMessagePriority.High);
        sched.Advance(TimeSpan.Zero);
        await Task.Delay(50);

        Assert.Contains(transport.Writes, w => w == "PRIVMSG #surfinite :hello chat");
        svc.Dispose();
    }

    [Fact]
    public async Task Privmsg_SelfEchoByLogin_IsFiltered() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var received = new List<ChatMessage>();
        svc.MessageReceived += (_, m) => received.Add(m);

        var connectTask = svc.ConnectAsync("surfinite", creds);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        // Self message — login matches the bot's NICK.
        transport.InjectIncoming(":surfinitebot!surfinitebot@surfinitebot.tmi.twitch.tv PRIVMSG #surfinite :hello from bot");
        // Another user — should NOT be filtered.
        transport.InjectIncoming(":alice!alice@alice.tmi.twitch.tv PRIVMSG #surfinite :hello from alice");

        for (int i = 0; i < 10 && received.Count == 0; i++) await Task.Delay(20);
        await Task.Delay(50);   // give the second message a chance to arrive

        Assert.Single(received);
        Assert.Equal("alice", received[0].Login);
        svc.Dispose();
    }

    [Fact]
    public async Task Send_TwoBackToBack_AreSpacedAtLeast1Second() {
        var (svc, transport, clock, sched) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

        int writesBefore = transport.Writes.Count;
        await svc.SendMessageAsync("first", OutgoingMessagePriority.High);
        await svc.SendMessageAsync("second", OutgoingMessagePriority.High);

        sched.Advance(TimeSpan.Zero);
        await Task.Delay(50);
        var firstSendTime = clock.UtcNow;
        int writesAfterFirst = transport.Writes.Count - writesBefore;
        Assert.True(writesAfterFirst >= 1, "first message should be sent");
        Assert.True(writesAfterFirst < 2, "second message should NOT be sent yet (gated by minInterval)");

        sched.Advance(TimeSpan.FromMilliseconds(1100));
        await Task.Delay(50);
        var totalWrites = transport.Writes.Count - writesBefore;
        Assert.True(totalWrites >= 2, "second message should be sent after >=1s gap");
        svc.Dispose();
    }

    [Fact]
    public async Task Ping_TriggersPong_BeforeJoinConfirmation() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);

        transport.InjectIncoming("PING :tmi.twitch.tv");
        for (int i = 0; i < 10; i++) {
            if (transport.Writes.Any(w => w.StartsWith("PONG"))) break;
            await Task.Delay(20);
        }
        Assert.Contains(transport.Writes, w => w == "PONG :tmi.twitch.tv");
        svc.Dispose();
    }

    [Fact]
    public async Task JoinConfirmationTimeout_TransitionsToJoinFailed() {
        var (svc, transport, clock, sched) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);

        // No ROOMSTATE/USERSTATE/353/366 injected — simulate a quietly-dropped JOIN.
        // Wait for JOIN to be sent first.
        for (int i = 0; i < 20 && !transport.Writes.Any(w => w.StartsWith("JOIN")); i++) await Task.Delay(20);

        sched.Advance(TimeSpan.FromSeconds(11));
        await Task.Delay(50);

        Assert.Equal(ChatConnectionState.JoinFailed, svc.State);
        svc.Dispose();
    }
}
