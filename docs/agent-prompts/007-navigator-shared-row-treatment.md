# 007. One shared row treatment in the left navigator: uniform selection, always-gray icons, aligned geometry

Status: pending
Created: 2026-10-03
Depends on: none

## Goal

The left navigator renders one row treatment across all five row kinds — `PROJECTS`, `WORKTREE`,
`DEVICE` (its hardware row and its PLC device rows), `TASKS`, and `SESSIONS`: the current row is marked
with `bg-accent/50` and nothing else, every row's icon stays `text-muted-foreground` in every state, and
every row's icon starts at the same x with the same `mb-1` rhythm between rows.

Before, measured on the maintainer's 2x screenshot of a live session and confirmed in the source:

| Row kind | Container | Icon | Current-row marker |
|---|---|---|---|
| `PROJECTS` | `rounded-sm px-1 py-1` + `mb-1`, no border | `Factory`, always gray | `bg-accent/50` |
| `WORKTREE` | same, but **no `mb-1`** | `GitBranch`, **always `text-chart-4` (blue)** | `bg-accent/70` with a target selected, `bg-accent` without |
| `DEVICE` (hardware + device rows) | same as `PROJECTS` | gray, or **`text-chart-2` (blue) when current** | `bg-accent` (full) |
| `TASKS` (`TaskRow`) | **`rounded-md border px-2 py-1 pr-8`**, no `mb-1` | type icon, always gray | **`border-ring/70` ring, no background** |
| `SESSIONS` (`SessionRow`) | the same `TASKS` card | `MessageSquareText`, always gray | **none at all** |

The two visible misalignments follow from that: the `TASKS`/`SESSIONS` icon box sits ~3.5 CSS px right of
the `PROJECTS` one (`px-2` + 1px border versus `px-1`; measured 30 vs 37 image px at 2x), and `TASKS` rows
touch each other (row pitch 63 image px = the 32px row) while `PROJECTS` rows keep a 4px gap (71 image px).

## Context

All anchors are in `studio/src/studio/workbench/WorkbenchNavigator.tsx` (1361 lines at `a79c76f`), by
symbol because the file is being edited in parallel elsewhere:

- The reference treatment is the `PROJECTS` row: `:1013`
  `group mb-1 flex min-h-8 cursor-pointer items-center gap-2 rounded-sm px-1 py-1` with
  `${workbenchSelected ? 'bg-accent/50' : 'hover:bg-accent/40'}`, `aria-current` on the current row, and
  `:1017` `<Factory className="h-4 w-4 text-muted-foreground" />`.
- `TaskRow` `:364-409`: row class at `:371`
  (`relative flex min-h-8 w-full items-center gap-2 rounded-md border px-2 py-1 pr-8 text-left hover:bg-accent/40` + `border-ring/70` when selected), icon `:377`.
- `SessionRow` `:429-…`: row class at `:436` (the same card with `border-transparent`), icon `:440`, no
  `selected` prop and no current marker. Its row menu is the absolute `DropdownMenuTrigger` at `:446`
  (`right-1`), which is why both rows carry `pr-8`.
- `DEVICE` rows in `renderDeviceSection` `:722-849`: hardware row class `:739` (`bg-accent` when current)
  and icon `:744` (`text-chart-2` when current); device row class `:784` (`bg-accent`) and icon `:789`
  (`text-chart-2`); the knowledge dot `:791` uses `knowledgeDotClass` (`:144-148`).
- `WORKTREE` row `:1124-1215`: class `:1129-1135` (`bg-accent` when current with no target selected,
  `bg-accent/70` when a target is selected, `hover:bg-accent/40` otherwise), `aria-current` `:1136`, the
  disclosure toggle `:1152-1154`, and `GitBranch` `:1155` with `text-chart-4`.
- Group headings that align with the row icon column today and must keep doing so:
  `SESSIONS` `:938` and the unbound group's `NO TARGET` `:1265`, both `px-2`.
- `TaskRow` is shared by `TASKS` (`:890`) and by the unbound-task group inside `WORKTREE`
  (`:1263-1279`, group container `ml-4 border-l py-0.5 pl-2`), so one change to it covers both.
- The `Add task` button `:864-867` sits below the task rows; it is a `Button` `size="xs"`
  (`studio/src/components/ui/button.tsx:23`: `h-6 gap-1 rounded-md px-2 … has-[>svg]:px-1.5`).

Where the divergence came from, so it is not reintroduced:

- `f78e517` (2026-09-21, `feat: refine task navigation`) established the shared navigator row:
  `rounded-sm px-1 py-1` with `bg-accent/50` for the current row, and a gray icon.
- `dc4a094` (2026-09-21, `feat: add task navigator actions`) replaced the task row with the
  `rounded-md border px-2 py-1 pr-8` card and the `border-ring/70` current marker.
- `fdf9ec6` (`feat: list a target's task conversations in the navigator`) copied that card into
  `SessionRow`. The `text-chart-2` / `text-chart-4` icon colours and the three background intensities
  came in with the same line of work.

No document records the three intensities or the icon recolouring: `docs/STYLEGUIDE.md:57-59` says only
that `accent` is for hover or active list rows, and `:73-74` that icons inherit the surrounding text
colour, which is what this change implements. The one document that has to move with the code is
`docs/ui-spec/studio-information-architecture-ui-spec.md`: its Visual Constraints row **Session rows**
(`:111`) says a session row reuses the task surface's conversation row treatment, and the change replaces
that chrome with the navigator's own row treatment.

The session marker is new state, not a new selection: `MainStudio.tsx:541` already holds
`chatTabs.activeId`, which is the open conversation's `sessionId` (compared against `tab.sessionId` at
`studio/src/studio/chat/ChatWorkspace.tsx:434`), and `MainStudio.tsx:2470` renders the navigator without
passing it.

