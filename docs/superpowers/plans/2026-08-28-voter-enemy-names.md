# Voter Enemy Names Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Name enemy creatures after chat voters (fair rotation, StS1-style Jr./Roman decorations) with an optional speech-bubble repeater that makes a named enemy "speak" its voter's chat messages.

**Architecture:** A Ti-layer addition retains voter display names per `VoteSession`; a new `SessionStarted` event on `VoteCoordinator` gives the game side one harvest point into a pure, unit-tested `VoterNamePool`. Game-side Harmony patches on `NCreature.UpdateBounds` place a bobbing name label below each enemy's intent icons (icons shift up ~40px), and a chat-message subscriber replays a named voter's messages as vanilla `NSpeechBubbleVfx` bubbles. Pure cosmetics — zero game-state mutation on any path.

**Tech Stack:** C# / .NET 9, Godot 4 (Godot.NET.Sdk mod project + Microsoft.NET.Sdk test project), HarmonyLib, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-28-voter-enemy-names-design.md`

## Global Constraints

- Settings keys are ADDITIVE: record + parse block + `SettingsBootstrap.BuildTemplate` + `SettingsWriter` + `.json.example` + panel row, NO `schemaVersion` bump.
- `nameEnemiesAfterVoters` default **true**; `namedEnemiesSpeak` default **true** (effective only while the first is on).
- `src/Ti/**` must stay BCL + Godot + System.Net.Http only — no `MegaCrit.*`, no `src/Game/*` types.
- Test project is `Microsoft.NET.Sdk`: no `Godot.*` / `MegaCrit.*` types in anything the test csproj compiles. Game-side files get `<Compile Remove>` entries; pure logic files ride the existing globs.
- Any test class that can trigger `TiLog` MUST carry `[Collection("TiLog.Sink")]`.
- Commit prefix: `voter-names/N:`. Commits to main are pre-authorized.
- Build check for game-side tasks: `dotnet build src/slay_the_streamer_2.csproj -v q` (0 errors; the 4 BossVotePatch nullability warnings are pre-existing).
- Full suite: `dotnet test tests/slay_the_streamer_2.tests.csproj` (all green, 484 baseline + new).
- Line numbers for `decompiled/sts2/...` refer to the COMMITTED (stale, May-23) tree — good for orientation only. Every game-type member this plan names was re-verified against the v0.111.0 DLL on 2026-08-28 via fresh ilspycmd (see spec §5/§6).

---

### Task 1: `VoteSession` retains voter display names (Ti layer)

**Files:**
- Modify: `src/Ti/Voting/VoteSession.cs` (fields ~line 26, `OnChatMessage` ~line 183)
- Test: create `tests/Voting/VoteSessionVoterNamesTests.cs`

**Interfaces:**
- Produces: `public IReadOnlyDictionary<string, string> VoterDisplayNames` on `VoteSession` — VoterKey → DisplayName, copy-on-read snapshot, valid in every state (empty before first vote). Task 6 consumes it.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Linq;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Voting;

[Collection("TiLog.Sink")]
public class VoteSessionVoterNamesTests : VoteSessionTestBase {

    [Fact]
    public void VoterDisplayNames_empty_before_any_vote() {
        var session = StartVote();
        Assert.Empty(session.VoterDisplayNames);
    }

    [Fact]
    public void VoterDisplayNames_captures_display_name_per_voter_key() {
        var session = StartVote();
        Chat.Inject(new ChatMessage("123", "somelogin", "SomeViewer", "#1",
            Clock.UtcNow, false, false, false));
        InjectYouTubeVote(session, "UCabc", 0);

        Assert.Equal(2, session.VoterDisplayNames.Count);
        Assert.Equal("SomeViewer", session.VoterDisplayNames["123"]);
        Assert.Equal("UCabc", session.VoterDisplayNames["yt:UCabc"]);
    }

    [Fact]
    public void VoterDisplayNames_vote_change_updates_name_not_count() {
        var session = StartVote();
        Chat.Inject(new ChatMessage("123", "somelogin", "OldName", "#1",
            Clock.UtcNow, false, false, false));
        Chat.Inject(new ChatMessage("123", "somelogin", "NewName", "#2",
            Clock.UtcNow, false, false, false));

        Assert.Single(session.VoterDisplayNames);
        Assert.Equal("NewName", session.VoterDisplayNames["123"]);
    }

    [Fact]
    public void VoterDisplayNames_non_vote_messages_not_captured() {
        var session = StartVote();
        Inject("chatty", "hello everyone");
        Assert.Empty(session.VoterDisplayNames);
    }

    [Fact]
    public void VoterDisplayNames_survives_close() {
        var session = StartVote();
        InjectTwitchVote(session, "42", 1);
        session.CloseNow();
        Assert.Single(session.VoterDisplayNames);
        Assert.Equal("login_42", session.VoterDisplayNames["42"]);
    }

