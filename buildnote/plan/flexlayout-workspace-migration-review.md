# Codebase Review: Migration to a FlexLayout Dockable Workspace

> **Pre-flight blocker:** `studio/src/studio/MainStudio.tsx` is currently **mid-merge with unresolved conflict markers** (`git status` = `UU`; markers at lines 30–38 and 539–548 against branch `voltarenyah/app-settings-page`). The file cannot compile as-is. Resolve this merge before any migration work. Some observations below straddle both sides of that merge; where it matters it is flagged.

---

## 1. FRONTEND STACK

| Concern | Reality (from `studio/package.json`, `vite.config.ts`, `tsconfig*.json`) |
|---|---|
| Desktop framework | **WinForms + WebView2** shell (`src/AutomationWorkbench.Desktop`, `Microsoft.Web.WebView2 1.0.4078.44`). In production WebView2 navigates to the ApiHost origin which serves `studio/dist` from `wwwroot`; in dev it's a plain browser SPA against Vite (`localhost:5173`). No Electron/Tauri. |
| React | **19.2.7** (`react`, `react-dom`), `StrictMode` enabled in `main.tsx` |
| TypeScript | `~6.0.2`, `tsc -b` project refs (`tsconfig.app.json` / `tsconfig.node.json`) |
| Build | **Vite 8** (`@vitejs/plugin-react`), `@` → `./src` alias, dev proxy `/api` → `localhost:5239` |
| Package manager | npm (`package-lock.json`) |
| UI libraries | `radix-ui`, `cmdk`, `sonner` (toasts), `lucide-react` icons, `react-markdown` + `remark-gfm`, `react-colorful`, `@floating-ui/dom`, `class-variance-authority`, `clsx`, `tailwind-merge`, `tw-animate-css` (shadcn-style `components/ui`) |
| Styling | **Tailwind CSS 4** (`@tailwindcss/vite`) + CSS variables; global styles in `studio/src/assets/main.css` |
| State management | **None** (no redux/zustand/jotai). One god component (`MainStudio`) with ~40 `useState`s + pure reducer modules (`*State.ts`) + exactly one module store (`theme.ts` via `useSyncExternalStore`) |
| Router | **None.** No react-router; a dev-only `isLangGraphFlowDevRoute` check in `App.tsx` is the only "routing" |

## 2. APPLICATION SHELL

- Root: `studio/src/main.tsx` → `studio/src/App.tsx` (`ErrorBoundary` → `TooltipProvider` → `MainStudio` + `Toaster`).
- Global shell, left explorer, top nav, main container, and status bar are **all one component**: `studio/src/studio/MainStudio.tsx` (2512 lines).
  - **Top nav**: `<header>` at `MainStudio.tsx:1794` — dock toggles, Settings/MCP-tools page switches, operation status, Workbench Assistant button, `ThemeToggle`.
  - **Left explorer**: `studio/src/studio/workbench/WorkbenchNavigator.tsx` inside a `data-dock="left"` shell div (`MainStudio.tsx:1865-1944`), width/visibility from `shellLayout`, pointer-drag resizer at `:1945-1952`.
  - **Main content**: `<main>` at `MainStudio.tsx:1954-2284`.
  - **Right "context dock"**: `data-dock="right"` at `:2285-2345` — swaps between `DevicePropertiesDock`, `KnowledgePropertiesDock`, `VersionControlDetailsDock`, `SessionDock`, `HardwarePropertiesDock` depending on tab.
  - **Status bar**: `<footer data-status-bar>` at `:2362-2421` — workbench/worktree/device pill, `RuntimeStateStatusBar`, DeepSeek API status/balance, TIA sessions popover.
  - **Overlay panels/dialogs**: `AppAssistantPanel` (right-side overlay, `:2346-2359`) and ~8 modal dialogs appended after the footer.

Hierarchy: `App → MainStudio → { header | left dock(WorkbenchNavigator) | main(view switch) | right dock(*Dock) | AppAssistantPanel | footer | dialogs }`. There is no shell/layout component layer between `App` and `MainStudio`.

## 3. CURRENT MAIN-VIEW NAVIGATION

**Pure local component state + conditional rendering. No router, no store.**

