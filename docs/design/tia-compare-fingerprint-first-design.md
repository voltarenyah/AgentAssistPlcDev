# Design Document: Fingerprint-First TIA Compare

## Overview

- Outcome: a TIA Compare reads lightweight, complete software evidence for every PLC object and exports XML only for evidence-nominated source candidates.
- Scope: Engineering MCP evidence capture, Agent comparison orchestration, best-effort commit-bound validation evidence, and the existing Version Control Compare result contract.
- UI Spec: N/A. Existing Compare presentation remains the surface; it receives explicit evidence and untrackable-result fields rather than a new UI layout.
- Governing ADRs: `docs/adr/ADR-0001-mandatory-commit-bound-software-evidence.md`.

## Requirement Boundary

- Convergence carrier: current TIA Compare discussion, 2026-09-06.
- Current requirements:
  - This applies only to projects created after the fingerprint-first codebase rebuild; legacy workbenches and their commits are outside this feature's domain.
  - A new project captures a complete initial snapshot for every Git-managed PLC source object in every configured PLC. Each later commit makes a best-effort capture and immutable commit-bound recording attempt.
  - Every Compare reads all available lightweight evidence for standard blocks, UDTs, tag tables, and F-blocks.
  - Standard blocks and UDTs use their readable TIA fingerprint components; tag tables use the TIA `ModifiedTimeStamp`; F-blocks use per-block `BlockOfflineSignature`.
  - XML is exported and normalized only for a new, removed, changed, or unreadable-evidence candidate.
  - Instance DBs are excluded from evidence snapshots, XML export, XML comparison, and deletion detection.
  - The software evidence capture, candidate decision, and candidate export are protected by one TIA Openness Exclusive Access lifetime.
  - A checksum is required as compile evidence but is not a content-difference verdict.
  - A checksum change with no explainable managed-source candidate remains an untrackable change and prompts a native SVN savepoint; it must not be reported as an Instance DB explanation. It does not invalidate a Git commit whose managed sources are consistent.
  - Evidence capture and immutable tag creation are best-effort post-commit recording. Failure is visible, but does not block or roll back a Git commit.
  - F-block changes are represented in the same object-evidence result as other blocks but never exported as XML.
- Non-goals:
  - Do not change, optimize, or make optional the current hardware Compare in this change.
  - Do not support, migrate, or infer evidence for workbenches or commits created before this feature is enabled.
  - Do not remove existing checksum, F-signature, untrackable-change, or exact XML validation safety paths before their fingerprint-first replacements prove their equivalent boundary.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — When a current master commit has v2 managed-source evidence, Compare shall read all live standard-block/UDT fingerprints, tag-table timestamps, and F-block signatures without reading or normalizing unchanged XML. Source: lightweight-evidence requirement.
- **AC-002** — When one standard block, UDT, or tag table has changed evidence, Compare shall export and compare only that object XML plus any new/removed/unreadable-evidence candidates. Source: candidate-only XML requirement.
- **AC-003** — When an Instance DB is added, removed, or regenerated, Compare shall not export it or report it as a source difference. Source: Instance DB exclusion requirement.
- **AC-004** — When a checksum changes but all readable managed evidence is unchanged, Compare shall report an untrackable change requiring a native savepoint without reporting an Instance DB source difference or invalidating managed-source consistency. Source: untrackable-change requirement.
- **AC-005** — When a per-F-block offline signature changes, Compare shall identify the F-block and produce a non-consistent safety result without exporting its XML. `00000000` is a normal value for an uncalled safety block and is compared by equality like any other signature. Source: F-block requirement.
- **AC-006** — When a user mutation races a Compare, Exclusive Access shall make capture, candidate decision, and candidate XML export one coherent project observation; cancellation/failure shall produce no completed candidate manifest or partial staging promotion. Source: atomicity requirement.
- **AC-007** — When a new-project commit is created, the application shall make a best-effort attempt to record its complete live managed-source evidence against that exact SHA. Capture/tag failure is visible but does not block or roll back the commit. Source: best-effort evidence requirement.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Current `sync_export` skips by checksum, copies the full staging tree, then reads all staged XML | `src/Agent/Workbench/PlcSourceScanner.cs`, `src/Agent/Workbench/SafeDeviceExportStager.cs` | Replace this Compare path; do not reuse whole-tree staging as the normal evidence path. |
| Standard blocks/UDTs expose `FingerprintProvider`; tag tables expose only `ModifiedTimeStamp` | `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs:1274` | Use typed evidence policy rather than one generic fingerprint assumption. |
| F-block XML export is prohibited but block offline signatures are readable and persisted | `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs:1635`, `src/Agent/Workbench/EngineeringStateWriter.cs` | Use signatures as F-block evidence; never nominate F-block XML. |
| Current planner re-exports Instance DBs because their normal metadata can lag regeneration | `src/Mcp.Engineering/Export/SyncPlanner.cs:145` | Delete that policy for Compare and filter Instance DBs before all candidate accounting. |
| Exact source evidence is an immutable `vc_validation` tag; current payload stores checksums and normalized XML hashes, not TIA fingerprints | `src/Agent/Workbench/WorkbenchConsistencyService.cs:522`, `src/Mcp.VersionControl/Git/Models.cs:39` | Add a v2 payload for best-effort managed-source recording after new-project commits. |
| `vc_commit_state_create` stores per-device checksums and F signatures only, and its caller is best effort | `src/Agent/Workbench/WorkbenchCoordinator.cs:3178` | Extend the best-effort post-commit record with full managed-source evidence; preserve native savepoint classification. |
| Existing result handling rejects a fast path after an untrackable commit | `src/Agent/Workbench/WorkbenchConsistencyService.cs:249` | Preserve and extend the same user-facing untrackable safety rule. |

