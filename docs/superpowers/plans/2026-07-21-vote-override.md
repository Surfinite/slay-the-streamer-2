# Vote Override Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The streamer can override a running chat vote (card rewards + ancients) a configurable number of times per act (`voteOverridesPerAct`, default 1) by clicking an option mid-countdown; the vote ends immediately with the clicked option as winner.

**Architecture:** New `VoteSession.TryCloseNow(forcedWinnerIndex)` in `Ti/Voting` fires the existing `Closed` event with a forced winner, so the untouched resume machinery applies it. Game-side, the two existing suppressed-click branches (card `SelectCard` prefix, `OnAlternateRewardSelected` prefix; ancient `OptionButtonClicked` prefix) become override entry points. A Godot-free `VoteOverrideBudget` static wraps a second instance of the renamed `ActBudgetTracker`. Spec: `docs/superpowers/specs/2026-07-21-vote-override-design.md`.

**Tech Stack:** C# / .NET 9, HarmonyLib 2.4, Godot 4.5 (mod side), xUnit (test project is `Microsoft.NET.Sdk` — no Godot/game types).

## Global Constraints

- `src/Ti/**` may be modified (Task 1) but must reference only BCL + Godot + System.Net.Http — **never** `MegaCrit.Sts2.*` or `src/Game/*` (TI/Game seam).
- Vote options are **0-indexed** (`#0`, `#1`, …) end-to-end; with `CardSkipAsVoteOption` on, Skip is `#0` and cards shift by +1.
- `tests/slay_the_streamer_2.tests.csproj` globs `..\src\Game\DecisionVotes\**\*.cs` with per-file `<Compile Remove>` for Harmony-bearing files — any new file in that folder that references Godot/game types MUST get a `Compile Remove`; Godot-free files ride the glob and are unit-testable.
- Ti test classes that trigger `TiLog` MUST be `[Collection("TiLog.Sink")]`.
- Vote-adjacent Ti tests use `VoteSessionTestBase` + its fake triad — never construct raw coordinators/fakes.
- Every Harmony prefix/postfix body change wrapped in try/catch; on exception log via `TiLog` with the existing per-patch prefix (`[SlayTheStreamer2][card-vote]` / `[ancient-vote]` / `[card-skip-gate]`) and fall back to today's behavior.
- Never block the Godot main thread on vote results (suspend-and-resume pattern only).
- `voteOverridesPerAct` semantics: `-1` unlimited, `0` disabled, `≥1` per-act budget; default **1**. Arming delay fixed at **1.5s**.
- Feature-off (`0`), terminal-chat, and MP states: click behavior must be byte-for-byte today's (silent suppression).
- Commit prefix `vote-override/N:`; build+test via `pwsh -File build.ps1` (never `dotnet build` alone); deploy via `pwsh -File install.ps1`.
- Chat receipt sends gated on `ChatConnectionState.ConnectedReadWrite`, fire-and-forget.

---

### Task 1: `Ti/Voting` — `TryCloseNow` + `Elapsed` + `VoteSnapshot.ForcedWinner` (TDD)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs`
- Modify: `src/Ti/Voting/VoteSnapshot.cs`
- Modify: `CLAUDE.md` (add `vote-override/N:` to the commit-conventions list)
- Test: `tests/Voting/VoteSessionForcedCloseTests.cs` (create)

**Interfaces:**
- Consumes: existing `VoteSession` internals (`_state`, `_winnerTcs`, timers, handlers).
- Produces (used by Tasks 5 & 7):
  - `public bool TryCloseNow(int forcedWinnerIndex)` — `false` unless `State == Open`; throws `ArgumentOutOfRangeException` for `forcedWinnerIndex < 0 || >= Options.Count`; on success sets `WinnerIndex`, completes the awaiter, fires `Closed`, sends **no** close receipt.
  - `public TimeSpan Elapsed { get; }` — time since the session opened.
  - `VoteSnapshot` gains trailing `bool ForcedWinner = false`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Voting/VoteSessionForcedCloseTests.cs`:

```csharp
using System;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionForcedCloseTests : VoteSessionTestBase {

    [Fact]
    public void TryCloseNow_sets_forced_winner_and_fires_Closed_once() {
        var s = StartVote();                    // options: Bash, Defend, Strike
        int closedCount = 0;
        s.Closed += (_, _) => closedCount++;
        InjectTwitchVote(s, "1001", 0);
        InjectTwitchVote(s, "1002", 0);         // chat leader is #0
        _ = s.AwaitWinnerAsync();               // suppress the never-awaited warn

        Assert.True(s.TryCloseNow(2));          // streamer forces #2 anyway

        Assert.Equal(VoteSessionState.Closed, s.State);
        Assert.Equal(2, s.WinnerIndex);
        Assert.Equal(1, closedCount);
    }

    [Fact]
    public async Task TryCloseNow_completes_AwaitWinnerAsync_with_forced_index() {
        var s = StartVote();
        var winnerTask = s.AwaitWinnerAsync();
        Assert.True(s.TryCloseNow(1));
        Assert.Equal(1, await winnerTask);
    }

    [Fact]
    public void TryCloseNow_sends_no_close_receipt() {
        var s = StartVote();                    // open receipt already sent
        _ = s.AwaitWinnerAsync();
        int before = Chat.SentMessages.Count;
        Assert.True(s.TryCloseNow(1));
        Assert.Equal(before, Chat.SentMessages.Count);
    }

    [Fact]
    public void Natural_CloseNow_still_sends_close_receipt() {
        // Regression guard: the no-receipt rule is forced-close-only.
        var s = StartVote();
        _ = s.AwaitWinnerAsync();
        int before = Chat.SentMessages.Count;
        s.CloseNow();
        Assert.Equal(before + 1, Chat.SentMessages.Count);
    }

    [Fact]
    public void TryCloseNow_returns_false_on_closed_and_cancelled_sessions() {
        var closed = StartVote();
        _ = closed.AwaitWinnerAsync();
        int naturalWinner = closed.CloseNow();
        Assert.False(closed.TryCloseNow(1));
        Assert.Equal(naturalWinner, closed.WinnerIndex);   // untouched

        var cancelled = StartVote();
        cancelled.Cancel();
        Assert.False(cancelled.TryCloseNow(1));
        Assert.Equal(VoteSessionState.Cancelled, cancelled.State);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TryCloseNow_throws_on_out_of_range_index(int idx) {
        var s = StartVote();                    // 3 options -> valid 0..2
        Assert.Throws<ArgumentOutOfRangeException>(() => s.TryCloseNow(idx));
        Assert.Equal(VoteSessionState.Open, s.State);   // still open after throw
        s.Cancel();
    }

    [Fact]
    public void Snapshot_carries_ForcedWinner_flag() {
        var forced = StartVote();
        _ = forced.AwaitWinnerAsync();
        forced.TryCloseNow(0);
        Assert.True(forced.Snapshot().ForcedWinner);

        var natural = StartVote();
        _ = natural.AwaitWinnerAsync();
        natural.CloseNow();
        Assert.False(natural.Snapshot().ForcedWinner);
    }

    [Fact]
    public void Elapsed_tracks_clock_advance() {
        var s = StartVote();
        Assert.Equal(TimeSpan.Zero, s.Elapsed);
        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), s.Elapsed);
        s.Cancel();
    }

    [Fact]
    public void Close_timer_is_dead_after_forced_close() {
        var s = StartVote(duration: TimeSpan.FromSeconds(30));
        int closedCount = 0;
        s.Closed += (_, _) => closedCount++;
        _ = s.AwaitWinnerAsync();
        Assert.True(s.TryCloseNow(0));
        // Advance past natural expiry; the disposed close timer must not re-fire.
        // (If FakeClock/FakeTimerScheduler use a different advance helper here,
        // copy the exact mechanism from the natural-expiry tests in VoteSessionTests.)
        Clock.Advance(TimeSpan.FromSeconds(60));
        Scheduler.RunDueTimers();
        Assert.Equal(1, closedCount);
        Assert.Equal(0, s.WinnerIndex);
    }
}
```

Note: if `FakeClock`/`FakeTimerScheduler` expose different advance/run methods than `Clock.Advance` / `Scheduler.RunDueTimers`, use whatever the existing timer-driven tests in `tests/Voting/VoteSessionTests.cs` use — the assertions stay the same.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter VoteSessionForcedClose`
Expected: FAIL — compile error CS1061 (`TryCloseNow`, `Elapsed`, `ForcedWinner` do not exist).

