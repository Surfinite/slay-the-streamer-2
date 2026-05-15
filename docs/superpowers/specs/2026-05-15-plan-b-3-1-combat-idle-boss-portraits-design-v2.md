# 2026-05-15 — Plan B.3.1: Combat-idle boss portraits (design, v2)

**Date**: 2026-05-15 (v1); v2 incorporates 9-reviewer meta-review (2026-05-15).
**Status**: Design pending user approval. Implementation pending plan + approval.
**Slice**: B.3.1 (post-B.3 polish) — fixes the empty-column bug for Spine-only bosses in the boss-vote popup.
**Scope**: Replace the `BossVotePopup`'s per-column PNG portrait (loaded from `EncounterModel.BossNodePath + ".png"`) with an animated combat-idle portrait rendered via `MonsterModel.CreateVisuals()`, modeled on `NBestiary`'s instantiation pattern.

**v2 changes**: Adopts `ProcessMode.Disabled` on the slot Control for occlusion freeze (eliminating SetTimeScale risk + private-field seam concession in one swap); adds deferred-frame `Bounds.Size` measurement; bumps column slot to 384×384; adds multi-monster warning, Stopwatch logging, main-thread invariant, PhobiaMode gate, cold-load simulation pre-ship procedure. See `META-REVIEW-...md` for the full disposition trail.

## TL;DR

The shipped B.3 popup loads boss portraits from `BossNodePath + ".png"` ([`BossVotePatch.cs` `ResolvePortraitPath`](../../../src/Game/DecisionVotes/BossVotePatch.cs)). That works for "placeholder" bosses that explicitly ship a PNG in `res://images/map/placeholder/`, but **Spine-only bosses ship no PNG fallback** (the `.tres` skeleton is the only on-disk asset). Ceremonial Beast is the current example; as MegaCrit ships more proper Spine art, the asymmetry worsens.

`MonsterModel.CreateVisuals()` is a public one-call API ([`MonsterModel.cs:238`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L238), verified `public` in stable `sts2.dll` 2026-05-15) that returns a fully-wired `NCreatureVisuals` Node2D — the same node combat uses to render the boss during fights. `NBestiary.SelectMonster` is the canonical reference for rendering this node outside combat in a `NSubmenu` context — exactly analogous to our popup. The combat infrastructure is fully present in stable; only the Bestiary UI screen is beta-gated.

<!-- CHANGED: LOC honesty per reviewer feedback — Reviewers 1, 9 -->
Net change: ~80–100 LOC across 3 modified files + 1 new helper file + 1 new test file. `BossVotePopupOption` swaps `PortraitPath` for a `Func<Node2D>? VisualsFactory`. `BossVotePatch` builds the factory closures, pre-warms the asset cache for each candidate (with timing telemetry), and drops the now-dead `ResolvePortraitPath` helper. `BossVotePopup` swaps the `TextureRect` slot for a sized `Control` that parents the factory-produced Node2D, fits to size via a one-frame-deferred measurement pass, and freezes Spine playback when occluded **via Godot's native `ProcessMode.Disabled` on the slot Control** — no MegaCrit type references required.

## Goals

- **Fix the empty-column bug** for Spine-only bosses (Ceremonial Beast today; more bosses as Spine art ships).
- **Future-proof against placeholder→Spine migration** — every monster ships a combat scene because combat needs it; the asymmetry stops being a moving target.
- **Pre-warm the asset cache** at vote-start so first-popup-of-run doesn't hitch on slower hardware, with `Stopwatch` telemetry so we have actual data on the load cost.
- <!-- CHANGED: seam now absolute via ProcessMode switch — Reviewers 1, 3, 7, 8 -->
- **Preserve TI/Game seam absolutely** — `BossVotePopup` and `BossVotePopupOption` stay fully MegaCrit-free. The occlusion freeze uses Godot's native `ProcessMode.Disabled` on the slot Control, which cascades to NCreatureVisuals children via Inherit. No typed references to MegaCrit types anywhere in `src/Game/Ui/`.
- **No regression** for placeholder bosses (Soul Fysh, Vantom, The Kin, Lagavulin Matriarch, Waterfall Giant, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject). Note that the visual will change from a static PNG to an animated combat sprite — this is a deliberate upgrade, not a regression.