## Design

### Selected Design

Introduce a single Engineering MCP operation named for source-evidence comparison. For each selected PLC it acquires `TiaPortal.ExclusiveAccess`, reads the complete live managed-source evidence snapshot, compares it to the commit-bound v2 evidence supplied by the Agent, exports only nominated XML objects into a new candidate directory, and returns both the live evidence and candidate manifest before releasing the lock. The Agent compares normalized XML only for returned candidates against the master files and persists the result.

New-project creation uses an initial `capture_source_evidence` operation: it reads all available lightweight evidence and records the initial snapshot after the initial source export/commit. Later Compare and commit preparation use `compare_source_evidence`: it reads the same full live lightweight evidence set, compares it with the latest available snapshot, and exports XML only for candidates. After each later Git commit, the application makes a best-effort v2 evidence-tag write for the exact SHA. The compiled software checksum is the authoritative invariant for the managed software domain; `managedSourceConsistent` is retained as historical capture metadata and is not required for checksum fast-gate eligibility. An untrackable commit still records the complete live managed snapshot and the existing untrackable marker; the native change remains a separate SVN-recording concern. `vc_commit_state_create` remains a checksum/F-signature native-state record, not a managed-source evidence substitute.

Evidence policy is fixed by object kind:

| Object kind | Live evidence | Baseline comparison | XML policy |
|---|---|---|---|
| Standard block / UDT | canonical named fingerprint set; TIA timestamps retained as diagnostics | equal fingerprint set = no XML; missing/read-failed = candidate | candidate only |
| Tag table | `ModifiedTimeStamp` | equal timestamp = no XML; changed/missing/read-failed = candidate | candidate only |
| F-block | `BlockOfflineSignature` per block and folded PLC signature | signature equality; `00000000` is a normal value for an uncalled block | never export XML |
| Instance DB | none | excluded | never export or diff |

The software checksum must be present before capture. It indicates a compiled project, but a mismatch after all managed evidence compares equal becomes an explicit untrackable result. The existing savepoint action writes the native record and untrackable marker; it does not fabricate a source XML difference.

### Components and Flow

```text
initial new-project capture
  -> capture_source_evidence (no baseline; all lightweight evidence)
  -> initial source export / commit
  -> best-effort v2 evidence tag

later Compare / commit preparation
  -> latest available v2 evidence for master HEAD when present
  -> Agent -> compare_source_evidence
                 acquire Exclusive Access
                 capture all readable evidence
                 filter Instance DBs
                 decide candidates
                 export candidate XML only
                 release Exclusive Access
  -> Agent compares candidate XML to matching master XML
  -> result: source differences | safety differences | untrackable
  -> after a successful commit: best-effort v2 evidence tag for the new SHA

managed-source equality validation
  -> same atomic evidence operation + candidate XML checks
  -> vc_validation_create(v2, HEAD, evidence snapshot)
```

### Contracts, State, and Persistence