- [ ] **Step 3: Implement**

In `src/Ti/Voting/VoteSnapshot.cs`, append a trailing member with a default (existing construction sites keep compiling):

```csharp
    int VoteId,                           // cycling 0..99 nonce assigned by VoteCoordinator; lets receipts/UI disambiguate consecutive votes
    bool ShowTag = true,                  // settings-ui/2.1: display-only toggle; parser remains defensive
    bool ForcedWinner = false             // vote-override: WinnerIndex was forced via TryCloseNow, not tallied
);
```

In `src/Ti/Voting/VoteSession.cs`:

Add a field next to `_noVotesReceived`:

```csharp
    private bool _forcedWinner;
```

Add the `Elapsed` property next to `TimeRemaining`:

```csharp
    public TimeSpan Elapsed => _clock.UtcNow - _openedAt;
```

Add `TryCloseNow` after `CloseNow()` / `CloseNowInternal`:

```csharp
    /// <summary>
    /// Close the vote immediately with a forced winner (streamer override).
    /// Returns false without side effects unless the session is Open — callers
    /// must consume override budget only on true. Fires the normal Closed
    /// event (popups/tally tear down as on natural close) but sends NO close
    /// receipt: override messaging is the caller's job, because the receipt
    /// needs game-side context (streamer name, remaining budget) that Ti must
    /// not know.
    /// </summary>
    public bool TryCloseNow(int forcedWinnerIndex) {
        if (forcedWinnerIndex < 0 || forcedWinnerIndex >= Options.Count)
            throw new ArgumentOutOfRangeException(nameof(forcedWinnerIndex),
                $"forced winner {forcedWinnerIndex} outside option range 0..{Options.Count - 1}");
        if (_state != VoteSessionState.Open) return false;

        if (_disconnectStartedAt is { } start) {
            _disconnectGapAccum += _clock.UtcNow - start;
            _disconnectStartedAt = null;
        }
        _state = VoteSessionState.Closing;
        WinnerIndex = forcedWinnerIndex;
        _forcedWinner = true;
        _chat.MessageReceived -= OnChatMessage;
        _chat.ConnectionStateChanged -= OnChatConnectionStateChanged;
        _closeTimer.Dispose();
        _periodicTimer?.Dispose();
        _state = VoteSessionState.Closed;
        _winnerTcs.TrySetResult(forcedWinnerIndex);
        if (!_anyoneAwaited)
            TiLog.Warn($"VoteSession {Id} force-closed with winner {forcedWinnerIndex} but AwaitWinnerAsync was never called; caller likely forgot to consume the result.");
        Closed?.Invoke(this, this);
        return true;
    }
```

In `Snapshot()`, pass the flag through (add after `ShowTag`):

```csharp
            ShowTag: _showTag,
            ForcedWinner: _forcedWinner);
```

In `CLAUDE.md`, add to the commit-conventions list after the Bossy Relics line:

```markdown
- Vote Override (streamer overrides a running vote): `vote-override/N:`
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter VoteSessionForcedClose`
Expected: PASS (9 tests). Then run the full Ti voting suite to catch regressions:
`dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter Voting`
Expected: PASS, no failures.

- [ ] **Step 5: Commit**

```bash
git add src/Ti/Voting/VoteSession.cs src/Ti/Voting/VoteSnapshot.cs tests/Voting/VoteSessionForcedCloseTests.cs CLAUDE.md
git commit -m "vote-override/1: VoteSession.TryCloseNow forced-winner close + Elapsed + snapshot flag"
```

---

### Task 2: Rename `SkipBudgetTracker` → `ActBudgetTracker`

