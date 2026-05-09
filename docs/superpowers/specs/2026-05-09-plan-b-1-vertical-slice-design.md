# Plan B.1 — vertical slice: TwitchIrcChatService + Neow blessing vote

**Date**: 2026-05-09
**Status**: Draft v1 — pre-meta-review
**Scope**: First sub-plan of Plan B. A vertical slice that proves the entire chat-vote architecture in-game end-to-end on a single decision (Neow's blessing). Deliberately narrow: one Harmony patch, real Twitch IRC, minimal in-game UI, JSON-file credentials. The remaining four v0.1 votes (card reward, boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2 (and possibly a B.3 for act-boss's custom screen).
**Predecessors**:
- [`2026-05-08-ti-layer-design-v2.md`](./2026-05-08-ti-layer-design-v2.md) — Plan A (the dependency-free TI core; v2.3 settled).
- [`2026-05-08-plan-b-prep-smoke-test-design-v2.md`](./2026-05-08-plan-b-prep-smoke-test-design-v2.md) — the smoke that proved blocking-prefix-await deadlocks under Godot's main-thread sync context.
- [`META-REVIEW-2026-05-08-plan-b-prep-smoke-test-design.md`](./META-REVIEW-2026-05-08-plan-b-prep-smoke-test-design.md) — the meta-review whose Smoke C predicted (and validated) the deadlock.

