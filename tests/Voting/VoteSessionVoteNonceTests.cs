using System;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionVoteNonceTests : VoteSessionTestBase {
    [Fact]
    public void VoteId_Is_Surfaced_From_Constructor() {
        var session = CreateSession(voteId: 42);
        Assert.Equal(42, session.VoteId);
    }

    [Fact]
    public void VoteCoordinator_Assigns_Sequential_VoteIds() {
        var coordinator = CreateCoordinator();
        var s1 = coordinator.Start("L1", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        s1.CloseNow();
        var s2 = coordinator.Start("L2", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        Assert.Equal(0, s1.VoteId);
        Assert.Equal(1, s2.VoteId);
    }

    [Fact]
    public void VoteCoordinator_VoteIds_Cycle_At_100() {
        var coordinator = CreateCoordinator();
        VoteSession? lastSession = null;
        for (int i = 0; i < 101; i++) {
            lastSession?.CloseNow();
            lastSession = coordinator.Start($"L{i}", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, lastSession!.VoteId);
    }
}
