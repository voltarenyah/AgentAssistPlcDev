const BASE = '/api'

/* ── Types ──────────────────────────────────────────── */

export type SessionInfo = {
  id: number
  mode: string
  projectPath: string | null
}

export type ConnectionInfo = {
  projectName: string | null
  attached: boolean
}

export type ConnectionEntry = {
  id: string
  sessionId: string | null
  projectName: string
  projectPath: string | null
  attached: boolean
  selectedPlc: string | null
}

export type ToolInfo = {
  name: string
  description: string | null
  serverName: string
  schema: object
}

export type BlockInfo = {
  name: string
  blockType: string
  programmingLanguage: string
  number: number
  groupPath: string | null
}

export type ProjectInfo = {
  name: string
  path: string
  lastModified: string
  blockCount: number
  plcDevices: string[]
}

export type ServerStatus = {
  servers: 'running' | 'starting'
  connected: string | null
  selectedProject: string | null
  connections: number
  chatReady: boolean
  tools: number
}

export type ChatMessage = {
  role: string
  content: string | null
  toolCallId: string | null
  toolCalls?: { id: string; name: string; argumentsJson: string }[] | null
  reasoningContent?: string | null
  timestamp: string | null
}

export type ChatSessionInfo = {
  sessionId: string
  title: string
  projectName?: string | null
  workbenchId?: string | null
  worktreeId?: string | null
  deviceId?: string | null
  createdAt: string
  updatedAt: string
  messageCount: number
  turnCount: number
  firstUserMessage: string | null
}

export type ChatSessionHeader = {
  sessionId: string
  title?: string | null
  projectName?: string | null
  workbenchId?: string | null
  worktreeId?: string | null
  deviceId?: string | null
  createdAt: string
  updatedAt: string
}

export type ChatSessionData = {
  header: ChatSessionHeader
  messages: ChatMessage[]
  roundUsages?: unknown[]
}

export type ActiveSessionInfo = {
  active: boolean
  sessionId?: string
  projectName?: string
  createdAt?: string
  updatedAt?: string
  messageCount?: number
}

export type ConfirmationRequest = {
  id: string
  toolName: string
  arguments: string
  destructiveCallsSoFar: number
  budget: number
}

export type SSEEvent = {
  kind: 'reasoning' | 'content' | 'progress' | 'answer' | 'error' | 'confirmation'
  delta: string
} & Partial<ConfirmationRequest>

export type ToolCallResult = {
  result?: unknown
  _requiresConfirmation?: boolean
  _confirmationId?: string
  _tier?: string
  _toolName?: string
  _summary?: string
}

/* ── Workbench storage API ─────────────────────────────────────────────── */

export type WorkbenchRegistration = {
  worktreeId: string
  name: string
  branch: string
  relativePath: string
}

export type Workbench = {
  schemaVersion: string
  workbenchId: string
  name: string
  createdAt: string
  rootPath: string
  repositoryPath: string
  engineeringProjectId: string | null
  sourceProjectPath: string | null
  worktrees: WorkbenchRegistration[]
}

export type Worktree = {
  schemaVersion: string
  worktreeId: string
  workbenchId: string
  name: string
  branch: string
  createdAt: string
  baseCommit: string | null
  engineeringProjectId: string | null
  sourceProjectPath: string | null
  deviceIds: string[]
  lastReconciliationCommit: string | null
}

export type DeviceInfo = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  plcName: string
  engineeringIdentity: string
  exportedSourceRoot: string
  modifiedSourceRoot: string
  knowledgeDbPath: string
}

export type KnowledgeVisualState = 'current' | 'stale' | 'missing' | 'failed'

export type OfflineBlockInfo = {
  id: string
  name: string
  number: number | null
  blockType: string
  programmingLanguage: string | null
  groupPath: string | null
  relativePath: string
  modified: boolean
}

export type DeviceSnapshot = DeviceInfo & {
  knowledge: {
    state: KnowledgeVisualState
    updatedAt: string | null
  }
  blocks: OfflineBlockInfo[]
  overlayCount: number
  diagnostics: string[]
}

