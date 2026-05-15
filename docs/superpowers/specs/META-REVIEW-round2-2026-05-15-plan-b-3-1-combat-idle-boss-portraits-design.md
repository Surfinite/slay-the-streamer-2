# Round-2 Meta-Review — Plan B.3.1 Combat-idle boss portraits

**Inputs**: 3 round-2 reviews of `2026-05-15-plan-b-3-1-combat-idle-boss-portraits-design-v2.md` (with Optional Enhancements 1, 3, 5, 6, 11 applied).
**Synthesis date**: 2026-05-16.

---

## A.1 Review Summary Table

| Reviewer | Sentiment | Key Focus | Unique Insight |
|---|---|---|---|
| R2-1 | Strongly positive | Async exception handling, factory null-safety | `?.` on `GetAnimationState()` for potato-defensive null check |
| R2-2 | Mixed-critical (3 blockers) | Seam framing, async scheduling, SetUpSkin signature | **`ApplyPortraitFit` calls `GetTree()` before popup is in tree** (real bug) + **`SetUpSkin` signature inverted in API surface** (real compile-break risk) |
| R2-3 | Mostly positive | Fire-and-forget try/catch, ProcessMode unverified Plan B | Restore `SetTimeScale(0f)` as documented Plan B in Open Risks (vs deleting it entirely) |

Overall sentiment: **v2 is near-ship, with two reality-checked blockers (SetUpSkin signature + GetTree scheduling) and several small consensus polishes.** No design pivot needed.

---

## A.2 Consensus Points

1. **Fire-and-forget `ApplyPortraitFit` needs try/catch** — **3 reviewers** (R2-1, R2-2, R2-3). Unobserved Task exceptions are a real .NET footgun; the slice's "degrade silently, log, never crash" principle requires the catch.

2. **`PortraitFit` needs slot-size=0 defense** — **2 reviewers** (R2-2, R2-3). Symmetric to the bounds-size defense already present; cheap addition.

3. **Seam-language contradiction** — **2 reviewers** (R2-2 blocker, R2-3 nit N3). v2 says "absolutely MegaCrit-free" but admits typed private helpers. Two paths: (a) make it truly absolute via handle DTO; (b) soften the claim. **Choose (b)**: user explicitly applied Optional #1 to use typed helpers; the seam goal is now "public surface MegaCrit-free with localized typed implementation," not "absolute."

4. **ProcessMode.Disabled freeze is unverified** — **2 reviewers** (R2-2 medium, R2-3 R2). Gate 7 catches regressions but spec deleted the v1 escape hatch. Restore `SetTimeScale(0f)` as a documented Plan B if gate 7 fails.

## A.3 Outlier Points (single reviewer, with merit)

