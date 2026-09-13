// src/Game/DecisionVotes/AuthorityLoc.cs
using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.Bootstrap;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.2: the explanation text appended to vanilla relic and event
/// strings, word for word from the rig-tested Sabotage catalogue with chat phrasing.
/// Pure: SuffixFor answers "what goes after this table key" and Append is idempotent.
/// No em dashes; the verb is "remove".</summary>
public static class AuthorityLoc {
    public const string AccentHex = "#668CFF";
    public const string Lead = "\n[color=" + AccentHex + "]Slay the Streamer:[/color] ";

    private static readonly Dictionary<string, string> Relics = new(StringComparer.Ordinal) {
        ["KALEIDOSCOPE"] = "for each card reward, chat votes to remove one option, Skip included, and you pick from the rest.",
        ["GLASS_EYE"] = "chat votes to remove one option on each of the five card rewards. You pick from the rest.",
        ["LOST_COFFER"] = "chat votes to remove one option from the card reward.",
        ["HEFTY_TABLET"] = "chat votes to remove one of the four options, Skip included. [red]Injury[/red] is added either way.",
        ["LEAD_PAPERWEIGHT"] = "chat votes to remove one of the three options, Skip included.",
        ["NEOWS_BONES"] = "card rewards from relics pulled here use the removal vote.",
        ["DREAM_CATCHER"] = "chat votes to remove one of the card-reward options each time you [gold]Rest[/gold].",
        ["DRIFTWOOD"] = "after chat removes an option from a card reward, you may reroll once for free. Chat then votes again on the new cards.",
        ["ORRERY"] = "you must take a card from each of the five rewards.",
        ["STRONGBOX"] = "you must take a card from each of the two rewards.",
    };

    private static readonly Dictionary<string, string> RelicExtraKeys = new(StringComparer.Ordinal) {
        ["DREAM_CATCHER.additionalRestSiteHealText"] = "chat removes one of the options first.",
    };

    private const string Unskippable = "the card rewards cannot be skipped.";
    // Options that grant exactly one CardReward read singular (Surfinite, 2026-09-13):
    // Brain Leech (RewardCount 1) and The Future of Potions (one reward). Trial Guilty
    // grants two, Colorful Philosophers three per option.
    private const string UnskippableSingle = "the card reward cannot be skipped.";
    private const string CrystalSphere = "you will not have the option to skip cards uncovered here.";

    private static readonly Dictionary<string, string> Events = new(StringComparer.Ordinal) {
        ["TRASH_HEAP.pages.INITIAL.options.DIVE_IN.description"] = "if Dream Catcher is found here, chat will vote to remove one of your card-reward options when you [gold]Rest[/gold].",
        ["CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE.description"] = CrystalSphere,
        ["CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN.description"] = CrystalSphere,
        ["CRYSTAL_SPHERE.minigame.instructions.description"] = CrystalSphere,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.NECROBINDER.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.IRONCLAD.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.REGENT.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.SILENT.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.DEFECT.description"] = Unskippable,
        ["THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.description"] = UnskippableSingle,
        ["BRAIN_LEECH.pages.INITIAL.options.RIP.description"] = UnskippableSingle,
        ["TRIAL.pages.NONDESCRIPT.options.GUILTY.description"] = Unskippable,
    };

    private const string GenericRemoveOne = "chat votes to remove one option from this relic's card rewards.";
    private const string GenericUnskippable = "card rewards from this relic cannot be skipped.";

    // removeOne mode variants (handoff 2026-09-13 section 5.2): the unskippable surfaces
    // become removal votes, so their lines say so. Keys absent here keep the Mixed text.
    private const string RemoveOneEvent = "chat votes to remove one option from the card rewards.";
    private const string RemoveOneEventSingle = "chat votes to remove one option from the card reward.";
    private const string RemoveOneCrystalSphere = "chat votes to remove one of the cards uncovered here.";

    private static readonly Dictionary<string, string> RemoveOneRelics = new(StringComparer.Ordinal) {
        ["ORRERY"] = "chat votes to remove one option from each of the five card rewards.",
        ["STRONGBOX"] = "chat votes to remove one option from each of the two card rewards.",
    };

    private static readonly Dictionary<string, string> RemoveOneEvents = new(StringComparer.Ordinal) {
        ["CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE.description"] = RemoveOneCrystalSphere,
        ["CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN.description"] = RemoveOneCrystalSphere,
        ["CRYSTAL_SPHERE.minigame.instructions.description"] = RemoveOneCrystalSphere,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.NECROBINDER.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.IRONCLAD.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.REGENT.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.SILENT.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.DEFECT.description"] = RemoveOneEvent,
        ["THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.description"] = RemoveOneEventSingle,
        ["BRAIN_LEECH.pages.INITIAL.options.RIP.description"] = RemoveOneEventSingle,
        ["TRIAL.pages.NONDESCRIPT.options.GUILTY.description"] = RemoveOneEvent,
    };

    public static IReadOnlyCollection<string> BuiltInRelicIds => Relics.Keys;
    public static IReadOnlyCollection<string> EventKeys => Events.Keys;

    /// <summary>The suffix for (table, key) under <paramref name="mode"/>, or null when the
    /// key carries no text. Free mode never reaches here (RulesActive is false).</summary>
    public static string? SuffixFor(string table, string key, RelicTextRegistry registry, NonCombatRewardMode mode = NonCombatRewardMode.Mixed) {
        bool removeOne = mode == NonCombatRewardMode.RemoveOne;
        switch (table) {
            case "relics":
                if (RelicExtraKeys.TryGetValue(key, out var extra)) return Lead + extra;
                int dot = key.LastIndexOf('.');
                if (dot <= 0) return null;
                string id = key.Substring(0, dot), field = key.Substring(dot + 1);
                if (field is not ("description" or "eventDescription")) return null;
                if (removeOne && RemoveOneRelics.TryGetValue(id, out var r1)) return Lead + r1;
                if (Relics.TryGetValue(id, out var line)) return Lead + line;
                if (registry.TryGetLearnedMode(id, out var learned))
                    return Lead + (removeOne || learned == AuthorityMode.RemoveOne ? GenericRemoveOne : GenericUnskippable);
                return null;
            case "events":
                string? ev = null;
                if (removeOne) RemoveOneEvents.TryGetValue(key, out ev);
                if (ev is null && !Events.TryGetValue(key, out ev)) return null;
                // The minigame instructions separate every sentence with a blank line.
                return key.EndsWith("minigame.instructions.description", StringComparison.Ordinal) ? "\n" + Lead + ev : Lead + ev;
            default:
                return null;
        }
    }

    /// <summary>Idempotent append: the fallback-table recursion calls GetRawText twice.</summary>
    public static string Append(string raw, string suffix) =>
        raw.EndsWith(suffix, StringComparison.Ordinal) ? raw : raw + suffix;

    /// <summary>Every (table, key) the catalogue answers, for tests.</summary>
    public static IEnumerable<(string Table, string Key)> AllKeysForTests() {
        foreach (var id in Relics.Keys) { yield return ("relics", id + ".description"); yield return ("relics", id + ".eventDescription"); }
        foreach (var k in RelicExtraKeys.Keys) yield return ("relics", k);
        foreach (var k in Events.Keys) yield return ("events", k);
        yield return ("relics", "X.description");
        yield return ("relics", "Y.description");
    }
}
