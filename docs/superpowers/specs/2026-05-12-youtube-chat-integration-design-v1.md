# YouTube chat parallel integration (design v1)

**Date**: 2026-05-12
**Status**: Draft v1 — promoted from [`notes/07-youtube-chat-feasibility.md`](../../../notes/07-youtube-chat-feasibility.md). Decisions D1–D10 were resolved in the notes/07 conversation 2026-05-12 (D6 was added retroactively during this spec-drafting session after the gap was noticed); this spec consolidates them into an implementation-ready shape. **Not yet implementation-ready in the same operational sense as B.2.1 v4** — pending an optional meta-review pass and/or FrostPrime input on the open questions section.
**Predecessor**: feasibility writeup at [`notes/07-youtube-chat-feasibility.md`](../../../notes/07-youtube-chat-feasibility.md). v0.2+ index entry at [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) under "YouTube chat parallel integration".
**Scope**: First slice of the **multi-platform chat** capability. Adds a `YouTubeChatService : IChatService` scraping the `youtubei` internal endpoint, a `MultiChatService` aggregator that wraps it alongside the existing `TwitchIrcChatService`, and the minimal `Ti/Voting/` + `Ti/Ui/` changes to support split-by-platform tally display (per D10). Read-only on YouTube; outgoing receipts continue to flow through Twitch only.

> **Architectural hard constraint** (carried forward from B.1/B.2.1): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. This spec changes ONE collaborator (`IChatService` becomes a multi) without touching `VoteCoordinator` or any Harmony patch class. The hard constraint is unaffected; B.1's vote patch and B.2.1's card-reward patch keep working with the same surface area.

## Author's note

This is a v1 draft of a feature that has had **no meta-review pass yet** — unlike B.1's 7-reviewer crowd or B.2.1's 7-reviewer + GPT5.5 follow-up. Surfinite's working assumption after promoting notes/07 to a spec is that a meta-review is *optional* for this slice because (a) the architectural shape mirrors B.1/B.2.1's well-trodden `Ti/Chat/` surface, (b) the only load-bearing fragility is the YouTube scraper which is structurally isolated, and (c) the decisions log already captures the contested choices. Decide post-draft whether to invoke `superpowers:document-context` + `meta-review` or move directly to `superpowers:writing-plans`.

Headline shifts from the notes/07 feasibility writeup:

1. **D7 retry cadence semantics nailed down** — the writeup says "retry every ~60s"; this spec defines the concrete state machine that produces that behaviour, including the mapping from "no live broadcast found" onto the existing `ChatConnectionState.Reconnecting` value (vs. inventing a YT-only state).
2. **Channel ID invalid edge case explicitly **NOT** distinguished** — a permanently invalid channel ID still gets the D7 "log Warn + retry every 60s" treatment, not `JoinFailed`. Avoids fragile attempts to disambiguate transient 404s from permanent ones via HTTP response shape; the only cost is a never-ending retry log when the operator typos their channel ID, which is fine (one Warn line per 60s and the in-game UI shows nothing).
3. **Platform discrimination via `VoterKey` prefix, not a `Platform` field** — per D9, `ChatMessage` schema is unchanged; YouTube's `UserId` is set to `$"yt:{channelId}"` and any consumer that needs platform info derives it from `VoterKey.StartsWith("yt:")`. A single private static `PlatformOf(ChatMessage)` helper in `VoteSession` carries the discrimination logic.
4. **Per-platform tally as a parallel side-dict, not a replacement** — `VoteSession._tallies : Dictionary<int, int>` stays as merged-total (close-receipts unchanged); a new `_talliesByPlatform : Dictionary<(string, int), int>` parallel structure feeds the UI label split rendering. Avoids breaking any of B.1's receipt invariants.
5. **MultiChatService aggregate `State` defined explicitly** — best-of-children, with documented mapping. The notes/07 writeup left this "TBD"; this spec resolves it.
6. **Twitch-only-deployment is unchanged** — when `youtubeChannelId` is null/absent in settings, `MultiChatService` is constructed with only the Twitch child and is functionally identical to passing the Twitch service directly. **`ModEntry` always wires `MultiChatService`**, even in the Twitch-only case, to keep one code path. Zero-overhead in the single-child case.
7. **Aggregate state propagation on dispose** — clarified: disposing `MultiChatService` disposes all child services. `ModEntry` hands ownership to the multi at construction time.

## Goals

1. **Read YouTube live chat in parallel with Twitch.** Both platforms feed a single merged vote tally; chat from either platform can drive a vote winner.
2. **Architectural fit must be invisible to `VoteCoordinator` and every Harmony patch.** The vote layer (`Ti/Voting/`), the existing patches (`NeowBlessingVotePatch`, `CardRewardVotePatch`, `CardRewardSkipGatePatch`), and the activation gate (`ShouldEnforceSkipGate`) all stay unchanged. The only `Ti/Voting/` surface change is the parallel per-platform tally; existing consumers see no behavioural difference.
3. **YouTube is read-only.** No `SendMessageAsync` path to YouTube. Receipts continue to flow through Twitch.
4. **The in-game tally label shows separate per-platform lines when YT is enabled** (per D10). Streamer and chat see which platform's votes are tracking where.
5. **Fail soft on every new YouTube failure mode.** Endpoint shape changed, no live broadcast, ratelimited, channel ID invalid, network error — all degrade to Twitch-only with a Warn log and periodic retry. Mod stays loaded; Twitch keeps working.
6. **De-risk the future "lift `Ti/*` into a base mod" goal** by making the multi-platform aggregator pattern real now — it generalises beyond the StS2 mod.

## Non-goals

