using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class VoterNamePoolTests {
    private static VoterNamePool Pool(int seed = 42) => new(new Random(seed));

    private static Dictionary<string, string> Voters(params (string Key, string Name)[] voters)
        => voters.ToDictionary(v => v.Key, v => v.Name);

    [Fact]
    public void Empty_pool_returns_false() {
        var pool = Pool();
        Assert.False(pool.TryTakeName(out _, out _));
    }

    [Fact]
    public void Every_distinct_voter_used_once_before_any_repeat() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice"), ("b", "Bob"), ("c", "Carol")));

        var firstRound = new List<string>();
        for (int i = 0; i < 3; i++) {
            Assert.True(pool.TryTakeName(out var name, out var key));
            firstRound.Add(key);
            Assert.DoesNotContain(" Jr.", name);
        }
        Assert.Equal(3, firstRound.Distinct().Count());   // all three keys, no repeats
    }

    [Fact]
    public void Second_use_is_Jr_third_is_roman_III() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice")));

        Assert.True(pool.TryTakeName(out var first, out _));
        Assert.Equal("Alice", first);
        Assert.True(pool.TryTakeName(out var second, out _));
        Assert.Equal("Alice Jr.", second);
        Assert.True(pool.TryTakeName(out var third, out _));
        Assert.Equal("Alice III", third);
        Assert.True(pool.TryTakeName(out var fourth, out _));
        Assert.Equal("Alice IV", fourth);
    }

    [Fact]
    public void New_voter_joining_mid_session_gets_priority_over_used_voters() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice")));
        Assert.True(pool.TryTakeName(out _, out _));          // Alice used once

        pool.AddVoters(Voters(("b", "Bob")));
        Assert.True(pool.TryTakeName(out var name, out var key));
        Assert.Equal("b", key);                                // Bob (count 0) before Alice Jr.
        Assert.Equal("Bob", name);
    }

    [Fact]
    public void Re_adding_a_voter_updates_display_name_but_keeps_used_count() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "OldName")));
        Assert.True(pool.TryTakeName(out _, out _));           // count -> 1

        pool.AddVoters(Voters(("a", "NewName")));
        Assert.True(pool.TryTakeName(out var name, out _));
        Assert.Equal("NewName Jr.", name);                     // count preserved -> Jr.
    }

    [Fact]
    public void Names_are_trimmed_and_capped_at_25_chars_with_ellipsis() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "  " + new string('x', 40) + "  ")));
        Assert.True(pool.TryTakeName(out var name, out _));
        Assert.Equal(new string('x', 24) + "…", name);    // 24 + ellipsis = 25
    }

    [Fact]
    public void Empty_or_whitespace_display_names_are_dropped() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "   ")));
        Assert.False(pool.TryTakeName(out _, out _));
        Assert.Equal(0, pool.DistinctVoterCount);
    }

    [Fact]
    public void Selection_among_least_used_is_seed_deterministic() {
        var a = Pool(seed: 7);
        var b = Pool(seed: 7);
        var voters = Voters(("a", "Alice"), ("b", "Bob"), ("c", "Carol"), ("d", "Dave"));
        a.AddVoters(voters);
        b.AddVoters(voters);
        for (int i = 0; i < 8; i++) {
            Assert.True(a.TryTakeName(out var nameA, out _));
            Assert.True(b.TryTakeName(out var nameB, out _));
            Assert.Equal(nameA, nameB);
        }
    }

    [Theory]
    [InlineData(3, "III")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(40, "XL")]
    public void Roman_numerals(int value, string expected) {
        Assert.Equal(expected, RomanNumerals.Convert(value));
    }
}
