using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatMessageTests {
    [Fact]
    public void VoterKeyIsUserIdWhenPresent() {
        var msg = new ChatMessage(
            UserId: "12345",
            Login: "alice",
            DisplayName: "Alice",
            Text: "hi",
            ReceivedAt: DateTimeOffset.UtcNow,
            IsSubscriber: false, IsModerator: false, IsVip: false);
        Assert.Equal("12345", msg.VoterKey);
    }

    [Fact]
    public void VoterKeyFallsBackToLoginWhenUserIdNull() {
        var msg = new ChatMessage(
            UserId: null,
            Login: "alice",
            DisplayName: "Alice",
            Text: "hi",
            ReceivedAt: DateTimeOffset.UtcNow,
            IsSubscriber: false, IsModerator: false, IsVip: false);
        Assert.Equal("login:alice", msg.VoterKey);
    }
}
