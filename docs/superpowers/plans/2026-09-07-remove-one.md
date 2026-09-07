# remove-one/ Implementation Plan (chat removes an option, unskippable rewards, per-origin rules, text)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Non-combat card rewards stop being plain pick votes: Ancient-relic and Dream Catcher card picks (both screen families) get a "chat removes one option" vote, event and shop-relic card rewards become unskippable, and every affected relic, event option and screen explains itself in text.

**Architecture:** One classifier (`RewardAuthority.Classify`) over three origin tag tables (combat, relic, rest site) decides a mode per `CardReward`. A shared `RemovalVoteFlow` runs the removal vote against an `IRemovalSurface` implemented once for `NCardRewardSelectionScreen` and once for `NChooseACardSelectionScreen`. Unskippable is input restraint on existing patch sites. Text is appended at read time by a `LocTable.GetRawText` postfix from a pure catalogue plus a learned relic-ID registry.

**Tech Stack:** C# / .NET 9, Godot .NET (StS2 Beta v0.111.0), HarmonyLib, xUnit. Build with `pwsh -File build.ps1`, deploy with `pwsh -File install.ps1`. Unit tests: `dotnet test tests/slay_the_streamer_2.tests.csproj`.

**Spec:** `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md` (sections 0 to 6, 8, 9, 10). Read it first; the plan argues from it.

## Global Constraints

- Beta-only: when `CombatOriginTags.TagPatchRegistered` or `CapturePatchRegistered` is false, every card reward classifies `NormalVote` (today's behaviour) with a one-time Warn.
- On screen the verb is always "remove", never "strike". No em dashes in shipped in-game text, chat receipts, code comments, or these files (use a comma, colon or full stop). The README keeps its existing em-dash bullet style, so the two README snippets in Task 12 are the one exception.
- Chat vote indices are 0-based. On removal votes Skip is `#0` when the reward allows skipping, cards are `#1..#N`; without Skip cards are `#0..#N-1`. Reuse `CardRewardOptionLabels`.
- Appended text lead, exactly: `"\n[color=#668CFF]Slay the Streamer:[/color] "`.
- Removed-option paint: solid red `Color(1f, 0.28f, 0.28f, 1f)`, one 0.35 s tween, no pulse, no caption.
- Prefix ordering in every vote patch: `_resumeInProgress` pass-through, then vote-in-progress handling, then the RemoveOne branch, then bail-to-vanilla gates.
- Every Harmony body is wrapped in try/catch and fails OPEN to vanilla; log through `TiLog`.
- Test classes that can trigger `TiLog` MUST carry `[Collection("TiLog.Sink")]`.
- New files under `src/Game/DecisionVotes/` are compiled into the test project by glob. Any new file there that references Godot, Harmony or `MegaCrit.*` types MUST get a `<Compile Remove=...>` line in `tests/slay_the_streamer_2.tests.csproj` in the same task. Files under `src/Game/Ui/` are NOT included unless listed.
- `src/Ti/` stays game-free (no `MegaCrit.*`, no `src/Game/*` references).
- Commit prefix: `remove-one/N:` with N = task number, `.M` for follow-ups inside a task.
- Do not run `install.ps1` until the final task; local play-testing is the operator's gate.

---

## File map

| File | Responsibility | Test project |
|---|---|---|
| `src/Game/DecisionVotes/AuthorityMode.cs` | `AuthorityMode` enum, `RewardOrigin` record, pure `AuthorityRules.Resolve` | included (BCL) |
| `src/Game/DecisionVotes/RelicOriginTags.cs` | relic push/pop around `RelicCmd.Obtain`, `CardReward` ctor postfix, `RelicFor(reward)`, `CurrentObtaining` | Compile Remove |
| `src/Game/DecisionVotes/RestSiteOriginTags.cs` | postfix on `Hook.ModifyRestSiteHealRewards` | Compile Remove |
| `src/Game/DecisionVotes/RewardAuthority.cs` | `Classify`, `ModeOfActiveReward`, `RulesActive`, one-time Warn | Compile Remove |
| `src/Game/DecisionVotes/CombatOriginTags.cs` (modify) | `ShouldVoteOn` delegates to `RewardAuthority` | already removed |
| `src/Game/DecisionVotes/RemovalClickRules.cs` | pure click verdicts after a removal | included |
| `src/Game/DecisionVotes/RemoveVoteReceipts.cs` | pure chat receipt strings for removal votes | included |
| `src/Game/DecisionVotes/RemovalRecords.cs` | per-reward removal record (weak table) | Compile Remove |
| `src/Game/DecisionVotes/IRemovalSurface.cs` | the screen abstraction the flow drives | Compile Remove |
| `src/Game/DecisionVotes/RemovalVoteFlow.cs` | start session, popup, resume, apply record, override-during-vote | Compile Remove |
| `src/Game/DecisionVotes/CardRewardRemovalSurface.cs` | `IRemovalSurface` for `NCardRewardSelectionScreen` + `_Ready` presenter postfix | Compile Remove |
| `src/Game/DecisionVotes/CardRewardVotePatch.cs` (modify) | RemoveOne branches in `SelectCard` / alt prefixes, Skip-flip gate | already removed |
| `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` (modify) | Unskippable Proceed block | already removed |
| `src/Game/DecisionVotes/ChooseACardRemovePatch.cs` | context, `ShowScreen` bind, `SelectHolder`/Skip prefixes, surface | Compile Remove |
| `src/Game/DecisionVotes/TopBarMapButtonGuardPatch.cs` (modify) | also blocks Map during a choose-a-card removal vote | already removed |
| `src/Game/DecisionVotes/UnskippableRewards.cs` | Skip deny, hide/disable, banner, rewards-set flag, header line | Compile Remove |
| `src/Game/DecisionVotes/AuthorityLoc.cs` | pure text catalogue, `SuffixFor`, `Append` | included |
| `src/Game/DecisionVotes/RelicTextRegistry.cs` | built-in + learned relic ids, JSON file | included |
| `src/Game/DecisionVotes/LocTextPatch.cs` | `LocTable.GetRawText` postfix | Compile Remove |
| `src/Game/DecisionVotes/EventOptionGrowPatch.cs` | grows event option buttons carrying the lead | Compile Remove |
| `src/Game/Ui/RemovalVisuals.cs` | red paint tween, clickable toggles | not included |
| `src/Game/Ui/RemovalStatusLine.cs` | banner-anchored status label | not included |
| `src/Game/Ui/CardRewardVotePopup.cs` (modify) | remove mode: title, generalised anchors, winner paint | not included |
| `src/Game/Ui/RewardsHeaderSubLabel.cs` | "Every card reward must be taken here." above the Loot banner | not included |
| `src/Game/DecisionVotes/VoteOverrideBudget.cs` (modify) | removal override receipt | included |
| settings + docs | `ModSettings.cs`, `SettingsBootstrap.cs`, `slay_the_streamer_2.json.example`, `SettingsPanelBuilder.cs`, `README.md`, `notes/06`, `notes/14`, `CLAUDE.md` | n/a |

---

### Task 1: AuthorityMode and the pure rules

**Files:**
- Create: `src/Game/DecisionVotes/AuthorityMode.cs`
- Test: `tests/Game/DecisionVotes/AuthorityRulesTests.cs`

**Interfaces:**
- Produces: `public enum AuthorityMode { Free, NormalVote, RemoveOne, Unskippable }`; `public readonly record struct RewardOrigin(bool CombatTagged, bool RelicTagged, bool RelicAncient, bool RestSiteTagged)`; `public static AuthorityMode AuthorityRules.Resolve(RewardOrigin origin, bool combatTagRegistered, bool combatCardVotesOnly)`; `public static bool AuthorityRules.RulesActive(bool combatTagRegistered, bool combatCardVotesOnly)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Game/DecisionVotes/AuthorityRulesTests.cs
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
    [InlineData(true)]
    [InlineData(false)]
    public void CombatTagUnregistered_EverythingIsNormalVote(bool combatOnly) {
        foreach (var o in new[] { Combat, AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(o, combatTagRegistered: false, combatCardVotesOnly: combatOnly));
    }

    [Fact]
    public void CombatOnlyOn_CombatVotes_EverythingElseFree() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, true));
        foreach (var o in new[] { AncientRelic, ShopRelic, RestSite, Untagged })
            Assert.Equal(AuthorityMode.Free, AuthorityRules.Resolve(o, true, true));
    }

    [Fact]
    public void CombatOnlyOff_TableApplies() {
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(Combat, true, false));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(AncientRelic, true, false));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(ShopRelic, true, false));
        Assert.Equal(AuthorityMode.RemoveOne, AuthorityRules.Resolve(RestSite, true, false));
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(Untagged, true, false));
    }

    [Fact]
    public void CombatTagWinsOverRelicTag() {
        var both = new RewardOrigin(true, true, true, false);
        Assert.Equal(AuthorityMode.NormalVote, AuthorityRules.Resolve(both, true, false));
    }

    [Fact]
    public void RelicTagWinsOverRestSiteTag() {
        var both = new RewardOrigin(false, true, false, true);
        Assert.Equal(AuthorityMode.Unskippable, AuthorityRules.Resolve(both, true, false));
    }

    [Fact]
    public void RulesActive_OnlyWhenRegisteredAndCombatOnlyOff() {
        Assert.True(AuthorityRules.RulesActive(true, false));
        Assert.False(AuthorityRules.RulesActive(true, true));
        Assert.False(AuthorityRules.RulesActive(false, false));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~AuthorityRulesTests`
Expected: build error, `AuthorityRules` not found.

- [ ] **Step 3: Implement**

```csharp
// src/Game/DecisionVotes/AuthorityMode.cs
namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Per-origin behaviour of one card reward (spec section 2).
/// Free = fully vanilla, no mod involvement (the checkbox-On streamer-free path).
/// NormalVote = today's pick vote. RemoveOne = chat votes which option to remove,
/// the streamer picks from the rest. Unskippable = no vote; Skip hidden and denied.</summary>
public enum AuthorityMode { Free, NormalVote, RemoveOne, Unskippable }

/// <summary>The tag facts one CardReward carries. Game code fills this from the
/// origin-tag tables; the rules never see a game type.</summary>
public readonly record struct RewardOrigin(bool CombatTagged, bool RelicTagged, bool RelicAncient, bool RestSiteTagged);

public static class AuthorityRules {
    /// <summary>Spec section 2 evaluation order. An unregistered combat tag (the
    /// game's default branch) means the classifier cannot tell events from combat,
    /// so everything stays a normal vote, exactly today's behaviour.</summary>
    public static AuthorityMode Resolve(RewardOrigin o, bool combatTagRegistered, bool combatCardVotesOnly) {
        if (!combatTagRegistered) return AuthorityMode.NormalVote;
        if (combatCardVotesOnly) return o.CombatTagged ? AuthorityMode.NormalVote : AuthorityMode.Free;
        if (o.CombatTagged) return AuthorityMode.NormalVote;
        if (o.RelicTagged) return o.RelicAncient ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable;
        if (o.RestSiteTagged) return AuthorityMode.RemoveOne;
        return AuthorityMode.Unskippable;
    }

    /// <summary>True when the per-origin rules (and their explanation text) are live.</summary>
    public static bool RulesActive(bool combatTagRegistered, bool combatCardVotesOnly) =>
        combatTagRegistered && !combatCardVotesOnly;
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~AuthorityRulesTests`
Expected: 7 passing (the Theory counts as 2).

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/AuthorityMode.cs tests/Game/DecisionVotes/AuthorityRulesTests.cs
git commit -m "remove-one/1: AuthorityMode + pure AuthorityRules table with tests"
```

---

### Task 2: Relic and rest-site origin tags, RewardAuthority classifier

**Files:**
- Create: `src/Game/DecisionVotes/RelicOriginTags.cs`, `src/Game/DecisionVotes/RestSiteOriginTags.cs`, `src/Game/DecisionVotes/RewardAuthority.cs`
- Modify: `src/Game/DecisionVotes/CombatOriginTags.cs:66-86` (`ShouldVoteOn` body)
- Modify: `tests/slay_the_streamer_2.tests.csproj:31-45` (three Compile Remove lines)

**Interfaces:**
- Consumes: `AuthorityRules.Resolve`, `CombatOriginTags.IsTagged / TryGetActiveReward / TagPatchRegistered / CapturePatchRegistered`.
- Produces: `RelicOriginTags.RelicFor(CardReward) -> RelicModel?`, `RelicOriginTags.CurrentObtaining -> RelicModel?`, `RestSiteOriginTags.IsTagged(CardReward)`, `RewardAuthority.Classify(CardReward?) -> AuthorityMode`, `RewardAuthority.ModeOfActiveReward()`, `RewardAuthority.RulesActive -> bool`.

No unit tests (Harmony patches); the operator matrix covers them. The registration Warn is covered by the existing default-branch row.

- [ ] **Step 1: Add the Compile Remove lines**

In `tests/slay_the_streamer_2.tests.csproj`, after line 45 (`<Compile Remove="..\src\Game\DecisionVotes\VoterSpeechPatch.cs" />`) add:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\RelicOriginTags.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\RestSiteOriginTags.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\RewardAuthority.cs" />
```

- [ ] **Step 2: Write RelicOriginTags**

```csharp
// src/Game/DecisionVotes/RelicOriginTags.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Which relic (if any) produced a CardReward (spec section 2). A prefix on
/// RelicCmd.Obtain pushes the relic; a HarmonyFinalizer pops it (a finalizer, not a
/// postfix: relic.AssertMutable() at RelicCmd.cs:23 is a real synchronous throw path).
/// __state carries "this call pushed" so the pop is exactly as conditional as the push.
/// Orrery, Glass Eye, Kaleidoscope, Lost Coffer and Strongbox construct their rewards
/// before their first await inside AfterObtained, so the stub's finalizer fires after
/// construction; nested obtains (Neow's Bones then Kaleidoscope) push inside a completed
/// pop. Both CardReward constructors are postfixed and tag the reward with the relic on
/// top of the stack. Local metadata only: catch + log, never throw.</summary>
internal static class RelicOriginTags {
    private static readonly ConditionalWeakTable<CardReward, RelicModel> Tags = new();
    private static readonly Stack<RelicModel> Obtaining = new();

    internal static RelicModel? RelicFor(CardReward reward) => Tags.TryGetValue(reward, out var relic) ? relic : null;

    /// <summary>The relic whose AfterObtained is currently running (innermost), or null.</summary>
    internal static RelicModel? CurrentObtaining => Obtaining.Count > 0 ? Obtaining.Peek() : null;

    private static void TagIfObtaining(CardReward instance) {
        try {
            if (Obtaining.Count > 0) {
                var relic = Obtaining.Peek();
                Tags.AddOrUpdate(instance, relic);
                TiLog.Info($"[SlayTheStreamer2][card-scope] tagged relic-origin card reward (relic={relic.Id.Entry}, rarity={relic.Rarity})");
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin tag failed", ex); }
    }

    [HarmonyPatch(typeof(RelicCmd), nameof(RelicCmd.Obtain), typeof(RelicModel), typeof(Player), typeof(int))]
    internal static class ObtainPatch {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        private static void PushPrefix(RelicModel relic, out bool __state) {
            __state = false;
            try { Obtaining.Push(relic); __state = true; }
            catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin push failed", ex); }
        }

        [HarmonyFinalizer]
        private static Exception? PopFinalizer(Exception? __exception, bool __state) {
            try { if (__state && Obtaining.Count > 0) Obtaining.Pop(); }
            catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] relic-origin pop failed", ex); }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(CardCreationOptions), typeof(int), typeof(Player), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorAPatch {
        static void Postfix(CardReward __instance) => TagIfObtaining(__instance);
    }

    [HarmonyPatch(typeof(CardReward), MethodType.Constructor,
        new[] { typeof(IEnumerable<CardModel>), typeof(CardCreationSource), typeof(Player), typeof(CardCreationOptions), typeof(PlayerChoiceSynchronizer) })]
    internal static class CtorBPatch {
        static void Postfix(CardReward __instance) => TagIfObtaining(__instance);
    }
}
```

Verify the `PlayerChoiceSynchronizer` namespace before building: `grep -rn "class PlayerChoiceSynchronizer" decompiled/sts2-v0.111.0 --include=*.cs` and fix the `using` if it is not `MegaCrit.Sts2.Core.Multiplayer`. Same for `CardCreationSource` (expected `MegaCrit.Sts2.Core.Entities.Cards`).

- [ ] **Step 3: Write RestSiteOriginTags**

```csharp
// src/Game/DecisionVotes/RestSiteOriginTags.cs
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Tags every CardReward a rest-site heal produced (Dream Catcher, including
/// Dense Vegetation's mimicked rest). Postfix on the static
/// Hook.ModifyRestSiteHealRewards (sole caller HealRestSiteOption.ExecuteRestSiteHeal).
/// A registration miss silently makes Dream Catcher an unskippable event reward
/// (fail-safe but wrong), so Prepare logs an Error.</summary>
internal static class RestSiteOriginTags {
    private static readonly ConditionalWeakTable<CardReward, object> Tags = new();
    private static readonly object Marker = new();

    internal static bool IsTagged(CardReward reward) => Tags.TryGetValue(reward, out _);

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyRestSiteHealRewards))]
    internal static class TagPatch {
        static bool Prepare(MethodBase? original) {
            if (original is not null) return true;
            if (AccessTools.Method(typeof(Hook), nameof(Hook.ModifyRestSiteHealRewards)) is null) {
                TiLog.Error("[SlayTheStreamer2][card-scope] Hook.ModifyRestSiteHealRewards not found; Dream Catcher rewards will classify as event rewards");
                return false;
            }
            return true;
        }

        static void Postfix(IRunState runState, Player player, List<Reward> rewards) {
            try {
                int tagged = 0;
                foreach (var reward in rewards) {
                    if (reward is CardReward cardReward) { Tags.GetValue(cardReward, _ => Marker); tagged++; }
                }
                if (tagged > 0) TiLog.Info($"[SlayTheStreamer2][card-scope] tagged {tagged} rest-site card reward(s)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] rest-site tagging failed", ex); }
        }
    }
}
```

- [ ] **Step 4: Write RewardAuthority**

```csharp
// src/Game/DecisionVotes/RewardAuthority.cs
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>The single decision point for every card reward (spec section 2).</summary>
internal static class RewardAuthority {
    private static int _degradedWarnFired;

    private static bool CombatTagRegistered => CombatOriginTags.TagPatchRegistered && CombatOriginTags.CapturePatchRegistered;
    private static bool CombatOnly => ModSettings.Current?.CombatCardVotesOnly ?? false;

    /// <summary>True when the per-origin rules and their explanation text apply.</summary>
    internal static bool RulesActive => AuthorityRules.RulesActive(CombatTagRegistered, CombatOnly);

    internal static AuthorityMode Classify(CardReward? reward) {
        if (!CombatTagRegistered) {
            if (Interlocked.CompareExchange(ref _degradedWarnFired, 1, 0) == 0) {
                TiLog.Warn("[SlayTheStreamer2][card-scope] combat-origin tagging did not register (default branch?); every card reward is a normal vote");
            }
            return AuthorityMode.NormalVote;
        }
        if (reward is null) return AuthorityMode.Free;   // unknown reward: fail-safe, streamer picks freely
        var relic = RelicOriginTags.RelicFor(reward);
        var origin = new RewardOrigin(
            CombatTagged: CombatOriginTags.IsTagged(reward),
            RelicTagged: relic is not null,
            RelicAncient: relic is not null && relic.Rarity == RelicRarity.Ancient,
            RestSiteTagged: RestSiteOriginTags.IsTagged(reward));
        return AuthorityRules.Resolve(origin, CombatTagRegistered, CombatOnly);
    }

    /// <summary>Mode of the reward whose selection sub-screen is on screen.</summary>
    internal static AuthorityMode ModeOfActiveReward() => Classify(CombatOriginTags.TryGetActiveReward());
}
```

- [ ] **Step 5: Make CombatOriginTags.ShouldVoteOn delegate**

Replace the body of `ShouldVoteOn` in `src/Game/DecisionVotes/CombatOriginTags.cs` (lines 72-82) with:

```csharp
    internal static bool ShouldVoteOn(CardReward? reward) => RewardAuthority.Classify(reward) == AuthorityMode.NormalVote;
```

Delete the now-unused `_degradedWarnFired` field in that file (the Warn moved to `RewardAuthority`). Keep `ShouldVoteOnActiveReward`. Result: every existing caller (vote prefix, skip gate counting, streamer-Skip budget, Skip-alt flip, counter label) now treats RemoveOne and Unskippable rewards as "not a normal vote", i.e. streamer-free, until later tasks add their behaviour. This is a working intermediate state.

- [ ] **Step 6: Build and run the whole suite**

Run: `pwsh -File build.ps1`
Expected: build OK, all tests pass. If `PlayerChoiceSynchronizer` or `CardCreationSource` fail to resolve, fix the `using` per the grep in Step 2.

- [ ] **Step 7: Commit**

```bash
git add src/Game/DecisionVotes/RelicOriginTags.cs src/Game/DecisionVotes/RestSiteOriginTags.cs src/Game/DecisionVotes/RewardAuthority.cs src/Game/DecisionVotes/CombatOriginTags.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/2: relic + rest-site origin tags, RewardAuthority classifier; ShouldVoteOn delegates"
```

---

### Task 3: Pure removal click rules and removal-vote receipts

**Files:**
- Create: `src/Game/DecisionVotes/RemovalClickRules.cs`, `src/Game/DecisionVotes/RemoveVoteReceipts.cs`
- Modify: `src/Game/DecisionVotes/VoteOverrideBudget.cs` (add `SendRemovalOverrideReceipt`)
- Test: `tests/Game/DecisionVotes/RemovalClickRulesTests.cs`, `tests/Game/DecisionVotes/RemoveVoteReceiptsTests.cs`

**Interfaces:**
- Produces: `public enum RemovalClickVerdict { Deny, Allow, AllowWithOverride }`; `RemovalClickRules.SkipIndex == -1`; `RemovalClickRules.Judge(int clicked, int? removed, int overridesRemaining)`; `RemoveVoteReceipts.Format(VoteSnapshot s, ReceiptKind kind, string streamerName)`; `RemoveVoteReceipts.FormatOverride(string streamerName, string takenLabel, int limit, int remaining, string? curseTitle)`; `VoteOverrideBudget.SendRemovalOverrideReceipt(string takenLabel, string? curseTitle = null)`.
- Index space: cards are their holder index `0..N-1`; Skip is `-1` (`RemovalClickRules.SkipIndex`). Vote indices are a different space (see Task 5).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Game/DecisionVotes/RemovalClickRulesTests.cs
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RemovalClickRulesTests {
    [Fact]
    public void NoRemovalYet_Denies() =>
        Assert.Equal(RemovalClickVerdict.Deny, RemovalClickRules.Judge(clicked: 1, removed: null, overridesRemaining: 3));

    [Fact]
    public void ClickOnOtherCard_Allows() =>
        Assert.Equal(RemovalClickVerdict.Allow, RemovalClickRules.Judge(0, removed: 2, overridesRemaining: 0));

    [Fact]
    public void ClickOnRemovedCard_WithBudget_AllowsWithOverride() =>
        Assert.Equal(RemovalClickVerdict.AllowWithOverride, RemovalClickRules.Judge(2, removed: 2, overridesRemaining: 1));

    [Fact]
    public void ClickOnRemovedCard_NoBudget_Denies() =>
        Assert.Equal(RemovalClickVerdict.Deny, RemovalClickRules.Judge(2, removed: 2, overridesRemaining: 0));

    [Fact]
    public void SkipRemoved_SkipClickCostsOverride() =>
        Assert.Equal(RemovalClickVerdict.AllowWithOverride,
            RemovalClickRules.Judge(RemovalClickRules.SkipIndex, removed: RemovalClickRules.SkipIndex, overridesRemaining: 1));

    [Fact]
    public void SkipRemoved_CardClickIsFree() =>
        Assert.Equal(RemovalClickVerdict.Allow, RemovalClickRules.Judge(0, removed: RemovalClickRules.SkipIndex, overridesRemaining: 0));
}
```

```csharp
// tests/Game/DecisionVotes/RemoveVoteReceiptsTests.cs
using System;
using System.Collections.Generic;
using SlayTheStreamer2.Game.DecisionVotes;
using SlayTheStreamer2.Ti.Voting;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RemoveVoteReceiptsTests {
    private static VoteSnapshot Snap(int? winner = null, int? tieAmong = null, bool noVotes = false,
            IReadOnlyDictionary<int, int>? tallies = null, bool showTag = false, int voteId = 7) {
        var opts = new List<VoteOption> { new(0, "Skip"), new(1, "Bash"), new(2, "Defend") };
        return new VoteSnapshot("remove-x", "Remove an option", opts, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(12),
            tallies ?? new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 },
            winner is null ? VoteSessionState.Open : VoteSessionState.Closed,
            winner, tieAmong, noVotes, TimeSpan.Zero, voteId, showTag);
    }

    [Fact]
    public void Open_NamesStreamerAndIndices() {
        var text = RemoveVoteReceipts.Format(Snap(), ReceiptKind.Open, "Surfinite");
        Assert.Equal("Vote: remove one option from Surfinite's card reward! Type 0, 1, 2. 30s left.", text);
    }

    [Fact]
    public void Open_WithTag_IncludesVoteId() {
        var text = RemoveVoteReceipts.Format(Snap(showTag: true), ReceiptKind.Open, "Surfinite");
        Assert.StartsWith("Vote [07]: remove one option", text);
    }

    [Fact]
    public void Close_RemovedCard() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 1), ReceiptKind.Close, "Surfinite");
        Assert.Equal("Chat removed 1: Bash. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void Close_RemovedSkip_SaysMustTakeACard() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 0), ReceiptKind.Close, "Surfinite");
        Assert.Equal("Chat removed 0: Skip. Surfinite must take a card.", text);
    }

    [Fact]
    public void Close_NoVotes_SaysRandomly() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 2, noVotes: true), ReceiptKind.Close, "Surfinite");
        Assert.Equal("No votes received. Chat removed 2: Defend randomly. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void Close_ThreeWayTie() {
        var text = RemoveVoteReceipts.Format(Snap(winner: 2, tieAmong: 3), ReceiptKind.Close, "Surfinite");
        Assert.Equal("3-way tie! Chat removed 2: Defend randomly. Surfinite picks from the rest.", text);
    }

    [Fact]
    public void PeriodicTally_UsesDefaultFormat() {
        var text = RemoveVoteReceipts.Format(Snap(), ReceiptKind.PeriodicTally, "Surfinite");
        Assert.Equal(EnglishReceipts.FormatPeriodicTally(Snap()), text);
    }

    [Fact]
    public void Override_Limited() =>
        Assert.Equal("Surfinite overrode chat's removal and took Bash. 0 overrides remaining this act",
            RemoveVoteReceipts.FormatOverride("Surfinite", "Bash", limit: 1, remaining: 0, curseTitle: null));

    [Fact]
    public void Override_WithCurse_Unlimited() =>
        Assert.Equal("Surfinite overrode chat's removal and took Skip. Cursed Overrides: gained Injury!",
            RemoveVoteReceipts.FormatOverride("Surfinite", "Skip", limit: -1, remaining: int.MaxValue, curseTitle: "Injury"));
}
```

Check `VoteSnapshot`'s parameter list in `src/Ti/Voting/VoteSnapshot.cs`: the test above assumes a trailing `bool ShowTag` after `VoteId`. If the record has a different order or name, adapt the positional call (the existing `EnglishReceiptsTests.Snap` shows the accepted shape; copy it and add the tag parameter it uses).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~RemovalClickRulesTests|FullyQualifiedName~RemoveVoteReceiptsTests"`
Expected: build errors for the two missing types.

