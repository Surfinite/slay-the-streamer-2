using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SlayTheStreamer2.Game.Bootstrap;

/// <summary>
/// Which Harmony patch classes stand or fall together.
///
/// Harmony registers one class at a time and stops at the first class that throws, so
/// a single member the game renamed used to leave the mod HALF-PATCHED: every class
/// before the throw live, every class after it missing, and nothing rolled back (game
/// v0.113.0 did exactly that to the published v0.4.3). A partly registered feature can
/// be worse than none: a prefix that suspends a click whose companion patch never
/// registered has no way to resume.
///
/// So patches register per FEATURE GROUP. If any class in a group throws while
/// patching, the whole group is unpatched, plus every group that builds on it, and the
/// rest of the mod carries on. This class is the pure part: the catalogue (class to
/// group, group dependencies), the failure closure, the runtime status other code
/// reads, and the one-line summary. The Harmony part is <c>PatchRegistrar</c>.
///
/// Groups are assigned by OUTERMOST class name, so a class nested in a patch container
/// (the skip gate's six, the origin tags, ...) belongs to its container's group.
/// A patch class missing from the catalogue still registers, as a group of its own.
/// </summary>
internal static class PatchGroups {
    internal const string CardVote = "card-vote";
    internal const string RewardModes = "reward-modes";
    internal const string RemoveOne = "remove-one";
    internal const string ExplainText = "explain-text";
    internal const string BossVote = "boss-vote";
    internal const string MapGuard = "map-guard";
    internal const string AncientVote = "ancient-vote";
    internal const string PaelExclusion = "pael-exclusion";
    internal const string ActVariant = "act-variant";
    internal const string CustomRandom = "custom-random";
    internal const string ChestRelics = "chest-relics";
    internal const string EliteRelics = "elite-relics";
    internal const string SealedNeow = "sealed-neow";
    internal const string RelicDisable = "relic-disable";
    internal const string VoterNames = "voter-names";
    internal const string SettingsUi = "settings-ui";

    private static readonly Dictionary<string, string> ClassToGroup = new(StringComparer.Ordinal) {
        // The card-reward vote and its skip gate share the Proceed/Skip/Reroll guards:
        // the vote suspends a click, the gate decides whether Proceed is allowed.
        ["CardRewardVotePatch"] = CardVote,
        ["CardRewardSkipGatePatch"] = CardVote,
        // Reward origin tags and the unskippable-rewards screen patches that act on them.
        ["CombatOriginTags"] = RewardModes,
        ["DraftOriginTags"] = RewardModes,
        ["RelicOriginTags"] = RewardModes,
        ["RestSiteOriginTags"] = RewardModes,
        ["UnskippableRewards"] = RewardModes,
        // Remove-one votes on the card-reward and choose-a-card screens.
        ["CardRewardRemovalSurface"] = RemoveOne,
        ["ChooseACardRemovePatch"] = RemoveOne,
        // The "Slay the Streamer:" explanation lines on relics and event options.
        ["LocTextPatch"] = ExplainText,
        ["EventOptionGrowPatch"] = ExplainText,
        ["BossVotePatch"] = BossVote,
        // Blocks the top-bar Map button during ANY vote, so it is not tied to one.
        ["TopBarMapButtonGuardPatch"] = MapGuard,
        ["AncientVotePatch"] = AncientVote,
        ["PaelOptionPool2NoWingPatch"] = PaelExclusion,
        ["ActVariantVotePatch"] = ActVariant,
        ["RandomCharacterButtonPatch"] = CustomRandom,
        ["RandomCharacterResolvePatch"] = CustomRandom,
        ["RandomCharacterRevealBeatPatch"] = CustomRandom,
        ["RandomCharacterDreadRefreshPatch"] = CustomRandom,
        ["RandomCharacterDreadEmbarkGuardPatch"] = CustomRandom,
        // Expanding a chest without the refund patches would lose relics from the pool.
        ["ChestRelicChoicePatch"] = ChestRelics,
        ["ChestRelicRefundPatch"] = ChestRelics,
        ["ChestRefundDemotionGuardPatch"] = ChestRelics,
        // The linked-set bookkeeping is what lets the elite rewards set complete.
        ["EliteRelicChoicePatch"] = EliteRelics,
        ["LinkedSetClaimBookkeepingPatch"] = EliteRelics,
        ["LinkedSetRefundPatch"] = EliteRelics,
        ["LinkedSetSignalRewirePatch"] = EliteRelics,
        ["TalismanReworkPatch"] = SealedNeow,
        ["DoomedIconPatch"] = SealedNeow,
        ["SealedNeowLocPatch"] = SealedNeow,
        ["RelicDisablePatch"] = RelicDisable,
        ["VoterNamesPatch"] = VoterNames,
        ["SettingsPanelPatch"] = SettingsUi,
        ["SettingsPanelClearPatch"] = SettingsUi,
    };

