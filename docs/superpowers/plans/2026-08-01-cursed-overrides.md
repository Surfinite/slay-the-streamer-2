# Cursed Overrides Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each vote-override spend adds a random curse card to the streamer's deck, behind a `cursedOverrides` setting defaulting to off.

**Architecture:** A pure picker (`CurseRoll.cs`, rides the test-csproj glob) plus a game-side roller (`CursedOverrides.cs`, `Compile Remove`d from tests) called at the three existing `VoteOverrideBudget.RecordUse()` sites. The chat receipt extends `VoteOverrideBudget.FormatOverrideReceipt` with an optional curse clause. Settings ride the established `voteOverridesPerAct` plumbing.

**Tech Stack:** .NET 9, Harmony (existing patches only — no new patch classes), xUnit, Godot-side game commands (`CardPileCmd.AddCursesToDeck`, `TaskHelper.RunSafely`).

**Spec:** `docs/superpowers/specs/2026-08-01-cursed-overrides-design.md`

## Global Constraints

- Setting key `cursedOverrides`, bool, **default `false`**.
- Curse pool: `ModelDb.CardPool<CurseCardPool>().AllCards.Where(c => c.CanBeGeneratedByModifiers)` — never hand-list curse types.
- RNG: private `Random` instance; **never** touch the run's seeded RNG.
- A curse-roll failure must never break the override: catch, `TiLog.Warn` with `[cursed-override]` prefix, return null.
- `VoteOverrideBudget.cs` and `CurseRoll.cs` stay Godot/MegaCrit-free (test-csproj glob compiles `src/Game/DecisionVotes/**`).
- Curse rolls strictly **after** `RecordUse()`; budget is consumed strictly after `TryCloseNow` succeeds (existing rule, unchanged).
- Commit prefix: `cursed-overrides/N:` — commits to main are pre-authorized within slice work.
- Test classes touching `VoteSession` tally events need `[Collection("TiLog.Sink")]` (none of the new tests do — they are pure formatters/pickers — but any future addition must).
- Run tests with: `dotnet test tests/slay_the_streamer_2.tests.csproj` (add `--filter` per task). Full pipeline: `pwsh -File build.ps1` then `pwsh -File install.ps1`.

---

### Task 1: `cursedOverrides` setting plumbing

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs` (record ~line 10-23; parser ~line 224-260)
- Modify: `src/Game/Bootstrap/SettingsBootstrap.cs` (~line 58-60, defaults dictionary)
- Modify: `src/Game/Ui/Settings/SettingsWriter.cs` (~line 35-37)
- Test: `tests/Bootstrap/ModSettingsTests.cs`, `tests/Game/Ui/Settings/SettingsWriterTests.cs`

**Interfaces:**
- Consumes: existing `ChatSettings` record, `ModSettings.FromJson` parse conventions.
- Produces: `ChatSettings.CursedOverrides` (bool, default false) — read later via `ModSettings.Current?.CursedOverrides ?? false`.

- [ ] **Step 1: Write the failing parse tests** in `tests/Bootstrap/ModSettingsTests.cs`, next to the `voteOverridesPerAct` block (~line 753), following the same temp-JSON pattern used there:

```csharp
    // --- cursedOverrides (cursed-overrides: override spends add a random curse, default false) ---

    [Theory]
    [InlineData("\"cursedOverrides\": true,", true, false)]
    [InlineData("\"cursedOverrides\": false,", false, false)]
    [InlineData("\"cursedOverrides\": \"yes\",", false, true)]  // non-bool -> default + warning
    [InlineData("", false, false)]                               // missing -> default, no warning
    public void CursedOverrides_parses_and_defaults(string fragment, bool expected, bool expectWarning) {
        // Body copied from VoteOverridesPerAct_parses_clamps_and_defaults directly above:
        // same WriteTempJson skeleton with {{fragment}} spliced in, then
        // assert settings.CursedOverrides == expected and warning presence.
    }
