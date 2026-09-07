# remove-one/ + sealed-neow/ : chat removes an option, unskippable rewards, per-origin rules, sealed-deck Neow tweaks (design)

Date: 2026-09-07. Status: approved design (brainstorm rulings by Surfinite 2026-09-07), awaiting
the implementation plan. Slice prefixes: `remove-one/N:` (sections 2 to 6) and `sealed-neow/N:`
(section 7); one spec because they share the loc seam and the relic-origin tags. On screen the
verb is always "remove", never "strike". No em dashes in this document or in any text it ships.

Port source: SabotageTheStreamer `strike/` (spec `2026-09-04-strike-removal-rules-design.md`,
rig matrix `notes/66-strike-matrix.md`, rig-green 2026-09-07, mod 0.0.46 to 0.0.48) and the
unmerged `neow2` branch (spec `2026-09-05-neow2-neow-relics-design.md`, 15 commits). Both live
at `C:\Users\Surfinite\SabotageTheStreamer`. This repo's landscape research is
`notes/13-card-choice-surfaces-inventory.md`. Decompile references are the fresh
`decompiled/sts2-v0.111.0/` tree.

## 0. Rulings taken in the brainstorm (Surfinite, 2026-09-07)

1. **Beta-only.** The combat tag (`Hook.BeforeCombatRewardOffered`) does not exist on the
   game's default branch. When it fails to register, the whole classifier stands down to
   today's behaviour (every card reward is a normal pick vote) with a one-time Warn.
2. **Click-to-start.** The removal vote starts on the streamer's first click on the screen
   (a card or Skip); nothing is taken by that click. Keeps the "streamer chooses when the
   countdown starts" pattern chat and streamers already know.
3. **Override on the removed option.** After the removal lands, clicking the removed option
   spends one vote override and takes it. At override budget 0 the option is unclickable.
   A click during the countdown is a normal vote override (ends the vote, takes that card).
4. **Settings.** The existing `combatCardVotesOnly` checkbox: **On** keeps today's behaviour
   (non-combat card rewards are streamer-free). **Off** applies the new per-origin rules and
   becomes the default again (it was flipped On in v0.3.0 because nothing explained what
   happened at non-combat rewards; the appended text now does).
5. **Accent lead** for every appended explanation: `Slay the Streamer:` in cornflower blue
   `#668CFF`, on its own line (the Sabotage `strike/18.1` and `18.6` rulings carried over).
6. **Relic text coverage:** a built-in relic-ID list (vanilla plus Strongbox from Haxxero's
   More Relics) plus a runtime learner for other mods' relics.
7. **Sealed-deck Neow tweaks** (Talisman rework with the Doomed enchantment; Leafy Poultice
   and Precarious Shears disabled) apply only when the Sealed Deck modifier is in the run.
   Tristan plays with Pikcube's Always Whale, which brings Neow's blessings back to a sealed
   run, so these are live for him. Neow's Bargain and Neow's Tithe are NOT ported.
8. Doomed uses the Doom power's tombstone icon on the card badge and the Doom power tooltip.
9. Massive Scroll is multiplayer-only (`IsAllowed` requires `Players.Count > 1`) and every
   vote patch bails in multiplayer, so it is out of the surface list by construction.

## 1. Goals and non-goals

Goals:
- A "chat removes one option" vote on every Ancient-relic card pick (both screen families)
  and on Dream Catcher, riding the existing `VoteSession` / `VoteCoordinator` machinery.
- Event and shop-relic card rewards unskippable by rule (input restraint only).
- One classifier, `RewardAuthority.Classify`, as the single decision point for every
  `CardReward`, replacing the scattered `CombatOriginTags.ShouldVoteOn` reads.
- Explanation text on every affected relic (hover and Ancient option button), event option,
  rest-site option and screen, word for word from the rig-tested Sabotage catalogue with chat
  phrasing.
- Third-party relics handled structurally, with learned text.
- Sealed-deck Neow: Talisman rework and two relic disables.

Non-goals: Scroll Boxes (bundle screen, 2 options, no Skip); Sea Glass and every multi-pick
grid; mid-combat pickers (potions, Discovery-class cards, Toolbox, Choices Paradox, Knowledge
Demon, Balls2's Dragon Balls, StS1 Boss Ancients' Summon Shape); deck manipulation; Brain
Leech "Share Knowledge" (already uncancelable); the Sabotage map travel hold (our
`TopBarMapButtonGuardPatch` already blocks Map while a rewards screen is mounted); a confirm
popup on override spends; Neow's Bargain and Tithe; any change to the Ancient, boss or
act-variant votes.

## 2. The rules table and the classifier

