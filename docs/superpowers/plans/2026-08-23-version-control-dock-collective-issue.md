# Version Control Dock Collective Issue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use test-driven development to implement this plan task-by-task.

**Goal:** Make the version-control dock present actionable Git/TIA state without obsolete validation labels or per-block push actions, and let users explicitly authorize compile/save when TIA has no PLC checksum.

**Architecture:** Keep the global commit flow in the changes page. Route an explicit `allowCompile` acknowledgement from the compare UI through the API, coordinator, consistency service, scanner, and export stager. Keep TIA checksum evidence attached to completed commits through the existing timeline model.

**Tech Stack:** React/Vitest, ASP.NET minimal APIs, C#/.NET xUnit, MCP engineering calls.

---

### Task 1: Version-control dock presentation

**Files:** `studio/src/studio/version-control/VersionControlPanel.tsx`, `VersionControlChanges.tsx`, `VersionControlCompare.tsx`, and their existing tests.

- [ ] Add failing tests for removing the obsolete validation label, suppressing the clean-state hero during an active differing comparison, and removing push-to-TIA controls.
- [ ] Implement the smallest presentation changes and keep the existing global commit message/action.
- [ ] Run the focused Studio tests.

### Task 2: Explicit compile/save acknowledgement for TIA compare

**Files:** `studio/src/api/client.ts`, `VersionControlCompare.tsx`, `src/ApiHost/WorkbenchApiModels.cs`, `src/Agent/Workbench/WorkbenchCoordinator.cs`, `WorkbenchConsistencyService.cs`, `PlcSourceScanner.cs`, `SafeDeviceExportStager.cs`, and focused tests.

- [ ] Add failing tests proving the first missing-checksum compare requests confirmation and the confirmed retry passes `allowCompile` through to compile/save and export.
- [ ] Add the optional request flag and propagate it through the compare stack.
- [ ] Show a confirmation action in the compare result for `PLC_CHECKSUM_UNAVAILABLE`; retry only after confirmation.
- [ ] Run focused Agent/API/Studio tests.

### Task 3: Timeline evidence and validation

**Files:** existing timeline/history components and tests only if needed.

- [ ] Verify every rendered commit exposes its short Git hash and completed commit evidence continues to render the TIA checksum.
- [ ] Remove obsolete “TIA validated” presentation without changing persisted evidence data.
- [ ] Run frontend and relevant .NET test suites, review diff and worktree status.