- **Cross-platform identity dedup.** Same human voting on both Twitch and YouTube counts as two votes (per D1). No display-name heuristics in v1.
- **Per-platform vote windows.** Single shared 30s window; YouTube under-represents by 2–5s due to its end-to-end lag (per D2). Documented limit.
- **YouTube outgoing receipts.** No `SendMessageAsync` path to YouTube (per D3). OAuth + Google verification is incompatible with "mod end users install".
- **Members-only chat support.** Anonymous scraping works only for public live chat (per D5). Streamers running members-only mode disable that restriction during mod-using streams.
- **YouTube-only deployments without Twitch.** Twitch remains the *receipt* channel per D8 — if Twitch isn't configured, in-game tally label is the only feedback, no chat receipts fire on any platform. (Architecturally supported via `MultiChatService`, but UX is degraded — document as a known mode, don't optimise for it.)
- **Manual video ID per stream.** Channel ID + auto-discovery only (per D4). Streamer configures once; mod handles per-stream video resolution.
- **Visual-combined tally rendering.** Split-by-platform lines only (per D10). A unified visual layout is explicitly deferred to a later iteration.
- **Super Chat / Super Sticker handling.** Reads the messages like normal chat (text + author); no monetary special-casing.
- **Latency compensation.** No adaptive window or per-platform offset (per D2).
- **YouTube-side moderation / VIP / member-priority filtering.** `IsSubscriber` / `IsModerator` map sensibly from YouTube's `isMember` / `isModerator`; no v1 consumer of those flags.
- **Helper / base-class extraction for "multi-platform chat".** Rule of three: not yet a pattern, just an aggregator for this one feature.
- **Localised receipts.** Same English-only receipts via Twitch; YT viewers see only the in-game tally label.
- **In-game error toasts.** Failures route to log only.
- **Persisting any YT state across save/quit/reload.** Connection state is process-lifetime; restart restarts discovery.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | **Cross-platform vote-counting: count twice.** Same human voting on both Twitch and YouTube counts as two votes via `(Platform, UserId)` keying — pragmatic impl: prefix YouTube `UserId` with `"yt:"` per D9. | Policing identity across anonymous chat platforms is fundamentally unfixable; matching display names doesn't prove same human and differing names don't disprove. **Future optional heuristic** (not v1): if same display name observed on both platforms during the same vote, prefer the Twitch vote and drop the YouTube vote. Picking-latest by timestamp is unreliable because YouTube chat lags Twitch by 2–5s — the timestamps don't align across the two clocks. |
| 2 | **Vote-window timing: single shared 30s window, no adjustment for YT lag.** YouTube under-represents because of its 2–5s end-to-end lag; accepted as a known limit for v1. | Adaptive windows and per-platform-close timers add stateful complexity for a problem that may not matter — the streamer's chat-balance is whatever it is on each platform; the mod doesn't owe statistical parity. Revisit only if FrostPrime's YT viewers complain. |
| 3 | **Outgoing-receipt policy: read-only YouTube; receipts via Twitch only.** No `SendMessageAsync` path to the YT service. | YouTube's posting path requires OAuth + Google app verification — multi-week process, incompatible with "mod end users install". YouTube viewers see the in-game tally label but no Twitch-style chat receipts. |
| 4 | **Video ID discovery: channel ID + auto-discovery.** Streamer configures `youtubeChannelId` once in settings JSON. At mod start (and on reconnect cadence per D7), the service GETs `https://www.youtube.com/channel/{ID}/live` and follows the redirect to find the active video ID. | Manual video ID per stream was rejected as worse UX (streamer has to edit JSON before every stream). The auto-discovery endpoint is a second scraped surface, slightly increasing fragility, but the cost is small (one extra GET per ~60s when no broadcast is live; zero per-poll cost once a broadcast is found). |
| 5 | **Members-only chat: not supported in v1.** | Anonymous scraping works only for public live chat. Members-only chat requires authenticated session cookies — brittle, security-sensitive, real onboarding hurdle. Document as a known limit. Streamers running members-only mode disable that restriction during mod-using streams. |
| 6 | **Settings JSON: add just `youtubeChannelId`** (optional, nullable string). Missing field OR `null` → YouTube disabled, only Twitch runs (NOT a `Malformed` condition — additive optional field). Non-empty value → YT enabled; mod auto-discovers active broadcast via the channel `/live` redirect per D4. `ModSettings.Load` treats whitespace-only OR control-char values as `Malformed` (same pattern as `oauthToken`'s shape-validation). Everything else (retry cadence per D7, polling intervals from YouTube's `timeoutMs`, receipt-on-state-change behaviour per D8) is hardcoded for v1. | Single optional field is the smallest possible surface change; no `schemaVersion` bump needed. Malformed-on-whitespace matches the existing B.1 `oauthToken` pattern so behaviour stays predictable. **Escape-hatch field `youtubeVideoIdOverride` was explicitly considered and rejected**: if YouTube ever changes the `/live` redirect format and breaks auto-discovery, we ship a code fix rather than asking streamers to find video IDs themselves. Resolved 2026-05-12 retroactively after this spec-drafting session noticed the gap in the original notes/07 decisions log. |
| 7 | **YouTube failure mode: silent degradation + periodic retry every ~60s.** All non-permanent YT failure modes (no live broadcast / endpoint shape broken / network error / channel ID invalid / ratelimited) get the same treatment: log Warn, transition to `Reconnecting`, schedule next attempt in 60s ± jitter. Twitch keeps working. Votes count Twitch-only until YT recovers. | Matches the spirit of `TwitchIrcChatService`'s transient-reconnect semantics and v4 spec Decision 21's "temp disconnect doesn't disable gate". Crucially: we deliberately do NOT try to distinguish "permanent" failures (e.g., misspelled channel ID → 404 forever) from "transient" ones (e.g., DNS hiccup) via response shape — heuristics there are fragile and unrewarding. Cost: a never-ending Warn-log loop when the streamer typos their channel ID, which is fine (one Warn per 60s; in-game UI shows nothing). |
| 8 | **Streamer status feedback: Twitch chat receipt at startup + on YT state changes.** The existing `slay-the-streamer-2 v… connected` startup receipt is extended to also report YouTube state. Examples: `slay-the-streamer-2 v0.2 connected (Twitch). YouTube: no live broadcast found, retrying.` / `slay-the-streamer-2 v0.2 connected (Twitch & YouTube tracking <channel>).` When YT later connects mid-session, a second receipt fires: `YouTube connected: tracking chat from <channel>`. When YT later disconnects mid-session (e.g., stream ends), a third receipt fires: `YouTube disconnected: live broadcast ended, will resume when next broadcast starts`. | Fits the Twitch-only-receipts model from D3 — no YT-side echo. Receipts fire from `ModEntry` (or the existing startup-receipt module — pin in implementation), subscribing to `MultiChatService.ConnectionStateChanged` and filtering on per-child transitions. Periodic re-receipts when the YT state churns (e.g., flapping every 60s) MUST be suppressed — only fire on transitions, never on retries that don't change effective state. |
| 9 | **Voter dedup keying: prefix YouTube `UserId` with `"yt:"`.** `ChatMessage` schema is unchanged. `YouTubeChatService` sets `UserId = $"yt:{authorChannelId}"` when raising `MessageReceived`. | `ChatMessage.VoterKey` is already `UserId ?? $"login:{Login}"`. Twitch user IDs are bare numeric strings; prefixing YT with `"yt:"` guarantees no collision and is forward-compatible (a future Discord/Kick/etc. service uses its own prefix). This is load-bearing for D1 (per-platform double-counting) and for the platform-discrimination helper in `VoteSession`. |
| 10 | **Receipt wording: Twitch chat receipts unchanged (merged tally invisibly), in-game vote-tally label MUST show separate per-platform lines when YT is enabled.** Format example: `Twitch: 0=1, 1=3, 2=0` / `YouTube: 0=0, 1=2, 2=1`. Visual-combining (one line, colour-coded; aggregate bar; etc.) is deferred. **`VoteSession` extends to track votes by `(platform, optionIndex)` AND merged `optionIndex`; close-receipts use the merged view.** | The decision pushes the only `Ti/Voting/` complexity outside the Chat layer. Twitch close-receipts staying merged is the right tradeoff: chat already sees `Chat chose Bash` as a single result, and adding per-platform breakdowns to receipt text would balloon the receipt size for a viewer audience that's already on the right platform. The split label is for the streamer and stream overlay; receipts are for the chatters who voted. |

## Architecture

```
src/
├── Ti/
│   ├── Chat/                                       ✏️  extended in v1
│   │   ├── IChatService.cs                         ✅ unchanged — interface already shaped for read-only impls
│   │   ├── ChatMessage.cs                          ✅ unchanged — D9 discipline only (no schema change)
│   │   ├── ChatConnectionState.cs                  ✅ unchanged — Reconnecting/JoinFailed/ConnectedReadOnly used for YT semantics
│   │   ├── TwitchIrcChatService.cs                 ✅ unchanged
│   │   ├── MultiChatService.cs                     🆕 v1 — aggregator wrapping N child IChatService
│   │   └── YouTubeChat/                            🆕 v1 — new sub-namespace; isolated scraper fragility
│   │       ├── YouTubeChatService.cs               🆕 v1 — IChatService impl, owns the state machine + poll loop
│   │       ├── YouTubeLiveChatScraper.cs           🆕 v1 — pure scraping: initial-page parse, get_live_chat poll
│   │       ├── YouTubeLiveBroadcastDiscovery.cs    🆕 v1 — channel/{ID}/live redirect-follow for video ID
│   │       └── YouTubeChatModels.cs                🆕 v1 — internal records for parsed response shapes
│   ├── Voting/                                     ✏️  minimal extension for D10
│   │   ├── VoteSession.cs                          ✏️  add parallel _talliesByPlatform side-dict + PlatformOf helper + TalliesByPlatform accessor
│   │   ├── VoteCoordinator.cs                      ✅ unchanged
│   │   └── ...                                     ✅ unchanged (snapshot, policies, etc.)
│   └── Ui/
│       └── VoteTallyLabel.cs                       ✏️  split-line rendering when TalliesByPlatform.Keys span >1 platform
├── Game/                                           ✅ unchanged — no Game-side patches in this slice
│   ├── Bootstrap/
│   │   └── ModSettings.cs                          ✏️  add optional `youtubeChannelId` key (nullable string)
│   └── ...                                         ✅ unchanged
└── ModEntry.cs                                     ✏️  construct MultiChatService(twitch, youtube?) and hand to VoteCoordinator; extend startup-receipt per D8

tests/
├── Ti/
│   ├── Chat/
│   │   ├── MultiChatServiceTests.cs                🆕 v1 — aggregator behaviour: state merge, message forwarding, sendCanSend routing, dispose-propagation (~12 tests)
│   │   └── YouTubeChat/
│   │       ├── YouTubeLiveChatScraperTests.cs      🆕 v1 — pure parse tests against canned response fixtures (~8 tests)
│   │       └── YouTubeLiveBroadcastDiscoveryTests.cs 🆕 v1 — redirect-follow + video-ID extraction tests (~4 tests)
│   └── Voting/
│       └── VoteSessionPerPlatformTallyTests.cs     🆕 v1 — per-platform tallies populated correctly, merged view unchanged, single-platform falls back gracefully (~6 tests)
└── Bootstrap/
    └── ModSettingsTests.cs                         ✏️  add ~6 tests covering D6: absent / JSON null / empty string / whitespace-only / control-char / valid non-empty
```

