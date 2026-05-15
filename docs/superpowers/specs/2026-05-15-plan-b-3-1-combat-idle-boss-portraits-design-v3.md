# 2026-05-15 — Plan B.3.1: Combat-idle boss portraits (design, v3)

**Date**: 2026-05-15 (v1, v2); 2026-05-16 (v3 after Round-2 meta-review).
**Status**: Design pending user approval. Implementation pending plan + approval.
**Slice**: B.3.1 (post-B.3 polish) — fixes the empty-column bug for Spine-only bosses in the boss-vote popup.
**Scope**: Replace the `BossVotePopup`'s per-column PNG portrait (loaded from `EncounterModel.BossNodePath + ".png"`) with an animated combat-idle portrait rendered via `MonsterModel.CreateVisuals()`, modeled on `NBestiary`'s instantiation pattern.

**Revision history**:
- **v1 → v2** (Round-1 meta-review, 9 reviewers, 2026-05-15): Adopted `ProcessMode.Disabled` on the slot Control for occlusion freeze (eliminating SetTimeScale risk + private-field seam concession); added deferred-frame `Bounds.Size` measurement; bumped column slot to 384×384; added multi-monster warning, Stopwatch logging, main-thread invariant, PhobiaMode gate, cold-load simulation pre-ship procedure. Applied user-picked Optional Enhancements 1, 3, 5, 6, 11.
- **v2 → v3** (Round-2 meta-review, 3 reviewers, 2026-05-16): Fixed `SetUpSkin` signature documentation (compile-break risk verified via decompile — `NCreatureVisuals.SetUpSkin(MonsterModel)`, not the inverse); fixed `ApplyPortraitFit` scheduling so it dispatches after the canvas is attached to the scene tree (avoiding `GetTree()` → null NRE); added top-level try/catch around the async helper for fire-and-forget exception observability; added `slot.Size = 0` fallback to `PortraitSlotSize`; softened seam claim from "absolute" to "public surface MegaCrit-free with localized typed implementation"; added `SetTimeScale(0f)` as documented Plan B in Open Risks if ProcessMode cascade fails gate 7; added `?.` null-safety on `GetAnimationState()` in factory; removed no-op `ProcessMode = Inherit` explicit set; softened PhobiaMode gate wording.

## TL;DR

The shipped B.3 popup loads boss portraits from `BossNodePath + ".png"` ([`BossVotePatch.cs` `ResolvePortraitPath`](../../../src/Game/DecisionVotes/BossVotePatch.cs)). That works for "placeholder" bosses that explicitly ship a PNG in `res://images/map/placeholder/`, but **Spine-only bosses ship no PNG fallback** (the `.tres` skeleton is the only on-disk asset). Ceremonial Beast is the current example; as MegaCrit ships more proper Spine art, the asymmetry worsens.

`MonsterModel.CreateVisuals()` is a public one-call API ([`MonsterModel.cs:238`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L238), verified `public` in stable `sts2.dll` 2026-05-15) that returns a fully-wired `NCreatureVisuals` Node2D — the same node combat uses to render the boss during fights. `NBestiary.SelectMonster` is the canonical reference for rendering this node outside combat in a `NSubmenu` context — exactly analogous to our popup. The combat infrastructure is fully present in stable; only the Bestiary UI screen is beta-gated.

<!-- CHANGED: LOC honesty per reviewer feedback — Reviewers 1, 9 -->
Net change: ~80–100 LOC across 3 modified files + 1 new helper file + 1 new test file. `BossVotePopupOption` swaps `PortraitPath` for a `Func<Node2D>? VisualsFactory`. `BossVotePatch` builds the factory closures, pre-warms the asset cache for each candidate (with timing telemetry), and drops the now-dead `ResolvePortraitPath` helper. `BossVotePopup` swaps the `TextureRect` slot for a sized `Control` that parents the factory-produced Node2D, fits to size via a one-frame-deferred measurement pass, and freezes Spine playback when occluded **via Godot's native `ProcessMode.Disabled` on the slot Control** — no MegaCrit type references required.

## Goals