    [Fact]
    public void VoterDisplayNames_snapshot_is_a_copy() {
        var session = StartVote();
        InjectTwitchVote(session, "42", 1);
        var snapshot = session.VoterDisplayNames;
        InjectTwitchVote(session, "43", 1);
        Assert.Single(snapshot);                          // old snapshot unchanged
        Assert.Equal(2, session.VoterDisplayNames.Count); // fresh read sees both
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoterDisplayNames"`
Expected: compile FAILURE — `'VoteSession' does not contain a definition for 'VoterDisplayNames'`.

- [ ] **Step 3: Implement**

In `VoteSession.cs`, next to `_votersByKey` (~line 26) add:

```csharp
    private readonly Dictionary<string, string> _displayNamesByKey = new();
```

Next to the `Tallies` copy-on-read property (~line 56) add:

```csharp
    /// <summary>
    /// DisplayName per VoterKey for everyone whose vote was accepted, retained
    /// for the voter-names feature. Bounded by the MaxVoters cap (the same
    /// early-return that bounds _votersByKey). Copy-on-read snapshot; valid in
    /// every state — harvest on Closed/Cancelled.
    /// </summary>
    public IReadOnlyDictionary<string, string> VoterDisplayNames =>
        new Dictionary<string, string>(_displayNamesByKey);
```

In `OnChatMessage`, directly after `_votersByKey[key] = idx;` (~line 183) add:

```csharp
        _displayNamesByKey[key] = msg.DisplayName;
```

(This is after the MaxVoters early-return, so the cap bounds this dict too. A
repeat vote refreshes the display name — deliberate: people rename.)

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all pass (baseline + 6 new).

- [ ] **Step 5: Commit**

```bash
git add src/Ti/Voting/VoteSession.cs tests/Voting/VoteSessionVoterNamesTests.cs
git commit -m "voter-names/1: VoteSession retains VoterKey -> DisplayName (bounded by MaxVoters)"
```

---

### Task 2: `VoteCoordinator.SessionStarted` event (Ti layer)

**Files:**
- Modify: `src/Ti/Voting/VoteCoordinator.cs` (event near the other members ~line 25; raise inside `Start` just before `return session;` ~line 83)
- Test: modify `tests/Voting/VoteCoordinatorTests.cs` (append tests to the existing class)

**Interfaces:**
- Produces: `public event EventHandler<VoteSession>? SessionStarted;` on `VoteCoordinator`, raised synchronously inside `Start(...)` AFTER `CurrentSession` is set and the session's own event handlers are attached, before `Start` returns. Task 6 consumes it.

- [ ] **Step 1: Write the failing tests** (append to the existing test class in `tests/Voting/VoteCoordinatorTests.cs`; match its existing base class/usings)

```csharp
    [Fact]
    public void SessionStarted_fires_with_the_new_session() {
        var coordinator = CreateCoordinator();
        VoteSession? seen = null;
        coordinator.SessionStarted += (_, s) => seen = s;

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));

        Assert.Same(session, seen);
    }

    [Fact]
    public void SessionStarted_subscriber_exception_does_not_break_start() {
        var coordinator = CreateCoordinator();
        coordinator.SessionStarted += (_, _) => throw new InvalidOperationException("boom");

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));

        Assert.NotNull(session);
        Assert.Equal(VoteSessionState.Open, session.State);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~SessionStarted"`
Expected: compile FAILURE — no `SessionStarted` member.

- [ ] **Step 3: Implement**

In `VoteCoordinator.cs`, near `public IMainThreadDispatcher Dispatcher` (~line 25):

```csharp
    /// <summary>
    /// Raised synchronously inside Start(...) with the newly created session,
    /// after CurrentSession is set. Single wiring point for cross-cutting
    /// observers (the voter-name pool harvests each session's voters on its
    /// terminal events). Subscriber exceptions are swallowed with a Warn —
    /// an observer must never break vote creation.
    /// </summary>
    public event EventHandler<VoteSession>? SessionStarted;
```

In `Start(...)`, after `SetFastPolling(true);` and before `return session;`:

```csharp
        try {
            SessionStarted?.Invoke(this, session);
        } catch (Exception ex) {
            TiLog.Warn($"[VoteCoordinator] SessionStarted subscriber threw: {ex.Message}");
        }
```

(`TiLog` is already imported in this file's namespace; add `using SlayTheStreamer2.Ti.Internal;` if not.)

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Ti/Voting/VoteCoordinator.cs tests/Voting/VoteCoordinatorTests.cs
git commit -m "voter-names/2: VoteCoordinator.SessionStarted event (single harvest point)"
```

---

### Task 3: `VoterNamePool` — fair rotation with Jr./Roman decorations (pure)

**Files:**
- Create: `src/Game/DecisionVotes/VoterNamePool.cs` (pure BCL — rides the test csproj's `..\src\Game\DecisionVotes\**\*.cs` glob; NO `<Compile Remove>`)
- Test: create `tests/Game/DecisionVotes/VoterNamePoolTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class VoterNamePool { public VoterNamePool(Random random); }`
  - `public void AddVoters(IReadOnlyDictionary<string, string> voterDisplayNames)`
  - `public bool TryTakeName(out string decoratedName, out string voterKey)`
  - `public int DistinctVoterCount { get; }`
  - `internal static class RomanNumerals { public static string Convert(int value); }` (same file)
- Task 6 owns the singleton instance; Task 7 calls `TryTakeName`; Task 8 uses `voterKey` for bubble matching.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class VoterNamePoolTests {
    private static VoterNamePool Pool(int seed = 42) => new(new Random(seed));

    private static Dictionary<string, string> Voters(params (string Key, string Name)[] voters)
        => voters.ToDictionary(v => v.Key, v => v.Name);

    [Fact]
    public void Empty_pool_returns_false() {
        var pool = Pool();
        Assert.False(pool.TryTakeName(out _, out _));
    }

    [Fact]
    public void Every_distinct_voter_used_once_before_any_repeat() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice"), ("b", "Bob"), ("c", "Carol")));

        var firstRound = new List<string>();
        for (int i = 0; i < 3; i++) {
            Assert.True(pool.TryTakeName(out var name, out var key));
            firstRound.Add(key);
            Assert.DoesNotContain(" Jr.", name);
        }
        Assert.Equal(3, firstRound.Distinct().Count());   // all three keys, no repeats
    }

    [Fact]
    public void Second_use_is_Jr_third_is_roman_III() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice")));

        Assert.True(pool.TryTakeName(out var first, out _));
        Assert.Equal("Alice", first);
        Assert.True(pool.TryTakeName(out var second, out _));
        Assert.Equal("Alice Jr.", second);
        Assert.True(pool.TryTakeName(out var third, out _));
        Assert.Equal("Alice III", third);
        Assert.True(pool.TryTakeName(out var fourth, out _));
        Assert.Equal("Alice IV", fourth);
    }

    [Fact]
    public void New_voter_joining_mid_session_gets_priority_over_used_voters() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "Alice")));
        Assert.True(pool.TryTakeName(out _, out _));          // Alice used once

        pool.AddVoters(Voters(("b", "Bob")));
        Assert.True(pool.TryTakeName(out var name, out var key));
        Assert.Equal("b", key);                                // Bob (count 0) before Alice Jr.
        Assert.Equal("Bob", name);
    }

    [Fact]
    public void Re_adding_a_voter_updates_display_name_but_keeps_used_count() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "OldName")));
        Assert.True(pool.TryTakeName(out _, out _));           // count -> 1

        pool.AddVoters(Voters(("a", "NewName")));
        Assert.True(pool.TryTakeName(out var name, out _));
        Assert.Equal("NewName Jr.", name);                     // count preserved -> Jr.
    }

    [Fact]
    public void Names_are_trimmed_and_capped_at_25_chars_with_ellipsis() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "  " + new string('x', 40) + "  ")));
        Assert.True(pool.TryTakeName(out var name, out _));
        Assert.Equal(new string('x', 24) + "…", name);    // 24 + ellipsis = 25
    }

    [Fact]
    public void Empty_or_whitespace_display_names_are_dropped() {
        var pool = Pool();
        pool.AddVoters(Voters(("a", "   ")));
        Assert.False(pool.TryTakeName(out _, out _));
        Assert.Equal(0, pool.DistinctVoterCount);
    }

    [Fact]
    public void Selection_among_least_used_is_seed_deterministic() {
        var a = Pool(seed: 7);
        var b = Pool(seed: 7);
        var voters = Voters(("a", "Alice"), ("b", "Bob"), ("c", "Carol"), ("d", "Dave"));
        a.AddVoters(voters);
        b.AddVoters(voters);
        for (int i = 0; i < 8; i++) {
            Assert.True(a.TryTakeName(out var nameA, out _));
            Assert.True(b.TryTakeName(out var nameB, out _));
            Assert.Equal(nameA, nameB);
        }
    }

    [Theory]
    [InlineData(3, "III")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(40, "XL")]
    public void Roman_numerals(int value, string expected) {
        Assert.Equal(expected, RomanNumerals.Convert(value));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoterNamePool"`
Expected: compile FAILURE — `VoterNamePool` not found.

- [ ] **Step 3: Implement `src/Game/DecisionVotes/VoterNamePool.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: session-lifetime fairness pool. Uniform-random among the
/// LEAST-used voters (strict rule per spec §4: nobody gets a second enemy
/// until every distinct voter has had one), decorated by use-count:
/// 1 → bare name, 2 → "Name Jr.", n≥3 → "Name III/IV/…" (StS1 homage).
/// Pure BCL — rides the test glob (CurseRoll precedent). NOT thread-safe by
/// itself; all callers are main-thread or marshal first (see VoterNamePoolHook).
/// </summary>
internal sealed class VoterNamePool {
    private const int MaxNameLength = 25;

    private sealed class Entry {
        public required string DisplayName;
        public int UsedCount;
    }

    private readonly Dictionary<string, Entry> _voters = new();
    private readonly Random _random;

    public VoterNamePool(Random random) {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    public int DistinctVoterCount => _voters.Count;

    public void AddVoters(IReadOnlyDictionary<string, string> voterDisplayNames) {
        foreach (var (key, rawName) in voterDisplayNames) {
            var name = CleanName(rawName);
            if (name is null) continue;
            if (_voters.TryGetValue(key, out var existing)) {
                existing.DisplayName = name;   // people rename; used-count preserved
            } else {
                _voters[key] = new Entry { DisplayName = name };
            }
        }
    }

    public bool TryTakeName(out string decoratedName, out string voterKey) {
        decoratedName = string.Empty;
        voterKey = string.Empty;
        if (_voters.Count == 0) return false;

        int minUsed = _voters.Values.Min(e => e.UsedCount);
        var candidates = _voters.Where(kv => kv.Value.UsedCount == minUsed).ToList();
        var picked = candidates[_random.Next(candidates.Count)];

        picked.Value.UsedCount++;
        voterKey = picked.Key;
        decoratedName = Decorate(picked.Value.DisplayName, picked.Value.UsedCount);
        return true;
    }

    private static string Decorate(string name, int useCount) => useCount switch {
        1 => name,
        2 => name + " Jr.",
        _ => name + " " + RomanNumerals.Convert(useCount),
    };

    private static string? CleanName(string raw) {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxNameLength) {
            trimmed = trimmed.Substring(0, MaxNameLength - 1) + "…";
        }
        return trimmed;
    }
}

/// <summary>Classic integer→Roman conversion (StS1 mod homage; capped by caller reality, guard at 3999).</summary>
internal static class RomanNumerals {
    private static readonly (int Value, string Symbol)[] Table = {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    public static string Convert(int value) {
        if (value < 1 || value > 3999) return value.ToString();
        var sb = new StringBuilder();
        foreach (var (v, s) in Table) {
            while (value >= v) { sb.Append(s); value -= v; }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/VoterNamePool.cs tests/Game/DecisionVotes/VoterNamePoolTests.cs
git commit -m "voter-names/3: VoterNamePool - min-count fair rotation, Jr./Roman decorations"
```

---

### Task 4: `BubbleText` sanitizer (pure)

**Files:**
- Create: `src/Game/DecisionVotes/BubbleText.cs` (pure BCL — rides the test glob)
- Test: create `tests/Game/DecisionVotes/BubbleTextTests.cs`

**Interfaces:**
- Produces: `internal static class BubbleText { public static string? Sanitize(string raw); public static int RawCharCount(string text); }` — `Sanitize` returns null when nothing displayable remains. Task 8 consumes both.

- [ ] **Step 1: Write the failing tests**

```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class BubbleTextTests {

    [Fact]
    public void Plain_short_message_passes_through() {
        Assert.Equal("hello there", BubbleText.Sanitize("hello there"));
    }

    [Fact]
    public void Brackets_are_neutralized() {
        // The speech bubble renders BBCode; '[' must never survive.
        Assert.Equal("(b)bold(/b)", BubbleText.Sanitize("[b]bold[/b]"));
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed() {
        Assert.Equal("a b c", BubbleText.Sanitize("  a\r\n b\t\t c  "));
    }

    [Fact]
    public void Empty_and_whitespace_only_return_null() {
        Assert.Null(BubbleText.Sanitize("   "));
        Assert.Null(BubbleText.Sanitize(""));
    }

    [Fact]
    public void Long_message_truncates_at_word_boundary_with_ellipsis() {
        // 64-char budget, cut backtracks to the last space (original mod's rule).
        string msg = string.Join(" ", System.Linq.Enumerable.Repeat("word", 30)); // 149 chars
        string? result = BubbleText.Sanitize(msg);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 67);          // 64 + "..."
        Assert.EndsWith("...", result);
        Assert.DoesNotContain("wor...", result);    // never cuts mid-word when a space exists
    }

    [Fact]
    public void Long_single_token_truncates_hard() {
        string? result = BubbleText.Sanitize(new string('x', 100));
        Assert.Equal(new string('x', 64) + "...", result);
    }

    [Fact]
    public void RawCharCount_ignores_spaces() {
        Assert.Equal(10, BubbleText.RawCharCount("hello there"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BubbleText"`
Expected: compile FAILURE — `BubbleText` not found.

- [ ] **Step 3: Implement `src/Game/DecisionVotes/BubbleText.cs`**

```csharp
using System.Text.RegularExpressions;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: chat-message hygiene for speech bubbles. The bubble label
/// renders BBCode, so square brackets are neutralized to parentheses
/// (renderer-agnostic — no dependency on a particular escape syntax).
/// 64-char budget with word-boundary backtrack copies the StS1 mod's rule.
/// Pure BCL — rides the test glob.
/// </summary>
internal static class BubbleText {
    private const int MaxLength = 64;

    public static string? Sanitize(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Replace('[', '(').Replace(']', ')');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxLength) {
            text = text.Substring(0, MaxLength);
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace > 0) text = text.Substring(0, lastSpace);
            text += "...";
        }
        return text;
    }

    /// <summary>Display-duration input: char count sans spaces (vanilla TalkCmd's rule).</summary>
    public static int RawCharCount(string text) => text.Replace(" ", "").Length;
}
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/BubbleText.cs tests/Game/DecisionVotes/BubbleTextTests.cs
git commit -m "voter-names/4: BubbleText sanitizer - bracket neutralization + 64-char word-boundary truncation"
```

---

### Task 5: Settings keys `nameEnemiesAfterVoters` + `namedEnemiesSpeak`

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs` (record ~line 10-25; parse block before `var creds` ~line 274)
- Modify: `src/Game/Bootstrap/SettingsBootstrap.cs` (`BuildTemplate` ~line 46)
- Modify: `src/Game/Ui/Settings/SettingsWriter.cs` (UI-managed keys block ~line 29)
- Modify: `src/Game/Ui/Settings/SettingsPanelBuilder.cs` (`Build`, after the Cursed Overrides help-text row)
- Modify: `src/slay_the_streamer_2.json.example`
- Test: modify `tests/Bootstrap/ModSettingsTests.cs`, `tests/Game/Ui/Settings/SettingsWriterTests.cs`

**Interfaces:**
- Produces: `ChatSettings.NameEnemiesAfterVoters` (bool, default true) and `ChatSettings.NamedEnemiesSpeak` (bool, default true). Tasks 7 and 8 read them via `ModSettings.Current`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Bootstrap/ModSettingsTests.cs` (before the `WriteTempJson` helpers, mirroring the `CombatCardVotesOnly` theory):

```csharp
    // --- nameEnemiesAfterVoters / namedEnemiesSpeak (voter-names, both default true) ---

    [Theory]
    [InlineData("\"nameEnemiesAfterVoters\": true,", true, false)]
    [InlineData("\"nameEnemiesAfterVoters\": false,", false, false)]
    [InlineData("\"nameEnemiesAfterVoters\": \"yes\",", true, true)]  // non-bool -> default + warning
    [InlineData("", true, false)]                                     // missing -> default, no warning
    public void NameEnemiesAfterVoters_parses_and_defaults(string fragment, bool expected, bool expectWarning) {
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
            Assert.Equal(expected, success.Settings.NameEnemiesAfterVoters);
            Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("nameEnemiesAfterVoters")));
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"namedEnemiesSpeak\": true,", true, false)]
    [InlineData("\"namedEnemiesSpeak\": false,", false, false)]
    [InlineData("\"namedEnemiesSpeak\": \"yes\",", true, true)]
    [InlineData("", true, false)]
    public void NamedEnemiesSpeak_parses_and_defaults(string fragment, bool expected, bool expectWarning) {
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
            Assert.Equal(expected, success.Settings.NamedEnemiesSpeak);
            Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("namedEnemiesSpeak")));
        } finally { File.Delete(path); }
    }
```

Append to `tests/Game/Ui/Settings/SettingsWriterTests.cs` (mirror `Write_persists_combatCardVotesOnly`, including its `TempPath`/cleanup shape):

```csharp
    [Fact]
    public void Write_persists_voter_name_settings() {
        var path = TempPath();
        try {
            var settings = MakeSettings() with { NameEnemiesAfterVoters = false, NamedEnemiesSpeak = false };
            SettingsWriter.Write(path, settings);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.False((bool)json["nameEnemiesAfterVoters"]!);
            Assert.False((bool)json["namedEnemiesSpeak"]!);
        } finally {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~NameEnemiesAfterVoters|FullyQualifiedName~NamedEnemiesSpeak|FullyQualifiedName~voter_name_settings"`
Expected: compile FAILURE — records lack the new members.

- [ ] **Step 3: Implement**

`ModSettings.cs` — record gains (after `CombatCardVotesOnly`):

```csharp
    bool NameEnemiesAfterVoters = true,
    bool NamedEnemiesSpeak = true);
```

Parse block before `var creds = ...`, mirroring `combatCardVotesOnly`:

```csharp
            bool nameEnemiesAfterVoters = true;
            if (root.TryGetProperty("nameEnemiesAfterVoters", out var nameEnemiesProp)) {
                if (nameEnemiesProp.ValueKind == JsonValueKind.True) nameEnemiesAfterVoters = true;
                else if (nameEnemiesProp.ValueKind == JsonValueKind.False) nameEnemiesAfterVoters = false;
                else warnings.Add("nameEnemiesAfterVoters is not a boolean; using default (true)");
            }

            bool namedEnemiesSpeak = true;
            if (root.TryGetProperty("namedEnemiesSpeak", out var enemiesSpeakProp)) {
                if (enemiesSpeakProp.ValueKind == JsonValueKind.True) namedEnemiesSpeak = true;
                else if (enemiesSpeakProp.ValueKind == JsonValueKind.False) namedEnemiesSpeak = false;
                else warnings.Add("namedEnemiesSpeak is not a boolean; using default (true)");
            }
```

Thread both through the `new ChatSettings(...)` call (append after `combatCardVotesOnly`).

`SettingsBootstrap.BuildTemplate` — append:

```csharp
        ["nameEnemiesAfterVoters"] = true,
        ["namedEnemiesSpeak"]      = true,
```

`SettingsWriter.cs` — append to the UI-managed keys block:

```csharp
        json["nameEnemiesAfterVoters"] = settings.NameEnemiesAfterVoters;
        json["namedEnemiesSpeak"] = settings.NamedEnemiesSpeak;
```

`src/slay_the_streamer_2.json.example` — append before the closing brace:

```json
  "nameEnemiesAfterVoters": true,
  "namedEnemiesSpeak": true
```

`SettingsPanelBuilder.Build` — after the Cursed Overrides `AddHelpText` row:

```csharp
        AddDivider(root);
        AddCheckboxRow(root, "Name enemies after chat voters", current.NameEnemiesAfterVoters,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { NameEnemiesAfterVoters = value }));
        AddHelpText(root, "Enemies are named after chatters who vote. Everyone gets a turn\nbefore anyone gets a second enemy (repeats become \"Jr.\", then \"III\").");
        AddDivider(root);
        AddCheckboxRow(root, "Named enemies repeat their voter's chat", current.NamedEnemiesSpeak,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { NamedEnemiesSpeak = value }));
        AddHelpText(root, "A named enemy speaks its chatter's messages in a speech bubble.\nBubble text is the raw chat message - your channel moderation is the filter.\nOnly applies while enemy naming is on.");
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: all pass.

- [ ] **Step 5: Build the game project** (panel builder is game-side)

Run: `dotnet build src/slay_the_streamer_2.csproj -v q`
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Game/Bootstrap/ModSettings.cs src/Game/Bootstrap/SettingsBootstrap.cs src/Game/Ui/Settings/SettingsWriter.cs src/Game/Ui/Settings/SettingsPanelBuilder.cs src/slay_the_streamer_2.json.example tests/Bootstrap/ModSettingsTests.cs tests/Game/Ui/Settings/SettingsWriterTests.cs
git commit -m "voter-names/5: nameEnemiesAfterVoters + namedEnemiesSpeak settings end-to-end (both default on)"
```

---

### Task 6: `VoterNamePoolHook` — harvest voters from every session

**Files:**
- Create: `src/Game/DecisionVotes/VoterNamePoolHook.cs` (Ti types + pool only — rides the test glob, NO Compile Remove)
- Modify: `src/ModEntry.cs` (one wiring line after `Voter.Default = Coordinator;` ~line 189)
- Test: create `tests/Game/DecisionVotes/VoterNamePoolHookTests.cs`

**Interfaces:**
- Consumes: `VoteCoordinator.SessionStarted` (Task 2), `VoteSession.VoterDisplayNames` (Task 1), `VoterNamePool` (Task 3).
- Produces: `internal static class VoterNamePoolHook { public static VoterNamePool Pool { get; } public static void Attach(VoteCoordinator coordinator); public static bool TakeNameLocked(out string decoratedName, out string voterKey); internal static void ResetForTests(); }`. Task 7 draws names via `TakeNameLocked` (shares the harvest lock).

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Tests.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class VoterNamePoolHookTests : VoteSessionTestBase {

    public VoterNamePoolHookTests() {
        VoterNamePoolHook.ResetForTests();
    }

    [Fact]
    public void Closed_session_voters_land_in_pool() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(session, "42", 1);
        session.CloseNow();

        Assert.Equal(1, VoterNamePoolHook.Pool.DistinctVoterCount);
        Assert.True(VoterNamePoolHook.Pool.TryTakeName(out var name, out var key));
        Assert.Equal("login_42", name);
        Assert.Equal("42", key);
    }

    [Fact]
    public void Cancelled_session_voters_also_land_in_pool() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var session = coordinator.Start("test", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(session, "7", 0);
        session.Cancel();

        Assert.Equal(1, VoterNamePoolHook.Pool.DistinctVoterCount);
    }

    [Fact]
    public void Voters_accumulate_across_sessions() {
        var coordinator = CreateCoordinator();
        VoterNamePoolHook.Attach(coordinator);

        var s1 = coordinator.Start("one", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(s1, "1", 0);
        s1.CloseNow();
        var s2 = coordinator.Start("two", new[] { "A", "B" }, TimeSpan.FromSeconds(30));
        InjectTwitchVote(s2, "2", 0);
        s2.CloseNow();

        Assert.Equal(2, VoterNamePoolHook.Pool.DistinctVoterCount);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoterNamePoolHook"`
Expected: compile FAILURE — `VoterNamePoolHook` not found.

- [ ] **Step 3: Implement `src/Game/DecisionVotes/VoterNamePoolHook.cs`**

```csharp
using System;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: bridges the vote layer to the fairness pool. Attach() once at
/// ModEntry wiring; every session (any vote type) contributes its voters on
/// its terminal event — cancelled sessions included (the person engaged).
/// Terminal events can fire off the main thread (Cancelled fires from the
/// chat-parser thread on disconnect), so harvest locks the pool; readers
/// (TryTakeName) are main-thread and take the same lock via TakeNameLocked.
/// Harvest is unconditional of the settings toggles — pool data is inert,
/// only the label patch reads it (mirrors the CombatOriginTags rule).
/// </summary>
internal static class VoterNamePoolHook {
    private static readonly object Gate = new();
    public static VoterNamePool Pool { get; private set; } = new(new Random());

    public static void Attach(VoteCoordinator coordinator) {
        coordinator.SessionStarted += (_, session) => {
            session.Closed += Harvest;
            session.Cancelled += Harvest;
        };
    }

    private static void Harvest(object? sender, VoteSession session) {
        try {
            lock (Gate) {
                Pool.AddVoters(session.VoterDisplayNames);
            }
            TiLog.Info($"[SlayTheStreamer2][voter-names] pool now {Pool.DistinctVoterCount} distinct voter(s)");
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] voter harvest failed: {ex.Message}");
        }
    }

    /// <summary>Main-thread name draw used by the label patch; shares the harvest lock.</summary>
    public static bool TakeNameLocked(out string decoratedName, out string voterKey) {
        lock (Gate) {
            return Pool.TryTakeName(out decoratedName, out voterKey);
        }
    }

    internal static void ResetForTests() {
        lock (Gate) {
            Pool = new VoterNamePool(new Random(42));
        }
    }
}
```

- [ ] **Step 4: Wire in `src/ModEntry.cs`** — directly after `Voter.Default = Coordinator;`:

```csharp
                VoterNamePoolHook.Attach(Coordinator);
```

(Add `using SlayTheStreamer2.Game.DecisionVotes;` if `ModEntry.cs` lacks it.)

- [ ] **Step 5: Run the full suite + game build**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj` then `dotnet build src/slay_the_streamer_2.csproj -v q`
Expected: all pass; 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/VoterNamePoolHook.cs src/ModEntry.cs tests/Game/DecisionVotes/VoterNamePoolHookTests.cs
git commit -m "voter-names/6: VoterNamePoolHook - harvest every session's voters into the pool"
```

---

### Task 7: `VoterNamesPatch` + `VoterNameLabel` — the on-screen names (game-side)

**Files:**
- Create: `src/Game/DecisionVotes/VoterNamesPatch.cs` (game-side)
- Create: `src/Game/Ui/VoterNameLabel.cs` (game-side)
- Modify: `tests/slay_the_streamer_2.tests.csproj` — add BOTH to the Compile Remove block:
  `<Compile Remove="..\src\Game\DecisionVotes\VoterNamesPatch.cs" />` (Ui/* is not glob-included, so `VoterNameLabel.cs` needs no entry — verify no `..\src\Game\Ui\**` glob was added since; only surgical includes exist there)
- Test: none (Godot/MegaCrit types); build check + operator matrix.

**Interfaces:**
- Consumes: `VoterNamePoolHook.TakeNameLocked` (Task 6), `ModSettings.Current.NameEnemiesAfterVoters` (Task 5).
- Produces: `internal static class VoterNamesPatch { internal static IEnumerable<(NCreature Node, string VoterKey)> NamedLivingCreatures(); }` — Task 8 consumes it.

**Game-side ground truth (verified v0.111.0, 2026-08-28):**
- `NCreature.UpdateBounds(Node boundsContainer)` (private; a `string boundsNodeName` overload exists — target the `Node` overload via `AccessTools.Method(typeof(NCreature), "UpdateBounds", new[] { typeof(Node) })`) sets `IntentContainer.Position` from the `IntentPos` Marker2D and scales Y by `Visuals.Scale.X`.
- `NCreature.IntentContainer` (public Control), `NCreature.Entity` (public Creature), `Creature.Monster` (null for players), `Creature.IsDead`.
- Bob (NIntent._Process): `Position = Vector2.Up * (Mathf.Sin(Time.GetTicksMsec() * 0.001f * MathF.PI + timeOffset) * 10f + 8f)`; first icon's `timeOffset = (float)creatureNode.GetHashCode() * 0.01f` (NCreature.UpdateIntent).
- Nameplate style (creature_state_display.tscn / NameplateLabel): font `res://themes/kreon_regular_glyph_space_one.tres`, size 24, color `(1, 0.964706, 0.886275)`, shadow `(0,0,0,0.25)` offset (2,1).
- Vanilla CULLS extra children of `IntentContainer` every `UpdateIntent` — the label must be a SIBLING on the NCreature, never a child of the container.

- [ ] **Step 1: Implement `src/Game/Ui/VoterNameLabel.cs`**

```csharp
using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Combat;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// voter-names: the on-screen username under an enemy's intent icons. Sibling
/// of NCreature.IntentContainer (vanilla culls the container's extra children
/// every turn — never parent inside it). Bobs with vanilla's exact intent
/// formula and phase so it moves in lockstep with the first icon, and mirrors
/// the container's Modulate/Visible each frame so it hides during attack
/// animations, fast-mode fades, and combat teardown for free.
/// </summary>
internal sealed partial class VoterNameLabel : Label {
    private const string KreonPath = "res://themes/kreon_regular_glyph_space_one.tres";
    private const int FontSize = 24;

    private Control? _container;      // the creature's IntentContainer
    private float _bobPhase;
    private Vector2 _basePosition;    // set by VoterNamesPatch on every UpdateBounds

    public static VoterNameLabel? TryCreate(string decoratedName, NCreature creature) {
        try {
            var label = new VoterNameLabel {
                Name = "VoterNameLabel",
                Text = decoratedName,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
                _container = creature.IntentContainer,
                _bobPhase = (float)creature.GetHashCode() * 0.01f,   // == first intent icon's phase
            };
            label.AddThemeColorOverride("font_color", new Color(1f, 0.964706f, 0.886275f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 0.25098f));
            label.AddThemeConstantOverride("shadow_offset_x", 2);
            label.AddThemeConstantOverride("shadow_offset_y", 1);
            label.AddThemeFontSizeOverride("font_size", FontSize);
            if (ResourceLoader.Exists(KreonPath) && ResourceLoader.Load(KreonPath) is Font kreon) {
                label.AddThemeFontOverride("font", kreon);
            }
            return label;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] label create failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called by the UpdateBounds postfix after vanilla lays the container out.</summary>
    public void SetBasePosition(Vector2 basePosition) => _basePosition = basePosition;

    public override void _Process(double delta) {
        var container = _container;
        if (container is null || !GodotObject.IsInstanceValid(container)) return;

        // Mirror the container's presentation state (attack-hide, fast-mode fade,
        // debug intent toggle all modulate/hide the container).
        Visible = container.Visible;
        Modulate = container.Modulate;

        // Vanilla NIntent bob, verbatim constants.
        Position = _basePosition + Vector2.Up *
            (Mathf.Sin((float)Time.GetTicksMsec() * 0.001f * (float)Math.PI + _bobPhase) * 10f + 8f);
    }
}
```

- [ ] **Step 2: Implement `src/Game/DecisionVotes/VoterNamesPatch.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: assigns pool names to enemy creature nodes and lays out the
/// name label under the intent icons (which shift up IntentShiftPx to make
/// room — StS1 used 42px). Assignment is per NCreature node lifetime via
/// ConditionalWeakTable; death/teardown cleans up through Godot's scene tree
/// (label is a child of the creature node). Pure cosmetics: every path is
/// try/catch → Warn; no game-state contact anywhere.
/// </summary>
internal static class VoterNamesPatch {
    internal const float IntentShiftPx = 40f;
    internal const float NameGapPx = 6f;   // gap between shifted icons and the label

    private sealed class Assignment {
        public required string VoterKey;
        public required string DecoratedName;
        public VoterNameLabel? Label;
    }

    private static readonly ConditionalWeakTable<NCreature, Assignment> Assignments = new();

    private static bool FeatureOn => ModSettings.Current?.NameEnemiesAfterVoters == true;

    private static bool IsMultiplayer() {
        try {
            return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count is int n && n > 1;
        } catch { return false; }
    }

    /// <summary>Task 8's sweep: all currently named, alive, valid creature nodes.</summary>
    internal static IEnumerable<(NCreature Node, string VoterKey)> NamedLivingCreatures() {
        foreach (var (node, assignment) in Assignments) {
            if (!GodotObject.IsInstanceValid(node)) continue;
            if (node.Entity is null || node.Entity.IsDead) continue;
            yield return (node, assignment.VoterKey);
        }
    }

    // UpdateBounds(Node) is the single site that lays IntentContainer out from
    // the per-creature IntentPos marker. Postfix: draw/attach the name and
    // shift the icons up. Re-runs whenever vanilla re-lays-out — that also
    // makes mid-run setting toggles self-heal (label removed / re-added on the
    // next pass) with no restore bookkeeping: vanilla recomputes Position from
    // the marker at every call before our shift.
    [HarmonyPatch(typeof(NCreature), "UpdateBounds", typeof(Node))]
    internal static class UpdateBounds_Postfix {
        static bool Prepare(System.Reflection.MethodBase? original) {
            if (original is not null) return true;
            if (AccessTools.Method(typeof(NCreature), "UpdateBounds", new[] { typeof(Node) }) is null) {
                TiLog.Error("[SlayTheStreamer2][voter-names] NCreature.UpdateBounds(Node) not found; enemy naming disabled");
                return false;
            }
            return true;
        }

        static void Postfix(NCreature __instance) {
            try {
                if (!GodotObject.IsInstanceValid(__instance)) return;
                var existing = Assignments.TryGetValue(__instance, out var assignment) ? assignment : null;

                if (!FeatureOn) {
                    // Toggled off mid-run: drop the label; vanilla layout already restored
                    // (this postfix simply didn't shift anything this pass).
                    if (existing?.Label is { } stale && GodotObject.IsInstanceValid(stale)) {
                        stale.QueueFree();
                        existing.Label = null;
                    }
                    return;
                }
                if (IsMultiplayer()) return;
                if (NCombatRoom.Instance is null) return;              // bestiary/menus: never name
                if (__instance.Entity is null) return;                  // too early; retry next pass
                if (__instance.Entity.Monster is null) return;          // players are never named

                if (existing is null) {
                    if (!VoterNamePoolHook.TakeNameLocked(out var decorated, out var key)) return;   // empty pool: vanilla look
                    existing = new Assignment { VoterKey = key, DecoratedName = decorated };
                    Assignments.Add(__instance, existing);
                    TiLog.Info($"[SlayTheStreamer2][voter-names] named {__instance.Entity.Monster.Id.Entry} after '{decorated}'");
                }

                if (existing.Label is null || !GodotObject.IsInstanceValid(existing.Label)) {
                    existing.Label = VoterNameLabel.TryCreate(existing.DecoratedName, __instance);
                    if (existing.Label is null) return;
                    __instance.AddChild(existing.Label);
                }

                // Vanilla just set IntentContainer.Position from the marker; shift the
                // icons up and park the label's base in the freed space below them.
                var container = __instance.IntentContainer;
                var originalPos = container.Position;
                container.Position = originalPos - new Vector2(0f, IntentShiftPx);

                var label = existing.Label;
                label.Size = new Vector2(Math.Max(container.Size.X, 300f), 30f);
                label.SetBasePosition(new Vector2(
                    originalPos.X + container.Size.X * 0.5f - label.Size.X * 0.5f,
                    originalPos.Y + container.Size.Y - IntentShiftPx + NameGapPx));
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][voter-names] UpdateBounds postfix failed: {ex.Message}");
            }
        }
    }
}
```

- [ ] **Step 3: Add the Compile Remove entry**

In `tests/slay_the_streamer_2.tests.csproj`, after the `CombatOriginTags.cs` entry:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\VoterNamesPatch.cs" />
```

(`src/Game/Ui/VoterNameLabel.cs` is outside every test-glob include — confirm `..\src\Game\Ui` still has only surgical `<Compile Include>` entries and none covers it.)

- [ ] **Step 4: Build + full suite**

Run: `dotnet build src/slay_the_streamer_2.csproj -v q` then `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: 0 errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/VoterNamesPatch.cs src/Game/Ui/VoterNameLabel.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "voter-names/7: name labels under intent icons - assignment, bob-synced label, 40px intent shift"
```

---

### Task 8: `VoterSpeechPatch` — speech bubbles from named enemies (game-side)

**Files:**
- Create: `src/Game/DecisionVotes/VoterSpeechPatch.cs` (game-side)
- Modify: `src/ModEntry.cs` (one wiring line after `VoterNamePoolHook.Attach(Coordinator);`)
- Modify: `tests/slay_the_streamer_2.tests.csproj` — `<Compile Remove="..\src\Game\DecisionVotes\VoterSpeechPatch.cs" />`
- Test: none beyond Task 4's sanitizer (Godot/MegaCrit types); build check + operator matrix.

**Interfaces:**
- Consumes: `VoterNamesPatch.NamedLivingCreatures()` (Task 7), `BubbleText.Sanitize/RawCharCount` (Task 4), `ModSettings.Current.NamedEnemiesSpeak` + `.NameEnemiesAfterVoters` (Task 5), `IChatConsumer.MessageReceived`, `IMainThreadDispatcher`.
- Produces: `internal static class VoterSpeechPatch { public static void Attach(IChatConsumer chat, IMainThreadDispatcher dispatcher); }`.

**Game-side ground truth (verified v0.111.0, 2026-08-28):**
- `NSpeechBubbleVfx.Create(string text, Creature speaker, double secondsToDisplay, VfxColor vfxColor = VfxColor.White)` — public, plain string (namespace `MegaCrit.Sts2.Core.Nodes.Vfx`).
- Vanilla duration rule (`TalkCmd.Play`): `max(0.5, rawCharCount * 0.12)` (0.10 when `SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast`); attach via `speaker.GetVfxContainer()?.AddChildSafely(vfx)`.
- `VfxColor`/`FastModeType`/`AddChildSafely` namespaces: resolve at compile time from `TalkCmd.cs`'s using set — `MegaCrit.Sts2.Core.Settings`, `MegaCrit.Sts2.Core.Saves`, `MegaCrit.Sts2.Core.Helpers`.

- [ ] **Step 1: Implement `src/Game/DecisionVotes/VoterSpeechPatch.cs`**

```csharp
using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Helpers;        // AddChildSafely
using MegaCrit.Sts2.Core.Nodes.Vfx;      // NSpeechBubbleVfx
using MegaCrit.Sts2.Core.Saves;          // SaveManager, FastModeType
using MegaCrit.Sts2.Core.Settings;       // VfxColor
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// voter-names: while a voter's name is on a living enemy, that voter's chat
/// messages replay as vanilla speech bubbles from the enemy. Matching is by
/// exact VoterKey (robust across Jr./Roman decorations and both platforms —
/// deliberately better than StS1's name-string comparison). Chat events fire
/// on background threads; everything Godot-facing is marshalled through the
/// main-thread dispatcher. Pure cosmetics; every path try/catch → Warn.
/// </summary>
internal static class VoterSpeechPatch {
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, DateTimeOffset> _lastBubbleByVoter = new();
    private static IMainThreadDispatcher? _dispatcher;

    public static void Attach(IChatConsumer chat, IMainThreadDispatcher dispatcher) {
        _dispatcher = dispatcher;
        chat.MessageReceived += OnMessage;
    }

    private static void OnMessage(object? sender, ChatMessage msg) {
        try {
            var settings = ModSettings.Current;
            if (settings is null || !settings.NamedEnemiesSpeak || !settings.NameEnemiesAfterVoters) return;
            var dispatcher = _dispatcher;
            if (dispatcher is null) return;

            var text = BubbleText.Sanitize(msg.Text);
            if (text is null) return;
            var voterKey = msg.VoterKey;

            // Cooldown check on the chat thread (cheap, racy-tolerant: worst
            // case one extra bubble). The dictionary is only mutated here.
            lock (_lastBubbleByVoter) {
                var now = DateTimeOffset.UtcNow;
                if (_lastBubbleByVoter.TryGetValue(voterKey, out var last) && now - last < Cooldown) return;
                _lastBubbleByVoter[voterKey] = now;
            }

            dispatcher.Post(() => ShowBubbleOnMainThread(voterKey, text));
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] bubble handler failed: {ex.Message}");
        }
    }

    private static void ShowBubbleOnMainThread(string voterKey, string text) {
        try {
            if (OverlayOcclusion.IsOccludingOverlayVisible()) return;   // popup up: bubble would be buried
            foreach (var (node, key) in VoterNamesPatch.NamedLivingCreatures()) {
                if (key != voterKey) continue;
                var creature = node.Entity;
                if (creature is null || creature.IsDead) return;

                bool fast = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast;
                double seconds = Math.Max(0.5, BubbleText.RawCharCount(text) * (fast ? 0.10 : 0.12));
                var vfx = NSpeechBubbleVfx.Create(text, creature, seconds, VfxColor.White);
                if (vfx != null) {
                    creature.GetVfxContainer()?.AddChildSafely(vfx);
                    TiLog.Info($"[SlayTheStreamer2][voter-names] bubble from '{voterKey}' ({text.Length} chars)");
                }
                return;   // one enemy per voter key by construction
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][voter-names] bubble create failed: {ex.Message}");
        }
    }
}
```

NOTE for the implementer: `OverlayOcclusion.IsOccludingOverlayVisible` is the
existing helper used by `CardRewardVotePatch.HandleVoteAsync` (src/Game/DecisionVotes/OverlayOcclusion.cs) —
check its exact member shape (method vs property, parameters) at the call site
and match it. If the namespaces for `VfxColor`/`FastModeType`/`AddChildSafely`
differ on the current game DLL, resolve from compiler errors — the members
themselves are verified present.

- [ ] **Step 2: Wire in `src/ModEntry.cs`** — after `VoterNamePoolHook.Attach(Coordinator);`:

```csharp
                VoterSpeechPatch.Attach(multi, dispatcher);
```

- [ ] **Step 3: Add the Compile Remove entry**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\VoterSpeechPatch.cs" />
```

- [ ] **Step 4: Build + full suite**

Run: `dotnet build src/slay_the_streamer_2.csproj -v q` then `dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: 0 errors; all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/VoterSpeechPatch.cs src/ModEntry.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "voter-names/8: speech bubbles - named enemies repeat their voter's chat (8s cooldown, sanitized)"
```

---

### Task 9: Docs + deploy for operator validation

**Files:**
- Modify: `README.md` (feature list, "What chat votes on" adjacent extras section, in-game settings list, known-caveats moderation note)
- Modify: `notes/06-followups-and-deferred.md` (new slice entry + operator matrix)
- No code.

- [ ] **Step 1: README** — add to the "🎛 Streamer-side extras" section:

```markdown
- **Enemies named after voters** *(new in v0.3.0, on by default)* — enemy creatures are named after chatters who vote, shown under their intent icons. Everyone gets a turn before anyone gets a second enemy (repeats become "Jr.", then "III"). With the companion setting on, a named enemy also **speaks its chatter's messages** as in-game speech bubbles. Bubble text is the raw chat message — your channel moderation is the content filter (turn just the bubbles off if that concerns you).
```

And to the "⚙️ In-game settings" list (after the Cursed Overrides bullet, matching panel order):

```markdown
- **Name enemies after chat voters** — enemies get named after chatters who vote; fair rotation, repeats decorated "Jr."/"III". On by default.
- **Named enemies repeat their voter's chat** — a named enemy speaks its chatter's messages in a speech bubble (raw message text; your channel moderation is the filter). On by default; only applies while naming is on.
```

- [ ] **Step 2: notes/06 entry** — new section at the top, mirroring the card-scope entry's shape: summary (both keys, defaults, spec/plan paths, commit range `voter-names/1`–`/9`), mechanism one-liner (SessionStarted → pool → UpdateBounds postfix label + NSpeechBubbleVfx), and this operator validation matrix:

```markdown
### Operator validation matrix (voter-names; PENDING)

- [ ] No votes yet → combat enemies unnamed, fully vanilla layout (no intent shift)
- [ ] After first vote → next combat names enemies; log `[voter-names] named <ID> after '<name>'`
- [ ] Fairness: with 2 test voters, 3+ enemies → both names appear before any "Jr."
- [ ] "Jr." then "III" appear only after pool exhaustion (1 voter, several enemies)
- [ ] Intent icons shifted up; name bobs in sync with first icon; multi-icon move (MultiAttack+Buff) still centered
- [ ] Name hides during enemy attack animation and in fast-mode instant kills (mirrors intent fade)
- [ ] Hover nameplate (vanilla) unaffected; bestiary and boss-vote popup show NO names
- [ ] Boss + mid-combat summon get names; player creature never named
- [ ] Bubble: named voter's message appears from their enemy; 8s cooldown holds under spam; `[b]test[/b]` renders as `(b)test(/b)`; >64-char message truncates with "..."
- [ ] Bubble suppressed while a vote popup is open
- [ ] `namedEnemiesSpeak` off → names yes, bubbles no; `nameEnemiesAfterVoters` off mid-run → labels vanish on next layout pass, vanilla layout back
- [ ] Both off → full vanilla; MP run → full vanilla
- [ ] Save-quit → Continue mid-combat → fresh names appear (accepted divergence)
- [ ] YT + Twitch voters both enter the pool (check `[voter-names] pool now N` log line)
```

- [ ] **Step 3: Build + install for the operator pass**

```powershell
pwsh -File build.ps1
pwsh -File install.ps1   # game must be closed
```

- [ ] **Step 4: Commit**

```bash
git add README.md notes/06-followups-and-deferred.md
git commit -m "voter-names/9: README + notes/06 operator matrix"
```

Release prep (manifest bump to 0.3.0, workshop changeNote incl. the combatCardVotesOnly default flip, zip, gh release, workshop upload) happens AFTER the operator gate is green — not part of this plan.
