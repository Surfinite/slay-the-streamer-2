using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Bossy Relics, chest surface: after vanilla pulls the single-player chest
/// relic, append N-1 extra pulls to the same picking session. The vanilla
/// treasure UI already renders 1-4 relics (SingleplayerRelicHolder +
/// MultiplayerRelicHolder1..4; HARD CAP 4) and its single-player vote
/// degenerate case enforces claim-one. Unclaimed relics are refunded on award
/// (Skipped results) or on full chest skip (which bypasses AwardRelics via
/// _singleplayerSkipped, hence the OnPicked postfix).
///
/// Chest pulls come from the SHARED grab bag only (BeginRelicPicking pulls
/// _sharedGrabBag.PullFromFront directly - the player bag is untouched), but
/// refunds here still use sharedBagOnly: false: the spec's decline semantics
/// require the PLAYER-bag copy to move to the back of its deque too, not
/// just stay wherever it already was (possibly the front, where the very
/// next elite offer would immediately re-offer it). Vanilla's later
/// award-animation tail still calls player.RelicGrabBag.MoveToFallback for
/// every Skipped result (see ChestRelicRefundPatch's doc comment below) -
/// ChestRefundDemotionGuardPatch blocks that demotion for relics this mod
/// has already refunded, so the moved-to-back position sticks.
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
internal static class ChestRelicChoicePatch {
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<RelicModel>?> CurrentRelicsRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<RelicModel>?>("_currentRelics");
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, RelicGrabBag> SharedBagRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, RelicGrabBag>("_sharedGrabBag");
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, IPlayerCollection> PlayersRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, IPlayerCollection>("_playerCollection");

    /// <summary>Extras (and the original) offered by the current chest session, pending refund.</summary>
    internal static readonly List<RelicModel> SessionOffer = new();

    static void Postfix(TreasureRoomRelicSynchronizer __instance) {
        try {
            SessionOffer.Clear();
            ChestRefundDemotionGuardPatch.ProtectedIds.Clear();
            int choices = RelicChoicePlanner.Clamp(ModSettings.Current?.RelicChoices ?? 1);
            if (choices <= 1) return;

            var players = PlayersRef(__instance).Players;
            if (players.Count != 1) return;
            var player = players[0];

            var current = CurrentRelicsRef(__instance);
            // Count == 0: empty chest / ShouldGenerateTreasure false - leave alone.
            // Count > 1: not the shape we expect (future game change) - leave alone.
            if (current is null || current.Count != 1) return;

            // First-ever-chest tutorial forces Gorget solo - keep any solo-Gorget
            // chest vanilla (over-broad by design: also skips rare later natural
            // Gorget draws, a missed expansion at worst). Compare by canonical Id
            // (not the Id.Entry string literal) to sidestep the UPPER_SNAKE_CASE
            // derivation landmine entirely.
            if (current[0].Id == ModelDb.Relic<Gorget>().Id) return;

            int extraCount = RelicChoicePlanner.ExtraCount(choices, current.Count, RelicChoicePlanner.MaxChoices);
            if (extraCount <= 0) return;

            var rng = SeedCompat.CreateRng(RelicChoicePlanner.OfferSeed(
                player.RunState.Rng?.StringSeed, "bossy-chest",
                player.RunState.CurrentActIndex, player.RunState.TotalFloor));

            // Track session offers incrementally (original relic(s) now, each
            // extra as it's pulled) so a mid-loop exception still leaves
            // whatever was already added refundable - not just whatever
            // happened to be in `current` after a post-loop AddRange.
            SessionOffer.AddRange(current);

            var sharedBag = SharedBagRef(__instance);
            for (int i = 0; i < extraCount; i++) {
                // Mirror the vanilla pull shape, rarity on OUR rng (vanilla's
                // chest stream position stays untouched -> the ORIGINAL relic is
                // identical to what vanilla would have offered).
                var rarity = RelicFactory.RollRarity(rng);
                var relic = sharedBag.PullFromFront(rarity, player.RunState);
                // Never pad with RelicFactory.FallbackRelic (Circlet singleton) -
                // a null pull means the shared bag is dry for this rarity, and
                // padding risks putting the SAME Circlet instance in the offer
                // twice, which would break the refund loop's id-based accounting.
                // Just offer fewer relics (vanilla's own rarity cascade makes a
                // null pull vanishingly rare anyway).
                if (relic is null) break;
                current.Add(relic);
                SessionOffer.Add(relic);
            }
            TiLog.Info($"[bossy-relics] chest offer expanded to {current.Count} relics");
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] ChestRelicChoicePatch failed; vanilla chest unchanged", ex);
        }
    }
}