- `type StudioTab = 'overview' | 'chat' | 'source' | 'knowledge' | 'git'` (`MainStudio.tsx:105`), held as `activeTab` (`:495`). Tab bar rendered at `:2046-2060`; each view is gated by `{activeTab === '…' && …}` (`:2063-2280`) — **inactive views are unmounted**, except inside `ChatWorkspace` where session panes stay mounted with `hidden`.
- A second dimension: `mainView: MainView` (`:108-112`, `:487`) selects project landing / worktree landing / hardware pages / device view when no device is selected; `activePage: 'studio' | 'tools' | 'settings'` (`:496`) swaps the whole shell body for `McpToolsHelper`/`SettingsPage` (`:1857-1863`).
- Cross-view jumps are imperative `setActiveTab(...)` calls scattered through handlers: `:1045` (hardware), `:1186/1197/1205` (chat session actions), `:1589` (git), `:1737`, `:2247` (PLC source → chat with context).
- Tab/page selection is **not persisted**; selection changes reset `activeTab` and wipe `chatTabs`.

## 4. EXISTING VIEW COMPONENTS

| View | Root component | Props | State deps | Mount side effects | Multi-instance safe? |
|---|---|---|---|---|---|
| Device Overview | **Inline JSX in MainStudio** (`:2063-2213`), not a component | n/a (closes over ~20 MainStudio states: `deviceView`, `deviceMeta`, `blocks`, `activeKnowledge`, `operation`, `rebuildArmed`, …) | Entirely MainStudio state | None of its own (buttons invoke MainStudio handlers) | **No** — must be extracted into a component first |
| AI Chat | `studio/src/studio/chat/ChatWorkspace.tsx` | all presentational: `tabs, busy, confirmation, sourceContext, onSend/onFocus/onDraftChange/onStop/onContinue/onConfirm…` | State owned by MainStudio (`chatTabs`); loads/saves global chat settings itself on mount (400 ms debounce, `ChatWorkspace.tsx:368-396`) | Settings fetch on mount; cleanup cancels it | Render-safe, but backed by **server singletons**: one active chat per device, one global device selection, one global confirmation queue (see §7) |
| PLC Source | `studio/src/studio/PlcSourcePanel.tsx` | `workbenchId, worktreeId, deviceId, deviceView, onChatWithAgent, onSnapshotReload` | Local: `typeFilter/query/expandedId/pendingAction/comparison` (all lost on unmount); `plcSourceState.ts` is pure | None (no effects) | **Yes** — no module state; only caveat is `BlockSourceView`'s un-aborted fetch (last-write-wins) |
| Knowledge | `studio/src/studio/NodeEdgesView.tsx` + `KnowledgePropertiesDock.tsx` | `context {wb,wt,dev}, projectName, onNodeSelect, onEdgeSelect` | Fetches node kinds/edges per `context` prop; internal split-drag with window listeners (`NodeEdgesView.tsx:178-182`) | Multiple fetch effects keyed on context; drag listeners | Yes (self-contained per context prop) |
| Version Control | `studio/src/studio/version-control/VersionControlPanel.tsx` (+ `VersionControlDetailsDock`) | `workbenchId, worktreeId, onSelectionChange` | Local `status/history/tab/loading`; `versionControlState.ts` pure helpers; selection lifted to MainStudio (`versionControlSelection`, `:549`) and reset on selection change (`:557-559`) | Refresh on mount | Yes |

Dead code note: `BlockSourceView.tsx` (287-line read-only source viewer) is **orphaned** — no imports outside itself; `PlcSourcePanel` is the live PLC source view.

## 5. ENGINEERING CONTEXT

Single source of truth = one triple in MainStudio:

```ts
// MainStudio.tsx:478
const [selection, setSelection] = useState<WorkbenchSelection>({ workbenchId, worktreeId, deviceId })
// type at studio/src/studio/workbench/WorkbenchNavigator.tsx:29
```

