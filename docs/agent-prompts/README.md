# Agent prompt queue

This directory records work that is **written before it is executed**: a human writes the prompt,
the requirement, and the acceptance criteria here, and an agent runs it later without re-deriving
what was meant. It is an intake queue, not a plan and not a design document, so it deliberately
stays lighter than `docs/plans/` and `docs/design/`.

- One item per file: `NNN-<kebab-title>.md`, numbered from `001`.
- This README is the index and the run protocol. It is the only file an agent must read first.
- An item is runnable when its `Status` is `pending`. The prompt inside it is self-contained.

## Human: adding an item

1. Copy the template below into `docs/agent-prompts/NNN-<kebab-title>.md` with the next free number.
2. Fill in Goal, Context, Constraints, and Done when. Those four sections are the contract every
   task in this repository uses (see the root `AGENTS.md`).
3. Add a row to the index table below with `Status: pending`.
4. Name concrete file paths, symbols, and commands in Context. An item that says "related files"
   or "as appropriate" pushes the requirement discovery back onto the agent.

## Agent: running the queue

1. Read this file, then the item files in ascending numeric order.
2. Choose the **lowest-numbered item whose `Status` is `pending`**, unless the human named a
   specific item. Run exactly one item per work unit; do not bundle two items.
3. Check `git status --short` first and preserve unrelated uncommitted work. Create the item's own
   branch (`codex/<issue-or-item>-<slug>`) or worktree when the item leads to a commit, per the root
   `AGENTS.md` parallel-development rules.
4. Execute the item's **Goal** inside its **Constraints**. Every rule in the root `AGENTS.md`
   applies unchanged: scope control, validation, UI evidence, commits, PR, no merge.
5. Prove each **Done when** check by actually running it. Do not copy an unverified claim into the
   result.
6. Update the item file: set `Status` to `done`, `blocked`, or `dropped`, and record under
   **Evidence** the commands run, their results, the branch, and the commit. Then update the index
   row to match.
7. Set `Status: blocked` with the exact blocking condition and the input needed when the item cannot
   be finished. Leaving an item `in-progress` across sessions loses the reason it stopped.

## Status values

| Status | Meaning |
|--------|---------|
| `pending` | Not started. Ready for the next agent. |
| `in-progress` | Claimed by an agent that is actively working on it. |
| `blocked` | Cannot proceed; the blocking condition and the needed input are recorded in the item. |
| `done` | Every `Done when` check has been verified and the evidence is recorded. |
| `dropped` | Deliberately not done. Record who decided and why. |

## Index

| # | Item | Status | Depends on | Primary files |
|---|------|--------|------------|---------------|
| [001](001-disable-right-dock-auto-expand-on-worktree-select.md) | Remove every right-dock auto-expand | pending | — | `studio/src/studio/MainStudio.tsx` |
| [002](002-task-page-source-objects-stages-and-git-bound-baseline.md) | Task page: editable staged source objects with a Git-bound baseline | pending | — | `studio/src/studio/workbench/TaskDetail.tsx`, `src/Agent/Workbench/WorkbenchCoordinator.cs` |
| [003](003-compare-task-mode-in-version-control.md) | Compare task mode in the version-control surface | done | 002 | `studio/src/studio/version-control/VersionControlCompare.tsx` |
| [004](004-task-commits-with-involved-source-objects.md) | Task page Commits section: which source objects each commit touched | pending | 002 | `studio/src/studio/workbench/TaskDetail.tsx`, `src/ApiHost/WorkbenchApiModels.cs` |
| [005](005-agent-stages-source-objects-with-approval.md) | Agent stages source objects for the current task through an approval card | pending | 002 | `src/Mcp.Engineering/Tools/EngineeringTools.cs` |
| [006](006-remove-right-dock-ai-sessions-page.md) | Remove the right dock's AI sessions page and move its operations into the navigator's conversations | pending | 001 | `studio/src/studio/chat/SessionDock.tsx`, `studio/src/studio/workbench/WorkbenchNavigator.tsx` |

Items 002–005 implement the missing execution layer of an already accepted design: task-scoped
source evidence (`docs/adr/ADR-0003-task-scoped-source-evidence.md`,
`docs/design/task-scoped-tia-compare-design.md`,
`docs/ui-spec/task-scoped-tia-compare-ui-spec.md`). The backend skeleton shipped; the entry points that
let a task acquire a compare basis, and the surfaces that show it, did not. Run 002 first: it is the
only item that establishes a usable stage baseline, and 003–005 depend on it.

Items 001 and 006 are independent of that work: they remove action-driven auto-expansion of the right
dock and retire the right dock's AI sessions page. Run 001 before 006, because both edit
`createChatSessionFromEmptyState` in `MainStudio.tsx`.

## Template

```markdown
# NNN. <Imperative title: what must change>

Status: pending
Created: YYYY-MM-DD
Depends on: none

## Goal

<The observable behavior change. One outcome; state what the user sees before and after.>

## Context

<Exact paths, symbols, line anchors, current code, related tests, and the root cause when known.
Include the evidence that makes this item unambiguous.>

## Constraints

<Architecture, contracts, persisted formats, conventions, and boundaries this change must respect.
Name the protected condition for every prohibition.>

## Done when

1. <Observable check, with the command or interaction that proves it.>
2. <Test/build/lint commands that must pass.>

## Evidence

<Filled in by the executing agent: commands run, results, branch, commit, skipped steps and risk.>
```
