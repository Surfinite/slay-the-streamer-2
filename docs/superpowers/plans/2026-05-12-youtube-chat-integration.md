# YouTube Chat Parallel Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add YouTube live chat reading to slay-the-streamer-2 in parallel with the existing Twitch integration. YouTube is read-only; both platforms feed a single merged vote tally; in-game label renders per-platform rows when YouTube is configured. Vote-nonce (`!NN` suffix) prevents back-to-back vote tally collisions, preserving the `#0 = skip` "Skip Gang" convention.

**Architecture:** Add a new `IChatConsumer` parent interface; make `IChatService : IChatConsumer` (additive). Implement `MultiChatService : IChatConsumer` aggregator that wraps N child services. New `Ti/Chat/YouTubeChat/` namespace owns all YouTube-specific code (scraping `youtubei` internal endpoint behind isolated interfaces). `VoteCoordinator` takes `IChatConsumer`; `VoteSession` extends with per-platform tally side-dict + `VoteId` + `ConfiguredPlatforms`; `VoteTallyLabel` renders split lines when multi-platform configured. `ModEntry` wires `MultiChatService(twitch, youtube?)` and routes B.2.1's `ShouldEnforceSkipGate()` through Twitch-specific `GetChildState`.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (already integrated for B.1/B.2.1 patches — unchanged in this slice), xUnit 2.9, `System.Net.Http.HttpClient`, `System.Text.Json`. Tests run via `dotnet test`; build assembled via `pwsh -File build.ps1`; install for live testing via `pwsh -File install.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-12-youtube-chat-integration-design-v4.md`](../specs/2026-05-12-youtube-chat-integration-design-v4.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Round-2 meta-review:** [`docs/superpowers/specs/META-REVIEW-round2-2026-05-12-youtube-chat-integration-design.md`](../specs/META-REVIEW-round2-2026-05-12-youtube-chat-integration-design.md) — rationale behind v3 → v4 changes referenced inline.

**Per-task commits**: each task ends in a `git commit` with a `yt-chat/N.M:` prefix.

---

## File Structure

**New files (source):**
- `src/Ti/Chat/IChatConsumer.cs` — read/send/state interface; new parent of `IChatService`.
- `src/Ti/Chat/ChatPlatformNames.cs` — string constants (`Twitch`, `YouTube`); used by `MultiChatService` registration, `VoteSession.PlatformOf`, `VoteTallyLabel`.
- `src/Ti/Chat/MultiChatService.cs` — `IChatConsumer` aggregator; aggregate-state caching; per-child events; partial-failure send semantics.
- `src/Ti/Chat/MultiChatServiceEvents.cs` — event-args records (`ChildConnectionStateChangedEventArgs`, `YouTubeEscalationRequestedEventArgs`). Kept as separate file so the main service stays focused.
- `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs` — `IChatService` impl; state machine; reconnect cadence; escalation counter.
- `src/Ti/Chat/YouTubeChat/YouTubeChatModels.cs` — internal record types for parsed responses.
- `src/Ti/Chat/YouTubeChat/YouTubeChatStatusReason.cs` — public enum (one file because public type).
- `src/Ti/Chat/YouTubeChat/YouTubeHttpStatusException.cs` — internal exception type for status-code-bearing failures.
- `src/Ti/Chat/YouTubeChat/IYouTubeHttp.cs` — HTTP abstraction interface.
- `src/Ti/Chat/YouTubeChat/YouTubeHttp.cs` — production `IYouTubeHttp` impl (HttpClient + CookieContainer + CONSENT cookie + UA + 15s timeout).
- `src/Ti/Chat/YouTubeChat/IYouTubeLiveBroadcastDiscovery.cs` + `YouTubeLiveBroadcastDiscovery.cs` — channel/{ID}/live redirect-follow.
- `src/Ti/Chat/YouTubeChat/IYouTubeLiveChatScraper.cs` + `YouTubeLiveChatScraper.cs` — page parse + get_live_chat poll + health-check telemetry.

**Modified files (source):**
- `src/Ti/Chat/IChatService.cs` — make it `: IChatConsumer`; remove members now in parent.
- `src/Ti/Voting/VoteCoordinator.cs` — change `IChatService chat` param/property to `IChatConsumer chat`; add `IReadOnlyList<string> ConfiguredPlatforms` constructor param; increment `_nextVoteId` per `Start`; pass `voteId` + `configuredPlatforms` to `VoteSession` ctor.
- `src/Ti/Voting/VoteSession.cs` — change `IChatService` to `IChatConsumer`; add `VoteId`, `TallyVersion`, `ConfiguredPlatforms`, `_talliesByPlatform`, `_lastVoteByPlatform`, `TalliesByPlatform`, `LastVoteByPlatform`; vote-nonce parsing in `OnChatMessage`; latest-wins per-platform update; increment `TallyVersion` on tally mutation.
- `src/Ti/Voting/VoteSnapshot.cs` — add `int VoteId` property.
- `src/Ti/Voting/EnglishReceipts.cs` — update `FormatOpen` to include `Vote [NN]:` prefix.
- `src/Ti/Ui/VoteTallyLabel.cs` — split rendering using `ConfiguredPlatforms`; VoteId in header; vote-echo marker; cache rendered text + `TallyVersion`-based invalidation.
- `src/Game/Bootstrap/ModSettings.cs` — add `youtubeChannelId` (nullable string) to settings record; D6 v4 trim-first + control-char-warn validation.
- `src/ModEntry.cs` — construct `MultiChatService(twitch, youtube?)`; pass `configuredPlatforms` to `VoteCoordinator`; subscribe to `ChildConnectionStateChanged` (startup + state-change D8 receipts with 120s debounce) and `EscalationRequested` (route to Twitch SendMessageAsync at priority High).
- `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` — `ShouldEnforceSkipGate()` routes through `MultiChatService.GetChildState(ChatPlatformNames.Twitch)` when `Voter.Default.Chat is MultiChatService`.

**New test files:**
- `tests/Chat/IChatConsumerHierarchyTests.cs` — type-system sanity checks for the split.
- `tests/Chat/ChatPlatformNamesTests.cs` — constants present, distinct.
- `tests/Chat/MultiChatServiceTests.cs` — aggregate state, child events, send fan-out, dispose ordering, GetChildState behavior (≈20 tests).
- `tests/Chat/YouTubeChat/YouTubeHttpTests.cs` — IYouTubeHttp behavior incl. CONSENT cookie test against a fake handler (≈4 tests).
- `tests/Chat/YouTubeChat/YouTubeLiveBroadcastDiscoveryTests.cs` — redirect-follow, consent redirect, query-param ordering (≈6 tests).
- `tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs` — fixture-based parse tests, paid-message extraction, defensive runs[], missing authorChannelId drop, members-only graceful degrade, clientVersion extraction, health-check telemetry (≈14 tests).
- `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs` — state transitions, initial-poll suppression, 429 backoff, dispose race, escalation event (≈14 tests).
- `tests/Voting/VoteSessionPerPlatformTallyTests.cs` — `ConfiguredPlatforms` rendering, latest-wins per-platform, mid-vote stability, invariant assertion (≈8 tests).
- `tests/Voting/VoteSessionVoteNonceTests.cs` — nonce parsing matrix, range check, format ID display (≈9 tests).
- `tests/Voting/EnglishReceiptsTests.cs` — extend with `Vote [NN]:` format test (1 new test).

**Modified test files:**
- `tests/Bootstrap/ModSettingsTests.cs` — ~6 tests for `youtubeChannelId` parsing per D6 v4.
- `tests/Voting/VoteSessionTests.cs` — update existing constructor calls to pass `configuredPlatforms` and `voteId` (no behavior change to existing tests).
- `tests/Voting/VoteCoordinatorTests.cs` — update existing constructor calls similarly.

**New non-source files:**
- `tests/Fixtures/youtube_live_chat_2026-05-12.json` — captured/anonymized initial fixture (Phase 0).
- `tests/Fixtures/youtube_live_chat_paid_message.json` — Super Chat sample.
- `tests/Fixtures/youtube_live_chat_members_only.json` — members-only response (empty actions, null continuation).
- `tests/Fixtures/youtube_live_chat_malformed_renderer.json` — for defensive parse tests.
- `tests/Fixtures/youtube_channel_live_redirect.html` — initial page sample.
- `notes/youtube-fixture-refresh.md` — monthly maintenance task documentation.

**One-file responsibilities:**
- `IChatConsumer`: read/send/state surface. No connect lifecycle.
- `IChatService`: extends `IChatConsumer` with `ConnectAsync`. No behavior change for existing impls.
- `MultiChatService`: aggregate N children behind one `IChatConsumer`. Owns lifecycle of registered children (disposes them on Dispose).
- `YouTubeChatService`: own the state machine + poll loop + reconnect timer + escalation counter for one YouTube channel. Read-only.
- `YouTubeLiveChatScraper`: pure parsing of YouTube's responses. No state machine. All regex/JSON-path fragility lives here.
- `YouTubeLiveBroadcastDiscovery`: pure discovery of a live broadcast video ID from a channel ID. Returns null on any failure.
- `YouTubeHttp`: HttpClient lifecycle + CONSENT cookie + UA + status-code-aware exception throwing.

---

## Phase 0: Verification spike

Phase 0 captures fresh YouTube response fixtures and confirms the scraper's regex/JSON-path assumptions hold against real responses. No shipping code; output is a set of fixture files in `tests/Fixtures/` and any spec adjustments documented in `notes/06-followups-and-deferred.md`.

### Task 1: Spike — capture and anonymize YouTube fixtures

**Files:**
- Create: `tests/Fixtures/youtube_live_chat_2026-05-12.json`
- Create: `tests/Fixtures/youtube_channel_live_redirect.html` (or just the response body)
- Modify: `notes/06-followups-and-deferred.md` (add a "YouTube fixture spike findings (2026-05-12)" section)

- [ ] **Step 1.1: Find a public live broadcast to capture against**

Pick any public live YouTube broadcast that's currently active. Note the channel ID (looks like `UCabc123...`).

- [ ] **Step 1.2: Capture the channel/live redirect**

Using browser DevTools or `curl`:
```powershell
curl -i -L "https://www.youtube.com/channel/{CHANNEL_ID}/live" -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36" -b "CONSENT=YES+cb" -o tests/Fixtures/youtube_channel_live_redirect.html
```

Note the final `Location:` header from the redirect chain. Verify it matches `https://www.youtube.com/watch?v={VIDEO_ID}`. Save the video ID.

- [ ] **Step 1.3: Capture the `live_chat?v={...}` page**

```powershell
curl -s "https://www.youtube.com/live_chat?v={VIDEO_ID}" -A "Mozilla/5.0 ..." -b "CONSENT=YES+cb" -o tests/Fixtures/youtube_live_chat_page.html
```

Open the file and:
- Grep for `INNERTUBE_API_KEY` — record the surrounding context (the regex anchor).
- Grep for `INNERTUBE_CONTEXT` and find `"clientVersion":"..."` — record value.
- Find the initial continuation token inside `ytInitialData` — typically nested under `continuationContents.liveChatContinuation.continuations[0].invalidationContinuationData.continuation` or `timedContinuationData.continuation`. Document the actual path.

- [ ] **Step 1.4: Capture a `get_live_chat` POST response**

```powershell
# Replace API_KEY, CLIENT_VERSION, CONTINUATION below from Step 1.3 findings.
$body = @{
    context = @{
        client = @{
            clientName = "WEB"
            clientVersion = "$CLIENT_VERSION"
        }
    }
    continuation = "$CONTINUATION"
} | ConvertTo-Json -Depth 5
curl -X POST "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key=$API_KEY" `
    -H "Content-Type: application/json" `
    -A "Mozilla/5.0 ..." `
    -b "CONSENT=YES+cb" `
    -d $body `
    -o tests/Fixtures/youtube_live_chat_2026-05-12.raw.json
```

- [ ] **Step 1.5: Anonymize the captured response**

Write a small script (or do manually) to replace:
- `authorExternalChannelId` values → synthetic IDs like `UCfixture001`, `UCfixture002`, ...
- `authorName.simpleText` → `Fixture Author 1`, `Fixture Author 2`, ...
- `message.runs[*].text` → benign content (e.g., `Test message #0`, `Test message #1`).
- `videoId` references → `FIXTUREvid001`.

Save anonymized version to `tests/Fixtures/youtube_live_chat_2026-05-12.json`. Verify structural keys/renderer types preserved.

- [ ] **Step 1.6: Document findings**

Append to `notes/06-followups-and-deferred.md`:

```markdown
## YouTube fixture spike findings (2026-05-12)

- `INNERTUBE_API_KEY` extraction regex confirmed: pattern `"INNERTUBE_API_KEY":"([A-Za-z0-9_-]+)"` matches.
- `INNERTUBE_CONTEXT.client.clientVersion` extraction path: `"clientVersion":"([0-9.]+)"` after `"INNERTUBE_CONTEXT"`.
- Initial continuation token path inside `ytInitialData`: `<actual JSON path observed>`.
- `get_live_chat` response structure: `continuationContents.liveChatContinuation.actions[]`, each containing `addChatItemAction.item.liveChatTextMessageRenderer` (or `liveChatPaidMessageRenderer` for Super Chats, or `liveChatMembershipItemRenderer` for member joins).
- Next continuation extracted from: `continuationContents.liveChatContinuation.continuations[0].{invalidationContinuationData|timedContinuationData}.continuation`.
- `timeoutMs` extracted from: same continuations[0] object's `timeoutMs` field.
- Any deviations from spec §"YouTubeLiveChatScraper" assumptions: <document here>.
```

- [ ] **Step 1.7: Commit fixtures + notes**

```powershell
git add tests/Fixtures/youtube_live_chat_2026-05-12.json tests/Fixtures/youtube_channel_live_redirect.html notes/06-followups-and-deferred.md
git commit -m "yt-chat/1.1: spike — capture YT response fixtures, document scraper paths"
```

**Note**: if any spec assumption is invalidated (e.g., the JSON path is different), STOP and update spec v4 OR document the corrected path in the spike-findings section and reference it in subsequent tasks.

---

## Phase 1: Plan A interface foundations

### Task 2: Introduce `IChatConsumer` parent interface

**Files:**
- Create: `src/Ti/Chat/IChatConsumer.cs`
- Modify: `src/Ti/Chat/IChatService.cs`
- Test: `tests/Chat/IChatConsumerHierarchyTests.cs`

Spec reference: §"`IChatConsumer` / `IChatService` split".

- [ ] **Step 2.1: Write the failing test**

Create `tests/Chat/IChatConsumerHierarchyTests.cs`:
```csharp
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class IChatConsumerHierarchyTests {
    [Fact]
    public void IChatService_Extends_IChatConsumer() {
        Assert.True(typeof(IChatConsumer).IsAssignableFrom(typeof(IChatService)),
            "IChatService must extend IChatConsumer");
    }

    [Fact]
    public void TwitchIrcChatService_Implements_IChatConsumer() {
        Assert.True(typeof(IChatConsumer).IsAssignableFrom(typeof(TwitchIrcChatService)));
    }

    [Fact]
    public void IChatConsumer_Does_Not_Have_ConnectAsync() {
        var connectAsync = typeof(IChatConsumer).GetMethod("ConnectAsync");
        Assert.Null(connectAsync);
    }

    [Fact]
    public void IChatService_Has_ConnectAsync() {
        var connectAsync = typeof(IChatService).GetMethod("ConnectAsync");
        Assert.NotNull(connectAsync);
    }
}
```

- [ ] **Step 2.2: Run test to verify it fails**

```powershell
dotnet test --filter "FullyQualifiedName~IChatConsumerHierarchyTests"
```
Expected: FAIL — `IChatConsumer` doesn't exist.

- [ ] **Step 2.3: Create `IChatConsumer` interface**

Create `src/Ti/Chat/IChatConsumer.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// Read/send/state surface of a chat service. Does NOT include connect-lifecycle.
/// Aggregators (MultiChatService) implement this without exposing ConnectAsync,
/// since children must be connected by the wiring code before construction.
/// </summary>
public interface IChatConsumer : IDisposable {
    ChatConnectionState State { get; }
    bool IsConnected { get; }
    bool CanSend { get; }
    DateTimeOffset? LastMessageReceivedAt { get; }
    Exception? LastError { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;

    void Disconnect();
    Task SendMessageAsync(
        string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default);
}
```

- [ ] **Step 2.4: Modify `IChatService` to extend `IChatConsumer`**

Modify `src/Ti/Chat/IChatService.cs`:
```csharp
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// IChatConsumer + connect lifecycle. Twitch and YouTube both implement this.
/// MultiChatService implements only IChatConsumer (no ConnectAsync — children
/// are pre-connected by ModEntry).
/// </summary>
public interface IChatService : IChatConsumer {
    Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default);
}
```

- [ ] **Step 2.5: Build and verify all tests pass**

```powershell
dotnet build
dotnet test
```
Expected: PASS. The existing `TwitchIrcChatService : IChatService` continues to satisfy both contracts because `IChatService : IChatConsumer`.

- [ ] **Step 2.6: Commit**

```powershell
git add src/Ti/Chat/IChatConsumer.cs src/Ti/Chat/IChatService.cs tests/Chat/IChatConsumerHierarchyTests.cs
git commit -m "yt-chat/2.1: add IChatConsumer parent interface; IChatService extends it"
```

---

### Task 3: Add `ChatPlatformNames` constants

**Files:**
- Create: `src/Ti/Chat/ChatPlatformNames.cs`
- Test: `tests/Chat/ChatPlatformNamesTests.cs`

Spec reference: §"`ChatPlatformNames` constants".

- [ ] **Step 3.1: Write the failing test**

Create `tests/Chat/ChatPlatformNamesTests.cs`:
```csharp
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatPlatformNamesTests {
    [Fact]
    public void Twitch_Constant_Has_Expected_Value() => Assert.Equal("twitch", ChatPlatformNames.Twitch);

    [Fact]
    public void YouTube_Constant_Has_Expected_Value() => Assert.Equal("youtube", ChatPlatformNames.YouTube);

    [Fact]
    public void Constants_Are_Distinct() => Assert.NotEqual(ChatPlatformNames.Twitch, ChatPlatformNames.YouTube);
}
```

