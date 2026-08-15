const BASE = '/api'

/* ── Types ──────────────────────────────────────────── */

export type SessionInfo = {
  id: number
  mode: string
  projectPath: string | null
  portalPath?: string | null
  acquisitionTime?: string
}

/** The TIA Portal session the engineering server is currently attached to. */
export type CurrentTiaSession = {
  attached: boolean
  sessionId: number | null
  projectName: string | null
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
  name: string | null
  path: string | null
  blockCount: number
  plcDevices: string[]
  author: string | null
  comment: string | null
  copyright: string | null
  family: string | null
  version: string | null
  lastModifiedBy: string | null
  creationTime: string | null
  size: number | null
  isModified: boolean
  isReadOnly: boolean
  isPrimary: boolean
  languages: string[]
  lastModified: string | null
}

export type ProjectCapabilities = {
  projectName: string | null
  projectPath: string | null
  isReadOnly: boolean
  isPrimary: boolean
  isModified: boolean
  canRead: boolean
  canAttemptWrite: boolean
  authenticationModes: string[]
  notes: string[]
}

export type ProjectCreateResult = {
  name: string | null
  path: string | null
  projectFilePath: string | null
}

export type ProjectArchiveResult = {
  projectName: string | null
  archivePath: string | null
  archivationMode: string
}

export type ProjectRetrieveResult = {
  name: string | null
  path: string | null
  projectFilePath: string | null
  isPrimary: boolean
  isReadOnly: boolean
  upgraded: boolean
}

export type ServerStatus = {
  storage: string
  legacyProjects: boolean
  version: string
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

export type WorktreeStatus = 'ongoing' | 'finished'

export type WorktreeTaskStatus = 'todo' | 'inProgress' | 'done'

export type WorktreeOverview = {
  worktreeId: string
  name: string
  branch: string
  relativePath: string
  createdAt: string | null
  purpose: string | null
  owner: string | null
  status: WorktreeStatus
  finishedUtc: string | null
  openTasks: number
  totalTasks: number
}

export type WorkbenchOverview = {
  workbenchId: string
  name: string
  createdAt: string
  rootPath: string
  repositoryPath: string
  engineeringProjectId: string | null
  sourceProjectPath: string | null
  purpose: string | null
  owner: string | null
  worktrees: WorktreeOverview[]
}

export type WorktreeDetail = {
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
  purpose: string | null
  owner: string | null
  status: WorktreeStatus
  finishedUtc: string | null
}

export type WorktreeTask = {
  taskId: string
  title: string
  details: string | null
  status: WorktreeTaskStatus
  elementRefs: string[]
  createdUtc: string
  doneUtc: string | null
}

export type WorktreeTaskList = {
  version: number
  tasks: WorktreeTask[]
}

export type AppAssistantRuntimeSnapshot = {
  schemaVersion: number
  workbenchId: string
  workbenchRevision: number
  focus: { worktreeId: string | null; deviceId: string | null }
  worktrees: Array<{
    worktreeId: string
    name: string
    branch: string
    gitStatus: string
    head: string | null
    todoCount: number
    svnBaseRevision: number | null
    svnCurrentRevision: number | null
    validationState: string
    devices: Array<{
      deviceId: string
      plcName: string | null
      tiaState: string
      knowledgeFreshness: string
    }>
  }>
  availableActions: Array<{ id: string; label: string; enabled: boolean; requiresApproval: boolean; blockedBy: string[] }>
  operation: { operationId: string | null; kind: string | null; status: string | number; message: string | null }
  observedAt: string
}

export type AppAssistantEvent = {
  kind: string
  data: Record<string, unknown>
}

const parseAssistantEvents = (body: string): AppAssistantEvent[] => body
  .split('\n\n')
  .map(block => {
    const event = block.match(/^event: ([^\n]+)$/m)?.[1]
    const data = block.match(/^data: ([^\n]+)$/m)?.[1]
    if (!event || !data) return null
    try { return { kind: event, data: JSON.parse(data) as Record<string, unknown> } } catch { return null }
  })
  .filter((value): value is AppAssistantEvent => value !== null)

const postAppAssistant = async (path: string, message: string, approval?: Record<string, unknown>, sessionId?: string) => {
  const response = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message, ...(approval ? { approval } : {}), ...(sessionId ? { sessionId } : {}) }),
  })
  if (!response.ok) {
    let body: { error?: string; message?: string } = {}
    try { body = await response.json() as typeof body } catch { /* plain error */ }
    throw new WorkbenchApiError(response.status, body.error ?? 'APP_ASSISTANT_ERROR', body.message ?? response.statusText)
  }
  return parseAssistantEvents(await response.text())
}

