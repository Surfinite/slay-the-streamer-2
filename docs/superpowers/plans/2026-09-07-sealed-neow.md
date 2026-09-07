# sealed-neow/ Implementation Plan (Neow's Talisman rework + relic disables in Sealed Deck runs)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In a Sealed Deck run (where Always Whale brings Neow's blessings back), Neow's Talisman upgrades 2 random cards and makes them Doomed instead of doing nothing, and Leafy Poultice and Precarious Shears are never offered.

**Architecture:** A `SealedDeckRun.IsActive` gate reads the live run's modifier list. A prefix on `NeowsTalisman.AfterObtained` substitutes the rework, using a new `StreamerDoomed : EnchantmentModel` registered by the game's own mod-type scan. A postfix on the base `RelicModel.IsAllowed` disables the two relics. Loc for the enchantment and the sealed-only Talisman text rides the same `LocTable.GetRawText` seam as the remove-one slice (provide and replace maps).

**Tech Stack:** C# / .NET 9, Godot .NET (StS2 Beta v0.111.0), HarmonyLib, xUnit. Build `pwsh -File build.ps1`, deploy `pwsh -File install.ps1`, tests `dotnet test tests/slay_the_streamer_2.tests.csproj`.

**Spec:** `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md` section 7 (plus rulings 7 and 8 in section 0). Independent of the remove-one plan except for sharing the loc seam idea; the two patches are separate classes and can land in either order.

## Global Constraints

