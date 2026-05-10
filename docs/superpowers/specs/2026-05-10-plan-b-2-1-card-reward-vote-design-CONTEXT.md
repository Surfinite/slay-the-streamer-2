# Context Document — Plan B.2.1 card reward vote

Companion document for `2026-05-10-plan-b-2-1-card-reward-vote-design.md`. Reviewers should read this first, then the spec.

---

## 1. Reviewer Brief

You are receiving two documents: this **context document** and a **spec** (`2026-05-10-plan-b-2-1-card-reward-vote-design.md`). Your role is to **critically analyze** the spec given the context provided.

You should identify weaknesses, risks, missing considerations, better alternatives, unnecessary complexity, things that should be removed, and things that are good and should be preserved. Suggest additions, potential future features worth considering, and architectural improvements. Be **constructively critical** — do not rubber-stamp.

Your review will be synthesized in a meta-review to improve the spec, so be **specific and actionable**. Reference section names, decision numbers, or file paths from the spec directly.

**Important**: You do NOT have direct access to the codebase. You are working from this context document only. The spec author has full codebase access and will validate all suggestions against the actual code during the meta-review. Flag where you feel uncertain due to limited visibility, and note any assumptions you are making about the code.

### Review Output Format

1. **One-line verdict**: overall assessment in a single sentence.
2. **What's good**: what should be kept as-is and why.
3. **Concerns & risks**: ranked by severity.
4. **Suggested changes**: specific, actionable modifications.
5. **Alternatives**: different approaches worth considering.
6. **Additions**: things missing that should be there.
7. **Removals**: things that shouldn't be in scope.
8. **Minor / nits**: low-priority observations.
9. **Assumptions you're making**: where you lacked codebase visibility and guessed. The spec author will validate these.

Reference section names from the spec. Don't soften criticism — the goal is to improve the spec, not to be polite about it.

---

## 2. Project Overview

**slay-the-streamer-2** is a fan-made mod for **Slay the Spire 2** (the .NET 9 / Godot 4.5.1 Mono roguelike from Mega Crit) that lets a Twitch streamer's chat vote on the streamer's in-game decisions. It's the StS2 successor to Tempus's original "Slay the Streamer" mod for StS1.

### v0.1 vote inventory (5 votes total)

- **Neow blessing** — ✅ shipped in B.1 (2026-05-10, `plan-b-1-complete` tag).
- **Card reward** — ⏳ this spec (B.2.1).
- **Boss relic pick** — B.2.2.
- **Map path** — B.2.3.
- **Act boss** — B.3 (heavyweight: custom screen replacing post-treasure-room flow).

In-game settings UI (currently JSON-file only) is B.2.4. Event-choice and shop-purchase voting are deferred to v0.2.

### Stage of development

- **Plan A complete** (TI core: vote engine + IRC parser + send queue + abstractions; 142 tests). Lives in `src/Ti/`.
- **Plan B prep complete** — three-scenario smoke test confirmed the Harmony deadlock under Godot's main-thread sync context. Suspend-and-resume is now non-negotiable.
- **B.1 shipped** — full vertical slice: real `TwitchIrcChatService` + Neow Harmony patch + JSON-file credentials + minimal in-game UI. 183/183 tests pass; 5-step operator-validation gate green.
- **B.2.1 is this spec** — second sub-plan. Adds card reward vote + Proceed-skip gate + skip counter UI.

### B.1 outcomes worth knowing for B.2.1 reviewers

These were findings from B.1's operator validation, recorded in `notes/06-followups-and-deferred.md`:

