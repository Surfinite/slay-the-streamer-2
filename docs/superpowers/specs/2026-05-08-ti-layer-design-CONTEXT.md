# Context Document for External Review — TI Layer Design Spec

This document is paired with [`2026-05-08-ti-layer-design.md`](./2026-05-08-ti-layer-design.md) (the spec under review). The reviewer has zero prior knowledge of this project; everything they need to assess the spec's quality is here.

---

## Reviewer Brief

You are receiving two documents: this context document and the spec at [`2026-05-08-ti-layer-design.md`](./2026-05-08-ti-layer-design.md).

**Your role**: critically analyze the spec given the context here. Identify weaknesses, risks, missing considerations, better alternatives, unnecessary complexity, things that should be removed, things that are good and should be preserved. Suggest additions, future features worth considering, and architectural improvements. Be constructively critical — not rubber-stamping.

Your review will be synthesized in a meta-review by the spec author to improve the spec. Be specific and actionable.

**Important**: You do **not** have direct access to the codebase. You're working from this context document only. The spec author has full code access and will validate all suggestions against the actual code during the meta-review. Where you feel uncertain due to limited visibility, flag it explicitly and note any assumptions you're making.

### Review output format

Structure your review as follows:

1. **One-line verdict** — overall assessment in a single sentence.
2. **What's good** — what should be kept as-is and why.
3. **Concerns & risks** — what worries you, ranked by severity.
4. **Suggested changes** — specific, actionable modifications to the spec.
5. **Alternatives** — different approaches worth considering.
6. **Additions** — things missing that should be there.
7. **Removals** — things in the spec that shouldn't be.
8. **Minor / nits** — low-priority observations.
9. **Assumptions you're making** — where you lacked visibility into the codebase and had to guess. The author will validate these.

Be specific. Reference section names from the spec. Don't soften your criticism — the goal is to improve the spec, not to be polite about it.

---

## 1. Project Overview

**`slay-the-streamer-2`** is a Slay the Spire 2 (StS2) mod that lets a Twitch streamer's chat vote on the streamer's in-game decisions (card rewards, Neow blessings, event choices, boss reward picks, shop purchases, map paths). It is inspired by Tempus / Chronometrics's StS1 "Slay the Streamer" mod (Steam Workshop 1610759491, github.com/Tempus/SlayTheStreamer), but **is not a port** — StS1 is Java/LibGDX, StS2 is Godot/.NET 9, so it's a clean reimplementation in C# against StS2's official modding API.

**Stage**: greenfield. Two days into research and design. No mod source code exists yet. The spec under review defines the architecture for the *Twitch-integration ("TI") layer*, which is the first sub-system to be implemented.

**Goals**:
- Ship a v0.1 MVP with chat votes on the six core decisions listed above.
- Read-only IRC, plus *outgoing* announcements (vote open / periodic tally / vote close) so viewers can confirm their votes landed despite the streamer-side video lag of 5–30 s.
- Design the TI layer (IRC + vote engine + optional UI) so it can later be lifted into a reusable base mod for *other* StS2 mods (analogous to robojumper's `ststwitch` for StS1, which doesn't exist yet for StS2). Same repo for v0.1 — extraction is a future, optional move.

**Constraints**:
- Solo developer, hobbyist project. Pace is comprehension-throttled rather than tooling-throttled.
- No license on Tempus's StS1 mod (default = all rights reserved). Reimplementation must be from feature-spec / behaviour-spec, not direct code copy.
- Target audience for the mod itself: streamers who play StS2 and want chat interaction.

## 2. Architecture & Tech Stack

**Languages & runtime**:
- C# / .NET 9 (StS2 ships .NET 9 with the game).
- Godot 4.5.1 Mono build chain. StS2 mods are *Godot projects*; the `csproj` uses `Godot.NET.Sdk/4.5.1` and the build goes through `godot --build-solutions --headless`.
- HarmonyLib (`0Harmony.dll`, ships with the game). Officially blessed runtime patching mechanism.

