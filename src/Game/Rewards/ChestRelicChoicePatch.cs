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
/// _sharedGrabBag.PullFromFront directly - the player bag is untouched), so
/// refunds here use sharedBagOnly: true. Vanilla's MoveToFallback demotion of
/// leftover relics in the PLAYER bag is undone by ReturnToPools' fallback
/// scrub + our not-inserting into the player deque (the relic never left it).
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
            int choices = RelicChoicePlanner.Clamp(ModSettings.Current?.RelicChoices ?? 1);
            if (choices <= 1) return;

            var players = PlayersRef(__instance).Players;
            if (players.Count != 1) return;
            var player = players[0];

            var current = CurrentRelicsRef(__instance);
            // Count == 0: empty chest / ShouldGenerateTreasure false - leave alone.
            // Count > 1: not the shape we expect (future game change) - leave alone.
            if (current is null || current.Count != 1) return;

            // First-ever-chest tutorial forces Gorget solo - keep it solo. Compare
            // by canonical Id (not the Id.Entry string literal) to sidestep the
            // UPPER_SNAKE_CASE derivation landmine entirely.
            if (current[0].Id == ModelDb.Relic<Gorget>().Id) return;

            int extraCount = RelicChoicePlanner.ExtraCount(choices, current.Count, RelicChoicePlanner.MaxChoices);
            if (extraCount <= 0) return;

            var rng = SeedCompat.CreateRng(RelicChoicePlanner.OfferSeed(
                player.RunState.Rng?.StringSeed, "bossy-chest",
                player.RunState.CurrentActIndex, player.RunState.TotalFloor));

            var sharedBag = SharedBagRef(__instance);
            for (int i = 0; i < extraCount; i++) {
                // Mirror the vanilla pull shape, rarity on OUR rng (vanilla's
                // chest stream position stays untouched -> the ORIGINAL relic is
                // identical to what vanilla would have offered).
                var rarity = RelicFactory.RollRarity(rng);
                var relic = sharedBag.PullFromFront(rarity, player.RunState) ?? RelicFactory.FallbackRelic;
                current.Add(relic);
            }
            SessionOffer.AddRange(current);
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
/// already returned, not before. This turns out not to matter: that
/// demotion runs on each player's OWN RelicGrabBag, and chest pulls only
/// ever came from the SHARED bag, so RelicGrabBag.MoveToFallback finds no
/// matching entry in the player's own deques (see RelicGrabBag.cs -
/// MoveToFallback is a no-op when the relic was never present) and never
/// touches _mpFallbackDequeue for these relics. Our refund
/// (sharedBagOnly: true) only ever touches the SHARED bag, so the two
/// writes are on disjoint bags regardless of ordering.
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
            RelicModel? claimed = (!skipped && index!.Value >= 0 && index.Value < offered.Count) ? offered[index.Value] : null;
            ChestRelicChoicePatch.SessionOffer.Clear();

            foreach (var relic in offered) {
                if (claimed != null && relic.Id == claimed.Id) continue;
                RelicReturnHelper.ReturnToPools(player, relic, sharedBagOnly: true);
            }
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] chest refund failed", ex);
        }
    }
}
