# Meta-review — Plan B.1 vertical slice design

**Source spec:** `docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design.md` (commit `9902486`)
**Updated spec:** `docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v2.md`
**Reviewers:** 5 (anonymous via t3.chat: DeepSeek, GPT, Gemini, Opus, Gemma classes)

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key Focus | Unique Insight |
|---|---|---|---|
| **Reviewer 1** | Mixed-critical | Twitch rate limits, double-free, _voteInProgress reset timing, public-static dispatcher | Surfaces 1-second inter-channel slow-mode (Twitch protocol detail) |
| **Reviewer 2** | Mixed-critical (very thorough) | Fail-soft on Voter.Start exception, options snapshot, JOIN-timeout, resume validity checks | **Catches "fail soft" not applied to Voter.Default.Start path** — losing the player's click is needlessly bad UX |
| **Reviewer 3** | Mixed-critical (architecturally sharpest) | Dispatcher exposure via VoteCoordinator, DisableEventOptions promotion, Godot-side path resolution | **Promotes DisableEventOptions from contingency to mitigation** — addresses the fast-click race at the source |
| **Reviewer 4** | Focused-critical | Double-free crash, keyboard hotkey bypass, RichTextLabel, EventSynchronizer alternative | **Keyboard hotkey bypass concern** — verified-false against decompile but worth ruling out explicitly |
| **Reviewer 5** | Mixed-critical (lifecycle-focused) | VoteTallyLabel lifecycle, _Process polling, multiplayer validation in gate, method signature check | **Method signature check in Prepare** — defensive against game-version drift |

All 5 reviews are **mixed-critical**. None recommend rejecting the spec; all recommend the same headline fix (`VoteTallyLabel` double-free) plus various sharpenings.

---

## A.2 — Consensus Points (2+ reviewers)

Ranked by reviewer count.

### 5/5 — UNIVERSAL CONSENSUS

**1. `VoteTallyLabel` double-free / lifecycle is the highest-severity bug.**
All 5 reviewers flag this. Mechanism: spec attaches the Label as a child of `NEventRoom` AND subscribes to `session.Closed`/`Cancelled` to call `QueueFree()`. When `NEventRoom` is freed by the game (scene transition, escape, normal completion), Godot frees children including the Label — making the explicit `QueueFree()` from the session-event handler a use-after-free / double-free. Will crash or corrupt state.

### 4/5

**2. Public mutable static `ModEntry.Dispatcher` should not be public-mutable.** (R1, R2, R3, R4)
Sets a footgun precedent for B.2's other patches. Best fix: expose via `VoteCoordinator.Dispatcher` (R3); fallback fix: `internal static ... { get; private set; }` (R2, R4).

**3. Use `RichTextLabel` not `Label` for `VoteTallyLabel`.** (R1, R3, R4, R5)
StS2's `LocString` titles likely contain BBCode/markup (color tags, keyword highlighting). `Label` renders them as literal text; `RichTextLabel` with `BbcodeEnabled = true` handles them natively. R3 also notes this is a near-free upgrade regardless.

### 3/5

**4. Stricter chat-readiness gate before opening a vote.** (R2, R3, R4)
Spec uses `chat.IsConnected`; this is `true` for `ConnectedReadOnly` (anonymous mode where `CanSend == false`). Opening a vote in this state means receipts go nowhere. Use `chat.State == ChatConnectionState.ConnectedReadWrite` (or `IsConnected && CanSend`).

**5. Try-catch `Voter.Default.Start` inside the prefix.** (R2, R3)
Plan A's `Start` can throw `InvalidOperationException` if a session is already open. Spec's failure-mode table accepts "player click is lost; re-click works" — but the prefix has already returned `false`, so the click visibly did nothing. Better: wrap in try/catch; on throw, reset flag + `return true` to let original run (vanilla fallback). R3 (M5) is sharpest on this.

**6. Resume-time validity checks before re-entering `OptionButtonClicked`.** (R2, R3, R4)
The dispatcher.Post resume blindly calls `room.OptionButtonClicked(options[winnerIndex], winnerIndex)`. If the room/event/options state has changed (escape, transition), this can throw inside StS2. Need: `IsInstanceValid(room)`, still-Neow check, re-read current options + bounds-check winner.

### 2/5

