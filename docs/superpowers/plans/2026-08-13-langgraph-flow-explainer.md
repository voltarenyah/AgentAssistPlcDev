# LangGraph Workflow Explainer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a developer-only standalone React page that visually explains the full Studio → ApiHost → LangGraph → Gateway lifecycle with hover details, pinned inspection, and highlighted workflow paths.

**Architecture:** Add a dev-only direct route switch in `App.tsx` that renders a static explainer page without touching `MainStudio`. Keep the workflow model in typed data, render the main map with accessible HTML/SVG primitives, and keep inspector/path-selection state local to the explainer.

**Tech Stack:** React 19, TypeScript, Vite, Lucide React, existing CSS/Tailwind setup, Vitest + happy-dom.

---

## File map

- Create `studio/src/dev/LangGraphFlowPage.tsx` — page state, header, controls, canvas/inspector composition, and event strip.
- Create `studio/src/dev/LangGraphFlowCanvas.tsx` — lane layout, node cards, connector lines, hover/click/focus behavior.
- Create `studio/src/dev/LangGraphFlowInspector.tsx` — selected node/edge detail panel and empty state.
- Create `studio/src/dev/langgraphFlowData.ts` — typed nodes, edges, path definitions, and lifecycle event definitions.
- Create `studio/src/dev/langgraph-flow.css` — terminal/runtime visual system and responsive rules.
- Modify `studio/src/App.tsx` — render `LangGraphFlowPage` only when `import.meta.env.DEV` and the URL query is `?dev=langgraph-flow`; otherwise preserve the existing shell.
- Create `studio/src/dev/LangGraphFlowPage.test.tsx` — route gating, path controls, selection, hover/focus detail, and no-API behavior.

## Task 1: Add typed workflow data and tests for its topology

**Files:**
- Create: `studio/src/dev/langgraphFlowData.ts`
- Create: `studio/src/dev/langgraphFlowData.test.ts`

- [ ] **Step 1: Write the failing topology tests.** Assert the data includes the four lanes, all graph node IDs, the orientation/read-only/mutation path IDs, and the SSE event order `progress`, `state`, `interrupt`, `answer`.
- [ ] **Step 2: Run the focused test.** Run `Push-Location studio; npm test -- --run src/dev/langgraphFlowData.test.ts; Pop-Location`. Expected: fail because the module does not exist.
- [ ] **Step 3: Implement the data model.** Define `LaneId`, `FlowPathId`, `FlowNode`, `FlowEdge`, `FlowPath`, and `LifecycleEvent` types. Add nodes for Studio bootstrap/chat/approval, ApiHost progress/state/interrupt/resume/answer, LangGraph `START`, `bootstrap_context`, `orient_with_llm`, `decide_with_llm`, `answer_decision`, `execute_read_tool`, `summarize_tool_result`, `propose_mutation`, `interrupt()`, and `END`, plus Gateway read/mutation/refresh. Add edges with route labels and path membership. Keep all explanations tied to current source references.
- [ ] **Step 4: Run the focused test.** Expected: PASS with the complete topology and exact event ordering.

## Task 2: Build the canvas and inspector presentation

**Files:**
- Create: `studio/src/dev/LangGraphFlowCanvas.tsx`
- Create: `studio/src/dev/LangGraphFlowInspector.tsx`
- Create: `studio/src/dev/langgraph-flow.css`

- [ ] **Step 1: Implement the canvas contract.** Accept `nodes`, `edges`, `activePath`, `hoveredId`, `selectedId`, and callbacks. Render four labeled swimlanes and node buttons with `aria-label`, `tabIndex`, and `onMouseEnter`/`onMouseLeave`/`onFocus`/`onClick` handlers. Render connectors as absolutely positioned SVG paths or HTML lines; mark inactive edges with reduced opacity and active edges with `data-active="true"`.
- [ ] **Step 2: Implement the inspector contract.** Accept a selected `FlowNode | FlowEdge | null`; show title, kind, explanation, source reference, input/output chips, and optional safety note. Show a readable empty state when no item is selected.
- [ ] **Step 3: Add terminal/runtime styling.** Use graphite, green, amber, and warm white CSS variables; add subtle grid background, lane separators, node glow, hover tooltip, keyboard focus ring, and responsive stacking under 1000px.
- [ ] **Step 4: Run TypeScript validation.** Run `Push-Location studio; npm run build; Pop-Location`. Expected: compilation succeeds once the page imports are added in the next task; if the isolated files are not yet imported, use the focused test after Task 3 instead.

