using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;      // CardCreationResult (for completion source generic arg)
using MegaCrit.Sts2.Core.Nodes.Cards.Holders; // NCardHolder
using MegaCrit.Sts2.Core.Nodes.Rewards;       // NRewardButton (spike-corrected namespace)
using MegaCrit.Sts2.Core.Nodes.CommonUi;      // NProceedButton
using MegaCrit.Sts2.Core.Nodes.Screens;       // NRewardsScreen
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection; // NCardRewardSelectionScreen
using MegaCrit.Sts2.Core.Rewards;             // CardReward, Reward
using MegaCrit.Sts2.Core.Runs;                // RunManager, RunState
using SlayTheStreamer2.Game.Bootstrap;        // SettingsResult, ChatSettings
using SlayTheStreamer2.Game.Ui;               // CardSkipCounterLabel
using SlayTheStreamer2.Ti.Chat;               // ChatConnectionState, OutgoingMessagePriority
using SlayTheStreamer2.Ti.Internal;           // TiLog
using SlayTheStreamer2.Ti.Voting;             // Voter

namespace SlayTheStreamer2.Game.DecisionVotes;

internal static class CardRewardSkipGatePatch {
    private static readonly SkipBudgetTracker _tracker = new();
    private static CardSkipCounterLabel? _activeLabel;

    /// <summary>
    /// Set true when the card sub-screen exits without an explicit user action
    /// (back-out via Escape→Resume). Consumed and reset by the next RewardSkippedFrom
    /// postfix call, which would otherwise spuriously decrement the budget.
    /// Mandatory-look (the 2026-05-11 design pivot — amends Decision 18) preserves
    /// looking-for-free: budget only ticks when the streamer commits to skip, not
    /// when they peek and back out.
    /// </summary>
    private static bool _suppressNextCardSkip;

    /// <summary>
    /// Per-rewards-screen tracking of which NRewardButton instances had their card
    /// sub-screen opened. Populated by the NRewardButton.OnRelease prefix below for
    /// CardReward / SpecialCardReward buttons; consulted by OnProceedButtonPressed
    /// prefix to enforce mandatory-look. Cleared in SetRewards postfix (fresh
    /// rewards screen → fresh tracking) and AfterOverlayClosed postfix (defensive,
    /// in case a screen is torn down without a subsequent SetRewards).
    /// </summary>
    private static readonly HashSet<ulong> _openedCardRewardButtonIds = new();