export type ReconciliationEntry = {
  relativePath: string
  kind: 'Added' | 'Changed' | 'Removed' | 'Unchanged' | 0 | 1 | 2 | 3
  baselineHash: string | null
  stagingHash: string | null
  componentIdentity: string | null
  storedFingerprints: string | null
  liveFingerprints: string | null
  fingerprintsMatch: boolean | null
}

export type ReconciliationPreview = {
  previewId: string
  worktreeId: string
  deviceId: string
  baselineTreeHash: string
  stagingTreeHash: string
  entries: ReconciliationEntry[]
}

export type RefreshApplyResult = {
  state: 'Rejected' | 'Committed' | 'FilesUpdatedCommitFailed' | number
  changedPaths: string[]
  commitSha: string | null
  error: string | null
}

export type KnowledgeUpdateResult = {
  dbPath: string
  updatedComponents: string[]
  appliedHashes: Record<string, string>
  warnings: string[]
}

export type ImportModifiedResult = {
  relativePath: string
  importSucceeded: boolean
  compileState: string
  warnings: string[]
  error: string | null
}

export type OperationState = 'running' | 'succeeded' | 'failed'

export type OperationStatus = {
  operationId: string
  operationType: string
  state: OperationState
  message: string
  updatedAt: string
  errorMessage: string | null
}

export class WorkbenchApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(
    status: number,
    code: string,
    message: string,
  ) {
    super(message)
    this.name = 'WorkbenchApiError'
    this.status = status
    this.code = code
  }
}

async function workbenchRequest<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, init)
  if (!response.ok) {
    let code = `HTTP_${response.status}`
    let message = `${response.status} ${response.statusText}`
    const rawBody = await response.text()
    if (rawBody) {
      try {
        const body = JSON.parse(rawBody) as { error?: string; message?: string }
        code = body.error || code
        message = body.message || body.error || message
      } catch {
        message = rawBody
      }
    }
    throw new WorkbenchApiError(response.status, code, message)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

const jsonRequest = (method: string, body?: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: body === undefined ? undefined : JSON.stringify(body),
})

const withOperation = (init: RequestInit, operationId?: string): RequestInit => {
  if (!operationId) return init
  return {
    ...init,
    headers: {
      ...(init.headers as Record<string, string> | undefined),
      'X-Operation-Id': operationId,
    },
  }
}

export const listWorkbenches = () => workbenchRequest<Workbench[]>('/workbenches')
export const openWorkbench = (rootPath: string) =>
  workbenchRequest<Workbench>('/workbenches/open', jsonRequest('POST', { rootPath }))
export const deleteWorkbench = (workbenchId: string, operationId?: string) =>
  workbenchRequest<{ deleted: boolean }>(`/workbenches/${encodeURIComponent(workbenchId)}`, withOperation({ method: 'DELETE' }, operationId))
export const createWorkbench = (
  name: string,
  engineeringSessionId: number,
  engineeringProjectPath: string,
  rootPath?: string,
  operationId?: string,
) =>
  workbenchRequest<Workbench>('/workbenches', withOperation(jsonRequest('POST', {
    name,
    engineeringSessionId,
    engineeringProjectPath,
    rootPath: rootPath?.trim() || null,
  }), operationId))
export const selectWorkbench = (workbenchId: string) =>
  workbenchRequest<void>(`/workbenches/${encodeURIComponent(workbenchId)}/select`, jsonRequest('POST'))
export const listWorktrees = (workbenchId: string) =>
  workbenchRequest<WorkbenchRegistration[]>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees`)
export const createWorktree = (workbenchId: string, name: string, branch: string, startPoint?: string, operationId?: string) =>
  workbenchRequest<Worktree>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees`, withOperation(jsonRequest('POST', {
    name,
    branch,
    startPoint: startPoint?.trim() || null,
  }), operationId))
