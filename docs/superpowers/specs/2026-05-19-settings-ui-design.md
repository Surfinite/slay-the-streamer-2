# In-game settings UI — design spec

**Date**: 2026-05-19
**Status**: Design (brainstorm-output). Ready for review → implementation plan.
**Scope**: First in-game settings UI for the Slay the Streamer 2 mod. Surfaces a curated subset of streamer-tunable knobs inside the existing vanilla mod-manager screen.

## TL;DR

- **Approach**: Approach B-modified from [notes/09](../../../notes/09-settings-and-tunable-knobs.md). Extend the existing `NModInfoContainer` right-hand panel inside `NModdingScreen` with our own settings rows when the selected mod row is ours. Zero new tab, zero new popup, zero scene-instantiation surgery.
- **Scope**: Five tunable knobs (vote duration, Act-1 variant vote toggle, chat skip, card skips per act, vote tag display) + two one-shot action buttons (unlock everything, back up modded save). Seven UI rows total.
- **Identity/credential settings stay JSON-only.** OAuth on stream is a real exposure risk; channel/username/YT-channel-ID are set-once.
- **Hot-reload**: four of five settings hot-reload cleanly on next consumer-call. One-shot actions take effect immediately. No restart required for anything in v1.
- **Persistence**: save on `NModdingScreen` close to the existing `slay_the_streamer_2.json`, read-merge-write to preserve JSON-only fields. Additive-optional schema — no `schemaVersion` bump.

## Motivation

Today, every tunable lives either in code-as-literal or in the JSON file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Streamers can tweak the four currently-exposed JSON fields (`channel` / `username` / `oauthToken` / `cardSkipsPerAct`) plus the YT field and `voteOnActVariant` between sessions, but only via a text editor outside the game. Some knobs that streamers reasonably want to flip between runs in the same session — vote duration, whether chat can skip, whether the on-screen vote tag shows — have no exposure at all.