- **Set by** `selectWorkbench` / `selectWorktree` / `selectDevice` (`MainStudio.tsx:934/951/972`), each of which also POSTs to the backend (`api.selectWorkbench/Worktree/Device`) so the server mirrors focus in `WorkbenchApiState.Selection` (`src/ApiHost/WorkbenchApiModels.cs:124`) — **in-memory only, not persisted**.
- **Current project**: server-side `WorkbenchMetadata` persisted as `workbench.json` per workbench root (`src/Agent/Workbench/WorkbenchCatalog.cs:328`), mirrored into `workbenches` state.
- **Branch/worktree**: `worktree.json` per worktree; frontend `activeWorktree` derived via `useMemo` (`:617`).
- **Device**: `deviceSelection: DeviceSelectionState` (`:483`; pure helpers + requestId race guard in `deviceSnapshot.ts:62-140`; module-level in-memory metadata cache `deviceMetadataMemory` keyed `wb:wt:dev`).
- **Selected PLC block/source object**: transient — `chatSourceContext` (`:542`, type `SourceChatContext` in `plcSourceState.ts`) and local expansion state in `PlcSourcePanel`.
- **Active AI session**: `chatTabs.activeId` (`chatTabState.ts`); sessions persisted server-side per device (`SessionManager`); app-assistant session is a random id per panel mount.
- **Runtime focus pushed to assistants**: `AppAssistantRuntimeSnapshot` via SSE `/api/workbenches/{id}/runtime-events` (`client.ts:367-381`, consumed `MainStudio.tsx:561-575`).

## 6. PLC SOURCE / EDITOR

**There is no editor.** No Monaco, CodeMirror, or any highlighting/diff library in dependencies.

- Source display is a static `<pre><code>` (`BlockSourceView.tsx:264-269`); the compare dialog renders server-computed `diffLines` as colored `<div>`s (`PlcSourceCompareDialog.tsx:104-111`). Nothing is editable; mutations are whole-object server RPCs (`openSourceInTia`, `compareSourceWithTia`, `acceptTiaSourceObject`, `pushSourceObjectToTia` in `client.ts`).
- **Lifecycle**: `PlcSourcePanel` has zero effects. `BlockSourceView` has one un-aborted fetch effect (harmless but unguarded). No models to dispose.
- **Buffer ownership**: server owns all text; client holds only the `deviceView` snapshot (MainStudio) and ephemeral fetch results.
- **Dirty state / cursor / selection**: nonexistent. **Scroll position**: plain DOM scroll, lost on remount.
- **Resize**: no `ResizeObserver` anywhere; fixed CSS (`max-h-[520px]`, `max-w-6xl`, `max-w-4xl`) — the main CSS debt for docking.
- **Remount survival**: filter/query/expansion/open-dialog/scroll all lost; only MainStudio-held `deviceView` and `chatSourceContext` survive.
- **Multiple editors**: possible today (no singletons in the panel); one-compare-dialog-at-a-time and single-`expandedId` are local assumptions, easy to widen.

## 7. AI CHAT

- **Ownership**: server is source of truth (`ApiChatService`, `CompatibilityEndpoints.cs:591`: one `ActiveChat`/`AgentLoop` per device). Frontend mirror = `chatTabs` in MainStudio via pure reducers (`chatTabState.ts`). `ChatWorkspace` is fully presentational.
- **Identity/multiplicity**: server-persisted `sessionId` per device; multiple sessions per device as tabs (all panes stay mounted with `hidden` — already remount-friendly). Global `chatBusy` allows one active turn app-wide.
- **Streaming**: fetch + `ReadableStream` SSE parsing in `api.sendChatMessage` (`client.ts:1911-1954`), consumed **in MainStudio** (`:1264-1275`), abort via `chatAbortRef` (`:494`).
- **Unmount mid-stream**: safe by construction — no unmount cleanup aborts; drafts lifted into `chatTabs`; server persists partial turns even on client abort. Hazard: switching device mid-stream wipes `chatTabs` while the stream keeps writing (reducers no-op; completion path reloads).
- **MCP/tool execution**: entirely server-side; approvals are server-parked `TaskCompletionSource`s mirrored by **polling `/api/logs` every 1s while `chatBusy`** (`MainStudio.tsx:1306-1333`), decided via `POST /api/chat/confirm/{id}`.
- **Context dependency**: hard — `POST /api/chat` body is `{message}` only; device resolved from the **server-global selection**, and `ensureChatContext()` re-asserts `api.selectDevice` before every chat op. Two chat panes targeting different devices would race the global.
- **Second chat system**: `AppAssistantPanel` (LangGraph sidecar, workbench scope, buffered POST + `EventSource` push). Its `sessionId` is generated **per mount** and transcript is local `useState` — remounting loses visible history and starts a fresh sidecar session. This is the worst remount casualty.

