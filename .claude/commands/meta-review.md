# Review Intake & Meta-Review

You are now entering **Review Intake Mode** for the plan currently in context (or the plan the user is about to reference). Your job has three phases.

---

## Phase 1: Ready

- Confirm which plan you're collecting reviews for (name it, summarize it in one line).
- Tell the user you're ready to receive reviews.
- Reviews will arrive as:
  - Pasted text in chat messages
  - File paths you should read (e.g. `./reviews/reviewer-1.md`)
  - Or a directory of review files to read in bulk
- Wait for input.

## Phase 2: Intake (repeat until told to stop)

For **each review** you receive:

1. **Read it fully.** If given a file path or directory, use your tools to read the contents.
2. **Acknowledge it** with a brief summary:
   - Reviewer identifier (use a label like "Reviewer 1", "Reviewer 2", or a name if provided)
   - Top 3–5 key points they raised
   - Their overall sentiment (strongly critical, mixed, mostly positive, etc.)
3. **Categorize every piece of feedback** into a running internal tracker using these categories:
   - 🏗️ **Architecture & Design**
   - ⚠️ **Risks & Concerns**
   - 🗑️ **Suggested Removals / Simplifications**
   - ➕ **Suggested Additions / Features**
   - 🔄 **Alternative Approaches**
   - ✅ **Confirmed Good / Keep As-Is**
   - 🔧 **Implementation Details & Nits**
   - 📦 **Dependencies & Integration**
   - 🔮 **Future Considerations**
4. **After acknowledging**, tell the user the running count: "N reviews ingested. Send the next, or say **ready for meta-review** when done."

**Do NOT start the meta-review until the user explicitly says so.** Trigger phrases include: "ready", "meta-review", "go ahead", "that's all", "do the meta-review", or similar clear intent.

If the user sends something ambiguous, ask whether it's a review or an instruction.

## Phase 3: Meta-Review & Updated Plan

When the user triggers the meta-review, produce **two outputs together**: the meta-review analysis and an updated plan.

---

### Part A: Meta-Review Analysis

#### A.1 — Review Summary Table
A table of all reviewers with columns: Reviewer | Sentiment | Key Focus Areas | Unique Insight (if any)

#### A.2 — Consensus Points (agreement across multiple reviewers)
Things **2 or more** reviewers flagged. Rank by how many reviewers raised each point. These are the highest-signal items.

#### A.3 — Outlier Points (raised by only one reviewer)
Still worth considering — call out which ones you think have merit despite being singular.

#### A.4 — Category Breakdown
Go through each category from the tracker above. For each:
- List all feedback items
- Note which reviewer(s) raised it
- **Reality-check against the actual codebase.** You have full code access; the reviewers did not. If a suggestion is based on a misunderstanding of the code, incorrect assumptions about the architecture, or is infeasible given the actual implementation, say so clearly and explain why. If a suggestion is validated by what you see in the code, note that too.
- Add your analysis: do you agree? Is it feasible? Does it conflict with other feedback or existing code?

#### A.5 — Conflicts & Contradictions
Where reviewers disagree with each other. Present both sides and give your reasoned recommendation, informed by your actual codebase knowledge.

#### A.6 — Recommended Plan Changes
A **prioritized, actionable list** of changes:
- **Must-do**: High consensus, high impact, or addresses real risks
- **Should-do**: Strong suggestions that meaningfully improve the plan
- **Consider**: Good ideas worth thinking about but not critical (these will be presented as a pick list — see Part B)
- **Reject (with reason)**: Suggestions you recommend ignoring, and why — especially those invalidated by actual code inspection

For each item, reference which reviewer(s) raised it.

#### A.7 — What Stays
Explicitly confirm what the reviewers agreed is solid and should remain unchanged. This is important — the user needs to know what's working, not just what to fix.

---

### Part B: Updated Plan

Produce the full updated plan with the following approach:

1. **Auto-apply** all **Must-do** and **Should-do** changes. Mark each change inline with a comment like `<!-- CHANGED: [brief reason] — Reviewers X, Y -->` so the user can see what shifted and why.
2. **Do NOT apply Consider-tier items.** Instead, append a section at the end of the updated plan:

   #### Optional Enhancements (pick what you want)
   Present a numbered list of all Consider-tier items. For each:
   - What the change would be
   - Which reviewer(s) suggested it
   - Effort estimate (trivial / small / medium / large)
   - Your recommendation (lean yes / lean no / neutral)

   The user can then say things like "also apply 2, 5, and 8" and you will incorporate them.

3. **Preserve everything marked as "Stays"** — do not accidentally modify confirmed-good parts of the plan while applying changes.

---

## Output

- Write the meta-review to `{plan-directory}/META-REVIEW-{plan-name}.md` and display it in chat.
- Write the updated plan to `{plan-path-without-ext}-v2.md` (or increment the version if one already exists) and display it in chat.
- After presenting both, tell the user:
  - "The updated plan has Must-do and Should-do changes applied. Review the Optional Enhancements list at the end and tell me which numbers to add, if any."
  - Wait for their selections or confirmation that the plan is good as-is.

## Ground Rules

- Be thorough and analytical, not just a summarizer. Add your own judgment.
- **Use your codebase access.** You are the only entity in this process that can see the actual code. Validate reviewer claims. Check feasibility. Catch suggestions that sound good in theory but don't work given the real implementation.
- Treat all reviewers as valuable but not infallible — weigh feedback by its reasoning quality, not just its existence.
- Stay in intake mode as long as needed. Do not rush to the meta-review.
