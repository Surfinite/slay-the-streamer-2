# Plan B.3.2 Act-Variant Vote Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a chat vote at the moment a streamer clicks Embark on the character-select screen, letting chat pick the Act 1 variant (Underdocks vs Overgrowth) before the run is generated. Use vanilla's existing `StartRunLobby.Act1` override hook so we don't reimplement RNG / variant logic.

**Architecture:** One Harmony prefix on the private `StartRunLobby.BeginRunLocally(string, List<ModifierModel>)`. Prefix returns `false` to suspend; an async handler runs a 30s `VoteSession` with a custom `formatReceipt` callback that intercepts no-votes close-receipts and side-channels the outcome; resume re-invokes `BeginRunLocally` reflectively after writing `__instance.Act1 = winnerKey` (restored in `finally`). Popup is a self-owned `CanvasLayer` parented to the 4:3 gameplay-area `Control` (matching `BossVotePopup`'s pattern); vertical 50/50 split with native-size combat backgrounds + entry banners (L1) or hex-color rectangles + title labels (L3 fallback). All cancellation/abandonment probes live in the patch, injected into the popup via `Func<bool>`.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (`0Harmony.dll` shipped with game), xUnit 2.9. Tests run via `dotnet test`; full build via `pwsh -File build.ps1`; install via `pwsh -File install.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-18-plan-b-3-2-act-variant-vote-design-v3.md`](../specs/2026-05-18-plan-b-3-2-act-variant-vote-design-v3.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Per-task commits:** each task ends with `git commit` using a `plan-b-3-2/N.M:` prefix. Commits to `main` are pre-authorised for this slice. Every commit ends with the trailer `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` per CLAUDE.md.

---

## File Structure

**New files:**
- `src/Game/Ui/ActVariantOption.cs` (~15 LOC) — DTO `readonly record struct` carrying primitives only. No Godot, no MegaCrit. Test-csproj-included via surgical `<Compile Include>`.
- `src/Game/Ui/ActVariantPopupMode.cs` (~5 LOC) — `internal enum { L1Textures, L3Fallback }`. Test-csproj-included.
- `src/Game/Ui/ActVariantVotePopup.cs` (~250 LOC) — Godot Control. Fully MegaCrit-free. Takes `Func<bool> shouldCancel` and `Action onUserAbandoned` from patch. NOT in test compile.
- `src/Game/DecisionVotes/ActVariantVoteResolver.cs` (~130 LOC) — Pure CLR. `BuildCandidates`, `ResolveWinnerKey`, `ShouldBail`, `ActVariantAssetPaths`, `ActVariantPrewarmResult`. IN test compile via auto-include.
- `src/Game/DecisionVotes/ActVariantReceiptFormatter.cs` (~30 LOC) — Pure CLR. `Format(VoteSnapshot, ReceiptKind, Action onNoVotes) → string`. IN test compile.
- `src/Game/DecisionVotes/ActVariantVotePatch.cs` (~280 LOC) — Harmony patch on `StartRunLobby.BeginRunLocally`. NOT in test compile.
- `tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs` (~80 LOC) — `BuildCandidates`, `ResolveWinnerKey`, `ShouldBail`.
- `tests/Game/DecisionVotes/ActVariantReceiptFormatterTests.cs` (~70 LOC) — Custom-formatter behavior, including the no-votes substitution and `onNoVotes` callback invocation.

**Modified files:**
- `src/Game/Bootstrap/ModSettings.cs` — add `VoteOnActVariant : bool = true` and `ForceL3PopupFallback : bool = false` to the loader path.
- `tests/slay_the_streamer_2.tests.csproj` — add `<Compile Include="..\src\Game\Ui\ActVariantOption.cs" />` and `<Compile Include="..\src\Game\Ui\ActVariantPopupMode.cs" />`.
- `notes/06-followups-and-deferred.md` — append B.3.2 acceptance-gate results section after operator validation; add bullets for any spike findings worth preserving.

**No changes:** `src/Ti/*` (any file), `EnglishReceipts.cs`, `VoteSession.cs`, `VoteCoordinator.cs`, `ModEntry.cs`, existing B.1/B.2/B.3 vote patches.

---

## Task 1: Pre-implementation spike — verify v3 assumptions against runtime

**Goal:** Surface 9 spike deliverables BEFORE writing production code. The spec is decompile-verified for most claims, but several runtime-only behaviors and asset paths can't be checked statically.

**Files:**
- Create: `notes/B3-2-spike-2026-05-18.md` (spike notes — committed at task end so future readers can see what was resolved)

- [ ] **Step 1: Verify `PreloadManager.Cache.GetTexture` is synchronous**

Read [`decompiled/sts2/MegaCrit/sts2/Core/Assets/AssetCache.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Assets/AssetCache.cs) — find the `GetTexture(string)` method. B.3.1 verified `GetScene` is sync (lines ~30-40 of that file). Confirm `GetTexture` follows the same pattern (no `await`, no `Task` return type, no async loader).

Record in spike notes: confirmed sync / actually async / NOT FOUND on this type. If async, blocks all asset-loading design — flag and stop.

- [ ] **Step 2: Verify `BeginRunLocally` idempotency**

Read [`decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Multiplayer/Game/Lobby/StartRunLobby.cs) lines 408–438 (the entire `BeginRunLocally` body). Trace what runs between method entry and line 411 (the `GetRandomList` call). Looking for: instance-field mutations, `RunManager` registrations, `LobbyListener` notifications.

If the method only constructs a local `Rng` from the seed before line 411, idempotency is safe. If it mutates `_isBeginningRun` (line 437) or any other instance flag before line 411, the second call would behave differently.

Record: idempotent / mutates state X before line 411. Decision: **if mutations exist, REMOVE the fallback re-invoke in Task 9 Step 4** and accept that player must restart on rare reflection failures.

- [ ] **Step 3: Verify `Act1` is read exactly once during `BeginRunLocally`**

In the same file, grep for `Act1` references inside `BeginRunLocally`. Spec asserts there's only one read at line 412. Confirm via decompile.

Optional runtime validation (if uncertain): write a temporary `[HarmonyPatch]` postfix on `BeginRunLocally` that logs `__instance.Act1` value, attach to the mod, run a vanilla run (without B.3.2 patch installed), confirm only one log line per Embark. Remove the postfix after validation.

Record: confirmed single read / additional read site at X. If additional reads exist, design needs `GetRandomList` postfix Plan B instead of `Act1` write — flag and stop.

- [ ] **Step 4: Identify the cancellation probe**

Find a `Func<bool>` shape that reliably returns `true` when the streamer has navigated back to character-select after clicking Embark. Candidates per spec:
- `NCharacterSelectScreen.Instance == null` (if such a static singleton exists)
- Polling `SceneTree.CurrentScene` identity (capture at vote start; compare during `_Process`)
- A known screen-state property change
- Subscription to a scene-tree-change signal

Read [`decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/CharacterSelect/NCharacterSelectScreen.cs) — confirm whether `Instance` static exists. If yes, that's the simplest probe.

Record: chosen probe expression. Used in Task 9 Step 4 (`IsRunStartAbandoned`).

- [ ] **Step 5: Identify the gameplay-area `Control` for popup parenting**

Read [`src/Game/Ui/BossVotePopup.cs`](../../../src/Game/Ui/BossVotePopup.cs) — find how it parents its `CanvasLayer`. The B.3 popup already handles the 4:3 gameplay-area lock correctly per operator validation across multiple resolutions; mirror its pattern.

If `BossVotePopup` parents to `SceneTree.Root` directly (and gets away with it because Godot's `Stretch` mode handles letterboxing for top-level CanvasLayers), B.3.2 can do the same. If it parents to a specific in-game Control, use the same target.

Record: parent target expression. Used in Task 10 Step 3.

- [ ] **Step 6: Locate combat-room background paths**

The popup needs an asset path per variant for the combat-room background art. Path candidates:
- `res://images/backgrounds/<act-id>/combat_bg.png` or similar
- A constant or property on `Overgrowth.cs` / `Underdocks.cs` (the `ActModel` subclasses)
- A field on `_rooms.normalEncounters[0]`'s scene tree

Read [`decompiled/sts2/MegaCrit/sts2/Core/Models/Acts/Overgrowth.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Acts/Overgrowth.cs) and [`decompiled/sts2/MegaCrit/sts2/Core/Models/Acts/Underdocks.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Acts/Underdocks.cs). Look for any `Background*Path` / `CombatBgPath` / `EnvironmentPath` properties. If none, grep `decompiled/sts2/` for `res://images/backgrounds/` to find the path scheme.

For each candidate path: verify `ResourceLoader.Exists(path)` would return true by checking the actual Godot asset structure (sample the install folder under `res://` or look for the path in any scene file).

Record: 2 background paths verified or null.

- [ ] **Step 7: Locate entry-banner paths**

The "Underdocks" / "Overgrowth" banner graphic shown at act start. Candidates:
- `res://images/ui/act_intros/<id>_banner.png`
- `res://animations/act_intros/<id>.tres`
- A property on `ActModel` or a sibling intro-screen file

Grep `decompiled/sts2/` for `act_intro`, `act_banner`, `intro_banner`. If found, follow to the path scheme.

Record: 2 banner paths verified or null.

- [ ] **Step 8: Verify banner anchor convention**

For each located banner asset, determine the natural anchor: top-center, center, top-left, etc. This locks the `column_width / 2, gameplay_height / 3` overlay position in Task 10. If the natural anchor is top-center, the position math is column-center-x at one-third from top. If center-anchored, the y is `gameplay_height / 3 + bannerHeight / 2`.

If banners aren't located (null in Step 7), skip this step — L3 fallback applies.

Record: per-banner anchor convention.

- [ ] **Step 9: Verify `VoteSession.Cancel()` idempotency**

Read [`src/Ti/Voting/VoteSession.cs`](../../../src/Ti/Voting/VoteSession.cs) — find `Cancel()`. Confirm a second call after the session has already transitioned to `Cancelled` or `Closed` state is a no-op (no exception, no double-fire of `Closed`/`Cancelled` events).

If NOT idempotent: in Task 11, wrap the `_Input` ESC handler's `_session.Cancel()` call with a local `_cancelledSent` guard.

Record: idempotent / needs local guard.

- [ ] **Step 10: Write spike notes and commit**

Write all findings to `notes/B3-2-spike-2026-05-18.md`. Structure as one section per Step (1-9). Include the chosen probe expression, parent target, and asset paths verbatim so Tasks 9-10 can copy them.

```bash
git add notes/B3-2-spike-2026-05-18.md
git commit -m "$(cat <<'EOF'
plan-b-3-2/1.1: pre-implementation spike findings

Records the 9 spike deliverable outcomes (Cache.GetTexture sync,
BeginRunLocally idempotency, Act1 read-site count, cancellation probe
choice, gameplay-area parent target, 4 asset paths, banner anchors,
Cancel() idempotency). Blocks decisions for Tasks 9-11.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `ActVariantOption` DTO + `ActVariantPopupMode` enum

**Goal:** Establish the test-compile-friendly DTO and enum that Tasks 3-11 reference.

**Files:**
- Create: `src/Game/Ui/ActVariantOption.cs`
- Create: `src/Game/Ui/ActVariantPopupMode.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` — add 2 surgical `<Compile Include>` lines.

- [ ] **Step 1: Write `ActVariantOption.cs`**

```csharp
namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// DTO for one act-variant column in the popup. Carries primitives only —
/// no Godot, no MegaCrit references, so it's reachable from the
/// Microsoft.NET.Sdk-based test project. Nullable asset paths allow L3
/// fallback when assets aren't located during the spike.
///
/// FallbackColorHex format: 6-digit RRGGBB (no leading '#', no alpha).
/// Sourced from each ActModel.MapBgColor.
/// </summary>
internal readonly record struct ActVariantOption(
    int Index,
    string Key,
    string Title,
    string? BackgroundPath,
    string? BannerPath,
    string FallbackColorHex);
```

- [ ] **Step 2: Write `ActVariantPopupMode.cs`**

```csharp
namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Render mode for ActVariantVotePopup. Determined by PreWarmAssets:
/// L1Textures when all 4 assets loaded; L3Fallback when any failed or
/// ForceL3PopupFallback setting is true.
/// </summary>
internal enum ActVariantPopupMode {
    L1Textures,
    L3Fallback,
}
```

- [ ] **Step 3: Add surgical `<Compile Include>` to test csproj**

Read [`tests/slay_the_streamer_2.tests.csproj`](../../../tests/slay_the_streamer_2.tests.csproj). Find the existing surgical `<Compile Include>` lines (e.g., `..\src\Game\Ui\PortraitFit.cs` from B.3.1). Add immediately after them:

```xml
<Compile Include="..\src\Game\Ui\ActVariantOption.cs" />
<Compile Include="..\src\Game\Ui\ActVariantPopupMode.cs" />
```

- [ ] **Step 4: Build the test project to confirm includes compile**

Run from repo root:
```powershell
dotnet build tests/slay_the_streamer_2.tests.csproj
```
Expected: succeeds. If errors reference missing types in `ActVariantOption.cs`, double-check it's pure-CLR (no `Godot.*` or `MegaCrit.*` imports).

- [ ] **Step 5: Commit**

```bash
git add src/Game/Ui/ActVariantOption.cs src/Game/Ui/ActVariantPopupMode.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "$(cat <<'EOF'
plan-b-3-2/2.1: ActVariantOption DTO + ActVariantPopupMode enum

readonly record struct ActVariantOption (Index, Key, Title,
BackgroundPath?, BannerPath?, FallbackColorHex). Nullable asset paths
support L3 fallback. Hex format: 6-digit RRGGBB only.

Both types added to test csproj via surgical <Compile Include> entries
per CLAUDE.md Tier 1 (src/Game/Ui/* is not in any auto-include glob).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `ActVariantVoteResolver` — `BuildCandidates`, `ResolveWinnerKey`, `ActVariantAssetPaths`

**Goal:** Pure-CLR candidate construction and winner-key resolution. Test-first; the asset paths are populated from Task 1 spike output.

**Files:**
- Create: `src/Game/DecisionVotes/ActVariantVoteResolver.cs`
- Create: `tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs`

- [ ] **Step 1: Write failing test for `BuildCandidates`**

Create `tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs`:

```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Game.Ui;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public sealed class ActVariantVoteResolverTests {

    [Fact]
    public void BuildCandidates_returns_exactly_two_options() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void BuildCandidates_keys_are_lowercase_overgrowth_and_underdocks() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal("overgrowth", candidates[0].Key);
        Assert.Equal("underdocks", candidates[1].Key);
    }

    [Fact]
    public void BuildCandidates_indices_are_stable_zero_and_one() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(0, candidates[0].Index);
        Assert.Equal(1, candidates[1].Index);
    }

    [Fact]
    public void BuildCandidates_titles_are_non_empty() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.False(string.IsNullOrWhiteSpace(candidates[0].Title));
        Assert.False(string.IsNullOrWhiteSpace(candidates[1].Title));
    }

    [Fact]
    public void BuildCandidates_fallback_color_matches_six_digit_hex() {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        var hexPattern = new Regex("^[0-9A-Fa-f]{6}$");
        Assert.Matches(hexPattern, candidates[0].FallbackColorHex);
        Assert.Matches(hexPattern, candidates[1].FallbackColorHex);
    }

    [Fact]
    public void BuildCandidates_returns_independent_list_instances_each_call() {
        var a = ActVariantVoteResolver.BuildCandidates();
        var b = ActVariantVoteResolver.BuildCandidates();
        Assert.NotSame(a, b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantVoteResolverTests"
```
Expected: build error (`ActVariantVoteResolver does not exist`).

- [ ] **Step 3: Write `ActVariantVoteResolver.cs` with `BuildCandidates` + `ResolveWinnerKey` + `ActVariantAssetPaths`**

```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Game.Ui;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Pure-CLR helpers for the B.3.2 act-variant vote. No Godot, no MegaCrit,
/// no TiLog. Lives in src/Game/DecisionVotes/ so it's picked up by the test
/// csproj's auto-include glob.
/// </summary>
internal static class ActVariantVoteResolver {

    /// <summary>
    /// Asset paths discovered during the Task 1 spike. Null entries trigger
    /// L3 fallback in the popup. FallbackColorHex values come from each
    /// ActModel.MapBgColor (verified during spike).
    /// </summary>
    internal static class ActVariantAssetPaths {
        // TODO(spike): populate from Task 1 Step 6-7. Set to null until verified.
        internal const string? OvergrowthCombatBackground = null;
        internal const string? UnderdocksCombatBackground = null;
        internal const string? OvergrowthEntryBanner = null;
        internal const string? UnderdocksEntryBanner = null;

        // From decompile: Overgrowth.MapBgColor = new Color("A78A67");
        //                 Underdocks.MapBgColor = new Color("9F95A5");
        internal const string OvergrowthFallbackHex = "A78A67";
        internal const string UnderdocksFallbackHex = "9F95A5";
    }

    internal static IReadOnlyList<ActVariantOption> BuildCandidates() {
        return new[] {
            new ActVariantOption(
                Index: 0,
                Key: "overgrowth",
                Title: "Overgrowth",
                BackgroundPath: ActVariantAssetPaths.OvergrowthCombatBackground,
                BannerPath: ActVariantAssetPaths.OvergrowthEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.OvergrowthFallbackHex),
            new ActVariantOption(
                Index: 1,
                Key: "underdocks",
                Title: "Underdocks",
                BackgroundPath: ActVariantAssetPaths.UnderdocksCombatBackground,
                BannerPath: ActVariantAssetPaths.UnderdocksEntryBanner,
                FallbackColorHex: ActVariantAssetPaths.UnderdocksFallbackHex),
        };
    }

    internal static string ResolveWinnerKey(IReadOnlyList<ActVariantOption> options, int? winnerIndex) {
        if (winnerIndex is null) return "random";
        if (winnerIndex < 0 || winnerIndex >= options.Count) return "random";
        return options[winnerIndex.Value].Key;
    }
}
```

- [ ] **Step 4: Run the 6 `BuildCandidates_*` tests to confirm pass**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantVoteResolverTests"
```
Expected: 6 passed.

- [ ] **Step 5: Add `ResolveWinnerKey` tests**

Append to `ActVariantVoteResolverTests.cs` (above the closing `}`):

```csharp
    [Theory]
    [InlineData(0, "overgrowth")]
    [InlineData(1, "underdocks")]
    public void ResolveWinnerKey_valid_index_returns_matching_key(int idx, string expected) {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal(expected, ActVariantVoteResolver.ResolveWinnerKey(candidates, idx));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    public void ResolveWinnerKey_null_or_out_of_range_returns_random(int? idx) {
        var candidates = ActVariantVoteResolver.BuildCandidates();
        Assert.Equal("random", ActVariantVoteResolver.ResolveWinnerKey(candidates, idx));
    }
```

- [ ] **Step 6: Run all resolver tests, expect 12 passing total**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantVoteResolverTests"
```
Expected: 12 passed.

- [ ] **Step 7: Update `ActVariantAssetPaths` constants with spike findings**

Edit `src/Game/DecisionVotes/ActVariantVoteResolver.cs` and replace the four `null` constants with actual `res://...` paths from `notes/B3-2-spike-2026-05-18.md` Steps 6-7. If any remained null after the spike, leave them null — L3 fallback handles it.

- [ ] **Step 8: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantVoteResolver.cs tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/3.1: ActVariantVoteResolver BuildCandidates + ResolveWinnerKey

Pure-CLR resolver covering candidate construction (hardcoded 2-element
[Overgrowth, Underdocks] pool, no unlock gating per spec) and
winner-index → key string mapping with "random" fallback for
null/out-of-range. 12 unit tests covering invariants + winner mapping.

ActVariantAssetPaths constants populated from spike findings.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `ActVariantVoteResolver.ShouldBail` + bail tests

**Goal:** Pure-CLR bail logic for the 5 testable bail reasons. `ResumeInProgress` and `VoteInProgress` are NOT in the enum — they have atomic-acquire semantics that pure functions can't replicate and are verified by Gates 7+12 instead.

**Files:**
- Modify: `src/Game/DecisionVotes/ActVariantVoteResolver.cs`
- Modify: `tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs`

- [ ] **Step 1: Write failing tests for `ShouldBail`**

Append to `ActVariantVoteResolverTests.cs`:

```csharp
    using ChatState = SlayTheStreamer2.Ti.Chat.ChatConnectionState;
    using BailReason = SlayTheStreamer2.Game.DecisionVotes.ActVariantVoteResolver.BailReason;

    [Fact]
    public void ShouldBail_none_when_all_inputs_healthy() {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: "random",
            candidateCount: 2);
        Assert.Equal(BailReason.None, reason);
    }

    [Fact]
    public void ShouldBail_settings_off_returns_SettingsOff() {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: false,
            playerCount: 1,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: "random",
            candidateCount: 2);
        Assert.Equal(BailReason.SettingsOff, reason);
    }

    [Fact]
    public void ShouldBail_multiplayer_returns_Multiplayer() {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 2,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: "random",
            candidateCount: 2);
        Assert.Equal(BailReason.Multiplayer, reason);
    }

    [Theory]
    [InlineData(ChatState.Disconnected)]
    [InlineData(ChatState.Connecting)]
    [InlineData(ChatState.AuthenticationFailed)]
    [InlineData(ChatState.JoinFailed)]
    [InlineData(ChatState.Disposed)]
    public void ShouldBail_chat_unreadable_returns_ChatUnreadable(ChatState state) {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: state,
            act1Value: "random",
            candidateCount: 2);
        Assert.Equal(BailReason.ChatUnreadable, reason);
    }

    [Theory]
    [InlineData(ChatState.ConnectedReadWrite)]
    [InlineData(ChatState.ConnectedReadOnly)]
    public void ShouldBail_chat_readable_passes_to_next_check(ChatState state) {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: state,
            act1Value: "random",
            candidateCount: 2);
        Assert.Equal(BailReason.None, reason);
    }

    [Theory]
    [InlineData("overgrowth")]
    [InlineData("underdocks")]
    [InlineData("anything-not-random")]
    public void ShouldBail_act1_pinned_returns_Act1Pinned(string act1) {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: act1,
            candidateCount: 2);
        Assert.Equal(BailReason.Act1Pinned, reason);
    }

    [Fact]
    public void ShouldBail_act1_pinned_uses_ordinal_comparison() {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: "RANDOM",   // wrong case → not "random" by ordinal
            candidateCount: 2);
        Assert.Equal(BailReason.Act1Pinned, reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShouldBail_pool_degenerate_returns_PoolDegenerate(int count) {
        var reason = ActVariantVoteResolver.ShouldBail(
            settingsEnabled: true,
            playerCount: 1,
            chatState: ChatState.ConnectedReadWrite,
            act1Value: "random",
            candidateCount: count);
        Assert.Equal(BailReason.PoolDegenerate, reason);
    }
```

- [ ] **Step 2: Run tests to confirm they fail**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantVoteResolverTests.ShouldBail"
```
Expected: build error (`BailReason does not exist`).

- [ ] **Step 3: Add `ShouldBail` + `BailReason` to `ActVariantVoteResolver.cs`**

Append to `ActVariantVoteResolver.cs`, inside the static class:

```csharp
    /// <summary>
    /// Bail reasons returnable as pure-function output. Atomic-acquire bails
    /// (ResumeInProgress, VoteInProgress) are intentionally NOT in this enum —
    /// they have Interlocked.CompareExchange semantics that pure functions
    /// cannot replicate. Those bails are handled inline in
    /// ActVariantVotePatch.Prefix and verified by operator-validation
    /// Gates 7 (spam-Embark) and 12 (Embark→ESC→Embark cycle).
    /// </summary>
    internal enum BailReason {
        None,
        SettingsOff,
        Multiplayer,
        ChatUnreadable,
        Act1Pinned,
        PoolDegenerate,
    }

    internal static BailReason ShouldBail(
            bool settingsEnabled,
            int playerCount,
            SlayTheStreamer2.Ti.Chat.ChatConnectionState chatState,
            string act1Value,
            int candidateCount) {
        if (!settingsEnabled) return BailReason.SettingsOff;
        if (playerCount > 1) return BailReason.Multiplayer;
        if (chatState is not (
                SlayTheStreamer2.Ti.Chat.ChatConnectionState.ConnectedReadWrite or
                SlayTheStreamer2.Ti.Chat.ChatConnectionState.ConnectedReadOnly))
            return BailReason.ChatUnreadable;
        if (!string.Equals(act1Value, "random", System.StringComparison.Ordinal))
            return BailReason.Act1Pinned;
        if (candidateCount <= 1) return BailReason.PoolDegenerate;
        return BailReason.None;
    }
```

- [ ] **Step 4: Run all resolver tests, expect ~25 passing**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantVoteResolverTests"
```
Expected: all pass. If `ChatConnectionState` member names differ from what's used above, adjust both production and test code to match.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantVoteResolver.cs tests/Game/DecisionVotes/ActVariantVoteResolverTests.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/4.1: ActVariantVoteResolver.ShouldBail + 5 BailReason values

Pure-CLR bail policy covering SettingsOff, Multiplayer, ChatUnreadable,
Act1Pinned, PoolDegenerate. Ordinal comparison for "random" matches
vanilla's StartRunLobby.GetAct decoder semantics.

ResumeInProgress and VoteInProgress are NOT in the enum — they require
atomic-acquire semantics that pure functions can't express. Those
bails are handled inline in ActVariantVotePatch.Prefix and verified by
operator gates 7 + 12.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `ActVariantReceiptFormatter` — custom-formatter helper + tests

**Goal:** Extract the `formatReceipt` callback into a pure-CLR static method so it's testable independently of the Harmony patch. Substitutes the no-votes close text and side-channels via an `Action onNoVotes` callback.

**Files:**
- Create: `src/Game/DecisionVotes/ActVariantReceiptFormatter.cs`
- Create: `tests/Game/DecisionVotes/ActVariantReceiptFormatterTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/Game/DecisionVotes/ActVariantReceiptFormatterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public sealed class ActVariantReceiptFormatterTests {

    private static VoteSnapshot MakeSnapshot(
            bool noVotes = false,
            int? winnerIndex = null,
            int? randomTieAmong = null,
            int voteId = 7) {
        var options = new[] {
            new VoteOption(0, "Overgrowth"),
            new VoteOption(1, "Underdocks"),
        };
        var tallies = new Dictionary<int, int> { { 0, noVotes ? 0 : 5 }, { 1, noVotes ? 0 : 3 } };
        return new VoteSnapshot(
            VoteId: voteId,
            Label: "Act 1 variant",
            Options: options,
            Tallies: tallies,
            Duration: TimeSpan.FromSeconds(30),
            TimeRemaining: TimeSpan.Zero,
            WinnerIndex: winnerIndex,
            NoVotesReceived: noVotes,
            RandomTieAmong: randomTieAmong,
            DisconnectGap: TimeSpan.Zero);
    }

    [Fact]
    public void Close_with_no_votes_returns_custom_text_and_invokes_onNoVotes() {
        var snap = MakeSnapshot(noVotes: true, winnerIndex: 0);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Close, () => called = true);
        Assert.Contains("no votes received", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vanilla random pick stands", result, StringComparison.OrdinalIgnoreCase);
        Assert.True(called);
    }

    [Fact]
    public void Close_with_winner_delegates_to_EnglishReceipts_FormatClose() {
        var snap = MakeSnapshot(noVotes: false, winnerIndex: 0);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Close, () => called = true);
        Assert.Equal(EnglishReceipts.FormatClose(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void Open_always_delegates_to_EnglishReceipts_FormatOpen() {
        var snap = MakeSnapshot(noVotes: false);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.Open, () => called = true);
        Assert.Equal(EnglishReceipts.FormatOpen(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void PeriodicTally_always_delegates_to_EnglishReceipts_FormatPeriodicTally() {
        var snap = MakeSnapshot(noVotes: false);
        bool called = false;
        var result = ActVariantReceiptFormatter.Format(snap, ReceiptKind.PeriodicTally, () => called = true);
        Assert.Equal(EnglishReceipts.FormatPeriodicTally(snap), result);
        Assert.False(called);
    }

    [Fact]
    public void Open_with_no_votes_does_NOT_invoke_onNoVotes() {
        // NoVotesReceived can be true mid-vote on session open if chat is empty;
        // only Close should trigger the side-channel.
        var snap = MakeSnapshot(noVotes: true);
        bool called = false;
        ActVariantReceiptFormatter.Format(snap, ReceiptKind.Open, () => called = true);
        Assert.False(called);
    }
}
```

- [ ] **Step 2: Verify `VoteSnapshot` shape matches tests above**

Open [`src/Ti/Voting/VoteSnapshot.cs`](../../../src/Ti/Voting/VoteSnapshot.cs). Confirm constructor parameter order and names match the `MakeSnapshot` helper. If `VoteSnapshot` is a `record` with different ordering, update the helper. If a parameter doesn't exist (e.g., `DisconnectGap`), remove it from the test.

- [ ] **Step 3: Run tests to confirm they fail**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantReceiptFormatterTests"
```
Expected: build error (`ActVariantReceiptFormatter does not exist`).

- [ ] **Step 4: Write `ActVariantReceiptFormatter.cs`**

```csharp
using System;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Custom formatter passed to VoteCoordinator.Start(formatReceipt: ...) for
/// the act-variant vote. Substitutes accurate close-receipt text when no
/// votes are received (the generic FormatClose would falsely claim chat
/// chose a random option). All other ReceiptKind cases delegate to
/// EnglishReceipts.
///
/// The onNoVotes callback is invoked when the no-votes Close branch fires,
/// side-channeling the outcome to the patch's HandleVoteAsync so it can
/// distinguish "no votes → preserve vanilla random pick" from "real winner"
/// without requiring a public VoteSession.Snapshot accessor (which doesn't
/// exist in the current Ti layer).
/// </summary>
internal static class ActVariantReceiptFormatter {

    internal const string NoVotesCloseText =
        "Act 1 variant vote closed: no votes received — vanilla random pick stands.";

    internal static string Format(VoteSnapshot snapshot, ReceiptKind kind, Action onNoVotes) {
        if (kind == ReceiptKind.Close && snapshot.NoVotesReceived) {
            onNoVotes();
            return NoVotesCloseText;
        }
        return kind switch {
            ReceiptKind.Open          => EnglishReceipts.FormatOpen(snapshot),
            ReceiptKind.PeriodicTally => EnglishReceipts.FormatPeriodicTally(snapshot),
            ReceiptKind.Close         => EnglishReceipts.FormatClose(snapshot),
            _                         => EnglishReceipts.FormatClose(snapshot),
        };
    }
}
```

- [ ] **Step 5: Run formatter tests, expect 5 passing**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ActVariantReceiptFormatterTests"
```
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantReceiptFormatter.cs tests/Game/DecisionVotes/ActVariantReceiptFormatterTests.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/5.1: ActVariantReceiptFormatter — no-votes substitution

Static helper invoked as the formatReceipt callback in
VoteCoordinator.Start. For ReceiptKind.Close with NoVotesReceived,
returns custom text and fires onNoVotes callback (side-channels the
outcome to HandleVoteAsync). All other cases delegate to
EnglishReceipts generic formatters.

5 unit tests under [Collection("TiLog.Sink")] cover no-votes branch,
delegation for winner/open/tally, and that Open with no votes does NOT
fire onNoVotes (only Close should).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Settings field additions

**Goal:** Add `VoteOnActVariant` (default true) and `ForceL3PopupFallback` (default false) to `ModSettings`. No schema bump — both are optional fields with defaults.

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs`

- [ ] **Step 1: Read existing `ModSettings.cs` to find the loader path**

Open [`src/Game/Bootstrap/ModSettings.cs`](../../../src/Game/Bootstrap/ModSettings.cs). Locate the section that parses optional bool fields (e.g., `voteOnNeow`, `voteOnBoss` — they follow a pattern of `root.TryGetProperty(...)` + `.GetBoolean()` with a default fallback). Note the existing pattern's shape.

- [ ] **Step 2: Add `VoteOnActVariant` field + parse logic**

Locate the `ChatSettings` record (around line 10) and add the field. If the record signature is currently `(string Channel, ChatCredentials Credentials, int CardSkipsPerAct, string? YoutubeChannelId)`, this requires evaluating whether to extend the record or use a different mechanism. Check what existing slices (B.1, B.3) did for their `voteOnNeow` / `voteOnBoss` toggles — if those toggles ARE in `ChatSettings`, follow the same pattern; if they're stored elsewhere (e.g., a settings singleton), use that.

If extending `ChatSettings`:
```csharp
public sealed record ChatSettings(
    string Channel,
    ChatCredentials Credentials,
    int CardSkipsPerAct,
    string? YoutubeChannelId,
    bool VoteOnActVariant = true,        // NEW
    bool ForceL3PopupFallback = false);  // NEW
```

In the parse path, after the existing `cardSkipsPerAct` block (around line 78):
```csharp
bool voteOnActVariant = true;
if (root.TryGetProperty("voteOnActVariant", out var voteActProp)) {
    if (voteActProp.ValueKind == JsonValueKind.True) voteOnActVariant = true;
    else if (voteActProp.ValueKind == JsonValueKind.False) voteOnActVariant = false;
    else warnings.Add("voteOnActVariant is not a boolean; using default (true)");
}

bool forceL3PopupFallback = false;
if (root.TryGetProperty("forceL3PopupFallback", out var forceL3Prop)) {
    if (forceL3Prop.ValueKind == JsonValueKind.True) forceL3PopupFallback = true;
    else if (forceL3Prop.ValueKind == JsonValueKind.False) forceL3PopupFallback = false;
    else warnings.Add("forceL3PopupFallback is not a boolean; using default (false)");
}
```

And include them when constructing the success result:
```csharp
return new SettingsResult.Success(
    new ChatSettings(normalisedChannel, credentials, cardSkipsPerAct, youtubeChannelId, voteOnActVariant, forceL3PopupFallback),
    warnings);
```

If `voteOnNeow` etc. live in a different settings container (e.g., a `VoteSettings` peer record), use that pattern instead. The plan author cannot fully prescribe the exact merge point without knowing how prior toggles landed; **read the existing code first** and follow the established pattern.

- [ ] **Step 3: Build to confirm settings load compiles**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds. If errors, check `ChatSettings` constructor usages elsewhere (`grep -rn "new ChatSettings(" src/`) and ensure each call site supplies the new positional args or uses default values.

- [ ] **Step 4: Quick smoke — write a unit test that confirms defaults**

Open existing settings tests (e.g., `tests/Game/Bootstrap/ModSettingsTests.cs` if it exists). Add:

```csharp
[Fact]
public void Load_omits_voteOnActVariant_defaults_to_true() {
    var settings = LoadFromJson("{ \"schemaVersion\": 1, \"channel\": \"x\", \"username\": \"x\", \"oauthToken\": \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\" }");
    var success = Assert.IsType<SettingsResult.Success>(settings);
    Assert.True(success.Settings.VoteOnActVariant);
    Assert.False(success.Settings.ForceL3PopupFallback);
}
```

If no existing settings test file exists, skip this step — the load path is exercised at mod startup; smoke validation in operator gates suffices.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Bootstrap/ModSettings.cs tests/Game/Bootstrap/ModSettingsTests.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/6.1: ModSettings — VoteOnActVariant + ForceL3PopupFallback

Two optional bool fields with defaults (true / false). No schema bump.
Parse path follows the existing voteOnNeow/voteOnBoss pattern; non-bool
values fall back to default with a warnings entry.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `ActVariantPrewarmResult` + `PreWarmAssets` (Patch-side helper)

**Goal:** Pre-warm both variants' asset textures synchronously on the main thread BEFORE the popup opens. Return an `ActVariantPrewarmResult` carrying the L1/L3 mode that flows into the popup constructor. `ForceL3PopupFallback` short-circuits with a normalized log line.

**Files:**
- Modify: `src/Game/DecisionVotes/ActVariantVoteResolver.cs` — add `ActVariantPrewarmResult` record.
- Create: `src/Game/DecisionVotes/ActVariantVotePatch.cs` (skeleton — just the pre-warm method for now; full patch lands in Tasks 8-9)

- [ ] **Step 1: Add `ActVariantPrewarmResult` to resolver file**

Append to `src/Game/DecisionVotes/ActVariantVoteResolver.cs` outside the static class:

```csharp
/// <summary>
/// Outcome of synchronous pre-warm. Mode flows into ActVariantVotePopup
/// constructor; Succeeded/Total/ElapsedMs feed pre-warm telemetry log.
/// </summary>
internal readonly record struct ActVariantPrewarmResult(
    SlayTheStreamer2.Game.Ui.ActVariantPopupMode Mode,
    int Succeeded,
    int Total,
    long ElapsedMs);
```

- [ ] **Step 2: Create `ActVariantVotePatch.cs` with `PreWarmAssets` only**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

// Full Harmony attribute and Prefix/PrefixContinue/HandleVoteAsync/Resume
// land in Tasks 8-9. This file currently hosts only the pre-warm helper.
internal static class ActVariantVotePatch {

    private static ActVariantPrewarmResult PreWarmAssets(
            IReadOnlyList<ActVariantOption> candidates,
            bool forceL3) {
        var sw = Stopwatch.StartNew();

        if (forceL3) {
            sw.Stop();
            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: 0/0 assets in {sw.ElapsedMilliseconds}ms (mode=L3, reason=ForceL3PopupFallback)");
            return new ActVariantPrewarmResult(ActVariantPopupMode.L3Fallback, 0, 0, sw.ElapsedMilliseconds);
        }

        int succeeded = 0, total = 0;
        foreach (var option in candidates) {
            foreach (var path in new[] { option.BackgroundPath, option.BannerPath }) {
                if (string.IsNullOrEmpty(path)) continue;
                total++;
                try {
                    _ = MegaCrit.Sts2.Core.Assets.PreloadManager.Cache.GetTexture(path);
                    succeeded++;
                } catch (Exception ex) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] preload failed for {option.Key} ({path}): {ex.Message}");
                }
            }
        }
        sw.Stop();
        var mode = (total > 0 && succeeded == total)
            ? ActVariantPopupMode.L1Textures
            : ActVariantPopupMode.L3Fallback;
        var reason = mode == ActVariantPopupMode.L1Textures ? "all assets loaded"
                   : total == 0 ? "no paths configured"
                   : $"{succeeded}/{total} assets loaded";
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] pre-warm: {succeeded}/{total} assets in {sw.ElapsedMilliseconds}ms (mode={mode}, reason={reason})");
        return new ActVariantPrewarmResult(mode, succeeded, total, sw.ElapsedMilliseconds);
    }
}
```

- [ ] **Step 3: Build the mod project to verify imports resolve**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds. If `PreloadManager.Cache.GetTexture` doesn't exist on the verified API surface (Task 1 Step 1), adjust based on spike findings — either rename to `GetTexture2D`, use a different cache accessor, or fall back to `ResourceLoader.Load<Texture2D>` directly.

- [ ] **Step 4: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantVoteResolver.cs src/Game/DecisionVotes/ActVariantVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/7.1: ActVariantPrewarmResult + PreWarmAssets

Synchronous main-thread pre-warm of all 4 candidate textures. Returns
ActVariantPrewarmResult with L1/L3 mode for popup propagation.
ForceL3PopupFallback short-circuits and logs a reason-tagged normalized
line so operator validation can see why textures are absent.

Per-asset try/catch — one missing texture doesn't kill the others.
Telemetry envelope target ≤100ms per spec Gate 8.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: `ActVariantVotePatch` — Harmony target, Prefix, bail order

**Goal:** Wire the Harmony attribute and `Prefix` bail order (atomic acquire + `ShouldBail` delegation). PrefixContinue / HandleVoteAsync / ResumeOnMainThread come in Task 9.

**Files:**
- Modify: `src/Game/DecisionVotes/ActVariantVotePatch.cs`

- [ ] **Step 1: Add Harmony attribute and patch-class skeleton**

Replace the top of `ActVariantVotePatch.cs` (above `PreWarmAssets`) with:

```csharp
[HarmonyPatch(typeof(StartRunLobby), "BeginRunLocally",
              new[] { typeof(string), typeof(List<ModifierModel>) })]
internal static class ActVariantVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;   // intentional process-lifetime suppression

    private static readonly Lazy<MethodInfo?> _beginRunLocallyMethod =
        new(() => AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally",
                                     new[] { typeof(string), typeof(List<ModifierModel>) }));

    private sealed class PendingActVariantVote {
        public int Cancelled;
        public int NoVotes;
    }

    static bool Prepare(MethodBase? original) {
        if (original is null) {
            if (_beginRunLocallyMethod.Value is null) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] hard check failed: StartRunLobby.BeginRunLocally(string, List<ModifierModel>) not found via reflection; patch will not register");
                return false;
            }
            return true;
        }
        var parameters = original.GetParameters();
        if (parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(string) ||
            parameters[1].ParameterType != typeof(List<ModifierModel>)) {
            TiLog.Error($"[SlayTheStreamer2][act-variant-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}");
            return false;
        }
        TiLog.Info($"[SlayTheStreamer2][act-variant-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
        return true;
    }
```

- [ ] **Step 2: Add `Prefix` method with bail order**

After `Prepare`, before `PreWarmAssets`:

```csharp
    static bool Prefix(StartRunLobby __instance, string seed, List<ModifierModel> modifiers) {
        // 1. Synthetic resume passes through.
        if (_resumeInProgress == 1) return true;

        // 2. Atomic acquire — moved up from v1 to close the chat-disconnect race
        //    where a click-during-vote could bail at the chat-readable check and
        //    let vanilla through while a vote is still in flight.
        if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
            TiLog.Debug("[SlayTheStreamer2][act-variant-vote] repeat click during open vote; suppressed");
            return false;
        }

        try {
            int playerCount = TryGetPlayerCount(__instance) ?? 1;
            var coordinator = Voter.Default;
            var chatState = coordinator?.Chat.State ?? ChatConnectionState.Disconnected;
            var candidates = ActVariantVoteResolver.BuildCandidates();

            var reason = ActVariantVoteResolver.ShouldBail(
                settingsEnabled: ModSettings.Current?.VoteOnActVariant ?? true,
                playerCount: playerCount,
                chatState: chatState,
                act1Value: __instance.Act1 ?? "random",
                candidateCount: candidates.Count);

            if (reason is not ActVariantVoteResolver.BailReason.None) {
                LogBailAndRelease(reason, __instance, playerCount);
                return true;
            }

            // ShouldBail.None guarantees coordinator is non-null (chat readable
            // was a precondition).
            return PrefixContinue(__instance, seed, modifiers, candidates, coordinator!);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] Prefix threw; bailing to vanilla", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }

    private static int? TryGetPlayerCount(StartRunLobby instance) {
        try { return instance?.Players?.Count; } catch { return null; }
    }

    private static void LogBailAndRelease(
            ActVariantVoteResolver.BailReason reason,
            StartRunLobby instance,
            int playerCount) {
        switch (reason) {
            case ActVariantVoteResolver.BailReason.SettingsOff:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] settings off; bailing to vanilla");
                break;
            case ActVariantVoteResolver.BailReason.Multiplayer:
                if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
                    TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] multiplayer detected (Players.Count={playerCount}); bailing to vanilla");
                }
                break;
            case ActVariantVoteResolver.BailReason.ChatUnreadable:
                TiLog.Debug("[SlayTheStreamer2][act-variant-vote] chat not readable; bailing to vanilla");
                break;
            case ActVariantVoteResolver.BailReason.Act1Pinned:
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] Act1 explicitly pinned ({instance.Act1}); skipping vote");
                break;
            case ActVariantVoteResolver.BailReason.PoolDegenerate:
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] degenerate pool; bailing to vanilla");
                break;
        }
        Interlocked.Exchange(ref _voteInProgress, 0);
    }

    // Stub — full implementation in Task 9.
    private static bool PrefixContinue(
            StartRunLobby instance, string seed, List<ModifierModel> modifiers,
            IReadOnlyList<ActVariantOption> candidates, VoteCoordinator coordinator) {
        TiLog.Warn("[SlayTheStreamer2][act-variant-vote] PrefixContinue stub — falling through to vanilla");
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }
```

- [ ] **Step 3: Build to confirm types resolve**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds.

- [ ] **Step 4: Run all existing tests to confirm no regression**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj
```
Expected: all tests pass (existing B.1/B.2/B.3 tests + new B.3.2 tests from Tasks 3-5).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/8.1: Harmony attribute + Prefix bail order

Patches private StartRunLobby.BeginRunLocally(string,
List<ModifierModel>) — resolved via AccessTools.Method with cached
Lazy<MethodInfo?>. Prepare-time hard check fails patch registration if
the method signature isn't found.

Bail order (v3 final):
  1. _resumeInProgress passthrough
  2. _voteInProgress atomic acquire (moved up to close
     chat-disconnect race per Reviewer R2-1 v1 + meta-review)
  3-7. delegated to ActVariantVoteResolver.ShouldBail

PrefixContinue currently stubs to vanilla — full suspend-and-resume
lands in plan-b-3-2/9.1.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: `PrefixContinue` + `HandleVoteAsync` + `ResumeOnMainThread`

**Goal:** Wire the full suspend-and-resume flow including the `formatReceipt` callback, popup construction, cancellation-dominates-no-votes ordering, one-shot `Act1` restoration, and the spike-conditional fallback re-invoke.

**Files:**
- Modify: `src/Game/DecisionVotes/ActVariantVotePatch.cs`

- [ ] **Step 1: Replace the `PrefixContinue` stub with the full implementation**

Replace the stub method with:

```csharp
    private static bool PrefixContinue(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> modifiers,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator) {

        // Defensive modifier copy so the resumed run is deterministic against
        // any UI mutation during the 30s vote window.
        var capturedModifiers = modifiers.ToList();

        var prewarm = PreWarmAssets(
            candidates,
            forceL3: ModSettings.Current?.ForceL3PopupFallback ?? false);

        var pending = new PendingActVariantVote();

        Func<VoteSnapshot, ReceiptKind, string> formatReceipt = (snap, kind) =>
            ActVariantReceiptFormatter.Format(snap, kind, () =>
                Interlocked.Exchange(ref pending.NoVotes, 1));

        VoteSession? session = null;
        try {
            var labels = candidates.Select(c => c.Title).ToList();
            session = coordinator.Start(
                label: "Act 1 variant",
                options: labels,
                duration: TimeSpan.FromSeconds(30),
                receipts: null,
                parsing: null,
                formatReceipt: formatReceipt);

            Func<bool> shouldCancel = () => IsRunStartAbandoned(instance);
            Action onUserAbandoned = () => Interlocked.Exchange(ref pending.Cancelled, 1);

            var popup = new ActVariantVotePopup(
                options: candidates,
                session: session,
                dispatcher: coordinator.Dispatcher,
                mode: prewarm.Mode,
                shouldCancel: shouldCancel,
                onUserAbandoned: onUserAbandoned);
            coordinator.Dispatcher.Post(() => popup.Open());

            _ = HandleVoteAsync(instance, seed, capturedModifiers, session, candidates, coordinator, pending);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] PrefixContinue threw; cancelling session", ex);
            try { session?.Cancel(); } catch { /* swallow */ }
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
        return false;
    }

    private static bool IsRunStartAbandoned(StartRunLobby instance) {
        // TODO(spike): replace with chosen probe from notes/B3-2-spike-2026-05-18.md
        // Step 4. Examples: NCharacterSelectScreen.Instance == null, scene-tree
        // identity check, etc.
        try { return /* spike-output probe */ false; } catch { return false; }
    }
```

The `/* spike-output probe */ false` placeholder must be replaced with the actual expression from Task 1 Step 4. If the spike output is `NCharacterSelectScreen.Instance == null`, the body becomes:
```csharp
try { return NCharacterSelectScreen.Instance == null; } catch { return false; }
```

- [ ] **Step 2: Add `HandleVoteAsync`**

After `IsRunStartAbandoned`:

```csharp
    private static async Task HandleVoteAsync(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> capturedModifiers,
            VoteSession session,
            IReadOnlyList<ActVariantOption> candidates,
            VoteCoordinator coordinator,
            PendingActVariantVote pending) {
        try {
            coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

            int? winnerIndex = null;
            try {
                int idx = await session.AwaitWinnerAsync();
                if (idx >= 0 && idx < candidates.Count) winnerIndex = idx;
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] AwaitWinnerAsync threw", ex);
            }

            // Cancellation dominates no-votes in receipt ordering.
            bool cancelled = Volatile.Read(ref pending.Cancelled) == 1;
            bool noVotes = Volatile.Read(ref pending.NoVotes) == 1;

            string winnerKey;
            if (cancelled) {
                winnerKey = "random";
            } else if (noVotes) {
                winnerKey = "random";
                // No-votes receipt already sent by formatReceipt callback during
                // session close — no additional send needed.
            } else {
                winnerKey = ActVariantVoteResolver.ResolveWinnerKey(candidates, winnerIndex);
            }

            TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: winnerKey={winnerKey} (cancelled={cancelled}, noVotes={noVotes}, seed={seed})");

            coordinator.Dispatcher.Post(() =>
                ResumeOnMainThread(instance, seed, capturedModifiers, winnerKey, cancelled));
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] HandleVoteAsync threw; fallback resume", ex);
            try {
                coordinator.Dispatcher.Post(() =>
                    ResumeOnMainThread(instance, seed, capturedModifiers, "random", cancelled: true));
            } catch (Exception postEx) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback Post threw; resetting flags", postEx);
                Interlocked.Exchange(ref _resumeInProgress, 0);
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        }
    }
