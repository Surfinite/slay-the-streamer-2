# Plan B.3.1 Combat-Idle Boss Portraits Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `BossVotePopup`'s per-column PNG portraits with animated combat-idle sprites rendered via `MonsterModel.CreateVisuals()`, fixing the empty-column bug for Spine-only bosses (currently Ceremonial Beast; more bosses as MegaCrit ships Spine art).

**Architecture:** Three files modify, one helper file new, one test file new. `BossVotePopupOption` DTO swaps `string? PortraitPath` for `Func<Node2D>? VisualsFactory`. `BossVotePatch` adds a synchronous pre-warm pass (with Stopwatch telemetry) and a factory builder closure that captures the canonical `MonsterModel` and calls `CreateVisuals` + animator wiring at popup-show time. `BossVotePopup` replaces its `TextureRect` per-column block with a sized `Control` slot, queues factory invocations into a `_pendingFits` list during column build, then dispatches them as fire-and-forget `ApplyPortraitFit` tasks AFTER the canvas is added to the scene tree (so `GetTree()` is non-null). Occlusion freeze uses `ProcessMode.Disabled` on the slot Control (cascades to NCreatureVisuals children via Inherit) — no Spine API contact. Public surface stays MegaCrit-free; two private static helpers (`GetVisualBounds`, `ApplyScaleAndHue`) localize the typed casts.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (`0Harmony.dll` shipped with game), xUnit 2.9. Tests run via `dotnet test`; full build via `pwsh -File build.ps1`; install via `pwsh -File install.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design-v3.md`](../specs/2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design-v3.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Per-task commits:** each task ends with `git commit` using a `plan-b-3-1/N.M:` prefix. Commits to `main` are pre-authorised for this slice. Every commit ends with the trailer `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` per CLAUDE.md.

---

## File Structure

**New files:**
- `src/Game/Ui/PortraitFit.cs` (~15 LOC) — pure-math fit-scale helper, unit-testable, no Godot tree / no MegaCrit / no TiLog.
- `tests/Game/Ui/PortraitFitTests.cs` (~50 LOC) — 6 `[Theory]` cases against `ComputeFitScale`.

**Modified files:**
- `src/Game/Ui/BossVotePopupOption.cs` (~8 LOC → ~8 LOC) — DTO field swap.
- `src/Game/Ui/BossVotePopup.cs` (~265 LOC → ~340 LOC) — replace `TextureRect` block with sized `Control` + queued fit; add `_portraitSlots` + `_pendingFits` fields; extend `_Process` occlusion handler with `slot.ProcessMode` toggle; add `ApplyPortraitFit` async helper; add `GetVisualBounds` + `ApplyScaleAndHue` private static helpers.
- `src/Game/DecisionVotes/BossVotePatch.cs` (~360 LOC → ~390 LOC) — add `PreWarmBossVisuals` helper, add `BuildVisualsFactory` helper, swap DTO construction from `PortraitPath` to `VisualsFactory`, delete dead `ResolvePortraitPath`. Document main-thread invariant in comment.
- `tests/slay_the_streamer_2.tests.csproj` — add `<Compile Include="..\src\Game\Ui\PortraitFit.cs" />` so the new helper is reachable from tests. (`src/Game/Ui/*.cs` is NOT in any auto-include glob; `BossVotePopup.cs` and `BossVotePopupOption.cs` remain excluded — `PortraitFit.cs` is the surgical inclusion.)
- `notes/06-followups-and-deferred.md` — append B.3.1 acceptance-gate results section after operator validation; add one bullet documenting the `ProcessMode.Disabled` cascade pattern for reuse.
- `README.md` — status section: move B.3.1 from "remaining" to "shipped" after acceptance (optional — only if README tracks v0.2 polish slices).

**No changes:** `BossVoteSeed.cs`, `BossVoteResolver.cs`, the entire `src/Ti/*` tree, settings, vote/session/coordinator/receipt layer, `ModEntry.cs`.

---

## Task 1: Pre-implementation spike — verify v3 assumptions against runtime

**Goal:** Surface any blockers BEFORE writing production code. The spec is decompile-verified for most claims, but a few runtime-only behaviours can't be checked statically.

**Files:**
- Create: `notes/B3-1-spike-2026-05-16.md` (spike notes; not committed unless findings change the plan)

- [ ] **Step 1: Verify `NCreatureVisuals.SpineBody` is non-null when `HasSpineAnimation` is true after `CreateVisuals`**

Read [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Combat/NCreatureVisuals.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Combat/NCreatureVisuals.cs) lines 136-157 (the `_Ready` method). Confirm:
- `SpineBody = new MegaSprite(_body)` runs only if `IsSpineNode` is true.
- `HasSpineAnimation` returns `SpineBody != null` (line 114).
- The factory's call shape `monster.GenerateAnimator(visuals.SpineBody)` is safe when guarded by `if (visuals.HasSpineAnimation)` because `SpineBody` is then definitely non-null.

Record: confirmed / surprises in spike notes.

- [ ] **Step 2: Verify `NCreatureVisuals.Bounds` is non-null after `CreateVisuals`**

Same file: lines 94 (`public Control Bounds { get; private set; }`) and 140 (`Bounds = GetNode<Control>("%Bounds")` in `_Ready`). Confirm `Bounds` is populated synchronously in `_Ready` (no async load). Confirm the `%Bounds` Control exists in every `creature_visuals/<id>.tscn` scene by sampling one or two via Godot editor or by reading scene files.

If `Bounds` is null for any boss's creature scene, the `GetVisualBounds` helper's null-check (`cv.Bounds is not null`) catches it cleanly, but it's worth knowing whether this happens in practice.

Record: assumption holds / one or more bosses have null Bounds (and which).

- [ ] **Step 3: Verify `MonsterModel.AssetPaths.First()` is the combat scene path**

Read [`decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/MonsterModel.cs) lines 68-85 (`AssetPaths` getter). Confirm `span[0] = VisualsPath` and that `VisualsPath` is `creature_visuals/<id_lowercase>` (line 66). Sample one or two boss models (e.g., `CeremonialBeast`, `Doormaker`) and verify their `VisualsPath` resolution.

Record: confirmed / overrides found.

- [ ] **Step 4: Verify `PreloadManager.LoadActAssets` includes monster scenes**

Read [`decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs) — find `AssetPaths`. Confirm it transitively includes `AllPossibleMonsters.SelectMany(m => m.AssetPaths)` or equivalent for all boss-tier encounters. This determines whether our pre-warm typically hits the cache (silent no-op, no vanilla Warn) or actually loads (vanilla logs `Asset not cached: ...`).

Record: act-side preload covers boss monster scenes / does not.

- [ ] **Step 5: Confirm test csproj inclusion strategy**

Open [`tests/slay_the_streamer_2.tests.csproj`](../../../tests/slay_the_streamer_2.tests.csproj). Confirm `src/Game/Ui/*.cs` is not in any auto-include glob. Decide between:
- (a) Single explicit include: `<Compile Include="..\src\Game\Ui\PortraitFit.cs" />`.
- (b) Glob include + excludes: `<Compile Include="..\src\Game\Ui\**\*.cs" />` + `<Compile Remove="..\src\Game\Ui\BossVotePopup.cs" /> <Compile Remove="..\src\Game\Ui\BossVotePopupOption.cs" /> <Compile Remove="..\src\Game\Ui\CardSkipCounterLabel.cs" />`.

Recommend (a) — surgical, no future-creep risk. Record decision.

- [ ] **Step 6: Smoke-check `slot.ProcessMode = Disabled` cascade locally if possible**

Optional pre-implementation check: open Godot, drag any Spine-using `.tscn` into a temporary scene under a `Control` parent. Set the Control's `ProcessMode` to `Disabled` and observe whether the Spine animation freezes. If yes, the spec's plan A is confirmed. If no, plan B (`SetTimeScale(0f)`) is the implementation path; flag for adjustment.

If this can't be done in 10 minutes, skip and rely on gate 7 to catch the failure mode.

Record: confirmed / skipped / discovered failure (and which).

- [ ] **Step 7: Decide whether to commit spike notes**

If all spikes confirm the spec's assumptions, leave the notes file uncommitted (working scratch). If anything contradicts the spec, commit the spike notes AND flag the spec drift to the user before continuing.

```bash
# Only if findings contradict spec:
git add notes/B3-1-spike-2026-05-16.md
git commit -m "$(cat <<'EOF'
plan-b-3-1/1.0: B.3.1 spike — runtime verification of v3 assumptions

[summary of what was found and which spec assumption(s) need revisiting]

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Create `PortraitFit` helper + tests (pure-math TDD)

**Goal:** Carve out the fit-scale math into a Godot-free, MegaCrit-free helper with unit tests. This is the only unit-testable code in B.3.1.

**Files:**
- Create: `src/Game/Ui/PortraitFit.cs`
- Create: `tests/Game/Ui/PortraitFitTests.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add Compile Include for PortraitFit.cs)