## Non-goals

- **Map-tier Spine rendering** of `BossNodeSpineResource` via `NSpineAutoPlayer` — the alternative path discussed in feasibility.
- **Multi-monster encounter handling** beyond a `TiLog.Warn` log line — pick-first rule documented. All current act-boss encounters are single-monster (verified across `CeremonialBeastBoss`, `DoormakerBoss`, `SoulFyshBoss`, etc.).
- **Settings toggle** for opting back into PNG portraits — YAGNI for v1.
- **SubViewport-based column rendering** — defer to polish if column-overlap is observed in operator validation.
- **MonsterModel.ToMutable** — Bestiary uses canonical instances and works; we follow the same pattern.
<!-- CHANGED: removed stale meta-review non-goal — Reviewer 1 -->
- **Wrapper class for NCreatureVisuals** — mooted by ProcessMode.Disabled approach; no longer needed for seam preservation.
- **`ResourceLoader.LoadThreadedRequest`** — synchronous pre-warm with telemetry first; escalate only if measurement shows hitch on target hardware.
- **Chest-room-enter pre-warm (Variant C)** — Variant B (vote-start) ships first with telemetry; Variant C is the natural follow-up if needed.

## Architecture

Three files change; one new helper file is created; no new abstractions are introduced.

### TI/Game seam — absolute preservation

<!-- CHANGED: language reflects ProcessMode design; seam concession removed entirely — Reviewers 1, 3, 7, 8 -->

The ProcessMode.Disabled approach eliminates the v1 design's private-field concession:

