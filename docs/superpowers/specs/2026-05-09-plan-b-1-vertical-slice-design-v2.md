# Plan B.1 — vertical slice: TwitchIrcChatService + Neow blessing vote (v2)

**Date**: 2026-05-09
**Status**: Draft v2 — post-meta-review, Must-do + Should-do auto-applied
**Predecessor**: [`2026-05-09-plan-b-1-vertical-slice-design.md`](./2026-05-09-plan-b-1-vertical-slice-design.md). See [`META-REVIEW-2026-05-09-plan-b-1-vertical-slice-design.md`](./META-REVIEW-2026-05-09-plan-b-1-vertical-slice-design.md) for the rationale behind every `<!-- CHANGED: -->` mark below.
**Scope**: First sub-plan of Plan B. A vertical slice that proves the entire chat-vote architecture in-game end-to-end on a single decision (Neow's blessing). One Harmony patch, real Twitch IRC, minimal in-game UI, JSON-file credentials. The remaining four v0.1 votes (card reward, boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2 (and possibly B.3 for act-boss's custom screen).

> **Architectural hard constraint** (carried forward from the smoke's verdict): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.** The smoke proved this hangs.

## Goals

1. **Validate the architecture in-game end-to-end** for a single decision: streamer launches mod, configures oauth via JSON file, starts a run, reaches Neow, picks any blessing button, vote opens to chat, chat votes via `#0`/`#1`/`#2`, vote closes, chat-chosen blessing applies, game proceeds.
2. **Ship the production `TwitchIrcChatService`** per Plan A v2.3's contract — TLS, CAP REQ tags+commands, send queue with rate limiter, reconnect-with-jitter, full state machine, plus a JOIN-confirmation timeout. <!-- CHANGED: JOIN timeout added — Reviewer 2 -->
3. **Surface enough streamer-facing visibility** to make B.1 demonstrable: an in-game multi-line indicator showing all vote options + tally counts + remaining seconds, plus a one-line "connected" PRIVMSG to chat on first successful Twitch connection per process. <!-- CHANGED: "first per process" added — Reviewer 3 -->
4. **Establish the Harmony patch shape** for event-based votes via `NEventRoom.OptionButtonClicked`. This pattern will be reused for B.2's card-reward / event-style votes; getting it right once de-risks the rest.
5. **Fail soft on every credentialing error** — missing file, malformed JSON, bad oauth, mid-vote disconnect, **`Voter.Default.Start` exception**. Mod stays loaded; game keeps running; votes silently no-op or fall back to vanilla when chat isn't available. No half-loaded states, no crashes. <!-- CHANGED: explicit Voter.Start fallback added — Reviewers 2, 3 -->

## Non-goals

- The other four v0.1 Harmony patches (card reward, boss relic, map path, act-boss). All deferred to B.2+.
- In-game settings panel for oauth/channel/policy configuration. Deferred to B.2; B.1 reads from a JSON file.
- Polished `VoteOverlayControl` (animated bars, percentages, autohide fade, winner-highlight effect). B.1 ships a multi-line `RichTextLabel`-based indicator; B.2 (or later polish) replaces with the full overlay. <!-- CHANGED: RichTextLabel — Reviewers 1, 3, 4, 5 -->
- `ChatStatusControl` (in-game connection-status indicator). Deferred to B.2.
- In-game error toast for auth failure. Deferred.
- Localised receipts (English-only via Plan A's `EnglishReceipts`).
- Multiplayer co-op support. B.1 explicitly bails out for `Players.Count > 1`. <!-- CHANGED: promoted from open-items to enforced — Reviewers 2, 5 -->
- Streamer escaping out of Neow screen mid-vote — B.1 detects via resume-time validity checks and drops safely; no graceful cancellation. <!-- CHANGED: now mitigated rather than acknowledged-as-undefined — Reviewers 2, 3 -->
- IRC fixture-generator tool (Plan A's optional enhancement #6) — post-MVP.

## Decisions (from session-3 brainstorming + meta-review v2)

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.1 is a single sub-plan; vertical slice; one Harmony patch.** | The smallest decomposition that's still smaller-than-monolithic. Validates the whole architecture (IRC + dispatcher + voting + Harmony + UI seam) in-game on one decision before fanning out to five more in B.2. |
| 2 | **First patch is Neow blessing.** | Single-shot decision per run; small fixed option count (3 — verified from `Neow.GenerateInitialOptions` returning 1 curse + 2 positive at [decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs)); no skip option; no stacking; same target Tempus's StS1 mod hit. <!-- CHANGED: decompile path cited — Reviewer 3 --> |
| 3 | **Patch target: `NEventRoom.OptionButtonClicked(EventOption option, int index)` Prefix.** | Single intercept point for any event-based decision. Filter by "current event is `Neow`" so card events / future events pass through. **Decompile-verified: no keyboard input handler exists on `NEventRoom`** (no `_UnhandledInput`, `_Input`, or `InputMap` references) — option selection is mouse-only, so this single intercept is sufficient. `EventOption.Chosen()` was considered but is fire-and-forget at the call sites (`TaskHelper.RunSafely(option.Chosen())`); a longer Task wouldn't suspend the game. `EventSynchronizer.ChooseLocalOption(int index)` is documented as a B.2 alternative for non-UI votes. <!-- CHANGED: keyboard-bypass concern explicitly verified-falsified — Reviewer 4; EventSynchronizer alternative documented — Reviewers 3, 4 --> |
| 4 | **Suspend-and-resume pattern with two-flag re-entry guard + immediate `DisableEventOptions`.** | Prefix returns `false` after firing `_ = HandleVoteAsync(...)`. Background task awaits the vote, then `dispatcher.Post(...)` re-invokes `OptionButtonClicked(winnerOption, winnerIndex)`. **Critical addition (v2): immediately after fire-and-forget, the prefix calls `room.Layout.DisableEventOptions()` ([decompile-verified](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Events/NEventLayout.cs#L310)) so the game's UI can't dispatch additional clicks during the vote. This prevents the "fast streamer click during async `option.Chosen()` settling" race at the source rather than band-aiding via flag-reset timing.** Two static flags (Interlocked): `_voteInProgress` (set across the whole vote) suppresses any clicks that slip past `DisableEventOptions`; `_resumeInProgress` (set ONLY around the resume's `OptionButtonClicked` call) makes the prefix return `true` so the chat-chosen click goes through to the original. <!-- CHANGED: DisableEventOptions promoted from contingency to primary mitigation — Reviewers 3, 4 --> |
| 5 | **Credentials: JSON file at user-data path, resolved Godot-side.** | `ModEntry.Init` resolves the path via Godot's `OS.GetUserDataDir()` (matches Godot's `user://` convention; correct across Linux/macOS/Windows; honours the actual `project.godot` project name) and passes the resolved path into `ModSettings.Load(string path)`. **Keeps `ModSettings` BCL-only and unit-testable.** Settings panel deferred to B.2; B.1 just reads. JSON includes a `schemaVersion` field; `Load` rejects unknown future versions as `Malformed`. <!-- CHANGED: path resolution moved to ModEntry — Reviewer 3; schemaVersion added — Reviewer 3 --> |
| 6 | **In-game vote UI: `RichTextLabel` with multi-line text.** | `Ti/Ui/VoteTallyLabel.cs` — a Godot `RichTextLabel` (`BbcodeEnabled = true`) showing all options + counts + time remaining. **Parented under `GetTree().Root` (NOT under `NEventRoom`) to avoid Godot's auto-free of children when the room is freed — eliminates double-free risk entirely.** Visible in the game window (captured by OBS — visible to everyone watching the stream, not streamer-only). No bars, no animations, no winner-highlight, no autohide fade. Polish deferred to B.2 / `VoteOverlayControl`. <!-- CHANGED: RichTextLabel — Reviewers 1, 3, 4, 5; root parenting — Reviewers 1, 5 --> |
| 7 | **Connection-status feedback: TiLog + connect-once chat receipt with mod version.** | On the **first** successful Twitch connect per process (gated by static `_connectAnnounced` bool to avoid reconnect-flap spam), send `"slay-the-streamer-2 v{InformationalVersion} connected — votes will go to <channel>"` to the channel as `OutgoingMessagePriority.High`. Auth failure logs at Error and stays silent in-chat. In-game status indicator (`ChatStatusControl`) deferred to B.2. <!-- CHANGED: once-per-process + mod version — Reviewer 3 --> |
| 8 | **v0.1 scope confirmed at 5 votes** (Neow + card reward + boss relic + map path + act-boss). | Verified from Tempus's StS1 source (2026-05-09): original mod's votes were Neow + boss relic + act-boss only; card reward came from the underlying `de.robojumper.ststwitch` base mod; event-choice / shop-purchase / map-path were not anywhere. Event-choice + shop-purchase are deferred to v0.2 as new-design problems. Act-boss is in v0.1 but heavyweight (custom screen replacing post-treasure-room flow); likely needs its own sub-plan. |
| 9 | **Failure modes degrade silently to "vanilla game".** | Missing JSON / malformed / bad oauth / mid-vote disconnect / **`Voter.Default.Start` throws inside the prefix**: mod stays loaded, harness silently returns `true` from the prefix (let original run), game plays normally without chat votes. No crash, no half-broken state, **no lost player click**. <!-- CHANGED: lost-click eliminated via try/catch — Reviewers 2, 3 --> |
| 10 | **Twitch rate-limit defaults: 20 messages / 30 seconds (conservative non-mod tier).** | Plan A's `OutgoingMessageQueue` is constructor-configurable (`int capacity, TimeSpan window`). B.1's `ModEntry` passes `(20, 30s)` to match Twitch's documented limit for unprivileged accounts. JSON setup docs note that streamers whose bot is a moderator/VIP/broadcaster in their channel can opt into `(100, 30s)` via a future `chatRateLimit` settings field (B.2 settings UI). Per-vote receipt volume (~6 messages per 30s vote) sits well under the conservative limit. <!-- CHANGED: NEW decision — Reviewers 1, 2; codebase-validated as configuration not code change --> |
| 11 | **Chat-readiness gate: `chat.State == ChatConnectionState.ConnectedReadWrite`**, not just `chat.IsConnected`. | Anonymous-mode (`ConnectedReadOnly`) reports `IsConnected == true` but `CanSend == false`. Opening a vote in that state means receipts go nowhere — chat sees no announcement, can vote randomly, no close receipt. Bad UX even if the architecture handles it. The stricter gate ensures chat actually sees what's happening before we open a vote. <!-- CHANGED: NEW decision — Reviewers 2, 3, 4 --> |

## Architecture

```
src/
├── Ti/                                          [Plan A — extractable, BCL+Godot only in Ui/Godot subns]
│   ├── Chat/
│   │   └── TwitchIrcChatService.cs              🆕 B.1   full impl per Plan A v2.3 §"ChatService" + JOIN timeout
│   ├── Ui/                                      🆕 B.1   new sub-namespace; Godot-dependent
│   │   └── VoteTallyLabel.cs                    🆕 B.1   Godot RichTextLabel, multi-line text indicator
│   ├── Voting/
│   │   └── VoteCoordinator.cs                   ✏️  B.1   add public IMainThreadDispatcher Dispatcher get-only
│   ├── Internal/                                ✅ Plan A complete
│   ├── Chat/Internal/                           ✅ Plan A complete (parser, OutgoingMessageQueue, retry policy)
│   └── Godot/                                   ✅ Plan B prep complete (DispatcherAutoload, GodotMainThreadDispatcher)
├── Game/                                        🆕 B.1   StS2-specific glue; not extractable
│   ├── Bootstrap/
│   │   └── ModSettings.cs                       🆕 B.1   JSON config reader (path injected); SettingsResult with warnings
│   └── DecisionVotes/
│       └── NeowBlessingVotePatch.cs             🆕 B.1   Harmony Prefix on NEventRoom.OptionButtonClicked
└── ModEntry.cs                                  ✏️  B.1   extend skeleton: load settings, build chat,
                                                          wire Voter.Default, attach indicator, connect

tests/
├── Chat/
│   └── TwitchIrcChatServiceTests.cs             🆕 B.1   deterministic parts: lifecycle, queue interactions,
│                                                          retry policy, JOIN timeout, CAP NAK fallback,
│                                                          dispose-during-reconnect, send-while-disconnected
└── Bootstrap/
    └── ModSettingsTests.cs                      🆕 B.1   JSON parse, missing-file, malformed,
                                                          oauth-prefix normalisation, channel-name normalisation,
                                                          schemaVersion validation, warnings list
```

**Legend**: 🆕 = new file in B.1; ✅ = already-shipped in earlier phase; ✏️ = existing file extended in B.1. <!-- CHANGED: legend added — Reviewer 3 -->

**Net new code estimate (revised)**: TwitchIrcChatService ~800 LOC source + ~250 LOC tests; VoteTallyLabel ~80 LOC; NeowBlessingVotePatch ~150 LOC; ModSettings ~80 LOC + ~80 LOC tests; ModEntry additions ~60 LOC; VoteCoordinator addition ~3 LOC. Total ~1,170 LOC of source, ~330 LOC of tests. <!-- CHANGED: revised upward — Reviewer 3 -->

**Allowed dependencies** (carried forward from Plan A):
- `Ti/Chat/`, `Ti/Voting/`, `Ti/Internal/`, `Ti/Chat/Internal/` may reference: BCL only.
- `Ti/Ui/` may reference: BCL + Godot. No `sts2.dll`.
- `Ti/Godot/` may reference: BCL + Godot. No `sts2.dll`.
- `Game/*` may reference everything.
- `Game/*` MUST NOT be referenced from `Ti/*`. Code-review enforcement; Roslyn analyser is post-MVP.

## `TwitchIrcChatService` (Plan A v2.3 implementation + JOIN timeout)

The headline net-new piece in B.1. Plan A's spec specifies the full contract; B.1 implements it with one addition: a JOIN-confirmation timeout.

**What B.1 must implement** (per Plan A v2.3 §"ChatService", with v2 additions marked):

- TLS connection to `irc.chat.twitch.tv:6697` via `SslStream`.
- TCP framing via `StreamReader.ReadLineAsync` over the TLS stream.
- Capability negotiation: `CAP REQ :twitch.tv/tags twitch.tv/commands`. Falls back to no-tags mode on `CAP NAK`.
- Login: `PASS oauth:<token>` + `NICK <username>` + `JOIN #<channel>`. Channel input normalised per Plan A.
- **JOIN-confirmation timeout (v2 addition)**: after sending `JOIN #channel`, start a 10s timer. Successful confirmation is any of: `ROOMSTATE`, `USERSTATE`, numeric `353` (NAMES list), or `366` (END_OF_NAMES). On timeout without confirmation, transition to `JoinFailed` and log a clear error. <!-- CHANGED: NEW — Reviewer 2 -->
- Anonymous-read mode (`creds == null`): `NICK justinfan{rand6}`. `ConnectedReadOnly`; `CanSend == false`.
- Read loop on a background `Task` with `CancellationToken`. Lines pass through `TwitchIrcParser` to `ChatMessage` events.
- Self-echo guard: drops `parsed.UserId == self.UserId`.
- Outgoing send via `OutgoingMessageQueue` constructed with **`(capacity: 20, window: TimeSpan.FromSeconds(30))`** by default. <!-- CHANGED: spec'd values — Reviewers 1, 2 -->
- All inbound events flow through `IMainThreadDispatcher.Post(...)` before raising — subscribers always observe events on the Godot main thread.
- Reconnect with exponential backoff + jitter (5/10/20/40/60s, ±20% jitter); auth failure, JOIN failure, channel-banned are terminal.
- IRC protocol-matrix handling per Plan A v2.3, **plus**: <!-- CHANGED: protocol additions — Reviewer 2 -->
  - `NOTICE #chan :msg_ratelimit` → log Warn, back off; do not retry the message.
  - `NOTICE #chan :msg_slowmode` → log Warn, back off.
  - `NOTICE #chan :msg_duplicate` → log Debug, drop the duplicate; do not retry.
- Diagnostic state: `LastMessageReceivedAt`, `LastError`, `State`, `IsConnected`, `CanSend`.
- `Dispose` semantics per Plan A v2.3 "Lifecycle / shutdown sequence".

**B.1 testing depth** (~17 tests via internal `IIrcTransport` test seam, up from 13): <!-- CHANGED: expanded test list — Reviewer 2 -->
- State-machine: Disconnected → Connecting → ConnectedReadWrite on PASS+NICK+JOIN success.
- Auth-failure NOTICE → terminal `AuthenticationFailed`; no retry.
- Channel-banned NOTICE → terminal `JoinFailed`; no retry.
- **JOIN-confirmation timeout → terminal `JoinFailed` after 10s of no ROOMSTATE/USERSTATE/353/366.**
- **CAP NAK fallback path** (no tags, falls back to login-by-prefix only).
- Network failure mid-stream → Reconnecting → ConnectedReadWrite (with FakeClock advancing backoff).
- **Dispose during reconnect-delay → cancels the retry timer; no stale read/write loops left alive.**
- **Disconnect during `Connecting` → no automatic reconnect.**
- **Send attempted while `Disconnected` → returns failed Task; doesn't crash.**
- **Send queue cancellation on Dispose → drains in-flight sends, drops queued.**
- Backoff sequence respects exponential schedule + jitter bounds.
- PING from server → PONG sent (also handled before full JOIN confirmation).
- RECONNECT command → graceful disconnect + immediate reconnect.
- Self-echo (parsed.UserId == self.UserId) → MessageReceived NOT raised.
- `MessageReceived` events flow through dispatcher (test with `ImmediateDispatcher`).
- `LastMessageReceivedAt` updated on every PRIVMSG.
- Anonymous mode: `creds == null` → `NICK justinfan*`; `CanSend == false`; `SendMessageAsync` returns failed Task.
- **`NOTICE` auth-failure recognised by `msg-id` tag (when tags enabled), not only by localized text.**
- **No stale reconnect after terminal AuthenticationFailed.**

The actual TLS socket I/O is operator-validated against a real Twitch test channel.

## `VoteCoordinator` (1-line addition)

Plan A's `VoteCoordinator` already holds an `IMainThreadDispatcher` privately. B.1 adds a get-only property: <!-- CHANGED: NEW — Reviewers 1, 2, 3, 4 -->

```csharp
public sealed class VoteCoordinator : IDisposable {
    private readonly IMainThreadDispatcher _dispatcher;
    // ... existing fields ...

    public IChatService Chat => _chat;
    public IMainThreadDispatcher Dispatcher => _dispatcher;   // NEW
    // ... rest unchanged ...
}
```

This eliminates the need for a public-mutable static `ModEntry.Dispatcher`. Patches read `Voter.Default!.Dispatcher`.

## `ModSettings` (Bootstrap)

```csharp
namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(string Channel, ChatCredentials Credentials);

public abstract record SettingsResult {
    public sealed record Success(ChatSettings Settings, IReadOnlyList<string> Warnings) : SettingsResult;   // CHANGED: warnings list
    public sealed record Missing(string Path) : SettingsResult;
    public sealed record Malformed(string Path, string Reason) : SettingsResult;
}

public static class ModSettings {
    public const int CurrentSchemaVersion = 1;

    /// <summary>Reads + validates settings from the given path. Always succeeds (returns a SettingsResult); never throws.</summary>
    /// <remarks>Path resolution is the caller's responsibility — ModEntry resolves via Godot's OS.GetUserDataDir().</remarks>
    public static SettingsResult Load(string path);   // CHANGED: path injected — Reviewer 3
}
```

**JSON shape** (v0.1):

```json
{
    "schemaVersion": 1,
    "channel": "surfinite",
    "username": "surfinitebot",
    "oauthToken": "oauth:abc123def456..."
}
```

- `schemaVersion`: must be `1` for B.1; `Load` returns `Malformed` for unknown versions. <!-- CHANGED: NEW — Reviewer 3 -->
- `channel`: any of `foo`, `#foo`, `https://twitch.tv/foo` accepted; normalised internally per Plan A's channel-normalisation rule. Normalisations are surfaced in `Success.Warnings` (e.g., `"channel name 'https://twitch.tv/surfinite' normalised to 'surfinite'"`). <!-- CHANGED: warnings — Reviewer 3 -->
- `username`: lowercased Twitch login of the bot account that owns the oauth token.
- `oauthToken`: accepts either `oauth:abc123` or bare `abc123`; normalised to bare via `ChatCredentials` ctor.

**Twitch oauth scopes required**: `chat:read`, `chat:write`. Document this in setup notes. <!-- CHANGED: NEW — Reviewer 2 -->

**Validation**:
- Empty / whitespace-only any field → `Malformed` with reason.
- Empty file or malformed JSON → `Malformed` with reason.
- File doesn't exist → `Missing(path)`.
- `schemaVersion != 1` → `Malformed` with reason `"unknown schemaVersion {n}; this mod build supports schemaVersion 1"`.

**Sample file generation**: when the file is missing, `ModEntry` logs the expected path and the JSON shape so the streamer can hand-create it. B.2's settings UI replaces this.

## `NeowBlessingVotePatch` (Harmony — the load-bearing piece)

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NeowBlessingVotePatch {
    private static int _voteInProgress;          // 0/1 — set across the whole vote (suppresses straggler clicks)
    private static int _resumeInProgress;        // 0/1 — set ONLY around the resume's OptionButtonClicked call
    private static readonly Lazy<FieldInfo?> _eventField =                                         // CHANGED: NEW — Reviewer 3
        new(() => AccessTools.Field(typeof(NEventRoom), "_event"));

    /// <summary>
    /// Called by Harmony with original=null to ask whether to process the class
    /// (return true), then once per resolved target. We return true on null so
    /// other patches in this class would be processed; on a real target, we verify
    /// the signature and the _event field exist.
    /// </summary>
    static bool Prepare(MethodBase? original) {
        if (original is null) {                                                                    // CHANGED: comment rewritten — Reviewers 1, 2, 5
            // Verify the _event field exists at install time, not silently at first vote.
            if (_eventField.Value is null) {
                TiLog.Error("[neow-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }
            return true;
        }

        var parameters = original.GetParameters();                                                 // CHANGED: signature check — Reviewer 5
        if (parameters.Length != 2 ||
            parameters[0].ParameterType.Name != "EventOption" ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[neow-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        // Resume re-entry: our own dispatcher.Post is calling OptionButtonClicked with the chat-chosen option.
        if (_resumeInProgress == 1) return true;

        // Filter: only intercept for Neow events.
        if (!IsNeowEvent(__instance)) return true;

        // Filter: locked / proceed options aren't eligible for a vote.                            // CHANGED: clearer comment — Reviewer 3
        if (option.IsLocked || option.IsProceed) return true;

        // Multiplayer bail-out — v0.1 is single-player only.                                      // CHANGED: promoted from open-items — Reviewers 2, 5
        if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
            TiLog.Warn("[neow-vote] multiplayer detected (Players.Count > 1); bailing to vanilla");
            return true;
        }

        // Mod not configured / chat unhealthy → vanilla behavior (let player's click apply).
        if (Voter.Default is null) return true;
        if (Voter.Default.Chat.State is not ChatConnectionState.ConnectedReadWrite) {              // CHANGED: stricter gate — Reviewers 2, 3, 4
            TiLog.Debug($"[neow-vote] chat not in ConnectedReadWrite (state={Voter.Default.Chat.State}); bailing to vanilla");
            return true;
        }

        // Repeat click during a vote in progress: suppress.
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[neow-vote] repeat click during open vote — suppressed");
            return false;
        }

        // Snapshot options on the main thread so we don't pass a live reference into background work.   // CHANGED: snapshot — Reviewer 2
        var liveOptions = GetCurrentOptions(__instance);
        if (liveOptions is null || liveOptions.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        var optionsSnapshot = liveOptions.ToList();
        var labels = optionsSnapshot.Select(o => o.Title.GetRawText()).ToList();

        // Open vote synchronously inside the prefix so we can fail soft on Voter.Start exceptions.   // CHANGED: try/catch in prefix — Reviewers 2, 3
        VoteSession session;
        try {
            session = Voter.Default.Start(
                label: "Neow's Bonus",
                options: labels,
                duration: TimeSpan.FromSeconds(30));
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;   // let the original run — player's click applies normally
        }

        // Disable the game's option buttons IMMEDIATELY — prevents fast-click race                // CHANGED: NEW; primary mitigation — Reviewers 3, 4
        // (and would also block keyboard input if any existed; verified-falsified for NEventRoom).
        try {
            __instance.Layout?.DisableEventOptions();
        } catch (Exception ex) {
            TiLog.Warn($"[neow-vote] DisableEventOptions threw (continuing): {ex.Message}");
        }

        TiLog.Info($"[neow-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(__instance, session, optionsSnapshot, index);
        return false;  // skip original — we'll resume via dispatcher.Post when vote completes
    }

    private static async Task HandleVoteAsync(NEventRoom room, VoteSession session,
                                              IReadOnlyList<EventOption> snapshot, int playerClickIndex) {
        try {
            // Attach the in-game tally label.
            Voter.Default!.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));                // CHANGED: dispatcher via coordinator — Reviewers 1,2,3,4

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[neow-vote] winnerIndex {winnerIndex} out of range; falling back to player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[neow-vote] resume: applying winner #{winnerIndex} on main thread");
            Voter.Default.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex));
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] HandleVoteAsync threw", ex);
            // Reset _voteInProgress so the streamer can re-click.
            try { Voter.Default?.Dispatcher.Post(() => Interlocked.Exchange(ref _voteInProgress, 0)); }
            catch { Interlocked.Exchange(ref _voteInProgress, 0); }
        }
    }

    private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,    // CHANGED: NEW; resume validity checks — Reviewers 2, 3
                                           int winnerIndex, int playerClickIndex) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            // Resume-time validity checks: room/event/options state may have changed during the vote.
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[neow-vote] resume: room no longer valid (likely scene transition); dropping resume");
                return;
            }
            if (!IsNeowEvent(room)) {
                TiLog.Warn("[neow-vote] resume: active event is no longer Neow; dropping resume");
                return;
            }
            var currentOptions = GetCurrentOptions(room)?.ToList();
            if (currentOptions is null || winnerIndex >= currentOptions.Count) {
                TiLog.Warn($"[neow-vote] resume: options changed (now {currentOptions?.Count ?? 0}, need index {winnerIndex}); dropping resume");
                return;
            }

            // Use the CURRENT option object at the winner index, not the captured snapshot (in case the underlying objects shifted).
            var winnerOption = currentOptions[winnerIndex];
            room.OptionButtonClicked(winnerOption, winnerIndex);
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] resume threw", ex);
        } finally {
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static bool IsNeowEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is Neow;
    }

    private static IReadOnlyList<EventOption>? GetCurrentOptions(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.CurrentOptions;
    }

    private static int? TryGetEventOwnerPlayerCount(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        return eventModel?.Owner?.RunState?.Players?.Count;
    }
}
```

<!-- CHANGED: NotImplementedException stubs replaced with concrete pseudocode — Reviewers 1, 3 -->

**Sequence diagram** (the suspend-and-resume in motion, with v2 mitigations):

```
[Main thread]  Player clicks blessing option #1
[Main thread]  StS2: NEventRoom.OptionButtonClicked(option, 1)
[Main thread]  Harmony prefix:
                 ├─ _resumeInProgress == 0; IsNeowEvent ✓; not locked/proceed ✓
                 ├─ multiplayer ✓ (single-player); Voter.Default ✓; State == ConnectedReadWrite ✓
                 ├─ _voteInProgress: 0 → 1 (Interlocked)
                 ├─ snapshot = optionsList.ToList()
                 ├─ try { session = Voter.Default.Start(...) } catch { reset flag; return true }
                 ├─ room.Layout.DisableEventOptions()  ← prevents further player clicks AT SOURCE
                 ├─ _ = HandleVoteAsync(room, session, snapshot, 1)
                 └─ return false  ← SUSPEND

