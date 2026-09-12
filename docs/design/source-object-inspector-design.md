# Design Document: Source Object Inspector

## Overview

- Outcome: Provide a read-only source inspector that renders all source-object types from exact device XML and can compose cross-block network evidence into a temporary visual workspace.
- Scope: Agent source projection, knowledge-query enrichment, workbench API, Studio inspector UI, and focused tests.
- UI Spec: `docs/ui-spec/source-object-inspector-ui-spec.md`
- Governing ADRs: None; this follows existing device-source and knowledge-query responsibilities without a durable architecture choice.

## Requirement Boundary

- PRD or convergence carrier: Confirmed user conversation, 2026-09-11.
- Current requirements: Inspect every source-object category; display program interfaces and networks in their native language form; use a true selectable LAD visualization; discover tag read/write networks across blocks from the knowledge DB; let users open referenced source objects; allow temporary network reordering only.
- Non-goals: XML editing/import; persistent card layouts; callers/callees; generic add-to-workspace actions; treating text-only matches as read/write facts.
- Open requirement fields: None.

## Acceptance Criteria

- **AC-001** — **When** a user right-clicks a source-object row, the system shall offer `Inspect object` for blocks, DBs, tag tables, and UDTs. Source: confirmed requirement.
- **AC-002** — **When** a program block is inspected, the system shall read the selected XML and return its interface sections/members plus independently addressable networks. Source: confirmed requirement.
- **AC-003** — **When** DB, tag-table, or UDT source is inspected, the system shall return a type-specific tabular projection from XML. Source: confirmed requirement.
- **AC-004** — **When** a LAD network is rendered, the system shall show its source topology as selectable graphical elements and wires. Source: confirmed requirement.
- **AC-005** — **When** a tag usage is requested, the system shall discover read/write/mention network locations through the knowledge DB and render the exact XML networks from all returned blocks together. Source: confirmed requirement.
- **AC-006** — **When** a selected rendered element resolves to one source object, the system shall open that object inspector; ambiguous/missing targets shall not navigate incorrectly. Source: confirmed requirement.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Source list/context menu | `studio/src/studio/PlcSourcePanel.tsx` | Extend the existing per-item menu rather than adding a competing selection surface. |
| Secure source resolver | `src/Agent/Workbench/DeviceSourceResolver.cs` | Resolve every requested relative XML path below the selected device source root. |
| Knowledge DB semantics | `src/Mcp.Knowledge/Tools/KnowledgeTools.cs` `get_variable_usage` | Reuse current read/write/mention discovery and extend returned location data with source file identity. |
| XML model | `src/PlcXml.Model/PlcXmlParser.cs` | Reuse safe XML parsing and typed interface/payload concepts; extend its projection where actual fixture formats require it. |
| LAD fixture | `tests/Mcp.Knowledge.Tests/Fixtures/FC_LAD_SimulateCylinder_Call [FC1].xml` | Wire topology is expressed through `Access`, `Part`, `Call`, and `Wire` connector references. |
| Existing API gateway | `src/ApiHost/WorkbenchApiModels.cs` | Add device-scoped read-only routes following the established `id/wt/device` contract. |

## Design

### Selected Design

Add a device-scoped `SourceObjectInspectorReader` in Agent. It accepts a trusted `DeviceContext` and relative source path, resolves it via `DeviceSourceResolver.ResolveEffective`, reads XML once, and projects a discriminated `SourceInspection` DTO. The projection deliberately preserves source identity and language-specific network topology rather than asking the semantic graph to reconstruct XML it never retained.

Program-block inspection projects block metadata, interface sections/members, and network descriptors. LAD descriptors contain access operands, parts/calls with pins, wire endpoint references, title/comment, and compile-unit identity. SCL descriptors contain a source/token projection suitable for formatted read-only display. DB/tag/UDT projections contain their source-specific rows and nested paths.

For tag usage, a new device-scoped API route forwards to an extended `get_variable_usage` result. Each returned row includes its block, block kind, source file, network index, title, direction, and network id. The browser uses that trusted relative source-file identity to request the existing source-inspection route, selects the specified exact XML network, and adds those cards to the same session-local inspector workspace.