- [ ] **Step 1: Add the test-csproj include for `PortraitFit.cs`**

Open [`tests/slay_the_streamer_2.tests.csproj`](../../../tests/slay_the_streamer_2.tests.csproj). Inside the existing `<ItemGroup>` containing the `Compile Include` directives (lines 11-28), add this line after line 16 (`<Compile Include="..\src\Game\DecisionVotes\**\*.cs" />`):

```xml
    <Compile Include="..\src\Game\Ui\PortraitFit.cs" />
```

This surgically includes the new helper without pulling in `BossVotePopup.cs` or `BossVotePopupOption.cs` (which reference Godot types not available to the test project).

- [ ] **Step 2: Write `PortraitFitTests.cs` (failing test)**

Create the file `tests/Game/Ui/PortraitFitTests.cs`:

```csharp
using Godot;
using SlayTheStreamer2.Game.Ui;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Ui;

public class PortraitFitTests {
    [Theory]
    [InlineData(100f, 100f, 256f, 256f, 1f)]     // smaller than slot → no upscale
    [InlineData(256f, 256f, 256f, 256f, 1f)]     // exact match → 1.0
    [InlineData(512f, 256f, 256f, 256f, 0.5f)]   // wider than slot → X-limited
    [InlineData(256f, 512f, 256f, 256f, 0.5f)]   // taller than slot → Y-limited
    [InlineData(0f,   0f,   256f, 256f, 1f)]     // zero bounds → fit=1.0 (Mathf.Max floor)
    [InlineData(-1f,  -1f,  256f, 256f, 1f)]     // negative bounds → fit=1.0 (Mathf.Max floor)
    public void ComputeFitScale_ReturnsExpected(
        float boundsX, float boundsY,
        float slotX, float slotY,
        float expected) {
        var fit = PortraitFit.ComputeFitScale(new Vector2(boundsX, boundsY), new Vector2(slotX, slotY));
        Assert.Equal(expected, fit, precision: 4);
    }
}
```

