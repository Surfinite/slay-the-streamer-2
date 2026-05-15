# Meta-Review — Plan B.3.1 Combat-idle boss portraits

**Inputs**: 9 reviews of `2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design.md` (against the companion `-CONTEXT.md`).
**Synthesis date**: 2026-05-15.

---

## A.1 Review Summary Table

| Reviewer | Sentiment | Key Focus Areas | Unique Insight |
|---|---|---|---|
| R1 | Mixed-critical | Type-check error, seam contradiction, positioning, Bounds.Size timing, save-quit gate | Bestiary's positioning model differs from spec's geometric-center approach |
| R2 | Mostly positive | Bounds.Size, SetTimeScale validation, pre-warm logging, column size | `SetAnimation("idle_loop")` on un-occlude to avoid mid-frame resume |
| R3 | Mostly positive | Seam framing, SetTimeScale demotion, column size, pre-warm measurement | "Either commit to seam (wrapper) or formally rewrite Rule #6" |
| R4 | Mostly positive | Bounds.Size, column size, const-bool toggle, mutability of MonsterModel | `const bool _freezeSpineOnOcclude = true` for trivial fallback |
| R5 | Strongly positive | Validates design choices, low-key concerns | Adds timing measurement as operator-validation step |
| R6 | Mixed-positive | Bounds.Size, HasSpineAnimation path, threaded loading, AssetPaths contract | Placeholder→Spine visual continuity concern (PNG ≠ combat sprite) |
| R7 | Critical-mixed | Seam erosion, pre-warm blocking, multi-monster blind spot | "Synchronous pre-warm violates never-block-main-thread" (misread) |
| R8 | Critical-mixed | Bounds.Size (CRITICAL), seam adapter node, multi-monster manager-entity | **`ProcessMode.Disabled` as Godot-native freeze (transformative)** |
| R9 | Mostly positive | Bounds.Size, smoothness check, cold-load simulation, defends private field | `Thread.Sleep(500)` simulation as cheap pre-ship measurement |

Overall sentiment: **positive direction, polish-tier concerns, no fundamental rework needed.** 7/9 reviewers either explicitly endorse the core design or land mostly-positive; only R7/R8 land critical-mixed, and both critiques resolve into the same set of fixable concerns the more-positive reviewers also raised.

---

## A.2 Consensus Points (≥2 reviewers)

Ranked by reviewer count and severity:

1. **`Bounds.Size = (0,0)` race must be addressed in the spec, not deferred** — **6 reviewers** (R1, R2, R4, R6, R8, R9). Reality-check: confirmed. The decompile shows `NCreatureVisuals.Bounds` is a `Control` resolved in `_Ready()`, and the Spine atlas measurement that determines Size is typically lazy. `NBestiary.SelectMonster` reads `Bounds.Size.Y` immediately after `AddChild`, but Bestiary's container is laid out *before* the visual is added — the popup's columns are not. **Must-fix in v2.**

2. **256×256 column size is too small for combat-idle sprites** — **9 reviewers** (all of them at varying intensities). Reality-check: combat sprites are ~500–800 px tall; at 0.32 fit-scale, animation detail is largely lost. The spec's own Key Question 3 surfaces this without resolving it. **Must-fix in v2: bump to 384×384 via named constant.**

3. **Spine `SetTimeScale(0f)` is unverified usage** — **8 reviewers** (R1, R2, R3, R4, R5, R7, R8, R9). Reality-check: I haven't found prior MegaCrit usage in the decompile. R3/R5/R7 want it demoted; R2/R4 want a toggle. **Superseded by ProcessMode.Disabled switch (see Conflicts §A.5).**

4. **Pre-warm needs measurement / logging** — **5 reviewers** (R2, R4, R5, R6, R9). Reality-check: spec admits no measurement was taken. Stopwatch logging is trivially cheap. **Must-fix in v2.**

