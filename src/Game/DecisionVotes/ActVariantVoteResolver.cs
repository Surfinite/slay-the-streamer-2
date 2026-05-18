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
    /// Per-variant fallback color constants used by ActVariantVoteResolver to
    /// populate FallbackColorHex on each ActVariantOption.
    ///
    /// The popup's L1 mode (full layered combat-backdrop scene) is built
    /// dynamically in ActVariantVotePatch via BackgroundAssets(key, rng) +
    /// NCombatBackground.Create(bg). The DTO doesn't carry asset paths
    /// directly — the patch derives them at vote-start from the variant Key.
    ///
    /// FallbackColorHex values from each ActModel.MapBgColor:
    ///   Overgrowth.MapBgColor = new Color("A78A67");
    ///   Underdocks.MapBgColor = new Color("9F95A5");
    /// </summary>
    internal static class ActVariantAssetPaths {
        internal const string OvergrowthFallbackHex = "A78A67";
        internal const string UnderdocksFallbackHex = "9F95A5";
    }

    internal static IReadOnlyList<ActVariantOption> BuildCandidates() {
        return new[] {
            new ActVariantOption(
                Index: 0,
                Key: "overgrowth",
                Title: "Overgrowth",
                FallbackColorHex: ActVariantAssetPaths.OvergrowthFallbackHex),
            new ActVariantOption(
                Index: 1,
                Key: "underdocks",
                Title: "Underdocks",
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

/// <summary>
/// Outcome of synchronous pre-warm. Mode flows into ActVariantVotePopup's
/// constructor; Succeeded/Total/ElapsedMs feed the pre-warm telemetry log
/// line. Pure-CLR — no Godot, no MegaCrit refs — so this type is reachable
/// from the test project.
/// </summary>
internal readonly record struct ActVariantPrewarmResult(
    SlayTheStreamer2.Game.Ui.ActVariantPopupMode Mode,
    int Succeeded,
    int Total,
    long ElapsedMs);
