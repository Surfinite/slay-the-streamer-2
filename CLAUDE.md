# Claude project notes — slay-the-streamer-2

Project-level instructions auto-loaded each session. Keep this file focused on load-bearing rules and workflow gotchas that have actually bitten the project. Per-slice rationale belongs in `docs/superpowers/specs/`; running follow-ups belong in `notes/06-followups-and-deferred.md`.

---

## Workflow gotchas (Tier 1 — these have bitten before)

### Build + install pipeline

After code changes:
```powershell
pwsh -File build.ps1     # rebuilds dist/ (dotnet publish + dotnet test + assemble)
pwsh -File install.ps1   # COPY ONLY — dist/ -> Steam mods folder; does NOT rebuild
```

Both steps are required. `install.ps1` is a copy step only. **The mod version printed in `godot.log` is `<manifest version>+<git sha>`** — the suffix is the git HEAD at *build* time (build.ps1 stamps `-p:Version` from `src/slay_the_streamer_2.json`). A stale `dist/` runs stale even after re-install; if the hash doesn't match `git log -1 --format=%H`, you skipped `build.ps1`. **Re-run `install.ps1` after the FINAL build** — building the release zip doesn't deploy it, and a build done *before* the version-bump commit leaves the local install on the prior stamp (bit us: local read `0.1.1` after `0.1.2` shipped).

### Verifying mod compat after an StS2 Beta game update

