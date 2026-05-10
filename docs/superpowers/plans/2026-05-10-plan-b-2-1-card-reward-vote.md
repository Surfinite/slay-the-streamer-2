# Plan B.2.1 Card Reward Vote Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add chat-vote-on-card-reward to the Slay the Streamer 2 mod, with a per-act skip budget that prevents the streamer from silently bypassing chat agency, and an in-game skip-counter label so the streamer always knows where they stand.

**Architecture:** Suspend-and-resume Harmony pattern (copy-paste-modify of B.1's `NeowBlessingVotePatch`) targeting `NCardRewardSelectionScreen.SelectCard`. Skip gate piggybacks on vanilla `NRewardsScreen.DisallowSkipping()` via three postfixes (`_Ready`, `RewardSkippedFrom`, `_ExitTree`). Pure budget logic extracted into `SkipBudgetTracker` for Godot-free unit testing. Skip gate enforces only when card-vote infrastructure is fully available (`ShouldEnforceSkipGate()` activation gate).

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (`0Harmony.dll` shipped with game), xUnit 2.9, `System.Text.Json`. Tests run via `dotnet test`; build assembled via `pwsh -File build.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md`](../specs/2026-05-10-plan-b-2-1-card-reward-vote-design-v4.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Per-task commits**: each task ends in a `git commit` with a `plan-b-2-1/N.M:` prefix. Surfinite has pre-authorised commits to `main` for this work.

---

## Spike-derived corrections (Task 1 desk-research output, 2026-05-10)

The Task 1 spike inspected decompiled source and found five corrections to the v4 spec. **All subsequent tasks MUST use these corrected accessors instead of the spec's originals.** The v4 spec assumed several APIs that don't exist as named; these are the real ones.

| # | Spec said | Use instead | Affects tasks |
|---|---|---|---|
| 1 | `RunState.Id` (assumed `Guid`) | **`runState.Rng.StringSeed`** (string — user's run seed). Pass `runState.Rng.StringSeed` as the string run-id to `SkipBudgetTracker.ObserveRunAndAct(...)` and to the run-id guard. | 6, 8, 9, 13 |
| 2 | `CardCreationResult.Card.Name.GetText()` for receipt labels | **`result.Card.Title`** — public string property; handles upgrade suffix. No method call chain needed. | 8 |
| 3 | `NRewardsScreen._ExitTree` (assumed declared) | **Not declared on NRewardsScreen** — would patch the inherited Godot Control/Node method. Spike runtime verification (Step 1.2) will confirm whether Harmony patches the inherited method. **If it doesn't fire**, Task 13 switches the second postfix target to `NRewardsScreen.AfterOverlayClosed()` (declared at decompiled NRewardsScreen.cs:460; called during overlay teardown before `QueueFreeSafely`). Default for the plan: try `_ExitTree` first per spec; switch on Step 1.2 finding. | 13 |
| 4 | `runState.Acts.Count - 1` for current-act index | **`runState.CurrentActIndex`** (0-based int, exposed on both `RunState` and `IRunState` interface). Cleaner; reflection still wraps it for fail-safe access but the property name is now pinned. | 12 |
| 5a | `NRewardButton` namespace `MegaCrit.Sts2.Core.Nodes` | **`MegaCrit.Sts2.Core.Nodes.Rewards`** | 11, 12 |
| 5b | `NRewardButton.Reward` accessed via reflection (`GetProperty("Reward")`) | **Public property of type `Reward?`** — call directly: `(button as NRewardButton)?.Reward is CardReward`. No `GetProperty` needed. | 12 |

These are documented in `notes/06-followups-and-deferred.md` under "Plan B.2.1 spike findings". Runtime verification of `_ExitTree` patchability and Mode B back-out remains for Steps 1.2 / 1.4 (operator-driven).

---

## File Structure

**New files:**
- `src/Game/DecisionVotes/SkipBudgetTracker.cs` — pure-logic class owning all budget/run-id/act-id state.
- `src/Game/DecisionVotes/CardRewardVotePatch.cs` — Harmony Prefix on `NCardRewardSelectionScreen.SelectCard`.
- `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` — three postfixes on `NRewardsScreen` + `ShouldEnforceSkipGate()` + helpers + skip-receipt formatter.
- `src/Game/Ui/CardSkipCounterLabel.cs` — Godot `RichTextLabel` parented under `NRewardsScreen` near proceed button.
- `tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs` — ~10 pure-logic tests.

**Modified files:**
- `src/Game/Bootstrap/ModSettings.cs` — extend `ChatSettings` record with `int CardSkipsPerAct`; add JSON parsing + clamp logic.
- `src/Ti/Chat/IChatService.cs` — no change (verified to expose `SendMessageAsync(text, priority)` already).
- `src/ModEntry.cs` — add `internal static SettingsResult? Settings { get; private set; }`; assign during init; retro-apply `[SlayTheStreamer2]` log prefix to existing call sites.
- `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` — add hard/soft `Prepare` run-ID guard; retro-apply `[SlayTheStreamer2]` log prefix.
- `tests/slay_the_streamer_2.tests.csproj` — add `Game/DecisionVotes/**/*.cs` to source includes.
- `tests/Bootstrap/ModSettingsTests.cs` — add ~5 tests for `cardSkipsPerAct` parsing.
- `notes/06-followups-and-deferred.md` — add reflected-members section + Task-1 spike findings + B.2.1 outcome at end.
- `README.md` — update status section after B.2.1 ships.

**One-file responsibilities:**
- `SkipBudgetTracker`: budget arithmetic. No Godot, no Harmony, no sts2.dll. Type-independent run-id (`string?`).
- `CardRewardVotePatch`: filter + suspend + resume + fallback for one decision type (cards). Mirrors `NeowBlessingVotePatch` shape.
- `CardRewardSkipGatePatch`: detect rewards-screen lifecycle; delegate budget logic to tracker; render UI; send chat receipts. Owns `_activeLabel` static.
- `CardSkipCounterLabel`: render budget snapshot on a Godot `RichTextLabel`; cleanup-safe.

---

## Phase 0: Verification spike

This phase produces no shipping code. Output is a documented Notes/06 entry pinning every reflected sts2.dll member B.2.1 depends on, plus answers to two open questions. **All subsequent phases assume Task 1's findings are recorded; if any hard `Prepare` requirement turns out to be unmet, escalate before writing the corresponding patch.**

### Task 1: Spike — verify Harmony patchability + pin reflected members

**Files:**
- Modify: `notes/06-followups-and-deferred.md` (add a new section)
- Temporary code (NOT to be committed to main): a throwaway `src/Game/DecisionVotes/SpikePatch.cs` containing no-op postfixes for `NRewardsScreen._Ready` and `_ExitTree` that just `TiLog.Info("[SlayTheStreamer2][spike] _Ready fired");` — used only to verify Harmony's patch behaviour. **Delete before this task's final commit.**

- [ ] **Step 1.1: Build the mod with the temporary spike patch in place**

Add a temporary file `src/Game/DecisionVotes/SpikePatch.cs`:
```csharp
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
internal static class SpikeReadyPatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen._Ready fired");
}

[HarmonyPatch(typeof(NRewardsScreen), "_ExitTree")]
internal static class SpikeExitTreePatch {
    static void Postfix() => TiLog.Info("[SlayTheStreamer2][spike] NRewardsScreen._ExitTree fired");
}
```

Build:
```powershell
pwsh -File build.ps1
```

Install: `pwsh -File install.ps1`. Run StS2; start a new run; defeat the first combat; observe rewards screen. Then exit the rewards screen.

- [ ] **Step 1.2: Confirm both postfixes fire**

Open `%APPDATA%\SlayTheSpire2\logs\godot.log`. Search for `[spike]`. Expected: at least one `_Ready fired` line when the rewards screen appeared, and one `_ExitTree fired` line when it was dismissed.

If `_Ready` doesn't fire: Harmony cannot patch the Godot lifecycle method as written. Try patching `_EnterTree` instead, or `_Notification` (with a check for `Node.NotificationReady`). Pin the working alternative in the next step.

If `_ExitTree` doesn't fire: cleanup-by-static-null is unreliable; document and rely on `IsInstanceValid` checks in all consumer sites. NOT a blocker for B.2.1 — the spec already has the IsInstanceValid fallback.

- [ ] **Step 1.3: Inspect decompiled source to pin reflected member shapes**

Use ILSpy output at `decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/NRewardsScreen.cs`. Pin:

- `_rewardButtons` field — record exact type (probably `List<Control>`).
- `_skippedRewardButtons` field — record exact type.
- `_proceedButton` field — record exact type (`NProceedButton`).
- `DisallowSkipping()` method signature — record `public void DisallowSkipping()`.
- `RewardCollectedFrom(Control)` method signature.
- `RewardSkippedFrom(Control)` method signature.

For `NCardRewardSelectionScreen` at `decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CardSelection/NCardRewardSelectionScreen.cs`:
- `_options` field — type `IReadOnlyList<CardCreationResult>`.
- The card-holder collection — find which field exposes the live `NCardHolder` controls. Likely `_cardRow.GetChildren()` filtered to `NCardHolder`. **Record the exact accessor.**

For `NRewardButton`:
- `Reward` accessor — record property vs field, public vs internal.

For `RunManager` and `RunState`:
- `RunManager.Instance.DebugOnlyGetState()` — verify returns non-null in modded production (start a real run, log the value).
- `RunState.Id` — record exact type. If `Guid`, fine. If `string` or other, the tracker still uses `string?` so just document the conversion.
- `RunState.Players.Count` — verify accessor.
- **Current-act access pattern**: try `runState.Acts.Count - 1`. Verify it changes when the player advances acts (use the DevConsole `act 2` command).

- [ ] **Step 1.4: Verify Mode B back-out path**

In a real run, on a card reward screen: open the card sub-screen, look at the cards, then check if there's any way back without picking. Likely candidates: an "X" button, a Back button, the Escape key, or right-click. **Record the result.**

- If back-out exists: Mode B verification step in the acceptance gate is doable.
- If no back-out exists: Mode B verification is N/A; record as "Mode B is theoretical for v0.1 — vanilla provides no back-out from card sub-screen".

- [ ] **Step 1.5: Update notes/06 with all spike findings**

In `notes/06-followups-and-deferred.md`, add a new section at the top:
```markdown
## Plan B.2.1 spike findings (2026-05-10)

### Harmony patchability of Godot lifecycle methods on NRewardsScreen
- `_Ready` postfix fires: YES / NO (record actual + fallback if no)
- `_ExitTree` postfix fires: YES / NO (record actual)

### Reflected sts2.dll members — B.2.1 dependency surface

CardRewardVotePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen` (type)
- `NCardRewardSelectionScreen.SelectCard(NCardHolder)` (method, public, void)
- `NCardRewardSelectionScreen._options` (field, IReadOnlyList<CardCreationResult>)
- Card-holder collection accessor: `<EXACT_FIELD_OR_METHOD_PINNED>` returning IEnumerable<NCardHolder>
- `MegaCrit.Sts2.Core.Models.CardCreationResult.Card.Name.GetText()` (call chain for receipt labels)
- `MegaCrit.Sts2.Core.Runs.RunManager.Instance` (singleton)
- `RunManager.DebugOnlyGetState()` returns RunState; **verified non-null in modded production: YES / NO**
- `RunState.Id` (type: <PINNED_TYPE>; conversion: `runState.Id.ToString()` for tracker)
- `RunState.Players.Count` for MP bail

CardRewardSkipGatePatch depends on:
- `MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen` (type)
- `NRewardsScreen._Ready()` — Harmony patches: YES / NO (fallback: <ALT_METHOD_NAME>)
- `NRewardsScreen._ExitTree()` — Harmony patches: YES / NO
- `NRewardsScreen.RewardSkippedFrom(Control)` (method, public)
- `NRewardsScreen.DisallowSkipping()` (method, public, parameterless, void)
- `NRewardsScreen._rewardButtons` (field, <PINNED_TYPE>)
- `NRewardsScreen._skippedRewardButtons` (field, <PINNED_TYPE>)
- `NRewardsScreen._proceedButton` (field, <PINNED_TYPE>)
- `MegaCrit.Sts2.Core.Nodes.NRewardButton` (type)
- `NRewardButton.<REWARD_ACCESSOR>` (<property|field>, type Reward)
- `MegaCrit.Sts2.Core.Rewards.CardReward` (type — for CardReward identity check)
- Current-act accessor: `<PINNED_PATTERN>`

NeowBlessingVotePatch (B.1, retro-touched in B.2.1):
- All B.1 reflection (already in NeowBlessingVotePatch.cs)
- `RunManager.Instance.DebugOnlyGetState()?.Id` (NEW in B.2.1 for run-ID guard)

### Vanilla back-out path from NCardRewardSelectionScreen

Result: <YES / NO>. If YES, exact mechanism: <Escape key | Back button | other>.
Implication for acceptance gate Mode B verification: <doable | record as N/A>.
```

- [ ] **Step 1.6: Delete the spike code**

Remove `src/Game/DecisionVotes/SpikePatch.cs`. Rebuild and reinstall to confirm the mod still loads cleanly with no spike postfixes registered.

- [ ] **Step 1.7: Commit**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m @'
plan-b-2-1/1.1: spike — pin reflected sts2.dll members + verify Harmony lifecycle patchability

Verified: _Ready / _ExitTree patchability, all reflected member shapes for
CardRewardVotePatch + CardRewardSkipGatePatch, RunManager.DebugOnlyGetState
non-null in modded production, current-act access pattern, Mode B back-out
path existence. Findings recorded in notes/06 as the dependency-surface
single-update-point.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 1: Plumbing — settings + ModEntry accessor + test wiring

### Task 2: Wire test project to compile `Game/DecisionVotes`

**Files:**
- Modify: `tests/slay_the_streamer_2.tests.csproj:11-16`

- [ ] **Step 2.1: Add `Game/DecisionVotes` to the source-reference list**

Edit the `<ItemGroup>` containing the `<Compile Include>` entries:
```xml
<ItemGroup>
  <Compile Include="..\src\Ti\Internal\**\*.cs" />
  <Compile Include="..\src\Ti\Chat\**\*.cs" />
  <Compile Include="..\src\Ti\Voting\**\*.cs" />
  <Compile Include="..\src\Game\Bootstrap\**\*.cs" />
  <Compile Include="..\src\Game\DecisionVotes\**\*.cs" />
</ItemGroup>
```

- [ ] **Step 2.2: Verify build still succeeds (no Game/DecisionVotes files exist in tests scope yet beyond NeowBlessingVotePatch which references sts2.dll types — but Bootstrap is already there as precedent, and the include resolves the empty/partial scope cleanly when nothing matches)**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: all 183 existing tests pass. If compile fails on `NeowBlessingVotePatch.cs` referencing `MegaCrit.Sts2.*` types: **STOP and escalate** — the existing csproj setup must be sourcing only sts2-free files. Workaround: include only `..\src\Game\DecisionVotes\SkipBudgetTracker.cs` explicitly once it exists (Task 5), and skip this csproj change until then. Document in commit message.

- [ ] **Step 2.3: Commit**

```powershell
git add tests/slay_the_streamer_2.tests.csproj
git commit -m "plan-b-2-1/2.1: wire test project to compile Game/DecisionVotes"
```

---

### Task 3: Extend `ChatSettings` and `ModSettings` with `cardSkipsPerAct`

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs` (extend `ChatSettings` record + JSON parsing)
- Test: `tests/Bootstrap/ModSettingsTests.cs` (add 5 new tests)

- [ ] **Step 3.1: Add `CardSkipsPerAct` field to the `ChatSettings` record**

In `src/Game/Bootstrap/ModSettings.cs`, change line 10:
```csharp
public sealed record ChatSettings(string Channel, ChatCredentials Credentials, int CardSkipsPerAct);
```

- [ ] **Step 3.2: Write the failing test for missing-key default**

In `tests/Bootstrap/ModSettingsTests.cs`, add:
```csharp
[Fact]
public void Load_CardSkipsPerActMissing_UsesDefault() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1,
        ""channel"": ""#foo"",
        ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234""
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(1, success.Settings.CardSkipsPerAct);   // default
}
```

(`WriteTempJson` is an existing test helper; use the same pattern as other ModSettings tests in the file.)

- [ ] **Step 3.3: Run — should fail (compile or runtime — `CardSkipsPerAct` missing or not parsed)**

```powershell
dotnet test --filter "FullyQualifiedName~CardSkipsPerActMissing" --nologo
```

Expected: FAIL — either compile error if record arg missing, or runtime mismatch.

- [ ] **Step 3.4: Implement parsing**

In `ModSettings.Load`, before constructing the `ChatSettings`, add:
```csharp
int cardSkipsPerAct = 1;   // default
if (root.TryGetProperty("cardSkipsPerAct", out var skipsProp)) {
    if (skipsProp.ValueKind != JsonValueKind.Number || !skipsProp.TryGetInt32(out var raw)) {
        warnings.Add("cardSkipsPerAct is not an integer; using default (1)");
    } else if (raw < -1) {
        warnings.Add($"cardSkipsPerAct {raw} clamped to -1 (unlimited)");
        cardSkipsPerAct = -1;
    } else {
        cardSkipsPerAct = raw;
    }
}
```

Then change the `ChatSettings` construction at the end of `Load`:
```csharp
return new SettingsResult.Success(new ChatSettings(normalisedChannel, creds, cardSkipsPerAct), warnings);
```

- [ ] **Step 3.5: Run — should pass**

```powershell
dotnet test --filter "FullyQualifiedName~CardSkipsPerActMissing" --nologo
```

Expected: PASS.

- [ ] **Step 3.6: Add the four remaining tests**

```csharp
[Fact]
public void Load_CardSkipsPerActInvalid_WarnsAndUsesDefault() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1, ""channel"": ""#foo"", ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234"",
        ""cardSkipsPerAct"": ""not a number""
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(1, success.Settings.CardSkipsPerAct);
    Assert.Contains(success.Warnings, w => w.Contains("cardSkipsPerAct"));
}

[Fact]
public void Load_CardSkipsPerActNegativeFive_ClampsToMinusOne() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1, ""channel"": ""#foo"", ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234"",
        ""cardSkipsPerAct"": -5
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(-1, success.Settings.CardSkipsPerAct);
    Assert.Contains(success.Warnings, w => w.Contains("clamped"));
}

[Fact]
public void Load_CardSkipsPerActZero_IsStrict() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1, ""channel"": ""#foo"", ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234"",
        ""cardSkipsPerAct"": 0
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(0, success.Settings.CardSkipsPerAct);
    Assert.DoesNotContain(success.Warnings, w => w.Contains("cardSkipsPerAct"));
}

[Fact]
public void Load_CardSkipsPerActPositive_Parses() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1, ""channel"": ""#foo"", ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234"",
        ""cardSkipsPerAct"": 3
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(3, success.Settings.CardSkipsPerAct);
}

[Fact]
public void Load_CardSkipsPerActMinusOne_IsUnlimited() {
    var path = WriteTempJson(@"{
        ""schemaVersion"": 1, ""channel"": ""#foo"", ""username"": ""bot"",
        ""oauthToken"": ""abcdefghijklmnopqrstuvwxyz1234"",
        ""cardSkipsPerAct"": -1
    }");
    var result = ModSettings.Load(path);
    var success = Assert.IsType<SettingsResult.Success>(result);
    Assert.Equal(-1, success.Settings.CardSkipsPerAct);
    Assert.DoesNotContain(success.Warnings, w => w.Contains("cardSkipsPerAct"));
}
```

- [ ] **Step 3.7: Run all tests**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: 188 tests pass (183 + 5 new). Existing tests that constructed `ChatSettings` directly will fail to compile due to the new required positional arg — **fix all callsites by passing `1` as the `CardSkipsPerAct` arg in test setup**, OR change the new arg to a property with default value (positional arg-with-default isn't supported in records, so the cleaner fix is to find every test constructing `ChatSettings` directly and update). Search:
```powershell
rg "new ChatSettings\(" tests/
```

- [ ] **Step 3.8: Commit**

```powershell
git add src/Game/Bootstrap/ModSettings.cs tests/Bootstrap/ModSettingsTests.cs
git commit -m "plan-b-2-1/3.1: ModSettings — cardSkipsPerAct parsing + 5 tests"
```

---

### Task 4: Add `ModEntry.Settings` static accessor + retro `[SlayTheStreamer2]` log prefix

**Files:**
- Modify: `src/ModEntry.cs`

- [ ] **Step 4.1: Add the static property declaration**

In `src/ModEntry.cs`, around line 27 (with the other `internal static` properties):
```csharp
internal static SettingsResult? Settings { get; private set; }
```

- [ ] **Step 4.2: Assign `Settings` from the load result in the switch block**

Around line 85 (the `switch (settingsResult)` block), set the property at the start of the switch:
```csharp
Settings = settingsResult;
switch (settingsResult) {
    case SettingsResult.Success s:
        // ... existing code ...
```

- [ ] **Step 4.3: Retro-apply `[SlayTheStreamer2]` log prefix to all `Log.Info`/`Log.Warn`/`Log.Error` call sites in ModEntry.cs**

The existing call sites already use `[slay_the_streamer_2]` as a prefix. Per Decision 20 in v4 spec, standardize on `[SlayTheStreamer2]` (PascalCase, matches assembly name). Do a find-and-replace in `src/ModEntry.cs`:
- Replace `[slay_the_streamer_2]` with `[SlayTheStreamer2]` in all string literals (~12 sites).

Verify with:
```powershell
rg "\[slay_the_streamer_2\]" src/ModEntry.cs
```
Expected: zero matches.

```powershell
rg "\[SlayTheStreamer2\]" src/ModEntry.cs
```
Expected: ~12 matches.

- [ ] **Step 4.4: Build and verify**

```powershell
pwsh -File build.ps1
```

Expected: build succeeds. Mod manifest references `slay_the_streamer_2.dll` (lowercase); verify it still loads:
```powershell
pwsh -File install.ps1
```

Then start the game once, observe `%APPDATA%\SlayTheSpire2\logs\godot.log`. Search for `[SlayTheStreamer2]`. Expected: log lines from ModEntry init.

- [ ] **Step 4.5: Commit**

```powershell
git add src/ModEntry.cs
git commit -m "plan-b-2-1/4.1: ModEntry.Settings static accessor + [SlayTheStreamer2] log prefix"
```

---

## Phase 2: SkipBudgetTracker (pure logic)

### Task 5: `SkipBudgetTracker` class + 10 unit tests

**Files:**
- Create: `src/Game/DecisionVotes/SkipBudgetTracker.cs`
- Test: `tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs`

- [ ] **Step 5.1: Write the class skeleton**

Create `src/Game/DecisionVotes/SkipBudgetTracker.cs`:
```csharp
using System;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure budget arithmetic. Owns the per-act skip counter and run/act change
/// detection. Main-thread-only — plain ++/= state, no Interlocked.
/// Run-id is typed as string? to decouple from the actual sts2.dll RunState.Id type.
/// </summary>
internal sealed class SkipBudgetTracker {
    private int _actSkipsUsed;
    private int? _lastSeenActIndex;
    private string? _lastSeenRunId;

    public int ActSkipsUsed => _actSkipsUsed;

    public void ObserveRunAndAct(string? runId, int? actIndex) {
        if (runId != null && runId != _lastSeenRunId) {
            _actSkipsUsed = 0;
            _lastSeenRunId = runId;
            _lastSeenActIndex = actIndex;
            return;
        }
        if (actIndex.HasValue && actIndex != _lastSeenActIndex) {
            _actSkipsUsed = 0;
            _lastSeenActIndex = actIndex;
        }
    }

    public bool IsSkipAllowed(int actLimit) {
        if (actLimit < 0) return true;
        return _actSkipsUsed < actLimit;
    }

    public void RecordSkip() => _actSkipsUsed++;

    public SkipBudgetSnapshot Snapshot(int actLimit) => new(
        UsedThisAct: _actSkipsUsed,
        LimitThisAct: actLimit,
        RemainingThisAct: actLimit < 0 ? int.MaxValue : Math.Max(0, actLimit - _actSkipsUsed));

    internal void ResetForTests() {
        _actSkipsUsed = 0;
        _lastSeenActIndex = null;
        _lastSeenRunId = null;
    }
}

internal readonly record struct SkipBudgetSnapshot(int UsedThisAct, int LimitThisAct, int RemainingThisAct);
```

- [ ] **Step 5.2: Write the failing tests file**

Create `tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs`:
```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class SkipBudgetTrackerTests {
    [Fact]
    public void IsSkipAllowed_LimitZero_AlwaysFalse() {
        var t = new SkipBudgetTracker();
        Assert.False(t.IsSkipAllowed(0));
    }

    [Fact]
    public void IsSkipAllowed_LimitMinusOne_AlwaysTrue() {
        var t = new SkipBudgetTracker();
        Assert.True(t.IsSkipAllowed(-1));
        t.RecordSkip();
        t.RecordSkip();
        t.RecordSkip();
        Assert.True(t.IsSkipAllowed(-1));
    }

    [Fact]
    public void IsSkipAllowed_PositiveLimit_TrueUntilExhausted() {
        var t = new SkipBudgetTracker();
        Assert.True(t.IsSkipAllowed(2));
        t.RecordSkip();
        Assert.True(t.IsSkipAllowed(2));
        t.RecordSkip();
        Assert.False(t.IsSkipAllowed(2));
    }

    [Fact]
    public void RecordSkip_Increments() {
        var t = new SkipBudgetTracker();
        Assert.Equal(0, t.ActSkipsUsed);
        t.RecordSkip();
        Assert.Equal(1, t.ActSkipsUsed);
        t.RecordSkip();
        Assert.Equal(2, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_RunChange_ResetsCounter() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        Assert.Equal(1, t.ActSkipsUsed);
        t.ObserveRunAndAct("run-2", 0);
        Assert.Equal(0, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_ActChangeSameRun_ResetsCounter() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.RecordSkip();
        Assert.Equal(2, t.ActSkipsUsed);
        t.ObserveRunAndAct("run-1", 1);
        Assert.Equal(0, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_IdenticalRunAndAct_DoesNotReset() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.ObserveRunAndAct("run-1", 0);
        Assert.Equal(1, t.ActSkipsUsed);
    }

    [Fact]
    public void ObserveRunAndAct_NullRunId_DoesNotResetByRun() {
        var t = new SkipBudgetTracker();
        t.ObserveRunAndAct("run-1", 0);
        t.RecordSkip();
        t.ObserveRunAndAct(null, 0);   // null run-id (degraded run detection)
        Assert.Equal(1, t.ActSkipsUsed);
    }

    [Fact]
    public void Snapshot_PositiveLimit_ReturnsCorrectRemaining() {
        var t = new SkipBudgetTracker();
        t.RecordSkip();
        var snap = t.Snapshot(3);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(3, snap.LimitThisAct);
        Assert.Equal(2, snap.RemainingThisAct);
    }

    [Fact]
    public void Snapshot_UnlimitedLimit_ReturnsIntMaxRemaining() {
        var t = new SkipBudgetTracker();
        t.RecordSkip();
        var snap = t.Snapshot(-1);
        Assert.Equal(1, snap.UsedThisAct);
        Assert.Equal(-1, snap.LimitThisAct);
        Assert.Equal(int.MaxValue, snap.RemainingThisAct);
    }
}
```

- [ ] **Step 5.3: Run — should pass (we wrote impl + tests together)**

```powershell
dotnet test --filter "FullyQualifiedName~SkipBudgetTrackerTests" --nologo
```

Expected: 10 tests pass.

- [ ] **Step 5.4: Run all tests for regression**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: 198 tests pass (188 + 10 new).

- [ ] **Step 5.5: Commit**

```powershell
git add src/Game/DecisionVotes/SkipBudgetTracker.cs tests/Game/DecisionVotes/SkipBudgetTrackerTests.cs
git commit -m "plan-b-2-1/5.1: SkipBudgetTracker — pure logic + 10 tests"
```

---

## Phase 3: NeowBlessingVotePatch run-ID guard

### Task 6: Add hard/soft `Prepare` run-ID guard to `NeowBlessingVotePatch` + retro log prefix

**Files:**
- Modify: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs`

- [ ] **Step 6.1: Add `RunIdGuardEnabled` flag and capture run-id at vote start**

In `NeowBlessingVotePatch`, add a new internal static flag near the existing flags (line 22):
```csharp
internal static bool RunIdGuardEnabled { get; private set; } = true;
```

Update `Prepare` to perform the soft check. After the existing hard-check block (around line 28-46), add:
```csharp
// Soft check: run-id accessor. Failure logs Warn but does NOT abort patch registration.
try {
    var rm = MegaCrit.Sts2.Core.Runs.RunManager.Instance;
    var stateMethod = rm?.GetType().GetMethod("DebugOnlyGetState");
    if (rm == null || stateMethod == null) {
        TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded — RunManager.Instance.DebugOnlyGetState() not reachable");
        RunIdGuardEnabled = false;
    } else {
        var state = stateMethod.Invoke(rm, null);
        if (state == null) {
            // Could be no-run-in-progress at Prepare time; not necessarily a failure.
            // Defer the check to runtime.
            TiLog.Info("[SlayTheStreamer2][neow-vote] DebugOnlyGetState returned null at Prepare time; will re-check at vote start");
        } else {
            var idProp = state.GetType().GetProperty("Id");
            if (idProp == null) {
                TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded — RunState.Id property not found");
                RunIdGuardEnabled = false;
            }
        }
    }
} catch (Exception ex) {
    TiLog.Warn($"[SlayTheStreamer2][neow-vote] run-ID guard degraded — Prepare soft check threw: {ex.Message}");
    RunIdGuardEnabled = false;
}
```

- [ ] **Step 6.2: Capture `runIdAtStart` in the prefix**

In `Prefix`, after the existing `Voter.Default` resolution and chat-readiness check (around line 67), before the `_voteInProgress` flip:
```csharp
string? runIdAtStart = null;
if (RunIdGuardEnabled) {
    try {
        var state = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState();
        runIdAtStart = state?.Id?.ToString();
        if (runIdAtStart == null) {
            TiLog.Warn("[SlayTheStreamer2][neow-vote] run-ID guard degraded for this vote — null state at start");
        }
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][neow-vote] run-ID guard degraded for this vote — {ex.Message}");
    }
}
```

Pass `runIdAtStart` into `HandleVoteAsync`:
```csharp
_ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, index, runIdAtStart);
```

- [ ] **Step 6.3: Update `HandleVoteAsync` signature and pass through**

Change the signature:
```csharp
private static async Task HandleVoteAsync(VoteCoordinator coordinator, NEventRoom room,
                                          VoteSession session, IReadOnlyList<EventOption> snapshot,
                                          int playerClickIndex, string? runIdAtStart) {
```

Pass `runIdAtStart` into both `ResumeOnMainThread` calls:
```csharp
coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, winnerIndex, playerClickIndex, runIdAtStart));
```
and
```csharp
coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, snapshot, playerClickIndex, playerClickIndex, runIdAtStart));
```

- [ ] **Step 6.4: Update `ResumeOnMainThread` to check the guard**

Change signature:
```csharp
private static void ResumeOnMainThread(NEventRoom room, IReadOnlyList<EventOption> snapshot,
                                       int preferredIndex, int playerClickIndex, string? runIdAtStart) {
```

After the existing `IsInstanceValid` and `IsNeowEvent` checks, before the options bounds check, add:
```csharp
if (runIdAtStart != null) {
    string? currentRunId = null;
    try {
        currentRunId = MegaCrit.Sts2.Core.Runs.RunManager.Instance?.DebugOnlyGetState()?.Id?.ToString();
    } catch { /* swallow — treat as null */ }
    if (currentRunId != runIdAtStart) {
        TiLog.Warn("[SlayTheStreamer2][neow-vote] resume aborted: run changed during vote");
        return;
    }
}
```

(No cancellation chat receipt for Neow — Neow is once-per-run, abandon-mid-vote is rare; matches B.1's silent absorption pattern. Card patch will add the receipt.)

- [ ] **Step 6.5: Retro-apply `[SlayTheStreamer2]` log prefix to all existing TiLog calls in NeowBlessingVotePatch.cs**

Search and replace `[neow-vote]` with `[SlayTheStreamer2][neow-vote]` in `src/Game/DecisionVotes/NeowBlessingVotePatch.cs`.

```powershell
rg "\"\[neow-vote\]" src/Game/DecisionVotes/NeowBlessingVotePatch.cs
```

Expected (after replace): zero matches; all migrated to `[SlayTheStreamer2][neow-vote]`.

- [ ] **Step 6.6: Build and verify**

```powershell
pwsh -File build.ps1
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: build succeeds; all 198 tests pass (Neow patch isn't unit-tested directly — only operator-validated).

- [ ] **Step 6.7: Smoke install — verify Neow vote still works (manual operator check)**

```powershell
pwsh -File install.ps1
```

Start StS2, start a new run, click any Neow blessing → vote should open as before. Cast a chat vote, verify resume applies. Search log for `[SlayTheStreamer2][neow-vote]` — should appear with prefix. **No regression in B.1's Neow vote.**

- [ ] **Step 6.8: Commit**

```powershell
git add src/Game/DecisionVotes/NeowBlessingVotePatch.cs
git commit -m @'
plan-b-2-1/6.1: NeowBlessingVotePatch — run-ID guard + log prefix

Hard/soft Prepare split: vote target shape stays hard (return false on
mismatch); run-id accessor is soft (log Warn + RunIdGuardEnabled = false,
patch still registers without guard). Capture runIdAtStart in Prefix; if
non-null, compare at resume; mismatch → Warn log + drop resume (no chat
receipt for Neow — once-per-run). Retro [SlayTheStreamer2] log prefix.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 4: CardRewardVotePatch (the vote)

Copy-paste-modify of `NeowBlessingVotePatch.cs`. Same patch shape; different target + holder snapshot signature for reroll detection.

### Task 7: Patch scaffold + Prepare hard checks

**Files:**
- Create: `src/Game/DecisionVotes/CardRewardVotePatch.cs`

- [ ] **Step 7.1: Create the file with class scaffold and Prepare hard checks only**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen.SelectCard))]
internal static class CardRewardVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;

    internal static bool PreparedSuccessfully { get; private set; }
    internal static bool RunIdGuardEnabled { get; private set; } = true;
    internal static bool VoteInProgress => _voteInProgress == 1;

    private static readonly Lazy<FieldInfo?> _optionsField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options"));
    // Card-holder collection accessor: PINNED IN TASK 1 SPIKE.
    // Common candidates: AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow")
    //                    then enumerate children filtered to NCardHolder.
    // Replace with the pinned accessor from notes/06.
    private static readonly Lazy<FieldInfo?> _cardHoldersHostField =
        new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow"));

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            // Hard checks at registration time (called once with null original).
            if (_optionsField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] hard check failed: _options field not found");
                PreparedSuccessfully = false;
                return false;
            }
            if (_cardHoldersHostField.Value is null) {
                TiLog.Error("[SlayTheStreamer2][card-vote] hard check failed: card-holder host field not found");
                PreparedSuccessfully = false;
                return false;
            }

            // Soft checks (run-ID guard) — log Warn but do NOT fail Prepare.
            try {
                var rm = RunManager.Instance;
                var stateMethod = rm?.GetType().GetMethod("DebugOnlyGetState");
                if (rm == null || stateMethod == null) {
                    TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded — RunManager.Instance.DebugOnlyGetState() not reachable");
                    RunIdGuardEnabled = false;
                }
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][card-vote] run-ID guard degraded — Prepare soft check threw: {ex.Message}");
                RunIdGuardEnabled = false;
            }

            PreparedSuccessfully = true;
            return true;
        }

        // Per-method check (called once per patched method).
        var parameters = original.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(NCardHolder)) {
            TiLog.Error($"[SlayTheStreamer2][card-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
            PreparedSuccessfully = false;
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][card-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }
}
```

- [ ] **Step 7.2: Build to verify the scaffold compiles**

```powershell
pwsh -File build.ps1
```

Expected: builds successfully; no Prefix yet so no behaviour change. (Mod will load with the patch registered but the prefix is missing → Harmony will error at startup. This is fine; we add the prefix in the next task. Don't install yet.)

- [ ] **Step 7.3: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardVotePatch.cs
git commit -m "plan-b-2-1/7.1: CardRewardVotePatch — scaffold + Prepare with hard/soft check split"
```

---

### Task 8: Prefix — guards, snapshot, holder signature, fire HandleVoteAsync

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs`

- [ ] **Step 8.1: Define the holder-signature record**

Add at the top of the class (after the `Lazy<FieldInfo?>` fields):
```csharp
private readonly record struct HolderSignature(int Count, int[] InstanceIds) {
    public bool Matches(IReadOnlyList<NCardHolder> current) {
        if (current.Count != Count) return false;
        for (int i = 0; i < Count; i++) {
            if (!GodotObject.IsInstanceValid(current[i])) return false;
            if (current[i].GetInstanceId() != (ulong)InstanceIds[i]) return false;
        }
        return true;
    }
}

private static HolderSignature CaptureSignature(IReadOnlyList<NCardHolder> holders) {
    var ids = new int[holders.Count];
    for (int i = 0; i < holders.Count; i++) ids[i] = (int)holders[i].GetInstanceId();
    return new HolderSignature(holders.Count, ids);
}

private static IReadOnlyList<NCardHolder>? GetCurrentHolders(NCardRewardSelectionScreen screen) {
    var host = _cardHoldersHostField.Value?.GetValue(screen) as Node;
    if (host is null) return null;
    return host.GetChildren().OfType<NCardHolder>().ToList();
}

private static int? FindHolderIndex(IReadOnlyList<NCardHolder> holders, NCardHolder target) {
    for (int i = 0; i < holders.Count; i++) {
        if (holders[i] == target || holders[i].GetInstanceId() == target.GetInstanceId()) return i;
    }
    return null;
}
```

- [ ] **Step 8.2: Add the Prefix method**

```csharp
static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder) {
    if (_resumeInProgress == 1) return true;

    // Hard guards
    if (!GodotObject.IsInstanceValid(__instance) || !GodotObject.IsInstanceValid(cardHolder)) return true;

    // Multiplayer bail
    int? playerCount = TryGetPlayerCount();
    if (playerCount is int n && n > 1) {
        if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] multiplayer detected (Players.Count > 1); bailing to vanilla");
        } else {
            TiLog.Debug("[SlayTheStreamer2][card-vote] multiplayer bail-out");
        }
        return true;
    }

    // Chat-readiness gate
    var coordinator = Voter.Default;
    if (coordinator is null) return true;
    if (coordinator.Chat.State is not ChatConnectionState.ConnectedReadWrite) {
        TiLog.Debug($"[SlayTheStreamer2][card-vote] chat not in ConnectedReadWrite (state={coordinator.Chat.State}); bailing to vanilla");
        return true;
    }

    // Atomic vote-in-progress flip
    if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
        TiLog.Debug("[SlayTheStreamer2][card-vote] repeat click during open vote — suppressed");
        return false;
    }

    // Snapshot options + holders
    var options = GetCurrentOptions(__instance);
    var holders = GetCurrentHolders(__instance);
    if (options is null || options.Count == 0 || holders is null || holders.Count == 0) {
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
    var optionsSnapshot = options.ToList();
    var holdersSnapshot = holders.ToList();
    var labels = optionsSnapshot.Select(o => o.Card.Name.GetText()).ToList();

    int playerClickIndex = FindHolderIndex(holdersSnapshot, cardHolder) ?? 0;

    var holderSig = CaptureSignature(holdersSnapshot);

    // Capture run-id (soft guard)
    string? runIdAtStart = null;
    if (RunIdGuardEnabled) {
        try {
            runIdAtStart = RunManager.Instance?.DebugOnlyGetState()?.Id?.ToString();
            if (runIdAtStart == null) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] run-ID guard degraded for this vote — null state at start");
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] run-ID guard degraded for this vote — {ex.Message}");
        }
    }

    // Voter.Start with try/catch fallback
    VoteSession session;
    try {
        session = coordinator.Start("Card Reward", labels, TimeSpan.FromSeconds(30));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-vote] Voter.Default.Start threw; falling back to vanilla", ex);
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    TiLog.Info($"[SlayTheStreamer2][card-vote] opening vote for {optionsSnapshot.Count} options; player clicked #{playerClickIndex}");
    _ = HandleVoteAsync(coordinator, __instance, session, optionsSnapshot, holderSig, playerClickIndex, runIdAtStart);
    return false;
}

private static IReadOnlyList<CardCreationResult>? GetCurrentOptions(NCardRewardSelectionScreen screen) {
    return _optionsField.Value?.GetValue(screen) as IReadOnlyList<CardCreationResult>;
}

private static int? TryGetPlayerCount() {
    try {
        return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count;
    } catch {
        return null;
    }
}
```

- [ ] **Step 8.3: Build to verify the prefix compiles**

```powershell
pwsh -File build.ps1
```

Expected: builds. Still no `HandleVoteAsync` body — Harmony will fail at runtime since the prefix calls a missing method. We add it next task.

- [ ] **Step 8.4: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardVotePatch.cs
git commit -m "plan-b-2-1/8.1: CardRewardVotePatch — Prefix with all guards + holder signature snapshot"
```

---

### Task 9: HandleVoteAsync + ResumeOnMainThread + cancellation receipt

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs`

- [ ] **Step 9.1: Add HandleVoteAsync**

```csharp
private static async Task HandleVoteAsync(
    VoteCoordinator coordinator,
    NCardRewardSelectionScreen screen,
    VoteSession session,
    IReadOnlyList<CardCreationResult> optionsSnapshot,
    HolderSignature holderSig,
    int playerClickIndex,
    string? runIdAtStart) {
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

        int winnerIndex;
        try {
            winnerIndex = await session.AwaitWinnerAsync();
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-vote] AwaitWinnerAsync threw; falling back to player click", ex);
            winnerIndex = playerClickIndex;
        }

        if (winnerIndex < 0 || winnerIndex >= optionsSnapshot.Count) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] winnerIndex {winnerIndex} out of range; using player click");
            winnerIndex = playerClickIndex;
        }

        TiLog.Info($"[SlayTheStreamer2][card-vote] resume: applying winner #{winnerIndex} on main thread");
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, winnerIndex, playerClickIndex, runIdAtStart, holderSig));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-vote] HandleVoteAsync threw; attempting fallback resume with player click", ex);
        try {
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(screen, playerClickIndex, playerClickIndex, runIdAtStart, holderSig));
        } catch (Exception postEx) {
            TiLog.Error("[SlayTheStreamer2][card-vote] fallback resume Post itself threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }
}
```

- [ ] **Step 9.2: Add ResumeOnMainThread**

```csharp
private static void ResumeOnMainThread(
    NCardRewardSelectionScreen screen,
    int preferredIndex,
    int playerClickIndex,
    string? runIdAtStart,
    HolderSignature snapshotSig) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    try {
        if (!GodotObject.IsInstanceValid(screen)) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] resume: screen no longer valid; dropping");
            return;
        }

        // Run-ID guard (only if we captured a non-null start id)
        if (runIdAtStart != null) {
            string? currentRunId = null;
            try {
                currentRunId = RunManager.Instance?.DebugOnlyGetState()?.Id?.ToString();
            } catch { /* swallow — treat as null */ }
            if (currentRunId != runIdAtStart) {
                TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: run changed during vote");
                SendCancellationReceipt();
                return;
            }
        }

        // Holder-signature check — detects reroll, screen rebuild, alternate path, etc.
        var currentHolders = GetCurrentHolders(screen);
        if (currentHolders == null || !snapshotSig.Matches(currentHolders)) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] resume aborted: card selection changed before apply");
            SendCancellationReceipt();
            return;
        }

        // Bounds check
        int applyIndex = preferredIndex;
        if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote] preferred index {applyIndex} out of range; falling back to player click");
            applyIndex = playerClickIndex;
        }
        if (applyIndex < 0 || applyIndex >= currentHolders.Count) {
            TiLog.Warn("[SlayTheStreamer2][card-vote] resume: neither preferred nor player index valid; dropping");
            return;
        }

        // Re-derive holder from current screen state and apply
        var winnerHolder = currentHolders[applyIndex];
        screen.SelectCard(winnerHolder);
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][card-vote] resume threw", ex);
    } finally {
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}

private static void SendCancellationReceipt() {
    var coordinator = Voter.Default;
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
    _ = coordinator.Chat.SendMessageAsync(
        "Vote result ignored — card selection changed before apply",
        OutgoingMessagePriority.Normal);
}
```

- [ ] **Step 9.3: Build and verify**

```powershell
pwsh -File build.ps1
```

Expected: build succeeds. `CardRewardVotePatch.cs` is now complete.

- [ ] **Step 9.4: Run all tests**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: 198 tests pass (no new tests for the patch — operator-validated).

- [ ] **Step 9.5: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardVotePatch.cs
git commit -m "plan-b-2-1/9.1: CardRewardVotePatch — HandleVoteAsync + ResumeOnMainThread + cancellation receipt"
```

---

## Phase 5: CardSkipCounterLabel (the UI)

### Task 10: `CardSkipCounterLabel` Godot RichTextLabel

**Files:**
- Create: `src/Game/Ui/CardSkipCounterLabel.cs`

- [ ] **Step 10.1: Create the label class**

```csharp
using Godot;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Godot RichTextLabel showing the streamer's per-act card-skip budget.
/// Parented under NRewardsScreen near the proceed button. Hidden when
/// cardSkipsPerAct == -1 (unlimited; nothing to display).
/// </summary>
public partial class CardSkipCounterLabel : RichTextLabel {
    private const int FontSize = 18;
    private static readonly Color DefaultColor = new(0.95f, 0.85f, 0.5f);

    public override void _Ready() {
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        AddThemeFontSizeOverride("normal_font_size", FontSize);
        AddThemeColorOverride("default_color", DefaultColor);
    }

    public void UpdateText(SkipBudgetSnapshot snap) {
        if (snap.LimitThisAct < 0) {
            Visible = false;
            return;
        }
        Visible = true;
        Text = $"Card skips: {snap.RemainingThisAct}/{snap.LimitThisAct} act";
    }

    /// <summary>
    /// Attach a new label as a child of `parent`, positioned near `proceedButton`
    /// (offset above-and-left). If `proceedButton` is null, falls back to the
    /// parent's top-right with a Warn log.
    /// </summary>
    public static CardSkipCounterLabel AttachTo(Node parent, Control? proceedButton) {
        var label = new CardSkipCounterLabel { Name = "CardSkipCounterLabel" };
        parent.AddChild(label);
        if (proceedButton is not null && GodotObject.IsInstanceValid(proceedButton)) {
            // Anchor above-and-left of the proceed button.
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
            label.Position = proceedButton.Position + new Vector2(0, -40);
            label.Size = new Vector2(300, 30);
        } else {
            TiLog.Warn("[SlayTheStreamer2][card-skip-label] _proceedButton not found; falling back to top-right of parent");
            label.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
            label.Position = new Vector2(-310, 10);
            label.Size = new Vector2(300, 30);
        }
        return label;
    }
}
```

- [ ] **Step 10.2: Build and verify**

```powershell
pwsh -File build.ps1
```

Expected: build succeeds.

- [ ] **Step 10.3: Commit**

```powershell
git add src/Game/Ui/CardSkipCounterLabel.cs
git commit -m "plan-b-2-1/10.1: CardSkipCounterLabel — RichTextLabel with proceed-button anchor"
```

---

## Phase 6: CardRewardSkipGatePatch (the gate)

### Task 11: Skip gate scaffold + Prepare hard checks + ShouldEnforceSkipGate + tracker instance

**Files:**
- Create: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`

- [ ] **Step 11.1: Create the file with scaffold**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

internal static class CardRewardSkipGatePatch {
    private static readonly SkipBudgetTracker _tracker = new();
    private static CardSkipCounterLabel? _activeLabel;

    private static readonly Lazy<FieldInfo?> _rewardButtonsField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));
    private static readonly Lazy<FieldInfo?> _proceedButtonField =
        new(() => AccessTools.Field(typeof(NRewardsScreen), "_proceedButton"));

    /// <summary>
    /// Skip gate enforces only when card-vote infrastructure is fully available.
    /// Temporary Twitch disconnect mid-run does NOT disable the gate.
    /// </summary>
    private static bool ShouldEnforceSkipGate() {
        if (ModEntry.Settings is not SettingsResult.Success) return false;
        if (!CardRewardVotePatch.PreparedSuccessfully) return false;
        if (Voter.Default == null) return false;
        return true;
    }

    private static bool PrepareHardChecks() {
        if (_rewardButtonsField.Value is null) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] hard check failed: _rewardButtons field not found");
            return false;
        }
        if (_proceedButtonField.Value is null) {
            TiLog.Warn("[SlayTheStreamer2][card-skip-gate] _proceedButton field not found; label will fallback to top-right");
            // Soft: label fallback handles this.
        }
        return true;
    }
}
```

- [ ] **Step 11.2: Build and verify**

```powershell
pwsh -File build.ps1
```

Expected: build succeeds. No postfixes registered yet — this commit doesn't change runtime behaviour.

- [ ] **Step 11.3: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m "plan-b-2-1/11.1: CardRewardSkipGatePatch — scaffold + ShouldEnforceSkipGate + tracker instance"
```

