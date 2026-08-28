# Card-choice surfaces in StS2 — inventory for the "what does chat / the Saboteur decide?" ruling

2026-08-28. Pre-decision landscape research, same convention as notes/07–11. Source: fresh XML-free
decompile of game Beta **v0.111.0** (`decompiled/sts2-v0.111.0/`), cross-checked against the default
branch tree (`decompiled/sts2-v0.107.1/`). Every family and site below was traced to its construction
site or `CardSelectCmd` call; nothing here is inferred from the wiki or from StS1.

Audience: basis for a message to FrostPrime (Tristan), who needs to rule on which card decisions the
Saboteur (SabotageTheStreamer) / chat (Slay the Streamer 2) gets authority over. Both mods sit on the
same game surfaces, so the *inventory* is shared; the *implementation cost* column is written for
this mod (chat vote on a suspended UI screen) — the Saboteur's model-level integration has the same
family structure but different per-family costs.

---

## 0. The one-paragraph answer to "why is Hefty Tablet a free pick but Kaleidoscope votes?"

Both mods hooked **one UI screen**: `NCardRewardSelectionScreen`, which is what every `CardReward`
object opens. MegaCrit reuse `CardReward` for far more than post-combat rewards (Kaleidoscope, Orrery,
Dream Catcher, five events, the Draft modifier…), so all of those vote. Everything that offers new
cards through a *different* screen — Hefty Tablet, Lead Paperweight, Scroll Boxes, Sea Glass, Sealed
Deck, Brain Leech's other branch — does not. **The current line is an accident of which screen got
patched first, not a design.** The v0.2.2 `combatCardVotesOnly` setting drew a *narrower* line inside
that same screen (combat-origin rewards only); it did not extend outward. Widening is a per-*screen-
family* cost, not a per-instance cost — which is the key fact for deciding what's cheap.

---

## 1. How the game is built (the structure the ruling has to fit)

There are exactly **two shared chokepoints** and two bespoke paths:

| Layer | Class | Role |
|---|---|---|
| Reward flow | `CardReward` → `NCardRewardSelectionScreen` (via `RewardsCmd.OfferCustom` or the combat room-end path) | "Add a card to your deck" reward button → pick 1 of 3. **This is what both mods vote on today.** |
| Decision arbitration | `CardSelectCmd` (`MegaCrit.Sts2.Core.Commands`) — 13 static `From*` entry methods, each opening one screen | Every other "which card(s)?" question in the game: new-card choices *and* deck manipulation *and* in-combat hand/pile picks. Only caller of every `N*SelectScreen.ShowScreen`. |
| Mutation | `CardPileCmd` / `CardCmd` | Actually adds/removes/upgrades/transforms the card after a decision. Never asks the player anything. |
| Bespoke | `MerchantEntry.OnTryPurchaseWrapper` (shop buys), `OneOffSynchronizer.DoLocalMerchantCardRemoval` (shop removal) | Shop does not use `CardSelectCmd` at all. |

`CardSelectCmd` entry → screen map (all screens exist on both branches):

| `CardSelectCmd` method | Screen | Picks | Callers (v0.111.0) |
|---|---|---|---|
| `FromChooseACardScreen` | `NChooseACardSelectionScreen` — the "Hefty Tablet screen": ≤3 cards, pick 1, optional skip | 1 | 13 |
| `FromChooseABundleScreen` | `NChooseABundleSelectionScreen` — pick 1 pack of N cards | 1 pack | 1 (Scroll Boxes) |
| `FromSimpleGridForRewards` / `FromSimpleGrid` | `NSimpleCardSelectScreen` — grid, min/max pick from prefs | K of N | 3 + 1 |
| `FromDeckForRemoval/Upgrade/Transformation/Enchantment/Generic` | `NDeck*SelectScreen` — your own deck | K of deck | 15 / 7 / 9 / 16 / 2 |
| `FromCombatPile` | `NCombatPileCardSelectScreen` — draw/discard/exhaust pile, mid-combat | K of pile | 18 |
| `FromHand`, `FromHandForDiscard`, `FromHandForUpgrade` | inline hand highlight, mid-combat | K of hand | 23 / 10 / 1 |

