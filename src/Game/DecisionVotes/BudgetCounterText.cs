namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>BBCode for the streamer budget counters (card skips, vote overrides), shared
/// by the card-reward screen label and the Ancient vote popup. Godot-free.</summary>
public static class BudgetCounterText {
    // Vanilla compendium rarity colours: Uncommon cyan-blue and Rare yellow-gold.
    public const string SkipAccentHex = "#87CEEB";
    public const string OverrideAccentHex = "#EFC851";

    public static string Skips(string streamer, int remaining) =>
        Line(streamer, remaining, remaining == 1 ? "card skip" : "card skips", SkipAccentHex);

    public static string Overrides(string streamer, int remaining) =>
        Line(streamer, remaining, remaining == 1 ? "vote override" : "vote overrides", OverrideAccentHex);

    private static string Line(string streamer, int remaining, string noun, string hex) =>
        $"[center]{streamer} has [b][color={hex}]{remaining} {noun}[/color][/b] remaining this act[/center]";
}
