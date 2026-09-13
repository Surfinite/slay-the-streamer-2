Slay the Streamer 2. Twitch (and optionally YouTube) chat votes on the streamer's choices during a Slay the Spire 2 run.

> 🎮 Tested against Slay the Spire 2 Beta `v0.111.0`. The same build loads on the default branch (`v0.107.1`), with one limitation: the non-combat card-reward rules below need a game hook the default branch doesn't have, so there every card reward is a normal pick vote.

## 🆕 New in v0.4.0

Card rewards that don't come from combat now play differently. Until now chat voted on every card reward the same way, which made Kaleidoscope and friends a long queue of identical votes and gave chat no say at all when the combat-only setting was on. Now:

- Ancient relics (Kaleidoscope, Glass Eye, Lost Coffer, Hefty Tablet, Lead Paperweight, and anything Neow's Bones pulls) and Dream Catcher use a remove-one vote. Chat votes which option to remove, Skip included, the removed one turns red, and the streamer picks from the rest. Clicking the removed option anyway costs a vote override. Driftwood's reroll is free after a removal and chat votes again on the new cards.
- Card rewards from events (Future of Potions, Colorful Philosophers, Brain Leech, Trial, Crystal Sphere) and shop relics (Orrery) can't be skipped. No vote; the streamer must take a card.
- Every affected relic and event says what will happen, in a blue "Slay the Streamer:" line on its own text.
- A new three-way setting, Non-combat card rewards, chooses between Mixed (the rules above, the default), Remove-one (a removal vote on every non-combat reward; Orrery becomes five votes), and Free (chat only votes after combat, the streamer picks everything else). The old "Card-reward votes only occur after combat" checkbox migrates automatically: On becomes Free, Off becomes Mixed.
- Draft picks are always the streamer's, in every mode. Same rule as Sealed Deck.
- The vote-override budget now shows on the Ancient vote screen too, so you can see whether an override click is available.

Sealed Deck runs with Neow's blessings available (Pikcube's Run Modifiers' Always Whale):

- Neow's Talisman upgrades 2 random cards and makes them Doomed (5 Doom to yourself each time you play one), instead of upgrading the Strikes and Defends a sealed deck doesn't have. The relic says so in game.
- Leafy Poultice and Precarious Shears are never offered in sealed runs.

Smaller things: the status text on remove-one screens no longer overlaps the cards; Escape opens the pause menu on those screens instead of pressing Skip; a rewards screen with potions left after an unskippable card reward is no longer a dead end; the override counter is centred even when Driftwood adds a Reroll button.

Existing `slay_the_streamer_2.json` files keep working. The migrated key is written back on the next settings save.

## ▶️ Demo

[Watch the v0.1 demo on Twitch](https://www.twitch.tv/videos/2782265574)

## ✨ What chat votes on

- Neow's blessing at the start of a run, and the Ancients' blessings from Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu and Darv
- Card rewards after each fight. Chat-skip is toggleable, and the streamer gets a per-act skip budget (0 / 1 / 2 / 3 / 5 / Unlimited)
- Card rewards from relics, events and rest sites, per the Non-combat card rewards setting above
- Act bosses, with animated combat-idle portraits in the vote popup. At Ascension 10 chat votes on both of the final act's bosses (optionally the same one twice)
- Act 1 variant (Underdocks or Overgrowth) when you click Embark. Toggleable

Streamer-side extras that aren't chat votes: vote overrides (with an optional curse per override), relic choices (pick 1 of up to 4 from chests and elites), and enemies named after voters, drawn by a raffle weighted by how often they vote, with their chat messages as speech bubbles.

Custom Mode's Sealed Deck and Draft both hand the deck-building picks to the streamer; chat votes once the run begins.

## 🛠 Quick install

1. Download `slay_the_streamer_2-v0.4.0.zip` from the assets below, or subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3761888849).
2. If installing manually, extract the zip into your `Slay the Spire 2/mods/` folder.
3. Launch the game once. The mod creates your settings file at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`. Open it (in game: settings, mod list, Slay the Streamer 2, Open settings folder), fill in your Twitch channel and bot credentials, and restart. The `slay_the_streamer_2.json` inside the mod folder is the manifest; credentials don't go there.
4. Pick the modded save profile.

The `username` account's votes don't count: the mod ignores that account's messages so its own receipts can't register as votes. Using your own account there is fine, but don't test voting from it. Use a second account or a friend.

Full setup guide, YouTube chat, and every setting: [README](https://github.com/Surfinite/slay-the-streamer-2#readme).

## 🤝 Mod compatibility

Tested with Slay the Relics reborn (no conflicts). Balls2, StS1 Boss Ancients and Haxxero's More Relics were checked against the new card-reward rules: their combat rewards vote normally, their custom Ancients get the Ancient vote, and More Relics' Strongbox is unskippable like Orrery.

## ⚠️ Known caveats

- Slay the Spire 2 can crash silently right after a modded launch (a game bug in its crash-reporter teardown, [reported to MegaCrit](https://github.com/megacrit/sts2-mod-uploader/issues/14)). It has been quiet for months; if it hits you, add `--force-sentry` to the game's Steam launch options.
- The modded save is its own profile. Modded runs don't count toward unlocks, and the boss vote can pick bosses you haven't unlocked. `unlock all` in the dev console (`~`) unlocks everything on the modded save.
- Twitch rate-limits receipts under heavy back-to-back voting (20 messages per 30 seconds). Votes still work; some chat messages may not appear.

## 🙏 Credits

Concept from Tempus's original StS1 [Slay the Streamer](https://github.com/Tempus/SlayTheStreamer). This is a from-scratch StS2 implementation; none of that code is used. MIT licensed.