export const selectWorktree = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<void>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/select`, jsonRequest('POST'))
export const listDevices = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<string[]>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices`)
export const selectDevice = (workbenchId: string, worktreeId: string, deviceId: string) =>
  workbenchRequest<void>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices/${encodeURIComponent(deviceId)}/select`, jsonRequest('POST'))
const devicePath = (workbenchId: string, worktreeId: string, deviceId: string) =>
  `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices/${encodeURIComponent(deviceId)}`
export const getDeviceInfo = (workbenchId: string, worktreeId: string, deviceId: string) =>
  workbenchRequest<DeviceSnapshot>(devicePath(workbenchId, worktreeId, deviceId))
export const openDeviceProject = (
  workbenchId: string,
  worktreeId: string,
  deviceId: string,
  operationId?: string,
) =>
  workbenchRequest<{ opened: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices/${encodeURIComponent(deviceId)}/tia/open`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const stageDeviceRefresh = (
  workbenchId: string,
  worktreeId: string,
  deviceId: string,
  operationId?: string,
  allowCompile = false,
) =>
  workbenchRequest<unknown>(
    `${devicePath(workbenchId, worktreeId, deviceId)}/refresh/stage${allowCompile ? '?allowCompile=true' : ''}`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const previewDeviceRefresh = (workbenchId: string, worktreeId: string, deviceId: string) =>
  workbenchRequest<ReconciliationPreview>(`${devicePath(workbenchId, worktreeId, deviceId)}/refresh/preview`)
export const applyDeviceRefresh = (workbenchId: string, worktreeId: string, deviceId: string, previewId: string, approvedPaths: string[], operationId?: string) =>
  workbenchRequest<RefreshApplyResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/refresh/apply`, withOperation(jsonRequest('POST', {
    previewId,
    approvedPaths,
  }), operationId))
export const updateDeviceKnowledge = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string) =>
  workbenchRequest<KnowledgeUpdateResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/knowledge/update`, withOperation(jsonRequest('POST'), operationId))
export const rebuildDeviceKnowledge = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string) =>
  workbenchRequest<KnowledgeUpdateResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/knowledge/rebuild`, withOperation(jsonRequest('POST'), operationId))
export type DeviceBootstrapResult = {
  baseline: RefreshApplyResult
  knowledge: KnowledgeUpdateResult
}
export const bootstrapDevice = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string) =>
  workbenchRequest<DeviceBootstrapResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/bootstrap`, withOperation(jsonRequest('POST'), operationId))
export const prepareDeviceEdit = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string) =>
  workbenchRequest<string>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/prepare-edit`, jsonRequest('POST', { relativePath }))
export const importDeviceSource = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string, operationId?: string) =>
  workbenchRequest<ImportModifiedResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/import`, withOperation(jsonRequest('POST', { relativePath }), operationId))