/// <summary>
/// Refunds. Award path: OnPicked with a vote completes -> AwardRelics raises
/// results synchronously; relics nobody received (Skipped, player=null) go
/// back to the shared bag. Skip path: single-player skip sets
/// _singleplayerSkipped and NEVER calls AwardRelics - refund the whole
/// session offer right there. Postfixing OnPicked covers both (it is the
/// common entry), using the session list captured at BeginRelicPicking.
///
/// Ordering vs NTreasureRoomRelicCollection's MoveToFallback demotion
/// (verified against v0.109 decompile): AwardRelics raises RelicsAwarded
/// synchronously inside OnPicked, but its UI subscriber
/// (NTreasureRoomRelicCollection.OnRelicsAwarded) only KICKS OFF an async
/// animation coroutine (AnimateRelicAwards) - the actual
/// player.RelicGrabBag.MoveToFallback(relic) calls happen after several
/// awaited tween/Cmd.Wait animation beats, i.e. well AFTER this postfix has
/// already returned. Our refund (sharedBagOnly: false, so it also moves the
/// player-bag copy to the back of its deque per the spec's decline
/// semantics) runs FIRST, inline in this postfix and therefore synchronously
/// ahead of the award animation's demotion beat; vanilla's demotion runs
/// LATER, in the award animation tail, for every Skipped result (player=null,
/// which fires for the sole player in single-player). Because the player bag
/// and shared bag are populated from overlapping pools, that later demotion
/// can still find - and evict - the refunded relic's copy in the player's
/// own rarity deque, moving it to the unserialized _mpFallbackDequeue.
/// ChestRefundDemotionGuardPatch below
/// blocks that demotion for any relic we've already refunded this session.
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
internal static class ChestRelicRefundPatch {
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, IPlayerCollection> PlayersRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, IPlayerCollection>("_playerCollection");

    static void Postfix(TreasureRoomRelicSynchronizer __instance, Player player, int? index) {
        try {
            if (ChestRelicChoicePatch.SessionOffer.Count == 0) return;
            var players = PlayersRef(__instance).Players;
            if (players.Count != 1) return;

            // After a CLAIM the vanilla flow has already run AwardRelics +
            // EndRelicVoting (session over: _currentRelics is null). After a
            // SKIP, _currentRelics is still set (deferred to room exit).
            bool skipped = !index.HasValue;
            var offered = new List<RelicModel>(ChestRelicChoicePatch.SessionOffer);
            ChestRelicChoicePatch.SessionOffer.Clear();

            for (int i = 0; i < offered.Count; i++) {
                // Skip the claimed slot BY INDEX, not by id - two distinct
                // offered relics can share an id if the shared bag offered the
                // same model twice (e.g. two Circlets), and id-based dedup
                // would then wrongly skip the refund of the OTHER copy too.
                if (!skipped && index!.Value == i) continue;
                var relic = offered[i];
                ChestRefundDemotionGuardPatch.ProtectedIds.Add(relic.Id);
                RelicReturnHelper.ReturnToPools(player, relic, sharedBagOnly: false);
            }
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] chest refund failed", ex);
        }
    }
}

/// <summary>
/// Vanilla's treasure-award animation demotes every Skipped result out of each
/// player's grab bag via MoveToFallback - a multiplayer bookkeeping step that
/// solo vanilla never triggers (solo chests are always 1 relic, so there are
/// never Skipped results). Our N-relic offers do trigger it, and it runs AFTER
/// our refund, silently demoting the refunded relic's player-bag copy to the
/// unserialized last-resort deque. Skip the demotion for relics this mod has
/// refunded in the current chest session (single-player only - the guard set
/// is only ever populated behind the Players.Count == 1 gate).
/// </summary>
[HarmonyPatch(typeof(RelicGrabBag), nameof(RelicGrabBag.MoveToFallback))]
internal static class ChestRefundDemotionGuardPatch {
    /// <summary>Relic ids refunded by the current/most recent chest session.
    /// Populated by ChestRelicRefundPatch, cleared at the next BeginRelicPicking.</summary>
    internal static readonly HashSet<ModelId> ProtectedIds = new();

    static bool Prefix(RelicModel toRemove) {
        try {
            if (ProtectedIds.Contains(toRemove.Id)) {
                TiLog.Info($"[bossy-relics] blocked fallback demotion of refunded {toRemove.Id.Entry}");
                return false;   // skip vanilla demotion
            }
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] demotion guard failed; vanilla demotion proceeds", ex);
        }
        return true;
    }
}
