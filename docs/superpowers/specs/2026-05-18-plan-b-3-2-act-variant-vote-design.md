# 2026-05-18 — Plan B.3.2: Act-variant vote (design)

**Date**: 2026-05-18
**Status**: Design pending meta-review. Implementation plan pending plan + approval.
**Slice**: B.3.2 (post-B.3.1) — chat picks Act 1 variant (Underdocks vs Overgrowth) at run start, before vanilla's coin-flip in `GetRandomList` would otherwise pick.
**Scope**: New Harmony patch on `StartRunLobby.BeginRunLocally`. New popup `Control` rendering vertical 50/50 split of each variant's combat-room background + entry banner. Suspend-and-resume pattern (same shape as B.1 / B.2.1 / B.3). No changes to existing voting infrastructure.

## TL;DR

Vanilla picks the Act 1 variant via a coin-flip inside [`ActModel.GetRandomList`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs#L414), called once at the start of [`StartRunLobby.BeginRunLocally:411`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L411). The next line — `list[0] = GetAct(Act1) ?? list[0]` ([line 412](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L412)) — is a vanilla override hook: if `StartRunLobby.Act1` is a known variant key (`"overgrowth"` / `"underdocks"`), it replaces the coin-flip pick. The hook exists because `NCharacterSelectScreen` has a (currently UI-hidden) `_actDropdown` that writes to this property; we use the same hook from chat instead.

**Mechanism.** Harmony prefix on `BeginRunLocally`, returns `false` to suspend. We start a 30s chat vote with two options (`#0 Overgrowth` / `#1 Underdocks`), `await session.AwaitWinnerAsync()`, then on the Godot main thread write `__instance.Act1 = winnerKey` and reflectively re-invoke `BeginRunLocally(seed, modifiers)`. Vanilla's existing line 412 picks up the chat winner via the same code path the dropdown would have used. No reimplementation of `GetRandomList` / seed / RNG.

**Net change**: ~450 LOC across 4 new files + 1 edit (settings). One new Harmony patch class, one new popup `Control`, one pure helper (`ActVariantVoteResolver`), one DTO (`ActVariantOption`). No `src/Ti/*` edits — existing generic receipt formatters work as-is.

**Risk surface**: smaller than B.3 (1 Harmony target, 1 popup, no idempotency-on-re-entry complexity since each Embark click is a fresh run-start). Two flagged uncertainties for reviewers: (1) `Act1` write-then-reinvoke pattern is unverified end-to-end, (2) combat-background and entry-banner asset paths not yet located in the decompile — research-spike during implementation with a text-only L3 fallback.

## Goals

- **Chat picks the Act 1 variant** when chat is connected and the streamer hasn't explicitly pinned a variant.
- **Reuse existing voting infrastructure** verbatim — `VoteCoordinator`, `VoteSession`, `VoteTallyLabel`, `EnglishReceipts`. Only new voting-layer addition is the receipt-string set.
- **Forward-compatible internals** — `ActVariantVotePopup` is parameterized on `IReadOnlyList<ActVariantOption>`; the candidate-pool builder is a private static method per-act. Adding an Act 2 variant vote in the future means a new Harmony patch + new candidate-pool method, not a popup rewrite.
- **Preserve TI/Game seam** — popup is MegaCrit-free at its public interface; all MegaCrit type contact lives in `ActVariantVotePatch`.
- **No regression** to existing voting features (B.1 / B.2.1 / B.2.2 / B.3 / B.3.1).

## Non-goals

- **Act 2 / Act 3 variant votes.** Vanilla has no Act 2 or Act 3 variants today. The architecture is forward-compatible (popup is generic) but no second-act patch ships in this slice. Future Act-2 variants would need ~1 day of additive work — see "Forward-compatibility notes" below.
- **Generic act-transition trigger surface.** Speculative infra for a feature MegaCrit hasn't shipped. YAGNI.
- **Multiplayer.** Singleplayer-only per Surfinite 2026-05-17.
- **Pre-act-N votes for variant pools that don't exist yet.** No Hive variants, no Glory variants, nothing to vote on.
- **Variant pool expansion (option 1 from notes/10's earlier deferral).** Different feature; deferred per memory 31663.
- **Settings UI surface** for the new toggle — `voteOnActVariant` is JSON-only for v1, matching `voteOnBoss` precedent.
- **Localized receipts** — English-only, matches B.1/B.2.1/B.3 posture.
- **Unlock gating** of the candidate pool — design under `unlock all` per [memory](../../../README.md). Both variants always shown.

## Architecture

### File layout

```
src/Game/DecisionVotes/
  ActVariantVotePatch.cs       — NEW. Harmony patch on StartRunLobby.BeginRunLocally.
                                  Suspend-and-resume, candidate-pool build, asset
                                  pre-warm. EXCLUDED from test compile.
  ActVariantVoteResolver.cs    — NEW. Pure static helpers. INCLUDED in test compile
                                  via auto-include of src/Game/DecisionVotes/**.

src/Game/Ui/
  ActVariantVotePopup.cs       — NEW. Godot Control + CanvasLayer. EXCLUDED from
                                  test compile (Godot types).
  ActVariantOption.cs          — NEW. DTO. INCLUDED in test compile via surgical
                                  <Compile Include="..\src\Game\Ui\ActVariantOption.cs" />.

src/Game/Bootstrap/Settings/
  ModSettings.cs               — EDIT. Add VoteOnActVariant : bool (default true).

(no changes to src/Ti/* — existing EnglishReceipts.FormatOpen / FormatPeriodicTally
                  / FormatClose are generic over VoteSnapshot; the slice label
                  "Act 1 variant vote" is passed at coordinator.Start time.
                  Cancellation receipt is a raw chat send, matching B.3's
                  BossVotePatch.SendIgnoredResultReceipt pattern.)
```

No changes to `src/Ti/Internal/`, `src/Ti/Voting/`, or any other voting-layer file. No new abstractions in `Ti/*`.

### TI/Game seam preserved

- [`src/Game/Ui/ActVariantOption.cs`](../../../src/Game/Ui/ActVariantOption.cs) is fully free of `MegaCrit.Sts2.*` references — it carries `(int Index, string Key, string Title, string BackgroundPath, string BannerPath)` only. No `ActModel` reference; the popup never sees MegaCrit types.
- [`src/Game/Ui/ActVariantVotePopup.cs`](../../../src/Game/Ui/ActVariantVotePopup.cs)'s **public interface** (constructor, public methods) is MegaCrit-free. Its **implementation** uses only `Godot.*` types (TextureRect, Control, CanvasLayer, ColorRect, Label, HBoxContainer) — same posture as `BossVotePopup`.
- [`src/Game/DecisionVotes/ActVariantVotePatch.cs`](../../../src/Game/DecisionVotes/ActVariantVotePatch.cs) is the only file touching `MegaCrit.Sts2.*` types (`ActModel`, `ModelDb`, `StartRunLobby`, `UnlockState`).
- [`src/Game/DecisionVotes/ActVariantVoteResolver.cs`](../../../src/Game/DecisionVotes/ActVariantVoteResolver.cs) is pure CLR — no Godot, no MegaCrit. Testable in `Microsoft.NET.Sdk` with no DLL resolution.

### Vanilla API surface

Verified against the decompile of `sts2.dll`:

- [`StartRunLobby.BeginRunLocally(string seed, List<ModifierModel> modifiers)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L408) — `private void` instance method. Harmony target via `AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally", new[] { typeof(string), typeof(List<ModifierModel>) })`.
- [`StartRunLobby.Act1`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L87) — `public string` get/set property, default `"random"`. Our write target.
- [`StartRunLobby.GetAct(string)`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs#L441) — `private static`. Decodes `"overgrowth"` → `ModelDb.Act<Overgrowth>()`, `"underdocks"` → `ModelDb.Act<Underdocks>()`, anything else → `null`. We rely on it via line 412; we don't call it directly.
- [`ModelDb.Act<T>()`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs) — `public static`. Returns the canonical `ActModel` for a type. Used to discover candidates' assets (combat-bg + entry-banner paths) without iterating `ActModel.AssetPaths` (which only covers the current variant and has canonical-state landmines per B.3.1's findings).
- `PreloadManager.Cache.GetTexture(string path)` — discarded-return cache prime. Used in pre-warm.

### Hidden dropdown — the override hook origin

`NCharacterSelectScreen` declares a private `_actDropdown : NActDropdown` field ([line 190](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs#L190)) which writes to `_lobby.Act1` ([line 484](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs#L484)) when populated. The control is NOT surfaced to players in the current build (verified by Surfinite 2026-05-17). Our patch uses the SAME `Act1` property the dropdown would have written to — vanilla's override mechanism, just driven by chat instead of UI. If MegaCrit ever surfaces the dropdown, our custom-mode-pin guard (bail condition X below) respects an explicit player pick.

## Trigger mechanics — suspend-and-resume

### Bail order (Prefix)

Each condition that returns `true` lets vanilla through unchanged; `false` suppresses the click (B.3 spam-Embark precedent).

```csharp
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally",
              new[] { typeof(string), typeof(List<ModifierModel>) })]
internal static class ActVariantVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;

    private static readonly Lazy<MethodInfo?> _beginRunLocallyMethod =
        new(() => AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally",
                                     new[] { typeof(string), typeof(List<ModifierModel>) }));

    static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers) {
        // 1. Synthetic resume → let vanilla through with chat-set Act1.
        if (_resumeInProgress == 1) return true;

        // 2. Settings toggle off.
        if (!ModSettings.Current.VoteOnActVariant) return true;

        // 3. Chat unreadable.
        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                        or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][act-variant-vote] chat not readable (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        // 4. Custom-mode pin (X policy). Respect explicit dropdown choice.
        if (__instance.Act1 != "random") {
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] Act1 explicitly pinned ({__instance.Act1}); skipping vote");
            return true;
        }

        // 5. Pool degeneracy (defensive — pool is always 2 today).
        var candidates = ActVariantVoteResolver.BuildCandidates();
        if (candidates.Count <= 1) {
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] degenerate pool (count={candidates.Count}); bailing to vanilla");
            return true;
        }

        // 6. Concurrent-vote guard (atomic).
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        return PrefixContinue(__instance, seed, modifiers, candidates, coordinator);
    }
}
```

No multiplayer bail — singleplayer-only per non-goals. (Future polish if multiplayer support resumes: add the `RunManager.Instance?.DebugOnlyGetState()?.Players?.Count > 1` check pattern from `BossVotePatch.TryGetPlayerCount`.)

No `RunIdGuardEnabled` analog — there is no run at this point. The state we care about (the `StartRunLobby` instance) is the `__instance` argument itself; we don't need to capture+compare across the async boundary because nothing about `__instance` changes between Embark click and re-invoke. (Defensive check: `GodotObject.IsInstanceValid(__instance)` before re-invoke, in `ResumeOnMainThread`.)

### Suspend-and-resume shape

```csharp
private static bool PrefixContinue(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> modifiers,
        IReadOnlyList<ActVariantOption> candidates,
        VoteCoordinator coordinator) {
    try {
        // Pre-warm BOTH variants' assets on the main thread before the popup opens.
        // ActModel.AssetPaths only covers the current variant and has canonical-state
        // landmines (B.3.1 finding); we build paths directly via SceneHelper /
        // ImageHelper, NOT via accessor enumeration.
        PreWarmAssets(candidates);

        var labels = candidates.Select(c => c.Title).ToList();
        var session = coordinator.Start("Act 1 variant vote", labels, TimeSpan.FromSeconds(30));

        var popup = new ActVariantVotePopup(
            options: candidates,
            session: session,
            dispatcher: coordinator.Dispatcher,
            isCancelled: () => !GodotObject.IsInstanceValid(instance));
        coordinator.Dispatcher.Post(() => popup.Show());

        _ = HandleVoteAsync(instance, seed, modifiers, session, candidates, coordinator);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; bailing to vanilla", ex);
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
    return false;  // suspend vanilla
}

private static async Task HandleVoteAsync(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> modifiers,
        VoteSession session,
        IReadOnlyList<ActVariantOption> candidates,
        VoteCoordinator coordinator) {
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

        int? winnerIndex = null;
        try {
            int idx = await session.AwaitWinnerAsync();
            if (idx >= 0 && idx < candidates.Count) winnerIndex = idx;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync threw; no override", ex);
        }

        string winnerKey = ActVariantVoteResolver.ResolveWinnerKey(candidates, winnerIndex);
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: dispatching with winnerKey={winnerKey}");
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(instance, seed, modifiers, winnerKey));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(instance, seed, modifiers, "random"));
    }
}

private static void ResumeOnMainThread(
        StartRunLobby instance,
        string seed,
        List<ModifierModel> modifiers,
        string winnerKey) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    try {
        if (!GodotObject.IsInstanceValid(instance)) {
            TiLog.Warn("[SlayTheStreamer2][act-variant-vote] resume: StartRunLobby instance no longer valid; aborting (run abandoned)");
            SendCancellationReceipt();
            return;
        }

        // Apply winner. "random" means no-winner fallback — leave Act1 alone,
        // vanilla's GetRandomList coin-flip will stand at line 412.
        if (winnerKey != "random") {
            instance.Act1 = winnerKey;
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Act1 = {winnerKey}");
        } else {
            TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: no winner; preserving vanilla random pick");
        }

        var method = _beginRunLocallyMethod.Value;
        if (method is null) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume: _beginRunLocallyMethod is null; cannot re-invoke");
            return;
        }
        method.Invoke(instance, new object?[] { seed, modifiers });
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
    } finally {
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}
```

### Cancellation

The popup polls `isCancelled` each frame (mirrors B.3's `IsRunDying` pattern). If the streamer navigates back to character-select (ESC / character-button click), `__instance` becomes invalid; the probe returns `true`; popup calls `session.Cancel()`; `AwaitWinnerAsync` returns `-1` (or throws — both lead to `winnerKey = "random"` in the resolver); `ResumeOnMainThread`'s validity check fails and aborts without re-invoking. A cancellation receipt (`"Act 1 variant vote cancelled — streamer navigated back."`) is sent via `SendCancellationReceipt()`.

No idempotency marker required (unlike B.3's `_lastSwapRunId` / `_lastSwapActIndex`): each Embark click is a fresh run-start; there's no "Golden Compass / back-arrow re-fire" path because there's no run yet. The atomic `_voteInProgress` is sufficient.

## Candidate pool

Hardcoded 2-element list, no unlock gating (per Q5e clarification — design under `unlock all`, matches B.3's "don't engineer around progression" philosophy).

```csharp
// ActVariantVoteResolver.cs
internal static class ActVariantVoteResolver {
    internal static IReadOnlyList<ActVariantOption> BuildCandidates() {
        return new[] {
            new ActVariantOption(
                Index: 0,
                Key: "overgrowth",
                Title: "Overgrowth",
                BackgroundPath: AssetPaths.OvergrowthCombatBackground,
                BannerPath: AssetPaths.OvergrowthEntryBanner),
            new ActVariantOption(
                Index: 1,
                Key: "underdocks",
                Title: "Underdocks",
                BackgroundPath: AssetPaths.UnderdocksCombatBackground,
                BannerPath: AssetPaths.UnderdocksEntryBanner),
        };
    }

    internal static string ResolveWinnerKey(IReadOnlyList<ActVariantOption> options, int? winnerIndex) {
        if (winnerIndex is null) return "random";
        if (winnerIndex < 0 || winnerIndex >= options.Count) return "random";
        return options[winnerIndex.Value].Key;
    }
}
```

`AssetPaths` is a static-readonly holder for the 4 paths (populated by the asset-discovery spike — see next section). `Key` strings are lowercase to match vanilla's `GetAct` decoder.

`Title` is hardcoded English. `ActModel` has no `Title` property in the decompile; localization is out of scope.

## Asset discovery — research spike

The popup needs four assets, paths NOT yet located in the decompile:

1. **Overgrowth combat-room background** — the bg you see during combat in an Overgrowth run.
2. **Underdocks combat-room background** — same, for Underdocks.
3. **Overgrowth entry banner** — the "Overgrowth" graphic shown at act start.
4. **Underdocks entry banner** — same, for Underdocks.

### Spike deliverables (first task of the implementation plan)

- 4 verified `res://...` paths (or `null` per asset if not located cleanly).
- Verification per asset: `ResourceLoader.Exists(path) == true`, asset loads via `PreloadManager.Cache.GetTexture(path)` without warning.
- **Banner anchor convention verified** (per B.3.1's "bounds-aware centering" lesson): the entry banner's natural anchor (top-center, center, top-left, etc.) needs verification before locking the `screen_height / 3` overlay position. If the banner's natural anchor is top-center, place at `Vector2(half_width / 2, screen_height / 3)`. If it's center, adjust.

### Path-building approach

Direct construction via `SceneHelper.GetScenePath(...)` / `ImageHelper.GetImagePath(...)` — **NOT** via `ActModel.AssetPaths` enumeration. Per B.3.1's findings:
- `ActModel.AssetPaths` only covers the current variant's resources; using it for both variants at run-start (when no variant is active) doesn't apply.
- Accessor-property enumeration on canonical model instances can hit `"Canonical model ... used in incorrect place"` (the `MonsterModel.AssetPaths` landmine generalizes to model-asset accessors with mutable-state side-effects).

The decompile's `Overgrowth.cs` and `Underdocks.cs` (the two `ActModel` subclasses) reference `ChestSpineResourcePath` and `MapTraveledColor` / `MapUntraveledColor` / `MapBgColor` but no `CombatBackgroundPath`. Combat-room background is likely on:
- An act-keyed scene resource under `res://scenes/rooms/combat/<act-id>.tscn` or similar.
- A constant in `CombatRoom` / `NCombatRoom` keyed off `runState.Act.Id.Entry.ToLowerInvariant()`.
- A field on the room model `_rooms.normalEncounters[0]` scene tree.

Entry banner is likely under `res://images/ui/act_intros/` or `res://animations/act_intros/`.

### L3 fallback

If 1+ of the 4 assets is not locatable, popup degrades to text-only:
- Per column: full-height `ColorRect` with the variant's `MapBgColor` (already on `ActModel`) as a backdrop, plus a centered `Label` with the variant title in a large font.
- Per-column tally label preserved.
- Banner overlay skipped.
- The popup widget code is identical; only `BackgroundPath` / `BannerPath` being null are handled at `Show()` time.

L3 is graceful enough that asset-spike failure is not a project-blocker.

## Pre-warm — BOTH variants

```csharp
private static void PreWarmAssets(IReadOnlyList<ActVariantOption> candidates) {
    var sw = Stopwatch.StartNew();
    int succeeded = 0;
    int total = 0;
    foreach (var option in candidates) {
        foreach (var path in new[] { option.BackgroundPath, option.BannerPath }) {
            if (string.IsNullOrEmpty(path)) continue;
            total++;
            try {
                _ = PreloadManager.Cache.GetTexture(path);
                succeeded++;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} ({path}): {ex.Message}");
            }
        }
    }
    sw.Stop();
    TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms");
}
```

Matches B.3.1's pre-warm telemetry pattern. Sync main-thread call (Cache.GetTexture is verified synchronous, same as `Cache.GetScene` used in B.3.1). Per-asset try/catch — one missing texture doesn't block the others.

Expected envelope: ≤ 100ms on baseline hardware (4 textures vs B.3.1's 3 monster scenes which clocked 76–82ms).

## Popup UI structure

```
CanvasLayer (layer = 100, owned by popup)
├── ColorRect (full screen, Color(0, 0, 0, 0.6), mouse_filter = Stop)   ← backdrop
└── HBoxContainer (full screen, separation = 0)
    ├── PanelContainer (50% width, 100% height, clip_contents = true)    ← column #0
    │   ├── TextureRect (background, stretch_mode = KeepCenter,
    │   │                anchor full-rect)                               ← native size, center-cropped
    │   ├── TextureRect (banner, anchor top-center,
    │   │                offset_top = screen_height / 3)                 ← native scale
    │   └── Label (tally, anchor bottom-center, MarginBottom = 80)
    └── PanelContainer (50% width, 100% height, clip_contents = true)    ← column #1
        └── (same shape)
```

### Sizing

- `screen_height / 3` is computed once at `Show()` from `GetViewportRect().Size.Y`. The popup doesn't reflow on viewport resize — matches B.3 / B.3.1 posture. If reflow becomes a regression target, it's a one-line `_Process` recompute.
- `clip_contents = true` + `stretch_mode = KeepCenter` is the native-size center-crop. The TextureRect is allowed to render at the texture's full native pixel size; the PanelContainer's half-screen width clips the left and right edges. This implements Surfinite's "render at the same size used for combat backgrounds, just center and cut off the left and right edges" requirement directly.
- Tally label font matches B.3's `VoteTallyLabel` styling for cross-vote consistency.

### Lifecycle

- `Show()` instantiates the CanvasLayer + child tree, adds to `SceneTree.Root`, subscribes to `session.TallyChanged`, starts `_Process` polling for `isCancelled`.
- `OnTally(snapshot)` updates `_leftTally.Text` / `_rightTally.Text` to `"#{i} — {count} votes"`.
- `session.Closed` event fires → `QueueFree` the CanvasLayer, unsubscribe.

### Occlusion freeze (deferred)

B.3.1's `ProcessMode.Disabled` cascade is not applied to B.3.2's v1 — backgrounds and banners are static textures. If asset spike turns up animated banners (Spine resources) and we want pause-aware freeze, the pattern is available via the B.3.1 reference impl. Documented as future polish.

## Settings

```jsonc
{
  // ... existing settings ...
  "voteOnActVariant": true  // default true; toggles the entire B.3.2 patch
}
```

Adding the field to `ModSettings.cs`:
```csharp
public bool VoteOnActVariant { get; init; } = true;
```

No schema-version bump (optional field with default, matches B.2.2 / B.3 precedent).

## Receipts

**No `EnglishReceipts.cs` edits.** The existing `FormatOpen` / `FormatPeriodicTally` / `FormatClose` formatters are generic over `VoteSnapshot` — they derive all wording from `s.Label`, `s.Options`, `s.Tallies`, `s.WinnerIndex`, `s.NoVotesReceived`, `s.RandomTieAmong`, `s.TimeRemaining`. The slice label `"Act 1 variant vote"` is passed in at `coordinator.Start("Act 1 variant vote", labels, 30s)` and flows through to every receipt automatically.

What chat sees, given the existing formatters:
- **Open** (via `FormatOpen`): `"Vote [NN]: Act 1 variant vote! Type 0, 1 — 30s left."`
- **Periodic tally** (via `FormatPeriodicTally`): `"Vote: 0=5 1=3, 22s left."`
- **Close — winner** (via `FormatClose`): `"Chat chose 0: Overgrowth."`
- **Close — tie** (via `FormatClose`): `"Tie between 0 Overgrowth and 1 Underdocks — chat chose 0: Overgrowth randomly."`
- **Close — no votes** (via `FormatClose`): `"No votes received — chat got 0: Overgrowth randomly."` (vanilla pick is unaffected — see ResumeOnMainThread; `"random"` means vote produced no override, vanilla pick stands; the receipt phrasing inherited from the generic formatter is slightly misleading here but is the existing convention).

**Cancellation receipt** is a raw send via `coordinator.Chat.SendMessageAsync(...)`, matching `BossVotePatch.SendIgnoredResultReceipt`:
- `"Act 1 variant vote cancelled — run-start abandoned."`

Receipt-policy cadence reuses `VoteReceiptPolicy.Default`. Periodic-tally dedup is on structural tally state (not rendered text) — invariant from CLAUDE.md Tier 1 and untouched by this slice.

## Forward-compatibility notes (not built today)

For when MegaCrit ships Act 2 variants:

1. **New Harmony patch**: prefix some Act-2-transition point (likely `IncrementActIndex` or an act-entry hook). Mirror `ActVariantVotePatch`'s structure.
2. **New candidate-pool builder**: e.g., `BuildAct2Candidates()` — returns `[Hive, HiveAlternate, ...]` as `ActVariantOption` DTOs.
3. **Mutate `runState.Acts[1]`**: harder than Act 1 because at run-start vanilla has already baked `Acts[1]` AND already called `GenerateRooms` on it. Need to:
   - Replace `Acts[1]` with the chat winner.
   - Re-run `GenerateRooms` on the new variant.
   - Preserve the shared-ancient subset from `RunManager.cs:483-489`.
4. **Popup reuse**: zero changes — `ActVariantVotePopup` takes `IReadOnlyList<ActVariantOption>` and renders N columns via the HBoxContainer. Pre-warm + resolver are also generic.

Estimated additional work: ~1 day (one new patch class + state-fixup) when MegaCrit ships Act 2 variants. Not built today (YAGNI).

## Test architecture

### Test csproj edits

`tests/slay_the_streamer_2.tests.csproj` already auto-includes `..\src\Game\DecisionVotes\**\*.cs`. `ActVariantVoteResolver.cs` is picked up automatically.

`ActVariantOption.cs` in `src/Game/Ui/` is NOT auto-included (per CLAUDE.md Tier 1: test csproj source-include globs are explicit; `src/Game/Ui/*` is not glob-included). Surgical include required:

```xml
<Compile Include="..\src\Game\Ui\ActVariantOption.cs" />
```

### Test classes

All carry `[Collection("TiLog.Sink")]` per CLAUDE.md Tier 1 (any class triggering `TiLog.*` must be in the sink-isolation collection).

- **`ActVariantVoteResolverTests`** — winner-index → key mapping:
  - `ResolveWinnerKey(options, 0) → "overgrowth"`
  - `ResolveWinnerKey(options, 1) → "underdocks"`
  - `ResolveWinnerKey(options, null) → "random"`
  - `ResolveWinnerKey(options, -1) → "random"`
  - `ResolveWinnerKey(options, 2) → "random"`
- **`ActVariantVoteCandidatesTests`** — `BuildCandidates()` invariants:
  - Returns exactly 2 entries.
  - `[0].Key == "overgrowth"`, `[1].Key == "underdocks"` (lowercase).
  - `[0].Index == 0`, `[1].Index == 1`.
  - Asset path fields are non-null (test against a fixture that sets them, since `AssetPaths` constants come from the spike).
- **`ActVariantVoteReceiptsTests`** — receipt strings for open / tally / close-winner / close-tie / close-no-votes / cancellation. Uses `VoteSessionTestBase.CreateCoordinator(...)` per CLAUDE.md Tier 2.

`ActVariantVotePatch.cs` and `ActVariantVotePopup.cs` are NOT in test compile — Harmony attributes and Godot types respectively.

## Operator-validation gates

11 gates total. Gate 11 added per the B.3.1-session memo (save-quit preservation).

| # | Gate | How verified |
|---|------|--------------|
| 1 | Vote fires on Embark click | Chat connected, click Embark → popup appears, character-select frozen behind. `godot.log` shows `[act-variant-vote] opening vote`. |
| 2 | Winner applied | Vote `#0` or `#1` → run starts with chat-chosen variant. Verify via first combat room (enemy set matches the variant's `GenerateAllEncounters`). |
| 3 | No-winner fallback | Chat silent → vote times out at 30s → run starts with vanilla's seed-deterministic pick. `godot.log` shows `[act-variant-vote] no winner; preserving vanilla random pick`. |
| 4 | Settings toggle off | `voteOnActVariant: false` in settings → no vote fires, vanilla flow unchanged. |
| 5 | Pool degeneracy guard | Defensive — not exercisable in current vanilla (pool is always 2). Smoke-only: `[act-variant-vote] degenerate pool` must NOT appear in normal play. |
| 6 | Cancellation | Embark, vote starts, ESC back to character-select → popup tears down, chat receives cancellation receipt. |
| 7 | Spam-Embark guard | Click Embark twice in quick succession → second click suppressed (`[act-variant-vote] repeat click; suppressed` in log). |
| 8 | Pre-warm telemetry | `godot.log` shows `[act-variant-vote] pre-warm: 4/4 assets in Nms` (or 0/0 + 1/2 etc. if L3 fallback active) before the open receipt. Envelope ≤ 100ms. |
| 9 | Sealed Deck modifier coexistence | Start run with Sealed Deck modifier active → vote still fires (Sealed Deck is in `_settings.Modifiers`, orthogonal to `Act1`). Run proceeds into Sealed Deck flow with chat-chosen variant. |
| 10 | Receipt delivery | Open + at-least-one periodic-tally + close receipts all arrive in chat. |
| 11 | Save-quit preservation | Start run with chat-chosen variant. After entering first combat, save-quit and Continue from main menu. Verify Act 1 variant is preserved (combat-bg matches the chat pick, not vanilla's seed-deterministic pick). |

**Operator-validation gate for bail condition X (`Act1 != "random"`) is un-testable in the current build** because the dropdown is UI-hidden — there's no in-game way to set `Act1` to a non-`"random"` value. The bail path itself is unit-testable via the Prefix logic (assert that `__instance.Act1 = "overgrowth"` → Prefix returns `true`, no vote fires). In-game validation deferred until MegaCrit surfaces the dropdown.

## Open items / risks

1. **`Act1` write-then-reinvoke approach unverified end-to-end.** Reviewers: please check whether vanilla reads `Act1` only once at `StartRunLobby.cs:412` or has other dependencies. The decompile shows one read site; nothing in `RunState` / `RunManager` references `Act1` post-construction. Risk: low, but worth a second pair of eyes.
2. **Asset paths not yet located in decompile.** Deliberate research-spike during implementation. L3 fallback (text-only label) is graceful. Risk: low (mitigated by fallback), high uncertainty.
3. **Save-quit serialization stability of `runState.Acts[0]`.** Per `RunState.cs:373-376` save uses `runState.Acts.Zip(save.Acts, …).ToSave()` — should be stable, but smoke-tested via Gate 11.
4. **Banner anchor convention not yet verified.** Asset-spike deliverable. Risk: low (alignment is one-pixel-tweakable post-discovery), informational.

## Cross-references

- [memory: plan-b-3-2-design-checkpoint](../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/plan_b_3_2_design_checkpoint.md) — locked-in decisions from brainstorming + memo.
- [memory: sts2-act-dropdown-hidden](../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_act_dropdown_hidden.md) — `_actDropdown` / `Act1` finding.
- [`BossVotePatch.cs`](../../../src/Game/DecisionVotes/BossVotePatch.cs) — suspend-and-resume reference implementation (B.3).
- [`BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs) — popup reference implementation (B.3 / B.3.1).
- [notes/10-boss-vote-feasibility.md](../../../notes/10-boss-vote-feasibility.md) — predecessor research for B.3.
- B.3.1 design: [2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md](2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md).
- [`StartRunLobby.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs) — patch target file.
- [`ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs) — variant selection logic.
- [`NCharacterSelectScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs) — hidden dropdown origin.
