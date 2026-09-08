# P1-T1 report

## Status

DONE

## Implementation

- Added `WorkbenchTagDocument`, `TagNode`, `TagAssignment`, and `TagEntityType` in `src/Agent/Workbench/Tags/WorkbenchTagModels.cs`.
- Added `WorkbenchTagStore` in `src/Agent/Workbench/Tags/WorkbenchTagStore.cs`.
- The store uses an independent schema version `1.0`, defaults to `%LOCALAPPDATA%/AutomationWorkbench/metadata/tags.json`, and accepts an injected path for tests.
- Missing storage starts as an empty tag document; corrupt, malformed, and unsupported documents raise `WorkbenchTagStoreException` without resetting the existing file.
- `Mutate` performs read-modify-write under a path-derived named mutex. Writes delegate to the repository `AtomicJsonStore`, retaining atomic temporary-file replacement and cleanup behavior.
- Existing Workbench metadata models and `workbench.json` schema behavior were not changed.

## TDD evidence

- RED: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~WorkbenchTagStoreTests -v q` failed to compile before the implementation because the requested store/models did not exist.
- GREEN: the same focused command passed all 4 focused tests.
- Required Agent validation: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` passed 403 tests.

## Files

- `src/Agent/Workbench/Tags/WorkbenchTagModels.cs`
- `src/Agent/Workbench/Tags/WorkbenchTagStore.cs`
- `tests/Agent.Tests/WorkbenchTagStoreTests.cs`

## Self-review

- Persistence round-trip asserts stable node IDs and assignments after constructing a new store instance.
- Corrupt and unsupported documents are asserted to fail clearly and remain byte-for-byte unchanged.
- Concurrent mutation coverage asserts no lost updates under serialized read-modify-write.
- The change is isolated to the new tag domain and its focused tests; no later service/API/UI work was included.

## Concerns

None for the P1-T1 scope. Later service work should use `Mutate` for all assignment/node changes rather than mutating a loaded document outside the store lock.
