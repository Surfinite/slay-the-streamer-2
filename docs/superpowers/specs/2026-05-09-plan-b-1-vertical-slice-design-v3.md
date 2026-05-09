# Plan B.1 — vertical slice: TwitchIrcChatService + Neow blessing vote (v3)

**Date**: 2026-05-09
**Status**: Draft v3 — post-follow-up-review by GPT5.5 on v2 (single reviewer; same conversation thread as one of the original 5)
**Predecessor**: [`2026-05-09-plan-b-1-vertical-slice-design-v2.md`](./2026-05-09-plan-b-1-vertical-slice-design-v2.md). Review at `2026-05-09-plan-b-1-vertical-slice-design_REVIEWS/V2_review.txt`.
**Scope**: First sub-plan of Plan B. A vertical slice that proves the entire chat-vote architecture in-game end-to-end on a single decision (Neow's blessing). One Harmony patch, real Twitch IRC, minimal in-game UI, JSON-file credentials. The remaining four v0.1 votes (card reward, boss relic, map path, act-boss) and the in-game settings panel are explicit non-goals — they belong to B.2 (and possibly B.3 for act-boss's custom screen).

> **Architectural hard constraint** (carried forward from the smoke's verdict): every Harmony prefix that triggers a vote MUST use the **suspend-and-resume** pattern. Prefix returns immediately (`false` to skip original, after firing `_ = HandleVoteAsync(...)` as fire-and-forget). The async handler runs the vote, then re-invokes the chosen game-state mutation via `dispatcher.Post(...)`. **No blocking the Godot main thread on `AwaitWinnerAsync().GetAwaiter().GetResult()`, ever.** The smoke proved this hangs.

## Author's note on v3 changes

GPT5.5's follow-up review on v2 surfaced 8 must-fix items (most narrow correctness fixes; a few real architectural soft-spots in v2's "no lost click" claim). All folded into v3 + marked with `<!-- CHANGED v3: ... -->`. Headline shifts:

1. **OAuth regex softened from hard-fail to warning** — the `^[a-z0-9]{30}$` hard-Malformed in v2 risked rejecting valid tokens from newer Twitch auth flows. v3 hard-fails only on empty/whitespace/control-chars; unusual shape produces a warning.
2. **OAuth scopes wording softened** — Twitch's IRC vs new-chat docs use different scope names (`chat:edit` vs `chat:write` vs `user:write:chat`). v3 doesn't claim canonical names; recommends operator-validation against the chosen token generator.
3. **Send policy enforces both 20/30s AND 1-msg/sec/channel** — token bucket alone allows bursting which violates Twitch's per-channel slow-mode for non-mod accounts. Implementation choice (queue-internal spacing vs external 1.5s spacer) deferred; spec specifies the constraint and adds a unit test.
4. **`HandleVoteAsync` outer catch now resumes with playerClickIndex** instead of just resetting the flag — closes a real lost-click path that v2's "no lost click" promise didn't actually cover.
5. **`ResumeOnMainThread` falls back to playerClickIndex** when winner index is invalid but room is still valid — pairs with #4 to make the no-lost-click promise actually true.
6. **Root-parented `VoteTallyLabel` lifecycle claim corrected** — root-parented labels don't auto-exit when `NEventRoom` is freed, so v2's "`_ExitTree` cancels session on escape" claim was wrong. v3 documents the actual behaviour: vote runs to normal close, resume validity checks drop safely. Acceptable for B.1.
7. **`VoteCoordinator` constructor call** in `ModEntry` was actually correct in v2; the v1 CONTEXT doc had the wrong order, which misled the reviewer. CONTEXT doc fixed in [`-CONTEXT.md`](./2026-05-09-plan-b-1-vertical-slice-design-CONTEXT.md).
8. **`GetFormattedText()` BBCode in chat receipts** documented as a known B.1 cosmetic limit — chat sees literal `[color]` tags; B.2 adds a stripper.

Plus several should-fixes (capture coordinator in prefix, CAP NAK self-echo fallback, JOIN timeout language softening, anonymous-mode op-validation demoted) and minor nits.

## Goals

1. **Validate the architecture in-game end-to-end** for a single decision: streamer launches mod, configures oauth via JSON file, starts a run, reaches Neow, picks any blessing button, vote opens to chat, chat votes via `#0`/`#1`/`#2`, vote closes, chat-chosen blessing applies, game proceeds.
2. **Ship the production `TwitchIrcChatService`** per Plan A v2.3's contract — TLS, CAP REQ tags+commands, send queue with rate limiter (20/30s + 1-msg/sec spacing), reconnect-with-jitter, full state machine, plus a JOIN-confirmation timeout. <!-- CHANGED v3: 1-msg/sec spacing added — V2 reviewer #3 -->
3. **Surface enough streamer-facing visibility** to make B.1 demonstrable: an in-game multi-line indicator showing all vote options + tally counts + remaining seconds, plus a one-line "connected" PRIVMSG to chat on first successful Twitch connection per process.
4. **Establish the Harmony patch shape** for event-based votes via `NEventRoom.OptionButtonClicked`. This pattern will be reused for B.2's card-reward / event-style votes; getting it right once de-risks the rest.
5. **Fail soft on every credentialing error AND every post-Start exception** — missing file, malformed JSON, bad oauth, mid-vote disconnect, `Voter.Default.Start` exception, **post-Start `HandleVoteAsync` exception**. Mod stays loaded; game keeps running; **player's click always applies (either chat-chosen or original)** when the room is still valid; votes silently no-op when chat isn't available. <!-- CHANGED v3: post-Start fallback added — V2 reviewer #6 -->

## Non-goals

