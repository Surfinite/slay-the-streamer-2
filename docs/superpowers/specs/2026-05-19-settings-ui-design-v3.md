# In-game settings UI — design spec (v3 — user-feedback applied)

**Date**: 2026-05-19
**Status**: Design (post-user-feedback). Ready for implementation plan.
**Scope**: First in-game settings UI for the Slay the Streamer 2 mod. Surfaces a curated subset of streamer-tunable knobs inside the existing vanilla mod-manager screen.
**Changes from v2**: Dropped "Unlock everything" button (over-engineering; vote patches don't filter by unlock state — replaced with a README note + dev-console escape hatch). Added "Restore from backup" button. Added Reveal-in-Explorer button next to settings file path. Chat-status indicators deferred to a separate slice. No auto-deletion features (no backup retention).

## TL;DR

- **Approach**: Approach B-modified from [notes/09](../../../notes/09-settings-and-tunable-knobs.md). Extend the existing `NModInfoContainer` right-hand panel inside `NModdingScreen` with our own settings rows when the selected mod row is ours. Zero new tab, zero new popup, zero scene-instantiation surgery.
- **Scope**: Five tunable knobs (vote duration, Act-1 variant vote toggle, chat skip, card skips per act, vote tag display) + two one-shot save-management buttons (back up modded save, restore from backup). Seven UI rows total + a settings-file-path informational row with a Reveal-in-Explorer button.
- **No "Unlock everything" button.** <!-- CHANGED v3: dropped — vote patches don't filter by unlock state; cards and relics aren't affected; bosses may be (vanilla-side filter, not ours). Cost of the button (confirmation flow, irreversibility, mis-click risk) outweighs the benefit. Replaced with a README note + the dev-console `unlock all` command as escape hatch. -->
- **Identity/credential settings stay JSON-only.** OAuth on stream is a real exposure risk; channel/username/YT-channel-ID are set-once.
- **Apply-on-change with multi-trigger save.** The five persisted knobs apply on the next vote / next reward screen / next budget check after the user touches the control. **`NModdingScreen` is disabled mid-run** via `_moddingScreenButton.Disable()`, so in practice changes take effect on the next run rather than during an active one. Save-management buttons take effect immediately.
- **Persistence**: debounced atomic write to the existing `slay_the_streamer_2.json`, read-merge-write to preserve JSON-only fields. Additive-optional schema — no `schemaVersion` bump.
- **Five persisted settings keys + two stateless save-management buttons.**

## Motivation

Today, every tunable lives either in code-as-literal or in the JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Streamers can tweak the four currently-exposed JSON fields (`channel` / `username` / `oauthToken` / `cardSkipsPerAct`) plus the YT field and `voteOnActVariant` between sessions, but only via a text editor outside the game. Some knobs that streamers reasonably want to flip between runs in the same session — vote duration, whether chat can skip, whether the on-screen vote tag shows — have no exposure at all.

This spec captures the v1 cut: the smallest set of knobs that delivers real streamer-UX value without exploding the test matrix. Future settings (per-vote feature toggles for Neow/Card/Ancients/Boss, receipt-cadence tuning, voter eligibility filters, locale, chat-status indicators, etc.) are deferred to later increments per [notes/09 Part B](../../../notes/09-settings-and-tunable-knobs.md#part-b-tunable-knobs-inventory).

**"Bo" reference**: Bo is a collaborator/playtester who has provided design input on chat-vote conventions from playing Tempus's original StS1 mod. When the spec cites "Bo confirmed X," it means X was validated against StS1 chat-vote behaviour.

## Settings inventory

Seven rows total, grouped by section.

### Group 1 — Vote behaviour

| UI label | Control | Default | JSON key | Applies |
|---|---|---|---|---|
| **Vote duration** | Slider 10–120s, step 5s, value badge `"30s"` | `30s` | `voteDurationSeconds: int` | ✅ next vote (next run) |
| **Vote on Act 1 variant** | Checkbox | `true` | `voteOnActVariant: bool` *(existing)* | ✅ next vote (next run) |
| **Allow chat to skip** | Checkbox | `true` | `cardSkipAsVoteOption: bool` | ✅ next reward screen |
| **Show vote tag** | Checkbox | **conditional**: `true` if `youtubeChannelId` is non-null, else `false` | `showVoteTag: bool` | ✅ next vote (next run) |

Notes:
- **Vote duration**: hardcoded `TimeSpan.FromSeconds(30)` in four patches today (the prefix bodies of `AncientVotePatch.PrefixContinue`, `BossVotePatch.PrefixContinue`, `CardRewardVotePatch.Prefix`, `ActVariantVotePatch.PrefixContinue`). All four switch to reading `ModSettings.Current.VoteDurationSeconds`. **Note**: `NeowBlessingVotePatch` was renamed to `AncientVotePatch` in commit `7bb0d24` (B.2.2 predicate-widening), so the AncientVotePatch entry covers Neow.
- **Vote on Act 1 variant**: already a `ChatSettings` field; this just exposes it in the UI.
- **Allow chat to skip**: when on, `#0` is **Skip** in the vote list and cards shift to `#1..#N`. See "Card-option construction" subsection below. When off, the streamer drives skip via the parent Proceed button and skip is excluded from the vote tally (current behaviour). Bo confirmed StS1's mod always allowed chat-skip with `#0 = skip`; default-on matches that convention. Help text: *"When on, chat can vote `#0` to skip a card reward (cards become `#1`, `#2`, etc.). When off, skipping is streamer-only."*
- **Show vote tag**: controls whether the `[04]`-style tag appears in the on-screen `VoteTallyLabel` and in chat receipts from `EnglishReceipts`. Tag display form is `[04]`; chat syntax is `!04`; never `#04` (which reads as option 4). Help text: *"Shows a vote tag (e.g. `[04]`) on screen and tells chat they can vote with `#1!04` so delayed votes don't land in the wrong vote. Useful for YouTube where chat lag is real. Stale-vote rejection works the same way regardless of this setting."*

**Card-option construction**:

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

**Parser invariant**: `VoteSession`'s parser ALWAYS drops stale-tag votes (votes with `!NN` where NN doesn't match the current `VoteId`). The `showVoteTag` toggle controls *display only*; the underlying anti-stale-vote mechanism remains defensive regardless.

### Group 2 — Streamer

| UI label | Control | Default | JSON key | Applies |
|---|---|---|---|---|
| **Card skips per act** *(streamer's)* | Dropdown: `0 (strict)` / `1` / `2` / `3` / `5` / `Unlimited` | `1` | `cardSkipsPerAct: int` *(existing; sentinel `-1` = unlimited)* | ✅ next budget check |

Notes:
- **Card skips per act**: number of card-reward skips the **streamer** can use per act (disambiguated in help text from the Group-1 "Allow chat to skip" row which is about *chat's* ability). One-line help: *"Number of card-reward skips the streamer can use per act. `0` = strict (no skips)."*
- Discrete dropdown rather than a slider — values are mode-shaped.
- "Unlimited" maps to the existing `-1` sentinel in JSON. Streamer never sees `-1`. UI reads JSON's current value back on open; if value isn't in the dropdown's enumerated set (e.g., legacy `4`), the dropdown shows `Custom (4)` and reverts to the enumerated set only when the user explicitly picks a new value.

### Group 3 — Save management <!-- CHANGED v3: renamed from "Profile maintenance" to reflect the narrower scope (no unlocking). -->

| UI label | Control | Behaviour |
|---|---|---|
| **Back up modded save** | Button + toast (no confirmation — non-destructive) | One-shot. Copies the StS2 user-data save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\` (local time). Includes a `backup-manifest.txt` with creation timestamp, mod version, and scope. On same-second collision, appends `-01`, `-02`, etc. On success: toast `"Backed up to backups/<timestamp>"`. On failure: log Warn and toast `"Backup failed."` Disabled while a copy is in progress. Motivation: streamers commonly run Spire Scryer alongside this mod, so save-modification risk is real. |
| **Restore from backup…** <!-- CHANGED v3: new button, applies pick-list item #10. --> | Button → opens backup-selection sub-popup | One-shot. Opens a popup listing all subfolders under `%APPDATA%\SlayTheSpire2\backups\` (most-recent first, each row showing folder name + manifest timestamp if readable). On row select + confirm: copies the backup contents back over the StS2 `save/` subfolder. Confirmation popup labels: `"Restore this backup"` / `"Cancel"`. Confirmation text: *"This will overwrite your current modded save with the contents of `<folder>`. **Your current save will be lost.** (Click `Back up modded save` first if you want to keep it.)"* On success: toast `"Restored from backups/<timestamp>. Restart recommended."` On failure: log Warn and toast `"Restore failed."` |

Both buttons are stateless — no JSON persistence required.

**Note on file deletion**: this mod will not delete files from the user's PC. No auto-pruning of old backups; the streamer manages disk space themselves. Rationale: any bug or future change that mis-deleted files would be a stream-day disaster.<!-- CHANGED v3: explicit no-auto-deletion policy per user push-back. -->

**Note on unlocks**: previously this section had an "Unlock everything" button. **Dropped from v1** — verification of [`BossVotePatch.cs:242`](../../../src/Game/DecisionVotes/BossVotePatch.cs#L242) and `CardRewardVotePatch` confirms our vote patches don't filter by unlock state; they consume vanilla pools directly. Cards and relics aren't affected by our code at all. **For boss vote**, our patch samples from the act's full boss pool (`runState.Act.AllBossEncounters`) regardless of which bosses the streamer has unlocked through vanilla progression — meaning chat may vote for, and the run may end on, a boss the streamer hasn't seen before in normal play. This is the intended behaviour: chat gets the full pool to pick from. The dev-console `unlock all` command remains available as an escape hatch for streamers who want their codex / progression to match what chat is seeing in-run.<!-- CHANGED v3+: corrected direction — over-exposure, not filtering. Vote samples full pool; streamer may fight bosses they haven't unlocked. -->

A README note will set expectations:
> **Slay the Streamer 2 was developed and tested on a modded save with all content unlocked.** The boss vote samples from the act's full boss pool, so chat may vote for — and the run may end on — bosses you haven't unlocked through vanilla progression. This is the intended behaviour; chat gets the full pool to pick from. If you want your boss codex / progression to match what chat is seeing in-run, run `unlock all` in the dev console at any time.

### Settings file path (informational row)

Below the three groups, a read-only label with a Reveal-in-Explorer button:

> Settings file: `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`   **[ Open folder ]**

Clicking `Open folder` shells out via `Godot.OS.ShellOpen(...)` (or platform-equivalent) to open the directory in Explorer. <!-- CHANGED v3: pick-list item #6 applied. --> Discoverability aid for streamers who need to edit JSON-only fields (credentials, schema, dev/debug knobs).

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

1. **First removes any existing injected settings panel** by searching for a child named `Sts2SettingsPanel` and calling `QueueFree()` on it. Verified vs decompiled `NModInfoContainer.cs` which only updates `_title`/`_image`/`_description` and does NOT clear arbitrary children.
2. **Then conditionally injects** a fresh `Sts2SettingsPanel` only when `mod.manifest.id == ModConstants.ModId` (`"slay_the_streamer_2"`).
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
│  Save management                               │
│    [ Back up modded save ]                     │
│    [ Restore from backup… ]                    │
│  ────────────────────────────────────────     │
│  Settings file:                                │
│  %APPDATA%\SlayTheSpire2\                      │
│    slay_the_streamer_2.json   [ Open folder ]  │
└────────────────────────────────────────────────┘
```

**Scroll behaviour**: implementation-time verification needed for whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If yes, the injected block uses default Godot layout. If no, the injected block wraps itself in a `ScrollContainer` to handle overflow at lower resolutions or with long vanilla descriptions.

**Style approach**: hand-rolled MegaText labels + stock Godot controls (CheckBox, HSlider, OptionButton, Button). Rationale: avoids `PreloadManager.Cache.GetScene(...).Instantiate()` complications (cross-scene-tree state, scene-unique-name collisions) and gives us full control over layout. Trade-off accepted: stylistic drift if MegaCrit changes vanilla settings rows. Mitigation: keep the panel visually simple so drift is minimal.

**Why this and not a tab or popup**: see v1 spec for full rationale; unchanged.

## Persistence

### Multi-trigger save

Save is triggered by **two paths**:

1. **Primary: debounced save-on-change**. Every control's value-change event sets `_dirty = true`, updates `ModSettings.Current` immediately (in-memory), and resets a 500ms `Timer`. When the timer fires, if `_dirty`, the write executes and `_dirty` clears.
2. **Backup: save-on-modding-screen-close**. Harmony postfix on `NModdingScreen._ExitTree()` (verified target) forces a flush if `_dirty` is still true.

This eliminates the `OnSubmenuClosed` reliability concern entirely. Confirmation popups for Restore-from-backup don't trigger the save path because they don't trigger `NModdingScreen._ExitTree()`. <!-- CHANGED v3: was confirmation popup for Unlock-everything; now Restore. -->

### Atomic write

Write path uses a temp-file + rename pattern:

1. Read existing JSON into a `JsonNode` (or empty `JsonObject` if file doesn't exist — **first-run path is supported**).
2. Overwrite the values for the **five persisted settings keys**: `voteDurationSeconds`, `voteOnActVariant`, `cardSkipAsVoteOption`, `showVoteTag`, `cardSkipsPerAct`.
3. Write merged JSON to `slay_the_streamer_2.json.tmp`.
4. Copy current `slay_the_streamer_2.json` (if it exists) to `slay_the_streamer_2.json.bak` (single rolling backup).
5. Atomic rename `.tmp` → main file.
6. On failure at any step: leave the previous file intact, log `TiLog.Warn`, show toast `"Failed to save settings."` In-memory `ModSettings.Current` still reflects the user's change.

### First-run path

If the settings panel is opened and `slay_the_streamer_2.json` doesn't exist (e.g., the streamer is configuring before adding credentials): on the first save trigger, the writer creates a new file containing `{"schemaVersion": 1}` plus the five UI-managed fields at their current values. Toast notes: `"Created settings file."` The streamer still needs to add credentials by hand to enable chat integration; the settings UI doesn't surface OAuth.

## Hot-reload mechanics

The existing `ChatSettings` record (a C# `record` — immutable reference type) is held by `ModEntry` as a single captured-once snapshot. v2 introduced a `Current` static for hot-reload, retained in v3:

```csharp
public static class ModSettings {
    private static ChatSettings? _current;

    public static ChatSettings? Current => System.Threading.Volatile.Read(ref _current);

    public static void UpdateCurrent(ChatSettings settings)
        => System.Threading.Volatile.Write(ref _current, settings);
}
```

**Consumer contract**: each call site that reads a `ChatSettings` field snapshots `Current` to a local **once per logical operation** to avoid torn reads across fields.

**Closure-capture audit**: all four vote patches (`AncientVotePatch`, `BossVotePatch`, `CardRewardVotePatch`, `ActVariantVotePatch`) use `static` methods that read `ChatSettings` inside their prefix bodies, not in patch-construction captures. `CardRewardSkipGatePatch` reads `ChatSettings.CardSkipsPerAct` inside its prefix/postfix bodies the same way. The migration is uniform: replace `success.Settings.X` reads with `ModSettings.Current?.X` reads at the same call sites.

**`NModdingScreen` is disabled mid-run.** [`NSettingsScreen.cs:157`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L157) calls `_moddingScreenButton.Disable()` when `RunManager.Instance.IsInProgress`. So in practice:
- Hot-reload happens at the main menu (between runs).
- "Next vote" in the inventory table means "first vote of the next run."
- This is acceptable for v1 since streamers typically configure between runs anyway, but the spec language reflects reality.

**Threading and test interaction**: production code reads `ModSettings.Current`; tests inject `ChatSettings` directly via `VoteSessionTestBase.CreateCoordinator(...)` constructor parameters. `ModSettings.Current` is the production-only read site.

## Schema migration

The three new fields are additive-optional with defaults applied in `ModSettings.Load`:
- `voteDurationSeconds: int` — default `30`, clamped to `[10, 120]` on load with `TiLog.Warn` if out-of-range.
- `cardSkipAsVoteOption: bool` — default `true` (no existing users to surprise; matches Bo's StS1 convention).
- `showVoteTag: bool` — default conditional on `youtubeChannelId`: `true` if non-null, else `false`.

Per existing convention, additive-optional fields don't require a `CurrentSchemaVersion` bump (current is `1`). Old JSON files load with defaults applied.

**Load validation**: each new field follows the existing `ModSettings.Load` pattern:
- Missing → apply default silently.
- Wrong type → warn + apply default.
- Out-of-range numeric (`voteDurationSeconds` outside `[10, 120]`, or `cardSkipsPerAct < -1`) → warn + clamp.
- Legacy unsupported `cardSkipsPerAct` value (e.g. `4`) → preserve as-is in load; UI displays `Custom (4)` until user picks an enumerated value.

## Implementation outline

New files under `src/Game/Ui/Settings/`:

| File | Purpose |
|---|---|
| `SettingsPanelPatch.cs` | Harmony postfix on `NModInfoContainer.Fill`; named-child cleanup + conditional injection. Holds a static dirty-bag for tracking pending changes across panel rebuilds. |
| `SettingsPanelBuilder.cs` | Imperative scene construction. Hand-rolled MegaText + stock Godot controls. |
| `SettingsWriter.cs` | Atomic read-merge-write of `slay_the_streamer_2.json` using `JsonNode`. Includes first-run create path. **Unit-testable** — no Godot dependencies. |
| `SettingsSaveDebouncer.cs` | 500ms Godot `Timer` wrapper that fires `SettingsWriter.WriteIfDirty()` on debounced changes. |
| `BackupSaveAction.cs` | One-shot button handler. Copies the save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\` with collision-handling suffixes. Writes a `backup-manifest.txt` file alongside. |
| `RestoreFromBackupAction.cs` <!-- CHANGED v3: new. --> | Opens backup-selection popup; on confirm, copies selected backup folder back over the `save/` subfolder. Includes confirmation popup with explicit button labels. |
| `RevealInExplorerAction.cs` <!-- CHANGED v3: new (pick-list #6). --> | Shells out to `Godot.OS.ShellOpen(userDataDir)` to open the settings directory in Explorer (or platform equivalent). |
| `ModConstants.cs` | Constants: `ModId = "slay_the_streamer_2"`, `SettingsFileName`, `BackupSubdir`. |

<!-- CHANGED v3: removed UnlockAllAction.cs (button dropped). -->

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
| `src/Ti/Voting/VoteCoordinator.cs` | `Start` accepts a `bool showTag` parameter; threads through to new `VoteSession`. **Test impact**: `VoteSessionTestBase.CreateCoordinator(...)` adds a default `showTag = true` parameter so existing tests don't break. |
| `src/Ti/Voting/VoteSession.cs` | Accept and store `showTag`; expose via `VoteSnapshot.ShowTag`. **Parser invariant unchanged**: stale-tag rejection runs regardless of `showTag`. |
| `src/Ti/Voting/VoteSnapshot.cs` | Add `bool ShowTag` field. |
| `src/Ti/Voting/EnglishReceipts.cs` | Conditionally render `[{VoteId:D2}]:` only when `ShowTag` is true. |
| `src/Ti/Ui/VoteTallyLabel.cs` | Conditionally render the `[NN]` header + `(or #N!NN)` hint only when `ShowTag` is true. |
| README | Document the three new optional fields, the JSON-only fields, the dev-console `unlock all` escape hatch, and the tested-with-everything-unlocked baseline note. |
| `dist/slay_the_streamer_2.example.json` | Example file for new installs. |

**MegaCrit API stability note**: backup/restore operate on the StS2 `save/` subfolder shape, which is vanilla-determined. Operator validation for each game-version update should re-confirm `BackupSaveAction` and `RestoreFromBackupAction` still produce/consume the expected layout.

**Load-order note**: the Harmony postfix on `NModInfoContainer.Fill` only takes effect after `ModEntry.Init` runs and `harmony.PatchAll` completes. Mod load order is already established (the mod loads at game start), so this is not a concern in practice.

## Test plan

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
- `tests/Game/DecisionVotes/CardRewardVoteOptionConstructionTests.cs` (new, if extractable):
  - `cardSkipAsVoteOption = true` → labels = `["Skip", card1, card2, card3]`.
  - `cardSkipAsVoteOption = false` → labels = `[card1, card2, card3]`.

**Non-unit-testable surfaces** (Godot UI): `SettingsPanelBuilder.cs`, `BackupSaveAction.cs`, `RestoreFromBackupAction.cs`, `RevealInExplorerAction.cs`. Validated via operator-validation checklist instead. Per CLAUDE.md "Test isolation for TiLog," any tests that exercise TiLog must use `[Collection("TiLog.Sink")]`.

## Operator-validation checklist

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
10. **Restore from backup**: <!-- CHANGED v3: replaces unlock-related items. --> Make a backup. Make an in-game change (advance the run, claim a card). Click `Restore from backup…`, select the prior backup, confirm. Confirm in-game state is back to pre-change.
11. **Restore confirmation labels**: Click `Restore from backup…`. Confirm popup uses `"Restore this backup"` / `"Cancel"` labels, NOT `"OK"` / `"Cancel"`.
12. **Reveal in Explorer**: Click `Open folder` next to the settings-file-path label. Confirm `%APPDATA%\SlayTheSpire2\` opens in Explorer.
13. **Mid-run inaccessibility**: Start a run. Open settings screen. Confirm "Modding" button is greyed/disabled.
14. **Full-pool boss vote on a not-fully-unlocked profile**: <!-- CHANGED v3+: corrected direction. --> On a fresh modded profile (without running dev-console `unlock all`), trigger a boss vote. Confirm: chat-vote candidates include bosses the streamer hasn't unlocked in vanilla; selected boss is actually fought; no in-run UI weirdness (locked-icon overlays, missing codex info during fight, etc.). Document any visual edge cases for the README note.

## Failure-mode table

| Failure | Expected behaviour |
|---|---|
| UI injection postfix throws | Log Error; vanilla mod manager still works; no settings panel appears |
| Settings write fails | Log Warn; toast `"Failed to save settings."`; in-memory `Current` reflects change |
| Backup fails | Log Warn; toast `"Backup failed."`; no partial-backup state left |
| Restore fails mid-copy | Log Error; toast `"Restore failed."`; some files may have copied. Existing `.bak` of settings file unaffected. No automatic rollback (user can restore from a different backup or re-run). <!-- CHANGED v3: new. --> |
| Malformed JSON on subsequent load | Existing `SettingsResult.Malformed` path: chat services don't connect; UI still loads with in-memory defaults |
| First-run with no file | First save creates file with `schemaVersion: 1` + UI-managed fields; toast `"Created settings file."` |
| `Open folder` shell call fails | Log Warn; toast `"Couldn't open folder."` Most users will have working `Godot.OS.ShellOpen`; this is defensive only. <!-- CHANGED v3: new. --> |

## Accessibility note

Mouse-only input is acceptable for v1. Controller / keyboard navigation through the injected panel is best-effort (whatever Godot's defaults provide on the stock controls used). Not explicitly tested in v1 operator validation.

## Cross-platform note

This mod is Windows-only in practice — the target audience runs Windows + Steam StS2. `OS.GetUserDataDir()` resolves correctly on Mac/Linux too (Godot handles this), so the persistence paths work cross-platform, but no v1 testing is planned outside Windows.

## Open implementation questions

1. **Backup scope**: copy only the StS2 `save/` subfolder, or the whole `SlayTheSpire2` user-data dir minus `logs/`, `slay_the_streamer_2.json`, and `backups/`? Spire Scryer touches the run-state files — confirm what to bundle so a restore is meaningful. Default proposal: copy `save/` only; revisit if user feedback suggests Scryer-state lives outside.
2. **Scroll container detection**: at implementation time, verify whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If not, wrap injected block in one.
3. **Backup-list display in Restore popup**: <!-- CHANGED v3: new. --> Filesystem listing vs richer card view? v1 proposal: simple `ItemList` showing each subfolder name + manifest timestamp (if `backup-manifest.txt` readable). No thumbnails, no preview of save state.
4. **Visual edge cases for full-pool boss vote on a not-fully-unlocked profile**: <!-- CHANGED v3+: reframed. --> post-operator-validation, document any UI weirdness when chat votes a not-yet-unlocked boss (locked-icon overlays in BossVotePopup, codex/lore stubs during fight, etc.) so the README note can flag them or so we know to ship `unlock all` guidance more prominently.

## Out of scope (deferred to later increments)

Per [notes/09 Tier 3](../../../notes/09-settings-and-tunable-knobs.md#suggested-v1-shortlist-research-level-recommendation):

- **Chat-service status indicators** (`✓ Twitch connected` / `⚠ YouTube disconnected`) <!-- CHANGED v3: pick-list item #7, intentionally deferred to a separate slice per user preference. -->
- Per-vote feature toggles for Neow / Card Reward / Ancients / Boss
- Receipt-policy tuning (announce-on-open, announce-on-close, periodic-tally cadence)
- Voter-eligibility filters (sub-only, mod-only, VIP-only)
- Sealed-deck / draft / Neow-ordering knobs
- UI placement knobs (tally label anchor, font size)
- Logging verbosity, receipt language i18n
- Per-stream overrides (vs the current session-wide model)
- JSON5/JSONC comment support
- Hot-reload for credential changes (would require chat-service teardown + reconnect)
- Backup retention / auto-pruning <!-- CHANGED v3: explicitly out-of-scope per user "no auto-deletion" policy. -->
- "Unlock everything" in-UI button <!-- CHANGED v3: dropped from v1; dev-console escape hatch is sufficient. -->

## Cross-references

- [META-REVIEW-2026-05-19-settings-ui-design.md](META-REVIEW-2026-05-19-settings-ui-design.md) — meta-review of v1 spec; v2 applied its Must-do + Should-do.
- [2026-05-19-settings-ui-design-v2.md](2026-05-19-settings-ui-design-v2.md) — v2 (post-meta-review). v3 supersedes.
- [notes/09-settings-and-tunable-knobs.md](../../../notes/09-settings-and-tunable-knobs.md) — original landscape research.
- [src/Game/Bootstrap/ModSettings.cs](../../../src/Game/Bootstrap/ModSettings.cs) — current schema; this spec adds three additive-optional fields + `Current` static.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs) — Harmony target for UI injection.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs) — line 157 disables the modding button in-run.
- [src/Game/DecisionVotes/BossVotePatch.cs](../../../src/Game/DecisionVotes/BossVotePatch.cs) — verified at line 242 to consume vanilla `Act.AllBossEncounters` without unlock-state filtering.
- [decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs) — the dev `unlock all` escape hatch documented in the README.

---

## Remaining Optional Enhancements (still on the pick-list)

Carried forward from v2 minus the applied/rejected items. Reply with "apply N, M, ..." to fold any of these in.

1. **`SettingApplyMode` enum for restart-required affordance pattern** *(R2)* — **Effort: small. Recommendation: lean no** (premature abstraction).
2. **Sub-popup for Group 3 buttons** *(R2, R3, R5)* — **Effort: medium**. **Recommendation: lean no** (only two buttons; both with their own confirmation patterns).
3. **Tri-state `Show vote tag`** *(R4)* — **Effort: small**. **Recommendation: lean no** (over-engineered for v1).
4. **"Pause Chat Voting" global toggle** *(R1)* — **Effort: medium** (threading through every patch + stable "voting paused" indicator). **Recommendation: lean yes if you want quick-disable; lean no otherwise.** Surfinite to decide.
5. **"Test chat" button** *(R4)* — **Effort: small**. **Recommendation: lean no for v1** (startup receipt already serves this purpose).
