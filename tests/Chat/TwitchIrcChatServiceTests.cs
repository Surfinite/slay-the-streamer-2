using System;
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
}