## 8. STATE MANAGEMENT — CONCEPTUAL BUCKETS

| State | Location | Bucket |
|---|---|---|
| `selection`, `deviceSelection`, `workbenches`, `devicesByWorktree`, TIA `sessions`/`currentSession` | MainStudio useState + server mirror | **Engineering context** |
| `chatTabs`, `pendingConfirmation`, `chatBusy`, `chatSourceContext`, device sessions | MainStudio + server `AgentLoop` | **Application/domain** (per-device domain state) |
| `appAssistantRuntime`, `appAssistantOpen` | MainStudio + SSE | **Application/domain** |
| `activeTab`, `mainView`, `activePage`, `shellLayout`, `knowledgeSelection`, `versionControlSelection`, dialog open-state | MainStudio | **Workspace UI state** (this is what FlexLayout should absorb — `activeTab`/`mainView`) |
| `PlcSourcePanel` filters/expansion, `NodeEdgesView` selection, VC `tab/status/history`, hardware node selection | view-local | **Individual view state** |
| `theme.ts` module store | module + localStorage | **Application** (global preference) |

## 9. PERSISTENCE

- **Frontend localStorage — exactly two keys**: `plc-studio.theme.v1` (`theme.ts:7`) and `plc-studio.shell-layout.v1` (`shellLayout.ts:11`; versioned, validated, clamped 240–420 px; read at `MainStudio.tsx:498`, written at `:577-579`).
- **Not persisted**: selection, active tab/page, open chat tabs, editor/view state, recent projects, window geometry.
- **Backend-persisted**: `%APPDATA%/AutomationWorkbench/config.json` (API key + chat settings, atomic store, `CompatibilityEndpoints.cs:52-72`); `workbench.json`/`worktree.json`; chat sessions `{worktreeRoot}\.automation\sessions\*.json`; app-assistant data under `%LOCALAPPDATA%/AutomationWorkbench/AppAssistant`.
- **Recommendation**: workspace layout JSON has two natural homes — (a) **global default layout**: a sibling versioned localStorage key `plc-studio.workspace-layout.v1` following the `shellLayout.ts` read/write/validate pattern exactly; (b) **per-workbench layout**: an `AtomicJsonStore`-backed file under the workbench root with a small GET/PUT endpoint pair, mirroring `workbench.json`. Do (a) in Phase 1; (b) only if layouts should follow the project across machines.

## 10. DESKTOP WINDOWING

- **Framework**: WinForms + WebView2 (`AutomationWorkbench.Desktop.csproj`: `net8.0-windows`, `UseWindowsForms`). Single-instance mutex (`Program.cs:10-18`), one `Form` (`MainWindow.cs`, hardcoded 1440×900/maximized), one WebView2 docked fill; spawns `ApiHost.exe` (127.0.0.1:5239) and optionally the LangGraph sidecar (`BackendProcessHost.cs`).
- **IPC**: **no WebView2 bridge at all** — everything is same-origin HTTP/SSE (`fetch` in `client.ts`; `EventSource` for runtime events; fetch-stream for chat). Native file dialogs are exposed as HTTP endpoints. No SignalR.
- **Multi-window today**: impossible — mutex + `NewWindowRequested` explicitly handled and redirected to the system browser (`MainWindow.cs:152-156`).
- **Shared state between windows**: n/a today, but the architecture is close: shared truth is server-side, and the `AppAssistantRuntimeSnapshot` revision machinery exists for cross-context notification. Gaps: mutex, no window-state persistence, and the single server-global `WorkbenchApiState.Selection` (two windows would fight over focus).
- **Window persistence**: none; size/position hardcoded.

