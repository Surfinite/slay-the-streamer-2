using System.Collections.Generic;
using SlayTheStreamer2.Game.Ui;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure-CLR helpers for the B.3.2 act-variant vote. No Godot, no MegaCrit,
/// no TiLog. Lives in src/Game/DecisionVotes/ so it's picked up by the test
/// csproj's auto-include glob.
/// </summary>
internal static class ActVariantVoteResolver {

    /// <summary>
    /// Asset paths discovered during the Task 1 spike. Background paths use
    /// each variant's MapMidBgPath (per-variant PNG) - the spec's original
    /// "combat-bg PNG" target doesn't exist in vanilla as a single texture.
    /// Banner constants stay null because per-variant banner textures don't
    /// exist; the popup renders title text instead (sourced from
    /// ActModel.Title.GetFormattedText() at runtime, OR the static Title
    /// field on each ActVariantOption).
    ///
    /// FilePathIdentifier verification (B.3.2 Task 3):
    ///   ActModel.FilePathIdentifier => Id.Entry.ToLowerInvariant()
    ///   Id.Entry is StringHelper.Slugify(type.Name) per ModelDb.GetEntry.
    ///   Slugify inserts '_' at camel-case boundaries then uppercases;
    ///   single-word class names ("Overgrowth", "Underdocks") have no
    ///   internal boundaries, so Slugify("Overgrowth") = "OVERGROWTH" and
    ///   FilePathIdentifier = "overgrowth". Same shape for "Underdocks".
    ///   Neither Overgrowth.cs nor Underdocks.cs overrides FilePathIdentifier.
    ///
    /// FallbackColorHex values come from each ActModel.MapBgColor:
    ///   Overgrowth.MapBgColor = new Color("A78A67");
    ///   Underdocks.MapBgColor = new Color("9F95A5");
    /// </summary>
    internal static class ActVariantAssetPaths {
        internal const string? OvergrowthCombatBackground =
            "res://images/packed/map/map_bgs/overgrowth/map_middle_overgrowth.png";
        internal const string? UnderdocksCombatBackground =
            "res://images/packed/map/map_bgs/underdocks/map_middle_underdocks.png";
        internal const string? OvergrowthEntryBanner = null;
        internal const string? UnderdocksEntryBanner = null;

        internal const string OvergrowthFallbackHex = "A78A67";
        internal const string UnderdocksFallbackHex = "9F95A5";
    }

    internal static IReadOnlyList<ActVariantOption> BuildCandidates() {
        return new[] {
            new ActVariantOption(
                Index: 0,
                Key: "overgrowth",
                Title: "Overgrowth",
                BackgroundPath: ActVariantAssetPaths.OvergrowthCombatBackground,
                BannerPath: ActVariantAssetPaths.OvergrowthEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.OvergrowthFallbackHex),
            new ActVariantOption(
                Index: 1,
                Key: "underdocks",
                Title: "Underdocks",
                BackgroundPath: ActVariantAssetPaths.UnderdocksCombatBackground,
                BannerPath: ActVariantAssetPaths.UnderdocksEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.UnderdocksFallbackHex),
        };
    }

    internal static string ResolveWinnerKey(IReadOnlyList<ActVariantOption> options, int? winnerIndex) {
        if (winnerIndex is null) return "random";
        if (winnerIndex < 0 || winnerIndex >= options.Count) return "random";
        return options[winnerIndex.Value].Key;
    }

    /// <summary>
    /// Bail reasons returnable as pure-function output. Atomic-acquire bails
    /// (ResumeInProgress, VoteInProgress) are intentionally NOT in this enum —
    /// they have Interlocked.CompareExchange semantics that pure functions
    /// cannot replicate. Those bails are handled inline in
    /// ActVariantVotePatch.Prefix and verified by operator-validation
    /// Gates 7 (spam-Embark) and 12 (Embark→ESC→Embark cycle).
    /// </summary>
    internal enum BailReason {
        None,
        SettingsOff,
        Multiplayer,
        ChatUnreadable,
        Act1Pinned,
        PoolDegenerate,
    }

    internal static BailReason ShouldBail(
            bool settingsEnabled,
            int playerCount,
            SlayTheStreamer2.Ti.Chat.ChatConnectionState chatState,
            string act1Value,
            int candidateCount) {
        if (!settingsEnabled) return BailReason.SettingsOff;
        if (playerCount > 1) return BailReason.Multiplayer;
        if (chatState is not (
                SlayTheStreamer2.Ti.Chat.ChatConnectionState.ConnectedReadWrite or
                SlayTheStreamer2.Ti.Chat.ChatConnectionState.ConnectedReadOnly))
            return BailReason.ChatUnreadable;
        if (!string.Equals(act1Value, "random", System.StringComparison.Ordinal))
            return BailReason.Act1Pinned;
        if (candidateCount <= 1) return BailReason.PoolDegenerate;
        return BailReason.None;
    }
}
