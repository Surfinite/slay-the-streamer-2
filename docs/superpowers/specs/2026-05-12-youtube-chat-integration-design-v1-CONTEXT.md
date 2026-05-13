# Context Document — YouTube chat parallel integration

Companion document for `2026-05-12-youtube-chat-integration-design-v1.md`. Reviewers should read this first, then the spec.

---

## 1. Reviewer Brief

You are receiving two documents: this **context document** and a **spec** (`2026-05-12-youtube-chat-integration-design-v1.md`). Your role is to **critically analyze** the spec given the context provided.

You should identify weaknesses, risks, missing considerations, better alternatives, unnecessary complexity, things that should be removed, and things that are good and should be preserved. Suggest additions, potential future features worth considering, and architectural improvements. Be **constructively critical** — do not rubber-stamp.

Your review will be synthesized in a meta-review to improve the spec, so be **specific and actionable**. Reference section names, decision numbers (D1–D10), or file paths from the spec directly.

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

### v0.1 status (2026-05-12)

- **Plan A complete** (TI core: vote engine + IRC parser + send queue + abstractions; 142 tests). Lives in `src/Ti/`.
- **B.1 shipped** — Neow blessing vote, full vertical slice. Production-validated.
- **B.2.1 shipped** — card reward vote + Proceed-skip gate + skip counter UI. Production-validated; 203 tests pass; tagged `plan-b-2-1-complete`. Two amendments (Decision 18 → "mandatory-look + transactional commit"; Decision 21 → "terminal chat failure also degrades gate") landed during operator validation.
- **B.2.2 not yet started** — Ancients (community term; formerly "boon-god") relic-grant vote. Likely collapses to predicate-widening on `NeowBlessingVotePatch`.
- **B.2.3 / B.3 / B.2.4** — map path, act boss, settings UI. Future.

### Where this spec sits

This spec is **NOT** in the B.2.x main sequence. It's a v0.2+ candidate slice prompted by FrostPrime (the streamer this mod is being aimed at), who runs simulcast on Twitch + YouTube and wants YouTube chat participation alongside Twitch.

