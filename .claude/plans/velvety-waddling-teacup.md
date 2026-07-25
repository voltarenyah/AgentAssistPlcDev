# Plan: Multi-Project + PLC Attach + Right-Click MCP Tools

## Context

The React UI currently connects to one TIA session at a time and shows hardcoded left/right dock layouts. The user wants:
- **Multi-project opening**: manage multiple projects in the workspace tree, switch between them, open projects by path (headless)
- **PLC attach**: select a PLC device within a connected project; filter blocks and tool operations by PLC
- **Call MCPs with right-click**: context menus on tree items (sessions, PLCs, blocks) invoke MCP tools

## Architecture constraint

The TIA Engineering MCP server supports **one connection at a time** (single-process limitation). "Multi-project" means the UI tracks a workspace of known projects but only one is actively connected at any moment. Switching is disconnect-then-connect.

---

## Step 1 — API: generic MCP tool-call endpoint + connection list

### 1a. Add a generic tool-call endpoint (`POST /api/tools/call`)

Add to `src/ApiHost/Program.cs` a single endpoint that calls any MCP tool by name on either server:

```
POST /api/tools/call
Body: { server: "engineering" | "knowledge", tool: string, args: object }
Response: { result: any } or error
```

The endpoint classifies the tool via `SandboxPolicy.Defaults`:
- **Read**: auto-allow, call immediately
- **Write**: auto-allow, call + audit
- **Destructive**: push a confirmation event (same pattern as the chat SSE `confirm` endpoint) — caller provides a `confirmId` or it blocks until confirmed
- **Unknown/Denied**: 403

For destructive tools, the caller includes `{ confirmId?: string }` and the endpoint blocks on the same `ConfirmChannel.Pending` dictionary used by the chat SSE handler.

### 1b. Replace single `connectedProjectName` with a connection registry

```csharp
static ConcurrentDictionary<string, ConnectionEntry> _connections = new();

record ConnectionEntry(string SessionId, string ProjectName, string? ProjectPath, bool Attached, string? SelectedPlc);
```

Add endpoints:
- `GET /api/connections` — list all known/active connections
- `POST /api/connections/switch` — disconnect current, connect to a different session/project
- `POST /api/project/select-plc` — set `SelectedPlc` on the active connection (`body: { plcName: string }`)

### 1c. Add context status / compare endpoints (for right-click)

```
GET  /api/project/context-status      → calls get_context_status
POST /api/project/compare             → calls compare_context
POST /api/project/sync                → calls sync_export (read-only per TIA, then ingest_source)
```

---

## Step 2 — API client: new functions in client.ts

Add to `studio/src/api/client.ts`:

```typescript
// Connection management
export type ConnectionEntry = {
  sessionId: string
  projectName: string
  projectPath: string | null
  attached: boolean
  selectedPlc: string | null
}
export async function getConnections(): Promise<ConnectionEntry[]>
export async function switchConnection(sessionId?: number, projectPath?: string): Promise<ConnectionEntry>
export async function selectPlc(plcName: string): Promise<void>
export async function openProject(projectPath: string, withUI?: boolean): Promise<ConnectionEntry>

// Generic MCP tool call
export type ToolCallRequest = { server: 'engineering' | 'knowledge', tool: string, args: Record<string, unknown> }
export type ToolCallResult = { result: unknown }
export async function callTool(req: ToolCallRequest): Promise<ToolCallResult>

// Context operations
export async function getContextStatus(): Promise<...>
export async function compareContext(): Promise<...>
```

---

## Step 3 — UI: Left dock → Project Explorer with multi-project tree

Rewrite the left dock from "TIA Sessions" list to a **Project Explorer** with:

### Tree structure
```
📁 Workspace
  ├── 🔌 Connected: [Project Name]          ← active connection
  │   ├── 🔷 [PLC Device 1]                 ← selectable (PLC attach)
  │   │   └── 📦 [Block list…]              ← shown when PLC selected
  │   └── 🔷 [PLC Device 2]
  ├── 📁 TIA Sessions                       ← collapsible group
  │   ├── Session #1 (mode, path)           ← click to connect
  │   └── Session #2
  └── [➕ Open Project…]                     ← button to enter project path
```

