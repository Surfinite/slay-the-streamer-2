# Meta-Review — YouTube Chat Parallel Integration (design v1)

**Date**: 2026-05-12
**Subject spec**: [`2026-05-12-youtube-chat-integration-design-v1.md`](./2026-05-12-youtube-chat-integration-design-v1.md)
**Output spec**: [`2026-05-12-youtube-chat-integration-design-v2.md`](./2026-05-12-youtube-chat-integration-design-v2.md)
**Reviewers**: 6 (T3 chat, varying models / partial usage-limit improv). Some read the original draft (D6-absent framing); some read the corrected v1. One had access to a GitHub link via accidentally-continued conversation. Treating all six as valuable; flagging where staleness affects credibility.

---

## A.1 — Review Summary Table

| Reviewer | Sentiment | Key focus areas | Unique insight |
|---|---|---|---|
| R1 | **Strongly critical** (5500 words, 18 numbered concerns) | Contract correctness; backlog/dedup; aggregate-state event timing | **Cursor-establishing initial poll** to prevent stale `#N` replay |
| R2 | **Mixed-positive** (3200 words, S1–S10) | Aggregate-state mismatch; scraper hygiene; ConnectAsync smell | **EU consent redirect** (`consent.youtube.com`); HttpClient lifecycle |
| R3 | **Mostly positive** (3800 words) | LSP/ConnectAsync; YT viewer UX; 429 backoff | **YouTube vote-echo on tally label** as cheap UX mitigation; scraper version fingerprint |
| R4 | **Mixed-brief** (~750 words; T3-limited) | LSP; typo loop; latency bias | 3–5-consecutive-404 → JoinFailed heuristic *(rejected on reasoning; see A.5)* |
| R5 | **Mixed** (3900 words, 15 concerns) | Contract smells; lifecycle; hardcoded values | **Extract `clientVersion` from page** (anti-bit-rot); drop `yt:anon-{guid}` fallback; partial-failure semantics for SendMessageAsync |
| R6 | **Mixed-to-critical** (3600 words, P1/P2/P3) | Skip-gate routing; Noita amplification; debounce | **Aggregate priority fall-through for mixed terminal states**; 30-failure escalation receipt (D7 UX without heuristics); vote-nonce for Noita |

---

## A.2 — Consensus Points (ranked by agreement)

### 6/6 — Unanimous

