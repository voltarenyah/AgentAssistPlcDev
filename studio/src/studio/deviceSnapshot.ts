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
  overlayCount: number
  diagnostics: string[]
}

export type DeviceSelectionState = {
  requestId: number
  deviceId: string
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
  overlayCount: snapshot.overlayCount,
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
    view: applyDeviceSnapshot(current.view, snapshot),
    sessions,
  }
}

export const failDeviceSelection = (
  current: DeviceSelectionState,
  requestId: number,
): DeviceSelectionState => current.requestId === requestId
  ? { ...current, view: null, sessions: [] }
  : current
