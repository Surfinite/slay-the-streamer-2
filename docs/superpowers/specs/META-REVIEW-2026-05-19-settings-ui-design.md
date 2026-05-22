# Meta-Review — Settings UI design spec (2026-05-19)

**Inputs**: 5 external reviews in `2026-05-19-settings-ui-design_REVIEWS/`
**Source spec**: `2026-05-19-settings-ui-design.md`
**Updated spec**: `2026-05-19-settings-ui-design-v2.md`

---

## Meta-reviewer correction (read first)

**My CONTEXT doc was wrong about `NeowBlessingVotePatch`.** I listed `NeowBlessingVotePatch + AncientVotePatch` as separate files in `Game/DecisionVotes/`, but git history shows commit `7bb0d24 plan-b-2-2/1.1: rename NeowBlessingVotePatch -> AncientVotePatch`. There is no `NeowBlessingVotePatch.cs` in the current codebase — it was renamed when B.2.2 widened the predicate to cover all `AncientEventModel` events (including Neow). The four vote-bearing decisions handled by AncientVotePatch include Neow.

**Impact**: Reviewers 1, 2, 3, and 5 all flagged "vote duration won't cover Neow" as a HIGH or CRITICAL concern. All four are invalidated by the actual code state. The spec's existing list of four duration call sites is complete.

**The user also flagged "we have no existing users."** Confirmed: this is a hobby mod still pre-1.0 with the user as the only operator. All reviewer concerns framed as "this will surprise/break existing streamers on upgrade" are downgraded accordingly — they remain valid as *first-launch UX design* but not as *migration risk*.

These two corrections defang ~30% of the reviewer concerns. The remaining ~70% are still substantive and the v2 spec applies them.

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus Areas | Unique Insight |
|---|---|---|---|
| **R1** (~6KB) | Mixed | UI-injection memory leak; default flip risk; settings-row styling | "Add a Pause Chat Voting global toggle" — only reviewer to suggest |
| **R2** (~33KB) | Mostly critical | Card numbering semantics; persistence atomicity; threading; tag terminology | Deepest dive on Card-option renumbering semantics; suggests `SettingApplyMode` enum for future-proofing |
| **R3** (~17KB) | Mostly positive | Save-on-close races vs popup submenus; first-run write path; showVoteTag YouTube default | Noticed the action-button confirmation popup is itself a submenu push that could trip the save hook |
| **R4** (~21KB) | Mixed | Mid-run reachability of NModdingScreen; Unlock-everything risk; multi-trigger save | **Only reviewer to flag NModdingScreen mid-run reachability** — verified correct, single highest-impact insight |
| **R5** (~17KB) | Mostly positive | Duplicate-append guard; closure captures in patches; testing strategy | Best surface coverage of "what tests does this slice need" |

All five agree Approach B-modified is correct. All five flag the duplicate-append risk. All five flag the `OnSubmenuClosed` hook reliability concern. Four of five flag the `cardSkipAsVoteOption` default-flip concern (invalidated by "no existing users" but renumbering question still real).

---

## A.2 — Consensus Points (ranked by reviewer count)

