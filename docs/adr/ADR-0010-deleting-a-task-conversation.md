# ADR-0010 Deleting a task's conversation

## Status

Accepted

## Context

A conversation lives in two stores at once, and deleting it has to keep them consistent:

- **A session file** under the worktree's sessions directory. `SessionManager.DeleteSession`
  (`src/Agent/Chat/SessionManager.cs:229-248`) removes it and is idempotent when the file is absent.
- **A graph entity** joined to its task by a `TaskSession` edge. `SessionGraphOperations.Register`
  creates both (`src/ApiHost/EngineeringGraphApi.cs:135-156`), and the task detail's conversation list
  is read back from that edge (`WorkbenchApiModels.cs:2152`).

Today's delete path reaches only the first store. `POST /api/chat/session/delete`
(`CompatibilityEndpoints.cs:515-521`) calls `ApiChatService.DeleteSession` (`:800-811`), which clears the
in-memory chat state and calls `SessionManager.DeleteSession`. Nothing removes the graph entity or its
edge, so after deleting a task-bound conversation the task detail still lists it and the row points at a
session that can no longer be loaded. That is a defect in the existing path, not only a gap in the new
section.

The user asked for a delete option on the navigator's conversation rows, and decided that re-binding a
conversation to another task is out of scope for now. The repository already has the pieces the delete
needs: the client function `deleteChatSession` (`studio/src/api/client.ts:2014`), the service-level
delete, the file-level delete, and `EngineeringGraphService.RemoveEntity`, which removes a graph entity
and both directions of its edges in one call (`EngineeringGraphService.cs:305-309`).

## Decision Point

- **Question**: what does deleting a conversation remove, in which order, and through which route?
- **Why a decision exists**: the two stores cannot be updated atomically, so the order decides which
  inconsistency a failure leaves behind; and the graph edge is not only a lookup — it is the record of
  which task the conversation belonged to, so removing it discards task evidence.
- **Scope boundary**: the delete operation, its route, its ordering, and what the UI asks before it
  runs. It does not add archiving, recovery, or bulk deletion, and it does not change how a conversation
  is created or bound.

## Decision

Deleting a conversation removes its graph entity and edges first, then its session file, through one
shared operation that both the existing compatibility route and a new device-scoped route call. The UI
asks for confirmation first, naming the conversation and stating that the link to its task is lost.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | A hard delete of both stores: graph entity and edges, then the session file, behind a confirmation. |
| **Order** | Graph first, file second. A failure after the graph write leaves an orphan file that nothing points at, which is invisible and re-registerable; the reverse order would leave the task detail listing a conversation that cannot be loaded, which is what the defect already is. |
| **Route** | A device-scoped `DELETE …/worktrees/{wt}/devices/{device}/sessions/{session}`, matching its sibling `GET` routes, so the operation names the context it acts on instead of resolving it from the current selection. The existing `POST /api/chat/session/delete` stays and shares the same operation, so its callers stop leaving dangling edges. |
| **Known unknowns** | Whether a deleted conversation should be recoverable. Nothing is retained today, so adding archiving later changes this decision rather than extending it. |
| **Reconsider when** | Users ask to undo a deletion, or conversations become evidence a task's history must retain. |

### Delete semantics

| Option | Fit | Cost |
|---|---|---|
| Hard delete both stores, graph first | Leaves the readable state consistent with what can actually be loaded | A failure between the two writes can leave an orphan file |
| Hard delete the file only (today) | Smallest change | Leaves a `TaskSession` edge pointing at a missing conversation, so the task detail lists something that cannot be opened |
| Remove the graph entity only | The conversation disappears from every list | Leaves the file on disk, and any stale index or re-registration brings it back |
| Soft delete, keep the file and mark it | Deletion becomes recoverable | Adds a persisted state that every reader — the list, the detail, the counts, the section — has to learn to filter |

**Selected**: the first. It is the only option whose result matches what the user sees, and the ordering
keeps the one failure mode invisible rather than misleading.

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. Hard delete both, graph first | Reuses the existing service, file delete and `RemoveEntity`; needs one shared operation and one route | The task detail and the navigator agree after a delete | None beyond the orphan-file case | One delete path, stated once | Irreversible: the conversation and its task link are gone |
| B. File only | No new code | None — it is the defect | The graph keeps accumulating edges to missing conversations | Two readers disagree about what exists | The cheapest option is also the broken one |
| C. Graph only | The conversation leaves every list | Invisible immediately | Files accumulate with no way to reach them | A silent disk leak | Deletion that does not delete |
| D. Soft delete | Recoverable | Deletion can be undone | A new persisted field plus a filter in every reader | Every consumer must remember the flag | Solves a problem the user has not reported |

**Selected**: A. B and C are what the repository already half-does; D buys recoverability the user did not ask for at the cost of a stored shape.

## Consequences

### Positive Consequences

- After a delete, the navigator's section, the task detail's conversation list and the device chat
  surface all agree: the conversation is gone from each.
- The existing compatibility route stops leaving dangling edges, which fixes a defect that predates the
  navigator section.
- One shared operation means the ordering rule is written once and cannot drift between routes.

### Negative Consequences

- Deleting is irreversible. A conversation is also the record of which task it belonged to, so the
  delete discards that evidence, and the confirmation has to say so.
- A failure between the two writes can leave an orphan session file. It is invisible and costs disk
  only, but nothing in the product reports or reclaims it.
- A delete is not transactional across the two stores, so this decision has to be re-read before any
  change that makes the two stores' consistency more load-bearing.

### Neutral Consequences

No entity kind, relation kind, or persisted shape is added. Conversations are still created and bound
exactly as before, and the session file format is unchanged.

## Architecture Impact

`ApiHost` gains one device-scoped route and one shared delete operation; the existing compatibility
route delegates to it. `Agent.Chat.SessionManager` is unchanged: it already deletes idempotently. The
studio gains a client function beside the existing typed device-scoped helpers and one row-menu item.

## Implementation Guidance

Express the operation once — remove the graph entity, then delete the file — and have both routes call
it, so the ordering cannot be reimplemented differently. Keep the file delete best-effort as it is
today, and let the route report success once the graph entity is gone, because that is what decides
whether the conversation is still reachable. Confirm before deleting, naming the conversation, and
state that its link to the task is lost; the repository's existing destructive task action uses the same
confirmation pattern.

## Related Information

- `docs/adr/ADR-0009-navigator-sessions-section.md` — the section whose rows this operation serves
- `src/ApiHost/CompatibilityEndpoints.cs:515-521, :800-811` — the existing route and service delete
- `src/Agent/Chat/SessionManager.cs:229-248` — the file-level delete
- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:305-309` — `RemoveEntity`
- `src/ApiHost/EngineeringGraphApi.cs:135-156` — the graph binding a conversation is registered with
- `studio/src/api/client.ts:2014` — the existing client delete
