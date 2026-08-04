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
  schema: Record<string, unknown>
  tier: 'read' | 'write' | 'destructive' | 'denied' | 'unknown'
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

export type ChatUsage = {
  promptTokens: number
  completionTokens: number
  totalTokens: number
  reasoningTokens?: number
  promptCacheHitTokens?: number
  promptCacheMissTokens?: number
}

export type ChatToolStats = {
  succeeded: number
  failed: number
}

export type ChatSessionData = {
  header: ChatSessionHeader
  messages: ChatMessage[]
  roundUsages?: (ChatUsage | null)[]
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
  kind: 'reasoning' | 'content' | 'progress' | 'answer' | 'error' | 'confirmation' | 'meta'
  delta: string
  /** meta events: exact context usage of the last billed API round, plus the round-cap flag. */
  hitRoundCap?: boolean
  usage?: ChatUsage | null
  toolCalls?: ChatToolStats | null
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
  sourceProjectPath: string | null
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

export type DeviceExportMetadata = {
  plcName: string | null
  deviceName: string | null
  typeIdentifier: string | null
  projectName: string | null
  projectAuthor: string | null
  projectComment: string | null
  projectVersion: string | null
  projectCopyright: string | null
  projectCreationTime: string | null
  projectLastModified: string | null
  projectLastModifiedBy: string | null
}

export type DeviceSnapshot = DeviceInfo & {
  device: DeviceExportMetadata | null
  knowledge: {
    state: KnowledgeVisualState
    updatedAt: string | null
  }
  blocks: OfflineBlockInfo[]
  overlayCount: number
  diagnostics: string[]
}

export type HardwareConfigurationProperty = {
  name: string
  value: string
}

export type HardwareConfigurationIoRange = {
  ioType: string
  startAddress: number
  lengthBits: number
  endAddress: number
  addressRange: string
}

export type HardwareConfigurationTag = {
  id: string
  name: string
  dataType: string
  ioType: string
  logicalAddress: string
  ownerPath: string | null
}

export type HardwareConfigurationNode = {
  id: string
  name: string
  path: string
  kind: string
  typeIdentifier: string | null
  properties: HardwareConfigurationProperty[]
  ioRanges: HardwareConfigurationIoRange[]
  children: HardwareConfigurationNode[]
}

export type HardwareConfigurationView = {
  state: 'available' | 'missing' | 'invalid'
  projectAmlPath: string | null
  exportedAt: string | null
  devices: HardwareConfigurationNode[]
  tags: HardwareConfigurationTag[]
  message: string | null
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

export type HardwareConfigurationReloadResult = {
  rootPath: string
  artifactCount: number
  deviceCount: number
  commitSha: string
  warnings?: string[] | null
}

export type HardwareConfigurationCompareArtifact = {
  scope: 'project' | 'device'
  deviceName: string | null
  state: 'same' | 'changed' | 'missing' | 'new' | 'unknown'
}

export type HardwareConfigurationCompareResult = {
  state: 'in-sync' | 'changed' | 'missing'
  rootPath: string
  artifacts: HardwareConfigurationCompareArtifact[]
  message: string
  stagingPath?: string | null
}

export type HardwareConfigurationOverwriteResult = {
  rootPath: string
  artifactCount: number
  commitSha: string
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
  engineeringSessionId: number | null,
  engineeringProjectPath: string | null,
  rootPath?: string,
  operationId?: string,
) =>
  workbenchRequest<Workbench>('/workbenches', withOperation(jsonRequest('POST', {
    name,
    engineeringSessionId,
    engineeringProjectPath,
    rootPath: rootPath?.trim() || null,
  }), operationId))
export const getSandboxRoots = () =>
  workbenchRequest<{ roots: string[] }>('/sandbox/roots')
export const browseTiaProjectFile = async (): Promise<string | null> => {
  const res = await fetch(`${BASE}/dialogs/tia-project`)
  if (!res.ok) throw new Error(`TIA project picker failed: ${res.status}`)
  const body = await res.json() as { path?: string | null }
  return body.path ?? null
}
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
export const deleteWorktree = (workbenchId: string, worktreeId: string, operationId?: string) =>
  workbenchRequest<{ deleted: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}`,
    withOperation({ method: 'DELETE' }, operationId),
  )
export const selectWorktree = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<void>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/select`, jsonRequest('POST'))
export type DeviceSummary = {
  deviceId: string
  plcName: string
}
export const listDevices = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<DeviceSummary[]>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices`)
export const getHardwareConfiguration = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<HardwareConfigurationView>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware`)
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
  withUI = true,
) =>
  workbenchRequest<{ opened: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices/${encodeURIComponent(deviceId)}/tia/open`,
    withOperation(jsonRequest('POST', { withUI }), operationId),
  )
export const attachDeviceProject = (
  workbenchId: string,
  worktreeId: string,
  deviceId: string,
  sessionId: number,
  operationId?: string,
) =>
  workbenchRequest<{ attached: boolean }>(
    `${devicePath(workbenchId, worktreeId, deviceId)}/tia/attach`,
    withOperation(jsonRequest('POST', { sessionId }), operationId),
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
export const reloadHardwareConfiguration = (workbenchId: string, worktreeId: string, operationId?: string) =>
  workbenchRequest<HardwareConfigurationReloadResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware/reload`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const compareHardwareConfiguration = (workbenchId: string, worktreeId: string, operationId?: string) =>
  workbenchRequest<HardwareConfigurationCompareResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware/compare`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const overwriteHardwareConfiguration = (
  workbenchId: string,
  worktreeId: string,
  confirmOverwrite: boolean,
  operationId?: string,
) =>
  workbenchRequest<HardwareConfigurationOverwriteResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware/overwrite`,
    withOperation(jsonRequest('POST', { confirmOverwrite }), operationId),
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
export const bootstrapDevice = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string, commitMessage?: string, allowCompile?: boolean) =>
  workbenchRequest<DeviceBootstrapResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/bootstrap${allowCompile ? '?allowCompile=true' : ''}`, withOperation(jsonRequest('POST', commitMessage ? { commitMessage } : {}), operationId))
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

export type KnowledgeGraphContext = {
  workbenchId: string
  worktreeId: string
  deviceId: string
}

const knowledgePath = (ctx: KnowledgeGraphContext, suffix: string) =>
  `${devicePath(ctx.workbenchId, ctx.worktreeId, ctx.deviceId)}/knowledge/${suffix}`

async function knowledgeRequest<T>(url: string, label: string): Promise<T> {
  const res = await fetch(url)
  if (!res.ok) {
    const error = new Error(`${label} failed: ${res.status}`) as Error & { status: number }
    error.status = res.status
    throw error
  }
  return res.json()
}

export async function getKnowledgeNodeKinds(ctx: KnowledgeGraphContext): Promise<{ kinds: string[] }> {
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'node-kinds')}`, 'Node kinds')
}

