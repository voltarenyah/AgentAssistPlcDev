import { describe, expect, it, vi } from 'vitest'
import { runOpenProjectInTia } from './deviceActions'

describe('explicit TIA open action', () => {
  it('uses captured device context without clearing or reloading offline state', async () => {
    const offlineState = { blocks: ['Main'], knowledge: 'current' }
    const before = offlineState
    const open = vi.fn().mockResolvedValue({ opened: true })

    await runOpenProjectInTia(open, {
      workbenchId: 'wb-1',
      worktreeId: 'wt-1',
      deviceId: 'dev-1',
      operationId: 'op-1',
    })

    expect(open).toHaveBeenCalledWith('wb-1', 'wt-1', 'dev-1', 'op-1')
    expect(offlineState).toBe(before)
    expect(offlineState).toEqual({ blocks: ['Main'], knowledge: 'current' })
  })
})
