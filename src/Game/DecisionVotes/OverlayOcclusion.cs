using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Shared occlusion probe used by vote-bearing UI (popups + corner
/// VoteTallyLabel) so they can hide while a vanilla overlay we don't want to
/// occlude is visible. Currently covers:
///   - Dev console (<c>NDevConsole.Instance.Visible</c>).
///   - Pause menu / settings / abandon-confirm modal / any other vanilla
///     submenu — detected via
///     <c>NRun.Instance.GlobalUi.SubmenuStack.Stack.SubmenusOpen</c>.
///     <c>GlobalUi.SubmenuStack</c> is an <c>NCapstoneSubmenuStack</c> that
///     wraps the actual <c>NRunSubmenuStack</c> at <c>.Stack</c>;
///     <c>NRunSubmenuStack</c> inherits <c>NSubmenuStack</c> which exposes
///     the count-based <c>SubmenusOpen</c> bool. Pause menu opens via
///     <c>NRun.Instance.GlobalUi.SubmenuStack.ShowScreen</c> per
///     <c>NTopBarPauseButton</c> — same code path through this stack.
///
/// Note: <c>SceneTree.Paused</c> is NOT a viable probe — StS2 uses
/// <c>RunManager.ActionExecutor.Pause()</c> for combat pausing, not Godot's
/// <c>SceneTree.Paused</c>, so the latter never goes true via the pause menu.
///
/// Defensive: any transient null/throw returns false so a flake in the
/// probe itself doesn't accidentally hide active vote UI.
/// </summary>
internal static class OverlayOcclusion {
    internal static bool IsOccludingOverlayVisible() {
        try {
            if (NDevConsole.Instance?.Visible ?? false) return true;
            if (NRun.Instance?.GlobalUi?.SubmenuStack?.Stack?.SubmenusOpen ?? false) return true;
            return false;
        } catch {
            return false;
        }
    }
}
