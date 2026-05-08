# TI layer design — IRC + vote engine boundary

**Date**: 2026-05-08
**Status**: Approved (sections 1–5 reviewed in brainstorming session 2)
**Scope**: the Twitch-integration layer of `slay-the-streamer-2`. Defines the boundary between the reusable chat-IO machinery and the StS2-specific vote bindings.

## Goals

1. **One-shot vote API** that StS2-side glue code can call to ask chat a multiple-choice question and get an answer.
2. **Modular within StS2**: a future StS2 mod that wants chat-driven interactions but not voting (e.g. "help the streamer", "every 5 min chat picks an outfit") can build on the same `ChatService` without forking IRC code.
3. **Same repo, clean seam**: ship as one mod for v0.1; design boundaries so a later "lift the TI layer into a base mod" change is a file move + small registration shim, not a refactor.
4. **Read + write IRC**: viewers need in-chat confirmation that votes are landing, since the on-screen vote bar is on a 5–30s video delay relative to real-time chat.

## Non-goals

- Cross-game reuse. The layer can use Godot and `sts2.dll` types where useful; it's mod-agnostic, not game-agnostic.
- Twitch Extension overlays / PubSub / channel-points integration. Read-only-ish IRC plus periodic outgoing tally announcements is the whole I/O surface.
- A general `!command` router. YAGNI for v0.1; the upper-tier `VoteSession` is the only built-in consumer of `ChatService`.
- A streamer-side configuration UI. Out of scope for this design pass; covered separately when v0.1 settings are designed.

## Decisions (from session 2 brainstorming)

| # | Decision | Rationale |
|---|---|---|
| 1 | Two-tier API: `ChatService` (lower) + `VoteSession` (upper) + optional UI | Cleanest extraction unit at `ChatService`; YAGNI says no `ChatCommandRouter` middle tier yet. |
| 2 | Strictly one vote open at a time | Matches StS's one-screen-at-a-time flow; `Voter.Start` throws if a session is open. |
| 3 | Receipts: open + periodic tally + close | Closes the streamer-vs-viewer lag gap; default tally cadence 7 s. Safe under Twitch's 100 msg / 30 s rate limit. |
| 4 | One vote per user, latest `#N` wins | Lets viewers correct typos and react to the running tally; matches robojumper's StS1 base mod. |
| 5 | Handcrafted minimal Twitch IRC client | ~200 LOC, zero NuGet deps. Cleanest extraction; Twitch's IRC subset is small. |
| 6 | Tie-break: uniform random across tied options | Simple, fair, "chat decides" stays intact. Receipt announces the random pick honestly. |
| 7 | No-voter edge: pick uniformly at random across all options | Game-side never gets `null`; receipts say so explicitly. |
| 8 | Vote-command parser: strict `#N` only | Reduces false positives from natural chat (no `!1`, no bare `1`). |

## Architecture

```
src/
├── Ti/                          [future-extractable; no StS2-specific types
│   │                             except logging]
│   ├── Chat/
│   │   ├── IChatService.cs
│   │   ├── ChatMessage.cs
│   │   ├── ChatCredentials.cs
│   │   ├── TwitchIrcChatService.cs      handcrafted IRC implementation
│   │   └── FakeChatService.cs           in-memory test/dev implementation
│   ├── Voting/
│   │   ├── VoteSession.cs
│   │   ├── VoteOption.cs
│   │   ├── VoteReceiptPolicy.cs
│   │   └── Voter.cs                     static convenience entry, owns
│   │                                     CurrentSession singleton
│   ├── Ui/                              optional, Godot-dependent
│   │   └── VoteOverlayControl.cs
│   └── Internal/
│       ├── GameThreadDispatcher.cs      marshal IRC → Godot main thread
│       ├── ConnectionRetryPolicy.cs
│       ├── TwitchIrcParser.cs           pure-function parser
│       └── IClock.cs                    + SystemClock + FakeClock
├── Game/                                [StS2-specific; NOT extractable]
│   ├── DecisionVotes/                   one Harmony patch per voted decision
│   └── Models/                          [TBD if sealed-deck uses AbstractModel]
└── ModEntry.cs                          [ModInitializer], wires everything
```

