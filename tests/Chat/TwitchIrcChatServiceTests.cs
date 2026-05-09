using System;
using System.Collections.Generic;
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
}