**Popout implication**: FlexLayout popouts (`window.open`) would open the system browser or be blocked by `NewWindowRequested` handling — real popout windows require desktop-host work (new `MainWindow` instances + removing mutex/redirect assumptions), a later phase.

## 11. TESTING

- **Unit/component**: Vitest 4 + happy-dom (per-file pragma), **no Testing Library** — raw `createRoot` + `act`, `querySelector` + `dispatchEvent`, API mocked via `vi.mock('@/api/client')`. 35 happy-dom test files.
- **Shell/navigation coverage**: `MainStudio.apiKey.test.tsx`, `MainStudio.deviceSelect.test.tsx`, `MainStudio.chatConfirm/chatFailure.test.tsx` drive the real shell via `data-*` hooks (`data-dock`, `data-dock-toggle`, `data-status-bar`, `data-api-status`, `data-chat-composer`, `data-confirmation`), aria-labels, and visible text. `shellLayout.test.ts` covers the dock-layout module directly.
- **Fragile couplings**: `MainStudio.contract.test.ts` and `WorkbenchNavigator.contract.test.ts` are **source-text contract tests** — they grep `MainStudio.tsx` for handler names, ordering, and literal JSX; they break on any reorganization regardless of runtime behavior. One `previousElementSibling` structural assertion in `deviceSelect.test.tsx`; one CSS-class assertion in `WorktreeVersionControlTimeline.test.tsx`.
- **E2E**: no browser E2E at all. `tests/E2E.Tests` is xunit backend lifecycle; `scripts/e2e-*.json` drive MCP tool calls over stdio (`scripts/mcp-e2e.mjs`). Manual UI smoke flow exists only as AGENTS.md instructions.
- **.NET**: xunit everywhere; `ApiHost.Tests` uses `Microsoft.AspNetCore.Mvc.Testing`; `AutomationWorkbench.Desktop.Tests` covers host/window.

## 12. IMPLEMENTATION RISKS (codebase-specific)

1. **Unresolved merge conflict in MainStudio.tsx** — hard blocker; also means two divergent state sets (`chatSourceContext` vs settings-page states) need reconciliation first.
2. **God component**: the future "views" are not components. Device Overview is ~150 lines of inline JSX closing over ~20 MainStudio states; you cannot dock it until it's extracted. Chat/source/knowledge/git get their context via props drilled from MainStudio — extraction is mechanical but touches everything.
3. **Unmount-on-tab-switch is load-bearing in reverse**: chat drafts and streams survive *because* state was lifted to MainStudio. FlexLayout unmounts tab content when tabs move between tabsets (unless `enableRenderOnDemand`/keep-alive is configured) — mostly safe for chat/source, **unsafe for `AppAssistantPanel`** (per-mount sessionId + local transcript; remount = new conversation). Its state must be lifted before it becomes a dockable view.
4. **Server-side singletons defeat multi-instance views**: one active chat per device, one server-global device selection (`POST /api/chat` carries no device/session id), one global confirmation queue, one `chatBusy`/`chatAbortRef`. Side-by-side PLC Source + AI Chat **for the same device** works; **two device contexts at once does not** — views would race `api.selectDevice`. This is the deepest architectural constraint; FlexLayout geometry won't fix it.
5. **One-view-at-a-time assumptions**: `activeTab` drives not just `<main>` but also the **right dock content** (`:2285-2345`) and right-dock visibility (`:2285`). With multiple visible views, "which dock content?" becomes ambiguous — docks must become per-view (inside the view) or workspace-aware.
6. **Imperative `setActiveTab` jumps** (`:1186, :1589, :2247`, …) — e.g. PLC source "Chat with Agent" sets context then switches tab. These must become `workspaceService.openView('chat', …)` / `focusView()` calls — which conveniently matches the planned semantic actions (`open_view`, `focus_view`, `show_source`, `show_diff`).
7. **CSS assumptions**: fixed `max-h-[520px]`/`max-w-6xl`/`min-h-[520px]` heights and `h-screen` shell; FlexLayout tab content must be `h-full w-full` container-relative. The scroll wrapper at `:2062` (`overflow-y-auto`) conflicts with FlexLayout's own sizing.
8. **No router assumptions** — good news: nothing to untangle; navigation state is one `useState`.
9. **ID collisions**: none found today (no DOM ids, keys derive from server data), but view instances will need stable ids (e.g. `viewKind + context key`) for the FlexLayout model and `ViewRegistry`.
10. **StrictMode double-mount**: `main.tsx` uses `StrictMode`; view components with fetch effects (`NodeEdgesView`, `BlockSourceView`) already tolerate it, but any new keep-alive logic must too.
11. **Test breakage**: the two source-text contract tests will fail the moment handlers move out of `MainStudio.tsx`; DOM tests survive only if `data-*` hooks, tab labels, `h1`, and `<footer>` structure are preserved.
12. **SSE connection count**: each mounted `AppAssistantPanel` opens its own `EventSource`; duplicated views scale connections linearly.