- [ ] **Step 3.2: Run test to verify it fails**

```powershell
dotnet test --filter "FullyQualifiedName~ChatPlatformNamesTests"
```
Expected: FAIL — type doesn't exist.

- [ ] **Step 3.3: Create `ChatPlatformNames`**

Create `src/Ti/Chat/ChatPlatformNames.cs`:
```csharp
namespace SlayTheStreamer2.Ti.Chat;

/// <summary>
/// String constants used to identify chat platforms across MultiChatService
/// registration, VoteSession platform discrimination, and VoteTallyLabel ordering.
/// </summary>
public static class ChatPlatformNames {
    public const string Twitch = "twitch";
    public const string YouTube = "youtube";
}
```

- [ ] **Step 3.4: Run tests to verify pass**

```powershell
dotnet test --filter "FullyQualifiedName~ChatPlatformNamesTests"
```
Expected: PASS.

- [ ] **Step 3.5: Commit**

```powershell
git add src/Ti/Chat/ChatPlatformNames.cs tests/Chat/ChatPlatformNamesTests.cs
git commit -m "yt-chat/3.1: add ChatPlatformNames string constants"
```

---

## Phase 2: VoteCoordinator + VoteSession extensions

Phase 2 extends Plan A's vote engine to accept `IChatConsumer`, support per-platform tallies, and support the vote-nonce. These changes are additive but touch the existing test base.

### Task 4: Switch `VoteCoordinator` + `VoteSession` to take `IChatConsumer`

**Files:**
- Modify: `src/Ti/Voting/VoteCoordinator.cs`
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `src/Ti/Voting/Voter.cs` (the `Default.Chat` accessor return type)

Spec reference: §"`VoteSession` per-platform tally (with `ConfiguredPlatforms`)" + §"`IChatConsumer` / `IChatService` split" (impact subsection).

- [ ] **Step 4.1: Modify `VoteCoordinator` constructor and `Chat` property**

Modify `src/Ti/Voting/VoteCoordinator.cs`:
```csharp
private readonly IChatConsumer _chat;
public IChatConsumer Chat => _chat;

public VoteCoordinator(
    IChatConsumer chat,                                  // CHANGED v4
    IClock clock,
    ITimerScheduler scheduler,
    IMainThreadDispatcher dispatcher,
    Random? random = null) {
    _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    // ... rest unchanged
}
```

- [ ] **Step 4.2: Modify `VoteSession` to take `IChatConsumer`**

Modify `src/Ti/Voting/VoteSession.cs`:
```csharp
private readonly IChatConsumer _chat;

internal VoteSession(
    string id, string label, IReadOnlyList<VoteOption> options, TimeSpan duration,
    IChatConsumer chat,                                  // CHANGED v4
    IClock clock, ITimerScheduler scheduler,
    IMainThreadDispatcher dispatcher, Random random,
    VoteParsingPolicy parsingPolicy, VoteReceiptPolicy receiptPolicy,
    Func<VoteSnapshot, ReceiptKind, string>? formatReceipt) {
    // ... rest unchanged
}
```

- [ ] **Step 4.3: Modify `Voter.Default.Chat` to return `IChatConsumer`**

If `Voter.cs` exposes `Chat` as `IChatService`, change it to `IChatConsumer`. Inspect first; the change is one line.

- [ ] **Step 4.4: Build and verify existing tests still pass**

```powershell
dotnet build
dotnet test
```
Expected: PASS. Existing tests pass `FakeChatService : IChatService` (which is `: IChatConsumer` transitively). No test code change required.

- [ ] **Step 4.5: Commit**

```powershell
git add src/Ti/Voting/VoteCoordinator.cs src/Ti/Voting/VoteSession.cs src/Ti/Voting/Voter.cs
git commit -m "yt-chat/4.1: VoteCoordinator/VoteSession/Voter accept IChatConsumer"
```

---

### Task 5: Add `VoteId` to `VoteSession` + `VoteSnapshot` + `VoteCoordinator.Start`

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `src/Ti/Voting/VoteSnapshot.cs`
- Modify: `src/Ti/Voting/VoteCoordinator.cs`
- Test: `tests/Voting/VoteSessionTests.cs` (extend; small) + `tests/Voting/VoteSessionVoteNonceTests.cs` (new)

Spec reference: §"Vote-nonce / per-vote ID (C3)" — Implementation subsection.

- [ ] **Step 5.1: Write a failing test for `VoteId` propagation**

Create `tests/Voting/VoteSessionVoteNonceTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Ti.Voting;
using SlayTheStreamer2.Tests.Chat;     // for FakeChatService — confirm namespace
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteSessionVoteNonceTests : VoteSessionTestBase {
    [Fact]
    public void VoteId_Is_Surfaced_From_Constructor() {
        var session = CreateSession(voteId: 42);
        Assert.Equal(42, session.VoteId);
    }

    [Fact]
    public void VoteCoordinator_Assigns_Sequential_VoteIds() {
        var coordinator = CreateCoordinator();
        var s1 = coordinator.Start("L1", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        s1.CloseNow();
        var s2 = coordinator.Start("L2", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        Assert.Equal(0, s1.VoteId);
        Assert.Equal(1, s2.VoteId);
    }

    [Fact]
    public void VoteCoordinator_VoteIds_Cycle_At_100() {
        // Construct a coordinator and Start() 101 times; the 101st session should have VoteId=0.
        var coordinator = CreateCoordinator();
        VoteSession? lastSession = null;
        for (int i = 0; i < 101; i++) {
            lastSession?.CloseNow();
            lastSession = coordinator.Start($"L{i}", new[] { "A", "B" }, TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, lastSession!.VoteId);
    }
}
```

Reuse `VoteSessionTestBase` (existing per `tests/Voting/VoteSessionTestBase.cs`). Add the helper methods if they don't exist:
```csharp
// In or near VoteSessionTestBase:
protected VoteSession CreateSession(int voteId = 0, IReadOnlyList<string>? configuredPlatforms = null) {
    // Use existing fakes and inject voteId + configuredPlatforms.
}
protected VoteCoordinator CreateCoordinator() { /* same fakes */ }
```

- [ ] **Step 5.2: Run test to verify it fails**

```powershell
dotnet test --filter "FullyQualifiedName~VoteSessionVoteNonceTests"
```
Expected: FAIL — `VoteSession.VoteId` doesn't exist.

- [ ] **Step 5.3: Add `VoteId` to `VoteSession`**

Modify `src/Ti/Voting/VoteSession.cs`:
```csharp
public int VoteId { get; }

internal VoteSession(
    string id, string label, IReadOnlyList<VoteOption> options, TimeSpan duration,
    IChatConsumer chat, IClock clock, ITimerScheduler scheduler,
    IMainThreadDispatcher dispatcher, Random random,
    VoteParsingPolicy parsingPolicy, VoteReceiptPolicy receiptPolicy,
    Func<VoteSnapshot, ReceiptKind, string>? formatReceipt,
    int voteId) {                                        // NEW v4
    // ... existing validation ...
    VoteId = voteId;
    // ... rest unchanged
}
```

- [ ] **Step 5.4: Add `VoteId` to `VoteSnapshot`**

Modify `src/Ti/Voting/VoteSnapshot.cs`:
```csharp
public sealed record VoteSnapshot(
    string Id,
    string Label,
    IReadOnlyList<VoteOption> Options,
    TimeSpan Duration,
    TimeSpan TimeRemaining,
    IReadOnlyDictionary<int, int> Tallies,
    VoteSessionState State,
    int? WinnerIndex,
    int? RandomTieAmong,
    bool NoVotesReceived,
    TimeSpan DisconnectGap,
    int VoteId                                 // NEW v4
);
```

Update `VoteSession.Snapshot()` to pass `VoteId` through.

- [ ] **Step 5.5: Add cycling `_nextVoteId` to `VoteCoordinator.Start`**

Modify `src/Ti/Voting/VoteCoordinator.cs`:
```csharp
private int _nextVoteId = 0;

public VoteSession Start(
    string label,
    IReadOnlyList<string> options,
    TimeSpan duration,
    VoteReceiptPolicy? receipts = null,
    VoteParsingPolicy? parsing = null,
    Func<VoteSnapshot, ReceiptKind, string>? formatReceipt = null,
    CancellationToken ct = default) {

    // ... existing validation ...

    var voteId = _nextVoteId;
    _nextVoteId = (_nextVoteId + 1) % 100;

    var session = new VoteSession(
        id: id, label: label, options: optionList, duration: duration,
        chat: _chat, clock: _clock, scheduler: _scheduler,
        dispatcher: _dispatcher, random: _random,
        parsingPolicy: parsing ?? VoteParsingPolicy.Default,
        receiptPolicy: receipts ?? VoteReceiptPolicy.Default,
        formatReceipt: formatReceipt,
        voteId: voteId);                            // NEW v4

    // ... rest unchanged
}
```

- [ ] **Step 5.6: Run all tests**

```powershell
dotnet test
```
Expected: PASS. Existing snapshot-consuming tests will need `VoteId` added to their snapshot construction; update them to `voteId: 0` where they construct a snapshot directly. Fix any failures from the new record-positional field.

- [ ] **Step 5.7: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs src/Ti/Voting/VoteSnapshot.cs src/Ti/Voting/VoteCoordinator.cs tests/Voting/VoteSessionVoteNonceTests.cs tests/Voting/VoteSessionTestBase.cs
git commit -m "yt-chat/5.1: VoteSession.VoteId + cycling 0-99 assignment in VoteCoordinator"
```

---

### Task 6: Add `ConfiguredPlatforms` + per-platform tally side-dict

**Files:**
- Modify: `src/Ti/Voting/VoteCoordinator.cs`
- Modify: `src/Ti/Voting/VoteSession.cs`
- Test: `tests/Voting/VoteSessionPerPlatformTallyTests.cs` (new)

Spec reference: §"`VoteSession` per-platform tally" + §"Latest-wins per-platform tally update algorithm".

- [ ] **Step 6.1: Write failing tests for `ConfiguredPlatforms` rendering and latest-wins**

Create `tests/Voting/VoteSessionPerPlatformTallyTests.cs`:
```csharp
using System;
using System.Linq;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

public class VoteSessionPerPlatformTallyTests : VoteSessionTestBase {
    [Fact]
    public void SinglePlatform_TalliesByPlatform_Returns_Null() {
        var session = CreateSession(configuredPlatforms: new[] { ChatPlatformNames.Twitch });
        Assert.Null(session.TalliesByPlatform);
    }

    [Fact]
    public void MultiPlatform_TalliesByPlatform_NonNull_Even_With_Zero_Votes() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        Assert.NotNull(session.TalliesByPlatform);
        // Should be seeded with zero entries for both platforms × all options
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 0)]);
        Assert.Equal(0, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 0)]);
    }

    [Fact]
    public void TwitchVote_Increments_TwitchTally_Only() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectTwitchVote(session, userId: "12345", optionIndex: 1);
        Assert.Equal(1, session.Tallies[1]);
        Assert.Equal(1, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 1)]);
        Assert.Equal(0, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 1)]);
    }

    [Fact]
    public void YouTubeVote_Increments_YouTubeTally_Only() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 2);
        Assert.Equal(1, session.Tallies[2]);
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.Twitch, 2)]);
        Assert.Equal(1, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 2)]);
    }

    [Fact]
    public void LatestWins_Decrements_Old_Platform_Tally() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 0);
        InjectYouTubeVote(session, channelId: "UCfixture001", optionIndex: 1);
        Assert.Equal(0, session.TalliesByPlatform![(ChatPlatformNames.YouTube, 0)]);
        Assert.Equal(1, session.TalliesByPlatform[(ChatPlatformNames.YouTube, 1)]);
        Assert.Equal(1, session.Tallies[1]);
        Assert.Equal(0, session.Tallies[0]);
    }

    [Fact]
    public void Invariant_SumByPlatform_Equals_SumMerged() {
        var session = CreateSession(
            configuredPlatforms: new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube });
        InjectTwitchVote(session, userId: "111", optionIndex: 0);
        InjectTwitchVote(session, userId: "222", optionIndex: 1);
        InjectYouTubeVote(session, channelId: "UCa", optionIndex: 0);
        InjectYouTubeVote(session, channelId: "UCb", optionIndex: 2);
        var perPlatformSum = session.TalliesByPlatform!.Values.Sum();
        var mergedSum = session.Tallies.Values.Sum();
        Assert.Equal(mergedSum, perPlatformSum);
    }

    [Fact]
    public void ConfiguredPlatforms_Empty_Throws() {
        Assert.Throws<ArgumentException>(() =>
            CreateSession(configuredPlatforms: Array.Empty<string>()));
    }

    [Fact]
    public void ConfiguredPlatforms_Null_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            CreateSession(configuredPlatforms: null!));
    }
}
```

`InjectTwitchVote` and `InjectYouTubeVote` are helpers on `VoteSessionTestBase` that simulate a `ChatMessage` arriving (Twitch sets `UserId = "12345"`, YouTube sets `UserId = "yt:UCfixture001"`).

- [ ] **Step 6.2: Run test to verify it fails**

```powershell
dotnet test --filter "FullyQualifiedName~VoteSessionPerPlatformTallyTests"
```
Expected: FAIL — `ConfiguredPlatforms` and `TalliesByPlatform` don't exist.

- [ ] **Step 6.3: Add `configuredPlatforms` to `VoteCoordinator` constructor**

Modify `src/Ti/Voting/VoteCoordinator.cs`:
```csharp
private readonly IReadOnlyList<string> _configuredPlatforms;

public VoteCoordinator(
    IChatConsumer chat,
    IReadOnlyList<string> configuredPlatforms,           // NEW v4
    IClock clock,
    ITimerScheduler scheduler,
    IMainThreadDispatcher dispatcher,
    Random? random = null) {
    _chat = chat ?? throw new ArgumentNullException(nameof(chat));
    _configuredPlatforms = configuredPlatforms ?? throw new ArgumentNullException(nameof(configuredPlatforms));
    if (configuredPlatforms.Count == 0)
        throw new ArgumentException("configuredPlatforms must not be empty", nameof(configuredPlatforms));
    // ... rest unchanged
}
```

Pass to the session in `Start`:
```csharp
var session = new VoteSession(
    // ... existing args ...
    configuredPlatforms: _configuredPlatforms,
    voteId: voteId);
```

- [ ] **Step 6.4: Add side-dict to `VoteSession`**

Modify `src/Ti/Voting/VoteSession.cs`:
```csharp
private readonly IReadOnlyList<string> _configuredPlatforms;
private readonly Dictionary<(string Platform, int OptionIndex), int> _talliesByPlatform = new();
private readonly Dictionary<string, DateTimeOffset> _lastVoteByPlatform = new();

public IReadOnlyList<string> ConfiguredPlatforms => _configuredPlatforms;
public IReadOnlyDictionary<(string Platform, int OptionIndex), int>? TalliesByPlatform =>
    _configuredPlatforms.Count > 1 ? _talliesByPlatform : null;
public IReadOnlyDictionary<string, DateTimeOffset> LastVoteByPlatform => _lastVoteByPlatform;

internal VoteSession(
    /* existing args */,
    IReadOnlyList<string> configuredPlatforms,
    int voteId) {
    // ... existing validation ...
    _configuredPlatforms = configuredPlatforms ?? throw new ArgumentNullException(nameof(configuredPlatforms));
    if (_configuredPlatforms.Count == 0)
        throw new ArgumentException("configuredPlatforms must not be empty", nameof(configuredPlatforms));
    VoteId = voteId;
    // Seed per-platform tally with zeros for all configured platforms × all options.
    foreach (var platform in _configuredPlatforms)
        for (int i = 0; i < options.Count; i++)
            _talliesByPlatform[(platform, i)] = 0;
    // ... rest unchanged
}

private static string PlatformOf(ChatMessage msg) =>
    msg.VoterKey.StartsWith("yt:", StringComparison.Ordinal)
        ? ChatPlatformNames.YouTube
        : ChatPlatformNames.Twitch;
```

- [ ] **Step 6.5: Update `OnChatMessage` to maintain per-platform tally**

In `VoteSession.OnChatMessage`, after `_tallies[idx]++` and before `TallyChanged?.Invoke(this, this);`:
```csharp
var platform = PlatformOf(msg);

// Latest-wins decrement of prior per-platform vote.
if (existing) {
    var priorKey = (platform, prior);
    if (_talliesByPlatform.TryGetValue(priorKey, out var priorCount) && priorCount > 0)
        _talliesByPlatform[priorKey] = priorCount - 1;
}

// Increment new per-platform vote.
var nextKey = (platform, idx);
_talliesByPlatform[nextKey] = _talliesByPlatform.TryGetValue(nextKey, out var nextCount)
    ? nextCount + 1
    : 1;

_lastVoteByPlatform[platform] = _clock.UtcNow;
```

- [ ] **Step 6.6: Run tests**

```powershell
dotnet test
```
Expected: PASS for new tests; existing tests pass once their `VoteCoordinator`/`VoteSession` construction is updated to pass `configuredPlatforms: new[] { ChatPlatformNames.Twitch }` (single-platform default).

- [ ] **Step 6.7: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs src/Ti/Voting/VoteCoordinator.cs tests/Voting/VoteSessionPerPlatformTallyTests.cs tests/Voting/VoteSessionTestBase.cs tests/Voting/VoteSessionTests.cs tests/Voting/VoteCoordinatorTests.cs
git commit -m "yt-chat/6.1: VoteSession per-platform tally side-dict + ConfiguredPlatforms"
```

---

### Task 7: Add `TallyVersion` to `VoteSession`

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Test: `tests/Voting/VoteSessionPerPlatformTallyTests.cs` (extend)

Spec reference: §"Latest-wins per-platform tally update algorithm" — `TallyVersion`.

- [ ] **Step 7.1: Add failing test**

