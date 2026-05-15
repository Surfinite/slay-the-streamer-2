# 2026-05-15 — Plan B.3.1: Combat-idle boss portraits (design)

**Date**: 2026-05-15
**Status**: Design pending user approval. Implementation pending plan + approval.
**Slice**: B.3.1 (post-B.3 polish) — fixes the empty-column bug for Spine-only bosses in the boss-vote popup.
**Scope**: Replace the `BossVotePopup`'s per-column PNG portrait (loaded from `EncounterModel.BossNodePath + ".png"`) with an animated combat-idle portrait rendered via `MonsterModel.CreateVisuals()`, modeled on `NBestiary`'s instantiation pattern.

## TL;DR

The shipped B.3 popup loads boss portraits from `BossNodePath + ".png"` ([`BossVotePatch.cs:341-352`](../../../src/Game/DecisionVotes/BossVotePatch.cs#L341-L352)). That works for "placeholder" bosses that explicitly ship a PNG in `res://images/map/placeholder/`, but **Spine-only bosses ship no PNG fallback** (the `.tres` skeleton is the only on-disk asset). Ceremonial Beast is the current example; as MegaCrit ships more proper Spine art, the asymmetry worsens.

`MonsterModel.CreateVisuals()` is a public one-call API ([`MonsterModel.cs:238`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L238), verified `public` in stable `sts2.dll` 2026-05-15) that returns a fully-wired `NCreatureVisuals` Node2D — the same node combat uses to render the boss during fights. `NBestiary.SelectMonster` ([`NBestiary.cs:208-276`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Bestiary/NBestiary.cs#L208-L276)) is a working reference for rendering this node outside combat in a `NSubmenu` context — exactly analogous to our popup. The combat infrastructure is fully present in stable; only the Bestiary UI screen is beta-gated.

Net change: ~50 LOC across 3 files. `BossVotePopupOption` swaps `PortraitPath` for a `Func<Node2D>? VisualsFactory`. `BossVotePatch` builds the factory closures, pre-warms the asset cache for each candidate (coding for slower hardware), and drops the now-dead `ResolvePortraitPath` helper. `BossVotePopup` swaps the `TextureRect` slot for a sized `Control` that parents the factory-produced Node2D, fits to size, and freezes Spine playback when occluded.

## Goals

- **Fix the empty-column bug** for Spine-only bosses (Ceremonial Beast today; more bosses as Spine art ships).
- **Future-proof against placeholder→Spine migration** — every monster ships a combat scene because combat needs it; the asymmetry stops being a moving target.
- **Pre-warm the asset cache** at vote-start so first-popup-of-run doesn't hitch on slower hardware.
- **Preserve TI/Game seam** — `BossVotePopup` and `BossVotePopupOption` stay MegaCrit-free; all MegaCrit type contact lives in `BossVotePatch`.
- **No regression** for placeholder bosses (Soul Fysh, Vantom, The Kin, Lagavulin Matriarch, Waterfall Giant, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject).

## Non-goals

- **Map-tier Spine rendering** of `BossNodeSpineResource` via `NSpineAutoPlayer` — the alternative path discussed in feasibility. Combat-idle gives higher fidelity (full colored sprite vs map silhouette) for the same effort, and avoids untested manual `SpineSprite` instantiation.
- **Multi-monster encounter handling** — pick-first rule documented. All current act-boss encounters are single-monster (verified across `CeremonialBeastBoss`, `DoormakerBoss`, `SoulFyshBoss`, etc.). Multi-monster boss support is YAGNI for v1.
- **Settings toggle** for opting back into PNG portraits — YAGNI for v1.
- **SubViewport-based column rendering** — defer to polish if column-overlap is observed in operator validation. `ClipContents = true` + `fit ≤ 1.0` cap should prevent it.
- **MonsterModel.ToMutable** — Bestiary uses canonical instances and works; we follow the same pattern.
- **Full multi-LLM meta-review** — change surface is small; normal PR self-review at integration time.

## Architecture

Three files change; no new abstractions are introduced.

### TI/Game seam preserved (at the interface)

- [`src/Game/Ui/BossVotePopupOption.cs`](../../../src/Game/Ui/BossVotePopupOption.cs) remains fully free of `MegaCrit.Sts2.*` references — the DTO's only new field is `Func<Node2D>? VisualsFactory`, a delegate type from `System` + `Godot` only.
- [`src/Game/Ui/BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs)'s **public interface** (constructor signature, public methods) stays MegaCrit-free. Its **implementation** adds one `private readonly List<NCreatureVisuals> _visualsByColumn` field — see the code-change section for the rationale (Spine-timescale freeze requires holding the typed reference). This is the minimum-acceptable concession; nothing else in `BossVotePopup` touches MegaCrit types.
- The factory's body (in `BossVotePatch`) is the only other place that touches `MonsterModel` / `EncounterModel`.
- This preserves future TI-extraction viability per the [TI extraction goal](../../../README.md) constraint: a future Twitch-only base-mod fork would need to replace `NCreatureVisuals` with whatever the new host game's equivalent is, but the change is contained to one private field and one `_Process` block.

### Vanilla API surface

Verified against the stable `sts2.dll` (`src/sts2.dll`, `LastWriteTime: 2026-05-15`):

- `public NCreatureVisuals MonsterModel.CreateVisuals()` — main entry point.
- `private NCreatureVisuals MonsterModel.CreateFallbackVisuals()` — internal fallback when the per-monster scene fails to load. Vanilla's safety net; we trust it.
- `protected virtual string MonsterModel.VisualsPath` — not directly accessible from `BossVotePatch`. Access via `monster.AssetPaths.First()` which is `public` and lists `VisualsPath` as its first entry ([`MonsterModel.cs:68-85`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L68-L85)).
- `PreloadManager.Cache.GetScene(string path)` — discarded-return cache prime.
- `public bool NCreatureVisuals.HasSpineAnimation` — gates animator wiring.
- `public Control NCreatureVisuals.Bounds` — used for fit-scale calculation.
- `public void NCreatureVisuals.SetScaleAndHue(float scale, float hue)` — applies scale; we pass `hue = 0f` (no hue shift).
- `public IEnumerable<MonsterModel> EncounterModel.AllPossibleMonsters` — abstract on base, single-monster `ReadOnlySingleElementList` on all current boss subclasses.

## Code changes

### `src/Game/Ui/BossVotePopupOption.cs`

DTO field swap:

```csharp
internal sealed record BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory);
```

Was: `string? PortraitPath`. Factory is invoked at popup `Show()` time on the main thread; returns the wired-up `NCreatureVisuals` cast to `Node2D` so this file stays MegaCrit-free.

### `src/Game/DecisionVotes/BossVotePatch.cs`

Three changes:

**(a) New pre-warm helper** — called synchronously on the main thread after candidate sampling, before session start. The (potentially perceptible) cold-load hitch lands here, between the streamer's Proceed click and the popup appearing — the natural moment for a load hitch, and doesn't eat into the visible 30s vote timer:

```csharp
private static void PreWarmBossVisuals(IEnumerable<EncounterModel> candidates) {
    foreach (var encounter in candidates) {
        try {
            var monster = encounter.AllPossibleMonsters.FirstOrDefault();
            if (monster is null) continue;
            var scenePath = monster.AssetPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(scenePath)) continue;
            _ = PreloadManager.Cache.GetScene(scenePath);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] preload failed for {encounter.Id?.Entry}: {ex.Message}");
        }
    }
}
```

**(b) New factory builder** — returns a closure capturing the canonical `MonsterModel`. Invoked once per column at popup `Show()` time:

```csharp
private static Func<Node2D>? BuildVisualsFactory(EncounterModel encounter) {
    var monster = encounter.AllPossibleMonsters.FirstOrDefault();
    if (monster is null) return null;
    return () => {
        var visuals = monster.CreateVisuals();
        if (visuals.HasSpineAnimation) {
            monster.GenerateAnimator(visuals.SpineBody);
            visuals.SetUpSkin(monster);
            visuals.SpineBody.GetAnimationState().SetAnimation("idle_loop");
        }
        return visuals;
    };
}
```

`idle_loop` is the canonical idle-animation ID — every `MonsterModel.GenerateAnimator` overrides start with `new AnimState("idle_loop", isLooping: true)` ([`MonsterModel.cs:387`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L387); confirmed for Ceremonial Beast at [`CeremonialBeast.cs:247`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Monsters/CeremonialBeast.cs#L247)).

**(c) Updated DTO construction site** — call `PreWarmBossVisuals` after sampling, build factories instead of resolving PNG paths:

```csharp
// after sampling candidates, before session start:
PreWarmBossVisuals(sample);

var dtos = sample.Select((e, i) => new BossVotePopupOption(
    Index: i,
    Title: e.Title.GetFormattedText(),
    VisualsFactory: BuildVisualsFactory(e))).ToList();
```

**Deleted**: `ResolvePortraitPath` ([`BossVotePatch.cs:341-352`](../../../src/Game/DecisionVotes/BossVotePatch.cs#L341-L352)) — now dead code.

### `src/Game/Ui/BossVotePopup.cs`

Three changes:

**(a) New field** for tracking visuals per column (needed for the Spine-timescale freeze):

```csharp
private readonly List<NCreatureVisuals> _visualsByColumn = new();
```

`NCreatureVisuals` is a MegaCrit type; this field is private and never escapes the class. See the Architecture section's seam discussion for the rationale. If even this implementation-side reference is considered a seam violation, the alternative is a wrapper-class with `Pause()` / `Resume()` methods — recommend accepting the private-field concession instead of adding machinery to solve a stylistic concern.

**(b) Column build** — replace the `TextureRect` block (currently [`BossVotePopup.cs:134-158`](../../../src/Game/Ui/BossVotePopup.cs#L134-L158)) with:

```csharp
var slot = new Control {
    Name = "PortraitSlot",
    CustomMinimumSize = new Vector2(256, 256),
    ClipContents = true,
};
col.AddChild(slot);

if (opt.VisualsFactory is not null) {
    try {
        var visuals = opt.VisualsFactory.Invoke();
        slot.AddChild(visuals);
        var fit = PortraitFit.ComputeFitScale(visuals.Bounds.Size, slot.CustomMinimumSize);
        visuals.SetScaleAndHue(fit, 0f);
        visuals.Position = slot.CustomMinimumSize * 0.5f;
        _visualsByColumn.Add(visuals);
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] visuals factory threw for column {opt.Index}: {ex.Message}");
    }
}
```

**(c) Occlusion handler** in `_Process` — extend the existing occlusion block (currently [`BossVotePopup.cs:211-219`](../../../src/Game/Ui/BossVotePopup.cs#L211-L219)) to also freeze Spine playback:

```csharp
if (_canvasLayer is not null) {
    bool occluded = false;
    try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
    catch { /* probe must never crash _Process */ }
    if (_canvasLayer.Visible == occluded) {
        _canvasLayer.Visible = !occluded;
        foreach (var v in _visualsByColumn) {
            v.SpineBody?.GetAnimationState().SetTimeScale(occluded ? 0f : 1f);
        }
    }
    if (occluded) return;
}
```

Per CLAUDE.md Tier 4: `SceneTree.Paused` is never toggled by StS2's pause menu, so Godot's native `ProcessMode` mechanism cannot drive this freeze. The `_isOccludingOverlayVisible` probe (which already detects pause menu via `NRun.Instance.GlobalUi.SubmenuStack.Stack.SubmenusOpen`) is the correct trigger.

### New file: `src/Game/Ui/PortraitFit.cs`

Pure-math helper extracted for unit-testability:

```csharp
using Godot;

namespace SlayTheStreamer2.Game.Ui;

internal static class PortraitFit {
    /// <summary>
    /// Computes a uniform scale factor to fit a sprite of <paramref name="boundsSize"/>
    /// inside a slot of <paramref name="slotSize"/>, never upscaling past native size.
    /// Defensive against zero or negative bounds (returns 1.0).
    /// </summary>
    public static float ComputeFitScale(Vector2 boundsSize, Vector2 slotSize) {
        var fit = Mathf.Min(
            slotSize.X / Mathf.Max(boundsSize.X, 1f),
            slotSize.Y / Mathf.Max(boundsSize.Y, 1f));
        return Mathf.Min(fit, 1f);
    }
}
```

## Data flow & lifecycle

```
1. Streamer clicks Proceed in chest room.
2. NTreasureRoom.OnProceedButtonPressed prefix returns false (B.3 existing).
3. HandleVoteAsync starts on main thread (B.3 existing):
   a. Sample 3 candidates from runState.Act.AllBossEncounters.
   b. PreWarmBossVisuals(sample)  ← NEW: synchronous cache prime
      └─ for each candidate: PreloadManager.Cache.GetScene(monster.AssetPaths.First())
      └─ cold-load hitch lands here (chest scene still visible).
   c. Build DTOs with VisualsFactory closures.
   d. Start session, construct popup, call popup.Show() on main thread.
4. BossVotePopup.Show():
   a. Build CanvasLayer + backdrop + content/columns (existing).
   b. For each column: invoke opt.VisualsFactory()  ← NEW: factory runs here
      └─ monster.CreateVisuals() returns NCreatureVisuals (Node2D)
      └─ wire animator + skin + idle_loop animation (if HasSpineAnimation)
      └─ parent under slot Control, fit-scale, center-position
      └─ append to _visualsByColumn for occlusion-freeze tracking.
   c. Subscribe Closed/Cancelled handlers, AddChild to SceneTree.Root.
5. _Process polls timer/tally (existing). On occlusion change:
   a. Toggle _canvasLayer.Visible (existing).
   b. Toggle Spine timescale per column  ← NEW.
6. Vote closes → session.Closed → dispatcher.Post(SafeQueueFree) →
   _canvasLayer.QueueFree() → Godot cascade frees all column children
   including NCreatureVisuals Node2Ds.
7. Resume: BossVotePatch posts ResumeOnMainThread (B.3 existing).
```

### Lifecycle ownership

- `NCreatureVisuals` instances are created lazily by the factory at popup `Show()` time.
- Owned by the `slot` Control's child list → column `VBoxContainer` → content `VBoxContainer` → `_canvasLayer`.
- Freed implicitly via Godot's parent-child cascade when `_canvasLayer.QueueFree()` runs. Each `NCreatureVisuals._ExitTree()` disconnects its own `NGame.PhobiaModeToggled` signal.
- **Factory-never-invoked case**: if the popup is constructed but `Show()` is never called (e.g., `_isRunDying` triggers `Cancel()` between popup construction and `Show()`), no `NCreatureVisuals` instances exist yet — there's nothing to leak. This is the design intent of `Func<Node2D>?` over a pre-instantiated `Node2D`.

### Main-thread guarantees

- Pre-warm: synchronous on main thread (inside existing `HandleVoteAsync`).
- Factory invocation: synchronous on main thread (inside `popup.Show()`).
- Cleanup: marshaled through `_dispatcher.Post(SafeQueueFree)` per existing B.1 suspend-and-resume pattern. No regression.

## Error handling

| Failure | Where | Behavior |
|---|---|---|
| `encounter.AllPossibleMonsters` is empty | `BuildVisualsFactory` | Returns `null`. Column renders empty (slot Control with no child). |
| `monster.AssetPaths.First()` throws or is empty | `PreWarmBossVisuals` | Per-candidate try/catch. Log warning, continue. Cold-load still works at factory time. |
| `PreloadManager.Cache.GetScene` throws | `PreWarmBossVisuals` | Same — per-candidate try/catch. `CreateVisuals` will hit `CreateFallbackVisuals` at factory time. |
| `monster.CreateVisuals()` throws | factory body | `CreateVisuals` has its own internal try/catch + fallback. Effectively can't throw. |
| Fallback scene itself throws | factory body | Caught by popup's per-column try/catch. Log warning, empty column. |
| `visuals.HasSpineAnimation` is false | factory body | Skip animator/skin/idle_loop wiring. Static pose still renders. Matches Bestiary. |
| `visuals.Bounds.Size == (0, 0)` | `PortraitFit.ComputeFitScale` | `Mathf.Max(..., 1f)` floor → `fit = 1.0`. No crash. |
| Popup constructed but never `Show()`n | dispatcher race | Factories drop with popup ref. No instances created. |
| Run dies mid-vote with visuals in tree | `_isRunDying` probe | `session.Cancel()` → `_cancelledHandler` → cascade frees. B.3 path unchanged. |
| Pause menu / dev console mid-vote | `_isOccludingOverlayVisible` probe | Canvas layer hides; Spine timescale → 0. Resume → timescale → 1. |

**Not handled (correctly):**
- **Threading**: factory closure captures `monster` reference; `MonsterModel` is mutable but `CreateVisuals` is a pure-getter chain. Factory only invoked on main thread.
- **Memory pressure**: 3 instances per popup, lifetime ≤ 30s. No pooling needed.
- **Multi-vote-per-act**: B.3 idempotency-per-(run, act) prevents it. Golden Compass re-vote reuses the same path transparently.

## Testing

### Unit tests

Only one slice is pure enough to unit-test: `PortraitFit.ComputeFitScale`.

`tests/Game/Ui/PortraitFitTests.cs`:
- Sprite smaller than slot → returns 1.0.
- Sprite wider than slot → returns `slotSize.X / boundsSize.X`.
- Sprite taller than slot → returns `slotSize.Y / boundsSize.Y`.
- Sprite exactly matches slot → returns 1.0.
- Bounds = (0, 0) → returns 1.0 (no divide-by-zero).
- Bounds = (-1, -1) → returns 1.0 (Mathf.Max floor).

Pure math, no `TiLog` calls. Does **not** require `[Collection("TiLog.Sink")]`.

### Not unit-tested (intentionally)

- `BuildVisualsFactory` — touches sealed MegaCrit types; mocking adds no value.
- `PreWarmBossVisuals` — touches `PreloadManager.Cache` singleton.
- `BossVotePopup` column build / occlusion freeze — needs Godot scene tree.

These are covered by operator validation. We're not going to mock `MonsterModel` or `PreloadManager` — the cost-to-value ratio is wrong and any mock would just verify our mock, not vanilla behavior.

### Operator validation checklist

Recorded in [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) under the B.3.1 heading on completion. All must pass before tagging `plan-b-3-1-complete`.

**Visual correctness:**
1. ☐ Boss vote popup shows animated combat sprites for all 3 candidates.
2. ☐ **Spine-only bosses render properly** — bug we're fixing. Ceremonial Beast vote shows visible idle-animating sprite (not empty column).
3. ☐ **Placeholder bosses still render** — regression check. All 9 placeholder bosses (Soul Fysh, Vantom, The Kin, Lagavulin Matriarch, Waterfall Giant, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject) render visibly.
4. ☐ Sprites are sized appropriately within their columns. No overlap into neighbors.
5. ☐ Sprite vertical position roughly centered in slot.

**Lifecycle correctness:**
6. ☐ First vote in a run doesn't stutter visibly vs subsequent votes (pre-warm working). Validate on slowest hardware available.
7. ☐ Pause menu mid-vote → popup hides, no animation visible behind menu. Resume → popup re-appears, animation resumes.
8. ☐ Dev console mid-vote → popup hides cleanly, console interactive. Close → popup re-appears.
9. ☐ Run abandonment mid-vote → popup frees promptly. Game-over screen reachable without popup overlay.
10. ☐ Save-quit mid-vote → Continue restores popup per B.3's existing behavior (no regression).

**Coverage:**
11. ☐ Validated on Act 1 boss vote.
12. ☐ Validated on Act 2 boss vote.
13. ☐ Validated on Act 3 boss vote.
14. ☐ Golden Compass re-vote produces consistent visuals (idempotency unchanged from B.3).

**Hardware envelope:**
15. ☐ Validated on primary dev machine (must pass).
16. ☐ Stretch: validate on second machine (Steam Deck or older laptop). If inaccessible, ship and accept one round of polish-iteration if community report surfaces stutter.

**Build pipeline (per CLAUDE.md Tier 1):**
17. ☐ `pwsh -File build.ps1` clean.
18. ☐ `pwsh -File install.ps1` clean.
19. ☐ `godot.log` version hash matches `git log -1 --format=%H`.
20. ☐ No `[boss-vote]` warnings in `godot.log` under normal flow.

## Commit conventions

Per CLAUDE.md slice convention: `plan-b-3-1/N.M:` prefix. Tag `plan-b-3-1-complete` on acceptance-gate green. Every commit ends with the trailer:

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

## Open risks

1. **`visuals.Bounds.Size` may be `(0, 0)` immediately after `AddChild`** if Spine atlas measurement is lazy. `PortraitFit.ComputeFitScale` handles this defensively (returns 1.0), but the visual result would be "sprite at native size" which could overflow the slot. `ClipContents = true` belts this — overflow renders are clipped to slot bounds. If observed, mitigation is a deferred measurement (one `await ToSignal(GetTree(), ProcessFrame)` before reading `Bounds.Size`), which would mean wrapping the factory body in an async path. Defer until observed.

2. **Pre-warm latency on cold disk** — if the streamer's machine has a spinning HDD and the boss assets aren't OS-cached, the synchronous `PreloadManager.Cache.GetScene` calls could block the main thread for hundreds of ms. The user perceives this as "Proceed button click → brief lag → popup appears." This is the design intent (load hitch before vote timer starts), but if it's offensive in practice, a follow-up could move the pre-warm to chest-room-enter (a postfix on `NTreasureRoom._Ready`), trading a small wasted-load risk (if streamer abandons before clicking Proceed) for guaranteed-warm cache.

3. **Spine timescale = 0 may have edge cases** in MegaSpine's animation state machine. Reference for the pattern is the game's own code; if MegaCrit doesn't drive `SetTimeScale(0)` anywhere themselves, our usage could be the first. Operator validation gate 7 (pause menu mid-vote) catches regressions; if observed, fallback is `_canvasLayer.Visible = false` only (skip the timescale call) — animations keep ticking invisibly during pause, minor CPU waste but no correctness issue.