- **Fix the empty-column bug** for Spine-only bosses (Ceremonial Beast today; more bosses as Spine art ships).
- **Future-proof against placeholder→Spine migration** — every monster ships a combat scene because combat needs it; the asymmetry stops being a moving target.
- **Pre-warm the asset cache** at vote-start so first-popup-of-run doesn't hitch on slower hardware, with `Stopwatch` telemetry so we have actual data on the load cost.
<!-- CHANGED-v3: soften seam claim — Reviewers R2-2, R2-3 -->
- **Preserve TI/Game seam at the public interface** — `BossVotePopupOption` is fully MegaCrit-free; `BossVotePopup`'s public API (constructor, public methods, public/internal fields visible to other classes) is fully MegaCrit-free. Implementation has two private static helpers (`GetVisualBounds`, `ApplyScaleAndHue`) that pattern-match-and-cast `Node2D → NCreatureVisuals` locally; these are the entire MegaCrit surface in `src/Game/Ui/`. The occlusion freeze uses Godot's native `ProcessMode.Disabled` on the slot Control, which is intended to cascade to NCreatureVisuals children via Inherit (validated by gate 7).
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

### TI/Game seam — public surface MegaCrit-free, private impl typed

<!-- CHANGED-v3: framing aligned with reality; no longer claims "absolute" — Reviewers R2-2, R2-3 -->

The seam guarantee for v3 is **public-surface-clean**, not absolute. v1's private-field concession is gone (ProcessMode.Disabled avoids the typed-state requirement); v2 applied Optional #1 to use typed private static helpers for fit measurement; v3 makes the framing honest:

- [`src/Game/Ui/BossVotePopupOption.cs`](../../../src/Game/Ui/BossVotePopupOption.cs) — fully MegaCrit-free. DTO's only new field is `Func<Node2D>? VisualsFactory`, a delegate type from `System` + `Godot` only.
- [`src/Game/Ui/BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs):
  - **Public/internal API** (constructor signature, public methods, fields visible to other classes): fully MegaCrit-free.
  - **Internal state**: holds `List<Control>` for slot references (Godot type, not MegaCrit) and `List<(Control, Node2D)>` for pending fits during construction.
  - **Private static helpers** (`GetVisualBounds`, `ApplyScaleAndHue`): pattern-match-and-cast `Node2D → NCreatureVisuals` locally. These are private, static, and never escape the class. A future TI-extraction fork would replace these two ~3-line helper bodies with equivalents for the new host game.
  - **Occlusion handler** (`_Process`): toggles `slot.ProcessMode` on Godot `Control` instances. Zero MegaCrit references in this code path; the freeze is intended to cascade to NCreatureVisuals children via Godot's `ProcessMode.Inherit` semantics (validated by gate 7).
- The factory's body (in `BossVotePatch`) does the heavy lifting — `MonsterModel.CreateVisuals()` + animator wiring.
- **TI extraction cost**: replace two ~3-line private static helpers in `BossVotePopup.cs`. Trivial.

### Vanilla API surface

Verified against the stable `sts2.dll` (`src/sts2.dll`, `LastWriteTime: 2026-05-15`):

- **`MonsterModel.CreateVisuals()`** — `public NCreatureVisuals`. Loads `creature_visuals/<id>.tscn` via `PreloadManager.Cache.GetScene` and instantiates it. Internal try/catch + fallback to `creature_visuals/fallback` if the per-monster scene fails. This is the same method combat uses to render the boss during fights.
- **`MonsterModel.AssetPaths`** — `public IEnumerable<string>`. **First entry is the monster's combat scene path** ([`MonsterModel.cs:68-85`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs#L68-L85): `span[0] = VisualsPath`).
  <!-- CHANGED: document this as decompile-verified observation, not API contract — Reviewer 6 -->
  *Note: this ordering is a decompile-verified observation, not a documented API contract. If MegaCrit ever reorders the list, pre-warm silently degrades to cold-load at factory time (no crash, no incorrect rendering — `CreateVisuals` reads `VisualsPath` directly).*
- **`MonsterModel.GenerateAnimator(MegaSprite)`** — sets up the creature's animation state machine. Initial state is always `AnimState("idle_loop", isLooping: true)`. Writes to the `MegaSprite` parameter only; does NOT mutate `MonsterModel` itself (decompile-verified, factory closure capture is safe).
<!-- CHANGED-v3: signature was inverted in v2; verified via decompile — Reviewer R2-2 -->
- **`NCreatureVisuals.SetUpSkin(MonsterModel)`** — applies any per-monster skin variants ([`NCreatureVisuals.cs:178`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Combat/NCreatureVisuals.cs#L178)). The method is on `NCreatureVisuals`, takes a `MonsterModel`. Internally delegates to `MonsterModel.SetupSkins(MegaSprite, MegaSkeleton)` (note: different method, plural `s`, on `MonsterModel`). The code samples below already use the correct shape `visuals.SetUpSkin(monster)`.
- **`PreloadManager.Cache.GetScene(string)`** — returns a `PackedScene`. **Verified synchronous** ([`AssetCache.cs:30-40`](../../../decompiled/sts2/MegaCrit/sts2/Core/Assets/AssetCache.cs#L30-L40)): on cache miss, calls `ResourceLoader.Load<Resource>(path, null, CacheMode.Reuse)` synchronously and logs `Log.Warn("Asset not cached: <path>")`. Discarding the return value primes the cache. Idempotent (cache is a `ConcurrentDictionary` keyed by path).
  - <!-- CHANGED: applied Optional #11 — verified via decompile grep --> **Practical implication for our pre-warm**: if `PreloadManager.LoadActAssets(runState.Act)` has already loaded the act's monster scenes (it does — `ActModel.AssetPaths` includes monster `AssetPaths` for all `AllPossibleMonsters`), our pre-warm hits the cache and is a no-op (no log noise). If it hasn't (rare race, e.g., player rushes the chest room before act-start preload completes), our pre-warm fires the vanilla `Log.Warn` lines and synchronously loads. Either path is correct; the no-op case is the common one.
  - **For escalation later**: `PreloadManager.LoadActAssets(ActModel)` is the vanilla async preload entry-point that uses `AssetLoadingSession` and doesn't fire the missed-cache warning. If telemetry shows our synchronous pre-warm is offensive, the Variant C escalation (postfix on `NTreasureRoom._Ready`) would call this method instead of our own helper.
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
            // CHANGED-v3: defensive ?. on GetAnimationState() — R2-1
            visuals.SpineBody.GetAnimationState()?.SetAnimation("idle_loop");
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

**(a) New private fields** for tracking slot Controls and pending fits (Godot types only — no MegaCrit references in field signatures):

<!-- CHANGED-v3: add _pendingFits to defer ApplyPortraitFit until popup is in tree — Reviewer R2-2 -->
```csharp
private readonly List<Control> _portraitSlots = new();
private readonly List<(Control Slot, Node2D Visuals)> _pendingFits = new();
```

**(b) Column build.** Replace the `TextureRect` block with a sized `Control` slot, factory invocation, and a queued fit pass. The fit pass dispatches *after* `_canvasLayer` is added to the scene tree, so `GetTree()` is non-null inside the async helper.

<!-- CHANGED-v3: removed no-op ProcessMode = Inherit; default is Inherit, only documented — Reviewer R2-3 -->
<!-- CHANGED-v3: queue fits to _pendingFits; dispatch after tree-attach — Reviewer R2-2 -->
```csharp
// Named constant — replaces v1's inline new Vector2(256, 256).
private static readonly Vector2 PortraitSlotSize = new(384, 384);

// Inside Show()'s column loop:
var slot = new Control {
    Name = "PortraitSlot",
    CustomMinimumSize = PortraitSlotSize,
    ClipContents = true,        // belt for any sprite that draws outside Bounds
    // ProcessMode is Inherit by default; the occlusion handler toggles Inherit↔Disabled.
};
col.AddChild(slot);
_portraitSlots.Add(slot);

if (opt.VisualsFactory is not null) {
    try {
        var visuals = opt.VisualsFactory.Invoke();
        slot.AddChild(visuals);
        // Queue the fit pass — actual measurement happens after the canvas is added
        // to the scene tree so GetTree() returns non-null inside ApplyPortraitFit.
        _pendingFits.Add((slot, visuals));
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] visuals factory threw for column {opt.Index}: {ex.Message}");
    }
}
```

After the column loop, immediately after `tree.Root.AddChild(_canvasLayer); _canvasLayer.AddChild(this);` (the existing B.3 tree-attach), dispatch the pending fits:

```csharp
// At end of Show(), after AddChild to root:
foreach (var (slot, visuals) in _pendingFits) {
    _ = ApplyPortraitFit(slot, visuals);   // fire-and-forget; helper has its own try/catch
}
_pendingFits.Clear();
```

<!-- CHANGED-v3: top-level try/catch — Reviewers R2-1, R2-2, R2-3 -->
<!-- CHANGED-v3: slot.Size=0 fallback to PortraitSlotSize — Reviewers R2-2, R2-3 -->
Where `ApplyPortraitFit` is a small async helper on the same class. **Typed private static helpers** keep the public surface MegaCrit-free while making the implementation readable. The entire method body is wrapped in try/catch so fire-and-forget exceptions don't go unobserved:

```csharp
private async Task ApplyPortraitFit(Control slot, Node2D visuals) {
    try {
        // Wait one frame so Spine atlas measurement + parent-container layout complete.
        // Safe: by the time this runs, _pendingFits has been dispatched AFTER the
        // canvas was added to SceneTree.Root, so GetTree() returns non-null.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(visuals) || !GodotObject.IsInstanceValid(slot)) return;

        var boundsSize = GetVisualBounds(visuals);
        if (boundsSize == Vector2.Zero) {
            // Spine atlas measurement may take >1 frame on some hardware. PortraitFit's
            // Mathf.Max floor returns fit=1.0 here, ClipContents=true on the slot belts
            // the overflow rendering. Log it so we have observability if this triggers.
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] Bounds.Size is zero after ProcessFrame yield; falling back to native scale + clip");
        }

        // slot.Size may also be (0,0) if layout hasn't completed. Fall back to the
        // intended slot size rather than letting fit=0 produce an invisible sprite.
        var slotSize = slot.Size;
        if (slotSize.X <= 0f || slotSize.Y <= 0f) {
            slotSize = PortraitSlotSize;
        }

        var fit = PortraitFit.ComputeFitScale(boundsSize, slotSize);
        ApplyScaleAndHue(visuals, fit);
        // Initial placement: provisional. Operator validation must check per-boss centering;
        // if misaligned, switch to Bestiary's (0, Size.Y * 0.5f) model or compute a
        // bounds-aware offset.
        visuals.Position = slotSize * 0.5f;
    } catch (Exception ex) {
        // Fire-and-forget exception observability — matches the slice's "degrade
        // silently, log, never crash" principle. The popup remains valid; just this
        // column's fit pass failed.
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] ApplyPortraitFit failed: {ex.Message}");
    }
}

