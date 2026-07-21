using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Rewards;                  // NRewardButton (spike-corrected namespace)
using MegaCrit.Sts2.Core.Nodes.CommonUi;                 // NProceedButton
using MegaCrit.Sts2.Core.Nodes.Screens;                  // NRewardsScreen
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;    // NCardRewardSelectionScreen (for TryConsumeStreamerSkip signature)
using MegaCrit.Sts2.Core.Rewards;                        // CardReward, Reward
using MegaCrit.Sts2.Core.Runs;                // RunManager, RunState
using SlayTheStreamer2.Game.Bootstrap;        // SettingsResult, ChatSettings
using SlayTheStreamer2.Game.Ui;               // CardSkipCounterLabel
using SlayTheStreamer2.Ti.Chat;               // ChatConnectionState, OutgoingMessagePriority
using SlayTheStreamer2.Ti.Internal;           // TiLog
using SlayTheStreamer2.Ti.Voting;             // Voter
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Skip-gate under the **Model 2 transactional** design (mandatory-look amendment to
/// Decision 18, 2026-05-11). Budget is charged only when the parent rewards screen is
/// committed via Proceed — sub-screen Skip and Escape→Resume back-out are both
/// pre-commit "tentative" states that vanilla tracks via `_skippedRewardButtons` but
/// our budget logic deliberately ignores. The streamer can re-open the same card
/// sub-screen and claim via vote to undo a tentative skip; only the final state at
/// Proceed time is what gets charged + reported to chat.
///
/// The other rule: once a vote countdown has started for a card, that card cannot
/// be skipped via any path — sub-screen Skip is blocked by the vote patch's existing
/// `OnAlternateRewardSelected` prefix; parent Skip is blocked by the new
/// VoteInProgress guard in our OnProceedButtonPressed prefix. Streamer cannot read
/// chat's trending vote and then bail.
/// </summary>
internal static class CardRewardSkipGatePatch {
    private static readonly ActBudgetTracker _tracker = new();
    private static CardSkipCounterLabel? _activeLabel;

    /// <summary>
    /// Reference to the currently-open NRewardsScreen. Set by the _Ready postfix when
    /// the rewards screen mounts; nulled by the AfterOverlayClosed postfix when it
    /// tears down. Read by <see cref="IsRewardsScreenActive"/> for the map-button
    /// guard — Map (and its M hotkey) is blocked whenever this is alive, preventing
    /// the streamer from navigating away from the rewards flow before engaging the
    /// vote system (the parent NRewardsScreen's Proceed gate would normally enforce
    /// this, but Map bypasses Proceed entirely).
    /// </summary>
    private static NRewardsScreen? _activeRewardsScreen;

    /// <summary>
    /// True while a rewards screen is mounted on the overlay stack. Used by the
    /// global map-button guard; see field doc-comment on
    /// <see cref="_activeRewardsScreen"/>. Defensive: clears the stored reference
    /// if the Godot side has freed the screen via an unexpected path.
    /// </summary>
    internal static bool IsRewardsScreenActive {
        get {
            if (_activeRewardsScreen is null) return false;
            if (!GodotObject.IsInstanceValid(_activeRewardsScreen)) {
                _activeRewardsScreen = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Per-rewards-screen tracking of which NRewardButton instances had their card
    /// sub-screen opened. Populated by the NRewardButton.OnRelease prefix for
    /// CardReward buttons; consulted by OnProceedButtonPressed
    /// to enforce mandatory-look. Cleared in _Ready postfix (fresh rewards
    /// screen → fresh tracking) and AfterOverlayClosed postfix (defensive cleanup).
    /// </summary>
    private static readonly HashSet<ulong> _openedCardRewardButtonIds = new();

    private static readonly Lazy<FieldInfo?> _rewardButtonsField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));
    private static readonly Lazy<FieldInfo?> _proceedButtonField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_proceedButton"));

    /// <summary>
    /// Dev-console hook: zero the per-act counter and refresh the on-screen label if
    /// it's currently mounted. Preserves run/act memory so the next rewards-screen
    /// observation doesn't fire a spurious "reset" chat receipt.
    /// </summary>
    internal static int ResetBudgetForDevConsole() {
        int previousUsed = _tracker.ActUsed;
        _tracker.ResetCounterOnly();
        if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
            _activeLabel.UpdateText(_tracker.Snapshot(BootstrapModSettings.Current?.CardSkipsPerAct ?? 1));
        }
        return previousUsed;
    }

    /// <summary>
    /// Skip gate enforces only when card-vote infrastructure is fully available.
    /// Temporary Twitch disconnect mid-run (`Reconnecting` / `Connecting`) does NOT
    /// disable the gate — those are transient and chat reconnect + IRC backlog
    /// handle them per Decision 21. **Terminal** chat-failure states DO disable the
    /// gate (Decision 21 amendment 2026-05-11 — see spec): if chat can't connect or
    /// join the channel, the streamer's gameplay should not be gated against a chat
    /// that will never recover for this session. Otherwise the streamer is stuck in
    /// the worst-of-both-worlds state (no chat to vote, but skip is still
    /// mandatory-look + budget gated).
    /// </summary>
    internal static bool ShouldEnforceSkipGate() {
        if (ModEntry.Settings is not SettingsResult.Success) return false;
        if (!CardRewardVotePatch.PreparedSuccessfully) return false;
        if (Voter.Default == null) return false;

        // Route Twitch-state-check explicitly (v4 Round-2 C-3): the aggregate
        // MultiChatService.State is best-of-children, which would mask Twitch
        // terminal failures when YT is alive. The gate exists to ensure
        // receipts can fire — and per D3 receipts only fire on Twitch — so
        // route through GetChildState(Twitch) when the chat is a multi.
        if (Voter.Default.Chat is MultiChatService multi) {
            var twitchState = multi.GetChildState(ChatPlatformNames.Twitch);
            if (twitchState is ChatConnectionState.AuthenticationFailed
                            or ChatConnectionState.JoinFailed
                            or ChatConnectionState.Disposed) return false;
        } else {
            // Direct-Twitch path (defensive — shouldn't happen post-v4 since
            // ModEntry always wires a MultiChatService; tests may inject a
            // FakeChatService directly).
            var chatState = Voter.Default.Chat?.State;
            if (chatState is ChatConnectionState.AuthenticationFailed
                          or ChatConnectionState.JoinFailed
                          or ChatConnectionState.Disposed) return false;
        }
        return true;
    }

    /// <summary>
    /// Hard-check shared by all postfixes' Prepare methods. Returns false if any
    /// required reflected field is missing (patch silently skips registration).
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
    /// Direct property access — NRewardButton.Reward is a public property.
    ///
    /// SpecialCardReward is deliberately NOT gateable: it has no selection
    /// sub-screen — clicking its button claims the card instantly via
    /// Reward.OnSelect(). Mandatory-look can't apply (the only way to "look"
    /// is to take), and vanilla treats leaving it behind as a free decline
    /// (Thieving Hopper recovered card, Lantern Key), so it must neither
    /// block Proceed nor charge skip budget. Surfaced live on FrostPrime's
    /// stream 2026-06-08: the gate made a recovered stolen card un-declinable.
    /// </summary>
    private static bool IsCardRewardButton(Control button) {
        if (!GodotObject.IsInstanceValid(button)) return false;
        if (button is not NRewardButton rb) return false;
        return rb.Reward is CardReward;
    }

    /// <summary>
    /// True iff at least one card reward remains alive in _rewardButtons. Vanilla
    /// removes claimed buttons via RewardCollectedFrom → RemoveButton; sub-screen
    /// Skip and back-out leave the button alive (they only add it to
    /// _skippedRewardButtons, which Model 2 deliberately ignores).
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
    /// Snapshot of pending card-reward buttons on a rewards screen. Under Model 2,
    /// "pending" simply means "alive in _rewardButtons" — sub-screen Skip and
    /// Escape→Resume both leave the button alive (vanilla never removes from
    /// _rewardButtons except on claim via RewardCollectedFrom). So pending counts
    /// exactly the card-rewards whose Reward.OnSkipped() vanilla will invoke when
    /// AfterOverlayClosed iterates remaining children on screen close.
    ///
    /// `Unopened` = pending buttons whose sub-screen the streamer never opened. The
    /// mandatory-look gate blocks Proceed when Unopened > 0.
    /// </summary>
    private readonly record struct PendingCardRewards(int Total, int Unopened);

    private static PendingCardRewards CountPendingCardRewards(NRewardsScreen screen) {
        var buttons = GetRewardButtons(screen);
        if (buttons is null) return new PendingCardRewards(0, 0);

        int total = 0;
        int unopened = 0;
        foreach (var b in buttons) {
            if (!GodotObject.IsInstanceValid(b)) continue;
            if (b is not NRewardButton rb) continue;
            // SpecialCardReward (sibling of CardReward, no sub-screen) is not
            // gateable — see IsCardRewardButton doc comment.
            if (rb.Reward is not CardReward) continue;
            total++;
            if (!_openedCardRewardButtonIds.Contains(rb.GetInstanceId())) unopened++;
        }
        return new PendingCardRewards(total, unopened);
    }

    /// <summary>
    /// Read the current act index from RunState (spike-pinned 0-based property).
    /// Returns null on access failure (tracker treats null as "no change").
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

    private static RunState? TryGetRunState() {
        try {
            return RunManager.Instance?.DebugOnlyGetState();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Ensure a CardSkipCounterLabel is attached as a child of the choose-a-card
    /// screen and shows the current committed snapshot. Hidden when
    /// <paramref name="actLimit"/> &lt; 0 (unlimited).
    /// Parenting under the screen means Godot's natural scene-tree teardown frees
    /// the label when the screen closes — no explicit cleanup patch needed.
    /// </summary>
    private static void AttachOrUpdateLabel(Node parent, Control? skipButton, int actLimit) {
        if (actLimit < 0) {
            if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
                _activeLabel.Visible = false;
            }
            return;
        }

        if (_activeLabel is null || !GodotObject.IsInstanceValid(_activeLabel)) {
            try {
                _activeLabel = CardSkipCounterLabel.AttachTo(parent, skipButton);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] label attach failed", ex);
                return;
            }
        }
        _activeLabel.UpdateText(_tracker.Snapshot(actLimit));
    }

    private static string FormatSkipReceipt(int actUsed, int actLimit) {
        string streamerName = BootstrapModSettings.GetStreamerDisplayName();
        if (actLimit < 0) return $"{streamerName} skipped a card reward.";
        int remaining = Math.Max(0, actLimit - actUsed);
        return $"{streamerName} skipped a card reward. {remaining} remaining this act";
    }

    private static void SendSkipReceipt(int actLimit) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = FormatSkipReceipt(_tracker.ActUsed, actLimit);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
    }

    /// <summary>
    /// Fires once at the moment the tracker detects a run change or act change. Lets
    /// chat know the budget reset without them having to infer from the running-skip
    /// receipts (which only fire on actual skips). Suppressed for unlimited
    /// (actLimit &lt; 0) and zero (actLimit == 0) — no meaningful reset to announce
    /// in either case ("reset to 0" is just noise; the streamer can't skip at all).
    /// Also suppressed if act detection failed (humanActNumber &lt;= 0); the reset
    /// still happened internally but we don't want to send "Act 0".
    /// </summary>
    private static void SendBudgetResetReceipt(int actLimit, int humanActNumber) {
        if (actLimit <= 0) return;
        if (humanActNumber <= 0) return;
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = $"Card skips reset to {actLimit} for Act {humanActNumber}";
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
    }

    /// <summary>
    /// Called by <c>CardRewardVotePatch.NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix</c>
    /// when the streamer clicks the (visible) Skip alternative on a card-reward sub-screen.
    ///
    /// Returns <c>true</c> if vanilla should be allowed to run (sub-screen closes, parent
    /// reward button is removed via vanilla's natural <c>RewardCollectedFrom</c> path —
    /// the Skip alt's <c>AfterSelected</c> was flipped to <c>EndSelectionAndCompleteReward</c>
    /// by <c>CardRewardVotePatch.CardRewardAlternative_Generate_Postfix</c>).
    /// Returns <c>false</c> to silently no-op when the per-act skip budget is exhausted —
    /// the streamer must engage chat instead.
    ///
    /// Gate-inactive states (chat in terminal-failure, vote patch unprepared, MP run,
    /// unlimited budget) all return <c>true</c> without touching the tracker —
    /// vanilla-like behavior preserved.
    ///
    /// Per-run / per-act resets are handled by the existing <c>NRewardsScreen_Ready_Postfix</c>
    /// when the parent rewards screen first opens, so this method does not re-run
    /// <c>ObserveRunAndAct</c>.
    /// </summary>
    internal static bool TryConsumeStreamerSkip(NCardRewardSelectionScreen subScreen) {
        try {
            if (!ShouldEnforceSkipGate()) return true;

            var runState = TryGetRunState();
            if (runState is null) return true;

            // MP bail — mirrors NRewardsScreen_Ready_Postfix's MP guard. In MP the budget
            // feature is disabled and streamer-Skip works as vanilla intends.
            try {
                if (runState.Players?.Count is int n && n > 1) return true;
            } catch { /* swallow — proceed without MP bail if accessor failed */ }

            int actLimit = BootstrapModSettings.Current?.CardSkipsPerAct ?? 1;
            if (actLimit < 0) return true;   // unlimited — no gating

            if (_tracker.ActUsed >= actLimit) {
                TiLog.Info($"[SlayTheStreamer2][card-skip-gate] streamer-Skip click blocked: budget exhausted ({_tracker.ActUsed}/{actLimit})");
                return false;
            }

            _tracker.RecordUse();
            TiLog.Info($"[SlayTheStreamer2][card-skip-gate] streamer-Skip click consumed: {_tracker.ActUsed}/{actLimit}");

            if (_activeLabel is not null && GodotObject.IsInstanceValid(_activeLabel)) {
                _activeLabel.UpdateText(_tracker.Snapshot(actLimit));
            }

            SendSkipReceipt(actLimit);
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] TryConsumeStreamerSkip failed; allowing click to fall through to vanilla", ex);
            return true;
        }
    }

    // v0.106.1 retargeted SetRewards → _Ready. Vanilla removed the standalone
    // SetRewards(IEnumerable<Reward>) method; ShowScreen(RewardsSet,...) now
    // pre-assigns _rewardsSet on the instance, and _Ready inlines the loop that
    // builds _rewardButtons from _rewardsSet.Rewards. So _Ready is the new single
    // population point per rewards-screen lifetime. Our postfix runs synchronously
    // after vanilla's _Ready returns — at that point _rewardButtons is fully
    // populated and HasUnclaimedCardReward / CountPendingCardRewards observe the
    // correct state. (Vanilla's UpdateScreenState is CallDeferred at the tail of
    // _Ready, but we don't depend on that having run.)
    [HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
    internal static class NRewardsScreen_Ready_Postfix {
        static bool Prepare() => PrepareHardChecks();

        static void Postfix(NRewardsScreen __instance) {
            try {
                // Fresh rewards screen → fresh mandatory-look tracking. ShowScreen
                // always Instantiates a new NRewardsScreen, so each _Ready is a
                // one-and-only population for that instance; clearing keeps the set
                // bounded across screens.
                _openedCardRewardButtonIds.Clear();

                // Track the active rewards screen for the map-button guard. Set this
                // unconditionally (before ShouldEnforceSkipGate), so Map is blocked
                // even in chat-degraded states — the streamer shouldn't be able to
                // navigate away from rewards regardless of chat availability.
                _activeRewardsScreen = __instance;

                if (!ShouldEnforceSkipGate()) return;

                var runState = TryGetRunState();
                if (runState is null) return;

                // MP bail
                try {
                    if (runState.Players?.Count is int n && n > 1) return;
                } catch { /* swallow — proceed without MP bail if accessor failed */ }

                int? actIndex = GetCurrentActIndex(runState);
                var resetReason = _tracker.ObserveRunAndAct(runState.Rng?.StringSeed, actIndex);

                if (resetReason != BudgetResetReason.None) {
                    // Human-readable act number (1-based). actIndex is 0-based; if null
                    // we send 0 to the receipt (which skips formatting it) and tag the
                    // log with "?" so it's debuggable.
                    int humanAct = actIndex.HasValue ? actIndex.Value + 1 : 0;
                    SendBudgetResetReceipt(BootstrapModSettings.Current?.CardSkipsPerAct ?? 1, humanAct);
                    TiLog.Info($"[SlayTheStreamer2][card-skip-gate] budget reset ({resetReason}); Act {(actIndex.HasValue ? humanAct.ToString() : "?")}");
                }

                // Vote-override budget shares the reset cadence (spec §2.5).
                var overrideReason = VoteOverrideBudget.Observe(runState.Rng?.StringSeed, actIndex);
                VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIndex.HasValue ? actIndex.Value + 1 : 0);

                // NOTE: Model 2 deliberately omits a population-time DisallowSkipping
                // call. The OnProceedButtonPressed prefix below is the single source of
                // truth for whether Proceed is allowed (mandatory-look + budget check).
                // Setting _skipDisallowed=true here would persist for the screen's
                // lifetime — bad if the streamer claims cards mid-screen and the
                // remaining budget would now allow a skip. The streamer will see
                // clicks no-op silently when blocked; counter label gives visual
                // feedback. v0.2 polish: live DisallowSkipping/AllowSkipping toggling
                // as state changes.
                //
                // CardSkipCounterLabel is no longer attached here — it now mounts
                // under the choose-a-card sub-screen (NCardRewardSelectionScreen)
                // so it appears only when the streamer is actually choosing a card,
                // not the whole time the rewards overview is up. See the postfix
                // on NCardRewardSelectionScreen._Ready below.
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] _Ready postfix failed", ex);
            }
        }
    }

    /// <summary>
    /// Charges per-pending-card budget when the rewards screen closes. Under Model 2
    /// this is the single charge point — RewardSkippedFrom (which fires on every
    /// sub-screen close-without-pick, including back-out) is deliberately not
    /// patched, so no charge happens at "tentative" time. Closing the screen means
    /// the streamer committed via Proceed (the OnProceedButtonPressed prefix already
    /// ran and approved); we just count what's about to get OnSkipped().
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "AfterOverlayClosed")]
    internal static class NRewardsScreen_AfterOverlayClosed_BudgetPrefix {
        static bool Prepare() => PrepareHardChecks();

        static void Prefix(NRewardsScreen __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return;
                if (CardRewardVotePatch.VoteInProgress) {
                    // Defensive — OnProceedButtonPressed prefix should have already blocked
                    // a vote-in-progress Proceed click. If the screen is closing for some
                    // other reason while a vote is open, the vote's IsInstanceValid drop
                    // path will handle it; we just don't charge.
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed: vote in progress; skipping budget charge");
                    return;
                }

                var pending = CountPendingCardRewards(__instance);
                if (pending.Total == 0) return;

                for (int i = 0; i < pending.Total; i++) {
                    _tracker.RecordUse();
                    SendSkipReceipt(BootstrapModSettings.Current?.CardSkipsPerAct ?? 1);
                }
                TiLog.Info($"[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed: charged {pending.Total} card-skip(s) on Proceed commit");
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] AfterOverlayClosed budget prefix failed", ex);
            }
        }
    }

    /// <summary>
    /// Attach the streamer-skip counter label as a child of the choose-a-card
    /// screen. Parenting under the screen means Godot's scene-tree teardown
    /// auto-frees the label when the streamer dismisses the screen — no
    /// explicit cleanup patch needed. _Ready fires once per screen instance
    /// (each ShowScreen instantiates fresh), so this is a one-and-only attach.
    /// Hidden during votes via CardSkipCounterLabel._Process polling
    /// CardRewardVotePatch.VoteInProgress.
    /// </summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
    internal static class NCardRewardSelectionScreen_Ready_LabelAttach_Postfix {
        static bool Prepare() => PrepareHardChecks();

        static void Postfix(NCardRewardSelectionScreen __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return;
                var runState = TryGetRunState();
                if (runState is null) return;
                try {
                    if (runState.Players?.Count is int n && n > 1) return;
                } catch { /* swallow — proceed without MP bail if accessor failed */ }

                // Resolve the Skip alternative button (always added first per vanilla
                // CardRewardAlternative.Generate ordering) so the label can poll its
                // GlobalPosition to track aspect-ratio changes. Null is tolerated —
                // the label falls back to a fixed viewport-Y anchor.
                Control? skipButton = null;
                try {
                    skipButton = __instance.GetNodeOrNull<Control>("UI/RewardAlternatives")?.GetChild(0) as Control;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] skip-button lookup threw: {ex.Message}");
                }

                AttachOrUpdateLabel(__instance, skipButton, BootstrapModSettings.Current?.CardSkipsPerAct ?? 1);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] choose-a-card _Ready postfix failed", ex);
            }
        }
    }

    [HarmonyPatch(typeof(NRewardsScreen), "AfterOverlayClosed")]
    internal static class NRewardsScreen_AfterOverlayClosed_Postfix {
        static bool Prepare() => true;
        static void Postfix(NRewardsScreen __instance) {
            // Label is parented under the choose-a-card screen now, so it auto-frees
            // when that screen closes (which happens before / independent of this
            // top-level rewards-screen close). Clear the static reference here as a
            // safety net for the next rewards-screen lifecycle.
            _activeLabel = null;

            // Defensive — _Ready postfix clears, but if a screen tears down before
            // any further rewards screen is shown (e.g., abandon-run mid-screen),
            // this keeps the set bounded.
            _openedCardRewardButtonIds.Clear();

            // Release the map-button guard. Only clear if this instance is the one
            // we're tracking — defensive against races where a fresh _Ready may have
            // already replaced the reference (shouldn't happen for the rewards-screen
            // lifecycle but cheap to guard).
            if (ReferenceEquals(_activeRewardsScreen, __instance)) {
                _activeRewardsScreen = null;
            }
        }
    }

    /// <summary>
    /// Mandatory-look tracker: fires when the streamer releases a card-reward
    /// NRewardButton. Vanilla's OnRelease (NRewardButton.cs:214) is the sync click
    /// handler that kicks off `GetReward()` — which awaits `Reward.OnSelectWrapper()`
    /// and opens the NCardRewardSelectionScreen sub-screen for CardReward.
    /// Records the button's instance ID; consulted later by
    /// OnProceedButtonPressed prefix.
    /// </summary>
    [HarmonyPatch(typeof(NRewardButton), "OnRelease")]
    internal static class NRewardButton_OnRelease_Prefix {
        static bool Prepare() => true;

        static void Prefix(NRewardButton __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return;
                if (!GodotObject.IsInstanceValid(__instance)) return;
                if (__instance.Reward is not CardReward) return;
                _openedCardRewardButtonIds.Add(__instance.GetInstanceId());
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] OnRelease prefix could not record opened button: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The Proceed commit gate. Under Model 2 the click on the parent's bottom-right
    /// button (visually "Skip" or "Proceed" depending on vanilla's _proceedButton.IsSkip
    /// state) is the single commit point — until this click, sub-screen Skip and
    /// Escape→Resume are reversible tentative states the streamer can undo by
    /// re-opening and claiming via vote.
    ///
    /// Three block reasons:
    ///   1. Vote in progress — streamer must not be able to read chat's trending vote
    ///      and bail. Sub-screen Skip during vote is blocked elsewhere by the vote
    ///      patch's OnAlternateRewardSelected prefix; this is the parallel guard for
    ///      parent Skip during vote (e.g., streamer Escape→Resume back to parent while
    ///      vote runs in background).
    ///   2. Mandatory-look unsatisfied — any pending card-reward button whose
    ///      sub-screen was never opened blocks the click.
    ///   3. Budget would be exceeded — pending count + already-used &gt; limit.
    ///
    /// On pass, the AfterOverlayClosed prefix charges budget per pending card during
    /// vanilla's natural screen-close path.
    ///
    /// Known v0.1 quirk: combat-reward FTUE first-fire shows a tutorial modal instead
    /// of actually proceeding. The streamer then clicks Proceed again. Our prefix
    /// runs on both clicks; the first lets vanilla through to FTUE (no charge — the
    /// screen doesn't tear down, AfterOverlayClosed doesn't fire), the second runs
    /// the same idempotent checks and AfterOverlayClosed charges once. No double-charge.
    /// </summary>
    [HarmonyPatch(typeof(NRewardsScreen), "OnProceedButtonPressed")]
    internal static class NRewardsScreen_OnProceedButtonPressed_Prefix {
        static bool Prepare() => PrepareHardChecks();

        static bool Prefix(NRewardsScreen __instance) {
            try {
                if (!ShouldEnforceSkipGate()) return true;

                if (CardRewardVotePatch.VoteInProgress) {
                    TiLog.Info("[SlayTheStreamer2][card-skip-gate] parent Skip blocked: vote in progress");
                    return false;
                }

                var pending = CountPendingCardRewards(__instance);
                if (pending.Total == 0) return true;   // nothing to commit — let vanilla through

                if (pending.Unopened > 0) {
                    TiLog.Info($"[SlayTheStreamer2][card-skip-gate] parent Skip blocked: mandatory-look unsatisfied for {pending.Unopened}/{pending.Total} pending card reward(s)");
                    return false;
                }

                int limit = BootstrapModSettings.Current?.CardSkipsPerAct ?? 1;
                if (limit >= 0 && _tracker.ActUsed + pending.Total > limit) {
                    int remaining = Math.Max(0, limit - _tracker.ActUsed);
                    TiLog.Info($"[SlayTheStreamer2][card-skip-gate] parent Skip blocked: would cost {pending.Total} skip(s); only {remaining}/{limit} budget remaining");
                    return false;
                }

                return true;   // gate passed; AfterOverlayClosed prefix will charge
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-skip-gate] OnProceedButtonPressed prefix failed; falling back to vanilla", ex);
                return true;
            }
        }
    }
}