5. **Multi-monster encounters need at least a `TiLog.Warn`** — **4 reviewers** (R1, R2, R7, R8). Reality-check: confirmed single-monster across current act bosses (CeremonialBeastBoss, DoormakerBoss, SoulFyshBoss, all use `ReadOnlySingleElementList<MonsterModel>`). One log line costs nothing and adds observability for future MegaCrit changes. **Must-fix in v2.**

6. **Seam-language contradiction between Goals and Architecture** — **4 reviewers** (R1, R3, R7, R8). Reality-check: confirmed. Goals says "BossVotePopup stays MegaCrit-free"; Architecture admits the private field. **Resolved by ProcessMode.Disabled switch (the field becomes unnecessary).**

7. **Type-check error in BossVotePopup column-build snippet** — **2 reviewers** (R1, R9). Reality-check: confirmed. `Func<Node2D>?` returns `Node2D`; the snippet accesses `.Bounds`, `.SetScaleAndHue`, and adds to `List<NCreatureVisuals>` without a cast. **Must-fix; also resolved by ProcessMode.Disabled switch (eliminates the `List<NCreatureVisuals>` field, leaving only the public Node2D surface).**

8. **`slot.CustomMinimumSize * 0.5f` should be `slot.Size * 0.5f`** — **2 reviewers** (R1, R3). Reality-check: confirmed. HBoxContainer with `SizeFlagsHorizontal = ExpandFill` will grow the slot beyond CustomMinimumSize. **Should-fix.**

9. **Wrapper class vs private field debate** — **CONFLICT** (R7/R8 push wrapper; R5/R9 explicitly defend private field; R3 says "pick one and document"; R4 neutral). **Resolved by ProcessMode.Disabled (no concession needed).**

10. **Pre-warm timing — Variant B vs C (chest-room-enter)** — **5 reviewers neutral-to-positive on B** (R2, R5, R6, R9 cautiously, R3); **2 reviewers prefer C as primary** (R7, R8). Reality-check: Variant B is the conservative ship; Variant C is the natural follow-up if measurement shows hitch. **Stay with B; Variant C goes to Consider list.**

---

## A.3 Outlier Points (single reviewer, with merit assessment)

