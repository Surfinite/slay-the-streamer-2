# Context Document — Plan B.3 boss vote

Companion document for `2026-05-13-plan-b-3-boss-vote-design.md`. Reviewers should read this first, then the spec.

---

## 1. Reviewer Brief

You are receiving two documents: this **context document** and a **spec** (`2026-05-13-plan-b-3-boss-vote-design.md`). Your role is to **critically analyze** the spec given the context provided.

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

**slay-the-streamer-2** is a fan-made mod for **Slay the Spire 2** (the .NET 9 / Godot 4.5.1 Mono roguelike from Mega Crit) that lets a Twitch/YouTube streamer's chat vote on the streamer's in-game decisions. It's the StS2 successor to Tempus's original "Slay the Streamer" mod for StS1.

### Vote inventory (current state at time of writing)

| Slice | What chat votes on | Status |
|---|---|---|
| B.1 | Neow blessing (opening NPC) | ✅ shipped (`plan-b-1-complete`) |
| B.2.1 | Card reward + per-act skip budget | ✅ shipped (`plan-b-2-1-complete`) |
| v0.2 yt-chat | YouTube live-chat integration (multi-platform) | ✅ shipped |
| B.2.2 | Mid-run Ancients (Pael / Tezcatara / Orobas / Nonupeipe / Tanx / Vakuu / Darv) — predicate-widening on B.1's `NEventRoom.OptionButtonClicked` patch | ⚠ Implementation-complete on `main`, operator-validation pending, not yet tagged |
| **B.3** | **Act boss (this spec)** | 🔵 design-complete; this review pass |
| B.2.3 | Map path (future) | 🔵 planned |

### Stage of development

- **Plan A complete** (TI core: vote engine + IRC parser + send queue + abstractions). Lives in `src/Ti/`.
- **Plan B prep complete** — three-scenario smoke test proved the Harmony deadlock under Godot's main-thread sync context. Suspend-and-resume is non-negotiable.
- **B.1 + B.2.1 + v0.2 yt-chat all shipped.** Production-validated. Suspend-and-resume Harmony pattern + multi-platform chat aggregation + per-vote skip-budget infrastructure all in place.
- **B.2.2 implementation-complete on `main`** (4 commits in, plus a 0.2 amendment commit adding Darv coverage). Operator-validation gate pending. Predicate-widens B.1's patch to cover the 7 `AncientEventModel` subclasses without changing the in-game UX shape.
- **B.3 is this spec.** First slice that requires:
  - A new Harmony target (`NTreasureRoom.OnProceedButtonPressed`, not the `NEventRoom` shape used by B.1 / B.2.2).
  - A new in-game Godot UI surface (`BossVotePopup` — `CanvasLayer` + backdrop + 3-column portrait layout). All prior slices either piggybacked on vanilla UI or used a non-modal in-corner label.

### Constraints

- **Solo hobbyist developer** (Windows 11, Godot Mono runtime). No deadline; goal is a shippable v0.1 with clean, testable architecture. Author is autistic; honest substance > polish; pushback on bad ideas welcomed.
- **Mod cannot modify the game itself** — must work via the shipped modding API + Harmony runtime patching against `sts2.dll`.
- **TI core stays game-agnostic** — `src/Ti/` references nothing from `MegaCrit.Sts2.*`. Load-bearing for future TI extraction.
- **Suspend-and-resume Harmony pattern is non-negotiable** post-smoke.
- **Per-task commits to `main`** with `plan-b-3/N.M:` prefix are pre-authorised.
- **Target audience**: streamers with unlocked-everything saves. Discovery progression is a non-concern for this slice.

### Target users

Twitch and/or YouTube streamers who play StS2 and want chat participation. Chat viewers vote via `#0` / `#1` / `#2` (0-indexed, latest-wins, 30s window). Multi-platform aggregation merges tallies across configured platforms.

---

## 3. Architecture & Tech Stack

### Languages & frameworks

- **C# 12 / .NET 9** (target framework `net9.0`).
- **Godot 4.5.1 Mono** (`Godot.NET.Sdk/4.5.1`).
- **xUnit** for unit tests, source-referenced (no DLL refs to mod project).
- **HarmonyLib** (`0Harmony.dll`, shipped with the game) for runtime method patching.

### High-level architecture (current state pre-B.3)