export const bootstrapAppAssistant = (sessionId?: string) =>
  postAppAssistant('/app-assistant/bootstrap', '', undefined, sessionId)
export const chatAppAssistant = (message: string, approval?: Record<string, unknown>, sessionId?: string) =>
  postAppAssistant('/app-assistant/chat', message, approval, sessionId)
export type AppAssistantFeedbackCategory =
  | 'wrong_worktree'
  | 'stale_status'
  | 'wrong_recommendation'
  | 'unavailable_action'
  | 'successful_completion'
export const submitAppAssistantFeedback = (category: AppAssistantFeedbackCategory, runId?: string) =>
  workbenchRequest<void>('/app-assistant/feedback', jsonRequest('POST', { category, runId }))
export const getAppAssistantRuntimeState = (workbenchId: string) =>
  workbenchRequest<AppAssistantRuntimeSnapshot>(`/workbenches/${encodeURIComponent(workbenchId)}/runtime-state`)
export const subscribeAppAssistantRuntime = (workbenchId: string, onSnapshot: (snapshot: AppAssistantRuntimeSnapshot) => void) => {
  if (typeof EventSource === 'undefined') return () => {}
  const source = new EventSource(`${BASE}/workbenches/${encodeURIComponent(workbenchId)}/runtime-events`)
  const handler = (event: Event) => {
    try {
      const data = JSON.parse((event as MessageEvent).data) as { snapshot?: AppAssistantRuntimeSnapshot }
      if (data.snapshot) onSnapshot(data.snapshot)
    } catch { /* reconnecting stream can contain partial data */ }
  }
  source.addEventListener('runtime-state', handler)
  return () => {
    source.removeEventListener('runtime-state', handler)
    source.close()
  }
}

export type FeatureImportObject = {
  deviceId: string
  plcName: string
  relativePath: string
  featureFingerprint: string
  importable: boolean
  reason: string | null
}

export type FeatureImportPlan = {
  planId: string
  workbenchId: string
  featureWorktreeId: string
  featureSha: string
  masterSha: string
  comparisonId: string
  objects: FeatureImportObject[]
}

export type FeatureImportOutcome = {
  deviceId: string
  relativePath: string
  state: FeatureImportState | number
  error: string | null
  warnings: string[]
}

export type FeatureImportState =
  | 'Pending'
  | 'Imported'
  | 'Failed'
  | 'KeptAfterCompileFailure'
  | 'RolledBack'

export type FeatureImportSession = {
  sessionId: string
  planId: string
  featureSha: string
  masterSha: string
  startedAt: string
  objects: FeatureImportOutcome[]
}

export type ValidatedMergeResult = {
  validationId: string
  state: ValidatedMergeState | number
  error: string | null
  devices: ValidatedMergeDevice[]
}

export type ValidatedMergeState = 'Ready' | 'CompileFailed' | 'SourceDifferent' | 'BranchMoved'

export type ValidatedMergeObject = {
  identity: string
  relativePath: string
  sha256: string
}

export type ValidatedMergeDevice = {
  deviceId: string
  plcName: string
  projectIdentity: string
  projectChecksum: string
  objects: ValidatedMergeObject[]
}

export type SourceDifference = {
  deviceId: string
  plcName: string
  relativePath: string
  identity: string
  kind: SourceDifferenceKind | number
  masterFingerprint: string | null
  tiaFingerprint: string | null
  supported: boolean
}

export type SourceDifferenceKind = 'Unchanged' | 'Changed' | 'Added' | 'Deleted'

export type ConsistencyState = 'Consistent' | 'Different' | 'ScanRequired' | 'Unavailable'

