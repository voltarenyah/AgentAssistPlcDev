# TIA Selection Global Commit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make TIA comparison checkboxes feed the existing global commit action, which overwrites the selected local source from the staged TIA export and commits it without a separate compare-panel accept button.

**Architecture:** Lift the compare selection and comparison id into `VersionControlChanges` through callbacks. The global commit action will route TIA-selected paths through `acceptTiaSynchronization`; ordinary local selections continue through `commitVcPaths`. The compare component will remain responsible only for comparison display and selection reporting.

**Tech Stack:** React, TypeScript, Vitest, existing ASP.NET/API synchronization endpoint.

---

### Task 1: Regression coverage

**Files:**
- Modify: `studio/src/studio/version-control/VersionControlCompare.test.tsx`
- Modify: `studio/src/studio/version-control/VersionControlChanges.test.tsx`

- [x] Add a failing compare test proving a checked TIA item has no individual accept button and reports `{ comparisonId, paths }` through the selection callback.
- [x] Add a failing changes-dock test proving the global commit action invokes `acceptTiaSynchronization` with the typed message for a selected TIA item.
- [x] Run the focused tests and confirm the failures are caused by the missing behavior.

### Task 2: Route TIA selections through the global commit action

**Files:**
- Modify: `studio/src/studio/version-control/VersionControlCompare.tsx`
- Modify: `studio/src/studio/version-control/VersionControlChanges.tsx`

- [x] Add compare selection/comparison callbacks and remove the individual “Accept selected TIA changes” action.
- [x] Track TIA-selected paths alongside ordinary source selections in the changes dock.
- [x] Make the global commit action call the existing TIA synchronization endpoint for TIA selections; keep ordinary local commits unchanged.
- [x] Refresh comparison/history and clear completed selections after the synchronization commit.

### Task 3: Verification

- [x] Run focused Studio tests.
- [x] Run the full Studio test suite.
- [x] Review the diff and worktree status for unrelated changes.