export const mergeWorktree = (workbenchId: string, sourceWorktreeId: string, targetWorktreeId: string, operationId?: string) =>
  workbenchRequest<unknown>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(sourceWorktreeId)}/merge`, withOperation(jsonRequest('POST', { targetWorktreeId }), operationId))
export const listDeviceSessions = (workbenchId: string, worktreeId: string, deviceId: string) =>
  workbenchRequest<ChatSessionInfo[]>(`${devicePath(workbenchId, worktreeId, deviceId)}/sessions`)
export const getOperationStatus = (operationId: string) =>
  workbenchRequest<OperationStatus>(`/operations/${encodeURIComponent(operationId)}`)
export const dismissOperationStatus = (operationId: string) =>
  workbenchRequest<void>(`/operations/${encodeURIComponent(operationId)}`, { method: 'DELETE' })

/* ── Version control types ──────────────────────────── */

export type VcStatusEntry = {
  filePath: string
  state: 'Untracked' | 'Modified' | 'Added' | 'Deleted' | 'Staged' | 'RenamedInWorkdir' | 'Conflicted'
  staged: boolean
}

export type VcStatusResult = {
  repoPath: string
  branch: string
  entries: VcStatusEntry[]
}

export type VcCommitEntry = {
  sha: string
  author: string
  message: string
  timestamp: string
  files: string[]
}

export type VcLogResult = {
  repoPath: string
  commits: VcCommitEntry[]
}

export type VcDiffLine = { type: string; content: string }
export type VcDiffHunk = { oldStart: number; newStart: number; lines: VcDiffLine[] }
export type VcDiffResult = {
  repoPath: string
  filePath: string
  oldSha: string | null
  newSha: string | null
  binary: boolean
  hunks: VcDiffHunk[]
}

/* ── Context compare / status types ──────────────────── */

export type ContextStatusResult = {
  plcName: string
  exportRoot: string
  manifestExists: boolean
  componentCount: number
  storedChecksum: string | null
  liveChecksum: string | null
  state: 'no-baseline' | 'in-sync' | 'changed' | 'unknown'
}

export type ContextCompareEntry = {
  name: string
  category: string
  sourcePath: string
  liveFingerprints: string | null
  storedFingerprints: string | null
  fingerprintsMatch: boolean | null
  liveModifiedDate: string | null
  storedModifiedDate: string | null
  state: 'same' | 'different' | 'new' | 'missing' | 'unverifiable' | 'unknown'
}

export type ContextCompareResult = {
  plcName: string
  exportRoot: string
  manifestExists: boolean
  storedChecksum: string | null
  liveChecksum: string | null
  components: ContextCompareEntry[]
}

/* ── Block source / knowledge types ──────────────────── */

export type NetworkInfo = {
  id: string
  index: number | null
  compileUnitId: string | null
  title: string | null
  language: string | null
  logicStatements: string | null
}

export type BlockSourceResult = {
  exists: boolean
  block?: { id: string; kind: string; name: string; sourceFile?: string; folderPath?: string }
  networks?: NetworkInfo[]
  message?: string
  dbPath?: string
}

export type VcBranchInfo = {
  name: string
  isHead: boolean
  sha: string
  upstream: string | null
}

/* ── Knowledge graph types ──────────────────────────── */

export type GraphNode = {
  id: string
  kind: string
  name: string
}

export type GraphEdge = {
  id: string
  from_node_id: string
  to_node_id: string
  type: string
}

export type GraphProperty = {
  name: string
  value: string
}

/* ── API calls ──────────────────────────────────────── */

export async function getStatus(): Promise<ServerStatus> {
  const res = await fetch(`${BASE}/status`)
  if (!res.ok) throw new Error(`Status failed: ${res.status}`)
  return res.json()
}

export async function getTools(): Promise<ToolInfo[]> {
  const res = await fetch(`${BASE}/tools`)
  if (!res.ok) throw new Error(`Tools failed: ${res.status}`)
  return res.json()
}

export async function getSessions(): Promise<SessionInfo[]> {
  const res = await fetch(`${BASE}/sessions`)
  if (!res.ok) throw new Error(`Sessions failed: ${res.status}`)
  return res.json()
}

export async function connect(opts: {
  sessionId?: number
  projectPath?: string
  withUI?: boolean
  timeoutSeconds?: number
}): Promise<ConnectionEntry> {
  const res = await fetch(`${BASE}/connect`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(opts),
  })
  if (!res.ok) throw new Error(`Connect failed: ${res.status}`)
  return res.json()
}

export async function disconnect(): Promise<void> {
  await fetch(`${BASE}/disconnect`, { method: 'POST' })
}

export async function getProjectInfo(): Promise<ProjectInfo> {
  const res = await fetch(`${BASE}/project/info`)
  if (!res.ok) throw new Error(`Project info failed: ${res.status}`)
  return res.json()
}

export async function getBlocks(plcName?: string): Promise<BlockInfo[]> {
  const params = plcName ? `?plcName=${encodeURIComponent(plcName)}` : ''
  const res = await fetch(`${BASE}/blocks${params}`)
  if (!res.ok) throw new Error(`Blocks failed: ${res.status}`)
  return res.json()
}

export async function getBlockSourceCode(blockName: string, plcName?: string): Promise<BlockSourceResult> {
  const params = plcName ? `?plcName=${encodeURIComponent(plcName)}` : ''
  const res = await fetch(`${BASE}/blocks/${encodeURIComponent(blockName)}/source-code${params}`)
  if (!res.ok) throw new Error(`Block source failed: ${res.status}`)
  return res.json()
}

/* ── Knowledge graph API ──────────────────────────────── */

export async function getKnowledgeNodeKinds(projectName: string): Promise<{ kinds: string[] }> {
  const res = await fetch(`${BASE}/knowledge/node-kinds?projectName=${encodeURIComponent(projectName)}`)
  if (!res.ok) throw new Error(`Node kinds failed: ${res.status}`)
  return res.json()
}

export async function getKnowledgeNodes(projectName: string, kind?: string): Promise<{ nodes: GraphNode[] }> {
  const params = new URLSearchParams({ projectName })
  if (kind) params.set('kind', kind)
  const res = await fetch(`${BASE}/knowledge/nodes?${params}`)
  if (!res.ok) throw new Error(`Nodes failed: ${res.status}`)
  return res.json()
}

export async function getKnowledgeEdgeTypes(projectName: string): Promise<{ types: string[] }> {
  const res = await fetch(`${BASE}/knowledge/edge-types?projectName=${encodeURIComponent(projectName)}`)
  if (!res.ok) throw new Error(`Edge types failed: ${res.status}`)
  return res.json()
}

export async function getKnowledgeEdges(projectName: string, fromNodeId: string, type?: string): Promise<{ edges: GraphEdge[] }> {
  const params = new URLSearchParams({ projectName, fromNodeId })
  if (type) params.set('type', type)
  const res = await fetch(`${BASE}/knowledge/edges?${params}`)
  if (!res.ok) throw new Error(`Edges failed: ${res.status}`)
  return res.json()
}

export async function getKnowledgeNodeProperties(projectName: string, nodeId: string): Promise<{ properties: GraphProperty[] }> {
  const res = await fetch(`${BASE}/knowledge/node-properties?projectName=${encodeURIComponent(projectName)}&nodeId=${encodeURIComponent(nodeId)}`)
  if (!res.ok) throw new Error(`Node properties failed: ${res.status}`)
  return res.json()
}

export async function getKnowledgeEdgeProperties(projectName: string, edgeId: string): Promise<{ properties: GraphProperty[] }> {
  const res = await fetch(`${BASE}/knowledge/edge-properties?projectName=${encodeURIComponent(projectName)}&edgeId=${encodeURIComponent(edgeId)}`)
  if (!res.ok) throw new Error(`Edge properties failed: ${res.status}`)
  return res.json()
}

export async function getKeyStatus(): Promise<{ configured: boolean }> {
  const res = await fetch(`${BASE}/config/key/status`)
  if (!res.ok) throw new Error(`Key status failed: ${res.status}`)
  return res.json()
}

export async function saveApiKey(key: string): Promise<{ status: string; chatReady: boolean }> {
  const res = await fetch(`${BASE}/config/key`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key }),
  })
  if (!res.ok) throw new Error(`Save key failed: ${res.status}`)
  return res.json()
}

export async function saveChatSettings(settings: {
  model: string
  thinkingEnabled: boolean
  reasoningEffort: string
  temperature: number
  topP: number
}): Promise<void> {
  await fetch(`${BASE}/config/settings`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings),
  })
}

export async function getChatHistory(): Promise<ChatMessage[]> {
  const res = await fetch(`${BASE}/chat/history`)
  if (!res.ok) throw new Error(`Chat history failed: ${res.status}`)
  return res.json()
}

export async function clearChatHistory(): Promise<void> {
  await fetch(`${BASE}/chat/clear`, { method: 'POST' })
}

export async function getChatSessions(_projectName?: string): Promise<ChatSessionInfo[]> {
  const res = await fetch(`${BASE}/chat/sessions`)
  if (!res.ok) throw new Error(`Chat sessions failed: ${res.status}`)
  return res.json()
}

export async function newChatSession(_projectName?: string): Promise<ChatSessionData> {
  const res = await fetch(`${BASE}/chat/session/new`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({}),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `New session failed: ${res.status}`)
  }
  return res.json()
}

export async function loadChatSession(sessionId: string, _projectName?: string): Promise<ChatSessionData> {
  const res = await fetch(`${BASE}/chat/session/load`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId }),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Load session failed: ${res.status}`)
  }
  return res.json()
}