This spec captures the v1 cut: the smallest set of knobs that delivers real streamer-UX value without exploding the test matrix. Future settings (per-vote feature toggles for Neow/Card/Ancients/Boss, receipt-cadence tuning, voter eligibility filters, locale, etc.) are deferred to later increments per [notes/09 Part B](../../../notes/09-settings-and-tunable-knobs.md#part-b-tunable-knobs-inventory).

## Settings inventory

Seven rows total, grouped by section.

### Group 1 — Vote behaviour

| UI label | Control | Default | JSON key | Hot-reload |
|---|---|---|---|---|
| **Vote duration** | Slider 10–120s, step 5s, value badge | `30s` | `voteDurationSeconds: int` | ✅ next vote |
| **Vote on Act 1 variant** | Checkbox | `true` | `voteOnActVariant: bool` *(existing)* | ✅ next vote |
| **Allow chat to skip** | Checkbox | `true` | `cardSkipAsVoteOption: bool` | ✅ next reward screen |
| **Show vote tag** | Checkbox | `false` | `showVoteTag: bool` | ✅ next vote |

Notes:
- **Vote duration**: existing hardcoded `TimeSpan.FromSeconds(30)` in four patches (`AncientVotePatch:137`, `BossVotePatch:270`, `CardRewardVotePatch:235`, `ActVariantVotePatch:264`). All four switch to reading `ChatSettings.VoteDurationSeconds`.
- **Vote on Act 1 variant**: already a `ChatSettings` field; this just exposes it in the UI.
- **Allow chat to skip**: when on, `#0` appears as a "Skip" option in the vote list for card-reward votes. When off, the streamer drives skip via the parent Proceed button and skip is excluded from the vote tally (current behaviour). Bo confirmed StS1's mod always allowed chat-skip with `#0 = skip`; default-on matches that convention.
- **Show vote tag**: controls whether the `#04`-style tag appears in the on-screen `VoteTallyLabel` and in chat receipts from `EnglishReceipts`. Help text: *"Tags each vote with a number (#04) so old votes can't bleed into a new one. Useful if YouTube vote lag is dropping votes into the wrong session."*

### Group 2 — Streamer

| UI label | Control | Default | JSON key | Hot-reload |
|---|---|---|---|---|
| **Card skips per act** *(streamer's)* | Dropdown: `0` (strict) / `1` / `2` / `3` / `5` / `Unlimited` | `1` | `cardSkipsPerAct: int` *(existing; sentinel `-1` = unlimited)* | ✅ next budget check |

Notes:
- **Card skips per act**: number of card-reward skips the **streamer** can use per act (disambiguated in help text from the Group-1 "Allow chat to skip" row which is about *chat's* ability). One-line help: *"Number of card-reward skips the streamer can use per act."*
- Discrete dropdown rather than a slider — values are mode-shaped (going from 3 to 5 to Unlimited is a behaviour change, not a smooth gradient).
- "Unlimited" maps to the existing `-1` sentinel in JSON. Streamer never sees `-1`.

### Group 3 — Profile maintenance

| UI label | Control | Behaviour |
|---|---|---|
| **Unlock everything** | Button + confirmation popup | One-shot. Confirm popup says: *"Mark every card, potion, relic, monster, event, epoch, and ascension as unlocked. **This cannot be undone.** Your modded save is separate from your regular Slay the Spire 2 progress."* On confirm: invoke the same APIs `UnlockConsoleCmd` uses (`SaveManager.Instance.Progress.MarkCardAsSeen` etc. across cards/potions/relics/monsters/events/epochs/ascensions) + `SaveManager.Instance.SaveProgressFile()` + toast `"Everything unlocked."` |
| **Back up modded save** | Button + toast | One-shot. Copies the StS2 user-data save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\`. Shows toast `"Backed up to backups/<timestamp>"` on success, or `"Backup failed — see godot.log"` on failure. Motivation: streamers commonly run Spire Scryer alongside this mod, so save-modification risk is real. |

Both action buttons are stateless — no JSON persistence required.

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

**Vanilla anchor**: the right-hand panel of `NModdingScreen` is `NModInfoContainer` ([decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs:51](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs#L51)). It populates three children — `_title` (MegaRichTextLabel), `_image` (TextureRect), `_description` (MegaRichTextLabel with author/version/description/errors) — inside `Fill(Mod)`.

**Injection point**: Harmony postfix on `NModInfoContainer.Fill`. If `mod.manifest.id == "slay_the_streamer_2"`, append our settings container as a child Control below the existing description. The screen rebuilds on each submenu-open, so re-population is naturally re-entrant.

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
│      can use per act.                          │
│  ────────────────────────────────────────     │
│  Profile maintenance                           │
│    [ Back up modded save ]                     │
│    [ Unlock everything ]                       │
│      This cannot be undone.                    │
└────────────────────────────────────────────────┘
```

**Why this and not a tab or popup**:
- **Tab injection (Approach A in notes/09)**: requires reflective `_tabs` dictionary mutation in `NSettingsTabManager`, packed-scene instantiation for tab/panel templates from `.tscn` paths, and risks tab-bar overflow at 5+ tabs. High implementation cost.
- **Popup from General-tab Modding row (Approach B1 in notes/09)**: adds discoverability friction (extra click to get to settings from the general settings tab via a sibling button to the existing "open mod manager" button). Mod-manager screen is where players already go to think about their installed mods.
- **Inline-below-the-LHS-row**: would require `NModMenuRow` surgery and constrain row width.

The RHS info panel is the right home: discoverable, no scene surgery, conditional on mod selection.

## Persistence

**Trigger**: on `NModdingScreen` close. Hooked via Harmony postfix on `NSubmenu.OnSubmenuClosed` (or whichever lifecycle hook is reliable for the modding submenu specifically).

**Write path**: `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`, full overwrite using read-merge-write semantics:
1. Read the current JSON file (or start with an empty `JsonObject` if it doesn't exist — but this case is unreachable because `ModSettings.Load` already runs at mod init and a missing file means the mod is in a broken state).
2. Use `System.Text.Json` `JsonNode` to round-trip unknown keys (preserves `forceL3PopupFallback` and any future fields we don't yet surface in UI).
3. Overwrite the values for the seven UI-managed keys.
4. Write back to disk.

**Dirty tracking**: one `bool _dirty` flag on the settings container, flipped on any control's value-change event. On submenu close, only write if `_dirty == true`. Avoids spurious disk writes when the user just inspects the settings.

**Cancel semantics**: there is no cancel button. Matches vanilla `NSettingsScreen` convention (changes persist on close). If users want to revert, they can edit the JSON file by hand.

**Failure handling**: if the write fails (file locked, permission denied), log `TiLog.Warn` and show a transient toast: `"Couldn't save settings — see godot.log."` The in-memory `ChatSettings` snapshot still updates so the user's session reflects the change even if the disk write didn't persist.

## Hot-reload mechanics

The existing `ChatSettings` record is held by `ModEntry` as a single captured immutable snapshot. To support hot-reload:

1. Add a `ModSettings.Current` static property holding the latest `ChatSettings`. Updated on settings-screen close.
2. Consumers (vote patches, `CardRewardSkipGatePatch`) read `ModSettings.Current.X` at the moment they need a value — not via captured locals.
3. For the `showVoteTag` toggle: `VoteCoordinator.Start` reads `ModSettings.Current.ShowVoteTag` and threads it into the new `VoteSession` instance. In-flight votes don't change behaviour mid-session; only the next-opened vote sees the new value. Acceptable per "next vote" hot-reload semantics in the inventory table.

The migration is mostly a "swap the reference, don't hold the reference" change — most call sites already pass `success.Settings` around and the change is uniform.

## Schema migration

The three new fields are additive-optional with defaults applied in `ModSettings.Load`:
- `voteDurationSeconds: int` — default `30`
- `cardSkipAsVoteOption: bool` — default `true`
- `showVoteTag: bool` — default `false`

Per existing convention, additive-optional fields don't require a `CurrentSchemaVersion` bump (current is `1`). Old JSON files load with defaults applied. `ModSettings.Load` emits a one-time `TiLog.Info` if it applied any defaults, so streamers upgrading from a config without these fields see a log note.

**Behaviour-change at upgrade**: `cardSkipAsVoteOption` defaults to `true`, which is a behaviour flip from current code (skip is currently NOT in the vote list). This is intentional per Bo's confirmation that StS1 always had chat-skip at `#0`. Documented in:
- README on GitHub
- The bundled `slay_the_streamer_2.example.json` shipped in `dist/`
- A one-time log line on first load after upgrade: `"cardSkipAsVoteOption defaulting to true; chat can now vote #0 to skip card rewards. Toggle off in mod settings if you prefer streamer-driven skip."`

## Implementation outline (sketch — not a plan)

New files under `src/Game/Ui/Settings/`:

| File | Purpose |
|---|---|
| `SettingsPanelPatch.cs` | Harmony postfix on `NModInfoContainer.Fill`; conditional injection of `SettingsPanelBuilder` output as a child Control when the mod row is ours. |
| `SettingsPanelBuilder.cs` | Imperative scene construction for the settings rows. ~150 LOC. Mirrors vanilla `NSettingsTickbox`/`NSettingsSlider`/`NSettingsButton` styling, either by duplicating vanilla packed scenes or wrapping stock Godot controls with MegaText siblings (resolved during implementation — see "Open implementation questions"). |
| `SettingsWriter.cs` | Read-merge-write of `slay_the_streamer_2.json` using `JsonNode` for unknown-key round-tripping. |
| `UnlockAllAction.cs` | One-shot button handler. Invokes `SaveManager.Instance.Progress.MarkCardAsSeen` and friends across all model types, then `SaveProgressFile()`. |
| `BackupSaveAction.cs` | One-shot button handler. Copies the save subfolder to `%APPDATA%\SlayTheSpire2\backups\YYYY-MM-DD-HHMMSS\`. |

Changes to existing files:

| File | Change |
|---|---|
| `src/Game/Bootstrap/ModSettings.cs` | Add three new optional fields with defaults. Add `Current` static property for hot-reload reads. |
| `src/Game/Bootstrap/ChatSettings` record | Extend with `VoteDurationSeconds`, `CardSkipAsVoteOption`, `ShowVoteTag` fields. |
| `src/ModEntry.cs` | Switch from captured-once `ChatSettings` to `ModSettings.Current` static. |
| `src/Game/DecisionVotes/AncientVotePatch.cs` | Read `ChatSettings.VoteDurationSeconds` for the `coordinator.Start` duration argument. |
| `src/Game/DecisionVotes/BossVotePatch.cs` | Same. |
| `src/Game/DecisionVotes/CardRewardVotePatch.cs` | Same; additionally read `CardSkipAsVoteOption` for option-list construction. |
| `src/Game/DecisionVotes/ActVariantVotePatch.cs` | Same vote-duration change. |
| `src/Ti/Voting/VoteCoordinator.cs` | `Start` accepts a `bool showTag` parameter; threads through to new `VoteSession`. |
| `src/Ti/Voting/VoteSession.cs` | Accept and store `showTag`; expose via `VoteSnapshot.ShowTag`. |
| `src/Ti/Voting/VoteSnapshot.cs` | Add `bool ShowTag` field. |
| `src/Ti/Voting/EnglishReceipts.cs` | Conditionally render `[{VoteId:D2}]:` only when `ShowTag` is true. |
| `src/Ti/Ui/VoteTallyLabel.cs` | Conditionally render the `#NN` suffix only when `ShowTag` is true. |
| README + `dist/slay_the_streamer_2.example.json` | Document the three new optional fields, the JSON-only fields, and the `cardSkipAsVoteOption` default-on behaviour change. |

## Open implementation questions

Flagged for the implementation-plan stage; don't block design approval.

1. **Backup scope**: copy only the StS2 `save/` subfolder, or the whole `SlayTheSpire2` user-data dir minus `logs/` and `slay_the_streamer_2.json`? Spire Scryer touches the run-state files — need to confirm what to bundle so a restore is meaningful. Default proposal: copy `save/` only; revisit if user feedback suggests Scryer-state lives outside.
2. **MegaCrit's settings-row styles in modded code**: do we duplicate vanilla `NSettingsTickbox`/`NSettingsSlider`/`NSettingsButton` packed scenes via `PreloadManager.Cache.GetScene(...).Instantiate()` (risk: cross-scene-tree state, scene-unique-name collisions), or build our own MegaText+Godot-control hybrid rows (risk: stylistic drift from vanilla)? Probably the latter — vanilla styles drift across patches anyway and a hand-rolled row gives us full control. Resolve during implementation.
3. **Vote-tag toggle parser semantics**: when `showVoteTag = false`, does `VoteSession`'s parser still drop stale-tag votes (`#NN` from a previous vote)? Recommendation: yes — the tag still exists internally and parsing-side defensiveness is decoupled from display. The toggle only affects what's rendered on-screen and in chat receipts.
4. **Submenu-close hook reliability**: `NSubmenu.OnSubmenuClosed` may fire from main-thread or via an event router; need to verify the hook fires exactly once per close and not on transient submenu-pushing-other-submenu cases. Fall back to a Harmony prefix on whatever method tears the modding screen down if the obvious hook doesn't work.
5. **Restart-required wording**: none of the v1 settings actually require restart. If a future setting does (e.g., `youtubeChannelId` if we ever surface it), we'll need a "applies on restart" affordance pattern. Out of scope for v1.

## Out of scope (deferred to later increments)

Per [notes/09 Tier 3](../../../notes/09-settings-and-tunable-knobs.md#suggested-v1-shortlist-research-level-recommendation):

- Per-vote feature toggles for Neow / Card Reward / Ancients / Boss
- Receipt-policy tuning (announce-on-open, announce-on-close, periodic-tally cadence)
- Voter-eligibility filters (sub-only, mod-only, VIP-only)
- Sealed-deck / draft / Neow-ordering knobs
- UI placement knobs (tally label anchor, font size)
- Logging verbosity, receipt language
- Per-stream overrides (vs the current session-wide model)
- JSON5 / JSONC comment support
- Hot-reload for credential changes (would require chat-service teardown + reconnect)

## Cross-references

- [notes/09-settings-and-tunable-knobs.md](../../../notes/09-settings-and-tunable-knobs.md) — original landscape research and tunable-knobs inventory.
- [src/Game/Bootstrap/ModSettings.cs](../../../src/Game/Bootstrap/ModSettings.cs) — current schema; this spec adds three additive-optional fields.
- [decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModInfoContainer.cs) — Harmony target for UI injection.
- [decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs](../../../decompiled/sts2/MegaCrit/sts2/Core/DevConsole/ConsoleCommands/UnlockConsoleCmd.cs) — the dev `unlock all` command whose underlying APIs we reuse for Row 6.
