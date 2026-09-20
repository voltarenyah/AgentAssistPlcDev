# Design Document: Task-scoped TIA Compare

## Overview

- Outcome: commit TIA source changes through one device-bound task without scanning unrelated task objects, while requiring a full scan before project-wide claims or SVN savepoints.
- Scope: engineering graph persistence, compare/commit coordination, validation evidence, API contracts, and Studio surfaces.
- UI Spec: `docs/ui-spec/task-scoped-tia-compare-ui-spec.md`
- Governing ADRs: `docs/adr/ADR-0003-task-scoped-source-evidence.md`

## Requirement Boundary

- Current requirements: a task has one device; staged objects use stable source IDs and fingerprint evidence; scoped scans export changed candidates only; full scans discover unassigned changes; savepoints are forbidden while source work is unresolved; completed tasks release stage ownership but retain history.
- Non-goals: automatic task assignment, using a project checksum as source truth, and creating an SVN savepoint from a task commit.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — A task compare reads only the active stage objects for its bound device and exports XML only for candidates.
- **AC-002** — A clean scoped result is labelled task-clean and cannot permit a project-wide claim or savepoint.
- **AC-003** — Committing Task A cannot make an uncommitted Task B change appear clean.
- **AC-004** — A full scan exposes changed unassigned source objects, and unresolved objects block native savepoints.
- **AC-005** — Done tasks release active stage ownership while preserving their source-object history.

## Selected Design

Graph tasks gain an immutable device ID. A dedicated active-stage record owns a registered source-object ID and stores its Git-content-bound fingerprint evidence. Historical task-to-source graph edges remain traceability records. Scoped comparison resolves only active-stage records, captures/export candidates under one TIA Exclusive Access lifetime, and returns a scoped result. After commit it records only the committed objects' evidence. Full comparison continues to capture all managed source objects, reports unassigned changed objects, and is called by the native-savepoint guard.

## Change Surface

| Responsibility | Change |
|---|---|
| `EngineeringGraphSchema` / `EngineeringGraphService` | Task device binding, active-stage ownership, and sparse evidence persistence. |
| `WorkbenchConsistencyService` / `WorkbenchCoordinator` | Scoped source comparison, commit evidence recording, and full-scan savepoint guard. |
| `WorkbenchApiModels` / `studio/src/api/client.ts` | Task stage, scoped compare, assignment, and savepoint-result contracts. |
| `WorktreeTasksPanel` / version-control components | Device-backed task stages and explicit scoped/full compare status. |

## Verification Strategy

| Claim | Level | Proof |
|---|---|---|
| One active owner and release on Done | L1 | Engineering graph schema/service tests. |
| Task A cannot hide Task B | L2 | Coordinator fake-TIA compare/commit test. |
| Scoped candidate export | L2 | Engineering/Agent test verifies only staged candidates are read. |
| Savepoint gate | L2 | Coordinator/API test blocks unresolved full-scan results. |
| UI wording and assignment | L1 | focused Vitest component/API-client tests. |

## References

- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs`
- `src/Agent/Workbench/WorkbenchConsistencyService.cs`
- `src/Agent/Workbench/WorkbenchCoordinator.cs`