- **The suspend-and-resume Harmony pattern works in production.** Plan A's `RunContinuationsAsynchronously` TCS + dispatcher.Post resume successfully avoids the main-thread deadlock that the smoke proved with blocking await. B.2.1 reuses this pattern verbatim.
- **`DisableEventOptions` visual = no hover pop, options stay readable.** Acceptable UX; chat readability concern was unfounded.
- **Twitch IRC delivers backlog on JOIN.** During mid-vote disconnect, votes sent to chat *during* the disconnect window were delivered after reconnect (within Twitch's recent-message backlog). Architectural assumption "we lose votes during disconnect" was overly pessimistic.
- **Z-order under `SceneTree.Root` works fine.** The B.1 in-game `VoteTallyLabel` is parented under root, not `CanvasLayer`.
- **Path resolution**: `OS.GetUserDataDir()` on Windows for StS2 returns `%APPDATA%\SlayTheSpire2\`. Settings JSON lives at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`.
- **B.1 noted a "resume-after-abandon race window"** — 30s background vote can complete after streamer abandons run. Currently absorbed silently (game ignores click into dying run). Notes/06 flagged "B.2 hardening: add a run-ID guard". B.2.1 implements this guard.

### Constraints

- **Solo hobbyist developer** (Windows 11, Godot Mono runtime). No deadline; goal is a shippable v0.1 with clean, testable architecture. Author is autistic; honest substance > polish; pushback on bad ideas welcomed.
- **Mod cannot modify the game itself** — must work via the shipped modding API + Harmony runtime patching against `sts2.dll`.
- **TI core stays game-agnostic** — `src/Ti/` references nothing from `MegaCrit.Sts2.*`. This boundary is load-bearing for the eventual extraction of the TI core into a reusable base-mod assembly.
- **Suspend-and-resume Harmony pattern is non-negotiable** post-smoke.
- **Per-task commits to `main`** with `plan-b-2-1/N.M:` prefix are pre-authorised.

### Target users

End users are Twitch streamers who play Slay the Spire 2 and want chat participation. Chat viewers type vote commands like `#0` / `#1` / `#2` (or bare `0`, `1`, `2`).

### **Critical design framing for B.2.1: chat-vs-streamer asymmetry**

This is new for B.2.1 and shapes several decisions. Surfinite (the author) framed it during brainstorming:

- **Chat wants the streamer to lose.** It's a "Slay the Streamer" mod — the conceit is adversarial.
- **Streamer wants entertainment + a fair-feeling fight.** Streamer has ultimate ownership of the run; chat is the antagonist within bounds the streamer sets.
- **Therefore: skip is never a chat-vote option for reward-type votes.** If chat could vote "skip card", they would skip every card → streamer auto-loses (no deck improvement) → bad streaming. So skip is a **streamer-only** option, gated by a settings-tunable budget.
- **This generalises** to B.2.2 (boss relic skip), and arguably to B.2.4 (settings UI must expose these knobs cleanly).

---

## 3. Architecture & Tech Stack

### Languages & frameworks

- **C# 12 / .NET 9** (target framework `net9.0`).
- **Godot 4.5.1 Mono** (`Godot.NET.Sdk/4.5.1`).
- **xUnit** for unit tests, source-referenced (no DLL refs to mod project).
- **HarmonyLib** (`0Harmony.dll`, shipped with the game) for runtime method patching.

### High-level architecture (post-B.1, pre-B.2.1)

```
┌───────────────────────────────────────────────────────────────────┐
│  Plan A core: src/Ti/  (BCL+Godot only, game-agnostic, 142 tests) │
│                                                                   │
│  Voting/  VoteCoordinator, VoteSession, Voter (static facade)     │
│  Chat/    IChatService, TwitchIrcChatService (B.1 production impl)│
│  Internal/  IClock, ITimerScheduler, IMainThreadDispatcher + fakes│
│  Ui/      VoteTallyLabel (Godot RichTextLabel under SceneTree.Root)│
│  Godot/   GodotMainThreadDispatcher + DispatcherAutoload          │
└───────────────────────────────────────────────────────────────────┘
                            ▲
                            │ uses
                            │
┌───────────────────────────────────────────────────────────────────┐
│  Game glue: src/Game/  (StS2-specific, references sts2.dll)       │
│                                                                   │
│  Bootstrap/         ModSettings (JSON config reader)              │
│  DecisionVotes/     NeowBlessingVotePatch (B.1)                   │
│                     CardRewardVotePatch (B.2.1 — this spec)       │
│                     CardRewardSkipGatePatch (B.2.1 — this spec)   │
│  Ui/                CardSkipCounterLabel (B.2.1 — this spec)      │
└───────────────────────────────────────────────────────────────────┘
                            ▲
                            │ entry point
                            │
                       src/ModEntry.cs
                       [ModInitializer] — wires the above
```

### Suspend-and-resume Harmony pattern (the load-bearing architectural decision)

```
Streamer clicks event option
           │
           ▼
[Harmony Prefix] ──┐ returns false (suspends original method)
                   │
                   ├──► fires `_ = HandleVoteAsync(...)` (fire-and-forget)
                   │
                   ▼
HandleVoteAsync (background task):
  await session.AwaitWinnerAsync()  ← chat votes; main thread NOT blocked
           │
           ▼
  dispatcher.Post(() => ResumeOnMainThread(...))
           │                  ▲
           ▼                  │ runs on main thread via Godot's CallDeferred
  ResumeOnMainThread:
    - check IsInstanceValid (room/screen still alive?)
    - check run-ID guard (B.2.1 NEW — same run as vote-start?)
    - check options bounds (vote winner index still maps to a real option?)
    - re-call original method with chat-chosen winner
```

### Key architectural decisions (carried forward; non-negotiable for B.2.1)

| # | Decision | Why |
|---|---|---|
| 1 | Suspend-and-resume, never blocking await | Smoke proved blocking-await deadlocks under Godot's main-thread sync context. |
| 2 | Two-flag re-entry guard (`_voteInProgress` + `_resumeInProgress`) | Prevents repeat-click-during-vote and prevents prefix re-entry during resume's re-call of the original method. Implemented as `int` + `Interlocked.CompareExchange` for atomic transitions. |
| 3 | Post-Start fallback in `HandleVoteAsync`'s outer catch | If the async vote handler itself throws, fall back to applying the streamer's original click via dispatcher.Post. "No lost click" promise. |
| 4 | Resume-time validity checks | `IsInstanceValid`, room/screen-still-correct-type, options bounds. |
| 5 | TI/Game seam | `Ti/*` is BCL+Godot only; never references `sts2.dll` or `Game/*`. Load-bearing for future TI extraction into reusable base-mod. |
| 6 | Connect-once chat receipt | First-successful-connect-per-process gated by `_connectAnnounced` static. |
| 7 | Mod degrades silently to vanilla on every failure | No crashes, no in-game error toasts in v0.1. |

---

## 4. Codebase Map

### Directory structure (current state, post-B.1)

```
slay-the-streamer-2/
  src/                                      the mod
    Ti/                                       extractable Twitch-integration core
      Chat/                                     IChatService + TwitchIrcChatService (~800 LOC) + ChatMessage + ChatCredentials
        Internal/                                 OutgoingMessageQueue, IIrcTransport + SslIrcTransport, parser
      Voting/                                   VoteCoordinator, VoteSession, Voter (static facade), EnglishReceipts
      Internal/                                 IClock, ITimerScheduler, IMainThreadDispatcher, TiLog + fakes
      Ui/                                       VoteTallyLabel (Godot RichTextLabel)
      Godot/                                    GodotMainThreadDispatcher + DispatcherAutoload
    Game/                                     StS2-specific glue
      Bootstrap/                                ModSettings (JSON reader)
      DecisionVotes/                            NeowBlessingVotePatch (~190 LOC, the B.1 reference)
    ModEntry.cs                               [ModInitializer] entry point
    slay_the_streamer_2.csproj
    slay_the_streamer_2.json                  mod manifest
  tests/                                    xUnit test project (source-referenced, 183 tests)
    Bootstrap/ModSettingsTests.cs
    Chat/TwitchIrcChatServiceTests.cs (~17 tests)
    Chat/Internal/FakeIrcTransport.cs
    [Plan A's existing tests for Ti/Voting + Ti/Internal]
  docs/superpowers/                         specs + plans + meta-reviews
  notes/                                    research log + follow-ups
  build.ps1                                 refresh DLLs, dotnet publish, dotnet test, assemble dist/
  install.ps1, uninstall.ps1
  README.md, LICENSE
```

### Decompiled game source (referenced but not in repo — gitignored)

Decompiled output of `sts2.dll` lives at `decompiled/sts2/MegaCrit/sts2/...`. Reviewers won't see it; spec links to specific paths there for the spec author's reference.

### Files most relevant to B.2.1

#### Existing (B.1) — for pattern reference

- `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` (~190 LOC) — **the source of truth for the suspend-and-resume pattern.** B.2.1's `CardRewardVotePatch.cs` will be a copy-paste-modify of this file. Notable:
  - `static bool Prepare(MethodBase? original)` — runs at patch registration; verifies field/method shape; returns `false` to skip patch if shape changed (mod degrades to vanilla).
  - `static bool Prefix(...)` — guard chain → atomic `Interlocked.CompareExchange` to set `_voteInProgress` flag → snapshot options → call `coordinator.Start(...)` → `__instance.Layout?.DisableEventOptions()` → fire `_ = HandleVoteAsync(...)` → return `false`.
  - `HandleVoteAsync` posts `VoteTallyLabel.AttachTo(session)` to main thread first, then awaits winner, then posts `ResumeOnMainThread(...)`. Outer try/catch falls back to `playerClickIndex` resume.
  - `ResumeOnMainThread` flips `_resumeInProgress = 1`, runs validity checks, re-calls `room.OptionButtonClicked(winnerOption, applyIndex)`, then resets both flags in `finally`.
  - Vote duration is **hardcoded** at `TimeSpan.FromSeconds(30)` — no settings key.

- `src/Game/Bootstrap/ModSettings.cs` (~115 LOC) — JSON reader returning a `SettingsResult` discriminated union (`Success` with warnings list / `Missing` / `Malformed`). Current schema has `schemaVersion` (must equal 1), `channel`, `username`, `oauthToken`. Includes URL normalisation (`https://twitch.tv/foo` → `foo`) and oauth shape warnings.

- `src/Ti/Ui/VoteTallyLabel.cs` (~80 LOC) — Godot `RichTextLabel` parented under `SceneTree.Root`. `_Process` polls `VoteSession.GetSnapshot()` and renders multi-line text. `_ExitTree` cleans up.

- `src/ModEntry.cs` — `[ModInitializer]` entry point. Sets `TiLog.Sink = ...`, registers Godot dispatcher autoload via `tree.Root.CallDeferred("add_child", autoload)`, builds `TwitchIrcChatService`, sets `Voter.Default`, calls `Harmony.PatchAll()`. New B.2.1 patches are picked up automatically by `PatchAll()` — no `ModEntry` change required.

#### From decompiled `sts2.dll` (referenced by spec)

- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` — the card reward sub-screen. Has `SelectCard(NCardHolder)` (the vote patch target), `_options` field of type `IReadOnlyList<CardCreationResult>` (vote option enumeration source), `_completionSource` TaskCompletionSource for the screen's own completion, `_rewardAlternativesContainer` for skip/reroll/alternates (NOT patched).
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen` — the parent rewards screen showing all rewards (gold, card, potion, relic). Public method `DisallowSkipping()` sets `_skipDisallowed = true` and disables Proceed when in Skip mode. Methods `RewardCollectedFrom(Control)` / `RewardSkippedFrom(Control)` are vanilla callbacks. `TryEnableProceedButton` checks `(!_skipDisallowed || !_proceedButton.IsSkip)`.
- `MegaCrit.Sts2.Core.AutoSlay.Handlers.Screens.CardRewardScreenHandler` — vanilla AutoSlay (the auto-play system) invokes `cardHolder.EmitSignal(NCardHolder.SignalName.Pressed, cardHolder)` to pick a card, which ultimately reaches `SelectCard`. **Confirms our patch target is correct.**
- `MegaCrit.Sts2.Core.Rewards.CardReward` — the underlying reward type; used by skip-detect to identify card-reward buttons.

---

## 5. Relevant Existing Patterns & Conventions

### Coding conventions

- **C# 12 + nullable reference types enabled** project-wide.
- **File-scoped namespaces.**
- **Em-dashes (—, U+2014)** preserved in commit messages and prose.
- **0-indexed vote options** — chat types `#0`, `#1`, `#2` (or bare `0`, `1`, `2`).
- **Interlocked-int flags** for cross-thread state, not `bool`. (`int` flag = 0 or 1, transitioned via `Interlocked.CompareExchange`.)
- **`internal static` patch classes** in `Game/DecisionVotes/`. Methods are `static bool Prepare/Prefix` (Harmony convention).
- **Reflection-based field access** through `HarmonyLib.AccessTools.Field(type, "_fieldName")`, typically wrapped in a `Lazy<FieldInfo?>`.
- **Logging via `SlayTheStreamer2.Ti.Internal.TiLog`** — has `Trace/Debug/Info/Warn/Error` static methods. Sink injected by `ModEntry` to forward to `MegaCrit.Sts2.Core.Logging.Logger`.

### Testing strategy

- **xUnit, source-referenced.** Tests project references mod project sources directly; no DLL ref. Eliminates the need to load `sts2.dll` in test runner.
- **Plan A's fakes are reused everywhere**: `FakeClock`, `FakeTimerScheduler`, `ImmediateDispatcher`, `FakeChatService`, `FakeIrcTransport`. All in `src/Ti/Internal/` and `tests/Chat/Internal/`.
- **Patch classes themselves are NOT unit-tested** — they depend on Harmony + sts2.dll. Coverage is via operator validation (manual playthroughs).
- **Pure logic IS unit-tested** — `ModSettings`, `OutgoingMessageQueue`, parser logic, anything in `Ti/`.
- **Operator validation gate** at the end of each sub-plan — manual playthrough scenarios that must all pass before tagging the slice complete.

### Settings / secrets

- JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` (resolved via `OS.GetUserDataDir()`).
- `oauthToken` accepted as either bare 30-char alphanumeric or `oauth:`-prefixed.
- Soft-failure throughout: missing/malformed file → log + degrade to vanilla, no crash.
- `SettingsResult` discriminated union pattern (records).

### Failure-mode philosophy

- Every external-input or game-coupled call is wrapped in try/catch.
- Catch handlers log via `TiLog.Error/Warn` with context, then degrade gracefully (vote drops, vanilla flow continues).
- No throw-from-handler scenarios reach the player. Mod is invisible when broken.

---

## 6. Current State & Known Issues

### What works today

- **All B.1 features** (Neow vote, IRC connect-once, settings load, in-game tally label) production-validated via the 5-step operator gate.
- **183/183 unit tests pass.**
- Mod loads cleanly with or without a settings file.
- Mid-vote disconnect/reconnect handles correctly (Twitch backlog delivers votes that arrived during the disconnect window).
- Streamer-escape mid-vote (via menu) — vote runs to normal close in background, resume drops via `IsInstanceValid` check, no crash.

### Known issues / debt

From `notes/06-followups-and-deferred.md`:

- **`forceFirstRunNeow: true` settings flag** — modded saves don't have unlock progression for Neow on first runs (separate save profile). Streamer onboarding pain. Deferred.
- **`copySaveFromUnmodded` settings flag** — alternate onboarding fix. Deferred.
- **`VoteSession.SendReceipt` send-failure log level** — currently Error during reconnect; should be Warn. Plan A revision pending.
- **Buffer close receipt during reconnect** — chat doesn't see the close receipt if close-timer fires during disconnect. Polish.
- **`TiLog.Sink` should scrub `ex.ToString()`** before forwarding (currently only scrubs `msg`; ex.Message could leak oauth on wrapped HTTP exceptions). Polish.
- **`IMainThreadDispatcher.DrainAsync` re-entrancy contract** — undocumented; pin down for tests.

### Vanilla bugs observed (NOT ours; recorded so we don't chase)

- `data.tree is null` in `NTopBarPauseButton.AnimUnhover` during scene transitions. Pure MegaCrit bug, harmless.
- `Error deleting current_run.save.backup` during run abandon. Steam-cloud save cleanup race, harmless.
- Godot rendering server "leaked at exit" warnings on shutdown (1050+ CanvasItems). Vanilla Godot lazy-cleanup ordering, harmless.

---

## 7. Context Specific to the Plan/Spec

### What B.2.1 touches

- **Adds two new files** in `src/Game/DecisionVotes/`: `CardRewardVotePatch.cs` (~210 LOC, copy-paste-modify of `NeowBlessingVotePatch.cs`) and `CardRewardSkipGatePatch.cs` (~180 LOC, owns counter state + two postfix patches).
- **Adds one new file** in `src/Game/Ui/`: `CardSkipCounterLabel.cs` (~70 LOC, Godot Label parented under `NRewardsScreen`).
- **Extends `src/Game/Bootstrap/ModSettings.cs`** with two new optional keys: `cardSkipsPerAct` (default 1) and `cardSkipsPerRun` (default -1).
- **Does NOT modify `src/ModEntry.cs`** — `Harmony.PatchAll()` picks up the new patches automatically.
- **Does NOT modify any `src/Ti/*` files** — TI/Game seam preserved.
- **Adds tests** in `tests/Bootstrap/ModSettingsTests.cs` (~6 new tests for the two settings keys) and a new `tests/Game/DecisionVotes/CardRewardSkipGateTests.cs` (~10 tests for skip-counter logic in isolation).

### Game-side mechanisms B.2.1 leverages

1. **Vanilla `NRewardsScreen.DisallowSkipping()` is public.** B.2.1's skip gate calls this method instead of patching `OnProceedButtonPressed`. Vanilla then handles the actual button-disable + "claim rewards" message. Smaller patch surface.
2. **Vanilla DevConsole handles all dev iteration.** StS2 ships `MegaCrit.Sts2.Core.Nodes.Debug.NDevConsole` with debug commands auto-unlocked when `ModManager.IsRunningModded() == true` (we always are). Open with backtick (`` ` ``). Useful commands: `win` (instakill enemies → trigger reward screen), `travel` (jump map nodes), `act <n>` (jump act, validates counter reset), `relic add <id>` (grant relic without reward flow). **No custom debug Harmony patches needed for B.2.1 testing.**
3. **`RunState.Acts` / current-act detection.** Spec acknowledges the implementer needs to verify the exact field name for current-act-index in `Prepare` — `Acts.Count - 1` or a `CurrentAct` property; uncertain from decompiled source alone.

### Prior approaches / rejected alternatives

These were considered during brainstorming and rejected, recorded so reviewers don't suggest them:

| Approach | Why rejected |
|---|---|
| Patch `NRewardsScreen.OnProceedButtonPressed` directly | Vanilla's existing `DisallowSkipping()` does the same job with smaller patch surface. |
| Make skip a chat-vote option (4th option = skip) | Chat-vs-streamer asymmetry — chat would skip every card → streamer auto-loses. |
| Per-encounter skip budget | Card rewards are 1-per-encounter, so per-encounter limit = unlimited. Per-act + per-run instead. |
| Extract a `SuspendAndResumePatchBase` abstract class in B.2.1 | Rule of Three. We have n=1 (Neow); B.2.1 makes n=2; B.2.2 will make n=3 — *that* is when shared structure becomes clear. Premature abstraction risk. |
| Patch reroll / alternate buttons | Reroll is a streamer-only escape; chat shouldn't override it. Vote starts on actual card click → naturally compatible. |
| Combine B.2.1-3 into one plan | Surfinite explicitly chose three separate plans. B.1 had a smoke-phase surprise; if boss relic has its own surprise, separate slices bound the blast radius. |
| Build a "B.2.0 dev tooling" sub-plan with custom debug Harmony patches | Vanilla DevConsole already has everything needed. No need to ship debug code that must be removed before tagging slices. |

### Dependencies / integrations

- **Twitch IRC at `irc.chat.twitch.tv:6697`** (TLS) — already wired by B.1's `TwitchIrcChatService`.
- **`MegaCrit.Sts2.Core.Modding`** — the StS2 modding API. `[ModInitializer]` attribute; `Mod`, `ModManifest`, `Logger` types.
- **HarmonyLib** — `[HarmonyPatch]`, `MethodBase`, `AccessTools`. Patches discovered via `Harmony.PatchAll()` reflection.
- **Godot 4.5.1 Mono runtime** — Node tree, scene tree, signals, deferred-call queue, `RichTextLabel`, `Label`.

### Performance / scale considerations

- **Card rewards happen every combat** (~16+ per act, ~50 per run). The vote + skip gate runs that often. State is minimal (a few ints), no allocations on the hot path. No performance concern.
- **Reflection on `_options` / `_rewardButtons`** — done once per `Prepare` (cached as `Lazy<FieldInfo?>`); subsequent reads via the cached `FieldInfo`. Not a concern.
- **Chat receipts**: 1-2 per vote (open + close) + 1 per skip. Plan A's `OutgoingMessageQueue` enforces 20 msg / 30s + 1 msg / sec spacing. Card-vote receipt volume is well within limits.

### Security considerations

- OAuth token in settings file — protected by file-system permissions (user-only `%APPDATA%`). `TiLog.Sink` scrubs token from log messages.
- No new secrets introduced by B.2.1. Counter state is in-memory only, not persisted.

---

## 8. Scope Boundaries

### Explicitly out of scope (do not suggest these)

- **B.2.2 boss relic, B.2.3 map path, B.2.4 settings UI, B.3 act-boss.** Each is its own sub-plan.
- **Helper / base-class extraction.** Deliberately deferred to B.2.2 per Rule of Three.
- **Patching reroll / alternate buttons** on `NCardRewardSelectionScreen`.
- **Patching `NRewardsScreen.OnProceedButtonPressed` directly** (we use `DisallowSkipping()` instead).
- **Per-relic curation** (chat-strong / streamer-strong relic blacklist). v0.2 polish.
- **Settings-driven vote duration** (B.1 hardcodes 30s; B.2.1 follows suit; settings UI in B.2.4).
- **BBCode stripping in receipts.** Address if it actually surfaces.
- **Multiplayer co-op support** — B.2.1 bails on `Players.Count > 1`.
- **Localised receipts** — English only (`EnglishReceipts`).
- **In-game error toasts** — silent degradation only.
- **Chat-readiness UI indicator** (`ChatStatusControl`). Deferred.
- **TI extraction into reusable base-mod assembly.** Plan A's seams are pre-drawn for this; not a B.2.1 concern.

### Fixed / non-negotiable

- **Suspend-and-resume Harmony pattern.** Smoke-proven; alternatives are not on the table.
- **TI/Game seam.** `Ti/*` cannot reference `Game/*` or `sts2.dll`.
- **No-helper extraction in B.2.1.** Re-evaluate after B.2.2.
- **Skip is never a chat-vote option.** Chat-vs-streamer asymmetry.
- **Default skip budget: 1 per act, unlimited per run.** Surfinite explicitly chose this.
- **Per-act AND per-run, both enforced.** Not "pick one mode".
- **Random fallback never picks skip** (even when budget allows).

### Deliberate trade-offs

- **Copy-paste duplication** of B.1's NeowBlessingVotePatch into B.2.1's CardRewardVotePatch (~200 LOC). Accepted as Rule-of-Three discipline.
- **Skip-counter label parented under `NRewardsScreen`, not a separate canvas layer.** Keeps it contextual; auto-cleans when screen frees.
- **Act/run change detection at next-rewards-screen, not at moment-of-change.** Accepts that the budget reset is "lazy" by one rewards screen. Acceptable because the budget is fresh by next combat.
- **Reroll-mid-vote silently absorbs.** No chat receipt for "vote cancelled, streamer rerolled". Adds complexity for an edge case.

---

## 9. Success Criteria

### Acceptance gate (5 steps; from spec)

The mod is B.2.1-ready only when all five operator-validation steps are green (manual playthroughs):

- **Step 0 — Vanilla baseline.** Settings present with B.1 keys only (no `cardSkipsPer*`): mod loads with defaults. Card rewards play with vote + skip gate using defaults. **No regressions in B.1 features.**
- **Step 1 — Happy path vote (3 successful runs).** Chat votes, winning card claimed via dispatcher.Post resume. Latest-wins on multi-vote. Both `#N` and bare `N` accepted. Close receipt fires with correct card name. VoteTallyLabel shows tally. Skip counter updates correctly when cards claimed.
- **Step 2 — Skip used.** With `cardSkipsPerAct: 1`: streamer skips card → chat receipt fires `Streamer skipped a card reward (1/1 act, 0/∞ run)` → counter label updates → next combat: Proceed disabled (must claim).
- **Step 3 — Skip blocked.** With `cardSkipsPerAct: 0`: rewards screen opens, Proceed visibly disabled, must claim card → vote runs → claim → Proceed enabled.
- **Step 4 — Counter resets.** `act 2` console command → counter resets → skip usable again. Same for new run.
- **Step 5 — Edge cases.** Mid-vote run abandon (run-ID guard fires, no crash). Mid-vote reroll (vote silently absorbs, streamer clicks new card → new vote). Streamer-escape mid-vote (vanilla flow). Rapid card clicks (only first triggers vote).

### Quality bars

- All Plan A + B.1 + B.2.1 unit tests pass.
- New unit tests for: settings parsing of new keys, skip-counter logic in isolation.
- No regressions in B.1's operator-validation steps.
- Mod degrades silently to vanilla on every new failure mode.
- No code in `src/Ti/*` references `sts2.dll` or `Game/*` (TI/Game seam).

---

## 10. Key Questions for Reviewers

Beyond your general critical review, please pay particular attention to:

1. **Is the dual skip budget (per-act + per-run, both enforced) the right shape?** Is it an over-design? Should it be one mode (per-act OR per-run) instead of both? Or is the dual model genuinely useful?

2. **Skip-counter label placement.** The spec says "parented under `NRewardsScreen` near `_proceedButton`". Reviewers without codebase access can't verify Godot's anchor system suits this. Does this approach sound robust for a v0.1 (acceptable to fail-open if proceed button position changes) or should we consider an alternative (e.g., extending the existing `VoteTallyLabel` with a skip-counter line)?

3. **Run-ID guard implementation uncertainty.** Spec acknowledges the implementer must verify the exact field name for run identifier in `Prepare`. Is "verify in `Prepare`, fail-open if absent" an acceptable risk-management posture, or should we fail-closed (don't register the patch at all) to make the failure mode louder?

4. **Skip via Proceed without ever opening the card sub-screen.** Currently this is accepted: if streamer clicks Proceed with a card unclaimed AND skips remaining > 0, vanilla `RewardSkippedFrom` fires for the unopened card; we count it as a skip and decrement budget. Streamer never *saw* the cards. Is this the right semantics, or should we differentiate "skipped without looking" from "looked but didn't pick" (would require additional patches to detect screen-open events)?

5. **Reroll-mid-vote silent absorb.** Spec accepts this with no chat receipt. Streamer could silently cause vote drops by rerolling. Is this an exploit chat would flag, or genuinely fine for v0.1?

6. **Helper extraction deferral.** Are we confident that copy-pasting ~200 LOC of patch boilerplate is the right call vs. a minimal utility-method extraction now? The Rule-of-Three argument is sound but conservative; some reviewers may prefer earlier abstraction.

---

## 11. Glossary / Domain Terms

### Slay the Spire 2 / game terms

- **Run**: A single playthrough from start to game-over or victory. Persists state in `RunState`.
- **Act**: One of three (or more, depending on character/mode) major sections of a run. Acts contain a map of nodes the player navigates.
- **Node / room**: A point on the map; types include combat, event, shop, rest, treasure, boss.
- **Combat / encounter**: A turn-based card-game fight with enemies. Ends in death or victory.
- **Reward(s) screen**: The post-combat screen showing earned rewards (gold, card pick, potion, sometimes a relic).
- **Card reward**: One of the rewards on the reward screen. Clicking it opens a sub-screen showing 3 cards (typically); player picks one to add to their deck.
- **Skip (a card)**: Click Proceed on the rewards screen without claiming the card pick. Vanilla allows this freely.
- **Reroll**: A relic-granted ability to discard the current 3-card set and generate a new one. Rare.
- **Alternate**: Non-card options on the card-selection sub-screen (e.g., gold instead of a card from certain relics). Rare.
- **Boss relic**: A choice between 3 relics offered after each act-boss kill. Always claimed (no skip in vanilla).
- **Neow**: The first NPC of every run, offering a "blessing" choice (1 of 3 typically). Single-shot per run.
- **AutoSlay**: Vanilla's auto-play system used for testing / accessibility. Provides a useful reference for which methods/signals constitute a "click".
- **DevConsole**: Vanilla in-game developer console; auto-unlocked when modded. Open with backtick.

### Twitch / chat terms

- **OAuth token**: User access token used to authenticate the mod's IRC connection to chat. Format: 30-char lowercase alphanumeric, optionally `oauth:`-prefixed.
- **PRIVMSG**: IRC command for sending a chat message.
- **JOIN**: IRC command for joining a channel.
- **CAP REQ**: IRC capability negotiation; we request `tags` + `commands` extensions.
- **Receipt**: A bot-sent chat message that announces vote state (open / progress / close).
- **Tally label**: In-game UI showing live vote counts.

### Project / architecture terms

- **TI / Twitch Integration core**: The game-agnostic `src/Ti/*` namespace. Future extraction target.
- **Game glue**: `src/Game/*` — the StS2-specific layer that wires TI to `sts2.dll` via Harmony.
- **Suspend-and-resume**: The Harmony pattern where the prefix returns false (suspending the original) and an async handler later re-invokes the original via the dispatcher with the chat-chosen argument.
- **Two-flag re-entry guard**: `_voteInProgress` (set during vote) and `_resumeInProgress` (set during the re-call of the original method). Together prevent both repeat-click and prefix-re-entry-during-resume.
- **Post-Start fallback**: If the async vote handler itself throws (post-`Voter.Start` but pre-resume), the outer catch posts a fallback resume with the streamer's original click. "No lost click" promise.
- **Run-ID guard**: New in B.2.1. Capture run identifier at vote-start; compare at resume; skip resume if changed. Closes the resume-after-abandon race.
- **Voter.Default**: A static facade over `VoteCoordinator`, set once by `ModEntry`; patches read it via `Voter.Default`.
- **Vote-on-click vs vote-on-screen-open**: Established model is vote-on-click (matches B.1 Neow). Streamer can sit on screen indefinitely; vote starts only when streamer clicks. B.2.1 keeps this model so streamer can use reroll / read cards before triggering vote.
- **Operator validation**: Manual playthrough scenarios that gate slice completion (vs. unit tests which validate logic).
- **Chat-vs-streamer asymmetry**: The design framing that chat wants the streamer to lose; therefore certain choices (e.g., skip-as-vote-option) are off-limits to chat. Streamer retains escape valves for these cases (skip via Proceed within budget).
