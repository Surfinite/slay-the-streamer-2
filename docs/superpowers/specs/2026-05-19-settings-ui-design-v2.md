# In-game settings UI — design spec (v2 — meta-review applied)

**Date**: 2026-05-19
**Status**: Design (post-meta-review). Ready for review → implementation plan.
**Scope**: First in-game settings UI for the Slay the Streamer 2 mod. Surfaces a curated subset of streamer-tunable knobs inside the existing vanilla mod-manager screen.
**Changes from v1**: Auto-applied Must-do + Should-do items from `META-REVIEW-2026-05-19-settings-ui-design.md`. Inline `<!-- CHANGED -->` markers identify each modification.

## TL;DR

- **Approach**: Approach B-modified from [notes/09](../../../notes/09-settings-and-tunable-knobs.md). Extend the existing `NModInfoContainer` right-hand panel inside `NModdingScreen` with our own settings rows when the selected mod row is ours. Zero new tab, zero new popup, zero scene-instantiation surgery.
- **Scope**: Five tunable knobs (vote duration, Act-1 variant vote toggle, chat skip, card skips per act, vote tag display) + two one-shot action buttons (unlock everything, back up modded save). Seven UI rows total.
- **Identity/credential settings stay JSON-only.** OAuth on stream is a real exposure risk; channel/username/YT-channel-ID are set-once.
- **Apply-on-change with multi-trigger save.** <!-- CHANGED: was "save-on-close", reviewers R2/R4/R5 flagged hook reliability. New model: ModSettings.Current updates immediately on each control change; disk write is debounced 500ms with close-hook as belt-and-braces. --> The five persisted knobs apply on the next vote / next reward screen / next budget check after the user touches the control. **`NModdingScreen` is disabled mid-run** <!-- CHANGED: VERIFIED at NSettingsScreen.cs:157 — _moddingScreenButton.Disable() runs when RunManager.Instance.IsInProgress. Reviewer R4 flagged this. The practical effect is "next-run first vote", not literal hot-reload during a fight. --> via `_moddingScreenButton.Disable()`, so in practice changes take effect on the next run rather than during an active one. Action buttons take effect immediately.
- **Persistence**: debounced atomic write to the existing `slay_the_streamer_2.json`, read-merge-write to preserve JSON-only fields. Additive-optional schema — no `schemaVersion` bump.
- **Five persisted settings keys + two stateless action buttons.** <!-- CHANGED: was "seven UI-managed keys", reviewers R2/R3 flagged the inaccuracy. -->

## Motivation

Today, every tunable lives either in code-as-literal or in the JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Streamers can tweak the four currently-exposed JSON fields (`channel` / `username` / `oauthToken` / `cardSkipsPerAct`) plus the YT field and `voteOnActVariant` between sessions, but only via a text editor outside the game. Some knobs that streamers reasonably want to flip between runs in the same session — vote duration, whether chat can skip, whether the on-screen vote tag shows — have no exposure at all.