export async function renameChatSession(sessionId: string, title: string): Promise<ChatSessionData> {
  const res = await fetch(`${BASE}/chat/session/rename`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, title }),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Rename session failed: ${res.status}`)
  }
  return res.json()
}

export async function deleteChatSession(sessionId: string, _projectName?: string): Promise<void> {
  const res = await fetch(`${BASE}/chat/session/delete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId }),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Delete session failed: ${res.status}`)
  }
}

export async function exportChatSession(sessionId: string): Promise<{ path: string }> {
  const res = await fetch(`${BASE}/chat/session/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId }),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Export session failed: ${res.status}`)
  }
  return res.json()
}

export async function getActiveSessionInfo(): Promise<ActiveSessionInfo> {
  const res = await fetch(`${BASE}/chat/session/info`)
  if (!res.ok) throw new Error(`Session info failed: ${res.status}`)
  return res.json()
}

export async function confirmTool(id: string, decision: 'allowOnce' | 'allowSession' | 'deny'): Promise<boolean> {
  const res = await fetch(`${BASE}/chat/confirm/${encodeURIComponent(id)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ decision }),
  })
  if (!res.ok && res.status !== 404) throw new Error(`Confirm failed: ${res.status}`)
  return res.ok
}

/* ── Multi-project connection API ──────────────────── */

