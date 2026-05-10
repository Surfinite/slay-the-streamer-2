# Meta-Review — Plan B.2.1 card reward vote design (v1)

**Date**: 2026-05-10
**Source spec**: [`2026-05-10-plan-b-2-1-card-reward-vote-design.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design.md) (commit `9e7379e` + `4d5962d`)
**Reviews ingested**: 7 (in [`./2026-05-10-plan-b-2-1-card-reward-vote-design_REVIEWS/`](./2026-05-10-plan-b-2-1-card-reward-vote-design_REVIEWS/))
**Output**: this document + [`-v2.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v2.md) (Must-do + Should-do auto-applied; Optional Enhancements appended for pick-list)

---

## A.1 — Reviewer Summary Table

| Reviewer | Sentiment | Key Focus Areas | Unique Insight |
|---|---|---|---|
| **R1** | Mixed (mostly positive, several Severity-1) | Skip-gate edge cases, settings/chat-disabled policy, counter semantics, EnglishReceipts seam | Skip gate doesn't re-disallow Proceed inside `RewardSkippedFrom` postfix when budget is exhausted by THAT skip on a multi-card screen |
| **R2** | Mixed (right on big calls, blocking concerns) | Reroll exploit, ModEntryState, label dangling-pointer, DebugOnly fragility, dual-budget YAGNI | Reroll-mid-vote is a genuine streamer exploit (vote-then-reroll-if-chat-converging) — defeats the gate's purpose |
| **R3** | Mostly positive with two non-trivial gaps | EnglishReceipts seam, Godot `_Ready` patchability risk, dual-budget AND semantics | **Godot `_Ready` may not be Harmony-patchable** — it's a native-to-managed notification, not a standard C# virtual; this is the highest unverified technical risk in the spec |
| **R4** | Constructive, action-oriented | Run-ID type pinning, reflection fragility, act-detection determinism, nullability discipline | Suggests `Prepare` should cache `FieldInfo _rewardButtons` and have a `static IEnumerable<Control> GetRewardButtons(NRewardsScreen)` helper — concrete API shape |
| **R5** | Mixed, pragmatic | DisallowSkipping lifecycle, threading model, EnglishReceipts seam, vote pacing | **`DisallowSkipping()` persists for entire screen lifecycle** — claiming a card via vote locks ALL other reward types from being skipped (Severity 1). And **30s × ~16 votes/act = 8 min/act of pure waiting**. |
| **R6** | Pragmatic, focused on real exploits | Save/Load loophole, multi-reward-type lockout, button claimed-state | **Save/Load Loophole** — static counters reset on reload; streamer save-quits for infinite skips. Plus `HasUnclaimedCardReward` must check button claimed-state, not just type |
| **R7** | Mixed (strong endorsement of design + critical fragility) | DebugOnly run-ID risk, EnglishReceipts seam, ModEntry settings access, queue rate-limit, random fallback | `DebugOnlyGetState()` may return null in release builds — guard could silently disable. Also: skip receipts must use `OutgoingMessageQueue`; random fallback shouldn't force a card pick when skip budget is available |

---

## A.2 — Consensus Points (ranked by reviewer count)

### 🔴 Universal (7/7) or near-universal (5-6/7)

1. **EnglishReceipts/TI seam violation** — Reviewers 1, 2, 3, 4, 5, 7. Adding `SkipReceipt` to `Ti/Voting/EnglishReceipts.cs` brings game-domain ("card skip", "act/run counters") into the supposedly game-agnostic TI core. Universal verdict: **inline in Game/, do not modify Ti/.**

2. **Run-ID guard implementation underspecified, with `DebugOnlyGetState` as load-bearing fragility** — Reviewers 1, 2, 3, 4, 5, 6, 7 (universal). Multiple sub-issues:
   - Method name signals fragility (R2, R5, R7)
   - Exact field name/type unverified (R1, R4, R7)
   - Null-at-resume case unspecified (R3)
   - Could silently disable in release builds (R7)
   - Should apply to Neow too for template consistency (R1, R2)

3. **`ModEntryState.Settings` static accessor doesn't exist; needs concrete design** — Reviewers 1, 2, 3, 5, 7. The spec hand-waves "injected via static accessor or constructor-equivalent". Universal verdict: pick a concrete pattern (most prefer `ModEntry.Settings` static + acknowledge it as a `ModEntry.cs` change).

4. **Static `_activeLabel` will dangle / point at freed Godot object** — Reviewers 1, 2, 3, 5, 6. The label dies with the rewards screen but the static C# reference doesn't auto-null. Universal verdict: either `IsInstanceValid` guard before every use, OR null on `_ExitTree`.

### 🟡 Strong (3-4/7)

5. **`Interlocked` is theatre on main-thread-only paths** — Reviewers 2, 3, 5, 7. Either drop and document main-thread-only OR use everywhere consistently. Most prefer drop.