**Files:**
- Rename: `src/Game/DecisionVotes/SkipBudgetTracker.cs` → `src/Game/DecisionVotes/ActBudgetTracker.cs`
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` (usages)
- Modify: `src/Game/Ui/CardSkipCounterLabel.cs` (snapshot type in `UpdateText`)
- Rename: `tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs` → `tests/Game/DecisionVotes/ActBudgetTrackerTests.cs`

**Interfaces:**
- Produces (used by Tasks 4–7):
  - `internal sealed class ActBudgetTracker` with `int ActUsed`, `BudgetResetReason ObserveRunAndAct(string? runId, int? actIndex)`, `bool IsUseAllowed(int actLimit)`, `void RecordUse()`, `ActBudgetSnapshot Snapshot(int actLimit)`, `internal void ResetForTests()`, `internal void ResetCounterOnly()`.
  - `internal readonly record struct ActBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct)` (member names unchanged).
  - `BudgetResetReason` enum unchanged.

- [ ] **Step 1: Rename file and identifiers**

```bash
git mv src/Game/DecisionVotes/SkipBudgetTracker.cs src/Game/DecisionVotes/ActBudgetTracker.cs
git mv tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs tests/Game/DecisionVotes/ActBudgetTrackerTests.cs
```

In `ActBudgetTracker.cs` rename: class `SkipBudgetTracker` → `ActBudgetTracker`; field `_actSkipsUsed` → `_actUsed`; property `ActSkipsUsed` → `ActUsed`; method `IsSkipAllowed` → `IsUseAllowed`; method `RecordSkip` → `RecordUse`; record `SkipBudgetSnapshot` → `ActBudgetSnapshot`. Update the class doc comment ("Pure budget arithmetic" stays accurate — it now serves both the skip and override budgets). Keep `ObserveRunAndAct`, `Snapshot`, `ResetForTests`, `ResetCounterOnly`, `BudgetResetReason` names as-is.

- [ ] **Step 2: Update all usages**

```bash
grep -rn "SkipBudgetTracker\|SkipBudgetSnapshot\|ActSkipsUsed\|RecordSkip\|IsSkipAllowed" src/ tests/
```

Expected hits (update each to the new names): `CardRewardSkipGatePatch.cs` (`_tracker` declaration, `ActSkipsUsed` reads in `ResetBudgetForDevConsole` / `TryConsumeStreamerSkip` / `OnProceedButtonPressed` prefix, `RecordSkip()` calls in `TryConsumeStreamerSkip` / `AfterOverlayClosed` prefix, `FormatSkipReceipt`'s caller), `CardSkipCounterLabel.cs` (`UpdateText(SkipBudgetSnapshot snap)` → `UpdateText(ActBudgetSnapshot snap)`), and the renamed test file (class name `ActBudgetTrackerTests`, all `new SkipBudgetTracker()` → `new ActBudgetTracker()`, member renames). No functional changes anywhere — mechanical rename only.

- [ ] **Step 3: Build + test**

Run: `pwsh -File build.ps1`
Expected: build succeeds, full test suite passes (the renamed tracker tests run under the new name; count unchanged).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "vote-override/2: rename SkipBudgetTracker -> ActBudgetTracker (generic per-act budget)"
```

---

### Task 3: `voteOverridesPerAct` setting end-to-end

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`
- Modify: `src/Game/Bootstrap/SettingsBootstrap.cs`
- Modify: `src/Game/Ui/Settings/SettingsWriter.cs`
- Modify: `src/Game/Ui/Settings/SettingsPanelBuilder.cs`
- Modify: `src/slay_the_streamer_2.json.example`
- Test: `tests/Bootstrap/ModSettingsTests.cs`, `tests/Bootstrap/SettingsBootstrapTests.cs`, `tests/Game/Ui/Settings/SettingsWriterTests.cs`

**Interfaces:**
- Produces (used by Tasks 4–7): `ChatSettings.VoteOverridesPerAct` (`int`, default `1`; `-1` unlimited, `0` disabled), persisted as JSON key `voteOverridesPerAct`.

This task is a verbatim clone of the `relicChoices` plumbing (commit `dedee90`), with `cardSkipsPerAct`'s clamp semantics (no upper bound; below `-1` clamps to `-1`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Bootstrap/ModSettingsTests.cs` (before the `WriteTempJson` helper):

```csharp
    // --- voteOverridesPerAct (vote-override: streamer mid-vote override budget, default 1) ---

    [Theory]
    [InlineData("\"voteOverridesPerAct\": 3,", 3, false)]
    [InlineData("\"voteOverridesPerAct\": 1,", 1, false)]
    [InlineData("\"voteOverridesPerAct\": 0,", 0, false)]     // 0 = disabled, valid
    [InlineData("\"voteOverridesPerAct\": -1,", -1, false)]   // -1 = unlimited, valid
    [InlineData("\"voteOverridesPerAct\": -5,", -1, true)]    // below -1 -> clamp + warning
    [InlineData("\"voteOverridesPerAct\": \"two\",", 1, true)] // non-int -> default + warning
    [InlineData("", 1, false)]                                 // missing -> default, no warning
    public void VoteOverridesPerAct_parses_clamps_and_defaults(string fragment, int expected, bool expectWarning) {
        var path = WriteTempJson($$"""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            {{fragment}}
            "cardSkipsPerAct": 1
        }
        """);
        try {
            var result = ModSettings.Load(path);
            var success = Assert.IsType<SettingsResult.Success>(result);
            Assert.Equal(expected, success.Settings.VoteOverridesPerAct);
            Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("voteOverridesPerAct")));
        } finally { File.Delete(path); }
    }
```

Append to `tests/Bootstrap/SettingsBootstrapTests.cs` (next to the relicChoices pair):

```csharp
    [Fact]
    public void Template_contains_voteOverridesPerAct_default_1() {
        var json = JsonNode.Parse(SettingsBootstrap.BuildTemplateJson())!.AsObject();
        Assert.Equal(1, (int)json["voteOverridesPerAct"]!);
    }

    [Fact]
    public void AddMissingKeys_adds_voteOverridesPerAct_to_old_files() {
        var json = JsonNode.Parse("{\"schemaVersion\":1}")!.AsObject();
        var added = SettingsBootstrap.AddMissingKeys(json);
        Assert.Contains("voteOverridesPerAct", added);
        Assert.Equal(1, (int)json["voteOverridesPerAct"]!);
    }
```

Append to `tests/Game/Ui/Settings/SettingsWriterTests.cs` (next to the relicChoices persist test):

```csharp
    [Fact]
    public void Write_persists_voteOverridesPerAct() {
        var path = TempPath();
        try {
            var settings = MakeSettings() with { VoteOverridesPerAct = 2 };
            SettingsWriter.Write(path, settings);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(2, (int)json["voteOverridesPerAct"]!);
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter VoteOverridesPerAct`
Expected: FAIL — compile error (no `VoteOverridesPerAct` member on `ChatSettings`).

- [ ] **Step 3: Implement**

`src/Game/Bootstrap/ModSettings.cs` — append to the `ChatSettings` record (at the END, after `RelicChoices`):

```csharp
    int RelicChoices = 1,
    int VoteOverridesPerAct = 1);
```

In `ModSettings.Load`, after the `relicChoices` parse block:

```csharp
            int voteOverridesPerAct = 1;
            if (root.TryGetProperty("voteOverridesPerAct", out var overridesProp)) {
                if (overridesProp.ValueKind != JsonValueKind.Number || !overridesProp.TryGetInt32(out var rawOverrides)) {
                    warnings.Add("voteOverridesPerAct is not an integer; using default (1)");
                } else if (rawOverrides < -1) {
                    warnings.Add($"voteOverridesPerAct {rawOverrides} clamped to -1 (unlimited)");
                    voteOverridesPerAct = -1;
                } else {
                    voteOverridesPerAct = rawOverrides;
                }
            }
```

