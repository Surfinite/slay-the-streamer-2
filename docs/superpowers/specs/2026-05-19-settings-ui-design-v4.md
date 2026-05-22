# In-game settings UI — design spec (v4 — backup buttons dropped)

**Date**: 2026-05-19
**Status**: Design. Ready for implementation plan.
**Scope**: First in-game settings UI for the Slay the Streamer 2 mod. Surfaces a curated subset of streamer-tunable knobs inside the existing vanilla mod-manager screen.
**Changes from v3**: Dropped the "Back up modded save" + "Restore from backup" buttons entirely. Rationale: our mod doesn't write to the save file (vanilla's save manager handles persistence of patch-driven mutations), there's no destructive action in this slice, and Spire Scryer's save-modification risk is Spire Scryer's problem to manage. Settings UI now has 5 functional rows + 1 informational file-path row. The Reveal-in-Explorer button stays.

## TL;DR

- **Approach**: Approach B-modified from [notes/09](../../../notes/09-settings-and-tunable-knobs.md). Extend the existing `NModInfoContainer` right-hand panel inside `NModdingScreen` with our own settings rows when the selected mod row is ours.
- **Scope**: Five tunable knobs (vote duration, Act-1 variant vote toggle, chat skip, card skips per act, vote tag display) + a settings-file-path informational row with a Reveal-in-Explorer button. **No action buttons.** <!-- CHANGED v4: dropped Backup + Restore + Unlock buttons entirely. Mod doesn't write to save; no destructive actions warrant backup infrastructure. -->
- **Identity/credential settings stay JSON-only.** OAuth on stream is a real exposure risk; channel/username/YT-channel-ID are set-once.
- **Apply-on-change with multi-trigger save.** The five persisted knobs apply on the next vote / next reward screen / next budget check after the user touches the control. **`NModdingScreen` is disabled mid-run** via `_moddingScreenButton.Disable()`, so in practice changes take effect on the next run rather than during an active one.
- **Persistence**: debounced atomic write to the existing `slay_the_streamer_2.json`, read-merge-write to preserve JSON-only fields. Additive-optional schema — no `schemaVersion` bump.

## Motivation

Today, every tunable lives either in code-as-literal or in the JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Streamers can tweak the four currently-exposed JSON fields (`channel` / `username` / `oauthToken` / `cardSkipsPerAct`) plus the YT field and `voteOnActVariant` between sessions, but only via a text editor outside the game. Some knobs that streamers reasonably want to flip between runs in the same session — vote duration, whether chat can skip, whether the on-screen vote tag shows — have no exposure at all.

