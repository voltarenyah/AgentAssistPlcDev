# ADR-0001: Record Best-Effort Commit-Bound Software Evidence for New TIA Projects

- Status: Accepted
- Date: 2026-09-06
- Decision owner: Product requirement confirmed in the TIA Compare design discussion

## Context

Fingerprint-first Compare needs a complete lightweight evidence snapshot for the Git-managed PLC source domain it can observe. TIA contains native elements and attributes that Openness does not expose as managed source; those elements are intentionally outside Git consistency. A worktree baseline identifies its native TIA project lineage. The current `vc_commit_state_create` path records checksum and safety data after a commit on a best-effort basis; the new v2 record extends that observation to fingerprints and tag timestamps.

This applies only to projects created after the fingerprint-first codebase rebuild. Legacy workbenches and historical commits are explicitly outside this feature's domain.

## Decision

New-project creation shall capture the first complete lightweight evidence snapshot. Each later commit shall make a best-effort attempt to write an immutable v2 validation tag bound to its exact SHA. The tag carries the managed PLC software evidence available at capture time: standard-block and UDT fingerprints, tag-table timestamps, F-block offline signatures, checksum/compile state, object identity/path, read state, and `managedSourceConsistent`.

Evidence capture and tag creation are an observation and recording step after a successful commit; they do not roll back or block the user's Git commit. A capture or tag-write failure must be visible and diagnosable, but its recovery is the next normal full lightweight-evidence capture rather than a migration or transaction-repair workflow.

An untrackable change is still recorded with a complete live snapshot and the existing untrackable marker so a native SVN savepoint can record the non-Git state. It does not negate `managedSourceConsistent` when every managed source object is equal; it is a separate native-recording dimension. Instance DBs and other deliberately unmanaged native elements are excluded from the evidence domain.

## Alternatives Considered

### Capture evidence only when Compare is requested

Rejected. New projects establish their initial snapshot at creation and later commits record fresh evidence best effort, so Compare normally has a recent baseline without legacy migration work.

### Store one mutable snapshot per worktree

Rejected. A mutable worktree record cannot prove which historical commit it describes and is overwritten as the worktree advances.

### Store evidence as a tracked repository file

Rejected. It changes every source commit's tree, risks merge conflicts, and conflicts with the existing immutable validation-tag authority.

## Consequences

- New workbench creation captures a full lightweight evidence snapshot; every later new-project commit attempts a fresh one.
- The existing post-commit best-effort evidence write becomes a richer managed-source evidence record, not a commit-completion transaction.
- Compare always re-reads all live lightweight evidence; a missing prior tag affects optimization but never makes stale evidence current.
- A transient tag-write failure leaves the commit intact, is visible to the user, and is refreshed by the next capture.
- Hardware remains outside the evidence invariant until separately designed.

## Amendment: checksum-authoritative fast gate

The compiled PLC software checksum is the authoritative invariant for the managed software
domain. The `managedSourceConsistent` field remains as historical capture metadata, but a v2
evidence record with an exact commit binding and complete device coverage may use the checksum
fast gate regardless of that flag. Source scans remain available for checksum mismatches,
untrackable changes, safety surfaces, or incomplete evidence.

## Verification

- Repository fixture tests prove a successful evidence recording binds a complete v2 validation tag to the exact commit SHA.
- Induced capture/tag failures prove the commit remains intact and the user receives an observable warning.
- Untrackable commits prove complete managed evidence exists, remain Git-complete when that managed evidence matches, and still surface the native SVN-savepoint requirement.
