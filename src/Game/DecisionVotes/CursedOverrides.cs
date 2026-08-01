using System;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Cursed Overrides (spec 2026-08-01): each vote-override spend adds a random
/// curse to the overriding player's deck. Game-side half of the feature —
/// pool build + deck add. The pure picker lives in CurseRoll.cs so it can
/// ride the test csproj glob; this file is Compile Remove'd there.
/// Called strictly AFTER VoteOverrideBudget.RecordUse(). A failure here must
/// never break the override: every path catches, Warns, and returns null.
/// </summary>
internal static class CursedOverrides {
    /// <summary>Private RNG — never the run's seeded RNG, which would shift
    /// vanilla's subsequent rolls.</summary>
    private static readonly Random _rng = new();

    /// <summary>Rolls and fire-and-forget-adds a curse for the local player.
    /// Card-reward override sites use this (the screen acts for the local
    /// player). Returns the curse Title for the receipt, or null.</summary>
    internal static string? TryRollCurseForLocalPlayer() {
        try {
            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            var local = players?.FirstOrDefault(p => LocalContext.IsMe(p));
            return TryRollCurse(local);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][cursed-override] local-player resolve failed; no curse: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rolls and fire-and-forget-adds a curse for the given player
    /// (ancient path passes the event Owner). Null player: Warn, no curse.</summary>
    internal static string? TryRollCurse(Player? player) {
        try {
            if (!(BootstrapModSettings.Current?.CursedOverrides ?? false)) return null;
            if (player is null) {
                TiLog.Warn("[SlayTheStreamer2][cursed-override] no player in reach; no curse");
                return null;
            }

            var pool = ModelDb.CardPool<CurseCardPool>().AllCards
                .Where(c => c.CanBeGeneratedByModifiers)
                .ToList();
            if (pool.Count == 0) {
                TiLog.Warn("[SlayTheStreamer2][cursed-override] curse pool is empty; no curse");
                return null;
            }

            var picked = CurseRoll.PickCurse(pool, _rng);
            TaskHelper.RunSafely(CardPileCmd.AddCursesToDeck(new[] { picked }, player));
            TiLog.Info($"[SlayTheStreamer2][cursed-override] {picked.Id.Entry} added to deck for override spend");
            return picked.Title;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][cursed-override] curse roll failed; override proceeds uncursed: {ex.Message}");
            return null;
        }
    }
}