[Threadpool]   HandleVoteAsync:
                 ├─ Voter.Default.Dispatcher.Post(VoteTallyLabel.AttachTo(session))
                 │    └─ Label parented under GetTree().Root, NOT under NEventRoom
                 └─ await session.AwaitWinnerAsync()  ← awaits TCS

[Main thread]  Each frame: VoteTallyLabel._Process polls session, redraws (with IsInstanceValid guard)
[Threadpool]   Chat votes flow through dispatcher → main thread → VoteSession tallies
[Threadpool]   30s elapse; close timer fires; dispatcher.Post(CloseNowInternal)
[Main thread]  Next idle frame: CloseNowInternal runs; TCS.TrySetResult(winnerIndex)

[Threadpool]   await resumes; HandleVoteAsync:
                 └─ Voter.Default.Dispatcher.Post(ResumeOnMainThread(room, snapshot, winner, playerClick))

[Main thread]  Next idle frame: ResumeOnMainThread runs:
                 ├─ _resumeInProgress = 1
                 ├─ Validity checks: IsInstanceValid ✓; IsNeowEvent ✓; index in range ✓
                 ├─ winnerOption = CURRENT options[winner]  ← re-read, don't trust stale snapshot
                 ├─ room.OptionButtonClicked(winnerOption, winner)  ← re-enters prefix
                 │    └─ prefix sees _resumeInProgress == 1 → returns true → original runs
                 │       EventSynchronizer.ChooseLocalOption(winner) → eventually option.Chosen()
                 ├─ _resumeInProgress = 0
                 └─ _voteInProgress = 0  ← any further clicks now retrigger normal flow

