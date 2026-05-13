# Settings UI integration + tunable-knobs inventory — research findings

**Date**: 2026-05-12
**Status**: Research findings. Two-part: (1) how we'd hook a new tab into vanilla's settings screen, (2) a comprehensive list of things a future settings UI could expose. Not a spec, not a plan, not a prioritized v1 list. The output is a menu; Surfinite picks which knobs ship and when.
**Motivation**: Surfinite (2026-05-12) wants to scope a future settings UI. Goal shape: add a tab to the right of vanilla's INPUT tab; ideally every feature is independently toggle-able so a streamer can pick "chat picks the map route but not the card rewards" or similar combinations. Not all knobs will ship in v1 — testing surface for full combinatorics is too large — but a complete menu is needed before deciding which to gate behind code-only flags vs UI-exposed.

## TL;DR

- **Vanilla settings screen is hookable but the path is invasive.** The tab list is a private `Dictionary<NSettingsTab, NSettingsPanel>` populated from named scene-child nodes during `NSettingsScreen._Ready()`. There's no official "register a custom tab" API in MegaCrit's modding surface. Reachable via Harmony postfix on `_Ready` + reflection on `NSettingsTabManager._tabs`, but we'd need to instantiate or duplicate scene nodes for the tab/panel templates — non-trivial because the scenes are loaded from `.tscn` packed resources, not from C# constructors.
- **Cleaner alternative: a settings button under the existing Modding subpanel.** Vanilla already shows a "Modding" line in settings when `SaveManager.Instance.SettingsSave.ModSettings != null && ModManager.Mods.Count > 0` (which is our case). The line currently has one button (`OpenModdingScreen` → mod-enable list). Adding our own button there to open a mod-specific settings popup is much less risky than adding a new tab.
- **Knobs inventory has ~30 candidate settings.** Current `slay_the_streamer_2.json` exposes 4 fields (channel/username/oauthToken/cardSkipsPerAct + schemaVersion). Everything else lives as hardcoded literals in code, hardcoded `VoteReceiptPolicy.Default`, or planned-but-not-yet-implemented features in `notes/06/07/08`. The big buckets: per-vote toggles (which decisions trigger chat voting), per-vote tuning (duration, receipt cadence), skip mechanics, sealed-deck/Neow ordering, onboarding (unlock everything etc.), UI placement, advanced filters.
- **Recommendation for v1**: ship JSON-only settings (no UI), expose ~5–8 high-impact knobs first, leave the settings UI itself as a post-v1 polish. Reason: every toggle adds a test-matrix dimension; shipping the toggle without confirming both states works in operator-validation is the trap.

## Part A: Hooking the vanilla settings screen

### Vanilla architecture summary

The settings screen is built around three classes:

- [`NSettingsScreen`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs) — the submenu (`NSubmenu` subclass). `_Ready()` ([NSettingsScreen.cs:129](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L129)) grabs the `NSettingsTabManager` and per-tab `NSettingsPanel` nodes via `GetNode<...>("%SettingsTabManager")`-style scene-unique-name lookups. Settings rows are wired by name (e.g., `content.GetNode<Node>("FastMode")` at [NSettingsScreen.cs:174](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L174)).
- [`NSettingsTabManager`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsTabManager.cs) — owns `private readonly Dictionary<NSettingsTab, NSettingsPanel> _tabs` ([line 76](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsTabManager.cs#L76)). `_Ready()` ([line 112](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsTabManager.cs#L112)) populates four tabs in order: General, Graphics, Sound, Input. Tab-switching is dictionary-insertion-order driven; `TabLeft`/`TabRight` (lines 161-179) navigate via `_tabs.Keys.ToList()`.
- `NSettingsTab` / `NSettingsPanel` / `NSettingsTickbox` / `NSettingsDropdown` / `NSettingsSlider` / `NSettingsButton` — the row-level UI controls. Each is a packed scene loaded via `[ScriptPath("res://src/Core/Nodes/Screens/Settings/...")]`.

The tabs and rows are defined in the scene file `res://scenes/screens/settings_screen.tscn` (referenced from [`NSettingsScreen.cs:78`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L78)). The scene is in the game's `.pck`, not in our mod directory.

### Approach A: Add a 5th tab via Harmony postfix on `_Ready`

The plan if we go this route:

1. Postfix `NSettingsTabManager._Ready()`. Confirmed patchable from prior smoke work — `_Ready` is a public override on Godot nodes, Harmony's name-based lookup works for it.
2. Either (a) duplicate one of the existing `NSettingsTab` and `NSettingsPanel` nodes via `Node.Duplicate()`, clear its content, set the label to "Streamer Mod"; or (b) instantiate the packed scenes by loading `NSettingsTab.tscn` / `NSettingsPanel.tscn` from `res://...` paths (the `ScriptPath` attribute hints at where the source `.cs` lives, but the actual scene paths are something like `res://scenes/screens/settings/settings_tab.tscn` — would need to confirm by reading the existing nodes' `SceneFilePath` property at runtime).
3. Use reflection to access `_tabs` (private), add our new tab/panel pair as a 5th entry. Dictionary insertion order is preserved in modern .NET, so it'll be the rightmost.
4. Populate our panel with `NSettingsTickbox`-style rows for the actual settings. Each row is its own packed-scene instance with a `Label` MegaRichTextLabel + control nested inside.
5. Wire the click signal on our tab (vanilla does this in [`NSettingsTabManager._Ready:130-136`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsTabManager.cs#L130-L136)).
6. Save/load on the existing `OnSubmenuClosed` lifecycle ([NSettingsScreen.cs:254-262](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L254-L262)) by hooking the signal or writing to our own `slay_the_streamer_2.json` independently.

**Risks**:
- Scene instantiation from C# of packed scenes that have `[ScriptPath]` C# back-ends has corner cases. We'd be doing what the editor does at design time but at runtime.
- `_tabs` is private — reflection access is fine for read but mutation has to be carefully timed (before any code reads `_tabs.First()`).
- `ResetTabs()` ([line 143](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsTabManager.cs#L143)) calls `_tabs.First().Key.Select()` on every open — fine since we'd add ourselves last.
- Each new `NSettingsPanel` content row needs a scrollable container, label MegaText nodes, etc. The DOM is non-trivial; duplicating an existing row is much less work than building one from scratch.
- Layout: the tab bar has limited horizontal space; a 5th tab might overflow or compress the other four.

### Approach B: Add a settings button inside vanilla's "Modding" subpanel

Vanilla already conditionally renders a `%Modding` row in the General settings tab ([NSettingsScreen.cs:141-145](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L141-L145)):

```csharp
if (SaveManager.Instance.SettingsSave.ModSettings != null && ModManager.Mods.Count > 0)
{
    GetNode<Control>("%Modding").Visible = true;
    GetNode<Control>("%ModdingDivider").Visible = true;
}
```

The button there opens [`NModdingScreen`](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/ModdingScreen/NModdingScreen.cs) which is just the enable/disable list per mod. Our options:

- B1: Patch `NSettingsScreen._Ready` to add our own button next to the existing "Open mod manager" button under the Modding row, opening a custom popup we build.
- B2: Patch the existing modding-button click handler so it opens a unified screen showing mod-enable AND per-mod-config (more invasive, breaks vanilla flow).

Approach B1 is the cleanest: zero scene-instantiation work because we're not adding a tab; just adding one button to a row. The popup we open can be a fully custom `Control` we build ourselves with whatever rows we want, no requirement to match `NSettingsPanel` styling.

**Trade-off vs Approach A**:
- B1: easier, less risky, no tab-overflow concern. Loses the "INPUT tab + ours = 5 tabs" aesthetic.
- A: matches Surfinite's stated preference ("tab to the right of INPUT"). Higher implementation cost.

### Approach C: No in-game UI, JSON-only

Current state. Streamer edits `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json` between sessions. Pros: zero code, fast to ship. Cons: no live editing, no validation hints, no discoverability for new streamers. Acceptable for v1 if onboarding docs are clear; not acceptable long-term as the knobs list grows past ~5 entries.

### Recommendation (research-level; final pick is Surfinite's)

For v1: ship Approach C plus expand `slay_the_streamer_2.json` with the v1 knob subset. Build Approach B1 as v0.2 (low risk, immediate streamer UX win). Reserve Approach A for v0.3+ if the knob count outgrows a popup, or if Surfinite wants the polish for an eventual public release.

### The lack of an official "register a custom tab" API

Confirmed by reading [`MegaCrit.Sts2.Core.Modding.ModSettings`](../decompiled/sts2/MegaCrit/sts2/Core/Modding/ModSettings.cs) — vanilla's mod-settings class tracks only `mods_enabled: bool` and a per-mod enable/disable list. There is no per-mod-config persistence surface and no UI extension point. We are on our own for our own settings file location and schema (we use `OS.GetUserDataDir() + "slay_the_streamer_2.json"` already). MegaCrit may add an official surface later; we should not block on it.

## Part B: Tunable-knobs inventory

Status legend:
- 🟢 **current** — already a field in our `slay_the_streamer_2.json` and consumed by code.
- 🟡 **hardcoded** — exists in code as a literal/const today; would be trivial to surface as a setting once wired in.
- 🔵 **planned** — flagged in `notes/06/07/08` as a future setting (sometimes implicit, sometimes called out).
- 🟣 **ask** — Surfinite asked for it directly in conversation (this session or prior memory).

### B.1 — Chat / platform connection

| Knob | Status | Notes |
|---|---|---|
| `channel` (Twitch channel) | 🟢 current | Required. `ModSettings.cs:54` validates non-empty. |
| `username` (bot username) | 🟢 current | Required. Lowercased with warning if mixed-case. |
| `oauthToken` (bot OAuth) | 🟢 current | Required. Shape-checked for 30 lowercase-alphanumeric chars. |
| `youtubeChannelId` | 🔵 planned | From [notes/07 D6](07-youtube-chat-feasibility.md). YouTube chat now implemented in `src/Ti/Chat/YouTubeChat/` per glob; needs the settings wire-up. Optional/nullable. |
| `chatPlatformsEnabled` | 🔵 planned | List of `"twitch"` / `"youtube"` or similar. Per [notes/07 D8](07-youtube-chat-feasibility.md), YT can fail silently and not block Twitch — but explicit opt-in via a settings flag is cleaner than always-attempt-both. Could collapse into "presence of `youtubeChannelId` = YT enabled", which is what notes/07 D6 actually settled on. |
| Twitch send-window cap (20 msgs / 30s) | 🟡 hardcoded | `ModEntry.cs:111-112` — `sendCapacity: 20, sendWindow: TimeSpan.FromSeconds(30)`. Streamer-irrelevant under normal use, but a verified-bot account would have higher caps. Probably NOT a UI knob, but worth noting in JSON-advanced for the future Twitch verified-bot path (per [notes/06 v0.2+](06-followups-and-deferred.md)). |

### B.2 — Per-vote feature toggles (Surfinite's "every feature toggle-able")

These are the "should chat vote on this decision?" knobs. Each ships as `bool` with a sensible default (likely `true` for shipped features, `false` for unreleased ones).

| Knob | Status | Notes |
|---|---|---|
| `voteOnNeow` | 🔵 planned + 🟣 ask | Currently always on if `chatCanVote` (implicit). Toggling off lets the streamer pick Neow blessing solo while still chat-voting on later screens. |
| `voteOnCardReward` | 🔵 planned + 🟣 ask | The B.2.1 surface. Disabling makes our patches no-op silently and lets vanilla decide. |
| `voteOnAncients` (Pael / Tezcatara) | 🔵 planned | B.2.2 work. Same shape as Neow vote per [sts2_ancients](../../../Users/Surfinite/.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_ancients.md) — likely predicate-widening on `NeowBlessingVotePatch.IsNeowEvent`. Toggle would gate the predicate. |
| `voteOnMapRoute` | 🔵 planned + 🟣 ask | Surfinite's example: "streamer wants chat to pick the map route". Not yet implemented; map-route patch surface unknown. |
| `voteOnShop` | 🔵 planned | Buy what, remove what. Per [notes/06](06-followups-and-deferred.md): "Plan A non-goals → shop covered later". |
| `voteOnEvent` | 🔵 planned | Per [notes/06](06-followups-and-deferred.md) + Discord 2026-05-12 (a community member): original Tempus mod **deliberately did NOT chat-control events** ("for the best"). Another community member flagged Slippery Bridge / The Trial as bad-for-chat-control. **Default off** if shipped; only enable per streamer's explicit choice. |
| `voteOnSealedDeckDraft` | 🔵 planned | Per [notes/08](08-sealed-deck-custom-mode-investigation.md): vanilla `Draft` modifier emergently lets chat vote on all 10 picks via our existing patches. So this is more like "does the streamer ENABLE Draft modifier" than "does our mod do something" — but worth a settings line that documents the interaction. |
| `voteOnPotionUse` | 🔵 planned | Far future. Chat decides when to drink the potion. Not in any current spec. |
| `voteOnCombatTargeting` | 🔵 planned | Far future. Per-card-play target vote. Slow; only relevant for Whispering Earrings-style "chat plays for the streamer" mode. |
| `voteOnWhisperingEarringsFirstTurn` | 🔵 planned | Per [notes/06](06-followups-and-deferred.md): two variants — `whisperingEarringsMode: "voteEachCard"` (slow) or `"spam"` (community-proposed first-vote-wins). Pick one or expose both. |

### B.3 — Per-vote tuning (when a vote IS happening, how does it behave)

| Knob | Status | Notes |
|---|---|---|
| Vote-window duration | 🟡 hardcoded | Hardcoded `TimeSpan.FromSeconds(30)` for both Neow ([NeowBlessingVotePatch.cs:132](../src/Game/DecisionVotes/NeowBlessingVotePatch.cs#L132)) and Card Reward ([CardRewardVotePatch.cs:230](../src/Game/DecisionVotes/CardRewardVotePatch.cs#L230)). Worth a per-decision override? Or single global `voteDurationSeconds: 30`? Probably global for v1. |
| Vote-receipt policy: announce-on-open | 🟡 hardcoded | `VoteReceiptPolicy.Default` has `AnnounceOnOpen = true` ([VoteReceiptPolicy.cs:11](../src/Ti/Voting/VoteReceiptPolicy.cs#L11)). Disabling means chat sees no "Vote opened" message — only the periodic tally and close. |
| Vote-receipt policy: periodic-tally cadence | 🟡 hardcoded | `PeriodicTallyEvery = null` → adaptive `max(7s, duration/5)` per [VoteReceiptPolicy.cs:7-8](../src/Ti/Voting/VoteReceiptPolicy.cs#L7-L8). Could be `voteTallyCadenceSeconds: 7` (positive = fixed) or `0` (off). |
| Vote-receipt policy: announce-on-close | 🟡 hardcoded | `AnnounceOnClose = true`. Disabling means chat sees no "Chat picked X" message. |
| Vote nonce (`!NN` suffix) on/off | 🔵 planned | v4 spec Decision 11 (notes/06). Currently always on per implementation. Toggle would let streamer disable for old-school feel. Low priority. |
| Skip option in vote (#0 = skip) | 🟣 ask | Currently OFF (skip excluded from vote tally; streamer drives skip via parent-Proceed). Future: `#0 = skip`. **Surfinite explicitly asked for a toggle** between the two behaviours. Implementation: branch in vote-option-list construction + parse policy in `VoteSession`. |
| Sub-only / mod-only / VIP-only filter | 🔵 planned | [notes/06 v0.2+](06-followups-and-deferred.md): "where-clause in VoteCoordinator.Start consumers". Settings would be one of: `voterEligibility: "all" | "subs" | "mods" | "vips" | "subsAndMods"`. Per-platform variant: separate Twitch/YouTube filters? |
| Latest-wins vs first-vote-wins | 🟡 hardcoded | Currently always latest-wins (B.1 verified). Tempus's StS1 mod was first-vote-wins per the original-feature inventory; some streamers may prefer that. Low-priority toggle. |

### B.4 — Skip-budget mechanics

| Knob | Status | Notes |
|---|---|---|
| `cardSkipsPerAct` | 🟢 current | Already in JSON. `-1` = unlimited, `0` = strict (no skip), `1` (default) etc. |
| `cardSkipsCarryOverActs` | 🔵 planned | Currently resets per act. A "bank-up unused skips" toggle would let strict-mode streamers accumulate. Not in any spec; speculative. |
| `cardSkipsResetReceiptOnRunStart` | 🔵 planned | Per [notes/06](06-followups-and-deferred.md) reset-receipt timing item: "consider suppressing when RunChanged AND humanAct == 1". Could be `suppressTrivialResetReceipts: true` (default). |
| Closed-via-back-arrow refund | 🔵 planned | Per [notes/06 vanilla map-screen go-back-arrow item](06-followups-and-deferred.md): refund-on-claim option for the map-screen back-arrow case. Either policy is configurable, or it's just a fix to ship without a toggle. |
| Counter label visibility | 🔵 planned | `CardSkipCounterLabel` placement is sub-optimal per [notes/06](06-followups-and-deferred.md). Could expose `showCardSkipCounter: true` toggle if streamer finds it distracting. |

### B.5 — Sealed-deck / draft / Neow ordering

| Knob | Status | Notes |
|---|---|---|
| `sealedDeckFlavour: "sealed" \| "draft" \| "off"` | 🔵 planned | Per [notes/08](08-sealed-deck-custom-mode-investigation.md), three vanilla-reachable shapes. The setting maps to "tick SealedDeck modifier" / "tick Draft modifier" / "do nothing" guidance — could even auto-tick the right vanilla modifier for the streamer if we want to be opinionated. |
| `neowVoteBeforeDraft: bool` | 🟣 ask | Surfinite's example #2: when sealed-deck is active, currently no Neow vote happens (vanilla replaces Neow with the draft kickoff). The polish item from [notes/08](08-sealed-deck-custom-mode-investigation.md) is to re-add a Neow vote before the draft. Setting toggles whether to inject the Neow vote or not. |
| `perCharacterMustIncludes` | 🔵 planned | Per [notes/06 sealed-deck v0.2+](06-followups-and-deferred.md): "configurable per-character must-include list (simple comma-separated string in settings JSON would suffice for v1)". Default empty. JSON shape: `{ "necrobinder": ["Bodyguard", "Unleash"], "defect": ["Zap", "Dualcast"] }`. Tied to the Q7 finding in notes/08 (Basic cards never appear in vanilla draft). Surfinite downgraded from load-bearing to "polish, may never happen". |

### B.6 — Onboarding / save-state workarounds

These are the "modded save profile starts progress-empty" gap items. Probably need a unified onboarding section in settings; each is a `bool`.

| Knob | Status | Notes |
|---|---|---|
| `forceFirstRunNeow: true` | 🔵 planned | Per [notes/06 B.1 follow-ups](06-followups-and-deferred.md). Force Neow first-run popup behaviour for unlocks. |
| `copySaveFromUnmodded: true` | 🔵 planned | Per [notes/06 B.1 follow-ups](06-followups-and-deferred.md). Lift the unmodded save into the modded profile. More invasive. |
| `unlockAllForMod: true` | 🔵 planned | Per [notes/08 sts2_custom_mode_unlock memory](../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_custom_mode_unlock.md). Unlock everything (cards, characters, Custom Mode) on the modded profile. Default likely on; lets a fresh-modded streamer reach Sealed Deck immediately. |
| `skipIntroLogo` | (vanilla) | Already a vanilla setting. Not ours. Listed for completeness only. |

### B.7 — UI / visual polish

| Knob | Status | Notes |
|---|---|---|
| Vote-tally label placement | 🟡 hardcoded | Per [notes/06](06-followups-and-deferred.md) — current placement is sub-optimal for viewers. Could expose `tallyLabelAnchor: "topRight" \| "bottomCenter" \| "custom"` with optional `x,y` overrides. Niche; only worth surfacing if streamers ask. |
| Vote-tally label font size | 🟡 hardcoded | Same source. Probably one setting `tallyLabelScale: 1.0` (multiplier) covers most needs. |
| Card-skip counter label placement/size | 🟡 hardcoded | Per [notes/06](06-followups-and-deferred.md) — placement at 0.62-0.98 × 0.74-0.82 currently. Same shape as tally label setting. |
| Show in-game per-platform tally split | 🔵 planned | Per [notes/07 D10](07-youtube-chat-feasibility.md): "in-game vote-tally label MUST show separate per-platform tallies when YT is enabled". Implementation-level requirement, but worth a `splitTallyByPlatform: bool` toggle for streamers who'd rather see a merged tally. |
| Show achievement-lock icon in modded runs | (vanilla) | Vanilla shows the lock icon when `AreAchievementsAndEpochsLocked()` per [notes/08](08-sealed-deck-custom-mode-investigation.md). Not ours; informational. |

### B.8 — Logging / diagnostics

| Knob | Status | Notes |
|---|---|---|
| `verboseChatLogging: bool` | 🔵 planned | Per [notes/06](06-followups-and-deferred.md): `TwitchIrcChatService.TransitionTo` silent-on-state-change is a gap; adding logging is the fix. Toggle would let streamers turn off the extra verbosity if it spams `godot.log`. Probably ship verbose-by-default and provide an off-toggle. |
| `voteReceiptLogLevel` | 🟡 hardcoded | Probably never a streamer-facing knob. |
| Twitch ratelimit-burst mitigations | 🔵 planned | Per [notes/06](06-followups-and-deferred.md): cancellation receipts dropped under 20/30s cap. Settings options could include `suppressTrivialReceiptsNearClose: true`, or specific receipt-priority overrides. Niche; ship as code-only knobs for now. |

### B.9 — Locale / receipts

| Knob | Status | Notes |
|---|---|---|
| `chatReceiptLanguage: "english"` | 🔵 planned | Per [notes/06 v0.2+](06-followups-and-deferred.md): "Add peer static helpers (`SpanishReceipts.cs` etc.) + `Func<VoteSnapshot, ReceiptKind, string>`". Settings line that picks which language pack to use. v1: english-only, no setting. |
| Override receipt wording | 🔵 planned | "Streamer-configurable receipt policy" per [notes/06 spec-level open items](06-followups-and-deferred.md). String-templates in JSON. Power-user; not v1. |

## Knobs that should NOT be in settings

Resist the temptation to expose:

- **`schemaVersion`** — already there, but should never be user-edited (it's how we detect breaking config changes between mod versions). Leave readable, treat any non-current value as Malformed (current behavior).
- **N/M for SealedDeck** (10-of-30) — hardcoded in vanilla per [notes/08](08-sealed-deck-custom-mode-investigation.md); changing them requires Harmony transpiler. Surfinite confirmed keep as-is.
- **Internal timing constants** — `MaxVoters = 10_000`, `MaxRetryDuration`, etc. These exist to prevent runaway state; not streamer-tunable.
- **VoteSession internals** — `voteId` allocation, nonce digit count, dedup window. Implementation details.
- **Patch enable/disable individually** — if a streamer wants to disable a feature, they should use the feature-level toggle (B.2), not a "disable patch X" knob.

## Suggested v1 shortlist (research-level recommendation)

Surfinite picks the final list; this is a shape-suggestion. The principle: ship the toggles that change WHO drives gameplay (chat vs streamer), not the toggles that change HOW chat does it. Tuning-the-tuning is for v0.2+.

Tier 1 — v1 JSON-only:
- All current 🟢 fields (no change).
- `voteOnNeow: true`
- `voteOnCardReward: true`
- `cardRewardSkipAsVoteOption: false` (matches current behaviour; toggle to `true` enables `#0 = skip` future shape per Surfinite's example #1)
- `unlockAllForMod: true` (default; lets fresh-modded streamers reach Custom Mode for sealed-deck experiments)

Tier 2 — v1 only if cheap, otherwise v0.2:
- `youtubeChannelId: null` (defers full YT settings work but reserves the field)
- `voteDurationSeconds: 30` (single global; per-decision overrides not yet)

Tier 3 — v0.2 or later:
- Everything else in B.2 (per-feature toggles for map / shop / event / ancients / etc.)
- Sealed-deck flavour, Neow-before-draft, per-character must-includes
- Voter-eligibility filters (sub-only etc.)
- UI placement knobs
- Logging verbosity, receipt language

The total Tier 1 surface is ~5 new fields — small enough to operator-validate without a combinatoric explosion. Tier 2 adds 2. Beyond that, each toggle is a 2x test-matrix multiplier, so Tier 3 needs deliberate per-feature operator validation rather than batched.

## Open questions

1. **Where should `slay_the_streamer_2.json` live going forward?** Currently in `OS.GetUserDataDir()` (i.e., `%APPDATA%\SlayTheSpire2\`). If we ever ship to a public audience, should we move to a per-mod subdirectory? Decision can be deferred; not v1.
2. **Should knobs be per-streamer (system-wide) or per-stream (per-run)?** All current knobs are session-wide. A per-stream override (e.g., "tonight's stream, disable card-reward voting") would need either a CLI/console flag or a per-stream JSON layer. Probably overkill; flag for later if streamers ask.
3. **Should the settings file support comments?** Standard JSON doesn't. JSON5 / JSONC would. Adds parser complexity. Probably skip; document via a separate README or `_comment` field convention.
4. **Hot-reload of settings during a run?** Currently we read once at mod init. Some knobs are cheap to hot-reload (vote duration, receipt policy); others would require teardown (chat platform, OAuth). Probably skip for v1; flag for v0.2+ if a streamer wants to tweak between rooms.
5. **Validation feedback channel.** Settings load currently returns `Malformed` and logs at Error/Warn. With a settings UI, we'd want toast-style in-game feedback. Already partially built — `NSettingsToast` exists ([NSettingsScreen.cs:86](../decompiled/sts2/MegaCrit/sts2/Core/Nodes/Screens/Settings/NSettingsScreen.cs#L86)) — but Approach A/B1 would need to hook it.
6. **`voteOnNeow: false` + sealed-deck-mode interaction**: if neither Neow vote nor sealed-deck draft fires, the streamer picks Neow solo (current vanilla). If only sealed-deck fires, streamer drafts solo. If only Neow vote fires, chat picks Neow + streamer plays standard run. The cell matrix is 4 combinations — each needs a smoke test. Document the matrix; do not let it ship un-tested.

## Cross-references

- [notes/06-followups-and-deferred.md](06-followups-and-deferred.md) — primary source for planned-but-not-implemented feature toggles and polish items.
- [notes/07-youtube-chat-feasibility.md](07-youtube-chat-feasibility.md) — YouTube-specific settings (`youtubeChannelId`, periodic-retry cadence, etc.).
- [notes/08-sealed-deck-custom-mode-investigation.md](08-sealed-deck-custom-mode-investigation.md) — sealed-deck-specific settings (flavour, Neow ordering, must-includes, unlock-everything).
- [`src/Game/Bootstrap/ModSettings.cs`](../src/Game/Bootstrap/ModSettings.cs) — current schema. Adding fields requires bumping `CurrentSchemaVersion` if backwards-incompatible (additive optional fields don't need a bump per current convention).
- Memory entry [`sts2_custom_mode_unlock`](../../.claude/projects/c--Users-Surfinite-slay-the-streamer-2/memory/sts2_custom_mode_unlock.md) — Custom Mode 3-win unlock gate; informs the `unlockAllForMod` knob.