export async function getConnections(): Promise<ConnectionEntry[]> {
  const res = await fetch(`${BASE}/connections`)
  if (!res.ok) throw new Error(`Connections failed: ${res.status}`)
  return res.json()
}

export async function switchConnection(opts: {
  sessionId?: number
  projectPath?: string
  withUI?: boolean
  timeoutSeconds?: number
}): Promise<ConnectionEntry> {
  const res = await fetch(`${BASE}/connections/switch`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(opts),
  })
  if (!res.ok) throw new Error(`Switch connection failed: ${res.status}`)
  return res.json()
}

export async function selectPlc(plcName: string): Promise<ConnectionEntry> {
  const res = await fetch(`${BASE}/project/select-plc`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ plcName }),
  })
  if (!res.ok) throw new Error(`Select PLC failed: ${res.status}`)
  return res.json()
}

export async function getContextStatus(outputDir?: string, plcName?: string): Promise<ContextStatusResult | null> {
  const searchParams = new URLSearchParams()
  if (outputDir) searchParams.set('outputDir', outputDir)
  if (plcName) searchParams.set('plcName', plcName)
  const qs = searchParams.toString()
  const res = await fetch(`${BASE}/project/context-status${qs ? '?' + qs : ''}`)
  if (!res.ok) throw new Error(`Context status failed: ${res.status}`)
  const arr: ContextStatusResult[] = await res.json()
  return arr.length > 0 ? arr[0] : null
}

export async function compareContext(outputDir?: string, plcName?: string): Promise<ContextCompareResult | null> {
  const searchParams = new URLSearchParams()
  if (outputDir) searchParams.set('outputDir', outputDir)
  if (plcName) searchParams.set('plcName', plcName)
  const qs = searchParams.toString()
  const res = await fetch(`${BASE}/project/compare${qs ? '?' + qs : ''}`)
  if (!res.ok) throw new Error(`Compare context failed: ${res.status}`)
  const arr: ContextCompareResult[] = await res.json()
  return arr.length > 0 ? arr[0] : null
}

/* ── Environment check ─────────────────────────────── */

export async function checkEnvironment(): Promise<unknown> {
  const res = await fetch(`${BASE}/check-environment`)
  if (!res.ok) throw new Error(`Check environment failed: ${res.status}`)
  return res.json()
}