> **Architectural hard constraint** (carried forward from the smoke's verdict): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.** The smoke proved this hangs.

## Goals

1. **Validate the architecture in-game end-to-end** for a single decision: streamer launches mod, configures oauth via JSON file, starts a run, reaches Neow, picks any blessing button, vote opens to chat, chat votes via `#0`/`#1`/`#2`, vote closes, chat-chosen blessing applies, game proceeds.
2. **Ship the production `TwitchIrcChatService`** per Plan A v2.3's contract — TLS, CAP REQ tags+commands, send queue with rate limiter, reconnect-with-jitter, full state machine. This is the most expensive single piece in v0.1; getting it done in B.1 unlocks every subsequent vote in B.2.
3. **Surface enough streamer-facing visibility** to make B.1 demonstrable: an in-game Label showing all vote options + tally counts + remaining seconds, plus a one-line "connected" PRIVMSG to chat on successful Twitch connection.
4. **Establish the Harmony patch shape** for event-based votes via `NEventRoom.OptionButtonClicked`. This pattern will be reused for B.2's card-reward / event-style votes; getting it right once de-risks the rest.
5. **Fail soft on every credentialing error** — missing file, malformed JSON, bad oauth, mid-vote disconnect. Mod stays loaded; game keeps running; votes silently no-op when chat isn't available. No half-loaded states, no crashes.

## Non-goals

- The other four v0.1 Harmony patches (card reward, boss relic, map path, act-boss). All deferred to B.2+.
- In-game settings panel for oauth/channel/policy configuration. Deferred to B.2; B.1 reads from a JSON file.
- Polished `VoteOverlayControl` (animated bars, percentages, autohide fade, winner-highlight effect). B.1 ships a Label-only indicator; B.2 (or later polish) replaces with the full overlay.
- `ChatStatusControl` (in-game connection-status indicator). Deferred; B.1 surfaces via TiLog + a one-line chat receipt only.
- In-game error toast for auth failure. Deferred.
- Localised receipts (English-only via Plan A's `EnglishReceipts`).
- Any handling of the streamer escaping out of the Neow screen mid-vote — B.1 acknowledges this as a known sharp edge; B.2 or later polish addresses.
- IRC fixture-generator tool (Plan A's optional enhancement #6) — post-MVP.

## Decisions (from session-3 brainstorming, 2026-05-09)

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.1 is a single sub-plan; vertical slice; one Harmony patch.** | The smallest decomposition that's still smaller-than-monolithic. Validates the whole architecture (IRC + dispatcher + voting + Harmony + UI seam) in-game on one decision before fanning out to five more in B.2. |
| 2 | **First patch is Neow blessing.** | Single-shot decision per run; small fixed option count (typically 3 — verified from `Neow.GenerateInitialOptions` returning 1 curse + 2 positive); no skip option; no stacking; same target Tempus's StS1 mod hit (de-risks the pattern). Slow iteration (one shot per run) but fastest path from "launch game" to "see vote happen" (~60s). |
| 3 | **Patch target: `NEventRoom.OptionButtonClicked(EventOption option, int index)` Prefix.** | Single intercept point at the UI layer for any event-based decision. Filter by "current event is `Neow`" so card events / future events pass through. `EventOption.Chosen()` was considered but is fire-and-forget (`TaskHelper.RunSafely(option.Chosen())` at the call sites) — returning a longer Task wouldn't actually suspend the game. `OptionButtonClicked` is synchronous (`void`); the suspend-and-resume pattern is necessary. |
| 4 | **Suspend-and-resume pattern with two-flag re-entry guard.** | Prefix returns `false` after firing `_ = HandleVoteAsync(...)`. Background task awaits the vote, then `dispatcher.Post(...)` re-invokes `OptionButtonClicked(winnerOption, winnerIndex)`. Two static flags (Interlocked): `_voteInProgress` (set across the whole vote) makes repeat-clicks during the vote return `false` (suppress, no second call to original); `_resumeInProgress` (set ONLY around the resume's `OptionButtonClicked` call) makes the prefix return `true` so the chat-chosen click goes through to the original. Single-flag designs misroute repeat-clicks; the spec's first draft had this bug — caught in self-review. |
| 5 | **Credentials: JSON file at user-data path.** | `%APPDATA%\Godot\app_userdata\Slay the Spire 2\slay_the_streamer_2.json` (Linux/macOS equivalents via `Environment.SpecialFolder.ApplicationData`). Easy to swap channels/tokens between testing sessions; natural target for B.2's settings UI to persist to. Settings panel deferred to B.2; B.1 just reads. |
| 6 | **In-game vote UI: minimal multi-line Label.** | `Ti/Ui/VoteTallyLabel.cs` — a `Label` (or `RichTextLabel`) Godot Control showing all options + counts + time remaining. Visible in the game window (captured by OBS — visible to everyone watching the stream, not streamer-only). No bars, no animations, no winner-highlight, no autohide fade. Polish deferred to B.2 / `VoteOverlayControl`. |
| 7 | **Connection-status feedback: TiLog + one-line chat receipt on connect.** | On successful Twitch connect, send `"slay-the-streamer-2 connected — votes will go to <channel>"` to the channel as a `OutgoingMessagePriority.High` message. Streamers with chat overlay open see it instantly. Auth failure logs at Error and stays silent in-chat. In-game status indicator (`ChatStatusControl`) deferred to B.2. |
| 8 | **v0.1 scope confirmed at 5 votes** (Neow + card reward + boss relic + map path + act-boss). | Verified from Tempus's StS1 source (2026-05-09): original mod's votes were Neow + boss relic + act-boss only; card reward came from the underlying `de.robojumper.ststwitch` base mod; event-choice / shop-purchase / map-path were not anywhere. We keep card reward + map path (well-understood patterns). Event-choice + shop-purchase are deferred to v0.2 as new-design problems. Act-boss is in v0.1 but heavyweight (custom screen replacing post-treasure-room flow); likely needs its own sub-plan. |
| 9 | **Failure modes degrade silently to "vanilla game".** | Missing JSON / malformed / bad oauth / mid-vote disconnect: mod stays loaded, `Voter.Default` may be null (or chat may be in `AuthenticationFailed` / `Disconnected`), Harmony prefix sees the unhealthy state and returns `true` to let original run. Game plays normally without chat votes. No crash, no half-broken state. |

## Architecture

```
src/
├── Ti/                                          [Plan A — extractable, BCL+Godot only in Ui/Godot subns]
│   ├── Chat/
│   │   └── TwitchIrcChatService.cs              🆕 B.1   full impl per Plan A v2.3 §"ChatService"
│   ├── Ui/                                      🆕 B.1   new sub-namespace; Godot-dependent
│   │   └── VoteTallyLabel.cs                    🆕 B.1   Godot Control, multi-line text indicator
│   ├── Voting/                                  ✅ Plan A complete
│   ├── Internal/                                ✅ Plan A complete
│   ├── Chat/Internal/                           ✅ Plan A complete (parser, OutgoingMessageQueue, retry policy)
│   └── Godot/                                   ✅ Plan B prep complete (DispatcherAutoload, GodotMainThreadDispatcher)
├── Game/                                        🆕 B.1   StS2-specific glue; not extractable
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       🆕 B.1   JSON config reader; returns SettingsResult
│   └── DecisionVotes/
│       └── NeowBlessingVotePatch.cs             🆕 B.1   Harmony Prefix on NEventRoom.OptionButtonClicked
└── ModEntry.cs                                  ✏️  B.1   extend skeleton: load settings, build chat,
                                                          wire Voter.Default, attach VoteTallyLabel, connect

tests/
├── Chat/
│   └── TwitchIrcChatServiceTests.cs             🆕 B.1   deterministic parts: lifecycle, queue interactions,
│                                                          retry policy invocations using FakeClock+FakeTimerScheduler
└── Bootstrap/
    └── ModSettingsTests.cs                      🆕 B.1   JSON parse, missing-file, malformed,
                                                          oauth-prefix normalisation, channel-name normalisation
```

**Net new code estimate**: TwitchIrcChatService ~600 LOC source + ~150 LOC tests; VoteTallyLabel ~50 LOC; NeowBlessingVotePatch ~100 LOC; ModSettings ~60 LOC + ~80 LOC tests; ModEntry additions ~40 LOC. Total ~1,000 LOC of source, ~230 LOC of tests.

**Allowed dependencies** (carried forward from Plan A; restated to set expectations for review):
- `Ti/Chat/`, `Ti/Voting/`, `Ti/Internal/`, `Ti/Chat/Internal/` may reference: BCL only. No Godot, no `sts2.dll`.
- `Ti/Ui/` may reference: BCL + Godot. No `sts2.dll`.
- `Ti/Godot/` may reference: BCL + Godot. No `sts2.dll`.
- `Game/*` may reference everything (BCL + Godot + `sts2.dll` + `Ti/*`).
- `Game/*` MUST NOT be referenced from `Ti/*`. Code-review enforcement; Roslyn analyser is post-MVP.

The lift-to-base-mod future (TI extraction goal) stays clean: `TwitchIrcChatService` is in `Ti/Chat/` and depends only on Plan A interfaces; `VoteTallyLabel` is in `Ti/Ui/` and depends only on Plan A's `VoteSession` plus Godot. Neither references StS2 types.

## `TwitchIrcChatService` (Plan A v2.3 implementation)

This is the headline net-new piece in B.1: the full production IRC client per Plan A's spec. No new design; B.1 is implementation. The contract is `IChatService` (already in `Ti/Chat/`), and `OutgoingMessageQueue` + `TwitchIrcParser` + `ConnectionRetryPolicy` are already built.

**What B.1 must implement** (carried verbatim from Plan A v2.3 §"ChatService"):

- TLS connection to `irc.chat.twitch.tv:6697` via `SslStream`.
- TCP framing via `StreamReader.ReadLineAsync` over the TLS stream — handles fragmentation correctly.
- Capability negotiation: `CAP REQ :twitch.tv/tags twitch.tv/commands`. Falls back to no-tags mode on `CAP NAK`.
- Login: `PASS oauth:<token>` + `NICK <username>` + `JOIN #<channel>`. Channel input is normalised (accepts `foo`, `#foo`, `https://twitch.tv/foo`, `http://twitch.tv/foo`, etc.).
- Anonymous-read mode (`creds == null`): `NICK justinfan{rand6}`. State transitions to `ConnectedReadOnly`; `CanSend == false`. **B.1 doesn't exercise this path**, but the implementation must not break it.
- Read loop on a background `Task` with `CancellationToken`. Lines pass through `TwitchIrcParser` to `ChatMessage` events (or are routed by command type per Plan A's IRC protocol-handling matrix).
- Self-echo guard: drops `parsed.UserId == self.UserId`.
- Outgoing send: every send goes through `OutgoingMessageQueue` (already built; enforces token-bucket rate limit + priority).
- All inbound events (`MessageReceived`, `ConnectionStateChanged`) flow through `IMainThreadDispatcher.Post(...)` before being raised, so subscribers always observe events on the Godot main thread.
- Reconnect with exponential backoff + jitter (5/10/20/40/60s, ±20% jitter); auth failure and join failure are terminal.
- IRC protocol-matrix handling per Plan A v2.3 (PING/PONG, RECONNECT, NOTICE auth-failure, NOTICE channel-banned, CAP ACK/NAK, USERSTATE, ROOMSTATE optional, CLEARCHAT/CLEARMSG ignore, USERNOTICE ignore, unknown/malformed Debug-log + counter).
- Diagnostic state: `LastMessageReceivedAt`, `LastError`, `State`, `IsConnected`, `CanSend`.
- `Dispose` semantics per Plan A's "Lifecycle / shutdown sequence" section.

**B.1 testing depth** for `TwitchIrcChatService`:
- Unit tests cover the deterministic state-machine transitions using `FakeClock` + `FakeTimerScheduler` + a stubbed `Stream` (or a small `IIrcTransport` interface introduced just for testability — implementation detail; not a public spec change).
- The actual TLS socket I/O is operator-validated against a real Twitch test channel.
- The IRC parser already has a 30+ test corpus from Plan A.

## `ModSettings` (Bootstrap)

```csharp
namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(string Channel, ChatCredentials Credentials);

public abstract record SettingsResult {
    public sealed record Success(ChatSettings Settings) : SettingsResult;
    public sealed record Missing(string Path) : SettingsResult;
    public sealed record Malformed(string Path, string Reason) : SettingsResult;
}

public static class ModSettings {
    /// <summary>Returns the resolved JSON path on this OS. ApplicationData = %APPDATA% on Windows.</summary>
    public static string GetSettingsPath();

    /// <summary>Reads + validates settings. Always succeeds (returns a SettingsResult); never throws.</summary>
    public static SettingsResult Load();
}
```

**JSON shape** (v0.1):

```json
{
    "channel": "surfinite",
    "username": "surfinitebot",
    "oauthToken": "oauth:abc123def456..."
}
```

- `channel`: any of `foo`, `#foo`, `https://twitch.tv/foo` accepted; normalised internally per Plan A's channel-normalisation rule.
- `username`: lowercased Twitch login of the bot account that owns the oauth token.
- `oauthToken`: accepts either `oauth:abc123` or bare `abc123`; normalised to bare via `ChatCredentials` ctor.

**Validation**:
- Empty / whitespace-only any field → `Malformed` with reason.
- Empty file or malformed JSON → `Malformed` with reason.
- File doesn't exist → `Missing(path)`. Caller logs at Info (not Error — first-launch state).

**Sample file generation** (out of scope for B.1 but worth flagging): when the file is missing, B.1 logs the expected path and the JSON shape so the streamer can hand-create it. B.2's settings UI replaces this with a generated file.

## `NeowBlessingVotePatch` (Harmony — the load-bearing piece)

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NeowBlessingVotePatch {
    private static int _voteInProgress;          // 0/1 — set for the whole vote duration
    private static int _resumeInProgress;        // 0/1 — set ONLY around the resume's OptionButtonClicked call

    /// <summary>
    /// Fail loudly if the patch target moved between game versions.
    /// Returns false on null original — Harmony handles the class-level Prepare check
    /// by passing original=null; we MUST return true on null per Plan A's gotcha note.
    /// </summary>
    static bool Prepare(MethodBase? original) {
        if (original is null) return true;       // class-level check — process this class
        TiLog.Info($"[neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        // Resume re-entry: our own dispatcher.Post is calling OptionButtonClicked with the
        // chat-chosen option. Let the original run so the click actually applies.
        if (_resumeInProgress == 1) return true;

        // Filter: only intercept for Neow events.
        if (!IsNeowEvent(__instance)) return true;

        // Filter: don't intercept locked / proceed options (they bypass the vote layer).
        if (option.IsLocked || option.IsProceed) return true;

        // Mod not configured / chat unhealthy → vanilla behavior (let player's click apply).
        if (Voter.Default is null) return true;
        if (!Voter.Default.Chat.IsConnected) return true;

        // Repeat click during a vote in progress: suppress (NEITHER vote again NOR call
        // original — original would invoke ChooseLocalOption and race our pending resume).
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[neow-vote] repeat click during open vote — suppressed");
            return false;
        }

        var allOptions = GetCurrentOptions(__instance);
        if (allOptions is null || allOptions.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        TiLog.Info($"[neow-vote] opening vote for {allOptions.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(__instance, allOptions, index);
        return false;  // skip original — we'll resume via dispatcher.Post when vote completes
    }

    private static async Task HandleVoteAsync(NEventRoom room, IReadOnlyList<EventOption> options, int playerClickIndex) {
        try {
            var labels = options.Select(o => o.Title.GetRawText()).ToList();
            var session = Voter.Default!.Start(
                label: "Neow's Bonus",
                options: labels,
                duration: TimeSpan.FromSeconds(30));

            // Attach the in-game tally label.
            ModEntry.Dispatcher.Post(() => VoteTallyLabel.AttachTo(room, session));

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= options.Count) {
                TiLog.Warn($"[neow-vote] winnerIndex {winnerIndex} out of range; falling back to player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[neow-vote] resume: applying winner #{winnerIndex} on main thread");
            ModEntry.Dispatcher.Post(() => {
                // Re-enter OptionButtonClicked with the chat-chosen option. _resumeInProgress
                // makes the prefix pass through to the original. Reset _voteInProgress AFTER
                // the resume completes so any further clicks re-trigger normal flow.
                Interlocked.Exchange(ref _resumeInProgress, 1);
                try {
                    room.OptionButtonClicked(options[winnerIndex], winnerIndex);
                } finally {
                    Interlocked.Exchange(ref _resumeInProgress, 0);
                    Interlocked.Exchange(ref _voteInProgress, 0);
                }
            });
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] HandleVoteAsync threw", ex);
            // Reset _voteInProgress so the streamer can re-click without restarting the game.
            ModEntry.Dispatcher.Post(() => Interlocked.Exchange(ref _voteInProgress, 0));
        }
    }

    private static bool IsNeowEvent(NEventRoom room) {
        // Implementation detail: AccessTools-read the private _event field, check
        // event.GetType() == typeof(Neow). Spec defers exact mechanism to implementation.
        // Decompile-search confirms: NEventRoom._event is the EventModel; Neow extends
        // AncientEventModel (decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs).
        throw new NotImplementedException("see implementation plan");
    }

    private static IReadOnlyList<EventOption>? GetCurrentOptions(NEventRoom room) {
        // Implementation detail: AccessTools-read _event, then EventModel.CurrentOptions
        // (verified to exist on EventModel via NEventRoom.cs:200).
        throw new NotImplementedException("see implementation plan");
    }
}
```

**Sequence diagram** (the suspend-and-resume in motion):

```
[Main thread]  Player clicks blessing option #1
[Main thread]  StS2: NEventRoom.OptionButtonClicked(option, 1) is called
[Main thread]  Harmony prefix fires:
                 ├─ _voteInProgress == 0 → set to 1
                 ├─ event is Neow ✓; option not locked/proceed ✓; Voter.Default ✓; chat connected ✓
                 ├─ allOptions = GetCurrentOptions(room)  → 3 options
                 ├─ _ = HandleVoteAsync(room, allOptions, 1)  ← fire-and-forget
                 └─ return false  ← SUSPEND (skip original; main thread freed immediately)

[Threadpool]   HandleVoteAsync begins:
                 ├─ Voter.Default.Start("Neow's Bonus", labels, 30s)
                 │    └─ open receipt enqueued; periodic timer scheduled
                 ├─ dispatcher.Post(VoteTallyLabel.AttachTo)  ← UI attach on main thread
                 └─ await session.AwaitWinnerAsync()  ← awaits TCS

[Main thread]  Next idle frame: VoteTallyLabel attached as child of room
[Main thread]  Each subsequent frame: VoteTallyLabel._Process polls session, redraws
[Threadpool]   Chat sends #0 / #1 / #2; OutgoingMessageQueue drains receipts;
               IRC client raises MessageReceived events via dispatcher (main thread)
[Main thread]  VoteSession tallies on main thread; raises TallyChanged
[Threadpool]   30s elapse; close timer fires on threadpool; dispatcher.Post(CloseNowInternal)
[Main thread]  Next idle frame: CloseNowInternal runs;
                 ├─ winner computed
                 ├─ TCS.TrySetResult(winnerIndex) — RunContinuationsAsynchronously
                 └─ Closed event fires

[Threadpool]   await resumes with winnerIndex; HandleVoteAsync continues:
                 └─ dispatcher.Post(resumeAction)  ← RESUME enqueued

[Main thread]  Next idle frame: resumeAction runs:
                 ├─ _resumeInProgress = 1
                 ├─ room.OptionButtonClicked(winnerOption, winnerIndex)  ← re-enters our prefix
                 │    └─ prefix sees _resumeInProgress == 1 → returns true → original runs;
                 │       EventSynchronizer.ChooseLocalOption(winnerIndex) → eventually option.Chosen()
                 │       (asynchronously kicks off via TaskHelper.RunSafely; main thread continues)
                 ├─ _resumeInProgress = 0
                 └─ _voteInProgress = 0  ← any further clicks now retrigger normal flow
```

**Why this never deadlocks** (the property the smoke proved we need):
- Prefix never blocks. Returns `false` immediately after firing the async task.
- `await session.AwaitWinnerAsync()` never blocks the main thread — the await happens on the threadpool because RunContinuationsAsynchronously is set on the underlying TCS.
- Resume is via `dispatcher.Post`, which under Godot uses `CallDeferred` to queue for the next idle frame. The main thread is always free to process idle frames.
- The reset of `_voteInProgress` is also via dispatcher.Post, ordered AFTER the resume Post, so the resume's re-entry sees the flag set and falls through.

## `VoteTallyLabel` (Ti/Ui)

```csharp
namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : Label {  // or RichTextLabel for richer formatting
    private VoteSession? _session;

    public static void AttachTo(Node parent, VoteSession session) {
        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label._session = session;
        // Anchor top-right by default; Surfinite will adjust positioning during polish.
        label.AnchorLeft = 0.6f; label.AnchorTop = 0.05f;
        label.AnchorRight = 0.98f; label.AnchorBottom = 0.4f;
        parent.AddChild(label);

        session.Closed += (_, _) => label.QueueFree();
        session.Cancelled += (_, _) => label.QueueFree();
    }

    public override void _Process(double delta) {
        if (_session is null || _session.State is VoteSessionState.Closed
                                              or VoteSessionState.Cancelled
                                              or VoteSessionState.Disposed) return;

        var sb = new StringBuilder();
        sb.AppendLine($"Chat voting — {(int)_session.TimeRemaining.TotalSeconds}s left");
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
        Text = sb.ToString();
    }
}
```

**Properties**:
- Polls `session` from `_Process` each frame (Plan A's prescribed pattern for UI controls).
- Subscribes only to `Closed` + `Cancelled` to self-remove via `QueueFree()`.
- No bars, no percentages, no winner-highlight, no autohide fade. Polish deferred to `VoteOverlayControl`.
- Position is hardcoded top-right for B.1; Surfinite will adjust during polish (per session-3 brainstorming).
- Lives in `Ti/Ui/` because it depends only on Plan A's `VoteSession` + Godot — no StS2 types.

**Threading**: `_Process` runs on the Godot main thread. Reads of `session.Tallies` / `session.TimeRemaining` are on the same thread that mutates them (per Plan A's threading guarantee that `VoteSession` events fire on the dispatcher thread). Safe.

## `ModEntry` extensions

Building on the existing skeleton at [`src/ModEntry.cs`](../../../src/ModEntry.cs) (sections 1–6 already wired by Plan B prep):

```csharp
// Existing sections (unchanged):
//   1. Resolve SceneTree
//   2. Attach DispatcherAutoload via deferred add_child
//   3. Optional Engine.RegisterSingleton
//   4. Wire IMainThreadDispatcher
//   5. TiLog.Sink → MegaCrit.Sts2.Core.Logging.Log passthrough

// NEW in B.1 — section 6 (replacing the empty Harmony.PatchAll block):

// 6. Load settings.
var settingsResult = ModSettings.Load();
ChatSettings? settings = null;
switch (settingsResult) {
    case SettingsResult.Success s:
        settings = s.Settings;
        Log.Info($"[slay_the_streamer_2] settings loaded; channel=#{settings.Channel}");
        break;
    case SettingsResult.Missing m:
        Log.Info($"[slay_the_streamer_2] no settings file at {m.Path}; mod loaded but Twitch not connected. " +
                 $"Create the file with: {{ \"channel\": \"...\", \"username\": \"...\", \"oauthToken\": \"oauth:...\" }}");
        break;
    case SettingsResult.Malformed m:
        Log.Error($"[slay_the_streamer_2] settings file at {m.Path} is malformed: {m.Reason}. Mod loaded but not connecting.");
        break;
}

// 7. Build TI services.
var clock = new SystemClock();
var scheduler = new SystemTimerScheduler();  // note: no-arg ctor per Plan B prep gotcha
Dispatcher = dispatcher;  // promote local to static field for Harmony patches

if (settings is not null) {
    var chat = new TwitchIrcChatService(dispatcher, clock);
    var coordinator = new VoteCoordinator(chat, dispatcher, scheduler, clock);
    Voter.Default = coordinator;

    // Send "connected" receipt on successful connect.
    chat.ConnectionStateChanged += (_, e) => {
        if (e.NewState is ChatConnectionState.ConnectedReadWrite && e.OldState is not ChatConnectionState.ConnectedReadWrite) {
            _ = chat.SendMessageAsync(
                $"slay-the-streamer-2 connected — votes will go to #{settings.Channel}",
                OutgoingMessagePriority.High);
        }
    };

    // 8. Connect non-blocking (matches Plan A's lifecycle).
    _ = chat.ConnectAsync(settings.Channel, settings.Credentials);
}

// 9. Apply Harmony patches (existing Plan B prep code; now finds NeowBlessingVotePatch).
var harmony = new Harmony("slay_the_streamer_2");
harmony.PatchAll(Assembly.GetExecutingAssembly());
// existing patch-count diagnostic logging stays as-is.
```

The existing top-level `try/catch` from Plan B prep wraps everything; nothing in B.1 changes the blast-radius bound.

`ModEntry.Dispatcher` becomes a public static field (was a local in the skeleton). `NeowBlessingVotePatch.HandleVoteAsync` reads it for the resume `Post`.

## Failure modes & graceful degradation

The promise: **mod loads, game runs, no crash** in every failure path. The streamer can always exit-and-relaunch to retry.

| Failure | What happens | Streamer experience |
|---|---|---|
| Settings JSON file doesn't exist | `ModSettings.Load()` returns `Missing`. `Voter.Default` stays null. Patch prefix returns `true`. | Game plays vanilla; no chat votes. Log says where to put the file. |
| Settings JSON is malformed | `Load()` returns `Malformed` with reason. Same as Missing for runtime behavior. | Game vanilla; log lists the parse error. |
| Settings has bad oauth | IRC connects, hits `NOTICE * :Login authentication failed`, transitions to `AuthenticationFailed`. `Voter.Default` is set but `chat.IsConnected == false`. Patch prefix returns `true`. | Game vanilla; log says "auth failed; check oauth token". B.2's settings UI adds re-auth without restart. |
| Channel doesn't exist / mod is banned | IRC reaches `JoinFailed`. `chat.IsConnected == false`. Patch returns `true`. | Game vanilla; log says join failed. |
| Network failure on connect | IRC retries with exponential backoff + jitter forever (per Plan A v2.3). `chat.IsConnected` toggles `false`/`true` over time. | Game vanilla until a connection lands; once chat is up, votes start working. |
| Mid-vote disconnect | Plan A's reconnect logic fires; VoteSession keeps tallying received messages; close receipt notes the disconnect gap (per Plan A v2.3 EnglishReceipts). | Vote completes normally. Chat receipt mentions "(chat was offline 8s during voting)" if there was a gap. |
| `Voter.Default.Start` throws (e.g., race condition; another vote unexpectedly open) | Caught in `HandleVoteAsync`'s outer try/catch; logged at Error; `_voteInProgress` reset; player click is lost (no chat resume, no original run because we returned false). **Sharp edge — flagged.** | Player's click "did nothing"; they re-click; second click works (flag reset). Bad UX but not a crash. |
| `AwaitWinnerAsync` throws (cancellation, etc.) | Caught in HandleVoteAsync; falls back to `playerClickIndex` (the option the streamer originally clicked). Resume Post still happens. | Game proceeds with the streamer's original click as if no chat vote happened. |
| Streamer escapes out of Neow screen mid-vote | Undefined behavior. Vote keeps running in background; resume Post will eventually call `OptionButtonClicked` on a screen that no longer exists. May throw inside StS2; HandleVoteAsync's catch handles it with an Error log. **Known sharp edge.** | Probably visually OK (next time they re-enter Neow they get fresh options) but log will show an exception. B.2 / polish addresses by cancelling the vote on screen-close. |

## Testing strategy

### Plan A regression (must stay green)
- All 142 existing tests pass. B.1 adds tests; nothing in `Ti/` core changes.

### New unit tests (~230 LOC, ~25 tests)

**`ModSettingsTests`** (~80 LOC, ~12 tests):
- Valid JSON parses to `Success` with correct `ChatSettings`.
- File missing returns `Missing(expectedPath)`.
- Empty file returns `Malformed`.
- Malformed JSON returns `Malformed` with parse error in reason.
- Empty/whitespace `channel` field returns `Malformed`.
- Empty `username` returns `Malformed`.
- Empty `oauthToken` returns `Malformed`.
- `oauth:abc123` and `abc123` token forms both normalise to bare `abc123` via ChatCredentials.
- Channel `foo` / `#foo` / `https://twitch.tv/foo` all normalise to `foo` (via ChatCredentials normalisation already tested in Plan A — just verify the Settings glue passes through).
- `GetSettingsPath()` returns expected platform-specific path (one test per OS would be nice but not blocking).

**`TwitchIrcChatServiceTests`** (~150 LOC, ~13 tests):
- Implementation can introduce an `IIrcTransport` (or similar) test seam to inject a fake stream — keep it `internal` so it doesn't widen the public API.
- State machine: Disconnected → Connecting → ConnectedReadWrite on PASS+NICK+JOIN success.
- Auth-failure NOTICE → terminal `AuthenticationFailed`; no retry.
- Channel-banned NOTICE → terminal `JoinFailed`; no retry.
- Network failure mid-stream → Reconnecting → ConnectedReadWrite (with FakeClock advancing the backoff).
- Backoff sequence respects exponential schedule + jitter bounds (5–6s, 10–12s, etc.).
- PING from server → PONG sent within the read loop.
- RECONNECT command → graceful disconnect + immediate reconnect.
- Self-echo (parsed.UserId == self.UserId) → MessageReceived NOT raised.
- `MessageReceived` events flow through `IMainThreadDispatcher.Post` (test with `ImmediateDispatcher` and verify dispatcher was called).
- `LastMessageReceivedAt` updated on every PRIVMSG.
- Anonymous mode: `creds == null` → connects with `NICK justinfan*`; `CanSend == false`; `SendMessageAsync` returns failed task.
- `Dispose` closes socket + cancels read loop + drains queue.

### Operator-validated (manual; this is the smoke equivalent for B.1)

**Step 1 — IRC alone**: with the mod loaded but no Neow patch firing yet (or with patch disabled via missing settings), point `TwitchIrcChatService` at a throwaway test channel + Surfinite's testbot oauth. From a second Twitch client (testbot's web chat, or another bot), send messages. Verify in mod log: `MessageReceived` fires, sent messages appear in the channel. Run BEFORE in-game testing — if IRC is broken, the Neow patch will look broken too.

**Step 2 — Full Neow vote in-game**: launch StS2 → start a run → reach Neow → click any blessing → confirm:
- In-game `VoteTallyLabel` appears showing all 3 options + counts (initially 0) + 30s remaining.
- Chat receipt posted to the channel: `"Vote: Neow's Bonus! Type 0, 1 or 2 — 30s left."` (or compact-labels variant per Plan A's policy).
- From the second client, send `#0` / `#1` / `#2`. Tally label updates in-game; periodic chat receipts reflect the running tally.
- Vote closes; close receipt posted (e.g., `"Chat chose 1: <blessing label>."`).
- Winning blessing applies; game proceeds to next phase (deck construction, run start, etc.).
- Streamer's repeat-clicks during the vote no-op cleanly (no double-vote, no error spam).

**Step 3 — Failure-mode operator-validation**:
- **No settings file**: rename/move the JSON file. Launch game. Verify mod log shows clear "no settings" message; reach Neow; vote does NOT happen; Neow plays normally; streamer's click applies as usual.
- **Bad oauth**: edit JSON file, mangle the oauth token. Launch. Verify log shows AuthenticationFailed; Neow plays normally.
- **Mid-vote disconnect**: open Neow; start vote; manually disconnect Wi-Fi or block port 6697 mid-vote (simulate via `netsh advfirewall` or yank ethernet); restore connection within ~15s. Verify reconnect happens, votes after reconnect tally, close receipt notes the disconnect gap (e.g., `"...(chat was offline 8s during voting)."`).

### Acceptance gate ("B.1 done")

All of:
- [ ] All 142 Plan A tests pass.
- [ ] All new unit tests pass.
- [ ] Step 1 IRC operator-validation green.
- [ ] Step 2 full Neow vote operator-validation green.
- [ ] Step 3 failure-mode operator-validation green for at least: no settings, bad oauth, mid-vote disconnect.
- [ ] `notes/06-followups-and-deferred.md` updated with B.1 outcome (resolved items, new known sharp edges, what's left for B.2).

## Open items / decompile-search to do during implementation

Items the spec deliberately leaves to implementation because they're either small-and-mechanical or need reading the live API:

1. **`NEventRoom._event` private field access**. Decompile-search confirms the field exists ([`NEventRoom.cs:200`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NEventRoom.cs#L200) reads `eventModel.CurrentOptions`). Implementation uses `AccessTools.Field(typeof(NEventRoom), "_event")` to read it from the patch.
2. **`EventModel.CurrentOptions` property accessibility**. Confirmed from [`NEventRoom.cs:200`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NEventRoom.cs#L200): public. No reflection needed.
3. **`IsNeowEvent(NEventRoom)` exact check**. Read `_event`, check `event is Neow` (the Neow class lives at [`Core/Models/Events/Neow.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs)).
4. **`EventOption.Title.GetRawText()`** is the assumed API for converting `LocString` to display text — verified from `EventOption.ToString()` (decompile shows `Title.GetRawText()` used). Implementation should confirm this is the right call vs `Title.ToString()`.
5. **Where `ModEntry.Dispatcher` is exposed**. Currently a local in `Init()`; B.1 promotes to `public static GodotMainThreadDispatcher Dispatcher`. NeowBlessingVotePatch reads it.
6. **Multiplayer Neow shape**. Decompile-search shows `NEventRoom.OptionButtonClicked` routes through `EventSynchronizer.ChooseLocalOption(index)` for non-proceed options, and `EventSynchronizer` deserialises an `IsShared` field to coordinate multiplayer. v0.1 is single-player only; B.1 patch should bail out (return `true`) if `_event.Owner.RunState.Players.Count > 1` to avoid surprising multiplayer co-op players. Add a Warn log on bail-out.
7. **`SystemTimerScheduler` constructor**. Plan B prep gotcha: `new SystemTimerScheduler()`, not `new SystemTimerScheduler(clock)`. Already noted in the handoff; surface here so spec reviewers don't think it's a typo.
8. **Test seam for TwitchIrcChatService TLS injection**. Implementation introduces an internal `IIrcTransport` (or similar) abstraction so the lifecycle/state-machine tests can inject a fake stream without going through real TLS. Public `IChatService` API stays unchanged.

## Risks & assumptions

- **`NEventRoom.OptionButtonClicked` signature is stable across the beta/stable game branches.** Stable was verified via the smoke (we're on stable now); beta drift was largely in `AbstractModel`, not `NEventRoom`. Low risk.
- **The two-flag re-entry guard handles repeat clicks safely.** Both `_voteInProgress` and `_resumeInProgress` are reset inside the same dispatcher.Post action that runs the resume — they cannot get out of order with the resume call itself. Worst case: a player click fires between idle frames AFTER our resume's `Interlocked.Exchange(_voteInProgress, 0)` runs but BEFORE the chat-chosen blessing's effects have fully settled. That click would trigger a fresh vote (returning false, no original call), which would race against in-progress effect application. Sharp edge: in practice, blessing effects take >1 frame to apply (Chosen() runs async), so a fast streamer click could hit this. If observed, mitigation is to keep `_voteInProgress = 1` until the resume's `option.Chosen()` task completes — but that requires awaiting a task we don't currently capture. Flagged for operator-validation.
- **Godot's main-thread SynchronizationContext doesn't pump `CallDeferred` during the frame the prefix returns.** The smoke proved this is the source of the original deadlock. We avoid the trap by not blocking — prefix returns immediately. The await in HandleVoteAsync runs on threadpool because RunContinuationsAsynchronously is set on the TCS. Resume is via dispatcher.Post, which queues for the NEXT frame, not the current one. Safe.
- **`ChatCredentials.ToString` redaction is sufficient defense-in-depth on top of `TiLog`'s oauth scrubber**. Plan A v2.3 settled this. B.1 inherits both layers.
- **The streamer's click during the vote doesn't accidentally trigger a second `OptionButtonClicked` invocation that races with our HandleVoteAsync's Post**. Tested in operator-validation step 2; if it does, the `Interlocked.CompareExchange` guard fails and the second click no-ops (returns `true`, original runs). The race window is between "background Post enqueued" and "main thread runs Post"; during that window, a fresh click would race. Worst case: chat-chosen blessing AND streamer's late click both attempt to apply. Need to verify in-game; if it's a problem, add additional guard (e.g., `room.Layout.DisableEventOptions()` after vote opens).
- **Plan A's `RunContinuationsAsynchronously` TCS pattern works as designed** — the smoke didn't actually validate it works for non-blocking awaits, only that blocking awaits deadlock. B.1 is the first real test of the non-blocking case. Risk: if Godot's SynchronizationContext does something unexpected with the await continuation, the resume might land on the wrong thread or never run. Operator-validation will catch.

## What "B.1 done" unlocks

When B.1's acceptance gate is green:
- The TI core is in production (B.2 just adds patches; no IRC work).
- The Harmony suspend-and-resume pattern is validated in real game state mutation (not just a no-op smoke).
- The dispatcher's `_voteInProgress` re-entry pattern is proven (B.2 patches reuse the shape).
- The credentials story works end-to-end (B.2 adds a settings UI on top of the same JSON file).
- The TI/Game seam holds under real use (no leaks of StS2 types into Ti/*).

B.2 then needs: 4 more Harmony patches (card reward, boss relic, map path, act-boss — last of which probably wants its own sub-plan for the custom screen), plus the in-game settings UI.

---

## Process: meta-review workflow

Per the established workflow ([`workflow_meta_review.md`](../../../../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/workflow_meta_review.md)):

1. After this spec is committed, run `/document-context` to produce the reviewer-ready context document.
2. Surfinite collects crowd-source LLM reviews via t3.chat (DeepSeek, GPT, Gemini, Claude Opus, Gemma, etc.).
3. Run `/meta-review` once the reviews directory is populated.
4. Apply Must-do + Should-do auto-fixes inline (revising spec to v2 if non-trivial).
5. Surface Optional Enhancements as a pick-list.
6. Run `superpowers:writing-plans` to produce the implementation plan.
7. Execute via `superpowers:subagent-driven-development` (Plan A precedent; per-task commits with `plan-b-1/X.Y:` prefix).