**Net new code estimate**: `YouTubeChatService` ~250 LOC (state machine + poll loop + reconnect cadence); `YouTubeLiveChatScraper` ~180 LOC (page-parse regex + `get_live_chat` POST + JSON traversal); `YouTubeLiveBroadcastDiscovery` ~80 LOC (channel-page GET + redirect follow + video-ID extract); `YouTubeChatModels` ~40 LOC (internal record types); `MultiChatService` ~150 LOC (state aggregation + event forwarding + dispose); `VoteSession` additions ~30 LOC + `VoteSnapshot` ~10 LOC if exposed; `VoteTallyLabel` additions ~40 LOC for split rendering; `ModSettings` additions ~15 LOC; `ModEntry` additions ~20 LOC. **Total ~815 LOC of source**, ~360 LOC of tests. Within the notes/07 1–2-week estimate; ~70% of the risk lives in the two scraper files.

## `YouTubeChatService` (the read-only chat impl)

### Reflected / external surface verification by Prepare-equivalent

YouTube has no in-process `Prepare`-style hook; instead the service's `ConnectAsync` is the first attempt point. The `MultiChatService` and `ModEntry` rely on the service to enter `Reconnecting` (not `JoinFailed`, not `AuthenticationFailed`) on any transient or unknown error. The service itself does NOT throw from `ConnectAsync`; failures surface via `ConnectionStateChanged → Reconnecting`.

### State machine

| Trigger | From | To | Notes |
|---|---|---|---|
| `ConnectAsync(channelId)` called | `Disconnected` | `Connecting` | First attempt to discover live broadcast |
| Live broadcast found + `get_live_chat` initial poll returns 200 OK with continuation | `Connecting` | `ConnectedReadOnly` | Begin scrape loop |
| Live-broadcast discovery returns no video ID | `Connecting` | `Reconnecting` | Per D7; schedule next attempt in ~60s |
| Live-broadcast discovery HTTP 4xx/5xx | `Connecting` | `Reconnecting` | Per D7; same cadence |
| Initial `get_live_chat` returns ≠ 200 OR continuation missing | `Connecting` | `Reconnecting` | Per D7; same cadence |
| Steady-state poll succeeds with new continuation | `ConnectedReadOnly` | `ConnectedReadOnly` | No transition; raise `MessageReceived` for each item |
| Steady-state poll returns empty continuation (live ended) | `ConnectedReadOnly` | `Reconnecting` | Stream ended; per D7 schedule retry — when streamer resumes broadcast, auto-pickup |
| Steady-state poll throws / returns non-200 | `ConnectedReadOnly` | `Reconnecting` | Same |
| `Disconnect()` called | any | `Disconnected` | Caller intent; stop poll loop |
| `Dispose()` called | any | `Disposed` | Service torn down |

**Notes on the state choices**:
- `AuthenticationFailed` is **never reachable** for this service. There is no auth.
- `JoinFailed` is **never reachable**. The "channel ID is permanently invalid" failure is observationally indistinguishable from "live broadcast not started yet" and gets the same `Reconnecting` + 60s retry treatment per D7.
- `ConnectedReadWrite` is **never reachable**. `CanSend` is hardcoded `false`.
- `Reconnecting` is the workhorse "I'm not happy but I'm trying" state. Both initial-startup failure modes (no broadcast yet, network error during discovery) and steady-state failure modes (poll returned 500, stream ended, continuation went stale) land here. The 60s retry cadence per D7 is the heartbeat of recovery.

### Reconnect cadence (per D7)

```csharp
private static readonly TimeSpan ReconnectBase = TimeSpan.FromSeconds(60);
private static readonly TimeSpan ReconnectJitter = TimeSpan.FromSeconds(10);
// Single fixed cadence, not exponential backoff. Rationale:
//   * The failure-mode distribution skews toward "stream isn't live yet"
//     which has no faster-recovery property — exponential backoff would
//     just delay recovery once the stream goes live.
//   * 60s is the floor below which we'd risk ratelimiting from YouTube
//     for repeated misses on the channel-live-redirect endpoint.
//   * Jitter (±10s) avoids fleet-coordination when many streamers
//     start at "the top of the hour" together.
```

### Poll cadence (steady-state)

The `get_live_chat` response includes a `timeoutMs` field telling us when YouTube wants the next poll. Honour it. Floor at 1s (defensive against malformed responses claiming `timeoutMs: 0`). Ceiling at 10s (defensive against responses claiming hours, which would silently stall the read loop).

```csharp
private static readonly TimeSpan PollFloor = TimeSpan.FromSeconds(1);
private static readonly TimeSpan PollCeiling = TimeSpan.FromSeconds(10);
```

### Service shape (behavioural contract)

```csharp
namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

public sealed class YouTubeChatService : IChatService {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly Func<IYouTubeHttp> _httpFactory;
    private readonly IYouTubeLiveBroadcastDiscovery _discovery;
    private readonly IYouTubeLiveChatScraper _scraper;

    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private bool _disposed;
    private string? _channelId;
    private string? _videoId;
    private string? _continuation;
    private string? _innertubeApiKey;
    private CancellationTokenSource? _cts;
    private IDisposable? _retryTimer;
    private Task? _pollLoopTask;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.Reconnecting;
    public bool CanSend => false;   // hardcoded; D3
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    public YouTubeChatService(
        IMainThreadDispatcher dispatcher,
        IClock clock,
        ITimerScheduler scheduler,
        Func<IYouTubeHttp> httpFactory,
        IYouTubeLiveBroadcastDiscovery discovery,
        IYouTubeLiveChatScraper scraper) { /* ctor stash */ }

    public Task ConnectAsync(string channelId, ChatCredentials? creds = null, CancellationToken ct = default) {
        // creds parameter ignored (no auth on YT scraping).
        // channelId is the YouTube channel ID (e.g., "UCabc...").
        // Returns Task.CompletedTask; failures surface via ConnectionStateChanged.
    }

    public void Disconnect() { /* stop loop, transition to Disconnected */ }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
            CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("YouTubeChatService is read-only (D3)."));

    public void Dispose() { /* idempotent; transition to Disposed */ }
}
```

### Connection flow (the load-bearing detail)

1. `ConnectAsync(channelId)` called → `_channelId = channelId`; transition `Disconnected → Connecting`.
2. Spawn an async task (`Task.Run`) that calls `_discovery.FindLiveVideoIdAsync(_channelId, ct)`.
3. Discovery flow:
   a. GET `https://www.youtube.com/channel/{_channelId}/live`, **allow auto-redirect**.
   b. If final URL matches `https://www.youtube.com/watch?v={videoId}` (post-redirect), extract `videoId`.
   c. If final URL is anything else (no redirect, channel-page-only, 404, etc.), return `null` (no live broadcast).
