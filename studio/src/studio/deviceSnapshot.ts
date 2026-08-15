import type {
  ChatSessionInfo,
  DeviceSummary,
  DeviceSnapshot,
  KnowledgeVisualState,
  OfflineBlockInfo,
  SourceObjectInfo,
} from '@/api/client'

export type DeviceMetadata = {
  deviceId: string
  plcName: string | null
  sourceObjectCount: number | null
}

type DeviceContext = {
  workbenchId: string
  worktreeId: string
  deviceId: string
}

const deviceMetadataMemory = new Map<string, DeviceMetadata>()

const deviceMetadataKey = ({ workbenchId, worktreeId, deviceId }: DeviceContext) =>
  `${workbenchId}:${worktreeId}:${deviceId}`

const rememberDeviceMetadata = (
  context: DeviceContext,
  metadata: Partial<Pick<DeviceMetadata, 'plcName' | 'sourceObjectCount'>>,
): DeviceMetadata => {
  const previous = deviceMetadataMemory.get(deviceMetadataKey(context))
  const next = {
    deviceId: context.deviceId,
    plcName: metadata.plcName ?? previous?.plcName ?? null,
    sourceObjectCount: metadata.sourceObjectCount ?? previous?.sourceObjectCount ?? null,
  }
  deviceMetadataMemory.set(deviceMetadataKey(context), next)
  return next
}

export const rememberDeviceSummary = (
  workbenchId: string,
  worktreeId: string,
  summary: DeviceSummary,
) => rememberDeviceMetadata(
  { workbenchId, worktreeId, deviceId: summary.deviceId },
  { plcName: summary.plcName },
)

export const rememberDeviceSnapshot = (snapshot: DeviceSnapshot) => rememberDeviceMetadata(
  snapshot,
  { plcName: snapshot.plcName, sourceObjectCount: snapshot.sourceObjectCount },
)

export const readDeviceMetadata = (context: DeviceContext): DeviceMetadata | null =>
  deviceMetadataMemory.get(deviceMetadataKey(context)) ?? null

export const clearDeviceMetadataMemory = () => {
  deviceMetadataMemory.clear()
}

export type DeviceViewState = {
  snapshot: DeviceSnapshot
  knowledgeState: KnowledgeVisualState
  knowledgeUpdatedAt: string | null
  blocks: OfflineBlockInfo[]
  sourceObjects: SourceObjectInfo[]
  sourceObjectCount: number
  diagnostics: string[]
}

export type DeviceSelectionState = {
  requestId: number
  deviceId: string
  cachedMetadata: DeviceMetadata | null
  selectedDeviceId: string | null
  selecting: boolean
  view: DeviceViewState | null
  sessions: ChatSessionInfo[]
}

export const applyDeviceSnapshot = (
  _previous: DeviceViewState | null,
  snapshot: DeviceSnapshot,
): DeviceViewState => ({
  snapshot,
  knowledgeState: snapshot.knowledge.state,
  knowledgeUpdatedAt: snapshot.knowledge.updatedAt,
  blocks: snapshot.blocks,
  sourceObjects: snapshot.sourceObjects ?? [],
  sourceObjectCount: snapshot.sourceObjectCount,
  diagnostics: snapshot.diagnostics,
})

export const retainSnapshotOnError = (
  previous: DeviceViewState | null,
  _error: unknown,
): DeviceViewState | null => previous

export const beginDeviceSelection = (
  previous: DeviceSelectionState | null,
  deviceId: string,
  requestId = (previous?.requestId ?? 0) + 1,
  cachedMetadata: DeviceMetadata | null = null,
): DeviceSelectionState => ({
  requestId,
  deviceId,
  cachedMetadata,
  selectedDeviceId: null,
  selecting: true,
  view: null,
  sessions: [],
})

export const completeDeviceSelection = (
  current: DeviceSelectionState,
  requestId: number,
  snapshot: DeviceSnapshot,
  sessions: ChatSessionInfo[],
): DeviceSelectionState => {
  if (current.requestId !== requestId || current.deviceId !== snapshot.deviceId) {
    return current
  }

  return {
    ...current,
    cachedMetadata: rememberDeviceSnapshot(snapshot),
    selectedDeviceId: snapshot.deviceId,
    selecting: false,
    view: applyDeviceSnapshot(current.view, snapshot),
    sessions,
  }
}

export const failDeviceSelection = (
  current: DeviceSelectionState,
  requestId: number,
): DeviceSelectionState => current.requestId === requestId
  ? { ...current, selectedDeviceId: null, selecting: false, view: null, sessions: [] }
  : current
