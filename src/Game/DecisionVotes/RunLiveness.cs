using MegaCrit.Sts2.Core.Runs;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Shared run-liveness probe used by vote-bearing patches to detect mid-vote
/// run death (abandon, game-over, save-quit-to-main-menu) and trigger prompt
/// session cancellation so on-screen vote UI tears down immediately instead
/// of waiting up to 30s for the vote timer to expire.
///
/// Fires on any of:
///   - <c>RunManager.Instance</c> null
///   - <c>RunManager.Instance.IsAbandoned</c>
///   - <c>RunState</c> fully torn down (<c>DebugOnlyGetState() == null</c>)
///   - <c>RunState.IsGameOver</c>
///
/// Fail-safe: any transient null/throw during normal play returns false so
/// an active vote isn't killed by a flake in the probe itself.
/// </summary>
internal static class RunLiveness {
    internal static bool IsRunDying() {
        try {
            var rm = RunManager.Instance;
            if (rm is null) return true;
            if (rm.IsAbandoned) return true;
            var state = rm.DebugOnlyGetState();
            if (state is null) return true;
            if (state.IsGameOver) return true;
            return false;
        } catch {
            return false;
        }
    }
}
