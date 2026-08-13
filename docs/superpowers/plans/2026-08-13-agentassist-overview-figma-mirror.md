# AgentAssist overview Figma mirror Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create and verify an editable Figma design mirroring the current AgentAssist overview screen.

**Architecture:** Create a new Figma Design file, use a temporary live-page capture only as a visual reference, and build the final screen with editable Figma frames, text, rectangles, table rows, and icon-like controls. Validate the resulting node tree and render before handing off the file URL.

**Tech Stack:** Figma Design file, Figma Plugin API via `use_figma`, live localhost app at `http://localhost:5173/`.

---

### Task 1: Create the Figma design file

**Files:**
- No repository files modified.

- [ ] Resolve the authenticated Figma plan with `whoami`.
- [ ] Create a new Design file named `AgentAssist — Overview Mirror` in the selected plan.
- [ ] Record the returned `file_key` and `file_url` for all later Figma calls.

### Task 2: Establish the editable screen frame

**Files:**
- No repository files modified.

- [ ] Inspect the new file’s pages and top-level nodes before writing.
- [ ] Create a top-level `App / 1280×720` frame positioned away from the page origin if needed.
- [ ] Set the canvas fill to white and create named top-level child frames: `TopBar`, `Body`, and `StatusBar`.
- [ ] Use auto-layout for `Body` and all related horizontal/vertical groups.

### Task 3: Build the overview UI hierarchy

**Files:**
- No repository files modified.

- [ ] Build `TopBar` at 48 px high with the left-tree toggle and right utility controls.
- [ ] Build `ProjectsSidebar` at 250 px wide with the `Projects` header, refresh/add controls, `NewProject`, nested `master`, and `New linked worktree` row.
- [ ] Build `OverviewMain` with the title, creation timestamp, root/source path metadata, assistant CTA, Purpose and Owner fields, and the Worktrees table.
- [ ] Build the table with headers `Title`, `Branch`, `Status`, `Owner`, `Purpose`, and `Tasks`, plus the `master` row and `Ongoing` status badge.
- [ ] Build `StatusBar` at 32 px high with `● Ready`, `Runtime rev 3 · idle`, `NewProject / no worktree / no device`, `API online`, `Balance CNY 18.59`, `0 TIA sessions`, refresh, and settings controls.
- [ ] Use the inspected light-theme colors, Geist-style typography, compact sizes, thin borders, and 6–10 px radii.

### Task 4: Validate and hand off

**Files:**
- No repository files modified.

- [ ] Inspect the created node tree and confirm major areas are independently selectable.
- [ ] Render or screenshot `App / 1280×720` and compare it with the live app structure.
- [ ] Fix any obvious layout or text mismatches in small incremental Figma updates.
- [ ] Return the final Figma URL and summarize the editable layers created.

## Self-review

- Spec coverage: Tasks 2–3 cover the full agreed screen structure, visual tokens, text content, and editability; Task 4 covers the acceptance checks.
- Placeholder scan: no TBD, TODO, FIXME, or unspecified implementation steps remain.
- Type/API consistency: all Figma writes use `use_figma`; the file key returned in Task 1 is reused for Tasks 2–4.