- Every behaviour here is gated on `SealedDeckRun.IsActive` (the run's `Modifiers` contains a `SealedDeck`). Outside sealed runs: vanilla Talisman, vanilla relic pools, vanilla Talisman text (main-menu compendium included).
- Constants: `TalismanCards = 2`, `TalismanDoom = 3`. Enchantment id `STREAMER_DOOMED` (class `StreamerDoomed`; never collides with Sabotage's `SABO_DOOMED`).
- Never `new` a model: `ModelDb.Enchantment<StreamerDoomed>().ToMutable()`.
- No em dashes in shipped text or these files. `Id.Entry` values are UPPER_SNAKE_CASE (`LEAFY_POULTICE`, `PRECARIOUS_SHEARS`, `NEOWS_TALISMAN`).
- New files live in `src/Game/Content/` (a new folder, NOT in the test globs). BCL-only files get a surgical `<Compile Include>` in the test csproj; Harmony/Godot files get nothing.
- Every Harmony body is try/catch and fails open to vanilla; log through `TiLog`.
- Commit prefix: `sealed-neow/N:`.

---

## File map

| File | Responsibility | Test project |
|---|---|---|
| `src/Game/Content/TalismanPickRules.cs` | pure pick-K-distinct + FNV-1a seed hash | surgical include |
| `src/Game/Content/SealedNeowLoc.cs` | pure text: Doomed keys (provide), Talisman keys (replace) | surgical include |
| `src/Game/Content/SealedDeckRun.cs` | `IsActive` gate | none |
| `src/Game/Content/StreamerDoomed.cs` | the enchantment | none |
| `src/Game/Content/DoomedIconPatch.cs` | `EnchantmentModel.IconPath` postfix (tombstone) | none |
| `src/Game/Content/SealedNeowLocPatch.cs` | `LocTable` provide/replace patches | none |
| `src/Game/Content/TalismanReworkPatch.cs` | `NeowsTalisman.AfterObtained` prefix | none |
| `src/Game/Content/RelicDisablePatch.cs` | `RelicModel.IsAllowed` postfix | none |
| `tests/Game/Content/TalismanPickRulesTests.cs`, `tests/Game/Content/SealedNeowLocTests.cs` | unit tests | yes |
| `README.md`, `notes/06`, `notes/14` (matrix rows), `CLAUDE.md` | docs | n/a |

---

### Task 1: Pure pick rules and seed hash

**Files:**
- Create: `src/Game/Content/TalismanPickRules.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add `<Compile Include="..\src\Game\Content\TalismanPickRules.cs" />` next to the other surgical includes)
- Test: `tests/Game/Content/TalismanPickRulesTests.cs`

**Interfaces:**
- Produces: `TalismanPickRules.PickIndices(int count, int take, Func<int, int> nextInt) -> IReadOnlyList<int>` (distinct, deterministic given the draw sequence, clamps bad draws); `TalismanPickRules.Fnv1a64(string text) -> ulong`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Game/Content/TalismanPickRulesTests.cs
using System.Collections.Generic;
using System.Linq;
using SlayTheStreamer2.Game.Content;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Content;

public class TalismanPickRulesTests {
    [Fact]
    public void Picks_AreDistinct_AndWithinRange() {
        int seed = 0;
        var picks = TalismanPickRules.PickIndices(10, 2, max => (seed += 7) % max);
        Assert.Equal(2, picks.Count);
        Assert.Equal(2, picks.Distinct().Count());
        Assert.All(picks, p => Assert.InRange(p, 0, 9));
    }

    [Fact]
    public void SameDraws_SamePicks() {
        IReadOnlyList<int> Run() { int s = 3; return TalismanPickRules.PickIndices(6, 3, max => (s = s * 31 + 7) % max); }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void TakeAtLeastCount_ReturnsEverything() {
        var picks = TalismanPickRules.PickIndices(3, 5, max => 0);
        Assert.Equal(new[] { 0, 1, 2 }, picks.OrderBy(x => x));
    }

    [Fact]
    public void TakeZero_OrEmptyDeck_ReturnsNone() {
        Assert.Empty(TalismanPickRules.PickIndices(5, 0, max => 0));
        Assert.Empty(TalismanPickRules.PickIndices(0, 2, max => 0));
    }

    [Fact]
    public void OutOfRangeDraw_IsClamped_NotThrown() {
        var picks = TalismanPickRules.PickIndices(4, 2, max => 99);
        Assert.Equal(2, picks.Distinct().Count());
    }

    [Fact]
    public void Fnv1a64_IsStable_AndSensitive() {
        Assert.Equal(TalismanPickRules.Fnv1a64("abc|talisman|0"), TalismanPickRules.Fnv1a64("abc|talisman|0"));
        Assert.NotEqual(TalismanPickRules.Fnv1a64("abc|talisman|0"), TalismanPickRules.Fnv1a64("abc|talisman|1"));
        Assert.Equal(14695981039346656037UL, TalismanPickRules.Fnv1a64(""));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~TalismanPickRulesTests`
Expected: build error (namespace missing).

- [ ] **Step 3: Implement**

```csharp
// src/Game/Content/TalismanPickRules.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.1 step 3: which deck cards the reworked Talisman upgrades.
/// Partial Fisher-Yates over the candidate indices, one draw per pick, so the result is
/// a pure function of (count, take, draw sequence). A draw outside [0, pool) is clamped
/// rather than thrown. Fnv1a64 turns the run seed string plus a salt into a stable ulong
/// for Rng(ulong), so a save-quit-Continue that re-runs the pickup picks the same cards.</summary>
public static class TalismanPickRules {
    public static IReadOnlyList<int> PickIndices(int count, int take, Func<int, int> nextInt) {
        if (count <= 0 || take <= 0) return Array.Empty<int>();
        var pool = Enumerable.Range(0, count).ToList();
        int n = Math.Min(take, count);
        var picks = new List<int>(n);
        for (int i = 0; i < n; i++) {
            int j = Math.Clamp(nextInt(pool.Count), 0, pool.Count - 1);
            picks.Add(pool[j]);
            pool.RemoveAt(j);
        }
        return picks;
    }

    public static ulong Fnv1a64(string text) {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) { hash ^= b; hash *= prime; }
        return hash;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~TalismanPickRulesTests`
Expected: 6 passing.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Content/TalismanPickRules.cs tests/Game/Content/TalismanPickRulesTests.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "sealed-neow/1: TalismanPickRules (distinct picks, FNV-1a seed) with tests"
```

---

### Task 2: Pure loc text for Doomed and the sealed Talisman

**Files:**
- Create: `src/Game/Content/SealedNeowLoc.cs`
- Modify: `tests/slay_the_streamer_2.tests.csproj` (add `<Compile Include="..\src\Game\Content\SealedNeowLoc.cs" />`)
- Test: `tests/Game/Content/SealedNeowLocTests.cs`

**Interfaces:**
- Produces: `SealedNeowLoc.TalismanCards == 2`, `SealedNeowLoc.TalismanDoom == 3`; `SealedNeowLoc.DoomedId == "STREAMER_DOOMED"`; `SealedNeowLoc.TryProvide(string table, string key, out string text) -> bool` (enchantment keys, always); `SealedNeowLoc.TryReplace(string table, string key, out string text) -> bool` (Talisman keys; the CALLER gates on sealed).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Game/Content/SealedNeowLocTests.cs
using SlayTheStreamer2.Game.Content;
using Xunit;

namespace SlayTheStreamer2.Tests.Game.Content;

public class SealedNeowLocTests {
    [Fact]
    public void Provides_DoomedKeys() {
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.title", out var title));
        Assert.Equal("Doomed", title);
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.description", out var desc));
        Assert.Equal("Apply [blue]{Amount}[/blue] [gold]Doom[/gold] to you when played.", desc);
        Assert.True(SealedNeowLoc.TryProvide("enchantments", "STREAMER_DOOMED.extraCardText", out var extra));
        Assert.Equal("Apply {Amount} [gold]Doom[/gold] to you.", extra);
    }

    [Fact]
    public void DoesNotProvide_OtherKeys() {
        Assert.False(SealedNeowLoc.TryProvide("enchantments", "INKY.title", out _));
        Assert.False(SealedNeowLoc.TryProvide("relics", "STREAMER_DOOMED.title", out _));
    }

    [Fact]
    public void Replaces_TalismanKeys_WithConstants() {
        Assert.True(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.description", out var d));
        Assert.Equal("Upon pickup, [gold]Upgrade[/gold] [blue]2[/blue] random cards. They become [red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played.", d);
        Assert.True(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.eventDescription", out var e));
        Assert.Equal("[gold]Upgrade[/gold] [blue]2[/blue] random cards. They become [red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played.", e);
        Assert.False(SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.flavor", out _));
        Assert.False(SealedNeowLoc.TryReplace("relics", "POMANDER.description", out _));
    }

    [Fact]
    public void NoEmDashes() {
        foreach (var (t, k) in new[] { ("enchantments", "STREAMER_DOOMED.title"), ("enchantments", "STREAMER_DOOMED.description"), ("enchantments", "STREAMER_DOOMED.extraCardText") }) {
            SealedNeowLoc.TryProvide(t, k, out var s); Assert.DoesNotContain("\u2014", s);
        }
        SealedNeowLoc.TryReplace("relics", "NEOWS_TALISMAN.description", out var d); Assert.DoesNotContain("\u2014", d);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~SealedNeowLocTests`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
// src/Game/Content/SealedNeowLoc.cs
using System;
using System.Collections.Generic;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2 text. Provide = keys vanilla does not have (the Doomed
/// enchantment; answered whenever asked). Replace = vanilla keys whose text changes only
/// during a sealed run (the caller gates). {Amount} is substituted by the engine from
/// the enchantment instance; the Talisman numbers are baked from the constants.</summary>
public static class SealedNeowLoc {
    public const int TalismanCards = 2;
    public const int TalismanDoom = 3;
    public const string DoomedId = "STREAMER_DOOMED";
    public const string TalismanId = "NEOWS_TALISMAN";

    private static readonly Dictionary<string, string> Provided = new(StringComparer.Ordinal) {
        [DoomedId + ".title"] = "Doomed",
        [DoomedId + ".description"] = "Apply [blue]{Amount}[/blue] [gold]Doom[/gold] to you when played.",
        [DoomedId + ".extraCardText"] = "Apply {Amount} [gold]Doom[/gold] to you.",
    };

    private static readonly Dictionary<string, string> Replaced = new(StringComparer.Ordinal) {
        [TalismanId + ".description"] = $"Upon pickup, [gold]Upgrade[/gold] [blue]{TalismanCards}[/blue] random cards. They become [red]Doomed[/red]: apply [red]{TalismanDoom}[/red] [gold]Doom[/gold] to you when played.",
        [TalismanId + ".eventDescription"] = $"[gold]Upgrade[/gold] [blue]{TalismanCards}[/blue] random cards. They become [red]Doomed[/red]: apply [red]{TalismanDoom}[/red] [gold]Doom[/gold] to you when played.",
    };

    public static bool TryProvide(string table, string key, out string text) {
        text = "";
        return table == "enchantments" && Provided.TryGetValue(key, out text!);
    }

    public static bool TryReplace(string table, string key, out string text) {
        text = "";
        return table == "relics" && Replaced.TryGetValue(key, out text!);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/slay_the_streamer_2.tests.csproj --filter FullyQualifiedName~SealedNeowLocTests`
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/Game/Content/SealedNeowLoc.cs tests/Game/Content/SealedNeowLocTests.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "sealed-neow/2: SealedNeowLoc (Doomed provide keys, sealed Talisman replace keys) with tests"
```

---

### Task 3: Sealed gate, Doomed enchantment, icon and loc patches

**Files:**
- Create: `src/Game/Content/SealedDeckRun.cs`, `src/Game/Content/StreamerDoomed.cs`, `src/Game/Content/DoomedIconPatch.cs`, `src/Game/Content/SealedNeowLocPatch.cs`

**Interfaces:**
- Produces: `SealedDeckRun.IsActive -> bool`; `StreamerDoomed : EnchantmentModel` (id `STREAMER_DOOMED`).

- [ ] **Step 1: Gate**

```csharp
// src/Game/Content/SealedDeckRun.cs
using System;
using System.Linq;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7 gate: the live run's modifier list contains a SealedDeck.
/// Re-evaluated at each use; never cached across runs. Infer from the modifier list,
/// never from the game mode (the notes/08 landmine).</summary>
internal static class SealedDeckRun {
    internal static bool IsActive {
        get {
            try {
                var mods = RunManager.Instance?.DebugOnlyGetState()?.Modifiers;
                return mods is not null && mods.Any(m => m is SealedDeck);
            } catch { return false; }
        }
    }
}
```

- [ ] **Step 2: The enchantment**

```csharp
// src/Game/Content/StreamerDoomed.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2. Registered by the game's own mod-type scan
/// (ModelDb.AllAbstractModelSubtypes unions ReflectionHelper.GetSubtypesInMods), id
/// STREAMER_DOOMED from the class name. Enchantments need no pool. OnPlay applies Doom
/// to the card's owner (the Inky idiom with the owner as target). Amount (the Doom per
/// play) is set by the granting relic through CardCmd.Enchant. Not stackable: one
/// enchantment per card, as every vanilla enchantment.</summary>
public sealed class StreamerDoomed : EnchantmentModel {
    public override bool HasExtraCardText => true;
    protected override IEnumerable<IHoverTip> ExtraHoverTips => new[] { HoverTipFactory.FromPower<DoomPower>() };

    public override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay? cardPlay) {
        var owner = Card.Owner.Creature;
        await PowerCmd.Apply<DoomPower>(choiceContext, owner, Amount, owner, Card);
    }
}
```

Verify the `CardPlay` and `PlayerChoiceContext` namespaces: `grep -rn "class CardPlay\b" decompiled/sts2-v0.111.0 --include=*.cs | head -1` and `grep -rn "class PlayerChoiceContext" decompiled/sts2-v0.111.0 --include=*.cs | head -1`; fix the `using` lines to match. `Card` and `Amount` are `EnchantmentModel` members (EnchantmentModel.cs:145 for `Amount`).

- [ ] **Step 3: Icon patch**

```csharp
// src/Game/Content/DoomedIconPatch.cs
using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Ruling 8: the card badge uses EnchantmentModel.IconPath (non-virtual getter,
/// private _iconPath cache, missing-glyph fallback). For StreamerDoomed return the Doom
/// power's own 256 px icon; the badge TextureRect scales mixed sizes already (vanilla
/// badges are 64 and 128 px). Falls back to vanilla's answer if the resource is absent.</summary>
[HarmonyPatch(typeof(EnchantmentModel), nameof(EnchantmentModel.IconPath), MethodType.Getter)]
internal static class DoomedIconPatch {
    private const string DoomIcon = "res://images/powers/doom_power.png";

    static void Postfix(EnchantmentModel __instance, ref string __result) {
        try {
            if (__instance is not StreamerDoomed) return;
            if (ResourceLoader.Exists(DoomIcon)) __result = DoomIcon;
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] doomed icon override failed: {ex.Message}"); }
    }
}
```

- [ ] **Step 4: Loc patches**

```csharp
// src/Game/Content/SealedNeowLocPatch.cs
using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.2 loc. Provide: the Doomed keys do not exist in any table, so
/// prefixes on GetRawText, HasEntry and GetLocString answer them (BaseLib's
/// MissingLocPatch does the same for its keys; prefixes compose). Replace: the two
/// Talisman keys read the rework text only while the run is sealed (postfix on
/// GetRawText), so the compendium and normal runs show vanilla text.</summary>
internal static class SealedNeowLocPatch {
    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
    internal static class RawTextPatch {
        static bool Prefix(string key, string ___name, ref string __result) {
            try {
                if (SealedNeowLoc.TryProvide(___name, key, out var text)) { __result = text; return false; }
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc provide failed: {ex.Message}"); }
            return true;
        }

        static void Postfix(string key, string ___name, ref string __result) {
            try {
                if (___name != "relics") return;
                if (SealedNeowLoc.TryReplace(___name, key, out var text) && SealedDeckRun.IsActive) __result = text;
            } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] loc replace failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.HasEntry))]
    internal static class HasEntryPatch {
        static bool Prefix(string key, string ___name, ref bool __result) {
            if (SealedNeowLoc.TryProvide(___name, key, out _)) { __result = true; return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(LocTable), nameof(LocTable.GetLocString))]
    internal static class GetLocStringPatch {
        static bool Prefix(string key, string ___name, ref LocString __result) {
            if (SealedNeowLoc.TryProvide(___name, key, out _)) { __result = new LocString(___name, key); return false; }
            return true;
        }
    }
}
```

Ordering note: the remove-one slice's `LocTextPatch` is a postfix on the same `GetRawText`; it only touches `relics`/`events` keys in its own catalogue, which do not overlap `NEOWS_TALISMAN.*` or the Doomed keys, so the two compose in any order.

- [ ] **Step 5: Build**

Run: `pwsh -File build.ps1`
Expected: build OK, tests green. Launch once (`install.ps1`) and confirm `godot.log` has no `ModelDb` errors at boot (the enchantment registers) and the Harmony patch list includes `EnchantmentModel.get_IconPath` and the three `LocTable` targets.

- [ ] **Step 6: Commit**

```bash
git add src/Game/Content/SealedDeckRun.cs src/Game/Content/StreamerDoomed.cs src/Game/Content/DoomedIconPatch.cs src/Game/Content/SealedNeowLocPatch.cs
git commit -m "sealed-neow/3: SealedDeckRun gate, StreamerDoomed enchantment, tombstone icon, provide/replace loc patches"
```

---

### Task 4: Talisman rework prefix

**Files:**
- Create: `src/Game/Content/TalismanReworkPatch.cs`

**Interfaces:**
- Consumes: `SealedDeckRun.IsActive`, `TalismanPickRules`, `SealedNeowLoc.TalismanCards/TalismanDoom`, `StreamerDoomed`.

- [ ] **Step 1: Implement**

```csharp
// src/Game/Content/TalismanReworkPatch.cs
using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Random;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.1. Vanilla NeowsTalisman.AfterObtained upgrades the last
/// Basic Strike and Defend (a no-op in a sealed deck). In a sealed run this prefix
/// substitutes: upgrade TalismanCards random upgradable cards and enchant each with
/// StreamerDoomed at TalismanDoom. The picks use a run-seeded Rng so a
/// save-quit-Continue that re-runs the pickup picks the same cards. The upgrade preview
/// is suppressed (CardPreviewStyle.None) and the enchant VFX kept, which shows the card
/// in its final upgraded and enchanted state. Everything else (Pomander flip, Bones
/// eligibility, icon, save shape) stays vanilla. Fails open to vanilla.</summary>
[HarmonyPatch(typeof(NeowsTalisman), nameof(NeowsTalisman.AfterObtained))]
internal static class TalismanReworkPatch {
    private const string Salt = "slay-the-streamer|talisman";

    static bool Prefix(NeowsTalisman __instance, ref Task __result) {
        try {
            if (!SealedDeckRun.IsActive) return true;
            __result = Rework(__instance);
            return false;
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][sealed-neow] talisman rework prefix failed; vanilla Talisman", ex);
            return true;
        }
    }

    private static Task Rework(NeowsTalisman relic) {
        try {
            var owner = relic.Owner;
            var canonical = ModelDb.Enchantment<StreamerDoomed>();
            var candidates = PileType.Deck.GetPile(owner).Cards
                .Where(c => c.IsUpgradable && canonical.CanEnchant(c))
                .ToList();
            var runState = owner.RunState;
            ulong seed = TalismanPickRules.Fnv1a64($"{runState.Rng?.StringSeed}|{Salt}|{runState.CurrentActIndex}");
            var rng = new Rng(seed);
            var picks = TalismanPickRules.PickIndices(candidates.Count, SealedNeowLoc.TalismanCards, max => rng.NextInt(max));
            foreach (int i in picks) {
                var card = candidates[i];
                CardCmd.Upgrade(card, CardPreviewStyle.None);
                CardCmd.Enchant(canonical.ToMutable(), card, SealedNeowLoc.TalismanDoom);
                var vfx = NCardEnchantVfx.Create(card);
                if (vfx is not null) NRun.Instance?.GlobalUi.CardPreviewContainer.AddChildSafely(vfx);
            }
            TiLog.Info($"[SlayTheStreamer2][sealed-neow] talisman: upgraded+doomed {picks.Count} of {candidates.Count} candidates (doom={SealedNeowLoc.TalismanDoom})");
        } catch (Exception ex) {
            TiLog.Error("[SlayTheStreamer2][sealed-neow] talisman rework failed mid-way", ex);
        }
        return Task.CompletedTask;
    }
}
```

Verify before building: `Rng(ulong)` is in `MegaCrit.Sts2.Core.Random` (Rng.cs:19); `AddChildSafely` is in `MegaCrit.Sts2.Core.Helpers` (GodotTreeExtensions.cs:10); `NRun.Instance.GlobalUi.CardPreviewContainer` exists (`grep -n "CardPreviewContainer" decompiled/sts2-v0.111.0/MegaCrit.Sts2.Core.Nodes*/NRunGlobalUi*.cs`); `CardModel.IsUpgradable` (CardModel.cs:634). If `CardPreviewContainer` is a property on a different type, follow the Field of Man-Sized Holes idiom (`FieldOfManSizedHoles.cs:59-63`) exactly.

- [ ] **Step 2: Build and smoke**

Run: `pwsh -File build.ps1`, `pwsh -File install.ps1`. Start a Custom run with Sealed Deck and Pikcube's Always Whale, take Neow's Talisman (or console `relic NEOWS_TALISMAN` after the sealed pick). Expected: two enchant VFX, two upgraded cards with a tombstone badge, hover shows the Doom tooltip, playing one applies 3 Doom; log line `talisman: upgraded+doomed 2 of N`. In a normal (unsealed) run, `relic NEOWS_TALISMAN` behaves as vanilla and its description reads vanilla.

- [ ] **Step 3: Commit**

```bash
git add src/Game/Content/TalismanReworkPatch.cs
git commit -m "sealed-neow/4: Neow's Talisman rework in sealed runs (upgrade 2 random cards, Doomed 3)"
```

---

### Task 5: Relic disables

**Files:**
- Create: `src/Game/Content/RelicDisablePatch.cs`

- [ ] **Step 1: Implement**

```csharp
// src/Game/Content/RelicDisablePatch.cs
using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using SlayTheStreamer2.Ti.Internal;

namespace SlayTheStreamer2.Game.Content;

/// <summary>Spec section 7.3. Postfix on the base RelicModel.IsAllowed(IRunState): in a
/// sealed run, Leafy Poultice and Precarious Shears are not allowed. One predicate
/// covers the Neow page (IsAllowedAtNeow defers to IsAllowed), Neow's Bones, every grab
/// bag pull (chests, elites, Bossy Relics' expansion) and shops. Neither relic overrides
/// the predicate. Console `relic LEAFY_POULTICE` bypasses IsAllowed (informational).</summary>
[HarmonyPatch(typeof(RelicModel), nameof(RelicModel.IsAllowed))]
internal static class RelicDisablePatch {
    private static readonly HashSet<string> Disabled = new(StringComparer.Ordinal) { "LEAFY_POULTICE", "PRECARIOUS_SHEARS" };
    private static int _logged;

    static void Postfix(RelicModel __instance, ref bool __result) {
        try {
            if (!__result) return;
            if (!Disabled.Contains(__instance.Id.Entry)) return;
            if (!SealedDeckRun.IsActive) return;
            __result = false;
            if (System.Threading.Interlocked.Exchange(ref _logged, 1) == 0)
                TiLog.Info("[SlayTheStreamer2][sealed-neow] sealed run: Leafy Poultice and Precarious Shears disabled");
        } catch (Exception ex) { TiLog.Warn($"[SlayTheStreamer2][sealed-neow] relic disable postfix failed: {ex.Message}"); }
    }
}
```

- [ ] **Step 2: Build and smoke**

Run: `pwsh -File build.ps1`, `pwsh -File install.ps1`. Several sealed + Always Whale starts: neither relic appears among Neow's options; `relic NEOWS_BONES` never pulls them. An unsealed run can still offer them.

- [ ] **Step 3: Commit**

```bash
git add src/Game/Content/RelicDisablePatch.cs
git commit -m "sealed-neow/5: Leafy Poultice + Precarious Shears disabled in sealed runs (base IsAllowed postfix)"
```

---

### Task 6: Docs, matrix rows, CLAUDE.md

**Files:**
- Modify: `README.md` (Sealed Deck bullet), `notes/06-followups-and-deferred.md`, `notes/14-remove-one-matrix.md` (append rows; create the file with just these rows if the remove-one plan has not run yet), `CLAUDE.md`

- [ ] **Step 1: README**

Extend the Sealed Deck bullet in "The mod also plays nicely with two vanilla Custom Mode modifiers":

> If Neow's blessings are available in your sealed run (Pikcube's Run Modifiers' **Always Whale** does that), two tweaks apply: **Neow's Talisman** upgrades 2 random cards and makes them **Doomed** (3 Doom to you each time you play one) instead of upgrading Strikes and Defends you do not have, and **Leafy Poultice** and **Precarious Shears** are never offered. Outside sealed runs everything is vanilla.

- [ ] **Step 2: notes/06 and CLAUDE.md**

Append to `notes/06` a short "Sealed-deck Neow tweaks (sealed-neow/, 2026-09-07)" section: spec section 7, the gate, the constants, "knobs in settings" and "more sealed-dead relics" as follow-ups, and the Doomed-icon decision. In `CLAUDE.md` add the commit prefix `- Sealed-deck Neow tweaks (Talisman rework, relic disables): \`sealed-neow/N:\`` and one landmine: "**A postfix on a virtual base predicate (`RelicModel.IsAllowed`) never reaches subclasses that override it without calling base.** Leafy Poultice and Precarious Shears do not override it; check any relic added to the disable list (`grep -n IsAllowed decompiled/.../Relics/<Relic>.cs`)." Add to the compat watchlist: `NeowsTalisman.AfterObtained` staying a public non-async `Task` method; `EnchantmentModel.IconPath` staying a non-virtual getter with the `_iconPath` cache; `powers/doom_power.png` existing; base `IsAllowedAtNeow` deferring to `IsAllowed`.

- [ ] **Step 3: Matrix rows**

Append to `notes/14-remove-one-matrix.md` (row numbers continue from the remove-one rows, or start at 1 in a "sealed-neow" section if the file is new):

| # | Row | Recipe | Evidence | Result |
|---|---|---|---|---|
| S1 | Sealed + Always Whale: Talisman upgrades 2 cards, tombstone badge, Doom tooltip on hover | take Talisman at Neow, or `relic NEOWS_TALISMAN` after the sealed pick | `talisman: upgraded+doomed 2 of N` | |
| S2 | Playing a Doomed card applies 3 Doom; twice = 6 | combat | Doom power stack on the player | |
| S3 | Save, quit, Continue right after the Talisman: the same two cards stay upgraded and Doomed | | badge persists | |
| S4 | Talisman text: sealed run shows the rework text on the Neow button and relic hover; main menu compendium and an unsealed run show vanilla text | | visual | |
| S5 | Leafy Poultice and Precarious Shears never offered at Neow or by Neow's Bones across 5+ sealed starts | `relic NEOWS_BONES` | `sealed run: Leafy Poultice and Precarious Shears disabled` | |
| S6 | Unsealed run: vanilla Talisman, both relics can appear | | no `[sealed-neow]` lines | |

- [ ] **Step 4: Final build, test, deploy, commit**

Run: `pwsh -File build.ps1` (green) then `pwsh -File install.ps1`.

```bash
git add README.md notes/06-followups-and-deferred.md notes/14-remove-one-matrix.md CLAUDE.md
git commit -m "sealed-neow/6: README sealed-deck note, notes/06 entry, matrix rows, CLAUDE.md prefix + base-predicate landmine"
```

Tag `sealed-neow-complete` once the matrix rows are green.

---

## Self-review notes

- Spec coverage: 7 gate (Task 3), 7.1 rework (Task 4), 7.2 enchantment, icon and loc (Tasks 2, 3), 7.3 disables (Task 5), ruling 8 icon (Task 3), docs and watchlist (Task 6).
- Type consistency: `SealedNeowLoc.TalismanCards/TalismanDoom` used by Task 4; `TalismanPickRules.PickIndices(int,int,Func<int,int>)` and `Fnv1a64(string)` used by Task 4; `SealedDeckRun.IsActive` used by Tasks 3, 4, 5.
- Verification steps that name exact greps: `CardPlay`/`PlayerChoiceContext` namespaces, `CardPreviewContainer` location.
