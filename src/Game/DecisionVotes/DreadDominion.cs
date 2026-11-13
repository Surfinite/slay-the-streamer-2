using System;
using System.Reflection;
using System.Threading;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Keeps a Dread run's Dominion in step with a chat boss swap (game v0.113.0+).
///
/// In Dread every act has a Dominion: a modifier on all of the act's hallway and elite
/// fights, taken from the act's boss (<c>SecondBossEncounter ?? BossEncounter</c>, so
/// from the second boss in the final act). Vanilla derives it in
/// DreadRunModel.ApplyCurrentDominion when the act is entered and AGAIN every time a
/// save is loaded; it is not stored in the save. The boss vote swaps the boss at the
/// chest, mid-act. Left alone, the fights after the chest keep the old boss's Dominion
/// while the top-bar tooltip (computed live from the current boss) shows the new one,
/// and the Dominion in force changes if the streamer saves, quits and continues.
///
/// So after every swap the Dominion is re-derived from the act's current boss, by the
/// same rule vanilla applies on load. That is the only choice that survives a reload.
///
/// Everything Dread-specific is reached by name: <c>EncounterModel.GetDominion()</c>
/// and <c>RunState.SetDominion(...)</c> do not exist before v0.113.0, and the mode is
/// compared by name (<see cref="GameModeNames"/>). On older game versions
/// <see cref="IsDreadRun"/> is simply never true.
/// </summary>
internal static class DreadDominion {
    private static readonly Lazy<MethodInfo?> GetDominionMethod = new(() =>
        MemberCompat.FirstMethod(typeof(EncounterModel), Type.EmptyTypes, "GetDominion"));
    private static int _unavailableWarned;

    internal static bool IsDreadRun(IRunState runState) => GameModeNames.IsDread(runState.GameMode);

    /// <summary>False when this is a Dread run and the members the re-sync needs cannot
    /// be found (a future game update). The boss vote then stays out of Dread rather
    /// than leave boss and Dominion disagreeing.</summary>
    internal static bool CanResync(IRunState runState) =>
        GetDominionMethod.Value is not null && SetDominionMethod(runState) is not null;

    /// <summary>Re-derives the Dominion from the act's current boss. No-op outside
    /// Dread. Called from the boss-swap delegates, so it also covers the silent
    /// re-apply after a save-quit rolled a swap back.</summary>
    internal static void Resync(IRunState runState, string trigger) {
        try {
            if (!IsDreadRun(runState)) return;
            var getDominion = GetDominionMethod.Value;
            var setDominion = SetDominionMethod(runState);
            if (getDominion is null || setDominion is null) {
                if (Interlocked.Exchange(ref _unavailableWarned, 1) == 0) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] Dread: EncounterModel.GetDominion / RunState.SetDominion not found (game update?); the Dominion will not follow chat's boss");
                }
                return;
            }
            var act = runState.Act;
            EncounterModel source = act.SecondBossEncounter ?? act.BossEncounter;
            // Null for a boss that defines no Dominion (a boss added by another mod);
            // vanilla sets "no Dominion" in that case too.
            object? dominion = getDominion.Invoke(source, null);
            setDominion.Invoke(runState, new[] { dominion });
            TiLog.Info($"[SlayTheStreamer2][boss-vote] Dread: Dominion re-derived from {source.Id.Entry} after {trigger} ({dominion?.GetType().Name ?? "none"})");
        } catch (Exception ex) {
            TiLog.Error($"[SlayTheStreamer2][boss-vote] Dread: Dominion re-sync after {trigger} threw; the act keeps its previous Dominion", ex);
        }
    }

    /// <summary>RunState.SetDominion(DominionModel?) on the concrete run state; it is not
    /// on IRunState. Matched by name and arity because its parameter type does not exist
    /// on older game versions.</summary>
    private static MethodInfo? SetDominionMethod(IRunState runState) {
        foreach (var m in runState.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (m.Name == "SetDominion" && m.GetParameters().Length == 1) return m;
        }
        return null;
    }
}
