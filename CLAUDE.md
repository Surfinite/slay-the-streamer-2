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
