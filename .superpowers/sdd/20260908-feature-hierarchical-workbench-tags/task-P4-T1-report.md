# P4-T1 implementation report

- Added typed client transport models for tag taxonomy nodes, assignments, entity projections, and server search results.
- Added client calls for taxonomy read/create/rename/delete, Workbench and Worktree tag read/assign/unassign, and `POST /api/workbenches/search`.
- Requests use stable IDs and return server-owned direct/inherited/effective/availability projections; no hierarchy or inheritance semantics are computed in the client.

## Validation

- `npm run build` (studio) — passed (`tsc -b` and Vite production build).
- `git diff --check` — passed (Git reported only the repository's LF/CRLF normalization warning).
- `npm test -- --run src/api/client.tags.test.ts` — passed (4 tests covering request URLs, verbs, bodies, response models, and 204 mutation handling).

## Scope / concerns

- No components, pages, or UI integration were changed.
- Added focused fetch-stub serialization coverage in `studio/src/api/client.tags.test.ts` for taxonomy, Workbench/Worktree assignment routes, and server-side search.

## Commit

- `feat: add workbench tag client transport` (scoped commit on the task branch)