- [ ] **Step 3: Implement**

```csharp
// src/Game/DecisionVotes/RemovalClickRules.cs
namespace SlayTheStreamer2.Game.DecisionVotes;

public enum RemovalClickVerdict { Deny, Allow, AllowWithOverride }

/// <summary>Spec section 3.2: after chat's removal lands, everything is legal except the
/// removed option, which costs a vote override. Index space: card holder index, or
/// <see cref="SkipIndex"/> for Skip. Pure function.</summary>
public static class RemovalClickRules {
    public const int SkipIndex = -1;

    public static RemovalClickVerdict Judge(int clicked, int? removed, int overridesRemaining) {
        if (removed is null) return RemovalClickVerdict.Deny;          // no removal yet: the vote owns the screen
        if (clicked != removed.Value) return RemovalClickVerdict.Allow;
        return overridesRemaining > 0 ? RemovalClickVerdict.AllowWithOverride : RemovalClickVerdict.Deny;
    }
}
```

```csharp
// src/Game/DecisionVotes/RemoveVoteReceipts.cs
using System;
using System.Linq;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Chat receipts for a removal vote (spec section 3). Pure functions; the
/// streamer name is passed in so the class stays testable without settings.</summary>
public static class RemoveVoteReceipts {
    public static string Format(VoteSnapshot s, ReceiptKind kind, string streamerName) => kind switch {
        ReceiptKind.Open => FormatOpen(s, streamerName),
        ReceiptKind.Close => FormatClose(s, streamerName),
        _ => EnglishReceipts.FormatPeriodicTally(s),
    };

    public static string FormatOpen(VoteSnapshot s, string streamerName) {
        var numbers = string.Join(", ", s.Options.Select(o => o.Index.ToString()));
        var tag = s.ShowTag ? $" [{s.VoteId:D2}]" : "";
        return $"Vote{tag}: remove one option from {streamerName}'s card reward! Type {numbers}. {(int)s.Duration.TotalSeconds}s left.";
    }

    public static string FormatClose(VoteSnapshot s, string streamerName) {
        if (s.WinnerIndex is not int idx) return "Vote: removal closed without a result.";
        var label = s.Options.First(o => o.Index == idx).Label;
        string outcome = label == CardRewardOptionLabels.SkipLabel
            ? $"{streamerName} must take a card."
            : $"{streamerName} picks from the rest.";
        string body;
        if (s.NoVotesReceived) body = $"No votes received. Chat removed {idx}: {label} randomly.";
        else if (s.RandomTieAmong is int tied && tied >= 3) body = $"{tied}-way tie! Chat removed {idx}: {label} randomly.";
        else if (s.RandomTieAmong is int) {
            var max = s.Tallies.Values.Max();
            var tiedLabels = string.Join(" and ", s.Tallies.Where(kv => kv.Value == max)
                .Select(kv => $"{kv.Key} {s.Options.First(o => o.Index == kv.Key).Label}"));
            body = $"Tie between {tiedLabels}. Chat removed {idx}: {label} randomly.";
        } else body = $"Chat removed {idx}: {label}.";
        if (s.DisconnectGap > TimeSpan.Zero)
            body = body.TrimEnd('.') + $" (chat was offline {(int)s.DisconnectGap.TotalSeconds}s during voting).";
        return body + " " + outcome;
    }

    public static string FormatOverride(string streamerName, string takenLabel, int limit, int remaining, string? curseTitle) {
        string curse = curseTitle is null ? "" : $" Cursed Overrides: gained {curseTitle}!";
        if (limit < 0) return $"{streamerName} overrode chat's removal and took {takenLabel}.{curse}";
        string noun = remaining == 1 ? "override" : "overrides";
        return $"{streamerName} overrode chat's removal and took {takenLabel}.{curse} {remaining} {noun} remaining this act";
    }
}
```