This spec captures the v1 cut: the smallest set of knobs that delivers real streamer-UX value without exploding the test matrix. Future settings (per-vote feature toggles for Neow/Card/Ancients/Boss, receipt-cadence tuning, voter eligibility filters, locale, etc.) are deferred to later increments per [notes/09 Part B](../../../notes/09-settings-and-tunable-knobs.md#part-b-tunable-knobs-inventory).

**"Bo" reference**: <!-- CHANGED: reviewers R3 and R5 flagged this as opaque. --> Bo is a collaborator/playtester who has provided design input on chat-vote conventions from playing Tempus's original StS1 mod. When the spec cites "Bo confirmed X," it means X was validated against StS1 chat-vote behaviour.

## Settings inventory

Seven rows total, grouped by section.

### Group 1 — Vote behaviour

| UI label | Control | Default | JSON key | Applies |
|---|---|---|---|---|
| **Vote duration** | Slider 10–120s, step 5s, value badge `"30s"` | `30s` | `voteDurationSeconds: int` | ✅ next vote (next run) |
| **Vote on Act 1 variant** | Checkbox | `true` | `voteOnActVariant: bool` *(existing)* | ✅ next vote (next run) |
| **Allow chat to skip** | Checkbox | `true` | `cardSkipAsVoteOption: bool` | ✅ next reward screen |
| **Show vote tag** | Checkbox | **conditional**: `true` if `youtubeChannelId` is non-null, else `false` <!-- CHANGED: was unconditional `false`, reviewers R2/R3/R4 flagged YouTube-lag mismatch. --> | `showVoteTag: bool` | ✅ next vote (next run) |

Notes:
- **Vote duration**: hardcoded `TimeSpan.FromSeconds(30)` in four patches today (the prefix bodies of `AncientVotePatch.PrefixContinue`, `BossVotePatch.PrefixContinue`, `CardRewardVotePatch.Prefix`, `ActVariantVotePatch.PrefixContinue`). <!-- CHANGED: was line-number references; reviewers R3/R5 flagged that those rot. Switched to method names. -->. All four switch to reading `ModSettings.Current.VoteDurationSeconds`. **Note**: `NeowBlessingVotePatch` was renamed to `AncientVotePatch` in commit `7bb0d24` (B.2.2 predicate-widening), so the AncientVotePatch entry covers Neow.<!-- CHANGED: explicit correction to head off the four reviewers who flagged "Neow missing." -->.
- **Vote on Act 1 variant**: already a `ChatSettings` field; this just exposes it in the UI.
- **Allow chat to skip**: when on, `#0` is **Skip** in the vote list and cards shift to `#1..#N`. See "Card-option construction" subsection below. <!-- CHANGED: reviewer R2 flagged the renumbering as under-specified. --> When off, the streamer drives skip via the parent Proceed button and skip is excluded from the vote tally (current behaviour). Bo confirmed StS1's mod always allowed chat-skip with `#0 = skip`; default-on matches that convention. Help text: *"When on, chat can vote `#0` to skip a card reward (cards become `#1`, `#2`, etc.). When off, skipping is streamer-only."*
- **Show vote tag**: controls whether the `[04]`-style tag appears in the on-screen `VoteTallyLabel` and in chat receipts from `EnglishReceipts`. <!-- CHANGED: terminology cleanup, reviewers R2/R3 flagged confusion. Tag display form is `[04]`; chat syntax is `!04`; never `#04` (which reads as option 4). --> Help text: *"Shows a vote tag (e.g. `[04]`) on screen and tells chat they can vote with `#1!04` so delayed votes don't land in the wrong vote. Useful for YouTube where chat lag is real. Stale-vote rejection works the same way regardless of this setting."*

**Card-option construction** <!-- CHANGED: new subsection in response to R2's renumbering concern. -->:

When `cardSkipAsVoteOption = false`:
```
#0 = first card
#1 = second card
#2 = third card
```

When `cardSkipAsVoteOption = true`:
```
#0 = Skip
#1 = first card
#2 = second card
#3 = third card
```

The list is constructed in `CardRewardVotePatch.Prefix` by prepending a `"Skip"` label to the existing `optionsSnapshot.Select(o => o.Card.Title)` projection when the setting is on. Index resolution at vote-close maps `#0` → vanilla skip path; `#N` → `optionsSnapshot[N - 1]`.

**Parser invariant** <!-- CHANGED: was open Q3; now locked into spec body per R2/R4 recommendation. -->: `VoteSession`'s parser ALWAYS drops stale-tag votes (votes with `!NN` where NN doesn't match the current `VoteId`). The `showVoteTag` toggle controls *display only*; the underlying anti-stale-vote mechanism remains defensive regardless.

### Group 2 — Streamer

