# TI layer design — IRC + vote engine boundary (v2)

**Date**: 2026-05-08 (v2: 2026-05-08, post-meta-review; v2.1: 2026-05-08, optional enhancements 1/6/9/10/11/12/13/14/15 folded in)
**Status**: Draft v2.1 — must-do + should-do changes from external review applied; 9 of 15 optional enhancements folded in; remaining 6 listed at the end for future consideration.
**Scope**: the Twitch-integration layer of `slay-the-streamer-2`. Defines the boundary between the reusable chat-IO machinery and the StS2-specific vote bindings.
**Predecessor**: [`2026-05-08-ti-layer-design.md`](./2026-05-08-ti-layer-design.md). See [`META-REVIEW-2026-05-08-ti-layer-design.md`](./META-REVIEW-2026-05-08-ti-layer-design.md) for the rationale behind every `<!-- CHANGED -->` mark below.

> **Namespace note**: this document uses `Ti` (short for "Twitch Integration", a deliberate echo of robojumper's StS1 `ststwitch` base mod) throughout. The shorthand will be expanded in a one-line comment at the top of every `Ti/*.cs` file. <!-- CHANGED: Document the namespace expansion to defuse "looks like a typo" feedback — Reviewers 1, 3, 4, 5 -->

## Goals

1. **One-shot vote API** that StS2-side glue code can call to ask chat a multiple-choice question and get an answer.
2. **Modular within StS2**: a future StS2 mod that wants chat-driven interactions but not voting (e.g. "help the streamer", "every 5 min chat picks an outfit") can build on the same `ChatService` without forking IRC code.
3. **Same repo, clean seam**: ship as one mod for v0.1; design boundaries so a later "lift the TI layer into a base mod" change is a file move + small registration shim, not a refactor.
4. **Read + write IRC**: viewers need in-chat confirmation that votes are landing, since the on-screen vote bar is on a 5–30 s video delay relative to real-time chat.
5. **Multiplayer-aware API shape**: StS2 supports co-op. Two players each running this mod with their own Twitch channels needs two coordinator instances. The API does not paint into a single-streamer corner. <!-- CHANGED: New goal added — instance-per-channel architecture; static singletons would block co-op — Reviewer 6 -->

## Non-goals

- Cross-game reuse. The layer can use Godot and `sts2.dll` types where useful; it's mod-agnostic, not game-agnostic.
- Twitch Extension overlays / PubSub / channel-points integration. Read-only-ish IRC plus periodic outgoing tally announcements is the whole I/O surface.
- A general `!command` router. YAGNI for v0.1; the upper-tier `VoteSession` is the only built-in consumer of `ChatService`.
- A streamer-side configuration UI. Out of scope for this design pass; covered separately when v0.1 settings are designed.
- Subscriber/mod/VIP-only voting filters in v0.1 — but the data is exposed in `ChatMessage` so future filters are a where-clause not a re-architecture. <!-- CHANGED: Added — exposing badge tags is essentially free with CAP REQ tags — Reviewer 6 -->

## Decisions (from session 2 brainstorming + meta-review)

| # | Decision | Rationale |
|---|---|---|
| 1 | Two-tier API: `ChatService` (lower) + `VoteSession` (upper) + optional UI. <!-- CHANGED: Coordinator added between them — see #9 below — Reviewers 2,3,4,5,6 --> | Cleanest extraction unit at `ChatService`; YAGNI says no `ChatCommandRouter` middle tier yet. |
| 2 | Strictly one vote open at a time **per coordinator instance**. <!-- CHANGED: "per coordinator" added; was "globally" — multiplayer requires per-streamer enforcement — Reviewer 6 --> | Matches StS's one-screen-at-a-time flow; `VoteCoordinator.Start` throws if a session is open on this coordinator. Co-op multiplayer with two `Ti` instances is supported because each coordinator enforces its own invariant. |
| 3 | Receipts: open + periodic tally + close | Closes the streamer-vs-viewer lag gap; default tally cadence 7 s. Safe under Twitch's send-queue rate limit (now enforced server-side by `TwitchIrcChatService` rather than relying on consumer discipline — see #11). |
| 4 | One vote per user, latest `#N` wins | Lets viewers correct typos and react to the running tally. (Note: the original StS1 base mod was actually first-vote-wins; we deliberately diverge for better UX.) |
| 5 | Handcrafted minimal Twitch IRC client. <!-- CHANGED: LOC estimate revised — Reviewers 4, 5 --> | Cleanest extraction — the day someone lifts `ChatService` to a base mod, no extra NuGet deps come along. Realistic size is 500–700 LOC of source plus ~200 LOC of tests. The "200 LOC" estimate in the original spec was for the happy-path echo case; the realistic scope (TLS, CAP, tag escaping, RECONNECT, auth-failure NOTICE, send queue with rate limiter, reconnect-with-rejoin, justinfan anonymous mode) is larger. The choice still stands. **Plan B**: if the handcrafted client proves problematic in implementation, vendor a single-file Twitch IRC client into `Ti/Chat/Vendor/` (preserving the zero-NuGet-dep extraction property). |
| 6 | Tie-break: uniform random across tied options | Simple, fair, "chat decides" stays intact. Receipt announces the random pick honestly. |
| 7 | No-voter edge: pick uniformly at random across all options | Game-side never gets `null`; receipts say so explicitly. |
| 8 | Vote-command parser: hash optional, anchored to start of message, terminated by space or end-of-message. <!-- CHANGED: Made configurable via VoteParsingPolicy; default also accepts !N — Reviewers 1, 2, 3, 5 --> | Default regex `^[#!]?(\d+)(?:\s\|$)` (configurable via `VoteParsingPolicy`). Accepts `#0`, `0`, `!0`, etc. The `^` anchor and trailing-boundary keep ordinals (`1st`, `2nd`), decimals (`1.5`), and inline numbers (`I have 3 cards`) from being miscounted. Default is permissive (accepts both `#` and `!`) since real Twitch chat is conditioned on `!command` from other bots. |
| 9 | **`VoteCoordinator` is instance-based; `Voter` is a thin static facade for ergonomic Harmony-patch call sites only**. <!-- CHANGED: New decision — was static-only; reviewer consensus on multiplayer + testability + extraction — Reviewers 2, 3, 4, 5, 6 --> | Static singletons block parallel tests, multiplayer, and clean extraction. Each `ModEntry.Initialize` creates one `VoteCoordinator` per streamer-channel. The static `Voter.Start(...)` exists as a Harmony-call-site convenience and delegates to the coordinator configured in `ModEntry`. |
| 10 | **`IMainThreadDispatcher` interface decouples `Ti/Internal/` from Godot**. <!-- CHANGED: New decision — fixes dependency-rule contradiction — Reviewers 1, 2, 3, 4, 5 --> | The original spec violated its own rule ("Godot in `Ti/Ui/*` only") because `GameThreadDispatcher` used `Godot.CallDeferred` from `Ti/Internal/`. Fix: define the dispatcher contract as an interface in `Ti/Internal/`; the Godot impl lives in `Ti/Godot/` (a new sub-namespace clearly marked as a Godot binding); tests use an `ImmediateDispatcher`. |
| 11 | **Outgoing send queue + rate limiter inside `TwitchIrcChatService`**. <!-- CHANGED: New decision — was missing entirely; consumer-discipline rate limiting was the brittle approach — Reviewers 3, 4, 5, 6 --> | Token-bucket rate limiter: 90 msg/30 s ceiling (under Twitch's 100 limit; configurable down to 18/30 s for non-broadcaster accounts). Priority queue: `Close > Open > Periodic`. Stale periodic tallies coalesce when backed up. Never relies on consumer discipline. |
| 12 | **`ITimerScheduler` for fake-advanceable timers**. <!-- CHANGED: New decision — `FakeClock` alone doesn't control `System.Threading.Timer` — Reviewers 3, 4, 5 --> | `VoteSession` schedules its close timer through `ITimerScheduler`, not `System.Threading.Timer` directly. `SystemTimerScheduler` (default) wraps real timers; `FakeTimerScheduler` (tests) advances deterministically alongside `FakeClock`. |
| 13 | **`AuthenticationFailed` is a terminal connection state — no retry on bad oauth**. <!-- CHANGED: Was an "open item" — promoted to a hard requirement — Reviewers 1, 3, 4, 5 --> | The infinite-retry loop in v1 would spam connection attempts forever against a wrong oauth token. Detect Twitch's `NOTICE * :Login authentication failed` / `:Error logging in`; transition to terminal `AuthenticationFailed`; emit final `ConnectionStateChanged`; stop retrying. Network-level transport failures still retry indefinitely with capped backoff (now with jitter). |
| 14 | **0-indexed options** (chat types `#0, #1, #2…`). <!-- CHANGED: Was 1-indexed; now matches the original StS1 mod and removes the chat-vs-list off-by-one — User input + matches Tempus's mod --> | Chat-index = list-index = `WinnerIndex`. No mental subtraction in `Game/DecisionVotes/*` patches when `WinnerIndex` is used to index the option list. Receipts say `"Type 0, 1 or 2"` — slightly unfamiliar to users new to the mod, but matches the original. Validation: max 10 options (`#0`–`#9`). |

## Architecture

```
src/
├── Ti/                          [future-extractable; no StS2-specific types]
│   ├── Chat/
│   │   ├── IChatService.cs
│   │   ├── ChatMessage.cs
│   │   ├── ChatCredentials.cs
│   │   ├── ChatConnectionState.cs            <!-- CHANGED: New enum — replaces bool IsConnected — Reviewers 3,4,5 -->
│   │   ├── TwitchIrcChatService.cs           handcrafted IRC implementation
│   │   ├── FakeChatService.cs                in-memory test/dev implementation
│   │   └── Internal/
│   │       ├── TwitchIrcParser.cs            pure-function parser (incl. tag escaping)
│   │       ├── OutgoingMessageQueue.cs       <!-- CHANGED: New — token-bucket rate limiter + priority queue — R3,R4,R5,R6 -->
│   │       └── ConnectionRetryPolicy.cs      with jitter
│   ├── Voting/
│   │   ├── VoteCoordinator.cs                <!-- CHANGED: New — instance-based session owner — R2,R3,R4,R5,R6 -->
│   │   ├── Voter.cs                          static facade over a process-default VoteCoordinator
│   │   ├── VoteSession.cs
│   │   ├── VoteOption.cs
│   │   ├── VoteSessionState.cs               <!-- CHANGED: New enum — Open / Closing / Closed / Cancelled / Disposed — R3,R4,R5 -->
│   │   ├── VoteReceiptPolicy.cs
│   │   ├── VoteParsingPolicy.cs              <!-- CHANGED: New — toggles !N acceptance — R1,R3,R5 -->
│   │   ├── IVoteReceiptFormatter.cs          <!-- CHANGED: New — i18n + testability — R3,R4,R5 -->
│   │   └── DefaultEnglishReceiptFormatter.cs
│   ├── Ui/                                   optional, Godot-dependent
│   │   └── VoteOverlayControl.cs             redraws driven by _Process — R6
│   ├── Internal/
│   │   ├── IMainThreadDispatcher.cs          <!-- CHANGED: Interface introduced — R1,R2,R4,R5 -->
│   │   ├── IClock.cs + SystemClock + FakeClock
│   │   ├── ITimerScheduler.cs + SystemTimerScheduler + FakeTimerScheduler   <!-- CHANGED: New — R3,R4,R5 -->
│   │   ├── ITiLogger.cs                      <!-- CHANGED: New — decouples from MegaCrit Log — R2,R4,R5 -->
│   │   └── DefaultTiLogger.cs                wraps MegaCrit.Sts2.Core.Logging.Log
│   └── Godot/                                <!-- CHANGED: New sub-namespace — clearly marked Godot binding — R1,R2,R4,R5 -->
│       ├── GodotMainThreadDispatcher.cs      ConcurrentQueue drained from _Process
│       └── DispatcherAutoload.cs             autoload Node that owns the dispatcher
├── Game/                                     [StS2-specific glue; NOT extractable]
│   ├── DecisionVotes/                        one Harmony patch per voted decision
│   ├── Models/                               [TBD if sealed-deck uses AbstractModel]
│   └── Bootstrap/
│       └── ModServices.cs                    locator: VoteCoordinator, IChatService, ITiLogger, IMainThreadDispatcher
└── ModEntry.cs                               [ModInitializer], wires everything
```

**Namespaces**: `SlayTheStreamer2.Ti.*` for the extractable layer; `SlayTheStreamer2.Game.*` for StS2-specific glue. The line between them is the lift point.

**Allowed dependencies** (corrected):  <!-- CHANGED: Dependency rule clarified to fix the leak — R1,R2,R3,R4,R5 -->
- `Ti/Chat/`, `Ti/Voting/`, `Ti/Internal/` may reference: BCL only. No Godot, no `sts2.dll`.
- `Ti/Ui/` may reference: BCL + Godot.
- `Ti/Godot/` may reference: BCL + Godot. Houses every type that calls Godot directly outside of UI.
- `Ti/Chat/Internal/` follows `Ti/Chat/`'s rules (BCL only).
- Logging: routed through `ITiLogger` (interface in `Ti/Internal/`); the default impl in `Ti/Internal/DefaultTiLogger.cs` is the *only* place that touches `MegaCrit.Sts2.Core.Logging.Log`. When extracted, the default impl gets replaced with a no-op or different logger; the rest of `Ti/*` is untouched.
- `Game/*` may reference everything.
- `Game/*` must not be referenced from `Ti/*`. Code-review enforcement for v0.1; a Roslyn analyzer is post-MVP.

## `ChatService` (lower tier)

```csharp
public interface IChatService : IDisposable {
    ChatConnectionState State { get; }                          // <!-- CHANGED: replaces bool IsConnected — R3,R4,R5 -->
    bool IsConnected { get; }                                   // convenience: State is Connected* | Reconnecting
    bool CanSend { get; }                                       // <!-- CHANGED: new — false in anonymous/disconnected/auth-failed states — R3,R4,R5 -->
    DateTime? LastMessageReceivedAt { get; }                    // <!-- CHANGED: new — diagnostic — R3 -->
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;   // <!-- CHANGED: was bool — now richer args -->

    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
    void Disconnect();
    Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default);  // <!-- CHANGED: priority added — R3,R4,R5 -->
}

public enum ChatConnectionState {                               // <!-- CHANGED: New — R3,R4,R5 -->
    Disconnected,
    Connecting,
    ConnectedReadOnly,                  // anonymous justinfan
    ConnectedReadWrite,                 // authenticated
    Reconnecting,
    AuthenticationFailed,               // terminal — no retry
    JoinFailed,                         // banned / channel doesn't exist — no retry
    Disposed,
}

public sealed record ChatMessage(                               // <!-- CHANGED: UserId/Login/IsSubscriber/IsModerator/IsVip added — R3,R4,R5,R6 -->
    string? UserId,                     // Twitch user-id from `user-id` tag. Null if untagged client.
    string Login,                       // lowercased Twitch login (from PRIVMSG prefix)
    string DisplayName,                 // from `display-name` tag; falls back to Login
    string Text,                        // PRIVMSG body
    DateTimeOffset ReceivedAt,          // from `tmi-sent-ts` tag if present, else local clock     <!-- CHANGED: DateTime → DateTimeOffset — R4,R5 -->
    bool IsSubscriber,                  // from badges
    bool IsModerator,                   // from badges
    bool IsVip,                         // from badges
    string VoterKey                     // = UserId ?? $"login:{Login}"; the stable key used by tallies. <!-- CHANGED: new — defuses ambiguity — R3,R4 -->
);

public sealed record ChatCredentials {
    public string Username { get; }
    public string OauthToken { get; }    // always stored without the "oauth:" prefix; TwitchIrcChatService prepends on PASS

    public ChatCredentials(string username, string oauthToken) {
        Username = username?.ToLowerInvariant() ?? throw new ArgumentNullException(nameof(username));
        // Accept either "oauth:abc123" or bare "abc123"; normalise to bare internally.    <!-- CHANGED: oauth: prefix normalisation — Optional Enhancement #11 -->
        OauthToken = oauthToken?.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) == true
            ? oauthToken.Substring(6)
            : oauthToken ?? throw new ArgumentNullException(nameof(oauthToken));
    }

    public override string ToString() => $"ChatCredentials[{Username}, oauth:<REDACTED>]";   // <!-- CHANGED: ToString redacts — R4,R5 -->
}

public enum OutgoingMessagePriority { Low, Normal, High }       // periodic, open, close
public sealed record ChatConnectionChangedEventArgs(ChatConnectionState OldState, ChatConnectionState NewState, string? Reason);
```

### Implementations

**`TwitchIrcChatService`** — TLS connection to `irc.chat.twitch.tv:6697`.

- TCP framing via `StreamReader.ReadLineAsync` over the TLS stream — handles fragmentation correctly. <!-- CHANGED: Was assumed-correct framing; spec now explicit — R2 -->
- Capabilities requested: `twitch.tv/tags` (display-name + user-id + badges + `tmi-sent-ts`) + `twitch.tv/commands` (NOTICE / RECONNECT / etc).
- Login: `PASS oauth:<token>` + `NICK <username>` + `JOIN #<channel>`. Channel input is normalised: accepts `foo`, `#foo`, `https://twitch.tv/foo`, `http://twitch.tv/foo`, `https://www.twitch.tv/foo/`, with optional trailing path segments stripped, lowercased. <!-- CHANGED: channel normalization — R4 + URL parsing — Optional Enhancement #14 -->
- Anonymous-read mode: `creds == null` → connect with `NICK justinfan{rand6}`. State transitions to `ConnectedReadOnly`; `CanSend` is `false`; `SendMessageAsync` returns a failed task (does *not* warn-spam — `VoteSession` checks `CanSend` first). <!-- CHANGED: Anonymous + receipts behavior fully specified — R3,R4 -->
- Read loop on a background `Task`. Lines pass through `TwitchIrcParser` to a `ChatMessage` (or are routed by command type per the protocol-handling table below). Self-echo guard: messages where `parsed.UserId == self.UserId` are silently dropped before `MessageReceived` fires. <!-- CHANGED: Self-echo filter — R5 -->
- Outgoing send queue: every send goes through `OutgoingMessageQueue` which enforces a token-bucket rate limit (default 90/30s; 18/30s when broadcaster/mod/VIP can't be confirmed) and priority ordering (`High > Normal > Low`). Stale `Low`-priority sends (e.g. periodic tallies) coalesce or drop when the queue is backed up. <!-- CHANGED: Rate limiter + priority queue — R3,R4,R5,R6 -->
- `LastMessageReceivedAt` updated on every parsed `PRIVMSG`. <!-- CHANGED: diagnostic field — R3 -->

### IRC protocol handling matrix          <!-- CHANGED: New table — R3,R4 -->

| IRC command | v0.1 behavior |
|---|---|
| `PING <server>` | Send `PONG <server>` immediately (within IRC read loop). |
| `PRIVMSG #chan :text` | Parse to `ChatMessage`; apply self-echo filter; raise `MessageReceived`. |
| `RECONNECT` | Disconnect gracefully, immediately reconnect (no backoff delay), re-`JOIN`. Do not emit a user-visible `ConnectionStateChanged`. |
| `NOTICE * :Login authentication failed` / `:Error logging in` / `:Improperly formatted auth` | Transition state → `AuthenticationFailed`; do not retry; emit `ConnectionStateChanged` once with `Reason`. |
| `NOTICE #chan :msg_banned` / `:msg_channel_suspended` | Transition → `JoinFailed`; do not retry that channel. |
| `CAP ACK` | Record acknowledged capabilities; proceed to `PASS`/`NICK`/`JOIN`. |
| `CAP NAK` | Log warning. If `tags` was NAK'd, fall back: `ChatMessage.UserId` will be `null` for all messages; `VoteSession` falls back to `VoterKey = "login:<login>"`. |
| `USERSTATE #chan` | Record bot's display-name; otherwise ignore. |
| `ROOMSTATE #chan` | Optional — record `slow`/`emote-only`/`subs-only` for diagnostic display; not used by voting in v0.1. |
| `CLEARCHAT` / `CLEARMSG` | Ignore in v0.1. |
| `USERNOTICE` | Ignore in v0.1 (sub announcements, raids, etc.). |
| Unknown / malformed | Log at `Debug`. Counter increments per dropped line; warn once per 100 drops. |

### Threading

- IRC reads on a background `Task`. Internally, all events (`MessageReceived`, `ConnectionStateChanged`) flow through the injected `IMainThreadDispatcher` before being raised to subscribers. <!-- CHANGED: was hardcoded GameThreadDispatcher — R1,R2,R4,R5 -->
- Subscribers always observe events on the dispatcher's target thread. In production, `GodotMainThreadDispatcher` queues into a `ConcurrentQueue<Action>` and drains the queue from a long-lived autoload `Node`'s `_Process`. In tests, `ImmediateDispatcher` invokes synchronously.
- Message dispatch is **batched**: the dispatcher drains its queue once per frame, not once per message. This avoids `CallDeferred` storms during chat brigades. <!-- CHANGED: explicit batching — R2,R3,R6 -->

### Reconnect

- Network-level failures (read timeout, socket close, TLS error): exponential backoff with jitter — base sequence 5s, 10s, 20s, 40s, capped at 60s; ±20% random jitter applied per attempt. <!-- CHANGED: jitter added — R5 -->
- Auth failures (per protocol matrix): no retry; terminal `AuthenticationFailed`.
- Each attempt re-emits `ConnectionStateChanged` (Disconnected → Reconnecting → Connected* or AuthenticationFailed).
- Heartbeat: if no IRC traffic of any kind for 5 minutes, force a disconnect+reconnect proactively (Twitch IRC has been observed to wedge silently). <!-- CHANGED: heartbeat — R5 -->

### Lifecycle

`ModEntry.Initialize` constructs `TwitchIrcChatService` and calls `ConnectAsync` non-blocking. The mod doesn't block game startup on connection; any vote that opens before connection is established sees `IsConnected == false` and runs locally with no chat input (covered by no-voter random pick).

Shutdown sequence (when StS2 exposes a mod-unload hook, or `ModEntry.Dispose` is invoked):  <!-- CHANGED: explicit shutdown ordering — R3,R4,R5 -->

1. Dispose any open `VoteSession` via `Cancel()` (no winner; pending awaits cancel).
2. Call `IChatService.DisconnectAsync()` with a 3-second timeout `CancellationToken`. The IRC read loop receives a cancellation and exits.
3. `await dispatcher.DrainAsync()` — process all queued events.
4. Dispose the dispatcher (drops any remaining queued actions; logs at Warn).
5. Dispose the IRC socket, send-queue worker, and `CancellationTokenSource`.

If StS2 doesn't provide a hook, the OS reclaims the socket; the dispatcher's `Dispose` runs from the `Node`'s `_ExitTree`.

## `VoteCoordinator` and `Voter`            <!-- CHANGED: New section — instance-based architecture — R2,R3,R4,R5,R6 -->

```csharp
public sealed class VoteCoordinator : IDisposable {
    public IChatService Chat { get; }
    public VoteSession? CurrentSession { get; }

    public VoteCoordinator(
        IChatService chat,
        IMainThreadDispatcher dispatcher,
        ITimerScheduler scheduler,
        IClock clock,
        ITiLogger logger,
        IVoteReceiptFormatter formatter,
        Random? random = null);

    public VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        CancellationToken ct = default);

    public void Dispose();   // disposes any active session; releases subscriptions
}

public static class Voter {
    /// <summary>Process-wide default coordinator; set once by ModEntry.Initialize.</summary>
    public static VoteCoordinator? Default { get; set; }

    /// <summary>Convenience for Harmony-patch call sites: delegates to Default.Start(...).</summary>
    public static VoteSession Start(
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null,
        VoteParsingPolicy? parsing = null,
        CancellationToken ct = default)
        => Default?.Start(label, options, duration, receipts, parsing, ct)
            ?? throw new InvalidOperationException("Voter.Default not initialised");
}
```

**Why both?** Harmony patches benefit from the static `Voter.Start(...)` ergonomic call site. Tests and multiplayer construct `VoteCoordinator` instances directly. Extraction is a file move plus dropping the `Voter` static (or keeping it as a per-extracted-base-mod convenience).

**Multiplayer**: a co-op mod variant in v0.2+ would construct two `VoteCoordinator` instances (one per player's Twitch channel) and each Harmony patch would route to the appropriate coordinator based on which player's decision is being intercepted. v0.1 still ships single-streamer; the API just doesn't preclude multiplayer.

## `VoteSession` (upper tier)

```csharp
public sealed class VoteSession : IDisposable {
    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }     // 0-indexed   <!-- CHANGED: 0-indexed — User input + matches original -->
    public TimeSpan Duration { get; }
    public TimeSpan TimeRemaining { get; }
    public IReadOnlyDictionary<int, int> Tallies { get; } // option index (0-based) -> count; includes zero entries for every option   <!-- CHANGED: zero entries included — R4 -->
    public VoteSessionState State { get; }                // <!-- CHANGED: replaces bool IsClosed — R3,R4,R5 -->
    public int? WinnerIndex { get; }                      // 0-based; null if state != Closed

    public event EventHandler<VoteSession>? TallyChanged;
    public event EventHandler<VoteSession>? Closed;
    public event EventHandler<VoteSession>? Cancelled;    // <!-- CHANGED: distinct from Closed — R5 -->

    public Task<int> AwaitWinnerAsync(CancellationToken ct = default);   // logs Warn on Closed if never called  <!-- CHANGED: no-await Warn — Optional Enhancement #12 -->
    public int CloseNow();                                // <!-- CHANGED: explicit early-close — R3,R4,R5 -->
    public void Cancel();                                 // <!-- CHANGED: abort without winner — R3,R4,R5 -->
    public void Dispose();                                // idempotent cleanup; calls Cancel() if state == Open

    public override string ToString();                    // <!-- CHANGED: debug aid — R3 -->
}

public enum VoteSessionState {                            // <!-- CHANGED: explicit state machine — R3,R4,R5 -->
    Open,           // accepting votes
    Closing,        // duration elapsed or CloseNow() called; computing winner + sending close receipt
    Closed,         // WinnerIndex set; subscribers notified
    Cancelled,      // Cancel() called; no winner; awaiting tasks cancelled
    Disposed,
}

public sealed record VoteOption {
    public int Index { get; }      // 0-based; always equals position in Options list
    public string Label { get; }
    internal VoteOption(int index, string label) { Index = index; Label = label; }   // <!-- CHANGED: constructor internal — only VoteCoordinator builds the list, defusing "VoteOption(5, "wrong")" misuse — Optional Enhancement #15 -->
}

public sealed record VoteReceiptPolicy(
    bool AnnounceOnOpen = true,
    TimeSpan? PeriodicTallyEvery = null,         // null  = adaptive: max(5s, duration/5)         <!-- CHANGED: adaptive cadence — Optional Enhancement #1 -->
                                                  // Zero  = no periodic tally
                                                  // value = fixed cadence
    bool AnnounceOnClose = true) {
    public static VoteReceiptPolicy Default => new();                                            // adaptive default
    public static VoteReceiptPolicy Silent => new(false, TimeSpan.Zero, false);
    public static VoteReceiptPolicy WithFixedCadence(TimeSpan cadence) => new(true, cadence, true);
}

public sealed record VoteParsingPolicy(                    // <!-- CHANGED: New — toggleable !N — R1,R3,R5 -->
    bool AcceptHashCommands = true,
    bool AcceptBangCommands = true) {
    public static VoteParsingPolicy Default => new();
}

public interface IVoteReceiptFormatter {                   // <!-- CHANGED: New — i18n + testability — R3,R4,R5 -->
    string FormatOpen(VoteSession session);
    string FormatPeriodicTally(VoteSession session);
    string FormatClose(VoteSession session);                  // includes tie/no-voter cases
}
```

### State machine                                          <!-- CHANGED: New — R3,R4 -->

```
   construct
      │
      ▼
  ┌────────┐  Duration elapsed │ CloseNow()      ┌──────────┐
  │  Open  │────────────────────────────────────▶│ Closing  │
  └────┬───┘                                     └────┬─────┘
       │                                              │ winner computed,
       │ Cancel() │ Dispose()                         │ close receipt enqueued
       ▼                                              ▼
  ┌─────────┐                                   ┌────────┐
  │Cancelled│                                   │ Closed │
  └────┬────┘                                   └────┬───┘
       │                                              │
       └──────────────────┐         ┌─────────────────┘
                          ▼         ▼
                       ┌──────────────┐
                       │   Disposed   │
                       └──────────────┘
```

- `Open`: accepts votes; periodic tally fires; tally events fire. Legal transitions: → `Closing` (duration elapsed or `CloseNow()`), → `Cancelled` (`Cancel()`), → `Disposed` (`Dispose()` calls `Cancel()` first).
- **No-await detection**: `VoteSession` tracks whether `AwaitWinnerAsync` has been called by anyone. If the session reaches `Closed` (or `Cancelled`) without ever being awaited, log at `Warn`: `"VoteSession <id> closed with winner <n> but AwaitWinnerAsync was never called — caller likely forgot to consume the result."` Helps catch a class of bug where a Harmony patch fires `Voter.Start(...)` but discards the returned session. <!-- CHANGED: no-await Warn — Optional Enhancement #12 -->
- `Closing`: a brief intermediate during winner computation + close-receipt enqueue. Not exposed to consumers in events; included for completeness.
- `Closed`: terminal-with-winner. `WinnerIndex` set; `Closed` event has fired; `Cancelled` event will not fire.
- `Cancelled`: terminal-without-winner. `WinnerIndex` is null; `Cancelled` event has fired; awaiting `AwaitWinnerAsync` tasks complete with `OperationCanceledException`.
- `Disposed`: cleanup. From `Closed` or `Cancelled`, `Dispose` is a no-op. From `Open`, `Dispose` calls `Cancel()` first.

### Concurrency

`VoteCoordinator.Start` (and therefore `Voter.Start`) throws `InvalidOperationException` if `CurrentSession != null && CurrentSession.State == Open`. Callers must dispose / cancel the previous session first. Loud-but-safe; surfaces misuse immediately.

The receipt-sequence ordering during close is: <!-- CHANGED: explicit ordering — R4 -->

1. Stop accepting new votes (state → `Closing`).
2. Compute winner (with random tie-break / no-voter handling).
3. Set `WinnerIndex`; transition state → `Closed`.
4. Unsubscribe from `IChatService.MessageReceived`.
5. Enqueue close receipt via `IChatService.SendMessageAsync(text, OutgoingMessagePriority.High)`.
6. Clear `coordinator.CurrentSession`.
7. Complete `AwaitWinnerAsync` tasks (using `TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)` to avoid main-thread reentrancy when consumers `await` from a Harmony patch). <!-- CHANGED: RunContinuationsAsynchronously — R5 -->
8. Fire `Closed` event.

A `Closed` handler can therefore call `Voter.Start(...)` for the next vote without colliding on `CurrentSession`. The send-queue's priority + ordering guarantees ensure the close receipt is sent before the next open receipt.

### Command parsing

- Default regex: `^[#!]?(\d+)(?:\s|$)` (when `VoteParsingPolicy.AllowBang == true`). Strict variant: `^#?(\d+)(?:\s|$)` (when `AllowBang == false`).
- Anchored to start of message; terminated by whitespace or end-of-message; leading `#` or `!` optional.
- Examples that match (default policy): `#0`, `0`, `!0`, `#0 lol`, `0 lol`, `00` (captures `0`).
- Examples that don't match: `lol #0` (not at start), `1st time` (digit followed by letter), `1.5 sec` (digit followed by `.`), `12` when there are only 4 options (parses to 12, out-of-range, ignored).
- Out-of-range index → silently ignored (`Debug` log + counter increment).

### Tally rules

- Backed by `Dictionary<string, int>` keyed by `ChatMessage.VoterKey` (= `UserId` if available, else `login:<login>`). <!-- CHANGED: VoterKey defined — R3,R4 -->
- Each valid vote replaces the user's prior entry (latest wins).
- `Tallies` is computed on read; includes a zero entry for every option index (snapshot, not live reference). <!-- CHANGED: zero entries — R4 -->
- `TallyChanged` fires only on actual change (re-vote with same option does not refire — already in v1; now explicitly tested).
- Voter dictionary capped at 10,000 unique voters; further unique voters are dropped with a `Warn` log fired once per session. <!-- CHANGED: dict cap — R5 -->

### Closing edge cases

When `Duration` elapses (or `CloseNow()` is called):

1. Compute final tallies.
2. Determine winner:
   - Exactly one option has the max count → winner.
   - Multiple options tie for max → uniform random across tied options.
   - Zero votes received → uniform random across **all** options.
3. Enqueue close receipt (`High` priority). Note any IRC-disconnect gap during the vote in the receipt text.
4. Fire events per state-machine ordering above.

When `Cancel()` is called: skip steps 1–3 (no winner, no close receipt). Fire `Cancelled` event; cancel awaits.

### Validation

- `VoteCoordinator.Start` / `Voter.Start` throws `ArgumentException` on:
  - empty `options` list,
  - `Duration < TimeSpan.FromSeconds(1)`,
  - **more than 10 options** (since chat can only single-digit `#0`–`#9`).  <!-- CHANGED: 10 not 9 — 0-indexed -->
  - any option label empty after trim, or > 200 chars after CR/LF/control-char strip.  <!-- CHANGED: label validation — R4 -->

### Threading

- Internal close timer registered through `ITimerScheduler`; `FakeTimerScheduler` drives it deterministically in tests.
- `IChatService.MessageReceived` events arrive on the dispatcher's target thread; `VoteSession` doesn't re-dispatch.
- All `TallyChanged` / `Closed` / `Cancelled` events fire on the dispatcher's target thread.

### Anonymous-mode behavior

If `IChatService.CanSend == false` at `Start` time (anonymous justinfan or disconnected), the vote runs but the receipt formatter's outputs are silently skipped (no warn-spam). The receipt at close still fires the `Closed` event but no chat message is sent. <!-- CHANGED: explicit anonymous handling — R3,R4 -->

### Receipts (default English formatter)

- Open: `"Vote: <label>! Type 0, 1 or 2 — <duration>s left."` (compact labels variant: `"Vote: <label>! 0 <a>, 1 <b>, 2 <c> — <duration>s."`). <!-- CHANGED: 0-indexed + compact labels — R4 + 0-indexed -->
- Periodic (cadence per `VoteReceiptPolicy.PeriodicTallyEvery`; default adaptive `max(5s, duration/5)` so a 30s vote ticks every 6s, a 60s vote every 12s, a 15s vote every 5s): `"Vote: 0=12 1=8 2=3, <remaining>s left."` Skipped if all tallies are zero (avoids spam). Skipped if identical to the previous tally message (avoids redundant chat noise). <!-- CHANGED: adaptive cadence — Optional Enhancement #1; identical-tally skip — R5 -->
- Close (winner): `"Chat chose 1: <label>."`  <!-- CHANGED: "chose" not "picked" — R3 -->
- Close (2-way tie): `"Tie! Chat chose 1: <label> randomly between <k> tied options."` <!-- CHANGED: shorter — R3 -->
- Close (3+ way tie): `"<k>-way tie! Chat chose 1: <label> randomly."` <!-- CHANGED: distinct format for 3+ ties — Optional Enhancement #13 -->
- Close (no votes): `"No votes received — chat got 1: <label> randomly."`
- Close (with disconnect gap): `"Chat chose 1: <label> (chat was offline 8s during voting)."`

### Rate-limit math

With adaptive cadence (`max(5s, duration/5)`):
- 15s vote: cadence = 5s → 1 open + ~2 periodic + 1 close = 4 messages.
- 30s vote: cadence = 6s → 1 + ~4 + 1 = 6 messages.
- 60s vote: cadence = 12s → 1 + ~4 + 1 = 6 messages.

Worst-case back-to-back 15s votes (event → card reward → shop): 3 votes × 4 messages each = 12 messages in ~45 seconds. Well under both 90/30s and 18/30s limits. <!-- CHANGED: math shown — R6; updated for adaptive cadence — Optional Enhancement #1 -->

The rate limiter inside `TwitchIrcChatService` enforces the ceiling regardless; this section is just to demonstrate that the default policy doesn't approach the limit.

## `VoteOverlayControl` (UI, optional)

```csharp
public sealed partial class VoteOverlayControl : Control {
    public void AttachTo(VoteSession session);
    public void Detach();

    [Export] public float AutoHideDelaySeconds { get; set; } = 3f;
    // Position/anchor handled via Godot Control's built-in Anchor* properties — no parallel API. <!-- CHANGED: removed redundant AnchorPosition — R5 -->
}
```

- Reads `session.Tallies`, `session.TimeRemaining`, `session.State` from `_Process` each frame; redraws bars/percentages/countdown. <!-- CHANGED: _Process-driven; not event-driven — R6 -->
- Subscribes only to `Closed` and `Cancelled` to schedule the auto-hide fade.
- On `Closed`: highlights winner, fades out after `AutoHideDelaySeconds`, self-detaches.
- On `Cancelled`: fades out immediately, self-detaches.
- `_ExitTree` unsubscribes defensively.

## `ChatStatusControl` (UI, optional, diagnostic)              <!-- CHANGED: New — Optional Enhancement #9 -->

A small Godot `Control` consuming `IChatService` for at-a-glance connection diagnostics.

```csharp
public sealed partial class ChatStatusControl : Control {
    public void AttachTo(IChatService chat);
    public void Detach();
}
```

- Renders a single line of status text, redrawn from `_Process`:
  - `ChatConnectionState.ConnectedReadWrite` + recent traffic: `"Chat: connected (last msg 3s ago)"`
  - `ConnectedReadOnly`: `"Chat: connected (read-only)"`
  - `Reconnecting`: `"Chat: reconnecting…"`
  - `AuthenticationFailed`: `"Chat: auth failed (check oauth)"` in error colour
  - `JoinFailed`: `"Chat: can't join channel"`
  - `Disconnected` / `Disposed`: `"Chat: disconnected"`
- Uses `IChatService.LastMessageReceivedAt` to compute the "last msg Ns ago" suffix.
- Subscribes to `ConnectionStateChanged` for instant text updates; `_Process` only updates the "ago" timestamp.
- Optional in v0.1 — game-side code can choose whether to show it. Strong recommendation: ship it on by default so streamers can self-diagnose during runs.

## `ImmediateDispatcher` (public, in `Ti/Internal/`)         <!-- CHANGED: ImmediateDispatcher elevated to public — Optional Enhancement #10 -->

```csharp
public sealed class ImmediateDispatcher : IMainThreadDispatcher {
    public void Post(Action action) => action();
    public Task DrainAsync() => Task.CompletedTask;
}
```

- Synchronous pass-through. The action runs on the calling thread.
- Default impl for **tests** (lets `VoteSession` tests assert on event timing without queueing).
- Also useful for **non-Godot harness** consumers — e.g. the IRC test-fixture generator tool, or a future headless integration-test suite. They construct `IChatService` + `ImmediateDispatcher` and never touch Godot.
- Public type rather than `internal`-only because consumer mods extracting the TI layer to a non-Godot context might want to use it directly.

## Logging via `ITiLogger`                                  <!-- CHANGED: New section — R2,R4,R5 -->

```csharp
public interface ITiLogger {
    void Debug(string msg);
    void Info(string msg);
    void Warn(string msg);
    void Error(string msg, Exception? ex = null);
}

public sealed class DefaultTiLogger : ITiLogger {
    // Wraps MegaCrit.Sts2.Core.Logging.Log on each call.
    // OauthToken or ChatCredentials-shaped strings are scrubbed before forwarding.    <!-- CHANGED: token scrubbing — R4,R5 -->
}
```

- `Ti/*` types take `ITiLogger` via constructor injection (or use the static `Voter.Default.Logger` for trivial call sites).
- The default impl wraps `MegaCrit.Sts2.Core.Logging.Log`; that's the only place in `Ti/*` that touches StS2 logging.
- When `Ti/*` is extracted to a base mod, `DefaultTiLogger` is replaced with an impl appropriate to the new context (likely just a different wrapper of the same `Log` API).
- All log calls scrub anything matching the oauth token before forwarding (defense-in-depth on top of `ChatCredentials.ToString` redaction).

**Log levels**:
- `Debug` for parser drops, out-of-range votes, redundant-tally skips.
- `Info` for connect/disconnect, vote open/close, IRC retry attempts.
- `Warn` for anonymous-mode send attempts (rare; mostly bug indicator now), mid-vote disconnects, voter-dict overflow, malformed IRC drops > 100/session.
- `Error` for `AuthenticationFailed`, unrecoverable parser/socket failures.

## Testing strategy

### Unit-testable cleanly (no Godot, no network)

- **`TwitchIrcParser`** — corpus-based tests. Inputs: real captured Twitch IRC lines (PRIVMSG with full tags including IRCv3 escaping `\:` `\s` `\\` `\r` `\n`, PING, JOIN, PART, CAP ACK/NAK, NOTICE auth-failure / channel-banned, RECONNECT, USERSTATE, ROOMSTATE, malformed/truncated/multi-byte). Output: expected `ChatMessage` (or `null` for non-message types).
- **`OutgoingMessageQueue`** — token-bucket rate limiter, priority ordering, stale-tally coalescing.  <!-- CHANGED: new test target — R3,R4,R5 -->
- **`VoteSession`** with `FakeChatService` + `FakeClock` + `FakeTimerScheduler` + seeded `Random`:
  - simple vote (3 voters → unique winner)
  - vote-change (latest wins; tally counts move correctly)
  - tie → random tie-break (seeded RNG; deterministic)
  - zero votes → random pick across all options
  - mid-vote disconnect (chat events stop), then reconnect (votes resume; close receipt notes gap)
  - concurrent `Coordinator.Start` while session active → throws
  - votes after close → ignored
  - validation: empty options, duration < 1s, > 10 options, empty labels, oversized labels → throws
  - state machine transitions: Open → Closing → Closed; Open → Cancelled; Open → Disposed → Cancelled
  - `AwaitWinnerAsync` cancellation: only the awaiting caller is cancelled; session continues
  - `CloseNow()` mid-vote: winner from current tallies; close receipt sent
  - `Cancel()` mid-vote: no winner; pending awaits throw `OperationCanceledException`
  - `Dispose()` is idempotent; double-dispose is a no-op
  - receipt formatter test: every receipt format with all combinations (winner / tie / no-voter / disconnect-gap)
  - voter-dict overflow → 10,001st voter dropped with one Warn
  - self-echo: bot's own message dropped at IRC layer
- **`ChatCredentials.ToString`** test: token never appears in output.
- **`DefaultTiLogger`** test: oauth-shaped strings are scrubbed.

### Integration / harder

- **`TwitchIrcChatService` connection lifecycle** — exponential backoff verified manually first; future automation could use a minimal IRC mock server.
- **`GodotMainThreadDispatcher`** — exercised implicitly by Godot scene tests; basic unit test for queue ordering with `ImmediateDispatcher`.

### Visual smoke

- A Godot scene wires `FakeChatService` + `VoteOverlayControl`. Dev-only buttons inject sample chat messages so we can watch the bars update.

### Manual end-to-end harness

- A small console runner that constructs `TwitchIrcChatService`, connects to a throwaway test channel, opens a fake vote, prints incoming `ChatMessage`s and `TallyChanged` events to stdout. We type `0` / `#0` / `!0` from a second Twitch window to validate the live IRC path including chat receipts. <!-- CHANGED: "throwaway test channel" — Twitch doesn't have private channels — R6 -->

### Mechanics

- Tests live in `slay-the-streamer-2.tests/` (xUnit, `net9.0`).
- Source-referenced (not DLL-referenced) so internals are testable without `InternalsVisibleTo` gymnastics. (Reviewer 4 questioned this; for a greenfield mod with no public API contract yet, source-referenced is lighter and the compilation-divergence risk is theoretical. Revisit if it bites.)
- Inject `IClock`, `ITimerScheduler`, `IMainThreadDispatcher`, `Random`, `ITiLogger` so every component is deterministically testable.

### IRC test-fixture generator                              <!-- CHANGED: New — Optional Enhancement #6 -->

A small console tool living in `tools/irc-fixture-generator/` (separate csproj, NOT shipped in the mod assembly):

- Takes a Twitch channel name as a CLI arg.
- Connects via `TwitchIrcChatService` in passive mode (`creds == null`, anonymous justinfan).
- Captures every raw IRC line received for a configurable duration (e.g. 30 minutes).
- Writes the lines to a JSON file at `slay-the-streamer-2.tests/Fixtures/irc-corpus-YYYY-MM-DD.json`, with metadata (channel, capture window, Twitch IRC server fingerprint).
- The parser test corpus loads these JSONs and asserts the parser handles every line correctly.
- Run once when bootstrapping the parser tests, then ad-hoc whenever Twitch ships an IRCv3 quirk that breaks our parser.

This hardens the parser without us having to hand-curate edge-case fixtures. ~50 LOC of throwaway tooling.

## Lifecycle / ModEntry wiring                              <!-- CHANGED: New section — R3,R4,R5 -->

```csharp
[ModInitializer("Initialize")]
public static class ModEntry {
    public static void Initialize() {
        // 1. Apply Harmony patches in our assembly
        var harmony = new Harmony("surfinite.slay_the_streamer_2");
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        // 2. Register the dispatcher autoload
        var dispatcher = new GodotMainThreadDispatcher();
        DispatcherAutoload.Register(dispatcher);

        // 3. Build TI services
        var logger = new DefaultTiLogger();
        var clock = new SystemClock();
        var scheduler = new SystemTimerScheduler(clock);
        var formatter = new DefaultEnglishReceiptFormatter();
        var chat = new TwitchIrcChatService(dispatcher, logger);
        var coordinator = new VoteCoordinator(chat, dispatcher, scheduler, clock, logger, formatter);

        // 4. Make the coordinator discoverable from Harmony patches
        Voter.Default = coordinator;

        // 5. Connect (non-blocking; settings UI provides credentials separately)
        var creds = ModSettings.LoadChatCredentials();   // out of scope for this design
        _ = chat.ConnectAsync(ModSettings.LoadChannel(), creds);

        logger.Info("Slay the Streamer 2: TI layer initialised");
    }
}
```

## Open items deferred to implementation / later

- **`MegaCrit.Sts2.Core.Logging.Log` thread-safety** — the IRC background task and timer threads call into the logger before reaching the dispatcher. If `Log` isn't thread-safe, `DefaultTiLogger` will need to buffer and flush on the main thread. Verify before implementation. <!-- CHANGED: explicit TODO — R3,R5 -->
- **Godot autoload registration from a mod assembly** — `AddAutoloadSingleton` requires a path or Node reference. The implementation needs to validate that runtime registration via `ProjectSettings.SetSetting("autoload/...", ...)` works from `[ModInitializer]`. Plan B: `DispatcherAutoload` is a hidden singleton `Node` added to the scene tree by the first `VoteOverlayControl` (or a hidden `Node` spawned by `ModEntry`). <!-- CHANGED: explicit TODO — R3 -->
- **Streamer-configurable receipt policy** — ship `VoteReceiptPolicy.Default` for v0.1; expose configuration when the broader settings UI is designed.
- **Reconnect retry budget** — v0.1 retries indefinitely with capped backoff and jitter for transport failures. Auth and join failures are terminal. If transport-retry-forever turns out annoying, add a `MaxRetryDuration` knob.
- **Twitch oauth source** — out of scope here; covered by the settings/onboarding design.
- **`AbstractModel` vs Harmony for the actual decision-substitution glue** — orthogonal to this layer. Decided per-decision in `Game/DecisionVotes/*` once we've inventoried `AbstractModel`'s virtual method surface (see notes/03 open questions).
- **Harmony deadlock risk on `await Voter.Start(...).AwaitWinnerAsync()`** — if a Harmony prefix runs on the Godot main thread and `AwaitWinnerAsync` continuations also dispatch to the main thread, blocking-await on the prefix could deadlock. The fix is to ensure `RunContinuationsAsynchronously` is set on the underlying `TaskCompletionSource` (now in spec) and to verify the Godot main-thread `SynchronizationContext` doesn't pump deferred actions during a blocking await. Validate with a smoke test before relying on the pattern. <!-- CHANGED: deadlock concern flagged — R3 -->

## Future work / out of scope for v0.1

- `ChatCommandRouter` middle tier (only when a second consumer mod actually appears).
- Twitch Helix API integration for richer features (channel point redemptions, polls, predictions).
- Whisper / Twitch Extension overlays.
- Lifting `Ti/*` into a separate base-mod assembly. Plan: when the lift happens, `Ti/Chat/`, `Ti/Voting/`, `Ti/Internal/` (minus `Ti/Godot/`) move to a new csproj; `Ti/Ui/` either moves with them or stays as a slay-the-streamer-2-specific control; `Ti/Godot/` stays with each consumer (or gets refactored into a non-Godot-defaulted impl).
- Subscriber/mod/VIP-only voting filters — data is exposed in `ChatMessage` already; filter logic is a `where` clause in `VoteCoordinator.Start` consumers when added.
- Localised receipts via additional `IVoteReceiptFormatter` impls.
- Multi-channel/multi-streamer support — now possible at the API level (instance-based `VoteCoordinator`); full StS2 co-op integration is a v0.2+ design pass.

---

# Optional Enhancements (pick what you want)

These were "Consider"-tier items from the meta-review — good ideas but not urgent. Tell me which numbers (if any) to fold into the spec, or leave them for future iterations.

**Folded in (v2.1)**: 1, 6, 9, 10, 11, 12, 13, 14, 15. See `<!-- CHANGED: ... — Optional Enhancement #N -->` markers throughout v2.1 for the exact shifts.

**Remaining for future consideration**:

| # | Change | Reviewers | Effort | Recommendation |
|---|---|---|---|---|
| 2 | **Reply-parent-msg-id per-voter receipts** — bot @-replies first-time voters with `"@viewer counted 1."` as a threaded reply on the open message. Closes the lag gap individually without chat-floor pollution. | R3 | Small | Lean no for v0.1 (volume scales with brigade size); strong candidate for v0.2 |
| 3 | **Receipt "quiet period"** — skip a periodic tally if it's byte-identical to the previously sent one (e.g., lopsided votes where #0 dominates and no one's switching). | R5 | Trivial | Lean yes — already half-done with the "skip if all zero" rule. *(Not picked in v2.1; revisit if chat-floor noise becomes a complaint.)* |
| 4 | **Heartbeat reconnect** — proactive disconnect + reconnect if no IRC traffic of any kind for 5 minutes. *(Already in v2.1 — see Reconnect section.)* | R5 | Already in v2 | n/a |
| 5 | **Observability dashboard** — basic counters (messages received, votes accepted, votes out-of-range, reconnects) are in v2.1; a fuller dashboard with per-stream stats is post-MVP. | R5 | Medium | Neutral — depends whether a streamer ever asks |
| 7 | **`TimeProvider` (BCL)** instead of custom `IClock`/`ITimerScheduler` — .NET 8+ ships a standard time abstraction. Adds one tiny NuGet dep (`Microsoft.Bcl.TimeProvider`) but trades ~30 lines of custom code for a maintained type. | R4, R5 | Small | Neutral — both shapes work; the v2.1 custom one is fine. Revisit if a NuGet dep becomes acceptable |
| 8 | **Vendor a single-file Twitch IRC library** (Plan B from decision #5) — only if the handcrafted client proves problematic during implementation. | R1, R5 | Medium | Neutral — Plan B; revisit during implementation |
