# Follow-ups and Deferred Items

Living list of things flagged during sessions that need attention later. Updated as discovered; ticked off as resolved. Most recent items at the top of each section.

---

## Plan B.2.1 design pivot — RESOLVED 2026-05-11: Mandatory-look + Model 2 (transactional)

**Chosen path (after two iterations)**:
- (b) **Mandatory-look**: streamer must open each pending card-reward sub-screen before any path that would skip it. Skipping-without-looking is impossible.
- **Model 2 — transactional commit**: sub-screen Skip AND Escape→Resume back-out both leave the button in vanilla's `_skippedRewardButtons` (which our budget logic deliberately ignores) → they're "tentative" states the streamer can undo by re-opening and claiming via vote. Budget is only charged when the parent Proceed/Skip button is clicked AND the screen actually tears down (via vanilla's `AfterOverlayClosed`). Per-card-button budget: if pending card count would exceed remaining budget, Proceed is blocked — so under default `cardSkipsPerAct: 1`, skipping two cards on the same screen is impossible (would cost 2 skips, budget allows 1).
- **No skip after vote start**: once a card vote countdown is running, neither sub-screen Skip (blocked by `OnAlternateRewardSelected` prefix, pre-existing) NOR parent Proceed (blocked by `VoteInProgress` guard in the OnProceedButtonPressed prefix) can be used. Streamer cannot read chat's trending vote and bail.

**Why Model 2 and not "immediate charge"**: the initial design (Model 1, plan-b-2-1/20.x commits) charged budget on each sub-screen close-without-pick. This made sub-screen Skip indistinguishable from a real commit, but Surfinite hit a UX problem in operator-validation Step 5: he clicked sub-screen Skip as an exploratory "what does this do?" action, got charged, then went back and claimed via vote — but the budget remained charged. Under Model 2 the charge happens only on the parent's final commit click, so exploratory clicks cost nothing if the streamer ultimately claims. The same model also avoids needing to block the vanilla "go-back arrow" (map screen → return to rewards screen), since by the time the streamer is on the map the commit has already happened — though the arrow does open an unresolved correctness question, see below.

**Spec amendment**: Decision 18 in [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](../docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) — Mode B → Mandatory-look. The Model 2 transactional refinement is a 2026-05-11 same-session iteration; the v4 amendment text is broadly correct in spirit, but the "Implementation surface" bullet list in that file refers to the Model 1 patches and is stale — see "Implementation surface (Model 2)" below for the canonical list. Re-fold into a Decision 18 amendment v2 or v5 spec if/when B.2.2 work begins.

**Implementation surface (Model 2)** — see `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`:
- `_openedCardRewardButtonIds: HashSet<ulong>` — per-rewards-screen tracking of which NRewardButton instances had their sub-screen opened. Cleared in `SetRewards` postfix (fresh screen) and `AfterOverlayClosed` postfix (defensive).
- `NRewardButton_OnRelease_Prefix` — records `__instance.GetInstanceId()` when `__instance.Reward is CardReward or SpecialCardReward`. Vanilla's sync click handler at [NRewardButton.cs:214](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rewards/NRewardButton.cs#L214), runs before the async `GetReward()` opens the sub-screen.
- `NRewardsScreen_OnProceedButtonPressed_Prefix` — the gate. Three block reasons: (1) `VoteInProgress` (no bailing on chat's trend); (2) `Unopened > 0` (mandatory-look); (3) `_actSkipsUsed + pending > limit` (budget). On pass, lets vanilla through to `AfterOverlayClosed`.
- `NRewardsScreen_AfterOverlayClosed_BudgetPrefix` — counts pending card rewards (alive in `_rewardButtons` — Model 2 deliberately ignores `_skippedRewardButtons`) and charges budget + sends one receipt per pending. Single charge point under Model 2.
- `NRewardsScreen_SetRewards_Postfix` — observes run/act change for budget reset; attaches the counter label. Does NOT call `DisallowSkipping` (Model 2 keeps Proceed enabled and relies on our prefix block — `_skipDisallowed` is sticky on the vanilla side and would persist incorrectly when the streamer claims mid-screen). Counter label gives visual feedback that budget is empty; clicks just no-op silently when blocked.

**Patches removed under Model 2 (vs Model 1)**:
- `NRewardsScreen_RewardSkippedFrom_Postfix` — vanilla's `RewardSkipped` signal fires for both back-out and sub-screen Skip, but Model 2 doesn't charge until Proceed commits, so no postfix needed.
- `NCardRewardSelectionScreen_ExitTree_Prefix` (the `_suppressNextCardSkip` back-out detector) — under Model 2 both back-out and sub-screen Skip are tentative-equivalent, no need to distinguish them.

**Vanilla quirk identified during design**: the parent's Proceed-as-Skip click does NOT emit `NRewardButton.RewardSkipped` for the buttons that get skipped — vanilla's `AfterOverlayClosed` iterates `_rewardsContainer` children directly and calls `Reward.OnSkipped()` (the model-level method) on each. So `RewardSkippedFrom` only catches the sub-screen-derived signal path. Under Model 2 we don't care (we charge in `AfterOverlayClosed` regardless of how the skip got triggered), but the asymmetry is worth remembering for B.2.2 / boss-relic / future patches.

**Known v0.1 quirk**: terminal screens with un-seen `combat_reward_ftue` route through `RewardFtueCheck` instead of actually proceeding ([NRewardsScreen.cs:444](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs#L444)). The OnProceedButtonPressed prefix runs both times the streamer clicks (FTUE trigger + real proceed). No double-charge because `AfterOverlayClosed` only fires on the real proceed.

---

## Vanilla bugs to file with MegaCrit (B.2.1 operator-validation discoveries, 2026-05-10)

- [x] ~~**Escape-from-card-select softlocks the Skip button.**~~ **Withdrawn 2026-05-11** — Surfinite re-tested by uninstalling the mod and confirmed vanilla Skip works fine after Escape→Resume from card sub-screen. The softlock was OUR bug, not vanilla's. Earlier diagnostic logs (commit `335dd49`) had me believe the gate wasn't running for this scenario, but the actual cause was: vanilla's NRewardButton.GetReward path emits `RewardSkipped` when `OnSelectWrapper` returns false (the back-out path), our `RewardSkippedFrom` postfix decremented budget on each back-out, multi-card re-eval then called `DisallowSkipping()` → Skip disabled → "softlock". Commit `bc7060f` (the back-out suppression in `_ExitTree` prefix) addresses this side-effect. The new design model (looking costs a skip) may make the Skip-disabled behaviour intended after budget exhaustion; tomorrow's confirmation pending.

## Plan B.2.1 UI / placement polish (deferred to v0.2)

- [ ] **`TwitchIrcChatService.TransitionTo` is silent on state changes.** Verified 2026-05-11 during operator-validation Step 7 auth-fail extension test: setting `oauthToken` to a one-letter-corrupted value triggered `AuthenticationFailed` state internally (confirmed via downstream `[card-vote] / [neow-vote]` chat-readiness logs showing `state=AuthenticationFailed`), but `[TwitchIrcChatService]`-tagged log lines were entirely absent from `godot.log` for the failed-connect journey. This is fine for the mod's correctness (gate degradation works) but makes diagnostic forensics harder — a streamer reporting "the bot won't work" has no easy log signal pointing at chat. Polish for v0.2: have `TransitionTo` emit a `TiLog.Info` or `TiLog.Warn` on every state change with `{old} -> {next}: {reason}` (the reason is already captured but not logged). Terminal transitions (`AuthenticationFailed`, `JoinFailed`, `Disposed`) should be Warn or Error.
- [ ] **Twitch standard ratelimit drops cancellation receipts**. Confirmed 2026-05-11 via operator-validation Step 6 sub-step B (run-abandon mid-vote): the close-receipt + cancellation-receipt pair fires within ~1s, and if a periodic-tally receipt fired in the last ~7s of the vote, that's 3 messages in ~10s. Surfinite confirmed his channel has **no** slowmode/follower/sub-only/emote restrictions ([Twitch Channel Modes screenshot 2026-05-11](../../../Pictures/Screenshots/)) — this is the standard 20-msgs-per-30s account ratelimit dropping messages during burst windows across multiple test cycles in close succession. Twitch returns a NOTICE: `Your message was not sent because you are sending messages too quickly` (only visible in `godot.log`; user-facing chat shows nothing for the dropped message). Observed drops are unpredictable across periodic tally, close receipt, or cancellation. Polish for v0.2: (a) suppress periodic tally when `TimeRemaining` is within close+cancellation slack (~5s); (b) merge close + cancellation into a composite receipt when the cancellation path is known at close time (needs VoteSession-level awareness — bigger change); (c) raise the cancellation-receipt priority from Normal to High so the queue de-prioritizes the periodic tally instead; (d) longer-term, request a verified-bot or known-bot account flag from Twitch to lift the 20/30s account-level cap.
- [ ] **Reset-receipt timing**: the `Card skips reset to N for Act M` chat receipt fires when `SetRewards_Postfix` runs — which is the FIRST rewards screen of the new act/run, after combat #1 of that act. Logically correct (that's when the tracker observes the change), but from a streamer/chat narrative perspective it lands at the end of the first combat, not at the act/run transition moment. Surfinite flagged 2026-05-11 as something to tweak during a critical-eye playthrough. Options: (a) hook an earlier signal (`RunManager.EnterAct`? `NCombatRoom._Ready`?) to fire the receipt at the transition; (b) leave as-is if the delayed receipt reads fine in practice. Also: on a fresh run, the receipt currently says `Card skips reset to 1 for Act 1`, which is a bit redundant ("of course it's reset, it's a new run") — consider suppressing when RunChanged AND humanAct == 1.
- [x] ~~**Vote option numbering across back-to-back votes — Noita pattern.**~~ **SUPERSEDED 2026-05-12 by Decision 11 in [`docs/superpowers/specs/2026-05-12-youtube-chat-integration-design-v4.md`](../docs/superpowers/specs/2026-05-12-youtube-chat-integration-design-v4.md) — vote-nonce (`!NN` suffix) chosen over Noita-style alternating numbers.** Original entry: Surfinite flagged 2026-05-11: when two card-reward votes happen back-to-back (or any two vote-bearing screens in quick succession), chat viewers with stream delay may accidentally cast a vote that lands in the *next* vote's tally because their `#0` / `#1` / `#2` collides with the new vote's option numbering. Noita's solution: the second vote numbers options `N+1`...`M` (continuing the prior vote's numbering), and a third vote falls back to `0`...`N` again — alternating ranges keep adjacent votes disjoint. **Why superseded**: alternating-numbers would break the StS1 `#0 = skip` "Skip Gang" meme by shifting skip to e.g. `#3` on alternating votes. The vote-nonce approach (each `VoteSession` gets a 2-digit ID, chatters can append `!NN` for precision, bare `#N` continues to work for the current vote) preserves Skip Gang AND solves the cross-vote collision problem for stream-delayed chatters who care. v0.2+ may revisit if bare-`#N` collisions remain widespread despite nonce availability.
- [x] ~~**Skip receipt wording**: chat currently sees `Streamer skipped a card reward (1/1 act)` at the moment `AfterOverlayClosed` fires...~~ **Resolved 2026-05-11 (plan-b-2-1/22.x)**. On-screen counter: `N card skip[s] remaining this act` (singular/plural by N==1). Skip receipt: `Streamer skipped a card reward. N remaining this act`. New act-change receipt: `Card skips reset to N for Act M` fires when the tracker detects a run or act change.
- [ ] **Vanilla "go-back arrow" on the map screen re-opens the prior rewards screen — Model 2 correctness under this path is unverified.** Surfinite identified 2026-05-11 that after Proceed-as-Skip on a rewards screen, a back-arrow appears at the bottom-left of the map screen ([Screenshot 2026-05-11 150905.png](../../../Pictures/Screenshots/Screenshot%202026-05-11%20150905.png)) that returns the player to the just-skipped rewards screen. Under Model 2 the budget was already charged on the prior Proceed click; if the streamer goes back and now claims a card via vote, the budget stays charged (no refund) — and if they sub-screen-Skip and Proceed again, the budget would be charged a SECOND time on the same logical card. **Two fixes possible**: (a) block the back-arrow when our gate is active (find the patch target — likely a method on the map screen / overlay stack); (b) implement refund-on-claim (patch `RewardCollectedFrom` to refund + send "un-skipped" receipt if the button was previously charged). v0.2 polish: pick one and ship. For v0.1, document as a known limit.

- [ ] **`CardSkipCounterLabel` placement is suboptimal.** Currently anchored at 0.62-0.98 horizontal × 0.74-0.82 vertical (right side, above the proceed button area). At default text size + default color, it appears as small text well to the left of the Skip button. Surfinite's brainstorming preference was "close to the proceed button for viewer readability". Polish needed: larger font, possibly bolder/coloured text, and tighter horizontal placement (likely AnchorLeft ~0.78 instead of 0.62). Verify on streamed playthrough at typical viewer screen size before tagging final.
- [ ] **Skip / Proceed button text observation (informational, no action needed).** Vanilla's proceed button is a single button whose label switches between "Skip" (when unclaimed rewards remain) and "Proceed" (when all rewards have been taken or skipped). Our `DisallowSkipping()` call piggybacks on the Skip-mode disable mechanic; it has no effect once vanilla transitions the button to Proceed mode (which is correct — by then no skip is needed).
- [x] ~~**Mid-vote sub-screen Skip allows abort-and-retry exploit.**~~ Resolved during operator-validation Step 4 (commit this work): added a 5th Harmony patch on `NCardRewardSelectionScreen.OnAlternateRewardSelected` that returns false from vanilla when `_voteInProgress == 1`. Sub-screen stays open until the vote's resume closes it via SelectCard. Blocks Skip / Reroll / alternate paths during vote — full closure of the see-tally-then-abort exploit. Decision 12 of the v4 spec is amended in spirit (alternates ARE patched, but only during active vote). Outside of a vote, alternates work as vanilla intends.
- [ ] **`cardSkipsPerAct: 0` strict mode still has a sub-screen Skip back-door OUTSIDE of votes.** Once a card sub-screen is open and the streamer clicks Skip without selecting a card AND no vote was triggered (e.g., they opened sub-screen but never clicked a card), vanilla skips the reward via `OnAlternateRewardSelected(DismissScreenAndRemoveReward)`. Our `RewardSkippedFrom` postfix sees the card-reward button as skipped → decrements budget per the normal flow. So in strict mode, the streamer pays one budget tick per attempt — but if budget is already 0/0, they pay nothing AND the skip succeeds. **Fix for v0.2**: extend the `OnAlternateRewardSelected` prefix added in this work to ALSO return false when `ShouldEnforceSkipGate() && !_tracker.IsSkipAllowed(settings.CardSkipsPerAct)` (i.e., budget exhausted). This would close the strict-mode back-door entirely.
- [ ] **Cancellation-receipt consistency: `IsInstanceValid` drop path doesn't send chat receipt.** `CardRewardVotePatch.ResumeOnMainThread` sends a cancellation receipt on the run-state-liveness drop path and on the options-signature mismatch path, but NOT on the `!IsInstanceValid(screen)` drop path (which is the path taken when the sub-screen is freed via OnAlternateRewardSelected). Chat sees nothing in that scenario. Small fix: add `SendCancellationReceipt()` to the IsInstanceValid drop path too.

---

## Plan B.2.1 spike findings (2026-05-10)

### Harmony patchability of Godot lifecycle methods on NRewardsScreen

- `_Ready` postfix fires: **YES** (runtime confirmed — `[SlayTheStreamer2][spike] NRewardsScreen._Ready fired` observed in godot.log when the combat-rewards screen appeared after first combat).
- `_ExitTree` postfix fires: **NO — `[HarmonyPatch(typeof(NRewardsScreen), "_ExitTree")]` raises `Undefined target method` at PatchAll time** (FATAL Init exception caught by ModEntry's outer try/catch; mod degraded to vanilla on that boot). Confirmed: `NRewardsScreen` does not declare `_ExitTree`, and Harmony's name-based lookup on the derived type does not walk inheritance. **Switched spike (commit `9678774`) to `AfterOverlayClosed()` — patches cleanly and fires reliably**; observed late, at the next-room transition rather than at Proceed click (the overlay scene-stack holds the screen briefly during Proceed → Map → next-combat handoff). Late-fire is acceptable for B.2.1's static `_activeLabel` cleanup because by the time the next `_Ready` runs for a fresh rewards screen, `AfterOverlayClosed` has already fired and nulled the static. **Production `CardRewardSkipGatePatch` (Task 13) MUST use `AfterOverlayClosed`, not `_ExitTree`.**
- Fallback for `_Ready` (not needed): if it had failed, `_EnterTree` postfix (also inherited) or `AfterOverlayShown()` (line 494, declared on NRewardsScreen) would have been the alternatives. Not relevant for v0.1.

### Reflected sts2.dll members — B.2.1 dependency surface

CardRewardVotePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` (type — public class, namespace confirmed)
- `NCardRewardSelectionScreen.SelectCard(NCardHolder)` — `private void SelectCard(NCardHolder cardHolder)` (line 255 of decompile; `NCardHolder` is `MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder`, abstract Control subclass)
- `NCardRewardSelectionScreen._options` — `private IReadOnlyList<CardCreationResult> _options` (line 98; `CardCreationResult` is in `MegaCrit.Sts2.Core.Entities.Cards`)
- Card-holder collection accessor: `private Control _cardRow` (line 96). The holders are children of `_cardRow` and are concretely `NGridCardHolder` (extends `NCardHolder`). Enumeration pattern verified in `RefreshOptions` (line 175): `_cardRow.GetChildren().OfType<NGridCardHolder>()`. For SelectCard you need the `NCardHolder` base type, so enumerate `_cardRow.GetChildren().OfType<NCardHolder>()` and index by position. Cards are added in order matching `_options` (verified at line 184: `for (int i = 0; i < _options.Count; i++) { ... _cardRow.AddChildSafely(holder); }`), so `_cardRow.GetChild<NCardHolder>(i)` aligns with `_options[i]`.
- Card title for chat receipts: `result.Card.Title` returns `string` (already formatted; handles upgrade suffix). **Spec called this `result.Card.Name.GetText()` — that chain does not exist.** Use `result.Card.Title` directly (from `CardModel.Title` property, line 92 of CardModel.cs decompile). `Card` is `CardCreationResult.Card => _modifiedCard ?? originalCard` (line 14 of CardCreationResult.cs).
- `MegaCrit.Sts2.Core.Runs.RunManager.Instance` — `public static RunManager Instance { get; } = new RunManager()` (line 62 of RunManager.cs; confirmed singleton, eagerly initialized)
- `RunManager.DebugOnlyGetState()` returns `RunState?` (declared as nullable; line 1394). Returns the private `State` field which is null when not in a run; **non-null in modded production: YES** (transitively confirmed by spike — runtime hit the rewards screen during a real run, so RunState was alive at the same instant the patch fired). Direct verification deferred but unnecessary given the structural guarantee.
- `RunState.Id` — **DOES NOT EXIST.** No `Id` property on RunState (verified by inspection of full file and grep). Spec assumed this. Closest stable run identifier: `runState.Rng.StringSeed` (string — the user-supplied seed) or `runState.Rng.Seed` (uint — deterministic hash of the StringSeed). Recommended: `runState.Rng.StringSeed` for the run-ID guard; reads cleanly and survives serialization. Conversion: already a string, no `.ToString()` needed. If a more "instance-identity" guard is wanted (distinguishing two runs with the same seed string, which is rare but possible if the same daily is re-attempted), fall back to reference equality on the `RunState` instance itself: capture `runState` at vote-start, compare `ReferenceEquals(runState, RunManager.Instance.DebugOnlyGetState())` at resume.
- `RunState.Players.Count` — `Players` is `IReadOnlyList<Player>` (line 39 of RunState.cs). `.Count` reachable; this is also already used by `NeowBlessingVotePatch.TryGetEventOwnerPlayerCount` via the `EventModel.Owner.RunState.Players.Count` chain — pattern already in production.
- Current-act access pattern: **`runState.CurrentActIndex`** is the cleanest accessor. It's a public `int` property with public getter/setter on `RunState` (line 43). `ActConsoleCmd.NextAct` writes via `RunManager.Instance.EnterAct(actIndex)` and the State exposes the index directly. The `IRunState` interface also exposes it (line 23 of IRunState.cs), so the property is part of the stable contract. `runState.Acts.Count - 1` would give the *final* act index, not the current one. `runState.CurrentRoom?.Act` does not exist on AbstractRoom in this surface. Recommended: `runState.CurrentActIndex` (0-based; add 1 for human-readable "Act N" display).

CardRewardSkipGatePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen` (type — public class, namespace confirmed)
- `NRewardsScreen._Ready()` — Harmony patches: **YES** (runtime confirmed; declared on NRewardsScreen at line 226, public override).
- `NRewardsScreen._ExitTree()` — Harmony patches: **NO** (FATAL Init exception: `Undefined target method`). NRewardsScreen does not declare `_ExitTree`; Harmony's name-based lookup on the derived type does not walk to the inherited Control/Node method. **Use `AfterOverlayClosed()` instead** (declared on NRewardsScreen at line 460; runtime-confirmed to patch and fire). Fires late (next-room transition rather than Proceed click) but the timing is fine for `_activeLabel` cleanup — see "Harmony patchability" section above.
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

Result: **YES — back-out exists via Escape → Resume.** Pressing Escape on the card sub-screen opens the global pause menu (Escape is bound to pause, not to a screen-local cancel); clicking Resume from the pause menu returns the player to the **parent rewards screen** with the card-reward item still unclaimed. From there the streamer can click Proceed to consume a skip from budget. Right-click does nothing (not a back-out path). Controller B-button untested (operator has no controller readily available); likely behaves the same as Escape since vanilla pause is global.
- **Mechanism note**: this works because the `NOverlayStack` pause-menu push pops the card sub-screen off the overlay stack first, returning the rewards screen to the top when Resume is clicked. The `NCardRewardSelectionScreen._ExitTree` override (line 230) completes its TaskCompletionSource with `(Array.Empty<NCardHolder>(), item2: false)` for the unselected-close case, so the parent rewards screen gracefully resumes with no card claimed. Structural support confirmed.

Implication for acceptance gate Mode B verification (Task 21, Step 6 sub-step): **doable**. Record sequence: open card sub-screen → press Escape → click Resume on pause menu → land on parent rewards screen with card unclaimed → click Proceed → skip is allowed (counter decrements). UX is awkward (2-step path) but real.

---

## YouTube fixture spike findings (2026-05-12)

Spike for Task 1 of [`docs/superpowers/plans/2026-05-12-youtube-chat-integration.md`](../docs/superpowers/plans/2026-05-12-youtube-chat-integration.md). Produces fixtures consumed by `YouTubeLiveChatScraperTests` in plan Tasks 17 and 18.

### Path taken: B — synthetic fixtures

Path A (capture real responses from a live broadcast) was deliberately skipped:
- The capture chain requires (a) identifying a public live broadcast currently streaming, (b) successfully hitting `youtube.com/channel/{ID}/live` → `/watch?v={V}` redirect, (c) hitting `youtube.com/live_chat?v={V}` and the `youtubei/v1/live_chat/get_live_chat` POST endpoint in sequence with the right cookies/headers, and (d) doing all of this inside the session window. Each step can fail independently (geo restrictions, age-gates, the broadcast ending mid-capture, YouTube redirect changes, cookie-consent walls). The probability of all four landing in a single session is low enough that planning around it would be a gamble.
- Path B's synthetic-fixture quality is bounded by how accurately we model YouTube's actual response shape. That shape is well-documented in three actively-maintained open-source libraries (pytchat, youtube-chat, chat-downloader), each of which has been extracting from this endpoint for years and has the shape pinned in their parsing code. The fixtures here are modelled directly on the public shape documented by those libraries.
- The fixture-realism risk is contained: if real YouTube responses don't match this shape closely enough that the regex/JSON parsing in `YouTubeLiveChatScraper` works, the failure surfaces during the first operator-validation playthrough (which exercises real YT chat end-to-end) — not later, not silently. The fix is to update the fixtures from a real capture at that point, when we have a known-good streaming broadcast on hand.

### Fixtures created

- [`tests/Fixtures/youtube_live_chat_page.html`](../tests/Fixtures/youtube_live_chat_page.html) — models the response from `GET https://www.youtube.com/live_chat?v=FIXTUREvid001`. Contains an embedded `ytcfg.data_` object (with `INNERTUBE_API_KEY`, `INNERTUBE_CONTEXT.client.clientVersion`) and an `ytInitialData` object (with `contents.liveChatRenderer.continuations[0].invalidationContinuationData.continuation` as the initial continuation token). A second `ytcfg.set(...)` call is also included for shape-faithfulness (some real responses embed both forms).
- [`tests/Fixtures/youtube_live_chat_2026-05-12.json`](../tests/Fixtures/youtube_live_chat_2026-05-12.json) — models the response body from `POST https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={API_KEY}`. Contains 4 `addChatItemAction` items wrapping `liveChatTextMessageRenderer` payloads (one plain, one with member badge via `customThumbnails`, one with `MODERATOR` icon badge AND an inline emoji run alongside the text runs, one plain) plus a `continuations[0].invalidationContinuationData` with a next continuation and `timeoutMs: 5000`.
- [`tests/Fixtures/youtube_channel_live_redirect.html`](../tests/Fixtures/youtube_channel_live_redirect.html) — added in a follow-up commit `yt-chat/1.2` because the initial spike commit omitted it despite it being listed at plan lines 68/90/167; reference-only since Task 16's `YouTubeLiveBroadcastDiscoveryTests` use a `RedirectingFakeHttp` mock that returns the `Location:` header without consuming this body, so the fixture is documentation of the watch-page markers (`<link rel="canonical">`, `og:url`, embedded `ytInitialData.currentVideoEndpoint.watchEndpoint.videoId`) a future body-inspecting discovery fallback would parse.

### Anonymization conventions used

All identifiers are synthetic placeholders following the spec's prescribed pattern:
- `authorExternalChannelId` → `UCfixture001` through `UCfixture004` (matches the public `UC...`-prefixed channel-ID shape).
- `authorName.simpleText` → `Fixture Author 1` through `Fixture Author 4`.
- `message.runs[*].text` → `Test message #0` through `Test message #3` (with the emoji-run on Test message #2 split into two text runs surrounding an emoji run, so the scraper's defensive runs[] concatenation gets exercised).
- `videoId` → `FIXTUREvid001` (the value matches the 11-char YouTube videoId convention).
- Continuation tokens, invalidation IDs, tracking params, client IDs all prefixed with `FIXTURE` or use base64-like-but-clearly-synthetic strings.
- `INNERTUBE_API_KEY` value uses YouTube's long-standing `AIzaSy` prefix (the API-key format Google has used for >10 years across all services) followed by `FIXTURE_synthetic_innertube_key_001`. Matches the regex `[A-Za-z0-9_-]+` cleanly.
- `clientVersion` set to `2.20260512.00.00.00` — matches the real 5-segment version shape YouTube uses for WEB client (e.g., `2.20240117.00.00.00`); test assertion is `Assert.Matches(@"^\d+\.\d+\.\d+\.\d+\.\d+$", ...)`, satisfied.

### Regex verification (all three scraper regexes confirmed against the HTML fixture)

Ran each regex from `YouTubeLiveChatScraper` against `youtube_live_chat_page.html` via PowerShell's `[regex]::Match`:

1. **API key**: `"INNERTUBE_API_KEY":"([A-Za-z0-9_-]+)"` → matches, captures `AIzaSyFIXTURE_synthetic_innertube_key_001`.
2. **clientVersion**: `"INNERTUBE_CONTEXT"[^{]*\{[^}]*"client"\s*:\s*\{[^}]*"clientVersion"\s*:\s*"([0-9.]+)"` with `RegexOptions.Singleline` → matches, captures `2.20260512.00.00.00`.
3. **Initial continuation**: `"(?:invalidationContinuationData|timedContinuationData)"\s*:\s*\{[^}]*"continuation"\s*:\s*"([A-Za-z0-9_=-]+)"` → matches, captures a 105-character continuation token.

### Deviation worth flagging for spec v4 / Task 18 review

**The continuation-extraction regex is brittle to JSON key ordering inside `invalidationContinuationData`.** The regex uses `[^}]*` between the opening `{` and `"continuation":` — a negated character class that bails at the first inner `}` it sees. Real YouTube responses I've seen documented from pytchat captures sometimes put a NESTED `invalidationId` object BEFORE the `continuation` field within `invalidationContinuationData`. When that ordering happens, `[^}]*` matches up to the first `}` of the inner `invalidationId` object, then can't continue, and the whole regex fails.

The fixture sidesteps this by ordering `timeoutMs`, then `continuation`, then `invalidationId` LAST inside `invalidationContinuationData` — which is one valid ordering YouTube has been observed to use, but is NOT guaranteed by the protocol.

**Implication for Task 17/18 implementation**: when capturing a real response in a future "fixture refresh" pass, if the regex starts no-matching where it used to match, the cause is likely a key-order shift. Two fixes are available:
- (a) Switch the continuation regex to a `RegexOptions.Singleline | [\s\S]*?` lazy-match form that can cross inner braces, then add a post-match validation that the captured string is base64-like (i.e., not accidentally extracting from a nested object's continuation field — but `invalidationContinuationData` doesn't have nested continuation fields, so this is safe).
- (b) Switch to a JSON-parse path for the initial-page continuation extraction (same as `PollAsync` does for the in-stream continuations). Adds ~20 LOC but eliminates the key-order fragility.

Recommend (b) for production hardening if a real-response refresh ever surfaces this issue. For v1 ship, (a) is the cheaper fix. Both are out-of-scope for this spike; documented here so the implementer of Task 17 knows the failure mode.

### csproj fixture-copy block

Added `<None Update="Fixtures\**\*"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>` to `tests/slay_the_streamer_2.tests.csproj`. Verified the fixtures appear under `tests/bin/Debug/net9.0/Fixtures/` after a fresh `dotnet build`. `FixtureLoader.Load` (Task 17.1) will find them via the relative `Fixtures/<filename>` path because the test runner sets its working directory to the test bin folder.

### Additional fixtures still needed (Task 18, not this spike)

Plan Task 18 adds three more synthetic JSON fixtures for edge-case parsing tests:
- `youtube_live_chat_paid_message.json` — one `liveChatPaidMessageRenderer` (with `#1` text) + one `liveChatMembershipItemRenderer` (no text).
- `youtube_live_chat_members_only.json` — empty `actions` array, no continuation (simulates members-only response).
- `youtube_live_chat_malformed_renderer.json` — one `liveChatTextMessageRenderer` MISSING `message.runs` (defensive-parsing test).

Those are Task 18's responsibility; this spike (Task 1) only commits the two top-level fixtures.

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

## Plan B.2.1 card reward vote (resolved 2026-05-11)

Plan B.2.1's spec is at [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](../docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) (with two 2026-05-11 amendments to Decisions 18 and 21); the implementation plan at [`docs/superpowers/plans/2026-05-10-plan-b-2-1-card-reward-vote.md`](../docs/superpowers/plans/2026-05-10-plan-b-2-1-card-reward-vote.md). Tagged `plan-b-2-1-complete`.

### Acceptance gate — all green

- [x] All B.1 regression tests pass (199 → 203 total with B.2.1's 4 new `BudgetResetReason` tests).
- [x] **Step 0** pure regression — Neow vote still works with B.2.1 patches loaded; no card-reward path exercised.
- [x] **Step 1** card vote happy path — 3 runs covering chat vote with `#N`/`N` parsing, latest-wins, close receipt with card name, VoteTallyLabel visible, skip-counter label visible near Proceed button.
- [x] **Step 2** skip used — sub-screen Skip + parent Proceed flow charges budget, chat receipt fires, counter updates.
- [x] **Step 3** skip blocked — strict mode (`cardSkipsPerAct: 0`) prevents skip at parent click; mandatory-look log fires; streamer must claim.
- [x] **Step 4** counter resets — `act 2` DevConsole jump resets `1/1 act` (4A-used and 4A-fresh paths); new run resets via run-id change (4B).
- [x] **Step 5** multi-reward-type screen — gold+potion+card combos work; mandatory-look gates the card portion specifically; non-card rewards unaffected.
- [x] **Step 6** edge cases — run-abandon mid-vote (cancellation receipt fires when not eaten by Twitch ratelimit), streamer-escape mid-vote (vote completes in background, sub-screen survives Escape→Resume — a vanilla behavior shift we lucked into), rapid clicks suppressed, dual-card-rewards via `relic PrayerWheel` work correctly under per-card-button budget, reroll via Driftwood blocked mid-vote at `CardReward.Reroll` level.
- [x] **Step 7** activation gate — `schemaVersion: 99` malformed-settings test degrades to vanilla cleanly. Auth-fail extension (Decision 21 amendment) also verified: one-character-corrupted oauthToken → `state=AuthenticationFailed` → skip-gate degrades same as malformed-settings.

### Architecture-defining outcomes

**The Model 2 transactional commit pattern is the right shape for "vote + budget" decisions in StS2.** The initial Model 1 design (immediate-charge on each sub-screen close-without-pick) felt natural from a Harmony-postfix-on-`RewardSkippedFrom` perspective, but operator-validation Step 5 surfaced a real UX trap: exploratory sub-screen Skip clicks charged budget the streamer didn't intend to spend. Model 2 (charge only when the parent Proceed click commits the screen) is symmetric, undoable until commit, and matches the player's mental model of "edit my choices until I press Proceed". For B.2.2 Ancients vote and onwards, expect to use the same shape: tentative state during the decision, committed at the screen-close button click.

**Per-button mandatory-look tracking via `NRewardButton.OnRelease` prefix is the cleanest "did the player engage with this option" signal.** Sync handler, fires before the async sub-screen opens, instance-id keyed. No need for completion-source inspection or signal-handler interception. Generalizes to any decision where "must have looked at option N before committing" is a rule.

**Vanilla's parent-Skip path does NOT emit `NRewardButton.RewardSkipped`.** The `RewardSkipped` signal fires only via `NRewardButton.GetReward`'s `OnSelectWrapper`-returned-false path (sub-screen close-without-pick). Parent's Proceed-as-Skip goes through `NOverlayStack.Remove` → `AfterOverlayClosed` → `Reward.OnSkipped()` (model-level method, no signal). Implication for any future budget-tracking patch: hook `AfterOverlayClosed` (prefix) to observe screen-level skip commits, NOT just the `RewardSkippedFrom` signal handler. The signal handler misses parent-Skip entirely.

**`OnAlternateRewardSelected` is a two-statement click handler.** Vanilla wires the reroll/skip/alternate button click as `OnAlternateRewardSelected(...)` AND `TaskHelper.RunSafely(rewardOption.OnSelect())` — TWO independent calls. Blocking only `OnAlternateRewardSelected` does nothing for alternates whose `AfterSelected` is `None`/`DoNothing` (reroll being the prime example). To fully block a reroll you must patch `CardReward.Reroll` directly. Anti-pattern to remember for B.2.2+.

### Findings worth preserving

- **Sub-screen Escape→Resume returns to the sub-screen, not the parent, under our patch set.** Spike findings (notes/06 line 78) said Escape→Resume pops the sub-screen and returns to the parent rewards screen. After our patches landed, Surfinite observed Escape→Resume keeps the sub-screen with cards visible — actually a better UX for the no-skip-during-vote principle (Step 6 verification). The behavior change is likely a side-effect of one of our `OnAlternateRewardSelected` / `_ExitTree` interactions; worth a forensic dive in v0.2 to understand whether it's intentional or accidental.
- **`OS.GetUserDataDir()` path is unchanged from B.1.** Settings at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Confirmed `ModSettings.Load` returns the expected `Malformed` variants for missing-required-field, bad-schemaVersion, and empty-credential cases.
- **Run-state liveness checks need a tiered guard** — `IsAbandoned` and `IsGameOver` are correct semantics-of-mid-vote checks, but in practice the IsInstanceValid-on-screen check fires first because vanilla teardown frees the screen before our IsAbandoned check can run. The IsInstanceValid drop path now sends a cancellation receipt (commit `1b4d3b0`) — this is the primary path for "vote ended while run was being torn down".
- **`CardReward.Title` is the canonical receipt name accessor.** The v3 spec called for `result.Card.Name.GetText()`; that chain doesn't exist. Use `result.Card.Title` (string) directly. Spike pinning (notes/06 line 50) called this out before implementation.
- **`RunState.Id` doesn't exist either.** Use `runState.Rng.StringSeed` (string — the user-supplied seed) for the run-ID guard. Confirmed during spike (notes/06 line 53).
- **`SetRewards` is the right hook for skip-gate setup, NOT `_Ready`.** `NRewardsScreen._Ready` does not populate `_rewardButtons`; it just wires UI nodes. Operator-validation Step 1 caught this with the original `_Ready` postfix → empty list → silent early-return. The plan-b-2-1/16.x commits switched to `SetRewards` postfix.
- **`HasUnclaimedCardReward` originally double-counted sub-screen-skipped buttons.** Under Model 1, the multi-card-reward re-eval branch could spuriously call `DisallowSkipping` because vanilla doesn't remove skipped buttons from `_rewardButtons` (only adds them to `_skippedRewardButtons`). Model 2 sidesteps the bug entirely by not calling `DisallowSkipping` from `RewardSkippedFrom_Postfix` at all. Lesson: if vanilla's data model splits "still alive" from "already handled", our patches need to filter explicitly — or restructure to not care.
- **Patch count for the slice: 8** (not 10 as one update mid-development suggested — `AfterOverlayClosed` is one method-target with Prefix + Postfix patches; Harmony counts it once).

### B.2.1 follow-ups (deferred to v0.2 / Plan C)

All deferred items are documented in the **UI/placement polish** section above (in this same file). Notable ones in priority-rough order:
- Vanilla map-screen go-back arrow + Model 2 commit interaction.
- Twitch ratelimit-burst handling (cancellation receipts dropped under the 20/30s account cap).
- Reset-receipt timing (fires at end of first combat, not at act transition itself).
- `TwitchIrcChatService.TransitionTo` silent-on-state-change logging gap.
- Counter label live update / pulse on tentative-skip state (Model 2 currently shows committed-only).
- Vote-option-numbering Noita pattern (back-to-back vote tally collisions under stream delay).
- Skip receipt wording was resolved live during validation (commits 22.x).

### B.2.2 readiness notes

- Boss relics are removed in StS2; **start-of-act "Ancients" picks** (community term — Pael, Tezcatara, etc.) take their place. Pael and Tezcatara are `MegaCrit.Sts2.Core.Models.Events.*` subclasses (confirmed via decompile 2026-05-12), granting `RelicRarity.Ancient` relics. Reuse the Neow vote pattern — they fire via the same `NEventRoom.OptionButtonClicked` we already patch. B.2.2 likely collapses to predicate-widening on `NeowBlessingVotePatch.IsNeowEvent`. See [`sts2_ancients`](../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_ancients.md) memory entry.

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

- **Sealed-deck draft start** — Tempus's original StS1 mod opened a run with a **sealed deck** that **the streamer drafts themselves** (corrected 2026-05-12 from FrostPrime Discord intel — earlier note said "chat drafted", which was wrong). The streamer's drafting was the "fair" tradeoff against chat's ongoing antagonism through subsequent voting. Mechanics confirmed from Discord (ArmadilloTea, Jessie, others):
  - Streamer drafts **N cards from a pool of M** (original mod was ~20×3 historically per Jessie's memory but should be configurable — defaults TBD per character).
  - **Chat picks the Neow bonus FIRST, then streamer drafts the sealed deck** — order matters so chat can't sandbag a deck commitment with a `remove 2 cards` Neow pick.
  - **StS2 already has a sealed-deck function in Custom Mode** (per ArmadilloTea: "doesnt give you a regular pick 1 of 3 neow bonus" — confirms it bypasses Neow when used standalone). Our implementation likely repurposes this: replace the Neow bonus-display step with `[draft a] Sealed Deck` option, then route through StS2's existing custom-mode deck-draft UI. Decompile investigation needed at scoping time.
  - **Character-specific must-include cards**: some characters (Necrobinder per Jessie — `Bodyguard` and `Unleash` for Osty Build) become unplayable without specific cards in the draft pool. Need a configurable per-character must-include list (simple comma-separated string in settings JSON would suffice for v1).
  - **Archaic Tooth / Orobas interaction** also flagged — without starter cards in pool, certain Orobas event interactions get forced into a single outcome. Document but probably accept for v1.
  - **Open design question (not in Discord thread)**: does chat vote on the character pick, or does the streamer choose? StS1 mod probably had streamer-chooses; defaulting to that is safe.
  - Major mode pivot — specced as a post-B.3 sub-plan, likely Plan E or its own headline "v0.2 mode pivot".
- **StS2 co-op multiplayer.** API is multiplayer-aware (`VoteCoordinator` is instance-based per Reviewer 6's catch); full multi-streamer impl deferred.
- **Subscriber/mod/VIP-only voting filters.** `ChatMessage` already exposes badge flags; future filter is a `where`-clause in `VoteCoordinator.Start` consumers.
- **Localised receipts.** Add peer static helpers (`SpanishReceipts.cs` etc.) + `Func<VoteSnapshot, ReceiptKind, string>` to `VoteCoordinator.Start`.
- **Whispering Earrings — chat plays first turn instead of Vakku.** From FrostPrime Discord 2026-05-12 (Jessie proposed, ArmadilloTea cautioned on time cost). The vanilla relic lets `Vakku` (an AI) play the streamer's first turn; the mod-flavor swap is "chat plays it instead". Two variants discussed:
  - **Vote per card**: chat votes on which card to play, then which target, etc. — flagged as too slow ("first turn might be too long").
  - **Spam mode** (Krakput): "free-for-all where chat spams numbers and it only takes one number to have that card in hand be played" — first valid vote wins, fast. Worth specifying as the primary mode; vote-per-card as an opt-in for slower streams.
  - Investigation: find what method/state Vakku-plays-first-turn hooks into; that's our patch target.
- **Chat skip-vote option (`#0` = skip on card-reward votes).** Per ArmadilloTea (2026-05-12 Discord): in the StS1 mod chat could vote skip as `#0` alongside the card options. Currently our mandatory-look + budget system gives the streamer skip agency but chat has no vote-for-skip option. ArmadilloTea also noted Surfinite's current model is fine for now ("console would be fine at first" if streamer wants to override). Future polish: add a chat-skip option that participates in the tally and applies if it wins, alongside the streamer's existing parent-Proceed gate. Likely needs a settings flag (`chatCanVoteSkip: true`).
- **YouTube chat parallel integration.** Per ArmadilloTea: FrostPrime wants YouTube chat participation alongside Twitch. Feasibility analysis done 2026-05-12 — see [`notes/07-youtube-chat-feasibility.md`](07-youtube-chat-feasibility.md) for the full writeup. **TL;DR**: doable via scraping the `youtubei` internal endpoint (~200 LOC custom scraper); architectural fit is clean (`YouTubeChatService : IChatService` + `MultiChatService` aggregator wrapping it alongside `TwitchIrcChatService`). Read-only on YouTube; receipts go to Twitch only. Effort estimate ~1–2 weeks. Five design decisions captured in the writeup; should reach out to FrostPrime when his tournament is over.
- **Events with card options outside the normal card-reward flow.** Per Jessie (2026-05-12): The Cheese Room and Brain Leach events grant card choices through their own custom screens, not `NCardRewardSelectionScreen`. Our patches don't fire on those paths. Either accept (chat doesn't vote on these) or invest in per-event patches. Likely accept for v0.1; revisit per-event if streamer feedback complains.
- **Events deliberately NOT chat-controlled (confirmed convention).** Per ArmadilloTea (2026-05-12): the StS1 mod intentionally did not let chat control event outcomes ("I think that's for the best"). Krakput flagged events like Slippery Bridge and The Trial as ones where chat control would be problematic. **Our v0.1 non-goal "Event choice voting" is correctly aligned with the original mod's design intent**, not just a scoping shortcut. Keep this principle visible when scoping v0.2 event work.
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
