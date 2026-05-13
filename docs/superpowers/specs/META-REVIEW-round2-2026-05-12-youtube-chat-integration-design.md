# Round-2 Meta-Review — YouTube Chat Parallel Integration (design v3)

**Date**: 2026-05-12
**Subject**: [`2026-05-12-youtube-chat-integration-design-v3.md`](./2026-05-12-youtube-chat-integration-design-v3.md)
**Output spec**: [`2026-05-12-youtube-chat-integration-design-v4.md`](./2026-05-12-youtube-chat-integration-design-v4.md)
**Reviewers**: 5 (round-2 pass on v3, post-C1/C2/C3/C4/C9 application)
**Round 1 meta-review**: [`META-REVIEW-2026-05-12-youtube-chat-integration-design.md`](./META-REVIEW-2026-05-12-youtube-chat-integration-design.md) — v1 → v2 transition.

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key focus | Unique insight |
|---|---|---|---|
| R1 | **Mixed-critical** (~3500 words, 8 blockers + 10 nits) | Contract correctness; specification tightness; PII redaction | Health-check telemetry should redact chat content/PII before logging |
| R2 | **Mixed-positive** (~1500 words, 5 substantive concerns) | C3 scope honesty; over-engineering callouts | **C3 is a Plan A vote-engine change touching ~7 files**, not "~40 LOC in Ti/Voting/" — spec should be honest about scope |
| R3 | **Strongly positive** (~150 words; sanity-check only) | Endorses implementation-ready | Verify `ScraperVersion` log helpfulness by intentionally breaking a regex during testing |
| R4 | **Mixed-critical** (~3800 words, 12 ranked concerns) | Concurrency; latest-wins algorithm; ConfiguredPlatforms | **Latest-wins per-platform tally decrement algorithm not specified**; cross-thread state convention from codebase not carried into YT service |
| R5 | **Strongly positive** (~700 words; "implementation-ready") | Acknowledges previous review items addressed | MultiChatService constructor lambda subscriptions never unsubscribed; `_consecutive429Count` reset wording loose |

---

## A.2 — Consensus Points

### 4-5/5 — Near-unanimous

**C-1. Escalation receipt mechanism is hand-wavy ("event or static facade")** — R1 #6, R2 #2, R4 High 11, R5. **4/5.**
- v3 says: `YouTubeChatService exposes an event EscalationRequested (or just calls ModEntry.SendEscalationReceipt() via a static facade)`.
- Static facade would create reverse dependency `Ti/Chat/YouTubeChat/` → `ModEntry`, **violating the TI/Game seam this very spec exists to preserve**.
- **Remedy**: Pin **event-only**. Drop the static-facade alternative entirely.

**C-2. No-op `IChatConsumer.ConnectionStateChanged` implementation is a new LSP smell** — R1 #1, R4 Critical 3, R5 minor. **3/5 explicit**.
- v3 fixes the v2 `ConnectAsync` throw with the IChatConsumer split, but introduces a new contract lie: the no-op event implementation.
- **Verified against code**: `VoteSession.cs` line 85 subscribes to `_chat.ConnectionStateChanged` for disconnect-gap tracking. If `MultiChatService.ConnectionStateChanged` is a no-op, `VoteSession`'s disconnect-gap tracking **breaks silently** when the chat is wrapped in a multi.
- **Remedy**: Keep the event in `IChatConsumer`; implement it properly in `MultiChatService` (cache `_lastAggregateState`; emit when aggregate changes). Keep `ChildConnectionStateChanged` for per-child receipts. This is **Round-1 R1 C5's caching concern resurfaced** — we dropped the aggregate event in v2 thinking nothing used it; that was wrong.

### 2-3/5

**C-3. `VoteTallyLabel` doesn't render configured-but-silent platforms** — R1 #2, R4 Critical 1. **2/5 + Round-1 echo**.
- v3 pseudocode: `if (!perPlatform.Keys.Any(k => k.Platform == platform)) continue;` — skips platforms that haven't voted yet. Contradicts Supported-degraded-modes spec text ("if YT is configured but disconnected, the label still shows the YouTube row with zero counts") AND Decision 10 ("gates on configuration not observation").
- **Verified**: Mid-vote scenario — Twitch votes first; YT row not rendered until first YT vote arrives. Same single→split snap v3 was supposed to fix.
- **Remedy**: Expose `IReadOnlyList<string> ConfiguredPlatforms` on `VoteSession`; iterate over it (not over observed tally keys). Per R4 High 9, this also replaces the bare `bool IsMultiPlatformConfigured`.