| UI label | Control | Default | JSON key | Applies |
|---|---|---|---|---|
| **Card skips per act** *(streamer's)* | Dropdown: `0 (strict)` / `1` / `2` / `3` / `5` / `Unlimited` | `1` | `cardSkipsPerAct: int` *(existing; sentinel `-1` = unlimited)* | ✅ next budget check |

Notes:
- **Card skips per act**: number of card-reward skips the **streamer** can use per act (disambiguated in help text from the Group-1 "Allow chat to skip" row which is about *chat's* ability). One-line help: *"Number of card-reward skips the streamer can use per act. `0` = strict (no skips)."* <!-- CHANGED: R4 suggested explicit "0 = strict" mention in help. -->
- Discrete dropdown rather than a slider — values are mode-shaped (going from 3 to 5 to Unlimited is a behaviour change, not a smooth gradient).
- "Unlimited" maps to the existing `-1` sentinel in JSON. Streamer never sees `-1`. UI reads JSON's current value back on open; if value isn't in the dropdown's enumerated set (e.g., legacy `4`), the dropdown shows `Custom (4)` and reverts to the enumerated set only when the user explicitly picks a new value.

### Group 3 — Profile maintenance

| UI label | Control | Behaviour |
|---|---|---|
| **Back up modded save** | Button + toast (no confirmation — non-destructive) <!-- CHANGED: R5 asked for the asymmetry with Unlock to be acknowledged. --> | One-shot. Copies the StS2 user-data save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\` (local time). Includes a `backup-manifest.txt` with creation timestamp, mod version, and scope. <!-- CHANGED: R2 suggested manifest. --> On same-second collision, appends `-01`, `-02`, etc. <!-- CHANGED: R2/R5 collision-handling. --> On success: toast `"Backed up to backups/<timestamp>"`. On failure: log Warn and toast `"Backup failed."` <!-- CHANGED: dropped "see godot.log" pointer per R1/R4 nit. --> Disabled while a copy is in progress. <!-- CHANGED: R3 addition. --> Motivation: streamers commonly run Spire Scryer alongside this mod, so save-modification risk is real. |
| **Unlock everything** | Button + confirmation popup with **explicit button labels** | One-shot. Confirmation popup labelled `"Unlock everything"` / `"Cancel"` (NOT `"OK"` / `"Cancel"`) <!-- CHANGED: R3 button-label flag. -->. Popup text: *"This will first back up your modded save, then mark every card, potion, relic, monster, event, epoch, and ascension as unlocked. **This cannot be undone.** Your modded save is separate from your regular Slay the Spire 2 progress."* <!-- CHANGED: auto-backup language per R2/R4. --> Flow on confirm: (1) run backup, (2) if backup succeeds, invoke same APIs as `UnlockConsoleCmd` + `SaveManager.Instance.SaveProgressFile()` + toast `"Everything unlocked."`, (3) if backup fails, show second confirmation `"Backup failed. Unlock without backup?"` with labels `"Unlock anyway"` / `"Cancel"`. |

Both action buttons are stateless — no JSON persistence required.

### Settings file path (informational row) <!-- CHANGED: R4 trivial-cost addition for "I need to edit my OAuth" cases. -->

Below the three groups, a read-only label:

> Settings file: `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`

No button; just a discoverability aid for streamers who need to edit JSON-only fields.

### Settings deliberately JSON-only

| Field | Why JSON-only |
|---|---|
| `schemaVersion` | Machine field; user-edits would break version-detection |
| `channel` | Set-once; normalised with warnings (URL stripping, `#` prefix handling) |
| `username` | Set-once; lowercased with warnings if mixed-case |
| `oauthToken` | Set-once; **OAuth value would be visible to viewers if the streamer opens settings while live** |
| `youtubeChannelId` | Set-once; trim-and-validate with warnings; nullable |
| `forceL3PopupFallback` | Dev/debug knob, never streamer-facing |

## UI placement

**Vanilla anchor**: the right-hand panel of `NModdingScreen` is `NModInfoContainer`. It populates three children — `_title` (MegaRichTextLabel), `_image` (TextureRect), `_description` (MegaRichTextLabel with author/version/description/errors) — inside `Fill(Mod)`.

**Injection point**: Harmony postfix on `NModInfoContainer.Fill`. The postfix:

1. **First removes any existing injected settings panel** by searching for a child named `Sts2SettingsPanel` and calling `QueueFree()` on it. <!-- CHANGED: was "append a child", reviewers R1/R2/R4/R5 unanimously flagged duplicate-append risk. Verified vs decompiled NModInfoContainer.cs which only updates _title/_image/_description and does NOT clear arbitrary children. -->
2. **Then conditionally injects** a fresh `Sts2SettingsPanel` only when `mod.manifest.id == ModConstants.ModId` (`"slay_the_streamer_2"`). <!-- CHANGED: R5 suggested constant rather than literal. -->
3. The named cleanup also handles "selected our mod, then another mod" — the panel doesn't appear under other mods because step 2's id check fails.

**Layout, top-to-bottom inside the panel:**

```
┌─ (vanilla) Title — "Slay the Streamer 2" ─────┐
│  (vanilla) Image                               │
│  (vanilla) Author / Version / Description     │
│  ────────────────────────────────────────     │
│  Vote behaviour                                │
│    Vote duration            [══●═══] 30s       │
│    Vote on Act 1 variant    [✓]                │
│    Allow chat to skip       [✓]                │
│    Show vote tag            [ ]                │
│  ────────────────────────────────────────     │
│  Streamer                                      │
│    Card skips per act       [ 1 ▾ ]            │
│      Number of card-reward skips the streamer  │
│      can use per act. 0 = strict (no skips).   │
│  ────────────────────────────────────────     │
│  Profile maintenance                           │
│    [ Back up modded save ]                     │
│    [ Unlock everything ]                       │
│      This cannot be undone.                    │
│  ────────────────────────────────────────     │
│  Settings file:                                │
│  %APPDATA%\SlayTheSpire2\                      │
│    slay_the_streamer_2.json                    │
└────────────────────────────────────────────────┘
```

**Scroll behaviour** <!-- CHANGED: R2/R3 raised scroll capacity concern. -->: implementation-time verification needed for whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If yes, the injected block uses default Godot layout. If no, the injected block wraps itself in a `ScrollContainer` to handle overflow at lower resolutions or with long vanilla descriptions.

**Style approach** <!-- CHANGED: was open Q2; resolved per R4 recommendation. -->: hand-rolled MegaText labels + stock Godot controls (CheckBox, HSlider, OptionButton, Button). Rationale: avoids `PreloadManager.Cache.GetScene(...).Instantiate()` complications (cross-scene-tree state, scene-unique-name collisions) and gives us full control over layout. Trade-off accepted: stylistic drift if MegaCrit changes vanilla settings rows. Mitigation: keep the panel visually simple so drift is minimal.

**Why this and not a tab or popup**: see v1 spec for full rationale; unchanged.

## Persistence

### Multi-trigger save <!-- CHANGED: was single-trigger on NSubmenu.OnSubmenuClosed; reviewers R2/R4/R5 flagged hook unreliability. -->

Save is triggered by **two paths**:

1. **Primary: debounced save-on-change**. Every control's value-change event sets `_dirty = true`, updates `ModSettings.Current` immediately (in-memory), and resets a 500ms `Timer`. When the timer fires, if `_dirty`, the write executes and `_dirty` clears.
2. **Backup: save-on-modding-screen-close**. Harmony postfix on `NModdingScreen._ExitTree()` (verified target) forces a flush if `_dirty` is still true.

This eliminates the `OnSubmenuClosed` reliability concern entirely. Confirmation popups for "Unlock everything" don't trigger the save path because they don't trigger `NModdingScreen._ExitTree()`. <!-- CHANGED: R3 specifically flagged confirmation-popup-as-submenu-push risk. -->

### Atomic write <!-- CHANGED: R2 suggested. -->

Write path uses a temp-file + rename pattern:

1. Read existing JSON into a `JsonNode` (or empty `JsonObject` if file doesn't exist — **first-run path is supported** <!-- CHANGED: v1 said "unreachable"; R1/R2/R3 corrected. -->).
2. Overwrite the values for the **five persisted settings keys**: `voteDurationSeconds`, `voteOnActVariant`, `cardSkipAsVoteOption`, `showVoteTag`, `cardSkipsPerAct`. <!-- CHANGED: was "seven UI-managed keys"; R2/R3 corrected. -->
3. Write merged JSON to `slay_the_streamer_2.json.tmp`.
4. Copy current `slay_the_streamer_2.json` (if it exists) to `slay_the_streamer_2.json.bak` (single rolling backup).
5. Atomic rename `.tmp` → main file.
6. On failure at any step: leave the previous file intact, log `TiLog.Warn`, show toast `"Failed to save settings."` In-memory `ModSettings.Current` still reflects the user's change.

### First-run path <!-- CHANGED: new section per R1/R2/R3. -->

If the settings panel is opened and `slay_the_streamer_2.json` doesn't exist (e.g., the streamer is configuring before adding credentials): on the first save trigger, the writer creates a new file containing `{"schemaVersion": 1}` plus the five UI-managed fields at their current values. Toast notes: `"Created settings file."` The streamer still needs to add credentials by hand to enable chat integration; the settings UI doesn't surface OAuth.

## Hot-reload mechanics

The existing `ChatSettings` record (a C# `record` — immutable reference type) is held by `ModEntry` as a single captured-once snapshot. v2 changes this to:

```csharp
public static class ModSettings {
    private static ChatSettings? _current;

    public static ChatSettings? Current => System.Threading.Volatile.Read(ref _current);

    public static void UpdateCurrent(ChatSettings settings)
        => System.Threading.Volatile.Write(ref _current, settings);
}
```

<!-- CHANGED: explicit Volatile semantics per R2. -->

**Consumer contract**: each call site that reads a `ChatSettings` field snapshots `Current` to a local **once per logical operation** to avoid torn reads across fields:

```csharp
var settings = ModSettings.Current;
if (settings is null) return;
session = coordinator.Start(label, options, TimeSpan.FromSeconds(settings.VoteDurationSeconds), ...);
```

Not this (torn-read risk):

```csharp
duration = TimeSpan.FromSeconds(ModSettings.Current.VoteDurationSeconds);
// ... settings update happens here ...
showTag = ModSettings.Current.ShowVoteTag;   // may not be from the same logical settings snapshot
```

**Closure-capture audit** <!-- CHANGED: R5 raised; verified across the four patches. -->: all four vote patches (`AncientVotePatch`, `BossVotePatch`, `CardRewardVotePatch`, `ActVariantVotePatch`) use `static` methods that read `ChatSettings` inside their prefix bodies, not in patch-construction captures. `CardRewardSkipGatePatch` reads `ChatSettings.CardSkipsPerAct` inside its prefix/postfix bodies the same way. The migration is uniform: replace `success.Settings.X` reads with `ModSettings.Current?.X` reads at the same call sites.

**`NModdingScreen` is disabled mid-run.** [`NSettingsScreen.cs:157`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L157) calls `_moddingScreenButton.Disable()` when `RunManager.Instance.IsInProgress`. <!-- CHANGED: critical R4 finding; spec was claiming hot-reload that's not actually reachable mid-run. --> So in practice:
- Hot-reload happens at the main menu (between runs).
- "Next vote" in the inventory table means "first vote of the next run."
- This is acceptable for v1 since streamers typically configure between runs anyway, but the spec language reflects reality.

**Threading and test interaction** <!-- CHANGED: R4 requested. -->: production code reads `ModSettings.Current`; tests inject `ChatSettings` directly via `VoteSessionTestBase.CreateCoordinator(...)` constructor parameters. `ModSettings.Current` is the production-only read site. Tests should not write to `ModSettings.Current` to avoid static-singleton-in-tests issues; existing test fixtures already pass settings explicitly.

## Schema migration

The three new fields are additive-optional with defaults applied in `ModSettings.Load`:
- `voteDurationSeconds: int` — default `30`, clamped to `[10, 120]` on load with `TiLog.Warn` if out-of-range.
- `cardSkipAsVoteOption: bool` — default `true` (no existing users to surprise; matches Bo's StS1 convention).
- `showVoteTag: bool` — default conditional on `youtubeChannelId`: `true` if non-null, else `false`. <!-- CHANGED: conditional default per R2/R3/R4. -->

Per existing convention, additive-optional fields don't require a `CurrentSchemaVersion` bump (current is `1`). Old JSON files load with defaults applied.

**Load validation** <!-- CHANGED: R2 requested explicit validation rules. -->: each new field follows the existing `ModSettings.Load` pattern:
- Missing → apply default silently.
- Wrong type → warn + apply default.
- Out-of-range numeric (`voteDurationSeconds` outside `[10, 120]`, or `cardSkipsPerAct < -1`) → warn + clamp.
- Legacy unsupported `cardSkipsPerAct` value (e.g. `4`) → preserve as-is in load; UI displays `Custom (4)` until user picks an enumerated value.

## Implementation outline

New files under `src/Game/Ui/Settings/`:

| File | Purpose |
|---|---|
| `SettingsPanelPatch.cs` | Harmony postfix on `NModInfoContainer.Fill`; named-child cleanup + conditional injection of `SettingsPanelBuilder` output. Also holds a static dirty-bag for tracking pending changes across panel rebuilds. <!-- CHANGED: R2 suggested decoupling dirty state from the panel; minimal form is a static here. --> |
| `SettingsPanelBuilder.cs` | Imperative scene construction for the settings rows using hand-rolled MegaText + stock Godot controls (CheckBox, HSlider, OptionButton, Button). ~150-250 LOC. |
| `SettingsWriter.cs` | Atomic read-merge-write of `slay_the_streamer_2.json` using `JsonNode`. Includes first-run create path. **Unit-testable** — no Godot dependencies. <!-- CHANGED: R2/R5 testing strategy. --> |
| `SettingsSaveDebouncer.cs` | 500ms Godot `Timer` wrapper that fires `SettingsWriter.WriteIfDirty()` on debounced changes. |
| `UnlockAllAction.cs` | Auto-backup + Unlock-everything flow. Invokes the same APIs as `UnlockConsoleCmd` (`SaveManager.Instance.Progress.MarkCardAsSeen` etc.) after a successful backup. |
| `BackupSaveAction.cs` | One-shot button handler. Copies the save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\` with collision-handling suffixes. Writes a `backup-manifest.txt` file alongside. |
| `ModConstants.cs` | Constants: `ModId = "slay_the_streamer_2"`, `SettingsFileName`, `BackupSubdir`. |

Changes to existing files:

| File | Change |
|---|---|
| `src/Game/Bootstrap/ModSettings.cs` | Add three new optional fields with defaults + validation. Add `Current` static property with `Volatile` semantics + `UpdateCurrent` method. Switch to `JsonNode`-based parsing. |
| `ChatSettings` record | Extend with `VoteDurationSeconds`, `CardSkipAsVoteOption`, `ShowVoteTag` fields. |
| `src/ModEntry.cs` | Call `ModSettings.UpdateCurrent(settings)` after load. Consumers downstream use `ModSettings.Current` rather than captured locals. |
| `src/Game/DecisionVotes/AncientVotePatch.cs` | Read `ModSettings.Current?.VoteDurationSeconds` for the `coordinator.Start` duration argument. |
| `src/Game/DecisionVotes/BossVotePatch.cs` | Same. |
| `src/Game/DecisionVotes/CardRewardVotePatch.cs` | Same; additionally read `CardSkipAsVoteOption` and prepend "Skip" label when on, with index remapping at vote-close. |
| `src/Game/DecisionVotes/ActVariantVotePatch.cs` | Same vote-duration change. |
| `src/Ti/Voting/VoteCoordinator.cs` | `Start` accepts a `bool showTag` parameter; threads through to new `VoteSession`. **Test impact**: `VoteSessionTestBase.CreateCoordinator(...)` adds a default `showTag = true` parameter so existing tests don't break. <!-- CHANGED: R5 test-impact note. --> |
| `src/Ti/Voting/VoteSession.cs` | Accept and store `showTag`; expose via `VoteSnapshot.ShowTag`. **Parser invariant unchanged**: stale-tag rejection runs regardless of `showTag`. |
| `src/Ti/Voting/VoteSnapshot.cs` | Add `bool ShowTag` field. |
| `src/Ti/Voting/EnglishReceipts.cs` | Conditionally render `[{VoteId:D2}]:` only when `ShowTag` is true. |
| `src/Ti/Ui/VoteTallyLabel.cs` | Conditionally render the `[NN]` header + `(or #N!NN)` hint only when `ShowTag` is true. |
| README + `dist/slay_the_streamer_2.example.json` | Document the three new optional fields and the JSON-only fields. |

**MegaCrit API stability note** <!-- CHANGED: R4. -->: `SaveManager.Instance.Progress.MarkCardAsSeen` and related unlock APIs are public-but-undocumented vanilla surfaces. Operator validation for each game-version update should re-confirm `UnlockAllAction.cs` still functions.

**Load-order note** <!-- CHANGED: R5. -->: the Harmony postfix on `NModInfoContainer.Fill` only takes effect after `ModEntry.Init` runs and `harmony.PatchAll` completes. Mod load order is already established (the mod loads at game start), so this is not a concern in practice but worth noting for any future hot-reload-the-mod scenarios.

## Test plan <!-- CHANGED: R2/R4/R5 requested. -->

Unit-testable surfaces (live under `tests/`):

- `tests/Bootstrap/ModSettingsTests.cs` — extend with cases for:
  - Missing `voteDurationSeconds` → defaults to `30`.
  - Out-of-range `voteDurationSeconds` → clamps + warns.
  - Missing `cardSkipAsVoteOption` → defaults `true`.
  - Missing `showVoteTag` → defaults conditionally based on `youtubeChannelId`.
  - Wrong-type for any new field → warns + applies default.
  - Legacy unsupported `cardSkipsPerAct = 4` → preserved as-is in load.
- `tests/Game/Ui/Settings/SettingsWriterTests.cs` (new):
  - Round-trip preserves unknown keys (`forceL3PopupFallback`, any future field).
  - Round-trip preserves JSON-only credential fields.
  - First-run path creates file with `schemaVersion: 1` + five UI-managed fields.
  - Atomic write leaves prior file intact on simulated mid-write failure.
- `tests/Voting/VoteSessionTagTests.cs` (extend existing):
  - `ShowTag = false` + parser still rejects stale `!NN`.
  - `ShowTag = false` + `EnglishReceipts` omits `[NN]:` prefix.
  - `ShowTag = true` + receipts include `[NN]:`.
- `tests/Game/DecisionVotes/CardRewardVoteOptionConstructionTests.cs` (new, if extractable from `CardRewardVotePatch`):
  - `cardSkipAsVoteOption = true` → labels = `["Skip", card1, card2, card3]`.
  - `cardSkipAsVoteOption = false` → labels = `[card1, card2, card3]`.
  - Index remap at vote-close: `#0` with skip-on → vanilla skip path; `#N` with skip-on → `optionsSnapshot[N-1]`.

**Non-unit-testable surfaces** (Godot UI):
- `SettingsPanelBuilder.cs`, `UnlockAllAction.cs` (Godot/main-thread dependencies). Validated via operator-validation checklist instead. Per CLAUDE.md "Test isolation for TiLog," any tests that exercise TiLog must use `[Collection("TiLog.Sink")]`.

## Operator-validation checklist <!-- CHANGED: R2/R4 requested. -->

Run at end of slice on a live stream:

1. **Duplicate-append guard**: Open mod manager, select our mod, select another mod, select ours again. Confirm no duplicate panels, no stale panels under wrong mod.
2. **Vote duration**: Set duration to 60s. Start a run. Next vote (Neow / Card Reward / Ancients / Boss / Act-1 variant) uses 60s.
3. **Vote on Act 1 variant**: Toggle off. Start a new run. No variant vote fires.
4. **Allow chat to skip**: Toggle on. Open a card reward. Chat sees `#0 Skip / #1 ... / #2 ... / #3 ...`. Chat vote `#0` → skips reward. Toggle off, repeat: chat sees `#0 / #1 / #2` (cards), streamer drives skip via Proceed.
5. **Show vote tag**: Toggle off when YouTube isn't configured. Tally label + chat receipts hide tag. Type `#0!99` in chat — vote is dropped (parser invariant). Toggle on, tag appears.
6. **Card skips per act**: Change to `0 (strict)`. Next reward screen, streamer's Skip button is disabled / counter reads 0.
7. **Persistence**: Close mod manager. Close game. Restart. Reopen mod manager. All settings preserved. JSON file: open in editor, confirm unknown keys (e.g. `forceL3PopupFallback`) still present.
8. **Settings write failure**: Mark `slay_the_streamer_2.json` read-only. Change a setting. Confirm toast `"Failed to save settings."` Confirm in-memory `ModSettings.Current` still reflects the change (next vote uses new value).
9. **Back up modded save**: Click button. Confirm `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\` exists with save data + `backup-manifest.txt`.
10. **Unlock everything**: Click button. Confirmation popup shows correct labels (`"Unlock everything"` / `"Cancel"`). Confirm. Backup runs first, then unlock. Toast `"Everything unlocked."` Confirm in-game progress shows unlocked items.
11. **Unlock with backup failure**: Mark backups/ read-only. Click Unlock. Second confirmation `"Backup failed. Unlock without backup?"` appears.
12. **Mid-run inaccessibility** <!-- CHANGED: per R4 finding. -->: Start a run. Open settings screen. Confirm "Modding" button is greyed/disabled.

## Failure-mode table <!-- CHANGED: R2 requested. -->

| Failure | Expected behaviour |
|---|---|
| UI injection postfix throws | Log Error; vanilla mod manager still works; no settings panel appears |
| Settings write fails | Log Warn; toast `"Failed to save settings."`; in-memory `Current` reflects change |
| Backup fails | Log Warn; toast `"Backup failed."`; no partial-backup state left |
| Unlock fails mid-action | Log Error with phase identifier; toast `"Unlock failed."`; some categories may have unlocked already (acceptable — re-clicking is idempotent) |
| Malformed JSON on subsequent load | Existing `SettingsResult.Malformed` path: chat services don't connect; UI still loads with in-memory defaults |
| First-run with no file | First save creates file with `schemaVersion: 1` + UI-managed fields; toast `"Created settings file."` |

## Accessibility note <!-- CHANGED: R2 requested. -->

Mouse-only input is acceptable for v1. Controller / keyboard navigation through the injected panel is best-effort (whatever Godot's defaults provide on the stock controls used). Not explicitly tested in v1 operator validation.

## Cross-platform note <!-- CHANGED: R4 requested. -->

This mod is Windows-only in practice — the target audience runs Windows + Steam StS2. `OS.GetUserDataDir()` resolves correctly on Mac/Linux too (Godot handles this), so the persistence paths work cross-platform, but no v1 testing is planned outside Windows.

## Open implementation questions

Reduced from v1's five to a smaller list. The resolved-in-spec items moved into the spec body above.

1. **Backup scope**: copy only the StS2 `save/` subfolder, or the whole `SlayTheSpire2` user-data dir minus `logs/`, `slay_the_streamer_2.json`, and `backups/`? Spire Scryer touches the run-state files — confirm what to bundle so a restore is meaningful. Default proposal: copy `save/` only; revisit if user feedback suggests Scryer-state lives outside.
2. **Scroll container detection**: at implementation time, verify whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If not, wrap injected block in one.

(Open Q2-style/Q3/Q4 from v1 are now resolved in spec body.)

## Out of scope (deferred to later increments)

Per [notes/09 Tier 3](../../../notes/09-settings-and-tunable-knobs.md#suggested-v1-shortlist-research-level-recommendation):

- Per-vote feature toggles for Neow / Card Reward / Ancients / Boss
- Receipt-policy tuning (announce-on-open, announce-on-close, periodic-tally cadence)
- Voter-eligibility filters (sub-only, mod-only, VIP-only)
- Sealed-deck / draft / Neow-ordering knobs
- UI placement knobs (tally label anchor, font size)
- Logging verbosity, receipt language i18n
- Per-stream overrides (vs the current session-wide model)
- JSON5/JSONC comment support
- Hot-reload for credential changes (would require chat-service teardown + reconnect)

## Cross-references

- [META-REVIEW-2026-05-19-settings-ui-design.md](META-REVIEW-2026-05-19-settings-ui-design.md) — meta-review of v1 spec; this v2 applies its Must-do and Should-do items.
- [notes/09-settings-and-tunable-knobs.md](../../../notes/09-settings-and-tunable-knobs.md) — original landscape research.
- [src/Game/Bootstrap/ModSettings.cs](../../../src/Game/Bootstrap/ModSettings.cs) — current schema; this spec adds three additive-optional fields + `Current` static.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs) — Harmony target for UI injection.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs) — line 157 disables the modding button in-run.
- [decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs) — dev `unlock all` command whose underlying APIs we reuse.

---

## Optional Enhancements (pick what you want)

The meta-review's Consider-tier items. Reply with "apply N, M, ..." to fold any of these into the spec.

1. **`SettingApplyMode` enum for restart-required affordance pattern** *(R2)* — Define `enum SettingApplyMode { Immediate, NextVote, NextRewardScreen, NextBudgetCheck, RestartRequired }` and tag each row in the inventory with its apply mode. v1 doesn't need RestartRequired in the UI but the enum is forward-looking. **Effort: small. Recommendation: lean no** — premature abstraction for 5 settings, and "next-run first vote" is already explicit in the inventory.

2. **Sub-popup for Group 3 action buttons** *(R2, R3, R5)* — Replace the inline Backup + Unlock buttons with a single `[Profile maintenance...]` button that opens a popup containing the two actions. **Effort: medium**. **Recommendation: lean no** — adds one click + popup infrastructure for what's essentially mis-click reduction; the existing confirmation popup on Unlock + auto-backup already handles the safety angle.

3. **Type-to-confirm for "Unlock everything"** *(R4)* — Confirmation popup requires typing `UNLOCK` to enable the confirm button. **Effort: small**. **Recommendation: lean no** — auto-backup is already the strongest mitigation; type-to-confirm reads as alarmist for a feature that just reveals already-paid-for content on a modded profile.

4. **Tri-state `Show vote tag`** *(R4)* — Replace boolean with `off` / `receipts-only` / `receipts-and-overlay`. **Effort: small**. **Recommendation: lean no** — over-engineered for v1; binary toggle covers the dominant use case.

5. **"Pause Chat Voting" global toggle** *(R1)* — Single boolean that suppresses all chat-vote invocation, leaving the streamer to play un-voted. **Effort: medium** (needs threading through every patch's prefix and a stable "voting paused" indicator). **Recommendation: lean yes if the streamer wants quick-disable, otherwise lean no**. Worth considering for v1 because it's a genuine streamer ask, but it does add a row + a behavioural toggle that needs operator validation. **Surfinite to decide.**

6. **Reveal-in-Explorer button next to settings file path** *(R4)* — Adds `[Open folder]` button that shells out to `explorer.exe %APPDATA%\SlayTheSpire2`. **Effort: small**. **Recommendation: lean yes** — trivially cheap, real support-thread reducer.

7. **Chat-service status indicators** *(R4)* — Display `✓ Twitch connected` / `⚠ YouTube disconnected` somewhere in the panel. **Effort: medium** (needs subscription to chat state changes + UI refresh). **Recommendation: neutral** — useful but adds a live-state UI surface that wasn't designed into v1.

8. **"Test chat" button** *(R4)* — Sends a test message to chat to verify connectivity. **Effort: small**. **Recommendation: lean no for v1** — useful for first-time setup but the existing startup receipt (`"slay-the-streamer-2 connected (Twitch)."`) already does this on mod boot.

9. **Backup retention policy** *(R4)* — Auto-prune backups older than N days, or cap at K most recent. **Effort: small** (delete + log). **Recommendation: lean yes** — unbounded growth is a real concern and the deletion logic is trivial. Suggested default: keep last 10.

10. **"Restore from backup" button** *(R4)* — Pick a backup folder; copy contents back to `save/`. **Effort: medium** (file picker, validation, confirmation). **Recommendation: lean no for v1** — the auto-backup-before-unlock already provides the most common recovery story; manual restore is a power-user feature.