```
┌──────────────────────────────────────────────────────────────────────┐
│  TI core: src/Ti/  (BCL + Godot + System.Net.Http only; game-agnostic)│
│                                                                      │
│  Voting/   VoteCoordinator (multi-platform aware),                   │
│            VoteSession, Voter (static facade), EnglishReceipts,      │
│            VoteReceiptPolicy (announce-on-open, periodic-tally, etc.)│
│  Chat/     IChatConsumer, IChatService, MultiChatService,            │
│            TwitchIrcChatService, YouTubeChat/* (scraper)             │
│  Internal/ IClock, ITimerScheduler, IMainThreadDispatcher + fakes    │
│  Ui/       VoteTallyLabel (Godot RichTextLabel under SceneTree.Root) │
│  Godot/    GodotMainThreadDispatcher + DispatcherAutoload            │
└──────────────────────────────────────────────────────────────────────┘
                            ▲
                            │ uses
                            │
┌──────────────────────────────────────────────────────────────────────┐
│  Game glue: src/Game/  (StS2-specific, references sts2.dll)          │
│                                                                      │
│  Bootstrap/      ModSettings (JSON config reader)                    │
│  DecisionVotes/  AncientVotePatch    (B.1 + B.2.2 — Neow + Ancients) │
│                  CardRewardVotePatch (B.2.1)                         │
│                  CardRewardSkipGatePatch (B.2.1)                     │
│                  SkipBudgetTracker (B.2.1 — pure logic, testable)    │
│                  BossVotePatch        (B.3 — NEW)                    │
│  Ui/             CardSkipCounterLabel (B.2.1)                        │
│                  BossVotePopup        (B.3 — NEW)                    │
└──────────────────────────────────────────────────────────────────────┘
                            ▲
                            │ entry point
                            │
                       src/ModEntry.cs
                       [ModInitializer] — wires the above
```

### Suspend-and-resume Harmony pattern (load-bearing for every vote)

```
Streamer clicks something (Neow option / card reward / Proceed in chest room / ...)
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
    - check run-ID guard (still the same run as vote-start?)
    - apply the vanilla side effect (re-call original, or invoke an API)
```

For B.3, the "apply the vanilla side effect" step is one call: `MapCmd.SetBossEncounter(runState, winnerEncounter)` — followed by a synthetic re-call of `NTreasureRoom.OnProceedButtonPressed` so the streamer's intent to leave the chest room is honoured.

### Key architectural decisions (non-negotiable for B.3)

| # | Decision | Why |
|---|---|---|
| 1 | Suspend-and-resume, never blocking await | Smoke proved blocking-await deadlocks under Godot's main-thread sync context. |
| 2 | Two-flag re-entry guard (`_voteInProgress` + `_resumeInProgress`) | Prevents repeat-click-during-vote and prevents prefix re-entry during resume's re-call. Implemented as `int` + `Interlocked.CompareExchange`. |
| 3 | Resume-time validity checks | `IsInstanceValid`, run-ID drift, options bounds. |
| 4 | TI/Game seam | `Ti/*` is BCL + Godot + System.Net.Http only; never references `sts2.dll` or `Game/*`. |
| 5 | Multi-platform chat (post-v0.2) | `IChatConsumer` is the parent interface; `MultiChatService : IChatConsumer` aggregates platforms. `VoteCoordinator` takes `IChatConsumer`. |
| 6 | 0-indexed vote options (`#0`, `#1`, `#2`) | Tempus's StS1 mod convention; explicit user preference. End-to-end across `VoteSession`, `VoteCoordinator`, `EnglishReceipts`, `VoteTallyLabel`. |
| 7 | Periodic-tally dedup compares structural state, never rendered text | Receipt strings always include `<remaining>s left` so text-equality dedup would never fire. Dedups on `(optionIndex → count)` instead. |
| 8 | Mod degrades silently to vanilla on every failure | No crashes, no in-game error toasts. |
| 9 | `[Collection("TiLog.Sink")]` for any test class that triggers `TiLog.*` | Static sink is captured by `TiLogTests`; tests that touch it must serialize. `VoteSession` warns on tally events, so voting-adjacent tests are especially prone. |

---

## 4. Codebase Map

### Directory structure (current state, post-B.2.2-impl)

```
slay-the-streamer-2/
  src/
    Ti/                                       extractable Twitch-integration core
      Chat/                                     IChatConsumer, IChatService, MultiChatService,
                                                TwitchIrcChatService, YouTubeChat/*
      Voting/                                   VoteCoordinator, VoteSession, Voter, EnglishReceipts,
                                                VoteReceiptPolicy
      Internal/                                 IClock, ITimerScheduler, IMainThreadDispatcher, TiLog
      Ui/                                       VoteTallyLabel
      Godot/                                    GodotMainThreadDispatcher + DispatcherAutoload
    Game/                                     StS2-specific glue
      Bootstrap/                                ModSettings
      DecisionVotes/                            AncientVotePatch  (covers Neow + 7 Ancients)
                                                CardRewardVotePatch + CardRewardSkipGatePatch
                                                SkipBudgetTracker
      Ui/                                       CardSkipCounterLabel
    ModEntry.cs                               [ModInitializer] entry point
  tests/                                    xUnit (source-referenced)
    Bootstrap/        ModSettingsTests
    Chat/             Twitch + YouTube + multi-platform tests
    Game/DecisionVotes/  SkipBudgetTrackerTests
    [Plan A's existing tests for Ti/Voting + Ti/Internal]
  docs/superpowers/                         specs + plans + meta-reviews
  notes/                                    research log + follow-ups
  build.ps1, install.ps1, uninstall.ps1
  README.md, LICENSE
```

### Decompiled game source (gitignored, regenerable)

`decompiled/sts2/MegaCrit/sts2/...` — ILSpy output of `sts2.dll`. Reviewers won't see it; the spec links specific paths there for the spec author's reference.

### Files most relevant to B.3

