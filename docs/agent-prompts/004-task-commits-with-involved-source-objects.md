# 004. Task page Commits section: which source objects each commit touched

Status: pending
Created: 2026-09-30
Depends on: 002

## Goal

The task page's **Commits** section shows the commits bound to the task, and expanding one shows the
PLC source objects that commit touched, so the user can see what changed and which blocks were
involved. Files that cannot be resolved to a source object are reported as a count rather than
silently dropped.

## Context

- The task's commit edges already exist in the API response: `detail.commits` is an array of
  `{ id, edgeId, provenance, isPrimary }` (`studio/src/api/client.ts:401-407`), populated from the
  task's `GitCommit` edges (`src/ApiHost/WorkbenchApiModels.cs:2127-2142`).
- Today they render as bare ids, and only when non-empty:
  `studio/src/studio/workbench/TaskDetail.tsx:92-96,118,167` (`TraceabilitySection`, `:28-53`).
- Commits are attributed to the task automatically: after an app-mediated commit the active task gets
  a `default` primary edge and explicit extra task ids get `manual` edges
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphCommitAttribution.cs:60-100`, called from
  `src/Agent/Workbench/WorkbenchCoordinator.cs:3514`).
- Commit → source object edges already exist as evidence: `EngineeringGraphEvidenceIndexer.IndexCommit`
  resolves each changed path through the device manifest, registers `"{deviceId}:{sourceId}"` and adds
  a `CommitSourceObject` edge with `GraphProvenance.Evidence`
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphEvidenceIndexer.cs:30-66`), driven by
  `WorkbenchCoordinator.IndexGraphEvidence` (`:3931-3947`, called at `:3517`). Unresolvable paths are
  stored as `graph_file_evidence` (`EngineeringGraphEvidenceIndexer.cs:48-52`;
  `EngineeringGraphModels.cs:32`; table `EngineeringGraphSchema.cs:79-84`).
- The read side is missing: `GET /api/workbenches/{id}/engineering-graph/{entityKind}/{entityId}`
  returns only `tasks` and `commits` for an entity
  (`src/ApiHost/WorkbenchApiModels.cs:734-759`), so a commit's outgoing source-object edges cannot be
  read; `EngineeringGraphEntityDetail` has no field for them (`client.ts:409-416`).
- Navigation gap: `navigateTaskDetail` handles only `session` and `sourceObject`
  (`studio/src/studio/MainStudio.tsx:2129-2133`); `commit` and `svnRevision` fall through to
  `setTraceabilityTarget` with no destination.
- Existing commit presentation to reuse or stay consistent with:
  `studio/src/studio/version-control/VersionControlHistory.tsx:63` (expanded commit shows author,
  time, sha, checksum, "Changed files · N" at `:234` and file rows at `:247-259` — paths only), fed by
  `VersionControlPanel.tsx:99-127`. Per-commit semantic blocks do not exist anywhere in the UI.
- The task detail shape must keep working for `MainStudio.taskChat.test.tsx` and
  `MainStudio.deviceSelect.test.tsx`, which mock `EngineeringTaskDetail`.

## Constraints

- Extend the graph entity read additively: a new response field, or a new read route. Do not change
  the meaning of the existing `tasks` / `commits` fields, and do not change the persisted schema or
  the evidence indexer's edge semantics.
- Keep the existing "Related records" traceability entries for SVN revisions and non-stage source
  object edges; this item only owns the Commits section.
- A commit with no resolvable source objects states that plainly; it must not render an empty list
  that looks like a loading failure.
- Do not backfill or repair attribution here. When attribution or evidence is missing, show what
  exists and name the missing part.
- Follow `docs/STYLEGUIDE.md`; compose existing primitives; add no dependency.

## Done when

1. Component tests prove: the Commits section lists the task's commits with a short hash and message;
   expanding one shows author, timestamp, its source objects (name and category) and the count of
   unresolvable files; a commit with no source objects renders an explicit statement.
2. A server test proves the new or extended read returns a commit's outgoing
   `source_object` evidence edges and its unresolved file evidence without changing the existing
   `tasks` / `commits` fields.
3. Clicking a source object inside an expanded commit navigates to that source object (reuse the
   existing `sourceObject` navigation path); a commit with no destination is not rendered as a
   clickable control that does nothing.
4. `cd studio && npm test -- --run`, `npm run build`, and `dotnet test
   tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` pass.
5. Runtime check with `.\launch.ps1`: open a task that has at least one commit and confirm the
   expanded commit names the touched blocks. If no such task exists locally, state that the check was
   skipped and what remains unverified.

## Evidence

Not yet executed.
