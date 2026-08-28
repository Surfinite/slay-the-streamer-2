using System.Linq;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionVoterNamesTests : VoteSessionTestBase {

    [Fact]
    public void VoterDisplayNames_empty_before_any_vote() {
        var session = StartVote();
        Assert.Empty(session.VoterDisplayNames);
    }

    [Fact]
    public void VoterDisplayNames_captures_display_name_per_voter_key() {
        var session = StartVote();
        Chat.Inject(new ChatMessage("123", "somelogin", "SomeViewer", "#1",
            Clock.UtcNow, false, false, false));
        InjectYouTubeVote(session, "UCabc", 0);

        Assert.Equal(2, session.VoterDisplayNames.Count);
        Assert.Equal("SomeViewer", session.VoterDisplayNames["123"]);
        Assert.Equal("UCabc", session.VoterDisplayNames["yt:UCabc"]);
    }

    [Fact]
    public void VoterDisplayNames_vote_change_updates_name_not_count() {
        var session = StartVote();
        Chat.Inject(new ChatMessage("123", "somelogin", "OldName", "#1",
            Clock.UtcNow, false, false, false));
        Chat.Inject(new ChatMessage("123", "somelogin", "NewName", "#2",
            Clock.UtcNow, false, false, false));

        Assert.Single(session.VoterDisplayNames);
        Assert.Equal("NewName", session.VoterDisplayNames["123"]);
    }

    [Fact]
    public void VoterDisplayNames_non_vote_messages_not_captured() {
        var session = StartVote();
        Inject("chatty", "hello everyone");
        Assert.Empty(session.VoterDisplayNames);
    }

    [Fact]
    public void VoterDisplayNames_survives_close() {
        var session = StartVote();
        InjectTwitchVote(session, "42", 1);
        session.CloseNow();
        Assert.Single(session.VoterDisplayNames);
        Assert.Equal("login_42", session.VoterDisplayNames["42"]);
    }

    [Fact]
    public void VoterDisplayNames_snapshot_is_a_copy() {
        var session = StartVote();
        InjectTwitchVote(session, "42", 1);
        var snapshot = session.VoterDisplayNames;
        InjectTwitchVote(session, "43", 1);
        Assert.Single(snapshot);                          // old snapshot unchanged
        Assert.Equal(2, session.VoterDisplayNames.Count); // fresh read sees both
    }
}