`RewardAuthority.Classify(CardReward) -> AuthorityMode` where
`AuthorityMode = NormalVote | RemoveOne | Unskippable | Free` (Free = the checkbox-On streamer-free path and any bail). The pure rules live in
`src/Game/DecisionVotes/AuthorityRules.cs` (BCL only, unit-tested; takes a `RewardOrigin`
record of booleans plus the two switches); the game-facing `RewardAuthority` fills the record
from the tag tables. Evaluation order:

1. Combat tag patch not registered (default branch) => NormalVote for everything.
2. `combatCardVotesOnly == true` => combat-tagged NormalVote, everything else Free (today).
3. `CombatOriginTags.IsTagged` => NormalVote.
4. `RelicOriginTags` hit: relic `Rarity == Ancient` => RemoveOne; any other rarity (Shop:
   Orrery, Strongbox) => Unskippable.
5. `RestSiteOriginTags` hit (Dream Catcher, incl. Dense Vegetation's mimicked rest) => RemoveOne.
6. Otherwise => Unskippable (the event bucket: Future of Potions, Colorful Philosophers x3,
   Brain Leech Rip, Trial Guilty x2, Crystal Sphere tiles).
7. Draft modifier picks are untagged and therefore land in rule 6. That is a no-op in
   practice: Draft already sets `CanSkip = false`, so there is no Skip button to hide and no
   Skip alternative to deny, and no text key is attached to Draft. No special case is needed
   (and none is safe: a "Draft run => Free" rule would also free every event reward later in
   that run).

| Origin | Detected by | Mode | Chat input | Skip |
|---|---|---|---|---|
| Combat: Monster/Elite/Boss, ? fights, event fights, tutorial, Prayer Wheel, White Star, The Hunt, Pokéball, Colosseum Ticket, Glorious Crown | `CombatOriginTags` | NormalVote (unchanged) | picks one option | as today |
| Ancient relic: Kaleidoscope x2, Glass Eye x5, Lost Coffer x1, Neow's Bones nested, any mod relic of Ancient rarity | `RelicOriginTags`, Ancient | RemoveOne | removes one option, Skip included | vanilla minus the removed option |
| Dream Catcher (rest site; Dense Vegetation) | `RestSiteOriginTags` | RemoveOne | same | same |
| Events (five listed above) | untagged non-combat | Unskippable | none | hidden, ESC denied, "Skip Rewards" disabled |
| Shop relic: Orrery x5 incl. Lord's Parasol auto-buy, Strongbox x2 | `RelicOriginTags`, non-Ancient | Unskippable | none | same |
| Draft modifier | untagged (rule 6) | Unskippable, a no-op | none | already `CanSkip = false` in vanilla |

Hefty Tablet and Lead Paperweight are the `CardSelectCmd` screen family and get RemoveOne
through section 4, not this table.

Tags:
- `RelicOriginTags` (new): `ConditionalWeakTable<CardReward, RelicModel>`. A prefix with
  `Priority.High` plus a `[HarmonyFinalizer]` on `RelicCmd.Obtain(RelicModel, Player, int)`
  push and pop the relic on a stack, with `__state` carrying "this call pushed" so the pop is
  exactly as conditional as the push. Finalizer, not postfix: `relic.AssertMutable()` at
  `RelicCmd.cs:23` is a real throw path. A new postfix on both `CardReward` constructors tags
  any reward born while the stack is non-empty. Orrery, Glass Eye, Kaleidoscope, Lost Coffer
  and Strongbox all construct their rewards before their first await inside `AfterObtained`,
  so the stub's finalizer fires after construction; nested obtains (Neow's Bones then
  Kaleidoscope) push inside a completed pop.
- `RestSiteOriginTags` (new): postfix on the static
  `Hook.ModifyRestSiteHealRewards(runState, player, rewards, isMimicked)` (Hook.cs:1571,
  sole caller `HealRestSiteOption.ExecuteRestSiteHeal`) tagging every `CardReward` in the
  list. `CombatOriginTags` shape (Prepare hard-checks the target; a miss makes Dream Catcher
  an unskippable event reward: fail-safe but wrong, so it logs an Error).
- `CombatOriginTags`: unchanged. Its `TagPatchRegistered` flag is what rule 1 reads.

Every current `CombatOriginTags.ShouldVoteOn` / `ShouldVoteOnActiveReward` call site (the vote
prefix, the skip-gate counting, streamer-Skip budget, the Skip-alt flip, the counter label)
switches to `RewardAuthority.Classify` / `RewardAuthority.ModeOfActiveReward()`:
- NormalVote: today's code paths, untouched.
- RemoveOne: section 3. Not gateable by the skip gate (no mandatory-look, no budget charge,
  no counter label), Skip alt NOT flipped (vanilla keep-claimable semantics).
