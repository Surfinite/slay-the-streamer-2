using System;
using System.Linq;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionPerPlatformTallyTests : VoteSessionTestBase {
    [Fact]
    public void SinglePlatform_TalliesByPlatform_Returns_Null() {
        var session = CreateSession(configuredPlatforms: new[] { ChatPlatformNames.Twitch });
        Assert.Null(session.TalliesByPlatform);
    }

    [Fact]
    public void MultiPlatform_TalliesByPlatform_NonNull_Even_With_Zero_Votes() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        Assert.NotNull(session.TalliesByPlatform);
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 0)]);
        Assert.Equal(0, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 0)]);
    }

    [Fact]
    public void TwitchVote_Increments_TwitchTally_Only() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectTwitchVote(session, userId: "12345", optionIndex: 1);
        Assert.Equal(1, session.Tallies[1]);
        Assert.Equal(1, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 1)]);
        Assert.Equal(0, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 1)]);
    }

    [Fact]
    public void YouTubeVote_Increments_YouTubeTally_Only() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 2);
        Assert.Equal(1, session.Tallies[2]);
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 2)]);
        Assert.Equal(1, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 2)]);
    }

    [Fact]
    public void LatestWins_Decrements_Old_Platform_Tally() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 0);
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 1);
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.YouTube, 0)]);
        Assert.Equal(1, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 1)]);
        Assert.Equal(1, session.Tallies[1]);
        Assert.Equal(0, session.Tallies[0]);
    }

    [Fact]
    public void Invariant_SumByPlatform_Equals_SumMerged() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectTwitchVote(session, userId: "111", optionIndex: 0);
        InjectTwitchVote(session, userId: "222", optionIndex: 1);
        InjectYouTubeVote(session, channelId: "UCa", optionIndex: 0);
        InjectYouTubeVote(session, channelId: "UCb", optionIndex: 2);
        var perPlatformSum = session.TalliesByPlatform!.Values.Sum();
        var mergedSum = session.Tallies.Values.Sum();
        Assert.Equal(mergedSum, perPlatformSum);
    }

    [Fact]
    public void ConfiguredPlatforms_Empty_Throws() {
        Assert.Throws<ArgumentException>(() =>
            CreateSession(configuredPlatforms: Array.Empty<string>()));
    }

    [Fact]
    public void ConfiguredPlatforms_Null_Throws() {
        // CreateSessionRaw bypasses CreateSession's default-substitution so
        // the null reaches the VoteSession ctor and the guard fires.
        Assert.Throws<ArgumentNullException>(() =>
            CreateSessionRaw(configuredPlatforms: null));
    }

    [Fact]
    public void TallyVersion_Starts_At_Zero() {
        var session = CreateSession();
        Assert.Equal(0, session.TallyVersion);
    }

    [Fact]
    public void TallyVersion_Increments_On_Accepted_Vote() {
        var session = CreateSession();
        InjectTwitchVote(session, userId: "u1", optionIndex: 0);
        Assert.Equal(1, session.TallyVersion);
        InjectTwitchVote(session, userId: "u2", optionIndex: 1);
        Assert.Equal(2, session.TallyVersion);
    }

    [Fact]
    public void TallyVersion_Does_Not_Increment_On_Invalid_Vote() {
        var session = CreateSession();
        InjectTwitchVote(session, userId: "u1", optionIndex: 99);
        Assert.Equal(0, session.TallyVersion);
    }
}