And thread it through the success constructor (append after `relicChoices`):

```csharp
                new ChatSettings(normalisedChannel, creds, cardSkipsPerAct, youtubeChannelId, voteOnActVariant, forceL3PopupFallback, voteDurationSeconds, cardSkipAsVoteOption, showVoteTag, voteTallyOnLeft, allowSameBossTwice, relicChoices, voteOverridesPerAct),
```

`src/Game/Bootstrap/SettingsBootstrap.cs` — template dictionary:

```csharp
        ["relicChoices"]         = 1,
        ["voteOverridesPerAct"]  = 1,
```

`src/Game/Ui/Settings/SettingsWriter.cs` — whitelist (the silent-drop trap):

```csharp
        json["relicChoices"] = settings.RelicChoices;
        json["voteOverridesPerAct"] = settings.VoteOverridesPerAct;
```

`src/slay_the_streamer_2.json.example`:

```json
  "relicChoices": 1,
  "voteOverridesPerAct": 1
```

`src/Game/Ui/Settings/SettingsPanelBuilder.cs` — insert after the card-skips block (after the `AddHelpText(root, "Card-rewards streamer can skip before initiating a vote.\nSkips reset each act.");` line):

```csharp
        AddDivider(root);
        AddVoteOverridesDropdown(root, current, debouncer);
        AddHelpText(root, "Times per act the streamer can override a live vote by clicking\nan option mid-countdown. Clicking Skip mid-vote costs an override,\nnot a card skip. Resets each act.");
```

