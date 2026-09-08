# P1-T2 implementation report

## Outcome

Implemented normalized hierarchical tag path creation, rename, derived path lookup, and conservative deletion rules on top of the P1-T1 store.

## Changes

- Added `WorkbenchTagService` using `WorkbenchTagStore.Mutate` for every mutation.
- Added explicit `WorkbenchTagDomainException` errors with stable codes for invalid paths/names, missing tags, sibling conflicts, assigned tags, non-leaf tags, and invalid persisted parent links.
- Creation trims segment display names, removes empty slash boundaries, rejects empty interior segments, compares siblings using invariant case-insensitive normalized names, and returns the existing stable leaf ID for repeated paths.
- Rename trims and validates names, protects same-parent collisions, and changes only node text/normalization; IDs, parent links, descendants, and assignments remain unchanged.
- Delete permits only unassigned leaf nodes.
- Added focused service tests for deep/idempotent creation, invalid paths and collision handling, ancestor rename identity/path behavior, and delete guards.

## Validation

- TDD RED: focused test run initially failed to compile because the service and domain exception were absent.
- Focused GREEN: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore -v q --filter FullyQualifiedName~WorkbenchTagServiceTests` — 4 passed.
- Required project validation: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — 408 passed.
- `git diff --check` passed.

## Scope

No API, UI, entity lifecycle, assignment, or search behavior was added.