---

### Task 12: HasUnclaimedCardReward + IsCardRewardButton helpers + GetCurrentActIndex

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`

- [ ] **Step 12.1: Add helper methods**

Inside the class, add:
```csharp
private static IReadOnlyList<Control>? GetRewardButtons(NRewardsScreen screen) {
    var raw = _rewardButtonsField.Value?.GetValue(screen);
    return raw as IReadOnlyList<Control> ?? (raw as IEnumerable<Control>)?.ToList();
}

private static bool IsCardRewardButton(Control button) {
    if (!GodotObject.IsInstanceValid(button)) return false;
    if (button is not NRewardButton rb) return false;
    // PINNED IN TASK 1 SPIKE: NRewardButton.<REWARD_ACCESSOR> — property vs field.
    // Below assumes a `Reward` property; replace with the pinned accessor if it's a field.
    var rewardProp = rb.GetType().GetProperty("Reward");
    var reward = rewardProp?.GetValue(rb);
    return reward is CardReward;
}

private static bool HasUnclaimedCardReward(NRewardsScreen screen) {
    var buttons = GetRewardButtons(screen);
    if (buttons is null) {
        TiLog.Warn("[SlayTheStreamer2][card-skip-gate] could not enumerate _rewardButtons; assuming no card reward");
        return false;
    }
    return buttons.Any(b => GodotObject.IsInstanceValid(b) && IsCardRewardButton(b));
}