Append to `tests/Voting/VoteSessionPerPlatformTallyTests.cs`:
```csharp
[Fact]
public void TallyVersion_Starts_At_Zero() {
    var session = CreateSession();
    Assert.Equal(0, session.TallyVersion);
}

[Fact]
public void TallyVersion_Increments_On_Accepted_Vote() {
    var session = CreateSession();
    InjectTwitchVote(session, userId: "1", optionIndex: 0);
    Assert.Equal(1, session.TallyVersion);
    InjectTwitchVote(session, userId: "2", optionIndex: 1);
    Assert.Equal(2, session.TallyVersion);
}

[Fact]
public void TallyVersion_Does_Not_Increment_On_Invalid_Vote() {
    var session = CreateSession();
    InjectTwitchVote(session, userId: "1", optionIndex: 99);   // out of range
    Assert.Equal(0, session.TallyVersion);
}
```

- [ ] **Step 7.2: Run test — expect FAIL**

```powershell
dotnet test --filter "FullyQualifiedName~TallyVersion"
```

- [ ] **Step 7.3: Add `TallyVersion` to `VoteSession`**

```csharp
public int TallyVersion { get; private set; }
```

Increment in `OnChatMessage` after the per-platform update:
```csharp
TallyVersion++;
TallyChanged?.Invoke(this, this);
```

- [ ] **Step 7.4: Run tests — expect PASS**

```powershell
dotnet test
```

- [ ] **Step 7.5: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionPerPlatformTallyTests.cs
git commit -m "yt-chat/7.1: VoteSession.TallyVersion for label cache invalidation"
```

---

### Task 8: Add vote-nonce parsing (`!NN` suffix)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Test: `tests/Voting/VoteSessionVoteNonceTests.cs` (extend with parsing matrix)

Spec reference: §"Vote-nonce / per-vote ID (C3)" — Implementation subsection + Decision 11.

- [ ] **Step 8.1: Add failing tests for nonce parsing matrix**

Append to `tests/Voting/VoteSessionVoteNonceTests.cs`:
```csharp
[Fact]
public void BareNumber_Without_Nonce_Counts() {
    var session = CreateSession(voteId: 42);
    InjectTwitchVoteText(session, userId: "1", text: "#1");
    Assert.Equal(1, session.Tallies[1]);
}

[Fact]
public void Nonce_Matching_VoteId_Counts() {
    var session = CreateSession(voteId: 42);
    InjectTwitchVoteText(session, userId: "1", text: "#1!42");
    Assert.Equal(1, session.Tallies[1]);
}

[Fact]
public void Nonce_NonPadded_Matches_When_Numeric_Equal() {
    var session = CreateSession(voteId: 4);
    InjectTwitchVoteText(session, userId: "1", text: "#1!4");
    Assert.Equal(1, session.Tallies[1]);
}

[Fact]
public void Nonce_ZeroPadded_Also_Matches() {
    var session = CreateSession(voteId: 4);
    InjectTwitchVoteText(session, userId: "1", text: "#1!04");
    Assert.Equal(1, session.Tallies[1]);
}

[Fact]
public void Stale_Nonce_Is_Dropped() {
    var session = CreateSession(voteId: 42);
    InjectTwitchVoteText(session, userId: "1", text: "#1!41");
    Assert.Equal(0, session.Tallies[1]);
}

[Fact]
public void OutOfRange_Nonce_Is_Dropped() {
    var session = CreateSession(voteId: 42);
    InjectTwitchVoteText(session, userId: "1", text: "#1!100");
    Assert.Equal(0, session.Tallies[1]);
}
```

Add helper `InjectTwitchVoteText(VoteSession, string userId, string text)` to test base that constructs a `ChatMessage` with raw text content.

- [ ] **Step 8.2: Run tests — expect FAIL**

```powershell
dotnet test --filter "FullyQualifiedName~VoteSessionVoteNonceTests"
```

- [ ] **Step 8.3: Update the regex in `VoteSession.BuildRegex`**

Modify `src/Ti/Voting/VoteSession.cs`:
```csharp
private static Regex BuildRegex(VoteParsingPolicy p) {
    var prefix = (p.AcceptHashCommands, p.AcceptBangCommands) switch {
        (true, true) => "[#!]?",
        (true, false) => "#?",
        (false, true) => "!?",
        _ => ""
    };
    // Group 1: option index. Group 2 (optional): vote-ID nonce.
    return new Regex($@"^{prefix}(\d+)(?:!(\d+))?(?:\s|$)", RegexOptions.Compiled);
}
```

- [ ] **Step 8.4: Update `OnChatMessage` nonce-check logic**

In `OnChatMessage` after `int.TryParse(match.Groups[1].Value, out var idx)`:
```csharp
if (idx < 0 || idx >= Options.Count) return;

// Nonce check (per Decision 11): if present, must match VoteId and be in range.
if (match.Groups[2].Success) {
    if (!int.TryParse(match.Groups[2].Value, out var nonce)) return;
    if (nonce < 0 || nonce > 99) {
        TiLog.Debug($"[VoteSession] vote {VoteId:D2}: dropped vote with out-of-range nonce {nonce}");
        return;
    }
    if (nonce != VoteId) {
        TiLog.Debug($"[VoteSession] vote {VoteId:D2}: dropped vote with stale nonce {nonce:D2}");
        return;
    }
}
// ... existing latest-wins + per-platform update logic ...
```

- [ ] **Step 8.5: Run tests — expect PASS**

```powershell
dotnet test
```

- [ ] **Step 8.6: Commit**

```powershell
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionVoteNonceTests.cs tests/Voting/VoteSessionTestBase.cs
git commit -m "yt-chat/8.1: vote-nonce parsing — bare #N or #N!NN matching VoteId"
```

---

### Task 9: Update `EnglishReceipts.FormatOpen` to include `Vote [NN]:`

**Files:**
- Modify: `src/Ti/Voting/EnglishReceipts.cs`
- Modify: `tests/Voting/EnglishReceiptsTests.cs`

Spec reference: §"Vote-nonce / per-vote ID (C3)" — Receipt format updates.

- [ ] **Step 9.1: Add failing test**

Append to `tests/Voting/EnglishReceiptsTests.cs`:
```csharp
[Fact]
public void FormatOpen_Includes_ZeroPadded_VoteId() {
    var snapshot = MakeSnapshotForOpen(voteId: 7);
    var formatted = EnglishReceipts.FormatOpen(snapshot);
    Assert.Contains("Vote [07]", formatted);
}

[Fact]
public void FormatOpen_VoteId_99_Renders_Two_Digits() {
    var snapshot = MakeSnapshotForOpen(voteId: 99);
    var formatted = EnglishReceipts.FormatOpen(snapshot);
    Assert.Contains("Vote [99]", formatted);
}
```

Add helper `MakeSnapshotForOpen(int voteId)` to the test class if not present (constructs a `VoteSnapshot` with placeholder options).

- [ ] **Step 9.2: Run test — expect FAIL**

```powershell
dotnet test --filter "FullyQualifiedName~EnglishReceiptsTests"
```

- [ ] **Step 9.3: Modify `EnglishReceipts.FormatOpen`**

```csharp
public static string FormatOpen(VoteSnapshot s) {
    var numbers = string.Join(", ", s.Options.Select(o => o.Index.ToString()));
    return $"Vote [{s.VoteId:D2}]: {s.Label}! Type {numbers} — {(int)s.Duration.TotalSeconds}s left.";
}
```

- [ ] **Step 9.4: Run tests — expect PASS**

```powershell
dotnet test
```

- [ ] **Step 9.5: Commit**

```powershell
git add src/Ti/Voting/EnglishReceipts.cs tests/Voting/EnglishReceiptsTests.cs
git commit -m "yt-chat/9.1: EnglishReceipts.FormatOpen includes zero-padded Vote [NN]: prefix"
```

---

## Phase 3: MultiChatService aggregator

### Task 10: `MultiChatService` skeleton + aggregate-state + GetChildState

**Files:**
- Create: `src/Ti/Chat/MultiChatService.cs`
- Create: `src/Ti/Chat/MultiChatServiceEvents.cs` (ChildConnectionStateChangedEventArgs record)
- Test: `tests/Chat/MultiChatServiceTests.cs`

Spec reference: §"`MultiChatService` (the aggregator)" + §"Aggregate state rule (with fall-through fix)".

- [ ] **Step 10.1: Create event-args record file**

Create `src/Ti/Chat/MultiChatServiceEvents.cs`:
```csharp
namespace SlayTheStreamer2.Ti.Chat;

public sealed record ChildConnectionStateChangedEventArgs(
    string ChildName,
    ChatConnectionChangedEventArgs Inner);
```

- [ ] **Step 10.2: Write failing tests for constructor validation + state aggregate**

Create `tests/Chat/MultiChatServiceTests.cs`:
```csharp
using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class MultiChatServiceTests {
    [Fact]
    public void Constructor_With_Empty_Children_Throws() {
        Assert.Throws<ArgumentException>(() => new MultiChatService());
    }

    [Fact]
    public void Constructor_With_Null_Service_Throws() {
        Assert.Throws<ArgumentException>(() =>
            new MultiChatService((ChatPlatformNames.Twitch, null!)));
    }

    [Fact]
    public void Constructor_With_Empty_Name_Throws() {
        var fake = new FakeChatService();
        Assert.Throws<ArgumentException>(() =>
            new MultiChatService(("", fake)));
    }

    [Fact]
    public void Constructor_With_Duplicate_Names_Throws() {
        var f1 = new FakeChatService();
        var f2 = new FakeChatService();
        Assert.Throws<ArgumentException>(() =>
            new MultiChatService(("twitch", f1), ("twitch", f2)));
    }

    [Fact]
    public void ConfiguredPlatforms_Reflects_Registration_Order() {
        var twitch = new FakeChatService();
        var youtube = new FakeChatService();
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(
            new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube },
            multi.ConfiguredPlatforms);
    }

    [Fact]
    public void GetChildState_Returns_Child_State_When_Found() {
        var twitch = new FakeChatService();
        twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite,
            multi.GetChildState(ChatPlatformNames.Twitch));
    }

    [Fact]
    public void GetChildState_Returns_Disposed_When_Name_Unknown() {
        var twitch = new FakeChatService();
        var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
        Assert.Equal(ChatConnectionState.Disposed,
            multi.GetChildState("nope"));
    }

    [Fact]
    public void Aggregate_BestOfChildren_Picks_ConnectedReadWrite() {
        var twitch = new FakeChatService();
        twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
        var youtube = new FakeChatService();
        youtube.SimulateState(ChatConnectionState.Reconnecting);
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(ChatConnectionState.ConnectedReadWrite, multi.State);
    }

    [Fact]
    public void Aggregate_MixedTerminal_AuthFailedRanksAbove_Disposed() {
        var twitch = new FakeChatService();
        twitch.SimulateState(ChatConnectionState.AuthenticationFailed);
        var youtube = new FakeChatService();
        youtube.SimulateState(ChatConnectionState.Disposed);
        var multi = new MultiChatService(
            (ChatPlatformNames.Twitch, twitch),
            (ChatPlatformNames.YouTube, youtube));
        Assert.Equal(ChatConnectionState.AuthenticationFailed, multi.State);
    }
}
```

Confirm `FakeChatService` exists (path: search `tests/Chat/FakeChatServiceTests.cs` references; likely `tests/Chat/FakeChatService.cs` or `src/Ti/Chat/FakeChatService.cs`). If `FakeChatService.SimulateState(state)` doesn't exist, add it.

- [ ] **Step 10.3: Run tests — expect FAIL**

```powershell
dotnet test --filter "FullyQualifiedName~MultiChatServiceTests"
```

- [ ] **Step 10.4: Create `MultiChatService` with constructor + GetChildState + AggregateState**

Create `src/Ti/Chat/MultiChatService.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat;

public sealed class MultiChatService : IChatConsumer {
    private readonly Dictionary<string, IChatConsumer> _children;
    public IReadOnlyList<string> ConfiguredPlatforms { get; }

    public MultiChatService(params (string Name, IChatConsumer Service)[] children) {
        if (children == null || children.Length == 0)
            throw new ArgumentException("MultiChatService requires ≥1 child", nameof(children));
        foreach (var (name, service) in children) {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("child Name must be non-empty", nameof(children));
            if (service == null)
                throw new ArgumentException($"child '{name}' Service must not be null", nameof(children));
        }
        var names = children.Select(c => c.Name).ToList();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
            throw new ArgumentException($"MultiChatService child names must be unique: {string.Join(", ", names)}", nameof(children));

        _children = children.ToDictionary(c => c.Name, c => c.Service, StringComparer.Ordinal);
        ConfiguredPlatforms = names;
    }

    public ChatConnectionState State => AggregateState();
    public bool IsConnected => _children.Values.Any(c => c.IsConnected);
    public bool CanSend => _children.Values.Any(c => c.CanSend);
    public DateTimeOffset? LastMessageReceivedAt =>
        _children.Values.Select(c => c.LastMessageReceivedAt).Where(x => x.HasValue).Max();
    public Exception? LastError => null;   // per Round-1 C-9 / Round-2 #14: aggregation loses info; consumers query per-child

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChildConnectionStateChangedEventArgs>? ChildConnectionStateChanged;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;   // wired in Task 11

    public ChatConnectionState GetChildState(string name) {
        if (_children.TryGetValue(name, out var child)) return child.State;
        TiLog.Warn($"[MultiChatService] GetChildState: unknown child name '{name}'; returning Disposed");
        return ChatConnectionState.Disposed;
    }

    public IChatConsumer? GetChild(string name) =>
        _children.TryGetValue(name, out var child) ? child : null;

    private ChatConnectionState AggregateState() {
        if (_children.Values.Any(c => c.State == ChatConnectionState.ConnectedReadWrite))
            return ChatConnectionState.ConnectedReadWrite;
        if (_children.Values.Any(c => c.State == ChatConnectionState.ConnectedReadOnly))
            return ChatConnectionState.ConnectedReadOnly;
        if (_children.Values.Any(c => c.State == ChatConnectionState.Reconnecting))
            return ChatConnectionState.Reconnecting;
        if (_children.Values.Any(c => c.State == ChatConnectionState.Connecting))
            return ChatConnectionState.Connecting;
        // All children terminal. Mixed-terminal: AuthFailed > JoinFailed > Disposed > Disconnected.
        var allTerminal = _children.Values.All(c =>
            c.State is ChatConnectionState.AuthenticationFailed
                    or ChatConnectionState.JoinFailed
                    or ChatConnectionState.Disposed
                    or ChatConnectionState.Disconnected);
        if (!allTerminal) return ChatConnectionState.Disconnected;
        if (_children.Values.Any(c => c.State == ChatConnectionState.AuthenticationFailed))
            return ChatConnectionState.AuthenticationFailed;
        if (_children.Values.Any(c => c.State == ChatConnectionState.JoinFailed))
            return ChatConnectionState.JoinFailed;
        if (_children.Values.All(c => c.State == ChatConnectionState.Disposed))
            return ChatConnectionState.Disposed;
        return ChatConnectionState.Disconnected;
    }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) =>
        throw new NotImplementedException("Not on IChatConsumer; ModEntry pre-connects children");

    public void Disconnect() { /* in Task 12 */ }
    public Task SendMessageAsync(string text, OutgoingMessagePriority priority = OutgoingMessagePriority.Normal, CancellationToken ct = default) =>
        throw new NotImplementedException("Wired in Task 12");
    public void Dispose() { /* in Task 12 */ }

    private void OnChildMessageReceived(object? sender, ChatMessage msg) =>
        MessageReceived?.Invoke(this, msg);   // wiring in Task 11
}
```

- [ ] **Step 10.5: Run tests — expect PASS for constructor / state tests**

```powershell
dotnet test --filter "FullyQualifiedName~MultiChatServiceTests"
```

The aggregate-state and constructor tests should pass. Send/Dispose tests come later.

- [ ] **Step 10.6: Commit**

```powershell
git add src/Ti/Chat/MultiChatService.cs src/Ti/Chat/MultiChatServiceEvents.cs tests/Chat/MultiChatServiceTests.cs
git commit -m "yt-chat/10.1: MultiChatService skeleton + AggregateState + GetChildState"
```

---

### Task 11: MultiChatService event forwarding + aggregate caching

**Files:**
- Modify: `src/Ti/Chat/MultiChatService.cs`
- Modify: `tests/Chat/MultiChatServiceTests.cs`

Spec reference: §"`MultiChatService` (the aggregator)" — `OnChildConnectionStateChangedInternal` + lambda unsubscription.

- [ ] **Step 11.1: Add failing tests for event propagation**

Append to `tests/Chat/MultiChatServiceTests.cs`:
```csharp
[Fact]
public void MessageReceived_From_Child_Forwards_Through_Multi() {
    var twitch = new FakeChatService();
    var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
    ChatMessage? received = null;
    multi.MessageReceived += (_, m) => received = m;
    twitch.SimulateIncoming(new ChatMessage(
        UserId: "1", Login: "a", DisplayName: "A", Text: "#0",
        ReceivedAt: DateTimeOffset.UtcNow,
        IsSubscriber: false, IsModerator: false, IsVip: false));
    Assert.NotNull(received);
    Assert.Equal("1", received!.UserId);
}

[Fact]
public void ChildConnectionStateChanged_Fires_On_Child_Transition() {
    var twitch = new FakeChatService();
    var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
    ChildConnectionStateChangedEventArgs? captured = null;
    multi.ChildConnectionStateChanged += (_, e) => captured = e;
    twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
    Assert.NotNull(captured);
    Assert.Equal(ChatPlatformNames.Twitch, captured!.ChildName);
}

[Fact]
public void Aggregate_ConnectionStateChanged_Fires_Only_When_Aggregate_Changes() {
    var twitch = new FakeChatService();
    var youtube = new FakeChatService();
    var multi = new MultiChatService(
        (ChatPlatformNames.Twitch, twitch),
        (ChatPlatformNames.YouTube, youtube));
    int aggregateFireCount = 0;
    multi.ConnectionStateChanged += (_, _) => aggregateFireCount++;

    twitch.SimulateState(ChatConnectionState.Connecting);          // aggregate: Disconnected → Connecting
    Assert.Equal(1, aggregateFireCount);

    youtube.SimulateState(ChatConnectionState.Connecting);         // aggregate stays Connecting
    Assert.Equal(1, aggregateFireCount);

    twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);  // aggregate: Connecting → ConnectedReadWrite
    Assert.Equal(2, aggregateFireCount);
}
```

- [ ] **Step 11.2: Run tests — expect FAIL**

- [ ] **Step 11.3: Wire events in `MultiChatService` constructor + `_lastAggregateState`**

Add to `MultiChatService.cs`:
```csharp
private readonly List<(string Name, EventHandler<ChatConnectionChangedEventArgs> Handler)> _stateHandlers = new();
private ChatConnectionState _lastAggregateState;