## Constraints

- **One row class set, five kinds.** Every row container carries the same geometry:
  `group mb-1 flex min-h-8 w-full cursor-pointer items-center gap-2 rounded-sm px-1 py-1 text-left`, plus
  `relative pr-8` for `TASKS` and `SESSIONS` (their 3-dots menu is absolutely positioned at `right-1`, and
  removing `pr-8` would let the title run under it). No `border`, no `rounded-md`, no `border-ring*`
  anywhere in the navigator's rows. Keep `min-h-8`, `gap-2` and the `h-4 w-4 shrink-0` icon size: they are
  what makes the geometry identical rather than merely similar.
- **One current-row marker.** `bg-accent/50` when the row is the current one, `hover:bg-accent/40`
  otherwise, for all five kinds. The `DEVICE` row's and `WORKTREE` row's stronger `bg-accent` and
  `bg-accent/70` go away; the cascade already says which level the user is at through which sections are
  populated and which row in each carries `aria-current`.
- **Icons never change colour with state.** Remove `text-chart-2` from the hardware and device icons and
  `text-chart-4` from `GitBranch`; every row icon is `text-muted-foreground`. Semantic status colours are
  **not** row identity and stay exactly as they are: `knowledgeDotClass`'s emerald/amber/red/muted dot
  (`:144-148`, `:791`), the amber "Unavailable" worktree badge (`:1158-1166`), and the mono muted branch
  text (`:1157`).
- **Keep every existing hook and contract**: `aria-current` on the current row of each kind,
  `data-task-selected` / `data-task-type` (`:374-375`), `data-worktree-row` (`:1137`),
  `data-device-target` (`:741`, `:786`), `data-session` / `data-session-open` (`:432`, `:438`), the
  row `<button>` with its `aria-label`, the 3-dots menu visible without hovering (ADR-0009 AC-017),
  the `WORKTREE` disclosure toggle `:1152-1154` and the unbound group's structure and indentation, the
  section headers, separators, collapse behaviour and tag-filter behaviour.
- **The session marker is a marker, not a selection change.** Add an optional
  `activeSessionId?: string | null` prop (default `null`) and mark that conversation's row with
  `bg-accent/50` and `aria-current`. `MainStudio.tsx` passes `chatTabs.activeId`. ADR-0009 AC-018 — opening
  a conversation leaves the navigator's workbench, worktree, device and task selection untouched — must
  still hold: the marker must not move any other row's `aria-current`, and a conversation that is not the
  open one must render exactly as before.
- **Group headings follow the column.** `SESSIONS` `:938` and `NO TARGET` `:1265` move from `px-2` to
  `px-1`: they are aligned with the row icon column today (8px versus 9px) and would otherwise be left
  4px right of it.
- **Leave the `Add task` button alone.** Its icon lands within about 2px of the row icon column after this
  change; overriding the primitive's own `px-1.5` for an exact match would contradict
  `docs/STYLEGUIDE.md:70-72` (use the primitive's established size rather than a bespoke override).
- **Docs: the UI spec only.** Add one Visual Constraints row stating the shared row treatment (geometry,
  `bg-accent/50`, always-gray icons, the semantic-status exception), reword **Session rows** (`:111`) so
  it no longer claims the task surface's row chrome, and add an Update History entry (v1.9). Do not edit
  `docs/STYLEGUIDE.md`, the ADRs or the design documents: they already state what this change implements.
- Follow `docs/STYLEGUIDE.md`, the colocated-test convention in `studio/AGENTS.md`, and add no dependency.
- Runtime/browser verification is deferred by the unattended-run rule: `.\launch.ps1` binds the shared
  ports 5173/5239. Name what that leaves unproven in the Evidence rather than implying it was seen.

## Done when

1. A new case in `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` renders all five row kinds with
   one current row each and asserts the **equality of the class sets across kinds**, not the presence of
   individual classes: every row container's classes are the same set (the `TASKS`/`SESSIONS` `pr-8`
   excepted), every row icon's classes are the same set in both the current and a non-current row of every
   kind, none of those classes matches `/text-chart-\d/`, `rounded-md`, `border-ring` or a bare `border`,
   and every current row carries `bg-accent/50` while its non-current sibling does not.
2. A case proves the spacing and the headings: the `TASKS` and `SESSIONS` rows carry `mb-1` like the
   `PROJECTS`/`DEVICE` rows, the `WORKTREE` row carries it too, and the `SESSIONS` group heading and the
   unbound group's `NO TARGET` heading carry `px-1`.
3. A case proves the session marker: with `activeSessionId` set to a listed conversation, only that row
   carries `bg-accent/50` and `aria-current`, and the current device row and current task row keep their
   own `aria-current`; with the prop omitted the navigator renders as it does today (AC-018 unaffected).
4. `rg -n "rounded-md border|text-chart-[24]|border-ring" studio/src/studio/workbench/WorkbenchNavigator.tsx`
   returns nothing; `rg -n "bg-accent" studio/src/studio/workbench/WorkbenchNavigator.tsx` shows only
   `bg-accent/50` for current rows and `hover:bg-accent/40`.
5. `cd studio && npm test -- --run` passes, `npm run build` (`tsc -b && vite build`) succeeds, and
   `npm run lint` reports no new warning.
6. A discrimination check is recorded in the Evidence: restoring one row's pre-change classes (for example
   the `TaskRow` card) makes Done-when 1 fail, so the equality assertions are not vacuous.
7. `docs/ui-spec/studio-information-architecture-ui-spec.md` carries the new Visual Constraints row, the
   reworded **Session rows** row, and update-history v1.9 naming this change.

## Evidence

<Filled in by the executing agent: commands run, results, branch, commit, skipped steps and risk.>