Note: no `[Collection("TiLog.Sink")]` — `PortraitFit` is pure math, never calls TiLog, no sink contention.

- [ ] **Step 3: Run tests, confirm compilation failure**

Run:
```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~PortraitFitTests"
```

Expected: build failure with `error CS0246: The type or namespace name 'PortraitFit' could not be found`.

- [ ] **Step 4: Create `src/Game/Ui/PortraitFit.cs`**

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

- [ ] **Step 5: Run tests, confirm pass**

Run:
```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~PortraitFitTests"
```

Expected: `Passed!  - 6 tests passed`.

- [ ] **Step 6: Run full test suite to confirm no regressions**

Run:
```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests still pass; total test count increases by 6.

- [ ] **Step 7: Commit**

```bash
git add src/Game/Ui/PortraitFit.cs tests/Game/Ui/PortraitFitTests.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "$(cat <<'EOF'
plan-b-3-1/2.1: add PortraitFit pure-math helper + xunit cases

Carves the fit-scale calculation out of the (untestable) BossVotePopup
column-build path. 6 [Theory] cases cover normal, edge (zero / negative
bounds), and degenerate inputs. Tests source-reference the helper via
new explicit Compile Include in tests csproj (src/Game/Ui/* is excluded
by default; this is the surgical inclusion for the unit-testable carve-out).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: DTO field swap in `BossVotePopupOption`

**Goal:** Swap `string? PortraitPath` for `Func<Node2D>? VisualsFactory` so the popup can lazy-instantiate visuals at `Show()` time without leaking on dispatcher races.

**Files:**
- Modify: `src/Game/Ui/BossVotePopupOption.cs`

- [ ] **Step 1: Read the current file**

Confirm current contents match the spec's described shape: `internal sealed record BossVotePopupOption(int Index, string Title, string? PortraitPath);`.

- [ ] **Step 2: Replace the record**

Replace the entire contents of `src/Game/Ui/BossVotePopupOption.cs` with:

```csharp
using System;
using Godot;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Game-free DTO for boss-vote popup column data. BossVotePatch maps
/// MegaCrit.Sts2 EncounterModel → BossVotePopupOption before constructing
/// the popup, so BossVotePopup never references MegaCrit types at the
/// public interface level.
///
/// VisualsFactory is invoked once per column at popup.Show() time on the
/// Godot main thread. The returned Node2D is parented under the column's
/// portrait slot. Lazy invocation: if Show() is never called (e.g., the
/// session is cancelled mid-construction), no NCreatureVisuals instances
/// are created — nothing to leak.
/// </summary>
internal sealed record BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory);
```

- [ ] **Step 3: Verify build (mod project)**

Run:
```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build fails with errors in `BossVotePopup.cs` (uses `PortraitPath`) and `BossVotePatch.cs` (constructs DTOs with `PortraitPath: ...`). This is expected — Tasks 4 and 5 fix those call sites. Confirm the error count is bounded (5-10 lines, all in those two files).

Do NOT commit yet — the working tree is in a broken intermediate state. Tasks 4 and 5 land in the same commit chain.

---

## Task 4: Update `BossVotePatch` — pre-warm helper + factory builder + DTO construction

**Goal:** Add the synchronous pre-warm pass with Stopwatch telemetry, the factory closure builder, and update the DTO construction site. Delete the dead `ResolvePortraitPath` helper.

**Files:**
- Modify: `src/Game/DecisionVotes/BossVotePatch.cs`

- [ ] **Step 1: Read the file to locate the modification sites**

Specifically identify:
- The candidate sampling + DTO construction block (the spec references it around `ResolvePortraitPath`'s call site at `BossVotePatch.cs:304-308`).
- The `ResolvePortraitPath` method definition (`BossVotePatch.cs:341-352`).
- The using directives at the top (need to add `System.Diagnostics` for Stopwatch if not already imported).

- [ ] **Step 2: Add `using System.Diagnostics;`**

Add to the using directives at the top of `BossVotePatch.cs` if not already present (check first — many files have it). Place in alphabetical order with the other `using System.*` directives.

- [ ] **Step 3: Add `PreWarmBossVisuals` helper**

Add this as a `private static` method inside the `BossVotePatch` class, near the other helpers (suggest: just below or above `ResolvePortraitPath`, which Step 6 will delete):

```csharp
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
            // AssetPaths.First() == VisualsPath (decompile-verified observation).
            // CreateVisuals reads VisualsPath directly anyway, so this prime is
            // best-effort — if ordering ever changes, factory time picks up the slack.
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

- [ ] **Step 4: Add `BuildVisualsFactory` helper**

Add this immediately after `PreWarmBossVisuals`:

```csharp
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
            // Verified shape: NCreatureVisuals.SetUpSkin(MonsterModel) at NCreatureVisuals.cs:178.
            // Defensive ?. on GetAnimationState() — should be non-null when HasSpineAnimation is true,
            // but guard for potatoes.
            visuals.SpineBody.GetAnimationState()?.SetAnimation("idle_loop");
        }
        return visuals;
    };
}
```

- [ ] **Step 5: Update the DTO construction site**

Locate the block that currently looks like (around `BossVotePatch.cs:304-309`):

```csharp
        // Map EncounterModel → BossVotePopupOption DTOs (keeps popup MegaCrit-free).
        var dtos = sample.Select((e, i) => new BossVotePopupOption(
            Index: i,
            Title: e.Title.GetFormattedText(),
            PortraitPath: ResolvePortraitPath(e))).ToList();
        var labels = dtos.Select(d => d.Title).ToList();
```

Replace with:

```csharp
        // IMPORTANT: PreWarmBossVisuals + factory construction + popup construction + popup.Show()
        // must all run synchronously on the Godot main thread BEFORE the first `await` in this
        // method. Godot resource loading and scene instantiation are main-thread-only. Do not
        // move any of these below an `await` without marshalling via IMainThreadDispatcher.
        PreWarmBossVisuals(sample);

        // Map EncounterModel → BossVotePopupOption DTOs (keeps popup MegaCrit-free).
        var dtos = sample.Select((e, i) => new BossVotePopupOption(
            Index: i,
            Title: e.Title.GetFormattedText(),
            VisualsFactory: BuildVisualsFactory(e))).ToList();
        var labels = dtos.Select(d => d.Title).ToList();
```

The `sample` variable is the candidate list and should already be `IReadOnlyList<EncounterModel>` or equivalent. If it's an `IEnumerable<EncounterModel>`, materialize it with `.ToList()` before passing to `PreWarmBossVisuals` (it iterates twice — once for the pre-warm loop, the existing code iterates it again for `Select`).

- [ ] **Step 6: Delete `ResolvePortraitPath`**

Find and delete the entire method (around `BossVotePatch.cs:341-352`):

```csharp
private static string? ResolvePortraitPath(EncounterModel boss) {
    try {
        var basePath = boss.BossNodePath;
        if (string.IsNullOrEmpty(basePath)) return null;
        return basePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? basePath
            : basePath + ".png";
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] BossNodePath access threw: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 7: Verify build (mod project)**

Run:
```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build still fails on `BossVotePopup.cs` (still uses `opt.PortraitPath` etc.). The number of error lines should be smaller than after Task 3 (the patch errors are gone; popup errors remain). Task 5 fixes the popup.

Do NOT commit yet — still in broken intermediate state.

---

## Task 5: Rewrite `BossVotePopup` column build + occlusion handler + helpers

**Goal:** Replace the `TextureRect`-based portrait load with a sized `Control` slot + factory-queued fit pass. Add `ProcessMode.Disabled` cascade for occlusion freeze. Add private static type helpers.

**Files:**
- Modify: `src/Game/Ui/BossVotePopup.cs`

This is the largest single change in the slice. Break into surgical edits.

- [ ] **Step 1: Add `using System.Threading.Tasks;` if not present**

Check the file's using directives. If `System.Threading.Tasks` is not imported, add it. Also confirm `System`, `System.Collections.Generic`, `System.Text`, `Godot`, `SlayTheStreamer2.Ti.Internal`, `SlayTheStreamer2.Ti.Voting`. Add `using MegaCrit.Sts2.Core.Nodes.Combat;` so the private static helpers can name `NCreatureVisuals`.

- [ ] **Step 2: Add the `PortraitSlotSize` constant**

Near the existing `LAYER_INDEX` constant (around line 24), add:

```csharp
/// <summary>
/// Slot size for each per-column animated portrait. 384×384 was chosen
/// to give combat-idle sprites (~500–800px native) reasonable visual
/// impact at fit-scale ≈ 0.5–0.77. Bumped from 256×256 (B.3 era PNG
/// portraits) after 9-reviewer consensus that 256² felt cramped.
/// </summary>
private static readonly Vector2 PortraitSlotSize = new(384, 384);
```

- [ ] **Step 3: Add the new private fields**

Find the existing `_tallyLabels` field (around `BossVotePopup.cs:50`). Add immediately after:

```csharp
/// <summary>
/// Slot Controls (one per column) — Godot type only, no MegaCrit refs.
/// Iterated by the _Process occlusion handler to toggle ProcessMode
/// (Disabled when occluded; Inherit otherwise). Setting Disabled on
/// the slot cascades to NCreatureVisuals children via Godot's
/// ProcessMode.Inherit semantics, freezing Spine playback.
/// </summary>
private readonly List<Control> _portraitSlots = new();

/// <summary>
/// Pairs of (slot, factory-produced visuals) queued during column build.
/// Dispatched as fire-and-forget ApplyPortraitFit tasks AFTER the canvas
/// layer is added to the scene tree, so GetTree() is non-null inside the
/// async helper. Cleared after dispatch.
/// </summary>
private readonly List<(Control Slot, Node2D Visuals)> _pendingFits = new();
```

- [ ] **Step 4: Replace the column-build per-column block**

Find the per-column body (around `BossVotePopup.cs:127-176`). The block currently builds `Portrait` (TextureRect) + `Name` + `Tally`. Replace JUST the `Portrait` block (lines starting at `// Portrait — defensive load.` up to and including `col.AddChild(portrait);`) with:

```csharp
            // Portrait slot: sized Control parents the factory-produced Node2D.
            // ClipContents = true belts any sprite that draws beyond Bounds.
            // ProcessMode is Inherit by default; the occlusion handler toggles
            // Inherit↔Disabled to drive the Spine-playback freeze via cascade.
            var slot = new Control {
                Name = "PortraitSlot",
                CustomMinimumSize = PortraitSlotSize,
                ClipContents = true,
            };
            col.AddChild(slot);
            _portraitSlots.Add(slot);

            if (opt.VisualsFactory is not null) {
                try {
                    var visuals = opt.VisualsFactory.Invoke();
                    slot.AddChild(visuals);
                    // Queue — actual measurement/fit happens after the canvas is
                    // added to SceneTree.Root (see end of Show()), so GetTree()
                    // is non-null when ApplyPortraitFit's await fires.
                    _pendingFits.Add((slot, visuals));
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][boss-vote] visuals factory threw for column {opt.Index}: {ex.Message}");
                }
            }
```

Keep the `Name` label block and the `Tally` block unchanged.

- [ ] **Step 5: Dispatch `_pendingFits` after tree-attach**

Find the existing end of `Show()`:

```csharp
        tree.Root.AddChild(_canvasLayer);
        _canvasLayer.AddChild(this);   // popup Control is parented under the layer
    }
```

Replace with:

```csharp
        tree.Root.AddChild(_canvasLayer);
        _canvasLayer.AddChild(this);   // popup Control is parented under the layer

        // Now that the popup is in the tree, GetTree() returns non-null.
        // Dispatch queued fits as fire-and-forget tasks; each has its own
        // try/catch so unobserved exceptions don't surface unpredictably.
        foreach (var (slot, visuals) in _pendingFits) {
            _ = ApplyPortraitFit(slot, visuals);
        }
        _pendingFits.Clear();
    }
```

- [ ] **Step 6: Add the `ApplyPortraitFit` async helper**

Add this method to the class. Suggested location: just after `Show()` (or before `_Process`, wherever fits the file's existing flow):

```csharp
/// <summary>
/// Defers Bounds.Size measurement by one process frame (Spine atlas
/// measurement is typically lazy) and applies a fit-scale + center
/// position to the visuals inside the slot. Fire-and-forget; wraps the
/// body in try/catch so exceptions are logged, not unobserved.
/// </summary>
private async Task ApplyPortraitFit(Control slot, Node2D visuals) {
    try {
        // Safe: this runs only AFTER Show() added the canvas to the tree.
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
```

- [ ] **Step 7: Add the private static type-bridging helpers**

Add these after `ApplyPortraitFit`:

```csharp
// Private static helpers: pattern-match-and-cast Node2D → NCreatureVisuals locally.
// The cast never escapes the popup's public API. A TI-extraction fork would replace
// these two helper bodies with the new host game's equivalents. Verified APIs:
// NCreatureVisuals.Bounds (Control) — populated in _Ready, NCreatureVisuals.cs:140.
// NCreatureVisuals.SetUpSkin(MonsterModel) — at NCreatureVisuals.cs:178.
// NCreatureVisuals.SetScaleAndHue(float scale, float hue) — at NCreatureVisuals.cs:190.
private static Vector2 GetVisualBounds(Node2D visuals) {
    if (visuals is NCreatureVisuals cv && cv.Bounds is not null) return cv.Bounds.Size;
    return Vector2.Zero;
}

private static void ApplyScaleAndHue(Node2D visuals, float scale) {
    if (visuals is NCreatureVisuals cv) cv.SetScaleAndHue(scale, 0f);
}
```

- [ ] **Step 8: Extend the `_Process` occlusion handler**

Find the existing occlusion block in `_Process` (around `BossVotePopup.cs:211-219`). Replace the block:

```csharp
        if (_canvasLayer is not null) {
            bool occluded = false;
            try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
            catch { /* probe must never crash _Process */ }
            if (_canvasLayer.Visible == occluded) {
                _canvasLayer.Visible = !occluded;
            }
            if (occluded) return;   // skip rebuilding label text while hidden
        }
