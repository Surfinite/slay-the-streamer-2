# YouTube chat parallel integration (design v2)

**Date**: 2026-05-12
**Status**: Draft v2 — post-6-reviewer meta-review on v1. 10 Must-do + 16 Should-do items applied. 10 Consider-tier items at end as pick list. See [`META-REVIEW-2026-05-12-youtube-chat-integration-design.md`](./META-REVIEW-2026-05-12-youtube-chat-integration-design.md) for the consensus / outlier / reality-check analysis behind these changes.
**Predecessor**: [`2026-05-12-youtube-chat-integration-design-v1.md`](./2026-05-12-youtube-chat-integration-design-v1.md).
**Scope**: Unchanged from v1 — multi-platform chat slice adding YouTube alongside Twitch.

> **Architectural hard constraint** (carried forward from B.1/B.2.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. This spec adds no Harmony patches; the constraint is unaffected.

## Author's note on v2 changes

The six-reviewer meta-review surfaced two real correctness bugs the spec was unaware of, one critical singleton (EU consent), and a small pile of contract-shape improvements. Headline shifts:

1. **`IChatService` interface split** (Must-do #2). `IChatConsumer` (read/send/state) becomes the parent type; `IChatService : IChatConsumer` adds `ConnectAsync`. `MultiChatService : IChatConsumer`. `VoteCoordinator` takes `IChatConsumer`. Removes the `ConnectAsync`-throws LSP smell that all 6 reviewers flagged. Additive; existing `: IChatService` implementations unchanged.
2. **`ShouldEnforceSkipGate()` routing** (Must-do #1). B.2.1's amendment reads `Voter.Default.Chat.State`, which after this spec is the aggregate. R1/R2/R6 caught that "best-of-children" aggregate masks Twitch's terminal state — the gate would wrongly enforce when receipts can't fire. Fix: `MultiChatService.GetChildState(string name)`; B.2.1 amendment text in this spec specifies Twitch-specific routing.
3. **EU consent redirect** (Must-do #5; R2 singleton). `youtube.com/channel/{ID}/live` from cookieless EU IPs redirects to `consent.youtube.com/m?continue=...`. Without handling, EU streamers' YT integration silently fails forever. Fix: pre-set `CONSENT=YES+cb` cookie via shared `CookieContainer`.
4. **`TalliesByPlatform` gates on configuration, not observation** (Must-do #4). The v1 `_observedPlatforms.Count > 1` rule produced a mid-vote rendering snap (single → split lines) when the first YT message arrived, and conflicted with acceptance Step 1. Fix: pass `IsMultiPlatformConfigured` to `VoteSession` at construction; UI gates on that.
5. **Initial-poll backlog suppression** (Must-do #6; R1 singleton). YouTube's first `get_live_chat` response can include backlog — stale `#1` from 30s ago could land in the current vote. Fix: cursor-establishing first poll emits no messages.
6. **HTTP 429 carve-out** (Must-do #7; 5/6 consensus). Honor `Retry-After`; exponential backoff 60→120→240→600s cap for 429 only; reset on next 2xx. Preserves D7's "one cadence" for the modal case; carves out the one failure mode where exponential helps.
7. **Aggregate priority fall-through for mixed terminal states** (Must-do #3). Undefined behavior in v1's priority table. Fix: explicit ranking `Disposed > AuthenticationFailed > JoinFailed > Disconnected` for mixed-terminal-children.
8. **`Reconnecting` receipt wording** (Must-do #8). v1's "live broadcast ended" is wrong for network failures and the never-connected-yet case. Fix: generic "YouTube disconnected; will retry every ~60s."
9. **Paid-message handling** (Must-do #9). v1 non-goal says "treat as normal chat"; v1 tests said "skip." Reconciled: text-bearing `liveChatPaidMessageRenderer` extracted as normal chat; text-less items (membership/sticker) skipped.
10. **D6 validation refinement** (Must-do #10). v1 used `char.IsControl` which flags TAB/LF/CR (realistic paste pollution). Fix: trim first; clamp empty/whitespace-only-trimmed to null (`Success`); flag `Malformed` only if post-trim non-empty contains control chars.

Other notable refinements (Should-do): drop aggregate `ConnectionStateChanged` (nothing uses it); drop random-GUID fallback for missing `authorChannelId` (skip + Debug log); extract `clientVersion` from page (anti-bit-rot); 30s debounce → 120s; cache `VoteTallyLabel` rendered text; vote-echo marker for YT votes; state-transition logging from day one; pinned constructor + platform-name constants.

**Explicitly rejected** (with reasoning in META-REVIEW §A.5):
- R4's 3–5-consecutive-404 → `JoinFailed` heuristic — re-introduces the 404-shape disambiguation D7 disclaims.
- R1/R5's `Reconnecting` removal from `IsConnected` — codebase reality (Twitch already includes it); v0.2 polish, not v1 break-of-consistency.
- R4's `PerPlatformVoteTally` class extraction — 4/6 reviewers explicitly side with side-dict.
- Folding R6's vote-nonce / Noita fix into this spec — surfaced as Consider C3 instead, plus a Should-do regression-analysis subsection.

## Goals

Unchanged from v1. Read YT live chat in parallel with Twitch; architectural fit invisible to `VoteCoordinator` and patches; YT read-only; in-game label split per platform when configured; fail soft; de-risk future TI extraction.

## Non-goals

Unchanged from v1 *except*: <!-- CHANGED: clarifications per Reviewers R1 #17 and R1 H10 -->

- **YouTube-only deployments without Twitch.** Moved from a v1 hard non-goal to a **Supported degraded mode** (see new section below) — acceptance Step 1 requires this to work; calling it a non-goal was misleading. <!-- CHANGED: R1 #17 -->
- **Super Chat / Super Sticker handling.** Text-bearing paid messages are treated as normal chat (vote commands like `#1` in a Super Chat ARE counted). Text-less items (membership joins, sticker-only paid items) are skipped. <!-- CHANGED: R1 H10 — reconciled v1 contradiction between non-goal text and test description -->

All other v1 non-goals preserved (cross-platform dedup, per-platform windows, members-only, manual video ID, visual-combining, latency compensation, etc.).

## Supported degraded modes <!-- NEW section per R1 #17 -->

- **YouTube-only mode** (Twitch terminal failure + YT configured + live). Votes flow into `VoteSession` via the YT child; in-game tally label renders YouTube row only; **no chat receipts fire on either platform** (Twitch is dead per D3 + read-only YT); card-skip gate degrades to vanilla per B.2.1 Decision 21 amendment. Acceptance Step 1 verifies this mode.
- **Twitch-only-deployment mode** (no `youtubeChannelId` in settings, or null/empty). `MultiChatService` constructed with only Twitch child; functionally identical to bare `TwitchIrcChatService` from `VoteCoordinator`'s perspective. Acceptance Step 0.
- **Twitch-alive-YouTube-disconnected mode** (steady state when YT broadcast not active or transient YT failure). Votes flow Twitch-only; receipts fire on Twitch; in-game tally label renders Twitch row only (gated by `IsMultiPlatformConfigured` — if YT is configured but disconnected, the label still shows the YouTube row with zero counts so chatters can see why their YT votes aren't appearing). <!-- CHANGED: per Must-do #4 — configured-not-observed gating -->

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **Cross-platform vote-counting: count twice.** | Unchanged from v1. |
| 2 | **Vote-window timing: single shared 30s window, no adjustment.** | Unchanged from v1. |
| 3 | **Outgoing-receipt policy: read-only YouTube; receipts via Twitch only.** | Unchanged from v1. |
| 4 | **Video ID discovery: channel ID + auto-discovery, with EU consent handling.** <!-- CHANGED: per Must-do #5 (R2 O-1) --> | Discovery flow now pre-sets `CONSENT=YES+cb` cookie via the shared `CookieContainer` (see HTTP client lifecycle in `YouTubeChatService` section). Without this, EU IPs hit `consent.youtube.com/m?continue=...` redirect; `FindLiveVideoIdAsync` returns null; mod silently fails. Rest of D4 unchanged. |
| 5 | **Members-only chat: not supported in v1.** | Unchanged from v1. |
| 6 | **Settings JSON: `youtubeChannelId` (optional, nullable string); validation refined.** <!-- CHANGED: per Must-do #10 (R6 Conflict 4) --> | Missing field OR JSON `null` → YT disabled (`Success`, NOT `Malformed`). Non-empty trimmed → preserved. **Trim first**: empty-after-trim → clamp to null (`Success`, no warning). Post-trim non-empty containing control characters → `Malformed`. (v1's `char.IsControl` rule flagged TAB/LF/CR within whitespace runs, which are realistic paste pollution; trim-first fixes this.) Escape-hatch `youtubeVideoIdOverride` still rejected. |
| 7 | **YouTube failure mode: silent degradation + ~60s retry + HTTP 429 carve-out.** <!-- CHANGED: per Must-do #7 (5/6 consensus) --> | Default cadence: ~60s ± 10s jitter for all transient failures. **HTTP 429 exception**: on 429 response, honor `Retry-After` header if present; otherwise exponential backoff (60s → 120s → 240s → 480s → 600s cap); reset to 60s on next non-429 response. Carve-out preserves D7's "no permanent/transient distinction" for everything except the one failure where YouTube has explicitly told us to slow down. **Channel-ID-invalid case still NOT distinguished** (one Warn per ~60s indefinitely is the documented cost; see Consider C2 for an optional escalation receipt). |
| 8 | **Streamer status feedback: extended startup receipt + on YT state changes, with corrected wording.** <!-- CHANGED: per Must-do #8 (4/6 consensus); receipt wording fix --> | Receipt examples: startup `slay-the-streamer-2 v0.2 connected (Twitch). YouTube: no live broadcast found, retrying.` / mid-session connect `YouTube connected: tracking chat from <channelId>` / **mid-session disconnect `YouTube disconnected; will retry every ~60s`** (was `"live broadcast ended, will resume when next broadcast starts"` in v1 — wrong for network-failure and never-connected cases). Flap-suppression debounce **changed from 30s to 120s** (2× reconnect cadence). <!-- CHANGED: per Should-do #15 (4/6 consensus) --> |
| 9 | **Voter dedup keying: prefix YouTube `UserId` with `"yt:"`.** | Unchanged from v1. `Login` field uses YouTube **display name** (not channel ID) for log-forensics readability; `UserId` carries the `"yt:{channelId}"` unique key. <!-- CHANGED: per Should-do #29 (R6 nit) --> |
| 10 | **Receipt wording: Twitch close-receipts merged; in-game tally split per platform; gates on configuration not observation.** <!-- CHANGED: per Must-do #4 (3/6 consensus) --> | Split-line rendering activates when `youtubeChannelId` is configured (and YT child is in any non-Disposed state) — NOT when the second platform first votes. Avoids the mid-vote single→split rendering snap that v1's `_observedPlatforms` gating produced. |

## Architecture

```
src/
├── Ti/
│   ├── Chat/                                       ✏️  extended in v2
│   │   ├── IChatConsumer.cs                        🆕 v2 — new parent interface (read/send/state) <!-- CHANGED: Must-do #2 -->
│   │   ├── IChatService.cs                         ✏️  now `: IChatConsumer`, adds ConnectAsync only
│   │   ├── ChatMessage.cs                          ✅ unchanged — D9 discipline only
│   │   ├── ChatConnectionState.cs                  ✅ unchanged
│   │   ├── ChatPlatformNames.cs                    🆕 v2 — string constants for child names <!-- CHANGED: Should-do #20 -->
│   │   ├── TwitchIrcChatService.cs                 ✅ unchanged (still `: IChatService`)
│   │   ├── MultiChatService.cs                     🆕 v2 — `: IChatConsumer`; no aggregate ConnectionStateChanged event <!-- CHANGED: Should-do #11 -->
│   │   └── YouTubeChat/                            🆕 v2 — isolated scraper namespace
│   │       ├── YouTubeChatService.cs               🆕 v2 — `: IChatService`; state machine + poll loop
│   │       ├── YouTubeLiveChatScraper.cs           🆕 v2 — page parse + get_live_chat poll; clientVersion extraction
│   │       ├── YouTubeLiveBroadcastDiscovery.cs    🆕 v2 — channel/{ID}/live with consent-cookie handling
│   │       ├── IYouTubeHttp.cs                     🆕 v2 — pinned HTTP abstraction <!-- CHANGED: Should-do #30 -->
│   │       └── YouTubeChatModels.cs                🆕 v2 — internal DTOs
│   ├── Voting/
│   │   ├── VoteSession.cs                          ✏️  IsMultiPlatformConfigured flag + per-platform side-dict + display-name based Login awareness
│   │   ├── VoteCoordinator.cs                      ✏️  takes `IChatConsumer`, not `IChatService` <!-- CHANGED: Must-do #2 -->
│   │   └── ...                                     ✅ unchanged
│   └── Ui/
│       └── VoteTallyLabel.cs                       ✏️  split-line rendering + per-platform last-vote-marker + cached text invalidation on TallyChanged <!-- CHANGED: Should-do #16, Should-do #18 -->
├── Game/                                           ✅ no DecisionVotes/ changes; ModSettings only <!-- CHANGED: per R1 wording fix — drop "Game/ unchanged" claim -->
│   ├── Bootstrap/
│   │   └── ModSettings.cs                          ✏️  add `youtubeChannelId` with refined trim-first validation
│   └── DecisionVotes/                              ✅ no changes
└── ModEntry.cs                                     ✏️  construct MultiChatService; route ShouldEnforceSkipGate Twitch-specifically via GetChildState; extend startup receipt per D8 with 120s flap-suppression <!-- CHANGED: Must-do #1 -->

tests/
├── Ti/
│   ├── Chat/
│   │   ├── MultiChatServiceTests.cs                🆕 v2 — incl. 3 skip-gate-masking scenarios; mixed-terminal fall-through; dispose-ordering throw; LastError null; partial-failure SendMessageAsync (~18 tests) <!-- CHANGED -->
│   │   └── YouTubeChat/
│   │       ├── YouTubeLiveChatScraperTests.cs      🆕 v2 — incl. clientVersion extraction; defensive runs[]; paid-message-with-text-extracted; sticker-only-skipped; backlog-suppression (~12 tests) <!-- CHANGED -->
│   │       ├── YouTubeLiveBroadcastDiscoveryTests.cs 🆕 v2 — incl. EU consent redirect handling (~6 tests) <!-- CHANGED -->
│   │       └── YouTubeChatServiceTests.cs          🆕 v2 — state machine, 429 backoff, dispose-races, initial-poll suppression (~10 tests) <!-- CHANGED: Should-do — fills v1 test-tree omission -->
│   └── Voting/
│       └── VoteSessionPerPlatformTallyTests.cs     🆕 v2 — incl. IsMultiPlatformConfigured rendering, mid-vote stability (~8 tests)
└── Bootstrap/
    └── ModSettingsTests.cs                         ✏️  ~7 tests for D6: absent / JSON null / empty / whitespace-only / whitespace-trimmed-empty / control-char-post-trim / valid <!-- CHANGED: per Must-do #10 -->
```

**Net new code estimate**: ~960 LOC source + ~440 LOC tests (up from v1's 815 + 360 because of EU consent handling, 429 backoff, IChatConsumer split, vote-echo marker, scraper-version fingerprint, defensive runs[] iteration, and added test coverage). Still within notes/07's 1–2 week estimate.

## `IChatConsumer` / `IChatService` split <!-- NEW section per Must-do #2 -->

```csharp
namespace SlayTheStreamer2.Ti.Chat;

// New parent interface: everything except ConnectAsync.
public interface IChatConsumer : IDisposable {
    ChatConnectionState State { get; }
    bool IsConnected { get; }
    bool CanSend { get; }
    DateTimeOffset? LastMessageReceivedAt { get; }
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    void Disconnect();
    Task SendMessageAsync(string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default);
}

// Existing IChatService becomes the child: adds the connect-lifecycle method.
public interface IChatService : IChatConsumer {
    Task ConnectAsync(string channel,
        ChatCredentials? creds = null,
        CancellationToken ct = default);
}
```

**Impact**:
- `TwitchIrcChatService : IChatService` — no change to its class declaration (it already implements the full surface).
- `YouTubeChatService : IChatService` — same.
- `MultiChatService : IChatConsumer` — implements only the parent. **No `ConnectAsync` method exists** at this type, so no runtime throw, no LSP violation, no footgun.
- `VoteCoordinator` constructor signature: takes `IChatConsumer` (was `IChatService`). Behaviorally identical; type system now documents the intent.
- `Voter.Default.Chat` returns `IChatConsumer`.

This is **additive** to the established Plan A interface. Existing code paths and tests that reference `IChatService` continue to work. The only places that change type-signature are:
- `MultiChatService` declaration (`: IChatConsumer`).
- `VoteCoordinator` constructor + `Chat` property type.
- `Voter.Chat` static property type.

Estimated impact on Plan A: ~30 LOC of signature edits across `VoteCoordinator.cs` and `Voter.cs`. Test surface is unchanged because tests pass concrete `FakeChatService`.

## `ChatPlatformNames` constants <!-- NEW section per Should-do #20 -->

```csharp
namespace SlayTheStreamer2.Ti.Chat;

internal static class ChatPlatformNames {
    public const string Twitch = "twitch";
    public const string YouTube = "youtube";
}
```

Used by `MultiChatService` registration, `VoteSession.PlatformOf`, `VoteTallyLabel` ordering, and `ModEntry` receipt routing. Replaces magic-string scattering across files.

## `YouTubeChatService` (the read-only chat impl)

### State machine

Unchanged from v1 (Disconnected → Connecting → ConnectedReadOnly | Reconnecting; never AuthenticationFailed, never JoinFailed, never ConnectedReadWrite).

`IsConnected` includes `ConnectedReadOnly` and `Reconnecting` to match the existing `TwitchIrcChatService` convention. <!-- CHANGED: reject R1 C2 / R5 #1 per META-REVIEW Conflict 2 — codebase reality is that Twitch already includes Reconnecting; changing YT alone would create inconsistency. Flagged for v0.2 cross-service polish. -->

### Reconnect cadence (per D7 + 429 carve-out) <!-- CHANGED: per Must-do #7 -->

```csharp
private static readonly TimeSpan ReconnectBase = TimeSpan.FromSeconds(60);
private static readonly TimeSpan ReconnectJitter = TimeSpan.FromSeconds(10);
private static readonly TimeSpan ReconnectCap = TimeSpan.FromSeconds(600);

private int _consecutive429Count;

private TimeSpan NextReconnectDelay(Exception? lastError, TimeSpan? retryAfter) {
    if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        return ClampJitter(retryAfter.Value);

    if (lastError is HttpRequestException httpEx && IsHttp429(httpEx)) {
        _consecutive429Count++;
        var backoff = TimeSpan.FromSeconds(
            Math.Min(60 * Math.Pow(2, _consecutive429Count - 1), 600));
        return ClampJitter(backoff);
    }

    _consecutive429Count = 0;   // reset on non-429
    return ClampJitter(ReconnectBase);
}
```

Reset `_consecutive429Count` to 0 on the next successful 2xx (`ConnectedReadOnly` transition).

### Poll cadence (steady-state)

Unchanged from v1 (floor 1s, ceiling 10s, honor `timeoutMs`).

### Initial-poll backlog suppression <!-- NEW section per Must-do #6 (R1 O-2) -->

When `YouTubeChatService` first transitions to `ConnectedReadOnly` (or transitions to `ConnectedReadOnly` after `Reconnecting`), the FIRST `get_live_chat` POST response is treated as **cursor-establishing only**:
- Parse the response to extract the next continuation token and `timeoutMs`.
- **Do not emit any messages from this response.**
- Begin emitting from the second poll forward.

Rationale: YouTube's initial response can include backlog/history messages from before the mod connected. A YT viewer who typed `#1` 30s before mod startup could have their stale message land in the current vote. Cursor-establishing suppression is the same pattern `pytchat` uses.

Defensive note: this MAY drop a small number of genuinely-new messages that arrived in the window between the live page load and the first poll. Acceptable trade-off; the alternative (admitting backlog) silently corrupts vote tallies.

Add test: `InitialPollMessagesAreNotEmitted_OnFirstConnectAfterReconnect`.

### Service shape

```csharp
public sealed class YouTubeChatService : IChatService {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IYouTubeHttp _http;                       // <!-- CHANGED: pin interface; see IYouTubeHttp section -->
    private readonly IYouTubeLiveBroadcastDiscovery _discovery;
    private readonly IYouTubeLiveChatScraper _scraper;

    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private bool _disposed;
    private bool _firstPollAfterConnect;                       // <!-- NEW: backlog suppression -->
    private int _consecutive429Count;                          // <!-- NEW: 429 backoff -->
    private string? _channelId;
    private string? _videoId;
    private string? _continuation;
    private string? _innertubeApiKey;
    private string? _innertubeClientVersion;                   // <!-- NEW: extracted from page per Should-do #13 -->
    private CancellationTokenSource? _cts;
    private IDisposable? _retryTimer;
    private Task? _pollLoopTask;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.Reconnecting;
    public bool CanSend => false;
    // ...

    // State-transition logging from day one per Should-do #17:
    private void TransitionTo(ChatConnectionState next, string reason) {
        if (_state == next) return;
        var old = _state;
        _state = next;
        TiLog.Info($"[YouTubeChatService] {old} → {next}: {reason}");
        var args = new ChatConnectionChangedEventArgs(old, next, reason);
        _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args));
    }
}
```

### HTTP client lifecycle <!-- NEW section per Must-do #5 (C-7 / R2 / R5 / R6) -->

`IYouTubeHttp` is a thin abstraction around a single shared `HttpClient` owned by `YouTubeChatService`:

```csharp
public interface IYouTubeHttp : IDisposable {
    Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, CancellationToken ct);
    Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, CancellationToken ct);
}

// Production impl shape:
internal sealed class YouTubeHttp : IYouTubeHttp {
    private readonly HttpClient _client;

    public YouTubeHttp() {
        var cookies = new CookieContainer();
        // Pre-set EU consent cookie so channel-live redirect doesn't land on consent.youtube.com.
        cookies.Add(new Uri("https://www.youtube.com/"),
                    new Cookie("CONSENT", "YES+cb", "/", ".youtube.com"));

        var handler = new HttpClientHandler {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            CookieContainer = cookies,
            UseCookies = true,
        };
        _client = new HttpClient(handler) {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // Realistic UA — bump alongside the scraper regex on YouTube redesigns.
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
    }

    // ... methods ...

    public void Dispose() => _client.Dispose();
}
```

**Lifecycle**: One `IYouTubeHttp` instance per `YouTubeChatService` instance; constructed in the service constructor (or injected); disposed in `Dispose()`. The shared `CookieContainer` preserves session cookies that YouTube sets across the discovery → page-parse → poll chain.

**EU consent handling**: The `CONSENT=YES+cb` cookie is pre-set on construction. Without this, fresh requests from EU IPs redirect to `consent.youtube.com/m?continue=...`; the auto-redirect-follow would land on the consent page; `FindLiveVideoIdAsync` would see a non-`/watch?v=` final URL and return null forever. The pre-set cookie skips the consent wall the same way every cross-language scraper library does.

### Connection flow

Unchanged from v1 except for:
1. **Step 3a (NEW)**: After `_discovery.FindLiveVideoIdAsync` returns a `videoId`, extract BOTH `INNERTUBE_API_KEY` AND `INNERTUBE_CONTEXT.client.clientVersion` from the same live_chat page response. Cache both for the lifetime of the connection. <!-- CHANGED: Should-do #13 (R5 O-3) -->
2. **Step 7 (UPDATED)**: On successful `ConnectedReadOnly` transition, set `_firstPollAfterConnect = true`. The next poll runs in cursor-establishing mode (no message emission). <!-- CHANGED: Must-do #6 -->
3. **Step 8 (UPDATED)**: On HTTP 429 specifically, set `_lastError = httpEx`, compute backoff via `NextReconnectDelay(...)` honoring `Retry-After`. <!-- CHANGED: Must-do #7 -->

### Steady-state poll loop

```csharp
while (!ct.IsCancellationRequested && _state == ChatConnectionState.ConnectedReadOnly) {
    var pollDelay = ClampPollDelay(_lastTimeoutMs);
    await Task.Delay(pollDelay, ct);
    try {
        var (messages, nextContinuation, nextTimeoutMs) =
            await _scraper.PollAsync(_innertubeApiKey, _innertubeClientVersion, _continuation, ct);

        if (_firstPollAfterConnect) {
            // Cursor-establishing poll: update continuation, do NOT emit messages.
            _firstPollAfterConnect = false;
            TiLog.Debug($"[YouTubeChatService] cursor-established; suppressed {messages.Count} backlog messages");
        } else {
            foreach (var msg in messages) {
                if (msg.AuthorChannelId is null or { Length: 0 }) {
                    // <!-- CHANGED: drop the random-GUID fallback per Should-do #12 (R5 O-4) -->
                    TiLog.Debug($"[YouTubeChatService] skipped message with missing authorChannelId");
                    continue;
                }
                var chatMessage = new ChatMessage(
                    UserId: $"yt:{msg.AuthorChannelId}",
                    Login: msg.AuthorDisplayName,    // <!-- CHANGED: per Should-do #29 (R6 nit); was AuthorChannelId -->
                    DisplayName: msg.AuthorDisplayName,
                    Text: msg.Text,
                    ReceivedAt: _clock.UtcNow,
                    IsSubscriber: msg.IsChatMember,
                    IsModerator: msg.IsChatModerator,
                    IsVip: false);
                LastMessageReceivedAt = _clock.UtcNow;
                _dispatcher.Post(() => MessageReceived?.Invoke(this, chatMessage));
            }
        }

        if (nextContinuation is null) {
            TiLog.Info("[YouTubeChatService] poll returned no continuation; broadcast ended");
            TransitionTo(ChatConnectionState.Reconnecting, "live broadcast ended");
            ArmReconnect();
            return;
        }
        _continuation = nextContinuation;
        _lastTimeoutMs = nextTimeoutMs;
    } catch (Exception ex) {
        LastError = ex;
        TiLog.Warn($"[YouTubeChatService] poll failed: {ex.Message}");
        TransitionTo(ChatConnectionState.Reconnecting, $"poll failed: {ex.GetType().Name}");
        ArmReconnect();
        return;
    }
}
```

## `YouTubeLiveChatScraper` (the load-bearing fragility)

Unchanged from v1 except:

1. **Extracts `clientVersion`** from `INNERTUBE_CONTEXT.client.clientVersion` alongside `INNERTUBE_API_KEY` on initial-page parse. The `PollAsync` body uses the extracted version, not a hardcoded one. <!-- CHANGED: Should-do #13 (R5 O-3) — anti-bit-rot -->
2. **Paid-message handling fixed**: `liveChatPaidMessageRenderer` with a `message.runs[].text` field is extracted as a normal chat message. Text-less items (`liveChatMembershipItemRenderer`, sticker-only payments) are skipped silently. <!-- CHANGED: Must-do #9 (R1 H10) — reconciles v1 contradiction -->
3. **Defensive `runs[]` iteration**: skip non-text runs (emoji/image runs without `text` field) silently; don't throw. <!-- CHANGED: Should-do #28 (R2 §10) -->
4. **Scraper version fingerprint**: `private const string ScraperVersion = "2026-05-12-v1";` logged at Info on first successful parse: `[YouTubeChatService] scraper v2026-05-12-v1 active; tracking videoId={...}`. <!-- CHANGED: Should-do #19 (R3 O-8) -->

### Tests (updated)

~12 fixture-based tests:
- Normal response: 5 text messages extracted correctly.
- Response with a paid message containing `#1` text: **paid message extracted** (was: skipped in v1). <!-- CHANGED: per Must-do #9 -->
- Response with a membership-item (text-less): skipped without error.
- Response with a sticker-only paid message: skipped without error.
- Response with malformed renderer: skipped with Debug log.
- Response with no continuation: returns `(messages: [], nextContinuation: null, nextTimeoutMs: 0)`.
- Initial-poll suppression: first call after `ConnectedReadOnly` returns messages but service does NOT emit them. <!-- CHANGED: Must-do #6 -->
- Message with image-run mixed with text-runs: text concatenated correctly; image-run skipped.
- `clientVersion` extracted from page matches expected; passed through to `PollAsync` body. <!-- CHANGED: Should-do #13 -->
- Message with missing `authorChannelId`: dropped with Debug log (no `yt:anon-` fallback). <!-- CHANGED: Should-do #12 -->

## `YouTubeLiveBroadcastDiscovery`

Unchanged from v1 except:
- Uses the shared `IYouTubeHttp` (with pre-set consent cookie); EU streamers no longer hit `consent.youtube.com` loop. <!-- CHANGED: Must-do #5 -->
- Adds explicit test for `consent.youtube.com` redirect handling. <!-- CHANGED: Should-do —operator validation Step 3f covers it in addition to unit test -->

### Tests (updated)

~6 tests:
- Redirect chain ending at `/watch?v={id}` → returns videoId.
- Redirect to `/channel/{ID}` (no live) → returns null.
- `HttpRequestException` thrown → returns null.
- 200 OK directly to `/channel/{ID}/live` (no redirect) → returns null.
- **NEW: First request without consent cookie pre-set redirects to `consent.youtube.com`; with cookie pre-set, request succeeds.** <!-- CHANGED: Must-do #5 -->
- **NEW: Query-param-order variations (`?v=XYZ&foo=bar` vs `?foo=bar&v=XYZ`) both extract correctly** — defensive `Uri` parsing. <!-- CHANGED: R3 §8 nit -->

## `MultiChatService` (the aggregator) <!-- HEAVILY REVISED per Must-do #1, #3; Should-do #11, #14, #20, #21, #22 -->

### Shape

```csharp
namespace SlayTheStreamer2.Ti.Chat;

public sealed class MultiChatService : IChatConsumer {  // <!-- CHANGED: IChatConsumer not IChatService -->
    private readonly IReadOnlyDictionary<string, IChatConsumer> _children;

    public MultiChatService(params (string Name, IChatConsumer Service)[] children) {  // <!-- CHANGED: pinned constructor; IChatConsumer not IChatService -->
        if (children == null || children.Length == 0)
            throw new ArgumentException("MultiChatService requires ≥1 child", nameof(children));
        _children = children.ToDictionary(c => c.Name, c => c.Service);
        foreach (var (name, child) in children) {
            child.MessageReceived += OnChildMessageReceived;
            child.ConnectionStateChanged += (s, e) => OnChildConnectionStateChanged(name, s, e);
        }
    }

    public ChatConnectionState State => AggregateState();
    public bool IsConnected => _children.Values.Any(c => c.IsConnected);
    public bool CanSend => _children.Values.Any(c => c.CanSend);
    public DateTimeOffset? LastMessageReceivedAt =>
        _children.Values.Select(c => c.LastMessageReceivedAt).Where(x => x.HasValue).Max();
    public Exception? LastError => null;  // <!-- CHANGED: Should-do #21; aggregating loses info; consumers should query per-child -->

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChildConnectionStateChangedEventArgs>? ChildConnectionStateChanged;

    // <!-- CHANGED: Should-do #11; aggregate ConnectionStateChanged event removed; nothing in spec subscribes to it -->
    event EventHandler<ChatConnectionChangedEventArgs>? IChatConsumer.ConnectionStateChanged {
        add { /* no-op; the aggregator only exposes ChildConnectionStateChanged */ }
        remove { }
    }

    // <!-- NEW: Must-do #1; expose per-child state for ShouldEnforceSkipGate routing -->
    public ChatConnectionState GetChildState(string name) =>
        _children.TryGetValue(name, out var child) ? child.State : ChatConnectionState.Disposed;

    public IChatConsumer? GetChild(string name) =>
        _children.TryGetValue(name, out var child) ? child : null;

    public void Disconnect() {
        foreach (var c in _children.Values) {
            try { c.Disconnect(); } catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child Disconnect threw: {ex.Message}");
            }
        }
    }

    public Task SendMessageAsync(string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default) {
        // <!-- CHANGED: Should-do #14 (R5 O-5); partial-failure semantics: continue past child errors -->
        var sendable = _children.Values.Where(c => c.CanSend).ToList();
        if (sendable.Count == 0) {
            TiLog.Debug("[MultiChatService] SendMessageAsync: no CanSend children; dropping");
            return Task.CompletedTask;
        }
        var tasks = sendable.Select(async c => {
            try { await c.SendMessageAsync(text, priority, ct); return (true, (Exception?)null); }
            catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child SendMessageAsync threw: {ex.Message}");
                return (false, ex);
            }
        }).ToList();
        return Task.WhenAll(tasks).ContinueWith(_ => {
            // Success if at least one child succeeded; logged failures don't surface.
        });
    }

    public void Dispose() {
        // <!-- CHANGED: Should-do #22 (R2 S9 / R1 H6); try/catch per child to prevent one bad child blocking the others -->
        foreach (var c in _children.Values) {
            try { c.Dispose(); } catch (Exception ex) {
                TiLog.Warn($"[MultiChatService] child Dispose threw: {ex.Message}");
            }
        }
    }
}

public sealed record ChildConnectionStateChangedEventArgs(
    string ChildName,
    ChatConnectionChangedEventArgs Inner);
```

### Aggregate state rule (with fall-through fix) <!-- CHANGED: Must-do #3 (R6 P1#2) -->

| Priority | State | Match rule |
|---|---|---|
| 1 (best) | `ConnectedReadWrite` | At least one child matches |
| 2 | `ConnectedReadOnly` | At least one child matches |
| 3 | `Reconnecting` | At least one child matches |
| 4 | `Connecting` | At least one child matches |
| 5 | `Disposed` | All children in `Disposed` |
| 6 | `AuthenticationFailed` | All children terminal, at least one in `AuthenticationFailed` |
| 7 | `JoinFailed` | All children terminal, at least one in `JoinFailed` |
| 8 (worst) | `Disconnected` | None of the above matches |

**Mixed-terminal fall-through**: When all children are in terminal states (`AuthenticationFailed`, `JoinFailed`, `Disposed`, or `Disconnected`) but the states differ:
- Use the priority ranking `Disposed > AuthenticationFailed > JoinFailed > Disconnected`.
- Return the highest-priority terminal state observed in any child.

This is the explicit rule R6's P1#2 flagged as undefined in v1. Mixed-terminal observation also logs a Warn so the operator notices the split.

### Tests (updated)

~18 tests, including:
- **Three skip-gate-masking scenarios** for `GetChildState("twitch")` (Must-do #1).
- **Mixed-terminal fall-through**: child A `JoinFailed` + child B `Disposed` → aggregate `Disposed`.
- **Dispose-ordering throw**: child[0].Dispose() throws → child[1] is still disposed.
- **LastError is null** with multiple children erroring.
- **Partial-failure SendMessageAsync**: one child throws → at least-one-succeeded → completed task; both children throw → completed task with Warn logs.

## `VoteSession` per-platform tally (with `IsMultiPlatformConfigured`) <!-- CHANGED: Must-do #4 -->

### What changes

```csharp
public sealed class VoteSession : IDisposable {
    private readonly Dictionary<int, int> _tallies;
    private readonly Dictionary<(string Platform, int OptionIndex), int> _talliesByPlatform = new();
    private readonly bool _isMultiPlatformConfigured;            // <!-- NEW: configured-not-observed gating -->
    private readonly Dictionary<string, DateTimeOffset> _lastVoteByPlatform = new();   // <!-- NEW: vote-echo support, Should-do #18 -->

    // Constructor takes IsMultiPlatformConfigured from coordinator.
    internal VoteSession(..., bool isMultiPlatformConfigured) {
        _isMultiPlatformConfigured = isMultiPlatformConfigured;
        // ...
    }

    // Returns the dict if multi-platform is CONFIGURED, regardless of whether
    // both platforms have actually contributed votes yet. Returns null only
    // when single-platform deployment.
    public IReadOnlyDictionary<(string Platform, int OptionIndex), int>? TalliesByPlatform =>
        _isMultiPlatformConfigured ? _talliesByPlatform : null;

    public IReadOnlyDictionary<string, DateTimeOffset> LastVoteByPlatform => _lastVoteByPlatform;
}
```

`VoteCoordinator` derives `isMultiPlatformConfigured` from the `IChatConsumer` it was constructed with: if it's a `MultiChatService` with both a `twitch` and a `youtube` child registered, true; otherwise false. (The check happens once at coordinator construction; YT child existence is configuration-driven via settings.)

### Platform discrimination

```csharp
private static string PlatformOf(ChatMessage msg) =>
    msg.VoterKey.StartsWith("yt:", StringComparison.Ordinal)
        ? ChatPlatformNames.YouTube
        : ChatPlatformNames.Twitch;
```

Same logic as v1, using the new constants. <!-- CHANGED: Should-do #20 -->

### `_lastVoteByPlatform` update

On every accepted vote:
```csharp
_lastVoteByPlatform[platform] = _clock.UtcNow;
```

Used by `VoteTallyLabel` to show the `◀ just now` marker for ~3s after each YT vote. <!-- CHANGED: Should-do #18 (R3 O-7) -->

### Tests (updated, ~8)

- Pre-existing tests preserved.
- **`TalliesByPlatform` returns non-null when `IsMultiPlatformConfigured=true` even before any YT message arrives.** <!-- CHANGED: per Must-do #4 -->
- **Mid-vote stability**: rendering does not snap from single-line to split-line when the first YT message arrives if both platforms were configured.
- **`LastVoteByPlatform` updates correctly** for both Twitch and YT messages.

## `VoteTallyLabel` (split rendering + vote-echo + cached text) <!-- HEAVILY REVISED per Must-do #4, Should-do #16, #18 -->

```csharp
public override void _Process(double delta) {
    if (!GodotObject.IsInstanceValid(this) || _session is null) return;
    if (_session.State is VoteSessionState.Closed
                          or VoteSessionState.Cancelled
                          or VoteSessionState.Disposed) return;

    // Update only when state has changed or the seconds-left integer ticked.
    // Avoids per-frame StringBuilder + Dictionary allocation hot path.
    // <!-- CHANGED: Should-do #16 (4/6 consensus on per-frame allocation) -->
    var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
    var tallyVersion = _session.TallyVersion;     // Incremented on TallyChanged
    if (secondsLeft == _cachedSecondsLeft &&
        tallyVersion == _cachedTallyVersion &&
        !HasRecentVoteEchoExpiry()) return;

    _cachedSecondsLeft = secondsLeft;
    _cachedTallyVersion = tallyVersion;

    var sb = new StringBuilder();
    sb.AppendLine($"Chat voting — {secondsLeft}s left");

    var perPlatform = _session.TalliesByPlatform;
    if (perPlatform is null) {
        // Single-platform — original rendering path
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
    } else {
        // Multi-platform — render in explicit display order, not alphabetical.
        // <!-- CHANGED: Should-do #23 (R5 #12 / R6 nit) -->
        foreach (var platform in PlatformDisplayOrder) {
            if (!perPlatform.Keys.Any(k => k.Platform == platform)) continue;
            sb.Append($"{Capitalize(platform)}: ");
            for (int i = 0; i < _session.Options.Count; i++) {
                perPlatform.TryGetValue((platform, i), out var count);
                if (i > 0) sb.Append(", ");
                sb.Append($"{i}={count}");
            }
            // Vote-echo marker — <!-- CHANGED: Should-do #18 (R3 O-7) -->
            if (_session.LastVoteByPlatform.TryGetValue(platform, out var lastVote) &&
                _clock.UtcNow - lastVote < TimeSpan.FromSeconds(3)) {
                sb.Append(" ◀ just now");
            }
            sb.AppendLine();
        }
    }
    Text = sb.ToString();
}

private static readonly string[] PlatformDisplayOrder = {
    ChatPlatformNames.Twitch,
    ChatPlatformNames.YouTube,
};
```

The render-then-cache pattern eliminates the per-frame `Dictionary<(string,int),int>` copy and StringBuilder churn that 4/6 reviewers flagged. The single-platform path also benefits (preexisting issue, fixed for both rendering modes).

## `ModSettings` extension (D6 trim-first refinement)

### `ModSettings.Load` behaviour (per D6 v2) <!-- CHANGED: Must-do #10 -->

- Field is optional. Absent OR JSON `null` → `youtubeChannelId = null` (`Success`, NO warning).
- Empty string `""` → clamp to null (`Success`, NO warning).
- **Trim leading/trailing whitespace first.** If post-trim is empty → clamp to null (`Success`, NO warning). Common case for paste artifacts.
- **Post-trim non-empty containing any control character (`char.IsControl(c)` true)** → `Malformed`. Indicates corrupted input, not benign paste.
- Otherwise (non-empty, no control chars) → preserved as-is.
- No `schemaVersion` bump.

### Tests

~7 tests:
- Absent → `Success`, null, no warning.
- Explicit JSON `null` → same as absent.
- Empty string → clamped to null, `Success`, no warning.
- Whitespace-only (`"   "`) → trimmed to empty → clamped to null, `Success`, no warning. <!-- CHANGED: was Malformed in v1 -->
- Whitespace surrounding valid ID (`"  UCabc123  "`) → trimmed to valid value, preserved.
- Embedded control char in trimmed value → `Malformed`.
- Valid non-empty → preserved.

## `ModEntry` wiring

### Wiring <!-- CHANGED: Must-do #1, #2; constructor pinning -->

```csharp
var twitch = new TwitchIrcChatService(...);
_ = twitch.ConnectAsync(settings.Channel, new ChatCredentials(...));

YouTubeChatService? youtube = null;
if (!string.IsNullOrEmpty(settings.YoutubeChannelId)) {
    youtube = new YouTubeChatService(...);
    _ = youtube.ConnectAsync(settings.YoutubeChannelId);
}

var chat = youtube is null
    ? new MultiChatService((ChatPlatformNames.Twitch, twitch))
    : new MultiChatService(
        (ChatPlatformNames.Twitch, twitch),
        (ChatPlatformNames.YouTube, youtube));

chat.ChildConnectionStateChanged += OnChildConnectionStateChanged;

var voter = new VoteCoordinator(chat, /* IsMultiPlatformConfigured */ youtube is not null, ...);
Voter.Default = voter;
```

### Skip-gate routing for B.2.1 Decision 21 amendment <!-- NEW section per Must-do #1 -->

B.2.1's `ShouldEnforceSkipGate()` reads `Voter.Default.Chat.State`. Now that `Chat` is `IChatConsumer` (specifically a `MultiChatService`), reading `.State` returns the aggregate, which by best-of-children rules masks Twitch's terminal state when YT is alive.

**Fix**: B.2.1's amendment text (currently in [`2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) Decision 21 amendment) is updated as part of this slice's implementation work:

```csharp
private static bool ShouldEnforceSkipGate() {
    if (ModEntry.Settings is not SettingsResult.Success) return false;
    if (!CardRewardVotePatch.PreparedSuccessfully) return false;
    if (Voter.Default == null) return false;

    // <!-- NEW: route Twitch-state-check explicitly, not via aggregate -->
    if (Voter.Default.Chat is MultiChatService multi) {
        var twitchState = multi.GetChildState(ChatPlatformNames.Twitch);
        if (twitchState is ChatConnectionState.AuthenticationFailed
                        or ChatConnectionState.JoinFailed
                        or ChatConnectionState.Disposed) return false;
    } else {
        // Direct-Twitch path (no multi); fall back to the existing check.
        if (Voter.Default.Chat.State is ChatConnectionState.AuthenticationFailed
                                     or ChatConnectionState.JoinFailed
                                     or ChatConnectionState.Disposed) return false;
    }
    return true;
}
```

This is a small change to `CardRewardSkipGatePatch.cs` (B.2.1 code), in scope for this slice because the B.2.1 amendment was made obsolete by this slice's introduction of the aggregator. Add a unit test for each of the three masking scenarios in `MultiChatServiceTests`.

### Startup receipt extension (with 120s flap-suppression) <!-- CHANGED: Should-do #15 -->

D8 receipt logic unchanged conceptually; debounce constant raised from 30s to 120s (= 2× reconnect cadence). Wording updates per Must-do #8.

## Failure modes & degradation (updated)

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `youtubeChannelId` absent / JSON `null` / empty | Single-child `MultiChatService`; functionally identical to v0.1. |
| 1b | `youtubeChannelId` whitespace-only | Trimmed to empty → clamped to null → same as #1 (`Success`, no warning). <!-- CHANGED: D6 v2 --> |
| 1c | `youtubeChannelId` non-empty trimmed but contains control chars | `Malformed`; entire mod degrades. |
| 2 | Channel doesn't exist (404) | Discovery returns null → `Reconnecting`. Retry indefinitely per D7. Twitch unaffected. **No JoinFailed transition** (D7 anti-heuristic). See Consider C2 for 30-failure escalation receipt. |
| 3 | Channel exists, no live broadcast | Same as #2. When streamer goes live mid-session, next retry succeeds; D8 mid-session receipt fires. |
| 4 | **HTTP 429** | <!-- CHANGED: per Must-do #7 --> Honor `Retry-After` if present; else exponential backoff (60→120→240→480→600s cap). Reset on next 2xx. **NOT** indefinitely-60s like other failures. |
| 4b | **EU consent redirect** | <!-- NEW: per Must-do #5 --> Pre-set `CONSENT=YES+cb` cookie sidesteps this entirely. If somehow encountered, discovery returns null → `Reconnecting`. |
| 5 | YouTube changes `live_chat` page HTML shape | Scraper parse returns null/throws → `Reconnecting`. Repeats indefinitely. Maintainer ships parser update. `ScraperVersion` log helps user-vs-fix version diagnosis. <!-- CHANGED: Should-do #19 --> |
| 6 | YouTube changes `get_live_chat` JSON shape | Same as #5. |
| 7 | Network error | `Reconnecting`; D7 cadence. |
| 8 | Both Twitch and YT down | Independent reconnect loops. Aggregate `Reconnecting`. Skip gate routes through `GetChildState("twitch")` and degrades correctly. <!-- CHANGED: Must-do #1 -->|
| 9 | YT broadcast ends mid-vote | Steady-state poll returns no continuation → `Reconnecting`. Per-platform tallies preserved. UI continues showing YT row with frozen values for the rest of the window (no UI snap). |
| 10 | YT poll returns 0 messages every cycle | No-op. |
| 11 | YT scraper returns malformed message | Skip + Debug log. Loop continues. |
| 11b | **YT message missing `authorChannelId`** | <!-- CHANGED: per Should-do #12 --> Drop the message + Debug log. **No `yt:anon-` fallback** (v1's was a vector for tally pollution). |
| 12 | `MultiChatService.SendMessageAsync` with no CanSend children | <!-- CHANGED: per Should-do #14 --> Returns `Task.CompletedTask` with Debug log. **Not** throw. |
| 13 | `YouTubeChatService.SendMessageAsync` called directly | Returns `Task.FromException<NotSupportedException>("YT is read-only (D3)")`. |
| 14 | `MultiChatService` constructed with empty children | Throws `ArgumentException`. |
| 15 | Same human votes on both platforms (D1) | Counted twice. |
| 16 | YT-side vote wins | Merged close-receipt fires on Twitch (D10). |
| 17 | ~~YT message missing authorChannelId~~ | **Removed** — replaced by #11b. <!-- CHANGED: per Should-do #12 --> |
| 18 | YT message Text empty/whitespace | Vote regex won't match; ignored. |
| 19 | `ChildConnectionStateChanged` consumer throws | Caught; logged Error. |
| 20 | Settings file changed at runtime | Not supported; init-time load. |
| 21 | Twitch terminal-failure + YT connected | Skip gate routes via `GetChildState("twitch")` → degrades to vanilla per B.2.1 D21 amendment. YT votes still flow into `VoteSession`; in-game label renders YT row; no chat receipts fire. (Worst-of-both-worlds mode, documented). <!-- CHANGED: per Must-do #1 -->|
| 22 | **Mixed terminal states across children** | <!-- NEW: per Must-do #3 --> Aggregate state returns highest-priority terminal per `Disposed > AuthenticationFailed > JoinFailed > Disconnected`. Mixed-terminal observation logs Warn. |
| 23 | **`HttpClient` socket exhaustion** | <!-- NEW: per Must-do #5 --> Single shared `HttpClient` per `YouTubeChatService` instance prevents this. Cookies preserved across discovery → page-load → poll chain. |

## Noita-pattern regression analysis (per Should-do #26; R3 #2, R6 P2#3) <!-- NEW section -->

**Pre-existing behavior (Twitch-only)**: end-to-end latency ~0.5–2s (stream delay + chat send). Back-to-back vote collisions (a chatter types `#1` for vote N at second 28; message arrives at second 29–30 — usually still in vote N) are a **rare edge case**.

**With YT added**: YT end-to-end latency ~7–20s (broadcast latency 5–15s + chat lag 2–5s). A YT viewer typing `#1` at second 28 of vote N has their message arrive at second 33–48 of *some* vote — frequently vote N+1 if the streamer triggers the next vote quickly.

**Frequency change**: Twitch-only → "rare edge case." With YT → **every-vote-or-two**.

**User-visible behavior**: YT viewers see their vote land on the wrong question. No receipt (D3); no way to know.

**v1 spec posture**: deferred to v0.2 per `notes/06-followups-and-deferred.md`. That deferral was correct for Twitch-only — the issue was rare. It is **not correct** for the YT-augmented case.

**Mitigation options ranked**:
1. **Vote-echo on tally label** (Should-do #18, applied) — gives the streamer visual feedback that YT votes are arriving; streamer can verbally relay. **Does not** fix Noita; addresses the receipt-less feedback gap.
2. **Vote-nonce in chat commands** (Consider C3) — `#1!42` where 42 is the vote-ID; vote-parsing drops messages with stale or no-match nonce. Backward-compatible (bare `#1` → "current vote"). ~30 LOC in `Ti/Voting/`. **Does** fix Noita.
3. **Per-platform vote windows** — explicitly rejected by D2.
4. **Latency compensation** — explicitly rejected by D2.

**Spec author's call for v2**: ship with mitigation #1 applied (Should-do #18); offer mitigation #2 as Consider C3 with a lean-no recommendation (genuine scope creep into Plan A `Ti/Voting/`). Operator-validation should explicitly measure how often YT viewers' votes land on the wrong question; if it's >10% of YT votes in practice, escalate C3 to Must-do for a v2.1.

## Acceptance gate (updated)

Steps 0–7 from v1 preserved with three updates:

- **Step 3d wording**: "lost connection, will retry" replaces "live broadcast ended" (network-failure case wording fix per Must-do #8). <!-- CHANGED -->
- **Step 3f (NEW)**: EU consent redirect operator validation. Test from an EU IP (or simulate by clearing cookies); verify discovery succeeds with the pre-set consent cookie. <!-- CHANGED: per Must-do #5 -->
- **Step 7**: Now verifies receipt **DELIVERY** (not just flap-suppression). Confirm all expected receipts appear in Twitch chat under combined vote-burst + YT-flap conditions; verify no receipts dropped to ratelimit. <!-- CHANGED: per Should-do #27 (R3, R6) -->
- **Step 1 sub-case ADDED**: Skip-gate masking scenarios. With Twitch in `AuthenticationFailed` + YT in `ConnectedReadOnly`, verify `ShouldEnforceSkipGate()` returns false (gate degraded). With Twitch in `JoinFailed` + YT in `ConnectedReadOnly`, same. With Twitch in `Reconnecting` + YT in anything, gate stays enforced (transient is not terminal). <!-- CHANGED: per Must-do #1 -->

## Open questions

Carried forward from v1. Two FrostPrime-coordination questions become more urgent with v2:

- Q2 (YT chat volume): meta-review's Noita regression analysis quantifies the per-vote-collision-rate change. Need FrostPrime's YT viewer count to predict whether the rate-vs-pain-threshold tips toward Consider C3.
- Q5 (existing YT overlay): if FrostPrime is already using a `youtubei`-scraping overlay, we may double-load his channel. Coordinate.

## LOC estimate + risk areas (updated)

**Total**: ~960 LOC source + ~440 LOC tests (up from 815 + 360 in v1). Increase is from:
- `IChatConsumer` interface + `IChatService` declaration edits (~10 LOC).
- `YouTubeHttp` impl with consent cookie + UA + lifecycle (~50 LOC).
- 429 backoff logic (~15 LOC).
- Initial-poll suppression (~10 LOC).
- `clientVersion` extraction (~10 LOC).
- Vote-echo marker + cached label text (~30 LOC).
- `MultiChatService` shape revisions (`GetChildState`, partial-failure send, dispose try/catch) (~30 LOC).
- B.2.1 skip-gate routing update (~15 LOC in CardRewardSkipGatePatch.cs).

Still within notes/07's 1–2 week estimate (closer to 2 weeks at this point).

## Cross-references

- [`notes/07-youtube-chat-feasibility.md`](../../../notes/07-youtube-chat-feasibility.md) — feasibility writeup + Decisions log D1–D10.
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — Noita pattern entry; B.2.1 design pivot history.
- [`docs/superpowers/specs/META-REVIEW-2026-05-12-youtube-chat-integration-design.md`](./META-REVIEW-2026-05-12-youtube-chat-integration-design.md) — v1 → v2 reasoning behind every change.
- [`docs/superpowers/specs/2026-05-12-youtube-chat-integration-design-v1.md`](./2026-05-12-youtube-chat-integration-design-v1.md) — predecessor.
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) — B.2.1 spec whose Decision 21 amendment text is updated as part of this slice.
- [`src/Ti/Chat/`](../../../src/Ti/Chat/) — destination for IChatConsumer split + MultiChatService + YouTubeChat/.

---

## Optional Enhancements (pick what you want)

Consider-tier items from the meta-review. Reply with "also apply C1, C4, C7" (etc.) to fold any in; or leave as-is and they stay deferred.

### C1. `YouTubeChatStatusReason` enum — improves D8 receipt accuracy
- **What**: Add an internal `enum YouTubeChatStatusReason { None, NoLiveBroadcastFound, LiveBroadcastEnded, NetworkError, RateLimited, ScraperParseFailed, InvalidOrUnavailableChannel, UnknownError }`. `YouTubeChatService.LastStatusReason` exposes it. `ModEntry`'s D8 receipts use reason-specific wording when available.
- **Reviewers**: R1 (H7), R2 (§6.B). 2/6.
- **Effort**: Small (~20 LOC).
- **Recommendation**: **Lean yes**. Improves operator diagnostics; replaces "generic disconnect" wording with specific causes when known.

### C2. 30-failure D7 escalation receipt — addresses typo case
- **What**: Counter on `YouTubeChatService` that increments on every consecutive `Reconnecting` cycle without an intervening `ConnectedReadOnly`. At N=30 (≈30 min), fire one elevated-priority Twitch receipt: `YouTube: still no live broadcast after 30 min — check that "youtubeChannelId" is correct.` Reset counter on next successful connection.
- **Reviewers**: R6 (P2#1, only). 1/6.
- **Effort**: Small (~15 LOC).
- **Recommendation**: **Lean yes**. Addresses the real UX gap from D7's anti-heuristic posture without reintroducing fragile heuristics. The "1 Warn/min forever" cost was the spec's documented downside; this caps the user-invisible-failure window at ~30 min.

### C3. Vote-nonce / per-vote ID — Noita pattern fix
- **What**: Each `VoteSession` gets a 2-digit ID (0–99, cycles). Chat receipts include the ID: `Vote #42: #0 Strike, #1 Defend... (type #0!42)`. Vote-parsing regex accepts optional `!NN` suffix; bare `#1` still works (backward-compatible, treated as "current vote"); `#1!41` for a prior vote is dropped with Debug log.
- **Reviewers**: R6 (P2#3, only). 1/6 but with strong quantification (per Noita-pattern regression analysis above).
- **Effort**: Medium (~40 LOC in `Ti/Voting/VoteSession.cs` + `VoteCoordinator.cs` + receipt formatters; ~30 LOC tests).
- **Recommendation**: **Neutral**. Genuine scope creep into Plan A `Ti/Voting/` code. Real problem; real solution; correct timing depends on whether the YT slice can ship before B.2.2 with the Noita regression accepted as documented. Surfacing as Consider so the user can override the deferral.

### C4. Scraper health-check / telemetry hook
- **What**: If scraper parse fails N times in a row at the same point, log Error with the truncated failing-input shape. Helps post-redesign diagnosis.
- **Reviewers**: R2 (alt), R3 (alt C). 2/6.
- **Effort**: Trivial (~5 LOC).
- **Recommendation**: **Lean yes**. Cheap diagnostic.

### C5. YouTube vote-command discoverability receipt at startup
- **What**: When YT is configured, fire one startup Twitch receipt: `YouTube chat: type #0, #1, or #2 to vote. Votes appear on the in-game overlay.` Once at startup, priority Low.
- **Reviewers**: R3 (#6.1). 1/6.
- **Effort**: Trivial (~5 LOC).
- **Recommendation**: **Lean yes**. Closes the YT feedback-less-loop minimally; tells YT viewers the protocol the streamer is using.

### C6. `ChatMessage.Text` truncation for YouTube
- **What**: Truncate YT message Text to 500 chars in the YT child before raising `MessageReceived`. Documented limit; vote regex only needs first ~50 chars anyway.
- **Reviewers**: R3 (#6.3). 1/6.
- **Effort**: Trivial (~3 LOC).
- **Recommendation**: **Neutral**. No demonstrated need; YT messages are typically short. Cheap to add if you're already touching the path.

### C7. Cache `_videoId` across short-window failures
- **What**: Don't clear `_videoId` on every `Reconnecting`; keep it for ~5 min as a hint to skip re-discovery on quick reconnects. Clear on TTL expiry or on confirmed broadcast-ended response.
- **Reviewers**: R6 (alt). 1/6.
- **Effort**: Small (~10 LOC).
- **Recommendation**: **Neutral**. Possible perf optimization for the network-flake case. The cost of always re-discovering is small (one extra HTTP GET per 60s).

### C8. Operator-validation Step: members-only chat probe
- **What**: Add Step 8 to acceptance gate: enable members-only mode on a test broadcast; verify the scraper degrades gracefully (probably surfaces as "no continuation" → treats as broadcast-ended → `Reconnecting`).
- **Reviewers**: R6 (additions). 1/6.
- **Effort**: Trivial (~1 line in acceptance gate; operator-time only).
- **Recommendation**: **Lean yes**. Confirms D5's "non-goal" failure mode is well-behaved, not just declared.

### C9. Refresh-fixtures-monthly task in `notes/`
- **What**: A documented monthly task in `notes/` to capture a fresh `live_chat` response into the fixture directory. Optionally with a PowerShell helper script that fetches + anonymizes.
- **Reviewers**: R6 (additions). 1/6.
- **Effort**: Trivial (one notes/ entry + optional 30-LOC script).
- **Recommendation**: **Lean yes**. Cheap insurance against fixture rot.

### C10. Display-name index for D1 future heuristic
- **What**: Pre-add `_voterDisplayNames : Dictionary<string, string>` to `VoteSession` (tracking display name per voter key). Enables D1's "same-display-name across platforms = drop YT vote" heuristic as v0.2 polish without retrofitting the data structure.
- **Reviewers**: R6 (additions). 1/6.
- **Effort**: Trivial (~10 LOC).
- **Recommendation**: **Lean no**. Speculative; YAGNI. The heuristic itself is on the v0.2 list with no concrete demand. Add when implementing the heuristic.

---

**v2 status**: Must-do + Should-do applied. Optional Enhancements above are the user's pick list — reply with the numbers you want folded in. The updated plan has Must-do and Should-do changes applied. Review the Optional Enhancements list above and tell me which numbers to add, if any.