```

Copy the exact `WriteTempJson` body from `VoteOverridesPerAct_parses_clamps_and_defaults` (same file, directly above) — only the fragment slot and the two assertions differ (`Assert.Equal(expected, success.Settings.CursedOverrides)` and the warnings check for substring `"cursedOverrides"`).

Also add to `tests/Game/Ui/Settings/SettingsWriterTests.cs`, following its existing key-roundtrip pattern: a case asserting the written JSON contains `"cursedOverrides": true` when the setting is true.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~CursedOverrides_parses"`
Expected: FAIL — `ChatSettings` has no `CursedOverrides` member (compile error is the failure mode here).

- [ ] **Step 3: Implement.** In `src/Game/Bootstrap/ModSettings.cs`:

Record (append after `VoteOverridesPerAct`):

```csharp
public sealed record ChatSettings(
    ... ,
    int VoteOverridesPerAct = 1,
    bool CursedOverrides = false);
```

Parser (insert after the `voteOverridesPerAct` block ~line 255, mirroring the `allowSameBossTwice` bool pattern at ~line 224):

```csharp
            bool cursedOverrides = false;
            if (root.TryGetProperty("cursedOverrides", out var cursedProp)) {
                if (cursedProp.ValueKind == JsonValueKind.True) cursedOverrides = true;
                else if (cursedProp.ValueKind == JsonValueKind.False) cursedOverrides = false;
                else warnings.Add("cursedOverrides is not a boolean; using default (false)");
            }
```

Append `cursedOverrides` to the `new ChatSettings(...)` construction (~line 260).

In `src/Game/Bootstrap/SettingsBootstrap.cs` defaults dictionary (~line 60):

```csharp
        ["cursedOverrides"]      = false,
```

In `src/Game/Ui/Settings/SettingsWriter.cs` (~line 37):

```csharp
        json["cursedOverrides"] = settings.CursedOverrides;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~CursedOverrides_parses|FullyQualifiedName~SettingsWriter"`