export type WorkbenchConsistencyResult = {
  comparisonId: string
  masterSha: string
  fastGatePassed: boolean
  state: ConsistencyState | number
  liveChecksums: Record<string, string | null>
  differences: SourceDifference[]
}

export type PendingSynchronizationResult = {
  comparisonId: string
  pendingPaths: string[]
  commitSha?: string | null
}

export type RollbackFeatureResult = {
  worktreeId: string
  branch: string
  historicalSha: string
  paths: string[]
}

export type TiaSyncEvidence = {
  schemaVersion: string
  evidenceKind: string
  commitSha: string
  workbenchId: string
  sourceWorktreeId: string | null
  confirmedAt: string
  confirmedBy: string
  machineValidated: boolean
  devices: {
    deviceId: string
    plcName: string
    projectIdentity: string
    projectChecksum: string
    objects: { identity: string; relativePath: string; sha256: string }[]
  }[]
}

export type DeviceInfo = {
  workbenchId: string
  worktreeId: string
  deviceId: string
  plcName: string
  engineeringIdentity: string
  sourceRoot: string
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

export type SourceObjectInfo = {
  id: string
  name: string
  number: number | null
  category: string
  programmingLanguage: string | null
  groupPath: string | null
  relativePath: string
  contentHash: string | null
  isKnowHowProtected: boolean | null
  modifiedDate: string | null
  status: string | null
}

export type DeviceSnapshot = DeviceInfo & {
  device: DeviceExportMetadata | null
  knowledge: {
    state: KnowledgeVisualState
    updatedAt: string | null
  }
  blocks: OfflineBlockInfo[]
  sourceObjects: SourceObjectInfo[]
  sourceObjectCount: number
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

export type HardwareBomItem = {
  id: string
  name: string
  path: string
  position: string
  positionNumber: number | null
  typeName: string | null
  typeIdentifier: string
  orderNumber: string | null
  firmwareVersion: string | null
}

export type HardwareBomView = {
  state: 'available' | 'missing' | 'invalid'
  exportedAt: string | null
  items: HardwareBomItem[]
  message: string | null
}

export type HardwareNetworkNode = {
  id: string
  address: string
  subnetMask: string | null
  profinetDeviceName: string | null
  deviceName: string
  devicePath: string
  interfaceLabel: string | null
  subnetName: string | null
}

export type HardwareNetworkView = {
  state: 'available' | 'missing' | 'invalid'
  exportedAt: string | null
  nodes: HardwareNetworkNode[]
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
export const browseExportDirectory = async (initialDirectory?: string): Promise<string | null> => {
  const query = initialDirectory?.trim() ? `?initialDirectory=${encodeURIComponent(initialDirectory.trim())}` : ''
  const res = await fetch(`${BASE}/dialogs/folder${query}`)
  if (!res.ok) throw new Error(`Export directory picker failed: ${res.status}`)
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
export const planFeatureImport = (workbenchId: string, featureWorktreeId: string, operationId?: string) =>
  workbenchRequest<FeatureImportPlan>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(featureWorktreeId)}/vc/import-plan`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const importFeaturePaths = (workbenchId: string, planId: string, paths: string[], operationId?: string) =>
  workbenchRequest<FeatureImportSession>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/import-plans/${encodeURIComponent(planId)}/import`,
    withOperation(jsonRequest('POST', { paths }), operationId),
  )
export const rollbackFeaturePaths = (workbenchId: string, sessionId: string, paths: string[], operationId?: string) =>
  workbenchRequest<FeatureImportSession>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/import-sessions/${encodeURIComponent(sessionId)}/rollback`,
    withOperation(jsonRequest('POST', { paths }), operationId),
  )
export const keepFeaturePathsAfterCompileFailure = (workbenchId: string, sessionId: string, paths: string[]) =>
  workbenchRequest<FeatureImportSession>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/import-sessions/${encodeURIComponent(sessionId)}/keep`,
    jsonRequest('POST', { paths }),
  )
export const validateFeatureMerge = (
  workbenchId: string,
  featureWorktreeId: string,
  importSessionId: string,
  machineValidated: boolean,
  confirmedBy: string,
  operationId?: string,
) =>
  workbenchRequest<ValidatedMergeResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(featureWorktreeId)}/vc/validate-merge`,
    withOperation(jsonRequest('POST', { importSessionId, machineValidated, confirmedBy }), operationId),
  )
export const mergeValidatedFeature = (workbenchId: string, validationId: string, operationId?: string) =>
  workbenchRequest<FeatureMergePublicationResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/validated-merges/${encodeURIComponent(validationId)}/merge`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const createRollbackFeature = (workbenchId: string, historicalSha: string, paths: string[], featureName: string, operationId?: string) =>
  workbenchRequest<RollbackFeatureResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/rollback-features`,
    withOperation(jsonRequest('POST', { historicalSha, paths, featureName }), operationId),
  )
export const compareMasterWithTia = (workbenchId: string, operationId?: string) =>
  workbenchRequest<WorkbenchConsistencyResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/compare-tia`,
    withOperation(jsonRequest('POST'), operationId),
  )
