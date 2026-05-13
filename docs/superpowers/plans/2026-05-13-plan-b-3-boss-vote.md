# Plan B.3 Boss Vote Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a chat-picks-the-next-act-boss vote, triggered on chest-room Proceed via the established two-flag suspend-and-resume Harmony pattern, swapping the act boss through `MapCmd.SetBossEncounter`. Visual is a self-owned `CanvasLayer` modal popup with N portrait columns and per-column tally labels.

**Architecture:** Suspend-and-resume Harmony pattern (copy-paste-modify of `CardRewardVotePatch.cs`) targeting `NTreasureRoom.OnProceedButtonPressed`. Three pure helpers (`BossVoteSeed` FNV-1a stable hash, `BossCandidateSampler` partial Fisher-Yates, `BossVoteResolver` bounds-checked index→option) extracted for unit testability since `BossVotePatch.cs` is excluded from `Compile` in the test csproj. Popup consumes `BossVotePopupOption` DTOs (kept MegaCrit-free) and is operator-validated rather than unit-tested. Resume path passes `int? winnerIndex` so vote-failure cases skip the boss swap but still fire the synthetic Proceed re-click — preserving the "no lost click" invariant without the boss mutation.

**Tech Stack:** C# 12 / .NET 9, Godot 4.5.1 Mono SDK, HarmonyLib (`0Harmony.dll` shipped with game), xUnit 2.9, `System.Threading.Tasks`. Tests run via `dotnet test`; full build via `pwsh -File build.ps1`; install via `pwsh -File install.ps1`.

**Source spec:** [`docs/superpowers/specs/2026-05-13-plan-b-3-boss-vote-design-v3.md`](../specs/2026-05-13-plan-b-3-boss-vote-design-v3.md). When the plan and spec disagree, the spec wins; flag the disagreement and stop for clarification.

**Per-task commits:** each task ends in a `git commit` with a `plan-b-3/N.M:` prefix. Surfinite has pre-authorised commits to `main` for this work.

---

## Spec drift noted up front (no action required mid-implementation)

The v3 spec lists `src/Ti/Voting/EnglishReceipts.cs` under "Modified files" as needing "new receipt entries (boss vote open / close / ignored-result)." On re-reading [`EnglishReceipts.cs`](../../../src/Ti/Voting/EnglishReceipts.cs), the formatters are **generic** — `FormatOpen` / `FormatPeriodicTally` / `FormatClose` consume a `VoteSnapshot` and use whatever `Label` was passed to `coordinator.Start(...)`. B.3 passes `"Act N boss vote"` as the label and gets correctly-formatted open/tally/close receipts at no code cost. The only B.3-specific receipt is the **ignored-result** receipt sent when resume's liveness checks fail — and per [`CardRewardVotePatch.cs:397-408`](../../../src/Game/DecisionVotes/CardRewardVotePatch.cs#L397-L408) this is a hardcoded string sent directly via `Voter.Default.Chat.SendMessageAsync(...)`, NOT a new `EnglishReceipts` entry. **This plan therefore does NOT modify `EnglishReceipts.cs`.** The ignored-result string lives inside the patch's private `SendIgnoredResultReceipt()` helper, mirroring B.2.1's `CardRewardVotePatch.SendCancellationReceipt()`.

---

## File Structure

**New files:**
- `src/Game/DecisionVotes/BossVoteSeed.cs` (~25 LOC) — pure helper. FNV-1a 32-bit stable hash. Compiled into tests.
- `src/Game/DecisionVotes/BossCandidateSampler.cs` (~30 LOC) — pure helper. Partial Fisher-Yates. Compiled into tests.
- `src/Game/DecisionVotes/BossVoteResolver.cs` (~20 LOC) — pure helper. Bounds-checked `ResolveWinner<T>`. Compiled into tests.
- `src/Game/DecisionVotes/BossVotePatch.cs` (~250 LOC) — Harmony patch + `HandleVoteAsync` + `ResumeOnMainThread(int? winnerIndex, ...)` + `SendIgnoredResultReceipt` + `ApplyBossSwap` runtime hook. Excluded from `Compile` in tests.
- `src/Game/Ui/BossVotePopupOption.cs` (~5 LOC) — DTO record `(int Index, string Title, string? PortraitPath)`.
- `src/Game/Ui/BossVotePopup.cs` (~200 LOC) — Godot `CanvasLayer`-rooted Control. Consumes DTOs + `VoteSession` + `IMainThreadDispatcher`. Operator-validated.
- `tests/Game/DecisionVotes/BossVoteSeedTests.cs` (~50 LOC).
- `tests/Game/DecisionVotes/BossCandidateSamplerTests.cs` (~70 LOC).
- `tests/Game/DecisionVotes/BossVoteResolverTests.cs` (~30 LOC).
- `notes/B3-spike-2026-05-13.md` — Task 1 spike output (not committed to plans/, lives in notes/).

**Modified files:**
- `src/ModEntry.cs:177` — update Harmony PatchAll comment to mention `BossVotePatch`. Patch is auto-discovered via reflection; no registration code change needed.
- `tests/slay_the_streamer_2.tests.csproj:23-26` — add `<Compile Remove="..\src\Game\DecisionVotes\BossVotePatch.cs" />` next to the existing exclusions.
- `notes/06-followups-and-deferred.md` — append B.3 acceptance-gate results section after operator validation.
- `README.md` — status section: move B.3 from "remaining" to "shipped" after acceptance.

**Test-csproj wiring**: `Compile Include="..\src\Game\DecisionVotes\**\*.cs"` (line 16 of csproj) auto-includes the three new pure helpers. The new test files at `tests/Game/DecisionVotes/*Tests.cs` are picked up by the SDK's default test glob. `src/Game/Ui/*.cs` is NOT in any Compile Include (matching the existing `src/Ti/Ui/` exclusion) — `BossVotePopup.cs` and `BossVotePopupOption.cs` are NOT compiled into the test project. The popup is operator-validated.

---

## Task 1: Pre-implementation spike — decompile verification

**Goal:** Resolve all 10 spike items from the v3 spec before writing any production code. Produces a notes file with concrete answers. Without this, downstream tasks risk implementing against assumed shapes.

**Files:**
- Create: `notes/B3-spike-2026-05-13.md`

- [ ] **Step 1: Verify `ActModel.AllBossEncounters` returns ≥ 3 per vanilla act**

Open `decompiled/sts2/MegaCrit/sts2/Core/Models/ActModel.cs`. Find `AllBossEncounters` (around line 119). Trace into per-act subclasses (`Underdocks`, `Overgrowth`, `Hive`, `Glory`) under `decompiled/sts2/MegaCrit/sts2/Core/Models/Acts/` and count `RoomType.Boss` entries in each act's `GenerateAllEncounters` override.

Record in spike notes: `{act: count}` table.

- [ ] **Step 2: Verify `AllBossEncounters` is NOT unlock-filtered**

In the same `ActModel.cs`, confirm `AllBossEncounters` does not apply an unlock filter (unlike `GetUnlockedAncients(UnlockState)` at line 197). If it does filter, document and note that unlocked-everything streamers are unaffected.

- [ ] **Step 3: Verify `HasSecondBoss` timing**

In `decompiled/sts2/MegaCrit/sts2/Core/Runs/RunManager.cs` near line 499-502, confirm the second-boss roll happens once at run start and only on A10+ (`AscensionLevel.DoubleBoss`) final-act runs. Trace `ActModel.SetSecondBossEncounter` callers — confirm no mid-run path mutates `_rooms.SecondBoss` once set.

Record: at what point in the run lifecycle is `HasSecondBoss` first true? Is it stable from that point onward?

- [ ] **Step 4: Verify `NTreasureRoom.OnProceedButtonPressed` signature**