```

- [ ] **Step 3: Add `ResumeOnMainThread`**

After `HandleVoteAsync`:

```csharp
    private static void ResumeOnMainThread(
            StartRunLobby instance,
            string seed,
            List<ModifierModel> capturedModifiers,
            string winnerKey,
            bool cancelled) {
        Interlocked.Exchange(ref _resumeInProgress, 1);
        string? previousAct1 = null;
        try {
            if (cancelled) {
                TiLog.Info("[SlayTheStreamer2][act-variant-vote] resume: vote cancelled; aborting without re-invoke");
                SendCancellationReceipt();
                return;
            }

            previousAct1 = instance.Act1;

            if (!string.Equals(winnerKey, "random", StringComparison.Ordinal)) {
                instance.Act1 = winnerKey;
                TiLog.Info($"[SlayTheStreamer2][act-variant-vote] resume: Act1 = {winnerKey} (previous: {previousAct1})");
            }

            var method = _beginRunLocallyMethod.Value;
            if (method is null) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] _beginRunLocallyMethod is null; cannot re-invoke");
                return;
            }

            try {
                method.Invoke(instance, new object?[] { seed, capturedModifiers });
            } catch (TargetInvocationException tie) {
                // Fallback re-invoke is SPIKE-CONDITIONAL. If notes/B3-2-spike-2026-05-18.md
                // Step 2 found BeginRunLocally NOT idempotent (mutates state before line 411),
                // DELETE this catch block before ship — accept that player must restart on
                // rare reflection failures rather than risk double-mutation.
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] re-invoke threw; attempting fallback (spike-gated)",
                    tie.InnerException ?? tie);
                winnerKey = "random";   // align with finally so restoration is consistent
                try {
                    instance.Act1 = "random";
                    method.Invoke(instance, new object?[] { seed, capturedModifiers });
                } catch (TargetInvocationException fallbackTie) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw; player may be soft-locked",
                        fallbackTie.InnerException ?? fallbackTie);
                } catch (Exception fallbackEx) {
                    TiLog.Error("[SlayTheStreamer2][act-variant-vote] fallback re-invoke threw (non-reflection)", fallbackEx);
                }
            }
        } catch (TargetInvocationException tie) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw (reflection)", tie.InnerException ?? tie);
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] resume threw", ex);
        } finally {
            if (previousAct1 is not null
                    && !string.Equals(winnerKey, "random", StringComparison.Ordinal)) {
                try { instance.Act1 = previousAct1; } catch { /* swallow */ }
            }
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }

    private static void SendCancellationReceipt() {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        _ = coordinator.Chat.SendMessageAsync(
            "Act 1 variant vote cancelled — run-start abandoned.",
            OutgoingMessagePriority.Normal);
    }
