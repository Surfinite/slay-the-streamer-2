# Meta-Review — TI Layer Design Spec (`2026-05-08-ti-layer-design.md`)

**Source spec**: [`2026-05-08-ti-layer-design.md`](./2026-05-08-ti-layer-design.md)
**Updated spec**: [`2026-05-08-ti-layer-design-v2.md`](./2026-05-08-ti-layer-design-v2.md) — must-do and should-do changes applied; consider-tier items listed at the end.
**Reviews ingested**: 6 (in [`./2026-05-08-ti-layer-design_REVIEWS/`](./2026-05-08-ti-layer-design_REVIEWS/)).
**Author input also folded in**: switch to **0-indexed options** (matches the original StS1 mod's `#0, #1, #2…` convention).

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key focus areas | Unique insight |
|---|---|---|---|
| **R1** | Mostly positive | IRC complexity, dispatcher coupling, retry budget | "Source-included library" middle path for IRC |
| **R2** | Mixed (positive on seam; critical on dependencies and IRC realities) | Godot leak in `Internal`, TCP stream fragmentation, frame stuttering, parser strictness | Inject the dispatch action via constructor (`Action<Action>`) — purges Godot from `Ti/Internal` |
| **R3** | Mixed; comprehensive | Threading edge cases (`CallDeferred` ordering, autoload registration), RECONNECT/NOTICE handling, rate limiter, tag mapping | Vote confirmation via `@reply-parent-msg-id` for individual lag-free receipts |
| **R4** | Mixed; most thorough | `ChatMessage` missing `UserId`, fake-clock vs `System.Threading.Timer` mismatch, static `Voter`, dispose semantics, receipt ordering races, IRC protocol gaps | State-machine sections for both `VoteSession` and `ChatService`; channel normalization |
| **R5** | Mixed; practical | Auth-failure footgun, missing rate limiter, `ChatMessage.UserId`, `FakeClock` insufficient without scheduler | **Self-echo prevention** — our own tally announcements would re-enter as votes (`#1`, `#2` literally appear in the message we send) |
| **R6** | Positive with focused critiques | **StS2 multiplayer breaks `Voter` singleton**, sub/mod tag exposure, IRC trade-off awareness | **Co-op multiplayer** — two players with separate Twitch channels need two coordinators; static `Voter.CurrentSession` can't model that |

---

## A.2 — Consensus Points (≥2 reviewers)

Ranked by reviewer count and impact.

1. **Static `Voter` / `Voter.CurrentSession` is a problem** (R2, R3, R4, R5, R6 — 5/6). R6 added the killer concrete: StS2 has co-op; two streamers each running this mod need two coordinators. Static state can't model that.
2. **Threading model leaks Godot into `Ti/Internal`** (R1, R2, R3, R4, R5 — 5/6). The spec's own dependency rule says Godot is allowed only in `Ti/Ui/*`, but `GameThreadDispatcher` uses `Godot.CallDeferred`. Need an `IMainThreadDispatcher` interface; Godot impl lives outside `Ti/Internal` (or in a clearly-marked Godot-binding sub-folder).
3. **`AwaitWinnerAsync` / `Dispose` cancellation and disposal semantics are underspecified** (R1, R2, R3, R4, R5 — 5/6). Multiple reviewers want explicit `CloseNow()` / `Cancel()` methods, plus a defined `VoteSession` state machine that distinguishes Closed-with-winner from Cancelled.
4. **Auth-failure handling is missing** (R1, R3, R4, R5; R2 implicit — 5/6). The spec's "infinite retry with exponential backoff" loops forever against a wrong oauth token. Need a terminal `AuthenticationFailed` state on bad-credential `NOTICE` from Twitch, don't retry.
5. **Parser regex needs work** (R1, R2, R3, R4, R5 — 5/6). The original spec's `#(\d+)` was both too loose (matches `C#1`, `abc#1`, URL fragments) and too strict (rejects bare numbers, `!N`). Our session-2 update to `^#?(\d+)(?:\s|$)` already addresses some of this, but R4 still wants stricter `[1-9]` single-digit, R3 still wants `!N` support behind a flag.
6. **`ChatMessage` missing stable user-id** (R3, R4, R5; R6 implicit via badge tags — 4/6). The spec text says "tally keys on Twitch user-id from CAP tags" but the `ChatMessage` record only exposes `User` (ambiguous: login? user-id?). Real bug.
7. **Outgoing rate limiter missing from `IChatService`** (R3, R4, R5; R6 implicit "show the math" — 4/6). `VoteSession`'s default cadence is safe, but `IChatService.SendMessageAsync` is public with no guardrail; future consumers (or a second mod sharing the bucket) can blow the limit.
8. **Test scheduler missing — `FakeClock` alone insufficient** (R3, R4, R5 — 3/6). Spec uses `System.Threading.Timer`, which is driven by real wall-clock and ignores `FakeClock`. Need an `ITimerScheduler` (or `TimeProvider`) so vote timers can be advanced deterministically in tests.
9. **`Ti` namespace is unclear / hard to grep** (R1, R3, R4, R5 — 4/6). Reads like the chemical symbol or a generic prefix. Either rename to `TwitchIntegration` or document the expansion prominently.
10. **Logging dependency on `MegaCrit.Sts2.Core.Logging.Log` is too tight** (R2, R4, R5 — 3/6). Want a thin `ITiLogger` (or `TiLog` static) shim so `Ti/*` is fully StS2-agnostic at the source level.
11. **Receipt text hardcoded in English** (R3 implicit, R4 explicit, R5 explicit — 3/6). Want `IVoteReceiptFormatter` so receipts are i18n-able and unit-testable without an `IChatService` mock.
12. **IRC protocol handling under-specified** (R3, R4 — 2/6 explicit, others implicit). RECONNECT, NOTICE (auth/join failure), CAP ACK/NAK, USERSTATE, ROOMSTATE, IRCv3 tag escaping all need a defined v0.1 behavior.
13. **`DateTime` → `DateTimeOffset`** (R4, R5 — 2/6). Unambiguous timezone handling for logs and timestamps.
14. **Handcrafted IRC LOC estimate is low by 2-3×** (R4, R5 — 2/6). Realistic with TLS, CAP, tags, RECONNECT, rate limiter, etc. is closer to 500-700 LOC. Bump the estimate; keep the decision.
15. **Anonymous mode + receipts are inconsistent** (R3, R4 — 2/6). If `creds == null`, `SendMessageAsync` fails — but `VoteSession`'s default policy sends receipts. Need to either skip receipts in anonymous mode or refuse to start votes there.
16. **TCP stream fragmentation** (R2 explicit; assumed by all). `Receive` doesn't return whole lines. Use `StreamReader.ReadLineAsync` (or buffer explicitly).
17. **`ChatCredentials` `record` auto-`ToString` dumps the oauth token** (R4, R5 — 2/6). Override `ToString` or stop using `record`.

---

## A.3 — Outlier Points (single reviewer, but worth weighing)

- **R6 — Co-op multiplayer breaks the static singleton.** Single reviewer raised it but it's the clinching argument for the consensus point #1 above. Promoted from outlier to load-bearing.
- **R5 — Self-echo: our own tally messages would re-enter as votes.** Critical and unique. Bot sends `"Vote: #1=12 #2=8 #3=3, 15s left."` — the parser sees `#1` (or `1` with our v1.5 regex) at start-of-message? Actually no: the announcement starts with `"Vote: …"`, not with a `#`. **Reality check**: with the current regex `^#?(\d+)(?:\s|$)`, the leading `Vote:` text means the regex anchored to `^` won't match. *But* the open-message format is `"Vote: card reward! Type 1, 2 or 3 — 30s left."` — that doesn't start with a digit either. So the bot's own messages won't currently match the parser. **However**, if anyone changes the receipt format and forgets, this becomes a silent bug. Cheapest defense: filter own messages by username at the IRC layer (`if msg.user-id == self.user-id: skip`). Adopting.
- **R3 — Vote confirmation via `@reply-parent-msg-id`.** Per-voter individual confirmation without rate-limit risk (one reply per unique voter per change). Good idea but post-MVP — added to Consider list.
- **R4 — Channel normalization** (`foo` and `#foo` both work). Trivially cheap. Adopting.
- **R5 — Heartbeat timeout** (proactively reconnect if no traffic in 5 min). Reasonable defensive measure. Consider list.
- **R5 — Viewer-id dict size cap** (defense vs. botnet OOM). Cheap. Adopting (default cap 10k).
- **R6 — Sub/mod/VIP tags exposed in `ChatMessage` even if unused in v0.1.** Free with tag parsing; future filter is a where-clause not a re-architecture. Adopting.
- **R5 — `TaskCompletionSource.RunContinuationsAsynchronously`** to avoid main-thread reentrancy when consumers `await` from a Harmony patch. Adopting.
- **R3 — `ToString` on `VoteSession` for debugging.** Trivially valuable. Adopting.
- **R5 — Observability counters** (messages received, votes accepted, votes out-of-range, reconnect attempts, rate-limit deferrals). Saved many a "chat voting didn't work" support thread. Adopting (basic counters; full metrics post-MVP).
- **R3 — `LastMessageReceivedAt` connection health metric.** One field, big diagnostic value. Adopting.

---

## A.4 — Category Breakdown

### 🏗️ Architecture & Design

- **Two-tier ChatService/VoteSession seam** — universally praised. **Validated against codebase**: the seam holds; `IChatService` carries no vote semantics in the v1 spec. Stays as-is.
- **Static `Voter` → instance `VoteCoordinator`** (R2, R3, R4, R5, R6). **Validated**: spec uses `static class Voter { static VoteSession? CurrentSession }`. R6's multiplayer argument is decisive. **Must-do** in v2.
- **`GameThreadDispatcher` Godot coupling** (R1, R2, R3, R4, R5). **Validated**: spec puts `GameThreadDispatcher.cs` in `Ti/Internal/` and uses `CallDeferred` directly, violating the spec's own dependency rule that Godot is allowed only in `Ti/Ui/*`. **Must-do**: introduce `IMainThreadDispatcher` interface; Godot impl lives in `Ti/Godot/` (new sub-namespace) or `Game/Infrastructure/`.
- **`Ti` namespace ambiguity** (R1, R3, R4, R5). I'm leaving the choice to the user — both options are acceptable. **Should-do**: rename `Ti` → `TwitchIntegration` or document the expansion in the README header. *V2 keeps `Ti` but adds an explicit expansion note in the architecture section.*
- **`VoteOption.Index` redundant** (R3). **Validated**: yes, the index is always position-in-list. Could be inferred. **Reject for v2**: keeping `Index` makes 0-indexed vs 1-indexed unambiguous in the API and matches the chat command directly. The cost is one extra field; the benefit is clarity. (R3's concern is about misuse — `VoteOption(5, "wrong index")` — addressed by making the constructor internal/factory-only.)
- **Receipt formatter as separate type** (R4 alternative 5; implied by R3, R5). **Should-do**: introduce `IVoteReceiptFormatter` with `DefaultEnglishFormatter`. Improves testability and future-proofs i18n.

### ⚠️ Risks & Concerns

- **Auth-failure infinite retry** (R1, R3, R4, R5). **Must-do**: detect Twitch's auth-failure NOTICE; don't retry. Terminal `AuthenticationFailed` connection state.
- **TCP stream fragmentation** (R2). **Validated**: spec says "lines are CRLF-delimited; pass each through TwitchIrcParser." TCP doesn't deliver lines. **Must-do**: explicitly use `StreamReader.ReadLineAsync` (or buffered framing).
- **`CallDeferred` storm during chat brigade** (R2, R3, R6). **Must-do**: dispatcher batches into a single `CallDeferred` per frame using a `ConcurrentQueue<Action>` drained from `_Process` (R3 alt 5.1 or R5 batching).
- **Receipt ordering races** (R4). **Must-do**: serialize all outgoing messages through a single send queue inside `TwitchIrcChatService`; specify the close-sequence ordering precisely.
- **Anonymous mode + receipts mismatch** (R3, R4). **Must-do**: `IChatService` exposes `bool CanSend`; `VoteSession` checks `CanSend` before sending receipts and silently skips them if false. No warnings.
- **Self-echo bug latent in receipt regex** (R5). **Must-do**: filter messages where `msg.UserId == self.UserId` at the IRC layer.

### 🗑️ Suggested Removals / Simplifications

- **Static `Voter.CurrentSession` public getter** (R5). **Adopting**: removed in v2; coordinator is instance-based.
- **`VoteReceiptPolicy.Silent`** (R5). **Reject**: it's a 1-line static; harmless. Keeping.
- **`[Export] AnchorPosition` on `VoteOverlayControl`** (R5). **Validated**: Godot `Control` already exposes anchor properties. **Adopting** — drop the redundant `Export`.
- **"200 LOC" anchor in IRC rationale** (R4, R5). **Adopting**: bump to "500–700 LOC, plus ~200 LOC of tests".
- **"Safe under Twitch's 100 msg / 30 s" as primary rate-limit claim** (R4). **Adopting**: rephrase to acknowledge regular-account 20/30s + 1/sec/channel limits, and add an explicit conservative rate limiter.
- **"Dispose chooses a winner"** (R4). **Adopting**: split into explicit `CloseNow()` (compute and announce) and `Cancel()` (abort without winner). `Dispose()` becomes idempotent cleanup that calls `Cancel()` if not already closed.

### ➕ Suggested Additions / Features

- **`CancellationToken` on `Voter.Start` / `VoteCoordinator.Start`** (R1, R2). **Adopting**.
- **`ChatMessage.UserId` (and IsSubscriber/IsModerator/IsVip flags)** (R3, R4, R5, R6). **Adopting**.
- **Outgoing send queue with rate limiter inside `TwitchIrcChatService`** (R3, R4, R5). **Adopting**: 90 msg / 30 s default ceiling (under the 100 limit), 1 msg/sec/channel floor for non-broadcaster accounts. Priority queue: close > open > periodic-tally; periodic tallies coalesce/skip if backed up.
- **`ITimerScheduler`** (or extend `IClock` to include scheduling) (R3, R4, R5). **Adopting**: new `ITimerScheduler` with `SystemTimerScheduler` and `FakeTimerScheduler` (the latter advancing deterministically with `FakeClock`).
- **`CanSend` and richer `ChatConnectionState` enum** (R3, R4, R5). **Adopting**.
- **IRC protocol handling table** (R3, R4). **Adopting** — explicit per-command behavior matrix.
- **VoteSession state machine** (Created → Open → Closing → Closed | Cancelled | Disposed) (R3, R4, R5). **Adopting**.
- **ChatCredentials redact `ToString`** (R4, R5). **Adopting**.
- **`DateTimeOffset` instead of `DateTime`** on `ChatMessage.ReceivedAt` (R4, R5). **Adopting**.
- **`ITiLogger` shim** (R2, R4, R5). **Adopting**: trivial interface; default impl wraps `MegaCrit.Sts2.Core.Logging.Log`.
- **Channel normalization** (`foo` vs `#foo` vs URLs) (R4). **Adopting**.
- **Self-echo filter** (R5). **Adopting**.
- **Viewer-id dict size cap** (R5). **Adopting** — 10k default.
- **`LastMessageReceivedAt`** on `IChatService` (R3). **Adopting**.
- **`ToString` on `VoteSession`** (R3). **Adopting**.
- **Basic observability counters** (R5). **Adopting**.
- **`TaskCompletionSource.RunContinuationsAsynchronously`** (R5). **Adopting**.
- **Heartbeat timeout** (R5). **Consider** (post-MVP): add to optional-enhancements list.
- **Reply-parent-msg-id per-voter receipts** (R3). **Consider**: future feature; in optional-enhancements list.
- **Connection-state metrics dashboard** (R5). **Consider** (post-MVP).
- **IRC test-fixture generator tool** (R3). **Consider**.

### 🔄 Alternative Approaches

- **Vendor a single-file Twitch IRC library** (R1, R5). **Reject for v2**: the handcrafted decision was deliberate and the spec already commits. The "vendor a known-good source file" idea is a viable Plan B — flagged in v2 as a fallback if the handcrafted client proves problematic during implementation.
- **`TimeProvider` over custom `IClock`** (R4, R5). **Consider**: defer to implementation. The custom `IClock` + `ITimerScheduler` shape is fine; can swap to BCL `TimeProvider` if it integrates cleanly.
- **Adaptive tally cadence** `max(5s, duration/5)` (R3, R5). **Consider**: post-MVP polish.
- **Vote confirmation via reply-thread** (R3). **Consider**: post-MVP feature for streamers who specifically want it.
- **Separate `VoteReceiptAnnouncer`** (R4 alt 5). **Reject for v2** — `IVoteReceiptFormatter` covers the testability win without splitting the announcer.
- **Thread-neutral `ChatService` + `MainThreadChatServiceDecorator`** (R4 alt 2). **Consider**: cleaner extraction for non-Godot harness use, but adds complexity. Defer.

### ✅ Confirmed Good / Keep As-Is

Universally praised; preserved unchanged in v2:
- Two-tier `ChatService` / `VoteSession` seam.
- Strictly-one-vote-at-a-time invariant (now enforced at coordinator instance, not statics).
- Latest-vote-wins tally semantics.
- Open + periodic + close receipt strategy.
- `FakeChatService` + (now) `FakeClock` + `FakeTimerScheduler` + seeded `Random` test design.
- Optional UI overlay (`VoteOverlayControl`).
- Disconnect-tolerance semantics.
- Read-only-plus-announcements I/O surface.
- Test project source-referenced (R4 questioned this; reject — for greenfield mod with no public API stability yet, source-referenced is lighter than `InternalsVisibleTo`).
- Rejected `ChatCommandRouter` for v0.1 (universal agreement YAGNI).
- Random tie-break and no-voter-random-pick.

### 🔧 Implementation Details & Nits

- **`Tallies` should include zero entries for every option** (R4). **Adopting**.
- **`TallyChanged` should not fire if vote is unchanged** (R4). **Adopting** (already in spec; will add a test).
- **Receipts deduplicate identical tally messages** (R5). **Adopting**.
- **UI redraws driven by `_Process`, not per-event** (R6). **Adopting**: `VoteOverlayControl` reads state in `_Process`, doesn't redraw on every `TallyChanged`.
- **Add jitter to reconnect backoff** (R5). **Adopting**.
- **Open receipt should include compact labels** (`"Type 0 Bash, 1 Defend, 2 Strike"`) (R4). **Adopting**.
- **Validate option labels** (non-empty, max length, strip control chars) (R4). **Adopting**.

### 📦 Dependencies & Integration

- **Autoload-from-mod-assembly is non-trivial** (R3). **Validated**: Godot's `AddAutoloadSingleton` requires a path to a scene file or a Node-class reference. Mod-assembly path is doable via `ProjectSettings.SetSetting("autoload/X", ...)` but it's runtime registration. **Adopting**: spec adds an explicit "GameThreadDispatcher autoload registration" subsection covering the path; falls back to a `_Process`-driven static dispatcher if autoload registration fails.
- **`MegaCrit.Sts2.Core.Logging.Log` thread-safety unverified** (R3, R5). **Adopting**: TODO in v2 to verify before implementation; if not thread-safe, the `ITiLogger` shim's default impl will buffer and flush on the main thread.

### 🔮 Future Considerations

These are flagged in v2's "Future work" section:
- Lifting `Ti/*` into a separate base-mod assembly (already in v1).
- Subscriber/mod/VIP-only voting filters (data exposed in v2; filter logic deferred).
- Channel-points / Twitch Extension overlays (already out-of-scope).
- Localised receipts via additional `IVoteReceiptFormatter` impls.
- Multi-channel/multi-streamer support (now possible since `Voter` is instance-based; full implementation is a v0.2 call).

---

## A.5 — Conflicts & Contradictions

### Parser strictness

- **R1**: parser too strict; suggest priority-based parser accepting `#N`, then `!N`.
- **R2**: parser too strict; suggest `(?:^|\s)[#!]?(\d+)(?:\s|$)`.
- **R3**: parser too strict; suggest `VoteCommandStyle` enum, default strict.
- **R4**: parser too **loose** (matches `C#1`, `abc#1`); suggest stricter `(?<!\S)#([1-9])(?!\d)` for single-digit only.
- **R5**: parser too strict; suggest behind-flag `!N` support, default strict.

**My read**: our session-2 update to `^#?(\d+)(?:\s|$)` already addresses R4's "too loose" concerns (anchored to start, terminated by space/EOL). For R1/R2/R3/R5's "still too strict" concerns, adopt R3/R5's middle path: extend to also accept `!N` behind a `VoteParsingPolicy` flag, default off.

**v2 decision**: parser regex is `^[#!]?(\d+)(?:\s|$)` when `VoteParsingPolicy.AllowBangCommands == true`, else `^#?(\d+)(?:\s|$)`. Default is `true` (lean permissive — Twitch chat conditioned on `!command` is real).

### Source-referenced tests vs. `InternalsVisibleTo`

- **R4**: source-reference creates a different compilation environment from the actual mod assembly; prefer `InternalsVisibleTo`.
- All other reviewers: silent or implicitly accept source-reference.

**My read**: R4's concern is theoretical. For a greenfield mod with no public API contract yet, source-referenced tests are lighter. The risk of "different compilation environment" is real for shipping libraries but minor for a mod assembly built with the same csproj settings. **v2 decision**: keep source-referenced; revisit if a compilation divergence actually bites.

### Dispose semantics

- **R3, R4, R5**: `Dispose` shouldn't choose a winner; it's surprising.
- **Original spec**: `Dispose` triggers an early close (compute winner).

**v2 decision**: split into explicit `CloseNow()` (compute and announce winner immediately), `Cancel()` (abort without winner; pending awaits cancel), and `Dispose()` (idempotent cleanup; calls `Cancel()` if open). Adopts R4's API shape.

---

## A.6 — Recommended Plan Changes

### Must-do (high consensus, high impact, real risks)

| # | Change | Reviewers | Section affected |
|---|---|---|---|
| 1 | Replace static `Voter` with instance `VoteCoordinator`; keep static facade only as a thin convenience | R2, R3, R4, R5, R6 | VoteSession, Voter |
| 2 | Add `IMainThreadDispatcher` interface; Godot impl outside `Ti/Internal/` | R1, R2, R4, R5 (R3 raised the autoload concern) | Architecture, Threading |
| 3 | Add `UserId`, `Login`, `IsSubscriber`, `IsModerator`, `IsVip`, `BadgeFlags` to `ChatMessage`; tally keys on `UserId`; fall back to `login:<login>` if untagged | R3, R4, R5, R6 | ChatService |
| 4 | Detect Twitch auth-failure `NOTICE`; introduce terminal `AuthenticationFailed` state; don't retry | R1, R3, R4, R5 | TwitchIrcChatService |
| 5 | Add outgoing send queue + rate limiter inside `TwitchIrcChatService` (90/30s ceiling, 1/sec/channel for non-broadcaster, priority: close > open > periodic; coalesce stale tallies) | R3, R4, R5, R6 | TwitchIrcChatService |
| 6 | Introduce `ITimerScheduler` (with `SystemTimerScheduler` and `FakeTimerScheduler`); `VoteSession` uses scheduler, not `System.Threading.Timer` | R3, R4, R5 | Internal, Testing |
| 7 | Split `Dispose` into explicit `CloseNow()` / `Cancel()`; specify VoteSession state machine (Open → Closing → Closed \| Cancelled → Disposed) | R1, R2, R3, R4, R5 | VoteSession |
| 8 | Specify shutdown lifecycle (signal IRC loop → await with timeout → drain dispatcher → dispose autoload → dispose socket) | R3, R4, R5 | Lifecycle |
| 9 | Self-echo filter at IRC layer (`if msg.UserId == self.UserId: skip`) | R5 | TwitchIrcChatService |
| 10 | Switch to **0-indexed options** (`#0, #1, #2…`); chat-index = list-index = WinnerIndex | User input + matches original StS1 | All API and receipts |
| 11 | Add `CancellationToken` to `Voter.Start` / `VoteCoordinator.Start`; specify cancellation cancels only awaiting caller, not the session | R1, R2, R3, R4 | Voter / VoteCoordinator |
| 12 | Add IRC protocol handling matrix (PING, PRIVMSG, RECONNECT, NOTICE, CAP ACK/NAK, USERSTATE, ROOMSTATE, CLEARCHAT/CLEARMSG/USERNOTICE) with explicit v0.1 behavior | R3, R4 | TwitchIrcChatService |
| 13 | Use `StreamReader.ReadLineAsync` (or explicit framing) to handle TCP stream fragmentation | R2 | TwitchIrcChatService |
| 14 | Add rich `ChatConnectionState` enum (Disconnected, Connecting, ConnectedReadOnly, ConnectedReadWrite, Reconnecting, AuthenticationFailed, JoinFailed, Disposed); expose `CanSend`; `VoteSession` skips receipts when `CanSend == false` | R3, R4, R5 | ChatService |

### Should-do (strong suggestions; meaningfully improve the spec)

| # | Change | Reviewers |
|---|---|---|
| 15 | Decouple logging via `ITiLogger` shim (default impl wraps `MegaCrit.Sts2.Core.Logging.Log`) | R2, R4, R5 |
| 16 | Introduce `IVoteReceiptFormatter` with `DefaultEnglishReceiptFormatter`; receipts become unit-testable without IRC mocking | R3, R4, R5 |
| 17 | `DateTime` → `DateTimeOffset` on `ChatMessage.ReceivedAt` | R4, R5 |
| 18 | `ChatCredentials.ToString` redacts oauth token; accept `oauth:`-prefixed or raw token, normalize internally | R4, R5 |
| 19 | Channel normalization (`foo`, `#foo`, URLs accepted; lowercased login) | R4 |
| 20 | Bump handcrafted IRC LOC estimate to "500–700 LOC + ~200 LOC tests" | R4, R5 |
| 21 | `TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)` for `AwaitWinnerAsync` | R5 |
| 22 | `VoteOverlayControl` redraws driven from `_Process`, not per-event | R6 |
| 23 | Anonymous-mode + receipts: `VoteSession` checks `CanSend` and silently skips if false | R3, R4 |
| 24 | Viewer-id dict size cap (10k default; warn-once on overflow) | R5 |
| 25 | `LastMessageReceivedAt` on `IChatService` for diagnostic UI | R3 |
| 26 | `ToString` override on `VoteSession` for debugging | R3 |
| 27 | Basic observability counters (messages received, votes accepted/rejected, reconnect attempts, rate-limit deferrals) | R5 |
| 28 | Add jitter to reconnect backoff | R5 |
| 29 | Rate-limit math shown in receipts section explicitly | R6 |
| 30 | `Tallies` includes zero entries for every option, not only voted ones | R4 |
| 31 | Receipt dedupes identical tally messages | R5 |
| 32 | Validate option labels (non-empty trimmed; max length; strip CR/LF/control chars) | R4 |
| 33 | Open receipt includes compact labels (`"Type 0 Bash, 1 Defend, 2 Strike"`) | R4 |
| 34 | Drop redundant `[Export] AnchorPosition` on `VoteOverlayControl` (Godot Control already has anchors) | R5 |
| 35 | Document `Ti` namespace expansion prominently in architecture section header | R1, R3, R4, R5 |
| 36 | Add `VoteParsingPolicy` flag for `!N` acceptance; default `true` | R1, R3, R5 |

### Consider (good ideas; ride at the bottom of v2 for explicit pick-or-skip)

See Optional Enhancements section in [`2026-05-08-ti-layer-design-v2.md`](./2026-05-08-ti-layer-design-v2.md).

### Reject (with reason)

| Suggestion | Reviewer | Reason |
|---|---|---|
| Remove `VoteOption.Index` field, infer from list position | R3 | Keeping makes 0-indexed convention explicit in the API. Constructor made internal-only to defuse the misuse concern. |
| Drop source-referenced tests; use `InternalsVisibleTo` | R4 | For a greenfield mod, source-reference is lighter and the "compilation divergence" risk is theoretical. Revisit if it bites. |
| Vendor a single-file Twitch IRC library | R1, R5 | Handcrafted is a deliberate decision; flagged as a Plan B in v2 if the implementation proves problematic. |
| `VoteReceiptPolicy` → `VoteReceiptMode` enum | R4 | The record shape supports future i18n / per-streamer config; enum collapses prematurely. |
| Drop `VoteReceiptPolicy.Silent` static | R5 | One-line convenience preset; harmless. |
| Separate `VoteReceiptAnnouncer` | R4 | `IVoteReceiptFormatter` covers the testability win without splitting the announcer responsibility. |
| Thread-neutral `ChatService` + `MainThreadChatServiceDecorator` | R4 | Adds complexity; the dispatcher abstraction (R1/R2/R5) covers the same goal more cheaply. |

---

## A.7 — What Stays (preserved unchanged in v2)

These elements got universal or near-universal endorsement:

1. **Two-tier `ChatService` / `VoteSession` seam.** The spec's clearest win.
2. **Strictly-one-vote-at-a-time invariant.** Now enforced per-coordinator-instance (multiplayer-safe).
3. **Latest-vote-wins tally semantics.** Better UX than the original first-wins.
4. **Open + periodic + close receipt strategy.** Genuinely solves the lag problem.
5. **`FakeChatService` + injectable `Random`** test design (now joined by `FakeClock` + `FakeTimerScheduler`).
6. **Optional `VoteOverlayControl`** as Godot `Control` consumer of `VoteSession` events (now `_Process`-driven).
7. **Read-only-plus-announcements I/O surface.** Out-of-scope decisions stay out.
8. **No `ChatCommandRouter` middle tier** for v0.1.
9. **Random tie-break + random no-voter pick** with transparent receipts.
10. **Disconnect-tolerance** (session preserved through IRC outages).
11. **Manifest** (`affects_gameplay: true`, `has_pck: false` for v0.1).
12. **Mod-agnostic-within-StS2** scope philosophy (not cross-game).
13. **Handcrafted Twitch IRC client** as the dependency-free chat-IO foundation (just bumped LOC estimate).

---

## Next steps

The updated spec is at [`2026-05-08-ti-layer-design-v2.md`](./2026-05-08-ti-layer-design-v2.md). Must-do and Should-do changes are applied inline with `<!-- CHANGED -->` comments. Consider-tier items appear as "Optional Enhancements" at the end of v2 — pick which ones (by number) to fold in, or leave them as future work.

Once v2 is approved (with whatever Optional Enhancements you select), the next step in the brainstorming flow is `writing-plans` to produce the implementation plan.