```

With:

```csharp
        if (_canvasLayer is not null) {
            bool occluded = false;
            try { occluded = _isOccludingOverlayVisible?.Invoke() ?? false; }
            catch { /* probe must never crash _Process */ }
            if (_canvasLayer.Visible == occluded) {
                _canvasLayer.Visible = !occluded;
                // REUSABLE PATTERN — Spine freeze via Godot's ProcessMode cascade:
                //   ProcessMode.Disabled on a parent Control halts _Process on all
                //   children whose ProcessMode is Inherit (the default). For
                //   Spine-rendered children, this freezes playback without touching
                //   MegaSpine's animation state.
                //   Per CLAUDE.md Tier 4: SceneTree.Paused is never toggled by
                //   StS2's pause menu, so Godot's native Pausable/WhenPaused modes
                //   don't help — drive the freeze from our occlusion probe instead.
                //   See Plan B in v3 spec Open Risks if gate 7 reveals this cascade
                //   doesn't freeze MegaSpine playback.
                foreach (var slot in _portraitSlots) {
                    slot.ProcessMode = occluded ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
                }
            }
            if (occluded) return;   // skip rebuilding label text while hidden
        }
```

- [ ] **Step 9: Verify build**

Run:
```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: clean build, zero errors. If anything fails, fix in place before proceeding — do not commit a broken build.