private static int? GetCurrentActIndex(object? runState) {
    if (runState is null) return null;
    // PINNED IN TASK 1 SPIKE: current-act access pattern.
    // Default candidate: runState.Acts.Count - 1.
    try {
        var actsProp = runState.GetType().GetProperty("Acts");
        var acts = actsProp?.GetValue(runState) as System.Collections.ICollection;
        if (acts is null) return null;
        return acts.Count - 1;
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][card-skip-gate] act-index access failed: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 12.2: Build to verify**

```powershell
pwsh -File build.ps1
```

Expected: builds.

- [ ] **Step 12.3: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m "plan-b-2-1/12.1: CardRewardSkipGatePatch — HasUnclaimedCardReward + IsCardRewardButton + GetCurrentActIndex helpers"
```

---

### Task 13: `_Ready` postfix + `_ExitTree` postfix + label attach/update

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`

- [ ] **Step 13.1: Add the AttachOrUpdateLabel helper**

```csharp
private static void AttachOrUpdateLabel(NRewardsScreen screen, int actLimit) {
    if (actLimit < 0) {
        // Hide existing label if any.
        if (_activeLabel != null && GodotObject.IsInstanceValid(_activeLabel)) {
            _activeLabel.Visible = false;
        }
        return;
    }

    if (_activeLabel == null || !GodotObject.IsInstanceValid(_activeLabel)) {
        var proceedButton = _proceedButtonField.Value?.GetValue(screen) as Control;
        try {
            _activeLabel = CardSkipCounterLabel.AttachTo(screen, proceedButton);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] label attach failed", ex);
            return;
        }
    }
    _activeLabel.UpdateText(_tracker.Snapshot(actLimit));
}
```

- [ ] **Step 13.2: Add the `_Ready` postfix nested class**

```csharp
[HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
internal static class NRewardsScreen_Ready_Postfix {
    static bool Prepare() => PrepareHardChecks();

    static void Postfix(NRewardsScreen __instance) {
        try {
            if (!ShouldEnforceSkipGate()) return;

            var runState = TryGetRunState();
            if (runState is null) return;

            // MP bail
            try {
                var playersProp = runState.GetType().GetProperty("Players");
                var players = playersProp?.GetValue(runState);
                var countProp = players?.GetType().GetProperty("Count");
                if (countProp?.GetValue(players) is int n && n > 1) return;
            } catch { /* swallow — proceed without MP bail if accessor failed */ }

            var runIdProp = runState.GetType().GetProperty("Id");
            string? runId = runIdProp?.GetValue(runState)?.ToString();
            int? actIndex = GetCurrentActIndex(runState);

            _tracker.ObserveRunAndAct(runId, actIndex);

            if (!HasUnclaimedCardReward(__instance)) return;

            var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
            if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct)) {
                __instance.DisallowSkipping();
            }

            AttachOrUpdateLabel(__instance, settings.CardSkipsPerAct);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] _Ready postfix failed", ex);
        }
    }
}

private static object? TryGetRunState() {
    try {
        return RunManager.Instance?.DebugOnlyGetState();
    } catch {
        return null;
    }
}
```

- [ ] **Step 13.3: Add the `_ExitTree` postfix nested class**

```csharp
[HarmonyPatch(typeof(NRewardsScreen), "_ExitTree")]
internal static class NRewardsScreen_ExitTree_Postfix {
    static bool Prepare() => true;   // No reflected fields; the patch is a simple null-out.
    static void Postfix() {
        _activeLabel = null;
    }
}
```

- [ ] **Step 13.4: Build to verify**

```powershell
pwsh -File build.ps1
```

Expected: builds.

- [ ] **Step 13.5: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m "plan-b-2-1/13.1: CardRewardSkipGatePatch — _Ready + _ExitTree postfixes + AttachOrUpdateLabel"
```

---

### Task 14: `RewardSkippedFrom` postfix + skip receipt formatter

**Files:**
- Modify: `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs`

- [ ] **Step 14.1: Add the skip-receipt formatter**

```csharp
private static string FormatSkipReceipt(int actUsed, int actLimit) {
    string limitPart = actLimit < 0 ? "unlimited act" : $"{actUsed}/{actLimit} act";
    return $"Streamer skipped a card reward ({limitPart})";
}

private static void SendSkipReceipt(int actLimit) {
    var coordinator = Voter.Default;
    // coordinator.Chat is the existing accessor (NeowBlessingVotePatch.cs:64 uses .State on it).
    // SendMessageAsync routes through OutgoingMessageQueue (TwitchIrcChatService.cs:285-292).
    if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;

    string text = FormatSkipReceipt(_tracker.ActSkipsUsed, actLimit);
    _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.Normal);
}
```

- [ ] **Step 14.2: Add the `RewardSkippedFrom` postfix nested class**

```csharp
[HarmonyPatch(typeof(NRewardsScreen), "RewardSkippedFrom")]
internal static class NRewardsScreen_RewardSkippedFrom_Postfix {
    static bool Prepare() => PrepareHardChecks();

