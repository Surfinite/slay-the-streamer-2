// src/Game/Content/TalismanReworkPatch.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Random;
using SlayTheStreamer2.Ti.Internal;

using SlayTheStreamer2.Game.DecisionVotes;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.1. Vanilla NeowsTalisman.AfterObtained upgrades the last
/// Basic Strike and Defend (a no-op in a sealed deck). In a sealed run this prefix
/// substitutes: upgrade TalismanCards random upgradable cards and enchant each with
/// StreamerDoomed at TalismanDoom. The picks use a run-seeded Rng. AfterObtained is never
/// re-run on Continue; the seeded Rng keeps the pick deterministic per run seed and act
/// without touching the run's own RNG streams. Whether the mutation survives a
/// save-quit-Continue depends on the save checkpoint (see the CLAUDE.md mid-room
/// mutation landmine); matrix row S3 covers it. The upgrade preview is suppressed
/// (CardPreviewStyle.None) and the enchant VFX kept, which shows the card in its final
/// upgraded and enchanted state. Everything else (Pomander flip, Bones eligibility,
/// icon, save shape) stays vanilla. Fails open to vanilla.</summary>
[HarmonyPatch(typeof(NeowsTalisman), nameof(NeowsTalisman.AfterObtained))]
internal static class TalismanReworkPatch {
    private const string Salt = "slay-the-streamer|talisman";

    static bool Prefix(NeowsTalisman __instance, ref Task __result) {
        try {
            if (!SealedDeckRun.IsActive()) return true;
            __result = Rework(__instance);
            return false;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][sealed-neow] talisman rework prefix failed; vanilla Talisman", ex);
            return true;
        }
    }

    private static Task Rework(NeowsTalisman relic) {
        try {
            var owner = relic.Owner;
            var canonical = ModelDb.Enchantment<StreamerDoomed>();
            var candidates = PileType.Deck.GetPile(owner).Cards
                .Where(c => c.IsUpgradable && canonical.CanEnchant(c))
                .ToList();
            var runState = owner.RunState;
            ulong seed = TalismanPickRules.Fnv1a64($"{runState.Rng?.StringSeed}|{Salt}|{runState.CurrentActIndex}");
            // SeedCompat: Rng(ulong) only exists on game >= v0.109.0; the default branch
            // (v0.107.1) has Rng(uint, int). One DLL serves both, so bind reflectively and
            // fold the 64-bit hash to 32 bits (still deterministic per run seed and act).
            var rng = SeedCompat.CreateRng(unchecked((uint)(seed ^ (seed >> 32))));
            var picks = TalismanPickRules.PickIndices(candidates.Count, SealedNeowLoc.TalismanCards, max => rng.NextInt(max));
            foreach (int i in picks) {
                var card = candidates[i];
                CardCmd.Upgrade(card, CardPreviewStyle.None);
                CardCmd.Enchant(canonical.ToMutable(), card, SealedNeowLoc.TalismanDoom);
                var vfx = NCardEnchantVfx.Create(card);
                if (vfx is not null) NRun.Instance?.GlobalUi.CardPreviewContainer.AddChildSafely(vfx);
            }
            TiLog.Info($"[SlayTheStreamer2][sealed-neow] talisman: upgraded+doomed {picks.Count} of {candidates.Count} candidates (doom={SealedNeowLoc.TalismanDoom})");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][sealed-neow] talisman rework failed mid-way", ex);
        }
        return Task.CompletedTask;
    }
}