[Main thread]  VoteTallyLabel sees session.Closed → label._ExitTree fires:
                 ├─ unsubscribe from session events
                 └─ if (IsInstanceValid(label)) label.QueueFree()  ← safe; root-parented
```

## `VoteTallyLabel` (Ti/Ui)

```csharp
namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {                                       // CHANGED: RichTextLabel — Reviewers 1, 3, 4, 5
    private VoteSession? _session;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    public static void AttachTo(VoteSession session) {                                             // CHANGED: parent argument removed — Reviewers 1, 5
        var tree = (Engine.GetMainLoop() as SceneTree);
        if (tree?.Root is null) {
            TiLog.Warn("[vote-tally-label] no SceneTree.Root available; skipping UI attach");
            return;
        }

        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        // Anchor top-right by default; Surfinite will adjust positioning during polish.
        label.AnchorLeft = 0.6f; label.AnchorTop = 0.05f;
        label.AnchorRight = 0.98f; label.AnchorBottom = 0.4f;
        label._session = session;
        label._closedHandler = (_, _) => label.SafeQueueFree();
        label._cancelledHandler = (_, _) => label.SafeQueueFree();
        session.Closed += label._closedHandler;
        session.Cancelled += label._cancelledHandler;

        tree.Root.AddChild(label);    // CHANGED: parented under root, NOT under NEventRoom — Reviewers 1, 5
    }

    /// <summary>
    /// Per-frame poll for tally + time remaining. Intentionally polling-based
    /// for B.1's minimal label — the cost is negligible for a 4-line text node,
    /// and it sidesteps the cleanup complexity of multiple event subscriptions.
    /// B.2's polished VoteOverlayControl should subscribe to TallyChanged instead.
    /// </summary>
    public override void _Process(double delta) {                                                  // CHANGED: comment added — Reviewers 1, 3, 4, 5 conflict resolution
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;                        // CHANGED: IsInstanceValid guard — Reviewers 1, 3, 4, 5
        if (_session.State is VoteSessionState.Closed
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

    public override void _ExitTree() {                                                             // CHANGED: NEW; clean unsubscribe + cancel — Reviewers 2, 3, 4
        if (_session is not null) {
            if (_closedHandler is not null) _session.Closed -= _closedHandler;
            if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;

            // If the label is being freed before the vote completes (e.g., scene transition),
            // cancel the session so the background HandleVoteAsync doesn't try to resume into
            // a stale room.
            if (_session.State is VoteSessionState.Open) {
                try { _session.Cancel(); }
                catch (Exception ex) { TiLog.Warn($"[vote-tally-label] session.Cancel threw on _ExitTree: {ex.Message}"); }
            }
        }
        _session = null;
        _closedHandler = null;
        _cancelledHandler = null;
        base._ExitTree();
    }

    private void SafeQueueFree() {                                                                 // CHANGED: defensive guard — Reviewers 1, 2, 3, 4, 5
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) {
            QueueFree();
        }
    }
}
```

**Properties (v2)**:
- **Parented under `GetTree().Root`, NOT under `NEventRoom`.** Eliminates the double-free risk that all 5 reviewers caught: when `NEventRoom` is freed by the game (scene transition, escape, normal completion), Godot frees its children — including the Label if it were a child — making the explicit `QueueFree()` from session events a use-after-free. Root-parenting + `_ExitTree` lifecycle means the Label's lifetime is fully controlled by us.
- `RichTextLabel` with `BbcodeEnabled = true` to handle StS2's localised strings (which may contain `[color]` or other markup).
- `_Process` polling for tally + time. Documented as intentional for B.1's minimal label; B.2's polished overlay should subscribe to events instead. (Conflict 1 in meta-review; resolved via R3+R4's view.)
- `_ExitTree` unsubscribes from session events AND cancels the session if still open — prevents `HandleVoteAsync` from trying to resume into a stale scene.
- `IsInstanceValid` guards on all Godot ops.

## `ModEntry` extensions

Building on the existing skeleton at [`src/ModEntry.cs`](../../../src/ModEntry.cs) (sections 1–5 already wired by Plan B prep). NEW in B.1 starts at section 6:

```csharp
// Existing sections 1-5 (unchanged): SceneTree resolve, dispatcher attach, RegisterSingleton,
//                                    GodotMainThreadDispatcher wired, TiLog.Sink → Log passthrough.

public static class ModEntry {
    // No public Dispatcher field — patches read Voter.Default!.Dispatcher instead.        // CHANGED: removed public static — Reviewers 1, 2, 3, 4
    private static int _connectAnnounced;                                                  // CHANGED: NEW — Reviewer 3
    private static readonly CancellationTokenSource _modCts = new();                       // CHANGED: NEW — Reviewer 2

    // ... [existing Init body sections 1-5] ...

    // 6. Resolve settings file path Godot-side, load settings.                            // CHANGED: path resolution — Reviewer 3
    var modVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    Log.Info($"[slay_the_streamer_2] mod version: {modVersion}");                          // CHANGED: NEW — Reviewer 3

    var settingsPath = Path.Combine(OS.GetUserDataDir(), "slay_the_streamer_2.json");
    var settingsResult = ModSettings.Load(settingsPath);
    ChatSettings? settings = null;
    switch (settingsResult) {
        case SettingsResult.Success s:
            settings = s.Settings;
            Log.Info($"[slay_the_streamer_2] settings loaded; channel=#{settings.Channel}");
            foreach (var w in s.Warnings) Log.Info($"[slay_the_streamer_2]   {w}");        // CHANGED: surface warnings — Reviewer 3
            break;
        case SettingsResult.Missing m:
            Log.Info($"[slay_the_streamer_2] no settings file at {m.Path}; mod loaded but Twitch not connected. " +
                     $"Create the file with: {{ \"schemaVersion\": 1, \"channel\": \"...\", \"username\": \"...\", \"oauthToken\": \"oauth:...\" }}");
            break;
        case SettingsResult.Malformed m:
            Log.Error($"[slay_the_streamer_2] settings file at {m.Path} is malformed: {m.Reason}. Mod loaded but not connecting.");
            break;
    }

    // 7. Build TI services.
    var clock = new SystemClock();
    var scheduler = new SystemTimerScheduler();   // no-arg ctor per Plan B prep gotcha

    if (settings is not null) {
        // OutgoingMessageQueue is constructed inside TwitchIrcChatService with conservative defaults.
        var chat = new TwitchIrcChatService(dispatcher, clock, scheduler,
            sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30));                       // CHANGED: explicit conservative limit — Reviewers 1, 2
        var coordinator = new VoteCoordinator(chat, clock, scheduler, dispatcher);
        Voter.Default = coordinator;

        // Send "connected" receipt on FIRST successful connect per process.               // CHANGED: connect-once — Reviewer 3
        chat.ConnectionStateChanged += (_, e) => {
            if (e.NewState is ChatConnectionState.ConnectedReadWrite
                && Interlocked.CompareExchange(ref _connectAnnounced, 1, 0) == 0) {
                _ = chat.SendMessageAsync(
                    $"slay-the-streamer-2 v{modVersion} connected — votes will go to #{settings.Channel}",
                    OutgoingMessagePriority.High);
            }
        };

        _ = chat.ConnectAsync(settings.Channel, settings.Credentials, _modCts.Token);
    }

    // 8. Apply Harmony patches (existing Plan B prep code).
    // ... [existing harmony.PatchAll + diagnostic logging] ...

    // 9. Register shutdown hook (when StS2 exposes one — for now, OS reclaims resources). // CHANGED: NEW — Reviewer 3
    // TODO B.2: Voter.Default = null; chat.Dispose(); _modCts.Cancel(); when shutdown API lands
}
```

## Failure modes & graceful degradation (v2)

The promise: **mod loads, game runs, no crash, no lost player click** in every failure path. <!-- CHANGED: "no lost click" added — Reviewers 2, 3 -->

| Failure | What happens | Streamer experience |
|---|---|---|
| Settings JSON file doesn't exist | `Load` returns `Missing`. `Voter.Default` stays null. Patch prefix returns `true`. | Game vanilla; log says where to put the file + the JSON shape. |
| Settings JSON malformed / wrong schemaVersion | `Load` returns `Malformed` with reason. Same as Missing for runtime. | Game vanilla; log lists the parse error. |
| Settings has bad oauth | IRC reaches `AuthenticationFailed`. State != ConnectedReadWrite. Patch returns `true`. | Game vanilla; log says auth failed. B.2 settings UI adds re-auth without restart. |
| Channel doesn't exist | IRC sends JOIN; **10s timeout fires; transitions to `JoinFailed`**. Patch returns `true`. | Game vanilla; log says join timed out. <!-- CHANGED: timeout handling — Reviewer 2 --> |
| Channel banned/suspended | IRC reaches `JoinFailed` via NOTICE. Patch returns `true`. | Game vanilla; log says channel join failed. |
| Network failure on connect | IRC retries with exponential backoff + jitter forever. State toggles. | Game vanilla until connection lands; once chat is up, votes start working. |
| Anonymous mode (creds == null) | State == `ConnectedReadOnly`; `CanSend == false`. Patch returns `true` (stricter gate). | Game vanilla. (B.1 doesn't intentionally enable anonymous mode; covered for safety.) <!-- CHANGED: stricter gate — Reviewers 2, 3, 4 --> |
| Mid-vote disconnect | Plan A's reconnect fires; VoteSession keeps tallying received messages; close receipt notes the disconnect gap. | Vote completes normally. Chat receipt mentions "(chat was offline 8s during voting)". |
| **`Voter.Default.Start` throws** in prefix | **Caught in prefix; `_voteInProgress` reset; `return true` → original runs.** | **Player's click applies normally — no lost click.** Log says vote couldn't open. <!-- CHANGED: try/catch fix — Reviewers 2, 3 --> |
| `AwaitWinnerAsync` throws | Caught in HandleVoteAsync; falls back to `playerClickIndex`. Resume Post still happens. | Game proceeds with the streamer's original click. |
| Streamer escapes Neow mid-vote | Resume's validity checks fire: `!IsInstanceValid(room)` or `!IsNeowEvent` → log Warn + drop resume. **VoteTallyLabel's `_ExitTree` cancels the session.** | Vote silently drops. No crash, no leak. <!-- CHANGED: now mitigated — Reviewers 2, 3 --> |
| Fast streamer click during/after resume | **`DisableEventOptions` blocks at the source.** Stragglers caught by `_voteInProgress` flag. Re-entry safe via `_resumeInProgress`. | Streamer's clicks visibly disabled during vote. <!-- CHANGED: primary mitigation — Reviewers 3, 4 --> |
| Game version drift breaks patch target | `Prepare` signature check + `_eventField` resolution fails → log Error + `return false` from Prepare → patch not applied → game vanilla. | Game vanilla; log clearly says patch failed at install. <!-- CHANGED: defensive Prepare — Reviewers 3, 5 --> |

## Testing strategy (v2)

### Plan A regression (must stay green)
- All 142 existing tests pass.

### New unit tests (~30 tests, ~330 LOC) <!-- CHANGED: revised count — Reviewer 2 -->

**`ModSettingsTests`** (~14 tests):
- Valid JSON parses to `Success` with correct `ChatSettings`.
- File missing returns `Missing(expectedPath)`.
- Empty file → `Malformed`. Malformed JSON → `Malformed`.
- Empty/whitespace any required field → `Malformed`.
- `oauth:abc123` and `abc123` token forms both normalise.
- Channel `foo` / `#foo` / `https://twitch.tv/foo` all normalise to `foo`.
- `https://twitch.tv/foo` URL form returns a `Success.Warnings` entry. <!-- CHANGED: warnings test — Reviewer 3 -->
- `schemaVersion` missing → `Malformed`.
- `schemaVersion: 999` → `Malformed` with clear "unknown schemaVersion" reason.
- `Load(nonexistentPath)` returns `Missing(nonexistentPath)`.