**7. Twitch rate-limit constants are wrong (90/30s).** (R1, R2)
**Validated against codebase: `OutgoingMessageQueue` is configurable (constructor takes `capacity, window`)**, NOT hardcoded. So this is a `ModEntry` configuration question, not a Plan A code change. The Plan A v2.3 spec's stated default of 90/30s doesn't match documented Twitch limits (20/30s non-mod, 100/30s mod/VIP). B.1's `ModEntry` should pass conservative `20/30s` defaults and document the mod/VIP option for higher rate.

**8. Snapshot options on the main thread, don't pass live `CurrentOptions` reference.** (R2 explicitly; R3 implied via "snapshot small record")
Spec passes `GetCurrentOptions(__instance)` (a live `IReadOnlyList<EventOption>` from the event model) into the background `HandleVoteAsync`. If the event mutates that list, downstream usage races. Cheap fix: `.ToList()` immediately to freeze.

**9. Subscribe to `TallyChanged` events vs `_Process` polling.** (R1, R5)
**Conflict with R3+R4** — R3 says "polling is intentional for B.1; document the choice"; R4 says "do not try to optimize this with events for a text label." See A.5 Conflict 1.

**10. Method-signature / target-resolution check in `Prepare`.** (R3 cache FieldInfo; R5 method-signature check)
Defensive against game-version drift. Currently `Prepare` only logs the resolved target; doesn't verify shape. R5 wants an actual signature check; R3 wants a `Lazy<FieldInfo?>` for the `_event` field with fail-loud-at-Prepare-time.

---

## A.3 — Outlier Points (1 reviewer only)

**R4 alone caught: keyboard hotkey bypass.**
> "Patching `NEventRoom.OptionButtonClicked` relies on mouse interaction. If StS2 allows keyboard shortcuts to select dialog options (as StS1 did), the game input logic likely calls `ChooseLocalOption` directly, bypassing your patch."

**Validated against codebase: this concern is FALSIFIED by the actual decompile.** Searched `NEventRoom.cs` for `_UnhandledInput`, `_Input`, `KeyPressed`, `key_pressed`, and `InputMap` references — **no matches**. `NEventRoom` does not register a keyboard input handler; option selection is mouse-button-only. R4 was reasoning from the StS1 precedent but StS2's `NEventRoom` doesn't repeat it. **Safely rejecting** this concern.

**However**, R4's underlying suggestion ("patch `EventSynchronizer.ChooseLocalOption` instead — survives UI refactors") still has merit as a B.2 consideration. Not adopting for B.1 (decompile evidence supports `OptionButtonClicked` being the right surface today), but flagging in the v2 spec's "B.2 considerations" section.

**R2 alone caught: JOIN-confirmation timeout.**
> "Twitch may quietly drop the JOIN for nonexistent channels rather than send a clean failure notice. `TwitchIrcChatService` needs a join/auth timeout."

**Validated as real**: Plan A v2.3's IRC protocol matrix handles the explicit `JoinFailed` NOTICE branches but doesn't specify a fallback timeout for silent JOIN drops. **Adopting** as a Should-do for B.1 with a documented timeout (10s) and a new `JoinFailed` transition path.

**R3 alone caught: settings file path resolution will diverge between .NET special folders and Godot's `user://`.**
> Spec's `ModSettings.GetSettingsPath` resolves via `Environment.SpecialFolder.ApplicationData` directly, but Godot's `user://` resolves via `OS.GetUserDataDir()` which depends on `project.godot`'s configured project name.