The Studio dialog owns the card list. Reordering is local React state through move controls; no browser storage, database, worktree file, or new persisted schema is introduced. The LAD renderer uses rails and connected HTML part elements, with source-wire evidence retained and selectable/focusable parts. SCL remains a readable source view. Element context menus expose `Open referenced object` only when a unique source-object name is available in the current source list.

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| `src/Agent/Workbench` | Add read-only source inspector projection/service and DTOs. | AC-002–004 | Device source resolver security and knowledge staleness lifecycle. |
| `src/Mcp.Knowledge` | Include source file in variable-usage location rows. | AC-005 | Existing read/write/mention semantics and response limits. |
| `src/ApiHost/WorkbenchApiModels.cs` | Add device-scoped inspector and usage routes. | AC-001–006 | Existing compatibility routes and mutation policy. |
| `studio/src/api/client.ts` | Add typed inspector/usage client calls. | AC-001–006 | Existing source action contracts. |
| `studio/src/studio` | Add inspector dialog, object tables, network workspace, LAD/SCL renderers; extend source-row menu. | UI Spec | Existing source list filtering and TIA/compare/chat actions. |
| Focused tests | Add parser/projection, route, and UI interaction coverage. | AC-001–006 | Existing fixture behavior. |

### Components and Flow

```text
source row or selected graph element
  -> Studio inspector dialog
  -> read-only device-scoped inspector API
  -> DeviceSourceResolver -> exact source XML -> SourceInspection DTO
  -> object table / interface table / SCL renderer / LAD topology renderer

tag usage request
  -> knowledge get_variable_usage (read/write/mention + source file)
  -> exact XML network projection for each result
  -> session-local mixed-block network cards
```

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output / exact format | Error or state behavior | Compatibility |
|---|---|---|---|---|
| Studio → inspector API | `GET .../source/inspect?relativePath=<normalized XML path>` | Discriminated `SourceInspection` with object and network projections. | 400 for invalid path; 404 missing file; 422 malformed/unsupported XML with path-safe message. | Additive route. |
| Studio → usage API | `GET .../source/usage?variable=<exact tag>` | Usage rows with `read`/`write`/`mention`, source file, block identity, and network index. The browser then requests the exact source networks. | 404/clear state for missing knowledge DB; direct inspection remains available. | Additive route and fields. |
| ApiHost → knowledge tool | Device DB path plus exact variable | Extend variable-usage row with nullable `sourceFile`. | Preserve existing fields/semantics. | Additive JSON field. |
| Studio dialog state | In-memory selected object and network-card array | Destroyed on close/unmount. | No restoration attempt. | No persistence change. |

### Security Boundary

The inspector never receives an absolute path from the browser. Every source request uses `DeviceSourceResolver.ResolveEffective` against the selected device context, retaining traversal/reparse-point protections. Cross-reference results identify only normalized relative source paths. The browser renders source-derived strings as React text/SVG text rather than injecting XML/HTML.

## Implementation Approach

- Slicing: Hybrid.
- Dependency order: First build and prove the source/usage DTOs and path-safe routes; then implement the dialog/table views; then add graphical LAD rendering and cross-block card workflow.
- First observable checkpoint: Inspect a block from the source row and see its XML-derived interface table plus an SCL network.
- Rationale: Exact source parsing and usage location contracts are shared prerequisites; visible source-type views can then be delivered vertically before the more complex LAD renderer.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| XML projections and topology | L1 | Focused `PlcXml.Model`/Agent tests | Fixture interfaces, simple-object rows, and ladder wire endpoints are exact. |
| Usage location contract | L2 | Focused `Mcp.Knowledge.Tests` and `ApiHost.Tests` | A tag result retains read/write/mention and source file/network index. |
| Secure inspector API | L2 | Focused `ApiHost.Tests` | Valid path returns projection; traversal/missing/malformed paths fail safely. |
| Context-menu/dialog behavior | L1 | `Push-Location studio; npm test -- --run ...; Pop-Location` | Every object category opens an inspector and existing actions remain. |
| Cross-block workspace and LAD selection | L1/L3 | Focused Studio test; launcher/browser smoke if practical | Mixed origin cards render and an LAD element supports selection/context menu. |
| Build compatibility | L2 | `dotnet build AgentAssistPlcDev.sln -v q`; Studio build | All changed layers compile. |

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| TIA LAD exports have diverse parts/pin shapes | Existing fixture contains contacts, coils, TON, SR, calls, and wire references; parser currently has no browser layout. | Define explicit supported shape mapping; render unknown parts as selectable labelled blocks rather than dropping topology. |
| Semantic usage has text-only matches | `get_variable_usage` deliberately returns `mention` when no directional edge exists. | Preserve/label evidence direction in each card. |
| Knowledge DB may be stale/missing | Device knowledge lifecycle is separate from source XML. | Keep direct XML inspection independent and present a distinct usage-discovery state. |
| Large network payloads | Existing knowledge logic is chunked for tool limits. | API projects selected networks only; do not load whole project XML collections. |

## References

- `docs/ui-spec/source-object-inspector-ui-spec.md`
- `src/Agent/Workbench/DeviceSourceResolver.cs`
- `src/Mcp.Knowledge/Tools/KnowledgeTools.cs`
- `src/PlcXml.Model/PlcXmlParser.cs`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-11 | 1.0 | Initial design from confirmed requirements. |