    static void Postfix(NRewardsScreen __instance, Control button) {
        try {
            if (!IsCardRewardButton(button)) return;
            if (!ShouldEnforceSkipGate()) return;   // settings-check BEFORE recording

            _tracker.RecordSkip();

            var settings = ((SettingsResult.Success)ModEntry.Settings!).Settings;
            SendSkipReceipt(settings.CardSkipsPerAct);

            // Multi-card-reward gate re-evaluation
            if (!_tracker.IsSkipAllowed(settings.CardSkipsPerAct) && HasUnclaimedCardReward(__instance)) {
                __instance.DisallowSkipping();
            }

            if (_activeLabel != null && GodotObject.IsInstanceValid(_activeLabel)) {
                _activeLabel.UpdateText(_tracker.Snapshot(settings.CardSkipsPerAct));
            }
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-skip-gate] RewardSkippedFrom postfix failed", ex);
        }
    }
}
```

- [ ] **Step 14.3: Build and run all tests**

```powershell
pwsh -File build.ps1
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: build succeeds; 198 tests pass.

- [ ] **Step 14.4: Commit**

```powershell
git add src/Game/DecisionVotes/CardRewardSkipGatePatch.cs
git commit -m @'
plan-b-2-1/14.1: CardRewardSkipGatePatch — RewardSkippedFrom postfix + skip receipt

Settings-check BEFORE recording skip in tracker. Multi-card-reward
re-evaluation calls DisallowSkipping again if THIS skip exhausted budget
while other unclaimed card rewards remain. Receipt routes through
OutgoingMessageQueue via coordinator.Chat.SendMessageAsync.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
'@
```

