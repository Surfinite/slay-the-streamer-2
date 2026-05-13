# B.2.2 Ancient Vote Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Widen the existing `NeowBlessingVotePatch` so chat votes apply to all 6 mid-run Ancients (Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu) in addition to Neow, via inheritance-based predicate detection on `AncientEventModel`.

**Architecture:** Single Harmony patch (`AncientVotePatch`, renamed from `NeowBlessingVotePatch`) attaches to `NEventRoom.OptionButtonClicked` and bails to vanilla unless the active event model `is AncientEventModel and not DeprecatedAncientEvent`. Vote title is derived from `eventModel.Title.GetFormattedText()` (e.g. "Pael's Offering"). All other voting machinery (run-id guard, single-option skip, multiplayer bail, resume-on-main-thread, run-state liveness checks) is reused verbatim from B.1.

**Tech Stack:** C# 12, .NET 8, Harmony 2.x patching against `sts2.dll` (Godot-compiled MegaCrit binary).

**TDD note:** Unit tests for the patch predicate are **not possible** in this codebase. The test csproj at [`tests/slay_the_streamer_2.tests.csproj:23-26`](../../../tests/slay_the_streamer_2.tests.csproj#L23-L26) explicitly excludes Harmony-patch files (they reference `MegaCrit.Sts2.*` types unavailable to the test project). Same constraint applied to B.1 and was accepted then. The "green bar" for each task is `dotnet test` (existing tests stay green — mechanical regression) plus successful `dotnet build`. Functional verification is the operator-validation gate in T5. This is a documented departure from strict TDD, consistent with prior voting slices.

**Reference design spec:** [`docs/superpowers/specs/2026-05-13-plan-b-2-2-ancient-vote-design.md`](../specs/2026-05-13-plan-b-2-2-ancient-vote-design.md)

---

## File structure

| File | Responsibility | Action |
|---|---|---|
| `src/Game/DecisionVotes/AncientVotePatch.cs` | Harmony patch + vote orchestration for Neow + all ancients | Rename from `NeowBlessingVotePatch.cs`; modify class name, log tags, predicate, title derivation |
| `src/ModEntry.cs` | Mod bootstrap, Harmony PatchAll trigger | Update comment on line 177 |
| `tests/slay_the_streamer_2.tests.csproj` | Test project config | Update `Compile Remove` path on line 23 |
| `notes/06-followups-and-deferred.md` | Acceptance-gate results log | Append operator-validation results in T5 |

No new files; no test files. Only one source file undergoes substantive change.

---

## Task 1: Rename patch class + file + update references

**Goal:** Rename `NeowBlessingVotePatch` → `AncientVotePatch` (file + class + all references) as one atomic commit. Pure rename; no behavior change. Verifies the slice's foundation compiles before any logic changes.

**Files:**
- Rename: `src/Game/DecisionVotes/NeowBlessingVotePatch.cs` → `src/Game/DecisionVotes/AncientVotePatch.cs`
- Modify: `src/ModEntry.cs:177` (comment)
- Modify: `tests/slay_the_streamer_2.tests.csproj:23` (`Compile Remove` path)

- [ ] **Step 1: Rename the patch file using `git mv`**

```bash
git mv src/Game/DecisionVotes/NeowBlessingVotePatch.cs src/Game/DecisionVotes/AncientVotePatch.cs
```

Expected: no output; file moved + staged.

- [ ] **Step 2: Rename the class declaration**

In `src/Game/DecisionVotes/AncientVotePatch.cs`, change line 22:

```csharp
internal static class NeowBlessingVotePatch {
```

to:

```csharp
internal static class AncientVotePatch {
```

- [ ] **Step 3: Update the comment in `src/ModEntry.cs`**

In `src/ModEntry.cs:177`, change:

```csharp
            //    NeowBlessingVotePatch attaches here via PatchAll.
```

to:

```csharp
            //    AncientVotePatch attaches here via PatchAll.
```

- [ ] **Step 4: Update the `Compile Remove` path in the test csproj**

In `tests/slay_the_streamer_2.tests.csproj:23`, change:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\NeowBlessingVotePatch.cs" />
```

to:

```xml
    <Compile Remove="..\src\Game\DecisionVotes\AncientVotePatch.cs" />
```

- [ ] **Step 5: Build to verify the rename is consistent**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds. No references to `NeowBlessingVotePatch` should remain in source.

- [ ] **Step 6: Run tests to verify nothing regressed**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass (this rename touches no test-reachable code).

- [ ] **Step 7: Verify no stray references remain**

```bash
git grep -n "NeowBlessingVotePatch"
```

Expected: matches only inside `docs/superpowers/specs/*` and `docs/superpowers/plans/*` and `notes/*` (historical design docs) — none in `src/` or `tests/`.

- [ ] **Step 8: Commit**

```bash
git add src/Game/DecisionVotes/AncientVotePatch.cs src/ModEntry.cs tests/slay_the_streamer_2.tests.csproj
git commit -m "$(cat <<'EOF'
plan-b-2-2/1.1: rename NeowBlessingVotePatch -> AncientVotePatch

Pure rename in preparation for predicate-widening to cover all 6 mid-run
Ancients. Updates the .cs file name, the class declaration, the comment in
ModEntry.cs, and the test csproj Compile Remove path. No behavior change.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Rename log tags from `[neow-vote]` to `[ancient-vote]`

**Goal:** Cosmetic rename of the log-tag prefix in all 33 log call sites inside `AncientVotePatch.cs`. No behavior change; only the strings inside `TiLog.*` calls change.

**Files:**
- Modify: `src/Game/DecisionVotes/AncientVotePatch.cs` (33 occurrences of `[neow-vote]`)

- [ ] **Step 1: Replace all `[neow-vote]` strings with `[ancient-vote]`**

Use sed (or your editor's replace-all) on the patch file:

```bash
sed -i 's/\[neow-vote\]/[ancient-vote]/g' src/Game/DecisionVotes/AncientVotePatch.cs
```

Note: on Windows PowerShell without GNU sed, use:

```powershell
(Get-Content src/Game/DecisionVotes/AncientVotePatch.cs) -replace '\[neow-vote\]', '[ancient-vote]' | Set-Content src/Game/DecisionVotes/AncientVotePatch.cs
```

- [ ] **Step 2: Verify the count of replacements**

```bash
git grep -c "\[ancient-vote\]" src/Game/DecisionVotes/AncientVotePatch.cs
```

Expected: `33` (matches the original count of `[neow-vote]` occurrences).

- [ ] **Step 3: Verify no stale `[neow-vote]` tags remain in source**

```bash
git grep -n "\[neow-vote\]" src/
```

Expected: no output.

- [ ] **Step 4: Build to verify no string interpolation broke**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds.

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/AncientVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-2-2/2.1: rename log tags [neow-vote] -> [ancient-vote]

Cosmetic rename across 33 log call sites in AncientVotePatch.cs. Reflects
the patch's widened scope (all ancients, not just Neow). No behavior change.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Widen the predicate to inheritance-based detection

**Goal:** Replace `IsNeowEvent` (which only matched `Neow`) with `IsAncientEvent` (matches any `AncientEventModel` subclass except `DeprecatedAncientEvent`). This is the substantive behavioral change of the slice — after this task, the patch fires for all 6 ancients.

**Files:**
- Modify: `src/Game/DecisionVotes/AncientVotePatch.cs` (add using directive; replace `IsNeowEvent` definition; update 2 call sites)

- [ ] **Step 1: Add the `Entities.Ancients` using directive**

In `src/Game/DecisionVotes/AncientVotePatch.cs`, add a new `using` line so `AncientEventModel` resolves. The existing imports (lines 1-17) include `MegaCrit.Sts2.Core.Events` and `MegaCrit.Sts2.Core.Models.Events`, but `AncientEventModel` lives in `MegaCrit.Sts2.Core.Entities.Ancients`. After line 13 (`using MegaCrit.Sts2.Core.Runs;`), add:

```csharp
using MegaCrit.Sts2.Core.Entities.Ancients;
```

The block of `MegaCrit.*` usings should then read (preserving alphabetical-ish order):

```csharp
using MegaCrit.Sts2.Core.Entities.Ancients;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
```

- [ ] **Step 2: Replace the `IsNeowEvent` method definition**

In `src/Game/DecisionVotes/AncientVotePatch.cs`, find:

```csharp
    private static bool IsNeowEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is Neow;
    }
```

Replace with:

```csharp
    private static bool IsAncientEvent(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room);
        return eventModel is AncientEventModel and not DeprecatedAncientEvent;
    }
```

- [ ] **Step 3: Update the call site inside `Prefix`**

Find:

```csharp
        if (!IsNeowEvent(__instance)) return true;
```

Replace with:

```csharp
        if (!IsAncientEvent(__instance)) return true;
```

- [ ] **Step 4: Update the call site inside `ResumeOnMainThread`**

Find:

```csharp
            if (!IsNeowEvent(room)) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume: active event is no longer Neow; dropping resume");
                return;
            }
```

Replace with:

```csharp
            if (!IsAncientEvent(room)) {
                TiLog.Warn("[SlayTheStreamer2][ancient-vote] resume: active event is no longer an ancient; dropping resume");
                return;
            }
```

(The log message body is also updated since "Neow" is no longer accurate.)

- [ ] **Step 5: Verify no stale `IsNeowEvent` references remain**

```bash
git grep -n "IsNeowEvent" src/
```

Expected: no output.

- [ ] **Step 6: Build to verify resolution**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds. If `AncientEventModel` or `DeprecatedAncientEvent` fails to resolve, the using directive in Step 1 was missed or misplaced — re-check.

- [ ] **Step 7: Run tests**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Game/DecisionVotes/AncientVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-2-2/3.1: widen predicate to AncientEventModel inheritance

Replaces IsNeowEvent (which only matched Neow) with IsAncientEvent, which
matches any AncientEventModel subclass except DeprecatedAncientEvent. Patch
now fires for Pael, Tezcatara, Orobas, Nonupeipe, Tanx, Vakuu in addition
to Neow. Updates both call sites (Prefix gate and ResumeOnMainThread
liveness check) and the resume log message.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Derive vote title from event-model name

**Goal:** Replace the hardcoded vote title `"Neow's Bonus"` with `$"{eventModel.Title.GetFormattedText()}'s Offering"`, so each ancient's vote gets a per-ancient title in chat receipts (e.g. "Pael's Offering", "Tezcatara's Offering"). Neow becomes "Neow's Offering" (small cosmetic regression accepted per the spec).

**Files:**
- Modify: `src/Game/DecisionVotes/AncientVotePatch.cs` (add helper; update one call site)

- [ ] **Step 1: Add the `GetVoteTitle` helper method**

In `src/Game/DecisionVotes/AncientVotePatch.cs`, locate the `IsAncientEvent` method (added in Task 3). Immediately after it, add a new private static helper:

```csharp
    private static string GetVoteTitle(NEventRoom room) {
        var eventModel = _eventField.Value?.GetValue(room) as EventModel;
        var name = eventModel?.Title.GetFormattedText() ?? "Ancient";
        return $"{name}'s Offering";
    }
```

The fallback `"Ancient"` covers the impossible-but-defensive case where reflection succeeds at `object` but the cast to `EventModel` fails. `EventModel` is already resolvable via the existing `using MegaCrit.Sts2.Core.Models;`.

- [ ] **Step 2: Update the `coordinator.Start(...)` call site**

In `src/Game/DecisionVotes/AncientVotePatch.cs`, find:

```csharp
            session = coordinator.Start("Neow's Bonus", labels, TimeSpan.FromSeconds(30));
```

Replace with:

```csharp
            session = coordinator.Start(GetVoteTitle(__instance), labels, TimeSpan.FromSeconds(30));
```

- [ ] **Step 3: Verify no stale `"Neow's Bonus"` references remain**

```bash
git grep -n "Neow's Bonus" src/
```

Expected: no output.

- [ ] **Step 4: Build to verify the helper resolves**

```bash
dotnet build src/slay_the_streamer_2.csproj
```

Expected: build succeeds.

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/slay_the_streamer_2.tests.csproj
```

Expected: all existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Game/DecisionVotes/AncientVotePatch.cs
git commit -m "$(cat <<'EOF'
plan-b-2-2/4.1: derive vote title from event-model name

Replaces hardcoded "Neow's Bonus" with a GetVoteTitle helper that returns
$"{eventModel.Title.GetFormattedText()}'s Offering". Each ancient now gets
a per-event vote title in chat receipts. Neow becomes "Neow's Offering"
(small cosmetic regression accepted per the design spec).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Build, install, operator-validation gate

**Goal:** Build the mod, install it into the Steam game folder, manually validate via in-game play that chat votes apply for Neow + all 6 ancients, record results, and tag the slice complete.

**Files:**
- Run: `build.ps1`, `install.ps1`
- Modify: `notes/06-followups-and-deferred.md` (append acceptance-gate results)
- Tag: `plan-b-2-2-complete`

- [ ] **Step 1: Full build**

```powershell
pwsh -File build.ps1
```

Expected: `dist/` rebuilt; `dotnet test` green inside the script. If `build.ps1` fails, fix the underlying issue — do NOT skip.

- [ ] **Step 2: Install to game folder**

```powershell
pwsh -File install.ps1
```

Expected: `dist/` copied to `<steam>/Slay the Spire 2/mods/slay_the_streamer_2/`. Per [CLAUDE.md](../../../CLAUDE.md): both build.ps1 AND install.ps1 are required after code changes. The mod hash logged at runtime startup will match `git log -1 --format=%H` if the steps were done in the right order.

- [ ] **Step 3: Validate Neow regression**

Start a fresh standard run in Slay the Spire 2 with chat connected (Twitch and/or YouTube per the JSON config at `%APPDATA%\SlayTheSpire2\slay_the_streamer_2.json`).

At the Neow event, expect:
- Chat receipt: "Voting on Neow's Offering: #0 ...". (Note the change from "Neow's Bonus" — this is intentional per the design spec.)
- Chat `#N` votes apply; the winning option is chosen even when the streamer's click differs.

Check `%APPDATA%\SlayTheSpire2\logs\godot.log` for `[ancient-vote]` lines confirming the vote opened and the resume fired. There should be NO `[neow-vote]` lines.

If this step fails, the rename/predicate is broken — fix before continuing.

- [ ] **Step 4: Validate each Act 2 ancient (Pael, Tezcatara, Orobas)**

One run per ancient, save-and-reload to the chest-room event if needed to retry. For each:
- Confirm vote opens with title "`{Name}`'s Offering" (e.g. "Pael's Offering").
- Confirm chat votes apply; winning relic granted.
- Confirm `[ancient-vote]` log lines fire.

Note: Orobas is gated on the `OrobasEpoch` unlock — if Orobas doesn't appear, that's vanilla gating, not a bug. Try a different seed/character to surface it, or skip and note in the results.

- [ ] **Step 5: Validate each Act 3 ancient (Nonupeipe, Tanx, Vakuu)**

Same as Step 4 but for Act 3 ancients. No epoch gates on Act 3 ancients.

- [ ] **Step 5.5: Validate Darv (cross-act, `AllSharedAncients`)**

Darv was missed during initial research and added to the plan after T3's code review identified it. It's a 7th `AncientEventModel` subclass declared at [`decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Darv.cs`](../../../decompiled/sts2/MegaCrit/sts2/Core/Models/Events/Darv.cs), exposed via `ModelDb.AllSharedAncients` rather than any per-act `AllAncients` list. Gated on `DarvEpoch` — only appears once that epoch is revealed.

If Darv doesn't appear in your save profile (epoch not revealed), that's vanilla gating, not a bug. Note in the results either way — the inheritance-based predicate handles Darv automatically, so encountering it once confirms the slice is truly inheritance-complete rather than allow-list-incomplete.

- [ ] **Step 6: Validate trolling override (any one ancient)**

On any ancient vote: streamer clicks option A in-game, chat votes for option B. Expected: winning option is B (chat overrides streamer's click). Confirms the suspend-and-resume flow works for the new event types.

- [ ] **Step 7: Record results in notes/06-followups-and-deferred.md**

Append a section to `notes/06-followups-and-deferred.md` documenting:
- Date of validation.
- Which ancients were validated (with vote-title text seen).
- Any ancients NOT validated (e.g. Orobas didn't roll) and the reason.
- Confirmation that no `[neow-vote]` log lines remain.
- Pass/fail for the trolling-override test.

Follow the existing structure used for B.1 / B.2.1 / yt-chat acceptance-gate entries in that file.

- [ ] **Step 8: Commit acceptance-gate results**

```bash
git add notes/06-followups-and-deferred.md
git commit -m "$(cat <<'EOF'
plan-b-2-2/5.1: record acceptance-gate results

Operator-validation passed for Neow + Act 2/Act 3 ancients + trolling
override. Recorded in notes/06-followups-and-deferred.md per the standard
slice-completion pattern.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 9: Tag the slice complete**

```bash
git tag plan-b-2-2-complete
git push origin main --tags
```

Expected: tag pushed; the slice is now considered shipped.

- [ ] **Step 10: Update README.md to reflect B.2.2 completion**

In `README.md`, locate the "Remaining v0.1 slices" line (around line 14) which currently lists `B.2.2 start-of-act Ancient-rarity relic vote ...`. Remove `B.2.2` from the remaining-slices list and add a shipped-line entry above (mirror the format of the B.1 / B.2.1 lines at lines 10-11):

```markdown
- **B.2.2 ancient vote** shipped 2026-MM-DD (`plan-b-2-2-complete` tag) — chat votes on the Ancient-rarity relic offered by Pael, Tezcatara, Orobas (Act 2), Nonupeipe, Tanx, Vakuu (Act 3), and Darv (cross-act, via `AllSharedAncients`), via the same `NEventRoom.OptionButtonClicked` patch that handles Neow's blessing.
```

Replace `2026-MM-DD` with today's actual date.

- [ ] **Step 11: Commit the README update**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
plan-b-2-2/5.2: README — move B.2.2 from remaining to shipped

Chat votes now apply for all 6 mid-run Ancients via the predicate-widened
AncientVotePatch (renamed from NeowBlessingVotePatch).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
git push origin main
```

---

## Done criteria

- All 5 tasks' commits are on `main`.
- `plan-b-2-2-complete` tag is pushed.
- README.md no longer lists B.2.2 as remaining.
- `notes/06-followups-and-deferred.md` has an acceptance-gate entry for B.2.2.
- In-game: chat votes apply to Neow + each ancient encountered; vote-title format is `{Name}'s Offering`; log tag is `[ancient-vote]` throughout.