## Task 3: Compose the page and add dev-only entry routing

**Files:**
- Create: `studio/src/dev/LangGraphFlowPage.tsx`
- Modify: `studio/src/App.tsx`

- [ ] **Step 1: Write the failing page tests.** Assert normal render does not show `DEV EXPLAINER`; set `window.history` to `/?dev=langgraph-flow` and mock `import.meta.env.DEV` via the test environment or export a small `isLangGraphFlowDevRoute(url, isDev)` helper; assert the page renders only when both are true. Assert no `fetch` call is made during render.
- [ ] **Step 2: Implement page state.** Track `activePath`, `hoveredId`, and `selectedId`. Add `All paths`, `Orientation`, `Read-only`, and `Mutation + approval` buttons. Pass derived active node/edge IDs into the canvas, use hover as transient preview, and use click/focus to pin the inspector. Render lifecycle events for the current path and a footer note that the map is static.
- [ ] **Step 3: Implement the route switch.** In `App.tsx`, check `import.meta.env.DEV && new URLSearchParams(window.location.search).get('dev') === 'langgraph-flow'`; render `LangGraphFlowPage` directly in that case, otherwise keep the existing `ErrorBoundary → TooltipProvider → MainStudio → Toaster` tree unchanged.
- [ ] **Step 4: Run focused page tests.** Expected: PASS for route gating, controls, selection, hover/focus details, and no-API behavior.

## Task 4: Add interaction tests and accessibility checks

**Files:**
- Modify: `studio/src/dev/LangGraphFlowPage.test.tsx`

- [ ] **Step 1: Test path highlighting.** Click `Read-only`, assert the read path button is selected, the read gateway node is active, and the mutation interrupt is not active. Click `Mutation + approval`, assert `interrupt()` and the approval event are visible and active.
- [ ] **Step 2: Test inspector behavior.** Fire `mouseEnter` on `decide_with_llm`, assert the concise hover detail appears; click the node, assert source reference and input/output details are pinned; fire keyboard focus on `propose_mutation`, assert the same detail is reachable without a mouse.
- [ ] **Step 3: Test safety copy.** Assert the mutation inspector includes the approval-before-execution note and the stale-revision refresh note.
- [ ] **Step 4: Run the focused tests.** Run `Push-Location studio; npm test -- --run src/dev/LangGraphFlowPage.test.tsx; Pop-Location`. Expected: PASS.

## Task 5: Verify build and local developer flow

**Files:**
- No new files; inspect the changed Studio files and existing route behavior.

- [ ] **Step 1: Run the full Studio tests.** Run `Push-Location studio; npm test -- --run; Pop-Location`. Expected: all existing and new tests pass; treat repeated `ECONNREFUSED localhost:3000` messages as noise only if the command still passes.
- [ ] **Step 2: Run the production build.** Run `Push-Location studio; npm run build; Pop-Location`. Expected: Vite build succeeds with no TypeScript errors.
- [ ] **Step 3: Start the repository launcher.** From the repository root run `./launch.ps1`, wait for health checks, and confirm the frontend, ApiHost, and sidecar respond as documented in `AGENTS.md`.
- [ ] **Step 4: Verify the direct dev page.** Open `http://localhost:5173/?dev=langgraph-flow`, confirm the explainer renders without a workbench selection, test all path buttons, hover/focus a node, click to pin the inspector, and check the browser console for local application errors.
- [ ] **Step 5: Verify normal UI isolation.** Open `http://localhost:5173/` without the query and confirm the normal Studio renders without a LangGraph Flow navigation item.
- [ ] **Step 6: Review the diff.** Run `git status --short` and `git diff --check`; preserve unrelated existing changes and report the test totals and any environment warnings.