// In constructor, AFTER _children is assigned:
_lastAggregateState = AggregateState();
foreach (var (name, child) in children) {
    child.MessageReceived += OnChildMessageReceived;
    var capturedName = name;
    EventHandler<ChatConnectionChangedEventArgs> handler =
        (s, e) => OnChildConnectionStateChangedInternal(capturedName, s, e);
    child.ConnectionStateChanged += handler;
    _stateHandlers.Add((name, handler));
}

private void OnChildConnectionStateChangedInternal(string name, object? sender, ChatConnectionChangedEventArgs e) {
    var newAggregate = AggregateState();
    var oldAggregate = _lastAggregateState;
    _lastAggregateState = newAggregate;

    var childArgs = new ChildConnectionStateChangedEventArgs(name, e);
    try { ChildConnectionStateChanged?.Invoke(this, childArgs); }
    catch (Exception ex) { TiLog.Error("[MultiChatService] ChildConnectionStateChanged handler threw", ex); }

    if (newAggregate != oldAggregate) {
        var aggArgs = new ChatConnectionChangedEventArgs(oldAggregate, newAggregate,
            $"child '{name}' transition triggered aggregate change");
        try { ConnectionStateChanged?.Invoke(this, aggArgs); }
        catch (Exception ex) { TiLog.Error("[MultiChatService] ConnectionStateChanged handler threw", ex); }
    }
}
```

- [ ] **Step 11.4: Run tests — expect PASS**

- [ ] **Step 11.5: Commit**

```powershell
git add src/Ti/Chat/MultiChatService.cs tests/Chat/MultiChatServiceTests.cs
git commit -m "yt-chat/11.1: MultiChatService event forwarding + _lastAggregateState caching"
```

---

### Task 12: MultiChatService send/disconnect/dispose

**Files:**
- Modify: `src/Ti/Chat/MultiChatService.cs`
- Modify: `tests/Chat/MultiChatServiceTests.cs`

Spec reference: §"`MultiChatService`" — `SendMessageAsync`, `Disconnect`, `Dispose`.

- [ ] **Step 12.1: Add failing tests for send behavior**

```csharp
[Fact]
public async Task SendMessageAsync_Routes_To_CanSend_Children_Only() {
    var twitch = new FakeChatService(); twitch.SetCanSend(true);
    var youtube = new FakeChatService(); youtube.SetCanSend(false);
    var multi = new MultiChatService(
        (ChatPlatformNames.Twitch, twitch),
        (ChatPlatformNames.YouTube, youtube));
    await multi.SendMessageAsync("hello");
    Assert.Single(twitch.Sent);
    Assert.Empty(youtube.Sent);
}

[Fact]
public async Task SendMessageAsync_Zero_CanSend_Children_Completes_Silently() {
    var twitch = new FakeChatService(); twitch.SetCanSend(false);
    var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
    await multi.SendMessageAsync("hello");
    Assert.Empty(twitch.Sent);
}

[Fact]
public async Task SendMessageAsync_All_Sends_Throw_Returns_Without_Throwing() {
    var twitch = new FakeChatService(); twitch.SetCanSend(true); twitch.SimulateSendThrow();
    var multi = new MultiChatService((ChatPlatformNames.Twitch, twitch));
    await multi.SendMessageAsync("hello"); // no exception
}

[Fact]
public void Disconnect_Propagates_To_All_Children() {
    var twitch = new FakeChatService(); twitch.SimulateState(ChatConnectionState.ConnectedReadWrite);
    var youtube = new FakeChatService(); youtube.SimulateState(ChatConnectionState.ConnectedReadOnly);
    var multi = new MultiChatService(
        (ChatPlatformNames.Twitch, twitch),
        (ChatPlatformNames.YouTube, youtube));
    multi.Disconnect();
    Assert.True(twitch.DisconnectCalled);
    Assert.True(youtube.DisconnectCalled);
}

[Fact]
public void Dispose_Disposes_All_Children_Even_If_One_Throws() {
    var twitch = new FakeChatService(); twitch.SimulateDisposeThrow();
    var youtube = new FakeChatService();
    var multi = new MultiChatService(
        (ChatPlatformNames.Twitch, twitch),
        (ChatPlatformNames.YouTube, youtube));
    multi.Dispose();
    Assert.True(youtube.DisposeCalled);
}
```

If `FakeChatService` lacks `SetCanSend`, `SimulateSendThrow`, `SimulateDisposeThrow`, `Sent`, `DisconnectCalled`, `DisposeCalled`, add them (small additions).

- [ ] **Step 12.2: Run tests — expect FAIL**

- [ ] **Step 12.3: Implement send/disconnect/dispose**

Replace the placeholder methods in `MultiChatService.cs`:
```csharp
public void Disconnect() {
    foreach (var c in _children.Values) {
        try { c.Disconnect(); } catch (Exception ex) {
            TiLog.Warn($"[MultiChatService] child Disconnect threw: {ex.Message}");
        }
    }
}

public async Task SendMessageAsync(string text,
    OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
    CancellationToken ct = default) {
    var sendable = _children.Values.Where(c => c.CanSend).ToList();
    if (sendable.Count == 0) {
        TiLog.Debug("[MultiChatService] SendMessageAsync: no CanSend children; receipt skipped");
        return;
    }
    int successCount = 0;
    foreach (var c in sendable) {
        try { await c.SendMessageAsync(text, priority, ct); successCount++; }
        catch (Exception ex) {
            TiLog.Warn($"[MultiChatService] child SendMessageAsync threw: {ex.Message}");
        }
    }
    if (successCount == 0) {
        TiLog.Warn($"[MultiChatService] SendMessageAsync: all {sendable.Count} sendable children failed; receipt dropped");
    }
}

public void Dispose() {
    for (int i = 0; i < _stateHandlers.Count; i++) {
        var (name, handler) = _stateHandlers[i];
        if (_children.TryGetValue(name, out var child)) {
            try { child.ConnectionStateChanged -= handler; }
            catch { /* swallow; child may already be disposed */ }
        }
    }
    foreach (var c in _children.Values) {
        try { c.MessageReceived -= OnChildMessageReceived; } catch { }
        try { c.Dispose(); } catch (Exception ex) {
            TiLog.Warn($"[MultiChatService] child Dispose threw: {ex.Message}");
        }
    }
}
```

- [ ] **Step 12.4: Run tests — expect PASS**

- [ ] **Step 12.5: Commit**

```powershell
git add src/Ti/Chat/MultiChatService.cs tests/Chat/MultiChatServiceTests.cs
git commit -m "yt-chat/12.1: MultiChatService Send (partial-failure tolerant) + Dispose (unsubscribes)"
```

---

## Phase 4: YouTubeChat infrastructure

Phase 4 builds the YouTube-specific scraper infrastructure: DTOs, HTTP abstraction, status enum, exception type, discovery, scraper. The `YouTubeChatService` (which ties these together with a state machine) comes in Phase 5.

### Task 13: `YouTubeChatModels` DTO records

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/YouTubeChatModels.cs`

Spec reference: §"`YouTubeLiveChatScraper`" — internal record types.

- [ ] **Step 13.1: Create the file**

Create `src/Ti/Chat/YouTubeChat/YouTubeChatModels.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Parsed result of YouTubeLiveChatScraper.ParseInitialPageAsync.
/// </summary>
internal sealed record InitialPageParseResult(
    string InnertubeApiKey,
    string ClientVersion,
    string InitialContinuation);

/// <summary>
/// Parsed result of YouTubeLiveChatScraper.PollAsync.
/// </summary>
internal sealed record PollResult(
    IReadOnlyList<ParsedChatMessage> Messages,
    string? NextContinuation,
    int NextTimeoutMs);

/// <summary>
/// One parsed message from a liveChatTextMessageRenderer or paid-message renderer.
/// Author display name (not channel ID) used for ChatMessage.Login per Decision 9.
/// </summary>
internal sealed record ParsedChatMessage(
    string AuthorChannelId,
    string AuthorDisplayName,
    string Text,
    bool IsChatMember,
    bool IsChatModerator);
```

- [ ] **Step 13.2: Build to verify compile**

```powershell
dotnet build
```
Expected: SUCCESS.

- [ ] **Step 13.3: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatModels.cs
git commit -m "yt-chat/13.1: YouTubeChatModels internal DTO records"
```

---

### Task 14: `YouTubeChatStatusReason` enum + `YouTubeHttpStatusException`

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/YouTubeChatStatusReason.cs`
- Create: `src/Ti/Chat/YouTubeChat/YouTubeHttpStatusException.cs`

Spec reference: §"`YouTubeChatStatusReason` enum" + §"Typed HTTP status exception".

- [ ] **Step 14.1: Create the enum**

Create `src/Ti/Chat/YouTubeChat/YouTubeChatStatusReason.cs`:
```csharp
namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Reason the YouTube chat service is in its current state. Surfaced to ModEntry
/// for reason-specific D8 receipt wording. InvalidOrUnavailableChannel was
/// removed in v4 — D7 explicitly does not disambiguate permanent 404s from
/// transient ones.
/// </summary>
public enum YouTubeChatStatusReason {
    None,
    NoLiveBroadcastFound,
    LiveBroadcastEnded,
    NetworkError,
    RateLimited,
    ScraperParseFailed,
    UnknownError,
}
```

- [ ] **Step 14.2: Create the exception type**

Create `src/Ti/Chat/YouTubeChat/YouTubeHttpStatusException.cs`:
```csharp
using System;
using System.Net;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Thrown by IYouTubeHttp on non-success status codes. Carries the status code
/// and any Retry-After delta so the reconnect cadence can honor it (per D7's
/// 429 carve-out).
/// </summary>
internal sealed class YouTubeHttpStatusException : Exception {
    public HttpStatusCode StatusCode { get; }
    public TimeSpan? RetryAfter { get; }

    public YouTubeHttpStatusException(HttpStatusCode statusCode, TimeSpan? retryAfter, string message)
        : base(message) {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }
}
```

- [ ] **Step 14.3: Build to verify compile**

```powershell
dotnet build
```

- [ ] **Step 14.4: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatStatusReason.cs src/Ti/Chat/YouTubeChat/YouTubeHttpStatusException.cs
git commit -m "yt-chat/14.1: YouTubeChatStatusReason enum + YouTubeHttpStatusException typed error"
```

---

### Task 15: `IYouTubeHttp` interface + `YouTubeHttp` production impl

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/IYouTubeHttp.cs`
- Create: `src/Ti/Chat/YouTubeChat/YouTubeHttp.cs`
- Test: `tests/Chat/YouTubeChat/YouTubeHttpTests.cs`

Spec reference: §"HTTP client lifecycle" — CONSENT cookie, UA, 15s timeout, throws YouTubeHttpStatusException.

- [ ] **Step 15.1: Write failing tests for HTTP behavior**

Create `tests/Chat/YouTubeChat/YouTubeHttpTests.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeHttpTests {
    [Fact]
    public async Task GetWithRedirectAsync_Returns_Response_On_Success() {
        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var http = new YouTubeHttp(handler);
        var response = await http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetWithRedirectAsync_Sends_Consent_Cookie() {
        string? capturedCookieHeader = null;
        var handler = new FakeHttpMessageHandler(req => {
            capturedCookieHeader = req.Headers.TryGetValues("Cookie", out var v)
                ? string.Join("; ", v) : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new YouTubeHttp(handler);
        await http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default);
        Assert.NotNull(capturedCookieHeader);
        Assert.Contains("CONSENT=YES+cb", capturedCookieHeader);
    }

    [Fact]
    public async Task GetWithRedirectAsync_Throws_YouTubeHttpStatusException_On_429() {
        var handler = new FakeHttpMessageHandler(req => {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return resp;
        });
        using var http = new YouTubeHttp(handler);
        var ex = await Assert.ThrowsAsync<YouTubeHttpStatusException>(() =>
            http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        => Task.FromResult(_handler(request));
}
```

- [ ] **Step 15.2: Run tests — expect FAIL (types don't exist)**

```powershell
dotnet test --filter "FullyQualifiedName~YouTubeHttpTests"
```

- [ ] **Step 15.3: Create `IYouTubeHttp` interface**

Create `src/Ti/Chat/YouTubeChat/IYouTubeHttp.cs`:
```csharp
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

/// <summary>
/// Thin HTTP abstraction for the YouTube scraper. Throws YouTubeHttpStatusException
/// on non-success responses (status code + Retry-After preserved).
/// </summary>
internal interface IYouTubeHttp : IDisposable {
    Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, CancellationToken ct);
    Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, CancellationToken ct);
}
```

- [ ] **Step 15.4: Create `YouTubeHttp` production impl**

Create `src/Ti/Chat/YouTubeChat/YouTubeHttp.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeHttp : IYouTubeHttp {
    private const string ChromeUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private readonly HttpClient _client;
    private readonly bool _ownsHandler;

    public YouTubeHttp() : this(BuildDefaultHandler(), ownsHandler: true) { }

    // Test-friendly constructor (FakeHttpMessageHandler injection).
    internal YouTubeHttp(HttpMessageHandler handler, bool ownsHandler = false) {
        _ownsHandler = ownsHandler;
        if (handler is HttpClientHandler clientHandler) {
            // Real production path: handler ALREADY has CookieContainer with CONSENT cookie.
            _client = new HttpClient(handler, disposeHandler: ownsHandler) {
                Timeout = TimeSpan.FromSeconds(15),
            };
        } else {
            // Test path: wrap handler to inject CONSENT cookie via header (CookieContainer
            // isn't honored by arbitrary HttpMessageHandlers).
            _client = new HttpClient(handler, disposeHandler: ownsHandler) {
                Timeout = TimeSpan.FromSeconds(15),
            };
            _client.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+cb");
        }
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUA);
    }

    private static HttpClientHandler BuildDefaultHandler() {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("https://www.youtube.com/"),
                    new Cookie("CONSENT", "YES+cb", "/", ".youtube.com"));
        return new HttpClientHandler {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            CookieContainer = cookies,
            UseCookies = true,
        };
    }

    public async Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, CancellationToken ct) {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        EnsureSuccessOrThrow(resp, url);
        return resp;
    }

    public async Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, CancellationToken ct) {
        var req = new HttpRequestMessage(HttpMethod.Post, url) {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        EnsureSuccessOrThrow(resp, url);
        return resp;
    }

    private static void EnsureSuccessOrThrow(HttpResponseMessage resp, Uri url) {
        if (resp.IsSuccessStatusCode) return;
        var retryAfter = resp.Headers.RetryAfter?.Delta;
        throw new YouTubeHttpStatusException(
            resp.StatusCode, retryAfter,
            $"HTTP {(int)resp.StatusCode} from {url}");
    }

    public void Dispose() => _client.Dispose();
}
```

- [ ] **Step 15.5: Run tests — expect PASS**

```powershell
dotnet test --filter "FullyQualifiedName~YouTubeHttpTests"
```

- [ ] **Step 15.6: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/IYouTubeHttp.cs src/Ti/Chat/YouTubeChat/YouTubeHttp.cs tests/Chat/YouTubeChat/YouTubeHttpTests.cs
git commit -m "yt-chat/15.1: IYouTubeHttp + production YouTubeHttp (CONSENT cookie, UA, 15s timeout, typed exception)"
```

---

### Task 16: `IYouTubeLiveBroadcastDiscovery` + `YouTubeLiveBroadcastDiscovery`

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/IYouTubeLiveBroadcastDiscovery.cs`
- Create: `src/Ti/Chat/YouTubeChat/YouTubeLiveBroadcastDiscovery.cs`
- Test: `tests/Chat/YouTubeChat/YouTubeLiveBroadcastDiscoveryTests.cs`

Spec reference: §"`YouTubeLiveBroadcastDiscovery` (the auto-discovery for D4)".

- [ ] **Step 16.1: Write failing tests**

Create `tests/Chat/YouTubeChat/YouTubeLiveBroadcastDiscoveryTests.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeLiveBroadcastDiscoveryTests {
    [Fact]
    public async Task Redirect_To_Watch_Url_Returns_VideoId() {
        var http = MakeFakeHttp(finalUri: "https://www.youtube.com/watch?v=ABCD1234");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("ABCD1234", result);
    }

    [Fact]
    public async Task Redirect_With_Watch_Url_Different_QueryOrder_Returns_VideoId() {
        var http = MakeFakeHttp(finalUri: "https://www.youtube.com/watch?foo=bar&v=ABCD1234&t=10");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("ABCD1234", result);
    }

    [Fact]
    public async Task Redirect_To_Channel_Page_Returns_Null() {
        var http = MakeFakeHttp(finalUri: "https://www.youtube.com/channel/UCfake");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Http_Throws_Returns_Null() {
        var http = new ThrowingFakeHttp();
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Status_429_Returns_Null() {
        var http = new ThrowingFakeHttp(new YouTubeHttpStatusException(
            HttpStatusCode.TooManyRequests, null, "429"));
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    // Helpers
    private static IYouTubeHttp MakeFakeHttp(string finalUri) {
        // Returns a fake IYouTubeHttp whose GetWithRedirectAsync returns a response
        // with RequestMessage.RequestUri = new Uri(finalUri).
        return new RedirectingFakeHttp(finalUri);
    }
}

internal sealed class RedirectingFakeHttp : IYouTubeHttp {
    private readonly string _finalUri;
    public RedirectingFakeHttp(string finalUri) => _finalUri = finalUri;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(_finalUri)),
        };
        return Task.FromResult(resp);
    }
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct) =>
        throw new NotImplementedException();
    public void Dispose() { }
}

internal sealed class ThrowingFakeHttp : IYouTubeHttp {
    private readonly Exception _ex;
    public ThrowingFakeHttp() => _ex = new HttpRequestException("boom");
    public ThrowingFakeHttp(Exception ex) => _ex = ex;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public void Dispose() { }
}
```

- [ ] **Step 16.2: Run tests — expect FAIL**

- [ ] **Step 16.3: Create discovery interface**

Create `src/Ti/Chat/YouTubeChat/IYouTubeLiveBroadcastDiscovery.cs`:
```csharp
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal interface IYouTubeLiveBroadcastDiscovery {
    /// <summary>
    /// Returns the live videoId if the channel has an active broadcast; null otherwise.
    /// All exceptions are caught internally — return value is the sole signal.
    /// </summary>
    Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct);
}
```

- [ ] **Step 16.4: Create discovery impl**

Create `src/Ti/Chat/YouTubeChat/YouTubeLiveBroadcastDiscovery.cs`:
```csharp
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeLiveBroadcastDiscovery : IYouTubeLiveBroadcastDiscovery {
    private readonly IYouTubeHttp _http;

    public YouTubeLiveBroadcastDiscovery(IYouTubeHttp http) {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<string?> FindLiveVideoIdAsync(string channelId, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(channelId)) return null;
        try {
            var url = new Uri($"https://www.youtube.com/channel/{Uri.EscapeDataString(channelId)}/live");
            using var resp = await _http.GetWithRedirectAsync(url, ct).ConfigureAwait(false);
            var finalUri = resp.RequestMessage?.RequestUri;
            if (finalUri is null) return null;
            if (!string.Equals(finalUri.Host, "www.youtube.com", StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.Equals(finalUri.AbsolutePath, "/watch", StringComparison.OrdinalIgnoreCase)) return null;
            var query = HttpUtility.ParseQueryString(finalUri.Query);
            var videoId = query["v"];
            return string.IsNullOrEmpty(videoId) ? null : videoId;
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveBroadcastDiscovery] FindLiveVideoIdAsync threw for {channelId}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
```

- [ ] **Step 16.5: Run tests — expect PASS**

- [ ] **Step 16.6: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/IYouTubeLiveBroadcastDiscovery.cs src/Ti/Chat/YouTubeChat/YouTubeLiveBroadcastDiscovery.cs tests/Chat/YouTubeChat/YouTubeLiveBroadcastDiscoveryTests.cs
git commit -m "yt-chat/16.1: YouTubeLiveBroadcastDiscovery — channel/{ID}/live redirect-follow"
```

---

### Task 17: `IYouTubeLiveChatScraper` + `ParseInitialPageAsync`

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/IYouTubeLiveChatScraper.cs`
- Create: `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs`
- Test: `tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs`

Spec reference: §"`YouTubeLiveChatScraper`" — initial-page parse + clientVersion extraction.

- [ ] **Step 17.1: Add a small fixture-loading test base**

Create `tests/Chat/YouTubeChat/FixtureLoader.cs`:
```csharp
using System.IO;
namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

internal static class FixtureLoader {
    public static string Load(string filename) =>
        File.ReadAllText(Path.Combine("Fixtures", filename));
}
```

Ensure `tests/Fixtures/` is copied to test output: edit `tests/slay_the_streamer_2.tests.csproj`:
```xml
<ItemGroup>
  <None Update="Fixtures\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 17.2: Write failing tests for ParseInitialPageAsync**

Create `tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs`:
```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeLiveChatScraperTests {
    [Fact]
    public async Task ParseInitialPage_Extracts_ApiKey_And_ClientVersion_And_Continuation() {
        var html = FixtureLoader.Load("youtube_live_chat_page.html");
        var http = new StaticBodyFakeHttp(html);
        var scraper = new YouTubeLiveChatScraper(http);
        var result = await scraper.ParseInitialPageAsync("FIXTUREvid001", default);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.InnertubeApiKey));
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+\.\d+$", result.ClientVersion);
        Assert.False(string.IsNullOrEmpty(result.InitialContinuation));
    }

    [Fact]
    public async Task ParseInitialPage_Returns_Null_When_ApiKey_Missing() {
        var html = "<html><body>nothing useful here</body></html>";
        var http = new StaticBodyFakeHttp(html);
        var scraper = new YouTubeLiveChatScraper(http);
        var result = await scraper.ParseInitialPageAsync("FIXTUREvid001", default);
        Assert.Null(result);
    }

    [Fact]
    public async Task ParseInitialPage_Http_Throws_Returns_Null() {
        var http = new ThrowingFakeHttp();
        var scraper = new YouTubeLiveChatScraper(http);
        Assert.Null(await scraper.ParseInitialPageAsync("FIXTUREvid001", default));
    }
}