The feasibility writeup at `notes/07-youtube-chat-feasibility.md` (2026-05-12) established:
- **Reading** YouTube chat is doable via a ~200-LOC roll-your-own scraper of the internal `youtubei` endpoint (the same one YouTube's chat popout uses). No quota, no OAuth.
- **Posting** to YouTube requires OAuth + Google app verification (multi-week, incompatible with "mod end users install"). Read-only on YouTube; receipts echo to Twitch only.
- **Architectural fit**: a `YouTubeChatService : IChatService` slots into the existing `Ti/Chat/` surface alongside `TwitchIrcChatService`. A `MultiChatService` aggregator wraps both into a single `IChatService` the existing `VoteCoordinator` consumes unchanged.

Decisions D1–D10 were resolved in conversation between Surfinite and Claude on 2026-05-12 — D1–D5 and D7–D10 in the original notes/07 session, and D6 (settings JSON schema) added retroactively during the spec-drafting session after the original gap was noticed. They are now inlined as the Decisions table in the spec. **Reviewers should treat D1–D10 as settled-by-the-author** unless they have a strong reason to push back.

### Constraints

- **Solo hobbyist developer** (Windows 11, Godot Mono runtime). No deadline; goal is shippable, clean, testable. Author is autistic; honest substance > polish; pushback on bad ideas welcomed.
- **Mod cannot modify the game itself** — must work via shipped modding API + Harmony runtime patching against `sts2.dll`. **This spec adds NO new Harmony patches** — pure additions to `Ti/` (chat layer + voting tally side-dict) and `ModEntry`.
- **TI core stays game-agnostic** — `src/Ti/` references nothing from `MegaCrit.Sts2.*`. This boundary is load-bearing for the eventual extraction of the TI core into a reusable base-mod assembly. **The YouTube child must respect this boundary**; the spec keeps it inside `Ti/Chat/YouTubeChat/`.
- **Suspend-and-resume Harmony pattern is non-negotiable** (smoke-proven from B.1 prep). Irrelevant for this spec since no patches are added, but reviewers should know it's the established pattern.

### Target users

End users are streamers running Twitch + (optionally) YouTube simulcasts of Slay the Spire 2 who want chat participation. Chat viewers on either platform type vote commands like `#0` / `#1` / `#2` (or bare `0`, `1`, `2`).

The specific streamer driving this slice is **FrostPrime**, brought to Surfinite's attention via FrostPrime's Discord (a community member, 2026-05-12). FrostPrime hasn't been contacted directly yet; the spec's "Open questions" section captures FrostPrime-coordination questions to ask once his tournament finishes.

---

## 3. Architecture & Tech Stack

### Languages & frameworks

- **C# 12 / .NET 9** (target framework `net9.0`).
- **Godot 4.5.1 Mono** (`Godot.NET.Sdk/4.5.1`).
- **xUnit** for unit tests, source-referenced (no DLL refs to mod project).
- **HarmonyLib** (`0Harmony.dll`, shipped with the game) for runtime method patching.
- **No new external dependencies** in this spec. The YouTube scraper uses `System.Net.Http.HttpClient` + `System.Text.Json` from the BCL. There is no maintained .NET YouTube-chat library worth pulling in (researched 2026-05-12; `pytchat` / `chat-downloader` / `youtube-chat` are Python/JS).

### High-level architecture (post-B.2.1, pre-this-spec)

```
┌───────────────────────────────────────────────────────────────────┐
│  Plan A core: src/Ti/  (BCL+Godot only, game-agnostic, 203 tests) │
│                                                                   │
│  Voting/  VoteCoordinator, VoteSession, Voter (static facade)     │
│  Chat/    IChatService                                            │
│           TwitchIrcChatService (B.1 production impl)              │
│           ChatMessage, ChatCredentials, ChatConnectionState       │
│  Internal/ IClock, ITimerScheduler, IMainThreadDispatcher + fakes │
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
│                     CardRewardVotePatch (B.2.1)                   │
│                     CardRewardSkipGatePatch (B.2.1)               │
│                     SkipBudgetTracker (B.2.1)                     │
│  Ui/                CardSkipCounterLabel (B.2.1)                  │
└───────────────────────────────────────────────────────────────────┘
                            ▲
                            │ entry point
                            │
                       src/ModEntry.cs
                       [ModInitializer] — wires the above
```

### Where this spec extends the architecture

```
┌───────────────────────────────────────────────────────────────────┐
│  Plan A core: src/Ti/                                             │
│                                                                   │
│  Chat/                                                            │
│    IChatService               ←── unchanged surface               │
│    TwitchIrcChatService       ←── unchanged                       │
│    MultiChatService           ←── NEW: aggregator over N children │
│    YouTubeChat/               ←── NEW sub-namespace               │
│      YouTubeChatService              IChatService impl            │
│      YouTubeLiveChatScraper          page parse + get_live_chat   │
│      YouTubeLiveBroadcastDiscovery   channel/{ID}/live → videoId  │
│      YouTubeChatModels               internal DTO records         │
│  Voting/                                                          │
│    VoteSession                ←── EXTENDED: add per-platform      │
│                                    side-dict + PlatformOf helper  │
│  Ui/                                                              │
│    VoteTallyLabel             ←── EXTENDED: split-line rendering  │
│                                    when both platforms observed   │
└───────────────────────────────────────────────────────────────────┘
                            ▲
                            │
                       ModEntry.cs:
                       chat = new MultiChatService(twitch, youtube?);
                       voter = new VoteCoordinator(chat, ...);   // unchanged
```

**No Game/ files added or modified.** Existing Harmony patches (`NeowBlessingVotePatch`, `CardRewardVotePatch`, `CardRewardSkipGatePatch`) read votes via `Voter.Default`, which now points at a `VoteCoordinator` wrapping a `MultiChatService` instead of a bare `TwitchIrcChatService`. They cannot tell the difference.

### Key architectural decisions (relevant to this spec)

| # | Decision | Status |
|---|---|---|
| 1 | `IChatService` is the seam for new chat platforms. | Established by Plan A; this spec adds a second impl. |
| 2 | `ChatMessage.VoterKey` is the vote-dedup primitive (`UserId ?? "login:{Login}"`). | Established by Plan A; this spec disciplines YT to prefix `UserId` with `"yt:"` (D9), preventing collisions with Twitch's bare-numeric IDs. **No schema change to `ChatMessage`.** |
| 3 | Chat layer is fully game-agnostic. | Load-bearing for TI extraction. This spec preserves it; all YT code lives under `Ti/Chat/YouTubeChat/`. |
| 4 | `VoteCoordinator` takes ONE `IChatService`. | This spec preserves it via the aggregator pattern — `MultiChatService` is itself an `IChatService`. `VoteCoordinator` doesn't change. |
| 5 | Suspend-and-resume Harmony pattern. | Irrelevant to this spec (no patches added). Mentioned for completeness. |
| 6 | Mod degrades silently to vanilla on every failure. | This spec extends the philosophy: all YT failure modes degrade to "Twitch-only" silently, without affecting the rest of the mod. |
| 7 | Read-only chat impls are first-class. | `IChatService` already has `CanSend` and `ConnectedReadOnly` states; the YouTube child uses these. No interface extension needed. |

---

## 4. Codebase Map

### Directory structure (current, post-B.2.1)

```
slay-the-streamer-2/
  src/                                       the mod
    Ti/                                        extractable Twitch-integration core
      Chat/                                      IChatService + TwitchIrcChatService (~800 LOC) + ChatMessage + ChatCredentials
        Internal/                                  OutgoingMessageQueue, IIrcTransport + SslIrcTransport, parser
      Voting/                                    VoteCoordinator, VoteSession, Voter (static facade), EnglishReceipts
      Internal/                                  IClock, ITimerScheduler, IMainThreadDispatcher, TiLog + fakes
      Ui/                                        VoteTallyLabel (Godot RichTextLabel)
      Godot/                                     GodotMainThreadDispatcher + DispatcherAutoload
    Game/                                      StS2-specific glue
      Bootstrap/                                 ModSettings (JSON reader)
      DecisionVotes/                             NeowBlessingVotePatch (B.1), CardRewardVotePatch (B.2.1),
                                                 CardRewardSkipGatePatch (B.2.1), SkipBudgetTracker (B.2.1)
      Ui/                                        CardSkipCounterLabel (B.2.1)
    ModEntry.cs                                [ModInitializer] entry point
    slay_the_streamer_2.csproj
    slay_the_streamer_2.json                   mod manifest
  tests/                                     xUnit test project (source-referenced, 203 tests)
    Bootstrap/ModSettingsTests.cs
    Chat/TwitchIrcChatServiceTests.cs (~17 tests)
    Chat/Internal/FakeIrcTransport.cs
    Game/DecisionVotes/SkipBudgetTrackerTests.cs (~10 tests, ~4 BudgetResetReason tests)
    [Plan A's existing tests for Ti/Voting + Ti/Internal]
  docs/superpowers/                          specs + plans + meta-reviews
  notes/                                     research log + follow-ups
  build.ps1                                  refresh DLLs, dotnet publish, dotnet test, assemble dist/
  install.ps1, uninstall.ps1
  README.md, LICENSE
```

### Decompiled game source (referenced but not in repo — gitignored)

Decompiled output of `sts2.dll` lives at `decompiled/sts2/MegaCrit/sts2/...`. **This spec does not require any decompiled-source references** because it adds no Harmony patches.

### Files most relevant to this spec

#### Existing (preserved) — for surface reference

- `src/Ti/Chat/IChatService.cs` — the interface the new YouTube child must satisfy. Already shaped for read-only impls: has `CanSend` property, `ConnectedReadOnly` state.
- `src/Ti/Chat/ChatMessage.cs` — `record` with `VoterKey => UserId ?? "login:{Login}"`. D9 says YT sets `UserId = "yt:{authorChannelId}"`. No schema change.
- `src/Ti/Chat/TwitchIrcChatService.cs` — the existing reference impl. State machine, reconnect loop with exponential backoff, terminal-state handling (`AuthenticationFailed`, `JoinFailed`) — the YouTube service mirrors the shape but with different specifics (60s fixed cadence per D7; no terminal states beyond `Disposed`).
- `src/Ti/Voting/VoteSession.cs` — destination for the per-platform tally side-dict. The existing `_tallies : Dictionary<int, int>` stays merged-total for close-receipts; a parallel `_talliesByPlatform : Dictionary<(string, int), int>` feeds the UI.
- `src/Ti/Voting/VoteCoordinator.cs` — **unchanged**. Takes one `IChatService`; will be handed a `MultiChatService` instead of a bare `TwitchIrcChatService`.
- `src/Ti/Ui/VoteTallyLabel.cs` — destination for split-line rendering when multiple platforms observed.
- `src/Game/Bootstrap/ModSettings.cs` — gets one new optional field (`youtubeChannelId`), no schemaVersion bump (additive optional field).
- `src/ModEntry.cs` — wires `MultiChatService(twitch, youtube?)` in place of bare Twitch, extends startup receipt per D8.

#### New (this spec adds)

- `src/Ti/Chat/MultiChatService.cs` — aggregator wrapping N child `IChatService`. Forwards messages, fans out sends only to `CanSend == true` children, computes aggregate state best-of-children.
- `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs` — `IChatService` impl driving the state machine + poll loop.
- `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs` — load-bearing fragility: regex-based extraction of `INNERTUBE_API_KEY` and continuation token from the live_chat page, plus POST to `youtubei/v1/live_chat/get_live_chat`.
- `src/Ti/Chat/YouTubeChat/YouTubeLiveBroadcastDiscovery.cs` — GET `youtube.com/channel/{ID}/live`, follow redirect, extract video ID from final URL.
- `src/Ti/Chat/YouTubeChat/YouTubeChatModels.cs` — internal record types for parsed responses.

---

## 5. Relevant Existing Patterns & Conventions

### Coding conventions

- **C# 12 + nullable reference types enabled** project-wide.
- **File-scoped namespaces.**
- **Em-dashes (—, U+2014)** preserved in commit messages and prose.
- **0-indexed vote options** — chat types `#0`, `#1`, `#2` (or bare `0`, `1`, `2`).
- **Interlocked-int flags** for cross-thread state, not `bool`. (Convention from B.1 patches; the spec's YouTube service is mostly main-thread-via-dispatcher, but the poll loop is on a Task.Run thread, so the same discipline applies.)
- **Reflection-based field access** through `HarmonyLib.AccessTools.Field(type, "_fieldName")`, wrapped in `Lazy<FieldInfo?>`. **Not applicable in this spec** (no Harmony patches).
- **Logging via `SlayTheStreamer2.Ti.Internal.TiLog`** — has `Trace/Debug/Info/Warn/Error` static methods. Sink injected by `ModEntry` to forward to `MegaCrit.Sts2.Core.Logging.Logger`. YT scraper failures log Warn; per-message parse failures log Debug.

### State machine conventions for `IChatService`

The existing `TwitchIrcChatService` defines the expected shape:

- `Disconnected` → `Connecting` on `ConnectAsync`.
- `Connecting` → `ConnectedReadOnly` or `ConnectedReadWrite` on successful join (depending on whether creds were passed).
- Any state → `Reconnecting` on transport failure; reconnect loop with exponential backoff schedules retry.
- `Connecting` → `AuthenticationFailed` (terminal) on bad oauth.
- `Connecting` → `JoinFailed` (terminal) on banned/wrong channel.
- Any state → `Disposed` on `Dispose()`.

**The YouTube service deviates** in three ways, all spec-resolved:
- `AuthenticationFailed` is unreachable (no auth).
- `JoinFailed` is unreachable (per D7, ALL non-Disposed failures land in `Reconnecting` and retry every ~60s — including invalid channel IDs; we deliberately don't try to distinguish "permanent" from "transient" 404s).
- Reconnect cadence is fixed at ~60s ± 10s jitter, NOT exponential. Rationale in spec (steady-state is "waiting for stream to start"; faster cadence doesn't help).

### Testing strategy

- **xUnit, source-referenced.** Tests project references mod project sources directly; no DLL ref. Eliminates the need to load `sts2.dll` in test runner.
- **Plan A's fakes are reused everywhere**: `FakeClock`, `FakeTimerScheduler`, `ImmediateDispatcher`, `FakeChatService`, `FakeIrcTransport`. All in `src/Ti/Internal/` and `tests/Chat/Internal/`.
- **`MultiChatService` is fully unit-testable** — pure logic over child services; spec proposes ~12 tests.
- **`YouTubeLiveChatScraper` and `YouTubeLiveBroadcastDiscovery` are fixture-based** — capture real JSON / HTTP redirect responses into `tests/Fixtures/youtube_live_chat_*.json`, run parser against them. When YouTube ships a redesign, refresh the fixture and bump the parser.
- **`YouTubeChatService` (the state machine wrapper) is unit-testable** with the standard fakes + a mock `IYouTubeHttp` / mock discovery / mock scraper. ~8 tests cover state transitions, reconnect arming, dispose propagation.
- **Operator validation gate** at the end of each slice — manual playthrough scenarios that must all pass before tagging the slice complete. Spec proposes a 7-step gate.

### Settings / secrets

- JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` (resolved via `OS.GetUserDataDir()`).
- This spec adds `youtubeChannelId` as an OPTIONAL string field. Absent / null / empty string all mean "YT disabled". No `schemaVersion` bump (additive optional field, backward-compatible).
- No new secrets — YouTube scraping is unauthenticated.

### Failure-mode philosophy

- Every external-input or game-coupled call is wrapped in try/catch.
- Catch handlers log via `TiLog.Error/Warn` with context, then degrade gracefully.
- No throw-from-handler scenarios reach the player. Mod is invisible when broken.
- This spec adds ~21 enumerated failure modes (see spec's Failure-modes table); all degrade to "Twitch-only" or "single-platform UI rendering" silently.

---

## 6. Current State & Known Issues

### What works today

- **All B.1 + B.2.1 features** production-validated via 5- and 7-step operator gates.
- **203/203 unit tests pass.**
- Mod loads cleanly with or without a settings file.
- Mid-vote disconnect/reconnect on Twitch handles correctly (Twitch backlog delivers votes that arrived during the disconnect window).
- Streamer-escape mid-vote (via menu) — vote runs to normal close in background, resume drops via `IsInstanceValid` check, no crash.
- Card-reward skip-gate Model 2 transactional commit working: sub-screen Skip and Escape→Resume back-out are tentative; budget only charged when parent Proceed/Skip click commits the screen.
- Decision 21 amendment in effect: terminal Twitch chat states (`AuthenticationFailed` / `JoinFailed` / `Disposed`) degrade the skip gate to vanilla. **This spec inherits that gate-degradation logic unchanged** — the YouTube child's state has no influence on `ShouldEnforceSkipGate()`.

### Known issues / debt (relevant to this spec)

From `notes/06-followups-and-deferred.md`:

- **`TwitchIrcChatService.TransitionTo` is silent on state changes** — operator-validation Step 7 showed `AuthenticationFailed` transition produced zero `[TwitchIrcChatService]`-tagged log lines. Diagnostic forensics suffer. Polish for v0.2. **Relevant to this spec** because the new `YouTubeChatService` should ship with state-transition logging from day one to avoid the same gap.
- **Twitch ratelimit drops cancellation receipts** — under burst conditions (close + cancellation + periodic-tally within ~10s), Twitch's 20-msgs-per-30s account cap drops messages. This spec adds D8's startup-and-state-change receipts on top of the existing receipt stream — **a real risk** that operator-validation Step 7 of this spec needs to confirm doesn't worsen the burst problem.
- **Vote-option-numbering Noita pattern** — back-to-back votes can collide under stream delay; chat viewers vote on the previous vote's `#N` and it lands on the next vote's tally. Tracked for v0.2. **Worse under YT** because of YT's 2–5s lag — flag for the meta-review.
- **`VoteSession.SendReceipt` send-failure log level** — currently Error during reconnect; should be Warn. Plan A revision pending. Not blocking this spec.

### Vanilla bugs observed (NOT ours; recorded so reviewers don't chase)

- `data.tree is null` in `NTopBarPauseButton.AnimUnhover` during scene transitions. Pure MegaCrit bug, harmless.
- `Error deleting current_run.save.backup` during run abandon. Steam-cloud save cleanup race, harmless.
- Godot rendering server "leaked at exit" warnings on shutdown. Vanilla Godot lazy-cleanup ordering, harmless.

### Recent significant changes

- 2026-05-11: B.2.1 shipped. Decision 18 amended to "mandatory-look + transactional commit Model 2". Decision 21 amended to "terminal chat-failure also degrades gate".
- 2026-05-12: Discord intel from FrostPrime's community surfaced multiple v0.2 candidates: sealed-deck draft start, Whispering Earrings chat-plays-first-turn, chat skip-vote-as-option, and **YouTube chat parallel integration** (this spec).
- 2026-05-12: notes/07 feasibility writeup produced (the spec's predecessor). Decisions D1–D10 resolved in a same-day conversation.

---

## 7. Context Specific to the Plan/Spec

### Decision numbering — D6 was resolved late

`notes/07-youtube-chat-feasibility.md`'s decisions log was originally written with a gap at D6 — only D1–D5 and D7–D10 had resolved entries. During this spec-drafting session, the gap was noticed and **D6 was added retroactively** to cover "Settings JSON schema additions" (the `youtubeChannelId` field's parsing/validation rules). D6 is now present in both notes/07 and this spec's Decisions table. **No further renumbering is intended**.

### What this spec touches

| Area | Change | Magnitude |
|---|---|---|
| `src/Ti/Chat/` | Add `MultiChatService.cs` + `YouTubeChat/` sub-namespace (4 files) | ~660 LOC source + ~310 LOC tests |
| `src/Ti/Voting/VoteSession.cs` | Add parallel `_talliesByPlatform` side-dict + `PlatformOf` helper + `TalliesByPlatform` accessor | ~30 LOC source + ~30 LOC tests |
| `src/Ti/Ui/VoteTallyLabel.cs` | Split-line rendering when both platforms observed | ~40 LOC |
| `src/Game/Bootstrap/ModSettings.cs` | One new optional field (`youtubeChannelId`) | ~15 LOC source + ~10 LOC tests |
| `src/ModEntry.cs` | Construct `MultiChatService(twitch, youtube?)`; extend startup receipt per D8 | ~70 LOC |
| `src/Game/DecisionVotes/*` | **No changes.** Existing patches read votes via `Voter.Default`; aggregator is invisible to them. | 0 LOC |

**Total ~815 LOC source + ~360 LOC tests.** Within the notes/07 1–2 week effort estimate.

### Risk concentration

~30% of total source LOC sits in scraper code (`YouTubeLiveChatScraper` + `YouTubeLiveBroadcastDiscovery`), which carries **~70% of the redesign-fragility risk**. The mitigation is structural — every YouTube scraper concern lives in `Ti/Chat/YouTubeChat/`; nothing outside that namespace cares about the JSON shape or the regex.

When YouTube ships a redesign (cross-language scraper libraries observe a 6–12 month cadence for this):
1. Pull a fresh `live_chat` response into the test fixtures.
2. Update the regex / JSON traversal in `YouTubeLiveChatScraper`.
3. Re-run fixture-tests.

This is the same maintenance model `pytchat` / `chat-downloader` / `youtube-chat` use.

### Prior approaches / rejected alternatives

Recorded so reviewers don't suggest them. (Many were vetted in the notes/07 feasibility analysis; some emerged during decision-resolution.)

| Approach | Why rejected |
|---|---|
| Use YouTube's official Data API v3 (`liveChatMessages.list`) | 10K-units-per-day quota, 5 units per call → daily quota consumed in one stream. Quota extensions require Google approval, not granted for hobby projects. Hard non-starter. |
| Post receipts to YouTube via official API + OAuth | Requires OAuth + Google app verification for >100 users (multi-week paperwork process). Incompatible with "mod end users install". |
| Vendor a Python/JS chat library and call it from C# | No active maintained .NET equivalent exists (researched 2026-05-12); cross-runtime invocation is heavier than the ~200-LOC custom scraper. |
| Per-platform vote windows (separate close timers, merged at end) | More state, more receipt-UX complexity (which "close" receipt fires when?). Single shared window with documented YT-lag limit is simpler. |
| Cross-platform identity dedup via display-name match | Display name match doesn't prove same human; differing names don't disprove. Fundamentally unfixable for anonymous chat. Future optional heuristic kept on the v0.2 list. |
| Manual video ID per stream (settings JSON edit before each stream) | Worse UX than channel ID + auto-discovery. Streamer would have to remember to edit JSON every stream. |
| Escape-hatch field `youtubeVideoIdOverride` in settings | Rejected during D6 resolution. Rationale: if YouTube ever changes the `/live` redirect format and breaks auto-discovery, we ship a code fix rather than asking streamers to find video IDs themselves. The escape-hatch would also need its own validation, its own "is the channel ID also present?" interaction logic, and its own UX — non-trivial complexity for a scenario that hasn't happened yet. |
| Members-only chat support | Requires authenticated session cookies — brittle, security-sensitive, real onboarding hurdle. Documented non-goal per D5. |
| Distinguish "permanent" YT failures (404 forever) from "transient" (DNS hiccup) and use `JoinFailed` for permanent | Heuristics are fragile. All non-Disposed failures land in `Reconnecting` per D7. Cost: never-ending Warn log when channel ID is typo'd — fine, one Warn per minute, no in-game UI impact. |
| Visual-combining the tally label (one line, colour-coded per platform) | Deferred per D10. Split-by-platform-lines is the v1 rendering. v0.2 can iterate. |
| YT-only deployment as a first-class mode | Architecturally supported (single-child `MultiChatService` with just the YT child), but UX-degraded — no chat receipts on any platform per D3, only in-game tally label. Document as a known mode, don't optimise for it. |
| Introduce a `Platform` field on `ChatMessage` | Would require a schema change. D9 instead uses `"yt:"` prefix on `UserId` and a private `PlatformOf(ChatMessage)` helper in `VoteSession`. No public-surface change. |
| Refactor `IChatService.ConnectAsync` to handle multi-channel | Would break the existing Twitch impl. Instead, `MultiChatService.ConnectAsync` throws `NotSupportedException` — `ModEntry` connects each child directly before constructing the multi. |

### Dependencies / integrations

- **Twitch IRC at `irc.chat.twitch.tv:6697`** — unchanged from B.1.
- **YouTube `youtubei` internal endpoint** at `https://www.youtube.com/youtubei/v1/live_chat/get_live_chat` (POST) and the live-chat-page HTML at `https://www.youtube.com/live_chat?v={videoId}` (GET). **Both undocumented and subject to silent shape changes.** This is the load-bearing fragility.
- **YouTube live-broadcast discovery** at `https://www.youtube.com/channel/{channelId}/live` (GET with auto-redirect). Final URL `/watch?v={videoId}` indicates active broadcast; anything else means none.
- **HarmonyLib** — unchanged from B.1/B.2.1; not used in this spec.
- **`MegaCrit.Sts2.Core.Modding`** — unchanged.
- **Godot 4.5.1 Mono runtime** — unchanged.

### Performance / scale considerations

- **YT chat volume**: `pytchat`-class scrapers reportedly lag on >1000 msg/min. FrostPrime's audience size is not yet measured; spec's Open Questions section flags this for FrostPrime-coordination. If it's an issue, mitigation is rate-limiting `MessageReceived` propagation in the YT child (sample 1-of-N) or extending the vote window.
- **Polling overhead**: `get_live_chat` polled per its `timeoutMs` field (floor 1s, ceiling 10s — defensive against malformed responses). One HTTP POST per cycle. Negligible compared to Godot's per-frame work.
- **Per-platform tally side-dict**: a small `Dictionary<(string, int), int>` updated per vote message. Identical asymptotic cost to the existing merged tally. No concern.
- **VoteTallyLabel split rendering**: ~2× the StringBuilder work per frame. Still trivial.

### Security considerations

- **No new secrets.** YouTube scraping is anonymous.
- **No new attack surface** — scraper makes outbound HTTPS to youtube.com only; no inbound network listeners.
- **YouTube's TOS**: scraping the internal endpoint is in a grey zone (used by all major non-official chat overlays/tools; never enforced against hobby projects historically). Same risk profile as `pytchat` etc. Document and accept.
- **OAuth token (Twitch)** — unchanged from B.1. `TiLog.Sink` scrubs token from log messages.

---

## 8. Scope Boundaries

### Explicitly out of scope (do not suggest these)

- **Cross-platform identity dedup.** Counting twice is the v1 decision per D1.
- **Per-platform vote windows.** Single shared 30s window per D2.
- **YouTube outgoing receipts.** Read-only YT per D3.
- **Members-only chat support.** Public chat only per D5.
- **Manual video ID per stream.** Channel ID + auto-discovery per D4.
- **YouTube-only deployment as an optimised first-class mode.** Architecturally supported via single-child aggregator, but documented as degraded.
- **Visual-combining tally label rendering.** Split lines only per D10.
- **Super Chat / Super Sticker special-handling.** Treated as normal chat messages.
- **Latency compensation.** No adaptive window or per-platform offset per D2.
- **YouTube-side moderation / VIP / member-priority filtering.** Badges map sensibly; no v1 consumer of those flags.
- **Helper / base-class extraction for "multi-platform chat".** Rule of three: not yet a pattern, just an aggregator for this one feature.
- **Localised receipts.** English only via Twitch.
- **In-game error toasts.** Log only.
- **Persisting YT state across save/quit/reload.** Process-lifetime only.
- **Adding new Harmony patches.** Pure chat-layer + voting tally side-dict + UI rendering changes. No Game/DecisionVotes/* touched.

### Fixed / non-negotiable

- **`IChatService` interface shape stays unchanged.** No new methods, no new properties, no new state values. YT fits the existing surface.
- **`ChatMessage` schema stays unchanged.** D9's "yt:" prefix is discipline, not schema.
- **`VoteCoordinator` stays unchanged.** Aggregator is invisible to it.
- **No new Harmony patches.** This is a chat-layer slice, not a game-decision-vote slice.
- **TI/Game seam.** `Ti/Chat/YouTubeChat/` cannot reference `MegaCrit.Sts2.*` or `Game/*`.
- **D7's "one cadence, no distinguishing permanent from transient".** 60s ± 10s fixed retry. Reviewers may push back, but the rationale is documented (Decision 7 + headline-shift 2 in the spec's author's note).
- **D9's "yt:" prefix.** Load-bearing for D1 (per-platform double-counting) and the `PlatformOf` helper. Not a placeholder.
- **D10's split-line rendering, not visual-combined.** Visual-combining is explicitly deferred.

### Deliberate trade-offs

- **YT under-represents votes by 2–5s due to its end-to-end lag** (per D2). Acceptable for v1.
- **Cross-platform double-count** is intentional (per D1). A user voting from both Twitch and YouTube counts twice.
- **Never-ending Warn log on typo'd channel ID** (per D7). Cost: one Warn per minute, no in-game impact. Avoids fragile heuristics for "is this 404 permanent?".
- **Receipt for "YouTube disconnected" uses generic wording** ("live broadcast ended"), which is slightly inaccurate for network-failure causes. Accepted v1 limit; receipt is at least directionally correct.
- **`MultiChatService` always wraps Twitch + YouTube?**, even in the Twitch-only deployment. Single-child case is functionally a passthrough; one code path is simpler than branching at `ModEntry`.
- **`MultiChatService.ConnectAsync` throws** rather than fanning out. Rationale: the existing `ConnectAsync(channel, creds)` signature can't carry per-child channel info. `ModEntry` connects each child directly.
- **Twitch close-receipts stay merged** (per D10). Per-platform breakdown is for the streamer/overlay (in-game label); chatters already know which platform they voted on.

---

## 9. Success Criteria

### Acceptance gate (7 steps; from spec)

The mod is v1-ready only when all seven operator-validation steps are green (manual playthroughs):

- **Step 0 — Vanilla regression (Twitch-only, new code path).** Settings with no `youtubeChannelId`. Mod loads. `MultiChatService` wraps only the Twitch child. Run Neow vote, card-reward vote, skip-blocked path — all identical to v0.1. In-game tally label uses single-platform rendering (no `Twitch:` prefix line). Confirms the new aggregator is functionally identical to the old direct-Twitch path in single-child case.
- **Step 1 — YT-only smoke.** Valid `youtubeChannelId` + deliberately-broken Twitch (bad oauth → `AuthenticationFailed`). YT messages flow into VoteSession. In-game label renders YouTube line. Card-skip gate degraded per Decision 21 amendment. No chat receipts on either platform.
- **Step 2 — Dual-platform happy path (3 runs).** Real live broadcasts on both. Neow vote, card-reward vote, card-reward skip — all flow correctly. Split tally label per D10. Twitch close-receipts merged per D10. No YT receipts per D3.
- **Step 3 — YT failure modes (per D7).** Five sub-steps: 3a no live broadcast at startup (D8 receipt), 3b YT broadcast ends mid-session, 3c channel ID typo (never-ending retry, no panic), 3d network kill mid-poll, 3e scraper-shape regression simulation.
- **Step 4 — Split tally label correctness.** Real-time updates from both platforms, latest-wins per voter, merged total equals sum of platforms.
- **Step 5 — Cross-platform double-count (per D1).** Same human on both platforms gets 2 votes.
- **Step 6 — Twitch-only-deployment + D6 settings parsing.** Three sub-cases: (6a) JSON `null`, (6b) empty string — both equivalent to absent / `Success`; (6c) whitespace-only OR control char — `Malformed`, mod degrades to vanilla.
- **Step 7 — Receipt flap suppression.** Rapid YT state toggles emit at most one Twitch receipt per stable state (30s debounce per platform).

### Quality bars

- All Plan A + B.1 + B.2.1 + v1 unit tests pass.
- New unit tests for: `MultiChatService` aggregator (~12 tests), `YouTubeLiveChatScraper` fixture-based parse (~8 tests), `YouTubeLiveBroadcastDiscovery` redirect-follow (~4 tests), `YouTubeChatService` state machine (~8 tests with mocks), `VoteSession` per-platform tallies (~6 tests), `ModSettings` `youtubeChannelId` parsing (~3 tests).
- No regressions in B.1 or B.2.1 operator-validation steps.
- Mod degrades silently to vanilla on every new YT failure mode.
- No code in `src/Ti/Chat/YouTubeChat/*` references `MegaCrit.Sts2.*` or `Game/*` (TI/Game seam preserved).

---

## 10. Key Questions for Reviewers

Beyond your general critical review, please pay particular attention to:

1. **Is the "all transient failures → `Reconnecting`, never `JoinFailed`" simplification (D7 + headline-shift 2) the right call?** The spec explicitly refuses to disambiguate "permanent" (typo'd channel ID, 404 forever) from "transient" (DNS hiccup, ratelimit) failures, on the grounds that the heuristics are fragile. Cost is a never-ending Warn-log loop in the typo case. Is this the right risk-management posture for a hobbyist mod, or should the spec invest in distinguishing them (e.g., transition to `JoinFailed` after N consecutive identical-shape 404s on the channel-page endpoint)?

2. **Per-platform tally as a parallel side-dict on `VoteSession`** — Is this the right shape, or should it be a separate `PerPlatformVoteTally` collaborator class? The side-dict keeps the latest-wins logic co-located with the merged tally (single source of truth for voter changes), but it adds `VoteSession` complexity. The class-extraction alternative is cleaner but duplicates the latest-wins logic.

3. **Single fixed 60s retry cadence, no exponential backoff** (per D7) — The spec argues exponential backoff is wrong here because the failure-mode distribution skews toward "stream isn't live yet", which has no faster-recovery property. But ratelimit-class failures DO benefit from backoff. Should the spec carve out a special case for HTTP 429 specifically (longer backoff for ratelimits), or accept the simpler one-cadence policy?

4. **`MultiChatService.ConnectAsync` throws `NotSupportedException`** — The spec says this is OK because `ModEntry` connects each child directly before constructing the multi. But this leaves an `IChatService` whose `ConnectAsync` is unsafe to call. Is this a smell? Alternatives: (a) split `IChatService` into a "consumer interface" (no ConnectAsync) and an "operator interface" (with ConnectAsync) so the multi only implements the consumer half; (b) accept the throw and document. Spec picks (b); reviewers may prefer (a).

5. **Aggregate state best-of-children semantics** — `MultiChatService.State` returns the BEST state across children (e.g., `ConnectedReadWrite` if any child is connected R/W). This matches "can we serve our purpose?" — if Twitch is alive, we can send receipts even if YT is dead. But it hides per-child state from any consumer that subscribes to `ConnectionStateChanged`. The spec mitigates with a separate `ChildConnectionStateChanged` event for `ModEntry`'s D8 receipts. Is this two-channel event API the right shape, or should the aggregate event carry per-child detail?

6. **Receipt flap suppression debounce (30s per platform)** — Spec picks 30s per-platform debounce on D8 mid-session receipts to avoid storm during YT state churn. This number is arbitrary. Too short risks receipt storm; too long delays "YouTube reconnected" feedback. Is 30s right? Or should it be cadence-tied (e.g., 2× the reconnect cadence, so 120s)?

7. **Should this slice ship before B.2.2 (Ancients vote), or after?** Architecturally independent — B.2.2 is a Harmony patch; this is a chat-layer addition. But operator-validation overlap means doing both at once probably doesn't fit a single focused-work week. Reviewers may have opinions on slice ordering. (Practical answer is "FrostPrime says when", but architectural opinions welcome.)

8. **Is the scraper isolation strategy sufficient?** All YouTube fragility lives in `Ti/Chat/YouTubeChat/`. When YouTube redesigns, the spec's claim is "1–2 LOC fix + fixture refresh". Is this realistic? Reviewers familiar with `pytchat`-class library maintenance may have observations on what tends to break in practice (e.g., the regex shape, the JSON path, the URL scheme, all of the above).

9. **Worst-of-both-worlds mode (Step 1 of acceptance gate)** — When Twitch is in terminal failure but YT is connected, votes still tally but no chat receipts fire (per D3 + Twitch is dead). In-game label is the only feedback. Spec documents this but doesn't optimise for it. Is this acceptable, or should the spec specify a fallback receipt mechanism (e.g., write to a file, log line for OBS to scrape, etc.)?

10. **Cross-platform Noita-pattern interaction** (vote-option-numbering collisions across back-to-back votes, worsened by YT's 2–5s lag) — Already tracked for v0.2 in notes/06, but this spec significantly increases the surface area for it. Should the meta-review push to fold the Noita-pattern fix INTO this spec, or keep it deferred to v0.2 polish?

---

## 11. Glossary / Domain Terms

### Slay the Spire 2 / game terms

- **Run**: A single playthrough from start to game-over or victory.
- **Act**: One of three (or more) major sections of a run.
- **Card reward / sub-screen / Proceed / Skip / etc.**: See B.2.1's context document — all relevant terms carried forward unchanged.
- **Neow**: The first NPC of every run, offering a "blessing" choice. B.1's vote target.
- **Ancients** (community term, formerly internal "boon-gods"): Pael, Tezcatara — start-of-act NPCs in StS2 that replaced the StS1 boss-relic system. Grant `RelicRarity.Ancient` relics. B.2.2's vote target.
- **DevConsole**: Vanilla in-game developer console; auto-unlocked when modded. Open with backtick.

### Twitch / chat terms

- **OAuth token**: User access token for the Twitch IRC connection.
- **PRIVMSG / JOIN / CAP REQ**: IRC primitives. See B.2.1 context doc.
- **Receipt**: A bot-sent chat message that announces vote state.
- **Tally label**: In-game UI showing live vote counts.

### YouTube / chat terms (NEW for this spec)

- **`youtubei` endpoint**: The internal API YouTube's own UI uses for live-chat polling. Specifically the `live_chat/get_live_chat` POST and the `INNERTUBE_API_KEY` extracted from `youtube.com/live_chat?v={videoId}`. Undocumented; subject to silent shape changes.
- **Continuation token**: Opaque string returned by `get_live_chat` for the next poll. Cycles per response.
- **`timeoutMs`**: Per-response field telling the client when to poll next. We honour it with floor 1s, ceiling 10s.
- **`authorChannelId`**: YouTube's stable per-author ID. Used as the YT-side `UserId` (prefixed `"yt:"` per D9).
- **Live broadcast** vs **stream**: A streamer's currently-active broadcast. Discovered via redirect on `youtube.com/channel/{channelId}/live`.
- **Channel ID**: A YouTube channel's stable identifier, looks like `UCabc123def456ghi789`. NOT the `@handle` (e.g., `@frostprime`). Visible in YouTube Studio's "Channel ID" setting, or in the channel-page URL.
- **Members-only chat**: A YouTube live-chat mode requiring channel membership. Anonymous scraping cannot read this — non-goal per D5.
- **Super Chat / Super Sticker / Membership Item**: Monetary chat items. Treated as normal chat for vote-parsing purposes (no special-case handling in v1).

### Project / architecture terms

- **TI / Twitch Integration core**: The game-agnostic `src/Ti/*` namespace. Now extending to multi-platform but keeping the "TI" name — "TI" effectively means "any platform Twitch-like chat", not literally Twitch.
- **Game glue**: `src/Game/*` — the StS2-specific layer.
- **Suspend-and-resume**: Established Harmony pattern from B.1. Not used in this spec.
- **`Voter.Default`**: Static facade over `VoteCoordinator`. Patches read it. **Wraps `MultiChatService` after this spec.**
- **`ShouldEnforceSkipGate()`** (B.2.1, preserved): Helper that disables the card-skip gate when the chat infrastructure is unavailable. Unchanged in this spec — only depends on Twitch chat state, not YouTube's.
- **`MultiChatService`** (NEW): Aggregator wrapping N child `IChatService` impls. Best-of-children for aggregate state; fan-out for sends to `CanSend == true` children only.
- **`PlatformOf(ChatMessage)`** (NEW): Private static helper in `VoteSession` that derives platform from `VoterKey.StartsWith("yt:")`. Two-way branch for v1; extensible to a registered table.
- **Per-platform tally side-dict** (NEW): `Dictionary<(string platform, int optionIndex), int>` on `VoteSession`. Parallel to the existing merged `_tallies`; feeds the in-game label's split rendering per D10.
- **`youtubei` scraper fragility**: The load-bearing risk. ~30% of LOC, ~70% of redesign-risk. Isolated in `Ti/Chat/YouTubeChat/`.
