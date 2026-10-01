# ADR-0007 Task target model: explicit target kind instead of a synthetic device

## Status

Accepted

## Context

Every worktree task must today bind to a registered PLC device, and the API enforces it:

```csharp
// src/ApiHost/WorkbenchApiModels.cs:872
if (string.IsNullOrWhiteSpace(request.DeviceId) || !worktree.DeviceIds.Contains(request.DeviceId, StringComparer.Ordinal))
    throw new EngineeringGraphConstraintException("A worktree task must select a registered device.", "TASK_DEVICE_REQUIRED");
```

`DeviceIds` is derived from the worktree's on-disk `devices/` directory
(`WorkbenchApiModels.cs:367-378` enumerates `devices/*/device.json`).

The product needs to separate a PLC hardware-configuration change from a program-block change: hardware
tasks bind to a hardware target, program tasks keep binding to their PLC.

Repository evidence that constrains the choice:

- `GraphEntityKind = { Task, Session, GitCommit, SourceObject, SvnRevision }` — there is **no** hardware
  entity kind (`EngineeringGraphModels.cs:3`).
- The task table's `device_id` is **already nullable** and `CreateTask` does not require it
  (`EngineeringGraphService.cs:33-59`). The API check above is the only non-null enforcement.
- Every same-device relationship rule already tolerates a null task device
  (`EngineeringGraphService.cs:211,252,285,314` all read `task.DeviceId is not null && target.DeviceId is not null && …`).
- A task with a null device already cannot stage source objects
  (`EngineeringGraphService.cs:69`), which is the correct behaviour for a hardware task.
- Hardware changes already produce a Git commit
  (`WorkbenchCoordinator.OverwriteHardwareFromStagingAsync` → `HardwareConfigurationOverwriteResult(root, files, commit.Sha)`),
  and `GraphRelationKind.TaskCommit` already exists — so a hardware task's evidence path needs no new
  graph concept.
- Hardware views are worktree-scoped today: `MainStudio.selectHardwarePage` sets `deviceId: null` and
  opens the `tree` / `bom` / `network` pages.

## Decision Point

- **Question**: how does a task express that its target is the worktree's hardware configuration rather
  than one of its PLC devices?
- **Why a decision exists**: at least two materially distinct options are credible and both fit the
  current repository — make `hardware` a member of the device list, or give the task an explicit target
  kind. They differ in blast radius across every device-scoped surface and in the persistence contract.
- **Scope boundary**: the task's target semantics, its persisted representation, and the create/validate
  contract. It does not decide how the hardware pages themselves work, and it does not add per-PLC
  hardware granularity.

## Decision

A worktree task carries an explicit target kind. `targetKind` is `device` or `hardware`; a `hardware`
task carries no `deviceId`. One worktree has exactly one hardware target, matching the existing
worktree-scoped hardware pages.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | Add an explicit `targetKind` to worktree tasks (`device` \| `hardware`), relax the create check to accept a hardware target with no device, and keep `deviceId` as the PLC binding for device tasks only. |
| **Why this** | It keeps "device" meaning a real PLC everywhere it is already used, and pays the cost once instead of at every device-scoped feature. |
| **Known unknowns** | Whether hardware ever needs per-PLC granularity. The shape carries an optional `deviceId` beside `targetKind`, so adding it later does not break the contract. |
| **Reconsider when** | Hardware work must be attributable to a specific PLC, or a second hardware-like target kind appears that the enum cannot express without changing the create contract again. |

### Persisted representation

| Option | Fit | Cost |
|---|---|---|
| Nullable `target_kind` column on `tasks`, effective default `device` | Mirrors how `device_id` itself was added as a nullable column without rewriting existing rows | One additive migration |
| `targetKind` inside the existing `metadata_json` | No migration; `metadata_json` is already a real payload home (`priority`, `intent`, `expectedResult`, `elementRefs`) | Turns a value that gates create validation and appears in the API response into an opaque blob |

**Selected**: the nullable column. The field gates validation and is part of the task contract, so it
belongs next to `device_id` rather than inside an opaque metadata bag.

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. Synthetic device (`hardware` joins the device list) | Only the create check changes; `ListDevices` already enumerates directories, so a fake device folder would make it "just work" | Smallest diff for the create path | A fake `deviceId` flows into device snapshots, `SourceObject` ids, the knowledge graph, device sessions, `tia/open`, PathJail roots, and the bound-PLC label — each needing a special case; a fake directory also sits in the path the TIA import and reconciler walk | "Device" stops meaning PLC, and every future device feature must remember the exception | Fastest to demo, most expensive to live with |
| B. Explicit target kind | Fits the null-tolerant relationship rules, the stage guard, and the existing commit-based hardware evidence without changing any of them | Keeps device semantics intact; distinguishes hardware tasks from legacy/unbound tasks, which the unbound-task group needs anyway | New field in the task contract plus one additive migration | One concept, one place; hardware is expressed where the task is defined | Every consumer of the task type must know the field exists |

**Selected**: B. Option A saves one validation branch now and charges every device-scoped surface later.

## Consequences

### Positive Consequences

- Hardware work and program-block work are distinguishable in the task list, the task detail, and the
  navigator's `TASKS` section without inventing a fake PLC.
- The existing null-tolerant graph rules, the source-stage guard, and the commit-based hardware evidence
  path are reused unchanged.
- Legacy and unbound tasks stay distinguishable from hardware tasks, so the navigator can keep showing
  them in their own group.

### Negative Consequences

- The task contract gains a field, so the API response, the client type, the create dialog, and the task
  detail all change in the same unit of work.
- `task-device-navigation-ui-spec.md` **AC-001** ("a worktree task must select a registered device") is
  superseded for hardware tasks; that criterion must be revised with the implementation.
- A hardware task cannot stage source objects or take part in source comparison; the UI must state that
  rather than presenting empty staging controls.

### Neutral Consequences

The hardware pages keep their current worktree scope and behaviour. No entity kind, relation kind, or
API route is added or removed.

## Architecture Impact

`EngineeringGraphService.CreateTask` and `UpdateTask` carry the new target, the graph schema gains one
additive nullable column, and the create endpoint's `TASK_DEVICE_REQUIRED` check becomes conditional on
the target kind. Consumers that key off `deviceId` keep working because a hardware task's `deviceId`
stays null, which is a value those consumers already handle.

## Implementation Guidance

Treat `targetKind` as the single source of truth for "what this task is about", and derive the navigator
grouping, the create dialog's preselect behaviour, and the task detail label from it. Do not add a second
flag that can disagree with it.

## Related Information

- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md` — places the hardware target as a row in the `DEVICE` section
- `docs/ui-spec/studio-information-architecture-ui-spec.md` — the surface that consumes the target
- `docs/ui-spec/task-device-navigation-ui-spec.md` — AC-001 superseded for hardware tasks
- `src/ApiHost/WorkbenchApiModels.cs:867-879` — the create check that changes
- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs` — the target-tolerant rules reused here