| Reviewer | Point | Merit | Disposition |
|---|---|---|---|
| R2-1 | `?.` on `GetAnimationState()` in factory | Cheap, defensive. | **Must-do** |
| R2-2 | **`ApplyPortraitFit` calls `GetTree()` before popup is in tree** | **Real bug** — neither `this` nor `slot` is in tree at column-build time. `GetTree()` returns null → `ToSignal(null, ...)` NRE. | **Must-do** |
| R2-2 | **`SetUpSkin` signature inverted in Vanilla API surface** | **Verified via decompile**: `NCreatureVisuals.SetUpSkin(MonsterModel)` at [`NCreatureVisuals.cs:178`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Combat/NCreatureVisuals.cs#L178). Code samples are right; documentation entry is inverted. | **Must-do** |
| R2-2 | Delegate/handle DTO (`BossPortraitVisual` record with `GetBoundsSize`/`ApplyScale` delegates) | Over-engineering. User explicitly applied Optional #1 to drop duck-typing for typed private helpers. Pragmatic seam is the chosen path. | **Reject (frame change instead)** |
| R2-2 | Bounds-zero retry loop (3–5 frames) | Premature. Single ProcessFrame yield + Warn log + Clip is acceptable; retry loop adds complexity for a hypothetical case operator validation hasn't yet observed. | **Reject (defer until observed)** |
| R2-2 | Soften ProcessMode Architecture claim ("intended to freeze, validated by gate 7") | Honest framing; matches the "unverified MegaSpine usage" caveat. | **Should-do** |
| R2-2 | Soften PhobiaMode gate wording (vanilla may only apply on enter-tree) | Fair — gate 11 currently overpromises. | **Should-do** |
| R2-3 | `ProcessMode = Inherit` on slot creation is a no-op (default) | True. Replace with documentation comment. | **Should-do** |
| R2-3 | Date off-by-one (header 2026-05-15, disposition 2026-05-16) | Trivial. v3 spans both days. | **Should-do (note both dates)** |
| R2-3 | No unit test for monster-count branch in `BuildVisualsFactory` | The lambda makes it awkward; the Warn log + gate 12 covers observability. YAGNI. | **Reject** |
| R2-3 | Restore `SetTimeScale(0f)` as Plan B in Open Risks | Cheap insurance — one paragraph documenting the fallback path. | **Must-do** |

---

## A.4 Category Breakdown

### 🏗️ Architecture & Design

| Feedback | Reviewers | Reality-check | Disposition |
|---|---|---|---|
| Seam-language contradiction | R2-2, R2-3 | Confirmed. v2 says "absolute" + admits private typed helpers. | **Must-do (soften claim)** |
| `ApplyPortraitFit` scheduling before tree | R2-2 | Confirmed bug. | **Must-do (defer until tree-attached)** |
| Delegate/handle DTO | R2-2 | Over-engineering given user's Optional #1 pick. | **Reject** |
| ProcessMode cascade unverified | R2-2, R2-3 | Real — no prior art in decompile for the cascade pattern, only validated empirically. | **Must-do (Plan B in Open Risks)** + **Should-do (soften claim)** |

### ⚠️ Risks & Concerns

| Feedback | Reviewers | Reality-check | Disposition |
|---|---|---|---|
| `SetUpSkin` signature wrong in docs | R2-2 | Confirmed (compile-break if code follows docs). | **Must-do** |
| Fire-and-forget exception handling | R2-1, R2-2, R2-3 | Real .NET footgun; matches "log + degrade" principle. | **Must-do** |
| slot.Size=0 defense | R2-2, R2-3 | Real edge case if layout hasn't completed by ProcessFrame yield. | **Must-do** |
| Bounds-zero retry loop | R2-2 | Premature. | **Reject (defer until observed)** |
| Missing `?.` on `GetAnimationState()` | R2-1 | Cheap, defensive. | **Must-do** |
| PhobiaMode gate overpromises | R2-2 | Gate 11 says "swap to phobia-safe variants automatically" — only true if vanilla applies live. | **Should-do (soften gate wording)** |

### 🗑️ Suggested Removals / Simplifications

| Feedback | Reviewers | Disposition |
|---|---|---|
| Remove "ProcessMode = Inherit" explicit set | R2-3 | **Should-do** (replace with comment) |
| Remove "absolutely" claim | R2-2, R2-3 | **Must-do** (soften to "at public surface") |

### ➕ Additions

| Feedback | Reviewers | Disposition |
|---|---|---|
| `SetTimeScale(0f)` Plan B in Open Risks | R2-3 | **Must-do** |
| Date alignment (note both 05-15 and 05-16) | R2-3 | **Should-do** |
| Unit test for monster-count branch | R2-3 | **Reject (YAGNI)** |

### ✅ Confirmed Good

Every change from Round 1 is endorsed:
- ProcessMode.Disabled vs SetTimeScale (R2-1, R2-2 medium concerns about unverified-ness aside, the approach itself is endorsed).
- Deferred Bounds.Size measurement.
- 384×384 column.
- Stopwatch logging.
- Multi-monster Warn.
- PhobiaMode gate (with wording softened).
- Resolution coverage gate 20.
- `GetScene` synchronicity verified.

### 🔧 Implementation Details & Nits

- R2-1's `slot.Size` clarification: no code change needed (already using `slot.Size` correctly).
- R2-1's Thread.Sleep simulation acknowledgment: no change.

---

## A.5 Conflicts & Contradictions

### Conflict: How to resolve the seam language

**R2-2 Position A**: Use delegate/handle DTO (`BossPortraitVisual` record) to make `BossVotePopup.cs` *truly* free of `NCreatureVisuals` references.
**R2-2 Position B / R2-3 N3**: Soften the "absolute" claim to acknowledge the typed private helpers.

**Resolution**: Position B. The user explicitly applied Optional Enhancement #1 in v2.1 to drop duck-typing in favor of typed private helpers; this was a deliberate choice. Adding a delegate-handle DTO now would walk that back and add a new abstraction class. The pragmatic compromise is to align the *language* with the *code*: "public surface MegaCrit-free; localized private typed helpers."

This is consistent with the project's "honest substance over polish" principle from the user profile.

---

## A.6 Recommended Plan Changes

### Must-do (auto-applied in v3)

1. **Fix `SetUpSkin` signature** in Vanilla API surface section. Correct entry: `NCreatureVisuals.SetUpSkin(MonsterModel)`. Verify code samples already match (they do).
2. **Fix `ApplyPortraitFit` scheduling**. Collect `(slot, visuals)` pairs into a `_pendingFits` list during column build; dispatch fire-and-forget after `tree.Root.AddChild(_canvasLayer)`. Use `slot.GetTree()` (now known non-null) inside the helper.
3. **Add top-level try/catch to `ApplyPortraitFit`** with `TiLog.Warn` on exception.
4. **Add slot-size=0 fallback** in `ApplyPortraitFit`: if `slot.Size.X <= 0 || slot.Size.Y <= 0`, use `PortraitSlotSize` (the known intended fallback).
5. **Soften seam language** throughout. Replace "preserve TI/Game seam absolutely" with "preserve TI/Game seam at the public interface." Acknowledge typed private helpers honestly.
6. **Add `?.` on `GetAnimationState()`** in factory: `visuals.SpineBody.GetAnimationState()?.SetAnimation("idle_loop")`.
7. **Document `SetTimeScale(0f)` as Plan B** in Open Risks for ProcessMode fallback if gate 7 reveals animations continue under occlusion.

### Should-do (auto-applied in v3)

8. **Soften ProcessMode Architecture claim**: "intended to freeze Spine playback via cascade; operator validation (gate 7) confirms behavior on this MegaSpine build."
9. **Soften PhobiaMode gate 11 wording**: "PhobiaMode toggle mid-vote → no crash; affected creature visuals update if vanilla supports live update for that monster."
10. **Remove no-op `ProcessMode = ProcessModeEnum.Inherit`** on slot creation; replace with one-line comment noting Inherit is default and the occlusion handler toggles between Disabled/Inherit.
11. **Date alignment**: change header date range to "2026-05-15 (v1) … 2026-05-16 (v3)" with brief revision history note.

### Reject (with reasons)

- **Delegate/handle DTO** (R2-2): conflicts with user-applied Optional #1; over-engineering.
- **Bounds-zero retry loop** (R2-2): premature; single yield + warn + clip is sufficient until observed.
- **Unit test for monster-count branch** (R2-3 N5): YAGNI; Warn + gate 12 covers it.

---

## A.7 What Stays

- ProcessMode.Disabled approach itself (just with Plan B documented).
- All v2 must-dos: deferred Bounds.Size, 384×384, Stopwatch logging, multi-monster Warn, main-thread invariant, save-quit gate correction.
- All v2 optional-applied items (typed private helpers, resolution gate, Bounds=0 diagnostic, ProcessMode pattern comment, GetScene-sync verification).
- `PortraitFit` unit tests (R2-3's "no test for monster-count" doesn't apply to `PortraitFit`).
- 20-gate operator validation structure.

---

## Net assessment

Round 2 is convergent. Three reviews, two genuine blockers (both small text/code fixes, verified via decompile), one strong consensus on async exception handling, and a handful of polish items. No design pivot.

**v3 ships after the Must-dos are applied.** Recommend proceeding to writing-plans immediately after user reviews v3.
