# ADR-0003 Task-scoped source evidence

## Status

Accepted

## Context

Fingerprint-first Compare currently records a complete live TIA source-evidence snapshot after every Git commit. That is correct only when the commit contains every live source change. With concurrent task work, committing Task A while Task B remains changed in TIA would bind Task B's live fingerprint to a Git commit that does not contain Task B's XML.

## Decision Point

- **Question**: How should source fingerprints be retained when one task commits while other task changes remain live in TIA?
- **Why a decision exists**: A complete live snapshot is simple but can hide uncommitted work; preserving a synthetic full snapshot is correct but unnecessary state copying; per-object evidence can remain tied to the exact Git source content.
- **Scope boundary**: Task-scoped TIA compare and Git commits. Full project compare and native SVN savepoints remain project-wide operations.

## Decision

Use device-bound task stages and sparse per-object evidence. A task-stage scan reads only the task's staged source objects. A task commit records fingerprint evidence only for source objects included in that commit, bound to their committed source content. It never records a full live TIA fingerprint snapshot.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | Task-stage evidence is per object and per committed Git content; full scans are the only project-wide truth check. |
| **Why this** | It preserves Task B's old Git baseline while Task B has uncommitted live changes. |
| **Known unknowns** | Live TIA verification remains required to confirm the Engineering MCP can capture an arbitrary scoped set under Exclusive Access. |
| **Reconsider when** | TIA exposes an atomic project-level source revision that can identify the exact Git source set without scanning objects. |

## Consequences

- A task is bound to one device and owns an active stage list of source-object IDs.
- A source object has at most one active task owner in a worktree. Completing a task releases ownership but retains task history.
- Task-clean means only that task's stage matches; it cannot claim the project is clean.
- A full scan finds changed unassigned source objects. They must be assigned or resolved before an SVN savepoint or another project-wide TIA operation.
- Project checksum captured during an ordinary Git commit is observation-only and is not source-difference evidence.

## Related Information

- `docs/adr/ADR-0001-mandatory-commit-bound-software-evidence.md`
- `docs/design/tia-compare-fingerprint-first-design.md`
