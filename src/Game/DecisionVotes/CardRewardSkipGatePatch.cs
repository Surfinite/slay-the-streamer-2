using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;       // NRewardButton (spike-corrected namespace)
using MegaCrit.Sts2.Core.Nodes.CommonUi;      // NProceedButton
using MegaCrit.Sts2.Core.Nodes.Screens;       // NRewardsScreen
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

    private static readonly Lazy<FieldInfo?> _rewardButtonsField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));
    private static readonly Lazy<FieldInfo?> _proceedButtonField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_proceedButton"));

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
        return rb.Reward is CardReward;
    }

    /// <summary>
    /// True iff at least one card reward remains unclaimed/unskipped on the screen.
    /// Vanilla removes claimed/skipped buttons from _rewardButtons in RewardCollectedFrom
    /// / RewardSkippedFrom (verified in spike notes) — so any CardReward-wrapping
    /// NRewardButton still in the list is unclaimed.
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
            // TEMP: unconditional entry log to diagnose why postfix appears silent in v0.1 testing.
            // Remove once skip-gate behaviour is operator-validated end-to-end.
            TiLog.Info($"[SlayTheStreamer2][card-skip-gate] SetRewards postfix fired; Settings={(ModEntry.Settings?.GetType().Name ?? "null")}, VotePrepared={CardRewardVotePatch.PreparedSuccessfully}, VoterDefault={(Voter.Default is null ? "null" : "set")}");
            try {
                if (!ShouldEnforceSkipGate()) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] SetRewards postfix bailed: ShouldEnforceSkipGate=false");
                    return;
                }

                var runState = TryGetRunState();
                if (runState is null) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] SetRewards postfix bailed: TryGetRunState returned null");
                    return;
                }

                // MP bail
                try {
                    if (runState.Players?.Count is int n && n > 1) {
                        TiLog.Info($"[SlayTheStreamer2][card-skip-gate] SetRewards postfix bailed: MP detected (Players.Count={n})");
                        return;
                    }
                } catch { /* swallow — proceed without MP bail if accessor failed */ }

                string? runId = runState.Rng?.StringSeed;
                int? actIndex = GetCurrentActIndex(runState);
                _tracker.ObserveRunAndAct(runId, actIndex);

                if (!HasUnclaimedCardReward(__instance)) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] SetRewards postfix bailed: no unclaimed card reward on screen");
                    return;
                }

                var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
                TiLog.Info($"[SlayTheStreamer2][card-skip-gate] SetRewards postfix proceeding: cardSkipsPerAct={settings.CardSkipsPerAct}, used={_tracker.ActSkipsUsed}, allowed={_tracker.IsSkipAllowed(settings.CardSkipsPerAct)}");
                if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct)) {
                    __instance.DisallowSkipping();
                }

                AttachOrUpdateLabel(__instance, settings.CardSkipsPerAct);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] SetRewards postfix failed", ex);
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
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "RewardSkippedFrom")]
    internal static class NRewardsScreen_RewardSkippedFrom_Postfix {
        static bool Prepare() => PrepareHardChecks();

        static void Postfix(NRewardsScreen __instance, Control button) {
            try {
                if (!IsCardRewardButton(button)) return;
                if (!ShouldEnforceSkipGate()) return;   // settings-check BEFORE recording (per spec)

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
}