- [`src/Game/Ui/BossVotePopupOption.cs`](../../../src/Game/Ui/BossVotePopupOption.cs) — fully MegaCrit-free. DTO's only new field is `Func<Node2D>? VisualsFactory`, a delegate type from `System` + `Godot` only.
- [`src/Game/Ui/BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs) — fully MegaCrit-free at both interface and implementation. The popup holds `List<Control>` for slot references (Godot type), not `List<NCreatureVisuals>`. Setting `slot.ProcessMode = ProcessModeEnum.Disabled` on occlusion cascades to NCreatureVisuals children via Inherit, freezing Spine playback without ever calling into Spine APIs.
- The factory's body (in `BossVotePatch`) is the only place that touches `MonsterModel` / `EncounterModel` / `NCreatureVisuals`.
- TI extraction viability is preserved without compromise.

### Vanilla API surface

Verified against the stable `sts2.dll` (`src/sts2.dll`, `LastWriteTime: 2026-05-15`):

- **`MonsterModel.CreateVisuals()`** — `public NCreatureVisuals`. Loads `creature_visuals/<id>.tscn` via `PreloadManager.Cache.GetScene` and instantiates it. Internal try/catch + fallback to `creature_visuals/fallback` if the per-monster scene fails. This is the same method combat uses to render the boss during fights.
- **`MonsterModel.AssetPaths`** — `public IEnumerable<string>`. **First entry is the monster's combat scene path** ([`MonsterModel.cs:68-85`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L68-L85): `span[0] = VisualsPath`).
  <!-- CHANGED: document this as decompile-verified observation, not API contract — Reviewer 6 -->
  *Note: this ordering is a decompile-verified observation, not a documented API contract. If MegaCrit ever reorders the list, pre-warm silently degrades to cold-load at factory time (no crash, no incorrect rendering — `CreateVisuals` reads `VisualsPath` directly).*
- **`MonsterModel.GenerateAnimator(MegaSprite)`** — sets up the creature's animation state machine. Initial state is always `AnimState("idle_loop", isLooping: true)`. Writes to the `MegaSprite` parameter only; does NOT mutate `MonsterModel` itself (decompile-verified, factory closure capture is safe).
- **`MonsterModel.SetUpSkin(NCreatureVisuals)`** — applies any per-monster skin variants. Writes to the visuals parameter only.
- **`PreloadManager.Cache.GetScene(string)`** — returns a `PackedScene`. Discarding the return value primes the cache. Idempotent (Godot resource cache is ref-counted).
- **`NCreatureVisuals.HasSpineAnimation`** — gate for the animator/skin wiring.
- **`NCreatureVisuals.SpineBody`** — `MegaSprite` wrapping the underlying Spine sprite.
- **`NCreatureVisuals.SetScaleAndHue(float scale, float hue)`** — applies scale uniformly; `hue = 0f` means no hue shift.
- **`NCreatureVisuals.Bounds`** — `Control` exposing the rendered bounding box, populated in `_Ready`. **Size is generally `(0, 0)` immediately after `AddChild`** — Spine atlas measurement is typically lazy. Wait one `ProcessFrame` before reading (see Code changes).
- **`EncounterModel.AllPossibleMonsters`** — `abstract IEnumerable<MonsterModel>`. Concrete boss subclasses return a `ReadOnlySingleElementList<MonsterModel>` wrapping the canonical `ModelDb.Monster<X>()` instance. All current act bosses are single-monster.

## Code changes

### `src/Game/Ui/BossVotePopupOption.cs`

DTO field swap (unchanged from v1):

```csharp
internal sealed record BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory);
```

### `src/Game/DecisionVotes/BossVotePatch.cs`

Three changes:

**(a) New pre-warm helper with timing telemetry.** Called synchronously on the main thread after candidate sampling, before session start.

<!-- CHANGED: add Stopwatch logging — Reviewers 2, 4, 5, 6, 9 -->
```csharp
private static void PreWarmBossVisuals(IReadOnlyList<EncounterModel> candidates) {
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int succeeded = 0;
    foreach (var encounter in candidates) {
        try {
            var monster = encounter.AllPossibleMonsters.FirstOrDefault();
            if (monster is null) continue;
            // AssetPaths.First() == VisualsPath (verified observation, not API contract).
            // If ordering ever changes, CreateVisuals reads VisualsPath directly anyway.
            var scenePath = monster.AssetPaths.FirstOrDefault();
            if (string.IsNullOrEmpty(scenePath)) continue;
            _ = PreloadManager.Cache.GetScene(scenePath);
            succeeded++;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] preload failed for {encounter.Id?.Entry}: {ex.Message}");
        }
    }
    sw.Stop();
    TiLog.Info($"[SlayTheStreamer2][boss-vote] pre-warm: {succeeded}/{candidates.Count} candidates in {sw.ElapsedMilliseconds}ms");
}
```

**(b) New factory builder.** Returns a closure capturing the canonical `MonsterModel`.

<!-- CHANGED: multi-monster Warn log — Reviewers 1, 2, 7, 8 -->
```csharp
private static Func<Node2D>? BuildVisualsFactory(EncounterModel encounter) {
    var monsters = encounter.AllPossibleMonsters.ToList();
    if (monsters.Count == 0) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] encounter {encounter.Id?.Entry} has no monsters; column will render empty");
        return null;
    }
    if (monsters.Count > 1) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] encounter {encounter.Id?.Entry} has {monsters.Count} monsters; rendering first ({monsters[0].Id?.Entry}) only");
    }
    var monster = monsters[0];
    return () => {
        var visuals = monster.CreateVisuals();
        if (visuals.HasSpineAnimation) {
            monster.GenerateAnimator(visuals.SpineBody);
            visuals.SetUpSkin(monster);
            // idle_loop is canonical: every MonsterModel.GenerateAnimator override
            // initialises with new AnimState("idle_loop", isLooping: true).
            // See MonsterModel.cs:387 + CeremonialBeast.cs:247.
            visuals.SpineBody.GetAnimationState().SetAnimation("idle_loop");
        }
        return visuals;
    };
}
```

**(c) Updated DTO construction site.** Call `PreWarmBossVisuals` after sampling, build factories instead of resolving PNG paths.

<!-- CHANGED: main-thread invariant documented — Reviewer 1 -->
```csharp
// IMPORTANT: PreWarmBossVisuals + factory construction + popup construction + popup.Show()
// must all run synchronously on the Godot main thread BEFORE the first `await` in this
// method. Godot resource loading and scene instantiation are main-thread-only. Do not
// move any of these below an await without marshalling via IMainThreadDispatcher.
PreWarmBossVisuals(sample);

