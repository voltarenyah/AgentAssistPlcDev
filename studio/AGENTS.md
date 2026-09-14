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

## Operation status and timing

- Operation timings measured on the server must stay live in the UI: extrapolate locally on a
  **monotonic** clock (`performance.now()`), never wall-clock time.
- A status update must not restart, freeze, or reset a running timer. A delayed snapshot can be
  older than the elapsed time already displayed, so clamp upward instead of replacing it.
- The active phase is keyed by `` `${startedAt}:${message}` `` so a new phase remounts the timer.
  Two consecutive fixes landed in `src/studio/workbench/OperationTimingList.tsx` for exactly this;
  its colocated test locks the behavior in.