```

- [ ] **Step 4: If spike Step 2 found `BeginRunLocally` NOT idempotent, delete the fallback re-invoke**

Read `notes/B3-2-spike-2026-05-18.md` Step 2. If it said "idempotent" or "safe to retry", leave the fallback as written. If it said "NOT idempotent" or "mutates state X before line 411", delete the `catch (TargetInvocationException tie) { ... }` block from `ResumeOnMainThread` (lines starting `// Fallback re-invoke is SPIKE-CONDITIONAL`) — replace with:

```csharp
            } catch (TargetInvocationException tie) {
                TiLog.Error("[SlayTheStreamer2][act-variant-vote] re-invoke threw; NOT retrying (BeginRunLocally is not idempotent per spike); player may be soft-locked",
                    tie.InnerException ?? tie);
            }
```

- [ ] **Step 5: Replace `IsRunStartAbandoned` placeholder with spike output**

Edit the body to use the actual probe expression. If spike Step 4 chose `NCharacterSelectScreen.Instance == null`:

```csharp
    private static bool IsRunStartAbandoned(StartRunLobby instance) {
        try { return MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen.Instance == null; }
        catch { return false; }
    }
```

(Adjust the namespace path to match the decompile.)

- [ ] **Step 6: Build to confirm full patch compiles**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds. Will fail if `ActVariantVotePopup` (referenced from `PrefixContinue`) doesn't exist yet — temporarily comment out the `new ActVariantVotePopup(...)` block + the `coordinator.Dispatcher.Post(() => popup.Open())` line with `// TODO(task10): popup wiring` and uncomment in Task 10.