/* ── File browser ──────────────────────────────────── */

export type BrowseEntry = {
  path: string
  parent: string | null
  directories: string[]
  files: string[]
}

export async function browsePath(path?: string): Promise<BrowseEntry> {
  const params = path ? `?path=${encodeURIComponent(path)}` : ''
  const res = await fetch(`${BASE}/browse${params}`)
  if (!res.ok) throw new Error(`Browse failed: ${res.status}`)
  return res.json()
}

/* ── Generic MCP tool call ─────────────────────────── */

export async function callTool(
  server: 'engineering' | 'knowledge',
  tool: string,
  args: Record<string, unknown> = {},
  confirmId?: string,
  decision?: string,
): Promise<ToolCallResult> {
  const res = await fetch(`${BASE}/tools/call`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ server, tool, args, confirmId, decision }),
  })
  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Tool call failed: ${res.status}`)
  }
  return res.json()
}

/* ── Version control API ────────────────────────────── */

export async function getVcStatus(workbenchId: string, worktreeId: string, deviceId: string): Promise<VcStatusResult> {
  return workbenchRequest<VcStatusResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/status`)
}

export async function getVcLog(workbenchId: string, worktreeId: string, deviceId: string, maxCount?: number, filePath?: string): Promise<VcLogResult> {
  const params = new URLSearchParams()
  if (maxCount) params.set('maxCount', String(maxCount))
  if (filePath) params.set('filePath', filePath)
  return workbenchRequest<VcLogResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/log?${params}`)
}

export async function getVcDiff(workbenchId: string, worktreeId: string, deviceId: string, filePath: string, oldSha?: string, newSha?: string): Promise<VcDiffResult> {
  const params = new URLSearchParams({ filePath })
  if (oldSha) params.set('oldSha', oldSha)
  if (newSha) params.set('newSha', newSha)
  return workbenchRequest<VcDiffResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/diff?${params}`)
}

export async function postVcAdd(workbenchId: string, worktreeId: string, deviceId: string, paths?: string[]): Promise<{ files: string[] }> {
  return workbenchRequest<{ files: string[] }>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/add`, jsonRequest('POST', { paths }))
}

export async function postVcCommit(workbenchId: string, worktreeId: string, deviceId: string, message: string): Promise<{ sha: string; message: string; files: string[] }> {
  return workbenchRequest<{ sha: string; message: string; files: string[] }>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/commit`, jsonRequest('POST', { message }))
}

export async function postVcRestore(workbenchId: string, worktreeId: string, deviceId: string, filePath?: string, sourceSha?: string): Promise<{ files?: string[] }> {
  return workbenchRequest<{ files?: string[] }>(`${devicePath(workbenchId, worktreeId, deviceId)}/vc/restore`, jsonRequest('POST', { filePath, sourceSha }))
}

export async function getVcBranches(): Promise<{ branches: VcBranchInfo[] }> {
  return workbenchRequest<{ branches: VcBranchInfo[] }>('/vc/branches')
}

export async function postVcCheckout(branchName: string): Promise<{ branch: string; sha: string }> {
  return workbenchRequest<{ branch: string; sha: string }>('/vc/checkout', jsonRequest('POST', { branch: branchName }))
}

/**
 * Send a chat message and receive streaming SSE events.
 * Returns when the stream completes.
 */
export async function sendChatMessage(
  message: string,
  onEvent: (event: SSEEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(`${BASE}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message }),
    signal,
  })

  if (!res.ok) {
    const body = await res.text()
    throw new Error(body || `Chat failed: ${res.status}`)
  }

  const reader = res.body?.getReader()
  if (!reader) throw new Error('No response body')

  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { done, value } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''

    for (const line of lines) {
      if (!line.startsWith('data: ')) continue
      const data = line.slice(6)
      if (data === '[DONE]') return
      try {
        const event = JSON.parse(data) as SSEEvent
        onEvent(event)
      } catch {
        // skip malformed lines
      }
    }
  }
}
