# Context Document — Plan B prep smoke test

Companion document for `2026-05-08-plan-b-prep-smoke-test-design.md`. Reviewers
should read this first, then the spec.

---

## 1. Reviewer Brief

You are receiving two documents: this **context document** and a **spec**
(`2026-05-08-plan-b-prep-smoke-test-design.md`). Your role is to **critically
analyze** the spec given the context provided.

You should identify weaknesses, risks, missing considerations, better
alternatives, unnecessary complexity, things that should be removed, and
things that are good and should be preserved. Suggest additions, potential
future features worth considering, and architectural improvements. Be
constructively critical — do not rubber-stamp.

Your review will be synthesized in a meta-review to improve the spec, so be
**specific and actionable**. Reference section names and code snippets
directly.

**Important:** You do NOT have direct access to the codebase. You are
working from this context document only. The spec author has full codebase
access and will validate all suggestions against the actual code during the
meta-review. Flag where you feel uncertain due to limited visibility, and
note any assumptions you are making about the code.

### Review Output Format

1. **One-line verdict**: overall assessment in a single sentence.
2. **What's good**: what should be kept as-is and why.
3. **Concerns & risks**: ranked by severity.
4. **Suggested changes**: specific, actionable modifications.
5. **Alternatives**: different approaches worth considering.
6. **Additions**: things missing that should be there.
7. **Removals**: things that shouldn't be in scope.
8. **Minor / nits**: low-priority observations.
9. **Assumptions you're making**: where you lacked codebase visibility and
   guessed. The spec author will validate these.

Reference section names or code snippets from the spec. Don't soften
criticism — the goal is to improve the spec, not to be polite about it.

---

## 2. Project Overview

**slay-the-streamer-2** is a fan-made mod for **Slay the Spire 2**
(`MegaCrit.Sts2`, .NET 9 / Godot 4.5.1 Mono game) that lets a Twitch
streamer's chat vote on the streamer's in-game decisions: card rewards,
Neow blessings, event choices, boss reward picks, shop purchases, and map
path selection.

It is the StS2 successor to Tempus's StS1 mod of the same concept (the
StS1 mod is included in `references/SlayTheStreamer-sts1` as a feature
inventory reference, not a code reference — different language, different
modding API).

### Stage of development

- **Plan A is complete.** The "TI core library" (Twitch-integration core
  — game-agnostic vote engine + IRC parser + outgoing message queue with
  rate limiting) was built across 33 commits, ~30 tasks, with full test
  coverage (142 unit tests, all passing). Plan A is the dependency-free
  foundation.
