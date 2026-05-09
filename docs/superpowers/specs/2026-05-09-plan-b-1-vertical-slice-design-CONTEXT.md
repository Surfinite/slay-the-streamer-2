# Context Document — Plan B.1 vertical slice

Companion document for `2026-05-09-plan-b-1-vertical-slice-design.md`. Reviewers should read this first, then the spec.

---

## 1. Reviewer Brief

You are receiving two documents: this **context document** and a **spec** (`2026-05-09-plan-b-1-vertical-slice-design.md`). Your role is to **critically analyze** the spec given the context provided.

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

**slay-the-streamer-2** is a fan-made mod for **Slay the Spire 2** (the .NET 9 / Godot 4.5.1 Mono roguelike from Mega Crit) that lets a Twitch streamer's chat vote on the streamer's in-game decisions. It's the StS2 successor to Tempus's original "Slay the Streamer" mod for StS1; the source codebase of that mod lives in `references/SlayTheStreamer-sts1/` as a feature-inventory reference only (different language, different modding API — not a code source).

**v0.1 vote inventory** (verified 2026-05-09 against Tempus's Java source):
- **Neow blessing** (in original — `StartGamePatch`)
- **Card reward** (NOT in original mod itself; came from `de.robojumper.ststwitch` base mod, which doesn't have an StS2 equivalent — we build it)
- **Boss relic pick** (in original — `NoSkipBossRelicPatch`)
- **Map path** (NOT in original; came from base mod — we build it)
- **Act boss** (in original — heavyweight: 534 LOC custom screen replacing post-treasure-room flow)

Event-choice and shop-purchase voting are explicitly deferred to v0.2 — they were not in Tempus's mod or its base-mod dependency, and they're new-design problems (what does "vote on a shop visit" even mean? per item? per visit? cost gating? skip allowed?).

### Stage of development

- **Plan A is complete.** The "TI core" — game-agnostic vote engine + IRC parser + outgoing message queue with rate limiting + all dispatcher/clock/scheduler abstractions — was built across ~30 tasks with 142 unit tests, all passing. Lives in `src/Ti/`. Released as commit `04xxxxxx` on the Plan A branch (long since merged to `main`).
- **Plan B prep ran.** A three-scenario smoke test (Smoke A from `[ModInitializer]`, Smoke B from a fire-and-forget Harmony prefix, Smoke C from a BLOCKING Harmony prefix) was implemented to validate two architectural concerns: (1) Godot autoload registration from a runtime-loaded mod assembly, and (2) the Harmony prefix + `await AwaitWinnerAsync()` deadlock risk. Both A and B succeeded; **C deadlocked the game exactly as the meta-review predicted**. Cleanup commit `4fa5a98` removed the disposable smoke files; the dispatcher infrastructure is now permanent in [`src/ModEntry.cs`](../../../src/ModEntry.cs) and [`src/Godot/`](../../../src/Godot/).
- **Plan B is decomposed into sub-plans.** B.1 (this spec) is the vertical slice: real `TwitchIrcChatService` + first Harmony patch (Neow) + minimal in-game UI + JSON-file credentials. B.2 (future spec) handles the remaining four votes + in-game settings UI; act-boss may warrant its own sub-plan due to its custom screen.

### Constraints

- **Solo hobbyist developer** (Windows 11, Godot Mono runtime). No deadline; goal is a shippable v0.1 with clean, testable architecture.
- **Mod cannot modify the game itself** — must work via the shipped modding API + Harmony runtime patching against `sts2.dll`.
- **TI core stays game-agnostic** — `src/Ti/` references nothing from Godot or `MegaCrit.Sts2.*`. This boundary is load-bearing for the eventual extraction of the TI core into a reusable base-mod assembly that other StS2 chat-driven mods could depend on (the "TI extraction goal").
- **Suspend-and-resume Harmony pattern is non-negotiable** post-smoke. Blocking-await on a Harmony prefix deadlocks the Godot main thread. Spec acknowledges this as a hard architectural constraint.

### Target users

End users are Twitch streamers who play Slay the Spire 2 and want chat participation. The "user" of the mod's installation flow is the streamer; the ongoing "users" are the chat viewers who type vote commands like `#0` / `#1` / `#2`.

---

## 3. Architecture & Tech Stack

### Languages & frameworks

- **C# 12 / .NET 9** (target framework `net9.0`).
- **Godot 4.5.1 Mono** (`Godot.NET.Sdk/4.5.1`) — the game engine StS2 runs on. Mod assemblies are .NET DLLs loaded into the Godot runtime via the StS2 modding API.
- **xUnit** for unit tests.
- **HarmonyLib** (`0Harmony.dll`, shipped with the game) for runtime method patching.

### High-level architecture (current state, post-Plan-A + post-smoke)

```
┌───────────────────────────────────────────────────────────────────┐
│  Plan A core: src/Ti/  (BCL-only, game-agnostic, 142 tests)       │
│                                                                   │
│  ┌────────────┐  ┌──────────────┐  ┌──────────────────────────┐   │
│  │ Voting     │  │ Chat         │  │ Internal                 │   │
│  │            │  │              │  │                          │   │
│  │ VoteSess   │  │ IChatService │  │ IClock + System + Fake   │   │
│  │ Coordinator│  │ FakeChat     │  │ ITimerScheduler + S/F    │   │
│  │ Voter      │  │ ChatMessage  │  │ IMainThreadDispatcher    │   │
│  │ Receipts   │  │ Internal:    │  │ TiLog (sink-redirected)  │   │
│  │            │  │   Parser     │  │ ImmediateDispatcher (test)│  │
│  │            │  │   Queue      │  │                          │   │
│  └────────────┘  └──────────────┘  └──────────────────────────┘   │
└───────────────────────────────────────────────────────────────────┘
                                ▲
                                │ Plan B.1 (this spec) adds:
                                │
┌───────────────────────────────┴───────────────────────────────────┐
│  Plan B.1 net new                                                 │
│                                                                   │
│  ┌──────────────────────────────────────┐  ┌──────────────────┐   │
│  │ Ti/Chat/TwitchIrcChatService.cs      │  │ Ti/Ui/           │   │
│  │   (full TLS + CAP + send queue +     │  │   VoteTallyLabel │   │
│  │    reconnect + state machine)         │  │   (Godot Label)  │   │
│  └──────────────────────────────────────┘  └──────────────────┘   │
│                                                                   │
│  ┌──────────────────────────────┐  ┌─────────────────────────┐    │
│  │ Game/Bootstrap/ModSettings   │  │ Game/DecisionVotes/     │    │
│  │   (JSON file reader)         │  │   NeowBlessingVotePatch │    │
│  │                              │  │   (Harmony, suspend-    │    │
│  │                              │  │    and-resume)          │    │
│  └──────────────────────────────┘  └─────────────────────────┘    │
│                                                                   │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │ ModEntry.cs (extends current skeleton: load settings,       │  │
│  │              wire Voter.Default, attach indicator, connect) │  │
│  └─────────────────────────────────────────────────────────────┘  │
└───────────────────────────────────────────────────────────────────┘
                                ▲
                                │ Already permanently wired (Plan B prep)
                                │
┌───────────────────────────────┴───────────────────────────────────┐
│  Permanent post-smoke scaffolding: src/Godot/                     │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │ DispatcherAutoload.cs       Node attached via                │ │
│  │                             tree.Root.CallDeferred(          │ │
│  │                               "add_child", autoload)         │ │
│  │                             during [ModInitializer].         │ │
│  │ GodotMainThreadDispatcher   Implements IMainThreadDispatcher │ │
│  │                             via CallDeferred("Run", ...).    │ │
│  │                             DrainAsync via barrier-Post.     │ │
│  └──────────────────────────────────────────────────────────────┘ │
└───────────────────────────────────────────────────────────────────┘
```

### Architectural decisions established before this spec

These are settled (Plan A + smoke verdict) and the spec inherits them:

| Decision | Source | Rationale |
|---|---|---|
| **Pure-function `TwitchIrcParser`** (no clock, no IO) | Plan A | Deterministically testable; clock-stamping deferred to `TwitchIrcChatService`. |
| **`IMainThreadDispatcher` abstraction** with `ImmediateDispatcher` (tests), `GodotMainThreadDispatcher` (production) | Plan A | Decouples the vote engine from Godot. The entire `Ti/` library is BCL-only (excepting `Ti/Godot/` and `Ti/Ui/`, which are deliberately Godot-dependent sub-namespaces). |
| **`IClock` + `ITimerScheduler`** abstractions with Fake/System impls | Plan A | Deterministic time control in tests. |
| **`Voter.Default` static facade over `VoteCoordinator`** | Plan A | Harmony-patched call sites can reach the coordinator without DI plumbing. Plan B.1's `ModEntry` sets this; the patch reads it. |
| **TCS uses `RunContinuationsAsynchronously`** on the winner Task | Plan A | Continuations don't piggyback the calling thread. |
| **Suspend-and-resume Harmony pattern; NEVER blocking-await on the main thread** | **Plan B prep smoke** | Smoke C confirmed: blocking on `AwaitWinnerAsync().GetAwaiter().GetResult()` from a Harmony prefix on `NSettingsScreen._Ready` deadlocked the game at startup. The chain: prefix blocks main thread → close timer fires on threadpool → dispatcher does `CallDeferred` → idle frame queued for main thread → main thread blocked → close never runs → `.GetResult()` waits forever. **Therefore: every Harmony prefix must return immediately after firing `_ = HandleVoteAsync(...)`; the async handler invokes the chat-winner's choice via `dispatcher.Post(...)` once the vote completes.** |
| **0-indexed vote options** (`#0`, `#1`, ...) | Plan A + StS1 mod precedent | Matches Tempus's mod and removes the chat-vs-list off-by-one. |
| **`OutgoingMessageQueue`: token-bucket rate limiter + priority queue + Low-coalescing** | Plan A | Twitch's send limit is 90/30s; periodic low-priority tally updates coalesce to the latest. Already implemented and tested. |

### Modding API details (relevant to the spec)

- StS2's `ModInitializerAttribute` declares an entry point (static method on a static class). Game calls it once at startup. `MegaCrit.Sts2.Core.Modding.ModManager` handles assembly load + `[ModInitializer]` discovery.
- The game ships its own `0Harmony.dll`. We reference the same one (with `<Private>false</Private>`) to avoid type-load conflicts at runtime.
- `MegaCrit.Sts2.Core.Logging.Log` is verified thread-safe (game's `Logger` holds a static lock around print + callback).
- `[ModInitializer]` runs during `NGame._EnterTree` — meaning the scene tree's Root is "busy setting up children". Direct `tree.Root.AddChild(node)` errors at this point. **Workaround established in smoke**: `tree.Root.CallDeferred("add_child", node)` defers the attach to the next idle frame.

---

## 4. Codebase Map

### Directory structure (relevant subset)

```
slay-the-streamer-2/
├── README.md
├── build.ps1                          # builds + tests; refreshes sts2.dll, 0Harmony.dll from game install
├── install.ps1                        # copies dist/ to game's mods folder
├── uninstall.ps1                      # removes mod from game's mods folder
├── docs/superpowers/specs/
│   ├── 2026-05-08-ti-layer-design-v2.md                     # Plan A spec (settled v2.3)
│   ├── 2026-05-08-plan-b-prep-smoke-test-design-v2.md       # Smoke spec (executed)
│   ├── META-REVIEW-2026-05-08-plan-b-prep-smoke-test-design.md  # The smoke's meta-review
│   ├── 2026-05-09-plan-b-1-vertical-slice-design.md         # ← THE SPEC UNDER REVIEW
│   └── 2026-05-09-plan-b-1-vertical-slice-design-CONTEXT.md # ← THIS DOC
├── notes/
│   ├── 02-original-mod-feature-inventory.md   # Tempus's mod scope (verified)
│   ├── 03-sts2-modding-api.md                 # Modding contract details
│   ├── 04-abstract-model-hook-surface.md      # AbstractModel hooks vs Harmony per-decision
│   ├── 05-build-pipeline.md
│   └── 06-followups-and-deferred.md           # Open items + smoke outcome captured
├── references/
│   └── SlayTheStreamer-sts1/                  # Tempus's Java source (feature reference; gitignored)
├── decompiled/sts2/                           # ilspycmd output (regeneration source; gitignored)
├── src/                               # the mod
│   ├── slay_the_streamer_2.csproj
│   ├── slay_the_streamer_2.json       # mod manifest
│   ├── project.godot
│   ├── icon.svg
│   ├── ModEntry.cs                    # PERMANENT skeleton; B.1 extends sections 6+
│   ├── sts2.dll                       # game ref, refreshed by build.ps1
│   ├── 0Harmony.dll                   # Harmony ref, refreshed by build.ps1
│   ├── Godot/                         # PERMANENT post-smoke scaffolding
│   │   ├── DispatcherAutoload.cs
│   │   └── GodotMainThreadDispatcher.cs
│   └── Ti/                            # Plan A TI core (29 files, ~1,200 LOC)
│       ├── Internal/                  # IClock, ITimerScheduler, IMainThreadDispatcher, TiLog, fakes, ImmediateDispatcher
│       ├── Chat/                      # IChatService, ChatMessage, ChatCredentials, ChatConnectionState, FakeChatService
│       │   └── Internal/              # TwitchIrcParser (pure), OutgoingMessageQueue (token-bucket+priority)
│       └── Voting/                    # VoteSession, VoteCoordinator, Voter, EnglishReceipts, VoteOption, VoteSessionState, VoteReceiptPolicy, VoteParsingPolicy, VoteSnapshot, ReceiptKind
└── tests/                             # xUnit, source-referenced
    ├── slay_the_streamer_2.tests.csproj
    ├── Internal/                      # tests for clock/scheduler/dispatcher/log
    ├── Chat/                          # tests for FakeChatService
    │   └── Internal/                  # tests for TwitchIrcParser + OutgoingMessageQueue
    └── Voting/                        # tests for VoteSession + Coordinator + Voter + Receipts
```

### Lines of code (rough scale)

- `src/Ti/`: ~1,200 LOC across 29 files.
- `src/Godot/`: ~80 LOC across 2 files.
- `src/ModEntry.cs`: ~85 LOC (skeleton with diagnostic logging).
- `tests/`: ~1,700 LOC, 142 tests passing.
- B.1 net new estimate: ~1,000 LOC source + ~230 LOC tests (per spec §"Architecture").

### Key contract files relevant to the spec

These are the contracts the spec implements against. Reviewers don't need to read them, but the spec assumes their shape:

- `Ti/Chat/IChatService.cs` — interface with `State` (enum), `IsConnected`, `CanSend`, `LastMessageReceivedAt`, `LastError`, events `MessageReceived` / `ConnectionStateChanged`, async `ConnectAsync` / `Disconnect` / `SendMessageAsync(text, priority, ct)`.
- `Ti/Chat/ChatConnectionState.cs` — enum: Disconnected, Connecting, ConnectedReadOnly, ConnectedReadWrite, Reconnecting, AuthenticationFailed (terminal), JoinFailed (terminal), Disposed.
- `Ti/Chat/ChatMessage.cs` — positional record `(UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip, VoterKey)`.
- `Ti/Chat/ChatCredentials.cs` — record with `oauth:` prefix normalisation, redacted `ToString`.
- `Ti/Voting/VoteCoordinator.cs` — instance-based session owner. Constructor signature is `(IChatService chat, IClock clock, ITimerScheduler scheduler, IMainThreadDispatcher dispatcher, Random? random = null)` — verified at [src/Ti/Voting/VoteCoordinator.cs:24](../../../src/Ti/Voting/VoteCoordinator.cs#L24). `Start(label, options, duration, receipts?, parsing?, formatReceipt?, ct?)` opens a session; throws if one is already open on the coordinator. *(Note: an earlier draft of this CONTEXT doc had the parameter order incorrect as `(chat, dispatcher, scheduler, clock, ...)` — corrected here to match actual code.)*
- `Ti/Voting/Voter.cs` — static facade: `public static VoteCoordinator? Default { get; set; }` and a `Start(...)` convenience that delegates.
- `Ti/Voting/VoteSession.cs` — exposes `Tallies`, `Options`, `TimeRemaining`, `State`, `WinnerIndex`, events `TallyChanged` / `Closed` / `Cancelled`, methods `AwaitWinnerAsync(ct)` / `CloseNow()` / `Cancel()` / `Dispose()`.
- `Ti/Internal/IMainThreadDispatcher.cs` — `Post(Action)` + `Task DrainAsync()`.
- `Ti/Internal/TiLog.cs` — static helper with `Debug` / `Info` / `Warn` / `Error(msg, ex?)` and an overrideable `Sink`.
- `Ti/Chat/Internal/OutgoingMessageQueue.cs` — token-bucket rate limiter + priority queue + Low-coalescing. Already built and tested.
- `Ti/Chat/Internal/TwitchIrcParser.cs` — pure function parser with full IRCv3 tag-escaping support. Already built and tested.

The single not-yet-implemented Plan A contract: **`TwitchIrcChatService`**, the production IRC client. Plan A's spec v2.3 specifies its full behaviour (TLS, CAP REQ tags+commands, send queue, reconnect, state machine, IRC protocol matrix). B.1 implements it.

### Decompile evidence relevant to the spec

The spec's patch target was identified by reading the decompiled StS2 sources (regenerated from `sts2.dll` via `ilspycmd`):

- **`MegaCrit.Sts2.Core.Models.Events.Neow`** extends `AncientEventModel`. `GenerateInitialOptions()` returns ~3 `EventOption` instances (1 curse + 2 positive, randomly chosen from larger pools).
- **`MegaCrit.Sts2.Core.Events.EventOption`** has `async Task Chosen()` which awaits an `OnChosen` callback. Importantly, Chosen() does NOT have a back-reference to its event model.
- **`MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom.OptionButtonClicked(EventOption option, int index)`** is the player's click handler. For non-`IsProceed` options, it calls `RunManager.Instance.EventSynchronizer.ChooseLocalOption(index)` which eventually fires `TaskHelper.RunSafely(eventOption.Chosen())` — fire-and-forget. **`OptionButtonClicked` is the chosen patch target**: synchronous void; takes both the option and its index; lives in StS2's Core.Nodes.Rooms.
- The `_event` field on `NEventRoom` is private; `_event.CurrentOptions` is public. The patch will use `AccessTools.Field(typeof(NEventRoom), "_event")` to read the active event model + its options.

---

## 5. Relevant Existing Patterns & Conventions

### Coding conventions

- C# 12, file-scoped namespaces, `Nullable enable` everywhere.
- Internal constructors for types only-instantiated-via-factories (`VoteOption`, `VoteSession`).
- File-per-type, kept small. Largest TI file is ~270 LOC.
- 0-indexed vote options.
- Em-dashes (—, U+2014) in spec strings and commit messages preserved verbatim.
- All commits follow `plan-X/Y.Z: <subject>` prefix and end with a `Co-Authored-By: Claude` trailer.

### Testing strategy

- **xUnit, deterministic fakes everywhere.** No real time, no real network.
- TDD for engine logic: failing test → impl → green → commit.
- `FakeClock` + `FakeTimerScheduler` + `ImmediateDispatcher` form the deterministic timing harness.
- Static-state isolation via xUnit `[Collection("TiLog.Sink")]` (multi-class parallelism guard for the static `TiLog.Sink` setter).
- Source-referenced (not DLL-referenced) so `internal` types are testable without `InternalsVisibleTo` ceremony.
- 142 tests pass at HEAD.

### Logging convention

- All TI code uses `TiLog.Info(...)` / `TiLog.Warn(...)` / `TiLog.Error(...)`.
- `TiLog.Sink` defaults to a passthrough into `MegaCrit.Sts2.Core.Logging.Log` — already wired in `ModEntry.cs`.
- `TiLog` scrubs anything matching `oauth:[a-z0-9]+` before forwarding (defense-in-depth atop `ChatCredentials.ToString` redaction).

### Build & test loop

- `pwsh -File build.ps1` refreshes `sts2.dll` + `0Harmony.dll` from the game install, runs `dotnet test`, then `dotnet publish` to `dist/publish-tmp/`, then assembles `dist/slay_the_streamer_2/`.
- `pwsh -File install.ps1` copies `dist/slay_the_streamer_2/` to `<game-install>/mods/slay_the_streamer_2/`.
- `pwsh -File uninstall.ps1` removes the install.
- All three scripts accept a `-GameInstall` parameter (default Steam path on Windows).

### Patterns the spec must respect

- **Dispatcher contract** — `IMainThreadDispatcher.Post(Action)` is the hop point for marshalling work onto the main thread. Plan A's TCS uses RunContinuationsAsynchronously specifically so consumers don't accidentally land on the wrong thread.
- **Voter facade** — Plan B.1 integrates by setting `Voter.Default = coordinator`, not by constructing parallel coordinators. (Multi-coordinator is a v0.2+ multiplayer concern.)
- **TI / Game seam** — `Ti/*` MUST NOT reference `Game/*` or `MegaCrit.Sts2.*` (with the exception of `Ti/Godot/` and `Ti/Ui/`, which deliberately reference Godot but never `sts2.dll`). Code-review enforcement; Roslyn analyser is post-MVP.

---

## 6. Current State & Known Issues

### What works today

- 142/142 tests pass.
- `pwsh -File build.ps1` reports green ("Plan B prep build cycle: OK").
- Game can boot with the mod loaded; `[slay_the_streamer_2] Init complete (skeleton — Plan B will fill in vote wiring).` appears in the log.
- The dispatcher infrastructure is live: `CallDeferred`-based hops from background threads to the Godot main thread work, validated by Smoke A and Smoke B.

### Smoke verdict (architectural finding that drives this spec)

The smoke ran on 2026-05-09 (commit `204d061` → `4fa5a98`):

- **Smoke A** ✅ (fire-and-forget vote opened from `[ModInitializer]`).
- **Smoke B** ✅ (fire-and-forget vote opened from a Harmony prefix on `NMainMenu._Ready`).
- **Smoke C** ❌ **Game hung at startup.** The blocking `session.AwaitWinnerAsync().GetAwaiter().GetResult()` from a Harmony prefix on `NSettingsScreen._Ready` deadlocked exactly as the meta-review predicted. (StS2 instantiates Settings during boot, before the main menu — so the hang manifested as "won't get to title screen".)

**Verdict**: Plan A's `RunContinuationsAsynchronously` on the winner TCS is **insufficient under Godot's main-thread sync context**. Godot 4 installs a `SynchronizationContext` on the main thread that re-captures `await` continuations onto thread 1; combined with the dispatcher's `CallDeferred` (which needs an idle frame on thread 1), any blocking-await chain on thread 1 deadlocks. Plan B must use suspend-and-resume only.

### What's wired today (already permanent)

From [`src/ModEntry.cs`](../../../src/ModEntry.cs):
- Top-level try/catch around the entire `Init()` body (bound mod-load blast radius).
- Godot version + main loop type logged for cross-version troubleshooting.
- `SceneTree` cast with explicit null check.
- `DispatcherAutoload` attached via `tree.Root.CallDeferred("add_child", autoload)` (deferred-attach because Root is busy during NGame._EnterTree).
- Optional `Engine.RegisterSingleton("DispatcherAutoload", autoload)` instrumentation (warn-and-continue on failure).
- `GodotMainThreadDispatcher` wired and pointed at the autoload.
- `TiLog.Sink` passthrough to `MegaCrit.Sts2.Core.Logging.Log` (with thread-safety inherited from the game's logger).
- `Harmony.PatchAll(Assembly.GetExecutingAssembly())` runs and logs the patched method count (currently 0).

### What's missing (B.1 fills in)

- `TwitchIrcChatService` (the real IRC client; Plan A only shipped `FakeChatService`).
- `Voter.Default` wiring in `ModEntry.Init` (was set during the smoke; removed in the cleanup commit).
- `ModSettings` JSON config reader.
- `NeowBlessingVotePatch` (the first Harmony patch).
- `VoteTallyLabel` (Godot Control for in-game vote display).

### Known issues / sharp edges acknowledged in the spec

- **Streamer escapes Neow screen mid-vote**: vote keeps running in background; resume Post will eventually call `OptionButtonClicked` on a screen that no longer exists. May throw inside StS2; HandleVoteAsync's catch logs at Error. Spec flags as a known sharp edge for B.2 / polish to address.
- **Player click race during the resume**: between the resume's `_voteInProgress = 0` and the chat-chosen blessing's full effect application (`option.Chosen()` runs async), a fast streamer click could trigger a fresh vote that races against in-progress effects. Spec flags as something to validate during operator testing.
- **In-game error toast for auth failure** is deferred to B.2. B.1 logs to the mod log and silences the chat receipt; the streamer has to check the log to know "auth failed; check oauth token".

---

## 7. Context Specific to the Plan/Spec

### What B.1 touches

- **Adds** `src/Ti/Chat/TwitchIrcChatService.cs` (full Plan A v2.3 implementation; ~600 LOC).
- **Adds** `src/Ti/Ui/` directory with `VoteTallyLabel.cs` (~50 LOC). New sub-namespace.
- **Adds** `src/Game/` directory tree with `Bootstrap/ModSettings.cs` and `DecisionVotes/NeowBlessingVotePatch.cs`.
- **Modifies** `src/ModEntry.cs` — extends sections 6+ to load settings, build the chat service, wire `Voter.Default`, send the connect receipt, connect non-blocking. Promotes `dispatcher` from local to public static for the patch to read.
- **Adds** `tests/Chat/TwitchIrcChatServiceTests.cs` and `tests/Bootstrap/ModSettingsTests.cs`.
- **Does NOT modify** anything in `src/Ti/Internal/`, `src/Ti/Voting/`, `src/Ti/Chat/Internal/`, or `src/Godot/`. Plan A's tests stay green by design.

### Prior attempts / rejected approaches

- **Blocking-prefix-await pattern was tested and rejected by Smoke C.** Spec acknowledges and constrains the patch shape accordingly.
- **`EventOption.Chosen()` as the patch target was considered and rejected.** It's `async Task` (initially attractive — could "just return a longer Task"), but the call sites use `TaskHelper.RunSafely(option.Chosen())` which is fire-and-forget. The game proceeds without awaiting; a longer Task wouldn't suspend anything. Suspend-and-resume via `OptionButtonClicked` is the right pattern.
- **In-game settings UI was considered for B.1 and deferred to B.2.** B.1 reads from a JSON file at `%APPDATA%\Godot\app_userdata\Slay the Spire 2\slay_the_streamer_2.json` (or platform equivalent). The settings UI in B.2 will write to the same file format, so migration is "settings UI replaces JSON-edit-by-hand", not "data migration".
- **The full `VoteOverlayControl` from Plan A v2.3's spec was considered for B.1 and deferred to B.2.** Polish (animated bars, percentages, autohide fade, winner-highlight) is out of B.1; only the multi-line text label is in.
- **Event-choice and shop-purchase voting were considered for v0.1 and pushed to v0.2.** Verified from Tempus's source: they were not in the original mod or its base-mod dependency. Each is a new design problem and warrants its own design pass.
- **Single-flag re-entry guard was considered and caught wrong during spec self-review.** A single `_voteInProgress` flag would let repeat-clicks during the vote pass through to the original `OptionButtonClicked` (which would call `ChooseLocalOption` and race the chat-chosen resume). Spec uses two flags: `_voteInProgress` (set across the whole vote; suppresses repeat clicks) + `_resumeInProgress` (set only around the resume's re-call; lets the chat-chosen click pass through).

### Dependencies & integrations

- **Twitch IRC** at `irc.chat.twitch.tv:6697` over TLS. `TwitchIrcChatService` connects, JOINs the streamer's channel, parses incoming PRIVMSGs via Plan A's parser, sends outgoing receipts via Plan A's queue.
- **Godot SceneTree** for attaching `VoteTallyLabel` as a child of the active `NEventRoom`.
- **StS2 modding API** for `[ModInitializer]` discovery.
- **HarmonyLib** for the Neow patch.
- **`%APPDATA%`** (or platform equivalent) for the settings JSON file path.

### Performance / scale / security

- **Performance**: the per-vote receipt traffic (~6 messages per 30s vote) is well under Twitch's 90/30s rate limit. Plan A's queue enforces the limit regardless.
- **Security**: oauth tokens stored in plaintext on disk in the JSON file. Same threat model as any non-OS-keychain solution. `TiLog` and `ChatCredentials.ToString` both scrub `oauth:[a-z0-9]+` to prevent accidental log leaks.
- **No multiplayer support in B.1.** Spec acknowledges that `EventSynchronizer` shows multiplayer-aware seams in StS2; the patch will bail out (return true, log Warn) if `_event.Owner.RunState.Players.Count > 1`.

---

## 8. Scope Boundaries

### Out of scope (deliberately)

- **The other four v0.1 Harmony patches** — card reward, boss relic, map path, act-boss. All deferred to B.2+.
- **In-game settings panel** for oauth/channel/policy configuration. Deferred to B.2; B.1 reads from JSON.
- **Polished `VoteOverlayControl`** (animated bars, percentages, autohide fade, winner-highlight). Deferred to B.2 / polish.
- **`ChatStatusControl`** in-game connection-status indicator. Deferred to B.2.
- **In-game error toast** for auth failure. Deferred.
- **Localised receipts.** English-only via Plan A's `EnglishReceipts`.
- **Multiplayer co-op support.** B.1 bails out if `Players.Count > 1`. v0.2+ design.
- **Streamer-escape-mid-vote handling.** Acknowledged sharp edge; B.2 / polish.
- **Sealed deck.** Not in v0.1 at all.
- **Event-choice / shop-purchase voting.** Deferred to v0.2 as new-design.

### Fixed and non-negotiable

- **Suspend-and-resume Harmony pattern.** The smoke proved blocking-await deadlocks; reviewers should not waste time suggesting alternatives that block the main thread.
- **`Voter.Default` is the integration point.** Plan A's facade. Don't suggest reworking the coordinator hierarchy for B.1.
- **Mod ID is `slay_the_streamer_2`.** Don't suggest renames.
- **TI core stays BCL-only** (with `Ti/Godot/` and `Ti/Ui/` as the deliberately-Godot-dependent sub-namespaces). Load-bearing for the TI extraction goal.
- **JSON-file credentials for B.1** (settings UI is B.2). Don't suggest the settings UI moves into B.1.
- **Single-coordinator design.** Multi-coordinator is for v0.2+ multiplayer.
- **`[ModInitializer]` is the entry point.** Game's auto-PatchAll fallback is insufficient — we need explicit setup.
- **Patch target is `NEventRoom.OptionButtonClicked`.** Decompile-evidenced; alternatives (`EventOption.Chosen`, `EventSynchronizer.ChooseLocalOption`) were considered and rejected for the reasons in §"Prior attempts".

### Trade-offs accepted deliberately

- **Slow iteration loop for B.1** (one Neow vote per fresh run) traded for the simplest decision shape and zero stacking concerns.
- **JSON-file credentials only Surfinite + technical early testers can configure** in B.1 (no streamer-friendly UI yet) traded for shipping the architecture validation faster. Settings UI in B.2 onboards real streamers.
- **Plain text in-game indicator** (no animations, bars, percentages, winner-highlight, autohide fade) traded for fast B.1 ship. Polish in B.2.
- **No retry / resilience for `Voter.Default.Start` exceptions in the patch** — if Start throws (race condition, etc.), the streamer's click is lost. Worst case: re-click works. Sharp edge documented; not blocking.

---

## 9. Success Criteria

### Acceptance gate (B.1 done)

All of:

1. All 142 Plan A tests still pass.
2. All new unit tests pass (~25 tests across `ModSettingsTests` + `TwitchIrcChatServiceTests`).
3. **IRC operator-validation green**: `TwitchIrcChatService` connects to a throwaway test channel using Surfinite's testbot oauth; `MessageReceived` fires for messages from a second client; sent messages appear in the channel.
4. **Full Neow vote operator-validation green**: launch StS2 → start run → reach Neow → click any blessing → in-game `VoteTallyLabel` shows all options + counts + remaining seconds; chat receipt posted; second client sends `#0`/`#1`/`#2`; tally updates; vote closes; close receipt posted; chat-chosen blessing applies; game proceeds; streamer's repeat-clicks during vote no-op cleanly.
5. **Failure-mode operator-validation green** for at least: no settings file (mod loads silently, Neow plays vanilla); bad oauth (mod loads, AuthenticationFailed logged, Neow plays vanilla); mid-vote disconnect (reconnect happens, post-reconnect votes tally, close receipt notes the disconnect gap).
6. `notes/06` updated with B.1 outcome.

### Quality bar

- Plan-A-permanent quality for everything in `Ti/*`, `Game/*`, `ModEntry.cs`. No shortcuts.
- All new code follows existing conventions (file-per-type, internal-where-appropriate, TiLog usage, etc.).
- Smoke equivalent: in-game operator validation is the same model as Plan B prep used.

### What B.1 unlocks for B.2

- TI core in production (B.2 just adds patches; no IRC work).
- Suspend-and-resume Harmony pattern validated in real game-state mutation (not just a no-op smoke).
- `_voteInProgress` + `_resumeInProgress` re-entry pattern proven (B.2 patches reuse the shape).
- Credentials story works end-to-end (B.2 adds settings UI on top of the same JSON file format).
- TI/Game seam validated under real use.

---

## 10. Key Questions for Reviewers

These are areas where reviewer input would be most valuable beyond the general critique:

1. **The two-flag re-entry guard.** The spec uses `_voteInProgress` (set across the whole vote; suppresses repeat clicks via `return false`) + `_resumeInProgress` (set only around the resume's re-call; lets the chat-chosen click pass via `return true`). The single-flag bug was caught in self-review. Are there edge cases the two-flag design still misses? Specifically: between the resume's `_resumeInProgress = 0` and the resume's `_voteInProgress = 0` (one line apart, both inside the same dispatcher.Post action), can any other thread observe inconsistent state? Or is the same-thread same-Post execution sufficient? Consider also: a player click that lands BETWEEN the resume completing and `option.Chosen()` finishing its async work — is the spec's "fast streamer click could trigger a fresh vote that races in-progress effects" sharp edge serious enough to warrant additional mitigation in B.1, or genuinely B.2-deferrable?

2. **Patch shape: is `NEventRoom.OptionButtonClicked` the right intercept point?** Decompile evidence (in §"Decompile evidence"): `OptionButtonClicked(option, index)` is synchronous void; for non-IsProceed options it calls `EventSynchronizer.ChooseLocalOption(index)` which eventually reaches `TaskHelper.RunSafely(option.Chosen())`. Alternatives considered: `EventOption.Chosen` (rejected because fire-and-forget at call sites) and `EventSynchronizer.ChooseLocalOption` (would put the patch in a different layer, potentially better-isolated from UI changes; might be cleaner for multiplayer-aware design later). Is `OptionButtonClicked` the right call, or should the patch live at the `EventSynchronizer` layer instead?

3. **`TwitchIrcChatService` test depth in B.1.** The spec proposes ~13 unit tests via an internal `IIrcTransport` test seam (state machine transitions, auth-failure path, reconnect-with-jitter, PING/PONG, RECONNECT command, self-echo, dispatcher-Post invariant, anonymous mode, dispose). The actual TLS socket I/O is operator-validated only. Is this depth right for B.1? Specifically: is operator-validating the live TLS path sufficient for B.1 acceptance, or should B.1 invest in a mock IRC server (substantial test infrastructure)? Trade-off: more confidence vs more infrastructure. B.2 adds 4 more patches; if `TwitchIrcChatService` has a bug, B.2 will surface it.

4. **`VoteTallyLabel` lifecycle and positioning.** The spec attaches the Label as a child of `NEventRoom` (the active event room). `_Process` polls; `Closed`/`Cancelled` trigger `QueueFree`. Position is hardcoded top-right. Surfinite plans a positioning pass during polish. Concerns: (a) does attaching as a child of `NEventRoom` cause Godot to free our Label automatically when the room is freed? If so, `QueueFree` on `Closed` is double-free. (b) Is `_Process` polling once per frame appropriate, or should the Label subscribe to `TallyChanged` events instead (frame-pacing concern)? (c) Does the choice of `Label` vs `RichTextLabel` matter — long blessing labels with BBCode markup (e.g., `[color]` tags from the game's localisation strings) might render strangely in plain Label.

5. **Failure-mode coverage in operator-validation.** Spec lists three failure modes for operator-validation: no settings file, bad oauth, mid-vote disconnect. Are there other failure modes that should be explicitly validated before B.1 ships? Candidates: (a) settings JSON exists but fields are empty/whitespace; (b) channel doesn't exist on Twitch (JoinFailed terminal state); (c) network failure on initial connect (transport retry forever); (d) `Voter.Default.Start` throws because of an unexpected race. The spec acknowledges these in the failure-modes table but only validates the top three operator-side; the others are unit-test-only.

6. **`ModEntry.Dispatcher` as a public static field.** B.1 promotes the dispatcher from a local in `Init()` to `public static GodotMainThreadDispatcher Dispatcher` so `NeowBlessingVotePatch.HandleVoteAsync` can read it for the resume Post. This is the simplest plumbing but introduces a public mutable static. Alternatives: (a) make it a get-only property with private setter (cosmetic); (b) thread it through `Voter.Default` (`coordinator.Dispatcher` — would need to add a property to `VoteCoordinator`); (c) construct an `IServiceLocator`-shaped object in ModEntry and expose that. Is the simple public static appropriate for B.1, or does it set a bad precedent for B.2's other patches?

---

## 11. Glossary / Domain Terms

| Term | Meaning |
|---|---|
| **TI core** | Twitch-integration core. The game-agnostic vote engine + IRC parser + outgoing queue built in Plan A. Lives in `src/Ti/`. |
| **TI extraction goal** | The eventual lift of `src/Ti/` into a separate base-mod assembly that other StS2 chat-driven mods could reuse. Plan A's seams are designed for this. |
| **Plan A / Plan B / Plan C** | Phase decomposition. A = TI core (done). B = Godot integration + real IRC client + Harmony patches + UX (in progress; B.1 = vertical slice). C = post-MVP IRC fixture-generator tool. |
| **Plan B prep / smoke** | The architectural validation done before B.1 was specced. Three Harmony-patch scenarios (A: `[ModInitializer]`; B: fire-and-forget Harmony prefix; C: blocking Harmony prefix). C deadlocked, proving suspend-and-resume is the only viable pattern. |
| **Suspend-and-resume** | The required Harmony pattern post-smoke. Prefix returns `false` (skip original) after firing `_ = HandleVoteAsync(...)`. Background task runs the vote, then `dispatcher.Post(...)` re-invokes the original action with the chat-chosen index. |
| **`[ModInitializer]`** | StS2 modding API attribute applied to a static class to declare it as a mod entry point. Named static method runs once at game startup. |
| **`Voter.Default`** | Plan A's static facade. A `VoteCoordinator?` field set once at startup; Harmony-patched call sites use `Voter.Start(...)` to reach it. |
| **`AwaitWinnerAsync`** | `VoteSession` method returning `Task<int>` that completes with the winning option index when the vote closes. Uses `RunContinuationsAsynchronously` so continuations don't run on the main thread. The smoke proved the flag is necessary but not sufficient — blocking the main thread on the result still deadlocks. |
| **`DispatcherAutoload` / `GodotMainThreadDispatcher`** | Permanent post-smoke scaffolding. The first is a Godot Node attached to SceneTree.Root via deferred-add; the second is the `IMainThreadDispatcher` impl that uses `CallDeferred("Run", ...)` to hop work onto the main thread. |
| **`NEventRoom`** | StS2's per-event UI room class (`MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom`). `OptionButtonClicked(option, index)` is the player's click handler — and B.1's Harmony patch target. |
| **`EventOption.Chosen()`** | StS2's per-option selection method. `async Task`. Called fire-and-forget via `TaskHelper.RunSafely(option.Chosen())` from `EventSynchronizer.ChooseLocalOption`. NOT the patch target (because fire-and-forget — extending it doesn't suspend the game). |
| **`EventSynchronizer`** | StS2's multiplayer-aware option-sync layer. `ChooseLocalOption(index)` is called by `OptionButtonClicked`. Multiplayer support in StS2 routes through here. |
| **Suspend-and-resume re-entry guard** | Two-flag pattern in B.1's patch: `_voteInProgress` (set across the whole vote; suppresses repeat clicks) + `_resumeInProgress` (set only around the resume; lets chat-chosen click pass through). Single-flag designs have a bug where repeat-clicks race the resume. |
| **CallDeferred** | Godot mechanism for queuing a method call to run on the next frame's idle period (main thread). The standard cross-thread hop in Godot 4.x. |
| **Harmony / `Harmony.PatchAll`** | HarmonyLib runtime method patching. `PatchAll` scans an assembly for `[HarmonyPatch]` types and applies them. The game ships `0Harmony.dll`; we reference it with `<Private>false</Private>`. |
| **0-indexed options** | Vote command convention: viewers type `#0`, `#1`, `#2` (not `#1`, `#2`, `#3`). Matches Tempus's StS1 mod and removes off-by-one. |
| **`TaskHelper.RunSafely`** | StS2 utility that runs a Task fire-and-forget with exception logging. The reason `EventOption.Chosen()` isn't a viable patch target. |
| **stable / beta branch** | Steam branches for Slay the Spire 2. Surfinite is on stable for Plan B (was on beta during Plan A). Modding API is byte-identical between branches; AbstractModel hooks have signature drift (beta is the newer dev branch). |
| **TI / Game seam** | The boundary between `src/Ti/*` (extractable, BCL-only or BCL+Godot) and `src/Game/*` (StS2-specific glue). `Game/*` references `Ti/*`; `Ti/*` MUST NOT reference `Game/*`. |