**Namespaces**: `SlayTheStreamer2.Ti.*` for the extractable layer; `SlayTheStreamer2.Game.*` for StS2-specific glue. The line between them is the lift point.

**Allowed dependencies**:
- `Ti/*` may reference: BCL, Godot (in `Ti/Ui/*` only), `MegaCrit.Sts2.Core.Logging.Log`. Nothing else from `sts2.dll`.
- `Game/*` may reference everything.
- `Game/*` must not be referenced from `Ti/*`. Enforced by code review (no automated guard for v0.1).

## `ChatService` (lower tier)

```csharp
public interface IChatService : IDisposable {
    bool IsConnected { get; }
    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<bool>? ConnectionStateChanged;
    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
    void Disconnect();
    Task SendMessageAsync(string text);
}

public sealed record ChatMessage(string User, string DisplayName, string Text, DateTime ReceivedAt);
public sealed record ChatCredentials(string Username, string OauthToken);
```

### Implementations

**`TwitchIrcChatService`** — TLS connection to `irc.chat.twitch.tv:6697`.
- Capabilities requested: `twitch.tv/tags` (display-name + user-id), `twitch.tv/commands` (notices). No `membership` (don't need JOIN/PART noise).
- Login: `PASS oauth:<token>` + `NICK <username>` + `JOIN #<channel>`.
- Anonymous-read mode: `creds == null` → connect with `NICK justinfan{rand6}`. `SendMessageAsync` fails (logs warning) in this mode.
- Read loop on a background `Task`. Lines are CRLF-delimited; pass each through `TwitchIrcParser` to a `ChatMessage` (or skipped non-PRIVMSG types).
- PING/PONG keepalive handled inline.
- Reconnect: on read failure or socket close, exponential backoff (5 s → 10 s → 20 s → 40 s, cap 60 s) on a separate retry loop. Each retry attempt re-emits `ConnectionStateChanged(false)` then `(true)` on success.

**`FakeChatService`** — in-memory implementation for tests and the visual smoke harness.
- `Inject(ChatMessage msg)` to deliver a message synchronously to subscribers.
- `Connect`/`Disconnect` flip the `IsConnected` flag and fire the state event; no I/O.
- `SendMessageAsync` records sent strings to an exposed list for assertions.

### Threading

- IRC reads on a background `Task`. Internally, all events (`MessageReceived`, `ConnectionStateChanged`) flow through `GameThreadDispatcher` before being raised to subscribers.
- `GameThreadDispatcher` queues actions and flushes them via Godot's `CallDeferred` on a long-lived autoload node. Subscribers always observe events on the Godot main thread; consumers never touch threads.

### Lifecycle

- `ModEntry.Initialize` constructs the service and fires `ConnectAsync` non-blocking. The mod doesn't block game startup on connection; the first decision vote that opens before connection is established sees `IsConnected == false` (vote still runs locally with no chat input — covered by no-voter edge case).
- On game shutdown: `Disconnect` if the game exposes a shutdown hook; otherwise the OS cleans up the socket.

## `VoteSession` (upper tier)

```csharp
public sealed class VoteSession : IDisposable {
    public string Id { get; }
    public string Label { get; }
    public IReadOnlyList<VoteOption> Options { get; }
    public TimeSpan Duration { get; }
    public TimeSpan TimeRemaining { get; }
    public IReadOnlyDictionary<int, int> Tallies { get; }
    public bool IsClosed { get; }
    public int? WinnerIndex { get; }

    public event EventHandler<VoteSession>? TallyChanged;
    public event EventHandler<VoteSession>? Closed;

    public Task<int> AwaitWinnerAsync(CancellationToken ct = default);
    public void Dispose();
}

public sealed record VoteOption(int Index, string Label);

public sealed record VoteReceiptPolicy(
    bool AnnounceOnOpen = true,
    TimeSpan? PeriodicTallyEvery = null,
    bool AnnounceOnClose = true) {
    public static VoteReceiptPolicy Default => new(true, TimeSpan.FromSeconds(7), true);
    public static VoteReceiptPolicy Silent => new(false, null, false);
}

public static class Voter {
    public static VoteSession Start(
        IChatService chat,
        string label,
        IReadOnlyList<string> options,
        TimeSpan duration,
        VoteReceiptPolicy? receipts = null);

    public static VoteSession? CurrentSession { get; }
}
```

### Concurrency

`Voter.Start` throws `InvalidOperationException` if `CurrentSession != null && !CurrentSession.IsClosed`. Callers must `Dispose` the previous session first. Loud-but-safe; surfaces misuse immediately rather than silently dropping votes into the wrong session.

### Command parsing

- Regex: `#(\d+)`, applied to the raw message text. (Case-insensitivity is moot since the captured group is digits.)
- First match per message wins. `"#1 oops #2"` counts as `#1`; the user can post a clean `#2` later to change.
- Out-of-range index (e.g. `#5` when only 3 options) → silently ignored.
- Non-numeric or empty (`#`, `#abc`) → silently ignored.
- Strict `#N` only — no `!N`, no bare `N`. Fewer false positives from regular chat.

### Tally rules

- Backed by a `Dictionary<string, int>` keyed by Twitch user-id (from CAP tags), value = the user's current 1-based option index.
- Each valid `#N` from a user replaces their prior entry.
- `Tallies` is computed on read by grouping the dict; `TallyChanged` fires after every replace (deduped if the new vote equals the old).
- Reads of `Tallies` return a snapshot dictionary (not a live reference).

### Closing

When `Duration` elapses (or `Dispose` is called early):
1. Compute final tallies.
2. Determine winner:
   - If exactly one option has the max count → that's the winner.
   - If multiple options tie for max → uniform random across tied options. Receipt announces the tie + random pick.
   - If zero votes received → uniform random across **all** options. Receipt says so.
3. Set `WinnerIndex`, set `IsClosed = true`.
4. Fire `Closed`. Any pending `AwaitWinnerAsync` tasks complete with the winner.
5. Send the close receipt (if policy enables it).
6. Set `Voter.CurrentSession = null` so a new vote can start.

### Disconnect tolerance

- IRC disconnect mid-vote does **not** abort the session. Existing tally is preserved; chat votes during outage are lost; post-reconnect votes resume counting.
- The close receipt notes the gap if one occurred (e.g. `"Chat picked #2: Defend (chat was offline 8 s during voting)."`).

### Receipts (default policy)

- Open: `"Vote: <label>! Type #1, #2, #3 — <duration>s left."`
- Periodic (every `PeriodicTallyEvery`, default 7 s): `"Vote: #1=12 #2=8 #3=3, <remaining>s left."` Skipped if all tallies are zero (avoids spam during dead air).
- Close (winner): `"Chat picked #<n>: <label>."`
- Close (tie): `"Tied between #<a> <label_a> and #<b> <label_b> — chose #<n> <label_n> by random pick."`
- Close (no votes): `"No votes received — picked #<n> <label> randomly."`

### Validation

- `Voter.Start` throws `ArgumentException` on:
  - empty `options` list,
  - `Duration < TimeSpan.FromSeconds(1)`,
  - more than 9 options (since chat can only single-digit `#1`–`#9`).
- Practical minimum durations are higher (5 s+); not enforced here.

### Threading

- Internal timer (`System.Threading.Timer`) ticks; ticks are routed through `GameThreadDispatcher` before triggering close logic.
- `IChatService.MessageReceived` events arrive already on the main thread (the `ChatService` dispatcher does that).
- All `TallyChanged` / `Closed` events fire on the Godot main thread.

## `VoteOverlayControl` (UI, optional)

```csharp
public sealed partial class VoteOverlayControl : Control {
    public void AttachTo(VoteSession session);
    public void Detach();

    [Export] public Vector2 AnchorPosition { get; set; } = new(20, 20);
    [Export] public float AutoHideDelaySeconds { get; set; } = 3f;
}
```

- Subscribes to `TallyChanged` and `Closed` on the attached session. Renders bars (one per option), label text, vote count, percentage, and a countdown.
- `_Process` updates the countdown from `session.TimeRemaining` each frame.
- On `Closed`: highlights the winner row, then fades and self-detaches after `AutoHideDelaySeconds`.
- `_ExitTree` unsubscribes defensively.
- Game-side may instantiate it once, attach on every vote open. Or skip it entirely and render its own UI from the same `VoteSession` events.
- v0.1: functional only — bars + counts + countdown. Animations and theming are post-MVP.

## Testing strategy

### Unit-testable (no Godot, no network)

- **`TwitchIrcParser`** — corpus-based tests. Inputs: real captured Twitch IRC lines (PRIVMSG with full tags, PING, JOIN, PART, CAP ACK, malformed/truncated lines, ACTION messages, lines with emote tags, multi-byte chars). Output: expected `ChatMessage` or `null`.
- **`VoteSession`** with `FakeChatService` and `FakeClock`:
  - simple vote (3 users, 3 different options, clean winner)
  - vote-change (alice: `#1` → `#2`, count of `#1` should drop, `#2` rise)
  - tie → random tie-break (seed the RNG for determinism)
  - zero votes → random pick across all options
  - mid-vote disconnect (chat events stop), then reconnect (votes resume)
  - concurrent `Voter.Start` while session active → throws
  - votes after `Closed` → ignored
  - validation: empty options, duration < 1s, > 9 options → throws

### Integration / harder

- **`TwitchIrcChatService` connection lifecycle** — exponential backoff verified manually first; future automation could use a minimal IRC mock server.
- **`GameThreadDispatcher`** — exercised implicitly by Godot scene tests; explicit unit test if the abstraction grows beyond a thin shim.

### Visual smoke

- A Godot scene wires `FakeChatService` + `VoteOverlayControl`. Dev-only buttons inject sample chat messages so we can watch the bars update.

### Manual end-to-end harness

- A small console runner that constructs `TwitchIrcChatService`, connects to a private test channel, opens a fake vote, prints incoming `ChatMessage`s and `TallyChanged` events to stdout. We type `#1` from a second Twitch window to validate the live IRC path including chat receipts.

### Mechanics

- Tests live in `slay-the-streamer-2.tests/` (xUnit, `net9.0`).
- Source-referenced (not DLL-referenced) so internals are testable without `InternalsVisibleTo` gymnastics.
- Mock/inject `IClock` (`SystemClock` default; `FakeClock` in tests) to advance time deterministically.
- Mock/inject `Random` similarly so tie-break tests are repeatable.

## Logging

- All TI-layer logs go through `MegaCrit.Sts2.Core.Logging.Log`. This is the one StS2-specific dependency we accept in `Ti/*`; when the layer is later extracted to a base mod, the dependency stays since every StS2 mod has it.
- Log levels:
  - `Info` for connect/disconnect, vote open/close, IRC retry attempts.
  - `Warn` for anonymous-mode send attempts, mid-vote disconnects, malformed IRC lines.
  - `Error` for unrecoverable parser/connection failures (after retry budget exhausted — though retry has no hard budget for v0.1).

## Open items deferred to implementation / later

- **Vote ID format**: defaults to `<label-slug>-<utc-timestamp>` if caller doesn't pass one. Solid enough for logs; revisit if we ever correlate across systems.
- **Streamer-configurable receipt policy**: ship `VoteReceiptPolicy.Default` for v0.1, expose configuration when the broader settings UI is designed.
- **Subscriber-only / mod-only voting filters**: v0.1 = open to all chatters. Subscriber/mod gating is a config feature for later.
- **Reconnect retry budget**: v0.1 retries indefinitely with the capped backoff. If this turns out annoying (e.g. wrong oauth token retries forever), add a max-attempts knob.
- **Twitch IRC oauth source**: how the streamer supplies their oauth token. Out of scope here; covered by the settings/onboarding design.
- **`AbstractModel` vs Harmony for the actual decision-substitution glue**: orthogonal to this layer. Decided per-decision in `Game/DecisionVotes/*` once we've inventoried `AbstractModel`'s virtual method surface (see notes/03 open questions).

## Future work / out of scope for v0.1

- `ChatCommandRouter` middle tier (only when a second consumer mod actually appears).
- Twitch Helix API integration for richer features (channel point redemptions, polls, predictions).
- Whisper / Twitch Extension overlays.
- Lifting `Ti/*` into a separate base-mod assembly. Plan: when the lift happens, `Ti/Chat/`, `Ti/Voting/`, `Ti/Internal/` move to a new csproj; `Ti/Ui/` either moves with them or stays as a slay-the-streamer-2-specific control. Game-side mods then take a manifest dependency on the new base mod. The seam is intentionally drawn so this is a file move + small registration shim, not a refactor.