4. If discovery returns null: log Warn `[YouTubeChatService] no live broadcast found for {channelId}; retrying in ~60s`; transition `Connecting → Reconnecting`; arm retry timer.
5. If discovery returns `videoId`: `_videoId = videoId`; proceed to scraper initial-page parse.
6. Scraper flow:
   a. GET `https://www.youtube.com/live_chat?v={_videoId}`. Parse out `INNERTUBE_API_KEY` (regex on `"INNERTUBE_API_KEY":"([^"]+)"`) and initial continuation token (regex on the `liveChatRenderer` initial-continuations JSON path, extracted heuristically as the inner-most contained `"continuation":"..."` near `liveChatRenderer`).
   b. POST `https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={_innertubeApiKey}` with JSON body `{"context": {"client": {"clientName": "WEB", "clientVersion": "2.20240101.00.00"}}, "continuation": "{_continuation}"}`.
   c. Parse response: extract list of message actions, each with `liveChatTextMessageRenderer.message.runs[].text`, `liveChatTextMessageRenderer.authorName.simpleText`, `liveChatTextMessageRenderer.authorExternalChannelId`, badge flags (`isChatMember`, `isChatModerator`). Extract next `continuation` token and `timeoutMs`.
7. On success of step 6: transition `Connecting → ConnectedReadOnly`; begin steady-state poll loop.
8. On any failure in step 6 (HTTP non-200, regex mismatch, JSON parse error, empty response): log Warn with context; transition `Connecting → Reconnecting`; arm retry timer. **`_videoId` is cleared** so the next retry starts from discovery, not from the cached video.

### Steady-state poll loop

```csharp
while (!ct.IsCancellationRequested && _state == ChatConnectionState.ConnectedReadOnly) {
    var pollDelay = ClampPollDelay(_lastTimeoutMs);
    await Task.Delay(pollDelay, ct);
    try {
        var (messages, nextContinuation, nextTimeoutMs) =
            await _scraper.PollAsync(_innertubeApiKey, _continuation, ct);
        foreach (var msg in messages) {
            // D9: prefix UserId with "yt:". msg.AuthorChannelId is YouTube's stable per-author ID.
            var chatMessage = new ChatMessage(
                UserId: $"yt:{msg.AuthorChannelId}",
                Login: msg.AuthorChannelId,     // No "login name" concept on YT; use channel ID
                DisplayName: msg.AuthorDisplayName,
                Text: msg.Text,
                ReceivedAt: _clock.UtcNow,
                IsSubscriber: msg.IsChatMember,
                IsModerator: msg.IsChatModerator,
                IsVip: false);
            LastMessageReceivedAt = _clock.UtcNow;
            _dispatcher.Post(() => MessageReceived?.Invoke(this, chatMessage));
        }
        if (nextContinuation is null) {
            // Live broadcast ended.
            TiLog.Info("[YouTubeChatService] poll returned no continuation; live broadcast ended");
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

### Reconnect arming

`ArmReconnect()` schedules a single one-shot timer (jittered ~60s ± 10s). On fire: clear `_videoId`, `_continuation`, `_innertubeApiKey`, transition `Reconnecting → Connecting`, restart from discovery (step 2 above). The cancellation token tied to `_cts` covers the whole lifecycle so `Disconnect()` / `Dispose()` cleanly abort whatever stage we're in.

## `YouTubeLiveChatScraper` (the load-bearing fragility)

Single-purpose, single-file. Has two operations: `ParseInitialPageAsync` (page-load → `INNERTUBE_API_KEY` + initial continuation) and `PollAsync` (continuation → messages + next continuation + timeoutMs). Returns plain DTOs from `YouTubeChatModels`.

The `INNERTUBE_API_KEY` regex and the message-renderer JSONPath are the *most likely things to break* when YouTube redesigns. Both live in this file behind small, named private methods. If YouTube ships a redesign:

- Bump the regex / JSON path.
- Re-run the canned-fixture tests (capture a live `live_chat` response into a `tests/Fixtures/youtube_live_chat_2026MMDD.json` file, swap the fixture).
- Most likely: 1–2 LOC change.

Worst case: YouTube changes the auth model (e.g., requires session cookies). At that point the whole approach becomes infeasible; document in notes/07 and consider deferring YT support indefinitely.

**Defensive parsing posture**: every field access in the JSON traversal MUST be null-safe. If a renderer is missing an expected sub-field, skip that one message (don't throw and tear down the loop). If the whole continuation block is missing from a response, that's the "broadcast ended" signal — return `(messages: [], nextContinuation: null, nextTimeoutMs: 0)`.

### Tests

Fixture-based. Capture 3–4 real `get_live_chat` responses (anonymised: replace channel IDs and message text with synthetic ones), check them into `tests/Fixtures/youtube_live_chat_*.json`. Each test:

- A "normal" response with 5 chat messages, all `liveChatTextMessageRenderer` — verify all 5 extracted with correct fields.
- A response with a `liveChatPaidMessageRenderer` (Super Chat) and `liveChatMembershipItemRenderer` (member join) mixed in — verify these are skipped without error.
- A response with a malformed message-renderer (missing `runs`) — verify it's skipped with a Debug log; other messages parsed normally.
- A response with no `continuationContents` — verify returns `(messages: [], nextContinuation: null, nextTimeoutMs: 0)` representing "broadcast ended".

## `YouTubeLiveBroadcastDiscovery` (the auto-discovery for D4)

```csharp
public interface IYouTubeLiveBroadcastDiscovery {
    Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct);
}
```

Implementation: HTTP GET `https://www.youtube.com/channel/{channelId}/live` with `HttpClientHandler.AllowAutoRedirect = true`. Inspect the final `HttpResponseMessage.RequestMessage.RequestUri` after redirects:

- If host is `www.youtube.com` AND path is `/watch` AND query contains `v=` → extract `videoId` from `v=` query param, return it.
- Otherwise → return `null`. Failure cases all collapse here: 404, redirect-to-channel-page (no live), redirect-loop, timeout, DNS failure (caught and converted to `null` with a Debug log).

### Tests

- Mock `IYouTubeHttp` to return a redirect chain ending at `/watch?v=ABCD1234` → assert `FindLiveVideoIdAsync` returns `"ABCD1234"`.
- Mock returning a redirect to `/channel/{ID}` (no live) → assert null.
- Mock throwing `HttpRequestException` → assert null (caught + logged Debug).
- Mock returning a 200 OK to the channel/live URL directly (no redirect) → assert null.

## `MultiChatService` (the aggregator)

### Shape

```csharp
namespace SlayTheStreamer2.Ti.Chat;

public sealed class MultiChatService : IChatService {
    private readonly IReadOnlyList<IChatService> _children;

    public MultiChatService(params IChatService[] children) {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0) throw new ArgumentException("MultiChatService requires ≥1 child", nameof(children));
        _children = children;
        foreach (var c in _children) {
            c.MessageReceived += OnChildMessageReceived;
            c.ConnectionStateChanged += OnChildConnectionStateChanged;
        }
    }

    public ChatConnectionState State => AggregateState();
    public bool IsConnected => _children.Any(c => c.IsConnected);
    public bool CanSend => _children.Any(c => c.CanSend);
    public DateTimeOffset? LastMessageReceivedAt =>
        _children.Select(c => c.LastMessageReceivedAt).Where(x => x.HasValue).Max();
    public Exception? LastError => _children.Select(c => c.LastError).FirstOrDefault(x => x is not null);

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "MultiChatService does not support a single-channel connect; " +
            "child services must be wired and connected by ModEntry directly.");

    public void Disconnect() { foreach (var c in _children) c.Disconnect(); }

    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
            CancellationToken ct = default) {
        // Fan-out only to children whose CanSend == true.
        // Returns Task.WhenAll of each child's send.
    }

    public void Dispose() { foreach (var c in _children) c.Dispose(); }
}
```

### Aggregate state rule