---

## Phase 7: Operator validation (acceptance gate)

Each task in this phase is a manual playthrough. **Do not skip these.** Document any findings in notes/06 as you go.

Pre-step for all of Phase 7: install the build with all patches:
```powershell
pwsh -File build.ps1
pwsh -File install.ps1
```

Confirm `%APPDATA%\SlayTheSpire2\Mods\slay_the_streamer_2` contains the latest DLL.

### Task 15: Step 0 — Pure regression check (B.1 features only)

- [ ] **Step 15.1: Set up test settings file (B.1 keys only)**

At `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`:
```json
{
  "schemaVersion": 1,
  "channel": "<your_channel>",
  "username": "<your_bot_username>",
  "oauthToken": "oauth:<your_token>"
}
```

(No `cardSkipsPerAct` — uses default 1.)

- [ ] **Step 15.2: Run the regression playthrough**

Start StS2. Confirm:
- Mod loads (log shows `[SlayTheStreamer2]` lines from ModEntry init).
- Twitch chat receipt fires once (`slay-the-streamer-2 v... connected`).
- Start a new run → reach Neow → click any blessing → vote opens to chat.
- Cast a chat vote (`#0`/`#1`/`#2`); vote closes; chat-chosen blessing applies.

**Abandon the run before the first combat to avoid exercising the card-reward path.**

