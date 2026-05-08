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

    [Fact]
    public void Parse_Privmsg_ExtractsLoginAndText() {
        var line = ":alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hello world";
        var evt = TwitchIrcParser.Parse(line);
        var msg = Assert.IsType<PrivmsgEvent>(evt).Message;
        Assert.Equal("alice", msg.Login);
        Assert.Equal("hello world", msg.Text);
    }

    [Fact]
    public void Parse_Privmsg_WithTags_ExtractsUserIdAndDisplayName() {
        var line = "@user-id=12345;display-name=Alice :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("12345", msg.UserId);
        Assert.Equal("Alice", msg.DisplayName);
    }

    [Fact]
    public void Parse_Privmsg_WithoutTags_UserIdIsNull_DisplayNameFallsBackToLogin() {
        var line = ":alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Null(msg.UserId);
        Assert.Equal("alice", msg.DisplayName);
    }

    [Fact]
    public void Parse_Privmsg_BadgeFlagsExtracted() {
        var line = "@badges=subscriber/12,moderator/1,vip/1;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.True(msg.IsSubscriber);
        Assert.True(msg.IsModerator);
        Assert.True(msg.IsVip);
    }

    [Fact]
    public void Parse_Privmsg_BadgesAbsent_FlagsFalse() {
        var line = "@user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.False(msg.IsSubscriber);
        Assert.False(msg.IsModerator);
        Assert.False(msg.IsVip);
    }

    [Fact]
    public void Parse_Privmsg_BadgesAreCaseSensitive_LowercaseOnly() {
        // Twitch's IRC tag values are always lowercased badge names; this asserts
        // we don't accidentally match mixed-case variants. Documents the contract.
        var line = "@badges=Subscriber/12;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.False(msg.IsSubscriber);
    }

    [Fact]
    public void Parse_Privmsg_ReceivedAtIsMinValue_WhenTmiSentTsAbsent() {
        // Parser is a pure function from string to data — no clock dependency.
        // TwitchIrcChatService (Plan B) stamps a real timestamp using its
        // injected IClock. MinValue is the "stamp me later" sentinel.
        var line = ":alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal(DateTimeOffset.MinValue, msg.ReceivedAt);
    }

    [Fact]
    public void Parse_Privmsg_ReceivedAt_FromTmiSentTs_WhenPresent() {
        var line = "@tmi-sent-ts=1715169600000;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(1715169600000);
        Assert.Equal(expected, msg.ReceivedAt);
    }

    [Fact]
    public void Parse_TagValue_Unescapes_ColonSpaceBackslashCRLF() {
        // Tag value: "a\:b\sc\\d\re\nf" → "a;b c\d\re\nf" (the \r and \n are literal control chars)
        var line = "@display-name=a\\:b\\sc\\\\d\\re\\nf;user-id=1 :alice!alice@alice.tmi.twitch.tv PRIVMSG #foo :hi";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("a;b c\\d\re\nf", msg.DisplayName);
    }

    [Fact]
    public void Parse_Notice_AuthFailed_ExtractsTextAndMsgId() {
        var line = "@msg-id=msg_login_unsuccessful :tmi.twitch.tv NOTICE * :Login authentication failed";
        var n = Assert.IsType<NoticeEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("msg_login_unsuccessful", n.MsgId);
        Assert.Equal("Login authentication failed", n.Text);
    }

    [Fact]
    public void Parse_Notice_ChannelTargeted_ExtractsChannel() {
        var line = "@msg-id=msg_banned :tmi.twitch.tv NOTICE #foo :You are permanently banned from this channel";
        var n = Assert.IsType<NoticeEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", n.Channel);
        Assert.Equal("msg_banned", n.MsgId);
    }

    [Fact]
    public void Parse_CapAck_ExtractsCapabilities() {
        var line = ":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands";
        var ack = Assert.IsType<CapAckEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal(2, ack.Capabilities.Count);
        Assert.Contains("twitch.tv/tags", ack.Capabilities);
    }

    [Fact]
    public void Parse_CapNak_ExtractsCapabilities() {
        var line = ":tmi.twitch.tv CAP * NAK :twitch.tv/membership";
        var nak = Assert.IsType<CapNakEvent>(TwitchIrcParser.Parse(line));
        Assert.Single(nak.Capabilities);
        Assert.Equal("twitch.tv/membership", nak.Capabilities[0]);
    }

    [Fact]
    public void Parse_UserState_ExtractsDisplayName() {
        var line = "@display-name=Bot :tmi.twitch.tv USERSTATE #foo";
        var us = Assert.IsType<UserStateEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", us.Channel);
        Assert.Equal("Bot", us.DisplayName);
    }

    [Fact]
    public void Parse_RoomState_ExposesAllTags() {
        var line = "@emote-only=0;subs-only=0;slow=10 :tmi.twitch.tv ROOMSTATE #foo";
        var rs = Assert.IsType<RoomStateEvent>(TwitchIrcParser.Parse(line));
        Assert.Equal("#foo", rs.Channel);
        Assert.Equal("10", rs.Tags["slow"]);
    }

    [Fact]
    public void Parse_TruncatedLine_ReturnsUnknown() {
        Assert.IsType<UnknownIrcEvent>(TwitchIrcParser.Parse("@incomplete"));
        Assert.IsType<UnknownIrcEvent>(TwitchIrcParser.Parse(":server"));
    }

    [Fact]
    public void Parse_MultiByteUserText_PreservesText() {
        var line = ":alice!a@a PRIVMSG #foo :こんにちは 🎉";
        var msg = Assert.IsType<PrivmsgEvent>(TwitchIrcParser.Parse(line)!).Message;
        Assert.Equal("こんにちは 🎉", msg.Text);
    }
}