// Private static helpers: cast Node2D → NCreatureVisuals locally. The cast never
// escapes the popup's public API (no method/field signature exposes the type), so
// the TI/Game seam is preserved at the interface level. A TI-extraction fork would
// replace these two helpers' bodies with the new host game's equivalents.
private static Vector2 GetVisualBounds(Node2D visuals) {
    if (visuals is NCreatureVisuals cv && cv.Bounds is not null) return cv.Bounds.Size;
    return Vector2.Zero;
}

private static void ApplyScaleAndHue(Node2D visuals, float scale) {
    if (visuals is NCreatureVisuals cv) cv.SetScaleAndHue(scale, 0f);
}
```

**Design note**: The helpers use pattern-match-and-cast (`is NCreatureVisuals cv`) so a future factory variant returning a different `Node2D` subclass degrades gracefully (silent no-op for fit; sprite renders at native scale with `ClipContents` clipping it). The cast never panics.

**(c) Occlusion handler in `_Process`.** Extend the existing block to toggle `ProcessMode` on slot Controls (no Spine API contact, no typed references).

<!-- CHANGED: ProcessMode.Disabled replaces SetTimeScale(0f) — Reviewer 8 -->
```csharp
if (_canvasLayer is not null) {
    bool occluded = false;
    try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
    catch { /* probe must never crash _Process */ }
    if (_canvasLayer.Visible == occluded) {
        _canvasLayer.Visible = !occluded;
        // REUSABLE PATTERN — Spine freeze via Godot's ProcessMode cascade:
        //   ProcessMode.Disabled on a parent Control halts _Process on all children
        //   whose ProcessMode is Inherit (the default). For Spine-rendered children,
        //   this freezes playback without touching MegaSpine's animation state.
        //   Per CLAUDE.md Tier 4: SceneTree.Paused is never toggled by StS2's pause
        //   menu, so Godot's native Pausable/WhenPaused modes don't help — drive the
        //   freeze from our occlusion probe instead.
        //   Any future mod UI that renders MegaCrit creatures + needs pause-aware
        //   freeze can reuse this pattern. Logged in notes/06-followups-and-deferred.md.
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
      └─ Queue (slot, visuals) into _pendingFits — DO NOT dispatch yet.
   c. Subscribe Closed/Cancelled handlers, AddChild to SceneTree.Root (B.3 existing).
   d. ◆ Dispatch _pendingFits: for each pair, _ = ApplyPortraitFit(slot, visuals).
      Each fire-and-forget task: try { await ToSignal(GetTree(), ProcessFrame);
      compute fit; apply scale + position; } catch (log Warn).
      Now safe to call GetTree() because the popup is in the tree.
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
7. ☐ <!-- CHANGED-v3: explicit animation-frame validation — Reviewer R2-2 --> Pause menu mid-vote → popup hides, slot ProcessMode → Disabled. **Verify animation does NOT advance during occlusion**: pick a boss with a clearly-moving idle (e.g., breathing or sway), pause ≥3 seconds, observe that the pose on resume matches the pose at pause time (allowing one frame of resume catch-up). If the animation visibly advances during pause, the ProcessMode cascade has failed — fall back to Plan B (SetTimeScale) per Open Risk #4.
8. ☐ Dev console mid-vote → popup hides cleanly, console interactive. Close → popup re-appears with smooth animation resume.
9. ☐ Run abandonment mid-vote → popup frees promptly. Game-over screen reachable without popup overlay.
10. ☐ <!-- CHANGED: corrected gate language — Reviewer 1 --> Save-quit mid-vote → no orphaned popup, no crash, no invalid boss swap. Continue restores run in a valid state per B.3's existing behavior (boss-swap idempotency via `_lastSwappedBossId`; popup is *not* restored — vote is cancelled or restarts per B.3's existing path).
11. ☐ <!-- CHANGED-v3: soften wording — Reviewer R2-2 --> **PhobiaMode toggle mid-vote** → no crash; affected creature visuals update if vanilla supports live update for the monster in question (PhobiaMode handling is per-visual via `NCreatureVisuals._EnterTree` → `NGame.PhobiaModeToggled` signal; whether visual swap is observable depends on whether the boss has phobia-mode body variants in its `.tscn`).
12. ☐ Multi-monster encounter (if one exists in tested build) → first monster renders + `[boss-vote] encounter X has N monsters` Warn appears in `godot.log`.

**Coverage:**
13. ☐ Validated on Act 1 boss vote.
14. ☐ Validated on Act 2 boss vote.
15. ☐ Validated on Act 3 boss vote.
16. ☐ Golden Compass re-vote produces consistent visuals (idempotency unchanged from B.3).

**Hardware / resolution envelope:**
17. ☐ **Pre-warm Stopwatch log under normal flow:** `[boss-vote] pre-warm: N/M candidates in Xms`. Record X for the project notes.
18. ☐ <!-- CHANGED: cold-load simulation — Reviewer 9 --> **Cold-load simulation on dev machine:** temporarily insert `Thread.Sleep(500)` after each `PreloadManager.Cache.GetScene` call. Validate that simulated 1.5s aggregate hitch is acceptable UX (click → ~1.5s pause → popup). If unacceptable, document escalation to Variant C (chest-room-enter pre-warm) as v0.2 polish. Revert the sleep.
19. ☐ <!-- CHANGED: reworked stretch gate — Reviewers 6, 7 --> **If a second machine is accessible** (Steam Deck, older laptop, spinning HDD): validate first-vote-of-run does not stutter visibly after popup appears. If inaccessible, the cold-load simulation (gate 18) is the substitute data point — no separate validation required.
20. ☐ <!-- CHANGED: applied Optional #3 — resolution coverage during normal testing flow --> **Resolution coverage during normal testing flow.** The author's primary dev posture is a small window for testing + 1440p ultrawide for fullscreen play, and StS2 supports smooth aspect-ratio / resolution changes during a session. No dedicated test pass is required; instead, *during the act 1/2/3 validation gates 13–15*, exercise at least: (a) the small testing window, (b) 1440p ultrawide fullscreen, (c) at least one intermediate window size encountered during normal resize. Confirm the popup layout, column slot sizing, and animated portrait centering remain visually correct across these. Flag any case where a sprite extends past its column or anchoring breaks.

**Build pipeline (per CLAUDE.md Tier 1):**
21. ☐ `pwsh -File build.ps1` clean.
22. ☐ `pwsh -File install.ps1` clean.
23. ☐ `godot.log` version hash matches `git log -1 --format=%H`.
24. ☐ No unexpected `[boss-vote]` Warn lines in `godot.log` under normal flow. Acceptable warns: multi-monster (only if a multi-monster encounter exists in tested build), Bounds=0 (only if Spine atlas measurement takes >1 frame). Vanilla `Asset not cached` Warns from `PreloadManager.Cache` are NOT our warnings — they're MegaCrit's signal that an asset wasn't pre-cached, harmless if rare.

## Commit conventions

Per CLAUDE.md slice convention: `plan-b-3-1/N.M:` prefix. Tag `plan-b-3-1-complete` on acceptance-gate green. Every commit ends with the trailer:

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

## Open risks

1. **`Bounds.Size` may still be `(0, 0)` after one `ProcessFrame` yield** if Spine atlas loading is multi-frame. Mitigation: the `Mathf.Max(..., 1f)` floor in `PortraitFit` returns `fit = 1.0`, `ClipContents = true` belts the overflow rendering, and a Warn is logged. If gate 1/4 observes consistent zero-bounds during operator validation, the fix is a retry loop or a `ToSignal` on `visuals.Bounds.Resized`. Defer until observed.

2. **Pre-warm latency on cold disk** — Stopwatch logging gives field data. If gate 18 (cold-load simulation) reveals offensive UX, escalate to chest-room-enter pre-warm in a follow-up slice. The synchronous-load-on-main-thread choice is bounded (≤3 scenes per vote, finite size), but observable on potato hardware.

3. **MegaCrit may rename `NCreatureVisuals.Bounds` or `SetScaleAndHue`** in a future game update. Failure mode: the typed private static helpers (`GetVisualBounds` / `ApplyScaleAndHue`) fail to compile against the new `sts2.dll`, surfacing as a build error rather than silent runtime failure — which is the right failure mode. Mitigation: rebuild against the new DLL, update helpers if needed.

<!-- CHANGED-v3: ProcessMode Plan B documented — Reviewer R2-3 -->
4. **`ProcessMode.Disabled` cascade may not freeze MegaSpine playback.** The Godot-native approach assumes MegaSpine advances animation via `_Process` (which `ProcessMode.Disabled` halts) and that the `NCreatureVisuals` subtree inherits ProcessMode from the slot Control. Both are likely but unverified — no prior art in the decompile for this exact cascade pattern. Gate 7 catches the failure mode: animation should visibly freeze for the duration of the pause-menu / dev-console occlusion.
   - **Plan B if gate 7 fails**: switch to per-visual `SetTimeScale(0f) / SetTimeScale(1f)` calls inside a new private static helper (matching the `GetVisualBounds` / `ApplyScaleAndHue` pattern), iterating `_pendingFits` or a parallel list. This was v1's mechanism, retained here as a documented fallback. The escape hatch keeps the seam at the public-interface level (helpers are private static) while reaching into Spine's animation state.
   - **Plan C if both fail**: drop the freeze requirement entirely; animations tick invisibly while the canvas is hidden. Minor CPU waste (3 idle Spine sprites for ≤30s), no correctness issue. Documented for completeness; only choose if Plans A and B both fail.

---

## Optional Enhancements — disposition

User applied **1, 3, 5, 6, 11** in v2 (2026-05-16). v3 round-2 incorporates an additional round of reviewer feedback that fixed two genuine bugs (SetUpSkin signature, ApplyPortraitFit scheduling) and several polish items; see Revision history at top. The original Optional list dispositions remain valid:

1. ✅ **Applied** — typed private static helpers replace duck-typing.
2. ⏸️ Deferred — UX-impact gate; gate 6 covers it implicitly.
3. ✅ **Applied as gate 20** — resolution coverage absorbed into normal test flow given user's small-window-and-1440p-ultrawide testing posture.
4. ⏸️ Deferred — bounds-aware centering; geometric center starts, operator validation catches misalignment.
5. ✅ **Applied** — Bounds=0 diagnostic Warn in `ApplyPortraitFit`.
6. ✅ **Applied** — `BossVotePopup` ProcessMode comment + **note added to `notes/06-followups-and-deferred.md` as part of implementation slice closure (one bullet under B.3.1 marking the reusable freeze pattern)**.
7. ⏸️ Deferred — column size tuning during operator validation.
8. ⏸️ Deferred — multi-monster eager preload; YAGNI.
9. ⏸️ Deferred — explicit list preferred over tree-walking.
10. ⏸️ Deferred — solo dev, gate 18 substitutes.
11. ✅ **Applied** — `PreloadManager.Cache.GetScene` confirmed synchronous via decompile ([`AssetCache.cs:30-40`](../../../decompiled/sts2/MegaCrit/sts2/Core/Assets/AssetCache.cs#L30-L40)); finding folded into Vanilla API surface section. Bonus discovery: `PreloadManager.LoadActAssets` is the vanilla async preload path for any future Variant C escalation.