**StS2 modding API surface** (relevant facts the spec depends on):
- Official modding namespace `MegaCrit.Sts2.Core.Modding/` is **13 files**. The full public surface: `Mod`, `ModInitializerAttribute`, `ModManager`, `ModManifest`, `ModLoadState`, `ModSource`, `ModSettings`, `SettingsSaveMod`, `ModHelper`, `IModManagerFileIo`, `ModManagerFileIo`, `CombatHookSubscriptionDelegate`, `RunHookSubscriptionDelegate`.
- A mod is a `.dll` (+ optional Godot `.pck`) + small `.json` manifest, dropped into `<game-install>/mods/<id>/`. Game auto-loads at startup.
- Two clean entry-point styles:
  - **Style A** — `[ModInitializer("MethodName")]` static method called once on load (lets us start IRC + register Harmony patches manually). This is what we'll use.
  - **Style B** — no `[ModInitializer]`; game auto-runs `Harmony.PatchAll(assembly)`. Insufficient for us because we need IRC startup.
- `ModHelper.SubscribeForRunStateHooks(id, runState => new AbstractModel[] { ... })` is the public registration API for content-injection (returns models that participate in a run).
- `AbstractModel` (1,038 lines, ~200 virtual methods) is the canonical extension hook. **Crucially, its surface does NOT allow "click for the player" — it's content/observation/transformation only.** The spec's "we use Harmony patches for player-choice substitution" follows from this finding (one Harmony patch per voted decision).

**TI layer architecture (the part under review — see spec for the full design)**:

```
┌─────────────────────────────────────────────────────────────────────┐
│  ModEntry  ([ModInitializer("Initialize")] static class)            │
│  - constructs TwitchIrcChatService + connects                       │
│  - applies Harmony patches                                          │
│  - registers AbstractModel hooks via ModHelper (if sealed deck      │
│    is in scope for v0.1)                                            │
└─────────────┬─────────────────────────────────────┬─────────────────┘
              │                                     │
              ▼                                     ▼
┌─────────────────────────┐           ┌────────────────────────────────┐
│ Game/DecisionVotes/*    │           │ Game/Models/*                  │
│ (Harmony patches, one   │           │ (AbstractModel subclasses for  │
│  per voted decision)    │           │  sealed deck if v0.1 keeps it) │
│                         │           │                                │
│ Calls Voter.Start(...) │           │                                │
│ then awaits the winner. │           │                                │
└─────────┬───────────────┘           └────────────────────────────────┘
          │ depends on                  
          ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Ti/  (the future-extractable layer)                                 │
│                                                                      │
│  Ti/Voting/                                                          │
│   - Voter (static entry, owns CurrentSession singleton)              │
│   - VoteSession (state, events, AwaitWinnerAsync)                    │
│   - VoteOption, VoteReceiptPolicy                                    │
│                                                                      │
│  Ti/Chat/                                                            │
│   - IChatService                                                     │
│   - TwitchIrcChatService (handcrafted; ~200 LOC, zero NuGet deps)    │
│   - FakeChatService (test/dev)                                       │
│   - ChatMessage, ChatCredentials                                     │
│                                                                      │
│  Ti/Ui/                                                              │
│   - VoteOverlayControl (Godot Control; optional)                     │
│                                                                      │
│  Ti/Internal/                                                        │
│   - GameThreadDispatcher (marshal IRC thread → Godot main thread)    │
│   - TwitchIrcParser                                                  │
│   - ConnectionRetryPolicy                                            │
│   - IClock + SystemClock + FakeClock (testability)                   │
└──────────────────────────────────────────────────────────────────────┘
```

**Key architectural decisions and *why***:

| Decision | Rationale |
|---|---|
| Two-tier API: `ChatService` (lower) + `VoteSession` (upper) + optional UI | Clean extraction unit at `ChatService`. Other future StS2 mods could build "help the streamer" / "outfit vote" / "5-min-poll" features using only `ChatService`. |
| Strictly one vote open at a time | Matches StS's one-screen-at-a-time flow; surfaces concurrency misuse loudly via `Voter.Start` throw. |
| Handcrafted minimal Twitch IRC client | Cleanest extraction — the day someone lifts `ChatService` to a base mod, no extra NuGet deps come along. Twitch's IRC subset is small (~200 LOC). |
| Latest `#N` from a user wins | Lets viewers correct typos and react to the running tally. *Deliberate divergence from the original StS1 base mod, which was first-vote-wins.* |
| Open + periodic-tally + close announcements (default cadence 7 s) | Closes the streamer-vs-viewer 5–30 s video-lag gap. Safe under Twitch rate limits (default 20 msg / 30 s; broadcaster/mod 100 / 30 s). |
| Mod-agnostic *within StS2*, not cross-game | Allows `Ti/*` to use Godot types and StS2 logging without compromising the extraction goal — extracted base mod still targets StS2 mods. |
| `Ti/Ui/` Godot `Control` is opt-in | Consumers can use it, replace it, or skip it entirely. Keeps `Ti/Chat/` and `Ti/Voting/` truly UI-free. |
| Async/await + events both supported | `VoteSession` exposes `event TallyChanged`/`Closed` plus `Task<int> AwaitWinnerAsync()`. Caller picks the style; no API forks. |
| Unit testing via `FakeChatService` + `FakeClock` (injected `IClock`) | Lets us deterministically test all of `VoteSession`'s edge cases (vote-change, ties, no-voters, mid-vote disconnect) with no network or `Thread.Sleep`. |

## 3. Codebase Map

The repo is currently **research notes only**. No mod source exists. The directory tree:

```
slay-the-streamer-2/
├── README.md                                project overview, repo layout, scope, toolchain
├── notes/
│   ├── 01-session-log.md                    session-by-session running log
│   ├── 02-original-mod-feature-inventory.md original StS1 mod feature surface (verified)
│   ├── 03-sts2-modding-api.md               StS2 modding API surface
│   ├── 04-abstract-model-hook-surface.md    AbstractModel inventory + Harmony-vs-AbstractModel
│   └── 05-build-pipeline.md                 Godot build chain, manifest, csproj
├── docs/superpowers/specs/
│   ├── 2026-05-08-ti-layer-design.md        the SPEC UNDER REVIEW
│   └── 2026-05-08-ti-layer-design-CONTEXT.md  this document
├── .claude/
│   ├── commands/
│   │   ├── document-context.md              the slash command that produced this doc
│   │   └── meta-review.md                   for synthesizing reviews back
│   └── settings.local.json                  per-user, gitignored
├── .gitignore
├── references/                              gitignored; cloned per-workspace
│   ├── SlayTheStreamer-sts1/                  Tempus's StS1 mod (Java) — feature reference only
│   └── STS2FirstMod/                          jiegec's StS2 example mod — toolkit reference
└── decompiled/                              gitignored; ILSpy output of sts2.dll
    └── sts2/                                  3,386 .cs files, 24 MB, top namespaces:
                                              Combat, Events, Hooks, Map, Modding, Models,
                                              Rewards, Rooms, Runs, Settings, etc.
```

**No mod source files exist yet.** The spec defines the file layout that will be created.

**Recent commit history**:
```
4289ebc session 2: TI-layer design spec + ported review tooling
405d575 prereqs: install Godot 4.5.1 Mono
d086e17 notes: AbstractModel hook surface, ModHelper registration, build pipeline
bda3bd5 notes: StS2 modding API initial pass
636c1aa notes: original-mod feature inventory
c86bb35 Initial workspace: README, gitignore, session 1 log
```

**Lines-of-code scale**: ~1,000 lines of research notes; zero source. The spec under review will become roughly **800–1,500 LOC of C#** when implemented (rough estimate: handcrafted IRC ~200 LOC, parser ~100, VoteSession ~250, dispatcher/internal ~150, fakes ~100, overlay UI ~150, tests ~400+).

## 4. Relevant Existing Patterns & Conventions

The project is too young to have many internal patterns. What exists:

- **Notes-driven research**: every research session produces a markdown note in `notes/`. The spec is the first design artefact synthesized from those notes.
- **No license issue with reimplementation**: Tempus's repo has no license file = all rights reserved. We use only feature/behaviour-level information from his code, not transcription. The spec author has been disciplined about this.
- **Reference repos are read-only**: cloned into `references/` (gitignored), never modified.
- **Decompiled game output is gitignored** (regenerable via `ilspycmd`).
- **Build pipeline is Godot-driven**, not vanilla `dotnet build`. The csproj uses `Godot.NET.Sdk/4.5.1` and the build invocation is `godot --build-solutions --quit --headless`.
- **Manifest is a small JSON file**: `id`, `name`, `author`, `description`, `version`, `has_pck`, `has_dll`, `dependencies`, `affects_gameplay`. Slay-the-streamer-2's manifest will set `affects_gameplay: true` and probably `has_pck: false` for v0.1 (no shipped Godot resources).

**Naming conventions in the spec** (worth assessing for fit):
- C# Pascal-case types (`VoteSession`, `ChatMessage`, `IChatService`).
- Namespace split `SlayTheStreamer2.Ti.*` vs `SlayTheStreamer2.Game.*` — the seam.
- `Ti` (uppercase-T-lowercase-i) chosen for "Twitch Integration" as a deliberate echo of robojumper's `ststwitch`. Reviewer feedback welcome on whether this reads cleanly.

**Testing**: the spec proposes xUnit in a separate `slay-the-streamer-2.tests/` csproj. No tests exist yet. Source-referenced (not DLL-referenced) so internals are testable.

**Logging**: spec proposes routing all `Ti/*` logs through `MegaCrit.Sts2.Core.Logging.Log`. This is the one StS2-specific dependency the TI layer accepts; reviewer feedback welcome on whether a tiny logging-shim abstraction would be worth it.

## 5. Current State & Known Issues

- **What works today**: nothing runs. Research notes only.
- **Known unknowns / pending research items** (from notes, not spec):
  - Specific Harmony-patch target classes for each of the six votes (Neow event class, card-reward screen, shop confirm path, etc.). Identified as a "design-time, not now" task — but the spec under review doesn't depend on those answers; it's about the TI layer, which is upstream of every Harmony patch.
  - Whether `has_pck: false` works correctly for code-only mods (the example mod uses both DLL and PCK).
  - Exact location of game logs on Windows (presumably `%APPDATA%/Slay the Spire 2/`).
  - Godot remote-debug attach workflow specifics — known to work via `--remote-debug tcp://127.0.0.1:6007` but exact attach steps not yet validated.
