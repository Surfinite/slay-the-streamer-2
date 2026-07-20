# Bossy Relics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Relic rewards (treasure chests + elite combat) offer N relics (setting `RelicChoices` 1–4, default 1); the streamer claims exactly one, the rest return to the back of the relic pool.

**Architecture:** Two Harmony patch files in a new `src/Game/Rewards/` folder reuse vanilla systems — the dormant `LinkedRewardSet` (chain-link UI) for elite rewards, and an append into `TreasureRoomRelicSynchronizer`'s 1–4-relic chest flow. A pure-logic `RelicChoicePlanner` holds unit-testable decisions; `RelicReturnHelper` re-inserts unclaimed relics (canonical models, back of deque, both grab bags). Spec: `docs/superpowers/specs/2026-07-20-bossy-relics-design.md`.

**Tech Stack:** C# / .NET 9, HarmonyLib 2.4, Godot 4.5 (mod side), xUnit (tests project is `Microsoft.NET.Sdk` — no Godot/game types).

## Global Constraints

- `src/Ti/**` must not be touched (TI/Game seam; this is all `src/Game/*`).
- New game-type-referencing files need `<Compile Remove>` in `tests/slay_the_streamer_2.tests.csproj` (the `..\src\Game\DecisionVotes\**` glob exists; a new `src/Game/Rewards/**` folder is NOT globbed into tests — only `RelicChoicePlanner` gets an explicit `<Compile Include>`).
- Every Harmony postfix body wrapped in try/catch; on exception log with `[bossy-relics]` prefix via `TiLog` and leave vanilla behavior.
- `RelicChoices == 1` → construct nothing; vanilla must be byte-identical.
- Never add rewards to `CombatRoom.ExtraRewards` (serialized → save-brick).
- Both features gate on true single-player: `player.RunState.Players.Count == 1`.
- Re-inserted relics must be **canonical** models (`ModelDb.GetByIdOrNull<RelicModel>(id)`), never the reward's `ToMutable()` copy.
- No vanilla RNG stream may be consumed by mod rolls — mod `Rng` only, created via `SeedCompat.CreateRng` (branch-portable ctor).
- Commit prefix `bossy-relics/N:`; build+test via `pwsh -File build.ps1` (never `dotnet build` alone); deploy via `pwsh -File install.ps1`.

---

### Task 1: `RelicChoicePlanner` (pure logic) + tests

**Files:**
- Create: `src/Game/Rewards/RelicChoicePlanner.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add explicit Compile Include)
- Test: `tests/Rewards/RelicChoicePlannerTests.cs` (create folder)

**Interfaces:**
- Consumes: nothing (System only — MUST NOT reference Godot or MegaCrit types; it is compiled into the test project).
- Produces (used by Tasks 4–6):
  - `static int Clamp(int requested)` → 1..4
  - `static int ExtraCount(int relicChoices, int existingCount, int maxTotal)` → how many extras to add (≥0)
  - `static uint OfferSeed(string? runSeed, string surfaceSalt, int actIndex, int floor)` → deterministic per-offer seed (FNV-1a)

- [ ] **Step 1: Write the failing tests**

Create `tests/Rewards/RelicChoicePlannerTests.cs`:

```csharp
using SlayTheStreamer2.Game.Rewards;
using Xunit;

namespace SlayTheStreamer2.Tests.Rewards;

public class RelicChoicePlannerTests {
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(9, 4)]
    public void Clamp_bounds_to_1_through_4(int requested, int expected) =>
        Assert.Equal(expected, RelicChoicePlanner.Clamp(requested));

    [Theory]
    [InlineData(1, 1, 4, 0)]   // N=1: never add extras
    [InlineData(4, 1, 4, 3)]   // chest with 1 vanilla relic, N=4 -> 3 extras
    [InlineData(4, 0, 4, 0)]   // empty chest: leave alone
    [InlineData(4, 2, 4, 2)]   // already 2 present (defensive): cap total at 4
    [InlineData(2, 1, 4, 1)]
    [InlineData(4, 4, 4, 0)]   // already at cap
    public void ExtraCount_respects_cap_and_existing(int choices, int existing, int cap, int expected) =>
        Assert.Equal(expected, RelicChoicePlanner.ExtraCount(choices, existing, cap));

    [Fact]
    public void OfferSeed_is_deterministic_and_context_sensitive() {
        var a = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 12);
        var b = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 12);
        var c = RelicChoicePlanner.OfferSeed("SEED123", "bossy-chest", 1, 12);
        var d = RelicChoicePlanner.OfferSeed("SEED123", "bossy-elite", 1, 13);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void OfferSeed_tolerates_null_run_seed() {
        // Pre-run / weird states: must not throw; still deterministic.
        Assert.Equal(
            RelicChoicePlanner.OfferSeed(null, "bossy-chest", 0, 0),
            RelicChoicePlanner.OfferSeed(null, "bossy-chest", 0, 0));
    }
}
```

Add to `tests/slay_the_streamer_2.tests.csproj`, next to the existing `<Compile Include>` items for mod sources (same ItemGroup that includes `..\src\Game\Bootstrap\**\*.cs`):

```xml
    <Compile Include="..\src\Game\Rewards\RelicChoicePlanner.cs" Link="ModSources\Game\Rewards\RelicChoicePlanner.cs" />
