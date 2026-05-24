using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.TopBar;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Blocks the top-bar Map button (and its <c>MegaInput.viewMap</c> hotkey, default M)
/// in two scenarios:
///
/// 1. <b>Any mod vote in flight</b> — without this guard the streamer can click Map
///    mid-vote, open <c>NMapScreen</c>, click a map node, and navigate to the next
///    room before the chat countdown finishes. Chat got a misleading "Chat chose X"
///    close receipt before the resume found the originating screen freed, and the
///    run advances to a state chat never picked.
///
/// 2. <b>Rewards screen mounted</b> — even with no vote running, the streamer can
///    open Map while the post-combat rewards screen is up (or while the card-reward
///    sub-screen is open pre-vote) and navigate away, bypassing chat engagement
///    entirely. The parent's <c>OnProceedButtonPressed</c> mandatory-look gate
///    enforces this for the Proceed button, but Map sidesteps that gate. (The
///    sub-screen always sits on top of the parent rewards screen, so the rewards-
///    screen probe transitively covers the "Choose a Card sub-screen open pre-vote"
///    case.)
///
/// Scope: blocks the click *open* path. Closing an already-open map screen is fine.
/// Probes <c>VoteInProgress</c> on each vote patch + <c>IsRewardsScreenActive</c>
/// on the skip-gate patch directly — keeps the dependency direction one-way (this
/// patch knows about the others; they don't know about it).
/// </summary>
[HarmonyPatch(typeof(NTopBarMapButton), "OnRelease")]
internal static class TopBarMapButtonGuardPatch {
    static bool Prepare() => true;

    static bool Prefix() {
        if (BossVotePatch.VoteInProgress) {
            TiLog.Info("[SlayTheStreamer2][map-guard] map button blocked: boss vote in progress");
            return false;
        }
        if (CardRewardVotePatch.VoteInProgress) {
            TiLog.Info("[SlayTheStreamer2][map-guard] map button blocked: card-reward vote in progress");
            return false;
        }
        if (AncientVotePatch.VoteInProgress) {
            TiLog.Info("[SlayTheStreamer2][map-guard] map button blocked: ancient-event vote in progress");
            return false;
        }
        if (ActVariantVotePatch.VoteInProgress) {
            TiLog.Info("[SlayTheStreamer2][map-guard] map button blocked: act-variant vote in progress");
            return false;
        }
        if (CardRewardSkipGatePatch.IsRewardsScreenActive) {
            TiLog.Info("[SlayTheStreamer2][map-guard] map button blocked: rewards screen still open (must engage chat or pick cards before navigating)");
            return false;
        }
        return true;
    }
}