Add to `VoteOverrideBudget` (after `SendOverrideReceipt`):

```csharp
    /// <summary>Removal-vote flavour of the override receipt (spec section 3.2).</summary>
    public static void SendRemovalOverrideReceipt(string takenLabel, string? curseTitle = null) {
        var coordinator = Voter.Default;
        if (coordinator?.Chat?.State != ChatConnectionState.ConnectedReadWrite) return;
        string text = RemoveVoteReceipts.FormatOverride(
            BootstrapModSettings.GetStreamerDisplayName(), takenLabel, Limit, Remaining, curseTitle);
        _ = coordinator.Chat.SendMessageAsync(text, OutgoingMessagePriority.High);
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~RemovalClickRulesTests|FullyQualifiedName~RemoveVoteReceiptsTests"`
Expected: 15 passing.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/RemovalClickRules.cs src/Game/DecisionVotes/RemoveVoteReceipts.cs src/Game/DecisionVotes/VoteOverrideBudget.cs tests/Game/DecisionVotes/RemovalClickRulesTests.cs tests/Game/DecisionVotes/RemoveVoteReceiptsTests.cs
git commit -m "remove-one/3: RemovalClickRules + RemoveVoteReceipts (pure, tested); removal override receipt"
```

---

### Task 4: Removal visuals and the status line (Godot UI helpers)

**Files:**
- Create: `src/Game/Ui/RemovalVisuals.cs`, `src/Game/Ui/RemovalStatusLine.cs`

**Interfaces:**
- Produces: `RemovalVisuals.RemovedRed`; `RemovalVisuals.PaintRemoved(Control target, Node tweenOwner) -> Tween?`; `RemovalVisuals.SetClickable(Control option, bool clickable)` (handles `NCardHolder.SetClickable` and `NClickableControl.Enable/Disable`); `RemovalStatusLine.Attach(Node parent, Control? bannerAnchor, Func<string> textProvider) -> RemovalStatusLine`; instance `.Detach()`.

Not unit-testable (Godot). Verified in the operator matrix.

- [ ] **Step 1: Write RemovalVisuals**

```csharp
// src/Game/Ui/RemovalVisuals.cs
using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>The removal look (spec section 3): removed = solid red, one 0.35 s tween,
/// no pulse, no caption. Visual only: catch + log.</summary>
internal static class RemovalVisuals {
    internal static readonly Color RemovedRed = new(1f, 0.28f, 0.28f, 1f);

    internal static Tween? PaintRemoved(Control target, Node tweenOwner) {
        try {
            if (!GodotObject.IsInstanceValid(target) || !GodotObject.IsInstanceValid(tweenOwner)) return null;
            var tween = tweenOwner.CreateTween();
            tween.TweenProperty(target, "modulate", RemovedRed, 0.35).SetTrans(Tween.TransitionType.Sine);
            return tween;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] removed paint failed", ex); return null; }
    }

    /// <summary>Card holders keep hover/inspect alive when unclickable; buttons are
    /// fully disabled (also unregisters their hotkeys).</summary>
    internal static void SetClickable(Control option, bool clickable) {
        try {
            if (!GodotObject.IsInstanceValid(option)) return;
            switch (option) {
                case NCardHolder holder: holder.SetClickable(clickable); break;
                case NClickableControl button: if (clickable) button.Enable(); else button.Disable(); break;
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] clickable toggle failed", ex); }
    }
}
```

- [ ] **Step 2: Write RemovalStatusLine**

```csharp
// src/Game/Ui/RemovalStatusLine.cs
using System;
using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>A one-line status label under the vanilla "Choose a Card" banner, polled
/// per frame (assign-on-change) from a text provider. Parented under the screen so
/// Godot frees it with the screen. Same Kreon face as the vote title, smaller.</summary>
internal sealed partial class RemovalStatusLine : Control {
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const int FontSize = 28;
    private const float GapBelowBanner = 70f;
    private const float FallbackTop = 260f;
    private static readonly Color TextColor = new(1f, 0.964706f, 0.886275f, 1f);

    private Label? _label;
    private Control? _banner;
    private Func<string>? _text;
    private string _last = "";

    internal static RemovalStatusLine Attach(Node parent, Control? bannerAnchor, Func<string> textProvider) {
        var line = new RemovalStatusLine {
            Name = "SlayTheStreamerRemovalStatusLine",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        line._banner = bannerAnchor;
        line._text = textProvider;
        line._label = new Label {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
        };
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) line._label.AddThemeFontOverride("font", font);
        line._label.AddThemeColorOverride("font_color", TextColor);
        line._label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        line._label.AddThemeConstantOverride("shadow_offset_x", 2);
        line._label.AddThemeConstantOverride("shadow_offset_y", 2);
        line._label.AddThemeFontSizeOverride("font_size", FontSize);
        line.AddChild(line._label);
        parent.AddChild(line);
        return line;
    }

    internal void Detach() {
        if (GodotObject.IsInstanceValid(this) && !IsQueuedForDeletion()) QueueFree();
    }