export const getWorkbenchComparison = (workbenchId: string, comparisonId: string) =>
  workbenchRequest<WorkbenchConsistencyResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/comparisons/${encodeURIComponent(comparisonId)}`,
  )
export const acceptTiaSynchronization = (workbenchId: string, comparisonId: string, paths: string[], message: string, operationId?: string) =>
  workbenchRequest<PendingSynchronizationResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/comparisons/${encodeURIComponent(comparisonId)}/accept`,
    withOperation(jsonRequest('POST', { paths, message }), operationId),
  )
export type PushToTiaOutcome = { path: string; success: boolean; message: string | null }
export type PushToTiaResult = { comparisonId: string; outcomes: PushToTiaOutcome[] }
export const pushSourcesToTia = (workbenchId: string, comparisonId: string, paths: string[], operationId?: string) =>
  workbenchRequest<PushToTiaResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/comparisons/${encodeURIComponent(comparisonId)}/push-to-tia`,
    withOperation(jsonRequest('POST', { paths }), operationId),
  )
export const validateTiaSynchronization = (workbenchId: string, confirmedBy: string, operationId?: string) =>
  workbenchRequest<VcValidationEvidence>(
    `/workbenches/${encodeURIComponent(workbenchId)}/vc/validate-sync`,
    withOperation(jsonRequest('POST', { confirmedBy }), operationId),
  )
export const getVersionControlWorktreeStatus = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<VcStatusResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/status`)
export const getVersionControlWorktreeLog = (workbenchId: string, worktreeId: string, maxCount = 30) =>
  workbenchRequest<VcLogResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/log?maxCount=${maxCount}`)
export const getWorktreeVersionControlTimeline = (
  workbenchId: string,
  worktreeId: string,
  offset = 0,
  limit = 10,
) => workbenchRequest<VersionControlTimelineResult>(
  `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/timeline?offset=${offset}&limit=${limit}`,
)
export const getVersionControlWorktreeDiff = (workbenchId: string, worktreeId: string, filePath: string, oldSha?: string, newSha?: string) => {
  const params = new URLSearchParams({ filePath })
  if (oldSha) params.set('oldSha', oldSha)
  if (newSha) params.set('newSha', newSha)
  return workbenchRequest<VcDiffResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/diff?${params}`)
}
export const commitVersionControlPaths = (workbenchId: string, worktreeId: string, paths: string[], message: string) =>
  workbenchRequest<{ sha: string; message: string; files: string[] }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/commit`,
    jsonRequest('POST', { paths, message }),
  )
export const validateFeatureVersionControl = (
  workbenchId: string,
  featureWorktreeId: string,
  importSessionId: string,
  machineValidated: boolean,
  confirmedBy: string,
  operationId?: string,
) => validateFeatureMerge(workbenchId, featureWorktreeId, importSessionId, machineValidated, confirmedBy, operationId)
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
export const getHardwareBom = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<HardwareBomView>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware/bom`)
export const getHardwareNetwork = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<HardwareNetworkView>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/hardware/network`)
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
  upgrade = false,
  authenticationMode?: string,
) =>
  workbenchRequest<{ opened: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/devices/${encodeURIComponent(deviceId)}/tia/open`,
    withOperation(jsonRequest('POST', { withUI, upgrade, authenticationMode: authenticationMode ?? null }), operationId),
  )
