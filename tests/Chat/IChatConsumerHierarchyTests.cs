using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class IChatConsumerHierarchyTests {
    [Fact]
    public void IChatService_Extends_IChatConsumer() {
        Assert.True(typeof(IChatConsumer).IsAssignableFrom(typeof(IChatService)),
            "IChatService must extend IChatConsumer");
    }

    [Fact]
    public void TwitchIrcChatService_Implements_IChatConsumer() {
        Assert.True(typeof(IChatConsumer).IsAssignableFrom(typeof(TwitchIrcChatService)));
    }

    [Fact]
    public void IChatConsumer_Does_Not_Have_ConnectAsync() {
        var connectAsync = typeof(IChatConsumer).GetMethod("ConnectAsync");
        Assert.Null(connectAsync);
    }

    [Fact]
    public void IChatService_Has_ConnectAsync() {
        var connectAsync = typeof(IChatService).GetMethod("ConnectAsync");
        Assert.NotNull(connectAsync);
    }
}