    public override void _Process(double delta) {
        try {
            if (_label is null) return;
            string text = _text?.Invoke() ?? "";
            if (text != _last) { _last = text; _label.Text = text; }
            Visible = text.Length > 0;
            float cx, top;
            if (_banner is not null && GodotObject.IsInstanceValid(_banner)) {
                var pos = _banner.GlobalPosition; var size = _banner.Size * _banner.Scale;
                cx = pos.X + size.X * 0.5f; top = pos.Y + size.Y + GapBelowBanner;
            } else { cx = GetViewportRect().Size.X * 0.5f; top = FallbackTop; }
            _label.OffsetLeft = cx - 520f; _label.OffsetRight = cx + 520f;
            _label.OffsetTop = top; _label.OffsetBottom = top + 44f;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] status line placement failed", ex); }
    }
}
```

Note the anchors are set as initializer properties, never via `SetAnchorsPreset` on a fresh Control (the zero-rect landmine), and positions come from the banner per frame, never from `GetGlobalRect` in `_Ready`.

- [ ] **Step 3: Build**

Run: `pwsh -File build.ps1`
Expected: build OK (files are compiled by the Godot project; the test project does not include `src/Game/Ui/*`).

- [ ] **Step 4: Commit**

```bash
git add src/Game/Ui/RemovalVisuals.cs src/Game/Ui/RemovalStatusLine.cs
git commit -m "remove-one/4: RemovalVisuals (red paint, clickable toggle) + RemovalStatusLine"
```

---

### Task 5: Popup remove mode with generalised anchors

**Files:**
- Modify: `src/Game/Ui/CardRewardVotePopup.cs`

**Interfaces:**
- Produces: a second constructor `CardRewardVotePopup(VoteSession session, IMainThreadDispatcher dispatcher, Node screen, IReadOnlyList<Control> cardHolders, Control? skipControl, Control? bannerAnchor, bool includeSkip, bool removeMode, Func<bool>? isRunDying, Func<bool>? isOccludingOverlayVisible)`. The existing constructor keeps working (it resolves holders, Skip and banner from the `NCardRewardSelectionScreen` and calls the new one with `removeMode: false`).
- Behaviour in remove mode: title `"Chat is choosing which option to remove"` (with the `[NN]` tag prefix as today); on `Closed`, before freeing, the winner's anchor is painted red via `RemovalVisuals.PaintRemoved(anchor, screen)` so the paint survives the popup.

- [ ] **Step 1: Refactor the constructor and anchor resolution**

Replace the fields `_screen`, `_includeSkip` usage and `ResolveAnchors` / `TryFindSkipButton` with explicit inputs:

```csharp
    private readonly Node _screen;                         // was NCardRewardSelectionScreen
    private readonly IReadOnlyList<Control> _cardHolders;
    private readonly Control? _skipControl;
    private readonly bool _includeSkip;
    private readonly bool _removeMode;

    /// <summary>Card-reward screen convenience: resolves holders (sorted by X), the Skip
    /// alternative button (first child of UI/RewardAlternatives) and the UI/Banner anchor.</summary>
    public CardRewardVotePopup(
            VoteSession session, IMainThreadDispatcher dispatcher, NCardRewardSelectionScreen screen,
            bool includeSkip, Func<bool>? isRunDying = null, Func<bool>? isOccludingOverlayVisible = null)
        : this(session, dispatcher, screen,
               ResolveCardHolders(screen), TryFindSkipButton(screen), screen.GetNodeOrNull<Control>("UI/Banner"),
               includeSkip, removeMode: false, isRunDying, isOccludingOverlayVisible) { }

    /// <summary>General form, used by both screen families (spec section 4).</summary>
    public CardRewardVotePopup(
            VoteSession session, IMainThreadDispatcher dispatcher, Node screen,
            IReadOnlyList<Control> cardHolders, Control? skipControl, Control? bannerAnchor,
            bool includeSkip, bool removeMode,
            Func<bool>? isRunDying = null, Func<bool>? isOccludingOverlayVisible = null) {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _cardHolders = cardHolders;
        _skipControl = skipControl;
        _bannerAnchor = bannerAnchor;
        _includeSkip = includeSkip;
        _removeMode = removeMode;
        _isRunDying = isRunDying;
        _isOccludingOverlayVisible = isOccludingOverlayVisible;
    }

    private static IReadOnlyList<Control> ResolveCardHolders(NCardRewardSelectionScreen screen) {
        var cardRow = _cardRowField.Value?.GetValue(screen) as Control;
        if (cardRow is null) {
            TiLog.Warn("[SlayTheStreamer2][card-vote-popup] _cardRow field not found on screen; per-card indicators omitted");
            return Array.Empty<Control>();
        }
        return cardRow.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList();
    }

    private static Control? TryFindSkipButton(NCardRewardSelectionScreen screen) {
        try {
            var container = screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
            if (container is null || container.GetChildCount() == 0) return null;
            return container.GetChild(0) as Control;
        } catch (Exception ex) {
            TiLog.Warn($"[SlayTheStreamer2][card-vote-popup] TryFindSkipButton threw: {ex.Message}");
            return null;
        }
    }

    private IEnumerable<(Control Anchor, int VoteIndex, bool IsSkip)> ResolveAnchors() {
        var results = new List<(Control, int, bool)>();
        int voteCursor = 0;
        if (_includeSkip) {
            if (_skipControl is not null) results.Add((_skipControl, voteCursor, true));
            else TiLog.Warn("[SlayTheStreamer2][card-vote-popup] no Skip control; #0 indicator omitted");
            voteCursor++;
        }
        foreach (var holder in _cardHolders) { results.Add((holder, voteCursor, false)); voteCursor++; }
        return results;
    }
```

In `Show()`: delete the `_bannerAnchor = _screen.GetNodeOrNull<Control>("UI/Banner")` lookup (it is now a constructor input; keep the Warn when it is null) and set the title text:

```csharp
        string titleBody = _removeMode ? "Chat is choosing which option to remove" : "Pick the worst option.";
        _titleLabel = new Label { Name = "Title", Text = $"{voteHint}{titleBody}", ... };
```

Replace the closed handler:

```csharp
        _closedHandler = (_, _) => _dispatcher.Post(() => { PaintWinnerIfRemoveMode(); SafeQueueFree(); });
```

and add:

```csharp
    private void PaintWinnerIfRemoveMode() {
        if (!_removeMode) return;
        try {
            if (_session.WinnerIndex is not int winner) return;
            foreach (var lbl in _optionLabels) {
                if (lbl.VoteIndex == winner) { RemovalVisuals.PaintRemoved(lbl.Anchor, _screen); return; }
            }
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-vote-popup] winner paint failed: {ex.Message}"); }
    }
```

Check `VoteSession` exposes the winner after close: `grep -n "WinnerIndex" src/Ti/Voting/VoteSession.cs`. If it only exposes it through `Snapshot()`, use `_session.Snapshot().WinnerIndex`.

- [ ] **Step 2: Build**

Run: `pwsh -File build.ps1`
Expected: build OK, tests unchanged (the popup is not in the test project). The existing card vote still uses the convenience constructor.

- [ ] **Step 3: Commit**

```bash
git add src/Game/Ui/CardRewardVotePopup.cs
git commit -m "remove-one/5: CardRewardVotePopup remove mode + generalised anchors for both screen families"
```

---

### Task 6: RemovalRecords, IRemovalSurface and RemovalVoteFlow

**Files:**
- Create: `src/Game/DecisionVotes/RemovalRecords.cs`, `src/Game/DecisionVotes/IRemovalSurface.cs`, `src/Game/DecisionVotes/RemovalVoteFlow.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (three Compile Remove lines)

**Interfaces:**
- Produces:
  - `internal sealed record RemovalRecord(int RemovedIndex, string RemovedLabel, object? OptionsSnapshot)` (RemovedIndex uses the Task 3 space: card index or `RemovalClickRules.SkipIndex`).
  - `RemovalRecords.Get(object key) -> RemovalRecord?`, `.Set(object key, RemovalRecord)`, `.Clear(object key)`.
  - `interface IRemovalSurface { Node ScreenNode {get;} object RecordKey {get;} string LogTag {get;} IReadOnlyList<string> CardTitles(); bool HasSkip {get;} IReadOnlyList<Control> CardHolders(); Control? SkipControl(); Control? BannerAnchor(); object? SnapshotOptions(); bool OptionsMatch(object? snapshot); }`
  - `RemovalVoteFlow.TryStart(IRemovalSurface surface, Action onFinished) -> bool` (false = bailed, caller lets vanilla proceed); `RemovalVoteFlow.ActiveSurface -> IRemovalSurface?`; `RemovalVoteFlow.IsActive -> bool`; `RemovalVoteFlow.TryOverrideDuringVote(string takenLabel) -> bool` (cancels the vote with no record and spends an override; caller then lets the click reach vanilla); `RemovalVoteFlow.EffectiveRecord(IRemovalSurface) -> RemovalRecord?` (record whose snapshot still matches, else clears and returns null); `RemovalVoteFlow.ApplyRecordVisuals(IRemovalSurface, RemovalRecord)` (paint + clickable at budget 0); `RemovalVoteFlow.StatusText(IRemovalSurface, bool hasReroll) -> string`.

- [ ] **Step 1: Compile Remove lines**

Add to `tests/slay_the_streamer_2.tests.csproj` next to the Task 2 lines:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\RemovalRecords.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\IRemovalSurface.cs" />
    <Compile Remove="..\src\Game\DecisionVotes\RemovalVoteFlow.cs" />
```

- [ ] **Step 2: RemovalRecords and IRemovalSurface**

```csharp
// src/Game/DecisionVotes/RemovalRecords.cs
using System.Runtime.CompilerServices;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>What chat removed on one reward (spec section 3.1). RemovedIndex is a card
/// holder index or RemovalClickRules.SkipIndex. OptionsSnapshot is surface-defined
/// (the CardCreationResult array for the reward screen; null for choose-a-card, whose
/// option list never changes).</summary>
internal sealed record RemovalRecord(int RemovedIndex, string RemovedLabel, object? OptionsSnapshot) {
    public bool IsSkip => RemovedIndex == RemovalClickRules.SkipIndex;
}

/// <summary>Keyed on the reward object (or the choose-a-card context), not the screen,
/// so ESC-and-reopen shows the same removal without a new vote.</summary>
internal static class RemovalRecords {
    private static readonly ConditionalWeakTable<object, RemovalRecord> Table = new();
    internal static RemovalRecord? Get(object key) => Table.TryGetValue(key, out var r) ? r : null;
    internal static void Set(object key, RemovalRecord record) => Table.AddOrUpdate(key, record);
    internal static void Clear(object key) => Table.Remove(key);
}
```

```csharp
// src/Game/DecisionVotes/IRemovalSurface.cs
using System.Collections.Generic;
using Godot;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>What RemovalVoteFlow needs from a screen. Implemented for the card-reward
/// screen (CardRewardRemovalSurface) and the choose-a-card screen
/// (ChooseACardRemovePatch.Surface). All members run on the main thread.</summary>
internal interface IRemovalSurface {
    Node ScreenNode { get; }
    object RecordKey { get; }
    string LogTag { get; }
    bool HasSkip { get; }
    IReadOnlyList<string> CardTitles();
    IReadOnlyList<Control> CardHolders();
    Control? SkipControl();
    Control? BannerAnchor();
    object? SnapshotOptions();
    bool OptionsMatch(object? snapshot);
}
```

- [ ] **Step 3: RemovalVoteFlow**

```csharp
// src/Game/DecisionVotes/RemovalVoteFlow.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SlayTheStreamer2.Game.Bootstrap;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Chat;
using SlayTheStreamer2.Ti.Internal;
using SlayTheStreamer2.Ti.Ui;
using SlayTheStreamer2.Ti.Voting;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Runs one removal vote against an IRemovalSurface (spec section 3). The
/// caller owns its own vote-in-progress flag and the click gating; this class owns the
/// session, the popup, the resume and the record. Suspend-and-resume shape: the await
/// runs off the main thread and every screen touch is dispatcher-posted.</summary>
internal static class RemovalVoteFlow {
    private static VoteSession? _session;
    private static IRemovalSurface? _surface;
    private static int _overridePending;

    internal static bool IsActive => _session is { State: VoteSessionState.Open };
    internal static IRemovalSurface? ActiveSurface => _surface;
    internal static VoteSession? ActiveSession => _session;

    /// <summary>Starts the vote. Returns false when chat is not readable, the coordinator
    /// is missing, or fewer than two options are removable; the caller then treats the
    /// reward as Free for this screen.</summary>
    internal static bool TryStart(IRemovalSurface surface, Action onFinished) {
        var coordinator = Voter.Default;
        if (coordinator is null) return false;
        if (coordinator.Chat.State is not (ChatConnectionState.ConnectedReadWrite or ChatConnectionState.ConnectedReadOnly)) {
            TiLog.Debug($"[SlayTheStreamer2][{surface.LogTag}] chat not readable ({coordinator.Chat.State}); removal vote skipped");
            return false;
        }
        var titles = surface.CardTitles();
        int removable = titles.Count + (surface.HasSkip ? 1 : 0);
        if (removable < 2) {
            TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] fewer than 2 removable options; removal vote skipped");
            return false;
        }
        var settings = ModSettings.Current;
        var duration = TimeSpan.FromSeconds(settings?.VoteDurationSeconds ?? 30);
        bool showTag = settings?.ShowVoteTag ?? false;
        var labels = CardRewardOptionLabels.Build(titles, surface.HasSkip);
        string streamer = ModSettings.GetStreamerDisplayName();

        VoteSession session;
        try {
            session = coordinator.Start("Remove an option", labels, duration, showTag,
                formatReceipt: (snap, kind) => RemoveVoteReceipts.Format(snap, kind, streamer));
        } catch (Exception ex) {
            TiLog.Error($"[SlayTheStreamer2][{surface.LogTag}] Voter.Default.Start threw; removal vote skipped", ex);
            return false;
        }
        _session = session;
        _surface = surface;
        Interlocked.Exchange(ref _overridePending, 0);
        var snapshot = surface.SnapshotOptions();
        TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] removal vote opened: {labels.Count} options (skip={surface.HasSkip})");
        _ = RunAsync(coordinator, surface, session, snapshot, onFinished);
        return true;
    }

    private static async Task RunAsync(VoteCoordinator coordinator, IRemovalSurface surface, VoteSession session, object? snapshot, Action onFinished) {
        try {
            coordinator.Dispatcher.Post(() => {
                VoteTallyLabel.AttachTo(session, RunLiveness.IsRunDying, ModSettings.Current?.VoteTallyOnLeft ?? false, OverlayOcclusion.IsOccludingOverlayVisible);
                try {
                    new CardRewardVotePopup(session, coordinator.Dispatcher, surface.ScreenNode,
                        surface.CardHolders(), surface.SkipControl(), surface.BannerAnchor(),
                        includeSkip: surface.HasSkip, removeMode: true,
                        RunLiveness.IsRunDying, OverlayOcclusion.IsOccludingOverlayVisible).Show();
                } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] popup attach failed: {ex.Message}"); }
            });

            int winner;
            try { winner = await session.AwaitWinnerAsync(); }
            catch (OperationCanceledException) {
                bool overridden = Interlocked.Exchange(ref _overridePending, 0) == 1;
                TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] removal vote cancelled ({(overridden ? "streamer override" : "run/chat")}); no removal recorded");
                coordinator.Dispatcher.Post(() => Finish(onFinished));
                return;
            }
            coordinator.Dispatcher.Post(() => {
                try { Apply(surface, snapshot, winner); }
                finally { Finish(onFinished); }
            });
        } catch (Exception ex) {
            TiLog.Error($"[SlayTheStreamer2][{surface.LogTag}] removal vote flow threw; screen released without a removal", ex);
            coordinator.Dispatcher.Post(() => Finish(onFinished));
        }
    }

    private static void Finish(Action onFinished) {
        _session = null;
        _surface = null;
        try { onFinished(); } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] onFinished threw", ex); }
    }

    private static void Apply(IRemovalSurface surface, object? snapshot, int winner) {
        if (!GodotObject.IsInstanceValid(surface.ScreenNode)) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] screen gone before the removal applied; nothing recorded");
            return;
        }
        if (RunLiveness.IsRunDying()) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] run dying before the removal applied; nothing recorded");
            return;
        }
        if (!surface.OptionsMatch(snapshot)) {
            TiLog.Warn($"[SlayTheStreamer2][{surface.LogTag}] options changed during the vote (reroll?); nothing recorded");
            return;
        }
        var titles = surface.CardTitles();
        int? cardIndex = CardRewardOptionLabels.ResolveCardIndex(winner, surface.HasSkip);
        RemovalRecord record = cardIndex is null
            ? new RemovalRecord(RemovalClickRules.SkipIndex, CardRewardOptionLabels.SkipLabel, snapshot)
            : new RemovalRecord(cardIndex.Value, cardIndex.Value < titles.Count ? titles[cardIndex.Value] : "an option", snapshot);
        RemovalRecords.Set(surface.RecordKey, record);
        ApplyRecordVisuals(surface, record);
        TiLog.Info($"[SlayTheStreamer2][{surface.LogTag}] chat removed {(record.IsSkip ? "Skip" : $"#{record.RemovedIndex} {record.RemovedLabel}")}");
    }

    /// <summary>Paint the removed option (idempotent: the popup already tweened it once)
    /// and make it unclickable when no override budget remains.</summary>
    internal static void ApplyRecordVisuals(IRemovalSurface surface, RemovalRecord record) {
        var control = record.IsSkip ? surface.SkipControl() : IndexOrNull(surface.CardHolders(), record.RemovedIndex);
        if (control is null) return;
        RemovalVisuals.PaintRemoved(control, surface.ScreenNode);
        bool canOverride = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
        RemovalVisuals.SetClickable(control, canOverride);
    }

    private static Control? IndexOrNull(IReadOnlyList<Control> list, int i) => i >= 0 && i < list.Count ? list[i] : null;

    /// <summary>The record for this surface if its options still match; a stale record
    /// (Driftwood reroll) is cleared so the next click starts a fresh vote.</summary>
    internal static RemovalRecord? EffectiveRecord(IRemovalSurface surface) {
        var record = RemovalRecords.Get(surface.RecordKey);
        if (record is null) return null;
        if (surface.OptionsMatch(record.OptionsSnapshot)) return record;
        RemovalRecords.Clear(surface.RecordKey);
        return null;
    }

    /// <summary>A streamer click during the countdown: end the vote with NO removal and
    /// spend one override; the caller lets the click reach vanilla. False when there is
    /// no open vote, no budget, or the click landed inside the arming delay.</summary>
    internal static bool TryOverrideDuringVote(string takenLabel) {
        try {
            var session = _session;
            if (session is null || session.State != VoteSessionState.Open) return false;
            if (!VoteOverrideBudget.Enabled || VoteOverrideBudget.Remaining <= 0) return false;
            if (session.Elapsed < VoteOverrideBudget.ArmingDelay) return false;
            Interlocked.Exchange(ref _overridePending, 1);
            session.Cancel();
            if (session.State != VoteSessionState.Cancelled) { Interlocked.Exchange(ref _overridePending, 0); return false; }
            VoteOverrideBudget.RecordUse();
            string? curse = CursedOverrides.TryRollCurseForLocalPlayer();
            VoteOverrideBudget.SendOverrideReceipt(takenLabel, curse);
            TiLog.Info($"[SlayTheStreamer2][remove-one] override during removal vote: streamer took {takenLabel}; {VoteOverrideBudget.Remaining} override(s) remaining this act");
            return true;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][remove-one] override during removal vote failed", ex); return false; }
    }

    /// <summary>Spec section 3.2 status lines (rig-tested wording from Sabotage strike/18.7).</summary>
    internal static string StatusText(IRemovalSurface surface, bool hasReroll) {
        string text;
        if (IsActive && ReferenceEquals(_surface, surface)) text = "";                         // the popup speaks
        else if (EffectiveRecord(surface) is { } r) {
            bool budget = VoteOverrideBudget.Enabled && VoteOverrideBudget.Remaining > 0;
            if (r.IsSkip) text = budget ? "Chat removed Skip. Take a card, or spend an override to skip." : "Chat removed Skip. You must take a card.";
            else text = budget ? $"Chat removed {r.RemovedLabel}. Choose from the rest, or spend an override to take it." : $"Chat removed {r.RemovedLabel}. Choose from the rest.";
            if (hasReroll) text += "\nYou can reroll these cards once, for free.";
        } else text = "Click any option to start the removal vote.";
        return text;
    }
}
```

Check `VoteSession.Elapsed` exists (`grep -n "Elapsed" src/Ti/Voting/VoteSession.cs`); `CardRewardVotePatch.TryOverrideWithCard` already uses it.

- [ ] **Step 4: Build**

Run: `pwsh -File build.ps1`
Expected: build OK, tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/RemovalRecords.cs src/Game/DecisionVotes/IRemovalSurface.cs src/Game/DecisionVotes/RemovalVoteFlow.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/6: RemovalRecords + IRemovalSurface + RemovalVoteFlow (session, popup, resume, override-during-vote)"
```

---

### Task 7: Removal vote on the card-reward screen

**Files:**
- Create: `src/Game/DecisionVotes/CardRewardRemovalSurface.cs`
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs` (Prefix lines 281-306, alt prefix lines 745-766, Generate postfix line 834)
- Modify: `tests/slay_the_streamer_2.tests.csproj` (one Compile Remove line)

**Interfaces:**
- Consumes: `RewardAuthority.ModeOfActiveReward`, `RemovalVoteFlow.*`, `RemovalClickRules.Judge`, `RemovalStatusLine`, `CardRewardOptionLabels`.
- Produces: `CardRewardRemovalSurface(NCardRewardSelectionScreen screen, CardReward reward) : IRemovalSurface`; static `CardRewardRemovalSurface.For(NCardRewardSelectionScreen) -> CardRewardRemovalSurface?` (null when the active reward is unknown); nested `ReadyPresenterPatch` (postfix on `NCardRewardSelectionScreen._Ready`) that attaches the status line and re-applies a record on re-open.

- [ ] **Step 1: Compile Remove line**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\CardRewardRemovalSurface.cs" />
```

- [ ] **Step 2: Write CardRewardRemovalSurface**

```csharp
// src/Game/DecisionVotes/CardRewardRemovalSurface.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>IRemovalSurface over NCardRewardSelectionScreen (spec section 3). The record
/// key is the CardReward, so ESC-and-reopen keeps the removal. The options snapshot is
/// the CardCreationResult array: a Driftwood reroll rebuilds it, which invalidates the
/// record and lets the next click start a fresh vote.</summary>
internal sealed class CardRewardRemovalSurface : IRemovalSurface {
    private static readonly Lazy<FieldInfo?> OptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_options"));
    private static readonly Lazy<FieldInfo?> CardRowField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_cardRow"));
    private static readonly Lazy<FieldInfo?> ExtraOptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions"));

    private readonly NCardRewardSelectionScreen _screen;
    private readonly CardReward _reward;

    internal CardRewardRemovalSurface(NCardRewardSelectionScreen screen, CardReward reward) { _screen = screen; _reward = reward; }

    internal static CardRewardRemovalSurface? For(NCardRewardSelectionScreen screen) {
        var reward = CombatOriginTags.TryGetActiveReward();
        return reward is null ? null : new CardRewardRemovalSurface(screen, reward);
    }

    public Node ScreenNode => _screen;
    public object RecordKey => _reward;
    public string LogTag => "card-remove";
    public bool HasSkip => SkipAltIndex() is not null;

    internal IReadOnlyList<CardCreationResult> Options() =>
        OptionsField.Value?.GetValue(_screen) as IReadOnlyList<CardCreationResult> ?? Array.Empty<CardCreationResult>();

    public IReadOnlyList<string> CardTitles() => Options().Select(o => o.Card.Title).ToList();

    public IReadOnlyList<Control> CardHolders() {
        if (CardRowField.Value?.GetValue(_screen) is not Node row) return Array.Empty<Control>();
        return row.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList();
    }

    internal IReadOnlyList<CardRewardAlternative> Alternatives() =>
        ExtraOptionsField.Value?.GetValue(_screen) as IReadOnlyList<CardRewardAlternative> ?? Array.Empty<CardRewardAlternative>();

    internal int? SkipAltIndex() {
        var alts = Alternatives();
        for (int i = 0; i < alts.Count; i++) if (alts[i]?.OptionId == "Skip") return i;
        return null;
    }

    internal bool HasReroll() => Alternatives().Any(a => a?.OptionId == "REROLL");

    public Control? SkipControl() {
        try {
            if (SkipAltIndex() is not int i) return null;
            var container = _screen.GetNodeOrNull<Control>("UI/RewardAlternatives");
            return container is not null && i < container.GetChildCount() ? container.GetChild(i) as Control : null;
        } catch { return null; }
    }

    public Control? BannerAnchor() => _screen.GetNodeOrNull<Control>("UI/Banner");

    public object? SnapshotOptions() => Options().ToArray();

    public bool OptionsMatch(object? snapshot) {
        if (snapshot is not CardCreationResult[] snap) return false;
        var current = Options();
        if (current.Count != snap.Length) return false;
        for (int i = 0; i < snap.Length; i++) if (!ReferenceEquals(current[i], snap[i])) return false;
        return true;
    }

    /// <summary>Holder index for a clicked holder, or null.</summary>
    internal int? IndexOf(NCardHolder holder) {
        var holders = CardHolders();
        for (int i = 0; i < holders.Count; i++) if (holders[i] == holder) return i;
        return null;
    }

    /// <summary>Attaches the status line and re-applies an existing record when the
    /// screen (re)opens under RemoveOne. Runs after CombatOriginTags' OnSelect capture
    /// (the screen is instantiated synchronously inside CardReward.OnSelect).</summary>
    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
    internal static class ReadyPresenterPatch {
        static void Postfix(NCardRewardSelectionScreen __instance) {
            try {
                if (RewardAuthority.ModeOfActiveReward() != AuthorityMode.RemoveOne) return;
                var surface = For(__instance);
                if (surface is null) return;
                RemovalStatusLine.Attach(__instance, surface.BannerAnchor(), () => RemovalVoteFlow.StatusText(surface, surface.HasReroll()));
                var record = RemovalVoteFlow.EffectiveRecord(surface);
                if (record is not null) {
                    // Holders tween into place over 0.5 s on show; paint them next frame so the
                    // holder list is populated and sorted.
                    __instance.CallDeferred(Node.MethodName.SetProcess, true);
                    Callable.From(() => RemovalVoteFlow.ApplyRecordVisuals(surface, record)).CallDeferred();
                }
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-remove] ready presenter failed", ex); }
        }
    }
}
```

- [ ] **Step 3: Wire the SelectCard prefix**

In `CardRewardVotePatch.Prefix`, replace the block at lines 287-306 (the `_voteInProgress` branch through the combat-only scope gate) with:

```csharp
        // Vote in progress: nothing may fall through to vanilla. Must run BEFORE the
        // bail-to-vanilla gates. A removal vote is handled first: a streamer click
        // during its countdown is an override that TAKES the clicked card (no removal).
        if (_voteInProgress == 1) {
            if (RemovalVoteFlow.IsActive) {
                var surfaceNow = CardRewardRemovalSurface.For(__instance);
                string label = surfaceNow?.IndexOf(cardHolder) is int ci && ci < surfaceNow.Options().Count
                    ? surfaceNow.Options()[ci].Card.Title : "a card";
                if (RemovalVoteFlow.TryOverrideDuringVote(label)) {
                    _activeSession = null;
                    Interlocked.Exchange(ref _voteInProgress, 0);
                    return true;   // vanilla takes the clicked card
                }
                TiLog.Debug("[SlayTheStreamer2][card-remove] click during removal vote; suppressed");
                return false;
            }
            if (TryOverrideWithCard(__instance, cardHolder)) return false;
            TiLog.Debug("[SlayTheStreamer2][card-vote] repeat click during open vote; suppressed");
            return false;
        }

        // Per-origin mode (spec section 2). RemoveOne: click-to-start the removal vote,
        // then RemovalClickRules after the removal. Unskippable and Free: vanilla picks.
        var mode = RewardAuthority.ModeOfActiveReward();
        if (mode == AuthorityMode.RemoveOne) return HandleRemoveOneCardClick(__instance, cardHolder);
        if (mode != AuthorityMode.NormalVote) {
            TiLog.Info($"[SlayTheStreamer2][card-vote] {mode} card reward - no pick vote (streamer picks)");
            return true;
        }
```

and add the helper to the class:

```csharp
    /// <summary>RemoveOne on the card-reward screen (spec section 3). Returns the prefix
    /// result: false = click consumed, true = vanilla proceeds.</summary>
    private static bool HandleRemoveOneCardClick(NCardRewardSelectionScreen screen, NCardHolder cardHolder) {
        try {
            var surface = CardRewardRemovalSurface.For(screen);
            if (surface is null) return true;
            if (TryGetPlayerCount() is int n && n > 1) return true;                  // multiplayer: vanilla
            var record = RemovalVoteFlow.EffectiveRecord(surface);
            if (record is null) {
                if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;
                if (!RemovalVoteFlow.TryStart(surface, onFinished: () => Interlocked.Exchange(ref _voteInProgress, 0))) {
                    Interlocked.Exchange(ref _voteInProgress, 0);
                    return true;                                                     // bail: streamer picks freely
                }
                return false;                                                        // the click only started the vote
            }
            int? clicked = surface.IndexOf(cardHolder);
            if (clicked is null) return true;
            return JudgeRemovalClick(clicked.Value, record, surface.Options()[clicked.Value].Card.Title, "card-remove");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][card-remove] click handling threw; vanilla proceeds", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }

    /// <summary>Shared verdict application for both screen families.</summary>
    internal static bool JudgeRemovalClick(int clicked, RemovalRecord record, string takenLabel, string logTag) {
        var verdict = RemovalClickRules.Judge(clicked, record.RemovedIndex, VoteOverrideBudget.Enabled ? VoteOverrideBudget.Remaining : 0);
        switch (verdict) {
            case RemovalClickVerdict.Allow: return true;
            case RemovalClickVerdict.AllowWithOverride:
                VoteOverrideBudget.RecordUse();
                string? curse = CursedOverrides.TryRollCurseForLocalPlayer();
                VoteOverrideBudget.SendRemovalOverrideReceipt(takenLabel, curse);
                TiLog.Info($"[SlayTheStreamer2][{logTag}] override: streamer took the removed option ({takenLabel}); {VoteOverrideBudget.Remaining} override(s) remaining this act");
                return true;
            default:
                TiLog.Debug($"[SlayTheStreamer2][{logTag}] click on the removed option denied (no override budget)");
                return false;
        }
    }
```

- [ ] **Step 4: Wire the alternate-select prefix**

Replace the body of `NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix.Prefix` (lines 745-766) with:

```csharp
        static bool Prefix(NCardRewardSelectionScreen __instance, int index) {
            if (_chatSkipResumeInProgress == 1) return true;

            if (_voteInProgress == 1) {
                if (RemovalVoteFlow.IsActive) {
                    // Skip during a removal vote: an override that skips (vanilla Skip semantics).
                    bool isSkip = FindSkipAlternativeIndex(__instance) == index;
                    if (isSkip && RemovalVoteFlow.TryOverrideDuringVote(CardRewardOptionLabels.SkipLabel)) {
                        _activeSession = null;
                        Interlocked.Exchange(ref _voteInProgress, 0);
                        return true;
                    }
                    TiLog.Info("[SlayTheStreamer2][card-remove] alternate blocked: removal vote in progress");
                    return false;
                }
                if (TryOverrideWithSkip(__instance, index)) return false;
                TiLog.Info("[SlayTheStreamer2][card-vote] OnAlternateRewardSelected blocked: vote in progress");
                return false;
            }

            var mode = RewardAuthority.ModeOfActiveReward();
            var skipIndex = FindSkipAlternativeIndex(__instance);
            bool clickedSkip = skipIndex.HasValue && index == skipIndex.Value;

            if (mode == AuthorityMode.RemoveOne) {
                if (!clickedSkip) return true;                                     // Reroll etc: vanilla
                var surface = CardRewardRemovalSurface.For(__instance);
                if (surface is null) return true;
                var record = RemovalVoteFlow.EffectiveRecord(surface);
                if (record is null) {
                    // Skip is a "click any option" starter too.
                    if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;
                    if (!RemovalVoteFlow.TryStart(surface, () => Interlocked.Exchange(ref _voteInProgress, 0))) {
                        Interlocked.Exchange(ref _voteInProgress, 0);
                        return true;
                    }
                    return false;
                }
                return JudgeRemovalClick(RemovalClickRules.SkipIndex, record, CardRewardOptionLabels.SkipLabel, "card-remove");
            }

            // (3) Streamer-Skip budget gate for NormalVote rewards only; other modes are
            // vanilla here (Unskippable is denied earlier by UnskippableRewards, Task 9).
            if (clickedSkip && mode == AuthorityMode.NormalVote) {
                if (!CardRewardSkipGatePatch.TryConsumeStreamerSkip(__instance)) return false;
            }
            return true;
        }
```

- [ ] **Step 5: Gate the Skip-alt flip**

In `CardRewardAlternative_Generate_Postfix.Postfix` line 834, `if (!CombatOriginTags.ShouldVoteOn(cardReward)) return;` already means "NormalVote only" after Task 2. Update the comment above it to say RemoveOne and Unskippable rewards keep vanilla Skip semantics. No code change.

- [ ] **Step 6: Build and smoke**

Run: `pwsh -File build.ps1` then `pwsh -File install.ps1`, launch the game on the Beta branch with the checkbox Off, console `relic KALEIDOSCOPE`. Expected in `godot.log`: `tagged relic-origin card reward (relic=KALEIDOSCOPE, rarity=Ancient)`, then on the first click `removal vote opened`, chat receipt `Vote: remove one option from ...`, after the countdown `chat removed #N ...`, the option red, and the next click picks. Confirm ESC then reopen shows the same red option and no new vote. This is a developer smoke, not the operator gate.

- [ ] **Step 7: Commit**

```bash
git add src/Game/DecisionVotes/CardRewardRemovalSurface.cs src/Game/DecisionVotes/CardRewardVotePatch.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/7: removal vote on the card-reward screen (click-to-start, record, click rules, overrides)"
```

---

### Task 8: Choose-a-card family (Hefty Tablet, Lead Paperweight)

**Files:**
- Create: `src/Game/DecisionVotes/ChooseACardRemovePatch.cs`
- Modify: `src/Game/DecisionVotes/TopBarMapButtonGuardPatch.cs` (add the new VoteInProgress to its check)
- Modify: `tests/slay_the_streamer_2.tests.csproj` (one Compile Remove line)

**Interfaces:**
- Consumes: `RemovalVoteFlow`, `RemovalClickRules`, `CardRewardVotePatch.JudgeRemovalClick`, `RemovalStatusLine`, `RelicOriginTags.CurrentObtaining`, `RewardAuthority.RulesActive`.
- Produces: `ChooseACardRemovePatch.VoteInProgress -> bool`; `ChooseACardRemovePatch.LastOpenedRelicId -> string?` (for Task 10's learner).

- [ ] **Step 1: Compile Remove line**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\ChooseACardRemovePatch.cs" />
```

- [ ] **Step 2: Write the patch**

```csharp
// src/Game/DecisionVotes/ChooseACardRemovePatch.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 4: the removal vote on NChooseACardSelectionScreen (Hefty
/// Tablet, Lead Paperweight, any mod relic using CardSelectCmd.FromChooseACardScreen
/// out of combat with 3+ options counting Skip). A prefix on FromChooseACardScreen opens
/// a context; the static ShowScreen postfix binds the screen when its _cards list
/// reference-equals the context's; prefixes on SelectHolder and OnSkipButtonReleased
/// run the same click state machine as the card-reward screen. Inside vanilla's 350 ms
/// open debounce the prefixes return true so vanilla drops the click itself.</summary>
[HarmonyPatch]
internal static class ChooseACardRemovePatch {
    private static readonly Lazy<FieldInfo?> CardsField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cards"));
    private static readonly Lazy<FieldInfo?> CardRowField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_cardRow"));
    private static readonly Lazy<FieldInfo?> SkipField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_skipButton"));
    private static readonly Lazy<FieldInfo?> OpenedField = new(() => AccessTools.Field(typeof(NChooseACardSelectionScreen), "_openedTicks"));
    private const ulong DebounceMsec = 350;

    private static int _voteInProgress;
    internal static bool VoteInProgress => _voteInProgress == 1;

    /// <summary>Id of the relic whose obtain opened the current context (learner input).</summary>
    internal static string? LastOpenedRelicId { get; private set; }

    private sealed class Context {
        public required IReadOnlyList<CardModel> Cards;
        public required bool CanSkip;
        public NChooseACardSelectionScreen? Screen;
    }
    private static Context? _current;

    /// <summary>IRemovalSurface over the bound screen. Option list never changes, so the
    /// snapshot is the context itself.</summary>
    private sealed class Surface : IRemovalSurface {
        private readonly Context _ctx;
        private readonly NChooseACardSelectionScreen _screen;
        public Surface(Context ctx, NChooseACardSelectionScreen screen) { _ctx = ctx; _screen = screen; }
        public Node ScreenNode => _screen;
        public object RecordKey => _ctx;
        public string LogTag => "choose-remove";
        public bool HasSkip => _ctx.CanSkip;
        public IReadOnlyList<string> CardTitles() => _ctx.Cards.Select(c => c.Title).ToList();
        public IReadOnlyList<Control> CardHolders() =>
            CardRowField.Value?.GetValue(_screen) is Node row
                ? row.GetChildren().OfType<NCardHolder>().OrderBy(h => h.Position.X).Cast<Control>().ToList()
                : new List<Control>();
        public Control? SkipControl() => SkipField.Value?.GetValue(_screen) as Control;
        public Control? BannerAnchor() => _screen.GetNodeOrNull<Control>("Banner") ?? _screen.GetNodeOrNull<Control>("UI/Banner");
        public object? SnapshotOptions() => _ctx;
        public bool OptionsMatch(object? snapshot) => ReferenceEquals(snapshot, _ctx);
        internal int? IndexOf(NCardHolder holder) {
            for (int i = 0; i < _ctx.Cards.Count; i++) if (ReferenceEquals(_ctx.Cards[i], holder.CardModel)) return i;
            return null;
        }
    }

    private static Surface? BoundSurface(NChooseACardSelectionScreen screen) =>
        _current is { Screen: { } s } ctx && ReferenceEquals(s, screen) ? new Surface(ctx, screen) : null;

    [HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
    [HarmonyPrefix]
    private static void OpenPrefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip) {
        try {
            if (!RewardAuthority.RulesActive) return;
            if (CombatManager.Instance?.IsInProgress ?? false) return;             // mid-combat pickers stay the streamer's
            if (RunManager.Instance?.DebugOnlyGetState()?.Players?.Count is int n && n > 1) return;
            if (cards.Count + (canSkip ? 1 : 0) < 3) return;                        // N >= 3 counting Skip
            _current = new Context { Cards = cards, CanSkip = canSkip };
            LastOpenedRelicId = RelicOriginTags.CurrentObtaining?.Id.Entry;
            TiLog.Info($"[SlayTheStreamer2][choose-remove] context open cards={cards.Count} canSkip={canSkip} relic={LastOpenedRelicId ?? "none"}");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] open prefix failed", ex); }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen.ShowScreen))]
    [HarmonyPostfix]
    private static void ShowScreenPostfix(NChooseACardSelectionScreen? __result) {
        try {
            if (__result is null || _current is null) return;
            if (!ReferenceEquals(CardsField.Value?.GetValue(__result), _current.Cards)) return;   // combat pickers never match
            _current.Screen = __result;
            var surface = new Surface(_current, __result);
            RemovalStatusLine.Attach(__result, surface.BannerAnchor(), () => RemovalVoteFlow.StatusText(surface, hasReroll: false));
            TiLog.Info("[SlayTheStreamer2][choose-remove] screen bound");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] screen bind failed", ex); }
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), nameof(NChooseACardSelectionScreen._ExitTree))]
    [HarmonyPostfix]
    private static void ExitTreePostfix(NChooseACardSelectionScreen __instance) {
        try {
            if (_current is { Screen: { } s } && ReferenceEquals(s, __instance)) {
                RemovalRecords.Clear(_current);
                _current = null;
                Interlocked.Exchange(ref _voteInProgress, 0);
            }
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][choose-remove] exit-tree cleanup failed", ex); }
    }

    private static bool InDebounce(NChooseACardSelectionScreen s) =>
        OpenedField.Value?.GetValue(s) is ulong t && Time.GetTicksMsec() - t <= DebounceMsec;

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "SelectHolder")]
    [HarmonyPrefix]
    private static bool SelectHolderPrefix(NChooseACardSelectionScreen __instance, NCardHolder cardHolder) {
        var surface = BoundSurface(__instance);
        if (surface is null) return true;
        if (InDebounce(__instance)) return true;                                    // vanilla drops it; never latch here
        int? clicked = surface.IndexOf(cardHolder);
        string label = clicked is int i ? _current!.Cards[i].Title : "a card";
        return Handle(surface, clicked ?? 0, label);
    }

    [HarmonyPatch(typeof(NChooseACardSelectionScreen), "OnSkipButtonReleased")]
    [HarmonyPrefix]
    private static bool SkipPrefix(NChooseACardSelectionScreen __instance) {
        var surface = BoundSurface(__instance);
        if (surface is null) return true;
        return Handle(surface, RemovalClickRules.SkipIndex, CardRewardOptionLabels.SkipLabel);
    }

    /// <summary>Same state machine as the card-reward screen: click-to-start, override
    /// during the vote, verdicts after the removal. Returns the prefix result.</summary>
    private static bool Handle(Surface surface, int clicked, string label) {
        try {
            if (_voteInProgress == 1) {
                if (RemovalVoteFlow.TryOverrideDuringVote(label)) { Interlocked.Exchange(ref _voteInProgress, 0); return true; }
                TiLog.Debug("[SlayTheStreamer2][choose-remove] click during removal vote; suppressed");
                return false;
            }
            var record = RemovalVoteFlow.EffectiveRecord(surface);
            if (record is null) {
                if (Interlocked.CompareExchange(ref _voteInProgress, 1, 0) != 0) return false;
                if (!RemovalVoteFlow.TryStart(surface, () => Interlocked.Exchange(ref _voteInProgress, 0))) {
                    Interlocked.Exchange(ref _voteInProgress, 0);
                    return true;
                }
                return false;
            }
            return CardRewardVotePatch.JudgeRemovalClick(clicked, record, label, "choose-remove");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][choose-remove] click handling threw; vanilla proceeds", ex);
            Interlocked.Exchange(ref _voteInProgress, 0);
            return true;
        }
    }
}
```

Verify the banner node name on the choose-a-card scene: `grep -n "Banner" decompiled/sts2-assets/scenes/screens/card_selection/choose_a_card_selection_screen.tscn` (Sabotage used `GetNode("Banner")`); adjust `BannerAnchor()` to the real path. `CardModel.Title` is a plain string (used by the card vote already).

- [ ] **Step 3: Map guard**

In `TopBarMapButtonGuardPatch.cs`, find the condition that reads `CardRewardVotePatch.VoteInProgress` / `AncientVotePatch.VoteInProgress` and add `|| ChooseACardRemovePatch.VoteInProgress` so Map and its hotkey stay blocked during a Hefty Tablet removal vote.

- [ ] **Step 4: Build and smoke**

Run: `pwsh -File build.ps1`, `pwsh -File install.ps1`, console `relic HEFTY_TABLET` and `relic LEAD_PAPERWEIGHT` (checkbox Off). Expected: `context open cards=3 canSkip=True`, `screen bound`, click-to-start, removal applied (Skip removable and itself removed on one run), pick proceeds, Injury still added on a Skip pick for Hefty Tablet.

- [ ] **Step 5: Commit**

```bash
git add src/Game/DecisionVotes/ChooseACardRemovePatch.cs src/Game/DecisionVotes/TopBarMapButtonGuardPatch.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/8: removal vote on the choose-a-card screen (Hefty Tablet, Lead Paperweight)"
```

---

### Task 9: Unskippable rewards

**Files:**
- Create: `src/Game/DecisionVotes/UnskippableRewards.cs`, `src/Game/Ui/RewardsHeaderSubLabel.cs`
- Modify: `src/Game/DecisionVotes/CardRewardVotePatch.cs` (alt prefix: deny before the budget gate), `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs` (Proceed prefix)
- Modify: `tests/slay_the_streamer_2.tests.csproj` (one Compile Remove line)

**Interfaces:**
- Produces: `UnskippableRewards.ShouldDenyAlternative(NCardRewardSelectionScreen screen, int altIndex) -> bool`; `UnskippableRewards.HasPendingUnskippable(NRewardsScreen) -> bool`; nested `SubScreenReadyPatch` (postfix `NCardRewardSelectionScreen._Ready`) and `RewardsScreenReadyPrefix` (prefix `NRewardsScreen._Ready`).

- [ ] **Step 1: Compile Remove line**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\UnskippableRewards.cs" />
```

- [ ] **Step 2: Header sub-label**

```csharp
// src/Game/Ui/RewardsHeaderSubLabel.cs
using System;
using System.Reflection;
using Godot;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Ui;

/// <summary>"Every card reward must be taken here." ABOVE the "Loot!" banner (the loot
/// column leaves no room below it), in the vote-title theme, positioned per frame from
/// the header label, never from GetGlobalRect in _Ready.</summary>
internal sealed partial class RewardsHeaderSubLabel : Control {
    private const string FontPath = "res://themes/kreon_bold_shared.tres";
    private const int FontSize = 29;
    private const float LineHeight = 44f;
    private static readonly Color TextColor = new(1f, 0.964706f, 0.886275f, 1f);

    private Control? _header;
    private Label? _label;

    internal static void Attach(Node screen, Control? header, string text) {
        var sub = new RewardsHeaderSubLabel {
            Name = "SlayTheStreamerUnskippableHeader",
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 1,
        };
        sub._header = header;
        sub._label = new Label {
            Text = text, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 0, AnchorTop = 0, AnchorRight = 0, AnchorBottom = 0,
        };
        var font = ResourceLoader.Load<Font>(FontPath);
        if (font is not null) sub._label.AddThemeFontOverride("font", font);
        sub._label.AddThemeColorOverride("font_color", TextColor);
        sub._label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        sub._label.AddThemeConstantOverride("shadow_offset_x", 3);
        sub._label.AddThemeConstantOverride("shadow_offset_y", 2);
        sub._label.AddThemeFontSizeOverride("font_size", FontSize);
        sub.AddChild(sub._label);
        screen.AddChild(sub);
    }

    public override void _Process(double delta) {
        try {
            if (_label is null) return;
            float cx, top;
            if (_header is not null && GodotObject.IsInstanceValid(_header)) {
                var pos = _header.GlobalPosition; var size = _header.Size * _header.Scale;
                cx = pos.X + size.X * 0.5f; top = pos.Y - LineHeight - 4f;
            } else { cx = GetViewportRect().Size.X * 0.5f; top = 110f; }
            _label.OffsetLeft = cx - 400f; _label.OffsetRight = cx + 400f;
            _label.OffsetTop = top; _label.OffsetBottom = top + LineHeight;
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] header sub-label placement failed", ex); }
    }
}
```

- [ ] **Step 3: UnskippableRewards**

```csharp
// src/Game/DecisionVotes/UnskippableRewards.cs
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Rewards;
using SlayTheStreamer2.Game.Ui;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 5: input restraint only, never a change to the alternatives
/// list. (1) Skip denied in the alternate-select prefix (covers the button and the
/// ESC/back hotkeys). (2) Skip button hidden and Disable()d, banner retitled, status
/// line attached. (3) The parent rewards screen gets vanilla's own
/// RewardsSet.WithSkippingDisallowed() from a _Ready PREFIX plus the header line; the
/// Proceed prefix blocks while an Unskippable card reward is still alive. Every branch
/// fails OPEN to vanilla-skippable.</summary>
internal static class UnskippableRewards {
    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, object> Screens = new();
    private static readonly object Marker = new();
    private static readonly Lazy<FieldInfo?> ExtraOptionsField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_extraOptions"));
    private static readonly Lazy<FieldInfo?> BannerField = new(() => AccessTools.Field(typeof(NCardRewardSelectionScreen), "_banner"));
    private static readonly Lazy<FieldInfo?> RewardsSetField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardsSet"));
    private static readonly Lazy<FieldInfo?> HeaderField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_headerLabel"));
    private static readonly Lazy<FieldInfo?> RewardButtonsField = new(() => AccessTools.Field(typeof(NRewardsScreen), "_rewardButtons"));

    internal static bool IsUnskippableScreen(NCardRewardSelectionScreen screen) => Screens.TryGetValue(screen, out _);

    internal static bool ShouldDenyAlternative(NCardRewardSelectionScreen screen, int altIndex) {
        try {
            if (!IsUnskippableScreen(screen)) return false;
            var alts = ExtraOptionsField.Value?.GetValue(screen) as System.Collections.Generic.IReadOnlyList<CardRewardAlternative>;
            return alts is not null && altIndex >= 0 && altIndex < alts.Count && alts[altIndex]?.OptionId == "Skip";
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] deny check failed; failing open", ex); return false; }
    }

    /// <summary>Any alive card-reward button on the rewards screen whose reward is Unskippable.</summary>
    internal static bool HasPendingUnskippable(NRewardsScreen screen) {
        try {
            if (RewardButtonsField.Value?.GetValue(screen) is not System.Collections.Generic.IEnumerable<Control> buttons) return false;
            return buttons.Any(b => GodotObject.IsInstanceValid(b) && b is NRewardButton rb && rb.Reward is CardReward cr
                                    && RewardAuthority.Classify(cr) == AuthorityMode.Unskippable);
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] pending check failed; failing open", ex); return false; }
    }

    [HarmonyPatch(typeof(NCardRewardSelectionScreen), "_Ready")]
    internal static class SubScreenReadyPatch {
        static void Postfix(NCardRewardSelectionScreen __instance) {
            try {
                if (RewardAuthority.ModeOfActiveReward() != AuthorityMode.Unskippable) return;
                Screens.AddOrUpdate(__instance, Marker);
                var alts = ExtraOptionsField.Value?.GetValue(__instance) as System.Collections.Generic.IReadOnlyList<CardRewardAlternative>;
                var container = __instance.GetNodeOrNull<Control>("UI/RewardAlternatives");
                if (alts is not null && container is not null) {
                    for (int i = 0; i < alts.Count && i < container.GetChildCount(); i++) {
                        if (alts[i]?.OptionId != "Skip") continue;
                        if (container.GetChild(i) is Control c) {
                            if (c is NClickableControl clickable) clickable.Disable();
                            c.Visible = false;
                        }
                    }
                }
                if (BannerField.Value?.GetValue(__instance) is NCommonBanner banner) banner.label.SetTextAutoSize("Choose a Card (no skip)");
                RemovalStatusLine.Attach(__instance, __instance.GetNodeOrNull<Control>("UI/Banner"), () => "This card reward cannot be skipped.");
                TiLog.Info("[SlayTheStreamer2][unskip] card reward screen restrained (Skip hidden and denied)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] screen restraint failed; vanilla skippable", ex); }
        }
    }

    /// <summary>PREFIX so vanilla's own DisallowSkipping branch in _Ready sees the flag.
    /// _rewardsSet is assigned in ShowScreen before the screen is pushed.</summary>
    [HarmonyPatch(typeof(NRewardsScreen), "_Ready")]
    internal static class RewardsScreenReadyPrefix {
        static void Prefix(NRewardsScreen __instance) {
            try {
                if (!RewardAuthority.RulesActive) return;
                if (RewardsSetField.Value?.GetValue(__instance) is not RewardsSet set) return;
                if (!set.Rewards.Any(r => r is CardReward cr && RewardAuthority.Classify(cr) == AuthorityMode.Unskippable)) return;
                set.WithSkippingDisallowed();
                RewardsHeaderSubLabel.Attach(__instance, HeaderField.Value?.GetValue(__instance) as Control, "Every card reward must be taken here.");
                TiLog.Info("[SlayTheStreamer2][unskip] rewards set restrained (Skip Rewards disabled)");
            } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][unskip] rewards-set restraint failed; vanilla skippable", ex); }
        }
    }
}
```

Check `NCommonBanner.label.SetTextAutoSize(string)` exists (`grep -n "SetTextAutoSize" decompiled/sts2-v0.111.0/MegaCrit.Sts2.addons.mega_text/MegaLabel.cs`) and the `NCommonBanner` namespace (`MegaCrit.Sts2.Core.Nodes.CommonUi` expected).

- [ ] **Step 4: Deny in the alternate prefix, block Proceed**

In `CardRewardVotePatch.NCardRewardSelectionScreen_OnAlternateRewardSelected_Prefix.Prefix` (as rewritten in Task 7), insert right after the `_voteInProgress` block and before `var mode = ...`:

```csharp
            if (UnskippableRewards.ShouldDenyAlternative(__instance, index)) {
                TiLog.Info("[SlayTheStreamer2][unskip] Skip denied on an unskippable card reward");
                return false;
            }
```

In `CardRewardSkipGatePatch.NRewardsScreen_OnProceedButtonPressed_Prefix.Prefix`, insert after the `VoteInProgress` block:

```csharp
                if (UnskippableRewards.HasPendingUnskippable(__instance)) {
                    TiLog.Info("[SlayTheStreamer2][unskip] Proceed blocked: an unskippable card reward is still pending");
                    return false;
                }
```

(This runs before `ShouldEnforceSkipGate` returns early? No: place it after the `if (!ShouldEnforceSkipGate()) return true;` line but note that gate returns early in chat-terminal states; unskippable must hold regardless, so put the unskippable check BEFORE the `ShouldEnforceSkipGate` line.)

- [ ] **Step 5: Build and smoke**

Run: `pwsh -File build.ps1`, `pwsh -File install.ps1`. Force an event card reward (dev console `event FUTURE_OF_POTIONS` if available, else find Colorful Philosophers) with the checkbox Off. Expected: banner "Choose a Card (no skip)", no Skip button, ESC does nothing, log `Skip denied on an unskippable card reward`, the parent's Skip Rewards button disabled, header line above "Loot!", Proceed blocked until the card is taken.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/UnskippableRewards.cs src/Game/Ui/RewardsHeaderSubLabel.cs src/Game/DecisionVotes/CardRewardVotePatch.cs src/Game/DecisionVotes/CardRewardSkipGatePatch.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/9: unskippable card rewards (Skip denied/hidden, banner, rewards-set flag, Proceed block)"
```

---

### Task 10: Text catalogue, relic registry, read-time loc patch

**Files:**
- Create: `src/Game/DecisionVotes/AuthorityLoc.cs`, `src/Game/DecisionVotes/RelicTextRegistry.cs`, `src/Game/DecisionVotes/LocTextPatch.cs`
- Modify: `src/Game/DecisionVotes/RelicOriginTags.cs` (learner call), `src/Game/DecisionVotes/ChooseACardRemovePatch.cs` (learner call)
- Test: `tests/Game/DecisionVotes/AuthorityLocTests.cs`, `tests/Game/DecisionVotes/RelicTextRegistryTests.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (one Compile Remove line for `LocTextPatch.cs`)

**Interfaces:**
- Produces: `AuthorityLoc.Lead` (const, exactly `"\n[color=#668CFF]Slay the Streamer:[/color] "`); `AuthorityLoc.SuffixFor(string table, string key, RelicTextRegistry registry) -> string?`; `AuthorityLoc.Append(string raw, string suffix) -> string` (idempotent); `AuthorityLoc.BuiltInRelicIds -> IReadOnlyCollection<string>`; `AuthorityLoc.EventKeys -> IReadOnlyCollection<string>`.
- `RelicTextRegistry(string learnedFilePath)`; `.IsKnown(string relicId)`; `.TryGetLearnedMode(string relicId, out AuthorityMode mode)`; `.Learn(string relicId, AuthorityMode mode)` (persists; no-op for built-in or already learned); static `RelicTextRegistry.Instance` (set by `LocTextPatch` lazily from `OS.GetUserDataDir()`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Game/DecisionVotes/AuthorityLocTests.cs
using System.IO;
using System.Text.Json;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class AuthorityLocTests {
    private static RelicTextRegistry Registry() => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

    [Fact]
    public void Lead_IsExact() =>
        Assert.Equal("\n[color=#668CFF]Slay the Streamer:[/color] ", AuthorityLoc.Lead);

    [Fact]
    public void RelicDescription_GetsSuffix() {
        var s = AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.description", Registry());
        Assert.Equal(AuthorityLoc.Lead + "for each card reward, chat votes to remove one option, Skip included, and you pick from the rest.", s);
    }

    [Fact]
    public void RelicEventDescription_GetsSameSuffix() =>
        Assert.Equal(AuthorityLoc.SuffixFor("relics", "GLASS_EYE.description", Registry()),
                     AuthorityLoc.SuffixFor("relics", "GLASS_EYE.eventDescription", Registry()));

    [Fact]
    public void RelicFlavor_GetsNothing() => Assert.Null(AuthorityLoc.SuffixFor("relics", "KALEIDOSCOPE.flavor", Registry()));

    [Fact]
    public void DreamCatcherRestText_GetsItsOwnLine() =>
        Assert.Equal(AuthorityLoc.Lead + "chat removes one of the options first.",
            AuthorityLoc.SuffixFor("relics", "DREAM_CATCHER.additionalRestSiteHealText", Registry()));

    [Fact]
    public void EventKeys_GetSuffix() {
        Assert.Equal(AuthorityLoc.Lead + "the card rewards cannot be skipped.",
            AuthorityLoc.SuffixFor("events", "BRAIN_LEECH.pages.INITIAL.options.RIP.description", Registry()));
        Assert.StartsWith("\n" + AuthorityLoc.Lead, AuthorityLoc.SuffixFor("events", "CRYSTAL_SPHERE.minigame.instructions.description", Registry()));
    }

    [Fact]
    public void UnknownKeyOrTable_GetsNothing() {
        Assert.Null(AuthorityLoc.SuffixFor("events", "TRASH_HEAP.title", Registry()));
        Assert.Null(AuthorityLoc.SuffixFor("cards", "STRIKE.description", Registry()));
    }

    [Fact]
    public void LearnedRelic_GetsGenericLineByMode() {
        var reg = Registry();
        reg.Learn("SOME_MOD_RELIC", AuthorityMode.RemoveOne);
        reg.Learn("SOME_SHOP_RELIC", AuthorityMode.Unskippable);
        Assert.Equal(AuthorityLoc.Lead + "chat votes to remove one option from this relic's card rewards.", AuthorityLoc.SuffixFor("relics", "SOME_MOD_RELIC.description", reg));
        Assert.Equal(AuthorityLoc.Lead + "card rewards from this relic cannot be skipped.", AuthorityLoc.SuffixFor("relics", "SOME_SHOP_RELIC.eventDescription", reg));
    }

    [Fact]
    public void Append_IsIdempotent() {
        string suffix = AuthorityLoc.Lead + "x.";
        string once = AuthorityLoc.Append("Base text.", suffix);
        Assert.Equal("Base text." + suffix, once);
        Assert.Equal(once, AuthorityLoc.Append(once, suffix));
    }

    [Fact]
    public void NoEmDashOrStrikeAnywhere() {
        var reg = Registry();
        reg.Learn("X", AuthorityMode.RemoveOne); reg.Learn("Y", AuthorityMode.Unskippable);
        foreach (var key in AuthorityLoc.AllKeysForTests()) {
            var s = AuthorityLoc.SuffixFor(key.Table, key.Key, reg)!;
            Assert.DoesNotContain("\u2014", s);
            Assert.DoesNotContain("strike", s.ToLowerInvariant());
            Assert.StartsWith(key.Key.EndsWith("minigame.instructions.description") ? "\n" + AuthorityLoc.Lead : AuthorityLoc.Lead, s);
        }
    }

    /// <summary>Every built-in vanilla key exists in the shipped English tables. Reads the
    /// extracted assets when present (decompiled/sts2-assets); skipped quietly otherwise.</summary>
    [Fact]
    public void BuiltInKeys_ExistInVanillaTables() {
        string root = FindRepoRoot();
        string relics = Path.Combine(root, "decompiled", "sts2-assets", "localization", "eng", "relics.json");
        string events = Path.Combine(root, "decompiled", "sts2-assets", "localization", "eng", "events.json");
        if (!File.Exists(relics) || !File.Exists(events)) return;
        using var r = JsonDocument.Parse(File.ReadAllText(relics));
        using var e = JsonDocument.Parse(File.ReadAllText(events));
        foreach (var id in AuthorityLoc.BuiltInRelicIds) {
            if (id == "STRONGBOX") continue;   // third-party (More Relics)
            Assert.True(r.RootElement.TryGetProperty(id + ".description", out _), $"missing relics key {id}.description");
        }
        Assert.True(r.RootElement.TryGetProperty("DREAM_CATCHER.additionalRestSiteHealText", out _));
        foreach (var key in AuthorityLoc.EventKeys)
            Assert.True(e.RootElement.TryGetProperty(key, out _), $"missing events key {key}");
    }

    private static string FindRepoRoot() {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
```

If the vanilla loc JSON is nested rather than flat (check the first lines of `decompiled/sts2-assets/localization/eng/relics.json`), adapt the lookup in the last test to walk the nesting; the intent is "the key the game reads exists".

```csharp
// tests/Game/DecisionVotes/RelicTextRegistryTests.cs
using System.IO;
using SlayTheStreamer2.Game.DecisionVotes;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.DecisionVotes;

public class RelicTextRegistryTests {
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "sts2-learned-" + Path.GetRandomFileName() + ".json");

    [Fact]
    public void BuiltIns_AreKnown_AndNeverLearned() {
        var path = TempPath();
        var reg = new RelicTextRegistry(path);
        Assert.True(reg.IsKnown("KALEIDOSCOPE"));
        reg.Learn("KALEIDOSCOPE", AuthorityMode.RemoveOne);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Learn_PersistsAndReloads() {
        var path = TempPath();
        new RelicTextRegistry(path).Learn("MOD_RELIC", AuthorityMode.Unskippable);
        var reloaded = new RelicTextRegistry(path);
        Assert.True(reloaded.TryGetLearnedMode("MOD_RELIC", out var mode));
        Assert.Equal(AuthorityMode.Unskippable, mode);
        Assert.True(reloaded.IsKnown("MOD_RELIC"));
    }

    [Fact]
    public void Learn_IsIdempotent() {
        var path = TempPath();
        var reg = new RelicTextRegistry(path);
        reg.Learn("MOD_RELIC", AuthorityMode.RemoveOne);
        var first = File.ReadAllText(path);
        reg.Learn("MOD_RELIC", AuthorityMode.RemoveOne);
        Assert.Equal(first, File.ReadAllText(path));
    }

    [Fact]
    public void CorruptFile_IsIgnored() {
        var path = TempPath();
        File.WriteAllText(path, "{ not json");
        var reg = new RelicTextRegistry(path);
        Assert.False(reg.TryGetLearnedMode("ANY", out _));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~AuthorityLocTests|FullyQualifiedName~RelicTextRegistryTests"`
Expected: build errors (types missing).

- [ ] **Step 3: RelicTextRegistry**

```csharp
// src/Game/DecisionVotes/RelicTextRegistry.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.3: which relic ids get explanation text. Built-in ids come
/// from AuthorityLoc; other mods' relics are learned the first time they produce a
/// governed card reward and persisted as a JSON array of {id, mode}. BCL only.</summary>
public sealed class RelicTextRegistry {
    private sealed record Entry(string Id, string Mode);

    private readonly string _path;
    private readonly Dictionary<string, AuthorityMode> _learned = new(StringComparer.Ordinal);

    public static RelicTextRegistry? Instance { get; set; }

    public RelicTextRegistry(string learnedFilePath) {
        _path = learnedFilePath;
        try {
            if (File.Exists(_path)) {
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? new();
                foreach (var e in entries)
                    if (Enum.TryParse<AuthorityMode>(e.Mode, out var m) && m is AuthorityMode.RemoveOne or AuthorityMode.Unskippable) _learned[e.Id] = m;
            }
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] learned-relics file unreadable; starting empty: {ex.Message}"); }
    }

    public bool IsKnown(string relicId) => AuthorityLoc.BuiltInRelicIds.Contains(relicId) || _learned.ContainsKey(relicId);

    public bool TryGetLearnedMode(string relicId, out AuthorityMode mode) => _learned.TryGetValue(relicId, out mode);

    public void Learn(string relicId, AuthorityMode mode) {
        if (mode is not (AuthorityMode.RemoveOne or AuthorityMode.Unskippable)) return;
        if (IsKnown(relicId)) return;
        _learned[relicId] = mode;
        try {
            var list = new List<Entry>();
            foreach (var kv in _learned) list.Add(new Entry(kv.Key, kv.Value.ToString()));
            File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            TiLog.Info($"[SlayTheStreamer2][card-scope] learned relic {relicId} as {mode}; text will show from now on");
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] could not persist learned relic {relicId}: {ex.Message}"); }
    }
}
```

- [ ] **Step 4: AuthorityLoc**

```csharp
// src/Game/DecisionVotes/AuthorityLoc.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.2: the explanation text appended to vanilla relic and event
/// strings, word for word from the rig-tested Sabotage catalogue with chat phrasing.
/// Pure: SuffixFor answers "what goes after this table key" and Append is idempotent.
/// No em dashes; the verb is "remove".</summary>
public static class AuthorityLoc {
    public const string AccentHex = "#668CFF";
    public const string Lead = "\n[color=" + AccentHex + "]Slay the Streamer:[/color] ";

    private static readonly Dictionary<string, string> Relics = new(StringComparer.Ordinal) {
        ["KALEIDOSCOPE"] = "for each card reward, chat votes to remove one option, Skip included, and you pick from the rest.",
        ["GLASS_EYE"] = "chat votes to remove one option on each of the five card rewards. You pick from the rest.",
        ["LOST_COFFER"] = "chat votes to remove one option from the card reward.",
        ["HEFTY_TABLET"] = "chat votes to remove one of the four options, Skip included. [red]Injury[/red] is added either way.",
        ["LEAD_PAPERWEIGHT"] = "chat votes to remove one of the three options, Skip included.",
        ["NEOWS_BONES"] = "card rewards from relics pulled here use the removal vote.",
        ["DREAM_CATCHER"] = "chat votes to remove one of the card-reward options each time you [gold]Rest[/gold].",
        ["DRIFTWOOD"] = "after chat removes an option from a card reward, you may reroll once for free. Chat then votes again on the new cards.",
        ["ORRERY"] = "you must take a card from each of the five rewards.",
        ["STRONGBOX"] = "you must take a card from each of the two rewards.",
    };

    private static readonly Dictionary<string, string> RelicExtraKeys = new(StringComparer.Ordinal) {
        ["DREAM_CATCHER.additionalRestSiteHealText"] = "chat removes one of the options first.",
    };

    private const string Unskippable = "the card rewards cannot be skipped.";
    private const string CrystalSphere = "you will not have the option to skip cards uncovered here.";

    private static readonly Dictionary<string, string> Events = new(StringComparer.Ordinal) {
        ["TRASH_HEAP.pages.INITIAL.options.DIVE_IN.description"] = "if Dream Catcher is found here, chat will vote to remove one of your card-reward options when you [gold]Rest[/gold].",
        ["CRYSTAL_SPHERE.pages.INITIAL.options.UNCOVER_FUTURE.description"] = CrystalSphere,
        ["CRYSTAL_SPHERE.pages.INITIAL.options.PAYMENT_PLAN.description"] = CrystalSphere,
        ["CRYSTAL_SPHERE.minigame.instructions.description"] = CrystalSphere,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.NECROBINDER.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.IRONCLAD.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.REGENT.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.SILENT.description"] = Unskippable,
        ["COLORFUL_PHILOSOPHERS.pages.INITIAL.options.DEFECT.description"] = Unskippable,
        ["THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.description"] = Unskippable,
        ["BRAIN_LEECH.pages.INITIAL.options.RIP.description"] = Unskippable,
        ["TRIAL.pages.NONDESCRIPT.options.GUILTY.description"] = Unskippable,
    };

    private const string GenericRemoveOne = "chat votes to remove one option from this relic's card rewards.";
    private const string GenericUnskippable = "card rewards from this relic cannot be skipped.";

    public static IReadOnlyCollection<string> BuiltInRelicIds => Relics.Keys;
    public static IReadOnlyCollection<string> EventKeys => Events.Keys;

    /// <summary>The suffix for (table, key), or null when the key carries no text.</summary>
    public static string? SuffixFor(string table, string key, RelicTextRegistry registry) {
        switch (table) {
            case "relics":
                if (RelicExtraKeys.TryGetValue(key, out var extra)) return Lead + extra;
                int dot = key.LastIndexOf('.');
                if (dot <= 0) return null;
                string id = key.Substring(0, dot), field = key.Substring(dot + 1);
                if (field is not ("description" or "eventDescription")) return null;
                if (Relics.TryGetValue(id, out var line)) return Lead + line;
                if (registry.TryGetLearnedMode(id, out var mode))
                    return Lead + (mode == AuthorityMode.RemoveOne ? GenericRemoveOne : GenericUnskippable);
                return null;
            case "events":
                if (!Events.TryGetValue(key, out var ev)) return null;
                // The minigame instructions separate every sentence with a blank line.
                return key.EndsWith("minigame.instructions.description", StringComparison.Ordinal) ? "\n" + Lead + ev : Lead + ev;
            default:
                return null;
        }
    }

    /// <summary>Idempotent append: the fallback-table recursion calls GetRawText twice.</summary>
    public static string Append(string raw, string suffix) =>
        raw.EndsWith(suffix, StringComparison.Ordinal) ? raw : raw + suffix;

    /// <summary>Every (table, key) the catalogue answers, for tests.</summary>
    public static IEnumerable<(string Table, string Key)> AllKeysForTests() {
        foreach (var id in Relics.Keys) { yield return ("relics", id + ".description"); yield return ("relics", id + ".eventDescription"); }
        foreach (var k in RelicExtraKeys.Keys) yield return ("relics", k);
        foreach (var k in Events.Keys) yield return ("events", k);
        yield return ("relics", "X.description");
        yield return ("relics", "Y.description");
    }
}
```

- [ ] **Step 5: LocTextPatch and the learner calls**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\LocTextPatch.cs" />
```

```csharp
// src/Game/DecisionVotes/LocTextPatch.cs
using System;
using System.IO;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.1: read-time text append. Every loc render goes
/// LocString.GetFormattedText -> LocManager.SmartFormat -> LocString.GetRawText ->
/// LocTable.GetRawText, so a postfix here covers vanilla files, modded loc files and
/// BaseLib's post-load injection alike, and survives language switches. Live check on
/// RewardAuthority.RulesActive, so flipping the checkbox hides the text at once.</summary>
[HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
internal static class LocTextPatch {
    private static int _warned;

    internal static RelicTextRegistry Registry =>
        RelicTextRegistry.Instance ??= new RelicTextRegistry(Path.Combine(OS.GetUserDataDir(), "slay_the_streamer_2.learned-relics.json"));

    static void Postfix(string key, string ___name, ref string __result) {
        try {
            if (!RewardAuthority.RulesActive) return;
            if (___name is not ("relics" or "events")) return;
            var suffix = AuthorityLoc.SuffixFor(___name, key, Registry);
            if (suffix is null) return;
            __result = AuthorityLoc.Append(__result, suffix);
        } catch (Exception ex) {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0) TiLog.Warn($"[SlayTheStreamer2][card-scope] loc text append failed: {ex.Message}");
        }
    }
}
```

In `RelicOriginTags.TagIfObtaining`, after `Tags.AddOrUpdate(instance, relic);` add:

```csharp
                if (RewardAuthority.RulesActive)
                    LocTextPatch.Registry.Learn(relic.Id.Entry, relic.Rarity == MegaCrit.Sts2.Core.Entities.Relics.RelicRarity.Ancient ? AuthorityMode.RemoveOne : AuthorityMode.Unskippable);
```

In `ChooseACardRemovePatch.OpenPrefix`, after `LastOpenedRelicId = ...;` add:

```csharp
            if (LastOpenedRelicId is { } rid) LocTextPatch.Registry.Learn(rid, AuthorityMode.RemoveOne);
```

- [ ] **Step 6: Run tests, build, smoke**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj` (all green, the two new classes included), then `pwsh -File build.ps1`, `pwsh -File install.ps1`. In game (checkbox Off): hover Kaleidoscope in the compendium and at an Ancient; open Trash Heap; Crystal Sphere's instruction panel. Expected: the blue "Slay the Streamer:" line on its own line. Flip the checkbox On, hover again: line gone.

- [ ] **Step 7: Commit**

```bash
git add src/Game/DecisionVotes/AuthorityLoc.cs src/Game/DecisionVotes/RelicTextRegistry.cs src/Game/DecisionVotes/LocTextPatch.cs src/Game/DecisionVotes/RelicOriginTags.cs src/Game/DecisionVotes/ChooseACardRemovePatch.cs tests/Game/DecisionVotes/AuthorityLocTests.cs tests/Game/DecisionVotes/RelicTextRegistryTests.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/10: AuthorityLoc catalogue + RelicTextRegistry learner + read-time LocTable.GetRawText append"
```

---

### Task 11: Event option button growth

**Files:**
- Create: `src/Game/DecisionVotes/EventOptionGrowPatch.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (one Compile Remove line)

- [ ] **Step 1: Compile Remove line**

```xml
    <Compile Remove="..\src\Game\DecisionVotes\EventOptionGrowPatch.cs" />
```

- [ ] **Step 2: Port the patch**

```csharp
// src/Game/DecisionVotes/EventOptionGrowPatch.cs
using System;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes.Events;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.DecisionVotes;

/// <summary>Spec section 6.4. An event option whose text carries the appended line is
/// squeezed by the button's fixed 100 px height (the %Text label auto-shrinks its font).
/// Grow the button instead: measure the full text at the vanilla 24 px font wrapped to
/// the label width, add the overflow to the button's minimum height, the label box and
/// the vertically centred nine-patches (Image, Outline, RedFlash, BlueFlash) and move
/// PlayerVoteContainer down. Regular buttons have %Text as a direct child; Ancient
/// buttons wrap it in an HBoxContainer. Null-tolerant: a scene rename degrades to the
/// vanilla squeeze.</summary>
[HarmonyPatch(typeof(NEventOptionButton), "_Ready")]
internal static class EventOptionGrowPatch {
    private const float VanillaHeight = 100f;
    private const float LabelInset = 13f;
    private const int VanillaFontSize = 24;
    private const float Margin = 6f;
    private static readonly Regex Tags = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);
    private static readonly string Marker = AuthorityLoc.Lead.TrimStart('\n');

    static void Postfix(NEventOptionButton __instance) {
        try {
            var label = __instance.GetNodeOrNull<MegaRichTextLabel>("%Text");
            if (label is null) return;
            string text = label.Text ?? "";
            if (!text.Contains(Marker, StringComparison.Ordinal)) return;

            float available = VanillaHeight - 2f * LabelInset;
            float width = label.Size.X > 0 ? label.Size.X : label.CustomMinimumSize.X;
            if (width <= 0) width = 722f;
            float needed = Measure(label, text, width);
            float extra = Mathf.Ceil(Mathf.Max(0f, needed + Margin - available));
            if (extra <= 0f) return;

            var min = __instance.CustomMinimumSize;
            __instance.CustomMinimumSize = new Vector2(min.X, VanillaHeight + extra);
            __instance.PivotOffset = new Vector2(__instance.PivotOffset.X, (VanillaHeight + extra) * 0.5f);

            if (label.GetParent() is HBoxContainer hbox) {
                hbox.OffsetBottom += extra;
                var lm = label.CustomMinimumSize;
                label.CustomMinimumSize = new Vector2(lm.X, lm.Y + extra);
            } else {
                label.OffsetBottom += extra;
            }

            foreach (var name in new[] { "Image", "Outline", "RedFlash", "BlueFlash" }) {
                if (__instance.GetNodeOrNull<Control>(name) is { } piece) {
                    piece.OffsetTop -= extra * 0.5f;
                    piece.OffsetBottom += extra * 0.5f;
                    piece.PivotOffset = new Vector2(piece.PivotOffset.X, piece.PivotOffset.Y + extra * 0.5f);
                }
            }
            if (__instance.GetNodeOrNull<Control>("PlayerVoteContainer") is { } votes) {
                votes.OffsetTop += extra;
                votes.OffsetBottom += extra;
            }
            TiLog.Info($"[SlayTheStreamer2][card-scope] event option grown by {extra}px for the explanation line (needed={needed:0} available={available:0})");
        } catch (Exception ex) { TiLog.Error("[SlayTheStreamer2][card-scope] event option grow failed (vanilla squeeze remains)", ex); }
    }

    private static float Measure(MegaRichTextLabel label, string text, float width) {
        string plain = Tags.Replace(text, "");
        try {
            var font = label.GetThemeFont("normal_font", "RichTextLabel");
            if (font is not null) return font.GetMultilineStringSize(plain, HorizontalAlignment.Left, width, VanillaFontSize).Y;
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][card-scope] event option measure fell back to an estimate: {ex.Message}"); }
        int charsPerLine = Mathf.Max(20, (int)(width / 12f));
        int lines = 0;
        foreach (var para in plain.Split('\n')) lines += Mathf.Max(1, (para.Length + charsPerLine - 1) / charsPerLine);
        return lines * (VanillaFontSize + 2f);
    }
}
```

Our `AncientVotePopup` highlights options by their button rect; confirm it reads the rect per frame (grep `GlobalPosition` in `src/Game/Ui/AncientVotePopup.cs`) so a grown Lost Coffer button is fully covered. If it caches sizes at show time, read them per frame.

- [ ] **Step 3: Build and smoke**

Run: `pwsh -File build.ps1`, `pwsh -File install.ps1`. Trash Heap's Dive In option and an Ancient offering Lost Coffer: the button is taller, the font is not shrunk, the pulse covers the whole box.

- [ ] **Step 4: Commit**

```bash
git add src/Game/DecisionVotes/EventOptionGrowPatch.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "remove-one/11: EventOptionGrowPatch (event buttons grow for the explanation line)"
```

---

### Task 12: Settings default, help text, docs, matrix, CLAUDE.md, release build

**Files:**
- Modify: `src/Game/Bootstrap/ModSettings.cs:22-26` (record default) and `:268-277` (parse default + comment), `src/Game/Bootstrap/SettingsBootstrap.cs:62`, `src/slay_the_streamer_2.json.example:17`, `src/Game/Ui/Settings/SettingsPanelBuilder.cs:183-185`, `README.md` (card-rewards row, setting bullet, compat list), `notes/06-followups-and-deferred.md`, `CLAUDE.md` (commit prefix list + watchlist), `tests/Bootstrap/ModSettingsTests.cs` / `SettingsBootstrapTests.cs` if they assert the old default
- Create: `notes/14-remove-one-matrix.md`

- [ ] **Step 1: Flip the default**

`ChatSettings` record: `bool CombatCardVotesOnly = false`. `ModSettings.cs` parse block: `bool combatCardVotesOnly = false;`, warning text `"... using default (false)"`, and replace the comment with: `// Default false again from v0.4.0 (Surfinite 2026-09-07): Off now means the per-origin rules (removal votes, unskippable rewards) with explanation text on the relics and events; On keeps the combat-only behaviour.` `SettingsBootstrap.cs`: `["combatCardVotesOnly"] = false`. `.json.example`: `"combatCardVotesOnly": false`.

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter "FullyQualifiedName~ModSettingsTests|FullyQualifiedName~SettingsBootstrapTests"`. Fix any test that pinned `true` as the default (update the expectation, not the code).

- [ ] **Step 2: Help text**

In `SettingsPanelBuilder.cs` replace the `AddHelpText` line after the checkbox with:

```csharp
        AddHelpText(root, "On: chat only votes on card rewards earned from combat; other card rewards are free picks.\nOff (default): chat votes to remove one option on Ancient-relic and Dream Catcher card rewards,\nand event or shop-relic card rewards cannot be skipped. Explanations appear on the relics and events themselves.");
```

- [ ] **Step 3: README**

In the "What chat votes on" table, replace the Card rewards row's description with:

> After each fight, chat picks which of the 3 cards is added to your deck (chat can also skip if you enable it). Card rewards that are **not** from combat follow different rules: on Ancient relics (Kaleidoscope, Glass Eye, Lost Coffer, Hefty Tablet, Lead Paperweight) and Dream Catcher, **chat votes which option to remove** and you pick from the rest; card rewards from events and shop relics (Orrery) **cannot be skipped**. Every affected relic and event says so in its own text. A setting restores the old "only combat rewards vote" behaviour.

Replace the setting bullet with:

> - **Card-reward votes only occur after combat** *(default off since v0.4.0)* — Off: non-combat card rewards use the rules above (removal votes on Ancient relics and Dream Catcher, unskippable event and shop-relic rewards), with a blue "Slay the Streamer:" line on the relics and events explaining what happens. On: chat only votes on combat card rewards and everything else is a free streamer pick. *(Beta branch only: the default branch lacks the hook this needs; there every card reward votes as a normal pick.)*

Add a sentence to the Vote overrides bullet: "After chat removes an option, clicking the removed option spends an override to take it anyway." In Mod compatibility add: "**Balls2**, **StS1 Boss Ancients**, **Haxxero's More Relics** — tested with the removal-vote rules; their combat card rewards vote normally, their custom Ancients get the Ancient vote, and More Relics' Strongbox is unskippable like Orrery."

- [ ] **Step 4: notes/06 and CLAUDE.md**

Append to `notes/06-followups-and-deferred.md` a section "Remove-one votes + unskippable rewards (remove-one/, 2026-09-07)" with: the spec path, the rulings summary (one line each), the third-party findings (Balls2, StS1 Boss Ancients, More Relics), the Sea Star clone edge, the Downfall `FromChooseACardScreen` patch as a compat item, and open follow-ups: settings knob for the learned-relic generic text, events to RemoveOne if Tristan wants a chat interaction on every screen, universal override confirm click.

In `CLAUDE.md` commit conventions add `- Remove-one votes + unskippable rewards + explanation text: \`remove-one/N:\``. In the landmines section add one entry: "**Mod loc text: append at READ time (`LocTable.GetRawText` postfix), not at load.** BaseLib writes custom-model strings straight into the table dictionary after load (`ModelLocPatch` on `ModelDb.Init`), so a `LoadTable` postfix never sees third-party relics; read-time also survives language switches. The fallback-table recursion calls `GetRawText` twice for English-fallback keys, so the append must be idempotent (`AuthorityLoc.Append`)." Add the spec section 9 watchlist items to the game-update compat notes.

- [ ] **Step 5: Operator matrix**

Create `notes/14-remove-one-matrix.md` with the 12 rows of spec section 10 as a table (`# | Row | Console recipe | Evidence anchor | Result`), the evidence anchors being the exact log strings shipped: `tagged relic-origin card reward`, `tagged N rest-site card reward(s)`, `removal vote opened`, `chat removed`, `override during removal vote`, `override: streamer took the removed option`, `context open cards=`, `screen bound`, `card reward screen restrained`, `rewards set restrained`, `Skip denied on an unskippable card reward`, `Proceed blocked: an unskippable card reward`, `learned relic`, `event option grown by`, `combat-origin tagging did not register`. Note that `relic ORRERY` via console is NOT the shop path (buy it, or use Lord's Parasol).

- [ ] **Step 6: Final build, test, deploy**

Run: `pwsh -File build.ps1` (all tests green) then `pwsh -File install.ps1`. Confirm `godot.log` shows the mod version stamp matching `git log -1 --format=%H` after the commit below (rebuild after committing if the stamp lags, per the CLAUDE.md pipeline note).

- [ ] **Step 7: Commit**

```bash
git add src/Game/Bootstrap/ModSettings.cs src/Game/Bootstrap/SettingsBootstrap.cs src/slay_the_streamer_2.json.example src/Game/Ui/Settings/SettingsPanelBuilder.cs README.md notes/06-followups-and-deferred.md notes/14-remove-one-matrix.md CLAUDE.md tests/Bootstrap
git commit -m "remove-one/12: default Off, help text, README, notes/06 + notes/14 matrix, CLAUDE.md prefix + read-time loc landmine"
```

Hand the matrix to the operator. The `remove-one-complete` tag is applied once the matrix is green.

---

## Self-review notes

- Spec coverage: section 2 (Tasks 1, 2), 2.1 (Task 12 notes), 3 (Tasks 3 to 7), 3.3 Driftwood (Task 7 via the options snapshot; Reroll block already exists), 4 (Task 8), 5 (Task 9), 6.1 to 6.3 (Task 10), 6.4 (Task 11), 8 (Task 12), 9 watchlist (Task 12), 10 (tests in Tasks 1, 3, 10; matrix in Task 12).
- Type consistency: `RemovalRecord(int RemovedIndex, string RemovedLabel, object? OptionsSnapshot)` is used identically in Tasks 6, 7, 8; `RemovalClickRules.SkipIndex` is the Skip marker everywhere; `CardRewardVotePatch.JudgeRemovalClick(int, RemovalRecord, string, string)` is called from Task 8 with the same shape; `RemovalVoteFlow.TryStart(IRemovalSurface, Action)` in Tasks 7 and 8; `LocTextPatch.Registry` in Task 10's learner calls.
- Known verification points left as explicit grep steps (namespaces for `PlayerChoiceSynchronizer`, `CardCreationSource`, `NCommonBanner`; the choose-a-card banner node path; `VoteSession.WinnerIndex`; `VoteSnapshot` parameter order). Each names the exact command.
