using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatPlatformNamesTests {
    [Fact]
    public void Twitch_Constant_Has_Expected_Value() => Assert.Equal("twitch", ChatPlatformNames.Twitch);

    [Fact]
    public void YouTube_Constant_Has_Expected_Value() => Assert.Equal("youtube", ChatPlatformNames.YouTube);

    [Fact]
    public void Constants_Are_Distinct() => Assert.NotEqual(ChatPlatformNames.Twitch, ChatPlatformNames.YouTube);
}