In `decompiled/sts2/MegaCrit/sts2/Core/Nodes/Rooms/NTreasureRoom.cs`, find the `OnProceedButtonPressed` method. Confirm:
- Visibility (public / private / protected)
- Parameter list (expected: parameterless)
- Return type (expected: `void`)

If private, the patch's synthetic re-call needs reflection (cache `MethodInfo` in a `Lazy<MethodInfo?>` like `CardRewardVotePatch._selectCardMethod`). If public, direct call works (like `AncientVotePatch`'s `room.OptionButtonClicked(...)`). Record the decision.

- [ ] **Step 5: Identify `BossVotePatch.Prepare` reflection surface**

Based on Step 4: if `OnProceedButtonPressed` is public and parameterless, `Prepare` only needs to verify the method signature (parameter count = 0). No private-field reflection needed — do NOT preemptively reflect `_proceedButton` or other fields the patch body doesn't read (per R2B's review feedback and `CardRewardVotePatch.Prepare`'s pattern of only verifying what's load-bearing).

If `OnProceedButtonPressed` is private, also cache the `MethodInfo` in `Prepare`'s hard checks.

Record the exact `Prepare` checklist.

- [ ] **Step 6: Verify `runState.CurrentActIndex` 0-based vs 1-based**

Search the decompile for `CurrentActIndex` usage. Find places where it's compared against `Acts.Count - 1` or `Acts.Count` and infer the base. Cross-check by reading the property definition on `RunState` / `IRunState`.

Record: 0-based or 1-based. Adjust the popup's title label expression accordingly (v3 spec assumes 0-based with `+1` for display).

- [ ] **Step 7: Verify `BossNodePath` extension handling**

In `decompiled/sts2/MegaCrit/sts2/Core/Models/EncounterModel.cs` lines 138-168, read the `BossNodePath` getter and `MapNodeAssetPaths`. Confirm whether `BossNodePath` returns a path with `.tres` suffix (in which case append `.png` to get the PNG fallback) or already includes an extension.

Record: the exact extension convention for PNG load.

- [ ] **Step 8: Verify `EncounterModel.Title.GetFormattedText()` plain text vs BBCode**

In `EncounterModel.cs` line 152, find the `Title` property and its `LocString` type. Look at `LocString.GetFormattedText()` — does it return plain text or BBCode markup (e.g., `[color=...]`)? Sample a few vanilla boss titles by inspecting their localization keys under `res://i18n/` or similar.

Record: plain text or BBCode. The popup uses `RichTextLabel` with `BbcodeEnabled = true` either way (harmless for plain text), but the answer informs whether boss names need post-processing for chat receipts.

- [ ] **Step 9: Verify `CanvasLayer.Layer = 100` collision check**

Grep the decompile for `CanvasLayer` instantiations and `Layer` assignments. Confirm no vanilla `CanvasLayer` uses layer index 100. If any conflict, choose a higher index (e.g., 200) and record the rationale.

```bash
git grep -h "CanvasLayer" -- decompiled/ | git grep -n "Layer\s*=\s*[0-9]"
```

Record: confirmed-safe layer index for `BossVotePopup.LAYER_INDEX`.

- [ ] **Step 10: Verify `_isRelicCollectionOpen` interaction with `OnProceedButtonPressed`**

In `NTreasureRoom.cs`, find `_isRelicCollectionOpen` and any check inside `OnProceedButtonPressed` that guards on it. Does Proceed no-op while the relic-collection overlay is open? Does it close the overlay first? Does it throw?

Record: the empirical behavior, plus whether Smoke I needs special setup.

- [ ] **Step 11: Write `notes/B3-spike-2026-05-13.md`**

Compose a notes file with one section per spike item, the answer, and a quote/citation from the decompile (file:line) supporting it. Mirror the structure of `notes/10-boss-vote-feasibility.md` for readability.

- [ ] **Step 12: Commit**