- [ ] **Step 10: Run full test suite**

Run:
```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass + `PortraitFitTests` (6) pass. Total count unchanged from Task 2.

- [ ] **Step 11: Commit (Tasks 3 + 4 + 5 land together)**

The DTO change (Task 3), patch update (Task 4), and popup rewrite (Task 5) form a single coherent unit — they cannot land in separate commits without one of them being in a broken intermediate state. Commit all three together:

```bash
git add src/Game/Ui/BossVotePopupOption.cs src/Game/Ui/BossVotePopup.cs src/Game/DecisionVotes/BossVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-3-1/5.1: swap BossVotePopup portraits from PNG to combat-idle CreateVisuals

BossVotePopupOption: PortraitPath → VisualsFactory (Func<Node2D>?).
BossVotePatch: add PreWarmBossVisuals (Stopwatch telemetry, per-candidate
  try/catch) + BuildVisualsFactory (closure capturing canonical MonsterModel,
  calls CreateVisuals + GenerateAnimator + SetUpSkin + SetAnimation idle_loop
  if HasSpineAnimation). Delete dead ResolvePortraitPath.
BossVotePopup: replace TextureRect column block with sized Control slot
  (384×384, ClipContents = true). Queue (slot, visuals) into _pendingFits
  during column build, dispatch ApplyPortraitFit fire-and-forget AFTER the
  canvas is added to SceneTree.Root (so GetTree() is non-null inside the
  async helper). _Process occlusion handler now also toggles slot.ProcessMode
  (Inherit↔Disabled) to cascade-freeze Spine playback.

