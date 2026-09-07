// src/Game/DecisionVotes/RemovalVoteFlow.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Runs one removal vote against an IRemovalSurface (spec section 3). The
/// caller owns its own vote-in-progress flag and the click gating; this class owns the
/// session, the popup, the resume and the record. Suspend-and-resume shape: the await
/// runs off the main thread and every screen touch is dispatcher-posted.</summary>
internal static class RemovalVoteFlow {
    private static VoteSession? _session;
    private static IRemovalSurface? _surface;
    private static int _overridePending;

    internal static bool IsActive => _session is { State: VoteSessionState.Open };
    internal static IRemovalSurface? ActiveSurface => _surface;
    internal static VoteSession? ActiveSession => _session;

    /// <summary>Starts the vote. Returns false when chat is not readable, the coordinator
    /// is missing, or fewer than two options are removable; the caller then treats the
    /// reward as Free for this screen.</summary>
    internal static bool TryStart(IRemovalSurface surface, Action onFinished) {
        var coordinator = Voter.Default;
        if (coordinator is null) return false;
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][{surface.LogTag}] chat not readable ({coordinator.Chat.State}); removal vote skipped");
            return false;
        }
        var titles = surface.CardTitles();
        int removable = titles.Count + (surface.HasSkip ? 1 : 0);
        if (removable < 2) {
            TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] fewer than 2 removable options; removal vote skipped");
            return false;
        }
        var settings = ModSettings.Current;
        var duration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
        bool showTag = settings?.ShowVoteTag ?? false;
        var labels = CardRewardOptionLabels.Build(titles, surface.HasSkip);
        string streamer = ModSettings.GetStreamerDisplayName();

        VoteSession session;
        try {
            session = coordinator.Start("Remove an option", labels, duration, showTag,
                formatReceipt: (snap, kind) => RemoveVoteReceipts.Format(snap, kind, streamer));
        } catch (Exception ex) {
            TiLog.Error($"[SlayTheStreamer2][{surface.LogTag}] Voter.Default.Start threw; removal vote skipped", ex);
            return false;
        }
        _session = session;
        _surface = surface;
        Interlocked.Exchange(ref _overridePending, 0);
        var snapshot = surface.SnapshotOptions();
        TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] removal vote opened: {labels.Count} options (skip={surface.HasSkip})");
        _ = RunAsync(coordinator, surface, session, snapshot, onFinished);
        return true;
    }

    private static async Task RunAsync(VoteCoordinator coordinator, IRemovalSurface surface, VoteSession session, object? snapshot, Action onFinished) {
        try {
            coordinator.Dispatcher.Post(() => {
                VoteTallyLabel.AttachTo(session, RunLiveness.IsRunDying, ModSettings.Current?.VoteTallyOnLeft ?? false, OverlayOcclusion.IsOccludingOverlayVisible);
                try {
                    new CardRewardVotePopup(session, coordinator.Dispatcher, surface.ScreenNode,
                        surface.CardHolders(), surface.SkipControl(), surface.BannerAnchor(),
                        includeSkip: surface.HasSkip, removeMode: true,
                        RunLiveness.IsRunDying, OverlayOcclusion.IsOccludingOverlayVisible).Show();
                } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] popup attach failed: {ex.Message}"); }
            });

            int winner;
            try { winner = await session.AwaitWinnerAsync(); }
            catch (OperationCanceledException) {
                bool overridden = Interlocked.Exchange(ref _overridePending, 0) == 1;
                TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] removal vote cancelled ({(overridden ? "streamer override" : "run/chat")}); no removal recorded");
                coordinator.Dispatcher.Post(() => Finish(onFinished));
                return;
            }
            coordinator.Dispatcher.Post(() => {
                try { Apply(surface, snapshot, winner); }
                finally { Finish(onFinished); }
            });
        } catch (Exception ex) {
            TiLog.Error($"[SlayTheStreamer2][{surface.LogTag}] removal vote flow threw; screen released without a removal", ex);
            coordinator.Dispatcher.Post(() => Finish(onFinished));
        }
    }

    private static void Finish(Action onFinished) {
        _session = null;
        _surface = null;
        try { onFinished(); } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] onFinished threw", ex); }
    }

    private static void Apply(IRemovalSurface surface, object? snapshot, int winner) {
        if (!GodotObject.IsInstanceValid(surface.ScreenNode)) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] screen gone before the removal applied; nothing recorded");
            return;
        }
        if (RunLiveness.IsRunDying()) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] run dying before the removal applied; nothing recorded");
            return;
        }
        if (!surface.OptionsMatch(snapshot)) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] options changed during the vote (reroll?); nothing recorded");
            return;
        }
        var titles = surface.CardTitles();
        int? cardIndex = CardRewardOptionLabels.ResolveCardIndex(winner, surface.HasSkip);
        RemovalRecord record = cardIndex is null
            ? new RemovalRecord(RemovalClickRules.SkipIndex, CardRewardOptionLabels.SkipLabel, snapshot)
            : new RemovalRecord(cardIndex.Value, cardIndex.Value < titles.Count ? titles[cardIndex.Value] : "an option", snapshot);
        RemovalRecords.Set(surface.RecordKey, record);
        ApplyRecordVisuals(surface, record);
        TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] chat removed {(record.IsSkip ? "Skip" : $"#{record.RemovedIndex} {record.RemovedLabel}")}");
    }

    /// <summary>Paint the removed option (idempotent: the popup already tweened it once)
    /// and make it unclickable when no override budget remains.</summary>
    internal static void ApplyRecordVisuals(IRemovalSurface surface, RemovalRecord record) {
        var control = record.IsSkip ? surface.SkipControl() : IndexOrNull(surface.CardHolders(), record.RemovedIndex);
        if (control is null) return;
        RemovalVisuals.PaintRemoved(control, surface.ScreenNode);
        bool canOverride = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
        RemovalVisuals.SetClickable(control, canOverride);
    }

    private static Control? IndexOrNull(IReadOnlyList<Control> list, int i) => i >= 0 && i < list.Count ? list[i] : null;

    /// <summary>The record for this surface if its options still match; a stale record
    /// (Driftwood reroll) is cleared so the next click starts a fresh vote.</summary>
    internal static RemovalRecord? EffectiveRecord(IRemovalSurface surface) {
        var record = RemovalRecords.Get(surface.RecordKey);
        if (record is null) return null;
        if (surface.OptionsMatch(record.OptionsSnapshot)) return record;
        RemovalRecords.Clear(surface.RecordKey);
        return null;
    }

    /// <summary>A streamer click during the countdown: end the vote with NO removal and
    /// spend one override; the caller lets the click reach vanilla. False when there is
    /// no open vote, no budget, or the click landed inside the arming delay.</summary>
    internal static bool TryOverrideDuringVote(string takenLabel) {
        try {
            var session = _session;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;
            Interlocked.Exchange(ref _overridePending, 1);
            session.Cancel();
            if (session.State != VoteSessionState.Cancelled) { Interlocked.Exchange(ref _overridePending, 0); return false; }
            VoteOverrideBudget.RecordUse();
            string? curse = CursedOverrides.TryRollCurseForLocalPlayer();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel, curse);
            TiLog.Info($"[SlayTheStreamer2][remove-one] override during removal vote: streamer took {takenLabel}; {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] override during removal vote failed", ex); return false; }
    }

    /// <summary>Spec section 3.2 status lines (rig-tested wording from Sabotage strike/18.7).</summary>
    internal static string StatusText(IRemovalSurface surface, bool hasReroll) {
        string text;
        if (IsActive && ReferenceEquals(_surface, surface)) text = "";                         // the popup speaks
        else if (EffectiveRecord(surface) is { } r) {
            bool budget = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
            if (r.IsSkip) text = budget ? "Chat removed Skip. Take a card, or spend an override to skip." : "Chat removed Skip. You must take a card.";
            else text = budget ? $"Chat removed {r.RemovedLabel}. Choose from the rest, or spend an override to take it." : $"Chat removed {r.RemovedLabel}. Choose from the rest.";
            if (hasReroll) text += "\nYou can reroll these cards once, for free.";
        } else text = "Click any option to start the removal vote.";
        return text;
    }
}
