# Voter Enemy Names — design spec

2026-08-28. Slice prefix: `voter-names/N`. Feature parity target: Tempus's StS1
`MonsterNamesPatch` + `MonsterMessageRepeater` (see
`references/SlayTheStreamer-sts1/`), adapted to StS2 and to Surfinite's
fairness rules (which deliberately differ from the original's weighted scheme).

## 1. Summary

Enemy creatures in combat are named after chat voters. The username renders
below the enemy's intent icons (which shift up to make room), in the same font
and style as the vanilla hover nameplate, bobbing in sync with the first
intent icon. Names are drawn from the pool of everyone who has voted this
game session, uniformly at random among the least-used names — nobody gets a
second enemy until every distinct voter has had one. Reuses are decorated
"Name Jr." then "Name III/IV/…".

Bonus (separately toggleable): while a voter's name is on a living enemy,
that voter's chat messages appear as vanilla speech bubbles from that enemy.

Both features are pure cosmetics: **zero game-state mutation anywhere**. Any
failure degrades to vanilla visuals, never to broken combat.

## 2. Settings

Two additive keys (record + parse + template + writer + panel; no
schemaVersion bump; same discipline as `combatCardVotesOnly`):

- `nameEnemiesAfterVoters` (bool, **default true**) — checkbox
  **"Name enemies after chat voters"**. Help text: names come from chatters
  who vote; everyone gets one before anyone gets a second.
- `namedEnemiesSpeak` (bool, **default true**) — checkbox
  **"Named enemies repeat their voter's chat"**, indented conceptually under
  the first (plain sibling row in the panel, help text notes it only applies
  while naming is on). Help text also carries the moderation note: bubble
  content is the chatter's raw message — the streamer's channel moderation is
  the only content filter.

Both read live at use time (the `ModSettings.Current` volatile-read pattern).
`namedEnemiesSpeak` is effective-AND with `nameEnemiesAfterVoters` — no name,
no bubble.

README gets a feature section + the moderation caveat line.

## 3. Voter capture — Ti layer (`VoteSession`)

`VoteSession` currently tallies per `ChatMessage.VoterKey` but discards
identities. Addition:

- `private readonly Dictionary<string, string> _displayNamesByKey` populated
  in `OnChatMessage` beside the tally write (`_displayNamesByKey[key] =
  msg.DisplayName`). Bounded by the existing `MaxVoters` cap — no new cap.
- `public IReadOnlyDictionary<string, string> VoterDisplayNames` snapshot
  accessor (copy-on-read, same style as existing snapshot surfaces), valid in
  any state — callers harvest on `Closed`/`Cancelled` (cancelled votes still
  count as "voted" for the pool: the person engaged).

Game-agnostic, no Godot/game types — safe for the TI extraction goal.
So the pool has ONE harvest point instead of a subscription scattered across
four vote patches, `VoteCoordinator.Start` gains a game-agnostic
`SessionStarted` event (raised with the new session); the pool hook subscribes
once at ModEntry wiring time and attaches to each session's
`Closed`/`Cancelled`. Existing tests construct coordinators via
`VoteSessionTestBase` and are unaffected by an additive event.

## 4. Fairness pool — `VoterNamePool` (pure, unit-tested)

`src/Game/DecisionVotes/VoterNamePool.cs` — pure BCL class riding the test
csproj glob (the `CurseRoll` precedent). Session-lifetime static state owned
by the game side (instance class + static holder, constructor-injectable
`Random` for tests).

State: `Dictionary<string voterKey, (string DisplayName, int UsedCount)>`.

- `AddVoters(IReadOnlyDictionary<string,string> voterDisplayNames)` — merge;
  existing keys update their display name (people can rename), UsedCount
  preserved. Called by a small hook on every vote session close/cancel.
- `TryTakeName(out string decorated, out string voterKey)` — uniform-random
  among entries with the **minimum UsedCount** (strict fairness: no second
  goes until every distinct voter is at count ≥ 1), increments the winner's
  count, decorates by the *new* count: 1 → bare name, 2 → `"{name} Jr."`,
  n ≥ 3 → `"{name} {RomanNumeral(n)}"`. Returns false when the pool is empty
  (feature silently absent — vanilla look until the first vote of the
  session).
- Roman-numeral helper capped at a sane bound (pool math makes >dozens
  implausible; original capped at 3999).

Display-name hygiene at add time: trim; drop empty; hard cap 25 chars with
ellipsis (YouTube display names can be long; Twitch logins max 25).

## 5. Name assignment + rendering — game side

New game-side files (Compile-Remove'd in the test csproj):
`VoterNamesPatch.cs` (patches + assignment) and `VoterNameLabel.cs` (the
Godot label node) in `src/Game/DecisionVotes/` (or `src/Game/Ui/` for the
label — follow the PortraitFit precedent at implementation time).

### Assignment

- Per enemy creature node, on first layout: draw from `VoterNamePool`.
  Store `NCreature → (voterKey, decoratedName)` in a `ConditionalWeakTable`
  (or on the label node itself). Bosses and mid-combat summons included;
  players excluded (`Entity.Monster != null` discriminates).
- Name held for the creature node's lifetime. Death/teardown needs no
  cleanup: the label is a child of the creature node, freed by Godot.
- Save-quit → Continue mid-combat: fresh draws (pool is in-memory;
  accepted 2026-08-28 with the session-scope decision).
- Multiplayer: bail (players count > 1), same probe as the other patches.

### Rendering (`VoterNameLabel`)

- A `Label` (vanilla `Label`, not MegaRichTextLabel — see the
  SettingsPanelBuilder rationale) added as a **sibling** of
  `NCreature.IntentContainer` — deliberately NOT inside it: vanilla's
  `UpdateIntent` culls the container's extra children every turn
  (NCreature.cs:426 `TakeLast` removal).
- Style copied from the vanilla hover nameplate (`creature_state_display.tscn`
  `NameplateLabel`): font `res://themes/kreon_regular_glyph_space_one.tres`,
  size 24, color `(1, 0.964706, 0.886275)`, shadow black 25% offset (2,1).
  Shrink-to-fit for long names (drop font size toward 12 when width would
  exceed ~intent-group width; the 25-char cap already bounds the worst case).
- Position: postfix on `NCreature.UpdateBounds(Node boundsContainer)`
  (NCreature.cs:382-395, the site that lays `IntentContainer` out from the
  per-creature `IntentPos` Marker2D):
  - shift `IntentContainer.Position.Y` up by `IntentShiftPx` (~40; tune in
    operator validation — original StS1 used 42),
  - place the label centered at the container's original position (i.e., in
    the freed space below the icons), applying the same `Visuals.Scale`
    handling as the vanilla lines above it.
  - Postfix only runs the shift/label when setting on AND a name is assigned;
    setting toggled off mid-run → next `UpdateBounds` pass removes the label
    and leaves vanilla layout (UpdateBounds recomputes position from the
    marker each call, so no restore bookkeeping is needed).
- Bob: label's `_Process` replicates vanilla's exact formula
  (`Position = base + Up * (sin(Time.GetTicksMsec()*0.001*π + phase)*10 + 8)`)
  with phase = the creature node's `GetHashCode()*0.01` — identical phase to
  vanilla's first intent icon (NCreature.cs:419-422), so the label bobs in
  lockstep with it.
- Visibility mirroring: each `_Process`, copy `IntentContainer.Modulate`
  alpha/visibility onto the label — vanilla hides/fades intents during enemy
  attacks and fast-mode; the name must never float over a mid-swing enemy.
  This also inherits combat-end/death fades for free.

## 6. Speech bubbles — `VoterSpeechPatch`

- Game-side subscriber to the chat layer's `MessageReceived` (the
  `IChatConsumer` event the coordinator already exposes). On message:
  1. Bail fast: either setting off, MP, no combat, no named creatures.
  2. Match `msg.VoterKey` against living named creatures (exact key match —
     robust across Jr./Roman decorations and both platforms; better than the
     original's name-string comparison).
  3. Sanitize: strip/escape `[` (the bubble label renders BBCode — chatters
     must not inject tags), collapse whitespace, truncate to 64 chars at a
     word boundary + `"..."` (original's rule).
  4. Per-creature cooldown (~5s) and one-bubble-at-a-time so a chatty voter
     can't wallpaper the screen.
  5. `dispatcher.Post` to main thread → replicate `TalkCmd.Play`'s body with
     the plain string: `IsDead` guard, duration = `max(0.5, rawChars * 0.12)`
     (0.10 in Fast mode), `NSpeechBubbleVfx.Create(text, creature, duration,
     VfxColor.White)`, `speaker.GetVfxContainer()?.AddChildSafely(...)`.
     (`TalkCmd.Play` itself takes a table-based `LocString`; the underlying
     `Create` is public and string-based — verified v0.111.0.)
- Occlusion: skip bubble creation while a vote popup/overlay is up
  (`OverlayOcclusion.IsOccludingOverlayVisible`) — bubbles behind the popup
  would expire unseen anyway.

## 7. Error handling and edge cases

- Every patch body try/catch → `TiLog.Warn`/`Error`; failure = vanilla
  visuals. No path touches run state, save state, or combat logic.
- Patch registration failures (game update renames a member): `Prepare` hard
  checks log one Error and skip registration; both features silently absent.
  No degrade-to-broken mode exists because there is nothing downstream.
- Empty pool → no label, no shift? **Decision: shift only when a label is
  placed** — an unnamed enemy keeps fully vanilla layout (mixed combats can
  briefly show shifted+named beside vanilla+unnamed if the pool runs dry
  mid-assignment; acceptable, signals the pool state honestly).
- Duplicate display names (two platforms, same display name): keys differ,
  both live in the pool; the second placement of the *string* may look like a
  repeat without "Jr.". Accepted — key-level fairness is what's guaranteed.
- The `Creature` handle for bubbles comes from the creature node's `Entity`;
  bubble creation re-checks `IsDead` and node validity on the main thread.
- Chat messages that are themselves votes (`#N`) still repeat as bubbles
  (they voted, the enemy "speaks" their vote — matches original; cheap to
  exclude later if it reads as noise).

## 8. Testing

- Unit (TDD): `VoterNamePool` — fairness (min-count selection, no second
  goes until exhaustion), decoration sequence (bare → Jr. → III → IV),
  rename-updates-display-name, cap/trim hygiene, empty-pool false,
  seeded-Random determinism. Settings parse blocks for both keys.
  Bubble sanitizer (pure string helper, rides test glob): BBCode escape,
  truncation at word boundary, whitespace collapse.
- `[Collection("TiLog.Sink")]` on any test class that can trip TiLog.
- Operator validation matrix (notes/06): names appear post-first-vote;
  fairness observed across combats; Jr. appears only after pool exhaustion
  (test with 1-2 voters); intent shift + bob sync; multi-intent moves
  (MultiAttack + Buff) center correctly; boss + summon naming; name hides
  during enemy attack animation; hover nameplate unaffected; bubble fires
  from the right enemy, cooldown works, `[`-injection neutralized, 64-char
  truncation; both toggles off → full vanilla; setting flips mid-combat;
  save-quit → Continue renames; MP bail; YT + Twitch voters both appear.

## 9. Out of scope (YAGNI)

- The original's "the \<Title\>" second line (decided out 2026-08-28).
- Persistent (cross-session) pool state.
- Content filtering beyond BBCode-escape/truncation (channel moderation is
  the filter — explicit stance, documented in README).
- Weighted selection by vote frequency (original's scheme; Surfinite's
  strict-fairness rule replaces it).
- Renaming the underlying `MonsterModel` (tooltips/log keep vanilla names;
  our name is an overlay only — deliberate divergence from StS1, keeps zero
  game-state contact).

## 10. Decisions log

- 2026-08-28 (Surfinite): default ON for both settings; bundle release with
  the `combatCardVotesOnly` default flip (no v0.2.3; ships as v0.3.0).
- 2026-08-28 (Surfinite): Jr./Roman decorations; session-lifetime pool;
  nothing shown on empty pool; usernames considered platform-vetted (explicit
  risk acceptance); bubbles included with own toggle after feasibility check.
- 2026-08-28 (design): overlay label instead of model rename; sibling-of-
  container attachment with replicated bob (vanilla culls container
  children); strict min-count fairness instead of StS1 weighting.
