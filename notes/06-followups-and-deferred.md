# Follow-ups and Deferred Items

Living list of things flagged during sessions that need attention later. Updated as discovered; ticked off as resolved. Most recent items at the top of each section.

---

## Plan B.2.1 spike findings (2026-05-10)

### Harmony patchability of Godot lifecycle methods on NRewardsScreen

- `_Ready` postfix fires: <RUNTIME-VERIFY: needs operator playthrough — observe godot.log for `[spike] NRewardsScreen._Ready fired` after a combat-rewards screen appears>
- `_ExitTree` postfix fires: <RUNTIME-VERIFY: needs operator playthrough — observe godot.log for `[spike] NRewardsScreen._ExitTree fired` after dismissing a rewards screen>
  - **Spike-author note**: `NRewardsScreen` does NOT declare `_ExitTree()` (verified in decompile — absent from both `MethodName` registry and `GetGodotMethodList`). Harmony will attempt to patch the inherited `Control._ExitTree` (or `Node._ExitTree`); whether that intercepts the actual call on subclassed instances is the question. If the postfix never fires in-game, fallback chain: (a) patch `AfterOverlayClosed()` (declared on NRewardsScreen, called when overlay teardown runs `this.QueueFreeSafely()`; see lines 460–480 of decompile) — this is *probably the right hook anyway* for skip-gate teardown, since it fires after the rewards UI is dismissed but before the node is freed; (b) patch `Control._ExitTree` if AfterOverlayClosed proves insufficient; (c) hold a `WeakReference<NRewardsScreen>` and poll `IsInstanceValid` from a Godot tick.
- Fallback if `_Ready` doesn't patch: try `_EnterTree` postfix (also inherited), or patch the first NRewardsScreen-declared method that runs after node setup — e.g., `AfterOverlayShown()` runs reliably after the screen becomes visible (line 494) and is a defensible alternative.

### Reflected sts2.dll members — B.2.1 dependency surface

CardRewardVotePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` (type — public class, namespace confirmed)
- `NCardRewardSelectionScreen.SelectCard(NCardHolder)` — `private void SelectCard(NCardHolder cardHolder)` (line 255 of decompile; `NCardHolder` is `MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder`, abstract Control subclass)
- `NCardRewardSelectionScreen._options` — `private IReadOnlyList<CardCreationResult> _options` (line 98; `CardCreationResult` is in `MegaCrit.Sts2.Core.Entities.Cards`)
- Card-holder collection accessor: `private Control _cardRow` (line 96). The holders are children of `_cardRow` and are concretely `NGridCardHolder` (extends `NCardHolder`). Enumeration pattern verified in `RefreshOptions` (line 175): `_cardRow.GetChildren().OfType<NGridCardHolder>()`. For SelectCard you need the `NCardHolder` base type, so enumerate `_cardRow.GetChildren().OfType<NCardHolder>()` and index by position. Cards are added in order matching `_options` (verified at line 184: `for (int i = 0; i < _options.Count; i++) { ... _cardRow.AddChildSafely(holder); }`), so `_cardRow.GetChild<NCardHolder>(i)` aligns with `_options[i]`.
- Card title for chat receipts: `result.Card.Title` returns `string` (already formatted; handles upgrade suffix). **Spec called this `result.Card.Name.GetText()` — that chain does not exist.** Use `result.Card.Title` directly (from `CardModel.Title` property, line 92 of CardModel.cs decompile). `Card` is `CardCreationResult.Card => _modifiedCard ?? originalCard` (line 14 of CardCreationResult.cs).
- `MegaCrit.Sts2.Core.Runs.RunManager.Instance` — `public static RunManager Instance { get; } = new RunManager()` (line 62 of RunManager.cs; confirmed singleton, eagerly initialized)
- `RunManager.DebugOnlyGetState()` returns `RunState?` (declared as nullable; line 1394). Returns the private `State` field which is null when not in a run; **non-null in modded production: <RUNTIME-VERIFY>** — should be non-null on a card-reward screen because the screen only appears mid-combat-reward sequence, which requires an active run.
- `RunState.Id` — **DOES NOT EXIST.** No `Id` property on RunState (verified by inspection of full file and grep). Spec assumed this. Closest stable run identifier: `runState.Rng.StringSeed` (string — the user-supplied seed) or `runState.Rng.Seed` (uint — deterministic hash of the StringSeed). Recommended: `runState.Rng.StringSeed` for the run-ID guard; reads cleanly and survives serialization. Conversion: already a string, no `.ToString()` needed. If a more "instance-identity" guard is wanted (distinguishing two runs with the same seed string, which is rare but possible if the same daily is re-attempted), fall back to reference equality on the `RunState` instance itself: capture `runState` at vote-start, compare `ReferenceEquals(runState, RunManager.Instance.DebugOnlyGetState())` at resume.
- `RunState.Players.Count` — `Players` is `IReadOnlyList<Player>` (line 39 of RunState.cs). `.Count` reachable; this is also already used by `NeowBlessingVotePatch.TryGetEventOwnerPlayerCount` via the `EventModel.Owner.RunState.Players.Count` chain — pattern already in production.
- Current-act access pattern: **`runState.CurrentActIndex`** is the cleanest accessor. It's a public `int` property with public getter/setter on `RunState` (line 43). `ActConsoleCmd.NextAct` writes via `RunManager.Instance.EnterAct(actIndex)` and the State exposes the index directly. The `IRunState` interface also exposes it (line 23 of IRunState.cs), so the property is part of the stable contract. `runState.Acts.Count - 1` would give the *final* act index, not the current one. `runState.CurrentRoom?.Act` does not exist on AbstractRoom in this surface. Recommended: `runState.CurrentActIndex` (0-based; add 1 for human-readable "Act N" display).

CardRewardSkipGatePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen` (type — public class, namespace confirmed)
- `NRewardsScreen._Ready()` — Harmony patches: <RUNTIME-VERIFY> (declared on NRewardsScreen at line 226; should patch cleanly since it's a `public override void` declared on the type)
- `NRewardsScreen._ExitTree()` — Harmony patches: <RUNTIME-VERIFY>. **Important**: NRewardsScreen does NOT declare `_ExitTree`; Harmony will target the inherited Godot Control/Node `_ExitTree`. Likely-safer alternative: `AfterOverlayClosed()` (declared on NRewardsScreen, line 460; runs during the overlay-close pipeline before `QueueFreeSafely`). See "Harmony patchability" section above for fallback chain.
- `NRewardsScreen.RewardSkippedFrom(Control)` — `public void RewardSkippedFrom(Control button)` (line 350; takes `Control` not `NRewardButton` — the signal is wired with `Callable.From<NRewardButton>` but the receiver signature accepts the base `Control` type)
- `NRewardsScreen.DisallowSkipping()` — `public void DisallowSkipping()` (line 306; no parameters, sets `_skipDisallowed = true` and disables proceed button if it's currently in Skip mode)
- `NRewardsScreen._rewardButtons` — `private readonly List<Control> _rewardButtons` (line 149; populated in `SetRewards` at line 291 with each `option` being either an `NRewardButton` or `NLinkedRewardSet`, both Control subclasses)
- `NRewardsScreen._skippedRewardButtons` — `private readonly List<Control> _skippedRewardButtons` (line 151; populated in `RewardSkippedFrom` at line 352)
- `NRewardsScreen._proceedButton` — `private NProceedButton _proceedButton` (line 129; type is concrete `NProceedButton` not Control)
- `MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton` (type — at `decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rewards/NRewardButton.cs`; **note namespace is `Nodes.Rewards`, not `Nodes`** as the spec wording implied)
- `NRewardButton.Reward` — **property** (not field): `public Reward? Reward { get; private set; }` (line 105). Type is `Reward?` (nullable) where `Reward` is `MegaCrit.Sts2.Core.Rewards.Reward` (abstract base class). Accessibility: **public getter, private setter** — reflection not needed for read; direct property access works. Initially null until `SetReward` is called during `Create`.
- `MegaCrit.Sts2.Core.Rewards.CardReward` (type — concrete subclass of `Reward`, line 29 of CardReward.cs; usable for `is CardReward` identity check on `NRewardButton.Reward`)
- Current-act accessor: **`runState.CurrentActIndex`** (0-based int; same rationale as CardRewardVotePatch — public on `RunState`, also exposed via `IRunState` interface, used internally by `ActConsoleCmd` and `RunManager.SetActInternal`)
- Vanilla `RewardCollectedFrom(Control)` removes button from `_rewardButtons`: **YES.** Verified at line 334–348 of decompile. Sequence: `int a = _rewardButtons.IndexOf(button); RemoveButton(button); ...` and `RemoveButton` calls `_rewardButtons.Remove(button)` (line 402). The button is also queue-freed (line 400). So a postfix observing `_rewardButtons` after `RewardCollectedFrom` will see the collected button gone. A skip-gate prefix that decides whether to hand control to chat must run BEFORE `RewardCollectedFrom` (e.g., on the click signal or via `RewardClaimed` signal interception) — but a postfix-on-`RewardCollectedFrom` for *post-claim* behavior (e.g., updating a per-act tally) sees the cleaned-up state.

NeowBlessingVotePatch (B.1, retro-touched in B.2.1):
- All B.1 reflection (already in NeowBlessingVotePatch.cs)
- `RunManager.Instance.DebugOnlyGetState()?.Rng.StringSeed` (NEW in B.2.1 for run-ID guard — note: **not** `.Id` as the spec suggested; that property does not exist)

### Vanilla back-out path from NCardRewardSelectionScreen

Result: <RUNTIME-VERIFY: open card sub-screen in-game, look for back/cancel mechanism — Escape key, X button, right-click, etc.>
- **Spike-author hint from decompile**: `NCardRewardSelectionScreen` itself has no obvious cancel button or escape-key handler in `_Ready` or elsewhere; the `_extraOptions` list (line 100, type `IReadOnlyList<CardRewardAlternative>`) drives "alternative reward" buttons via `OnAlternateRewardSelected` (line 247) which can complete the screen with `PostAlternateCardRewardAction.DismissScreenAndRemoveReward`. There may also be a global escape-handler at the `NOverlayStack` level (the screen is pushed via `NOverlayStack.Instance.Push` at line 149, which is the standard overlay pattern shared with NRewardsScreen). Operator should test: Escape key, right-click, controller B-button. If a back-out exists, it likely closes the sub-screen without selecting (returning control to NRewardsScreen). The `_ExitTree` override (line 230) handles the unselected-completion case: if `_completionSource` is not yet completed, it fires with `(Array.Empty<NCardHolder>(), item2: false)` — so back-out is structurally supported.
Implication for acceptance gate Mode B verification: <doable | record as N/A — depends on verify result>.

---

## Outstanding from session 2

### First action next session

- [ ] **Code-quality review for commit `bfb77d6`** (Task 5.2 — VoteSession parsing + tally). Implementer reported DONE, but spec/code-quality reviewer rounds were not run before the session's context budget ran out. Implementation is verbatim per plan, so risk is low; do the review as the first subagent dispatch in the new session.

### Polish (deferrable; only do if/when biting)

- [ ] **`*.sln` gitignore section placement** — currently under "OS" section in `.gitignore`. Reviewer suggested moving to a dedicated "Build artefacts" section. Cosmetic.
- [ ] **`FakeChatService` polish** (from Task 3.1 review):
  - Use `_sent.AsReadOnly()` instead of returning `_sent` cast as `IReadOnlyList<SentMessage>`; closes a determined-cast loophole.
  - Add three small tests: `SimulateState` event firing, `Dispose` transition to `Disposed`, same-state transition is silent.
- [ ] **`FakeTimerScheduler.SchedulePeriodic`** doesn't validate `interval > 0`. Plan A call sites are safe (cadence floor 7s). Defensive guard one-liner.
- [ ] **`ImmediateDispatcher.Post(null)`** would NRE inside the lambda. Optional `ArgumentNullException.ThrowIfNull(action)` for clearer stack.
- [ ] **`DrainAsyncCompletesImmediately` test** asserts no-throw, not actual immediate completion. A regression making it `Task.Delay(5s)` would still pass. Optional: assert `IsCompletedSuccessfully` before awaiting.

---

## Plan B.1 vertical slice (resolved 2026-05-10)

Plan B.1's spec is at [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](../docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md); the implementation plan at [`docs/superpowers/plans/2026-05-09-plan-b-1-vertical-slice.md`](../docs/superpowers/plans/2026-05-09-plan-b-1-vertical-slice.md). Tagged `plan-b-1-complete`.

### Acceptance gate — all green

- [x] All Plan A regression tests pass (142 → 183 total with B.1's additions).
- [x] All new unit tests pass (~40 new across `ModSettings` + `TwitchIrcChatService` + `OutgoingMessageQueue` spacing).
- [x] **Step 0** vanilla-baseline operator-validation green (no settings file → mod loads silently, Neow plays vanilla).
- [x] **Step 1** IRC operator-validation green (connect succeeds, "connected" receipt fires once per process).
- [x] **Step 2** full Neow vote operator-validation green (3 successful runs covering: no-vote random pick with "randomly" close-receipt; latest-wins with multi-vote-from-one-user; both `#N` and bare `N` accepted; in-game tally label visible top-right; z-order above game UI).
- [x] **Step 3** failure-mode operator-validation green (bad oauth → AuthenticationFailed terminal + chat-readiness-gate bail; mid-vote disconnect with reconnect → vote completes correctly via Twitch's IRC backlog-on-JOIN; streamer escape mid-vote → resume drops or absorbs silently, no crash).

### Architecture-defining outcome

**The suspend-and-resume Harmony pattern is now production-validated.** The smoke proved blocking-await deadlocks under Godot's main-thread sync context; B.1's first real Neow vote was the first evidence that Plan A's `RunContinuationsAsynchronously` design + dispatcher-Post resume actually works for non-blocking mutation. Pattern is reusable verbatim for B.2's other 4 Harmony patches.

### Findings worth preserving

- **`DisableEventOptions` visual = no hover pop, options stay readable.** The earlier B.2 follow-up "evaluate keeping `DisableEventOptions` vs flag-only suppression" is closed — keep `DisableEventOptions`. Chat readability concern was unfounded.
- **BBCode-in-chat absent for Neow event options.** `EventOption.Title.GetFormattedText()` returns plain text for the relics seen in B.1 testing. Earlier "needs a BBCode stripper" concern closed for v0.1; revisit if B.2's other patches surface markup in receipt text.
- **Twitch IRC delivers backlog on JOIN.** During mid-vote disconnect, votes sent to Twitch chat *during* the disconnect window were delivered to our bot after reconnect (within Twitch's recent-message backlog). Architectural assumption "we lose votes during disconnect" was overly pessimistic — close-receipt's "(chat was offline Xs)" annotation may be misleading in cases where votes weren't actually lost.
- **Z-order under `SceneTree.Root` works fine.** The `CanvasLayer` fallback comment in `VoteTallyLabel.AttachTo` is unused; keep the comment but no action needed.
- **Path resolution**: `OS.GetUserDataDir()` on Windows for StS2 returns `%APPDATA%\SlayTheSpire2\` (not the default `Godot/app_userdata/Slay the Spire 2/` Godot convention). The game has its own override. JSON config goes at `C:\Users\Surfinite\AppData\Roaming\SlayTheSpire2\slay_the_streamer_2.json`.
- **Code-review caught one real bug** (Task 14 dispose-guard): the `_state != ChatConnectionState.Disposed` check in `RunConnectionAsync`'s catch was functionally a no-op until Task 28 properly transitioned state. Fixed via two-catch (OperationCanceledException no-op + generic Exception with `!_disposed` guard). The two-stage review (spec + quality) earned its keep here.
- **Namespace ambiguity caught at build time** (Task 35): `ModSettings` exists in both `SlayTheStreamer2.Game.Bootstrap` and `MegaCrit.Sts2.Core.Modding`. Resolved cleanly via `using BootstrapModSettings = ...` alias.

### B.1 follow-ups (deferred to B.2 / Plan C / cleanup)

Onboarding & UX:

- [ ] **`forceFirstRunNeow: true` settings flag** — modded saves don't have unlock progression for Neow on first runs (separate save profile = no unlocks = no Neow). Tempus's StS1 mod did this via `Settings.isTestingNeow = true`; StS2 likely has an equivalent. Decompile-search needed for the exact field/method.
- [ ] **`copySaveFromUnmodded: true` settings flag** — alternative onboarding fix, lift the streamer's existing unmodded progress into the modded save folder. More involved (file copy + path resolution) but more "real run" experience than the unlock-flag approach.
- [ ] **Streamer onboarding note** — "Pick any blessing; chat will override your choice. Picking is what triggers the vote — you can sit on the screen as long as you want before clicking, useful for pacing." Belongs in README usage section + B.2 settings UI tooltip. **Possible B.2/B.3 architecture pivot**: vote-on-room-shown rather than vote-on-click. Pros: no confusing manual pre-click. Cons: streamer can't pause before vote starts; doesn't generalise to inherently-click-triggered decisions (card reward, shop, map). Probably keep current model + add docs; revisit if streamers complain.

Logging & UX polish:

- [ ] **Resume-after-abandon race window** — 30s background vote can complete after streamer abandons the run. Currently absorbed silently (game ignores click into dying run). B.2 hardening: add a run-ID guard (compare `RunState`'s id at vote-start vs at resume) and skip the resume Post if the run changed.
- [ ] **`VoteSession.SendReceipt` send-failure log level too noisy** — when chat is mid-Reconnect, the close-timer fires and the receipt-send fails with `Cannot send in state Reconnecting`. Currently logged at Error; should be Warn (it's an expected degraded path, not an exception). Plan A revision.
- [ ] **Buffer close receipt during reconnect** — chat doesn't see the close receipt if the close-timer fires during disconnect. B.2 polish: buffer the receipt and re-send post-reconnect with a "delayed by Xs" annotation.

### Vanilla bugs observed (NOT ours; recorded so we don't chase them later)

- `data.tree is null` in `MegaCrit.Sts2.Core.Nodes.TopBar.NTopBarPauseButton.AnimUnhover` during scene transitions (e.g., game-over → main menu after run abandon). Pure MegaCrit; the pause-button starts an async `AwaitProcessFrame` on a Node that's been removed from the tree by then. Harmless; game continues.
- `Error deleting current_run.save.backup: Failed` in `MegaCrit.Sts2.Core.Saves.RunManager.OnEnded` during run abandon. Steam-cloud save cleanup race. Harmless.
- Godot rendering server "leaked at exit" warnings (1050+ CanvasItems, 373 ShapedTextData, etc.) on shutdown. Vanilla Godot lazy-cleanup ordering. The OS reclaims everything immediately after; the warnings just mean the rendering server's own cleanup pass didn't catch every Resource. Our mod adds 1–2 of these at most (one `RichTextLabel` + one `Node`); the rest are vanilla.

---

## Pre-Plan-B prep (resolved)

- [x] **Switch Steam branch from beta to stable.** Done 2026-05-08.
- [x] **Re-run `ilspycmd`** against the new stable `sts2.dll`. Done 2026-05-08; diff captured. Beta is the *newer* dev branch; what we saw as "stable removed X" was actually "beta added X that hasn't shipped yet." Modding contract (`Mod`, `ModInitializerAttribute`, `ModManifest`, `Logger`) is byte-identical between branches; only `ModManager` got internal hardening (circular-dep detection). `AbstractModel` had real signature drift (mostly `PlayerChoiceContext` parameter additions in beta + 4 new auto-play-phase callbacks; `ICombatState`/`NullCombatState`/`PlayerTurnPhase` deleted in stable). Doesn't affect v0.1 (Harmony-heavy); affects v0.2+ combat hooks only.
- [x] **Update `notes/03/04/05`** — drift summary added inline as callout boxes in `notes/03` and `notes/04`.
- [x] **Verify `MegaCrit.Sts2.Core.Logging.Log` is thread-safe.** Confirmed: `Logger.LogMessage` holds a `static readonly object _lockObj` around `_logPrinter.Print` + `LogCallback?.Invoke`. `TiLog.Sink` can be a direct passthrough — no buffering needed.
- [x] **Validate Godot autoload registration from a mod assembly.** Resolved by Plan B prep smoke (commit `204d061`, run 2026-05-09). Direct `tree.Root.AddChild(node)` from `[ModInitializer]` errors with "Parent node is busy setting up children" because `Init` runs during `NGame._EnterTree`. Fix: `tree.Root.CallDeferred("add_child", autoload)` — defers the attach to the next idle frame. `Engine.RegisterSingleton(name, node)` works as optional instrumentation. Working pattern is permanently captured in `src/ModEntry.cs`.
- [x] **Smoke-test the Harmony deadlock risk.** **Resolved with the deadlock confirmed**, exactly as the meta-review predicted. Smoke C ran a Harmony prefix on `NSettingsScreen._Ready` that did `session.AwaitWinnerAsync().GetAwaiter().GetResult()` on the Godot main thread. The game hung at startup (StS2 instantiates Settings during boot, before main menu). The deadlock chain: prefix blocks main thread → close timer fires on threadpool → dispatcher does `CallDeferred` → idle frame queued for main thread → main thread blocked → close never runs → `.GetResult()` waits forever. **Plan A's `RunContinuationsAsynchronously` on the winner TCS is insufficient under Godot's main-thread sync context** (which re-captures `await` continuations onto thread 1; observed in Smoke A's `continuation thread=1, main thread=1` log). **Plan B must use suspend-and-resume**: Harmony prefix returns `false` to skip the original method, kicks off `_ = HandleVoteAsync(...)`, and the async handler invokes the chat-winner's choice via `dispatcher.Post(...)` once the vote completes. No blocking the main thread, ever. This was on the meta-review's "Future considerations" list; the smoke promoted it to "the only viable pattern."

---

## Plan B implementation reminders

Items deferred from Plan A reviews to "fix when the real impl lands":

- [ ] **`TiLog.Sink` should scrub `ex.ToString()`** before forwarding. `TiLog.Error(msg, ex)` only scrubs `msg`; if an exception's Message contains an oauth token (e.g., wrapped HTTP exception), an unscrubbed Sink that calls `ex.ToString()` leaks it. Wire up in Plan B's `ModEntry` Sink.
- [ ] **Pin down `IMainThreadDispatcher.DrainAsync` re-entrancy contract.** Are actions enqueued *during* draining awaited? Or only those queued *at the moment of the call*? Document, match `GodotMainThreadDispatcher`'s actual behaviour, add a test.
- [ ] **Pin down `IMainThreadDispatcher.Post` exception policy.** Synchronous (`ImmediateDispatcher`) propagates. Godot impl can't propagate (`CallDeferred` is fire-and-forget). Choose: log via `TiLog.Error` + continue (recommended), swallow, or crash. Document on the interface; add a queue-poison-resistance test for `GodotMainThreadDispatcher`.
- [ ] **`TwitchIrcChatService` must stamp `ChatMessage.ReceivedAt`** when `tmi-sent-ts` tag is absent. Plan A's parser returns `DateTimeOffset.MinValue` as a sentinel; service stamps from injected `IClock` before raising `MessageReceived`.

---

## Optional Enhancements not folded in

From the meta-review's Optional Enhancements table — flagged for future consideration:

- [ ] **#2 Reply-parent-msg-id per-voter receipts.** Bot @-replies first-time voters via Twitch's reply-thread feature. Closes the lag gap individually. Strong v0.2 candidate; lean-no for v0.1 (volume scales with brigade size).
- [ ] **#5 Observability dashboard.** Basic counters are in v2.3; fuller per-stream stats dashboard is post-MVP.
- [ ] **#7 `TimeProvider` (BCL .NET 8+)** instead of custom `IClock`/`ITimerScheduler`. ~30 lines of custom code vs. one NuGet dep. Revisit if NuGet deps become acceptable.
- [ ] **#8 Vendor a single-file Twitch IRC library** as Plan B fallback if handcrafted client proves problematic.

(#3 quiet-period dedup is effectively resolved by v2.3's tally-state dedup. #4 heartbeat reconnect was deliberately removed in v2.2 spec rollback.)

---

## Spec-level open items (from v2.3 spec)

- [ ] **Streamer oauth source / onboarding UX** — covered when settings UI is designed.
- [ ] **Streamer-configurable receipt policy** — ship `VoteReceiptPolicy.Default` for v0.1; expose configuration with the settings UI.
- [ ] **Reconnect retry budget knob** (`MaxRetryDuration`) — add if transport-retry-forever proves annoying. Auth/join failures are already terminal.
- [ ] **`AbstractModel` vs Harmony per-decision** — orthogonal to TI layer. Decide per `Game/DecisionVotes/*` patch in Plan B+. See `notes/04-abstract-model-hook-surface.md` table for the per-decision recommendation.

---

## v0.2+ (explicitly out of scope for v0.1)

- **StS2 co-op multiplayer.** API is multiplayer-aware (`VoteCoordinator` is instance-based per Reviewer 6's catch); full multi-streamer impl deferred.
- **Subscriber/mod/VIP-only voting filters.** `ChatMessage` already exposes badge flags; future filter is a `where`-clause in `VoteCoordinator.Start` consumers.
- **Localised receipts.** Add peer static helpers (`SpanishReceipts.cs` etc.) + `Func<VoteSnapshot, ReceiptKind, string>` to `VoteCoordinator.Start`.
- **`ChatCommandRouter` middle tier.** Only when a second non-voting consumer mod actually appears.
- **Twitch Helix API.** Channel-point redemptions, polls, predictions — out of scope.
- **Twitch Extension overlays / whispers.** Read-only IRC + periodic receipts is the entire v0.1 surface.
- **Lifting `Ti/*` into a separate base-mod assembly** (TI-extraction goal). Plan A's seams are pre-drawn so this is a file-move + small registration shim, not a refactor.

---

## Reference materials worth peeking before Plan B

Optional but useful:

- **Crowd Control mod for StS2** (`C:\Users\Surfinite\Downloads\SlayTheSpire2-CC-110.zip`) — Warp World's `CrowdControl.dll` via ILSpy as a *capability reference* (proves which game systems are mod-reachable).
- **spire-scryer** (`github.com/Sezmol/spire-scryer`) — open-source C# StS2 mod that pushes `RunManager` state to Cloudflare Worker → Twitch PubSub overlay. Useful as a reading-game-state reference. No declared license.
- **spire-codex** (`github.com/ptrlrd/spire-codex`) — not a mod, a web data service. Useful as a card/relic/event data reference.