`AggregateState()` returns the "best-most" state across children:

| Priority | State | Meaning |
|---|---|---|
| 1 (best) | `ConnectedReadWrite` | Any child can read AND write — typically the Twitch child once joined |
| 2 | `ConnectedReadOnly` | At least one child is reading (no writing) — typically the YT-only-deployment or transient Twitch read-only |
| 3 | `Reconnecting` | At least one child is mid-reconnect; no child has reached Connected |
| 4 | `Connecting` | At least one child is initially connecting; no child has reached Connected or Reconnecting |
| 5 | `JoinFailed` | All children in JoinFailed |
| 6 | `AuthenticationFailed` | All children in AuthFailed |
| 7 | `Disconnected` | All children Disconnected |
| 8 (worst) | `Disposed` | All children Disposed |

Implementation: iterate the priority list above; return the first state matched by at least one child for priorities 1–4 (best-of), or matched by all children for priorities 5–8 (worst-of).

### Event forwarding

- `MessageReceived`: passthrough. Both children forward; the aggregator re-raises with `this` as `sender`.
- `ConnectionStateChanged`: passthrough only when the AGGREGATE state changes. Computes aggregate before and after the child transition; raises iff different. **Crucial for D8**: each ConnectionStateChanged event from a CHILD MUST also be reported individually to `ModEntry` so it can emit the per-platform startup/midsession receipts. Resolution: expose a `ChildConnectionStateChanged` event on `MultiChatService` in addition to the aggregate one. `ModEntry` subscribes to `ChildConnectionStateChanged` to fire receipts; nothing else uses it.

```csharp
public sealed record ChildConnectionStateChangedEventArgs(
    string ChildName,                        // "twitch" or "youtube"
    ChatConnectionChangedEventArgs Inner);

public event EventHandler<ChildConnectionStateChangedEventArgs>? ChildConnectionStateChanged;
```

(`ChildName` is constructor-tagged: `MultiChatService` takes `(string name, IChatService service)` pairs, or — simpler — a tagged child registration helper. Pin the exact constructor shape during implementation; the contract is just "the child name is available on the event".)

### Tests

