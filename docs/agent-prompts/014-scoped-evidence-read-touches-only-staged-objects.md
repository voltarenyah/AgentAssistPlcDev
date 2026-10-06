# 014. A scoped evidence read touches only the staged objects

Status: done
Created: 2026-10-06
Depends on: 010

## Goal

When a comparison asks for specific source objects, the engineering adapter locates exactly those
objects through their manifest source path and reads only them, instead of walking every managed
object of the PLC. `Task only` then costs what its stage list contains rather than what the project
contains. Anything the fast path cannot locate by path falls back to the complete walk, so the new
path costs time at worst — never correctness.

## Context

Measured on the real project `SWT2-PEI-N` / `Sino_PEI` (2026-10-06, one staged block):

- The dock's `Task only` compare took 17 s. Its three reported phases are 0.15 s (master commit read)
  + **9.2 s** (`compare_source_evidence`) + 0.15 s (one candidate XML compare) ≈ 9.5 s; the remaining
  ~7.5 s is the project attach (`EnsureActiveProjectMatchesWorktreeAsync`: `disconnect` →
  `list_sessions` → `connect` with `withUI: true`, `WorkbenchCoordinator.cs:2274-2298`) plus the
  exclusive-session wait, neither of which is reported as a phase.
- That 9.2 s call reported "Read **1** evidence object(s)" while the project holds **774** managed
  objects (223 FC, 211 FB, 210 UDT, 95 DB, 28 OB, 7 tag tables; 774 XML files on disk). So the read
  walks the whole project and discards everything but the staged object.
- Where the walk is (all in `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`, `CaptureLiveSnapshot`):
  - `BlockEnumerator.Enumerate(plc.BlockGroup)` (:1628) walks every block and nested group; the
    `selectedIds` filter (`:1673`) happens only after `ManagedSourceScope.IsExcluded` and the
    fail-safe probe (`FailSafeBlocks.IsFailSafe` → `block.GetService<SafetySignatureProvider>()`,
    per block, `:1639-1641`).
  - `TagTableEnumerator.Enumerate` (:1695) and `PlcTypeEnumerator.Enumerate` (:1714) walk and filter
    the same way.
- Identity, which is what makes a targeted lookup possible: the id is
  `StableId.Create(category, sourcePath)` = `base64url(sha256("category|sourcePath"))`
  (`Export/StableId.cs:13`) and `sourcePath = ExportManifest.SourcePathOf(name, groupPath)` =
  `groupPath is null ? name : groupPath + "/" + name` (`Export/ExportManifest.cs:41`), where
  `groupPath` is the `/`-joined **user-group** path built by the enumerators. The request therefore
  carries everything needed to locate an object: `baseline.Objects` already holds each requested
  object's `SourcePath`, `Category` and `Kind`.
- Not to be changed: `IEngineeringPlatform.CompareSourceEvidence` / `CaptureSourceEvidence` and their
  MCP tool signatures are pinned by `tests/Mcp.Engineering.Tests/SourceEvidenceCaptureContractTests`.
  No signature may change; the requested paths come from the `baseline` snapshot that is already a
  parameter.
- Correct already, and to be preserved: the expensive per-object reads (`ReadBlockTimestamps`,
  `FingerprintReader.TryRead`, `ReadTypeMetadata`, `ReadTagTableModified`) happen after the filter.

## Constraints

- One object walk per requested object, not per project object. Descend only the user groups named by
  the source path; match the leaf by name; never enumerate a sibling group's objects.
- Verify before trusting: recompute `StableId.Create(category, SourcePathOf(name, foundGroupPath))`
  for the located object and require it to equal the requested id. Any mismatch, any object that
  cannot be located, and any requested object whose `Kind` is not `standard-block`, `udt` or
  `tag-table` (F-blocks and instance DBs keep their current handling) → fall back to the complete
  walk for that call.
- The complete-walk path must stay byte-for-byte equivalent in behavior for a full scan
  (`sourceObjectIds` empty/null) and for any call that falls back.
- The scoped snapshot must remain exportable: populate the same `Live` entries and the same
  `BlocksById` / `TablesById` / `TypesById` lookups the candidate export uses
  (`ReExportComponent`, `:1884-1911`), so a nominated candidate still exports.
- Add no new persisted state, no new tool parameter, no contract change.

## Done when

1. A scoped call locates each requested object through its path and reads only those objects;
   `CaptureLiveSnapshot`'s loops are not entered for it.
2. A scoped call whose request cannot be satisfied by path (unknown path, id mismatch, F-block or
   instance-DB kind, missing baseline object) takes the existing complete walk, and the full-scan
   call is untouched.
3. `dotnet build AgentAssistPlcDev.sln`, `tests/Mcp.Engineering.Tests`, `tests/Agent.Tests` and
   `tests/ApiHost.Tests` pass.
4. Live measurement by the user on the same worktree and the same staged block: the dock's
   "Read every lightweight fingerprint, tag timestamp, and F-block signature under one TIA lock…"
   phase drops from 9.2 s toward the cost of the staged objects, with the same candidate and the same
   accept/commit outcome.

## Implementation plan (exact edit points)

1. `Adapter/BlockEnumerator.cs`, `Adapter/PlcTypeEnumerator.cs`, `Adapter/TagTableEnumerator.cs`:
   add `TryFindBySourcePath(root, groupPath, name, out object, out string? foundGroupPath)` next to
   each `Enumerate`/`Walk`, splitting `groupPath` on `/` and matching each group by `Name`
   (`PlcBlockUserGroup` / `PlcTypeUserGroup` / `PlcTagTableUserGroup`), then the leaf by `Name`.