#### Existing patches — B.3's architectural twins

- **`AncientVotePatch.cs`** (~190 LOC, originally `NeowBlessingVotePatch.cs` for B.1, renamed and widened during B.2.2). Source of truth for the suspend-and-resume Harmony pattern on `NEventRoom.OptionButtonClicked`. Notable:
  - `Prepare(MethodBase? original)` runs at patch registration; verifies field/method shape via reflection; returns `false` to skip the patch if vanilla shape changed (mod degrades to vanilla).
  - `Prefix(...)` is a guard chain → atomic `Interlocked.CompareExchange` to set `_voteInProgress` → snapshot options → call `coordinator.Start(...)` → fire `_ = HandleVoteAsync(...)` → return `false`.
  - `HandleVoteAsync` posts `VoteTallyLabel.AttachTo(session)` to main thread, awaits the winner, then posts `ResumeOnMainThread(...)`. Outer try/catch falls back to the streamer's original click on async failure.
  - `ResumeOnMainThread` flips `_resumeInProgress = 1`, runs validity checks, re-calls `room.OptionButtonClicked(winnerOption, applyIndex)`, then resets both flags in `finally`.
  - Vote duration is **hardcoded** at `TimeSpan.FromSeconds(30)`.

- **`CardRewardVotePatch.cs` + `CardRewardSkipGatePatch.cs`** (~210 + ~180 LOC) — the B.2.1 implementation. Same suspend-and-resume shape but on `NCardRewardSelectionScreen.SelectCard` and with a sibling skip-gate patch that uses vanilla `NRewardsScreen.DisallowSkipping()`.

- **`SkipBudgetTracker.cs`** — the pure-logic carve-out from B.2.1. No Godot, no Harmony, no sts2.dll. Type-independent run-id (`string?`). Demonstrates the "factor pure logic into a testable helper" pattern that B.3 will likely follow for sampling + guard composition.

#### Existing UI patterns

- **`VoteTallyLabel`** (Plan A) — Godot `RichTextLabel` parented under `SceneTree.Root`. Per-frame `_Process` polls `VoteSession.GetSnapshot()` and renders multi-line text. Used by all voting slices for the small in-corner live-tally.

- **`CardSkipCounterLabel`** (B.2.1) — Godot `Label` parented under `NRewardsScreen` near the proceed button. Auto-cleans when its parent screen frees. Demonstrates the "in-context" UI lifecycle pattern.

- **B.3 introduces a new UI shape** — `BossVotePopup`, a modal `CanvasLayer` rooted directly at `SceneTree.Root` (NOT parented to a vanilla node) so it overlays the entire scene. First such surface in this mod.

#### From decompiled `sts2.dll` (referenced by spec)

- **`MapCmd.SetBossEncounter(IRunState runState, EncounterModel boss)`** — the entire boss-swap API. Public static. Mutates `runState.Act._rooms.Boss`, refreshes top-bar `BossIcon`, re-renders the map preserving streamer drawings (`clearDrawings: false`). No-op for UI side effects when `TestMode.IsOn`.
- **`ActModel.AllBossEncounters`** — `IEnumerable<EncounterModel>` filtered to `RoomType.Boss`. Source of B.3's candidate sampling pool.
- **`ActModel.SetSecondBossEncounter`** — for the A10+ DoubleBoss second boss (Act 3 only). B.3 does NOT call this in v1; vanilla picks the second boss at run start from the remainder.
- **`EncounterModel.MapNodeAssetPaths`** — either a Spine resource at `res://animations/map/<id>/<id>_node_skel_data.tres`, or fallback to PNG (`.tres.png` + `.tres_outline.png`). B.3 uses the PNG path.
- **`EncounterModel.Title`** — `LocString` returning `encounters.<id>.title`. Rendered as the per-column label under each portrait.
- **`NTreasureRoom.OnProceedButtonPressed`** — the chest-room Proceed click. B.3's Harmony prefix target. After the vote, B.3 re-calls this same method via the dispatcher to honour the streamer's original intent (synthetic-click resume).
- **`NTreasureRoom._hasChestBeenOpened` / `_isRelicCollectionOpen`** — private fields surfaced in the feasibility doc as potential earlier triggers; rejected in favor of the cleaner Proceed-click trigger.

---

## 5. Relevant Existing Patterns & Conventions

### Coding conventions

- **C# 12 + nullable reference types enabled** project-wide.
- **File-scoped namespaces.**
- **Em-dashes (—, U+2014)** preserved in prose and commit messages.
- **0-indexed vote options** (`#0`, `#1`, `#2`).
- **Interlocked-int flags** for cross-thread state, not `bool`.
- **`internal static` patch classes** in `Game/DecisionVotes/`. Methods are `static bool Prepare/Prefix` (Harmony convention).
- **Reflection-based field access** through `HarmonyLib.AccessTools.Field(type, "_fieldName")`, typically wrapped in a `Lazy<FieldInfo?>`.
- **Logging via `SlayTheStreamer2.Ti.Internal.TiLog`** with prefix `[SlayTheStreamer2][<slice-tag>]` (e.g. `[ancient-vote]`, `[card-vote]`; B.3 will use `[boss-vote]`).