## 13. RECOMMENDED INSERTION POINT

The clean boundary is the `<main>` element at `MainStudio.tsx:1954-2284`, specifically the **device-selected branch** (`:2044-2283`: tab bar + `{activeTab === …}` blocks). Replace that fragment with `<WorkspaceHost>` while leaving untouched:

- header (`:1794-1855`), left dock + navigator (`:1865-1952`), right dock (initially — see Phase 2), `AppAssistantPanel` overlay, footer (`:2362-2421`), all dialogs, and the non-device branches (project/worktree/hardware landing pages stay conditional above WorkspaceHost in Phase 1).

Concretely: `WorkspaceHost` receives the same props the five tab blocks currently consume (`selection`, `deviceView`, `chatTabs`, handlers, …) and renders a FlexLayout `Model` whose tabs map 1:1 to today's `StudioTab` values. `WorkbenchViewHost` wraps each existing component, supplying context from `WorkspaceService` (which initially just reads MainStudio's lifted state). `activeTab` becomes derived from the FlexLayout model's active tabset selection rather than a `useState`.

## 14. PROPOSED MIGRATION (staged, no rewrite)

- **Phase 0 — unblock & extract (no FlexLayout yet).** Resolve the merge conflict. Extract Device Overview inline JSX (`:2063-2213`) into `studio/src/studio/DeviceOverviewView.tsx` with explicit props. Extract the tab-bar + tab-content switch into `WorkspaceHost.tsx` that still renders one tab at a time (behavior-identical). Update/rewrite `MainStudio.contract.test.ts`. Run `npm test -- --run`.
- **Phase 1 — FlexLayout behind WorkspaceHost.** Add flexlayout-react; `WorkspaceHost` builds a `Model` from a `WorkspaceService`-held layout descriptor (single tabset, five tabs = today's UX); `ViewRegistry` maps `viewKind → component`; `WorkbenchViewHost` adapts props per `WorkbenchViewInstance { instanceId, viewKind, context }`. Persist layout JSON to `plc-studio.workspace-layout.v1` (shellLayout pattern). Convert the six `setActiveTab` call sites to `workspaceService.focusView(...)`. Keep domain state in MainStudio. Fix view CSS to be container-relative. Tab labels/`data-*` hooks preserved for tests.
- **Phase 2 — docks & assistant.** Move right-dock content into per-view panels (each view owns its properties dock) or make dock visibility workspace-aware. Lift `AppAssistantPanel` session/transcript state into a MainStudio-level store so it survives docking; make it a workspace view if desired.
- **Phase 3 — multi-context & semantic API.** Address server singletons: pass device/session identity in `/api/chat*` requests instead of relying on global selection; per-device `chatBusy`/abort; then enable multiple device-scoped view instances. Expose `open_view()/focus_view()/show_source()/show_diff()` through WorkspaceService for agent/MCP workflows (the workbench-scoped AppAssistant is the natural first caller).
- **Phase 4 — popouts/multi-window.** Desktop host work: second `MainWindow`/WebView2 per popout, relax mutex + `NewWindowRequested` redirect, window-state persistence, server focus arbitration. Only after Phase 3.

## 15. RETURN FORMAT

### A. Current component hierarchy

```
main.tsx (StrictMode)
└─ App.tsx ─ ErrorBoundary ─ TooltipProvider
   └─ MainStudio.tsx  (all state lives here)
      ├─ <header> top nav (dock toggles, settings/tools, assistant button, ThemeToggle)
      ├─ [activePage==='tools']    McpToolsHelper
      ├─ [activePage==='settings'] SettingsPage
      └─ [studio] <div flex>
         ├─ left dock ─ WorkbenchNavigator      (fixed explorer)
         ├─ <main>                              (← migration boundary)
         │   ├─ fatalError / project / worktree / hardware landing pages
         │   └─ device branch: tab bar + ONE of:
         │       overview→inline JSX · chat→ChatWorkspace · source→PlcSourcePanel
         │       knowledge→NodeEdgesView · git→VersionControlPanel
         ├─ right dock ─ DevicePropertiesDock | KnowledgePropertiesDock |
         │               VersionControlDetailsDock | SessionDock | HardwarePropertiesDock
         ├─ AppAssistantPanel (overlay, key=workbenchId)
         └─ <footer data-status-bar> status bar (+ TiaSessionsPanel popover)
      └─ modal dialogs (CreateWorkbench, NewWorktree, Delete×2, Archive, Refresh, Compile, ProjectAccess)
```

### B. Relevant file map

- Shell/navigation: `studio/src/App.tsx`, `studio/src/main.tsx`, `studio/src/studio/MainStudio.tsx`, `studio/src/studio/shellLayout.ts`
- Explorer: `studio/src/studio/workbench/WorkbenchNavigator.tsx` (+ landing pages, `RuntimeStateStatusBar`, `TiaSessionsPanel`)
- Views: `studio/src/studio/chat/ChatWorkspace.tsx` (+ `SessionDock`, `chatTabState.ts`), `studio/src/studio/PlcSourcePanel.tsx` (+ `plcSourceState.ts`, `PlcSourceCompareDialog.tsx`, orphaned `BlockSourceView.tsx`), `studio/src/studio/NodeEdgesView.tsx` + `KnowledgePropertiesDock.tsx`, `studio/src/studio/version-control/VersionControlPanel.tsx` (+ `versionControlState.ts`, docks), `studio/src/studio/Hardware*View.tsx`, `studio/src/studio/appAssistant/AppAssistantPanel.tsx` (+ `appAssistantState.ts`)
- Context/state: `studio/src/studio/deviceSnapshot.ts`, `studio/src/studio/theme.ts`, `studio/src/studio/settings/settingsState.ts`, `studio/src/api/client.ts`
- Backend: `src/ApiHost/CompatibilityEndpoints.cs` (chat/device-selection globals), `src/ApiHost/WorkbenchApiModels.cs` (selection), `src/ApiHost/AppAssistant/*`, `src/Agent/Workbench/WorkbenchCatalog.cs`, `src/Agent/Chat/SessionManager.cs`
- Desktop: `src/AutomationWorkbench.Desktop/{Program,MainWindow,BackendProcessHost,RuntimePaths}.cs`
- Tests: `studio/src/studio/MainStudio.*.test.tsx`, `shellLayout.test.ts`, `*.contract.test.ts`; `tests/E2E.Tests`, `scripts/mcp-e2e.mjs`

### C. Current state-flow diagram

```
WorkbenchNavigator ──onSelect*──► MainStudio.selection (wb/wt/dev)
                                      │  POST select* ──► ApiHost WorkbenchApiState.Selection (GLOBAL, in-memory)
                                      ▼
                    derived: deviceSelection (snapshot fetch), knowledgeContext,
                             selectedChatContext, activeWorkbench/Worktree
                                      │ props drilling
        ┌───────────────┬────────────┼──────────────┬───────────────┐
        ▼               ▼            ▼              ▼               ▼
  Overview(inline)  ChatWorkspace  PlcSourcePanel NodeEdgesView  VersionControlPanel
        │               │            │              │               │
        │               └─ events ◄──┴── fetch/SSE ─┴── /api/* ◄────┘   (all keyed by wb/wt/dev props)
        │               ▲
        │   chatTabs / chatAbortRef / pendingConfirmation live in MainStudio
        │               │ polls /api/logs (1s, while chatBusy)
        ▼               ▼
   activeTab/mainView (local useState) ── decides which ONE view is mounted
   shellLayout ── localStorage plc-studio.shell-layout.v1
   AppAssistantRuntimeSnapshot ◄── EventSource /workbenches/{id}/runtime-events
```

### D. Recommended workspace architecture

```
MainStudio (engineering context + domain state, unchanged ownership)
 ├─ header / left dock(WorkbenchNavigator) / footer / dialogs   ← untouched
 └─ <WorkspaceHost>
     ├─ WorkspaceService  (view instances, semantic ops: openView/focusView/showSource/showDiff;
     │                     layout persistence plc-studio.workspace-layout.v1)
     ├─ ViewRegistry      (viewKind → component + title + context requirements)
     └─ FlexLayout Model  (owns ALL geometry: tabsets, splits, sizes)
         └─ WorkbenchViewHost[instanceId]  (context resolution, error boundary, keep-alive)
             ├─ DeviceOverviewView (extracted)
             ├─ ChatWorkspace      (state already lifted — OK)
             ├─ PlcSourcePanel     (multi-instance safe)
             ├─ NodeEdgesView
             └─ VersionControlPanel
   Domain state (chatTabs, deviceView, selection) stays OUTSIDE the FlexLayout model.
   AppAssistantPanel: lift session state, then register as a view (Phase 2).
```

### E. Blocking architectural issues

1. Unresolved merge conflict in `MainStudio.tsx` (compilation blocker, two divergent state sets).
2. Device Overview is inline JSX, not a component — cannot be docked until extracted.
3. Server-global device selection + device-less `POST /api/chat` + one-active-chat-per-device: prevents two device contexts side by side; needs API change before true multi-instance.
4. `AppAssistantPanel` per-mount session/transcript — must be lifted before any docking/remount.
5. Right-dock content is coupled to the single `activeTab` — needs a per-view or workspace-aware answer before multiple visible views.

### F. Decisions needing human confirmation

- Layout persistence scope: global localStorage default vs per-workbench JSON file (or both).
- Do hardware pages / project & worktree landing pages become workspace views too, or stay conditional above WorkspaceHost?
- Is two-different-devices-side-by-side an actual requirement (drives the Phase 3 server work), or is one device context with source+chat side by side sufficient for now?
- Should right-dock property panels become per-view panes inside the workspace, or remain a single shell-level dock?
- Fate of orphaned `BlockSourceView.tsx` (delete vs revive as the source viewer).
- Keep or drop the source-text contract tests (`MainStudio.contract.test.ts`) during extraction.
- Popout windows: in scope for the desktop host roadmap at all? (Requires WinForms/WebView2 work, not just FlexLayout.)

### G. Suggested Phase 1 file changes

1. `studio/src/studio/MainStudio.tsx` — resolve merge conflict; extract overview JSX and tab switch; mount `<WorkspaceHost>` in place of `MainStudio.tsx:2044-2283`; replace `setActiveTab` call sites with `workspaceService.focusView`; keep all domain state here.
2. New `studio/src/studio/DeviceOverviewView.tsx` — extracted overview, props-only.
3. New `studio/src/studio/workspace/WorkspaceHost.tsx` — FlexLayout host + model construction.
4. New `studio/src/studio/workspace/WorkspaceService.ts` — instances + semantic ops + persistence (mirroring `shellLayout.ts` read/write/validate with `plc-studio.workspace-layout.v1`).
5. New `studio/src/studio/workspace/ViewRegistry.tsx` and `WorkbenchViewHost.tsx` (with `WorkbenchViewInstance` type).
6. `studio/src/studio/shellLayout.ts` — unchanged (left/right docks stay shell-owned).
7. CSS: container-relative sizing in `PlcSourcePanel`, `ChatWorkspace`, `NodeEdgesView`, `VersionControlPanel`; revisit the `overflow-y-auto` wrapper at `:2062`.
8. Tests: rewrite `MainStudio.contract.test.ts` (or delete), adjust `MainStudio.deviceSelect.test.tsx` sibling assertion, add `WorkspaceService` persistence tests following `shellLayout.test.ts`.
9. `studio/package.json` — add `flexlayout-react` (Phase 1 only, not now).

---

*Generated from inspected source; no code was changed and nothing was installed during this review. Assumptions marked where noted.*
