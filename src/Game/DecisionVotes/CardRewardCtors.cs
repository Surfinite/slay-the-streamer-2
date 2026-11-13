using System;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// The CardReward constructors the origin-tag patches postfix, found by SHAPE (every
/// instance constructor that takes a <c>Player player</c>) rather than by exact
/// argument list. Game v0.113.0 appended <c>CardSelectorStack? selectorStack = null</c>
/// to both constructors; the exact-list <c>[HarmonyPatch(..., MethodType.Constructor,
/// new[] { ... })]</c> attributes stopped matching, Harmony threw "Undefined target
/// method", and that aborted patch registration for the whole mod.
///
/// Never name the appended type in a patch attribute: it does not exist on the
/// default branch (v0.107.1), and one DLL serves both.
/// </summary>
internal static class CardRewardCtors {
    private static readonly Lazy<IReadOnlyList<ConstructorInfo>> _withPlayer = new(() =>
        MemberCompat.ConstructorsWithParameter(typeof(CardReward), "player", typeof(Player)));

    /// <summary>Two on every game version checked (v0.107.1, v0.111.0, v0.113.0).</summary>
    internal static IReadOnlyList<ConstructorInfo> WithPlayer => _withPlayer.Value;

    /// <summary>Shared Harmony <c>Prepare</c> for a constructor-tagging patch. Returning
    /// false at registration time skips the class cleanly; an empty
    /// <c>TargetMethods()</c> without this would fall through to attribute resolution
    /// and throw.</summary>
    internal static bool Prepare(MethodBase? original, string tagKind) {
        if (original is not null) return true;
        int count = WithPlayer.Count;
        if (count == 0) {
            TiLog.Error($"[SlayTheStreamer2][card-scope] no CardReward constructor takes a Player (game update?); {tagKind} tags will not be applied");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][card-scope] {tagKind} tags bound to {count} CardReward constructor(s)");
        return true;
    }
}