- The other four v0.1 Harmony patches (card reward, boss relic, map path, act-boss). All deferred to B.2+.
- In-game settings panel for oauth/channel/policy configuration. Deferred to B.2; B.1 reads from a JSON file.
- Polished `VoteOverlayControl` (animated bars, percentages, autohide fade, winner-highlight effect). B.1 ships a multi-line `RichTextLabel`-based indicator; B.2 (or later polish) replaces with the full overlay.
- BBCode-stripping for chat receipts. B.1 sends `EventOption.Title.GetFormattedText()` as-is to chat, which may include literal `[color]` tags; this is a known cosmetic limit acknowledged for B.1. B.2 adds a stripper. <!-- CHANGED v3: explicit BBCode-in-chat note — V2 reviewer #10 -->
- `ChatStatusControl` (in-game connection-status indicator). Deferred to B.2.
- In-game error toast for auth failure. Deferred.
- Localised receipts (English-only via Plan A's `EnglishReceipts`).
- Multiplayer co-op support. B.1 explicitly bails out for `Players.Count > 1`.
- Streamer escaping out of Neow screen mid-vote — vote runs to normal close in background; resume's validity checks drop safely. **Note v3 correction**: v2 incorrectly claimed `_ExitTree` cancels the session on escape; root-parenting means `_ExitTree` only fires on session close, not on room destruction. The actual behaviour is "vote completes normally on its own timer; resume drops because room is gone." <!-- CHANGED v3: lifecycle claim corrected — V2 reviewer #5 -->
- IRC fixture-generator tool (Plan A's optional enhancement #6) — post-MVP.

## Decisions (from session-3 brainstorming + meta-review v2 + V2 follow-up review)

| # | Decision | Rationale |
|---|---|---|
| 1 | **B.1 is a single sub-plan; vertical slice; one Harmony patch.** | The smallest decomposition that's still smaller-than-monolithic. Validates the whole architecture in-game before fanning out. |
| 2 | **First patch is Neow blessing.** | Single-shot decision per run; small fixed option count (3 — verified from `Neow.GenerateInitialOptions` returning 1 curse + 2 positive at [decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Neow.cs)); no skip option; no stacking; same target Tempus's StS1 mod hit. |
| 3 | **Patch target: `NEventRoom.OptionButtonClicked(EventOption option, int index)` Prefix.** | Single intercept point for any event-based decision. Filter by "current event is `Neow`". Decompile-verified: no keyboard input handler exists on `NEventRoom`. `EventOption.Chosen()` rejected (fire-and-forget at call sites). `EventSynchronizer.ChooseLocalOption` documented as B.2 alternative. |
| 4 | **Suspend-and-resume pattern with two-flag re-entry guard + immediate `DisableEventOptions`.** | See v2 spec for the full rationale. v3 sharpens the post-Start failure handling: the outer catch in `HandleVoteAsync` now attempts a fallback resume with `playerClickIndex` (when the room is still valid) before giving up. This means `DisableEventOptions` is acceptable because **every same-room failure path either resumes (chat-chosen or fallback) or only drops when the room is gone** — no "buttons disabled forever" scenario. <!-- CHANGED v3: post-Start fallback resume added; DisableEventOptions safety argument tightened — V2 reviewer #6, #8 --> |
| 5 | **Credentials: JSON file at user-data path, resolved Godot-side.** | `ModEntry.Init` resolves via Godot's `OS.GetUserDataDir()`; passes resolved path into `ModSettings.Load(string path)`. JSON includes `schemaVersion: 1`. |
| 6 | **In-game vote UI: `RichTextLabel` with multi-line text, parented under `GetTree().Root`.** | Eliminates double-free risk. **Behaviour clarification (v3)**: root-parenting means the label is NOT auto-freed when `NEventRoom` is freed. If the streamer escapes mid-vote, the vote continues until its normal close timer; resume validity checks then drop the application of the result. `_ExitTree` only fires when WE call `QueueFree` (on session Closed/Cancelled) — it does NOT detect room destruction. v2's "`_ExitTree` cancels the session" claim was incorrect; v3 acknowledges the actual behaviour as acceptable for B.1. <!-- CHANGED v3: lifecycle behaviour clarified — V2 reviewer #5 --> |
| 7 | **Connection-status feedback: TiLog + connect-once chat receipt with mod version.** | First-successful-connect-per-process gated by `_connectAnnounced` static; subsequent reconnect transitions don't re-spam. Includes `InformationalVersion`. |
| 8 | **v0.1 scope confirmed at 5 votes** (Neow + card reward + boss relic + map path + act-boss). | Verified from Tempus's StS1 source (2026-05-09). |
| 9 | **Failure modes degrade silently to "vanilla game", no lost player click.** | Missing JSON / malformed / bad oauth / mid-vote disconnect / `Voter.Default.Start` throws / **post-Start `HandleVoteAsync` throws**: in every same-room case, the player's click applies (either as chat-chosen or as fallback to original). Only when the room itself is gone does the resume drop entirely. <!-- CHANGED v3: post-Start fallback — V2 reviewer #6 --> |
| 10 | **Twitch send policy: 20 messages / 30s AND 1 message / second / channel.** | The 20/30s token-bucket alone allows bursting (e.g., connected receipt + vote-open receipt back-to-back within 1s) which violates Twitch's documented per-channel slow-mode limit for unprivileged accounts. v3 adds the 1-msg/sec spacing constraint. **Implementation choice deferred to implementer**: either (a) extend `OutgoingMessageQueue` with a min-spacing param, (b) wrap with an external 1.5s spacer, or (c) configure as `(capacity: 1, window: TimeSpan.FromSeconds(1.5))` for B.1 and accept it's effectively non-bursting. Test: queueing connected receipt + vote-open receipt back-to-back doesn't write both within the same second. <!-- CHANGED v3: 1-msg/sec spacing — V2 reviewer #3 --> |
| 11 | **Chat-readiness gate: `chat.State == ChatConnectionState.ConnectedReadWrite`**, not just `chat.IsConnected`. | Anonymous-mode (`ConnectedReadOnly`) opens a "silent vote" — chat sees no announcement. The stricter gate ensures chat actually sees the vote. |

## Architecture

```
src/
├── Ti/                                          [Plan A — extractable, BCL+Godot only in Ui/Godot subns]
│   ├── Chat/
│   │   └── TwitchIrcChatService.cs              🆕 B.1   full impl per Plan A v2.3 + JOIN timeout + 1-msg/sec spacing
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
└── ModEntry.cs                                  ✏️  B.1   extend skeleton

tests/
├── Chat/
│   └── TwitchIrcChatServiceTests.cs             🆕 B.1   ~17 tests (lifecycle, queue, JOIN timeout, CAP NAK, etc.)
└── Bootstrap/
    └── ModSettingsTests.cs                      🆕 B.1   ~14 tests (JSON parse, oauth normalisation, schemaVersion, warnings)
```

**Legend**: 🆕 = new file in B.1; ✅ = already-shipped; ✏️ = existing file extended.

**Net new code estimate**: TwitchIrcChatService ~800 LOC + ~250 LOC tests; VoteTallyLabel ~80 LOC; NeowBlessingVotePatch ~160 LOC; ModSettings ~80 LOC + ~80 LOC tests; ModEntry additions ~60 LOC; VoteCoordinator addition ~3 LOC. Total ~1,180 LOC of source, ~330 LOC of tests.

**Allowed dependencies** (carried forward from Plan A): unchanged from v2.

## `TwitchIrcChatService` (Plan A v2.3 implementation + JOIN timeout + 1-msg/sec spacing)

The headline net-new piece in B.1. Plan A's spec specifies the full contract; B.1 implements it with three additions: a JOIN-confirmation timeout, a 1-msg/sec per-channel spacing constraint, and CAP NAK self-echo fallback.

**What B.1 must implement** (per Plan A v2.3 §"ChatService", with v3 additions marked):

- TLS connection to `irc.chat.twitch.tv:6697` via `SslStream`.
- TCP framing via `StreamReader.ReadLineAsync` over the TLS stream.
- Capability negotiation: `CAP REQ :twitch.tv/tags twitch.tv/commands`. Falls back to no-tags mode on `CAP NAK`.
- Login: `PASS oauth:<token>` + `NICK <username>` + `JOIN #<channel>`. Channel input normalised per Plan A.
- **JOIN-confirmation timeout**: after sending `JOIN #channel`, start a 10s timer. Successful confirmation is any of: `ROOMSTATE`, `USERSTATE`, numeric `353` (NAMES list), or `366` (END_OF_NAMES). On timeout without confirmation, transition to `JoinFailed` and log a clear error. **v3 wording softening**: this is treated as `JoinFailed` for v0.1 because the streamer can fix channel config and restart; if real-world false positives occur (Twitch transient slowness), B.2 may reclassify the timeout path as retryable. <!-- CHANGED v3: timeout language softened — V2 reviewer #12 -->
- Anonymous-read mode (`creds == null`): `NICK justinfan{rand6}`. `ConnectedReadOnly`; `CanSend == false`.
- Read loop on a background `Task` with `CancellationToken`. Lines pass through `TwitchIrcParser` to `ChatMessage` events.
- **Self-echo guard, CAP NAK aware** <!-- CHANGED v3: explicit fallback — V2 reviewer #13 -->:
  - With tags: drops `parsed.UserId == self.UserId`.
  - Without tags (CAP NAK): falls back to `string.Equals(parsed.Login, self.Login, StringComparison.OrdinalIgnoreCase)`.
  - If neither UserId nor Login is determinable: do not attempt self-echo suppression.
  - Test: in no-tags mode, self-message (matched by login) is filtered.
- Outgoing send via `OutgoingMessageQueue` constructed with **`(capacity: 20, window: TimeSpan.FromSeconds(30))`** PLUS **a 1-second minimum inter-message spacing constraint**. <!-- CHANGED v3: spacing — V2 reviewer #3 -->
  - Implementation choice: extend `OutgoingMessageQueue` with a `minInterval: TimeSpan` parameter (preferred — keeps the queue authoritative); OR wrap with an external 1.5s spacer in `TwitchIrcChatService`; OR if extending the queue isn't desired for B.1, configure as `(1, 1.5s)` and accept effectively non-bursting (still well under per-vote receipt volume).
  - Test: queueing two messages back-to-back results in a ≥1s gap between actual writes (via FakeClock + FakeTimerScheduler).
- All inbound events flow through `IMainThreadDispatcher.Post(...)` before raising — subscribers always observe events on the Godot main thread.
- Reconnect with exponential backoff + jitter (5/10/20/40/60s, ±20% jitter); auth failure, JOIN failure, channel-banned are terminal.
- IRC protocol-matrix handling per Plan A v2.3, **plus**:
  - `NOTICE #chan :msg_ratelimit` → log Warn, back off; do not retry the message.
  - `NOTICE #chan :msg_slowmode` → log Warn, back off.
  - `NOTICE #chan :msg_duplicate` → log Debug, drop the duplicate; do not retry.
- Diagnostic state: `LastMessageReceivedAt`, `LastError`, `State`, `IsConnected`, `CanSend`.
- `Dispose` semantics per Plan A v2.3 "Lifecycle / shutdown sequence".

**B.1 testing depth** (~18 tests via internal `IIrcTransport` test seam) <!-- CHANGED v3: +1 test for spacing — V2 reviewer #3 -->:
- All tests from v2 PLUS:
- **Send queue 1-msg/sec spacing**: queueing connected receipt + vote-open receipt back-to-back results in ≥1s actual write gap.
- **Self-echo in no-tags (CAP NAK) mode**: self-message matched by login is filtered. <!-- CHANGED v3: NEW — V2 reviewer #13 -->

## `VoteCoordinator` (1-line addition)

Plan A's `VoteCoordinator` already holds an `IMainThreadDispatcher` privately (constructor signature is `(IChatService chat, IClock clock, ITimerScheduler scheduler, IMainThreadDispatcher dispatcher, Random? random = null)` — verified at [src/Ti/Voting/VoteCoordinator.cs:24](../../../src/Ti/Voting/VoteCoordinator.cs#L24)). B.1 adds a get-only property: <!-- CHANGED v3: actual constructor signature documented in-spec; CONTEXT doc had a typo — V2 reviewer #4 -->

```csharp
public sealed class VoteCoordinator : IDisposable {
    private readonly IMainThreadDispatcher _dispatcher;

    public IChatService Chat => _chat;
    public IMainThreadDispatcher Dispatcher => _dispatcher;   // NEW
}
```

This eliminates the need for a public-mutable static `ModEntry.Dispatcher`. Patches read `Voter.Default!.Dispatcher`.

## `ModSettings` (Bootstrap)

```csharp
namespace SlayTheStreamer2.Game.Bootstrap;

public sealed record ChatSettings(string Channel, ChatCredentials Credentials);

public abstract record SettingsResult {
    public sealed record Success(ChatSettings Settings, IReadOnlyList<string> Warnings) : SettingsResult;
    public sealed record Missing(string Path) : SettingsResult;
    public sealed record Malformed(string Path, string Reason) : SettingsResult;
}

public static class ModSettings {
    public const int CurrentSchemaVersion = 1;

    public static SettingsResult Load(string path);
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

- `schemaVersion`: must be `1` for B.1; `Load` returns `Malformed` for unknown versions.
- `channel`: any of `foo`, `#foo`, `https://twitch.tv/foo` accepted; normalised internally per Plan A's channel-normalisation rule. Normalisations are surfaced in `Success.Warnings`.
- `username`: lowercased Twitch login of the bot account that owns the oauth token. **If input differs from lowercased form, surface in `Success.Warnings`** (e.g., `"username 'SurfiniteBot' lowercased to 'surfinitebot'"`). <!-- CHANGED v3: lowercasing warning — V2 reviewer nit #4 -->
- `oauthToken`: accepts either `oauth:abc123` or bare `abc123`; normalised to bare via `ChatCredentials` ctor. **Validation (v3 softened from v2's hard regex)**: <!-- CHANGED v3: regex softened — V2 reviewer #1 -->
  - Empty / whitespace-only → `Malformed`.
  - Contains whitespace or control chars after stripping `oauth:` prefix → `Malformed`.
  - Doesn't match the common Twitch user-access-token shape (`^[a-z0-9]{30}$`) → `Success` with a warning (`"oauth token doesn't match the common Twitch user-access-token shape (30 lowercase alphanumeric chars); will let Twitch authentication be the source of truth"`). Twitch's actual auth response is the validity gate.

**Twitch oauth setup notes** (v3 wording softened): <!-- CHANGED v3: scope wording softened — V2 reviewer #2 -->

The token must be a **Twitch user-access token** for the bot account (not an app-access token). For Twitch IRC, the relevant chat scopes vary across Twitch's documentation pages depending on which auth path the streamer uses:

- Legacy IRC docs list `chat:read` + `chat:edit`.
- Some Twitch chat docs list `chat:read` + `chat:write`.
- Newer chatbot/EventSub docs list `user:read:chat` + `user:write:chat`.

**Operator-validation against the chosen token generator is the source of truth.** For the B.1 reference setup, the operator-validation step uses [a specific generator] with [the scopes that actually worked]; document the exact tested combination once in this spec after validation, rather than guessing canonical scope names. The `username` field must match the bot account that owns the token (lowercase Twitch login).

For receipt rate limit beyond the conservative `(20/30s, 1-msg/sec)` default, the bot account must be a moderator/VIP/broadcaster in the streamer's channel; B.2's settings UI will expose `chatRateLimit: "mod"` to opt into `100/30s` (with the per-channel spacing relaxed for mod accounts).

**Validation summary** (v3):
- Empty / whitespace-only any field → `Malformed`.
- Empty file or malformed JSON → `Malformed`.
- File doesn't exist → `Missing(path)`.
- `schemaVersion != 1` → `Malformed`.
- `oauthToken` empty/whitespace/control-chars → `Malformed`.
- `oauthToken` non-empty but unusual shape → `Success` with warning. <!-- CHANGED v3 -->
- Channel normalisation, username lowercasing → `Success` with warnings.

## `NeowBlessingVotePatch` (Harmony — the load-bearing piece)

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NEventRoom), nameof(NEventRoom.OptionButtonClicked))]
internal static class NeowBlessingVotePatch {
    private static int _voteInProgress;          // 0/1 — set across the whole vote
    private static int _resumeInProgress;        // 0/1 — set ONLY around the resume's OptionButtonClicked call
    private static int _multiplayerWarnFired;    // 0/1 — debounce multiplayer Warn log to avoid spam   // CHANGED v3: throttle — V2 reviewer nit #2
    private static readonly Lazy<FieldInfo?> _eventField =
        new(() => AccessTools.Field(typeof(NEventRoom), "_event"));

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_eventField.Value is null) {
                TiLog.Error("[neow-vote] NEventRoom._event field not found; patch will not function");
                return false;
            }
            return true;
        }

        var parameters = original.GetParameters();                                                 // CHANGED v3: typeof check — V2 reviewer nit #1
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(EventOption) ||
            parameters[1].ParameterType != typeof(int)) {
            TiLog.Error($"[neow-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            return false;
        }
        TiLog.Info($"[neow-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }

    static bool Prefix(NEventRoom __instance, EventOption option, int index) {
        if (_resumeInProgress == 1) return true;
        if (!IsNeowEvent(__instance)) return true;
        if (option.IsLocked || option.IsProceed) return true;

        // Multiplayer bail-out — v0.1 is single-player only.
        if (TryGetEventOwnerPlayerCount(__instance) is int playerCount && playerCount > 1) {
            // Throttle: one Warn per process, then Debug.
            if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {                // CHANGED v3 — V2 reviewer nit #2
                TiLog.Warn("[neow-vote] multiplayer detected (Players.Count > 1); bailing to vanilla (further bail-outs at Debug level)");
            } else {
                TiLog.Debug("[neow-vote] multiplayer bail-out");
            }
            return true;
        }

        // Capture coordinator once; avoid repeated Voter.Default reads in async continuations.    // CHANGED v3 — V2 reviewer #9
        var coordinator = Voter.Default;
        if (coordinator is null) return true;
        if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
            TiLog.Debug($"[neow-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
            return true;
        }

        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[neow-vote] repeat click during open vote — suppressed");
            return false;
        }

        var liveOptions = GetCurrentOptions(__instance);
        if (liveOptions is null || liveOptions.Count == 0) {
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        var optionsSnapshot = liveOptions.ToList();
        var labels = optionsSnapshot.Select(o => o.Title.GetFormattedText()).ToList();

        VoteSession session;
        try {
            session = coordinator.Start("Neow's Bonus", labels, TimeSpan.FromSeconds(30));
        } catch (Exception ex) {
            TiLog.Error("[neow-vote] Voter.Default.Start threw; falling back to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }

        // Disable the game's option buttons IMMEDIATELY — prevents fast-click race at source.
        // This is safe because every same-room failure path either resumes (chat-chosen or fallback)
        // or only drops when the room itself is gone — there is no path that leaves buttons          // CHANGED v3: tightened safety argument — V2 reviewer #8
        // disabled forever in a still-active room.
        try {
            __instance.Layout?.DisableEventOptions();
        } catch (Exception ex) {
            TiLog.Warn($"[neow-vote] DisableEventOptions threw (continuing): {ex.Message}");
        }

        TiLog.Info($"[neow-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{index}");
        _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index);
        return false;
    }

    private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,        // CHANGED v3: coordinator captured — V2 reviewer #9
                                              VoteSession session, IReadOnlyList<EventOption> snapshot,
                                              int playerClickIndex) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (Exception ex) {
                TiLog.Error("[neow-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = playerClickIndex;
            }

            if (winnerIndex < 0 || winnerIndex >= snapshot.Count) {
                TiLog.Warn($"[neow-vote] winnerIndex {winnerIndex} out of snapshot range; using player click");
                winnerIndex = playerClickIndex;
            }

            TiLog.Info($"[neow-vote] resume: applying winner #{winnerIndex} on main thread");
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex));
        } catch (Exception ex) {
            // CHANGED v3: outer catch now attempts fallback resume instead of just resetting flag.   // V2 reviewer #6
            // This closes the "lost click" path that v2's promise didn't cover.
            TiLog.Error("[neow-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
            try {
                coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex));
            } catch (Exception postEx) {
                TiLog.Error("[neow-vote] fallback resume Post itself threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }

    private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                           int preferredIndex, int playerClickIndex) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        try {
            // Resume-time validity checks: room/event/options state may have changed during the vote.
            if (!GodotObject.IsInstanceValid(room)) {
                TiLog.Warn("[neow-vote] resume: room no longer valid (likely scene transition); dropping resume");
                return;   // Drop is acceptable — room is gone, buttons are gone with it.
            }
            if (!IsNeowEvent(room)) {
                TiLog.Warn("[neow-vote] resume: active event is no longer Neow; dropping resume");
                return;   // Same reasoning.
            }
            var currentOptions = GetCurrentOptions(room)?.ToList();
            if (currentOptions is null || currentOptions.Count == 0) {
                TiLog.Warn("[neow-vote] resume: no current options; dropping");
                return;
            }

            // CHANGED v3: try preferredIndex (chat winner), then fallback to playerClickIndex,        // V2 reviewer #7
            // then drop only if BOTH are out of range. Pairs with HandleVoteAsync's outer catch
            // to honour the "no lost click while room is valid" promise.
            int applyIndex = preferredIndex;
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[neow-vote] resume: preferred index {applyIndex} out of range; falling back to player click");
                applyIndex = playerClickIndex;
            }
            if (applyIndex < 0 || applyIndex >= currentOptions.Count) {
                TiLog.Warn($"[neow-vote] resume: neither preferred nor player index valid (options now {currentOptions.Count}); dropping");
                return;
            }

            var winnerOption = currentOptions[applyIndex];
            room.OptionButtonClicked(winnerOption, applyIndex);
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

**Sequence diagram** (v3 — incorporating capture-coordinator + post-Start fallback):

```
[Main thread]  Player clicks blessing option #1
[Main thread]  StS2: NEventRoom.OptionButtonClicked(option, 1)
[Main thread]  Harmony prefix:
                 ├─ _resumeInProgress == 0; IsNeowEvent ✓; not locked/proceed ✓; not multiplayer ✓
                 ├─ coordinator = Voter.Default  (captured once for the vote's lifetime)
                 ├─ coordinator.Chat.State == ConnectedReadWrite ✓
                 ├─ _voteInProgress: 0 → 1; snapshot = options.ToList()
                 ├─ try { session = coordinator.Start(...) } catch { reset flag; return true }
                 ├─ room.Layout.DisableEventOptions()  ← prevents further player clicks AT SOURCE
                 ├─ _ = HandleVoteAsync(coordinator, room, session, snapshot, 1)
                 └─ return false  ← SUSPEND

[Threadpool]   HandleVoteAsync:
                 ├─ coordinator.Dispatcher.Post(VoteTallyLabel.AttachTo(session))
                 └─ try { winner = await session.AwaitWinnerAsync() }
                    catch (Exception)  ← ANY failure path here (TCS faulted, dispatcher dead, etc.)
                      └─ coordinator.Dispatcher.Post(ResumeOnMainThread(playerClickIndex, playerClickIndex))
                         ← fallback resume with player's original click; closes the "lost click" gap

[Threadpool]   On normal completion:
                 └─ coordinator.Dispatcher.Post(ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex))

[Main thread]  Next idle frame: ResumeOnMainThread:
                 ├─ _resumeInProgress = 1
                 ├─ if (!IsInstanceValid(room) || !IsNeowEvent(room)): drop (room is gone — acceptable)
                 ├─ currentOptions = GetCurrentOptions(room)
                 ├─ applyIndex = winnerIndex
                 ├─ if applyIndex out of currentOptions.Count: applyIndex = playerClickIndex
                 ├─ if STILL out of range: drop with Warn
                 ├─ room.OptionButtonClicked(currentOptions[applyIndex], applyIndex)  ← always applies SOMETHING when room is valid
                 │    └─ prefix sees _resumeInProgress == 1 → returns true → original runs
                 ├─ _resumeInProgress = 0
                 └─ _voteInProgress = 0
```

## `VoteTallyLabel` (Ti/Ui)

```csharp
namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {
    private VoteSession? _session;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    public static void AttachTo(VoteSession session) {
        var tree = (Engine.GetMainLoop() as SceneTree);
        if (tree?.Root is null) {
            TiLog.Warn("[vote-tally-label] no SceneTree.Root available; skipping UI attach");
            return;
        }

        var label = new VoteTallyLabel { Name = "VoteTallyLabel" };
        label.BbcodeEnabled = true;
        label.FitContent = true;
        label.AnchorLeft = 0.6f; label.AnchorTop = 0.05f;
        label.AnchorRight = 0.98f; label.AnchorBottom = 0.4f;
        label._session = session;
        label._closedHandler = (_, _) => label.SafeQueueFree();
        label._cancelledHandler = (_, _) => label.SafeQueueFree();
        session.Closed += label._closedHandler;
        session.Cancelled += label._cancelledHandler;

        // CHANGED v3: implementation flexibility note — V2 reviewer #11
        // Direct-to-Root attachment may have z-order issues if the game uses CanvasLayers internally.
        // If the label appears behind game UI during operator-validation, change AttachTo to find or
        // create a CanvasLayer named "SlayTheStreamerOverlayLayer" under root and attach there instead.
        tree.Root.AddChild(label);
    }

    /// <summary>
    /// Per-frame poll for tally + time. Intentionally polling-based for B.1's minimal label;
    /// B.2's polished VoteOverlayControl should subscribe to TallyChanged instead.
    /// </summary>
    public override void _Process(double delta) {
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        var sb = new StringBuilder();
        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);                   // CHANGED v3: clamp to 0 — V2 reviewer nit #7
        sb.AppendLine($"Chat voting — {secondsLeft}s left");
        for (int i = 0; i < _session.Options.Count; i++) {
            _session.Tallies.TryGetValue(i, out var count);
            sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
        }
        Text = sb.ToString();
    }

    public override void _ExitTree() {
        // Note v3: _ExitTree fires when WE call QueueFree (on session Closed/Cancelled). It does
        // NOT fire when NEventRoom is destroyed — the label is root-parented, so room destruction
        // doesn't propagate. This means session.Cancel() here is for the case where SOMETHING ELSE         // CHANGED v3: lifecycle behaviour clarified — V2 reviewer #5
        // freed the label (e.g., scene tree teardown on game close) — defensive only.
        if (_session is not null) {
            if (_closedHandler is not null) _session.Closed -= _closedHandler;
            if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;
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

    private void SafeQueueFree() {
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) {
            QueueFree();
        }
    }
}
```

**Properties (v3)**:
- **Parented under `GetTree().Root`**, NOT under `NEventRoom`. Eliminates double-free risk.
- **Lifecycle clarification (v3)**: root-parenting means `NEventRoom` destruction does NOT trigger `_ExitTree` on the label. The label only exits the tree when WE call `QueueFree` (on session Closed/Cancelled) or when the entire scene tree tears down (e.g., game close). v2's claim that `_ExitTree` cancels the session on streamer-escape was incorrect — actual behaviour: vote runs to its normal close; resume validity checks drop application. <!-- CHANGED v3 -->
- **`RichTextLabel` with `BbcodeEnabled = true`** to handle StS2's `GetFormattedText()` markup. **Known B.1 limit**: chat receipts get the same `GetFormattedText()` text, so `[color]` and similar tags appear as literal text in Twitch chat. B.2 adds a stripper. <!-- CHANGED v3 -->
- **Implementation flexibility (v3)**: `tree.Root.AddChild(label)` may have z-order issues; if so, switch to a CanvasLayer overlay during operator-validation.
- `_Process` polling for tally + time. Documented as intentional for B.1's minimal label.
- `IsInstanceValid` guards on all Godot ops.
- TimeRemaining clamped to ≥0 to avoid showing `-1s left` briefly.

## `ModEntry` extensions

Unchanged from v2. The `VoteCoordinator` constructor call uses the actual constructor signature `(chat, clock, scheduler, dispatcher)` (verified at [src/Ti/Voting/VoteCoordinator.cs:24](../../../src/Ti/Voting/VoteCoordinator.cs#L24)). The CONTEXT doc had a typo showing `(chat, dispatcher, scheduler, clock)`; that's been fixed in v3 of the CONTEXT doc. <!-- CHANGED v3: clarification — V2 reviewer #4 -->

## Failure modes & graceful degradation (v3)

The promise: **mod loads, game runs, no crash; no lost player click whenever the room is still valid**. <!-- CHANGED v3: precise wording — V2 reviewer #6, #8 -->

| Failure | What happens | Streamer experience |
|---|---|---|
| Settings JSON file doesn't exist | `Load` returns `Missing`. `Voter.Default` stays null. Patch returns `true`. | Game vanilla; log says where to put the file. |
| Settings JSON malformed / wrong schemaVersion | `Load` returns `Malformed`. Same as Missing for runtime. | Game vanilla; log lists the parse error. |
| Settings has unusual oauth shape (warning, not error) | `Load` returns `Success` with warning; connect proceeds; Twitch auth determines validity. | If oauth is actually valid, mod works. If invalid, AuthenticationFailed path. <!-- CHANGED v3 --> |
| Settings has bad oauth | IRC reaches `AuthenticationFailed`. Patch returns `true`. | Game vanilla; log says auth failed. |
| Channel doesn't exist | IRC sends JOIN; **10s timeout fires; transitions to `JoinFailed`** (treated as terminal for v0.1; B.2 may reclassify if false positives observed). Patch returns `true`. | Game vanilla; log says join timed out. |
| Network failure on connect | IRC retries with exponential backoff + jitter forever. | Game vanilla until connection lands. |
| Anonymous mode (creds == null) | State == `ConnectedReadOnly`; patch returns `true` (stricter gate). | Game vanilla. |
| Mid-vote disconnect | Plan A's reconnect fires; VoteSession keeps tallying received messages; close receipt notes the disconnect gap. | Vote completes normally. |
| `Voter.Default.Start` throws in prefix | Caught in prefix; `_voteInProgress` reset; `return true` → original runs. | Player's click applies normally. |
| `AwaitWinnerAsync` throws | Caught in HandleVoteAsync; falls back to `playerClickIndex`. Resume Post still happens. | Player's click applies via resume. |
| **`HandleVoteAsync` throws (post-Start, e.g., dispatcher fault)** | **Outer catch attempts fallback resume with `playerClickIndex`**. <!-- CHANGED v3: NEW path — V2 reviewer #6 --> | **Player's click applies via fallback resume; no lost click while room is valid.** |
| Streamer escapes Neow mid-vote | Vote runs to normal close timer; resume's `IsInstanceValid(room)` check fails; resume drops cleanly. **VoteTallyLabel stays alive until session closes** (root-parented; not auto-freed by room destruction); when session closes, `Closed` handler triggers `SafeQueueFree`. <!-- CHANGED v3: corrected behaviour — V2 reviewer #5 --> | Vote silently drops on resume. No crash, no leak. Buttons re-enabled by next room load. |
| Resume's winner index out of currentOptions range | **Falls back to `playerClickIndex`**. <!-- CHANGED v3 — V2 reviewer #7 --> | Player's original click applies. |
| Resume's both winner AND player index out of range | Drops with Warn (genuinely no valid index to apply). | Acceptable — options changed underneath; will re-enable on next interaction. |
| Fast streamer click during/after resume | `DisableEventOptions` blocks at the source. Stragglers caught by `_voteInProgress`. Re-entry safe via `_resumeInProgress`. | Streamer's clicks visibly disabled during vote. |
| Game version drift breaks patch target | `Prepare` signature check (`typeof(EventOption)`) + `_eventField` resolution fails → log Error + `return false` → patch not applied → game vanilla. | Game vanilla; log clearly says patch failed at install. |

## Testing strategy (v3)

### Plan A regression
- All 142 existing tests pass.

### New unit tests (~32 tests, ~340 LOC) <!-- CHANGED v3: +2 tests — V2 reviewer #3, #13 -->

**`ModSettingsTests`** (~14 tests, unchanged from v2 except oauth-regex tests):
- All v2 tests PLUS:
- **Oauth with unusual shape** (e.g., 30 chars but mixed-case) → `Success` with warning (not `Malformed`). <!-- CHANGED v3: regex softened — V2 reviewer #1 -->
- **Username with uppercase** ("SurfiniteBot") → `Success`, channel/credentials hold lowercased value, warning surfaced. <!-- CHANGED v3 -->

**`TwitchIrcChatServiceTests`** (~18 tests):
- All v2 tests PLUS:
- **Send queue 1-msg/sec spacing**: queueing two messages back-to-back results in ≥1s actual write gap (FakeClock + FakeTimerScheduler). <!-- CHANGED v3 -->
- **Self-echo in CAP NAK (no-tags) mode**: self-message matched by login is filtered. <!-- CHANGED v3 -->

### Operator-validated

**Step 0 — Vanilla baseline**: unchanged from v2.

**Step 1 — IRC alone**: unchanged from v2 EXCEPT:
- **Anonymous mode test demoted from acceptance gate to "nice if easy"**. The patch bails out for anonymous mode anyway; unit-test coverage is sufficient. <!-- CHANGED v3 — V2 reviewer #14 -->

**Step 2 — Full Neow vote**: unchanged from v2 EXCEPT:
- **Z-order verification (NEW)**: explicitly verify `VoteTallyLabel` renders ABOVE the game's Neow UI, not behind it. If behind, switch to CanvasLayer per the implementation note. <!-- CHANGED v3 — V2 reviewer #11 -->
- **Operator-validated oauth scope generator (NEW)**: document the exact token-generator URL + scope combination that worked in operator-validation (defuses the IRC scope-naming inconsistency). <!-- CHANGED v3 — V2 reviewer #2 -->

**Step 3 — Failure-mode operator-validation**: unchanged from v2.

### Acceptance gate ("B.1 done")

All of:
- [ ] All 142 Plan A tests pass.
- [ ] All ~32 new unit tests pass (including spacing test + CAP NAK self-echo). <!-- CHANGED v3 -->
- [ ] Step 0 vanilla-baseline operator-validation green.
- [ ] Step 1 IRC operator-validation green (JOIN-timeout). Anonymous mode optional. <!-- CHANGED v3 -->
- [ ] Step 2 full Neow vote operator-validation green (including z-order check + documented oauth scope combo). <!-- CHANGED v3 -->
- [ ] Step 3 failure-mode operator-validation green: no settings, bad oauth, mid-vote disconnect, multiplayer bail-out, streamer escape.
- [ ] `notes/06` updated with B.1 outcome.

## Open items / verified facts

**Verified during meta-review** (no longer open):
- All v2 verified items.
- **`LocString.GetFormattedText()` substitutes variables; `LocString.GetRawText()` returns the localisation TEMPLATE without substitution** (verified at [decompiled/sts2/MegaCrit/sts2/Core/Localization/LocString.cs:62,67](../../../decompiled/sts2/MegaCrit/sts2/Core/Localization/LocString.cs#L62)). For chat/UI we want substituted variables → `GetFormattedText()` is correct. BBCode tags in the formatted output appear as literal text in Twitch chat — known B.1 cosmetic limit. <!-- CHANGED v3 -->
- **`VoteCoordinator` constructor signature** is `(IChatService chat, IClock clock, ITimerScheduler scheduler, IMainThreadDispatcher dispatcher, Random? random = null)` — verified at [src/Ti/Voting/VoteCoordinator.cs:24](../../../src/Ti/Voting/VoteCoordinator.cs#L24). The v1 CONTEXT doc had the wrong order; fixed in v3 of CONTEXT. <!-- CHANGED v3 -->
- **`NEventLayout.DisableEventOptions()` calls `optionButton.Disable()` on each button** (standard Godot button-disabled behavior; renders with reduced contrast). <!-- CHANGED v3 -->

**Still open** (need verification during implementation):
1. **Oauth scope combination that actually works** — Twitch IRC docs are inconsistent. Document the exact tested combo during operator-validation.
2. **`OutgoingMessageQueue` 1-msg/sec spacing implementation choice** — extend the queue, wrap externally, or configure `(1, 1.5s)`.
3. **`VoteTallyLabel` z-order under root** — operator-test; if behind game UI, switch to CanvasLayer.
4. **`SystemTimerScheduler()` no-arg ctor** (Plan B prep gotcha).
5. **TiLog scrubbing of bare oauth tokens** (verify via test).

## Risks & assumptions (v3)

- All v2 risks/assumptions hold.
- **The 1-msg/sec spacing implementation choice doesn't matter to the spec's correctness** — any of the three options achieves the constraint; implementer picks based on which fits the queue's design best.
- **Falling back to `playerClickIndex` in resume's failure paths is safe** because it's just calling the same `OptionButtonClicked` we intercepted — same code path the player would have taken vanilla. <!-- CHANGED v3 -->
- **Root-parented label NOT being auto-freed on room transition is acceptable** — the vote completes normally and the resume drops cleanly when the room is gone; no leak (label is freed on session Closed/Cancelled).

## What "B.1 done" unlocks

When B.1's acceptance gate is green:
- The TI core is functionally complete; production-tested by B.2's wider patch surface.
- The Harmony suspend-and-resume pattern is validated in real game state mutation.
- The dispatcher's two-flag re-entry pattern + post-Start fallback resume is proven (B.2 patches reuse the shape).
- The credentials story works end-to-end (B.2 adds settings UI on top).
- The TI/Game seam holds under real use.
- `VoteTallyLabel` lifecycle pattern is established (B.2's `VoteOverlayControl` polish builds on it, and is the place to add BBCode-stripping for chat receipts).

B.2 then needs: 4 more Harmony patches (card reward, boss relic, map path, act-boss), in-game settings UI, BBCode-stripper for chat receipts, optional reconsideration of `EventSynchronizer.ChooseLocalOption` as patch site, possibly shared `DecisionVoteGate`.

---

## Process: meta-review workflow

1. Spec v3 (this document) committed.
2. If further review pass desired, run `/document-context` against v3 and collect more reviews.
3. Otherwise, run `superpowers:writing-plans` to produce the implementation plan.
4. Execute via `superpowers:subagent-driven-development` (per-task commits with `plan-b-1/X.Y:` prefix).
5. **Open `notes/06-followups-and-deferred.md` for editing during B.1 implementation, not after**.

---

## V2 follow-up review status (session 4 continued)

Single follow-up reviewer (GPT5.5, same conversation thread as one of the original 5). All 8 must-fix items folded into v3; should-fix items applied where they materially improve correctness.

| # | Issue | Status |
|---|---|---|
| 1 | OAuth regex too strict | **APPLIED** — softened to warning. |
| 2 | OAuth scope wording | **APPLIED** — softened; document tested combo via operator-validation. |
| 3 | 1-msg/sec spacing missing | **APPLIED** — Decision #10; impl choice deferred; new test. |
| 4 | VoteCoordinator constructor order | **CONTEXT DOC FIXED** — v2 spec was correct; CONTEXT doc had typo. |
| 5 | Root-parented label lifecycle claim | **APPLIED** — corrected behaviour documented; B.1 acceptable. |
| 6 | Post-Start exception fallback | **APPLIED** — outer catch now attempts fallback resume. |
| 7 | ResumeOnMainThread playerClickIndex fallback | **APPLIED** — preferred → player → drop. |
| 8 | DisableEventOptions could leave UI stuck | **APPLIED** — covered by #6+#7; safety argument tightened in Decision #4. |
| 9 | Capture coordinator instead of repeated Voter.Default reads | **APPLIED**. |
| 10 | Label sanitization policy | **APPLIED** — keep `GetFormattedText()`; document BBCode-in-chat as known B.1 limit. |
| 11 | CanvasLayer fallback for z-order | **APPLIED** — implementation note + operator-validation step. |
| 12 | JOIN timeout terminal-vs-retryable | **APPLIED** — wording softened. |
| 13 | CAP NAK self-echo guard | **APPLIED** — login-fallback specified. |
| 14 | Anonymous mode op-validation demoted | **APPLIED** — moved to "nice if easy". |
| nits | typeof check, throttle multiplayer Warn, lowercasing warning, clamp negative seconds | **APPLIED** (most). |