And add the row factory next to `AddCardSkipsDropdown` (same shape; metadata not item-ids — id -1 collides with Godot's auto-assign sentinel):

```csharp
    private static void AddVoteOverridesDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row   = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        inner.AddChild(MakeRowLabel("Streamer vote overrides / act"));

        var dropdown = new OptionButton {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(115, 0),
        };
        if (_kreonRegular != null) dropdown.AddThemeFontOverride("font", _kreonRegular);
        dropdown.AddThemeFontSizeOverride("font_size", 22);
        dropdown.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        (string Label, int Value)[] entries = [
            ("0  (off)", 0),
            ("1", 1),
            ("2", 2),
            ("3", 3),
            ("Unlimited", -1)
        ];
        int selectedIdx = -1;
        for (int i = 0; i < entries.Length; i++) {
            dropdown.AddItem(entries[i].Label);
            dropdown.SetItemMetadata(i, entries[i].Value);
            if (entries[i].Value == current.VoteOverridesPerAct) selectedIdx = i;
        }
        if (selectedIdx == -1) {
            dropdown.AddItem($"Custom ({current.VoteOverridesPerAct})");
            int customIdx = dropdown.ItemCount - 1;
            dropdown.SetItemMetadata(customIdx, current.VoteOverridesPerAct);
            selectedIdx = customIdx;
        }
        dropdown.Selected = selectedIdx;

        var popup = dropdown.GetPopup();
        if (_kreonRegular != null) popup.AddThemeFontOverride("font", _kreonRegular);
        popup.AddThemeFontSizeOverride("font_size", 22);

        dropdown.ItemSelected += idx => {
            var value = dropdown.GetItemMetadata((int)idx).AsInt32();
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { VoteOverridesPerAct = value });
        };

        inner.AddChild(dropdown);
        parent.AddChild(row);
    }
```

(Keep the "Open settings folder" `AddFilePathRow` last.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `pwsh -File build.ps1`
Expected: build succeeds; new settings tests pass; no regressions.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "vote-override/3: voteOverridesPerAct setting end-to-end (default 1, -1 unlimited, 0 off)"
```

---

### Task 4: `VoteOverrideBudget` shared holder (Godot-free) + tests

**Files:**
- Create: `src/Game/DecisionVotes/VoteOverrideBudget.cs`
- Test: `tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs` (create)

**Interfaces:**
- Consumes: `ActBudgetTracker` / `ActBudgetSnapshot` / `BudgetResetReason` (Task 2), `ChatSettings.VoteOverridesPerAct` (Task 3), `Voter.Default` + `ChatConnectionState` + `OutgoingMessagePriority` (existing Ti), `ModSettings.GetStreamerDisplayName()` (existing).
- Produces (used by Tasks 5–7):
  - `static TimeSpan ArmingDelay` (1.5s)
  - `static int Limit` / `static bool Enabled` / `static int Remaining`
  - `static ActBudgetSnapshot Snapshot()`
  - `static BudgetResetReason Observe(string? runId, int? actIndex)`
  - `static void RecordUse()`
  - `static void SendOverrideReceipt(string takenLabel)` / `static void SendResetReceiptIfAny(BudgetResetReason reason, int humanActNumber)`
  - `internal static string FormatOverrideReceipt(string streamerName, string takenLabel, int limit, int remaining)` / `internal static string FormatResetReceipt(int limit, int humanActNumber)`
  - `internal static void ResetForTests()`

This file is deliberately Godot-free (no `Godot.*`, no `MegaCrit.*`) so it rides the test csproj's `DecisionVotes/**` glob — do NOT add a `Compile Remove` for it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs`:

```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class VoteOverrideBudgetTests {

    [Theory]
    [InlineData("Surfinite", "Ricochet", 2, 1, "Surfinite overrode the vote and took Ricochet. 1 override remaining this act")]
    [InlineData("Surfinite", "Ricochet", 3, 2, "Surfinite overrode the vote and took Ricochet. 2 overrides remaining this act")]
    [InlineData("Surfinite", "Skip",     1, 0, "Surfinite overrode the vote and took Skip. 0 overrides remaining this act")]
    [InlineData("Surfinite", "Ricochet", -1, 2147483647, "Surfinite overrode the vote and took Ricochet.")]  // unlimited: no count
    public void FormatOverrideReceipt_covers_plural_zero_and_unlimited(
        string name, string taken, int limit, int remaining, string expected) =>
        Assert.Equal(expected, VoteOverrideBudget.FormatOverrideReceipt(name, taken, limit, remaining));

    [Fact]
    public void FormatResetReceipt_names_limit_and_act() =>
        Assert.Equal("Vote overrides reset to 1 for Act 2", VoteOverrideBudget.FormatResetReceipt(1, 2));

    [Fact]
    public void Observe_and_RecordUse_drive_Snapshot_through_tracker() {
        VoteOverrideBudget.ResetForTests();
        VoteOverrideBudget.Observe("SEED-A", 0);
        VoteOverrideBudget.RecordUse();
        // Limit falls back to default 1 when ModSettings.Current is null (test env).
        var snap = VoteOverrideBudget.Snapshot();
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(0, snap.RemainingThisAct);

        // Act change resets the counter.
        var reason = VoteOverrideBudget.Observe("SEED-A", 1);
        Assert.Equal(BudgetResetReason.ActChanged, reason);
        Assert.Equal(0, VoteOverrideBudget.Snapshot().UsedThisAct);
        VoteOverrideBudget.ResetForTests();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter VoteOverrideBudget`
Expected: FAIL — compile error CS0246 (`VoteOverrideBudget` does not exist).

- [ ] **Step 3: Implement**

Create `src/Game/DecisionVotes/VoteOverrideBudget.cs`:

```csharp
using System;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Voting;
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Shared per-act vote-override budget (spec 2026-07-21 §2.2). One static
/// surface consumed by CardRewardVotePatch, AncientVotePatch, and the
/// streamer budget counter label. Godot-free on purpose: it rides the test
/// csproj's DecisionVotes glob, so no Godot or MegaCrit types may appear here.
/// Main-thread-only, like the tracker it wraps.
/// </summary>
internal static class VoteOverrideBudget {
    /// <summary>Override clicks only register this long after vote start, so
    /// the double-click that opened the vote can't consume an override.</summary>
    public static readonly TimeSpan ArmingDelay = TimeSpan.FromSeconds(1.5);

    private static readonly ActBudgetTracker _tracker = new();

    /// <summary>-1 unlimited, 0 disabled, >=1 per-act budget. Default 1.</summary>
    public static int Limit => BootstrapModSettings.Current?.VoteOverridesPerAct ?? 1;

    public static bool Enabled => Limit != 0;

    public static int Remaining => Limit < 0 ? int.MaxValue : Math.Max(0, Limit - _tracker.ActUsed);

    public static ActBudgetSnapshot Snapshot() => _tracker.Snapshot(Limit);

    public static BudgetResetReason Observe(string? runId, int? actIndex) =>
        _tracker.ObserveRunAndAct(runId, actIndex);

    public static void RecordUse() => _tracker.RecordUse();

    /// <summary>Pure formatter, unit-tested. Unlimited (limit &lt; 0) omits the count.</summary>
    internal static string FormatOverrideReceipt(string streamerName, string takenLabel, int limit, int remaining) {
        if (limit < 0) return $"{streamerName} overrode the vote and took {takenLabel}.";
        string noun = remaining == 1 ? "override" : "overrides";
        return $"{streamerName} overrode the vote and took {takenLabel}. {remaining} {noun} remaining this act";
    }

    internal static string FormatResetReceipt(int limit, int humanActNumber) =>
        $"Vote overrides reset to {limit} for Act {humanActNumber}";

    /// <summary>Replaces the vote's normal close receipt (TryCloseNow sends none).
    /// High priority to match the close receipt it stands in for.</summary>
    public static void SendOverrideReceipt(string takenLabel) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = FormatOverrideReceipt(
            BootstrapModSettings.GetStreamerDisplayName(), takenLabel, Limit, Remaining);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.High);
    }

    /// <summary>Mirrors the skip budget's reset receipt suppression rules:
    /// nothing for limit &lt;= 0 (off/unlimited) or unknown act.</summary>
    public static void SendResetReceiptIfAny(BudgetResetReason reason, int humanActNumber) {
        if (reason == BudgetResetReason.None) return;
        if (Limit <= 0) return;
        if (humanActNumber <= 0) return;
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        _ = coordinator.Chat.SendMessageAsync(
            FormatResetReceipt(Limit, humanActNumber), OutgoingMessagePriority.Normal);
    }

    internal static void ResetForTests() => _tracker.ResetForTests();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter VoteOverrideBudget`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/VoteOverrideBudget.cs tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs
git commit -m "vote-override/4: VoteOverrideBudget shared holder (arming delay, budget, receipts)"
```

---

### Task 5: Card-reward integration (`CardRewardVotePatch` + `CardRewardSkipGatePatch` observe)

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs`
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` (`_Ready` postfix: one observe call)

No new unit tests (Harmony-bearing files are `Compile Remove`d); correctness rides Task 1/4 unit tests + Task 8 operator validation. Build must stay green.

**Interfaces:**
- Consumes: `VoteSession.TryCloseNow` / `.Elapsed` / `.State` / `.Options` (Task 1); `VoteOverrideBudget.*` (Task 4); existing `FindHolderIndex` / `GetCurrentHolders` / `GetCurrentOptions` / `FindSkipAlternativeIndex` / `ResumeSkipOnMainThread`.
- Produces: override-capable card votes; `_overrideSkipPending` routing contract used only inside this file.

- [ ] **Step 1: Add override context statics**

In `CardRewardVotePatch`, next to `_voteInProgress`:

```csharp
    private static VoteSession? _activeSession;
    private static bool _activeIncludeSkip;
    private static int _overrideSkipPending;
```

- [ ] **Step 2: Set context at vote start, defensively observe budget**

In `Prefix`, immediately after the successful `session = coordinator.Start(...)` try/catch block, add:

```csharp
        // Vote-override context: consulted by the suppressed-click branches.
        // _overrideSkipPending cleared defensively — a stale flag from a vote
        // whose Cancel() lost a race must not leak into this vote.
        _activeSession = session;
        _activeIncludeSkip = includeSkip;
        Interlocked.Exchange(ref _overrideSkipPending, 0);
```

Earlier in `Prefix`, right after the run-id capture block (before `Voter.Start`), add the defensive budget observe (rewards-screen `_Ready` is the primary observe site; this covers edge orderings):

```csharp
        // Vote-override budget: best-effort observe so the counter is fresh even
        // if this vote fires before any rewards-screen _Ready this act.
        try {
            var rsForObserve = RunManager.Instance?.DebugOnlyGetState();
            int? actIdx = rsForObserve?.CurrentActIndex;
            var overrideReason = VoteOverrideBudget.Observe(rsForObserve?.Rng?.StringSeed, actIdx);
            VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIdx.HasValue ? actIdx.Value + 1 : 0);
        } catch { /* observe is best-effort */ }
```

- [ ] **Step 3: Card-click override in the suppressed-click branch**

Replace the existing repeat-click branch body:

```csharp
        // Atomic vote-in-progress flip
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            if (TryOverrideWithCard(__instance, cardHolder)) return false;
            TiLog.Debug("[SlayTheStreamer2][card-vote] repeat click during open vote; suppressed");
            return false;
        }
