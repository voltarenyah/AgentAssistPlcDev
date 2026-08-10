# Worktree Version-Control Timeline Design

**Date:** 2026-08-10  
**Status:** Approved design

## Goal

Add a modular version-control information area to the worktree Overview page. The area makes the relationship between Git commits, SVN savepoints, and TIA project checksums understandable without requiring users to open the existing version-control workspace.

## User-facing behavior

The component is scoped to the currently selected worktree. It does not expose branch navigation or branch management.

The component appears on the Overview page between Worktree metadata and Tasks. It contains:

- A Git lane at the top.
- An SVN lane below it.
- A horizontal connector line for each lane.
- Small circular Git shapes and small rectangular SVN shapes.
- TIA checksum and the relevant Git hash/revision identifier beneath each shape.
- Dashed vertical connectors from SVN shapes to their corresponding Git shapes.
- A horizontally scrollable rail for dense history.
- The newest 10 Git history entries initially.
- A `Load more` control that requests the next 10 older Git entries and appends them.

There is no separate time strip below the lanes. Timestamp information is available in the hover detail instead. Git and SVN events belonging to the same savepoint use the same rail position so they align visually.

Hovering a shape shows a non-interactive detail surface with:

- Commit or revision message.
- Author.
- Localized timestamp.
- Full Git SHA or SVN revision number.
- TIA project checksum, or `—` when no valid checksum is recorded.
- For an SVN shape, the linked Git SHA.

The component has independent loading, empty, error, and load-more states. A failed timeline request does not prevent metadata, tasks, or modified blocks from rendering.

## Data contract

Add a worktree-level endpoint under the existing API namespace:

```text
GET /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/timeline?offset=0&limit=10
```

The response is designed for incremental rendering rather than exposing raw Git and SVN services to the browser:

```json
{
  "gitCommits": [
    {
      "sha": "full-git-sha",
      "author": "Author",
      "message": "Commit message",
      "timestamp": "2026-08-10T08:00:00Z",
      "tiaChecksum": "PLC_1:checksum-1",
      "svnRevision": 184,
      "files": ["devices/PLC_1/source/Blocks/Main.xml"]
    }
  ],
  "svnRevisions": [
    {
      "revision": 184,
      "author": "Author",
      "message": "Savepoint message",
      "timestamp": "2026-08-10T08:00:00Z",
      "tiaChecksum": "PLC_1:checksum-1",
      "gitCommitSha": "full-git-sha"
    }
  ],
  "hasMore": true
}
```

The initial request uses `offset=0&limit=10`. Each `Load more` request uses the next offset, for example `offset=10&limit=10`, and appends only unseen Git SHA values. The server returns `hasMore: false` when no older Git entries remain.

## History mapping rules

The server owns the mapping because it can read Git blobs and the selected worktree's SVN store without exposing repository paths to the UI.

1. Read Git history newest-first for the requested window.
2. For each Git commit, inspect the changed-file list.
3. Only treat `engineering-state/revision.json` as newly recorded TIA state when that file changed in the Git commit. A later Git-only commit that merely inherits the previous revision file must show `tiaChecksum: null` and no SVN link.
4. Read `revision.json` at that Git commit. If it contains a valid project checksum, return it; otherwise return `null`.
5. Emit an SVN revision only when the commit records a new SVN revision that differs from the previous Git timeline state. Link the emitted revision to that Git SHA.
6. Use the SVN revision metadata stored in `revision.json` for savepoint linkage. Do not include unrelated worktree branch-copy history in this component.
7. Preserve Git-only commits in the Git lane even when they have no checksum or SVN revision.
8. Preserve chronological order newest-first in the API response. The component reverses the display order for a left-to-right older-to-newer rail; newly loaded older entries are inserted at the left while the existing viewport remains stable.

This prevents ordinary local XML commits from incorrectly inheriting a previous TIA checksum or appearing to create a new SVN savepoint.

## Component design

Create a focused component in `studio/src/studio/workbench/WorktreeVersionControlTimeline.tsx` with a small presentation helper module if the alignment calculations need independent tests.

The parent worktree page passes only `workbenchId` and `worktreeId`. The timeline owns fetching, pagination, deduplication, and hover state. It uses existing project UI primitives and CSS variables so light and dark themes remain consistent with the landing page.

The rail uses a shared event-column model:

```ts
type TimelineColumn = {
  git: TimelineGitCommit | null
  svn: TimelineSvnRevision | null
}
```

Git commits provide the ordered columns. SVN entries are placed into the column for their linked Git SHA. Columns without SVN data retain an empty lower slot; columns without a Git entry are not expected for this scoped savepoint model.

Shapes are keyboard-focusable buttons, not passive hover-only elements. `aria-label` text includes the event type and short identifier, and the same detail content is exposed with a focus state so keyboard users receive the hover information.

## Error and edge cases

- No history: show a compact empty state and hide `Load more`.
- Request failure: show a retry affordance in the component and keep surrounding Overview content available.
- No checksum: render `TIA —`.
- Git-only commit: render only the Git shape and keep the SVN column empty.
- Multiple Git commits with the same recorded SVN revision: only the first commit that introduces the revision receives the SVN link.
- Missing or malformed historical `revision.json`: treat checksum and SVN data as absent for that commit; do not fail the whole response.
- Long history: preserve a minimum event width and use horizontal scrolling rather than compressing labels into unreadable text.
- Long messages or author names: truncate in the compact detail surface with the full value available through text wrapping/title semantics.

## Testing strategy

Frontend tests should cover:

- The component requests 10 entries on first render.
- Git SHA, checksum, and SVN revision labels render in their respective lanes.
- Git-only commits render without an SVN shape and show `TIA —` when no checksum is present.
- Dashed-link semantics are present only for linked SVN/Git pairs.
- Hover/focus detail exposes message, author, timestamp, full identifier, and checksum.
- `Load more` requests older entries and appends without duplicate Git commits.
- Empty, error, and retry states do not remove surrounding worktree sections.

Backend/API tests should cover:

- Pagination defaults and `hasMore` behavior.
- A combined TIA/SVN savepoint maps to one Git commit and one linked SVN revision.
- A later Git-only commit does not inherit the previous checksum or SVN link.
- Missing historical revision metadata is tolerated.
- Branch-copy revisions are excluded from the worktree timeline response.

The existing Studio build, lint, and Vitest suite remain the verification baseline.

## Scope exclusions

- No branch switching or branch visualization.
- No mutation actions from this component.
- No replacement of the existing version-control workspace.
- No new filtering, search, zoom, or time-axis controls in the first version.
