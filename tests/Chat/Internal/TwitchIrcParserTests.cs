using System;
using SlayTheStreamer2.Ti.Chat.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.Internal;

public class TwitchIrcParserTests {
    [Fact]
    public void Parse_Ping_ExtractsToken() {
        var evt = TwitchIrcParser.Parse("PING :tmi.twitch.tv");
        var ping = Assert.IsType<PingEvent>(evt);
        Assert.Equal("tmi.twitch.tv", ping.Token);
    }

    [Fact]
    public void Parse_Reconnect_ReturnsReconnectEvent() {
        var evt = TwitchIrcParser.Parse(":tmi.twitch.tv RECONNECT");
        Assert.IsType<ReconnectEvent>(evt);
    }

    [Fact]
    public void Parse_EmptyLine_ReturnsNull() {
        Assert.Null(TwitchIrcParser.Parse(""));
        Assert.Null(TwitchIrcParser.Parse("   "));
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsUnknown() {
        var evt = TwitchIrcParser.Parse(":server FOO bar baz");
        var unk = Assert.IsType<UnknownIrcEvent>(evt);
        Assert.Equal(":server FOO bar baz", unk.Raw);
    }
}