```

(Match the `Link` style of neighboring includes if they differ — copy whatever pattern the existing surgical include for `PortraitFit.cs` uses.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter RelicChoicePlanner`
Expected: FAIL — `RelicChoicePlanner` does not exist (compile error CS0246).

- [ ] **Step 3: Write the implementation**

Create `src/Game/Rewards/RelicChoicePlanner.cs`:

```csharp
using System;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Pure decision logic for the Bossy Relics feature (pick 1 of N relic rewards).
/// System-only on purpose: compiled into the test project, so no Godot or
/// MegaCrit types may appear here.
/// </summary>
public static class RelicChoicePlanner {
    public const int MaxChoices = 4;   // chest UI ships exactly 4 relic holders

    public static int Clamp(int requested) => Math.Clamp(requested, 1, MaxChoices);

    /// <summary>
    /// How many extra relics to add so the offer reaches Clamp(relicChoices),
    /// never exceeding maxTotal and never turning a 0-relic context (empty
    /// chest, no relic reward) into a non-empty one.
    /// </summary>
    public static int ExtraCount(int relicChoices, int existingCount, int maxTotal) {
        if (existingCount <= 0) return 0;
        int target = Math.Min(Clamp(relicChoices), maxTotal);
        return Math.Max(0, target - existingCount);
    }

    /// <summary>
    /// Deterministic per-offer seed (FNV-1a 32-bit) from run seed + surface +
    /// act/floor, so a save-quit-regenerated offer re-rolls identical rarities
    /// without any stream-position tracking.
    /// </summary>
    public static uint OfferSeed(string? runSeed, string surfaceSalt, int actIndex, int floor) {
        unchecked {
            const uint prime = 16777619u;
            uint hash = 2166136261u;
            foreach (char c in runSeed ?? "no-seed") { hash ^= c; hash *= prime; }
            foreach (char c in surfaceSalt)          { hash ^= c; hash *= prime; }
            hash ^= (uint)actIndex; hash *= prime;
            hash ^= (uint)floor;    hash *= prime;
            return hash;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter RelicChoicePlanner`
Expected: PASS (all listed tests).

- [ ] **Step 5: Commit**

```powershell
git add src/Game/Rewards/RelicChoicePlanner.cs tests/Rewards/RelicChoicePlannerTests.cs tests/slay_the_streamer_2.tests.csproj
git commit -m @'
bossy-relics/1: RelicChoicePlanner pure logic + tests

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 2: `RelicChoices` setting — model, bootstrap, writer (+ `AllowSameBossTwice` whitelist fix)

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs` (record at :10-21, Load parse block near :222-227, Success construction at :230-232)
- Modify: `src/Game/Bootstrap/SettingsBootstrap.cs` (`BuildTemplate()` at :46-59)
- Modify: `src/Game/Ui/Settings/SettingsWriter.cs` (whitelist at :28-34)
- Modify: `src/slay_the_streamer_2.json.example`
- Test: extend `tests/Bootstrap/ModSettingsTests.cs`, `tests/Bootstrap/SettingsBootstrapTests.cs`, `tests/Settings/SettingsWriterTests.cs` (locate exact filenames with `Glob tests/**/*Tests.cs`; add to the file that already tests the corresponding class)

**Interfaces:**
- Consumes: existing `ChatSettings` record, `RelicChoicePlanner.Clamp` is NOT used here (settings layer clamps inline like `voteDurationSeconds` does — keeps Bootstrap free of the Rewards namespace).
- Produces: `ChatSettings.RelicChoices` (int, default 1) — read by Tasks 5–6 as `ModSettings.Current?.RelicChoices ?? 1`.

- [ ] **Step 1: Write the failing tests**