| Boundary | Input | Output | Error/state behavior | Compatibility |
|---|---|---|---|---|
| Version Control tag | v2 `sourceEvidence` per device: identity, path, evidence kind, value/read state, and `managedSourceConsistent`; retain checksum and XML hashes | immutable managed-source evidence for one commit when recording succeeds | capture/tag failure is surfaced but leaves the commit intact; untrackable marker remains a separate native-state flag | only rebuilt-codebase projects use v2; legacy workbenches are out of scope |
| Agent -> Engineering MCP | `capture`: PLC and output root; `compare`: PLC, baseline evidence, and output root | live snapshot, candidate list, candidate export results, checksum/F states | lock/cancel/export failure returns no promotable snapshot | new tools; old export tools remain available |
| Engineering -> Agent | candidates contain stable identity, path, kind, reason, and export policy | Agent reads only candidate XML | F-block candidate has `exportPolicy=none` | no F-block XML path introduced |
| Compare result -> Studio | existing differences/safety/timings plus `untrackable` reason field | visible savepoint message in existing panel | untrackable is a native-recording warning, not an Instance DB source difference | existing consumers tolerate additive JSON |

### Worktree and Commit Semantics

One worktree baseline identifies the one native TIA project lineage. New-project creation records the initial full lightweight-evidence snapshot. Later commits retain the existing successful Git commit behavior and then attempt to bind the newest complete evidence snapshot to the resulting SHA. A tag failure remains visible and leaves the commit intact; no legacy migration, branch rollback, or tag-repair transaction is introduced by this feature. A later Compare always re-reads the full lightweight evidence set, so an unavailable tag can affect optimization but not justify treating stale evidence as current.

### Implementation Approach

- Slicing: hybrid.
- Dependency order: verify exclusive access behavior -> define evidence/planning contracts -> implement atomic Engineering capture -> replace Agent staging/compare path -> bind managed-source evidence to validation and expose result -> validate live performance.
- First observable checkpoint: a pure evidence planner returns no XML candidates for unchanged standard blocks/UDTs, timestamp-stable tags, equal F-block signatures, and any Instance DB.
- Rationale: the policy/planner can be tested without TIA; the atomic operation is then the smallest component that removes both double-checksum cost and whole-tree staging.

## Verification Strategy

| Claim / AC | Level | Operation | Observable pass condition |
|---|---|---|---|
| Evidence policy and Instance DB filtering | L1 | focused Engineering/Agent planner tests | exact candidate identities/reasons; no Instance DB candidate |
| best-effort commit evidence | L2 | `Mcp.VersionControl.Tests` + Agent tests | successful capture binds a v2 tag to the same SHA; induced capture/tag failure leaves the commit intact and returns an observable warning |
| Atomic capture/export | L2 | Engineering MCP integration against a connected V17 project | lock spans capture through candidate export; cancellation leaves no promoted candidate root |
| Candidate-only XML behavior | L2 | Agent service tests with fake Engineering result | only returned candidate paths are read/normalized |
| Untrackable behavior | L2 | Agent/API tests | checksum-only unmanaged-native state prompts savepoint, reports no managed source difference, and preserves managed-source consistency |
| F-block behavior | L1/L2 | signature-diff fixtures and live F-CPU probe | changed signature is attributed and no XML export call occurs; equal zero values remain unchanged |
| Runtime/performance outcome | L3 | local Studio Compare with manual TIA edits and retained timing report | unchanged run has zero staged XML indexing; one changed object exports/normalizes only that object |

## Material Risks

| Risk | Evidence | In-scope response |
|---|---|---|
| V17 may limit a read/export operation while Exclusive Access is held | current code uses it for writes; CAx import is explicitly excluded | run a read-only live probe before relying on the new tool; retain full validation fallback on incompatible failure |
| Tag timestamps may not explain all checksum differences | tag tables have no fingerprint provider | checksum-only unmanaged-native state becomes untrackable and prompts a native savepoint without invalidating managed-source consistency |
| Evidence capture/tag write fails after commit | current state write is best effort | preserve the commit, return an observable recording warning, and refresh on the next full capture |
| F-signature provider returns unavailable evidence | existing `ReadSafety` read states | classify unavailable evidence and never suppress it; do not treat `00000000` as unavailable or changed by itself |

## References

- `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`
- `src/Mcp.Engineering/Export/SyncPlanner.cs`
- `src/Agent/Workbench/WorkbenchConsistencyService.cs`
- `src/Agent/Workbench/WorkbenchCoordinator.cs`
- `src/Agent/Workbench/SafeDeviceExportStager.cs`
- `src/Mcp.VersionControl/Git/Models.cs`
- `docs/adr/ADR-0001-mandatory-commit-bound-software-evidence.md`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-06 | 1.0 | Initial design from TIA Compare timing and evidence review |
| 2026-09-06 | 1.1 | Treat `00000000` F-signatures as a normal uncalled-block value; only value changes are safety differences |
