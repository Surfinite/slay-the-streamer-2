# Meta-review — Plan B prep smoke test design

**Source spec:** `docs/superpowers/specs/2026-05-08-plan-b-prep-smoke-test-design.md` (commit `53e8ad6`)
**Updated spec:** `docs/superpowers/specs/2026-05-08-plan-b-prep-smoke-test-design-v2.md`
**Reviewers:** 6 (OwlAlpha, DeepSeekV4, GPT5.5, Gemini3.1Pro, Gemma4, Opus4.7_web)

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus | Unique Insight |
|---|---|---|---|
| **OwlAlpha** | Mixed-critical | Smoke B blocking gap, DrainAsync, fallback bugs, polish | Fairly comprehensive; first reviewer's pass through the spec |
| **DeepSeekV4** | Mixed-critical | Smoke B, null fallback, race, install permissions | **Catches Smoke A vs Smoke B race condition** (single-session VoteCoordinator) |
| **GPT5.5** | Strongly critical | Smoke B, full architectural rethink, ConcurrentQueue dispatcher | **`MainLoop` vs `SceneTree` cast** — caught a real compile bug none of the others spotted; thread-ID logging idea |
| **Gemini3.1Pro** | Mixed-critical | Smoke B, DrainAsync, lifecycle timing | Suggests `_EnterTree` patching as alternative attachment point if main loop is null |
| **Gemma4** | Mixed-critical | Smoke B, DrainAsync (compact) | Tightest review — surgical, no spread |
| **Opus4.7_web** | Mixed-critical | Smoke B (extensive), `Prepare()` patch validator, version pinning | `Prepare()` method on Harmony patch class to fail loudly if target not found |

All six reviews are **mixed-critical**. None recommend rejecting the spec; all recommend specific fixes plus the same headline Smoke C addition.

---

## A.2 — Consensus Points (2+ reviewers)

Ranked by reviewer count.

### 6/6 — UNIVERSAL CONSENSUS

**1. Smoke B does not validate the blocking-await pattern that Plan B actually needs.**
This is the single biggest issue. All 6 reviewers independently concluded that the fire-and-forget pattern (`_ = SmokeRunner.RunSmokeB(...)`) only validates that Harmony can schedule a background task — not that a Harmony prefix can safely block on `AwaitWinnerAsync` on the Godot main thread. Without a blocking smoke (commonly called "Smoke C"), the spec ships under a misleading banner.

**2. `IMainThreadDispatcher.DrainAsync()` is missing from `GodotMainThreadDispatcher`.**
Confirmed: `src/Ti/Internal/IMainThreadDispatcher.cs` declares both `Post(Action)` and `Task DrainAsync()` as regular interface methods (no default implementations). The spec's snippet only shows `Post`. This is a **compile failure**, not a design question.

### 5/6

**3. The `Engine.GetMainLoop()` null fallback is broken.** (OwlAlpha, DeepSeek, GPT5.5, Gemini, Opus)
The catch block calls `Engine.GetMainLoop()` again without a null check — if the original failure was from a null main loop, the fallback throws the same NRE.

**4. Need to log Harmony patch results after `PatchAll`.** (DeepSeek, GPT5.5, Gemini, Gemma4, Opus)
If `PatchAll` silently applies zero patches (signature mismatch, type filtering, etc.), the streamer can't distinguish "patch never applied" from "I haven't reached the main menu yet." `harmony.GetPatchedMethods()` exposes this; it's three log lines.

### 4/6

**5. `registeredSingleton` is set but never read.** (OwlAlpha, DeepSeek, GPT5.5, Opus)
Cosmetic but a clean fix.

**6. `SimulateMessage` vs `Inject` terminology drift.** (OwlAlpha, GPT5.5, Opus, multiple)
The spec's prose says `SimulateMessage(...)` in flow diagrams; the actual API is `Inject(ChatMessage)`. Same fact called out 4 times.

**7. `ConnectAsync(...).GetAwaiter().GetResult()` is an anti-pattern to imprint.** (DeepSeek, GPT5.5, Opus, OwlAlpha-implicit)
Safe today because `FakeChatService.ConnectAsync` returns a completed task, but this is the *exact* sync-over-async pattern the smoke is meant to rule out for production. Either guard with comment or refactor.

### 3/6

