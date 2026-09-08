using System.Linq;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7 gate: the live run's modifier list contains a SealedDeck.
/// Re-evaluated at each use; never cached across runs. Infer from the modifier list,
/// never from the game mode (the notes/08 landmine).</summary>
internal static class SealedDeckRun {
    internal static bool IsActive() {
        try {
            var mods = RunManager.Instance?.DebugOnlyGetState()?.Modifiers;
            return mods is not null && mods.Any(m => m is SealedDeck);
        } catch { return false; }
    }

    /// <summary>Same predicate against an explicit run state, for patches that already
    /// have one on hand (e.g. RelicModel.IsAllowed's runState argument) instead of
    /// going through RunManager.Instance.</summary>
    internal static bool IsActive(IRunState? runState) {
        try {
            var mods = runState?.Modifiers;
            return mods is not null && mods.Any(m => m is SealedDeck);
        } catch { return false; }
    }
}
