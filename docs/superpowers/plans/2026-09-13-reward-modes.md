# Reward Modes (three-way non-combat setting, Draft tag, Ancient override counter, v0.4.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `combatCardVotesOnly` checkbox with a three-way `nonCombatCardRewards` setting (`free` | `removeOne` | `mixed`, default `mixed`), make the Draft modifier a streamer-only pick in every mode, show the vote-override budget on the Ancient vote screen, then ship v0.4.0.

**Architecture:** The setting is a Godot-free enum carried on `ChatSettings`; `AuthorityRules.Resolve` (pure, unit-tested) takes the enum instead of a bool and every game patch keeps calling `RewardAuthority.Classify`, so the new mode propagates without touching the vote patches. Text stays read-time (`LocTable.GetRawText` postfix) and becomes mode-aware through one extra `SuffixFor` parameter. Draft picks get their own origin tag by wrapping the `Func<Task>` that `Draft.GenerateNeowOption` returns, because the ten `CardReward`s are constructed after awaits and a push/pop around the method would only see the first. The Ancient counter is a third label inside `AncientVotePopup` fed by a shared Godot-free text helper.

**Tech Stack:** C# / .NET 9, Godot 4 (Godot.NET.Sdk mod project), HarmonyLib 2.4, xUnit tests in `tests/` (Microsoft.NET.Sdk, no Godot types), `pwsh -File build.ps1` + `pwsh -File install.ps1`.