- [ ] **Step 7: Commit**

```bash
git add src/Game/DecisionVotes/ActVariantVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/9.1: PrefixContinue + HandleVoteAsync + ResumeOnMainThread

Full suspend-and-resume flow:
- Custom formatReceipt callback via ActVariantReceiptFormatter for
  no-votes substitution + side-channel.
- Defensive modifier copy at prefix time.
- Cancellation dominates no-votes in HandleVoteAsync.
- Resume writes Act1 in try, restores in finally (one-shot semantics).
- Spike-gated fallback re-invoke with Act1=random alignment.
- TargetInvocationException unwrap throughout for cleaner diagnostics.

IsRunStartAbandoned probe wired to spike Step 4 finding.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: `ActVariantVotePopup` — node tree, L1/L3 branching

**Goal:** Build the popup as a self-owned `CanvasLayer` with a vertical 50/50 split, native-size combat backgrounds (L1) or hex-color rects + title labels (L3). Fully MegaCrit-free interface and implementation.

**Files:**
- Create: `src/Game/Ui/ActVariantVotePopup.cs`

- [ ] **Step 1: Create the popup class with constructor + Open()**

```csharp
using System;
using System.Collections.Generic;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Vertical 50/50 split popup for the act-variant vote. Renders L1 (native-
/// size combat backgrounds + entry banners) or L3 (hex-color rects + title
/// labels) based on the mode parameter from PreWarmAssets. Fully MegaCrit-
/// free; cancellation probe is injected via Func<bool> shouldCancel from
/// the patch.
/// </summary>
internal sealed partial class ActVariantVotePopup : Control {
    private readonly IReadOnlyList<ActVariantOption> _options;
    private readonly VoteSession _session;
    private readonly Ti.Internal.IMainThreadDispatcher _dispatcher;
    private readonly ActVariantPopupMode _mode;
    private readonly Func<bool> _shouldCancel;
    private readonly Action _onUserAbandoned;

    private CanvasLayer? _canvasLayer;
    private bool _userAbandoned;
    private Label[] _tallyLabels = Array.Empty<Label>();

    private const int CanvasLayerIndex = 100;
    private const float BackdropAlpha = 0.6f;

    public ActVariantVotePopup(
            IReadOnlyList<ActVariantOption> options,
            VoteSession session,
            Ti.Internal.IMainThreadDispatcher dispatcher,
            ActVariantPopupMode mode,
            Func<bool> shouldCancel,
            Action onUserAbandoned) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _mode = mode;
        _shouldCancel = shouldCancel ?? throw new ArgumentNullException(nameof(shouldCancel));
        _onUserAbandoned = onUserAbandoned ?? throw new ArgumentNullException(nameof(onUserAbandoned));
    }

    /// <summary>
    /// Builds the CanvasLayer tree and attaches it to the gameplay-area
    /// surface. Must be called on the Godot main thread.
    /// </summary>
    public void Open() {
        try {
            _canvasLayer = BuildNodeTree();
            var parent = GetGameplayAreaParent();
            parent.AddChild(_canvasLayer);
            _session.TallyChanged += OnTally;
            _session.Closed += OnClosed;
            TiLog.Debug($"[SlayTheStreamer2][act-variant-vote] popup opened (mode={_mode})");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][act-variant-vote] popup Open threw", ex);
        }
    }

    private Node GetGameplayAreaParent() {
        // TODO(spike): replace with the gameplay-area Control identified in
        // notes/B3-2-spike-2026-05-18.md Step 5. Mirror BossVotePopup's pattern.
        return GetTree().Root;  // placeholder — most likely needs to be a specific Control
    }