var dtos = sample.Select((e, i) => new BossVotePopupOption(
    Index: i,
    Title: e.Title.GetFormattedText(),
    VisualsFactory: BuildVisualsFactory(e))).ToList();
```

**Deleted**: `ResolvePortraitPath` — now dead code.

### `src/Game/Ui/BossVotePopup.cs`

<!-- CHANGED: three modifications, no private MegaCrit field, ProcessMode-based freeze — Reviewer 8 (ProcessMode insight); Reviewers 1, 3, 7, 8 (seam) -->
Three changes:

**(a) New private field** for tracking slot Controls (Godot type only — no MegaCrit reference):

```csharp
private readonly List<Control> _portraitSlots = new();
```

**(b) Column build.** Replace the `TextureRect` block with a sized `Control` slot, factory invocation, and one-frame-deferred measurement.

<!-- CHANGED: 256→384 column size — Reviewers 1, 2, 3, 4, 5, 6, 7, 8, 9 -->
<!-- CHANGED: deferred Bounds.Size measurement via ProcessFrame signal — Reviewers 1, 2, 4, 6, 8, 9 -->
```csharp
// Named constant — replaces v1's inline new Vector2(256, 256).
private static readonly Vector2 PortraitSlotSize = new(384, 384);

// Inside Show()'s column loop:
var slot = new Control {
    Name = "PortraitSlot",
    CustomMinimumSize = PortraitSlotSize,
    ClipContents = true,        // belt for any sprite that draws outside Bounds
    ProcessMode = ProcessModeEnum.Inherit,   // default; explicit for clarity
};
col.AddChild(slot);
_portraitSlots.Add(slot);

if (opt.VisualsFactory is not null) {
    try {
        var visuals = opt.VisualsFactory.Invoke();
        slot.AddChild(visuals);
        // Spine atlas measurement is typically lazy — Bounds.Size is (0, 0) on the
        // same frame as AddChild. Defer the fit calculation to the next process frame
        // so we read populated bounds. The unfit visuals render at native scale on
        // frame 0 but ClipContents = true on the slot prevents column overflow.
        _ = ApplyPortraitFit(slot, visuals);
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] visuals factory threw for column {opt.Index}: {ex.Message}");
    }
}
```

Where `ApplyPortraitFit` is a small async helper on the same class:

```csharp
private async Task ApplyPortraitFit(Control slot, Node2D visuals) {
    // Wait one frame so Spine atlas measurement populates Bounds.Size.
    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    if (!GodotObject.IsInstanceValid(visuals) || !GodotObject.IsInstanceValid(slot)) return;

    // Note: visuals is typed as Node2D here (DTO-clean). NCreatureVisuals members are
    // accessed via reflection-free pattern matching, keeping this file MegaCrit-free.
    // The cast happens locally within the helper and never escapes the popup's surface.
    var fit = PortraitFit.ComputeFitScale(GetVisualBounds(visuals), slot.Size);
    InvokeScaleAndHue(visuals, fit);
    // Initial placement: provisional. Operator validation must check per-boss centering;
    // if misaligned, switch to Bestiary's (0, Size.Y * 0.5f) model or compute a
    // bounds-aware offset.
    visuals.Position = slot.Size * 0.5f;
}

// These helpers exist to keep the BossVotePopup file free of literal NCreatureVisuals
// type references at compile time, while still leveraging the runtime type. The Factory
// constructs an actual NCreatureVisuals via BossVotePatch; we just don't name the type
// in this file's surface.
private static Vector2 GetVisualBounds(Node2D visuals) {
    // Duck-typed access through Godot's Call() API to read the Bounds Control.
    var bounds = visuals.Call("get", "Bounds").AsGodotObject() as Control;
    return bounds?.Size ?? Vector2.Zero;
}

