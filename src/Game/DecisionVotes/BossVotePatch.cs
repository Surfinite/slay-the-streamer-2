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
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;   // NCreatureVisuals
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

// OnProceedButtonPressed is private with an NButton param — must use string literal +
// explicit param types (nameof won't bind across private accessibility, and the NButton
// arg is discarded inside the vanilla method body but is part of the signature).
[HarmonyPatch(typeof(NTreasureRoom), "OnProceedButtonPressed", new[] { typeof(NButton) })]
internal static class BossVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;

    /// <summary>True while a boss vote is in flight. Read by the global map-button
    /// guard (<c>TopBarMapButtonGuardPatch</c>) so the streamer can't bypass an
    /// active vote by clicking Map / pressing M.</summary>
    internal static bool VoteInProgress => _voteInProgress == 1;

    // Per-(run, act) marker set after each completed boss-vote resume.
    // Prevents the vote from re-triggering on subsequent NTreasureRoom.OnProceedButtonPressed
    // clicks within the same run+act, which can happen via:
    //   - Golden Compass relic (Ancient, Act 2+): produces a fixed linear map with TWO
    //     chest rooms (GoldenPathActMap).
    //   - Map-screen back-arrow: after a chest exit, the streamer can navigate back
    //     to re-pick the relic; clicking Proceed again would otherwise re-trigger.
    // Process-local; lost on game restart.
    //
    // _lastSwappedBossId tracks WHICH boss was applied (null = no swap, i.e., no-winner
    // fallback or ApplyBossSwap throw). The idempotency check verifies the swap is still
    // present in runState.Act.BossEncounter — if not, StS2 save-quit rolled back the
    // run state to a pre-swap snapshot and we need to re-fire the vote. Without this
    // verification, the streamer would save-quit-and-Continue, fight vanilla's pre-rolled
    // boss, AND the popup would never re-prompt because the marker still matched.
    private static string? _lastSwapRunId;
    private static int _lastSwapActIndex = -1;
    private static ModelId? _lastSwappedBossId;

    /// <summary>
    /// Last NTreasureRoom captured in PrefixContinue. The rerollvote dev command
    /// reads this to re-open a fresh vote on the same chest without needing the
    /// streamer to abandon + start a new run. Cleared on resume; never read while
    /// _voteInProgress == 0.
    /// </summary>
    private static NTreasureRoom? _activeRoom;

    /// <summary>
    /// Monotonic counter incremented each time a vote (fresh or rerolled) goes
    /// "live". HandleVoteAsync captures the generation at start; ResumeOnMainThread
    /// compares its captured generation against the current value and bails if
    /// stale (i.e., a reroll superseded this resume). Prevents the cancelled-old-
    /// session's resume from applying a swap or firing the synthetic Proceed
    /// re-click on top of the fresh popup.
    /// </summary>
    private static int _voteGeneration;

    /// <summary>
    /// Dev-only salt mixed into the BossVoteSeed seed when rerollvote fires.
    /// Reset to 0 in PrefixContinue when a fresh vote starts. Each rerollvote
    /// call increments it, producing a different sample from the same (run, act)
    /// without changing the underlying determinism for production.
    /// </summary>
    private static int _rerollSalt;

    internal static bool RunIdGuardEnabled { get; private set; } = true;

    /// <summary>
    /// Runtime override hook for operator debugging. Defaults to MapCmd.SetBossEncounter.
    /// Tests do NOT use this seam (the patch file is excluded from Compile in the
    /// test csproj) — the testable winner-index→option mapping lives in BossVoteResolver.
    /// </summary>
    internal static Action<IRunState, EncounterModel> ApplyBossSwap { get; set; }
        = (rs, boss) => MapCmd.SetBossEncounter(rs, boss);

    private static readonly Lazy<MethodInfo?> _proceedMethod =
        new(() => AccessTools.Method(typeof(NTreasureRoom), "OnProceedButtonPressed", new[] { typeof(NButton) }));

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            // Registration-time. Hard check: the patched method must resolve.
            if (_proceedMethod.Value is null) {
                TiLog.Error("[SlayTheStreamer2][boss-vote] hard check failed: NTreasureRoom.OnProceedButtonPressed(NButton) not found via reflection; patch will not register");
                return false;
            }

            // Soft check: run-id accessor. Failure logs Warn but does NOT abort registration.
            try {
                var rm = RunManager.Instance;
                if (rm == null) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded: RunManager.Instance not reachable");
                    RunIdGuardEnabled = false;
                } else if (rm.GetType().GetMethod("DebugOnlyGetState") is null) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded: DebugOnlyGetState() not found");
                    RunIdGuardEnabled = false;
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] run-ID guard degraded: Prepare soft check threw: {ex.Message}");
                RunIdGuardEnabled = false;
            }
            return true;
        }

        // Per-method signature check.
        var parameters = original.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(NButton)) {
            TiLog.Error($"[SlayTheStreamer2][boss-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][boss-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NTreasureRoom __instance, NButton _) {
        // 1. Synthetic resume re-call → let vanilla through.
        if (_resumeInProgress == 1) return true;

        // 2. Validity check.
        if (!GodotObject.IsInstanceValid(__instance)) return true;

        // 3. Multiplayer bail.
        int? playerCount = TryGetPlayerCount();
        if (playerCount is int n && n > 1) {
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] multiplayer detected (Players.Count > 1); bailing to vanilla");
            } else {
                TiLog.Debug("[SlayTheStreamer2][boss-vote] multiplayer bail-out");
            }
            return true;
        }

        // 4. Chat-readable bail.
        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                        or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][boss-vote] chat not readable (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        // 5. Atomic acquire.
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][boss-vote] repeat click during open vote; suppressed");
            return false;
        }

        return PrefixContinue(__instance, coordinator);
    }

    private static int? TryGetPlayerCount() {
        try {
            return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count;
        } catch {
            return null;
        }
    }

    // Occlusion probe now lives in the shared OverlayOcclusion helper so the
    // other vote popups + the corner VoteTallyLabel can use the same logic.

    /// <summary>
    /// Probe passed to BossVotePopup so it can detect mid-vote run death and
    /// cancel the session promptly. Without this, the popup waits up to 30s
    /// for the vote timer to expire — long enough that the streamer ends up
    /// on the game-over screen (or main menu after save-quit) with the popup
    /// still rendered on top, blocking the Continue / Main Menu buttons.
    /// The save-quit-Continue marker-staleness fix lives separately in
    /// PrefixContinue's idempotency check (compares the marker against
    /// current Act.BossEncounter.Id to detect rolled-back swaps).
    /// </summary>
    private static bool IsRunDying() => RunLiveness.IsRunDying();

    /// <summary>
    /// Builds and starts a boss vote: samples candidates, pre-warms visuals,
    /// creates the session + popup, and spins up HandleVoteAsync. Used by both
    /// the fresh-vote path (PrefixContinue) and the rerollvote dev command
    /// (RerollCurrent). Throws on failure; caller is responsible for flag cleanup.
    /// Returns the sampled list for log messages.
    /// </summary>
    private static IReadOnlyList<EncounterModel> BuildAndStartVote(
            VoteCoordinator coordinator,
            NTreasureRoom room,
            IRunState runState,
            string? runIdAtStart,
            int salt,
            int gen) {
        // Materialize pool once; exclude SecondBossEncounter if A10+ DoubleBoss.
        var pool = runState.Act.AllBossEncounters.ToList();
        if (runState.Act.HasSecondBoss) {
            var secondId = runState.Act.SecondBossEncounter?.Id;
            if (secondId is not null) pool.RemoveAll(e => e.Id == secondId);
        }
        if (pool.Count <= 1) {
            throw new InvalidOperationException($"degenerate pool (count={pool.Count}); cannot start vote");
        }

        // Stable deterministic seed + per-reroll salt for dev re-rolls.
        int seed = BossVoteSeed.Stable(runState.Rng?.StringSeed, runState.CurrentActIndex) + salt;
        var rng = new Random(seed);
        var sample = BossCandidateSampler.SampleDistinct(pool, count: 3, rng);

        var sampledIds = string.Join(", ", sample.Select((e, i) => $"#{i}={e.Title.GetFormattedText()}({e.Id})"));
        TiLog.Info($"[SlayTheStreamer2][boss-vote] opening vote for {sample.Count} options; seed={seed} (salt={salt}); gen={gen}; sampled: {sampledIds}");

        PreWarmBossVisuals(sample);

        var dtos = sample.Select((e, i) => new BossVotePopupOption(
            Index: i,
            Title: e.Title.GetFormattedText(),
            VisualsFactory: BuildVisualsFactory(e))).ToList();
        var labels = dtos.Select(d => d.Title).ToList();

        var label = salt == 0
            ? $"Act {runState.CurrentActIndex + 1} boss vote"
            : $"Act {runState.CurrentActIndex + 1} boss vote (reroll #{salt})";
        var settings = SlayTheStreamer2.Game.Bootstrap.ModSettings.Current;
        var voteDuration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
        var showTag = settings?.ShowVoteTag ?? false;
        var session = coordinator.Start(label, labels, voteDuration, showTag);

        try {
            var popup = new BossVotePopup(
                dtos,
                session,
                coordinator.Dispatcher,
                isOccludingOverlayVisible: OverlayOcclusion.IsOccludingOverlayVisible,
                isRunDying: IsRunDying);
            coordinator.Dispatcher.Post(() => popup.Show(runState.CurrentActIndex + 1));
        } catch {
            try { session.Cancel(); } catch { /* swallow */ }
            throw;
        }

        _ = HandleVoteAsync(coordinator, room, session, sample, runIdAtStart, gen);
        return sample;
    }

    private static bool PrefixContinue(NTreasureRoom room, VoteCoordinator coordinator) {
        IRunState? runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState is null) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        // Idempotency: skip if we've already voted for this run+act. Covers:
        //   - Golden Compass (Ancient relic): linear map with 2 chest rooms.
        //   - Map-screen back-arrow: returns the streamer to the chest after exit.
        // Without this guard, the second Proceed click would re-fire the vote with
        // identical candidates (seed is per-run-per-act). Marker is set in
        // ResumeOnMainThread after the synthetic re-call completes.
        //
        // BUT: also verify the swap survived. StS2's save-quit snapshot can
        // predate our mid-room swap (the save was made before the runState
        // mutation committed to disk), so after save-quit-and-Continue the
        // runState.Act.BossEncounter is the original pre-swap value. In that
        // case, clear the marker and let the vote fire fresh.
        string? currentRunId = runState.Rng?.StringSeed;
        if (currentRunId is not null
                && currentRunId == _lastSwapRunId
                && runState.CurrentActIndex == _lastSwapActIndex) {
            var currentBossId = runState.Act.BossEncounter?.Id;
            bool swapStillInPlace = _lastSwappedBossId is null
                || (currentBossId is not null && currentBossId == _lastSwappedBossId);
            if (swapStillInPlace) {
                TiLog.Info($"[SlayTheStreamer2][boss-vote] Act {runState.CurrentActIndex + 1} already had a boss vote this run; skipping subsequent chest click (Golden Compass or map back-arrow)");
                Interlocked.Exchange(ref _voteInProgress, 0);
                return true;
            }
            // Marker matched + swap is gone: StS2 save-quit-and-Continue rolled
            // back the runState to pre-swap. We remember which boss chat picked,
            // so silently re-apply it rather than forcing a redundant re-vote.
            // Streamer transitions to the map with the original chat-picked boss
            // restored; chat sees no new messages.
            if (_lastSwappedBossId is not null) {
                try {
                    var restoredBoss = runState.Act.AllBossEncounters
                        .FirstOrDefault(e => e.Id == _lastSwappedBossId);
                    if (restoredBoss is not null) {
                        TiLog.Info($"[SlayTheStreamer2][boss-vote] Act {runState.CurrentActIndex + 1} marker matched but Act.BossEncounter is {currentBossId}; save-quit rolled back the swap — silently restoring {_lastSwappedBossId} (chat already voted)");
                        ApplyBossSwap(runState, restoredBoss);
                        Interlocked.Exchange(ref _voteInProgress, 0);
                        return true;
                    }
                    TiLog.Warn($"[SlayTheStreamer2][boss-vote] _lastSwappedBossId {_lastSwappedBossId} not found in current pool; clearing marker and re-firing vote");
                } catch (Exception ex) {
                    TiLog.Error($"[SlayTheStreamer2][boss-vote] silent restore of {_lastSwappedBossId} threw; clearing marker and re-firing vote", ex);
                }
            }
            // Couldn't restore (no-winner marker, boss not in pool, or restore
            // threw). Clear marker and fall through to fresh vote.
            _lastSwapRunId = null;
            _lastSwapActIndex = -1;
            _lastSwappedBossId = null;
        }

        // Capture room + reset reroll salt + bump generation BEFORE BuildAndStartVote
        // so the rerollvote dev command has the state it needs and the new vote's
        // generation is captured by HandleVoteAsync.
        _activeRoom = room;
        _rerollSalt = 0;
        int newGen = Interlocked.Increment(ref _voteGeneration);

        // Run-id capture (soft guard).
        string? runIdAtStart = null;
        if (RunIdGuardEnabled) {
            try {
                runIdAtStart = runState.Rng?.StringSeed;
                if (runIdAtStart is null) TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded for this vote — null state or null seed at start");
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] run-ID guard degraded for this vote — {ex.Message}");
            }
        }

        try {
            BuildAndStartVote(coordinator, room, runState, runIdAtStart, salt: 0, gen: newGen);
        } catch (InvalidOperationException ex) {
            // Degenerate pool — log Info (not Error) and bail to vanilla.
            TiLog.Info($"[SlayTheStreamer2][boss-vote] {ex.Message}; bailing to vanilla");
            _activeRoom = null;
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] BuildAndStartVote threw; bailing to vanilla", ex);
            _activeRoom = null;
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Synchronously primes the asset cache for each candidate boss's combat scene.
    /// Called on the Godot main thread between candidate sampling and session start
    /// so the cold-load hitch (if any) lands BEFORE the popup appears, not during
    /// the visible 30s vote timer.
    ///
    /// PreloadManager.Cache.GetScene is verified synchronous (AssetCache.cs:30-40).
    /// Per-candidate try/catch ensures one missing scene doesn't block the others;
    /// CreateVisuals falls back to creature_visuals/fallback if a scene is missing.
    /// </summary>
    private static void PreWarmBossVisuals(IReadOnlyList<EncounterModel> candidates) {
        var sw = Stopwatch.StartNew();
        int succeeded = 0;
        foreach (var encounter in candidates) {
            try {
                var monster = encounter.AllPossibleMonsters.FirstOrDefault();
                if (monster is null) continue;
                // Construct the combat scene path directly from the monster's Id.Entry
                // rather than going through monster.AssetPaths.First(): AssetPaths
                // transitively calls GenerateMoveStateMachine(), which throws
                // "Canonical model ... used in incorrect place" for bosses with
                // mutable internal state (Ceremonial Beast is the observed case
                // — its GenerateMoveStateMachine assigns BeastCryState, which
                // AssertMutable-fails on canonical instances). VisualsPath itself
                // is pure — it's just SceneHelper.GetScenePath of the lowered entry —
                // so we replicate that convention here. Verified vs the path
                // strings in godot.log warns (e.g. res://scenes/creature_visuals/kin_follower.tscn).
                var entry = monster.Id?.Entry;
                if (string.IsNullOrEmpty(entry)) continue;
                var scenePath = SceneHelper.GetScenePath("creature_visuals/" + entry.ToLowerInvariant());
                _ = PreloadManager.Cache.GetScene(scenePath);
                succeeded++;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] preload failed for {encounter.Id?.Entry}: {ex.Message}");
            }
        }
        sw.Stop();
        TiLog.Info($"[SlayTheStreamer2][boss-vote] pre-warm: {succeeded}/{candidates.Count} candidates in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Picks the monster to render in the boss-vote popup column for a (potentially
    /// multi-monster) encounter. For single-monster encounters this is trivially the
    /// only monster. For multi-monster encounters, AllPossibleMonsters[0] is the
    /// correct primary for most cases (Queen, Crusher / Kaiser Crab) — only TheKinBoss
    /// is the exception in the current build: its AllPossibleMonsters is
    /// [KinFollower, KinPriest] but the visual "leader" is the priest.
    ///
    /// Multi-monster boss list (current build):
    ///   - KAISER_CRAB_BOSS: [CRUSHER, ROCKET] → Crusher (index 0); renders a
    ///     single claw (Crusher is one of two claws flanking the player in
    ///     combat — Kaiser Crab's visual identity comes from the background
    ///     "crab face" + both claws). The popup shows just the claw, which
    ///     reads as small/unrecognizable. Resolving cleanly would require
    ///     compositing both Crusher and Rocket scenes side-by-side, or
    ///     pulling in the boss-specific background art — both significant
    ///     work for a single boss. Tracked as deferred follow-up; revisit
    ///     if MegaCrit ships a unified Kaiser Crab bestiary image.
    ///   - QUEEN_BOSS: [QUEEN, TORCH_HEAD_AMALGAM] → Queen (index 0) ✓
    ///   - THE_KIN_BOSS: [KIN_FOLLOWER, KIN_PRIEST] → KIN_PRIEST (special-case)
    ///
    /// Note: Id.Entry uses UPPER_SNAKE_CASE in the runtime, NOT the PascalCase class
    /// name — verified via godot.log gate-1 capture (e.g.,
    /// "encounter THE_KIN_BOSS has 2 monsters; rendering primary KIN_FOLLOWER").
    /// If MegaCrit ships more multi-monster bosses where the first monster isn't the
    /// visual primary, add entries to the special-case branch below.
    /// </summary>
    private static MonsterModel? PickPrimaryMonster(EncounterModel encounter, IReadOnlyList<MonsterModel> monsters) {
        if (monsters.Count == 0) return null;
        if (monsters.Count == 1) return monsters[0];

        // Known exception: THE_KIN_BOSS's leader is the priest, not the follower.
        if (encounter.Id?.Entry == "THE_KIN_BOSS") {
            var priest = monsters.FirstOrDefault(m => m.Id?.Entry == "KIN_PRIEST");
            if (priest is not null) return priest;
        }

        return monsters[0];
    }

    /// <summary>
    /// Builds a closure that lazily produces an animated combat-idle NCreatureVisuals
    /// for the encounter's primary monster. Invoked once per column at popup.Show()
    /// time on the Godot main thread. Returns null if the encounter has no monsters
    /// (defensive: shouldn't happen for canonical boss encounters).
    ///
    /// idle_loop is canonical across all monsters (MonsterModel.cs:387 +
    /// per-monster GenerateAnimator overrides). For non-Spine creatures
    /// (HasSpineAnimation == false), skips animator wiring; static pose renders.
    /// </summary>
    private static Func<Node2D>? BuildVisualsFactory(EncounterModel encounter) {
        // Kaiser Crab special case: the per-claw monster scenes (crusher.tscn,
        // rocket.tscn) are placeholder Sprite2D nodes with no texture and
        // Visible=false — the actual visual is a unified Spine scene at
        // creature_visuals/kaiser_crab_boss.tscn that ships in v0.106.1. Bypass
        // PickPrimaryMonster + NCreatureVisuals entirely and load the unified
        // scene directly; it's a plain Node2D + SpineSprite (no NCreatureVisuals
        // script), so the SetUpSkin / per-monster animator path doesn't apply.
        if (encounter.Id?.Entry == "KAISER_CRAB_BOSS") {
            return BuildKaiserCrabVisualsFactory(encounter);
        }

        var monsters = encounter.AllPossibleMonsters.ToList();
        var monster = PickPrimaryMonster(encounter, monsters);
        if (monster is null) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] encounter {encounter.Id?.Entry} has no monsters; column will render empty");
            return null;
        }
        if (monsters.Count > 1) {
            // Multi-monster Info (not Warn) — known cases (TheKin, Queen, KaiserCrab)
            // are handled by PickPrimaryMonster's heuristic. Log for observability
            // so any future multi-monster boss surfaces in godot.log.
            TiLog.Info($"[SlayTheStreamer2][boss-vote] encounter {encounter.Id?.Entry} has {monsters.Count} monsters; rendering primary {monster.Id?.Entry}");
        }
        return () => {
            var visuals = monster.CreateVisuals();
            // SpineBody is populated by NCreatureVisuals._Ready (line 149 of
            // decompiled NCreatureVisuals.cs), which doesn't fire until the
            // visuals are added to the SceneTree — i.e., AFTER the popup's
            // slot.AddChild(visuals) call below. Checking HasSpineAnimation in
            // the factory body is therefore always false; the bestiary diag log
            // captured 2026-05-24 confirms this (zero [anim-diag] boss-vote
            // lines emitted because the if-block was skipped every column).
            //
            // Subscribe to the Ready signal so the animator setup runs at the
            // same lifecycle point that NCreature._Ready does it in the
            // bestiary — by then SpineBody is populated and GenerateAnimator's
            // SetNextState(initialState) writes the right initial animation to
            // track 0. No explicit SetAnimation override: matching the bestiary
            // exactly is the design intent (e.g., The Insatiable's bestiary
            // entry plays "intro_loop" — what looks like a still image, that's
            // vanilla art — and our previous explicit SetAnimation("idle_loop")
            // was forcing a deviation from that reference).
            var encounterId = encounter.Id?.Entry;
            var monsterId = monster.Id?.Entry;
            visuals.Ready += () => ApplyMonsterAnimationAfterReady(visuals, monster, encounterId, monsterId);
            return visuals;
        };
    }

    /// <summary>
    /// Runs after <c>NCreatureVisuals._Ready</c> has populated <c>SpineBody</c>.
    /// Mirrors the shape of <c>NCreature._Ready</c> (decompiled line 299-310):
    /// <c>GenerateAnimator</c> sets the CreatureAnimator's initial state on
    /// track 0; <c>SetUpSkin</c> applies per-monster skin variants. The
    /// initial state per monster matches what the bestiary shows, which is the
    /// design intent.
    /// </summary>
    private static void ApplyMonsterAnimationAfterReady(NCreatureVisuals visuals, MonsterModel monster, string? encounterId, string? monsterId) {
        try {
            if (!GodotObject.IsInstanceValid(visuals)) return;
            if (!visuals.HasSpineAnimation) return;
            monster.GenerateAnimator(visuals.SpineBody);
            visuals.SetUpSkin(monster);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] deferred animation setup failed for encounter={encounterId} monster={monsterId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Kaiser Crab uses a unified Spine scene at
    /// <c>res://scenes/creature_visuals/kaiser_crab_boss.tscn</c> — root Node2D
    /// with a SpineSprite child named "Visuals" using
    /// <c>kaiser_crab_skeleton_data.tres</c>. Per-claw scenes (crusher / rocket)
    /// are placeholder no-texture sprites; loading them gives an empty popup
    /// column. We bypass <c>NCreatureVisuals</c> entirely for this encounter.
    ///
    /// Animation setup happens in <see cref="ApplyKaiserCrabAnimationAfterReady"/>
    /// — kaiser_crab.skel uses a three-track namespaced animation layout
    /// (<c>body/idle_loop</c>, <c>left/idle_loop</c>, <c>right/idle_loop</c>)
    /// rather than the per-monster flat <c>idle_loop</c> convention, so we set
    /// all three tracks the same way vanilla combat does (see
    /// <c>NKaiserCrabBossBackground.cs:86-88</c>).
    /// </summary>
    private static Func<Node2D>? BuildKaiserCrabVisualsFactory(EncounterModel encounter) {
        const string scenePath = "res://scenes/creature_visuals/kaiser_crab_boss.tscn";
        var encounterId = encounter.Id?.Entry;
        return () => {
            try {
                var scene = PreloadManager.Cache.GetScene(scenePath);
                var root = scene.Instantiate<Node2D>(PackedScene.GenEditState.Disabled);

                // The unified scene's Visuals SpineSprite ships at scale (0.5, 0.5)
                // sized for the combat backdrop — far too big for the popup column.
                // ApplyPortraitFit can't auto-measure this scene because it isn't an
                // NCreatureVisuals (no Bounds Control), so fit falls back to 1.0.
                // Apply a static scale here. 0.16 was operator-tuned 2026-05-25 to
                // fit the body + both claws inside the popup column's 448×640 slot
                // with room to breathe; tweak the multiplier if a future PortraitSlot
                // size change wants the crab bigger or smaller.
                root.Scale = new Vector2(0.16f, 0.16f);

                // Defer SpineSprite setup (HasAnimation / SetAnimation / diag log)
                // until after the scene is in the tree and the SpineSprite has
                // initialized its skeleton. Same lifecycle reason as the regular
                // monster path — the Spine bindings NRE if called before the
                // scene's _Ready cycle runs.
                root.Ready += () => ApplyKaiserCrabAnimationAfterReady(root, encounterId);
                return root;
            } catch (Exception ex) {
                TiLog.Error($"[SlayTheStreamer2][boss-vote] Kaiser Crab unified scene load failed; column will render empty", ex);
                // Match the contract — visuals factory may return any Node2D, but on
                // catastrophic failure we'd prefer to render nothing rather than
                // crash. Return a bare empty Node2D so the column has *something* in
                // the SceneTree.
                return new Node2D();
            }
        };
    }

    /// <summary>
    /// Runs after the Kaiser Crab unified scene's root has entered the tree.
    /// At that point the SpineSprite child is fully initialized and the Spine
    /// bindings (GetAnimationState / SetAnimation) are safe to call.
    /// kaiser_crab.skel doesn't follow the per-monster flat-name convention
    /// (idle_loop / idle_loop1); animations live under <c>body/</c>,
    /// <c>left/</c>, <c>right/</c> sub-namespaces with a separate track per
    /// piece. We mirror vanilla's combat-entry setup at
    /// <c>NKaiserCrabBossBackground.cs:86-88</c> and assign all three tracks
    /// directly.
    /// </summary>
    private static void ApplyKaiserCrabAnimationAfterReady(Node2D root, string? encounterId) {
        try {
            if (!GodotObject.IsInstanceValid(root)) return;
            if (root.GetNodeOrNull("Visuals") is not Node spineNode) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] Kaiser Crab: unified scene missing 'Visuals' SpineSprite child; column renders unanimated");
                return;
            }
            var spine = new MegaCrit.Sts2.Core.Bindings.MegaSpine.MegaSprite(spineNode);

            // kaiser_crab.skel uses three-track namespaced animations rather
            // than the conventional flat "idle_loop". Vanilla's
            // NKaiserCrabBossBackground.cs:86-88 sets all three on combat
            // entry; we mirror that so body + both claws idle together.
            //   track 0: body/idle_loop
            //   track 1: left/idle_loop
            //   track 2: right/idle_loop
            var state = spine.GetAnimationState();
            if (state is null) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] Kaiser Crab: GetAnimationState returned null; column renders unanimated");
                return;
            }
            state.SetAnimation("body/idle_loop",  loop: true, trackId: 0);
            state.SetAnimation("left/idle_loop",  loop: true, trackId: 1);
            state.SetAnimation("right/idle_loop", loop: true, trackId: 2);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] Kaiser Crab deferred setup failed for encounter={encounterId}: {ex.Message}");
        }
    }

    private static async Task HandleVoteAsync(
        VoteCoordinator coordinator,
        NTreasureRoom room,
        VoteSession session,
        IReadOnlyList<EncounterModel> sample,
        string? runIdAtStart,
        int gen) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session, RunLiveness.IsRunDying, ModSettings.Current?.VoteTallyOnLeft ?? false, OverlayOcclusion.IsOccludingOverlayVisible));

            int? winnerIndex;
            try {
                int idx = await session.AwaitWinnerAsync();
                if (idx < 0 || idx >= sample.Count) {
                    TiLog.Warn($"[SlayTheStreamer2][boss-vote] winnerIndex {idx} out of range [0, {sample.Count}); no swap will be applied");
                    winnerIndex = null;
                } else {
                    winnerIndex = idx;
                }
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][boss-vote] AwaitWinnerAsync threw; no swap will be applied", ex);
                winnerIndex = null;
            }

            TiLog.Info($"[SlayTheStreamer2][boss-vote] resume: dispatching with winnerIndex={(winnerIndex?.ToString() ?? "null")}");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, sample, winnerIndex, runIdAtStart, gen));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] HandleVoteAsync threw; attempting no-winner fallback resume", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, sample, winnerIndex: null, runIdAtStart, gen));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][boss-vote] fallback resume Post itself threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(
            NTreasureRoom room,
            IReadOnlyList<EncounterModel> sample,
            int? winnerIndex,
            string? runIdAtStart,
            int gen) {
        // Generation guard: if this resume is from a session that was superseded by
        // a rerollvote, bail without applying any swap or firing the synthetic
        // Proceed re-click. _voteInProgress stays acquired (the fresh vote owns it).
        if (gen != _voteGeneration) {
            TiLog.Info($"[SlayTheStreamer2][boss-vote] resume from stale generation {gen} (current={_voteGeneration}); rerollvote superseded — discarding");
            return;
        }
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] resume: room no longer valid; dropping");
                SendIgnoredResultReceipt();
                return;
            }

            // Liveness checks (mirror AncientVotePatch / CardRewardVotePatch).
            IRunState? currentState;
            try {
                var rm = RunManager.Instance;
                if (rm is null) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: RunManager.Instance is null");
                    SendIgnoredResultReceipt();
                    return;
                }
                if (rm.IsAbandoned) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run was abandoned during vote");
                    SendIgnoredResultReceipt();
                    return;
                }
                currentState = rm.DebugOnlyGetState();
                if (currentState is null) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run state is gone");
                    SendIgnoredResultReceipt();
                    return;
                }
                if (currentState.IsGameOver) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run is over (player dead)");
                    SendIgnoredResultReceipt();
                    return;
                }
                if (runIdAtStart is not null) {
                    string? currentRunId = currentState.Rng?.StringSeed;
                    if (currentRunId != runIdAtStart) {
                        TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run changed during vote");
                        SendIgnoredResultReceipt();
                        return;
                    }
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] resume aborted: liveness check threw ({ex.Message})");
                SendIgnoredResultReceipt();
                return;
            }

            // Apply boss swap if we have a valid winner.
            // Track the actually-applied boss ID so the idempotency check in
            // PrefixContinue can verify the swap survived save-quit (StS2's save
            // snapshot may predate our mid-room mutation).
            ModelId? swappedBossId = null;
            if (winnerIndex.HasValue) {
                try {
                    var winnerEncounter = BossVoteResolver.ResolveWinner(sample, winnerIndex.Value);
                    TiLog.Info($"[SlayTheStreamer2][boss-vote] resume: applying boss swap to {winnerEncounter.Id}");
                    ApplyBossSwap(currentState, winnerEncounter);
                    swappedBossId = winnerEncounter.Id;
                } catch (Exception ex) {
                    TiLog.Error("[SlayTheStreamer2][boss-vote] ApplyBossSwap threw; preserving vanilla boss", ex);
                }
            } else {
                TiLog.Info("[SlayTheStreamer2][boss-vote] resume: no winner; preserving vanilla boss");
            }

            // Synthetic Proceed re-click. _resumeInProgress=1 makes the prefix pass through.
            // OnProceedButtonPressed is private with an NButton param (discarded); reflective
            // invoke via the cached _proceedMethod handle. Pass null for the NButton arg.
            try {
                var method = _proceedMethod.Value;
                if (method is null) {
                    TiLog.Error("[SlayTheStreamer2][boss-vote] resume: _proceedMethod is null; cannot fire synthetic Proceed");
                } else {
                    method.Invoke(room, new object?[] { null });
                }
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][boss-vote] synthetic OnProceedButtonPressed threw", ex);
            }

            // Mark this run+act as having completed its boss vote. Subsequent chest-room
            // clicks within the same run+act (Golden Compass second chest, map back-arrow)
            // are skipped by the idempotency check at the top of PrefixContinue. Set
            // regardless of swap outcome — chat had its opportunity even on no-winner.
            // swappedBossId is null when no swap applied (no-winner or ApplyBossSwap
            // threw); the idempotency check treats null as "skip re-vote" since chat
            // had its chance.
            _lastSwapRunId = currentState.Rng?.StringSeed;
            _lastSwapActIndex = currentState.CurrentActIndex;
            _lastSwappedBossId = swappedBossId;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] resume threw", ex);
        } finally {
            // Order matters: clear _resumeInProgress first, then _voteInProgress.
            _activeRoom = null;
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static void SendIgnoredResultReceipt() {
        var coordinator = Voter.Default;
        var state = coordinator?.Chat?.State;
        if (state != ChatConnectionState.ConnectedReadWrite) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] ignored-result receipt skipped: chat state is {state?.ToString() ?? "null"}");
            return;
        }
        _ = coordinator!.Chat.SendMessageAsync(
            "Vote result ignored — run abandoned during boss vote",
            OutgoingMessagePriority.Normal);
        TiLog.Info("[SlayTheStreamer2][boss-vote] ignored-result receipt queued");
    }

    /// <summary>
    /// Dev-only: re-open the current boss vote with a fresh sample from the
    /// same (run, act) but a different seed (via _rerollSalt). The stale
    /// HandleVoteAsync from the cancelled old session will see its resume
    /// land on a stale generation and bail cleanly via the guard in
    /// ResumeOnMainThread — no swap, no synthetic Proceed click.
    /// Used by RerollVoteConsoleCmd. Returns (success, message) for the
    /// console to format.
    /// </summary>
    internal static (bool ok, string message) RerollCurrent() {
        if (_voteInProgress != 1) return (false, "No active boss vote to reroll.");

        var coordinator = Voter.Default;
        if (coordinator is null) return (false, "Voter.Default is null.");

        var room = _activeRoom;
        if (room is null || !GodotObject.IsInstanceValid(room)) return (false, "Active chest room is invalid.");

        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState is null) return (false, "RunState is null.");

        var oldSession = coordinator.CurrentSession;
        if (oldSession is null) return (false, "No active vote session.");

        // Pre-flight: verify the pool will still produce a usable sample BEFORE
        // tearing down the existing session. If reroll would degenerate, leave
        // the old vote intact.
        try {
            var preflightPool = runState.Act.AllBossEncounters.ToList();
            if (runState.Act.HasSecondBoss) {
                var secondId = runState.Act.SecondBossEncounter?.Id;
                if (secondId is not null) preflightPool.RemoveAll(e => e.Id == secondId);
            }
            if (preflightPool.Count <= 1) {
                return (false, $"Pre-flight: degenerate pool (count={preflightPool.Count}); cannot reroll.");
            }
        } catch (Exception ex) {
            return (false, $"Pre-flight pool inspection threw: {ex.Message}");
        }

        // Tear down: bump generation FIRST so the cancelled session's resume
        // sees a stale gen. Then cancel.
        _rerollSalt++;
        int newGen = Interlocked.Increment(ref _voteGeneration);
        try { oldSession.Cancel(); } catch { /* swallow */ }

        // Build fresh vote with bumped salt.
        try {
            string? runIdAtStart = null;
            if (RunIdGuardEnabled) {
                try { runIdAtStart = runState.Rng?.StringSeed; } catch { /* swallow */ }
            }
            var sample = BuildAndStartVote(coordinator, room, runState, runIdAtStart, salt: _rerollSalt, gen: newGen);
            return (true, $"Rerolled boss vote (salt #{_rerollSalt}); options: {string.Join(", ", sample.Select((e, i) => $"#{i} {e.Title.GetFormattedText()}"))}");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] reroll BuildAndStartVote threw; vote is now in degraded state", ex);
            return (false, $"Reroll failed: {ex.Message}. Vote may be in degraded state; consider votenow + abandon.");
        }
    }
}