**`TwitchIrcChatServiceTests`** (~17 tests, expanded per R2):
- All tests from spec v1 PLUS:
- JOIN-confirmation timeout → `JoinFailed`.
- CAP NAK fallback path.
- Disconnect during `Connecting` → no automatic reconnect.
- Dispose during reconnect-delay → cancels timer; no stale loops.
- Send while `Disconnected` → returns failed Task.
- Send queue cancellation on Dispose.
- No stale reconnect after terminal `AuthenticationFailed`.
- `NOTICE msg-id`-based auth-failure detection (when tags enabled).

### Operator-validated (the smoke equivalent for B.1)

**Step 0 — Vanilla baseline** (NEW): <!-- CHANGED: NEW — Reviewer 3 -->
- Build + install + launch with NO settings file. Verify:
  - Mod log shows clear "no settings" message.
  - Mod log shows the patched method name.
  - Reach Neow; verify all blessing buttons function as vanilla (no chat vote opens, no UI overlay appears, streamer's click applies immediately).
- This is the "did the patch attached accidentally break the game" baseline.

**Step 1 — IRC alone**: with settings configured but Neow patch's vote logic temporarily skipped (or test bot in throwaway channel), verify:
- TwitchIrcChatService connects to throwaway test channel.
- `MessageReceived` fires for messages from a second client.
- Sent messages appear in the channel.
- "connected" receipt fires once on successful connect (not on subsequent reconnects). <!-- CHANGED: once-per-process verify — Reviewer 3 -->
- **Anonymous mode test** (NEW): connect with `creds == null`, verify state becomes `ConnectedReadOnly`, `CanSend == false`, messages from another client still fire `MessageReceived`. <!-- CHANGED: NEW — Reviewer 5 -->
- **JOIN-timeout test** (NEW): point at a definitely-nonexistent channel name (e.g., random UUID), verify state transitions to `JoinFailed` within ~10s. <!-- CHANGED: NEW — Reviewer 2 -->

**Step 2 — Full Neow vote**: launch StS2 → start a run → reach Neow → click any blessing → confirm:
- In-game `VoteTallyLabel` appears showing all 3 options + counts (initially 0) + 30s remaining.
- **All blessing buttons are visibly disabled during the vote** (DisableEventOptions verification). <!-- CHANGED: verify — Reviewers 3, 4 -->
- Chat receipt posted to the channel.
- From the second client, send `#0` / `#1` / `#2`. Tally label updates in-game.
- Vote closes; close receipt posted; winning blessing applies; game proceeds.
- **No-chat-input case** (NEW): start a Neow vote, send NO chat votes during the 30s window, verify a random option is selected (per Plan A's no-voter random pick), close receipt notes "No votes received", game proceeds normally. <!-- CHANGED: NEW — Reviewer 3 -->
- Streamer's repeat-clicks during the vote no-op cleanly (buttons disabled).

**Step 3 — Failure-mode operator-validation**:
- **No settings file**: covered in Step 0.
- **Bad oauth**: edit JSON to mangle the token. Verify log shows AuthenticationFailed; Neow plays normally (vanilla).
- **Mid-vote disconnect**: open Neow → start vote → manually disconnect Wi-Fi or block port 6697 mid-vote → restore within ~15s. Verify reconnect happens; post-reconnect votes tally; close receipt notes the disconnect gap.
- **Multiplayer bail-out** (NEW): if able to reach Neow in a multiplayer save, verify mod logs `"multiplayer detected; bailing to vanilla"` and player's click applies normally. (If multiplayer is hard to reach in v0.1, mock via a forced `Players.Count > 1` check; document that this path is then test-only-validated.) <!-- CHANGED: NEW — Reviewer 5 -->
- **Streamer escape** (NEW): start Neow vote, escape/back out if the game allows. Verify no crash; log says resume dropped. <!-- CHANGED: NEW — Reviewer 2 -->

### Acceptance gate ("B.1 done")

All of:
- [ ] All 142 Plan A tests pass.
- [ ] All ~30 new unit tests pass.
- [ ] Step 0 vanilla-baseline operator-validation green.
- [ ] Step 1 IRC operator-validation green (including anonymous mode + JOIN-timeout).
- [ ] Step 2 full Neow vote operator-validation green (including DisableEventOptions verification + no-chat-input case).
- [ ] Step 3 failure-mode operator-validation green: no settings, bad oauth, mid-vote disconnect, multiplayer bail-out, streamer escape.
- [ ] `notes/06` updated with B.1 outcome.

## Open items / verified facts

**Verified during meta-review** (no longer open): <!-- CHANGED: items moved to verified — Reviewer 3 -->

- `EventModel.CurrentOptions` is public (verified at [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NEventRoom.cs:200](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NEventRoom.cs#L200)).
- `NEventLayout.DisableEventOptions()` exists ([NEventLayout.cs:310](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Events/NEventLayout.cs#L310)).
- `NEventRoom` has NO keyboard input handler (no `_UnhandledInput`/`_Input`/`InputMap` references in the decompiled file).
- `Plan A v2.3 EnglishReceipts` includes the disconnect-gap annotation (verified at [src/Ti/Voting/EnglishReceipts.cs:46](../../../src/Ti/Voting/EnglishReceipts.cs#L46)).
- `OutgoingMessageQueue` is configurable per-instance, not hardcoded to 90/30s (verified at [src/Ti/Chat/Internal/OutgoingMessageQueue.cs:32](../../../src/Ti/Chat/Internal/OutgoingMessageQueue.cs#L32)).

**Still open** (need verification during implementation):

1. **`EventOption.Title.GetRawText()` actual return shape**. Spec assumes plain text or BBCode-compatible markup. If it returns BBCode, `RichTextLabel` handles it natively. If it returns plain text, that's also fine in `RichTextLabel` — no behavior change. Verification: print a Neow option title to log on first vote and inspect.
2. **`VoteCoordinator.Dispatcher` get-only property addition**. Plan A change; trivial (1 line).
3. **`SystemTimerScheduler()` no-arg ctor**. Plan B prep gotcha — keep flagged so the implementer doesn't waste time trying to pass an `IClock`.
4. **Test seam for TwitchIrcChatService TLS injection**. Implementation introduces an internal `IIrcTransport` (or similar) abstraction. Public `IChatService` API stays unchanged.
5. **TiLog scrubbing of bare oauth tokens**, not just `oauth:[a-z0-9]+` prefix. Verify via test. <!-- CHANGED: NEW — Reviewer 2 -->

## Risks & assumptions (v2)

- **`NEventRoom.OptionButtonClicked` signature is stable.** Patch's `Prepare` now does an explicit signature check; on drift, fail-loud at install time, not silently at first vote.
- **The two-flag re-entry guard + `DisableEventOptions` handles fast clicks safely.** `DisableEventOptions` removes the click source; flags catch any stragglers; `_resumeInProgress` ensures the chat-chosen click goes through. Validated in operator-test Step 2.
- **Godot's main-thread SynchronizationContext doesn't pump `CallDeferred` during the frame the prefix returns.** Smoke proved this. We avoid the trap by not blocking. Resume is via dispatcher.Post, which queues for the NEXT frame.
- **Plan A's `RunContinuationsAsynchronously` works for non-blocking awaits.** B.1 is the first real test; operator-validation will catch any surprises.
- **JOIN-confirmation timeout fires reliably for nonexistent channels.** Handled via state-machine timer, unit-tested.

## What "B.1 done" unlocks

When B.1's acceptance gate is green:
- The TI core is **functionally complete; production-tested by B.2's wider patch surface**. <!-- CHANGED: softer wording — Reviewer 3 -->
- The Harmony suspend-and-resume pattern is validated in real game state mutation.
- The dispatcher's two-flag re-entry pattern is proven (B.2 patches reuse the shape).
- The credentials story works end-to-end (B.2 adds a settings UI on top of the same JSON format + schemaVersion field).
- The TI/Game seam holds under real use.
- `VoteTallyLabel` lifecycle pattern is established (B.2's `VoteOverlayControl` polish builds on it).

B.2 then needs: 4 more Harmony patches (card reward, boss relic, map path, act-boss), in-game settings UI, and (per B.2 considerations) reconsideration of `EventSynchronizer.ChooseLocalOption` as the patch site for non-UI votes, plus possibly a shared `DecisionVoteGate` to centralise the two-flag pattern.

---

## Process: meta-review workflow

Per the established workflow:
1. Spec v2 (this document) committed.
2. If further review pass desired, run `/document-context` against v2 and collect more reviews.
3. Otherwise, run `superpowers:writing-plans` to produce the implementation plan.
4. Execute via `superpowers:subagent-driven-development` (per-task commits with `plan-b-1/X.Y:` prefix).

---

# Optional Enhancements (pick what you want)

These are "Consider"-tier items from the meta-review — good ideas but not urgent. Tell me which numbers (if any) to fold into the spec, or leave them for future iterations.

| # | Change | Reviewer(s) | Effort | My recommendation |
|---|---|---|---|---|
| 1 | **Subscribe to `TallyChanged` events instead of `_Process` polling** in `VoteTallyLabel`. | R1, R5 | Small | **Lean no for B.1.** R3+R4 are right that polling is idiot-proof for a 4-line text label. Defer to B.2's polished overlay. |
| 2 | **Introduce `DecisionVoteGate` shared abstraction** (Game/DecisionVotes/) now instead of waiting for B.2. | R2 | Small | **Lean no.** Premature for one patch; document for B.2 cleanup before adding 4 more patches. |
| 3 | **Embed a tiny in-mod fake IRC server** for unit tests instead of `IIrcTransport` seam. | R1, R2, R3 | Medium | **Neutral.** R3 suggests a 1-hour spike to estimate cost. Defer pending operator-validation results — if `TwitchIrcChatService` proves flaky, this is the obvious next investment. |
| 4 | **Validate oauth token format with regex** (`[a-z0-9]{30}` or similar) in `ModSettings`. | R2 | Trivial | **Lean yes.** One-line addition; gives a clearer error than waiting for `AuthenticationFailed`. |
| 5 | **Visual highlight on player's clicked button** so the streamer knows their click was captured (even though chat will override). | R5 | Small | **Lean no.** `DisableEventOptions` already provides visible feedback (buttons greyed out). Defer additional highlight to B.2 polish. |
| 6 | **`ModSettings` documents oauth scopes (`chat:read`, `chat:write`)** in the JSON setup notes. | R2 | Trivial | **Lean yes.** Implementation note; helps streamers generate the right token. |
| 7 | **`/document-context` follow-up review pass** after v2 — collect crowd-source reviews on the v2 spec before writing the implementation plan. | (workflow) | Variable | **Neutral.** v2 has substantial changes from v1; another review pass is defensible but doubles the workflow time. Your call. |
| 8 | **Add `_UnhandledInput` patch to `NEventRoom`** as defensive insurance against a future StS2 patch that adds keyboard input. | R4 (extrapolated) | Small | **Lean no.** No keyboard handler exists today; defending against a hypothetical future change is YAGNI. Re-evaluate if a game patch adds keyboard event-option support. |
| 9 | **Promote `notes/06-followups-and-deferred.md` to a running log opened during B.1 implementation**, not after. | R3 | Trivial | **Lean yes.** Cheap habit; gives B.2's meta-review a head start on follow-ups. |
| 10 | **`--smoke-only` ModEntry flag** that skips Harmony patching to reproduce smoke-mode for diagnostic purposes. | R3 | Small | **Lean no for B.1.** Smoke is shipped/done; if a B.2 patch breaks something, the `Prepare` signature/field checks should surface it without needing a smoke fallback. Reconsider if B.2 debugging proves painful. |

---

The updated plan has Must-do and Should-do changes applied. Review the Optional Enhancements list above and tell me which numbers to add, if any.