**8. `MethodName.Run` for private method may not be source-generated.** (OwlAlpha, DeepSeek, GPT5.5)
**Validated against Godot 4.5.1 source generator behavior: this concern is largely unfounded.** Godot's source generator emits `MethodName` constants for *all* methods in a partial Godot-derived class, regardless of visibility. (See `Godot.SourceGenerators` 4.5.1 — emits for any method, including private.) However, if the reviewer concern is wrong, the alternative (`CallDeferred("Run", ...)` with a string literal) is still valid and more portable across Godot versions; recommended as a defense-in-depth.

**9. `// SMOKE-TEST: DELETE AFTER VALIDATION` headers on disposable files.** (OwlAlpha, DeepSeek, Gemini, Opus)
Spec calls this out as Section 10 question #5 but doesn't include in the design. Five-second fix.

### 2/6

**10. Smoke A and Smoke B race / overlap.** (DeepSeek, GPT5.5)
**Validated against Plan A code: this is real.** `VoteCoordinator` is single-session by design (Plan A Task 6.1's `Start_WhileOpen_Throws` test enforces it). If Smoke B fires while Smoke A is still running (player reaches main menu within 3 seconds of game launch — possible on a fast SSD), Smoke B's `Voter.Start(...)` throws `InvalidOperationException` and Smoke B silently fails.

**11. Make `DispatcherAutoload` more robust** (queue + drain) (GPT5.5, Gemini)
Two reviewers propose a `ConcurrentQueue<Action>` + `_Process` drain instead of `CallDeferred(MethodName.Run, Callable.From(...))`. **My pushback below — this is overengineering for the smoke.**

---

## A.3 — Outlier Points (1 reviewer only)

**GPT5.5 alone caught: `Engine.GetMainLoop()` returns `MainLoop`, not `SceneTree`. `.Root` is on `SceneTree`.**

**This is a real compile bug in the spec.** Verified against `decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/MainMenu/NMainMenu.cs:253` which uses the idiomatic `GetTree().Root` (correct pattern: `GetTree()` is a `Node` method returning `SceneTree`, and `Root` is a `SceneTree` property). The spec snippet `Engine.GetMainLoop().Root.AddChild(autoload)` won't compile in Godot 4.5.1 C# bindings — `MainLoop` doesn't have a `Root` property.

GPT5.5's recommended fix: `var tree = Engine.GetMainLoop() as SceneTree ?? throw ...`. This is the correct pattern. **Adopting.**

**GPT5.5 alone proposed: thread-ID logging at init + dispatcher drain time.**
This is a great diagnostic addition. Costs ~3 lines, surfaces "are dispatcher actions actually running on the main thread?" as observable evidence rather than implicit assumption. **Adopting.**

**Opus4.7_web alone proposed: `Prepare()` method on Harmony patch class.**
Returns `false` if the target method isn't found, with a log line. Distinguishes "patch never applied" from "I haven't reached the main menu yet" at patch-installation time rather than waiting for runtime evidence. **Adopting.**

**DeepSeek alone proposed: timeout watchdog around each smoke.**
If the smoke deadlocks, the streamer sees nothing — game just hangs. A watchdog `Task.Delay(15s)` that logs a timeout warning gives positive evidence of failure rather than ambiguous silence. **Adopting.**

**Gemini alone proposed: `_EnterTree` patching as alternative attachment if `Init()` runs before `MainLoop` is ready.**
Worth keeping as a Plan-B-future-work note but not actioning in v2. The spec's null-check + abort-with-clear-error path (per OwlAlpha/DeepSeek/Opus suggestions) is sufficient diagnostic.

**Opus4.7_web alone proposed: top-level `try/catch` around the entire `Init()` body.**
If anything in `Init` throws post-canary-log, the mod is half-loaded — `Voter.Default` may be null, autoload may exist without dispatcher pointing at it. Bounding the blast radius with a top-level catch + "mod load failed; subsequent game behavior unmodified" log is good hygiene. **Adopting.**

**DeepSeek alone proposed: install.ps1 admin permission check.**
**Pushing back.** Steam directories are writable by the user that installed Steam (default Windows 11 setup). Adding admin checks is paranoia for a hobbyist solo-dev workflow.

**GPT5.5 alone proposed: separate `FakeChatService` per smoke** to avoid shared-state bugs.
Resolved differently — by serializing Smoke A and B (DeepSeek's variant) we keep one chat instance and avoid the race. Two coordinators with one shared chat would still have race risk on the chat itself.

---

## A.4 — Category Breakdown

### 🏗️ Architecture & Design

- **Smoke C (blocking variant)** — universal. Real, adopting.
- **Make `DispatcherAutoload` queue-based** — GPT5.5, Gemini. **Pushing back partially:** the simple `CallDeferred(MethodName.Run, Callable.From(...))` is the standard Godot pattern and what we should test FIRST. The smoke's job is to find out if it works. If it fails, the queue+drain pattern is a known fallback. Adopting the queue pattern preemptively adds complexity and doesn't validate the simpler approach. Adding the queue alternative as a documented Plan B fallback in v2 but keeping the simple form for the smoke.
- **Plan B should avoid blocking entirely; use suspend-and-resume** — GPT5.5 (and implicitly Gemini). **Strong pushback:** This is exactly what the smoke is designed to TEST. Declaring "blocking won't work" before running the smoke is presumption. Plan A's `RunContinuationsAsynchronously` design is specifically intended to make blocking-await safe; if the smoke proves it isn't, then we redesign — but not before. Smoke C exists to give us evidence either way.
- **`SceneTree` cast required** — GPT5.5. Validated, real compile bug. Adopting.
- **Use `SynchronizationContext` instead of autoload Node** — GPT5.5, OwlAlpha alternative. Worth noting as future-work but out of scope for the smoke.

### ⚠️ Risks & Concerns

- **Null `MainLoop` at `Init()`** — multiple reviewers. Real risk; spec acknowledges. The fallback bug is a separate concrete issue. Adopting fix (cache + null-check + clear-error abort).
- **Smoke A vs Smoke B race** — DeepSeek, GPT5.5. Real (verified against `VoteCoordinator`'s single-session contract). Adopting fix (serialize Smoke B to wait for Smoke A's task to complete).
- **`ConnectAsync.GetResult()` imprinting anti-pattern** — multiple. Real concern for Plan B copy-paste. Adopting comment + Plan-B-pattern note.
- **Patch target may not resolve** — Opus, multiple. Real. Adopting `Prepare()` validator.

### 🗑️ Suggested Removals / Simplifications

- **Remove `registeredSingleton` unused variable** — 4 reviewers. Adopting.
- **Demote `Engine.RegisterSingleton` to optional, make `Root.AddChild` primary** — GPT5.5, Opus. Validated reasoning: `RegisterSingleton` doesn't add anything load-bearing for a `CallDeferred`-based dispatcher; `AddChild` is the actual mechanism. Adopting.
- **Drop "autoload" from class name; runtime-attached node, not editor autoload** — GPT5.5. Reasonable. **Pushing back:** the spec's `notes/06` task tracks "validate Godot autoload registration from a mod assembly" — keeping the name `DispatcherAutoload` keeps the connection to that historical concern. Renaming would make the mapping less clear. Sticking with the name but adding a class-level XML doc explaining "not an editor autoload".

### ➕ Suggested Additions / Features

- **Smoke C** — universal. Adopting.
- **Harmony patch logging after `PatchAll`** — 5 reviewers. Adopting.
- **Diagnostic log inside the prefix** — 4 reviewers. Adopting.
- **Patch `Prepare()` validator** — Opus. Adopting.
- **Watchdog timeout around each smoke** — DeepSeek. Adopting.
- **Thread-ID logging** — GPT5.5. Adopting.
- **Top-level `try/catch` in `Init`** — Opus. Adopting.
- **Smoke-test headers on disposable files** — 4 reviewers. Adopting.
- **Log file path hint in `Init()`** — DeepSeek. Adopting.
- **Add Smoke C row to success matrix** — implicit in "add Smoke C." Adopting.

### 🔄 Alternative Approaches

- **`SynchronizationContext` instead of autoload** — GPT5.5, OwlAlpha. Future-work note in v2.
- **Patch `_EnterTree` instead of `_Ready`** — Gemini, DeepSeek. Future-work note.
- **`SceneTree.CreateTimer` + signal as fallback** — Opus. Future-work note.
- **Pre-registered Godot project autoload (proxy)** — GPT5.5. Adds a project-config dependency; not adopting (the runtime path is what we're testing).
- **`AccessTools.Method` to resolve target before patching** — GPT5.5. This is what `Prepare()` does internally; adopting via `Prepare()`.

### ✅ Confirmed Good / Keep As-Is

- **Two-assumption framing** — universal acknowledgment.
- **Disposable vs permanent split** — universal acknowledgment.
- **Success matrix structure** — multiple reviewers called this out as the strongest part of the spec.
- **Deterministic `#0` injection + winner=0 expected** — multiple reviewers approved.
- **`<Private>false</Private>` on Harmony reference** — multiple approved.
- **`Interlocked.Exchange` re-entry guard** — multiple approved (one — OwlAlpha — said it's overkill for single-thread, but the others rightly noted the guard is conceptually appropriate even if `Interlocked` is overkill).
- **`try/catch` in `SmokeRunner.Run`** — universal approval.
- **Canary log line** in `Init()` — multiple approved.
- **Plan A's `RunContinuationsAsynchronously` design** — implicit in all reviews; nobody questioned the underlying TCS pattern itself.
- **Explicit `Harmony.PatchAll` call** — universal approval (handles the StS2 modding contract correctly).

### 🔧 Implementation Details & Nits

- **`Interlocked.Exchange` on a single-thread field** — OwlAlpha. **Pushing back partially:** OwlAlpha is technically correct that the prefix runs on the main thread only, so `Interlocked` is overkill. But the cost is zero (single instruction) and it documents intent ("if there were ever a multi-thread case, this guard handles it"). Sticking with `Interlocked` as a one-line decision.
- **Log string consistency** — GPT5.5. Adopting (canonical labels: `[smoke-A]`, `[smoke-B]`, `[smoke-C]`).
- **`MethodName.Run` private-method concern** — partially validated false (Godot source-gen emits MethodName for private). Defense-in-depth: switching to string literal `"Run"` for portability.
- **Build path fragility** (`.godot/mono/temp/bin/Release/`) — Opus. Adopting (use `dotnet publish` with explicit OutputPath).
- **`install.ps1` path echo** — GPT5.5. Adopting.
- **`Callable.From(action)` overload resolution** — GPT5.5. Defense-in-depth: explicit `Callable.From((Action)action)` cast.
- **`DispatcherAutoload` should catch action exceptions** — GPT5.5. Adopting (small block in the deferred call site).

### 📦 Dependencies & Integration

- **`build.ps1` parameterize game install path** — GPT5.5. Adopting.
- **`install.ps1` should validate mod loaded** (post-install hint) — DeepSeek. Adopting.
- **Add `uninstall.ps1` for symmetry** — Opus. Adopting.
- **Pin Harmony version in `build.ps1` log** — Opus. Adopting (one log line).

### 🔮 Future Considerations

- **`SynchronizationContext`-based dispatcher** as Plan-B fallback — GPT5.5, OwlAlpha. Adding to "future work" section.
- **`SceneTree.CreateTimer` + signal** as deferred-call alternative — Opus. Adding to "future work."
- **ConcurrentQueue dispatcher** as fallback — GPT5.5, Gemini. Adding to "future work."
- **`_EnterTree` patching** as alt attachment — Gemini, DeepSeek. Adding to "future work."

---

## A.5 — Conflicts & Contradictions

**Conflict 1: Smoke B should be removed vs. Smoke B should be kept (and Smoke C added).**
- GPT5.5 leans toward "rename Smoke B's claim down or replace with Smoke C."
- Most others lean toward "keep Smoke B (it does validate non-blocking schedule), add Smoke C for the blocking case."
- **Resolution:** Keep Smoke B with reframed scope ("validates non-blocking Harmony prefix scheduling"), add Smoke C for blocking. Both signals are useful; Smoke B + Smoke C green = total green; Smoke B green + Smoke C hung tells us exactly what failed.

**Conflict 2: `DrainAsync` semantics — `Task.CompletedTask` no-op vs. one-frame `ToSignal` vs. barrier-Post pattern.**
- Gemma4 and the spec author lean toward `Task.CompletedTask`.
- Opus and OwlAlpha suggest the same with explicit XML doc.
- GPT5.5 proposes a barrier-Post pattern: post a TCS-set action and await it (validates "all queued-at-call-time actions ran").
- Gemini suggests `await ToSignal(_autoload.GetTree(), SceneTree.SignalName.ProcessFrame)`.
- **Resolution:** Adopt the barrier-Post pattern (GPT5.5's). It's not much more code and gives observable "previously-posted actions have run" semantics, which is what most callers would expect from `DrainAsync`. Document that recursively-posted actions during drain are not awaited.

**Conflict 3: Should `DispatcherAutoload` be queue-based or `CallDeferred`-based?**
- GPT5.5 and Gemini argue queue-based is more robust.
- The spec uses `CallDeferred` directly, which is the standard Godot 4.x pattern.
- **Resolution:** Keep `CallDeferred` (simple, idiomatic, what we want to validate). Add queue-based as a documented Plan-B fallback. The smoke's job is to validate the simpler approach; if it fails, we have a known alternative.

**Conflict 4: Drop `Engine.RegisterSingleton` entirely vs. keep as optional.**
- GPT5.5 and Opus argue drop it; `AddChild` is the load-bearing op.
- Spec keeps both as primary + fallback.
- **Resolution:** Demote `RegisterSingleton` to optional instrumentation. Make `Root.AddChild` (after `SceneTree` cast) the primary path. Try `RegisterSingleton` after `AddChild` succeeds; log warn-and-continue on failure. This decouples the two concerns: "did the node attach?" vs "did the singleton registration work?".

---

## A.6 — Recommended Plan Changes

### Must-do (high consensus, real risks/bugs)

1. **Add Smoke C: blocking-await prefix.** [universal] The single biggest gap.
2. **Implement `DrainAsync()` on `GodotMainThreadDispatcher`.** [universal] Compile failure otherwise. Use barrier-Post pattern with XML-doc.
3. **Cast `Engine.GetMainLoop()` to `SceneTree` before accessing `Root`.** [GPT5.5, validated] Real compile bug.
4. **Fix the null-`MainLoop` fallback** to cache once and null-check explicitly. [5/6 reviewers]
5. **Log Harmony patch results** after `PatchAll`. [5/6 reviewers]
6. **Add `Prepare()` validator** to each Harmony patch class. [Opus]
7. **Diagnostic log inside the prefix** before fire-and-forget. [4/6 reviewers]
8. **Serialize Smoke B (and C) to wait for Smoke A's completion** (avoid `VoteCoordinator` single-session race). [DeepSeek, GPT5.5, validated]
9. **Fix `SimulateMessage` → `Inject` terminology drift** in Scope and Flow sections. [4/6 reviewers]
10. **Remove unused `registeredSingleton` variable.** [4/6 reviewers]
11. **Add `// SMOKE-TEST: DELETE AFTER VALIDATION` headers** to disposable files. [4/6 reviewers]
12. **Demote `RegisterSingleton` to optional instrumentation**, make `AddChild` the primary path. [GPT5.5, Opus]
13. **Top-level `try/catch` around `Init()` body** to bound mod-load blast radius. [Opus]
14. **Watchdog timeout** around each smoke (15s `Task.Delay`). [DeepSeek]
15. **Comment the `ConnectAsync.GetAwaiter().GetResult()` smoke-only pattern** explicitly so Plan B doesn't copy it. [3+ reviewers]

### Should-do (strong improvements)

16. **Add Smoke C row to success matrix.** [implicit consensus]
17. **Thread-ID logging** at init + dispatch points. [GPT5.5]
18. **Catch + log exceptions in `DispatcherAutoload`'s deferred run.** [GPT5.5]
19. **Use string literal `"Run"` instead of `MethodName.Run`** for portability. [3 reviewers, defense-in-depth]
20. **Use explicit `Callable.From((Action)action)` cast.** [GPT5.5, defense-in-depth]
21. **Switch build pipeline to `dotnet publish` with explicit `OutputPath`** to avoid `.godot/mono/temp/bin/` path drift. [Opus]
22. **Parameterize game install path** in `build.ps1` and `install.ps1`. [GPT5.5]
23. **`install.ps1` echoes source/destination paths.** [GPT5.5]
24. **Add `uninstall.ps1`** for symmetry. [Opus]
25. **Log file path hint** in `Init()` so streamer knows where to look. [DeepSeek]

### Consider (nice-to-have, presented as pick-list in v2)

See Optional Enhancements at the end of the v2 spec.

### Reject (with reason)

- **GPT5.5's "Plan B should abandon blocking entirely; use suspend-and-resume."** Pre-empts what the smoke is supposed to discover. Plan A's `RunContinuationsAsynchronously` was designed specifically to make blocking-await viable; the smoke's job is to verify, not assume. If Smoke C deadlocks, then we redesign — but not before.
- **GPT5.5's full ConcurrentQueue rewrite of `DispatcherAutoload`.** Overengineering for a smoke. Adding ConcurrentQueue + `_Process` drain is a real fallback, but the simpler `CallDeferred` pattern is the standard and what we want to test. Documenting the fallback in v2's "Future work" section.
- **GPT5.5's "rename `DispatcherAutoload` to `GodotDispatcherNode`."** Loses the connection to `notes/06` item #6 (autoload registration). Keeping the name with a class-level doc explaining the runtime-attachment.
- **DeepSeek's "install.ps1 admin permissions check."** Steam directories are user-writable by default on Windows 11. Paranoid for a hobbyist workflow.
- **DeepSeek's "Autoload node not removed after smoke."** Misunderstanding — `DispatcherAutoload` is permanent Plan-B scaffolding, not disposable.
- **OwlAlpha's "remove `Interlocked.Exchange`."** Technically correct (single-thread context), but the cost is zero and it documents intent. Sticking with `Interlocked`.
- **Multiple reviewers' "make `Run` method public/internal."** Validated false against Godot 4.5.1's source generator (emits `MethodName` for private methods). Switching to string literal as defense-in-depth handles the concern more portably.

---

## A.7 — What Stays

The following elements of the original spec were universally praised and should remain unchanged:

- **Two-assumption framing** tied to `notes/06` items #6 and #7.
- **Disposable vs permanent boundary** with `src/Smoke/` as a deletion unit.
- **Success matrix** as the diagnostic decision tree.
- **Deterministic `#0` injection** with winner=0 expectation as a third diagnostic signal.
- **`<Private>false</Private>` on the Harmony reference.**
- **`try/catch` in `SmokeRunner.Run`** with `Task` return (not `async void`).
- **Canary log line** as first thing in `Init()`.
- **Explicit `Harmony.PatchAll(Assembly.GetExecutingAssembly())`** call (handles the StS2 modding contract correctly).
- **Plan A's `RunContinuationsAsynchronously` TCS design** — nobody questioned this; Smoke C will validate it under the realistic blocking pattern.
- **Use of `FakeChatService`** to isolate the smoke from network/parsing/auth complexity.
- **3-second vote duration with 3 options** — appropriate for a smoke.

---

## My pushback layer (controller's editorial)

The user explicitly invited pushback. Here's where I diverge from reviewer consensus:

1. **Pushback on GPT5.5's "Plan B may need to avoid blocking entirely" thesis.** This pre-empts the smoke. The `RunContinuationsAsynchronously` design from Plan A is specifically there to enable blocking-await safely. The smoke is the validation gate, not a recommendation engine. Smoke C exists for this reason — let the evidence drive the architecture decision.

2. **Pushback on "rewrite `DispatcherAutoload` to use ConcurrentQueue + `_Process` drain."** This adds complexity that doesn't validate the simpler pattern. The smoke's job is to find out if `CallDeferred` works for our use case. If yes, ship it. If no, we have a documented fallback. Don't substitute the fallback for the test.

3. **Pushback on "remove `Engine.RegisterSingleton` entirely."** Adopted partially — demoted to optional — but I'm keeping the call because spec question #1 from the context doc explicitly asks reviewers to weigh in on its idiomatic-ness, and if it works, future code that wants `Engine.GetSingleton("DispatcherAutoload")` benefits. Cost is one line.

4. **Pushback on "install.ps1 admin permissions concern."** False positive on Windows 11 with default Steam install. Not worth complexity.

5. **Pushback on "MethodName.Run won't work for private."** Validated against Godot 4.5.1 source-generator — it emits MethodName for all methods including private. The reviewers were guessing; I have access to the actual SDK. Adopting string-literal `"Run"` as defense-in-depth (cheaper than version-checking the source generator), but the underlying concern is mostly unfounded.

6. **Pushback on Smoke A/B race "low probability."** Reviewers split on severity; I rate it higher than they did because Plan A's `Start_WhileOpen_Throws` test (`tests/Voting/VoteCoordinatorTests.cs`) directly verifies that the coordinator throws if a vote is already open. So if the player reaches main menu within 3s, Smoke B literally throws — not "may have issues," definitely throws. Adopting the serialization fix.

7. **Pushback on "tests CallDeferred not frame ticks" pedantry.** GPT5.5 wanted a separate `_Process` canary. We don't need one — `CallDeferred` only fires on a frame tick, so if the smoke completes, frame ticks were processed. The proposed canary is redundant evidence.

The reviewer pool's strongest catch was GPT5.5's `MainLoop`/`SceneTree` cast issue — that's the kind of compile-bug spot that no amount of context-doc detail makes obvious without familiarity with the Godot 4.x C# bindings. Worth highlighting as the meta-review's biggest single insight, alongside the universal Smoke C consensus.