6. **Receipt counter semantics wrong / `0/∞ run` after skip is misleading** — Reviewers 1, 5, 6. Pick used-or-remaining and stick to it; never display `0/∞` after a non-zero increment.

7. **`HasUnclaimedCardReward` must check button claimed-state, not just type** — Reviewers 1, 4, 6. Otherwise multi-reward screens trip the gate even after the card is claimed (related to R5's lifecycle concern).

8. **Drop `cardSkipsPerRun` for v0.1 (YAGNI)** — Reviewers 2, 5, 7. Default `1/act, ∞/run` makes the per-run knob inert; per-act × acts ≈ same total. Counter-argument: Surfinite explicitly chose dual-budget during brainstorming.

### 🟢 Medium (2/7)

9. **Run-ID guard for Neow too** — R1, R2. Keep templates aligned for B.2.2 copy.
10. **Multi-card-reward gate re-evaluation in `RewardSkippedFrom`** — R1, R6. If a skip exhausts the budget, the next card on the SAME screen still skippable until next `_Ready`.
11. **Reroll-mid-vote needs explicit Decision** — R1, R2. Currently buried as edge case. Three options: patch reroll-as-skip (R2 leans here), send chat receipt (R1 alternative), accept silent absorb (R6 confirmed).
12. **Pure budget-state class extraction (`SkipBudgetTracker`)** — R1, R2. Improves testability without violating Rule of Three (it's *not* the deferred suspend/resume helper — different concern).
13. **Acceptance Step 0 split** — R2, R3. Step 0 = pure regression check (no card path); Step 1 = card defaults.
14. **Card-holder re-derivation at resume (not capture)** — R1, R5. Use `holders[winnerIndex]` from current screen state, not a captured `NCardHolder` reference that may be freed by reroll.
15. **`VoteTallyLabel.AttachTo` not mentioned in `CardRewardVotePatch`** — R3, R5. Copy-paste-modify oversight; without it the tally label won't appear during card votes.
16. **`DisallowSkipping()` lifecycle locks all reward types after card claim** — R5 (Severity High), R6 (Severity 1, related framing). Need `RewardCollectedFrom` postfix to re-evaluate.

### Single-reviewer items with strong merit

17. **Save/Load loophole** — R6 (Severity 1). Static counters reset on reload because `_lastSeenRunId` mismatch resets the budget. Streamer save-scums for infinite skips.
18. **Godot `_Ready` patchability unverified** — R3 (highest technical risk). If unpatchable, the entire skip-gate initialization breaks.
19. **30s × 16 votes/act pacing** — R5. ~8 minutes of pure waiting per act. Pacing crisis.
20. **Skip receipts must use `OutgoingMessageQueue`** — R7. Otherwise risk Twitch shadow-ban. Reality-checked: `coordinator.Chat.SendMessageAsync(text, priority)` DOES route through the queue ([TwitchIrcChatService.cs:285-292](../../src/Ti/Chat/TwitchIrcChatService.cs#L285-L292)) — so the fix is just specifying the call path explicitly.
21. **Random fallback should respect skip budget** — R7. If chat sends zero votes AND `cardSkipsPerAct > 0`, fallback could skip instead of forcing a random card. Counter-argument: Decision 10 explicitly chose "play the game" semantics.
22. **MP-bail consistency check for both new patches** — R7. Currently the spec only mentions Players.Count > 1 for the gate; vote patch needs the same (verify accessor from `NCardRewardSelectionScreen` context).
23. **Operator step for clicking Abandon Run mid-vote** — R7. Currently no operator step actually validates the run-ID guard fires in real runtime.

---

## A.3 — Outlier Points (single-reviewer, judgment calls)

Reviewer 4 suggests a small `IRewardSkipGate` interface as light abstraction at n=2. **Reject**: this directly contradicts Decision 13 (Rule of Three, deferred to B.2.2). Other reviewers explicitly endorse the deferral (R2: "Don't let any reviewer talk you out of this"; R3, R7 same).

Reviewer 2 suggests detecting "skip without looking" via a `_Ready` postfix on `NCardRewardSelectionScreen` setting a flag. R6 disagreed with this — said "Counting unopened card skips is correct. If they didn't cost a skip, the streamer could blindly bypass chat without consequence." **Defer to R6's reasoning**: don't differentiate; the budget already bounds the bypass.

Reviewer 6 suggests patching `StartRun` / `TransitionToAct` for precise reset rather than lazy `_Ready` detection. **Optional enhancement**: more precise but adds patch surface; lazy is acceptable per the spec's existing trade-off statement.

Reviewer 7 suggests `RichTextLabel` for `CardSkipCounterLabel` from day one to leave colour open. **Optional enhancement**: small change, leaves design space open.

Reviewer 7 suggests rendering "unlimited" or "inf" instead of `∞` for glyph safety. **Should-do**: cheap, prevents broken-looking UI.

---

## A.4 — Category Breakdown with Reality Checks

### 🏗️ Architecture & Design

| Item | Reviewers | My reality check | Verdict |
|---|---|---|---|
| EnglishReceipts seam violation | 1, 2, 3, 4, 5, 7 | Confirmed: `EnglishReceipts.cs` lives in `src/Ti/Voting/`. Adding card-domain helpers there violates the seam. | **Must-do**: inline in `Game/`. |
| Settings access pattern (`ModEntryState.Settings`) | 1, 2, 3, 5, 7 | Confirmed: B.1's `ModEntry` does not expose settings via a static accessor. The skip gate's reference to `ModEntryState.Settings` is fictional. | **Must-do**: define `ModEntry.Settings` static + mark `ModEntry.cs` as ✏️ in architecture. |
| Skip receipts must use `OutgoingMessageQueue` | 7 | Reality-checked: `IChatService.SendMessageAsync(text, priority)` routes through the queue automatically in `TwitchIrcChatService`. Just need the spec to be specific about calling `coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal)`. | **Must-do**: specify call path. |
| Pure `SkipBudgetTracker` extraction | 1, 2 | Different concern from the deferred suspend/resume helper — this is pure logic for testability. Doesn't violate Rule of Three. | **Optional Enhancement** (Surfinite preference for clean code may favour). |
| `IRewardSkipGate` interface at n=2 | 4 | Contradicts Decision 13. | **Reject**. |
| Patch `StartRun` / `TransitionToAct` for precise reset | 6 | Adds patch surface. Lazy detection is documented and acceptable. | **Optional Enhancement**. |

### ⚠️ Risks & Concerns

| Item | Reviewers | My reality check | Verdict |
|---|---|---|---|
| **Run-ID guard / `DebugOnlyGetState()` fragility** | 1, 2, 3, 4, 5, 6, 7 (universal) | The method name signals debug-only, but it's used in `ActConsoleCmd.Process` etc — runtime code paths. Likely works in modded builds but worth verification. The exact field for "stable run ID" is unverified — spec says "Guid? noted as placeholder". | **Must-do**: tighten spec — verify `DebugOnlyGetState()` returns non-null in modded production; pin exact field/type in `Prepare`; fail-open with `Warn` log if guard can't capture; apply guard to Neow too. |
| **Static `_activeLabel` dangling pointer** | 1, 2, 3, 5, 6 | Real Godot bug pattern. C# reference outlives native instance. | **Must-do**: `IsInstanceValid` guard before every use, OR null on `_ExitTree` postfix on `NRewardsScreen`. |
| **`DisallowSkipping()` persists, locks all reward types** | 5, 6 | Reality-checked NRewardsScreen: vanilla's `_skipDisallowed = true` is sticky; `TryEnableProceedButton` checks `(!_skipDisallowed || !_proceedButton.IsSkip)`. After we set it, only the Skip-mode button is disabled — the vanilla state machine then transitions the button to Proceed mode once all rewards are claimed. So actually: **once card is claimed, `_proceedButton.IsSkip` becomes false (Proceed mode), the OR in `TryEnableProceedButton` becomes true, and Proceed is enabled** — **the gate self-corrects**. R5/R6's concern is partially mitigated by vanilla's existing logic. BUT: if the streamer has remaining UNCLAIMED non-card rewards (e.g., gold), the proceed button stays in Skip mode, and the gate's `_skipDisallowed = true` blocks the streamer from skipping the gold too. R5's concern stands for this case. | **Should-do**: document the actual behaviour explicitly; add operator-validation case for "card + gold, claim card via vote, verify gold can still be skipped/claimed". If gold IS blocked, add `RewardCollectedFrom` postfix to re-evaluate. |
| **Save/Load loophole** | 6 | Real. Static state is process-lifetime; reload doesn't trigger run-change detection because the SAME run-ID is captured and run continues. Wait — actually if save-quit-reload preserves run, `_lastSeenRunId` matches → counter doesn't reset → no infinite skip. The exploit would only work if save-quit-reload generates a NEW run-id, which is unusual. **Reality check needed**: does StS2 give the same RunState.Id after save+reload? If yes, no exploit. If no, real exploit. Reviewer 6 assumed the latter. | **Must-do**: document as known-fragility edge case; defer fix to v0.2 unless reality-check shows real exploit; add a `notes/06` follow-up. |
| **Godot `_Ready` patchability** | 3 | I don't know definitively whether HarmonyLib patches Godot lifecycle methods. B.1 patches `OptionButtonClicked` (not a Godot lifecycle method); precedent doesn't apply. Worth verifying with a tiny smoke before committing the postfix body. | **Should-do**: add explicit verification step in implementation plan ("Task 1: write `_Ready` postfix that just logs; verify it fires on rewards screen"). If it fails, fall back to `_EnterTree` or `_Notification` patching. |
| **Reroll-mid-vote exploit** | 1, 2 | Real concern. Streamer-incentive analysis: clicking a card commits to vote → watching tally → rerolling resets vote freely. Defeats the gate. R6 implicitly accepted this (alternates as intended escape valve), but the exploit is more pointed than alternates because it's specifically vote-cancellation. | **Should-do**: explicit Decision — chat receipt on reroll-mid-vote (`vote cancelled — streamer rerolled`). Lighter than patching reroll; provides social pressure. |
| **30s vote × 16 combats/act pacing** | 5 | Real concern. 8 min/act of waiting is a lot. But reducing to 15s reduces chat engagement window. Genuine trade-off. | **Should-do**: document as pacing concern; consider 20s default for card votes specifically (settings-driven duration is B.2.4 territory; v0.1 needs to pick one number). |
| **Multi-card-reward gate re-evaluation** | 1, 6 | Real edge case. Vanilla can have rewards screens with multiple card rewards (rare but possible). If skip exhausts budget on first card, second card's Proceed should also be disabled. | **Must-do**: re-evaluate gate inside `RewardSkippedFrom` postfix after counter increment. |
| **`HasUnclaimedCardReward` claimed-state precision** | 1, 4, 6 | Reality check: vanilla `RewardCollectedFrom` removes the button from `_rewardButtons` (per the decompiled source: `int a = _rewardButtons.IndexOf(button); RemoveButton(button);`). So checking `_rewardButtons` for any CardReward-typed button automatically excludes claimed ones. R6's concern is partially addressed by vanilla. BUT: skipped buttons are added to `_skippedRewardButtons`; need to check if they're also removed from `_rewardButtons`. Likely yes given the structure. | **Should-do**: spec must verify in `Prepare` that skipped/collected buttons are removed from `_rewardButtons`; if not, exclude them via `_skippedRewardButtons`. |

### 🗑️ Suggested Removals / Simplifications

| Item | Reviewers | Verdict |
|---|---|---|
| Drop `cardSkipsPerRun` for v0.1 (YAGNI) | 2, 5, 7 | **Optional Enhancement**: Surfinite explicitly chose dual-budget during brainstorming ("Can we have Per Act and Per Run please. selectable in the settings."). Reject auto-applying. Surface as pick-list option for re-decision. |
| Open Question 1 ("MP label") | 2 | Already answered by `Players.Count > 1` bail. **Should-do**: delete. |
| Reference to "upgrade `CardSkipCounterLabel` to `RichTextLabel`" speculation | 2 | Speculation; not actionable. **Should-do**: remove or move to a v0.2 footnote. |
| Reference to nonexistent B.1 `voteDuration` settings key | 1 | Already partially fixed in v1 (the parenthetical was changed). **Verify in v2 spec**. |

### ➕ Suggested Additions / Features

| Item | Reviewers | Verdict |
|---|---|---|
| Multi-reward-type acceptance test | 1, 5, 6 | **Must-do**: add Step explicitly testing card + gold/potion/relic. |
| Abandon-run-mid-vote operator step | 7 | **Must-do**: validates run-ID guard fires in real runtime. |
| MP-bail in vote patch (not just gate) | 7 | **Must-do**: vote patch also bails on MP. Need to verify accessor from `NCardRewardSelectionScreen` context. |
| `Prepare` shape check for `_proceedButton` | 7 | **Should-do**: degraded label position if missing; explicit Warn log. |
| `Prepare` `if (CardRewardVotePatch.VoteInProgress) return;` in skip gate | 7 | **Should-do**: safety against modal-blocking assumption. |
| Run-ID guard log Warn (not Info) | 7 | **Should-do**: makes the safety feature observable. |
| Comprehensive reflected-members list per patch | 2 | **Should-do**: makes `Prepare` exhaustive. |
| Pure `SkipBudgetTracker` extraction | 1, 2 | **Optional**: testability win. |
| Telemetry log line at every "vote silently absorbed" path | 2 | **Should-do**: logs are the only debugging signal given "no error toast" policy. |
| Explicit AutoSlay interaction handling | 2 | **Should-do**: document expected behaviour (AutoSlay's `EmitSignal(Pressed)` will trigger our vote — that's fine if AutoSlay is off in production). |
| Card name uniqueness in receipts | 5 | **Optional**: edge case; document as known limit. |
| `notes/06` entry listing every reflected sts2.dll member | 2 | **Optional**: maintainability win. |
| Negative skip values other than -1 test | 7 | **Should-do**: small test addition. |

### 🔄 Alternative Approaches

| Item | Reviewers | Verdict |
|---|---|---|
| **Extend `VoteTallyLabel` instead of separate `CardSkipCounterLabel`** | 2, 3 (alt), 5 (alt), 6 (alt), 7 (alt-implied) | **Optional Enhancement**: 4 reviewers prefer this. BUT Surfinite explicitly said in brainstorming "A 'Card skip's remaining' counter will have to get displayed close to the proceed button". Genuine conflict between reviewer consensus and stated requirement. **Surface as pick-list for explicit re-decision.** Pros (per reviewers): no anchor risk, no `_activeLabel` dangle, eliminates ~70 LOC, reuses Root z-order. Cons: less spatial co-location with action; counter only visible during votes (not when streamer is on rewards screen pre-vote). |
| OR semantics for dual budget instead of AND | 3 | Rejected by R3 themselves on reflection. AND is correct for absolute caps. **No change**. |
| Composite run-id fingerprint as fallback to DebugOnlyGetState | 7 | **Should-do** if `DebugOnlyGetState` proves null in production; otherwise unnecessary. Add as fallback strategy. |

### ✅ Confirmed Good / Keep As-Is

- **Suspend-and-resume reuse from B.1** — endorsed by all 7 reviewers
- **Decision 3: piggyback `DisallowSkipping()`** — universal endorsement
- **Decision 7: skip never a chat-vote option / chat-vs-streamer asymmetry framing** — universal endorsement; multiple reviewers called this the spec's strongest contribution
- **Decision 13: Rule of Three / no helper extraction** — endorsed by R1, R2, R3, R7; R4 weakly suggested an interface and was reality-checked as contradictory
- **Decision 14: vanilla DevConsole, no debug patches** — endorsed
- **Run-ID guard concept** — endorsed; only the implementation specifics are contested
- **Fail-soft degradation matrix** — endorsed
- **Independent `Prepare` failure isolation between vote and gate patches** — explicitly endorsed by R5
- **Vote-on-click model preserved** — endorsed by R7

### 🔧 Implementation Details & Nits

| Item | Reviewers | Verdict |
|---|---|---|
| `Interlocked` consistency | 2, 3, 5, 7 | **Must-do**: drop `Interlocked` for skip counters; document as main-thread-only. (Vote patch's `Interlocked.CompareExchange` for `_voteInProgress` stays — that flag genuinely crosses threads.) |
| Receipt counter semantics: used vs remaining; never `0/∞` | 1, 5, 6 | **Must-do**: pick one convention (recommend: receipt = used/limit, label = remaining/limit), drop run-half from receipt when run is unlimited. |
| `VoteTallyLabel.AttachTo` in `HandleVoteAsync` | 3, 5 | **Must-do**: explicit mention in spec to prevent copy-paste oversight. |
| Card-holder re-derivation at resume | 1, 5 | **Must-do**: use `holders[winnerIndex]` from current screen state, not captured ref. |
| Acceptance Step 0 / Step 1 split | 2, 3 | **Should-do**: Step 0 = pure no-regression (no card path); Step 1 = card defaults. |
| LOC estimates may be low | 2, 3, 5 | **Optional**: bump to ~230-250 for `CardRewardVotePatch`; not blocking. |
| Latest-vs-first fallback semantics on rapid clicks | 1 | **Should-do**: state explicitly that first click sets fallback; subsequent swallowed. |
| Comment on `_multiplayerWarnFired` purpose | 2, 5 | **Optional nit**. |
| Use `unlimited`/`inf` instead of `∞` (glyph safety) | 7 | **Should-do**: cheap insurance. |
| `Lazy<FieldInfo?>` pattern for skip gate too | 5 | **Should-do**: matches B.1 convention. |
| TiLog `[SlayTheStreamer2]` prefix | 4 | **Optional nit**. |

### 📦 Dependencies & Integration

| Item | Reviewers | Verdict |
|---|---|---|
| Skip receipt routing through `OutgoingMessageQueue` | 7 | **Must-do**: specify `coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal)` explicitly. Reality-check confirmed this routes through the queue. |
| AutoSlay → `EmitSignal(Pressed)` → vote fires | 2 | **Should-do**: document expected behaviour. |
| `ModEntry.cs` change for settings accessor | 1, 2, 3, 5, 7 | **Must-do**: mark `ModEntry.cs` as ✏️ in architecture; show the static. |

### 🔮 Future Considerations

- **Persist skip counters in save** for save/load resilience (R6) — defer to v0.2; minimum: document loophole in v0.1.
- **Per-card-vote duration override** (R5) — defer to B.2.4 settings UI.
- **Reroll-as-skip-counter patching** (R2 suggested, but compromise is chat receipt only) — defer to v0.2 if streamers report exploit usage.
- **`SkipBudgetTracker` test extraction** (R1, R2) — Optional enhancement; could land in B.2.1 or later.
- **AllowSkipping re-enable after card claim** (R5) — depends on whether the multi-reward-type lockout is observed in operator validation.
- **Skip-without-looking detection** (R2) — defer; budget already bounds bypass.
- **Per-relic curation** (already in spec) — v0.2.

---

## A.5 — Conflicts & Contradictions

### Conflict 1: Separate label vs extending VoteTallyLabel

- **R3, R5, R6, R7 (4 reviewers)**: extend `VoteTallyLabel` — eliminates anchor risk, dangling-ref bug, and ~70 LOC
- **R1, R2 (silent on this question or supporting separate)**: separate label preserves spatial co-location
- **Surfinite (brainstorming)**: explicitly stated "A 'Card skip's remaining' counter will have to get displayed close to the proceed button"

**My recommendation**: **Surface as Optional Enhancement.** The reviewer majority leans extend, and the engineering arguments are strong, but Surfinite stated a specific spatial requirement during brainstorming. He should re-decide with the reviewer concerns explicit. If he picks extend, we save 70 LOC + dangling-ref bug; if he sticks with separate, we add the `IsInstanceValid` guard (which is Must-do regardless of label location).

### Conflict 2: Drop `cardSkipsPerRun` for v0.1

- **R2, R5, R7 (3 reviewers)**: drop — per-run knob is inert under default config; YAGNI
- **R1 (mild)**: dual-budget is "reasonable for v0.1"
- **R3 (mild)**: dual model has genuine utility for longer runs (act 4, endless mode)
- **Surfinite (brainstorming)**: explicitly chose dual-budget — "Can we have Per Act and Per Run please. selectable in the settings."

**My recommendation**: **Surface as Optional Enhancement.** Surfinite explicitly chose this; auto-removing would override a deliberate decision. But 3 reviewers raising YAGNI is signal worth surfacing.

### Conflict 3: Reroll-mid-vote handling

- **R2**: patch reroll-counts-as-skip (preferred)
- **R1**: send chat receipt (cheaper)
- **R6**: silently absorb is fine ("alternates as intended escape valve")
- **Spec v1**: silently absorb (matches R6)

**My recommendation**: **Should-do compromise** — send chat receipt on reroll-mid-vote (`vote cancelled — streamer rerolled`). Cheap transparency, makes social pressure possible without additional patch surface. R6's "intended escape valve" framing is preserved (no patch on reroll itself), but chat sees what happened.

### Conflict 4: Random fallback respects skip budget?

- **R7**: random fallback should pick skip when chat is silent and budget allows
- **Spec v1 / Decision 10**: random fallback never picks skip ("play the game" semantics)

**My recommendation**: **Reject the change**. Decision 10's "play the game" rationale is correct — chat-silence shouldn't auto-trigger a streamer-only escape valve. R7's argument is reasonable (consistency with streamer-ownership) but Decision 10's framing is stronger. Document the explicit rationale in v2 to pre-empt future rebuttals.

### Conflict 5: Run-ID guard for Neow

- **R1, R2**: add it to Neow now (template consistency)
- **No reviewer disagrees**

**My recommendation**: **Should-do**. ~5 LOC, prevents two subtly-different suspend/resume templates entering B.2.2.

---

## A.6 — Recommended Plan Changes

### Must-do (auto-applied to v2)

1. **M1 — Inline skip receipt formatting; do not modify `EnglishReceipts`.** Add `FormatSkipReceipt` static helper in `CardRewardSkipGatePatch.cs`. (R1, R2, R3, R4, R5, R7 — universal)

2. **M2 — Define `ModEntry.Settings` static accessor.** Mark `ModEntry.cs` as ✏️ in architecture; specify type as `SettingsResult.Success?` set during init. Patches read via `ModEntry.Settings`. (R1, R2, R3, R5, R7)

3. **M3 — `_activeLabel` lifecycle: `IsInstanceValid` guard before every use.** Plus null-on-`_ExitTree` postfix as belt-and-suspenders. (R1, R2, R3, R5, R6)

4. **M4 — Run-ID guard: tighten implementation.** Verify `DebugOnlyGetState()` returns non-null in modded production builds (verification step in implementation plan). Pin exact field name + type in `Prepare`. Fail-open with `Warn` log if guard cannot capture run-id. **Apply guard to `NeowBlessingVotePatch` too** (template consistency — see Conflict 5). (R1, R2, R3, R4, R5, R6, R7)

5. **M5 — Re-evaluate gate inside `RewardSkippedFrom` postfix.** After counter increment, re-check `HasUnclaimedCardReward(__instance) && !budgetRemaining` and call `__instance.DisallowSkipping()` to prevent multi-card-reward bypass. (R1, R6)

6. **M6 — `HasUnclaimedCardReward` precision.** `Prepare` must verify that `RewardCollectedFrom` / `RewardSkippedFrom` remove buttons from `_rewardButtons` (or exclude via `_skippedRewardButtons`). (R1, R4, R6)

7. **M7 — Drop `Interlocked` for skip counters; document main-thread-only.** Vote patch's `Interlocked.CompareExchange` for `_voteInProgress` stays (genuinely cross-thread). (R2, R3, R5, R7)

8. **M8 — Receipt counter semantics: used/limit consistently; suppress run-half when unlimited; never `0/∞`.** Format: `Streamer skipped a card reward (1/1 act)` when run unlimited; `(1/1 act, 2/3 run)` when both finite. Label uses remaining/limit consistently: `Card skips: 0/1 act, ∞ run`. (R1, R5, R6, R7)

9. **M9 — Skip receipts route through `OutgoingMessageQueue` via `coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal)`.** Reality-checked: this path enqueues into the rate-limited queue. (R7)

10. **M10 — `VoteTallyLabel.AttachTo(session)` explicit mention in `CardRewardVotePatch.HandleVoteAsync`.** Prevents copy-paste oversight. (R3, R5)

11. **M11 — Card-holder re-derivation at resume.** `ResumeOnMainThread` reads current holders from screen state and indexes by winner index, not captured `NCardHolder` ref. (R1, R5)

12. **M12 — MP-bail in vote patch.** Add `Players.Count > 1` bail to `CardRewardVotePatch` (verify accessor from `NCardRewardSelectionScreen` context — likely via `RunManager.Instance.DebugOnlyGetState()?.Players?.Count`). (R7)

13. **M13 — Save/Load loophole documentation.** Add to failure modes table; add to `notes/06` as known v0.1 limitation; defer fix to v0.2. (R6)

14. **M14 — Acceptance gate additions:** (a) split Step 0 from Step 1; (b) add multi-reward-type case (card + gold/potion); (c) add Abandon-Run-mid-vote case to actually validate run-ID guard fires. (R2, R3 / R1, R5, R6 / R7)

### Should-do (auto-applied to v2)

15. **S1 — Verify Godot `_Ready` patchability before implementation.** Add explicit verification step: implementer writes a no-op `_Ready` postfix that just logs, runs the mod, confirms it fires when a rewards screen appears. Fall back to `_EnterTree` or `_Notification` if `_Ready` proves unpatchable. (R3)

16. **S2 — Reroll-mid-vote: send chat receipt** `vote cancelled — streamer rerolled`. Compromise between R2's patch-reroll and R6's silent-absorb. (R1, R2 conflict-resolved via R6's framing preservation)

17. **S3 — Vote duration: keep 30s default with explicit acknowledgment.** Document the pacing concern (~16 votes/act × 30s = 8 min/act). Settings-driven duration is B.2.4. Note that 30s is preserved for B.2.1 to maintain consistency with Neow; B.2.4 will introduce per-vote duration tuning. (R5)

18. **S4 — `Prepare` additions:** verify `_proceedButton` field; verify `_rewardButtons` field name and element type; verify `RewardCollectedFrom` / `RewardSkippedFrom` button-removal semantics. Use `Lazy<FieldInfo?>` pattern matching B.1. (R4, R5, R7)

19. **S5 — Skip gate `if (CardRewardVotePatch.VoteInProgress) return;` guard.** Belt-and-suspenders against modal-blocking assumption. (R7)

20. **S6 — Run-ID guard log level: Warn (not Info).** Makes the safety feature observable. (R7)

21. **S7 — Comprehensive "Reflected members" subsection per patch.** Lists every type/field/method by name. (R2)

22. **S8 — Operator step for AutoSlay interaction documentation.** AutoSlay is off in production; if on, our vote patch fires (acceptable). (R2)

23. **S9 — Replace `∞` with `unlimited` (or `inf`) in receipts and label.** Glyph rendering safety. Could keep `∞` in label only if we test the glyph during Step 1. (R7)

24. **S10 — Telemetry log lines** at every "vote silently absorbed" path: run-ID mismatch, IsInstanceValid fail, bounds check fail, post-Start fallback. All Info level for path-taken, Warn for unexpected (run-ID mismatch is Warn per S6). (R2)

25. **S11 — Negative-other-than-`-1` settings test.** `cardSkipsPerAct: -5` clamps to `-1`. (R7)

26. **S12 — Document `DisallowSkipping()` lifecycle behaviour explicitly:** vanilla's existing logic transitions Proceed button mode after card claim, but if non-card unclaimed rewards remain and skip budget is 0, the streamer will be locked out of skipping them too. Operator validation Step covers this. If observed as friction, B.2.2 follow-up adds `RewardCollectedFrom` postfix to re-evaluate. (R5, R6)

27. **S13 — Latest-vs-first fallback semantics:** state explicitly that the first card click sets the playerClickIndex fallback; subsequent clicks during vote are swallowed (no fallback update). Matches B.1 Neow behaviour. (R1)

28. **S14 — Remove the speculative "upgrade `CardSkipCounterLabel` to `RichTextLabel`" sentence.** Move to v0.2 footnote or delete. (R2)

29. **S15 — Delete Open Question 1 ("MP label").** Already answered by `Players.Count > 1` bail. (R2)

### Consider (Optional Enhancements pick-list — see v2 spec)

30. **C1 — Drop `cardSkipsPerRun` for v0.1.** YAGNI; per-act × acts ≈ same cap. (R2, R5, R7) — Conflicts with Surfinite's brainstorming preference.
31. **C2 — Extend `VoteTallyLabel` instead of separate `CardSkipCounterLabel`.** 4 reviewers prefer; conflicts with stated spatial-co-location requirement. (R3, R5, R6, R7)
32. **C3 — Pure `SkipBudgetTracker` extraction** for testability. (R1, R2)
33. **C4 — Patch `StartRun` / `TransitionToAct` for precise reset** (avoid lazy detection). (R6)
34. **C5 — `RichTextLabel` for `CardSkipCounterLabel` from day one** (future colour). (R7)
35. **C6 — Composite run-id fingerprint as fallback** to `DebugOnlyGetState()`. (R7)
36. **C7 — Card name uniqueness disambiguation in receipts** (e.g., suffix duplicates). (R5)
37. **C8 — Skip-without-looking detection** via sub-screen `_Ready` postfix. (R2)
38. **C9 — `notes/06` entry listing every reflected sts2.dll member.** (R2)
39. **C10 — TiLog `[SlayTheStreamer2]` prefix** for greppability. (R4)

### Reject (with reason)

- **`IRewardSkipGate` interface at n=2** (R4) — Contradicts Decision 13 (Rule of Three). Multiple other reviewers explicitly endorse the deferral.
- **Random fallback respects skip budget** (R7) — Decision 10's "play the game" rationale is correct; chat-silence shouldn't auto-trigger streamer-only escape valve.
- **Skip-without-looking distinct from looked-but-didn't-pick** (R2) — R6's reasoning ("the budget already bounds the bypass") is correct. Don't need precision.
- **Patching `OnProceedButtonPressed` directly** (not actually proposed but worth documenting as rejected) — `DisallowSkipping()` is the better surface.

---

## A.7 — What Stays (explicit confirmation)

These elements of the v1 spec are validated by reviewer consensus and remain unchanged in v2:

- **Decision 1**: B.2.1 covers card reward only.
- **Decision 2**: Patch target `NCardRewardSelectionScreen.SelectCard(NCardHolder)`.
- **Decision 3**: Skip-gate piggybacks on `NRewardsScreen.DisallowSkipping()`.
- **Decision 5**: Suspend-and-resume pattern reused verbatim from B.1.
- **Decision 7**: Skip is never a chat-vote option (chat-vs-streamer asymmetry).
- **Decision 8**: Dual budget per-act + per-run, both enforced (AND semantics) — pending Optional Enhancement C1 if user re-decides.
- **Decision 10**: Random fallback never picks skip.
- **Decision 11**: Receipt format: name-only.
- **Decision 12**: Reroll, alternates, non-`SelectCard` buttons not patched (modified slightly for chat-receipt-on-reroll per S2).
- **Decision 13**: No helper / base class extraction in B.2.1 (Rule of Three).
- **Decision 14**: Use vanilla DevConsole for dev iteration.
- **TI/Game seam**: Unchanged. The EnglishReceipts violation was caught by reviewers and is now explicitly inlined in Game/.
- **Suspend-and-resume hard constraint**: Unchanged.
- **Failure-soft posture**: Unchanged; extended with new failure modes.
- **Vote-on-click model**: Unchanged.
- **Independent `Prepare` failure isolation between vote and gate patches**: Unchanged.

---

## Reviewer-acknowledged spec strengths (preserve these in all future B.2.x specs)

These came up as positively confirmed across multiple reviewers and represent the spec's strongest patterns to carry forward to B.2.2 and beyond:

1. **Explicit failure-mode table with row-per-failure-path**.
2. **Independent `Prepare` validation per patch class** (so one patch can degrade without taking the other down).
3. **Decisions table with rationale per row**.
4. **Cross-references to decompiled source** in spec (helps the implementer; reviewers without code access flag this as helpful even when they can't follow the links).
5. **Explicit "Author's note" framing key design constraints** (chat-vs-streamer asymmetry, Rule of Three, lean-on-vanilla).
6. **Reality-checked numbers** where possible (LOC estimates, schemaVersion, cap defaults).
7. **Explicit non-goals list** preventing scope creep.