Expected: PASS. Then run the full suite once (`dotnet test tests/slay_the_streamer_2.tests.csproj`) — the record gained a defaulted parameter, so existing constructions must still compile and pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Bootstrap/ModSettings.cs src/Game/Bootstrap/SettingsBootstrap.cs src/Game/Ui/Settings/SettingsWriter.cs tests/Bootstrap/ModSettingsTests.cs tests/Game/Ui/Settings/SettingsWriterTests.cs
git commit -m "cursed-overrides/1: cursedOverrides setting plumbed end-to-end (default off)"
```

---

### Task 2: `CurseRoll.PickCurse` — pure picker

**Files:**
- Create: `src/Game/DecisionVotes/CurseRoll.cs`
- Test: `tests/Game/DecisionVotes/CurseRollTests.cs`

**Interfaces:**
- Consumes: nothing (BCL only — this file rides the test-csproj glob; no Godot/MegaCrit types allowed).
- Produces: `internal static T PickCurse<T>(IReadOnlyList<T> pool, Random rng)` in `SlayTheStreamer2.Game.DecisionVotes.CurseRoll` — throws `ArgumentException` on empty pool.

- [ ] **Step 1: Write the failing tests** in `tests/Game/DecisionVotes/CurseRollTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class CurseRollTests {

    [Fact]
    public void PickCurse_returns_the_only_element_of_a_singleton_pool() =>
        Assert.Equal("Injury", CurseRoll.PickCurse(new[] { "Injury" }, new Random(42)));

    [Fact]
    public void PickCurse_is_deterministic_for_a_seeded_rng() {
        var pool = new[] { "Clumsy", "Debt", "Decay", "Doubt", "Guilty" };
        var a = CurseRoll.PickCurse(pool, new Random(1234));
        var b = CurseRoll.PickCurse(pool, new Random(1234));
        Assert.Equal(a, b);
    }

    [Fact]
    public void PickCurse_reaches_every_element_over_many_draws() {
        var pool = new[] { "Clumsy", "Debt", "Decay", "Doubt", "Guilty" };
        var rng = new Random(42);
        var seen = new HashSet<string>();
        for (int i = 0; i < 500; i++) seen.Add(CurseRoll.PickCurse(pool, rng));
        Assert.Equal(pool.Length, seen.Count);
    }

    [Fact]
    public void PickCurse_throws_on_empty_pool() =>
        Assert.Throws<ArgumentException>(() => CurseRoll.PickCurse(Array.Empty<string>(), new Random(42)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~CurseRollTests"`
Expected: FAIL — `CurseRoll` does not exist (compile error).

- [ ] **Step 3: Implement** `src/Game/DecisionVotes/CurseRoll.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure uniform picker for Cursed Overrides (spec 2026-08-01). Godot-free on
/// purpose: it rides the test csproj's DecisionVotes glob, so no Godot or
/// MegaCrit types may appear here. The game-side pool build + deck add live
/// in CursedOverrides.cs (Compile Remove'd from the test project).
/// </summary>
internal static class CurseRoll {
    internal static T PickCurse<T>(IReadOnlyList<T> pool, Random rng) {
        if (pool.Count == 0) throw new ArgumentException("curse pool is empty", nameof(pool));
        return pool[rng.Next(pool.Count)];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~CurseRollTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/CurseRoll.cs tests/Game/DecisionVotes/CurseRollTests.cs
git commit -m "cursed-overrides/2: CurseRoll.PickCurse pure uniform picker"
```

---

### Task 3: Receipt gains optional curse clause

**Files:**
- Modify: `src/Game/DecisionVotes/VoteOverrideBudget.cs:37-54`
- Test: `tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs`

**Interfaces:**
- Consumes: existing `FormatOverrideReceipt(string, string, int, int)` and `SendOverrideReceipt(string)`.
- Produces: `FormatOverrideReceipt(string streamerName, string takenLabel, int limit, int remaining, string? curseTitle = null)` and `SendOverrideReceipt(string takenLabel, string? curseTitle = null)`. Existing callers compile unchanged (defaulted parameter).

- [ ] **Step 1: Write the failing tests** — add to `tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs`:

```csharp
    [Theory]
    [InlineData("Surfinite", "Ricochet", "Injury", 2, 1,
        "Surfinite overrode the vote and took Ricochet. Cursed Overrides: gained Injury! 1 override remaining this act")]
    [InlineData("Surfinite", "Skip", "Doubt", 1, 0,
        "Surfinite overrode the vote and took Skip. Cursed Overrides: gained Doubt! 0 overrides remaining this act")]
    [InlineData("Surfinite", "Ricochet", "Writhe", -1, 2147483647,
        "Surfinite overrode the vote and took Ricochet. Cursed Overrides: gained Writhe!")]  // unlimited: no count
    public void FormatOverrideReceipt_appends_curse_clause_when_present(
        string name, string taken, string curse, int limit, int remaining, string expected) =>
        Assert.Equal(expected, VoteOverrideBudget.FormatOverrideReceipt(name, taken, limit, remaining, curse));

    [Fact]
    public void FormatOverrideReceipt_null_curse_is_unchanged() =>
        Assert.Equal("Surfinite overrode the vote and took Ricochet. 1 override remaining this act",
            VoteOverrideBudget.FormatOverrideReceipt("Surfinite", "Ricochet", 2, 1, null));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteOverrideBudgetTests"`
Expected: FAIL — no 5-argument `FormatOverrideReceipt` overload (compile error).

- [ ] **Step 3: Implement** — replace `FormatOverrideReceipt` and `SendOverrideReceipt` in `src/Game/DecisionVotes/VoteOverrideBudget.cs`:

```csharp
    /// <summary>Pure formatter, unit-tested. Unlimited (limit &lt; 0) omits the count.
    /// Non-null curseTitle appends the Cursed Overrides clause (spec 2026-08-01 §5).</summary>
    internal static string FormatOverrideReceipt(
            string streamerName, string takenLabel, int limit, int remaining, string? curseTitle = null) {
        string curse = curseTitle is null ? "" : $" Cursed Overrides: gained {curseTitle}!";
        if (limit < 0) return $"{streamerName} overrode the vote and took {takenLabel}.{curse}";
        string noun = remaining == 1 ? "override" : "overrides";
        return $"{streamerName} overrode the vote and took {takenLabel}.{curse} {remaining} {noun} remaining this act";
    }
```

```csharp
    public static void SendOverrideReceipt(string takenLabel, string? curseTitle = null) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = FormatOverrideReceipt(
            BootstrapModSettings.GetStreamerDisplayName(), takenLabel, Limit, Remaining, curseTitle);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.High);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~VoteOverrideBudgetTests"`
Expected: PASS, including the four pre-existing `FormatOverrideReceipt_covers_plural_zero_and_unlimited` cases (null default must not change them).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/VoteOverrideBudget.cs tests/Game/DecisionVotes/VoteOverrideBudgetTests.cs
git commit -m "cursed-overrides/3: override receipt gains optional curse clause"
```

---

### Task 4: `CursedOverrides.TryRollCurse` — game-side roller

**Files:**
- Create: `src/Game/DecisionVotes/CursedOverrides.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj:41` (add one `Compile Remove` line)

**Interfaces:**
- Consumes: `CurseRoll.PickCurse` (Task 2); `ModSettings.Current?.CursedOverrides` (Task 1); game APIs `ModelDb.CardPool<CurseCardPool>()`, `CardPileCmd.AddCursesToDeck(IEnumerable<CardModel>, Player)`, `TaskHelper.RunSafely(Task)`, `LocalContext.IsMe(Player?)`, `RunManager.Instance.DebugOnlyGetState()`.
- Produces: `internal static string? TryRollCurse(Player? player)` and `internal static string? TryRollCurseForLocalPlayer()` — both return the curse Title for the receipt, or null (setting off / no player / failure).

- [ ] **Step 1: Implement** `src/Game/DecisionVotes/CursedOverrides.cs` (no unit test — game-side by design; verified by build + operator gate):

```csharp
using System;
using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;
using BootstrapModSettings = SlayTheStreamer2.Game.Bootstrap.ModSettings;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Cursed Overrides (spec 2026-08-01): each vote-override spend adds a random
/// curse to the overriding player's deck. Game-side half of the feature —
/// pool build + deck add. The pure picker lives in CurseRoll.cs so it can
/// ride the test csproj glob; this file is Compile Remove'd there.
/// Called strictly AFTER VoteOverrideBudget.RecordUse(). A failure here must
/// never break the override: every path catches, Warns, and returns null.
/// </summary>
internal static class CursedOverrides {
    /// <summary>Private RNG — never the run's seeded RNG, which would shift
    /// vanilla's subsequent rolls.</summary>
    private static readonly Random _rng = new();

    /// <summary>Rolls and fire-and-forget-adds a curse for the local player.
    /// Card-reward override sites use this (the screen acts for the local
    /// player). Returns the curse Title for the receipt, or null.</summary>
    internal static string? TryRollCurseForLocalPlayer() {
        try {
            var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
            var local = players?.FirstOrDefault(p => LocalContext.IsMe(p));
            return TryRollCurse(local);
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][cursed-override] local-player resolve failed; no curse: {ex.Message}");
            return null;
        }
    }

    /// <summary>Rolls and fire-and-forget-adds a curse for the given player
    /// (ancient path passes the event Owner). Null player: Warn, no curse.</summary>
    internal static string? TryRollCurse(Player? player) {
        try {
            if (!(BootstrapModSettings.Current?.CursedOverrides ?? false)) return null;
            if (player is null) {
                TiLog.Warn("[SlayTheStreamer2][cursed-override] no player in reach; no curse");
                return null;
            }

            var pool = ModelDb.CardPool<CurseCardPool>().AllCards
                .Where(c => c.CanBeGeneratedByModifiers)
                .ToList();
            if (pool.Count == 0) {
                TiLog.Warn("[SlayTheStreamer2][cursed-override] curse pool is empty; no curse");
                return null;
            }

            var picked = CurseRoll.PickCurse(pool, _rng);
            TaskHelper.RunSafely(CardPileCmd.AddCursesToDeck(new[] { picked }, player));
            TiLog.Info($"[SlayTheStreamer2][cursed-override] {picked.Id.Entry} added to deck for override spend");
            return picked.Title;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][cursed-override] curse roll failed; override proceeds uncursed: {ex.Message}");
            return null;
        }
    }
}
```

(`TiLog.Warn` takes a single `string` — verified `src/Ti/Internal/TiLog.cs:24` — so interpolating `ex.Message` as above is correct.)

- [ ] **Step 2: Exclude from the test project.** In `tests/slay_the_streamer_2.tests.csproj`, add alongside the other DecisionVotes removals (after line 41):

```xml
    <Compile Remove="..\src\Game\DecisionVotes\CursedOverrides.cs" />
```

- [ ] **Step 3: Verify both projects build and tests still pass**

Run: `dotnet build src/slay_the_streamer_2.csproj -c Release` — Expected: build succeeds, no new warnings.
Run: `dotnet test tests/slay_the_streamer_2.tests.csproj` — Expected: PASS (the test project must not try to compile `CursedOverrides.cs`; a MegaCrit-type compile error here means the `Compile Remove` is missing or misspelled).

- [ ] **Step 4: Commit**

```bash
git add src/Game/DecisionVotes/CursedOverrides.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "cursed-overrides/4: game-side TryRollCurse (pool build + fire-and-forget deck add)"
```

---

### Task 5: Wire the three override sites

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs:158-159` and `:201-202` (the two `RecordUse()` + `SendOverrideReceipt` pairs)
- Modify: `src/Game/DecisionVotes/AncientVotePatch.cs:332-346` (`TryOverride`) and its two callers at `:96` and `:136`

**Interfaces:**
- Consumes: `CursedOverrides.TryRollCurseForLocalPlayer()` / `TryRollCurse(Player?)` (Task 4); `SendOverrideReceipt(string, string?)` (Task 3).
- Produces: nothing new — behavior only.

- [ ] **Step 1: Card-reward take-card site.** In `TryOverrideWithCard` (CardRewardVotePatch.cs ~line 158), replace:

```csharp
            VoteOverrideBudget.RecordUse();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel);
```

with:

```csharp
            VoteOverrideBudget.RecordUse();
            string? curseTitle = CursedOverrides.TryRollCurseForLocalPlayer();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel, curseTitle);
```

- [ ] **Step 2: Card-reward override-skip site.** In `TryOverrideWithSkip` (~line 201), same replacement with `"Skip"` as the label:

```csharp
            VoteOverrideBudget.RecordUse();
            string? curseTitle = CursedOverrides.TryRollCurseForLocalPlayer();
            VoteOverrideBudget.SendOverrideReceipt("Skip", curseTitle);
```

This covers both `includeSkip` sub-cases — the roll happens where the use is recorded, not in the resume handler (spec §4).

- [ ] **Step 3: Ancient site.** In `AncientVotePatch.cs`, change `TryOverride`'s signature to take the room so it can reach the event Owner:

```csharp
    private static bool TryOverride(NEventRoom room, EventOption option, int index) {
```

Update both callers (lines 96 and 136): `TryOverride(__instance, option, index)`.

Inside, after `RecordUse()` (~line 342), replace:

```csharp
            VoteOverrideBudget.RecordUse();
            string label;
            try { label = option.Title.GetFormattedText(); } catch { label = $"#{index}"; }
            VoteOverrideBudget.SendOverrideReceipt(label);
```

with:

```csharp
            VoteOverrideBudget.RecordUse();
            var owner = (_eventField.Value?.GetValue(room) as EventModel)?.Owner;
            string? curseTitle = CursedOverrides.TryRollCurse(owner);
            string label;
            try { label = option.Title.GetFormattedText(); } catch { label = $"#{index}"; }
            VoteOverrideBudget.SendOverrideReceipt(label, curseTitle);
```

- [ ] **Step 4: Build and run the full test suite**

Run: `dotnet build src/slay_the_streamer_2.csproj -c Release && dotnet test tests/slay_the_streamer_2.tests.csproj`
Expected: build clean, all tests pass (both patch files are `Compile Remove`d from tests, so this is a compile-and-regression check).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/CardRewardVotePatch.cs src/Game/DecisionVotes/AncientVotePatch.cs
git commit -m "cursed-overrides/5: roll a curse at all three override spend sites"
```

---

### Task 6: Settings panel row

**Files:**
- Modify: `src/Game/Ui/Settings/SettingsPanelBuilder.cs:186-187` (build order) and the method it calls

**Interfaces:**
- Consumes: existing `AddCheckboxRow(Container, string, bool, Action<bool>)` (~line 252), `AddHelpText`, `AddDivider`, `SettingsSaveDebouncer.MarkDirtyAndRestart`, `ChatSettings.CursedOverrides` (Task 1).
- Produces: nothing new — one more panel row.

- [ ] **Step 1: Add the row.** In `BuildPanel` (~line 186), directly after the vote-overrides dropdown block:

```csharp
        AddVoteOverridesDropdown(root, current, debouncer);
        AddHelpText(root, "Times per act the streamer can override a live vote by clicking\nan option mid-countdown. Clicking Skip mid-vote costs an override,\nnot a card skip. Resets each act.");
        AddDivider(root);
        AddCheckboxRow(root, "Cursed Overrides", current.CursedOverrides,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { CursedOverrides = value }));
        AddHelpText(root, "Each vote override also adds a random curse card to your deck.");