- [ ] **Step 15.3: Verify**

- All `[SlayTheStreamer2]` log prefixes are present (no leftover `[slay_the_streamer_2]`).
- No new errors in log.
- Neow vote completes correctly.

- [ ] **Step 15.4: Commit a note if anything failed; otherwise just proceed**

If failed: investigate before going further. Most likely causes: log prefix mistype, ModEntry settings accessor not assigned correctly, Neow patch run-ID guard misfiring.

```powershell
# No code commit unless issues found and fixed.
```

---

### Task 16: Step 1 — Card vote happy path (3 successful runs)

- [ ] **Step 16.1: Run 3 separate runs, exercising different aspects per run**

Each run: start, defeat first combat, observe rewards screen.

**Run A — basic vote:**
- Click the Card item on the rewards screen → card sub-screen opens.
- Click any card → vote opens (chat receipt fires).
- Cast chat vote `#1` → vote closes; chat-chosen card claimed.

**Run B — latest-wins:**
- Click the Card item → click any card → vote opens.
- From the same chatter, type `#0` then `#2`. Verify `#2` wins (latest-wins).

**Run C — bare-N format:**
- Click the Card item → click any card → vote opens.
- From a chatter, type bare `1` (no `#`). Verify it's accepted.

- [ ] **Step 16.2: Verify visible elements**

