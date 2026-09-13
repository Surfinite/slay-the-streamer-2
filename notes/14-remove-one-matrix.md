# Remove-one votes + unskippable rewards: operator matrix

Spec: `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md` section 10. Run every row with the `combatCardVotesOnly` checkbox **Off** (the new default) unless a row says otherwise. Console recipes use `relic <ID>`, e.g. `relic KALEIDOSCOPE` / `GLASS_EYE` / `LOST_COFFER` / `DREAM_CATCHER` / `HEFTY_TABLET` / `LEAD_PAPERWEIGHT` / `DRIFTWOOD` / `NEOWS_BONES` / `NEOWS_TALISMAN` / `STRONGBOX`.

**Orrery caveat:** `relic ORRERY` via the dev console does NOT reproduce the shop path (it constructs the relic without going through the purchase flow that tags its card rewards). To exercise Orrery, either buy it from a shop, or take Lord's Parasol (which auto-grants Orrery through the same purchase-tagging path).

**Warn-text note:** the Skip-button probe warn text is `no Skip control; #0 indicator omitted` (this replaced the older `could not locate Skip button` wording from an earlier slice; don't search logs for the old string).

Evidence anchors below are exact substrings as shipped (see file:line):
- `tagged relic-origin card reward` : `src/Game/DecisionVotes/RelicOriginTags.cs:42`
- `tagged N rest-site card reward(s)` : `src/Game/DecisionVotes/RestSiteOriginTags.cs:43` (N is a number)
- `removal vote opened` : `src/Game/DecisionVotes/RemovalVoteFlow.cs:63`
- `chat removed` : `src/Game/DecisionVotes/RemovalVoteFlow.cs:126`
- `override during removal vote` : `src/Game/DecisionVotes/RemovalVoteFlow.cs:173`
- `override: streamer took the removed option` : `src/Game/DecisionVotes/CardRewardVotePatch.cs:255`
- `context open cards=` : `src/Game/DecisionVotes/ChooseACardRemovePatch.cs:93`
- `screen bound` : `src/Game/DecisionVotes/ChooseACardRemovePatch.cs:106`
- `card reward screen restrained` : `src/Game/DecisionVotes/UnskippableRewards.cs:73`
- `rewards set restrained` : `src/Game/DecisionVotes/UnskippableRewards.cs:89`
- `Skip denied on an unskippable card reward` : `src/Game/DecisionVotes/CardRewardVotePatch.cs:818`
- `Proceed blocked: an unskippable card reward` : `src/Game/DecisionVotes/CardRewardSkipGatePatch.cs:613`
- `learned relic` : `src/Game/DecisionVotes/RelicTextRegistry.cs:46`
- `event option grown by` : `src/Game/DecisionVotes/EventOptionGrowPatch.cs:65`
- `combat-origin tagging did not register` : `src/Game/DecisionVotes/RewardAuthority.cs:23`

| # | Row | Console recipe | Evidence anchor | Result |
|---|---|---|---|---|
| 1 | Kaleidoscope: two removal votes; click-to-start; red paint; pick from the rest; ESC and reopen shows the same red option with no re-vote (red lands ~1.1 s after reopen, after vanilla's fade-in); status line sits in the countdown slot, "removal"/"removed" in red, hidden while the vote runs | `relic KALEIDOSCOPE` | `tagged relic-origin card reward` on obtain; `removal vote opened` per screen; `chat removed` on close; no second `removal vote opened` on reopen | [ ] |
| 2 | Glass Eye: five votes sharing the act's override budget; Lost Coffer: one vote plus potion | `relic GLASS_EYE`, `relic LOST_COFFER` | `tagged relic-origin card reward` x5 (Glass Eye) / x1 (Lost Coffer); `removal vote opened` per screen; `override: streamer took the removed option` decrements the shared act budget | [ ] |
| 3 | Dream Catcher at a rest site and via Dense Vegetation; rest option text | `relic DREAM_CATCHER` at a campfire; trigger Dense Vegetation | `tagged N rest-site card reward(s)`; `removal vote opened`; rest-option explanation text visible | [ ] |
| 4 | Hefty Tablet and Lead Paperweight via console and via Neow's curse slot; two runs: chat removes a card (Skip survives as a pick) and chat removes Skip itself (must take a card); Injury still added on Skip | `relic HEFTY_TABLET`, `relic LEAD_PAPERWEIGHT`; also via Neow curse-slot pick | `context open cards=`; `screen bound`; `chat removed Skip` case; Injury still granted after a Skip removal | [ ] |
| 5 | Neow's Bones nesting an eligible relic | `relic NEOWS_BONES` | `tagged relic-origin card reward` for the nested relic's own reward; `removal vote opened` for the nested pick | [ ] |
| 6 | Per surface: comply; override a removed card; override a removed Skip (completes the reward: gone from Loot, no second curse on reopen; Escape opens the pause menu, never Skip; remove-one/16); budget 0 unclickable; override during the countdown; chat offline mid-vote (cancel => vanilla screen); the gold "N vote overrides remaining" counter shows during the removal vote AND after the removal on both screens (remove-one/14) | any RemoveOne surface above | `chat removed`; `override: streamer took the removed option`; `override during removal vote`; budget-0 click produces no override log; chat disconnect cancels cleanly to a normal vanilla screen | [ ] |
| 7 | Driftwood: reroll after a removal is free and re-votes; reroll denied during a vote | `relic DRIFTWOOD` alongside any RemoveOne surface | second `removal vote opened` after a free reroll; reroll attempt during an open vote is denied (no reroll log, vote unaffected) | [ ] |
| 8 | Unskippable: the five events incl. ESC; Orrery purchase; Lord's Parasol; Strongbox; map blocked while pending; Proceed blocked while pending; Crystal Sphere with potions left after the cards: Proceed re-enables and the header line disappears (`Skip Rewards released`, remove-one/19) | Future of Potions, Colorful Philosophers, Brain Leech Rip, Trial Guilty, Crystal Sphere; buy Orrery or take Lord's Parasol; `relic STRONGBOX` | `card reward screen restrained`; `rewards set restrained`; `Skip denied on an unskippable card reward`; `Proceed blocked: an unskippable card reward`; map button also blocked while pending | [ ] |
| 9 | Text: every appended key on hover, on the Ancient button, in the compendium, and on the rest option; the grown event buttons (Trash Heap, Lost Coffer on an Ancient page); text absent with the checkbox On | any relic/event above; flip checkbox On for the negative check | `event option grown by`; explanation text visible on hover/Ancient button/compendium/rest option with checkbox Off; absent with checkbox On | [ ] |
| 10 | Third-party: Balls2 Pokeball reward pick-votes; Balls2 Donu and an StS1 Boss Ancient get the Ancient vote; Strongbox unskippable with its line; a learned relic appears in the file (use a Sabotage-free test relic or Strongbox with the built-in entry removed) | Balls2 Pokeball combat reward; Balls2 Donu / StS1 Boss Ancients' Donu-Deca; `relic STRONGBOX`; an unrecognized third-party relic | normal combat vote for Pokeball; Ancient vote fires for the custom ancients; `card reward screen restrained` / `rewards set restrained` for Strongbox; `learned relic` for the unrecognized one | [ ] |
| 11 | Sealed deck with Always Whale: Talisman upgrades 2 cards, tombstone badge, Doom tooltip, Doom applied on play; Leafy Poultice and Precarious Shears never offered across seeds; unsealed run shows vanilla Talisman text and behaviour | Sealed Deck modifier + Pikcube's Always Whale, take Neow's Talisman | tombstone badge on the 2 upgraded cards; Doom power tooltip on hover; Doom applied when the card is played; neither disabled relic appears across several seeds; a non-sealed run's Talisman is unchanged vanilla | see the "sealed-neow rows" section below (rows S1-S6); not part of remove-one |
| 12 | Regression: normal combat reward vote; checkbox On restores v0.3.1 behaviour bit for bit; default branch (v0.107.1) install logs the stand-down and pick-votes everything | plain combat win; toggle checkbox On; install on the default branch | normal combat card reward vote fires as before; checkbox On behaves exactly like the pre-remove-one release; default branch logs `combat-origin tagging did not register` and every card reward is a normal vote | [ ] |

## sealed-neow rows

Spec: `docs/superpowers/specs/2026-09-07-remove-one-unskippable-sealed-neow-design.md` section 7. Every row below requires the Sealed Deck Custom Mode modifier plus Pikcube's Run Modifiers' Always Whale (so Neow's blessings, including Talisman, are offered in the sealed run). Evidence anchors:
- `talisman: upgraded+doomed {n} of {N} candidates (doom=5)` : `src/Game/Content/TalismanReworkPatch.cs:60`
- `sealed run: Leafy Poultice and Precarious Shears disabled` : `src/Game/Content/RelicDisablePatch.cs:27`
- all sealed-neow lines carry the `[SlayTheStreamer2][sealed-neow]` tag

| # | Row | Recipe | Evidence | Result |
|---|---|---|---|---|
| S1 | Sealed + Always Whale: Talisman upgrades 2 cards, tombstone badge, Doom tooltip on hover | take Talisman at Neow, or `relic NEOWS_TALISMAN` after the sealed pick | `talisman: upgraded+doomed 2 of N` | [ ] |
| S2 | Playing a Doomed card applies 5 Doom; twice = 10 | combat | Doom power stack on the player | [ ] |
| S3 | Save, quit, Continue right after the Talisman: the same two cards stay upgraded and Doomed | | badge persists | [ ] |
| S4 | Talisman text: sealed run shows the rework text on the Neow button and relic hover; main menu compendium and an unsealed run show vanilla text | | visual | [ ] |
| S5 | Leafy Poultice and Precarious Shears never offered at Neow or by Neow's Bones across 5+ sealed starts | `relic NEOWS_BONES` | `sealed run: Leafy Poultice and Precarious Shears disabled` (logged once per game session) | [ ] |
| S6 | Unsealed run: vanilla Talisman, both relics can appear | | no `[sealed-neow]` lines | [ ] |

Hand this matrix to the operator. `remove-one-complete` is applied once rows 1-10 and 12 are green; `sealed-neow-complete` is applied once rows S1-S6 are green.
