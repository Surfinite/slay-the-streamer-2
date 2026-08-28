using System.Text.RegularExpressions;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: chat-message hygiene for speech bubbles. The bubble label
/// renders BBCode, so square brackets are neutralized to parentheses
/// (renderer-agnostic — no dependency on a particular escape syntax).
/// 64-char budget with word-boundary backtrack copies the StS1 mod's rule.
/// Pure BCL — rides the test glob.
/// </summary>
internal static class BubbleText {
    private const int MaxLength = 64;

    public static string? Sanitize(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Replace('[', '(').Replace(']', ')');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxLength) {
            text = text.Substring(0, MaxLength);
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace > 0) text = text.Substring(0, lastSpace);
            text += "...";
        }
        return text;
    }

    /// <summary>Display-duration input: char count sans spaces (vanilla TalkCmd's rule).</summary>
    public static int RawCharCount(string text) => text.Replace(" ", "").Length;
}
