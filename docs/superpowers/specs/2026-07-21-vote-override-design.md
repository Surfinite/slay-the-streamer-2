# Vote Override — design spec (2026-07-21)

Streamer-side balance feature, directly requested by a streamer ("his chat are too
good at denying him wins"). The streamer may **override a running vote** a
configurable number of times per act (default 1) by clicking an option while the
countdown runs — the vote ends immediately with the clicked option as the winner.

Feature brief: `notes/12-vote-override-feature-brief.md`. Approach chosen in
brainstorm: **first-class early-resolve in `Ti/Voting` + game-side outcome
routing** (over pure-game-side `Cancel()`+direct-apply, and over
synthetic-super-vote injection).

## Decisions locked with Surfinite (2026-07-21)

1. **Scope**: card-reward votes (primary) + ancient votes (verified nearly free —
   identical suppressed-click branch, prefix already holds the clicked index).
   Act-variant vote **excluded**. Boss votes excluded (custom popup, no native
   click surface).
2. **Receipt**: override + remaining count, e.g.
   `Surfinite overrode the vote and took Ricochet. 1 override remaining this act`.
3. **Arming delay**: fixed **1.5s** named constant, not a setting. Its only job is
   swallowing the double-click that opened the vote.
4. **Same-pick consumes**: an armed override click always consumes an override,
   even if it picks the option chat was already winning on. No tally-race
   ambiguity, no free early-endings.
5. **Skip during a vote consumes an OVERRIDE, not a card skip** (from the brief):
   an override is more powerful than a pre-vote skip — the streamer has seen what
   chat is voting for. Pre-vote skip behavior and the skip budget are unchanged.
6. **Counter differentiation is colour-first** (amended after Section 3 review):
   the emphasised fragment of the skip counter renders in `StsColors.blue`
   (`#87CEEB`) and of the override counter in `StsColors.gold` (`#EFC851`) —
   exactly the colours the vanilla compendium uses for its Uncommon / Rare
   filter labels (`card_library.tscn` label modulates). Bold stacks on top if a
   bold Kreon variant exists.

## 1. Player-visible behavior

- Vote start is unchanged: streamer clicks a card (or an ancient option), vote
  countdown opens exactly as today.
- While the countdown runs, if the streamer has >0 overrides remaining and ≥1.5s
  have elapsed since vote start: a click on a card option (or the Skip button on
  card votes, or an ancient option) **ends the vote immediately with the clicked
  option as the winner** and consumes one override.
- Clicks inside the 1.5s arming window are silently suppressed exactly as today
  and consume nothing (this eats the opening double-click).
- With 0 overrides remaining (or the feature disabled), all clicks during a vote
  behave exactly as today (silently suppressed).
- Reroll and the parent rewards-screen Proceed button stay blocked during a vote
  regardless of overrides — reroll would rebuild `_options` mid-vote; Proceed is
  the parent commit path, out of scope.
- Chat sees the override receipt (Decision 2) in place of the normal
  "Chat chose #X" close receipt. The on-screen popup and corner tally tear down
  exactly as on a natural vote end; no special override visual.
- Counter text (card-vote screens only): while no vote runs, the existing label
  reads `{Streamer} has N card skips remaining this act` with the
  `N card skips` fragment in `StsColors.blue`; while a vote runs it swaps to
  `{Streamer} has N vote overrides remaining this act` with the
  `N vote overrides` fragment in `StsColors.gold`. Hidden during a vote when 0
  overrides remain (i.e., exactly today's hide-during-vote behavior). Ancient
  votes get **no counter label** this slice (no natural anchor position in the
  event room; the receipt carries the remaining count) — follow-up if streamers
  ask.
- Budget resets per act and per run, mirroring card skips. Chat sees
  `Vote overrides reset to N for Act M` under the same suppression rules as the
  skip-reset receipt (nothing when limit ≤ 0 or act unknown).

## 2. Architecture

### 2.1 `Ti/Voting` — forced-winner close (`VoteSession`)

New API, TDD'd first:

- **`bool TryCloseNow(int forcedWinnerIndex)`** — returns `false` (doing
  nothing) unless `State == Open`; throws `ArgumentOutOfRangeException` if the
  index isn't a valid option index. On success, performs the full close
  sequence (unhook chat handlers, dispose timers, `State = Closed`, complete
  the winner TCS, fire `Closed`) with these differences from natural close:
  - `WinnerIndex = forcedWinnerIndex` — `ComputeWinner()` is not consulted.
  - The snapshot carries a new **`ForcedWinner: bool`** field (appended to
    `VoteSnapshot` with a default value so existing construction sites are
    untouched).
  - **No close receipt is sent.** The caller owns override messaging — the
    receipt needs the streamer name and remaining-budget count, which are
    game-side concepts Ti must not know (same seam reasoning as
    `SendSkipReceipt`).
- **`TimeSpan Elapsed`** => `_clock.UtcNow - _openedAt` — for the arming check;
  testable with `FakeClock`.

No new terminal state or event. Reusing `Closed` means popups (which subscribe
to `Closed` + `Cancelled` per the CLAUDE.md landmine), the corner tally, and
`AwaitWinnerAsync` all behave correctly with zero changes: the awaiter completes
with the forced index and the existing resume machinery applies it.

The `Try` shape exists for the close-timer race: if natural expiry fires just
before the override click, `TryCloseNow` returns `false` and the caller must
not consume an override. Both paths run on the main thread via the dispatcher,
so this is belt-and-braces, not a live race.

### 2.2 `VoteOverrideBudget` (new, `src/Game/DecisionVotes/`, Godot-free)

Static holder owning the override budget, shared by the card patch, the ancient
patch, and the counter label:

- Wraps an instance of the renamed tracker (§2.5): `Observe(runId, actIndex)`
  (returns reset reason for the reset receipt), `TryConsume(limit)`,
  `Remaining(limit)`.
- Owns receipt-text formatting (`FormatOverrideReceipt(takenLabel, remaining)`,
  `FormatResetReceipt(limit, humanAct)`) as pure string functions — unit-tested
  without Godot. Sending goes through `Voter.Default.Chat` gated on
  `ConnectedReadWrite`, same as `SendSkipReceipt`.
- Also exposes the arming constant: `OverrideArmingDelay = 1.5s`.

### 2.3 Card-reward integration (`CardRewardVotePatch`)

The patch gains a static *override context* — the current `VoteSession` +
`includeSkip` flag — set when a vote opens, cleared when the resume paths reset
`_voteInProgress`. Needed because the override click arrives in a different
prefix invocation than the one that started the vote.

**Card click during vote** — in the existing suppressed-click branch (the
`Interlocked.CompareExchange(_voteInProgress) != 0` arm of `Prefix`): if the
feature is enabled, `session.Elapsed >= OverrideArmingDelay`, and
`TryConsume`-able → map the clicked holder to its card index (existing
`FindHolderIndex` against `GetCurrentHolders`), shift by one if `includeSkip`,
and call `session.TryCloseNow(voteIndex)`. On `true`: consume the override,
send the override receipt, update the counter label. The prefix returns `false`
either way — the winner is applied by the normal resume path re-invoking
`SelectCard` with `_resumeInProgress = 1`, not by letting this click through.
Check `TryCloseNow`'s result BEFORE consuming (never consume on a lost race).

**Skip click during vote** — in the vote-in-progress blocked branch of
`NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix`, when the clicked
alt is the Skip alt (existing `FindSkipAlternativeIndex`), same arming/budget
checks, then:

- `includeSkip == true`: Skip is vote option `#0` → `TryCloseNow(0)` → the
  normal `ResumeSkipOnMainThread` path runs (chat-skip semantics: reward
  consumed outright via the flipped `EndSelectionAndCompleteReward` alt;
  `_chatSkipResumeInProgress` keeps it card-skip-budget-free — override-skip
  charges the override budget only, per Decision 5).
- `includeSkip == false`: Skip isn't a vote option, so there is no index to
  force. Set a static `_overrideSkipPending` flag, then `session.Cancel()`.
  `HandleVoteAsync` gains a `catch (OperationCanceledException)` that checks
  the flag: set → route to `ResumeSkipOnMainThread` (and clear the flag);
  not set → preserve today's cancellation behavior (fall back to player click;
  run-death liveness checks then drop it). Run-death cancellation must keep
  its current behavior — only the flag routes to skip-resume.

An override consumes at click time; if the resume later aborts (screen freed,
run died, options changed), the override stays spent — same "the world moved
on" semantics as the existing cancellation receipt; no refund plumbing.

### 2.4 Ancient integration (`AncientVotePatch`)

The identical suppressed-click branch (`AncientVotePatch.cs:119`) gets the same
armed/budget check. The prefix already holds the clicked option `index`, and
ancient vote options map 1:1 to indices (no skip concept, no holder mapping) →
`session.TryCloseNow(index)` directly. Receipt takes the clicked option's
label. Same static override-context pattern (current session) as the card
patch. `VoteOverrideBudget.Observe` is called in the prefix before the budget
check.

### 2.5 Budget tracker generalization

Rename `SkipBudgetTracker` → `ActBudgetTracker` (+ `SkipBudgetSnapshot` →
`ActBudgetSnapshot`; member names generalized: `ActSkipsUsed` → `ActUsed`,
`RecordSkip` → `RecordUse`, `IsSkipAllowed` → `IsUseAllowed`). The class is
already budget-agnostic per-act arithmetic; existing tests carry over under the
new names. Two instances: the existing one in `CardRewardSkipGatePatch`, a new
one inside `VoteOverrideBudget`.

Override-budget observation points: `NRewardsScreen._Ready` postfix (one line
next to the existing skip observation — primary reset receipt site) and
defensively at each vote-start prefix (card + ancient) so the budget is correct
even for act transitions that reach an ancient before any rewards screen.

## 3. Counter UI (`CardSkipCounterLabel` → `StreamerBudgetCounterLabel`)

Rename + extend the existing label rather than adding a sibling node. Its
`_Process` already polls `CardRewardVotePatch.VoteInProgress` and hides during
votes; that branch becomes the swap point:

- **No vote running**: skip text (today's), `[b]`+`StsColors.blue` on the
  `N card skips` fragment. Hidden states unchanged (limit ≤ 0 → hidden).
- **Vote running**: override text, `[b]`+`StsColors.gold` on the
  `N vote overrides` fragment, when overrides remain; hidden when 0 remain,
  when the feature is disabled (`voteOverridesPerAct == 0`), or when unlimited
  (`-1`, mirroring the skip label's unlimited convention).

Same node, same Skip-button-anchored positioning machinery, same vote-popup
visual space (the popup owns the rest of the screen; this label occupies the
position the skip text vacates).

Colour via BBCode `[color=]` using `StsColors.blue` / `StsColors.gold`
(seam-legal: the label lives in `src/Game/Ui`, which may reference MegaCrit
types). Bold: the label's theme currently maps `bold_font` to regular Kreon, so
`[b]` alone renders regular — locate a vanilla bold Kreon `.tres` in
`decompiled/sts2-assets/themes/`; if none ships, use a `FontVariation` with
`variation_embolden` over regular Kreon. Colour is the primary differentiator;
bold is best-effort on top.

## 4. Settings plumbing

`voteOverridesPerAct` — int, **default 1**, semantics mirror `cardSkipsPerAct`
(`-1` unlimited, `0` disabled, parse+clamp mirrors its ranges). Full clone of
the `RelicChoices` end-to-end pattern (`bossy-relics/2`):

1. Record field appended at the **END** of `ChatSettings`.
2. Parse + clamp in `ModSettings.Load`.
3. `SettingsBootstrap` template + migration for existing settings files.
4. **`SettingsWriter` whitelist** (the silent-drop trap).
5. `SettingsPanelBuilder` row (keep "Open settings folder" button last).
6. `slay_the_streamer_2.json.example`.
7. Tests in the three settings test files.

## 5. Error handling & safety rails

- **Lost close race**: `TryCloseNow` returns `false` → no override consumed, no
  receipt; the vote resolves naturally. Consume strictly after a `true` return.
- **Run-death mid-vote**: popup probe cancels the session (existing). The new
  `OperationCanceledException` handler routes to skip-resume ONLY when
  `_overrideSkipPending` is set; otherwise current behavior is preserved
  (fallback resume → liveness checks drop it).
- **Resume aborts after an override** (screen freed / reroll signature
  mismatch / run change): existing cancellation-receipt machinery runs
  unchanged; chat may see the override receipt followed by
  "Vote result ignored…" — accurate, and consistent with today.
- **Feature-off / degraded states**: `voteOverridesPerAct == 0`, terminal chat
  states, MP runs — the override branches simply never fire; suppressed-click
  behavior is byte-for-byte today's.
- **Receipt sends** gated on `ConnectedReadWrite`, fire-and-forget with error
  logging, same as all existing receipts (Twitch burst-drop caveat applies as
  today).

## 6. Testing & validation

- **Ti (TDD-first)**: `TryCloseNow` — forced winner lands in `WinnerIndex` and
  the TCS; `Closed` fires once; no close receipt sent; `false` on
  already-Closed/Cancelled/Disposed; throw on out-of-range index; `ForcedWinner`
  flag in snapshot; `Elapsed` under `FakeClock`. Via `VoteSessionTestBase` +
  fake triad; `[Collection("TiLog.Sink")]` (close paths log).
- **Game**: `ActBudgetTracker` rename carries existing tests;
  `VoteOverrideBudget` arithmetic + receipt formatting unit-tested (Godot-free
  by construction). New Harmony-bearing code needs `Compile Remove` entries in
  the test csproj (the `DecisionVotes/**` glob is included with per-file
  removes); `VoteOverrideBudget.cs` rides the glob.
- **Operator validation gate** (live game): card vote override (card + Skip,
  both `cardSkipAsVoteOption` settings), ancient vote override, arming window
  eats the double-click, counter swap + colours render, budget reset on act
  transition, 0-override behavior identical to today.

## 7. Slice conventions

- Commit prefix: `vote-override/N:` — added to CLAUDE.md's commit-conventions
  list in the first commit.
- Rider: fix the stale `NCardRewardSelectionScreen_Ready_HideSkipButton_Postfix`
  doc-comment reference in `CardRewardVotePatch` (the class no longer exists;
  the Skip button is visible and carries the `#0` popup indicator).
- Release context: bundled with Bossy Relics into **v0.2.0** (manifest bump,
  GitHub release, Workshop upload) — release work is outside this slice.
- Game compat: any newly bound game member must exist on both v0.109.0 beta and
  v0.107.1 non-beta. The only NEW binding this design introduces is
  `StsColors.blue` / `StsColors.gold` (verified present in the v0.109.0
  decompile; verify against v0.107.1 during plan-time compat check — expected
  fine, `StsColors` is a long-stable vanilla helper).