**C-1. `MultiChatService.ConnectAsync` throwing is wrong.** R1 (H9), R2 (S3), R3 (#1), R4 (#1), R5 (#3), R6 (P2#4) — every reviewer flags this.
- Split on remedy: R3 prefers no-op-return-completed-task; R2/R5/R6 push for interface split (`IChatConsumer` / `IChatService`).
- **My read**: interface split is correct. The spec's Goal 6 ("de-risk lift `Ti/*` into a base mod") makes this exactly the wrong moment to ship an `IChatService` that lies about its contract. Cost is ~30 LOC and one new file. The split is *additive* — `IChatService` keeps its current surface, we just give it a parent type. Doesn't violate the "interface unchanged" hard constraint.

### 5/6

**C-2. HTTP 429 carve-out for exponential backoff.** R1 (H8), R3 (#3), R4 (alt), R5 (#7), R6 (P2#2).
- Hammering YouTube every 60s after a 429 risks soft IP block. Carve-out is ~5–10 LOC inside `ArmReconnect()` and doesn't complicate the state machine.
- Honor `Retry-After` header if present; else multiply cadence by 2 up to 600s cap; reset on next 2xx.

### 4/6

**C-3. `ShouldEnforceSkipGate()` routing through aggregator is broken.** R1 (C1), R2 (S1), R6 (P1#1) explicit + R5 (#1 indirect via assumption #7).
- **Confirmed against code**: B.2.1's amendment reads `Voter.Default.Chat.State`. `Voter.Default.Chat` returns the `IChatService` passed to `VoteCoordinator`. After this spec, that's `MultiChatService` whose `State` is best-of-children. Three masking scenarios from R2/R6:
  - Twitch `AuthenticationFailed` + YT `ConnectedReadOnly` → aggregate `ConnectedReadOnly` → gate **enforced** (wrong; D21 says degrade).
  - Twitch `AuthenticationFailed` + YT `Reconnecting` → aggregate `Reconnecting` → gate **enforced** (wrong).
  - Twitch `JoinFailed` + YT `ConnectedReadOnly` → aggregate `ConnectedReadOnly` → gate **enforced** (wrong).
- This is a real D21 regression that operator-validation Step 1 would catch but probably AFTER implementation is done.
- Remedy: `MultiChatService.GetChildState(string name)` accessor; route the Twitch-state-check through it; B.2.1's amendment text updates to specify Twitch-specific routing.

**C-4. Receipt flap-suppression 30s debounce is wrong or under-specified.** R1 (M18), R3 (Change 5), R5 (#13), R6 (P3#2).
- R6's framing is sharpest: with a 60s reconnect cadence, you can't flap faster than 60s — a 30s window is **unreachable in normal operation**.
- Remedy: change to 120s (= 2× cadence) OR drop entirely and rely on "real transitions only."

**C-5. Per-frame allocation in `VoteTallyLabel`** (the new `Dictionary<(string, int), int>` copy). R1 (M13), R3 (#8.4), R5 (#199), R6 (#198).
- Each `_Process` frame allocates a fresh dictionary copy via `new Dictionary<...>(_talliesByPlatform)`. At 60fps during a 30s vote, that's 1800 transient dict allocations.
- Remedy: return `IReadOnlyDictionary<>` wrapper (not copy), OR cache rendered text in the label and invalidate only on `TallyChanged`.
- Note: existing single-platform path has the same problem; the spec inherits it. Worth fixing for both.

**C-6. "Reconnecting" receipt wording is wrong for never-connected case.** R1 (H7), R2 (§8), R5 (#11), R6 (#11).
- "YouTube disconnected: live broadcast ended, will resume when next broadcast starts" is misleading for network failures, scraper regressions, and especially the never-connected-yet case (when the streamer hasn't started YT broadcasting, there's nothing to *re*-connect).
- Remedy: generic wording `"YouTube disconnected; will retry every ~60s"` OR reason-specific wording (which requires R1/R2's status-reason concept — see C-9).

### 3/6

**C-7. HTTP client lifecycle unspecified.** R2 (S2 — most thorough), R5 (#7), R6 (#10).
- R2 most thorough: single shared `HttpClient` per service with `HttpClientHandler { AllowAutoRedirect = true, CookieContainer = ... }`, set realistic `User-Agent`, document timeout, disposed in `Dispose()`.
- R2 also adds **EU consent redirect** handling: pre-set `CONSENT=YES+cb` cookie or detect/follow `consent.youtube.com` redirect. **This is critical for non-US streamers** and would otherwise cause silent failure.

**C-8. `TalliesByPlatform` gating on observation is wrong.** R1 (C3), R2 (S4 partial), R5 (#2).
- R5's framing is sharpest: mid-vote rendering snap when first YT message arrives.
- R1's framing: contradicts acceptance Step 1 (YT-only smoke says "YouTube line renders" but `_observedPlatforms.Count > 1` would render single-line).
- Remedy: gate on **configuration** (YT enabled in settings AND child not in `Disposed`), not on observation.

**C-9. `MultiChatService.LastError` aggregation is wrong.** R1 (minor), R5 (#8), R6 (P3#3).
- `_children.Select(c => c.LastError).FirstOrDefault(x => x is not null)` returns first-by-iteration-order. Loses information; non-deterministic.
- Remedy: return null, document "consumers query per-child" OR pair with timestamps and return most-recent.

**C-10. Two-event API on `MultiChatService` is unused.** R2 (S5), R5 (#9), R6 (P3#1).
- R6's sharpest argument: **nothing in the spec actually subscribes to the aggregate `ConnectionStateChanged`**. ModEntry subscribes to `ChildConnectionStateChanged`. VoteCoordinator doesn't subscribe. Why does the aggregate event exist?
- Remedy: drop the aggregate event. Keep only `ChildConnectionStateChanged`. `IChatService.ConnectionStateChanged` is implemented as never-fires (or fires only on `Disposed` transitions if some consumer needs it). The interface compliance is preserved; the unused-but-misleading event is gone.

**C-11. "1–2 LOC fix" maintenance claim is optimistic.** R1 (M11), R3 (#4), R5 (acknowledged).
- R3's quantification: regex update is 1–2 LOC; JSON path restructuring is 5–15 LOC.
- Remedy: soften to "1–15 LOC depending on breakage type; expect quarterly fixture refreshes."

### 2/6

**C-12. State-transition logging on `YouTubeChatService` from day one.** R2 (additions), R5 (additions).
- B.1 `TransitionTo` is silent (notes/06); operator-validation Step 7 of B.2.1 suffered for it.
- Remedy: Info-level log on every transition: `[YouTubeChatService] Connecting → ConnectedReadOnly (initial poll OK, tracking videoId=XYZ)`.

**C-13. Child-name magic strings → constants.** R1 (M16), R5 (#10).
- `"twitch"` and `"youtube"` scattered across `VoteSession`, `MultiChatService`, `ModEntry`.
- Remedy: `internal static class ChatPlatformNames { const string Twitch = "twitch"; const string YouTube = "youtube"; }`.

**C-14. Dispose-ordering robustness.** R2 (S9), R1 (H6 implied).
- `foreach (var c in _children) c.Dispose()` — if child[0] throws, child[1] never disposed.
- Remedy: wrap each in try/catch + log.

**C-15. Noita pattern materially worsened.** R3 (#2), R6 (P2#3).
- R6's quantification: Twitch latency ~0.5–2s; YT latency ~7–20s (broadcast + chat lag combined). Vote-N-vs-N+1 collision goes from rare edge case to every-vote-or-two.
- R3's mitigation: vote-echo on tally label (gives streamer visual feedback without solving root cause).
- R6's mitigation: vote-nonce in commands (`#1!42` syntax), ~30 LOC in vote-parsing.
- **My read**: R3's vote-echo addresses the symptom (YT viewers can't tell if their vote landed); R6's vote-nonce addresses the cause. **Both warrant inclusion** — vote-echo as Should-do, vote-nonce in Consider (genuine scope creep into `Ti/Voting/`).

**C-16. Aggregate priority fall-through for mixed-terminal states.** R6 (P1#2) explicit; R3 (#8.2) brushes against it.
- Priorities 5–8 in the priority table say "matched by all children." If two children are in *different* terminal states (e.g., Twitch `JoinFailed`, YT (hypothetically) `AuthenticationFailed`), no priority matches.
- **Confirmed gap.** Remedy: explicit rule — `(Disposed > AuthenticationFailed > JoinFailed > Disconnected)` ordering; return worst per ranking when "all terminal but mismatched."

---

## A.3 — Outlier Points (1 reviewer; flagging high-merit singletons)

**O-1. EU consent redirect** (R2 only). **CRITICAL singleton.** Without `CONSENT=YES+cb` cookie or follow-through logic, EU streamers hit `consent.youtube.com/m?continue=...` on discovery; final URL never matches `/watch?v=`; mod silently fails forever. **Must-do.**

**O-2. Initial-poll backlog suppression** (R1 only). YouTube's initial `get_live_chat` response can include backlog/history. A YT viewer who typed `#1` 20s before mod connected could have their stale message counted as a vote in the current vote. **Must-do** — minimum is "do not emit messages from the cursor-establishing first poll."

**O-3. Extract `clientVersion` from page** (R5 only). Hardcoded `"2.20240101.00.00"` in the `get_live_chat` POST body will rot. The `live_chat?v=` page already exposes `INNERTUBE_CONTEXT.client.clientVersion`; extract alongside `INNERTUBE_API_KEY`. **Should-do** — anti-bit-rot, ~3 LOC.

**O-4. Drop `yt:anon-{randomGuid}` fallback** (R5 only). Current failure-mode #17 admits unlimited single-use voters if `authorChannelId` is missing. **Should-do** — replace with drop-message-and-Debug-log; matches the existing posture for other malformed renderers.

**O-5. `MultiChatService.SendMessageAsync` partial-failure semantics** (R5 only). `Task.WhenAll(child sends)` throws AggregateException if any child fails; caller sees "failure" even when Twitch chatters received the receipt. **Should-do** — continue past failures; log each; return completed if at least one succeeded.

**O-6. 30-failure escalation receipt for D7 typo case** (R6 only). After ~30 minutes of consecutive `Reconnecting`, fire one elevated-priority Twitch receipt: `"YouTube: still no live broadcast after 30 min — check that 'youtubeChannelId' is correct"`. Counter-based, not 404-shape-heuristic; preserves D7's anti-heuristic posture. **Consider** — addresses real UX gap; pulls in scope.

**O-7. YouTube vote-echo on tally label** (R3 only). When a YT vote arrives, append `◀ just now` to that platform's line for ~3s. Gives streamer visual heartbeat that YT is participating. **Should-do** — directly mitigates D2+D3+Noita compound (C-15) cheaply (~15 LOC).

**O-8. Scraper version fingerprint** (R3 only). `private const string ScraperVersion = "2026-05-12-v1"` logged at Info on first successful parse. Helps post-redesign user-vs-maintainer-version diagnosis. **Should-do** — trivial.

**O-9. Scraper telemetry on shape changes** (R2, R3 alt) — if scraper parse fails N times in a row at the same point, log Error with truncated failing-input shape. Speeds post-redesign diagnosis. **Consider**.

---

## A.4 — Category Breakdown

### 🏗️ Architecture & Design

- **`MultiChatService.ConnectAsync` throw → no-op or interface split** (C-1, 6/6). Verdict: **interface split** (`IChatConsumer` + `IChatService : IChatConsumer`). `MultiChatService : IChatConsumer`. `VoteCoordinator` takes `IChatConsumer`. Additive interface change; existing implementations unaffected. **Must-do.**
- **Aggregate event API is two-channel and unused** (C-10, 3/6). Drop aggregate `ConnectionStateChanged`; keep only `ChildConnectionStateChanged`. **Should-do.**
- **Aggregate priority fall-through** (C-16, 2/6). Explicit rule for mixed-terminal-states. **Must-do** (undefined behavior).
- **`TalliesByPlatform` gate from observed → configured** (C-8, 3/6). Pass platform-configured info into `VoteSession` at construction. **Must-do.**

### ⚠️ Risks & Concerns

- **Skip-gate routing through aggregator masks Twitch terminal state** (C-3, 4/6). `GetChildState(name)` + update B.2.1 amendment text. **Must-do.**
- **EU consent redirect** (O-1, 1/6). `CONSENT=YES+cb` cookie + detect-and-follow. **Must-do.**
- **Initial-poll backlog replays stale `#N`** (O-2, 1/6). Cursor-establishing first poll, no message emission. **Must-do.**
- **Noita pattern amplified by YT latency** (C-15, 2/6). At minimum: regression analysis subsection quantifying the worsening. Don't fold the vote-nonce fix (R6) into this spec — that's `Ti/Voting/` scope creep — but document the dependency. **Should-do.**
- **HTTP 429 → IP block risk** (C-2, 5/6). `Retry-After` honoring + exponential backoff for 429 only. **Must-do.**
- **D7 typo case is invisible to streamer** (O-6, 1/6 — R6). Counter-based escalation receipt at 30 failures. **Consider** (good idea but scope addition).
- **Mid-vote rendering snap from observation gating** (C-8 / R5 framing). Same fix as C-8. **Must-do.**
- **Paid-message contradiction** (R1 H10, 1/6 but real spec contradiction). Non-goals say "treat as normal chat"; tests say "skip." Reconcile: paid messages WITH text → extract as normal; membership items WITHOUT text → skip. **Should-do** — small fix.

### 🗑️ Suggested Removals / Simplifications

- **Drop aggregate `ConnectionStateChanged` on MultiChatService** (C-10, 3/6). Should-do.
- **Drop `yt:anon-{randomGuid}` fallback** in failure mode #17 (O-4, 1/6). Should-do.
- **Drop "30s debounce" → 120s or remove entirely** (C-4, 4/6). Should-do.
- **Remove or soften "1–2 LOC fix" claim** (C-11, 3/6). Should-do.
- **Remove "Game/ unchanged" claim** (R1 §7 only). The spec says Game/ unchanged then lists `ModSettings.cs` as ✏️. Reword to "no `Game/DecisionVotes/` patches changed." **Should-do** — accurate framing.
- **Move "YT-only deployments" from Non-goals to Supported-degraded-modes** (R1 #17). Accurate description. **Should-do** — wording.

### ➕ Suggested Additions / Features

- **EU consent + UA + HttpClient lifecycle** (C-7, 3/6 + O-1). Must-do.
- **`clientVersion` extraction** (O-3, 1/6 — R5). Should-do.
- **State-transition logging on YT service** (C-12, 2/6). Should-do.
- **YT vote-echo on tally label** (O-7, 1/6 — R3). Should-do.
- **Scraper version fingerprint** (O-8, 1/6 — R3). Should-do.
- **Scraper health-check / telemetry** (O-9, 2/6 weak). Consider.
- **Partial-failure semantics for `SendMessageAsync`** (O-5, 1/6 — R5). Should-do.
- **Cache `_videoId` across short-window failures** (R6 alt). Consider.
- **YouTube vote-command discoverability receipt** at startup (R3 #6.1, 1/6). Consider — closes feedback loop minimally.
- **30-failure D7 escalation receipt** (O-6, 1/6 — R6). Consider.
- **Vote-nonce / per-vote ID** for Noita (R6, 1/6). Consider — genuine scope creep; reject for v1 if v0.2 is acceptable timeline.
- **`ChatMessage.Text` truncation for YouTube** (R3 #6.3, 1/6). Consider.
- **Operator-validation Step: members-only chat probe** (R6 additions, 1/6). Consider.
- **Refresh-fixtures-monthly task** in notes/ (R6, 1/6). Consider.

### 🔄 Alternative Approaches

- **Interface split** (R2, R3 alt A, R5, R6) vs no-op-return-from-ConnectAsync (R3, R4 favor). **Chose split** — better for Goal 6.
- **`PerPlatformVoteTally` class extraction** (R4, R1 alt C). **Reject for v1** — rule of three; 4/6 explicitly side with side-dict.
- **`Platform` field on `ChatMessage`** (R3 alt). **Reject** — D9's `"yt:"` prefix is explicitly chosen; reviewers acknowledge.
- **Per-vote nonce for Noita** (R6). **Defer** — real fix but scope creep into `Ti/Voting/` Plan A code.
- **Grace period for YT votes after close** (R3 alt B). **Reject** — complicates close semantics.
- **Best-of-children aggregate state** (R3 affirms; R4 affirms). **Keep as-is.**

### ✅ Confirmed Good (Keep As-Is)

- **`MultiChatService` aggregator pattern** (all 6).
- **D9 `"yt:"` prefix discipline** (all 6).
- **Read-only YT (D3)** (R1, R2, R3).
- **D7 fixed cadence + jitter** (R3, R4, R6 affirm; only R4 wants the 3–5-404 heuristic which is rejected).
- **Parallel per-platform tally side-dict** (R1, R2, R3, R5, R6 — 5/6 affirm).
- **Scraper isolation in `Ti/Chat/YouTubeChat/`** (all 6).
- **Single-child `MultiChatService` as passthrough** (R2, R3, R6).
- **21-row failure-modes table** (R1, R2, R3, R6).
- **Acceptance gate Step 0 (regression via aggregator)** (R1, R2, R3, R6).
- **`timeoutMs` floor 1s / ceiling 10s** (R3).
- **`CanSend = false` hardcoded** (R3).
- **`_videoId` cleared on reconnect** (R3 — though R6 wants caching, see Consider).
- **D6 retroactive resolution with rationale preserved** (R6 explicitly approves).

### 🔧 Implementation Details & Nits

- **`MultiChatService` constructor shape pinned** (R1 M16, R5 #10). Named registrations + platform name constants. **Should-do.**
- **`MultiChatService.LastError` semantics** (C-9, 3/6). Return null or document. **Should-do.**
- **Dispose-ordering robustness** (C-14, 2/6). try/catch per child. **Should-do.**
- **Per-frame allocation in `VoteTallyLabel`** (C-5, 4/6). Cache rendered text. **Should-do** (upgraded from Consider given 4/6 mention).
- **Platform-ordering explicit constant**, not `StringComparer.Ordinal` (R5 #12, R6 nit). **Should-do** — `PlatformDisplayOrder` constant.
- **Acceptance Step 7: verify receipt delivery, not just flap suppression** (R3 Change 4, R6 #24). **Should-do.**
- **Acceptance Step 3d wording: "lost connection, will retry" not "live broadcast ended"** (R6 #11). **Should-do.**
- **Login for YT uses display name, not channel ID** (R6 nit). Improves log forensics. **Should-do.**
- **`runs[]` defensive iteration: skip non-text runs** (R2 §10). **Should-do.**

### 📦 Dependencies & Integration

- **HttpClient lifecycle + UA + cookies** — see C-7. Must-do bundle.
- **`ITimerScheduler` injection clarity for `_retryTimer`** (R3 §10 assumption #3). Document the abstraction.
- **`IYouTubeHttp` interface shape pinned** (R5 — currently silent). Specify in spec.

### 🔮 Future Considerations

- Sealing `IsConnected`-semantics across services (Twitch + YT have same `Reconnecting`-inclusive definition; debated; v0.2 polish, **NOT** a v1 change — see A.5).
- Per-vote nonce / Noita root-cause fix (R6) — defer to v0.2 unless Noita-regression-analysis (C-15) shows shipping without it is untenable.
- Members-only chat support (R6) — D5 non-goal; preserve.
- Cross-platform display-name dedup heuristic (R6 §additions) — D1 explicitly defers; preserve.

---

## A.5 — Conflicts & Contradictions

### Conflict 1 — D7 typo-case remedy: R4 (heuristic) vs R6 (counter)

- **R4**: "After 3–5 consecutive 404s on `/channel/{ID}/live`, transition to `JoinFailed`." This is a 404-shape heuristic — exactly what D7 disclaims.
- **R6**: "Counter-based: after N=30 consecutive `Reconnecting` cycles, fire one-shot elevated receipt; state stays `Reconnecting`." Preserves D7's posture.
- **Verdict**: R6 wins. D7's entire point is avoiding fragile 404-shape disambiguation; R4's remedy reintroduces it. R6's counter-based escalation is the right shape — it acknowledges the UX gap without claiming to know "permanent vs transient." **Move R6's escalation into Consider (C4 in pick list)**.

### Conflict 2 — `IsConnected` includes `Reconnecting`: R1/R5 (bug) vs codebase reality

- **R1 (C2) + R5 (#1)**: "Semantically wrong; `Reconnecting` means not currently reading."
- **R5 explicitly flags as assumption #2**: "I assume `TwitchIrcChatService.IsConnected` returns false during Reconnecting."
- **Reality (verified from code)**: `TwitchIrcChatService.IsConnected` includes `Reconnecting`. The YT spec **matches** the existing convention. R5's assumption is wrong; R1 isn't conditional but draws the same conclusion.
- **Verdict**: **Reject as a v1 fix.** Changing YT alone creates inconsistency. The semantic concern is real and worth flagging for v0.2 polish (affects both Twitch and YT). Document explicitly in v2 and leave consistent with Twitch.

### Conflict 3 — `PerPlatformVoteTally` extraction: R4 + R1 alt C (extract) vs R3, R5, R6 (side-dict)

- **R4**: Extract `PlatformTallyTracker` class.
- **R1**: "If `VoteSession` is already large, a small helper could work — must not own voter identity or latest-wins policy."
- **R3, R5, R6**: Explicit "side-dict is correct for v1; rule of three; extract when third platform arrives."
- **Verdict**: Side-dict (4/6 explicit). Defer extraction.

### Conflict 4 — D6 `char.IsControl` validation: R6 (over-engineered) vs others (silent agreement)

- **R6 §"Removals"**: "`char.IsControl` triggers on TAB/LF/CR — realistic paste pollution. Trim first, only flag if non-empty post-trim contains control chars."
- **Verdict**: R6 right. Update D6 validation to: trim leading/trailing whitespace; if remaining is empty → clamp to null (Success); if remaining contains any control char or is only whitespace → `Malformed`. **Should-do** — refines D6's `Malformed` rule.

### Conflict 5 — Vote-nonce / Noita fold-in: R6 (fold in) vs scope discipline (defer)

- **R6 (P2#3)**: "Fold a minimum fix into this spec. Cheapest option is a per-vote nonce." Quantifies as ~30 LOC in vote-parsing.
- **R3 (#10)**: "Keep deferred. The Noita pattern is a pre-existing issue this spec exacerbates but doesn't cause."
- **My read**: R6's quantification (every-vote-or-two collision under YT) is sobering but R3's scope argument holds. The user's discipline on v0.1/v0.2 boundaries (per `notes/06`) is consistent. **Compromise**: include the *regression analysis* (Should-do); offer the *nonce fix* as Consider with a "lean: no" recommendation; let user override.

---

## A.6 — Recommended Plan Changes

### Must-do (10 items)

1. **Skip-gate routing through aggregator** (C-3). Add `MultiChatService.GetChildState(string name)`. Update B.2.1 amendment text in this spec to specify Twitch-specific routing. Add three masking-scenario tests.
2. **Interface split: `IChatConsumer` / `IChatService`** (C-1). `MultiChatService : IChatConsumer`. `VoteCoordinator` takes `IChatConsumer`. Additive change to Plan A.
3. **Aggregate priority fall-through for mixed-terminal states** (C-16). Explicit ranking; document.
4. **`TalliesByPlatform` gating: observed → configured** (C-8). Pass platform-configured info to `VoteSession`.
5. **EU consent redirect + HttpClient lifecycle** (O-1, C-7). Pre-set `CONSENT=YES+cb` cookie; single shared `HttpClient` with `CookieContainer`; realistic UA; documented timeout.
6. **Initial-poll backlog suppression** (O-2). Cursor-establishing first poll emits no messages.
7. **HTTP 429 carve-out** (C-2). Honor `Retry-After`; exponential backoff (60→120→240→…→600s cap); reset on 2xx.
8. **`Reconnecting` receipt wording fix** (C-6). Generic "YouTube disconnected; will retry every ~60s."
9. **Paid-message handling fix** (R1 H10). Text-bearing `liveChatPaidMessageRenderer` extracted as normal chat; text-less items skipped. Update test descriptions accordingly.
10. **D6 validation refinement** (Conflict 4 / R6). Trim first; clamp empty/whitespace post-trim to null OR `Malformed` based on whether original was whitespace.

### Should-do (16 items)

11. **Drop aggregate `ConnectionStateChanged` on MultiChatService** (C-10). Keep only `ChildConnectionStateChanged`.
12. **Drop `yt:anon-{randomGuid}` fallback** in failure mode #17 (O-4). Skip message + Debug log.
13. **Extract `clientVersion` from page** (O-3). Anti-bit-rot.
14. **`SendMessageAsync` partial-failure semantics** (O-5). Continue + aggregate-success.
15. **Receipt flap-suppression debounce: 30s → 120s** (C-4). 2× reconnect cadence.
16. **Per-frame allocation fix in `VoteTallyLabel`** (C-5). Cache rendered text; invalidate on `TallyChanged`.
17. **State-transition logging on `YouTubeChatService`** (C-12). Info-level on every transition.
18. **YT vote-echo on tally label** (O-7). `◀ just now` marker for ~3s after each YT vote.
19. **Scraper version fingerprint** (O-8). Logged at first success.
20. **Pin `MultiChatService` constructor** + platform-name constants (C-13). Replace magic strings.
21. **`MultiChatService.LastError` returns null + per-child query documented** (C-9).
22. **Dispose-ordering try/catch per child** (C-14).
23. **Platform-ordering explicit constant** (R5 #12, R6 nit). `PlatformDisplayOrder` static array.
24. **Soften "1–2 LOC fix" claim** (C-11). Replace with realistic range.
25. **Move "YT-only deployments" from Non-goals → Supported-degraded-modes** (R1 #17).
26. **Noita-pattern regression analysis subsection** in Failure modes & degradation (C-15). Quantify; flag the open question for ship/defer.
27. **Acceptance Step 7: verify receipt delivery + Step 3d wording fix** (R3, R6).
28. **Defensive `runs[]` iteration** (R2 §10). Skip non-text runs.
29. **Login uses display name for YT, not channel ID** (R6 nit). Improves log forensics.
30. **Specify `IYouTubeHttp` interface shape** (R5 implicit).

### Consider (offered as pick list — see Part B's Optional Enhancements)

C1. `YouTubeChatStatusReason` enum (R1 H7, R2 §6.B) — improves receipt accuracy.
C2. 30-failure D7 escalation receipt (O-6 / R6).
C3. Vote-nonce / per-vote ID for Noita (R6 — scope creep into Ti/Voting/).
C4. Scraper health-check / telemetry hook (R2, R3 alt).
C5. YouTube vote-command discoverability receipt at startup (R3 #6.1).
C6. `ChatMessage.Text` truncation to 500 chars for YouTube (R3 #6.3).
C7. Cache `_videoId` across short-window failures (R6 alt).
C8. Operator-validation Step: members-only chat probe (R6 additions).
C9. `notes/`-tracked refresh-fixtures-monthly task (R6).
C10. Display-name index for D1 future heuristic (R6).

### Reject (with reason)

R-1. **R4's 3–5-consecutive-404 → JoinFailed heuristic**. Reason: reintroduces the 404-shape disambiguation that D7 explicitly rejects as fragile. R6's counter-based escalation (C2 in pick list) is the right remedy.
R-2. **R1's `Reconnecting` removal from `IsConnected`**. Reason: codebase reality — `TwitchIrcChatService.IsConnected` already includes `Reconnecting`. Changing YT alone creates inconsistency. Document existing convention; flag for v0.2 polish across both services. (Same for R5 #1.)
R-3. **R4's `PerPlatformVoteTally` class extraction**. Reason: 4/6 reviewers explicitly side with side-dict; rule of three applies.
R-4. **R3's grace period for YT post-close**. Reason: complicates close semantics; rejected by spec design and R3 acknowledges.
R-5. **R3's `Platform` enum field on `ChatMessage`**. Reason: D9's `"yt:"` prefix is the explicit design choice; reviewers acknowledge it's load-bearing for forward compat.
R-6. **Folding the vote-nonce / Noita fix into this spec**. Reason: scope creep into `Ti/Voting/` Plan A code; v0.1/v0.2 boundary discipline preserved. Surface as Consider item C3 instead.

---

## A.7 — What Stays (explicitly confirmed)

These are the spec's strong points; reviewers across the board endorse them:

- **`MultiChatService` aggregator pattern** — the architectural spine. All 6.
- **D9's `"yt:"` prefix discipline** — load-bearing, forward-compat-friendly, zero-schema-change. All 6.
- **D3 read-only YouTube** — pragmatic constraint, correctly motivated. R1, R2, R3.
- **D7 fixed-cadence-no-permanent-vs-transient** — right call (with 429 carve-out per C-2 and counter-escalation per C2 pick list). R3, R6 affirm.
- **Parallel per-platform tally side-dict (not replacement)** — preserves B.1 invariants. R1, R2, R3, R5, R6.
- **Scraper isolation in `Ti/Chat/YouTubeChat/`** — fragility containment. All 6.
- **Single-child `MultiChatService` as passthrough** — one code path. R2, R3, R6.
- **21-row failure-modes table** — discipline that catches operator-validation bugs early. R1, R2, R3, R6.
- **Acceptance Step 0 (regression via aggregator)** — catches the most likely regression. All 6 endorse explicitly or implicitly.
- **`timeoutMs` floor/ceiling defensive defaults** — R3.
- **`CanSend = false` hardcoded** — honest, simple, unbypassable. R3.
- **`PlatformOf` helper static method in `VoteSession`** — keeps discrimination boundary tight. R1, R2.
- **D6 retroactive resolution with rationale preserved in history** — R6 explicitly approves.
- **D10 split rendering, not visual-combining** — deferral of visual-combining is correctly scoped. No reviewer pushes back.

---

**Part B — Updated Plan**: see [`2026-05-12-youtube-chat-integration-design-v2.md`](./2026-05-12-youtube-chat-integration-design-v2.md). 10 Must-do + 16 Should-do changes auto-applied with inline `<!-- CHANGED: ... -->` annotations. Consider-tier items presented as a pick list at the end of v2.