export const openWorkbenchProject = (
  workbenchId: string,
  operationId?: string,
  withUI = true,
  upgrade = false,
  authenticationMode?: string,
) =>
  workbenchRequest<{ opened: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/tia/open`,
    withOperation(jsonRequest('POST', { withUI, upgrade, authenticationMode: authenticationMode ?? null }), operationId),
  )
export const openWorktreeProject = (
  workbenchId: string,
  worktreeId: string,
  operationId?: string,
  withUI = true,
  upgrade = false,
  authenticationMode?: string,
) =>
  workbenchRequest<{ opened: boolean }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/tia/open`,
    withOperation(jsonRequest('POST', { withUI, upgrade, authenticationMode: authenticationMode ?? null }), operationId),
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
export const applyDeviceRefresh = (workbenchId: string, worktreeId: string, deviceId: string, previewId: string, approvedPaths: string[], operationId?: string, commitMessage?: string) =>
  workbenchRequest<RefreshApplyResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/refresh/apply`, withOperation(jsonRequest('POST', {
    previewId,
    approvedPaths,
    commitMessage: commitMessage?.trim() || null,
  }), operationId))
export const updateDeviceKnowledge = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string) =>
  workbenchRequest<KnowledgeUpdateResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/knowledge/update`, withOperation(jsonRequest('POST'), operationId))
export const rebuildDeviceKnowledge = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string) =>
  workbenchRequest<KnowledgeUpdateResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/knowledge/rebuild`, withOperation(jsonRequest('POST'), operationId))
export type DeviceBootstrapResult = {
  baseline: RefreshApplyResult
  knowledge: KnowledgeUpdateResult
}
export type WorktreeBootstrapResult = {
  devices: DeviceBootstrapResult[]
}
export const bootstrapDevice = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string, commitMessage?: string, allowCompile?: boolean) =>
  workbenchRequest<DeviceBootstrapResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/bootstrap${allowCompile ? '?allowCompile=true' : ''}`, withOperation(jsonRequest('POST', commitMessage ? { commitMessage } : {}), operationId))
export const bootstrapWorktree = (workbenchId: string, worktreeId: string, deviceId: string, operationId?: string, commitMessage?: string, allowCompile?: boolean) =>
  workbenchRequest<WorktreeBootstrapResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/bootstrap-worktree${allowCompile ? '?allowCompile=true' : ''}`, withOperation(jsonRequest('POST', commitMessage ? { commitMessage } : {}), operationId))
export const prepareDeviceEdit = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string) =>
  workbenchRequest<string>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/prepare-edit`, jsonRequest('POST', { relativePath }))
export const importDeviceSource = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string, operationId?: string) =>
  workbenchRequest<ImportModifiedResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/import`, withOperation(jsonRequest('POST', { relativePath }), operationId))
export type DiffLine = {
  kind: 'same' | 'added' | 'removed'
  text: string
}
export type SourceObjectComparison = {
  comparisonId: string
  relativePath: string
  name: string
  category: string
  same: boolean
  diffLines: DiffLine[]
  localHash: string | null
  tiaHash: string | null
  diagnostics: string[]
}
export type SourceObjectSyncResult = {
  comparisonId: string
  relativePath: string
  success: boolean
  message: string | null
}
export const openSourceInTia = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string, operationId?: string) =>
  workbenchRequest<unknown>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/open-in-tia`, withOperation(jsonRequest('POST', { relativePath }), operationId))
export const compareSourceWithTia = (workbenchId: string, worktreeId: string, deviceId: string, relativePath: string, operationId?: string) =>
  workbenchRequest<SourceObjectComparison>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/compare-tia`, withOperation(jsonRequest('POST', { relativePath }), operationId))
export const acceptTiaSourceObject = (workbenchId: string, worktreeId: string, deviceId: string, comparisonId: string, operationId?: string) =>
  workbenchRequest<SourceObjectSyncResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/comparisons/${encodeURIComponent(comparisonId)}/accept`, withOperation(jsonRequest('POST'), operationId))