In the existing ModSettings test file add (adapt the file's established helper for writing a temp settings JSON — reuse whatever pattern its `voteDurationSeconds` clamp tests use):

```csharp
[Theory]
[InlineData("\"relicChoices\": 3,", 3, false)]
[InlineData("\"relicChoices\": 1,", 1, false)]
[InlineData("\"relicChoices\": 0,", 1, true)]    // below min -> clamp + warning
[InlineData("\"relicChoices\": 9,", 4, true)]    // above max -> clamp + warning
[InlineData("\"relicChoices\": \"two\",", 1, true)] // non-int -> default + warning
[InlineData("", 1, false)]                        // missing -> default, no warning
public void RelicChoices_parses_clamps_and_defaults(string fragment, int expected, bool expectWarning) {
    var result = LoadWithFragment(fragment);   // existing helper name may differ — reuse it
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(expected, success.Settings.RelicChoices);
    Assert.Equal(expectWarning, success.Warnings.Any(w => w.Contains("relicChoices")));
}
```

In the SettingsBootstrap test file:

```csharp
[Fact]
public void Template_contains_relicChoices_default_1() {
    var json = System.Text.Json.Nodes.JsonNode.Parse(SettingsBootstrap.BuildTemplateJson())!.AsObject();
    Assert.Equal(1, (int)json["relicChoices"]!);
}

[Fact]
public void AddMissingKeys_adds_relicChoices_to_old_files() {
    var json = System.Text.Json.Nodes.JsonNode.Parse("{\"schemaVersion\":1}")!.AsObject();
    var added = SettingsBootstrap.AddMissingKeys(json);
    Assert.Contains("relicChoices", added);
    Assert.Equal(1, (int)json["relicChoices"]!);
}
```

In the SettingsWriter test file (reuse its temp-file pattern):

```csharp
[Fact]
public void Write_persists_relicChoices_and_allowSameBossTwice() {
    var settings = MakeSettings() with { RelicChoices = 3, AllowSameBossTwice = true }; // reuse file's factory helper
    var path = WriteToTemp(settings);                                                  // reuse file's helper
    var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    Assert.Equal(3, (int)json["relicChoices"]!);
    Assert.True((bool)json["allowSameBossTwice"]!);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo --filter "RelicChoices|relicChoices"`
Expected: FAIL — `ChatSettings` has no `RelicChoices` member (CS0117/CS1061).

- [ ] **Step 3: Implement**

`ModSettings.cs` — append to the record (END of parameter list; positional construction at :231 depends on order):

```csharp
public sealed record ChatSettings(
    string Channel,
    ChatCredentials Credentials,
    int CardSkipsPerAct,
    string? YoutubeChannelId,
    bool VoteOnActVariant = true,
    bool ForceL3PopupFallback = false,
    int VoteDurationSeconds = 30,
    bool CardSkipAsVoteOption = true,
    bool ShowVoteTag = false,
    bool VoteTallyOnLeft = false,
    bool AllowSameBossTwice = false,
    int RelicChoices = 1);
```

Add the parse block after the `allowSameBossTwice` block (:222-227), cloning the `voteDurationSeconds` shape:

```csharp
            int relicChoices = 1;
            if (root.TryGetProperty("relicChoices", out var relicChoicesProp)) {
                if (relicChoicesProp.ValueKind != JsonValueKind.Number || !relicChoicesProp.TryGetInt32(out var rawChoices)) {
                    warnings.Add("relicChoices is not an integer; using default (1)");
                } else if (rawChoices < 1) {
                    warnings.Add($"relicChoices {rawChoices} below minimum; clamped to 1");
                    relicChoices = 1;
                } else if (rawChoices > 4) {
                    warnings.Add($"relicChoices {rawChoices} above maximum; clamped to 4");
                    relicChoices = 4;
                } else {
                    relicChoices = rawChoices;
                }
            }
```

Extend the Success construction (:231) with the new final positional arg:

```csharp
                new ChatSettings(normalisedChannel, creds, cardSkipsPerAct, youtubeChannelId, voteOnActVariant, forceL3PopupFallback, voteDurationSeconds, cardSkipAsVoteOption, showVoteTag, voteTallyOnLeft, allowSameBossTwice, relicChoices),
```

`SettingsBootstrap.cs` — add to `BuildTemplate()` after `"allowSameBossTwice"`:

```csharp
        ["relicChoices"]         = 1,
```

`SettingsWriter.cs` — extend the whitelist (:29-34); this also fixes the pre-existing `AllowSameBossTwice` persistence bug:

```csharp
        json["voteDurationSeconds"] = settings.VoteDurationSeconds;
        json["voteOnActVariant"] = settings.VoteOnActVariant;
        json["cardSkipAsVoteOption"] = settings.CardSkipAsVoteOption;
        json["showVoteTag"] = settings.ShowVoteTag;
        json["cardSkipsPerAct"] = settings.CardSkipsPerAct;
        json["voteTallyOnLeft"] = settings.VoteTallyOnLeft;
        json["allowSameBossTwice"] = settings.AllowSameBossTwice;
        json["relicChoices"] = settings.RelicChoices;
```

`src/slay_the_streamer_2.json.example` — add alongside the other optional keys (match file's comma placement):

```json
    "relicChoices": 1,
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests\slay_the_streamer_2.tests.csproj --nologo`
Expected: PASS, including all pre-existing tests (the record change is source-compatible: all callers use named/positional prefixes that still line up).

- [ ] **Step 5: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs src/Game/Bootstrap/SettingsBootstrap.cs src/Game/Ui/Settings/SettingsWriter.cs src/slay_the_streamer_2.json.example tests/
git commit -m @'
bossy-relics/2: RelicChoices setting (1-4, default 1) end-to-end; fix AllowSameBossTwice persistence

The settings-writer whitelist was missing allowSameBossTwice, so its UI
checkbox never persisted - fixed while adding relicChoices to the same
whitelist.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 3: Settings-panel dropdown

**Files:**
- Modify: `src/Game/Ui/Settings/SettingsPanelBuilder.cs` (`Build` at :165-195; new row factory next to `AddCardSkipsDropdown` at :271-324)

**Interfaces:**
- Consumes: `ChatSettings.RelicChoices` (Task 2), existing `MakeRow`/`MakeRowLabel`/`AddHelpText`/`AddDivider`/`_kreonRegular` helpers.
- Produces: UI only.

No unit test possible (Godot types); verified by build + operator validation (Task 7).

- [ ] **Step 1: Add the row factory** (place directly after `AddCardSkipsDropdown`, mirroring its metadata pattern):

```csharp
    private static void AddRelicChoicesDropdown(Container parent, ChatSettings current, SettingsSaveDebouncer debouncer) {
        var row   = MakeRow();
        var inner = row.GetChild<HBoxContainer>(0);

        inner.AddChild(MakeRowLabel("Relic choices"));

        var dropdown = new OptionButton {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(115, 0),
        };
        if (_kreonRegular != null) dropdown.AddThemeFontOverride("font", _kreonRegular);
        dropdown.AddThemeFontSizeOverride("font_size", 22);
        dropdown.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        // Metadata (not item-ids) mirrors AddCardSkipsDropdown: id=-1 collides
        // with Godot's auto-assign sentinel.
        (string Label, int Value)[] entries = [
            ("1  (vanilla)", 1),
            ("2", 2),
            ("3", 3),
            ("4", 4)
        ];
        int selectedIdx = 0;
        for (int i = 0; i < entries.Length; i++) {
            dropdown.AddItem(entries[i].Label);
            dropdown.SetItemMetadata(i, entries[i].Value);
            if (entries[i].Value == current.RelicChoices) selectedIdx = i;
        }
        dropdown.Selected = selectedIdx;

        var popup = dropdown.GetPopup();
        if (_kreonRegular != null) popup.AddThemeFontOverride("font", _kreonRegular);
        popup.AddThemeFontSizeOverride("font_size", 22);

        dropdown.ItemSelected += idx => {
            var value = dropdown.GetItemMetadata((int)idx).AsInt32();
            debouncer.MarkDirtyAndRestart(ModSettings.Current! with { RelicChoices = value });
        };

        inner.AddChild(dropdown);
        parent.AddChild(row);
    }
```

- [ ] **Step 2: Wire into `Build`** — insert BEFORE `AddFilePathRow(root)` (:192) so the "Open settings folder" button stays last (Surfinite's explicit preference):

```csharp
        AddDivider(root);
        AddVoteTallySideDropdown(root, current, debouncer);
        AddDivider(root);
        AddRelicChoicesDropdown(root, current, debouncer);
        AddHelpText(root, "Relics offered per chest / elite reward. Streamer picks 1;\nthe rest go back into the relic pool. 1 = vanilla.");
        AddDivider(root);
        AddFilePathRow(root);
```

- [ ] **Step 3: Build**

Run: `pwsh -File build.ps1`
Expected: `Plan B prep build cycle: OK`, 0 errors, all existing tests pass.

- [ ] **Step 4: Commit**

```powershell
git add src/Game/Ui/Settings/SettingsPanelBuilder.cs
git commit -m @'
bossy-relics/3: Relic choices dropdown in settings panel (above folder button)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 4: `RelicReturnHelper` — back-of-deque re-insertion

**Files:**
- Create: `src/Game/Rewards/RelicReturnHelper.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` — NOTHING to add (new folder isn't globbed; only `RelicChoicePlanner.cs` was explicitly included in Task 1). Verify no glob accidentally sweeps `src/Game/Rewards/**` (search the csproj for `Game\Rewards`); if Task 1's include was added as a folder glob by mistake, fix to the single file.

**Interfaces:**
- Consumes: game types `Player`, `RelicModel`, `RelicGrabBag`, `ModelDb`; HarmonyLib `AccessTools`.
- Produces (used by Tasks 5–6):
  - `static void ReturnToPools(Player player, RelicModel relic, bool sharedBagOnly)` — idempotent; canonicalizes; appends to BACK of the rarity deque; also removes from `_mpFallbackDequeue` if vanilla demoted it there. `sharedBagOnly: true` for chest-skip refunds (chest pulls only touched the shared bag).

- [ ] **Step 1: Implement**

Create `src/Game/Rewards/RelicReturnHelper.cs`:

```csharp
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Returns unclaimed Bossy-Relics offers to the relic economy: appends the
/// CANONICAL model to the BACK of its rarity deque (vanilla pulls scan from
/// the front, so "back" = seen again only after the rest of that rarity has
/// cycled — locked decision, spec 2026-07-20). Pulls are destructive to both
/// the player bag and the shared bag (RelicFactory.PullNextRelicFromFront),
/// so restoration mirrors both; chest pulls only touched the shared bag, so
/// chest-skip refunds pass sharedBagOnly: true.
/// </summary>
internal static class RelicReturnHelper {
    // RelicGrabBag internals: Dictionary<RelicRarity, List<RelicModel>> _deques
    // (front = index 0) and List<RelicModel> _mpFallbackDequeue (where vanilla's
    // MoveToFallback demotes chest leftovers).
    private static readonly AccessTools.FieldRef<RelicGrabBag, Dictionary<RelicRarity, List<RelicModel>>> DequesRef =
        AccessTools.FieldRefAccess<RelicGrabBag, Dictionary<RelicRarity, List<RelicModel>>>("_deques");
    private static readonly AccessTools.FieldRef<RelicGrabBag, List<RelicModel>> FallbackRef =
        AccessTools.FieldRefAccess<RelicGrabBag, List<RelicModel>>("_mpFallbackDequeue");

    internal static void ReturnToPools(Player player, RelicModel relic, bool sharedBagOnly) {
        try {
            // Deques hold CANONICAL models; rewards hold ToMutable() copies.
            var canonical = ModelDb.GetByIdOrNull<RelicModel>(relic.Id);
            if (canonical is null) {
                TiLog.Warn($"[bossy-relics] cannot return {relic.Id}: no canonical model");
                return;
            }
            // Never return a relic the player now owns (they claimed it, or own
            // a one-of-a-kind copy) - IsAllowed pruning would drop it anyway,
            // but don't rely on that.
            foreach (var owned in player.Relics) {
                if (owned.Model.Id == canonical.Id) return;
            }
            if (!sharedBagOnly) ReturnToBag(player.RelicGrabBag, canonical);
            ReturnToBag(player.RunState.SharedRelicGrabBag, canonical);
            TiLog.Info($"[bossy-relics] returned {canonical.Id.Entry} to pool (back of {canonical.Rarity} deque{(sharedBagOnly ? ", shared bag only" : "")})");
        } catch (System.Exception ex) {
            TiLog.Error($"[bossy-relics] ReturnToPools failed for {relic?.Id}", ex);
        }
    }

    private static void ReturnToBag(RelicGrabBag bag, RelicModel canonical) {
        // Undo a vanilla MoveToFallback demotion if it beat us to this relic.
        FallbackRef(bag).RemoveAll(r => r.Id == canonical.Id);

        var deques = DequesRef(bag);
        if (!deques.TryGetValue(canonical.Rarity, out var deque)) {
            deque = new List<RelicModel>();
            deques[canonical.Rarity] = deque;
        }
        if (deque.Exists(r => r.Id == canonical.Id)) return;   // idempotence
        deque.Add(canonical);   // back of deque
    }
}
```

NOTE for implementer: `player.Relics` — verify the exact owned-relics accessor on `Player` in the decompile at `C:\Users\SURFIN~1\AppData\Local\Temp\claude\c--Users-Surfinite-slay-the-streamer-2\64b52315-d22d-4f47-bc06-7e67c0c52d4b\scratchpad\decomp-v109\MegaCrit.Sts2.Core.Entities.Players\Player.cs` (grep for `Relics`). If it's e.g. `player.RelicCollection.Relics` or items expose `.Model.Id` differently, adjust the ownership loop accordingly — the intent is "player already owns this relic id → skip".

- [ ] **Step 2: Build**

Run: `pwsh -File build.ps1`
Expected: OK. (Tests project untouched — file not included there.)

- [ ] **Step 3: Commit**

```powershell
git add src/Game/Rewards/RelicReturnHelper.cs
git commit -m @'
bossy-relics/4: RelicReturnHelper - canonical back-of-deque re-insertion

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 5: `EliteRelicChoicePatch` — linked N-relic elite rewards

**Files:**
- Create: `src/Game/Rewards/EliteRelicChoicePatch.cs`

**Interfaces:**
- Consumes: `RelicChoicePlanner` (Task 1), `RelicReturnHelper.ReturnToPools` (Task 4), `SeedCompat.CreateRng(uint)` (existing, `src/Game/DecisionVotes/SeedCompat.cs`), game types per code below.
- Produces: registered via `Harmony.PatchAll` in `ModEntry.Init` automatically (`[HarmonyPatch]` attributes). Static `Registry` internal to this file.

- [ ] **Step 1: Implement**

Create `src/Game/Rewards/EliteRelicChoicePatch.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Bossy Relics, elite surface: when a combat RewardsSet contains the standard
/// single elite RelicReward, replace it with vanilla's (dormant) LinkedRewardSet
/// containing the original + N-1 fixed-rarity extras. Vanilla renders the
/// chain-link UI and enforces claim-one; we refund unclaimed members to the
/// back of the relic deques.
///
/// Patch point is WithRewardsFromRoom (not GenerateRewardsFor): it runs before
/// Populate, and the tutorial path (TryGenerateTutorialRewards) produces
/// PREDETERMINED relic rewards that are already populated - the unpopulated
/// filter below therefore skips tutorials for free.
/// </summary>
[HarmonyPatch(typeof(RewardsSet), nameof(RewardsSet.WithRewardsFromRoom))]
internal static class EliteRelicChoicePatch {
    /// <summary>Members of linked sets THIS mod created, pending refund.
    /// Instance-identity registry; entries removed as refunds fire.</summary>
    internal static readonly HashSet<Reward> Registry = new();
    /// <summary>Wrappers this mod created that still need completion bookkeeping.</summary>
    internal static readonly HashSet<LinkedRewardSet> PendingWrappers = new();

    static void Postfix(RewardsSet __instance, AbstractRoom room) {
        try {
            int choices = RelicChoicePlanner.Clamp(ModSettings.Current?.RelicChoices ?? 1);
            if (choices <= 1) return;
            var player = __instance.Player;
            if (player.RunState.Players.Count != 1) return;

            // Exactly the vanilla elite shape: one rarity-less, unpopulated
            // RelicReward directly in the set (never inside ExtraRewards - those
            // were appended from CombatRoom.ExtraRewards and are serialized).
            var candidates = __instance.Rewards
                .Where(r => r is RelicReward rr && !rr.IsPopulated && rr.Rarity == MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.None)
                .Cast<RelicReward>()
                .ToList();
            if (candidates.Count != 1) return;
            var original = candidates[0];
            if (room is CombatRoom combat && combat.ExtraRewards.TryGetValue(player, out var extras) && extras.Contains(original)) return;

            int extraCount = RelicChoicePlanner.ExtraCount(choices, 1, RelicChoicePlanner.MaxChoices);
            var rng = SeedCompat.CreateRng(RelicChoicePlanner.OfferSeed(
                player.RunState.Rng?.StringSeed, "bossy-elite",
                player.RunState.CurrentActIndex, player.RunState.TotalFloor));

            var members = new List<Reward> { original };
            for (int i = 0; i < extraCount; i++) {
                // Fixed-rarity ctor consumes no vanilla RNG; rarity from OUR rng.
                members.Add(new RelicReward(RelicFactory.RollRarity(rng), player));
            }

            var wrapper = new LinkedRewardSet(members, player);
            int idx = __instance.Rewards.IndexOf(original);
            __instance.Rewards[idx] = wrapper;

            foreach (var m in members) Registry.Add(m);
            PendingWrappers.Add(wrapper);
            TiLog.Info($"[bossy-relics] elite offer expanded to {members.Count} linked relics (act {player.RunState.CurrentActIndex}, floor {player.RunState.TotalFloor})");
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] EliteRelicChoicePatch failed; vanilla rewards unchanged", ex);
        }
    }
}

/// <summary>
/// When a child of OUR linked set is claimed, vanilla calls
/// LinkedRewardSet.RemoveReward(child). Complete the wrapper's bookkeeping
/// (vanilla never sets its SuccessfullySelected via the UI path - dormant-code
/// wart) so RewardsSet completion fires cleanly instead of Log.Error/hang.
/// </summary>
[HarmonyPatch(typeof(LinkedRewardSet), nameof(LinkedRewardSet.RemoveReward))]
internal static class LinkedSetClaimBookkeepingPatch {
    static void Postfix(LinkedRewardSet __instance, Reward reward) {
        try {
            if (!EliteRelicChoicePatch.PendingWrappers.Remove(__instance)) return;
            EliteRelicChoicePatch.Registry.Remove(reward);   // claimed member: no refund
            // Wrapper's own OnSelect trivially returns true; selecting it marks
            // SuccessfullySelected and lets the set complete. Fire-and-forget is
            // safe: no awaiter depends on ordering here (validated by vanilla's
            // own TestMode flow which selects wrappers the same way).
            _ = __instance.Select();
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] linked-set bookkeeping failed", ex);
        }
    }
}

/// <summary>
/// Refund path: NLinkedRewardSet.GetReward calls OnSkipped() on the remaining
/// members after a claim, and skipping the whole screen routes
/// LinkedRewardSet.OnSkipped -> each member's OnSkipped. Either way, a member
/// of OUR set that gets skipped goes back to the pool. Registry.Remove makes
/// the (known) double-OnSkipped harmless.
/// </summary>
[HarmonyPatch(typeof(RelicReward), nameof(RelicReward.OnSkipped))]
internal static class LinkedSetRefundPatch {
    static void Postfix(RelicReward __instance) {
        try {
            if (!EliteRelicChoicePatch.Registry.Remove(__instance)) return;
            if (__instance.Relic is null) return;   // never populated - nothing was pulled
            RelicReturnHelper.ReturnToPools(__instance.Player, __instance.Relic, sharedBagOnly: false);
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] elite refund failed", ex);
        }
    }
}
```

NOTES for implementer (verify against the decompile before building; adjust if reality differs, and say so in the commit message):
1. `Reward.Select()` — the public claim entry-point's exact name/signature on `Reward` (research named `SelectUnsynchronized()`; check `MegaCrit.Sts2.Core.Rewards\Reward.cs` around L84-98 and use whichever public method sets `SuccessfullySelected` WITHOUT re-triggering synchronizer traffic; prefer the unsynchronized one).
2. `Reward.Player` — protected vs public; `RelicReward.OnSkipped` postfix uses `__instance.Player`. If protected, use `Traverse`/`AccessTools.Property(typeof(Reward), "Player")`.
3. `player.RunState.TotalFloor` — confirm property name (seen in `EncounterModel.cs` v109 diff); fall back to `CurrentActIndex` only (seed still deterministic) if absent.
4. `RelicRarity` namespace — the code above uses `MegaCrit.Sts2.Core.Entities.Relics.RelicRarity` (per `RelicReward.cs` usings); confirm.
5. Registry lifetime: entries removed on claim/refund. Stale entries from an abandoned run are inert (instance-identity never matches new objects) — accept; do NOT add run-liveness wiring (YAGNI, and this is not a vote).

- [ ] **Step 2: Build**

Run: `pwsh -File build.ps1`
Expected: OK, 0 errors, all tests pass.

- [ ] **Step 3: Smoke-check patch registration**

Run: `pwsh -File install.ps1`, launch game, check `%APPDATA%\SlayTheSpire2\logs\godot.log` contains the mod's `Harmony patched N method(s)` block now listing `RewardsSet.WithRewardsFromRoom`, `LinkedRewardSet.RemoveReward`, `RelicReward.OnSkipped` (patch count grows from 17 to 20). If dev console available: `unlock all`, start a run, fight the first elite with `relicChoices: 4` set in the settings JSON.

- [ ] **Step 4: Commit**

```powershell
git add src/Game/Rewards/EliteRelicChoicePatch.cs
git commit -m @'
bossy-relics/5: elite relic rewards become pick-1-of-N LinkedRewardSet

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 6: `ChestRelicChoicePatch` — N-relic chests

**Files:**
- Create: `src/Game/Rewards/ChestRelicChoicePatch.cs`

**Interfaces:**
- Consumes: `RelicChoicePlanner`, `RelicReturnHelper`, `SeedCompat.CreateRng`; game types per code below.
- Produces: Harmony patches auto-registered via `PatchAll`.

- [ ] **Step 1: Implement**

Create `src/Game/Rewards/ChestRelicChoicePatch.cs`:

```csharp
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Rewards;

/// <summary>
/// Bossy Relics, chest surface: after vanilla pulls the single-player chest
/// relic, append N-1 extra pulls to the same picking session. The vanilla
/// treasure UI already renders 1-4 relics (SingleplayerRelicHolder +
/// MultiplayerRelicHolder1..4; HARD CAP 4) and its single-player vote
/// degenerate case enforces claim-one. Unclaimed relics are refunded on award
/// (Skipped results) or on full chest skip (which bypasses AwardRelics via
/// _singleplayerSkipped, hence the OnPicked postfix).
///
/// Chest pulls come from the SHARED grab bag only (BeginRelicPicking pulls
/// _sharedGrabBag.PullFromFront directly - the player bag is untouched), so
/// refunds here use sharedBagOnly: true. Vanilla's MoveToFallback demotion of
/// leftover relics in the PLAYER bag is undone by ReturnToPools' fallback
/// scrub + our not-inserting into the player deque (the relic never left it).
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
internal static class ChestRelicChoicePatch {
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<RelicModel>?> CurrentRelicsRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<RelicModel>?>("_currentRelics");
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, RelicGrabBag> SharedBagRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, RelicGrabBag>("_sharedGrabBag");
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, IPlayerCollection> PlayersRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, IPlayerCollection>("_playerCollection");

    /// <summary>Extras (and the original) offered by the current chest session, pending refund.</summary>
    internal static readonly List<RelicModel> SessionOffer = new();

    static void Postfix(TreasureRoomRelicSynchronizer __instance) {
        try {
            SessionOffer.Clear();
            int choices = RelicChoicePlanner.Clamp(ModSettings.Current?.RelicChoices ?? 1);
            if (choices <= 1) return;

            var players = PlayersRef(__instance).Players;
            if (players.Count != 1) return;
            var player = players[0];

            var current = CurrentRelicsRef(__instance);
            // Count == 0: empty chest / ShouldGenerateTreasure false - leave alone.
            // Count > 1: not the shape we expect (future game change) - leave alone.
            if (current is null || current.Count != 1) return;

            // First-ever-chest tutorial forces Gorget solo - keep it solo.
            if (current[0].Id.Entry == "GORGET") return;

            int extraCount = RelicChoicePlanner.ExtraCount(choices, current.Count, RelicChoicePlanner.MaxChoices);
            if (extraCount <= 0) return;

            var rng = SeedCompat.CreateRng(RelicChoicePlanner.OfferSeed(
                player.RunState.Rng?.StringSeed, "bossy-chest",
                player.RunState.CurrentActIndex, player.RunState.TotalFloor));

            var sharedBag = SharedBagRef(__instance);
            for (int i = 0; i < extraCount; i++) {
                // Mirror the vanilla pull shape, rarity on OUR rng (vanilla's
                // chest stream position stays untouched -> the ORIGINAL relic is
                // identical to what vanilla would have offered).
                var rarity = RelicFactory.RollRarity(rng);
                var relic = sharedBag.PullFromFront(rarity, player.RunState) ?? RelicFactory.FallbackRelic;
                current.Add(relic);
            }
            SessionOffer.AddRange(current);
            TiLog.Info($"[bossy-relics] chest offer expanded to {current.Count} relics");
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] ChestRelicChoicePatch failed; vanilla chest unchanged", ex);
        }
    }
}

/// <summary>
/// Refunds. Award path: OnPicked with a vote completes -> AwardRelics raises
/// results; relics nobody received (Skipped, player=null) go back to the shared
/// bag. Skip path: single-player skip sets _singleplayerSkipped and NEVER calls
/// AwardRelics - refund the whole session offer right there. Postfixing
/// OnPicked covers both (it is the common entry), using the session list
/// captured at BeginRelicPicking.
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.OnPicked))]
internal static class ChestRelicRefundPatch {
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<RelicModel>?> CurrentRelicsRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<RelicModel>?>("_currentRelics");
    private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, IPlayerCollection> PlayersRef =
        AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, IPlayerCollection>("_playerCollection");

    static void Postfix(TreasureRoomRelicSynchronizer __instance, Player player, int? index) {
        try {
            if (ChestRelicChoicePatch.SessionOffer.Count == 0) return;
            var players = PlayersRef(__instance).Players;
            if (players.Count != 1) return;

            // After a CLAIM the vanilla flow has already run AwardRelics +
            // EndRelicVoting (session over: _currentRelics is null). After a
            // SKIP, _currentRelics is still set (deferred to room exit).
            bool skipped = !index.HasValue;
            var offered = new List<RelicModel>(ChestRelicChoicePatch.SessionOffer);
            RelicModel? claimed = (!skipped && index!.Value >= 0 && index.Value < offered.Count) ? offered[index.Value] : null;
            ChestRelicChoicePatch.SessionOffer.Clear();

            foreach (var relic in offered) {
                if (claimed != null && relic.Id == claimed.Id) continue;
                RelicReturnHelper.ReturnToPools(player, relic, sharedBagOnly: true);
            }
        } catch (System.Exception ex) {
            TiLog.Error("[bossy-relics] chest refund failed", ex);
        }
    }
}
```

NOTES for implementer (verify against the decompile; adjust + note in commit):
1. `IPlayerCollection` — namespace (`MegaCrit.Sts2.Core.Entities.Players`?) and its `Players` property type; check `TreasureRoomRelicSynchronizer.cs` usings.
2. Gorget detection: confirm `Id.Entry` for `Gorget` is `"GORGET"` (UPPER_SNAKE landmine) — grep the decompiled `Gorget.cs`; safer alternative: `current[0].Id == ModelDb.Relic<Gorget>().Id` with `using MegaCrit.Sts2.Core.Models.Relics;`.
3. Refund ordering vs `NTreasureRoomRelicCollection`'s `MoveToFallback` handler on `RelicsAwarded`: our `OnPicked` postfix runs BEFORE the UI's event handler finishes demoting? `AwardRelics` fires the event synchronously inside `OnPicked` (before our postfix). So demotion already happened → `ReturnToPools`' fallback-scrub handles the player bag, and the shared-bag re-insert is ours alone. If testing shows otherwise, move the refund into a `RelicsAwarded` subscription made in the `BeginRelicPicking` postfix instead.
4. Skip path: vanilla defers `EndRelicVoting` to room exit; our immediate shared-bag refund is safe because the session's `_currentRelics` list is never re-read for pulls. Verify no double-refund on room exit (no vanilla code touches the bags there).

- [ ] **Step 2: Build**

Run: `pwsh -File build.ps1`
Expected: OK, all tests pass.

- [ ] **Step 3: Commit**

```powershell
git add src/Game/Rewards/ChestRelicChoicePatch.cs
git commit -m @'
bossy-relics/6: chests offer pick-1-of-N via vanilla relic-picking session

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

---

### Task 7: Docs, deploy, operator validation

**Files:**
- Modify: `CLAUDE.md` (commit-conventions list), `README.md` (settings table / description of the new setting)

**Interfaces:** none — documentation + the human validation gate.

- [ ] **Step 1: CLAUDE.md** — add to the commit-conventions list:

```markdown
- Bossy Relics (pick-1-of-N relic rewards): `bossy-relics/N:`
```

- [ ] **Step 2: README** — add a row/bullet where the other settings are documented:

```markdown
- **`relicChoices`** (1–4, default 1) — relics offered per treasure chest and elite kill. The streamer picks one; the rest go back into the relic pool (back of the queue). 1 = vanilla. Not a chat vote — this one's for the streamer.
```

- [ ] **Step 3: Build + deploy**

```powershell
pwsh -File build.ps1
pwsh -File install.ps1
```
Expected: OK; 417+ tests pass (new planner/settings tests included).

- [ ] **Step 4: Commit**

```powershell
git add CLAUDE.md README.md
git commit -m @'
bossy-relics/7: document relicChoices setting + commit prefix

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
'@
```

- [ ] **Step 5: Operator validation (Surfinite, in-game)** — from spec §5:

1. Elite, `relicChoices` 2/3/4: chain icons between rows; claim removes group; no completion `Log.Error` in godot.log; Proceed works on relic-only screen; declined relics reappear much later.
2. Chest, 2/3/4: layout sane; claim awards one; ESC/skip refunds all; first-ever chest (fresh profile) still solo Gorget.
3. Save-quit mid-rewards-screen and mid-chest → Continue: same relics re-offered; no dupes. Remove mod → reload: clean vanilla.
4. Controller focus reaches linked rows.
5. `relicChoices` 1: log shows no `[bossy-relics]` lines at all.
6. godot.log: no `rewardIndex=-1` errors after linked-child claim.

Tag when green: `git tag bossy-relics-complete && git push origin bossy-relics-complete`.

---

## Self-review notes (done at plan time)

- Spec coverage: behavior §1→Tasks 5/6; architecture §2→Tasks 4/5/6; settings §3→Tasks 2/3 (incl. AllowSameBossTwice fix and dropdown-above-folder-button); §4 rails→Global Constraints + try/catch in every patch; §5 testing→Tasks 1/2 unit + Task 7 operator checklist; §6 conventions→Task 7.
- All game-API uncertainties are flagged as numbered implementer NOTES with the exact decompile path to check — they are verification steps, not placeholders.
- Type consistency: `RelicChoicePlanner.Clamp/ExtraCount/OfferSeed`, `RelicReturnHelper.ReturnToPools(player, relic, sharedBagOnly)`, `EliteRelicChoicePatch.Registry`/`PendingWrappers`, `ChestRelicChoicePatch.SessionOffer` — names match across Tasks 1/4/5/6.