This spec captures the v1 cut: the smallest set of knobs that delivers real streamer-UX value without exploding the test matrix. Future settings (per-vote feature toggles for Neow/Card/Ancients/Boss, receipt-cadence tuning, voter eligibility filters, chat-status indicators, etc.) are deferred to later increments per [notes/09 Part B](../../../notes/09-settings-and-tunable-knobs.md#part-b-tunable-knobs-inventory).

**"Bo" reference**: Bo is a collaborator/playtester who has provided design input on chat-vote conventions from playing Tempus's original StS1 mod.

## Settings inventory

Five rows total across two groups, plus an informational file-path row.

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

### Why no Backup / Restore / Unlock buttons <!-- CHANGED v4: documenting the deletion rationale for future-me. -->

Earlier drafts of this spec proposed `Back up modded save`, `Restore from backup`, and `Unlock everything` buttons. All three are out of v1 scope for these reasons:

- **Our mod doesn't write to the save file.** Vanilla's `SaveManager` persists patch-driven mutations (e.g., the boss-encounter swap from `MapCmd.SetBossEncounter`); we don't touch the file ourselves. There's nothing destructive in our code path to back up against.
- **No `Unlock everything` button.** Verification of [`BossVotePatch.cs:242`](../../../src/Game/DecisionVotes/BossVotePatch.cs#L242) and `CardRewardVotePatch` confirms our vote patches don't filter by unlock state. Cards and relics aren't affected. For boss vote, our patch samples from the act's full boss pool (`runState.Act.AllBossEncounters`) regardless of which bosses the streamer has unlocked through vanilla progression — meaning chat may vote for, and the run may end on, a boss the streamer hasn't seen before in normal play. This is the intended behaviour. Boss unlocks come early in vanilla progression so this is rarely a long-term issue. The dev-console `unlock all` command remains available as an escape hatch.
- **Spire Scryer save-modification risk is Spire Scryer's problem.** Streamers running third-party save-modifying mods should use whatever backup tool they prefer (manual folder copy, or a Scryer-side feature).
- **No file-deletion features anywhere.** Avoids any liability for accidentally deleting streamer data if a future bug or change misbehaved.

A README note will set expectations for the boss-pool behaviour:
> **Slay the Streamer 2 was developed and tested on a modded save with all content unlocked.** The boss vote samples from the act's full boss pool, so chat may vote for — and the run may end on — bosses you haven't unlocked through vanilla progression. This is the intended behaviour; chat gets the full pool to pick from. Boss unlocks come early in vanilla play, so this is rarely a lasting issue. If you want your boss codex / progression to match what chat is seeing in-run, run `unlock all` in the dev console at any time.

### Settings file path (informational row)

Below the two functional groups, a read-only label with a Reveal-in-Explorer button:

> Settings file: `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`   **[ Open folder ]**

Clicking `Open folder` shells out via `Godot.OS.ShellOpen(...)` to open the directory in Explorer. Discoverability aid for streamers who need to edit JSON-only fields (credentials, schema, dev/debug knobs).

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
│  Settings file:                                │
│  %APPDATA%\SlayTheSpire2\                      │
│    slay_the_streamer_2.json   [ Open folder ]  │
└────────────────────────────────────────────────┘
```

**Scroll behaviour**: implementation-time verification needed for whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If yes, the injected block uses default Godot layout. If no, the injected block wraps itself in a `ScrollContainer` to handle overflow at lower resolutions or with long vanilla descriptions. (Less likely to be a concern in v4 with the shorter row count, but still worth confirming.)

**Style approach**: hand-rolled MegaText labels + stock Godot controls (CheckBox, HSlider, OptionButton, Button). Rationale: avoids `PreloadManager.Cache.GetScene(...).Instantiate()` complications (cross-scene-tree state, scene-unique-name collisions) and gives us full control over layout. Trade-off accepted: stylistic drift if MegaCrit changes vanilla settings rows. Mitigation: keep the panel visually simple so drift is minimal.

**Why this and not a tab or popup**: see v1 spec for full rationale; unchanged.

## Persistence

### Multi-trigger save

Save is triggered by **two paths**:

1. **Primary: debounced save-on-change**. Every control's value-change event sets `_dirty = true`, updates `ModSettings.Current` immediately (in-memory), and resets a 500ms `Timer`. When the timer fires, if `_dirty`, the write executes and `_dirty` clears.
2. **Backup: save-on-modding-screen-close**. Harmony postfix on `NModdingScreen._ExitTree()` (verified target) forces a flush if `_dirty` is still true.

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

The existing `ChatSettings` record (a C# `record` — immutable reference type) is held by `ModEntry` as a single captured-once snapshot. v2 introduced a `Current` static for hot-reload, retained:

```csharp
public static class ModSettings {
    private static ChatSettings? _current;

    public static ChatSettings? Current => System.Threading.Volatile.Read(ref _current);

    public static void UpdateCurrent(ChatSettings settings)
        => System.Threading.Volatile.Write(ref _current, settings);
}
```

**Consumer contract**: each call site that reads a `ChatSettings` field snapshots `Current` to a local **once per logical operation** to avoid torn reads across fields.

**Closure-capture audit**: all four vote patches (`AncientVotePatch`, `BossVotePatch`, `CardRewardVotePatch`, `ActVariantVotePatch`) use `static` methods that read `ChatSettings` inside their prefix bodies, not in patch-construction captures. `CardRewardSkipGatePatch` reads `ChatSettings.CardSkipsPerAct` inside its prefix/postfix bodies the same way.

**`NModdingScreen` is disabled mid-run.** [`NSettingsScreen.cs:157`](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L157) calls `_moddingScreenButton.Disable()` when `RunManager.Instance.IsInProgress`. So in practice:
- Hot-reload happens at the main menu (between runs).
- "Next vote" in the inventory table means "first vote of the next run."

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
| `RevealInExplorerAction.cs` | Shells out to `Godot.OS.ShellOpen(userDataDir)` to open the settings directory. |
| `ModConstants.cs` | Constants: `ModId = "slay_the_streamer_2"`, `SettingsFileName`. |

<!-- CHANGED v4: removed BackupSaveAction.cs, RestoreFromBackupAction.cs (buttons dropped). -->

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
| README | Document the three new optional fields, the JSON-only fields, the dev-console `unlock all` escape hatch, and the full-pool boss-vote behaviour note. |
| `dist/slay_the_streamer_2.example.json` | Example file for new installs. |

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

**Non-unit-testable surfaces** (Godot UI): `SettingsPanelBuilder.cs`, `RevealInExplorerAction.cs`. Validated via operator-validation checklist instead. Per CLAUDE.md "Test isolation for TiLog," any tests that exercise TiLog must use `[Collection("TiLog.Sink")]`.

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
9. **Reveal in Explorer**: Click `Open folder` next to the settings-file-path label. Confirm `%APPDATA%\SlayTheSpire2\` opens in Explorer.
10. **Mid-run inaccessibility**: Start a run. Open settings screen. Confirm "Modding" button is greyed/disabled.
11. **Full-pool boss vote on a not-fully-unlocked profile**: On a fresh modded profile (without running dev-console `unlock all`), trigger a boss vote. Confirm: chat-vote candidates include bosses the streamer hasn't unlocked in vanilla; selected boss is actually fought; no in-run UI weirdness (locked-icon overlays, missing codex info during fight, etc.). Document any visual edge cases for the README note.

## Failure-mode table

| Failure | Expected behaviour |
|---|---|
| UI injection postfix throws | Log Error; vanilla mod manager still works; no settings panel appears |
| Settings write fails | Log Warn; toast `"Failed to save settings."`; in-memory `Current` reflects change |
| Malformed JSON on subsequent load | Existing `SettingsResult.Malformed` path: chat services don't connect; UI still loads with in-memory defaults |
| First-run with no file | First save creates file with `schemaVersion: 1` + UI-managed fields; toast `"Created settings file."` |
| `Open folder` shell call fails | Log Warn; toast `"Couldn't open folder."` Most users will have working `Godot.OS.ShellOpen`; this is defensive only. |

<!-- CHANGED v4: removed Backup-fails and Restore-fails rows (buttons dropped). -->

## Accessibility note

Mouse-only input is acceptable for v1. Controller / keyboard navigation through the injected panel is best-effort (whatever Godot's defaults provide on the stock controls used). Not explicitly tested in v1 operator validation.

## Cross-platform note

This mod is Windows-only in practice — the target audience runs Windows + Steam StS2. `OS.GetUserDataDir()` resolves correctly on Mac/Linux too (Godot handles this), so the persistence paths work cross-platform, but no v1 testing is planned outside Windows.

## Open implementation questions

1. **Scroll container detection**: at implementation time, verify whether `NModInfoContainer` is already inside a `ScrollContainer` ancestor. If not, wrap injected block in one. Lower priority in v4 with only 5 functional rows.
2. **Visual edge cases for full-pool boss vote on a not-fully-unlocked profile**: post-operator-validation, document any UI weirdness when chat votes a not-yet-unlocked boss (locked-icon overlays in BossVotePopup, codex/lore stubs during fight, etc.) so the README note can flag them or so we know to ship `unlock all` guidance more prominently.

## Out of scope (deferred to later increments)

Per [notes/09 Tier 3](../../../notes/09-settings-and-tunable-knobs.md#suggested-v1-shortlist-research-level-recommendation):

- **Chat-service status indicators** (`✓ Twitch connected` / `⚠ YouTube disconnected`) — intentionally deferred to a separate slice.
- **"Pause Chat Voting" global toggle** — intentionally deferred to a separate slice. <!-- CHANGED v4+: user wants this eventually but as its own slice; threading the toggle through every vote-bearing patch is non-trivial and best done as a focused increment. -->
- **Backup / Restore / Unlock buttons** — out of v1 per rationale in "Why no Backup / Restore / Unlock buttons" subsection above. <!-- CHANGED v4. -->
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

- [META-REVIEW-2026-05-19-settings-ui-design.md](META-REVIEW-2026-05-19-settings-ui-design.md) — meta-review of v1 spec.
- [2026-05-19-settings-ui-design-v2.md](2026-05-19-settings-ui-design-v2.md) — post-meta-review.
- [2026-05-19-settings-ui-design-v3.md](2026-05-19-settings-ui-design-v3.md) — post-user-feedback (Unlock dropped; Restore added). v4 supersedes (Backup + Restore also dropped).
- [notes/09-settings-and-tunable-knobs.md](../../../notes/09-settings-and-tunable-knobs.md) — original landscape research.
- [src/Game/Bootstrap/ModSettings.cs](../../../src/Game/Bootstrap/ModSettings.cs) — current schema.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs) — Harmony target for UI injection.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs) — line 157 disables the modding button in-run.
- [src/Game/DecisionVotes/BossVotePatch.cs](../../../src/Game/DecisionVotes/BossVotePatch.cs) — verified at line 242 to consume vanilla `Act.AllBossEncounters` without unlock-state filtering.

<!-- CHANGED v4+: Optional Enhancements section removed entirely. Pause Chat Voting and Chat-status indicators both deferred to separate slices (see Out of scope). All other pick-list items rejected. -->