internal sealed class StaticBodyFakeHttp : IYouTubeHttp {
    private readonly string _body;
    public StaticBodyFakeHttp(string body) => _body = body;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public void Dispose() { }
}
```

(Reuses `ThrowingFakeHttp` from Task 16.)

- [ ] **Step 17.3: Run tests — expect FAIL**

- [ ] **Step 17.4: Create scraper interface**

Create `src/Ti/Chat/YouTubeChat/IYouTubeLiveChatScraper.cs`:
```csharp
using System.Threading;
using System.Threading.Tasks;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal interface IYouTubeLiveChatScraper {
    /// <summary>
    /// Fetches the live_chat?v={videoId} page and parses out INNERTUBE_API_KEY,
    /// clientVersion, and the initial continuation token. Returns null on any failure.
    /// </summary>
    Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct);

    /// <summary>
    /// POSTs to /youtubei/v1/live_chat/get_live_chat with the current continuation.
    /// Returns parsed messages + next continuation + next timeoutMs.
    /// Throws YouTubeHttpStatusException on non-2xx; throws on unexpected parse failures
    /// (caller catches and transitions to Reconnecting).
    /// </summary>
    Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct);
}
```

- [ ] **Step 17.5: Create scraper impl with ParseInitialPageAsync**

Create `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs`:
```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

internal sealed class YouTubeLiveChatScraper : IYouTubeLiveChatScraper {
    private const string ScraperRevision = "yt-scraper-2026-05-12-a";

    private static readonly Regex ApiKeyRegex = new(
        @"""INNERTUBE_API_KEY""\s*:\s*""([A-Za-z0-9_-]+)""",
        RegexOptions.Compiled);