```

Add the helper (near `FindSkipAlternativeIndex`):

```csharp
    /// <summary>
    /// Streamer override: an armed click on a card during an open vote ends the
    /// vote immediately with that card as the winner and consumes one override
    /// (spec 2026-07-21 §2.3). Returns false — with the click staying suppressed —
    /// whenever any precondition fails; the caller then logs the normal
    /// suppressed-click line. Budget is consumed strictly AFTER TryCloseNow
    /// succeeds (never consume on a lost close-timer race).
    /// </summary>
    private static bool TryOverrideWithCard(NCardRewardSelectionScreen screen, NCardHolder clicked) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;

            var holders = GetCurrentHolders(screen);
            var options = GetCurrentOptions(screen);
            if (holders is null || options is null) return false;
            int? cardIndex = FindHolderIndex(holders, clicked);
            if (cardIndex is null || cardIndex.Value >= options.Count) return false;

            int voteIndex = _activeIncludeSkip ? cardIndex.Value + 1 : cardIndex.Value;
            string takenLabel = options[cardIndex.Value].Card.Title;

            if (!session.TryCloseNow(voteIndex)) return false;

            VoteOverrideBudget.RecordUse();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel);
            TiLog.Info($"[SlayTheStreamer2][card-vote] override: streamer forced #{voteIndex} ({takenLabel}); {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] override attempt failed; click suppressed", ex);
            return false;
        }
    }
```

- [ ] **Step 4: Skip-click override in the `OnAlternateRewardSelected` prefix**

Replace branch (2) of `NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix.Prefix`:

```csharp
            // (2) Vote in progress — streamer cannot bail via alt-select, but an
            // armed Skip click with override budget ends the vote as an override.
            if (_voteInProgress == 1) {
                if (TryOverrideWithSkip(__instance, index)) return false;
                TiLog.Info("[SlayTheStreamer2][card-vote] OnAlternateRewardSelected blocked: vote in progress");
                return false;
            }
```

Add the helper next to `TryOverrideWithCard`:

```csharp
    /// <summary>
    /// Streamer override via the Skip button during an open vote. Consumes an
    /// OVERRIDE, never a card skip (spec Decision 5). Two sub-cases:
    /// includeSkip=true — Skip is vote option #0, force it through the normal
    /// TryCloseNow path (ResumeSkipOnMainThread then applies chat-skip
    /// semantics, budget-free via _chatSkipResumeInProgress). includeSkip=false
    /// — Skip is not a vote option, so flag _overrideSkipPending and Cancel();
    /// HandleVoteAsync routes the cancellation to ResumeSkipOnMainThread.
    /// </summary>
    private static bool TryOverrideWithSkip(NCardRewardSelectionScreen screen, int clickedIndex) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;

            var skipIndex = FindSkipAlternativeIndex(screen);
            if (skipIndex is null || clickedIndex != skipIndex.Value) return false;

            if (_activeIncludeSkip) {
                if (!session.TryCloseNow(0)) return false;   // Skip is vote option #0
            } else {
                Interlocked.Exchange(ref _overrideSkipPending, 1);
                session.Cancel();
                if (session.State != VoteSessionState.Cancelled) {
                    // Lost a race to natural close — revert; do not consume.
                    Interlocked.Exchange(ref _overrideSkipPending, 0);
                    return false;
                }
            }

            VoteOverrideBudget.RecordUse();
            VoteOverrideBudget.SendOverrideReceipt("Skip");
            TiLog.Info($"[SlayTheStreamer2][card-vote] override: streamer forced Skip; {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] skip-override attempt failed; click suppressed", ex);
            Interlocked.Exchange(ref _overrideSkipPending, 0);
            return false;
        }
    }
```

- [ ] **Step 5: Route override-skip cancellation in `HandleVoteAsync`**

Replace the `AwaitWinnerAsync` try/catch:

```csharp
            int winnerIndex;
            try {
                winnerIndex = await session.AwaitWinnerAsync();
            } catch (OperationCanceledException) {
                if (Interlocked.Exchange(ref _overrideSkipPending, 0) == 1) {
                    // Streamer override-skip on a vote with no Skip option: the
                    // session was cancelled as the transport; apply chat-skip
                    // semantics (reward consumed, no card-skip budget charge).
                    TiLog.Info("[SlayTheStreamer2][card-vote] override-skip: routing cancelled session to skip-resume");
                    coordinator.Dispatcher.Post(() => ResumeSkipOnMainThread(screen, runIdAtStart, optionsSig));
                    return;
                }
                // Non-override cancellation (run death etc.): preserve prior
                // behavior — fallback resume; liveness checks drop it if the
                // world moved on.
                TiLog.Info("[SlayTheStreamer2][card-vote] vote cancelled; falling back to player click");
                winnerIndex = includeSkip ? playerClickIndex + 1 : playerClickIndex;
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
                winnerIndex = includeSkip ? playerClickIndex + 1 : playerClickIndex;
            }
```

- [ ] **Step 6: Clear context wherever `_voteInProgress` resets after a started vote**

Add `_activeSession = null;` immediately before each `Interlocked.Exchange(ref _voteInProgress, 0);` in: `ResumeOnMainThread`'s `finally`, `ResumeSkipOnMainThread`'s `finally` AND its mid-body `Interlocked.Exchange(ref _voteInProgress, 0)` (before the reflective invoke), and `HandleVoteAsync`'s outer-catch flag-reset path. (Do NOT touch the pre-vote bail-out resets in `Prefix` — no context was set there yet.)

- [ ] **Step 7: Budget observe in the rewards-screen `_Ready` postfix**

In `CardRewardSkipGatePatch.NRewardsScreen_Ready_Postfix.Postfix`, after the existing skip-tracker observe/reset-receipt block, add:

```csharp
                // Vote-override budget shares the reset cadence (spec §2.5).
                var overrideReason = VoteOverrideBudget.Observe(runState.Rng?.StringSeed, actIndex);
                VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIndex.HasValue ? actIndex.Value + 1 : 0);
```

- [ ] **Step 8: Fix the stale doc comment (spec §7 rider)**

In `CardRewardVotePatch.CardRewardAlternative_Generate_Postfix`'s doc comment, replace the sentence referencing `NCardRewardSelectionScreen_Ready_HideSkipButton_Postfix` (that class no longer exists) with:

```
    /// Streamer interaction: the Skip button remains visible (the vote popup
    /// anchors its #0 indicator to it when CardSkipAsVoteOption is on), and
