# HANDOFF 2026-09-13: remove-one + sealed-neow testing, then the three-way setting

Written for a fresh session. Read this, then `CLAUDE.md` (Tier 1 and 2), then the spec and
the matrix named below. Everything here is committed on `main`; nothing is in flight.

## 1. Where things stand

- HEAD `40ea4bf` (`sealed-neow/8`). 580 unit tests green. `build.ps1` + `install.ps1` were run
  at that commit, so the installed mod's stamp is `0.3.1+40ea4bf...`. Manifest version is still
  `0.3.1`; the README already says "since v0.4.0" for the setting default, so a `release/v0.4.0`
  bump is owed before shipping.
- Two slices shipped this week via subagent-driven development, all on `main`:
  - `remove-one/1..13` (`ed886bd..f01d62a`): per-origin classifier for card rewards, "chat
    removes one option" vote on the card-reward screen and the choose-a-card screen (Hefty
    Tablet, Lead Paperweight), unskippable event and shop-relic card rewards, appended
    explanation text at read time, event option button growth, `combatCardVotesOnly` default
    flipped to `false`.
  - `sealed-neow/1..8` (`333d456..40ea4bf`): in Sealed Deck runs only, Neow's Talisman upgrades
    2 random cards and gives them the new `StreamerDoomed` enchantment (5 Doom on play, raised
    from 3 on 2026-09-13 after Sabotage playtest feedback); Leafy Poultice and Precarious
    Shears never offered.
- Spec (binding): `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md`.
  Plans: `docs/superpowers/plans/2026-09-07-remove-one.md`, `2026-09-07-sealed-neow.md`.
  Running notes and every review-round parking: `notes/06-followups-and-deferred.md` (the two
  sections at the end). Operator matrix: `notes/14-remove-one-matrix.md` (rows 1-12 gate
  `remove-one-complete`, S1-S6 gate `sealed-neow-complete`; every row is still `[ ]`).
- NOTHING in either slice has run in game yet. Surfinite is testing today (2026-09-13).

## 2. What the tester needs to know first

1. **The mod was disabled in the local mod manager** as of the last godot.log before this work
   ("Skipping loading mod slay_the_streamer_2, it is set to disabled in settings"). Enable it.
2. **First launch check:** godot.log must contain the `[SlayTheStreamer2] Harmony patched N
   method(s):` block listing, among the old targets, `LocTable.GetRawText`, `LocTable.HasEntry`,
   `LocTable.GetLocString`, `EnchantmentModel.get_IconPath`, `NeowsTalisman.AfterObtained`,
   `RelicModel.IsAllowed`, `RelicCmd.Obtain`, both `CardReward` ctors,
   `Hook.ModifyRestSiteHealRewards`, `CardSelectCmd.FromChooseACardScreen`,
   `NChooseACardSelectionScreen.ShowScreen/_ExitTree/SelectHolder/OnSkipButtonReleased`,
   `NEventOptionButton._Ready`. If instead there is one `FATAL: Init failed` line, `PatchAll`
   threw and the mod is inert; the most likely cause is a Harmony binding error (see the new
   CLAUDE.md landmine: `___field` injection takes the LITERAL field name, so `LocTable._name`
   is `____name`; this exact bug shipped and was fixed in `0ff2da4`).
3. The setting must be **Off** ("Card-reward votes only occur after combat") for the new rules.
   Tristan's file is already Off (files that existed at v0.2.2 were written `false`).
4. Beta branch only: on the default branch the combat hook is missing and everything falls back
   to normal pick votes with a one-time Warn (`combat-origin tagging did not register`).
5. Console recipes: `relic KALEIDOSCOPE|GLASS_EYE|LOST_COFFER|DREAM_CATCHER|HEFTY_TABLET|
   LEAD_PAPERWEIGHT|DRIFTWOOD|NEOWS_BONES|NEOWS_TALISMAN|STRONGBOX`. `relic ORRERY` is NOT the
   shop path (buy it, or Lord's Parasol). Sealed rows need a Custom run with Sealed Deck plus
   Pikcube's Always Whale (Tristan's setup) so Neow's blessings appear.
6. Log anchors are listed in `notes/14`; all removal-vote lines are tagged `[card-remove]`,
   `[choose-remove]`, `[remove-one]`, `[unskip]`, `[card-scope]`, `[sealed-neow]`.

