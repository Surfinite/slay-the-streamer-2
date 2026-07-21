# Vote Override — feature brief for brainstorming (2026-07-21)

Handoff doc for a fresh session. Start with the `superpowers:brainstorming` skill
(Surfinite explicitly wants a brainstorming pass), then spec → plan →
subagent-driven execution, following the same pipeline the Bossy Relics slice used
(spec `docs/superpowers/specs/2026-07-20-bossy-relics-design.md`, plan
`docs/superpowers/plans/2026-07-20-bossy-relics.md` are good structural templates).

## The feature (Surfinite's words, lightly structured)

Streamer-side balance feature, directly requested by a streamer ("his chat are too
good at denying him wins"). Give the streamer the option to **override the vote** a
configurable number of times per act, **default 1**.

Mechanics:
- Vote timer starts exactly as today (streamer clicks a card to open the vote).
- While the vote countdown is running, clicks are currently suppressed. Change: if
  the streamer has >0 overrides remaining, a click on a card (or on skip) **ends
  the vote immediately with the clicked option as the winner** and consumes one
  override.
- **Skip during a vote consumes an OVERRIDE, not a card skip.** Rationale: an
  override is more powerful than a pre-vote skip — the streamer has seen what chat
  is voting for and is reacting to it. (Pre-vote skip behavior and the skip budget
  are unchanged.)
- **Arming delay**: override clicks should only register ~1-2 seconds after the
  vote starts, so the double-click that opened the vote can't accidentally consume
  an override.

UI:
- A counter styled like the existing "\[streamer-name] has N card skips remaining
  this act" text: **"\[streamer-name] has N vote overrides remaining this act"**.
- Shown ONLY while the countdown is running, in the SAME screen position the
  skips-remaining text occupies (that text currently disappears when the vote
  starts — the override text takes its place during the vote).
- Hidden entirely when 0 overrides remain.
- Cosmetic rider: make the "N card skips" fragment of the existing text **bold**,
  and the "N vote overrides" fragment of the new text **bold**.

Scope:
- **Card reward votes are the primary goal.**
- Ancient blessings: nice-to-have if it falls out easily; fine to defer.
- Boss votes: explicitly excluded (custom popup screen, no native click surface).
- Act-variant vote: not mentioned — ask during brainstorming.
- Surfinite is open to alternate implementation shapes if the described approach
  gets complicated.

## Codebase pointers (verified as of `bossy-relics-complete` tag, game v0.109.0)

- **Skip counter text + budget**: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`
  (skip budget gating, `SkipBudgetTracker`), and wherever the "has N card skips
  remaining this act" label is rendered (grep for `skips remaining` /
  `GetStreamerDisplayName` — display-name helper is `ModSettings.GetStreamerDisplayName()`
  in `src/Game/Bootstrap/ModSettings.cs`). The new counter should reuse this
  label's position/styling machinery.
- **Card vote flow**: `src/Game/DecisionVotes/CardRewardVotePatch.cs` — suspends
  `NCardRewardSelectionScreen.SelectCard` via prefix (suspend-and-resume pattern,
  CLAUDE.md Tier 2). HOW clicks are suppressed during the countdown is the first
  thing to investigate: an override is essentially "let a suppressed click through,
  cancel the session, resume with the clicked winner."
- **Vote session/coordinator**: `src/Ti/Voting/` — `VoteSession`, `VoteCoordinator`.
  An override needs an "end now with forced winner" API. That's a GENERIC voting
  concept, so extending `Ti/Voting` is seam-legal (CLAUDE.md TI/Game seam: no
  MegaCrit types in `src/Ti`). Note `VoteSession.Cancel()` fires `Cancelled`, and
  natural expiry fires `Closed` — an override is semantically a third thing
  ("resolved early with winner X"); brainstorm whether it's `Close(forcedWinner)`
  or a new terminal event. Popups subscribe to both existing events (CLAUDE.md
  landmine) — a new terminal path must not leak popups.
- **Settings plumbing pattern**: clone the `RelicChoices` end-to-end from the
  bossy-relics slice (`bossy-relics/2` commit) — record append at END of
  `ChatSettings`, parse+clamp in `ModSettings.Load`, `SettingsBootstrap` template +
  migration, **`SettingsWriter` whitelist** (silent-drop trap), panel row in
  `SettingsPanelBuilder` (keep "Open settings folder" button last), json.example,
  tests in the three settings test files.
- **Per-act reset**: `SkipBudgetTracker.ObserveRunAndAct(runState.Rng?.StringSeed, actIndex)`
  is the existing per-act/per-run reset pattern — the override budget should mirror
  it (maybe generalize the tracker rather than duplicating).
- **Receipts**: `EnglishReceipts` in `src/Ti/Voting` (or nearby) — decide during
  brainstorming what chat sees when an override happens (e.g. "Streamer overrode
  the vote: X"). Chat-facing text matters for this feature's social contract.
- **Tests**: vote-adjacent tests MUST use `VoteSessionTestBase` + the fake triad
  (CLAUDE.md Tier 2); `[Collection("TiLog.Sink")]` for anything that logs.
  `Ti/Voting` is fully unit-testable — the forced-winner session API should be
  TDD'd there.

## Context for the release

- Bossy Relics is complete (tag `bossy-relics-complete`, main at `a7de81b`),
  operator-validated, NOT yet released. Plan: bundle Bossy Relics + Vote Override
  into **v0.2.0** (manifest bump, GitHub release with notes carrying all standing
  sections, Workshop upload via `workshop/upload.ps1` with a fresh `changeNote` —
  release workflow details in auto-memory `release-and-game-update-workflow`).
- Commit prefix for the new slice: pick one (e.g. `vote-override/N:`) and add it to
  CLAUDE.md's commit-conventions list in the first commit.
- Game currently v0.109.0 beta / v0.107.1 non-beta; one DLL serves both — any newly
  bound game member must exist on both (check `decomp-old`/`decomp-v108`/`decomp-v109`
  in the session scratchpad if still present, else re-decompile per CLAUDE.md).

## Known open questions for the brainstorm (non-exhaustive)

1. Act-variant vote: include or exclude? (Pre-run — the override counter text
   position may not even exist there.)
2. What does chat see on an override (receipt text)? Does the tally overlay show
   anything special?
3. Does an override that picks the same option chat was winning still consume the
   override? (Presumably yes — simplest — but confirm.)
4. Arming delay: fixed 1.5s? configurable? Interaction with very short vote
   durations (min is 10s).
5. Ancients: the ancient vote also uses suspend-and-resume on a native screen
   (`NEventRoom.OptionButtonClicked`) — is the click-suppression mechanism there
   close enough to card rewards that it falls out nearly free?
6. Override counter + skip counter both per-act: shared tracker generalization or
   two parallel trackers?
