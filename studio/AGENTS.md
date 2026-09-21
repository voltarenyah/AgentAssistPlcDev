# Studio (React / Vite frontend) rules

`docs/ui-spec/` holds UI specifications. The root `AGENTS.md` keeps the shared test flow and the
end-to-end workspace smoke scenario.

## Commands

- Tests: `npm test -- --run` (vitest). Lint: `npm run lint` (oxlint).
- Build: `npm run build` = `tsc -b && vite build` — a type error fails the build.
- `tsconfig.app.json` sets `noUnusedLocals` and `noUnusedParameters`: dead locals and unused
  parameters are build failures, not warnings.

## Conventions

- Tests are colocated with the component they cover (`X.test.tsx` beside `X.tsx`); all existing
  test files follow this. A behavior change adds or updates the colocated test.
- Do not add a dependency for what the existing stack already covers (React 19, Vite, Tailwind,
  radix-ui, lucide, cmdk, flexlayout, sonner). A new runtime dependency is a maintainer decision.

## Notion UI components

- The catalog's **Notion Kit** tab previews only a few primitives. It is not the library's
  inventory: the inventory is <https://notion-ui.vercel.app/docs> (22 components and 12 blocks as
  of 2026-09-21). Check that catalogue before writing a new Studio component; when notion-kit
  already ships it, import it instead of building a replacement.
- `@notion-kit/ui` exports more than `primitives`. Also importable as `@notion-kit/ui/<name>`:
  `alert-modal`, `calendar`, `cover`, `icon-block`, `icon-menu`, `kanban`,
  `learning-steps-dialog`, `navbar`, `selectable`, `sidebar`, `single-image-dropzone`, `tags-input`,
  `timeline`, `timezone-menu`, `tree`, `unsplash`. Prefer the specific subpath: the `primitives`
  barrel adds a ~250 kB chunk (85 kB gzip) to the route importing it.
- The shadcn registry route for notion-kit is not published: every endpoint shape the docs and the
  package imply returns 404 (`/registry/notion-ui.json`, `/r/<name>.json`, `/registry.json`, and the
  matching raw GitHub paths), checked 2026-09-21. `registries` therefore stays empty in
  `studio/components.json`; do not add a URL that 404s. Import from the installed package instead.
  The local shadcn config is otherwise usable: `tailwind.css` now points at the real
  `src/assets/main.css`, and the CLI runs as `npx shadcn@latest` here because the repo uses npm.
- Every notion-kit surface needs the `.notion-kit-surface` wrapper and an explicit `Button` size;
  see `docs/STYLEGUIDE.md`.

## Operation status and timing

- Operation timings measured on the server must stay live in the UI: extrapolate locally on a
  **monotonic** clock (`performance.now()`), never wall-clock time.
- A status update must not restart, freeze, or reset a running timer. A delayed snapshot can be
  older than the elapsed time already displayed, so clamp upward instead of replacing it.
- The active phase is keyed by `` `${startedAt}:${message}` `` so a new phase remounts the timer.
  Two consecutive fixes landed in `src/studio/workbench/OperationTimingList.tsx` for exactly this;
  its colocated test locks the behavior in.