~12 tests:
- One child connected RW + one Reconnecting → aggregate is `ConnectedReadWrite`.
- One child Connecting + one Disconnected → aggregate is `Connecting`.
- Both children Reconnecting → aggregate is `Reconnecting`.
- `MessageReceived` from child A forwards to subscriber on the multi.
- `SendMessageAsync` routes only to `CanSend == true` children.
- `SendMessageAsync` with zero CanSend children — returns `Task.FromException<InvalidOperationException>` (consistent with `TwitchIrcChatService`'s "Cannot send in state X").
- `Dispose` propagates to all children.
- `Disconnect` propagates to all children.
- `ChildConnectionStateChanged` fires for child A's transition without aggregate change.
- Single-child MultiChatService is functionally equivalent to bare child (`State`, `IsConnected`, etc.).
- Adding a child whose `CanSend` toggles dynamically — aggregate `CanSend` reflects correctly.
- Construction with empty array throws.

## `VoteSession` per-platform tally extension (per D10)

### What changes

```csharp
public sealed class VoteSession : IDisposable {
    // EXISTING (unchanged): merged-total tally; close-receipts use this.
    private readonly Dictionary<int, int> _tallies;

    // NEW: per-platform parallel tally; UI label uses this.
    private readonly Dictionary<(string Platform, int OptionIndex), int> _talliesByPlatform = new();

    // NEW: accessor for the UI label. Returns null when only one platform has been observed;
    // the label can then fall back to single-line rendering using the merged Tallies dict.
    public IReadOnlyDictionary<(string Platform, int OptionIndex), int>? TalliesByPlatform =>
        _observedPlatforms.Count > 1 ? new Dictionary<(string, int), int>(_talliesByPlatform) : null;

    private readonly HashSet<string> _observedPlatforms = new();
}
```

### Platform discrimination

```csharp
private static string PlatformOf(ChatMessage msg) =>
    msg.VoterKey.StartsWith("yt:", StringComparison.Ordinal) ? "youtube" : "twitch";
```

Single static helper, defined private inside `VoteSession`. **No public surface change to `ChatMessage`**. The "yt:" prefix is the contract per D9.

(Forward-compat: when a third platform is added, this becomes a small switch or a registered table. For v1 it's a two-way branch.)

### Where it plugs in

In `OnChatMessage(object?, ChatMessage)`, after the merged `_tallies[idx]++` and before the `TallyChanged` event raise:

```csharp
var platform = PlatformOf(msg);
_observedPlatforms.Add(platform);

// Decrement prior per-platform vote if the same voter changed their mind.
if (existing) {
    var priorKey = (platform, prior);
    if (_talliesByPlatform.TryGetValue(priorKey, out var priorCount) && priorCount > 0)
        _talliesByPlatform[priorKey] = priorCount - 1;
}
var nextKey = (platform, idx);
_talliesByPlatform[nextKey] = _talliesByPlatform.TryGetValue(nextKey, out var nextCount)
    ? nextCount + 1
    : 1;
```

Latest-wins is preserved per-voter; merged `_tallies` already enforces voter-uniqueness via `_votersByKey`. Since the per-platform side-dict is derived from the same voter event, the merged count and the sum of per-platform counts MUST always be equal — call this an invariant; assert it in tests.

### Snapshot

`VoteSnapshot` gains an optional `TalliesByPlatform` field. The default formatter (`EnglishReceipts.FormatClose` etc.) ignores it — close-receipts stay merged per D10. The UI label reads it.

### Tests

~6 tests in `VoteSessionPerPlatformTallyTests`:
- Votes from Twitch-only (no "yt:" UserIds) — `TalliesByPlatform` returns null; merged tally correct.
- Votes from YouTube-only (all "yt:" UserIds) — `TalliesByPlatform` returns null; merged tally correct.
- Mixed votes — `TalliesByPlatform` has both platforms, counts correct per platform; merged tally equals sum.
- Latest-wins from a YouTube voter changing their mind — per-platform tally decrements + increments correctly; merged tally net-zero on the change.
- Cross-platform same display name, different `VoterKey`s — counted as two votes (regression test for D1).
- Close-receipt receives a snapshot with merged tally only (verifies D10 receipt invariant).

## `VoteTallyLabel` (the UI rendering for D10)

### What changes

`VoteTallyLabel._Process` currently builds a single-block label. v1 splits when `_session.TalliesByPlatform` is non-null:

```csharp
public override void _Process(double delta) {
    if (!GodotObject.IsInstanceValid(this) || _session is null) return;
    if (_session.State is VoteSessionState.Closed
                          or VoteSessionState.Cancelled
                          or VoteSessionState.Disposed) return;

    var sb = new StringBuilder();
    var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
    sb.AppendLine($"Chat voting — {secondsLeft}s left");

    var perPlatform = _session.TalliesByPlatform;
    if (perPlatform is null) {
        // Single platform — original rendering path
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
    } else {
        // Multi-platform — split lines per platform per D10
        var platforms = perPlatform.Keys.Select(k => k.Platform).Distinct()
            .OrderBy(p => p, StringComparer.Ordinal);   // deterministic order: "twitch" before "youtube"
        foreach (var platform in platforms) {
            sb.Append($"{Capitalize(platform)}: ");
            for (int i = 0; i < _session.Options.Count; i++) {
                perPlatform.TryGetValue((platform, i), out var count);
                if (i > 0) sb.Append(", ");
                sb.Append($"{i}={count}");
            }
            sb.AppendLine();
        }
    }
    Text = sb.ToString();
}

private static string Capitalize(string s) =>
    string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
```

The single-platform path is the existing rendering, preserved verbatim. The multi-platform path is the new one.

**Visual-combining is explicitly deferred** per D10 — no colour-per-platform, no aggregate bar, no merged final-line. Two text lines, period. v0.2+ may iterate.

### Anchors

Unchanged from B.1; existing AnchorLeft/Top/Right/Bottom values still apply. The label grows downward when multi-platform; if it overlaps important UI elements during operator validation, polish in v0.2.

## `ModSettings` extension

### JSON shape

```jsonc
{
  "schemaVersion": 1,
  "channel": "frostprime",
  "username": "slay_the_streamer",
  "oauthToken": "...",
  "cardSkipsPerAct": 1,

  // NEW v1 — optional; absent OR null OR empty string means YouTube disabled.
  // Stable YouTube channel ID. NOT the @handle; the underlying channel ID
  // (looks like "UCabc123...", visible in the YouTube channel-page URL or
  // YouTube Studio's "Channel ID" setting). Auto-discovery polls
  // youtube.com/channel/{this}/live every ~60s when no broadcast active.
  "youtubeChannelId": "UCabc123def456ghi789"
}
```

### `ModSettings.Load` behaviour (per D6)

- Field is optional. Absent OR JSON `null` → `youtubeChannelId = null` in the parsed `SettingsResult.Success` (YT disabled). **NOT a `Malformed` condition** — additive optional field, backward-compatible with v0.1 settings files. No warnings list entry.
- Empty string → treated as missing (clamp to null, `Success` with no warning). Common no-op shape — distinct from whitespace-only, which is more likely a paste mistake.
- Whitespace-only string (`"   "`, `"\t\n"`, etc.) → `Malformed`. Indicates a paste-error or typo, not a benign disable. Same pattern as `oauthToken`'s shape-validation in B.1.
- Contains control characters (any `char.IsControl(c)` true after trimming) → `Malformed`. Same rationale; never legitimate in a real YouTube channel ID.
- Otherwise non-empty → preserved as-is. **No deeper format validation in `Load`** (e.g., we don't check the `"UC..."` prefix or length; YouTube channel IDs have evolved over time). The channel-page GET will fail gracefully per D7 if the ID is bogus — streamer sees "no live broadcast found" in logs, in-game label stays single-platform until fixed.
- No `schemaVersion` bump.

### Tests

5 additional tests in `ModSettingsTests.cs`:
- `youtubeChannelId` absent — `Success` with `youtubeChannelId == null`, no warning.
- `youtubeChannelId` explicit JSON `null` — same as absent.
- `youtubeChannelId` empty string — clamped to null, `Success` with no warning.
- `youtubeChannelId` whitespace-only — `Malformed`.
- `youtubeChannelId` with embedded control char — `Malformed`.
- `youtubeChannelId` valid non-empty — preserved.

## `ModEntry` wiring

### What changes

```csharp
// EXISTING shape (illustrative):
//   var twitch = new TwitchIrcChatService(...);
//   _ = twitch.ConnectAsync(settings.Channel, new ChatCredentials(...));
//   var voter = new VoteCoordinator(twitch, ...);

// NEW v1:
var twitch = new TwitchIrcChatService(...);
_ = twitch.ConnectAsync(settings.Channel, new ChatCredentials(...));

IChatService chat;
if (!string.IsNullOrWhiteSpace(settings.YoutubeChannelId)) {
    var youtube = new YouTubeChatService(...);
    _ = youtube.ConnectAsync(settings.YoutubeChannelId);
    chat = new MultiChatService(
        ("twitch", twitch),
        ("youtube", youtube));
} else {
    chat = new MultiChatService(("twitch", twitch));   // single-child; functionally a passthrough
}

// Subscribe to per-child connection events for D8 startup-receipt extension.
if (chat is MultiChatService multi) {
    multi.ChildConnectionStateChanged += OnChildConnectionStateChanged;
}

var voter = new VoteCoordinator(chat, ...);
Voter.Default = voter;
```

### Startup receipt extension (per D8)

`ModEntry` already fires a `slay-the-streamer-2 v… connected` receipt when Twitch joins. v1 extends this:

```
slay-the-streamer-2 v0.2 connected (Twitch). YouTube: no live broadcast found, retrying.
```

Format: the **base receipt fires on the FIRST `ChildConnectionStateChanged` for Twitch → ConnectedReadWrite** (existing behaviour). When that fires, inspect the YT child's current state synchronously:

- YT not configured (`youtubeChannelId` was null) → suffix omitted entirely (single-platform message).
- YT in `Connecting` or `Reconnecting` → suffix `. YouTube: no live broadcast found, retrying.`
- YT in `ConnectedReadOnly` (rare — could happen if YT connects faster than Twitch) → suffix `. YouTube: tracking chat from <channelId>.`

Mid-session YT state transitions ALSO fire a receipt, but only on the specific transitions worth reporting:

- `Reconnecting/Connecting → ConnectedReadOnly` → `YouTube connected: tracking chat from <channelId>` (priority Normal)
- `ConnectedReadOnly → Reconnecting` → `YouTube disconnected: live broadcast ended, will resume when next broadcast starts` (priority Normal)

**Flap suppression**: if the YT state oscillates (e.g., a flaky network produces ConnectedReadOnly → Reconnecting → ConnectedReadOnly within a few seconds), only fire receipts on stable transitions. Pragmatic implementation: debounce per-platform state-change receipts to one per 30s. Suppress receipts that aren't a "real" transition (e.g., Reconnecting → Connecting → Reconnecting still counts as one Reconnecting episode for receipt purposes).

### Disposal

`ModEntry`'s teardown disposes the `MultiChatService`, which propagates to children. No code change needed beyond replacing `twitch.Dispose()` with `chat.Dispose()` at the relevant teardown site.

## Failure modes & degradation

| # | Failure mode | Behaviour |
|---|---|---|
| 1 | `youtubeChannelId` absent OR JSON `null` OR empty string in settings | `MultiChatService` constructed with only Twitch child; functionally identical to v0.1. No YT codepath exercised. Per D6 this is `Success`, not `Malformed`. |
| 1b | `youtubeChannelId` whitespace-only OR contains control chars | `ModSettings.Load` returns `Malformed` per D6. Entire mod degrades to vanilla (existing B.1/B.2.1 malformed-settings handling). `ShouldEnforceSkipGate()` returns false. Streamer sees the existing "settings malformed" log line; needs to fix the setting and restart. |
| 2 | `youtubeChannelId` present but channel doesn't exist (404 on `youtube.com/channel/{ID}/live`) | Discovery returns null → `Reconnecting`. Per D7, retry every 60s indefinitely. Warn-log line per retry. Twitch unaffected. **No JoinFailed transition**: we deliberately don't distinguish "permanent" from "transient" 404. Documented limit. |
| 3 | `youtubeChannelId` present, channel exists, but no current live broadcast | Discovery returns null (final URL is channel page, not `/watch?v=`) → `Reconnecting`. Per D7. When streamer goes live mid-session, next retry succeeds, YT auto-connects, D8 mid-session receipt fires. |
| 4 | YouTube ratelimits us (HTTP 429 on `live_chat` page or `get_live_chat` POST) | Single Warn log; transition to `Reconnecting`; retry in ~60s. If ratelimit persists, retries continue indefinitely with one log per attempt. Twitch unaffected. **No backoff** — D7's fixed 60s cadence is the documented behaviour. Future polish (v0.2+): exponential backoff for 429 specifically. |
| 5 | YouTube changes the `live_chat` page HTML shape (regex for `INNERTUBE_API_KEY` no longer matches) | `YouTubeLiveChatScraper.ParseInitialPageAsync` returns null/throws → transition to `Reconnecting`. Repeats indefinitely. Operator/maintainer needs to ship a regex update. Documented as the load-bearing fragility; isolated in one file. |
| 6 | YouTube changes the `get_live_chat` JSON shape | Same as #5, but in the poll loop. `YouTubeLiveChatScraper.PollAsync` returns malformed → transition to `Reconnecting`. Same recovery path (ship a parser update). |
| 7 | Network error during discovery / initial page / poll | `HttpRequestException` caught at each call site; transition to `Reconnecting`. Per D7 cadence. Twitch unaffected. |
| 8 | Network partition for the entire process (both Twitch and YT down) | Each service handles independently. Twitch goes to its own Reconnecting (with B.1's exponential backoff). YT goes to its own 60s-cadence Reconnecting. Aggregate state is `Reconnecting`. `ShouldEnforceSkipGate()` from B.2.1 is unaffected — temp disconnect is not a terminal state. |
| 9 | YT live broadcast ends mid-vote | Steady-state poll returns empty continuation → `ConnectedReadOnly → Reconnecting`. Any votes already received during the vote window are preserved in `_tallies` and `_talliesByPlatform`. Close-receipt fires normally with the merged tally. `VoteTallyLabel` continues showing the YouTube line for the rest of the vote window (with stale-but-final values) — explicit UX choice for v1; v0.2 may collapse to single-platform if YT disconnects mid-vote. |
| 10 | YT scraper returns 0 messages every poll (e.g., empty chat) | No-op. `MessageReceived` not raised; periodic poll continues per `timeoutMs`. No state change. |
| 11 | YT scraper returns malformed message (missing fields) | Single message skipped with Debug log; loop continues. No state change. |
| 12 | `MultiChatService.SendMessageAsync` called with no CanSend children | Returns `Task.FromException<InvalidOperationException>`. Consistent with `TwitchIrcChatService`'s "Cannot send in state X". Receipt code in `VoteSession.SendReceipt` already catches via continuation; logs Error. |
| 13 | `YouTubeChatService.SendMessageAsync` called directly (bypassing the multi) | Returns `Task.FromException<NotSupportedException>("YouTubeChatService is read-only (D3).")`. No live caller in v1 — defensive. |
| 14 | `MultiChatService` constructed with empty array | `ArgumentException` thrown from constructor. ModEntry MUST always pass at least the Twitch child. |
| 15 | Same human votes on both Twitch and YouTube (D1 behaviour) | Two votes recorded — once under Twitch `UserId`, once under `"yt:{ytChannelId}"`. Merged `_tallies` reflects both. Per-platform tally reflects them on separate platform rows. Documented behaviour per D1. |
| 16 | Vote winner is YouTube-side (e.g., 3 YouTube votes vs 1 Twitch) | Standard winner selection; close-receipt fires on Twitch with merged tally (`Chat chose Bash`). No mention of platform breakdown in the receipt per D10. |
| 17 | YT `authorChannelId` is missing from a message (defensive) | Fall back to `Login = "yt:anon-" + randomGuidSuffix`. UserId = same. Effectively a unique per-message voter — they can vote once and never again. Rare; documented. |
| 18 | YT message Text is empty or only whitespace | Treated like any chat message: `VoteSession`'s regex won't match → ignored at the vote-tally layer. No state issue. |
| 19 | `MultiChatService.ChildConnectionStateChanged` consumer (ModEntry's receipt sender) throws | Caught in the event-raise try/catch; logged Error. Other children's events continue firing. |
| 20 | Settings file changes during runtime (operator edits while game running) | Not supported; settings loaded once at mod init. Standard v0.1 behaviour. Documented limit. |
| 21 | Twitch terminal-failure (AuthFailed / JoinFailed) while YT is connected | `ShouldEnforceSkipGate()` from B.2.1 returns false → card-skip gate degrades to vanilla. YT votes still flow into `VoteSession` but no chat receipts fire (D3 + nothing to send through). In-game tally label shows YouTube-only single-platform rendering. **Edge: gate is disabled but votes are still tallied** — chat sees in-game label only. Acceptable for v1; document. |

## Acceptance gate

7-step operator-validation gate. Each step is a manual playthrough by the streamer (or Surfinite as proxy). Match the v4 spec's shape: Step 0 is the regression, Steps 1–N exercise new behaviour, Step N+1 covers failure-mode degradation.

- **Step 0 — Vanilla-regression (Twitch-only, new code path).** Settings file with NO `youtubeChannelId` (or with `youtubeChannelId: null` / `""`). Mod loads. `MultiChatService` wraps only the Twitch child. Run a Neow vote AND a card-reward vote AND verify a skip blocked via Decision 21 path — all behave identically to v0.1. **Specifically verify**: in-game tally label uses single-platform rendering (no `Twitch:` prefix line, just `#0 / #1 / #2` like before). Confirms the new aggregator code is functionally identical to the old direct-Twitch path in the single-child case.

- **Step 1 — YT-only smoke.** Settings file with a valid `youtubeChannelId` AND a deliberately-broken Twitch config (e.g., bad oauthToken so Twitch lands in `AuthenticationFailed`). Mod loads. YT child connects to a real live broadcast. **Verify**: YT messages flow into `MessageReceived` and reach `VoteSession`. In-game tally label renders YouTube line for any vote (no Twitch row since no Twitch messages arrive). Card-skip gate is degraded (per B.2.1 Decision 21 amendment — terminal Twitch state disables the gate). No chat receipts fire on either platform (per D3 — Twitch is dead and YT is read-only). YT votes still control merged-tally winner selection. **This is the worst-of-both-worlds mode**: documented but supported.

- **Step 2 — Dual-platform happy path (3 runs).** Settings file with both Twitch and `youtubeChannelId` valid. Real live broadcast active on YT. Mod loads.
  - **Run 1**: Neow vote → chat votes on both platforms → merged tally selects winner → close-receipt fires on Twitch with `Chat chose <option>`. In-game tally label shows split lines: `Twitch: 0=N, 1=M, 2=K` and `YouTube: 0=A, 1=B, 2=C`. No YT receipt fires.
  - **Run 2**: Card-reward vote → same flow, same split rendering. Skip-gate still functions per B.2.1.
  - **Run 3**: Card-reward skip used (parent-Skip after sub-screen back-out per Model 2). Skip receipt fires on Twitch only. Counter label updates.

- **Step 3 — YT failure modes (per D7).**
  - **3a — No live broadcast at startup**: YT channel ID set but not live. Mod loads. Startup receipt on Twitch reads `... YouTube: no live broadcast found, retrying.` In-game tally label shows single-platform (Twitch only) during votes. Logs show `[YouTubeChatService] no live broadcast found for {channelId}; retrying in ~60s` once per minute. **Then** streamer starts a YouTube live broadcast mid-session. Within 60s, YT auto-discovers and connects. Mid-session receipt fires on Twitch: `YouTube connected: tracking chat from <channelId>`. Next vote shows split per-platform rendering.
  - **3b — YT live broadcast ends mid-session**: Steady state, both platforms connected. Streamer ends the YouTube broadcast (but not the Twitch one). Within 1–2 polls, YT enters `Reconnecting`. Twitch receipt fires: `YouTube disconnected: live broadcast ended, will resume when next broadcast starts`. Subsequent votes show single-platform rendering until YT reconnects.
  - **3c — Channel ID typo**: Set `youtubeChannelId` to a deliberately invalid value (e.g., `UCnotarealchannel`). Mod loads. Startup receipt reads `... YouTube: no live broadcast found, retrying.` Same log behaviour as 3a, but the retry NEVER succeeds. Confirms D7's "we don't distinguish permanent from transient" decision works in practice — no spam, one Warn log per minute, no impact on Twitch.
  - **3d — Network kill mid-YT-poll**: Simulate by disabling network briefly. YT scraper poll throws → `Reconnecting`. Twitch receipt fires `YouTube disconnected: live broadcast ended, ...` (slightly wrong wording for the network case — acceptable v1 limit; the receipt is generic). When network restores, YT auto-reconnects within 60s.
  - **3e — Scraper-shape regression simulation**: Mock the scraper to return a malformed initial-page response. YT lands in `Reconnecting` and stays there. Confirms isolation — Twitch and gate unaffected.

- **Step 4 — Split tally label rendering correctness.** Dual-platform vote in flight. Verify label updates in real time as votes flow in from both platforms. Specifically: a vote arrives from YouTube → YouTube line updates ONLY; Twitch line unchanged. A latest-wins change from a Twitch voter → Twitch line shows decrement-then-increment; YouTube line unchanged. Merged-tally close-receipt on Twitch matches the SUM of platform tallies. Step 5 verifies the inverse: cross-platform same-display-name double-counting.

- **Step 5 — Cross-platform double-count (per D1).** Streamer (or test viewer) sends the same vote command (e.g., `#1`) from the same human's Twitch AND YouTube accounts. Verify: two distinct vote increments, one on each platform line. Merged count is 2. Documented behaviour per D1.

- **Step 6 — Twitch-only-deployment-mode + D6 settings parsing.** Three sub-cases, all of which must behave correctly:
  - 6a — Settings with `youtubeChannelId: null` (explicit JSON null, not absent). Mod loads as `Success`; no YT receipts, no YT log noise, no YT codepath entered. Identical behaviour to absent.
  - 6b — Settings with `youtubeChannelId: ""` (empty string). Same as 6a — `Success`, YT disabled, no codepath entered.
  - 6c — Settings with `youtubeChannelId: "   "` (whitespace-only) OR `youtubeChannelId: "valid_idwith_bell"` (embedded control char). `ModSettings.Load` returns `Malformed`. Entire mod degrades to vanilla; existing malformed-settings logs fire. Confirms D6's stricter validation distinguishes "I left this off" from "I pasted something wrong".

- **Step 7 — Receipt flap suppression.** Use a deliberately-flaky setup (e.g., toggle the YT broadcast on/off rapidly OR simulate via test hook). Verify that YT state changes within a 30s window emit AT MOST ONE Twitch receipt per stable state. No-mid-session-receipt-storm.

## Open questions

Carried forward from notes/07 (no design changes in this spec; just spec authorship can't answer them):

1. **Is YouTube parallel a hard requirement, or nice-to-have for FrostPrime?** Affects whether this slice ships before B.2.2 / B.3 or after. Need FrostPrime input.
2. **What's the expected YouTube chat volume?** The `pytchat`-class scrapers reportedly lag on >1000 msg/min; we'd need to benchmark before committing if FrostPrime's YT audience is that big. Mitigation if it is: rate-limit `MessageReceived` propagation in the YT child (sample 1-of-N), or extend the vote-window. Either way, we'd want to measure before redesigning.
3. **Members-only or public chat?** Determined non-goal per D5; confirm with FrostPrime that members-only-disabled-during-streams is acceptable.
4. **Cross-platform vote-counting policy preference (D1)?** Per-platform double-count is the v1 decision; FrostPrime may want a different policy (e.g., display-name-match heuristic). Hooks for it are in place via the `PlatformOf` helper + per-platform tally side-dict.
5. **Does the existing chat-overlay he uses for YouTube (the "displays both chats on screen" tool a community member mentioned) constrain our integration shape?** If the overlay is reading from the same `youtubei` endpoint, we may be doubling load on YouTube's side; benchmarking + maybe coordinate.
6. **Is FrostPrime open to having Surfinite continue this work after the tournament rather than commissioning someone else?** Per a community member: "he intended on paying someone". Coordination question, not a spec question.

## LOC estimate + risk areas

**Total**: ~815 LOC source + ~360 LOC tests. Within notes/07's 1–2-week effort estimate.

| Component | Source LOC | Tests LOC | Risk band |
|---|---|---|---|
| `YouTubeLiveChatScraper` | ~180 | ~120 | **HIGH** — load-bearing fragility per YouTube redesigns. Isolated in one file; one-LOC fixes when broken. Fixture-based tests enable fast regression after a YouTube change. |
| `YouTubeLiveBroadcastDiscovery` | ~80 | ~50 | LOW-MEDIUM — second scraped endpoint; smaller surface than the chat scraper but same redesign-fragility class. |
| `YouTubeChatService` (state machine + poll loop) | ~250 | ~80 | MEDIUM — wires the two scrapers into the IChatService surface. Risks: incorrect state transitions, leaked CancellationTokenSource, missing dispatcher.Post on message raise. Most failure modes surface in operator validation Step 3. |
| `MultiChatService` | ~150 | ~60 | LOW — pure logic over child services. Aggregate-state rule is the trickiest piece; well-covered by unit tests. |
| `VoteSession` per-platform tally | ~30 | ~30 | LOW — additive side-dict, no behavioural change to existing close-receipts. Invariant test (per-platform sum equals merged) catches drift. |
| `VoteTallyLabel` split rendering | ~40 | (operator-validated only) | LOW — UI-only, single _Process method. Operator validation Step 4 verifies. |
| `ModSettings` extension | ~15 | ~10 | LOW — single optional field. |
| `ModEntry` wiring + D8 receipts | ~70 | (operator-validated only) | LOW-MEDIUM — flap-suppression and per-child-event subscription are the only spots with subtle state. Operator validation Step 7 verifies. |

**Risk concentration**: ~30% of total source LOC sits in scraper code, which carries ~70% of the redesign-fragility risk. The mitigation is structural — every YouTube scraper concern lives in `Ti/Chat/YouTubeChat/`; nothing outside that namespace cares about the JSON shape or the regex. If/when YouTube ships a redesign:

1. Pull a fresh `live_chat` response into the test fixtures.
2. Update the regex / JSON traversal in `YouTubeLiveChatScraper`.
3. Re-run fixture-tests to confirm.

This is the same maintenance model `pytchat` / `chat-downloader` / `youtube-chat` use. The notes/07 writeup estimates a 6–12 month cadence for YouTube changes; expect to ship a small `Ti/Chat/YouTubeChat/` patch on that cadence post-v1.

## Cross-references

- [`notes/07-youtube-chat-feasibility.md`](../../../notes/07-youtube-chat-feasibility.md) — feasibility writeup + authoritative Decisions log (D1–D5, D7–D10).
- [`notes/06-followups-and-deferred.md`](../../../notes/06-followups-and-deferred.md) — v0.2+ list entry pointing at notes/07; B.2.1 design pivot context.
- [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](./2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md) — spec-shape template; Decision 21 (chat-state activation gate) is unaffected by this slice but explicitly preserved.
- [`docs/superpowers/specs/2026-05-09-plan-b-1-vertical-slice-design-v3.md`](./2026-05-09-plan-b-1-vertical-slice-design-v3.md) — B.1 spec where `TwitchIrcChatService` and `ChatMessage` were specified.
- [`src/Ti/Chat/IChatService.cs`](../../../src/Ti/Chat/IChatService.cs) — the interface YouTube must satisfy.
- [`src/Ti/Chat/ChatMessage.cs`](../../../src/Ti/Chat/ChatMessage.cs) — `VoterKey` is the dedup primitive; "yt:" prefixing is the D9 contract.
- [`src/Ti/Chat/TwitchIrcChatService.cs`](../../../src/Ti/Chat/TwitchIrcChatService.cs) — reference implementation; the state-machine + reconnect cadence in this spec mirrors its shape.
- [`src/Ti/Voting/VoteSession.cs`](../../../src/Ti/Voting/VoteSession.cs) — destination for the parallel per-platform tally side-dict.
- [`src/Ti/Voting/VoteCoordinator.cs`](../../../src/Ti/Voting/VoteCoordinator.cs) — unchanged; consumes whatever `IChatService` ModEntry hands it.
- [`src/Ti/Ui/VoteTallyLabel.cs`](../../../src/Ti/Ui/VoteTallyLabel.cs) — destination for split rendering.
- Memory: [`youtube_chat_scraping_landscape`](../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/youtube_chat_scraping_landscape.md) — cross-project notes on the scraping approach.

---

**Pre-meta-review status**: v1 draft. Decisions D1–D5, D7–D10 are inlined and rationalised. Architecture, contracts, failure modes, acceptance gate, and LOC estimate are all populated. Open questions are FrostPrime-coordination questions, not design questions. **Next step (Surfinite's choice)**: (a) invoke `superpowers:document-context` + `meta-review` for a multi-reviewer pass like B.1 / B.2.1 had; or (b) move directly to `superpowers:writing-plans` to generate an implementation plan.
