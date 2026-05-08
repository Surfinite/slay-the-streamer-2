# Generate External Review Context Document

You have just created or recently discussed a plan or design spec. Your job now is to produce a single, self-contained **Context Document** that will be handed to external reviewers alongside that plan/spec. The reviewers have **zero prior knowledge** of this project or codebase.

## What to include

Produce a markdown document with the following sections:

### 1. Reviewer Brief
Explain to the reviewers what their job is:
- They are receiving two documents: this context document and a plan/spec.
- Their role is to **critically analyze** the plan/spec given the context provided.
- They should identify: weaknesses, risks, missing considerations, better alternatives, unnecessary complexity, things that should be removed, and things that are good and should be preserved.
- They should suggest additions, potential future features worth considering, and architectural improvements.
- They should be constructively critical — not rubber-stamping.
- Their review will be synthesized in a meta-review to improve the plan/spec, so they should be specific and actionable.
- **Important**: Reviewers do NOT have direct access to the codebase. They are working from this context document only. The plan/spec author has full codebase access and will validate all suggestions against the actual code during the meta-review. Reviewers should flag where they feel uncertain due to limited visibility and note any assumptions they are making about the code.

### Review Output Format
Structure your review as follows:

1. **One-line verdict**: Your overall assessment in a single sentence.
2. **What's good**: What should be kept as-is and why.
3. **Concerns & risks**: What worries you, ranked by severity.
4. **Suggested changes**: Specific, actionable modifications to the plan/spec.
5. **Alternatives**: Different approaches worth considering.
6. **Additions**: Things missing that should be there.
7. **Removals**: Things that shouldn't be.
8. **Minor / nits**: Low-priority observations.
9. **Assumptions you're making**: Where you lacked visibility into
   the codebase and had to guess. The plan/spec author will validate these.

Be specific. Reference section names or step numbers from the plan/spec.
Don't soften your criticism — the goal is to improve the plan/spec, not
to be polite about it.

### 2. Project Overview
- What the project is and what problem it solves.
- Current stage of development (greenfield, mature, refactor, etc.).
- Key goals and constraints (timeline, team size, budget if relevant).
- Target users / audience if relevant.

### 3. Architecture & Tech Stack
- Languages, frameworks, major dependencies.
- High-level architecture (services, modules, data flow).
- Key architectural decisions already made and **why**.
- Include a simple diagram in text/mermaid if it aids understanding.

### 4. Codebase Map
- Directory structure overview (focus on what matters, skip noise like node_modules, decompiled output, references/).
- Where the important logic lives.
- Key files and modules relevant to the plan/spec.
- Current lines-of-code scale / rough size.

### 5. Relevant Existing Patterns & Conventions
- Coding conventions, naming patterns, error handling approach.
- Testing strategy and current coverage.
- How config, secrets, and environment management work.
- Any patterns the plan/spec must respect or deliberately changes.

### 6. Current State & Known Issues
- What works today.
- Known technical debt, bugs, or fragile areas.
- Recent significant changes.
- Anything the reviewers should know that might affect the plan/spec's feasibility.

### 7. Context Specific to the Plan/Spec
- Which parts of the codebase the plan/spec touches or depends on.
- Any prior attempts or rejected approaches for the same problem, and why they were rejected.
- Dependencies, integrations, or external systems involved.
- Performance, scale, or security considerations relevant to the plan/spec.

### 8. Scope Boundaries
- What is explicitly **out of scope** for this plan/spec and why.
- Constraints or decisions that are **fixed and non-negotiable** (reviewers should not waste time suggesting alternatives to these).
- Known trade-offs that were accepted deliberately.

### 9. Success Criteria
- How will we know the plan/spec succeeded?
- What are the measurable or observable outcomes?
- Any acceptance criteria, performance targets, or quality bars.

### 10. Key Questions for Reviewers
- Read the plan/spec carefully and identify 3–5 specific questions or areas of uncertainty where reviewer input would be most valuable.
- Frame these clearly so reviewers know where to focus attention beyond their general review.

### 11. Glossary / Domain Terms
- Define any project-specific or domain-specific terminology a reviewer would need.

## Instructions for you (Claude Code)

- **Read the codebase** — don't guess. Use tools to inspect the actual directory structure, key config files, READMEs, and the files most relevant to the plan/spec.
- Be **thorough but concise**. Reviewers value density over padding.
- If the plan/spec references specific files, modules, or systems, make sure those are explained in the context doc.
- If you are unsure about something, say so explicitly rather than fabricating details.
- Output the final document in a single markdown fenced block, ready to copy-paste.
- Find the most recent plan or spec file in (in priority order):
  1. `docs/superpowers/plans/` — implementation plans (output of the `writing-plans` skill)
  2. `docs/superpowers/specs/` — design specs (output of the `brainstorming` skill)
  3. `docs/plans/` — fallback for projects not using the superpowers convention
- The context document should be named identically but with `-CONTEXT` appended before the extension. For example, if the source is `docs/superpowers/specs/2026-05-08-ti-layer-design.md`, the context document should be `docs/superpowers/specs/2026-05-08-ti-layer-design-CONTEXT.md`.
- Write the file to disk at that path next to the source.
