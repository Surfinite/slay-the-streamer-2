# Follow-ups and Deferred Items

Living list of things flagged during sessions that need attention later. Updated as discovered; ticked off as resolved. Most recent items at the top of each section.

---

## Settings UI (resolved 2026-05-22)

First in-game settings UI for the mod. Injects a custom settings panel into the right-hand side of vanilla's `NModInfoContainer` when the player selects Slay the Streamer 2 in the mod manager. Five tunable knobs + an Open-folder button. Hot-reload between runs via `ModSettings.Current`-as-static + a 500ms-debounced atomic writer with `.bak` rolling backup. Vanilla-style row visuals (Kreon font, tickbox atlas icons, slider grabber from `scrollbar_train_large.tres`, dividers).

### Acceptance gate — 9 green, 2 deferred

Per the plan's operator-validation checklist:

- **Step 2 (vote duration applies)** ☑ slider change reflects in next-run vote
- **Step 3 (`voteOnActVariant` toggle off)** ☑ after hot-reload fix (7.1: read `ModSettings.Current` not captured-once)
- **Step 4 (`cardSkipAsVoteOption` chat skip)** ☑ after 7.3 (switched to `DismissScreenAndRemoveReward` — fixes the re-click + zero-budget softlock that `KeepReward` introduced)
- **Step 5 (`showVoteTag` display + parser invariant)** ☑ tag hidden when off; stale `!NN` still dropped
- **Step 6 (card skips per act `0 strict`)** ☑ after 7.4 (hide `CardSkipCounterLabel` when `LimitThisAct <= 0`)
- **Step 7 (persistence + unknown-key preservation + Unlimited dropdown)** ☑ after 7.4 (Godot 4's `OptionButton.AddItem(label, id=-1)` treats -1 as auto-assign sentinel; switched to `ItemMetadata`)
- **Step 8 (write-failure toast)** ☑ after 7.5–7.7 ("Failed to save settings." appears below the file-path row when the JSON is read-only; single shared hide-timer; 4s of quiet to dismiss)
- **Step 9 (Reveal in Explorer)** ☑ Open-settings-folder button shells out via `OS.ShellOpen`
- **Step 10 (mid-run inaccessibility)** ☑ confirmed greyed
- **Step 1 (co-mod interaction with Relics Reborn) — deferred** to release-prep; not part of v1 shipping criteria
- **Step 11 (full-pool boss vote on a partially-unlocked profile) — deferred** ("document edge cases" task more than pass/fail; the originally-planned `Unlock everything` button was dropped during v3 → README note documents the full-pool behaviour and the dev-console escape hatch `unlock all`)

### Known issues flagged for follow-up

- **Act-variant vote correctness bug (pre-existing, surfaced during op-val).** When chat picks an Act 1 variant (e.g. Overgrowth), the run still loads the other variant (Underdocks). Verified `Lobby.Act1` IS the reader (`StartRunLobby.cs:412` → `GetAct(Act1)`), and our resume DOES set `Lobby.Act1 = winnerKey` and removes the eager finally-restore (7.2). But `NCharacterSelectScreen.OnEmbarkPressed:484` runs `_lobby.Act1 = _actDropdown.CurrentOption` AFTER our prefix returns and our pre-Invoke write — clobbering the chat-chosen variant with the dropdown's default `"random"`. Fix path: write to `_actDropdown._currentOptionIndex` reflectively before re-invoke, OR find an earlier hook. WIP investigation lives in working-tree edits to `src/Game/DecisionVotes/ActVariantVotePatch.cs` at tag time; resume in a fresh session.

### Architectural outcomes

- **Approach B-modified locked.** Inject as a Harmony postfix on `NModInfoContainer.Fill`; the panel renders as a `ScrollContainer` over the description region with `ModImage` hidden + `ModDescription` shrunk-and-repositioned to make room. Avoids scene-tab surgery and the brittle vanilla `_tabs` reflective mutation considered in spec v1 Approach A.
- **Vanilla styling via theme-injection, not scene instantiation.** `NSettingsSlider` and `NSettingsDropdown` are abstract — can't instantiate. `NSettingsTickbox` has no Label sibling. Solution: hand-rolled plain Godot controls (CheckBox, HSlider, OptionButton, Button) with vanilla theme resources applied via `AddThemeFontOverride` / `AddThemeIconOverride` / `AddThemeStyleboxOverride`. Specific resources used: `kreon_regular_shared.tres` (labels), `kreon_bold_glyph_space_two.tres` (button), `reward_skip_button.png` (button background), `checkbox_ticked.tres` / `checkbox_unticked.tres` AtlasTextures (tickbox icons, downscaled to 40×40), `scrollbar_train_large.tres` AtlasTexture (slider grabber, downscaled aspect-preserved to 36px height). Track + fill use `StyleBoxFlat` with vertical `ContentMargin` rather than vanilla's NinePatchRect because HSlider's `slider` stylebox stretches to the full control height — colours match vanilla `self_modulate` values (`Color(0, 0, 0, 0.361)` for the dark track, `Color(0.361, 0.451, 0.459, 1)` for the teal fill).
- **`AtlasTexture.GetImage()` returns the full atlas, not the cropped region.** Critical landmine when downscaling vanilla icons — must `GetRegion(atlas.Region)` first, then resize. See `SettingsPanelBuilder.Downscale`. Without this, scaling produces a thumbnail of the entire UI atlas (mostly transparent).
- **Godot 4 `OptionButton.AddItem(label, id = -1)` sentinel collision.** The default `id = -1` is Godot's "auto-assign from index" sentinel; passing `-1` literally as our Unlimited value got reinterpreted as auto-assign → wrote 5 (the auto-index) instead. Solution: use `ItemMetadata` (Variant, no sentinel interpretation) rather than `ItemId` for value storage.
- **`CustomMinimumSize` is layout-only, not icon-size.** CheckBox icons render at the texture's native pixel size. To make them visually larger or smaller, downscale the source texture and apply via `AddThemeIconOverride`. `CustomMinimumSize` only affects the control's layout hit-box.

---

## Plan B.3.2 act-variant vote (resolved 2026-05-18)

Adds a pre-act-1 chat vote between the two Act 1 variants (Underdocks vs Overgrowth), suspending the Embark click until chat picks. Closes the cross-act-variant-boss-pool gap deferred from B.3.1 by giving the streamer's chat a way to steer the variant choice. Covers both Standard runs (`NCharacterSelectScreen`) and Custom mode (`NCustomRunScreen`).

### Acceptance gate — 15 green

All 15 plan gates passed under both Standard and Custom modes:

- **Gate 1 (vote fires on Embark)** ☑ popup appears; character-select frozen
- **Gate 2 (winner applied)** ☑ chat pick lands as Lobby.Act1; first combat scene matches
- **Gate 3 (no-winner fallback)** ☑ silent chat → custom no-votes receipt → vanilla random pick
- **Gate 4 (settings toggle off)** ☑ `voteOnActVariant: false` short-circuits cleanly; log line reworded to "skipping vote — setting disabled"
- **Gate 5 (pool degeneracy)** ☑ defensive guard active; not exercisable in vanilla 2-variant pool
- **Gate 6 (ESC cancellation)** ☑ popup tears down, cancellation receipt fires, embark button stays clickable, ESC continues to back out to main menu (after the Option B patch-target migration)
- **Gate 7 (spam-Embark guard)** ☑ second click during vote suppressed via atomic `_voteInProgress`
- **Gate 8 (pre-warm telemetry)** ☑ `pre-warm: 14/14 assets in 19–69ms (mode=L1Textures)` consistently; envelope well under any "feels slow" threshold
- **Gate 9 (Sealed Deck modifier coexistence)** ☑ vote runs alongside SealedDeck without crosstalk
- **Gate 10 (receipt delivery)** ☑ open + ≥1 tally + close all reach chat under ConnectedReadWrite
- **Gate 11 (save-quit preservation)** ☑ chat-picked variant survives Continue
- **Gate 12 (Embark→ESC→Embark cycle)** ☑ flags release cleanly; second click runs a fresh vote
- **Gate 13 (chat disconnect mid-vote)** ☑ cancellation propagates correctly
- **Gate 14 (multi-resolution popup correctness)** ☑ validated at windowed-1/3, 1080p fullscreen, ultrawide 1440. After bgHolder fix, scenes fill columns at every resolution; banners read as one continuous horizontal bar across the screen seam.
- **Gate 15 (Standard mode regression)** ☑ confirmed.

Tag `plan-b-3-2-complete` applied at HEAD on slice closure.

### Architecture-defining outcomes

- **Patch surface: `OnEmbarkPressed`, not `BeginRunLocally` (Option B).** The original plan patched `StartRunLobby.BeginRunLocally`, but operator validation surfaced a stuck-UI bug on cancel: `NCharacterSelectScreen.OnEmbarkPressed` disables embark/back/character buttons and calls `_lobby.SetReady(true)` BEFORE the vanilla call chain reaches `BeginRunLocally`. Cancel-mid-vote left the lobby in a half-mutated state with no clean restoration path. Migrating the patch up to `OnEmbarkPressed` itself means: on suspend, vanilla never touched the UI, so cancel is a clean no-op. On confirm, we set `Lobby.Act1` and reflectively re-invoke the same method with `_resumeInProgress=1` set so our prefix passes through and vanilla's full body runs unmodified. This is the same pattern any future "vote on click" feature should follow — patch the click handler, not the downstream consumer.
- **Multi-target Harmony pattern**: Custom mode has its own `NCustomRunScreen.OnEmbarkPressed(NButton)` parallel to `NCharacterSelectScreen.OnEmbarkPressed(NButton)`. Same signature, same shape (`Disable` UI → `SetReady(true)` → eventually `BeginRunLocally`). `TargetMethods()` returning both `MethodBase`s with shared `Prefix` + small type-dispatch (`GetLobby` / `GetOnEmbarkPressedMethod`) covers both with zero duplication. Future "intercept Embark click in any mode" feature should reuse this pattern; daily-run + multiplayer-load screens would slot in identically.
- **`VoteSession.Cancel()` fires `Cancelled`, NOT `Closed`**: surfaced when the popup teardown didn't fire on ESC despite the patch-side flow logging "vote cancelled". Both events are independent terminal states; any popup needs to subscribe to both (and route both through a dispatcher-marshaled handler because `Cancelled` can fire from the chat-parser thread on disconnect). Now captured as CLAUDE.md Tier 4.
- **`NCombatBackground` parent must be a Center-anchored zero-size Control**: mirrors vanilla `BgContainer` in `combat_room.tscn:41-50`. The visual's internal `Layer_NN` offsets are calibrated for a viewport-center origin frame, NOT top-left. Anchoring to `FullRect` shifts the texture's center off-screen by half a viewport and clips most of it. The fix is one Control: `bgHolder.SetAnchorsAndOffsetsPreset(LayoutPreset.Center)`. Documented as CLAUDE.md Tier 4 because anyone trying to embed a vanilla combat scene in a custom UI will hit this.
- **Vanilla act-banner styling now reusable**: `ActVariantVotePopup.ApplyTitleTheme` / `ApplyActNumberTheme` mirror `act_banner.tscn`'s font choices (Spectral Bold gold for titles, Kreon Regular light blue for the smaller "Act N" / countdown text) with the source values centralized as const fields at the top of the popup class. Any future popup that wants the StS2-native look (vs RichTextLabel default) can reuse the constants + helpers directly. Closes the cross-vote font-swap follow-up flagged in B.3.1 for the act-variant popup specifically; the other vote popups (B.1 Neow / B.2.1 card / B.2.2 ancient / B.3 boss) still want the cross-cut polish slice noted in B.3.1.
- **Centralized "skipping vote — <reason>" log phrasing**: replaces "bailing to vanilla" which collided with the chat-fully-disconnected log line and made log triage harder for the operator. New phrasing is unambiguously slice-local. Future per-slice bail logs should follow the same convention.
- **`OperationCanceledException` cancel is Debug, not Error**: when the popup calls `_session.Cancel()`, the propagation through `await session.AwaitWinnerAsync()` is an `OperationCanceledException` — the expected user-abandon flow, not a bug. Log severity: Debug. Reserve Error for genuinely unexpected exceptions.

### Findings worth preserving (spec/spike corrections discovered during operator validation)

- **Asset paths for combat backdrops**: the original Task 1 spike picked `MapMidBgPath` (`images/packed/map/map_bgs/<v>/map_middle_<v>.png`) which is the **map screen strip**, not the combat backdrop. The actual combat scene path is `res://scenes/backgrounds/<key>/<key>_background.tscn` built up by `BackgroundAssets(key, rng) + NCombatBackground.Create(bg)`. The map-bg vs combat-bg distinction is now documented in `notes/asset-extraction.md`.
- **Full asset extraction transformed asset-discovery accuracy**: `notes/asset-extraction.md` documents GDRE Tools extraction of the full `.pck`. Once the assets exist on disk, grep against actual paths replaces guessing `res://` URLs. This is now a permanent workflow tool, not B.3.2-specific.
- **Custom mode is a separate Harmony surface**: `NCustomRunScreen` parallels `NCharacterSelectScreen` for every embark-related concern. Any future "do X on Embark in any mode" patch must `TargetMethods` both. Single-target patches that only hit `NCharacterSelectScreen` silently miss Custom runs.

### Follow-ups / deferred items

- **FTUE-cancel corner case**: on a profile that hasn't seen the accept-tutorials FTUE, vanilla's `OnEmbarkPressed` disables the embark button and shows a modal at line 472-480, returning early. If the user accepts the modal, `OnEmbarkPressed` recurses; our prefix fires fresh and the vote runs. If the user then cancels THAT vote, the embark button is left disabled from vanilla's pre-modal disable, same stuck-UI shape the cancel-under-BeginRunLocally bug had. Narrow (first run on fresh profile + voluntary cancel) and not a regression from vanilla's own behavior in that path. Accept-as-documented per CLAUDE.md's "design as if streamer has unlocked everything." Captured in the patch class doc and the `ActVariantVotePatch.cs` class header.
- **Cross-vote font/text-alignment polish slice**: B.3.2 mirrors vanilla act-banner styling within its own popup, but the other vote popups (B.1 Neow / B.2.1 card / B.2.2 ancient / B.3 boss) still use RichTextLabel defaults. Surfinite's preference (carried over from B.3.1) is one dedicated polish slice that touches all popups uniformly. Tally label in the act-variant popup was deliberately left at default theme for this same reason — pending the cross-vote pass.
- **Act-2+ variant vote**: when MegaCrit ships variant alternates for Act 2 / 3 / 4, the B.3.2 architecture generalizes by parameterizing the candidate-builder on act index. Right now `ActVariantVoteResolver.BuildCandidates()` is hard-coded for Act 1. Easy lift when needed.
- **Test isolation `[Collection("TiLog.Sink")]` gap (still pre-existing)**: same `VoteSessionTests.Closed_WithoutAwait_LogsWarn` flake B.3.1 noted resurfaced once during this slice's commits. Re-runs always pass. Still not caused by B.3.2; still documented in CLAUDE.md Tier 1.

---

## Plan B.3.1 combat-idle boss portraits (resolved 2026-05-16)

Replaced B.3's static PNG portraits with animated combat-idle sprites rendered via `MonsterModel.CreateVisuals()`, fixing the empty-column bug for Spine-only bosses (Ceremonial Beast pre-fix; future Spine-only bosses post-fix).

### Acceptance gate — 21 green, 2 skipped with rationale

All B.3.1 gates passed:

- **Visual correctness (gates 1–6)**: ☑ all bosses render animated combat sprites, Ceremonial Beast renders (the headline bug fix), all 9 placeholder bosses render correctly. Bounds-aware centering keeps each sprite vertically anchored regardless of native-origin convention. Soul Fysh's idle oscillation no longer clips with the 6%-per-side fit margin (0.88).
- **Lifecycle correctness (gates 7–12)**: ☑ ProcessMode.Disabled cascade freezes Spine playback during pause-menu / dev-console occlusion (gate 7 visual-smoothness check passed). Run abandonment, save-quit-and-Continue, and PhobiaMode toggle all degrade gracefully without crash. Multi-monster encounters log primary-monster pick at Info level for observability.
- **Coverage (gates 13–16)**: ☑ Act 1 Overgrowth, Act 2 Hive, Act 3 Glory all validated. Golden Compass two-chest scenario correctly hits B.3's idempotency guard and skips the second vote; the `_lastSwappedBossId` triad is unchanged from B.3.
- **Hardware envelope (gates 17–20)**: ☑ Pre-warm Stopwatch baseline `pre-warm: 3/3 candidates in 76–82ms` across runs — well under any "harmless" threshold. Resolution coverage validated at 4:3 small testing window, 1080p widescreen, 1440p ultrawide fullscreen (with the post-fix 448×448 slot size).
- **Build pipeline (gates 21–24)**: ☑ `build.ps1` + `install.ps1` clean; godot.log version hash matches HEAD on every cycle; only `[boss-vote]` log lines under normal flow are Info-level (Stopwatch + multi-monster picker observations).
- **Gate 18 (cold-load Thread.Sleep simulation)**: skipped — pre-warm baseline of 76–82ms is consistently low across multiple runs, and gate 17's organic measurement makes the synthetic latency exercise low-value. Documented as accepted skip.
- **Gate 19 (second-hardware envelope)**: skipped — no accessible second machine. Gate 17's organic timing is the substitute data point.

Tag `plan-b-3-1-complete` applied at HEAD on slice closure.

### Architecture-defining outcomes

- **ProcessMode.Disabled cascade pattern (reusable)**: setting `ProcessMode.Disabled` on a parent Control halts `_Process` on all children whose ProcessMode is `Inherit` (the default). For Spine-rendered NCreatureVisuals children, this freezes playback without reaching into MegaSpine's animation state — no `SetTimeScale(0)` API call, no typed reference exposure. Driver: the popup's occlusion probe (`_isOccludingOverlayVisible`), since per CLAUDE.md Tier 4 `SceneTree.Paused` is never toggled by StS2's pause menu. **Any future mod UI rendering MegaCrit creatures + needing pause-aware freeze can reuse this pattern.** Reference implementation: `BossVotePopup._Process` occlusion block.
- **Bounds-aware centering pattern**: `NCreatureVisuals` origins are typically at the creature's feet, not the visual center, so naive `visuals.Position = slot.Size * 0.5f` floats the body upward. The correct formula is `visuals.Position = slotSize * 0.5f - boundsCenter * fit` where `boundsCenter = Bounds.Position + Bounds.Size * 0.5f`. Read via the typed private static `GetVisualBoundsRect(Node2D)` helper.
- **TI/Game seam framing**: public-interface MegaCrit-free, with localized typed private static helpers (`GetVisualBounds`, `ApplyScaleAndHue`) for the cast-and-call sites. Honest version of "absolute seam" — TI extraction would touch ~6 lines of helper bodies, not the public popup API.
- **Variant B pre-warm timing (vote-start, synchronous main-thread)**: load hitch lands between Proceed-click and popup-appearance rather than during the visible vote timer. Stopwatch telemetry confirms 76–82ms across runs — well below any "feels broken" threshold. Variant C (chest-room-enter postfix) remains on the table for a follow-up if community reports surface hitch on slower hardware.
- **`PortraitFit.ComputeFitScale` carve-out**: pure-math fit-scale calculation extracted into a Godot-free, MegaCrit-free static helper for unit-testability. 6 `[Theory]` cases including zero/negative bounds. Surgical `Compile Include` in test csproj (one line) avoids pulling other `src/Game/Ui/*` files into the test project.
- **rerollvote dev console command**: re-opens current boss vote with a fresh sample. Generation-tracking via static `_voteGeneration` + `_rerollSalt` so the cancelled session's stale resume bails cleanly without applying a swap or firing the synthetic Proceed re-click. Pre-flight pool check leaves the existing vote intact on degenerate-pool failure.

### Findings worth preserving (spec/spike corrections discovered during operator validation)

The v3 spec was internally consistent but several assumptions about vanilla didn't survive contact with `godot.log`. Captured here so the next slice doesn't repeat them:

- **Multi-monster bosses exist in the current build**: three of them (`KAISER_CRAB_BOSS`, `QUEEN_BOSS`, `THE_KIN_BOSS`). The Round-1 spike's "all current act bosses are single-monster" finding was wrong — the spike checked Spine availability, not encounter monster count. PickPrimaryMonster's special-case branch handles `THE_KIN_BOSS → KIN_PRIEST`; `QUEEN_BOSS` and `KAISER_CRAB_BOSS` happen to have the visual primary at index 0 by luck.
- **`ModelId.Entry` is UPPER_SNAKE_CASE, not PascalCase**: e.g., `THE_KIN_BOSS` not `TheKinBoss`, `KIN_PRIEST` not `KinPriest`. The v3 spec used PascalCase in code samples; first implementation pass used PascalCase too; operator validation log capture (`[boss-vote] encounter THE_KIN_BOSS has 2 monsters; rendering primary KIN_FOLLOWER`) was what revealed the format mismatch. Always verify identifier formats against actual runtime logs, not class names.
- **`ActModel.AssetPaths` does NOT include monster combat scenes**: it includes act-level paths (background, map nodes, ancient assets) but not `creature_visuals/<id>.tscn`. So our pre-warm IS the cold load — not a redundant cache prime as the v3 spec implied. The vanilla `Asset not cached:` warns in `godot.log` for monster scenes are MegaCrit's signal that vanilla didn't pre-load them, harmless if rare. Our pre-warm Stopwatch numbers (76–82ms) are the real cold-load cost.
- **`MonsterModel.AssetPaths` access throws `"Canonical model ... used in incorrect place"`** for bosses with mutable internal state inside their `GenerateMoveStateMachine()` (Ceremonial Beast's `BeastCryState` assignment is the canary). Don't iterate `AssetPaths` for pre-warm — build the scene path directly via `SceneHelper.GetScenePath("creature_visuals/" + monster.Id.Entry.ToLowerInvariant())`. The pure `VisualsPath` accessor is `protected`, hence the manual reconstruction.
- **Act-variant pool size = sample size for Act 1**: each Act 1 variant ships exactly 3 bosses (Underdocks: Waterfall Giant / Soul Fysh / Lagavulin Matriarch; Overgrowth: Vantom / Ceremonial Beast / The Kin). `BossCandidateSampler.SampleDistinct(pool, count: 3, rng)` from a 3-pool is a reshuffle — `rerollvote` only changes column ORDER for Act 1 in a given run, not the set. See deferred follow-up section above for the two paths to address this.
- **Kaiser Crab is composite, not blank**: Crusher's combat scene loads correctly; it just renders as a single claw (Crusher is one of two claws flanking the player in combat, with the Kaiser Crab "face" being the background). The popup shows the claw alone, which reads as small/unrecognizable. Not a code bug — would need cross-scene compositing or boss-specific background pull to resolve. Documented as deferred.
- **Test csproj `src/Game/Ui/*` is excluded by default** — only `src/Ti/Internal/`, `src/Ti/Chat/`, `src/Ti/Voting/`, `src/Game/Bootstrap/`, `src/Game/DecisionVotes/` are auto-included. Unit-testable helpers in `src/Game/Ui/` need explicit `<Compile Include="..\src\Game\Ui\X.cs" />`. PortraitFit had to be `System.Numerics.Vector2`-typed (not `Godot.Vector2`) because the test project has no Godot reference.

### Follow-ups + observations (deferred to v0.2+)

- **Cross-act-variant boss pool OR pre-act variant vote** — already documented in the deferred section above (this same file, just below). Surfinite's current lean: Option 2 (pre-act variant vote). Defer until B.3.1 ships; can start as B.3.2 or similar.
- **Kaiser Crab composite rendering**: would need to render both Crusher and Rocket side-by-side OR pull in the boss-specific background. Significant work for a single boss; revisit if MegaCrit ships a unified Kaiser Crab bestiary image.
- **Font swap across all vote popups (B.1 Neow, B.2.1 card reward, B.2.2 ancient, B.3 / B.3.1 boss)**: Surfinite wants the default game font instead of the current RichTextLabel default. Cross-vote concern — belongs in a dedicated polish slice that touches all popups uniformly, not B.3.1.
- **Text alignment polish across B.1 / B.2.1 / B.2.2 popups**: same dedicated polish slice as the font swap.
- **Bigger ultrawide column packing**: Surfinite noted "ultrawide is 95% good ... I'd squish them together a little" — but specifically said "no point introducing a case for a resolution no one is going to stream at." Accept as known-shippable.
- **Lagavulin "asleep" idle**: Lagavulin's idle animation starts in a sleep pose in combat; the popup shows the same sleep pose. Vanilla behavior — keep consistent with bestiary. Revisit if MegaCrit changes the default bestiary animation.
- **Test isolation `[Collection("TiLog.Sink")]` gap (pre-existing)**: operator validation rebuilds occasionally surfaced the `Collection was modified during enumeration` flake in `VoteSessionTests.Closed_WithoutAwait_LogsWarn`. Not caused by B.3.1; documented in CLAUDE.md Tier 1 as a known parallel-test-collection issue. Adding the missing collection markers to the affected test classes is a separate small slice.
- **Pre-warm escalation to Variant C (chest-room-enter postfix)**: documented above as a fallback if hitch ever becomes user-visible. Use `PreloadManager.LoadActAssets` (the vanilla async preload entry-point that doesn't fire missed-cache warns) as the implementation path. Currently no observable need.

---

## Plan B.3.1 boss-vote pool / act-variant expansion (deferred; identified 2026-05-16)

During B.3.1 operator validation, Surfinite observed that `rerollvote` always returns the same 3 bosses because the act's `AllBossEncounters` pool size equals the sample size (3 of 3). Each Act 1 variant ships a fixed 3-boss roster:

| Act variant | Bosses |
|---|---|
| Act 1 Underdocks | Waterfall Giant, Soul Fysh, Lagavulin Matriarch |
| Act 1 Overgrowth | Vantom, Ceremonial Beast, The Kin |
| Act 2 Hive | The Insatiable, Knowledge Demon, Kaiser Crab |
| Act 3 Glory | Queen, Test Subject, Doormaker |

The vote is functionally a reordering exercise rather than a multiple-choice draw. Two follow-up options were considered:

**Option 1 — Cross-variant pool at boss-vote time** (~1 day if cross-variant boss swap works cleanly; ~2–3 days if it doesn't): union Underdocks + Overgrowth bosses when on Act 1, so the chest-room vote draws from a pool of 6. Spike needed: confirm `MapCmd.SetBossEncounter` of a cross-variant boss doesn't break `HasScene` boss-specific scenes (TheKin/Queen/KaiserCrab) or `LoadActAssets` preloading. The data layer is permissive — `_rooms.Boss` doesn't validate variant binding — so the risk is downstream (cold-load hitch, custom BGM/background transition).

**Option 2 — Pre-act variant vote** (~3–5 days; B.1-sized slice): new vote at run start where chat picks Underdocks or Overgrowth. New Harmony target (find vanilla's variant-selection point in `RunManager`/`ActModel.Generate`), new vote orchestration, possibly new 2-column popup or reuse `BossVotePopup` with different title. Vanilla-ish UX — chat picks the variant, in-act content stays normal.

**Surfinite's current lean (2026-05-16)**: **Option 2**. More vanilla-feeling, sets up a generalisable pattern for any future "act-flavor" votes. Defer until B.3.1 ships.

---

## YouTube chat parallel integration — acceptance gate results (partial, 2026-05-12)

Operator-validation run against `yt-chat-v0.2` work. Both currently-runnable steps **PASSED**; remaining steps deferred pending FrostPrime contact or alternative live YT broadcast access.

- [x] **Step 0 — Vanilla regression** (Twitch-only, new code path). Mod loaded at `344db72`. New aggregator + IChatConsumer split + B.2.1 skip-gate-routing-via-GetChildState proven non-regressing.
- [x] **Step 1 — YT-only smoke** (valid `youtubeChannelId` + deliberately bad `oauthToken`). Validated end-to-end against FrostPrime's live channel `UCnrdFUk_XfPJooztStcHG4g` (videoId `II6NztxNhEQ` at the time). Surfinite's `1` chat message → counted as YT vote → applied to in-game state. Card-skip gate degraded correctly. No chat receipts fired.
- [ ] **Step 2 — Dual-platform happy path** (3 runs). Deferred: requires valid Twitch + valid YT both live. Surfinite needs to contact FrostPrime before doing more live YT testing, OR use own/friend's stream.
- [ ] **Step 3 — YT failure modes** (sub-steps 3a-3e). Partial offline-doable: typo'd channel ID (3c) can be tested anytime. 3a/3b/3d/3e require live broadcast control.
- [ ] **Step 4 — Split tally label correctness**. Deferred (needs dual-platform vote in flight).
- [ ] **Step 5 — Cross-platform double-count** (D1). Deferred (needs control of both platforms).
- [ ] **Step 6 — Twitch-only-deployment + D6 settings parsing**. Doable offline anytime — pure settings-JSON variants.
- [ ] **Step 7 — Receipt flap suppression + delivery**. Deferred (would need triggered YT state churn).

### Fixes that landed during operator-validation Step 1

Four real-world bugs surfaced and were fixed live during the Step 1 run (Surfinite + Claude pairing):

1. **`yt-chat/15.2`**: `CONSENT=YES+cb` cookie sent via `DefaultRequestHeaders.Add("Cookie", ...)` instead of `CookieContainer.Add(Uri, Cookie)`. The container path silently dropped the cookie when `Cookie.Domain` had a leading dot — known .NET quirk. Symptom: request landed at `consent.youtube.com` instead of channel page; body length 556KB vs the expected 1.1MB.
2. **`yt-chat/16.2`**: `YouTubeLiveBroadcastDiscovery` body-parses `<link rel="canonical" href="https://www.youtube.com/watch?v=VIDEOID">` instead of inspecting `RequestMessage.RequestUri` after redirect. YouTube no longer redirects `/channel/{ID}/live` → `/watch?v=...` (verified live; they now serve the live page directly with canonical link in the body).
3. **`yt-chat/17.2`**: `ContinuationRegex` extended to accept `reloadContinuationData` as a third container type (alongside `invalidationContinuationData` and `timedContinuationData`) AND to include `%` in the character class (URL-encoded `%3D` padding is the actual byte sequence in live tokens). Symmetric fix to `ExtractContinuation` JSON path inside `PollAsync`.
4. **`yt-chat/29.2`**: Vote-start gate in `NeowBlessingVotePatch` / `CardRewardVotePatch` loosened from `is ConnectedReadWrite` to `is (ConnectedReadWrite or ConnectedReadOnly)`. The v4 spec's "votes flow in YT-only mode" promise was being defeated by the existing patches which gated on the stricter CRW (Twitch-can-send). Receipt-send sites kept strict CRW because they actually invoke `SendMessageAsync` and correctly need to no-op in YT-only mode.

### Findings worth preserving

- **YouTube's `/channel/{ID}/live` no longer redirects to `/watch?v=...`** as of 2026-05-12. The live videoId is embedded in the page body. Likely a YouTube change within the past ~year. Discovery via body-parse is the right shape going forward.
- **`.NET CookieContainer.Add(Uri, Cookie)` with `Cookie.Domain` having a leading dot silently fails**. Direct header (`DefaultRequestHeaders.Add("Cookie", ...)`) is the robust workaround — this matches what every cross-language scraper library does anyway.
- **YouTube uses three continuation container types**: `reloadContinuationData` (initial page), `invalidationContinuationData` (steady state, most common), `timedContinuationData` (steady state, alternate). All three should be matched.
- **YouTube member badges live in `authorBadges[]`, NOT `message.runs[]`**. Visual `#3 / #4` member-level pills don't appear in the chat-message text our scraper extracts; no risk of badge-as-vote false positives.
- **Scraper version log line** (`[YouTubeLiveChatScraper] scraper yt-scraper-2026-05-12-a active; tracking videoId=...`) is genuinely the primary debug signal during operator-validation. Keep it.

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

## Plan B.3 boss vote (resolved 2026-05-15)

Plan B.3's spec is at [`docs/superpowers/specs/2026-05-13-plan-b-3-boss-vote-design-v3.md`](../docs/superpowers/specs/2026-05-13-plan-b-3-boss-vote-design-v3.md); the implementation plan at [`docs/superpowers/plans/2026-05-13-plan-b-3-boss-vote.md`](../docs/superpowers/plans/2026-05-13-plan-b-3-boss-vote.md). Slice tagged `plan-b-3-complete`. Implementation done subagent-driven across the original 7 tasks (`plan-b-3/1.1` through `7.1`), then four operator-validation-found bugfixes (`6.2` portrait-load spam, `6.3` dev-console occlusion, `6.4` `ui_cancel` swallow + pause-menu probe, `6.5` corrected submenu-stack probe, `6.6` cancel session on run-dying, `7.2` Golden Compass / back-arrow idempotency, `7.3` marker-clear on run-dying (later reverted), `7.4` silent-restore of swap on save-quit-rollback). Initial Stop-hook chaos from a stale `claude-mem` v10 also surfaced and forced an upgrade to v13.2.0 mid-slice; documented separately in the dev-tools memory.

### Acceptance gate — 7 green, 2 deferred

- [x] **Smoke A** — Act 1 happy path. Standard run, chest exit, popup with 3 candidates, chat votes via `!vote #N`, top-bar boss icon updates, walked to Act 1 boss → expected fight.
- [x] **Smoke B** — Acts 2 + 3 non-DoubleBoss coverage. Same flow on both acts.
- [x] **Smoke C** — A10+ DoubleBoss exclusion. Log line `HasSecondBoss=true; excluding {id}` fires, popup's 3 candidates omit the pre-rolled second boss, primary ≠ second after the swap. **Highest-risk smoke per the holistic review; passing this confirms `ModelId` value-equality + `HasSecondBoss` timing are correct.**
- [x] **Smoke D** — run abandoned mid-vote. Pause menu reachable via ESC (after `6.4` unblocked `ui_cancel` and `6.5` corrected the submenu probe), Give Up → confirm → run abandons. `[boss-vote] resume aborted: run is over (player dead)` fires, `Vote result ignored — run abandoned during boss vote` queued (subject to Twitch rate-limit, see below). Popup vanishes (after `6.6` cancels session on run-dying), Continue / Main Menu buttons clickable normally.
- [x] **Smoke E** — chat disabled. With `chatService.State` not in {`ConnectedReadOnly`, `ConnectedReadWrite`}, prefix bails to vanilla, Proceed click flows normally, no popup.
- [ ] **Smoke F** — multiplayer bail. **Deferred** — Surfinite has no MP test environment available at acceptance-gate time. Code path is independently verified by reading `TryGetPlayerCount` + the early-bail guard; matches `CardRewardVotePatch` shape which has been MP-validated.
- [ ] **Smoke G** — first-defeat achievement check. **Deferred** — target audience are unlocked-everything streamers per the spec (notes/10), and modded saves are separate save files from regular saves, so first-defeat-loss risk doesn't realistically apply. If a first-discovery streamer ever runs the mod and reports a missed achievement, revisit; otherwise this gap is irrelevant.
- [x] **Smoke H** — save-reload determinism + popup persistence. Seed-determinism confirmed (same `seed=-1302187658`, same 3 candidates in same order on re-vote within one run). Save-quit-and-Continue scenario also validated after `7.4`'s silent-restore fix: the saved runState rolls back the boss swap, but `_lastSwappedBossId` lets us re-apply the chat-picked boss silently without re-voting. Streamer transitions to map with the original chat-picked boss restored; chat sees no new messages.
- [x] **Smoke I** — relic-collection overlay mid-vote. Mouse clicks no-op as intended (backdrop's `MouseFilter = Stop`); pressing `D` opens the deck overlay behind the popup which is fine (overlay visible but un-actionable while the modal is up).

### Architecture-defining outcomes

**Modal CanvasLayer popup is a viable mod-UI pattern with three load-bearing rules.** B.3 is the first slice to ship a modal overlay (prior slices used the non-modal corner `VoteTallyLabel`). The three rules the operator-validation smokes hammered out:
1. **Be ready to yield the screen.** Any vanilla overlay we don't want to occlude — dev console, pause menu, settings, submenu modals — needs an explicit probe. The seam is `Func<bool>? isOccludingOverlayVisible` on the popup; the patch supplies `NDevConsole.Instance?.Visible || NRun.Instance?.GlobalUi?.SubmenuStack?.Stack?.SubmenusOpen ?? false`. **`SceneTree.Paused` is NOT viable** as a pause-detection probe — StS2 uses `RunManager.ActionExecutor.Pause()` for its pause concept, never toggling Godot's `SceneTree.Paused`.
2. **Be ready to bail when the run dies.** Mid-vote abandon / game-over / save-quit need to cancel the session promptly, otherwise the popup persists through scene transitions for up to 30s waiting for the vote timer. The probe is `Func<bool>? isRunDying`; patch supplies `RunManager.IsAbandoned || runState.IsGameOver || nulls`. Triggers `session.Cancel()` which the existing `Cancelled` handler converts into `SafeQueueFree`.
3. **Be careful about input swallow scope.** `ui_accept` (Enter/Space/gamepad-A) is the intended swallow target — prevents accidental confirmation of the underlying button. `ui_cancel` (ESC) MUST be left alone — it's the pause-menu shortcut and blocking it makes the streamer unable to abandon the run for the vote's full 30s window. The v3 spec specified both swallows; operator validation showed `ui_cancel` was over-zealous.

**Idempotency-with-verification handles all known re-entry paths.** The patch tracks `(_lastSwapRunId, _lastSwapActIndex, _lastSwappedBossId)` after each successful resume. On subsequent Prefix calls within the same run+act, the idempotency check compares against current `Act.BossEncounter.Id`:
- Match → skip vote (Golden Compass second chest, map back-arrow).
- Mismatch → save-quit rolled back the swap; silently re-apply the recorded swap via `ApplyBossSwap(runState, lastSwappedBoss)` and let vanilla Proceed run. Chat doesn't re-vote.
- Marker null (no-winner from prior vote, or boss not in pool, or restore throws) → fall through to a fresh vote.

This pattern would generalize to any future vote slice that suspends-and-resumes on a vanilla mutation: tracking what was applied + verifying it survived is the right shape, not just tracking that "a vote happened."

**Spike-as-Task-1 caught two plan errors before they bit the build.** Task 1 spike (decompile verification of 10 open questions) was structured to produce a notes file BEFORE any code shipped. It corrected two load-bearing wrong-assumptions in the plan: `NTreasureRoom.OnProceedButtonPressed` is private with an `NButton` parameter (plan assumed parameterless public — Task 7 ended up needing reflective invoke), and `NButton` lives in `MegaCrit.Sts2.Core.Nodes.GodotExtensions` not `MegaCrit.Sts2.Core.Nodes`. Also caught later by Task 7's implementer agent: `EncounterModel.Id` is a `ModelId` record not a `string`, so the SecondBoss exclusion uses value-equality (matching `ActModel.cs:289`'s vanilla pattern). The spike-first discipline is keeping its weight every time we use it.

### Findings worth preserving

- **Spine-only bosses ship no PNG fallback.** Ceremonial Beast's empty popup column wasn't a bug — `EncounterModel.MapNodeAssetPaths` returns either the Spine `.tres` OR two PNGs, never both. Bosses with full Spine animations (Ceremonial Beast) explicitly don't override `BossNodeSpineResource`; bosses still using placeholder art (Soul Fysh / Vantom / The Kin / Waterfall Giant / Lagavulin / Doormaker / Kaiser Crab / Knowledge Demon / Test Subject) DO override it to return null + point `BossNodePath` at `res://images/map/placeholder/<id>_icon`. Identical logic in both `sts2/` and `sts2-beta-snapshot/` decompiles — MegaCrit's Spine migration is content-side, not code-side. As they ship more Spine art, more bosses will lose the placeholder PNG fallback, so the popup's empty-box surface area will grow over time unless we add Spine support. Listed as a polish slice in v0.2+.
- **Map button bypass surfaced as a chat-vs-streamer asymmetry hole.** The top-right map button in the chest room lets the streamer advance to map without clicking Proceed — bypassing the boss-vote patch entirely. The streamer can pick the next room from the map and the chat-vote never fires. By design (the patch targets `OnProceedButtonPressed`), but listed as polish-worthy in v0.2+.
- **Twitch's 20/30s account-level rate limit is real and reproducible.** A normal 30s boss vote produces open + ~4 periodic tallies + close + (sometimes) ignored-result — 5-7 messages. Bursts close to the cap, and the ignored-result receipt is the one most likely to be dropped (it fires last). The existing `TwitchIrcChatService` warning `Your message was not sent because you are sending messages too quickly` is the silent-drop signal. Already a tracked v0.2 polish item per CLAUDE.md.
- **Save-quit can predate mid-room mutations.** StS2's save snapshot point appears to be earlier than `MapCmd.SetBossEncounter`'s call site — save-quit-Continue rolls back the swap. This is why `7.4`'s silent-restore mechanism exists. **Generalizes**: any future patch that mutates `runState` mid-room needs a similar "remember what we did + verify on next prefix call" pattern, OR needs to find a way to commit at a save-checkpoint boundary.
- **Multi-version claude-mem misadventure consumed half a session.** Stale `claude-mem` v10.0.7 had a Stop-hook bug that flooded the chat with "Transcript path missing" errors for hundreds of consecutive turns. Forced an upgrade to v13.2.0 mid-slice (12.7.3 changelog fixes the exact bug). Surfaced an unrelated gap: third-party marketplaces in Claude Code don't auto-refresh, and `thedotmack/claude-mem`'s `main` branch is also stale relative to tags. Worth flagging upstream to both.

### Follow-ups + observations

Per-task code-quality nits surfaced in subagent reviews during implementation, plus operator-validation findings. **None blocked the slice tag; all are explicit followups for a future polish slice.**

Production-side polish:
- **BossVoteSeed** plan's known-value test had the wrong expected value (`-1424385571`) — actual canonical FNV-1a-32 result for `("abc", 0)` is `1781783633`. Implementer caught it during Task 2 and corrected the test, not the implementation. Plan v3 should be amended if anyone re-reads it.
- **BossCandidateSampler** silently clamps negative `count` to empty instead of throwing `ArgumentOutOfRangeException`. Inconsistent with the null-arg guards in the same method.
- **BossVoteResolver** could use a one-line comment on the `(uint)winnerIndex >= (uint)options.Count` idiom; `[Theory]+[InlineData]` would tighten the three valid-index tests.
- **BossVotePopup.LAYER_INDEX** is `public const` on an `internal sealed` type — should be `internal const`. Cosmetic.
- **BossVotePopup._Process** allocates a new `Dictionary` snapshot per dirty frame because it calls `_session.Tallies` inside the per-option loop. Hoist outside the loop (matches `VoteTallyLabel.cs`'s pattern).
- **BossVotePopup.Show** doesn't log success. Other UI attach methods do; useful for operator-validation grep.
- **BossVotePopup._Process** uses `new StringBuilder()` for ≤20 char-appends per option. `new string('▮', count)` is one-line and lower-allocation.
- **BossVotePatch.PreparedSuccessfully** property doesn't exist; siblings (`CardRewardVotePatch`) have it for sibling-patch cross-checks. Latent gap if a B.3-adjacent gate is ever added.
- **BossVotePatch** log message spacing inconsistency (`"degraded: RunManager..."` vs sibling patches' `"degraded:RunManager..."`). Cosmetic.
- **BossVotePatch.Prefix → PrefixContinue split** has no rationale comment. A reader from `AncientVotePatch` will wonder why this slice diverges.
- **Plan said `BossVotePopup.cs` compiles into tests** — wrong, the popup has Godot dependencies. Implementation correctly excludes by leaving `src/Game/Ui/` out of the test csproj's `Compile Include` list. Spec note worth fixing.

Cross-slice / pre-existing:
- **`YouTubeChatServiceTests.Escalation_*` flake** in `FakeTimerScheduler.Advance` (List.Remove ArgumentOutOfRangeException). Reproduces intermittently; ran clean on retry every time during B.3 work. Not introduced by B.3.

UX / feature followups (eventual polish slice candidates):
- **Map button bypass** (above) — streamer can advance via map button without firing the boss vote. To fix, also patch the map-navigation entry point.
- **Spine portrait support** (above) — gradually-growing visual gap as MegaCrit ships more Spine bosses. Renderer is `NSpineAutoPlayer` per the decompile; `EncounterModel.BossNodeSpineResource` exposes the `MegaSkeletonDataResource` already.
- **Save-quit-and-fully-exit** (process restart) loses the in-memory `_lastSwappedBossId` marker, so post-restart Continue fires a fresh vote instead of silent-restore. Less common path; accepted degradation.
- **Vote-control dev commands** (`addtime N`, `pausevote`, `resumevote`) — already documented in the v0.2+ section below.

---

## Plan B.2.2 ancient vote (resolved 2026-05-14)

Plan B.2.2's spec is at [`docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md`](../docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md); the implementation plan at [`docs/superpowers/plans/2026-05-13-plan-b-2-2-ancient-vote.md`](../docs/superpowers/plans/2026-05-13-plan-b-2-2-ancient-vote.md). Slice tagged `plan-b-2-2-complete`.

### Acceptance gate — green (one ancient presumed good without in-game observation)

- [x] **Neow regression** — vote title reads "Neow's Offering" in Twitch chat (cosmetic regression from "Neow's Bonus" was accepted in the spec). Chat votes apply.
- [x] **Act 2 ancients** — Pael, Tezcatara, Orobas all validated in-game. Vote opens with `{Name}'s Offering` title, chat votes apply, winning relic granted.
- [x] **Act 3 ancients** — Nonupeipe, Tanx validated in-game. **Vakuu not encountered** during operator-validation (vanilla seed/character roll didn't surface him); presumed good on the strength of the inheritance-based predicate plus successful validation of the other five mid-run ancients.
- [x] **Darv** (cross-act, `AllSharedAncients`, gated on `DarvEpoch`) — validated in-game. Confirms the inheritance predicate handles the cross-act path that the per-act `AllAncients` lists don't surface.
- [x] **Trolling override** — streamer click overridden by chat vote on at least one ancient. Suspend-and-resume flow works on the new event types.
- [x] **Log inspection** — `[ancient-vote]` log lines appear; no `[neow-vote]` lines remain anywhere in `godot.log`.

### Architecture-defining outcomes

**Inheritance-based predicate is the right shape for "events that share an underlying option-button flow".** The predicate `eventModel is AncientEventModel and not DeprecatedAncientEvent` covered all seven mid-run ancients (Neow + 6 ancients + Darv via `AllSharedAncients`) without any per-type allow-list maintenance. Future ancients MegaCrit ships will auto-work. The existing safety nets (single-option skip → vanilla; multiplayer bail → vanilla; `HandleVoteAsync` try/catch → fallback to player click; resume-time liveness checks) cover any non-standard option semantics in future ancients without preemptive defensive coding.

**Per-event-model vote title derivation generalizes cleanly.** `eventModel.Title.GetFormattedText()` returned the expected localized names ("Neow", "Pael", "Tezcatara", etc.) with no fallback hits during validation. The `"Ancient"` fallback in `GetVoteTitle` remained unreachable as expected. The `$"{name}'s Offering"` suffix is English-only and would need localization for a non-English build (not in current scope).

### Findings worth preserving

- **Darv was missed in initial brainstorming research.** It's an `AncientEventModel` subclass exposed via `ModelDb.AllSharedAncients` (cross-act) rather than any per-act `AllAncients` list at [`decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs:121`](../decompiled/sts2/MegaCrit/sts2/Core/Models/ModelDb.cs#L121). Caught during T3's code review. Lesson for future predicate-widening slices: grep `class.*:.*AncientEventModel` (or equivalent base-class probe) across `decompiled/sts2/MegaCrit/sts2/Core/Models/Events/`, not just the per-act `AllAncients` enumerations.
- **`AncientEventModel` lives in `MegaCrit.Sts2.Core.Models`, NOT `MegaCrit.Sts2.Core.Entities.Ancients`.** The B.2.2 spec originally claimed the latter; T3's implementer verified the actual namespace from the decompile and corrected upstream. The `Entities/Ancients/` folder contains dialogue + identity types (`AncientDialogueSet`, etc.), not the model itself. Memory entry [`sts2_ancients`](../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_ancients.md) updated to flag this.
- **The "Strike/Defend dependency on ancient relic pickup" investigation surfaced two relics worth knowing about.** Only `NutritiousSoup` (Tezcatara Pool1, enchants Basic Strikes — silently inert if deck has no Strikes) and `ArchaicTooth` (Orobas Pool3, transcends one of `{Bash, Neutralize, Unleash, FallingStar, Dualcast}` — game-side filtered via `SetupForPlayer`) interact with starter cards. The game handles ArchaicTooth correctly without our help; NutritiousSoup is a vanilla quirk we don't fix (Sealed/Draft-only edge case). See spec § Edge cases.
- **Suspend-and-resume Harmony pattern (from B.1) is fully event-type-agnostic.** No changes needed to the resume flow, run-id guard, or multiplayer bail to support six new event types. Predicate-widening alone was sufficient.

### Follow-ups + observations

- **Mod-version hash mystery (2026-05-14):** Surfinite's runtime log at validation time showed `[INFO] [SlayTheStreamer2] mod version: 1.0.0+d7eb5a5985018847dd27239366bd03dd3d8ab25d`, but `d7eb5a5` is the commit that *predates* all six B.2.2 commits. New behaviors (`[ancient-vote]` log lines, "Neow's Offering" vote title) were confirmed running in-game, so the deployed dist is current — the version-hash capture in `build.ps1` appears to be stale or out of sync with the actual HEAD at build time. Worth a forensic dive on what `build.ps1` reads vs. what `git log -1 --format=%H` would say. Doesn't block the slice; flag for v0.2 polish.
- **Vakuu acceptance-gate gap.** Not encountered during validation. The inheritance-based predicate makes per-ancient coverage rationally optional once a few have been confirmed, but if a Vakuu vote ever misbehaves later, this gap is the explanation.
- **Stale internal vocabulary fixes were caught in code review, not brainstorming.** Two log/comment strings still said "Neow blessing" / "Neow bonus" after T3-T4's main edits. Caught in T3 and T4 code-reviewer agents, fixed in follow-up commits `e314048` and via T4's bundled polish. Lesson: when widening the scope of an existing patch, grep for ALL string-literal references to the old narrow term (`git grep "Neow"`), not just the function names.

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

- **Sealed-deck draft start** — Tempus's original StS1 mod opened a run with a **sealed deck** that **the streamer drafts themselves** (corrected 2026-05-12 from FrostPrime Discord intel — earlier note said "chat drafted", which was wrong). The streamer's drafting was the "fair" tradeoff against chat's ongoing antagonism through subsequent voting. Mechanics confirmed from Discord (multiple community members):
  - Streamer drafts **N cards from a pool of M** (original mod was ~20×3 historically per one community member's memory but should be configurable — defaults TBD per character).
  - **Chat picks the Neow bonus FIRST, then streamer drafts the sealed deck** — order matters so chat can't sandbag a deck commitment with a `remove 2 cards` Neow pick.
  - **StS2 already has a sealed-deck function in Custom Mode** (per a community member: "doesnt give you a regular pick 1 of 3 neow bonus" — confirms it bypasses Neow when used standalone). Our implementation likely repurposes this: replace the Neow bonus-display step with `[draft a] Sealed Deck` option, then route through StS2's existing custom-mode deck-draft UI. Decompile investigation needed at scoping time.
  - **Character-specific must-include cards**: some characters (Necrobinder per one community member — `Bodyguard` and `Unleash` for Osty Build) become unplayable without specific cards in the draft pool. Need a configurable per-character must-include list (simple comma-separated string in settings JSON would suffice for v1).
  - **Archaic Tooth / Orobas interaction** also flagged — without starter cards in pool, certain Orobas event interactions get forced into a single outcome. Document but probably accept for v1.
  - **Open design question (not in Discord thread)**: does chat vote on the character pick, or does the streamer choose? StS1 mod probably had streamer-chooses; defaulting to that is safe.
  - Major mode pivot — specced as a post-B.3 sub-plan, likely Plan E or its own headline "v0.2 mode pivot".
- **StS2 co-op multiplayer.** API is multiplayer-aware (`VoteCoordinator` is instance-based per Reviewer 6's catch); full multi-streamer impl deferred.
- **Subscriber/mod/VIP-only voting filters.** `ChatMessage` already exposes badge flags; future filter is a `where`-clause in `VoteCoordinator.Start` consumers.
- **Localised receipts.** Add peer static helpers (`SpanishReceipts.cs` etc.) + `Func<VoteSnapshot, ReceiptKind, string>` to `VoteCoordinator.Start`.
- **Whispering Earrings — chat plays first turn instead of Vakku.** From FrostPrime Discord 2026-05-12 (one community member proposed, another cautioned on time cost). The vanilla relic lets `Vakku` (an AI) play the streamer's first turn; the mod-flavor swap is "chat plays it instead". Two variants discussed:
  - **Vote per card**: chat votes on which card to play, then which target, etc. — flagged as too slow ("first turn might be too long").
  - **Spam mode** (per one community member): "free-for-all where chat spams numbers and it only takes one number to have that card in hand be played" — first valid vote wins, fast. Worth specifying as the primary mode; vote-per-card as an opt-in for slower streams.
  - Investigation: find what method/state Vakku-plays-first-turn hooks into; that's our patch target.
- **Chat skip-vote option (`#0` = skip on card-reward votes).** Per a community member (2026-05-12 Discord): in the StS1 mod chat could vote skip as `#0` alongside the card options. Currently our mandatory-look + budget system gives the streamer skip agency but chat has no vote-for-skip option. They also noted Surfinite's current model is fine for now ("console would be fine at first" if streamer wants to override). Future polish: add a chat-skip option that participates in the tally and applies if it wins, alongside the streamer's existing parent-Proceed gate. Likely needs a settings flag (`chatCanVoteSkip: true`).
- **YouTube chat parallel integration.** Per a community member: FrostPrime wants YouTube chat participation alongside Twitch. Feasibility analysis done 2026-05-12 — see [`notes/07-youtube-chat-feasibility.md`](07-youtube-chat-feasibility.md) for the full writeup. **TL;DR**: doable via scraping the `youtubei` internal endpoint (~200 LOC custom scraper); architectural fit is clean (`YouTubeChatService : IChatService` + `MultiChatService` aggregator wrapping it alongside `TwitchIrcChatService`). Read-only on YouTube; receipts go to Twitch only. Effort estimate ~1–2 weeks. Five design decisions captured in the writeup; should reach out to FrostPrime when his tournament is over.
- **Events with card options outside the normal card-reward flow.** Per a community member (2026-05-12): The Cheese Room and Brain Leach events grant card choices through their own custom screens, not `NCardRewardSelectionScreen`. Our patches don't fire on those paths. Either accept (chat doesn't vote on these) or invest in per-event patches. Likely accept for v0.1; revisit per-event if streamer feedback complains.
- **Events deliberately NOT chat-controlled (confirmed convention).** Per a community member (2026-05-12): the StS1 mod intentionally did not let chat control event outcomes ("I think that's for the best"). Another community member flagged events like Slippery Bridge and The Trial as ones where chat control would be problematic. **Our v0.1 non-goal "Event choice voting" is correctly aligned with the original mod's design intent**, not just a scoping shortcut. Keep this principle visible when scoping v0.2 event work.
- **Vote-control dev console commands (`addtime N`, `pausevote`, `resumevote`).** Surfinite floated 2026-05-15 during B.3 operator validation. Streamer-facing QoL — primary motivation per Surfinite is FrostPrime's "bargain with chat" pattern (e.g., "I'll buy 5 gift subs if you change your vote to X — extending the clock by 30s to give you time to swap"). Cross-cutting: benefits all four vote types (Neow, card reward, Ancients, boss), not just B.3 — so it's a `VoteSession` API change, not a per-slice feature.
  - `addtime N`: shift `_openedAt` backward by N seconds, cancel-and-reschedule `_closeTimer`. Periodic-tally cadence unchanged. Negative N supported (clamped to immediate-close if it would push remaining ≤ 0).
  - `pausevote`: cancel `_closeTimer` + `_periodicTimer`, set `_isPaused = true`, capture `_timeRemainingAtPause`. `TimeRemaining` getter returns the frozen value while paused. Chat messages still feed `_tallies` (per Surfinite spec — "still counting votes"). Visual popups already poll `TimeRemaining`, so the timer label naturally freezes too.
  - `resumevote`: reschedule timers using `_timeRemainingAtPause`, clear `_isPaused`.
  - Three console commands subclass `AbstractConsoleCmd` (~30 LOC each, auto-discovered via reflection; pattern is established by `VoteNowConsoleCmd.cs`).
  - **Subtle gotcha to design**: pause + chat-disconnect interaction. `VoteSession` already tracks `_disconnectGapAccum` to compensate when chat goes offline mid-vote. If a streamer pauses, then chat disconnects, then reconnects, then the streamer resumes — should the disconnect window count? Probably no (paused time shouldn't double-count). Needs a brief design decision + a unit test.
  - **Effort estimate**: ~2 days — VoteSession API changes + 8-10 new unit tests + 3 console commands + ~30 min operator smokes.
  - **Risk**: VoteSession is the shared primitive across all 4 voting slices. Modifying its internals (especially the `TimeRemaining` getter and timer scheduling) needs care so we don't subtly break existing slices.
  - Slice candidate name: `Plan-B-3.X vote-control dev commands` or `Plan-C-vote-control` — fits the existing slice-naming pattern either way.
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