**C-4. Vote ID not visible in in-game overlay** — R1 #3, R4 Critical 2, R5 (related concern about `[42]` glyph confusion). **3/5**.
- v3 vote-nonce: receipt format `Vote [42]: #0 Strike...` but `VoteTallyLabel` header still `Chat voting — 30s left`. YT viewers (the people the nonce most benefits) **never see the ID** because YT is read-only.
- **Remedy**: Update `VoteTallyLabel._Process` header: `Chat voting [42] — 30s left`.

**C-5. `YouTubeChatStatusReason` is defined but not mechanically wired in `TransitionTo`** — R1 #5, R4 Critical 4. **2/5**.
- v3 specifies the enum + `LastStatusReason` property, but the shown `TransitionTo(state, reason)` pseudocode doesn't accept a reason parameter; poll-loop call sites don't pass one.
- **Remedy**: Update `TransitionTo` signature to `(ChatConnectionState, string reason, YouTubeChatStatusReason statusReason = None)`. Update all call sites to pass the reason. Reset to `None` on successful `ConnectedReadOnly`. Add tests asserting `LastStatusReason` for each failure path.

**C-6. 429 handling via `HttpRequestException` inspection is mechanically brittle** — R1 #7, R4 Critical 5. **2/5**.
- v3 pseudocode: `if (lastError is HttpRequestException httpEx && IsHttp429(httpEx))`. But `Retry-After` lives on `HttpResponseMessage.Headers`, not naturally on the exception. `HttpRequestException.StatusCode` is .NET 5+ but unreliable depending on how the exception was raised.
- **Remedy**: Define an internal typed exception `YouTubeHttpStatusException` (or a result object) that captures `StatusCode` and `RetryAfter`. `IYouTubeHttp` throws this on non-success. `NextReconnectDelay` keys on `StatusCode == TooManyRequests`.

**C-7. `IsMultiPlatformConfigured` bool is not enough; needs `ConfiguredPlatforms` list** — R4 High 9, R5 (derivation contradiction). **2/5 + R1 #2 implicit dependency**.
- v3 has contradictory wording: text says "VoteCoordinator derives it from the IChatConsumer"; pseudocode shows `new VoteCoordinator(chat, /* IsMultiPlatformConfigured */ youtube is not null, ...)`.
- A bool answers "single vs multi" but not "which rows to show" (needed for C-3 fix).
- **Remedy**: Replace the bool with `IReadOnlyList<string> ConfiguredPlatforms` constructor parameter on `VoteCoordinator` → propagated to `VoteSession`. Explicit, no type-inspection of `IChatConsumer`.

**C-8. Noita-pattern regression analysis section is stale** — R1 #9, R4 High 12. **2/5**.
- v3 still contains v2's lean-no recommendation for C3 even though C3 was promoted to Decision 11.
- The v2 Optional Enhancements detail entries section (preserved "for context") similarly says C3 is Consider with neutral recommendation.
- **Remedy**: Rewrite the Noita section to reflect "C3 applied as Decision 11; supersedes notes/06 entry; operator-validation should measure adoption." Remove or mark obsolete the detail entries section for items now applied.

### 1-2/5 — singletons or smaller consensus

**C-9. `InvalidOrUnavailableChannel` enum value never emitted; conflicts with D7** — R2 #3, R4 minor. **2/5**.
- v3 enum includes it with comment "collapsed to NoLiveBroadcastFound for v1." Never emitted = dead code.
- D7 explicitly says we don't disambiguate permanent 404s.
- **Remedy**: Remove the enum value. Add back in v0.2 if/when disambiguation is implemented.

**C-10. C4 telemetry over-engineered for v1** — R2 #4. **1/5**.
- v3 has `_consecutiveParseFailuresAt : Dictionary<string, int>` with string keys for each failure location.
- For ~4 distinct failure points, a `Dictionary<string, int>` is heavy. Simpler: single `(string? lastLocation, int count)` tuple.
- **Remedy**: Simplify per R2. Same diagnostic value; less code.