    private static readonly Lazy<FieldInfo?> _rewardButtonsField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));
    private static readonly Lazy<FieldInfo?> _skippedRewardButtonsField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_skippedRewardButtons"));
    private static readonly Lazy<FieldInfo?> _proceedButtonField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_proceedButton"));
    private static readonly Lazy<FieldInfo?> _subScreenCompletionSourceField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_completionSource"));

    /// <summary>
    /// Skip gate enforces only when card-vote infrastructure is fully available.
    /// Temporary Twitch disconnect mid-run does NOT disable the gate (chat reconnect
    /// + backlog handles that). Permanent missing-infrastructure degrades to vanilla.
    /// </summary>
    private static bool ShouldEnforceSkipGate() {
        if (ModEntry.Settings is not SettingsResult.Success) return false;
        if (!CardRewardVotePatch.PreparedSuccessfully) return false;
        if (Voter.Default == null) return false;
        return true;
    }

    /// <summary>
    /// Hard-check shared by all three postfixes' Prepare methods. Returns false
    /// if any required reflected field is missing (patch silently skips registration).
    /// </summary>
    internal static bool PrepareHardChecks() {
        if (_rewardButtonsField.Value is null) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] hard check failed: NRewardsScreen._rewardButtons field not found");
            return false;
        }
        // _proceedButton is a soft requirement: if missing, label falls back to top-right
        // (handled in CardSkipCounterLabel.AttachTo). Don't fail Prepare just for the button.
        if (_proceedButtonField.Value is null) {
            TiLog.Warn("[SlayTheStreamer2][card-skip-gate] _proceedButton field not found; label will fallback to top-right of parent");
        }
        // _skippedRewardButtons is a soft requirement: without it, CountPendingCardRewards
        // can't filter out already-sub-screen-skipped buttons, so the AfterOverlayClosed
        // budget prefix may over-charge on screens that mixed sub-screen skip + parent skip.
        // Mandatory-look itself still works (it's keyed on opened-set, not skipped list).
        if (_skippedRewardButtonsField.Value is null) {
            TiLog.Warn("[SlayTheStreamer2][card-skip-gate] _skippedRewardButtons field not found; budget charge may double-count on mixed-skip screens");
        }
        return true;
    }

    /// <summary>
    /// Reflect into NRewardsScreen._rewardButtons and return as IReadOnlyList&lt;Control&gt;.
    /// Vanilla stores this as List&lt;Control&gt; (mixes NRewardButton and NLinkedRewardSet,
    /// both Control subclasses). Returns null if reflection fails.
    /// </summary>
    private static IReadOnlyList<Control>? GetRewardButtons(NRewardsScreen screen) {
        var raw = _rewardButtonsField.Value?.GetValue(screen);
        return raw as IReadOnlyList<Control> ?? (raw as IEnumerable<Control>)?.ToList();
    }

    /// <summary>
    /// True iff the button is an NRewardButton wrapping a CardReward.
    /// Uses direct property access (NRewardButton.Reward is a public property — no reflection).
    /// </summary>
    private static bool IsCardRewardButton(Control button) {
        if (!GodotObject.IsInstanceValid(button)) return false;
        if (button is not NRewardButton rb) return false;
        // Two distinct card-reward types in vanilla:
        //  - CardReward: choose-1-of-3 (opens NCardRewardSelectionScreen sub-screen)
        //  - SpecialCardReward: single guaranteed card (no sub-screen, just claim or skip)
        // Both add a card to the deck and both should count against the per-act budget.
        // SpecialCardReward extends Reward directly (NOT CardReward), so we check both.
        return rb.Reward is CardReward or SpecialCardReward;
    }

    /// <summary>
    /// True iff at least one card reward remains unclaimed/unskipped on the screen.
    /// Vanilla removes claimed buttons from _rewardButtons in RewardCollectedFrom, but
    /// sub-screen-skipped buttons stay in _rewardButtons (they're added to
    /// _skippedRewardButtons instead, see NRewardsScreen.RewardSkippedFrom). For the
    /// "should skip be allowed?" check at SetRewards time, "unclaimed" means either
    /// still-pending OR already-skipped — both are CardReward buttons whose ultimate
    /// fate depends on subsequent action, so we don't filter by _skippedRewardButtons here.
    /// </summary>
    private static bool HasUnclaimedCardReward(NRewardsScreen screen) {
        var buttons = GetRewardButtons(screen);
        if (buttons is null) {
            TiLog.Warn("[SlayTheStreamer2][card-skip-gate] could not enumerate _rewardButtons; assuming no card reward");
            return false;
        }
        return buttons.Any(b => GodotObject.IsInstanceValid(b) && IsCardRewardButton(b));
    }

    /// <summary>
    /// Snapshot of pending card-reward buttons on a rewards screen, for the mandatory-look
    /// gate. "Pending" = NRewardButton in _rewardButtons (still alive) that wraps a
    /// CardReward/SpecialCardReward AND is NOT yet in _skippedRewardButtons. These are
    /// the buttons the parent's Proceed-as-Skip click would newly skip via vanilla's
    /// AfterOverlayClosed → Reward.OnSkipped() iteration (sub-screen-skipped buttons
    /// have already been charged through the RewardSkippedFrom postfix).
    /// </summary>
    private readonly record struct PendingCardRewards(int Total, int Unopened);

    private static PendingCardRewards CountPendingCardRewards(NRewardsScreen screen) {
        var buttons = GetRewardButtons(screen);
        if (buttons is null) return new PendingCardRewards(0, 0);

        var skippedRaw = _skippedRewardButtonsField.Value?.GetValue(screen);
        var skipped = skippedRaw as IReadOnlyList<Control>
            ?? (skippedRaw as IEnumerable<Control>)?.ToList()
            ?? (IReadOnlyList<Control>)Array.Empty<Control>();

        int total = 0;
        int unopened = 0;
        foreach (var b in buttons) {
            if (!GodotObject.IsInstanceValid(b)) continue;
            if (b is not NRewardButton rb) continue;
            if (rb.Reward is not (CardReward or SpecialCardReward)) continue;
            if (skipped.Contains(b)) continue;   // already counted via RewardSkippedFrom
            total++;
            if (!_openedCardRewardButtonIds.Contains(rb.GetInstanceId())) unopened++;
        }
        return new PendingCardRewards(total, unopened);
    }

    /// <summary>
    /// Read the current act index from RunState. Uses the public CurrentActIndex
    /// property (spike-pinned; 0-based). Returns null on access failure (degraded
    /// run detection — tracker treats null as "no change").
    /// </summary>
    private static int? GetCurrentActIndex(RunState? runState) {
        if (runState is null) return null;
        try {
            return runState.CurrentActIndex;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] CurrentActIndex access failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Try to read the current RunState. Wraps the DebugOnlyGetState() call in try/catch
    /// so callers can treat any failure as "no run state available" (degraded operation).
    /// </summary>
    private static RunState? TryGetRunState() {
        try {
            return RunManager.Instance?.DebugOnlyGetState();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Ensure a CardSkipCounterLabel is attached to the rewards screen and shows
    /// the current snapshot. Hides (or skips creating) when actLimit < 0 (unlimited).
    /// IsInstanceValid guards against the static _activeLabel pointing at a freed node
    /// (belt-and-suspenders alongside the AfterOverlayClosed null-out).
    /// </summary>
    private static void AttachOrUpdateLabel(NRewardsScreen screen, int actLimit) {
        if (actLimit < 0) {
            if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
                _activeLabel.Visible = false;
            }
            return;
        }

        if (_activeLabel is null || !GodotObject.IsInstanceValid(_activeLabel)) {
            var proceedButton = _proceedButtonField.Value?.GetValue(screen) as Control;
            try {
                _activeLabel = CardSkipCounterLabel.AttachTo(screen, proceedButton);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] label attach failed", ex);
                return;
            }
        }
        _activeLabel.UpdateText(_tracker.Snapshot(actLimit));
    }

    /// <summary>
    /// Format the skip receipt sent to chat. Used/limit semantics; "unlimited act"
    /// rendering for actLimit < 0. Per spec Decision 16, this stays in Game/ — do
    /// NOT add to Ti/Voting/EnglishReceipts (would violate the TI/Game seam).
    /// </summary>
    private static string FormatSkipReceipt(int actUsed, int actLimit) {
        string limitPart = actLimit < 0 ? "unlimited act" : $"{actUsed}/{actLimit} act";
        return $"Streamer skipped a card reward ({limitPart})";
    }

    /// <summary>
    /// Send the skip receipt to chat. Routes through OutgoingMessageQueue via
    /// coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal) —
    /// rate limiting (20/30s + 1/sec spacing) preserved per spec.
    /// </summary>
    private static void SendSkipReceipt(int actLimit) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;

        string text = FormatSkipReceipt(_tracker.ActSkipsUsed, actLimit);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
    }

    // Patches SetRewards (NOT _Ready) — vanilla NRewardsScreen._Ready does NOT populate
    // _rewardButtons; it just wires UI nodes and signal handlers. _rewardButtons is filled
    // later by an explicit SetRewards(IEnumerable<Reward>) call from RunManager / NCombatRoom.
    // Operator-validation Step 1 caught this: with a _Ready postfix, _rewardButtons was empty
    // → HasUnclaimedCardReward returned false → silent early-return → label never attached
    // and DisallowSkipping never called. SetRewards is the right hook because by the end of
    // it (line 294 of decompile, just before UpdateScreenState), _rewardButtons is fully wired.
    [HarmonyPatch(typeof(NRewardsScreen), "SetRewards")]
    internal static class NRewardsScreen_SetRewards_Postfix {
        static bool Prepare() => PrepareHardChecks();

        static void Postfix(NRewardsScreen __instance) {
            try {
                // Fresh rewards screen → fresh mandatory-look tracking. Old buttons were
                // QueueFreed by RemoveButton in vanilla SetRewards; their instance IDs
                // can never recur on the new buttons, but clearing keeps the set bounded
                // and removes the "stale-but-harmless" mental footnote.
                _openedCardRewardButtonIds.Clear();

                if (!ShouldEnforceSkipGate()) return;

                var runState = TryGetRunState();
                if (runState is null) return;

                // MP bail
                try {
                    if (runState.Players?.Count is int n && n > 1) return;
                } catch { /* swallow — proceed without MP bail if accessor failed */ }

                string? runId = runState.Rng?.StringSeed;
                int? actIndex = GetCurrentActIndex(runState);
                _tracker.ObserveRunAndAct(runId, actIndex);

                if (!HasUnclaimedCardReward(__instance)) return;

                var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
                if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct)) {
                    __instance.DisallowSkipping();
                }

                AttachOrUpdateLabel(__instance, settings.CardSkipsPerAct);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] SetRewards postfix failed", ex);
            }
        }
    }

    /// <summary>
    /// Charges per-pending-card budget when the rewards screen closes. This is the only
    /// place the parent's Proceed-as-Skip path can be observed: vanilla's OnProceedButtonPressed
    /// removes the screen via NOverlayStack.Remove (non-terminal) or kicks off
    /// ProceedFromTerminalRewardsScreen (terminal), both of which lead here. AfterOverlayClosed
    /// then iterates remaining children and calls Reward.OnSkipped() per button — no
    /// RewardSkipped signal is fired on that path, so RewardSkippedFrom_Postfix never sees
    /// it. Without this prefix, parent-Skip would skip cards for FREE budget-wise.
    ///
    /// Pending count filters out sub-screen-skipped buttons (already in _skippedRewardButtons,
    /// already charged through RewardSkippedFrom). Sub-screen-claimed buttons are absent
    /// from _rewardButtons entirely (RemoveButton in RewardCollectedFrom). So this prefix
    /// only charges for buttons whose skip side-effect is about to fire NOW.
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "AfterOverlayClosed")]
    internal static class NRewardsScreen_AfterOverlayClosed_BudgetPrefix {
        static bool Prepare() => PrepareHardChecks();

        static void Prefix(NRewardsScreen __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return;
                if (CardRewardVotePatch.VoteInProgress) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed: vote in progress; skipping budget charge");
                    return;
                }

                var pending = CountPendingCardRewards(__instance);
                if (pending.Total == 0) return;

                var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
                for (int i = 0; i < pending.Total; i++) {
                    _tracker.RecordSkip();
                    SendSkipReceipt(settings.CardSkipsPerAct);
                }
                TiLog.Info($"[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed: charged {pending.Total} card-skip(s) from parent-Skip path");
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed budget prefix failed", ex);
            }
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "AfterOverlayClosed")]
    internal static class NRewardsScreen_AfterOverlayClosed_Postfix {
        static bool Prepare() => true;   // No reflected fields needed.
        static void Postfix() {
            // Label is now parented under SceneTree.Root (commit 17cb1d7) so it doesn't
            // auto-free with the rewards screen. Free it explicitly here, then null the
            // static so the next SetRewards postfix builds a fresh one.
            if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
                _activeLabel.QueueFree();
            }
            _activeLabel = null;

            // Defensive — SetRewards postfix already clears, but if a screen tears down
            // without a subsequent SetRewards (e.g., abandon-run mid-screen), this keeps
            // the set bounded.
            _openedCardRewardButtonIds.Clear();
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "RewardSkippedFrom")]
    internal static class NRewardsScreen_RewardSkippedFrom_Postfix {
        static bool Prepare() => PrepareHardChecks();

        static void Postfix(NRewardsScreen __instance, Control button) {
            try {
                if (!IsCardRewardButton(button)) return;
                if (!ShouldEnforceSkipGate()) return;   // settings-check BEFORE recording (per spec)

                // Back-out suppression: if the player just opened the card sub-screen and
                // backed out via Escape→Resume (no card click, no alternate click), vanilla
                // emits RewardSkipped via NRewardButton.GetReward → OnSelectWrapper false path.
                // Decision 18 (Mode B) says looking-without-committing must NOT cost a skip.
                // The _ExitTree prefix on NCardRewardSelectionScreen detects this case and
                // sets _suppressNextCardSkip; consume it here and skip the decrement.
                if (_suppressNextCardSkip) {
                    _suppressNextCardSkip = false;
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom suppressed: sub-screen back-out without explicit action (Mode B preserves looking-for-free)");
                    return;
                }

                // Mid-vote skip guard: if a card vote is currently in progress, the streamer
                // is using the sub-screens Skip path to abort the vote (vanilla still fires
                // RewardSkippedFrom on the card button as part of the dismiss flow). Don't
                // decrement the budget for this — the vote will drop silently when the
                // resume hits IsInstanceValid.
                if (CardRewardVotePatch.VoteInProgress) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom fired while vote in progress; not decrementing budget");
                    return;
                }

                _tracker.RecordSkip();

                var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
                SendSkipReceipt(settings.CardSkipsPerAct);

                // Multi-card-reward gate re-evaluation: if THIS skip exhausted budget AND
                // another unclaimed card reward remains on this screen, call DisallowSkipping
                // again so vanilla disables Proceed for the remaining card(s).
                if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct) && HasUnclaimedCardReward(__instance)) {
                    __instance.DisallowSkipping();
                }

                if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
                    _activeLabel.UpdateText(_tracker.Snapshot(settings.CardSkipsPerAct));
                }
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom postfix failed", ex);
            }
        }
    }

    /// <summary>
    /// Sub-screen back-out detection. Mandatory-look (the 2026-05-11 design pivot —
    /// amends Decision 18) preserves looking-for-free: opening a card sub-screen and
    /// backing out via Escape→Resume must NOT cost a skip. But vanilla emits
    /// NRewardButton.RewardSkipped on the back-out path (via OnSelectWrapper returning
    /// false), which would otherwise hit our RewardSkippedFrom postfix and decrement.
    ///
    /// The distinguishing signal is `_completionSource.Task.IsCompleted` BEFORE vanilla
    /// _ExitTree runs:
    ///   - true  → explicit action (SelectCard or OnAlternateRewardSelected) already SetResult
    ///             → legitimate skip / claim flow → don't suppress
    ///   - false → vanilla is about to SetResult(empty, false) → it's a back-out → suppress
    ///             the next RewardSkippedFrom decrement
    /// Implemented as a Prefix so we read the state before vanilla mutates it.
    /// </summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_ExitTree")]
    internal static class NCardRewardSelectionScreen_ExitTree_Prefix {
        static bool Prepare() => _subScreenCompletionSourceField.Value is not null;

        static void Prefix(NCardRewardSelectionScreen __instance) {
            try {
                var cs = _subScreenCompletionSourceField.Value?.GetValue(__instance)
                    as TaskCompletionSource<Tuple<IEnumerable<NCardHolder>, bool>>;
                if (cs is null) return;
                if (!cs.Task.IsCompleted) {
                    _suppressNextCardSkip = true;
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] sub-screen back-out detected (completion source pending); next RewardSkippedFrom will be suppressed");
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] _ExitTree prefix could not read completion source: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Mandatory-look tracker: fires when the streamer releases a card-reward NRewardButton.
    /// Vanilla's OnRelease (NRewardButton.cs:214) is the sync click handler that kicks off
    /// `GetReward()` — which awaits `Reward.OnSelectWrapper()` and opens the
    /// NCardRewardSelectionScreen sub-screen for CardReward/SpecialCardReward.
    ///
    /// This is the cleanest "the streamer is about to look at this card reward" signal —
    /// sync, fires before the async sub-screen open, and is keyed to the specific button
    /// instance. We just record the instance ID so the OnProceedButtonPressed gate below
    /// can check it later. Non-card rewards (gold, potion, relic) skip the add (they claim
    /// directly without a sub-screen — mandatory-look doesn't apply).
    /// </summary>
    [HarmonyPatch(typeof(NRewardButton), "OnRelease")]
    internal static class NRewardButton_OnRelease_Prefix {
        static bool Prepare() => true;

        static void Prefix(NRewardButton __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return;
                if (!GodotObject.IsInstanceValid(__instance)) return;
                if (__instance.Reward is not (CardReward or SpecialCardReward)) return;
                _openedCardRewardButtonIds.Add(__instance.GetInstanceId());
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] OnRelease prefix could not record opened button: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The mandatory-look gate. Blocks the parent's Proceed/Skip click when any
    /// pending (alive + not-yet-sub-screen-skipped) card-reward button hasn't had its
    /// sub-screen opened. Also enforces per-card budget — if pending count would exceed
    /// remaining budget, blocks AND calls DisallowSkipping as belt-and-suspenders.
    ///
    /// pending == 0 means either (a) no card rewards on screen or (b) all card rewards
    /// already claimed or sub-screen-skipped → no further skip side effects happen via
    /// this click → vanilla can run unmodified.
    ///
    /// Known v0.1 quirk: terminal screens with un-seen "combat_reward_ftue" route through
    /// RewardFtueCheck instead of actually proceeding. The streamer sees a tutorial, then
    /// has to click Proceed again. Our prefix runs both times; the second run would re-check
    /// pending (potentially still > 0 if no skip happened first time). Acceptable — FTUE
    /// only fires once per save profile, and the budget hasn't been charged yet either (we
    /// charge in AfterOverlayClosed prefix, not here).
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    internal static class NRewardsScreen_OnProceedButtonPressed_Prefix {
        static bool Prepare() => PrepareHardChecks();

        static bool Prefix(NRewardsScreen __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return true;
                if (CardRewardVotePatch.VoteInProgress) return true;   // vote will handle its own resume

                var pending = CountPendingCardRewards(__instance);
                if (pending.Total == 0) return true;   // nothing to skip — let vanilla through

                if (pending.Unopened > 0) {
                    TiLog.Info($"[SlayTheStreamer2][card-skip-gate] parent Skip blocked: mandatory-look unsatisfied for {pending.Unopened}/{pending.Total} pending card reward(s)");
                    return false;
                }

                var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
                int limit = settings.CardSkipsPerAct;
                if (limit >= 0 && _tracker.ActSkipsUsed + pending.Total > limit) {
                    int remaining = Math.Max(0, limit - _tracker.ActSkipsUsed);
                    TiLog.Info($"[SlayTheStreamer2][card-skip-gate] parent Skip blocked: would cost {pending.Total} skip(s); only {remaining}/{limit} budget remaining");
                    __instance.DisallowSkipping();
                    return false;
                }

                return true;
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] OnProceedButtonPressed prefix failed; falling back to vanilla", ex);
                return true;
            }
        }
    }
}