```bash
git add notes/B3-spike-2026-05-13.md
git commit -m "$(cat <<'EOF'
plan-b-3/1.1: spike — decompile verification

Resolves the 10 spike items from the v3 design spec before any
production code is written. Records concrete answers for boss pool
size per act, unlock filtering, HasSecondBoss timing,
OnProceedButtonPressed signature/visibility, Prepare surface,
CurrentActIndex base, BossNodePath extension handling, title
BBCode behavior, safe CanvasLayer layer index, and
_isRelicCollectionOpen interaction.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `BossVoteSeed` pure helper (TDD)

**Goal:** FNV-1a 32-bit stable hash for deterministic sampling seed. `HashCode.Combine` is unsuitable because `string.GetHashCode()` is per-process-randomized in .NET 5+ — would break save-load determinism (Smoke H).

**Files:**
- Create: `tests/Game/DecisionVotes/BossVoteSeedTests.cs`
- Create: `src/Game/DecisionVotes/BossVoteSeed.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Game/DecisionVotes/BossVoteSeedTests.cs`:

```csharp
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossVoteSeedTests {
    [Fact]
    public void Stable_SameInput_ReturnsSameValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed1", 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_DifferentActIndex_ReturnsDifferentValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed1", 1);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Stable_DifferentSeed_ReturnsDifferentValue() {
        int a = BossVoteSeed.Stable("seed1", 0);
        int b = BossVoteSeed.Stable("seed2", 0);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Stable_NullSeed_DoesNotThrow_AndIsDeterministic() {
        int a = BossVoteSeed.Stable(null, 0);
        int b = BossVoteSeed.Stable(null, 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_EmptySeed_DoesNotThrow_AndIsDeterministic() {
        int a = BossVoteSeed.Stable("", 0);
        int b = BossVoteSeed.Stable("", 0);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Stable_KnownValue_FnvOneACompliance() {
        // FNV-1a 32-bit of "abc" + actIndex 0, computed independently.
        // fnvOffset = unchecked((int)2166136261); fnvPrime = 16777619.
        // hash starts at fnvOffset.
        //   hash ^= 'a' (97); hash *= 16777619; (carries through int unchecked)
        //   hash ^= 'b' (98); hash *= 16777619;
        //   hash ^= 'c' (99); hash *= 16777619;
        //   hash ^= 0 (actIndex); hash *= 16777619;
        // Expected value verified offline using:
        //   python3 -c "h = 0x811c9dc5
        //                for c in 'abc': h ^= ord(c); h = (h * 0x01000193) & 0xFFFFFFFF
        //                h ^= 0; h = (h * 0x01000193) & 0xFFFFFFFF
        //                print(h if h < 0x80000000 else h - 0x100000000)"
        // Result: -1424385571
        int v = BossVoteSeed.Stable("abc", 0);
        Assert.Equal(-1424385571, v);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossVoteSeedTests"
```

Expected: `BossVoteSeedTests` compile error — `BossVoteSeed` type does not exist yet.

- [ ] **Step 3: Implement `BossVoteSeed`**

Create `src/Game/DecisionVotes/BossVoteSeed.cs`:

```csharp
namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// FNV-1a 32-bit stable hash for boss-vote candidate-sampling RNG seed.
/// Does NOT use HashCode.Combine because string.GetHashCode() is per-process
/// randomized in .NET 5+ — that would break save-load determinism (Smoke H
/// expects identical candidate sets after reload).
/// </summary>
internal static class BossVoteSeed {
    public static int Stable(string? runSeed, int actIndex) {
        unchecked {
            const int fnvOffset = unchecked((int)2166136261);
            const int fnvPrime = 16777619;
            int hash = fnvOffset;
            foreach (char c in runSeed ?? string.Empty) {
                hash ^= c;
                hash *= fnvPrime;
            }
            hash ^= actIndex;
            hash *= fnvPrime;
            return hash;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossVoteSeedTests"
```

Expected: 6 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add tests/Game/DecisionVotes/BossVoteSeedTests.cs src/Game/DecisionVotes/BossVoteSeed.cs
git commit -m "$(cat <<'EOF'
plan-b-3/2.1: BossVoteSeed pure helper — FNV-1a 32-bit stable hash

Stable seed for candidate-sampling RNG. Avoids HashCode.Combine
because string.GetHashCode() is per-process randomized in .NET 5+,
which would break Smoke H (same run + same act → same candidates
on save-reload).

Six tests cover within-process determinism, different-input
divergence, null/empty-seed safety, and a known-value FNV-1a
compliance check against an independent Python implementation.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `BossCandidateSampler` pure helper (TDD)

**Goal:** Partial Fisher-Yates sampling without replacement. Deterministic under seeded RNG, no game references, compiles into tests.

**Files:**
- Create: `tests/Game/DecisionVotes/BossCandidateSamplerTests.cs`
- Create: `src/Game/DecisionVotes/BossCandidateSampler.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Game/DecisionVotes/BossCandidateSamplerTests.cs`:

```csharp
using System;
using System.Linq;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossCandidateSamplerTests {
    [Fact]
    public void SampleDistinct_SameSeed_ReturnsSameOrder() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s1 = BossCandidateSampler.SampleDistinct(pool, 3, new Random(42));
        var s2 = BossCandidateSampler.SampleDistinct(pool, 3, new Random(42));
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void SampleDistinct_PoolLargerThanCount_ReturnsExactCount() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(3, s.Count);
    }

    [Fact]
    public void SampleDistinct_NoDuplicates() {
        var pool = new[] { "A", "B", "C", "D", "E" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(s.Count, s.Distinct().Count());
    }

    [Fact]
    public void SampleDistinct_PoolEqualsCount_ReturnsAllItems() {
        var pool = new[] { "A", "B", "C" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(3, s.Count);
        Assert.Equal(new[] { "A", "B", "C" }.ToHashSet(), s.ToHashSet());
    }

    [Fact]
    public void SampleDistinct_PoolSmallerThanCount_ReturnsPoolSize() {
        var pool = new[] { "A", "B" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Equal(2, s.Count);
        Assert.Equal(new[] { "A", "B" }.ToHashSet(), s.ToHashSet());
    }

    [Fact]
    public void SampleDistinct_SinglePool_ReturnsSingle() {
        var pool = new[] { "A" };
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Single(s);
        Assert.Equal("A", s[0]);
    }

    [Fact]
    public void SampleDistinct_EmptyPool_ReturnsEmpty() {
        var pool = Array.Empty<string>();
        var s = BossCandidateSampler.SampleDistinct(pool, 3, new Random(1));
        Assert.Empty(s);
    }

    [Fact]
    public void SampleDistinct_ZeroCount_ReturnsEmpty() {
        var pool = new[] { "A", "B", "C" };
        var s = BossCandidateSampler.SampleDistinct(pool, 0, new Random(1));
        Assert.Empty(s);
    }

    [Fact]
    public void SampleDistinct_NullSource_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            BossCandidateSampler.SampleDistinct<string>(null!, 3, new Random(1)));
    }

    [Fact]
    public void SampleDistinct_NullRng_Throws() {
        var pool = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentNullException>(() =>
            BossCandidateSampler.SampleDistinct(pool, 3, null!));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossCandidateSamplerTests"
```

Expected: compile error — `BossCandidateSampler` does not exist.

- [ ] **Step 3: Implement `BossCandidateSampler`**

Create `src/Game/DecisionVotes/BossCandidateSampler.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Partial Fisher-Yates sampling without replacement. Deterministic under
/// seeded RNG. Used by BossVotePatch to draw up to N boss candidates from
/// the act's AllBossEncounters pool.
/// </summary>
internal static class BossCandidateSampler {
    public static IReadOnlyList<T> SampleDistinct<T>(
        IReadOnlyList<T> source, int count, Random rng) {

        if (source is null) throw new ArgumentNullException(nameof(source));
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        if (count <= 0 || source.Count == 0) return Array.Empty<T>();

        // Copy to mutable list; partial Fisher-Yates from the front.
        // After n = min(count, source.Count) iterations, items[0..n) is a
        // uniform random sample without replacement.
        var items = new List<T>(source);
        int n = Math.Min(count, items.Count);
        for (int i = 0; i < n; i++) {
            int j = rng.Next(i, items.Count);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items.GetRange(0, n);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossCandidateSamplerTests"
```

Expected: 10 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add tests/Game/DecisionVotes/BossCandidateSamplerTests.cs src/Game/DecisionVotes/BossCandidateSampler.cs
git commit -m "$(cat <<'EOF'
plan-b-3/3.1: BossCandidateSampler pure helper — partial Fisher-Yates

Samples up to N distinct items from an IReadOnlyList<T> using a seeded
Random. Used by BossVotePatch to draw boss candidates from the act's
AllBossEncounters pool. Game-free; compiles into the test project.

Ten tests cover: same-seed determinism, exact-count return for large
pools, no duplicates, pool-equals-count, pool-smaller-than-count
(returns pool size), single-item pool, empty pool, zero count, and
null-arg guards.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `BossVoteResolver` pure helper (TDD)

**Goal:** Bounds-checked index→option lookup. Replaces the `Action<IRunState, EncounterModel> ApplyBossSwap` "test seam" claim from v2 (which was unreachable because `BossVotePatch.cs` is excluded from tests). The actual testable logic is the winner-index→option mapping.

**Files:**
- Create: `tests/Game/DecisionVotes/BossVoteResolverTests.cs`
- Create: `src/Game/DecisionVotes/BossVoteResolver.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Game/DecisionVotes/BossVoteResolverTests.cs`:

```csharp
using System;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

[Collection("TiLog.Sink")]
public class BossVoteResolverTests {
    [Fact]
    public void ResolveWinner_ValidIndex_ReturnsOption() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("B", BossVoteResolver.ResolveWinner(options, 1));
    }

    [Fact]
    public void ResolveWinner_FirstIndex_ReturnsFirst() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("A", BossVoteResolver.ResolveWinner(options, 0));
    }

    [Fact]
    public void ResolveWinner_LastIndex_ReturnsLast() {
        var options = new[] { "A", "B", "C" };
        Assert.Equal("C", BossVoteResolver.ResolveWinner(options, 2));
    }

    [Fact]
    public void ResolveWinner_OutOfRange_Throws() {
        var options = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, 3));
    }

    [Fact]
    public void ResolveWinner_NegativeIndex_Throws() {
        var options = new[] { "A", "B", "C" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, -1));
    }

    [Fact]
    public void ResolveWinner_EmptyOptions_AnyIndexThrows() {
        var options = Array.Empty<string>();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BossVoteResolver.ResolveWinner(options, 0));
    }

    [Fact]
    public void ResolveWinner_NullOptions_Throws() {
        Assert.Throws<ArgumentNullException>(() =>
            BossVoteResolver.ResolveWinner<string>(null!, 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossVoteResolverTests"
```

Expected: compile error — `BossVoteResolver` does not exist.

- [ ] **Step 3: Implement `BossVoteResolver`**

Create `src/Game/DecisionVotes/BossVoteResolver.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>
/// Bounds-checked index→option lookup. Game-free testable seam for
/// BossVotePatch's winner→encounter resolution. The actual
/// MapCmd.SetBossEncounter invocation is operator-validated via Smoke A,
/// not unit-tested.
/// </summary>
internal static class BossVoteResolver {
    public static T ResolveWinner<T>(IReadOnlyList<T> options, int winnerIndex) {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if ((uint)winnerIndex >= (uint)options.Count) {
            throw new ArgumentOutOfRangeException(nameof(winnerIndex),
                $"winnerIndex {winnerIndex} out of range [0, {options.Count})");
        }
        return options[winnerIndex];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~BossVoteResolverTests"
```

Expected: 7 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add tests/Game/DecisionVotes/BossVoteResolverTests.cs src/Game/DecisionVotes/BossVoteResolver.cs
git commit -m "$(cat <<'EOF'
plan-b-3/4.1: BossVoteResolver pure helper — bounds-checked winner lookup

Testable seam for BossVotePatch's winner index → option mapping.
ApplyBossSwap (the actual MapCmd.SetBossEncounter call site) remains
a runtime hook inside BossVotePatch.cs and is operator-validated.

Seven tests cover valid indices (first / middle / last), out-of-range
and negative-index throws, empty-options throws, and null-options throws.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `BossVotePopupOption` DTO

**Goal:** MegaCrit-free record for popup option data. The patch maps `EncounterModel` → `BossVotePopupOption` before constructing the popup; popup never imports MegaCrit types.

**Files:**
- Create: `src/Game/Ui/BossVotePopupOption.cs`

- [ ] **Step 1: Create the DTO**

Create `src/Game/Ui/BossVotePopupOption.cs`:

```csharp
namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Game-free DTO for boss-vote popup column data. BossVotePatch maps
/// MegaCrit.Sts2 EncounterModel → BossVotePopupOption before constructing
/// the popup, so BossVotePopup never references MegaCrit types.
/// </summary>
internal sealed record BossVotePopupOption(int Index, string Title, string? PortraitPath);
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds. The file lives in `src/Game/Ui/` which is part of the mod build (the test csproj does not include this path, so the DTO is NOT in the test project — intentional).

- [ ] **Step 3: Commit**

```bash
git add src/Game/Ui/BossVotePopupOption.cs
git commit -m "$(cat <<'EOF'
plan-b-3/5.1: BossVotePopupOption DTO — popup is MegaCrit-free

Record (int Index, string Title, string? PortraitPath). BossVotePatch
maps EncounterModel → BossVotePopupOption before constructing
BossVotePopup; popup never imports MegaCrit.Sts2 types. Resolves
round-2 reviewer concern about popup architecture coupling.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `BossVotePopup` Godot Control

**Goal:** Self-owned `CanvasLayer` modal popup. Operator-validated; not unit-tested (Godot deps). Cleanup is dispatcher-marshaled.

**Files:**
- Create: `src/Game/Ui/BossVotePopup.cs`

- [ ] **Step 1: Create skeleton (class, fields, constructor)**

Create `src/Game/Ui/BossVotePopup.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>
/// Modal boss-vote popup. Self-owned CanvasLayer rooted at SceneTree.Root.
/// Renders up to N portrait columns plus a live timer and per-column tally
/// labels. Lifecycle: subscribes to session.Closed / session.Cancelled with
/// handlers marshaled through IMainThreadDispatcher to guarantee main-thread
/// QueueFree. Live tally + timer via _Process polling — NOT subscribed to
/// TallyChanged (which can fire on the chat parser's thread).
/// </summary>
internal sealed partial class BossVotePopup : Control {
    /// <summary>
    /// Layer index for the popup's CanvasLayer. Spike Step 9 verified no
    /// vanilla CanvasLayer uses 100; adjust here if a future spike finds
    /// a collision.
    /// </summary>
    public const int LAYER_INDEX = 100;

    private readonly IReadOnlyList<BossVotePopupOption> _options;
    private readonly VoteSession _session;
    private readonly IMainThreadDispatcher _dispatcher;

    private CanvasLayer? _canvasLayer;
    private RichTextLabel? _timerLabel;
    private readonly List<RichTextLabel> _tallyLabels = new();

    private EventHandler<VoteSession>? _closedHandler;
    private EventHandler<VoteSession>? _cancelledHandler;

    private int _cachedSecondsLeft = -1;
    private int _cachedTallyVersion = -1;

    public BossVotePopup(
        IReadOnlyList<BossVotePopupOption> options,
        VoteSession session,
        IMainThreadDispatcher dispatcher) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Build the CanvasLayer + backdrop + columns and add to SceneTree.Root.
    /// Subscribe to session lifecycle events. Must be called on the main thread.
    /// </summary>
    public void Show(int actNumberOneBased) {
        // implemented in Step 2
    }

    public override void _Process(double delta) {
        // implemented in Step 3
    }

    public override void _UnhandledInput(InputEvent @event) {
        // implemented in Step 4
    }

    public override void _ExitTree() {
        // implemented in Step 5
        base._ExitTree();
    }
}
```

- [ ] **Step 2: Implement `Show`**

Replace the `Show` placeholder with:

```csharp
public void Show(int actNumberOneBased) {
    var tree = Engine.GetMainLoop() as SceneTree;
    if (tree?.Root is null) {
        TiLog.Warn("[SlayTheStreamer2][boss-vote] no SceneTree.Root available; popup not shown");
        return;
    }

    _canvasLayer = new CanvasLayer {
        Name = "BossVotePopupCanvasLayer",
        Layer = LAYER_INDEX,
        ProcessMode = ProcessModeEnum.Always,   // keep updating during pause
    };

    // Backdrop — full-screen 60%-opaque black, stops mouse input.
    var backdrop = new ColorRect {
        Name = "Backdrop",
        Color = new Color(0, 0, 0, 0.6f),
        MouseFilter = MouseFilterEnum.Stop,
        AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
    };
    _canvasLayer.AddChild(backdrop);

    // Content root — VBox centered with title, timer, columns.
    var content = new VBoxContainer {
        Name = "Content",
        AnchorLeft = 0.1f, AnchorTop = 0.15f, AnchorRight = 0.9f, AnchorBottom = 0.85f,
    };
    _canvasLayer.AddChild(content);

    var title = new RichTextLabel {
        Name = "Title",
        BbcodeEnabled = true,
        FitContent = true,
        Text = $"[b]ACT {actNumberOneBased} BOSS VOTE[/b]",
    };
    content.AddChild(title);

    _timerLabel = new RichTextLabel {
        Name = "Timer",
        BbcodeEnabled = true,
        FitContent = true,
        Text = $"{(int)_session.TimeRemaining.TotalSeconds}s left",
    };
    content.AddChild(_timerLabel);

    var columns = new HBoxContainer {
        Name = "Columns",
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ExpandFill,
    };
    content.AddChild(columns);

    foreach (var opt in _options) {
        var col = new VBoxContainer {
            Name = $"Column{opt.Index}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        columns.AddChild(col);

        // Portrait — defensive load.
        var portrait = new TextureRect {
            Name = "Portrait",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(256, 256),
        };
        if (!string.IsNullOrEmpty(opt.PortraitPath)) {
            try {
                var tex = ResourceLoader.Load<Texture2D>(opt.PortraitPath);
                if (tex is not null) portrait.Texture = tex;
            } catch (Exception ex) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] portrait load failed for {opt.PortraitPath}: {ex.Message}");
            }
        }
        col.AddChild(portrait);

        var nameLabel = new RichTextLabel {
            Name = "Name",
            BbcodeEnabled = true,
            FitContent = true,
            Text = $"#{opt.Index} {opt.Title}",
        };
        col.AddChild(nameLabel);

        var tally = new RichTextLabel {
            Name = "Tally",
            BbcodeEnabled = true,
            FitContent = true,
            Text = "0",
        };
        col.AddChild(tally);
        _tallyLabels.Add(tally);
    }

    // Lifecycle hooks: marshal cleanup through the dispatcher to guarantee
    // main-thread context (Closed/Cancelled may fire from the chat parser
    // thread or the timer callback).
    _closedHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
    _cancelledHandler = (_, _) => _dispatcher.Post(SafeQueueFree);
    _session.Closed += _closedHandler;
    _session.Cancelled += _cancelledHandler;

    tree.Root.AddChild(_canvasLayer);
    _canvasLayer.AddChild(this);   // popup Control is parented under the layer
}

private void SafeQueueFree() {
    if (_canvasLayer is not null
            && GodotObject.IsInstanceValid(_canvasLayer)
            && !_canvasLayer.IsQueuedForDeletion()) {
        _canvasLayer.QueueFree();
    }
}
```

- [ ] **Step 3: Implement `_Process` polling**

Replace the `_Process` placeholder:

```csharp
public override void _Process(double delta) {
    if (_session.State is VoteSessionState.Closed
                          or VoteSessionState.Cancelled
                          or VoteSessionState.Disposed) return;
    if (_timerLabel is null) return;

    int secondsLeft = Math.Max(0, (int)_session.TimeRemaining.TotalSeconds);
    int tallyVersion = _session.TallyVersion;

    if (secondsLeft == _cachedSecondsLeft && tallyVersion == _cachedTallyVersion) return;
    _cachedSecondsLeft = secondsLeft;
    _cachedTallyVersion = tallyVersion;

    _timerLabel.Text = $"{secondsLeft}s left";

    for (int i = 0; i < _options.Count; i++) {
        var opt = _options[i];
        _session.Tallies.TryGetValue(opt.Index, out int count);
        var bar = new StringBuilder();
        for (int b = 0; b < count && b < 20; b++) bar.Append('▮');
        _tallyLabels[i].Text = $"{bar} {count}";
    }
}
```

- [ ] **Step 4: Implement `_UnhandledInput` swallowing**

Replace the `_UnhandledInput` placeholder:

```csharp
public override void _UnhandledInput(InputEvent @event) {
    if (@event.IsActionPressed("ui_accept") || @event.IsActionPressed("ui_cancel")) {
        GetViewport().SetInputAsHandled();
    }
}
```

- [ ] **Step 5: Implement `_ExitTree` cleanup**

Replace the `_ExitTree` placeholder:

```csharp
public override void _ExitTree() {
    if (_closedHandler is not null) _session.Closed -= _closedHandler;
    if (_cancelledHandler is not null) _session.Cancelled -= _cancelledHandler;
    _closedHandler = null;
    _cancelledHandler = null;
    base._ExitTree();
}
```

- [ ] **Step 6: Build to verify compilation**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds. Resolve any compile errors (likely typos in Godot API names — they vary by Godot version).

- [ ] **Step 7: Run tests to verify nothing regressed**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all 200+ existing tests still pass; new sampler/seed/resolver tests pass; popup file is NOT compiled into tests so it doesn't affect the test run.

- [ ] **Step 8: Commit**

```bash
git add src/Game/Ui/BossVotePopup.cs
git commit -m "$(cat <<'EOF'
plan-b-3/6.1: BossVotePopup — self-owned CanvasLayer modal

Godot Control consuming BossVotePopupOption DTOs (MegaCrit-free) and
VoteSession + IMainThreadDispatcher. CanvasLayer at LAYER_INDEX=100
parented under SceneTree.Root with ProcessMode.Always so the timer
keeps updating during pause.

Backdrop is a 60%-opaque ColorRect with MouseFilter.Stop. Popup root
overrides _UnhandledInput to swallow ui_accept and ui_cancel,
preventing accidental Proceed activation via keyboard/gamepad
underneath the modal.

Live tally + timer via _Process polling session.Snapshot — NOT
subscribed to TallyChanged (which can fire from the chat parser's
thread per VoteSession.cs:196). Cleanup marshaled through
_dispatcher.Post(SafeQueueFree) on session.Closed / session.Cancelled
for main-thread guarantee. _ExitTree unsubscribes both handlers.

Defensive portrait load: string.IsNullOrEmpty + try/catch around
ResourceLoader.Load<Texture2D>; empty box on null/throw/null-result.

Operator-validated; src/Game/Ui/ is not in the test Compile Include
(same pattern as src/Ti/Ui/VoteTallyLabel.cs).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `BossVotePatch` Harmony patch

**Goal:** The Harmony glue. Combines all helpers + popup into the suspend-and-resume flow. Excluded from test compile (references MegaCrit + Harmony + Godot).

**Files:**
- Create: `src/Game/DecisionVotes/BossVotePatch.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add `Compile Remove` line)
- Modify: `src/ModEntry.cs:177` (comment update)

- [ ] **Step 1: Create the patch skeleton**

Create `src/Game/DecisionVotes/BossVotePatch.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

[HarmonyPatch(typeof(NTreasureRoom), nameof(NTreasureRoom.OnProceedButtonPressed))]
internal static class BossVotePatch {
    private static int _voteInProgress;
    private static int _resumeInProgress;
    private static int _multiplayerWarnFired;

    internal static bool RunIdGuardEnabled { get; private set; } = true;

    /// <summary>
    /// Runtime override hook for operator debugging. Defaults to MapCmd.SetBossEncounter.
    /// Tests do NOT use this seam (the patch file is excluded from Compile in the
    /// test csproj) — the testable winner-index→option mapping lives in BossVoteResolver.
    /// </summary>
    internal static Action<IRunState, EncounterModel> ApplyBossSwap { get; set; }
        = (rs, boss) => MapCmd.SetBossEncounter(rs, boss);

    // Implemented in Steps 2-8 below.
}
```

If Step 4 of the spike found `OnProceedButtonPressed` is **private**, add a `Lazy<MethodInfo?>` for the method here and use reflection in Step 6's synthetic re-call. The skeleton above assumes public (most common shape for Godot button handlers).

- [ ] **Step 2: Implement `Prepare`**

Add to the class body:

```csharp
static bool Prepare(MethodBase? original) {
    if (original is null) {
        // Registration-time. Soft-check the run-id accessor (vote still
        // works without it; just no abandon-mid-vote safety net).
        try {
            var rm = RunManager.Instance;
            if (rm is null) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded: RunManager.Instance not reachable");
                RunIdGuardEnabled = false;
            } else if (rm.GetType().GetMethod("DebugOnlyGetState") is null) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded: DebugOnlyGetState() not found");
                RunIdGuardEnabled = false;
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] run-ID guard degraded: Prepare soft check threw: {ex.Message}");
            RunIdGuardEnabled = false;
        }
        return true;
    }

    // Per-method signature check. v3 spec Step 5 (spike-derived): only verify
    // what the patch body actually uses — the method itself, parameterless.
    var parameters = original.GetParameters();
    if (parameters.Length != 0) {
        TiLog.Error($"[SlayTheStreamer2][boss-vote] target signature mismatch: {original.DeclaringType?.FullName}.{original.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
        return false;
    }
    TiLog.Info($"[SlayTheStreamer2][boss-vote] target resolved: {original.DeclaringType?.FullName}.{original.Name}");
    return true;
}
```

- [ ] **Step 3: Implement `Prefix` guard chain**

Add:

```csharp
static bool Prefix(NTreasureRoom __instance) {
    // 1. Synthetic resume re-call → let vanilla through.
    if (_resumeInProgress == 1) return true;

    // 2. Validity check.
    if (!GodotObject.IsInstanceValid(__instance)) return true;

    // 3. Multiplayer bail.
    int? playerCount = TryGetPlayerCount();
    if (playerCount is int n && n > 1) {
        if (Interlocked.CompareExchange(ref _multiplayerWarnFired, 1, 0) == 0) {
            TiLog.Warn("[SlayTheStreamer2][boss-vote] multiplayer detected (Players.Count > 1); bailing to vanilla");
        } else {
            TiLog.Debug("[SlayTheStreamer2][boss-vote] multiplayer bail-out");
        }
        return true;
    }

    // 4. Chat-readable bail.
    var coordinator = Voter.Default;
    if (coordinator is null) return true;
    if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite
                                    or ChatConnectionState.ConnectedReadOnly)) {
        TiLog.Debug($"[SlayTheStreamer2][boss-vote] chat not readable (state={coordinator.Chat.State}); bailing to vanilla");
        return true;
    }

    // 5. Atomic acquire.
    if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) {
        TiLog.Debug("[SlayTheStreamer2][boss-vote] repeat click during open vote; suppressed");
        return false;
    }

    // 6-12 implemented in Steps 4-5 below.
    return PrefixContinue(__instance, coordinator);
}

private static int? TryGetPlayerCount() {
    try {
        return RunManager.Instance?.DebugOnlyGetState()?.Players?.Count;
    } catch {
        return null;
    }
}
```

- [ ] **Step 4: Implement `PrefixContinue` — candidate sampling + DTO mapping**

Add:

```csharp
private static bool PrefixContinue(NTreasureRoom room, VoteCoordinator coordinator) {
    IRunState? runState = RunManager.Instance?.DebugOnlyGetState();
    if (runState is null) {
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    // Materialize pool once; exclude SecondBossEncounter if A10+ DoubleBoss.
    List<EncounterModel> pool;
    try {
        pool = runState.Act.AllBossEncounters.ToList();
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][boss-vote] AllBossEncounters threw", ex);
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    if (runState.Act.HasSecondBoss) {
        string? secondId = runState.Act.SecondBossEncounter?.Id;
        if (!string.IsNullOrEmpty(secondId)) {
            pool.RemoveAll(e => e.Id == secondId);
            TiLog.Info($"[SlayTheStreamer2][boss-vote] HasSecondBoss=true; excluding {secondId} from sample");
        } else {
            TiLog.Warn("[SlayTheStreamer2][boss-vote] HasSecondBoss true but SecondBossEncounter missing");
        }
    }

    if (pool.Count < 3) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] only {pool.Count} bosses available for Act {runState.CurrentActIndex + 1} — possible content change?");
    }
    if (pool.Count <= 1) {
        TiLog.Info($"[SlayTheStreamer2][boss-vote] degenerate pool (count={pool.Count}); skipping vote");
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    // Stable deterministic seed; same run + same act → same candidates across processes.
    int seed = BossVoteSeed.Stable(runState.Rng?.StringSeed, runState.CurrentActIndex);
    var rng = new Random(seed);
    var sample = BossCandidateSampler.SampleDistinct(pool, count: 3, rng);

    var sampledIds = string.Join(", ", sample.Select((e, i) => $"#{i}={e.Title.GetFormattedText()}({e.Id})"));
    TiLog.Info($"[SlayTheStreamer2][boss-vote] opening vote for {sample.Count} options; seed={seed}; sampled: {sampledIds}");

    // Run-id capture (soft guard).
    string? runIdAtStart = null;
    if (RunIdGuardEnabled) {
        try {
            runIdAtStart = runState.Rng?.StringSeed;
            if (runIdAtStart is null) TiLog.Warn("[SlayTheStreamer2][boss-vote] run-ID guard degraded for this vote — null state or null seed at start");
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] run-ID guard degraded for this vote — {ex.Message}");
        }
    }

    // Map EncounterModel → BossVotePopupOption DTOs (keeps popup MegaCrit-free).
    var dtos = sample.Select((e, i) => new BossVotePopupOption(
        Index: i,
        Title: e.Title.GetFormattedText(),
        PortraitPath: ResolvePortraitPath(e))).ToList();
    var labels = dtos.Select(d => d.Title).ToList();

    // Start session.
    VoteSession session;
    try {
        session = coordinator.Start($"Act {runState.CurrentActIndex + 1} boss vote", labels, TimeSpan.FromSeconds(30));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][boss-vote] Voter.Default.Start threw; falling back to vanilla", ex);
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    // Construct popup; cancel session and bail on construction failure.
    try {
        var popup = new BossVotePopup(dtos, session, coordinator.Dispatcher);
        coordinator.Dispatcher.Post(() => popup.Show(runState.CurrentActIndex + 1));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][boss-vote] BossVotePopup construction threw; cancelling session", ex);
        try { session.Cancel(); } catch { /* swallow */ }
        Interlocked.Exchange(ref _voteInProgress, 0);
        return true;
    }

    _ = HandleVoteAsync(coordinator, room, session, sample, runIdAtStart);
    return false;
}

private static string? ResolvePortraitPath(EncounterModel boss) {
    try {
        var basePath = boss.BossNodePath;
        if (string.IsNullOrEmpty(basePath)) return null;
        return basePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? basePath
            : basePath + ".png";
    } catch (Exception ex) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] BossNodePath access threw: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 5: Implement `HandleVoteAsync`**

Add:

```csharp
private static async Task HandleVoteAsync(
    VoteCoordinator coordinator,
    NTreasureRoom room,
    VoteSession session,
    IReadOnlyList<EncounterModel> sample,
    string? runIdAtStart) {
    try {
        coordinator.Dispatcher.Post(() => VoteTallyLabel.AttachTo(session));

        int? winnerIndex;
        try {
            int idx = await session.AwaitWinnerAsync();
            if (idx < 0 || idx >= sample.Count) {
                TiLog.Warn($"[SlayTheStreamer2][boss-vote] winnerIndex {idx} out of range [0, {sample.Count}); no swap will be applied");
                winnerIndex = null;
            } else {
                winnerIndex = idx;
            }
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] AwaitWinnerAsync threw; no swap will be applied", ex);
            winnerIndex = null;
        }

        TiLog.Info($"[SlayTheStreamer2][boss-vote] resume: dispatching with winnerIndex={(winnerIndex?.ToString() ?? "null")}");
        coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, sample, winnerIndex, runIdAtStart));
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][boss-vote] HandleVoteAsync threw; attempting no-winner fallback resume", ex);
        try {
            coordinator.Dispatcher.Post(() => ResumeOnMainThread(room, sample, winnerIndex: null, runIdAtStart));
        } catch (Exception postEx) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] fallback resume Post itself threw; resetting flags", postEx);
            Interlocked.Exchange(ref _resumeInProgress, 0);
            Interlocked.Exchange(ref _voteInProgress, 0);
        }
    }
}
```

- [ ] **Step 6: Implement `ResumeOnMainThread`**

Add:

```csharp
private static void ResumeOnMainThread(
    NTreasureRoom room,
    IReadOnlyList<EncounterModel> sample,
    int? winnerIndex,
    string? runIdAtStart) {
    Interlocked.Exchange(ref _resumeInProgress, 1);
    try {
        if (!GodotObject.IsInstanceValid(room)) {
            TiLog.Warn("[SlayTheStreamer2][boss-vote] resume: room no longer valid; dropping");
            SendIgnoredResultReceipt();
            return;
        }

        // Liveness checks (mirror AncientVotePatch / CardRewardVotePatch).
        IRunState? currentState;
        try {
            var rm = RunManager.Instance;
            if (rm is null) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: RunManager.Instance is null");
                SendIgnoredResultReceipt();
                return;
            }
            if (rm.IsAbandoned) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run was abandoned during vote");
                SendIgnoredResultReceipt();
                return;
            }
            currentState = rm.DebugOnlyGetState();
            if (currentState is null) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run state is gone");
                SendIgnoredResultReceipt();
                return;
            }
            if (currentState.IsGameOver) {
                TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run is over (player dead)");
                SendIgnoredResultReceipt();
                return;
            }
            if (runIdAtStart is not null) {
                string? currentRunId = currentState.Rng?.StringSeed;
                if (currentRunId != runIdAtStart) {
                    TiLog.Warn("[SlayTheStreamer2][boss-vote] resume aborted: run changed during vote");
                    SendIgnoredResultReceipt();
                    return;
                }
            }
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][boss-vote] resume aborted: liveness check threw ({ex.Message})");
            SendIgnoredResultReceipt();
            return;
        }

        // Apply boss swap if we have a valid winner.
        if (winnerIndex.HasValue) {
            try {
                var winnerEncounter = BossVoteResolver.ResolveWinner(sample, winnerIndex.Value);
                TiLog.Info($"[SlayTheStreamer2][boss-vote] resume: applying boss swap to {winnerEncounter.Id}");
                ApplyBossSwap(currentState, winnerEncounter);
            } catch (Exception ex) {
                TiLog.Error("[SlayTheStreamer2][boss-vote] ApplyBossSwap threw; preserving vanilla boss", ex);
            }
        } else {
            TiLog.Info("[SlayTheStreamer2][boss-vote] resume: no winner; preserving vanilla boss");
        }

        // Synthetic Proceed re-click. _resumeInProgress=1 makes the prefix pass through.
        try {
            room.OnProceedButtonPressed();
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][boss-vote] synthetic OnProceedButtonPressed threw", ex);
        }
    } catch (Exception ex) {
        TiLog.Error("[SlayTheStreamer2][boss-vote] resume threw", ex);
    } finally {
        // Order matters: clear _resumeInProgress first, then _voteInProgress.
        Interlocked.Exchange(ref _resumeInProgress, 0);
        Interlocked.Exchange(ref _voteInProgress, 0);
    }
}
```

If Step 4 of the spike found `OnProceedButtonPressed` is private, replace `room.OnProceedButtonPressed();` above with a cached reflective invoke matching `CardRewardVotePatch._selectCardMethod`'s shape.

- [ ] **Step 7: Implement `SendIgnoredResultReceipt`**

Add:

```csharp
private static void SendIgnoredResultReceipt() {
    var coordinator = Voter.Default;
    var state = coordinator?.Chat?.State;
    if (state != ChatConnectionState.ConnectedReadWrite) {
        TiLog.Warn($"[SlayTheStreamer2][boss-vote] ignored-result receipt skipped: chat state is {state?.ToString() ?? "null"}");
        return;
    }
    _ = coordinator!.Chat.SendMessageAsync(
        "Vote result ignored — run abandoned during boss vote",
        OutgoingMessagePriority.Normal);
    TiLog.Info("[SlayTheStreamer2][boss-vote] ignored-result receipt queued");
}
```

- [ ] **Step 8: Update test csproj to exclude `BossVotePatch.cs` from `Compile`**

Edit `tests/slay_the_streamer_2.tests.csproj`. Find the existing `Compile Remove` lines (currently lines 23-26):

```xml
    <Compile Remove="..\src\Game\DecisionVotes\AncientVotePatch.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\SpikePatch.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\CardRewardVotePatch.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\CardRewardSkipGatePatch.cs" />
```

Add a fifth line:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\BossVotePatch.cs" />
```

- [ ] **Step 9: Update `ModEntry.cs` comment**

In `src/ModEntry.cs:177`, find:

```csharp
            //    AncientVotePatch attaches here via PatchAll.
```

Replace with:

```csharp
            //    AncientVotePatch + CardRewardVotePatch (+ skip-gate sibling) + BossVotePatch
            //    attach here via PatchAll.
```

- [ ] **Step 10: Full build to verify**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds. If `OnProceedButtonPressed` turns out to be private (per spike Step 4), the direct-call line in Step 6 will fail to compile — switch to the reflective invoke shape at that point.

- [ ] **Step 11: Run tests to verify no regressions**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass; new sampler/seed/resolver tests pass; `BossVotePatch.cs` is excluded from test compile so it doesn't affect the test run.

- [ ] **Step 12: Commit**

```bash
git add src/Game/DecisionVotes/BossVotePatch.cs src/ModEntry.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "$(cat <<'EOF'
plan-b-3/7.1: BossVotePatch — Harmony glue for chest-room boss vote

Suspend-and-resume Harmony prefix on NTreasureRoom.OnProceedButtonPressed.
Two-flag (_voteInProgress + _resumeInProgress) re-entry guard verbatim
from CardRewardVotePatch. Three-way prefix branch: synthetic-resume
passes through, double-click suppressed, fresh click acquires the
vote flag atomically.

PrefixContinue handles candidate sampling: materialize
AllBossEncounters once, exclude SecondBossEncounter on A10+ DoubleBoss
(defensively null-guarded), pool < 3 warn-logs (future-proofing
against MegaCrit content updates), pool <= 1 releases flag and bails
to vanilla. Sample via BossCandidateSampler seeded by
BossVoteSeed.Stable(StringSeed, ActIndex) — stable FNV-1a hash so
save-reload produces the same candidates (Smoke H verifies
cross-process determinism).

HandleVoteAsync produces int? winnerIndex; out-of-range and exception
paths both produce null. ResumeOnMainThread skips ApplyBossSwap on
null but still fires the synthetic OnProceedButtonPressed re-click,
preserving the "no lost click" invariant while preserving the vanilla
boss when chat or vote machinery failed.

ApplyBossSwap is a runtime override hook (defaults to
MapCmd.SetBossEncounter); the testable winner-index → option mapping
is in BossVoteResolver. SendIgnoredResultReceipt sends a hardcoded
string via Voter.Default.Chat.SendMessageAsync — no EnglishReceipts
modification needed (the generic open/tally/close receipts work for
boss votes as-is via VoteSession's formatter).

Patch excluded from Compile in the test csproj (5th Compile Remove
line). ModEntry comment at line 177 updated.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: Build + install + operator validation

**Goal:** Run all 9 smokes from the v3 spec. Record results. Any failure here is a stop-and-fix, not a continue-with-known-issues.

**Files:**
- Run: `build.ps1`, `install.ps1`
- Modify: `notes/06-followups-and-deferred.md` (append acceptance-gate results)

- [ ] **Step 1: Full build**

```powershell
pwsh -File build.ps1
```

Expected: `dist/` rebuilt; `dotnet test` green inside the script. If `build.ps1` fails, fix the underlying issue — do NOT skip. (Per CLAUDE.md: skipping `build.ps1` and re-running `install.ps1` will copy stale `dist/` and the version hash in `godot.log` will not match git HEAD.)

- [ ] **Step 2: Install to game folder**

```powershell
pwsh -File install.ps1
```

Expected: `dist/` copied to the Steam mods folder. Confirm by checking the timestamp of `<steam>/Slay the Spire 2/mods/slay_the_streamer_2/dist/slay_the_streamer_2.dll` matches the build.

- [ ] **Step 3: Smoke A — Act 1 happy path**

Start a standard run with chat connected (Twitch and/or YouTube per the JSON config). Play through Act 1 to the chest room. Click Proceed.

Verify:
- Popup appears with 3 portraits.
- 30s timer counts down.
- Chat votes via `!vote #N` (or bare `N`) — votes register in the popup's tally bars.
- Popup closes after 30s.
- Top-bar boss icon updates to the chosen boss.
- Walk to the boss → the chat-picked boss fight starts.
- `godot.log` contains `[SlayTheStreamer2][boss-vote] target resolved`, `opening vote for 3 options; seed=…`, `sampled candidates: …`, `resume: applying boss swap to …`.
- After the chest→map transition, no orphaned `CanvasLayer` (visually check stream feed and `godot.log` for any popup-cleanup errors).
- Pressing Enter / Space / gamepad-A during the vote does NOT advance Proceed.
- Stream-feed visual check: corner `VoteTallyLabel` + modal `BossVotePopup` showing the same tally — if it feels redundant on a typical viewer-aspect-ratio capture, note as polish item for v0.2; do NOT block acceptance.

- [ ] **Step 4: Smoke B — Acts 2 and 3 (non-DoubleBoss)**

Similar smoke on Acts 2 and 3 below A10 ascension. Use the DevConsole (`` ` `` to open, then `act 2` / `act 3`) to jump between acts and reach each chest room.

Verify per act: same checks as Smoke A.

- [ ] **Step 5: Smoke C — A10+ DoubleBoss**

Start an A10 run, reach Act 3 chest room.

Verify:
- `godot.log` contains `[SlayTheStreamer2][boss-vote] HasSecondBoss=true; excluding {id}` with the pre-rolled second boss's Id.
- Popup's 3 candidates do NOT include the second boss.
- Vote on a primary candidate.
- After the swap: primary boss in the map ≠ pre-rolled second boss.
- Walk to Act 3 boss → primary fight, then second-boss fight, two distinct encounters.

- [ ] **Step 6: Smoke D — run abandoned mid-vote**

Open chest-room vote, then abandon the run via the pause menu.

Verify:
- `godot.log` contains `[SlayTheStreamer2][boss-vote] resume aborted: run was abandoned during vote`.
- Chat sees BOTH the normal close receipt (`"Vote: ... — Chat chose ..."`) AND the ignored-result receipt (`"Vote result ignored — run abandoned during boss vote"`). Document the dual-receipt order.
- No orphaned `BossVotePopupCanvasLayer` in the scene tree (inspect via remote-debug or log "no leak" check). 

- [ ] **Step 7: Smoke E — chat disabled**

In `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`, remove the `channel` or rename the file. Restart the game.

Verify:
- `godot.log` shows no Twitch connection or "no settings file at …".
- Open a chest room, click Proceed → vanilla flow runs (no popup, no vote).
- `godot.log` contains `[SlayTheStreamer2][boss-vote] chat not readable (state=Disconnected); bailing to vanilla` (or similar).

Restore the settings file when done.

- [ ] **Step 8: Smoke F — multiplayer bail**

Start a 2-player run (if multiplayer is reachable in this build).

Verify:
- Open a chest room, click Proceed → vanilla flow runs.
- `godot.log` contains `[SlayTheStreamer2][boss-vote] multiplayer detected (Players.Count > 1); bailing to vanilla`.

If multiplayer is not accessible in the current build, skip and document the gap.

- [ ] **Step 9: Smoke G — first-defeat achievement check**

In Standard Mode (not A10+), pick an unlocked but not-yet-defeated boss (or use a fresh modded save). Vote chat-pick that boss as the act boss. Defeat it.

Verify the first-defeat achievement registers — check via Steam achievement progress and/or in-game collection tracker.

- [ ] **Step 10: Smoke H — save & reload mid-vote (cross-process determinism)**

Open chest-room vote. Note the exact 3 candidates shown in the popup (write them down). Open the pause menu → Save & Quit. Fully close the game and re-launch (this is the cross-process test, not just a reload).

Re-open the save. Streamer is back at the chest room with the pre-Proceed state. Click Proceed.

Verify:
- The popup shows **the same 3 candidates** as before. Same order is bonus but not required (the popup display order is the sample order; if `BossCandidateSampler` is deterministic under the same seed, same order is expected).
- `godot.log` shows the same `seed=…` value as before.
- Vote proceeds normally.

If the candidates differ → `BossVoteSeed.Stable` is not actually cross-process stable; bisect: confirm the FNV-1a unit tests pass within-process (they should), then check for accidentally using `HashCode.Combine` somewhere in the patch.

- [ ] **Step 11: Smoke I — relic-collection overlay mid-vote**

Open a chest, click on the relic in the chest to add it to the deck, click Proceed to open the vote. While the vote is running, open the relic-collection overlay (deck button or similar).

Verify:
- Popup stays modal — input is swallowed.
- Vote continues counting down.
- When vote completes, synthetic Proceed re-click works correctly:
  - If the overlay is still open: behavior matches what spike Step 10 documented (no-op vs auto-close vs throw).
  - Close the overlay manually: chest → map transition proceeds normally with the chat-picked boss.

- [ ] **Step 12: Append acceptance-gate results to `notes/06-followups-and-deferred.md`**

Append a section to `notes/06-followups-and-deferred.md` documenting:
- Date of validation.
- One line per smoke with pass/fail/skipped + any caveats.
- Dual-receipt behavior on Smoke D (order: close first, ignored second).
- The exact `seed=…` value from Smoke H, demonstrating it matched across processes.
- Any anomalies observed (e.g., portrait load failures, BBCode rendering quirks).
- Confirmation that `[SlayTheStreamer2][boss-vote]` log lines are present and `[ancient-vote]` / `[card-vote]` are also present from prior slices (no regressions).

Follow the existing structure used for B.1 / B.2.1 / yt-chat acceptance-gate entries in that file.

- [ ] **Step 13: Commit acceptance-gate results**

```bash
git add notes/06-followups-and-deferred.md
git commit -m "$(cat <<'EOF'
plan-b-3/8.1: record acceptance-gate results

Operator-validation passed for Smokes A-I: Act 1 happy path, Acts
2/3 coverage, A10+ DoubleBoss exclusion, run-abandon mid-vote
(dual-receipt confirmed), chat-disabled bail, multiplayer bail,
first-defeat achievement, save-reload cross-process determinism
(stable hash works), and relic-collection-overlay interaction.
Recorded in notes/06-followups-and-deferred.md per the standard
slice-completion pattern.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: README bump + tag complete

**Goal:** Move B.3 from "remaining" to "shipped" in the README, then tag the slice.

**Files:**
- Modify: `README.md`
- Tag: `plan-b-3-complete`

- [ ] **Step 1: Update README.md**

In `README.md`, locate the "Remaining v0.1 slices" line (or v0.2 line, whichever currently lists `B.3 act-boss vote`). Remove `B.3` from the remaining-slices list and add a shipped-line entry above (mirror the format of the B.1 / B.2.1 / B.2.2 shipped lines):

```markdown
- **B.3 act-boss vote** shipped 2026-MM-DD (`plan-b-3-complete` tag) — chat votes on the act boss after the chest room. Up to 3 random candidates sampled via stable hash of (StringSeed, ActIndex); A10+ DoubleBoss-aware exclusion of the pre-rolled second boss; modal CanvasLayer popup with portrait columns and live tallies; suspend-and-resume Harmony pattern on NTreasureRoom.OnProceedButtonPressed.
```

Replace `2026-MM-DD` with today's actual date.

- [ ] **Step 2: Commit the README update**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
plan-b-3/9.1: README — move B.3 from remaining to shipped

Chat now votes on the next act's boss after the chest room. Up to
3 random candidates from AllBossEncounters, A10+ DoubleBoss
duplicate-boss prevention via SecondBoss exclusion, modal popup
overlay, and save-reload determinism via stable FNV-1a hash of
(StringSeed, ActIndex).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 3: Tag the slice complete**

```bash
git tag plan-b-3-complete
git push origin main --tags
```

Expected: tag pushed; the slice is now considered shipped.

---

## Done criteria

- All 9 tasks' commits are on `main`.
- `plan-b-3-complete` tag is pushed.
- README.md no longer lists B.3 as remaining.
- `notes/06-followups-and-deferred.md` has an acceptance-gate entry for B.3.
- In-game: chat votes apply on every act's chest exit; A10+ Act 3 never produces duplicate primary/second bosses; save-reload preserves the candidate set; popup behaves as a true modal (input swallowing); cancellation/abandon paths produce both close-receipt and ignored-result-receipt without orphaned UI.
- Log tag `[boss-vote]` appears throughout; no `[neow-vote]` / `[card-vote]` / `[ancient-vote]` regressions.