    private static readonly Regex ClientVersionRegex = new(
        @"""INNERTUBE_CONTEXT""[^{]*\{[^}]*""client""\s*:\s*\{[^}]*""clientVersion""\s*:\s*""([0-9.]+)""",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ContinuationRegex = new(
        @"""(?:invalidationContinuationData|timedContinuationData)""\s*:\s*\{[^}]*""continuation""\s*:\s*""([A-Za-z0-9_=-]+)""",
        RegexOptions.Compiled);

    private readonly IYouTubeHttp _http;
    private bool _firstSuccessLogged;

    // Single-location health-check telemetry per Round-2 C-10 + C-20.
    private string? _lastFailureLocation;
    private int _consecutiveFailureCount;

    public YouTubeLiveChatScraper(IYouTubeHttp http) {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, CancellationToken ct) {
        try {
            var url = new Uri($"https://www.youtube.com/live_chat?v={Uri.EscapeDataString(videoId)}");
            using var resp = await _http.GetWithRedirectAsync(url, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var apiKeyMatch = ApiKeyRegex.Match(body);
            if (!apiKeyMatch.Success) { RecordFailure("INNERTUBE_API_KEY_regex", body); return null; }

            var versionMatch = ClientVersionRegex.Match(body);
            if (!versionMatch.Success) { RecordFailure("INNERTUBE_CONTEXT_clientVersion", body); return null; }

            var continuationMatch = ContinuationRegex.Match(body);
            if (!continuationMatch.Success) { RecordFailure("initial_continuation_extract", body); return null; }

            RecordSuccess(videoId);
            return new InitialPageParseResult(
                InnertubeApiKey: apiKeyMatch.Groups[1].Value,
                ClientVersion: versionMatch.Groups[1].Value,
                InitialContinuation: continuationMatch.Groups[1].Value);
        } catch (Exception ex) {
            TiLog.Debug($"[YouTubeLiveChatScraper] ParseInitialPageAsync threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) =>
        throw new NotImplementedException("Implemented in Task 18");

    private void RecordSuccess(string videoId) {
        _lastFailureLocation = null;
        _consecutiveFailureCount = 0;
        if (!_firstSuccessLogged) {
            TiLog.Info($"[YouTubeLiveChatScraper] scraper {ScraperRevision} active; tracking videoId={videoId}");
            _firstSuccessLogged = true;
        }
    }

    private void RecordFailure(string location, string responseBody) {
        if (_lastFailureLocation == location) {
            _consecutiveFailureCount++;
        } else {
            _lastFailureLocation = location;
            _consecutiveFailureCount = 1;
        }
        if (_consecutiveFailureCount == 5) {
            var structuralSample = BuildStructuralSample(responseBody);
            TiLog.Error($"[YouTubeLiveChatScraper] 5 consecutive parse failures at {location}; structural sample: {structuralSample}", null);
        }
    }

    /// <summary>
    /// PII-safe structural summary: response length, top-level JSON keys observed (best-effort grep,
    /// not full parse), renderer type names observed. NEVER includes message text or author info.
    /// </summary>
    private static string BuildStructuralSample(string body) {
        var len = body.Length;
        var topKeys = Regex.Matches(body, @"""([A-Za-z]+)""\s*:", RegexOptions.None)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(k => k.EndsWith("Renderer") || k == "responseContext" || k == "contents" || k == "continuationContents")
            .Take(10);
        return $"length={len}, observed keys = [{string.Join(", ", topKeys)}]";
    }
}
```

Note: the `BuildStructuralSample` uses `System.Linq` — add `using System.Linq;` at top.

- [ ] **Step 17.6: Run tests — expect PASS**

The fixture file `tests/Fixtures/youtube_live_chat_page.html` must contain real-looking `INNERTUBE_API_KEY`, `INNERTUBE_CONTEXT` with `clientVersion`, and a continuation token. Use the spike capture from Task 1.

- [ ] **Step 17.7: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/IYouTubeLiveChatScraper.cs src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs tests/Chat/YouTubeChat/FixtureLoader.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "yt-chat/17.1: YouTubeLiveChatScraper.ParseInitialPageAsync + health-check telemetry"
```

---

### Task 18: `YouTubeLiveChatScraper.PollAsync`

**Files:**
- Modify: `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs`
- Modify: `tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs`
- Create test fixtures: `tests/Fixtures/youtube_live_chat_paid_message.json`, `tests/Fixtures/youtube_live_chat_members_only.json`, `tests/Fixtures/youtube_live_chat_malformed_renderer.json`

Spec reference: §"`YouTubeLiveChatScraper` (the load-bearing fragility)" — `PollAsync` extraction logic.

- [ ] **Step 18.1: Build minimal additional fixtures**

Hand-craft (or trim from the Phase 0 capture) three small JSON fixtures:

`tests/Fixtures/youtube_live_chat_paid_message.json` — a response containing ONE `liveChatPaidMessageRenderer` whose `message.runs` contains text `#1`. And ONE `liveChatMembershipItemRenderer` (no text).

`tests/Fixtures/youtube_live_chat_members_only.json` — a response with empty `actions` array and no continuation (simulated members-only response).

`tests/Fixtures/youtube_live_chat_malformed_renderer.json` — a response containing one `liveChatTextMessageRenderer` MISSING its `message.runs` field (defensive parsing test).

- [ ] **Step 18.2: Write failing tests**

Append to `YouTubeLiveChatScraperTests.cs`:
```csharp
[Fact]
public async Task PollAsync_Normal_Text_Messages_Extracted() {
    var body = FixtureLoader.Load("youtube_live_chat_2026-05-12.json");
    var http = new PostBodyFakeHttp(body);
    var scraper = new YouTubeLiveChatScraper(http);
    var result = await scraper.PollAsync("KEY", "1.0.0.0", "CONT", default);
    Assert.NotEmpty(result.Messages);
    Assert.NotNull(result.NextContinuation);
    Assert.True(result.NextTimeoutMs > 0);
}

[Fact]
public async Task PollAsync_Paid_Message_With_Text_Counted_As_Normal() {
    var body = FixtureLoader.Load("youtube_live_chat_paid_message.json");
    var http = new PostBodyFakeHttp(body);
    var scraper = new YouTubeLiveChatScraper(http);
    var result = await scraper.PollAsync("KEY", "1.0.0.0", "CONT", default);
    // Paid message with text "#1" extracted; text-less membership item skipped.
    Assert.Contains(result.Messages, m => m.Text == "#1");
}

[Fact]
public async Task PollAsync_MalformedRenderer_Skipped_Other_Messages_Parsed() {
    var body = FixtureLoader.Load("youtube_live_chat_malformed_renderer.json");
    var http = new PostBodyFakeHttp(body);
    var scraper = new YouTubeLiveChatScraper(http);
    var result = await scraper.PollAsync("KEY", "1.0.0.0", "CONT", default);
    // Malformed renderer skipped; the result may have other messages or be empty,
    // but the call must not throw.
    Assert.NotNull(result);
}

[Fact]
public async Task PollAsync_MembersOnly_Like_Returns_Empty_With_Null_Continuation() {
    var body = FixtureLoader.Load("youtube_live_chat_members_only.json");
    var http = new PostBodyFakeHttp(body);
    var scraper = new YouTubeLiveChatScraper(http);
    var result = await scraper.PollAsync("KEY", "1.0.0.0", "CONT", default);
    Assert.Empty(result.Messages);
    Assert.Null(result.NextContinuation);
}
```

Add helper `PostBodyFakeHttp` like `StaticBodyFakeHttp` but for `PostJsonAsync`.

- [ ] **Step 18.3: Run tests — expect FAIL**

- [ ] **Step 18.4: Implement `PollAsync`**

Replace the throw in `YouTubeLiveChatScraper.PollAsync` with:
```csharp
public async Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, CancellationToken ct) {
    var url = new Uri($"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={Uri.EscapeDataString(apiKey)}");
    var requestJson = JsonSerializer.Serialize(new {
        context = new { client = new { clientName = "WEB", clientVersion = clientVersion } },
        continuation = continuation,
    });
    using var resp = await _http.PostJsonAsync(url, requestJson, ct).ConfigureAwait(false);
    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;

    if (!root.TryGetProperty("continuationContents", out var contents) ||
        !contents.TryGetProperty("liveChatContinuation", out var continuationObj)) {
        // Members-only / broadcast-ended-like.
        return new PollResult(Array.Empty<ParsedChatMessage>(), null, 0);
    }

    var messages = new List<ParsedChatMessage>();
    if (continuationObj.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array) {
        foreach (var action in actions.EnumerateArray()) {
            var msg = TryParseAction(action);
            if (msg is not null) messages.Add(msg);
        }
    }

    string? nextContinuation = null;
    int nextTimeoutMs = 0;
    if (continuationObj.TryGetProperty("continuations", out var conts) &&
        conts.ValueKind == JsonValueKind.Array && conts.GetArrayLength() > 0) {
        var first = conts[0];
        // Try invalidationContinuationData or timedContinuationData.
        if (TryGetContinuationData(first, out var contData)) {
            if (contData.TryGetProperty("continuation", out var c)) nextContinuation = c.GetString();
            if (contData.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var tms)) nextTimeoutMs = tms;
        }
    }
    return new PollResult(messages, nextContinuation, nextTimeoutMs);
}

private static bool TryGetContinuationData(JsonElement first, out JsonElement contData) {
    if (first.TryGetProperty("invalidationContinuationData", out contData)) return true;
    if (first.TryGetProperty("timedContinuationData", out contData)) return true;
    contData = default;
    return false;
}

private static ParsedChatMessage? TryParseAction(JsonElement action) {
    try {
        if (!action.TryGetProperty("addChatItemAction", out var addAction)) return null;
        if (!addAction.TryGetProperty("item", out var item)) return null;

        JsonElement renderer;
        bool isText = item.TryGetProperty("liveChatTextMessageRenderer", out renderer);
        bool isPaid = !isText && item.TryGetProperty("liveChatPaidMessageRenderer", out renderer);
        if (!isText && !isPaid) return null;   // membership item, sticker, etc. — skip

        if (!renderer.TryGetProperty("message", out var message)) return null;
        if (!message.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;

        var textBuilder = new System.Text.StringBuilder();
        foreach (var run in runs.EnumerateArray()) {
            if (run.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) {
                textBuilder.Append(t.GetString());
            }
            // Skip emoji/image runs silently (defensive runs[] iteration).
        }
        var text = textBuilder.ToString();

        // Author channel ID — required per spec; drop message if missing (no anon-GUID fallback).
        string? channelId = null;
        if (renderer.TryGetProperty("authorExternalChannelId", out var aid) && aid.ValueKind == JsonValueKind.String) {
            channelId = aid.GetString();
        }
        if (string.IsNullOrEmpty(channelId)) {
            TiLog.Debug("[YouTubeLiveChatScraper] skipped message with missing authorExternalChannelId");
            return null;
        }

        string displayName = "";
        if (renderer.TryGetProperty("authorName", out var name) &&
            name.TryGetProperty("simpleText", out var nameText)) {
            displayName = nameText.GetString() ?? "";
        }

        bool isMember = false, isMod = false;
        if (renderer.TryGetProperty("authorBadges", out var badges) && badges.ValueKind == JsonValueKind.Array) {
            foreach (var badge in badges.EnumerateArray()) {
                if (!badge.TryGetProperty("liveChatAuthorBadgeRenderer", out var br)) continue;
                if (br.TryGetProperty("customThumbnails", out _)) isMember = true;
                if (br.TryGetProperty("icon", out var icon) &&
                    icon.TryGetProperty("iconType", out var iconType) &&
                    iconType.GetString() == "MODERATOR") isMod = true;
            }
        }

        return new ParsedChatMessage(channelId, displayName, text, isMember, isMod);
    } catch (Exception ex) {
        TiLog.Debug($"[YouTubeLiveChatScraper] TryParseAction skipped action due to exception: {ex.Message}");
        return null;
    }
}
```

Add `using System.Text.Json;` and `using System.Collections.Generic;` at top.

- [ ] **Step 18.5: Run tests — expect PASS**

- [ ] **Step 18.6: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs tests/Chat/YouTubeChat/YouTubeLiveChatScraperTests.cs tests/Fixtures/youtube_live_chat_paid_message.json tests/Fixtures/youtube_live_chat_members_only.json tests/Fixtures/youtube_live_chat_malformed_renderer.json
git commit -m "yt-chat/18.1: YouTubeLiveChatScraper.PollAsync (paid-text extracted; defensive runs[]; channelId required)"
```

---

## Phase 5: YouTubeChatService

Phase 5 ties Phase 4's scraper components into a state machine implementing `IChatService`. Each task adds one slice of behavior with TDD.

### Task 19: `YouTubeChatService` skeleton + state machine + `TransitionTo`

**Files:**
- Create: `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`
- Test: `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`

Spec reference: §"`YouTubeChatService` (the read-only chat impl)" — State machine + service shape + concurrency subsection.

- [ ] **Step 19.1: Write failing tests for skeleton + initial state**

Create `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`:
```csharp
using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using SlayTheStreamer2.Ti.Internal;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeChatServiceTests {
    [Fact]
    public void Initial_State_Is_Disconnected() {
        var svc = MakeService();
        Assert.Equal(ChatConnectionState.Disconnected, svc.State);
    }

    [Fact]
    public void CanSend_Is_Always_False() {
        var svc = MakeService();
        Assert.False(svc.CanSend);
    }

    [Fact]
    public async Task SendMessageAsync_Throws_NotSupported() {
        var svc = MakeService();
        await Assert.ThrowsAsync<NotSupportedException>(() => svc.SendMessageAsync("hello"));
    }

    [Fact]
    public void LastStatusReason_Initial_Is_None() {
        var svc = MakeService();
        Assert.Equal(YouTubeChatStatusReason.None, svc.LastStatusReason);
    }

    // Helper — define MakeService() that injects fake discovery/scraper/http/dispatcher/clock/scheduler.
    private static YouTubeChatService MakeService(
        IYouTubeLiveBroadcastDiscovery? discovery = null,
        IYouTubeLiveChatScraper? scraper = null) {
        var dispatcher = new ImmediateDispatcher();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var scheduler = new FakeTimerScheduler();
        return new YouTubeChatService(
            dispatcher, clock, scheduler,
            discovery ?? new StubDiscovery(),
            scraper ?? new StubScraper());
    }
}

internal sealed class StubDiscovery : IYouTubeLiveBroadcastDiscovery {
    public string? NextResult { get; set; } = "FIXTUREvid001";
    public Task<string?> FindLiveVideoIdAsync(string channelId, System.Threading.CancellationToken ct) =>
        Task.FromResult(NextResult);
}

internal sealed class StubScraper : IYouTubeLiveChatScraper {
    public InitialPageParseResult? NextInitialResult { get; set; } =
        new InitialPageParseResult("APIKEY", "1.0.0", "CONT0");
    public PollResult NextPollResult { get; set; } =
        new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 5000);
    public Task<InitialPageParseResult?> ParseInitialPageAsync(string videoId, System.Threading.CancellationToken ct) =>
        Task.FromResult(NextInitialResult);
    public Task<PollResult> PollAsync(string apiKey, string clientVersion, string continuation, System.Threading.CancellationToken ct) =>
        Task.FromResult(NextPollResult);
}
```

- [ ] **Step 19.2: Run tests — expect FAIL (type doesn't exist)**

- [ ] **Step 19.3: Create skeleton**

Create `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`:
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Ti.Chat.YouTubeChat;

public sealed class YouTubeChatService : IChatService {
    private readonly IMainThreadDispatcher _dispatcher;
    private readonly IClock _clock;
    private readonly ITimerScheduler _scheduler;
    private readonly IYouTubeLiveBroadcastDiscovery _discovery;
    private readonly IYouTubeLiveChatScraper _scraper;

    private int _disposed;   // Interlocked
    private ChatConnectionState _state = ChatConnectionState.Disconnected;
    private string? _channelId;

    public ChatConnectionState State => _state;
    public bool IsConnected => _state is
        ChatConnectionState.ConnectedReadOnly or
        ChatConnectionState.Reconnecting;
    public bool CanSend => false;
    public DateTimeOffset? LastMessageReceivedAt { get; private set; }
    public Exception? LastError { get; private set; }
    public YouTubeChatStatusReason LastStatusReason { get; private set; } = YouTubeChatStatusReason.None;

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<ChatConnectionChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<YouTubeEscalationRequestedEventArgs>? EscalationRequested;

    public YouTubeChatService(
        IMainThreadDispatcher dispatcher,
        IClock clock,
        ITimerScheduler scheduler,
        IYouTubeLiveBroadcastDiscovery discovery,
        IYouTubeLiveChatScraper scraper) {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _scraper = scraper ?? throw new ArgumentNullException(nameof(scraper));
    }

    public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
        // Implemented in Task 21
        _channelId = channel;
        return Task.CompletedTask;
    }

    public void Disconnect() { /* Task 21 */ }

    public Task SendMessageAsync(string text,
        OutgoingMessagePriority priority = OutgoingMessagePriority.Normal,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("YouTubeChatService is read-only (D3)."));

    public void Dispose() {
        Interlocked.Exchange(ref _disposed, 1);
        // Full teardown in Task 22.
    }

    private void TransitionTo(
        ChatConnectionState next,
        string reason,
        YouTubeChatStatusReason statusReason) {
        if (_state == next && LastStatusReason == statusReason) return;
        var old = _state;
        _state = next;
        LastStatusReason = statusReason;
        TiLog.Info($"[YouTubeChatService] {old} → {next}: {reason} (reason={statusReason})");
        var args = new ChatConnectionChangedEventArgs(old, next, reason);
        _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args));
    }
}

public sealed record YouTubeEscalationRequestedEventArgs(
    int ConsecutiveReconnectCount,
    YouTubeChatStatusReason LastStatusReason);
```

(Optionally move the record into `MultiChatServiceEvents.cs` or its own file. Spec calls for keeping it in scope of YT.)

- [ ] **Step 19.4: Run tests — expect PASS**

- [ ] **Step 19.5: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatService.cs tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs
git commit -m "yt-chat/19.1: YouTubeChatService skeleton + TransitionTo + LastStatusReason + Interlocked _disposed"
```

---

### Task 20: `YouTubeChatService` connection flow (discovery → page parse → cursor-establishing poll)

**Files:**
- Modify: `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`
- Modify: `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`

Spec reference: §"Initial-poll backlog suppression — explicit flow".

- [ ] **Step 20.1: Add failing tests for connection success + initial-poll suppression**

Append to `YouTubeChatServiceTests.cs`:
```csharp
[Fact]
public async Task ConnectAsync_Successful_Transitions_To_ConnectedReadOnly() {
    var svc = MakeService();
    var tcs = new TaskCompletionSource();
    svc.ConnectionStateChanged += (_, e) => {
        if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
    };
    await svc.ConnectAsync("UCfake");
    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(ChatConnectionState.ConnectedReadOnly, svc.State);
}

[Fact]
public async Task Discovery_Returns_Null_Transitions_To_Reconnecting() {
    var discovery = new StubDiscovery { NextResult = null };
    var svc = MakeService(discovery: discovery);
    var tcs = new TaskCompletionSource();
    svc.ConnectionStateChanged += (_, e) => {
        if (e.NewState == ChatConnectionState.Reconnecting) tcs.TrySetResult();
    };
    await svc.ConnectAsync("UCfake");
    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(YouTubeChatStatusReason.NoLiveBroadcastFound, svc.LastStatusReason);
}

[Fact]
public async Task Initial_Cursor_Establishing_Poll_Does_Not_Emit_Messages() {
    var scraper = new StubScraper {
        NextPollResult = new PollResult(
            new[] { new ParsedChatMessage("UC1", "U1", "#0", false, false) },
            "CONT1", 5000),
    };
    var svc = MakeService(scraper: scraper);
    int received = 0;
    svc.MessageReceived += (_, _) => received++;
    var tcs = new TaskCompletionSource();
    svc.ConnectionStateChanged += (_, e) => {
        if (e.NewState == ChatConnectionState.ConnectedReadOnly) tcs.TrySetResult();
    };
    await svc.ConnectAsync("UCfake");
    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert.Equal(0, received);   // initial poll's messages suppressed
}
```

- [ ] **Step 20.2: Run tests — expect FAIL**

- [ ] **Step 20.3: Implement `ConnectAsync` connection flow**

In `YouTubeChatService.cs`, replace `ConnectAsync`:
```csharp
private CancellationTokenSource? _cts;
private string? _videoId;
private string? _continuation;
private string? _apiKey;
private string? _clientVersion;
private Task? _connectTask;

public Task ConnectAsync(string channel, ChatCredentials? creds = null, CancellationToken ct = default) {
    if (Volatile.Read(ref _disposed) == 1) return Task.CompletedTask;
    if (_state != ChatConnectionState.Disconnected) return Task.CompletedTask;
    _channelId = channel ?? throw new ArgumentNullException(nameof(channel));
    _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    TransitionTo(ChatConnectionState.Connecting, "ConnectAsync called", YouTubeChatStatusReason.None);
    _connectTask = Task.Run(() => RunConnectAsync(_cts.Token));
    return Task.CompletedTask;
}

private async Task RunConnectAsync(CancellationToken ct) {
    try {
        var videoId = await _discovery.FindLiveVideoIdAsync(_channelId!, ct).ConfigureAwait(false);
        if (videoId is null) {
            TransitionTo(ChatConnectionState.Reconnecting,
                "no live broadcast found", YouTubeChatStatusReason.NoLiveBroadcastFound);
            // Reconnect timer arm comes in Task 22.
            return;
        }
        _videoId = videoId;

        var pageResult = await _scraper.ParseInitialPageAsync(videoId, ct).ConfigureAwait(false);
        if (pageResult is null) {
            TransitionTo(ChatConnectionState.Reconnecting,
                "initial page parse failed", YouTubeChatStatusReason.ScraperParseFailed);
            return;
        }
        _apiKey = pageResult.InnertubeApiKey;
        _clientVersion = pageResult.ClientVersion;

        // Cursor-establishing poll: messages discarded.
        var cursorResult = await _scraper.PollAsync(
            _apiKey, _clientVersion, pageResult.InitialContinuation, ct).ConfigureAwait(false);
        if (cursorResult.NextContinuation is null) {
            TransitionTo(ChatConnectionState.Reconnecting,
                "cursor-establishing poll returned no continuation",
                YouTubeChatStatusReason.LiveBroadcastEnded);
            return;
        }
        if (cursorResult.Messages.Count > 0) {
            TiLog.Debug($"[YouTubeChatService] cursor-established; suppressed {cursorResult.Messages.Count} backlog messages");
        }
        _continuation = cursorResult.NextContinuation;

        TransitionTo(ChatConnectionState.ConnectedReadOnly,
            "initial connect succeeded", YouTubeChatStatusReason.None);

        // Steady-state poll loop starts in Task 21.
    } catch (OperationCanceledException) {
        // expected on Disconnect/Dispose
    } catch (YouTubeHttpStatusException ex) {
        LastError = ex;
        var reason = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            ? YouTubeChatStatusReason.RateLimited
            : YouTubeChatStatusReason.NetworkError;
        TransitionTo(ChatConnectionState.Reconnecting,
            $"HTTP {(int)ex.StatusCode} during connect", reason);
    } catch (Exception ex) {
        LastError = ex;
        TransitionTo(ChatConnectionState.Reconnecting,
            $"connect failed: {ex.GetType().Name}", YouTubeChatStatusReason.NetworkError);
    }
}
```

- [ ] **Step 20.4: Run tests — expect PASS**

- [ ] **Step 20.5: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatService.cs tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs
git commit -m "yt-chat/20.1: YouTubeChatService connection flow (discovery → page → cursor-establishing poll)"
```

---

### Task 21: `YouTubeChatService` steady-state poll loop + Disconnect

**Files:**
- Modify: `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`
- Modify: `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`

Spec reference: §"Steady-state poll loop" + §"Concurrency / lifecycle" — `Disconnect()` cancels CTS.

- [ ] **Step 21.1: Add failing tests**

Append:
```csharp
[Fact]
public async Task SteadyState_Emits_Subsequent_Poll_Messages() {
    var scraper = new StubScraperWithSequence();
    scraper.PollResults.Enqueue(new PollResult(Array.Empty<ParsedChatMessage>(), "CONT1", 50));   // cursor
    scraper.PollResults.Enqueue(new PollResult(
        new[] { new ParsedChatMessage("UC1", "U1", "#0", false, false) },
        "CONT2", 50));   // first steady-state — should emit
    var svc = MakeService(scraper: scraper);
    var received = new List<ChatMessage>();
    svc.MessageReceived += (_, m) => received.Add(m);
    await svc.ConnectAsync("UCfake");
    // Wait briefly for steady-state poll to run; FakeTimerScheduler can advance time deterministically.
    await Task.Delay(200);
    Assert.Contains(received, m => m.Text == "#0");
    Assert.StartsWith("yt:", received[0].UserId);
}

[Fact]
public void Disconnect_Transitions_To_Disconnected() {
    var svc = MakeService();
    svc.ConnectAsync("UCfake").GetAwaiter().GetResult();
    svc.Disconnect();
    Assert.Equal(ChatConnectionState.Disconnected, svc.State);
}
```

Add `StubScraperWithSequence` helper that returns sequenced `PollResult`s from a Queue.

- [ ] **Step 21.2: Run tests — expect FAIL**

- [ ] **Step 21.3: Implement steady-state loop + Disconnect**

In `YouTubeChatService.cs`, add `RunPollLoopAsync` and call it from `RunConnectAsync` after a successful transition to `ConnectedReadOnly`:
```csharp
// At end of successful RunConnectAsync:
TransitionTo(ChatConnectionState.ConnectedReadOnly, ...);
_pollLoopTask = Task.Run(() => RunPollLoopAsync(_cts!.Token));
return;

private Task? _pollLoopTask;

private async Task RunPollLoopAsync(CancellationToken ct) {
    var minPoll = TimeSpan.FromSeconds(1);
    var maxPoll = TimeSpan.FromSeconds(10);
    int lastTimeoutMs = 5000;
    while (!ct.IsCancellationRequested && _state == ChatConnectionState.ConnectedReadOnly) {
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(lastTimeoutMs, (int)minPoll.TotalMilliseconds, (int)maxPoll.TotalMilliseconds));
        try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        if (Volatile.Read(ref _disposed) == 1) return;
        try {
            var result = await _scraper.PollAsync(_apiKey!, _clientVersion!, _continuation!, ct).ConfigureAwait(false);
            if (result.NextContinuation is null) {
                TransitionTo(ChatConnectionState.Reconnecting,
                    "poll returned no continuation; broadcast ended",
                    YouTubeChatStatusReason.LiveBroadcastEnded);
                return;
            }
            foreach (var msg in result.Messages) {
                if (Volatile.Read(ref _disposed) == 1) return;
                var chatMessage = new ChatMessage(
                    UserId: $"yt:{msg.AuthorChannelId}",
                    Login: msg.AuthorDisplayName,
                    DisplayName: msg.AuthorDisplayName,
                    Text: msg.Text,
                    ReceivedAt: _clock.UtcNow,
                    IsSubscriber: msg.IsChatMember,
                    IsModerator: msg.IsChatModerator,
                    IsVip: false);
                LastMessageReceivedAt = _clock.UtcNow;
                _dispatcher.Post(() => MessageReceived?.Invoke(this, chatMessage));
            }
            _continuation = result.NextContinuation;
            lastTimeoutMs = result.NextTimeoutMs;
        } catch (OperationCanceledException) { return; }
        catch (YouTubeHttpStatusException ex) {
            LastError = ex;
            var reason = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? YouTubeChatStatusReason.RateLimited
                : YouTubeChatStatusReason.NetworkError;
            TransitionTo(ChatConnectionState.Reconnecting,
                $"poll HTTP {(int)ex.StatusCode}", reason);
            return;
        } catch (Exception ex) {
            LastError = ex;
            TransitionTo(ChatConnectionState.Reconnecting,
                $"poll failed: {ex.GetType().Name}",
                YouTubeChatStatusReason.NetworkError);
            return;
        }
    }
}

public void Disconnect() {
    if (Volatile.Read(ref _disposed) == 1) return;
    _cts?.Cancel();
    TransitionTo(ChatConnectionState.Disconnected, "Disconnect called", YouTubeChatStatusReason.None);
}
```

- [ ] **Step 21.4: Run tests — expect PASS**

- [ ] **Step 21.5: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatService.cs tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs
git commit -m "yt-chat/21.1: YouTubeChatService steady-state poll loop + Disconnect"
```

---

### Task 22: Reconnect cadence + 429 carve-out + Dispose

**Files:**
- Modify: `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`
- Modify: `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`

Spec reference: §"Reconnect cadence (per D7 + 429 carve-out)" + §"Concurrency / lifecycle".

- [ ] **Step 22.1: Add failing tests**

```csharp
[Fact]
public async Task Reconnect_ArmsTimer_After_Reconnecting_Transition() {
    var scheduler = new FakeTimerScheduler();
    // ... construct service with scheduler ...
    // verify scheduler.PendingCount > 0 after transition to Reconnecting
}

[Fact]
public async Task RateLimit_429_With_RetryAfter_Uses_Header() {
    var http = new ThrowingFakeHttp(new YouTubeHttpStatusException(
        System.Net.HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(180), "429"));
    // construct service where discovery succeeds but pollAsync throws 429
    // verify next reconnect delay ≈ 180s (within jitter)
}

[Fact]
public async Task Consecutive_429_Backs_Off_Exponentially() {
    // 3 consecutive 429s with no Retry-After header
    // verify delays: 60s → 120s → 240s (within jitter)
}

[Fact]
public void Dispose_Cancels_Pending_Reconnect_Timer() {
    var scheduler = new FakeTimerScheduler();
    // ... construct service in Reconnecting state ...
    int beforeDispose = scheduler.PendingCount;
    svc.Dispose();
    Assert.Equal(0, scheduler.PendingCount);
}
```

Adapt to existing `FakeTimerScheduler` API (`PendingCount`, `AdvanceTime`, etc. — already used by Plan A tests).

- [ ] **Step 22.2: Run tests — expect FAIL**

- [ ] **Step 22.3: Implement `ArmReconnect` + 429 backoff + Dispose**

Add to `YouTubeChatService.cs`:
```csharp
private static readonly TimeSpan ReconnectBase = TimeSpan.FromSeconds(60);
private static readonly TimeSpan ReconnectJitter = TimeSpan.FromSeconds(10);
private static readonly TimeSpan ReconnectCap = TimeSpan.FromSeconds(600);

private IDisposable? _retryTimer;
private int _consecutive429Count;

// Call ArmReconnect() at end of any RunConnectAsync / RunPollLoopAsync path that transitioned to Reconnecting.
private void ArmReconnect() {
    if (Volatile.Read(ref _disposed) == 1) return;
    _retryTimer?.Dispose();
    var delay = NextReconnectDelay(LastError);
    _retryTimer = _scheduler.Schedule(delay, () => {
        if (Volatile.Read(ref _disposed) == 1) return;
        // Clear ephemeral state and re-run connect flow.
        _videoId = null; _continuation = null; _apiKey = null; _clientVersion = null;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        TransitionTo(ChatConnectionState.Connecting, "reconnect timer fired", YouTubeChatStatusReason.None);
        _connectTask = Task.Run(() => RunConnectAsync(_cts.Token));
    });
}

private TimeSpan NextReconnectDelay(Exception? lastError) {
    if (lastError is YouTubeHttpStatusException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } ex) {
        _consecutive429Count++;
        if (ex.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
            return retryAfter + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ReconnectJitter.TotalMilliseconds);
        var backoff = TimeSpan.FromSeconds(Math.Min(60 * Math.Pow(2, _consecutive429Count - 1), ReconnectCap.TotalSeconds));
        return ClampJitter(backoff);
    }
    _consecutive429Count = 0;
    return ClampJitter(ReconnectBase);
}

private static TimeSpan ClampJitter(TimeSpan baseDelay) {
    var jitterMs = (Random.Shared.NextDouble() - 0.5) * 2 * ReconnectJitter.TotalMilliseconds;
    var total = baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    return total < TimeSpan.Zero ? TimeSpan.Zero : total;
}

public void Dispose() {
    if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
    try { _cts?.Cancel(); } catch { }
    try { _cts?.Dispose(); } catch { }
    try { _retryTimer?.Dispose(); } catch { }
    if (_state != ChatConnectionState.Disposed) {
        var old = _state;
        _state = ChatConnectionState.Disposed;
        var args = new ChatConnectionChangedEventArgs(old, ChatConnectionState.Disposed, "Dispose");
        try { _dispatcher.Post(() => ConnectionStateChanged?.Invoke(this, args)); } catch { }
    }
}
```

In `RunConnectAsync` and `RunPollLoopAsync`, call `ArmReconnect()` after every `TransitionTo(Reconnecting, ...)` and reset `_consecutive429Count = 0` on `ConnectedReadOnly` transition (the existing code already calls TransitionTo before each return; add the reset and the `ArmReconnect()` call).

Reset on success — add after the `TransitionTo(ChatConnectionState.ConnectedReadOnly, ...)` line:
```csharp
_consecutive429Count = 0;
```

- [ ] **Step 22.4: Run tests — expect PASS**

- [ ] **Step 22.5: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatService.cs tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs
git commit -m "yt-chat/22.1: YouTubeChatService reconnect cadence + 429 backoff + Dispose"
```

---

### Task 23: Escalation receipt event (30-cycle counter)

**Files:**
- Modify: `src/Ti/Chat/YouTubeChat/YouTubeChatService.cs`
- Modify: `tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs`

Spec reference: §"30-failure escalation receipt" (Round-2 C-1; event-only mechanism).

- [ ] **Step 23.1: Add failing tests**

```csharp
[Fact]
public async Task Escalation_Fires_At_30th_Consecutive_Reconnect() {
    var discovery = new StubDiscovery { NextResult = null };   // always fails
    var svc = MakeService(discovery: discovery);
    YouTubeEscalationRequestedEventArgs? captured = null;
    svc.EscalationRequested += (_, e) => captured = e;
    await svc.ConnectAsync("UCfake");
    // Advance scheduler to fire 30 reconnects.
    // Verify captured is non-null exactly once.
}

[Fact]
public async Task Escalation_Does_Not_Fire_If_Connection_Succeeds_Before_Threshold() {
    // ... 29 fails then a success ...
    // Verify captured is null
}

[Fact]
public async Task Escalation_OneShot_Until_Counter_Resets() {
    // Hit 30 → escalation fires.
    // Continue another 30 fails → escalation does NOT fire again.
    // Then succeed → counter resets.
    // Another 30 fails → escalation fires again.
}
```

- [ ] **Step 23.2: Run tests — expect FAIL**

- [ ] **Step 23.3: Add counter + event raise**

In `YouTubeChatService.cs`:
```csharp
private int _consecutiveReconnectCount;
private bool _escalationSent;

// In ArmReconnect, BEFORE scheduling the timer:
_consecutiveReconnectCount++;
if (_consecutiveReconnectCount == 30 && !_escalationSent) {
    _escalationSent = true;
    var args = new YouTubeEscalationRequestedEventArgs(_consecutiveReconnectCount, LastStatusReason);
    try { EscalationRequested?.Invoke(this, args); }
    catch (Exception ex) { TiLog.Error("[YouTubeChatService] EscalationRequested handler threw", ex); }
}

// After successful ConnectedReadOnly transition (where _consecutive429Count is reset):
_consecutiveReconnectCount = 0;
_escalationSent = false;
```

- [ ] **Step 23.4: Run tests — expect PASS**

- [ ] **Step 23.5: Commit**

```powershell
git add src/Ti/Chat/YouTubeChat/YouTubeChatService.cs tests/Chat/YouTubeChat/YouTubeChatServiceTests.cs
git commit -m "yt-chat/23.1: YouTubeChatService escalation receipt (30-cycle one-shot + EscalationRequested event)"
```

---

## Phase 6: VoteTallyLabel UI

### Task 24: `VoteTallyLabel` split rendering + VoteId header + vote-echo marker + cached text

**Files:**
- Modify: `src/Ti/Ui/VoteTallyLabel.cs`

Spec reference: §"`VoteTallyLabel` (split rendering + vote-echo + cached text)".

This task is hard to unit-test (Godot `RichTextLabel` requires the engine). Validation is operator-driven via Step 4 of the acceptance gate. We still write the code carefully with the patterns the spec requires.

- [ ] **Step 24.1: Update `VoteTallyLabel._Process` and add cache invalidation fields**

Modify `src/Ti/Ui/VoteTallyLabel.cs`:
```csharp
using System;
using System.Linq;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Ti.Ui;

public sealed partial class VoteTallyLabel : RichTextLabel {
    private VoteSession? _session;
    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    // Cache invalidation fields (per Round-2 C-5)
    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;
    private bool _cachedVoteEchoActive;   // tracks whether any vote-echo marker is currently rendered

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
        tree.Root.AddChild(label);
    }

    public override void _Process(double delta) {
        if (!GodotObject.IsInstanceValid(this) || _session is null) return;
        if (_session.State is VoteSessionState.Closed
                              or VoteSessionState.Cancelled
                              or VoteSessionState.Disposed) return;

        var secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
        var tallyVersion = _session.TallyVersion;
        var echoActive = ComputeEchoActive();
        if (secondsLeft == _cachedSecondsLeft &&
            tallyVersion == _cachedTallyVersion &&
            echoActive == _cachedVoteEchoActive) return;

        _cachedSecondsLeft = secondsLeft;
        _cachedTallyVersion = tallyVersion;
        _cachedVoteEchoActive = echoActive;

        var sb = new StringBuilder();
        sb.AppendLine($"Chat voting [{_session.VoteId:D2}] — {secondsLeft}s left, type #N (or #N!{_session.VoteId:D2})");

        var perPlatform = _session.TalliesByPlatform;
        if (perPlatform is null) {
            for (int i = 0; i < _session.Options.Count; i++) {
                _session.Tallies.TryGetValue(i, out var count);
                sb.AppendLine($"#{i} {_session.Options[i].Label}: {count}");
            }
        } else {
            foreach (var platform in _session.ConfiguredPlatforms) {
                sb.Append($"{Capitalize(platform)}: ");
                for (int i = 0; i < _session.Options.Count; i++) {
                    perPlatform.TryGetValue((platform, i), out var count);
                    if (i > 0) sb.Append(", ");
                    sb.Append($"{i}={count}");
                }
                if (IsVoteEchoVisible(platform)) sb.Append(" ◀ just now");
                sb.AppendLine();
            }
        }
        Text = sb.ToString();
    }

    private bool ComputeEchoActive() {
        if (_session is null) return false;
        foreach (var platform in _session.ConfiguredPlatforms)
            if (IsVoteEchoVisible(platform)) return true;
        return false;
    }

    private bool IsVoteEchoVisible(string platform) {
        if (_session is null) return false;
        if (!_session.LastVoteByPlatform.TryGetValue(platform, out var lastVote)) return false;
        return DateTimeOffset.UtcNow - lastVote < TimeSpan.FromSeconds(3);
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    public override void _ExitTree() {
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
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion())
            QueueFree();
    }
}
```

- [ ] **Step 24.2: Build to verify**

```powershell
dotnet build
```
Expected: PASS (Godot-runtime behavior verified during acceptance gate Step 4).

- [ ] **Step 24.3: Commit**

```powershell
git add src/Ti/Ui/VoteTallyLabel.cs
git commit -m "yt-chat/24.1: VoteTallyLabel — split rendering + VoteId header + vote-echo + cached text"
```

---

## Phase 7: Settings + ModEntry wiring + B.2.1 routing update

### Task 25: `ModSettings.youtubeChannelId` with D6 v4 trim-first validation

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Modify: `tests/Bootstrap/ModSettingsTests.cs`

Spec reference: §"`ModSettings` extension (D6 trim-first refinement)" + Decision 6.

- [ ] **Step 25.1: Read existing `ModSettings.cs` to understand its shape**

```powershell
type src/Game/Bootstrap/ModSettings.cs | Select-Object -First 50
```

The implementer must inspect the existing record structure to know exactly where to add the field. The spec assumes a `ChatSettings`-record-like structure with a parser returning `SettingsResult` discriminated union.

- [ ] **Step 25.2: Write failing tests**

Append to `tests/Bootstrap/ModSettingsTests.cs`:
```csharp
[Fact]
public void YoutubeChannelId_Absent_Returns_Success_With_Null() {
    var json = """{"schemaVersion":1,"channel":"foo","username":"bar","oauthToken":"oauth:xxx","cardSkipsPerAct":1}""";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Null(success.Settings.YoutubeChannelId);
}

[Fact]
public void YoutubeChannelId_JsonNull_Returns_Success_With_Null() {
    var json = """{"schemaVersion":1,"channel":"foo","username":"bar","oauthToken":"oauth:xxx","cardSkipsPerAct":1,"youtubeChannelId":null}""";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Null(success.Settings.YoutubeChannelId);
}

[Fact]
public void YoutubeChannelId_EmptyString_Returns_Success_With_Null() {
    var json = """{...,"youtubeChannelId":""}"""; // use real template
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Null(success.Settings.YoutubeChannelId);
}

[Fact]
public void YoutubeChannelId_WhitespaceOnly_Returns_Success_With_Null_No_Warning() {
    var json = """{...,"youtubeChannelId":"   "}""";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Null(success.Settings.YoutubeChannelId);
    Assert.Empty(success.Warnings);
}

[Fact]
public void YoutubeChannelId_Valid_NonEmpty_Preserved() {
    var json = """{...,"youtubeChannelId":"UCabc123def456"}""";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal("UCabc123def456", success.Settings.YoutubeChannelId);
}

[Fact]
public void YoutubeChannelId_WhitespaceSurroundingValid_Trimmed() {
    var json = """{...,"youtubeChannelId":"  UCabc123def456  "}""";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal("UCabc123def456", success.Settings.YoutubeChannelId);
}

[Fact]
public void YoutubeChannelId_ContainsControlChar_Returns_Success_With_Warning_And_Null() {
    var json = "{\"schemaVersion\":1,\"channel\":\"foo\",\"username\":\"bar\",\"oauthToken\":\"oauth:xxx\",\"cardSkipsPerAct\":1,\"youtubeChannelId\":\"UC\\u0001abc\"}";
    var result = ModSettings.LoadFromJson(json);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Null(success.Settings.YoutubeChannelId);
    Assert.Contains(success.Warnings, w => w.Contains("youtubeChannelId", StringComparison.OrdinalIgnoreCase));
}
```

Use the existing JSON template format (the `...` placeholders need filling in with the rest of the required fields).

- [ ] **Step 25.3: Run tests — expect FAIL**

- [ ] **Step 25.4: Add `YoutubeChannelId` to the settings record**

Modify `src/Game/Bootstrap/ModSettings.cs`. Add the property to the settings record (e.g., `ChatSettings` or whatever it's named):
```csharp
public record ChatSettings(
    // ... existing fields ...
    int CardSkipsPerAct,
    string? YoutubeChannelId   // NEW v4
);
```

- [ ] **Step 25.5: Add parsing + validation logic**

In the loader (where the JSON is parsed into the record), add:
```csharp
string? youtubeChannelId = null;
if (root.TryGetProperty("youtubeChannelId", out var ytEl) && ytEl.ValueKind != JsonValueKind.Null) {
    if (ytEl.ValueKind != JsonValueKind.String) {
        // Wrong type — treat as warning + disable YT, per D6 v4 (consistent with control-char path).
        warnings.Add("youtubeChannelId must be a string; YouTube integration disabled.");
    } else {
        var raw = ytEl.GetString() ?? "";
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) {
            // Empty or whitespace-only → clamp to null silently.
            youtubeChannelId = null;
        } else if (trimmed.Any(char.IsControl)) {
            warnings.Add("youtubeChannelId malformed (control characters); YouTube integration disabled.");
            youtubeChannelId = null;
        } else {
            youtubeChannelId = trimmed;
        }
    }
}
// pass youtubeChannelId into the ChatSettings ctor
```

- [ ] **Step 25.6: Run tests — expect PASS**

```powershell
dotnet test --filter "FullyQualifiedName~ModSettingsTests"
```

- [ ] **Step 25.7: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "yt-chat/25.1: ModSettings.YoutubeChannelId — D6 v4 trim-first + control-char warn (YT-only disable)"
```

---

### Task 26: `ModEntry` wires Twitch + optional YouTube + MultiChatService

**Files:**
- Modify: `src/ModEntry.cs`

Spec reference: §"`ModEntry` wiring".

- [ ] **Step 26.1: Read existing `ModEntry.cs` to understand current wiring**

```powershell
type src/ModEntry.cs
```

Identify where `TwitchIrcChatService` is constructed, where `VoteCoordinator` is constructed, and where `Voter.Default` is assigned.

- [ ] **Step 26.2: Update wiring**

Replace the chat construction block:
```csharp
// Existing pattern (approx):
//   var twitch = new TwitchIrcChatService(...);
//   _ = twitch.ConnectAsync(settings.Channel, new ChatCredentials(...));
//   var voter = new VoteCoordinator(twitch, ...);
//   Voter.Default = voter;

// NEW:
var twitch = new TwitchIrcChatService(/* existing args */);
_ = twitch.ConnectAsync(settings.Channel, new ChatCredentials(/* ... */));

YouTubeChatService? youtube = null;
if (!string.IsNullOrEmpty(settings.YoutubeChannelId)) {
    youtube = new YouTubeChatService(
        dispatcher: _dispatcher,
        clock: _clock,
        scheduler: _scheduler,
        discovery: new YouTubeLiveBroadcastDiscovery(_youtubeHttp),
        scraper: new YouTubeLiveChatScraper(_youtubeHttp));
    _ = youtube.ConnectAsync(settings.YoutubeChannelId);
}

var multi = youtube is null
    ? new MultiChatService((ChatPlatformNames.Twitch, (IChatConsumer)twitch))
    : new MultiChatService(
        (ChatPlatformNames.Twitch, (IChatConsumer)twitch),
        (ChatPlatformNames.YouTube, (IChatConsumer)youtube));

var configuredPlatforms = youtube is null
    ? new[] { ChatPlatformNames.Twitch }
    : new[] { ChatPlatformNames.Twitch, ChatPlatformNames.YouTube };

var voter = new VoteCoordinator(multi, configuredPlatforms, _clock, _scheduler, _dispatcher);
Voter.Default = voter;
```

Where `_youtubeHttp` is a single `YouTubeHttp` instance constructed once in `ModEntry` and disposed in teardown. (If `ModEntry` doesn't already have a shared lifetime hook for disposable services, add one — a `List<IDisposable> _disposables` cleared in `Unload`.)

- [ ] **Step 26.3: Build and verify no regressions in existing tests**

```powershell
dotnet build
dotnet test
```

- [ ] **Step 26.4: Commit**

```powershell
git add src/ModEntry.cs
git commit -m "yt-chat/26.1: ModEntry wires MultiChatService(twitch, youtube?) + configuredPlatforms"
```

---

### Task 27: ModEntry D8 receipts — startup + on YT state changes (120s debounce)

**Files:**
- Modify: `src/ModEntry.cs`

Spec reference: §"Startup receipt extension (with 120s flap-suppression)" + Decision 8.

- [ ] **Step 27.1: Subscribe to `ChildConnectionStateChanged` and `EscalationRequested`**

Add to `ModEntry` (after the multi is constructed and Voter.Default assigned):
```csharp
private static readonly TimeSpan ReceiptDebounce = TimeSpan.FromSeconds(120);
private static DateTimeOffset _lastYouTubeReceiptAt = DateTimeOffset.MinValue;
private static bool _twitchStartupReceiptSent;
private static ChatConnectionState _lastTwitchStateForReceipt = ChatConnectionState.Disconnected;
private static ChatConnectionState _lastYouTubeStateForReceipt = ChatConnectionState.Disconnected;

multi.ChildConnectionStateChanged += (_, e) => OnChildConnectionStateChanged(twitch, youtube, e);
if (youtube is not null) {
    youtube.EscalationRequested += (_, e) => OnYouTubeEscalation(twitch, e);
}

private static void OnChildConnectionStateChanged(
    TwitchIrcChatService twitch, YouTubeChatService? youtube,
    ChildConnectionStateChangedEventArgs e) {
    // Twitch first-time-connected receipt (startup).
    if (e.ChildName == ChatPlatformNames.Twitch &&
        e.Inner.NewState == ChatConnectionState.ConnectedReadWrite &&
        !_twitchStartupReceiptSent) {
        _twitchStartupReceiptSent = true;
        var msg = BuildStartupReceipt(youtube);
        _ = twitch.SendMessageAsync(msg, OutgoingMessagePriority.Normal);
    }

    // YouTube state-change receipts (mid-session) — gated by 120s debounce.
    if (e.ChildName == ChatPlatformNames.YouTube && youtube is not null) {
        var now = DateTimeOffset.UtcNow;
        var stateChangedFromLast =
            e.Inner.NewState != _lastYouTubeStateForReceipt;
        var inDebounce = (now - _lastYouTubeReceiptAt) < ReceiptDebounce;

        if (!stateChangedFromLast || inDebounce) return;

        var receipt = BuildYouTubeStateReceipt(youtube, e.Inner.NewState);
        if (receipt is not null) {
            _ = twitch.SendMessageAsync(receipt, OutgoingMessagePriority.Normal);
            _lastYouTubeReceiptAt = now;
            _lastYouTubeStateForReceipt = e.Inner.NewState;
        }
    }
}

private static string BuildStartupReceipt(YouTubeChatService? youtube) {
    if (youtube is null)
        return "slay-the-streamer-2 connected (Twitch).";
    return youtube.State switch {
        ChatConnectionState.ConnectedReadOnly =>
            $"slay-the-streamer-2 connected (Twitch & YouTube tracking).",
        _ =>
            "slay-the-streamer-2 connected (Twitch). YouTube: no live broadcast found, retrying.",
    };
}

private static string? BuildYouTubeStateReceipt(YouTubeChatService youtube, ChatConnectionState newState) {
    return newState switch {
        ChatConnectionState.ConnectedReadOnly =>
            $"YouTube connected: tracking chat.",
        ChatConnectionState.Reconnecting => youtube.LastStatusReason switch {
            YouTubeChatStatusReason.NoLiveBroadcastFound =>
                "YouTube: no live broadcast found, retrying.",
            YouTubeChatStatusReason.LiveBroadcastEnded =>
                "YouTube: live broadcast ended; will resume when next broadcast starts.",
            YouTubeChatStatusReason.NetworkError =>
                "YouTube: connection lost; will retry.",
            YouTubeChatStatusReason.RateLimited =>
                "YouTube: temporarily rate-limited; will retry.",
            YouTubeChatStatusReason.ScraperParseFailed =>
                "YouTube: connection issue; will retry.",
            _ => "YouTube disconnected; will retry every ~60s.",
        },
        _ => null,
    };
}
```

- [ ] **Step 27.2: Build to verify compile**

```powershell
dotnet build
```

- [ ] **Step 27.3: Commit**

```powershell
git add src/ModEntry.cs
git commit -m "yt-chat/27.1: ModEntry D8 startup + state-change receipts with 120s debounce"
```

---

### Task 28: ModEntry escalation receipt subscriber

**Files:**
- Modify: `src/ModEntry.cs`

Spec reference: §"30-failure escalation receipt" (event-only mechanism).

- [ ] **Step 28.1: Implement `OnYouTubeEscalation`**

```csharp
private static void OnYouTubeEscalation(TwitchIrcChatService twitch, YouTubeEscalationRequestedEventArgs e) {
    var msg = "YouTube: still not connected after repeated retries — check \"youtubeChannelId\" in settings (see logs for details).";
    _ = twitch.SendMessageAsync(msg, OutgoingMessagePriority.High);
    TiLog.Warn($"[ModEntry] YouTube escalation fired after {e.ConsecutiveReconnectCount} reconnects; reason={e.LastStatusReason}");
}
```

(The subscription was added in Task 26's setup block; this task implements the handler.)

- [ ] **Step 28.2: Build**

```powershell
dotnet build
```

- [ ] **Step 28.3: Commit**

```powershell
git add src/ModEntry.cs
git commit -m "yt-chat/28.1: ModEntry escalation receipt subscriber"
```

---

### Task 29: B.2.1 skip-gate routing through `MultiChatService.GetChildState`

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`

Spec reference: §"Skip-gate routing for B.2.1 Decision 21 amendment".

- [ ] **Step 29.1: Locate the existing `ShouldEnforceSkipGate` method**

```powershell
type src/Game/DecisionVotes/CardRewardSkipGatePatch.cs | Select-String "ShouldEnforceSkipGate" -Context 0,30
```

- [ ] **Step 29.2: Add MultiChatService-aware routing**

Replace the existing `ShouldEnforceSkipGate` body:
```csharp
private static bool ShouldEnforceSkipGate() {
    if (ModEntry.Settings is not SettingsResult.Success) return false;
    if (!CardRewardVotePatch.PreparedSuccessfully) return false;
    if (Voter.Default == null) return false;

    // Route Twitch-state-check explicitly (per v4 Round-2 C-3 / Must-do #1)
    if (Voter.Default.Chat is MultiChatService multi) {
        var twitchState = multi.GetChildState(ChatPlatformNames.Twitch);
        if (twitchState is ChatConnectionState.AuthenticationFailed
                        or ChatConnectionState.JoinFailed
                        or ChatConnectionState.Disposed) return false;
    } else {
        // Direct-Twitch path (defensive — shouldn't happen post-v4 since ModEntry always wires multi)
        if (Voter.Default.Chat.State is ChatConnectionState.AuthenticationFailed
                                      or ChatConnectionState.JoinFailed
                                      or ChatConnectionState.Disposed) return false;
    }
    return true;
}
```

- [ ] **Step 29.3: Build and run all tests**

```powershell
dotnet build
dotnet test
```
Expected: PASS. No regressions in B.2.1 tests.

- [ ] **Step 29.4: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m "yt-chat/29.1: CardRewardSkipGatePatch routes Twitch-state-check via MultiChatService.GetChildState"
```

---

## Phase 8: Docs + operator validation

### Task 30: `notes/youtube-fixture-refresh.md` monthly task documentation

**Files:**
- Create: `notes/youtube-fixture-refresh.md`

Spec reference: §"Fixture-refresh task (C9 applied)".

- [ ] **Step 30.1: Create the file**

```markdown
# YouTube fixture refresh — monthly maintenance task

**Cadence**: monthly OR when scraper health-check telemetry (`[YouTubeLiveChatScraper] N consecutive parse failures at ...`) starts firing.

**Why**: YouTube's `youtubei` endpoint is undocumented and changes silently. The `tests/Fixtures/youtube_live_chat_*.json` fixtures rot when YouTube ships a redesign; refreshing them monthly catches regressions before they bite during a live operator-validation session.

## Manual refresh process

1. Pick a public live broadcast (YouTube channel currently streaming). Note the channel ID.
2. Capture the channel/live redirect:
   ```powershell
   curl -i -L "https://www.youtube.com/channel/$CHANNEL_ID/live" `
       -A "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36" `
       -b "CONSENT=YES+cb"
   ```
   Note the final `Location:` header — confirm it matches `/watch?v={VIDEO_ID}`.

3. Capture the `live_chat?v=...` page:
   ```powershell
   curl -s "https://www.youtube.com/live_chat?v=$VIDEO_ID" `
       -A "Mozilla/5.0 ..." -b "CONSENT=YES+cb" `
       -o tests/Fixtures/youtube_live_chat_page.html
   ```
   Inspect the file:
   - `INNERTUBE_API_KEY` value (regex `"INNERTUBE_API_KEY":"([A-Za-z0-9_-]+)"`).
   - `INNERTUBE_CONTEXT.client.clientVersion`.
   - Initial continuation token nested under `liveChatContinuation.continuations[0]`.

4. Capture a `get_live_chat` POST response:
   ```powershell
   $body = '{"context":{"client":{"clientName":"WEB","clientVersion":"$CV"}},"continuation":"$CONT"}'
   curl -X POST "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key=$API_KEY" `
       -H "Content-Type: application/json" -A "Mozilla/5.0 ..." -b "CONSENT=YES+cb" `
       -d $body -o tests/Fixtures/youtube_live_chat_2026-MM-DD.raw.json
   ```

5. Anonymize:
   - `authorExternalChannelId` → `UCfixture001`, `UCfixture002`, ...
   - `authorName.simpleText` → `Fixture Author 1`, ...
   - `message.runs[*].text` → benign text (e.g., `Test message #0`).
   - `videoId` → `FIXTUREvid001`.

6. Save anonymized to `tests/Fixtures/youtube_live_chat_2026-MM-DD.json`. Archive the prior fixture.

7. Run scraper tests:
   ```powershell
   dotnet test --filter "FullyQualifiedName~YouTubeLiveChatScraperTests"
   ```
   If anything fails, the parser needs updating — see `src/Ti/Chat/YouTubeChat/YouTubeLiveChatScraper.cs`.

8. Bump `ScraperRevision` constant in `YouTubeLiveChatScraper.cs` to the current date.

9. Commit: `chore(yt-chat): refresh fixtures YYYY-MM-DD`.

## Redesign-response checklist

If a YouTube redesign breaks the scraper, the failures are scoped to:
- `ApiKeyRegex` — update regex pattern.
- `ClientVersionRegex` — update regex pattern.
- `ContinuationRegex` — update regex pattern.
- JSON traversal in `PollAsync` — check renderer type names and field paths.

The health-check telemetry log line tells you exactly which location is failing.
```

- [ ] **Step 30.2: Commit**

```powershell
git add notes/youtube-fixture-refresh.md
git commit -m "yt-chat/30.1: notes/youtube-fixture-refresh.md monthly maintenance task"
```

---

### Task 31: Operator-validation gate (manual; documented)

This task produces no code. It's the operator's manual execution of acceptance gate Steps 0-7 from the v4 spec, run after all prior tasks land.

**Files:**
- Modify: `notes/06-followups-and-deferred.md` (append a "YouTube chat parallel integration — acceptance gate results" section at the bottom with results per step)

- [x] **Step 31.1: Run Step 0 — Vanilla regression (Twitch-only, new code path)** ✅ 2026-05-12

Set `settings.json` with no `youtubeChannelId`. Run StS2. Trigger a Neow vote AND a card-reward vote. Verify all B.1/B.2.1 behavior identical to v0.1. In-game label uses single-platform rendering.

**Result**: PASSED. Mod loaded at hash `344db72`; all vanilla B.1/B.2.1 behavior intact; new aggregator + IChatConsumer split + B.2.1 skip-gate-routing-via-GetChildState changes proven non-regressing.

- [x] **Step 31.2: Run Step 1 — YT-only smoke** ✅ 2026-05-12

Set `settings.json` with valid `youtubeChannelId` + deliberately bad `oauthToken`. Run StS2. Verify:
- Twitch lands in `AuthenticationFailed`.
- YT child connects (assuming a live broadcast exists).
- YT messages flow into VoteSession.
- In-game label renders YouTube row.
- Card-skip gate is degraded.
- No chat receipts fire.

**Result**: PASSED against FrostPrime's live channel. Validated end-to-end via Surfinite's single `1` chat message → counted as YT vote → applied to in-game state. Discovery + scraper + poll loop + JSON renderer parse + vote regex + tally + UI all proven against real YouTube response shapes. Three follow-up fixes landed during validation:
- `yt-chat/15.2`: CONSENT cookie sent as direct header (CookieContainer.Add(Uri,Cookie) silently dropped the cookie in production).
- `yt-chat/16.2`: Discovery body-parses `<link rel="canonical">` instead of redirect-follow (YouTube no longer redirects `/channel/{ID}/live` → `/watch?v=`).
- `yt-chat/17.2`: Continuation regex supports `reloadContinuationData` + URL-encoded `%`-chars in tokens.
- `yt-chat/29.2`: Vote-start gate accepts `ConnectedReadOnly` (not just `ConnectedReadWrite`) so YT-only mode actually runs votes per the v4 spec's supported-degraded-mode promise.

- [ ] **Step 31.3: Run Step 2 — Dual-platform happy path (3 runs)**

Set both Twitch and `youtubeChannelId` valid. Real live YT broadcast active. Three runs covering Neow vote, card-reward vote, card-reward skip. Verify split tally rendering + merged close-receipts + Skip Gang `#0` works on both platforms.

- [ ] **Step 31.4: Run Step 3 — YT failure modes (sub-steps 3a-3e)**

3a: No live broadcast at startup; verify D8 receipt fires, retry every ~60s, then start broadcast mid-session and verify auto-reconnect + D8 "YouTube connected" receipt.

3b: YT broadcast ends mid-session; verify D8 "YouTube disconnected" receipt.

3c: Typo `youtubeChannelId`. Verify never-ending retry, no panic. After ~30 min, verify escalation receipt: `YouTube: still not connected after repeated retries — check "youtubeChannelId" ...`.

3d: Disable network briefly; verify `Reconnecting` + auto-reconnect.

3e: Simulated scraper regression (mock the scraper to return malformed initial-page response — done via dev-flag or test hook). Verify isolation — Twitch and gate unaffected.

3f (NEW v4): Cookie-cleared discovery. Clear cookies; verify the pre-set CONSENT cookie still works (discovery succeeds, no consent.youtube.com redirect failure).

- [ ] **Step 31.5: Run Step 4 — Split tally label correctness**

Dual-platform vote in flight. Verify label updates real-time. Twitch vote → Twitch line updates only. YT vote → YT line + "◀ just now" marker for ~3s. Latest-wins decrement works correctly. Merged close-receipt matches platform sum.

- [ ] **Step 31.6: Run Step 5 — Cross-platform double-count (per D1)**

Same human votes on both Twitch and YouTube. Verify 2 votes counted (one per platform).

- [ ] **Step 31.7: Run Step 6 — Twitch-only-deployment + D6 settings parsing**

6a: `youtubeChannelId: null`. Mod loads as Success, no YT codepath.

6b: `youtubeChannelId: ""`. Same.

6c: `youtubeChannelId: "  "` (whitespace-only). Same.

6c-malformed: `youtubeChannelId: "UCabc"`. Verify mod loads Success with warning logged; YT disabled; Twitch continues.

- [ ] **Step 31.8: Run Step 7 — Receipt flap suppression + delivery verification**

Rapidly toggle YT broadcast on/off. Verify receipts fire at most once per 120s. Verify all expected receipts DO appear (no drop to Twitch ratelimit under combined burst).

- [ ] **Step 31.9: Record results in `notes/06`**

Append to `notes/06-followups-and-deferred.md`:
```markdown
## YouTube chat parallel integration — acceptance gate results

- [ ] Step 0: vanilla regression — <PASS / FAIL with notes>
- [ ] Step 1: YT-only smoke — <result>
- [ ] Step 2: dual-platform happy path (3 runs) — <result>
- [ ] Step 3: YT failure modes — <sub-step results>
- [ ] Step 4: split tally label correctness — <result>
- [ ] Step 5: cross-platform double-count — <result>
- [ ] Step 6: Twitch-only-deployment + D6 — <sub-step results>
- [ ] Step 7: receipt flap suppression + delivery — <result>

<Notes on any surprises, polish items deferred, etc.>
```

- [ ] **Step 31.10: Final commit + tag**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "yt-chat/31.1: acceptance gate results recorded"
git tag yt-chat-v0.2-complete
```

---

## Spec coverage self-review

Cross-check against v4 spec sections:

| v4 section | Covered by task(s) |
|---|---|
| Goals (1-6) | Tasks 2-29 (architectural span) |
| Non-goals | Out of scope by design |
| Supported degraded modes | Tasks 25-26, 31 (operator validation) |
| Decisions D1-D11 | D1 (Task 6 cross-platform); D2 (no impl; deferred); D3 (Task 19 CanSend=false); D4 (Task 16 discovery); D5 (Task 18 members-only test); D6 (Task 25); D7 (Tasks 22, 23); D8 (Task 27); D9 (Tasks 6, 21); D10 (Tasks 6, 24); D11 (Tasks 5, 8, 9) |
| TI extraction modularity | Documentation only (spec section) |
| `IChatConsumer` / `IChatService` split | Task 2 |
| `ChatPlatformNames` | Task 3 |
| `YouTubeChatService` (full spec) | Tasks 13-23 |
| HTTP client lifecycle (CONSENT cookie) | Task 15 |
| Typed 429 exception | Task 14 + Task 15 + Task 22 |
| Reconnect cadence + 429 carve-out | Task 22 |
| Initial-poll backlog suppression | Task 20 |
| `YouTubeChatStatusReason` enum | Task 14 + Task 20+ all transition sites |
| 30-failure escalation receipt | Tasks 23 + 28 |
| Concurrency / lifecycle | Tasks 19, 21, 22 |
| `YouTubeLiveChatScraper` (paid msg + defensive runs + clientVersion + health telemetry) | Tasks 17, 18 |
| `YouTubeLiveBroadcastDiscovery` | Task 16 |
| `MultiChatService` (full spec) | Tasks 10, 11, 12 |
| `VoteSession` per-platform tally + `ConfiguredPlatforms` + `VoteId` + `TallyVersion` | Tasks 5, 6, 7, 8 |
| `EnglishReceipts.FormatOpen` with VoteId | Task 9 |
| `VoteTallyLabel` split rendering + cache + vote-echo | Task 24 |
| `ModSettings` extension | Task 25 |
| `ModEntry` wiring + skip-gate routing | Tasks 26, 27, 28, 29 |
| Failure modes & degradation | Verified by Tasks 31.4 (Step 3) |
| Acceptance gate (Steps 0-7) | Task 31 |
| Fixture-refresh task | Task 30 |

All v4 spec sections covered.

---

## Plan complete

Plan saved to `docs/superpowers/plans/2026-05-12-youtube-chat-integration.md`. Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task, review between tasks, fast iteration. Uses `superpowers:subagent-driven-development`.

**2. Inline Execution** — execute tasks in this session via `superpowers:executing-plans`, batch execution with checkpoints.

Which approach?


