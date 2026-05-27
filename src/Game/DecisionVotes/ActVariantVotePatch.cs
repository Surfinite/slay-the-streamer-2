using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

// Patch targets: NCharacterSelectScreen.OnEmbarkPressed(NButton _) AND
//                NCustomRunScreen.OnEmbarkPressed(NButton _).
//
// Why this seam (vs. StartRunLobby.BeginRunLocally — the original target):
// OnEmbarkPressed disables the embark / back / character buttons and calls
// _lobby.SetReady(true) BEFORE eventually reaching BeginRunLocally. Patching
// at BeginRunLocally meant a cancel-mid-vote left the lobby UI in a half-
// mutated state (buttons disabled, lobby marked ready, no path back). Patching
// at OnEmbarkPressed means: on suspend, NONE of those mutations have run yet,
// so cancel is a clean no-op — vanilla never touched the UI, no restoration
// needed. On confirm, we set Lobby.Act1 and reflectively re-invoke the same
// method; the second pass goes through with _resumeInProgress=1 set so our
// prefix passes through, and vanilla's full body runs unmodified.
//
// Both screens implement IStartRunLobbyListener, expose a public Lobby
// property, and have the identical OnEmbarkPressed(NButton) signature, so a
// shared TargetMethods() pattern works for both. The right MethodInfo for
// re-invocation is picked per-screen via a small type dispatch (see GetLobby
// / GetOnEmbarkPressedMethod).
//
// FTUE interaction (corner case, NCharacterSelectScreen only): on a profile
// that hasn't seen the FTUE, vanilla's OnEmbarkPressed shows a modal at
// line 472-480 and returns early after disabling _embarkButton. If the user
// then accepts the modal, OnEmbarkPressed recurses and our prefix fires.
// Cancelling THAT vote leaves _embarkButton disabled from vanilla's pre-modal
// disable, with no restoration path — same stuck-UI the cancel-under-
// BeginRunLocally bug had. Narrow (first run on fresh profile + voluntary
// cancel) and not a regression from vanilla's own behavior in that path.
// CLAUDE.md's "design as if streamer has unlocked everything" applies.
[HarmonyPatch]
internal static partial class ActVariantVotePatch {

    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;   // intentional process-lifetime suppression — once-per-process is the right cadence

    /// <summary>True while an act-variant vote is in flight. Read by the global
    /// map-button guard (<c>TopBarMapButtonGuardPatch</c>) so the streamer can't
    /// bypass an active vote by clicking Map / pressing M. (Act-variant votes
    /// happen on the character-select screen, where the top-bar Map button isn't
    /// shown — but the probe is exposed for uniformity in case future flows
    /// change.)</summary>
    internal static bool VoteInProgress => _voteInProgress == 1;

    /// <summary>
    /// Shared cancellation/no-votes flag state for one active vote.
    /// Allocated locally in PrefixContinue (Task 9); not a static singleton.
    /// Mentioned here for type visibility — class extension lands in Task 9.
    /// </summary>
    private sealed class PendingActVariantVote {
        public int Cancelled;
        public int NoVotes;
    }

    private static readonly Lazy<MethodInfo?> _characterSelectOnEmbark = new(() =>
        AccessTools.Method(typeof(NCharacterSelectScreen), "OnEmbarkPressed", new[] { typeof(NButton) }));
    private static readonly Lazy<MethodInfo?> _customRunOnEmbark = new(() =>
        AccessTools.Method(typeof(NCustomRunScreen), "OnEmbarkPressed", new[] { typeof(NButton) }));

    // Reflection handles for compensating vanilla's dropdown clobber inside
    // NCharacterSelectScreen.OnEmbarkPressed (line 484): `_lobby.Act1 =
    // _actDropdown.CurrentOption`. Set _actDropdown._currentOptionIndex to
    // the winner's option-index before re-invoke so vanilla writes the
    // chat-chosen variant instead of the dropdown's default "random".
    private static readonly Lazy<FieldInfo?> _actDropdownField = new(() =>
        AccessTools.Field(typeof(NCharacterSelectScreen), "_actDropdown"));
    private static readonly Lazy<FieldInfo?> _actDropdownOptionsField = new(() =>
        AccessTools.Field(typeof(NActDropdown), "_options"));
    private static readonly Lazy<FieldInfo?> _actDropdownCurrentOptionIndexField = new(() =>
        AccessTools.Field(typeof(NActDropdown), "_currentOptionIndex"));

