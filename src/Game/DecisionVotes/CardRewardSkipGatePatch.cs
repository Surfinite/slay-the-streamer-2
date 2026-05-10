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
}