**C-11. Latest-wins per-platform tally decrement algorithm not specified** — R4 High 8. **1/5 but real correctness gap**.
- v3 says side-dict parallel to merged tally; doesn't show the update sequence on vote-change.
- **Verified against code**: `VoteSession._votersByKey : Dictionary<string, int>` stores option index, not platform. For latest-wins to work on per-platform side-dict, the decrement needs the **prior vote's platform**. Since voter identity is stable (same VoterKey across a session), `PlatformOf(msg)` for the current message gives the platform for the prior vote too (same voter, same prefix). So the algorithm is straightforward — but it should be explicit.
- **Remedy**: Add explicit pseudocode to the per-platform tally section.

**C-12. Cross-thread/concurrency spec missing for `YouTubeChatService`** — R4 High 6. **1/5 but project convention says interlocked**.
- CONTEXT doc §5 says "Interlocked-int flags for cross-thread state, not bool." v3 uses raw `bool _disposed`, raw `ChatConnectionState _state`. Misses the convention.
- **Remedy**: Add a concurrency subsection. Use `int _disposed` with `Interlocked`. State changes go through one synchronized `TransitionTo`. Retry-timer / dispose race covered.

**C-13. Fire-and-forget ConnectAsync unobserved exceptions** — R4 High 7. **1/5**.
- `_ = youtube.ConnectAsync(...);` in `ModEntry`. If `ConnectAsync` throws synchronously, exception is unobserved.
- **Remedy**: Spec ConnectAsync contract: must never throw for external failures; catches internally; routes through state machine + retry. Add a one-line clarification.

**C-14. Vote ID formatting under-specified** — R1 #4, R5 (related). **2/5**.
- v3 says "2-digit ID (0–99 cycling)" with example `[42]`. Doesn't say `[7]` vs `[07]`.
- **Remedy**: Display zero-padded `[07]`; accept both `!7` and `!07` in parsing; range-check 0-99 (reject with Debug log if out of range).

**C-15. MultiChatService lambda subscriptions never unsubscribed** — R5. **1/5**.
- Constructor lambda: `child.ConnectionStateChanged += (s, e) => OnChildConnectionStateChanged(name, s, e);` — captured closure; no matching `-=` in Dispose.
- v1-era ownership model (multi owns children) makes this benign today, but spec-cleanliness wants explicit dispose.
- **Remedy**: Store lambda references; unsubscribe in Dispose; document the ownership assumption.

**C-16. `_consecutive429Count` reset wording loose** — R5. **1/5**.
- v3: "reset to 60s on next non-2xx response." That's wrong — should reset on the next **non-429** outcome (a 500 isn't a rate-limit so the backoff should reset).
- **Remedy**: "reset on any non-429 outcome."

**C-17. C3 scope honestly understated** — R2 #1. **1/5 but high signal**.
- v3 says "~40 LOC in `Ti/Voting/`." R2's enumeration: `VoteSession` ctor + parsing + `VoteCoordinator.Start` + `_nextVoteId` field + `EnglishReceipts.FormatOpen` + `VoteSnapshot` + `VoteParsingPolicy`/regex + tests. ~7 files.
- **Remedy**: Add scope acknowledgment paragraph to Decision 11. Not a problem; just honest framing.

**C-18. `MultiChatService.SendMessageAsync` silently drops all-send-failed** — R1 #15, R4 High 10. **2/5**.
- v3 returns `Task.CompletedTask` with Debug log when zero CanSend children OR all children fail.
- **Remedy**: Distinguish "zero CanSend" (Debug; expected during YT-only) from "all-attempted-and-failed" (Warn; receipt was dropped).

**C-19. Initial-poll suppression flow ambiguity** — R1 #8. **1/5**.
- v3: "On successful `ConnectedReadOnly` transition, set `_firstPollAfterConnect = true`. The next poll runs in cursor-establishing mode."
- Ambiguity: is the cursor-establishing poll done **before** ConnectedReadOnly (during the initial page+POST sequence) or **after** (the first iteration of the steady-state loop)? Both interpretations are possible.
- **Remedy**: Pin the flow: "initial page-load + first POST happen during `Connecting`; if successful, transition to `ConnectedReadOnly`; the cursor-establishing poll's response is the one that triggered the transition — no messages emitted from it; steady-state loop emits from second POST onward."

**C-20. Health-check telemetry should redact PII from sample logging** — R1 #17. **1/5**.
- v3: log Error with "truncated sample (first 500 chars)" of failing response. A `get_live_chat` response contains display names, channel IDs, message text.
- **Remedy**: Log a structural sample (JSON keys / renderer type names) rather than raw content. Or redact known PII fields.

