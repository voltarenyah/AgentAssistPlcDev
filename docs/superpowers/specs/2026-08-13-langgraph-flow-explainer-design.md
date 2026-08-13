# LangGraph Workflow Explainer Design

Date: 2026-08-13

## Goal

Create a developer-only standalone webpage that explains the full Workbench App Assistant lifecycle in this repository. The page should make the relationship between the React Studio, ApiHost SSE bridge, Python LangGraph graph, and C# Workbench Gateway easy to understand through an interactive flow diagram with hover detail.

## Scope

In scope:

- The browser lifecycle from Studio request to ApiHost, LangGraph, gateway, and response.
- The Python graph nodes and conditional routes currently implemented in `agent-service/app_assistant/graph.py`.
- Orientation, direct-answer/clarification, read-only tool, and approval-gated mutation paths.
- LangGraph checkpoint/thread behavior and approval resume semantics.
- The ApiHost event sequence: `progress → state → interrupt → answer`.
- Hover-to-preview detail, click-to-pin an inspector, path highlighting, and a small path selector.
- Responsive behavior for a normal desktop browser and a narrow viewport.

Out of scope:

- Live API calls or live graph execution.
- Editing graph code from the explainer.
- Adding the page to normal product navigation or exposing it in the production UI.
- Replacing the existing assistant UI.

## Placement and entry point

Implement the page in the existing React/Vite Studio as a dev-only direct route or query entry point that does not appear in normal navigation. The route should render the explainer without requiring an active workbench selection or a running LangGraph sidecar.

The page should be isolated from `MainStudio` product flows. A lightweight route switch in the top-level app is acceptable if it can be gated by a development-only condition; otherwise a dedicated dev entry should be used. The implementation must follow existing TypeScript, React, Tailwind/CSS, and Lucide conventions where practical.

## Visual direction

Use a terminal/runtime aesthetic:

- Near-black graphite background with green terminal text and amber approval highlights.
- Monospace labels for nodes, event names, code references, and state keys.
- Warm off-white body copy for explanations.
- Thin technical borders, subtle grid/noise texture, and restrained glow around active nodes.
- Avoid a generic dashboard layout; the graph canvas is the primary surface.

## Information architecture

The page contains:

1. Header: title, short description, `DEV EXPLAINER` badge, and a compact legend.
2. Path controls: `All paths`, `Orientation`, `Read-only`, and `Mutation + approval`.
3. Main canvas: four swimlanes — Studio, ApiHost, LangGraph, Gateway — with nodes and directed connectors.
4. Inspector: selected node/edge title, plain-language explanation, implementation reference, inputs/outputs, and safety notes.
5. Event strip: the concrete SSE sequence for the current path.
6. Footer note: explains that the diagram is a static map of the current implementation and should be updated when graph topology changes.

## Graph model

Represent the diagram as typed local data so the visual components remain presentational. Each node includes:

- `id`, `lane`, `label`, `kind`, `summary`
- `detail`, `reference`, `inputs`, `outputs`
- optional `safety` and `event` metadata

Edges include:

- `from`, `to`, `label`
- optional `condition`, `paths`, or `emphasis`

The core LangGraph topology is:

```text
START → bootstrap_context
bootstrap_context → orient_with_llm | decide_with_llm
orient_with_llm → END
decide_with_llm → answer_decision | execute_read_tool | propose_mutation
answer_decision → END
execute_read_tool → summarize_tool_result → END
propose_mutation → END
```

The explanatory model must make the `interrupt()` inside `propose_mutation` visible as an approval gate even though the compiled graph edge continues to the node's returned state after resume.

## Lifecycle paths

The path selector highlights these sequences:

- Orientation: Studio bootstrap → ApiHost progress → LangGraph bootstrap context → orientation model → ApiHost state/answer → Studio.
- Read-only: Studio chat → ApiHost progress → LangGraph context → decision → gateway read → summarize → ApiHost state/answer → Studio.
- Mutation: Studio chat → ApiHost progress → LangGraph context → decision → proposal → `interrupt()` → ApiHost interrupt → Studio approval → ApiHost resume → gateway mutation → refreshed state → answer.

The `All paths` mode keeps the complete topology visible while de-emphasizing non-selected routes. Hovering a node or edge temporarily previews its highlight and tooltip; clicking pins the inspector. Keyboard focus must provide the same detail access as hover.

## Component boundaries

- `LangGraphFlowPage.tsx`: owns selected item, hover item, active path, and responsive layout.
- `LangGraphFlowCanvas.tsx`: renders lanes, nodes, edge paths, SVG/HTML connectors, and accessible labels.
- `LangGraphFlowInspector.tsx`: renders selected item detail and empty state.
- `langgraphFlowData.ts`: typed nodes, edges, lifecycle events, path definitions, and copy.
- `langgraph-flow.css`: visual system, responsive rules, animations, tooltip/inspector states.

Prefer HTML/SVG over a new graph library so the page stays dependency-free, inspectable, and easy to update alongside the Python graph.

## Error and safety explanation

The inspector should explicitly call out:

- Model failures fall back to clarification rather than unsafe action.
- Read tools are allowlisted and require a selected worktree.
- Mutations do not execute before approval.
- Approval resumes the checkpointed thread with a `Command(resume=...)` value.
- Stale workbench revisions trigger refresh/re-plan behavior.

These are explanations only; the explainer must not execute mutations or call the app APIs.

## Validation

- Add focused component tests for route visibility, path selector changes, hover/click inspector behavior, and keyboard focus.
- Run the Studio test suite and production build.
- Start the local launcher and verify the direct dev URL renders while the normal app UI does not expose a navigation entry.
- Check the browser console for local application errors.

## Acceptance criteria

- A developer can understand the full Studio → ApiHost → LangGraph → Gateway lifecycle without reading the source first.
- Hovering any meaningful node or edge shows concise detail; clicking pins richer detail in the inspector.
- The three representative paths visibly highlight the correct branches.
- Approval and SSE event ordering are visually explicit.
- The page is usable without a selected workbench and does not perform real actions.
- Existing product screens and tests remain unaffected.