- Unskippable: section 5. Not gateable by the skip budget; the Proceed gate blocks while one
  is unresolved (section 5, point 3).
- Free: fully vanilla, as today's out-of-scope path.

### 2.1 Third-party relics

The classifier is structural, so mod relics need no per-mod code. Verified against the three
mods installed on Surfinite's machine (all BaseLib):
- **Balls2** (dandylion1740, Workshop 3749536304): Pokéball adds a `CardReward` inside
  `TryModifyRewards` for combat rooms (combat-tagged, NormalVote); Dragon Balls opens a
  choose-a-card screen mid-combat (excluded); Ball Sack offers relic rewards only. Its Donu
  ancient (`CustomAncientModel`) already gets our Ancient vote.
- **StS1 Boss Ancients** (2D20, Workshop 3748223713 / 3769771107): nine `CustomAncientModel`
  ancients incl. Donu/Deca (Ancient vote fires); Colosseum Ticket and Glorious Crown add
  combat card rewards (NormalVote); Fusion Device opens a pick-3 grid on obtain (excluded);
  Summon Shape is a combat card (excluded); Big Fish event offers a relic reward.
- **Haxxero's More Relics** (Workshop 3783938307): Strongbox (Shop rarity) offers a Rare and
  an Uncommon card reward on pickup => Unskippable, like Orrery. Nothing else touches cards.

Shapes seen across other Workshop relic packs and how they land: extra reward added at combat
end (combat-tagged); rewards offered directly on pickup (relic-tagged, classified by rarity);
direct choose-a-card screen on pickup (section 4); option-list mutation (no new surface); a
CLONED existing `CardReward` added to the live screen (Hades Ancients' Sea Star: the clone
carries no tag and lands in the unskippable event bucket; rare, documented, not special-cased).
The Downfall port also patches `CardSelectCmd.FromChooseACardScreen`; compat check owed if
Tristan ever runs it.

## 3. The RemoveOne vote on the card-reward screen

Trigger (ruling 2). In `CardRewardVotePatch.Prefix` (on `SelectCard`) and the
`OnAlternateRewardSelected` prefix, after the `_resumeInProgress` and `_voteInProgress`
branches and before every bail-to-vanilla gate (the prefix-ordering landmine), when the
active reward classifies RemoveOne:
- No removal recorded for this reward yet, no vote open => start the removal vote and return
  false (the click is consumed; nothing is taken). Same MP / chat-readability / single-option
  bails as the pick vote apply first; if any bail fires the reward behaves as Free for this
  screen (streamer picks freely, Info log).
- Removal already recorded => section 3.2 click rules.
- Vote open => existing suppress / override branch (a card click ends the vote with that card
  taken and spends an override; Skip click likewise via `TryOverrideWithSkip`; the removal
  is not recorded).

