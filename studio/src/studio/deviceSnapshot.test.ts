import { describe, expect, it } from 'vitest'
import type { DeviceSnapshot, OfflineBlockInfo } from '@/api/client'
import {
  applyDeviceSnapshot,
  beginDeviceSelection,
  completeDeviceSelection,
  failDeviceSelection,
  retainSnapshotOnError,
} from './deviceSnapshot'

const block = (name: string, blockType: string, number: number): OfflineBlockInfo => ({
  id: `${blockType}:${number}`,
  name,
  number,
  blockType,
  programmingLanguage: 'LAD',
  groupPath: null,
  relativePath: `Blocks/${name} [${blockType}${number}].xml`,
  modified: false,
})

const snapshot = (overrides: Partial<DeviceSnapshot> = {}): DeviceSnapshot => ({
  workbenchId: 'workbench',
  worktreeId: 'master',
  deviceId: 'plc-1',
  plcName: 'PLC_1',
  engineeringIdentity: 'project/plc-1',
  exportedSourceRoot: 'C:/workbench/exported-source',
  modifiedSourceRoot: 'C:/workbench/modified-source',
  knowledgeDbPath: 'C:/workbench/plc-knowledge.db',
  device: null,
  knowledge: { state: 'missing', updatedAt: null },
  blocks: [],
  overlayCount: 0,
  diagnostics: [],
  ...overrides,
})

describe('device snapshot state', () => {
  it('hydrates blocks and persisted knowledge from a selected-device snapshot', () => {
    const next = applyDeviceSnapshot(null, snapshot({
      knowledge: { state: 'current', updatedAt: '2026-07-29T08:00:00Z' },
      blocks: [block('Main', 'OB', 1)],
      overlayCount: 2,
      diagnostics: ['overlay warning'],
    }))

    expect(next.knowledgeState).toBe('current')
    expect(next.knowledgeUpdatedAt).toBe('2026-07-29T08:00:00Z')
    expect(next.blocks).toHaveLength(1)
    expect(next.overlayCount).toBe(2)
    expect(next.diagnostics).toEqual(['overlay warning'])
  })

  it('retains the last successful offline snapshot when refresh fails', () => {
    const previous = applyDeviceSnapshot(null, snapshot({
      knowledge: { state: 'stale', updatedAt: null },
      blocks: [block('Main', 'OB', 1)],
    }))

    expect(retainSnapshotOnError(previous, new Error('TIA unavailable'))).toBe(previous)
  })

  it('clears the prior device while a different device selection is pending or fails', () => {
    const selectedA = completeDeviceSelection(
      beginDeviceSelection(null, 'plc-a'),
      1,
      snapshot({ deviceId: 'plc-a', plcName: 'PLC_A' }),
      [{ sessionId: 'a', projectName: 'PLC_A', createdAt: '', updatedAt: '', messageCount: 0, turnCount: 0, firstUserMessage: null }],
    )

    const pendingB = beginDeviceSelection(selectedA, 'plc-b')
    expect(pendingB.view).toBeNull()
    expect(pendingB.sessions).toEqual([])

    const failedB = failDeviceSelection(pendingB, pendingB.requestId)
    expect(failedB.view).toBeNull()
    expect(failedB.sessions).toEqual([])
  })

  it('rejects an out-of-order selection response and a mismatched snapshot identity', () => {
    const pendingA = beginDeviceSelection(null, 'plc-a')
    const pendingB = beginDeviceSelection(pendingA, 'plc-b')

    const lateA = completeDeviceSelection(
      pendingB,
      pendingA.requestId,
      snapshot({ deviceId: 'plc-a' }),
      [],
    )
    expect(lateA).toBe(pendingB)

    const mismatchedB = completeDeviceSelection(
      pendingB,
      pendingB.requestId,
      snapshot({ deviceId: 'plc-a' }),
      [],
    )
    expect(mismatchedB).toBe(pendingB)
  })

  it('keeps B fully selected when B completes before the older A request', () => {
    const pendingA = beginDeviceSelection(null, 'plc-a')
    const pendingB = beginDeviceSelection(pendingA, 'plc-b')
    const selectedB = completeDeviceSelection(
      pendingB,
      pendingB.requestId,
      snapshot({ deviceId: 'plc-b', plcName: 'PLC_B' }),
      [{ sessionId: 'b', projectName: 'PLC_B', createdAt: '', updatedAt: '', messageCount: 0, turnCount: 0, firstUserMessage: null }],
    )
    const lateA = completeDeviceSelection(
      selectedB,
      pendingA.requestId,
      snapshot({ deviceId: 'plc-a', plcName: 'PLC_A' }),
      [{ sessionId: 'a', projectName: 'PLC_A', createdAt: '', updatedAt: '', messageCount: 0, turnCount: 0, firstUserMessage: null }],
    )

    expect(lateA.selectedDeviceId).toBe('plc-b')
    expect(lateA.view?.snapshot.deviceId).toBe('plc-b')
    expect(lateA.sessions[0]?.sessionId).toBe('b')
    expect(lateA.selecting).toBe(false)
  })
})