Public API of BossVotePopup remains MegaCrit-free; two private static helpers
(GetVisualBounds, ApplyScaleAndHue) localize the NCreatureVisuals casts.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Full build + install

**Goal:** Build the mod, copy to Steam mods folder, prepare for operator validation.

- [ ] **Step 1: Run the full build pipeline**

```bash
pwsh -File build.ps1
```

Expected: builds `dist/` cleanly (dotnet publish + dotnet test + assemble). All tests pass. No build warnings on the new code (CRLF/LF warnings on Windows are expected and harmless per CLAUDE.md Tier 4).

If anything fails: do NOT proceed to install. Fix in place and re-run.

- [ ] **Step 2: Install to Steam mods folder**

```bash
pwsh -File install.ps1
```

Expected: copy-only step from `dist/` to Steam mods folder. Should complete in a few seconds.

- [ ] **Step 3: Verify the installed version**

Note the current git HEAD:
```bash
git log -1 --format=%H
```

Launch StS2. After it loads, check `%APPDATA%\SlayTheSpire2\logs\godot.log` for the mod version line. Confirm it matches the HEAD hash from above. If it doesn't match, `dist/` is stale — re-run `build.ps1` and `install.ps1`.

---

## Task 7: Operator validation — visual correctness (gates 1–6)

**Goal:** Walk through one or more runs to confirm the animated portraits render correctly across the boss roster.

**Reference:** v3 spec § Operator validation checklist, gates 1–6.

- [ ] **Step 1: Validate Act 1 boss vote rendering (gate 1, gate 3, gate 5, gate 6)**

Start a new run on any character. Use DevConsole (backtick) to jump to chest: `act 1` then progress to the chest room. Click Proceed. Confirm:
- 3 portrait columns appear with animated combat sprites (not static, not empty).
- Names render under each portrait.
- No visual overflow into neighboring columns.
- Sprites are roughly centered in their 384×384 slots.
- Visual impact is acceptable (sprites are large enough to read animation detail).

Record observed bosses and any visual oddities.

- [ ] **Step 2: Specifically validate Ceremonial Beast renders (gate 2 — the bug fix)**

Continue advancing acts (DevConsole `act 2`, `act 3` as needed) until a Ceremonial Beast vote is reached. May require multiple runs or DevConsole to manipulate the boss pool. Confirm Ceremonial Beast renders as a visible animated sprite, NOT an empty column.

If still empty, the bug fix has failed — stop and diagnose before continuing.

- [ ] **Step 3: Validate all placeholder bosses render (gate 3 regression check)**

Through one or more runs, observe at least these 9 placeholder bosses in boss-vote popups:
- Soul Fysh, Vantom, The Kin, Lagavulin Matriarch, Waterfall Giant, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject.

Confirm each renders as a visible animated sprite. Use DevConsole `act 1` / `act 2` / `act 3` to reach votes; if a specific boss's vote is hard to reach, accept "render in combat" as a substitute proxy (combat uses the same `CreateVisuals` path).

Record any missing or visually-broken bosses.

- [ ] **Step 4: Check godot.log for unexpected boss-vote warnings**

After each run, scan `%APPDATA%\SlayTheSpire2\logs\godot.log` for `[boss-vote]` lines. Expected:
- `pre-warm: 3/3 candidates in Xms` (Info) — once per vote.
- Nothing else under normal flow.

Unexpected warns to investigate:
- `Bounds.Size is zero after ProcessFrame yield` — measurement-race fired; sprite still renders via ClipContents fallback but visual may be cropped.
- `multi-monster encounter ...` — should not fire under current StS2 build.
- `visuals factory threw ...` — investigate the bracketed message.

Record findings.

---

## Task 8: Operator validation — lifecycle correctness (gates 7–12)

