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
using MegaCrit.Sts2.Core.Nodes.Rooms;
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

// PrefixContinue / HandleVoteAsync / ResumeOnMainThread land in Task 9.
// This file currently hosts the Harmony target wiring + Prefix bail order +
// the pre-warm helper. PrefixContinue is a stub that falls through to vanilla.
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally",
              new[] { typeof(string), typeof(List<ModifierModel>) })]
internal static partial class ActVariantVotePatch {

    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;   // intentional process-lifetime suppression — once-per-process is the right cadence

    /// <summary>
    /// Shared cancellation/no-votes flag state for one active vote.
    /// Allocated locally in PrefixContinue (Task 9); not a static singleton.
    /// Mentioned here for type visibility — class extension lands in Task 9.
    /// </summary>
    private sealed class PendingActVariantVote {
        public int Cancelled;
        public int NoVotes;
    }

    private static readonly Lazy<MethodInfo?> _beginRunLocallyMethod =
        new(() => AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally",
                                     new[] { typeof(string), typeof(List<ModifierModel>) }));

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_beginRunLocallyMethod.Value is null) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] hard check failed: StartRunLobby.BeginRunLocally(string, List<ModifierModel>) not found via reflection; patch will not register");
                return false;
            }
            return true;
        }
        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(string) ||
            parameters[1].ParameterType != typeof(List<ModifierModel>)) {
            TiLog.Error($"[SlayTheStreamer2][act-variant-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    private static bool GetVoteOnActVariantSetting() {
        return ModEntry.Settings is SettingsResult.Success s
            ? s.Settings.VoteOnActVariant
            : true;
    }

    private static bool GetForceL3PopupFallbackSetting() {
        return ModEntry.Settings is SettingsResult.Success s
            ? s.Settings.ForceL3PopupFallback
            : false;
    }

    static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers) {
        // 1. Synthetic resume passes through.
        if (_resumeInProgress == 1) return true;

        // 2. Atomic acquire — moved up to close the chat-disconnect race
        //    (per spec v3 round-2 meta-review).
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        try {
            int playerCount = TryGetPlayerCount(__instance) ?? 1;
            var coordinator = Voter.Default;
            var chatState = coordinator?.Chat.State ?? ChatConnectionState.Disconnected;
            var candidates = ActVariantVoteResolver.BuildCandidates();

            var reason = ActVariantVoteResolver.ShouldBail(
                settingsEnabled: GetVoteOnActVariantSetting(),
                playerCount: playerCount,
                chatState: chatState,
                act1Value: __instance.Act1 ?? ActVariantVoteResolver.RandomActKey,
                candidateCount: candidates.Count);

            if (reason is not ActVariantVoteResolver.BailReason.None) {
                LogBailAndRelease(reason, __instance, playerCount);
                return true;
            }

            // coordinator guaranteed non-null when ShouldBail returns None.
            return PrefixContinue(__instance, seed, modifiers, candidates, coordinator!);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] Prefix threw; bailing to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }

    private static int? TryGetPlayerCount(StartRunLobby instance) {
        try { return instance?.Players?.Count; } catch { return null; }
    }

    private static void LogBailAndRelease(
            ActVariantVoteResolver.BailReason reason,
            StartRunLobby instance,
            int playerCount) {
        switch (reason) {
            case ActVariantVoteResolver.BailReason.SettingsOff:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] settings off; bailing to vanilla");
                break;
            case ActVariantVoteResolver.BailReason.Multiplayer:
                if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] multiplayer detected (Players.Count={playerCount}); bailing to vanilla");
                }
                break;
            case ActVariantVoteResolver.BailReason.ChatUnreadable:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] chat not readable; bailing to vanilla");
                break;
            case ActVariantVoteResolver.BailReason.Act1Pinned:
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] Act1 explicitly pinned ({instance.Act1}); skipping vote");
                break;
            case ActVariantVoteResolver.BailReason.PoolDegenerate:
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] degenerate pool; bailing to vanilla");
                break;
        }
        Interlocked.Exchange(ref _voteInProgress, 0);
    }

    private static bool PrefixContinue(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> modifiers,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator) {

        // Defensive modifier copy so the resumed run is deterministic against
        // any UI mutation during the 30s vote window.
        var capturedModifiers = modifiers.ToList();

        // Build an Rng from the seed for BackgroundAssets layer-picking.
        // Mirror vanilla's StartRunLobby pattern (StartRunLobby.cs:410):
        //   Rng rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed));
        // We salt with a slice-specific suffix so picks don't collide with vanilla.
        var rng = new Rng((uint)StringHelper.GetDeterministicHashCode(seed + "-act-variant-vote"));

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
            session = coordinator.Start(
                label: "Act 1 variant",
                options: labels,
                duration: TimeSpan.FromSeconds(30),
                receipts: null,
                parsing: null,
                formatReceipt: formatReceipt);

            var popup = new ActVariantVotePopup(
                candidates: candidates,
                factories: factories,
                mode: prewarm.Mode,
                session: session,
                dispatcher: coordinator.Dispatcher,
                shouldCancel: () => IsRunStartAbandoned(instance),
                onUserAbandoned: () => Interlocked.Exchange(ref pending.Cancelled, 1),
                isOccludingOverlayVisible: IsOccludingOverlayVisible);
            coordinator.Dispatcher.Post(() => popup.Open());

            _ = HandleVoteAsync(instance, seed, capturedModifiers, session, candidates, coordinator, pending);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; cancelling started session", ex);
            try { session?.Cancel(); } catch { /* swallow */ }
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        return false;  // suspend vanilla
    }

    /// <summary>
    /// Probe passed to ActVariantVotePopup so the popup hides itself while the
    /// dev console (or other vanilla overlay we don't want to cover) is up.
    /// At run-start, NRun.Instance is null until BeginRunLocally completes, so
    /// the submenu-stack arm BossVotePatch uses isn't relevant here — only
    /// NDevConsole is. Mirrors BossVotePatch.IsOccludingOverlayVisible's defensive
    /// shape so future code can fold in pause-menu detection without rework.
    /// </summary>
    private static bool IsOccludingOverlayVisible() {
        try {
            return NDevConsole.Instance?.Visible ?? false;
        } catch {
            return false;
        }
    }

    private static bool IsRunStartAbandoned(StartRunLobby instance) {
        // Spike #4 attempted to identify a probe via NGame.RootSceneContainer.CurrentScene,
        // but character-select sits as a submenu of NMainMenu, so CurrentScene stays
        // NMainMenu throughout the vote. There's no clean "navigated back" signal at
        // run-start; the popup's ESC handler (Task 11) is the primary cancellation path.
        // Returning false means the popup's _Process poll never fires the cancel path;
        // only ESC (or game shutdown) cancels.
        return false;
    }

    private static async Task HandleVoteAsync(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> capturedModifiers,
            VoteSession session,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator,
            PendingActVariantVote pending) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int? winnerIndex = null;
            try {
                int idx = await session.AwaitWinnerAsync();
                if (idx >= 0 && idx < candidates.Count) winnerIndex = idx;
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

            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: winnerKey={winnerKey} (cancelled={cancelled}, noVotes={noVotes}, seed={seed})");

            coordinator.Dispatcher.Post(() =>
                ResumeOnMainThread(instance, seed, capturedModifiers, winnerKey, cancelled));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
            try {
                coordinator.Dispatcher.Post(() =>
                    ResumeOnMainThread(instance, seed, capturedModifiers, ActVariantVoteResolver.RandomActKey, cancelled: true));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback Post threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> capturedModifiers,
            string winnerKey,
            bool cancelled) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        string? previousAct1 = null;
        try {
            if (cancelled) {
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: vote cancelled; aborting without re-invoke");
                SendCancellationReceipt();
                return;
            }

            previousAct1 = instance.Act1;

            if (!string.Equals(winnerKey, ActVariantVoteResolver.RandomActKey, StringComparison.Ordinal)) {
                instance.Act1 = winnerKey;
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Act1 = {winnerKey} (previous: {previousAct1})");
            }

            var method = _beginRunLocallyMethod.Value;
            if (method is null) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] _beginRunLocallyMethod is null; cannot re-invoke");
                return;
            }

            try {
                method.Invoke(instance, new object?[] { seed, capturedModifiers });
            } catch (TargetInvocationException tie) {
                // Spike #2 verified BeginRunLocally is idempotent pre-line-411 (only
                // Rng construction runs before the GetRandomList call). Fallback
                // re-invoke with Act1=random is safe — won't double-create state.
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] re-invoke threw; attempting fallback with Act1=random",
                    tie.InnerException ?? tie);
                winnerKey = ActVariantVoteResolver.RandomActKey;  // align with finally so restoration is consistent
                try {
                    instance.Act1 = ActVariantVoteResolver.RandomActKey;
                    method.Invoke(instance, new object?[] { seed, capturedModifiers });
                } catch (TargetInvocationException fallbackTie) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw; player may be soft-locked",
                        fallbackTie.InnerException ?? fallbackTie);
                } catch (Exception fallbackEx) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw (non-reflection)", fallbackEx);
                }
            }
        } catch (TargetInvocationException tie) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw (reflection)", tie.InnerException ?? tie);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
        } finally {
            if (previousAct1 is not null
                    && !string.Equals(winnerKey, ActVariantVoteResolver.RandomActKey, StringComparison.Ordinal)) {
                try { instance.Act1 = previousAct1; } catch { /* swallow */ }
            }
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
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
