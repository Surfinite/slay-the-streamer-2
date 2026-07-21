using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class AncientVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;
    private static VoteSession? _activeSession;

    /// <summary>True while an ancient-event vote is in flight. Read by the global
    /// map-button guard (<c>TopBarMapButtonGuardPatch</c>) so the streamer can't
    /// bypass an active vote by clicking Map / pressing M.</summary>
    internal static bool VoteInProgress => _voteInProgress == 1;
    private static readonly Lazy<FieldInfo?> _eventField =
        new(() => AccessTools.Field(typeof(NEventRoom), "_event"));

    internal static bool RunIdGuardEnabled { get; private set; } = true;

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_eventField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][ancient-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }

            // Soft check: run-id accessor. Failure logs Warn but does NOT abort patch registration.
            try {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm == null) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] run-ID guard degraded:RunManager.Instance not reachable");
                    RunIdGuardEnabled = false;
                } else {
                    // Verify the type shape: RunManager exposes DebugOnlyGetState; deeper Rng.StringSeed
                    // shape is verified at runtime (state may be null at Prepare — no run in progress).
                    var rmType = rm.GetType();
                    var stateMethod = rmType.GetMethod("DebugOnlyGetState");
                    if (stateMethod == null) {
                        TiLog.Warn("[SlayTheStreamer2][ancient-vote] run-ID guard degraded:DebugOnlyGetState() not found");
                        RunIdGuardEnabled = false;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] run-ID guard degraded:Prepare soft check threw: {ex.Message}");
                RunIdGuardEnabled = false;
            }

            return true;
        }

        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EventOption) ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[SlayTheStreamer2][ancient-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][ancient-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        if (_resumeInProgress == 1) return true;
        if (!IsAncientEvent(__instance)) return true;
        if (option.IsLocked || option.IsProceed) {
            // During an open vote such clicks must be suppressed, not passed to
            // vanilla — options stay enabled when an override is available.
            return _voteInProgress != 1;
        }

        // Vote in progress: nothing may fall through to vanilla — the options
        // are left clickable when an override is available, so this must run
        // BEFORE the multiplayer/chat bail-to-vanilla gates (a mid-vote click
        // during a chat disconnect would otherwise reach vanilla and advance
        // the event under a pending vote).
        if (_voteInProgress == 1) {
            if (TryOverride(option, index)) return false;
            TiLog.Debug("[SlayTheStreamer2][ancient-vote] click during open vote; suppressed");
            return false;
        }

        if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] multiplayer detected (Players.Count > 1); bailing to vanilla (further bail-outs at Debug)");
            } else {
                TiLog.Debug("[SlayTheStreamer2][ancient-vote] multiplayer bail-out");
            }
            return true;
        }

        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        // Vote-start gate: any chat state where we can READ messages is sufficient.
        // ConnectedReadOnly covers v0.2 YT-only-mode (Twitch terminal, YT alive): YT
        // messages still flow into VoteSession; receipts won't fire but the vote runs.
        // Receipt-send sites independently check ConnectedReadWrite per D3.
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                        or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][ancient-vote] chat not readable (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        string? runIdAtStart = null;
        if (RunIdGuardEnabled) {
            try {
                var state = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
                runIdAtStart = state?.Rng?.StringSeed;
                if (runIdAtStart == null) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] run-ID guard degraded for this vote:null state or null seed at start");
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] run-ID guard degraded for this vote:{ex.Message}");
            }
        }

        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            if (TryOverride(option, index)) return false;
            TiLog.Debug("[SlayTheStreamer2][ancient-vote] repeat click during open vote; suppressed");
            return false;
        }

        var liveOptions = GetCurrentOptions(__instance);
        if (liveOptions is null || liveOptions.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        var optionsSnapshot = liveOptions.ToList();
        var labels = optionsSnapshot.Select(o => o.Title.GetFormattedText()).ToList();

        // Single-option ancient offering is degenerate (chat "votes" on the only option).
        // Skip the ceremony, let vanilla apply the streamer's click directly. Mirrors
        // CardRewardVotePatch's same-named check; Surfinite's request 2026-05-12 after
        // Discord feedback flagged it as a real edge case.
        if (labels.Count <= 1) {
            TiLog.Info($"[SlayTheStreamer2][ancient-vote] single-option offering; skipping vote (option: {labels[0]})");
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        VoteSession session;
        try {
            var settings = SlayTheStreamer2.Game.Bootstrap.ModSettings.Current;
            var voteDuration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
            var showTag = settings?.ShowVoteTag ?? false;
            session = coordinator.Start(GetVoteTitle(__instance), labels, voteDuration, showTag);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][ancient-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        _activeSession = session;

        // Vote-override budget: observe (reset receipt on act/run change), then
        // decide whether the option buttons stay clickable. With an override
        // available, the streamer must be able to click an option mid-vote —
        // the prefix suppresses everything vanilla-bound anyway. The decision
        // is stable for this vote: only one vote runs at a time, so the budget
        // cannot be consumed elsewhere mid-vote.
        bool overrideAvailable = false;
        try {
            var rsForObserve = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            int? actIdx = rsForObserve?.CurrentActIndex;
            var overrideReason = VoteOverrideBudget.Observe(rsForObserve?.Rng?.StringSeed, actIdx);
            VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIdx.HasValue ? actIdx.Value + 1 : 0);
            overrideAvailable = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
        } catch { /* observe is best-effort; fall through to vanilla disable */ }

        if (overrideAvailable) {
            TiLog.Info("[SlayTheStreamer2][ancient-vote] override available; leaving event options clickable");
        } else {
            try {
                __instance.Layout?.DisableEventOptions();
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] DisableEventOptions threw (continuing): {ex.Message}");
            }
        }

        TiLog.Info($"[SlayTheStreamer2][ancient-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index, runIdAtStart);
        return false;
    }

    private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                              VoteSession session, IReadOnlyList<EventOption> snapshot,
                                              int playerClickIndex, string? runIdAtStart) {
        try {
            coordinator.Dispatcher.Post(() => {
                VoteTallyLabel.AttachTo(session, RunLiveness.IsRunDying, ModSettings.Current?.VoteTallyOnLeft ?? false, OverlayOcclusion.IsOccludingOverlayVisible);
                try {
                    new AncientVotePopup(session, coordinator.Dispatcher, room, RunLiveness.IsRunDying, OverlayOcclusion.IsOccludingOverlayVisible).Show();
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][ancient-vote] popup attach failed; corner tally still active: {ex.Message}");
                }
            });

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][ancient-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] winnerIndex {winnerIndex} out of snapshot range; using player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[SlayTheStreamer2][ancient-vote] resume: applying winner #{winnerIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex, runIdAtStart));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][ancient-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex, runIdAtStart));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][ancient-vote] fallback resume Post itself threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                _activeSession = null;
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                           int preferredIndex, int playerClickIndex, string? runIdAtStart) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume: room no longer valid; dropping resume");
                return;
            }
            if (!IsAncientEvent(room)) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume: active event is no longer an ancient; dropping resume");
                return;
            }
            // Run-state liveness checks. Catches:
            //  - Run abandoned via menu (RunManager.IsAbandoned set true in AbandonInternal — RunState
            //    survives in memory during teardown, so seed-only check below misses this case)
            //  - Player died (RunState.IsGameOver true once GuaranteeKillAllPlayers ran)
            //  - State fully torn down (DebugOnlyGetState null)
            // Seed-only guard remains as a final check for "started a NEW run with a different seed
            // before our resume Posted" — rare but possible.
            try {
                var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
                if (rm is null) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume aborted: RunManager.Instance is null");
                    return;
                }
                if (rm.IsAbandoned) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume aborted: run was abandoned during vote");
                    return;
                }
                var currentState = rm.DebugOnlyGetState();
                if (currentState is null) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume aborted: run state is gone");
                    return;
                }
                if (currentState.IsGameOver) {
                    TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume aborted: run is over (player dead)");
                    return;
                }
                if (runIdAtStart != null) {
                    string? currentRunId = currentState.Rng?.StringSeed;
                    if (currentRunId != runIdAtStart) {
                        TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume aborted: run changed during vote");
                        return;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] resume aborted: liveness check threw ({ex.Message})");
                return;
            }
            var currentOptions = GetCurrentOptions(room)?.ToList();
            if (currentOptions is null || currentOptions.Count == 0) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume: no current options; dropping");
                return;
            }

            int applyIndex = preferredIndex;
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] resume: preferred index {applyIndex} out of range; falling back to player click");
                applyIndex = playerClickIndex;
            }
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] resume: neither preferred nor player index valid (options now {currentOptions.Count}); dropping");
                return;
            }

            var winnerOption = currentOptions[applyIndex];
            room.OptionButtonClicked(winnerOption, applyIndex);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][ancient-vote] resume threw", ex);
        } finally {
            Interlocked.Exchange(ref _resumeInProgress, 0);
            _activeSession = null;
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static bool IsAncientEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is AncientEventModel and not DeprecatedAncientEvent;
    }

    /// <summary>
    /// Streamer override: an armed click on an ancient option during an open
    /// vote ends the vote with that option as the winner and consumes one
    /// override (spec 2026-07-21 §2.4). Ancient options map 1:1 to vote
    /// indices — no skip concept, no holder mapping. Budget consumed strictly
    /// AFTER TryCloseNow succeeds.
    /// </summary>
    private static bool TryOverride(EventOption option, int index) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;
            if (index < 0 || index >= session.Options.Count) return false;

            if (!session.TryCloseNow(index)) return false;

            VoteOverrideBudget.RecordUse();
            string label;
            try { label = option.Title.GetFormattedText(); } catch { label = $"#{index}"; }
            VoteOverrideBudget.SendOverrideReceipt(label);
            TiLog.Info($"[SlayTheStreamer2][ancient-vote] override: streamer forced #{index} ({label}); {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][ancient-vote] override attempt failed; click suppressed", ex);
            return false;
        }
    }

    private static string GetVoteTitle(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        var name = eventModel?.Title.GetFormattedText() ?? "Ancient";
        return $"{name}'s Offering";
    }

    private static IReadOnlyList<EventOption>? GetCurrentOptions(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.CurrentOptions;
    }

    private static int? TryGetEventOwnerPlayerCount(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.Owner?.RunState?.Players?.Count;
    }
}