export const pushSourceObjectToTia = (workbenchId: string, worktreeId: string, deviceId: string, comparisonId: string, operationId?: string) =>
  workbenchRequest<SourceObjectSyncResult>(`${devicePath(workbenchId, worktreeId, deviceId)}/source/comparisons/${encodeURIComponent(comparisonId)}/push-to-tia`, withOperation(jsonRequest('POST'), operationId))
export const mergeWorktree = (workbenchId: string, sourceWorktreeId: string, targetWorktreeId: string, operationId?: string) =>
  workbenchRequest<unknown>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(sourceWorktreeId)}/merge`, withOperation(jsonRequest('POST', { targetWorktreeId }), operationId))
export const listDeviceSessions = (workbenchId: string, worktreeId: string, deviceId: string) =>
  workbenchRequest<ChatSessionInfo[]>(`${devicePath(workbenchId, worktreeId, deviceId)}/sessions`)
export const getOperationStatus = (operationId: string) =>
  workbenchRequest<OperationStatus>(`/operations/${encodeURIComponent(operationId)}`)
export const dismissOperationStatus = (operationId: string) =>
  workbenchRequest<void>(`/operations/${encodeURIComponent(operationId)}`, { method: 'DELETE' })

/* ── Project / worktree landing page API ─────────────── */

const worktreePath = (workbenchId: string, worktreeId: string) =>
  `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}`

export const getWorkbenchOverview = (workbenchId: string) =>
  workbenchRequest<WorkbenchOverview>(`/workbenches/${encodeURIComponent(workbenchId)}/overview`)
export const updateWorkbench = (workbenchId: string, patch: { purpose?: string | null; owner?: string | null }) =>
  // Returns the reloaded workbench metadata (not the overview aggregate); callers
  // that need the overview re-fetch it via getWorkbenchOverview.
  workbenchRequest<Workbench>(`/workbenches/${encodeURIComponent(workbenchId)}`, jsonRequest('PATCH', patch))
export const getWorktreeDetail = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<WorktreeDetail>(worktreePath(workbenchId, worktreeId))
export const updateWorktree = (
  workbenchId: string,
  worktreeId: string,
  patch: { purpose?: string | null; owner?: string | null; status?: WorktreeStatus },
) =>
  workbenchRequest<WorktreeDetail>(worktreePath(workbenchId, worktreeId), jsonRequest('PATCH', patch))
export const listWorktreeTasks = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<WorktreeTaskList>(`${worktreePath(workbenchId, worktreeId)}/tasks`)
export const createWorktreeTask = (
  workbenchId: string,
  worktreeId: string,
  task: { title: string; details?: string | null; elementRefs?: string[] },
) =>
  workbenchRequest<WorktreeTask>(`${worktreePath(workbenchId, worktreeId)}/tasks`, jsonRequest('POST', task))
export const updateWorktreeTask = (
  workbenchId: string,
  worktreeId: string,
  taskId: string,
  patch: { title?: string; details?: string | null; status?: WorktreeTaskStatus; elementRefs?: string[] },
) =>
  workbenchRequest<WorktreeTask>(`${worktreePath(workbenchId, worktreeId)}/tasks/${encodeURIComponent(taskId)}`, jsonRequest('PATCH', patch))
export const deleteWorktreeTask = (workbenchId: string, worktreeId: string, taskId: string) =>
  workbenchRequest<void>(`${worktreePath(workbenchId, worktreeId)}/tasks/${encodeURIComponent(taskId)}`, { method: 'DELETE' })

/* ── Version control types ──────────────────────────── */

export type VcStatusEntry = {
  filePath: string
  state: VcFileStatusState
  staged: boolean
}

export type VcFileStatusState =
  | 'Untracked'
  | 'Modified'
  | 'Added'
  | 'Deleted'
  | 'Staged'
  | 'RenamedInWorkdir'
  | 'Conflicted'

export type VcValidationState = 'Validated' | 'Unlabeled' | 'Invalid'

export type SourceChangeState = 'Modified' | 'Added' | 'Deleted' | 'Unauthorized'

export type VcSourceEntry = {
  filePath: string
  deviceId: string
  plcName: string
  category: string
  objectName: string
  state: SourceChangeState
  authorizedOnMaster: boolean
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
  validationState: VcValidationState
  evidenceKind: string | null
}

export type VcLogResult = {
  repoPath: string
  commits: VcCommitEntry[]
}
export type VersionControlTimelineGitCommit = {
  sha: string
  author: string
  message: string
  timestamp: string
  files: string[]
  tiaChecksum: string | null
  svnRevision: number | null
}
export type VersionControlTimelineSvnRevision = {
  revision: number
  author: string
  message: string
  timestamp: string
  tiaChecksum: string | null
  gitCommitSha: string
}
export type VersionControlTimelineResult = {
  gitCommits: VersionControlTimelineGitCommit[]
  svnRevisions: VersionControlTimelineSvnRevision[]
  hasMore: boolean
}
export type VcValidationEvidence = {
  schemaVersion: string
  evidenceKind: 'tia-sync' | 'feature-merge'
  commitSha: string
  workbenchId: string
  sourceWorktreeId: string | null
  confirmedAt: string
  confirmedBy: string
  machineValidated: boolean
  devices: Array<{
    deviceId: string
    plcName: string
    projectIdentity: string
    projectChecksum: string
    objects: Array<{ identity: string; relativePath: string; sha256: string }>
  }>
}

export type VcDiffLine = { type: 'context' | 'addition' | 'deletion'; content: string }
export type VcDiffHunk = { oldStart: number; newStart: number; lines: VcDiffLine[] }
export type VcXmlHeaderChange = { field: string; oldValue: string | null; newValue: string | null }
export type VcXmlMultilingualTextChange = {
  ownerKind: string
  ownerId: string
  networkNumber: number | null
  field: string
  culture: string
  oldValue: string | null
  newValue: string | null
}
export type VcXmlChangeSummary = {
  summaryAvailable: boolean
  logicOrStructureChanged: boolean
  headerChanges: VcXmlHeaderChange[]
  multilingualTextChanges: VcXmlMultilingualTextChange[]
}
export type VcDiffResult = {
  repoPath: string
  filePath: string
  oldSha: string | null
  newSha: string | null
  binary: boolean
  hunks: VcDiffHunk[]
  summary: VcXmlChangeSummary
}

export type FeatureMergePublicationResult = {
  merged: boolean
  sha: string
  evidence: VcValidationEvidence
  validationTag: string
}

export type VersionControlSelection =
  | { kind: 'source'; entry: VcSourceEntry }
  | { kind: 'difference'; difference: SourceDifference }
  | { kind: 'commit'; commit: VcCommitEntry }
  | { kind: 'validation'; evidence: VcValidationEvidence }
  | null

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

export async function getAppAssistantHealth(): Promise<unknown> {
  const res = await fetch(`${BASE}/app-assistant/health`)
  if (!res.ok) throw new Error(`App assistant health failed: ${res.status}`)
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

export async function getCurrentTiaSession(): Promise<CurrentTiaSession> {
  const res = await fetch(`${BASE}/sessions/current`)
  if (!res.ok) throw new Error(`Current session failed: ${res.status}`)
  return res.json()
}

export async function closeTiaSession(sessionId: number): Promise<void> {
  const res = await fetch(`${BASE}/sessions/close`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId }),
  })
  if (!res.ok) throw new Error(`Close session failed: ${res.status}`)
}

export async function saveTiaProject(): Promise<void> {
  const res = await fetch(`${BASE}/tia/project/save`, { method: 'POST' })
  if (!res.ok) throw new Error(`Save TIA project failed: ${res.status}`)
}

export async function getProjectInfo(): Promise<ProjectInfo> {
  const res = await fetch(`${BASE}/project/info`)
  if (!res.ok) throw new Error(`Project info failed: ${res.status}`)
  return res.json()
}

export async function getTiaProjectInfo(): Promise<ProjectInfo> {
  const res = await fetch(`${BASE}/tia/project-info`)
  if (!res.ok) throw new Error(`TIA project info failed: ${res.status}`)
  return res.json()
}

export async function getProjectCapabilities(): Promise<ProjectCapabilities> {
  const res = await fetch(`${BASE}/tia/project-capabilities`)
  if (!res.ok) throw new Error(`TIA project capabilities failed: ${res.status}`)
  return res.json()
}

export async function createTiaProject(targetDirectory: string, projectName: string): Promise<ProjectCreateResult> {
  const res = await fetch(`${BASE}/tia/project/create`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ targetDirectory, projectName }),
  })
  if (!res.ok) throw new Error(`TIA project creation failed: ${res.status}`)
  return res.json()
}

export async function archiveTiaProject(targetDirectory: string, archiveName: string, archivationMode = 'compressed', operationId?: string): Promise<ProjectArchiveResult> {
  const res = await fetch(`${BASE}/tia/project/archive`, withOperation(jsonRequest('POST', { targetDirectory, archiveName, archivationMode }), operationId))
  if (!res.ok) throw new Error(`TIA project archive failed: ${res.status}`)
  return res.json()
}

export async function retrieveTiaProject(archivePath: string, targetDirectory: string, upgrade = false, openMode = 'primary'): Promise<ProjectRetrieveResult> {
  const res = await fetch(`${BASE}/tia/project/retrieve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ archivePath, targetDirectory, upgrade, openMode }),
  })
  if (!res.ok) throw new Error(`TIA project retrieval failed: ${res.status}`)
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