```

The `GetGameplayAreaParent` placeholder must be replaced with the actual parent target from Task 1 Step 5. If `BossVotePopup` parents to `GetTree().Root` directly and that works at all 3 tested resolutions, leave the placeholder. If it parents to a specific `Control`, mirror that target.

- [ ] **Step 2: Add the node-tree builder (`BuildNodeTree`)**

Continue the class with:

```csharp
    private CanvasLayer BuildNodeTree() {
        var layer = new CanvasLayer { Layer = CanvasLayerIndex };

        var backdrop = new ColorRect {
            Color = new Color(0f, 0f, 0f, BackdropAlpha),
            MouseFilter = MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.AddChild(backdrop);

        var hbox = new HBoxContainer { CustomMinimumSize = Vector2.Zero };
        hbox.AddThemeConstantOverride("separation", 0);
        hbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer.AddChild(hbox);

        _tallyLabels = new Label[_options.Count];

        for (int i = 0; i < _options.Count; i++) {
            var column = BuildColumn(_options[i], i);
            hbox.AddChild(column);
        }

        return layer;
    }

    private PanelContainer BuildColumn(ActVariantOption option, int columnIndex) {
        // PanelContainer is a Container that would normally arrange children
        // sequentially. We insert a free-positioning Control as its sole child
        // to opt out of container layout and allow overlay placement.
        var column = new PanelContainer {
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };

        var free = new Control { MouseFilter = MouseFilterEnum.Ignore };
        free.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        column.AddChild(free);

        bool l1 = _mode == ActVariantPopupMode.L1Textures
               && !string.IsNullOrEmpty(option.BackgroundPath);

        if (l1) {
            AddL1Background(free, option.BackgroundPath!);
            if (!string.IsNullOrEmpty(option.BannerPath))
                AddL1Banner(free, option.BannerPath!);
        } else {
            AddL3Fallback(free, option);
        }

        var tally = new Label {
            Text = $"#{option.Index} — 0 votes",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tally.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        tally.OffsetTop = -80;
        tally.OffsetLeft = -150;
        tally.OffsetRight = 150;
        free.AddChild(tally);
        _tallyLabels[columnIndex] = tally;

        return column;
    }

    private void AddL1Background(Control parent, string path) {
        var tex = ResourceLoader.Load<Texture2D>(path);
        var rect = new TextureRect {
            Texture = tex,
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        parent.AddChild(rect);
    }

    private void AddL1Banner(Control parent, string path) {
        var tex = ResourceLoader.Load<Texture2D>(path);
        var rect = new TextureRect {
            Texture = tex,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // Top-center anchor at column_width / 2, gameplay_height / 3.
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        rect.OffsetTop = GetViewportRect().Size.Y / 3f;
        parent.AddChild(rect);
    }

    private void AddL3Fallback(Control parent, ActVariantOption option) {
        var rect = new ColorRect {
            Color = ParseHex(option.FallbackColorHex),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        parent.AddChild(rect);

        var title = new Label {
            Text = option.Title,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        title.OffsetLeft = -200;
        title.OffsetRight = 200;
        title.OffsetTop = -30;
        title.OffsetBottom = 30;
        // TODO: scale font for large readable text — borrow from VoteTallyLabel
        // or use a theme override.
        parent.AddChild(title);
    }

    private static Color ParseHex(string rrggbb) {
        // RRGGBB format only — no '#', no alpha. ParseInt is locale-independent.
        if (rrggbb.Length != 6) return new Color(0.5f, 0.5f, 0.5f, 1f);
        try {
            int r = Convert.ToInt32(rrggbb.Substring(0, 2), 16);
            int g = Convert.ToInt32(rrggbb.Substring(2, 2), 16);
            int b = Convert.ToInt32(rrggbb.Substring(4, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        } catch {
            return new Color(0.5f, 0.5f, 0.5f, 1f);
        }
    }
```

- [ ] **Step 3: Build to confirm popup compiles**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds.

- [ ] **Step 4: Uncomment the popup wiring in Task 9's PrefixContinue (if it was commented out)**

If Task 9 Step 6 required commenting out the `new ActVariantVotePopup(...)` block, restore it now that the type exists.

- [ ] **Step 5: Build the full mod project**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Game/Ui/ActVariantVotePopup.cs src/Game/DecisionVotes/ActVariantVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/10.1: ActVariantVotePopup — node tree + L1/L3 branching

CanvasLayer + ColorRect backdrop + HBoxContainer with two
PanelContainer columns. Each column wraps a free-positioning Control
(opts out of PanelContainer's sequential layout) inside which:
- L1: TextureRect(background, KeepCentered) + TextureRect(banner at
  screen_height/3) + tally Label.
- L3: ColorRect(FallbackColorHex) + centered title Label + tally Label.

Mode chosen by PreWarmAssets result; per-column also degrades to L3 if
its specific BackgroundPath is null.

ParseHex helper accepts RRGGBB-only format (no '#', no alpha) matching
the FallbackColorHex contract.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: Popup lifecycle — TallyChanged, _Process, _Input ESC, Closed

**Goal:** Wire the popup's runtime behavior — tally updates, abandonment polling, ESC handling, and cleanup on session close.

**Files:**
- Modify: `src/Game/Ui/ActVariantVotePopup.cs`

- [ ] **Step 1: Add `OnTally`, `_Process`, `_Input`, `OnClosed`**

Append to `ActVariantVotePopup`:

```csharp
    private void OnTally(object? sender, VoteSession session) {
        if (_userAbandoned) return;
        try {
            var tallies = session.Tallies;
            for (int i = 0; i < _options.Count && i < _tallyLabels.Length; i++) {
                int count = tallies.TryGetValue(_options[i].Index, out var c) ? c : 0;
                _tallyLabels[i].Text = $"#{_options[i].Index} — {count} votes";
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][act-variant-vote] OnTally threw: {ex.Message}");
        }
    }

    public override void _Process(double delta) {
        if (_userAbandoned) return;
        if (_shouldCancel()) {
            _userAbandoned = true;
            try { _onUserAbandoned(); } catch { /* swallow */ }
            try { _session.Cancel(); } catch { /* swallow — spike #9 confirmed idempotent */ }
        }
    }

    public override void _Input(InputEvent @event) {
        // _Input fires BEFORE _UnhandledInput; ensures popup gets ESC even if a
        // parent control would have consumed it via _UnhandledInput.
        if (_userAbandoned) return;
        if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape }) {
            _userAbandoned = true;
            try { _onUserAbandoned(); } catch { /* swallow */ }
            try { _session.Cancel(); } catch { /* swallow */ }
            GetViewport().SetInputAsHandled();
        }
    }

    private void OnClosed(object? sender, VoteSession session) {
        try {
            _session.TallyChanged -= OnTally;
            _session.Closed -= OnClosed;
        } catch { /* swallow */ }
        if (_canvasLayer is not null && GodotObject.IsInstanceValid(_canvasLayer)) {
            _canvasLayer.QueueFree();
            _canvasLayer = null;
        }
    }
```

- [ ] **Step 2: Build to confirm popup is complete**

```powershell
dotnet build src/slay_the_streamer_2.csproj
```
Expected: succeeds.

- [ ] **Step 3: Run all tests to confirm no regression**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj
```
Expected: all tests pass.

- [ ] **Step 4: If spike #9 found `Cancel()` NOT idempotent, add a local guard**

Read `notes/B3-2-spike-2026-05-18.md` Step 9. If `Cancel()` is NOT idempotent, add a `private bool _cancelCalled` field and guard both `_Process` and `_Input` paths:

```csharp
private bool _cancelCalled;

private void TryCancelOnce() {
    if (_cancelCalled) return;
    _cancelCalled = true;
    try { _session.Cancel(); } catch { /* swallow */ }
}
```

Then replace `_session.Cancel()` calls in `_Process` and `_Input` with `TryCancelOnce()`.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Ui/ActVariantVotePopup.cs
git commit -m "$(cat <<'EOF'
plan-b-3-2/11.1: ActVariantVotePopup lifecycle — tally, abandonment, ESC

- OnTally(snapshot) updates per-column "#N — M votes" labels.
- _Process polls shouldCancel each frame; on fire, writes the
  side-channel flag via onUserAbandoned and calls session.Cancel().
- _Input intercepts ESC BEFORE _UnhandledInput — guarantees popup
  owns the key even if a parent control would have consumed it later.
- OnClosed unsubscribes events and QueueFrees the CanvasLayer.

Cancel() idempotency assumed per spike #9; local guard added if spike
found otherwise.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Full test pass + dotnet test green

**Goal:** Confirm all unit tests pass (existing + new). No code change in this task — just verification.

- [ ] **Step 1: Run full test suite**

```powershell
dotnet test tests/slay_the_streamer_2.tests.csproj --nologo
```
Expected: all tests pass. New B.3.2 tests should add ~30 to the existing count.

- [ ] **Step 2: If any test fails, fix it before proceeding**

Common failure modes:
- `ChatConnectionState` enum value name mismatch — use the actual names from `src/Ti/Chat/ChatConnectionState.cs`.
- `VoteSnapshot` constructor signature mismatch — adjust `MakeSnapshot` test helper to match the real record.
- `[Collection("TiLog.Sink")]` missing — add to any test class that exercises `VoteCoordinator.Start` (the formatter tests do, the resolver tests don't).

No commit needed for this verification task — it's a checkpoint.

---

## Task 13: Build + install + operator-validation gates

**Goal:** Run the 15 operator-validation gates in-game. Most can be exercised today; some are blocked on specific spike outputs.

**Files:**
- Modify: `notes/06-followups-and-deferred.md` — append B.3.2 validation results at the end.

- [ ] **Step 1: Run the full build pipeline**

```powershell
pwsh -File build.ps1
```
Expected: build succeeds, dist/ regenerated. Confirm `godot.log` version hash will match HEAD by noting the build's git HEAD:

```powershell
git log -1 --format=%H
```

- [ ] **Step 2: Install to Steam mods folder**

```powershell
pwsh -File install.ps1
```
Expected: dist/ contents copied to `%STEAM%\steamapps\common\Slay the Spire 2\mods\slay_the_streamer_2\`. Confirm with:

```powershell
Get-ChildItem "$env:ProgramFiles(x86)\Steam\steamapps\common\Slay the Spire 2\mods\slay_the_streamer_2\" | Select-Object Name, LastWriteTime
```

(Adjust path to your Steam install location.)

- [ ] **Step 3: Launch the game, configure settings, run Gate 1**

Launch Slay the Spire 2. Confirm chat connects (per `godot.log`). Click Embark on character-select.

**Gate 1 — Vote fires on Embark click**: popup appears, mouse interaction with character-select blocked, ESC cancels vote. `godot.log` contains `[act-variant-vote] opening vote` (or equivalent — actual log line may differ; look for the formatReceipt open-receipt or `pre-warm: N/M` log).

Record pass/fail and any log excerpts in `notes/06-followups-and-deferred.md` B.3.2 section.

- [ ] **Step 4: Run Gates 2-3 (winner + no-winner)**

**Gate 2 — Winner applied**: vote in chat with `#0` or `#1`; run starts with the chat-chosen variant. Verify by checking the first combat room's enemy set matches the chosen variant's `GenerateAllEncounters` (Overgrowth enemies: Nibbits, Slimes, Inklets, etc.; Underdocks enemies: Corpse Slugs, Cultists, Fossil Stalker, etc.).

**Gate 3 — No-winner fallback**: with chat silent, click Embark and let vote time out. Verify custom no-votes receipt arrives in chat ("vanilla random pick stands"). Run starts with vanilla's seed-deterministic pick (verifiable by entering the same seed twice — same enemy set should appear).

- [ ] **Step 5: Run Gates 4-7 (toggle, degeneracy, cancellation, spam)**

**Gate 4 — Settings toggle off**: edit `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`, set `"voteOnActVariant": false`, restart game, Embark. No vote fires.

**Gate 5 — Pool degeneracy**: defensive only; not exercisable today.

**Gate 6 — Cancellation**: Embark, vote starts, press ESC. Popup tears down, chat receives cancellation receipt, NO run starts (back at character-select).

**Gate 7 — Spam-Embark guard**: click Embark twice rapidly. Second click suppressed (godot.log: `repeat click during open vote; suppressed`).

- [ ] **Step 6: Run Gate 8 (pre-warm telemetry)**

**Gate 8**: confirm godot.log shows `[act-variant-vote] pre-warm: N/M assets in Tms (mode=L1|L3, reason=...)` before the open receipt. Time should be ≤ 100ms on your hardware.

If L3 is unexpected (i.e., asset paths SHOULD have been located), revisit Task 1 Steps 6-7 and update `ActVariantAssetPaths` constants.

- [ ] **Step 7: Run Gates 9-11 (Sealed Deck, receipts, save-quit)**

**Gate 9 — Sealed Deck coexistence**: start a Custom run with Sealed Deck modifier. Confirm vote still fires; after winner, run proceeds with Sealed Deck flow + chat-chosen variant.

**Gate 10 — Receipt delivery**: confirm chat received open + ≥1 periodic-tally + close receipts (validated only in ConnectedReadWrite; if ConnectedReadOnly, this gate is N/A).

**Gate 11 — Save-quit preservation**: start a run with chat-chosen variant. Enter first combat. Save-quit and Continue from main menu. Verify combat-bg / enemy set still matches the chat-picked variant (not vanilla's seed pick).

- [ ] **Step 8: Run Gates 12-13 (Embark cycle, chat disconnect)**

**Gate 12 — Embark→ESC→Embark cycle**: Embark, ESC mid-vote, then Embark again. Second Embark fires a fresh vote (atomic state correctly reset).

**Gate 13 — Chat disconnect mid-vote**: Embark, then disconnect Twitch IRC (e.g., kill the connection in OBS or change a setting that causes disconnect). Vote times out; vanilla pick stands; no crash.

- [ ] **Step 9: Run Gate 14 (multi-resolution)**

Open the game at three different resolutions and run Gate 14's 4 sub-checks at each:

(a) Windowed at ~1/3 of a 1920-wide monitor (manually resize the window to ~640px wide).
(b) 1920×1080 fullscreen.
(c) Ultrawide 1440 fullscreen (3440×1440 or 2560×1440 ultrawide).

At each: open the vote, confirm
- Popup is centered in the 4:3 gameplay area (not the raw window — letterbox bars should sit outside the popup).
- Backgrounds preserve aspect ratio (no horizontal/vertical squish).
- Banners stay inside their column boundary (no bleed across the divider).
- CanvasLayer parent is the gameplay-area Control (verifiable via Godot's remote-scene-tree inspector if you have it; otherwise infer from rendering).

If any sub-check fails, revisit Task 10 Step 1 — `GetGameplayAreaParent()` may need to target a specific Control instead of `GetTree().Root`.

- [ ] **Step 10: Run Gate 15 (Standard mode)**

Start a Standard run with NO modifiers selected (deselect Sealed Deck etc.). Confirm vote fires identically — no implicit gating on `_settings.GameMode`.

- [ ] **Step 11: Append validation results to notes**

Add a section to `notes/06-followups-and-deferred.md`:

```markdown
## B.3.2 acceptance-gate results — 2026-05-18

Tag: `plan-b-3-2-complete` (pending acceptance — set after this section)
HEAD at validation: <git rev-parse HEAD>

| Gate | Status | Notes |
|---|---|---|
| 1 — Vote fires on Embark | ✅/❌ | [excerpt] |
| 2 — Winner applied | ✅/❌ | |
| 3 — No-winner fallback | ✅/❌ | |
| 4 — Settings toggle off | ✅/❌ | |
| 5 — Pool degeneracy | N/A | defensive |
| 6 — Cancellation | ✅/❌ | |
| 7 — Spam-Embark guard | ✅/❌ | |
| 8 — Pre-warm telemetry | ✅/❌ | Tms baseline |
| 9 — Sealed Deck coexistence | ✅/❌ | |
| 10 — Receipt delivery | ✅/❌ | |
| 11 — Save-quit preservation | ✅/❌ | |
| 12 — Embark→ESC→Embark cycle | ✅/❌ | |
| 13 — Chat disconnect mid-vote | ✅/❌ | |
| 14 — Multi-resolution (4 sub-checks × 3 resolutions) | ✅/❌ | per-resolution notes |
| 15 — Standard mode (no modifiers) | ✅/❌ | |

Findings / surprises: [...]
```

Fill in the table during validation.

- [ ] **Step 12: Commit validation results**

```bash
git add notes/06-followups-and-deferred.md
git commit -m "$(cat <<'EOF'
plan-b-3-2/13.1: B.3.2 acceptance-gate results

Operator validation across 15 gates. All gates ✅ (or document
exceptions per gate row). Confirms in-game behavior matches v3 spec
for Standard + Custom (Sealed Deck) modes at 3 tested resolutions.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 14: Slice acceptance + tag

**Goal:** Tag the slice complete and update CLAUDE.md with any Tier-1/Tier-4 findings worth preserving.

**Files:**
- Modify: `CLAUDE.md` — add bullets for any new gotchas surfaced during the spike or validation (e.g., the `Act1`-write override mechanism if it turned out non-obvious).

- [ ] **Step 1: Update CLAUDE.md with any preservation-worthy findings**

If the spike or validation surfaced anything that future slices would benefit from knowing — e.g., a non-obvious quirk of `BeginRunLocally` lifecycle, a Godot anchor convention, a settings-load gotcha — append a bullet to the relevant Tier section of CLAUDE.md.

Most likely candidates (decide based on what you found):
- "**`StartRunLobby.Act1` is a one-shot override hook**" — if not already in CLAUDE.md.
- "**`PreloadManager.Cache.GetTexture` is synchronous and safe to call from the main thread**" — confirms B.3.1's `GetScene` generalization.
- Any unexpected probe behavior from Step 4 of the spike.

- [ ] **Step 2: Run final regression checks**

```powershell
pwsh -File build.ps1
dotnet test tests/slay_the_streamer_2.tests.csproj
```
Expected: build succeeds, all tests pass.

Also verify no `[act-variant-vote]` Warn or Error logs appear in `godot.log` under normal flow (a fresh Embark → vote → winner → run cycle should produce only Info/Debug lines).

- [ ] **Step 3: Tag the slice complete**

```bash
git tag plan-b-3-2-complete -m "B.3.2 act-variant vote slice complete

15 operator-validation gates passing; design v3 acceptance gate
green. No src/Ti/* changes; no regression in B.1/B.2/B.3 family."
```

- [ ] **Step 4: Push the tag (if origin tracks)**

```bash
git push origin plan-b-3-2-complete
```

If pushing also pushes main, that's expected for this slice (commits to main pre-authorized).

- [ ] **Step 5: Final commit if CLAUDE.md was updated**

If Step 1 added bullets:
```bash
git add CLAUDE.md
git commit -m "$(cat <<'EOF'
CLAUDE.md: capture B.3.2 Tier-N findings

[Brief description of what was added and why future slices benefit.]

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Spec Self-Review (post-write)

**Spec coverage check** — each spec section mapped to tasks:

| Spec section | Task(s) |
|---|---|
| Architecture / file layout | Task 2-11 |
| TI/Game seam preserved | Task 10 (popup MegaCrit-free); Task 9 (probe in patch) |
| Vanilla API surface | Task 8 (Harmony attribute), Task 9 (Act1 write, reflective invoke) |
| Trigger mechanics + bail order | Task 8 |
| Suspend-and-resume shape | Task 9 |
| Cancellation | Task 9 (probe), Task 11 (popup polling + ESC) |
| Candidate pool + bail logic | Task 3-4 |
| Asset discovery (research spike) | Task 1 |
| Pre-warm — BOTH variants | Task 7 |
| Popup UI structure | Task 10 |
| Sizing (gameplay-area-aware) | Task 10 (placeholder) + Task 13 Step 9 (verification) |
| Settings | Task 6 |
| Receipts | Task 5 (formatter) |
| Operator-validation gates | Task 13 |
| Test architecture | Task 3-5 |
| Open items / risks | Task 1 (spike resolutions) |
| Spike → Gate dependency | Task 1 (spike outputs) + Task 13 (gate runs) |

**Placeholder scan** — searched for "TBD", "TODO", "fill in":
- Task 9 Step 5: `IsRunStartAbandoned` placeholder — REQUIRES spike output substitution. Documented inline.
- Task 10 Step 1: `GetGameplayAreaParent` placeholder — REQUIRES spike output substitution. Documented inline.
- Task 3 Step 3: `ActVariantAssetPaths` four `null` constants — populated in Task 3 Step 7 from spike.

All placeholders are intentional spike-output substitution points, each flagged with an explicit substitution step.

**Type consistency check** — `BailReason` enum values, `ActVariantOption` field names, `ActVariantPrewarmResult` shape are consistent across Tasks 2-11.

**Scope check** — 14 tasks total; clean per-task commits; each task self-contained and reverifiable.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-05-18-plan-b-3-2-act-variant-vote.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration. Best for this plan because Task 1 (spike) blocks Tasks 9-11 substitutions; subagent-driven cleanly handles the dependency by checking spike notes between tasks.

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints. Best if you want continuous visibility into every step.

Which approach?