Decompile the OLD (`src/sts2.dll` — still the prior build's binary until you rebuild) and the NEW game DLL with the **same `ilspycmd`**, then `diff -rq` those two. **Don't** diff against the committed `decompiled/sts2/` baseline — ilspycmd-version emit differences (`<>z__`↔`_003C_003E`, `global::` prefixes) flood it with false hits (~1979 vs ~1138 real for v0.107.0→v0.107.1). Decompile **XML-free**: the game ships `sts2.xml` beside the DLL since v0.107.1, and a sibling `.xml` makes ILSpy embed doc comments into every file. Filter changed files to the patch surface, then member-level-`diff` each hit (a file usually "differs" only from body/balance changes that don't touch our bound signatures). v0.107.0→v0.107.1 was ~1138 real changes, **zero** mod impact.

### Test isolation for TiLog

Any xUnit test class that triggers `TiLog.Info/Warn/Error` MUST be marked:
```csharp
[Collection("TiLog.Sink")]
public class FooTests : ... { }
```

Without this, the class runs in parallel with `TiLogTests` (which captures the static sink) and surfaces as `InvalidOperationException: Collection was modified during enumeration`. Voting tests are especially prone — `VoteSession` warns on tally events. Found the hard way during Task 5 of yt-chat work.

### Commit conventions

Per-task commits to `main` with a slice-specific prefix:
- B.1: `plan-b-1/N.M:`
- B.2.1: `plan-b-2-1/N.M:`
- B.2.2: `plan-b-2-2/N.M:`
- v0.2 yt-chat: `yt-chat/N.M:`
- B.3: `plan-b-3/N.M:`
- B.3.1: `plan-b-3-1/N.M:`
- B.3.2: `plan-b-3-2/N.M:`
- Settings UI: `settings-ui/N.M:`
- v0.1.1 A10 double-boss vote: `boss-2round/N:`
- 2026-06 stream-feedback fixes (FrostPrime live findings): `stream-polish/N:`

Commits to main are pre-authorized within slice work. Tag with `<slice>-complete` once the operator-validation gate is green.

### Periodic-tally dedup keys on tally STATE, not rendered text

`VoteSession`'s periodic-tally dedup compares the underlying tally state (option index → count), NOT the rendered receipt string. Every render includes `<remaining>s left` so text-equality dedup would never fire and the receipt would spam every tick. Surfaced as a bug during spec v2.3 review. **If you refactor the dedup or the receipt formatter, keep the comparison on the structural tally — never on the formatted text.**

### `*.sln` is intentionally gitignored

`dotnet build` auto-generates a `.sln` that references gitignored paths (build output, copied DLLs). Committing it would break fresh clones. If you open the repo in Rider/VS expecting a solution file, generate one locally with `dotnet sln` — don't commit the result.

### Test csproj source-include globs are explicit — `src/Game/Ui/*` is NOT included by default

`tests/slay_the_streamer_2.tests.csproj` source-references mod project files via these `<Compile Include>` globs:
- `..\src\Ti\Internal\**\*.cs`
- `..\src\Ti\Chat\**\*.cs`
- `..\src\Ti\Voting\**\*.cs`
- `..\src\Game\Bootstrap\**\*.cs`
- `..\src\Game\DecisionVotes\**\*.cs` (with per-file `Compile Remove` for the Harmony-patch classes)

Anything outside those paths needs an **explicit** `<Compile Include="..\src\<path>\<file>.cs" />`. If you add a unit-testable helper in `src/Game/Ui/*` (the pattern B.3.1's `PortraitFit.cs` established), add the surgical include yourself — don't broaden to a glob, because the rest of `src/Game/Ui/*` references Godot types not visible to the test project's `Microsoft.NET.Sdk`.

Also: the test project is `Microsoft.NET.Sdk` (not `Godot.NET.Sdk`), so `Godot.*` types are unavailable. Unit-testable helpers must use `System.Numerics` instead of `Godot.Vector2`. Callers (which are typically Godot-side) do the conversion at the call site — cheap and one-directional.

---

## Architectural rules (Tier 2 — load-bearing)

### TI/Game seam

`src/Ti/` is BCL + Godot + System.Net.Http only. **Never reference `MegaCrit.Sts2.*` or anything from `src/Game/*`.** This boundary is load-bearing for the eventual extraction of `Ti/` as a reusable multi-platform chat-integration base-mod assembly.

`src/Game/` is the StS2-specific glue: Harmony patches, settings, mod bootstrap. It depends on `Ti/` and on `sts2.dll`.

### Suspend-and-resume Harmony pattern

Every Harmony prefix that triggers a vote MUST use this shape:
1. Prefix returns `false` to suspend the original method.
2. Prefix kicks off `_ = HandleVoteAsync(...)` as fire-and-forget.
3. Async handler awaits `session.AwaitWinnerAsync()`.
4. On completion, handler invokes `dispatcher.Post(() => ResumeOnMainThread(...))`.
5. `ResumeOnMainThread` re-calls the original method with the chat-chosen winner.

**Never block the Godot main thread** with `session.AwaitWinnerAsync().GetAwaiter().GetResult()`. Smoke-proven deadlock during Plan B prep (Plan A's `RunContinuationsAsynchronously` is insufficient under Godot's main-thread sync context).

Reference impl: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` (B.1) and `CardRewardVotePatch.cs` (B.2.1).

### Chat-layer shape (v0.2+)

- `IChatConsumer` is the parent interface — read messages, send messages, state, events. No `ConnectAsync`.
- `IChatService : IChatConsumer` adds `ConnectAsync` for platforms that need a connect lifecycle (Twitch, YouTube).
- `MultiChatService : IChatConsumer` is the N-platform aggregator. Its aggregate `State` is best-of-children for active states; worst-of for terminal states (`AuthenticationFailed > JoinFailed > Disposed > Disconnected`).
- `VoteCoordinator` takes `IChatConsumer`, not `IChatService` — the connect lifecycle is wired by `ModEntry`, not by the voting layer.
- `IFastPollable` (optional capability, post-v0.1.2) — `VoteCoordinator` enables it on `Start` and disables on session `Closed`/`Cancelled`; `MultiChatService` forwards to pollable children; `YouTubeChatService` polls every 1s during a vote (else YouTube's server cadence). Twitch is event-push and doesn't implement it. NOTE: the YT poll loop uses real `Task.Delay` (not `ITimerScheduler`), so its tests use real short delays + an instance-settable `FastPollInterval`, not the fake scheduler.

### YouTube scraper isolation

All YouTube-specific fragility lives under `src/Ti/Chat/YouTubeChat/`. This folder is **deletable** for a Twitch-only TI extraction — nothing outside that namespace depends on YouTube specifics.

Maintenance task: `notes/youtube-fixture-refresh.md` documents the monthly capture-and-refresh process for fixture files.

### 0-indexed chat vote options

Chat votes are 0-indexed: `#0`, `#1`, `#2`, ... matching Tempus's StS1 mod convention. **Do not regress to 1-indexed** — most C# instincts pull toward `array[i+1]` UI labels, but the user preference is explicit. `VoteSession`, `VoteCoordinator`, `EnglishReceipts`, and `VoteTallyLabel` all assume 0-indexing end-to-end. Future vote-bearing decisions (B.2.2 Ancients, B.2.3 map, B.3 boss) must follow the same convention.

### Test-fake triad and `VoteSessionTestBase`

The standard test setup for any `VoteSession`/`VoteCoordinator`-adjacent code is:

```csharp
FakeClock clock = new();
FakeTimerScheduler scheduler = new(clock);
ImmediateDispatcher dispatcher = new();
Random rng = new(42);   // seeded for determinism
```

Plus `VoteSessionTestBase.CreateCoordinator(...)` which already encapsulates the triad + the `IReadOnlyList<string> configuredPlatforms` ctor parameter. **Don't roll your own** — extending `VoteSessionTestBase` or its sibling fixtures is correct; instantiating raw `VoteCoordinator` in a new test class will silently disagree with timing/dispatch assumptions and bite during async-await tests.

---

## Navigation (Tier 3 — where things live)

| Need | Where |
|---|---|
| Running follow-ups + design pivots + acceptance-gate results | `notes/06-followups-and-deferred.md` |
| Design specs per slice | `docs/superpowers/specs/` |
| Implementation plans per slice | `docs/superpowers/plans/` |
| Settings file location at runtime | `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` (NOT Godot's default `app_userdata/Slay the Spire 2/` — StS2 overrides) |
| Logs at runtime | `%APPDATA%\SlayTheSpire2\logs\godot.log` |
| YouTube scraper-fixture refresh process | `notes/youtube-fixture-refresh.md` |
| Pre-spec landscape research (YouTube chat) | `notes/07-youtube-chat-feasibility.md` |
| Pre-spec landscape research (sealed-deck + Draft modifier in vanilla Custom Mode) | `notes/08-sealed-deck-custom-mode-investigation.md` |
| Pre-spec landscape research (settings UI hook surface + tunable-knobs inventory) | `notes/09-settings-and-tunable-knobs.md` |
| Pre-spec landscape research (B.3 act boss vote feasibility) | `notes/10-boss-vote-feasibility.md` |
| StS2 asset extraction workflow (`.pck` → `decompiled/sts2-assets/`) | `notes/asset-extraction.md` |
| Decompiled game source (regenerable, gitignored) | `decompiled/sts2/MegaCrit/sts2/...` |
| Reference repos (gitignored, cloned per-workspace) | `references/SlayTheStreamer-sts1/` (Tempus's StS1 original — feature reference only, no license, no code copy); `references/STS2FirstMod/` (jiegec's StS2 modding toolkit reference) |

---

## Landmines (Tier 4 — rare-but-painful, worth a quick mention)

- **`.NET CookieContainer.Add(Uri, Cookie)` silently drops cookies** whose `Domain` has a leading dot. Use `DefaultRequestHeaders.Add("Cookie", ...)` directly. Surfaced in `yt-chat/15.2`.
- **YouTube no longer redirects `/channel/{ID}/live` → `/watch?v=...`** (verified 2026-05-12). Body-parse `<link rel="canonical">` instead. Surfaced in `yt-chat/16.2`.
- **YouTube continuation tokens are URL-encoded** (`%3D` for trailing `=` padding) and appear under three container types (`reloadContinuationData`, `invalidationContinuationData`, `timedContinuationData`). The first is the initial-page form; the latter two are steady-state. Surfaced in `yt-chat/17.2`.
- **YouTube member badges live in `authorBadges[]`, NOT `message.runs[]`**. Visual `#3 / #4` member-level pills don't appear in chat text; no false-positive vote risk from badges.
- **`TwitchIrcChatService.TransitionTo` is silent on state changes** (known v0.2 polish gap). Diagnostic forensics suffer when Twitch fails. `YouTubeChatService` was deliberately built with proper transition logging because of this lesson.
- **Twitch 20-msgs-per-30s account-level rate limit drops receipts under burst**. Multiple receipts in a close window (e.g. periodic-tally + close + cancellation) may silently fail to deliver. Known v0.2 polish item; consider rate-limiting receipt emission or batching.
- **`VoteCoordinator` constructor now takes `IReadOnlyList<string> configuredPlatforms`** (post-v0.2). Existing tests that construct directly must pass `new[] { ChatPlatformNames.Twitch }` for single-platform. `VoteSessionTestBase.CreateCoordinator` handles this.
- **`HttpResponseMessage.RequestMessage.RequestUri` doesn't always reflect the final post-redirect URL** in all .NET configurations. Don't rely on it; parse the response body instead. Surfaced indirectly in the original (now-replaced) yt-chat/16.1 discovery design.
- **`Neow.GenerateInitialOptions` branches on `RunState.Modifiers.Count > 0`, NOT `GameMode == Custom`** ([`decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs:215`](decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs#L215)). Any modifier whose `GenerateNeowOption(EventModel)` returns non-null (`SealedDeck`, `Draft`, `Specialized`, `Insanity`, `AllStar`) replaces the standard pick-3 with a single-option modifier kickoff. Don't infer Neow behavior from the game mode — infer it from the modifier list. Affects any future Neow-adjacent patch (B.2.2 Ancients predicate-widening, sealed-deck "Neow before draft" polish, etc.). Surfaced in notes/08.
- **`CardRarityOddsType.RegularEncounter` rarity odds NEVER roll `CardRarity.Basic`**. `CardFactory.RollForRarity` ([`decompiled/sts2/MegaCrit/sts2/Core/Factories/CardFactory.cs:199`](decompiled/sts2/MegaCrit/sts2/Core/Factories/CardFactory.cs#L199)) only weights Common/Uncommon/Rare. Character-identity Basics like `Bodyguard`/`Unleash` (Necrobinder) and `Zap`/`Dualcast` (Defect) are silently absent from any pool generated with these odds — including `SealedDeck` and `Draft` modifier pools. Affects any feature that samples "a player's character cards" via `CardCreationOptions(..., RegularEncounter)`. Surfaced in notes/08.
- **`replaceTreasureWithElites` parameter in `ActModel.CreateMap` / `StandardActMap.CreateFor` is dead code in this build.** The only live caller (`RunManager.cs:549`) hardcodes `false`. No ascension level activates it; the `AscensionLevel` enum tops out at `DoubleBoss` and has no "chests as elites" entry. Don't infer ascension behavior from this parameter's existence. The only ascension that interacts with bosses is `DoubleBoss` (final act only). Surfaced in notes/10 after a research correction round.
- **CRLF/LF normalization warnings on Windows are expected and harmless.** `warning: in the working copy of '...', LF will be replaced by CRLF the next time Git touches it` fires on every `git add` for text files. Subagents that scan tool output sometimes report these as concerns; they aren't. Don't change `core.autocrlf` to silence them — the warning is informational about a working-copy normalization that Git is doing correctly.
- **StS2 doesn't use `SceneTree.Paused`.** Pause-menu / settings / modals go through `RunManager.ActionExecutor.Pause()` for combat-time pausing; Godot's `SceneTree.Paused` is never toggled by the pause menu. Anyone trying to detect "is any vanilla submenu open" via `SceneTree.Paused` will always read `false` and waste a debug cycle (B.3 burned `plan-b-3/6.4` on this). The real probe is `NRun.Instance.GlobalUi.SubmenuStack.Stack.SubmenusOpen` — and **note the `.Stack` indirection**: the outer `SubmenuStack` returns an `NCapstoneSubmenuStack` wrapper, not the `NSubmenuStack` with the `SubmenusOpen` bool. Reference: `BossVotePatch.IsOccludingOverlayVisible`.
- **Vanilla bosses ship Spine `.tres` OR PNG fallback, never both.** `EncounterModel.MapNodeAssetPaths` returns one OR the other based on `BossNodeSpineResource`. Bosses with full Spine art (e.g., Ceremonial Beast) have no PNG sibling; "placeholder" bosses that explicitly override `BossNodeSpineResource => null` (e.g., Soul Fysh, Vantom, The Kin, Waterfall Giant, Lagavulin, Doormaker, Kaiser Crab, Knowledge Demon, Test Subject) point `BossNodePath` at `res://images/map/placeholder/<id>_icon` and DO ship a `.png`. As MegaCrit ships more Spine art, fewer bosses will have PNG fallbacks. Any code loading boss icons as `Texture2D` from `BossNodePath + ".png"` will hit empty boxes for Spine-only bosses unless `ResourceLoader.Exists(path)` is pre-checked (`BossVotePopup` defensive load pattern, `plan-b-3/6.2`). Full Spine rendering requires the `NSpineAutoPlayer` node + `MegaSkeletonDataResource` from `BossNodeSpineResource` — not currently used by any mod-side code.
- **StS2 save-quit can snapshot pre-mutation state.** Mid-room mutations of `runState` (verified for `MapCmd.SetBossEncounter`; likely applies to other mid-room writes) may be lost on save-quit-and-Continue — the save was taken at an earlier checkpoint. Any patch that mutates `runState` mid-room needs a "remember-what-we-did + verify-on-next-prefix + silently-re-apply" pattern (B.3's `_lastSwappedBossId` + idempotency check in `BossVotePatch.PrefixContinue`), OR needs to commit at a save-checkpoint boundary. The user-visible failure mode without this: chat votes, runs the swap, save-quit + Continue, swap is gone AND idempotency check would block a re-vote — fight goes to vanilla's pre-rolled boss instead. Process-restart-after-save-quit is a separate path (in-memory marker is lost, fresh vote fires).
- **`ModelId.Entry` is UPPER_SNAKE_CASE, NOT PascalCase.** `TheKinBoss` (the C# class name) has `Id.Entry == "THE_KIN_BOSS"`; the monster `KinPriest` has `Id.Entry == "KIN_PRIEST"`. Any code that string-compares `Id.Entry` against a literal needs the snake-case form. B.3.1 first shipped with `"TheKinBoss"` PascalCase comparisons that silently never matched until the godot.log line `[boss-vote] encounter THE_KIN_BOSS has 2 monsters; rendering primary KIN_FOLLOWER` made it obvious (`plan-b-3-1/7.3`). Always verify identifier formats against runtime `godot.log`, not C# class names.
- **`MonsterModel.AssetPaths` throws `"Canonical model ... used in incorrect place"` for bosses with mutable internal state inside their `GenerateMoveStateMachine()`.** The throw originates from `AssertMutable` checks that fire when a canonical (non-`ToMutable()`) instance's move-state-machine construction tries to write to an internal field. Ceremonial Beast's `BeastCryState` assignment is the observed canary; other stateful bosses likely behave the same. **Don't iterate `AssetPaths` on canonical instances** — build the combat scene path directly via `SceneHelper.GetScenePath("creature_visuals/" + monster.Id.Entry.ToLowerInvariant())`, matching `MonsterModel.VisualsPath`'s own construction. Surfaced in `plan-b-3-1/7.3` after pre-warm Warns showed up in operator validation.
- **`ActModel.AssetPaths` does NOT include monster combat scenes.** It covers act-level paths (background, map nodes, ancient assets, second-boss icon) but transitively lists ZERO `creature_visuals/<id>.tscn`. So `PreloadManager.LoadActAssets` does not pre-cache monster scenes — anyone using the act-asset cache as a substitute for monster-scene caching is wrong. Monster scenes cold-load at `MonsterModel.CreateVisuals()` time on first access (vanilla logs `Asset not cached:` warns — those are MegaCrit's signal that pre-load skipped them, not a mod bug to silence). Surfaced in B.3.1 spike + operator validation.
- **`ProcessMode.Disabled` on a parent Control cascades to `Inherit`-mode Spine children for a clean freeze** — useful pattern when you need pause-aware freeze for MegaCrit creature scenes and can't use `SceneTree.Paused` (which StS2 never toggles per the entry above). Setting `slot.ProcessMode = ProcessModeEnum.Disabled` on the slot Control halts `_Process` on all children whose ProcessMode is `Inherit` (the default), which is what freezes the MegaSpine animation advance. No `SetTimeScale(0)` API contact required, no typed-NCreatureVisuals reference needed at the toggle site. Reference implementation: `BossVotePopup._Process` occlusion block (`plan-b-3-1/7.1`).
- **`VoteSession.Cancel()` fires the `Cancelled` event, NOT `Closed`.** They are independent terminal states (`VoteSession.cs:237` for natural-expiry `Closed`, `VoteSession.cs:249` for `Cancel()`-triggered `Cancelled`). Popups that subscribe only to `Closed` will leak on user cancellation — the popup stays on screen until the process exits. Subscribe to both, route both through a single teardown handler, and marshal through the main-thread dispatcher because `Cancelled` may fire from the chat-parser thread on disconnect. `ActVariantVotePopup` shipped with the Closed-only bug and got fixed in `plan-b-3-2/13.1` after operator validation surfaced the stuck-popup-after-ESC symptom. `BossVotePopup.cs:82-83, 217-220` is the reference shape.
- **`NCombatBackground` must be parented under a Center-anchored zero-size Control, NOT a FullRect Control.** Vanilla uses `BgContainer` in [`combat_room.tscn:41-50`](decompiled/sts2-assets/scenes/rooms/combat_room.tscn#L41-L50) — `anchors_preset=8` with cancelling offsets. The internal `Layer_NN` controls inside `<zone>_background.tscn` are positioned at absolute offsets that ASSUME the parent's `(0,0)` is at the **viewport center**, not top-left. Anchoring the visual to `LayoutPreset.FullRect` shifts the texture's center off-screen and clips the bottom half (and most of the right half). Fix is one Control: `bgHolder.SetAnchorsAndOffsetsPreset(LayoutPreset.Center); free.AddChild(bgHolder); bgHolder.AddChild(visual);`. Surfaced in `plan-b-3-2/13.1` operator validation. Reference: `ActVariantVotePopup.BuildColumn` L1 branch.
- **Custom mode has its own `NCustomRunScreen.OnEmbarkPressed(NButton)` parallel to `NCharacterSelectScreen.OnEmbarkPressed(NButton)`.** Both screens implement `IStartRunLobbyListener` and expose public `Lobby`. Both disable embark/back/character buttons + call `_lobby.SetReady(true)` BEFORE the call chain reaches `BeginRunLocally`. Any "vote on Embark click" patch needs `TargetMethods()` returning both methods with a type-dispatch (`switch (screen) { NCharacterSelectScreen s => s.Lobby, NCustomRunScreen c => c.Lobby, ... }`) — a single-target patch on `NCharacterSelectScreen` silently misses Custom runs. `plan-b-3-2/13.3` migrated to this pattern after operator validation showed Custom runs slipping past a single-target patch. Reference: `ActVariantVotePatch.cs` (TargetMethods + GetLobby + GetOnEmbarkPressedMethod).
- **Patch the click handler, not the downstream consumer, for "intercept Embark" features.** `OnEmbarkPressed` disables UI and marks `_lobby.SetReady(true)` BEFORE eventually reaching `BeginRunLocally`. Suspending at `BeginRunLocally` means cancel-mid-suspend leaves the UI half-mutated (buttons disabled, lobby marked ready) with no clean restoration path. Suspending at `OnEmbarkPressed` means vanilla never touched the UI on cancel — clean no-op. On confirm, set `Lobby.Act1` and reflectively re-invoke `OnEmbarkPressed` with `_resumeInProgress=1` set so the prefix passes through. Reference: `ActVariantVotePatch.ResumeOnMainThread` (`plan-b-3-2/13.3`).
- **`RunLiveness.IsRunDying()` is for IN-RUN votes only — never wire it to a pre-run vote.** The probe returns `true` when `RunManager.Instance.DebugOnlyGetState()` is null, and `RunState` is null pre-`BeginRunLocally`. The act-variant vote fires on Embark click, before the run is created, so passing `RunLiveness.IsRunDying` to its `VoteTallyLabel.AttachTo` will cancel the session on the first frame and the popup never appears. Use a narrower probe in that context (the act-variant patch uses `IsRunStartAbandoned` for the popup; the corner tally label should pass no probe at all and rely on `session.Cancelled` propagation from the popup's ESC handler). Boss / Ancient / Card-Reward votes are mid-run and use `RunLiveness.IsRunDying` safely. Surfaced 2026-05-25 — adding the probe uniformly across all four vote patches broke the act-variant trigger; caught when Surfinite asked whether a font-only change could have done it.
- **`MegaSprite.GetAnimationState()` THROWS on game ≥ v0.107.0** when the Spine skeleton/animation-state isn't initialized yet (`return TryGetAnimationState() ?? throw new InvalidOperationException(...)`). On v0.106.1 it returned a possibly-null wrapper and never threw. Mod code that calls it directly (the Kaiser Crab boss-portrait path) must use the new **`MegaSprite.TryGetAnimationState()`** (returns `MegaAnimationState?`) for the graceful null path. The regular-monster animator path hits `GetAnimationState` transitively via vanilla's `CreatureAnimator` ctor, so it can still throw on a cold scene — but both mod call sites are wrapped in try/catch, so worst case is a static (unanimated) portrait + a `[boss-vote] ...deferred animation setup failed` Warn, never a crash or vote-logic failure. Surfaced verifying v0.107.0 compat (`boss-2round/1`); reference: `BossVotePatch.ApplyKaiserCrabAnimationAfterReady`.
- **`SpecialCardReward` has NO selection sub-screen — clicking its reward button claims the card instantly** via `Reward.OnSelect()`. It is a SIBLING of `CardReward` (both derive from `Reward` directly); vanilla uses it for the Thieving Hopper stolen-card recovery and the Lantern Key. Any "mandatory-look" or skip-budget gate that counts it as a card reward turns vanilla's free decline into a forced take (the only way to "open" it is to claim it). The card-skip gate type-checks `is CardReward` only — don't re-add `SpecialCardReward` to the gateable set. Surfaced live on FrostPrime's stream 2026-06-08; fixed in `stream-polish/3`.
- **StS2 modded launches can crash silently ~2-3s into startup (Sentry teardown race, game bug).** When modded, `SentryService` shuts down ("Shutting down because event reporting is disabled") but the sentry-godot GDExtension keeps a `should_sample` callable registered; a later `SceneTree::process` signal emission invokes it on a freed object → native AV in `Object::get_instance_id`, process dies with NOTHING in godot.log (crashpad writes a minidump to `%APPDATA%\SlayTheSpire2\sentry\reports\` instead — analyze with cdb from winget Microsoft.WinDbg). Intermittent/racy, version-independent (repro'd on v0.107.1 AND v0.108.0, 2026-07-15). NOT a mod bug — any mod triggers the modded-shutdown path. Workaround: `--force-sentry` in Steam launch options (Sentry stays alive; modded events still suppressed via `AlwaysRejectEvent`). Reported: megacrit/sts2-mod-uploader#14 (macOS sibling: #10). If a user reports "game opens then closes with mods", check for this before suspecting our code.
- **StS2's asset cache can free Godot resources out from under static C# caches between screens** ("Unloading N missed cache assets"). A statically cached `Font`/`Texture2D`/`StyleBox` whose native side was freed throws `ObjectDisposedException: Cannot access a disposed object` on next use — seen re-opening the mod manager after abandoning runs. Guard every static Godot-resource cache with `GodotObject.IsInstanceValid(...)` before reuse and recreate when stale. Reference: `SettingsPanelBuilder.Revalidate` (`stream-polish/4`).
- **`ActModel.SetSecondBossEncounter` does NOT refresh any UI** — unlike `MapCmd.SetBossEncounter`, which sets the model AND calls `NRun.Instance.GlobalUi.TopBar.BossIcon.RefreshBossIcon()` + `NMapScreen.Instance?.SetMap(rs.Map, rs.Rng.Seed, clearDrawings: false)`. There is no `MapCmd.SetSecondBossEncounter`. Any code that swaps the A10 DoubleBoss second boss must replicate that refresh manually (guard with `TestMode.IsOff`), or the combat loads the right boss but the top-bar icon and the map's `SecondBossMapPoint` keep showing vanilla's pre-swap second boss. `NTopBarBossIcon.RefreshBossIcon()` re-reads BOTH `Act.BossEncounter` and `Act.SecondBossEncounter`, so one call updates both icons. Reference: `BossVotePatch.ApplySecondBossSwap` (`boss-2round/2`). Surfaced in operator validation 2026-06-05.
