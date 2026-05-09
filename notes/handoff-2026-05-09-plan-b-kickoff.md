# Session handoff: kick off Plan B

Paste this verbatim as the first message in a fresh Claude Code session for `c:\Users\Surfinite\slay-the-streamer-2`.

---

# Continuation: slay-the-streamer-2 — kick off Plan B

You're starting Plan B for a Slay the Spire 2 Twitch chat-vote mod. Plan A (the dependency-free TI core library) is complete and Plan B prep (architectural smoke test) has finished with decisive results. Your job: brainstorm Plan B's scope, write its spec, run it through the meta-review workflow, then plan and execute.

## Read these first (in order)

1. **Auto-loaded memory** at `C:\Users\Surfinite\.claude\projects\c--Users-Surfinite-slay-the-streamer-2\memory\MEMORY.md` and the files it links to. Critical entries: user profile (Surfinite — solo hobbyist), pacing feedback (~20x faster than human-paced), meta-review workflow (crowd-source LLM reviews, code-blind context docs), communication preferences (don't tell him when to stop; politeness is genuine).
2. `notes/06-followups-and-deferred.md` — **especially the "Pre-Plan-B prep (resolved)" section**. It captures the smoke test's verdict: blocking-prefix-await deadlocks under Godot's main-thread sync context. **Plan B must use suspend-and-resume**, not blocking await.
3. `docs/superpowers/specs/2026-05-08-ti-layer-design-v2.md` — Plan A spec (settled v2.3). The TI core library you'll build on.
4. `docs/superpowers/specs/2026-05-08-plan-b-prep-smoke-test-design-v2.md` — the smoke spec (executed). Useful as a reference for `notes/06`'s findings.
5. `docs/superpowers/specs/META-REVIEW-2026-05-08-plan-b-prep-smoke-test-design.md` — the meta-review's predictions, validated by the smoke.
6. `src/ModEntry.cs` — the permanent skeleton ready for Plan B Phase 1 to extend. Read this carefully; understand each numbered section so you know what IS already wired vs what needs adding.
7. `notes/02-original-mod-feature-inventory.md` — feature scope of Tempus's StS1 mod, our reference for v0.1.
8. `notes/04-abstract-model-hook-surface.md` — per-decision Harmony-vs-AbstractModel recommendations for the 6 v0.1 votes.

## Current state

- **HEAD**: `4fa5a98` (`plan-b-prep/8.1: smoke succeeded with deadlock-confirmed; cleanup`).
- **Branch**: `main` (Surfinite explicitly authorised staying on main throughout; per-task commits with `plan-b/X.Y:` prefix are pre-authorised).
- **Tests**: 142/142 passing. `pwsh -File build.ps1` → `Plan B prep build cycle: OK`.
- **What's wired**: Godot dispatcher (`DispatcherAutoload` + `GodotMainThreadDispatcher`), `TiLog.Sink → MegaCrit.Sts2.Core.Logging.Log` passthrough, `Harmony.PatchAll` infrastructure (currently 0 patches), `[ModInitializer("Init")]` entry-point with diagnostic logging.
- **What's missing**: `TwitchIrcChatService` (the real IRC client), `Voter.Default` wiring (not set since smoke removal), all 6 Harmony patches, oauth credentials sourcing, any UX.

## What the smoke proved (architecture-defining)

The single most load-bearing finding from prep work:

> **Plan A's `RunContinuationsAsynchronously` on the winner TCS is insufficient.** Godot 4's C# bindings install a `SynchronizationContext` on the main thread that re-captures `await` continuations onto thread 1. A Harmony prefix that does `session.AwaitWinnerAsync().GetAwaiter().GetResult()` on the Godot main thread will deadlock, because the close timer's callback dispatches via `CallDeferred` which needs an idle frame on the (blocked) main thread.

**Plan B must therefore use suspend-and-resume** for every Harmony prefix:

```csharp
[HarmonyPatch(typeof(SomeDecisionScreen), nameof(OnPlayerPicked))]
internal static class CardRewardVotePatch {
    static bool Prefix(...originalArgs...) {
        _ = HandleVoteAsync(originalArgs);
        return false;   // skip original — we're hijacking
    }
    static async Task HandleVoteAsync(...) {
        var winner = await Voter.Start(...).AwaitWinnerAsync();
        ModEntry.Dispatcher.Post(() => InvokeWinnerChoice(winner));
    }
}
```

The prefix returns immediately, original is skipped, vote runs in background, winner's choice is invoked via the dispatcher when the vote completes. **No blocking thread 1, ever.** Document this in Plan B's spec as a hard architectural constraint.

## Resume protocol

1. Read the files listed above.
2. Invoke `superpowers:brainstorming`. Do NOT skip to writing-plans — Plan B has no spec yet, and skipping the spec stage on a project this size will cost more in rework than the brainstorm session takes.
3. Brainstorm should explore:
   - **Scope decomposition.** v0.1 likely needs: IRC client + ModEntry wiring + at-least-one Harmony patch end-to-end + oauth-source-of-truth UX (even if minimal). Recommend breaking Plan B into multiple sub-plans if needed (B.1 = IRC + ModEntry, B.2 = first Harmony patch, B.3 = remaining 5 patches, B.4 = settings UX). Each sub-plan = its own spec → plan → implement cycle.
   - **Patch target selection.** Of the 6 v0.1 votes (card reward, Neow, event choice, boss reward, shop purchase, map path), Neow is probably simplest as the first end-to-end target.
   - **Oauth/credentials story.** Streamer needs to provide their bot's oauth token somehow. Settings UI? Config file in mods folder? Environment variable? Decide trade-offs.
   - **Reconnect/disconnect UX.** Plan A tracks disconnect gap; Plan B surfaces it to the streamer (chat receipt + log? on-screen overlay? neither for v0.1?).
4. Write the spec to `docs/superpowers/specs/YYYY-MM-DD-plan-b-<scope>-design.md`.
5. Run `/document-context` to produce the reviewer-ready context document.
6. **Pause for crowd-source LLM review.** Surfinite uses t3.chat to feed the spec + context to multiple frontier models (DeepSeek, GPT, Gemini, Claude Opus, Gemma, etc.) and collects reviews into a directory. Wait for him to do this.
7. Run `/meta-review` once reviews are in.
8. Apply Must-do + Should-do auto-fixes; offer Optional Enhancements as pick-list.
9. Run `superpowers:writing-plans` to produce the implementation plan.
10. Execute via `superpowers:subagent-driven-development` (Surfinite's preferred execution flow; matches Plan A's pattern).

## Conventions and gotchas (do not repeat-discover these)

- **Godot ambiguities** in `ModEntry.cs`: `Environment` (vs `System.Environment`), `LogLevel` (vs `MegaCrit.Sts2.Core.Logging.LogLevel`). Already fully-qualified at the necessary call sites; don't introduce shorter forms.
- **`Engine.GetMainLoop()` returns `MainLoop`, not `SceneTree`.** Cast with `as SceneTree` and null-check before using `.Root`. Direct `.Root.AddChild(...)` errors during `[ModInitializer]` because `NGame._EnterTree` is busy; use `tree.Root.CallDeferred("add_child", autoload)`.
- **`SystemTimerScheduler` has no constructor** — `new SystemTimerScheduler()`, not `new SystemTimerScheduler(clock)`.
- **`FakeChatService.Inject(ChatMessage)`** is the message-injection API (no `SimulateMessage` method).
- **`ChatMessage` ctor positional order**: `(UserId, Login, DisplayName, Text, ReceivedAt, IsSubscriber, IsModerator, IsVip)`.
- **Em-dashes** (—, U+2014) in commit messages and spec strings — preserve verbatim.
- **0-indexed options** (`#0, #1, #2, ...`).
- **Use the PowerShell tool** (not Bash) for `git commit -m @'...'@` invocations — bash mangles the `@` literal.
- **Harmony's `Prepare(MethodBase original)` is called twice**: class-level with `original=null` ("should I process this class?"), then once per resolved target. **Return `true` on `null`**, otherwise the entire patch class is skipped.
- **AbstractModel signature drift between beta and stable** — beta is the *newer* dev branch. Many `PlayerChoiceContext` parameters and 4 new auto-play-phase callbacks exist only in beta; will land in stable on the next game patch. See `notes/04` callout box.

## User preferences (from memory but worth surfacing)

- **Don't tell him when to stop or pause.** He decides when to stop.
- **Apply own judgment, push back on reviewers when warranted** — don't blindly accept reviewer suggestions.
- **Pacing**: AI-paired sessions move ~20x faster than traditional pace. User comprehension/reply latency is the actual throttle, not your output rate.
- **Per-task commits are pre-authorised** on `main`; commit each task without asking.
- **Memory writes**: only save load-bearing facts not derivable from code/notes. Avoid duplicating what's already in spec/plan/notes.

## Don'ts

- Don't switch git branches without asking.
- Don't switch Steam branch back to beta.
- Don't re-decompile sts2.dll unless game is patched.
- Don't push to GitHub unless explicitly asked.
- Don't merge or finalise — Plan B is the start of a new arc.
- Don't reintroduce the smoke files (deleted in `4fa5a98`); the dispatcher is permanently wired in `src/ModEntry.cs`.
- Don't try to make blocking-prefix-await work; it doesn't, and the smoke proved it. Suspend-and-resume only.

## What "done" looks like

For Plan B v0.1: streamer installs the mod, configures their bot's oauth token, launches StS2, starts a run, and Twitch chat votes on Neow blessing / card rewards / event choices / boss rewards / shop purchases / map paths. Receipts post to chat at the configured cadence. Disconnect gaps are mentioned in close receipts. Mod handles a Twitch IRC reconnect cleanly mid-run.

When that works in-game (operator-validated, like the smoke was), Plan B v0.1 is done. Plan C (IRC fixture-generator tool, sketched only) is post-v0.1.

---

Ready? Read the files listed above and then invoke `superpowers:brainstorming`.