### Testing strategy

- **xUnit, source-referenced.** Tests project references mod project sources directly; no DLL ref. Eliminates the need to load `sts2.dll` in test runner.
- **Plan A's fake triad** is the standard test setup for any voting-adjacent code:
  ```csharp
  FakeClock clock = new();
  FakeTimerScheduler scheduler = new(clock);
  ImmediateDispatcher dispatcher = new();
  Random rng = new(42);   // seeded for determinism
  ```
  Plus `VoteSessionTestBase.CreateCoordinator(...)` which encapsulates the triad + the post-v0.2 ctor parameter `IReadOnlyList<string> configuredPlatforms`. **New test classes should extend `VoteSessionTestBase`; instantiating raw `VoteCoordinator` will silently disagree on timing/dispatch.**
- **Patch classes themselves are NOT unit-tested.** They reference `MegaCrit.Sts2.*` types and are explicitly excluded from `Compile` in the test csproj. Coverage is via operator validation (manual playthroughs). Same constraint applied to B.1, B.2.1, B.2.2; same will apply to B.3's `BossVotePatch.cs`.
- **Pure-logic carve-outs ARE unit-tested.** Sampling, guard composition, vote-window math, anything Godot-free — split into helper classes that the test project compiles.
- **`[Collection("TiLog.Sink")]`** is mandatory for any test class that triggers `TiLog.*` because the static sink is shared. Without it, `InvalidOperationException: Collection was modified during enumeration` surfaces from parallel test interaction with `TiLogTests`. Voting tests are especially prone.

### Settings / secrets

- JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` (resolved via `OS.GetUserDataDir()`).
- Soft-failure throughout: missing/malformed file → log + degrade to vanilla, no crash.
- `SettingsResult` discriminated union pattern (records).
- **B.3 introduces no new settings keys.** Per-vote toggles and global vote-duration knob are explicitly deferred (see Scope Boundaries).

### Failure-mode philosophy

- Every external-input or game-coupled call is wrapped in try/catch.
- Catch handlers log via `TiLog.Error/Warn` with context, then degrade gracefully (vote drops, vanilla flow continues).
- No throw-from-handler scenarios reach the player. Mod is invisible when broken.

### Build + deploy pipeline

After code changes:
```powershell
pwsh -File build.ps1     # rebuilds dist/ (dotnet publish + dotnet test + assemble)
pwsh -File install.ps1   # COPY ONLY — dist/ -> Steam mods folder; does NOT rebuild
```
**The mod version printed in `godot.log` is the git HEAD at build time, not install time.** A stale `dist/` means a stale mod runs even after re-install. This has bitten this project once; reviewers should know the gotcha exists when evaluating the acceptance gate.

---

## 6. Current State & Known Issues

### What works today

- **All B.1, B.2.1, v0.2 yt-chat features** production-validated.
- **B.2.2** code is on `main` (implementation complete, operator-validation pending). Predicate-widens the Neow patch to handle all 7 `AncientEventModel` subclasses including Darv (added late after a code-review caught the missing cross-act case).
- Mid-vote disconnect/reconnect handles correctly across both Twitch and YouTube.
- Streamer-escape mid-vote (via menu, abandon run) — vote runs to normal close in background, resume drops via `IsInstanceValid` / run-ID checks, no crash.

### Known issues / debt (from `notes/06-followups-and-deferred.md`)

- **`TwitchIrcChatService.TransitionTo` is silent on state changes** — diagnostic forensics suffer when Twitch fails. v0.2 polish gap. `YouTubeChatService` was deliberately built with proper transition logging because of this lesson.
- **Twitch 20-msgs-per-30s account-level rate limit drops receipts under burst** — multiple receipts in a close window (periodic-tally + close + cancellation) may silently fail to deliver. v0.2 polish item.
- **No per-vote settings toggle wired yet** — `voteOnNeow`, `voteOnCardReward`, `voteOnAncients`, `voteOnBoss` are all 🔵 planned but unimplemented; the current shape is "if chat is configured, every vote fires". B.3 deliberately ships without `voteOnBoss` to match this baseline.
- **Vote duration is hardcoded `30s` everywhere.** Future global `voteDurationSeconds` knob is on the roadmap. Per-vote override is not.
- **First-vote-wins vs latest-wins** — all current votes are latest-wins; Tempus's StS1 mod was first-vote-wins. Low-priority toggle on the roadmap.
- **Vote-tally label placement** — current placement is sub-optimal for viewers; `tallyLabelAnchor` knob proposed for future settings.

### Game-side landmines (corrected facts that have surprised this project)

- **`.NET CookieContainer.Add(Uri, Cookie)` silently drops cookies whose `Domain` has a leading dot.** Use `DefaultRequestHeaders.Add("Cookie", ...)` directly. Surfaced during YouTube scraper work.
- **`Neow.GenerateInitialOptions` branches on `RunState.Modifiers.Count > 0`, NOT `GameMode == Custom`.** Any active modifier that returns a non-null `GenerateNeowOption` replaces the standard pick-3 with a single-option modifier kickoff. Don't infer Neow behavior from game mode — infer it from the modifier list.
- **`CardRarityOddsType.RegularEncounter` rarity odds NEVER roll `CardRarity.Basic`.** Only weights Common/Uncommon/Rare. Character-identity Basics like `Bodyguard`/`Unleash` (Necrobinder) are silently absent from any pool generated with these odds — including `SealedDeck` and `Draft` modifier pools.
- **`replaceTreasureWithElites` parameter in `ActModel.CreateMap` / `StandardActMap.CreateFor` is dead code in this build.** The only live caller (`RunManager.cs:549`) hardcodes `false`. No ascension level activates it. **Practical implication for B.3: the chest room always exists regardless of ascension level, so B.3's trigger always fires.**
- **CRLF/LF normalization warnings on Windows are expected and harmless.** `warning: in the working copy of '...', LF will be replaced by CRLF` fires on every `git add` for text files; ignore.

---

## 7. Context Specific to the Plan/Spec

### What B.3 touches

- **Adds one new patch class** in `src/Game/DecisionVotes/`: `BossVotePatch.cs` (estimated ~200 LOC, copy-paste-modify of `AncientVotePatch.cs` / `CardRewardVotePatch.cs`).
- **Adds one new UI class** in `src/Game/Ui/`: `BossVotePopup.cs` (estimated ~150 LOC, Godot `CanvasLayer`-rooted Control owning a `ColorRect` backdrop + 3-column portrait layout with live tally labels).
- **Adds one new test class** in `tests/Game/DecisionVotes/`: `BossVotePatchTests.cs` covering sampling, guards, pool-size edge cases. Marked `[Collection("TiLog.Sink")]`. The `BossVotePatch.cs` file itself is excluded from `Compile` in the test csproj (same pattern as B.1 / B.2.1 / B.2.2); the pure-logic helpers ARE compiled.
- **Extends `src/Ti/Voting/EnglishReceipts.cs`** (NOTE the path — this is in `Ti/Voting/`, not `Ti/Receipts/`) with new boss-vote receipt strings, OR reuses the existing generic decision-vote formatter — to be confirmed during implementation by reading the current `EnglishReceipts` surface.
- **Modifies `src/ModEntry.cs`** to register the patch in the Harmony PatchAll chain (same pattern as the comment around `ModEntry.cs:177` for the existing slices).
- **Does NOT modify any `src/Ti/*` core files** beyond the receipts addition.
- **Does NOT add new settings keys.**
- **Does NOT modify `src/Game/Bootstrap/ModSettings.cs`.**

### Game-side mechanisms B.3 leverages

1. **`MapCmd.SetBossEncounter` is the single boss-swap API.** Public static one-liner; handles the model mutation + top-bar icon refresh + map re-render. No vanilla-state writes from our patch besides this call.
2. **`NTreasureRoom.OnProceedButtonPressed` is the trigger surface.** Suspend-and-resume prefix returns `false`, vote runs in background, resume does the boss swap and synthesizes a Proceed re-click via `dispatcher.Post`. Same pattern as the resume-via-OptionButtonClicked re-call in B.1 / B.2.2.
3. **`runState.Act.AllBossEncounters` is the candidate pool.** Sample 3 distinct via a process-local `Random` (NOT `runState.Rng`, which is the run-deterministic generator and must not be polluted by mod draws).
4. **`EncounterModel.MapNodeAssetPaths` + `BossNodePath + ".png"` is the portrait asset.** Same icons the streamer already sees on the map; free visual consistency. PNG path avoids pulling in the MegaSpine binding surface.
5. **No special "DoubleBoss" handling.** Vanilla's existing run-start logic in `RunManager.cs:499-502` picks the second boss from `AllBossEncounters` excluding the primary. After our chat-vote swap of the primary, vanilla's exclusion still holds; the second boss may or may not be one of our 3 candidates, which is fine.

### Prior approaches / rejected alternatives

Recorded in `notes/10-boss-vote-feasibility.md` and the brainstorming session that produced this spec:

| Approach | Why rejected |
|---|---|
| Trigger on `TreasureRoom.Exit` (model layer) | Fires after the streamer is already transitioning to the map; no good visual anchor; would need to block the map-load. |
| Trigger on `OnChestButtonReleased` / mid-relic-pick | Too early — streamer is still mid-decision on the relic. |
| Piggyback on `NOverlayStack.ShowBackstop` for the backdrop | Tightly couples to vanilla's overlay-stack contract; would have to implement `IOverlayScreen` lifecycle; risk of interfering with vanilla overlays. |
| `NModalContainer.Instance.Add(...)` for a true modal popup | Vanilla modals are short-text-and-button shape; wrong shape for a 3-column image+vote popup. |
| Spine-animated boss portraits | Higher production value but pulls in MegaSpine binding usage; PNG fallback is sufficient for v1. |
| Vote on the pair under DoubleBoss (top-2 win) | Bigger UI + two-winner tally logic; deferred to v0.2 polish. |
| Two separate votes under DoubleBoss (one popup each) | Doubles the chest-exit pause to 60s; clunky. Deferred. |
| Explicit "keep current boss" 4th option | Wider UI (4 columns); explicit status-quo signal. Brainstorm settled on 3 fresh samples with no current-boss preservation logic; chat can land on the same boss by chance. |
| Per-vote `voteOnBoss` settings toggle in B.3 | Matches the unwired-toggle baseline across all current slices; a cross-cutting "B.2 surface toggles" slice will wire all of them at once. |
| Silhouette / spoiler-safe mode in v1 | Target audience is unlocked-everything streamers; spoiler-aversion is a non-concern. Deferred to future polish. |
| Vote duration > 30s for boss specifically | Author explicitly chose to keep all votes at the same duration; future knob will be global, not per-vote. |
| Vote on Acts 2 and 3 only (skip Act 1) | Author chose all 3 acts for Tempus parity. |

### Dependencies / integrations

- **Twitch IRC at `irc.chat.twitch.tv:6697`** (TLS) — already wired by B.1's `TwitchIrcChatService`.
- **YouTube live-chat scraper** under `src/Ti/Chat/YouTubeChat/` — already wired by v0.2 yt-chat. Uses the youtubei internal endpoint (no quota, no OAuth) rather than the official Data API v3.
- **`MegaCrit.Sts2.Core.Modding`** — `[ModInitializer]`, `Mod`, `ModManifest`, `Logger` types.
- **HarmonyLib** — `[HarmonyPatch]`, `MethodBase`, `AccessTools`. Patches discovered via `Harmony.PatchAll()` reflection.
- **Godot 4.5.1 Mono runtime** — Node tree, scene tree, signals, deferred-call queue, `CanvasLayer`, `ColorRect`, `TextureRect`, `Label`, `Tween`.

### Performance / scale considerations

- **Boss votes fire 3 times per run** (one per act, at chest exit). Lowest-frequency vote surface in the mod. No performance concern.
- **PNG load on popup show** — `Texture2D` resource load via Godot's `ResourceLoader`. 3 textures × 1 popup-show-event per act. Negligible. If a path is missing the `TextureRect` shows an empty box and the vote still works.
- **Reflection on `NTreasureRoom`** — done once per `Prepare` (cached as `Lazy<FieldInfo?>`); subsequent reads via the cached `FieldInfo`. Not a concern.
- **Chat receipts** — 1-2 per vote (open + close) + periodic-tally (adaptive cadence). 3 votes/run × 5 receipts = 15 messages/run. Well within Twitch's 20/30s rate limit.

### Security considerations

- No new secrets introduced by B.3.
- No persistent state beyond what vanilla already does — boss swap is in-memory `runState.Act._rooms.Boss` mutation; persists via vanilla's existing save-game flow.

### Multiplayer

- **B.3 bails to vanilla in multiplayer** (`runState.Players.Count > 1`), matching all prior voting slices.
- `MapCmd.SetBossEncounter` mutates `runState.Act` on the local instance only; it does NOT sync to remote peers. Singleplayer-only is fine for v1.
- A multiplayer sync message for boss swap would be a separate slice if/when multiplayer support is added across all votes.

---

## 8. Scope Boundaries

### Explicitly out of scope (do not suggest these)

- **Per-vote `voteOnBoss` settings toggle** — deferred to a future cross-cutting toggle slice.
- **Per-decision vote-duration override** — 30s hardcoded; future global knob, not per-vote.
- **Silhouette / spoiler-safe mode (`showBossNames: false`)** — target audience is unlocked-everything streamers.
- **A10+ DoubleBoss pair-vote or chained second-boss vote** — primary only in v1; vanilla picks the second.
- **Multiplayer sync of the boss swap** — bail to vanilla.
- **Spine-animated boss portraits** — PNG fallback is sufficient.
- **Explicit "keep current boss" option** — chat can land on the same boss by chance via the 3-fresh sample.
- **Localised receipts** — English only.
- **In-game error toasts** — silent degradation only.
- **TI extraction into reusable base-mod assembly** — not a B.3 concern.
- **B.2.3 map-path vote, B.2.4 settings UI, future B.5+** — separate slices.

### Fixed / non-negotiable

- **Suspend-and-resume Harmony pattern.** Smoke-proven; alternatives are not on the table.
- **TI/Game seam.** `Ti/*` cannot reference `Game/*` or `sts2.dll`.
- **0-indexed vote options.**
- **Self-owned `CanvasLayer` + `ColorRect` backdrop popup architecture** (Option 2 from the feasibility doc, not vanilla's `NOverlayStack` and not `NModalContainer`).
- **PNG portrait render path** (not Spine).
- **`MapCmd.SetBossEncounter` is the only vanilla mutation.**
- **Vote fires on every act (1, 2, 3).**
- **Process-local sampling RNG, NOT `runState.Rng`.**
- **30s vote window matching every other vote.**

### Deliberate trade-offs

- **Copy-paste duplication** of suspend-and-resume boilerplate from `AncientVotePatch.cs` / `CardRewardVotePatch.cs` into a new `BossVotePatch.cs`. Accepted as Rule-of-Three discipline; the project is now at n=3 (Ancient, Card, Boss), so a shared `SuspendAndResumePatchBase` is plausibly the right next move but is **deliberately deferred to a separate refactor slice** rather than introduced inside B.3.
- **No current-boss preservation in the sample.** Chat may land on the current boss randomly; redundant `SetBossEncounter(currentBoss)` is idempotent (only redundant icon refresh).
- **No "keep current boss" UI affordance.** Brainstorm settled on "chat is always in charge; sample is 3 fresh".
- **Vote always fires on chest exit; no streamer opt-out within a run.** Settings toggle for the whole feature is deferred but feature-level on/off is the eventual unit, not per-act.
- **Boss icons leak unseen-boss names.** Spoiler-aversion is N/A for the target audience.
- **First-defeat achievement check is operator-validated, not engineered around.** If `ActModel.DefeatedAllEnemiesAchievement` happens to key on per-encounter rather than per-act, chat-swapping bosses could break first-defeat tracking — flagged in the spec for smoke verification, not pre-emptively coded around.

---

## 9. Success Criteria

### Acceptance gate (7 smokes, from spec)

The slice is B.3-ready only when all seven operator-validation smokes are green (manual playthroughs):

- **Smoke A — Act 1 happy path.** Standard run, exit Act 1 chest, popup appears, 3 portraits render, timer counts down, chat votes via `!vote #N` (or `#N`), popup closes, top-bar boss icon updates, walk to Act 1 boss → expected fight starts.
- **Smoke B — Acts 2 and 3 coverage.** Similar smoke on Acts 2 and 3.
- **Smoke C — A10+ DoubleBoss.** Act 3 chest, vote on primary boss, confirm both bosses fight in sequence (vanilla picks the second from the remainder).
- **Smoke D — Run abandoned mid-vote.** Open chest-room vote, abandon run → cancellation receipt fires in chat, no orphaned `CanvasLayer` in the scene tree.
- **Smoke E — Chat disabled.** Disable chat in settings → vote does not open, Proceed click works vanilla.
- **Smoke F — Multiplayer bail.** 2-player run, exit chest → vote does not open, Proceed click works vanilla.
- **Smoke G — First-defeat achievement check.** Standard Mode with a not-yet-defeated boss, vote it in, defeat it → achievement registers.

### Quality bars

- All existing tests pass + new `BossVotePatchTests` pass.
- No regressions in B.1 / B.2.1 / B.2.2 operator validation.
- `[SlayTheStreamer2][boss-vote]` log lines appear on smoked acts; no exceptions on the boss-swap path.
- Runtime startup hash in `godot.log` matches `git log -1 --format=%H` post-merge (stale-`dist/` check).
- Mod degrades silently to vanilla on every new failure mode.
- No code in `src/Ti/*` references `sts2.dll` or `Game/*` (TI/Game seam).

---

## 10. Key Questions for Reviewers

Beyond your general critical review, please pay particular attention to:

1. **Popup architecture.** The spec uses a self-owned `CanvasLayer` + `ColorRect` backdrop + Control. Two alternatives (`NOverlayStack` piggyback, `NModalContainer`) were rejected in the feasibility doc for coupling reasons. Is the self-owned approach truly lower-risk, or is there a reason — focus management, input routing, pause behavior, controller-input handling, vanilla scene transitions — that piggybacking on vanilla's infrastructure would actually be safer for a modal popup? B.3 is the first slice introducing a modal in this mod; prior slices used non-modal labels.

2. **Re-entrancy semantics on `_voteInProgress` during a synthetic resume click.** The resume calls `NTreasureRoom.OnProceedButtonPressed` again via dispatcher. The prefix will run a second time. The spec says "second prefix call while flag is true returns immediately and bails to vanilla (or re-suspends silently — TBD on whichever is safer; same trade-off as B.2.1's `_resumeInProgress`)". Has this been resolved in the existing B.2.1 codebase, and if so what's the correct shape for B.3 to copy?

3. **Sampling RNG choice.** Spec uses a process-local `Random` (NOT `runState.Rng`) to avoid polluting the run-deterministic generator. But this means: replay determinism across runs is lost for the candidate set, AND identical save-loads will see different candidates. Is the spec's choice right (run-RNG hygiene > replay determinism for cosmetic-on-startup state), or should it use a seed derived from `runState.Rng.StringSeed + actIndex` so the candidate set is at least save-determined within a run?

4. **Pool-size edge case verification.** Spec asserts `ActModel.AllBossEncounters` returns ≥3 per act and is NOT unlock-filtered, based on the feasibility doc. Reviewers without codebase access can't verify either claim. Both are flagged as planning-phase spike items, but reviewers may have intuition about whether either assumption is fragile under MegaCrit's likely future content cadence.

5. **Helper extraction trigger.** With B.3 making n=3 patch implementations of the same suspend-and-resume shape (`AncientVotePatch`, `CardRewardVotePatch`, `BossVotePatch`), Rule of Three has fired. The spec deliberately defers the extraction to a separate refactor slice. Is this discipline correct, or should the boilerplate be extracted as part of B.3 to avoid drift across three near-identical files? (Counter-argument: B.3's resume is structurally different — it calls `MapCmd.SetBossEncounter` first and THEN re-clicks Proceed, vs. the other two which just re-call the original method.)

6. **Test seam for `MapCmd.SetBossEncounter`.** Spec calls for "a thin abstraction over `MapCmd.SetBossEncounter` (an `IBossSwapper` interface or equivalent)" so the winner→swap path is unit-testable. Is an interface the right shape, or is a delegate (`Action<IRunState, EncounterModel>`) lighter-weight and equally good? B.2.1 used a similar pattern; reviewers may have a preference.

7. **Achievement tracking under chat-swap.** Spec accepts the first-defeat achievement risk as an operator-validation smoke rather than engineering around. If a reviewer thinks the smoke is insufficient (e.g., because some achievements ARE per-encounter rather than per-act in StS2's tracking model), they should call it out.

---

## 11. Glossary / Domain Terms

### Slay the Spire 2 / game terms

- **Run**: A single playthrough from start to game-over or victory. Persists state in `RunState`.
- **Act**: One of three major sections of a run. Acts contain a map of nodes the player navigates.
- **Node / room**: A point on the map; types include combat, event, shop, rest, treasure (chest), boss.
- **Chest / treasure room**: A map node with a chest containing a relic (or skip). Always present mid-act regardless of route. The trigger surface for B.3's vote.
- **Map / map screen**: The route-selection screen between rooms; node types are visible. Streamer can draw on it.
- **Boss**: The final encounter of an act. Picked at run start by vanilla from `ActModel.AllBossEncounters` for that act; B.3 lets chat re-pick it after the chest room.
- **DoubleBoss (A10+)**: Ascension level 10 modifier. Adds a second boss to the final act only. B.3 votes on the primary boss only; vanilla picks the second from the remainder.
- **Boss discovery order**: Vanilla logic that force-picks unseen bosses for first-discovery streaming. Non-issue for B.3's target audience.
- **Ancient**: Mid-run NPC events offering Ancient-rarity relics (Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu, Darv). B.2.2 covers chat-voting on these.
- **Neow**: The first NPC of every run, offering a "blessing" choice. B.1 covers chat-voting on Neow.
- **Card reward**: One of the rewards on the post-combat reward screen. B.2.1 covers chat-voting on these.
- **Relic**: A persistent run-modifier item. Boss-relic vote is a separate (planned) slice.
- **DevConsole**: Vanilla in-game developer console; auto-unlocked when modded. Open with backtick. `act <n>` jumps acts (useful for B.3 smoke testing).
- **TestMode**: Vanilla's headless / scripted run mode. `MapCmd.SetBossEncounter` is no-op for UI side effects when `TestMode.IsOn`. We don't run in test mode at runtime.

### Twitch / YouTube / chat terms

- **OAuth token (Twitch)**: User access token used for IRC chat authentication. Format: 30-char lowercase alphanumeric, optionally `oauth:`-prefixed.
- **Receipt**: A bot-sent chat message that announces vote state (open / progress / close / cancellation).
- **Tally label**: In-game UI showing live vote counts. `VoteTallyLabel` is the small in-corner one; B.3's `BossVotePopup` adds per-column tally labels inside the modal.
- **`!vote #N` or bare `#N`**: The chat vote command syntax.
- **MultiChatService**: The post-v0.2 aggregator that merges votes from Twitch + YouTube into one tally.

### Project / architecture terms

- **TI / Twitch-Integration core**: The game-agnostic `src/Ti/*` namespace. Future extraction target.
- **Game glue**: `src/Game/*` — the StS2-specific layer that wires TI to `sts2.dll` via Harmony.
- **Suspend-and-resume**: The Harmony pattern where the prefix returns `false` (suspending the original) and an async handler later re-invokes the original via the dispatcher with the chat-chosen argument. Sometimes paired with a separate API call (B.3 calls `MapCmd.SetBossEncounter` before re-clicking Proceed).
- **Two-flag re-entry guard**: `_voteInProgress` (set during vote) + `_resumeInProgress` (set during the re-call). Together prevent both repeat-click and prefix-re-entry-during-resume.
- **Post-Start fallback**: If the async vote handler itself throws, the outer catch posts a fallback resume with the streamer's original click. "No lost click" promise.
- **Run-ID guard**: Capture run identifier at vote-start; compare at resume; skip resume if changed. Closes the resume-after-abandon race.
- **Voter.Default**: Static facade over `VoteCoordinator`, set once by `ModEntry`; patches read it.
- **Vote-on-click**: Established model — vote starts when streamer clicks something (Neow option / card / Proceed in chest room). Streamer can sit on the screen indefinitely; vote starts only when they click.
- **Operator validation**: Manual playthrough smokes that gate slice completion (vs. unit tests which validate isolated logic).
- **Chat-vs-streamer asymmetry**: The framing that chat wants the streamer to lose; therefore certain options (e.g., "skip card") are off-limits to chat. For B.3 this manifests as "no 'keep current boss' option that effectively neutralizes the vote".
- **Periodic-tally dedup**: The receipt-cadence optimization that compares structural tally state (option→count) rather than rendered text — because rendered text always includes `<remaining>s left`, text-equality would never dedup.