**Goal:** Validate pause-menu freeze, dev-console occlusion, run-abandonment, save-quit, PhobiaMode.

- [ ] **Step 1: Pause-menu freeze validation (gate 7 — critical)**

During an Act 1 boss vote, with a clearly-animating boss visible:
1. Note the current animation pose (e.g., breathing in / breathing out).
2. Open the pause menu (ESC).
3. Wait 3 seconds.
4. Close the pause menu.
5. **Verify the animation does NOT visibly advance during the pause.** Allow one frame of resume catch-up — if the pose on resume is roughly where it was at pause time, the cascade is working. If the pose has clearly advanced (e.g., breathing cycle has progressed), the ProcessMode cascade has failed.

If failed: switch to Plan B per Open Risk #4 (replace `slot.ProcessMode` toggle with per-visual `SetTimeScale(0f) / SetTimeScale(1f)` via a new private static helper). Stop and update the plan + spec.

- [ ] **Step 2: Dev-console occlusion (gate 8)**

Mid-vote, open the dev console (backtick). Confirm:
- Popup hides cleanly.
- Console is fully interactive.
- Close console (backtick again).
- Popup re-appears with smooth animation resume (no NRE, no glitch).

- [ ] **Step 3: Run abandonment mid-vote (gate 9)**

Mid-vote, open pause menu → Give Up → confirm. Confirm:
- Popup frees promptly (no orphaned CanvasLayer overlaying the game-over screen).
- Game-over screen is reachable and interactive.

- [ ] **Step 4: Save-quit mid-vote (gate 10)**

Mid-vote, open pause menu → Save & Quit. Confirm:
- No crash.
- No orphaned popup.
- No invalid boss-swap state in the save file.
- Click Continue from the main menu → run restores in a valid state per B.3's existing behavior (boss-swap idempotency via `_lastSwappedBossId`; the popup is NOT restored — vote restarts per B.3's existing path).

- [ ] **Step 5: PhobiaMode toggle (gate 11)**

Open the settings menu and toggle PhobiaMode on mid-vote (if reachable from the pause menu during the vote — if not, validate before/after the vote instead). Confirm:
- No crash.
- For monsters with phobia-mode body variants in their `.tscn`, the visual updates.
- For monsters without variants (most), the visual remains unchanged.

- [ ] **Step 6: Multi-monster encounter check (gate 12 — typically a no-op)**

If a multi-monster encounter has been added to StS2 since the spec was written (unlikely for boss-tier), confirm the popup renders the first monster and a `[boss-vote] encounter X has N monsters` Warn line appears in `godot.log`.

If no multi-monster encounter exists in the tested build, skip this gate with a note.

---

## Task 9: Operator validation — coverage + hardware envelope (gates 13–20)

**Goal:** Confirm coverage across all 3 acts + edge cases + resolution coverage + hardware envelope.

- [ ] **Step 1: Act 1 / Act 2 / Act 3 coverage (gates 13–15)**

Confirm at least one boss vote per act has been validated. Use DevConsole `act 1`, `act 2`, `act 3` if needed. Note: with the resolution coverage gate (20) bundled in, this naturally interleaves window-size testing.

- [ ] **Step 2: Golden Compass re-vote (gate 16)**

In a run where chat has already voted on a boss, use the Golden Compass relic (or DevConsole equivalent) to trigger a re-vote in the same act. Confirm:
- The popup appears again.
- All 3 columns render correctly.
- The re-vote result correctly swaps the boss (B.3 idempotency unchanged).

If Golden Compass isn't reachable in the tested build, skip with a note.

- [ ] **Step 3: Pre-warm Stopwatch log baseline (gate 17)**

After any single Act 1 boss vote, locate the `[boss-vote] pre-warm: 3/3 candidates in Xms` line in `godot.log`. Record X for project notes. Typical expectation: <5 ms when act-side preload (`PreloadManager.LoadActAssets`) has already cached the monster scenes; up to ~500 ms if it hasn't.

- [ ] **Step 4: Cold-load simulation (gate 18)**

Temporarily edit `src/Game/DecisionVotes/BossVotePatch.cs` to insert `System.Threading.Thread.Sleep(500);` immediately after the `_ = PreloadManager.Cache.GetScene(scenePath);` line inside `PreWarmBossVisuals`. Rebuild + reinstall (`pwsh -File build.ps1 && pwsh -File install.ps1`). Trigger a boss vote and observe:
- Simulated ~1.5s aggregate hitch (3 candidates × 500 ms) between Proceed click and popup appearance.
- Is this acceptable UX? (Click → ~1.5s pause → popup appears with smooth animation.)

If acceptable: revert the sleep, rebuild, reinstall. Document the data point.
If unacceptable: revert the sleep, document the escalation plan (move pre-warm to chest-room-enter via `PreloadManager.LoadActAssets` postfix on `NTreasureRoom._Ready`), and add to `notes/06-followups-and-deferred.md` as a v0.2 polish item.

- [ ] **Step 5: Resolution coverage during normal testing (gate 20)**

Confirm the popup renders correctly across:
- (a) The small testing window typically used during development.
- (b) 1440p ultrawide fullscreen.
- (c) At least one intermediate window size encountered during natural window resize.

