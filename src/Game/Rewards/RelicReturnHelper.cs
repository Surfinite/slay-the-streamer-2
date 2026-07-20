using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Returns unclaimed Bossy-Relics offers to the relic economy: appends the
/// CANONICAL model to the BACK of its rarity deque (vanilla pulls scan from
/// the front, so "back" = seen again only after the rest of that rarity has
/// cycled — locked decision, spec 2026-07-20). Pulls are destructive to both
/// the player bag and the shared bag (RelicFactory.PullNextRelicFromFront),
/// so restoration mirrors both. Chest pulls only touch the shared bag
/// directly, but the spec's decline semantics still require the PLAYER-bag
/// copy to move to the back too (so the very next elite offer doesn't
/// immediately re-offer a relic chat just declined) - see
/// ChestRelicRefundPatch for why chest refunds pass sharedBagOnly: false.
/// </summary>
internal static class RelicReturnHelper {
    // RelicGrabBag internals: Dictionary<RelicRarity, List<RelicModel>> _deques
    // (front = index 0) and List<RelicModel> _mpFallbackDequeue (where vanilla's
    // MoveToFallback demotes chest leftovers). Verified against v0.109 decompile
    // (MegaCrit.Sts2.Core.Runs/RelicGrabBag.cs) - field names unchanged.
    private static readonly AccessTools.FieldRef<RelicGrabBag, Dictionary<RelicRarity, List<RelicModel>>> DequesRef =
        AccessTools.FieldRefAccess<RelicGrabBag, Dictionary<RelicRarity, List<RelicModel>>>("_deques");
    private static readonly AccessTools.FieldRef<RelicGrabBag, List<RelicModel>> FallbackRef =
        AccessTools.FieldRefAccess<RelicGrabBag, List<RelicModel>>("_mpFallbackDequeue");

    internal static void ReturnToPools(Player player, RelicModel relic, bool sharedBagOnly) {
        try {
            // Deques hold CANONICAL models; rewards hold ToMutable() copies.
            var canonical = ModelDb.GetByIdOrNull<RelicModel>(relic.Id);
            if (canonical is null) {
                TiLog.Warn($"[bossy-relics] cannot return {relic.Id}: no canonical model");
                return;
            }
            // Never return a relic the player now owns (they claimed it, or own
            // a one-of-a-kind copy) - IsAllowed pruning would drop it anyway,
            // but don't rely on that. player.Relics is IReadOnlyList<RelicModel>
            // directly (verified against v0.109 decompile, Player.cs:81) - each
            // entry exposes .Id itself, not via a wrapper's .Model.Id.
            foreach (var owned in player.Relics) {
                if (owned.Id == canonical.Id) return;
            }
            if (!sharedBagOnly) ReturnToBag(player.RelicGrabBag, canonical);
            ReturnToBag(player.RunState.SharedRelicGrabBag, canonical);
            TiLog.Info($"[bossy-relics] returned {canonical.Id.Entry} to pool (back of {canonical.Rarity} deque{(sharedBagOnly ? ", shared bag only" : "")})");
        } catch (System.Exception ex) {
            TiLog.Error($"[bossy-relics] ReturnToPools failed for {relic?.Id}", ex);
        }
    }

    private static void ReturnToBag(RelicGrabBag bag, RelicModel canonical) {
        // Undo a vanilla MoveToFallback demotion if it beat us to this relic.
        FallbackRef(bag).RemoveAll(r => r.Id == canonical.Id);

        var deques = DequesRef(bag);
        if (!deques.TryGetValue(canonical.Rarity, out var deque)) {
            deque = new List<RelicModel>();
            deques[canonical.Rarity] = deque;
        }
        // "Return to pool" always means back-of-deque, whether or not a copy
        // was already present: if the canonical relic is already in the
        // deque (e.g. a chest refund's player-bag copy never left it),
        // remove that entry first so the Add below always moves it to the
        // back, rather than leaving it wherever it happened to sit.
        deque.RemoveAll(r => r.Id == canonical.Id);
        deque.Add(canonical);   // back of deque
    }
}