## 3. Behaviour summary (what "correct" looks like)

- Combat card rewards: unchanged pick vote (Skip is `#0` when "Allow chat to skip" is on).
- Ancient-relic card rewards (Kaleidoscope, Glass Eye, Lost Coffer, Neow's Bones nested, any
  mod relic of Ancient rarity) and Dream Catcher: the screen shows "Click any option to start the
  removal vote."; the streamer's first click (card or Skip) starts the vote and takes nothing;
  chat votes which option to REMOVE (Skip is `#0` when present); the removed option paints red;
  the next click picks. Clicking the removed option costs one vote override (unclickable at
  budget 0). A click during the countdown is a normal override (takes that card). Closing and
  reopening the screen keeps the removal (recorded per reward). Driftwood: reroll is free after
  the removal and re-votes on the new cards.
- Hefty Tablet / Lead Paperweight (choose-a-card screen): same flow; Skip counts as an option.
- Event card rewards (Future of Potions, Colorful Philosophers, Brain Leech Rip, Trial Guilty,
  Crystal Sphere) and shop-relic rewards (Orrery, Strongbox from Haxxero's More Relics): no vote;
  Skip hidden and denied (ESC too), banner "Choose a Card (no skip)", status line "This card
  reward cannot be skipped.", the parent rewards screen's Skip Rewards disabled and "Every card
  reward must be taken here." above the Loot banner; Proceed blocked until taken. Known,
  accepted: after a Driftwood reroll the rebuilt Skip button LOOKS live but the deny holds.
- Text: a blue line `Slay the Streamer: ...` appended on its own line to relic descriptions
  (hover, compendium, Ancient option button), the twelve event option keys, Crystal Sphere's
  instructions panel, Dream Catcher's rest-site line. Event option buttons grow to fit. The line
  disappears when the setting is On. Third-party relics that produce a governed card reward are
  learned into `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.learned-relics.json` and get a
  generic line from then on.
- Draft modifier picks: currently fall into the "untagged" bucket, so they are unskippable
  no-ops (vanilla Draft already has `CanSkip = false`): the streamer picks, no vote. The README
  still claims chat drafts the starting deck; that sentence is wrong now (fix in section 5).

## 4. Known risks and parkings to keep in mind while reading test feedback

- Rule 6 fails CLOSED: any card reward the classifier cannot attribute becomes unskippable
  (four restraints at once). Rows 8, 10, 12 are the ones that de-risk it. Known misfire: a
  CLONED CardReward (Hades Ancients' Sea Star) carries no tag.
- `RemovalVoteFlow.TryStart` now refuses to start when a vote is already active and `Finish`
  only clears its own session; a back-to-back override-then-start race was the concern.
- The alternate-select prefix's catch does not reset `_voteInProgress` (callees catch
  internally). If a screen ever gets stuck with every click ignored, look for a stuck flag.
- `RemovalStatusLine` polls its text at 4 Hz; `RewardsHeaderSubLabel` resolves the Loot header
  lazily because `NRewardsScreen._headerLabel` is assigned inside vanilla `_Ready`.
- Talisman: `AfterObtained` is NOT re-run on Continue; whether the upgrade+enchant survives a
  save-quit-Continue depends on the save checkpoint (row S3). If S3 fails, the answer is the
  CLAUDE.md mid-room mutation landmine, not the pick seed.
- Doomed badge uses `images/powers/doom_power.png` (256 px VRAM-compressed) scaled into the
  badge slot; check it visually in S1. Hover shows the Doom power tooltip.
- `RelicDisablePatch` logs its evidence line once per game session.
- Third-party mods verified by decompile only (Balls2, StS1 Boss Ancients, More Relics; all
  installed locally; see memory `frostprime_mod_list`): their combat rewards pick-vote, their
  custom ancients get the Ancient vote, Strongbox is unskippable. The Downfall port also patches
  `FromChooseACardScreen` (compat check only if Tristan runs it).

## 5. Agreed next work (SHIPPED as reward-modes/1..8, 2026-09-13)

Surfinite's rulings from the 2026-09-13 conversation:

1. **Three-way setting replacing the checkbox.** JSON key `nonCombatCardRewards` with values
   `free` | `removeOne` | `mixed`, default `mixed`. Migration on load: old `combatCardVotesOnly`
   `true` -> `free`, `false` -> `mixed`; drop the old key on the next write. Panel: a dropdown
   row like the card-skips one. Label: "Non-combat card rewards" (Surfinite's longer label would
   wrap next to the dropdown). Help text, concise, one line each (Surfinite: long descriptions
   are the same as no description):
   - Free: "Chat votes only after combat; the streamer picks the rest, Skip allowed."
   - Remove-one: "Chat gets a remove-one style vote on all non-combat card-rewards."
   - Mixed: "Cards from Ancients use a remove-one vote. Events are unskippable with no voting."
2. **`removeOne` mode**: classifier rule 6 and the shop-relic branch return RemoveOne. Text
   catalogue gets mode-aware variants for the twelve event keys, Orrery and Strongbox ("chat
   votes to remove one option from the card rewards"; Crystal Sphere: "chat votes to remove one
   of the cards uncovered here."). No new layouts: those rewards use the same card-reward screen,
   popup, status line and paint as Kaleidoscope. Pacing note for Tristan: Orrery becomes five
   removal votes, Colorful Philosophers three. Surfinite offered this mode to Tristan on Discord
   as the fallback if chat complains about no vote on events.
3. **Draft**: streamer picks in ALL modes, chat never votes on it (same rationale as Sealed
   Deck). Needs a small Draft origin tag (prefix on `Draft.OfferRewards` pushing a flag that the
   existing `CardReward` ctor postfix reads) so `removeOne` cannot turn Draft into ten removal
   votes. Correct the README Draft sentence.
4. **Override counter on the Ancient screens** (Surfinite, 2026-09-13 testing): the Ancient
   vote popup shows nothing about overrides, so the streamer cannot tell one is spendable.
   Show the same gold "{streamer} has N vote overrides remaining this act" line the card
   screens use (`StreamerBudgetCounterLabel`, viewport-centred X) while an Ancient vote is
   open. Check `AncientVotePatch`/`AncientVotePopup` for where the override click lands.
5. Update README, the in-game help text, notes/06, notes/14 (new rows for `removeOne` mode and
   Draft), CLAUDE.md commit prefix. Then `release/v0.4.0` (manifest bump, changeNote, README
   Beta version pin per `release_and_game_update_workflow` memory).

Estimate: about half a day of subagent-driven development, roughly five tasks. Process: use
`superpowers:brainstorming` (bounded; the design above is the brief), `writing-plans`, then
`subagent-driven-development`. Commit prefix suggestion: `reward-modes/N:`.

## 6. Context that is not in the repo

- Tristan (FrostPrime) confirmed the tournament is next year (sponsor fiscal years); he has been
  slow to play on stream. Surfinite told him on 2026-09-12 that the next Slay the Streamer has:
  unskippable event/merchant card selections, Ancient relics with the remove-one vote, Leafy
  Poultice and Precarious Shears disabled, Talisman reworked; and offered events-as-remove-one
  if chat complains. A build goes to him "in a few days"; he should DM before playing so the mod
  can be brought up to the latest game version.
- Sabotage playtest (2026-09-12) feedback drove Doom 3 -> 5. Surfinite noted he might have
  designed the mod as its own modifier ("Slay the Streamer") that arms only when selected, but
  existing users are used to the current shape; not changing that.
- Sister repo: `C:\Users\Surfinite\SabotageTheStreamer` (source of the ported strike/ and neow2
  work). Its rig-tested wording was carried word for word with "Slay the Streamer:" as the lead.
- The subagent-driven ledgers for both slices (rulings, parked findings, per-task review
  outcomes) were archived to the session scratchpad, which does not survive the session; their
  substance is in notes/06 and the "Rulings I made" summary Surfinite received. If you need the
  per-task detail, `git log e272742..40ea4bf` plus the task briefs' content in the plans is the
  record.
- Workflow habits Surfinite expects: per-task commits to `main`; `build.ps1` then `install.ps1`
  after the final commit so the stamp matches HEAD; no em dashes in shipped text or code
  comments; 0-indexed chat votes; honest, concise substance; do not tell him when to stop.