| Reviewer | Point | Merit | Disposition |
|---|---|---|---|
| R1 | Save-quit gate 10 likely wrong (B.3 probably doesn't restore mid-vote popup) | **Valid.** B.3's `_lastSwappedBossId` is for boss-state idempotency, not popup restoration. The gate as written implies more than B.3 actually does. | Must-do (reword gate). |
| R1 | Pre-warm main-thread invariant must be documented | **Valid.** Without an explicit "before first await" invariant, a future refactor could break the assumption silently. | Must-do (one sentence in spec). |
| R1 | Bestiary positioning model differs from spec's geometric center | **Valid.** Bestiary uses `(0, Bounds.Size.Y * 0.5f)` — different intent. | Should-do (note and validate visually). |
| R3 | `IPortraitVisuals` micro-interface as middle ground | **Superseded** by ProcessMode.Disabled. The interface is no longer needed. | Reject. |
| R4 | Verify `GenerateAnimator`/`SetUpSkin` don't mutate `MonsterModel` | **Decompile-checked.** `GenerateAnimator(MegaSprite)` writes only to the MegaSprite parameter; `SetUpSkin(NCreatureVisuals)` writes to the visuals parameter. Neither mutates `MonsterModel`. Closure capture is safe. | No change (already correct, worth a comment). |
| R4 | `const bool _freezeSpineOnOcclude = true` toggle | **Superseded** by ProcessMode.Disabled. No toggle needed. | Reject. |
| R5 | Add `Time.get_ticks_msec()` log during validation | Subsumed by Stopwatch logging (consensus item 4). | Must-do (already). |
| R6 | Placeholder→Spine visual continuity concern (combat sprite ≠ map portrait) | **Valid observation.** Placeholder bosses' combat sprites may look different from their map PNGs. This is a deliberate visual change. | Should-do (note in spec; not a regression, an upgrade). |
| R6 | `AssetPaths.First()` ordering is decompile observation, not contract | **Decompile-confirmed.** `MonsterModel.AssetPaths` does `span[0] = VisualsPath` — VisualsPath is first by construction. Worth documenting as a verified observation. | Should-do (one-line spec note). |
| R6 | `_visualsByColumn` populate/read race | **Invalid.** Reality-check: `Show()` builds columns before adding to tree (`tree.Root.AddChild(_canvasLayer)` is the last line). `_Process` cannot fire until after Show() completes. No race. | Reject (clarify in spec if it would help future readers). |
| R6 | What does `HasSpineAnimation = false` static render look like? | **YAGNI.** All current act bosses are Spine-animated. Spec already says "static pose still renders, matches Bestiary." | No change (already documented). |
| R7 | Synchronous pre-warm violates "never-block-main-thread" | **Misread.** The project's rule is about async-deadlock under Godot's sync context (smoke-proven Plan B prep), not about all main-thread synchronous work. Harmony patches do synchronous main-thread work routinely. | Reject. |
| R7 | Remove `Mathf.Max(boundsSize.X, 1f)` floor | **Invalid.** The defensive floor is 4 characters, prevents divide-by-zero on a path that *will* be exercised given consensus #1. Other reviewers (R1, R2, R4, R6, R9) want *more* defense, not less. | Reject. |
| R8 | `ProcessMode.Disabled` as alternative to SetTimeScale | **Transformative.** See Conflicts §A.5. Promoted to Must-do. | Must-do. |
| R8 | Self-managing Adapter Node with VisibilityChanged signal | Subsumed by simpler ProcessMode approach. | Reject. |
| R8 | Multi-monster loop with AssetPaths validation (handle invisible "manager" entity) | **Speculative future-proofing.** No current encounter has this shape. The Warn log gives observability; loop-and-validate is YAGNI. | Reject. |
| R9 | `Thread.Sleep(500)` simulation pre-ship | **Cheap and effective.** 10-minute investment, produces a data point on dev hardware. | Should-do (add to operator validation procedure). |
| R9 | Wrapper class is *worse* than private field | **Vindicated** by ProcessMode.Disabled — both concession AND wrapper become unnecessary. | No change (the v2 design moots both). |

---

## A.4 Category Breakdown

### 🏗️ Architecture & Design

| Feedback | Reviewer(s) | Reality-check | Disposition |
|---|---|---|---|
| Wrapper class for absolute seam | R7, R8, R3 (micro-interface) | The popup is in `src/Game/Ui/` (game-glue layer). One MegaCrit reference doesn't materially change TI extraction cost. But the ProcessMode switch makes the entire debate moot. | **Reject (mooted)** |
| Defend private field | R5, R9 | Sound argument, but ProcessMode.Disabled is even cleaner. | **Mooted by Must-do** |
| **Switch to `ProcessMode.Disabled` on slot** | R8 (alone, but cleanest answer) | Godot-native, no Spine API contact, no typed references needed. Slot's ProcessMode = Disabled cascades to NCreatureVisuals children via Inherit. | **Must-do** |
| Save-quit gate semantics | R1 | B.3 handles boss-swap idempotency, not popup restoration. Gate language is over-claiming. | **Must-do** |
| Document main-thread invariant | R1 | The `before first await` guarantee is real but undocumented. | **Must-do** |
| Positioning differs from Bestiary | R1 | Bestiary's `(0, Size.Y * 0.5f)` is intentional; geometric center may produce per-boss misalignment. | **Should-do (validate visually)** |

### ⚠️ Risks & Concerns

| Feedback | Reviewer(s) | Reality-check | Disposition |
|---|---|---|---|
| **`Bounds.Size = (0,0)` race** | R1, R2, R4, R6, R8, R9 | Confirmed. Spine atlas measurement is lazy. Even Bestiary works only because its container is laid out before AddChild. | **Must-do (defer measurement)** |
| Type-check error in column build | R1, R9 | Confirmed. Resolved by ProcessMode switch (factory return value stays as Node2D, no cast needed). | **Must-do** |
| SetTimeScale(0f) unverified | R1, R2, R3, R4, R5, R7, R8, R9 | Confirmed. Mooted by ProcessMode switch. | **Mooted by Must-do** |
| Pre-warm blocks main thread (HDD perceived as crash) | R2, R4, R5, R6, R7, R8, R9 | Real concern. Logging gives data; chest-room-enter is the follow-up if needed. | **Must-do (logging); Consider (escalate timing)** |
| Multi-monster blind spot | R1, R2, R7, R8 | One log line costs nothing. | **Must-do** |
| AssetPaths.First() ordering | R6 | Decompile-confirmed: `span[0] = VisualsPath`. Worth a doc note. | **Should-do** |
| HasSpineAnimation = false path | R1, R6 | YAGNI for current bosses; spec already says "matches Bestiary static pose." | **No change** |
| _visualsByColumn race | R6 | False alarm — Show() adds to tree last. | **Reject (mooted by ProcessMode switch removing the field anyway)** |
| Mutability of MonsterModel via GenerateAnimator | R4 | Decompile-checked: writes only to MegaSprite parameter. | **No change** |

### 🗑️ Suggested Removals / Simplifications

| Feedback | Reviewer(s) | Disposition |
|---|---|---|
| Remove "stays MegaCrit-free" claim | R1 | **Must-do** (with ProcessMode switch, language can now claim absolute seam) |
| Remove "~50 LOC" estimate | R1, R9 | **Should-do** (acknowledge LOC is closer to 80–100) |
| Remove "Effectively can't throw" | R1 | **Should-do** (soften to "expected-to-be-caught") |
| Remove Full multi-LLM meta-review non-goal | R1 | **Should-do** (stale — this IS the meta-review) |
| Remove stretch gate 16 | R6, R7 | **Should-do** (rework, not remove) |
| Drop "Not handled (correctly)" parenthetical | R4 | **Should-do** (rename to "Deliberately unhandled") |

### ➕ Suggested Additions / Features

| Feedback | Reviewer(s) | Disposition |
|---|---|---|
| Stopwatch logging around pre-warm | R2, R4, R5, R6, R9 | **Must-do** |
| Multi-monster Warn log | R1, R2, R7, R8 | **Must-do** |
| Main-thread invariant doc | R1 | **Must-do** |
| PhobiaMode operator gate | R1, R4 | **Should-do** |
| Visual-smoothness check on gate 7 | R9 | **Should-do (with ProcessMode switch, less critical but still valuable)** |
| Cold-load simulation pre-ship | R9 | **Should-do** |
| Document `idle_loop` assumption with comment | R2, R9 | **Should-do** |
| UX impact gate ("dominant visual element") | R3 | **Consider** |
| Resolution/UI-scale validation | R1 | **Consider** |
| Bounds-aware centering | R1 | **Consider** |
| Multi-monster loop with AssetPaths validation | R8 | **Reject (YAGNI)** |
| Diagnostic log when Bounds.Size = 0 | R9 | **Mooted by Must-do (deferred measurement)** |
| Document Spine TimeScale=0 pattern for future slices | R9 | **Reject (no longer using TimeScale)** |
| _visualsByColumn cleanup on close | R1, R2 | **Mooted by Must-do (no list anymore)** |
| SetAnimation("idle_loop") on un-occlude | R2 | **Reject (mooted; ProcessMode preserves animation state)** |
| const bool freeze toggle | R4 | **Reject (mooted)** |

### 🔄 Alternative Approaches

| Alternative | Reviewer(s) | Disposition |
|---|---|---|
| Wrapper class | R7, R8 (variants), R3 (interface) | **Reject (mooted)** |
| ProcessMode.Disabled | R8 | **MUST-DO** |
| ResourceLoader.LoadThreadedRequest | R2, R6, R7 (with caveats) | **Consider as follow-up** |
| Chest-room-enter pre-warm | R2, R3, R6, R8 | **Consider as follow-up** |
| Larger popup layout | R1, R2 | **Must-do (384x384); larger redesign is Consider** |
| Drop freeze entirely | R3, R5 (fallback), R7 (primary), R8 | **Reject (user explicitly chose freeze; ProcessMode achieves freeze cleanly)** |

### ✅ Confirmed Good / Keep As-Is

Universal or near-universal praise across reviewers:

- **Factory delegate pattern** — all 9 reviewers endorse.
- **`CreateVisuals` + Bestiary precedent** — all 9.
- **`PortraitFit` extraction with unit tests** — all 9.
- **Pre-warm at vote-start (Variant B)** — strongly endorsed; only R7/R8 prefer C as primary.
- **Lifecycle ownership chain reasoning** — R2, R5, R6, R9 explicit praise.
- **Error handling table** — R2, R5, R6, R9 explicit praise.
- **Operator validation checklist structure** — R1, R2, R3, R5, R9 explicit praise.
- **Non-goals discipline** — R3 explicit praise.

### 🔧 Implementation Details & Nits

| Feedback | Reviewer(s) | Disposition |
|---|---|---|
| `slot.Size` vs `slot.CustomMinimumSize` | R3 | **Should-do** |
| `opt.VisualsFactory()` vs `.Invoke()` | R2 | **Should-do (minor)** |
| Cast Node2D → NCreatureVisuals | R9 | **Mooted by ProcessMode (no cast needed)** |
| Line-number refs will stale | R4, R6 | **Should-do (remove, use section names)** |
| PortraitFit.ComputeFitScale visibility | R4 | **Reject (low-value style note)** |
| Docstring inaccuracy in PortraitFit | R6 | **Should-do (one-line fix)** |
| Stretch goal wording | R6 | **Should-do** |
| Em-dash and Co-Authored-By trailer noted | R3, R5, R6, R9 | **No change (already correct)** |

### 📦 Dependencies & Integration

| Feedback | Reviewer(s) | Reality-check | Disposition |
|---|---|---|---|
| Verify `PreloadManager.Cache.GetScene` is idempotent | R4, R9 | Godot resource cache is ref-counted; confirmed idempotent. | **No change** |
| Verify `_isOccludingOverlayVisible` probe alignment | R2 | B.3 ships with this probe; gate 7 already validates. | **No change** |

### 🔮 Future Considerations

These survive into the `notes/06-followups-and-deferred.md` register, not v1:

- Larger popup redesign (3 × 384px is already moving toward this; bigger redesign deferred).
- Chest-room-enter pre-warm escalation.
- `ResourceLoader.LoadThreadedRequest`.
- Wrapper class if TI extraction becomes imminent (currently aspirational).

---

## A.5 Conflicts & Contradictions

### Conflict 1: Seam concession (wrapper vs private field)

**Position A** (R7, R8): Wrapper class is required to preserve TI/Game seam absolutely.
**Position B** (R5, R9): Private field is the right pragmatic choice; wrapper is over-engineering.
**Position C** (R3): Pick one and be honest about it.

**Resolution**: R8's other suggestion — `ProcessMode.Disabled` on the slot Control — eliminates the need for *either* approach. The popup never needs typed access to NCreatureVisuals because Godot's ProcessMode cascades from Control parent to Node2D children. The slot Control is a Godot type, fully MegaCrit-free. Both Position A and Position B are now moot.

### Conflict 2: PortraitFit floor

**Position A** (R1, R2, R4, R6, R9): The `Mathf.Max(bounds, 1f)` floor is correct defense.
**Position B** (R7): Remove the floor — over-engineering.

**Resolution**: Keep the floor. Position A wins on count and on reasoning. The floor is 4 characters and prevents divide-by-zero on the `Bounds.Size = (0,0)` path that is now explicitly Must-do-mitigated.

### Conflict 3: SetTimeScale(0f) demotion vs removal

**Position A** (R3, R7, R8): Make `Visible = false` only the primary path; drop SetTimeScale.
**Position B** (R2, R4): Keep SetTimeScale behind a toggle.
**Position C** (R5, R9): Validate, keep as designed.

**Resolution**: ProcessMode.Disabled replaces both `SetTimeScale(0f)` and `Visible = false`-only freeze with a third option that's cleaner than either. All three positions are mooted.

### Conflict 4: Pre-warm timing (B vs C)

**Position A** (most reviewers): B (vote-start) is acceptable for v1; measure first.
**Position B** (R7, R8): C (chest-room-enter) should be primary.

**Resolution**: Stay with B. Add Stopwatch logging (Must-do consensus). If field data shows hitch >200ms on potato hardware, escalate to C in a follow-up slice. R7/R8's concern is valid but predictive; ship the measurement and react to data.

---

## A.6 Recommended Plan Changes

### Must-do (auto-applied in v2)

1. **Switch occlusion freeze to `ProcessMode.Disabled` on the slot Control.** Eliminates seam concession, SetTimeScale risk, type-check ambiguity, and the `_visualsByColumn` field in one swap. (Inspired by R8; reality-check confirmed.)

2. **Add deferred `Bounds.Size` measurement.** Use `await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame)` (or `CallDeferred`) before reading `visuals.Bounds.Size`. (R1, R2, R4, R6, R8, R9.)

3. **Increase column slot size from 256×256 to 384×384** via named constant `PortraitSlotSize`. (R1, R2, R3, R4, R5, R6, R7, R8, R9.)

4. **Fix the type-check bug** in column-build snippet (mooted by ProcessMode switch — return Node2D stays Node2D, no cast or `_visualsByColumn` needed). (R1, R9.)

5. **Make seam language consistent.** With ProcessMode switch, the spec can now claim absolute MegaCrit-free seam in `BossVotePopup` and `BossVotePopupOption`. Remove the "private field concession" framing entirely. (R1, R3, R7, R8.)

6. **Add multi-monster `TiLog.Warn`** in `BuildVisualsFactory` when `AllPossibleMonsters.Count() > 1`. (R1, R2, R7, R8.)

7. **Add `Stopwatch` logging** around `PreWarmBossVisuals` (Info level, includes elapsed ms and candidate count). (R2, R4, R5, R6, R9.)

8. **Document main-thread invariant** for pre-warm: "must run synchronously before the first `await` in `HandleVoteAsync`." (R1.)

9. **Reword save-quit gate 10** to match B.3's actual behavior (boss-swap idempotency, not popup restoration). (R1.)

### Should-do (auto-applied in v2)

10. **Use `slot.Size * 0.5f`** for centering (not `slot.CustomMinimumSize * 0.5f`). (R3.)

11. **Note that initial positioning is provisional** and operator-validation must check per-boss centering; mention Bestiary's `(0, Size.Y * 0.5f)` as the fallback model if geometric center misaligns. (R1.)

12. **Document `AssetPaths.First()` ordering as decompile-verified observation**, not API contract, in Vanilla API surface section. (R6.)

13. **Add cold-load simulation step** to pre-tag-complete procedure: temporarily insert `Thread.Sleep(500)` after each `GetScene` call, validate UX is acceptable, revert. (R9.)

14. **Add inline comment in `BuildVisualsFactory`** documenting `idle_loop` canonical assumption and citing `MonsterModel.cs:387`. (R2, R9.)

15. **Rework stretch gate 16** — drop "stretch goal" wording; phrase as "validate on second machine if accessible; otherwise document hitch measurement from gate 21." (R6, R7.)

16. **Add PhobiaMode operator-validation gate.** (R1, R4.)

17. **Update LOC honesty** — replace "~50 LOC across 3 files" with "~80–100 LOC across 3 modified files + 1 new helper file + 1 new test file." (R1, R9.)

18. **Remove or rework Non-goals item "Full multi-LLM meta-review"** — stale since this review IS the meta-review. (R1.)

19. **Soften "effectively can't throw"** in Error handling table. (R1.)

20. **Rename "Not handled (correctly)" → "Deliberately unhandled."** (R4.)

21. **Remove specific line-number references** (`:134-158`, `:211-219`) which will stale on first commit. Use section names. (R4, R6.)

22. **Add operator-validation gate 21**: "First-vote-after-fresh-launch (cold OS cache) on dev machine. Stopwatch log shows < 200ms or escalation to chest-room-enter pre-warm is documented as v0.2 polish." (R3, R9.)

23. **Add visual-smoothness check to gate 7**: "Animation resumes smoothly without visible frame-skip after un-occlude." (R9.)

24. **Note that placeholder bosses will now render as animated combat sprites** (different art style from prior PNG portraits — this is a visual upgrade, not a regression). (R6.)

### Consider (presented as pick list in v2; not auto-applied)

These are presented at the end of v2 as Optional Enhancements.

### Reject (with reasons)

- **Wrapper class** (R7, R8, R3 interface): mooted by ProcessMode.Disabled.
- **Remove `Mathf.Max` floor** (R7): contradicts 5 other reviewers; 4 characters of valid defense.
- **"Synchronous pre-warm violates never-block-main-thread"** (R7): misreads the project rule; the rule is about async-deadlock, not all sync work.
- **`const bool _freezeSpineOnOcclude`** (R4): mooted by ProcessMode.Disabled.
- **Self-managing Adapter Node with VisibilityChanged signal** (R8): more machinery than ProcessMode approach.
- **Multi-monster loop with AssetPaths validation** (R8): YAGNI; log warning is sufficient observability.
- **`SetAnimation("idle_loop")` on un-occlude** (R2): mooted by ProcessMode (animation state preserved naturally).
- **Diagnostic log when Bounds.Size = 0** (R9): mooted by deferred measurement.
- **Document Spine TimeScale=0 pattern for future** (R9): not using TimeScale anymore.

---

## A.7 What Stays (do not regress in v2)

These design choices were universally endorsed or had no substantive pushback:

- **`MonsterModel.CreateVisuals()` as the rendering API** — all 9 reviewers endorse.
- **Factory delegate pattern** (`Func<Node2D>? VisualsFactory`) — all 9 endorse.
- **`PortraitFit` pure-math helper with 6 unit-test cases** — all 9 endorse.
- **Pre-warm at vote-start (Variant B)** — 7/9 endorse, 2 prefer C but accept B as v1.
- **Trust vanilla's `CreateFallbackVisuals` chain** — universally endorsed.
- **No mocking of sealed MegaCrit types** — universally endorsed.
- **Bestiary as the precedent reference** — universally endorsed.
- **TI/Game seam preservation** — universally endorsed (now actually achieved via ProcessMode).
- **NBestiary-style animator wiring** (`GenerateAnimator` → `SetUpSkin` → `SetAnimation("idle_loop")`) — universally endorsed.
- **Lifecycle ownership cascade** (factory at Show() time; Godot frees the tree) — universally endorsed.
- **Slot Control with `ClipContents = true`** — universally endorsed.
- **Operator validation as primary test gate** for patch + popup code — universally endorsed.
- **Commit prefix `plan-b-3-1/N.M:`** and `plan-b-3-1-complete` tag — no pushback.

---

## Net assessment

The v1 spec had two latent design issues — the `Bounds.Size` race and the seam-concession — that became visible under review pressure. R8's `ProcessMode.Disabled` suggestion resolves the seam issue entirely (and was buried in their critical-mixed review; this is exactly the kind of insight that justifies the meta-review process). The `Bounds.Size` race resolves with a one-frame deferral. Column sizing is a pure parameter change.

**The v2 design is materially better than v1 on every axis raised by reviewers.** No reviewer suggestion turned out to require a fundamental rethink; this is a healthy meta-review outcome.

Recommend proceeding to writing-plans on v2 after the user reviews the updated spec.