Before the first click the screen shows a status line under the banner:
"Click any option to start the removal vote." (new line; the only text in this slice that
has not been rig-tested in Sabotage, because the Sabo's screen has no click-to-start).

Options. Vote labels are the card titles in holder order plus, when the reward has a Skip
alternative, `Skip` as the LAST index (unlike the pick vote, where Skip is #0 when
`cardSkipAsVoteOption` is on; here Skip is always present when the reward allows it,
independent of that setting, because Skip is a removable option and not a chat pick).
`RemoveVoteOptionLabels.Build(cardTitles, hasSkip)` / `ResolveRemovedIndex` are pure and
unit-tested. A reward with fewer than 2 removable options (1 card, no Skip) is degenerate:
no vote, streamer picks (Info log).

Session. `coordinator.Start("Remove an option", labels, voteDuration, showTag,
formatReceipt: RemoveVoteReceipts.Format)`. Receipts (chat):
- open: `Vote: remove one option from {streamer}'s card reward! Type 0-N. 30s left.`
  (the existing open template with the label substituted).
- periodic tally: unchanged.
- close: `Chat removed {i}: {label}. {streamer} picks from the rest.` Tie / no-vote variants
  keep the existing wording with "removed" for "chose".
- override on the removed option (section 3.2): `{streamer} overrode chat's removal and took
  {label}. N overrides remaining this act` (through `VoteOverrideBudget.SendOverrideReceipt`
  with a "removal" flavour; Cursed Overrides applies as for any override).
- reroll (section 3.3): a fresh open receipt.

Popup. `CardRewardVotePopup` gains a mode: title "Chat is choosing which option to remove",
`#N` labels beside each card and Skip as in the pick vote, and on close the winner's holder
(or the Skip button) tweens to solid red `(1, 0.28, 0.28)` over 0.35 s with no pulse
(`RemovalVisuals.PaintRemoved`, ported). The corner tally label attaches as for any vote.

### 3.1 Removal record

`RemovalRecords`: `ConditionalWeakTable<CardReward, RemovalRecord(int RemovedIndex, bool
IsSkip, CardCreationResult[] OptionsSignature)>`. Written on the main thread in the resume
step; read by the click rules, the screen `_Ready` postfix (re-paint on re-open) and the
status line. Keyed on the reward, not the screen, so ESC-and-reopen, back-out-and-reopen and
the parent rewards screen's re-click all show the same red option without re-voting. The
signature makes a rerolled reward (options changed) count as "no removal yet".

Resume (chat-parser thread => `dispatcher.Post`): the usual liveness checks
(`IsRunLiveForResume`, options signature) then: record the removal, paint red, retitle the
status line (3.2), release the input lock. There is no `SelectCard` re-invoke; the streamer's
next click is the pick. Cancellation (run death, disconnect) => no record, vanilla screen.

### 3.2 Click rules after the removal

Pure `RemovalClickRules.Judge(clickedIndex, removedIndex, rerollIndex, overridesRemaining)`
(ported from Sabotage's `RemovalRules`; unit-tested):
- `clicked == rerollIndex` => Allow, free.
- `clicked != removed` => Allow (vanilla proceeds).
- `clicked == removed` => `overridesRemaining > 0 ? AllowWithOverride : Deny`.
AllowWithOverride: `VoteOverrideBudget.RecordUse()`, curse roll, receipt, then vanilla
proceeds with that click. Deny: return false, Debug log. At budget 0 the removed holder gets
`SetClickable(false)` (hover and inspect stay live) and a removed Skip button gets
`Disable()`; when budget becomes available again mid-screen (it cannot; budgets reset per act)
nothing needs re-enabling.

Status line (Kreon, same theme as the vote title; strings from Sabotage `strike/18.7`):
- removed a card, budget > 0: `Chat removed {CardModel.Title}. Choose from the rest, or spend
  an override to take it.`
- removed a card, budget 0: `Chat removed {CardModel.Title}. Choose from the rest.`
- removed Skip, budget > 0: `Chat removed Skip. Take a card, or spend an override to skip.`
- removed Skip, budget 0: `Chat removed Skip. You must take a card.`
- with Driftwood's Reroll on screen, append `\nYou can reroll these cards once, for free.`

Skip semantics on RemoveOne rewards are vanilla (`EndSelectionAndDoNotCompleteReward`, reward
stays claimable, ESC hotkey live): the Skip-alt flip in `CardRewardAlternative_Generate_Postfix`
is gated to NormalVote only. Skipping a RemoveOne reward never charges the card-skip budget
and never blocks Proceed.

### 3.3 Driftwood

`CardReward.Reroll()` is blocked while a vote is open (existing prefix, unchanged) and
allowed otherwise. After a removal, Reroll is free: the reroll rebuilds `_options`, the
removal record's signature no longer matches, so the reward is "no removal yet" again and the
streamer's next click starts a fresh removal vote on the new cards (Sabotage's "chat then
votes again"). Reroll before any vote is also allowed (vanilla), and simply means the first
vote runs on the rerolled cards. Orobas is Driftwood's sole source.

## 4. The choose-a-card family (Hefty Tablet, Lead Paperweight)

`CardSelectCmd.FromChooseACardScreen(context, cards, player, canSkip)` (CardSelectCmd.cs:194)
is the sole opener of `NChooseACardSelectionScreen`; `ShowScreen(cards, canSkip)` (:148) is
static, returns the instance, and assigns `_cards` BY REFERENCE (watchlist item).

`ChooseACardRemovePatch` (new, `src/Game/DecisionVotes/`):
- Prefix on `FromChooseACardScreen`: when `!CombatManager.Instance.IsInProgress`, single
  player, and `cards.Count + (canSkip ? 1 : 0) >= 3`, open a `ChooseACardContext(cards,
  canSkip)` (static, main thread; cleared on `_ExitTree` of the bound screen and on run
  cleanup). Never returns false; catch + log.
- Postfix on `ShowScreen`: bind the returned screen when its `_cards` reference-equals the
  context's list (combat pickers never match).
- Prefixes on `SelectHolder(NCardHolder)` and `OnSkipButtonReleased(NButton)` (private,
  string targets; keyboard confirm funnels through the same handlers): inside the vanilla
  350 ms open debounce (`Time.GetTicksMsec() - _openedTicks <= 350`) return true so vanilla
  drops the click itself (a click consumed by us in that window would look like a dead
  screen). Otherwise the same state machine as section 3: first click starts the vote (Skip
  index = `cards.Count`), clicks during the vote are suppressed or override, clicks after the
  removal run `RemovalClickRules`.
- Resume applies the record to the bound screen and paints; the removal record is keyed on
  the context (there is no `CardReward`), so a screen that closes and re-opens for the same
  relic obtain cannot happen (the command awaits one screen); the record dies with the context.
- The vote popup anchors to the screen's `_cardRow` holders and `_skipButton`; the popup's
  anchor discovery is generalised to take a holder list plus an optional Skip control, so the
  same class serves both screen families.

Edges: Lead Paperweight after a Skip removal leaves two cards (smallest legal case). Hefty
Tablet's Injury is added on Skip as in vanilla. Neow's Bones can nest an eligible relic:
the Ancient vote on the Neow page resolves before the option replay, so the two overlays
never coexist. Massive Scroll never fires in single player (ruling 9).

## 5. Unskippable

Input restraint only, on `Classify == Unskippable`, never a structural change to the
alternatives list (`Generate` order is a shared contract; keep the list intact):
1. The existing `OnAlternateRewardSelected` prefix returns false for the alternative with
   `OptionId == "Skip"` (covers the click and the ESC / back hotkeys, which both funnel
   through it). Logged once per screen: `[unskip] Skip denied on an unskippable card reward`.
2. The existing `NCardRewardSelectionScreen._Ready` postfix hides and `Disable()`s the Skip
   `NCardRewardAlternativeButton` (unregisters its hotkeys), retitles the banner
   "Choose a Card (no skip)" via the banner label's `SetTextAutoSize`, and attaches the
   status line "This card reward cannot be skipped." (no counter).
3. New `NRewardsScreen._Ready` PREFIX (our existing postfix stays): read `_rewardsSet` and,
   if any `CardReward` classifies Unskippable, call the public `set.WithSkippingDisallowed()`
   so vanilla's own branch disables the "Skip Rewards" button, and add the header sub-label
   "Every card reward must be taken here." ABOVE the "Loot!" banner in the vote-title theme,
   positioned per frame from the header (never `GetGlobalRect` in `_Ready`). Our
   `OnProceedButtonPressed` prefix additionally blocks while an Unskippable card reward button
   is still alive (belt and braces; vanilla's disabled button is the visible signal).
4. Which reward a screen belongs to: `CombatOriginTags.ActiveRewardCapturePatch`
   (`CardReward.OnSelect` prefix) already records it; `RewardAuthority.ModeOfActiveReward()`
   reads it.
Reroll stays legal on an unskippable reward (a reroll is not a skip); after a Driftwood
reroll the rebuilt Skip button may LOOK live (Sabotage row 31) but the deny in point 1 holds.
Orrery and Strongbox get the same three points; Lord's Parasol's zero-click auto-buy too.
Every patch fails open to vanilla-skippable.

## 6. Text

### 6.1 Seam

A postfix on `LocTable.GetRawText(string key)` (public, both branches). `LocString
.GetFormattedText()` -> `LocManager.SmartFormat` -> `LocString.GetRawText()` ->
`LocTable.GetRawText(key)`, so every render of every loc string passes here. The postfix
reads the table's private `_name` (`___name` injection), looks up `(table, key)` in the
catalogue, and appends the suffix when the text does not already end with it (the fallback
table recursion calls the method twice for English-fallback keys; the marker check makes the
second call a no-op). Why read-time rather than Sabotage's load-time rewrite: BaseLib injects
mod relic strings straight into the table's dictionary after load (`ModelLocPatch`, a
`ModelDb.Init` postfix), so a `LoadTable` postfix never sees them; read-time also survives
language switches with no captured-base bookkeeping. Cost: one dictionary lookup per raw-text
read on a small set. The appended text never contains bare `{` `}` (SmartFormat runs after
us) and may use vanilla bbcode tags.

Sealed-deck Talisman text (section 7) is a REPLACEMENT, not an append, gated on the run
being sealed; the same postfix serves it through a second "replace" map.

### 6.2 Catalogue (`AuthorityLoc`, `src/Game/DecisionVotes/`, BCL only, unit-tested)

`Lead = "\n[color=#668CFF]Slay the Streamer:[/color] "`. Vanilla keyword markup is repeated
where a keyword is mentioned (`[red]Injury[/red]`, `[gold]Rest[/gold]`). Relic ids expand to
`.description` always and `.eventDescription` when that key exists (the Ancient option button
reads `.eventDescription ?? .description`, so one append covers hover, compendium and button).

Relics:
- KALEIDOSCOPE: `for each card reward, chat votes to remove one option, Skip included, and you pick from the rest.`
- GLASS_EYE: `chat votes to remove one option on each of the five card rewards. You pick from the rest.`
- LOST_COFFER: `chat votes to remove one option from the card reward.`
- HEFTY_TABLET: `chat votes to remove one of the four options, Skip included. [red]Injury[/red] is added either way.`
- LEAD_PAPERWEIGHT: `chat votes to remove one of the three options, Skip included.`
- NEOWS_BONES: `card rewards from relics pulled here use the removal vote.`
- DREAM_CATCHER: `chat votes to remove one of the card-reward options each time you [gold]Rest[/gold].`
- DRIFTWOOD: `after chat removes an option from a card reward, you may reroll once for free. Chat then votes again on the new cards.`
- ORRERY: `you must take a card from each of the five rewards.`
- STRONGBOX (More Relics; appended only if the key exists): `you must take a card from each of the two rewards.`
- `DREAM_CATCHER.additionalRestSiteHealText` (relics table, explicit key): `chat removes one of the options first.`

Events:
- `TRASH_HEAP.pages.INITIAL.options.DIVE_IN.description`: `if Dream Catcher is found here, chat will vote to remove one of your card-reward options when you [gold]Rest[/gold].`
- `CRYSTAL_SPHERE.pages.INITIAL.options.{UNCOVER_FUTURE,PAYMENT_PLAN}.description`: `you will not have the option to skip cards uncovered here.`
- `CRYSTAL_SPHERE.minigame.instructions.description`: the same line preceded by one extra blank line (the instructions panel's paragraph rhythm).
- `COLORFUL_PHILOSOPHERS.pages.INITIAL.options.{NECROBINDER,IRONCLAD,REGENT,SILENT,DEFECT}.description`, `THE_FUTURE_OF_POTIONS.pages.INITIAL.options.POTION.description`, `BRAIN_LEECH.pages.INITIAL.options.RIP.description`, `TRIAL.pages.NONDESCRIPT.options.GUILTY.description`: `the card rewards cannot be skipped.`

Generic lines for learned relics (6.3): Ancient rarity `chat votes to remove one option from
this relic's card rewards.`; other rarities `card rewards from this relic cannot be skipped.`

Screens (code-side): "Click any option to start the removal vote."; "Chat is choosing which
option to remove"; the four removed-state lines and the reroll note (3.2); "Choose a Card (no
skip)"; "This card reward cannot be skipped."; "Every card reward must be taken here."

The catalogue tests assert: no em dash anywhere, no "strike", every vanilla key present in
the shipped `relics` / `events` tables (a fixture copied from the decompiled assets), the
append is idempotent under a double call, and the Lead is the first thing after the newline.

The catalogue is gated on `combatCardVotesOnly == false` and the combat tag being registered:
when the rules do not apply, the text does not show (a read-time seam makes this a live check).

### 6.3 Relic-ID registry and learner

`RelicTextRegistry`: the built-in set above plus a learned set persisted at
`%APPDATA%\SlayTheSpire2\slay_the_streamer_2.learned-relics.json` (`[{ "id": "...",
"mode": "RemoveOne" | "Unskippable" }]`). A relic is learned the first time `RelicOriginTags`
tags a reward it produced, or a `ChooseACardContext` opens while it is the relic on the
obtain stack, and its id is not built-in. Written on the main thread, best effort, one Warn
on failure; read at boot. Learned relics get the generic line from the next launch on (the
loc seam is read-time, so in practice from the next hover). The registry is what
`AuthorityLoc` consults for relic keys.

### 6.4 Event option button growth

Port `EventOptionGrowPatch` (postfix on `NEventOptionButton._Ready`): an option whose `%Text`
carries the Lead is measured at the vanilla 24 px font, and the overflow is added to the
button's minimum height, the label box, and the centred Shadow / Outline / RedFlash /
BlueFlash nine-patches, so the frame follows and the font no longer auto-shrinks. Both the
regular and the Ancient button layouts are handled; node lookups are null-tolerant. Our
`AncientVotePopup` highlights options by their button rect, so a grown button is covered.

## 7. Sealed-deck Neow tweaks (`sealed-neow/`)

Gate: `SealedDeckRun.IsActive` = `RunManager.Instance?.DebugOnlyGetState()?.Modifiers` contains
a `SealedDeck` modifier. Re-evaluated at each use (cheap), never cached across runs.

### 7.1 Neow's Talisman rework

Vanilla `NeowsTalisman.AfterObtained` (sealed class, public non-async `Task` method,
dispatched virtually from `RelicCmd.Obtain`) upgrades the last Basic Strike and Defend: a
no-op in a sealed deck. Prefix: if the run is sealed, set `__result` to the rework and return
false; otherwise vanilla. Rework (synchronous): candidates = deck cards in pile order where
`IsUpgradable` and `Doomed.CanEnchant(card)`; pick `min(2, count)` distinct indices with a
run-seeded `Rng` (seed from the run seed string plus a salt, so a save-quit-Continue that
re-runs the pickup picks the same cards) through the pure `TalismanPickRules.PickIndices`
(ported, unit-tested); per pick `CardCmd.Upgrade(card, CardPreviewStyle.None)` then the
non-generic `CardCmd.Enchant(doomed.ToMutable(), card, 3)` and `NCardEnchantVfx.Create(card)`
into `NRun.Instance.GlobalUi.CardPreviewContainer` so the streamer sees which cards were hit.
Constants `Cards = 2`, `Doom = 3` in one place; a settings knob is a follow-up if wanted. The
Pomander coin flip, Neow's Bones eligibility, icon, run-history name and save compatibility
stay vanilla.

### 7.2 The Doomed enchantment

`StreamerDoomed : EnchantmentModel` (id `STREAMER_DOOMED`; class in `src/Game/Content/`):
`HasExtraCardText => true`; `OnPlay` => `PowerCmd.Apply<DoomPower>(choiceContext,
Card.Owner.Creature, Amount, Card.Owner.Creature, Card)` (the Inky idiom); `ExtraHoverTips`
=> the Doom power tip. Registration is the game's own mod-type scan
(`ModelDb.AllAbstractModelSubtypes` unions `ReflectionHelper.GetSubtypesInMods<AbstractModel>()`),
so no BaseLib dependency and no pool (enchantments need none). Never `new` it:
`ModelDb.Enchantment<StreamerDoomed>().ToMutable()`.

Icon (ruling 8): `EnchantmentModel.IconPath` is a non-virtual getter with a private cache
that falls back to `enchantments/missing_enchantment.png`. A postfix returning
`res://images/powers/doom_power.png` (256 px, `CompressedTexture2D`, exists on both branches)
when `__instance is StreamerDoomed` and the resource exists. The card badge's `TextureRect`
already scales mixed sizes (vanilla badges are 64 and 128 px). `PreloadManager.Cache
.GetCompressedTexture2D` is the loader vanilla uses, so no cache landmine.

Loc (`enchantments` table, injected by the same read-time seam as a "provide" map, since
the key does not exist in vanilla): `STREAMER_DOOMED.title` "Doomed";
`.description` "Apply [blue]{Amount}[/blue] [gold]Doom[/gold] to you when played.";
`.extraCardText` "Apply {Amount} [gold]Doom[/gold] to you." (`{Amount}` is substituted by the
engine from the instance amount). Provide-map keys are answered by the postfix when the table
lookup would otherwise throw, via a prefix on `GetRawText` and `HasEntry` for the exact keys
(BaseLib's `MissingLocPatch` does the same for its keys; the two compose).

Talisman text, replaced only while sealed (relics table, both keys):
`.description` "Upon pickup, [gold]Upgrade[/gold] [blue]2[/blue] random cards. They become
[red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played.";
`.eventDescription` "[gold]Upgrade[/gold] [blue]2[/blue] random cards. They become
[red]Doomed[/red]: apply [red]3[/red] [gold]Doom[/gold] to you when played."
Outside a sealed run (main menu compendium, normal runs) vanilla text shows.

### 7.3 Relic disables

Postfix on the base `RelicModel.IsAllowed(IRunState)`: if the run is sealed and
`__instance.Id.Entry` is `LEAFY_POULTICE` or `PRECARIOUS_SHEARS`, `__result = false`. One
predicate covers the Neow page (`IsAllowedAtNeow` defers to it), Neow's Bones, every grab bag
pull (chests, elites, Bossy Relics' expansion) and shops. Neither relic overrides the
predicate. The set is a constant; a settings list is a follow-up if wanted. Console
`relic LEAFY_POULTICE` bypasses `IsAllowed` (informational).

## 8. Settings, receipts and README

- `combatCardVotesOnly` default flips to `false` in `ChatSettings`, `SettingsBootstrap` and
  the `.json.example`. No migration of existing files: v0.3.0's bootstrap wrote `true` into
  every file, so installs keep `true` until the streamer flips the checkbox; the release
  notes and README say so, and Tristan is told directly.
- Checkbox label unchanged; help text becomes: "On: chat only votes on card rewards earned
  from combat; other card rewards are free picks. Off (default): chat votes to remove one
  option on Ancient-relic and Dream Catcher card rewards, and event or shop-relic card
  rewards cannot be skipped. Explanations appear on the relics and events themselves."
- README: the "Card rewards" row and the setting bullet describe the three behaviours; a
  short "Sealed Deck" note covers the Talisman rework and the two disabled relics; the mod
  compatibility section gains Balls2, StS1 Boss Ancients and More Relics as tested.

## 9. Landmines honoured and watchlist

- Prefix order: `_resumeInProgress` pass-through, then `_voteInProgress`, then the
  RemoveOne branch, then bail gates.
- `RelicCmd.Obtain` latch is a finalizer with `__state`; `CardReward` has two constructors
  (one Harmony key covers both).
- `NChooseACardSelectionScreen._Ready` is not patched (it calls into already-patched
  vanilla code in Sabotage's world; here it is simply unnecessary: `ShowScreen` returns the
  instance).
- Anchors as initializers, never `SetAnchorsPreset` on a fresh Control; positions from the
  header per frame, never `GetGlobalRect` in `_Ready`; rename before `QueueFree` on replaced
  labels; Godot resources revalidated with `IsInstanceValid` (the static-cache landmine).
- `VoteSession.Cancel()` fires `Cancelled`, not `Closed`: the popup and the resume path
  subscribe to both.
- `Id.Entry` is UPPER_SNAKE_CASE (`LEAFY_POULTICE`, `STREAMER_DOOMED`).
- Compat watchlist (add to CLAUDE.md at release prep): `FromChooseACardScreen` shape and its
  synchronous head; the 350 ms `SelectHolder` debounce; `ShowScreen` as sole opener returning
  the instance and assigning `_cards` by reference; the four vanilla relics constructing
  rewards before their first await; `Hook.ModifyRestSiteHealRewards` list-in signature;
  Skip before Reroll in `CardRewardAlternative.Generate`; `LocTable.GetRawText` staying the
  single read path under `SmartFormat`; `EnchantmentModel.IconPath` staying non-virtual with
  the `_iconPath` cache; `NeowsTalisman.AfterObtained` staying a non-async public method;
  base `IsAllowedAtNeow` deferring to `IsAllowed`; `NEventOptionButton` keeping `%Text` and
  its four nine-patch siblings; `EventOption.FromRelic` falling back to
  `relic.DynamicEventDescription`.

## 10. Testing

Unit (`tests/`, `[Collection("TiLog.Sink")]` where logging is touched; source includes are
surgical per CLAUDE.md): `AuthorityRules` full table incl. both switch states and the
unregistered-combat-tag stand-down; `RemovalClickRules` incl. reroll and budget 0;
`RemoveVoteOptionLabels` build and resolve; `RemoveVoteReceipts` open/close/tie/override
strings; `AuthorityLoc` (no em dash, no "strike", keys present against the asset fixture,
idempotent, Lead first); `RelicTextRegistry` round trip and learn-once; `TalismanPickRules`
(distinct, deterministic, take >= count, take = 0).

Operator matrix (new `notes/14-remove-one-matrix.md`; run with the checkbox Off unless stated;
console recipes `relic KALEIDOSCOPE|GLASS_EYE|LOST_COFFER|DREAM_CATCHER|HEFTY_TABLET|
LEAD_PAPERWEIGHT|DRIFTWOOD|NEOWS_BONES|NEOWS_TALISMAN|STRONGBOX`; Orrery must be BOUGHT):
1. Kaleidoscope: two removal votes; click-to-start; red paint; pick from the rest; ESC and
   reopen shows the same red option with no re-vote.
2. Glass Eye: five votes sharing the act's override budget; Lost Coffer: one vote plus potion.
3. Dream Catcher at a rest site and via Dense Vegetation; rest option text.
4. Hefty Tablet and Lead Paperweight via console and via Neow's curse slot; Skip removed and
   itself removable; Injury still added on Skip.
5. Neow's Bones nesting an eligible relic.
6. Per surface: comply; override a removed card; override a removed Skip; budget 0 unclickable;
   override during the countdown; chat offline mid-vote (cancel => vanilla screen).
7. Driftwood: reroll after a removal is free and re-votes; reroll denied during a vote.
8. Unskippable: the five events incl. ESC; Orrery purchase; Lord's Parasol; Strongbox; map
   blocked while pending; Proceed blocked while pending.
9. Text: every appended key on hover, on the Ancient button, in the compendium, and on the
   rest option; the grown event buttons (Trash Heap, Lost Coffer on an Ancient page); text
   absent with the checkbox On.
10. Third-party: Balls2 Pokéball reward pick-votes; Balls2 Donu and an StS1 Boss Ancient get
    the Ancient vote; Strongbox unskippable with its line; a learned relic appears in the
    file (use a Sabotage-free test relic or Strongbox with the built-in entry removed).
11. Sealed deck with Always Whale: Talisman upgrades 2 cards, tombstone badge, Doom tooltip,
    Doom applied on play; Leafy Poultice and Precarious Shears never offered across seeds;
    unsealed run shows vanilla Talisman text and behaviour.
12. Regression: normal combat reward vote; checkbox On restores v0.3.1 behaviour bit for bit;
    default branch (v0.107.1) install logs the stand-down and pick-votes everything.