    static IEnumerable<MethodBase> TargetMethods() {
        var found = new List<MethodBase>(2);
        if (_characterSelectOnEmbark.Value is { } m1) found.Add(m1);
        else TiLog.Error("[SlayTheStreamer2][act-variant-vote] NCharacterSelectScreen.OnEmbarkPressed(NButton) not found via reflection");
        if (_customRunOnEmbark.Value is { } m2) found.Add(m2);
        else TiLog.Error("[SlayTheStreamer2][act-variant-vote] NCustomRunScreen.OnEmbarkPressed(NButton) not found via reflection");
        return found;
    }

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            // Patch-level Prepare — fires before TargetMethods. Pass to let
            // TargetMethods drive registration; per-target validation runs
            // when Prepare(MethodBase) is called for each target below.
            return true;
        }
        var parameters = original.GetParameters();
        if (parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(NButton)) {
            TiLog.Error($"[SlayTheStreamer2][act-variant-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    /// <summary>
    /// Look up the screen's StartRunLobby. Each screen exposes a public
    /// Lobby property but they're distinct types — dispatch by C# type.
    /// </summary>
    private static StartRunLobby? GetLobby(object screen) => screen switch {
        NCharacterSelectScreen s => s.Lobby,
        NCustomRunScreen c => c.Lobby,
        _ => null,
    };

    /// <summary>
    /// Look up the MethodInfo for re-invoking the originating screen's
    /// OnEmbarkPressed during the synthetic resume path.
    /// </summary>
    private static MethodInfo? GetOnEmbarkPressedMethod(object screen) => screen switch {
        NCharacterSelectScreen _ => _characterSelectOnEmbark.Value,
        NCustomRunScreen _ => _customRunOnEmbark.Value,
        _ => null,
    };

    private static bool GetVoteOnActVariantSetting() {
        // Hot-reload read: ModSettings.Current is updated immediately when the
        // streamer toggles the setting via the in-game UI. ModEntry.Settings is
        // the captured-once load-time value and would mask the UI change.
        return SlayTheStreamer2.Game.Bootstrap.ModSettings.Current?.VoteOnActVariant ?? true;
    }

    private static bool GetForceL3PopupFallbackSetting() {
        return SlayTheStreamer2.Game.Bootstrap.ModSettings.Current?.ForceL3PopupFallback ?? false;
    }

    static bool Prefix(object __instance, NButton _) {
        // 1. Synthetic resume passes through. Set when ResumeOnMainThread
        //    re-invokes OnEmbarkPressed to let vanilla's body run unmodified.
        if (_resumeInProgress == 1) return true;

        // 2. Atomic acquire — close the chat-disconnect race.
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        try {
            var lobby = GetLobby(__instance);
            var onEmbarkMethod = GetOnEmbarkPressedMethod(__instance);
            if (lobby is null || onEmbarkMethod is null) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] unexpected screen type ({__instance?.GetType().FullName ?? "null"}) or null Lobby/method; passing through");
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }

            int playerCount = TryGetPlayerCount(lobby) ?? 1;
            var coordinator = Voter.Default;
            var chatState = coordinator?.Chat.State ?? ChatConnectionState.Disconnected;
            var candidates = ActVariantVoteResolver.BuildCandidates();

            var reason = ActVariantVoteResolver.ShouldBail(
                settingsEnabled: GetVoteOnActVariantSetting(),
                playerCount: playerCount,
                chatState: chatState,
                act1Value: lobby.Act1 ?? ActVariantVoteResolver.RandomActKey,
                candidateCount: candidates.Count);

            if (reason is not ActVariantVoteResolver.BailReason.None) {
                LogBailAndRelease(reason, lobby, playerCount);
                return true;
            }

            // coordinator guaranteed non-null when ShouldBail returns None.
            return PrefixContinue(__instance, onEmbarkMethod, lobby, candidates, coordinator!);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] Prefix threw; passing through", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }

    private static int? TryGetPlayerCount(StartRunLobby lobby) {
        try { return lobby?.Players?.Count; } catch { return null; }
    }

    private static void LogBailAndRelease(
            ActVariantVoteResolver.BailReason reason,
            StartRunLobby lobby,
            int playerCount) {
        switch (reason) {
            case ActVariantVoteResolver.BailReason.SettingsOff:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] skipping vote — setting disabled");
                break;
            case ActVariantVoteResolver.BailReason.Multiplayer:
                if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] skipping vote — multiplayer run (Players.Count={playerCount})");
                }
                break;
            case ActVariantVoteResolver.BailReason.ChatUnreadable:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] skipping vote — chat not readable");
                break;
            case ActVariantVoteResolver.BailReason.Act1Pinned:
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] skipping vote — Act1 explicitly pinned ({lobby.Act1})");
                break;
            case ActVariantVoteResolver.BailReason.PoolDegenerate:
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] skipping vote — degenerate candidate pool");
                break;
        }
        Interlocked.Exchange(ref _voteInProgress, 0);
    }

    private static bool PrefixContinue(
            object screen,
            MethodInfo onEmbarkMethod,
            StartRunLobby lobby,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator) {

        // Non-deterministic RNG for BackgroundAssets layer-picking (cosmetic —
        // chooses which variant of bg/fg layer scenes to preview). Vanilla
        // normally seeds layer picks from the run seed inside BeginRunLocally,
        // but that seed doesn't exist yet at OnEmbarkPressed-time. Random
        // per-vote layer choice is fine for a 30-second preview.
        var rng = new Rng((uint)System.Environment.TickCount);

        // Pre-warm full layered backdrop scenes for both variants.
        var prewarm = PreWarmAssets(
            candidates,
            forceL3: GetForceL3PopupFallbackSetting(),
            rng,
            out BackgroundAssets?[] backgroundAssetsByCandidate);

        // Build Func<Node>? factories per candidate. Null factories trigger
        // popup L3 fallback for that column.
        var factories = new Func<Node>?[candidates.Count];
        for (int i = 0; i < candidates.Count; i++) {
            var bg = backgroundAssetsByCandidate[i];
            if (bg is null) continue;
            // Capture bg in the closure.
            var captured = bg;
            factories[i] = () => NCombatBackground.Create(captured);
        }

        // Local-only pending state — NO static singleton.
        var pending = new PendingActVariantVote();

        // Custom formatReceipt callback: substitutes the no-votes close text
        // AND side-channels pending.NoVotes for HandleVoteAsync.
        Func<VoteSnapshot, ReceiptKind, string> formatReceipt = (snapshot, kind) =>
            ActVariantReceiptFormatter.Format(snapshot, kind, () =>
                Interlocked.Exchange(ref pending.NoVotes, 1));

        VoteSession? session = null;
        try {
            var labels = candidates.Select(c => c.Title).ToList();
            var settings = SlayTheStreamer2.Game.Bootstrap.ModSettings.Current;
            var voteDuration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
            var showTag = settings?.ShowVoteTag ?? false;
            session = coordinator.Start(
                label: "Act 1 variant",
                options: labels,
                duration: voteDuration,
                showTag: showTag,
                receipts: null,
                parsing: null,
                formatReceipt: formatReceipt);

            var popup = new ActVariantVotePopup(
                candidates: candidates,
                factories: factories,
                mode: prewarm.Mode,
                session: session,
                dispatcher: coordinator.Dispatcher,
                shouldCancel: IsRunStartAbandoned,
                onUserAbandoned: () => Interlocked.Exchange(ref pending.Cancelled, 1),
                isOccludingOverlayVisible: OverlayOcclusion.IsOccludingOverlayVisible);
            coordinator.Dispatcher.Post(() => popup.Open());

            _ = HandleVoteAsync(screen, onEmbarkMethod, lobby, session, candidates, coordinator, pending);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; cancelling started session", ex);
            try { session?.Cancel(); } catch { /* swallow */ }
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        return false;  // suspend vanilla
    }

    private static bool IsRunStartAbandoned() {
        // Popup ESC handler is the primary cancel path. No useful "navigated
        // away" probe at run-start — both screens sit as a submenu of NMainMenu
        // throughout the vote. Reserved here for parity with BossVotePopup's
        // plumbing; always returns false.
        return false;
    }

    private static async Task HandleVoteAsync(
            object screen,
            MethodInfo onEmbarkMethod,
            StartRunLobby lobby,
            VoteSession session,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator,
            PendingActVariantVote pending) {
        try {
            // No isRunDying probe here: act-variant vote runs PRE-run on Embark, before
            // BeginRunLocally creates the RunState. RunLiveness.IsRunDying treats a null
            // RunState as "dying" and would cancel the session immediately. The popup's
            // own ESC handler cancels the session on user-bail and the label tears down
            // via its Cancelled subscription, so no extra probe is needed here.
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session, placeOnLeft: ModSettings.Current?.VoteTallyOnLeft ?? false, isOccludingOverlayVisible: OverlayOcclusion.IsOccludingOverlayVisible));

            int? winnerIndex = null;
            try {
                int idx = await session.AwaitWinnerAsync();
                if (idx >= 0 && idx < candidates.Count) winnerIndex = idx;
            } catch (OperationCanceledException) {
                // Expected when the popup cancels the session (ESC, shutdown).
                // VoteSession.Cancel() propagates cancellation through the
                // TaskCompletionSource the await is observing — log at Debug,
                // not Error.
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync cancelled (expected on user abandon)");
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync threw", ex);
            }

            bool cancelled = Volatile.Read(ref pending.Cancelled) == 1;
            bool noVotes = Volatile.Read(ref pending.NoVotes) == 1;

            string winnerKey;
            if (cancelled) {
                winnerKey = ActVariantVoteResolver.RandomActKey;
            } else if (noVotes) {
                winnerKey = ActVariantVoteResolver.RandomActKey;
                // The custom no-votes receipt was already sent by formatReceipt
                // callback during session.Close. No additional send needed.
            } else {
                winnerKey = ActVariantVoteResolver.ResolveWinnerKey(candidates, winnerIndex);
            }

            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: winnerKey={winnerKey} (cancelled={cancelled}, noVotes={noVotes})");

            coordinator.Dispatcher.Post(() =>
                ResumeOnMainThread(screen, onEmbarkMethod, lobby, winnerKey, cancelled));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
            try {
                coordinator.Dispatcher.Post(() =>
                    ResumeOnMainThread(screen, onEmbarkMethod, lobby, ActVariantVoteResolver.RandomActKey, cancelled: true));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback Post threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(
            object screen,
            MethodInfo onEmbarkMethod,
            StartRunLobby lobby,
            string winnerKey,
            bool cancelled) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        string? previousAct1 = null;
        bool needsAct1Restore = false;
        try {
            if (cancelled) {
                // Clean no-op: vanilla OnEmbarkPressed never ran, so the lobby
                // UI is still in pre-click state (embark/back/character buttons
                // all enabled, lobby not SetReady). Just send the receipt and
                // release flags via finally.
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: vote cancelled; lobby UI untouched, no re-invoke");
                SendCancellationReceipt();
                return;
            }

            previousAct1 = lobby.Act1;

            if (!string.Equals(winnerKey, ActVariantVoteResolver.RandomActKey, StringComparison.Ordinal)) {
                lobby.Act1 = winnerKey;
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Lobby.Act1 = {winnerKey} (previous: {previousAct1})");
            }

            // Stage the dropdown so vanilla's `_lobby.Act1 = _actDropdown.CurrentOption`
            // at NCharacterSelectScreen.OnEmbarkPressed line 484 writes our
            // winnerKey instead of "random". SetReady → BeginRunIfAllPlayersReady
            // → BeginRunLocally reads Lobby.Act1 synchronously inside Invoke, so
            // a post-Invoke fixup would be too late. NCustomRunScreen has no
            // such clobber. Failures here are logged and pass through to the
            // previous (broken-for-singleplayer) behavior, never throw.
            if (screen is NCharacterSelectScreen) {
                TrySyncActDropdownIndex(screen, winnerKey);
            }

            try {
                // NButton arg is discarded inside vanilla's body — pass null.
                // _resumeInProgress=1 (set above) ensures our prefix passes
                // through on this synthetic re-entry.
                onEmbarkMethod.Invoke(screen, new object?[] { (NButton?)null });
            } catch (TargetInvocationException tie) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] re-invoke threw; attempting fallback with Act1=random",
                    tie.InnerException ?? tie);
                needsAct1Restore = true;
                try {
                    lobby.Act1 = ActVariantVoteResolver.RandomActKey;
                    // Resync dropdown back to "random" so the retry Invoke's
                    // line-484 write doesn't re-clobber Lobby.Act1 with the
                    // (failed) winner index we staged above.
                    if (screen is NCharacterSelectScreen) {
                        TrySyncActDropdownIndex(screen, ActVariantVoteResolver.RandomActKey);
                    }
                    onEmbarkMethod.Invoke(screen, new object?[] { (NButton?)null });
                    needsAct1Restore = false;   // fallback succeeded; leave Act1=random for vanilla
                } catch (TargetInvocationException fallbackTie) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw; player may be soft-locked",
                        fallbackTie.InnerException ?? fallbackTie);
                } catch (Exception fallbackEx) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw (non-reflection)", fallbackEx);
                }
            }
        } catch (TargetInvocationException tie) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw (reflection)", tie.InnerException ?? tie);
            needsAct1Restore = true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
            needsAct1Restore = true;
        } finally {
            // Only restore Lobby.Act1 on a real failure (both Invoke attempts
            // threw, or an outer catch fired). The happy path intentionally
            // leaves Lobby.Act1 = winnerKey so vanilla's post-Invoke run-state
            // construction reads the chat-chosen variant.
            if (needsAct1Restore && previousAct1 is not null) {
                try { lobby.Act1 = previousAct1; } catch { /* swallow */ }
            }
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    /// <summary>
    /// Reflectively sets NCharacterSelectScreen._actDropdown._currentOptionIndex
    /// to the slot in _options matching act1Value, so vanilla's line-484 write
    /// (`_lobby.Act1 = _actDropdown.CurrentOption`) writes our intended value
    /// rather than the dropdown's default. Never throws; degrades to logged
    /// no-op (i.e., previous broken behavior — vanilla writes "random") if any
    /// reflection step fails. _options order is read at runtime so a MegaCrit
    /// reorder is detected rather than silently misrouted.
    /// </summary>
    private static void TrySyncActDropdownIndex(object screen, string act1Value) {
        try {
            var dropdownField = _actDropdownField.Value;
            if (dropdownField is null) {
                TiLog.Warn("[SlayTheStreamer2][act-variant-vote] _actDropdown field not found on NCharacterSelectScreen; vanilla will clobber Lobby.Act1 with dropdown default");
                return;
            }
            if (dropdownField.GetValue(screen) is not NActDropdown dropdown) {
                TiLog.Warn("[SlayTheStreamer2][act-variant-vote] _actDropdown on NCharacterSelectScreen is null or wrong type; vanilla will clobber Lobby.Act1");
                return;
            }
            var optionsField = _actDropdownOptionsField.Value;
            if (optionsField?.GetValue(null) is not string[] options) {
                TiLog.Warn("[SlayTheStreamer2][act-variant-vote] NActDropdown._options not found or wrong type; cannot sync dropdown index");
                return;
            }
            int targetIndex = Array.IndexOf(options, act1Value);
            if (targetIndex < 0) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] winnerKey '{act1Value}' not found in NActDropdown._options ([{string.Join(",", options)}]); cannot sync dropdown index");
                return;
            }
            var indexField = _actDropdownCurrentOptionIndexField.Value;
            if (indexField is null) {
                TiLog.Warn("[SlayTheStreamer2][act-variant-vote] NActDropdown._currentOptionIndex field not found; cannot sync dropdown index");
                return;
            }
            indexField.SetValue(dropdown, targetIndex);
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: synced _actDropdown index to {targetIndex} ({act1Value}) so vanilla's line-484 write lands the chat-chosen variant");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] TrySyncActDropdownIndex threw; vanilla may clobber Lobby.Act1", ex);
        }
    }

    private static void SendCancellationReceipt() {
        var coordinator = Voter.Default;
        var state = coordinator?.Chat?.State;
        if (state != ChatConnectionState.ConnectedReadWrite) {
            TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] cancellation receipt skipped: chat state is {state?.ToString() ?? "null"}");
            return;
        }
        _ = coordinator!.Chat.SendMessageAsync(
            "Act 1 variant vote cancelled — run-start abandoned.",
            OutgoingMessagePriority.Normal);
    }

    /// <summary>
    /// Synchronously pre-warms each candidate variant's full layered combat
    /// backdrop scene. For each candidate:
    /// 1. Constructs BackgroundAssets(option.Key, rng), which walks
    ///    res://scenes/backgrounds/&lt;key&gt;/layers/ via DirAccess and picks
    ///    one random bg layer per layer group + an optional fg layer.
    /// 2. Pre-loads the root BackgroundScenePath via
    ///    PreloadManager.Cache.GetScene(...) — verified sync per spike #1.
    /// 3. Pre-loads each layer's .tscn via Cache.GetScene.
    ///
    /// Returns (a) an ActVariantPrewarmResult with the L1/L3 mode +
    /// per-candidate Succeeded/Total counts + ElapsedMs for telemetry, and
    /// (b) a parallel BackgroundAssets?[] (null entry = that variant's
    /// preload failed). The BackgroundAssets array is patch-side only
    /// because BackgroundAssets is a MegaCrit type.
    ///
    /// L3 fallback policy (all-or-nothing per v3 spec): forceL3 OR any load
    /// failure → mode=L3Fallback. The popup's L3 path renders a hex
    /// ColorRect + title text, no scene instantiation.
    /// </summary>
    internal static ActVariantPrewarmResult PreWarmAssets(
            IReadOnlyList<ActVariantOption> candidates,
            bool forceL3,
            Rng rng,
            out BackgroundAssets?[] backgroundAssetsByCandidate) {
        var sw = Stopwatch.StartNew();
        backgroundAssetsByCandidate = new BackgroundAssets?[candidates.Count];

        if (forceL3) {
            sw.Stop();
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: 0/0 assets in {sw.ElapsedMilliseconds}ms (mode={ActVariantPopupMode.L3Fallback}, reason=ForceL3PopupFallback)");
            return new ActVariantPrewarmResult(ActVariantPopupMode.L3Fallback, 0, 0, sw.ElapsedMilliseconds);
        }

        int succeeded = 0;
        int total = 0;
        bool allOk = true;

        for (int i = 0; i < candidates.Count; i++) {
            var option = candidates[i];
            BackgroundAssets? bg = null;
            try {
                bg = new BackgroundAssets(option.Key, rng);
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] BackgroundAssets construction failed for {option.Key}: {ex.Message}");
                allOk = false;
                continue;
            }

            // Root scene
            total++;
            try {
                _ = PreloadManager.Cache.GetScene(bg.BackgroundScenePath);
                succeeded++;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} root ({bg.BackgroundScenePath}): {ex.Message}");
                allOk = false;
                continue;
            }

            // BG layers
            bool layersOk = true;
            foreach (var layerPath in bg.BgLayers) {
                total++;
                try {
                    _ = PreloadManager.Cache.GetScene(layerPath);
                    succeeded++;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} layer ({layerPath}): {ex.Message}");
                    layersOk = false;
                }
            }

            // Optional FG layer
            if (!string.IsNullOrEmpty(bg.FgLayer)) {
                total++;
                try {
                    _ = PreloadManager.Cache.GetScene(bg.FgLayer);
                    succeeded++;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} fg ({bg.FgLayer}): {ex.Message}");
                    layersOk = false;
                }
            }

            if (layersOk) {
                backgroundAssetsByCandidate[i] = bg;
            } else {
                allOk = false;
            }
        }

        sw.Stop();
        var mode = allOk && succeeded == total && total > 0
            ? ActVariantPopupMode.L1Textures
            : ActVariantPopupMode.L3Fallback;
        var reason = mode == ActVariantPopupMode.L1Textures
            ? "all assets loaded"
            : total == 0 ? "no paths resolved"
            : $"{succeeded}/{total} assets loaded";

        // On L3 fallback, null out the parallel array so the popup doesn't
        // attempt to instantiate any factories. (Per all-or-nothing policy.)
        if (mode == ActVariantPopupMode.L3Fallback) {
            for (int i = 0; i < backgroundAssetsByCandidate.Length; i++) {
                backgroundAssetsByCandidate[i] = null;
            }
        }

        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms (mode={mode}, reason={reason})");
        return new ActVariantPrewarmResult(mode, succeeded, total, sw.ElapsedMilliseconds);
    }
}