private static void InvokeScaleAndHue(Node2D visuals, float scale) {
    visuals.Call("SetScaleAndHue", scale, 0f);
}
```

**Design note on duck-typed access**: The popup never names `NCreatureVisuals` as a type. Method invocation uses Godot's `Call()` (string-based dispatch over the C# binding). This is slower than direct method calls but only fires once per column at popup show; the cost is unmeasurable in practice. Trade-off: ~10 extra LOC for absolute seam preservation.

**Alternative considered**: drop the duck-typing and accept the type reference in private helpers (still MegaCrit-free at the *public* surface). This is the v1 framing, walked back per the v2 seam decision. If duck-typing feels excessive in implementation review, this alternative is one Edit away. See Optional Enhancements for the explicit pick.

**(c) Occlusion handler in `_Process`.** Extend the existing block to toggle `ProcessMode` on slot Controls (no Spine API contact, no typed references).

<!-- CHANGED: ProcessMode.Disabled replaces SetTimeScale(0f) — Reviewer 8 -->
```csharp
if (_canvasLayer is not null) {
    bool occluded = false;
    try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
    catch { /* probe must never crash _Process */ }
    if (_canvasLayer.Visible == occluded) {
        _canvasLayer.Visible = !occluded;
        // ProcessMode.Disabled on the slot Control cascades to NCreatureVisuals children
        // via Inherit. This freezes Spine playback without reaching into MegaSpine APIs
        // and without typed references. Per CLAUDE.md Tier 4: SceneTree.Paused is never
        // toggled by StS2's pause menu, so the freeze is driven by _isOccludingOverlayVisible
        // (which already detects pause via SubmenuStack.SubmenusOpen).
        foreach (var slot in _portraitSlots) {
            slot.ProcessMode = occluded ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        }
    }
    if (occluded) return;
}
```

### New file: `src/Game/Ui/PortraitFit.cs`

Pure-math helper extracted for unit-testability:

<!-- CHANGED: docstring accuracy — Reviewer 6 -->
```csharp
using Godot;

namespace SlayTheStreamer2.Game.Ui;