2. `TiaV17Adapter.CompareSourceEvidence` (:595-642): when `sourceObjectIds` is non-empty, build
   `scopedObjects` from `baseline.Objects` for those ids; pass it to `CaptureManagedSourceEvidence`.
3. `CaptureManagedSourceEvidence` (:644) and `CaptureLiveSnapshot` (:1616): thread `scopedObjects`;
   at the top of the snapshot loops, when it is non-null, call a new
   `TryCaptureRequestedComponents(plc, safetySurface, scopedObjects, ref fBlockSignatures, ref fBlockReadFailed)`
   and return its snapshot when it succeeds, otherwise fall through to today's loops unchanged.
4. `TryCaptureRequestedComponents`: per requested object — split the source path, find it, verify the
   derived id, skip `instance-db`, then reproduce exactly the per-object work of the corresponding
   loop body (block: `CategoryOf`, `ReadBlockTimestamps`, `FingerprintReader.TryRead`, `Live` +
   `BlocksById`; UDT: `ReadTypeMetadata`, `FingerprintReader.TryRead`, `Live` + `TypesById`; tag
   table: `ReadTagTableModified`, `Live` + `TablesById`). Return null on the first object it cannot
   locate or verify.

## Evidence

Branch `codex/010-task-only-compare`. Nothing pushed; no PR.

### What changed

- `Adapter/BlockEnumerator.cs`, `Adapter/PlcTypeEnumerator.cs`, `Adapter/TagTableEnumerator.cs` —
  each gains `TryFindBySourcePath`, which splits the manifest source path and descends only the user
  groups that path names (matching `.Name` per level), then the leaf by name. A sibling group's
  objects are never enumerated.
- `Adapter/TiaV17Adapter.cs` — `CompareSourceEvidence` derives the requested objects from the
  `baseline` snapshot it already receives (and gives up on the fast path when an id has no baseline
  record); `CaptureManagedSourceEvidence` and `CaptureLiveSnapshot` thread them through; the new
  `TryCaptureRequestedComponents` reads exactly those objects, reproducing the per-object work of each
  category's loop body and filling `Live` / `BlocksById` / `TablesById` / `TypesById` so a nominated
  candidate still exports. A staged F-block reads only its own signature, buffered until the read
  succeeds so a later fallback cannot duplicate it.
- `Export/ScopedSourceRequest.cs` (new) — the Siemens-free part: the path split and the identity
  check. This is where the fallback rule is testable without TIA.
- `tests/Mcp.Engineering.Tests/ScopedSourceRequestTests.cs` (new, 12 cases) — the split (root, one
  group, nested groups; null, empty, leading and trailing separator refused) and the identity check
  (the asked-for object accepted; a different category, including the fail-safe `F` key, and a
  different path rejected).

### Validation run

| Command | Observed result |
|---|---|
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors |
| `dotnet test tests/Mcp.Engineering.Tests` | 130 passed (118 before, +12) |
| `dotnet test … --filter SourceEvidenceCaptureContractTests` | 2 passed — the pinned tool signatures are unchanged |
| `dotnet test tests/Agent.Tests` | 518 passed |
| `dotnet test tests/ApiHost.Tests` | 227 passed |
| studio | untouched by this item (no frontend file changed) |

### Done when checks

1. **Implemented; live measurement is the remaining proof.** A scoped call no longer enters
   `CaptureLiveSnapshot`'s loops (verified by reading the change and by the build); whether it reads
   only the staged objects against a real project is what the user's timing run shows. I did not drive
   TIA myself.
2. **Partly proven.** The pure rule is proven by the 12 new cases; the adapter's fallback branches
   (unknown path, id mismatch, excluded object, F-block without a provider, instance DB, missing
   baseline record, no path to split) all return null and are covered by review, not by a test — the
   adapter needs a live portal.
3. **Passed** — see the table.
4. **Not yet measured** — see the measurement request below.

### Measurement request (the one check I cannot run here)

Same worktree (`SWT2-PEI-N` / `master`), same staged block, run `Task only` again. The
"Read every lightweight fingerprint, tag timestamp, and F-block signature under one TIA lock…" phase
should fall from **9.2 s** toward the cost of the one staged object, with the same candidate row
(`Blocks:202_VocDoorGeneral:0`) and the same commit behaviour.

### Notes, decisions and risks

- **All-or-nothing fast path:** the first object it cannot locate or verify abandons the whole read and
  the complete walk runs. A task staging twenty objects where one was renamed therefore pays today's
  cost — slower, never wrong.
- **Exact name matching:** group and leaf names are compared with `StringComparison.Ordinal`, which is
  how the source path is built. A case-only difference in TIA (names are unique per group) falls back
  rather than resolving to a wrong object.
- **Engine assumption still unmeasured:** the win depends on how expensive Openness makes the walk
  versus a per-object read. If the enumeration itself is cheap and the per-object probes dominated,
  the improvement will be smaller than the 774:1 object ratio suggests. The user's measurement decides.
- **Out of scope, worth its own item:** ~7.5 s of the observed 17 s is the project attach
  (`EnsureActiveProjectMatchesWorktreeAsync`: `disconnect` → `list_sessions` → `connect` with
  `withUI: true`) plus the exclusive-session wait, and neither is reported as a comparison phase, so
  the panel cannot explain the total. Recording them as phases is a small coordinator change.