- **Known issues with the StS2 platform that affect the spec**:
  - StS2 is in early access; the modding API may shift between game patches. Spec doesn't address an upgrade strategy.
  - We have to copy `sts2.dll` from the game install into `src/` before each build (per `STS2FirstMod`'s `build.sh`). Build script implication, not spec-relevant.
- **Tooling**:
  - .NET SDK 9.0.313 installed.
  - ILSpy CLI 9.1.0.7988 (pinned — 10.0.1 has a malformed `DotnetToolSettings.xml`).
  - Godot 4.5.1 Mono installed at `C:\Tools\Godot_4.5.1_mono\`.
  - Decompiled game source available at `decompiled/sts2/`.

## 6. Context Specific to the Spec

### What the spec touches

The spec defines the **TI layer** — Twitch IRC client + vote engine + optional Godot vote-overlay UI. It does **not** specify:
- The Harmony patches that bind chat votes to in-game decisions (those live in `Game/DecisionVotes/*` and consume `Voter.Start`).
- Sealed-deck logic (an `AbstractModel` problem, not a TI problem).
- The settings UI / oauth-token onboarding (deferred to a separate design pass).
- Anything outside `Ti/*`.

### Prior approaches considered and rejected

- **Building a separate "Twitch Integration for StS2" base mod first** (mirroring robojumper's StS1 layer). Rejected for v0.1 in favour of monolithic. Revisit later if the community wants it. The current spec keeps internal seams clean enough that this lift is a file move + small registration shim, not a refactor.
- **Three-tier with `ChatCommandRouter`**: a middle layer that routes `!commands` to handlers. Considered to support hypothetical future "outfit vote" / "help the streamer" mods that aren't vote-driven. Rejected as YAGNI — will refactor *into* this if a second consumer appears.
- **Single-tier `VoteSession` owning its own IRC** (no exposed `ChatService`). Rejected — defeats the extraction goal; future non-vote chat-driven mods would have to redo the IRC work.
- **TwitchLib as the IRC library**. Rejected in favour of handcrafted minimal IRC — ~10 transitive NuGet deps would clutter the extraction lift later. Reviewer feedback welcome on whether this is over-purist.
- **Using `RunHookSubscriptionDelegate` / `AbstractModel`** for the actual vote-substitution. Rejected after reading `AbstractModel.cs` — its 200 virtual methods cover content/observation/transformation, not "click for the player". Harmony is required for the player-choice substitution. (See `notes/04-abstract-model-hook-surface.md` for the table mapping each v0.1 vote to its plan.)

### Dependencies and integration

- **HarmonyLib** ships with StS2 (`0Harmony.dll`). Spec assumes Harmony is available; no NuGet dep needed.
- **Godot.NET.Sdk** for the csproj. Required for any mod that touches Godot APIs (we will, for the UI overlay; the rest of `Ti/*` could in principle use `Microsoft.NET.Sdk` if isolated).
- **`MegaCrit.Sts2.Core.Logging.Log`** is the one StS2-specific dependency `Ti/*` accepts (for logging).
- **`MegaCrit.Sts2.Core.Modding.ModHelper`** is used by `ModEntry` (not by `Ti/*`).
- **No NuGet packages**. Specifically no TwitchLib, no `Microsoft.Extensions.Logging`, no `Microsoft.Extensions.DependencyInjection` — handcrafted everything.

### Performance / scale / security

- **Performance**: all event marshalling onto Godot's main thread via `CallDeferred`. IRC reads on background `Task`. Vote tally is trivial CPU. No expected hotspots.
- **Scale**: a busy StS streamer is ~50–500 viewers actively voting in a 30-second window. ~10–100 chat messages per second peak. Tally O(messages), `TallyChanged` events ~O(unique-voters-per-second). Negligible.
- **Security**:
  - oauth token must be loaded from somewhere streamer-supplied; out of scope here, but spec uses `ChatCredentials(string Username, string OauthToken)` so the consumer can decide.
  - No PII collection beyond Twitch user-ids in the in-memory vote tally (cleared between sessions).
  - The mod doesn't open any inbound network ports.
  - Outgoing IRC messages: rate limits are the main concern; default policy 7 s tally cadence keeps us well under both the 20/30s and 100/30s ceilings.
- **Reliability**: spec proposes infinite-retry exponential backoff on IRC reconnect. Could be a footgun if oauth is wrong (retries forever); spec flags this as an open item.

## 7. Scope Boundaries

### Out of scope for this spec

- **Cross-game reuse**. The TI layer is mod-agnostic *within StS2*, not portable to other games. It can use Godot/`sts2.dll` types where useful.
- **Twitch Extension overlays / PubSub / channel points / predictions / polls / bits**. Read-only-plus-announcements IRC is the entire I/O surface.
- **Generic `!command` router** (e.g. `!help`, `!stats`). The upper-tier `VoteSession` is the only built-in consumer of `ChatService`. A `ChatCommandRouter` middle tier may appear in a later design pass *only* if a second consumer of `ChatService` materialises.
- **Streamer-side configuration UI**. Deferred to the separate v0.1 settings/onboarding design.
- **Game-side glue** (Harmony patches, `AbstractModel` hooks, decision-to-vote bindings). Consumers of the TI layer; not part of this spec.
- **Sealed-deck mechanics, monster-naming, chat speech bubbles, custom UI imagery**. v0.1 polish or post-v0.1.

### Fixed and non-negotiable

- **Read-only IRC plus periodic announcements** is the only I/O. Don't suggest Twitch Extensions / bits / channel points.
- **Mod-agnostic-within-StS2** (NOT cross-game). Don't suggest abstracting away StS2 / Godot.
- **Single repo, single mod assembly for v0.1**. Don't suggest splitting into multiple mods now.
- **No NuGet dependency on TwitchLib or any other Twitch-specific library**. Handcrafted is a deliberate decision for extractability.
- **Strictly one open vote at a time**. Don't suggest multi-vote concurrency.
- **One vote per user, latest `#N` wins**. Don't suggest first-vote-locked or accumulating semantics.

### Trade-offs accepted deliberately

- Handcrafted IRC client = more upfront code and a small risk of corner-case bugs vs. TwitchLib's maturity. Accepted for the extraction win and zero-deps cleanliness.
- Strict `#N` parser (no `!1`, no bare `1`) trades some natural-chat false-negatives for far fewer false-positives. Accepted.
- Tied-vote and no-voter resolution by uniform random trades reproducibility for simplicity and "chat-decides" framing. Accepted; tests inject `Random` for determinism.
- Infinite retry on IRC reconnect: simple, but if oauth is wrong the loop never gives up. Spec flags as open item.

## 8. Success Criteria

The spec is successful if, when implemented, the following hold:

1. **Functional**: A `Game/DecisionVotes/*` Harmony patch can call `Voter.Start("card reward", ["Bash", "Defend", "Strike"], 30s)` and reliably get a winner back as either an event, a callback, or `await voter.AwaitWinnerAsync()`. All three styles work without API forks.
2. **Lifeable**: A second StS2 mod author could later take `Ti/Chat/*` (and optionally `Ti/Internal/*`) into a new csproj, drop the StS2-specific logging dep with a tiny shim, and have a working chat-IO base mod with no `Ti/Voting` cruft.
3. **Testable**: Every edge case in the spec's "Closing", "Tally rules", and "Validation" sections is covered by a unit test using `FakeChatService` + `FakeClock`. No flaky `Thread.Sleep`-based tests.
4. **Stable in lag conditions**: Viewers see periodic chat tallies appear without rate-limit hits, and a single vote outcome is announced even if IRC drops mid-vote.
5. **No game-thread violations**: All `TallyChanged`/`Closed` events fire on Godot's main thread; consumers never have to think about threading.

There are no hard performance / latency targets — the load is trivially small.

## 9. Key Questions for Reviewers

Where reviewer input is most valuable:

1. **Is the `ChatService` / `VoteSession` boundary actually a clean enough seam to lift later?** The current draft treats `IChatService` as the extraction unit. Are there subtle leaks (e.g. `VoteReceiptPolicy` baking in vote semantics; `ChatMessage` carrying StS2-specific assumptions; the `GameThreadDispatcher`'s coupling to Godot's `CallDeferred`) that would make the lift more painful than the spec implies?

2. **Is the threading model robust?** Background IRC `Task` → `GameThreadDispatcher` (queue) → Godot `CallDeferred` → main-thread event delivery. Are there reasonable failure modes (queue overflow, dispatcher disposed during shutdown, `CallDeferred` ordering surprises) the spec should explicitly address rather than assume?

3. **Is the parser regex `^#?(\d+)(?:\s|$)` calibrated right?** The original StS1 base mod required strict `^#N`; we've relaxed to "hash optional, anchored to start, terminated by whitespace/EOL" which accepts both `1` and `#1` while rejecting `1st`/`1.5`/`I have 3 cards`. Have we got the boundary conditions right, or is there a chat pattern we'll regret missing? (For comparison, Noita TI accepts bare numbers at start with no `#`.)

4. **Does "latest vote wins + 7s periodic tally" produce coherent UX?** A viewer who types `#1` then changes to `#2` 6 s later sees the count of `#1` go down only after the next tally. Is this confusing, or does it work fine? Is 7 s the right default, or 5 s, or 10 s? Should the cadence adapt to vote duration (e.g. duration / 5)?

5. **Is the handcrafted IRC client actually the right call?** Risks: subtle bugs in CAP REQ tag handling, missed PING/PONG edge cases, TLS handshake quirks. Benefits: zero-NuGet-deps cleanliness for extraction. Is there a middle path (e.g. a tiny well-maintained Twitch IRC NuGet that's <500 LOC of source) that gives most of the cleanliness with less re-implementation risk?

6. **Anything in the spec that locks us out of features we'll regret in v0.2 / v0.3?** Specifically: the strictly-one-vote-at-a-time invariant, the read-only-plus-announcements I/O surface, the no-`ChatCommandRouter` decision. Are any of these one-way doors that will hurt in 6 months?

## 10. Glossary / Domain Terms

| Term | Meaning |
|---|---|
| **StS / StS1 / StS2** | Slay the Spire, the Mega Crit deck-building roguelike. StS1 (2019, Java/LibGDX) and StS2 (2025 early access, Godot/.NET 9) are the two versions. |
| **Mega Crit** | Studio that makes Slay the Spire. They officially support modding for both StS1 and StS2. |
| **Tempus / Chronometrics** | Author of the original "Slay the Streamer" StS1 mod we're inspired by. |
| **robojumper** | Author of the StS1 "Twitch Integration" base mod, the underlying chat-vote framework Tempus's mod sits on top of. **No equivalent exists for StS2** — that gap is part of the motivation for this project. |
| **Harmony / HarmonyLib** | C# runtime patching library. Lets us inject `Prefix`/`Postfix`/`Transpiler` code around any game method. Ships with StS2 (`0Harmony.dll`). The standard mechanism for "intercept the player's input handling". |
| **AbstractModel** | StS2's canonical extension class — a 1,038-line abstract class with ~200 virtual methods. The game's built-in cards, relics, powers, monsters all subclass it. Mods register their own subclasses via `ModHelper.SubscribeForRunStateHooks`. **Cannot substitute the player's choice**, only modify content, observe events, and transform values. |
| **ModHelper** | Static class in `MegaCrit.Sts2.Core.Modding`. Public API: `AddModelToPool`, `SubscribeForRunStateHooks`, `SubscribeForCombatStateHooks`. |
| **`[ModInitializer("Init")]`** | Attribute on a static class containing a static method named `Init`. Game calls it once on mod load. |
| **Godot.NET.Sdk** | Godot's MSBuild SDK used in `csproj` for Godot-aware C# projects. Required for any mod calling Godot APIs (UI, scenes, signals). |
| **Twitch IRC** | Twitch's chat protocol, an IRC-flavoured TCP protocol over TLS at `irc.chat.twitch.tv:6697`. Supports CAP REQ for tags (display names, badges, user IDs). |
| **CAP REQ** | IRC capability negotiation. We request `twitch.tv/tags` and `twitch.tv/commands` so PRIVMSG lines include user IDs and display names. |
| **`#N` / `N`** | The vote command syntax. Viewer types e.g. `#1` or just `1` at the start of a message. The TI layer's regex is `^#?(\d+)(?:\s|$)` — hash optional, start of message, terminated by whitespace or end. |
| **Sealed deck** | A defining feature of the original StS1 mod: chat picks the streamer's starting deck from a chat-curated pool. Possibly in / possibly out of v0.1 — tentative yes from the user. |
| **TI / Ti layer** | "Twitch Integration" layer. The reusable chat-IO + vote-engine code that lives in `SlayTheStreamer2.Ti.*`. The seam between this and `SlayTheStreamer2.Game.*` is the future extraction point. |
| **`#1` lag problem** | Viewer types `#1`; the streamer's video frame showing the updated vote bar is on a 5–30 s delay; viewer can't tell from the video whether their vote registered. The spec's periodic-announcement strategy is the mitigation. |
| **Justinfan** | Twitch's anonymous-IRC convention. Connecting with a `NICK justinfan{rand}` allows read-only access without an oauth token. |
| **PubSub / Twitch Extensions** | Twitch's higher-level integration mechanisms (channel-point redemptions, on-stream overlays). **Out of scope** for this project. |