```

(The first three lines already exist — only the `AddDivider`/`AddCheckboxRow`/`AddHelpText` trio is new. The existing `AddDivider` before "Show vote tag" stays.)

- [ ] **Step 2: Build**

Run: `dotnet build src/slay_the_streamer_2.csproj -c Release`
Expected: clean build (panel code is Godot-side; visual check happens in the operator gate).

- [ ] **Step 3: Commit**

```bash
git add src/Game/Ui/Settings/SettingsPanelBuilder.cs
git commit -m "cursed-overrides/6: Cursed Overrides tickbox in settings panel"
```

---

### Task 7: Build, deploy, operator-validation gate

**Files:**
- Modify: `notes/06-followups-and-deferred.md` (gate results entry, after validation)

- [ ] **Step 1: Full pipeline**

```powershell
pwsh -File build.ps1     # rebuild dist/ (publish + tests + assemble)
pwsh -File install.ps1   # copy dist/ -> Steam mods folder
```

Expected: 470+ tests pass; after launch, `godot.log` mod version stamp matches `git log -1 --format=%H`.

- [ ] **Step 2: Operator-validation gate** (Surfinite, live game + chat). Checklist from spec §7:

1. Setting **on**, card-take override: curse lands, card-added preview plays, receipt reads `"... took X. Cursed Overrides: gained Y! N override(s) remaining this act"`.
2. Setting **on**, override-to-Skip (both a vote with Skip as option #0 and one without): curse lands, receipt labels `Skip`.
3. Setting **on**, ancient-option override: curse lands, receipt shows the option label + curse.
4. Setting **off** (default): all three paths behave exactly as v0.2.0 — no curse, receipt unchanged.
5. Exhausted budget: clicking mid-vote does nothing — no curse roll (no `RecordUse`, so no roll).
6. Save-quit → Continue after a cursed override: curse still in deck (mid-room snapshot landmine check).
7. Settings panel: tickbox renders under the Vote Overrides dropdown, toggles persist across game restart (check `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` gains `"cursedOverrides"`).

- [ ] **Step 3: Record gate results** in `notes/06-followups-and-deferred.md` following the existing acceptance-gate entry format; note any deferred minors.

- [ ] **Step 4: Tag and commit**

```bash
git add notes/06-followups-and-deferred.md
git commit -m "cursed-overrides/7: acceptance-gate results"
git tag cursed-overrides-complete
```

---

## Self-Review Notes

- Spec §2 (setting) → Tasks 1, 6. Spec §3 (picker split) → Tasks 2, 4. Spec §4 (three sites, ordering) → Task 5. Spec §5 (receipt) → Task 3. Spec §6 (edge cases) → Task 4 (null/empty/exception paths) + Task 7 gate items 4-6. Spec §7 (testing) → Tasks 1-3 unit, Task 7 gate.
- Type check: `PickCurse<T>` is generic — Task 2 tests use `string`, Task 4 uses `CardModel`; consistent. `SendOverrideReceipt(string, string?)` produced in Task 3 matches all three Task 5 call sites. `TryOverride(NEventRoom, EventOption, int)` signature change is confined to AncientVotePatch (both callers updated in the same task).
- `CardModel.Title` assigns directly to `string` (established by `takenLabel` in CardRewardVotePatch); no `GetFormattedText()` needed on the card path.