- VoteTallyLabel (top-right) shows tally during each vote.
- Skip-counter label visible near Proceed button before/after vote: `Card skips: 1/1 act`.
- Counter unchanged after card claim (no skip used).
- Close receipt fires with chat-chosen card name.

- [ ] **Step 16.3: Document findings**

If anything off, note in notes/06 under "Plan B.2.1 acceptance gate findings". Then fix-and-retry before moving on.

---

### Task 17: Step 2 — Skip used (with `cardSkipsPerAct: 1`)

- [ ] **Step 17.1: Settings unchanged (default `cardSkipsPerAct: 1`); start a new run**

Defeat first combat. Rewards screen appears. **Without claiming the card**, click Proceed.

- [ ] **Step 17.2: Verify skip behaviour**

- Skip allowed (Proceed clicks through normally).
- Chat receipt: `Streamer skipped a card reward (1/1 act)`.
- Counter label updates to `Card skips: 0/1 act`.

- [ ] **Step 17.3: Verify next-combat block**

Defeat the next combat. Rewards screen appears with card. Verify:
- Proceed button is visibly disabled (vanilla "Skip" mode greyed).
- Click the card item → vote runs → claim → Proceed re-enables.

- [ ] **Step 17.4: Commit any spec-revising notes if needed**

---

### Task 18: Step 3 — Skip blocked (`cardSkipsPerAct: 0`)

- [ ] **Step 18.1: Edit settings file to `cardSkipsPerAct: 0`**

Restart the game (settings load happens at init).

- [ ] **Step 18.2: Run a combat → rewards screen → verify**

- Proceed button visibly disabled from the moment the rewards screen appears.
- Streamer must click card → vote runs → claim → Proceed enables.
- Counter label shows `Card skips: 0/0 act`.
- No way to bypass without claiming.

- [ ] **Step 18.3: Document and commit if anything off**

---

### Task 19: Step 4 — Counter resets (act jump + new run)

- [ ] **Step 19.1: Set `cardSkipsPerAct: 1`, restart, get into a run**

Use 1 skip. Counter shows `0/1 act`.

- [ ] **Step 19.2: Open the DevConsole (backtick) and use `act 2`**

Defeat next combat in act 2. Rewards screen appears. Verify counter resets to `1/1 act`.

- [ ] **Step 19.3: Test new-run reset**

Abandon current run. Start a fresh run (new run-id). Defeat first combat. Verify counter is `1/1 act` (reset).

---

### Task 20: Step 5 — Multi-reward-type screen

- [ ] **Step 20.1: Find or trigger a multi-reward screen**

Easiest path: kill the act 1 boss (or use DevConsole `kill` and `relic add <id>` to simulate a boss-relic + card screen). Aim for a rewards screen with at least 2 reward items, one being a card.

- [ ] **Step 20.2: With `cardSkipsPerAct: 0`, claim the card via vote**

Verify after card claim:
- Proceed transitions to enabled (vanilla state machine self-corrected, OR remains disabled — record either result).
- The other reward (gold/potion/relic) can be claimed or skipped as normal.

- [ ] **Step 20.3: Document the lifecycle observation**

If Proceed stays disabled after card claim while other unclaimed rewards remain:
- This is the R5/R6 "DisallowSkipping locks all reward types" issue.
- Record in notes/06 as a B.2.2 polish item: "Add `RewardCollectedFrom` postfix to re-evaluate gate after card claim".
- For B.2.1 v0.1, accept and document — operator verifies that this case is rare in practice.

---

### Task 21: Step 6 — Edge cases

- [ ] **Step 21.1: Mid-vote run abandon**

Start a card vote (click a card). Immediately open menu and click Abandon Run. Wait 30 seconds for the vote timer to expire. Verify:
- Log shows `[SlayTheStreamer2][card-vote] resume aborted: run changed during vote` (Warn level).
- Chat receipt fires: `Vote result ignored — card selection changed before apply`.
- No crash.

- [ ] **Step 21.2: Mid-vote reroll (if a relic enables it)**

If you can find a reroll-granting relic via `relic add <id>` or in normal play: start a card vote, click reroll on the sub-screen. Wait for vote timer.
- Verify cancellation receipt: `Vote result ignored — card selection changed before apply`.
- Click a new card → new vote starts.

If no reroll mechanic available in current build, record as N/A.

- [ ] **Step 21.3: Streamer escape (menu) mid-vote**