**Validated as real**: the `%APPDATA%\Godot\app_userdata\Slay the Spire 2\` path is *Godot's* convention based on the running game's project name; .NET's `ApplicationData` won't naturally land there without hardcoding. **Adopting** R3's fix: resolve path Godot-side in `ModEntry`, pass into `ModSettings.Load(path)` — keeps `ModSettings` BCL-only and testable.

**R3 alone caught: JSON schemaVersion field.**
> Cheap to add now; expensive to retrofit later when B.2 grows the schema.

**Adopting** — `"schemaVersion": 1` field, with `Load` rejecting unknown future versions as `Malformed`.

**R3 alone caught: connect-receipt should fire once per process, not once per ConnectedReadWrite transition.**
> Reconnection flaps would spam the chat with "connected" messages.

**Adopting** — `_connectAnnounced` static gate in `ModEntry`. Also include mod version in the message (R3 L3).

**R5 alone caught: anonymous mode operator-validation.**
> "Spec says 'B.1 doesn't exercise this path' but the implementation must not break it. Without live validation, a bug could silently break read-only use cases."

**Adopting partially** — adding to operator-validation step 1 (IRC alone) since it's cheap to verify.

**R3 alone caught: explicitly test no-chat-input case.**
> Spec's fallback handles `winnerIndex < 0 || >= options.Count`, suggesting "no votes returns -1." If Plan A actually returns `0` (default), the streamer's click is silently ignored without the test catching it.

**Validated against codebase**: `VoteSession`'s no-voter path uses `_random.Next(0, Options.Count)` (per Plan A's spec — uniform random across all options when zero votes received). So the winner index is always a valid index, not -1. R3's fallback `winnerIndex < 0` is dead code in the no-voter case. **Adopting** the explicit operator-validation step ("no chat input → vote completes with random pick") since it's cheap and surfaces the actual behavior.

**R3 alone caught: vanilla baseline operator-validation step.**
> "Build + install + launch with no settings file, no Twitch involvement at all, reach Neow. Confirm the mod is fully invisible."

**Adopting** as Step 0 — it's a sanity check that the patch attached doesn't accidentally break the game. This was implicitly inside Step 3's "no settings" sub-case but worth promoting.

---

## A.4 — Category Breakdown

### 🏗️ Architecture & Design

- **`VoteTallyLabel` lifecycle** (5/5). Adopting: parent under `GetTree().Root` (avoids Godot's auto-free of children); `IsInstanceValid` guards on all session-event handlers; unsubscribe + `session.Cancel()` in `_ExitTree` (R4's lifecycle addition, R2's cleanup hardening).
- **Public-static `ModEntry.Dispatcher`** (4/5). **Validated**: `VoteCoordinator` already holds an `IMainThreadDispatcher` privately. Adding `public IMainThreadDispatcher Dispatcher => _dispatcher;` is a 1-line Plan A change. Adopting (R3's fix). Patch reads `Voter.Default!.Dispatcher`.
- **Patch target choice** (R4 alternative, R3 conditional). All 5 reviewers ultimately endorse `OptionButtonClicked` for B.1 with `EventSynchronizer.ChooseLocalOption` flagged for B.2 reconsideration. Keeping current; documenting the alternative.
- **Promote `DisableEventOptions` from contingency to mitigation** (R3, R4). **Validated**: `MegaCrit.Sts2.Core.Nodes.Events.NEventLayout.DisableEventOptions()` exists at line 310 of the decompiled file; already called internally by `NEventRoom.BeforeOptionChosen`. Adopting: call `room.Layout.DisableEventOptions()` immediately after the vote opens.

### ⚠️ Risks & Concerns

- **Twitch rate limit (90/30s wrong)** (R1, R2). **Validated as real config issue, not a code bug**. Adopting: ModEntry passes `20/30s` conservative default to `OutgoingMessageQueue`; document the mod/VIP path to `100/30s`.
- **Fast-click race during resume** (R1, R3, R4, R5 all touch). Three proposed mitigations: (a) delay reset (R1), (b) `DisableEventOptions` (R3, R4), (c) defer to B.2 (spec). Adopting (b) — cleanest, prevents the race rather than papering over it. R3's L4 alternative is now closed.
- **Voter.Default.Start can throw, click lost** (R2, R3). Adopting: try/catch in prefix; on throw, log + reset flag + `return true`.
- **Settings path Godot-vs-.NET divergence** (R3). Adopting.
- **JOIN-confirmation timeout missing** (R2). Adopting.
- **Patch target instability** (R5). Adopting: signature check in Prepare.
- **Cached `FieldInfo` for `_event`** (R3). Adopting.
- **No multiplayer bail-out in code** (R5, R2 mentioned). Adopting: move from open-items to actual prefix code.

### 🗑️ Suggested Removals / Simplifications

- **`throw new NotImplementedException` stubs in spec body** (R1). Adopting: replace with concrete pseudocode for `IsNeowEvent` and `GetCurrentOptions`.
- **Open Items #2 (`EventModel.CurrentOptions` accessibility)** (R3). Adopting: it's verified as public; move to "Verified facts" subsection or delete.
- **Open Items #7 (`SystemTimerScheduler` ctor signature)** (R3). Soft-rejecting — keep in spec, it's a real footgun to flag for the implementer.
- **`RichTextLabel` parenthetical ambiguity** (R3). Adopting: pick `RichTextLabel`.
- **Prepare comment confusion** (R1, R2, R5). Adopting: rewrite the comment.

### ➕ Suggested Additions / Features

- **JSON schemaVersion field** (R3 M1). Adopting.
- **Connect-receipt once per process + mod version** (R3 M2 + L3). Adopting.
- **No-chat-input operator-validation step** (R3 M6). Adopting.
- **Anonymous-mode operator-validation** (R5). Adopting.
- **Multiplayer bail-out in prefix code + acceptance gate** (R5). Adopting.
- **Vanilla baseline operator-validation step (Step 0)** (R3). Adopting.
- **`ModSettings.SettingsResult.Success` warnings list** (R3). Adopting (small).
- **`CancellationToken` on `HandleVoteAsync`** (R2). Adopting (small).
- **NOTICE msg_ratelimit / msg_slowmode handling in TwitchIrcChatService** (R2). Adopting (Plan A v2.3 protocol matrix already lists "Unknown / malformed Debug-log + counter" — these specific NOTICEs warrant Warn + back-off).
- **5-second timeout on resume Post** (R5). **Rejecting** — overcomplication; if `dispatcher.Post` doesn't run, the game is broken in worse ways. Trust the dispatcher.
- **Visual highlight on player's clicked button** (R5). Deferring to B.2 / polish — out of B.1's "minimal UI" scope.

### 🔄 Alternative Approaches

- **Patch `EventSynchronizer.ChooseLocalOption`** (R3 §5, R4 §5). Documenting as B.2 consideration; not adopting for B.1.
- **Synchronous `Voter.Start` in prefix** (R2 Alternative 2). **Adopting** — overlaps with the try/catch fix (the synchronous Start IS where the try/catch lives).
- **`DecisionVoteGate` shared-state class now** (R2 Alternative 3). Deferring to B.2 — premature for one patch. Documenting.
- **Local fake IRC server for tests** (R2 Alternative 4). Reviewers agree: not worth for B.1. Documenting.
- **Mock `TcpClient`/`SslStream` directly** (R1 Alternative 3). R1 also recommends keeping `IIrcTransport` seam. Keeping spec's choice.

### ✅ Confirmed Good / Keep As-Is

- **Suspend-and-resume pattern as load-bearing constraint** (5/5).
- **Two-flag re-entry guard structure** (5/5 — R1 and R5 want refinements but no one rejects the pattern).
- **Vertical-slice scope (Neow + IRC + minimal UI)** (5/5).
- **Failure-mode table comprehensiveness** (5/5).
- **Sequence diagram in `NeowBlessingVotePatch`** (R3 specifically called this out as worth keeping for B.2 patches).
- **TI/Game seam preservation** (5/5).
- **Operator-validation 3-step plan** (5/5 — additions accepted, structure preserved).
- **`IIrcTransport` test seam** (R1, R2 endorse).
- **Decompile-evidenced patch target choice** (5/5).
- **Decision #8 verification-against-Tempus's-source paragraph** (R3 explicitly).
- **`_Process` polling for the minimal Label** (R3 + R4 — see A.5 Conflict 1).
- **Plan A's `RunContinuationsAsynchronously` design** (5/5 — nobody questions it; spec inherits correctly).

### 🔧 Implementation Details & Nits

- **`Prepare` comment is misleading** (R1, R2, R5). Adopting: rewrite.
- **Architecture tree emoji legend missing** (R3). Adopting.
- **Decompile file path for Neow.GenerateInitialOptions reference** (R3). Adopting.
- **Filter ordering comment in prefix** (R3). Adopting.
- **VoteTallyLabel autowrap / clipping** (R2). Adopting.
- **VoteTallyLabel SetProcess(true)** (R2). Adopting.
- **Logging clean-up around credentials** (R2). Adopting (TiLog already scrubs `oauth:[a-z0-9]+`; verify scrubs bare tokens too).
- **Mod version in connect message + log** (R3 L3). Adopting.
- **LOC estimate is optimistic** (R3). Adopting: revise to ~1,300 source / ~330 tests.
- **One canonical "0-indexed options" statement** (R3). Adopting.
- **Voter.Default cleared on shutdown** (R3). Adopting.
- **TwitchIrcChatService split into helper classes if it gets large** (R3). Documenting as implementation flexibility.

### 📦 Dependencies & Integration

- **Twitch oauth scopes documentation (`chat:read`, `chat:write`)** (R2). Adopting in JSON-schema doc.
- **EventSub/API mention** (R2 §3.11). Adopting one-line note in spec.

### 🔮 Future Considerations

- **`DecisionVoteGate` shared abstraction** (R2 Alternative 3). Documenting for B.2.
- **`EventSynchronizer.ChooseLocalOption` as alternative patch site for non-UI votes** (R3 §5, R4 §5). Documenting for B.2.
- **`VoteOverlayControl` polish, autohide fade, winner-highlight** (already in spec's B.2 plans).
- **In-game settings UI** (already B.2).
- **Local fake IRC server for tests** (R1, R2). Documenting; revisit if `TwitchIrcChatService` proves flaky.
- **`TimeProvider` (BCL .NET 8+)** (already noted in `notes/06`).

---

## A.5 — Conflicts & Contradictions

### Conflict 1: `_Process` polling vs `TallyChanged` event subscription for `VoteTallyLabel`

- R1 (Concern MEDIUM) and R5 (Concern HIGH severity 3) want event-driven updates with `_Process` only for the timer.
- R3 (Concern L2) says polling is intentional for B.1 and worth a one-line comment.
- R4 (Minor / nits) says "Do not try to 'optimize' this with events for a text label."

**Resolution:** Keep `_Process` polling for B.1's minimal Label (R3 + R4's view). Reasoning: per-frame polling is idiot-proof, the cost is negligible for a single Label, the event-driven design is right for B.2's polished `VoteOverlayControl` but adds threading-safety complexity that's wasted on a 4-line text label. Add a one-line comment in `VoteTallyLabel` documenting the choice. R1 and R5 are right architecturally for B.2; for B.1 the simpler approach stays.

### Conflict 2: Resume re-entry guard reset timing

- R1 (Concern MEDIUM) wants `_voteInProgress` reset delayed ~1s via a separate `dispatcher.Post`.
- R3 (H2) wants `room.Layout.DisableEventOptions()` called immediately on resume so further clicks can't fire.
- R4 (Concern MEDIUM) same as R3.

**Resolution:** Adopt R3 + R4's `DisableEventOptions` mitigation. It's structurally cleaner — prevents the race at the source (game stops dispatching clicks) rather than band-aiding via timing. R1's delay is a fallback if `DisableEventOptions` doesn't exist or fails — confirmed via decompile that it DOES exist, so the delay isn't needed.

### Conflict 3: VoteTallyLabel parenting strategy

- R5 (Removal #1 + Alternative 3) wants the Label parented under `GetTree().Root`, not `NEventRoom`.
- R1 (Concern HIGH + Suggested change 2) same.
- R2 (Should fix #8) keeps it under `NEventRoom` but adds defensive guards.
- R4 (Suggested changes) adds `IsInstanceValid` guard but doesn't move parenting.

**Resolution:** Adopt R5 + R1 — parent under `GetTree().Root` (or a dedicated overlay node). This eliminates the double-free risk entirely rather than guarding against it. R2/R4's defensive guards still apply (handlers can still fire after disposal in race conditions). Best of both: re-parent + add guards.

---

## A.6 — Recommended Plan Changes

### Must-do (high consensus, real risks/bugs)

1. **Fix `VoteTallyLabel` lifecycle**: parent under `GetTree().Root`, not `NEventRoom`. Add `IsInstanceValid(this)` guards in `_Process` and event handlers. Unsubscribe from session events in `_ExitTree`. Call `session.Cancel()` in `_ExitTree` if vote still open. [5/5 reviewers]
2. **Use `RichTextLabel` not `Label`** with `BbcodeEnabled = true`. [4/5 reviewers]
3. **Eliminate public mutable static `ModEntry.Dispatcher`**: add `public IMainThreadDispatcher Dispatcher => _dispatcher;` to `VoteCoordinator`; patch reads `Voter.Default!.Dispatcher`. [4/5 reviewers — codebase-validated as a 1-line Plan A change]
4. **Stricter chat-readiness gate**: `chat.State == ChatConnectionState.ConnectedReadWrite` instead of `chat.IsConnected`. [3/5 reviewers]
5. **Try-catch `Voter.Default.Start` inside prefix**: on throw, reset flag + log + `return true` (vanilla fallback). [2/5 reviewers, technically critical]
6. **Promote `DisableEventOptions` to mitigation, not contingency**: call `room.Layout.DisableEventOptions()` immediately after the vote opens. [R3, R4 — codebase-validated, eliminates fast-click race + verified-falsified keyboard-bypass concern]
7. **Snapshot options on the main thread** via `.ToList()` before passing to `HandleVoteAsync`. [R2]
8. **Resume-time validity checks**: `IsInstanceValid(room)`, still-Neow check, re-read `CurrentOptions`, bounds-check `winnerIndex`. Use the freshly-read option object at the index, not the captured reference. [R2, R3]
9. **Add `Players.Count > 1` multiplayer bail-out to prefix code**, not just open-items list. Add to acceptance gate. [R5, R2]
10. **Twitch rate limit: pass conservative `20/30s` to `OutgoingMessageQueue`** in `ModEntry`; document mod/VIP path to `100/30s` in JSON setup docs. [R1, R2 — codebase-validated as a configuration choice, not a Plan A code bug]
11. **JSON `schemaVersion` field**: `"schemaVersion": 1`; `Load` rejects unknown future versions as `Malformed`. [R3]
12. **Connect receipt fires once per process** (track via `_connectAnnounced` static); include mod version in the message. [R3]
13. **Settings path resolved Godot-side**: `ModEntry` uses `OS.GetUserDataDir()` (or `ProjectSettings.GlobalizePath("user://...")`) and passes the resolved path to `ModSettings.Load(string path)`. [R3]
14. **Method signature check + `Lazy<FieldInfo?>` for `_event` in `Prepare`**: fail-loud at patch-install time on game-version drift. [R5, R3]
15. **Replace `NotImplementedException` stubs** in spec body with concrete pseudocode; remove "verified facts" from open-items. [R1, R3]
16. **Fix `Prepare` comment**: clarify intent of returning `true` on null original. [R1, R2, R5]

### Should-do (strong improvements)

17. **Add JOIN-confirmation timeout to `TwitchIrcChatService`**: 10s timeout from JOIN to first ROOMSTATE/USERSTATE/353; transition to `JoinFailed` on timeout. [R2]
18. **Add NOTICE `msg_ratelimit` / `msg_slowmode` handling**: log at Warn, back off. [R2]
19. **Operator-validation Step 0 (vanilla baseline)**: install + launch with no settings, reach Neow, confirm vanilla flow. [R3]
20. **Operator-validation: no-chat-input case**: Step 2 sub-step. [R3]
21. **Operator-validation: anonymous mode**: Step 1 sub-step. [R5]
22. **`ModSettings.SettingsResult.Success` carries warnings list** (e.g., "channel name normalised from URL"). [R3]
23. **`CancellationToken` plumbed through `HandleVoteAsync`** from a mod-level CTS. [R2]
24. **Document Twitch oauth scopes** (`chat:read`, `chat:write`) in JSON setup. [R2]
25. **Document EventSub/API as out-of-scope for v0.1** (forward-looking note). [R2]
26. **Cache `_event` `FieldInfo` via static `Lazy`**. [R3]
27. **Verify TiLog scrubs bare tokens**, not just `oauth:[a-z0-9]+`. [R2]
28. **Mod version logged in `Init`**. [R3 L3]
29. **`Voter.Default` cleared on shutdown** (when StS2 exposes a hook). [R3]
30. **Architecture tree emoji legend** + decompile file path citations. [R3]
31. **Soften "TI core in production" wording** in "What B.1 done unlocks". [R3]
32. **Revise LOC estimate** to ~1,300 source / ~330 tests. [R3]
33. **Add unit tests** per R2's expanded list: CAP NAK fallback, JOIN timeout, disconnect-while-reconnecting, dispose-during-reconnect, send-while-disconnected, queue cancellation on dispose, no-stale-reconnect-after-terminal-auth-failure. [R2]
34. **VoteTallyLabel autowrap + minimum size** for long blessing labels. [R2]
35. **Filter ordering comment in prefix** rewritten for clarity. [R3]

### Consider (nice-to-have, presented as pick-list)

See "Optional Enhancements" at the end of v2 spec.

### Reject (with reason)

- **R4's "keyboard hotkey bypass"**: VERIFIED FALSE against decompile — `NEventRoom` has no `_UnhandledInput`/`_Input`/input-map handlers. Option selection is mouse-button-only. Documenting the verification in the v2 spec to defuse re-occurrence.
- **R1's "subscribe to TallyChanged instead of polling"** (Conflict 1): for B.1's minimal Label, polling is simpler; R3+R4 are right. B.2's polished overlay should subscribe.
- **R5's "5s timeout on resume Post"**: overcomplication — if the dispatcher fails, the entire mod fails in worse ways. Trust the dispatcher.
- **R1's "1-second inter-message gap"**: minor protocol detail; Plan A's queue uses token-bucket which approximates this; not adding to B.1 (would be a Plan A change).
- **R1's "msg_duplicate handling"**: minor; defer to B.2.
- **R5's "remove polished VoteOverlayControl from B.2 scope"**: out of B.1's authority.
- **R5's "visual highlight on player's clicked button"**: out of "minimal UI" scope; defer to B.2 polish.
- **R2's "remove 'production' phrasing for TwitchIrcChatService"**: semantic preference; spec is clear about scope.

---

## A.7 — What Stays

The spec elements universally praised (or at least not contested by any reviewer) and preserved unchanged in v2:

- **Suspend-and-resume pattern as the architectural hard constraint** (5/5).
- **Two-flag re-entry guard structure** (5/5).
- **Decision #8: v0.1 = 5 votes, decoded against Tempus's source** (5/5).
- **TI/Game seam dependency rules** (5/5).
- **JSON-file credentials at user-data path for B.1; settings UI deferred to B.2** (5/5).
- **`OptionButtonClicked` as the patch target** (5/5; alternatives flagged for B.2 only).
- **`IIrcTransport` test seam approach** for `TwitchIrcChatService` (R1, R2).
- **3-step operator-validation model** (5/5; just adds Step 0 + extra sub-steps).
- **Failure-mode table structure** (5/5; just refines specific rows).
- **Scope discipline (no settings UI, no polished overlay, no act-boss)** (5/5).
- **Plan A's `RunContinuationsAsynchronously` TCS design** (5/5 — nobody questioned it).
- **Sequence diagram in patch section** (R3 explicitly).
- **Open Items #1, #5, #6, #7, #8** (specific listed items survive; #2 and #4 get upgraded out of "open").

---

## My pushback layer (controller's editorial)

The user explicitly invited pushback. Here's where I diverge from reviewer consensus:

1. **Pushback on R1 + R5's "switch to event-driven `VoteTallyLabel`."** R3 + R4 are right that for a 4-line text Label, `_Process` polling is idiot-proof and the cost is negligible. Event subscriptions add cleanup complexity (unsubscribe in `_ExitTree`, handle multiple subscriptions, etc.) for zero observable benefit. B.2's polished `VoteOverlayControl` should subscribe; B.1's minimal Label should poll.

2. **Pushback on R4's keyboard-bypass concern.** Verified false — `NEventRoom` doesn't have keyboard input handlers. The reviewer was reasoning from StS1 precedent which doesn't carry over. Documenting the verification so future-self (and B.2 reviewers) don't re-raise it.

3. **Pushback on R5's 5s resume-Post timeout.** This is exactly the kind of "defensive code for things that shouldn't happen" that adds real complexity to handle imaginary failure. If `dispatcher.Post` doesn't run, the game's idle loop is broken — a 5s timeout doesn't recover from that, it just adds another failure path. Trust the dispatcher; if it ever does fail, the bigger problem will surface elsewhere first.

4. **Pushback on the rate-limit concern's framing as a "bug".** R1 and R2 framed `90/30s` as broken. **It's not — it's configurable per-instance.** The spec needs to specify the conservative default, but `OutgoingMessageQueue` is fine. Plan A's documentation may need updating but that's separate from B.1.

5. **Pushback on R4's "patch `EventSynchronizer.ChooseLocalOption` instead."** R4's correct that it's more multiplayer-aware and lower-layer, but for B.1 the codebase evidence supports `OptionButtonClicked` (decompile-verified as the single click intercept; works for the single-player v0.1 case). B.2's broader patch surface might revisit this; B.1 doesn't need to.

6. **The 5/5 consensus on `VoteTallyLabel` double-free was the most valuable single insight from the review pool.** Worth highlighting as the meta-review's biggest single catch — every reviewer independently spotted that "child of `NEventRoom` + explicit `QueueFree` from session events" is a use-after-free pattern in Godot. R5's recommendation to parent under `GetTree().Root` is the cleanest fix; the others' defensive guards are appropriate complementary insurance.

The reviewer pool's strongest collective contribution this round was lifecycle reasoning: every Godot interaction in B.1 got scrutinised for free/dispose ordering, and the spec is materially safer for it. Future patches should expect the same scrutiny on every UI seam.
