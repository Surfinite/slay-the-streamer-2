// src/Game/DecisionVotes/RemovalStatusText.cs
namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>BBCode wording for the removal-vote status line and the vote popup title
/// (spec section 3.2; rig-tested wording from Sabotage strike/18.7). Godot-free so it
/// rides the test csproj's DecisionVotes glob. Only the removal word is painted red,
/// in the same red as the removed option (Surfinite, 2026-09-13).</summary>
internal static class RemovalStatusText {
    /// <summary>RemovalVisuals.RemovedRed (1, 0.28, 0.28) as a BBCode colour.</summary>
    internal const string RedHex = "#FF4747";

    private static string Red(string word) => $"[color={RedHex}]{word}[/color]";

    internal static string Prompt() => $"Click any option to start the {Red("removal")} vote.";

    internal static string AfterRemoval(bool isSkip, string removedLabel, bool canOverride, bool hasReroll) {
        string text;
        if (isSkip) text = canOverride ? $"Chat {Red("removed")} Skip. Take a card, or spend an override to skip." : $"Chat {Red("removed")} Skip. You must take a card.";
        else text = canOverride
            ? $"Chat {Red("removed")} {Escape(removedLabel)}. Choose from the rest, or spend an override to take it."
            : $"Chat {Red("removed")} {Escape(removedLabel)}. Choose from the rest.";
        if (hasReroll) text += "\nYou can reroll these cards once, for free.";
        return text;
    }

    /// <summary>The vote hint is "[NN] " when tags are on; its brackets are escaped so
    /// BBCode does not read them as a tag.</summary>
    internal static string PopupTitle(string voteHint) => $"{Escape(voteHint)}Chat is choosing which option to {Red("remove")}";

    /// <summary>Card titles are game data; keep any bracket from opening a tag.</summary>
    private static string Escape(string s) {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s) sb.Append(c switch { '[' => "[lb]", ']' => "[rb]", _ => c.ToString() });
        return sb.ToString();
    }
}