Start a card vote. Open menu and resume gameplay (don't abandon). Wait for vote timer.
- Vote runs to normal close in background.
- Resume drops via IsInstanceValid (screen still exists, but the resume validity sequence handles transient state).
- No crash.

- [ ] **Step 21.4: Rapid card clicks**

Start a card vote (first click). Immediately click multiple other cards.
- Only first triggers vote.
- Subsequent clicks no-op via `_voteInProgress` guard.
- Log shows `repeat click during open vote — suppressed` (Debug level).

- [ ] **Step 21.5: Mode B verification (look + back out)**

**Conditional on Task 1's spike result.** If vanilla supports back-out from `NCardRewardSelectionScreen` to `NRewardsScreen` without selecting:
- Open card sub-screen, see cards, return to rewards screen WITHOUT picking, click Proceed.
- With `cardSkipsPerAct: 1`: skip is allowed (counter decrements). Confirms Decision 18 — Mode B.

If no back-out exists: record as "Mode B verification N/A".

---

### Task 22: Step 7 — Activation-gate verification (malformed settings)

- [ ] **Step 22.1: Edit settings file to malformed state**

Set the oauth token to a bad value:
```json
{
  "schemaVersion": 1,
  "channel": "#foo",
  "username": "bot",
  "oauthToken": "oauth:bad"
}
```

The mod will load with `Settings = SettingsResult.Success` but Twitch will fail to authenticate. **Wait — this is a chat-disconnected scenario, not malformed.** For true malformed settings, use:
```json
{
  "schemaVersion": 1,
  "channel": "",
  "username": "",
  "oauthToken": ""
}
```

This makes ModSettings.Load return `SettingsResult.Malformed`.

- [ ] **Step 22.2: Restart game and verify**

- Mod loads but logs `settings file at ... is malformed`.
- Start a run, defeat first combat, observe rewards screen.
- **Skip gate does NOT enforce** — Proceed is in vanilla state.
- Skip-counter label NOT visible.
- Clicking Proceed proceeds vanilla.
- No chat receipt fires (Twitch never connected).

- [ ] **Step 22.3: Document and commit findings**

Restore good settings before continuing.

---

## Phase 8: Finalization

### Task 23: Update `notes/06` with B.2.1 outcome

**Files:**
- Modify: `notes/06-followups-and-deferred.md`

- [ ] **Step 23.1: Add a "Plan B.2.1 (resolved YYYY-MM-DD)" section at the top**

Mirror the format of the existing "Plan B.1 vertical slice (resolved 2026-05-10)" section. Include:
- Acceptance gate checkboxes (all 7 steps).
- Architecture-defining outcome (suspend-and-resume reused successfully on second decision type).
- Findings worth preserving (any spike findings, any operator-validation surprises, any vanilla bugs observed).
- B.2.1 follow-ups (deferred to B.2.2 / Plan C / cleanup) — at minimum:
  - DisallowSkipping lifecycle re-evaluation (RewardCollectedFrom postfix) — only if Step 5 revealed the lockout
  - Save/Load loophole — persist counters in v0.2 or earlier if streamers exploit
  - Per-relic curation — v0.2 polish

- [ ] **Step 23.2: Commit**

```powershell
git add notes/06-followups-and-deferred.md
git commit -m "plan-b-2-1/23.1: notes/06 — B.2.1 outcome captured"
```

---

### Task 24: Tag `plan-b-2-1-complete` + update README scope

**Files:**
- Modify: `README.md` (status section)

- [ ] **Step 24.1: Update README status**

In `README.md`, around line 7-12, update the status block:
```markdown
**Status**: v0.1 in active development. **B.2.1 card reward vote shipped**
2026-05-10 (`plan-b-2-1-complete` tag) — chat now votes on card rewards
with a per-act skip budget so the streamer can't silently bypass chat
agency. The remaining 3 votes (boss relic, map path, act boss) plus an
in-game settings UI come in B.2.2-4 and B.3.
**Not yet for end users** — installation requires manual JSON config.
```

Also update the Scope section's checked-vs-unchecked items:
```markdown
- Neow blessings (✅ shipped in B.1, 2026-05-10)
- Card rewards (✅ shipped in B.2.1, 2026-05-10)
- Boss relic picks
- Map path selection
- Act boss (custom screen — likely needs its own sub-plan, B.3)
```

- [ ] **Step 24.2: Commit + tag**

```powershell
git add README.md
git commit -m "plan-b-2-1/24.1: README — B.2.1 shipped; card rewards now chat-voted"
git tag plan-b-2-1-complete
```

- [ ] **Step 24.3: Verify tag**

```powershell
git log --oneline plan-b-2-1-complete -1
git tag --list plan-b-*
```

Expected: see `plan-b-1-complete` and `plan-b-2-1-complete` listed.

- [ ] **Step 24.4: Final test sweep**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```

Expected: 198 tests pass (193 from B.1 + Plan A baseline + 5 new ModSettings + 10 new SkipBudgetTracker).

Wait — actual count: 183 pre-B.2.1 + 5 + 10 = 198. Confirm.

- [ ] **Step 24.5: Final operator-validation sanity**

Reinstall and run a quick sanity playthrough: settings present (good config), start a run, Neow vote works, defeat first combat, card vote works, counter label visible, skip-counter updates correctly.

If all green: B.2.1 is shipped. Tag is in place. Done.

---

## Self-review

### Spec coverage check

- ✅ Decision 1 (single-vote scope): single sub-plan, 24 tasks, all card-reward specific.
- ✅ Decision 2 (patch target SelectCard): Tasks 7-9.
- ✅ Decision 3 (gate target _Ready postfix): Task 13. Task 1 verifies patchability.
- ✅ Decision 4 (RewardSkippedFrom postfix): Task 14.
- ✅ Decision 5 (suspend-and-resume reused): Tasks 7-9 mirror NeowBlessingVotePatch shape.
- ✅ Decision 6 (run-ID guard, hard/soft Prepare): Task 6 (Neow), Tasks 7-9 (card).
- ✅ Decision 7 (skip never chat-vote option): no patch task; baked into vote-options snapshot logic in Task 8.
- ✅ Decision 8 (per-act budget): Tasks 5, 14.
- ✅ Decision 9 (CardSkipCounterLabel RichTextLabel): Task 10.
- ✅ Decision 10 (random fallback never picks skip): no code change needed; uses Plan A's existing fallback.
- ✅ Decision 11 (receipt format name-only): Task 8 builds labels from `Card.Name.GetText()`.
- ✅ Decision 12 (reroll/alternates not patched, generic cancellation receipt): Task 9.
- ✅ Decision 13 (no helper extraction): no helper task in plan.
- ✅ Decision 14 (use vanilla DevConsole): no debug-patch task; Task 19 uses DevConsole `act 2`.
- ✅ Decision 15 (ModEntry.Settings accessor): Task 4.
- ✅ Decision 16 (skip receipt inlined Game side): Task 14.
- ✅ Decision 17 (no Interlocked for skip counters): Task 5 uses plain `++`.
- ✅ Decision 18 (Mode B): Task 21 conditional verification.
- ✅ Decision 19 (SkipBudgetTracker, string? run-id): Task 5.
- ✅ Decision 20 (TiLog [SlayTheStreamer2] prefix): Tasks 4, 6, all new patch files.
- ✅ Decision 21 (ShouldEnforceSkipGate activation gate): Task 11. Task 22 validates.

Acceptance gate coverage (Steps 0-7 → Tasks 15-22): all 7 steps mapped.

Failure modes coverage: spec lists 17 failure modes. Most are runtime behaviours, not separate tasks. The relevant tasks include the necessary log/recovery code:
- #1, #2, #3 (Prepare failures): Tasks 6, 7, 11 each have hard/soft Prepare.
- #4, #5 (postfix throws): try/catch in Tasks 13, 14.
- #6 (_ExitTree throws/doesn't fire): Task 13 has try/catch; Task 1 verifies patchability.
- #7-#10 (DebugOnlyGetState null cases): Tasks 6, 9 handle.
- #11, #12 (run-ID + holder-signature mismatches): Task 9 has cancellation receipt.
- #13 (settings missing/malformed): Task 22 validates.
- #14 (Save/Load loophole): documented in notes/06 in Task 23.
- #15 (AutoSlay): documented; no code change needed.
- #16 (MP bail): Tasks 8, 13.
- #17 (Twitch disconnected mid-run): Task 11's `ShouldEnforceSkipGate` does NOT include chat state, so this is correct (gate stays active).

### Placeholder scan

- No "TBD"/"TODO"/"implement later" phrases.
- All test code shown explicitly.
- All file paths exact.
- All commands shown verbatim.
- Task 1 spike has `<EXACT_FIELD_OR_METHOD_PINNED>` markers in the notes/06 template — these are explicitly meant for the spike to fill in, NOT placeholders for the implementer to leave.
- Task 12 has comments referencing "PINNED IN TASK 1 SPIKE" as guidance for the implementer to swap in the actual accessor name. The default candidate code is provided (`Acts.Count - 1`, `_cardRow`'s children, `Reward` property). If the spike pins something different, swap it.

### Type consistency check

- `SettingsResult` (named in Task 4): pre-existing in B.1's `ModSettings.cs`.
- `ChatSettings` extended in Task 3 with `CardSkipsPerAct` field; Tasks 13, 14 read it via `success.Settings.CardSkipsPerAct`.
- `SkipBudgetTracker` (Task 5): `ObserveRunAndAct(string?, int?)`, `IsSkipAllowed(int)`, `RecordSkip()`, `Snapshot(int)` — all signatures match between definition and call sites in Tasks 13, 14.
- `SkipBudgetSnapshot` record struct (Task 5): `UsedThisAct`, `LimitThisAct`, `RemainingThisAct` — all referenced consistently in Task 10's `UpdateText`.
- `CardRewardVotePatch.PreparedSuccessfully` (Task 7) read by `ShouldEnforceSkipGate()` (Task 11). Same name.
- `CardRewardVotePatch.VoteInProgress` (Task 8) — exposed for skip gate cross-check; not used in current postfixes but available.
- `CardSkipCounterLabel.AttachTo(Node parent, Control? proceedButton)` (Task 10) called from `AttachOrUpdateLabel` (Task 13) with matching signature.
- `HolderSignature.Matches(IReadOnlyList<NCardHolder>)` (Task 8) called from `ResumeOnMainThread` (Task 9). Match.

No type mismatches found.

---

**Plan complete and saved to `docs/superpowers/plans/2026-05-10-plan-b-2-1-card-reward-vote.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

**Which approach?**