export const getWorktreeVcStatus = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<VcStatusResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/status`)
export const getWorktreeVcLog = (workbenchId: string, worktreeId: string, maxCount?: number, filePath?: string) => {
  const params = new URLSearchParams()
  if (maxCount) params.set('maxCount', String(maxCount))
  if (filePath) params.set('filePath', filePath)
  const query = params.toString()
  return workbenchRequest<VcLogResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/log${query ? `?${query}` : ''}`)
}
export const getWorktreeVcDiff = (workbenchId: string, worktreeId: string, filePath: string, oldSha?: string, newSha?: string) => {
  const params = new URLSearchParams({ filePath })
  if (oldSha) params.set('oldSha', oldSha)
  if (newSha) params.set('newSha', newSha)
  return workbenchRequest<VcDiffResult>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/diff?${params}`)
}
export const commitVcPaths = (workbenchId: string, worktreeId: string, paths: string[], message: string) =>
  workbenchRequest<{ sha: string; message: string; files: string[] }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/commit`,
    jsonRequest('POST', { paths, message }),
  )
export const getVcValidation = (workbenchId: string, worktreeId: string, sha: string) =>
  workbenchRequest<VcValidationEvidence | null>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/validation/${encodeURIComponent(sha)}`)

export type EngineeringRevisionState = {
  schemaVersion: number
  svn: { url: string; revision: number } | null
  tia: { projectChecksum: string | null } | null
  safety: { fSignature: string | null } | null
  validation: { compileStatus: string | null } | null
}

export type WorktreeEngineeringState = {
  revision: EngineeringRevisionState | null
  svnUrl: string | null
  baseSvnRevision: number | null
  managedTiaProjectPath: string | null
  tiaStorePath: string
  pendingCommit: boolean
}

export type RestoreTiaProjectResult = {
  gitCommit: string
  svnUrl: string
  svnRevision: number
  restoredDirectory: string
  restoredProjectPath: string | null
}

export type SavepointInfo = {
  sha: string
  message: string
  svnUrl: string | null
  svnRevision: number | null
  projectChecksum: string | null
  compileStatus: string | null
  fSignature: string | null
}

export const getWorktreeEngineeringState = (workbenchId: string, worktreeId: string) =>
  workbenchRequest<WorktreeEngineeringState>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/engineering-state`)
export const getWorktreeSavepoints = (workbenchId: string, worktreeId: string, maxCount = 30) =>
  workbenchRequest<SavepointInfo[]>(`/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/savepoints?maxCount=${maxCount}`)
export const restoreTiaProject = (workbenchId: string, worktreeId: string, gitCommit?: string) =>
  workbenchRequest<RestoreTiaProjectResult>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/restore-tia`,
    jsonRequest('POST', { gitCommit: gitCommit || null }),
  )
export const createSvnSavepoint = (workbenchId: string, worktreeId: string, message: string) =>
  workbenchRequest<{ sha: string; message: string; files: string[] }>(
    `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/svn-savepoint`,
    jsonRequest('POST', { message }),
  )

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