export async function getKnowledgeNodes(ctx: KnowledgeGraphContext, kind?: string): Promise<{ nodes: GraphNode[] }> {
  const params = new URLSearchParams()
  if (kind) params.set('kind', kind)
  const query = params.size > 0 ? `?${params}` : ''
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'nodes')}${query}`, 'Nodes')
}

export async function getKnowledgeEdgeTypes(ctx: KnowledgeGraphContext): Promise<{ types: string[] }> {
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'edge-types')}`, 'Edge types')
}

export async function getKnowledgeEdges(ctx: KnowledgeGraphContext, nodeId?: string, type?: string): Promise<{ edges: GraphEdge[]; truncated?: boolean }> {
  const params = new URLSearchParams()
  if (nodeId) params.set('nodeId', nodeId)
  if (type) params.set('type', type)
  const query = params.size > 0 ? `?${params}` : ''
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'edges')}${query}`, 'Edges')
}

export async function getKnowledgeNodeProperties(ctx: KnowledgeGraphContext, nodeId: string): Promise<{ properties: GraphProperty[] }> {
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'node-properties')}?nodeId=${encodeURIComponent(nodeId)}`, 'Node properties')
}

export async function getKnowledgeEdgeProperties(ctx: KnowledgeGraphContext, edgeId: string): Promise<{ properties: GraphProperty[] }> {
  return knowledgeRequest(`${BASE}${knowledgePath(ctx, 'edge-properties')}?edgeId=${encodeURIComponent(edgeId)}`, 'Edge properties')
}

export async function getKeyStatus(): Promise<{ configured: boolean }> {
  const res = await fetch(`${BASE}/config/key/status`)
  if (!res.ok) throw new Error(`Key status failed: ${res.status}`)
  return res.json()
}

export type DeepSeekBalance = {
  isAvailable: boolean
  balances: {
    currency: string
    totalBalance: string
    grantedBalance: string
    toppedUpBalance: string
  }[]
  fetchedAt: string
}

export async function getDeepSeekBalance(): Promise<DeepSeekBalance> {
  const res = await fetch(`${BASE}/config/balance`)
  if (!res.ok) throw new Error(`DeepSeek balance failed: ${res.status}`)
  return res.json()
}

export async function saveApiKey(key: string): Promise<void> {
  const res = await fetch(`${BASE}/config/key`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apiKey: key }),
  })
  if (!res.ok) throw new Error(`Save key failed: ${res.status}`)
}

export type ChatSettings = {
  model: string
  thinkingEnabled: boolean
  reasoningEffort: string
  temperature: number
  topP: number
  contextWindow?: number
  // AgentLoop policy knobs (optional so they round-trip untouched when absent)
  roundLimit?: number
  promptTokenBudget?: number
  promptTokenWarningThreshold?: number
  toolResultMaxChars?: number
  toolResultCompactChars?: number
  historyTokenThreshold?: number
  recentTurnsToKeep?: number
  collapsedAnswerChars?: number
}

export async function getChatSettings(): Promise<ChatSettings> {
  const res = await fetch(`${BASE}/config/settings`)
  if (!res.ok) throw new Error(`Chat settings failed: ${res.status}`)
  return res.json()
}

export async function saveChatSettings(settings: ChatSettings): Promise<void> {
  const res = await fetch(`${BASE}/config/settings`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(settings),
  })
  if (!res.ok) throw new Error(`Save chat settings failed: ${res.status}`)
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

export type PendingConfirmation = {
  id: string
  toolName: string
  arguments: string
}

/** Raw server log lines; chat destructive-tool confirmations appear here as JSON entries. */
export async function getLogs(): Promise<string[]> {
  const res = await fetch(`${BASE}/logs`)
  if (!res.ok) throw new Error(`Logs failed: ${res.status}`)
  return res.json()
}

export async function grantChatRounds(additional = 6): Promise<void> {
  const res = await fetch(`${BASE}/chat/grant-rounds`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ additional }),
  })
  if (!res.ok) throw new Error(`Grant rounds failed: ${res.status}`)
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