```

(keep the rest of the sentence about the hotkey reassignment and Escape fall-through unchanged).

- [ ] **Step 9: Build + full test suite**

Run: `pwsh -File build.ps1`
Expected: build succeeds, all tests pass (no new tests here; nothing regresses).

- [ ] **Step 10: Commit**

```bash
git add src/Game/DecisionVotes/CardRewardVotePatch.cs src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m "vote-override/5: card-vote override (card + Skip clicks mid-vote), override-skip cancel routing"
```

---

### Task 6: Counter label — rename + colour/bold budget swap

**Files:**
- Rename: `src/Game/Ui/CardSkipCounterLabel.cs` → `src/Game/Ui/StreamerBudgetCounterLabel.cs`
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` (type references)

**Interfaces:**
- Consumes: `VoteOverrideBudget.Snapshot()` (Task 4), `ActBudgetSnapshot` (Task 2), `CardRewardVotePatch.VoteInProgress` (existing).
- Produces: `StreamerBudgetCounterLabel` with unchanged public surface — `static StreamerBudgetCounterLabel AttachTo(Node parent, Control? skipButton)` and `internal void UpdateText(ActBudgetSnapshot snap)`.

- [ ] **Step 1: Rename file + class**

```bash
git mv src/Game/Ui/CardSkipCounterLabel.cs src/Game/Ui/StreamerBudgetCounterLabel.cs
```

Rename class `CardSkipCounterLabel` → `StreamerBudgetCounterLabel` (declaration, `AttachTo` return type + construction, `Name = "StreamerBudgetCounterLabel"`). In `CardRewardSkipGatePatch`: field `private static StreamerBudgetCounterLabel? _activeLabel;` and the `StreamerBudgetCounterLabel.AttachTo(...)` call in `AttachOrUpdateLabel`. Update the class doc comment: it now shows the card-skip budget outside votes and the vote-override budget during votes.

- [ ] **Step 2: Add accent constants + bold font**

```csharp
    // Vanilla default body font + its bold sibling (both ship in vanilla themes/).
    private const string FontPath = "res://themes/kreon_regular_shared.tres";
    private const string BoldFontPath = "res://themes/kreon_bold_shared.tres";

    // Vanilla compendium rarity colours (card_library.tscn Uncommon/Rare label
    // modulates == StsColors.blue / StsColors.gold). Hex literals rather than
    // StsColors bindings: identical values, zero new cross-branch game bindings.
    private const string SkipAccentHex = "#87CEEB";       // Uncommon cyan-blue
    private const string OverrideAccentHex = "#EFC851";   // Rare yellow-gold
```

In `ApplyTheme`, load the bold font for the `[b]` tags (fall back to regular if the load fails):

```csharp
    private static void ApplyTheme(RichTextLabel label) {
        var font = ResourceLoader.Load<Font>(FontPath);
        var bold = ResourceLoader.Load<Font>(BoldFontPath) ?? font;
        if (font is not null) {
            label.AddThemeFontOverride("normal_font", font);
            label.AddThemeFontOverride("italics_font", font);
        }
        if (bold is not null) {
            label.AddThemeFontOverride("bold_font", bold);
            label.AddThemeFontOverride("bold_italics_font", bold);
        }
        label.AddThemeFontSizeOverride("normal_font_size", FontSize);
        label.AddThemeFontSizeOverride("bold_font_size", FontSize);
        label.AddThemeFontSizeOverride("italics_font_size", FontSize);
        label.AddThemeFontSizeOverride("bold_italics_font_size", FontSize);
    }
```

- [ ] **Step 3: Two-budget display state + text builders**

Add fields:

```csharp
    private ActBudgetSnapshot _skipSnapshot;
    private bool _showingOverride;
    private int _lastOverrideRemaining = int.MinValue;
```

Replace `UpdateText` (same signature — callers unchanged) and add the override builder:

```csharp
    internal void UpdateText(ActBudgetSnapshot snap) {
        _skipSnapshot = snap;
        if (snap.LimitThisAct <= 0) {
            Visible = false;
            return;
        }
        Visible = true;
        string noun = snap.RemainingThisAct == 1 ? "card skip" : "card skips";
        string streamerName = ModSettings.GetStreamerDisplayName();
        // [center] BBCode horizontally centers the text inside the layout box;
        // _Process positions the box itself relative to the Skip button.
        Text = $"[center]{streamerName} has [b][color={SkipAccentHex}]{snap.RemainingThisAct} {noun}[/color][/b] remaining this act[/center]";
    }

    private void SetOverrideText(ActBudgetSnapshot snap) {
        string noun = snap.RemainingThisAct == 1 ? "vote override" : "vote overrides";
        string streamerName = ModSettings.GetStreamerDisplayName();
        Text = $"[center]{streamerName} has [b][color={OverrideAccentHex}]{snap.RemainingThisAct} {noun}[/color][/b] remaining this act[/center]";
    }
```

- [ ] **Step 4: Swap logic in `_Process`**

Replace the vote-in-progress hide block at the top of `_Process` (the position-polling tail below it is unchanged):

```csharp
    public override void _Process(double delta) {
        if (CardRewardVotePatch.VoteInProgress) {
            // During a vote this label shows the OVERRIDE budget in the same
            // screen position the skip text occupies. Hidden when the feature
            // is off (limit 0), unlimited (-1, mirroring the skip label's
            // unlimited convention), or exhausted (0 remaining) — the last
            // being exactly the old hide-during-vote behavior.
            var snap = VoteOverrideBudget.Snapshot();
            bool show = snap.LimitThisAct > 0 && snap.RemainingThisAct > 0;
            if (Visible != show) Visible = show;
            if (!show) { _showingOverride = false; return; }
            if (!_showingOverride || _lastOverrideRemaining != snap.RemainingThisAct) {
                SetOverrideText(snap);
                _showingOverride = true;
                _lastOverrideRemaining = snap.RemainingThisAct;
            }
        } else {
            if (_showingOverride) {
                _showingOverride = false;
                _lastOverrideRemaining = int.MinValue;
                UpdateText(_skipSnapshot);   // restore skip text + its visibility rules
            }
            if (!Visible) Visible = true;
        }

        // Poll the Skip button each frame ... (existing positioning code unchanged)
```

Add `using SlayTheStreamer2.Game.DecisionVotes;` if not already present (it is — `CardRewardVotePatch` is already referenced).

- [ ] **Step 5: Build + full test suite**

Run: `pwsh -File build.ps1`
Expected: build succeeds, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "vote-override/6: StreamerBudgetCounterLabel — override counter during votes, rarity-colour accents, bold Kreon"
```

---

### Task 7: Ancient-vote integration (`AncientVotePatch`)

**Files:**
- Modify: `src/Game/DecisionVotes/AncientVotePatch.cs`

**Interfaces:**
- Consumes: `VoteSession.TryCloseNow` / `.Elapsed` / `.State` / `.Options` (Task 1); `VoteOverrideBudget.*` (Task 4).
- Produces: override-capable ancient votes.

- [ ] **Step 1: Context static + locked/proceed guard**

Add next to `_voteInProgress`:

```csharp
    private static VoteSession? _activeSession;
