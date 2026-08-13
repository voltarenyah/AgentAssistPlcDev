# AgentAssist overview Figma mirror

## Goal

Create an editable Figma design that mirrors the current AgentAssist overview screen at the initial, light-theme state. The Figma output should be a visual reference for the live interface, not a redesign and not a prototype of the interaction states.

## Scope

Included:

- One overview screen at a 1280×720 reference viewport.
- The top utility bar, left Projects/worktree tree, central NewProject overview, Worktrees table, and bottom runtime/status bar.
- Visible labels and example data from the current live screen: NewProject, master, project paths, Purpose, Owner, Worktrees, Ready, API online, balance, and TIA sessions.
- Editable frames, text, rectangles, table rows, controls, and icon placeholders/instances where practical.
- Light-theme typography, spacing, borders, colors, and compact density derived from the live app and its source tokens.

Excluded:

- Workbench Assistant open state.
- Mutation approval cards, dialogs, menus, or hover states.
- Dark theme.
- Runtime behavior, navigation wiring, or code changes to the application.

## Visual specification

- Canvas: 1280×720, white background.
- Top bar: 48 px high, white/card fill, 1 px bottom border.
- Left sidebar: 250 px wide, #fafafa fill, 1 px right border; 48 px local header with Projects label, refresh, and add controls.
- Main content: flexible white region with compact 20 px outer padding and a centered overview column.
- Bottom status bar: 32 px high, compact horizontal status items with a top border.
- Colors: foreground #0a0a0a, muted text #737373, border/input #e5e5e5, muted/accent #f5f5f5, primary #171717, status green/blue accents only where visible in the live UI.
- Typography: Geist-style sans, compact UI sizes approximately 9–11 px for labels and table content, 16–18 px for the project title.
- Shape language: 6–10 px corner radii, thin neutral borders, minimal shadows, no gradients.

## Structure

1. `App / 1280×720` frame.
2. `TopBar` with left tree toggle and right utility icon cluster.
3. `Body` horizontal frame containing `ProjectsSidebar`, resize handle, and `OverviewMain`.
4. `ProjectsSidebar` with Projects header, NewProject row, nested master worktree row, and New linked worktree control.
5. `OverviewMain` with title/metadata header, assistant CTA, Purpose/Owner fields, and a Worktrees card containing the table header and master row.
6. `StatusBar` with readiness, runtime/context, API, balance, sessions, refresh, and settings items.

## Fidelity and editability

The design will be rebuilt as editable Figma layers rather than delivered as a single flattened screenshot. A live-page capture may be used temporarily as a visual reference while composing the editable hierarchy, then removed from the final file if it is not part of the requested mirror.

## Acceptance criteria

- The final Figma file opens as a design file and contains the single overview screen at the agreed reference size.
- The visible layout hierarchy matches the current app: top bar, left project tree, central overview, worktrees table, and bottom status bar.
- Text content and example data match the inspected live screen closely enough for side-by-side review.
- Neutral light-theme tokens, compact typography, spacing, borders, and radii are visibly consistent with the app.
- Major areas remain independently selectable and editable in Figma.