### State changes
Replace `activeSessionId`/`connectedName` with:
```typescript
const [connections, setConnections] = useState<ConnectionEntry[]>([])
const [activeConnId, setActiveConnId] = useState<string | null>(null)
const [selectedPlc, setSelectedPlc] = useState<string | null>(null)
```

### Behavior
- On mount: `getConnections()` fetches existing connections
- Click session → `switchConnection(sessionId)` → UI updates all state
- Click PLC → `selectPlc(name)` → blocks filter to that PLC
- "Open Project" button → input field → `openProject(path)` → headless connect

---

## Step 4 — UI: Right-click context menus

Use existing `ContextMenu`/`ContextMenuTrigger`/`ContextMenuItem` from `context-menu.tsx`.

### Menu structure

**Session node (right-click):**
```
▶ Connect          ← calls switchConnection(sessionId)
  Show Details     ← shows session info
```

**Connected project (right-click):**
```
  Project Info     ← calls getProjectInfo() → shows in right dock
  Check Status     ← calls getContextStatus()
  Compare          ← calls compareContext()
  Sync             ← calls syncExport()
───
  Disconnect       ← calls disconnect()
```

**PLC device (right-click):**
```
▶ Select               ← calls selectPlc(name)
  List Blocks           ← calls getBlocks(plcName)
  Export Tags           ← calls callTool("export_tag_tables", {plcName, outputDir})
  Export UDTs           ← calls callTool("export_udts", {plcName, outputDir})
  Export All Blocks     ← calls callTool("export_all_blocks", {outputDir})
───
  Show Device Info      ← shows PLC details
```

**Block (in blocks tab list, right-click):**
```
  Export Block     ← calls callTool("export_block", {blockName, outputDir})
  Compile Block    ← calls callTool("compile_block", {blockName})
  Show Info        ← highlights block info in right dock
```

### Implementation approach

Wrap tree items in `ContextMenu` + `ContextMenuTrigger`:

```tsx
<ContextMenu>
  <ContextMenuTrigger asChild>
    <SessionNode session={...} />  ← existing node
  </ContextMenuTrigger>
  <ContextMenuContent>
    <ContextMenuItem onClick={...}>Connect</ContextMenuItem>
    <ContextMenuItem onClick={...}>Show Details</ContextMenuItem>
  </ContextMenuContent>
</ContextMenu>
```

Add a `useToolCall` helper that handles the destructive tool confirmation flow for write/destructive tools called from context menus. This reuses the same `ConfirmChannel` pattern — show the confirmation dialog, then call the tool.

---

## Step 5 — UI: PLC attach in right dock

When `selectedPlc` is set:
- Right dock **Blocks tab** shows blocks only for `selectedPlc`
- Status bar shows `PLC: {selectedPlc}`
- Document tabs at top filter to blocks of selected PLC
- Right dock **Project tab** highlights the selected PLC

---

## Files to modify

| File | Changes |
|------|---------|
| `src/ApiHost/Program.cs` | Add `/api/tools/call`, `/api/connections`, `/api/project/select-plc`, context endpoints; replace single `connectedProjectName` with registry |
| `studio/src/api/client.ts` | Add all new API functions and types |
| `studio/src/studio/MainStudio.tsx` | Rewrite left dock as Project Explorer, add context menus, add PLC selection, update state model |
| No new files needed | All changes fit in existing files |

## Order of implementation

1. API: connection registry + generic tool-call endpoint
2. API: context/compare/sync endpoints
3. API client: new functions
4. UI: left dock → Project Explorer with multi-project tree
5. UI: right-click context menus
6. UI: PLC attach / PLC filtering

## Verification

1. `dotnet build src/ApiHost/` — 0 errors
2. `cd studio && npx tsc --noEmit` — 0 errors
3. `cd studio && npm run build` — builds successfully
4. Open the app, verify tree shows sessions, connect to one, see PLCs, switch to another
5. Right-click each item type, verify menu items appear and invoke the right MCP tools
6. Select a PLC device, verify blocks filter to that PLC