**C-21. `MultiChatService` constructor validation missing** — R1 #12. **1/5**.
- v3: `_children = children.ToDictionary(c => c.Name, c => c.Service);`. Throws on duplicate keys (with unclear message); doesn't validate null name/service.
- **Remedy**: Validate name non-empty + service non-null + use `StringComparer.Ordinal`. Throw with clear message on duplicate.

**C-22. `MultiChatService.GetChildState` silent-fail on typo** — R1 #11. **1/5**.
- v3: returns `Disposed` for unknown child name. Skip-gate routing through `GetChildState(Twitch)` would silently degrade if the constant string ever diverged.
- **Remedy**: Log Warn on unknown name; keep the Disposed fallback so callers don't have to check `TryGet`.

**C-23. Failure mode missing: initial-poll suppression drops 1-3% of votes** — R5. **1/5**.
- The cursor-establishing poll discards genuinely-new messages that arrived in the page-load-to-first-poll window. Acceptable trade-off but should be flagged.
- **Remedy**: Add row to failure-modes table.

**C-24. Move `ChatPlatformNames.YouTube` constant into `Ti/Chat/YouTubeChat/`** — R5 (Round-1 also). **1/5**.
- v3 mentions this as an optional 5-LOC cleanup but doesn't commit.
- R5: extraction story is cleaner if the YouTube constant lives in the YouTube namespace; deleting the folder removes the const automatically.
- **Remedy**: Commit to the move.

---

## A.3 — Outlier Points (1 reviewer, high merit)

**O-1. R4: Malformed `youtubeChannelId` should disable YT only, not whole mod.** Currently D6 says malformed control-char value → `Malformed` settings → entire mod degrades. Since `youtubeChannelId` is optional and non-secret, a softer behavior is more user-friendly: log Warn, disable YT, continue Twitch. **Lean yes** — matches the spec's "all YT failures degrade to Twitch-only" philosophy and avoids hostage-taking-the-whole-mod for a single bad optional field. Adds ~10 LOC to `ModSettings.Load` to support per-field warnings + continue.

**O-2. R3: Verify ScraperVersion log usefulness by deliberately breaking a regex during testing.** Sanity check — the log line is the primary post-redesign debug tool; confirm it actually says what we expect. **Lean yes** — implementation/test note, not a spec change.

