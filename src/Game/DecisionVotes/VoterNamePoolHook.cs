using System;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: bridges the vote layer to the fairness pool. Attach() once at
/// ModEntry wiring; every session (any vote type) contributes its voters on
/// its terminal event — cancelled sessions included (the person engaged).
/// Terminal events can fire off the main thread (Cancelled fires from the
/// chat-parser thread on disconnect), so harvest locks the pool; readers
/// (TryTakeName) are main-thread and take the same lock via TakeNameLocked.
/// Harvest is unconditional of the settings toggles — pool data is inert,
/// only the label patch reads it (mirrors the CombatOriginTags rule).
/// </summary>
internal static class VoterNamePoolHook {
    private static readonly object Gate = new();
    public static VoterNamePool Pool { get; private set; } = new(new Random());

    public static void Attach(VoteCoordinator coordinator) {
        coordinator.SessionStarted += (_, session) => {
            session.Closed += Harvest;
            session.Cancelled += Harvest;
        };
    }

    private static void Harvest(object? sender, VoteSession session) {
        try {
            lock (Gate) {
                Pool.AddVoters(session.VoterDisplayNames);
            }
            TiLog.Info($"[SlayTheStreamer2][voter-names] pool now {Pool.DistinctVoterCount} distinct voter(s)");
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] voter harvest failed: {ex.Message}");
        }
    }

    /// <summary>Main-thread name draw used by the label patch; shares the harvest lock.</summary>
    public static bool TakeNameLocked(out string decoratedName, out string voterKey) {
        lock (Gate) {
            return Pool.TryTakeName(out decoratedName, out voterKey);
        }
    }

    internal static void ResetForTests() {
        lock (Gate) {
            Pool = new VoterNamePool(new Random(42));
        }
    }
}