| # | Point | Reviewers | Verdict |
|---|---|---|---|
| 1 | **Duplicate-append risk on `NModInfoContainer.Fill`** — postfix needs cleanup of prior injection before appending | R1, R2, R4, R5 | ✅ **Validated by code.** Fill only updates 3 fields; doesn't clear children. **Must-do.** |
| 2 | **`OnSubmenuClosed` hook reliability is load-bearing and under-specified** | R1, R2, R3, R5 | ✅ Valid. Spec acknowledged this as Open Q4. **Must-do**: pick concrete trigger (multi-trigger save). |
| 3 | **`cardSkipAsVoteOption` default flip is risky for existing configs** | R1, R2, R3, R4, R5 | ⚠️ **Invalidated for migration risk** (no existing users). **But renumbering semantics question remains.** Spec needs explicit option-list construction. **Must-do (semantics only).** |
| 4 | **`showVoteTag = false` default is wrong for YouTube users** | R2, R3, R4 | ✅ Valid. Tag exists specifically for YouTube lag; defaulting to off when YouTube is configured defeats the purpose. **Should-do** (conditional default). |
| 5 | **`Unlock everything` needs stronger irreversibility protection** | R1, R2, R3, R4 | ✅ Valid. Confirmation popup alone is the weakest mitigation. **Should-do.** |
| 6 | **Vote-tag terminology (`#04` vs `[04]` vs `!04`) is conflated in help text** | R2, R3 | ✅ Valid. Help text says `#04` which reads as "option 4." **Must-do** (cheap cleanup). |
| 7 | **"Seven UI-managed keys" is wrong — only five persist** | R2, R3 | ✅ Valid. Two are action buttons. **Must-do** (wording fix). |
| 8 | **"Missing-file is unreachable at write time" assumption is wrong** | R1, R2, R3 | ✅ Valid. `SettingsResult.Missing` is a normal load state. **Must-do**. |
| 9 | **Save-on-change-immediately is more robust than save-on-close** | R2, R4, R5 | ✅ Valid. Decouples from hook reliability; better UX. **Must-do** (combined with #2). |
| 10 | **Action buttons should be in a sub-popup, not inline with toggles** | R2, R3, R5 | ⚠️ Debatable. Adds UI surface; benefit is mis-click reduction. **Consider** (pick-list). |
| 11 | **`ModSettings.Current` needs explicit threading semantics** | R2, R4, R5 | ✅ Valid. Static singleton crossing async boundaries. **Should-do**. |
| 12 | **Open Q2 (vanilla scenes vs hand-roll) and Q3 (parser semantics) should be resolved in spec body, not deferred** | R2, R4 | ✅ Valid for Q3 (correctness invariant); reasonable for Q2 (style choice). **Must-do for Q3, Should-do for Q2.** |
| 13 | **NeowBlessingVotePatch is missing from the vote-duration change list** | R1 (implied), R2, R3, R5 | ❌ **Invalidated by code.** Renamed to AncientVotePatch in plan-b-2-2/1.1. Already in the list. |

---

## A.3 — Outlier Points

| # | Point | Reviewer | Verdict |
|---|---|---|---|
| 1 | **`NModdingScreen` is disabled mid-run** | R4 only | ✅ **VERIFIED.** [`NSettingsScreen.cs:151-158`](decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L151-L158) calls `_moddingScreenButton.Disable()` when `RunManager.Instance.IsInProgress`. The spec's "hot-reload → next vote" framing is misleading; it's actually "next-run first vote." **Must-do**: correct the framing. |
| 2 | **Auto-backup before unlock-everything** | R2 (mentioned), R4 (implied) | ✅ Valid mitigation; cheap to implement. **Should-do**. |
| 3 | **Atomic write (temp file + rename)** | R2 only | ✅ Valid for any persistent settings file. Cheap. **Should-do**. |
| 4 | **Card-option renumbering needs explicit table** | R2 only | ✅ Valid even without migration risk — spec doesn't actually specify where Skip goes in the list. **Must-do**. |
| 5 | **Confirmation popup button labels** ("Unlock everything" not "OK") | R3 only | ✅ Cheap, real foot-gun reduction. **Must-do**. |
| 6 | **`SettingApplyMode` enum metadata pattern** | R2 only | Forward-looking; v1 doesn't need it. **Consider**. |
| 7 | **Closure captures in vote patches** | R5 only | ⚠️ Worth checking but low risk — all four patches I've read use static methods that read `ChatSettings` at invocation time. **Should-do**: explicit audit statement in spec. |
| 8 | **`Pause Chat Voting` global toggle** | R1 only | ⚠️ Useful feature, but adds a row and creates a "is voting currently paused?" state to maintain. **Consider**. |
| 9 | **Settings file path display in UI** | R4 only | ✅ Trivial cost, high support-thread-prevention value. **Should-do**. |
| 10 | **Test plan section** | R2, R4, R5 (cross-cutting) | ✅ Spec should at minimum list unit-testable surfaces. **Should-do**. |
| 11 | **Operator-validation checklist** | R2, R4 | ✅ Existing slice-completion convention; should be in spec. **Should-do**. |
| 12 | **`VoteCoordinator.Start` signature change test impact** | R5 only | ✅ Cheap to mention. **Should-do**: one sentence about `VoteSessionTestBase.CreateCoordinator`. |
| 13 | **Restore-from-backup button** | R4 only | Complements Unlock-everything's destructiveness. Real-world useful. **Consider**. |
| 14 | **Backup retention policy** | R4 only | Unbounded growth otherwise. Real concern. **Consider**. |
| 15 | **Settings-panel name as a constant** | R2, R5 (related) | Trivial; do during implementation. **Nit** (apply silently). |

---

## A.4 — Category Breakdown (with codebase reality checks)

### 🏗️ Architecture & Design

- **Multi-trigger save (debounced + close fallback)** — R2, R4, R5. ✅ Validated. The spec's single-trigger save is brittle; multi-trigger removes the `OnSubmenuClosed` reliability dependency. **Applied to v2.**
- **`ModSettings.Current` threading via `Volatile.Write/Read`** — R2. ✅ Validated. `ChatSettings` is a `record` (reference type, immutable); volatile semantics are cheap. **Applied to v2.**
- **`SettingsUiState` decoupled from Godot node lifecycle** — R2. ✅ Validated, but smaller form: a static dirty-bag in the patch class is sufficient for our 5-field surface. **Applied (minimal form) to v2.**
- **Rename `showTag` → `DisplayVoteTag`** — R2. ⚠️ Bikeshed. `showTag` reads fine in context. **Reject** (minor, not worth the churn).
- **`ModSettings.Current` as method vs property** — R3. ⚠️ Marginal. Property is idiomatic in C#. **Reject**.
- **`SettingApplyMode` enum** — R2. Forward-looking. **Consider** (pick-list).

### ⚠️ Risks & Concerns

- **Duplicate-append on Fill** — R1, R2, R4, R5. ✅ Validated by reading [`NModInfoContainer.cs:51-98`](decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs#L51-L98) — only `_title.Text`, `_image.Texture`, `_description.Text` are reset; arbitrary children are not. **Applied to v2** (named-child-cleanup before append).
- **`OnSubmenuClosed` reliability** — R1, R2, R3, R5. ✅ Validated and resolved via multi-trigger save (debounced-on-change is primary; close-hook is belt-and-braces). **Applied to v2.**
- **NModdingScreen mid-run reachability** — R4. ✅ **VERIFIED at [`NSettingsScreen.cs:157`](decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L157): `_moddingScreenButton.Disable()` runs when `RunManager.Instance.IsInProgress`**. Mod-manager screen is unreachable during a run. **Applied to v2**: corrected hot-reload framing to "next-run first vote" with explicit acknowledgement.
- **`cardSkipAsVoteOption` default flip** — R1, R2, R3, R4, R5. ⚠️ **Migration risk invalidated** (no existing users). Renumbering semantics still need spec text. **Applied to v2** (semantics table only, default remains `true`).
- **`showVoteTag = false` weakens YouTube UX** — R2, R3, R4. ✅ Valid. **Applied to v2**: conditional default (`true` if `youtubeChannelId` non-null, else `false`).
- **`Unlock everything` irreversibility** — R1 (implied), R2, R3, R4. ✅ Valid. **Applied to v2**: auto-backup before unlock + explicit button labels + visual emphasis note. Type-to-confirm is in the Consider pick-list.
- **RHS panel scroll capacity** — R2, R3. ⚠️ Worth a verification note. **Applied to v2** as implementation-time check.
- **Atomic write** — R2. ✅ Valid. **Applied to v2** (temp + rename).
- **Closure-capture audit** — R5. ✅ All four vote patches use static methods reading `ChatSettings` at invocation time (verified across `AncientVotePatch.cs`, `CardRewardVotePatch.cs`). **Applied to v2** as an audit statement.
- **First-run write path ("missing-file unreachable" is wrong)** — R1, R2, R3. ✅ Validated. **Applied to v2.**

### 🗑️ Suggested Removals / Simplifications

- **Remove "seven UI-managed keys"** — R2, R3. ✅ **Applied**.
- **Remove "No restart required" if apply timing is unclear** — R2. ✅ **Applied** via multi-trigger save (changes apply on each change event, not at close).
- **Remove "this case is unreachable" from missing-file write** — R1, R2, R3. ✅ **Applied**.
- **Skip `Unlock everything` from v1 entirely** — R4. ⚠️ **Reject**. User confirmed in brainstorming they want this button (with the "this can't be undone + modded saves are separate" warning). Code-side cost is small.

### ➕ Suggested Additions / Features

- **Test plan section** — R2, R4, R5. ✅ **Applied** (minimal list).
- **Operator-validation checklist** — R2, R4. ✅ **Applied**.
- **Pause Chat Voting global toggle** — R1. **Consider** (adds row + state).
- **Failure-mode table** — R2. **Applied** (small version).
- **Backup manifest file** — R2. ✅ Cheap and useful. **Applied**.
- **Settings file path display** — R4. ✅ **Applied**.
- **Reveal-in-Explorer button** — R4. **Consider** (adds OS-shell-launch code).
- **Chat-status indicators** — R4. **Consider**.
- **"Test chat" button** — R4. **Consider**.
- **Backup retention policy** — R4. **Consider**.
- **"Restore from backup" button** — R4. **Consider**.
- **Accessibility/controller note** — R2. **Applied** ("mouse-only acceptable for v1" stated explicitly).
- **MegaCrit API stability note** — R4. ✅ **Applied** (one-line note).
- **Slider value-badge format** — R3. ✅ **Applied** (`"30s"`).
- **Slider live-update during drag** — R5. **Applied** (live update; commit on drag end).
- **Confirmation popup for backup acknowledged** — R5. ✅ **Applied** (one-line note: "no confirm — backup is non-destructive").
- **Toast spec** — R5. **Applied** (reference NSettingsToast pattern, flag as new in NModdingScreen context).

### 🔄 Alternative Approaches

- **Save-on-each-change debounced** — R2, R4, R5. ✅ **Applied** (combined with close fallback).
- **Action buttons in sub-popup** — R2, R3, R5. **Consider** (real mis-click protection vs extra click).
- **Platform-aware `showVoteTag` default** — R2, R3. ✅ **Applied** (conditional default).
- **Type-to-confirm Unlock** — R4. **Consider**.
- **Tri-state `showVoteTag`** (off / receipts-only / receipts-and-overlay) — R4. **Consider** (probably over-engineered for v1).
- **Skip Unlock from v1** — R4. ❌ **Reject** (user wants it).

### ✅ Confirmed Good / Keep As-Is

- Approach B-modified (all 5 reviewers).
- TI/Game seam respected (R2, R3, R5).
- `showTag` parameter on `VoteCoordinator.Start` is the right TI surface (R3, R5).
- Read-merge-write via `JsonNode` (R2, R3, R5).
- `ModSettings.Current` static for hot-reload (R1, R3, R5).
- Seven-row v1 scope discipline (R3, R4, R5).
- Credentials JSON-only (R1, R2, R3, R4).
- Dirty-flag optimisation (R3).
- "Parser stays defensive when `showVoteTag = false`" recommendation (R1, R2, R3 explicitly agree).
- Action-button confirmation popup pattern (R5).
- Fail-open failure handling (R5).

### 🔧 Implementation Details & Nits

- **Confirmation button labels** ("Unlock everything"/"Cancel" not "OK"/"Cancel") — R3. ✅ **Applied**.
- **Toast text — drop "see godot.log"** — R1, R4. ✅ **Applied** (replace with `"Failed to save settings"`).
- **Sentinel "Unlimited" ↔ `-1` mapping** — R1. ✅ **Applied**.
- **Timestamp format local vs UTC** — R2, R3. ✅ **Applied** (local time, `YYYY-MM-DD-HHMMSS`).
- **Backup folder collision handling** — R2, R5. ✅ **Applied** (append `-01`, `-02`).
- **Tag terminology cleanup** — R2, R3. ✅ **Applied** (`[04]` for the tag form; `!04` for the syntax; never `#04`).
- **"Allow chat to skip" help mentions renumbering** — R2. ✅ **Applied** (combined with semantics table).
- **"Bo" undefined for cold readers** — R3, R5. ✅ **Applied** (one-line gloss).
- **Line numbers will rot** — R3, R5. ✅ **Applied** (switch to method names where possible).
- **Card-skips help: `0 = strict`** — R4. ✅ **Applied**.
- **`SaveProgressFile()` synchronous main thread** — R4. ✅ **Applied** as one-line note.
- **Hardcoded mod ID string** — R5. ✅ **Applied** (constant).

### 📦 Dependencies & Integration

- **MegaCrit API stability** — R4. ✅ **Applied** (one-line "revalidate per game patch in operator validation").
- **Load-order dependency** — R5. ✅ **Applied** as note.

### 🔮 Future Considerations

- **`SettingApplyMode` enum** — R2. **Consider**.
- **Sub-popup for richer warnings** — R2 alt C. **Consider**.

---

## A.5 — Conflicts & Contradictions

| Conflict | Resolution |
|---|---|
| R2 wants "default `false` for existing configs" vs R4 wants "default `false` everywhere" vs spec wants "default `true`" | **No existing users (user confirmed)** → default `true` universally is fine. No migration question. |
| R2 wants `showVoteTag` defaulted based on platform vs R3 same vs R4 "tri-state alternative" | **Conditional default** (`true` if YouTube configured, else `false`). Tri-state is deferred to Consider. |
| R4 wants `Unlock everything` removed entirely vs R2 wants it kept with auto-backup vs spec wants it kept with confirmation only | **Auto-backup before unlock + explicit button labels**. Type-to-confirm is in Consider. |
| R2/R5 want save-on-each-change vs R3 says save-on-close is fine if hook is reliable | **Multi-trigger save** (debounced-on-change primary; close-hook backup). Best of both. |
| R2 wants `SettingsUiState` separate from panel vs spec uses `_dirty` on panel | **Minimal form**: static dirty-bag in `SettingsPanelPatch.cs`. Doesn't need a separate component for 5 fields. |

---

## A.6 — Recommended Plan Changes (prioritized)

### Must-do (auto-applied to v2)

1. **Correct CONTEXT doc + spec on `NeowBlessingVotePatch`** — it's been absorbed into `AncientVotePatch`; no separate patch exists.
2. **Correct hot-reload framing**: `NModdingScreen` is disabled mid-run, so "hot-reload" means "settings apply on next run." Update TL;DR, success criteria, inventory table.
3. **Duplicate-append guard on `NModInfoContainer.Fill`**: postfix searches for a named child `Sts2SettingsPanel` and removes it unconditionally before deciding whether to append.
4. **Multi-trigger save**: debounced 500ms save-on-change is primary; `OnSubmenuClosed` is belt-and-braces. Open Q4 resolved.
5. **Atomic write**: temp file + rename, with current file backed up as `.bak`.
6. **First-run write path**: if file doesn't exist at write time, create with `schemaVersion: 1` + UI-managed fields. No more "this case is unreachable."
7. **"Seven UI-managed keys" → "five persisted settings keys"** with explicit list.
8. **Card-option construction table**: explicit specification of where Skip sits in the option list when `cardSkipAsVoteOption = true`.
9. **Lock open Q3** in spec body: "`VoteSession`'s parser ALWAYS drops stale-tag votes regardless of `showVoteTag`."
10. **`ModSettings.Current` threading**: `Volatile.Write/Read` semantics + "consumers should snapshot to a local once per logical operation" convention.
11. **Tag terminology**: standardize on `[04]` (display form) and `!04` (chat syntax). Never `#04`.
12. **`ModSettings.Current` updates immediately on control change**, write debounced.
13. **Confirmation popup button labels**: "Unlock everything" / "Cancel", not "OK" / "Cancel".

### Should-do (auto-applied to v2)

14. **Conditional default for `showVoteTag`**: `true` if `youtubeChannelId` non-null, else `false`.
15. **Auto-backup before `Unlock everything`**: confirmation → backup → unlock; on backup failure, second confirmation for "unlock without backup."
16. **Settings file path display** in panel (read-only label).
17. **Test plan section** listing unit-testable surfaces.
18. **Operator-validation checklist** at end of spec.
19. **Backup manifest file** (timestamp, mod version, scope).
20. **Closure-capture audit statement**: all four vote patches verified to read `ChatSettings` at invocation time.
21. **Help text fixes**: "Allow chat to skip" mentions renumbering; "Show vote tag" describes display only.
22. **Toast text**: drop "see godot.log" pointer for end-user-facing toasts.
23. **Backup folder collision handling**: append `-01`, `-02` if same-second collision.
24. **Resolve open Q2**: commit to hand-rolled MegaText + Godot-control hybrid for settings rows (stylistic drift acceptable; cross-scene-tree risk avoided).
25. **MegaCrit API stability note** for unlock APIs.
26. **Slider behaviour**: live update during drag; commit on release.
27. **"Bo" gloss**: one-line reference.

### Consider (pick-list — see Part B)

(Numbered for user selection in v2's Optional Enhancements section.)

### Reject (with reason)

- **R2/R5: NeowBlessingVotePatch missing from duration call sites** — Invalidated by code: patch no longer exists, renamed to AncientVotePatch (which IS in the list).
- **R1/R2/R3/R4/R5: `cardSkipAsVoteOption` migration logging / config-aware defaults** — Invalidated by "no existing users." Default `true` universally; no migration story needed.
- **R2: `Make logs never include oauthToken when dumping merged JSON`** — Defensive but unnecessary; ModSettings.Load doesn't dump JSON to logs and never has.
- **R4: Skip `Unlock everything` from v1** — User wants this in v1; brainstorm decision.
- **R2: Rename `showTag` → `DisplayVoteTag`** — Bikeshed.
- **R3: `ModSettings.GetCurrent()` method instead of property** — Idiomatic C# is property.

---

## A.7 — What Stays

These were affirmed by multiple reviewers; v2 preserves them unchanged:

- **Approach B-modified** as the UI-injection strategy.
- **TI/Game seam discipline** — settings code stays in `src/Game/`; only `showTag` is a TI-surface change.
- **`showTag` parameter on `VoteCoordinator.Start`** as the right TI/Game boundary.
- **Read-merge-write via `JsonNode`** for unknown-key round-tripping.
- **`ModSettings.Current` static** as the hot-reload mechanism (consumers read at invocation time).
- **Seven-row v1 scope** — every row earns its place.
- **Credentials JSON-only** — non-negotiable.
- **Dirty-flag** to avoid spurious writes.
- **"Parser stays defensive when display tag is off"** — locked into spec body as an invariant.
- **Action-button confirmation popup pattern.**
- **Fail-open failure handling** — log + toast, in-memory state still updated.
- **Discrete dropdown for `Card skips per act`** (not a slider).
- **`-1` sentinel mapping to "Unlimited"** in JSON.
- **Save to `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`** via `OS.GetUserDataDir()`.
- **Default `cardSkipAsVoteOption = true`** universally (after the "no existing users" correction).
- **Streamer-private credentials (no OAuth in UI).**
- **Additive-optional schema without `CurrentSchemaVersion` bump.**

---

End of meta-review. See `2026-05-19-settings-ui-design-v2.md` for the updated spec with Must-do + Should-do changes applied and an Optional Enhancements section for Consider-tier items.