**O-3. R5: Streamer-side note about `[42]` syntax discoverability.** YT viewers see `[42]` glyph in receipts (well, they don't — receipts only fire on Twitch) but `[42]` in in-game overlay header (after C-4 fix). Without context, this looks like a glitch. Round-1 dropped C5 (startup discoverability receipt). R5 suggests considering a one-time hint as a tooltip or in a single receipt at first-vote-of-session. **Lean no** — streamer compensates verbally; another receipt adds Twitch chat noise; matches the C5 reasoning from round 1.

**O-4. R4: Replace `MultiChatService` `LastError => null` with timestamp-based most-recent error.** Round-1 accepted "return null + per-child query" as honest. R4 wants information preserved. **Lean no** — keeping null + per-child query is honest about the aggregation problem; tracking timestamps adds state for diagnostic-only value.

**O-5. R1: 30-failure receipt wording should be generic** (not "after 30 min"). Under 429 backoff, 30 cycles may be much longer than 30 minutes. **Lean yes** — change wording to "after repeated retries" or "after extended time." Quick fix.

---

## A.4 — Category Breakdown (truncated)

Full breakdown details in v4 inline annotations. Highlights:

- **🏗️ Architecture & Design**: Keep IChatConsumer split; **emit aggregate event properly** (don't no-op); `ConfiguredPlatforms` replaces bool.
- **⚠️ Risks & Concerns**: Lambda unsubscription; cross-thread state convention; 429 typed exception; latest-wins algorithm explicit.
- **🗑️ Suggested Removals**: `InvalidOrUnavailableChannel` dead enum value; stale v2/C3 language; the no-op event implementation.
- **➕ Suggested Additions**: `ConfiguredPlatforms` list; concurrency subsection; tally-update pseudocode; vote-ID in label header; lambda unsubscription; PII redaction.
- **🔄 Alternatives**: Malformed-YT-disables-YT-only (O-1) — lean yes.
- **✅ Confirmed Good**: IChatConsumer split direction; skip-gate routing; EU consent; initial-poll suppression concept; 429 carve-out concept; vote-nonce direction; TI extraction modularity; C2 escalation concept; C9 fixture-refresh; vote-nonce preserving `#0 = skip`.
- **🔧 Implementation Details**: VoteId zero-padding; `_consecutive429Count` reset on non-429; constructor validation; GetChildState Warn-on-typo.
- **🔮 Future Considerations**: IsConnected-includes-Reconnecting (still flagged for v0.2); 3-digit VoteId (lean no for v1); explicit Twitch-only fork support (low-cost if YouTube const moves).

---

## A.5 — Conflicts & Contradictions

### Conflict 1 — Remove `ConnectionStateChanged` from `IChatConsumer` vs implement it properly

R1 (Option B) and R4 (Action 2) entertain removing the event from the parent interface. **Reality-check**: `VoteSession.cs` line 85 subscribes to it for disconnect-gap tracking. Removing from `IChatConsumer` breaks `VoteSession` compilation since `VoteCoordinator` passes `IChatConsumer.Chat` to `VoteSession`. **Verdict**: keep in interface, implement properly (cache `_lastAggregateState`, emit on aggregate change). Same conclusion R1 and R4 reach as their preferred option.

### Conflict 2 — Escalation receipt mechanism: event vs callback

R1/R4/R5 prefer event. R2 prefers callback (`Func<string, Task>?` passed at construction). **Verdict**: event — consistent with `ChildConnectionStateChanged` pattern already established in `MultiChatService`; callback would introduce a new pattern. The seam concern (no static facade) is preserved either way.

### Conflict 3 — Aggregate terminal-state priority ordering

v3: `Disposed > AuthenticationFailed > JoinFailed > Disconnected`. R4 questions: `AuthenticationFailed > JoinFailed > Disposed > Disconnected` might be more actionable since AuthFailed is more user-actionable than Disposed. **Verdict**: R4's ranking is arguably better, but since skip-gate routes through Twitch-specific `GetChildState` (not aggregate) post-C-2 fix, aggregate terminal ranking is diagnostic-only. Defer to R4's ordering with `AuthenticationFailed` first; cheap to change.

### Conflict 4 — C4 telemetry shape: dictionary vs single tracker

v3: dictionary keyed by failure location. R2: single `(lastLocation, count)` tuple. R5/R1 don't take a side. **Verdict**: simplify per R2 — fewer than 5 distinct failure points in the scraper means a dictionary is over-allocation.

### Conflict 5 — Malformed `youtubeChannelId` whole-mod vs YT-only degradation

v3 (D6) makes malformed-YT-channel-id a `Malformed` result → whole mod degrades. R4 (Critical 10, Should-do 10): soften to disable YT only, continue Twitch. **Verdict**: apply R4's recommendation. Matches the "all YT failures → Twitch-only" philosophy. Promote to Must-do.

---

## A.6 — Recommended Changes (prioritized)

### Must-do (auto-apply in v4)

1. **Emit aggregate `ConnectionStateChanged` properly in `MultiChatService`** (C-2 + Conflict 1). Cache `_lastAggregateState`; raise on change. Remove the no-op explicit-impl. **Verified bug** — `VoteSession` depends on this event.
2. **Pin escalation receipt as event-only** (C-1). Drop the static-facade alternative. Add `YouTubeEscalationRequestedEventArgs` record.
3. **`VoteTallyLabel` renders configured platforms with zero rows** (C-3). Use `ConfiguredPlatforms` list, not observed-keys filter.
4. **Add vote ID to `VoteTallyLabel` header** (C-4). `Chat voting [07] — 30s left`.
5. **`YouTubeChatStatusReason` mechanically wired** (C-5). `TransitionTo(state, reason, statusReason = None)` signature; all call sites pass explicit reason; reset to `None` on `ConnectedReadOnly`.
6. **Typed 429 exception** (C-6). Internal `YouTubeHttpStatusException` with `StatusCode` + `RetryAfter`. Replace `IsHttp429(HttpRequestException)` shim.
7. **Replace `IsMultiPlatformConfigured` bool with `IReadOnlyList<string> ConfiguredPlatforms`** (C-7). Through `VoteCoordinator` → `VoteSession`. Explicit parameter; no type-inspection.
8. **Add latest-wins per-platform tally update pseudocode** (C-11). Explicit decrement-old + increment-new sequence using platform from `PlatformOf(msg)`.
9. **Remove `InvalidOrUnavailableChannel` enum value** (C-9).
10. **Rewrite stale Noita section + Optional Enhancements detail entries** (C-8). C3 is applied; the v2 lean-no language must go.
11. **Malformed `youtubeChannelId` disables YT only, not whole mod** (Conflict 5 / O-1). D6 updates: post-trim non-empty with control chars → log Warn + clamp to null (YT disabled) + add warning to settings result.

### Should-do (auto-apply in v4)

12. **Simplify C4 telemetry to single failure-location tracker** (C-10).
13. **30-failure receipt wording: generic "after repeated retries"** (O-5).
14. **`MultiChatService.SendMessageAsync` Warn on all-failed**, Debug on zero-CanSend (C-18).
15. **`MultiChatService` constructor validation** (C-21).
16. **`MultiChatService` lambda unsubscription on Dispose** (C-15).
17. **Health-check telemetry redacts PII** (C-20).
18. **Move `ChatPlatformNames.YouTube` to `Ti/Chat/YouTubeChat/`** (C-24).
19. **Spec ConnectAsync safety contract** — never throws for external failures (C-13).
20. **`GetChildState` Warns on unknown name** (C-22).
21. **Vote-nonce formatting: `[07]` zero-padded; accept `!7` and `!07`; range-check 0-99** (C-14).
22. **Add concurrency/Interlocked subsection to `YouTubeChatService`** (C-12).
23. **Pin initial-poll-suppression flow explicitly** (C-19).
24. **`_consecutive429Count` resets on any non-429 outcome** (C-16).
25. **Add C3-scope-honesty paragraph to Decision 11** (C-17).
26. **Add failure mode: initial-poll suppression drops 1-3% of votes** (C-23).
27. **In-game label hints at `!NN` syntax**: `Chat voting [07] — 30s left, type #N (or #N!07)` (C-4 extension).
28. **Aggregate terminal priority: `AuthenticationFailed > JoinFailed > Disposed > Disconnected`** (Conflict 3).

### Consider (pick list)

CC1. **3-digit VoteId** (`[042]`) — ~8.3 hr cycle vs 50 min for 2-digit. R4. **Lean no** — 50 min is enough.
CC2. **Vote-nonce explainer one-time receipt** at first vote: `Tip: append !07 to vote in vote 07 specifically (helps stream-delayed viewers).` R5. **Lean neutral** — adds Twitch noise; matches reasoning for dropping round-1 C5.
CC3. **`MultiChatService.LastError` timestamp-based most-recent** instead of null. R4. **Lean no** — null + per-child is honest.

### Reject

- **R1/R4 "remove ConnectionStateChanged from IChatConsumer"** — would break `VoteSession.cs`. The proper-impl path is the right resolution.
- **R4's "AuthenticationFailed should be terminal-priority above Disposed"** taken as a recommendation rather than blocker; apply as Should-do #28.

---

## A.7 — What Stays

- **`IChatConsumer` / `IChatService` split** — R1, R3, R4, R5 all endorse direction.
- **Skip-gate routing through `GetChildState(Twitch)`** — endorsed by R3, R4, R5.
- **EU consent + CONSENT cookie** — endorsed by all.
- **Initial-poll backlog suppression concept** — endorsed by all.
- **HTTP 429 carve-out concept** — endorsed by all (mechanism needs C-6 fix).
- **Vote-nonce direction (`!NN` syntax)** — endorsed by all; "preserves Skip Gang" framing applauded by R3, R5.
- **TI extraction modularity section** — endorsed by R3, R4, R5.
- **30-failure escalation receipt concept** — endorsed by all (wording needs O-5 fix; mechanism needs C-1 fix).
- **C9 fixture-refresh task** — endorsed.
- **C4 scraper telemetry concept** — endorsed (shape needs C-10 fix).
- **Side-dict per-platform tally (not class extraction)** — R4 endorses for v1.
- **Drop-message on missing `authorChannelId`** — R4 endorses over the v1 random-GUID fallback.
- **Receipts merged on close (D10)** — unchanged consensus.
- **`#0 = skip` "Skip Gang" preservation** — R3 celebrates explicitly.

---

**Part B — Updated Plan**: see [`2026-05-12-youtube-chat-integration-design-v4.md`](./2026-05-12-youtube-chat-integration-design-v4.md). 11 Must-do + 17 Should-do changes auto-applied with inline `<!-- CHANGED v4: ... -->` annotations. Three Consider items presented as pick list.