MegaCrit also ship a **pluggable `ICardSelector`** (`CardSelectCmd.Selector` / `.LocalSelector`,
`UseSelector`/`PushSelector`) built for their tests/AutoSlay and used by one relic (Whispering
Earring auto-answers choices with the first option, no UI). It is tempting as a single integration
point but: it *replaces* the screen rather than suspending it, `GetSelectedCards(options, min, max)`
receives no context (can't tell Hefty Tablet from "discard a card"), and `LocalSelector` doesn't exist
on the default branch. The practical integration is the pattern this mod already uses: prefix the
screen's select method (suspend → vote → resume) plus a prefix on the `CardSelectCmd.From*` entry to
capture *why* (`PlayerChoiceContext.LastInvolvedModel` names the relic / card / potion / monster).

---

## 2. Tier A — the reward screen (`CardReward`) — **votes today**

All sites that construct a `CardReward`/`SpecialCardReward` (exhaustive, 16 live sites + save-load).
"Combat-origin" = passes through `Hook.BeforeCombatRewardOffered` = what `combatCardVotesOnly` keeps.

| Site | Trigger | Options / picks / skip | Combat-origin? | Slay votes today |
|---|---|---|---|---|
| `RewardsSet.GenerateRewardsFor` (Monster / Elite / Boss) | every combat win | 3 / 1 / skip (budgeted) | yes | yes |
| Tutorial rewards (`TryGenerateTutorialRewards`, Ironclad first run) | first fights | 3 fixed / 1 | yes | yes |
| Prayer Wheel (`TryModifyRewards`) | extra reward after Monster fights | 3 / 1 | yes | yes |
| White Star (`TryModifyRewards`) | extra Boss-tier reward after Elite fights | 3 / 1 | yes | yes |
| The Hunt (card `OnPlay`, fatal kill) | extra reward that combat | 3 / 1 | yes | yes |
| Thieving Hopper stolen card (`SwipePower.BeforeDeath`) | `SpecialCardReward`, 1 fixed card | accept / leave | yes | no (no choice; skip-gate deliberately excludes it) |
| The Lantern Key quest card (`TheLanternKey.Fight`) | `SpecialCardReward`, 1 fixed card | accept / leave | yes (event fight) | no (same) |
| Kaleidoscope (`AfterObtained`, incl. as Neow option) | relic pickup | 2 rewards × (3 from other characters' pools) / 1 each | **no** | yes unless combat-only |
| Orrery (`AfterObtained`) | relic pickup | 5 rewards × 3 / 1 each | no | yes unless combat-only |
| Glass Eye (`AfterObtained`) | relic pickup | 5 rewards × 3, rarity-locked C,C,U,U,R / 1 each | no | yes unless combat-only |
| Lost Coffer (`AfterObtained`) | relic pickup | 1 reward × 3 (+ a potion) / 1 | no | yes unless combat-only |
| Dream Catcher (`TryModifyRestSiteHealRewards`) | rest-site Heal | 3 / 1 | no | yes unless combat-only |
| The Future of Potions (event, `Trade`) | event option | 3 (all pre-upgraded) / 1 | no | yes unless combat-only |
| Trial (event, `NondescriptGuilty`) | event option | 2 rewards × 3 / 1 each | no | yes unless combat-only |
| Brain Leech — "Rip" (event) | event option | 3 colorless / 1 (× RewardCount) | no | yes unless combat-only |
| Colorful Philosophers (event, `OfferRewards`) | event option | 3 rewards (C/U/R of one color) × 3 / 1 each | no | yes unless combat-only |
| Crystal Sphere card item (`CrystalSphereCardReward`) | minigame prize | 3 of one rarity / 1 | no | yes unless combat-only |
| Draft (Custom-mode modifier, `OfferRewards`) | run start | 10 × (3 / 1, **no skip**) | no | yes unless combat-only |

Not choices, listed to close the loop: `LinkedRewardSet` exists but is never constructed in v0.111.0;
no relic overrides `ShouldAllowSelectingMoreCardRewards` (multi-pick from one reward is dormant).

---

## 3. Tier B — "choose NEW card(s)" through other screens — **free pick today**

This is the gap Tristan noticed. Exhaustive: every `FromChooseACardScreen` / `FromChooseABundleScreen`
/ `FromSimpleGrid(ForRewards)` caller in the game (18 sites). Split by *when* it happens, because
mid-combat is a different conversation.

### 3a. Outside combat (map / relic pickup / event / Neow)

| Site | Trigger | Options / picks / skip | Screen | Vote cost |
|---|---|---|---|---|
| **Hefty Tablet** (`AfterObtained`) | relic pickup (Ancient / Neow-curse pool) | 3 rare / 1 / skip (skipping still adds Injury) | ChooseACard | **Medium** — first patch of this screen family; then free for every sibling |
| **Lead Paperweight** (`AfterObtained`) | relic pickup (Ancient / Neow-positive) | 2 colorless / 1 / skip | ChooseACard | free once Hefty Tablet is done |
| **Massive Scroll** (`AfterObtained`) | relic pickup, **multiplayer-only** | 3 / 1 / skip | ChooseACard | free once done (and the mod bails in MP anyway) |
| **Scroll Boxes** (`AfterObtained`) | relic pickup (Ancient / Neow-positive) | **2 packs × 3 cards** (2C+1U; 1% all-Claw for Defect) / 1 pack / **no skip** | ChooseABundle | **Medium-small** — one relic, a 2-option vote; the vote popup needs a "pack" rendering (3 cards per option) |
| **Brain Leech — "Share Knowledge"** (event) | event option | 5 / 1 / no cancel | SimpleGrid (single pick) | **Medium** — grid screen, but single-pick so the vote shape is the usual one |
| **Room Full of Cheese — "Gorge"** (event) | event option | 8 common / **pick 2** / no cancel | SimpleGrid (multi-pick) | **Hard** — needs a "pick K" vote (see §6) |
| **Sea Glass** (`AfterObtained`) | relic pickup (Ancient) | 15 (5C/5U/5R of another character) / **pick any 0..15** / skip | SimpleGrid (multi-pick) | **Hard** — free-form subset choice; no sane chat-vote shape |
| **Sealed Deck** (Custom-mode modifier, Neow option) | run start | 30 / **pick exactly 10** / no cancel | SimpleGrid (multi-pick) | **Hard** — 10 sequential votes or a ranked ballot; already ruled "streamer drafts" in README |

Easy-to-confuse, **not** in this tier (they use `CardReward` → already Tier A): Kaleidoscope, Draft,
Crystal Sphere, Brain Leech's "Rip" branch.

### 3b. Mid-combat (timing-sensitive — a 30s vote pauses the fight)

| Site | Trigger | Options / picks / skip | Notes |
|---|---|---|---|
| Attack / Skill / Power / Colorless Potion (`OnUse`) | potion use, player turn | 3 / 1 / skip | ChooseACard |
| Discovery, Splash, Quasar (cards, `OnPlay`) | card play, player turn | 3 / 1 / skip | ChooseACard |
| Abundance (card, `OnPlay`) | card play | 3 powers / 1 / **no skip** | ChooseACard |
| Toolbox (relic, `BeforeHandDraw`) | turn 1, before first draw | 3 colorless / 1 / no skip | ChooseACard — fires before the player has agency |
| Choices Paradox (relic, `AfterPlayerTurnStart`) | turn 1 | 5 powers / 1 / no skip | SimpleGrid |
| Knowledge Demon "Curse of Knowledge" (monster move) | **enemy turn**, per target | 2 curses / 1 / no skip | ChooseACard — the player chooses which curse they receive |

Technically the same screen patch as 3a covers all of these for free; the cost is **gameplay**:
each is a combat pause of one vote duration, potions/Discover cards can fire several times per fight,
and the original StS1 mod never voted mid-combat. Recommend ruling these out explicitly rather than
leaving them implied.

---

## 4. Tier C — "choose which of YOUR cards" (deck manipulation) — free pick today

Same `CardSelectCmd` machinery, but the option set is the player's own deck/hand/pile, not new cards.
Listed so the ruling can say yes/no to the *category*; per-instance detail is in the decompile.

| Family | Representative sites | Option set size | Vote shape problem |
|---|---|---|---|
| Remove (`FromDeckForRemoval`, 15 callers) | rest-site Cook (remove 2, Meat Cleaver), Empty Cage (remove 2), events Field of Man-Sized Holes / Luminous Choir / Wellspring / Zen Weaver / Amalgamator (Strikes & Defends) | whole deck (20–60 cards) | `#0…#47` in chat is unusable; often "pick 2" |
| Upgrade (`FromDeckForUpgrade`, 7) | rest-site **Smith** (every campfire), events Aroma of Chaos / Sapphire Seed / Spirit Grafter / Trial | whole deck | as above — and Smith is the single most frequent decision in a run |
| Transform (`FromDeckForTransformation`, 9) | Astrolabe (3 cards), events Morphic Grove / Symbiote / Whispering Hollow / Endless Conveyor | whole deck | as above |
| Enchant (`FromDeckForEnchantment`, 16) | events Grave of the Forgotten / Self-Help Book / Stone of All Time / Waterlogged Scriptorium / Wood Carvings… | whole deck (filtered) | as above |
| Duplicate (`FromDeckGeneric`) | **Dolly's Mirror** (pick 1 deck card to clone) | whole deck | as above — Surfinite's instinct that it "feels like Hefty Tablet" is about the *outcome* (a card is added); the *choice* is over the deck |
| Shop card removal | bespoke `DoLocalMerchantCardRemoval` | whole deck | as above, plus it's a gold decision |
| In-combat pile / hand picks (`FromCombatPile` 18, `FromHand*` 34) | Tutor / Seance / Wish / Gambling Chip / Armaments-style effects | pile or hand | mid-combat; dozens per fight |

Verdict for the ruling: deck manipulation is a different *kind* of decision (which of my cards) with
an option set chat can't realistically enumerate; ruling it out as a category is defensible on UX alone.

---

## 5. Tiers D/E — shop, and no-choice card grants

- **Shop purchases** — 7 cards on offer (5 character: 2 Attack/2 Skill/1 Power, one on sale; 2 colorless),
  bespoke path (`MerchantEntry.OnTryPurchaseWrapper` → `MerchantCardEntry.OnTryPurchase`). A vote here
  is "spend the streamer's gold" and interleaves with relic/potion buys — a genuinely different
  feature (buying-vote), not a card-reward vote. Cost: **hard**, and Tristan should decide whether
  it's even in scope conceptually.
- **No-choice grants** (nothing to vote on, listed so the inventory is complete): curses from ~12
  events, Neow's Bones/Torment/Sacrifice, AllStar / Insanity / Specialized modifiers, Clone rest-site
  option, random adds from ~10 events (Bugslayer, Byrdonis Nest, Tinker Time, Zen Weaver…),
  Neow's Talisman (auto-upgrades last Strike/Defend). Neow and the seven Ancients offer **relics
  only** — no Ancient option opens a card choice.

---

## 6. Why some of this is hard: the vote-shape problem

The mod's vote primitive is "one message = one option, 0-indexed, pick the winner". That maps to:

| Choice shape | Instances | Vote fit |
|---|---|---|
| pick 1 of ≤3 (or 1 pack of 2) | all of Tier A, Hefty/Lead/Massive/Scroll Boxes/Brain Leech-Share, all of 3b | **fits today's machinery** |
| pick 1 of 5–8 | Choices Paradox, Brain Leech-Share (5) | fits (popup layout stretches) |
| pick exactly K of N (K>1) | Cheese 2-of-8, Sealed Deck 10-of-30, Cook/Empty Cage remove-2 | **needs a new vote type**: K sequential rounds (Sealed Deck = 10 votes ≈ 5 min), or a ranked/approval ballot the mod doesn't have |
| pick any subset | Sea Glass 0..15 | no reasonable chat shape; either "vote per card in/out" (15 votes) or streamer-only |
| pick from your deck (20–60) | all of Tier C | option enumeration unreadable in chat; would need a paged/search UI — a different product |

---

## 7. Candidate rulings (pick one, or use as a menu)

| # | Line | Adds vs today | Implementation (this mod) |
|---|---|---|---|
| R1 | **Combat wins only** (v0.3.0 default) | — | done |
| R2 | **Every `CardReward` screen** (toggle off) | Kaleidoscope, Orrery, Glass Eye, Lost Coffer, Dream Catcher, 5 events, Draft | done |
| R3 | **Any time the game offers you NEW cards outside combat and you pick ONE** | R2 + Hefty Tablet, Lead Paperweight, Massive Scroll, Scroll Boxes, Brain Leech-Share | **Medium**: one new screen patch (ChooseACard) + one small (Bundle) + grid single-pick; ~1 slice. Covers everything Tristan has named so far. |
| R4 | R3 + multi-pick new-card grids | + Cheese, Sea Glass, Sealed Deck | **Hard**: needs a pick-K vote type; Sea Glass has no good shape. Recommend excluding these three by name even under R3. |
| R5 | R3/R4 + mid-combat new-card choices | + potions, Discover cards, Toolbox, Choices Paradox, Knowledge Demon | Cheap technically, **costly in pacing**; StS1 mod never did it. Recommend explicit "no". |
| R6 | Any card decision including deck manipulation | + Smith, remove/transform/enchant, Dolly's Mirror, shop removal | Different product (deck-browsing vote UI). Recommend "no" as a category. |
| R7 | Shop purchases | buying votes | Separate feature; decide separately. |

**Suggested crisp line to offer Tristan (R3 with the three named carve-outs):**
> Chat/the Saboteur decides whenever the game shows the streamer a small set of **new** cards to **add
> to the deck**, **outside combat**, and the pick is a **single card or single pack**. Multi-card grids
> (Sea Glass, Sealed Deck, Room Full of Cheese "Gorge") and anything that asks "which of *your* cards"
> stay with the streamer.

That line is implementable in one slice, matches the intuition in the stream quote ("you should be
able to pick the rare card" — Hefty Tablet), and keeps the two genuinely awkward families out by name
rather than by an unstated rule.

---

## 8. Quick reference for the message (one line per thing Tristan might ask about)

| Thing | What happens | Votes today (Slay, default) | Under R3 | Cost |
|---|---|---|---|---|
| Post-combat card reward | pick 1 of 3 | yes | yes | — |
| Kaleidoscope | 2 × pick 1 of 3 (other characters' cards) | no (yes with combat-only off) | yes | — |
| Orrery | 5 × pick 1 of 3 | no (yes with combat-only off) | yes | — |
| Glass Eye / Lost Coffer / Dream Catcher | pick 1 of 3 (×5 / ×1 / ×1) | no (yes with combat-only off) | yes | — |
| Future of Potions / Trial / Brain Leech-Rip / Colorful Philosophers / Crystal Sphere | event: pick 1 of 3 | no (yes with combat-only off) | yes | — |
| **Hefty Tablet** | pick 1 of 3 rares (skip = Injury) | **no** | **yes** | medium (unlocks the family) |
| **Lead Paperweight** | pick 1 of 2 colorless | **no** | **yes** | free after Hefty Tablet |
| **Scroll Boxes** | pick 1 of 2 packs of 3 | **no** | **yes** | medium-small |
| Massive Scroll | pick 1 of 3 (MP only) | no | yes | free |
| Brain Leech-Share Knowledge | pick 1 of 5 | no | yes | medium |
| Room Full of Cheese-Gorge | pick **2** of 8 | no | carve-out | hard |
| **Sea Glass** | pick **any** of 15 | no | carve-out | hard / no good shape |
| Sealed Deck | pick **10** of 30 | no (documented) | carve-out | hard |
| Draft modifier | 10 × pick 1 of 3 | no (yes with combat-only off) | yes | — |
| Potions / Discovery / Splash / Quasar / Abundance | mid-combat pick 1 of 3 | no | no (R5) | cheap but pauses combat |
| Toolbox / Choices Paradox | turn-1 pick | no | no (R5) | as above |
| Knowledge Demon curse pick | enemy turn, pick 1 of 2 curses | no | no (R5) | as above |
| **Dolly's Mirror** | clone 1 card **from your deck** | no | no (R6) | deck-browsing UI |
| Smith / Cook / Astrolabe / Empty Cage / transform & enchant events | your deck | no | no (R6) | deck-browsing UI |
| Shop | buy from 7 / remove 1 | no | no (R7) | separate feature |
| Thieving Hopper card back / Lantern Key card | accept-or-leave, 1 fixed card | no | no (nothing to choose) | — |

---

## 9. Verification greps (fresh decompile; re-run after game updates)

```
grep -rn "new CardReward("        decompiled/sts2-v0.111.0   # Tier A sites (16 live + Reward.FromSerializable)
grep -rn "new SpecialCardReward(" decompiled/sts2-v0.111.0   # 2 live + FromSerializable
grep -rln "CardSelectCmd.FromChooseACardScreen(" decompiled/sts2-v0.111.0   # 13
grep -rln "CardSelectCmd.FromChooseABundleScreen(" decompiled/sts2-v0.111.0 # 1 (ScrollBoxes)
grep -rln "CardSelectCmd.FromSimpleGridForRewards(" decompiled/sts2-v0.111.0 # 3 (SeaGlass, SealedDeck, EventModel helper)
grep -rn "OfferCustom(" decompiled/sts2-v0.111.0             # 24 callers; card-bearing ones are in §2
```