- **Plan B is the next phase.** Plan B will wire the TI core into the
  actual game via Godot autoloads, Harmony patches against StS2 game
  methods, and a real `TwitchIrcChatService` (the live IRC client built on
  top of Plan A's `TwitchIrcParser` and `OutgoingMessageQueue`).
- **The spec under review is "Plan B prep"** — a smoke test designed to
  validate two architectural assumptions before Plan B writing begins.

### Constraints

- Solo hobbyist developer (Windows 11, Godot Mono runtime).
- No deadline; goal is a shippable v0.1 with clean, testable architecture.
- Mod must not require modifying the game itself — must work via the
  shipped modding API + Harmony runtime patching.
- The TI core is deliberately game-agnostic (designed for later extraction
  into a reusable base mod assembly — the "TI extraction goal").

### Target users

End users are Twitch streamers who play Slay the Spire 2 and want chat
participation. The "user" of the mod's installation flow is the streamer;
the ongoing "users" are the chat viewers who type vote commands.

---

## 3. Architecture & Tech Stack

### Languages & frameworks

- **C# 12 / .NET 9** (target framework `net9.0`).
- **Godot 4.5.1 Mono** (`Godot.NET.Sdk/4.5.1`) — the game engine StS2 is
  built on. Mod assemblies are .NET DLLs loaded into the Godot runtime.
- **xUnit** for unit tests.
- **HarmonyLib** (`0Harmony.dll`, shipped with the game) for runtime
  method patching when the official modding API doesn't suffice.

### High-level architecture (current state, post-Plan-A)

```
┌─────────────────────────────────────────────────────────────┐
│  Plan A core: src/Ti/  (BCL-only, game-agnostic, TESTED)    │
│                                                             │
│  ┌──────────┐  ┌──────────┐  ┌──────────────────────────┐  │
│  │ Voting   │  │ Chat     │  │ Internal                 │  │
│  │          │  │          │  │                          │  │
│  │ VoteSess │  │ IChat    │  │ IClock, ITimerScheduler  │  │
│  │ Coord    │  │ FakeChat │  │ IMainThreadDispatcher    │  │
│  │ Voter    │  │ Internal:│  │ TiLog (sink-redirected)  │  │
│  │ Receipts │  │   Parser │  │                          │  │
│  │          │  │   Queue  │  │                          │  │
│  └──────────┘  └──────────┘  └──────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                          ▲
                          │ Plan B will add:
                          │
┌─────────────────────────┴───────────────────────────────────┐
│  Plan B (NOT YET WRITTEN; this spec is prep for it)         │
│                                                             │
│  ┌──────────┐  ┌──────────────────┐  ┌─────────────────┐   │
│  │ ModEntry │  │ Godot/           │  │ Patches/        │   │
│  │ [ModInit]│  │  DispatcherAuto  │  │  CardRewardPatch│   │
│  │          │  │  GodotDispatcher │  │  NeowPatch ...  │   │
│  └──────────┘  └──────────────────┘  └─────────────────┘   │
│                                                             │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ TwitchIrcChatService : IChatService                  │   │
│  │   uses TwitchIrcParser + OutgoingMessageQueue        │   │
│  └──────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

### Key architectural decisions already made (Plan A)

| Decision | Rationale |
|---|---|
| **Pure-function `TwitchIrcParser`** (no clock, no IO) | Deterministically testable; clock-stamping deferred to `TwitchIrcChatService` (Plan B). |
| **`IMainThreadDispatcher` abstraction** with `ImmediateDispatcher` (tests) and a future `GodotMainThreadDispatcher` (production) | Decouples vote-engine from Godot; the entire TI library is BCL-only. |
| **`IClock` + `ITimerScheduler`** abstractions with `Fake*` and `System*` impls | Deterministic time control in tests; real `System.Threading.Timer` in production. |
| **`Voter.Default` static facade** over `VoteCoordinator` | Harmony-patched call sites can reach the coordinator without DI plumbing. Multi-coordinator scenarios bypass the facade. |
| **`AwaitWinnerAsync` returns Task<int>**, uses `RunContinuationsAsynchronously` | Continuations don't piggyback the calling thread; intended to avoid main-thread reentrancy in Harmony prefixes. |
| **`TiLog` static helper with overrideable `Sink`** | Production redirects to game's `MegaCrit.Sts2.Core.Logging.Log`; tests redirect to a captured list. |
| **Fire-and-forget receipts with `OnlyOnFaulted` continuation** | Plan A's hardening pass (5.9) wired this — receipt sends log faults via TiLog rather than silently swallowing exceptions. |
| **`OutgoingMessageQueue` with token-bucket + priority + Low-coalescing** | Twitch's send limit is ~100/30s; periodic low-priority tally updates coalesce to the latest. |

### Plan-B-specific seams pre-drawn in Plan A

The TI library deliberately exposes seams designed for Plan B integration:

- `IMainThreadDispatcher` (production impl is the one this spec validates).
- `IChatService` (production impl will be `TwitchIrcChatService`, not yet
  written; `FakeChatService` is the test impl already used in 142 tests).
- `Voter.Default` (production wires from `[ModInitializer]`).
- `TiLog.Sink` (production redirects to game logging).
- `EnglishReceipts` static helper + optional `Func<VoteSnapshot, ReceiptKind, string>` override on `VoteCoordinator.Start`
  (production can inject a localized formatter).

---

## 4. Codebase Map

### Directory structure (relevant subset)

```
slay-the-streamer-2/
├── README.md
├── build.ps1                          # Plan A build+test script (Plan B will extend)
├── docs/superpowers/
│   ├── specs/
│   │   ├── 2026-05-08-ti-layer-design-v2.md       # Plan A design spec (settled)
│   │   ├── 2026-05-08-plan-b-prep-smoke-test-design.md   # ← THE SPEC UNDER REVIEW
│   │   └── 2026-05-08-plan-b-prep-smoke-test-design-CONTEXT.md  # ← THIS DOC
│   └── plans/
│       └── 2026-05-08-ti-core-implementation.md   # Plan A's executed plan
├── notes/
│   ├── 02-original-mod-feature-inventory.md
│   ├── 03-sts2-modding-api.md         # Modding contract (just updated for stable drift)
│   ├── 04-abstract-model-hook-surface.md # AbstractModel hooks (drift updated)
│   ├── 05-build-pipeline.md
│   └── 06-followups-and-deferred.md   # Open items list, includes #6 and #7
├── src/                               # the mod (currently TI library only)
│   ├── slay_the_streamer_2.csproj    # Godot.NET.Sdk/4.5.1
│   ├── slay_the_streamer_2.json      # mod manifest
│   ├── project.godot                 # Godot project config
│   ├── icon.svg                      # Godot icon
│   ├── ModEntry.cs                   # placeholder; spec REPLACES with [ModInitializer]
│   ├── sts2.dll                      # game ref, refreshed by build.ps1
│   └── Ti/                           # the TI core library
│       ├── Internal/                 # IClock, ITimerScheduler, IMainThreadDispatcher, TiLog, fakes
│       ├── Chat/                     # ChatMessage, IChatService, FakeChatService, etc.
│       │   └── Internal/             # TwitchIrcParser (pure), OutgoingMessageQueue
│       └── Voting/                   # VoteSession, VoteCoordinator, Voter, EnglishReceipts, etc.
└── tests/                            # xUnit tests against src/Ti
    ├── slay_the_streamer_2.tests.csproj
    ├── Internal/                     # tests for clock/scheduler/dispatcher/log
    ├── Chat/Internal/                # tests for parser + queue
    └── Voting/                       # tests for VoteSession + Coordinator + Voter + Receipts
```

`decompiled/sts2/` and `references/` are gitignored (regenerable).

### Lines of code (rough scale)

- `src/Ti/`: ~1,200 lines across 29 .cs files.
- `tests/`: ~1,640 lines across ~15 test classes, 142 tests.
- The mod itself currently has only a placeholder `ModEntry.cs` (5 lines)
  — the spec under review fills it in.

### Files most relevant to the spec

| File | Why it matters |
|---|---|
| `src/Ti/Internal/IMainThreadDispatcher.cs` | Defines the dispatcher contract Plan B's `GodotMainThreadDispatcher` must implement. **Has `Post(Action)` AND `Task DrainAsync()` methods** — the spec's component snippet only shows `Post`. |
| `src/Ti/Internal/ImmediateDispatcher.cs` | Test-only dispatcher (synchronous). Reference impl. |
| `src/Ti/Voting/Voter.cs` | Static facade Plan B `[ModInitializer]` configures via `Voter.Default = coord`. |
| `src/Ti/Voting/VoteCoordinator.cs` | Constructor takes `(IChatService, IClock, ITimerScheduler, IMainThreadDispatcher, Random?)`. |
| `src/Ti/Voting/VoteSession.cs` | Has `AwaitWinnerAsync` (the deadlock-suspect method). Uses `_winnerTcs` with `RunContinuationsAsynchronously`. |
| `src/Ti/Chat/FakeChatService.cs` | Has `Inject(ChatMessage msg)` and `SimulateState(...)`; its `_sent` list is exposed via `SentMessages` property (`AsReadOnly()` post-hardening). |
| `src/Ti/Chat/ChatMessage.cs` | Positional record: `(UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip)`. |
| `src/ModEntry.cs` | Currently a 5-line placeholder; the spec replaces this. |
| `references/STS2FirstMod/NewScript.cs` | The reference modding pattern (other StS2 mod author's `[ModInitializer]` example). |

---

## 5. Relevant Existing Patterns & Conventions

### Coding conventions

- C# 12, file-scoped namespaces, `Nullable` enabled.
- Plan A code uses **internal constructors** for types that should only be
  instantiated via factories (`VoteOption`, `VoteSession`).
- Em-dashes (—, U+2014) are used intentionally in spec strings and commit
  messages; preserved through formatting.
- 0-indexed vote options (`#0, #1, ...`) — matches the StS1 mod
  convention and Surfinite's preference.
- File-per-type, kept small. The largest file (`VoteSession.cs`) is ~270
  lines after Plan A's full feature set.
- All commits follow `plan-a/X.Y: <subject>` prefix and end with a
  `Co-Authored-By` trailer.

### Testing strategy

- **xUnit, deterministic fakes everywhere.** No real time, no real network.
- TDD for engine logic: write failing test → implement → confirm green →
  commit. Every behaviour change is preceded by a failing test.
- `FakeClock` + `FakeTimerScheduler` + `ImmediateDispatcher` form the
  deterministic timing harness. Tests explicitly `Scheduler.Advance(...)`
  to fire timers.
- Static-state isolation via `[Collection("TiLog.Sink")]` (multi-class
  parallelism guard for the static `TiLog.Sink` setter).
- 142 tests pass; Plan A's exit criterion was 128 tests but reality drifted
  upward as edge-case tests landed.

### Logging

- `TiLog` is a static helper with three log levels (`Info`, `Warn`,
  `Error`). It exposes a settable `Sink` property:
  `Action<LogLevel, string, Exception?>`.
- Default sink is a no-op; tests assign a capturing list; production (Plan
  B) will assign a passthrough to `MegaCrit.Sts2.Core.Logging.Log`.
- Verified thread-safe: `Logger` (the game's logger that backs `Log`)
  holds a `static readonly object _lockObj` around its print + callback.
  Documented in `notes/03`.

### Configuration & secrets

- No config system yet. Streamer-facing settings (oauth token, channel
  name) are deliberately deferred — they'll need a settings UI in Plan B+.
- The `ChatCredentials` record is constructed inline for tests; production
  source is unsettled.

### Patterns the spec must respect

- **Dispatcher contract** — `IMainThreadDispatcher.Post(Action)` is the
  hop point for marshalling timer callbacks onto the main thread.
- **Voter facade** — Plan B integrates by setting `Voter.Default`, not by
  constructing a parallel coordinator system.
- **TiLog passthrough** — Production logging routes through TiLog's Sink.
- **Manifest format** — `slay_the_streamer_2.json` is correct for v0.1
  (mod id `slay_the_streamer_2`, has_dll: true, affects_gameplay: true).

---

## 6. Current State & Known Issues

### What works today

- 142/142 unit tests pass on `dotnet test`.
- `pwsh -File build.ps1` reports "Plan A build + test cycle: OK".
- Working tree is clean as of commit `53e8ad6` (the spec being reviewed).

### Known issues / fragile areas

The spec under review explicitly addresses two unverified architectural
assumptions:

1. **Godot autoload registration from a runtime-loaded mod assembly.**
   `AddAutoloadSingleton` requires a class reference resolvable at engine
   boot, which a runtime-loaded mod can't provide. The candidate runtime
   alternatives are `Engine.RegisterSingleton(name, instance)` plus
   `Engine.GetMainLoop().Root.AddChild(node)`, OR plain `Root.AddChild`
   alone. **Neither is verified.**
2. **Harmony prefix + `await AwaitWinnerAsync()` deadlock risk.** The
   intended Plan B pattern is for a Harmony prefix on a game method to
   `await Voter.Start(...).AwaitWinnerAsync()`. The TCS uses
   `RunContinuationsAsynchronously` to avoid forcing continuations onto
   the main thread, but this hasn't been validated against Godot's
   `CallDeferred` dispatcher under realistic conditions.

### Recent significant changes

- **Plan A finalized and hardened.** Final commit on the Plan A path is
  `990a6ef` — a 13-fix consolidated hardening pass addressing concerns
  surfaced by per-task and final-review subagent reviewers.
- **Stable-branch drift verified.** Surfinite was on Steam beta during
  Plan A development. Switched to stable on 2026-05-08; re-decompiled and
  diffed. The Modding contract (`Mod`, `ModInitializerAttribute`,
  `ModManifest`, `Logger`) is byte-identical between branches. AbstractModel
  hooks have signature drift (mostly forward — beta is the newer dev
  branch, will become stable on next patch). `notes/03+04` updated.
- **Spec under review committed** as `53e8ad6` on `main`.

### Anything that might affect feasibility

- Surfinite is on **Godot 4.5.1 Mono**. Some `Engine.RegisterSingleton`
  signatures vary across Godot versions; the spec's snippet may need
  adaptation if 4.5.1's API differs from what's assumed.
- The game ships its own `0Harmony.dll`. We must reference the same one
  (not a NuGet copy) to avoid type-load conflicts at runtime.
- `MegaCrit.Sts2.Core.Modding.ModManager` only auto-runs `Harmony.PatchAll`
  if the assembly does NOT have a `[ModInitializer]` attribute. Since this
  spec uses `[ModInitializer]`, the `Init` method must call
  `new Harmony(...).PatchAll(Assembly.GetExecutingAssembly())` explicitly.
  **The spec includes this; reviewers should verify.**

---

## 7. Context Specific to the Plan/Spec

### What the spec touches

- **Replaces** `src/ModEntry.cs` (currently a placeholder) with a real
  `[ModInitializer]` implementation.
- **Adds** a new `src/Godot/` directory containing two files:
  `DispatcherAutoload.cs` (a Godot Node) and `GodotMainThreadDispatcher.cs`
  (an `IMainThreadDispatcher` impl).
- **Adds** a new `src/Smoke/` directory (disposable post-validation):
  `SmokeRunner.cs` and `SmokeMainMenuPatch.cs`.
- **Modifies** `src/slay_the_streamer_2.csproj` to reference `0Harmony.dll`.
- **Modifies** `build.ps1` to refresh `0Harmony.dll`, build Release config,
  and assemble `dist/slay_the_streamer_2/`.
- **Adds** a new `install.ps1` to copy `dist/` to the game's mods folder.

### Prior attempts / rejected approaches

- The original Plan A spec considered `AddAutoloadSingleton` directly in
  `[ModInitializer]`. It was deferred to "Plan B prep" precisely because
  runtime-mod assemblies can't satisfy `AddAutoloadSingleton`'s class-by-name
  contract — hence the runtime alternatives the spec proposes.
- Heartbeat-based reconnect logic was *removed* from the v2.2 spec rollback
  (in Plan A) because TwitchIrcChatService already has reconnection at the
  IRC layer; redundant.
- The "TI extraction goal" considered putting `Ti/` in a separate assembly
  during Plan A. Decision: keep in same assembly for v0.1; lift later as a
  file-move. The seams (interfaces, DI surfaces) are already clean enough
  for that to be a straightforward refactor.

### Dependencies & integrations

- **Godot 4.5.1 Mono runtime** — the spec's `DispatcherAutoload : Node`
  uses `CallDeferred(MethodName.Run, Callable.From(action))`. Godot's
  source generator produces the `MethodName` constants.
- **`0Harmony.dll`** (HarmonyLib) — referenced from the game install with
  `<Private>false</Private>` to avoid double-loading.
- **`sts2.dll`** — referenced for `MegaCrit.Sts2.Core.Modding.*`,
  `MegaCrit.Sts2.Core.Logging.Log`, and `MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu`
  (the Harmony patch target).
- **Game's mods folder**: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\<id>\`.

### Performance / scale / security

- **No security implications** for this smoke test (no network, no auth,
  no user data).
- **Performance**: the smoke runs once, completes within ~3-6 seconds.
- **Failure modes are benign**: if either smoke deadlocks, the game itself
  may hang; the streamer would have to force-quit. Not a destructive
  failure, but worth noting for the streamer doing the test.

### Known gap in the spec

The `IMainThreadDispatcher` interface has TWO methods: `Post(Action)` and
`Task DrainAsync()`. The spec's `GodotMainThreadDispatcher` snippet only
shows `Post`. **The implementation must also provide `DrainAsync()`** —
likely `Task.CompletedTask` for a `CallDeferred`-based dispatcher (since
"drain" semantics for a frame-deferred queue are ambiguous: do you wait
for *currently-queued* deferred calls, or also for those queued during the
drain?). Reviewers should weigh in.

---

## 8. Scope Boundaries

### Out of scope (deliberately)

- **Real Twitch IRC connection.** `FakeChatService` only. The actual
  `TwitchIrcChatService` is Plan B Phase 1 work.
- **Player-input substitution.** The smoke just logs the winner; it does
  NOT replace any player input. The actual "vote replaces click"
  mechanics are Plan B's core hooks.
- **Card / event / map / shop / boss / Neow Harmony patches.** None. The
  smoke patches `NMainMenu._Ready`, which is only chosen because it's a
  reliably-fired benign target.
- **UI prompts, sound, anything visible to the streamer.** Log lines via
  `MegaCrit.Sts2.Core.Logging.Log` only.
- **Configurable vote labels / durations.** Hardcoded for the smoke
  (`["A","B","C"]`, 3 seconds).
- **Settings UI / oauth onboarding.** Punted to Plan B+.

### Fixed and non-negotiable

- **Mod ID is `slay_the_streamer_2`.** Don't suggest renames.
- **Single-coordinator design.** `Voter.Default` is the production
  pattern; multi-coordinator is for v0.2+ (multiplayer / multi-channel).
- **`[ModInitializer]` is the entry point.** The game's auto-PatchAll
  fallback (no `[ModInitializer]`, just `[HarmonyPatch]` attributes) is
  insufficient for our mod because we need explicit setup for the
  dispatcher and `Voter.Default`.
- **TI core stays BCL-only.** `src/Ti/` references nothing from Godot or
  MegaCrit; that boundary is load-bearing for the TI extraction goal.
- **`RunContinuationsAsynchronously` on the winner TCS.** Plan A's design
  decision; smoke validates it works with Godot dispatcher.

### Trade-offs accepted deliberately

- **Smoke A and Smoke B both inject votes via `FakeChatService.Inject`.**
  This bypasses the IRC parser and the outgoing queue; it tests only the
  vote-engine + dispatcher path. That's sufficient for the autoload and
  deadlock concerns; the parser and queue are independently tested in 26
  Plan A unit tests.
- **No retry / resilience in the smoke.** If the smoke fails, the
  diagnosis is in the log and the user reports back; we don't try to
  recover automatically.
- **Smoke B uses `Interlocked.Exchange` instead of removing the patch
  after first fire.** Keeping the patch installed is simpler; the guard
  is cheap.

---

## 9. Success Criteria

### Observable outcomes

The streamer installs the mod, launches the game, and observes the
StS2 log file (`%APPDATA%/Godot/app_userdata/Slay the Spire 2/...`).

**Both smokes succeed:**
- Within ~3-6 seconds of game launch: `[smoke-A] starting...` and
  `[smoke-A] winner=0 (expected 0)`.
- Within ~3-6 seconds of reaching the main menu: `[smoke-B] starting (from
  Harmony prefix on NMainMenu._Ready)...` and `[smoke-B] winner=0 (expected 0)`.

**Either smoke fails:**
- Missing log lines, exceptions, hung game, or `winner != 0`.
- Detailed log output captured in `notes/06`, follow-up investigation task
  filed, Plan B writing blocked until resolved.

### Acceptance bar

- **Both smokes green** → spec succeeded; delete disposable scaffolding;
  write Plan B with confidence in the architecture.
- **Smoke A green, B silent or hangs** → architectural pivot needed for
  Harmony patterns before Plan B.
- **Smoke A silent or hangs** → autoload mechanism wrong; investigate
  alternative attachment before any Plan B work.

### Quality bar

- The permanent files (`DispatcherAutoload.cs`, `GodotMainThreadDispatcher.cs`,
  the real `ModEntry.cs`) must be of Plan-B-permanent quality: no shortcuts
  the smoke tolerates that production wouldn't.
- The disposable files (`Smoke/`) must be obviously disposable (folder name,
  file headers if needed) so removal is unambiguous.

---

## 10. Key Questions for Reviewers

These are areas where reviewer input would be most valuable beyond the
general review:

1. **Autoload registration approach.** The spec proposes
   `Engine.GetMainLoop().Root.AddChild(autoload); Engine.RegisterSingleton("DispatcherAutoload", autoload);`
   as the primary path, with plain `Root.AddChild` as fallback. Is there
   a more idiomatic Godot 4.5 pattern? Specifically: does `Engine.RegisterSingleton`
   have any side effects that affect a `Node` instance's frame-tick behavior?
   Will `Root.AddChild` alone cause `_Process` and `CallDeferred` to fire as
   expected for a mod-introduced node?

2. **`IMainThreadDispatcher.DrainAsync()` semantics for a `CallDeferred`-based
   dispatcher.** The interface has two methods; the spec's snippet only
   implements `Post`. What should `DrainAsync` do for a Godot dispatcher?
   Options: (a) `Task.CompletedTask` (no-op, document that it doesn't drain
   for frame-deferred), (b) wait one frame via `await ToSignal(autoload, "_process")`,
   (c) something more elaborate. Plan A's tests use `ImmediateDispatcher`
   where DrainAsync is a no-op; nothing currently calls DrainAsync from
   non-test code.

3. **Harmony prefix shape for the Smoke B test.** The spec uses
   `static void Prefix()` (synchronous void) and fire-and-forgets the async
   smoke runner via `_ = SmokeRunner.RunSmokeB(...)`. This validates that
   *fire-and-forget* await-from-prefix doesn't deadlock — but Plan B's real
   pattern will need the prefix to *block* until the vote completes (so it
   can substitute the player's input). Does Smoke B as designed actually
   validate the realistic Plan B pattern, or does it only validate a
   fire-and-forget variant? If the latter, what's the smallest additional
   smoke (Smoke C?) that would validate the blocking pattern?

4. **Failure observability.** If Smoke B never logs because the prefix
   wasn't applied (e.g., `Harmony.PatchAll` didn't pick it up, or `NMainMenu._Ready`
   has a signature variant), how does the streamer distinguish that from
   "I haven't reached the main menu yet"? Should `ModEntry.Init()` log a
   diagnostic line listing the patch targets that were successfully applied?

5. **Cleanup discipline.** The "disposable" files (`Smoke/`) and
   `ModEntry.SmokeChat` static field will be deleted post-success. Is the
   delineation clear enough that future-Surfinite (or a contributor)
   doesn't accidentally leave smoke residue in production? Should the
   files have explicit `// SMOKE-TEST: DELETE AFTER VALIDATION` headers?

---

## 11. Glossary / Domain Terms

| Term | Meaning |
|---|---|
| **TI core** | Twitch-integration core. The game-agnostic vote engine + IRC parser + outgoing queue built in Plan A. Lives in `src/Ti/`. |
| **TI extraction goal** | The eventual lift of `src/Ti/` into a separate base-mod assembly that other mods could reuse. Plan A's seams (interfaces, DI) are designed for this. |
| **Plan A / Plan B / Plan C** | Phase decomposition. A = TI core (done). B = Godot integration + real IRC client. C = IRC fixture-generator tool + corpus. |
| **`[ModInitializer]`** | The StS2 modding API attribute applied to a static class to declare it as a mod entry point. The named static method runs once at game startup. |
| **`Voter.Default`** | Plan A's static facade. A `VoteCoordinator?` field set once at startup; Harmony-patched call sites use `Voter.Start(...)` to reach it. |
| **`AwaitWinnerAsync`** | `VoteSession` method returning `Task<int>` that completes with the winning option index when the vote closes. Uses `RunContinuationsAsynchronously` so continuations don't run on the main thread. |
| **Smoke A / Smoke B** | The two scenarios in this spec. A fires from `[ModInitializer]`; B fires from a Harmony prefix on `NMainMenu._Ready`. |
| **`DispatcherAutoload`** | Spec-introduced Godot Node that exposes `Post(Action)` via `CallDeferred`. The hop mechanism for marshalling timer callbacks onto the Godot main thread. |
| **`FakeChatService`** | Plan A's in-memory `IChatService` impl for tests. Has `Inject(ChatMessage)` to deliver messages and `SimulateState(...)` to drive connection state changes. The smoke uses it instead of a real Twitch connection. |
| **`NMainMenu`** | StS2's main menu screen class (`MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu`). `_Ready` is a Godot lifecycle method called when the screen is added to the scene tree. |
| **CallDeferred** | Godot mechanism for queuing a method call to run on the next frame's idle period (main thread). The standard cross-thread hop in Godot 4.x. |
| **Harmony / `Harmony.PatchAll`** | HarmonyLib runtime method patching. `PatchAll` scans an assembly for `[HarmonyPatch]` types and applies them. The game ships `0Harmony.dll` natively. |
| **stable / beta branch** | Steam branches for Slay the Spire 2. Surfinite was on beta during Plan A; switched to stable for Plan B. |
| **0-indexed options** | Vote command convention: viewers type `#0`, `#1`, `#2` (not `#1`, `#2`, `#3`). Inherited from the StS1 mod. |
