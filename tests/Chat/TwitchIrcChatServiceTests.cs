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

[Collection("TiLog.Sink")]
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

    [Fact]
    public async Task RateLimitNotice_LogsAndDoesNotChangeState() {
        var (svc, transport, _, _) = Build();
        var logs = new List<string>();
        var oldSink = TiLog.Sink;
        TiLog.Sink = (level, msg, ex) => {
            if (level >= LogLevel.Warn) lock (logs) logs.Add(msg);
        };
        try {
            var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
            var connectTask = svc.ConnectAsync("surfinite", creds);
            transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
            for (int i = 0; i < 10 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

            transport.InjectIncoming("@msg-id=msg_ratelimit :tmi.twitch.tv NOTICE #surfinite :Your message was not sent because you are sending messages too quickly.");
            for (int i = 0; i < 10 && !logs.Any(l => l.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)); i++) await Task.Delay(20);

            Assert.Equal(ChatConnectionState.ConnectedReadWrite, svc.State);
            Assert.Contains(logs, l => l.Contains("ratelimit", StringComparison.OrdinalIgnoreCase));
        } finally {
            TiLog.Sink = oldSink;
            svc.Dispose();
        }
    }

    [Fact]
    public async Task ConnectAsync_NoCredentials_EntersConnectedReadOnly() {
        var (svc, transport, _, _) = Build();
        var connectTask = svc.ConnectAsync("surfinite", creds: null);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 10 && !svc.IsConnected; i++) await Task.Delay(20);

        Assert.Equal(ChatConnectionState.ConnectedReadOnly, svc.State);
        Assert.False(svc.CanSend);
        Assert.Contains(transport.Writes, w => w.StartsWith("NICK justinfan"));
        Assert.DoesNotContain(transport.Writes, w => w.StartsWith("PASS"));
        svc.Dispose();
    }

    [Fact]
    public async Task SendMessageAsync_InAnonymousMode_ReturnsFailedTask() {
        var (svc, transport, _, _) = Build();
        var connectTask = svc.ConnectAsync("surfinite", creds: null);
        transport.InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 10 && !svc.IsConnected; i++) await Task.Delay(20);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await svc.SendMessageAsync("nope"));
        svc.Dispose();
    }

    [Fact]
    public async Task TransportClose_TriggersReconnect_WithBackoff() {
        // Use a transport factory that hands out a fresh fake on each call.
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        var transports = new List<FakeIrcTransport>();
        var svc = new TwitchIrcChatService(
            dispatcher, clock, sched,
            transportFactory: () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
            sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30), sendMinInterval: TimeSpan.FromSeconds(1));

        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);
        for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
        transports[0].InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 20 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

        // Simulate remote close.
        transports[0].Close();
        for (int i = 0; i < 20 && svc.State != ChatConnectionState.Reconnecting; i++) await Task.Delay(20);
        Assert.Equal(ChatConnectionState.Reconnecting, svc.State);

        // Advance past first backoff (5s nominal, ±20% jitter — advance 7s to be safe).
        sched.Advance(TimeSpan.FromSeconds(7));
        for (int i = 0; i < 20 && transports.Count < 2; i++) await Task.Delay(20);
        Assert.True(transports.Count >= 2, "second transport should be created on reconnect");
        svc.Dispose();
    }

    [Fact]
    public async Task ReconnectCommand_TriggersGracefulReconnect() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var transports = new List<FakeIrcTransport>();
        var svc = new TwitchIrcChatService(
            new ImmediateDispatcher(), clock, sched,
            () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
            20, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        _ = svc.ConnectAsync("surfinite", creds);
        for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
        transports[0].InjectIncoming(":tmi.twitch.tv ROOMSTATE #surfinite");
        for (int i = 0; i < 20 && svc.State != ChatConnectionState.ConnectedReadWrite; i++) await Task.Delay(20);

        transports[0].InjectIncoming("RECONNECT");
        // Wait for the read loop to process RECONNECT, exit, and start reconnecting
        for (int i = 0; i < 50 && svc.State != ChatConnectionState.Reconnecting; i++) await Task.Delay(20);

        sched.Advance(TimeSpan.FromSeconds(7));   // past first backoff
        for (int i = 0; i < 20 && transports.Count < 2; i++) await Task.Delay(20);

        Assert.True(transports.Count >= 2);
        svc.Dispose();
    }

    [Fact]
    public async Task AuthenticationFailed_DoesNotTriggerReconnect() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var dispatcher = new ImmediateDispatcher();
        var transports = new List<FakeIrcTransport>();
        var svc = new TwitchIrcChatService(
            dispatcher, clock, sched,
            transportFactory: () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
            sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30), sendMinInterval: TimeSpan.FromSeconds(1));

        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        var connectTask = svc.ConnectAsync("surfinite", creds);
        for (int i = 0; i < 20 && transports.Count < 1; i++) await Task.Delay(20);
        transports[0].InjectIncoming(":tmi.twitch.tv NOTICE * :Login authentication failed");

        for (int i = 0; i < 20 && svc.State != ChatConnectionState.AuthenticationFailed; i++) await Task.Delay(20);
        sched.Advance(TimeSpan.FromSeconds(120));
        await Task.Delay(50);

        Assert.Equal(1, transports.Count);   // no reconnect
        svc.Dispose();
    }

    [Fact]
    public void Disconnect_WhileConnecting_StopsTransport_NoReconnect() {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sched = new FakeTimerScheduler(clock);
        var transports = new List<FakeIrcTransport>();
        var svc = new TwitchIrcChatService(
            new ImmediateDispatcher(), clock, sched,
            () => { var t = new FakeIrcTransport(); transports.Add(t); return t; },
            20, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        _ = svc.ConnectAsync("surfinite", creds);
        svc.Disconnect();

        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
        sched.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, transports.Count);   // no reconnect after explicit Disconnect
        svc.Dispose();
    }

    [Fact]
    public async Task SendMessageAsync_WhenDisconnected_ReturnsFailedTask() {
        var (svc, _, _, _) = Build();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await svc.SendMessageAsync("nope"));
        svc.Dispose();
    }

    [Fact]
    public void Dispose_TransitionsToDisposed_AndClosesTransport() {
        var (svc, transport, _, _) = Build();
        var creds = new ChatCredentials("surfinitebot", "abc123def456ghi789jkl012mno345");
        _ = svc.ConnectAsync("surfinite", creds);
        svc.Dispose();
        Assert.Equal(ChatConnectionState.Disposed, svc.State);
        Assert.True(transport.Disposed);
    }
}