    /// <summary>Group to the groups it cannot work without.</summary>
    private static readonly Dictionary<string, string[]> Dependencies = new(StringComparer.Ordinal) {
        // The reward rules restrain Proceed through the skip gate's prefix.
        [RewardModes] = new[] { CardVote },
        // A removal vote needs the screens' guards and the origin tags that pick it.
        [RemoveOne] = new[] { CardVote, RewardModes },
        // The text explains rules that would no longer be enforced: without the
        // reward modes there are none, and without remove-one the lines that say
        // "chat votes to remove one option" describe a vote that no longer happens.
        [ExplainText] = new[] { RewardModes, RemoveOne },
    };

    internal static IReadOnlyList<string> All { get; } =
        ClassToGroup.Values.Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal).ToArray();

    /// <summary>The group of a patch class, by its OUTERMOST class name; null when the
    /// class was never assigned one.</summary>
    internal static string? GroupOf(string outermostClassName) =>
        ClassToGroup.TryGetValue(outermostClassName, out var group) ? group : null;

    internal static IReadOnlyList<string> DependenciesOf(string group) =>
        Dependencies.TryGetValue(group, out var deps) ? deps : Array.Empty<string>();

    /// <summary>Every group that must be off given these directly failed groups: the
    /// failed ones plus, transitively, every group that depends on one. Name order.</summary>
    internal static IReadOnlyList<string> Closure(IEnumerable<string> failed) {
        var off = new HashSet<string>(failed, StringComparer.Ordinal);
        bool grew = true;
        while (grew) {
            grew = false;
            foreach (var group in All) {
                if (off.Contains(group)) continue;
                if (DependenciesOf(group).Any(off.Contains)) { off.Add(group); grew = true; }
            }
        }
        return off.OrderBy(g => g, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The single godot.log line a tester reads to confirm registration.</summary>
    internal static string FormatSummary(string gameVersion, string modVersion, int totalGroups, int methodCount, IReadOnlyDictionary<string, string> failures) {
        var line = $"[SlayTheStreamer2] patch summary: game {gameVersion}, mod {modVersion}: "
            + $"{totalGroups - failures.Count}/{totalGroups} groups active, {methodCount} methods";
        foreach (var failure in failures.OrderBy(f => f.Key, StringComparer.Ordinal)) {
            line += $"; FAILED {failure.Key} ({failure.Value})";
        }
        return line;
    }

    /// <summary>What a streamer would call each group. Groups with no entry are
    /// internal or cosmetic and are left out of the chat notice.</summary>
    private static readonly Dictionary<string, string> StreamerFacingNames = new(StringComparer.Ordinal) {
        [CardVote] = "card reward votes",
        [RewardModes] = "non-combat card reward rules",
        [RemoveOne] = "remove-one votes",
        [BossVote] = "boss vote",
        [AncientVote] = "Ancient votes",
        [ActVariant] = "Act 1 vote",
        [CustomRandom] = "Custom Mode Random button",
        [ChestRelics] = "chest relic choices",
        [EliteRelics] = "elite relic choices",
        [SealedNeow] = "Sealed Deck Neow changes",
        [VoterNames] = "enemy names",
        [SettingsUi] = "in-game settings panel",
    };

    /// <summary>One sentence for the Twitch startup receipt naming the features that
    /// are off on this game version, or null when there is nothing worth saying. A
    /// group that only went down with a group it depends on is not named: the root
    /// cause already is.</summary>
    internal static string? ChatNotice(IReadOnlyDictionary<string, string> failures) {
        var names = failures.Keys
            .Where(group => !DependenciesOf(group).Any(failures.ContainsKey))
            .OrderBy(group => group, StringComparer.Ordinal)
            .Select(group => StreamerFacingNames.TryGetValue(group, out var name) ? name : null)
            .Where(name => name is not null)
            .ToArray();
        return names.Length == 0 ? null : $"Not working on this game version: {string.Join(", ", names)}.";
    }

    // ---- runtime status, written once by the registrar at mod init ----

    private static readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);

    /// <summary>Records a group as off. The first reason given is the one kept.</summary>
    internal static void MarkFailed(string group, string reason) => _failures.TryAdd(group, reason);

    /// <summary>True when the group's patches were rolled back (or never applied)
    /// because registration failed. False before registration and in unit tests, so
    /// code that ANDs a "patch is live" flag with this behaves as it always did.</summary>
    internal static bool IsFailed(string group) => _failures.ContainsKey(group);

    internal static IReadOnlyDictionary<string, string> Failures => _failures;
}