```

In `Prefix`, replace the locked/proceed early-out with a vote-aware version (spec §2.4 discovery — with options left enabled during votes, these clicks must not fall through to vanilla mid-vote):

```csharp
        if (option.IsLocked || option.IsProceed) {
            // During an open vote such clicks must be suppressed, not passed to
            // vanilla — options stay enabled when an override is available.
            return _voteInProgress != 1;
        }
```

- [ ] **Step 2: Override attempt in the suppressed-click branch**

Replace the repeat-click branch:

```csharp
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            if (TryOverride(option, index)) return false;
            TiLog.Debug("[SlayTheStreamer2][ancient-vote] repeat click during open vote; suppressed");
            return false;
        }
```

Add the helper (near `GetVoteTitle`):

```csharp
    /// <summary>
    /// Streamer override: an armed click on an ancient option during an open
    /// vote ends the vote with that option as the winner and consumes one
    /// override (spec 2026-07-21 §2.4). Ancient options map 1:1 to vote
    /// indices — no skip concept, no holder mapping. Budget consumed strictly
    /// AFTER TryCloseNow succeeds.
    /// </summary>
    private static bool TryOverride(EventOption option, int index) {
        try {
            var session = _activeSession;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;
            if (index < 0 || index >= session.Options.Count) return false;

            if (!session.TryCloseNow(index)) return false;

            VoteOverrideBudget.RecordUse();
            string label;
            try { label = option.Title.GetFormattedText(); } catch { label = $"#{index}"; }
            VoteOverrideBudget.SendOverrideReceipt(label);
            TiLog.Info($"[SlayTheStreamer2][ancient-vote] override: streamer forced #{index} ({label}); {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][ancient-vote] override attempt failed; click suppressed", ex);
            return false;
        }
    }
```

- [ ] **Step 3: Budget observe + conditional `DisableEventOptions` + context set**

In `Prefix`, after the successful `session = coordinator.Start(...)` block, add the context set:

```csharp
        _activeSession = session;
```

Then replace the unconditional `DisableEventOptions` block with:

```csharp
        // Vote-override budget: observe (reset receipt on act/run change), then
        // decide whether the option buttons stay clickable. With an override
        // available, the streamer must be able to click an option mid-vote —
        // the prefix suppresses everything vanilla-bound anyway. The decision
        // is stable for this vote: only one vote runs at a time, so the budget
        // cannot be consumed elsewhere mid-vote.
        bool overrideAvailable = false;
        try {
            var rsForObserve = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
            int? actIdx = rsForObserve?.CurrentActIndex;
            var overrideReason = VoteOverrideBudget.Observe(rsForObserve?.Rng?.StringSeed, actIdx);
            VoteOverrideBudget.SendResetReceiptIfAny(overrideReason, actIdx.HasValue ? actIdx.Value + 1 : 0);
            overrideAvailable = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
        } catch { /* observe is best-effort; fall through to vanilla disable */ }

        if (overrideAvailable) {
            TiLog.Info("[SlayTheStreamer2][ancient-vote] override available; leaving event options clickable");
        } else {
            try {
                __instance.Layout?.DisableEventOptions();
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][ancient-vote] DisableEventOptions threw (continuing): {ex.Message}");
            }
        }
```

- [ ] **Step 4: Clear context on resume**

In `ResumeOnMainThread`'s `finally` and in `HandleVoteAsync`'s outer-catch flag-reset path, add `_activeSession = null;` immediately before `Interlocked.Exchange(ref _voteInProgress, 0);`.

- [ ] **Step 5: Build + full test suite**

Run: `pwsh -File build.ps1`
Expected: build succeeds, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/AncientVotePatch.cs
git commit -m "vote-override/7: ancient-vote override — options stay clickable when an override is available"
```

---

### Task 8: Deploy + operator validation gate

**Files:** none (validation task).

- [ ] **Step 1: Final build + deploy**

```powershell
pwsh -File build.ps1     # rebuild dist/ (publish + tests + assemble)
pwsh -File install.ps1   # copy dist/ -> Steam mods folder (REQUIRED after final build)
```

Expected: both succeed. Confirm in `%APPDATA%\SlayTheSpire2\logs\godot.log` after launch that the mod version suffix matches `git log -1 --format=%H`.

- [ ] **Step 2: Operator validation (Surfinite, live game + live Twitch chat)**

Card-reward votes:
- [ ] Override a card mid-vote (with `cardSkipAsVoteOption` ON and OFF): vote ends instantly, clicked card is claimed, receipt reads "… overrode the vote and took X. N override(s) remaining this act".
- [ ] Override via Skip mid-vote, `cardSkipAsVoteOption` ON (forces `#0`) and OFF (`Cancel` routing): reward consumed outright, card-skip budget untouched, override budget −1.
- [ ] Double-click that opens a vote does NOT consume an override (1.5s arming window); a click at ~1s is silently suppressed.
- [ ] Counter label: skip text with blue-bold fragment outside votes; swaps to gold-bold override text during votes; hidden during votes at 0 remaining; hidden for `voteOverridesPerAct` 0 and −1 (overrides still work on −1). Check the label doesn't collide with the popup's `#0` indicator near the Skip button — if it does, adjust `GapBelowSkipButton`.
- [ ] With 0 overrides remaining: reroll, Proceed, Skip, and card clicks all behave exactly as before this feature.
- [ ] Same-pick override (click chat's current leader) still consumes an override.

Ancient votes:
- [ ] With an override available: options stay clickable during the vote; override click ends the vote with that option; receipt fires.
- [ ] With no override available (0 remaining or feature off): options grey out exactly as before.
- [ ] Locked (unaffordable) option click mid-vote: suppressed, nothing happens.

Cross-cutting:
- [ ] Act transition: both reset receipts fire ("Card skips reset…", "Vote overrides reset…"); override budget back to full.
- [ ] Run-death / abandon mid-vote: unchanged behavior (session cancels, no override consumed, no stuck popups).
- [ ] `voteOverridesPerAct` dropdown in the mod settings panel persists across restart.

- [ ] **Step 3: On green gate**

```bash
git tag vote-override-complete
```

Record validation results + any follow-ups in `notes/06-followups-and-deferred.md`. (Release bundling into v0.2.0 with Bossy Relics is a separate workflow — not part of this slice.)
