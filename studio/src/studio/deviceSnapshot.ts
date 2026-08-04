import type {
  ChatSessionInfo,
  DeviceSnapshot,
  KnowledgeVisualState,
  OfflineBlockInfo,
} from '@/api/client'

export type DeviceViewState = {
  snapshot: DeviceSnapshot
  knowledgeState: KnowledgeVisualState
  knowledgeUpdatedAt: string | null
  blocks: OfflineBlockInfo[]
  sourceObjectCount: number
  diagnostics: string[]
}

export type DeviceSelectionState = {
  requestId: number
  deviceId: string
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
): DeviceSelectionState => ({
  requestId,
  deviceId,
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
