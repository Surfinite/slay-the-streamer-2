using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2 text. Provide = keys vanilla does not have (the Doomed
/// enchantment; answered whenever asked). Replace = vanilla keys whose text changes only
/// during a sealed run (the caller gates). {Amount} is substituted by the engine from
/// the enchantment instance; the Talisman numbers are baked from the constants.</summary>
public static class SealedNeowLoc {
    public const int TalismanCards = 2;
    public const int TalismanDoom = 5;
    public const string DoomedId = "STREAMER_DOOMED";
    public const string TalismanId = "NEOWS_TALISMAN";

    private static readonly Dictionary<string, string> Provided = new(StringComparer.Ordinal) {
        [DoomedId + ".title"] = "Doomed",
        [DoomedId + ".description"] = "Apply [blue]{Amount}[/blue] [gold]Doom[/gold] to you when played.",
        [DoomedId + ".extraCardText"] = "Apply {Amount} [gold]Doom[/gold] to you.",
    };

    private static readonly Dictionary<string, string> Replaced = new(StringComparer.Ordinal) {
        [TalismanId + ".description"] = $"Upon pickup, [gold]Upgrade[/gold] [blue]{TalismanCards}[/blue] random cards. They become [red]Doomed[/red]: apply [red]{TalismanDoom}[/red] [gold]Doom[/gold] to you when played.",
        [TalismanId + ".eventDescription"] = $"[gold]Upgrade[/gold] [blue]{TalismanCards}[/blue] random cards. They become [red]Doomed[/red]: apply [red]{TalismanDoom}[/red] [gold]Doom[/gold] to you when played.",
    };

    public static bool TryProvide(string table, string key, out string text) {
        string? found = null;
        bool hit = table == "enchantments" && Provided.TryGetValue(key, out found);
        text = found ?? "";
        return hit;
    }

    public static bool TryReplace(string table, string key, out string text) {
        string? found = null;
        bool hit = table == "relics" && Replaced.TryGetValue(key, out found);
        text = found ?? "";
        return hit;
    }
}
