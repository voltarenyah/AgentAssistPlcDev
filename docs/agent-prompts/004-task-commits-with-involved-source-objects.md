# 004. Task page Commits section: which source objects each commit touched

Status: done
Created: 2026-09-30
Depends on: 002

## Goal

The task page's **Commits** section shows the commits bound to the task, and expanding one shows the
PLC source objects that commit touched, so the user can see what changed and which blocks were
involved. Files that cannot be resolved to a source object are reported as a count rather than
silently dropped.

## Context

- The task's commit edges already exist in the API response: `detail.commits` is an array of
  `{ id, edgeId, provenance, isPrimary }` (`studio/src/api/client.ts:417`), populated from the
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
  read; `EngineeringGraphEntityDetail` has no field for them (`client.ts:425`).
- Navigation gap: `navigateTaskDetail` handles only `session` and `sourceObject`
  (`studio/src/studio/MainStudio.tsx:2260`); `commit` and `svnRevision` fall through to
  `setTraceabilityTarget` with no destination.
- Line numbers here are from commit `366bd16`. `client.ts` and `MainStudio.tsx` shift often; locate
  every anchor by symbol name and confirm its current line before relying on it.
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

Branch `codex/004-task-commit-blocks` on base `27b0f7d` (item 002's final commit). Commits:
`eddd0aa` (backend read + client type), `c5d8e23` (task page Commits section), then the docs commit
that carries this record as the branch tip (its own SHA is not quoted here because quoting it would
change it). Both halves landed; no new route, dependency, schema, or write path.

### What changed

- `src/ApiHost/EngineeringGraphApi.cs` — `EngineeringGraphEntityDetailApiResponse` gains
  `SourceObjects` and `UnresolvedFiles`, documented as filled for a `git_commit` entity only.
- `src/ApiHost/WorkbenchApiModels.cs` — the entity-detail route fills them from
  `EngineeringGraphService.GetEdges(GraphEntityKind.GitCommit, sha, GraphEntityKind.SourceObject)`
  and `GetFileEvidence(sha)`. The existing `tasks` / `commits` projections are untouched.
- `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` — new
  `EngineeringGraphCommitEntityApiReportsItsSourceObjectsAndUnresolvedFilesAdditively`.
- `studio/src/api/client.ts` — `EngineeringGraphEntityDetail.sourceObjects` / `.unresolvedFiles`.
- `studio/src/studio/workbench/TaskCommitsSection.tsx` (new) + colocated test — the Commits section:
  commit list with short hash and message, disclosure per commit, author/timestamp, touched source
  objects with name and category, unresolvable-file count, explicit no-source-object statement.
- `studio/src/studio/workbench/TaskDetail.tsx` (+ test) — the Commits section is this component; the
  generic `TraceabilitySection` it replaced was removed (no other consumer).
- `studio/src/studio/workbench/WorktreeVersionControlTimeline.test.tsx` — its
  `EngineeringGraphEntityDetail` fixture gains the two new fields.

### Read contract added

`GET /api/workbenches/{id}/engineering-graph/{entityKind}/{entityId}` — unchanged request, two new
response fields, filled for `entityKind = git_commit` and `[]` for every other kind:

- `sourceObjects`: `[{ id, edgeId, provenance, isPrimary }]` — the commit's outgoing
  `CommitSourceObject` evidence edges (`provenance: "evidence"`), ordered by `id`. `id` is the
  `"{deviceId}:{sourceId}"` identity `EngineeringGraphEvidenceIndexer.IndexCommit` registers.
- `unresolvedFiles`: `["<worktree-relative path>"]` — the commit's `graph_file_evidence` rows,
  ordered by path.

`tasks` and `commits` keep their exact previous meaning (incoming task edges, and the commits that
touched this entity). Nothing else about the route changed; no persisted schema, indexer edge
semantics, or write path was touched. The UI reads two further existing routes: `GET
.../worktrees/{wt}/vc/log?maxCount=100` for message/author/timestamp, and `GET
.../devices/{deviceId}` (`sourceObjects`) for name/category.

### Done when

1. **Passed.** `cd studio && npx vitest run src/studio/workbench/TaskCommitsSection.test.tsx
   src/studio/workbench/TaskDetail.test.tsx` → 2 files, 17 passed. `TaskCommitsSection.test.tsx` (9)
   proves: short hash + message in the list; the explicit "Message unavailable" statement when the
   worktree history has no row; expansion showing author, year-fixed timestamp, `Function block` /
   `Main` (category + name) and `1 changed file could not be resolved to a source object.`;
   `commit-no-source-objects` stating that plainly instead of an empty list; a source object inside
   an expanded commit calling `onNavigate('sourceObject', 'dev-1:block-main')`; no `Open Commits <id>`
   control and no navigation from the disclosure; no source-object link without an `onNavigate`;
   Remove for a manual edge calling `onRemove('commit', …)`; a failed evidence read naming the error
   and retrying to success. `TaskDetail.test.tsx` (8) keeps the section-composition assertions and
   now asserts the commit is not a link to nowhere.
2. **Passed.** `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter
   "FullyQualifiedName~EngineeringGraphCommitEntityApiReportsItsSourceObjectsAndUnresolvedFilesAdditively"`
   → 1 passed. It registers a commit with two `CommitSourceObject` evidence edges, one task edge and
   two `graph_file_evidence` paths, then asserts `sourceObjects` (2, ids and `evidence` provenance
   in order), `unresolvedFiles` (2, in path order), that `tasks` still reports the task and `commits`
   is still empty for a commit entity, and that the same route for a `source_object` entity still
   reports its task/commit edges with both new fields empty.
3. **Passed.** Covered in the two component tests above: the click path reuses
   `onNavigate('sourceObject', id)` (`MainStudio.navigateTaskDetail` already focuses the source view
   for that kind), and `TaskDetail.test.tsx` asserts no `Open Commits` control exists.
4. **Passed.** `cd studio && npm test -- --run` → 85 files, 548 passed (base 539 + 9 new).
   `npm run build` (`tsc -b && vite build`) → built, 0 errors. `npm run lint` (oxlint) → 0 errors,
   15 pre-existing warnings, none in a changed file. `dotnet build AgentAssistPlcDev.sln -v q` →
   0 errors. `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj -v q` → 178 passed (base 177 + 1).
5. **SKIPPED — not run, not verified.** Deferred by the unattended-run rule for this work unit:
   `.\launch.ps1` would take the shared ports 5173/5239 and no TIA is available, so no
   runtime/browser step was performed. Residual risk: the live rendering of the section against a
   real worktree (real `vc_log` payload, real device manifest, real evidence rows written by
   `IndexGraphEvidence`) is proven only by unit/component tests and fakes. A human must run
   `.\launch.ps1`, open a task with at least one commit, expand it and confirm the touched blocks and
   the unresolvable count match the commit.

### Notes, decisions and risks

- **Decision:** the two fields were added to the existing entity-detail route instead of a new route.
  The response is already entity-shaped, the read is one round trip per expansion, and additive
  fields leave every existing consumer working. A new route would have duplicated the entity lookup
  and the `GRAPH_ENTITY_NOT_FOUND` behavior.
- **Decision:** `sourceObjects` / `unresolvedFiles` are filled for `git_commit` only. A task entity's
  outgoing source-object edges are its *stage* edges (`TaskSourceObject`), not commit evidence;
  returning them in a field read next to `commits` would let a reader mistake a stage for a commit's
  touched object. Other kinds return `[]`, which the server test pins.
- **Known limitation (named, not hidden):** message, author and timestamp come from the worktree's
  bounded Git log, and `vc_log` caps `maxCount` at 100. A task commit older than that window renders
  its short hash plus "Message unavailable: this commit is not in the worktree history this page
  read.", and the expanded panel states the author and timestamp are unavailable. Nothing is
  backfilled. Residual risk: on a worktree with more than 100 commits a task's older commits are
  hash-only. Closing it needs a per-SHA commit-metadata read, which does not exist today; it is a
  follow-up, deliberately out of this item's scope.
- **Known limitation:** name and category are resolved from the devices' current exported manifests at
  read time. An object the manifest no longer lists renders its `{deviceId}:{sourceId}` id with
  `Category unavailable` rather than a guessed name. The commit's touched set itself is unaffected:
  it is read from the persisted edges, not recomputed.
- **Removed:** `TraceabilitySection` in `TaskDetail.tsx`. It existed only to render the Commits list,
  which this item replaces, and no other module imported it (`grep TraceabilitySection` → its former
  definition only). `Related records` keeps its own renderer and behavior unchanged.
- **Not changed:** `studio/src/studio/version-control/**` (item 003) and
  `src/Mcp.Engineering/Tools/**` (item 005) were not touched; attribution was not backfilled or
  repaired anywhere.