internal static class PortraitFit {
    /// <summary>
    /// Computes a uniform scale factor to fit a sprite of <paramref name="boundsSize"/>
    /// inside a slot of <paramref name="slotSize"/>, never upscaling past native size.
    /// Defensive against zero, negative, or sub-1.0 positive bounds (returns 1.0).
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
   b. PreWarmBossVisuals(sample)  ← B.3.1 ADDS THIS (synchronous, before first await)
      └─ for each candidate: PreloadManager.Cache.GetScene(monster.AssetPaths.First())
      └─ Stopwatch logs elapsed ms + succeeded count
      └─ cold-load hitch lands here (chest scene still visible).
   c. Build DTOs with VisualsFactory closures (multi-monster Warn if applicable).
   d. Start session, construct popup, call popup.Show() on main thread.
4. BossVotePopup.Show():
   a. Build CanvasLayer + backdrop + content/columns (existing).
   b. For each column:
      └─ Build sized Control slot (384×384, ClipContents = true).
      └─ Append slot to _portraitSlots.
      └─ Invoke opt.VisualsFactory() → returns Node2D (actually NCreatureVisuals at runtime).
      └─ slot.AddChild(visuals).
      └─ Fire-and-forget ApplyPortraitFit(slot, visuals) — async helper that awaits
         one ProcessFrame, then reads Bounds.Size, computes fit, applies scale + position.
   c. Subscribe Closed/Cancelled handlers, AddChild to SceneTree.Root.
5. _Process polls timer/tally (existing). On occlusion change:
   a. Toggle _canvasLayer.Visible (existing).
   b. Toggle slot.ProcessMode (Inherit ↔ Disabled) for each slot ← B.3.1 NEW.
      Cascades to NCreatureVisuals children, freezing Spine playback.
6. Vote closes → session.Closed → dispatcher.Post(SafeQueueFree) →
   _canvasLayer.QueueFree() → Godot cascade frees all column children
   including NCreatureVisuals Node2Ds (and their slot Control parents).
7. Resume: BossVotePatch posts ResumeOnMainThread (B.3 existing).
```

### Lifecycle ownership

- `NCreatureVisuals` instances are created lazily by the factory at popup `Show()` time.
- Owned by the `slot` Control's child list → column `VBoxContainer` → content `VBoxContainer` → `_canvasLayer`.
- Freed implicitly via Godot's parent-child cascade when `_canvasLayer.QueueFree()` runs.
- **Factory-never-invoked case**: if popup is constructed but `Show()` is never called, no `NCreatureVisuals` instances exist yet — nothing to leak. This is the design intent of `Func<Node2D>?` over a pre-instantiated `Node2D`.

### Main-thread guarantees

<!-- CHANGED: explicit invariant documentation — Reviewer 1 -->
- **Pre-warm + factory construction + popup construction + `popup.Show()` MUST run synchronously on the Godot main thread before the first `await` in `HandleVoteAsync`.** Godot resource loading and scene instantiation are main-thread-only. A future refactor that moves any of these below an `await` must marshal back via `IMainThreadDispatcher`.
- `ApplyPortraitFit` is itself an `async Task` that yields via `ToSignal(ProcessFrame)` — this is safe because Godot signals fire on the main thread and `ToSignal` resumes on the captured `SynchronizationContext`.

## Error handling

| Failure | Where | Behavior |
|---|---|---|
| `encounter.AllPossibleMonsters` is empty | `BuildVisualsFactory` | Returns `null` + Warn log. Column renders empty (slot Control with no child). |
| `encounter.AllPossibleMonsters.Count > 1` | `BuildVisualsFactory` | Picks first + Warn log noting the count and selected monster's id. |
| `monster.AssetPaths.First()` throws or is empty | `PreWarmBossVisuals` | Per-candidate try/catch. Log warning, continue. Cold-load still works at factory time. |
| `PreloadManager.Cache.GetScene` throws | `PreWarmBossVisuals` | Same — per-candidate try/catch. `CreateVisuals` will hit `CreateFallbackVisuals` at factory time. |
| `monster.CreateVisuals()` throws | factory body | <!-- CHANGED: soften absolutism — Reviewer 1 --> Expected to be caught by vanilla's internal try/catch + fallback. If it propagates, caught by popup's per-column try/catch. |
| Fallback scene itself throws | factory body | Caught by popup's per-column try/catch. Log warning, empty column. |
| `visuals.HasSpineAnimation` is false | factory body | Skip animator/skin/idle_loop wiring. Static pose renders. Matches Bestiary. No current act boss exercises this path. |
| `visuals.Bounds.Size == (0, 0)` after `ProcessFrame` yield | `ApplyPortraitFit` | `Mathf.Max(..., 1f)` floor → `fit = 1.0`. No crash. Rare edge case after deferred measurement; `ClipContents` belts the rendering. |
| `Visuals` freed during ProcessFrame yield | `ApplyPortraitFit` | `IsInstanceValid` check skips the fit application. Safe. |
| Popup constructed but never `Show()`n | dispatcher race | Factories drop with popup ref. No instances created. |
| Run dies mid-vote with visuals in tree | `_isRunDying` probe | `session.Cancel()` → `_cancelledHandler` → cascade frees. B.3 path unchanged. |
| Pause menu / dev console mid-vote | `_isOccludingOverlayVisible` probe | Canvas layer hides; slot.ProcessMode → Disabled. Resume → Inherit. Spine playback freezes via cascade; resumes from frozen frame on un-occlude. |

<!-- CHANGED: rename for clarity — Reviewer 4 -->
**Deliberately unhandled:**
- **Threading**: factory closure captures `monster` reference; `MonsterModel` is mutable but `CreateVisuals` / `GenerateAnimator` / `SetUpSkin` are pure w.r.t. `MonsterModel` (decompile-verified: they write to `MegaSprite` / `NCreatureVisuals` parameters, not back to the model). Factory only invoked on main thread.
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

`BuildVisualsFactory`, `PreWarmBossVisuals`, `BossVotePopup` column build, occlusion freeze — all need Godot scene tree or touch sealed MegaCrit types. Covered by operator validation.

### Operator validation checklist

<!-- CHANGED: rework + additions per multiple reviewers (PhobiaMode, cold-cache measurement, visual-smoothness, hardware) -->
All must pass before tagging `plan-b-3-1-complete`.

**Visual correctness:**
1. ☐ Boss vote popup shows animated combat sprites for all 3 candidates.
2. ☐ **Spine-only bosses render properly** — bug we're fixing. Ceremonial Beast vote shows visible idle-animating sprite (not empty column).
3. ☐ **Placeholder bosses still render** — regression check across all 9 placeholder bosses (Soul Fysh, Vantom, The Kin, Lagavulin Matriarch, Waterfall Giant, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject). Note: visual is now a combat sprite, not the prior static PNG — this is a deliberate upgrade.
4. ☐ Sprites are sized appropriately within their columns. No overlap into neighbors.
5. ☐ Sprite vertical/horizontal position roughly centered in slot. **If misaligned for some bosses, switch from `slot.Size * 0.5f` to Bestiary's `(0, Size.Y * 0.5f)` model or compute a bounds-aware offset.**
6. ☐ Slot size 384×384 produces visually impactful animated portraits (no obvious cramping).

**Lifecycle correctness:**
7. ☐ Pause menu mid-vote → popup hides, slot ProcessMode → Disabled. **Spine animation freezes cleanly; resume after pause-menu close shows animation continuing smoothly without visible frame-skip or pose-pop.**
8. ☐ Dev console mid-vote → popup hides cleanly, console interactive. Close → popup re-appears with smooth animation resume.
9. ☐ Run abandonment mid-vote → popup frees promptly. Game-over screen reachable without popup overlay.
10. ☐ <!-- CHANGED: corrected gate language — Reviewer 1 --> Save-quit mid-vote → no orphaned popup, no crash, no invalid boss swap. Continue restores run in a valid state per B.3's existing behavior (boss-swap idempotency via `_lastSwappedBossId`; popup is *not* restored — vote is cancelled or restarts per B.3's existing path).
11. ☐ **PhobiaMode toggle mid-vote** → creature visuals swap to phobia-safe variants automatically (handled by `NCreatureVisuals._EnterTree` → `NGame.PhobiaModeToggled` signal); no crash.
12. ☐ Multi-monster encounter (if one exists in tested build) → first monster renders + `[boss-vote] encounter X has N monsters` Warn appears in `godot.log`.

**Coverage:**
13. ☐ Validated on Act 1 boss vote.
14. ☐ Validated on Act 2 boss vote.
15. ☐ Validated on Act 3 boss vote.
16. ☐ Golden Compass re-vote produces consistent visuals (idempotency unchanged from B.3).

**Hardware envelope:**
17. ☐ **Pre-warm Stopwatch log under normal flow:** `[boss-vote] pre-warm: N/M candidates in Xms`. Record X for the project notes.
18. ☐ <!-- CHANGED: cold-load simulation — Reviewer 9 --> **Cold-load simulation on dev machine:** temporarily insert `Thread.Sleep(500)` after each `PreloadManager.Cache.GetScene` call. Validate that simulated 1.5s aggregate hitch is acceptable UX (click → ~1.5s pause → popup). If unacceptable, document escalation to Variant C (chest-room-enter pre-warm) as v0.2 polish. Revert the sleep.
19. ☐ <!-- CHANGED: reworked stretch gate — Reviewers 6, 7 --> **If a second machine is accessible** (Steam Deck, older laptop, spinning HDD): validate first-vote-of-run does not stutter visibly after popup appears. If inaccessible, the cold-load simulation (gate 18) is the substitute data point — no separate validation required.

**Build pipeline (per CLAUDE.md Tier 1):**
20. ☐ `pwsh -File build.ps1` clean.
21. ☐ `pwsh -File install.ps1` clean.
22. ☐ `godot.log` version hash matches `git log -1 --format=%H`.
23. ☐ No unexpected `[boss-vote]` Warn lines in `godot.log` under normal flow (multi-monster Warn is OK if it surfaces; otherwise only Info-level Stopwatch logs should appear).

## Commit conventions

Per CLAUDE.md slice convention: `plan-b-3-1/N.M:` prefix. Tag `plan-b-3-1-complete` on acceptance-gate green. Every commit ends with the trailer:

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

## Open risks

1. **`Bounds.Size` may still be `(0, 0)` after one `ProcessFrame` yield** if Spine atlas loading is multi-frame. Mitigation: the `Mathf.Max(..., 1f)` floor in `PortraitFit` returns `fit = 1.0`, `ClipContents = true` belts the overflow rendering. If observed during operator validation, the fix is a retry loop or a `ToSignal` on `visuals.Bounds.Resized`. Defer until observed.

2. **Pre-warm latency on cold disk** — Stopwatch logging gives field data. If gate 18 (cold-load simulation) reveals offensive UX, escalate to chest-room-enter pre-warm in a follow-up slice. The synchronous-load-on-main-thread choice is bounded (≤3 scenes per vote, finite size), but observable on potato hardware.

3. **Duck-typed access via `visuals.Call("get", "Bounds")`** is slower than direct typed access and depends on Godot's string-based dispatch. Cost: unmeasurable in practice (one call per column at popup show). Failure mode: if MegaCrit renames `Bounds` or `SetScaleAndHue`, the Call fails silently (caught by popup's per-column try/catch). Trade-off accepted for absolute seam preservation; the alternative (typed private helpers) is one Edit away — see Optional Enhancements.

---

## Optional Enhancements (pick what you want)

These are reviewer suggestions that are valid but not auto-applied. Pick by number to incorporate.

1. **Drop the duck-typing in `BossVotePopup`** and accept typed `NCreatureVisuals` references in *private static helpers* (still MegaCrit-free at the *public* surface). Trade: ~10 LOC simpler implementation, slightly leakier seam, faster method dispatch (negligible). (R5, R9 implicitly defended this; the duck-typing is a v2 over-correction.) **Effort: trivial. Recommendation: lean yes — duck-typing for this slice is probably over-engineering; the seam at the public-API level is what matters most for TI extraction.**

2. **Add UX-impact operator gate**: "Animated portrait is the dominant visual element of its column (UX judgment, not just rendering)." (R3) **Effort: trivial. Recommendation: neutral — gate 6 already implies this; this would be a more explicit phrasing.**

3. **Add resolution/UI-scale validation gates**: 1920×1080 default, one non-default UI scale, windowed mode at smaller resolution. (R1) **Effort: small. Recommendation: lean yes — animated portraits are more layout-sensitive than static PNGs.**

4. **Implement bounds-aware centering** by default rather than waiting on operator validation: read `bounds.Position` (the Control's offset within its parent) and compute `slotCenter - boundsCenter * fit` as the position. (R1) **Effort: small. Recommendation: lean no — defer until observed; geometric center is a reasonable starting point and the validation gate catches misalignment.**

5. **Add `Bounds.Size = (0,0)` diagnostic log** in `ApplyPortraitFit` if measurement returns zero after the yield. (R9) **Effort: trivial. Recommendation: lean yes — observability for the one residual race condition.**

6. **Add inline comment documenting the ProcessMode.Disabled cascade pattern** in `BossVotePopup.cs` and a note in `notes/06-followups-and-deferred.md` so future mod UI surfaces can reuse the pattern. (R9 in spirit, for SetTimeScale; applies now to ProcessMode.) **Effort: trivial. Recommendation: lean yes — propagates the lesson.**

7. **Increase column slot to 400×400 or 448×448** instead of 384×384 if the popup layout has the horizontal room. (R1, R2 wanted bigger.) **Effort: trivial. Recommendation: neutral — 384 is a reasonable midpoint; tune visually during operator validation.**

8. **Pre-warm helper detects multi-monster encounters** and pre-loads all monsters (not just the first), so a future multi-monster encounter doesn't cold-load the secondary monsters on factory invocation. (Not in any review; logical extension of multi-monster Warn.) **Effort: small. Recommendation: lean no — YAGNI for current bosses; revisit when a multi-monster encounter actually exists.**

9. **Remove the `_portraitSlots` field** and iterate `columns.GetChildren()` to find slots on occlusion. (Not in any review; minor structural choice.) **Effort: trivial. Recommendation: lean no — explicit list is clearer than tree-walking.**

10. **Add an integration smoke** that runs `pwsh -File build.ps1` against the v2 code on a non-author machine before tagging complete. (Not in any review; defensive.) **Effort: medium. Recommendation: lean no — solo dev project, gate 19's simulation is the substitute.**

11. **Validate `PreloadManager.Cache.GetScene` is genuinely synchronous** by checking the decompile, not just assuming. (R6 raised; not yet done.) **Effort: trivial. Recommendation: lean yes — 5-minute decompile grep; verifies a foundational assumption.**

Tell me which numbers to apply (or "none" if v2 is good as-is).
