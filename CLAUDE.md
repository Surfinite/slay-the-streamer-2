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

Both steps are required. `install.ps1` is a copy step only. **The mod version printed in `godot.log` is the git HEAD at build time, not install time** — a stale `dist/` means a stale mod runs even after re-install. If the version hash in the log doesn't match `git log -1 --format=%H`, you skipped `build.ps1`.

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
- v0.2 yt-chat: `yt-chat/N.M:`

Commits to main are pre-authorized within slice work. Tag with `<slice>-complete` once the operator-validation gate is green.

Every Claude commit ends with the trailer:

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
```

Pass the message via a single-quoted HEREDOC to `git commit -m` so the trailer renders correctly.

### Periodic-tally dedup keys on tally STATE, not rendered text

`VoteSession`'s periodic-tally dedup compares the underlying tally state (option index → count), NOT the rendered receipt string. Every render includes `<remaining>s left` so text-equality dedup would never fire and the receipt would spam every tick. Surfaced as a bug during spec v2.3 review. **If you refactor the dedup or the receipt formatter, keep the comparison on the structural tally — never on the formatted text.**

### `*.sln` is intentionally gitignored

`dotnet build` auto-generates a `.sln` that references gitignored paths (build output, copied DLLs). Committing it would break fresh clones. If you open the repo in Rider/VS expecting a solution file, generate one locally with `dotnet sln` — don't commit the result.

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