**Spec:** `notes/handoff-2026-09-13-remove-one-testing-and-next.md` section 5 (Surfinite's rulings, 2026-09-13), building on `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md`.

## Global Constraints

- Commit prefix for this slice: `reward-modes/N:` (add to CLAUDE.md in Task 7). Per-task commits to `main` are pre-authorized.
- No em dashes anywhere in shipped text, code comments, or notes. Use commas, colons, or full stops.
- Chat votes stay 0-indexed. Nothing here changes vote indexing.
- TI/Game seam: nothing under `src/Ti/` is touched.
- Files under `src/Game/DecisionVotes/` are compiled into the test project by glob. Any NEW file there that references Godot or `MegaCrit.*` types needs a `<Compile Remove>` line in `tests/slay_the_streamer_2.tests.csproj`. Godot-free files there are unit-testable and need nothing.
- Any test class that can trigger `TiLog.Info/Warn/Error` must carry `[Collection("TiLog.Sink")]`.
- Harmony `___field` injection uses the literal field name after three underscores (`LocTable._name` is `____name`).
- Help text in the settings panel: one line per mode, concise (Surfinite: long descriptions are the same as no description).
- JSON key `nonCombatCardRewards`, string values exactly `free`, `removeOne`, `mixed` (parse case-insensitively, write camelCase). Default `mixed`.
- Migration: old `combatCardVotesOnly` `true` -> `free`, `false` -> `mixed`; the old key is dropped on the next write.
- Draft picks: streamer picks in ALL modes, chat never votes on them.
- Run `cd tests && dotnet test --nologo -v q` for the unit suite (about 590 tests, 6 s). `pwsh -File build.ps1` runs publish + tests + assembles `dist/`. `pwsh -File install.ps1` copies `dist/` to the Steam mods folder and is refused while the game is running.
- Game facts used below: `Draft.GenerateNeowOption(EventModel)` is `public override Func<Task>` on `MegaCrit.Sts2.Core.Models.Modifiers.Draft`; its private `OfferRewards` loops ten times constructing `new CardReward(creationOptions, 3, player) { CanSkip = false }` with an await between iterations. Orrery grants five card rewards, Strongbox two, Colorful Philosophers three per option, Trial Guilty two, Brain Leech one, Future of Potions one.

---

## File map

| File | Responsibility |
|---|---|
| `src/Game/Bootstrap/NonCombatRewardMode.cs` (new) | The enum + JSON string mapping. Godot-free. |
| `src/Game/Bootstrap/ModSettings.cs` | Record field, parser with migration. |
| `src/Game/Bootstrap/SettingsBootstrap.cs` | Template key, migration of the old key at file-ensure time. |
| `src/Game/Ui/Settings/SettingsWriter.cs` | Writes the new key, removes the old one. |
| `src/slay_the_streamer_2.json.example` | Documented default. |
| `src/Game/Ui/Settings/SettingsPanelBuilder.cs` | Dropdown row + three help lines replace the checkbox. |
| `src/Game/DecisionVotes/AuthorityMode.cs` | `RewardOrigin.DraftTagged`; `Resolve`/`RulesActive` take the mode. |
| `src/Game/DecisionVotes/RewardAuthority.cs` | Reads the mode from settings; fills `DraftTagged`. |
| `src/Game/DecisionVotes/RelicOriginTags.cs` | Learns relics with the classifier's verdict, not a hard-coded rarity rule. |
| `src/Game/DecisionVotes/AuthorityLoc.cs` | Mode-aware suffixes. |
| `src/Game/DecisionVotes/LocTextPatch.cs` | Passes the mode. |
| `src/Game/DecisionVotes/DraftOriginTags.cs` (new, Harmony, `Compile Remove`) | Draft origin tag. |
| `src/Game/DecisionVotes/BudgetCounterText.cs` (new, Godot-free) | Shared skip/override counter BBCode. |
| `src/Game/Ui/StreamerBudgetCounterLabel.cs` | Uses `BudgetCounterText`. |
| `src/Game/Ui/AncientVotePopup.cs` | Override counter line. |
| `README.md`, `notes/06-followups-and-deferred.md`, `notes/14-remove-one-matrix.md`, `CLAUDE.md` | Docs. |
| `src/slay_the_streamer_2.json`, `workshop/workshop.json` | Release. |

---

### Task 1: The setting (enum, parse with migration, write, bootstrap, example)

**Files:**
- Create: `src/Game/Bootstrap/NonCombatRewardMode.cs`
- Modify: `src/Game/Bootstrap/ModSettings.cs` (record at line 10-27; parser block at lines 269-277 and the ctor call at line 306)
- Modify: `src/Game/Ui/Settings/SettingsWriter.cs:39`
- Modify: `src/Game/Bootstrap/SettingsBootstrap.cs` (template line 62; `AddMissingKeys` at line 84)
- Modify: `src/slay_the_streamer_2.json.example:17`
- Modify (compile fix only): `src/Game/DecisionVotes/RewardAuthority.cs:15`, `src/Game/Ui/Settings/SettingsPanelBuilder.cs:183-185`
- Test: `tests/Bootstrap/ModSettingsTests.cs`, `tests/Bootstrap/SettingsBootstrapTests.cs`, `tests/Game/Ui/Settings/SettingsWriterTests.cs`

**Interfaces:**
- Produces: `public enum NonCombatRewardMode { Free, RemoveOne, Mixed }` in namespace `SlayTheStreamer2.Game.Bootstrap`; `public static class NonCombatRewardModes` with `public const string Key = "nonCombatCardRewards"`, `public static string ToJson(NonCombatRewardMode m)` returning `"free"`/`"removeOne"`/`"mixed"`, `public static bool TryParse(string? s, out NonCombatRewardMode m)` (case-insensitive, trims).
- Produces: `ChatSettings.NonCombatCardRewards` (type `NonCombatRewardMode`, default `Mixed`) REPLACING `CombatCardVotesOnly` at the same positional slot.
- Consumed by Task 2 (panel), Task 3 (rules).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Bootstrap/ModSettingsTests.cs`, replacing the existing `CombatCardVotesOnly_parses_and_defaults` theory (lines 804-828) with:

```csharp
    // --- nonCombatCardRewards (three-way: free | removeOne | mixed; default mixed, Surfinite
    // 2026-09-13). The old combatCardVotesOnly bool migrates: true -> free, false -> mixed. ---

    [Theory]
    [InlineData("\"nonCombatCardRewards\": \"free\",", NonCombatRewardMode.Free, false)]
    [InlineData("\"nonCombatCardRewards\": \"removeOne\",", NonCombatRewardMode.RemoveOne, false)]
    [InlineData("\"nonCombatCardRewards\": \"REMOVEONE\",", NonCombatRewardMode.RemoveOne, false)]
    [InlineData("\"nonCombatCardRewards\": \"mixed\",", NonCombatRewardMode.Mixed, false)]
    [InlineData("\"nonCombatCardRewards\": \"banana\",", NonCombatRewardMode.Mixed, true)]   // unknown -> default + warning
    [InlineData("\"nonCombatCardRewards\": 3,", NonCombatRewardMode.Mixed, true)]            // non-string -> default + warning
    [InlineData("", NonCombatRewardMode.Mixed, false)]                                        // missing -> default, no warning
    public void NonCombatCardRewards_parses_and_defaults(string fragment, NonCombatRewardMode expected, bool expectWarning) {
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
            Assert.Equal(expected, success.Settings.NonCombatCardRewards);
            Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("nonCombatCardRewards")));
        } finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"combatCardVotesOnly\": true,", NonCombatRewardMode.Free)]
    [InlineData("\"combatCardVotesOnly\": false,", NonCombatRewardMode.Mixed)]
    public void CombatCardVotesOnly_migrates_when_new_key_absent(string fragment, NonCombatRewardMode expected) {
        var path = WriteTempJson($$"""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            {{fragment}}
            "cardSkipsPerAct": 1
        }
        """);
        try {
            var success = Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
            Assert.Equal(expected, success.Settings.NonCombatCardRewards);
            Assert.Contains(success.Warnings, w => w.Contains("combatCardVotesOnly") && w.Contains("migrated"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void NewKey_wins_over_old_key() {
        var path = WriteTempJson("""
        {
            "schemaVersion": 1, "channel": "x", "username": "y",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "combatCardVotesOnly": true,
            "nonCombatCardRewards": "removeOne",
            "cardSkipsPerAct": 1
        }
        """);
        try {
            var success = Assert.IsType<SettingsResult.Success>(ModSettings.Load(path));
            Assert.Equal(NonCombatRewardMode.RemoveOne, success.Settings.NonCombatCardRewards);
        } finally { File.Delete(path); }
    }
```

Add a new test class file `tests/Bootstrap/NonCombatRewardModeTests.cs`:

```csharp
using SlayTheStreamer2.Game.Bootstrap;
using Xunit;

namespace SlayTheStreamer2.Tests.Bootstrap;

public class NonCombatRewardModeTests {
    [Theory]
    [InlineData(NonCombatRewardMode.Free, "free")]
    [InlineData(NonCombatRewardMode.RemoveOne, "removeOne")]
    [InlineData(NonCombatRewardMode.Mixed, "mixed")]
    public void ToJson_RoundTrips(NonCombatRewardMode mode, string json) {
        Assert.Equal(json, NonCombatRewardModes.ToJson(mode));
        Assert.True(NonCombatRewardModes.TryParse(json, out var back));
        Assert.Equal(mode, back);
    }

    [Theory]
    [InlineData(" Free ")]
    [InlineData("REMOVEONE")]
    [InlineData("remove_one")]
    public void TryParse_IsForgivingAboutCaseAndSpaces(string s) => Assert.True(NonCombatRewardModes.TryParse(s, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("banana")]
    public void TryParse_RejectsUnknown(string? s) => Assert.False(NonCombatRewardModes.TryParse(s, out _));
}
```

In `tests/Game/Ui/Settings/SettingsWriterTests.cs`, replace `Write_persists_combatCardVotesOnly` (line 112-121) with:

```csharp
    [Fact]
    public void Write_persists_nonCombatCardRewards_and_drops_the_old_key() {
        var path = TempPath();
        try {
            File.WriteAllText(path, """{ "schemaVersion": 1, "combatCardVotesOnly": true }""");
            var settings = MakeSettings() with { NonCombatCardRewards = NonCombatRewardMode.RemoveOne };
            SettingsWriter.Write(path, settings);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("removeOne", (string)json["nonCombatCardRewards"]!);
            Assert.False(json.ContainsKey("combatCardVotesOnly"));
        } finally { if (File.Exists(path)) File.Delete(path); }
    }
```

In `tests/Bootstrap/SettingsBootstrapTests.cs`, add after `EnsureFile_AddsMissingKeys_WithoutTouchingExistingValues`:

```csharp
    [Theory]
    [InlineData("true", "free")]
    [InlineData("false", "mixed")]
    public void EnsureFile_MigratesCombatCardVotesOnly(string oldValue, string expectedMode) {
        var path = WriteTemp($$"""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345",
            "combatCardVotesOnly": {{oldValue}}
        }
        """);
        try {
            var outcome = SettingsBootstrap.EnsureFile(path);
            var added = Assert.IsType<SettingsBootstrap.Outcome.AddedMissingKeys>(outcome);
            Assert.Contains("nonCombatCardRewards", added.Keys);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(expectedMode, (string)json["nonCombatCardRewards"]!);
            Assert.False(json.ContainsKey("combatCardVotesOnly"));
        } finally { CleanUp(path); }
    }

    [Fact]
    public void EnsureFile_NewFile_DefaultsToMixed() {
        var path = WriteTemp("""
        {
            "schemaVersion": 1,
            "channel": "surfinite",
            "username": "surfinitebot",
            "oauthToken": "abc123def456ghi789jkl012mno345"
        }
        """);
        try {
            SettingsBootstrap.EnsureFile(path);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal("mixed", (string)json["nonCombatCardRewards"]!);
        } finally { CleanUp(path); }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd tests && dotnet test --nologo -v q --filter "FullyQualifiedName~NonCombatRewardMode|FullyQualifiedName~NonCombatCardRewards|FullyQualifiedName~Migrat|FullyQualifiedName~DefaultsToMixed|FullyQualifiedName~drops_the_old_key"`
Expected: compile errors (`NonCombatRewardMode` does not exist).

- [ ] **Step 3: Create the enum file**

`src/Game/Bootstrap/NonCombatRewardMode.cs`:

```csharp
using System;

namespace SlayTheStreamer2.Game.Bootstrap;

/// <summary>How card rewards that did NOT come from combat are handled (Surfinite's
/// ruling 2026-09-13, replacing the combatCardVotesOnly checkbox).
/// Free: the streamer picks, Skip allowed, no chat vote (the old checkbox On).
/// RemoveOne: every non-combat card reward gets the remove-one vote.
/// Mixed (default): Ancient relics and Dream Catcher get the remove-one vote; event and
/// shop-relic rewards are unskippable with no vote (the old checkbox Off).</summary>
public enum NonCombatRewardMode { Free, RemoveOne, Mixed }

public static class NonCombatRewardModes {
    public const string Key = "nonCombatCardRewards";
    public const NonCombatRewardMode Default = NonCombatRewardMode.Mixed;

    public static string ToJson(NonCombatRewardMode mode) => mode switch {
        NonCombatRewardMode.Free => "free",
        NonCombatRewardMode.RemoveOne => "removeOne",
        _ => "mixed",
    };

    /// <summary>Case-insensitive; tolerates surrounding spaces and an underscore or
    /// hyphen inside removeOne.</summary>
    public static bool TryParse(string? s, out NonCombatRewardMode mode) {
        mode = Default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        switch (s.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant()) {
            case "free": mode = NonCombatRewardMode.Free; return true;
            case "removeone": mode = NonCombatRewardMode.RemoveOne; return true;
            case "mixed": mode = NonCombatRewardMode.Mixed; return true;
            default: return false;
        }
    }

    /// <summary>The old boolean's meaning: On (true) was "combat only" = Free.</summary>
    public static NonCombatRewardMode FromLegacyCombatOnly(bool combatCardVotesOnly) =>
        combatCardVotesOnly ? NonCombatRewardMode.Free : NonCombatRewardMode.Mixed;
}
```

- [ ] **Step 4: Change the record and the parser**

In `src/Game/Bootstrap/ModSettings.cs` replace line 25 `bool CombatCardVotesOnly = false,` with:

```csharp
    NonCombatRewardMode NonCombatCardRewards = NonCombatRewardMode.Mixed,
```

Replace the parser block (lines 269-277, the comment plus the `combatCardVotesOnly` reads) with:

```csharp
            // Three-way mode from v0.4.0 (Surfinite 2026-09-13). The old combatCardVotesOnly
            // bool still parses when the new key is absent: true -> free, false -> mixed.
            NonCombatRewardMode nonCombatCardRewards = NonCombatRewardModes.Default;
            if (root.TryGetProperty(NonCombatRewardModes.Key, out var modeProp)) {
                if (modeProp.ValueKind == JsonValueKind.String && NonCombatRewardModes.TryParse(modeProp.GetString(), out var parsedMode)) {
                    nonCombatCardRewards = parsedMode;
                } else {
                    warnings.Add($"{NonCombatRewardModes.Key} is not one of free|removeOne|mixed; using default (mixed)");
                }
            } else if (root.TryGetProperty("combatCardVotesOnly", out var legacyProp)
                       && legacyProp.ValueKind is JsonValueKind.True or JsonValueKind.False) {
                nonCombatCardRewards = NonCombatRewardModes.FromLegacyCombatOnly(legacyProp.ValueKind == JsonValueKind.True);
                warnings.Add($"combatCardVotesOnly migrated to {NonCombatRewardModes.Key}={NonCombatRewardModes.ToJson(nonCombatCardRewards)}; the old key is dropped on the next settings write");
            }
```

In the `new ChatSettings(...)` call at line 306 replace `combatCardVotesOnly` with `nonCombatCardRewards`.

- [ ] **Step 5: Writer, bootstrap, example**

`src/Game/Ui/Settings/SettingsWriter.cs` line 39: replace `json["combatCardVotesOnly"] = settings.CombatCardVotesOnly;` with:

```csharp
        json[NonCombatRewardModes.Key] = NonCombatRewardModes.ToJson(settings.NonCombatCardRewards);
        json.Remove("combatCardVotesOnly");   // legacy key, migrated on load; dropped here
```

`src/Game/Bootstrap/SettingsBootstrap.cs` line 62: replace `["combatCardVotesOnly"]  = false,` with `["nonCombatCardRewards"] = "mixed",`.

In `AddMissingKeys` (line 84) add the migration before the template loop:

```csharp
    internal static IReadOnlyList<string> AddMissingKeys(JsonObject json) {
        var added = new List<string>();
        // Legacy migration: derive the three-way mode from the old bool, then drop the bool,
        // so the template default below never overrides a user's "combat only" choice.
        if (!json.ContainsKey(NonCombatRewardModes.Key)
                && json.TryGetPropertyValue("combatCardVotesOnly", out var legacy)
                && legacy is JsonValue lv && lv.TryGetValue<bool>(out var combatOnly)) {
            json[NonCombatRewardModes.Key] = NonCombatRewardModes.ToJson(NonCombatRewardModes.FromLegacyCombatOnly(combatOnly));
            json.Remove("combatCardVotesOnly");
            added.Add(NonCombatRewardModes.Key);
        }
        foreach (var (key, defaultValue) in BuildTemplate()) {
```

`src/slay_the_streamer_2.json.example` line 17: replace `"combatCardVotesOnly": false,` with `"nonCombatCardRewards": "mixed",`.

- [ ] **Step 6: Minimal compile fixes for the two consumers (Tasks 2 and 3 replace these)**

`src/Game/DecisionVotes/RewardAuthority.cs` line 15: replace with

```csharp
    private static bool CombatOnly => (ModSettings.Current?.NonCombatCardRewards ?? NonCombatRewardMode.Mixed) == NonCombatRewardMode.Free;
```

`src/Game/Ui/Settings/SettingsPanelBuilder.cs` lines 183-184: replace with

```csharp
        AddCheckboxRow(root, "Card-reward votes only occur after combat", current.NonCombatCardRewards == NonCombatRewardMode.Free,
            value => debouncer.MarkDirtyAndRestart(ModSettings.Current! with { NonCombatCardRewards = NonCombatRewardModes.FromLegacyCombatOnly(value) }));
```

Also update the `MakeSettings` helper in `tests/Game/Ui/Settings/SettingsWriterTests.cs` only if it names `CombatCardVotesOnly` (it does not; it uses positional args up to `ShowVoteTag`). Grep once: `grep -rn CombatCardVotesOnly src tests` must return nothing after this step.

- [ ] **Step 7: Run the whole suite**

Run: `cd tests && dotnet test --nologo -v q`
Expected: all pass (about 605).

- [ ] **Step 8: Commit**

```bash
git add src/Game/Bootstrap src/Game/Ui/Settings/SettingsWriter.cs src/Game/Ui/Settings/SettingsPanelBuilder.cs src/Game/DecisionVotes/RewardAuthority.cs src/slay_the_streamer_2.json.example tests/Bootstrap tests/Game/Ui/Settings/SettingsWriterTests.cs
git commit -m "reward-modes/1: nonCombatCardRewards three-way setting (free|removeOne|mixed, default mixed) with combatCardVotesOnly migration on load, ensure-file and write"
```

---

### Task 2: Settings panel dropdown

**Files:**
- Modify: `src/Game/Ui/Settings/SettingsPanelBuilder.cs` (lines 183-185; new method next to `AddCardSkipsDropdown` at line 292)

**Interfaces:**
- Consumes: `NonCombatRewardMode`, `NonCombatRewardModes` (Task 1), `ChatSettings.NonCombatCardRewards`.

No unit test: the panel is Godot-only. Verified in game (Task 7 matrix row M1).

- [ ] **Step 1: Replace the checkbox with a dropdown row**

Replace lines 183-185 (the `AddCheckboxRow(... "Card-reward votes only occur after combat" ...)` call and its `AddHelpText`) with:

```csharp
        AddNonCombatRewardsDropdown(root, current, debouncer);
        AddHelpText(root, "Free: chat votes only after combat; the streamer picks the rest, Skip allowed.\nRemove-one: chat gets a remove-one style vote on all non-combat card rewards.\nMixed: cards from Ancients use a remove-one vote. Events are unskippable with no voting.");
```

- [ ] **Step 2: Add the dropdown method**

Insert after `AddCardSkipsDropdown` (after line 345):

```csharp
    private static void AddNonCombatRewardsDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row   = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        // Short label on purpose: the longer "Card rewards not from combat" wrapped
        // beside the dropdown (Surfinite, 2026-09-13).
        inner.AddChild(MakeRowLabel("Non-combat card rewards"));

        var dropdown = new OptionButton {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(150, 0),
        };
        if (_kreonRegular != null) dropdown.AddThemeFontOverride("font", _kreonRegular);
        dropdown.AddThemeFontSizeOverride("font_size", 22);
        dropdown.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        (string Label, NonCombatRewardMode Value)[] entries = [
            ("Free", NonCombatRewardMode.Free),
            ("Remove-one", NonCombatRewardMode.RemoveOne),
            ("Mixed", NonCombatRewardMode.Mixed),
        ];
        int selectedIdx = 0;
        for (int i = 0; i < entries.Length; i++) {
            dropdown.AddItem(entries[i].Label);
            dropdown.SetItemMetadata(i, (int)entries[i].Value);
            if (entries[i].Value == current.NonCombatCardRewards) selectedIdx = i;
        }
        dropdown.Selected = selectedIdx;

        var popup = dropdown.GetPopup();
        if (_kreonRegular != null) popup.AddThemeFontOverride("font", _kreonRegular);
        popup.AddThemeFontSizeOverride("font_size", 22);

        dropdown.ItemSelected += idx => {
            var value = (NonCombatRewardMode)dropdown.GetItemMetadata((int)idx).AsInt32();
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { NonCombatCardRewards = value });
        };

        inner.AddChild(dropdown);
        parent.AddChild(row);
    }
```

- [ ] **Step 3: Build the mod project**

Run: `cd src && dotnet build --nologo -v q`
Expected: `Build succeeded.` with no new warnings.

- [ ] **Step 4: Commit**

```bash
git add src/Game/Ui/Settings/SettingsPanelBuilder.cs
git commit -m "reward-modes/2: settings panel dropdown 'Non-combat card rewards' (Free / Remove-one / Mixed) with one-line help per mode"
```

---

### Task 3: Mode-aware classifier rules

**Files:**
- Modify: `src/Game/DecisionVotes/AuthorityMode.cs` (whole `AuthorityRules` class, lines 13-29)
- Modify: `src/Game/DecisionVotes/RewardAuthority.cs` (lines 15-18, 34)
- Modify: `src/Game/DecisionVotes/RelicOriginTags.cs:27-28` (the `Learn` call)
- Test: `tests/Game/DecisionVotes/AuthorityRulesTests.cs`

**Interfaces:**
- Produces: `AuthorityRules.Resolve(RewardOrigin o, bool combatTagRegistered, NonCombatRewardMode mode)`; `AuthorityRules.RulesActive(bool combatTagRegistered, NonCombatRewardMode mode)`; `RewardAuthority.Mode` (internal static `NonCombatRewardMode`).
- `RewardOrigin` unchanged in this task (Task 5 adds `DraftTagged`).

- [ ] **Step 1: Rewrite the rules tests**

Replace the body of `tests/Game/DecisionVotes/AuthorityRulesTests.cs` with:

```csharp
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class AuthorityRulesTests {
    private static RewardOrigin Combat => new(CombatTagged: true, RelicTagged: false, RelicAncient: false, RestSiteTagged: false);
    private static RewardOrigin AncientRelic => new(false, true, true, false);
    private static RewardOrigin ShopRelic => new(false, true, false, false);
    private static RewardOrigin RestSite => new(false, false, false, true);
    private static RewardOrigin Untagged => new(false, false, false, false);

    [Theory]
    [InlineData(NonCombatRewardMode.Free)]
    [InlineData(NonCombatRewardMode.RemoveOne)]
    [InlineData(NonCombatRewardMode.Mixed)]
    public void CombatTagUnregistered_EverythingIsNormalVote(NonCombatRewardMode mode) {
        foreach (var o in new[] { Combat, AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(o, combatTagRegistered: false, mode));
    }

    [Fact]
    public void Free_CombatVotes_EverythingElseFree() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.Free));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.Free, AuthorityRules.Resolve(o, true, NonCombatRewardMode.Free));
    }

    [Fact]
    public void Mixed_TableApplies() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(AncientRelic, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(ShopRelic, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(RestSite, true, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(Untagged, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RemoveOne_EveryNonCombatRewardIsRemoveOne() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, NonCombatRewardMode.RemoveOne));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(o, true, NonCombatRewardMode.RemoveOne));
    }

    [Fact]
    public void CombatTagWinsOverRelicTag() {
        var both = new RewardOrigin(true, true, true, false);
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(both, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RelicTagWinsOverRestSiteTag() {
        var both = new RewardOrigin(false, true, false, true);
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(both, true, NonCombatRewardMode.Mixed));
    }

    [Fact]
    public void RulesActive_OnlyWhenRegisteredAndNotFree() {
        Assert.True(AuthorityRules.RulesActive(true, NonCombatRewardMode.Mixed));
        Assert.True(AuthorityRules.RulesActive(true, NonCombatRewardMode.RemoveOne));
        Assert.False(AuthorityRules.RulesActive(true, NonCombatRewardMode.Free));
        Assert.False(AuthorityRules.RulesActive(false, NonCombatRewardMode.Mixed));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd tests && dotnet test --nologo -v q --filter "FullyQualifiedName~AuthorityRulesTests"`
Expected: compile error (no `Resolve` overload taking `NonCombatRewardMode`).

- [ ] **Step 3: Implement the rules**

Replace the `AuthorityRules` class in `src/Game/DecisionVotes/AuthorityMode.cs` with (add `using SlayTheStreamer2.Game.Bootstrap;` at the top of the file):

```csharp
public static class AuthorityRules {
    /// <summary>Spec section 2 evaluation order, parameterised by the three-way mode
    /// (handoff 2026-09-13 section 5). An unregistered combat tag (the game's default
    /// branch) means the classifier cannot tell events from combat, so everything stays
    /// a normal vote. Free = the old "combat only" checkbox On. RemoveOne turns every
    /// non-combat reward into a removal vote. Mixed is the spec table.</summary>
    public static AuthorityMode Resolve(RewardOrigin o, bool combatTagRegistered, NonCombatRewardMode mode) {
        if (!combatTagRegistered) return AuthorityMode.NormalVote;
        if (o.CombatTagged) return AuthorityMode.NormalVote;
        if (mode == NonCombatRewardMode.Free) return AuthorityMode.Free;
        if (o.RelicTagged) return o.RelicAncient || mode == NonCombatRewardMode.RemoveOne ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
        if (o.RestSiteTagged) return AuthorityMode.RemoveOne;
        return mode == NonCombatRewardMode.RemoveOne ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
    }

    /// <summary>True when the per-origin rules (and their explanation text) are live.</summary>
    public static bool RulesActive(bool combatTagRegistered, NonCombatRewardMode mode) =>
        combatTagRegistered && mode != NonCombatRewardMode.Free;
}
```

- [ ] **Step 4: Wire RewardAuthority and the relic learner**

In `src/Game/DecisionVotes/RewardAuthority.cs` replace lines 15-18 with:

```csharp
    internal static NonCombatRewardMode Mode => ModSettings.Current?.NonCombatCardRewards ?? NonCombatRewardModes.Default;

    /// <summary>True when the per-origin rules and their explanation text apply.</summary>
    internal static bool RulesActive => AuthorityRules.RulesActive(CombatTagRegistered, Mode);
```

and line 34 `return AuthorityRules.Resolve(origin, CombatTagRegistered, CombatOnly);` with `return AuthorityRules.Resolve(origin, CombatTagRegistered, Mode);`. Delete the `CombatOnly` property.

In `src/Game/DecisionVotes/RelicOriginTags.cs` replace lines 27-28 (the `if (RewardAuthority.RulesActive) LocTextPatch.Registry.Learn(...)` line) with:

```csharp
                // Learn with the classifier's own verdict so removeOne mode records RemoveOne
                // for shop relics; the tag is already in place, so Classify sees it.
                if (RewardAuthority.RulesActive)
                    LocTextPatch.Registry.Learn(relic.Id.Entry, RewardAuthority.Classify(instance));
```

- [ ] **Step 5: Run the whole suite and build**

Run: `cd tests && dotnet test --nologo -v q` then `cd ../src && dotnet build --nologo -v q`
Expected: all pass; build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/AuthorityMode.cs src/Game/DecisionVotes/RewardAuthority.cs src/Game/DecisionVotes/RelicOriginTags.cs tests/Game/DecisionVotes/AuthorityRulesTests.cs
git commit -m "reward-modes/3: AuthorityRules take the three-way mode (removeOne: shop relics and events become removal votes); relics learned with the classifier's verdict"
```

---

### Task 4: Mode-aware explanation text

**Files:**
- Modify: `src/Game/DecisionVotes/AuthorityLoc.cs`
- Modify: `src/Game/DecisionVotes/LocTextPatch.cs:39`
- Test: `tests/Game/DecisionVotes/AuthorityLocTests.cs`, `tests/Game/Content/SealedNeowLocTests.cs:52-57` (call-site update only)

**Interfaces:**
- Produces: `AuthorityLoc.SuffixFor(string table, string key, RelicTextRegistry registry, NonCombatRewardMode mode = NonCombatRewardMode.Mixed)`.
- Text (handoff section 5.2), all after the blue lead:
  - RemoveOne mode, twelve event keys: `chat votes to remove one option from the card rewards.` (Brain Leech and Future of Potions singular: `chat votes to remove one option from the card reward.`)
  - RemoveOne mode, Crystal Sphere keys: `chat votes to remove one of the cards uncovered here.`
  - RemoveOne mode, ORRERY: `chat votes to remove one option from each of the five card rewards.`; STRONGBOX: `chat votes to remove one option from each of the two card rewards.`
  - RemoveOne mode, learned relics: the existing `GenericRemoveOne` regardless of the learned mode.
  - Mixed mode: unchanged text. Free mode: never called (RulesActive is false).

- [ ] **Step 1: Add the failing tests**

Append to `tests/Game/DecisionVotes/AuthorityLocTests.cs` (inside the class), and add `using SlayTheStreamer2.Game.Bootstrap;` at the top:

```csharp
    [Fact]
    public void RemoveOneMode_EventsBecomeRemovalText() {
        var reg = Registry();
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from the card rewards.",
            AuthorityLoc.SuffixFor("events", "TRIAL.pages.NONDESCRIPT.options.GUILTY.description", reg, NonCombatRewardMode.RemoveOne));
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from the card reward.",
            AuthorityLoc.SuffixFor("events", "BRAIN_LEECH.pages.INITIAL.options.RIP.description", reg, NonCombatRewardMode.RemoveOne));
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one of the cards uncovered here.",
            AuthorityLoc.SuffixFor("events", "CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE.description", reg, NonCombatRewardMode.RemoveOne));
        Assert.Equal("\n" + AuthorityLoc.Lead + "chat votes to remove one of the cards uncovered here.",
            AuthorityLoc.SuffixFor("events", "CRYSTAL_SPHERE.minigame.instructions.description", reg, NonCombatRewardMode.RemoveOne));
    }

    [Fact]
    public void RemoveOneMode_ShopRelicsBecomeRemovalText() {
        var reg = Registry();
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from each of the five card rewards.",
            AuthorityLoc.SuffixFor("relics", "ORRERY.description", reg, NonCombatRewardMode.RemoveOne));
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from each of the two card rewards.",
            AuthorityLoc.SuffixFor("relics", "STRONGBOX.eventDescription", reg, NonCombatRewardMode.RemoveOne));
        // Ancient relics read the same in both modes.
        Assert.Equal(AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.description", reg),
            AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.description", reg, NonCombatRewardMode.RemoveOne));
    }

    [Fact]
    public void RemoveOneMode_LearnedRelicAlwaysGetsRemovalText() {
        var reg = Registry();
        reg.Learn("SOME_SHOP_RELIC", AuthorityMode.Unskippable);
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from this relic's card rewards.",
            AuthorityLoc.SuffixFor("relics", "SOME_SHOP_RELIC.eventDescription", reg, NonCombatRewardMode.RemoveOne));
    }

    [Fact]
    public void MixedMode_IsTheDefaultAndUnchanged() {
        var reg = Registry();
        Assert.Equal(AuthorityLoc.SuffixFor("relics", "ORRERY.description", reg),
            AuthorityLoc.SuffixFor("relics", "ORRERY.description", reg, NonCombatRewardMode.Mixed));
        Assert.Equal(AuthorityLoc.Lead + "you must take a card from each of the five rewards.",
            AuthorityLoc.SuffixFor("relics", "ORRERY.description", reg));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd tests && dotnet test --nologo -v q --filter "FullyQualifiedName~AuthorityLocTests"`
Expected: compile error (no 4-argument `SuffixFor`).

- [ ] **Step 3: Implement**

In `src/Game/DecisionVotes/AuthorityLoc.cs` add `using SlayTheStreamer2.Game.Bootstrap;`, then add these tables after the `Events` dictionary:

```csharp
    // removeOne mode variants (handoff 2026-09-13 section 5.2): the unskippable surfaces
    // become removal votes, so their lines say so. Keys absent here keep the Mixed text.
    private const string RemoveOneEvent = "chat votes to remove one option from the card rewards.";
    private const string RemoveOneEventSingle = "chat votes to remove one option from the card reward.";
    private const string RemoveOneCrystalSphere = "chat votes to remove one of the cards uncovered here.";

    private static readonly Dictionary<string, string> RemoveOneRelics = new(StringComparer.Ordinal) {
        ["ORRERY"] = "chat votes to remove one option from each of the five card rewards.",
        ["STRONGBOX"] = "chat votes to remove one option from each of the two card rewards.",
    };

    private static readonly Dictionary<string, string> RemoveOneEvents = new(StringComparer.Ordinal) {
        ["CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE.description"] = RemoveOneCrystalSphere,
        ["CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN.description"] = RemoveOneCrystalSphere,
        ["CRYSTAL_SPHERE.minigame.instructions.description"] = RemoveOneCrystalSphere,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.NECROBINDER.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.IRONCLAD.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.REGENT.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.SILENT.description"] = RemoveOneEvent,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.DEFECT.description"] = RemoveOneEvent,
        ["THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.description"] = RemoveOneEventSingle,
        ["BRAIN_LEECH.pages.INITIAL.options.RIP.description"] = RemoveOneEventSingle,
        ["TRIAL.pages.NONDESCRIPT.options.GUILTY.description"] = RemoveOneEvent,
    };
```

Replace `SuffixFor` with:

```csharp
    /// <summary>The suffix for (table, key) under <paramref name="mode"/>, or null when the
    /// key carries no text. Free mode never reaches here (RulesActive is false).</summary>
    public static string? SuffixFor(string table, string key, RelicTextRegistry registry, NonCombatRewardMode mode = NonCombatRewardMode.Mixed) {
        bool removeOne = mode == NonCombatRewardMode.RemoveOne;
        switch (table) {
            case "relics":
                if (RelicExtraKeys.TryGetValue(key, out var extra)) return Lead + extra;
                int dot = key.LastIndexOf('.');
                if (dot <= 0) return null;
                string id = key.Substring(0, dot), field = key.Substring(dot + 1);
                if (field is not ("description" or "eventDescription")) return null;
                if (removeOne && RemoveOneRelics.TryGetValue(id, out var r1)) return Lead + r1;
                if (Relics.TryGetValue(id, out var line)) return Lead + line;
                if (registry.TryGetLearnedMode(id, out var learned))
                    return Lead + (removeOne || learned == AuthorityMode.RemoveOne ? GenericRemoveOne : GenericUnskippable);
                return null;
            case "events":
                string? ev = null;
                if (removeOne) RemoveOneEvents.TryGetValue(key, out ev);
                if (ev is null && !Events.TryGetValue(key, out ev)) return null;
                // The minigame instructions separate every sentence with a blank line.
                return key.EndsWith("minigame.instructions.description", StringComparison.Ordinal) ? "\n" + Lead + ev : Lead + ev;
            default:
                return null;
        }
    }
```

In `src/Game/DecisionVotes/LocTextPatch.cs` line 39 replace `AuthorityLoc.SuffixFor(____name, key, Registry)` with `AuthorityLoc.SuffixFor(____name, key, Registry, RewardAuthority.Mode)`.

- [ ] **Step 4: Run the whole suite and build**

Run: `cd tests && dotnet test --nologo -v q` then `cd ../src && dotnet build --nologo -v q`
Expected: all pass (the `SealedNeowLocTests.ReplaceKeys_DoNotCollideWithAuthorityLocCatalogue` test still compiles because the new parameter has a default); build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/AuthorityLoc.cs src/Game/DecisionVotes/LocTextPatch.cs tests/Game/DecisionVotes/AuthorityLocTests.cs
git commit -m "reward-modes/4: mode-aware explanation text (removeOne variants for the twelve event keys, Crystal Sphere, Orrery, Strongbox and learned relics)"
```

---

### Task 5: Draft origin tag (streamer picks in every mode)

**Files:**
- Create: `src/Game/DecisionVotes/DraftOriginTags.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add `<Compile Remove="..\src\Game\DecisionVotes\DraftOriginTags.cs" />` beside line 48)
- Modify: `src/Game/DecisionVotes/AuthorityMode.cs` (`RewardOrigin` + `Resolve`)
- Modify: `src/Game/DecisionVotes/RewardAuthority.cs:28-33` (fill the new field)
- Test: `tests/Game/DecisionVotes/AuthorityRulesTests.cs`

**Interfaces:**
- Produces: `RewardOrigin(bool CombatTagged, bool RelicTagged, bool RelicAncient, bool RestSiteTagged, bool DraftTagged = false)`; `DraftOriginTags.IsTagged(CardReward)`; log tag `[card-scope] tagged Draft card reward`.
- `Resolve`: `DraftTagged` (without `CombatTagged`) returns `AuthorityMode.Free` in every mode.

- [ ] **Step 1: Add the failing rules tests**

Append to `tests/Game/DecisionVotes/AuthorityRulesTests.cs`:

```csharp
    private static RewardOrigin DraftPick => new(false, false, false, false, DraftTagged: true);

    [Theory]
    [InlineData(NonCombatRewardMode.Free)]
    [InlineData(NonCombatRewardMode.RemoveOne)]
    [InlineData(NonCombatRewardMode.Mixed)]
    public void DraftPick_IsFreeInEveryMode(NonCombatRewardMode mode) =>
        Assert.Equal(AuthorityMode.Free, AuthorityRules.Resolve(DraftPick, true, mode));

    [Fact]
    public void DraftPick_StillNormalVoteWhenCombatTagUnregistered() =>
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(DraftPick, false, NonCombatRewardMode.Mixed));
```

- [ ] **Step 2: Run to verify failure**

Run: `cd tests && dotnet test --nologo -v q --filter "FullyQualifiedName~DraftPick"`
Expected: compile error (`DraftTagged` not a parameter).

- [ ] **Step 3: Extend the origin and the rules**

In `src/Game/DecisionVotes/AuthorityMode.cs` change the record to:

```csharp
/// <summary>The tag facts one CardReward carries. Game code fills this from the
/// origin-tag tables; the rules never see a game type. DraftTagged marks the ten
/// picks of the Draft modifier (streamer-only in every mode, like Sealed Deck).</summary>
public readonly record struct RewardOrigin(bool CombatTagged, bool RelicTagged, bool RelicAncient, bool RestSiteTagged, bool DraftTagged = false);
```

and in `Resolve`, insert after the `o.CombatTagged` line:

```csharp
        if (o.DraftTagged) return AuthorityMode.Free;   // Draft: the streamer drafts, chat never votes (handoff 2026-09-13)
```

In `src/Game/DecisionVotes/RewardAuthority.cs` add `DraftTagged: DraftOriginTags.IsTagged(reward)` as the last argument of the `new RewardOrigin(...)` at lines 29-33.

- [ ] **Step 4: Create the Harmony tag**

`src/Game/DecisionVotes/DraftOriginTags.cs`:

```csharp
// src/Game/DecisionVotes/DraftOriginTags.cs
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Tags the ten CardRewards the Draft modifier constructs so the classifier
/// returns Free for them in every mode (the streamer drafts, chat never votes; same
/// rationale as Sealed Deck). Draft.OfferRewards builds each reward after an await, so
/// a push/pop around the method would only catch the first: instead the Func&lt;Task&gt;
/// that GenerateNeowOption returns is wrapped, and the flag stays up until that task
/// completes. Every branch fails open (untagged = whatever the mode says).</summary>
internal static class DraftOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();
    private static int _active;

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    private static void TagIfDrafting(CardReward instance) {
        try {
            if (Volatile.Read(ref _active) == 0) return;
            Tags.AddOrUpdate(instance, Marker);
            TiLog.Info("[SlayTheStreamer2][card-scope] tagged Draft card reward");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft tag failed", ex); }
    }

    [HarmonyPatch(typeof(Draft), nameof(Draft.GenerateNeowOption))]
    internal static class GenerateNeowOptionPatch {
        static void Postfix(ref Func<Task> __result) {
            try {
                var inner = __result;
                if (inner is null) return;
                __result = async () => {
                    Interlocked.Increment(ref _active);
                    try { await inner(); }
                    finally { Interlocked.Decrement(ref _active); }
                };
                TiLog.Info("[SlayTheStreamer2][card-scope] Draft option wrapped; its picks are streamer-only");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] Draft wrap failed; picks follow the mode", ex); }
        }
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(CardCreationOptions), typeof(int), typeof(Player), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorAPatch {
        static void Postfix(CardReward __instance) => TagIfDrafting(__instance);
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player), typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorBPatch {
        static void Postfix(CardReward __instance) => TagIfDrafting(__instance);
    }
}
```

Check the two `CardReward` constructor signatures against `RelicOriginTags.cs` lines 58-68 (they must be identical; copy from there if the game build differs). Check `using` namespaces compile: `Draft` is in `MegaCrit.Sts2.Core.Models.Modifiers`; `CardCreationOptions`/`CardCreationSource`/`CardModel` are wherever `RelicOriginTags.cs` imports them from (copy its `using` block if the build complains).

Add to `tests/slay_the_streamer_2.tests.csproj` next to line 48:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\DraftOriginTags.cs" />
```

- [ ] **Step 5: Run suite and build**

Run: `cd tests && dotnet test --nologo -v q` then `cd ../src && dotnet build --nologo -v q`
Expected: all pass; build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/DraftOriginTags.cs src/Game/DecisionVotes/AuthorityMode.cs src/Game/DecisionVotes/RewardAuthority.cs tests/slay_the_streamer_2.tests.csproj tests/Game/DecisionVotes/AuthorityRulesTests.cs
git commit -m "reward-modes/5: Draft origin tag (wrap Draft.GenerateNeowOption's task); Draft picks are Free in every mode"
```

---

### Task 6: Override counter on the Ancient vote screen

**Files:**
- Create: `src/Game/DecisionVotes/BudgetCounterText.cs` (Godot-free)
- Modify: `src/Game/Ui/StreamerBudgetCounterLabel.cs` (`UpdateText` at line 67-77, `SetOverrideText` at line 79-83)
- Modify: `src/Game/Ui/AncientVotePopup.cs` (fields at 49-51, `Show` at 85-130, `_Process` placement at 205-225, `ApplyTitleTheme`)
- Test: `tests/Game/DecisionVotes/BudgetCounterTextTests.cs`

**Interfaces:**
- Produces: `BudgetCounterText.Skips(string streamer, int remaining)` and `BudgetCounterText.Overrides(string streamer, int remaining)` returning the exact BBCode `StreamerBudgetCounterLabel` renders today (`[center]{streamer} has [b][color=#87CEEB]{n} card skip(s)[/color][/b] remaining this act[/center]` and the gold `#EFC851` override twin).

- [ ] **Step 1: Write the failing test**

`tests/Game/DecisionVotes/BudgetCounterTextTests.cs`:

```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class BudgetCounterTextTests {
    [Fact]
    public void Overrides_Singular() =>
        Assert.Equal("[center]Surfinite has [b][color=#EFC851]1 vote override[/color][/b] remaining this act[/center]",
            BudgetCounterText.Overrides("Surfinite", 1));

    [Fact]
    public void Overrides_PluralAndZero() {
        Assert.Contains("2 vote overrides", BudgetCounterText.Overrides("Surfinite", 2));
        Assert.Contains("0 vote overrides", BudgetCounterText.Overrides("Surfinite", 0));
    }

    [Fact]
    public void Skips_Singular() =>
        Assert.Equal("[center]Surfinite has [b][color=#87CEEB]1 card skip[/color][/b] remaining this act[/center]",
            BudgetCounterText.Skips("Surfinite", 1));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd tests && dotnet test --nologo -v q --filter "FullyQualifiedName~BudgetCounterTextTests"`
Expected: compile error.

- [ ] **Step 3: Create the helper and use it in the label**

`src/Game/DecisionVotes/BudgetCounterText.cs`:

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>BBCode for the streamer budget counters (card skips, vote overrides), shared
/// by the card-reward screen label and the Ancient vote popup. Godot-free.</summary>
public static class BudgetCounterText {
    // Vanilla compendium rarity colours: Uncommon cyan-blue and Rare yellow-gold.
    public const string SkipAccentHex = "#87CEEB";
    public const string OverrideAccentHex = "#EFC851";

    public static string Skips(string streamer, int remaining) =>
        Line(streamer, remaining, remaining == 1 ? "card skip" : "card skips", SkipAccentHex);

    public static string Overrides(string streamer, int remaining) =>
        Line(streamer, remaining, remaining == 1 ? "vote override" : "vote overrides", OverrideAccentHex);

    private static string Line(string streamer, int remaining, string noun, string hex) =>
        $"[center]{streamer} has [b][color={hex}]{remaining} {noun}[/color][/b] remaining this act[/center]";
}
```

In `src/Game/Ui/StreamerBudgetCounterLabel.cs`: delete the two `const string ...AccentHex` lines (28-29); in `UpdateText` replace the `noun`/`streamerName`/`Text = ...` three lines with `Text = BudgetCounterText.Skips(ModSettings.GetStreamerDisplayName(), snap.RemainingThisAct);`; make `SetOverrideText` body `Text = BudgetCounterText.Overrides(ModSettings.GetStreamerDisplayName(), snap.RemainingThisAct);`.

- [ ] **Step 4: Add the line to the Ancient popup**

In `src/Game/Ui/AncientVotePopup.cs`:

Add fields after `_timerLabel` (line 51):

```csharp
    private RichTextLabel? _overrideLabel;
    private int _cachedOverrideRemaining = int.MinValue;
    // Above the title (title sits 120 px above the dialogue box). Tune in place.
    private const float OverrideGapAboveDialogue = 195f;
    private const int OverrideFontSize = 26;
```

In `Show()`, after the timer label is added (after line 129):

```csharp
        // Override budget line (Surfinite, 2026-09-13): the Ancient screen showed nothing
        // about overrides, so the streamer could not tell one was spendable. Hidden when the
        // override feature is off (limit 0) or unlimited (-1), like the card-screen label.
        if (VoteOverrideBudget.Snapshot().LimitThisAct > 0) {
            _overrideLabel = new RichTextLabel {
                Name = "OverrideBudget",
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            var bodyFont = ResourceLoader.Load<Font>(FontPath);
            var boldFont = ResourceLoader.Load<Font>(TitleFontPath) ?? bodyFont;
            if (bodyFont is not null) _overrideLabel.AddThemeFontOverride("normal_font", bodyFont);
            if (boldFont is not null) _overrideLabel.AddThemeFontOverride("bold_font", boldFont);
            _overrideLabel.AddThemeFontSizeOverride("normal_font_size", OverrideFontSize);
            _overrideLabel.AddThemeFontSizeOverride("bold_font_size", OverrideFontSize);
            _overrideLabel.AddThemeColorOverride("default_color", BodyTextColor);
            _canvasLayer.AddChild(_overrideLabel);
        }
```

Add `using SlayTheStreamer2.Game.Bootstrap;` and `using SlayTheStreamer2.Game.DecisionVotes;` to the file's usings if absent.

In `_Process`, inside the `if (_dialogueAnchor is not null && ...)` block after the timer placement:

```csharp
            if (_overrideLabel is not null) {
                PlaceLabel(_overrideLabel,
                    new Vector2(centerX, dPos.Y - OverrideGapAboveDialogue),
                    halfWidth: 320f, halfHeight: 28f);
            }
```

At the end of `_Process` (after the tally refresh), add:

```csharp
        if (_overrideLabel is not null) {
            int remaining = VoteOverrideBudget.Remaining;
            if (remaining != _cachedOverrideRemaining) {
                _cachedOverrideRemaining = remaining;
                _overrideLabel.Text = BudgetCounterText.Overrides(ModSettings.GetStreamerDisplayName(), remaining);
            }
        }
```

Note: the existing early-return `if (secondsLeft == _cachedSecondsLeft && tallyVersion == _cachedTallyVersion) return;` sits before the tally refresh. Place the override block BEFORE that early return so an override spent by the streamer (which closes the vote) is not the only trigger; the label must refresh at least once on the first frame. `PlaceLabel` already takes `Control` in this popup? Check its signature at the bottom of the file; if it is `Label`, change the parameter type to `Control` (it only sets anchors and offsets).

- [ ] **Step 5: Run suite and build**

Run: `cd tests && dotnet test --nologo -v q` then `cd ../src && dotnet build --nologo -v q`
Expected: all pass; build succeeded.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/BudgetCounterText.cs src/Game/Ui/StreamerBudgetCounterLabel.cs src/Game/Ui/AncientVotePopup.cs tests/Game/DecisionVotes/BudgetCounterTextTests.cs
git commit -m "reward-modes/6: vote-override budget line on the Ancient vote popup; shared BudgetCounterText"
```

---

### Task 7: Docs, notes, matrix, CLAUDE.md

**Files:**
- Modify: `README.md` (lines 123, 131, 159)
- Modify: `notes/06-followups-and-deferred.md` (append a section)
- Modify: `notes/14-remove-one-matrix.md` (append rows)
- Modify: `CLAUDE.md` (commit prefix list; watchlist)
- Modify: `notes/handoff-2026-09-13-remove-one-testing-and-next.md` (mark section 5 done)

- [ ] **Step 1: README**

Line 123 (Card rewards row): replace the last sentence `A setting restores the old "only combat rewards vote" behaviour.` with `The **Non-combat card rewards** setting picks between this (Mixed, the default), a remove-one vote on every non-combat reward (Remove-one), or free streamer picks (Free).`

Line 131 (Draft bullet): replace with:

```markdown
- **Draft** — the run starts with 10 sequential pick-1-of-3 screens. **You** draft; chat does not vote on these picks in any mode (the same rule as Sealed Deck). Chat votes on every card reward after the run begins.
```

Line 159: replace the whole `Card-reward votes only occur after combat` bullet with:

```markdown
- **Non-combat card rewards** *(since v0.4.0; replaces the old "Card-reward votes only occur after combat" checkbox, which migrates automatically)* — **Mixed** (default): Ancient-relic and Dream Catcher card rewards get a remove-one vote; event and shop-relic card rewards cannot be skipped and have no vote. **Remove-one**: every non-combat card reward gets the remove-one vote (Orrery becomes five removal votes, Colorful Philosophers three). **Free**: chat only votes on combat card rewards; everything else is a free streamer pick. A blue "Slay the Streamer:" line on the relics and events explains the active rule. *(Beta branch only: the default branch lacks the hook this needs; there every card reward votes as a normal pick.)*
```

- [ ] **Step 2: Matrix rows**

Append to `notes/14-remove-one-matrix.md` before the `## sealed-neow rows` heading:

```markdown
## reward-modes rows (v0.4.0)

Evidence anchors: `Draft option wrapped` and `tagged Draft card reward` : `src/Game/DecisionVotes/DraftOriginTags.cs`; `combatCardVotesOnly migrated` : `src/Game/Bootstrap/ModSettings.cs`.

| # | Row | Recipe | Evidence | Result |
|---|---|---|---|---|
| M1 | Settings panel shows "Non-combat card rewards" dropdown (Free / Remove-one / Mixed) with three help lines; changing it writes `nonCombatCardRewards` and the file no longer has `combatCardVotesOnly` | open Mods > Slay the Streamer 2 | file content | [ ] |
| M2 | Migration: a file with `combatCardVotesOnly: true` and no new key loads as Free (log `combatCardVotesOnly migrated`), and the panel shows Free | edit the file, restart | log line | [ ] |
| M3 | Remove-one mode: Brain Leech Rip gives a removal vote (Skip is #0); Orrery purchase gives five removal votes; text on the event option and relic hover reads the removal wording | mode = removeOne; Brain Leech; buy Orrery | `removal vote opened` per screen | [ ] |
| M4 | Free mode: Kaleidoscope reward is a free pick, no text on the relic | mode = free; `relic KALEIDOSCOPE` | `Free card reward - no pick vote` | [ ] |
| M5 | Draft modifier: ten picks, no vote in any mode, Skip absent (vanilla), no status line | Custom run with Draft, each mode | `Draft option wrapped`, `tagged Draft card reward` x10 | [ ] |
| M6 | Ancient vote shows "{streamer} has N vote overrides remaining this act" above the title; decrements after an override; hidden when overrides are 0/unlimited in settings | any Ancient | visual | [ ] |
```

- [ ] **Step 3: notes/06, handoff, CLAUDE.md**

Append to `notes/06-followups-and-deferred.md`:

```markdown
## Reward modes (reward-modes/, 2026-09-13)

Handoff section 5 shipped: `nonCombatCardRewards` (`free` | `removeOne` | `mixed`, default `mixed`) replaces `combatCardVotesOnly` (migrated on load and on ensure-file; dropped on write). `AuthorityRules.Resolve` takes the mode; `removeOne` turns shop-relic and untagged (event) rewards into removal votes with mode-aware text in `AuthorityLoc`. Draft picks carry `DraftOriginTags` (wrapping the `Func<Task>` from `Draft.GenerateNeowOption`, because the ten rewards are constructed after awaits) and classify Free in every mode. The Ancient vote popup shows the override budget line.

Follow-ups deferred:
- The learned-relic file stores the mode at learn time; in removeOne mode the text ignores it (always removal wording), so a later switch back to Mixed shows the learned mode again. Acceptable.
- Pacing note for Tristan: removeOne makes Orrery five votes and Colorful Philosophers three.
```

In the handoff file, change the section 5 heading to `## 5. Agreed next work (SHIPPED as reward-modes/1..8, 2026-09-13)`.

In `CLAUDE.md` add to the commit conventions list after the sealed-neow line:

```markdown
- Reward modes (three-way non-combat setting, Draft tag, Ancient override counter): `reward-modes/N:`
```

and to the remove-one watchlist paragraph append: `; Draft.GenerateNeowOption returning a Func<Task> and OfferRewards constructing CardReward per iteration (DraftOriginTags)`.

- [ ] **Step 4: Commit**

```bash
git add README.md notes/06-followups-and-deferred.md notes/14-remove-one-matrix.md notes/handoff-2026-09-13-remove-one-testing-and-next.md CLAUDE.md
git commit -m "reward-modes/7: README, matrix rows M1-M6, notes/06, handoff status, CLAUDE.md prefix and watchlist"
```

---

### Task 8: Release prep v0.4.0 (build + install; publish only after operator validation)

**Files:**
- Modify: `src/slay_the_streamer_2.json` (`"version": "0.3.1"` -> `"0.4.0"`)
- Modify: `workshop/workshop.json` (`changeNote`)
- Modify: `README.md` (add the Beta pin callout under the title if absent: `> 🎮 **Tested against Slay the Spire 2 Beta `v0.111.0`.**`)

- [ ] **Step 1: Bump the manifest and the change note**

Set `"version": "0.4.0"` in `src/slay_the_streamer_2.json`. Replace the `changeNote` in `workshop/workshop.json` with:

```
v0.4.0: non-combat card rewards get new rules. Card rewards from Ancient relics (Kaleidoscope, Glass Eye, Lost Coffer, Hefty Tablet, Lead Paperweight, Neow's Bones) and Dream Catcher now use a remove-one vote: chat votes which option to remove, the streamer picks from the rest, and an override lets the streamer take the removed one. Card rewards from events and shop relics (Orrery) cannot be skipped. A new three-way setting (Free / Remove-one / Mixed) replaces the old combat-only checkbox; Remove-one puts a removal vote on every non-combat reward. Draft picks are always the streamer's. Sealed Deck runs: Neow's Talisman upgrades 2 random cards and makes them Doomed; Leafy Poultice and Precarious Shears are never offered. Vote-override budget now shows on the Ancient screen. Tested against game Beta v0.111.0.
```

If the README lacks the `> 🎮 **Tested against ...` callout near the top, add it after the intro paragraph (line 6).

- [ ] **Step 2: Commit, build, install**

```bash
git add src/slay_the_streamer_2.json workshop/workshop.json README.md
git commit -m "release/v0.4.0: manifest bump, workshop changeNote, README Beta pin"
pwsh -File build.ps1
pwsh -File install.ps1
```

Expected: `Passed!` in the build output; install prints `Done.` (if it says the DLL is in use, close the game and rerun install).

- [ ] **Step 3: Hand over for operator validation**

Rows M1-M6 in `notes/14-remove-one-matrix.md` must pass in game before anything is published. Do NOT run these until Surfinite says the rows are green (outward-facing):

```powershell
Compress-Archive -Path dist\slay_the_streamer_2 -DestinationPath dist\slay_the_streamer_2-v0.4.0.zip
git push origin main
gh release create v0.4.0 --title v0.4.0 --notes-file <notes file> dist\slay_the_streamer_2-v0.4.0.zip
pwsh -File workshop\upload.ps1
```

Release notes: keep every prior section and add a "🆕 New in v0.4.0" section (memory `release_and_game_update_workflow`).

---

## Self-review

- Spec coverage: handoff 5.1 (setting + migration + panel + help text) = Tasks 1-2; 5.2 (removeOne classifier + mode-aware text) = Tasks 3-4; 5.3 (Draft tag + README sentence) = Tasks 5, 7; 5.4 (docs, notes, CLAUDE.md, release) = Tasks 7-8; the 2026-09-13 addition (Ancient override counter) = Task 6.
- Placeholder scan: none.
- Type consistency: `NonCombatRewardMode` / `NonCombatRewardModes` (Task 1) used by Tasks 2-4; `RewardOrigin.DraftTagged` (Task 5) matches `RewardAuthority`'s call; `BudgetCounterText.Overrides/Skips` (Task 6) match both call sites; `AuthorityLoc.SuffixFor` 4th parameter defaulted so untouched callers compile.
