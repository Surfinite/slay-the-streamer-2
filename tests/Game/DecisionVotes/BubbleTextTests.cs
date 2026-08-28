using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class BubbleTextTests {

    [Fact]
    public void Plain_short_message_passes_through() {
        Assert.Equal("hello there", BubbleText.Sanitize("hello there"));
    }

    [Fact]
    public void Brackets_are_neutralized() {
        // The speech bubble renders BBCode; '[' must never survive.
        Assert.Equal("(b)bold(/b)", BubbleText.Sanitize("[b]bold[/b]"));
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed() {
        Assert.Equal("a b c", BubbleText.Sanitize("  a\r\n b\t\t c  "));
    }

    [Fact]
    public void Empty_and_whitespace_only_return_null() {
        Assert.Null(BubbleText.Sanitize("   "));
        Assert.Null(BubbleText.Sanitize(""));
    }

    [Fact]
    public void Long_message_truncates_at_word_boundary_with_ellipsis() {
        // 64-char budget, cut backtracks to the last space (original mod's rule).
        string msg = string.Join(" ", System.Linq.Enumerable.Repeat("word", 30)); // 149 chars
        string? result = BubbleText.Sanitize(msg);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 67);          // 64 + "..."
        Assert.EndsWith("...", result);
        Assert.DoesNotContain("wor...", result);    // never cuts mid-word when a space exists
    }

    [Fact]
    public void Long_single_token_truncates_hard() {
        string? result = BubbleText.Sanitize(new string('x', 100));
        Assert.Equal(new string('x', 64) + "...", result);
    }

    [Fact]
    public void RawCharCount_ignores_spaces() {
        Assert.Equal(10, BubbleText.RawCharCount("hello there"));
    }
}
