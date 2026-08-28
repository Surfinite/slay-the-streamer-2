using System;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Tests.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class VoterNamePoolHookTests : VoteSessionTestBase {

    public VoterNamePoolHookTests() {
        VoterNamePoolHook.ResetForTests();
    }

    [Fact]
    public void Closed_session_voters_land_in_pool() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(session, "42", 1);
        session.CloseNow();

        Assert.Equal(1, VoterNamePoolHook.Pool.DistinctVoterCount);
        Assert.True(VoterNamePoolHook.Pool.TryTakeName(out var name, out var key));
        Assert.Equal("login_42", name);
        Assert.Equal("42", key);
    }

    [Fact]
    public void Cancelled_session_voters_also_land_in_pool() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(session, "7", 0);
        session.Cancel();

        Assert.Equal(1, VoterNamePoolHook.Pool.DistinctVoterCount);
    }

    [Fact]
    public void Voters_accumulate_across_sessions() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var s1 = coordinator.Start("one", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(s1, "1", 0);
        s1.CloseNow();
        var s2 = coordinator.Start("two", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(s2, "2", 0);
        s2.CloseNow();

        Assert.Equal(2, VoterNamePoolHook.Pool.DistinctVoterCount);
    }
}
