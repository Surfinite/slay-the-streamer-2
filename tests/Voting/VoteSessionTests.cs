using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteSessionTests : VoteSessionTestBase {
    [Fact]
    public void NewSessionStartsInOpenState() {
        var s = StartVote();
        Assert.Equal(VoteSessionState.Open, s.State);
        Assert.Null(s.WinnerIndex);
    }

    [Fact]
    public void OptionsExposed_ZeroIndexed_PositionMatchesIndex() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(3, s.Options.Count);
        Assert.Equal(0, s.Options[0].Index);
        Assert.Equal("A", s.Options[0].Label);
        Assert.Equal(2, s.Options[2].Index);
    }

    [Fact]
    public void TalliesStartAtZeroForEveryOption() {
        var s = StartVote(options: new[] { "A", "B", "C" });
        Assert.Equal(0, s.Tallies[0]);
        Assert.Equal(0, s.Tallies[1]);
        Assert.Equal(0, s.Tallies[2]);
    }

    [Fact]
    public void TimeRemainingStartsAtDuration() {
        var s = StartVote(duration: TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(45), s.TimeRemaining);
    }

    [Fact]
    public void TimeRemainingDecreasesAsClockAdvances() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        Scheduler.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(23), s.TimeRemaining);
    }

    [Fact]
    public void SnapshotMirrorsCurrentState() {
        var s = StartVote();
        var snap = s.Snapshot();
        Assert.Equal(s.Id, snap.Id);
        Assert.Equal(s.Label, snap.Label);
        Assert.Equal(s.State, snap.State);
        Assert.Equal(s.Options.Count, snap.Options.Count);
    }

    [Fact]
    public void EmptyOptionsThrow() {
        Assert.Throws<ArgumentException>(() => StartVote(options: System.Array.Empty<string>()));
    }

    [Fact]
    public void DurationLessThanOneSecondThrows() {
        Assert.Throws<ArgumentException>(() => StartVote(duration: TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void MoreThanTenOptionsThrows() {
        var eleven = new string[11];
        for (int i = 0; i < 11; i++) eleven[i] = $"opt{i}";
        Assert.Throws<ArgumentException>(() => StartVote(options: eleven));
    }

    [Fact]
    public void EmptyOrWhitespaceLabelThrows() {
        var bad = new[] { "  ", "" };
        Assert.Throws<ArgumentException>(() => StartVote(options: bad));
    }
}