This is expected to happen organically during gates 1–14 testing. Flag any case where:
- A sprite extends past its column.
- Anchoring breaks (e.g., column slots fly off-screen).
- The popup's overall layout breaks at extreme aspect ratios.

- [ ] **Step 6: Optional second-hardware validation (gate 19)**

If a second machine (Steam Deck, older laptop, spinning HDD) is accessible: validate first-vote-of-run does not stutter visibly after popup appears. If inaccessible: gate 18's cold-load simulation is the substitute data point — no separate validation required.

---

## Task 10: Acceptance finalisation — notes/06 + README + tag

**Goal:** Document the acceptance-gate results, capture the reusable freeze pattern in followups, and tag the slice complete.

**Files:**
- Modify: `notes/06-followups-and-deferred.md`
- Modify: `README.md` (optional, only if it tracks v0.2 polish slices)

- [ ] **Step 1: Append acceptance-gate results to `notes/06-followups-and-deferred.md`**

Add a new section at the appropriate location (after the existing B.3 section, before any deferred items):

```markdown
## B.3.1 — Combat-idle boss portraits (shipped YYYY-MM-DD)

**Acceptance gates (all green):**

[Bulleted list of gates 1–23 with outcomes. For each: ✅ pass with brief notes, or ⚠️ note + follow-up reference.]

**Pre-warm timing baseline:** `[boss-vote] pre-warm: 3/3 candidates in Xms` typical on dev machine (insert X).

**Cold-load simulation (gate 18):** [Acceptable / Escalate to v0.2 polish].

**Reusable pattern unlocked:**

ProcessMode.Disabled cascade for Spine playback freeze. Any future mod UI
rendering MegaCrit creatures + needing pause-aware freeze can reuse the
same pattern: set ProcessMode.Disabled on the slot Control during occlusion,
Inherit otherwise. Cascades to NCreatureVisuals children via Godot's
ProcessMode.Inherit semantics without touching MegaSpine APIs. See
BossVotePopup._Process for reference implementation. Driver: the occlusion
probe (currently _isOccludingOverlayVisible in BossVotePatch) — SceneTree.Paused
is NEVER toggled by StS2's pause menu, so Godot's native pause modes don't help.
```

- [ ] **Step 2: Update README.md (optional)**

If `README.md` has a status table or feature inventory section, move B.3.1 from "planned" or "remaining" to "shipped." If not, skip.

- [ ] **Step 3: Commit notes + README**

```bash
git add notes/06-followups-and-deferred.md README.md
git commit -m "$(cat <<'EOF'
plan-b-3-1/10.1: B.3.1 acceptance-gate results + ProcessMode pattern note

All 23 operator-validation gates green. Ceremonial Beast and other Spine-only
bosses now render animated combat-idle portraits in the boss-vote popup
(previously empty columns). Placeholder bosses render correctly via the same
code path (no regression).

Notes/06 updated with gate results, pre-warm timing baseline, and a one-bullet
description of the ProcessMode.Disabled cascade pattern for reuse by future
mod UI surfaces.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: Tag the slice**

```bash
git tag plan-b-3-1-complete
git log --oneline -10
```

Confirm `plan-b-3-1-complete` points at the HEAD commit.

---

## Self-review checklist

Before considering the plan complete:

**Spec coverage:**
- v3 spec § Code changes: Tasks 2, 3, 4, 5 cover all four files.
- v3 spec § Data flow & lifecycle: Tasks 4 (pre-warm) and 5 (queue + dispatch) match the flow exactly.
- v3 spec § Error handling table: Task 5's `ApplyPortraitFit` try/catch, Task 4's per-candidate try/catch + multi-monster Warn, Task 5's defensive cast helpers — all cover the spec's failure modes.
- v3 spec § Testing § Operator validation: Tasks 7–9 cover all 23 gates. Task 10 wraps with notes/06.
- v3 spec § Open risks: Task 8 Step 1 has the explicit fallback procedure if gate 7 fails (Plan B).

**Placeholder scan:**
- No "TBD" / "TODO" / "implement later" in any step.
- Every code step contains the actual code.
- Every command step has the exact command and expected output.

**Type consistency:**
- `BossVotePopupOption(int Index, string Title, Func<Node2D>? VisualsFactory)` — consistent in Tasks 3, 4, 5.
- `PreWarmBossVisuals(IReadOnlyList<EncounterModel>)` — consistent in Task 4.
- `BuildVisualsFactory(EncounterModel) → Func<Node2D>?` — consistent in Task 4 + factory invocation in Task 5.
- `ApplyPortraitFit(Control slot, Node2D visuals)` — consistent in Task 5 column build + dispatch + helper definition.
- `GetVisualBounds(Node2D) → Vector2`, `ApplyScaleAndHue(Node2D, float)` — defined in Task 5 Step 7; referenced in Task 5 Step 6 (`ApplyPortraitFit`).
- `PortraitFit.ComputeFitScale(Vector2 boundsSize, Vector2 slotSize) → float` — defined in Task 2, used in Task 5.
- `PortraitSlotSize` (private static readonly Vector2) — defined in Task 5 Step 2, used in Step 4 + Step 6.
- `_portraitSlots` (List<Control>) — defined in Task 5 Step 3, populated in Step 4, iterated in Step 8.
- `_pendingFits` (List<(Control, Node2D)>) — defined in Task 5 Step 3, populated in Step 4, dispatched + cleared in Step 5.
