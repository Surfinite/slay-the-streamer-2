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
    /// Asset paths discovered during the Task 1 spike and refined after the user's
    /// full asset-extraction pass (2026-05-18). Background paths use each variant's
    /// back/sky combat-layer PNG (images/rooms/&lt;variant&gt;/&lt;variant&gt;_00.png) — this
    /// is the canonical "small-preview" thumbnail per notes/asset-extraction.md.
    /// (An earlier pick of MapMidBgPath was wrong: that's the map-screen strip,
    /// not the combat backdrop. Real combat backdrops are layered Control scenes
    /// at scenes/backgrounds/&lt;variant&gt;/&lt;variant&gt;_background.tscn with 5 parallax
    /// layers driven by NCombatBackground.cs — too heavy for a popup thumbnail.)
    ///
    /// Banner constants stay null because per-variant banner textures don't
    /// exist; the popup renders title text via ActVariantOption.Title at runtime.
    ///
    /// FallbackColorHex values come from each ActModel.MapBgColor:
    ///   Overgrowth.MapBgColor = new Color("A78A67");
    ///   Underdocks.MapBgColor = new Color("9F95A5");
    /// </summary>
    internal static class ActVariantAssetPaths {
        internal const string? OvergrowthCombatBackground =
            "res://images/rooms/overgrowth/overgrowth_00.png";
        internal const string? UnderdocksCombatBackground =
            "res://images/rooms/underdocks/underdocks_00.png";
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
